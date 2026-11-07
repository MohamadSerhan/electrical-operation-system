Imports System.IO
Imports System.Linq
Imports System.Net

Public Enum MapSignal
    Unknown
    Normal
    Warning
    Fault
    Fire
End Enum

Public NotInheritable Class MonitoringSample
    Public Property DeviceId As String = String.Empty
    Public Property TimestampUtc As DateTime
    Public Property Verified As Boolean
    Public Property Signal As MapSignal = MapSignal.Unknown
    Public Property DoorOpen As Boolean?
    Public Property Detail As String = String.Empty

    Public ReadOnly Property IsCurrent As Boolean
        Get
            Return Verified AndAlso TimestampUtc <= DateTime.UtcNow.AddSeconds(5) AndAlso TimestampUtc >= DateTime.UtcNow.AddSeconds(-30)
        End Get
    End Property

    Public Function Clone() As MonitoringSample
        Return DirectCast(MemberwiseClone(), MonitoringSample)
    End Function
End Class

Public NotInheritable Class MonitoringDevice
    Public Property Id As String = Guid.NewGuid().ToString("N")
    Public Property Name As String = "New device"
    Public Property Kind As String = "Fire"
    Public Property Model As String = String.Empty
    Public Property Location As String = String.Empty
    Public Property PanelId As String = String.Empty
    Public Property SiteId As String = String.Empty
    Public Property [Loop] As String = String.Empty
    Public Property Address As String = String.Empty
    Public Property EventAddress As String = String.Empty
    Public Property Zone As String = String.Empty
    Public Property IpAddress As String = String.Empty
    Public Property ExternalId As String = String.Empty
    Public Property Notes As String = String.Empty
    Public Property DrawingTag As String = String.Empty
    Public Property DrawingReference As String = String.Empty
    Public Property Commissioned As Boolean = True

    Public Function Clone() As MonitoringDevice
        Return DirectCast(MemberwiseClone(), MonitoringDevice)
    End Function

    Public Overrides Function ToString() As String
        Return Name & " [" & Id & "]"
    End Function
End Class

Public NotInheritable Class MonitoringDeviceRegistry
    Public Property Version As Integer = 1
    Public Property Devices As New List(Of MonitoringDevice)

    Public Function Clone() As MonitoringDeviceRegistry
        Return New MonitoringDeviceRegistry With {.Version = Version, .Devices = Devices.Select(Function(item) item.Clone()).ToList()}
    End Function

    Public Sub Validate()
        If Version <> 1 OrElse Devices Is Nothing Then Throw New InvalidDataException("Unsupported monitoring device registry.")
        Dim ids As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim addresses As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each device In Devices
            If device Is Nothing OrElse String.IsNullOrWhiteSpace(device.Id) OrElse Not ids.Add(device.Id) Then Throw New InvalidDataException("Monitoring devices require unique IDs.")
            For Each fieldValue As String In {device.Id, device.Name, device.Kind, device.Model, device.Location, device.PanelId, device.SiteId,
                              device.Loop, device.Address, device.EventAddress, device.Zone, device.IpAddress, device.ExternalId, device.Notes,
                              device.DrawingTag, device.DrawingReference}
                If fieldValue Is Nothing OrElse fieldValue.Length > 500 OrElse fieldValue.Any(Function(c) Char.IsControl(c)) Then Throw New InvalidDataException("Device fields must contain at most 500 printable characters.")
            Next
            If String.IsNullOrWhiteSpace(device.Name) OrElse Not {"Fire", "Camera", "Access", "Equipment"}.Contains(device.Kind) Then Throw New InvalidDataException("Enter a device name and choose Fire, Camera, Access or Equipment.")
            If device.IpAddress <> "" Then
                Dim address As IPAddress = Nothing
                If Not IPAddress.TryParse(device.IpAddress, address) Then Throw New InvalidDataException(device.Name & ": enter an IPv4/IPv6 address, not a URL or credentials.")
            End If
            If device.Kind = "Fire" Then
                If Not device.Commissioned Then
                    If String.IsNullOrWhiteSpace(device.DrawingTag) Then Throw New InvalidDataException(device.Name & ": an uncommissioned fire device requires its drawing tag.")
                    Continue For
                End If
                If String.IsNullOrWhiteSpace(device.PanelId) OrElse String.IsNullOrWhiteSpace(device.Loop) OrElse String.IsNullOrWhiteSpace(device.Address) Then Throw New InvalidDataException(device.Name & ": panel, loop and device address are required.")
                Dim key = device.PanelId & vbNullChar & device.SiteId & vbNullChar & device.Loop & vbNullChar & device.Address
                If Not addresses.Add(key) Then Throw New InvalidDataException(device.Name & ": duplicate panel/site/loop/address.")
                If device.EventAddress <> "" Then
                    If Devices.Any(Function(other) Not ReferenceEquals(other, device) AndAlso other IsNot Nothing AndAlso
                        other.Kind = "Fire" AndAlso other.Commissioned AndAlso String.Equals(other.PanelId, device.PanelId, StringComparison.OrdinalIgnoreCase) AndAlso
                        String.Equals(other.SiteId, device.SiteId, StringComparison.OrdinalIgnoreCase) AndAlso
                        String.Equals(other.EventAddress, device.EventAddress, StringComparison.OrdinalIgnoreCase) AndAlso
                        (other.Zone = "" OrElse device.Zone = "" OrElse String.Equals(other.Zone, device.Zone, StringComparison.OrdinalIgnoreCase))) Then
                        Throw New InvalidDataException(device.Name & ": overlapping event-address matches. Include the vendor loop identity or distinct explicit zones.")
                    End If
                End If
            End If
        Next
    End Sub
End Class

Public NotInheritable Class RoomTemperatureLimits
    Public Property LowAlarmC As Double = 10
    Public Property LowWarningC As Double = 15
    Public Property HighWarningC As Double = 30
    Public Property HighAlarmC As Double = 35

    Public Function Clone() As RoomTemperatureLimits
        Return DirectCast(MemberwiseClone(), RoomTemperatureLimits)
    End Function

    Public Sub Validate()
        Dim values = {LowAlarmC, LowWarningC, HighWarningC, HighAlarmC}
        If values.Any(Function(value) Not Double.IsFinite(value)) OrElse LowAlarmC < -50 OrElse HighAlarmC > 150 OrElse
            Not (LowAlarmC < LowWarningC AndAlso LowWarningC < HighWarningC AndAlso HighWarningC < HighAlarmC) Then
            Throw New InvalidDataException("Room temperature limits must rise: low alarm < low warning < high warning < high alarm (-50 to 150 C).")
        End If
    End Sub

    Public Function Evaluate(value As Double?) As MapSignal
        If Not value.HasValue OrElse Not Double.IsFinite(value.Value) Then Return MapSignal.Unknown
        If value.Value <= LowAlarmC OrElse value.Value >= HighAlarmC Then Return MapSignal.Fault
        If value.Value <= LowWarningC OrElse value.Value >= HighWarningC Then Return MapSignal.Warning
        Return MapSignal.Normal
    End Function
End Class
