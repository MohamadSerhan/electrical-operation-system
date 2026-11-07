Imports System.Globalization
Imports System.IO
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Threading
Imports Microsoft.Data.Sqlite
Imports Npgsql
Imports NpgsqlTypes

Public NotInheritable Class RecordedCommand
    Friend Shared ReadOnly JsonOptions As New JsonSerializerOptions With {.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals}
    Public Property Id As Guid = Guid.NewGuid()
    Public Property Sql As String = String.Empty
    Public Property Parameters As List(Of RecordedParameter) = New List(Of RecordedParameter)()

    Public Shared Function Capture(command As NpgsqlCommand) As RecordedCommand
        Dim result As New RecordedCommand With {.Sql = command.CommandText}
        For Each parameter As NpgsqlParameter In command.Parameters
            result.Parameters.Add(New RecordedParameter With {
                .Name = parameter.ParameterName, .Type = parameter.NpgsqlDbType,
                .Value = JsonSerializer.SerializeToElement(If(parameter.Value Is DBNull.Value, Nothing, parameter.Value), JsonOptions)})
        Next
        Return result
    End Function

    Public Function Execute(connection As NpgsqlConnection, transaction As NpgsqlTransaction) As Integer
        Using command As New NpgsqlCommand(Sql, connection, transaction)
            For Each parameter In Parameters
                command.Parameters.AddWithValue(parameter.Name, parameter.Type, parameter.Restore())
            Next
            Return command.ExecuteNonQuery()
        End Using
    End Function
End Class

Public NotInheritable Class RecordedParameter
    Public Property Name As String = String.Empty
    Public Property Type As NpgsqlDbType
    Public Property Value As JsonElement

    Public Function Restore() As Object
        If Value.ValueKind = JsonValueKind.Null Then Return DBNull.Value
        Select Case Type
            Case NpgsqlDbType.Text, NpgsqlDbType.Jsonb
                Return Value.GetString()
            Case NpgsqlDbType.Integer
                Return Value.GetInt32()
            Case NpgsqlDbType.Smallint
                Return Value.GetInt16()
            Case NpgsqlDbType.Bigint
                Return Value.GetInt64()
            Case NpgsqlDbType.Double
                Return Value.GetDouble()
            Case NpgsqlDbType.TimestampTz
                Return Value.GetDateTime().ToUniversalTime()
            Case NpgsqlDbType.Uuid
                Return Value.GetGuid()
            Case NpgsqlDbType.Bytea
                Return Value.GetBytesFromBase64()
            Case NpgsqlDbType.Array Or NpgsqlDbType.Text
                Return Value.Deserialize(Of String())()
            Case NpgsqlDbType.Array Or NpgsqlDbType.Real
                Return Value.Deserialize(Of Single())(RecordedCommand.JsonOptions)
            Case Else
                Throw New InvalidDataException("Unsupported durable recording parameter: " & Type.ToString())
        End Select
    End Function
End Class

' Commit locally before attempting PostgreSQL. Replays use stable primary keys and delete only after commit.
Public NotInheritable Class DurableRecordingOutbox
    Implements IDisposable

    Private Const Capacity As Long = 500000
    Private ReadOnly _path As String
    Private ReadOnly _gate As New Object()
    Private ReadOnly _flushGate As New SemaphoreSlim(1, 1)
    Private ReadOnly _cancel As New CancellationTokenSource()
    Private _worker As Task
    Private _lastError As String = String.Empty
    Private _pending As Long
    Private _lastWriteTicks As Long
    Private _lastUpsWriteTicks As Long
    Private _lastTransformerWriteTicks As Long
    Private _lastUpsRows As Integer
    Private _totalUpsRows As Long
    Private _schemaReady As Boolean

    Public Sub New(path As String)
        _path = IO.Path.GetFullPath(path)
        Directory.CreateDirectory(IO.Path.GetDirectoryName(_path))
        Using connection = OpenLocal()
            Using command = connection.CreateCommand()
                command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS recording_outbox(" &
                    "id INTEGER PRIMARY KEY AUTOINCREMENT, payload TEXT NOT NULL);" &
                    "CREATE TABLE IF NOT EXISTS alarm_recording_state(equipment TEXT PRIMARY KEY, payload TEXT NOT NULL);"
                command.ExecuteNonQuery()
                command.CommandText = "SELECT count(*) FROM recording_outbox"
                _pending = CLng(command.ExecuteScalar())
            End Using
        End Using
    End Sub

    Public ReadOnly Property PendingCount As Long
        Get
            Return Interlocked.Read(_pending)
        End Get
    End Property

    Public ReadOnly Property LastError As String
        Get
            Return Volatile.Read(_lastError)
        End Get
    End Property

    Public ReadOnly Property LastWriteUtc As DateTime?
        Get
            Return RecordedTime(Interlocked.Read(_lastWriteTicks))
        End Get
    End Property

    Public ReadOnly Property LastUpsWriteUtc As DateTime?
        Get
            Return RecordedTime(Interlocked.Read(_lastUpsWriteTicks))
        End Get
    End Property

    Public ReadOnly Property LastTransformerWriteUtc As DateTime?
        Get
            Return RecordedTime(Interlocked.Read(_lastTransformerWriteTicks))
        End Get
    End Property

    Public ReadOnly Property LastUpsRows As Integer
        Get
            Return Volatile.Read(_lastUpsRows)
        End Get
    End Property

    Private Shared Function RecordedTime(ticks As Long) As DateTime?
        If ticks = 0 Then Return Nothing
        Return New DateTime(ticks, DateTimeKind.Utc)
    End Function

    Public ReadOnly Property TotalUpsRows As Long
        Get
            Return Interlocked.Read(_totalUpsRows)
        End Get
    End Property

    Private Function OpenLocal() As SqliteConnection
        Dim connection As New SqliteConnection(New SqliteConnectionStringBuilder With {
            .DataSource = _path, .Pooling = False, .DefaultTimeout = 5}.ToString())
        connection.Open()
        Using command = connection.CreateCommand()
            command.CommandText = "PRAGMA synchronous=FULL"
            command.ExecuteNonQuery()
        End Using
        Return connection
    End Function

    Public Function ReadAlarmState(equipment As String) As String
        SyncLock _gate
            Using connection = OpenLocal(), command = connection.CreateCommand()
                command.CommandText = "SELECT payload FROM alarm_recording_state WHERE equipment=$e"
                command.Parameters.AddWithValue("$e", equipment)
                Return TryCast(command.ExecuteScalar(), String)
            End Using
        End SyncLock
    End Function

    Public Sub Enqueue(commands As IReadOnlyList(Of RecordedCommand),
                       Optional equipment As String = Nothing, Optional state As String = Nothing)
        If commands.Count = 0 AndAlso equipment Is Nothing Then Return
        SyncLock _gate
            Try
                If PendingCount + commands.Count > Capacity Then
                    Throw New IOException("Durable recording buffer is full; recording requires operator intervention. No buffered records were discarded.")
                End If
                Using connection = OpenLocal(), transaction = connection.BeginTransaction()
                    For Each item In commands
                        Using command = connection.CreateCommand()
                            command.Transaction = transaction
                            command.CommandText = "INSERT INTO recording_outbox(payload) VALUES($p)"
                            command.Parameters.AddWithValue("$p", JsonSerializer.Serialize(item))
                            command.ExecuteNonQuery()
                        End Using
                    Next
                    If equipment IsNot Nothing Then
                        Using command = connection.CreateCommand()
                            command.Transaction = transaction
                            command.CommandText = "INSERT INTO alarm_recording_state(equipment,payload) VALUES($e,$p) " &
                                "ON CONFLICT(equipment) DO UPDATE SET payload=excluded.payload"
                            command.Parameters.AddWithValue("$e", equipment)
                            command.Parameters.AddWithValue("$p", state)
                            command.ExecuteNonQuery()
                        End Using
                    End If
                    transaction.Commit()
                End Using
                Interlocked.Add(_pending, commands.Count)
            Catch ex As Exception When TypeOf ex Is SqliteException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Volatile.Write(_lastError, "Local durable recording failed: " & ex.Message)
                Throw
            End Try
        End SyncLock
    End Sub

    Public Sub Start()
        If _worker IsNot Nothing Then Return
        _worker = Task.Run(Async Function()
                               While Not _cancel.IsCancellationRequested
                                   Try
                                       Await FlushAsync().ConfigureAwait(False)
                                   Catch ex As Exception When TypeOf ex Is NpgsqlException OrElse TypeOf ex Is SqliteException OrElse
                                       TypeOf ex Is IOException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is TimeoutException OrElse
                                       TypeOf ex Is JsonException OrElse TypeOf ex Is UnauthorizedAccessException
                                       SyncLock _gate
                                           Volatile.Write(_lastError, "PostgreSQL replay pending (" & PendingCount.ToString(CultureInfo.InvariantCulture) & "): " & ex.Message)
                                       End SyncLock
                                   End Try
                                   Try
                                       Await Task.Delay(1000, _cancel.Token).ConfigureAwait(False)
                                   Catch ex As OperationCanceledException
                                       Exit While
                                   End Try
                               End While
                           End Function)
    End Sub

    Public Async Function FlushAsync() As Task
        Await _flushGate.WaitAsync().ConfigureAwait(False)
        Try
            Await Task.Run(Sub() FlushBatch()).ConfigureAwait(False)
        Finally
            _flushGate.Release()
        End Try
    End Function

    Private Sub FlushBatch()
        Dim rows As New List(Of KeyValuePair(Of Long, RecordedCommand))()
        SyncLock _gate
            Using connection = OpenLocal(), command = connection.CreateCommand()
                command.CommandText = "SELECT id,payload FROM recording_outbox ORDER BY id LIMIT 1000"
                Using reader = command.ExecuteReader()
                    While reader.Read()
                        Dim item = JsonSerializer.Deserialize(Of RecordedCommand)(reader.GetString(1))
                        If item Is Nothing Then Throw New InvalidDataException("Durable recording payload is empty.")
                        rows.Add(New KeyValuePair(Of Long, RecordedCommand)(reader.GetInt64(0), item))
                    End While
                End Using
            End Using
        End SyncLock
        If rows.Count = 0 Then Return
        Dim upsRows = 0
        Dim transformerRows = 0
        Using connection = PostgresDatabase.Open()
            If Not _schemaReady Then
                UpsHistorian.EnsureHistorySchema(connection)
                EquipmentAlarmRecorder.EnsureSchema(connection)
                Using schema As New NpgsqlCommand("CREATE TABLE IF NOT EXISTS recording_replay_commits(command_id uuid PRIMARY KEY)", connection)
                    schema.ExecuteNonQuery()
                End Using
                _schemaReady = True
            End If
            Using transaction = connection.BeginTransaction()
                For Each row In rows
                    If row.Value.Sql.StartsWith("INSERT INTO measurement_samples(", StringComparison.Ordinal) Then transformerRows += 1
                    Using receipt As New NpgsqlCommand("INSERT INTO recording_replay_commits(command_id) VALUES(@id) ON CONFLICT DO NOTHING", connection, transaction)
                        receipt.Parameters.AddWithValue("id", row.Value.Id)
                        If receipt.ExecuteNonQuery() = 1 AndAlso
                           Not row.Value.Sql.Contains("equipment_raw_acquisitions", StringComparison.Ordinal) Then row.Value.Execute(connection, transaction)
                    End Using
                    If row.Value.Sql.StartsWith("INSERT INTO ups_samples ", StringComparison.Ordinal) Then
                        Dim device = CStr(row.Value.Parameters.Single(Function(parameter) parameter.Name = "id").Restore())
                        Dim stamp = CDate(row.Value.Parameters.Single(Function(parameter) parameter.Name = "t").Restore())
                        Using verify As New NpgsqlCommand("SELECT count(*) FROM ups_samples WHERE device_id=@id AND sampled_utc=@t", connection, transaction)
                            verify.Parameters.AddWithValue("id", device)
                            verify.Parameters.AddWithValue("t", stamp)
                            If CLng(verify.ExecuteScalar()) <> 1 Then Throw New IOException("UPS read-back failed for " & device)
                        End Using
                        upsRows += 1
                    End If
                Next
                transaction.Commit()
            End Using
        End Using
        SyncLock _gate
            Using connection = OpenLocal(), command = connection.CreateCommand()
                command.CommandText = "DELETE FROM recording_outbox WHERE id<=$id"
                command.Parameters.AddWithValue("$id", rows(rows.Count - 1).Key)
                Dim removed = command.ExecuteNonQuery()
                Interlocked.Add(_pending, -removed)
            End Using
            Volatile.Write(_lastError, String.Empty)
            Dim writtenTicks = DateTime.UtcNow.Ticks
            Interlocked.Exchange(_lastWriteTicks, writtenTicks)
            If upsRows > 0 Then
                Interlocked.Exchange(_lastUpsWriteTicks, writtenTicks)
                Volatile.Write(_lastUpsRows, upsRows)
                Interlocked.Add(_totalUpsRows, upsRows)
            End If
            If transformerRows > 0 Then Interlocked.Exchange(_lastTransformerWriteTicks, writtenTicks)
        End SyncLock
        Using connection = PostgresDatabase.Open(), cleanup As New NpgsqlCommand(
            "DELETE FROM recording_replay_commits WHERE command_id=ANY(@ids)", connection)
            cleanup.Parameters.AddWithValue("ids", rows.Select(Function(row) row.Value.Id).ToArray())
            cleanup.ExecuteNonQuery()
        End Using
    End Sub

    Public Async Function StopAsync() As Task
        _cancel.Cancel()
        If _worker IsNot Nothing Then Await _worker.ConfigureAwait(False)
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        Task.Run(Function() StopAsync()).GetAwaiter().GetResult()
        _cancel.Dispose()
        _flushGate.Dispose()
    End Sub
End Class
