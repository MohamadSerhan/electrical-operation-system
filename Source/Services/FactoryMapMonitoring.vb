Imports System.Globalization
Imports System.Linq

Public NotInheritable Class FireMapEventEvidence
    Public Sub New(record As ZetaFireEvent)
        Dim identity = FireEventIdentity.Parse(record)
        SiteId = record.SiteId
        Address = identity.Address
        Zone = identity.Zone
        [Loop] = identity.Loop
        AddressSource = identity.AddressSource
        IdentityConflict = identity.Conflict
        TimestampUtc = record.TimestampUtc
        Category = record.EventType
        Message = record.Message
        Description = record.Description
        EventId = record.EventId
        EventKey = FireEventIdentity.Key(record)
        Signal = FireEventIdentity.Signal(record)
        LocationInfo = FireEventLocation.Parse(record.Description, record.Message)
    End Sub
    Public ReadOnly Property SiteId As String
    Public ReadOnly Property Address As String
    Public ReadOnly Property Zone As String
    Public ReadOnly Property TimestampUtc As DateTimeOffset?
    Public ReadOnly Property Category As String
    Public ReadOnly Property Message As String
    Public ReadOnly Property Description As String
    Public ReadOnly Property EventId As String
    Public ReadOnly Property EventKey As String
    Public ReadOnly Property [Loop] As String
    Public ReadOnly Property AddressSource As String
    Public ReadOnly Property IdentityConflict As Boolean
    Public ReadOnly Property Signal As MapSignal
    Friend ReadOnly Property LocationInfo As FireEventLocation
End Class

Friend NotInheritable Class MapIndication
    Public Sub New(signal As MapSignal, text As String, Optional detail As String = "")
        Me.Signal = signal
        Me.Text = text
        Me.Detail = detail
    End Sub
    Public ReadOnly Property Signal As MapSignal
    Public ReadOnly Property Text As String
    Public ReadOnly Property Detail As String
End Class

Friend NotInheritable Class FactoryMapMonitoring
    Private Sub New()
    End Sub

    Public Shared Function IsCeiling(model As String) As Boolean
        Return {"Smoke detector", "Zeta smoke detector", "Zeta heat detector", "Zeta multi-sensor detector"}.Contains(model)
    End Function

    Public Shared Function IsWallMounted(model As String) As Boolean
        Return {"Zeta fire panel", "Zeta repeater panel", "Zeta manual call point", "Zeta sounder",
            "Zeta sounder beacon", "Zeta siren", "Zeta beam detector", "Zeta interface module",
            "Zeta isolator module", "Fire suppression control panel"}.Contains(model)
    End Function

    Public Shared Function DefaultMountHeight(model As String) As Single
        If IsCeiling(model) OrElse model = "Zeta beam detector" Then Return 3
        If model = "Zeta manual call point" Then Return 1.4F
        If model.Contains("panel", StringComparison.OrdinalIgnoreCase) AndAlso IsWallMounted(model) Then Return 1.2F
        Return If(IsWallMounted(model), 2.2F, 0)
    End Function

    Public Shared Function ConcealedFire(asset As FactoryMapAsset) As Boolean
        Return asset.Category = "Fire System" AndAlso asset.Model <> "Fire extinguisher" AndAlso asset.Model <> "Fire hydrant" AndAlso
            Not asset.Model.EndsWith("cylinder bank", StringComparison.Ordinal)
    End Function

    Public Shared Function Temperature(room As FactoryBuildingPart, snapshots As IReadOnlyList(Of MeterSnapshot)) As Double?
        If Not room.TemperatureSourceConfirmed OrElse Not room.TemperatureSourceId.StartsWith("PLC:", StringComparison.Ordinal) Then Return Nothing
        Dim id = room.TemperatureSourceId.Substring(4)
        Dim snapshot = snapshots.FirstOrDefault(Function(item) String.Equals(item.TransformerName, id, StringComparison.OrdinalIgnoreCase))
        Return MeterValue(snapshot, 15)
    End Function

    Public Shared Function MeterValue(snapshot As MeterSnapshot, index As Integer) As Double?
        If Not IsCurrentMeter(snapshot) OrElse index < 0 OrElse
            snapshot.DecodedValues Is Nothing OrElse snapshot.DecodedValues.Length <= index OrElse Not Single.IsFinite(snapshot.DecodedValues(index)) Then Return Nothing
        Return CDbl(snapshot.DecodedValues(index))
    End Function

    Public Shared Function IsCurrentMeter(snapshot As MeterSnapshot) As Boolean
        Return snapshot IsNot Nothing AndAlso snapshot.Quality = MeterQuality.Good AndAlso snapshot.CapturedAtUtc.HasValue AndAlso
            snapshot.CapturedAtUtc.Value <= DateTime.UtcNow.AddSeconds(5) AndAlso snapshot.CapturedAtUtc.Value >= DateTime.UtcNow.AddSeconds(-30)
    End Function

    Public Shared Function IsCurrentUps(snapshot As UpsSnapshot) As Boolean
        If snapshot Is Nothing Then Return False
        Dim device = UpsSettingsStore.Current.Devices.FirstOrDefault(Function(item) String.Equals(item.Id, snapshot.DeviceId, StringComparison.OrdinalIgnoreCase))
        Dim interval = If(device Is Nothing, 10, device.PollIntervalSeconds)
        Return snapshot IsNot Nothing AndAlso snapshot.IsGood AndAlso snapshot.TimestampUtc <= DateTime.UtcNow.AddSeconds(5) AndAlso
            snapshot.TimestampUtc >= DateTime.UtcNow.AddSeconds(-(interval * 3 + 5))
    End Function

    Public Shared Function UpsBatteryMode(snapshot As UpsSnapshot) As String
        If Not IsCurrentUps(snapshot) Then Return "Unknown"
        Dim current = snapshot.Value("BatteryCurrent")
        Dim source = snapshot.Value("OutputSource")
        If (current.HasValue AndAlso Not ValidUpsValue("BatteryCurrent", current.Value)) OrElse
            (source.HasValue AndAlso Not ValidUpsValue("OutputSource", source.Value)) Then Return "Unknown (invalid battery state data)"
        If source = 5 Then Return If(current < 0, "Unknown (on-battery source conflicts with charging current)", "Discharging")
        If current > 0 Then Return "Discharging"
        If current < 0 Then Return "Charging"
        If current = 0 Then Return "Idle / float (current zero)"
        Return "Unknown (battery current unavailable)"
    End Function

    Private Shared Function ValidUpsValue(key As String, value As Double) As Boolean
        If Not Double.IsFinite(value) Then Return False
        Dim definition = UpsOidMap.Definitions.FirstOrDefault(Function(item) item.Key = key)
        If definition IsNot Nothing AndAlso (value < definition.MinValid OrElse value > definition.MaxValid) Then Return False
        Return Not {"OutputSource", "BatteryStatus"}.Contains(key) OrElse value = Math.Truncate(value)
    End Function

    Private Shared Function UpsIndication(id As String, rack As Boolean, snapshots As IReadOnlyDictionary(Of String, UpsSnapshot)) As MapIndication
        Dim ups As UpsSnapshot = Nothing
        Dim prefix = If(rack, "Parent UPS " & id & ": ", "")
        If Not snapshots.TryGetValue(id, ups) OrElse ups Is Nothing Then Return New MapIndication(MapSignal.Unknown, prefix & "UPS data unavailable")
        If Not ups.IsGood Then Return New MapIndication(MapSignal.Unknown, prefix & "UPS acquisition: " & UpsQualityText.Describe(ups.Quality),
            "Source UPS: " & id & Environment.NewLine & ups.QualityDetail & Environment.NewLine & "No current measurements. Acquisition failure is not a battery fault.")
        If Not IsCurrentUps(ups) Then Return New MapIndication(MapSignal.Unknown, prefix & "UPS acquisition: STALE / invalid timestamp", "No current measurements; old/future data cannot indicate current equipment state.")
        If Not String.Equals(ups.DeviceId, id, StringComparison.OrdinalIgnoreCase) Then Return New MapIndication(MapSignal.Unknown, prefix & "UPS source identity mismatch", "Snapshot device ID does not match the linked UPS.")
        Dim invalid = ups.Values.Where(Function(entry) Not ValidUpsValue(entry.Key, entry.Value)).Select(Function(entry) entry.Key).
            Concat(ups.Phases.Where(Function(entry) entry.Value IsNot Nothing AndAlso entry.Value.Any(Function(value) value.HasValue AndAlso Not ValidUpsValue(entry.Key, value.Value))).
                   Select(Function(entry) entry.Key)).ToArray()
        If invalid.Length > 0 Then Return New MapIndication(MapSignal.Unknown, prefix & "UPS measurements invalid", "Invalid source channels: " & String.Join(", ", invalid) & ". Current alarm evaluation unavailable; this is not a battery fault.")
        Dim alarms = UpsAlarmEvaluator.Evaluate(ups, AlarmSettingsStore.Current.Ups)
        Dim active As New List(Of (Severity As AlarmSeverity, Text As String))
        For Each item In alarms
            If item.Severity = AlarmSeverity.Normal Then Continue For
            Dim severity = item.Severity
            Dim card = item.CardAlarm
            Dim onBattery = item.IsOnBattery OrElse
                (card IsNot Nothing AndAlso card.DescriptionOid = "1.3.6.1.2.1.33.1.6.3.2" AndAlso card.Active = True AndAlso
                 Not card.EvidenceConflict AndAlso card.CardSeverity <> "Major" AndAlso card.CardSeverity <> "Critical")
            If onBattery Then severity = AlarmSeverity.PreAlarm
            Dim text = item.Text
            If card IsNot Nothing AndAlso Not card.Active.HasValue Then
                severity = AlarmSeverity.PreAlarm
                text = "Unconfirmed card condition: " & text
            End If
            active.Add((severity, text))
        Next
        Dim mode = UpsBatteryMode(ups)
        If mode = "Discharging" AndAlso Not active.Any(Function(item) item.Text = "UPS ON BATTERY") Then active.Add((AlarmSeverity.PreAlarm, "Battery discharging (signed UPS current)"))
        If mode.StartsWith("Unknown (on-battery", StringComparison.Ordinal) Then active.Add((AlarmSeverity.PreAlarm, mode))
        Dim ordered = active.OrderByDescending(Function(item) item.Severity).ToArray()
        Dim worst = If(ordered.Length = 0, AlarmSeverity.Normal, ordered(0).Severity)
        Dim signal = If(worst >= AlarmSeverity.Alarm, MapSignal.Fault, If(worst = AlarmSeverity.PreAlarm, MapSignal.Warning, MapSignal.Normal))
        Dim title = If(ordered.Length = 0, "No active evaluated UPS alarms", ordered(0).Text)
        If ordered.Length > 1 Then title &= " (+" & (ordered.Length - 1).ToString(CultureInfo.InvariantCulture) & " more)"
        Dim detail = "Source UPS: " & id & Environment.NewLine &
            If(rack, "Parent UPS alarms below are NOT a diagnosis of this individual rack." & Environment.NewLine, "") &
            String.Join(Environment.NewLine, ordered.Select(Function(item) item.Severity.ToString() & ": " & item.Text))
        Return New MapIndication(signal, prefix & title, detail)
    End Function

    Public Shared Function ExpectedDeviceKind(category As String) As String
        Return If(category = "CCTV", "Camera", If(category = "Fire System", "Fire", If(category = "Access Control", "Access", "Equipment")))
    End Function

    Public Shared Function DeviceMatches(asset As FactoryMapAsset, device As MonitoringDevice) As Boolean
        Return device IsNot Nothing AndAlso device.Kind = ExpectedDeviceKind(asset.Category)
    End Function

    Public Shared Function DoorState(sample As MonitoringSample) As String
        If sample Is Nothing OrElse Not sample.IsCurrent OrElse Not sample.DoorOpen.HasValue Then Return "Door contact UNKNOWN"
        Return If(sample.DoorOpen.Value, "Door OPEN", "Door CLOSED")
    End Function

    Private Shared Function MeterIndication(asset As FactoryMapAsset, snapshots As IReadOnlyList(Of MeterSnapshot),
                                            ratings As IReadOnlyDictionary(Of String, Double)) As MapIndication
        Dim meter = FactoryMapMeterLink.Find(asset, snapshots)
        If meter Is Nothing Then Return New MapIndication(MapSignal.Unknown, "No verified current meter source")
        Dim independent = FactoryMapMeterLink.Supports(asset.Model)
        Dim transformerSource = PowerEventTracker.TransformerNames.Contains(meter.TransformerName, StringComparer.OrdinalIgnoreCase)
        Dim limits = If(transformerSource, AlarmSettingsStore.Current.Transformers, AlarmSettingsStore.Current.Loads)
        Dim voltages As Double?() = {MeterValue(meter, 0), MeterValue(meter, 1), MeterValue(meter, 2)}
        Dim maximum As Double
        Dim load As Double? = Nothing
        If ratings IsNot Nothing AndAlso ratings.TryGetValue(meter.TransformerName, maximum) Then load = LoadGaugeMath.GetLoadPercent(MeterValue(meter, 10), maximum)
        Dim condition = AlarmEvaluator.Evaluate(load, MeterValue(meter, 15), If(meter.PowerState = TransformerPowerState.Off, Array.Empty(Of Double?)(), voltages), limits)
        If condition.Severity <> AlarmSeverity.Normal Then
            Return New MapIndication(If(condition.Severity >= AlarmSeverity.Alarm, MapSignal.Fault, MapSignal.Warning),
                If(independent, "Meter " & meter.TransformerName & ": ", "") & condition.Title, condition.Detail & Environment.NewLine & "Source meter: " & meter.TransformerName)
        End If
        If meter.PowerState = TransformerPowerState.Off Then Return New MapIndication(MapSignal.Warning, "Equipment power OFF", meter.PowerDetail)
        If Not voltages.All(Function(value) value.HasValue) OrElse Not load.HasValue OrElse Not MeterValue(meter, 15).HasValue Then
            Return New MapIndication(MapSignal.Unknown, "No alarm in available meter data / coverage incomplete", "Missing channels or rated load prevent a complete evaluation. Source meter: " & meter.TransformerName)
        End If
        Return New MapIndication(MapSignal.Normal, "NORMAL", "Source meter: " & meter.TransformerName & "; PLC temperature source remains unverified.")
    End Function

    Public Shared Function EvaluateAsset(asset As FactoryMapAsset, snapshots As IReadOnlyList(Of MeterSnapshot),
                                 upsSnapshots As IReadOnlyDictionary(Of String, UpsSnapshot), devices As MonitoringDeviceRegistry,
                                 fire As IReadOnlyDictionary(Of String, IReadOnlyList(Of FireMapEventEvidence)),
                                 Optional live As IReadOnlyDictionary(Of String, MonitoringSample) = Nothing,
                                 Optional ratings As IReadOnlyDictionary(Of String, Double) = Nothing) As MapIndication
        Dim upsId = If(asset.Category = "Battery Rooms", asset.ParentUpsId, asset.EquipmentId)
        If FactoryMapMeterLink.Supports(asset.Model) Then Return MeterIndication(asset, snapshots, ratings)
        If upsId.StartsWith("UPS-", StringComparison.OrdinalIgnoreCase) Then Return UpsIndication(upsId, asset.Category = "Battery Rooms", upsSnapshots)
        If asset.MonitoringDeviceId <> "" Then
            Dim device = devices.Devices.FirstOrDefault(Function(item) item.Id = asset.MonitoringDeviceId)
            If device Is Nothing Then Return New MapIndication(MapSignal.Unknown, "Registered device unavailable")
            If Not DeviceMatches(asset, device) Then Return New MapIndication(MapSignal.Unknown, "Monitoring device type mismatch", "Registered device kind does not match this map symbol; correct the link.")
            If device.Kind = "Fire" AndAlso Not device.Commissioned Then
                Return New MapIndication(MapSignal.Unknown, "Drawing device - addressing NOT commissioned",
                    "Drawing tag: " & device.DrawingTag & Environment.NewLine & "Reference: " & device.DrawingReference &
                    Environment.NewLine & "No panel/loop/address or live alarm relationship has been inferred from a drawing tag.")
            End If
            Dim sample As MonitoringSample = Nothing
            If live IsNot Nothing AndAlso live.TryGetValue(device.Id, sample) Then
                If Not sample.IsCurrent Then Return New MapIndication(MapSignal.Unknown, "Device state unverified / stale", "Previous values are not current status.")
                Dim signal = If(sample.Signal = MapSignal.Fire AndAlso device.Kind <> "Fire", MapSignal.Fault, sample.Signal)
                If device.Kind = "Fire" AndAlso signal = MapSignal.Fault Then signal = MapSignal.Warning
                Dim text = signal.ToString()
                If signal >= MapSignal.Warning AndAlso Not String.IsNullOrWhiteSpace(sample.Detail) Then text &= ": " & sample.Detail
                If device.Kind = "Access" Then text &= " / " & DoorState(sample)
                Return New MapIndication(signal, text, sample.Detail)
            End If
            If device.Kind = "Fire" Then
                Dim evidence As IReadOnlyList(Of FireMapEventEvidence) = Nothing
                If device.EventAddress <> "" AndAlso device.SiteId <> "" AndAlso fire.TryGetValue(device.PanelId, evidence) Then
                    Dim matches = evidence.Where(Function(item) item.SiteId = device.SiteId AndAlso item.Address = device.EventAddress AndAlso
                        (device.Zone = "" OrElse item.Zone = device.Zone)).OrderByDescending(Function(item) item.TimestampUtc).ToArray()
                    If matches.Length > 0 Then
                        Dim latest = matches(0)
                        Return New MapIndication(MapSignal.Unknown, "Fire device EVENT HISTORY - live state UNKNOWN",
                            "Latest matched event: " & latest.Category & " / " & latest.Message & Environment.NewLine &
                            "Event UTC: " & If(latest.TimestampUtc.HasValue, latest.TimestampUtc.Value.ToString("u"), "--") &
                            Environment.NewLine & "History may include restores/resets; no live fire/fault inference and no room-fire animation.")
                    End If
                End If
                Return New MapIndication(MapSignal.Unknown, "Fire device live state UNKNOWN", "No exact addressed event evidence. No title/location guessing. Live panel condition is not supplied by the existing history connector.")
            End If
            Return New MapIndication(MapSignal.Unknown, If(device.Kind = "Camera", "Camera stream not commissioned", If(device.Kind = "Access", "Door contact / personnel feed not commissioned", "Equipment status feed not commissioned")),
                                     "Registered endpoint only. No live state or people have been inferred.")
        End If
        Return MeterIndication(asset, snapshots, ratings)
    End Function
End Class
