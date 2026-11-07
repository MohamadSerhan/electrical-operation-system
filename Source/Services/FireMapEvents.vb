Imports System.Globalization
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.RegularExpressions

Friend NotInheritable Class FireEventIdentity
    Public Property Address As String = ""
    Public Property Zone As String = ""
    Public Property [Loop] As String = ""
    Public Property AddressSource As String = ""
    Public Property Conflict As Boolean

    Public Shared Function Parse(record As ZetaFireEvent) As FireEventIdentity
        Dim result As New FireEventIdentity
        Dim text = record.Description & " " & record.Message
        Dim describedAddress = Extract(text, "\b(?:device\s+address|address|addr)\s*[:=#-]?\s*([A-Z0-9]+(?:[./:_-][A-Z0-9]+)*)")
        If describedAddress = "" Then describedAddress = Extract(record.Message, "\bFault with\s+(\d+)\s*,")
        Dim describedZone = Extract(text, "\bzone\s*[:=#-]?\s*([A-Z0-9]+(?:[./_-][A-Z0-9]+)*)")
        Dim describedLoop = Extract(text, "\bloop\s*[:=#-]?\s*(\d+)")
        result.Address = If(record.DeviceAddress <> "", record.DeviceAddress.Trim(), describedAddress)
        result.Zone = If(record.Zone <> "", record.Zone.Trim(), describedZone)
        result.Loop = describedLoop
        result.AddressSource = If(record.DeviceAddress <> "", "event field", If(describedAddress <> "", "description/message", "not supplied"))
        Dim structuredParts = AddressParts(record.DeviceAddress)
        Dim descriptionParts = AddressParts(describedAddress)
        If result.Loop = "" Then result.Loop = If(structuredParts.Loop <> "", structuredParts.Loop, descriptionParts.Loop)
        Dim faultAddress = Extract(record.Message, "\bFault with\s+(\d+)\s*,")
        result.Conflict = (record.DeviceAddress <> "" AndAlso describedAddress <> "" AndAlso
            Not SameAddress(record.DeviceAddress, describedAddress)) OrElse
            (record.Zone <> "" AndAlso describedZone <> "" AndAlso Canonical(record.Zone) <> Canonical(describedZone))
        If faultAddress <> "" AndAlso result.Address <> "" AndAlso Not SameAddress(faultAddress, result.Address) Then result.Conflict = True
        If describedLoop <> "" AndAlso structuredParts.Loop <> "" AndAlso Canonical(describedLoop) <> structuredParts.Loop Then result.Conflict = True
        If structuredParts.Loop <> "" AndAlso descriptionParts.Loop <> "" AndAlso structuredParts.Loop <> descriptionParts.Loop Then result.Conflict = True
        If describedLoop <> "" AndAlso descriptionParts.Loop <> "" AndAlso Canonical(describedLoop) <> descriptionParts.Loop Then result.Conflict = True
        If Regex.Matches(text, "\bloop\s*[:=#-]?\s*(\d+)", RegexOptions.IgnoreCase).
            Select(Function(match) Canonical(match.Groups(1).Value)).Distinct().Count() > 1 Then result.Conflict = True
        Dim allAddresses = Regex.Matches(text, "\b(?:device\s+address|address|addr)\s*[:=#-]?\s*([A-Z0-9]+(?:[./:_-][A-Z0-9]+)*)", RegexOptions.IgnoreCase).
            Select(Function(match) Canonical(match.Groups(1).Value)).Distinct().ToArray()
        If allAddresses.Length > 1 OrElse Regex.Matches(text, "\bzone\s*[:=#-]?\s*(\d+)", RegexOptions.IgnoreCase).
            Select(Function(match) Canonical(match.Groups(1).Value)).Distinct().Count() > 1 Then result.Conflict = True
        Return result
    End Function

    Private Shared Function Extract(text As String, pattern As String) As String
        Dim matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase)
        Dim values = matches.Select(Function(match) match.Groups(1).Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        Return If(values.Length = 1 AndAlso Regex.IsMatch(values(0), "\d"), values(0), "")
    End Function

    Public Shared Function AddressParts(value As String) As ([Loop] As String, Address As String)
        Dim match = Regex.Match(If(value, "").Trim(), "^(?:L|LOOP\s*)(\d+)\s*[/:-]\s*(?:A|ADDR(?:ESS)?\s*)?(\d+)$", RegexOptions.IgnoreCase)
        Return If(match.Success, (Canonical(match.Groups(1).Value), Canonical(match.Groups(2).Value)), ("", Canonical(value)))
    End Function

    Private Shared Function SameAddress(left As String, right As String) As Boolean
        Dim a = AddressParts(left)
        Dim b = AddressParts(right)
        Return a.Address = b.Address AndAlso (a.Loop = "" OrElse b.Loop = "" OrElse a.Loop = b.Loop)
    End Function

    Public Shared Function Canonical(value As String) As String
        Dim text = If(value, "").Trim().ToUpperInvariant()
        Dim number As Integer
        Return If(Integer.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, number), number.ToString(CultureInfo.InvariantCulture), text)
    End Function

    Public Shared Function Key(record As ZetaFireEvent) As String
        Dim identity = If(record.EventId <> "", "id:" & record.EventId,
            String.Join(vbNullChar, {record.TimestampUtc?.ToString("O"), record.EventType, record.Zone, record.DeviceAddress, record.Description, record.Message}))
        Return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(record.SiteId & vbNullChar & identity)))
    End Function

    Public Shared Function Signal(record As ZetaFireEvent) As MapSignal
        Dim category = record.EventType.Trim().ToLowerInvariant()
        If Regex.IsMatch(record.Message, "\b(restor(?:e|ed|al)|cleared|normal|test|reset)\b", RegexOptions.IgnoreCase) Then Return MapSignal.Unknown
        If {"fire", "fire alarm", "alarm"}.Contains(category) Then Return MapSignal.Fire
        Return If({"fault", "trouble"}.Contains(category), MapSignal.Warning, MapSignal.Unknown)
    End Function
End Class

Friend NotInheritable Class FireEventLocation
    Public Property Area As String = ""
    Public Property Location As String = ""
    Public Property DescribedPanel As String = ""
    Public Property DeviceType As String = ""
    Public Property Source As String = "not supplied"
    Public Property Conflict As Boolean

    Public ReadOnly Property Display As String
        Get
            Dim value = String.Join(" - ", {Area, Location}.Where(Function(part) part <> ""))
            Return If(Conflict, value & " [conflicting location]", If(value = "", "Unresolved", value))
        End Get
    End Property

    Public Shared Function Parse(description As String, message As String) As FireEventLocation
        Dim text = description & " " & message
        Dim result As New FireEventLocation
        Dim areas = Regex.Matches(text, "\bAREA\s*[:#-]?\s*(\d+)\b", RegexOptions.IgnoreCase).
            Select(Function(match) "Area " & FireEventIdentity.Canonical(match.Groups(1).Value)).Distinct().ToArray()
        result.Area = String.Join(" / ", areas)
        Dim locations As New List(Of String)
        Dim aliases As (Name As String, Pattern As String)() = {
            ("Panel room", "\b(?:MAIN\s+)?PANEL[\s-]*ROOM\b"),
            ("UPS room", "\bUPS[\s-]*ROOM\b"),
            ("Basement electrical room", "\b(?:UNDER[\s-]*GROUND|UNDEGROUND|BASEMENT)\s+(?:ELECTRICAL\s+)?(?:PANEL|ROOM|LVDB)\b|\b(?:PANEL|LVDB)\s+(?:UNDER[\s-]*GROUND|BASEMENT)\b|\bBASEMENT\s+LVDB\b"),
            ("Generator bank", "\bGEN[\s-]*SET(?:\s+AREA)?\b|\bGENERATOR(?:S)?(?:\s+(?:ROOM|AREA|BANK))?\b"),
            ("Compressor room", "\bCOMPRESSOR(?:S)?[\s-]*(?:ROOM|AREA)\b"),
            ("Transformer room", "\bTRANSFORMER(?:S)?[\s-]*(?:ROOM|AREA)\b"),
            ("Battery room", "\bBATTER(?:Y|IES)[\s-]*ROOM\b"),
            ("Syrup room", "\bSYRUP[\s-]*(?:ROOM|AREA)\b"),
            ("Cold room", "\bCOLD[\s-]*ROOM\b"),
            ("Warehouse", "\bWAREHOUSE\b"),
            ("Laboratory", "\b(?:LABORATORY|LAB\s+ROOM)\b"),
            ("Filtration room", "\bFILTRATION[\s-]*ROOM\b"),
            ("Water treatment", "\bWATER[\s-]*TREATMENT\b")}
        For Each aliasItem In aliases
            If Regex.IsMatch(text, aliasItem.Pattern, RegexOptions.IgnoreCase) Then locations.Add(aliasItem.Name)
        Next
        For Each match As Match In Regex.Matches(text, "\bPET[\s-]*([1-6])\b", RegexOptions.IgnoreCase)
            locations.Add("PET-" & match.Groups(1).Value)
        Next
        For Each match As Match In Regex.Matches(text, "\bCAN[\s-]*LINE(?:[\s-]*(\d+))?\b", RegexOptions.IgnoreCase)
            locations.Add(If(match.Groups(1).Success, "Canline-" & match.Groups(1).Value, "Canline"))
        Next
        Dim distinct = locations.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        result.Location = String.Join(" / ", distinct)
        result.Conflict = areas.Length > 1 OrElse distinct.Length > 1
        If Regex.IsMatch(text, "\b(?:UNDER[\s-]*GROUND|UNDEGROUND|BASEMENT)\s+PANEL\b|\bPANEL\s+(?:UNDER[\s-]*GROUND|BASEMENT)\b", RegexOptions.IgnoreCase) Then
            result.DescribedPanel = "Underground panel"
        ElseIf Regex.IsMatch(text, "\bUPS[\s-]*ROOM\s+PANEL\b", RegexOptions.IgnoreCase) Then
            result.DescribedPanel = "UPS room panel"
        ElseIf Regex.IsMatch(text, "\b(?:MAIN\s+)?PANEL[\s-]*ROOM(?:\s+PANEL)?\b", RegexOptions.IgnoreCase) Then
            result.DescribedPanel = If(Regex.IsMatch(text, "\bMAIN\s+PANEL", RegexOptions.IgnoreCase), "Main panel room panel", "Panel room panel")
        End If
        If result.DescribedPanel <> "" Then
            Dim rating = Regex.Match(text, "\b(?:UPS\s*)?(\d+)\s*KVA\b", RegexOptions.IgnoreCase)
            If rating.Success Then result.DescribedPanel &= " / UPS " & rating.Groups(1).Value & " kVA"
        End If
        Dim types As (Name As String, Pattern As String)() = {
            ("Sounder module", "\bSND\s+MODULE\b|\bSOUNDER\s+(?:CIRCUIT\s+CONTROLLER|MODULE)\b"),
            ("Input/output module", "\bINPUT\s*/\s*OUTPUT\s+MODULE\b|\bI/O\s+MODULE\b"),
            ("Smoke detector", "\bSMOKE\s+DETECTOR\b|\bSD(?:[\s-]|\d)"),
            ("Heat detector", "\bHEAT\s+DETECTOR\b"),
            ("Manual call point", "\b(?:MCP|MANUAL\s+CALL\s+POINT)\b"),
            ("Sounder / siren", "\b(?:SIREN|SOUNDER)\b")}
        For Each kind In types
            If Regex.IsMatch(text, kind.Pattern, RegexOptions.IgnoreCase) Then
                result.DeviceType = kind.Name
                Exit For
            End If
        Next
        If result.Area <> "" OrElse result.Location <> "" OrElse result.DescribedPanel <> "" Then result.Source = "description/message (inferred)"
        Return result
    End Function

    Public Shared Function ResolveRelated(events As IReadOnlyList(Of FireMapEventEvidence)) As IReadOnlyDictionary(Of String, FireEventLocation)
        Dim result As New Dictionary(Of String, FireEventLocation)(StringComparer.Ordinal)
        Dim groups = events.Where(Function(item) item.Address <> "" AndAlso Not item.IdentityConflict AndAlso
                item.TimestampUtc.HasValue AndAlso item.TimestampUtc.Value <= DateTimeOffset.UtcNow).
            DistinctBy(Function(item) (item.SiteId, item.Loop, item.Address, item.Zone, item.LocationInfo.Area, item.LocationInfo.Location,
                item.LocationInfo.DescribedPanel, item.LocationInfo.Conflict)).
            GroupBy(Function(item) (item.SiteId, FireEventIdentity.AddressParts(item.Address).Address)).
            ToDictionary(Function(group) group.Key, Function(group) group.ToArray())
        For Each item In events
            Dim own = item.LocationInfo
            result(item.EventKey) = own
            If item.IdentityConflict OrElse own.Conflict OrElse item.Address = "" OrElse Not item.TimestampUtc.HasValue OrElse item.TimestampUtc.Value > DateTimeOffset.UtcNow Then Continue For
            Dim related = groups((item.SiteId, FireEventIdentity.AddressParts(item.Address).Address)).
                Where(Function(other) FireEventIdentity.Canonical(other.Loop) = FireEventIdentity.Canonical(item.Loop) AndAlso
                    Not other.LocationInfo.Conflict AndAlso other.LocationInfo.Location <> "" AndAlso
                    (item.Zone = "" OrElse other.Zone = "" OrElse FireEventIdentity.Canonical(item.Zone) = FireEventIdentity.Canonical(other.Zone))).ToArray()
            Dim locations = related.Select(Function(other) other.LocationInfo.Location).Distinct().ToArray()
            Dim areas = related.Select(Function(other) other.LocationInfo.Area).Where(Function(area) area <> "").Distinct().ToArray()
            Dim panels = related.Select(Function(other) other.LocationInfo.DescribedPanel).Where(Function(name) name <> "").Distinct().ToArray()
            If locations.Length > 1 OrElse areas.Length > 1 OrElse panels.Length > 1 OrElse
                (own.Location <> "" AndAlso locations.Any(Function(value) value <> own.Location)) OrElse
                (own.Area <> "" AndAlso areas.Any(Function(value) value <> own.Area)) Then
                result(item.EventKey) = New FireEventLocation With {.Area = own.Area, .Location = own.Location, .DescribedPanel = own.DescribedPanel,
                    .DeviceType = own.DeviceType, .Source = "conflicting related events for this panel/site/loop/address", .Conflict = True}
            ElseIf own.Location = "" AndAlso locations.Length = 1 Then
                result(item.EventKey) = New FireEventLocation With {.Area = If(own.Area <> "", own.Area, areas.FirstOrDefault("")),
                    .Location = locations(0), .DescribedPanel = panels.FirstOrDefault(""), .DeviceType = own.DeviceType,
                    .Source = "related event with same panel/site/loop/address; event " & String.Join(", ", related.Select(Function(other) other.EventId).Distinct().Take(3))}
            End If
        Next
        Return result
    End Function
End Class

Friend NotInheritable Class FireMapMatch
    Public Property PanelId As String = ""
    Public Property Evidence As FireMapEventEvidence
    Public Property AssetId As String = ""
    Public Property RoomId As String = ""
    Public Property Level As String = ""
    Public Property MatchReason As String = ""
    Public Property LocationInfo As FireEventLocation

    Public Function Indication() As MapIndication
        Dim item = Evidence
        Return New MapIndication(item.Signal, If(item.Signal = MapSignal.Fire, "FIRE", "FAULT") & " - latched event / " & MatchReason,
            "Panel / site: " & PanelId & " / " & item.SiteId & Environment.NewLine &
            "Event: " & item.EventId & " / " & If(item.TimestampUtc.HasValue, item.TimestampUtc.Value.ToString("u"), "--") & Environment.NewLine &
            "Address: " & If(item.Address = "", "--", item.Address) & " (" & item.AddressSource & "); loop: " & item.Loop & "; zone: " & item.Zone & Environment.NewLine &
            "Area / location: " & If(LocationInfo, item.LocationInfo).Display & "; described panel: " & If(LocationInfo, item.LocationInfo).DescribedPanel & Environment.NewLine &
            "Location source: " & If(LocationInfo, item.LocationInfo).Source & "; device type: " & item.LocationInfo.DeviceType & Environment.NewLine &
            item.Description & Environment.NewLine & item.Message & Environment.NewLine &
            "Match: " & MatchReason & Environment.NewLine &
            "Latched historical event, NOT verified current panel state. Reset Map Alarms clears this local indication only.")
    End Function
End Class

Friend NotInheritable Class FireMapEventResolver
    Private Shared Function Words(value As String) As String
        Return " " & Regex.Replace(If(value, "").ToUpperInvariant(), "[^A-Z0-9]+", " ").Trim() & " "
    End Function

    Private Shared Function Mentions(text As String, name As String) As Boolean
        Dim normalized = Words(name)
        Return normalized.Trim().Length >= 3 AndAlso Words(text).Contains(normalized, StringComparison.Ordinal)
    End Function

    Public Shared Function Resolve(panel As String, item As FireMapEventEvidence, assets As IReadOnlyList(Of FactoryMapAsset),
                                    parts As IReadOnlyList(Of FactoryBuildingPart), registry As MonitoringDeviceRegistry,
                                    allowUnscopedDrawing As Boolean) As FireMapMatch
        Return ResolveLocated(panel, item, assets, parts, registry, allowUnscopedDrawing, item.LocationInfo)
    End Function

    Public Shared Function ResolveLocated(panel As String, item As FireMapEventEvidence, assets As IReadOnlyList(Of FactoryMapAsset),
                                          parts As IReadOnlyList(Of FactoryBuildingPart), registry As MonitoringDeviceRegistry,
                                          allowUnscopedDrawing As Boolean, location As FireEventLocation) As FireMapMatch
        Dim result As New FireMapMatch With {.PanelId = panel, .Evidence = item, .LocationInfo = location}
        If item.IdentityConflict Then
            result.MatchReason = "Unmapped: conflicting address/zone evidence"
            Return result
        End If
        Dim scoped = registry.Devices.Where(Function(device) device.Kind = "Fire" AndAlso
            ((device.PanelId = panel AndAlso device.SiteId = item.SiteId) OrElse
             (allowUnscopedDrawing AndAlso Not device.Commissioned AndAlso device.PanelId = "" AndAlso device.SiteId = ""))).ToArray()
        Dim candidates = scoped.Where(Function(device) device.Commissioned AndAlso item.Address <> "" AndAlso
            ((device.EventAddress <> "" AndAlso FireEventIdentity.Canonical(device.EventAddress) = FireEventIdentity.Canonical(item.Address)) OrElse
             (item.Loop <> "" AndAlso FireEventIdentity.Canonical(device.Loop) = FireEventIdentity.Canonical(item.Loop) AndAlso
              FireEventIdentity.Canonical(device.Address) = FireEventIdentity.AddressParts(item.Address).Address)) AndAlso
            (device.Zone = "" OrElse item.Zone = "" OrElse FireEventIdentity.Canonical(device.Zone) = FireEventIdentity.Canonical(item.Zone))).ToArray()
        If candidates.Length > 0 Then Return DeviceResult(result, candidates, assets, "exact panel/site/address")
        Dim text = item.Description & " " & item.Message
        candidates = scoped.Where(Function(device) device.DrawingTag <> "" AndAlso Mentions(text, device.DrawingTag)).ToArray()
        If candidates.Length > 0 Then Return DeviceResult(result, candidates, assets, "drawing tag in description (inferred, not commissioned)")
        candidates = scoped.Where(Function(device) Regex.IsMatch(device.Name, "\d|[\s-]") AndAlso Mentions(text, device.Name)).ToArray()
        If candidates.Length > 0 Then Return DeviceResult(result, candidates, assets, "unique device name in description (inferred)")
        Dim rooms = parts.Where(Function(part) part.RoomId <> "").GroupBy(Function(part) (part.Level, part.RoomId)).Select(Function(group) group.First()).ToArray()
        Dim zoneRooms = rooms.Where(Function(room) room.FirePanelId = panel AndAlso room.FireSiteId = item.SiteId AndAlso
            item.Zone <> "" AndAlso room.FireZone <> "" AndAlso FireEventIdentity.Canonical(room.FireZone) = FireEventIdentity.Canonical(item.Zone)).ToArray()
        If zoneRooms.Length = 0 Then
            If location.Conflict Then
                result.MatchReason = "Unmapped: conflicting described/related locations"
                Return result
            End If
            Dim eligible = rooms.Where(Function(room) allowUnscopedDrawing OrElse (room.FirePanelId = panel AndAlso room.FireSiteId = item.SiteId)).ToArray()
            zoneRooms = eligible.Where(Function(room) Mentions(text, room.RoomName) OrElse ProductionRoom(text, room.RoomName) OrElse
                Mentions(location.Location, room.RoomName.Split("/"c)(0).Trim()) OrElse
                (location.Location = "Basement electrical room" AndAlso room.Level = "Basement" AndAlso
                 Regex.IsMatch(room.RoomName, "\b(?:LVDB|PANEL|ELECTRICAL)\b", RegexOptions.IgnoreCase))).ToArray()
            If zoneRooms.Length = 0 AndAlso location.Location = "UPS room" Then
                zoneRooms = eligible.Where(Function(room) assets.Any(Function(asset) asset.RoomId = room.RoomId AndAlso asset.Level = room.Level AndAlso asset.Category = "UPS")).ToArray()
            End If
        End If
        If zoneRooms.Length = 1 Then
            result.RoomId = zoneRooms(0).RoomId
            result.Level = zoneRooms(0).Level
            result.MatchReason = "room/zone only: " & zoneRooms(0).RoomName & " (device not uniquely identified; " & location.Source & ")"
        Else
            result.MatchReason = If(zoneRooms.Length > 1, "Unmapped: ambiguous room/zone", "Unmapped: no unique device or room; commissioning required")
        End If
        Return result
    End Function

    Private Shared Function ProductionRoom(text As String, name As String) As Boolean
        Dim room = Regex.Match(name, "^PET[\s-]*([1-6])$", RegexOptions.IgnoreCase)
        Return room.Success AndAlso Regex.IsMatch(text, "\bPET[\s-]*" & room.Groups(1).Value & "\b", RegexOptions.IgnoreCase)
    End Function

    Private Shared Function DeviceResult(result As FireMapMatch, devices As MonitoringDevice(), assets As IReadOnlyList(Of FactoryMapAsset), reason As String) As FireMapMatch
        Dim ids = devices.Select(Function(device) device.Id).ToHashSet()
        Dim mapped = assets.Where(Function(asset) asset.Category = "Fire System" AndAlso asset.IsPlaced AndAlso ids.Contains(asset.MonitoringDeviceId)).ToArray()
        If devices.Length = 1 AndAlso mapped.Length = 1 Then
            result.AssetId = mapped(0).Id
            result.Level = mapped(0).Level
            result.MatchReason = reason
        Else
            result.MatchReason = "Unmapped: ambiguous or missing map symbol (" & reason & ")"
        End If
        Return result
    End Function
End Class
