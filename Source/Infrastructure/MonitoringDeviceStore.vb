Imports System.IO
Imports System.Text
Imports System.Text.Json

Public NotInheritable Class MonitoringDeviceStore
    Private Shared ReadOnly Gate As New Object
    Private Shared ReadOnly Options As New JsonSerializerOptions With {.WriteIndented = True, .PropertyNameCaseInsensitive = True}
    Private Shared _revision As Integer

    Public Shared ReadOnly Property Revision As Integer
        Get
            SyncLock Gate
                Return _revision
            End SyncLock
        End Get
    End Property

    Private Sub New()
    End Sub

    Public Shared Function Load(Optional path As String = Nothing) As MonitoringDeviceRegistry
        SyncLock Gate
            Dim target = Resolve(path)
            If Not File.Exists(target) Then Return New MonitoringDeviceRegistry
            Dim document = JsonSerializer.Deserialize(Of MonitoringDeviceRegistry)(File.ReadAllText(target), Options)
            If document Is Nothing Then Throw New InvalidDataException("Monitoring device registry is empty.")
            document.Validate()
            Return document
        End SyncLock
    End Function

    Public Shared Sub Save(document As MonitoringDeviceRegistry, session As UserSession, Optional path As String = Nothing)
        If session Is Nothing OrElse Not PlcConfigurationAccess.CanEdit(session.RoleName, session.IsOfflinePreview) Then Throw New UnauthorizedAccessException("Monitoring configuration requires authorized live access.")
        document.Validate()
        SyncLock Gate
            Dim target = Resolve(path)
            Directory.CreateDirectory(IO.Path.GetDirectoryName(target))
            Dim temporary = target & "." & Guid.NewGuid().ToString("N") & ".tmp"
            Try
                File.WriteAllText(temporary, JsonSerializer.Serialize(document, Options), New UTF8Encoding(False))
                File.Move(temporary, target, True)
                _revision += 1
                Try
                    File.AppendAllText(target & ".audit.jsonl", JsonSerializer.Serialize(New With {
                        .TimestampUtc = DateTime.UtcNow, .Username = session.Username, .Action = "MonitoringDevicesChanged",
                        .DeviceIds = document.Devices.Select(Function(device) device.Id).ToArray()
                    }) & Environment.NewLine, New UTF8Encoding(False))
                Catch ex As IOException
                    Throw New IOException("Device registry was saved, but its audit record could not be written. Check storage access before further changes.", ex)
                Catch ex As UnauthorizedAccessException
                    Throw New UnauthorizedAccessException("Device registry was saved, but its audit record could not be written. Check storage permissions.", ex)
                End Try
            Finally
                If File.Exists(temporary) Then File.Delete(temporary)
            End Try
        End SyncLock
    End Sub

    Private Shared Function Resolve(path As String) As String
        Return IO.Path.GetFullPath(If(path, IO.Path.Combine(AppContext.BaseDirectory, "EOEMSData", "Configuration", "monitoring-devices.json")))
    End Function
End Class
