Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Linq

Friend NotInheritable Class FactoryMapBuildingRenderer
    Private ReadOnly _graphics As Graphics
    Private ReadOnly _project As Func(Of Single, Single, Single, PointF)
    Private ReadOnly _hit As GraphicsPath
    Private ReadOnly _part As FactoryBuildingPart
    Private ReadOnly _nearTop As Boolean
    Private ReadOnly _stairFaces As New List(Of (Depth As Single, Points As PointF(), Tint As Color))
    Private ReadOnly _stairLines As New List(Of Action)

    Private Sub New(graphics As Graphics, part As FactoryBuildingPart, project As Func(Of Single, Single, Single, PointF), hit As GraphicsPath)
        _graphics = graphics
        _part = part
        Dim angle = part.Rotation * Math.PI / 180
        Dim cosine = Math.Cos(angle), sine = Math.Sin(angle)
        Dim worldX = part.X * 1365, worldY = part.Y * 766
        Dim baseHeight = part.BaseMeters * 8
        Dim mirror = part.Kind = "Stairs" AndAlso part.Style.EndsWith("right", StringComparison.Ordinal)
        _project = Function(x, y, z)
                       If mirror Then y = -y
                       If part.MirrorHorizontal Then x = -x
                       If part.MirrorVertical Then y = -y
                       Return project(worldX + CSng(x * cosine - y * sine), worldY + CSng(x * sine + y * cosine), z + baseHeight)
                   End Function
        _hit = hit
        Dim origin = _project(0, 0, 0)
        Dim roof = _project(0, 0, 1)
        Dim across = _project(0, 1, 0)
        Dim along = _project(1, 0, 0)
        Dim groundScale = Math.Sqrt((across.X - origin.X) ^ 2 + (across.Y - origin.Y) ^ 2) *
            Math.Sqrt((along.X - origin.X) ^ 2 + (along.Y - origin.Y) ^ 2)
        _nearTop = (roof.X - origin.X) ^ 2 + (roof.Y - origin.Y) ^ 2 < groundScale * 0.4R
    End Sub

    Public Shared Sub Draw(graphics As Graphics, part As FactoryBuildingPart, parts As IEnumerable(Of FactoryBuildingPart),
                           opacity As Integer, project As Func(Of Single, Single, Single, PointF), hit As GraphicsPath)
        Dim resolved = FactoryMapBuildingStore.Resolve(part, parts)
        Dim renderer As New FactoryMapBuildingRenderer(graphics, resolved, project, hit)
        Dim color = ColorTranslator.FromHtml(part.BodyColor)
        Select Case part.Kind
            Case "Floor" : renderer.Floor(color)
            Case "Road" : renderer.Road(color)
            Case "Marking" : renderer.Marking(color)
            Case "Wall" : renderer.Wall(parts.Where(Function(item) item.ParentWallId = part.Id).OrderBy(Function(item) item.Offset), color, opacity)
            Case "Stairs" : renderer.Stairs(color)
            Case Else : renderer.Opening(color)
        End Select
        For Each stairFace In renderer._stairFaces.OrderBy(Function(face) face.Depth)
            renderer.Face(stairFace.Points, stairFace.Tint)
        Next
        For Each railing In renderer._stairLines
            railing()
        Next
    End Sub

    Private Sub Marking(color As Color)
        Dim w = _part.Length / 2
        Dim d = _part.Depth / 2
        Dim z = _part.HeightMeters * 8
        If {"Rectangle", "Ellipse", "Freehand"}.Contains(_part.Style) Then
            Using path As New GraphicsPath()
                If _part.Style = "Freehand" Then
                    path.AddLines(_part.DrawingPoints.Select(Function(p) _project(p.X * _part.Length, p.Y * _part.Depth, z)).ToArray())
                ElseIf _part.Style = "Rectangle" Then
                    path.AddPolygon({_project(-w, -d, z), _project(w, -d, z), _project(w, d, z), _project(-w, d, z)})
                Else
                    path.AddPolygon(Enumerable.Range(0, 64).Select(Function(i) _project(CSng(w * Math.Cos(i * Math.PI / 32)),
                        CSng(d * Math.Sin(i * Math.PI / 32)), z)).ToArray())
                End If
                Using pen As New Pen(color, 2), pick As New Pen(color, 7)
                    pen.LineJoin = LineJoin.Round
                    _graphics.DrawPath(pen, path)
                    path.Widen(pick)
                    _hit.AddPath(path, False)
                End Using
            End Using
            Return
        End If
        AddHit({_project(-w, -d, z), _project(w, -d, z), _project(w, d, z), _project(-w, d, z)})
        Select Case _part.Style
            Case "Solid line"
                Stripe(-w, -d, _part.Length, _part.Depth, z, color)
            Case "Dashed line"
                For x As Single = -w To w Step 16
                    Stripe(x, -d, Math.Min(9, w - x), _part.Depth, z, color)
                Next
            Case "Walkway"
                Stripe(-w, -d, _part.Length, _part.Depth, z, Color.FromArgb(110, color))
                Dim edge = Math.Min(1, _part.Depth / 5)
                Stripe(-w, -d, _part.Length, edge, z, color)
                Stripe(-w, d - edge, _part.Length, edge, z, color)
                For x As Single = -w + 10 To w - 5 Step 24
                    Line(x - 3, 0, z, x + 3, 0, z, color)
                    Line(x, -2, z, x + 3, 0, z, color)
                    Line(x, 2, z, x + 3, 0, z, color)
                Next
            Case "Keep clear hatching"
                For x As Single = -w To w Step 10
                    Line(x, -d, z, Math.Min(w, x + _part.Depth), d, z, color)
                Next
                Using pen As New Pen(color, 1)
                    _graphics.DrawPolygon(pen, {_project(-w, -d, z), _project(w, -d, z), _project(w, d, z), _project(-w, d, z)})
                End Using
        End Select
    End Sub

    Private Sub Road(color As Color)
        If FactoryMapRoadGeometry.IsCurve(_part) Then
            CurvedRoad(color)
            Return
        End If
        Dim w = _part.Length / 2
        Dim d = _part.Depth / 2
        Dim z = _part.HeightMeters * 8
        Dim points = {_project(-w, -d, z), _project(w, -d, z), _project(w, d, z), _project(-w, d, z)}
        If _part.Style = "Unmarked asphalt" Then
            Dim state = _graphics.Save()
            Try
                _graphics.SmoothingMode = SmoothingMode.None
                Using brush As New SolidBrush(color)
                    _graphics.FillPolygon(brush, points)
                End Using
            Finally
                _graphics.Restore(state)
            End Try
            AddHit(points)
            Return
        End If
        Using brush As New SolidBrush(color)
            _graphics.FillPolygon(brush, points)
        End Using
        AddHit(points)
        Dim yellow = Color.FromArgb(245, 204, 48)
        Dim margin = Math.Min(2, d / 6)
        Dim stroke = Math.Min(1.8F, d / 8)
        For Each side In {-1.0F, 1.0F}
            Stripe(-w, side * (d - margin) - stroke / 2, _part.Length, stroke, z, Color.FromArgb(204, 212, 216))
        Next
        Select Case _part.Style
            Case "Dashed yellow center"
                For x As Single = -w To w Step 22
                    Stripe(x, -stroke / 2, Math.Min(13, w - x), stroke, z + 0.02F, yellow)
                Next
            Case "Double yellow center"
                Stripe(-w, -stroke * 1.5F, _part.Length, stroke, z, yellow)
                Stripe(-w, stroke * 0.5F, _part.Length, stroke, z, yellow)
            Case "Pedestrian crossing"
                For x As Single = -w To w Step 8
                    Stripe(x, -d + margin * 2, Math.Min(4, w - x), _part.Depth - margin * 4, z, Color.WhiteSmoke)
                Next
            Case "Parking bays"
                For x As Single = -w To w Step 22
                    Stripe(x, -d + margin, stroke, _part.Depth - margin * 2, z, yellow)
                Next
        End Select
    End Sub

    Private Sub CurvedRoad(color As Color)
        Dim halfAngle = _part.RoadBendDegrees * Math.PI / 360
        Dim z = _part.HeightMeters * 8
        Dim halfWidth = _part.Depth / 2
        Dim stroke = Math.Min(1.8F, halfWidth / 8)
        Dim margin = Math.Min(2, halfWidth / 6)
        Dim paint As Action(Of Double, Double, Single, Single, Color) =
            Sub(first, last, inner, outer, tint)
                Dim points = FactoryMapRoadGeometry.Strip(_part, first, last, inner, outer).
                    Select(Function(p) _project(p.X, p.Y, z)).ToArray()
                Using brush As New SolidBrush(tint)
                    _graphics.FillPolygon(brush, points)
                End Using
            End Sub
        Dim outline = FactoryMapRoadGeometry.Strip(_part, -halfAngle, halfAngle, -halfWidth, halfWidth).
            Select(Function(p) _project(p.X, p.Y, z)).ToArray()
        Using brush As New SolidBrush(color)
            _graphics.FillPolygon(brush, outline)
        End Using
        AddHit(outline)
        For Each side In {-1, 1}
            Dim offset = side * (halfWidth - margin)
            paint(-halfAngle, halfAngle, offset - stroke / 2, offset + stroke / 2, Color.FromArgb(204, 212, 216))
        Next
        Dim arcLength = 2 * halfAngle * _part.Length
        For distance As Double = 0 To arcLength Step 22
            paint(-halfAngle + distance / _part.Length, -halfAngle + Math.Min(distance + 13, arcLength) / _part.Length,
                  -stroke / 2, stroke / 2, Color.FromArgb(245, 204, 48))
        Next
    End Sub

    Private Sub Stripe(x As Single, y As Single, width As Single, depth As Single, z As Single, color As Color)
        If width <= 0 OrElse depth <= 0 Then Return
        Using brush As New SolidBrush(color)
            _graphics.FillPolygon(brush, {_project(x, y, z), _project(x + width, y, z),
                _project(x + width, y + depth, z), _project(x, y + depth, z)})
        End Using
    End Sub

    Private Sub Stairs(color As Color)
        Dim width = _part.Length
        Dim depth = _part.Depth
        Dim height = _part.HeightMeters * 8
        Dim turn = _part.Style <> "Straight flight"
        Dim uTurn = _part.Style.StartsWith("U-turn", StringComparison.Ordinal)
        Dim flightDepth = If(turn, Math.Min(width * 0.3F, depth * 0.4F), depth)
        Dim steps = Math.Max(1, CInt(Math.Ceiling(_part.HeightMeters / If(turn, 0.36F, 0.18F))))
        Dim run = If(turn, width - flightDepth, width)
        For index = 0 To steps - 1
            Dim z = height * (index + 1) / If(turn, steps * 2, steps)
            Box(-width / 2 + index * run / steps, -depth / 2, 0, run / steps, flightDepth, z, color)
        Next
        If turn Then
            Box(width / 2 - flightDepth, -depth / 2, 0, flightDepth, If(uTurn, depth, flightDepth), height / 2, color)
            For index = 0 To steps - 1
                Dim z = height * (steps + index + 1) / (steps * 2)
                If uTurn Then
                    Box(-width / 2 + run - (index + 1) * run / steps, depth / 2 - flightDepth, 0, run / steps, flightDepth, z, color)
                Else
                    Dim y = -depth / 2 + flightDepth + index * (depth - flightDepth) / steps
                    Box(width / 2 - flightDepth, y, 0, flightDepth, (depth - flightDepth) / steps, z, color)
                End If
            Next
        End If
        For index = 0 To steps
            Dim x = -width / 2 + index * run / steps
            Dim z = height * index / If(turn, steps * 2, steps)
            Dim y = -depth / 2
            Line(x, y, z, x, y, z + 8, Color.FromArgb(95, 112, 124))
            If index < steps Then Line(x, y, z + 8, x + run / steps, y, z + height / If(turn, steps * 2, steps) + 8, Color.FromArgb(95, 112, 124))
        Next
    End Sub

    Private Sub Wall(openings As IEnumerable(Of FactoryBuildingPart), color As Color, opacity As Integer)
        Dim start = -_part.Length / 2
        Dim height = _part.HeightMeters * 8
        If color.R = 141 AndAlso color.G = 170 AndAlso color.B = 189 Then color = Color.FromArgb(216, 224, 228)
        Dim tint = Color.FromArgb(CInt(opacity * 255 / 100 * If(_part.Material = "Glass", 0.6F, 1.0F)), color)
        For Each cutout As FactoryBuildingPart In openings
            Dim left = cutout.Offset - cutout.Length / 2
            Dim right = cutout.Offset + cutout.Length / 2
            If left > start Then Box(start, -_part.Depth / 2, 0, left - start, _part.Depth, height, tint)
            If cutout.BaseMeters > 0 Then Box(left, -_part.Depth / 2, 0, cutout.Length, _part.Depth, cutout.BaseMeters * 8, tint)
            Dim top = (cutout.BaseMeters + cutout.HeightMeters) * 8
            If top < height Then Box(left, -_part.Depth / 2, top, cutout.Length, _part.Depth, height - top, tint)
            start = right
        Next
        If start < _part.Length / 2 Then Box(start, -_part.Depth / 2, 0, _part.Length / 2 - start, _part.Depth, height, tint)
    End Sub

    Private Sub Floor(color As Color)
        Dim w = _part.Length / 2
        Dim d = _part.Depth / 2
        Dim z = _part.HeightMeters * 8
        Dim local = If(_part.Style = "L-shaped",
            {New PointF(-w, -d), New PointF(w, -d), New PointF(w, 0), New PointF(0, 0), New PointF(0, d), New PointF(-w, d)},
            {New PointF(-w, -d), New PointF(w, -d), New PointF(w, d), New PointF(-w, d)})
        Dim points = local.Select(Function(point) _project(point.X, point.Y, z)).ToArray()
        Dim bounds = RectangleF.FromLTRB(points.Min(Function(p) p.X), points.Min(Function(p) p.Y),
                                        points.Max(Function(p) p.X), points.Max(Function(p) p.Y))
        bounds.Width = Math.Max(0.1F, bounds.Width)
        bounds.Height = Math.Max(0.1F, bounds.Height)
        Dim mean = (CInt(color.R) + color.G + color.B) / 3.0F
        Dim surface = Color.FromArgb(Math.Clamp(CInt(mean + (color.R - mean) * 1.08F), 0, 255),
                                     Math.Clamp(CInt(mean + (color.G - mean) * 1.08F), 0, 255),
                                     Math.Clamp(CInt(mean + (color.B - mean) * 1.08F), 0, 255))
        Dim highlight = Color.FromArgb(Math.Min(255, surface.R + 14), Math.Min(255, surface.G + 14), Math.Min(255, surface.B + 14))
        Dim shade = Color.FromArgb(CInt(surface.R * 0.94F), CInt(surface.G * 0.94F), CInt(surface.B * 0.94F))
        Using brush As New LinearGradientBrush(bounds, highlight, shade, 65.0F),
              edge As New Pen(Color.FromArgb(100, 54, 65, 73), 0.65F)
            _graphics.FillPolygon(brush, points)
            _graphics.DrawPolygon(edge, points)
        End Using
        AddHit(points)
        ' Polygon textures already clip themselves; only procedural lines need a saved clip.
        If Not {"Tile", "Epoxy", "Concrete"}.Contains(_part.Material) Then
            Dim texture = If(_part.Material = "Asphalt", Color.FromArgb(90, 43, 48, 52),
                If(_part.Material = "Sand", Color.FromArgb(100, 177, 143, 87),
                If(_part.Material = "Gravel", Color.FromArgb(105, 107, 111, 108), Color.FromArgb(65, 140, 148, 153))))
            Dim pattern = If(_part.Material = "Gravel", HatchStyle.SmallConfetti,
                If(_part.Material = "Asphalt", HatchStyle.Percent10, HatchStyle.Percent05))
            Using brush As New HatchBrush(pattern, texture, Color.Transparent)
                _graphics.FillPolygon(brush, points)
            End Using
            Return
        End If
        Dim state = _graphics.Save()
        Try
            Using clip As New GraphicsPath()
                clip.AddPolygon(points)
                _graphics.SetClip(clip, CombineMode.Intersect)
            End Using
            Using softEdge As New Pen(Color.FromArgb(20, 29, 37, 45), 12),
                  contact As New Pen(Color.FromArgb(50, 29, 37, 45), 2)
                _graphics.DrawPolygon(softEdge, points)
                _graphics.DrawPolygon(contact, points)
            End Using
        Finally
            _graphics.Restore(state)
        End Try
    End Sub

    Private Sub Opening(color As Color)
        If _part.Style = "Open hole" Then
            ' A bare cut-out in the wall: no frame, leaf or glass.
            Dim holeWidth = _part.Length / 2, holeDepth = Math.Max(1, _part.Depth) / 2, holeHeight = _part.HeightMeters * 8
            Dim outline = {_project(-holeWidth, -holeDepth, 0), _project(holeWidth, -holeDepth, 0),
                _project(holeWidth, -holeDepth, holeHeight), _project(-holeWidth, -holeDepth, holeHeight)}
            Using edge As New Pen(Color.FromArgb(150, 60, 70, 78), 1.0F) With {.DashStyle = DashStyle.Dash}
                _graphics.DrawPolygon(edge, outline)
            End Using
            AddHit(outline)
            Return
        End If
        Dim width = _part.Length
        Dim height = _part.HeightMeters * 8
        Dim depth = Math.Max(1, _part.Depth)
        Dim frame = Math.Min(1.5F, Math.Min(width / 12, height / 12))
        Dim frameColor = If(_part.Material = "Wood", Color.FromArgb(105, 76, 48), Color.FromArgb(91, 108, 121))
        Box(-width / 2, -depth / 2, 0, frame, depth, height, frameColor)
        Box(width / 2 - frame, -depth / 2, 0, frame, depth, height, frameColor)
        Box(-width / 2, -depth / 2, height - frame, width, depth, frame, frameColor)
        If _part.Kind = "Window" Then Box(-width / 2, -depth / 2, 0, width, depth, frame, frameColor)
        Dim clearWidth = width - frame * 2
        Dim bottom = If(_part.Kind = "Window", frame, Math.Min(0.1F, height / 20))
        Dim clearHeight = height - frame - bottom
        Dim right = _part.OpeningDirection.StartsWith("Right", StringComparison.Ordinal)
        Dim direction = If(_part.OpeningDirection.EndsWith("outward", StringComparison.Ordinal), -1.0F, 1.0F)
        If _part.Style = "Rapid roll-up" Then
            Box(-width / 2 - 1, -depth / 2 - 1, height, width + 2, depth + 2, 3, frameColor)
            Dim leafHeight = If(_part.IsOpen, Math.Min(2, clearHeight), clearHeight)
            Dim base = If(_part.IsOpen, height - frame - leafHeight, bottom)
            If _part.IsOpen Then
                Box(-clearWidth / 2, -0.5F, base, clearWidth, 1, leafHeight, color)
            Else
                Dim windowBottom = bottom + clearHeight * 0.45F
                Dim windowHeight = clearHeight * 0.22F
                Dim windowWidth = clearWidth * 0.32F
                Dim gap = clearWidth * 0.08F
                Box(-clearWidth / 2, -0.5F, bottom, clearWidth, 1, windowBottom - bottom, color)
                Box(-clearWidth / 2, -0.5F, windowBottom + windowHeight, clearWidth, 1, clearHeight * 0.33F, color)
                Dim left = -gap / 2 - windowWidth
                Dim rightWindow = gap / 2
                Box(-clearWidth / 2, -0.5F, windowBottom, left + clearWidth / 2, 1, windowHeight, color)
                Box(-gap / 2, -0.5F, windowBottom, gap, 1, windowHeight, color)
                Box(rightWindow + windowWidth, -0.5F, windowBottom, clearWidth / 2 - rightWindow - windowWidth, 1, windowHeight, color)
                For Each x In {left, rightWindow}
                    Dim pane = {_project(x, -0.55F, windowBottom), _project(x + windowWidth, -0.55F, windowBottom),
                        _project(x + windowWidth, -0.55F, windowBottom + windowHeight), _project(x, -0.55F, windowBottom + windowHeight)}
                    Using glass As New SolidBrush(Color.FromArgb(95, 171, 224, 238)), border As New Pen(frameColor, 0.9F)
                        _graphics.FillPolygon(glass, pane)
                        _graphics.DrawPolygon(border, pane)
                    End Using
                    AddHit(pane)
                Next
            End If
            For z As Single = base + 1 To base + leafHeight Step 2
                If Not _part.IsOpen AndAlso z > bottom + clearHeight * 0.45F AndAlso z < bottom + clearHeight * 0.67F Then Continue For
                Line(-clearWidth / 2, -0.6F, z, clearWidth / 2, -0.6F, z, frameColor)
            Next
            Return
        End If
        If _part.Style = "Sliding" OrElse _part.Style = "Sliding gate" Then
            Dim offset = If(_part.IsOpen, clearWidth * If(right, 1, -1), 0)
            Leaf(-clearWidth / 2 + offset, 0, clearWidth, bottom, clearHeight, color, 0)
            Line(-width, 0, height + 1, width, 0, height + 1, frameColor)
            Return
        End If
        Dim doubleLeaf = _part.Style = "Double leaf" OrElse _part.Style = "Double swing"
        Dim canSwing = _part.Kind <> "Window" OrElse _part.Style = "Casement"
        Dim angle = If(_part.IsOpen AndAlso canSwing, 80.0F * direction, 0)
        If doubleLeaf Then
            Leaf(-clearWidth / 2, 0, clearWidth / 2, bottom, clearHeight, color, angle)
            Leaf(clearWidth / 2, 0, -clearWidth / 2, bottom, clearHeight, color, -angle)
        ElseIf right Then
            Leaf(clearWidth / 2, 0, -clearWidth, bottom, clearHeight, color, -angle)
        Else
            Leaf(-clearWidth / 2, 0, clearWidth, bottom, clearHeight, color, angle)
        End If
    End Sub

    Private Sub Leaf(hinge As Single, y As Single, width As Single, bottom As Single, height As Single, color As Color, angle As Single)
        Dim radians = angle * Math.PI / 180
        Dim projectLeaf As Func(Of Single, Single, Single, PointF) =
            Function(x, dy, z) _project(hinge + CSng(x * Math.Cos(radians) - dy * Math.Sin(radians)),
                                        y + CSng(x * Math.Sin(radians) + dy * Math.Cos(radians)), z)
        Dim glass = _part.Material = "Glass" OrElse _part.Kind = "Window"
        Dim tint = If(glass, Color.FromArgb(78, color), color)
        Dim panel = {projectLeaf(0, 0, bottom), projectLeaf(width, 0, bottom), projectLeaf(width, 0, bottom + height), projectLeaf(0, 0, bottom + height)}
        If _part.Kind = "Gate" AndAlso _part.Material = "Steel" Then
            For x As Single = 0 To Math.Abs(width) Step 3
                Dim actualX = x * Math.Sign(width)
                Using pen As New Pen(color, 1.5F)
                    _graphics.DrawLine(pen, projectLeaf(actualX, 0, bottom), projectLeaf(actualX, 0, bottom + height))
                End Using
            Next
            Using pen As New Pen(color, 2)
                _graphics.DrawPolygon(pen, panel)
            End Using
        Else
            Face(panel, tint)
            If _part.Material = "Wood" Then
                For x As Single = 3 To Math.Abs(width) - 1 Step 3
                    Using pen As New Pen(Color.FromArgb(95, 85, 53, 26), 0.65F)
                        _graphics.DrawLine(pen, projectLeaf(x * Math.Sign(width), 0, bottom + 1), projectLeaf(x * Math.Sign(width), 0, bottom + height - 1))
                    End Using
                Next
            End If
        End If
        AddHit(panel)
        Using pen As New Pen(Color.FromArgb(90, 108, 122), 1.1F)
            _graphics.DrawPolygon(pen, panel)
            Dim handle = width * 0.87F
            _graphics.DrawLine(pen, projectLeaf(handle, -0.5F, bottom + height * 0.4F), projectLeaf(handle, -0.5F, bottom + height * 0.55F))
            If _part.Kind = "Window" Then _graphics.DrawLine(pen, projectLeaf(width / 2, 0, bottom), projectLeaf(width / 2, 0, bottom + height))
        End Using
    End Sub

    Private Sub Box(x As Single, y As Single, z As Single, width As Single, depth As Single, height As Single, color As Color)
        Dim bottom = {_project(x, y, z), _project(x + width, y, z), _project(x + width, y + depth, z), _project(x, y + depth, z)}
        Dim top = {_project(x, y, z + height), _project(x + width, y, z + height), _project(x + width, y + depth, z + height), _project(x, y + depth, z + height)}
        If _part.Kind = "Stairs" Then
            Dim coordinates = {New PointF(x, y), New PointF(x + width, y), New PointF(x + width, y + depth), New PointF(x, y + depth)}
            For index = 0 To 3
                Dim nextIndex = (index + 1) Mod 4
                Dim centerX = (coordinates(index).X + coordinates(nextIndex).X) / 2
                Dim centerY = (coordinates(index).Y + coordinates(nextIndex).Y) / 2
                _stairFaces.Add((StairDepth(centerX, centerY, z + height / 2),
                    {bottom(index), bottom(nextIndex), top(nextIndex), top(index)},
                    Color.FromArgb(color.A, CInt(color.R * 0.85F), CInt(color.G * 0.85F), CInt(color.B * 0.85F))))
            Next
            _stairFaces.Add((StairDepth(x + width / 2, y + depth / 2, z + height), top, color))
            Return
        End If
        For Each index In Enumerable.Range(0, 4).OrderBy(Function(i) bottom(i).Y + bottom((i + 1) Mod 4).Y)
            Dim nextIndex = (index + 1) Mod 4
            Face({bottom(index), bottom(nextIndex), top(nextIndex), top(index)}, Color.FromArgb(color.A, CInt(color.R * 0.85F), CInt(color.G * 0.85F), CInt(color.B * 0.85F)))
        Next
        If _part.Kind = "Wall" Then
            Dim alpha = If(_nearTop AndAlso _part.Material <> "Glass" AndAlso color.A > 0, 245, CInt(color.A))
            Dim cap = Color.FromArgb(alpha, CInt(color.R * 0.12F + 218), CInt(color.G * 0.12F + 218), CInt(color.B * 0.12F + 218))
            Using fill As New SolidBrush(cap),
                  edge As New Pen(Color.FromArgb(Math.Min(235, alpha), 43, 53, 61), 1.25F)
                _graphics.FillPolygon(fill, top)
                _graphics.DrawPolygon(edge, top)
            End Using
            Using light As New Pen(Color.FromArgb(Math.Min(220, alpha), Color.White), 0.85F)
                _graphics.DrawLine(light, top(0), top(1))
            End Using
            AddHit(top)
        Else
            Face(top, color)
        End If
    End Sub

    Private Function StairDepth(x As Single, y As Single, z As Single) As Single
        Dim origin = _project(0, 0, 0)
        Dim a = _project(1, 0, 0)
        Dim b = _project(0, 1, 0)
        Dim c = _project(0, 0, 1)
        Dim nx = (b.X - origin.X) * (c.Y - origin.Y)
        Dim ny = -(a.X - origin.X) * (c.Y - origin.Y)
        Dim nz = (a.X - origin.X) * (b.Y - origin.Y) - (b.X - origin.X) * (a.Y - origin.Y)
        If nz < 0 Then
            nx = -nx
            ny = -ny
            nz = -nz
        End If
        Return x * nx + y * ny + z * nz
    End Function

    Private Sub Face(points As PointF(), color As Color)
        Using brush As New SolidBrush(color), pen As New Pen(Color.FromArgb(Math.Min(190, color.A + 40), 88, 117, 137), 0.7F)
            _graphics.FillPolygon(brush, points)
            If _part.Kind = "Wall" AndAlso {"Brick", "Steel", "Wood"}.Contains(_part.Material) Then
                Dim pattern = If(_part.Material = "Wood", HatchStyle.Vertical, If(_part.Material = "Brick", HatchStyle.SmallGrid, HatchStyle.Horizontal))
                Using texture As New HatchBrush(pattern, Color.FromArgb(Math.Min(55, color.A), 68, 87, 100), Color.Transparent)
                    Dim smoothing = _graphics.SmoothingMode
                    Try
                        ' Hatch pixels are already rasterized; the wall fill and outline remain anti-aliased.
                        _graphics.SmoothingMode = SmoothingMode.None
                        _graphics.FillPolygon(texture, points)
                    Finally
                        _graphics.SmoothingMode = smoothing
                    End Try
                End Using
            End If
            _graphics.DrawPolygon(pen, points)
        End Using
        AddHit(points)
    End Sub

    Private Sub AddHit(points As PointF())
        Dim area As Single
        For index = 0 To points.Length - 1
            Dim nextPoint = points((index + 1) Mod points.Length)
            area += points(index).X * nextPoint.Y - nextPoint.X * points(index).Y
        Next
        If area < 0 Then Array.Reverse(points)
        _hit.AddPolygon(points)
    End Sub

    Private Sub Line(x1 As Single, y1 As Single, z1 As Single, x2 As Single, y2 As Single, z2 As Single, color As Color)
        If _part.Kind = "Stairs" Then
            _stairLines.Add(Sub()
                                Using pen As New Pen(color, 0.8F)
                                    _graphics.DrawLine(pen, _project(x1, y1, z1), _project(x2, y2, z2))
                                End Using
                            End Sub)
            Return
        End If
        Using pen As New Pen(color, 0.8F)
            _graphics.DrawLine(pen, _project(x1, y1, z1), _project(x2, y2, z2))
        End Using
    End Sub
End Class
