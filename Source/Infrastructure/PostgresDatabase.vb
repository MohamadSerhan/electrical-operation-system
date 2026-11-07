Imports System.IO
Imports System.Text.Json
Imports Npgsql

' Central PostgreSQL access: connection settings, schema bootstrap and the
' key/value configuration store used by all settings stores.
Public NotInheritable Class PostgresDatabase
    Private Shared ReadOnly InitGate As New Object()
    Private Shared _connectionString As String
    Private Shared _initialized As Boolean
    Private Shared _lastFailureUtc As DateTime = DateTime.MinValue

    Private Sub New()
    End Sub

    Public Shared Function ConnectionString() As String
        If _connectionString Is Nothing Then
            Dim raw = "Host=localhost;Port=5432;Username=eoems_app;Database=eoems"
            Try
                Dim path = System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json")
                If File.Exists(path) Then
                    Using document = JsonDocument.Parse(File.ReadAllText(path),
                                                        New JsonDocumentOptions With {.CommentHandling = JsonCommentHandling.Skip})
                        Dim section As JsonElement = Nothing
                        Dim value As JsonElement = Nothing
                        If document.RootElement.TryGetProperty("Postgres", section) AndAlso
                           section.TryGetProperty("ConnectionString", value) AndAlso
                           value.ValueKind = JsonValueKind.String AndAlso
                           Not String.IsNullOrWhiteSpace(value.GetString()) Then
                            raw = value.GetString()
                        End If
                    End Using
                End If
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException
            End Try
            Dim builder As New NpgsqlConnectionStringBuilder(raw)
            ApplyOverride(builder)
            Dim envPassword = Environment.GetEnvironmentVariable("EOEMS_PG_PASSWORD")
            If Not String.IsNullOrEmpty(envPassword) Then builder.Password = envPassword
            ' The provisioned least-privilege service role always wins over the bootstrap account.
            Dim servicePassword = PostgresServiceAccount.ResolvePassword(builder)
            If servicePassword IsNot Nothing Then
                builder.Username = PostgresServiceAccount.RoleName
                builder.Password = servicePassword
            End If
            If String.IsNullOrWhiteSpace(builder.Database) Then builder.Database = "eoems"
            builder.ApplicationName = "EOEMS_SCADA"
            builder.Timeout = 5
            builder.CommandTimeout = 60
            _connectionString = builder.ConnectionString
        End If
        Return _connectionString
    End Function

    ' Opens a connection, creating the database and schema on first use.
    Public Shared Function Open() As NpgsqlConnection
        EnsureReady()
        Dim connection As New NpgsqlConnection(ConnectionString())
        connection.Open()
        Return connection
    End Function

    Private Shared Function OverridePath() As String
        Return System.IO.Path.Combine(AppContext.BaseDirectory, "data", "postgres-connection.json")
    End Function

    ' Connection details saved from the Configuration page win over appsettings.json.
    ' The password is encrypted with Windows DPAPI (current user) and never stored in clear text.
    Private Shared Sub ApplyOverride(builder As NpgsqlConnectionStringBuilder)
        Try
            Dim path = OverridePath()
            If Not File.Exists(path) Then Return
            Using document = JsonDocument.Parse(File.ReadAllText(path))
                Dim root = document.RootElement
                Dim value As JsonElement = Nothing
                If root.TryGetProperty("host", value) AndAlso Not String.IsNullOrWhiteSpace(value.GetString()) Then builder.Host = value.GetString()
                If root.TryGetProperty("port", value) AndAlso value.ValueKind = JsonValueKind.Number Then builder.Port = value.GetInt32()
                If root.TryGetProperty("database", value) AndAlso Not String.IsNullOrWhiteSpace(value.GetString()) Then builder.Database = value.GetString()
                If root.TryGetProperty("username", value) AndAlso Not String.IsNullOrWhiteSpace(value.GetString()) Then builder.Username = value.GetString()
                If root.TryGetProperty("password", value) AndAlso Not String.IsNullOrEmpty(value.GetString()) Then
                    Dim plain = System.Security.Cryptography.ProtectedData.Unprotect(
                        Convert.FromBase64String(value.GetString()), Nothing,
                        System.Security.Cryptography.DataProtectionScope.CurrentUser)
                    builder.Password = System.Text.Encoding.UTF8.GetString(plain)
                End If
            End Using
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException OrElse
                                   TypeOf ex Is FormatException OrElse TypeOf ex Is System.Security.Cryptography.CryptographicException
        End Try
    End Sub

    Public Shared Function LoadSettings() As PostgresConnectionSettings
        Dim builder As New NpgsqlConnectionStringBuilder(ConnectionString())
        Return New PostgresConnectionSettings With {
            .Host = builder.Host, .Port = builder.Port, .Database = builder.Database,
            .Username = builder.Username, .Password = builder.Password}
    End Function

    ' Returns Nothing when the server accepts the credentials, otherwise the reason it does not.
    Public Shared Function TestConnection(settings As PostgresConnectionSettings) As String
        Dim builder = settings.ToBuilder()
        builder.Pooling = False
        Try
            Using connection As New NpgsqlConnection(builder.ConnectionString)
                connection.Open()
            End Using
            Return Nothing
        Catch ex As PostgresException When ex.SqlState = "3D000"
            Return Nothing
        Catch ex As Exception When TypeOf ex Is NpgsqlException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is IOException
            Return ex.Message
        End Try
    End Function

    ' Only the server location is stored here. Credentials live in PostgresServiceAccount.
    Public Shared Sub SaveSettings(settings As PostgresConnectionSettings)
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(OverridePath()))
        Dim json = JsonSerializer.Serialize(New With {
            .host = settings.Host, .port = settings.Port, .database = settings.Database})
        Dim target = OverridePath()
        Dim temporary = target & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Try
            Using stream As New FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                Dim bytes = System.Text.Encoding.UTF8.GetBytes(json)
                stream.Write(bytes, 0, bytes.Length)
                stream.Flush(True)
            End Using
            File.Move(temporary, target, True)
        Finally
            If File.Exists(temporary) Then File.Delete(temporary)
        End Try
        Reconfigure()
    End Sub

    ' Forget the cached connection so new settings apply without restarting the app.
    Public Shared Sub Reconfigure()
        SyncLock InitGate
            _connectionString = Nothing
            _initialized = False
            _lastFailureUtc = DateTime.MinValue
            NpgsqlConnection.ClearAllPools()
        End SyncLock
    End Sub

    Private Shared Sub EnsureReady()
        If _initialized Then Return
        SyncLock InitGate
            If _initialized Then Return
            ' Avoid hammering an unreachable server from UI threads.
            If (DateTime.UtcNow - _lastFailureUtc).TotalSeconds < 15 Then
                Throw New InvalidOperationException("PostgreSQL is unavailable (recent connection failure).")
            End If
            Try
                CreateDatabaseIfMissing()
                Using connection As New NpgsqlConnection(ConnectionString())
                    connection.Open()
                    Using command = connection.CreateCommand()
                        command.CommandText = SchemaSql
                        command.ExecuteNonQuery()
                    End Using
                End Using
                _initialized = True
            Catch
                _lastFailureUtc = DateTime.UtcNow
                Throw
            End Try
        End SyncLock
    End Sub

    Private Shared Sub CreateDatabaseIfMissing()
        Dim target As New NpgsqlConnectionStringBuilder(ConnectionString())
        Dim databaseName = target.Database
        Dim admin As New NpgsqlConnectionStringBuilder(ConnectionString()) With {.Database = "postgres", .Pooling = False}
        Using connection As New NpgsqlConnection(admin.ConnectionString)
            connection.Open()
            Using exists = connection.CreateCommand()
                exists.CommandText = "SELECT 1 FROM pg_database WHERE datname = @name"
                exists.Parameters.AddWithValue("name", databaseName)
                If exists.ExecuteScalar() IsNot Nothing Then Return
            End Using
            Using create = connection.CreateCommand()
                create.CommandText = "CREATE DATABASE " & QuoteIdentifier(databaseName)
                create.ExecuteNonQuery()
            End Using
        End Using
    End Sub

    Private Shared Function QuoteIdentifier(name As String) As String
        Return """" & name.Replace("""", """""") & """"
    End Function

    Public Shared Function TryReadDocument(key As String) As String
        Try
            Using connection = Open()
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT value::text FROM app_config WHERE key = @key"
                    command.Parameters.AddWithValue("key", key)
                    Dim result = command.ExecuteScalar()
                    Return If(result Is Nothing OrElse result Is DBNull.Value, Nothing, CStr(result))
                End Using
            End Using
        Catch ex As Exception When TypeOf ex Is NpgsqlException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is IOException
            Return Nothing
        End Try
    End Function

    ' Returns True when stored; callers keep their file copy as a fallback/backup payload.
    Public Shared Function TryWriteDocument(key As String, json As String, actor As String) As Boolean
        Try
            Using connection = Open()
                Using transaction = connection.BeginTransaction()
                    Using command = connection.CreateCommand()
                        command.Transaction = transaction
                        command.CommandText =
                            "INSERT INTO app_config(key, value, updated_at, updated_by) VALUES (@key, @value::jsonb, now(), @actor) " &
                            "ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, updated_at = now(), updated_by = EXCLUDED.updated_by"
                        command.Parameters.AddWithValue("key", key)
                        command.Parameters.AddWithValue("value", json)
                        command.Parameters.AddWithValue("actor", If(actor, String.Empty))
                        command.ExecuteNonQuery()
                    End Using
                    Using audit = connection.CreateCommand()
                        audit.Transaction = transaction
                        audit.CommandText = "INSERT INTO config_audit(key, changed_at, actor, value) VALUES (@key, now(), @actor, @value::jsonb)"
                        audit.Parameters.AddWithValue("key", key)
                        audit.Parameters.AddWithValue("actor", If(actor, String.Empty))
                        audit.Parameters.AddWithValue("value", json)
                        audit.ExecuteNonQuery()
                    End Using
                    transaction.Commit()
                End Using
            End Using
            Return True
        Catch ex As Exception When TypeOf ex Is NpgsqlException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is IOException
            Return False
        End Try
    End Function

    ' PostgreSQL is the source of truth; the JSON file is a local mirror (used for backups and as fallback).
    ' A pre-existing file is imported into PostgreSQL the first time it is found.
    Public Shared Function LoadDocument(key As String, filePath As String) As String
        Dim json = TryReadDocument(key)
        If json IsNot Nothing Then Return json
        If Not File.Exists(filePath) Then Return Nothing
        json = File.ReadAllText(filePath)
        TryWriteDocument(key, json, "import")
        Return json
    End Function

    Private Shared ReadOnly SchemaSql As String =
        "CREATE TABLE IF NOT EXISTS app_config (" &
        " key text PRIMARY KEY, value jsonb NOT NULL, updated_at timestamptz NOT NULL DEFAULT now(), updated_by text NOT NULL DEFAULT '');" &
        "CREATE TABLE IF NOT EXISTS config_audit (" &
        " id bigserial PRIMARY KEY, key text NOT NULL, changed_at timestamptz NOT NULL DEFAULT now(), actor text NOT NULL DEFAULT '', value jsonb NOT NULL);" &
        "CREATE TABLE IF NOT EXISTS measurement_samples (" &
        " analyzer_id text NOT NULL, captured_at timestamptz NOT NULL, session_id uuid NOT NULL, cycle_id bigint NOT NULL," &
        " sample_kind text NOT NULL DEFAULT 'Measured', vals real[] NOT NULL, PRIMARY KEY (analyzer_id, captured_at));" &
        "ALTER TABLE measurement_samples ADD COLUMN IF NOT EXISTS alarm_evidence jsonb;" &
        "CREATE TABLE IF NOT EXISTS sample_quality_minute (" &
        " analyzer_id text NOT NULL, bucket_start timestamptz NOT NULL, total_samples integer NOT NULL, good_samples integer NOT NULL," &
        " bad_samples integer NOT NULL, first_at timestamptz NOT NULL, last_at timestamptz NOT NULL, PRIMARY KEY (analyzer_id, bucket_start));" &
        "CREATE TABLE IF NOT EXISTS measurement_15min (" &
        " analyzer_id text NOT NULL, parameter_index smallint NOT NULL, bucket_start timestamptz NOT NULL," &
        " min_value real NOT NULL, max_value real NOT NULL, sum_value double precision NOT NULL, sample_count integer NOT NULL," &
        " PRIMARY KEY (analyzer_id, parameter_index, bucket_start));" &
        "DROP TABLE IF EXISTS equipment_raw_acquisitions;" &
        EnergySchemaSql()
    ' Permanent accounting tables: nothing in the application deletes from them, whatever the raw history or log limits are.
    ' energy_shift holds one row per equipment and shift (A = 06:00-18:00, B = 18:00-06:00 local, dated by the shift start day).
    Private Shared Function EnergySchemaSql() As String
        Return "CREATE TABLE IF NOT EXISTS energy_shift (" &
        " equipment_type text NOT NULL, equipment_id text NOT NULL, shift_date date NOT NULL, shift char(1) NOT NULL CHECK (shift IN ('A','B'))," &
        " kwh double precision NOT NULL, covered_seconds integer NOT NULL, samples integer NOT NULL, first_utc timestamptz, last_utc timestamptz," &
        " complete boolean NOT NULL DEFAULT false, updated_at timestamptz NOT NULL DEFAULT now()," &
        " PRIMARY KEY (equipment_type, equipment_id, shift_date, shift));" &
        "CREATE TABLE IF NOT EXISTS ecg_outage (" &
        " start_utc timestamptz PRIMARY KEY, end_utc timestamptz, duration_seconds integer NOT NULL DEFAULT 0, updated_at timestamptz NOT NULL DEFAULT now());" &
        "CREATE TABLE IF NOT EXISTS ecg_outage_period (" &
        " period_kind text NOT NULL CHECK (period_kind IN ('day','week','month','year')), period_start date NOT NULL," &
        " outages integer NOT NULL, total_seconds bigint NOT NULL, longest_seconds integer NOT NULL, updated_at timestamptz NOT NULL DEFAULT now()," &
        " PRIMARY KEY (period_kind, period_start));" &
        EnergyViewSql("energy_daily", "day") & EnergyViewSql("energy_weekly", "week") &
        EnergyViewSql("energy_monthly", "month") & EnergyViewSql("energy_yearly", "year")
    End Function

    Private Shared Function EnergyViewSql(name As String, unit As String) As String
        Return "CREATE OR REPLACE VIEW " & name & " AS SELECT equipment_type, equipment_id, date_trunc('" & unit & "', shift_date)::date AS period_start," &
            " SUM(kwh) FILTER (WHERE shift='A') AS kwh_shift_a, SUM(kwh) FILTER (WHERE shift='B') AS kwh_shift_b, SUM(kwh) AS kwh_total," &
            " SUM(covered_seconds) AS covered_seconds, bool_and(complete) AS complete FROM energy_shift GROUP BY 1,2,3;"
    End Function
End Class
