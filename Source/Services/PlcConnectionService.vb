Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Net.Sockets
Imports System.Threading
Imports System.Threading.Tasks

Public NotInheritable Class PlcConnectionService
    Implements IDisposable

    Private ReadOnly _pollGate As New SemaphoreSlim(1, 1)
    Private ReadOnly _lifecycleGate As New SemaphoreSlim(1, 1)
    Private ReadOnly _statusGate As New Object()
    Private _settings As PlcSettings
    Private _currentStatus As New PlcConnectionStatus()
    Private _snapshots As IReadOnlyList(Of MeterSnapshot) = Array.Empty(Of MeterSnapshot)()
    Private _pollLoop As Task
    Private _lifetime As CancellationTokenSource
    Private _disposed As Boolean
    Private _transactionId As Integer
    Private _acquisitionCycleId As Long
    Private _completedCycleCount As Long
    Private _successfulCycleCount As Long
    Private _successfulTransactions As Long
    Private _failedTransactions As Long
    Private _retryCount As Long
    Private _timeoutCount As Long
    Private ReadOnly _metricsGate As New Object()
    Private ReadOnly _recentCycleDurations As New Queue(Of Double)()
    Private ReadOnly _recentCycleCompletionTicks As New Queue(Of Long)()
    Private ReadOnly _recentSuccessfulCycleTicks As New Queue(Of Long)()
    Private _lastSuccessfulCycleCompletedUtc As DateTime?
    Private ReadOnly _powerTracker As New PowerEventTracker()
    Private ReadOnly _powerStore As PowerEventStore
    Private _powerLogError As String = String.Empty
    Private _lastPowerSaveUtc As DateTime?
    Private _powerRecordSignature As String = String.Empty

    Public Event StatusChanged(sender As Object, status As PlcConnectionStatus)
    Public Event ReadingsChanged(sender As Object, snapshots As IReadOnlyList(Of MeterSnapshot))

    Public Sub New(settings As PlcSettings, Optional powerEventDatabasePath As String = Nothing)
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        _settings = settings
        _powerStore = New PowerEventStore(powerEventDatabasePath)
    End Sub

    Public ReadOnly Property CurrentStatus As PlcConnectionStatus
        Get
            SyncLock _statusGate
                Return _currentStatus
            End SyncLock
        End Get
    End Property

    Public ReadOnly Property CurrentReadings As IReadOnlyList(Of MeterSnapshot)
        Get
            SyncLock _statusGate
                Return _snapshots
            End SyncLock
        End Get
    End Property

    Public Async Function StartAsync() As Task
        If _disposed Then Throw New ObjectDisposedException(NameOf(PlcConnectionService))
        Await _lifecycleGate.WaitAsync().ConfigureAwait(False)
        Try
            StartCore()
        Finally
            _lifecycleGate.Release()
        End Try
    End Function

    ' Start/stop/configure are serialized so a settings save racing a shutdown or restore cannot restart polling afterwards.
    Private Sub StartCore()
        If _disposed OrElse _pollLoop IsNot Nothing Then Return
        _lifetime = New CancellationTokenSource()
        Dim token = _lifetime.Token
        _pollLoop = Task.Run(Function() PollLoopAsync(token))
    End Sub

    Public Async Function PollNowAsync() As Task
        If _disposed Then Throw New ObjectDisposedException(NameOf(PlcConnectionService))
        Dim token = If(_lifetime Is Nothing, CancellationToken.None, _lifetime.Token)
        Await PollOnceAsync(token).ConfigureAwait(False)
    End Function

    Public Async Function StopAsync() As Task
        Await _lifecycleGate.WaitAsync().ConfigureAwait(False)
        Try
            Await StopCoreAsync().ConfigureAwait(False)
        Finally
            _lifecycleGate.Release()
        End Try
    End Function

    Private Async Function StopCoreAsync() As Task
        If _lifetime Is Nothing Then Return
        _lifetime.Cancel()
        If _pollLoop IsNot Nothing Then
            Try
                Await _pollLoop.ConfigureAwait(False)
            Catch ex As OperationCanceledException
            End Try
        End If
        _lifetime.Dispose()
        _lifetime = Nothing
        _pollLoop = Nothing
        _powerTracker.Interrupt("Monitoring stopped")
        PersistPowerEvents(DateTime.UtcNow, True)
        If _powerLogError.Length > 0 Then
            PublishStatus(New PlcConnectionStatus With {
                .Health = CurrentStatus.Health, .PowerLogError = _powerLogError,
                .Detail = "Power-event recording failed: " & _powerLogError
            })
        End If
    End Function

    Public Async Function ConfigureAsync(settings As PlcSettings) As Task
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        settings.Validate()
        Await _lifecycleGate.WaitAsync().ConfigureAwait(False)
        Try
        Await StopCoreAsync().ConfigureAwait(False)
        _settings = settings
        SyncLock _metricsGate
            ' Timing statistics from the previous interval would corrupt the new interval measurements.
            _recentCycleDurations.Clear()
            _recentCycleCompletionTicks.Clear()
            _recentSuccessfulCycleTicks.Clear()
        End SyncLock
        SyncLock _statusGate
            _snapshots = Array.Empty(Of MeterSnapshot)()
            _currentStatus = New PlcConnectionStatus With {
                .Health = PlcHealth.NotChecked,
                .Detail = "Settings saved; waiting for the next FC03 read.",
                .PollingIntervalSeconds = settings.PollingIntervalSeconds,
                .RequestedIntervalMilliseconds = settings.EffectivePollingIntervalMilliseconds
            }
        End SyncLock
        RaiseEvent ReadingsChanged(Me, _snapshots)
        RaiseEvent StatusChanged(Me, CurrentStatus)
        StartCore()
        Finally
            _lifecycleGate.Release()
        End Try
    End Function

    Private Async Function PollLoopAsync(cancellationToken As CancellationToken) As Task
        While Not cancellationToken.IsCancellationRequested
            Dim cycleTimer = Stopwatch.StartNew()
            Await PollOnceAsync(cancellationToken).ConfigureAwait(False)
            Dim remaining = TimeSpan.FromMilliseconds(_settings.EffectivePollingIntervalMilliseconds) - cycleTimer.Elapsed
            If remaining > TimeSpan.Zero Then Await Task.Delay(remaining, cancellationToken).ConfigureAwait(False)
        End While
    End Function

    Private Async Function PollOnceAsync(cancellationToken As CancellationToken) As Task
        Await _pollGate.WaitAsync(cancellationToken).ConfigureAwait(False)
        Try
            If Not String.IsNullOrWhiteSpace(_settings.ConfigurationError) Then
                PublishConfigurationError(_settings.ConfigurationError)
                Return
            End If
            Try
                _settings.Validate()
            Catch ex As InvalidDataException
                PublishConfigurationError(ex.Message)
                Return
            End Try

            ' Publishing "Connecting" every cycle made the status pill and diagnostics flicker; only announce it before the first result.
            If CurrentStatus.Health = PlcHealth.NotChecked Then
                PublishStatus(New PlcConnectionStatus With {
                    .Health = PlcHealth.Connecting,
                    .Detail = "Reading all four PM5110 blocks using Modbus TCP FC03.",
                    .PollingIntervalSeconds = _settings.PollingIntervalSeconds,
                    .RequestedIntervalMilliseconds = _settings.EffectivePollingIntervalMilliseconds,
                    .LastPollUtc = DateTime.UtcNow
                })
            End If

            Dim timer = Stopwatch.StartNew()
            Dim cycleId = Interlocked.Increment(_acquisitionCycleId)
            Dim results As New List(Of MeterSnapshot)(_settings.Blocks.Count)
            Dim logErrors As New List(Of String)()
            For Each block In _settings.Blocks
                cancellationToken.ThrowIfCancellationRequested()
                results.Add(Await ReadBlockWithRetryAsync(block, cycleId, cancellationToken, logErrors).ConfigureAwait(False))
            Next

            Dim captured = DateTime.UtcNow
            _powerTracker.Update(results, captured, TimeSpan.FromMilliseconds(Math.Max(5000,
                _settings.EffectivePollingIntervalMilliseconds * 2.0R + _settings.ReadTimeoutMilliseconds * 8.0R)))
            PersistPowerEvents(captured)
            Dim powerSummary = _powerTracker.Summary
            Dim successful = results.Where(Function(snapshot) snapshot.Quality = MeterQuality.Good).Count()
            Dim invalid = results.Where(Function(snapshot) snapshot.Quality = MeterQuality.BadQuality OrElse snapshot.Quality = MeterQuality.MappingError OrElse snapshot.Quality = MeterQuality.Timeout).Count()
            Dim health As PlcHealth
            Dim detail As String
            If successful = results.Count Then
                health = PlcHealth.Connected
                detail = "Online — all four FC03 reads succeeded."
            ElseIf successful > 0 Then
                health = PlcHealth.PartialCommunication
                detail = $"Partial communication — {successful} of {results.Count} blocks read; {invalid} block(s) have bad quality or mapping errors."
            ElseIf results.Any(Function(snapshot) snapshot.Quality = MeterQuality.MappingError) Then
                health = PlcHealth.MappingError
                detail = "Mapping error — one or more configured register ranges are invalid."
            ElseIf invalid > 0 Then
                health = If(results.Any(Function(snapshot) snapshot.Quality = MeterQuality.Timeout), PlcHealth.Timeout, PlcHealth.BadQuality)
                detail = If(health = PlcHealth.Timeout, "Timeout — PLC did not complete a read within the configured limit.", "Bad quality — Modbus returned data that failed frame or value validation.")
            Else
                health = PlcHealth.Unreachable
                detail = "Offline — no transformer block returned a valid FC03 read."
            End If
            If logErrors.Count > 0 Then detail &= " Read log error: " & String.Join(" | ", logErrors.Distinct())
            If powerSummary.Length > 0 Then detail = powerSummary & " — " &
                String.Join("; ", results.Select(Function(item) item.TransformerName & ": " & item.PowerDetail)) & " | " & detail
            If _powerLogError.Length > 0 Then detail &= " POWER EVENT LOG ERROR: " & _powerLogError
            timer.Stop()
            Dim diagnostics = RecordCycle(timer.Elapsed.TotalMilliseconds, successful = results.Count)
            PublishReadings(results.AsReadOnly())
            PublishStatus(New PlcConnectionStatus With {
                .PowerSummary = powerSummary,
                .PowerLogError = _powerLogError,
                .Health = health,
                .Detail = detail,
                .CheckedAtUtc = captured,
                .LastPollUtc = captured,
                .LatencyMilliseconds = timer.ElapsedMilliseconds,
                .PollingIntervalSeconds = _settings.PollingIntervalSeconds,
                .RequestedIntervalMilliseconds = _settings.EffectivePollingIntervalMilliseconds,
                .Diagnostics = diagnostics
            })
        Finally
            _pollGate.Release()
        End Try
    End Function

    Private Sub PersistPowerEvents(nowUtc As DateTime, Optional force As Boolean = False)
        Dim records = _powerTracker.PendingRecords()
        If Not force AndAlso records.Count = 0 AndAlso _lastPowerSaveUtc.HasValue AndAlso _powerLogError.Length = 0 Then Return
        Dim signature = String.Join("|", records.Select(Function(item) item.Id & If(item.EndUtc.HasValue, ":closed", ":open")).OrderBy(Function(item) item))
        If Not force AndAlso _powerLogError.Length = 0 AndAlso signature = _powerRecordSignature AndAlso
           _lastPowerSaveUtc.HasValue AndAlso nowUtc - _lastPowerSaveUtc.Value < TimeSpan.FromSeconds(1) Then Return
        Try
            _powerStore.Save(records)
            _powerTracker.AcceptPersisted()
            _lastPowerSaveUtc = nowUtc
            _powerRecordSignature = String.Join("|", records.Where(Function(item) Not item.EndUtc.HasValue).
                Select(Function(item) item.Id & ":open").OrderBy(Function(item) item))
            _powerLogError = String.Empty
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
                                  TypeOf ex Is Microsoft.Data.Sqlite.SqliteException OrElse TypeOf ex Is Security.SecurityException
            _powerLogError = ex.Message
        End Try
    End Sub

    Private Async Function ReadBlockWithRetryAsync(block As PlcBlock, cycleId As Long,
                                                    cancellationToken As CancellationToken,
                                                    logErrors As List(Of String)) As Task(Of MeterSnapshot)
        Dim lastSnapshot As MeterSnapshot = Nothing
        For attempt = 1 To 2
            If attempt > 1 Then Interlocked.Increment(_retryCount)
            Dim attemptStarted = DateTime.UtcNow
            Dim requestTimer = Stopwatch.StartNew()
            Dim effectiveStart = block.RegisterStart - _settings.RegisterAddressOffset
            Dim receivedRegisters As UShort() = Nothing
            Try
                receivedRegisters = Await ReadHoldingRegistersAsync(_settings, effectiveStart, _settings.RegisterCount, NextTransactionId(), cancellationToken).ConfigureAwait(False)
                Dim values = PlcModbus.DecodeRegisters(receivedRegisters, _settings.SwapFloatWords)
                requestTimer.Stop()
                Interlocked.Increment(_successfulTransactions)
                lastSnapshot = New MeterSnapshot With {
                    .TransformerName = block.TransformerName,
                    .RegisterStart = block.RegisterStart,
                    .EffectiveRegisterStart = effectiveStart,
                    .RegisterCount = _settings.RegisterCount,
                    .CapturedAtUtc = DateTime.UtcNow,
                    .AttemptStartedAtUtc = attemptStarted,
                    .AcquisitionCycleId = cycleId,
                    .AttemptCount = attempt,
                    .ResponseLatencyMilliseconds = requestTimer.Elapsed.TotalMilliseconds,
                    .Quality = MeterQuality.Good,
                    .CommunicationStatus = If(PlcModbus.IsUnavailableOffFrequency(values, 8),
                        "FC03 read succeeded; frequency unavailable (no voltage)", "FC03 read succeeded"),
                    .RawRegisters = receivedRegisters,
                    .DecodedValues = values
                }
                Try
                    PlcReadingsLog.AppendAttempt(lastSnapshot, attempt)
                Catch ex As IOException
                    logErrors.Add(ex.Message)
                Catch ex As UnauthorizedAccessException
                    logErrors.Add(ex.Message)
                End Try
                Return lastSnapshot
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                requestTimer.Stop()
                Dim cancelledAttempt = New MeterSnapshot With {
                    .TransformerName = block.TransformerName,
                    .RegisterStart = block.RegisterStart,
                    .EffectiveRegisterStart = effectiveStart,
                    .RegisterCount = _settings.RegisterCount,
                    .CapturedAtUtc = DateTime.UtcNow,
                    .AttemptStartedAtUtc = attemptStarted,
                    .AcquisitionCycleId = cycleId,
                    .AttemptCount = attempt,
                    .Quality = MeterQuality.Offline,
                    .CommunicationStatus = "Read cancelled",
                    .ErrorMessage = "Polling was cancelled before the FC03 response completed."
                }
                Try
                    PlcReadingsLog.AppendAttempt(cancelledAttempt, attempt)
                Catch logException As IOException
                    logErrors.Add(logException.Message)
                Catch logException As UnauthorizedAccessException
                    logErrors.Add(logException.Message)
                End Try
                Throw
            Catch ex As Exception When TypeOf ex Is ArgumentOutOfRangeException OrElse TypeOf ex Is InvalidDataException OrElse
                TypeOf ex Is IOException OrElse TypeOf ex Is SocketException OrElse TypeOf ex Is TimeoutException
                requestTimer.Stop()
                Interlocked.Increment(_failedTransactions)
                If TypeOf ex Is TimeoutException Then Interlocked.Increment(_timeoutCount)
                If TypeOf ex Is InvalidDataException AndAlso ex.Data.Contains("RawRegisters") Then
                    receivedRegisters = TryCast(ex.Data("RawRegisters"), UShort())
                End If
                Dim quality = If(TypeOf ex Is ArgumentOutOfRangeException, MeterQuality.MappingError,
                    If(TypeOf ex Is TimeoutException, MeterQuality.Timeout, If(TypeOf ex Is InvalidDataException, MeterQuality.BadQuality, MeterQuality.Offline)))
                lastSnapshot = New MeterSnapshot With {
                    .TransformerName = block.TransformerName,
                    .RegisterStart = block.RegisterStart,
                    .EffectiveRegisterStart = effectiveStart,
                    .RegisterCount = _settings.RegisterCount,
                    .CapturedAtUtc = DateTime.UtcNow,
                    .AttemptStartedAtUtc = attemptStarted,
                    .AcquisitionCycleId = cycleId,
                    .AttemptCount = attempt,
                    .ResponseLatencyMilliseconds = requestTimer.Elapsed.TotalMilliseconds,
                    .Quality = quality,
                    .CommunicationStatus = If(quality = MeterQuality.Offline, "Read failed", If(receivedRegisters Is Nothing, "Response validation failed", "FC03 response received; value validation failed")),
                    .RawRegisters = receivedRegisters,
                    .ErrorMessage = ex.Message
                }
                Try
                    PlcReadingsLog.AppendAttempt(lastSnapshot, attempt)
                Catch logException As IOException
                    logErrors.Add(logException.Message)
                Catch logException As UnauthorizedAccessException
                    logErrors.Add(logException.Message)
                End Try
                If (quality <> MeterQuality.Offline AndAlso quality <> MeterQuality.Timeout) OrElse attempt = 2 Then Return lastSnapshot
            End Try
        Next
        Return lastSnapshot
    End Function

    Private Function RecordCycle(durationMilliseconds As Double, fullySuccessful As Boolean) As AcquisitionDiagnosticsSnapshot
        Dim completedUtc = DateTime.UtcNow
        Dim nowTicks = Stopwatch.GetTimestamp()
        Dim completedCycles = Interlocked.Increment(_completedCycleCount)
        If fullySuccessful Then Interlocked.Increment(_successfulCycleCount)
        SyncLock _metricsGate
            _recentCycleDurations.Enqueue(durationMilliseconds)
            While _recentCycleDurations.Count > 128
                _recentCycleDurations.Dequeue()
            End While
            _recentCycleCompletionTicks.Enqueue(nowTicks)
            While _recentCycleCompletionTicks.Count > 1 AndAlso
                  Stopwatch.GetElapsedTime(_recentCycleCompletionTicks.Peek(), nowTicks) > TimeSpan.FromMinutes(1)
                _recentCycleCompletionTicks.Dequeue()
            End While
            If fullySuccessful Then
                _recentSuccessfulCycleTicks.Enqueue(nowTicks)
                While _recentSuccessfulCycleTicks.Count > 1 AndAlso
                      Stopwatch.GetElapsedTime(_recentSuccessfulCycleTicks.Peek(), nowTicks) > TimeSpan.FromMinutes(1)
                    _recentSuccessfulCycleTicks.Dequeue()
                End While
                _lastSuccessfulCycleCompletedUtc = completedUtc
            End If

            Dim sortedDurations = _recentCycleDurations.OrderBy(Function(value) value).ToArray()
            Dim rate As Double? = Nothing
            Dim averageCycleIntervalMilliseconds As Double? = Nothing
            If _recentCycleCompletionTicks.Count >= 2 Then
                Dim elapsed = Stopwatch.GetElapsedTime(_recentCycleCompletionTicks.Peek(),
                                                       _recentCycleCompletionTicks.Last()).TotalMilliseconds
                If elapsed > 0 Then
                    averageCycleIntervalMilliseconds = elapsed / (_recentCycleCompletionTicks.Count - 1)
                End If
            End If
            If _recentSuccessfulCycleTicks.Count >= 2 Then
                Dim elapsed = Stopwatch.GetElapsedTime(_recentSuccessfulCycleTicks.Peek(),
                                                       _recentSuccessfulCycleTicks.Last()).TotalSeconds
                If elapsed > 0 Then rate = (_recentSuccessfulCycleTicks.Count - 1) / elapsed
            End If
            Return New AcquisitionDiagnosticsSnapshot With {
                .CompletedCycles = completedCycles,
                .SuccessfulCycles = Interlocked.Read(_successfulCycleCount),
                .FailedCycles = completedCycles - Interlocked.Read(_successfulCycleCount),
                .SuccessfulTransactions = Interlocked.Read(_successfulTransactions),
                .FailedTransactions = Interlocked.Read(_failedTransactions),
                .RetryCount = Interlocked.Read(_retryCount),
                .TimeoutCount = Interlocked.Read(_timeoutCount),
                .LastCycleDurationMilliseconds = durationMilliseconds,
                .AverageCycleDurationMilliseconds = sortedDurations.Average(),
                .P95CycleDurationMilliseconds = sortedDurations(Math.Max(0, CInt(Math.Ceiling(sortedDurations.Length * 0.95R)) - 1)),
                .MaximumCycleDurationMilliseconds = sortedDurations.Last(),
                .AverageCycleIntervalMilliseconds = averageCycleIntervalMilliseconds,
                .SuccessfulCycleRateHz = rate,
                .LastCycleCompletedUtc = completedUtc,
                .LastSuccessfulCycleCompletedUtc = _lastSuccessfulCycleCompletedUtc,
                .SourceFreshnessAvailable = False
            }
        End SyncLock
    End Function

    Private Function NextTransactionId() As UShort
        Return CUShort(Interlocked.Increment(_transactionId) And &HFFFF)
    End Function

    Private Sub PublishConfigurationError(message As String)
        Dim snapshots As New List(Of MeterSnapshot)()
        Dim defaults = New PlcSettings().Blocks
        For index = 0 To 3
            Dim block = If(_settings.Blocks IsNot Nothing AndAlso index < _settings.Blocks.Count, _settings.Blocks(index), defaults(index))
            If block Is Nothing Then block = defaults(index)
            snapshots.Add(New MeterSnapshot With {
                .TransformerName = block.TransformerName,
                .RegisterStart = block.RegisterStart,
                .EffectiveRegisterStart = block.RegisterStart - _settings.RegisterAddressOffset,
                .Quality = MeterQuality.MappingError,
                .CommunicationStatus = "Mapping error",
                .ErrorMessage = message
            })
        Next
        PublishReadings(snapshots.AsReadOnly())
        PublishStatus(New PlcConnectionStatus With {
            .Health = PlcHealth.MappingError,
            .Detail = "Mapping error — " & message,
            .PollingIntervalSeconds = _settings.PollingIntervalSeconds,
            .RequestedIntervalMilliseconds = _settings.EffectivePollingIntervalMilliseconds
        })
    End Sub

    Private Sub PublishReadings(snapshots As IReadOnlyList(Of MeterSnapshot))
        SyncLock _statusGate
            _snapshots = snapshots
        End SyncLock
        RaiseEvent ReadingsChanged(Me, snapshots)
    End Sub

    Private Sub PublishStatus(status As PlcConnectionStatus)
        SyncLock _statusGate
            _currentStatus = status
        End SyncLock
        RaiseEvent StatusChanged(Me, status)
    End Sub

    Public Shared Async Function ReadHoldingRegistersAsync(settings As PlcSettings, startRegister As Integer, count As Integer, transactionId As UShort, cancellationToken As CancellationToken) As Task(Of UShort())
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        If startRegister < 0 OrElse startRegister > 65535 OrElse count < 1 OrElse count > 125 OrElse startRegister + count > 65536 Then
            Throw New ArgumentOutOfRangeException(NameOf(startRegister), "The FC03 register range is outside the Modbus address space.")
        End If

        Using timeout As New CancellationTokenSource(settings.ReadTimeoutMilliseconds)
            Using linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token)
                Using client As New TcpClient()
                    Try
                        Await client.ConnectAsync(settings.Host, settings.Port, linked.Token).ConfigureAwait(False)
                        Using stream = client.GetStream()
                            Dim request As Byte() = {
                                CByte((transactionId >> 8) And &HFF), CByte(transactionId And &HFF),
                                0, 0, 0, 6, CByte(settings.UnitId), 3,
                                CByte((startRegister >> 8) And &HFF), CByte(startRegister And &HFF),
                                CByte((count >> 8) And &HFF), CByte(count And &HFF)
                            }
                            Await stream.WriteAsync(request, linked.Token).ConfigureAwait(False)
                            Dim header(6) As Byte
                            Await ReadExactlyAsync(stream, header, linked.Token).ConfigureAwait(False)
                            Dim responseTransaction = CUInt(header(0)) * 256UI + header(1)
                            Dim responseLength = CInt(header(4)) * 256 + CInt(header(5))
                            If responseTransaction <> transactionId OrElse header(2) <> 0 OrElse header(3) <> 0 OrElse
                               header(6) <> CByte(settings.UnitId) OrElse responseLength < 3 OrElse responseLength > 253 Then
                                Throw New InvalidDataException("Invalid Modbus TCP response header.")
                            End If

                            Dim pdu(responseLength - 2) As Byte
                            Await ReadExactlyAsync(stream, pdu, linked.Token).ConfigureAwait(False)
                            If pdu(0) = &H83 Then
                                If pdu.Length < 2 Then Throw New InvalidDataException("Truncated Modbus exception response.")
                                Throw New InvalidDataException("Modbus exception response code " & pdu(1).ToString(Globalization.CultureInfo.InvariantCulture) & ".")
                            End If
                            If pdu(0) <> 3 OrElse pdu.Length < 2 OrElse pdu(1) <> count * 2 OrElse pdu.Length <> count * 2 + 2 Then
                                Dim invalidResponse As New InvalidDataException("FC03 returned an unexpected function code or register count.")
                                If pdu.Length >= 2 AndAlso pdu(0) = 3 Then invalidResponse.Data("RawRegisters") = ParseAvailableRegisters(pdu)
                                Throw invalidResponse
                            End If

                            Dim registers(count - 1) As UShort
                            For index = 0 To count - 1
                                registers(index) = CUShort(CInt(pdu(2 + index * 2)) * 256 + CInt(pdu(3 + index * 2)))
                            Next
                            Return registers
                        End Using
                    Catch ex As OperationCanceledException When timeout.IsCancellationRequested AndAlso Not cancellationToken.IsCancellationRequested
                        Throw New TimeoutException("Modbus TCP connect/read timed out after " & settings.ReadTimeoutMilliseconds.ToString(Globalization.CultureInfo.InvariantCulture) & " ms.")
                    End Try
                End Using
            End Using
        End Using
    End Function

    Private Shared Async Function ReadExactlyAsync(stream As NetworkStream, buffer As Byte(), cancellationToken As CancellationToken) As Task
        Dim offset = 0
        While offset < buffer.Length
            Dim received = Await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken).ConfigureAwait(False)
            If received = 0 Then Throw New EndOfStreamException("PLC disconnected before the complete FC03 response arrived.")
            offset += received
        End While
    End Function

    Private Shared Function ParseAvailableRegisters(pdu As Byte()) As UShort()
        Dim byteCount = CInt(pdu(1))
        If byteCount Mod 2 <> 0 OrElse byteCount > pdu.Length - 2 Then Return Array.Empty(Of UShort)()
        If byteCount = 0 Then Return Array.Empty(Of UShort)()
        Dim registers(byteCount \ 2 - 1) As UShort
        For index = 0 To registers.Length - 1
            registers(index) = CUShort(CInt(pdu(2 + index * 2)) * 256 + CInt(pdu(3 + index * 2)))
        Next
        Return registers
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        If _disposed Then Return
        _disposed = True
        If _lifetime IsNot Nothing Then _lifetime.Cancel()
    End Sub
End Class
