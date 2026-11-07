Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Net.Sockets

Public Enum FireConnectionType
    LocalModbus
    ZetaCloud
    BmsModbus
    BmsVendor
End Enum

Public NotInheritable Class FireConnectionSettings
    Public Property ConnectionType As FireConnectionType
    Public Property AutoConnect As Boolean
    Public Property AutoReconnect As Boolean = True
    Public Property LocalHost As String = "192.168.107.90"
    Public Property LocalPort As Integer = 502
    Public Property BmsHost As String = "www.zetaremote.com"
    Public Property BmsPort As Integer = 3333
    Public Property BmsModbusHost As String = String.Empty
    Public Property BmsModbusPort As Integer = 502
    Public Property CloudAuthentication As String = "Not configured"
    Public Property CloudSiteIdentifier As String = String.Empty
    Public Property CloudSiteName As String = String.Empty
    Public Property BmsAuthentication As String = "Not configured"
    Public Property BmsRole As String = "Unverified"
    Public Property BmsConnectionDirection As String = "Unverified"
    Public Property BmsProtocolReference As String = String.Empty
    Public Property LocalProfile As FirePollingProfile
    Public Property CloudProfile As FirePollingProfile
    Public Property BmsModbusProfile As FirePollingProfile
    Public Property BmsVendorProfile As FirePollingProfile
    Public Property UnitId As Integer = 1
    Public Property TimeoutMilliseconds As Integer = 3000
    Public Property RetryCount As Integer = 3
    Public Property RetryIntervalSeconds As Integer = 10
    Public Property PollSeconds As Integer = 5
    Public Property FreshnessSeconds As Integer = 20
    Public Property StartAddress As Integer
    Public Property RegisterCount As Integer = 1
    Public Property MappingVerified As Boolean
    Public Property MappingReference As String = String.Empty
    Public Property MappingProof As String = String.Empty
    Public Property CloudUrl As String = "https://www.zetaremote.com"
    Public Property CloudApiEndpoint As String = String.Empty
    Public Property PanelNetworkMode As String = "Enabled Local"
    Public Property PanelNetworkAddress As String = "1"
    Public Property PanelNetworkName As String = "PANEL NAME"

    Public Function Copy() As FireConnectionSettings
        Dim result = DirectCast(MemberwiseClone(), FireConnectionSettings)
        result.LocalProfile = LocalProfile?.Copy()
        result.CloudProfile = CloudProfile?.Copy()
        result.BmsModbusProfile = BmsModbusProfile?.Copy()
        result.BmsVendorProfile = BmsVendorProfile?.Copy()
        Return result
    End Function

    Public Sub CaptureProfile(mode As FireConnectionType)
        Dim profile As New FirePollingProfile With {.UnitId = UnitId, .TimeoutMilliseconds = TimeoutMilliseconds,
            .RetryCount = RetryCount, .RetryIntervalSeconds = RetryIntervalSeconds, .PollSeconds = PollSeconds,
            .FreshnessSeconds = FreshnessSeconds, .StartAddress = StartAddress, .RegisterCount = RegisterCount,
            .MappingReference = MappingReference, .MappingVerified = MappingVerified, .MappingProof = MappingProof,
            .AutoConnect = AutoConnect, .AutoReconnect = AutoReconnect}
        Select Case mode
            Case FireConnectionType.LocalModbus : LocalProfile = profile
            Case FireConnectionType.ZetaCloud : CloudProfile = profile
            Case FireConnectionType.BmsModbus : BmsModbusProfile = profile
            Case FireConnectionType.BmsVendor : BmsVendorProfile = profile
        End Select
    End Sub

    Public Sub SelectProfile(mode As FireConnectionType)
        Dim profile As FirePollingProfile = Nothing
        Select Case mode
            Case FireConnectionType.LocalModbus : profile = LocalProfile
            Case FireConnectionType.ZetaCloud : profile = CloudProfile
            Case FireConnectionType.BmsModbus : profile = BmsModbusProfile
            Case FireConnectionType.BmsVendor : profile = BmsVendorProfile
        End Select
        profile = If(profile, If(mode = FireConnectionType.ZetaCloud,
            New FirePollingProfile With {.PollSeconds = 30, .TimeoutMilliseconds = 10000, .FreshnessSeconds = 90},
            New FirePollingProfile))
        ConnectionType = mode
        UnitId = profile.UnitId
        TimeoutMilliseconds = profile.TimeoutMilliseconds
        RetryCount = profile.RetryCount
        RetryIntervalSeconds = profile.RetryIntervalSeconds
        PollSeconds = profile.PollSeconds
        FreshnessSeconds = profile.FreshnessSeconds
        StartAddress = profile.StartAddress
        RegisterCount = profile.RegisterCount
        MappingReference = profile.MappingReference
        MappingVerified = profile.MappingVerified
        MappingProof = profile.MappingProof
        AutoConnect = profile.AutoConnect
        AutoReconnect = profile.AutoReconnect
    End Sub

    Public Function IsModbus() As Boolean
        Return ConnectionType = FireConnectionType.LocalModbus OrElse ConnectionType = FireConnectionType.BmsModbus
    End Function

    Public Function Host() As String
        Return If(ConnectionType = FireConnectionType.LocalModbus, LocalHost,
            If(ConnectionType = FireConnectionType.BmsModbus, BmsModbusHost, BmsHost))
    End Function

    Public Function Port() As Integer
        Return If(ConnectionType = FireConnectionType.LocalModbus, LocalPort,
            If(ConnectionType = FireConnectionType.BmsModbus, BmsModbusPort, BmsPort))
    End Function

    Public Sub Validate(Optional validateRegisters As Boolean = True)
        If Not [Enum].IsDefined(ConnectionType) Then Throw New InvalidDataException("Select a supported connection type.")
        ValidateHost(LocalHost, False)
        ValidateHost(BmsHost, True)
        ValidateHost(BmsModbusHost, True)
        If LocalPort < 1 OrElse LocalPort > 65535 OrElse BmsPort < 1 OrElse BmsPort > 65535 OrElse BmsModbusPort < 1 OrElse BmsModbusPort > 65535 Then Throw New InvalidDataException("Ports must be 1-65535.")
        If Not {"Not configured", "None", "Username / password", "API token"}.Contains(CloudAuthentication) OrElse
            Not {"Not configured", "None", "Username / password", "API token"}.Contains(BmsAuthentication) Then Throw New InvalidDataException("Select an available authentication metadata type.")
        If Not {"Unverified", "Client", "Server"}.Contains(BmsRole) Then Throw New InvalidDataException("Select an available documented connection role.")
        If Not {"Unverified", "Panel initiates outbound connection (reference only)", "SCADA connects to server (reference only)"}.Contains(BmsConnectionDirection) Then Throw New InvalidDataException("Select a documented connection-direction reference.")
        If CloudSiteIdentifier Is Nothing OrElse CloudSiteIdentifier.Length > 256 OrElse
            CloudSiteIdentifier.Any(Function(c) Char.IsControl(c)) Then Throw New InvalidDataException("Cloud site identifier must be plain text, up to 256 characters.")
        If CloudSiteName Is Nothing OrElse CloudSiteName.Length > 128 OrElse CloudSiteName.Any(Function(c) Char.IsControl(c)) Then Throw New InvalidDataException("Site name must be plain text, up to 128 characters.")
        If BmsProtocolReference Is Nothing OrElse BmsProtocolReference.Length > 256 OrElse
            BmsProtocolReference.Any(Function(c) Char.IsControl(c)) Then Throw New InvalidDataException("BMS protocol reference must be plain text, up to 256 characters.")
        If Not {"Disabled", "Enabled Local", "Enabled Global"}.Contains(PanelNetworkMode) Then Throw New InvalidDataException("Select a supported Zeta panel-network mode.")
        If String.IsNullOrWhiteSpace(PanelNetworkAddress) OrElse PanelNetworkAddress.Length > 32 OrElse
            PanelNetworkAddress.Any(Function(c) Char.IsControl(c)) Then Throw New InvalidDataException("Panel network address must be recorded as plain text, up to 32 characters.")
        If PanelNetworkName Is Nothing OrElse PanelNetworkName.Length > 128 OrElse
            PanelNetworkName.Any(Function(c) Char.IsControl(c)) Then Throw New InvalidDataException("Panel network name must be plain text, up to 128 characters.")
        If UnitId < 1 OrElse UnitId > 247 Then Throw New InvalidDataException("Modbus Unit ID must be 1-247.")
        If TimeoutMilliseconds < 250 OrElse TimeoutMilliseconds > 30000 Then Throw New InvalidDataException("Timeout must be 250-30000 ms.")
        If RetryCount < 0 OrElse RetryCount > 5 Then Throw New InvalidDataException("Retry count must be 0-5.")
        If RetryIntervalSeconds < 5 OrElse RetryIntervalSeconds > 300 Then Throw New InvalidDataException("Retry interval must be 5-300 seconds.")
        If ConnectionType = FireConnectionType.ZetaCloud AndAlso CloudApiEndpoint = ZetaCloudEventClient.EventRoute Then
            If PollSeconds < 30 OrElse PollSeconds > 570 Then Throw New InvalidDataException("Cloud polling interval must be 30-570 seconds.")
        ElseIf Not {1, 2, 5, 10, 30}.Contains(PollSeconds) Then
            Throw New InvalidDataException("Polling interval must be 1, 2, 5, 10 or 30 seconds.")
        End If
        If FreshnessSeconds < 1 OrElse FreshnessSeconds > 600 OrElse
            (IsModbus() AndAlso FreshnessSeconds < PollSeconds + CInt(Math.Ceiling(TimeoutMilliseconds / 1000.0))) Then
            Throw New InvalidDataException("Freshness must cover the polling interval plus timeout, up to 600 seconds.")
        End If
        If validateRegisters AndAlso (StartAddress < 0 OrElse StartAddress > 65535 OrElse RegisterCount < 1 OrElse RegisterCount > 125 OrElse StartAddress + RegisterCount > 65536) Then
            Throw New InvalidDataException("Use a zero-based FC04 address 0-65535 and 1-125 registers within that range.")
        End If
        If MappingReference Is Nothing OrElse MappingReference.Length > 256 OrElse MappingReference.Any(Function(c) Char.IsControl(c)) Then Throw New InvalidDataException("Mapping reference must be plain text, up to 256 characters.")
        If validateRegisters AndAlso MappingVerified AndAlso String.IsNullOrWhiteSpace(MappingReference) Then Throw New InvalidDataException("Identify the vendor-approved FC04 mapping document/version before confirming it.")
        ValidateHttps(CloudUrl, False)
        If CloudApiEndpoint.StartsWith("/", StringComparison.Ordinal) Then
            If CloudApiEndpoint <> ZetaCloudEventClient.EventRoute Then Throw New InvalidDataException("Use the observed /api//v2/sites/{site_id}/events route; alternative routes are not verified.")
        Else
            ValidateHttps(CloudApiEndpoint, True)
        End If
        If ConnectionType = FireConnectionType.ZetaCloud AndAlso CloudApiEndpoint = ZetaCloudEventClient.EventRoute Then
            ZetaCloudEventClient.ValidateEndpoint(Me)
            If PollSeconds < 30 Then Throw New InvalidDataException("Cloud polling must be at least 30 seconds.")
            If FreshnessSeconds < PollSeconds + Math.Ceiling(TimeoutMilliseconds / 1000.0R) Then Throw New InvalidDataException("Cloud freshness must cover polling plus request timeout, up to 600 seconds.")
        End If
        For Each mode In {FireConnectionType.LocalModbus, FireConnectionType.ZetaCloud, FireConnectionType.BmsModbus, FireConnectionType.BmsVendor}
            Dim present = If(mode = FireConnectionType.LocalModbus, LocalProfile,
                If(mode = FireConnectionType.ZetaCloud, CloudProfile, If(mode = FireConnectionType.BmsModbus, BmsModbusProfile, BmsVendorProfile)))
            If present Is Nothing Then Continue For
            Dim candidate = Copy()
            candidate.SelectProfile(mode)
            candidate.LocalProfile = Nothing
            candidate.CloudProfile = Nothing
            candidate.BmsModbusProfile = Nothing
            candidate.BmsVendorProfile = Nothing
            candidate.Validate(validateRegisters)
        Next
    End Sub

    Public Function UnavailableReason() As String
        If ConnectionType = FireConnectionType.ZetaCloud Then
            If CloudApiEndpoint <> ZetaCloudEventClient.EventRoute OrElse String.IsNullOrWhiteSpace(CloudSiteIdentifier) Then Return "API NOT CONFIGURED: set the observed event route and verify the proposed site identifier. Website reachability is not authenticated API data."
            Return String.Empty
        End If
        If ConnectionType = FireConnectionType.BmsVendor Then Return "PROTOCOL UNVERIFIED / UNSUPPORTED: Zeta BMS server protocol, client/server role and authentication are undocumented. Panel CONNECTED does not prove SCADA access; no port 3333 client connection or receiving service is started."
        If String.IsNullOrWhiteSpace(Host()) Then Return If(ConnectionType = FireConnectionType.BmsModbus,
            "Configure the independently verified BMS Modbus host/port. The vendor server www.zetaremote.com:3333 is not assumed to support Modbus.",
            "Configure the Local Modbus panel IP/hostname before connecting.")
        If Not MappingVerified Then Return "Confirm the panel/interface supports FC04 and enter its vendor-approved zero-based register mapping. No default register map is assumed."
        Return String.Empty
    End Function

    Private Shared Sub ValidateHost(value As String, allowEmpty As Boolean)
        If allowEmpty AndAlso value = String.Empty Then Return
        If String.IsNullOrWhiteSpace(value) OrElse value.Length > 253 OrElse value <> value.Trim() OrElse value.Any(Function(c) Char.IsControl(c)) Then Throw New InvalidDataException("Enter a valid IP address or hostname, without URL, path or credentials.")
        Dim address As IPAddress = Nothing
        If IPAddress.TryParse(value, address) Then
            If address.AddressFamily = AddressFamily.InterNetwork AndAlso
                (value.Split("."c).Length <> 4 OrElse Not String.Equals(value, address.ToString(), StringComparison.Ordinal)) Then
                Throw New InvalidDataException("Use a full dotted-decimal IPv4 address without abbreviated or octal components.")
            End If
            If value.Contains(":"c) AndAlso address.ScopeId <> 0 Then Throw New InvalidDataException("Scoped IPv6 addresses are not supported.")
            Return
        End If
        If value.All(Function(c) Char.IsDigit(c) OrElse c = "."c) OrElse Uri.CheckHostName(value) <> UriHostNameType.Dns Then
            Throw New InvalidDataException("Enter a valid IP address or DNS hostname.")
        End If
    End Sub

    Private Shared Sub ValidateHttps(value As String, allowEmpty As Boolean)
        If allowEmpty AndAlso value = String.Empty Then Return
        Dim endpoint As Uri = Nothing
        If value Is Nothing OrElse value.Any(Function(c) Char.IsControl(c)) OrElse
           Not Uri.TryCreate(value, UriKind.Absolute, endpoint) OrElse endpoint.Scheme <> Uri.UriSchemeHttps OrElse
           endpoint.UserInfo.Length > 0 OrElse endpoint.Query.Length > 0 OrElse endpoint.Fragment.Length > 0 Then
            Throw New InvalidDataException("Cloud endpoints must be HTTPS URLs without embedded credentials, query strings or fragments.")
        End If
    End Sub
End Class

Public NotInheritable Class FirePollingProfile
    Public Property UnitId As Integer = 1
    Public Property TimeoutMilliseconds As Integer = 3000
    Public Property RetryCount As Integer = 3
    Public Property RetryIntervalSeconds As Integer = 10
    Public Property PollSeconds As Integer = 5
    Public Property FreshnessSeconds As Integer = 20
    Public Property StartAddress As Integer
    Public Property RegisterCount As Integer = 1
    Public Property MappingReference As String = String.Empty
    Public Property MappingVerified As Boolean
    Public Property MappingProof As String = String.Empty
    Public Property AutoConnect As Boolean
    Public Property AutoReconnect As Boolean = True

    Public Function Copy() As FirePollingProfile
        Return DirectCast(MemberwiseClone(), FirePollingProfile)
    End Function
End Class
