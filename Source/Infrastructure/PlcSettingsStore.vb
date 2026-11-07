Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Text.Json

Public NotInheritable Class PlcSettingsStore
    Private Shared ReadOnly SerializerOptions As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True,
        .WriteIndented = True
    }

    Private Sub New()
    End Sub

    Public Shared Function DirectoryPath() As String
        Return Path.Combine(AppContext.BaseDirectory, "data")
    End Function

    Public Shared Function Load() As PlcSettings
        Dim settingsPath = Path.Combine(DirectoryPath(), "plc-settings.json")
        Dim json = PostgresDatabase.LoadDocument("plc-settings", settingsPath)
        If json Is Nothing Then Return Nothing
        Dim settings = JsonSerializer.Deserialize(Of PlcSettings)(json, SerializerOptions)
        If settings Is Nothing Then Throw New InvalidDataException("Saved PLC settings are empty.")
        settings.Validate()
        Return settings
    End Function

    Public Shared Sub Save(settings As PlcSettings, actor As String, Optional previousSettings As PlcSettings = Nothing)
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        settings.Validate()

        Dim settingsFolder = DirectoryPath()
        IO.Directory.CreateDirectory(settingsFolder)
        Dim settingsFilePath = Path.Combine(settingsFolder, "plc-settings.json")
        Dim tempPath = settingsFilePath & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Dim serialized = JsonSerializer.Serialize(settings, SerializerOptions)
        Try
            File.WriteAllText(tempPath, serialized, New UTF8Encoding(False))
            File.Move(tempPath, settingsFilePath, True)
        Finally
            If File.Exists(tempPath) Then File.Delete(tempPath)
        End Try
        PostgresDatabase.TryWriteDocument("plc-settings", serialized, actor)
        AppendAudit(actor, previousSettings, settings)
    End Sub

    Private Shared Sub AppendAudit(actor As String, previousSettings As PlcSettings, settings As PlcSettings)
        Dim auditPath = Path.Combine(DirectoryPath(), "MeterLogs", "plc-settings-audit.csv")
        IO.Directory.CreateDirectory(Path.GetDirectoryName(auditPath))
        Dim oldValues = FlattenSettings(previousSettings)
        Dim newValues = FlattenSettings(settings)
        SyncLock GetType(PlcSettingsStore)
            Dim newFile = Not File.Exists(auditPath)
            Using writer As New StreamWriter(auditPath, True, New UTF8Encoding(False))
                If newFile Then writer.WriteLine("TimestampUtc,Username,SettingName,OldValue,NewValue")
                For Each setting In newValues
                    Dim oldValue = If(oldValues.ContainsKey(setting.Key), oldValues(setting.Key), String.Empty)
                    If String.Equals(oldValue, setting.Value, StringComparison.Ordinal) Then Continue For
                    writer.WriteLine(String.Join(",", {
                        DateTime.UtcNow.ToString("o", Globalization.CultureInfo.InvariantCulture),
                        Quote(actor),
                        Quote(setting.Key),
                        Quote(oldValue),
                        Quote(setting.Value)
                    }))
                Next
            End Using
        End SyncLock
    End Sub

    Private Shared Function FlattenSettings(settings As PlcSettings) As Dictionary(Of String, String)
        Dim values As New Dictionary(Of String, String)(StringComparer.Ordinal)
        If settings Is Nothing Then Return values
        values("PLC Host") = settings.Host
        values("PLC Port") = settings.Port.ToString(Globalization.CultureInfo.InvariantCulture)
        values("Unit ID") = settings.UnitId.ToString(Globalization.CultureInfo.InvariantCulture)
        values("Polling interval seconds") = settings.PollingIntervalSeconds.ToString(Globalization.CultureInfo.InvariantCulture)
        values("Read timeout milliseconds") = settings.ReadTimeoutMilliseconds.ToString(Globalization.CultureInfo.InvariantCulture)
        values("Word swap") = settings.SwapFloatWords.ToString()
        values("Register offset") = settings.RegisterAddressOffset.ToString(Globalization.CultureInfo.InvariantCulture)
        values("Register count") = settings.RegisterCount.ToString(Globalization.CultureInfo.InvariantCulture)
        values("Total maximum load kW") = settings.TotalMaxLoadKw.ToString("R", Globalization.CultureInfo.InvariantCulture)
        For Each block In settings.Blocks
            values(block.TransformerName & " maximum load kW") = block.MaxLoadKw.ToString("R", Globalization.CultureInfo.InvariantCulture)
            values(block.TransformerName & " register start") = block.RegisterStart.ToString(Globalization.CultureInfo.InvariantCulture)
        Next
        Return values
    End Function

    Private Shared Function Quote(value As String) As String
        Return """" & If(value, String.Empty).Replace("""", """""") & """"
    End Function
End Class
