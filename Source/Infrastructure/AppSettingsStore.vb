Imports System.IO
Imports System.Text.Json

Public NotInheritable Class AppSettingsStore
    Private Shared ReadOnly SerializerOptions As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True,
        .ReadCommentHandling = JsonCommentHandling.Skip
    }

    Public Shared Function Load() As AppSettings
        Dim settings As AppSettings = Nothing
        Dim settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json")

        Try
            If File.Exists(settingsPath) Then
                settings = JsonSerializer.Deserialize(Of AppSettings)(File.ReadAllText(settingsPath), SerializerOptions)
            End If
        Catch ex As JsonException
            'A malformed optional settings file must not stop the operator from reaching the sign-in screen.
        Catch ex As IOException
        End Try

        If settings Is Nothing Then settings = New AppSettings()
        settings.Normalize()
        Try
            Dim savedUiSettings = UiSettingsStore.Load()
            If savedUiSettings IsNot Nothing Then settings.Ui = savedUiSettings
        Catch ex As JsonException
            settings.Ui.ConfigurationError = "Saved UI settings are malformed: " & ex.Message
        Catch ex As InvalidDataException
            settings.Ui.ConfigurationError = "Saved UI settings are invalid: " & ex.Message
        Catch ex As IOException
            settings.Ui.ConfigurationError = "Saved UI settings could not be read: " & ex.Message
        End Try
        Try
            Dim savedPlcSettings = PlcSettingsStore.Load()
            If savedPlcSettings IsNot Nothing Then settings.Plc = savedPlcSettings
        Catch ex As JsonException
            settings.Plc.ConfigurationError = "Saved PLC settings are malformed: " & ex.Message
        Catch ex As InvalidDataException
            settings.Plc.ConfigurationError = "Saved PLC settings are invalid: " & ex.Message
        Catch ex As IOException
            settings.Plc.ConfigurationError = "Saved PLC settings could not be read: " & ex.Message
        End Try

        'Only a local developer can opt in at process launch; the distributed configuration remains secure by default.
        If String.Equals(Environment.GetEnvironmentVariable("EOEMS_OFFLINE_PREVIEW"), "1", StringComparison.Ordinal) Then
            settings.Ui.EnableOfflinePreview = True
        End If

        Return settings
    End Function
End Class
