Imports System.Data
Imports System.Globalization
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Data.Sqlite

Public NotInheritable Class PortableAuthenticationService
    Private ReadOnly _settings As SqlSettings

    Public Sub New(settings As SqlSettings)
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        _settings = settings
    End Sub

    Public Async Function IsPortableAdministratorSetupRequiredAsync(cancellationToken As CancellationToken) As Task(Of Boolean)
        Using connection = Await OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
            Await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(False)
            Using command = connection.CreateCommand()
                command.CommandText = "SELECT COUNT(*) FROM SecureUsers;"
                Dim count = Convert.ToInt64(Await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False), CultureInfo.InvariantCulture)
                Return count = 0
            End Using
        End Using
    End Function

    Public Async Function CreateFirstPortableAdministratorAsync(loginName As String, displayName As String,
                                                                 password As String, cancellationToken As CancellationToken) As Task(Of AuthResult)
        Dim normalizedLogin = If(loginName, String.Empty).Trim()
        Dim normalizedName = If(displayName, String.Empty).Trim()
        If normalizedLogin.Length = 0 OrElse normalizedLogin.Length > 100 Then
            Return Failure("Enter an administrator user ID of 1 to 100 characters.")
        End If
        If normalizedName.Length = 0 OrElse normalizedName.Length > 200 Then
            Return Failure("Enter an administrator display name of 1 to 200 characters.")
        End If
        If String.IsNullOrWhiteSpace(password) OrElse password.Length < 12 Then
            Return Failure("The administrator password must be at least 12 characters.")
        End If

        Try
            Using connection = Await OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
                Await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(False)
                Using transaction = connection.BeginTransaction(deferred:=False)
                    Using countCommand = connection.CreateCommand()
                        countCommand.Transaction = transaction
                        countCommand.CommandText = "SELECT COUNT(*) FROM SecureUsers;"
                        Dim count = Convert.ToInt64(Await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False), CultureInfo.InvariantCulture)
                        If count <> 0 Then Return Failure("A portable account already exists. First-administrator setup is closed.")
                    End Using

                    Dim userId = Guid.NewGuid().ToString("D")
                    Using insertUser = connection.CreateCommand()
                        insertUser.Transaction = transaction
                        insertUser.CommandText = "INSERT INTO SecureUsers (UserId, LoginName, DisplayName, RoleName, PasswordHash, IsActive, CreatedUtc, ChangedUtc, IsSuperAdmin) VALUES ($id, $login, $name, 'Administrator', $hash, 1, $created, NULL, 1);"
                        insertUser.Parameters.AddWithValue("$id", userId)
                        insertUser.Parameters.AddWithValue("$login", normalizedLogin)
                        insertUser.Parameters.AddWithValue("$name", normalizedName)
                        insertUser.Parameters.AddWithValue("$hash", PasswordHasher.HashPbkdf2(password))
                        insertUser.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                        Await insertUser.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
                    End Using
                    Await WriteAuditAsync(connection, transaction, normalizedLogin, True, "First portable administrator created", cancellationToken).ConfigureAwait(False)
                    transaction.Commit()
                End Using
            End Using
            Return New AuthResult With {.Succeeded = True, .Message = "Portable administrator created. Sign in with the new account."}
        Catch ex As SqliteException
            Return Failure("Portable administrator setup failed: " & ex.Message)
        Catch ex As IOException
            Return Failure("Portable administrator setup failed: " & ex.Message)
        Catch ex As UnauthorizedAccessException
            Return Failure("Portable administrator setup failed: " & ex.Message)
        Catch ex As InvalidDataException
            Return Failure(ex.Message)
        Catch ex As InvalidOperationException
            Return Failure("Portable administrator setup failed: " & ex.Message)
        End Try
    End Function

    Public Async Function AuthenticateAsync(loginName As String, password As String, cancellationToken As CancellationToken) As Task(Of AuthResult)
        If String.IsNullOrWhiteSpace(loginName) OrElse String.IsNullOrWhiteSpace(password) Then
            Return Failure("Enter both user ID and password.")
        End If
        Dim normalizedLogin = loginName.Trim()
        If normalizedLogin.Length > 100 Then Return Failure("Sign-in failed.")

        Try
            Using connection = Await OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
                Await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(False)
                Dim result As AuthResult
                Dim succeeded As Boolean
                Dim detail As String
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT UserId, DisplayName, RoleName, PasswordHash, IsActive, IsSuperAdmin FROM SecureUsers WHERE LoginName = $login COLLATE NOCASE LIMIT 1;"
                    command.Parameters.AddWithValue("$login", normalizedLogin)
                    Using reader = Await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                        If Not Await reader.ReadAsync(cancellationToken).ConfigureAwait(False) Then
                            succeeded = False
                            detail = "Unknown user"
                            result = Failure("Sign-in failed.")
                        Else
                            Dim isActive = reader.GetInt64(reader.GetOrdinal("IsActive")) <> 0
                            Dim passwordHash = reader.GetString(reader.GetOrdinal("PasswordHash"))
                            If Not isActive OrElse Not PasswordHasher.VerifyPbkdf2(password, passwordHash) Then
                                succeeded = False
                                detail = If(isActive, "Invalid password", "Inactive account")
                                result = Failure("Sign-in failed.")
                            Else
                                succeeded = True
                                detail = "Authenticated"
                                result = New AuthResult With {
                                    .Succeeded = True,
                                    .Message = "Signed in.",
                                    .Session = New UserSession With {
                                        .UserId = reader.GetString(reader.GetOrdinal("UserId")),
                                        .Username = normalizedLogin,
                                        .DisplayName = reader.GetString(reader.GetOrdinal("DisplayName")),
                                        .RoleName = reader.GetString(reader.GetOrdinal("RoleName")),
                                        .IsSuperAdministrator = reader.GetInt64(reader.GetOrdinal("IsSuperAdmin")) <> 0,
                                        .IsOfflinePreview = False
                                    }
                                }
                            End If
                        End If
                    End Using
                End Using
                Await WriteAuditAsync(connection, Nothing, normalizedLogin, succeeded, detail, cancellationToken).ConfigureAwait(False)
                Return result
            End Using
        Catch ex As SqliteException
            Return Failure("Portable authentication is unavailable: " & ex.Message)
        Catch ex As IOException
            Return Failure("Portable authentication is unavailable: " & ex.Message)
        Catch ex As UnauthorizedAccessException
            Return Failure("Portable authentication is unavailable: " & ex.Message)
        Catch ex As InvalidDataException
            Return Failure(ex.Message)
        Catch ex As InvalidOperationException
            Return Failure("Portable authentication is unavailable: " & ex.Message)
        End Try
    End Function

    Public Async Function ChangePasswordAsync(userId As String, currentPassword As String, newPassword As String,
                                               cancellationToken As CancellationToken) As Task(Of AuthResult)
        Dim accountId As Guid
        If Not Guid.TryParse(userId, accountId) Then Return Failure("The signed-in account cannot be identified.")
        If String.IsNullOrWhiteSpace(currentPassword) Then Return Failure("Enter your current password.")
        If String.IsNullOrWhiteSpace(newPassword) OrElse newPassword.Length < 12 Then
            Return Failure("The new password must be at least 12 characters.")
        End If
        If String.Equals(currentPassword, newPassword, StringComparison.Ordinal) Then
            Return Failure("Choose a new password different from the current one.")
        End If

        Try
            Using connection = Await OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
                Await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(False)
                Using transaction = connection.BeginTransaction(deferred:=False)
                    Dim loginName As String
                    Dim currentHash As String
                    Using selectCommand = connection.CreateCommand()
                        selectCommand.Transaction = transaction
                        selectCommand.CommandText = "SELECT LoginName, PasswordHash FROM SecureUsers WHERE UserId = $id AND IsActive = 1;"
                        selectCommand.Parameters.AddWithValue("$id", accountId.ToString("D"))
                        Using reader = Await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                            If Not Await reader.ReadAsync(cancellationToken).ConfigureAwait(False) Then
                                Return Failure("The active account was not found.")
                            End If
                            loginName = reader.GetString(reader.GetOrdinal("LoginName"))
                            currentHash = reader.GetString(reader.GetOrdinal("PasswordHash"))
                        End Using
                    End Using
                    If Not PasswordHasher.VerifyPbkdf2(currentPassword, currentHash) Then
                        Return Failure("Current password is incorrect.")
                    End If

                    Using updateCommand = connection.CreateCommand()
                        updateCommand.Transaction = transaction
                        updateCommand.CommandText = "UPDATE SecureUsers SET PasswordHash = $newHash, ChangedUtc = $changed WHERE UserId = $id AND IsActive = 1 AND PasswordHash = $currentHash;"
                        updateCommand.Parameters.AddWithValue("$newHash", PasswordHasher.HashPbkdf2(newPassword))
                        updateCommand.Parameters.AddWithValue("$changed", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                        updateCommand.Parameters.AddWithValue("$id", accountId.ToString("D"))
                        updateCommand.Parameters.AddWithValue("$currentHash", currentHash)
                        If Await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False) <> 1 Then
                            Return Failure("The password was not changed. Please sign in again and retry.")
                        End If
                    End Using
                    Await WriteAuditAsync(connection, transaction, loginName, True, "Password changed", cancellationToken).ConfigureAwait(False)
                    transaction.Commit()
                End Using
            End Using
            Return New AuthResult With {.Succeeded = True, .Message = "Password changed successfully."}
        Catch ex As SqliteException
            Return Failure("Password change failed: " & ex.Message)
        Catch ex As IOException
            Return Failure("Password change failed: " & ex.Message)
        Catch ex As UnauthorizedAccessException
            Return Failure("Password change failed: " & ex.Message)
        Catch ex As InvalidDataException
            Return Failure(ex.Message)
        Catch ex As InvalidOperationException
            Return Failure("Password change failed: " & ex.Message)
        End Try
    End Function

    ' Renames the sign-in name only. UserId, role, Super Administrator flag and password hash are untouched.
    Public Async Function RenameLoginAsync(userId As String, currentPassword As String, newLoginName As String,
                                           cancellationToken As CancellationToken) As Task(Of AuthResult)
        Dim accountId As Guid
        If Not Guid.TryParse(userId, accountId) Then Return Failure("The signed-in account cannot be identified.")
        Dim newLogin = If(newLoginName, String.Empty).Trim()
        If newLogin.Length < 3 OrElse newLogin.Length > 100 OrElse newLogin.Any(Function(c) Char.IsControl(c) OrElse Char.IsWhiteSpace(c)) Then
            Return Failure("The user name must be 3 to 100 characters without spaces.")
        End If
        If String.IsNullOrWhiteSpace(currentPassword) Then Return Failure("Enter your current password.")

        Try
            Using connection = Await OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
                Await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(False)
                Using transaction = connection.BeginTransaction(deferred:=False)
                    Dim oldLogin As String
                    Dim currentHash As String
                    Using selectCommand = connection.CreateCommand()
                        selectCommand.Transaction = transaction
                        selectCommand.CommandText = "SELECT LoginName, PasswordHash FROM SecureUsers WHERE UserId = $id AND IsActive = 1;"
                        selectCommand.Parameters.AddWithValue("$id", accountId.ToString("D"))
                        Using reader = Await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                            If Not Await reader.ReadAsync(cancellationToken).ConfigureAwait(False) Then Return Failure("The active account was not found.")
                            oldLogin = reader.GetString(0)
                            currentHash = reader.GetString(1)
                        End Using
                    End Using
                    If Not PasswordHasher.VerifyPbkdf2(currentPassword, currentHash) Then Return Failure("Current password is incorrect.")
                    If String.Equals(oldLogin, newLogin, StringComparison.Ordinal) Then Return Failure("Choose a different user name.")

                    Using updateCommand = connection.CreateCommand()
                        updateCommand.Transaction = transaction
                        updateCommand.CommandText = "UPDATE SecureUsers SET LoginName = $login, ChangedUtc = $changed WHERE UserId = $id AND IsActive = 1;"
                        updateCommand.Parameters.AddWithValue("$login", newLogin)
                        updateCommand.Parameters.AddWithValue("$changed", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                        updateCommand.Parameters.AddWithValue("$id", accountId.ToString("D"))
                        If Await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False) <> 1 Then
                            Return Failure("The user name was not changed.")
                        End If
                    End Using
                    Await WriteAuditAsync(connection, transaction, newLogin, True, "User name changed from " & oldLogin, cancellationToken).ConfigureAwait(False)
                    transaction.Commit()
                End Using
            End Using
            Return New AuthResult With {.Succeeded = True, .Message = "User name changed to " & newLogin & "."}
        Catch ex As SqliteException When ex.SqliteErrorCode = 19
            Return Failure("That user name is already in use.")
        Catch ex As Exception When TypeOf ex Is SqliteException OrElse TypeOf ex Is IOException OrElse
                                   TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is InvalidOperationException
            Return Failure("User name change failed: " & ex.Message)
        End Try
    End Function

    ' Authoritative check for database-credential management: re-verifies the password and the
    ' Super Administrator flag of the immutable UserId instead of trusting the session object.
    Public Async Function AuthorizeSuperAdministratorAsync(userId As String, password As String,
                                                           cancellationToken As CancellationToken) As Task(Of Boolean)
        Dim accountId As Guid
        If Not Guid.TryParse(userId, accountId) OrElse String.IsNullOrEmpty(password) Then Return False
        Try
            Using connection = Await OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
                Await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(False)
                Dim loginName As String = Nothing
                Dim hash As String = Nothing
                Dim isSuper As Boolean
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT LoginName, PasswordHash, IsSuperAdmin FROM SecureUsers WHERE UserId = $id AND IsActive = 1;"
                    command.Parameters.AddWithValue("$id", accountId.ToString("D"))
                    Using reader = Await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(False)
                        If Await reader.ReadAsync(cancellationToken).ConfigureAwait(False) Then
                            loginName = reader.GetString(0)
                            hash = reader.GetString(1)
                            isSuper = reader.GetInt64(2) <> 0
                        End If
                    End Using
                End Using
                If loginName Is Nothing Then Return False
                Dim allowed = isSuper AndAlso PasswordHasher.VerifyPbkdf2(password, hash)
                If Not allowed Then
                    Await WriteAuditAsync(connection, Nothing, loginName, False, "Credential management denied", cancellationToken).ConfigureAwait(False)
                End If
                Return allowed
            End Using
        Catch ex As Exception When TypeOf ex Is SqliteException OrElse TypeOf ex Is IOException OrElse
                                   TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is InvalidOperationException
            Return False
        End Try
    End Function

    Public Async Function RecordAuditAsync(loginName As String, succeeded As Boolean, detail As String,
                                           cancellationToken As CancellationToken) As Task
        Try
            Using connection = Await OpenConnectionAsync(cancellationToken).ConfigureAwait(False)
                Await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(False)
                Await WriteAuditAsync(connection, Nothing, loginName, succeeded, detail, cancellationToken).ConfigureAwait(False)
            End Using
        Catch ex As Exception When TypeOf ex Is SqliteException OrElse TypeOf ex Is IOException OrElse
                                   TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is InvalidOperationException
        End Try
    End Function

    Private Async Function OpenConnectionAsync(cancellationToken As CancellationToken) As Task(Of SqliteConnection)
        Dim databasePath = _settings.ResolveDatabasePath()
        Dim folder = IO.Path.GetDirectoryName(databasePath)
        If String.IsNullOrEmpty(folder) Then Throw New InvalidDataException("The portable database folder is invalid.")
        Directory.CreateDirectory(folder)
        Dim connection = New SqliteConnection(New SqliteConnectionStringBuilder With {
            .DataSource = databasePath,
            .Mode = SqliteOpenMode.ReadWriteCreate,
            .Cache = SqliteCacheMode.Private
        }.ToString())
        Try
            Await connection.OpenAsync(cancellationToken).ConfigureAwait(False)
            Return connection
        Catch
            connection.Dispose()
            Throw
        End Try
    End Function

    Private Shared Async Function EnsureSchemaAsync(connection As SqliteConnection, cancellationToken As CancellationToken) As Task
        Using command = connection.CreateCommand()
            command.CommandText =
                "CREATE TABLE IF NOT EXISTS SecureUsers (" &
                "UserId TEXT NOT NULL PRIMARY KEY, LoginName TEXT NOT NULL COLLATE NOCASE UNIQUE, " &
                "DisplayName TEXT NOT NULL, RoleName TEXT NOT NULL, PasswordHash TEXT NOT NULL, " &
                "IsActive INTEGER NOT NULL DEFAULT 1, CreatedUtc TEXT NOT NULL, ChangedUtc TEXT NULL);" &
                "CREATE TABLE IF NOT EXISTS AuthenticationAudit (" &
                "AuditId INTEGER PRIMARY KEY AUTOINCREMENT, LoginName TEXT NOT NULL, Succeeded INTEGER NOT NULL, " &
                "Detail TEXT NOT NULL, OccurredUtc TEXT NOT NULL);" &
                "CREATE INDEX IF NOT EXISTS IX_AuthenticationAudit_OccurredUtc ON AuthenticationAudit (OccurredUtc DESC);"
            Await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
        End Using

        Dim hasSuperColumn = False
        Using command = connection.CreateCommand()
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('SecureUsers') WHERE name = 'IsSuperAdmin';"
            hasSuperColumn = Convert.ToInt64(Await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(False), CultureInfo.InvariantCulture) > 0
        End Using
        If Not hasSuperColumn Then
            Using command = connection.CreateCommand()
                command.CommandText = "ALTER TABLE SecureUsers ADD COLUMN IsSuperAdmin INTEGER NOT NULL DEFAULT 0;"
                Await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
            End Using
        End If
        Using command = connection.CreateCommand()
            ' The immutable identity is the UserId; the original administrator becomes the Super Administrator once.
            command.CommandText =
                "UPDATE SecureUsers SET IsSuperAdmin = 1 WHERE UserId = (SELECT UserId FROM SecureUsers " &
                "WHERE RoleName = 'Administrator' AND IsActive = 1 ORDER BY CreatedUtc, rowid LIMIT 1) " &
                "AND NOT EXISTS (SELECT 1 FROM SecureUsers WHERE IsSuperAdmin = 1);" &
                "CREATE TRIGGER IF NOT EXISTS TR_SecureUsers_UserIdImmutable BEFORE UPDATE OF UserId ON SecureUsers " &
                "BEGIN SELECT RAISE(ABORT, 'UserId is immutable'); END;" &
                "CREATE TRIGGER IF NOT EXISTS TR_SecureUsers_SuperAdminProtected BEFORE DELETE ON SecureUsers WHEN OLD.IsSuperAdmin = 1 " &
                "BEGIN SELECT RAISE(ABORT, 'The Super Administrator cannot be deleted'); END;" &
                "CREATE TRIGGER IF NOT EXISTS TR_SecureUsers_SuperAdminFlag BEFORE UPDATE OF IsSuperAdmin ON SecureUsers WHEN OLD.IsSuperAdmin = 1 AND NEW.IsSuperAdmin = 0 " &
                "BEGIN SELECT RAISE(ABORT, 'The Super Administrator flag cannot be cleared'); END;"
            Await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
        End Using
    End Function

    Private Shared Async Function WriteAuditAsync(connection As SqliteConnection, transaction As SqliteTransaction,
                                                   loginName As String, succeeded As Boolean, detail As String,
                                                   cancellationToken As CancellationToken) As Task
        Using command = connection.CreateCommand()
            command.Transaction = transaction
            command.CommandText = "INSERT INTO AuthenticationAudit (LoginName, Succeeded, Detail, OccurredUtc) VALUES ($login, $succeeded, $detail, $occurred);"
            command.Parameters.AddWithValue("$login", loginName)
            command.Parameters.AddWithValue("$succeeded", If(succeeded, 1, 0))
            command.Parameters.AddWithValue("$detail", detail)
            command.Parameters.AddWithValue("$occurred", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
            Await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(False)
        End Using
    End Function

    Private Shared Function Failure(message As String) As AuthResult
        Return New AuthResult With {.Succeeded = False, .Message = message}
    End Function
End Class
