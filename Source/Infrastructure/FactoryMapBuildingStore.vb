Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Serialization

Friend NotInheritable Class FactoryBuildingPart
    Public Property Id As String = Guid.NewGuid().ToString("N")
    Public Property Name As String = "New wall"
    Public Property Kind As String = "Wall"
    Public Property Style As String = "Solid"
    Public Property Material As String = "Concrete"
    Public Property BodyColor As String = "#8DAABD"
    Public Property X As Single = 0.5F
    Public Property Y As Single = 0.5F
    Public Property Length As Single = 80
    Public Property Depth As Single = 2
    Public Property HeightMeters As Single = 3
    Public Property BaseMeters As Single
    Public Property Level As String = "Ground"
    Public Property Rotation As Single
    Public Property MirrorHorizontal As Boolean
    Public Property MirrorVertical As Boolean
    Public Property RoadBendDegrees As Single = 90
    Public Property ParentWallId As String = String.Empty
    Public Property Offset As Single
    Public Property IsOpen As Boolean
    Public Property OpeningDirection As String = "Left inward"
    Public Property RoomId As String = String.Empty
    Public Property RoomName As String = String.Empty
    Public Property MonitoringDeviceId As String = String.Empty
    Public Property TemperatureSourceId As String = String.Empty
    Public Property TemperatureSourceConfirmed As Boolean
    Public Property FirePanelId As String = String.Empty
    Public Property FireSiteId As String = String.Empty
    Public Property FireZone As String = String.Empty
    Public Property DrawingPoints As New List(Of PointF)

    <JsonIgnore>
    Public Property Position As PointF
        Get
            Return New PointF(X, Y)
        End Get
        Set(value As PointF)
            X = value.X
            Y = value.Y
        End Set
    End Property

    Public Function Clone() As FactoryBuildingPart
        Dim result = DirectCast(MemberwiseClone(), FactoryBuildingPart)
        result.DrawingPoints = If(DrawingPoints Is Nothing, Nothing, New List(Of PointF)(DrawingPoints))
        Return result
    End Function

    Public Overrides Function ToString() As String
        Return If(RoomName = "", "", RoomName & " / ") & Name & " [" & Kind & "] " & Length.ToString("0.#", CultureInfo.InvariantCulture) & "u / " &
            HeightMeters.ToString("0.##", CultureInfo.InvariantCulture) & "m / " & Rotation.ToString("0", CultureInfo.InvariantCulture) &
            "deg (" & Id.Substring(Math.Max(0, Id.Length - 6)) & ")"
    End Function
End Class

Friend NotInheritable Class FactoryMapBuildingStore
    Public Shared ReadOnly Levels As String() = {"Ground", "Mezzanine", "Upper mezzanine", "Basement"}
    Friend Shared ReadOnly Styles As New Dictionary(Of String, String()) From {
        {"Wall", {"Solid"}}, {"Floor", {"Rectangle", "L-shaped"}},
        {"Door", {"Single leaf", "Double leaf", "Sliding", "Rapid roll-up"}},
        {"Window", {"Fixed", "Casement", "Sliding"}},
        {"Gate", {"Single swing", "Double swing", "Sliding gate", "Rapid roll-up", "Open hole"}},
        {"Stairs", {"Straight flight", "L-turn left", "L-turn right", "U-turn left", "U-turn right"}},
        {"Road", {"Dashed yellow center", "Double yellow center", "Unmarked asphalt", "Pedestrian crossing", "Parking bays",
                  "Curved dashed left", "Curved dashed right"}},
        {"Marking", {"Solid line", "Dashed line", "Walkway", "Keep clear hatching", "Rectangle", "Ellipse", "Freehand"}}
    }
    Friend Shared ReadOnly Materials As New Dictionary(Of String, String()) From {
        {"Wall", {"Concrete", "Brick", "Glass", "Steel", "Wood"}},
        {"Floor", {"Tile", "Concrete", "Asphalt", "Sand", "Gravel", "Epoxy"}},
        {"Door", {"Glass", "Steel", "Wood"}}, {"Window", {"Glass"}},
        {"Gate", {"Steel", "Glass", "Wood"}}, {"Stairs", {"Concrete", "Steel", "Wood"}}, {"Road", {"Asphalt", "Concrete"}},
        {"Marking", {"Epoxy"}}
    }
    Friend Shared ReadOnly Directions As String() = {"Left inward", "Right inward", "Left outward", "Right outward"}
    Private Shared ReadOnly Options As New JsonSerializerOptions With {.WriteIndented = True, .PropertyNameCaseInsensitive = True}

    Private NotInheritable Class BuildingDocument
        Public Property Version As Integer = 2
        Public Property Parts As List(Of FactoryBuildingPart)
    End Class

    Private Sub New()
    End Sub

    Public Shared Function Defaults(Optional dimensions As FactoryMapDimensions = Nothing) As List(Of FactoryBuildingPart)
        Dim result As New List(Of FactoryBuildingPart)
        Dim settings = If(dimensions, FactoryMapDimensions.Defaults())
        For Each wall In FactoryMapWalls.Create()
            Dim dx = (wall.Finish.X - wall.Start.X) * 1365
            Dim dy = (wall.Finish.Y - wall.Start.Y) * 766
            Dim size = settings.Components.Single(Function(item) item.Name = If(wall.IsExternal, "External Wall", "Internal Wall"))
            result.Add(New FactoryBuildingPart With {
                .Id = "WALL-" & result.Count.ToString("000", CultureInfo.InvariantCulture), .Name = wall.Room,
                .Position = New PointF((wall.Start.X + wall.Finish.X) / 2, (wall.Start.Y + wall.Finish.Y) / 2),
                .Length = CSng(Math.Sqrt(dx * dx + dy * dy)), .Rotation = NormalizeAngle(CSng(Math.Atan2(dy, dx) * 180 / Math.PI)),
                .Depth = CSng(size.WidthMeters * 8), .HeightMeters = CSng(size.HeightMeters)
            })
        Next
        For Each room In {
            (Name:="Warehouse", X:=74, Y:=21, W:=348, D:=125),
            (Name:="Syrup upper", X:=212, Y:=147, W:=167, D:=108),
            (Name:="Syrup lower", X:=196, Y:=256, W:=73, D:=138),
            (Name:="Sugar storage", X:=269, Y:=256, W:=101, D:=80),
            (Name:="Storage", X:=370, Y:=256, W:=73, D:=80),
            (Name:="Cold room", X:=269, Y:=336, W:=101, D:=58),
            (Name:="Laboratory", X:=370, Y:=336, W:=54, D:=58),
            (Name:="Generator bank", X:=42, Y:=256, W:=68, D:=422),
            (Name:="Compressor room", X:=111, Y:=256, W:=84, D:=328),
            (Name:="Panel room", X:=199, Y:=413, W:=172, D:=171),
            (Name:="Battery B1", X:=131, Y:=584, W:=53, D:=36),
            (Name:="Battery B2", X:=184, Y:=584, W:=53, D:=36),
            (Name:="Battery B3", X:=131, Y:=620, W:=106, D:=28),
            (Name:="Battery B4", X:=186, Y:=648, W:=51, D:=34),
            (Name:="Store", X:=131, Y:=648, W:=55, D:=34),
            (Name:="Transformer area", X:=237, Y:=584, W:=139, D:=95),
            (Name:="Water treatment upper", X:=371, Y:=413, W:=58, D:=171),
            (Name:="Water treatment lower", X:=376, Y:=584, W:=77, D:=79),
            (Name:="Filtration", X:=477, Y:=584, W:=137, D:=79),
            (Name:="PET 1", X:=480, Y:=487, W:=253, D:=97),
            (Name:="PET 2", X:=480, Y:=390, W:=253, D:=97),
            (Name:="Can line", X:=505, Y:=333, W:=228, D:=57),
            (Name:="PET 3", X:=548, Y:=147, W:=187, D:=110),
            (Name:="PET 4", X:=505, Y:=53, W:=184, D:=94),
            (Name:="PET 5", X:=1070, Y:=248, W:=105, D:=142),
            (Name:="PET 6", X:=1056, Y:=144, W:=218, D:=84),
            (Name:="Offices vertical", X:=816, Y:=351, W:=61, D:=121),
            (Name:="Offices horizontal", X:=920, Y:=386, W:=131, D:=59),
            (Name:="Reception", X:=1288, Y:=322, W:=57, D:=60),
            (Name:="HR", X:=1037, Y:=584, W:=149, D:=54)
        }
            result.Add(New FactoryBuildingPart With {
                .Id = "FLOOR-" & result.Count.ToString("000", CultureInfo.InvariantCulture),
                .Name = room.Name & " floor", .Kind = "Floor", .Style = "Rectangle", .Material = "Concrete",
                .BodyColor = "#CED7DC", .X = CSng((room.X + room.W / 2) / 1365), .Y = CSng((room.Y + room.D / 2) / 766),
                .Length = room.W, .Depth = room.D, .HeightMeters = 0.02F
            })
        Next
        result.AddRange(DefaultRoads())
        Return result
    End Function

    Public Shared Function DefaultRoads() As List(Of FactoryBuildingPart)
        Dim result As New List(Of FactoryBuildingPart)
        Dim routes As PointF()() = {
            New PointF() {New PointF(10, 695), New PointF(65, 725), New PointF(1092, 725), New PointF(1350, 555)},
            New PointF() {New PointF(690, 10), New PointF(920, 95), New PointF(1272, 48), New PointF(1350, 70)}
        }
        For Each route In routes
            For index = 1 To route.Length - 1
                Dim a = route(index - 1)
                Dim b = route(index)
                Dim dx = b.X - a.X
                Dim dy = b.Y - a.Y
                result.Add(New FactoryBuildingPart With {
                    .Id = "ROAD-" & result.Count.ToString("00", CultureInfo.InvariantCulture), .Name = "Factory driveway " & (result.Count + 1),
                    .Kind = "Road", .Style = "Dashed yellow center", .Material = "Asphalt", .BodyColor = "#454C53",
                    .X = (a.X + b.X) / 2 / 1365, .Y = (a.Y + b.Y) / 2 / 766,
                    .Length = CSng(Math.Sqrt(dx * dx + dy * dy)) + 8, .Depth = 38, .HeightMeters = 0.02F,
                    .Rotation = NormalizeAngle(CSng(Math.Atan2(dy, dx) * 180 / Math.PI))
                })
            Next
        Next
        Return result
    End Function

    Public Shared Function NormalizeAngle(angle As Single) As Single
        Return (angle Mod 360 + 360) Mod 360
    End Function

    Public Shared Function MaterialColor(material As String) As String
        Select Case material
            Case "Tile" : Return "#D9DED8"
            Case "Concrete" : Return "#BCC6CA"
            Case "Asphalt" : Return "#454C53"
            Case "Sand" : Return "#D8C08A"
            Case "Gravel" : Return "#96958A"
            Case "Epoxy" : Return "#4DAA9D"
            Case "Brick" : Return "#B88269"
            Case "Glass" : Return "#9DD6E6"
            Case "Steel" : Return "#8FA6B3"
            Case "Wood" : Return "#B58B5B"
            Case Else : Throw New ArgumentException("Unsupported building material.", NameOf(material))
        End Select
    End Function

    Public Shared Function Resolve(part As FactoryBuildingPart, parts As IEnumerable(Of FactoryBuildingPart)) As FactoryBuildingPart
        If String.IsNullOrEmpty(part.ParentWallId) Then Return part
        Dim wall = parts.Single(Function(item) item.Id = part.ParentWallId AndAlso item.Kind = "Wall")
        Dim resolved = part.Clone()
        Dim point = WorldPoint(wall, part.Offset, 0)
        resolved.X = point.X / 1365
        resolved.Y = point.Y / 766
        resolved.Rotation = wall.Rotation
        resolved.Depth = wall.Depth
        resolved.BaseMeters = wall.BaseMeters + part.BaseMeters
        resolved.MirrorHorizontal = part.MirrorHorizontal Xor wall.MirrorHorizontal
        resolved.MirrorVertical = part.MirrorVertical Xor wall.MirrorVertical
        Return resolved
    End Function

    Public Shared Function WorldPoint(part As FactoryBuildingPart, x As Single, y As Single) As PointF
        If part.MirrorHorizontal Then x = -x
        If part.MirrorVertical Then y = -y
        Dim angle = part.Rotation * Math.PI / 180
        Return New PointF(part.X * 1365 + CSng(x * Math.Cos(angle) - y * Math.Sin(angle)),
                          part.Y * 766 + CSng(x * Math.Sin(angle) + y * Math.Cos(angle)))
    End Function

    Public Shared Function LocalPoint(part As FactoryBuildingPart, point As PointF) As PointF
        Dim angle = part.Rotation * Math.PI / 180
        Dim x = point.X - part.X * 1365, y = point.Y - part.Y * 766
        Return New PointF(CSng(x * Math.Cos(angle) + y * Math.Sin(angle)) * If(part.MirrorHorizontal, -1, 1),
            CSng(-x * Math.Sin(angle) + y * Math.Cos(angle)) * If(part.MirrorVertical, -1, 1))
    End Function

    Public Shared Sub Validate(parts As IEnumerable(Of FactoryBuildingPart))
        Dim list = parts.ToList()
        Dim ids As New HashSet(Of String)(StringComparer.Ordinal)
        For Each part In list
            If part Is Nothing OrElse String.IsNullOrWhiteSpace(part.Id) OrElse Not ids.Add(part.Id) Then Throw New InvalidDataException("Building parts must have unique nonempty IDs.")
            If Not Levels.Contains(part.Level) Then Throw New InvalidDataException("Select a supported building level.")
            If String.IsNullOrWhiteSpace(part.Name) OrElse part.Name.Length > 120 Then Throw New InvalidDataException("Enter a building-part name of 1-120 characters.")
            If part.RoomId Is Nothing OrElse part.RoomName Is Nothing OrElse part.RoomName.Length > 120 OrElse
                ((part.RoomId = "") <> (part.RoomName = "")) Then Throw New InvalidDataException("Room membership needs both an ID and a name, or neither.")
            If part.Kind Is Nothing OrElse Not Styles.ContainsKey(part.Kind) OrElse Not Styles(part.Kind).Contains(part.Style) OrElse
                Not Materials(part.Kind).Contains(part.Material) Then Throw New InvalidDataException("Choose a supported building type, style and material.")
            If Not ValidColor(part.BodyColor) OrElse Not Directions.Contains(part.OpeningDirection) Then Throw New InvalidDataException("Choose a valid building color and opening direction.")
            If part.DrawingPoints Is Nothing OrElse part.DrawingPoints.Count > 4096 OrElse
                part.DrawingPoints.Any(Function(point) Not Single.IsFinite(point.X) OrElse Not Single.IsFinite(point.Y) OrElse
                    Math.Abs(point.X) > 0.5F OrElse Math.Abs(point.Y) > 0.5F) Then Throw New InvalidDataException("Drawing points must be finite normalized coordinates (maximum 4096).")
            If part.Kind = "Marking" AndAlso part.Style = "Freehand" AndAlso part.DrawingPoints.Count < 2 Then Throw New InvalidDataException("A pen stroke needs at least two points.")
            If {part.X, part.Y, part.Length, part.Depth, part.HeightMeters, part.BaseMeters, part.Rotation, part.Offset}.Any(Function(value) Not Single.IsFinite(value)) Then Throw New InvalidDataException("Building dimensions must be finite.")
            If part.X < 0 OrElse part.X > 4 OrElse part.Y < 0 OrElse part.Y > 4 OrElse part.Length < 2 OrElse part.Length > 5460 OrElse
                part.Depth < 0.2F OrElse part.Depth > 800 OrElse part.HeightMeters < 0.01F OrElse part.HeightMeters > 100 OrElse
                part.BaseMeters < 0 OrElse part.BaseMeters > 100 OrElse part.Rotation < 0 OrElse part.Rotation >= 360 Then Throw New InvalidDataException("Building position or size is out of range.")
            If Not Single.IsFinite(part.RoadBendDegrees) OrElse part.RoadBendDegrees < 5 OrElse part.RoadBendDegrees > 170 Then
                Throw New InvalidDataException("Road bend angle must be 5-170 degrees.")
            End If
            If FactoryMapRoadGeometry.IsCurve(part) AndAlso part.Length <= part.Depth / 2 + 1 Then
                Throw New InvalidDataException("Road bend radius (Length) must exceed half its road width (Depth) by at least 1 plan unit.")
            End If
            If part.ParentWallId Is Nothing Then Throw New InvalidDataException("The parent wall ID must be empty or a valid wall ID.")
            For Each link In {part.MonitoringDeviceId, part.TemperatureSourceId, part.FirePanelId, part.FireSiteId, part.FireZone}
                If link Is Nothing OrElse link.Length > 120 OrElse link.Any(Function(c) Char.IsControl(c)) Then Throw New InvalidDataException("Building monitoring links must be empty or valid IDs (max 120 characters).")
            Next
            If (part.TemperatureSourceId <> "" AndAlso (Not part.TemperatureSourceId.StartsWith("PLC:", StringComparison.Ordinal) OrElse part.TemperatureSourceId.Length <= 4)) OrElse
                (part.TemperatureSourceConfirmed AndAlso part.TemperatureSourceId = "") Then Throw New InvalidDataException("A commissioned room temperature requires a PLC:<configured meter name> source.")
            If part.FireZone <> "" AndAlso (part.FirePanelId = "" OrElse part.FireSiteId = "") Then Throw New InvalidDataException("Room fire zones require both panel and site identity.")
            If part.MonitoringDeviceId <> "" AndAlso Not {"Door", "Gate"}.Contains(part.Kind) Then Throw New InvalidDataException("Access points can only link to doors or gates.")
            If part.ParentWallId <> "" Then
                Dim wall = list.SingleOrDefault(Function(item) item IsNot Nothing AndAlso item.Id = part.ParentWallId AndAlso item.Kind = "Wall")
                If Not {"Door", "Window", "Gate"}.Contains(part.Kind) OrElse wall Is Nothing OrElse wall.Id = part.Id Then Throw New InvalidDataException("Doors, windows and gates can only attach to an existing wall.")
                If wall.Level <> part.Level Then Throw New InvalidDataException("An opening must be on the same level as its parent wall.")
                If Math.Abs(part.Offset) + part.Length / 2 > wall.Length / 2 + 0.01F OrElse part.BaseMeters + part.HeightMeters > wall.HeightMeters + 0.001F Then
                    Throw New InvalidDataException("The opening must fit within its wall's length and height. Resize or detach it first.")
                End If
                Dim center = WorldPoint(wall, part.Offset, 0)
                If center.X < 0 OrElse center.X > 5460 OrElse center.Y < 0 OrElse center.Y > 3064 Then Throw New InvalidDataException("An attached opening's center must remain inside the supported site extent.")
            End If
        Next
        For Each room In list.Where(Function(part) part.RoomId <> "").GroupBy(Function(part) part.RoomId)
            If room.Select(Function(part) part.Level).Distinct().Count() <> 1 Then Throw New InvalidDataException("Every member of a room must be on the same level.")
            If room.Select(Function(part) part.RoomName).Distinct().Count() <> 1 Then Throw New InvalidDataException("Every member of a room must use the same room name.")
            If room.Select(Function(part) (part.TemperatureSourceId, part.TemperatureSourceConfirmed, part.FirePanelId, part.FireSiteId, part.FireZone)).Distinct().Count() <> 1 Then Throw New InvalidDataException("Every room member must use the same monitoring links. Edit them with Building > Rooms.")
        Next
        For Each wall In list.Where(Function(item) item.Kind = "Wall")
            Dim openings = list.Where(Function(item) item.ParentWallId = wall.Id).OrderBy(Function(item) item.Offset).ToArray()
            For index = 1 To openings.Length - 1
                If openings(index - 1).Offset + openings(index - 1).Length / 2 > openings(index).Offset - openings(index).Length / 2 - 0.1F Then
                    Throw New InvalidDataException("Openings on the same wall must not overlap.")
                End If
            Next
        Next
    End Sub

    Public Shared Function ValidColor(color As String) As Boolean
        Return color IsNot Nothing AndAlso System.Text.RegularExpressions.Regex.IsMatch(color, "\A#[0-9A-Fa-f]{6}\z")
    End Function

    Public Shared Function Load(Optional path As String = Nothing) As List(Of FactoryBuildingPart)
        Dim resolved = StorePath(path)
        If Not File.Exists(resolved) Then Return Defaults(FactoryMapDimensionStore.Load())
        Dim document = JsonSerializer.Deserialize(Of BuildingDocument)(File.ReadAllText(resolved), Options)
        If document Is Nothing OrElse (document.Version <> 1 AndAlso document.Version <> 2) OrElse document.Parts Is Nothing Then Throw New InvalidDataException("Unsupported or empty building layout.")
        Validate(document.Parts)
        If document.Version = 1 AndAlso document.Parts.Count > 0 AndAlso Not document.Parts.Any(Function(part) part.Kind = "Road") Then document.Parts.AddRange(DefaultRoads())
        Return document.Parts
    End Function

    Public Shared Sub Save(parts As IEnumerable(Of FactoryBuildingPart), Optional path As String = Nothing)
        Dim list = parts.ToList()
        Validate(list)
        Dim resolved = StorePath(path)
        Directory.CreateDirectory(IO.Path.GetDirectoryName(resolved))
        Dim temporary = resolved & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Try
            File.WriteAllText(temporary, JsonSerializer.Serialize(New BuildingDocument With {.Parts = list}, Options), New UTF8Encoding(False))
            If File.Exists(resolved) Then File.Copy(resolved, resolved & ".bak", True)
            File.Move(temporary, resolved, True)
        Finally
            If File.Exists(temporary) Then File.Delete(temporary)
        End Try
    End Sub

    Private Shared Function StorePath(path As String) As String
        Return If(String.IsNullOrWhiteSpace(path), IO.Path.Combine(AppContext.BaseDirectory, "data", "factory-map-building.json"), IO.Path.GetFullPath(path))
    End Function
End Class
