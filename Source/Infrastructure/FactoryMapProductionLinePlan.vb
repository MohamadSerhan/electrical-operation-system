Imports System.Drawing
Imports System.IO

Friend NotInheritable Class FactoryMapProductionConnection
    Public Property Line As String
    Public Property FromId As String
    Public Property ToId As String
    Public Property Medium As String
    Public Property ElevationUnits As Single
    Public Property Route As PointF()
End Class

Friend NotInheritable Class FactoryMapProductionLinePlan
    Public ReadOnly Assets As List(Of FactoryMapAsset)
    Public ReadOnly Building As List(Of FactoryBuildingPart)
    Public ReadOnly Connections As New List(Of FactoryMapProductionConnection)
    Public ReadOnly Notes As New List(Of String)
    Private _line As String
    Private _room As String
    Private _brand As String

    Public Sub New(assets As IEnumerable(Of FactoryMapAsset), building As IEnumerable(Of FactoryBuildingPart))
        Me.Assets = assets.Select(Function(a) a.Clone()).ToList()
        Me.Building = building.Select(Function(p) p.Clone()).ToList()
    End Sub

    Public Sub Build()
        If Assets.Any(Function(a) a.Id.StartsWith("FLOW-", StringComparison.Ordinal)) Then
            Throw New InvalidDataException("A complete-line proposal already exists; do not duplicate it.")
        End If
        For Each line In {"PET1", "PET2", "PET3", "PET4", "PET5", "PET6", "CAN1"}
            BuildLine(line)
        Next
        Notes.Add("PROPOSED schematic only: manufacturer GA drawings, rated speeds, SKU sizes, sanitary design, utility sizing, guarding, maintenance/escape clearances and conveyor wall penetrations require site/OEM approval.")
        Notes.Add("PET4/PET5: still water. PET1/PET2/PET3/PET6 and CAN1: CSD. PET5/PET6 retain Krones identification; their integrated architecture is an assumption to verify.")
        Notes.Add("Selected secondary packaging is shrink/tray packs. Case packers remain unplaced as an alternative, not an extra serial stage. Preprinted cans do not require a bottle labeller.")
    End Sub

    Private Function UseAsset(id As String, model As String, x As Single, y As Single, Optional rotation As Single = 0) As FactoryMapAsset
        Dim matches = Assets.Where(Function(a) a.Id = id).ToArray()
        If matches.Length > 1 Then Throw New InvalidDataException("Duplicate asset ID: " & id)
        Dim asset = matches.SingleOrDefault()
        If asset Is Nothing Then
            asset = New FactoryMapAsset With {.Id = id, .EquipmentId = "", .Source = "Proposed visual equipment; no acquisition source"}
            Assets.Add(asset)
        End If
        asset.Name = _line & " " & _brand & " PROPOSED / " & model
        asset.Model = model
        asset.CustomEquipment = Nothing
        Dim categories = FactoryMapItemForm.Shapes.Where(Function(entry) entry.Value.Contains(model)).Select(Function(entry) entry.Key).ToArray()
        If categories.Length = 0 Then Throw New InvalidDataException("No map catalog model exists for proposed equipment: " & model)
        asset.Category = categories(0)
        asset.Position = New PointF(x / 1365, y / 766)
        asset.Rotation = rotation
        asset.ScaleX = 1 : asset.ScaleY = 1 : asset.ScaleZ = 1
        asset.BaseHeightMeters = 0 : asset.Level = "Ground"
        asset.IsPlaced = True
        asset.RoomId = _room
        asset.Location = "PROPOSED " & _line & " process layout; dimensions and site installation unverified"
        Return asset
    End Function

    Private Sub RetireDuplicate(asset As FactoryMapAsset)
        asset.IsPlaced = False
        asset.Location = "Retained catalog item; superseded in the proposed integrated line. IDs and links preserved."
    End Sub

    Private Sub BuildLine(line As String)
        _line = line
        _brand = If(line = "PET5" OrElse line = "PET6", "Krones", "Sidel")
        _room = If(line = "PET5", "DRAW-ROOM-P5", If(line = "PET6", "DRAW-ROOM-P6", "REF-ROOM-" & line))
        Dim floor = Building.Single(Function(p) p.Kind = "Floor" AndAlso p.RoomId = _room AndAlso p.Level = "Ground")
        Dim cx = floor.X * 1365, cy = floor.Y * 766
        Dim west = cx - floor.Length / 2, east = cx + floor.Length / 2
        Dim reverse = line = "PET5" OrElse line = "PET6"
        Dim direction As Single = If(reverse, -1, 1)
        Dim coreX = If(reverse, cx, west + 84)
        Dim water = line = "PET4" OrElse line = "PET5"
        Dim cans = line = "CAN1"
        If cans Then coreX = west + 96
        Dim coreId = If(line = "CAN1", "CANLINE-1", If(line = "PET3", "REF-PET3-FILLER", "LINE-" & line))
        Dim prefix = "COMPLETE-" & line & "-"
        Dim plan = "COMPLETE-PLAN-" & line & "-"
        Dim core = UseAsset(coreId, If(cans, "Can filler and seamer - carbonated drinks",
            If(water, "PET Combi - still water", "PET Combi - carbonated drinks")), coreX, cy, If(reverse, 180, 0))
        Dim coreSize = FactoryMapEquipmentSizes.Dimensions(core)
        If coreSize.Length * 8 > floor.Length - 8 OrElse coreSize.Width * 8 > floor.Depth - 8 Then
            Throw New InvalidDataException(line & " proposed filling block does not fit its room. Do not shrink equipment.")
        End If
        For Each old In Assets.Where(Function(a) a.RoomId = _room AndAlso a.IsPlaced AndAlso
            (a.Model = "Conveyor" OrElse a.Id = prefix & "OUTFEED" OrElse
             cans AndAlso (a.Model = "Can seamer" OrElse a.Id = prefix & "07"))).ToArray()
            RetireDuplicate(old)
        Next
        If cans Then
            Dim depal = UseAsset(prefix & "01", "Empty container depalletizer", west + 24, cy)
            Dim rinse = UseAsset("FLOW-" & line & "-RINSER", "Empty-can inverter and rinser", west + 53, cy)
            rinse.Name = line & " PROPOSED / Empty-can rinsing/inverting station"
            Connect(depal, rinse, "empty cans", {New PointF(west + 40, cy), New PointF(west + 47, cy)})
            Connect(rinse, core, "rinsed cans", {New PointF(west + 61, cy), New PointF(coreX - 28, cy)})
            Dim ends = UseAsset("FLOW-" & line & "-ENDS", "Can end magazine", coreX + 16, cy - 22)
            Connect(ends, core, "can ends", {New PointF(coreX + 16, cy - 18), New PointF(coreX + 22, cy - 18), New PointF(coreX + 22, cy)}, 18)
            For Each old In Assets.Where(Function(a) a.Id = prefix & "02" OrElse a.Id = prefix & "05" OrElse a.Id = prefix & "07").ToArray()
                RetireDuplicate(old)
            Next
        Else
            Dim bridge As FactoryMapAsset, sorter As FactoryMapAsset
            If reverse Then
                Dim feedFloor = floor.Clone()
                feedFloor.Id = "PROPOSED-" & line & "-FEED-FLOOR"
                feedFloor.Name = line & " PROPOSED adjoining preform/cap feed zone"
                feedFloor.RoomId = "PROPOSED-ROOM-" & line & "-FEED"
                feedFloor.RoomName = feedFloor.Name
                feedFloor.X = (east + 48) / 1365 : feedFloor.Y = cy / 766
                feedFloor.Length = 96 : feedFloor.Depth = 88
                feedFloor.Style = "Rectangle" : feedFloor.Rotation = 0 : feedFloor.DrawingPoints.Clear()
                feedFloor.ParentWallId = "" : feedFloor.MonitoringDeviceId = ""
                feedFloor.TemperatureSourceId = "" : feedFloor.TemperatureSourceConfirmed = False
                feedFloor.FirePanelId = "" : feedFloor.FireSiteId = "" : feedFloor.FireZone = ""
                Building.Add(feedFloor)
                _room = feedFloor.RoomId
                Dim cage = UseAsset(prefix & "01", "Preform cage and tipper", east + 74, cy + 26)
                Dim hopper = UseAsset(prefix & "05", "Preform hopper", east + 47, cy + 26)
                Dim elevator = UseAsset(plan & "17", "Preform elevator", east + 47, cy + 8, 180)
                bridge = UseAsset("FLOW-" & line & "-PREFORM-BRIDGE", "Preform roller sorter and bridge", east + 22, cy + 8, 180)
                Connect(cage, hopper, "bulk preforms", {New PointF(east + 62, cy + 26), New PointF(east + 53.4F, cy + 26)}, 6)
                Connect(hopper, elevator, "preforms to elevator", {New PointF(east + 47, cy + 21.2F), New PointF(east + 47, cy + 12.4F)}, 6)
                Connect(elevator, bridge, "oriented preforms", {New PointF(east + 37, cy + 8), New PointF(east + 34, cy + 8)}, 18)
                Dim caps = UseAsset(prefix & "07", "Cap elevator", east + 70, cy - 24, 180)
                sorter = UseAsset(plan & "18", "Cap sorter and chute", east + 46, cy - 24, 180)
                Connect(caps, sorter, "loose caps", {New PointF(east + 62.8F, cy - 24), New PointF(east + 50.8F, cy - 24)}, 18)
                Dim transfer = AddTransfer(floor, east, cy + 8, "preform and cap infeed")
                Connect(bridge, core, "preforms to oven", {New PointF(east + 10, cy + 8), New PointF(east + 8, cy + 8),
                    New PointF(east + 8, transfer.Y), New PointF(east - 8, transfer.Y),
                    New PointF(east - 8, cy + 8), New PointF(coreX + 40, cy + 8)}, 10)
                Connect(sorter, core, "oriented caps to capper", {New PointF(east + 41.2F, cy - 24), New PointF(east + 12, cy - 24),
                    New PointF(east + 12, transfer.Y), New PointF(east - 12, transfer.Y),
                    New PointF(east - 12, cy - 14), New PointF(coreX - 32, cy - 14), New PointF(coreX - 32, cy)}, 12)
                Notes.Add(line & ": full-size Combi retained in Krones room; feeds moved to a PROPOSED adjoining zone. This zone is not an existing surveyed building; land, enclosure, hygiene, access and structural suitability need verification.")
                _room = floor.RoomId
            Else
                Dim cage = UseAsset(prefix & "01", "Preform cage and tipper", west + 17, cy + 27)
                Dim hopper = UseAsset(prefix & "05", "Preform hopper", west + 44, cy + 27)
                Dim elevator = UseAsset(plan & "17", "Preform elevator", west + 70, cy + 27)
                bridge = UseAsset("FLOW-" & line & "-PREFORM-BRIDGE", "Preform roller sorter and bridge", west + 96, cy + 27)
                Connect(cage, hopper, "bulk preforms", {New PointF(west + 29, cy + 27), New PointF(west + 37.6F, cy + 27)}, 6)
                Connect(hopper, elevator, "preforms to elevator", {New PointF(west + 50.4F, cy + 27), New PointF(west + 60, cy + 27)}, 6)
                Connect(elevator, bridge, "oriented preforms", {New PointF(west + 80, cy + 27), New PointF(west + 84, cy + 27)}, 18)
                Connect(bridge, core, "preforms to oven", {New PointF(west + 108, cy + 27),
                    New PointF(west + 108, cy + 18), New PointF(coreX - 6, cy + 18), New PointF(coreX - 6, cy + 8)}, 18)
                Dim caps = UseAsset(prefix & "07", "Cap elevator", coreX + 12, cy - 30)
                sorter = UseAsset(plan & "18", "Cap sorter and chute", coreX + 34, cy - 30)
                Connect(caps, sorter, "loose caps", {New PointF(coreX + 19.2F, cy - 30), New PointF(coreX + 29.2F, cy - 30)}, 18)
                Connect(sorter, core, "oriented caps to capper", {New PointF(coreX + 38.8F, cy - 30),
                    New PointF(coreX + 40, cy - 30), New PointF(coreX + 40, cy)}, 16)
            End If
        End If

        Dim controlIndex = 0
        For Each id In {prefix & "08", prefix & "13", prefix & "14"}
            Dim existing = Assets.SingleOrDefault(Function(a) a.Id = id)
            If existing Is Nothing Then Continue For
            UseAsset(id, existing.Model, If(cans, coreX + 50 + controlIndex * 22,
                If(reverse, coreX - 24 + controlIndex * 22, west + 20 + controlIndex * 27)), cy - If(cans, 22, 30))
            controlIndex += 1
        Next
        If water Then
            For Each old In Assets.Where(Function(a) a.Id = plan & "20" OrElse a.Id = plan & "21").ToArray()
                RetireDuplicate(old)
            Next
        End If
        Dim utilityNames = If(cans, {"Plant air", "Cooling supply and return", "Electrical supply"}, {"Blow air", "Plant air", "Cooling supply and return", "Electrical supply"})
        For index = 0 To utilityNames.Length - 1
            Dim ux = coreX - 30 + index * 18
            Dim uy = cy + If(cans, 22, If(reverse, 27, 44))
            Dim endpoint = UseAsset("FLOW-" & line & "-UTILITY-" & index, "Utility cabinet", ux, uy)
            endpoint.Name = line & " PROPOSED / " & utilityNames(index) & " interface (site rating unverified)"
            Connect(endpoint, core, utilityNames(index) & " interface", {New PointF(ux, uy - 3), New PointF(ux, cy + 14)}, 22)
        Next

        Dim lane = If(line = "PET1", 775, If(line = "PET2", 670, If(line = "CAN1", 550,
            If(line = "PET3", 410, If(line = "PET4", 342, If(line = "PET5", 700, 480))))))
        Dim startX As Single = If(reverse, 1190, 824)
        Dim packingRoom = If(lane < 438, "DRAW-ROOM-EXT", "DRAW-ROOM-PROD")
        _room = packingRoom
        Dim stages As New List(Of FactoryMapAsset)
        Dim cursor = startX
        For Each stage In {("07", "Bottle inspection station"), ("08", "Bottle reject station"),
            ("DRY", "Bottle air knife dryer"), ("LABEL", "Bottle labeler"), ("09", "Date coding station"),
            ("10", "Bottle accumulation table"), ("11", "Shrink wrapper"), ("13", "Palletizer"), ("14", "Pallet stretch wrapper")}
            If cans AndAlso stage.Item1 = "LABEL" Then Continue For
            Dim id = If(stage.Item1 = "LABEL", prefix & "PACK-06", If(stage.Item1 = "DRY", "FLOW-" & line & "-DRYER", plan & stage.Item1))
            Dim nominal = FactoryMapEquipmentSizes.Nominal(stage.Item2)
            cursor += direction * nominal.Length * 4
            Dim asset = UseAsset(id, stage.Item2, cursor, lane, If(reverse, 180, 0))
            If cans AndAlso stage.Item1 = "07" Then asset.Name = line & " PROPOSED / Fill-level and seam inspection"
            If cans AndAlso stage.Item1 = "08" Then asset.Name = line & " PROPOSED / Can reject station"
            stages.Add(asset)
            cursor += direction * (nominal.Length * 4 + 6)
        Next
        Dim exitX = If(reverse, west, east)
        Dim outfeed = AddTransfer(floor, exitX, cy, "bottle/can outfeed")
        Dim outfeedCorridor = If(line = "PET4", 794, exitX + direction * 8)
        Dim first = stages(0)
        Dim coreOut = New PointF(coreX + direction * coreSize.Length * 4, cy)
        Connect(core, first, If(cans, "sealed cans", "filled capped bottles"),
            {coreOut, New PointF(exitX - direction * 8, cy), New PointF(exitX - direction * 8, outfeed.Y),
             New PointF(outfeedCorridor, outfeed.Y), New PointF(outfeedCorridor, lane), New PointF(startX, lane)})
        For index = 1 To stages.Count - 1
            Dim previous = stages(index - 1), current = stages(index)
            Dim a = previous.Position.X * 1365 + direction * FactoryMapEquipmentSizes.Dimensions(previous).Length * 4
            Dim b = current.Position.X * 1365 - direction * FactoryMapEquipmentSizes.Dimensions(current).Length * 4
            Connect(previous, current, If(previous.Model = "Palletizer", "loaded pallets",
                If(previous.Model = "Shrink wrapper", "shrink packs", "bottles/cans")), {New PointF(a, lane), New PointF(b, lane)})
        Next
        Dim palletSide As Single = If(line = "PET2", -1, 1)
        Dim emptyPallets = UseAsset("FLOW-" & line & "-EMPTY-PALLETS", "Pallet stack", stages(stages.Count - 2).Position.X * 1365, lane + palletSide * 27)
        Connect(emptyPallets, stages(stages.Count - 2), "empty pallets", {New PointF(emptyPallets.Position.X * 1365, lane + palletSide * 20), New PointF(emptyPallets.Position.X * 1365, lane + palletSide * 12)}, 1)
        Dim utilityX = startX + If(line = "PET4", 0, direction * 55)
        Dim utilityY = lane - 30
        Dim supply = UseAsset(plan & "01", "Product buffer tank", utilityX, utilityY)
        Dim pump = UseAsset(plan & "02", "Sanitary product pump", utilityX + direction * 16, utilityY)
        Connect(supply, pump, "treated water/product", {New PointF(utilityX + direction * 6, utilityY), New PointF(utilityX + direction * 10, utilityY)}, 5)
        Dim product = pump
        If Not water Then
            Dim mixer = UseAsset(plan & "20", "Syrup mixing skid", utilityX + direction * 38, utilityY)
            Dim carbonator = UseAsset(plan & "21", "Beverage carbonator", utilityX + direction * 64, utilityY)
            Connect(pump, mixer, "water and syrup blending", {New PointF(utilityX + direction * 22, utilityY), New PointF(utilityX + direction * 28, utilityY)}, 7)
            Connect(mixer, carbonator, "blended drink", {New PointF(utilityX + direction * 49, utilityY), New PointF(utilityX + direction * 54, utilityY)}, 7)
            product = carbonator
            Dim co2 = UseAsset("FLOW-" & line & "-CO2", "Utility cabinet", utilityX + direction * 64, utilityY - 15)
            co2.Name = line & " PROPOSED / Food-grade CO2 interface (source/rating unverified)"
            Connect(co2, carbonator, "food-grade CO2", {New PointF(utilityX + direction * 64, utilityY - 12), New PointF(utilityX + direction * 64, utilityY)}, 12)
            Dim ingredients = UseAsset("FLOW-" & line & "-INGREDIENTS", "Utility cabinet", utilityX + direction * 38, utilityY - 15)
            ingredients.Name = line & " PROPOSED / Treated water and syrup interfaces (site supplies unverified)"
            Connect(ingredients, mixer, "treated water and syrup", {New PointF(utilityX + direction * 38, utilityY - 12), New PointF(utilityX + direction * 38, utilityY)}, 10)
            Notes.Add(line & ": deaerated treated-water, syrup supply and regulated CO2 supply are site-utility interfaces; refrigeration/pressure ratings unverified.")
        Else
            Dim waterSupply = UseAsset("FLOW-" & line & "-WATER", "Utility cabinet", utilityX, utilityY - 16)
            waterSupply.Name = line & " PROPOSED / Treated drinking-water interface (site source unverified)"
            Connect(waterSupply, supply, "treated drinking water", {New PointF(utilityX, utilityY - 13), New PointF(utilityX, utilityY)}, 12)
        End If
        Dim service = AddTransfer(floor, exitX, cy + If(line = "PET4", 12, -12), "product and CIP services")
        Dim serviceCorridor = If(line = "PET4", 794, exitX + direction * 12)
        Connect(product, core, If(water, "treated still water", "carbonated beverage"),
            {New PointF(product.Position.X * 1365, utilityY), New PointF(exitX + direction * 10, utilityY),
             New PointF(exitX + direction * 10, service.Y), New PointF(exitX - direction * 10, service.Y),
             New PointF(exitX - direction * 10, cy - 12), New PointF(coreX + direction * 12, cy - 12)}, 14)
        Dim cipY As Single = If(line = "CAN1", 610, If(line = "PET2", 721, If(line = "PET4", 456, lane + 32)))
        Dim cip = UseAsset(plan & "04", "CIP station - 1 tank", utilityX, cipY)
        Dim manifold = UseAsset(plan & "03", "Sanitary valve manifold", utilityX + direction * 22, cipY)
        Dim recovery = UseAsset(plan & "05", "CIP return skid", utilityX + direction * 48, cipY)
        Connect(cip, manifold, "CIP supply", {New PointF(utilityX + direction * 10, cipY), New PointF(utilityX + direction * 12, cipY)}, 5)
        Connect(manifold, core, "CIP supply", {New PointF(manifold.Position.X * 1365, cipY), New PointF(serviceCorridor, cipY),
            New PointF(serviceCorridor, service.Y), New PointF(exitX - direction * 12, service.Y),
            New PointF(exitX - direction * 12, cy - 12), New PointF(coreX, cy - 12)}, 16)
        Connect(core, recovery, "CIP return", {New PointF(coreX, cy - 12), New PointF(exitX - direction * 14, cy - 12),
            New PointF(exitX - direction * 14, service.Y), New PointF(serviceCorridor + direction * 2, service.Y),
            New PointF(serviceCorridor + direction * 2, cipY), New PointF(recovery.Position.X * 1365, cipY)}, 18)
        Connect(recovery, cip, "CIP recovered solution", {New PointF(recovery.Position.X * 1365, cipY + 4), New PointF(cip.Position.X * 1365, cipY + 4)}, 4)
    End Sub

    Private Function AddTransfer(floor As FactoryBuildingPart, x As Single, y As Single, purpose As String) As PointF
        Dim wall = Building.Where(Function(p) p.Kind = "Wall" AndAlso p.RoomId = floor.RoomId AndAlso Math.Abs(Math.Cos(p.Rotation * Math.PI / 180)) < 0.1).
            OrderBy(Function(p) Math.Abs(p.X * 1365 - x) + Math.Abs(p.Y * 766 - y)).First()
        If Math.Abs(wall.X * 1365 - x) > 10 Then Throw New InvalidDataException(_line & " has no matching proposed transfer wall.")
        Dim length As Single = If(purpose.StartsWith("product", StringComparison.Ordinal), 5, 8)
        Dim preferred = CSng((y - wall.Y * 766) * If(Math.Sin(wall.Rotation * Math.PI / 180) < 0, -1, 1))
        Dim offsets = {preferred, preferred - 12, preferred + 12, preferred - 24, preferred + 24, preferred - 36, preferred + 36}
        Dim openings = Building.Where(Function(p) p.ParentWallId = wall.Id).ToArray()
        Dim available = offsets.Where(Function(offset) Math.Abs(offset) + length / 2 <= wall.Length / 2 - 2 AndAlso
            openings.All(Function(p) Math.Abs(p.Offset - offset) >= (p.Length + length) / 2 + 2)).ToArray()
        If available.Length = 0 Then Throw New InvalidDataException(_line & ": no non-overlapping proposed conveyor/service opening fits this wall.")
        Dim selectedOffset = available(0)
        Dim center = FactoryMapBuildingStore.WorldPoint(wall, selectedOffset, 0)
        Dim id = "FLOW-" & _line & "-OPENING-" & If(purpose.StartsWith("product", StringComparison.Ordinal), "SERVICE",
            If(purpose.StartsWith("preform", StringComparison.Ordinal), "INFEED", "OUTFEED"))
        Building.Add(New FactoryBuildingPart With {.Id = id, .Name = _line & " PROPOSED / " & purpose & " wall opening",
            .Kind = "Gate", .Style = "Sliding gate", .Material = "Steel", .BodyColor = "#B7C9D3",
            .X = center.X / 1365, .Y = center.Y / 766, .Length = length,
            .Depth = wall.Depth, .HeightMeters = If(purpose.StartsWith("product", StringComparison.Ordinal), 2.2F, 1.5F), .BaseMeters = 0.4F, .Level = "Ground",
            .ParentWallId = wall.Id, .Offset = selectedOffset, .Rotation = wall.Rotation,
            .RoomId = floor.RoomId, .RoomName = floor.RoomName, .IsOpen = True})
        Return center
    End Function

    Private Sub Connect(a As FactoryMapAsset, b As FactoryMapAsset, medium As String, route As PointF(), Optional z As Single = 6)
        Dim clean = route.Where(Function(p, i) i = 0 OrElse p <> route(i - 1)).ToArray()
        If clean.Length < 2 Then Throw New InvalidDataException("A process connection needs distinct endpoints.")
        Dim index = Connections.Where(Function(c) c.Line = _line).Count() + 1
        Connections.Add(New FactoryMapProductionConnection With {.Line = _line, .FromId = a.Id, .ToId = b.Id, .Medium = medium, .ElevationUnits = z, .Route = clean})
        Dim geometry As New FactoryMapCadGeometry
        Dim liquid = medium.Contains("CIP", StringComparison.Ordinal) OrElse medium.Contains("water", StringComparison.Ordinal) OrElse
            medium.Contains("beverage", StringComparison.Ordinal) OrElse medium.Contains("drink", StringComparison.Ordinal) OrElse
            medium.Contains("product", StringComparison.Ordinal) OrElse medium.Contains("interface", StringComparison.Ordinal) OrElse medium.Contains("CO2", StringComparison.Ordinal)
        Dim width As Single = If(liquid, 0.5F, If(medium.Contains("pallet", StringComparison.Ordinal), 8, 3.5F))
        For i = 1 To clean.Length - 1
            Dim p = clean(i - 1), q = clean(i)
            If p.X <> q.X AndAlso p.Y <> q.Y Then Throw New InvalidDataException("Connections must use orthogonal segments.")
            AddBox(geometry, Math.Min(p.X, q.X) - width / 2, Math.Min(p.Y, q.Y) - width / 2, z,
                   Math.Abs(q.X - p.X) + width, Math.Abs(q.Y - p.Y) + width, If(liquid, 0.5F, 0.6F))
            If Not liquid Then
                Dim distance = Math.Abs(q.X - p.X) + Math.Abs(q.Y - p.Y)
                For stepIndex = 0 To CInt(distance / 24)
                    Dim t = If(distance > 0, Math.Min(1, stepIndex * 24 / distance), 0)
                    AddBox(geometry, p.X + (q.X - p.X) * t - 0.25F, p.Y + (q.Y - p.Y) * t - 0.25F, 0, 0.5F, 0.5F, z)
                Next
            End If
        Next
        Dim minX = geometry.Vertices.Min(Function(p) p.X), maxX = geometry.Vertices.Max(Function(p) p.X)
        Dim minY = geometry.Vertices.Min(Function(p) p.Y), maxY = geometry.Vertices.Max(Function(p) p.Y)
        Dim definition = FactoryMapCadImporter.FromGeometry(_line & " " & medium, geometry)
        definition.ImportNotes = "PROPOSED process connection: " & a.Name & " -> " & b.Name & ". Route/height and penetrations require site approval."
        Dim asset = FactoryMapCustomEquipmentStore.CreateAsset(definition)
        asset.Id = "FLOW-" & _line & "-LINK-" & index
        asset.Name = _line & " PROPOSED / " & medium
        asset.Position = New PointF(CSng((minX + maxX) / 2 / 1365), CSng((minY + maxY) / 2 / 766))
        asset.BaseHeightMeters = If(liquid, z / 8, 0)
        asset.BodyColor = If(liquid, If(medium.Contains("CIP", StringComparison.Ordinal), "#AD8DBB", "#79A9BA"), "#87A9B6")
        asset.RoomId = _room : asset.Level = "Ground" : asset.IsPlaced = True
        asset.Location = definition.ImportNotes
        Assets.Add(asset)
    End Sub

    Private Shared Sub AddBox(mesh As FactoryMapCadGeometry, x As Single, y As Single, z As Single, w As Single, d As Single, h As Single)
        Dim start = mesh.Vertices.Count
        For Each p In {New FactoryMapCadPoint With {.X = x, .Y = y, .Z = z}, New FactoryMapCadPoint With {.X = x + w, .Y = y, .Z = z},
            New FactoryMapCadPoint With {.X = x + w, .Y = y + d, .Z = z}, New FactoryMapCadPoint With {.X = x, .Y = y + d, .Z = z},
            New FactoryMapCadPoint With {.X = x, .Y = y, .Z = z + h}, New FactoryMapCadPoint With {.X = x + w, .Y = y, .Z = z + h},
            New FactoryMapCadPoint With {.X = x + w, .Y = y + d, .Z = z + h}, New FactoryMapCadPoint With {.X = x, .Y = y + d, .Z = z + h}}
            mesh.Vertices.Add(p)
        Next
        For Each face In {New Integer() {0, 1, 5, 4}, New Integer() {1, 2, 6, 5}, New Integer() {2, 3, 7, 6},
                          New Integer() {3, 0, 4, 7}, New Integer() {4, 5, 6, 7}, New Integer() {0, 3, 2, 1}}
            mesh.Faces.Add(face.Select(Function(i) i + start).ToArray())
        Next
    End Sub
End Class
