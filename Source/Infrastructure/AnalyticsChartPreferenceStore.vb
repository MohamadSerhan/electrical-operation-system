Imports System.IO
Imports System.Text
Imports System.Text.Json

Public NotInheritable Class AnalyticsChartPreference
    Public Property ChartType As AnalyticsChartType = AnalyticsChartType.Line
    Public Property ShowMarkers As Boolean
    Public Property ShowDensity As Boolean
    Public Property ShowGrid As Boolean = True
End Class

Public NotInheritable Class AnalyticsChartPreferenceStore
    Private Shared ReadOnly Options As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True,
        .WriteIndented = True
    }

    Private Sub New()
    End Sub

    Public Shared Function DefaultPath() As String
        Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EOEMS_SCADA", "analytics-chart-preferences.json")
    End Function

    Public Shared Function Load(Optional path As String = Nothing) As Dictionary(Of String, AnalyticsChartPreference)
        Dim settingsPath = If(String.IsNullOrWhiteSpace(path), DefaultPath(), IO.Path.GetFullPath(path))
        If Not File.Exists(settingsPath) Then
            Return New Dictionary(Of String, AnalyticsChartPreference)(StringComparer.Ordinal)
        End If
        Dim preferences = JsonSerializer.Deserialize(Of Dictionary(Of String, AnalyticsChartPreference))(
            File.ReadAllText(settingsPath), Options)
        If preferences Is Nothing Then Throw New InvalidDataException("Saved Analytics chart preferences are empty.")
        Return New Dictionary(Of String, AnalyticsChartPreference)(preferences, StringComparer.Ordinal)
    End Function

    Public Shared Sub Save(preferences As IReadOnlyDictionary(Of String, AnalyticsChartPreference),
                           Optional path As String = Nothing)
        If preferences Is Nothing Then Throw New ArgumentNullException(NameOf(preferences))
        Dim settingsPath = If(String.IsNullOrWhiteSpace(path), DefaultPath(), IO.Path.GetFullPath(path))
        Dim folder = IO.Path.GetDirectoryName(settingsPath)
        Directory.CreateDirectory(folder)
        Dim temporaryPath = settingsPath & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Try
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences, Options), New UTF8Encoding(False))
            File.Move(temporaryPath, settingsPath, True)
        Finally
            If File.Exists(temporaryPath) Then File.Delete(temporaryPath)
        End Try
    End Sub
End Class
