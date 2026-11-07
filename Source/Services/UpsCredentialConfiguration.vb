Imports System.IO
Imports System.Linq

Public NotInheritable Class UpsCredentialConfiguration
    Private Sub New()
    End Sub

    Public Shared Function HasElectricalRead(snapshot As UpsSnapshot) As Boolean
        If snapshot Is Nothing OrElse Not snapshot.IsGood Then Return False
        Return snapshot.OutputKw.HasValue AndAlso
            {"OutputSource", "OutputFrequency", "BatteryStatus"}.All(Function(key) snapshot.Value(key).HasValue) AndAlso
            {"OutputVoltage", "OutputCurrent", "OutputLoad"}.All(
                Function(key) snapshot.Phases.ContainsKey(key) AndAlso snapshot.Phases(key).Length > 0 AndAlso
                    snapshot.Phases(key).All(Function(value) value.HasValue))
    End Function

    Public Shared Sub AssignProfile(deviceId As String, profileName As String, session As UserSession, confirmWorkingDevice As Boolean)
        WebCardCredentialStore.RequireAdministrator(session)
        If SnmpCredentialStore.Resolve(profileName) Is Nothing Then Throw New InvalidDataException("Select a configured credential profile.")
        Dim settings = UpsSettingsStore.Current.Clone()
        Dim device = settings.Devices.SingleOrDefault(Function(item) item.Id = deviceId)
        If device Is Nothing Then Throw New InvalidDataException("Select a registered UPS.")
        device.CredentialProfile = profileName
        device.UseProfileTransport = True
        UpsSettingsStore.Save(settings, session.Username, session.RoleName, session.IsOfflinePreview, confirmWorkingDevice)
    End Sub

End Class
