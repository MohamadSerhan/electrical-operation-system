Imports System.IO
Imports System.Security.AccessControl
Imports System.Security.Cryptography
Imports System.Security.Principal
Imports System.Text
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports Npgsql

Public NotInheritable Class ServiceAccountResult
    Public Property Succeeded As Boolean
    Public Property Message As String = String.Empty
End Class

' Dedicated least-privilege PostgreSQL role used by the application. Its random password is
' independent of every EOEMS login password and is kept only in a DPAPI-protected file.
Public NotInheritable Class PostgresServiceAccount
    Public Const RoleName As String = "eoems_app"

    Private Shared ReadOnly Entropy As Byte() = Encoding.UTF8.GetBytes("EOEMS.PostgreSQL.ServiceCredential.v1")
    Private Shared ReadOnly PasswordPattern As New Regex("^[A-Za-z0-9]{32,128}$", RegexOptions.Compiled)
    Private Shared ReadOnly NamePattern As New Regex("^[A-Za-z_][A-Za-z0-9_]{0,62}$", RegexOptions.Compiled)
    Private Shared ReadOnly FileGate As New Object()

    Private NotInheritable Class SecretState
        Public Property Current As String
        Public Property Pending As String
    End Class

    Private Enum ProbeResult
        Accepted
        Rejected
        Unreachable
    End Enum

    Private Sub New()
    End Sub

    Private Shared Function SecretPath() As String
        Return Path.Combine(AppContext.BaseDirectory, "data", "pg-service.secret")
    End Function

    Public Shared Function IsProvisioned() As Boolean
        Return File.Exists(SecretPath())
    End Function

    Public Shared Function GeneratePassword() As String
        Return RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 40)
    End Function

    Private Shared Function Protect(value As String) As String
        If String.IsNullOrEmpty(value) Then Return String.Empty
        Return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser))
    End Function

    Private Shared Function Unprotect(value As String) As String
        If String.IsNullOrEmpty(value) Then Return Nothing
        Return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser))
    End Function

    Private Shared Function Load() As SecretState
        SyncLock FileGate
            Dim path = SecretPath()
            If Not File.Exists(path) Then Return Nothing
            Try
                Using document = JsonDocument.Parse(File.ReadAllText(path))
                    Dim state As New SecretState()
                    Dim value As JsonElement = Nothing
                    If document.RootElement.TryGetProperty("current", value) Then state.Current = Unprotect(value.GetString())
                    If document.RootElement.TryGetProperty("pending", value) Then state.Pending = Unprotect(value.GetString())
                    Return state
                End Using
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException OrElse
                                       TypeOf ex Is FormatException OrElse TypeOf ex Is CryptographicException
                Return Nothing
            End Try
        End SyncLock
    End Function

    Private Shared Sub Save(current As String, pending As String)
        SyncLock FileGate
            Dim path = SecretPath()
            If String.IsNullOrEmpty(current) AndAlso String.IsNullOrEmpty(pending) Then
                If File.Exists(path) Then File.Delete(path)
                Return
            End If
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path))
            Dim temp = path & ".tmp"
            File.WriteAllText(temp, JsonSerializer.Serialize(New With {
                .role = RoleName, .current = Protect(current), .pending = Protect(pending)}))
            RestrictToCurrentUser(temp)
            File.Move(temp, path, True)
        End SyncLock
    End Sub

    Private Shared Sub RestrictToCurrentUser(path As String)
        Try
            Dim info As New FileInfo(path)
            Dim security = info.GetAccessControl()
            security.SetAccessRuleProtection(True, False)
            security.AddAccessRule(New FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, AccessControlType.Allow))
            info.SetAccessControl(security)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
                                   TypeOf ex Is PlatformNotSupportedException OrElse TypeOf ex Is InvalidOperationException
        End Try
    End Sub

    Private Shared Function Probe(template As NpgsqlConnectionStringBuilder, password As String) As ProbeResult
        Dim builder As New NpgsqlConnectionStringBuilder(template.ConnectionString) With {
            .Username = RoleName, .Password = password, .Pooling = False, .Timeout = 5}
        Try
            Using connection As New NpgsqlConnection(builder.ConnectionString)
                connection.Open()
            End Using
            Return ProbeResult.Accepted
        Catch ex As PostgresException When ex.SqlState = "3D000"
            Return ProbeResult.Accepted
        Catch ex As PostgresException When ex.SqlState = "28P01" OrElse ex.SqlState = "28000"
            Return ProbeResult.Rejected
        Catch ex As Exception When TypeOf ex Is NpgsqlException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is IOException
            Return ProbeResult.Unreachable
        End Try
    End Function

    ' Returns the service password, or Nothing when the service role has not been provisioned.
    ' A leftover pending value means a rotation was interrupted; it is settled here.
    Friend Shared Function ResolvePassword(template As NpgsqlConnectionStringBuilder) As String
        Dim state = Load()
        If state Is Nothing Then Return Nothing
        If Not String.IsNullOrEmpty(state.Pending) Then
            If Probe(template, state.Pending) = ProbeResult.Accepted Then
                Save(state.Pending, Nothing)
                Return state.Pending
            End If
            If Not String.IsNullOrEmpty(state.Current) AndAlso Probe(template, state.Current) = ProbeResult.Accepted Then
                Save(state.Current, Nothing)
                Return state.Current
            End If
        End If
        Return If(String.IsNullOrEmpty(state.Current), state.Pending, state.Current)
    End Function

    ' Sends a SCRAM-SHA-256 verifier instead of the plaintext, so the password never appears
    ' in a statement that PostgreSQL could write to its server log.
    Friend Shared Function BuildScramVerifier(password As String) As String
        Dim salt = RandomNumberGenerator.GetBytes(16)
        Const iterations As Integer = 4096
        Dim salted = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32)
        Dim clientKey = HMACSHA256.HashData(salted, Encoding.UTF8.GetBytes("Client Key"))
        Dim storedKey = SHA256.HashData(clientKey)
        Dim serverKey = HMACSHA256.HashData(salted, Encoding.UTF8.GetBytes("Server Key"))
        Return "SCRAM-SHA-256$" & iterations.ToString(Globalization.CultureInfo.InvariantCulture) & ":" &
            Convert.ToBase64String(salt) & "$" & Convert.ToBase64String(storedKey) & ":" & Convert.ToBase64String(serverKey)
    End Function

    Private Shared Sub SetLogin(connection As NpgsqlConnection, password As String)
        If Not PasswordPattern.IsMatch(password) Then Throw New InvalidOperationException("Invalid generated credential.")
        Using command = connection.CreateCommand()
            command.CommandText = "ALTER ROLE " & RoleName & " WITH LOGIN PASSWORD '" & BuildScramVerifier(password) & "'"
            command.ExecuteNonQuery()
        End Using
    End Sub

    Private Shared Function Quote(name As String) As String
        Return """" & name.Replace("""", """""") & """"
    End Function

    ' Uses PostgreSQL administrator credentials once (never stored) to create or repair the
    ' dedicated role, hand it the application database and give it a fresh random password.
    Public Shared Function Provision(host As String, port As Integer, database As String,
                                     adminUser As String, adminPassword As String) As ServiceAccountResult
        If Not NamePattern.IsMatch(If(database, String.Empty)) Then
            Return Fail("The database name may only contain letters, digits and underscores.")
        End If
        If String.IsNullOrWhiteSpace(adminUser) Then Return Fail("Enter the PostgreSQL administrator user.")

        Dim template As New NpgsqlConnectionStringBuilder With {
            .Host = host, .Port = port, .Database = database, .Pooling = False, .Timeout = 5}
        Dim previous = Load()
        Dim previousPassword = If(previous Is Nothing, Nothing, If(String.IsNullOrEmpty(previous.Current), previous.Pending, previous.Current))
        Dim newPassword = GeneratePassword()
        Dim roleChanged = False
        Dim notes As New List(Of String)

        Try
            Dim admin As New NpgsqlConnectionStringBuilder(template.ConnectionString) With {
                .Database = "postgres", .Username = adminUser.Trim(), .Password = adminPassword}
            Using connection As New NpgsqlConnection(admin.ConnectionString)
                connection.Open()
                Save(previousPassword, newPassword)

                Dim exists As Boolean
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT 1 FROM pg_roles WHERE rolname = @r"
                    command.Parameters.AddWithValue("r", RoleName)
                    exists = command.ExecuteScalar() IsNot Nothing
                End Using
                If Not exists Then
                    Using command = connection.CreateCommand()
                        command.CommandText = "CREATE ROLE " & RoleName & " NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT"
                        command.ExecuteNonQuery()
                    End Using
                End If
                SetLogin(connection, newPassword)
                roleChanged = True

                Dim databaseExists As Boolean
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT 1 FROM pg_database WHERE datname = @d"
                    command.Parameters.AddWithValue("d", database)
                    databaseExists = command.ExecuteScalar() IsNot Nothing
                End Using
                Try
                    Using command = connection.CreateCommand()
                        command.CommandText = If(databaseExists,
                            "ALTER DATABASE " & Quote(database) & " OWNER TO " & RoleName,
                            "CREATE DATABASE " & Quote(database) & " OWNER " & RoleName)
                        command.ExecuteNonQuery()
                    End Using
                Catch ex As PostgresException When ex.SqlState = "42501"
                    If Not databaseExists Then
                        Using command = connection.CreateCommand()
                            command.CommandText = "CREATE DATABASE " & Quote(database)
                            command.ExecuteNonQuery()
                        End Using
                    End If
                    notes.Add("Database ownership could not be transferred; privileges were granted instead.")
                End Try
            End Using

            admin.Database = database
            Using connection As New NpgsqlConnection(admin.ConnectionString)
                connection.Open()
                Using command = connection.CreateCommand()
                    command.CommandText =
                        "GRANT CONNECT ON DATABASE " & Quote(database) & " TO " & RoleName & ";" &
                        "GRANT ALL ON SCHEMA public TO " & RoleName & ";" &
                        "DO $$ DECLARE r record; BEGIN " &
                        "FOR r IN SELECT tablename FROM pg_tables WHERE schemaname = 'public' LOOP " &
                        "EXECUTE format('ALTER TABLE public.%I OWNER TO " & RoleName & "', r.tablename); END LOOP; " &
                        "FOR r IN SELECT sequencename FROM pg_sequences WHERE schemaname = 'public' LOOP " &
                        "EXECUTE format('ALTER SEQUENCE public.%I OWNER TO " & RoleName & "', r.sequencename); END LOOP; " &
                        "END $$;"
                    Try
                        command.ExecuteNonQuery()
                    Catch ex As PostgresException When ex.SqlState = "42501"
                        notes.Add("Some existing tables could not be reassigned to the service role.")
                    End Try
                End Using
            End Using
        Catch ex As Exception When TypeOf ex Is NpgsqlException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is IOException
            RollbackAfterFailure(template, previousPassword, roleChanged, adminUser, adminPassword)
            Return Fail("Provisioning failed: " & ex.Message)
        End Try

        If Probe(template, newPassword) <> ProbeResult.Accepted Then
            RollbackAfterFailure(template, previousPassword, roleChanged, adminUser, adminPassword)
            Return Fail("The new service credential could not be verified. The previous configuration was kept.")
        End If

        Save(newPassword, Nothing)
        PostgresDatabase.SaveSettings(New PostgresConnectionSettings With {.Host = host, .Port = port, .Database = database})
        Return New ServiceAccountResult With {
            .Succeeded = True,
            .Message = "Service role '" & RoleName & "' is ready and verified." & If(notes.Count > 0, " " & String.Join(" ", notes), String.Empty)}
    End Function

    Private Shared Sub RollbackAfterFailure(template As NpgsqlConnectionStringBuilder, previousPassword As String,
                                            roleChanged As Boolean, adminUser As String, adminPassword As String)
        Try
            If roleChanged AndAlso Not String.IsNullOrEmpty(previousPassword) Then
                Dim admin As New NpgsqlConnectionStringBuilder(template.ConnectionString) With {
                    .Database = "postgres", .Username = adminUser.Trim(), .Password = adminPassword}
                Using connection As New NpgsqlConnection(admin.ConnectionString)
                    connection.Open()
                    SetLogin(connection, previousPassword)
                End Using
            End If
            Save(previousPassword, Nothing)
        Catch ex As Exception When TypeOf ex Is NpgsqlException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is IOException
            ' The pending value stays on disk so ResolvePassword can settle it on the next start.
        End Try
    End Sub

    ' The service role cannot alter itself (PostgreSQL 16+), so rotation needs administrator
    ' credentials once. Writers keep spooling in SQLite while the pools are recycled.
    Public Shared Function Rotate(adminUser As String, adminPassword As String,
                                  Optional verifier As Func(Of NpgsqlConnectionStringBuilder, String, Boolean) = Nothing) As ServiceAccountResult
        If Not IsProvisioned() Then Return Fail("The service role is not provisioned yet. Run Provision / Repair first.")
        If String.IsNullOrWhiteSpace(adminUser) Then Return Fail("Enter the PostgreSQL administrator user.")
        Dim template As New NpgsqlConnectionStringBuilder(PostgresDatabase.ConnectionString())
        Dim oldPassword = template.Password
        If Not String.Equals(template.Username, RoleName, StringComparison.Ordinal) Then
            Return Fail("The application is not using the service role. Run Provision / Repair first.")
        End If
        template.Pooling = False
        Dim admin As New NpgsqlConnectionStringBuilder(template.ConnectionString) With {
            .Database = "postgres", .Username = adminUser.Trim(), .Password = adminPassword}

        Dim newPassword = GeneratePassword()
        Dim altered = False
        Try
            Using connection As New NpgsqlConnection(admin.ConnectionString)
                connection.Open()
                Save(oldPassword, newPassword)
                SetLogin(connection, newPassword)
                altered = True

                Dim verified As Boolean
                Try
                    verified = If(verifier IsNot Nothing, verifier(template, newPassword), Probe(template, newPassword) = ProbeResult.Accepted)
                Catch ex As Exception When TypeOf ex Is NpgsqlException OrElse TypeOf ex Is InvalidOperationException
                    verified = False
                End Try

                If Not verified Then
                    SetLogin(connection, oldPassword)
                    altered = False
                    Save(oldPassword, Nothing)
                    Return Fail("The new credential could not be verified; the previous credential was restored.")
                End If
            End Using
        Catch ex As Exception When TypeOf ex Is NpgsqlException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is IOException
            ' If the role was already changed the pending value stays so ResolvePassword can settle it.
            If Not altered Then
                Try
                    Save(oldPassword, Nothing)
                Catch inner As IOException
                End Try
            End If
            Return Fail("Rotation failed: " & ex.Message)
        End Try

        Save(newPassword, Nothing)
        PostgresDatabase.Reconfigure()
        Return New ServiceAccountResult With {.Succeeded = True, .Message = "Service password rotated and verified."}
    End Function

    Private Shared Function Fail(message As String) As ServiceAccountResult
        Return New ServiceAccountResult With {.Succeeded = False, .Message = message}
    End Function
End Class
