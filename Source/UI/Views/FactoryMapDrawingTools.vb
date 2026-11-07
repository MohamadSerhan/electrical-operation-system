Imports System.Drawing
Imports System.Linq
Imports System.Windows.Forms

Partial Friend NotInheritable Class FactoryMapCanvas
    Friend Shared ReadOnly DrawingTools As String() = {"Select", "Wall", "Room", "Floor", "Door", "Window", "Gate", "Line", "Rectangle", "Ellipse", "Pen"}
    Private _drawingTool As String = "Select"
    Private _drawingColor As Color = Color.FromArgb(35, 115, 210)
    Private _drawingActive As Boolean
    Private _penLimitReached As Boolean
    Private ReadOnly _drawingPoints As New List(Of PointF)
    Private _drawingPreview As New List(Of FactoryBuildingPart)
    Private ReadOnly _handleTargets As New Dictionary(Of String, PointF)
    Private _handleAnchorOffset As PointF

    Private Sub BeginHandleDrag(point As Point, height As Single)
        _handleAnchorOffset = PointF.Empty
        If String.IsNullOrEmpty(_dragHandle) OrElse Not _handleTargets.ContainsKey(_dragHandle) Then Return
        Dim anchor = ScreenToMapAtHeight(point, height)
        Dim target = _handleTargets(_dragHandle)
        _handleAnchorOffset = New PointF(target.X / 1365 - anchor.X, target.Y / 766 - anchor.Y)
    End Sub

    Private Sub PaintTransformHandles(graphics As Graphics, center As PointF, positions As Dictionary(Of String, PointF),
                                      height As Single, project As Func(Of Single, Single, Single, PointF))
        _handleTargets.Clear()
        Using brush As New SolidBrush(UiTheme.Blue), pen As New Pen(Color.White, 1.2F), guide As New Pen(UiTheme.Blue, 1)
            guide.DashStyle = Drawing2D.DashStyle.Dot
            For Each entry In positions
                Dim point = project(entry.Value.X, entry.Value.Y, height * 8)
                Dim dx = point.X - center.X, dy = point.Y - center.Y
                Dim distance = CSng(Math.Sqrt(dx * dx + dy * dy))
                Dim minimum = If(entry.Key = "Rotate", 44.0F, 26.0F)
                If distance < minimum AndAlso distance > 0.001F Then
                    point = New PointF(center.X + dx * minimum / distance, center.Y + dy * minimum / distance)
                End If
                Dim box As New RectangleF(point.X - 5, point.Y - 5, 10, 10)
                _handles.Add(entry.Key, RectangleF.Inflate(box, 3, 3))
                _handleTargets.Add(entry.Key, entry.Value)
                graphics.DrawLine(guide, center, point)
                If entry.Key = "Rotate" Then
                    graphics.FillEllipse(brush, box)
                    graphics.DrawEllipse(pen, box)
                Else
                    graphics.FillRectangle(brush, box)
                    graphics.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height)
                End If
            Next
        End Using
    End Sub

    <System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public Property DrawingColor As Color
        Get
            Return _drawingColor
        End Get
        Set(value As Color)
            _drawingColor = value
        End Set
    End Property

    Friend Shared Function EditShortcut(keys As Keys) As String
        Select Case keys
            Case Keys.Control Or Keys.Z : Return "Undo"
            Case Keys.Control Or Keys.Y, Keys.Control Or Keys.Shift Or Keys.Z : Return "Redo"
            Case Keys.Control Or Keys.X : Return "Cut"
            Case Keys.Control Or Keys.C : Return "Copy"
            Case Keys.Control Or Keys.V : Return "Paste"
            Case Keys.Control Or Keys.D : Return "Duplicate"
        End Select
        Return ""
    End Function

    Public Sub CompleteEditGesture()
        EndPointerGesture()
        Capture = False
    End Sub

    Public Function PlacementCenter() As PointF
        Dim point = ScreenToMap(New Point(ClientSize.Width \ 2, ClientSize.Height \ 2))
        Return New PointF(Math.Clamp(point.X, 0, 4), Math.Clamp(point.Y, 0, 4))
    End Function

    Public Sub SetDrawingTool(tool As String)
        If Not DrawingTools.Contains(tool) Then Throw New ArgumentException("Unsupported drawing tool.", NameOf(tool))
        CompleteEditGesture()
        _drawingTool = tool
        If tool <> "Select" Then SetPlanView()
        Invalidate()
    End Sub

    Private Function DrawingPoint(point As Point) As PointF
        Dim map = ScreenToMap(point)
        Return New PointF(Math.Clamp(map.X * 1365, 0, 5460), Math.Clamp(map.Y * 766, 0, 3064))
    End Function

    Private Function SnapDrawingPoint(point As PointF, Optional excludedId As String = "") As PointF
        If Not SnapEnabled OrElse (ModifierKeys And Keys.Alt) = Keys.Alt Then Return point
        Return FactoryMapPlacement.SnapWallPoint(point, _building, ActiveLevel, excludedId, SnapTolerance())
    End Function

    Private Sub BeginDrawing(point As Point)
        _drawingPoints.Clear()
        Dim start = DrawingPoint(point)
        If _drawingTool = "Wall" OrElse _drawingTool = "Room" Then start = SnapDrawingPoint(start)
        _drawingPoints.Add(start)
        _drawingPoints.Add(start)
        _drawingPreview.Clear()
        _penLimitReached = False
        _drawingActive = True
        Cursor = Cursors.Cross
    End Sub

    Private Sub UpdateDrawing(point As Point)
        Dim current = DrawingPoint(point)
        Dim start = _drawingPoints(0)
        If (ModifierKeys And Keys.Shift) = Keys.Shift Then
            Dim dx = current.X - start.X, dy = current.Y - start.Y
            If {"Room", "Floor", "Rectangle", "Ellipse"}.Contains(_drawingTool) Then
                Dim size = Math.Max(Math.Abs(dx), Math.Abs(dy))
                current = New PointF(Math.Clamp(start.X + If(dx < 0, -size, size), 0, 5460),
                                     Math.Clamp(start.Y + If(dy < 0, -size, size), 0, 3064))
            Else
                Dim angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * Math.PI / 4
                Dim distance = Math.Sqrt(dx * dx + dy * dy)
                current = New PointF(Math.Clamp(start.X + CSng(distance * Math.Cos(angle)), 0, 5460),
                                     Math.Clamp(start.Y + CSng(distance * Math.Sin(angle)), 0, 3064))
            End If
        End If
        If _drawingTool = "Wall" Then current = SnapDrawingPoint(current)
        If _drawingTool = "Pen" Then
            Dim previous = _drawingPoints.Last()
            If _drawingPoints.Count < 4096 AndAlso (current.X - previous.X) ^ 2 + (current.Y - previous.Y) ^ 2 > 0.25 Then
                _drawingPoints.Add(current)
            ElseIf _drawingPoints.Count = 4096 AndAlso Not _penLimitReached Then
                _penLimitReached = True
                RaiseEvent BuildingChanged("Pen stroke reached 4096 points. Release to finish this stroke, then start another.")
            End If
        Else
            _drawingPoints(1) = current
        End If
        _drawingPreview = CreateDrawnParts()
        Invalidate()
    End Sub

    Private Function CreateDrawnParts() As List(Of FactoryBuildingPart)
        Dim result As New List(Of FactoryBuildingPart)
        Dim first = _drawingPoints(0), last = _drawingPoints.Last()
        Dim color = $"#{DrawingColor.R:X2}{DrawingColor.G:X2}{DrawingColor.B:X2}"
        If {"Door", "Window", "Gate"}.Contains(_drawingTool) Then
            Dim opening As New FactoryBuildingPart With {
                .Name = "New " & _drawingTool.ToLowerInvariant(), .Kind = _drawingTool, .Level = ActiveLevel,
                .Style = If(_drawingTool = "Window", "Casement", If(_drawingTool = "Gate", "Double swing", "Single leaf")),
                .Material = If(_drawingTool = "Window", "Glass", "Steel"), .BodyColor = color,
                .X = last.X / 1365, .Y = last.Y / 766, .Length = If(_drawingTool = "Gate", 40, 18),
                .HeightMeters = If(_drawingTool = "Window", 1.2F, 2.1F), .BaseMeters = If(_drawingTool = "Window", 1.0F, 0)
            }
            If SnapEnabled AndAlso (ModifierKeys And Keys.Alt) <> Keys.Alt Then
                FactoryMapPlacement.AttachOpening(opening, _building, SnapTolerance())
            End If
            result.Add(opening)
            Return result
        End If
        Dim left = _drawingPoints.Min(Function(p) p.X), top = _drawingPoints.Min(Function(p) p.Y)
        Dim width = _drawingPoints.Max(Function(p) p.X) - left, depth = _drawingPoints.Max(Function(p) p.Y) - top
        If _drawingTool = "Wall" OrElse _drawingTool = "Line" Then
            Dim length = CSng(Math.Sqrt((last.X - first.X) ^ 2 + (last.Y - first.Y) ^ 2))
            If length < 2 Then Return result
            result.Add(New FactoryBuildingPart With {
                .Name = If(_drawingTool = "Wall", "Drawn wall", "Drawn line"), .Level = ActiveLevel,
                .Kind = If(_drawingTool = "Wall", "Wall", "Marking"), .Style = If(_drawingTool = "Wall", "Solid", "Solid line"),
                .Material = If(_drawingTool = "Wall", "Concrete", "Epoxy"), .BodyColor = color,
                .X = (first.X + last.X) / 2 / 1365, .Y = (first.Y + last.Y) / 2 / 766,
                .Length = length, .Depth = 2, .HeightMeters = If(_drawingTool = "Wall", 3.0F, 0.03F),
                .Rotation = FactoryMapBuildingStore.NormalizeAngle(CSng(Math.Atan2(last.Y - first.Y, last.X - first.X) * 180 / Math.PI))
            })
            Return result
        End If
        If _drawingTool <> "Pen" AndAlso (width < 2 OrElse depth < 2) Then Return result
        If _drawingTool = "Pen" AndAlso width < 2 AndAlso depth < 2 Then Return result
        width = Math.Max(2, width)
        depth = Math.Max(0.2F, depth)
        If depth > 800 Then Return result
        Dim floor As New FactoryBuildingPart With {
            .Name = "Drawn " & _drawingTool.ToLowerInvariant(), .Level = ActiveLevel,
            .Kind = If(_drawingTool = "Room" OrElse _drawingTool = "Floor", "Floor", "Marking"),
            .Style = If(_drawingTool = "Pen", "Freehand", If(_drawingTool = "Ellipse", "Ellipse", "Rectangle")),
            .Material = "Epoxy", .BodyColor = color,
            .X = (left + width / 2) / 1365, .Y = (top + depth / 2) / 766, .Length = width, .Depth = depth, .HeightMeters = 0.02F
        }
        If _drawingTool = "Pen" Then
            floor.DrawingPoints = _drawingPoints.Select(Function(p) New PointF(Math.Clamp((p.X - left) / width - 0.5F, -0.5F, 0.5F),
                Math.Clamp((p.Y - top) / depth - 0.5F, -0.5F, 0.5F))).ToList()
        End If
        result.Add(floor)
        If _drawingTool = "Room" Then
            Dim roomId = Guid.NewGuid().ToString("N")
            Dim roomName = "Room " & (_building.Where(Function(p) p.RoomId <> "").Select(Function(p) p.RoomId).Distinct().Count() + 1).ToString()
            Dim corners = {New PointF(left, top), New PointF(left + width, top), New PointF(left + width, top + depth), New PointF(left, top + depth)}
            For index = 0 To 3
                Dim a = corners(index), b = corners((index + 1) Mod 4)
                result.Add(New FactoryBuildingPart With {
                    .Name = roomName & " wall " & (index + 1).ToString(), .Level = ActiveLevel,
                    .X = (a.X + b.X) / 2 / 1365, .Y = (a.Y + b.Y) / 2 / 766,
                    .Length = If(index Mod 2 = 0, width, depth), .Depth = 2,
                    .Rotation = index * 90, .BodyColor = "#8DAABD"
                })
            Next
            For Each part In result
                part.RoomId = roomId
                part.RoomName = roomName
            Next
            floor.Name = roomName & " floor"
        End If
        Return result
    End Function

    Private Sub FinishDrawing(point As Point)
        UpdateDrawing(point)
        _drawingActive = False
        If _drawingPreview.Count = 0 Then
            RaiseEvent BuildingChanged("Drawing not created: drag at least 2 plan units; floor/shape depth cannot exceed 800 units.")
            Invalidate()
            Return
        End If
        Try
            FactoryMapBuildingStore.Validate(_building.Concat(_drawingPreview))
            _building.AddRange(_drawingPreview)
            SelectBuildingPart(_drawingPreview(0))
            RaiseEvent BuildingChanged("Drawing created. Choose Select to move / resize / rotate; Save to persist. Ctrl+Z undoes the entire drawing.")
            RaiseEvent EditCommitted()
        Catch ex As IO.InvalidDataException
            RaiseEvent BuildingChanged("Drawing rejected: " & ex.Message)
        End Try
        _drawingPreview.Clear()
        Invalidate()
    End Sub

    Private Sub DrawDrawingPreview(graphics As Graphics, project As Func(Of Single, Single, Single, PointF))
        If Not _drawingActive Then Return
        For Each part In _drawingPreview
            Using hit As New Drawing2D.GraphicsPath()
                FactoryMapBuildingRenderer.Draw(graphics, part, _building.Concat(_drawingPreview), 40, project, hit)
            End Using
        Next
        If _drawingPoints.Count > 1 Then
            Dim first = _drawingPoints(0), last = _drawingPoints.Last()
            Dim length = Math.Sqrt((first.X - last.X) ^ 2 + (first.Y - last.Y) ^ 2)
            Dim label = If({"Room", "Floor", "Rectangle", "Ellipse"}.Contains(_drawingTool),
                Math.Abs(last.X - first.X).ToString("0.#") & " x " & Math.Abs(last.Y - first.Y).ToString("0.#") & " u", length.ToString("0.#") & " u")
            Dim position = project(last.X, last.Y, 0)
            TextRenderer.DrawText(graphics, label, Font, New Point(CInt(position.X + 12), CInt(position.Y + 12)), UiTheme.Ink, UiTheme.Surface)
        End If
    End Sub

    Private Sub ResizeWallEndpoint(candidate As FactoryBuildingPart, anchor As PointF, startHandle As Boolean)
        Dim fixedPoint = FactoryMapBuildingStore.WorldPoint(candidate, If(startHandle, candidate.Length / 2, -candidate.Length / 2), 0)
        Dim movingPoint = SnapDrawingPoint(New PointF(anchor.X * 1365, anchor.Y * 766), candidate.Id)
        Dim first = If(startHandle, movingPoint, fixedPoint), last = If(startHandle, fixedPoint, movingPoint)
        Dim dx = last.X - first.X, dy = last.Y - first.Y
        Dim length = CSng(Math.Sqrt(dx * dx + dy * dy))
        If length < 2 Then Return
        candidate.Length = Math.Min(5460, length)
        candidate.Position = New PointF((first.X + last.X) / 2 / 1365, (first.Y + last.Y) / 2 / 766)
        candidate.Rotation = FactoryMapBuildingStore.NormalizeAngle(CSng(Math.Atan2(dy, dx) * 180 / Math.PI) + If(candidate.MirrorHorizontal, 180, 0))
    End Sub

    Private Function ScreenToMapAtHeight(point As Point, height As Single) As PointF
        Dim bounds = GetProjectedBounds()
        Dim scale = Math.Min(Math.Max(1, ClientSize.Width - 32) / bounds.Width,
                             Math.Max(1, ClientSize.Height - 32) / bounds.Height) * _zoom
        Return ScreenToMap(New Point(point.X, CInt(point.Y + height * 8 * Math.Cos(_elevation) * scale)))
    End Function

    Private Function AssetHandleFrame(asset As FactoryMapAsset) As RectangleF
        Dim unrotated = asset.Clone()
        unrotated.Rotation = If(asset.Category = "Generators", 270, 0)
        Return AssetBounds(unrotated, PointF.Empty)
    End Function

    Private Sub DrawAssetHandles(graphics As Graphics, project As Func(Of Single, Single, Single, PointF))
        If Not EditMode Then Return
        _handles.Clear()
        If _selectedAssets.Count > 1 OrElse _selectedAsset Is Nothing OrElse Not _hitAreas.ContainsKey(_selectedAsset) Then Return
        Dim asset = _selectedAsset
        Dim frame = AssetHandleFrame(asset)
        Dim rotation = asset.Rotation + If(asset.Category = "Generators", 90, 0)
        Dim positions As New Dictionary(Of String, PointF) From {
            {"Length", New PointF(frame.Right, 0)}, {"Depth", New PointF(0, frame.Bottom)},
            {"Rotate", New PointF(0, frame.Top - Math.Max(14, SnapTolerance() * 3))}
        }
        Dim z = MountHeight(asset) * 8
        Dim center = project(asset.Position.X * 1365, asset.Position.Y * 766, z)
        Dim worldPositions = positions.ToDictionary(Function(entry) entry.Key,
            Function(entry)
                Dim world = FactoryMapPlacement.RotatePoint(entry.Value.X, entry.Value.Y, rotation)
                Return New PointF(asset.Position.X * 1365 + world.X, asset.Position.Y * 766 + world.Y)
            End Function)
        PaintTransformHandles(graphics, center, worldPositions, MountHeight(asset), project)
    End Sub

    Private Sub TransformAsset(point As Point)
        Dim asset = _selectedAsset
        Dim anchor = ScreenToMapAtHeight(point, MountHeight(asset))
        anchor = New PointF(anchor.X + _handleAnchorOffset.X, anchor.Y + _handleAnchorOffset.Y)
        Dim dx = (anchor.X - asset.Position.X) * 1365, dy = (anchor.Y - asset.Position.Y) * 766
        Dim offset = If(asset.Category = "Generators", 90, 0)
        Dim radians = (asset.Rotation + offset) * Math.PI / 180
        If _dragHandle = "Rotate" Then
            asset.Rotation = FactoryMapBuildingStore.NormalizeAngle(CSng(Math.Atan2(dy, dx) * 180 / Math.PI + 90 - offset))
        Else
            Dim frame = AssetHandleFrame(asset)
            If _dragHandle = "Length" Then
                Dim local = Math.Abs(dx * Math.Cos(radians) + dy * Math.Sin(radians))
                asset.ScaleX = Math.Clamp(CSng(asset.ScaleX * local / Math.Max(0.01F, frame.Right)), 0.1F, 5)
            ElseIf _dragHandle = "Depth" Then
                Dim local = Math.Abs(-dx * Math.Sin(radians) + dy * Math.Cos(radians))
                asset.ScaleY = Math.Clamp(CSng(asset.ScaleY * local / Math.Max(0.01F, frame.Bottom)), 0.1F, 5)
            End If
        End If
        RaiseEvent AssetMoved(asset)
        Invalidate()
    End Sub
End Class
