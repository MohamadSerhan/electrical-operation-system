Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Security
Imports System.Text
Imports System.Threading
Imports System.Threading.Channels

Public NotInheritable Class PlcReadingsLog
    Private NotInheritable Class LogRecord
        Public Property TimestampUtc As DateTime
        Public Property TransformerName As String = String.Empty
        Public Property RegisterStart As Integer
        Public Property EffectiveRegisterStart As Integer
        Public Property RegisterCount As Integer
        Public Property Attempt As Integer
        Public Property CommunicationStatus As String = String.Empty
        Public Property Quality As MeterQuality
        Public Property RawRegisters As UShort() = Array.Empty(Of UShort)()
        Public Property DecodedValues As Single()
        Public Property ErrorMessage As String = String.Empty
    End Class

    Private Shared ReadOnly Gate As New Object()
    Private Shared ReadOnly DiagnosticsGate As New Object()
    Private Shared ReadOnly Queue As Channel(Of LogRecord) =
        Channel.CreateBounded(Of LogRecord)(New BoundedChannelOptions(2048) With {
            .FullMode = BoundedChannelFullMode.Wait,
            .SingleReader = True,
            .SingleWriter = False,
            .AllowSynchronousContinuations = False
        })
    Private Shared ReadOnly Worker As Task = Task.Run(Function() WriterLoopAsync())
    Private Shared _droppedAttempts As Long
    Private Shared _writeFailures As Long
    Private Shared _lastWriteLatencyMilliseconds As Double?
    Private Shared _lastWriteUtc As DateTime?
    Private Shared _lastError As String = String.Empty
    Private Shared _stopped As Integer
    Private Const RetentionDays As Integer = 7
    Private Const GoodSampleLogSeconds As Double = 60
    Private Shared ReadOnly LastGoodLogged As New Dictionary(Of String, DateTime)(StringComparer.OrdinalIgnoreCase)
    Private Shared ReadOnly Purger As Task = Task.Run(Sub() PurgeOldLogs())

    Private Sub New()
    End Sub

    ' Raw attempt logs are diagnostic only: measurements live in PostgreSQL, so successful first-attempt reads are
    ' not logged here (no second copy). Failures and retries are kept; files older than a week are dropped.
    Private Shared Function ShouldLogGood(transformer As String, timestamp As DateTime) As Boolean
        SyncLock LastGoodLogged
            Dim last As DateTime
            If LastGoodLogged.TryGetValue(transformer, last) AndAlso (timestamp - last).TotalSeconds < GoodSampleLogSeconds Then Return False
            LastGoodLogged(transformer) = timestamp
            Return True
        End SyncLock
    End Function

    Private Shared Sub PurgeOldLogs()
        Try
            Dim directory = DirectoryPath()
            If Not IO.Directory.Exists(directory) Then Return
            Dim cutoff = DateTime.UtcNow.AddDays(-RetentionDays)
            For Each file In IO.Directory.EnumerateFiles(directory, "PM5110_*.csv")
                If IO.File.GetLastWriteTimeUtc(file) < cutoff Then IO.File.Delete(file)
            Next
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
        End Try
    End Sub

    Public Shared Function DirectoryPath() As String
        Return Path.Combine(AppContext.BaseDirectory, "data", "MeterLogs")
    End Function

    Public Shared Sub AppendAttempt(snapshot As MeterSnapshot, attempt As Integer)
        If snapshot Is Nothing Then Throw New ArgumentNullException(NameOf(snapshot))
        If attempt < 1 Then Throw New ArgumentOutOfRangeException(NameOf(attempt))
        If Volatile.Read(_stopped) <> 0 Then
            Interlocked.Increment(_droppedAttempts)
            SetError("PLC readings logger is stopped; an attempt record was not queued.")
            Return
        End If
        Dim timestamp = If(snapshot.CapturedAtUtc, DateTime.UtcNow)
        If snapshot.Quality = MeterQuality.Good AndAlso attempt = 1 Then Return
        Dim record As New LogRecord With {
            .TimestampUtc = timestamp,
            .TransformerName = snapshot.TransformerName,
            .RegisterStart = snapshot.RegisterStart,
            .EffectiveRegisterStart = snapshot.EffectiveRegisterStart,
            .RegisterCount = snapshot.RegisterCount,
            .Attempt = attempt,
            .CommunicationStatus = snapshot.CommunicationStatus,
            .Quality = snapshot.Quality,
            .RawRegisters = If(snapshot.RawRegisters Is Nothing, Array.Empty(Of UShort)(), DirectCast(snapshot.RawRegisters.Clone(), UShort())),
            .DecodedValues = If(snapshot.DecodedValues Is Nothing, Nothing, DirectCast(snapshot.DecodedValues.Clone(), Single())),
            .ErrorMessage = snapshot.ErrorMessage
        }
        If Queue.Writer.TryWrite(record) Then Return
        Interlocked.Increment(_droppedAttempts)
        SetError("PLC readings log queue is full; an attempt record was dropped.")
    End Sub

    Public Shared Function GetDiagnostics() As OperationalLogDiagnosticsSnapshot
        Dim lastLatency As Double?
        Dim lastWrite As DateTime?
        Dim lastError As String
        SyncLock DiagnosticsGate
            lastLatency = _lastWriteLatencyMilliseconds
            lastWrite = _lastWriteUtc
            lastError = _lastError
        End SyncLock
        Return New OperationalLogDiagnosticsSnapshot With {
            .QueueDepth = If(Queue.Reader.CanCount, Queue.Reader.Count, -1),
            .DroppedAttempts = Interlocked.Read(_droppedAttempts),
            .WriteFailures = Interlocked.Read(_writeFailures),
            .LastWriteLatencyMilliseconds = lastLatency,
            .LastWriteUtc = lastWrite,
            .LastError = lastError
        }
    End Function

    Private Shared Async Function WriterLoopAsync() As Task
        While Await Queue.Reader.WaitToReadAsync().ConfigureAwait(False)
            Dim batch As New List(Of LogRecord)(64)
            Dim record As LogRecord = Nothing
            While batch.Count < 64 AndAlso Queue.Reader.TryRead(record)
                batch.Add(record)
            End While
            Await WriteWithRetryAsync(batch).ConfigureAwait(False)
        End While
    End Function

    Private Shared Async Function WriteWithRetryAsync(batch As IReadOnlyList(Of LogRecord)) As Task
        For attempt = 1 To 5
            Dim timer = Stopwatch.StartNew()
            Try
                WriteBatch(batch)
                timer.Stop()
                SyncLock DiagnosticsGate
                    _lastWriteLatencyMilliseconds = timer.Elapsed.TotalMilliseconds
                    _lastWriteUtc = DateTime.UtcNow
                    _lastError = String.Empty
                End SyncLock
                Return
            Catch ex As IOException
                timer.Stop()
                Interlocked.Increment(_writeFailures)
                SetError("PLC readings log I/O failed: " & ex.Message)
            Catch ex As UnauthorizedAccessException
                timer.Stop()
                Interlocked.Increment(_writeFailures)
                SetError("PLC readings log access failed: " & ex.Message)
            Catch ex As SecurityException
                timer.Stop()
                Interlocked.Increment(_writeFailures)
                SetError("PLC readings log security failure: " & ex.Message)
            End Try
            If attempt < 5 Then
                Await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1))).ConfigureAwait(False)
            End If
        Next
        Interlocked.Add(_droppedAttempts, batch.Count)
        SetError("PLC readings logger dropped " &
                 batch.Count.ToString(CultureInfo.InvariantCulture) &
                 " attempt record(s) after repeated storage failures.")
    End Function

    Private Shared Sub WriteBatch(batch As IReadOnlyList(Of LogRecord))
        For Each group In batch.GroupBy(Function(record) GetLogFilePath(record.TimestampUtc), StringComparer.OrdinalIgnoreCase)
            Dim logFilePath = group.Key
            IO.Directory.CreateDirectory(Path.GetDirectoryName(logFilePath))
            SyncLock Gate
                Dim isNew = Not File.Exists(logFilePath)
                Using writer As New StreamWriter(logFilePath, True, New UTF8Encoding(False))
                    If isNew Then
                        writer.Write("TimestampUtc,Transformer,RegisterStart,EffectiveRegisterStart,RegisterCount,Attempt,CommunicationStatus,DataQuality,RawRegisters")
                        For Each field In PlcModbus.FieldNames
                            writer.Write("," & Quote(field))
                        Next
                        writer.WriteLine(",ErrorMessage")
                    End If
                    For Each record In group
                        WriteRecord(writer, record)
                    Next
                End Using
            End SyncLock
        Next
    End Sub

    Private Shared Function GetLogFilePath(timestamp As DateTime) As String
        Return Path.Combine(DirectoryPath(), "PM5110_" &
                            timestamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture) & ".csv")
    End Function

    Private Shared Sub WriteRecord(writer As StreamWriter, record As LogRecord)
        writer.Write(String.Join(",", {
            record.TimestampUtc.ToString("o", CultureInfo.InvariantCulture),
            Quote(record.TransformerName),
            record.RegisterStart.ToString(CultureInfo.InvariantCulture),
            record.EffectiveRegisterStart.ToString(CultureInfo.InvariantCulture),
            record.RegisterCount.ToString(CultureInfo.InvariantCulture),
            record.Attempt.ToString(CultureInfo.InvariantCulture),
            Quote(record.CommunicationStatus),
            record.Quality.ToString(),
            Quote(String.Join(";", record.RawRegisters.Select(Function(value) value.ToString(CultureInfo.InvariantCulture))))
        }))
        If record.DecodedValues IsNot Nothing AndAlso record.DecodedValues.Length = PlcModbus.FieldNames.Length Then
            For Each value In record.DecodedValues
                writer.Write("," & value.ToString("R", CultureInfo.InvariantCulture))
            Next
        Else
            For Each field In PlcModbus.FieldNames
                writer.Write(",")
            Next
        End If
        writer.WriteLine("," & Quote(record.ErrorMessage))
    End Sub

    Private Shared Function Quote(value As String) As String
        Return """" & If(value, String.Empty).Replace("""", """""") & """"
    End Function

    Private Shared Sub SetError(message As String)
        SyncLock DiagnosticsGate
            _lastError = message
        End SyncLock
    End Sub

    Public Shared Async Function StopAsync() As Task
        If Interlocked.Exchange(_stopped, 1) = 0 Then Queue.Writer.TryComplete()
        Await Worker.ConfigureAwait(False)
    End Function
End Class
