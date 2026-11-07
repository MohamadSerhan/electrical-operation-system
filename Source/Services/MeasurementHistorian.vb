Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Security
Imports System.Threading
Imports System.Threading.Channels
Imports Microsoft.Data.Sqlite
Imports Npgsql

Public NotInheritable Class HistorianDiagnosticsSnapshot
    Public Property QueueDepth As Integer
    Public Property DroppedCycles As Long
    Public Property WriteFailures As Long
    Public Property LastWriteLatencyMilliseconds As Double?
    Public Property LastWriteUtc As DateTime?
    Public Property LastError As String = String.Empty
    Public Property SpoolRows As Long
    Public Property SpoolCapacity As Long
    Public Property SpoolDroppedRows As Long
End Class

Public NotInheritable Class HistorianMeasurementPoint
    Public Property AcquisitionSessionId As String = String.Empty
    Public Property AnalyzerId As String = String.Empty
    Public Property ParameterId As String = String.Empty
    Public Property TimestampUtc As DateTime
    Public Property Value As Double
    Public Property Unit As String = String.Empty
    Public Property Quality As String = String.Empty
    Public Property SampleKind As String = "Measured"
    Public Property AcquisitionCycleId As Long
    Public Property AlarmEvidence As String
End Class

Public NotInheritable Class HistorianRangeSummary
    Public Property AvailableSampleCount As Long
    Public Property GoodSampleCount As Long
    Public Property BadSampleCount As Long
    Public Property FirstSampleUtc As DateTime?
    Public Property LastSampleUtc As DateTime?
End Class

' Measurements live in PostgreSQL. Every good acquisition snapshot that passes the configured recording
' interval is stored in this legacy-compatible table. EquipmentRawHistory additionally retains every acquisition.
' Full-resolution rows and quality counters are not automatically expired.
Public NotInheritable Class MeasurementHistorian
    Implements IDisposable

    Private NotInheritable Class StoredMeasurement
        Public Property ParameterIndex As Integer
        Public Property Value As Double
        Public Property Unit As String = "unverified"
    End Class

    Private NotInheritable Class StoredSnapshot
        Public Property TransformerName As String = String.Empty
        Public Property CapturedAtUtc As DateTime
        Public Property Quality As MeterQuality
        Public Property AttemptCount As Integer
        Public Property ResponseLatencyMilliseconds As Double?
        Public Property ErrorMessage As String = String.Empty
        Public Property SampleKind As String = "Measured"
        Public Property SourceSkewMilliseconds As Double?
        Public Property Measurements As StoredMeasurement() = Array.Empty(Of StoredMeasurement)()
        Public Property AlarmEvidence As String
    End Class

    Private NotInheritable Class StoredCycle
        Public Property SessionId As String = String.Empty
        Public Property CycleId As Long
        Public Property EnqueuedAtUtc As DateTime
        Public Property Snapshots As StoredSnapshot() = Array.Empty(Of StoredSnapshot)()
        Public Property BarrierCompletion As TaskCompletionSource(Of Boolean)
    End Class

    Private NotInheritable Class LastStored
        Public Property AtUtc As DateTime
        Public Property Values As Single()
    End Class

    Private NotInheritable Class SampleRow
        Public Property AnalyzerId As String = String.Empty
        Public Property CapturedAtUtc As DateTime
        Public Property SessionId As Guid
        Public Property CycleId As Long
        Public Property SampleKind As String = "Measured"
        Public Property Values As Single()
        Public Property AlarmEvidence As String
    End Class

    Private NotInheritable Class QualityRow
        Public Property AnalyzerId As String = String.Empty
        Public Property BucketStartUtc As DateTime
        Public Property Total As Integer
        Public Property Good As Integer
        Public Property Bad As Integer
        Public Property FirstAtUtc As DateTime
        Public Property LastAtUtc As DateTime
    End Class

    Private NotInheritable Class ReducedBatch
        Public ReadOnly Samples As New List(Of SampleRow)()
        Public ReadOnly Quality As New Dictionary(Of String, QualityRow)(StringComparer.Ordinal)
    End Class

    Private Const BatchLimit As Integer = 16
    Private Const SpoolCapacity As Long = 500000
    Private Const SpoolDrainChunk As Integer = 2000
    Private Const SpoolDrainChunksPerCall As Integer = 20
    Private Const MaximumWriteAttempts As Integer = 5
    Private Const HeartbeatSeconds As Double = 60
    Private Const ParameterCount As Integer = MeasurementCatalog.StoredChannelCount
    Private Const LegacyMigratedKey As String = "historian.legacy_migrated"
    Private Shared ReadOnly PurgeInterval As TimeSpan = TimeSpan.FromHours(6)
    Private Shared ReadOnly BucketResolutionMilliseconds As Integer() = {100, 250, 1000, 10000}
    Private Shared ReadOnly ParameterIdList As String() = MeasurementCatalog.StoredParameterIds
    Private ReadOnly _databasePath As String
    Private _retentionDays As Integer
    Private ReadOnly _sessionId As String = Guid.NewGuid().ToString("N")
    Private ReadOnly _diagnosticsGate As New Object()
    Private ReadOnly _queue As Channel(Of StoredCycle)
    Private ReadOnly _writerTask As Task
    Private _droppedCycles As Long
    Private _spoolRows As Long
    Private _spoolDroppedRows As Long
    Private _spoolPending As Boolean
    Private _postgresDown As Boolean
    Private _writeFailures As Long
    Private _lastWriteLatencyMilliseconds As Double?
    Private _lastWriteUtc As DateTime?
    Private _lastError As String = String.Empty
    Private _stopped As Integer
    Private _databaseInitialized As Boolean
    Private _lastPurgeUtc As DateTime = DateTime.MinValue
    Private _legacyChecked As Boolean
    Private _durableOutbox As DurableRecordingOutbox
    Private _maintenance As Task = Task.CompletedTask

    Public Sub New(Optional databasePath As String = Nothing,
                   Optional capacity As Integer = 128,
                   Optional recordingSettings As RecordingSettings = Nothing,
                   Optional migrateLegacyDatabase As Boolean = True)
        If capacity < 1 Then Throw New ArgumentOutOfRangeException(NameOf(capacity))
        If recordingSettings IsNot Nothing Then recordingSettings.Validate()
        _retentionDays = If(recordingSettings Is Nothing, 30, recordingSettings.HistorianRetentionDays)
        _legacyChecked = Not migrateLegacyDatabase
        _databasePath = If(String.IsNullOrWhiteSpace(databasePath),
            Path.Combine(AppContext.BaseDirectory, "EOEMSData", "Historian", "eoems_historian.db"),
            Path.GetFullPath(databasePath))
        _queue = Channel.CreateBounded(Of StoredCycle)(New BoundedChannelOptions(capacity) With {
            .FullMode = BoundedChannelFullMode.Wait,
            .SingleReader = True,
            .SingleWriter = False,
            .AllowSynchronousContinuations = False
        })
        _writerTask = Task.Run(Function() WriterLoopAsync())
    End Sub

    Public Sub AttachOutbox(outbox As DurableRecordingOutbox)
        If outbox Is Nothing Then Throw New ArgumentNullException(NameOf(outbox))
        If _durableOutbox IsNot Nothing Then Throw New InvalidOperationException("The historian already has a durable recording outbox.")
        _durableOutbox = outbox
    End Sub

    ' Local SQLite file used only for export-job tracking and backups; measurements are in PostgreSQL.
    Public ReadOnly Property DatabasePath As String
        Get
            Return _databasePath
        End Get
    End Property

    ' Retains the legacy planning window; full-resolution history is no longer expired.
    Public Sub UpdateRetention(days As Integer)
        Volatile.Write(_retentionDays, Math.Max(1, days))
        _lastPurgeUtc = DateTime.MinValue
    End Sub

    Public Function TryEnqueue(cycleId As Long, snapshots As IReadOnlyList(Of MeterSnapshot),
                              Optional alarmEvidence As IReadOnlyDictionary(Of String, String) = Nothing) As Boolean
        If Volatile.Read(_stopped) <> 0 Then Throw New ObjectDisposedException(NameOf(MeasurementHistorian))
        If cycleId <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(cycleId))
        If snapshots Is Nothing OrElse snapshots.Count <> 4 Then
            Throw New InvalidDataException("A historian cycle must contain all four transformer quality records.")
        End If

        Dim stored = snapshots.Select(Function(snapshot) CopySnapshot(snapshot)).ToList()
        stored.Add(CreateTotalSnapshot(snapshots))
        If alarmEvidence IsNot Nothing Then
            For Each snapshot In stored
                alarmEvidence.TryGetValue(snapshot.TransformerName, snapshot.AlarmEvidence)
            Next
        End If
        If stored.Any(Function(snapshot) snapshot Is Nothing) OrElse
           stored.Take(4).Select(Function(snapshot) snapshot.TransformerName).Distinct(StringComparer.OrdinalIgnoreCase).Count() <> 4 Then
            Throw New InvalidDataException("A historian cycle must have four uniquely identified transformer records.")
        End If
        Dim cycle As New StoredCycle With {
            .SessionId = _sessionId,
            .CycleId = cycleId,
            .EnqueuedAtUtc = DateTime.UtcNow,
            .Snapshots = stored.ToArray()
        }
        If _queue.Writer.TryWrite(cycle) Then Return True
        Interlocked.Increment(_droppedCycles)
        SetError("Historian queue is full; this acquisition cycle was not queued.")
        Return False
    End Function

    Private Shared Function CreateTotalSnapshot(snapshots As IReadOnlyList(Of MeterSnapshot)) As StoredSnapshot
        Dim valid = snapshots.All(Function(snapshot)
                                      Return snapshot IsNot Nothing AndAlso
                                          snapshot.Quality = MeterQuality.Good AndAlso
                                          snapshot.DecodedValues IsNot Nothing AndAlso
                                          snapshot.DecodedValues.Length = PlcModbus.FieldNames.Length AndAlso
                                          Single.IsFinite(snapshot.DecodedValues(10)) AndAlso
                                          snapshot.CapturedAtUtc.HasValue AndAlso
                                          snapshot.CapturedAtUtc.Value.Kind = DateTimeKind.Utc
                                  End Function)
        Dim capturedAt = If(valid, snapshots.Max(Function(snapshot) snapshot.CapturedAtUtc.Value), DateTime.UtcNow)
        Dim skewMilliseconds As Double? = Nothing
        If valid Then skewMilliseconds = (capturedAt - snapshots.Min(Function(snapshot) snapshot.CapturedAtUtc.Value)).TotalMilliseconds
        Dim accepted = valid AndAlso skewMilliseconds.HasValue AndAlso
            skewMilliseconds.Value <= LoadGaugeMath.MaximumTotalAlignmentSkewMilliseconds
        Dim totalChannels As Double() = Nothing
        If accepted Then
            totalChannels = ElectricalCalculations.TotalChannels(snapshots.Select(Function(snapshot) CType(snapshot.DecodedValues, IReadOnlyList(Of Single))).ToArray())
            accepted = Double.IsFinite(totalChannels(MeasurementCatalog.ActivePowerIndex))
        End If
        Return New StoredSnapshot With {
            .TransformerName = "TOTAL",
            .CapturedAtUtc = capturedAt,
            .Quality = If(accepted, MeterQuality.Good, MeterQuality.BadQuality),
            .AttemptCount = If(accepted, snapshots.Sum(Function(snapshot) snapshot.AttemptCount), 0),
            .ErrorMessage = If(accepted, String.Empty, "TOTAL unavailable: four good PTOT readings from one cycle must be within " &
                LoadGaugeMath.MaximumTotalAlignmentSkewMilliseconds.ToString("0", CultureInfo.InvariantCulture) & " ms."),
            .SampleKind = "Calculated",
            .SourceSkewMilliseconds = skewMilliseconds,
            .Measurements = If(accepted, ToStoredMeasurements(totalChannels), Array.Empty(Of StoredMeasurement)())
        }
    End Function

    Private Shared Function ToStoredMeasurements(channels As Double()) As StoredMeasurement()
        Return channels.
            Select(Function(value, index) New With {value, index}).
            Where(Function(item) Double.IsFinite(item.value)).
            Select(Function(item) New StoredMeasurement With {
                .ParameterIndex = item.index,
                .Value = item.value,
                .Unit = GetUnit(item.index)
            }).ToArray()
    End Function

    Public Function GetDiagnostics() As HistorianDiagnosticsSnapshot
        Dim lastLatency As Double?
        Dim lastWrite As DateTime?
        Dim lastError As String
        SyncLock _diagnosticsGate
            lastLatency = _lastWriteLatencyMilliseconds
            lastWrite = _lastWriteUtc
            lastError = _lastError
        End SyncLock
        If _durableOutbox IsNot Nothing Then
            lastWrite = _durableOutbox.LastTransformerWriteUtc
            If _durableOutbox.LastError.Length > 0 Then lastError = _durableOutbox.LastError
        End If
        Return New HistorianDiagnosticsSnapshot With {
            .QueueDepth = If(_queue.Reader.CanCount, _queue.Reader.Count, -1),
            .DroppedCycles = Interlocked.Read(_droppedCycles),
            .WriteFailures = Interlocked.Read(_writeFailures),
            .LastWriteLatencyMilliseconds = lastLatency,
            .LastWriteUtc = lastWrite,
            .LastError = lastError,
            .SpoolRows = Interlocked.Read(_spoolRows) + If(_durableOutbox Is Nothing, 0, _durableOutbox.PendingCount),
            .SpoolCapacity = SpoolCapacity,
            .SpoolDroppedRows = Interlocked.Read(_spoolDroppedRows)
        }
    End Function

    Private Shared Function CopySnapshot(snapshot As MeterSnapshot) As StoredSnapshot
        If snapshot Is Nothing Then Return Nothing
        Dim measurements As StoredMeasurement() = Array.Empty(Of StoredMeasurement)()
        If snapshot.Quality = MeterQuality.Good Then
            If snapshot.DecodedValues Is Nothing OrElse snapshot.DecodedValues.Length <> PlcModbus.FieldNames.Length OrElse
               snapshot.DecodedValues.Where(Function(value, index) Not Single.IsFinite(value) AndAlso
                   Not PlcModbus.IsUnavailableOffFrequency(snapshot.DecodedValues, index)).Any() Then
                Throw New InvalidDataException("A good-quality historian snapshot must contain finite mapped floats (except unavailable frequency at confirmed low voltage).")
            End If
            measurements = ToStoredMeasurements(ElectricalCalculations.TransformerChannels(snapshot.DecodedValues))
        End If
        Return New StoredSnapshot With {
            .TransformerName = snapshot.TransformerName,
            .CapturedAtUtc = If(snapshot.CapturedAtUtc, DateTime.UtcNow),
            .Quality = snapshot.Quality,
            .AttemptCount = snapshot.AttemptCount,
            .ResponseLatencyMilliseconds = snapshot.ResponseLatencyMilliseconds,
            .ErrorMessage = snapshot.ErrorMessage,
            .SampleKind = "Measured",
            .Measurements = measurements
        }
    End Function

    Private Shared Function GetUnit(parameterIndex As Integer) As String
        Return MeasurementCatalog.GetUnit(parameterIndex)
    End Function

    Private Shared Function GetDeadband(parameterIndex As Integer) As Single
        Select Case parameterIndex
            Case 0 To 7, 15
                Return 0.5F
            Case 8
                Return 0.02F
            Case MeasurementCatalog.PowerFactorIndex
                Return 0.01F
            Case Else
                Return 2.0F
        End Select
    End Function

    Private Shared Function HasMeaningfulChange(previous As Single(), current As Single()) As Boolean
        For index = 0 To ParameterCount - 1
            Dim now = current(index)
            If Single.IsNaN(now) Then Continue For
            Dim before = previous(index)
            If Single.IsNaN(before) OrElse Math.Abs(now - before) >= GetDeadband(index) Then Return True
        Next
        Return False
    End Function

    Private Async Function WriterLoopAsync() As Task
        While Await _queue.Reader.WaitToReadAsync().ConfigureAwait(False)
            Dim batch As New List(Of StoredCycle)(BatchLimit)
            Dim cycle As StoredCycle = Nothing
            If Not _queue.Reader.TryRead(cycle) Then Continue While
            Dim barrier As StoredCycle = Nothing
            While True
                If cycle.BarrierCompletion IsNot Nothing Then
                    barrier = cycle
                    Exit While
                End If
                batch.Add(cycle)
                If batch.Count >= BatchLimit OrElse Not _queue.Reader.TryRead(cycle) Then Exit While
            End While
            Dim batchSucceeded = True
            If batch.Count > 0 Then batchSucceeded = Await WriteWithRetryAsync(batch).ConfigureAwait(False)
            If barrier IsNot Nothing Then
                Try
                    If Not _databaseInitialized Then InitializeDatabase()
                    barrier.BarrierCompletion.TrySetResult(batchSucceeded)
                Catch ex As Exception
                    barrier.BarrierCompletion.TrySetException(ex)
                    Throw
                End Try
            End If
        End While
    End Function

    Public Async Function CreateBackupDatabaseAsync(destinationPath As String) As Task
        If String.IsNullOrWhiteSpace(destinationPath) Then Throw New ArgumentException("A backup database path is required.", NameOf(destinationPath))
        If Volatile.Read(_stopped) <> 0 Then Throw New ObjectDisposedException(NameOf(MeasurementHistorian))
        Dim fullDestination = Path.GetFullPath(destinationPath)
        If String.Equals(fullDestination, _databasePath, StringComparison.OrdinalIgnoreCase) Then
            Throw New ArgumentException("Historian backup destination must differ from the active database.", NameOf(destinationPath))
        End If
        If File.Exists(fullDestination) Then Throw New IOException("Historian backup destination already exists: " & fullDestination)

        Dim completion As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Await _queue.Writer.WriteAsync(New StoredCycle With {.BarrierCompletion = completion}).ConfigureAwait(False)
        Dim completed = Await Task.WhenAny(completion.Task, _writerTask).ConfigureAwait(False)
        If completed Is _writerTask Then
            Await _writerTask.ConfigureAwait(False)
            Throw New IOException("Historian writer stopped before the backup barrier was completed.")
        End If
        If Not Await completion.Task.ConfigureAwait(False) Then
            Throw New IOException("Historian could not flush queued cycles before backup.")
        End If

        Dim parent = Path.GetDirectoryName(fullDestination)
        If String.IsNullOrWhiteSpace(parent) Then Throw New IOException("Historian backup destination has no parent folder.")
        Directory.CreateDirectory(parent)
        Await Task.Run(
            Sub()
                Using source = OpenConnection()
                    Dim destinationSettings As New SqliteConnectionStringBuilder With {
                        .DataSource = fullDestination,
                        .Mode = SqliteOpenMode.ReadWriteCreate,
                        .Cache = SqliteCacheMode.Private,
                        .Pooling = False
                    }
                    Using destination As New SqliteConnection(destinationSettings.ToString())
                        destination.Open()
                        source.BackupDatabase(destination)
                    End Using
                End Using
            End Sub).ConfigureAwait(False)
    End Function

    Private Sub InitializeDatabase()
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath))
        Using connection = OpenConnection()
            Using command = connection.CreateCommand()
                command.CommandText = "PRAGMA journal_mode=WAL;PRAGMA synchronous=NORMAL;" &
                    "CREATE TABLE IF NOT EXISTS spool_samples(id INTEGER PRIMARY KEY AUTOINCREMENT, analyzer_id TEXT NOT NULL, " &
                    "captured_ticks INTEGER NOT NULL, session_id TEXT NOT NULL, cycle_id INTEGER NOT NULL, sample_kind TEXT NOT NULL, vals BLOB NOT NULL);" &
                    "CREATE TABLE IF NOT EXISTS spool_quality(analyzer_id TEXT NOT NULL, bucket_ticks INTEGER NOT NULL, total INTEGER NOT NULL, " &
                    "good INTEGER NOT NULL, bad INTEGER NOT NULL, first_ticks INTEGER NOT NULL, last_ticks INTEGER NOT NULL, " &
                    "PRIMARY KEY(analyzer_id, bucket_ticks));"
                command.ExecuteNonQuery()
                command.CommandText = "SELECT count(*) FROM pragma_table_info('spool_samples') WHERE name='alarm_evidence'"
                If CLng(command.ExecuteScalar()) = 0 Then
                    command.CommandText = "ALTER TABLE spool_samples ADD COLUMN alarm_evidence TEXT"
                    command.ExecuteNonQuery()
                End If
                command.CommandText = "SELECT (SELECT COUNT(*) FROM spool_samples), (SELECT COUNT(*) FROM spool_quality)"
                Using reader = command.ExecuteReader()
                    If reader.Read() Then
                        Interlocked.Exchange(_spoolRows, reader.GetInt64(0))
                        _spoolPending = reader.GetInt64(0) > 0 OrElse reader.GetInt64(1) > 0
                    End If
                End Using
            End Using
        End Using
        _databaseInitialized = True
    End Sub

    Private Async Function WriteWithRetryAsync(batch As IReadOnlyList(Of StoredCycle)) As Task(Of Boolean)
        Dim reduced As ReducedBatch
        Try
            If Not _databaseInitialized Then InitializeDatabase()
            reduced = Reduce(batch)
        Catch ex As Exception When TypeOf ex Is SqliteException OrElse TypeOf ex Is IOException OrElse
                                   TypeOf ex Is UnauthorizedAccessException
            Interlocked.Add(_droppedCycles, batch.Count)
            SetError("Historian local buffer is unavailable; cycles dropped: " & ex.Message)
            Return False
        End Try

        If _durableOutbox IsNot Nothing Then
            Try
                Dim commands As New List(Of RecordedCommand)()
                For Each row In reduced.Quality.Values
                    commands.Add(CreateQualityCommand(row))
                Next
                For Each sample In reduced.Samples
                    commands.Add(CreateSampleCommand(sample.AnalyzerId, sample.CapturedAtUtc, sample.SessionId,
                        sample.CycleId, sample.SampleKind, sample.Values, sample.AlarmEvidence))
                Next
                _durableOutbox.Enqueue(commands)
                If _maintenance.IsCompleted AndAlso DateTime.UtcNow - _lastPurgeUtc > PurgeInterval Then
                    _lastPurgeUtc = DateTime.UtcNow
                    _maintenance = Task.Run(Sub()
                                                Try
                                                    Using connection = PostgresDatabase.Open()
                                                        If Not _legacyChecked Then
                                                            _legacyChecked = True
                                                            StartLegacyMigration()
                                                        End If
                                                        DrainSpool(connection)
                                                    End Using
                                                    SetError(String.Empty)
                                                Catch ex As Exception When TypeOf ex Is NpgsqlException OrElse TypeOf ex Is SqliteException OrElse
                                                    TypeOf ex Is IOException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is TimeoutException
                                                    SetError("Historian maintenance pending: " & ex.Message)
                                                    _lastPurgeUtc = DateTime.MinValue
                                                End Try
                                            End Sub)
                End If
                Return True
            Catch ex As Exception When TypeOf ex Is SqliteException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Interlocked.Add(_droppedCycles, batch.Count)
                SetError("Historian durable recording failed; cycles not buffered: " & ex.Message)
                Return False
            End Try
        End If

        ' While PostgreSQL is known to be down, try once per batch and buffer locally instead of stalling the writer.
        Dim attempts = If(_postgresDown, 1, MaximumWriteAttempts)
        For attempt = 1 To attempts
            Dim timer = Stopwatch.StartNew()
            Try
                CommitToPostgres(reduced)
                timer.Stop()
                _postgresDown = False
                SyncLock _diagnosticsGate
                    _lastWriteLatencyMilliseconds = timer.Elapsed.TotalMilliseconds
                    _lastWriteUtc = DateTime.UtcNow
                    _lastError = If(_spoolPending, "Replaying buffered records to PostgreSQL.", String.Empty)
                End SyncLock
                Return True
            Catch ex As Exception When TypeOf ex Is NpgsqlException OrElse TypeOf ex Is SqliteException OrElse
                                       TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
                                       TypeOf ex Is SecurityException OrElse TypeOf ex Is InvalidOperationException
                timer.Stop()
                Interlocked.Increment(_writeFailures)
                SetError("Historian PostgreSQL write failed (attempt " &
                         attempt.ToString(CultureInfo.InvariantCulture) & "/" &
                         attempts.ToString(CultureInfo.InvariantCulture) & "): " & ex.Message)
            End Try
            If attempt < attempts Then
                Await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1))).ConfigureAwait(False)
            End If
        Next

        _postgresDown = True
        Try
            AppendToSpool(reduced)
            Dim rows = Interlocked.Read(_spoolRows)
            SetError("PostgreSQL unavailable - " & rows.ToString("N0", CultureInfo.InvariantCulture) & " record(s) buffered locally (" &
                     (100.0R * rows / SpoolCapacity).ToString("0.#", CultureInfo.InvariantCulture) &
                     "% of buffer). Check the PostgreSQL password/connection in Configuration.")
        Catch ex As Exception When TypeOf ex Is SqliteException OrElse TypeOf ex Is IOException OrElse
                                   TypeOf ex Is UnauthorizedAccessException
            Interlocked.Add(_droppedCycles, batch.Count)
            SetError("PostgreSQL unavailable and the local buffer failed; cycles dropped: " & ex.Message)
        End Try
        Return False
    End Function

    Private Shared Function ToUtc(value As DateTime) As DateTime
        Select Case value.Kind
            Case DateTimeKind.Utc
                Return value
            Case DateTimeKind.Local
                Return value.ToUniversalTime()
            Case Else
                Return DateTime.SpecifyKind(value, DateTimeKind.Utc)
        End Select
    End Function

    ' Applies the dead-band/heartbeat filter and per-minute quality aggregation once, so a retry or a spool
    ' replay stores exactly the same rows.
    Private Function Reduce(batch As IReadOnlyList(Of StoredCycle)) As ReducedBatch
        Dim result As New ReducedBatch()
        For Each cycle In batch
            For Each snapshot In cycle.Snapshots
                Dim capturedAt = ToUtc(snapshot.CapturedAtUtc)
                Dim good = snapshot.Quality = MeterQuality.Good
                Dim minuteStart = New DateTime(capturedAt.Ticks - (capturedAt.Ticks Mod TimeSpan.TicksPerMinute), DateTimeKind.Utc)
                Dim key = snapshot.TransformerName & "|" & minuteStart.Ticks.ToString(CultureInfo.InvariantCulture)
                Dim quality As QualityRow = Nothing
                If Not result.Quality.TryGetValue(key, quality) Then
                    quality = New QualityRow With {
                        .AnalyzerId = snapshot.TransformerName, .BucketStartUtc = minuteStart,
                        .FirstAtUtc = capturedAt, .LastAtUtc = capturedAt}
                    result.Quality.Add(key, quality)
                End If
                quality.Total += 1
                If good Then quality.Good += 1 Else quality.Bad += 1
                If capturedAt < quality.FirstAtUtc Then quality.FirstAtUtc = capturedAt
                If capturedAt > quality.LastAtUtc Then quality.LastAtUtc = capturedAt
                If Not good OrElse snapshot.Measurements.Length = 0 Then Continue For

                Dim values = BuildValues(snapshot)
                result.Samples.Add(New SampleRow With {
                    .AnalyzerId = snapshot.TransformerName, .CapturedAtUtc = capturedAt,
                    .SessionId = Guid.ParseExact(cycle.SessionId, "N"), .CycleId = cycle.CycleId,
                    .SampleKind = snapshot.SampleKind, .Values = values, .AlarmEvidence = snapshot.AlarmEvidence})
            Next
        Next
        Return result
    End Function

    Private Sub CommitToPostgres(reduced As ReducedBatch)
        Using connection = PostgresDatabase.Open()
            If Not _legacyChecked Then
                _legacyChecked = True
                StartLegacyMigration()
            End If
            DrainSpool(connection)
            CommitRows(connection, reduced.Samples, reduced.Quality.Values)
            If DateTime.UtcNow - _lastPurgeUtc > PurgeInterval Then
                _lastPurgeUtc = DateTime.UtcNow
                Try
                Catch ex As NpgsqlException
                    SetError("Historian retention cleanup failed: " & ex.Message)
                End Try
            End If
        End Using
    End Sub

    Private Shared Sub CommitRows(connection As NpgsqlConnection, samples As IEnumerable(Of SampleRow), quality As IEnumerable(Of QualityRow))
        Using transaction = connection.BeginTransaction()
            For Each row In quality
                WriteQualityMinute(connection, transaction, row)
            Next
            For Each sample In samples
                InsertSample(connection, transaction, sample.AnalyzerId, sample.CapturedAtUtc, sample.SessionId,
                             sample.CycleId, sample.SampleKind, sample.Values, sample.AlarmEvidence)
            Next
            transaction.Commit()
        End Using
    End Sub

    Private Sub AppendToSpool(reduced As ReducedBatch)
        Using connection = OpenConnection()
            Using transaction = connection.BeginTransaction()
                For Each sample In reduced.Samples
                    Using command = connection.CreateCommand()
                        command.Transaction = transaction
                        command.CommandText = "INSERT INTO spool_samples(analyzer_id, captured_ticks, session_id, cycle_id, sample_kind, vals, alarm_evidence) VALUES ($a,$t,$s,$c,$k,$v,$e)"
                        command.Parameters.AddWithValue("$a", sample.AnalyzerId)
                        command.Parameters.AddWithValue("$t", sample.CapturedAtUtc.Ticks)
                        command.Parameters.AddWithValue("$s", sample.SessionId.ToString("N"))
                        command.Parameters.AddWithValue("$c", sample.CycleId)
                        command.Parameters.AddWithValue("$k", sample.SampleKind)
                        command.Parameters.AddWithValue("$e", If(sample.AlarmEvidence, CObj(DBNull.Value)))
                        Dim bytes(sample.Values.Length * 4 - 1) As Byte
                        Buffer.BlockCopy(sample.Values, 0, bytes, 0, bytes.Length)
                        command.Parameters.AddWithValue("$v", bytes)
                        command.ExecuteNonQuery()
                    End Using
                Next
                For Each row In reduced.Quality.Values
                    Using command = connection.CreateCommand()
                        command.Transaction = transaction
                        command.CommandText =
                            "INSERT INTO spool_quality(analyzer_id, bucket_ticks, total, good, bad, first_ticks, last_ticks) VALUES ($a,$b,$t,$g,$x,$f,$l) " &
                            "ON CONFLICT(analyzer_id, bucket_ticks) DO UPDATE SET total = total + excluded.total, good = good + excluded.good, " &
                            "bad = bad + excluded.bad, first_ticks = min(first_ticks, excluded.first_ticks), last_ticks = max(last_ticks, excluded.last_ticks)"
                        command.Parameters.AddWithValue("$a", row.AnalyzerId)
                        command.Parameters.AddWithValue("$b", row.BucketStartUtc.Ticks)
                        command.Parameters.AddWithValue("$t", row.Total)
                        command.Parameters.AddWithValue("$g", row.Good)
                        command.Parameters.AddWithValue("$x", row.Bad)
                        command.Parameters.AddWithValue("$f", row.FirstAtUtc.Ticks)
                        command.Parameters.AddWithValue("$l", row.LastAtUtc.Ticks)
                        command.ExecuteNonQuery()
                    End Using
                Next
                transaction.Commit()
            End Using
            Interlocked.Add(_spoolRows, reduced.Samples.Count)
            _spoolPending = True

            ' Bounded buffer: when full, the oldest buffered rows are discarded and counted as lost.
            Dim excess = Interlocked.Read(_spoolRows) - SpoolCapacity
            If excess > 0 Then
                Dim removeCount = excess + SpoolCapacity \ 20
                Using command = connection.CreateCommand()
                    command.CommandText = "DELETE FROM spool_samples WHERE id IN (SELECT id FROM spool_samples ORDER BY id LIMIT $n)"
                    command.Parameters.AddWithValue("$n", removeCount)
                    Dim removed = command.ExecuteNonQuery()
                    Interlocked.Add(_spoolRows, -removed)
                    Interlocked.Add(_spoolDroppedRows, removed)
                End Using
            End If
        End Using
    End Sub

    ' Replays buffered rows (oldest first) into PostgreSQL; a bounded amount per call keeps the writer responsive.
    Private Sub DrainSpool(connection As NpgsqlConnection)
        If Not _spoolPending Then Return
        Using lite = OpenConnection()
            For chunk = 1 To SpoolDrainChunksPerCall
                Dim samples As New List(Of SampleRow)()
                Dim maxId As Long = 0
                Using command = lite.CreateCommand()
                    command.CommandText = "SELECT id, analyzer_id, captured_ticks, session_id, cycle_id, sample_kind, vals, alarm_evidence FROM spool_samples ORDER BY id LIMIT " &
                        SpoolDrainChunk.ToString(CultureInfo.InvariantCulture)
                    Using reader = command.ExecuteReader()
                        While reader.Read()
                            maxId = reader.GetInt64(0)
                            Dim bytes = CType(reader.GetValue(6), Byte())
                            Dim values(bytes.Length \ 4 - 1) As Single
                            Buffer.BlockCopy(bytes, 0, values, 0, bytes.Length)
                            samples.Add(New SampleRow With {
                                .AnalyzerId = reader.GetString(1),
                                .CapturedAtUtc = New DateTime(reader.GetInt64(2), DateTimeKind.Utc),
                                .SessionId = Guid.ParseExact(reader.GetString(3), "N"),
                                .CycleId = reader.GetInt64(4), .SampleKind = reader.GetString(5), .Values = values,
                                .AlarmEvidence = If(reader.IsDBNull(7), Nothing, reader.GetString(7))})
                        End While
                    End Using
                End Using
                If samples.Count = 0 Then Exit For
                CommitRows(connection, samples, Array.Empty(Of QualityRow)())
                Using command = lite.CreateCommand()
                    command.CommandText = "DELETE FROM spool_samples WHERE id <= $id"
                    command.Parameters.AddWithValue("$id", maxId)
                    command.ExecuteNonQuery()
                End Using
                Interlocked.Add(_spoolRows, -samples.Count)
            Next
            If Interlocked.Read(_spoolRows) > 0 Then Return

            Dim quality As New List(Of QualityRow)()
            Using command = lite.CreateCommand()
                command.CommandText = "SELECT analyzer_id, bucket_ticks, total, good, bad, first_ticks, last_ticks FROM spool_quality"
                Using reader = command.ExecuteReader()
                    While reader.Read()
                        quality.Add(New QualityRow With {
                            .AnalyzerId = reader.GetString(0), .BucketStartUtc = New DateTime(reader.GetInt64(1), DateTimeKind.Utc),
                            .Total = reader.GetInt32(2), .Good = reader.GetInt32(3), .Bad = reader.GetInt32(4),
                            .FirstAtUtc = New DateTime(reader.GetInt64(5), DateTimeKind.Utc),
                            .LastAtUtc = New DateTime(reader.GetInt64(6), DateTimeKind.Utc)})
                    End While
                End Using
            End Using
            If quality.Count > 0 Then CommitRows(connection, Array.Empty(Of SampleRow)(), quality)
            Using command = lite.CreateCommand()
                command.CommandText = "DELETE FROM spool_quality"
                command.ExecuteNonQuery()
            End Using
            _spoolPending = False
        End Using
    End Sub

    Private Shared Function BuildValues(snapshot As StoredSnapshot) As Single()
        Dim values(ParameterCount - 1) As Single
        For index = 0 To ParameterCount - 1
            values(index) = Single.NaN
        Next
        For Each measurement In snapshot.Measurements
            values(measurement.ParameterIndex) = CSng(measurement.Value)
        Next
        Return values
    End Function

    Private Shared Sub InsertSample(connection As NpgsqlConnection, transaction As NpgsqlTransaction, analyzerId As String,
                                    capturedAtUtc As DateTime, sessionId As Guid, cycleId As Long, sampleKind As String,
                                    values As Single(), Optional alarmEvidence As String = Nothing)
        CreateSampleCommand(analyzerId, capturedAtUtc, sessionId, cycleId, sampleKind, values, alarmEvidence).Execute(connection, transaction)
    End Sub

    Private Shared Function CreateSampleCommand(analyzerId As String, capturedAtUtc As DateTime, sessionId As Guid,
                                               cycleId As Long, sampleKind As String, values As Single(), alarmEvidence As String) As RecordedCommand
        Using command As New NpgsqlCommand()
            command.CommandText =
                "INSERT INTO measurement_samples(analyzer_id, captured_at, session_id, cycle_id, sample_kind, vals, alarm_evidence) " &
                "VALUES (@a, @t, @s, @c, @k, @v, @e) ON CONFLICT DO NOTHING"
            command.Parameters.AddWithValue("a", analyzerId)
            command.Parameters.AddWithValue("t", capturedAtUtc)
            command.Parameters.AddWithValue("s", sessionId)
            command.Parameters.AddWithValue("c", cycleId)
            command.Parameters.AddWithValue("k", sampleKind)
            command.Parameters.AddWithValue("v", values)
            command.Parameters.AddWithValue("e", NpgsqlTypes.NpgsqlDbType.Jsonb, If(alarmEvidence, CObj(DBNull.Value)))
            Return RecordedCommand.Capture(command)
        End Using
    End Function

    Private Shared Sub WriteQualityMinute(connection As NpgsqlConnection, transaction As NpgsqlTransaction, row As QualityRow)
        CreateQualityCommand(row).Execute(connection, transaction)
    End Sub

    Private Shared Function CreateQualityCommand(row As QualityRow) As RecordedCommand
        Using command As New NpgsqlCommand()
            command.CommandText =
                "INSERT INTO sample_quality_minute(analyzer_id, bucket_start, total_samples, good_samples, bad_samples, first_at, last_at) " &
                "VALUES (@a, @b, @n, @g, @x, @f, @l) " &
                "ON CONFLICT (analyzer_id, bucket_start) DO UPDATE SET " &
                "total_samples = sample_quality_minute.total_samples + EXCLUDED.total_samples, " &
                "good_samples = sample_quality_minute.good_samples + EXCLUDED.good_samples, " &
                "bad_samples = sample_quality_minute.bad_samples + EXCLUDED.bad_samples, " &
                "first_at = LEAST(sample_quality_minute.first_at, EXCLUDED.first_at), " &
                "last_at = GREATEST(sample_quality_minute.last_at, EXCLUDED.last_at)"
            command.Parameters.AddWithValue("a", row.AnalyzerId)
            command.Parameters.AddWithValue("b", row.BucketStartUtc)
            command.Parameters.AddWithValue("n", row.Total)
            command.Parameters.AddWithValue("g", row.Good)
            command.Parameters.AddWithValue("x", row.Bad)
            command.Parameters.AddWithValue("f", row.FirstAtUtc)
            command.Parameters.AddWithValue("l", row.LastAtUtc)
            Return RecordedCommand.Capture(command)
        End Using
    End Function

    Private Shared Function GetParameterId(parameterIndex As Integer) As String
        If parameterIndex < 0 OrElse parameterIndex >= ParameterCount Then
            Throw New ArgumentOutOfRangeException(NameOf(parameterIndex))
        End If
        Return ParameterIdList(parameterIndex)
    End Function

    Private Shared Function GetParameterIndex(parameterId As String) As Integer
        Return Array.IndexOf(ParameterIdList, parameterId)
    End Function

    Public Function GetRangeSummary(analyzerId As String,
                                    startUtc As DateTime,
                                    endUtc As DateTime) As HistorianRangeSummary
        ValidateRange(analyzerId, startUtc, endUtc)
        Try
            Using connection = PostgresDatabase.Open()
                Using command = connection.CreateCommand()
                    command.CommandText =
                        "SELECT COALESCE(SUM(total_samples), 0), COALESCE(SUM(good_samples), 0), COALESCE(SUM(bad_samples), 0), " &
                        "MIN(first_at), MAX(last_at) FROM sample_quality_minute " &
                        "WHERE analyzer_id = @a AND bucket_start >= @start AND bucket_start < @end"
                    command.Parameters.AddWithValue("a", analyzerId)
                    command.Parameters.AddWithValue("start", startUtc)
                    command.Parameters.AddWithValue("end", endUtc)
                    Using reader = command.ExecuteReader()
                        If Not reader.Read() Then Return New HistorianRangeSummary()
                        Return New HistorianRangeSummary With {
                            .AvailableSampleCount = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                            .GoodSampleCount = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                            .BadSampleCount = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
                            .FirstSampleUtc = If(reader.IsDBNull(3), CType(Nothing, DateTime?), DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)),
                            .LastSampleUtc = If(reader.IsDBNull(4), CType(Nothing, DateTime?), DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc))
                        }
                    End Using
                End Using
            End Using
        Catch ex As Exception When TypeOf ex Is NpgsqlException OrElse TypeOf ex Is InvalidOperationException
            SetError("Historian read failed: " & ex.Message)
            Throw New IOException("Historian data could not be read. Database unavailability is not an empty history.", ex)
        End Try
    End Function

    ' Size of the PostgreSQL database holding the measurements, or -1 when unreachable.
    Public Function GetDatabaseSizeBytes() As Long
        Try
            Using connection = PostgresDatabase.Open()
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT pg_database_size(current_database())"
                    Return CLng(command.ExecuteScalar())
                End Using
            End Using
        Catch ex As Exception When TypeOf ex Is NpgsqlException OrElse TypeOf ex Is InvalidOperationException
            Return -1
        End Try
    End Function

    Public Function GetEarliestSampleUtc() As DateTime?
        Return QueryBoundary("MIN(first_at)")
    End Function

    Public Function GetLatestSampleUtc() As DateTime?
        Return QueryBoundary("MAX(last_at)")
    End Function

    Private Function QueryBoundary(expression As String) As DateTime?
        Try
            Using connection = PostgresDatabase.Open()
                Using command = connection.CreateCommand()
                    command.CommandText = "SELECT " & expression & " FROM sample_quality_minute"
                    Dim value = command.ExecuteScalar()
                    If value Is Nothing OrElse value Is DBNull.Value Then Return Nothing
                    Return DateTime.SpecifyKind(CDate(value), DateTimeKind.Utc)
                End Using
            End Using
        Catch ex As Exception When TypeOf ex Is NpgsqlException OrElse TypeOf ex Is InvalidOperationException
            SetError("Historian read failed: " & ex.Message)
            Throw New IOException("Historian time range could not be read. Check the PostgreSQL connection.", ex)
        End Try
    End Function

    Private Shared Function ResolveIndexes(parameterIds As IReadOnlyCollection(Of String)) As Integer()
        Return parameterIds.Distinct(StringComparer.Ordinal).
            Select(Function(id) GetParameterIndex(id) + 1).
            Where(Function(index) index > 0).ToArray()
    End Function

    ' Averages (bucketSeconds > 0) or raw rows of stored parameters for trend pages; NaN/missing values come back as Nothing.
    Public Function ReadSeries(analyzerId As String, parameterIds As String(), startUtc As DateTime, endUtc As DateTime,
                               bucketSeconds As Integer) As IReadOnlyList(Of UpsColumnSample)
        ValidateRange(analyzerId, startUtc, endUtc)
        Dim indexes = parameterIds.Select(Function(id) GetParameterIndex(id) + 1).ToArray()
        If indexes.Any(Function(index) index <= 0) Then Throw New ArgumentException("Unknown historian parameter.", NameOf(parameterIds))
        Dim sql As String
        If bucketSeconds > 0 Then
            sql = "SELECT to_timestamp(floor(extract(epoch FROM captured_at)/@b)*@b), " &
                String.Join(", ", indexes.Select(Function(index) "avg(NULLIF(vals[" & index.ToString(CultureInfo.InvariantCulture) & "],'NaN'::real))")) &
                " FROM measurement_samples WHERE analyzer_id=@a AND captured_at>=@f AND captured_at<@t GROUP BY 1 ORDER BY 1"
        Else
            sql = "SELECT captured_at, " & String.Join(", ", indexes.Select(Function(index) "vals[" & index.ToString(CultureInfo.InvariantCulture) & "]")) &
                " FROM measurement_samples WHERE analyzer_id=@a AND captured_at>=@f AND captured_at<@t ORDER BY captured_at"
        End If
        Dim samples As New List(Of UpsColumnSample)()
        Try
            Using connection = PostgresDatabase.Open()
                Using command As New NpgsqlCommand(sql, connection)
                    command.CommandTimeout = 60
                    command.Parameters.AddWithValue("a", analyzerId)
                    command.Parameters.AddWithValue("f", NpgsqlTypes.NpgsqlDbType.TimestampTz, startUtc)
                    command.Parameters.AddWithValue("t", NpgsqlTypes.NpgsqlDbType.TimestampTz, endUtc)
                    If bucketSeconds > 0 Then command.Parameters.AddWithValue("b", NpgsqlTypes.NpgsqlDbType.Double, CDbl(bucketSeconds))
                    Using reader = command.ExecuteReader()
                        While reader.Read()
                            Dim values(indexes.Length - 1) As Double?
                            For column = 0 To indexes.Length - 1
                                If Not reader.IsDBNull(column + 1) Then
                                    Dim value = Convert.ToDouble(reader.GetValue(column + 1), CultureInfo.InvariantCulture)
                                    If Double.IsFinite(value) Then values(column) = value
                                End If
                            Next
                            samples.Add(New UpsColumnSample With {.TimestampUtc = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc), .Values = values})
                        End While
                    End Using
                End Using
            End Using
        Catch ex As Exception When TypeOf ex Is NpgsqlException OrElse TypeOf ex Is InvalidOperationException
            SetError("Historian read failed: " & ex.Message)
            Throw New IOException("Historian data could not be read. Database unavailability is not an empty history.", ex)
        End Try
        Return samples
    End Function

    Public Sub StreamRawMeasurements(analyzerId As String,
                                     parameterIds As IReadOnlyCollection(Of String),
                                     startUtc As DateTime,
                                     endUtc As DateTime,
                                     consume As Action(Of HistorianMeasurementPoint),
                                     cancellationToken As CancellationToken,
                                     Optional includeAlarmEvidence As Boolean = True)
        ValidateRange(analyzerId, startUtc, endUtc)
        If parameterIds Is Nothing OrElse parameterIds.Count = 0 Then Throw New ArgumentException("At least one parameter is required.", NameOf(parameterIds))
        If consume Is Nothing Then Throw New ArgumentNullException(NameOf(consume))
        Dim indexes = ResolveIndexes(parameterIds)
        If indexes.Length = 0 Then Return

        Using connection = PostgresDatabase.Open()
            Using command = connection.CreateCommand()
                command.CommandText =
                    "SELECT s.session_id, s.analyzer_id, u.i, s.captured_at, u.v, s.sample_kind, s.cycle_id, " & If(includeAlarmEvidence, "s.alarm_evidence::text ", "NULL::text ") &
                    "FROM measurement_samples s CROSS JOIN LATERAL unnest(s.vals) WITH ORDINALITY AS u(v, i) " &
                    "WHERE s.analyzer_id = @a AND s.captured_at >= @start AND s.captured_at < @end " &
                    "AND u.i = ANY(@idx) AND u.v <> 'NaN'::real ORDER BY s.captured_at, s.cycle_id, u.i"
                command.Parameters.AddWithValue("a", analyzerId)
                command.Parameters.AddWithValue("start", startUtc)
                command.Parameters.AddWithValue("end", endUtc)
                command.Parameters.AddWithValue("idx", indexes.Select(Function(index) CLng(index)).ToArray())
                Using registration = cancellationToken.Register(Sub() command.Cancel())
                    Using reader = command.ExecuteReader()
                        While reader.Read()
                            cancellationToken.ThrowIfCancellationRequested()
                            Dim parameterIndex = CInt(reader.GetInt64(2)) - 1
                            consume(New HistorianMeasurementPoint With {
                                .AcquisitionSessionId = reader.GetGuid(0).ToString("N"),
                                .AnalyzerId = reader.GetString(1),
                                .ParameterId = GetParameterId(parameterIndex),
                                .TimestampUtc = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
                                .Value = CDbl(reader.GetFloat(4)),
                                .Unit = GetUnit(parameterIndex),
                                .Quality = "Good",
                                .SampleKind = reader.GetString(5),
                                .AcquisitionCycleId = reader.GetInt64(6), .AlarmEvidence = If(reader.IsDBNull(7), Nothing, reader.GetString(7))
                            })
                        End While
                    End Using
                End Using
            End Using
        End Using
    End Sub

    Public Function CountRawMeasurements(analyzerId As String,
                                         parameterIds As IReadOnlyCollection(Of String),
                                         startUtc As DateTime,
                                         endUtc As DateTime) As Long
        ValidateRange(analyzerId, startUtc, endUtc)
        If parameterIds Is Nothing OrElse parameterIds.Count = 0 Then Return 0
        Dim indexes = ResolveIndexes(parameterIds)
        If indexes.Length = 0 Then Return 0
        Try
            Using connection = PostgresDatabase.Open()
                Using command = connection.CreateCommand()
                    command.CommandText =
                        "SELECT count(*) FROM measurement_samples s CROSS JOIN LATERAL unnest(s.vals) WITH ORDINALITY AS u(v, i) " &
                        "WHERE s.analyzer_id = @a AND s.captured_at >= @start AND s.captured_at < @end " &
                        "AND u.i = ANY(@idx) AND u.v <> 'NaN'::real"
                    command.Parameters.AddWithValue("a", analyzerId)
                    command.Parameters.AddWithValue("start", startUtc)
                    command.Parameters.AddWithValue("end", endUtc)
                    command.Parameters.AddWithValue("idx", indexes.Select(Function(index) CLng(index)).ToArray())
                    Return CLng(command.ExecuteScalar())
                End Using
            End Using
        Catch ex As Exception When TypeOf ex Is NpgsqlException OrElse TypeOf ex Is InvalidOperationException
            SetError("Historian read failed: " & ex.Message)
            Throw New IOException("Historian measurement count could not be read. Check the PostgreSQL connection.", ex)
        End Try
    End Function

    Private Shared Sub ValidateRange(analyzerId As String, startUtc As DateTime, endUtc As DateTime)
        If String.IsNullOrWhiteSpace(analyzerId) Then Throw New ArgumentException("An analyzer is required.", NameOf(analyzerId))
        If startUtc.Kind <> DateTimeKind.Utc OrElse endUtc.Kind <> DateTimeKind.Utc OrElse endUtc <= startUtc Then
            Throw New ArgumentOutOfRangeException(NameOf(startUtc), "Historian range must be a positive UTC interval.")
        End If
    End Sub

    Private Shared Function GetParameterUnit(parameterId As String) As String
        Select Case parameterId
            Case "PTOT_KW"
                Return "kW"
            Case "TDEGC_C"
                Return "°C (source unverified)"
            Case "FREQUENCY"
                Return "Hz"
            Case Else
                Return If(parameterId.StartsWith("V_", StringComparison.Ordinal) OrElse parameterId.StartsWith("AVG_V", StringComparison.Ordinal), "V", "A")
        End Select
    End Function

    Private Function OpenConnection() As SqliteConnection
        Dim builder As New SqliteConnectionStringBuilder With {
            .DataSource = _databasePath,
            .Mode = SqliteOpenMode.ReadWriteCreate,
            .Cache = SqliteCacheMode.Shared,
            .Pooling = True
        }
        Dim connection As New SqliteConnection(builder.ToString())
        connection.Open()
        Return connection
    End Function

    ' One-time import of the old SQLite historian into PostgreSQL, thinned with the same dead-band rules,
    ' followed by dropping the old tables and shrinking the file.
    Private Sub StartLegacyMigration()
        If Not File.Exists(_databasePath) Then Return
        Task.Run(
            Sub()
                Try
                    MigrateLegacy()
                Catch ex As Exception When TypeOf ex Is NpgsqlException OrElse TypeOf ex Is SqliteException OrElse
                                           TypeOf ex Is IOException OrElse TypeOf ex Is InvalidOperationException
                    SetError("Legacy historian migration failed (will retry on next start): " & ex.Message)
                End Try
            End Sub)
    End Sub

    Private Sub MigrateLegacy()
        Using legacy = OpenConnection()
            If LegacyTableExists(legacy) Then
                If PostgresDatabase.TryReadDocument(LegacyMigratedKey) Is Nothing Then
                    CopyLegacyData(legacy)
                    If Not PostgresDatabase.TryWriteDocument(LegacyMigratedKey, "true", "migration") Then
                        Throw New InvalidOperationException("Could not record migration completion in PostgreSQL.")
                    End If
                End If
                Using drop = legacy.CreateCommand()
                    drop.CommandText =
                        "DROP TABLE IF EXISTS MeasurementBuckets;DROP TABLE IF EXISTS SampleQualityBuckets;" &
                        "DROP TABLE IF EXISTS MeasurementValues;DROP TABLE IF EXISTS TransformerSamples;" &
                        "DROP TABLE IF EXISTS AcquisitionCycles;"
                    drop.CommandTimeout = 600
                    drop.ExecuteNonQuery()
                End Using
            End If
        End Using
        If New FileInfo(_databasePath).Length < 64L * 1024 * 1024 Then Return
        SqliteConnection.ClearAllPools()
        Using compact As New SqliteConnection(New SqliteConnectionStringBuilder With {
                .DataSource = _databasePath, .Mode = SqliteOpenMode.ReadWrite, .Pooling = False}.ToString())
            compact.Open()
            Using command = compact.CreateCommand()
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);VACUUM;"
                command.CommandTimeout = 3600
                command.ExecuteNonQuery()
            End Using
        End Using
    End Sub

    Private Shared Function LegacyTableExists(connection As SqliteConnection) As Boolean
        Using command = connection.CreateCommand()
            command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='TransformerSamples'"
            Return command.ExecuteScalar() IsNot Nothing
        End Using
    End Function

    Private Shared Function ParseLegacyTime(text As String) As DateTime
        Return DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime()
    End Function

    Private Sub CopyLegacyData(legacy As SqliteConnection)
        Dim minimumSeconds = 10.0R
        Using target = PostgresDatabase.Open()
            Dim analyzers As New List(Of String)()
            Using command = legacy.CreateCommand()
                command.CommandText = "SELECT DISTINCT TransformerId FROM TransformerSamples"
                Using reader = command.ExecuteReader()
                    While reader.Read()
                        analyzers.Add(reader.GetString(0))
                    End While
                End Using
            End Using

            For Each analyzer In analyzers
                CopyLegacyQuality(legacy, target, analyzer)
                CopyLegacySamples(legacy, target, analyzer, minimumSeconds)
            Next
        End Using
    End Sub

    Private Shared Sub CopyLegacyQuality(legacy As SqliteConnection, target As NpgsqlConnection, analyzer As String)
        Using command = legacy.CreateCommand()
            command.CommandTimeout = 3600
            command.CommandText =
                "SELECT substr(CapturedAtUtc, 1, 16), COUNT(*), SUM(CASE WHEN Quality='Good' THEN 1 ELSE 0 END), " &
                "MIN(CapturedAtUtc), MAX(CapturedAtUtc) FROM TransformerSamples WHERE TransformerId=$a GROUP BY 1"
            command.Parameters.AddWithValue("$a", analyzer)
            Using transaction = target.BeginTransaction()
                Using reader = command.ExecuteReader()
                    While reader.Read()
                        Dim bucket = ParseLegacyTime(reader.GetString(0) & ":00Z")
                        Dim total = reader.GetInt32(1)
                        Dim good = reader.GetInt32(2)
                        Using insert = target.CreateCommand()
                            insert.Transaction = transaction
                            insert.CommandText =
                                "INSERT INTO sample_quality_minute(analyzer_id, bucket_start, total_samples, good_samples, bad_samples, first_at, last_at) " &
                                "VALUES (@a, @b, @t, @g, @x, @f, @l) ON CONFLICT DO NOTHING"
                            insert.Parameters.AddWithValue("a", analyzer)
                            insert.Parameters.AddWithValue("b", bucket)
                            insert.Parameters.AddWithValue("t", total)
                            insert.Parameters.AddWithValue("g", good)
                            insert.Parameters.AddWithValue("x", total - good)
                            insert.Parameters.AddWithValue("f", ParseLegacyTime(reader.GetString(3)))
                            insert.Parameters.AddWithValue("l", ParseLegacyTime(reader.GetString(4)))
                            insert.ExecuteNonQuery()
                        End Using
                    End While
                End Using
                transaction.Commit()
            End Using
        End Using
    End Sub

    Private Shared Sub CopyLegacySamples(legacy As SqliteConnection, target As NpgsqlConnection, analyzer As String,
                                         minimumSeconds As Double)
        Using samples = legacy.CreateCommand()
            samples.CommandTimeout = 3600
            samples.CommandText =
                "SELECT SessionId, CycleId, CapturedAtUtc, SampleKind FROM TransformerSamples " &
                "WHERE TransformerId=$a AND Quality='Good' ORDER BY CapturedAtUtc"
            samples.Parameters.AddWithValue("$a", analyzer)
            Using valuesCommand = legacy.CreateCommand()
                valuesCommand.CommandText =
                    "SELECT ParameterIndex, Value FROM MeasurementValues WHERE SessionId=$s AND CycleId=$c AND TransformerId=$a"
                Dim sessionParameter = valuesCommand.Parameters.Add("$s", SqliteType.Text)
                Dim cycleParameter = valuesCommand.Parameters.Add("$c", SqliteType.Integer)
                valuesCommand.Parameters.AddWithValue("$a", analyzer)

                Dim transaction = target.BeginTransaction()
                Dim pending = 0
                Dim lastConsidered = DateTime.MinValue
                Dim lastStored As LastStored = Nothing
                Using reader = samples.ExecuteReader()
                    While reader.Read()
                        Dim capturedAt = ParseLegacyTime(reader.GetString(2))
                        If (capturedAt - lastConsidered).TotalSeconds < minimumSeconds Then Continue While
                        lastConsidered = capturedAt

                        Dim sessionText = reader.GetString(0)
                        sessionParameter.Value = sessionText
                        cycleParameter.Value = reader.GetInt64(1)
                        Dim values(ParameterCount - 1) As Single
                        For index = 0 To ParameterCount - 1
                            values(index) = Single.NaN
                        Next
                        Dim valueCount = 0
                        Using valueReader = valuesCommand.ExecuteReader()
                            While valueReader.Read()
                                Dim index = valueReader.GetInt32(0)
                                If index >= 0 AndAlso index < ParameterCount Then
                                    values(index) = CSng(valueReader.GetDouble(1))
                                    valueCount += 1
                                End If
                            End While
                        End Using
                        If valueCount = 0 Then Continue While
                        If lastStored IsNot Nothing AndAlso
                           (capturedAt - lastStored.AtUtc).TotalSeconds < HeartbeatSeconds AndAlso
                           Not HasMeaningfulChange(lastStored.Values, values) Then
                            Continue While
                        End If
                        Dim sessionId As Guid
                        If Not Guid.TryParseExact(sessionText, "N", sessionId) Then sessionId = Guid.Empty
                        InsertSample(target, transaction, analyzer, capturedAt, sessionId, reader.GetInt64(1),
                                     reader.GetString(3), values)
                        lastStored = New LastStored With {.AtUtc = capturedAt, .Values = values}
                        pending += 1
                        If pending >= 5000 Then
                            transaction.Commit()
                            transaction.Dispose()
                            transaction = target.BeginTransaction()
                            pending = 0
                        End If
                    End While
                End Using
                transaction.Commit()
                transaction.Dispose()
            End Using
        End Using
    End Sub

    Private Sub SetError(message As String)
        SyncLock _diagnosticsGate
            _lastError = message
        End SyncLock
    End Sub

    Public Async Function StopAsync() As Task
        If Interlocked.Exchange(_stopped, 1) <> 0 Then
            Await _writerTask.ConfigureAwait(False)
            Await _maintenance.ConfigureAwait(False)
            Return
        End If
        _queue.Writer.TryComplete()
        Await _writerTask.ConfigureAwait(False)
        Await _maintenance.ConfigureAwait(False)
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        Task.Run(Function() StopAsync()).GetAwaiter().GetResult()
    End Sub
End Class
