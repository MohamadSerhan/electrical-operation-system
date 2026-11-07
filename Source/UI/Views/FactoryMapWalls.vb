Imports System.Drawing
Imports System.Linq

Friend NotInheritable Class FactoryMapWall
    Public ReadOnly Property Room As String
    Public ReadOnly Property Start As PointF
    Public ReadOnly Property Finish As PointF
    Public ReadOnly Property IsExternal As Boolean

    Public Sub New(room As String, start As PointF, finish As PointF, isExternal As Boolean)
        Me.Room = room
        Me.Start = start
        Me.Finish = finish
        Me.IsExternal = isExternal
    End Sub
End Class

Friend NotInheritable Class FactoryMapWalls
    Private Sub New()
    End Sub

    Public Shared Function Create() As IReadOnlyList(Of FactoryMapWall)
        Dim walls As New List(Of FactoryMapWall)
        ' Approximate raster-plan traces in the approved 1365 x 766 image, not surveyed CAD.
        AddPath(walls, "Warehouse", True, 74, 146, 74, 21, 422, 21, 422, 146, 74, 146)
        AddPath(walls, "Factory envelope", True, 422, 53, 505, 53, 505, 257)
        AddPath(walls, "Factory envelope", True, 505, 53, 689, 53, 689, 94, 812, 94, 812, 144, 1054, 144)
        AddPath(walls, "Factory envelope", True, 1297, 123, 1345, 123, 1345, 234, 1297, 234, 1297, 322)
        AddPath(walls, "Reception", True, 1297, 322, 1288, 322, 1288, 412)
        AddPath(walls, "Reception", True, 1288, 322, 1345, 322)
        AddPath(walls, "Reception", True, 1288, 382, 1345, 382)
        AddPath(walls, "Factory envelope", True, 1186, 438, 1290, 438)
        AddPath(walls, "Factory envelope", True, 1186, 438, 1186, 584, 462, 584)
        AddPath(walls, "Factory envelope", True, 1290, 438, 1290, 435)
        AddPath(walls, "Generator bank", True, 42, 256, 42, 678, 110, 678, 110, 256, 42, 256)
        AddPath(walls, "Compressor room", False, 110, 584, 195, 584, 195, 413)
        AddPath(walls, "Syrup upper", False, 212, 146, 212, 255, 195, 255, 195, 265)
        AddPath(walls, "Syrup upper", False, 379, 255, 379, 182, 402, 182)
        AddPath(walls, "Syrup upper", False, 422, 182, 422, 255)
        AddPath(walls, "Syrup lower", False, 195, 285, 195, 394, 269, 394, 269, 256)
        AddPath(walls, "Storage row", False, 195, 255, 422, 255)
        AddPath(walls, "Sugar storage", False, 269, 336, 370, 336, 370, 256)
        AddPath(walls, "Storage", False, 370, 336, 443, 336, 443, 315, 450, 315, 450, 256)
        AddPath(walls, "Cold room", False, 269, 394, 370, 394, 370, 336)
        AddPath(walls, "Laboratory", False, 370, 394, 424, 394, 424, 337)
        AddPath(walls, "Panel room", False, 199, 413, 371, 413, 371, 584, 199, 584, 199, 413)
        AddPath(walls, "Water treatment upper", False, 371, 413, 429, 413, 429, 544)
        AddPath(walls, "Water treatment upper", False, 429, 565, 429, 584, 371, 584)
        AddPath(walls, "Transformer area", False, 237, 584, 376, 584, 376, 679, 237, 679)
        AddPath(walls, "Battery B1/B2", False, 131, 584, 237, 584, 237, 620, 131, 620, 131, 584)
        AddPath(walls, "Battery B1/B2", False, 184, 584, 184, 620)
        AddPath(walls, "Battery B3", False, 131, 620, 131, 648, 237, 648)
        AddPath(walls, "Battery B3", False, 237, 620, 237, 629)
        AddPath(walls, "Battery B4 / Store", False, 131, 648, 131, 682, 237, 682, 237, 648)
        AddPath(walls, "Battery B4 / Store", False, 186, 648, 186, 682)
        AddPath(walls, "Water treatment lower", False, 376, 584, 453, 584, 453, 663, 376, 663)
        AddPath(walls, "Filtration", False, 477, 584, 614, 584, 614, 663, 477, 663, 477, 584)
        AddPath(walls, "PET 1", False, 462, 487, 462, 584)
        AddPath(walls, "PET 1", False, 480, 487, 733, 487, 733, 497)
        AddPath(walls, "PET 1", False, 733, 519, 733, 557)
        AddPath(walls, "PET 1", False, 733, 578, 733, 584)
        AddPath(walls, "PET 2", False, 480, 487, 480, 434)
        AddPath(walls, "PET 2", False, 480, 414, 480, 390, 733, 390, 733, 466)
        AddPath(walls, "Can line", False, 505, 390, 505, 386)
        AddPath(walls, "Can line", False, 505, 364, 505, 333, 733, 333, 733, 369)
        AddPath(walls, "PET 3", False, 548, 257, 735, 257, 735, 147, 550, 147)
        AddPath(walls, "PET 3", False, 527, 147, 527, 176)
        AddPath(walls, "PET 3", False, 548, 176, 548, 231, 596, 231, 596, 257)
        AddPath(walls, "PET 4", False, 527, 107, 646, 107, 646, 146)
        AddPath(walls, "PET 4", False, 594, 107, 594, 146)
        AddPath(walls, "PET 4", False, 689, 94, 689, 123)
        AddPath(walls, "PET 6", False, 1056, 141, 1205, 141)
        AddPath(walls, "PET 6", False, 1056, 173, 1019, 173, 1019, 217, 1056, 217)
        AddPath(walls, "PET 6", False, 1056, 194, 1056, 228, 1274, 228, 1274, 222)
        AddPath(walls, "PET 6", False, 1274, 201, 1274, 161)
        AddPath(walls, "PET 5", False, 1070, 248, 1175, 248, 1175, 390, 1170, 390)
        AddPath(walls, "PET 5", False, 1150, 390, 1070, 390, 1070, 274)
        AddPath(walls, "Offices vertical", False, 816, 351, 877, 351, 877, 472, 816, 472, 816, 351)
        AddPath(walls, "Offices horizontal", False, 920, 386, 1051, 386, 1051, 445, 920, 445, 920, 386)
        AddPath(walls, "HR", False, 1037, 584, 1037, 638, 1186, 638, 1186, 607)
        Return walls.AsReadOnly()
    End Function

    Private Shared Sub AddPath(walls As List(Of FactoryMapWall), room As String, external As Boolean, ParamArray coordinates As Single())
        For index = 0 To coordinates.Length - 4 Step 2
            walls.Add(New FactoryMapWall(room, New PointF(coordinates(index) / 1365.0F, coordinates(index + 1) / 766.0F),
                New PointF(coordinates(index + 2) / 1365.0F, coordinates(index + 3) / 766.0F), external))
        Next
    End Sub

    Public Shared Sub Draw(graphics As Graphics, wall As FactoryMapWall, dimensions As FactoryMapDimensions, opacity As Integer,
                           project As Func(Of Single, Single, Single, PointF), imageSize As Size)
        Dim dimension = dimensions.Components.Single(Function(item) item.Name = If(wall.IsExternal, "External Wall", "Internal Wall"))
        Dim height = CSng(dimension.HeightMeters * 8)
        Dim width = CSng(dimension.WidthMeters * 8)
        Dim x1 = wall.Start.X * imageSize.Width
        Dim y1 = wall.Start.Y * imageSize.Height
        Dim x2 = wall.Finish.X * imageSize.Width
        Dim y2 = wall.Finish.Y * imageSize.Height
        Dim length = CSng(Math.Sqrt((x2 - x1) ^ 2 + (y2 - y1) ^ 2))
        Dim offsetX = -(y2 - y1) / length * width / 2
        Dim offsetY = (x2 - x1) / length * width / 2
        Dim bottom = {project(x1 + offsetX, y1 + offsetY, 0), project(x2 + offsetX, y2 + offsetY, 0),
                      project(x2 - offsetX, y2 - offsetY, 0), project(x1 - offsetX, y1 - offsetY, 0)}
        Dim top = {project(x1 + offsetX, y1 + offsetY, height), project(x2 + offsetX, y2 + offsetY, height),
                   project(x2 - offsetX, y2 - offsetY, height), project(x1 - offsetX, y1 - offsetY, height)}
        Dim alpha = CInt(opacity * 255 / 100)
        Dim tint = If(wall.IsExternal, Color.FromArgb(79, 124, 156), Color.FromArgb(106, 164, 185))
        Using fill As New SolidBrush(Color.FromArgb(alpha, tint)),
              cap As New SolidBrush(Color.FromArgb(Math.Min(130, alpha + 15), tint)),
              edge As New Pen(Color.FromArgb(Math.Min(180, alpha + 45), tint), 0.8F)
            Dim face = {bottom(0), bottom(1), top(1), top(0)}
            graphics.FillPolygon(fill, face)
            graphics.FillPolygon(cap, top)
            graphics.DrawPolygon(edge, face)
            graphics.DrawPolygon(edge, top)
            graphics.DrawLine(edge, bottom(3), top(3))
            graphics.DrawLine(edge, bottom(2), top(2))
        End Using
    End Sub
End Class
