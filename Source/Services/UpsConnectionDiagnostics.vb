Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Globalization
Imports System.Linq
Imports System.Net
Imports System.Net.NetworkInformation
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports Lextm.SharpSnmpLib
Imports Lextm.SharpSnmpLib.Messaging

' What a layered connection test could actually establish. Only evidence-backed categories are used.
Public Enum UpsConnectionFinding
    NotTested = 0
    Connected = 1
    ConnectedWithoutUpsMib = 2
    AuthenticationRejected = 3
    AgentAliveRequestUnanswered = 4
    NoSnmpResponseHostUp = 5
    PortUnreachable = 6
    HostNotResponding = 7
    ConfigurationError = 8
    Cancelled = 9
    ' The same authorized credential is answered with the other community-based version (v1 vs v2c): confirmed misconfiguration.
    WrongVersionConfirmed = 10
    AccessDenied = 11
    NetworkUnreachable = 12
End Enum

Public NotInheritable Class UpsConnectionTest
    Public Property DeviceId As String = String.Empty
    Public Property Label As String = String.Empty
    Public Property AtsBank As Integer
    Public Property IpAddress As String = String.Empty
    Public Property Port As Integer = 161
    Public Property ProfileName As String = String.Empty
    Public Property ProfileVersion As String = String.Empty
    Public Property ConnectionStamp As String = String.Empty
    Public Property TimeoutMilliseconds As Integer
    Public Property Retries As Integer
    Public Property TimestampUtc As DateTime
    Public Property SourceAddress As String = String.Empty
    Public Property PingReplies As Integer
    Public Property PingAttempts As Integer
    Public Property PingMilliseconds As Double?
    Public Property HttpPortOpen As Boolean?
    Public Property SnmpResponds As Boolean
    Public Property SnmpQuality As UpsQuality = UpsQuality.NoData
    Public Property SnmpDetail As String = String.Empty
    Public Property LatencyMilliseconds As Double?
    Public Property SysName As String = String.Empty
    Public Property SysDescr As String = String.Empty
    Public Property SysObjectId As String = String.Empty
    Public Property Manufacturer As String = String.Empty
    Public Property Model As String = String.Empty
    Public Property SupportsUpsMib As Boolean
    Public Property MappingVerified As Boolean
    ' Credential-free SNMPv3 engine discovery: proves whether an SNMP agent is listening at all.
    Public Property EngineProbe As String = "not run"
    Public Property EngineId As String = String.Empty
    Public Property EngineBoots As Integer?
    Public Property Finding As UpsConnectionFinding
    Public Property Summary As String = String.Empty
    Public Property PossibleCauses As IReadOnlyList(Of String) = Array.Empty(Of String)()
    Public Property ElapsedMilliseconds As Double
    Public Property HistoryError As String = String.Empty
    ' v1 <-> v2c cross-check with the SAME configured credential (never another credential).
    Public Property AlternateVersion As String = String.Empty
    Public Property AlternateVersionResult As String = String.Empty

    ' Protocol version that actually produced an SNMP response during this test.
    Public ReadOnly Property RespondingVersion As String
        Get
            If SnmpResponds Then Return ProfileVersion
            If Finding = UpsConnectionFinding.WrongVersionConfirmed Then Return AlternateVersion & " (profile says " & ProfileVersion & ")"
            If Finding = UpsConnectionFinding.AuthenticationRejected Then Return "v3 (request rejected)"
            If EngineProbe = "reply" Then Return "none (agent alive, v3 discovery only)"
            Return "none"
        End Get
    End Property

    Public ReadOnly Property IsConnected As Boolean
        Get
            Return Finding = UpsConnectionFinding.Connected OrElse Finding = UpsConnectionFinding.ConnectedWithoutUpsMib
        End Get
    End Property
End Class

' One object of the OID map read individually from a device (read-only GET).
Public NotInheritable Class UpsOidCheck
    Public Property Key As String = String.Empty
    Public Property Oid As String = String.Empty
    Public Property Description As String = String.Empty
    Public Property Status As String = String.Empty
    Public Property RawValue As String = String.Empty
    Public Property ScaledValue As String = String.Empty
    Public Property Unit As String = String.Empty
    Public Property Present As Boolean
    Public Property InRange As Boolean
End Class

Public NotInheritable Class UpsOidAudit
    Public Property DeviceId As String = String.Empty
    Public Property IpAddress As String = String.Empty
    Public Property TimestampUtc As DateTime
    Public Property SysObjectId As String = String.Empty
    Public Property Model As String = String.Empty
    Public Property Checks As IReadOnlyList(Of UpsOidCheck) = Array.Empty(Of UpsOidCheck)()
    Public Property Aborted As String = String.Empty
    ' The capability probe behind this audit (groups, verified table indices, unsupported OIDs).
    Public Property Capability As UpsCapabilityProfile

    ' Objects the dashboard needs before the mapping can be called verified for this card.
    Public Shared ReadOnly CoreKeys As String() = {"OutputSource", "OutputFrequency", "OutputVoltage", "OutputCurrent", "OutputPower", "OutputLoad", "BatteryStatus"}

    Public ReadOnly Property PresentCount As Integer
        Get
            Return Checks.Where(Function(item) item.Present).Count()
        End Get
    End Property

    Public ReadOnly Property MappingVerified As Boolean
        Get
            If Aborted.Length > 0 Then Return False
            If Capability IsNot Nothing Then Return Capability.Complete AndAlso Capability.CoreMapped
            Return CoreKeys.All(Function(key) Checks.Any(Function(item) item.Key = key AndAlso item.Present AndAlso item.InRange))
        End Get
    End Property
End Class

' Layered, read-only connectivity diagnostics for the UPS fleet. Only GET requests and credential-free SNMPv3
' engine discovery are sent; nothing is ever written to a card and no credential is guessed.
Friend Module UpsConnectionDiagnostics
    Private ReadOnly Tests As New ConcurrentDictionary(Of String, UpsConnectionTest)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly Audits As New ConcurrentDictionary(Of String, UpsOidAudit)(StringComparer.OrdinalIgnoreCase)
    Public Const MaxParallelTests As Integer = 4

    Public Function LastTest(deviceId As String) As UpsConnectionTest
        Dim result As UpsConnectionTest = Nothing
        Tests.TryGetValue(deviceId, result)
        If result IsNot Nothing Then
            Dim device = UpsSettingsStore.Current.Devices.FirstOrDefault(Function(item) item.Id = deviceId)
            Try
                If device Is Nothing OrElse result.ConnectionStamp <> SnmpCredentialStore.ConnectionStamp(SnmpCredentialStore.EffectiveDevice(device)) Then Return Nothing
            Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is IO.InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
                Trace.TraceError("Cannot validate prior UPS connection evidence: " & ex.Message)
                Return Nothing
            End Try
        End If
        Return result
    End Function

    Public Function LastAudit(deviceId As String) As UpsOidAudit
        Dim result As UpsOidAudit = Nothing
        Audits.TryGetValue(deviceId, result)
        If result IsNot Nothing Then
            Dim device = UpsSettingsStore.Current.Devices.FirstOrDefault(Function(item) item.Id = deviceId)
            If device Is Nothing OrElse UpsCapabilityProbe.CurrentEvidence(device) IsNot result.Capability Then Return Nothing
        End If
        Return result
    End Function

    Public Function AllTests() As IReadOnlyList(Of UpsConnectionTest)
        Return Tests.Values.ToList()
    End Function

    Public Function Describe(finding As UpsConnectionFinding) As String
        Select Case finding
            Case UpsConnectionFinding.Connected : Return "SNMP Responding"
            Case UpsConnectionFinding.ConnectedWithoutUpsMib : Return "Connected – no UPS-MIB"
            Case UpsConnectionFinding.AuthenticationRejected : Return "Confirmed Authentication Error (USM report)"
            Case UpsConnectionFinding.AccessDenied : Return "Access Denied (SNMP error response)"
            Case UpsConnectionFinding.NetworkUnreachable : Return "Network Unreachable (socket error)"
            Case UpsConnectionFinding.AgentAliveRequestUnanswered : Return "SNMP Timeout – Cause Unknown (agent discovery answered)"
            Case UpsConnectionFinding.NoSnmpResponseHostUp : Return "SNMP Timeout – Cause Unknown (host up)"
            Case UpsConnectionFinding.PortUnreachable : Return "UDP port unreachable"
            Case UpsConnectionFinding.HostNotResponding : Return "SNMP Timeout – Cause Unknown (no network replies)"
            Case UpsConnectionFinding.ConfigurationError : Return "Configuration error"
            Case UpsConnectionFinding.WrongVersionConfirmed : Return "Wrong SNMP version (confirmed)"
            Case UpsConnectionFinding.Cancelled : Return "Cancelled"
            Case Else : Return "Not tested"
        End Select
    End Function

    ' ---------- Connection test ----------
    Public Function TestDevice(device As UpsDevice, token As CancellationToken) As UpsConnectionTest
        Dim watch = Stopwatch.StartNew()
        Dim result As New UpsConnectionTest With {
            .DeviceId = device.Id, .Label = device.Label, .AtsBank = device.AtsBank, .IpAddress = device.IpAddress.Trim(), .Port = device.Port,
            .ProfileName = device.CredentialProfile, .TimeoutMilliseconds = device.TimeoutMilliseconds, .Retries = device.Retries,
            .TimestampUtc = DateTime.UtcNow}
        Try
            Dim address As IPAddress = Nothing
            If Not IPAddress.TryParse(result.IpAddress, address) OrElse address.AddressFamily <> AddressFamily.InterNetwork Then
                result.Finding = UpsConnectionFinding.ConfigurationError
                result.Summary = "Invalid IPv4 address '" & device.IpAddress & "'."
                Return Store(result, watch)
            End If
            token.ThrowIfCancellationRequested()
            RunPing(result, address, token)
            token.ThrowIfCancellationRequested()
            device = SnmpCredentialStore.EffectiveDevice(device)
            result.Port = device.Port
            result.TimeoutMilliseconds = device.TimeoutMilliseconds
            result.Retries = device.Retries
            result.ConnectionStamp = SnmpCredentialStore.ConnectionStamp(device)
            Dim credential = SnmpCredentialStore.Resolve(device.CredentialProfile)
            If credential Is Nothing Then
                result.Finding = UpsConnectionFinding.ConfigurationError
                result.Summary = "Credentials Not Configured: profile '" & device.CredentialProfile & "' does not exist."
                result.PossibleCauses = {"Correct the registry entry or create the credential profile on the Credentials tab."}
                Return Store(result, watch)
            End If
            result.ProfileVersion = credential.Version
            Dim target As New IPEndPoint(address, device.Port)
            result.SourceAddress = SourceAddressFor(target)

            ' Configured credential, exactly as the poller uses it (identity objects only).
            Dim probe = UpsAcquisitionService.Probe(result.IpAddress, device.Port, device.CredentialProfile, device.TimeoutMilliseconds, device.Retries)
            result.SnmpResponds = probe.Responds
            result.SnmpQuality = probe.Quality
            result.SnmpDetail = probe.Detail
            result.LatencyMilliseconds = probe.RoundTripMilliseconds
            result.SysName = probe.SysName
            result.SysDescr = probe.SysDescr
            result.SysObjectId = probe.SysObjectId
            result.Manufacturer = probe.Manufacturer
            result.Model = probe.Model
            result.SupportsUpsMib = probe.SupportsUpsMib

            If probe.Responds Then
                result.Finding = If(probe.SupportsUpsMib, UpsConnectionFinding.Connected, UpsConnectionFinding.ConnectedWithoutUpsMib)
                result.Summary = "SNMP " & credential.Version & " GET answered in " & FormatMs(probe.RoundTripMilliseconds) &
                                 " (sysName '" & probe.SysName & "', sysObjectID " & probe.SysObjectId & ")." &
                                 If(probe.SupportsUpsMib, "", " The agent does not expose the RFC 1628 UPS-MIB identity objects.")
                result.PossibleCauses = If(probe.SupportsUpsMib, Array.Empty(Of String)(),
                    New String() {"RFC 1628 UPS-MIB support disabled or not provided by this card/firmware.", "Device at this IP is not a UPS card (check identity)."})
                Dim audit = AuditOids(device, token)
                result.MappingVerified = audit.MappingVerified
                result.Summary &= " " & MappingText(audit) & "."
                Return Store(result, watch)
            End If

            If probe.Quality = UpsQuality.AuthFailure Then
                result.Finding = UpsConnectionFinding.AuthenticationRejected
                result.Summary = "The agent answered and rejected the SNMPv3 request: " & probe.Detail
                result.PossibleCauses = {"SNMPv3 user name, authentication or privacy settings differ from the card configuration."}
                Return Store(result, watch)
            End If
            If probe.Quality = UpsQuality.ConfigError Then
                result.Finding = UpsConnectionFinding.ConfigurationError
                result.Summary = probe.Detail
                result.PossibleCauses = {"Fix the credential profile on the Credentials tab."}
                Return Store(result, watch)
            End If
            If probe.Detail.Contains("noAccess", StringComparison.Ordinal) OrElse probe.Detail.Contains("authorizationError", StringComparison.Ordinal) Then
                result.Finding = UpsConnectionFinding.AccessDenied
                result.Summary = probe.Detail
                result.PossibleCauses = {"Card returned an access error. Check the SNMP view and allowed manager/source IP."}
                Return Store(result, watch)
            End If
            If probe.Detail = "Network error: NetworkUnreachable" OrElse probe.Detail = "Network error: HostUnreachable" Then
                result.Finding = UpsConnectionFinding.NetworkUnreachable
                result.Summary = probe.Detail
                result.PossibleCauses = {"The network stack reported an unreachable route/host. Verify routing and VLAN access."}
                Return Store(result, watch)
            End If

            token.ThrowIfCancellationRequested()
            RunEngineDiscovery(result, target, Math.Max(1500, device.TimeoutMilliseconds))
            token.ThrowIfCancellationRequested()
            If result.PingReplies = 0 Then result.HttpPortOpen = TcpOpen(address, 80, 1500)
            If result.Finding = UpsConnectionFinding.WrongVersionConfirmed Then Return Store(result, watch)
            Classify(result, credential.Version)
        Catch ex As OperationCanceledException
            result.Finding = UpsConnectionFinding.Cancelled
            result.Summary = "Test cancelled by the operator."
        Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is IO.InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
                                   TypeOf ex Is ArgumentException OrElse TypeOf ex Is SnmpException
            result.Finding = UpsConnectionFinding.ConfigurationError
            result.SnmpQuality = UpsQuality.ConfigError
            result.Summary = ex.Message
        End Try
        Return Store(result, watch)
    End Function

    Private Function Store(result As UpsConnectionTest, watch As Stopwatch) As UpsConnectionTest
        result.ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds
        If result.Finding <> UpsConnectionFinding.Cancelled AndAlso result.DeviceId.Length > 0 Then Tests(result.DeviceId) = result
        If result.DeviceId.Length > 0 Then
            Dim folder = IO.Path.GetDirectoryName(UpsSettingsStore.SettingsPath())
            Try
                IO.Directory.CreateDirectory(folder)
                SyncLock Tests
                    IO.File.AppendAllText(IO.Path.Combine(folder, "ups-connection-history.jsonl"),
                        System.Text.Json.JsonSerializer.Serialize(result) & Environment.NewLine)
                End SyncLock
            Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is UnauthorizedAccessException
                result.HistoryError = "Connection evidence could not be persisted: " & ex.Message
                result.Summary &= " " & result.HistoryError
                Trace.TraceError(result.HistoryError)
            End Try
        End If
        Return result
    End Function

    Private Sub Classify(result As UpsConnectionTest, version As String)
        Dim snmpText = "SNMP " & version & " GET (profile '" & result.ProfileName & "'): " & result.SnmpDetail &
                       If(result.AlternateVersion.Length > 0, " SNMP " & result.AlternateVersion & " with the same credential: " & result.AlternateVersionResult & ".", "")
        Dim hostUp = result.PingReplies > 0 OrElse result.HttpPortOpen.GetValueOrDefault()
        Select Case result.EngineProbe
            Case "reply"
                result.Finding = UpsConnectionFinding.AgentAliveRequestUnanswered
                result.Summary = snmpText & " The SNMP agent itself is running: it answered credential-free SNMPv3 engine discovery" &
                                 If(result.EngineId.Length > 0, " (engine " & result.EngineId & ")", "") & ". The configured " & version & " request was not answered."
                If version = "v3" Then
                    result.PossibleCauses = {
                        "EOEMS host " & result.SourceAddress & " not permitted in the card's SNMPv3 access/manager list.",
                        "SNMPv3 user not configured on this card (most cards answer this with a report, so the ACL is more likely)."}
                Else
                    result.PossibleCauses = {
                        "SNMP " & version & " disabled on this card (SNMPv3-only configuration).",
                        "Read community configured on the card differs from profile '" & result.ProfileName & "' (Liebert cards silently drop unknown communities).",
                        "EOEMS host " & result.SourceAddress & " not listed in the card's SNMP v1/v2c access (manager) table.",
                        "SNMPv3 discovery traverses UDP " & result.Port.ToString(CultureInfo.InvariantCulture) & "; this does not exclude protocol-specific filtering or manager/view ACLs."}
                End If
            Case "port unreachable"
                result.Finding = UpsConnectionFinding.PortUnreachable
                result.Summary = snmpText & " The host returned ICMP port unreachable for UDP " & result.Port.ToString(CultureInfo.InvariantCulture) & "."
                result.PossibleCauses = {
                    "No SNMP agent is listening: SNMP disabled on the card or the agent is not running.",
                    "Wrong SNMP port configured in the registry (registry uses " & result.Port.ToString(CultureInfo.InvariantCulture) & ")."}
            Case Else
                If hostUp Then
                    result.Finding = UpsConnectionFinding.NoSnmpResponseHostUp
                    result.Summary = snmpText & " The host answers " &
                                     If(result.PingReplies > 0, "ping", "HTTP") & ", but nothing answered on UDP " & result.Port.ToString(CultureInfo.InvariantCulture) &
                                     " for the configured request or for SNMPv3 engine discovery. Cause not yet determined."
                    result.PossibleCauses = {
                        "SNMP agent disabled on the card.",
                        "Card drops every request from unlisted managers (EOEMS host " & result.SourceAddress & " not in the access table).",
                        "UDP " & result.Port.ToString(CultureInfo.InvariantCulture) & " filtered between the EOEMS subnet and this device (router/VLAN ACL).",
                        "Different card type or firmware without an SNMP agent, or this IP is not the UPS card."}
                Else
                    result.Finding = UpsConnectionFinding.HostNotResponding
                    result.Summary = snmpText & " No reply to ping, HTTP or SNMP from " & result.IpAddress & "."
                    result.PossibleCauses = {
                        "Card or UPS powered off, card not booted, or network cable/switch port down.",
                        "Wrong IP address in the registry (card re-addressed or DHCP).",
                        "Device on a different VLAN/subnet not routed from " & result.SourceAddress & "."}
                End If
        End Select
    End Sub

    Private Sub RunPing(result As UpsConnectionTest, address As IPAddress, token As CancellationToken)
        result.PingAttempts = 2
        Using ping As New Ping()
            For attempt = 1 To result.PingAttempts
                token.ThrowIfCancellationRequested()
                Try
                    Dim reply = ping.Send(address, 1000)
                    If reply.Status = IPStatus.Success Then
                        result.PingReplies += 1
                        If Not result.PingMilliseconds.HasValue OrElse reply.RoundtripTime < result.PingMilliseconds.Value Then result.PingMilliseconds = reply.RoundtripTime
                    End If
                Catch ex As PingException
                End Try
            Next
        End Using
    End Sub

    ' Sends the standard SNMPv3 discovery request (no user, no keys). Any SNMPv3-capable agent answers with a report.
    Private Sub RunEngineDiscovery(result As UpsConnectionTest, target As IPEndPoint, timeoutMs As Integer)
        Try
            Using client As New UdpClient(target.AddressFamily)
                client.Client.ReceiveTimeout = timeoutMs
                client.Connect(target)
                Dim payload = Messenger.GetNextDiscovery(SnmpType.GetRequestPdu).ToBytes()
                client.Send(payload, payload.Length)
                Dim remote As IPEndPoint = Nothing
                Dim reply = client.Receive(remote)
                result.EngineProbe = "reply"
                Try
                    Dim message = MessageFactory.ParseMessages(reply, New Lextm.SharpSnmpLib.Security.UserRegistry())(0)
                    result.EngineId = DescribeEngineId(message.Parameters.EngineId.GetRaw())
                    result.EngineBoots = message.Parameters.EngineBoots.ToInt32()
                Catch ex As Exception When TypeOf ex Is SnmpException OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is InvalidOperationException
                    result.EngineId = "unparsed reply"
                End Try
            End Using
        Catch ex As SocketException When ex.SocketErrorCode = SocketError.ConnectionReset
            result.EngineProbe = "port unreachable"
        Catch ex As SocketException When ex.SocketErrorCode = SocketError.TimedOut
            result.EngineProbe = "timeout"
        Catch ex As SocketException
            result.EngineProbe = "socket " & ex.SocketErrorCode.ToString()
        End Try
    End Sub

    ' RFC 3411 engine ID: 4-byte enterprise (high bit set) + format byte + data. Format 3 = MAC address.
    Private Function DescribeEngineId(raw As Byte()) As String
        If raw Is Nothing OrElse raw.Length = 0 Then Return String.Empty
        Dim hex = String.Concat(raw.Select(Function(item) item.ToString("X2", CultureInfo.InvariantCulture)))
        If raw.Length < 5 OrElse (raw(0) And &H80) = 0 Then Return hex
        Dim enterprise = ((raw(0) And &H7F) << 24) Or (CInt(raw(1)) << 16) Or (CInt(raw(2)) << 8) Or raw(3)
        Dim text = hex & " (enterprise " & enterprise.ToString(CultureInfo.InvariantCulture) & If(enterprise = 476, " Liebert/Vertiv", "")
        If raw(4) = 3 AndAlso raw.Length = 11 Then
            text &= ", MAC " & String.Join(":", raw.Skip(5).Select(Function(item) item.ToString("X2", CultureInfo.InvariantCulture)))
        End If
        Return text & ")"
    End Function

    Friend Function SourceAddressFor(target As IPEndPoint) As String
        Try
            Using probe As New Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
                probe.Connect(target)
                Return DirectCast(probe.LocalEndPoint, IPEndPoint).Address.ToString()
            End Using
        Catch ex As SocketException
            Return "no route (" & ex.SocketErrorCode.ToString() & ")"
        End Try
    End Function

    Private Function TcpOpen(address As IPAddress, port As Integer, timeoutMs As Integer) As Boolean
        Try
            Using client As New TcpClient(address.AddressFamily)
                Dim connect = client.ConnectAsync(address, port)
                Return connect.Wait(timeoutMs) AndAlso client.Connected
            End Using
        Catch ex As AggregateException
            Return False
        Catch ex As SocketException
            Return False
        End Try
    End Function

    ' Bounded parallel test; one silent unit never delays the others beyond its own timeout.
    Public Async Function TestManyAsync(devices As IReadOnlyList(Of UpsDevice), progress As IProgress(Of UpsConnectionTest),
                                        token As CancellationToken) As Task(Of IReadOnlyList(Of UpsConnectionTest))
        Dim results As New ConcurrentBag(Of UpsConnectionTest)()
        Dim concurrency = Math.Min(MaxParallelTests, UpsSettingsStore.Current.MaxConcurrentPolls)
        Using gate As New SemaphoreSlim(concurrency, concurrency)
            Dim work = devices.Select(Async Function(device)
                                          Try
                                              Await gate.WaitAsync(token).ConfigureAwait(False)
                                          Catch ex As OperationCanceledException
                                              Return
                                          End Try
                                          Try
                                              Dim captured = device.Clone()
                                              Dim result = Await Task.Run(Function() TestDevice(captured, token)).ConfigureAwait(False)
                                              results.Add(result)
                                              If result.IsConnected AndAlso UpsAcquisitionService.Current IsNot Nothing Then UpsAcquisitionService.Current.PollNow(device.Id)
                                              If progress IsNot Nothing Then progress.Report(result)
                                          Finally
                                              gate.Release()
                                          End Try
                                      End Function).ToList()
            Await Task.WhenAll(work).ConfigureAwait(False)
        End Using
        Return results.ToList()
    End Function

    ' ---------- OID / capability audit ----------
    ' Runs the grouped capability probe (the same one the poller uses and caches) and presents it as an audit.
    Public Function AuditOids(device As UpsDevice, token As CancellationToken) As UpsOidAudit
        Dim capability = UpsCapabilityProbe.Run(device, token)
        Dim audit As New UpsOidAudit With {
            .DeviceId = device.Id, .IpAddress = device.IpAddress, .TimestampUtc = capability.VerifiedUtc,
            .SysObjectId = capability.SysObjectId, .Model = capability.Model, .Checks = capability.Checks,
            .Aborted = capability.Aborted, .Capability = capability}
        Audits(device.Id) = audit
        Return audit
    End Function

    ' ---------- Known-good comparison ----------
    Public Function KnownGoodReference(devices As IEnumerable(Of UpsDevice)) As UpsConnectionTest
        Return devices.Where(Function(item) item.Verification = UpsVerification.Verified).Select(Function(item) LastTest(item.Id)).
            Where(Function(item) item IsNot Nothing AndAlso item.Finding = UpsConnectionFinding.Connected).
            OrderByDescending(Function(item) item.TimestampUtc).FirstOrDefault()
    End Function

    Public Function Compare(subject As UpsConnectionTest, reference As UpsConnectionTest) As IReadOnlyList(Of String)
        Dim lines As New List(Of String)()
        If subject Is Nothing Then
            lines.Add("Connection test not run for the selected UPS. Run 'Test Selected' before comparing with a known-good reference.")
            Return lines
        End If
        If reference Is Nothing Then
            lines.Add("No known-good reference tested in this session. Run 'Test All Registered' (a Verified unit must answer).")
            Return lines
        End If
        Dim row = Sub(name As String, a As String, b As String)
                      Dim same = String.Equals(a, b, StringComparison.OrdinalIgnoreCase)
                      lines.Add(If(same, "  same   ", "  DIFFERS") & "  " & name.PadRight(22) & " " & If(a, "").PadRight(34) & " | known-good: " & b)
                  End Sub
        lines.Add("Compared with known-good " & reference.DeviceId & " " & reference.IpAddress & " (tested " & reference.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) & ")")
        row("Credential profile", subject.ProfileName, reference.ProfileName)
        row("SNMP version", subject.ProfileVersion, reference.ProfileVersion)
        row("UDP port", subject.Port.ToString(CultureInfo.InvariantCulture), reference.Port.ToString(CultureInfo.InvariantCulture))
        row("EOEMS source IP", subject.SourceAddress, reference.SourceAddress)
        row("Ping", If(subject.PingReplies > 0, "reply", "no reply"), If(reference.PingReplies > 0, "reply", "no reply"))
        row("v3 engine discovery", If(subject.IsConnected, "not needed", subject.EngineProbe), "not needed")
        row("Configured GET", If(subject.SnmpResponds, "answered", "unanswered"), If(reference.SnmpResponds, "answered", "unanswered"))
        row("sysObjectID", If(subject.SysObjectId.Length > 0, subject.SysObjectId, "unknown"), reference.SysObjectId)
        row("UPS-MIB", If(subject.SnmpResponds, If(subject.SupportsUpsMib, "yes", "no"), "unknown"), If(reference.SupportsUpsMib, "yes", "no"))
        If subject.EngineId.Length > 0 Then lines.Add("  info     Engine ID " & subject.EngineId)
        Return lines
    End Function

    ' ---------- Operator checklist (card side; EOEMS never changes card settings) ----------
    Public Function Checklist(test As UpsConnectionTest) As IReadOnlyList(Of String)
        Dim source = If(test Is Nothing OrElse test.SourceAddress.Length = 0, "the EOEMS host", test.SourceAddress)
        Dim version = If(test Is Nothing OrElse test.ProfileVersion.Length = 0, "v1/v2c", test.ProfileVersion)
        Dim ip = If(test Is Nothing, "the registered IP", test.IpAddress)
        Return New String() {
            "[ ] Card web page › Communications/SNMP: SNMP protocol enabled.",
            "[ ] SNMP " & version & " enabled on the card (it may be configured SNMPv3-only).",
            "[ ] SNMPv1/v2c Access entry ENABLED with the approved read-only community and allowed source IP (trap destinations are NOT access entries).",
            "[ ] For v3: SNMPv3 User entry ENABLED; exact username, security level, authentication algorithm/password and privacy algorithm/password match the profile.",
            "[ ] Card/firmware supports the selected algorithms. Web administrator login credentials are separate from SNMP credentials.",
            "[ ] Approved READ-ONLY credential configured on the card and the same value stored in EOEMS profile '" & If(test Is Nothing, "default", test.ProfileName) & "'.",
            "[ ] " & source & " added to the card's SNMP access / manager table with read access.",
            "[ ] RFC 1628 UPS-MIB (and Liebert Global Products MIB) enabled on the card.",
            "[ ] SNMP agent on UDP 161; no router/VLAN ACL blocks UDP 161 from " & source & ".",
            "[ ] Device identity at " & ip & " confirmed (card display/label, sysName) – not swapped with another unit.",
            "[ ] Do not enable SNMP write access; EOEMS only needs read."}
    End Function

    ' ---------- Classification (evidence only) ----------
    Public Function Classification(device As UpsDevice, test As UpsConnectionTest, snapshot As UpsSnapshot) As String
        Dim capability = UpsCapabilityProbe.CurrentEvidence(device)
        Dim live = snapshot IsNot Nothing AndAlso snapshot.IsGood
        Dim testCurrent = test IsNot Nothing AndAlso (snapshot Is Nothing OrElse test.TimestampUtc >= snapshot.TimestampUtc)
        If live OrElse (testCurrent AndAlso test.Finding = UpsConnectionFinding.Connected) Then
            If capability Is Nothing OrElse Not capability.IdentityAnswered Then Return "Reachable – capabilities not yet probed"
            If Not capability.SupportsUpsMib Then Return "SNMP responsive but identity unknown"
            Return If(capability.FullyMapped, "Reachable and fully mapped", "Reachable but partially mapped")
        End If
        If Not testCurrent Then
            If snapshot IsNot Nothing AndAlso snapshot.Quality = UpsQuality.AuthFailure Then Return "Authentication failure (confirmed)"
            If snapshot IsNot Nothing AndAlso snapshot.Quality = UpsQuality.ConfigError Then Return "Wrong configuration (confirmed)"
            If snapshot IsNot Nothing AndAlso (snapshot.Quality = UpsQuality.Timeout OrElse snapshot.Quality = UpsQuality.Offline) Then Return "SNMP timeout (not yet diagnosed)"
            Return "Not tested"
        End If
        Select Case test.Finding
            Case UpsConnectionFinding.ConnectedWithoutUpsMib : Return "SNMP responsive but identity unknown"
            Case UpsConnectionFinding.AuthenticationRejected : Return "Authentication failure (confirmed USM report)"
            Case UpsConnectionFinding.AccessDenied : Return "Access Denied (SNMP response)"
            Case UpsConnectionFinding.WrongVersionConfirmed, UpsConnectionFinding.ConfigurationError : Return "Wrong configuration (confirmed)"
            Case UpsConnectionFinding.NoSnmpResponseHostUp, UpsConnectionFinding.AgentAliveRequestUnanswered : Return "SNMP Timeout – Cause Unknown"
            Case UpsConnectionFinding.PortUnreachable : Return "SNMP timeout (ICMP port unreachable – no agent on UDP " & test.Port.ToString(CultureInfo.InvariantCulture) & ")"
            Case UpsConnectionFinding.HostNotResponding : Return "No network response (path or device down – not distinguished)"
            Case Else : Return "Not tested"
        End Select
    End Function

    Public Function RequiredAction(device As UpsDevice, test As UpsConnectionTest, snapshot As UpsSnapshot) As String
        Dim classText = Classification(device, test, snapshot)
        Dim source = If(test Is Nothing OrElse test.SourceAddress.Length = 0, "the EOEMS host IP", test.SourceAddress)
        If classText = "Reachable and fully mapped" Then Return "None."
        If classText = "Reachable but partially mapped" Then Return "None for connectivity. Unsupported objects are shown as '—' (see list)."
        If classText.StartsWith("Reachable", StringComparison.Ordinal) Then Return "Run 'Discover Supported OIDs'."
        If test Is Nothing Then Return "Run 'Test Selected' to diagnose."
        Select Case test.Finding
            Case UpsConnectionFinding.AgentAliveRequestUnanswered
                Return "Card web page › SNMP: enable SNMP " & test.ProfileVersion & " (or supply an approved SNMPv3 user for a v3 profile); set the read-only community to the value stored in profile '" &
                       test.ProfileName & "'; add " & source & " to the SNMP access/manager table. Then 'Recheck Failed Only'."
            Case UpsConnectionFinding.ConnectedWithoutUpsMib
                Return "Enable the RFC 1628 UPS-MIB on the card, or confirm this IP is really the UPS card."
            Case UpsConnectionFinding.AuthenticationRejected
                Return "Align the SNMPv3 user/auth/privacy settings between the card and profile '" & test.ProfileName & "'."
            Case UpsConnectionFinding.WrongVersionConfirmed
                Return "Assign this unit a profile with SNMP " & test.AlternateVersion & " (same credential) or enable SNMP " & test.ProfileVersion & " on the card."
            Case UpsConnectionFinding.ConfigurationError
                Return "Fix the registry entry / credential profile: " & test.Summary
            Case UpsConnectionFinding.NoSnmpResponseHostUp
                Return "On the card: confirm the SNMP agent is enabled and " & source & " is in its access table; network team: confirm UDP 161 is not filtered; confirm the card type at this IP."
            Case UpsConnectionFinding.PortUnreachable
                Return "Enable the SNMP agent on the card (nothing listens on UDP " & test.Port.ToString(CultureInfo.InvariantCulture) & ") or correct the port."
            Case UpsConnectionFinding.HostNotResponding
                Return "Site check: UPS/card powered and booted, network cable and switch port, IP address on the card display matches " & test.IpAddress & "."
            Case Else
                Return "Run 'Test Selected'."
        End Select
    End Function

    ' ---------- Reports (never contain secrets) ----------
    Private Function OrderedDevices(devices As IReadOnlyList(Of UpsDevice)) As IEnumerable(Of UpsDevice)
        Return devices.OrderBy(Function(item) item.AtsBank).ThenBy(Function(item) item.Label, StringComparer.OrdinalIgnoreCase)
    End Function

    Private Function MatrixRow(device As UpsDevice, snapshots As IReadOnlyDictionary(Of String, UpsSnapshot), timeFormat As String) As String()
        Dim inv = CultureInfo.InvariantCulture
        Dim test = LastTest(device.Id)
        Dim snapshot As UpsSnapshot = Nothing
        If snapshots IsNot Nothing Then snapshots.TryGetValue(device.Id, snapshot)
        Dim capability = UpsCapabilityProbe.CurrentEvidence(device)
        Dim live = snapshot IsNot Nothing AndAlso snapshot.IsGood
        Dim version = If(live, snapshot.ProtocolVersion, If(test Is Nothing, "not tested", test.RespondingVersion))
        Dim identity = UpsQualityText.DescribeVerification(device.Verification) &
                       If(capability IsNot Nothing AndAlso capability.IdentityAnswered, " – sysName '" & capability.SysName & "', " & If(capability.Model.Length > 0, capability.Model, "no UPS-MIB model"),
                          If(test IsNot Nothing AndAlso test.SysName.Length > 0, " – sysName '" & test.SysName & "'", " – no SNMP identity read"))
        Dim supportedCount = If(capability Is Nothing OrElse Not capability.IdentityAnswered, "—",
                                capability.SupportedParameterCount.ToString(inv) & "/" & capability.RequestedParameterCount.ToString(inv) & If(capability.Complete, "", " (incomplete)"))
        Dim unsupported = If(capability Is Nothing OrElse Not capability.IdentityAnswered, "—",
                             If(capability.UnsupportedOids.Count = 0, "none", String.Join("; ", capability.UnsupportedOids)))
        Dim response = If(live, FormatMs(snapshot.RoundTripMilliseconds) & " poll",
                          If(test IsNot Nothing AndAlso test.LatencyMilliseconds.HasValue, FormatMs(test.LatencyMilliseconds) & " GET", "no response"))
        Dim lastGood = If(snapshot IsNot Nothing AndAlso snapshot.LastGoodUtc.HasValue, snapshot.LastGoodUtc.Value.ToLocalTime().ToString(timeFormat, inv), "never")
        Dim failure = If(live, If(snapshot.PartialGroups.Count > 0, "Partial: " & String.Join("; ", snapshot.PartialGroups), "—"),
                         If(snapshot IsNot Nothing AndAlso snapshot.LastFailureReason.Length > 0, snapshot.LastFailureReason,
                            If(test IsNot Nothing, test.SnmpDetail, If(snapshot Is Nothing, "", snapshot.QualityDetail))))
        Dim profile = SnmpCredentialStore.ListProfiles().FirstOrDefault(Function(item) String.Equals(item.Name, device.CredentialProfile, StringComparison.OrdinalIgnoreCase))
        Dim configured = profile IsNot Nothing AndAlso profile.IsConfigured
        Return {device.Id, device.IpAddress, device.AtsBank.ToString(inv), version,
                If(snapshot Is Nothing, "NO DATA", UpsQualityText.Describe(snapshot.Quality)), identity, supportedCount, unsupported,
                response, lastGood, failure, Classification(device, test, snapshot), RequiredAction(device, test, snapshot),
                device.Label, device.CredentialProfile, If(profile Is Nothing, "not configured", profile.Version), If(configured, "Yes", "No"),
                If(live, snapshot.AlarmDetailsStatus, "No current card alarm details"),
                If(live, String.Join("; ", snapshot.CardAlarms.Select(Function(alarm) alarm.Name & " [" & alarm.StateText & "] " &
                    alarm.CardType & "/" & alarm.CardSeverity & " " & alarm.DescriptionOid)), "")}
    End Function

    Private ReadOnly MatrixHeader As String() = {"Registered Device ID", "IP", "ATS Bank", "Actual Responding SNMP Version", "Current Connection Status",
        "Identity Verification", "Supported Parameter Count", "Unsupported OIDs", "Response Time", "Last Successful Poll", "Failure Reason",
        "Classification", "Required Administrator/Card-Side Action", "UPS Label", "Assigned Profile", "Configured Version", "Profile Configured",
        "Card Alarm Detail Status", "Card Conditions"}

    Public Function BuildMarkdownReport(devices As IReadOnlyList(Of UpsDevice), snapshots As IReadOnlyDictionary(Of String, UpsSnapshot)) As String
        Dim text As New StringBuilder()
        Dim inv = CultureInfo.InvariantCulture
        text.AppendLine("# EOEMS UPS SNMP Connection & OID Test Matrix")
        text.AppendLine()
        text.AppendLine("Generated " & DateTime.Now.ToString("dd MMM yyyy HH:mm:ss", inv) & " on " & Environment.MachineName &
                        ". Read-only traffic only (SNMP GET/GETNEXT and credential-free SNMPv3 engine discovery). No SNMP SET was sent. Credentials are not included.")
        text.AppendLine()
        text.AppendLine("| " & String.Join(" | ", MatrixHeader) & " |")
        text.AppendLine("|" & String.Concat(Enumerable.Repeat("---|", MatrixHeader.Length)))
        For Each device In OrderedDevices(devices)
            text.AppendLine("| " & String.Join(" | ", MatrixRow(device, snapshots, "dd MMM HH:mm:ss").Select(AddressOf Md)) & " |")
        Next
        text.AppendLine()
        text.AppendLine("## Per-unit evidence")
        Dim reference = KnownGoodReference(devices)
        For Each device In OrderedDevices(devices)
            Dim test = LastTest(device.Id)
            text.AppendLine()
            text.AppendLine("### " & device.Id & " – " & device.Label & " – " & device.IpAddress)
            If test Is Nothing Then
                text.AppendLine("- Connection test: not run in this session.")
            Else
                text.AppendLine("- Finding: **" & Describe(test.Finding) & "** – " & Md(test.Summary))
                text.AppendLine("- Source IP " & test.SourceAddress & "; ping " & test.PingReplies.ToString(inv) & "/" & test.PingAttempts.ToString(inv) &
                                "; profile '" & test.ProfileName & "' (" & test.ProfileVersion & "); timeout " & test.TimeoutMilliseconds.ToString(inv) & " ms, retries " & test.Retries.ToString(inv) &
                                "; test took " & FormatMs(test.ElapsedMilliseconds) & ".")
                If test.AlternateVersion.Length > 0 Then text.AppendLine("- SNMP " & test.AlternateVersion & " with the same credential: " & Md(test.AlternateVersionResult))
                If test.EngineId.Length > 0 Then text.AppendLine("- SNMPv3 engine: " & test.EngineId & If(test.EngineBoots.HasValue, ", boots " & test.EngineBoots.Value.ToString(inv), ""))
                If test.HttpPortOpen.HasValue Then text.AppendLine("- TCP 80: " & If(test.HttpPortOpen.Value, "open (proves host reachability only, not SNMP)", "closed/timeout"))
                If test.PossibleCauses.Count > 0 Then
                    text.AppendLine("- Possible causes (not yet distinguished):")
                    For Each cause In test.PossibleCauses
                        text.AppendLine("  - " & Md(cause))
                    Next
                End If
                If Not test.IsConnected Then
                    text.AppendLine("- Card-side checklist:")
                    For Each item In Checklist(test)
                        text.AppendLine("  - " & Md(item))
                    Next
                    text.AppendLine("- Comparison:")
                    text.AppendLine("```")
                    For Each line In Compare(test, reference)
                        text.AppendLine(line)
                    Next
                    text.AppendLine("```")
                End If
            End If
            Dim capability = UpsCapabilityProbe.CurrentEvidence(device)
            If capability IsNot Nothing AndAlso capability.IdentityAnswered Then
                text.AppendLine("- Capability probe " & capability.VerifiedUtc.ToLocalTime().ToString("HH:mm:ss", inv) & " (" & FormatMs(capability.ElapsedMilliseconds) & "): " &
                                UpsCapabilityProbe.MappingText(capability) & If(capability.Aborted.Length > 0, " – " & Md(capability.Aborted), ""))
                text.AppendLine("  - Identity: sysName '" & Md(capability.SysName) & "', sysObjectID " & capability.SysObjectId & ", sysDescr '" & Md(capability.SysDescr) &
                                "', " & Md(capability.Manufacturer) & " " & Md(capability.Model) & ", UPS fw '" & Md(capability.UpsSoftware) & "', agent fw '" & Md(capability.AgentSoftware) & "'")
                text.AppendLine("  - Groups: " & String.Join("; ", capability.Groups.Select(Function(item) item.Name & " " & item.Supported.ToString(inv) & "/" & item.Requested.ToString(inv) &
                                                                                          " " & item.Status & " " & item.Milliseconds.ToString("0", inv) & " ms")))
                For Each line In capability.IndexEvidence
                    text.AppendLine("  - " & Md(line))
                Next
                For Each check In capability.Checks
                    text.AppendLine("  - `" & check.Oid & "` " & check.Key & ": " & Md(check.Status) & If(check.Present, " raw " & Md(check.RawValue) & " → " & Md(check.ScaledValue), ""))
                Next
            End If
        Next
        Return text.ToString()
    End Function

    Public Function BuildCsvReport(devices As IReadOnlyList(Of UpsDevice), snapshots As IReadOnlyDictionary(Of String, UpsSnapshot)) As String
        Dim text As New StringBuilder()
        text.AppendLine(String.Join(",", MatrixHeader.Concat({"Label", "Finding", "Test Time"}).Select(AddressOf Csv)))
        For Each device In OrderedDevices(devices)
            Dim test = LastTest(device.Id)
            Dim row = MatrixRow(device, snapshots, "yyyy-MM-dd HH:mm:ss").Concat({device.Label, If(test Is Nothing, "Not tested", Describe(test.Finding)),
                If(test Is Nothing, "", test.TimestampUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))})
            text.AppendLine(String.Join(",", row.Select(AddressOf Csv)))
        Next
        Return text.ToString()
    End Function

    Public Function MappingText(audit As UpsOidAudit) As String
        If audit Is Nothing Then Return "Not audited"
        If audit.Capability IsNot Nothing Then Return UpsCapabilityProbe.MappingText(audit.Capability)
        If audit.Checks.Count = 0 Then Return "Audit failed"
        Return If(audit.MappingVerified, "Verified", "Incomplete") & " (" & audit.PresentCount.ToString(CultureInfo.InvariantCulture) & " objects)"
    End Function

    Public Function FormatMs(value As Double?) As String
        If Not value.HasValue Then Return "—"
        Return value.Value.ToString("0", CultureInfo.InvariantCulture) & " ms"
    End Function

    Private Function Md(value As String) As String
        Return If(value, "").Replace("|", "/").Replace(vbCr, " ").Replace(vbLf, " ")
    End Function

    Private Function Csv(value As String) As String
        Dim text = If(value, "")
        If text.IndexOfAny({","c, """"c, ControlChars.Cr, ControlChars.Lf}) >= 0 Then Return """" & text.Replace("""", """""") & """"
        Return text
    End Function
End Module
