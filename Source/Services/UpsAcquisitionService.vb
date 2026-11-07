Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Globalization
Imports System.Linq
Imports System.Net
Imports System.Threading
Imports System.Threading.Tasks
Imports Lextm.SharpSnmpLib
Imports Lextm.SharpSnmpLib.Messaging
Imports Lextm.SharpSnmpLib.Security

Friend NotInheritable Class UpsSnmpFailure
    Inherits Exception

    Public ReadOnly Property Quality As UpsQuality
    ' SNMP error-status from a response PDU (1 tooBig, 2 noSuchName, 5 genErr …); 0 when the failure is not an error response.
    Public ReadOnly Property ErrorStatus As Integer

    Public Sub New(quality As UpsQuality, message As String, Optional errorStatus As Integer = 0)
        MyBase.New(message)
        Me.Quality = quality
        Me.ErrorStatus = errorStatus
    End Sub

    Public ReadOnly Property IsErrorResponse As Boolean
        Get
            Return ErrorStatus > 0
        End Get
    End Property
End Class

' Read-only SNMP GET client (v1 / v2c / v3 USM). This module never issues SET requests.
Friend Module UpsSnmpClient
    Private NotInheritable Class RequestGate
        Public CompletedTicks As Long
    End Class
    Private ReadOnly RequestGates As New ConcurrentDictionary(Of String, RequestGate)(StringComparer.Ordinal)
    Friend Const MinimumRequestGapMilliseconds As Integer = 25
    Private ReadOnly UsmReportReasons As New Dictionary(Of String, String) From {
        {"1.3.6.1.6.3.15.1.1.1.0", "unsupported security level"},
        {"1.3.6.1.6.3.15.1.1.2.0", "not in time window"},
        {"1.3.6.1.6.3.15.1.1.3.0", "unknown user name"},
        {"1.3.6.1.6.3.15.1.1.4.0", "unknown engine ID"},
        {"1.3.6.1.6.3.15.1.1.5.0", "wrong authentication digest (password/protocol)"},
        {"1.3.6.1.6.3.15.1.1.6.0", "decryption error (privacy password/protocol)"}}

    Public Function GetValues(endpoint As IPEndPoint, credential As SnmpCredential, oids As IList(Of String),
                              timeoutMs As Integer) As IList(Of Variable)
        Return Execute(endpoint, credential, oids, timeoutMs, False)
    End Function

    ' GETNEXT (read-only) – used only to verify which table rows (phase indices) a card really implements.
    Public Function GetNextValues(endpoint As IPEndPoint, credential As SnmpCredential, oids As IList(Of String),
                                  timeoutMs As Integer) As IList(Of Variable)
        Return Execute(endpoint, credential, oids, timeoutMs, True)
    End Function

    Public Function ErrorStatusName(status As Integer) As String
        Select Case status
            Case 1 : Return "tooBig"
            Case 2 : Return "noSuchName"
            Case 3 : Return "badValue"
            Case 4 : Return "readOnly"
            Case 5 : Return "genErr"
            Case 6 : Return "noAccess"
            Case 16 : Return "authorizationError"
            Case Else : Return "error " & status.ToString(CultureInfo.InvariantCulture)
        End Select
    End Function

    Private Function Execute(endpoint As IPEndPoint, credential As SnmpCredential, oids As IList(Of String),
                             timeoutMs As Integer, nextRequest As Boolean) As IList(Of Variable)
        Dim elapsedRequest = Stopwatch.StartNew()
        Dim gate = RequestGates.GetOrAdd(endpoint.ToString(), Function(key) New RequestGate())
        If Not Monitor.TryEnter(gate, Math.Max(1, timeoutMs)) Then
            Throw New UpsSnmpFailure(UpsQuality.Timeout, "SNMP request queue timeout: another read is still using this endpoint; no new request was sent.")
        End If
        Try
            If gate.CompletedTicks > 0 Then
                Dim elapsed = (Stopwatch.GetTimestamp() - gate.CompletedTicks) * 1000.0R / Stopwatch.Frequency
                If elapsed < MinimumRequestGapMilliseconds Then Thread.Sleep(CInt(Math.Ceiling(MinimumRequestGapMilliseconds - elapsed)))
            End If
            Dim remaining = timeoutMs - CInt(elapsedRequest.ElapsedMilliseconds)
            If remaining <= 0 Then Throw New UpsSnmpFailure(UpsQuality.Timeout, "SNMP request budget expired while waiting for this endpoint.")
            Return ExecuteCore(endpoint, credential, oids, remaining, nextRequest)
        Finally
            gate.CompletedTicks = Stopwatch.GetTimestamp()
            Monitor.Exit(gate)
        End Try
    End Function

    Private Function ExecuteCore(endpoint As IPEndPoint, credential As SnmpCredential, oids As IList(Of String),
                                 timeoutMs As Integer, nextRequest As Boolean) As IList(Of Variable)
        Dim variables = oids.Select(Function(oid) New Variable(New ObjectIdentifier(oid))).ToList()
        Try
            Select Case credential.Version
                Case "v1", "v2c"
                    If String.IsNullOrEmpty(credential.Community) Then Throw New UpsSnmpFailure(UpsQuality.ConfigError, "Credential profile has no community string.")
                    Dim version = If(credential.Version = "v1", VersionCode.V1, VersionCode.V2)
                    If Not nextRequest Then Return Messenger.Get(version, endpoint, New OctetString(credential.Community), variables, timeoutMs)
                    Dim request As New GetNextRequestMessage(Messenger.NextRequestId, version, New OctetString(credential.Community), variables)
                    Return CheckPdu(request.GetResponse(timeoutMs, endpoint).Pdu())
                Case "v3"
                    Return GetV3(endpoint, credential, variables, timeoutMs, nextRequest)
                Case Else
                    Throw New UpsSnmpFailure(UpsQuality.ConfigError, "Unsupported SNMP version '" & credential.Version & "'.")
            End Select
        Catch ex As ErrorException
            Dim status = 0
            Try
                status = ex.Body.Pdu().ErrorStatus.ToInt32()
            Catch inner As Exception When TypeOf inner Is NullReferenceException OrElse TypeOf inner Is InvalidCastException OrElse TypeOf inner Is SnmpException
            End Try
            Throw New UpsSnmpFailure(UpsQuality.Invalid, "SNMP error response: " & ErrorStatusName(Math.Max(1, status)), Math.Max(1, status))
        Catch ex As Lextm.SharpSnmpLib.Messaging.TimeoutException
            ' Factual only: a timeout alone does not tell whether the cause is the network, the agent, the ACL or the credential.
            Throw New UpsSnmpFailure(UpsQuality.Timeout,
                "SNMP GET timed out after " & timeoutMs.ToString(CultureInfo.InvariantCulture) & " ms. Cause not yet determined.")
        Catch ex As DecryptionException
            Throw New UpsSnmpFailure(UpsQuality.AuthFailure, "SNMPv3 decryption failed (privacy password/protocol).")
        Catch ex As Net.Sockets.SocketException When ex.SocketErrorCode = Net.Sockets.SocketError.ConnectionReset
            Throw New UpsSnmpFailure(UpsQuality.Offline, "ICMP port unreachable: no SNMP agent is listening on UDP " &
                                     endpoint.Port.ToString(CultureInfo.InvariantCulture) & ".")
        Catch ex As Net.Sockets.SocketException
            Throw New UpsSnmpFailure(UpsQuality.Offline, "Network error: " & ex.SocketErrorCode.ToString())
        End Try
    End Function

    ' MD5/SHA1/DES are offered only because older IntelliSlot cards support nothing stronger; the administrator chooses.
#Disable Warning BC40000
    Private Function GetV3(endpoint As IPEndPoint, credential As SnmpCredential, variables As IList(Of Variable),
                           timeoutMs As Integer, nextRequest As Boolean) As IList(Of Variable)
        Dim budget = Stopwatch.StartNew()
        Dim remaining = Function() As Integer
                            Dim milliseconds = timeoutMs - CInt(budget.ElapsedMilliseconds)
                            If milliseconds <= 0 Then Throw New UpsSnmpFailure(UpsQuality.Timeout, "SNMPv3 discovery/request exceeded its time budget.")
                            Return milliseconds
                        End Function
        Dim authentication As IAuthenticationProvider
        Select Case credential.AuthProtocol
            Case "MD5" : authentication = New MD5AuthenticationProvider(New OctetString(credential.AuthSecret))
            Case "SHA1" : authentication = New SHA1AuthenticationProvider(New OctetString(credential.AuthSecret))
            Case "SHA256" : authentication = New SHA256AuthenticationProvider(New OctetString(credential.AuthSecret))
            Case "SHA384" : authentication = New SHA384AuthenticationProvider(New OctetString(credential.AuthSecret))
            Case "SHA512" : authentication = New SHA512AuthenticationProvider(New OctetString(credential.AuthSecret))
            Case "None" : authentication = DefaultAuthenticationProvider.Instance
            Case Else : Throw New UpsSnmpFailure(UpsQuality.ConfigError, "Unsupported SNMPv3 authentication protocol.")
        End Select
        Dim privacy As IPrivacyProvider
        Select Case credential.PrivProtocol
            Case "DES" : privacy = New DESPrivacyProvider(New OctetString(credential.PrivSecret), authentication)
            Case "AES128" : privacy = New AESPrivacyProvider(New OctetString(credential.PrivSecret), authentication)
            Case "AES192" : privacy = New AES192PrivacyProvider(New OctetString(credential.PrivSecret), authentication)
            Case "AES256" : privacy = New AES256PrivacyProvider(New OctetString(credential.PrivSecret), authentication)
            Case "None" : privacy = New DefaultPrivacyProvider(authentication)
            Case Else : Throw New UpsSnmpFailure(UpsQuality.ConfigError, "Unsupported SNMPv3 privacy protocol.")
        End Select
        Dim discovery = Messenger.GetNextDiscovery(SnmpType.GetRequestPdu)
        Dim report = discovery.GetResponse(remaining(), endpoint)
        Dim user = New OctetString(credential.UserName)
        Dim request As ISnmpMessage
        If nextRequest Then
            request = New GetNextRequestMessage(VersionCode.V3, Messenger.NextMessageId, Messenger.NextRequestId, user, variables, privacy, Messenger.MaxMessageSize, report)
        Else
            request = New GetRequestMessage(VersionCode.V3, Messenger.NextMessageId, Messenger.NextRequestId, user, variables, privacy, Messenger.MaxMessageSize, report)
        End If
        Dim reply = request.GetResponse(remaining(), endpoint)
        Dim pdu = reply.Pdu()
        If pdu.TypeCode = SnmpType.ReportPdu Then
            Dim reason = "SNMPv3 report"
            If pdu.Variables.Count > 0 Then
                Dim text As String = Nothing
                If UsmReportReasons.TryGetValue(pdu.Variables(0).Id.ToString(), text) Then reason = "SNMPv3: " & text
            End If
            Dim reportOid = If(pdu.Variables.Count > 0, pdu.Variables(0).Id.ToString(), "")
            Dim credentialError = {"1.3.6.1.6.3.15.1.1.3.0", "1.3.6.1.6.3.15.1.1.5.0", "1.3.6.1.6.3.15.1.1.6.0"}.Contains(reportOid)
            Throw New UpsSnmpFailure(If(credentialError, UpsQuality.AuthFailure, UpsQuality.Invalid),
                                    reason & " (agent USM report; not proof of an authenticated identity)")
        End If
        Return CheckPdu(pdu)
    End Function
#Enable Warning BC40000

    Private Function CheckPdu(pdu As ISnmpPdu) As IList(Of Variable)
        Dim status = pdu.ErrorStatus.ToInt32()
        If status <> 0 Then Throw New UpsSnmpFailure(UpsQuality.Invalid, "SNMP error response: " & ErrorStatusName(status), status)
        Return pdu.Variables
    End Function

    Public Function IsMissing(data As ISnmpData) As Boolean
        If data Is Nothing Then Return True
        Return data.TypeCode = SnmpType.NoSuchObject OrElse data.TypeCode = SnmpType.NoSuchInstance OrElse
               data.TypeCode = SnmpType.EndOfMibView OrElse data.TypeCode = SnmpType.Null
    End Function

    Public Function ToNumber(data As ISnmpData) As Double?
        If IsMissing(data) Then Return Nothing
        Select Case data.TypeCode
            Case SnmpType.Integer32 : Return DirectCast(data, Integer32).ToInt32()
            Case SnmpType.Gauge32 : Return DirectCast(data, Gauge32).ToUInt32()
            Case SnmpType.Counter32 : Return DirectCast(data, Counter32).ToUInt32()
            Case SnmpType.TimeTicks : Return DirectCast(data, TimeTicks).ToUInt32()
            Case Else
                Dim parsed As Double
                If Double.TryParse(data.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, parsed) Then Return parsed
                Return Nothing
        End Select
    End Function

    Public Function ToText(data As ISnmpData) As String
        If IsMissing(data) Then Return String.Empty
        If data.TypeCode = SnmpType.TimeTicks Then
            Dim span = DirectCast(data, TimeTicks).ToTimeSpan()
            Return CInt(Math.Floor(span.TotalDays)).ToString(CultureInfo.InvariantCulture) & "d " & span.ToString("hh\:mm\:ss", CultureInfo.InvariantCulture)
        End If
        Return data.ToString().Trim()
    End Function
End Module

Public NotInheritable Class UpsProbeResult
    Public Property IpAddress As String = String.Empty
    Public Property Responds As Boolean
    Public Property Quality As UpsQuality
    Public Property Detail As String = String.Empty
    Public Property RoundTripMilliseconds As Double?
    Public Property SysName As String = String.Empty
    Public Property SysDescr As String = String.Empty
    Public Property SysObjectId As String = String.Empty
    Public Property Manufacturer As String = String.Empty
    Public Property Model As String = String.Empty
    Public Property SupportsUpsMib As Boolean
    ' Version of the request that actually answered (the device's configured profile); empty when nothing answered.
    Public Property ProtocolVersion As String = String.Empty
End Class

Public NotInheritable Class UpsAcquisitionDiagnostics
    Public Property Running As Boolean
    Public Property PollsSucceeded As Long
    Public Property PollsFailed As Long
    Public Property LastPollUtc As DateTime?
    Public Property InFlight As Integer
    Public Property MaxConcurrency As Integer
End Class

' Polls every enabled UPS on a background task. Bounded concurrency, per-device schedule with stagger and
' automatic recovery retries for offline devices. Independent of the PLC service and never blocks the UI thread.
Public NotInheritable Class UpsAcquisitionService
    Implements IDisposable

    Private NotInheritable Class DeviceState
        Public NextDueUtc As DateTime
        Public Failures As Integer
        Public InFlight As Boolean
        Public LastGood As UpsSnapshot
        Public LastFailureReason As String = String.Empty
        Public LastFailureUtc As DateTime?
    End Class

    Private ReadOnly _snapshots As New ConcurrentDictionary(Of String, UpsSnapshot)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _states As New Dictionary(Of String, DeviceState)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _stateGate As New Object()
    Private _cancellation As CancellationTokenSource
    Private _loop As Task
    Private _semaphore As SemaphoreSlim
    Private _semaphoreSize As Integer
    Private _version As Long
    Private _pollsOk As Long
    Private _pollsFailed As Long
    Private _inFlight As Integer
    Private _lastPollTicks As Long
    Private ReadOnly _analyzerSnapshots As New ConcurrentDictionary(Of String, UpsSnapshot)(StringComparer.OrdinalIgnoreCase)
    Private Const MaximumAnalyzerPendingSamples As Integer = 10000
    Private ReadOnly _analyzerPending As New Queue(Of UpsSnapshot)()
    Private _analyzerDropped As Long
    Private ReadOnly _analyzerGate As New Object()
    Private _analyzerCancellation As CancellationTokenSource
    Private _analyzerTasks As Task = Task.CompletedTask

    Public Sub SetAnalyzerPolling(channels As IReadOnlyDictionary(Of String, String()), intervalMilliseconds As Integer)
        SyncLock _analyzerGate
            If _analyzerCancellation IsNot Nothing Then _analyzerCancellation.Cancel()
            _analyzerSnapshots.Clear()
            _analyzerPending.Clear()
            _analyzerDropped = 0
            If channels Is Nothing OrElse channels.Count = 0 Then Return
            If intervalMilliseconds < 25 OrElse intervalMilliseconds > 60000 Then Throw New ArgumentOutOfRangeException(NameOf(intervalMilliseconds))
            Dim cancellation As New CancellationTokenSource()
            Dim previous = _analyzerTasks
            _analyzerCancellation = cancellation
            Dim settings = UpsSettingsStore.Current
            Dim requests = channels.Select(Function(item) (
                Device:=settings.Devices.Single(Function(device) device.Id.Equals(item.Key, StringComparison.OrdinalIgnoreCase)).Clone(),
                Keys:=item.Value.ToArray())).ToArray()
            _analyzerTasks = Task.Run(Async Function()
                Try
                    Await previous.ConfigureAwait(False)
                    Dim token = cancellation.Token
                    Using gate As New SemaphoreSlim(requests.Length, requests.Length)
                        Await Task.WhenAll(requests.Select(Function(request) Task.Run(
                            Function() RunAnalyzerAsync(request.Device, request.Keys, intervalMilliseconds, gate, token), token))).ConfigureAwait(False)
                    End Using
                Catch ex As OperationCanceledException When cancellation.IsCancellationRequested
                Finally
                    SyncLock _analyzerGate
                        If _analyzerCancellation Is cancellation Then _analyzerCancellation = Nothing
                        cancellation.Dispose()
                    End SyncLock
                End Try
            End Function)
            For Each request In requests
                PollNow(request.Device.Id)
            Next
        End SyncLock
    End Sub

    Public Function GetAnalyzerSnapshots() As IReadOnlyDictionary(Of String, UpsSnapshot)
        Return New Dictionary(Of String, UpsSnapshot)(_analyzerSnapshots, StringComparer.OrdinalIgnoreCase)
    End Function

    Public ReadOnly Property AnalyzerDroppedSamples As Long
        Get
            SyncLock _analyzerGate
                Return _analyzerDropped
            End SyncLock
        End Get
    End Property

    Public Function DrainAnalyzerSnapshots() As IReadOnlyList(Of UpsSnapshot)
        SyncLock _analyzerGate
            Dim pending = _analyzerPending.ToArray()
            _analyzerPending.Clear()
            Return pending
        End SyncLock
    End Function

    Public Function StopAnalyzerPollingAndDrain() As IReadOnlyList(Of UpsSnapshot)
        SyncLock _analyzerGate
            If _analyzerCancellation IsNot Nothing Then _analyzerCancellation.Cancel()
            _analyzerSnapshots.Clear()
            Dim pending = _analyzerPending.ToArray()
            _analyzerPending.Clear()
            Return pending
        End SyncLock
    End Function

    Private Sub PublishAnalyzerSnapshot(snapshot As UpsSnapshot)
        _analyzerSnapshots(snapshot.DeviceId) = snapshot
        If _analyzerPending.Count >= MaximumAnalyzerPendingSamples Then
            _analyzerPending.Dequeue()
            _analyzerDropped += 1
        End If
        _analyzerPending.Enqueue(snapshot)
    End Sub

    Private Async Function RunAnalyzerAsync(device As UpsDevice, keys As String(), intervalMilliseconds As Integer,
                                            gate As SemaphoreSlim, token As CancellationToken) As Task
        Dim due = Stopwatch.GetTimestamp()
        Dim period = CLng(intervalMilliseconds * CDbl(Stopwatch.Frequency) / 1000)
        While Not token.IsCancellationRequested
            Await gate.WaitAsync(token).ConfigureAwait(False)
            Try
                Dim configured = UpsSettingsStore.Current
                Dim currentDevice = configured.Devices.SingleOrDefault(Function(item) item.Id.Equals(device.Id, StringComparison.OrdinalIgnoreCase))
                Dim snapshot As UpsSnapshot
                If Not configured.AcquisitionEnabled OrElse currentDevice Is Nothing OrElse Not currentDevice.Enabled Then
                    snapshot = New UpsSnapshot With {.DeviceId = device.Id, .TimestampUtc = DateTime.UtcNow,
                        .Quality = UpsQuality.Disabled, .QualityDetail = "UPS acquisition or this device is disabled."}
                Else
                    snapshot = PollAnalyzerDevice(currentDevice, keys, Math.Clamp(intervalMilliseconds * 2, 100, 500))
                End If
                SyncLock _analyzerGate
                    If token.IsCancellationRequested Then Return
                    PublishAnalyzerSnapshot(snapshot)
                End SyncLock
            Catch ex As Exception When Not TypeOf ex Is OperationCanceledException
                Trace.WriteLine("UPS analyzer " & device.Id & ": " & ex.Message)
                SyncLock _analyzerGate
                    If Not token.IsCancellationRequested Then
                        PublishAnalyzerSnapshot(New UpsSnapshot With {.DeviceId = device.Id, .TimestampUtc = DateTime.UtcNow,
                            .Quality = UpsQuality.Invalid, .QualityDetail = "Analyzer request failed: " & ex.Message})
                    End If
                End SyncLock
            Finally
                gate.Release()
            End Try
            due += period
            Dim now = Stopwatch.GetTimestamp()
            If due < now Then due = now
            Dim remaining = Math.Max(1, CInt(Math.Ceiling((due - now) * 1000.0R / Stopwatch.Frequency)))
            Await Task.Delay(remaining, token).ConfigureAwait(False)
        End While
    End Function

    ' The running instance, for the settings page (test / discovery). Nothing in offline preview.
    Public Shared Property Current As UpsAcquisitionService
    Public Event SnapshotRecorded(snapshot As UpsSnapshot)

    ' Incremented whenever any snapshot changes so views can skip redundant redraws.
    Public ReadOnly Property Version As Long
        Get
            Return Interlocked.Read(_version)
        End Get
    End Property

    Public ReadOnly Property IsRunning As Boolean
        Get
            Return _loop IsNot Nothing AndAlso Not _loop.IsCompleted
        End Get
    End Property

    Public Function GetSnapshot(deviceId As String) As UpsSnapshot
        Dim snapshot As UpsSnapshot = Nothing
        If Not _snapshots.TryGetValue(deviceId, snapshot) Then Return Nothing
        Return ApplyStaleness(snapshot)
    End Function

    Public Function GetSnapshots() As IReadOnlyDictionary(Of String, UpsSnapshot)
        Dim result As New Dictionary(Of String, UpsSnapshot)(StringComparer.OrdinalIgnoreCase)
        For Each entry In _snapshots
            result(entry.Key) = ApplyStaleness(entry.Value)
        Next
        Return result
    End Function

    ' A Good snapshot older than three poll intervals is reported Stale; its values are kept only as LastGood.
    Private Shared Function ApplyStaleness(snapshot As UpsSnapshot) As UpsSnapshot
        If snapshot Is Nothing OrElse Not snapshot.IsGood Then Return snapshot
        Dim device = UpsSettingsStore.Current.Devices.FirstOrDefault(Function(item) String.Equals(item.Id, snapshot.DeviceId, StringComparison.OrdinalIgnoreCase))
        Dim interval = If(device Is Nothing, 10, device.PollIntervalSeconds)
        Dim limit = TimeSpan.FromSeconds(interval * 3 + 5)
        Dim sameConnection As Boolean
        Dim reason As String = String.Empty
        Try
            sameConnection = device IsNot Nothing AndAlso snapshot.ConnectionStamp = SnmpCredentialStore.ConnectionStamp(SnmpCredentialStore.EffectiveDevice(device))
            If Not sameConnection Then reason = "Connection settings changed; awaiting a verified poll with the current profile."
        Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is IO.InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
            reason = ex.Message
        End Try
        If sameConnection AndAlso DateTime.UtcNow - snapshot.TimestampUtc <= limit Then Return snapshot
        Return New UpsSnapshot With {
            .DeviceId = snapshot.DeviceId, .TimestampUtc = snapshot.TimestampUtc, .Quality = UpsQuality.Stale,
            .QualityDetail = If(reason.Length > 0, reason, "Last good reading is older than " & CInt(limit.TotalSeconds).ToString(CultureInfo.InvariantCulture) & " s."),
            .LastGoodUtc = snapshot.TimestampUtc, .LastGood = snapshot, .SysName = snapshot.SysName, .SysObjectId = snapshot.SysObjectId,
            .Manufacturer = snapshot.Manufacturer, .IdentModel = snapshot.IdentModel,
            .ProtocolVersion = snapshot.ProtocolVersion, .CapabilityVerifiedUtc = snapshot.CapabilityVerifiedUtc,
            .SupportedParameters = snapshot.SupportedParameters, .UnsupportedOids = snapshot.UnsupportedOids,
            .LastFailureReason = snapshot.LastFailureReason, .LastFailureUtc = snapshot.LastFailureUtc}
    End Function

    Public Function GetDiagnostics() As UpsAcquisitionDiagnostics
        Dim ticks = Interlocked.Read(_lastPollTicks)
        Return New UpsAcquisitionDiagnostics With {
            .Running = IsRunning,
            .PollsSucceeded = Interlocked.Read(_pollsOk),
            .PollsFailed = Interlocked.Read(_pollsFailed),
            .LastPollUtc = If(ticks = 0, CType(Nothing, DateTime?), New DateTime(ticks, DateTimeKind.Utc)),
            .InFlight = Volatile.Read(_inFlight),
            .MaxConcurrency = _semaphoreSize}
    End Function

    Public Sub Start()
        If IsRunning Then Return
        _cancellation = New CancellationTokenSource()
        Dim token = _cancellation.Token
        _loop = Task.Run(Function() RunAsync(token))
    End Sub

    Public Async Function StopAsync() As Task
        SetAnalyzerPolling(Nothing, 0)
        Dim analyzerTasks As Task
        SyncLock _analyzerGate
            analyzerTasks = _analyzerTasks
        End SyncLock
        Await analyzerTasks.ConfigureAwait(True)
        If _cancellation Is Nothing Then Return
        _cancellation.Cancel()
        Try
            If _loop IsNot Nothing Then Await Task.WhenAny(_loop, Task.Delay(5000)).ConfigureAwait(True)
        Catch ex As OperationCanceledException
        End Try
    End Function

    ' Schedules an immediate poll of one device (used by "Test selected" on the settings page).
    Public Sub PollNow(deviceId As String)
        SyncLock _stateGate
            Dim state As DeviceState = Nothing
            If _states.TryGetValue(deviceId, state) Then
                state.NextDueUtc = DateTime.UtcNow
                state.Failures = 0
            End If
        End SyncLock
    End Sub

    Private Async Function RunAsync(token As CancellationToken) As Task
        While Not token.IsCancellationRequested
            Try
                ScheduleDue(token)
            Catch ex As Exception When Not TypeOf ex Is OperationCanceledException
                Trace.WriteLine("UPS acquisition scheduler: " & ex.Message)
            End Try
            Try
                Await Task.Delay(250, token).ConfigureAwait(False)
            Catch ex As OperationCanceledException
                Exit While
            End Try
        End While
    End Function

    Private Sub ScheduleDue(token As CancellationToken)
        Dim settings = UpsSettingsStore.Current
        If _semaphore Is Nothing OrElse _semaphoreSize <> settings.MaxConcurrentPolls Then
            _semaphore = New SemaphoreSlim(settings.MaxConcurrentPolls, settings.MaxConcurrentPolls)
            _semaphoreSize = settings.MaxConcurrentPolls
        End If
        Dim now = DateTime.UtcNow
        Dim registered As New HashSet(Of String)(settings.Devices.Select(Function(device) device.Id), StringComparer.OrdinalIgnoreCase)
        Dim enabledDevices = settings.Devices.Where(Function(device) device.Enabled).ToList()
        SyncLock _stateGate
            For Each key In _states.Keys.Where(Function(id) Not registered.Contains(id)).ToList()
                _states.Remove(key)
                _snapshots.TryRemove(key, Nothing)
                Interlocked.Increment(_version)
            Next
            For Each device In settings.Devices
                If device.Enabled AndAlso settings.AcquisitionEnabled Then Continue For
                Dim existing As UpsSnapshot = Nothing
                If Not _snapshots.TryGetValue(device.Id, existing) OrElse existing.Quality <> UpsQuality.Disabled Then
                    _snapshots(device.Id) = New UpsSnapshot With {.DeviceId = device.Id, .TimestampUtc = now, .Quality = UpsQuality.Disabled,
                        .QualityDetail = If(settings.AcquisitionEnabled, "Polling disabled for this unit.", "UPS acquisition is disabled.")}
                    _states.Remove(device.Id)
                    Interlocked.Increment(_version)
                End If
            Next
            If Not settings.AcquisitionEnabled Then Return
            For index = 0 To enabledDevices.Count - 1
                Dim device = enabledDevices(index)
                Dim state As DeviceState = Nothing
                If Not _states.TryGetValue(device.Id, state) Then
                    ' Stagger first polls across the interval so all units are not hit in the same instant.
                    state = New DeviceState With {
                        .NextDueUtc = now.AddMilliseconds(index * device.PollIntervalSeconds * 1000.0R / Math.Max(1, enabledDevices.Count))}
                    _states(device.Id) = state
                End If
                If state.InFlight OrElse state.NextDueUtc > now Then Continue For
                state.InFlight = True
                Dim captured = device.Clone()
                Dim capturedState = state
                Dim semaphore = _semaphore
                Call Task.Run(Function() PollScheduledAsync(captured, capturedState, semaphore, token))
            Next
        End SyncLock
    End Sub

    Private Async Function PollScheduledAsync(device As UpsDevice, state As DeviceState, semaphore As SemaphoreSlim,
                                              token As CancellationToken) As Task
        Dim acquired = False
        Try
            Await semaphore.WaitAsync(token).ConfigureAwait(False)
            acquired = True
            Interlocked.Increment(_inFlight)
            Dim lastGood As UpsSnapshot
            Dim failures As Integer
            SyncLock _stateGate
                lastGood = state.LastGood
                failures = state.Failures
            End SyncLock
            Dim snapshot = PollDevice(device, failures, lastGood)
            Interlocked.Exchange(_lastPollTicks, DateTime.UtcNow.Ticks)
            SyncLock _stateGate
                If snapshot.IsGood Then
                    state.Failures = 0
                    state.LastGood = snapshot
                    state.NextDueUtc = DateTime.UtcNow.AddSeconds(device.PollIntervalSeconds)
                    Interlocked.Increment(_pollsOk)
                Else
                    state.Failures += 1
                    state.LastFailureReason = UpsQualityText.Describe(snapshot.Quality) & ": " & snapshot.QualityDetail
                    state.LastFailureUtc = snapshot.TimestampUtc
                    ' Keep checking failed connections even after a long outage; concurrency still bounds network load.
                    state.NextDueUtc = DateTime.UtcNow.AddSeconds(Math.Min(5, device.PollIntervalSeconds))
                    Interlocked.Increment(_pollsFailed)
                End If
                snapshot.LastFailureReason = state.LastFailureReason
                snapshot.LastFailureUtc = state.LastFailureUtc
                If token.IsCancellationRequested Then Return
                If _states.ContainsKey(device.Id) Then
                    _snapshots(device.Id) = snapshot
                    Interlocked.Increment(_version)
                End If
            End SyncLock
            If Not token.IsCancellationRequested Then RaiseEvent SnapshotRecorded(snapshot)
        Catch ex As OperationCanceledException
        Catch ex As Exception
            Interlocked.Increment(_pollsFailed)
            Trace.WriteLine("UPS poll " & device.Id & " failed unexpectedly: " & ex.Message)
        Finally
            If acquired Then
                Interlocked.Decrement(_inFlight)
                semaphore.Release()
            End If
            SyncLock _stateGate
                state.InFlight = False
            End SyncLock
        End Try
    End Function

    Private Shared Function Endpoint(ip As String, port As Integer) As IPEndPoint
        Return New IPEndPoint(IPAddress.Parse(ip.Trim()), port)
    End Function

    ' Full read of one UPS. Returns a snapshot with a quality state; never throws.
    Public Shared Function PollDevice(device As UpsDevice, previousFailures As Integer, lastGood As UpsSnapshot) As UpsSnapshot
        Return PollDeviceCore(device, previousFailures, lastGood, Nothing)
    End Function

    Public Shared Function PollAnalyzerDevice(device As UpsDevice, keys As IReadOnlyCollection(Of String),
                                             Optional requestBudgetMilliseconds As Integer = 500) As UpsSnapshot
        If keys Is Nothing OrElse keys.Count = 0 Then Throw New ArgumentException("Select at least one UPS channel.", NameOf(keys))
        If requestBudgetMilliseconds < 25 OrElse requestBudgetMilliseconds > 500 Then Throw New ArgumentOutOfRangeException(NameOf(requestBudgetMilliseconds))
        Return PollDeviceCore(device, 0, Nothing, keys, requestBudgetMilliseconds)
    End Function

    Private Shared Function PollDeviceCore(device As UpsDevice, previousFailures As Integer, lastGood As UpsSnapshot,
                                          analyzerKeys As IReadOnlyCollection(Of String),
                                          Optional requestBudgetMilliseconds As Integer = 500) As UpsSnapshot
        Dim started = Stopwatch.StartNew()
        Dim failed = Function(quality As UpsQuality, detail As String) As UpsSnapshot
                         Dim failures = previousFailures + 1
                         ' Three consecutive timeouts = the unit is treated as offline.
                         Dim effective = If(quality = UpsQuality.Timeout AndAlso failures >= 3, UpsQuality.Offline, quality)
                         Return New UpsSnapshot With {
                             .DeviceId = device.Id, .TimestampUtc = DateTime.UtcNow, .Quality = effective, .QualityDetail = detail,
                             .RoundTripMilliseconds = started.Elapsed.TotalMilliseconds,
                             .ConsecutiveFailures = failures, .LastGood = lastGood,
                             .LastGoodUtc = If(lastGood Is Nothing, CType(Nothing, DateTime?), lastGood.TimestampUtc)}
                     End Function
        Dim credential As SnmpCredential
        Dim connectionStamp As String
        Try
            device = SnmpCredentialStore.EffectiveDevice(device)
            If analyzerKeys IsNot Nothing Then
                device = device.Clone()
                device.TimeoutMilliseconds = Math.Min(requestBudgetMilliseconds, device.TimeoutMilliseconds)
                device.Retries = 0
            End If
            credential = SnmpCredentialStore.Resolve(device.CredentialProfile)
            connectionStamp = SnmpCredentialStore.ConnectionStamp(device)
        Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is IO.InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
            Return failed(UpsQuality.ConfigError, ex.Message)
        End Try
        If credential Is Nothing Then Return failed(UpsQuality.ConfigError, "Credential profile '" & device.CredentialProfile & "' does not exist.")
        Dim target As IPEndPoint
        Try
            target = Endpoint(device.IpAddress, device.Port)
        Catch ex As FormatException
            Return failed(UpsQuality.ConfigError, "Invalid IP address.")
        End Try
        ' Capabilities are proven per card (grouped probe, unsupported OIDs isolated) and cached; polls then request
        ' only objects this card is known to provide, so one unsupported OID can never fail the whole reading.
        Try
            Dim capability = UpsCapabilityProbe.GetCached(device)
            If analyzerKeys Is Nothing AndAlso capability IsNot Nothing AndAlso Not capability.Complete AndAlso
               DateTime.UtcNow - capability.VerifiedUtc >= TimeSpan.FromMinutes(1) Then capability = Nothing
            Dim probedNow = False
            If capability Is Nothing Then
                If analyzerKeys IsNot Nothing Then
                    Dim evidence = UpsCapabilityProbe.CurrentEvidence(device)
                    If evidence Is Nothing Then Return failed(UpsQuality.NoData, "Waiting for background UPS identity/capability verification; see UPS Monitoring.")
                    If Not evidence.IdentityAnswered Then Return failed(
                        If(evidence.FailureQuality = UpsQuality.NoData, UpsQuality.Timeout, evidence.FailureQuality),
                        "Background capability verification failed: " & evidence.Aborted & " Missing measurements are unknown, not zero.")
                    If evidence.Complete AndAlso Not evidence.SupportsUpsMib Then Return failed(UpsQuality.NotSupported,
                        "SNMP identity answered, but the verified card does not provide the RFC 1628 UPS-MIB. No verified measurement mapping; missing is not zero.")
                    Return failed(UpsQuality.NoData, "Incomplete background capability verification: " & evidence.Aborted)
                End If
                capability = UpsCapabilityProbe.Run(device, CancellationToken.None)
                probedNow = True
                If Not capability.IdentityAnswered Then
                    Return failed(If(capability.FailureQuality = UpsQuality.NoData, UpsQuality.Timeout, capability.FailureQuality), capability.Aborted)
                End If
            End If
            Dim selectedKeys = If(analyzerKeys Is Nothing, Nothing, AnalyzerMeasurementKeys(analyzerKeys, capability))
            Dim measurementBindings = capability.MeasurementBindings.Where(
                Function(item) selectedKeys Is Nothing OrElse selectedKeys.Contains(item.Definition.Key)).ToArray()
            Dim overridden = capability.MeasurementBindings.Select(Function(item) item.Definition.Key).ToHashSet(StringComparer.Ordinal)
            For Each prefix In {"Input", "Bypass"}
                If overridden.Contains(prefix & "VoltageLN") OrElse overridden.Contains(prefix & "VoltageLL") Then overridden.Add(prefix & "Voltage")
            Next
            If UpsMeasurementMap.IsNxl(capability.Model) Then overridden.Add("OutputPower")
            Dim scalarDefinitions = UpsOidMap.Definitions.Where(Function(item) Not overridden.Contains(item.Key) AndAlso Not item.PerPhase AndAlso Not UpsOidMap.IdentityKeys.Contains(item.Key) AndAlso
                                                                    capability.Supports(item.Oid) AndAlso
                                                                    (selectedKeys Is Nothing OrElse selectedKeys.Contains(item.Key))).ToList()
            Dim scalarOids As New List(Of String) From {UpsOidMap.SysObjectId, UpsOidMap.SysUpTime, UpsOidMap.SysName}
            scalarOids.AddRange(scalarDefinitions.Select(Function(item) item.Oid))
            Dim phaseDefinitions = UpsOidMap.Definitions.Where(Function(item) item.PerPhase AndAlso Not overridden.Contains(item.Key) AndAlso
                (selectedKeys Is Nothing OrElse selectedKeys.Contains(item.Key))).ToList()
            Dim analyzerData As IDictionary(Of String, ISnmpData) = Nothing
            If analyzerKeys IsNot Nothing Then
                Dim selectedOids As New List(Of String)(scalarOids)
                For Each definition In phaseDefinitions
                    For Each index In capability.IndicesFor(UpsCapabilityProbe.LinesKeyFor(definition.Key))
                        Dim oid = definition.Oid & index.ToString(CultureInfo.InvariantCulture)
                        If capability.Supports(oid) Then selectedOids.Add(oid)
                    Next
                Next
                selectedOids.AddRange(measurementBindings.Select(Function(item) item.Definition.Oid))
                analyzerData = GetWithRetry(target, credential, selectedOids.Distinct(StringComparer.Ordinal).ToList(), device,
                    acquisitionBudget:=started, acquisitionLimitMilliseconds:=requestBudgetMilliseconds)
            End If
            ' First group = liveness + scalars. A timeout here is a communication failure for the whole unit.
            Dim scalarData = If(analyzerData, GetWithRetry(target, credential, scalarOids, device))
            Dim rtt = started.Elapsed.TotalMilliseconds

            Dim values As New Dictionary(Of String, Double)(StringComparer.Ordinal)
            Dim invalid As New List(Of String)()
            Dim partialGroups As New List(Of String)()
            Dim vanished As New List(Of String)()
            Dim rawMeasurements As New Dictionary(Of String, String)(StringComparer.Ordinal)
            Dim measurementSources As New Dictionary(Of String, String)(StringComparer.Ordinal)
            Dim snapshot As New UpsSnapshot With {.DeviceId = device.Id, .TimestampUtc = DateTime.UtcNow, .RoundTripMilliseconds = rtt,
                .ConnectionStamp = connectionStamp,
                .ProtocolVersion = credential.Version, .SysDescr = capability.SysDescr, .Manufacturer = capability.Manufacturer,
                .IdentModel = capability.Model, .UpsSoftware = capability.UpsSoftware, .AgentSoftware = capability.AgentSoftware,
                .CapabilityVerifiedUtc = If(capability.Complete, capability.VerifiedUtc, CType(Nothing, DateTime?)),
                .SupportedParameters = capability.SupportedParameterCount, .UnsupportedOids = capability.UnsupportedOids}
            snapshot.SysObjectId = UpsSnmpClient.ToText(Lookup(scalarData, UpsOidMap.SysObjectId))
            snapshot.SysUpTime = UpsSnmpClient.ToText(Lookup(scalarData, UpsOidMap.SysUpTime))
            snapshot.SysName = UpsSnmpClient.ToText(Lookup(scalarData, UpsOidMap.SysName))
            If capability.SysObjectId.Length > 0 AndAlso Not String.Equals(snapshot.SysObjectId, capability.SysObjectId, StringComparison.Ordinal) Then
                ' Another device now answers at this address (card swapped/re-addressed): never mix its data with the old map.
                UpsCapabilityProbe.Invalidate(device.Id)
                Return failed(UpsQuality.Invalid, "Device identity changed at " & device.IpAddress & " (sysObjectID " & capability.SysObjectId & " → " &
                              snapshot.SysObjectId & "). Capabilities will be re-verified before data is accepted.")
            End If
            For Each definition In scalarDefinitions
                Dim data = Lookup(scalarData, definition.Oid)
                rawMeasurements(definition.Oid) = If(UpsSnmpClient.IsMissing(data), "unavailable", UpsSnmpClient.ToText(data))
                If UpsSnmpClient.IsMissing(data) Then
                    vanished.Add(definition.Key)
                    Continue For
                End If
                Dim raw = UpsSnmpClient.ToNumber(data)
                If Not raw.HasValue Then Continue For
                Dim scaled = raw.Value * definition.Scale
                If scaled < definition.MinValid OrElse scaled > definition.MaxValid Then
                    invalid.Add(definition.Key)
                Else
                    values(definition.Key) = scaled
                    measurementSources(definition.Key) = definition.Oid & " ×" & definition.Scale.ToString("0.###", CultureInfo.InvariantCulture)
                End If
            Next

            ' Per-phase tables: only rows proven by the capability walk; position 0..2 = the table rows in index order (L1..L3).
            Dim phases As New Dictionary(Of String, Double?())(StringComparer.Ordinal)
            For Each tableGroup In phaseDefinitions.GroupBy(Function(item) UpsCapabilityProbe.LinesKeyFor(item.Key))
                Dim indices = capability.IndicesFor(tableGroup.Key)
                If indices.Length = 0 Then Continue For
                Dim oids As New List(Of String)()
                For Each definition In tableGroup
                    For Each index In indices
                        Dim oid = definition.Oid & index.ToString(CultureInfo.InvariantCulture)
                        If capability.Supports(oid) Then oids.Add(oid)
                    Next
                Next
                If oids.Count = 0 Then Continue For
                Dim phaseData As IDictionary(Of String, ISnmpData)
                Try
                    phaseData = If(analyzerData, GetWithRetry(target, credential, oids, device))
                Catch ex As UpsSnmpFailure When ex.Quality = UpsQuality.Timeout
                    ' The scalars already arrived: keep them and flag this table instead of discarding the whole reading.
                    partialGroups.Add(TableName(tableGroup.Key) & ": " & ex.Message)
                    Continue For
                End Try
                For Each definition In tableGroup
                    Dim items(indices.Length - 1) As Double?
                    Dim any = False
                    For position = 0 To indices.Length - 1
                        Dim oid = definition.Oid & indices(position).ToString(CultureInfo.InvariantCulture)
                        If Not capability.Supports(oid) Then Continue For
                        Dim data = Lookup(phaseData, oid)
                        rawMeasurements(oid) = If(UpsSnmpClient.IsMissing(data), "unavailable", UpsSnmpClient.ToText(data))
                        If UpsSnmpClient.IsMissing(data) Then
                            vanished.Add(definition.Key & " L" & (position + 1).ToString(CultureInfo.InvariantCulture))
                            Continue For
                        End If
                        Dim raw = UpsSnmpClient.ToNumber(data)
                        If Not raw.HasValue Then Continue For
                        Dim scaled = raw.Value * definition.Scale
                        If scaled < definition.MinValid OrElse scaled > definition.MaxValid Then
                            invalid.Add(definition.Key & " L" & (position + 1).ToString(CultureInfo.InvariantCulture))
                        Else
                            items(position) = scaled
                            any = True
                        End If
                    Next
                    If any Then
                        phases(definition.Key) = items
                        measurementSources(definition.Key) = String.Join("; ", indices.Select(Function(index) definition.Oid &
                            index.ToString(CultureInfo.InvariantCulture) & " ×" & definition.Scale.ToString("0.###", CultureInfo.InvariantCulture)))
                    End If
                Next
            Next
            ' Line counts reported to the UI follow the verified table rows (not a blind 1..NumLines assumption).
            If capability.InputIndices.Length > 0 Then values("InputLines") = capability.InputIndices.Length
            If capability.OutputIndices.Length > 0 Then values("OutputLines") = capability.OutputIndices.Length
            If capability.BypassIndices.Length > 0 Then values("BypassLines") = capability.BypassIndices.Length
            If vanished.Count > 0 Then
                ' A previously supported object disappeared (firmware change/agent restart): re-verify on the next poll.
                UpsCapabilityProbe.Invalidate(device.Id)
                partialGroups.Add("Objects no longer provided: " & String.Join(", ", vanished))
            End If

            Dim vendorData As IDictionary(Of String, ISnmpData) = New Dictionary(Of String, ISnmpData)()
            If measurementBindings.Length > 0 Then
                Try
                    vendorData = If(analyzerData, GetWithRetry(target, credential, measurementBindings.Select(Function(item) item.Definition.Oid).ToList(), device))
                Catch ex As UpsSnmpFailure When ex.Quality = UpsQuality.Timeout
                    partialGroups.Add("Vertiv measurements: " & ex.Message)
                End Try
            End If
            snapshot.RawMeasurements = rawMeasurements
            snapshot.MeasurementSources = measurementSources
            UpsMeasurementMap.Apply(measurementBindings, vendorData, snapshot, values, phases, invalid)
            snapshot.Values = values
            snapshot.Phases = phases
            Dim alarmDevice = device.Clone()
            alarmDevice.TimeoutMilliseconds = Math.Min(1000, device.TimeoutMilliseconds)
            alarmDevice.Retries = 0
            Dim alarmBudget = Stopwatch.StartNew()
            If analyzerKeys Is Nothing Then
                UpsAlarmReader.Read(snapshot,
                    Function(oids) GetWithRetry(target, credential, oids, alarmDevice, alarmBudget),
                    Function(oid) UpsSnmpClient.GetNextValues(target, credential, {oid}, alarmDevice.TimeoutMilliseconds).FirstOrDefault())
            Else
                snapshot.AlarmDetailsStatus = "Selected-channel analyzer request only; alarm monitoring runs separately."
            End If
            If snapshot.PowerConsistencyIssue.Length > 0 Then invalid.Add(snapshot.PowerConsistencyIssue)
            snapshot.InvalidFields = invalid
            snapshot.PartialGroups = partialGroups
            snapshot.LastGoodUtc = snapshot.TimestampUtc
            snapshot.RoundTripMilliseconds = started.Elapsed.TotalMilliseconds
            If Not capability.SupportsUpsMib Then
                Dim notSupported = failed(UpsQuality.NotSupported, "Device answers SNMP (" & If(snapshot.SysName, "") & ") but does not implement the RFC 1628 UPS-MIB.")
                notSupported.SysName = snapshot.SysName
                notSupported.SysObjectId = snapshot.SysObjectId
                notSupported.SysDescr = snapshot.SysDescr
                notSupported.ProtocolVersion = credential.Version
                Return notSupported
            End If
            If values.Count = 0 AndAlso phases.Count = 0 Then Return failed(UpsQuality.Invalid, "No valid UPS values in this poll.")
            snapshot.Quality = UpsQuality.Good
            Dim notes As New List(Of String)()
            notes.Add(snapshot.MeasurementMap)
            If Not snapshot.AlarmDetailsComplete Then notes.Add(snapshot.AlarmDetailsStatus)
            If probedNow OrElse Not capability.Complete Then notes.Add(If(capability.Complete, "capabilities verified", "capability probe incomplete") &
                " (" & UpsCapabilityProbe.MappingText(capability) & ")" & If(capability.Aborted.Length = 0, "", ": " & capability.Aborted))
            If partialGroups.Count > 0 Then notes.Add("PARTIAL – " & String.Join("; ", partialGroups))
            If invalid.Count > 0 Then notes.Add("out-of-range values ignored: " & String.Join(", ", invalid))
            snapshot.QualityDetail = If(notes.Count = 0, "OK", String.Join(" • ", notes))
            Return snapshot
        Catch ex As UpsSnmpFailure
            Return failed(ex.Quality, ex.Message)
        Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is IO.InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
            Return failed(UpsQuality.ConfigError, ex.Message)
        Catch ex As Exception When TypeOf ex Is SnmpException OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is InvalidOperationException
            Return failed(UpsQuality.Invalid, ex.GetType().Name & ": " & ex.Message)
        End Try
    End Function

    Private Shared Function AnalyzerMeasurementKeys(keys As IReadOnlyCollection(Of String), capability As UpsCapabilityProfile) As HashSet(Of String)
        Dim selected As New HashSet(Of String)(keys.Where(Function(key) Not key.StartsWith("@", StringComparison.Ordinal)), StringComparer.Ordinal)
        If keys.Any(Function(key) UpsInputEstimates.IsEstimateKey(key)) Then
            selected.Add("InputVoltageLL")
            selected.Add("InputVoltage")
        End If
        If keys.Contains(UpsInputEstimates.KvaKey) Then selected.Add("InputCurrent")
        If keys.Contains("@OutputKw") OrElse keys.Contains("@OutputPf") Then selected.Add("OutputPower")
        If keys.Contains("@OutputLoadMax") Then selected.Add("OutputLoad")
        If keys.Contains("@OutputKva") OrElse keys.Contains("@OutputPf") Then
            selected.Add("OutputApparentVa")
            If Not capability.MeasurementBindings.Any(Function(item) item.Definition.Key = "OutputApparentVa") Then
                selected.Add("OutputVoltage")
                selected.Add("OutputCurrent")
            End If
        End If
        For Each prefix In {"Input", "Bypass"}
            If selected.Contains(prefix & "Voltage") Then
                selected.Add(prefix & "VoltageLN")
                selected.Add(prefix & "VoltageLL")
            End If
        Next
        Return selected
    End Function

    Private Shared Function TableName(linesKey As String) As String
        Select Case linesKey
            Case "InputLines" : Return "Input table"
            Case "BypassLines" : Return "Bypass table"
            Case Else : Return "Output table"
        End Select
    End Function

    Private Shared Function Lookup(data As IDictionary(Of String, ISnmpData), oid As String) As ISnmpData
        Dim value As ISnmpData = Nothing
        If data IsNot Nothing AndAlso data.TryGetValue(oid, value) Then Return value
        Return Nothing
    End Function

    ' Selected reads allow 32 bindings so complete input/output selections fit one request; full background reads retain 24.
    ' An error response (SNMPv1 noSuchName, genErr, tooBig) rejects the whole PDU,
    ' so that batch is retried object-by-object and only the rejected objects are left missing.
    Private Shared Function GetWithRetry(target As IPEndPoint, credential As SnmpCredential, oids As IList(Of String),
                                         device As UpsDevice, Optional alarmBudget As Stopwatch = Nothing,
                                         Optional acquisitionBudget As Stopwatch = Nothing,
                                         Optional acquisitionLimitMilliseconds As Integer = 500) As IDictionary(Of String, ISnmpData)
        Dim result As New Dictionary(Of String, ISnmpData)(StringComparer.Ordinal)
        Dim batchSize = If(acquisitionBudget Is Nothing, 24, 32)
        For offset = 0 To oids.Count - 1 Step batchSize
            If alarmBudget IsNot Nothing AndAlso alarmBudget.Elapsed >= TimeSpan.FromSeconds(4) Then
                Throw New UpsSnmpFailure(UpsQuality.Timeout, "Four-second alarm detail budget exceeded.")
            End If
            Dim batch = oids.Skip(offset).Take(batchSize).ToList()
            Dim timeout = device.TimeoutMilliseconds
            If acquisitionBudget IsNot Nothing Then
                timeout = Math.Min(timeout, acquisitionLimitMilliseconds - CInt(acquisitionBudget.ElapsedMilliseconds))
                If timeout <= 0 Then Throw New UpsSnmpFailure(UpsQuality.Timeout, "Selected-channel UPS request exceeded its " &
                    acquisitionLimitMilliseconds.ToString(CultureInfo.InvariantCulture) & " ms budget.")
            End If
            Try
                For Each variable In UpsCapabilityProbe.GetWithRetries(target, credential, batch, timeout, device.Retries)
                    result(variable.Id.ToString()) = variable.Data
                Next
            Catch ex As UpsSnmpFailure When acquisitionBudget Is Nothing AndAlso ex.IsErrorResponse AndAlso ex.ErrorStatus <> 6 AndAlso ex.ErrorStatus <> 16
                For Each oid In batch
                    If alarmBudget IsNot Nothing AndAlso alarmBudget.Elapsed >= TimeSpan.FromSeconds(4) Then
                        Throw New UpsSnmpFailure(UpsQuality.Timeout, "Four-second alarm detail budget exceeded.")
                    End If
                    Try
                        For Each variable In UpsCapabilityProbe.GetWithRetries(target, credential, {oid}, device.TimeoutMilliseconds, device.Retries)
                            result(variable.Id.ToString()) = variable.Data
                        Next
                    Catch inner As UpsSnmpFailure When inner.IsErrorResponse
                    End Try
                Next
            End Try
        Next
        Return result
    End Function

    ' Read-only identity probe for discovery: system group + UPS-MIB identity. Never writes to the device.
    Public Shared Function Probe(ip As String, port As Integer, credentialProfile As String, timeoutMs As Integer,
                                 Optional retries As Integer = 0) As UpsProbeResult
        Dim result As New UpsProbeResult With {.IpAddress = ip}
        Dim started = Stopwatch.StartNew()
        Try
            Dim credential = SnmpCredentialStore.Resolve(credentialProfile)
            If credential Is Nothing Then
                result.Quality = UpsQuality.ConfigError
                result.Detail = "Credentials Not Configured: profile '" & credentialProfile & "' does not exist."
                Return result
            End If
            Dim oids As New List(Of String) From {UpsOidMap.SysDescr, UpsOidMap.SysObjectId, UpsOidMap.SysName,
                UpsOidMap.Find("Manufacturer").Oid, UpsOidMap.Find("IdentModel").Oid}
            Dim device As New UpsDevice With {.TimeoutMilliseconds = timeoutMs, .Retries = retries}
            Dim data = GetWithRetry(Endpoint(ip, port), credential, oids, device)
            result.RoundTripMilliseconds = started.Elapsed.TotalMilliseconds
            result.Responds = data.Values.Any(Function(value) Not UpsSnmpClient.IsMissing(value))
            result.ProtocolVersion = credential.Version
            result.Quality = UpsQuality.Good
            result.SysDescr = UpsSnmpClient.ToText(Lookup(data, UpsOidMap.SysDescr))
            result.SysObjectId = UpsSnmpClient.ToText(Lookup(data, UpsOidMap.SysObjectId))
            result.SysName = UpsSnmpClient.ToText(Lookup(data, UpsOidMap.SysName))
            result.Manufacturer = UpsSnmpClient.ToText(Lookup(data, UpsOidMap.Find("Manufacturer").Oid))
            result.Model = UpsSnmpClient.ToText(Lookup(data, UpsOidMap.Find("IdentModel").Oid))
            result.SupportsUpsMib = result.Manufacturer.Length > 0 OrElse result.Model.Length > 0
            result.Detail = If(result.SupportsUpsMib, "RFC 1628 UPS-MIB present", "SNMP agent without UPS-MIB")
        Catch ex As UpsSnmpFailure
            result.Quality = ex.Quality
            result.Detail = ex.Message
        Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is IO.InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
            result.Quality = UpsQuality.ConfigError
            result.Detail = ex.Message
        Catch ex As Exception When TypeOf ex Is SnmpException OrElse TypeOf ex Is FormatException OrElse TypeOf ex Is ArgumentException
            result.Quality = UpsQuality.Invalid
            result.Detail = ex.Message
        End Try
        Return result
    End Function

    ' Bounded discovery scan (8 parallel probes). Callers must enforce the administrator confirmation for ranges.
    Public Shared Async Function ScanAsync(addresses As IReadOnlyList(Of String), port As Integer, credentialProfile As String,
                                           timeoutMs As Integer, progress As IProgress(Of UpsProbeResult),
                                           token As CancellationToken) As Task(Of IReadOnlyList(Of UpsProbeResult))
        Dim results As New ConcurrentBag(Of UpsProbeResult)()
        Dim concurrency = Math.Min(4, UpsSettingsStore.Current.MaxConcurrentPolls)
        Using gate As New SemaphoreSlim(concurrency, concurrency)
            Dim tasks = addresses.Select(Async Function(ip)
                                             Await gate.WaitAsync(token).ConfigureAwait(False)
                                             Try
                                                 token.ThrowIfCancellationRequested()
                                                 Dim target = SnmpCredentialStore.EffectiveDevice(New UpsDevice With {
                                                     .IpAddress = ip, .Port = port, .CredentialProfile = credentialProfile,
                                                     .TimeoutMilliseconds = timeoutMs, .UseProfileTransport = True})
                                                 Dim probeResult = Await Task.Run(Function() Probe(ip, target.Port, credentialProfile, target.TimeoutMilliseconds, target.Retries), token).ConfigureAwait(False)
                                                 results.Add(probeResult)
                                                 If progress IsNot Nothing Then progress.Report(probeResult)
                                             Finally
                                                 gate.Release()
                                             End Try
                                         End Function).ToList()
            Await Task.WhenAll(tasks).ConfigureAwait(False)
        End Using
        Return results.OrderBy(Function(item) Version4Key(item.IpAddress)).ToList()
    End Function

    Private Shared Function Version4Key(ip As String) As Long
        Dim address As IPAddress = Nothing
        If Not IPAddress.TryParse(ip, address) Then Return Long.MaxValue
        Dim bytes = address.GetAddressBytes()
        Return (CLng(bytes(0)) << 24) Or (CLng(bytes(1)) << 16) Or (CLng(bytes(2)) << 8) Or bytes(3)
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        SetAnalyzerPolling(Nothing, 0)
        If _cancellation IsNot Nothing Then
            _cancellation.Cancel()
            _cancellation.Dispose()
            _cancellation = Nothing
        End If
    End Sub
End Class
