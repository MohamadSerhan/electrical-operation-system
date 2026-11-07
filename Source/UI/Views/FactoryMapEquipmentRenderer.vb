Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Linq

Friend NotInheritable Class FactoryMapEquipmentRenderer
    Private NotInheritable Class PaintContext
        Public Graphics As Graphics
        Public Scale As Single = 1
        Public Clip As RectangleF?
    End Class
    Private ReadOnly _context As PaintContext
    Private ReadOnly Property _graphics As Graphics
        Get
            Return _context.Graphics
        End Get
    End Property
    Private ReadOnly _project As Func(Of Single, Single, Single, PointF)
    Private ReadOnly _hitArea As GraphicsPath
    Private ReadOnly _bodyColor As Color?
    Private ReadOnly _commands As New List(Of (Depth As Single, Paint As Action))
    Private ReadOnly _depthX As Single
    Private ReadOnly _depthY As Single
    Private ReadOnly _depthZ As Single
    Private ReadOnly _reflected As Boolean
    Private ReadOnly _measure As Action(Of Single, Single, Single)
    Private ReadOnly _geometryOnly As Boolean
    Private _refinedFinish As Boolean
    Private Shared ReadOnly RawSizes As New System.Collections.Concurrent.ConcurrentDictionary(Of String, (Length As Single, Width As Single, Height As Single))
    Private Structure Vertex
        Public Point As PointF
        Public Depth As Single
    End Structure
    Private Shared ReadOnly Steel As Color = Color.FromArgb(192, 204, 211)
    Private Shared ReadOnly Dark As Color = Color.FromArgb(35, 44, 51)
    Private Shared ReadOnly Yellow As Color = Color.FromArgb(250, 198, 29)

    Private Sub New(graphics As Graphics, project As Func(Of Single, Single, Single, PointF), hitArea As GraphicsPath, bodyColor As String,
                    Optional measure As Action(Of Single, Single, Single) = Nothing, Optional geometryOnly As Boolean = False,
                    Optional context As PaintContext = Nothing, Optional reflected As Boolean = False)
        _context = If(context, New PaintContext With {.Graphics = graphics})
        _project = project
        _hitArea = hitArea
        _measure = measure
        _geometryOnly = geometryOnly
        _reflected = reflected
        Dim origin = project(0, 0, 0)
        Dim xAxis = project(1, 0, 0)
        Dim yAxis = project(0, 1, 0)
        Dim zAxis = project(0, 0, 1)
        Dim a = xAxis.X - origin.X
        Dim b = yAxis.X - origin.X
        Dim c = xAxis.Y - origin.Y
        Dim d = yAxis.Y - origin.Y
        Dim e = zAxis.Y - origin.Y
        Dim nx = b * e
        Dim ny = -a * e
        Dim nz = a * d - b * c
        Dim magnitude = Math.Max(0.000001F, CSng(Math.Sqrt(nx * nx + ny * ny + nz * nz)))
        Dim handedness As Single = If(reflected, -1, 1)
        _depthX = nx / magnitude * handedness
        _depthY = ny / magnitude * handedness
        _depthZ = nz / magnitude * handedness
        If Not String.IsNullOrEmpty(bodyColor) Then _bodyColor = ColorTranslator.FromHtml(bodyColor)
    End Sub

    Friend Shared Function MirrorProjection(asset As FactoryMapAsset, project As Func(Of Single, Single, Single, PointF)) As Func(Of Single, Single, Single, PointF)
        Return Function(x, y, z) project(x * If(asset.MirrorHorizontal, -1, 1), y * If(asset.MirrorVertical, -1, 1), z)
    End Function

    Public Shared Sub Draw(graphics As Graphics, asset As FactoryMapAsset,
                           project As Func(Of Single, Single, Single, PointF), hitArea As GraphicsPath)
        If asset.Model = FactoryMapCustomEquipmentStore.ModelName Then
            Using renderer As New FactoryMapCustomEquipmentRenderer()
                renderer.Draw(graphics, asset, project, hitArea)
            End Using
            Return
        End If
        Dim size = FactoryMapEquipmentSizes.ScaleFor(asset)
        DrawCore(graphics, asset, Function(x, y, z) project(x * size.X, y * size.Y, z * size.Z), hitArea)
    End Sub

    Friend NotInheritable Class VectorDrawing
        Implements IDisposable
        Private ReadOnly _renderer As FactoryMapEquipmentRenderer
        Private ReadOnly _commands As Action()
        Public ReadOnly Path As GraphicsPath
        Public ReadOnly Bounds As RectangleF

        Public Sub New(asset As FactoryMapAsset, project As Func(Of Single, Single, Single, PointF))
            Path = New GraphicsPath(FillMode.Winding)
            Dim left = Single.MaxValue, top = Single.MaxValue, right = Single.MinValue, bottom = Single.MinValue
            Using bitmap As New Bitmap(1, 1), graphics = Drawing.Graphics.FromImage(bitmap)
                Dim size = FactoryMapEquipmentSizes.ScaleFor(asset)
                _renderer = Build(graphics, asset,
                    Function(x, y, z)
                        Dim point = project(x * size.X, y * size.Y, z * size.Z)
                        left = Math.Min(left, point.X) : top = Math.Min(top, point.Y)
                        right = Math.Max(right, point.X) : bottom = Math.Max(bottom, point.Y)
                        Return point
                    End Function, Path)
            End Using
            _commands = _renderer._commands.OrderBy(Function(item) item.Depth).Select(Function(item) item.Paint).ToArray()
            Bounds = RectangleF.FromLTRB(left, top, right, bottom)
        End Sub

        Public Sub Paint(graphics As Graphics, center As PointF, scale As Single, Optional detailScale As Single = 0)
            Dim state = graphics.Save()
            Try
                _renderer._context.Graphics = graphics
                _renderer._context.Scale = If(detailScale > 0, detailScale, scale)
                graphics.TranslateTransform(center.X, center.Y)
                graphics.ScaleTransform(scale, scale)
                If _renderer._refinedFinish Then graphics.SmoothingMode = SmoothingMode.AntiAlias
                Dim clip = graphics.VisibleClipBounds
                clip.Inflate(1 / scale, 1 / scale)
                _renderer._context.Clip = clip
                For Each command As Action In _commands
                    command()
                Next
            Finally
                _renderer._context.Clip = Nothing
                graphics.Restore(state)
            End Try
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Path.Dispose()
        End Sub
    End Class

    Public Shared Function Measure(graphics As Graphics, asset As FactoryMapAsset,
                                   project As Func(Of Single, Single, Single, PointF), hitArea As GraphicsPath) As RectangleF
        Dim size = FactoryMapEquipmentSizes.ScaleFor(asset)
        Dim scaled As Func(Of Single, Single, Single, PointF) = Function(x, y, z) project(x * size.X, y * size.Y, z * size.Z)
        Dim left = Single.MaxValue, top = Single.MaxValue, right = Single.MinValue, bottom = Single.MinValue
        Dim observe As Action(Of Single, Single, Single) =
            Sub(x, y, z)
                Dim point = scaled(x, y, z)
                left = Math.Min(left, point.X) : right = Math.Max(right, point.X)
                top = Math.Min(top, point.Y) : bottom = Math.Max(bottom, point.Y)
            End Sub
        DrawCore(graphics, asset, scaled, hitArea, observe, geometryOnly:=True)
        Return If(IsOpeningModel(asset.Model), hitArea.GetBounds(), RectangleF.FromLTRB(left, top, right, bottom))
    End Function

    Public Shared Function RawSize(asset As FactoryMapAsset) As (Length As Single, Width As Single, Height As Single)
        If asset.Model = FactoryMapCustomEquipmentStore.ModelName Then
            Dim size = FactoryMapEquipmentSizes.Dimensions(asset)
            Return (size.Length * 8, size.Width * 8, size.Height * 8)
        End If
        Dim key = asset.Model
        If FactoryMapEquipmentSizes.IsVariableLength(asset.Model) Then key &= "|" & asset.ConveyorLength.ToString(Globalization.CultureInfo.InvariantCulture)
        Return RawSizes.GetOrAdd(key,
            Function(unused As String) As (Length As Single, Width As Single, Height As Single)
                Dim minX = Single.MaxValue, minY = Single.MaxValue, minZ As Single = 0
                Dim maxX = Single.MinValue, maxY = Single.MinValue, maxZ = Single.MinValue
                Dim observe As Action(Of Single, Single, Single) =
                    Sub(x, y, z)
                        minX = Math.Min(minX, x) : maxX = Math.Max(maxX, x)
                        minY = Math.Min(minY, y) : maxY = Math.Max(maxY, y)
                        minZ = Math.Min(minZ, z) : maxZ = Math.Max(maxZ, z)
                    End Sub
                Using bitmap As New Bitmap(1, 1), measuring = System.Drawing.Graphics.FromImage(bitmap), hit As New GraphicsPath(FillMode.Winding)
                    For Each direction In {-1.0F, 1.0F}
                        Dim facing = direction
                        DrawCore(measuring, asset, Function(x, y, z) New PointF((x - y) * facing, (x + y) * 0.5F - z), hit, observe)
                    Next
                End Using
                If maxX <= minX OrElse maxY <= minY OrElse maxZ <= minZ Then Throw New IO.InvalidDataException("Invalid equipment geometry for " & asset.Model & ".")
                Return (maxX - minX, maxY - minY, maxZ - minZ)
            End Function)
    End Function

    Friend Shared Sub DrawImportedGeometry(graphics As Graphics, asset As FactoryMapAsset,
                                           project As Func(Of Single, Single, Single, PointF), hit As GraphicsPath)
        Dim definition = asset.CustomEquipment
        Dim renderer As New FactoryMapEquipmentRenderer(graphics, MirrorProjection(asset, project), hit, asset.BodyColor,
            reflected:=asset.MirrorHorizontal Xor asset.MirrorVertical)
        renderer._refinedFinish = True
        Dim points = definition.CadGeometry.Vertices.Select(Function(p) New FactoryMapCadPoint With {
            .X = p.X * definition.LengthMeters * 8, .Y = p.Y * definition.WidthMeters * 8,
            .Z = p.Z * definition.HeightMeters * 8}).ToArray()
        Dim color = renderer.Body(Steel)
        For Each indices As Integer() In definition.CadGeometry.Faces
            Dim vertices = indices.Select(Function(i) renderer.V(points(i).X, points(i).Y, points(i).Z)).ToArray()
            If definition.CadGeometry.IsPlan Then
                renderer.ImportedPath(indices.Concat({indices(0)}).ToArray(), points, renderer.Body(Dark))
            Else
                Dim a = points(indices(0)), b = points(indices(1)), c = points(indices(2))
                Dim nx = (b.Y - a.Y) * (c.Z - a.Z) - (b.Z - a.Z) * (c.Y - a.Y)
                Dim ny = (b.Z - a.Z) * (c.X - a.X) - (b.X - a.X) * (c.Z - a.Z)
                Dim nz = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)
                Dim magnitude = CSng(Math.Sqrt(nx * nx + ny * ny + nz * nz))
                Dim light = If(magnitude > 0, 0.72F + 0.25F * Math.Abs((nx * 0.3F - ny * 0.4F + nz * 0.86F) / magnitude), 0.85F)
                renderer.Face(vertices, Shade(color, light))
            End If
        Next
        For Each path In definition.CadGeometry.Paths
            renderer.ImportedPath(path, points, renderer.Body(Dark))
        Next
        Dim state = graphics.Save()
        Try
            graphics.SmoothingMode = SmoothingMode.AntiAlias
            For Each drawCommand In renderer._commands.OrderBy(Function(item) item.Depth)
                drawCommand.Paint()
            Next
        Finally
            graphics.Restore(state)
        End Try
    End Sub

    Private Sub ImportedPath(indices As Integer(), points As FactoryMapCadPoint(), color As Color)
        For index = 1 To indices.Length - 1
            Dim a = points(indices(index - 1)), b = points(indices(index))
            Line(a.X, a.Y, a.Z, b.X, b.Y, b.Z, color)
            Dim first = _project(a.X, a.Y, a.Z), last = _project(b.X, b.Y, b.Z)
            If first = last Then Continue For
            Using path As New GraphicsPath(), pen As New Pen(Color.Black, 5)
                path.AddLine(first, last)
                path.Widen(pen)
                _hitArea.AddPath(path, False)
            End Using
        Next
    End Sub

    Private Shared Sub DrawCore(graphics As Graphics, asset As FactoryMapAsset,
                                project As Func(Of Single, Single, Single, PointF), hitArea As GraphicsPath,
                                Optional measure As Action(Of Single, Single, Single) = Nothing, Optional geometryOnly As Boolean = False)
        If IsOpeningModel(asset.Model) Then
            Dim size = FactoryMapEquipmentSizes.Nominal(asset.Model)
            Dim part As New FactoryBuildingPart With {
                .Kind = If(asset.Model.EndsWith("door", StringComparison.Ordinal), "Door", "Gate"),
                .X = 0, .Y = 0, .Length = size.Length * FactoryMapEquipmentSizes.UnitsPerMeter,
                .Depth = size.Width * FactoryMapEquipmentSizes.UnitsPerMeter, .HeightMeters = size.Height,
                .Style = If(asset.Model = "Double swing gate", "Double swing",
                    If(asset.Model = "Sliding gate", "Sliding gate", If(asset.Model = "Rapid roll-up gate", "Rapid roll-up",
                    If(asset.Model.EndsWith("door", StringComparison.Ordinal), "Single leaf", "Single swing")))),
                .Material = If(asset.Model = "Glass entry door", "Glass", If(asset.Model = "Wood entry door", "Wood", "Steel")),
                .BodyColor = If(String.IsNullOrEmpty(asset.BodyColor), If(asset.Model = "Wood entry door", "#B2885B", "#65A8C6"), asset.BodyColor),
                .IsOpen = asset.IsOpen, .OpeningDirection = asset.OpeningDirection
            }
            FactoryMapBuildingRenderer.Draw(graphics, part, {part}, 40, project, hitArea)
            Return
        End If
        Dim renderer = Build(graphics, asset, project, hitArea, measure, geometryOnly)
        Dim state = graphics.Save()
        Try
            If renderer._refinedFinish Then graphics.SmoothingMode = SmoothingMode.AntiAlias
            For Each drawCommand In renderer._commands.OrderBy(Function(item) item.Depth)
                drawCommand.Paint()
            Next
        Finally
            graphics.Restore(state)
        End Try
    End Sub

    Private Shared Function Build(graphics As Graphics, asset As FactoryMapAsset,
                                  project As Func(Of Single, Single, Single, PointF), hitArea As GraphicsPath,
                                  Optional measure As Action(Of Single, Single, Single) = Nothing,
                                  Optional geometryOnly As Boolean = False) As FactoryMapEquipmentRenderer
        Dim renderer As New FactoryMapEquipmentRenderer(graphics, If(measure Is Nothing, MirrorProjection(asset, project), project),
            hitArea, asset.BodyColor, measure, geometryOnly, reflected:=measure Is Nothing AndAlso (asset.MirrorHorizontal Xor asset.MirrorVertical))
        Select Case asset.Model
            Case "PET Combi - still water", "PET Combi - carbonated drinks", "Can filler and seamer - carbonated drinks"
                renderer.IntegratedFillingBlock(asset.Model)
            Case "Preform cage and tipper", "Preform roller sorter and bridge", "Can end magazine", "Empty-can inverter and rinser"
                renderer.ProductionFeed(asset.Model)
            Case FactoryMapCustomEquipmentStore.SidelModelName : renderer.SidelBlowerAssembly()
            Case "CIP station - 1 tank" : renderer.CipStation(1)
            Case "CIP station - 2 tanks" : renderer.CipStation(2)
            Case "CIP station - 3 tanks" : renderer.CipStation(3)
            Case "Krones-style mixing and carbonation skid" : renderer.ReferenceProcessSkid(False)
            Case "Water treatment process skid" : renderer.ReferenceProcessSkid(True)
            Case "Smoke detector", "Zeta fire panel", "Zeta repeater panel", "Zeta smoke detector", "Zeta heat detector",
                 "Zeta multi-sensor detector", "Zeta manual call point", "Zeta sounder", "Zeta sounder beacon", "Zeta siren", "Zeta beam detector",
                 "Zeta interface module", "Zeta isolator module", "Fire suppression control panel", "CO2 cylinder bank", "FM200 cylinder bank"
                renderer.FireMonitoringDevice(asset.Model)
            Case "Preform elevator", "Cap elevator", "Cap sorter and chute", "Cap sterilizer", "Bottle unscrambler",
                 "Rinser filler capper monoblock", "Air conveyor blower", "HEPA filler canopy",
                 "Product buffer tank", "Sanitary product pump", "Sanitary valve manifold", "CIP return skid",
                 "Bottle air knife dryer", "Bottle reject station", "Inline checkweigher", "Inline metal detector",
                 "Can seamer", "Empty container depalletizer", "Returnable bottle washer", "Foam cleaning station"
                renderer.FillerAccessory(asset.Model)
            Case "Ultrafiltration skid", "Ultrafiltration twin rack", "Reverse osmosis skid", "Sand filter vessel", "Activated carbon filter",
                 "Water softener skid", "UV sterilizer skid", "Chemical dosing skid", "Water booster pump skid", "Water treatment control panel",
                 "Stainless water tank", "Stainless process vat", "CIP cleaning skid"
                renderer.WaterSystem(asset.Model)
            Case "PET bottle blower", "Bottle rinser", "Rotary bottle filler", "Bottle capper", "Bottle labeler", "Shrink wrapper",
                 "Bottle accumulation table", "Filler control panel", "Air conveyor", "Conveyor corner"
                renderer.FillerSystem(asset.Model, asset.ConveyorLength)
            Case "Caterpillar generator" : renderer.Generator()
            Case "Caterpillar generator 810 kVA" : renderer.RatedGenerator(False)
            Case "Caterpillar generator 1500 kVA" : renderer.RatedGenerator(True)
            Case "HP compressor 40 bar" : renderer.Compressor()
            Case "HP compressor 40 bar with control and power panels"
                renderer.Compressor()
                renderer.ServicePanels(35)
            Case "Atlas Copco compressor 7 bar with control and power panels"
                renderer.AirPlant("Atlas Copco compressor 7 bar")
                renderer.ServicePanels(35)
            Case "Atlas Copco compressor 7 bar", "Refrigerated air dryer", "Compressed air filter bank"
                renderer.AirPlant(asset.Model)
            Case "Indoor wall AC 1.5 HP", "Indoor wall AC 2.5 HP", "Indoor cassette AC 5 HP", "Indoor ducted AC 10 HP", "Indoor floor AC 20 HP",
                 "Outdoor AC 1.5 HP", "Outdoor AC 2.5 HP", "Outdoor AC 5 HP", "Outdoor AC 10 HP", "Outdoor AC 20 HP",
                 "VRV outdoor system", "VRV indoor cassette", "Air handling unit"
                renderer.Hvac(asset.Model)
            Case "Vertiv EXL 500 kVA" : renderer.Ups(False)
            Case "Vertiv NXL 800 kVA" : renderer.Ups(True)
            Case "Chiller" : renderer.Chiller()
            Case "Chiller with control and power panels"
                renderer.Chiller()
                renderer.ServicePanels(40)
            Case "Hansa chiller" : renderer.HansaChiller(False)
            Case "Hansa chiller with control and power panels"
                renderer.HansaChiller(True)
            Case "Air-cooled screw chiller", "Water-cooled screw chiller", "Centrifugal water-cooled chiller", "Chiller pump and buffer skid"
                renderer.ChillerPlant(asset.Model)
            Case "Compressor control panel", "Chiller control panel"
                renderer.ControlCabinet("HMI console")
            Case "Compressor power panel", "Chiller power panel"
                renderer.PowerPanel("ACB", False)
            Case "Hansa chiller cooling tower", "HP compressor cooling tower 40 bar"
                renderer.ProcessTower(asset.Model = "HP compressor cooling tower 40 bar")
            Case "Battery rack - 4 levels" : renderer.BatteryRack()
            Case "Beverage line with filler" : renderer.ProductionLine(asset.ConveyorLength, True)
            Case "Conveyor" : renderer.ProductionLine(asset.ConveyorLength, False)
            Case "PET bottling line", "Can filling line", "Glass bottling line"
                renderer.CompleteLine(asset.Model, asset.ConveyorLength)
            Case "Syrup mixing skid", "Beverage carbonator", "Plate pasteurizer", "Bottle inspection station", "Date coding station",
                 "Case packer", "Palletizer", "Pallet stretch wrapper", "Preform hopper",
                 "Steam boiler", "Forklift", "Pallet rack", "Pallet stack", "Loading dock",
                 "Office desk", "Office chair", "Office cabinet"
                renderer.FactoryUtility(asset.Model)
            Case "Transformer" : renderer.Transformer()
            Case "Wall panel", "Wall panel with energy meter" : renderer.ElectricalPanel("Wall", asset.Model.EndsWith("with energy meter", StringComparison.Ordinal))
            Case "Floor panel", "Floor panel with energy meter" : renderer.ElectricalPanel("Floor", asset.Model.EndsWith("with energy meter", StringComparison.Ordinal))
            Case "Switchboard lineup", "Switchboard lineup with energy meters" : renderer.ElectricalPanel("Lineup", asset.Model.EndsWith("with energy meters", StringComparison.Ordinal))
            Case "ACB panel with energy meter", "ATS panel with energy meter", "MTS panel with energy meter", "Load panel with energy meter"
                renderer.PowerPanel(asset.Model.Split(" "c)(0), True)
            Case "RMU switchgear", "Eaton RMU switchgear", "SF6 circuit breaker", "HV voltage regulator", "Eaton HV voltage regulator",
                 "HV utility pole", "HV lattice transmission tower", "HV pole fuse cutouts", "HV insulator assembly"
                renderer.HighVoltage(asset.Model)
            Case "Safety bollard"
                renderer.Cylinder(0, 0, 0, 3.5F, 1, Dark)
                renderer.Cylinder(0, 0, 1, 1.8F, 10, renderer.Body(Yellow))
                renderer.Cylinder(0, 0, 5, 1.85F, 2, Dark)
                renderer.Cylinder(0, 0, 10, 2, 0.6F, renderer.Body(Yellow))
            Case "ATS cabinet", "Motor control center", "PLC cabinet", "Variable frequency drive", "HMI console", "Energy meter"
                renderer.ControlCabinet(asset.Model)
            Case "Electric motor" : renderer.Motor(0, 0)
            Case "Centrifugal pump" : renderer.Pump(0)
            Case "Twin pump skid"
                renderer.Box(-19, -10, 0, 38, 20, 1, Dark)
                renderer.Pump(-6)
                renderer.Pump(6)
                renderer.Line(-16, -8, 7, -16, 8, 7, Color.SteelBlue)
            Case "Vertical storage tank" : renderer.StorageTank()
            Case "Horizontal air receiver"
                renderer.HorizontalTank(-15, 0, 8, 30, 6, renderer.Body(Color.SteelBlue))
                renderer.Box(-10, -3, 0, 3, 6, 3, Steel)
                renderer.Box(7, -3, 0, 3, 6, 3, Steel)
                renderer.Cylinder(0, 0, 14, 1, 4, Steel)
                renderer.Cylinder(0, 0, 18, 2, 1, Color.WhiteSmoke)
            Case "Cooling tower" : renderer.CoolingTower()
            Case "Ventilation fan" : renderer.VentilationFan()
            Case "Pressure transmitter", "Temperature sensor", "Stack beacon" : renderer.Sensor(asset.Model)
            Case "Pipe section", "Isolation valve", "Cable tray" : renderer.Distribution(asset.Model)
            Case "Fire extinguisher", "Fire hydrant" : renderer.FireDevice(asset.Model)
            Case "Dome camera"
                renderer.Cylinder(0, 0, 14, 6, 2, renderer.Body(Steel))
                renderer.Cylinder(0, 0, 9, 4.5F, 5, Dark)
                renderer.Cylinder(0, 0, 8, 2.5F, 2, Color.FromArgb(78, 111, 130))
            Case "Camera"
                renderer.Box(-1, -1, 0, 2, 2, 14, renderer.Body(Steel))
                renderer.Box(-5, -3, 14, 10, 6, 5, renderer.Body(Steel))
                renderer.Box(4, -2, 15, 2, 4, 3, Dark)
            Case "Access reader"
                renderer.Box(-2, -2, 0, 4, 4, 16, renderer.Body(Dark))
                renderer.Box(-1, 2, 10, 2, 0.4F, 4, Color.LightSkyBlue)
            Case Else
                renderer.Box(-7, -4, 0, 14, 8, 18, renderer.Body(If(asset.Model = "Fire panel", Color.Firebrick, Steel)))
                renderer.Vents(-5, 4.1F, 3, 10, 4)
                renderer.Box(4, 4.2F, 8, 0.8F, 0.5F, 4, Dark)
                renderer.Box(-4, 4.2F, 11, 6, 0.4F, 4, Dark)
                If asset.Model = "Fire panel" Then
                    renderer.Box(-3, 4.8F, 12, 4, 0.2F, 2, Color.FromArgb(153, 175, 181))
                End If
        End Select
        Return renderer
    End Function

    Private Sub ServicePanels(x As Single)
        Component(x, -13, 0.85F, Sub(renderer) renderer.ControlCabinet("HMI console"))
        Component(x, 10, 0.85F, Sub(renderer) renderer.PowerPanel("ACB", False))
        Line(x - 5, -12, 1, x - 5, 12, 1, Dark)
    End Sub

    Private Sub ChillerPlant(model As String)
        If model = "Air-cooled screw chiller" Then
            Chiller(True)
            Component(0, -23, 0.7F, Sub(renderer) renderer.HorizontalTank(-16, 0, 7, 32, 5, renderer.Body(Color.SteelBlue)))
        ElseIf model = "Chiller pump and buffer skid" Then
            Box(-27, -16, 0, 54, 32, 2, Dark)
            Component(-8, -8, 0.75F, Sub(renderer) renderer.Pump(0))
            Component(-8, 8, 0.75F, Sub(renderer) renderer.Pump(0))
            Cylinder(17, 0, 2, 8, 25, Body(Steel))
            Cylinder(17, 0, 27, 4, 2, Steel)
            For Each y In {-10.0F, 10.0F}
                Line(-23, y, 7, 17, y, 7, If(y < 0, Color.RoyalBlue, Color.Firebrick))
                Line(17, y, 7, 17, 0, 7, Steel)
            Next
        Else
            Dim centrifugal = model.StartsWith("Centrifugal", StringComparison.Ordinal)
            Box(-29, -17, 0, 58, 34, 3, Dark)
            For Each y In {-8.0F, 8.0F}
                HorizontalTank(-24, y, 11, 48, 7, Body(Color.FromArgb(120, 166, 165)))
                For Each x In {-25.0F, 24.0F}
                    HorizontalTank(x, y, 11, 1.5F, 8, Steel)
                Next
                If _depthX > 0 Then HorizontalTank(25, y, 11, 7, 3, If(y < 0, Color.RoyalBlue, Color.Firebrick))
            Next
            If centrifugal Then
                HorizontalTank(-10, 0, 25, 20, 7, Body(Steel))
                Cylinder(4, 0, 28, 4, 7, Dark)
                Line(3, 0, 32, 3, 8, 20, Steel)
            Else
                For Each x In {-12.0F, 9.0F}
                    Box(x, -4, 18, 12, 8, 8, Body(Steel))
                    HorizontalTank(x - 2, 0, 23, 4, 4, Dark)
                Next
            End If
            Component(-27, 19, 0.7F, Sub(renderer) renderer.ControlCabinet("HMI console"))
        End If
    End Sub

    Private Sub PowerPanel(kind As String, metered As Boolean)
        Dim width = If(kind = "Load", 18.0F, 15.0F)
        Casing(Sub()
                   Box(-width / 2 - 1, -6, 0, width + 2, 12, 2, Dark)
                   Box(-width / 2, -5, 2, width, 10, 29, Body(Steel))
               End Sub)
        If _depthY <= 0 Then Return
        Dim front = 5.3F
        Line(-width / 2 + 1, front, 3, width / 2 - 1, front, 3, Dark)
        Line(width / 2 - 2, front, 12, width / 2 - 2, front, 17, Dark)
        If kind = "ACB" Then
            Box(-5, front, 10, 10, 0.8F, 8, Dark)
            Box(-3, front + 1, 13, 6, 0.4F, 3, Steel)
            Box(-3, front + 1.5F, 11, 2, 0.3F, 1, Color.Firebrick)
            Box(1, front + 1.5F, 11, 2, 0.3F, 1, Color.SeaGreen)
        ElseIf kind = "ATS" OrElse kind = "MTS" Then
            For Each x In {-4.0F, 4.0F}
                Box(x - 1.5F, front, 12, 3, 0.8F, 5, Dark)
            Next
            Line(-4, front + 1, 19, 4, front + 1, 19, Dark)
            Cylinder(0, front + 1, 13, 1.2F, 3, Dark)
            If kind = "ATS" Then Box(-3, front, 6, 6, 0.8F, 4, Color.LightSkyBlue)
        Else
            For index = 0 To 4
                Box(-5, front, 5 + index * 3, 10, 0.7F, 2, Dark)
                For breaker = 0 To 4
                    Box(-4 + breaker * 1.8F, front + 0.8F, 5.5F + index * 3, 1, 0.3F, 1, Steel)
                Next
            Next
        End If
        If metered Then
            Box(-4, front, 23, 8, 0.8F, 5, Dark)
            Box(-3, front + 1, 25, 6, 0.3F, 2, Color.LightGreen)
        End If
        Vents(-5, front + 0.2F, 3.5F, 10, 2)
        FaceText(kind, -4, front + 1, 30, Dark)
    End Sub

    Private Sub Insulator(x As Single, y As Single, z As Single, height As Single)
        Cylinder(x, y, z, 0.8F, height, Color.FromArgb(124, 81, 53))
        For index = 0 To 5
            Cylinder(x, y, z + index * height / 6, 1.8F, 0.7F, Color.FromArgb(151, 105, 70))
        Next
        Cylinder(x, y, z + height, 1, 1.5F, Steel)
    End Sub

    Private Sub HighVoltage(model As String)
        If model = "HV lattice transmission tower" Then
            For Each side In {-1.0F, 1.0F}
                For Each depthSide As Single In {-1.0F, 1.0F}
                    Line(side * 13, depthSide * 10, 0, side * 4, depthSide * 3, 70, Body(Steel))
                    Box(side * 13 - 2, depthSide * 10 - 2, 0, 4, 4, 2, Dark)
                Next
            Next
            For level = 0 To 6
                Dim z = level * 10.0F
                Dim x = 13 - z * 9 / 70
                Dim y = 10 - z * 7 / 70
                Dim nextX = 13 - (z + 10) * 9 / 70
                Dim nextY = 10 - (z + 10) * 7 / 70
                For Each side In {-1.0F, 1.0F}
                    Line(-x, side * y, z, nextX, side * nextY, z + 10, Body(Steel))
                    Line(x, side * y, z, -nextX, side * nextY, z + 10, Body(Steel))
                    Line(side * x, -y, z, side * nextX, nextY, z + 10, Body(Steel))
                    Line(side * x, y, z, side * nextX, -nextY, z + 10, Body(Steel))
                Next
            Next
            For Each z In {40.0F, 55.0F, 70.0F}
                Box(-25, -2, z, 50, 4, 1.2F, Body(Steel))
                For Each side In {-1.0F, 1.0F}
                    Line(0, 0, z + 8, side * 25, 0, z, Steel)
                    Insulator(side * 23, 0, z - 8, 8)
                    Line(side * 23, -15, z - 8, side * 23, 15, z - 8, Dark)
                Next
            Next
            Return
        End If
        If model.Contains("pole", StringComparison.OrdinalIgnoreCase) OrElse model = "HV insulator assembly" Then
            Dim pole = model = "HV utility pole"
            Dim base = If(pole, 51.0F, 4.0F)
            If pole Then
                Cylinder(0, 0, 0, 2, 62, Body(Color.FromArgb(153, 159, 159)))
                Box(-4, -4, 0, 8, 8, 2, Dark)
                Box(-5, -3, 15, 10, 6, 10, Body(Steel))
            End If
            Box(-19, -1.5F, base, 38, 3, 2, Body(Steel))
            For Each x In {-14.0F, 0.0F, 14.0F}
                Insulator(x, 0, base + 2, 8)
                If model <> "HV insulator assembly" Then
                    Insulator(x, 5, base - 8, 7)
                    Line(x, 0, base + 11, x, 5, base, Steel)
                    Box(x - 0.7F, 5, base - 7, 1.4F, 1.4F, 6, Color.WhiteSmoke)
                    Line(x, 5, base, x + 2, 6, base - 7, Steel)
                End If
                If pole Then Line(x, -17, base + 11, x, 17, base + 11, Dark)
            Next
            Return
        End If
        If model.Contains("regulator", StringComparison.OrdinalIgnoreCase) Then
            Box(-24, -10, 0, 48, 20, 2, Dark)
            For Each x In {-15.0F, 0.0F, 15.0F}
                Cylinder(x, 0, 2, 6, 20, Body(Color.FromArgb(163, 183, 174)))
                Cylinder(x, 0, 22, 6.5F, 1, Steel)
                For Each y In {-3.0F, 3.0F}
                    Insulator(x, y, 23, 7)
                Next
                For index = 0 To 4
                    Box(x - 4 + index * 2, 5, 5, 0.7F, 3, 13, Body(Steel))
                Next
            Next
            Component(0, 16, 0.65F, Sub(renderer) renderer.ControlCabinet("HMI console"))
            FaceText(If(model.StartsWith("Eaton", StringComparison.Ordinal), "EATON AVR", "HV AVR"), -12, 9, 20, Dark)
            Return
        End If
        Dim breaker = model = "SF6 circuit breaker"
        Dim bays = If(breaker, 1, 3)
        Casing(Sub()
                   Box(-bays * 7 - 1, -7, 0, bays * 14 + 2, 14, 2, Dark)
                   For index = 0 To bays - 1
                       Box(-bays * 7 + index * 14, -6, 2, 13.7F, 12, 29, Body(Color.FromArgb(186, 200, 195)))
                   Next
               End Sub)
        For index = 0 To bays - 1
            Dim x = -bays * 7 + index * 14
            If _depthY > 0 Then
                Box(x + 2, 6.2F, 20, 9, 0.6F, 6, Dark)
                Box(x + 3, 6.9F, 22, 6, 0.3F, 2, Color.WhiteSmoke)
                Line(x + 7, 7, 8, x + 7, 7, 18, Dark)
                Line(x + 3, 7, 13, x + 10, 7, 13, Dark)
                Box(x + 5.5F, 7, 11, 3, 0.5F, 3, Dark)
                Cylinder(x + 9, 7, 17, 1, 1, Color.SeaGreen)
            End If
            For Each y In {-3.0F, 3.0F}
                Insulator(x + 7, y, 31, 5)
            Next
        Next
        FaceText(If(model.StartsWith("Eaton", StringComparison.Ordinal), "EATON RMU", If(breaker, "SF6", "RMU")), -bays * 7 + 1, 7, 30, Dark)
    End Sub

    Private Sub RatedGenerator(large As Boolean)
        Dim length = If(large, 44.0F, 32.0F)
        Dim width = If(large, 18.0F, 14.0F)
        Dim height = If(large, 21.0F, 17.0F)
        Casing(Sub()
                   Box(-length / 2 - 1, -width / 2 - 1, 0, length + 2, width + 2, 3, Dark)
                   For Each x In {-length / 3, length / 3}
                       Box(x, -width / 2 - 1, 0, 2, width + 2, 1, Steel)
                   Next
                   Box(-length / 2, -width / 2, 3, length, width, height, Body(Yellow))
               End Sub)
        If _depthX < 0 Then
            Box(-length / 2 - 0.4F, -width / 2 + 1, 4, 0.5F, width - 2, height - 2, Dark)
            For index = 0 To 9
                Line(-length / 2 - 0.6F, -width / 2 + 1 + index * (width - 2) / 9, 5,
                     -length / 2 - 0.6F, -width / 2 + 1 + index * (width - 2) / 9, height + 1, Steel)
            Next
        End If
        For Each side In {-1.0F, 1.0F}
            If side * _depthY <= 0 Then Continue For
            For index = 0 To 3
                Dim x = -length / 2 + 3 + index * (length - 6) / 4
                Line(x, side * (width / 2 + 0.1F), 4, x, side * (width / 2 + 0.1F), height + 2, Dark)
                Box(x + 1, side * (width / 2 + 0.3F), 9, 0.8F, 0.4F, 3, Dark)
            Next
            Vents(-length / 2 + 2, side * (width / 2 + 0.5F), 5, length / 4, 7)
        Next
        If _depthY > 0 Then
            Box(2, width / 2 + 0.3F, 10, 5, 0.6F, 5, Dark)
            Box(3, width / 2 + 1, 12, 3, 0.2F, 2, Color.LightSkyBlue)
        End If
        HorizontalTank(0, 0, height + 5, length / 3, 1.5F, Dark)
        Cylinder(length / 3, 0, height + 4, 1, 6, Dark)
        Cylinder(length / 3, 0, height + 10, 1.5F, 0.6F, Dark)
        For Each x In {-length / 3, length / 3}
            Cylinder(x, -width / 3, height + 3, 0.7F, 1.5F, Steel)
        Next
        FaceText(If(large, "CAT 1500 kVA", "CAT 810 kVA"), -length / 4, width / 2 + 1, height, Dark)
    End Sub

    Private Sub AirPlant(model As String)
        If model = "Compressed air filter bank" Then
            Box(-15, -5, 0, 30, 10, 2, Dark)
            For Each x In {-9.0F, 0.0F, 9.0F}
                Cylinder(x, 0, 4, 2.8F, 17, Body(Color.SteelBlue))
                Cylinder(x, 0, 21, 3.4F, 3, Steel)
                Line(x, 0, 24, x, -4, 24, Steel)
                Cylinder(x, 0, 1, 0.4F, 3, Dark)
            Next
            Line(-12, -4, 24, 12, -4, 24, Steel)
            Return
        End If
        Dim atlas = model = "Atlas Copco compressor 7 bar"
        Casing(Sub()
                   Box(-18, -10, 0, 36, 20, 2, Dark)
                   Box(-17, -9, 2, 34, 18, If(atlas, 23, 18), Body(If(atlas, Color.FromArgb(227, 183, 41), Color.FromArgb(141, 174, 193))))
               End Sub)
        If _depthY > 0 Then
            Box(-16, 9.2F, 4, 20, 0.5F, If(atlas, 19, 14), Dark)
            Vents(-15, 9.8F, 5, 17, If(atlas, 12, 9))
            Box(7, 9.3F, 14, 7, 0.5F, 6, Dark)
            Box(8, 10, 16, 5, 0.2F, 3, Color.LightSkyBlue)
        End If
        If _depthX > 0 Then
            Line(16, 0, 9, 22, 0, 9, Steel)
            Line(16, 4, 16, 22, 4, 16, Steel)
        End If
        If atlas Then
            For Each x In {-11.0F, 4.0F}
                Fan(x, 0, 25.2F, 4.5F)
            Next
            If _depthY > 0 Then
                Line(4, 10, 4, 4, 10, 24, Dark)
                Box(12, 10, 8, 1, 0.6F, 4, Dark)
                Box(9, 10, 20, 1, 0.5F, 1, Color.Firebrick)
            End If
        End If
        FaceText(If(atlas, "ATLAS 7 bar", "AIR DRYER"), -12, 10, 24, Dark)
    End Sub

    Private Sub Hvac(model As String)
        Dim enclosure = Body(Color.FromArgb(218, 227, 228))
        Dim large = model.Contains("20 HP") OrElse model.Contains("10 HP")
        If model.StartsWith("Indoor wall", StringComparison.Ordinal) Then
            Dim width = If(model.Contains("2.5"), 28.0F, 22.0F)
            Casing(Sub() Box(-width / 2, -3, 9, width, 6, 8, enclosure))
            If _depthY <= 0 Then Return
            Box(-width / 2 + 1, 3.2F, 9.5F, width - 2, 0.6F, 2, Dark)
            For index = 0 To 3
                Line(-width / 2 + 1, 4, 9.5F + index * 0.5F, width / 2 - 1, 4, 9.5F + index * 0.5F, Steel)
            Next
            Box(width / 2 - 5, 3.5F, 14, 3, 0.3F, 1, Color.LightGreen)
        ElseIf model.Contains("cassette") Then
            Casing(Sub() Box(-10, -10, 15, 20, 20, 4, enclosure))
            Box(-7, -7, 19.1F, 14, 14, 0.4F, Dark)
            For index = 0 To 4
                Line(-6, -6 + index * 3, 19.6F, 6, -6 + index * 3, 19.6F, Steel)
            Next
            For Each side In {-1.0F, 1.0F}
                Box(-8, side * 9, 19.2F, 16, 1, 0.4F, Dark)
                Box(side * 9, -8, 19.2F, 1, 16, 0.4F, Dark)
            Next
        ElseIf model.Contains("ducted") OrElse model = "Air handling unit" Then
            Dim height = If(model = "Air handling unit", 22.0F, 12.0F)
            Casing(Sub() Box(-22, -10, 2, 44, 20, height, enclosure))
            If _depthY > 0 Then
                For Each x In {-15.0F, 0.0F, 15.0F}
                    Line(x, 10.2F, 3, x, 10.2F, height + 1, Dark)
                Next
                Vents(-20, 10.4F, 5, 10, 7)
            End If
            If _depthX < 0 Then Box(-24, -6, 4, 2, 12, 9, Dark)
            If _depthX > 0 Then Box(22, -6, 4, 2, 12, 9, Dark)
        ElseIf model.Contains("Indoor floor") Then
            Casing(Sub() Box(-7, -5, 0, 14, 10, 35, enclosure))
            If _depthY <= 0 Then Return
            Vents(-6, 5.3F, 18, 12, 12)
            Box(-3, 5.4F, 11, 6, 0.4F, 4, Dark)
        Else
            Dim vrv = model = "VRV outdoor system"
            Dim width = If(vrv, 32.0F, If(model.Contains("20 HP"), 34.0F, If(model.Contains("10 HP"), 28.0F,
                If(model.Contains("5 HP") AndAlso Not model.Contains("1.5") AndAlso Not model.Contains("2.5"), 26.0F, If(model.Contains("2.5"), 22.0F, 18.0F)))))
            Dim height = If(vrv, 32.0F, If(model.Contains("20 HP"), 34.0F, If(large, 28.0F, If(model.Contains("2.5"), 16.0F, 15.0F))))
            Casing(Sub()
                       Box(-width / 2 - 1, -6, 0, width + 2, 12, 2, Dark)
                       Box(-width / 2, -5, 2, width, 10, height, enclosure)
                   End Sub)
            If vrv Then
                For Each x In {-8.0F, 8.0F}
                    Fan(x, 0, height + 2, 6)
                Next
                If _depthY > 0 Then Vents(-width / 2 + 1, 5.3F, 5, width - 2, 18)
            ElseIf _depthY > 0 Then
                For index = 0 To If(large, 1, 0)
                    Dim z = 9 + index * 12
                    FrontFan(-4, 5.5F, z, 5)
                Next
                Vents(3, 5.3F, 4, width / 2 - 4, If(large, 15, 7))
            End If
            If _depthX > 0 Then
                Line(width / 2, 1, 5, width / 2 + 5, 1, 5, Color.Sienna)
                Line(width / 2, 3, 7, width / 2 + 5, 3, 7, Color.Sienna)
            End If
        End If
    End Sub

    Private Sub ProcessTower(compressor As Boolean)
        Dim cells = If(compressor, 1, 2)
        Dim width = cells * 24.0F
        Casing(Sub()
                   Box(-width / 2 - 2, -13, 0, width + 4, 26, 3, Dark)
                   Box(-width / 2, -11, 3, width, 22, 22, Body(If(compressor, Color.FromArgb(151, 185, 184), Steel)))
               End Sub)
        If _depthY > 0 Then Vents(-width / 2 + 1, 11.3F, 5, width - 2, 12)
        For index = 0 To cells - 1
            Dim x = -width / 2 + 12 + index * 24
            Cylinder(x, 0, 25, 9, 3, Body(Steel))
            Fan(x, 0, 28, 8)
        Next
        If _depthX > 0 Then
            Line(width / 2, 5, 8, width / 2 + 8, 5, 8, Color.RoyalBlue)
            Line(width / 2, -5, 18, width / 2 + 8, -5, 18, Color.Firebrick)
        ElseIf _depthX < 0 Then
            Box(-width / 2 - 3, -12, 0, 2, 2, 32, Steel)
            Box(-width / 2 - 3, -6, 0, 2, 2, 32, Steel)
            For z As Single = 3 To 30 Step 3
                Line(-width / 2 - 3.2F, -12, z, -width / 2 - 3.2F, -4, z, Steel)
            Next
        End If
    End Sub

    Private Sub Component(x As Single, y As Single, scale As Single, build As Action(Of FactoryMapEquipmentRenderer))
        Dim observe As Action(Of Single, Single, Single) = Nothing
        If _measure IsNot Nothing Then observe = Sub(px, py, pz) _measure(x + px * scale, y + py * scale, pz * scale)
        Dim child As New FactoryMapEquipmentRenderer(_graphics, Function(px, py, pz) _project(x + px * scale, y + py * scale, pz * scale),
                                                      _hitArea, If(_bodyColor.HasValue, ColorTranslator.ToHtml(_bodyColor.Value), ""), observe, _geometryOnly, _context, _reflected)
        build(child)
        For Each drawCommand In child._commands
            _commands.Add((Depth(x, y, 0) + drawCommand.Depth * scale, drawCommand.Paint))
        Next
    End Sub

    Private Sub CompleteLine(model As String, length As Single)
        Dim run = length
        Dim scale = Math.Min(1.0F, run / 140.0F)
        Conveyor(-run / 2, run)
        Component(-run * 0.3F, 0, 0.65F * scale, Sub(renderer) renderer.FillerSystem(If(model = "PET bottling line", "PET bottle blower", "Bottle rinser"), 60))
        Component(0, 0, 0.85F * scale, Sub(renderer) renderer.FillerSystem("Rotary bottle filler", 60))
        Component(run * 0.24F, 0, 0.6F * scale,
                  Sub(renderer)
                      If model = "Can filling line" Then
                          renderer.FillerAccessory("Can seamer")
                      Else
                          renderer.FillerSystem("Bottle labeler", 60)
                      End If
                  End Sub)
        Component(run * 0.42F, 0, 0.6F * scale, Sub(renderer) renderer.FactoryUtility("Case packer"))
        Component(-run / 2, 14 * scale, scale, Sub(renderer) renderer.ControlCabinet("HMI console"))
        If model = "Can filling line" Then
            For index = 0 To 7
                Cylinder(-run / 2 + (12 + index * 6) * scale, 0, 8, 1.6F * scale, 3 * scale, Color.Silver)
            Next
        End If
    End Sub

    Private Sub FactoryUtility(model As String)
        Dim stainless = Body(Steel)
        Select Case model
            Case "Office desk"
                For Each x In {-14.0F, 12.0F}
                    For Each y In {-7.0F, 5.0F}
                        Box(x, y, 0, 2, 2, 16, Dark)
                    Next
                Next
                Box(-15, -8, 16, 30, 16, 1, Body(Color.BurlyWood))
                Box(-4, 1, 17, 8, 3, 0.6F, Dark)
                Box(-0.7F, -4, 17, 1.4F, 1, 4, Dark)
                Box(-6, -5, 20, 12, 1, 8, Dark)
                Box(-5, -5.2F, 21, 10, 0.2F, 6, Color.LightSkyBlue)
            Case "Office chair"
                Cylinder(0, 0, 0, 1.2F, 9, Dark)
                For Each angle In {0, 72, 144, 216, 288}
                    Dim x = CSng(Math.Cos(angle * Math.PI / 180) * 6)
                    Dim y = CSng(Math.Sin(angle * Math.PI / 180) * 6)
                    Line(0, 0, 2, x, y, 1, Dark)
                    Cylinder(x, y, 0, 0.8F, 1.5F, Dark)
                Next
                Box(-6, -6, 9, 12, 12, 2, Body(Color.SlateGray))
                Box(-6, -6, 10, 12, 2, 13, Body(Color.SlateGray))
            Case "Office cabinet"
                Box(-9, -4.5F, 0, 18, 9, 36, Body(Color.LightSlateGray))
                Line(0, 4.7F, 1, 0, 4.7F, 35, Dark)
                Box(-2, 4.6F, 16, 0.6F, 0.5F, 4, Dark)
                Box(1.4F, 4.6F, 16, 0.6F, 0.5F, 4, Dark)
            Case "Syrup mixing skid"
                WaterSystem("Stainless process vat")
                Component(22, 0, 0.5F, Sub(renderer) renderer.Pump(0))
                Line(0, 12, 8, 24, 12, 8, Steel)
                Box(-15, 8, 0, 8, 5, 20, stainless)
                Box(-14, 13.3F, 13, 5, 0.3F, 4, Color.LightSkyBlue)
            Case "Beverage carbonator"
                Box(-23, -12, 0, 46, 24, 2, Dark)
                For Each x In {-12.0F, 10.0F}
                    Cylinder(x, 0, 2, 8, 25, stainless)
                    Cylinder(x, 0, 27, 4, 2, Steel)
                Next
                Line(-12, -9, 12, 10, -9, 12, Steel)
                Line(-12, -9, 12, -12, 0, 12, Steel)
                Box(14, 7, 2, 7, 5, 18, stainless)
            Case "Plate pasteurizer"
                Box(-20, -9, 0, 40, 18, 2, Dark)
                For index = 0 To 14
                    Box(-15 + index * 1.6F, -7, 4, 0.9F, 14, 19, stainless)
                Next
                Box(-18, -8, 2, 3, 16, 24, Color.SteelBlue)
                Box(10, -8, 2, 3, 16, 24, Color.SteelBlue)
                For Each y In {-5.0F, 5.0F}
                    Line(-22, y, 7, -15, y, 7, Color.Firebrick)
                    Line(13, y, 19, 20, y, 19, Color.RoyalBlue)
                Next
            Case "Preform hopper"
                Box(-10, -9, 0, 2, 2, 17, Steel)
                Box(8, 7, 0, 2, 2, 17, Steel)
                Box(-11, -10, 15, 22, 20, 12, stainless)
                Box(-9, -8, 27, 18, 16, 1, Dark)
                For index = 0 To 6
                    Cylinder(-7 + index * 2, 0, 28, 0.8F, 2, Color.LightSkyBlue)
                Next
                Box(9, -3, 8, 18, 6, 3, stainless)
            Case "Bottle inspection station", "Date coding station"
                Conveyor(-22, 44)
                For Each x In {-7.0F, 7.0F}
                    Box(x, -6, 0, 1, 1, 24, Steel)
                Next
                Box(-7, -6, 23, 15, 12, 2, stainless)
                Box(-5, -5, 12, 4, 3, 6, Dark)
                Box(-4, -1.7F, 13, 2, 0.4F, 3, Color.LightSkyBlue)
                Box(8, 7, 0, 6, 4, 18, stainless)
                If model = "Bottle inspection station" Then Box(3, -4, 10, 1, 8, 11, Color.LightSkyBlue)
            Case "Case packer"
                Conveyor(-30, 60)
                For Each x In {-18.0F, 16.0F}
                    For Each y In {-10.0F, 8.0F}
                        Box(x, y, 0, 2, 2, 27, stainless)
                    Next
                Next
                Box(-18, -10, 26, 36, 20, 2, stainless)
                Box(-9, -5, 8, 18, 10, 8, Color.Tan)
                Box(-4, -2, 18, 8, 4, 6, Dark)
                Line(0, 0, 24, 0, 0, 27, Steel)
            Case "Palletizer"
                Box(-16, -14, 0, 32, 28, 2, Dark)
                Cylinder(0, 0, 2, 7, 6, Body(Color.Orange))
                Box(-4, -4, 8, 8, 8, 18, Body(Color.Orange))
                Box(-2, -3, 24, 24, 6, 5, Body(Color.Orange))
                Cylinder(20, 0, 20, 3, 7, Dark)
                Box(16, -5, 17, 8, 10, 2, Steel)
                Box(16, -5, 13, 1, 10, 5, Steel)
                Component(27, 0, 0.7F, Sub(renderer) renderer.FactoryUtility("Pallet stack"))
            Case "Pallet stretch wrapper"
                Cylinder(0, 0, 0, 17, 2, Dark)
                Component(0, 0, 0.7F, Sub(renderer) renderer.FactoryUtility("Pallet stack"))
                Box(19, -4, 0, 6, 8, 35, Body(Color.SteelBlue))
                Box(15, -3, 13, 4, 6, 12, Steel)
                Cylinder(16, 0, 14, 2, 10, Color.WhiteSmoke)
            Case "Steam boiler"
                Box(-21, -11, 0, 42, 22, 3, Dark)
                HorizontalTank(-17, 0, 13, 34, 9, stainless)
                HorizontalTank(-22, 0, 13, 5, 5, Body(Color.Firebrick))
                Cylinder(11, 0, 20, 2, 18, Dark)
                Box(8, 9, 2, 10, 5, 18, stainless)
                Box(10, 14.3F, 13, 5, 0.3F, 4, Color.LightSkyBlue)
            Case "Pallet rack"
                For Each x In {-20.0F, 20.0F}
                    For Each y In {-8.0F, 8.0F}
                        Box(x, y, 0, 1.4F, 1.4F, 36, Body(Color.RoyalBlue))
                    Next
                Next
                For z As Single = 2 To 34 Step 11
                    Box(-20, -8, z, 42, 18, 1, Color.Orange)
                    For Each x In {-14.0F, 1.0F}
                        Box(x, -6, z + 1, 12, 14, 8, Color.Tan)
                    Next
                Next
            Case "Pallet stack"
                For z As Single = 0 To 5 Step 2.5F
                    For index = 0 To 4
                        Box(-12, -9 + index * 4, z, 24, 2.6F, 1.2F, Color.FromArgb(153, 113, 70))
                    Next
                Next
                For level = 0 To 2
                    For Each x In {-11.0F, 1.0F}
                        Box(x, -8, 7 + level * 7, 10, 16, 6.5F, Body(Color.Tan))
                    Next
                Next
            Case "Loading dock"
                Box(-25, -20, 0, 50, 40, 6, Body(Color.Gray))
                Box(-18, -20, 6, 36, 22, 1, Steel)
                For Each x In {-22.0F, 20.0F}
                    Box(x, -22, 1, 2, 3, 4, Dark)
                Next
                For index = 0 To 3
                    Box(-12, 3 + index * 4, 0, 24, 4, 5 - index, Steel)
                Next
            Case "Forklift"
                Box(-11, -7, 4, 22, 14, 7, Body(Yellow))
                For Each x In {-8.0F, 8.0F}
                    For Each y In {-8.0F, 8.0F}
                        Cylinder(x, y, 0, 3, 4, Dark)
                    Next
                Next
                Box(-2, -3, 11, 6, 6, 3, Dark)
                For Each x In {-7.0F, 7.0F}
                    For Each y In {-6.0F, 6.0F}
                        Box(x, y, 11, 1, 1, 13, Steel)
                    Next
                Next
                Box(-8, -7, 24, 17, 14, 1, Dark)
                For Each y In {-4.0F, 4.0F}
                    Box(12, y, 0, 2, 1, 26, Dark)
                    Box(13, y, 1, 14, 1.5F, 1.2F, Steel)
                Next
        End Select
    End Sub

    Private Sub WaterSystem(model As String)
        Dim stainless = Body(Color.FromArgb(188, 201, 202))
        Select Case model
            Case "Water treatment control panel"
                ControlCabinet("PLC cabinet")
                Box(-4, 5.9F, 13, 8, 0.4F, 6, Color.SteelBlue)
                FaceText("WATER", -4, 6.4F, 21, Dark)
            Case "Stainless water tank", "Stainless process vat"
                For Each x In {-7.0F, 7.0F}
                    For Each y In {-7.0F, 7.0F}
                        Box(x - 1, y - 1, 0, 2, 2, 7, Steel)
                    Next
                Next
                Cylinder(0, 0, 6, 12, If(model = "Stainless process vat", 16, 30), stainless)
                Cylinder(0, 0, If(model = "Stainless process vat", 22, 36), 12, 1, Color.Silver)
                Cylinder(0, 0, If(model = "Stainless process vat", 23, 37), 3, 1.5F, Steel)
                Line(0, 0, 7, 0, 16, 7, Color.SteelBlue)
                If model = "Stainless process vat" Then
                    Box(-2, -2, 24, 4, 4, 4, Dark)
                    Cylinder(0, 0, 14, 0.6F, 14, Dark)
                End If
            Case "Sand filter vessel", "Activated carbon filter", "Water softener skid"
                Dim count = If(model = "Water softener skid", 2, 1)
                Box(-count * 9, -9, 0, count * 18, 18, 2, Dark)
                For index = 0 To count - 1
                    Dim x = (index - (count - 1) / 2.0F) * 18
                    Cylinder(x, 0, 2, 7, 27, Body(If(model = "Activated carbon filter", Color.DarkSlateGray, Color.SteelBlue)))
                    Cylinder(x, 0, 29, 5, 2, Steel)
                    Box(x - 3, -3, 31, 6, 6, 3, Dark)
                    Line(x, -8, 7, x, -8, 31, Color.SteelBlue)
                    Line(x, -8, 31, x, 0, 31, Color.SteelBlue)
                Next
            Case "Chemical dosing skid"
                Box(-18, -9, 0, 36, 18, 2, Steel)
                For Each x In {-10.0F, 10.0F}
                    Cylinder(x, 0, 2, 6, 14, Body(Color.Beige))
                    Cylinder(x, 0, 16, 2, 2, Dark)
                    Box(x - 2, -2, 18, 4, 4, 4, Color.SteelBlue)
                    Line(x, 0, 21, x, -10, 21, Color.SteelBlue)
                Next
            Case "Water booster pump skid"
                Box(-22, -15, 0, 44, 30, 2, Dark)
                Pump(-8)
                Pump(8)
                Line(-19, -12, 8, -19, 12, 8, Color.SteelBlue)
                Box(12, 10, 2, 8, 5, 18, stainless)
            Case "CIP cleaning skid"
                Box(-30, -12, 0, 60, 24, 2, Dark)
                For Each x In {-20.0F, 0.0F, 20.0F}
                    Cylinder(x, 0, 2, 8, 23, stainless)
                    Cylinder(x, 0, 25, 3, 2, Steel)
                    Line(x, -9, 5, x, -9, 26, Color.SteelBlue)
                Next
                Line(-25, -9, 5, 25, -9, 5, Color.SteelBlue)
            Case "UV sterilizer skid"
                Box(-22, -9, 0, 44, 18, 2, Dark)
                HorizontalTank(-18, 0, 10, 36, 4, stainless)
                Box(-15, -3, 2, 2, 6, 6, Steel)
                Box(13, -3, 2, 2, 6, 6, Steel)
                Box(6, 5, 2, 12, 4, 13, Body(Steel))
                Box(8, 9.2F, 10, 6, 0.3F, 3, Color.LightSkyBlue)
            Case Else
                Dim twin = model = "Ultrafiltration twin rack"
                Dim width = If(twin, 56.0F, 38.0F)
                Box(-width / 2, -10, 0, width, 20, 2, Dark)
                For Each x In {-width / 2 + 1, width / 2 - 2}
                    Box(x, -9, 2, 2, 2, 34, Steel)
                    Box(x, 7, 2, 2, 2, 34, Steel)
                Next
                Box(-width / 2, -9, 34, width, 2, 2, Steel)
                If model = "Reverse osmosis skid" Then
                    For level = 0 To 3
                        HorizontalTank(-16, 0, 9 + level * 6, 32, 2.2F, Body(Color.WhiteSmoke))
                    Next
                Else
                    Dim count = If(twin, 8, 5)
                    For index = 0 To count - 1
                        Dim x = -width / 2 + 5 + index * (width - 10) / (count - 1)
                        Cylinder(x, 0, 5, 2.4F, 26, Body(Color.WhiteSmoke))
                        Cylinder(x, 0, 31, 3, 1, Color.SteelBlue)
                        Line(x, 0, 5, x, -6, 5, Color.SteelBlue)
                    Next
                End If
                Line(-width / 2 + 3, -6, 5, width / 2 - 3, -6, 5, Color.SteelBlue)
                Line(-width / 2 + 3, -6, 32, width / 2 - 3, -6, 32, Color.SteelBlue)
                Box(width / 2 - 9, 7, 7, 8, 4, 18, stainless)
                Box(width / 2 - 8, 11.2F, 18, 5, 0.3F, 4, Color.LightSkyBlue)
                HorizontalTank(-12, -6, 4, 12, 2, Color.SteelBlue)
        End Select
    End Sub

    Private Sub FillerSystem(model As String, length As Single)
        Dim stainless = Body(Steel)
        Select Case model
            Case "Filler control panel"
                ControlCabinet("HMI console")
                FaceText("FILL", -4, 6, 18, Dark)
            Case "Air conveyor"
                ProductionLine(length, False)
                Box(-length / 2, -3, 14, length, 6, 3, stainless)
                For Each x In {-length / 3, length / 3}
                    Box(x, -5, 0, 1.5F, 1.5F, 17, Steel)
                    Box(x, 4, 0, 1.5F, 1.5F, 17, Steel)
                Next
            Case "Conveyor corner"
                ProductionLine(length, False)
                Box(length / 2 - 8, 0, 6, 8, length / 2, 2, Dark)
                Line(length / 2 - 9, 0, 10, length / 2 - 9, length / 2, 10, Steel)
                Line(length / 2 + 1, 0, 10, length / 2 + 1, length / 2, 10, Steel)
                Box(length / 2 - 7, length / 2 - 3, 0, 2, 2, 6, Steel)
            Case "Bottle accumulation table"
                Box(-15, -12, 0, 2, 2, 8, Steel)
                Box(13, 10, 0, 2, 2, 8, Steel)
                Cylinder(0, 0, 8, 18, 2, stainless)
                For index = 0 To 11
                    Dim angle = index * Math.PI / 6
                    Cylinder(CSng(Math.Cos(angle) * 12), CSng(Math.Sin(angle) * 12), 10, 1.2F, 4, Color.LightSkyBlue)
                Next
            Case "Rotary bottle filler", "Bottle rinser", "Bottle capper"
                Box(-17, -14, 0, 34, 28, 4, Dark)
                Cylinder(0, 0, 4, 13, 5, stainless)
                Cylinder(0, 0, 9, 2, 20, Steel)
                Cylinder(0, 0, 25, 14, 3, stainless)
                For index = 0 To 11
                    Dim angle = index * Math.PI / 6
                    Dim x = CSng(Math.Cos(angle) * 10)
                    Dim y = CSng(Math.Sin(angle) * 10)
                    Cylinder(x, y, 10, 1.1F, 6, Color.LightSkyBlue)
                    Cylinder(x, y, 21, 0.5F, 4, If(model = "Bottle capper", Color.Orange, Steel))
                Next
                ProductionLine(60, False)
            Case Else
                Dim blower = model = "PET bottle blower"
                Dim wrapper = model = "Shrink wrapper"
                Box(-24, -12, 0, 48, 24, 4, Dark)
                Box(-23, -11, 4, 46, 22, 5, stainless)
                For Each x In {-23.0F, 21.0F}
                    For Each y In {-11.0F, 9.0F}
                        Box(x, y, 9, 2, 2, 22, Steel)
                    Next
                Next
                Box(-23, -11, 30, 46, 22, 2, stainless)
                If wrapper Then
                    Box(-17, -10, 9, 34, 20, 20, Body(Color.FromArgb(101, 144, 164)))
                    Box(-18, -5, 10, 1, 10, 10, Dark)
                    Box(17, -5, 10, 1, 10, 10, Dark)
                ElseIf blower Then
                    For Each x In {-15.0F, -5.0F, 5.0F, 15.0F}
                        Box(x - 3, -5, 10, 6, 10, 14, Body(Color.SlateGray))
                        Cylinder(x, 0, 24, 1, 5, Steel)
                    Next
                    Box(-25, 8, 13, 8, 4, 9, stainless)
                    Box(-24, 12.2F, 17, 5, 0.2F, 4, Color.LightSkyBlue)
                Else
                    For Each x In {-12.0F, 12.0F}
                        Cylinder(x, 0, 9, 5, 17, Body(Color.WhiteSmoke))
                        Cylinder(x, 0, 26, 6, 1, Steel)
                    Next
                    Cylinder(0, 0, 9, 5, 8, Steel)
                End If
                ProductionLine(70, False)
        End Select
    End Sub

    Private Sub IntegratedFillingBlock(model As String)
        _refinedFinish = True
        Dim cans = model.StartsWith("Can ", StringComparison.Ordinal)
        Dim stainless = Body(Color.FromArgb(225, 229, 232))
        Dim glass = Color.FromArgb(88, 110, 124)
        If Not cans Then
            Component(-35, 0, 0.8F, Sub(r) r.SidelBlowerAssembly(False))
            Beam(-10, 0, 7, 7, 0, 7, 0.4F, Steel)
            Cylinder(-4, 0, 6.4F, 3.2F, 0.5F, Steel)
            For Each y In {-2.0F, 2.0F}
                Beam(-10, y, 8.3F, 7, y, 8.3F, 0.15F, Steel)
            Next
        Else
            Box(-25, -2.5F, 6, 25, 5, 0.6F, glass)
            For Each x In {-23.0F, -13.0F}
                Box(x, -2, 0, 0.5F, 4, 6, Steel)
            Next
        End If
        Cylinder(13, 0, 0.4F, 10, 1.6F, Dark)
        Cylinder(13, 0, 2, 9.2F, 4, Steel)
        Cylinder(13, 0, 6, 8.7F, 0.6F, stainless)
        For index = 0 To 15
            Dim angle = index * Math.PI / 8
            Dim x = 13 + CSng(Math.Cos(angle) * 7.3F), y = CSng(Math.Sin(angle) * 7.3F)
            Cylinder(x, y, 11, 0.25F, 2.5F, Steel)
            Beam(x, y, 9, x, y, 11, 0.15F, Steel)
            If cans Then
                Cylinder(x, y, 6.6F, 0.5F, 2.4F, Color.LightGray)
            Else
                Bottle(x, y, 6.6F)
            End If
        Next
        Cylinder(13, 0, 13.5F, 9.2F, 1, stainless)
        Cylinder(13, 0, 14.5F, 2.8F, 3, stainless)
        For Each x In {3.0F, 23.0F}
            For Each y In {-9.5F, 9.0F}
                Box(x, y, 0, 0.4F, 0.4F, 15, Steel)
            Next
        Next
        For Each y In {-9.5F, 9.0F}
            For Each x In {3.5F, 10.0F, 16.5F}
                Box(x, y, 2, 6, 0.12F, 11, glass)
                Box(x + 5.4F, y + 0.12F, 7, 0.15F, 0.2F, 0.8F, Steel)
            Next
        Next
        BeveledBox(2.5F, -10, 15, 21, 20, 0.6F, 0.3F, stainless)
        Cylinder(31, 0, 1, 5.5F, 1, Dark)
        Cylinder(31, 0, 2, 5, 4.5F, Steel)
        Cylinder(31, 0, 12, 5.2F, 1, stainless)
        Cylinder(31, 0, 13, 2, 3, Dark)
        For index = 0 To 7
            Dim angle = index * Math.PI / 4
            Dim x = 31 + CSng(Math.Cos(angle) * 3.6F), y = CSng(Math.Sin(angle) * 3.6F)
            Cylinder(x, y, 9, 0.4F, 3, Steel)
        Next
        Box(22, -2, 6, 20, 4, 0.5F, Steel)
        For Each y In {-2.1F, 2.1F}
            Beam(22, y, 8.5F, 42, y, 8.5F, 0.15F, Steel)
        Next
        Beam(31, -7, 15, 31, 0, 12, 0.45F, Steel)
        Box(29, -8, 12.8F, 4, 2, 2, stainless)
        If model <> "PET Combi - still water" Then
            Cylinder(15, -13, 1, 3.5F, 9, stainless)
            Beam(15, -13, 10, 13, 0, 16, 0.3F, Steel)
        End If
    End Sub

    Private Sub ProductionFeed(model As String)
        _refinedFinish = True
        Dim shell = Body(Steel)
        Select Case model
            Case "Empty-can inverter and rinser"
                BeveledBox(-12, -5, 0, 24, 10, 3, 0.3F, shell)
                For Each x In {-11.0F, 10.0F}
                    Box(x, -4, 3, 0.5F, 8, 13, Steel)
                Next
                Box(-12, -4, 8, 24, 8, 0.6F, Dark)
                For index = 0 To 7
                    Dim x = -10 + index * 2.8F
                    Cylinder(x, 0, 9, 0.8F, 2.4F, Color.LightGray)
                    Cylinder(x, 0, 11.4F, 0.7F, 0.15F, Steel)
                    Cylinder(x, 0, 6, 0.12F, 2, Steel)
                Next
                For Each y In {-3.0F, 3.0F}
                    Beam(-12, y, 12, 12, y, 12, 0.15F, Steel)
                    HorizontalTank(-10, y, 5, 20, 0.35F, Steel)
                Next
                Box(-10, -4.5F, 16, 20, 9, 0.4F, shell)
            Case "Preform cage and tipper"
                Box(-10, -8, 0, 20, 16, 1, Dark)
                For Each x In {-10.0F, 9.0F}
                    For Each y In {-8.0F, 7.0F}
                        Box(x, y, 1, 0.4F, 0.4F, 17, Steel)
                    Next
                Next
                For Each z In {2.0F, 9.0F, 17.0F}
                    For Each y In {-8.0F, 7.5F}
                        Beam(-10, y, z, 9.5F, y, z, 0.25F, Steel)
                    Next
                Next
                For index = 0 To 9
                    For Each y In {-8.0F, 7.5F}
                        Beam(-9 + index * 2, y, 2, -9 + index * 2, y, 17, 0.09F, Steel)
                    Next
                Next
                BeveledBox(-8, -6, 2, 16, 12, 12, 0.5F, Color.FromArgb(193, 178, 146))
                Box(10, -7, 0, 1, 1, 21, Steel)
                Box(10, 6, 0, 1, 1, 21, Steel)
                WallCylinder(10, -8, 11, 1, 2, Dark)
                Beam(10, -6, 3, 16, -6, 18, 0.6F, Steel)
                Face({V(8, -6, 14), V(16, -6, 18), V(16, 6, 18), V(8, 6, 14)}, shell)
            Case "Preform roller sorter and bridge"
                For Each x In {-12.0F, 10.0F}
                    Box(x, -4, 0, 0.5F, 8, 16, Steel)
                Next
                For Each y In {-2.0F, 2.0F}
                    HorizontalTank(-14, y, 16, 28, 0.65F, Steel)
                    Beam(-14, y * 1.6F, 18, 14, y * 1.6F, 18, 0.2F, Steel)
                Next
                For index = 0 To 10
                    Cylinder(-12 + index * 2.4F, 0, 15, 0.4F, 2.5F, Color.LightSkyBlue)
                Next
                Box(12, -1, 14, 3, 2, 2, Dark)
            Case "Can end magazine"
                BeveledBox(-5, -4, 0, 10, 8, 14, 0.25F, shell)
                For Each x In {-3.0F, 3.0F}
                    Cylinder(x, 0, 14, 1.8F, 10, Steel)
                    For index = 0 To 15
                        Cylinder(x, 0, 14.2F + index * 0.6F, 1.9F, 0.12F, Color.LightGray)
                    Next
                Next
                Beam(4, 0, 15, 12, 0, 11, 0.5F, Steel)
        End Select
    End Sub

    Private Sub SidelBlowerAssembly(Optional includeStandaloneFeed As Boolean = True)
        _refinedFinish = True
        Dim shell = Body(Color.FromArgb(225, 229, 232))
        Dim glazing = Color.FromArgb(80, 99, 112)
        Dim trim = Color.FromArgb(94, 118, 143)
        Dim frame = Color.FromArgb(188, 199, 207)
        Dim belt = Color.FromArgb(42, 49, 55)

        ' The reference has an octagonal rotary enclosure and a separate rectangular machine head.
        Cylinder(-24, 0, 0.5F, 3.4F, 0.7F, Dark)
        Cylinder(-24, 0, 1.2F, 2.5F, 5.8F, frame)
        Cylinder(-24, 0, 6.5F, 4.9F, 0.5F, trim, 8)
        Cylinder(-24, 0, 12.2F, 5.6F, 0.5F, trim, 8)
        Cylinder(-24, 0, 12.7F, 5.4F, 0.25F, shell, 8)
        For index = 0 To 7
            Dim angle = index * Math.PI / 4
            Dim nextAngle = (index + 1) * Math.PI / 4
            Dim x1 = -24 + CSng(Math.Cos(angle) * 5.2F)
            Dim y1 = CSng(Math.Sin(angle) * 5.2F)
            Dim x2 = -24 + CSng(Math.Cos(nextAngle) * 5.2F)
            Dim y2 = CSng(Math.Sin(nextAngle) * 5.2F)
            Box(x1 - 0.2F, y1 - 0.2F, 0.3F, 0.4F, 0.4F, 11.9F, frame)
            Face({V(x1, y1, 7), V(x2, y2, 7), V(x2, y2, 12.2F), V(x1, y1, 12.2F)},
                 If(index Mod 2 = 0, shell, Shade(shell, 0.94F)))
            Dim midPanelX = (x1 + x2) / 2
            Dim midPanelY = (y1 + y2) / 2
            Beam(midPanelX, midPanelY, 7.3F, midPanelX, midPanelY, 11.9F, 0.055F, frame)
            If index Mod 2 = 0 Then
                Face({V(x1, y1, 0.7F), V(x2, y2, 0.7F), V(x2, y2, 5.8F), V(x1, y1, 5.8F)}, shell)
            Else
                Dim midX = (x1 + x2) / 2
                Dim midY = (y1 + y2) / 2
                Box(midX - 0.4F, midY - 0.4F, 1, 0.8F, 0.8F, 4.6F, Dark)
                Cylinder(midX, midY, 2.5F, 0.55F, 2.4F, frame)
            End If
            Beam(x1, y1, 6.4F, x2, y2, 6.4F, 0.24F, frame)
            Beam(-24 + (x1 + 24) * 1.06F, y1 * 1.06F, 7.4F,
                 -24 + (x2 + 24) * 1.06F, y2 * 1.06F, 7.4F, 0.13F, frame)
        Next
        For Each x In {-27.4F, -24.0F}
            For Each y In {-3.4F, 0.0F}
                BeveledBox(x, y, 12.95F, 3, 3, 0.18F, 0.16F, frame)
                BeveledBox(x + 0.25F, y + 0.25F, 13.13F, 2.5F, 2.5F, 0.1F, 0.1F, Color.WhiteSmoke)
                Box(x + 1.1F, y + 1.35F, 13.23F, 0.8F, 0.15F, 0.06F, frame)
            Next
        Next

        Box(-18, -3.8F, 0.7F, 7.5F, 7.6F, 0.6F, Dark)
        For Each x In {-17.8F, -11.4F}
            For Each y In {-3.6F, 2.8F}
                Box(x, y, 0, 0.9F, 0.9F, 6.5F, shell)
                Box(x + 0.25F, y + 0.25F, 1, 0.4F, 0.4F, 4.6F, Dark)
            Next
        Next
        Cylinder(-14.2F, 0, 1.3F, 2.2F, 5, frame)
        Box(-18.3F, -4.1F, 6.5F, 8.1F, 8.2F, 0.5F, trim)
        Box(-17.8F, -3.7F, 7, 7.1F, 7.4F, 4.6F, glazing)
        For Each y In {-3.85F, 3.7F}
            For Each x In {-17.9F, -14.4F, -10.9F}
                Box(x, y, 7, 0.2F, 0.2F, 4.6F, frame)
            Next
            Beam(-17.9F, y, 7.2F, -10.7F, y, 7.2F, 0.2F, frame)
        Next
        BeveledBox(-18.2F, -4, 11.6F, 8, 8, 0.5F, 0.4F, trim)
        BeveledBox(-17.6F, -3.4F, 12.1F, 6.8F, 6.8F, 0.2F, 0.35F, trim)
        BeveledBox(-16.6F, -2.4F, 12.3F, 3.4F, 3.4F, 0.15F, 0.2F, frame)
        For Each y In {-3.92F, 3.92F}
            For Each x In {-16.9F, -13.4F}
                Box(x, y, 8.6F, 0.15F, 0.15F, 0.9F, frame)
            Next
        Next

        BeveledBox(-16, -7.8F, 7.4F, 19, 5.5F, 7.5F, 0.3F, shell)
        For Each x In {-9.8F, -3.6F}
            Box(x, -2.28F, 8.2F, 0.06F, 0.04F, 6.1F, frame)
            Box(x, -7.86F, 8.2F, 0.06F, 0.04F, 6.1F, frame)
        Next
        Box(-15.2F, -2.24F, 8, 17.3F, 0.06F, 0.18F, trim)
        For Each x In {-15.6F, -9.6F, -3.6F}
            Box(x, -7.4F, 14.9F, 5.6F, 4.7F, 0.15F, Color.WhiteSmoke)
            For index = 0 To 3
                Box(x + 0.4F + index * 0.65F, -6.7F, 15.05F, 0.35F, 0.08F, 0.04F, Dark)
            Next
        Next
        For Each y In {-2.2F, 2.5F}
            For Each x In {-9.8F, -6.0F, -2.2F, 1.6F}
                Box(x, y, 0.5F, 0.25F, 0.25F, 9.7F, frame)
            Next
            Beam(-9.8F, y, 1, 2, y, 1, 0.28F, frame)
            Beam(-9.8F, y, 10.2F, 2, y, 10.2F, 0.28F, frame)
            For Each x In {-9.5F, -5.7F, -1.9F}
                Box(x, y, 2, 3.2F, 0.12F, 6.9F, glazing)
                Box(x + 2.7F, y - 0.1F, 5, 0.12F, 0.3F, 0.8F, frame)
                For Each hingeZ In {3.0F, 7.4F}
                    Box(x + 0.1F, y - 0.04F, hingeZ, 0.15F, 0.2F, 0.35F, frame)
                Next
            Next
        Next
        Box(-10, -2, 0.7F, 12, 4.5F, 0.6F, Dark)
        Box(-8.5F, -0.8F, 1.3F, 8.5F, 2, 4.3F, frame)
        Box(-32, 3.6F, 1.8F, 14, 1, 0.35F, belt)
        For Each y In {3.45F, 4.75F}
            Beam(-32, y, 2.5F, -18, y, 2.5F, 0.12F, frame)
        Next
        For Each x In {-30.0F, -20.0F}
            Box(x, 3.8F, 0, 0.3F, 0.3F, 1.8F, frame)
            Box(x - 0.5F, 3.5F, 0, 1.3F, 0.9F, 0.15F, frame)
        Next

        Box(2.5F, 1, 9.7F, 28.5F, 3, 0.35F, frame)
        For index = 0 To 55
            Box(2.6F + index * 0.5F, 1.15F, 10.05F, 0.075F, 2.65F, 0.05F, Shade(frame, 0.8F))
        Next
        For Each x In {3.0F, 10.0F, 17.0F, 24.0F, 30.5F}
            For Each y In {-4.8F, 3.6F}
                Box(x, y, 0, 0.4F, 0.4F, 9.7F, frame)
                Box(x - 0.3F, y - 0.3F, 0, 1, 1, 0.2F, frame)
                Beam(x, y, 4, x + 0.2F, y, 9.6F, 0.25F, frame)
            Next
            Beam(x, -4.8F, 9.5F, x, 4, 9.5F, 0.35F, frame)
        Next
        For Each y In {-4.8F, 3.8F}
            For Each x In {3.0F, 10.0F, 17.0F, 24.0F}
                Beam(x, y, 1, x + 6.5F, y, 9.4F, 0.2F, frame)
            Next
        Next
        For Each x In {3.0F, 7.0F, 11.0F, 15.0F, 19.0F, 23.0F, 27.0F, 30.8F}
            Box(x, 3.8F, 10.1F, 0.16F, 0.16F, 2.7F, frame)
        Next
        For Each z In {11.5F, 12.8F}
            Beam(2.6F, 3.9F, z, 31, 3.9F, z, 0.14F, frame)
            Beam(31, 1, z, 31, 3.9F, z, 0.14F, frame)
        Next
        Box(2.6F, 3.85F, 10.1F, 11.5F, 0.12F, 0.6F, shell)
        Box(17.5F, 3.85F, 10.1F, 13.5F, 0.12F, 0.6F, shell)

        ' Open treads and diagonal stringers connect the ground to the front platform.
        For index = 0 To 13
            Dim stairY = 9.3F - index * 0.4F
            Dim stairZ = (index + 1) * 10.05F / 14
            Box(14.3F, stairY, stairZ - 0.15F, 3, 0.4F, 0.15F, frame)
            For Each offset In {0.1F, 0.22F}
                Box(14.45F, stairY + offset, stairZ, 2.7F, 0.045F, 0.035F, Shade(frame, 0.8F))
            Next
        Next
        For Each x In {14.2F, 17.4F}
            Beam(x, 9.8F, 0.2F, x, 3.8F, 9.7F, 0.22F, frame)
            Beam(x, 9.8F, 2.7F, x, 3.8F, 12.6F, 0.14F, frame)
            Beam(x, 9.8F, 1.5F, x, 3.8F, 11.4F, 0.12F, frame)
            For index = 0 To 3
                Dim stairY = 9.6F - index * 1.8F
                Dim stairZ = 0.7F + index * 2.97F
                Box(x, stairY, stairZ, 0.14F, 0.14F, 2.5F, frame)
            Next
        Next

        Face({V(3, -4.2F, 9.6F), V(29, -4.2F, 16.6F),
              V(29, -0.8F, 16.6F), V(3, -0.8F, 9.6F)}, belt)
        For Each y In {-4.35F, -0.65F}
            Face({V(3, y, 9.4F), V(29, y, 16.4F), V(29, y, 17.4F), V(3, y, 10.4F)}, shell)
            Beam(3, y, 9.4F, 29, y, 16.4F, 0.35F, frame)
            Beam(3, y, 10.4F, 29, y, 17.4F, 0.18F, frame)
        Next
        Face({V(1, -4.2F, 9.4F), V(3, -4.2F, 9.6F),
              V(3, -0.8F, 9.6F), V(1, -0.8F, 9.4F)}, frame)
        For index = 0 To 31
            Dim x = 3.3F + index * 0.8F
            Dim conveyorZ = 9.6F + (x - 3) * 7 / 26
            Beam(x, -4.2F, conveyorZ + 0.1F, x, -0.8F, conveyorZ + 0.1F, 0.16F, frame)
            If index Mod 4 = 0 Then
                For Each y In {-4.35F, -0.65F}
                    Box(x, y, conveyorZ, 0.15F, 0.15F, 1, frame)
                Next
            End If
        Next

        For Each x In {23.0F, 29.0F}
            For Each y In {-8.0F, -5.0F}
                Box(x, y, 0, 0.35F, 0.35F, 17.8F, frame)
            Next
            Beam(x, -8, 6, x, -5, 12, 0.22F, frame)
        Next
        Box(22, -9.9F, 9.7F, 10, 1.8F, 0.35F, frame)
        For Each x In {22.2F, 25.4F, 28.6F, 31.8F}
            Box(x - 0.1F, -9.9F, 10.05F, 0.2F, 0.2F, 2.7F, frame)
        Next
        For Each z In {11.5F, 12.7F}
            Beam(22, -9.8F, z, 32, -9.8F, z, 0.14F, frame)
        Next
        Face({V(24.2F, -7.2F, 13.6F), V(27.8F, -7.2F, 13.6F),
              V(29, -8, 17.7F), V(23, -8, 17.7F)}, shell)
        Face({V(27.8F, -7.2F, 13.6F), V(27.8F, -5.8F, 13.6F),
              V(29, -5, 17.7F), V(29, -8, 17.7F)}, shell)
        Face({V(27.8F, -5.8F, 13.6F), V(24.2F, -5.8F, 13.6F),
              V(23, -5, 17.7F), V(29, -5, 17.7F)}, shell)
        Face({V(24.2F, -5.8F, 13.6F), V(24.2F, -7.2F, 13.6F),
              V(23, -8, 17.7F), V(23, -5, 17.7F)}, shell)
        Box(24.2F, -7.2F, 13.5F, 3.6F, 1.4F, 0.15F, belt)
        For Each y In {-8.0F, -5.0F}
            Beam(23, y, 17.7F, 29, y, 17.7F, 0.25F, frame)
        Next
        Beam(23, -8, 17.7F, 23, -5, 17.7F, 0.25F, frame)
        Beam(29, -8, 17.7F, 29, -5, 17.7F, 0.25F, frame)
        Beam(27, -6.5F, 14, 30.6F, -2.5F, 19.6F, 0.9F, belt)
        For Each y In {-3.0F, -2.0F}
            Beam(28, y - 3.7F, 14.5F, 31, y, 19.7F, 0.25F, frame)
        Next
        Box(29.3F, -3.6F, 19.7F, 2.2F, 2.2F, 0.3F, frame)
        Box(28.9F, -1.1F, 14.7F, 1.3F, 1.8F, 1.2F, glazing)
        Cylinder(29.6F, -0.2F, 15.9F, 0.4F, 0.7F, frame)

        If includeStandaloneFeed Then
        Box(-5.8F, 6.1F, 0.4F, 5.8F, 3.8F, 2.5F, shell)
        Box(-5.4F, 6.5F, 2.9F, 5, 3, 0.12F, belt)
        For Each x In {-5.8F, -0.4F}
            Box(x, 6.1F, 2.9F, 0.4F, 3.8F, 1.1F, shell)
        Next
        Box(-5.8F, 9.5F, 2.9F, 5.8F, 0.4F, 1.1F, shell)
        Box(-5.1F, 6.6F, 0, 0.4F, 0.4F, 0.4F, Dark)
        Box(-1.1F, 9.1F, 0, 0.4F, 0.4F, 0.4F, Dark)
        Box(-6.3F, 6.5F, 0.4F, 1.4F, 2.5F, 15.3F, shell)
        Box(-4.88F, 6.65F, 3, 0.12F, 2.15F, 12.4F, belt)
        For index = 0 To 17
            Box(-4.75F, 6.7F, 3.2F + index * 0.67F, 0.15F, 2, 0.15F, frame)
        Next
        Beam(-5.6F, 7.7F, 15.5F, -11.4F, 3, 12.5F, 0.5F, frame)
        Box(-6.3F, 6.5F, 15.7F, 1.8F, 2.5F, 0.4F, trim)
        Box(-0.8F, 7.2F, 0.5F, 1.2F, 1.5F, 1.2F, glazing)
        WallCylinder(-0.2F, 8.7F, 1.1F, 0.45F, 0.6F, frame)
        End If

        For Each x In {-10.0F, -7.2F}
            BeveledBox(x, 4.1F, 0.3F, 2.4F, 1.8F, 3.8F, 0.12F, shell)
            Box(x + 0.2F, 5.91F, 0.7F, 2, 0.06F, 3, frame)
            Box(x + 0.1F, 4.4F, 4.1F, 2.2F, 1.6F, 0.3F, frame)
            Box(x + 0.25F, 5.35F, 4.4F, 1.9F, 0.2F, 1.5F, shell)
            Box(x + 0.45F, 5.56F, 4.7F, 1.45F, 0.08F, 0.95F, glazing)
            Box(x + 0.6F, 5.65F, 4.8F, 1.1F, 0.03F, 0.65F, Color.LightSkyBlue)
            Box(x + 0.68F, 5.69F, 5.25F, 0.95F, 0.02F, 0.07F, Color.FromArgb(195, 226, 239))
            For index = 0 To 2
                Box(x + 0.68F + index * 0.3F, 5.69F, 4.9F, 0.2F, 0.02F, 0.18F, glazing)
            Next
            For Each offset In {0.35F, 0.75F, 1.15F}
                Cylinder(x + offset, 4.8F, 4.4F, 0.1F, 0.08F, Dark)
            Next
            Cylinder(x + 1.8F, 4.8F, 4.4F, 0.12F, 0.1F, Color.Red)
            Box(x + 1.9F, 6, 2.5F, 0.12F, 0.12F, 0.65F, Dark)
        Next
        BeveledBox(27.4F, 1.6F, 0.3F, 2.8F, 1.6F, 6, 0.12F, shell)
        Box(27.6F, 3.21F, 0.5F, 2.4F, 0.08F, 5.6F, frame)
        For index = 0 To 7
            Box(27.8F, 3.31F, 1 + index * 0.25F, 1.5F, 0.06F, 0.08F, Dark)
        Next
        Beam(25.5F, -0.4F, 7.5F, 25.5F, 1.2F, 7.5F, 0.22F, Color.FromArgb(179, 188, 91))
        Beam(25.5F, 1.2F, 7.5F, 28.5F, 1.2F, 5.6F, 0.22F, Color.FromArgb(179, 188, 91))
        Cylinder(30.2F, 3.3F, 12.9F, 0.15F, 0.6F, Dark)
        Cylinder(30.2F, 3.3F, 13.5F, 0.18F, 0.3F, Color.Red)
    End Sub

    Private Sub FillerAccessory(model As String)
        Dim stainless = Body(Steel)
        Select Case model
            Case "Preform elevator", "Cap elevator"
                Box(-18, -9, 0, 36, 18, 2, Dark)
                Box(-16, -8, 2, 15, 16, 13, stainless)
                Box(-14, -6, 15, 11, 12, 1, Dark)
                For Each side In {-1.0F, 1.0F}
                    Line(-4, side * 6, 6, 14, side * 6, 34, Steel)
                    Line(-4, side * 6, 9, 14, side * 6, 37, Steel)
                    Box(12, side * 7, 2, 2, 2, 33, stainless)
                Next
                For index = 0 To 11
                    Dim x = -4 + index * 1.5F, z = 8 + index * 2.3F
                    Box(x, -5, z, 2.5F, 10, 1.2F, Body(Color.SteelBlue))
                    If model = "Preform elevator" Then
                        HorizontalTank(x, 0, z + 1.7F, 2.5F, 0.8F, Color.LightSkyBlue)
                    Else
                        Cylinder(x, 0, z + 1.2F, 1, 0.8F, Color.Gold)
                    End If
                Next
                Box(13, -5, 35, 7, 10, 2, stainless)
            Case "Cap sorter and chute"
                Box(-10, -10, 0, 20, 20, 2, Dark)
                For Each x In {-7.0F, 7.0F}
                    Box(x, -6, 2, 2, 12, 21, stainless)
                Next
                Cylinder(0, 0, 23, 10, 5, stainless)
                Cylinder(0, 0, 28, 8, 0.5F, Dark)
                For index = 0 To 11
                    Dim a = index * Math.PI / 6
                    Cylinder(CSng(Math.Cos(a) * 6), CSng(Math.Sin(a) * 6), 28.5F, 1, 0.8F, Color.Gold)
                Next
                For Each y In {-2.0F, 2.0F}
                    Line(8, y, 26, 22, y, 14, Steel)
                Next
                For index = 0 To 5
                    Cylinder(9 + index * 2, 0, 25 - index * 1.7F, 0.8F, 0.7F, Color.Gold)
                Next
            Case "Product buffer tank"
                For Each x In {-6.0F, 5.0F}
                    Box(x, -5, 0, 1.5F, 10, 6, stainless)
                Next
                Cylinder(0, 0, 6, 9, 22, stainless)
                Cylinder(0, 0, 28, 8, 2, stainless)
                Cylinder(0, 0, 30, 3, 1, Dark)
                Cylinder(0, 0, 31, 1, 4, Steel)
                Box(-3, -3, 35, 6, 6, 5, stainless)
                HorizontalTank(7, 0, 8, 7, 1.2F, Steel)
                Cylinder(12, 0, 9, 1, 4, Steel)
                Cylinder(12, 0, 13, 2, 0.6F, Color.RoyalBlue)
            Case "Sanitary product pump"
                Pump(0)
                HorizontalTank(-20, 0, 7, 1, 2.6F, Steel)
                Cylinder(-11, 0, 16.8F, 3, 0.6F, Steel)
            Case "Sanitary valve manifold", "CIP return skid"
                Box(-22, -9, 0, 44, 18, 2, Dark)
                If model = "CIP return skid" Then
                    Component(0, 0, 0.7F, Sub(renderer) renderer.Pump(0))
                    Cylinder(12, 0, 2, 5, 14, stainless)
                    Cylinder(12, 0, 16, 4, 1, Steel)
                    HorizontalTank(-18, -5, 16, 33, 1.2F, Steel)
                Else
                    For Each x In {-18.0F, 16.0F}
                        Box(x, -7, 2, 2, 14, 23, stainless)
                    Next
                    For Each z In {9.0F, 18.0F}
                        HorizontalTank(-21, 0, z, 42, 1.3F, Steel)
                    Next
                    For Each x In {-12.0F, 0.0F, 12.0F}
                        Cylinder(x, 0, 8, 2, 12, Steel)
                        Cylinder(x, 0, 20, 3, 5, stainless)
                        Cylinder(x, 0, 25, 2.5F, 3, Dark)
                        Box(x - 1, -1, 28, 2, 2, 2, Color.LightSkyBlue)
                    Next
                End If
            Case "Air conveyor blower"
                Box(-11, -9, 0, 22, 18, 2, stainless)
                HorizontalTank(-8, 0, 9, 12, 7, stainless)
                HorizontalTank(-9, 0, 9, 1, 5, Dark)
                Box(2, -4, 3, 8, 8, 8, Body(Color.SteelBlue))
                Cylinder(-1, 0, 15, 3, 6, Steel)
                Box(-5, -4, 21, 8, 8, 2, stainless)
            Case "HEPA filler canopy"
                For Each x In {-24.0F, 22.0F}
                    For Each y In {-21.0F, 19.0F}
                        Box(x, y, 0, 2, 2, 40, stainless)
                    Next
                Next
                Box(-24, -21, 40, 48, 42, 3, stainless)
                For Each x In {-17.0F, -3.0F, 11.0F}
                    Box(x, -15, 43, 10, 30, 2, Body(Color.WhiteSmoke))
                    Fan(x + 5, 0, 45, 4)
                    For vent = 0 To 4
                        Line(x + 1, -12 + vent * 5, 45, x + 9, -12 + vent * 5, 45, Dark)
                    Next
                Next
                Line(-23, -20, 33, 23, -20, 33, Steel)
            Case "Foam cleaning station"
                Box(-8, -4, 0, 16, 8, 22, stainless)
                Box(-6, 4.2F, 15, 12, 0.5F, 5, Dark)
                Cylinder(0, 0, 22, 2, 2, Color.RoyalBlue)
                For index = 0 To 4
                    HorizontalTank(-5, 6, 4 + index * 2, 10, 0.8F, Body(Color.SteelBlue))
                Next
                Line(5, 6, 12, 9, 6, 22, Steel)
                Box(-5, -3, 1, 4, 6, 8, Color.WhiteSmoke)
            Case "Rinser filler capper monoblock"
                Box(-42, -18, 0, 84, 36, 3, Dark)
                For Each station In {(-25.0F, 8.0F), (0.0F, 13.0F), (27.0F, 6.0F)}
                    Cylinder(station.Item1, 0, 3, station.Item2, 7, stainless)
                    Cylinder(station.Item1, 0, 10, 2, 18, Steel)
                    Cylinder(station.Item1, 0, 27, station.Item2, 2, stainless)
                    For index = 0 To 7
                        Dim a = index * Math.PI / 4
                        Dim x = station.Item1 + CSng(Math.Cos(a) * (station.Item2 - 2))
                        Dim y = CSng(Math.Sin(a) * (station.Item2 - 2))
                        Bottle(x, y, 10)
                        Cylinder(x, y, 22, 0.6F, 5, Steel)
                    Next
                Next
                For Each x In {-41.0F, 39.0F}
                    For Each y In {-17.0F, 15.0F}
                        Box(x, y, 3, 2, 2, 30, stainless)
                    Next
                Next
                Box(-42, -18, 33, 84, 36, 2, stainless)
                Conveyor(-46, 92)
                Component(34, 20, 0.5F, Sub(renderer) renderer.ControlCabinet("HMI console"))
            Case "Bottle unscrambler"
                Cylinder(0, 0, 0, 17, 3, Dark)
                Cylinder(0, 0, 3, 16, 21, stainless)
                Cylinder(0, 0, 24, 15, 1, Dark)
                Cylinder(0, 0, 25, 7, 3, stainless)
                For index = 0 To 9
                    Dim a = index * Math.PI / 5
                    Bottle(CSng(Math.Cos(a) * 11), CSng(Math.Sin(a) * 11), 25)
                Next
                Component(19, 0, 0.7F, Sub(renderer) renderer.Conveyor(-10, 30))
            Case "Empty container depalletizer"
                Box(-25, -20, 0, 50, 40, 2, Dark)
                Box(-18, -14, 2, 36, 28, 3, Body(Color.Tan))
                For level = 0 To 3
                    Box(-18, -14, 5 + level * 6, 36, 28, 0.5F, Steel)
                    For Each x In {-13.0F, -4.0F, 5.0F, 14.0F}
                        For Each y In {-9.0F, 0.0F, 9.0F}
                            Bottle(x, y, 6 + level * 6)
                        Next
                    Next
                Next
                For Each x In {-24.0F, 22.0F}
                    Box(x, -18, 2, 2, 36, 37, stainless)
                Next
                Box(-24, -18, 39, 48, 36, 2, stainless)
                Box(-20, -17, 32, 40, 3, 3, Body(Color.SteelBlue))
                Component(27, 0, 0.6F, Sub(renderer) renderer.Conveyor(-5, 25))
            Case "Returnable bottle washer"
                Casing(Sub()
                           Box(-35, -14, 0, 70, 28, 3, Dark)
                           Box(-32, -13, 3, 64, 26, 24, stainless)
                       End Sub)
                Conveyor(-42, 84)
                For Each x In {-32.0F, 31.0F}
                    Box(x, -6, 7, 1, 12, 13, Dark)
                Next
                If _depthY > 0 Then
                    For Each x In {-24.0F, -8.0F, 8.0F, 24.0F}
                        Box(x - 5, 13.2F, 13, 10, 0.5F, 8, Dark)
                        Box(x - 4, 13.8F, 14, 8, 0.2F, 6, Color.LightSkyBlue)
                    Next
                End If
                Cylinder(-15, 0, 27, 3, 5, Steel)
                Cylinder(15, 0, 27, 3, 5, Steel)
                Component(0, -17, 0.5F, Sub(renderer) renderer.Pump(0))
            Case "Can seamer"
                Box(-13, -11, 0, 26, 22, 3, Dark)
                Cylinder(0, 0, 3, 10, 8, stainless)
                Cylinder(0, 0, 11, 2, 18, Steel)
                Cylinder(0, 0, 28, 10, 4, stainless)
                Box(-5, -5, 32, 10, 10, 6, Body(Color.SteelBlue))
                For index = 0 To 5
                    Dim a = index * Math.PI / 3
                    Dim x = CSng(Math.Cos(a) * 7), y = CSng(Math.Sin(a) * 7)
                    Cylinder(x, y, 11, 1.5F, 5, Steel)
                    Cylinder(x, y, 23, 2, 5, Dark)
                Next
                Conveyor(-24, 48)
            Case "Cap sterilizer", "Bottle air knife dryer", "Bottle reject station", "Inline checkweigher", "Inline metal detector"
                Conveyor(-24, 48)
                If model = "Bottle reject station" Then
                    Box(-5, -11, 8, 10, 5, 4, stainless)
                    Line(0, -6, 10, 0, -1, 10, Steel)
                    Box(-3, -2, 8, 6, 1, 4, Body(Color.SteelBlue))
                    Box(5, 5, 0, 15, 14, 13, stainless)
                    Box(6, 6, 13, 13, 12, 0.5F, Dark)
                    Bottle(12, 11, 3)
                ElseIf model = "Inline checkweigher" Then
                    Box(-8, -4, 5, 16, 8, 3, Body(Color.SeaGreen))
                    Box(12, -9, 0, 2, 2, 22, stainless)
                    Box(9, -10, 22, 10, 3, 7, Dark)
                    Box(10, -6.8F, 23, 8, 0.2F, 5, Color.LightSkyBlue)
                Else
                    For Each y In {-7.0F, 5.0F}
                        Box(-10, y, 0, 20, 2, 25, stainless)
                    Next
                    Box(-10, -7, 25, 20, 14, 3, stainless)
                    If model = "Bottle air knife dryer" Then
                        For Each y In {-5.0F, 4.0F}
                            Box(-7, y, 9, 14, 1, 14, Body(Color.SteelBlue))
                            Line(-7, y, 14, 7, y, 22, Steel)
                        Next
                        HorizontalTank(-5, 0, 31, 10, 3, Body(Color.SteelBlue))
                    ElseIf model = "Cap sterilizer" Then
                        Box(-9, -6, 16, 18, 12, 9, Body(Color.SteelBlue))
                        For index = 0 To 8
                            Cylinder(-18 + index * 4, 0, 8, 1.3F, 1, Color.Gold)
                        Next
                        Cylinder(0, 0, 28, 2, 5, Steel)
                    Else
                        Box(-9, 7.2F, 19, 9, 0.6F, 5, Dark)
                        Box(-8, 8, 20, 7, 0.3F, 3, Color.LightSkyBlue)
                    End If
                End If
        End Select
    End Sub

    Public Shared Function IsOpeningModel(model As String) As Boolean
        Return {"Single swing gate", "Double swing gate", "Sliding gate", "Rapid roll-up gate", "Glass entry door", "Wood entry door"}.Contains(model)
    End Function

    Private Function Body(original As Color) As Color
        Return If(_bodyColor, original)
    End Function

    Private Sub Motor(x As Single, y As Single)
        Box(x - 9, y - 5, 0, 18, 10, 1.5F, Dark)
        Box(x - 5, y - 3, 1.5F, 2, 6, 2, Body(Steel))
        Box(x + 3, y - 3, 1.5F, 2, 6, 2, Body(Steel))
        HorizontalTank(x - 7, y, 7, 14, 4, Body(Color.SteelBlue))
        For index As Integer = 0 To 7
            Line(x - 5 + index * 1.4F, y - 3, 9.5F, x - 5 + index * 1.4F, y + 3, 9.5F, Dark)
        Next
        Box(x - 1, y - 2, 11, 5, 4, 3, Body(Steel))
        HorizontalTank(x + 7, y, 7, 4, 1, Steel)
        HorizontalTank(x - 9, y, 7, 2, 4.5F, Body(Steel))
    End Sub

    Private Sub Pump(y As Single)
        Motor(5, y)
        HorizontalTank(-7, y, 7, 4, 1.6F, Steel)
        HorizontalTank(-14, y, 7, 7, 4.5F, Body(Color.FromArgb(44, 131, 135)))
        HorizontalTank(-19, y, 7, 5, 2, Body(Steel))
        Cylinder(-11, y, 10, 1.8F, 6, Body(Steel))
        Cylinder(-11, y, 16, 3, 0.8F, Steel)
    End Sub

    Private Sub StorageTank()
        For Each x In {-5.0F, 5.0F}
            Box(x, -3, 0, 1, 6, 4, Steel)
        Next
        Cylinder(0, 0, 4, 9, 22, Body(Steel))
        Cylinder(0, 0, 26, 7, 2, Body(Steel))
        Cylinder(0, 0, 28, 2, 2, Dark)
        Line(8, 0, 8, 14, 0, 8, Color.SteelBlue)
        Line(-7, 6, 5, -7, 6, 27, Dark)
        Line(-4, 8, 5, -4, 8, 27, Dark)
        For rung As Integer = 0 To 7
            Line(-7, 6, 6 + rung * 3, -4, 8, 6 + rung * 3, Steel)
        Next
    End Sub

    Private Sub ControlCabinet(model As String)
        If model = "Energy meter" Then
            Box(-6, -2, 5, 12, 4, 12, Body(Steel))
            Box(-5, 2.2F, 9, 10, 0.3F, 6, Dark)
            Box(-4, 2.6F, 11, 8, 0.2F, 3, Color.LightGreen)
            For index As Integer = 0 To 2
                Box(-3 + index * 3, 2.6F, 7, 1, 0.3F, 1, Dark)
            Next
            Return
        End If
        If model = "HMI console" Then
            Box(-7, -5, 0, 14, 10, 2, Dark)
            Box(-2, -2, 2, 4, 4, 11, Body(Steel))
            Box(-9, -3, 13, 18, 6, 12, Body(Steel))
            Box(-7, 3.2F, 16, 14, 0.5F, 7, Dark)
            Box(-6, 3.8F, 17, 12, 0.2F, 5, Color.LightSkyBlue)
            Return
        End If
        ElectricalPanel(If(model = "Motor control center", "Lineup", "Floor"), False)
        Select Case model
            Case "ATS cabinet"
                For index As Integer = 0 To 1
                    Box(-3 + index * 4, 5.2F, 18, 2, 0.5F, 2, Color.FromArgb(200, 187, 120))
                Next
                Box(-2, 5.2F, 11, 4, 0.5F, 5, Dark)
                FaceText("ATS", -4, 5.8F, 24, Dark)
            Case "PLC cabinet"
                Box(-4, 5.2F, 11, 8, 0.4F, 10, Dark)
                For index As Integer = 0 To 4
                    Box(-3.5F + index * 1.4F, 5.7F, 13, 1, 0.5F, 6, Steel)
                    Box(-3.3F + index * 1.4F, 6.3F, 18, 0.3F, 0.1F, 0.4F, Color.FromArgb(120, 166, 133))
                Next
            Case "Motor control center"
                For column As Integer = 0 To 2
                    For row As Integer = 0 To 3
                        Box(-13.5F + column * 10, 5.2F, 4 + row * 5, 7, 0.4F, 4, Body(Steel))
                        Box(-12.5F + column * 10, 5.8F, 5 + row * 5, 2, 0.2F, 2, Dark)
                    Next
                Next
            Case "Variable frequency drive"
                Box(-3, 5.2F, 17, 6, 0.4F, 5, Dark)
                Box(-2, 5.7F, 19, 4, 0.2F, 2, Color.LightSkyBlue)
                Vents(-3, 5.7F, 5, 6, 7)
        End Select
    End Sub

    Private Sub CoolingTower()
        Box(-11, -9, 0, 22, 18, 3, Dark)
        Box(-10, -8, 3, 20, 16, 20, Body(Color.FromArgb(159, 184, 176)))
        Vents(-9, 8.2F, 5, 18, 12)
        Cylinder(0, 0, 23, 7, 3, Body(Steel))
        Fan(0, 0, 26.2F, 6)
        Line(10, 0, 7, 15, 0, 7, Color.SteelBlue)
        Line(10, 3, 17, 15, 3, 17, Color.Firebrick)
    End Sub

    Private Sub VentilationFan()
        Box(-9, -9, 0, 18, 18, 3, Body(Steel))
        Cylinder(0, 0, 3, 8, 7, Body(Steel))
        Fan(0, 0, 10.2F, 7)
    End Sub

    Private Sub Fan(x As Single, y As Single, z As Single, radius As Single)
        Cylinder(x, y, z, radius, 0.4F, Dark)
        For blade As Integer = 0 To 3
            Dim angle = blade * Math.PI / 2
            Dim dx = CSng(Math.Cos(angle) * radius * 0.8)
            Dim dy = CSng(Math.Sin(angle) * radius * 0.8)
            Face({V(x - dy * 0.22F, y + dx * 0.22F, z + 0.6F),
                  V(x + dx - dy * 0.25F, y + dy + dx * 0.25F, z + 0.6F),
                  V(x + dx + dy * 0.25F, y + dy - dx * 0.25F, z + 0.6F),
                  V(x + dy * 0.1F, y - dx * 0.1F, z + 0.6F)}, Steel)
        Next
        Cylinder(x, y, z + 0.8F, radius * 0.17F, 0.8F, Steel)
        For ring As Integer = 1 To 3
            Dim points As New List(Of PointF)
            For index As Integer = 0 To 16
                Dim angle = index * Math.PI / 8
                points.Add(_project(x + CSng(Math.Cos(angle) * radius * ring / 3), y + CSng(Math.Sin(angle) * radius * ring / 3), z + 1.8F))
            Next
            _commands.Add((Depth(x, y, z + 1.8F), Sub()
                                                     Using pen As New Pen(Color.FromArgb(145, 158, 163), 0.55F / _context.Scale)
                                                         _graphics.DrawLines(pen, points.ToArray())
                                                     End Using
                                                 End Sub))
        Next
    End Sub

    Private Sub Sensor(model As String)
        Cylinder(0, 0, 0, 3, 1, Steel)
        Cylinder(0, 0, 1, 0.7F, 10, Steel)
        If model = "Stack beacon" Then
            For level As Integer = 0 To 2
                Cylinder(0, 0, 11 + level * 4, 2.5F, 3.8F, {Color.SeaGreen, Color.Goldenrod, Color.Firebrick}(level))
            Next
            Cylinder(0, 0, 23, 2.8F, 1, Dark)
        ElseIf model = "Pressure transmitter" Then
            Cylinder(0, 0, 11, 3.8F, 4, Body(Color.SteelBlue))
            Box(-2.5F, 3.5F, 12, 5, 0.5F, 3, Color.WhiteSmoke)
            Line(-1.5F, 4.2F, 12.5F, 1.5F, 4.2F, 14, Dark)
        Else
            Box(-3, -3, 11, 6, 6, 5, Body(Steel))
            Cylinder(0, 0, 16, 1, 3, Body(Steel))
            Line(0, 0, 19, 4, 0, 19, Dark)
        End If
    End Sub

    Private Sub Distribution(model As String)
        If model = "Cable tray" Then
            Box(-18, -4, 3, 36, 1, 3, Body(Steel))
            Box(-18, 3, 3, 36, 1, 3, Body(Steel))
            For index As Integer = 0 To 11
                Box(-17 + index * 3, -3, 3, 0.7F, 6, 0.7F, Steel)
            Next
            For index As Integer = 0 To 2
                Line(-18, -1 + index, 4, 18, -1 + index, 4, Dark)
            Next
        Else
            HorizontalTank(-18, 0, 5, 36, 2, Body(Color.SteelBlue))
            For Each x In {-16.0F, 14.0F}
                HorizontalTank(x, 0, 5, 2, 3, Steel)
            Next
            If model = "Isolation valve" Then
                Box(-3, -3, 2, 6, 6, 6, Body(Steel))
                Cylinder(0, 0, 8, 0.6F, 6, Steel)
                Cylinder(0, 0, 14, 4, 0.6F, Color.Firebrick)
                Line(-4, 0, 15, 4, 0, 15, Dark)
                Line(0, -4, 15, 0, 4, 15, Dark)
            End If
        End If
    End Sub

    Private Sub FireMonitoringDevice(model As String)
        If model.Contains("cylinder bank", StringComparison.Ordinal) Then
            Box(-15, -5, 0, 30, 10, 2, Dark)
            For Each x In {-10.0F, 0.0F, 10.0F}
                Cylinder(x, 0, 2, 4, 28, Body(If(model.StartsWith("CO2", StringComparison.Ordinal), Color.Firebrick, Color.DarkRed)))
                Cylinder(x, 0, 30, 2.5F, 2, Steel)
                Cylinder(x, 0, 32, 1.1F, 4, Color.Goldenrod)
                Box(x - 2, 4, 12, 4, 0.3F, 8, Color.WhiteSmoke)
                Line(x, 0, 36, x, -4, 39, Color.Goldenrod)
            Next
            HorizontalTank(-13, -4, 39, 26, 0.8F, Color.Goldenrod)
            Box(-16, -5, 15, 32, 1, 2, Steel)
        ElseIf model = "Zeta beam detector" Then
            Box(-5, -2, 0, 10, 4, 12, Body(Color.WhiteSmoke))
            Box(-3.5F, 2.1F, 3, 7, 0.5F, 7, Dark)
            WallCylinder(0, 2.6F, 6.5F, 2.6F, 0.6F, Color.Black)
            WallCylinder(0, 3.2F, 6.5F, 1.6F, 0.15F, Color.FromArgb(44, 64, 86))
            Box(3, 2.7F, 1.5F, 0.6F, 0.2F, 0.6F, Color.Firebrick)
        ElseIf model = "Zeta siren" Then
            Box(-4, -3, 0, 8, 6, 2, Dark)
            Box(-3, -3, 2, 6, 6, 8, Body(Color.Firebrick))
            Box(-6, 2, 5, 12, 4, 9, Body(Color.Firebrick))
            Box(-5, 6.1F, 6, 10, 0.3F, 7, Dark)
            For Each z In {7.0F, 9.0F, 11.0F}
                Line(-4, 6.5F, z, 4, 6.5F, z, Steel)
            Next
        ElseIf model.Contains("detector", StringComparison.Ordinal) Then
            Cylinder(0, 0, -0.7F, 6, 0.7F, Body(Color.WhiteSmoke))
            Cylinder(0, 0, -1.2F, 5.7F, 0.5F, Body(Color.Gainsboro))
            Cylinder(0, 0, -3.4F, 5.2F, 2.2F, Body(Color.Ivory))
            If model <> "Zeta heat detector" Then
                For Each z In {-2.8F, -2.0F}
                    Cylinder(0, 0, z, 5.25F, 0.3F, Dark)
                Next
                For index = 0 To 7
                    Dim angle = index * Math.PI / 4
                    Box(CSng(Math.Cos(angle) * 4.7) - 0.3F, CSng(Math.Sin(angle) * 4.7) - 0.3F,
                        -3.0F, 0.6F, 0.6F, 1.8F, Body(Color.WhiteSmoke))
                Next
            End If
            Cylinder(0, 0, -4.0F, 3.9F, 0.6F, Body(Color.WhiteSmoke))
            Box(2.6F, 2.6F, -3.6F, 0.6F, 0.6F, 0.5F, Color.Firebrick)
            If model = "Zeta heat detector" OrElse model = "Zeta multi-sensor detector" Then
                Cylinder(0, 0, -5.3F, 0.65F, 1.3F, Color.FromArgb(120, 87, 62))
                For Each x In {-1.3F, 1.3F}
                    Box(x - 0.2F, -0.2F, -5.4F, 0.4F, 0.4F, 1.4F, Body(Color.WhiteSmoke))
                Next
            End If
        ElseIf model = "Zeta sounder" OrElse model = "Zeta sounder beacon" Then
            WallCylinder(0, -2, 5, 5, 2, Body(Color.Firebrick))
            WallCylinder(0, 0, 5, 4.5F, 4, Body(Color.Firebrick))
            WallCylinder(0, 4, 5, 3.2F, 0.3F, Dark)
            For Each z In {3.5F, 5.0F, 6.5F}
                Line(-2.2F, 4.4F, z, 2.2F, 4.4F, z, Color.Firebrick)
            Next
            If model.EndsWith("beacon", StringComparison.Ordinal) Then
                Cylinder(0, 0, 10, 3, 0.6F, Body(Color.Firebrick))
                Cylinder(0, 0, 10.6F, 2.8F, 3.2F, Color.FromArgb(245, 164, 146))
                Cylinder(0, 0, 13.8F, 2.2F, 0.4F, Color.MistyRose)
            End If
        ElseIf model = "Zeta manual call point" Then
            Box(-5, -2, 0, 10, 4, 10, Body(Color.Firebrick))
            Box(-3.5F, 2.1F, 2, 7, 0.3F, 5, Color.WhiteSmoke)
            Line(-2, 2.5F, 4.5F, 2, 2.5F, 4.5F, Color.Firebrick)
            Line(-1, 2.5F, 3.2F, 0, 2.5F, 4.3F, Color.Firebrick)
            Line(1, 2.5F, 3.2F, 0, 2.5F, 4.3F, Color.Firebrick)
            FaceText("ZETA", -3.5F, 2.5F, 9.5F, Color.WhiteSmoke)
        ElseIf model.Contains("module", StringComparison.Ordinal) Then
            Box(-6, -2, 0, 12, 4, 8, Body(Color.WhiteSmoke))
            Box(-4, 2.1F, 2, 8, 0.3F, 4, Color.LightGray)
            Box(3, 2.5F, 6, 1, 0.2F, 1, Color.Goldenrod)
        Else
            Box(-12, -3, 0, 24, 6, 28, Body(Color.Firebrick))
            Box(-9, 3.1F, 17, 18, 0.4F, 8, Dark)
            Box(-7, 3.6F, 19, 10, 0.2F, 4, Color.LightSkyBlue)
            For Each x In {-7.0F, -3.0F, 1.0F, 5.0F}
                Box(x, 3.7F, 12, 1.2F, 0.3F, 1.2F, If(x < 0, Color.Goldenrod, Color.Firebrick))
            Next
            Vents(-8, 3.2F, 4, 16, 3)
            FaceText("ZETA", -8, 3.8F, 27, Color.WhiteSmoke)
        End If
    End Sub

    Private Sub FireDevice(model As String)
        If model = "Smoke detector" Then
            Cylinder(0, 0, 10, 5, 1, Body(Steel))
            Cylinder(0, 0, 11, 4, 3, Body(Color.WhiteSmoke))
            Cylinder(0, 0, 14, 2.5F, 1, Dark)
        Else
            Cylinder(0, 0, 0, 4, 2, Dark)
            Cylinder(0, 0, 2, 3.5F, If(model = "Fire hydrant", 15, 12), Body(Color.Firebrick))
            Cylinder(0, 0, 16, 1, 2, Steel)
            If model = "Fire hydrant" Then
                HorizontalTank(-6, 0, 13, 12, 2, Body(Color.Firebrick))
                Cylinder(0, 0, 18, 3, 1, Body(Color.Firebrick))
            Else
                Box(-3, -1, 15, 6, 2, 1, Dark)
                Box(-2, 3.5F, 7, 4, 0.2F, 5, Color.WhiteSmoke)
                Line(3, 0, 14, 5, 0, 10, Dark)
                Line(5, 0, 10, 5, 0, 4, Dark)
            End If
        End If
    End Sub

    Private Sub ElectricalPanel(style As String, metered As Boolean)
        Dim bays = If(style = "Lineup", 3, 1)
        Dim bayWidth = If(style = "Wall", 13.0F, 10.0F)
        Dim depth = If(style = "Wall", 4.0F, 9.0F)
        Dim height = If(style = "Wall", 17.0F, 25.0F)
        Dim width = bays * bayWidth
        Dim base = If(style = "Wall", 6.0F, 2.0F)
        If style <> "Wall" Then Box(-width / 2 - 1, -depth / 2 - 1, 0, width + 2, depth + 2, 2, Dark)
        For bay = 0 To bays - 1
            Dim x = -width / 2 + bay * bayWidth
            Box(x, -depth / 2, base, bayWidth - 0.3F, depth, height, Body(Steel))
            Box(x + 0.5F, depth / 2 + 0.1F, base + 0.7F, bayWidth - 1.3F, 0.3F, height - 1.4F, Body(Color.FromArgb(196, 204, 207)))
            Line(x + bayWidth - 2, depth / 2 + 0.6F, base + height * 0.4F,
                 x + bayWidth - 2, depth / 2 + 0.6F, base + height * 0.55F, Dark)
            Vents(x + 1.5F, depth / 2 + 0.6F, base + 2, bayWidth - 3.5F, 3)
            Box(x + 2, depth / 2 + 0.5F, base + height * 0.4F, 2, 0.5F, 3, Dark)
            If metered Then
                Box(x + 2, depth / 2 + 0.6F, base + height - 6, 5, 0.5F, 4, Dark)
                Box(x + 2.5F, depth / 2 + 1.2F, base + height - 4.5F, 4, 0.2F, 2, Color.LightGreen)
                For buttonIndex As Integer = 0 To 2
                    Box(x + 2.6F + buttonIndex, depth / 2 + 1.2F, base + height - 5.5F, 0.6F, 0.2F, 0.5F, Steel)
                Next
            End If
        Next
    End Sub

    Private Sub Generator()
        Box(-11, -6, 0, 22, 12, 2, Dark)
        Box(-10, -5, 2, 20, 10, 11, Body(Yellow))
        Box(-10.2F, -4, 3, 0.6F, 8, 9, Dark)
        For index = 0 To 5
            Line(-10.6F, -3 + index, 4, -10.6F, -3 + index, 11, Steel)
        Next
        Box(-1, -3, 13, 6, 6, 1, Body(Yellow))
        For Each x In {-6.0F, 2.0F}
            Line(x, -4.5F, 13, x, 4.5F, 13, Shade(Body(Yellow), 0.65F))
        Next
        Line(-9, -4.5F, 13, 9, -4.5F, 13, Color.FromArgb(255, 227, 120))
        Line(-9, 4.5F, 13, 9, 4.5F, 13, Shade(Body(Yellow), 0.65F))
        Cylinder(5, 1, 13, 0.8F, 7, Dark)
        Cylinder(5, 1, 20, 1.5F, 1, Dark)
        Vents(-6, 5.2F, 4, 6, 5)
        Box(3, 5.1F, 7, 3, 0.5F, 3, Dark)
        For Each x In {-7.0F, 0.0F, 7.0F}
            Line(x, 5.3F, 3, x, 5.3F, 12, Shade(Body(Yellow), 0.55F))
            Line(x + 1, 5.6F, 7, x + 1, 5.6F, 9, Dark)
        Next
        For Each x In {-8.0F, 7.0F}
            Box(x, -5, 0, 2, 10, 1.2F, Steel)
            Cylinder(x, 0, 14, 0.6F, 1.3F, Dark)
        Next
        FaceText("CAT", 2, 5.8F, 10, Color.Black)
    End Sub

    Private Sub Compressor()
        Box(-25, -13, 0, 50, 26, 3, Dark)
        For Each x In {-21.0F, 19.0F}
            Box(x, -11, 3, 3, 22, 2, Steel)
        Next
        Component(-11, 0, 0.85F, Sub(renderer) renderer.Motor(0, 0))
        Box(0, -6, 3, 15, 12, 8, Body(Color.FromArgb(53, 117, 153)))
        For index = 0 To 2
            Dim x = 1 + index * 5.5F
            Dim height = 11 - index * 2.0F
            Cylinder(x, 0, 11, 3.5F - index * 0.6F, height, Steel)
            For fin = 0 To 5
                Cylinder(x, 0, 12 + fin * height / 7, 4 - index * 0.6F, 0.5F, Dark)
            Next
            Cylinder(x, 0, 11 + height, 3.5F - index * 0.6F, 1, Steel)
            Line(x, 0, 12 + height, x, 8, 12 + height, Color.Sienna)
            Line(x, 8, 12 + height, 18, 8, 8 + index * 3, Color.Sienna)
        Next
        For Each y In {-10.0F, 10.0F}
            HorizontalTank(-20, y, 7, 40, 2, Body(Color.SteelBlue))
        Next
        Casing(Sub() Box(-3, -9, 3, 2, 18, 20, Body(Color.FromArgb(67, 87, 97))))
        For index = 0 To 8
            Line(-1, -8 + index * 2, 4, -1, -8 + index * 2, 22, Steel)
        Next
        Cylinder(19, 7, 7, 1, 13, Steel)
        HorizontalTank(17.5F, 7, 20, 2, 2, Color.WhiteSmoke)
        FaceText("HP 40 bar", -17, 12, 8, Color.White)
    End Sub

    Private Sub Ups(nxl As Boolean)
        Dim cabinets = If(nxl, 4, 2)
        Dim bay = If(nxl, 8.0F, 10.0F)
        Dim width = cabinets * bay
        Dim enclosure = Body(Color.FromArgb(40, 42, 44))
        Dim grille = Color.FromArgb(24, 26, 28)
        Dim seam = Color.FromArgb(83, 87, 90)
        Casing(Sub()
                   Box(-width / 2, -6.5F, 0, width, 13, 2, Color.FromArgb(24, 26, 28))
                   Box(-width / 2, -6, 2, width, 12, 29, enclosure)
                   For index = 0 To cabinets - 1
                       Dim x = -width / 2 + index * bay
                       If _depthY > 0 Then
                           Box(x + 0.15F, 6, 2.4F, bay - 0.3F, 0.25F, 28.1F, enclosure)
                       End If
                   Next
               End Sub)
        Casing(Sub()
                   For index = 0 To cabinets - 1
                       Dim x = -width / 2 + index * bay
                       If _depthY > 0 Then
                           If nxl Then
                               For row = 0 To 4
                                   Box(x + 0.8F, 6.3F, 4 + row * 4, bay - 1.6F, 0.1F, 2.4F, grille)
                               Next
                           Else
                               Box(x + 0.7F, 6.3F, 3.5F, bay - 1.4F, 0.1F, 26, grille)
                           End If
                       End If
                   Next
               End Sub)
        For index = 0 To cabinets - 1
            Dim x = -width / 2 + index * bay
            If _depthY > 0 Then
                Line(x + 0.1F, 6.3F, 2.4F, x + 0.1F, 6.3F, 30.5F, seam)
                If nxl Then
                    For row = 0 To 4
                        Dim z = 4 + row * 4
                        For slot = 0 To 12
                            Dim ventX = x + 1 + slot * (bay - 2) / 12
                            Line(ventX, 6.45F, z + 0.3F, ventX, 6.45F, z + 2.1F, seam)
                        Next
                    Next
                    Line(x + bay - 0.5F, 6.5F, 12, x + bay - 0.5F, 6.5F, 18, seam)
                    If index = 0 OrElse index = 2 Then Box(x + 0.8F, 6.4F, 28, 1.2F, 0.15F, 1.2F, Steel)
                Else
                    For row = 0 To 23
                        Dim z = 4 + row
                        For diagonal = 0 To 7
                            Dim ventX = x + 1 + diagonal
                            Line(ventX, 6.45F, z, ventX + 0.45F, 6.45F, z + 0.65F, Color.FromArgb(56, 59, 61))
                        Next
                    Next
                    Dim handleX = If(index = 0, x + bay - 0.8F, x + 0.6F)
                    Box(handleX, 6.6F, 13, 0.3F, 0.5F, 4, Color.FromArgb(17, 18, 19))
                End If
                If index = If(nxl, 1, 0) Then
                    Dim screenX = x + If(nxl, 1.2F, 1.1F)
                    Box(screenX - 0.4F, 6.5F, 23.2F, 5.3F, 0.3F, 3.8F, Color.FromArgb(12, 14, 16))
                    Box(screenX, 6.85F, 23.8F, 3.8F, 0.12F, 2.5F, If(nxl, Color.FromArgb(45, 54, 91), Color.FromArgb(133, 154, 145)))
                    Box(screenX - 0.2F, 7, 23.8F, 0.15F, 0.1F, 2.5F, Color.YellowGreen)
                    If Not nxl Then
                        For row = 0 To 2
                            Line(screenX + 0.5F, 7.05F, 24.3F + row * 0.5F, screenX + 2.5F, 7.05F, 24.3F + row * 0.5F, Color.LightGray)
                        Next
                    End If
                    Box(screenX + 4.1F, 7, 24, 0.35F, 0.1F, 0.5F, Color.Firebrick)
                End If
                For slot = 0 To CInt(bay)
                    Line(x + slot * 0.9F, 6.55F, 0.4F, x + slot * 0.9F, 6.55F, 1.7F, seam)
                Next
            ElseIf _depthY < 0 Then
                Vents(x + 1, -6.3F, 5, bay - 2, 17)
            End If
        Next
        If Not nxl Then Box(-width / 2 + 0.8F, -4.8F, 31, width - 1.6F, 7.5F, 0.6F, Color.FromArgb(132, 135, 137))
        For index = 0 To cabinets - 1
            Dim x = -width / 2 + index * bay
            Dim roof = If(nxl, 31.0F, 31.6F)
            Line(x + 0.5F, -4.5F, roof, x + bay - 0.5F, -4.5F, roof, seam)
            Line(x + 0.5F, 2.5F, roof, x + bay - 0.5F, 2.5F, roof, seam)
            Line(x + 0.5F, -4.5F, roof, x + 0.5F, 2.5F, roof, seam)
            Line(x + bay - 0.5F, -4.5F, roof, x + bay - 0.5F, 2.5F, roof, seam)
        Next
        FaceText(If(nxl, "NXL 800", "EXL 500"), -width / 2 + 1, 6.8F, 29.5F, Steel)
    End Sub

    Private Sub HansaChiller(withPanel As Boolean)
        Dim blue = Body(Color.FromArgb(35, 101, 173))
        Dim insulation = Color.FromArgb(49, 56, 61)
        Box(-29, -18, 0, 58, 36, 2, Dark)
        For Each x In {-22.0F, 17.0F}
            Box(x, -12, 2, 5, 23, 4, blue)
            Box(x - 1, -13, 2, 7, 25, 1, blue)
        Next
        HorizontalTank(-25, 0, 12, 48, 8, blue)
        HorizontalTank(-26, 0, 12, 1.5F, 8.5F, blue)
        HorizontalTank(23, 0, 12, 2, 8.5F, blue)
        For Each y In {-4.0F, 4.0F}
            HorizontalTank(25, y, 12, 4, 2.4F, blue)
            HorizontalTank(29, y, 12, 1, 3, Steel)
        Next
        For Each x In {-19.0F, 14.0F}
            Box(x, -5, 19, 3, 10, 4, blue)
        Next
        HorizontalTank(-25, -2, 28, 44, 6, insulation)
        HorizontalTank(-26, -2, 28, 1.5F, 6.5F, Dark)
        HorizontalTank(19, -2, 28, 2, 6.5F, Dark)
        HorizontalTank(21, -2, 28, 8, 3.5F, insulation)
        Cylinder(25, -2, 28, 3.5F, 14, insulation)
        HorizontalTank(-6, -2, 42, 31, 3.5F, insulation)
        Cylinder(-6, -2, 34, 3.5F, 8, insulation)
        HorizontalTank(-26, -2, 28, 3, 2, blue)
        For Each x In {-16.0F, 3.0F}
            HorizontalTank(x, -9, 21, 14, 4, blue)
            Box(x + 2, -13, 19, 7, 8, 3, blue)
            Cylinder(x + 6, -9, 25, 1.5F, 3, Steel)
        Next
        HansaPumps()
        If withPanel Then HansaPanel()
    End Sub

    Private Sub HansaPumps()
        Dim blue = Body(Color.FromArgb(35, 101, 173))
        For Each x In {-14.0F, 12.0F}
            Component(x, 15, 0.48F, Sub(renderer) renderer.Pump(0))
            HorizontalTank(x - 10, 15, 3.5F, 4, 1.2F, blue)
            Cylinder(x - 6, 15, 6, 1, 8, Steel)
            HorizontalTank(x - 6, 11, 14, 4, 1.4F, blue)
        Next
        HorizontalTank(-24, 19, 4, 48, 1.2F, blue)
    End Sub

    Private Sub HansaPanel()
        Dim white = Color.FromArgb(239, 242, 244)
        For Each x In {31.0F, 43.0F}
            Box(x, 8, 0, 2, 5, 9, Steel)
        Next
        Box(29, 6, 9, 17, 8, 29, white)
        If _depthY > 0 Then
            Line(38, 14.2F, 10, 38, 14.2F, 37, Dark)
            Box(31, 14.3F, 29, 6, 0.5F, 5, Dark)
            Box(32, 14.9F, 30, 4, 0.2F, 3, Color.LightSkyBlue)
            For index = 0 To 2
                Box(31, 14.5F, 25 - index * 3, 1.2F, 0.5F, 1.2F, Color.SeaGreen)
                Box(34, 14.5F, 25 - index * 3, 1.2F, 0.5F, 1.2F, Color.RoyalBlue)
            Next
            Box(32, 14.5F, 14, 3, 0.5F, 3, Color.Gold)
            Box(33, 15.1F, 15, 1.5F, 0.4F, 1.5F, Color.Firebrick)
            Line(43, 14.4F, 17, 43, 14.4F, 23, Dark)
        End If
        For Each x In {32.0F, 36.0F, 41.0F}
            Line(x, 9, 9, x, 9, 3, Dark)
            Line(x, 9, 3, 22, 9, 3, Dark)
        Next
    End Sub

    Private Sub Chiller(Optional screw As Boolean = False)
        Casing(Sub()
                   Box(-24, -10, 0, 48, 20, 2, Dark)
                   Box(-23, -9, 2, 46, 18, 15, Body(Color.FromArgb(190, 201, 202)))
               End Sub)
        For Each side In {-1.0F, 1.0F}
            If side * _depthY <= 0 Then Continue For
            For index = 0 To 3
                Dim x = -21 + index * 11
                Box(x, side * 9.2F, 3, 10, 0.5F, 12, Dark)
                Vents(x + 0.5F, side * 9.8F, 4, 9, 8)
            Next
        Next
        For index = 0 To 3
            Dim x = -17 + index * 11
            Cylinder(x, 0, 17, 4.8F, 1, Body(Steel))
            Fan(x, 0, 18, 4.4F)
        Next
        If _depthY > 0 Then
            Box(16, 9.8F, 8, 6, 0.8F, 7, Body(Steel))
            Box(17, 10.7F, 11, 4, 0.3F, 3, Color.LightSkyBlue)
        End If
        If _depthX > 0 Then
            For Each y In {-4.0F, 4.0F}
                Line(23, y, 7, 30, y, 7, If(y < 0, Color.Firebrick, Color.RoyalBlue))
                Cylinder(27, y, 6, 1.6F, 2, Steel)
            Next
        End If
        FaceText(If(screw, "SCREW CHILLER", "CHILLER"), -9, 10, 15, Dark)
    End Sub

    Private Sub BatteryRack()
        For Each x In {-13.0F, 13.0F}
            For Each y In {-4.0F, 4.0F}
                Box(x, y, 0, 0.8F, 0.8F, 28, Body(Steel))
            Next
        Next
        For level = 0 To 3
            Dim z = 1 + level * 7.0F
            Box(-13, -4, z, 27, 9, 0.8F, Body(Steel))
            For battery = 0 To 5
                Dim x = -11.5F + battery * 4
                Box(x, -3, z + 1, 3, 6, 4.5F, Dark)
                Box(x + 0.3F, -1.5F, z + 5.5F, 0.7F, 0.9F, 0.6F, Color.Firebrick)
                Box(x + 2, -1.5F, z + 5.5F, 0.7F, 0.9F, 0.6F, Color.RoyalBlue)
                Box(x + 0.4F, 3.1F, z + 2, 2, 0.1F, 2, Color.FromArgb(230, 194, 75))
            Next
        Next
    End Sub

    Private Sub ProductionLine(length As Single, filler As Boolean)
        Conveyor(-length / 2, length)
        If Not filler Then Return
        If length >= 54 Then
            For Each side In {-1.0F, 1.0F}
                Dim x = side * length / 3 - 5
                Box(x, -5, 0, 10, 10, 2, Dark)
                Casing(Sub()
                           Box(x, -4.8F, 3, 10, 9.6F, 5, Body(Steel))
                           For Each endX In {x, x + 9.3F}
                               Box(endX, -4.8F, 8, 0.7F, 9.6F, 12, Steel)
                           Next
                           Box(x, -4.8F, 19.5F, 10, 9.6F, 0.5F, Body(Color.FromArgb(221, 228, 230)))
                       End Sub)
                Box(x + 1, -3.2F, 9, 8, 6.4F, 0.5F, Body(Color.FromArgb(77, 134, 148)))
                Line(x + 0.6F, -4.9F, 18, x + 9.4F, -4.9F, 18, Yellow)
                Line(x + 0.6F, 4.9F, 18, x + 9.4F, 4.9F, 18, Yellow)
                Box(x + 2, 4.9F, 12, 4, 0.3F, 4, Dark)
                Box(x + 2.4F, 5.2F, 12.5F, 3.2F, 0.1F, 3, Color.FromArgb(74, 160, 185))
            Next
        End If
        Cylinder(0, 0, 0, 11, 3, Body(Steel))
        Cylinder(0, 0, 3, 10, 7, Body(Color.FromArgb(111, 150, 164)))
        For index = 0 To 7
            Dim angle = index * Math.PI / 4
            Dim x = CSng(Math.Cos(angle) * 8)
            Dim y = CSng(Math.Sin(angle) * 8)
            Bottle(x, y, 10)
            Line(x, y, 15, x, y, 20, Steel)
        Next
        For Each x In {-10.0F, 10.0F}
            Box(x, -5, 3, 1, 1, 20, Steel)
        Next
        Cylinder(0, 0, 21, 11, 2, Body(Steel))
        Cylinder(0, 0, 23, 8.5F, 0.15F, Body(Color.FromArgb(216, 225, 228)))
        For Each x In {-6.0F, 6.0F}
            Line(x, -5, 23.2F, x, 5, 23.2F, Shade(Steel, 0.65F))
        Next
        Cylinder(0, 0, 23, 3, 3, Dark)
    End Sub

    Private Sub Conveyor(start As Single, length As Single)
        For offset = 3 To CInt(length) - 2 Step 15
            Box(start + offset, -2.5F, 0, 1, 1, 6, Steel)
            Box(start + offset, 2, 0, 1, 1, 6, Steel)
        Next
        Box(start, -3, 6, length, 6, 1.5F, Body(Color.FromArgb(48, 100, 112)))
        For offset = 1 To CInt(length) - 1 Step 4
            Line(start + offset, -2.8F, 7.6F, start + offset, 2.8F, 7.6F, Steel)
        Next
        Line(start, -3, 10, start + length, -3, 10, Steel)
        Line(start, 3, 10, start + length, 3, 10, Steel)
        For offset = 5 To CInt(length) - 3 Step 9
            Bottle(start + offset, 0, 7.8F)
        Next
    End Sub

    Private Sub Bottle(x As Single, y As Single, z As Single)
        Cylinder(x, y, z, 0.9F, 3, Color.FromArgb(105, 193, 212))
        Cylinder(x, y, z + 3, 0.4F, 1.1F, Color.FromArgb(105, 193, 212))
        Cylinder(x, y, z + 4.1F, 0.5F, 0.4F, Color.RoyalBlue)
    End Sub

    Private Sub CipStation(count As Integer)
        Dim stainless = Body(Color.FromArgb(188, 199, 205))
        Dim width = count * 20.0F + 10
        Box(-width / 2, -11, 0, width, 22, 1.2F, Dark)
        For index = 0 To count - 1
            Dim x = (index - (count - 1) / 2.0F) * 20 - 3
            For Each dx In {-5.0F, 5.0F}
                For Each y In {-4.0F, 4.0F}
                    Cylinder(x + dx, y, 1.2F, 0.6F, 5.8F, Steel)
                    Cylinder(x + dx, y, 1.2F, 1.1F, 0.5F, Dark)
                Next
            Next
            Cylinder(x, 0, 7, 7.7F, 23, stainless)
            Cylinder(x, 0, 30, 7.3F, 1, Color.Silver)
            Cylinder(x, 0, 31, 2.2F, 0.8F, Steel)
            Cylinder(x, 0, 32, 0.5F, 4, Steel)
            Line(x, 0, 36, x, -7, 36, Steel)
            Line(x, -7, 36, x, -7, 4, Steel)
            For Each z In {8.0F, 28.5F}
                Cylinder(x, 0, z, 7.85F, 0.25F, Steel)
            Next
            Line(x, 0, 7, x, 9, 7, Steel)
            Box(x - 1, 7.8F, 6, 2, 2, 2, Color.SteelBlue)
        Next
        For Each z In {4.0F, 6.0F}
            Line(-width / 2 + 1, 9, z, width / 2 - 1, 9, z, Steel)
        Next
        Dim endX = width / 2 - 4
        HorizontalTank(endX - 4, 7, 3, 6, 1.5F, Color.SteelBlue)
        Box(endX - 2, -6, 2, 5, 5, 9, stainless)
        Box(endX - 1, -0.8F, 7, 3, 0.3F, 2, Color.LightSkyBlue)
        For Each x In {-width / 2 + 2, width / 2 - 2}
            Box(x, 8, 1, 0.7F, 0.7F, 6, Steel)
        Next
        Dim ladderX = -width / 2 + 1.5F
        For Each dx In {0.0F, 3.0F}
            Line(ladderX + dx, 7, 2, ladderX + dx, 7, 31, Steel)
        Next
        For z As Single = 3 To 30 Step 3
            Line(ladderX, 7, z, ladderX + 3, 7, z, Steel)
        Next
    End Sub

    Private Sub ReferenceProcessSkid(water As Boolean)
        Dim stainless = Body(Color.FromArgb(188, 199, 205))
        Box(-25, -12, 0, 50, 24, 1.5F, Dark)
        For Each x In {-24.0F, 22.0F}
            For Each y In {-10.0F, 9.0F}
                Box(x, y, 1.5F, 1.2F, 1.2F, 31, Steel)
            Next
            Box(x, -10, 32, 1.2F, 20, 1.2F, Steel)
        Next
        For Each z In {5.0F, 30.0F}
            Line(-24, -10, z, 23, -10, z, Steel)
            Line(-24, 10, z, 23, 10, z, Steel)
        Next
        For Each x In {-17.0F, 15.0F}
            Dim base = If(water AndAlso x > 0, 11.0F, 4.0F)
            Dim height = If(water, If(x > 0, 22.0F, 19.0F), 20.0F)
            Cylinder(x, -2, base, If(water, 5.5F, 4.3F), height, stainless)
            Cylinder(x, -2, base + height, 2, 1, Steel)
            Line(x, -2, base + height + 1, x, -2, 37, Steel)
            Line(x, -2, 37, x, 7, 37, Steel)
            Line(x, 7, 37, x, 7, 6, Steel)
            For Each y In {-5.0F, 1.0F}
                Box(x - 3, y, 1.5F, 0.8F, 0.8F, base - 1, Steel)
            Next
        Next
        If water Then
            For level = 0 To 4
                HorizontalTank(-10, -5, 7 + level * 4.5F, 22, 1.5F, Color.WhiteSmoke)
            Next
        Else
            Cylinder(-7, 2, 7, 1.5F, 20, Steel)
            Cylinder(-7, 2, 27, 2, 4, Color.SteelBlue)
            Line(-17, 0, 15, -7, 2, 15, Steel)
            Line(-7, 2, 15, 15, 0, 15, Steel)
        End If
        For Each x In {-15.0F, 10.0F}
            HorizontalTank(x - 4, 7, 4, 7, 2, Color.WhiteSmoke)
            HorizontalTank(x + 3, 7, 4, 3, 1.7F, Color.SteelBlue)
            Line(x, 7, 6, x, -2, 6, Steel)
            Box(x - 1, 4, 6, 2, 2, 2, Color.SteelBlue)
        Next
        Casing(Sub()
                   Box(If(water, -6, 1), 0, 1.5F, If(water, 12, 21), 9, If(water, 21, 29), stainless)
               End Sub)
        Dim cabinetX = If(water, -6, 1)
        Box(cabinetX, 9.1F, 2, 2, 0.4F, If(water, 20, 28), Color.FromArgb(27, 79, 126))
        Box(cabinetX + 3, 9.2F, 15, 7, 0.4F, 5, Dark)
        Box(cabinetX + 3.5F, 9.7F, 15.5F, 6, 0.2F, 4, Color.LightSkyBlue)
        For index = 0 To 2
            Cylinder(cabinetX + 3 + index * 2, 9, 22 + If(water, 0, 9), 0.6F, 1, If(index = 1, Color.Goldenrod, Color.SeaGreen))
        Next
        For Each x In {-21.0F, 19.0F}
            For z As Single = 10 To 25 Step 7
                Box(x, 9.4F, z, 2, 0.6F, 2, Color.WhiteSmoke)
            Next
        Next
    End Sub

    Private Sub Transformer()
        Box(-11, -8, 0, 22, 16, 2, Dark)
        Dim tank = Body(Color.FromArgb(126, 145, 132))
        Box(-8, -5, 2, 16, 10, 12.5F, tank)
        For Each side In {-1, 1}
            Dim y = If(side < 0, -7.5F, 5.2F)
            Box(-8.7F, y, 4, 17.4F, 2.3F, 8.5F, Shade(tank, 0.65F))
            For index = 0 To 11
                Box(-8.5F + index * 1.5F, y, 3.5F, 0.5F, 2.3F, 10, Steel)
            Next
            Box(-8.7F, y, 13.5F, 17.4F, 2.3F, 0.5F, tank)
        Next
        Box(-8.7F, -5.7F, 14.5F, 17.4F, 11.4F, 1, Steel)
        Box(-7.8F, -4.8F, 15.5F, 15.6F, 9.6F, 0.3F, tank)
        HorizontalTank(-7, 3.3F, 17.2F, 14, 1.4F, Steel)
        Box(-5.5F, 2.6F, 15.8F, 1, 1.4F, 1, Dark)
        Box(4.5F, 2.6F, 15.8F, 1, 1.4F, 1, Dark)
        For index = 0 To 2
            Dim x = -5 + index * 5
            Dim porcelain = Color.FromArgb(141, 91, 62)
            Cylinder(x, -2.3F, 15.8F, 1.15F, 4.5F, porcelain)
            For ring = 0 To 3
                Cylinder(x, -2.3F, 16.4F + ring * 0.9F, 1.65F, 0.3F, porcelain)
            Next
            Cylinder(x, -2.3F, 20.3F, 0.55F, 0.7F, Steel)
            Cylinder(x, 0.9F, 15.8F, 0.85F, 1.8F, porcelain)
            Cylinder(x, 0.9F, 17.1F, 1.2F, 0.3F, porcelain)
            Cylinder(x, 0.9F, 17.6F, 0.4F, 0.6F, Steel)
        Next
        Box(7.5F, -3.2F, 4, 2.4F, 4.5F, 6, Dark)
        Box(7.6F, -3, 10, 2.2F, 4.1F, 0.3F, Steel)
        Box(-2, 7.6F, 7, 4, 1.4F, 3, Dark)
        Face({V(-1.3F, 9, 7.5F), V(1.3F, 9, 7.5F), V(0, 9, 9.6F)}, Yellow)
        For Each x In {-7.8F, 7.8F}
            For Each y In {-4.8F, 4.8F}
                Cylinder(x, y, 15.5F, 0.35F, 0.5F, Dark)
            Next
        Next
    End Sub

    Private Sub Vents(x As Single, y As Single, z As Single, width As Single, rows As Integer)
        For index = 0 To rows - 1
            Line(x, y, z + index * 1.2F, x + width, y, z + index * 1.2F, Dark)
        Next
    End Sub

    Private Sub FrontFan(x As Single, y As Single, z As Single, radius As Single)
        Dim disk As New List(Of Vertex)
        For index = 0 To 23
            Dim angle = index * Math.PI / 12
            disk.Add(V(x + CSng(Math.Cos(angle) * radius), y, z + CSng(Math.Sin(angle) * radius)))
        Next
        For index = 0 To disk.Count - 1
            Face({V(x, y, z), disk(index), disk((index + 1) Mod disk.Count)}, Dark, False)
        Next
        For ring = 1 To 3
            For index = 0 To 23
                Dim a = index * Math.PI / 12
                Dim b = (index + 1) * Math.PI / 12
                Line(x + CSng(Math.Cos(a) * radius * ring / 3), y + 0.2F, z + CSng(Math.Sin(a) * radius * ring / 3),
                     x + CSng(Math.Cos(b) * radius * ring / 3), y + 0.2F, z + CSng(Math.Sin(b) * radius * ring / 3), Steel)
            Next
        Next
        For index = 0 To 3
            Dim angle = index * Math.PI / 2
            Line(x, y + 0.3F, z, x + CSng(Math.Cos(angle) * radius), y + 0.3F, z + CSng(Math.Sin(angle) * radius), Steel)
        Next
    End Sub

    Private Sub Casing(build As Action)
        Dim start = _commands.Count
        build()
        Dim faces = _commands.Skip(start).OrderBy(Function(item) item.Depth).ToArray()
        _commands.RemoveRange(start, _commands.Count - start)
        ' Paint the casing before its external details; model methods omit rear-facing mounted panels.
        _commands.Add((Single.MinValue, Sub()
                                           For Each faceCommand In faces
                                               faceCommand.Paint()
                                           Next
                                       End Sub))
    End Sub

    Private Sub Box(x As Single, y As Single, z As Single, width As Single, depth As Single, height As Single, color As Color)
        Dim bottom = {V(x, y, z), V(x + width, y, z), V(x + width, y + depth, z), V(x, y + depth, z)}
        Dim top = {V(x, y, z + height), V(x + width, y, z + height),
                   V(x + width, y + depth, z + height), V(x, y + depth, z + height)}
        For index = 0 To 3
            Dim nextIndex = (index + 1) Mod 4
            Face({bottom(index), bottom(nextIndex), top(nextIndex), top(index)}, Shade(color, If(index Mod 2 = 0, 0.72F, 0.86F)))
        Next
        Face(top, color)
    End Sub

    Private Sub BeveledBox(x As Single, y As Single, z As Single, width As Single, depth As Single, height As Single,
                          bevel As Single, color As Color)
        If bevel <= 0 OrElse bevel * 2 >= Math.Min(width, depth) OrElse height <= 0 Then
            Throw New ArgumentException("A beveled enclosure needs positive dimensions and a bevel smaller than half its footprint.")
        End If
        Dim cornerX = {x + bevel, x + width - bevel, x + width, x + width,
                       x + width - bevel, x + bevel, x, x}
        Dim cornerY = {y, y, y + bevel, y + depth - bevel,
                       y + depth, y + depth, y + depth - bevel, y + bevel}
        Dim edgeHeight = Math.Min(bevel, height / 3)
        Dim top As New List(Of Vertex)
        For index = 0 To 7
            Dim nextIndex = (index + 1) Mod 8
            Dim insetX = If(cornerX(index) < x + width / 2, edgeHeight, -edgeHeight)
            Dim insetY = If(cornerY(index) < y + depth / 2, edgeHeight, -edgeHeight)
            Dim nextInsetX = If(cornerX(nextIndex) < x + width / 2, edgeHeight, -edgeHeight)
            Dim nextInsetY = If(cornerY(nextIndex) < y + depth / 2, edgeHeight, -edgeHeight)
            Face({V(cornerX(index), cornerY(index), z), V(cornerX(nextIndex), cornerY(nextIndex), z),
                  V(cornerX(nextIndex), cornerY(nextIndex), z + height - edgeHeight),
                  V(cornerX(index), cornerY(index), z + height - edgeHeight)},
                 Shade(color, If(index Mod 4 = 0, 0.82F, If(index Mod 4 = 2, 0.91F, 0.87F))))
            Face({V(cornerX(index), cornerY(index), z + height - edgeHeight),
                  V(cornerX(nextIndex), cornerY(nextIndex), z + height - edgeHeight),
                  V(cornerX(nextIndex) + nextInsetX, cornerY(nextIndex) + nextInsetY, z + height),
                  V(cornerX(index) + insetX, cornerY(index) + insetY, z + height)}, Shade(color, 0.96F), False)
            top.Add(V(cornerX(index) + insetX, cornerY(index) + insetY, z + height))
        Next
        Face(top.ToArray(), color)
    End Sub

    Private Sub Cylinder(x As Single, y As Single, z As Single, radius As Single, height As Single, color As Color, Optional sides As Integer = 12)
        Dim bottom As New List(Of Vertex)
        Dim top As New List(Of Vertex)
        For index = 0 To sides - 1
            Dim angle = index * Math.PI / (sides / 2.0R)
            Dim px = x + CSng(Math.Cos(angle) * radius)
            Dim py = y + CSng(Math.Sin(angle) * radius)
            bottom.Add(V(px, py, z))
            top.Add(V(px, py, z + height))
        Next
        For index = 0 To sides - 1
            Dim nextIndex = (index + 1) Mod sides
            Dim light = 0.78F + 0.18F * CSng(Math.Cos((index + 0.5F) * Math.PI / (sides / 2.0R) - 2.1R))
            Face({bottom(index), bottom(nextIndex), top(nextIndex), top(index)}, Shade(color, light), False)
        Next
        Face(top.ToArray(), color)
    End Sub

    Private Sub Beam(x1 As Single, y1 As Single, z1 As Single, x2 As Single, y2 As Single, z2 As Single,
                     thickness As Single, color As Color)
        Dim dx = x2 - x1, dy = y2 - y1, dz = z2 - z1
        Dim length = CSng(Math.Sqrt(dx * dx + dy * dy + dz * dz))
        If length <= 0 OrElse thickness <= 0 Then Throw New ArgumentException("A structural beam needs positive length and thickness.")
        Dim horizontal = CSng(Math.Sqrt(dx * dx + dy * dy))
        Dim half = thickness / 2
        Dim ux = If(horizontal > 0.0001F, -dy / horizontal, 1.0F) * half
        Dim uy = If(horizontal > 0.0001F, dx / horizontal, 0.0F) * half
        Dim vx = -dz * uy / length
        Dim vy = dz * ux / length
        Dim vz = (dx * uy - dy * ux) / length
        Dim first = {V(x1 + ux + vx, y1 + uy + vy, z1 + vz),
                     V(x1 - ux + vx, y1 - uy + vy, z1 + vz),
                     V(x1 - ux - vx, y1 - uy - vy, z1 - vz),
                     V(x1 + ux - vx, y1 + uy - vy, z1 - vz)}
        Dim last = {V(x2 + ux + vx, y2 + uy + vy, z2 + vz),
                    V(x2 - ux + vx, y2 - uy + vy, z2 + vz),
                    V(x2 - ux - vx, y2 - uy - vy, z2 - vz),
                    V(x2 + ux - vx, y2 + uy - vy, z2 - vz)}
        Face(first, Shade(color, 0.8F), Not _refinedFinish)
        For index = 0 To 3
            Dim nextIndex = (index + 1) Mod 4
            Face({first(index), first(nextIndex), last(nextIndex), last(index)}, Shade(color, If(index Mod 2 = 0, 0.86F, 0.72F)), Not _refinedFinish)
        Next
        Face(last, color, Not _refinedFinish)
    End Sub

    Private Sub WallCylinder(x As Single, y As Single, z As Single, radius As Single, depth As Single, color As Color)
        Dim back As New List(Of Vertex)
        Dim front As New List(Of Vertex)
        For index = 0 To 11
            Dim angle = index * Math.PI / 6
            Dim px = x + CSng(Math.Cos(angle) * radius)
            Dim pz = z + CSng(Math.Sin(angle) * radius)
            back.Add(V(px, y, pz))
            front.Add(V(px, y + depth, pz))
        Next
        Face(back.ToArray(), Shade(color, 0.8F))
        For index = 0 To 11
            Dim nextIndex = (index + 1) Mod 12
            Face({back(index), front(index), front(nextIndex), back(nextIndex)}, Shade(color, 0.72F + index Mod 3 * 0.08F))
        Next
        Face(front.ToArray(), color)
    End Sub

    Private Sub HorizontalTank(x As Single, y As Single, z As Single, length As Single, radius As Single, color As Color)
        Dim first As New List(Of Vertex)
        Dim last As New List(Of Vertex)
        For index = 0 To 11
            Dim angle = index * Math.PI / 6
            Dim py = y + CSng(Math.Cos(angle) * radius)
            Dim pz = z + CSng(Math.Sin(angle) * radius)
            first.Add(V(x, py, pz))
            last.Add(V(x + length, py, pz))
        Next
        Face(first.ToArray(), Shade(color, 0.8F))
        For index = 0 To 11
            Dim nextIndex = (index + 1) Mod 12
            Dim light = 0.78F + 0.18F * CSng(Math.Cos((index + 0.5F) * Math.PI / 6 - 2.1R))
            Face({first(index), last(index), last(nextIndex), first(nextIndex)}, Shade(color, light), False)
        Next
        Face(last.ToArray(), color)
    End Sub

    Private Function Depth(x As Single, y As Single, z As Single) As Single
        Return x * _depthX + y * _depthY + z * _depthZ
    End Function

    Private Function V(x As Single, y As Single, z As Single) As Vertex
        If _measure IsNot Nothing Then _measure(x, y, z)
        Return New Vertex With {.Point = _project(x, y, z), .Depth = Depth(x, y, z)}
    End Function

    Private Sub Face(vertices As Vertex(), color As Color, Optional outline As Boolean = True)
        If _measure IsNot Nothing AndAlso Not _geometryOnly Then Return
        Dim points(vertices.Length - 1) As PointF
        Dim left = Single.MaxValue, top = Single.MaxValue, right = Single.MinValue, bottom = Single.MinValue
        Dim depth As Double
        For index = 0 To vertices.Length - 1
            Dim point = vertices(index).Point
            points(index) = point
            left = Math.Min(left, point.X) : top = Math.Min(top, point.Y)
            right = Math.Max(right, point.X) : bottom = Math.Max(bottom, point.Y)
            depth += vertices(index).Depth
        Next
        Dim bounds = RectangleF.FromLTRB(left, top, right, bottom)
        If Not _geometryOnly Then _commands.Add((CSng(depth / vertices.Length), Sub()
                                                                          If _context.Clip.HasValue AndAlso Not bounds.IntersectsWith(_context.Clip.Value) Then Return
                                                                          Using pen As New Pen(Shade(color, If(_refinedFinish, 0.82F, 0.65F)),
                                                                                              If(_refinedFinish, 0.22F, 0.28F) / _context.Scale)
                                                                              Dim smoothing = _graphics.SmoothingMode
                                                                              If Not outline AndAlso Not _refinedFinish Then _graphics.SmoothingMode = SmoothingMode.None
                                                                              Using brush As New SolidBrush(color)
                                                                                  _graphics.FillPolygon(brush, points)
                                                                              End Using
                                                                              _graphics.SmoothingMode = smoothing
                                                                              If outline Then _graphics.DrawPolygon(pen, points)
                                                                          End Using
                                                                      End Sub))
        Dim signedArea As Single
        For index = 0 To points.Length - 1
            Dim nextPoint = points((index + 1) Mod points.Length)
            signedArea += points(index).X * nextPoint.Y - nextPoint.X * points(index).Y
        Next
        If signedArea < 0 Then Array.Reverse(points)
        _hitArea.AddPolygon(points)
    End Sub

    Private Sub Line(x1 As Single, y1 As Single, z1 As Single, x2 As Single, y2 As Single, z2 As Single, color As Color)
        If _measure IsNot Nothing Then
            _measure(x1, y1, z1)
            _measure(x2, y2, z2)
            Return
        End If
        If _geometryOnly Then Return
        Dim first = _project(x1, y1, z1), last = _project(x2, y2, z2)
        Dim bounds = RectangleF.FromLTRB(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y),
            Math.Max(first.X, last.X), Math.Max(first.Y, last.Y))
        bounds.Inflate(0.5F, 0.5F)
        _commands.Add(((Depth(x1, y1, z1) + Depth(x2, y2, z2)) / 2 + 0.02F, Sub()
                                                                                 If _context.Clip.HasValue AndAlso Not bounds.IntersectsWith(_context.Clip.Value) Then Return
                                                                                 Using pen As New Pen(color, 0.8F / _context.Scale)
                                                                                     _graphics.DrawLine(pen, first, last)
                                                                                 End Using
                                                                             End Sub))
    End Sub

    Private Sub FaceText(text As String, x As Single, y As Single, z As Single, color As Color)
        If _measure IsNot Nothing OrElse _geometryOnly Then Return
        If _depthY <= 0.001F Then Return
        Dim origin = _project(x, y, z)
        Dim unit = _project(x + 10, y, z)
        Dim projectedWidth = CSng(Math.Sqrt((unit.X - origin.X) ^ 2 + (unit.Y - origin.Y) ^ 2))
        _commands.Add((Depth(x, y, z) + 0.03F, Sub()
                                                 Dim pixelSize = Math.Min(9.0F, projectedWidth * _context.Scale * 0.45F)
                                                 ' Illegible micro-text only adds visual noise over the equipment faces.
                                                 If pixelSize < 5.0F Then Return
                                                 Dim size = pixelSize / _context.Scale
                                                 Using font As New Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel), brush As New SolidBrush(color)
                                                     _graphics.DrawString(text, font, brush, origin)
                                                 End Using
                                             End Sub))
    End Sub

    Private Shared Function Shade(color As Color, factor As Single) As Color
        Return Color.FromArgb(CInt(color.R * factor), CInt(color.G * factor), CInt(color.B * factor))
    End Function
End Class
