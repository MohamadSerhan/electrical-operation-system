Imports System.IO
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json

Public NotInheritable Class WebCardCredentialSummary
    Public Property DeviceId As String = String.Empty
    Public Property ManagementUrl As String = String.Empty
    Public Property Username As String = String.Empty
    Public Property HasPassword As Boolean
    Public Property UpdatedUtc As DateTime?
End Class

Public NotInheritable Class WebCardCredentialStore
    Private Shared ReadOnly Gate As New Object()
    Private Shared ReadOnly Entropy As Byte() = Encoding.UTF8.GetBytes("EOEMS-WebCard-Credentials-v1")
    Private Shared ReadOnly Options As New JsonSerializerOptions With {.WriteIndented = True}

    Private NotInheritable Class StoredLogin
        Public Property DeviceId As String = String.Empty
        Public Property ManagementUrl As String = String.Empty
        Public Property Username As String = String.Empty
        Public Property Password As String = String.Empty
        Public Property UpdatedUtc As DateTime
    End Class

    Private Sub New()
    End Sub

    Public Shared Sub RequireAdministrator(session As UserSession)
        If session Is Nothing OrElse String.IsNullOrWhiteSpace(session.UserId) OrElse String.IsNullOrWhiteSpace(session.Username) OrElse
           Not PlcConfigurationAccess.CanEdit(session.RoleName, session.IsOfflinePreview) Then
            Throw New UnauthorizedAccessException("Only an authenticated administrator can manage Web Card credentials.")
        End If
    End Sub

    Public Shared Function StorePath() As String
        Return Path.Combine(AppContext.BaseDirectory, "EOEMSData", "Configuration", "web-card-credentials.json")
    End Function

    Public Shared Function ManagementUri(deviceId As String, value As String) As Uri
        Dim device = UpsSettingsStore.Current.Devices.SingleOrDefault(Function(item) item.Id = deviceId)
        If device Is Nothing Then Throw New InvalidDataException("Select a registered UPS first.")
        Dim result As Uri = Nothing
        If Not Uri.TryCreate(value, UriKind.Absolute, result) OrElse
           (result.Scheme <> Uri.UriSchemeHttps AndAlso result.Scheme <> Uri.UriSchemeHttp) OrElse
           result.Host <> device.IpAddress OrElse result.UserInfo.Length > 0 OrElse
           result.Query.Length > 0 OrElse result.Fragment.Length > 0 OrElse value.Any(Function(c) Char.IsControl(c)) Then
            Throw New InvalidDataException("Use an http/https management URL for this UPS's registered IP only, without credentials, query or fragment. Device addresses are not changed here.")
        End If
        Return result
    End Function

    Private Shared Function ReadAll() As List(Of StoredLogin)
        If Not File.Exists(StorePath()) Then Return New List(Of StoredLogin)()
        Try
            Dim entries = JsonSerializer.Deserialize(Of List(Of StoredLogin))(File.ReadAllText(StorePath()), Options)
            If entries Is Nothing OrElse entries.Any(Function(item) item Is Nothing OrElse String.IsNullOrWhiteSpace(item.DeviceId) OrElse
                String.IsNullOrEmpty(item.Username) OrElse String.IsNullOrEmpty(item.Password) OrElse String.IsNullOrWhiteSpace(item.ManagementUrl)) OrElse
               entries.Select(Function(item) item.DeviceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() <> entries.Count Then
                Throw New InvalidDataException("Invalid Web Card credential store. Restore its protected backup before editing.")
            End If
            Return entries
        Catch ex As Exception When TypeOf ex Is JsonException OrElse TypeOf ex Is IOException
            Throw New InvalidDataException("Cannot read Web Card credentials. Restore the protected backup before editing.", ex)
        End Try
    End Function

    Public Shared Function Load(deviceId As String, session As UserSession) As WebCardCredentialSummary
        RequireAdministrator(session)
        SyncLock Gate
            Dim entry = ReadAll().SingleOrDefault(Function(item) item.DeviceId = deviceId)
            If entry Is Nothing Then Return New WebCardCredentialSummary With {
                .DeviceId = deviceId, .ManagementUrl = ManagementUri(deviceId, "https://" & RegisteredIp(deviceId) & "/").AbsoluteUri}
            Return New WebCardCredentialSummary With {
                .DeviceId = entry.DeviceId, .ManagementUrl = ManagementUri(deviceId, entry.ManagementUrl).AbsoluteUri,
                .Username = Unprotect(entry.Username), .HasPassword = Unprotect(entry.Password).Length > 0, .UpdatedUtc = entry.UpdatedUtc}
        End SyncLock
    End Function

    Private Shared Function RegisteredIp(deviceId As String) As String
        Dim device = UpsSettingsStore.Current.Devices.SingleOrDefault(Function(item) item.Id = deviceId)
        If device Is Nothing Then Throw New InvalidDataException("Select a registered UPS first.")
        Return device.IpAddress
    End Function

    Public Shared Sub Save(deviceId As String, managementUrl As String, username As String, password As String, session As UserSession)
        RequireAdministrator(session)
        Dim url = ManagementUri(deviceId, managementUrl).AbsoluteUri
        If String.IsNullOrWhiteSpace(username) OrElse username.Length > 256 Then Throw New InvalidDataException("Enter the approved Web Card username (up to 256 characters).")
        If password IsNot Nothing AndAlso (password.Length = 0 OrElse password.Length > 1024) Then Throw New InvalidDataException("Enter a non-empty Web Card password (up to 1024 characters). Blank input retains a saved password.")
        SyncLock Gate
            Dim entries = ReadAll()
            Dim entry = entries.SingleOrDefault(Function(item) item.DeviceId = deviceId)
            If entry Is Nothing Then
                entry = New StoredLogin With {.DeviceId = deviceId}
                entries.Add(entry)
            End If
            If password Is Nothing Then
                If Unprotect(entry.Password).Length = 0 Then Throw New InvalidDataException("Enter a Web Card password for this device.")
            Else
                entry.Password = Protect(password)
            End If
            entry.ManagementUrl = url
            entry.Username = Protect(username)
            entry.UpdatedUtc = DateTime.UtcNow
            Dim file = StorePath()
            Directory.CreateDirectory(Path.GetDirectoryName(file))
            Dim temporary = file & "." & Guid.NewGuid().ToString("N") & ".tmp"
            Try
                System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(entries, Options), New UTF8Encoding(False))
                If System.IO.File.Exists(file) Then System.IO.File.Copy(file, file & ".bak", True)
                System.IO.File.Move(temporary, file, True)
            Finally
                If System.IO.File.Exists(temporary) Then System.IO.File.Delete(temporary)
            End Try
            System.IO.File.AppendAllText(Path.Combine(Path.GetDirectoryName(file), "web-card-credentials-audit.jsonl"),
                JsonSerializer.Serialize(New With {.TimestampUtc = DateTime.UtcNow, .Actor = session.Username, .Action = "WebCardCredentialSaved", .DeviceId = deviceId}) & Environment.NewLine)
        End SyncLock
    End Sub

    Friend Shared Function VerifyProtection(deviceId As String, session As UserSession) As Boolean
        RequireAdministrator(session)
        SyncLock Gate
            Dim entry = ReadAll().SingleOrDefault(Function(item) item.DeviceId = deviceId)
            Return entry IsNot Nothing AndAlso Unprotect(entry.Username).Length > 0 AndAlso Unprotect(entry.Password).Length > 0
        End SyncLock
    End Function

    Private Shared Function Protect(value As String) As String
        Return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser))
    End Function

    Private Shared Function Unprotect(value As String) As String
        If value.Length = 0 Then Return String.Empty
        Try
            Return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser))
        Catch ex As Exception When TypeOf ex Is CryptographicException OrElse TypeOf ex Is FormatException
            Throw New InvalidDataException("Cannot decrypt Web Card credentials under this Windows account. Restore the original Windows account or re-enter approved credentials.", ex)
        End Try
    End Function
End Class
