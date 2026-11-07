Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json

Public NotInheritable Class FireConnectionDocument
    Public Property FormatVersion As Integer = 1
    Public Property Settings As New FireConnectionSettings
    Public Property ProtectedUsername As String = String.Empty
    Public Property ProtectedPassword As String = String.Empty
    Public Property ProtectedBmsUsername As String = String.Empty
    Public Property ProtectedBmsPassword As String = String.Empty
    Public Property ProtectedCloudToken As String = String.Empty
    Public Property ProtectedCloudEmail As String = String.Empty
    Public Property ProtectedCloudPassword As String = String.Empty
    Public Property UpdatedUtc As DateTime?
End Class

Public NotInheritable Class FireConnectionStore
    Private ReadOnly _gate As New Object
    Private ReadOnly _path As String
    Private Shared ReadOnly Entropy As Byte() = Encoding.UTF8.GetBytes("EOEMS-FireConnection-v1")
    Private Shared ReadOnly Options As New JsonSerializerOptions With {.WriteIndented = True}

    Public Sub New(Optional settingsPath As String = Nothing)
        _path = Path.GetFullPath(If(settingsPath, Path.Combine(AppContext.BaseDirectory, "EOEMSData", "Configuration", "fire-connection.json")))
    End Sub

    Public ReadOnly Property FilePath As String
        Get
            Return _path
        End Get
    End Property

    Public Function Load() As FireConnectionDocument
        SyncLock _gate
            If Not File.Exists(_path) Then Return New FireConnectionDocument
            Dim document = JsonSerializer.Deserialize(Of FireConnectionDocument)(File.ReadAllText(_path), Options)
            If document Is Nothing OrElse document.Settings Is Nothing OrElse document.ProtectedUsername Is Nothing OrElse document.ProtectedPassword Is Nothing Then
                Throw New InvalidDataException("Invalid fire connection settings. Restore the protected configuration before editing.")
            End If
            document.Settings.Validate()
            If document.FormatVersion < 1 OrElse document.FormatVersion > 3 Then Throw New InvalidDataException("Unsupported fire settings format.")
            If document.FormatVersion = 1 Then
                If document.Settings.ConnectionType = FireConnectionType.BmsModbus Then
                    document.Settings.BmsModbusHost = document.Settings.BmsHost
                    document.Settings.BmsModbusPort = document.Settings.BmsPort
                    document.Settings.BmsHost = "www.zetaremote.com"
                    document.Settings.BmsPort = 3333
                End If
                document.Settings.CaptureProfile(document.Settings.ConnectionType)
            End If
            If document.Settings.MappingVerified AndAlso Not HasMappingProof(document.Settings) Then
                document.Settings.MappingVerified = False
                document.Settings.MappingProof = String.Empty
                document.Settings.CaptureProfile(document.Settings.ConnectionType)
            End If
            ClearUnprovenProfiles(document.Settings)
            VerifyProtected(document.ProtectedUsername)
            VerifyProtected(document.ProtectedPassword)
            VerifyProtected(document.ProtectedBmsUsername)
            VerifyProtected(document.ProtectedBmsPassword)
            VerifyProtected(document.ProtectedCloudToken)
            VerifyProtected(document.ProtectedCloudEmail)
            VerifyProtected(document.ProtectedCloudPassword)
            Return document
        End SyncLock
    End Function

    Public Sub Save(settings As FireConnectionSettings, username As String, password As String, clearCredentials As Boolean, session As UserSession,
                    Optional clearAllCredentials As Boolean = False, Optional cloudToken As String = Nothing,
                    Optional clearCloudToken As Boolean = False, Optional cloudEmail As String = Nothing,
                    Optional cloudPassword As String = Nothing)
        RequireAdministrator(session)
        settings.Validate()
        If settings.MappingVerified AndAlso Not HasMappingProof(settings) Then Throw New InvalidDataException("Mapping is not verified: review vendor documentation and successfully Test Connection for this exact host/unit/register range before saving it as verified.")
        If username IsNot Nothing AndAlso username.Length > 256 Then Throw New InvalidDataException("Username must not exceed 256 characters.")
        If password IsNot Nothing AndAlso password.Length > 1024 Then Throw New InvalidDataException("Password must not exceed 1024 characters.")
        If cloudEmail IsNot Nothing AndAlso (String.IsNullOrWhiteSpace(cloudEmail) OrElse cloudEmail.Length > 320 OrElse cloudEmail.Any(Function(c) Char.IsControl(c))) Then Throw New InvalidDataException("Invalid Zeta login email.")
        If cloudPassword IsNot Nothing AndAlso (cloudPassword.Length = 0 OrElse cloudPassword.Length > 4096) Then Throw New InvalidDataException("Invalid Zeta login password.")
        If (cloudEmail Is Nothing) <> (cloudPassword Is Nothing) Then Throw New InvalidDataException("Both Zeta login fields are required.")
        If cloudEmail IsNot Nothing Then ZetaCloudEventClient.ValidateEndpoint(settings)
        SyncLock _gate
            Dim document = Load()
            document.Settings = settings.Copy()
            document.Settings.CaptureProfile(settings.ConnectionType)
            ClearUnprovenProfiles(document.Settings)
            document.FormatVersion = 3
            If cloudToken IsNot Nothing AndAlso cloudToken.Length > 0 Then cloudToken = ZetaCloudEventClient.NormalizeToken(cloudToken)
            If clearCloudToken OrElse clearAllCredentials Then
                document.ProtectedCloudToken = String.Empty
            ElseIf Not String.IsNullOrEmpty(cloudToken) Then
                document.ProtectedCloudToken = Protect(cloudToken)
            End If
            If clearCloudToken OrElse clearAllCredentials OrElse (Not String.IsNullOrEmpty(cloudToken) AndAlso cloudEmail Is Nothing) Then
                document.ProtectedCloudEmail = String.Empty
                document.ProtectedCloudPassword = String.Empty
            ElseIf cloudEmail IsNot Nothing Then
                document.ProtectedCloudEmail = Protect(cloudEmail.Trim())
                document.ProtectedCloudPassword = Protect(cloudPassword)
            End If
            If clearAllCredentials Then
                document.ProtectedBmsUsername = String.Empty
                document.ProtectedBmsPassword = String.Empty
                document.ProtectedUsername = String.Empty
                document.ProtectedPassword = String.Empty
            End If
            If clearCredentials Then
                If settings.ConnectionType = FireConnectionType.BmsVendor Then
                    document.ProtectedBmsUsername = String.Empty
                    document.ProtectedBmsPassword = String.Empty
                Else
                    document.ProtectedUsername = String.Empty
                    document.ProtectedPassword = String.Empty
                End If
            Else
                If settings.ConnectionType = FireConnectionType.BmsVendor Then
                    If Not String.IsNullOrEmpty(username) Then document.ProtectedBmsUsername = Protect(username)
                    If Not String.IsNullOrEmpty(password) Then document.ProtectedBmsPassword = Protect(password)
                Else
                    If Not String.IsNullOrEmpty(username) Then document.ProtectedUsername = Protect(username)
                    If Not String.IsNullOrEmpty(password) Then document.ProtectedPassword = Protect(password)
                End If
            End If
            WriteDocument(document)
        End SyncLock
    End Sub

    Friend Sub RenewCloudToken(settings As FireConnectionSettings, token As String)
        SyncLock _gate
            Dim document = Load()
            If document.Settings.ConnectionType <> FireConnectionType.ZetaCloud OrElse document.Settings.CloudUrl <> settings.CloudUrl OrElse
                document.Settings.CloudApiEndpoint <> settings.CloudApiEndpoint OrElse document.Settings.CloudSiteIdentifier <> settings.CloudSiteIdentifier OrElse
                document.ProtectedCloudEmail.Length = 0 OrElse document.ProtectedCloudPassword.Length = 0 Then
                Throw New InvalidDataException("Saved Zeta login changed during renewal; reconnect using the current settings.")
            End If
            document.ProtectedCloudToken = Protect(ZetaCloudEventClient.NormalizeToken(token))
            WriteDocument(document)
        End SyncLock
    End Sub

    Private Sub WriteDocument(document As FireConnectionDocument)
        document.UpdatedUtc = DateTime.UtcNow
        Directory.CreateDirectory(Path.GetDirectoryName(_path))
        Dim temporary = _path & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Try
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, Options), New UTF8Encoding(False))
            File.Move(temporary, _path, True)
        Finally
            If File.Exists(temporary) Then File.Delete(temporary)
        End Try
    End Sub

    Public Shared Sub RequireAdministrator(session As UserSession)
        If session Is Nothing OrElse String.IsNullOrWhiteSpace(session.UserId) OrElse String.IsNullOrWhiteSpace(session.Username) OrElse
           Not PlcConfigurationAccess.CanEdit(session.RoleName, session.IsOfflinePreview) Then
            Throw New UnauthorizedAccessException("Only an authenticated administrator can change fire connection settings or control communication.")
        End If
    End Sub

    Friend Shared Function MappingIdentity(settings As FireConnectionSettings) As String
        Return JsonSerializer.Serialize(New With {settings.ConnectionType, .Host = settings.Host(), .Port = settings.Port(),
            settings.UnitId, settings.StartAddress, settings.RegisterCount, settings.MappingReference})
    End Function

    Private Shared Sub ClearUnprovenProfiles(settings As FireConnectionSettings)
        For Each mode In {FireConnectionType.LocalModbus, FireConnectionType.ZetaCloud, FireConnectionType.BmsModbus, FireConnectionType.BmsVendor}
            Dim profile = If(mode = FireConnectionType.LocalModbus, settings.LocalProfile,
                If(mode = FireConnectionType.ZetaCloud, settings.CloudProfile,
                If(mode = FireConnectionType.BmsModbus, settings.BmsModbusProfile, settings.BmsVendorProfile)))
            If profile Is Nothing OrElse Not profile.MappingVerified Then Continue For
            Dim candidate = settings.Copy()
            candidate.SelectProfile(mode)
            If Not HasMappingProof(candidate) Then
                profile.MappingVerified = False
                profile.MappingProof = String.Empty
            End If
        Next
    End Sub

    Friend Shared Function CreateMappingProof(settings As FireConnectionSettings) As String
        Return Protect(MappingIdentity(settings))
    End Function

    Public Shared Function HasMappingProof(settings As FireConnectionSettings) As Boolean
        If Not settings.IsModbus() OrElse String.IsNullOrEmpty(settings.MappingProof) Then Return False
        Try
            Dim bytes = ProtectedData.Unprotect(Convert.FromBase64String(settings.MappingProof), Entropy, DataProtectionScope.CurrentUser)
            Try
                Return Encoding.UTF8.GetString(bytes) = MappingIdentity(settings)
            Finally
                CryptographicOperations.ZeroMemory(bytes)
            End Try
        Catch ex As Exception When TypeOf ex Is CryptographicException OrElse TypeOf ex Is FormatException
            Return False
        End Try
    End Function

    Friend Shared Function Protect(value As String) As String
        Dim bytes = Encoding.UTF8.GetBytes(value)
        Try
            Return Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser))
        Finally
            CryptographicOperations.ZeroMemory(bytes)
        End Try
    End Function

    Friend Shared Function Unprotect(value As String) As String
        If String.IsNullOrEmpty(value) Then Return String.Empty
        Dim bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser)
        Try
            Return Encoding.UTF8.GetString(bytes)
        Finally
            CryptographicOperations.ZeroMemory(bytes)
        End Try
    End Function

    Private Shared Sub VerifyProtected(value As String)
        If value Is Nothing Then Throw New InvalidDataException("Invalid protected fire credential field.")
        If value.Length = 0 Then Return
        Dim bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser)
        CryptographicOperations.ZeroMemory(bytes)
    End Sub
End Class
