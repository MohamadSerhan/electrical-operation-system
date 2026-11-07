Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json

' Read-only view of a credential profile for the UI: secrets are reported only as "set / not set".
Public NotInheritable Class SnmpCredentialSummary
    Public Property Name As String = String.Empty
    Public Property Version As String = "v2c"
    Public Property UserName As String = String.Empty
    Public Property AuthProtocol As String = "None"
    Public Property PrivProtocol As String = "None"
    Public Property HasCommunity As Boolean
    Public Property HasAuthSecret As Boolean
    Public Property HasPrivSecret As Boolean
    Public Property UpdatedUtc As DateTime?
    Public Property UpdatedBy As String = String.Empty
    Public Property Port As Integer = 161
    Public Property TimeoutMilliseconds As Integer = 2000
    Public Property Retries As Integer = 1
    Public ReadOnly Property SecurityMode As String
        Get
            Return If(AuthProtocol = "None", "NoAuthNoPriv", If(PrivProtocol = "None", "AuthNoPriv", "AuthPriv"))
        End Get
    End Property
    Public ReadOnly Property IsConfigured As Boolean
        Get
            If Not SnmpCredentialStore.Versions.Contains(Version) Then Return False
            If Version <> "v3" Then Return HasCommunity
            Return UserName.Length > 0 AndAlso SnmpCredentialStore.AuthProtocols.Contains(AuthProtocol) AndAlso
                SnmpCredentialStore.PrivProtocols.Contains(PrivProtocol) AndAlso
                (AuthProtocol <> "None" OrElse PrivProtocol = "None") AndAlso
                (AuthProtocol = "None" OrElse HasAuthSecret) AndAlso (PrivProtocol = "None" OrElse HasPrivSecret)
        End Get
    End Property
End Class

' Decrypted credential handed only to the SNMP client; never logged or displayed.
Friend NotInheritable Class SnmpCredential
    Public Property Name As String = String.Empty
    Public Property Version As String = "v2c"
    Public Property Community As String = String.Empty
    Public Property UserName As String = String.Empty
    Public Property AuthProtocol As String = "None"
    Public Property AuthSecret As String = String.Empty
    Public Property PrivProtocol As String = "None"
    Public Property PrivSecret As String = String.Empty
End Class

' Named SNMP credential profiles. Community strings and v3 secrets are encrypted with Windows DPAPI (current user).
Public NotInheritable Class SnmpCredentialStore
    Public Shared ReadOnly Versions As String() = {"v1", "v2c", "v3"}
    Public Shared ReadOnly AuthProtocols As String() = {"None", "MD5", "SHA1", "SHA256", "SHA384", "SHA512"}
    Public Shared ReadOnly PrivProtocols As String() = {"None", "DES", "AES128", "AES192", "AES256"}

    Private Shared ReadOnly Entropy As Byte() = Encoding.UTF8.GetBytes("EOEMS-SNMP-CREDENTIALS-v1")
    Private Shared ReadOnly Gate As New Object()
    Private Shared ReadOnly SerializerOptions As New JsonSerializerOptions With {.WriteIndented = True, .PropertyNameCaseInsensitive = True}

    Private Sub New()
    End Sub

    Public Shared Event Changed As EventHandler

    Private Shared Sub RequireAdministrator(session As UserSession)
        If session Is Nothing OrElse String.IsNullOrWhiteSpace(session.UserId) OrElse String.IsNullOrWhiteSpace(session.Username) OrElse
           Not PlcConfigurationAccess.CanEdit(session.RoleName, session.IsOfflinePreview) Then
            Throw New UnauthorizedAccessException("Only an authenticated administrator can edit SNMP credentials.")
        End If
    End Sub

    Private NotInheritable Class StoredProfile
        Public Property Name As String = String.Empty
        Public Property Version As String = "v2c"
        Public Property Community As String = String.Empty
        Public Property UserName As String = String.Empty
        Public Property AuthProtocol As String = "None"
        Public Property AuthSecret As String = String.Empty
        Public Property PrivProtocol As String = "None"
        Public Property PrivSecret As String = String.Empty
        Public Property UpdatedUtc As DateTime?
        Public Property UpdatedBy As String = String.Empty
        Public Property Port As Integer = 161
        Public Property TimeoutMilliseconds As Integer = 2000
        Public Property Retries As Integer = 1
    End Class

    Public Shared Function StorePath(Optional baseDirectory As String = Nothing) As String
        Return Path.Combine(If(baseDirectory, AppContext.BaseDirectory), "EOEMSData", "Configuration", "snmp-credentials.json")
    End Function

    Private Shared Function LoadAll() As List(Of StoredProfile)
        Dim path = StorePath()
        If Not File.Exists(path) Then Return New List(Of StoredProfile)()
        Try
            Dim profiles = JsonSerializer.Deserialize(Of List(Of StoredProfile))(File.ReadAllText(path), SerializerOptions)
            If profiles Is Nothing OrElse profiles.Any(Function(item) item Is Nothing) Then Throw New InvalidDataException("SNMP credential store is empty or invalid.")
            Dim names As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each profile In profiles
                If String.IsNullOrWhiteSpace(profile.Name) OrElse Not names.Add(profile.Name) OrElse
                   Not Versions.Contains(profile.Version) OrElse Not AuthProtocols.Contains(profile.AuthProtocol) OrElse Not PrivProtocols.Contains(profile.PrivProtocol) OrElse
                   profile.Port < 1 OrElse profile.Port > 65535 OrElse profile.TimeoutMilliseconds < 200 OrElse profile.TimeoutMilliseconds > 10000 OrElse
                   profile.Retries < 0 OrElse profile.Retries > 3 Then
                    Throw New InvalidDataException("SNMP credential store contains an invalid or duplicate profile. Restore its protected backup.")
                End If
            Next
            Return profiles
        Catch ex As Exception When TypeOf ex Is JsonException OrElse TypeOf ex Is IOException
            Throw New InvalidDataException("Cannot read the SNMP credential store. Restore its protected backup before editing.", ex)
        End Try
    End Function

    Public Shared Function ListProfiles() As IReadOnlyList(Of SnmpCredentialSummary)
        SyncLock Gate
            Return LoadAll().Select(Function(profile) New SnmpCredentialSummary With {
                .Name = profile.Name, .Version = profile.Version, .UserName = profile.UserName,
                .AuthProtocol = profile.AuthProtocol, .PrivProtocol = profile.PrivProtocol,
                .HasCommunity = Not String.IsNullOrEmpty(profile.Community),
                .HasAuthSecret = Not String.IsNullOrEmpty(profile.AuthSecret),
                .HasPrivSecret = Not String.IsNullOrEmpty(profile.PrivSecret),
                .UpdatedUtc = profile.UpdatedUtc, .UpdatedBy = profile.UpdatedBy,
                .Port = profile.Port, .TimeoutMilliseconds = profile.TimeoutMilliseconds, .Retries = profile.Retries}).ToList()
        End SyncLock
    End Function

    ' A Nothing secret keeps the stored value; an empty string clears it.
    Public Shared Sub Save(name As String, version As String, community As String, userName As String,
                           authProtocol As String, authSecret As String, privProtocol As String, privSecret As String,
                           session As UserSession, Optional port As Integer = 161, Optional timeoutMilliseconds As Integer = 2000,
                           Optional retries As Integer = 1, Optional confirmWorkingDevices As Boolean = False)
        RequireAdministrator(session)
        name = If(name, String.Empty).Trim()
        If name.Length = 0 OrElse name.Length > 40 OrElse name.Any(Function(c) Not (Char.IsLetterOrDigit(c) OrElse c = "-"c OrElse c = "_"c)) Then
            Throw New InvalidDataException("Profile name must be 1–40 letters, digits, '-' or '_'.")
        End If
        If Not Versions.Contains(version) Then Throw New InvalidDataException("SNMP version must be v1, v2c or v3.")
        If Not AuthProtocols.Contains(authProtocol) Then Throw New InvalidDataException("Unsupported authentication protocol.")
        If Not PrivProtocols.Contains(privProtocol) Then Throw New InvalidDataException("Unsupported privacy protocol.")
        If port < 1 OrElse port > 65535 Then Throw New InvalidDataException("SNMP port must be 1–65535.")
        If timeoutMilliseconds < 200 OrElse timeoutMilliseconds > 10000 Then Throw New InvalidDataException("Timeout must be 200–10000 ms.")
        If retries < 0 OrElse retries > 3 Then Throw New InvalidDataException("Retry count must be 0–3.")
        SyncLock Gate
            Dim all = LoadAll()
            Dim profile = all.FirstOrDefault(Function(item) String.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            Dim created = profile Is Nothing
            If Not created AndAlso ConfirmationUsers(name).Count > 0 AndAlso Not confirmWorkingDevices Then
                Throw New InvalidDataException("Explicit administrator confirmation is required to change a shared profile or a working UPS credential.")
            End If
            If created Then
                profile = New StoredProfile With {.Name = name}
                all.Add(profile)
            End If
            profile.Version = version
            profile.UserName = If(userName, String.Empty).Trim()
            profile.AuthProtocol = authProtocol
            profile.PrivProtocol = privProtocol
            profile.Port = port
            profile.TimeoutMilliseconds = timeoutMilliseconds
            profile.Retries = retries
            If community IsNot Nothing Then profile.Community = Protect(community)
            If authSecret IsNot Nothing Then profile.AuthSecret = Protect(authSecret)
            If privSecret IsNot Nothing Then profile.PrivSecret = Protect(privSecret)
            If version = "v3" Then
                If profile.UserName.Length = 0 Then Throw New InvalidDataException("SNMPv3 requires a user name.")
                If Encoding.UTF8.GetByteCount(profile.UserName) > 32 Then Throw New InvalidDataException("SNMPv3 user name must not exceed 32 UTF-8 bytes.")
                If authProtocol = "None" AndAlso privProtocol <> "None" Then Throw New InvalidDataException("SNMPv3 privacy requires authentication.")
                If authProtocol <> "None" AndAlso Unprotect(profile.AuthSecret).Length < 8 Then Throw New InvalidDataException("SNMPv3 authentication password must be at least 8 characters.")
                If privProtocol <> "None" AndAlso Unprotect(profile.PrivSecret).Length < 8 Then Throw New InvalidDataException("SNMPv3 privacy password must be at least 8 characters.")
            ElseIf String.IsNullOrEmpty(profile.Community) Then
                Throw New InvalidDataException("SNMP " & version & " requires a community string.")
            End If
            If version <> "v3" Then
                profile.UserName = String.Empty
                profile.AuthProtocol = "None"
                profile.PrivProtocol = "None"
                profile.AuthSecret = String.Empty
                profile.PrivSecret = String.Empty
            Else
                profile.Community = String.Empty
                If authProtocol = "None" Then profile.AuthSecret = String.Empty
                If privProtocol = "None" Then profile.PrivSecret = String.Empty
            End If
            profile.UpdatedUtc = DateTime.UtcNow
            profile.UpdatedBy = session.Username
            WriteAll(all, session.Username, If(created, "SnmpCredentialCreated", "SnmpCredentialUpdated"), name)
        End SyncLock
        RaiseEvent Changed(Nothing, EventArgs.Empty)
    End Sub

    Public Shared Sub Delete(name As String, session As UserSession)
        RequireAdministrator(session)
        SyncLock Gate
            If UpsSettingsStore.Current.Devices.Any(Function(item) String.Equals(item.CredentialProfile, name, StringComparison.OrdinalIgnoreCase)) Then
                Throw New InvalidDataException("This profile is assigned to UPS devices. Reassign them before removing it.")
            End If
            Dim all = LoadAll()
            If all.RemoveAll(Function(item) String.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)) = 0 Then Throw New InvalidDataException("Credential profile does not exist.")
            WriteAll(all, session.Username, "SnmpCredentialDeleted", name)
        End SyncLock
        RaiseEvent Changed(Nothing, EventArgs.Empty)
    End Sub

    Public Shared Function ProtectedUsers(name As String) As IReadOnlyList(Of String)
        Return UpsSettingsStore.Current.Devices.Where(Function(device) String.Equals(device.CredentialProfile, name, StringComparison.OrdinalIgnoreCase) AndAlso
            IsProtectedDevice(device)).Select(Function(device) device.DisplayName).ToList()
    End Function

    Public Shared Function ConfirmationUsers(name As String) As IReadOnlyList(Of String)
        Dim users = UpsSettingsStore.Current.Devices.Where(Function(device) String.Equals(device.CredentialProfile, name, StringComparison.OrdinalIgnoreCase)).ToList()
        Return If(users.Count > 1, users.Select(Function(device) device.DisplayName).ToList(), ProtectedUsers(name))
    End Function

    Public Shared Function IsProtectedDevice(device As UpsDevice) As Boolean
        Dim test = UpsConnectionDiagnostics.LastTest(device.Id)
        Return {"UPS-C01", "UPS-C03", "UPS-C04"}.Contains(device.Id, StringComparer.OrdinalIgnoreCase) OrElse
            (test IsNot Nothing AndAlso test.IsConnected)
    End Function

    Public Shared Sub Duplicate(sourceName As String, newName As String, session As UserSession)
        RequireAdministrator(session)
        newName = If(newName, String.Empty).Trim()
        SyncLock Gate
            If LoadAll().Any(Function(item) String.Equals(item.Name, newName.Trim(), StringComparison.OrdinalIgnoreCase)) Then
                Throw New InvalidDataException("The destination profile already exists.")
            End If
            Dim source = Resolve(sourceName)
            If source Is Nothing Then Throw New InvalidDataException("Select an existing profile to duplicate.")
            Dim summary = ListProfiles().First(Function(item) item.Name = source.Name)
            Save(newName, source.Version, source.Community, source.UserName, source.AuthProtocol, source.AuthSecret,
                 source.PrivProtocol, source.PrivSecret, session, summary.Port, summary.TimeoutMilliseconds, summary.Retries)
        End SyncLock
    End Sub

    Public Shared Function EffectiveDevice(device As UpsDevice) As UpsDevice
        Dim result = device.Clone()
        If Not result.UseProfileTransport Then Return result
        Dim profile = ListProfiles().FirstOrDefault(Function(item) String.Equals(item.Name, device.CredentialProfile, StringComparison.OrdinalIgnoreCase))
        If profile Is Nothing Then Throw New InvalidDataException("Credentials Not Configured: profile '" & device.CredentialProfile & "' does not exist.")
        result.Port = profile.Port
        result.TimeoutMilliseconds = profile.TimeoutMilliseconds
        result.Retries = profile.Retries
        Return result
    End Function

    Friend Shared Function ConnectionStamp(device As UpsDevice) As String
        Dim profile = ListProfiles().FirstOrDefault(Function(item) String.Equals(item.Name, device.CredentialProfile, StringComparison.OrdinalIgnoreCase))
        Return String.Join("|", device.IpAddress, device.Port.ToString(Globalization.CultureInfo.InvariantCulture),
                           device.CredentialProfile, device.UseProfileTransport.ToString(),
                           If(profile Is Nothing, "missing", profile.Version & ":" & If(profile.UpdatedUtc.HasValue, profile.UpdatedUtc.Value.Ticks.ToString(Globalization.CultureInfo.InvariantCulture), "legacy")))
    End Function

    Friend Shared Function Resolve(name As String) As SnmpCredential
        SyncLock Gate
            Dim profile = LoadAll().FirstOrDefault(Function(item) String.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))
            If profile Is Nothing Then Return Nothing
            Dim credential As New SnmpCredential With {
                .Name = profile.Name, .Version = profile.Version, .Community = Unprotect(profile.Community),
                .UserName = profile.UserName, .AuthProtocol = profile.AuthProtocol, .AuthSecret = Unprotect(profile.AuthSecret),
                .PrivProtocol = profile.PrivProtocol, .PrivSecret = Unprotect(profile.PrivSecret)}
            If credential.Version = "v3" Then
                If String.IsNullOrWhiteSpace(credential.UserName) OrElse Encoding.UTF8.GetByteCount(credential.UserName) > 32 OrElse
                   (credential.AuthProtocol = "None" AndAlso credential.PrivProtocol <> "None") OrElse
                   (credential.AuthProtocol <> "None" AndAlso credential.AuthSecret.Length < 8) OrElse
                   (credential.PrivProtocol <> "None" AndAlso credential.PrivSecret.Length < 8) Then
                    Throw New InvalidDataException("Credentials Not Configured: SNMPv3 user/security settings are incomplete.")
                End If
            ElseIf credential.Community.Length = 0 Then
                Throw New InvalidDataException("Credentials Not Configured: community string is missing.")
            End If
            Return credential
        End SyncLock
    End Function

    Private Shared Sub WriteAll(all As List(Of StoredProfile), username As String, action As String, profileName As String)
        Dim path = StorePath()
        Dim folder = System.IO.Path.GetDirectoryName(path)
        Directory.CreateDirectory(folder)
        Dim temporary = path & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Try
            File.WriteAllText(temporary, JsonSerializer.Serialize(all, SerializerOptions), New UTF8Encoding(False))
            If File.Exists(path) Then File.Copy(path, path & ".bak", True)
            File.Move(temporary, path, True)
        Finally
            If File.Exists(temporary) Then File.Delete(temporary)
        End Try
        ' The audit trail records who changed which profile – never the secret values.
        Dim audit = JsonSerializer.Serialize(New With {.TimestampUtc = DateTime.UtcNow, .Username = If(username, String.Empty), .Action = action, .Profile = profileName})
        File.AppendAllText(System.IO.Path.Combine(folder, "snmp-credentials-audit.jsonl"), audit & Environment.NewLine, New UTF8Encoding(False))
    End Sub

    Private Shared Function Protect(plain As String) As String
        If String.IsNullOrEmpty(plain) Then Return String.Empty
        Return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser))
    End Function

    Private Shared Function Unprotect(cipher As String) As String
        If String.IsNullOrEmpty(cipher) Then Return String.Empty
        Try
            Return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(cipher), Entropy, DataProtectionScope.CurrentUser))
        Catch ex As Exception When TypeOf ex Is CryptographicException OrElse TypeOf ex Is FormatException
            Throw New InvalidDataException("Cannot decrypt the SNMP secret under this Windows account. Restore the original account or re-enter approved credentials.", ex)
        End Try
    End Function
End Class
