Imports System.IO
Imports System.Text
Imports System.Text.Json

Public NotInheritable Class UiSettingsStore
    Private Shared ReadOnly Options As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True,
        .WriteIndented = True
    }

    Private Sub New()
    End Sub

    Private Shared Function SettingsPath() As String
        Return Path.Combine(AppContext.BaseDirectory, "data", "ui-settings.json")
    End Function

    Public Shared Function Load() As UiSettings
        Dim path = SettingsPath()
        Dim json = PostgresDatabase.LoadDocument("ui-settings", path)
        If json Is Nothing Then Return Nothing
        Dim settings = JsonSerializer.Deserialize(Of UiSettings)(json, Options)
        If settings Is Nothing Then Throw New InvalidDataException("Saved UI settings are empty.")
        settings.Validate()
        Return settings
    End Function

    Public Shared Sub Save(settings As UiSettings, actor As String, previousSettings As UiSettings)
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        settings.Validate()
        Dim settingsFilePath = SettingsPath()
        Dim folder = Path.GetDirectoryName(settingsFilePath)
        Directory.CreateDirectory(folder)
        Dim tempPath = settingsFilePath & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Dim serialized = JsonSerializer.Serialize(settings, Options)
        Try
            File.WriteAllText(tempPath, serialized, New UTF8Encoding(False))
            File.Move(tempPath, settingsFilePath, True)
        Finally
            If File.Exists(tempPath) Then File.Delete(tempPath)
        End Try
        PostgresDatabase.TryWriteDocument("ui-settings", serialized, actor)

        Dim auditPath = Path.Combine(folder, "MeterLogs", "ui-settings-audit.csv")
        Directory.CreateDirectory(Path.GetDirectoryName(auditPath))
        Dim oldValue = If(previousSettings Is Nothing, "Auto (100 ms)", Describe(previousSettings.RefreshIntervalMilliseconds))
        Dim newValue = Describe(settings.RefreshIntervalMilliseconds)
        If String.Equals(oldValue, newValue, StringComparison.Ordinal) Then Return
        SyncLock GetType(UiSettingsStore)
            Dim isNewFile = Not File.Exists(auditPath)
            Using writer As New StreamWriter(auditPath, True, New UTF8Encoding(False))
                If isNewFile Then writer.WriteLine("TimestampUtc,Username,SettingName,OldValue,NewValue")
                writer.WriteLine(String.Join(",", {
                    DateTime.UtcNow.ToString("o", Globalization.CultureInfo.InvariantCulture),
                    Quote(actor),
                    Quote("UI refresh interval"),
                    Quote(oldValue),
                    Quote(newValue)
                }))
            End Using
        End SyncLock
    End Sub

    Public Shared Function Describe(milliseconds As Integer) As String
        Return If(milliseconds = 0, "Auto (100 ms)", milliseconds.ToString(Globalization.CultureInfo.InvariantCulture) & " ms")
    End Function

    Private Shared Function Quote(value As String) As String
        Return """" & If(value, String.Empty).Replace("""", """""") & """"
    End Function
End Class
