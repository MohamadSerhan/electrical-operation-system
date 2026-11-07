Imports System.IO
Imports System.Text.Json

Public NotInheritable Class BackupSettings
    Public Property AutomaticBackupEnabled As Boolean = True
    Public Property BackupFolderPath As String = "EOEMSData\Backups"
    Public Property DailyBackupTime As String = "00:01"
    Public Property RetentionDays As Integer

    Public Sub Validate()
        If String.IsNullOrWhiteSpace(BackupFolderPath) Then
            Throw New InvalidDataException("Backup folder is required.")
        End If
        If BackupFolderPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0 Then
            Throw New InvalidDataException("Backup folder contains invalid path characters.")
        End If
        Dim backupTime As TimeSpan
        If Not TimeSpan.TryParseExact(DailyBackupTime, "hh\:mm",
                                      Globalization.CultureInfo.InvariantCulture, backupTime) OrElse
           backupTime < TimeSpan.Zero OrElse backupTime >= TimeSpan.FromDays(1) Then
            Throw New InvalidDataException("Daily backup time must use 24-hour HH:mm format.")
        End If
        If RetentionDays < 0 OrElse RetentionDays > 3650 Then
            Throw New InvalidDataException("Backup retention must be between 0 and 3,650 days; zero disables deletion.")
        End If
    End Sub

    Public Function ResolveBackupFolder(Optional baseDirectory As String = Nothing) As String
        Validate()
        Dim root = If(String.IsNullOrWhiteSpace(baseDirectory), AppContext.BaseDirectory, baseDirectory)
        If Path.IsPathRooted(BackupFolderPath) Then Return Path.GetFullPath(BackupFolderPath)
        Return Path.GetFullPath(Path.Combine(root, BackupFolderPath))
    End Function
End Class

Public NotInheritable Class BackupSettingsStore
    Private Shared ReadOnly Gate As New Object()
    Private Shared ReadOnly SerializerOptions As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True,
        .WriteIndented = True
    }

    Private Sub New()
    End Sub

    Public Shared Function SettingsPath(Optional baseDirectory As String = Nothing) As String
        Return Path.Combine(If(String.IsNullOrWhiteSpace(baseDirectory), AppContext.BaseDirectory, baseDirectory),
                            "EOEMSData", "Configuration", "backup-settings.json")
    End Function

    Public Shared Function Load(Optional baseDirectory As String = Nothing) As BackupSettings
        Dim path = SettingsPath(baseDirectory)
        Dim json As String = If(String.IsNullOrWhiteSpace(baseDirectory),
            PostgresDatabase.LoadDocument("backup-settings", path),
            If(File.Exists(path), File.ReadAllText(path), Nothing))
        If json Is Nothing Then Return New BackupSettings()
        Try
            Dim settings = JsonSerializer.Deserialize(Of BackupSettings)(json, SerializerOptions)
            If settings Is Nothing Then Throw New InvalidDataException("Backup settings file is empty.")
            settings.Validate()
            Return settings
        Catch ex As JsonException
            Throw New InvalidDataException("Backup settings are malformed: " & ex.Message, ex)
        Catch ex As IOException
            Throw New InvalidDataException("Backup settings could not be read: " & ex.Message, ex)
        End Try
    End Function

    Public Shared Sub Save(settings As BackupSettings,
                           username As String,
                           roleName As String,
                           previous As BackupSettings,
                           Optional baseDirectory As String = Nothing)
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        If Not PlcConfigurationAccess.CanEdit(roleName, False) Then
            Throw New UnauthorizedAccessException("Only an authenticated administrator can edit backup settings.")
        End If
        settings.Validate()
        Dim path = SettingsPath(baseDirectory)
        Dim folder = System.IO.Path.GetDirectoryName(path)
        Directory.CreateDirectory(folder)
        Dim temporary = path & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Dim oldFile = path & "." & Guid.NewGuid().ToString("N") & ".bak"
        Dim auditPath = System.IO.Path.Combine(folder, "backup-settings-audit.jsonl")
        Dim auditRecord = JsonSerializer.Serialize(New With {
            .TimestampUtc = DateTime.UtcNow,
            .Username = If(username, String.Empty),
            .Action = "BackupSettingsChanged",
            .Before = previous,
            .After = settings
        }, SerializerOptions)
        SyncLock Gate
            Try
                File.WriteAllText(temporary, JsonSerializer.Serialize(settings, SerializerOptions), New System.Text.UTF8Encoding(False))
                If File.Exists(path) Then
                    File.Replace(temporary, path, oldFile)
                Else
                    File.Move(temporary, path)
                End If
                File.AppendAllText(auditPath, auditRecord & Environment.NewLine, New System.Text.UTF8Encoding(False))
                If String.IsNullOrWhiteSpace(baseDirectory) Then
                    PostgresDatabase.TryWriteDocument("backup-settings", JsonSerializer.Serialize(settings, SerializerOptions), username)
                End If
            Finally
                If File.Exists(temporary) Then File.Delete(temporary)
                If File.Exists(oldFile) Then File.Delete(oldFile)
            End Try
        End SyncLock
    End Sub
End Class
