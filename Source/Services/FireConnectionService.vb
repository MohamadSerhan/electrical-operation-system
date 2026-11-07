Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Net.NetworkInformation
Imports System.Net.Sockets
Imports System.Security.Cryptography
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks

Public NotInheritable Class FireConnectionEvent
    Public Property TimestampUtc As DateTime
    Public Property Message As String = String.Empty
    Public Property TestResult As FireConnectionTestResult
End Class

Public NotInheritable Class FireDiagnosticStage
    Public Property State As String = "NOT TESTED"
    Public Property Detail As String = String.Empty
    Public Property ResponseMilliseconds As Double?
    Public Property TimestampUtc As DateTime?
    Public Property NativeErrorCode As Integer?

    Public Function Copy() As FireDiagnosticStage
        Return DirectCast(MemberwiseClone(), FireDiagnosticStage)
    End Function

    Public Overrides Function ToString() As String
        Return State & If(ResponseMilliseconds.HasValue, " (" & ResponseMilliseconds.Value.ToString("0", Globalization.CultureInfo.InvariantCulture) & " ms)", String.Empty) &
            If(Detail.Length > 0, " - " & Detail, String.Empty)
    End Function
End Class

Public NotInheritable Class FireConnectionTestResult
    Public Property ConnectionType As FireConnectionType
    Public Property Host As String = String.Empty
    Public Property Port As Integer
    Public Property TimestampUtc As DateTime?
    Public Property IsReference As Boolean
    Public Property CompletedUtc As DateTime?
    Public Property SourceAddress As String = "NOT RECORDED"
    Public Property SourceInterface As String = "NOT RECORDED"
    Public Property SourceEvidence As String = "NOT RECORDED"
    Public Property DestinationAddress As String = "NOT RECORDED"
    Public Property FailureStage As String = "NOT RECORDED"
    Public Property Reachability As New FireDiagnosticStage
    Public Property Transport As New FireDiagnosticStage
    Public Property Protocol As New FireDiagnosticStage
    Public Property RegisterMapping As String = "NOT VERIFIED"
    Public Property Interpretation As String = "UNKNOWN - not implemented"
    Public Property PrimaryError As String = String.Empty

    Public Function Copy() As FireConnectionTestResult
        Dim result = DirectCast(MemberwiseClone(), FireConnectionTestResult)
        result.Reachability = Reachability.Copy()
        result.Transport = Transport.Copy()
        result.Protocol = Protocol.Copy()
        Return result
    End Function

    Public Function Summary() As String
        Dim target = If(Host.Contains(":"c), "[" & Host & "]", Host) & ":" & Port.ToString(Globalization.CultureInfo.InvariantCulture)
        Return "Target " & ConnectionType.ToString() & " " & target &
            " | IP Reachability: " & Reachability.ToString() & " | TCP: " & Transport.ToString() &
            " | Modbus: " & Protocol.ToString() & " | Register mapping: " & RegisterMapping &
            " | Source: " & SourceAddress & " | Interface: " & SourceInterface & " | Source evidence: " & SourceEvidence &
            " | Destination address: " & DestinationAddress & " | Failure stage: " & FailureStage &
            " | Fire Status: UNKNOWN | " & PrimaryError &
            If(IsReference, " | Supplied prior test; timestamp not provided; not retested.",
               " | Test UTC: " & TimestampUtc.GetValueOrDefault().ToString("O", Globalization.CultureInfo.InvariantCulture)) &
            If(CompletedUtc.HasValue, " | Completed UTC: " & CompletedUtc.Value.ToString("O", Globalization.CultureInfo.InvariantCulture), String.Empty)
    End Function
End Class

Public NotInheritable Class FireConnectionSnapshot
    Public Property State As String = "NOT CONFIGURED"
    Public Property ConnectionType As FireConnectionType
    Public Property Endpoint As String = String.Empty
    Public Property LastSuccessUtc As DateTime?
    Public Property LastDataUtc As DateTime?
    Public Property ResponseMilliseconds As Double?
    Public Property ConnectedSinceUtc As DateTime?
    Public Property ConsecutiveFailures As Integer
    Public Property LastError As String = String.Empty
    Public Property LastTestResult As String = String.Empty
    Public Property LastTest As FireConnectionTestResult
    Public Property ProtocolHealth As String = "DISCONNECTED"
    Public Property TransportHealth As String = "DISCONNECTED"
    Public Property EventLogError As String = String.Empty
    Public Property Monitoring As Boolean
    Public Property HasCredentials As Boolean
    Public Property HasCloudCredentials As Boolean
    Public Property HasSavedCloudLogin As Boolean
    Public Property HasBmsCredentials As Boolean
    Public Property HasCloudToken As Boolean
    Public Property CloudAuthentication As String = "NOT TESTED"
    Public Property CloudCredentialRenewalRequired As Boolean
    Public Property CloudTransportSucceeded As Boolean
    Public Property CloudErrorOrigin As String = String.Empty
    Public Property CloudServerErrorCategory As String = String.Empty
    Public Property HttpStatus As Integer?
    Public Property CloudStage As String = "NOT TESTED"
    Public Property CloudSessionExpiresUtc As DateTimeOffset?
    Public Property LastEventUtc As DateTimeOffset?
    Public Property Events As IReadOnlyList(Of FireConnectionEvent) = Array.Empty(Of FireConnectionEvent)()

    Public Function Copy() As FireConnectionSnapshot
        Dim result = DirectCast(MemberwiseClone(), FireConnectionSnapshot)
        result.LastTest = LastTest?.Copy()
        result.Events = Events.Select(Function(item) New FireConnectionEvent With {.TimestampUtc = item.TimestampUtc, .Message = item.Message, .TestResult = item.TestResult?.Copy()}).ToArray()
        Return result
    End Function
End Class

Partial Public NotInheritable Class FireConnectionService
    Implements IDisposable

    Private ReadOnly _store As FireConnectionStore
    Private ReadOnly _gate As New Object
    Private ReadOnly _commands As New SemaphoreSlim(1, 1)
    Private ReadOnly _communicationGate As New SemaphoreSlim(1, 1)
    Private ReadOnly _lifetime As New CancellationTokenSource
    Private _settings As New FireConnectionSettings
    Private _snapshot As New FireConnectionSnapshot
    Private _cancellation As CancellationTokenSource
    Private _worker As Task
    Private _loadError As String = String.Empty
    Private _eventReadError As String = String.Empty
    Private _allowCommunication As Boolean
    Private _disposed As Boolean
    Private _lastStoppedMessage As String = String.Empty
    Private ReadOnly _testedMappings As New HashSet(Of String)(StringComparer.Ordinal)

    Public Sub New(Optional store As FireConnectionStore = Nothing, Optional allowCommunication As Boolean = True,
                   Optional cloudClient As ZetaCloudEventClient = Nothing)
        _store = If(store, New FireConnectionStore)
        _cloudClient = If(cloudClient, New ZetaCloudEventClient)
        _cloudHistory = New ZetaCloudEventHistory(_store.FilePath)
        Try
            _mapAlerts = New FireMapAlertStore(_store.FilePath)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is CryptographicException OrElse TypeOf ex Is FormatException
            _mapAlertsError = ex.Message
        End Try
        _allowCommunication = allowCommunication
        Try
            Dim document = _store.Load()
            _settings = document.Settings.Copy()
            _snapshot.HasCredentials = document.ProtectedPassword.Length > 0 OrElse document.ProtectedUsername.Length > 0 OrElse document.ProtectedBmsUsername.Length > 0 OrElse document.ProtectedBmsPassword.Length > 0
            _snapshot.HasCloudCredentials = document.ProtectedPassword.Length > 0 OrElse document.ProtectedUsername.Length > 0
            _snapshot.HasBmsCredentials = document.ProtectedBmsUsername.Length > 0 OrElse document.ProtectedBmsPassword.Length > 0
            _snapshot.HasCloudToken = document.ProtectedCloudToken.Length > 0
            _snapshot.HasSavedCloudLogin = document.ProtectedCloudEmail.Length > 0 AndAlso document.ProtectedCloudPassword.Length > 0
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is JsonException OrElse
            TypeOf ex Is CryptographicException OrElse TypeOf ex Is FormatException OrElse TypeOf ex Is UnauthorizedAccessException
            _loadError = "Cannot load/decrypt fire connection settings. Restore the protected configuration using the original Windows account before editing."
            _snapshot.LastError = _loadError
        End Try
        DescribeSettings()
        If _loadError.Length = 0 Then
            If _settings.ConnectionType = FireConnectionType.ZetaCloud AndAlso Not _snapshot.HasCloudToken AndAlso Not _snapshot.HasSavedCloudLogin AndAlso _settings.CloudApiEndpoint = ZetaCloudEventClient.EventRoute Then
                _snapshot.State = "NOT CONFIGURED"
            ElseIf _settings.UnavailableReason().Length = 0 Then
                _snapshot.State = "OFFLINE"
            Else
                _snapshot.State = StateForSettings(_settings)
            End If
        End If
        LoadEvents()
        Dim previousTest = _snapshot.Events.LastOrDefault(Function(item) item.TestResult IsNot Nothing)
        If previousTest IsNot Nothing Then
            _snapshot.LastTest = previousTest.TestResult.Copy()
            Dim restored = _snapshot.LastTest
            If restored.Transport.State <> "CONNECTED" AndAlso restored.Transport.State <> "NOT TESTED" Then
                If restored.Transport.Detail.Length > 0 AndAlso Not restored.PrimaryError.Contains(restored.Transport.Detail, StringComparison.Ordinal) Then
                    restored.PrimaryError &= " - " & restored.Transport.Detail
                End If
                If restored.FailureStage = "NOT RECORDED" Then restored.FailureStage = If(restored.Reachability.State = "SUCCESS", "TCP TRANSPORT", "IP REACHABILITY")
            End If
        ElseIf _settings.ConnectionType = FireConnectionType.LocalModbus AndAlso _settings.LocalHost = "192.168.107.90" AndAlso _settings.LocalPort = 502 Then
            _snapshot.LastTest = New FireConnectionTestResult With {
                .ConnectionType = FireConnectionType.LocalModbus, .Host = "192.168.107.90", .Port = 502, .IsReference = True,
                .Reachability = New FireDiagnosticStage With {.State = "SUCCESS", .ResponseMilliseconds = 1, .Detail = "Supplied result: 0-1 ms"},
                .Transport = New FireDiagnosticStage With {.State = "REFUSED", .NativeErrorCode = 10061, .Detail = "Supplied result: ConnectionRefused (10061)"},
                .Protocol = New FireDiagnosticStage With {.Detail = "TCP connection was unsuccessful"},
                .SourceAddress = "192.168.105.29", .SourceEvidence = "Supplied source reference; not observed by SCADA",
                .DestinationAddress = "192.168.107.90", .FailureStage = "TCP TRANSPORT",
                .PrimaryError = "TCP CONNECTION REFUSED - 192.168.107.90:502 (10061; supplied prior result)"}
        End If
        If _snapshot.LastTest IsNot Nothing Then _snapshot.LastTestResult = _snapshot.LastTest.Summary()
    End Sub

    Public ReadOnly Property StoreFilePath As String
        Get
            Return _store.FilePath
        End Get
    End Property

    Public Function ReadSettings() As FireConnectionSettings
        SyncLock _gate
            Return _settings.Copy()
        End SyncLock
    End Function

    Public Function ReadStatus() As FireConnectionSnapshot
        SyncLock _gate
            Dim result = _snapshot.Copy()
            If _worker IsNot Nothing AndAlso _worker.IsFaulted Then
                result.State = "OFFLINE"
                result.LastError = "Fire monitoring stopped unexpectedly. Review the application error and reconnect."
                result.Monitoring = False
                result.ConnectedSinceUtc = Nothing
                Dim observed = _worker.Exception
            ElseIf (result.Monitoring OrElse result.ConnectionType = FireConnectionType.ZetaCloud) AndAlso (result.State = "ONLINE" OrElse result.State = "CONNECTING") AndAlso result.LastDataUtc.HasValue AndAlso
                (DateTime.UtcNow - result.LastDataUtc.Value).TotalSeconds > _settings.FreshnessSeconds Then
                result.State = "DEGRADED"
                result.LastError = "Monitoring data is stale. Fire panel status is UNKNOWN."
                result.ProtocolHealth = "STALE"
                result.ConnectedSinceUtc = Nothing
            End If
            Return result
        End SyncLock
    End Function

    Public Async Function StartAsync(Optional enableCommunication As Boolean = True) As Task
        Await _commands.WaitAsync().ConfigureAwait(False)
        Try
            _allowCommunication = enableCommunication
            If Not enableCommunication Then
                Await StopWorkerAsync().ConfigureAwait(False)
                _cloudClient.InvalidateAuthorization()
                SetStopped("OFFLINE", "Communication is disabled for this session. Fire panel status is UNKNOWN.")
                Return
            End If
            If Not ReadSettings().AutoConnect Then Return
            If Not ReadStatus().Monitoring Then
                Await StopWorkerAsync().ConfigureAwait(False)
                StartWorker()
            End If
        Finally
            _commands.Release()
        End Try
    End Function

    Public Async Function ConnectAsync(session As UserSession) As Task
        FireConnectionStore.RequireAdministrator(session)
        If Not Await _commands.WaitAsync(0).ConfigureAwait(False) Then Throw New InvalidOperationException("Another fire connection command is running.")
        Try
            RequireCommunication()
            Dim reason = ReadSettings().UnavailableReason()
            If reason.Length = 0 AndAlso ReadSettings().IsModbus() AndAlso Not FireConnectionStore.HasMappingProof(ReadSettings()) Then reason = "Connect requires saved FC04 test evidence for the reviewed manufacturer mapping."
            If reason.Length = 0 AndAlso ReadSettings().ConnectionType = FireConnectionType.ZetaCloud AndAlso Not ReadStatus().HasCloudToken AndAlso Not ReadStatus().HasSavedCloudLogin Then reason = "Sign in to Zeta or save an authorized cloud token before connecting. Legacy credentials are not reused."
            If reason.Length > 0 Then
                Throw New InvalidOperationException(reason)
            End If
            Await StopWorkerAsync().ConfigureAwait(False)
            StartWorker()
        Finally
            _commands.Release()
        End Try
    End Function

    Public Async Function DisconnectAsync(session As UserSession) As Task
        FireConnectionStore.RequireAdministrator(session)
        Await StopAsync().ConfigureAwait(False)
    End Function

    Public Async Function StopAsync() As Task
        Await _commands.WaitAsync().ConfigureAwait(False)
        Try
            Await StopWorkerAsync().ConfigureAwait(False)
            If _loadError.Length = 0 Then SetStopped(If(ReadSettings().UnavailableReason().Length = 0, "OFFLINE", StateForSettings(ReadSettings())), "Monitoring disconnected. Fire panel status is UNKNOWN.")
        Finally
            _commands.Release()
        End Try
    End Function

    Public Async Function SaveAsync(settings As FireConnectionSettings, username As String, password As String,
                                    clearCredentials As Boolean, session As UserSession, Optional clearAllCredentials As Boolean = False,
                                    Optional cloudToken As String = Nothing, Optional clearCloudToken As Boolean = False) As Task
        FireConnectionStore.RequireAdministrator(session)
        Dim candidate = settings.Copy()
        candidate.Validate()
        Await _commands.WaitAsync().ConfigureAwait(False)
        Try
            Await SaveSettingsCoreAsync(candidate, username, password, clearCredentials, session, clearAllCredentials, cloudToken, clearCloudToken).ConfigureAwait(False)
        Finally
            _commands.Release()
        End Try
    End Function

    Private Async Function SaveSettingsCoreAsync(candidate As FireConnectionSettings, username As String, password As String,
                                                 clearCredentials As Boolean, session As UserSession, clearAllCredentials As Boolean,
                                                 cloudToken As String, clearCloudToken As Boolean,
                                                 Optional cloudEmail As String = Nothing, Optional cloudPassword As String = Nothing) As Task
        If _loadError.Length > 0 Then Throw New InvalidOperationException(_loadError)
        If candidate.MappingVerified AndAlso Not FireConnectionStore.HasMappingProof(candidate) Then
            SyncLock _gate
                If Not _testedMappings.Contains(FireConnectionStore.MappingIdentity(candidate)) Then Throw New InvalidDataException("Mapping not verified. Review the vendor mapping and successfully Test Connection for these exact settings before Save.")
                candidate.MappingProof = FireConnectionStore.CreateMappingProof(candidate)
            End SyncLock
        End If
        Await Task.Run(Sub() _store.Save(candidate, username, password, clearCredentials, session, clearAllCredentials, cloudToken, clearCloudToken, cloudEmail, cloudPassword)).ConfigureAwait(False)
        If clearAllCredentials Then
            SyncLock _gate
                _testedMappings.Clear()
            End SyncLock
        End If
        Dim document = Await Task.Run(Function() _store.Load()).ConfigureAwait(False)
        Dim wasMonitoring = ReadStatus().Monitoring
        Await StopWorkerAsync().ConfigureAwait(False)
        _cloudClient.InvalidateAuthorization()
        SyncLock _gate
            _settings = document.Settings.Copy()
            _snapshot = New FireConnectionSnapshot With {
                .Events = _snapshot.Events, .EventLogError = _snapshot.EventLogError, .LastTestResult = _snapshot.LastTestResult, .LastTest = _snapshot.LastTest?.Copy(),
                .HasCredentials = document.ProtectedPassword.Length > 0 OrElse document.ProtectedUsername.Length > 0 OrElse document.ProtectedBmsPassword.Length > 0 OrElse document.ProtectedBmsUsername.Length > 0,
                .HasCloudCredentials = document.ProtectedPassword.Length > 0 OrElse document.ProtectedUsername.Length > 0,
                .HasCloudToken = document.ProtectedCloudToken.Length > 0,
                .HasSavedCloudLogin = document.ProtectedCloudEmail.Length > 0 AndAlso document.ProtectedCloudPassword.Length > 0,
                .HasBmsCredentials = document.ProtectedBmsPassword.Length > 0 OrElse document.ProtectedBmsUsername.Length > 0}
            DescribeSettings()
        End SyncLock
        Record("Connection settings saved locally. No panel configuration was changed.")
        Dim missingCloudToken = candidate.ConnectionType = FireConnectionType.ZetaCloud AndAlso
            candidate.CloudApiEndpoint = ZetaCloudEventClient.EventRoute AndAlso Not ReadStatus().HasCloudToken AndAlso Not ReadStatus().HasSavedCloudLogin
        SetStopped(If(missingCloudToken, "NOT CONFIGURED", If(candidate.UnavailableReason().Length = 0, "OFFLINE", StateForSettings(candidate))), candidate.UnavailableReason(), logEvent:=False)
        If wasMonitoring AndAlso _allowCommunication Then StartWorker()
    End Function

    Public Async Function TestAsync(settings As FireConnectionSettings, session As UserSession) As Task(Of String)
        FireConnectionStore.RequireAdministrator(session)
        RequireCommunication()
        Dim candidate = settings.Copy()
        candidate.Validate(validateRegisters:=False)
        If Not Await _commands.WaitAsync(0).ConfigureAwait(False) Then Throw New InvalidOperationException("Another fire connection command is running.")
        Try
            Dim test As New FireConnectionTestResult With {
                .ConnectionType = candidate.ConnectionType,
                .Host = If(candidate.ConnectionType = FireConnectionType.ZetaCloud, New Uri(candidate.CloudUrl).Host, candidate.Host()),
                .Port = If(candidate.ConnectionType = FireConnectionType.ZetaCloud, New Uri(candidate.CloudUrl).Port, candidate.Port()),
                .TimestampUtc = DateTime.UtcNow}
            If Not candidate.IsModbus() OrElse String.IsNullOrWhiteSpace(candidate.Host()) Then
                test.PrimaryError = candidate.UnavailableReason()
                test.Protocol.State = "UNSUPPORTED"
                test.FailureStage = "PROTOCOL AVAILABILITY"
                CompleteTest(test)
                Throw New InvalidOperationException(test.PrimaryError)
            End If
            Await _communicationGate.WaitAsync(_lifetime.Token).ConfigureAwait(False)
            Try
                test.Reachability = Await TestReachabilityAsync(candidate.Host(), candidate.TimeoutMilliseconds, _lifetime.Token).ConfigureAwait(False)
                Using client As New TcpClient()
                    test.Transport = Await TestTransportAsync(client, candidate, test, _lifetime.Token).ConfigureAwait(False)
                    If test.Transport.State <> "CONNECTED" Then
                        test.PrimaryError = If(test.Reachability.State <> "SUCCESS",
                            "IP REACHABILITY " & test.Reachability.State & " - " & test.Host & ". ", String.Empty) &
                            "TCP CONNECTION " & test.Transport.State & " - " & test.Host & ":" & test.Port.ToString(Globalization.CultureInfo.InvariantCulture)
                        test.PrimaryError &= " - " & test.Transport.Detail
                        test.FailureStage = If(test.Reachability.State <> "SUCCESS", "IP REACHABILITY", "TCP TRANSPORT")
                        test.Protocol.Detail = "Modbus protocol and register mapping could not be tested because the TCP connection was unsuccessful."
                        SyncLock _gate
                            _testedMappings.Remove(FireConnectionStore.MappingIdentity(candidate))
                        End SyncLock
                    ElseIf Not candidate.MappingVerified OrElse String.IsNullOrWhiteSpace(candidate.MappingReference) Then
                        test.PrimaryError = "TCP CONNECTED - MODBUS REGISTER MAPPING NOT CONFIGURED"
                        test.FailureStage = "REGISTER CONFIGURATION"
                        test.Protocol.Detail = "An approved register range and manufacturer reference must be reviewed before sending FC04."
                    Else
                        Dim watch = Stopwatch.StartNew()
                        test.FailureStage = "REGISTER CONFIGURATION"
                        Try
                            candidate.Validate()
                            test.FailureStage = "MODBUS FC04"
                            test.Protocol.TimestampUtc = DateTime.UtcNow
                            watch.Restart()
                            Using deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token)
                                deadline.CancelAfter(candidate.TimeoutMilliseconds)
                                Try
                                    Await FireModbusClient.ReadConnectedAsync(candidate, client.GetStream(), deadline.Token).ConfigureAwait(False)
                                Catch ex As OperationCanceledException When Not _lifetime.IsCancellationRequested
                                    Throw New TimeoutException("FC04 response timed out after " & candidate.TimeoutMilliseconds.ToString(Globalization.CultureInfo.InvariantCulture) & " ms.")
                                End Try
                            End Using
                            SyncLock _gate
                                _testedMappings.Add(FireConnectionStore.MappingIdentity(candidate))
                            End SyncLock
                            test.Protocol.State = "RESPONSE RECEIVED"
                            test.Protocol.Detail = "Valid FC04 framing; not verification of register definitions"
                            test.RegisterMapping = "REVIEWED REFERENCE - INTERPRETATION NOT VERIFIED"
                            test.PrimaryError = "MODBUS RESPONSE RECEIVED - PANEL STATUS INTERPRETATION NOT VERIFIED"
                            test.FailureStage = "REGISTER INTERPRETATION"
                        Catch ex As Exception When IsCommunicationError(ex)
                            SyncLock _gate
                                _testedMappings.Remove(FireConnectionStore.MappingIdentity(candidate))
                            End SyncLock
                            test.Protocol.State = If(test.FailureStage = "REGISTER CONFIGURATION", "NOT TESTED", If(TypeOf ex Is TimeoutException, "TIMEOUT", "FAILED"))
                            test.Protocol.Detail = CommunicationError(ex)
                            If TypeOf ex Is SocketException Then test.Protocol.NativeErrorCode = DirectCast(ex, SocketException).NativeErrorCode
                            test.PrimaryError = If(test.FailureStage = "REGISTER CONFIGURATION", "REGISTER CONFIGURATION INVALID", "MODBUS FC04 " & test.Protocol.State) & " - " & test.Protocol.Detail
                        Finally
                            If test.Protocol.TimestampUtc.HasValue Then test.Protocol.ResponseMilliseconds = watch.Elapsed.TotalMilliseconds
                        End Try
                    End If
                End Using
            Finally
                _communicationGate.Release()
            End Try
            CompleteTest(test)
            Return test.Summary()
        Finally
            _commands.Release()
        End Try
    End Function

    Private Shared Async Function TestReachabilityAsync(host As String, timeoutMilliseconds As Integer, token As CancellationToken) As Task(Of FireDiagnosticStage)
        Dim stage As New FireDiagnosticStage With {.TimestampUtc = DateTime.UtcNow}
        Dim watch = Stopwatch.StartNew()
        Try
            Using ping As New Ping()
                Dim reply = Await ping.SendPingAsync(host, TimeSpan.FromMilliseconds(Math.Min(timeoutMilliseconds, 3000)), cancellationToken:=token).ConfigureAwait(False)
                If reply.Status = IPStatus.Success Then
                    stage.State = "SUCCESS"
                    stage.ResponseMilliseconds = reply.RoundtripTime
                    Return stage
                End If
                stage.State = If(reply.Status = IPStatus.TimedOut, "TIMEOUT", "FAILED")
                stage.Detail = reply.Status.ToString()
            End Using
        Catch ex As PingException
            Dim socket = TryCast(ex.InnerException, SocketException)
            stage.State = If(socket IsNot Nothing, ClassifySocketError(socket.SocketErrorCode), "FAILED")
            stage.Detail = If(socket IsNot Nothing, socket.Message, ex.Message)
            stage.NativeErrorCode = If(socket IsNot Nothing, CType(socket.NativeErrorCode, Integer?), Nothing)
        Catch ex As SocketException
            stage.State = ClassifySocketError(ex.SocketErrorCode)
            stage.Detail = ex.Message
            stage.NativeErrorCode = ex.NativeErrorCode
        Finally
            If Not stage.ResponseMilliseconds.HasValue Then stage.ResponseMilliseconds = watch.Elapsed.TotalMilliseconds
        End Try
        Return stage
    End Function

    Private Shared Async Function TestTransportAsync(client As TcpClient, settings As FireConnectionSettings, test As FireConnectionTestResult, cancellationToken As CancellationToken) As Task(Of FireDiagnosticStage)
        Dim stage As New FireDiagnosticStage With {.TimestampUtc = DateTime.UtcNow}
        Dim address As IPAddress = Nothing
        If IPAddress.TryParse(settings.Host(), address) Then
            test.DestinationAddress = address.ToString()
            Try
                ' UDP Connect selects a local route without sending any datagram.
                Using route As New Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
                    route.Connect(New IPEndPoint(address, settings.Port()))
                    DescribeSource(test, DirectCast(route.LocalEndPoint, IPEndPoint).Address, "OS route selection only; TCP not established")
                End Using
            Catch ex As SocketException
                test.SourceEvidence = "Route unavailable: " & ClassifySocketError(ex.SocketErrorCode) & " (" & ex.NativeErrorCode.ToString(Globalization.CultureInfo.InvariantCulture) & ")"
            End Try
        End If
        Dim watch = Stopwatch.StartNew()
        Using deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            deadline.CancelAfter(settings.TimeoutMilliseconds)
            Try
                Await client.ConnectAsync(settings.Host(), settings.Port(), deadline.Token).ConfigureAwait(False)
                stage.State = "CONNECTED"
                Dim remoteAddress = DirectCast(client.Client.RemoteEndPoint, IPEndPoint).Address
                test.DestinationAddress = If(remoteAddress.IsIPv4MappedToIPv6, remoteAddress.MapToIPv4(), remoteAddress).ToString()
                DescribeSource(test, DirectCast(client.Client.LocalEndPoint, IPEndPoint).Address, "Observed established TCP socket")
            Catch ex As OperationCanceledException When Not cancellationToken.IsCancellationRequested
                stage.State = "TIMEOUT"
                stage.Detail = "Configured timeout: " & settings.TimeoutMilliseconds.ToString(Globalization.CultureInfo.InvariantCulture) & " ms"
            Catch ex As SocketException
                stage.State = ClassifySocketError(ex.SocketErrorCode)
                stage.NativeErrorCode = ex.NativeErrorCode
                stage.Detail = ex.SocketErrorCode.ToString() & " (" & ex.NativeErrorCode.ToString(Globalization.CultureInfo.InvariantCulture) & "): " & ex.Message
            Finally
                stage.ResponseMilliseconds = watch.Elapsed.TotalMilliseconds
            End Try
        End Using
        Return stage
    End Function

    Private Shared Sub DescribeSource(test As FireConnectionTestResult, address As IPAddress, evidence As String)
        If address.IsIPv4MappedToIPv6 Then address = address.MapToIPv4()
        test.SourceAddress = address.ToString()
        test.SourceEvidence = evidence
        Try
            Dim adapter = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(
                Function(item) item.GetIPProperties().UnicastAddresses.Any(Function(unicast) unicast.Address.Equals(address)))
            test.SourceInterface = If(adapter Is Nothing, "Interface not identified for " & address.ToString(), adapter.Name & " [" & adapter.Id & "]")
        Catch ex As NetworkInformationException
            test.SourceInterface = "Interface lookup failed (" & ex.ErrorCode.ToString(Globalization.CultureInfo.InvariantCulture) & "): " & ex.Message
        End Try
    End Sub

    Public Shared Function ClassifySocketError(errorCode As SocketError) As String
        Select Case errorCode
            Case SocketError.ConnectionRefused : Return "REFUSED"
            Case SocketError.TimedOut : Return "TIMEOUT"
            Case SocketError.NetworkUnreachable, SocketError.HostUnreachable, SocketError.NetworkDown, SocketError.HostDown : Return "NETWORK UNREACHABLE"
            Case SocketError.HostNotFound, SocketError.NoData, SocketError.TryAgain, SocketError.NoRecovery : Return "DNS FAILURE"
            Case Else : Return "FAILED"
        End Select
    End Function

    Private Sub CompleteTest(test As FireConnectionTestResult)
        test.CompletedUtc = DateTime.UtcNow
        SyncLock _gate
            _snapshot.LastTest = test.Copy()
            _snapshot.LastTestResult = test.Summary()
        End SyncLock
        Record(test.Summary(), test)
    End Sub

    Public Function DiagnosticExport() As String
        Dim status As FireConnectionSnapshot
        Dim settings As FireConnectionSettings
        SyncLock _gate
            status = ReadStatus()
            settings = _settings.Copy()
        End SyncLock
        Dim now = DateTime.UtcNow
        Dim freshness = If(Not status.Monitoring, "UNAVAILABLE (disconnected)",
            If(status.State = "ONLINE" AndAlso status.LastDataUtc.HasValue AndAlso
               (now - status.LastDataUtc.Value).TotalSeconds <= settings.FreshnessSeconds,
               If(settings.ConnectionType = FireConnectionType.ZetaCloud, "FRESH (historical event synchronization only)", "FRESH (raw registers)"), "STALE / UNAVAILABLE"))
        Return JsonSerializer.Serialize(New With {
            .SchemaVersion = 1, .ExportedUtc = now, .ReadOnly = True,
            .LastConnectionTest = status.LastTest,
            .LastTestPrimaryResult = If(status.LastTest Is Nothing, "No test performed.", status.LastTest.PrimaryError),
            .CloudEventConnector = New With {.Authentication = status.CloudAuthentication, .HttpStatus = status.HttpStatus,
                .SessionExpiresUtc = status.CloudSessionExpiresUtc,
                .CredentialRenewalRequired = status.CloudCredentialRenewalRequired, .TransportResponseReceived = status.CloudTransportSucceeded,
                .ErrorOrigin = status.CloudErrorOrigin, .ServerErrorCategory = status.CloudServerErrorCategory,
                .EventQuery = "start=UTC today minus 30 days; end=UTC today",
                .ResponseStage = status.CloudStage, .LastSynchronizationUtc = status.LastDataUtc, .LastReturnedEventUtc = status.LastEventUtc,
                .LiveFirePanelCondition = "UNKNOWN"},
            .ActiveMonitoring = New With {
                .ConnectionType = settings.ConnectionType.ToString(), .Target = status.Endpoint,
                .State = If(status.Monitoring, status.State, "DISCONNECTED"), .Monitoring = status.Monitoring,
                .ProtocolHealth = status.ProtocolHealth, .TransportHealth = status.TransportHealth,
                .LastValidResponseUtc = status.LastDataUtc, .ResponseMilliseconds = status.ResponseMilliseconds,
                .DataFreshness = freshness, .FireStatus = "UNKNOWN",
                .ConsecutiveFailures = status.ConsecutiveFailures, .LastCommunicationError = status.LastError,
                .CommissioningRequirement = settings.UnavailableReason()},
            .EventLogError = status.EventLogError, .Events = status.Events},
            New JsonSerializerOptions With {.WriteIndented = True})
    End Function

    Public Async Function ExportDiagnosticsAsync(path As String) As Task
        ValidateExportPath(path)
        Dim destination = IO.Path.GetFullPath(path)
        Dim content = DiagnosticExport()
        Dim temporary = destination & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Try
            Await File.WriteAllTextAsync(temporary, content).ConfigureAwait(False)
            File.Move(temporary, destination, True)
        Finally
            If File.Exists(temporary) Then File.Delete(temporary)
        End Try
    End Function

    Public Sub ValidateExportPath(path As String)
        Dim destination = IO.Path.GetFullPath(path)
        If String.Equals(destination, IO.Path.GetFullPath(_store.FilePath), StringComparison.OrdinalIgnoreCase) OrElse
            String.Equals(destination, IO.Path.GetFullPath(_store.FilePath & ".events.jsonl"), StringComparison.OrdinalIgnoreCase) OrElse
            String.Equals(destination, IO.Path.GetFullPath(_store.FilePath & ".events.jsonl.previous"), StringComparison.OrdinalIgnoreCase) OrElse
            destination.StartsWith(IO.Path.GetFullPath(_store.FilePath & ".cloud-events.db"), StringComparison.OrdinalIgnoreCase) OrElse
            destination.StartsWith(IO.Path.GetFullPath(_store.FilePath & ".map-alerts"), StringComparison.OrdinalIgnoreCase) Then
            Throw New InvalidOperationException("Export to a separate file, not the protected fire configuration, map alerts, diagnostic log or historical event database.")
        End If
    End Sub

    Private Sub RequireCommunication()
        If _disposed Then Throw New ObjectDisposedException(NameOf(FireConnectionService))
        If Not _allowCommunication Then Throw New InvalidOperationException("Fire communication is disabled in this preview/test session.")
        If _loadError.Length > 0 Then Throw New InvalidOperationException(_loadError)
    End Sub

    Private Sub StartWorker()
        If Not _allowCommunication OrElse _disposed Then Return
        Dim reason = If(_loadError.Length > 0, _loadError, ReadSettings().UnavailableReason())
        If reason.Length = 0 AndAlso ReadSettings().IsModbus() AndAlso Not FireConnectionStore.HasMappingProof(ReadSettings()) Then reason = "Mapping has no valid successful FC04 test evidence. Review documentation, Test Connection and Save before connecting."
        If reason.Length = 0 AndAlso ReadSettings().ConnectionType = FireConnectionType.ZetaCloud AndAlso Not ReadStatus().HasCloudToken AndAlso Not ReadStatus().HasSavedCloudLogin Then reason = "Sign in to Zeta or save a cloud token; old unsupported Cloud credentials are not reused."
        If reason.Length > 0 Then
            SetStopped(StateForSettings(ReadSettings()), reason)
            Return
        End If
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token)
        _lastStoppedMessage = String.Empty
        Dim token = _cancellation.Token
        Dim settings = ReadSettings()
        SyncLock _gate
            _snapshot.Monitoring = True
            _snapshot.State = "CONNECTING"
            _snapshot.ProtocolHealth = "CONNECTING"
            _snapshot.TransportHealth = "CONNECTING"
            _snapshot.LastDataUtc = Nothing
            _snapshot.LastError = String.Empty
        End SyncLock
        Record("Monitoring started: read-only " & If(settings.ConnectionType = FireConnectionType.ZetaCloud, "cloud event GET", "FC04") & ". Fire panel operating configuration is unchanged.")
        _worker = Task.Run(Function() If(settings.ConnectionType = FireConnectionType.ZetaCloud, MonitorCloudAsync(settings, token), MonitorAsync(settings, token)))
    End Sub

    Private Shared Function StateForSettings(settings As FireConnectionSettings) As String
        If settings.ConnectionType = FireConnectionType.ZetaCloud AndAlso settings.CloudApiEndpoint = ZetaCloudEventClient.EventRoute Then Return "NOT CONFIGURED"
        If settings.ConnectionType = FireConnectionType.ZetaCloud OrElse settings.ConnectionType = FireConnectionType.BmsVendor OrElse
            (settings.IsModbus() AndAlso Not String.IsNullOrWhiteSpace(settings.Host()) AndAlso
             (Not settings.MappingVerified OrElse Not FireConnectionStore.HasMappingProof(settings))) Then
            Return "PROTOCOL NOT VERIFIED"
        End If
        Return "NOT CONFIGURED"
    End Function

    Private Async Function StopWorkerAsync() As Task
        If _cancellation Is Nothing Then Return
        _cancellation.Cancel()
        Try
            Await _worker.ConfigureAwait(False)
        Catch ex As OperationCanceledException When _cancellation.IsCancellationRequested
            ' Cancellation closes pending connect/read and retry delays.
        Finally
            _cancellation.Dispose()
            _cancellation = Nothing
            _worker = Nothing
            SyncLock _gate
                _snapshot.Monitoring = False
                _snapshot.ConnectedSinceUtc = Nothing
            End SyncLock
        End Try
    End Function

    Private Async Function MonitorAsync(settings As FireConnectionSettings, token As CancellationToken) As Task
        Dim failures = 0
        Dim lastLoggedFailure As String = String.Empty
        Try
            While True
                token.ThrowIfCancellationRequested()
                Dim successful = False
                Dim watch = Stopwatch.StartNew()
                Try
                    Await _communicationGate.WaitAsync(token).ConfigureAwait(False)
                    Try
                        Await FireModbusClient.ReadAsync(settings, token).ConfigureAwait(False)
                    Finally
                        _communicationGate.Release()
                    End Try
                    Dim now = DateTime.UtcNow
                    Dim recovered As Boolean
                    SyncLock _gate
                        recovered = _snapshot.State <> "ONLINE" OrElse (_snapshot.LastDataUtc.HasValue AndAlso
                            (now - _snapshot.LastDataUtc.Value).TotalSeconds > settings.FreshnessSeconds)
                        If recovered OrElse Not _snapshot.ConnectedSinceUtc.HasValue Then _snapshot.ConnectedSinceUtc = now
                        _snapshot.LastSuccessUtc = now
                        _snapshot.LastDataUtc = now
                        _snapshot.ResponseMilliseconds = watch.Elapsed.TotalMilliseconds
                        _snapshot.ConsecutiveFailures = 0
                        _snapshot.LastError = String.Empty
                        _snapshot.State = "ONLINE"
                        _snapshot.ProtocolHealth = "FC04 RESPONSE VALID (raw data only)"
                        _snapshot.TransportHealth = "CONNECTED"
                    End SyncLock
                    If recovered Then Record("ONLINE: valid fresh FC04 monitoring data received. This is not a fire NORMAL indication.")
                    successful = True
                    failures = 0
                    lastLoggedFailure = String.Empty
                Catch ex As Exception When IsCommunicationError(ex)
                    failures += 1
                    SyncLock _gate
                        _snapshot.ConsecutiveFailures = failures
                        _snapshot.ConnectedSinceUtc = Nothing
                        _snapshot.LastError = CommunicationError(ex) & " COMMUNICATION LOST - FIRE STATUS UNKNOWN."
                        _snapshot.State = "OFFLINE"
                        _snapshot.ProtocolHealth = "FAILED"
                        _snapshot.TransportHealth = If(TypeOf ex Is SocketException,
                            ClassifySocketError(DirectCast(ex, SocketException).SocketErrorCode), "UNKNOWN (read failed)")
                    End SyncLock
                    Dim failure = CommunicationError(ex)
                    If failure <> lastLoggedFailure Then
                        Record("OFFLINE: " & failure)
                        lastLoggedFailure = failure
                    End If
                End Try
                If Not successful AndAlso Not settings.AutoReconnect AndAlso failures > settings.RetryCount Then
                    SyncLock _gate
                        _snapshot.Monitoring = False
                    End SyncLock
                    Record("Retry limit reached. Automatic reconnection is disabled; use Connect to retry.")
                    Return
                End If
                Dim delay = If(successful, settings.PollSeconds, settings.RetryIntervalSeconds)
                Await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(False)
            End While
        Catch ex As OperationCanceledException When token.IsCancellationRequested
            Return
        End Try
    End Function

    Private Shared Function IsCommunicationError(ex As Exception) As Boolean
        Return TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is SocketException OrElse TypeOf ex Is TimeoutException
    End Function

    Private Shared Function CommunicationError(ex As Exception) As String
        If TypeOf ex Is TimeoutException Then Return ex.Message
        If TypeOf ex Is InvalidDataException Then Return ex.Message
        If TypeOf ex Is EndOfStreamException Then Return "Monitoring interface closed before the complete FC04 response arrived."
        If TypeOf ex Is SocketException Then
            Dim socket = DirectCast(ex, SocketException)
            Return "TCP CONNECTION " & ClassifySocketError(socket.SocketErrorCode) & " (" &
                socket.NativeErrorCode.ToString(Globalization.CultureInfo.InvariantCulture) & "; " & socket.SocketErrorCode.ToString() & "): " & socket.Message
        End If
        Return ex.Message
    End Function

    Private Sub DescribeSettings()
        _snapshot.ConnectionType = _settings.ConnectionType
        Dim host = _settings.Host()
        If host.Contains(":"c) Then host = "[" & host & "]"
        _snapshot.Endpoint = If(_settings.ConnectionType = FireConnectionType.ZetaCloud, _settings.CloudUrl,
            host & ":" & _settings.Port().ToString(Globalization.CultureInfo.InvariantCulture))
    End Sub

    Private Sub SetStopped(state As String, message As String, Optional logEvent As Boolean = True)
        Dim changed = _lastStoppedMessage <> message
        SyncLock _gate
            _snapshot.State = state
            _snapshot.Monitoring = False
            _snapshot.ConnectedSinceUtc = Nothing
            _snapshot.ProtocolHealth = "DISCONNECTED"
            _snapshot.TransportHealth = "DISCONNECTED"
        End SyncLock
        _lastStoppedMessage = message
        If logEvent AndAlso changed Then Record(message)
    End Sub

    Private Sub Record(message As String, Optional testResult As FireConnectionTestResult = Nothing)
        If message.Length = 0 Then Return
        Dim entry As New FireConnectionEvent With {.TimestampUtc = DateTime.UtcNow, .Message = message, .TestResult = testResult?.Copy()}
        SyncLock _gate
            If testResult Is Nothing AndAlso _snapshot.Events.Count > 0 AndAlso _snapshot.Events.Last().Message = message Then Return
            _snapshot.Events = _snapshot.Events.Concat({entry}).TakeLast(100).ToArray()
            Dim path = _store.FilePath & ".events.jsonl"
            Try
                Directory.CreateDirectory(IO.Path.GetDirectoryName(path))
                If File.Exists(path) AndAlso New FileInfo(path).Length > 262144 Then File.Move(path, path & ".previous", True)
                File.AppendAllText(path, JsonSerializer.Serialize(entry) & Environment.NewLine)
                _snapshot.EventLogError = _eventReadError
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                _snapshot.EventLogError = "Connection event logging failed. Check local configuration directory access and available storage."
            End Try
        End SyncLock
    End Sub

    Private Sub LoadEvents()
        Dim path = _store.FilePath & ".events.jsonl"
        Dim previous = path & ".previous"
        If Not File.Exists(path) AndAlso Not File.Exists(previous) Then Return
        Try
            Dim lines As IEnumerable(Of String) = Array.Empty(Of String)()
            If File.Exists(previous) Then lines = File.ReadLines(previous)
            If File.Exists(path) Then lines = lines.Concat(File.ReadLines(path))
            Dim events = lines.TakeLast(100).Select(Function(line) JsonSerializer.Deserialize(Of FireConnectionEvent)(line)).ToArray()
            If events.Any(Function(entry) entry Is Nothing OrElse String.IsNullOrEmpty(entry.Message)) Then Throw New InvalidDataException("Invalid fire connection event log.")
            If events.Any(Function(entry) entry.TestResult IsNot Nothing AndAlso
                (entry.TestResult.Host Is Nothing OrElse entry.TestResult.PrimaryError Is Nothing OrElse
                 entry.TestResult.Reachability Is Nothing OrElse entry.TestResult.Transport Is Nothing OrElse entry.TestResult.Protocol Is Nothing OrElse
                 entry.TestResult.Reachability.State Is Nothing OrElse entry.TestResult.Reachability.Detail Is Nothing OrElse
                 entry.TestResult.Transport.State Is Nothing OrElse entry.TestResult.Transport.Detail Is Nothing OrElse
                 entry.TestResult.Protocol.State Is Nothing OrElse entry.TestResult.Protocol.Detail Is Nothing OrElse
                 entry.TestResult.SourceAddress Is Nothing OrElse entry.TestResult.SourceInterface Is Nothing OrElse
                 entry.TestResult.SourceEvidence Is Nothing OrElse entry.TestResult.DestinationAddress Is Nothing OrElse entry.TestResult.FailureStage Is Nothing OrElse
                 Not entry.TestResult.TimestampUtc.HasValue)) Then Throw New InvalidDataException("Invalid structured connection test event.")
            _snapshot.Events = events
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse
            TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is JsonException
            _eventReadError = "Cannot read previous fire connection events. Check the local event log format and access permissions."
            _snapshot.EventLogError = _eventReadError
        End Try
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If _disposed Then Return
        _disposed = True
        _lifetime.Cancel()
        If _cancellation IsNot Nothing Then _cancellation.Cancel()
        _cloudClient.Dispose()
    End Sub
End Class
