Imports System.Drawing
Imports System.Linq

Partial Friend NotInheritable Class FactoryMapCanvas
    Public Event ConnectorDrawn(kind As String, fromAsset As FactoryMapAsset, fromPoint As PointF, toAsset As FactoryMapAsset, toPoint As PointF)

    Private _connectorTool As String = ""
    Private _connectorStartAsset As FactoryMapAsset
    Private _connectorStartPoint As PointF
    Private _connectorStartScreen As Point
    Private _connectorStarted As Boolean
    Private _connectorCursor As Point

    Public ReadOnly Property ConnectorTool As String
        Get
            Return _connectorTool
        End Get
    End Property

    Public Sub SetConnectorTool(kind As String)
        CompleteEditGesture()
        _connectorTool = If(kind, "")
        _connectorStarted = False
        _connectorStartAsset = Nothing
        Cursor = If(_connectorTool = "", Cursors.Default, Cursors.Cross)
        If _connectorTool <> "" Then SetPlanView()
        Invalidate()
    End Sub

    Public Sub SyncConnectors()
        FactoryMapConnectors.Sync(_assets)
    End Sub

    Private Function ConnectorTarget(point As Point) As FactoryMapAsset
        For Each entry In _hitAreas.Reverse()
            Dim item = entry.Key
            If FactoryMapConnectors.IsConnector(item) OrElse Not _assets.Contains(item) OrElse Not item.IsPlaced OrElse
                item.Level <> ActiveLevel OrElse Not _visibleLayers.Contains(item.Category) OrElse Not ShouldShowAsset(item) Then Continue For
            If _assetHitGeometry(item).Contains(point) Then Return item
        Next
        Return Nothing
    End Function

    Private Function ConnectorClick(point As Point) As Boolean
        Dim target = ConnectorTarget(point)
        Dim ground = ScreenToMapAtHeight(point, 0)
        Dim spot = New PointF(Math.Clamp(ground.X, 0, 4), Math.Clamp(ground.Y, 0, 4))
        If Not _connectorStarted Then
            _connectorStarted = True
            _connectorStartAsset = target
            _connectorStartPoint = spot
            _connectorStartScreen = point
            _connectorCursor = point
            Invalidate()
            Return True
        End If
        If target IsNot Nothing AndAlso target Is _connectorStartAsset Then Return True
        Dim kind = _connectorTool
        Dim startAsset = _connectorStartAsset, startPoint = _connectorStartPoint
        _connectorTool = ""
        _connectorStarted = False
        _connectorStartAsset = Nothing
        Cursor = Cursors.Default
        Invalidate()
        RaiseEvent ConnectorDrawn(kind, startAsset, startPoint, target, spot)
        Return True
    End Function

    ' Press-drag-release draws a connector in one gesture; a plain click still starts click-click mode.
    Private Function ConnectorDragEnds(point As Point) As Boolean
        If _connectorTool = "" OrElse Not _connectorStarted Then Return False
        Dim dx = point.X - _connectorStartScreen.X, dy = point.Y - _connectorStartScreen.Y
        If dx * dx + dy * dy < 100 Then Return False
        Return ConnectorClick(point)
    End Function

    Private Sub ConnectorMove(point As Point)
        If Not _connectorStarted Then Return
        _connectorCursor = point
        Invalidate()
    End Sub

    Private Function CancelConnectorTool() As Boolean
        If _connectorTool = "" Then Return False
        _connectorTool = ""
        _connectorStarted = False
        _connectorStartAsset = Nothing
        Cursor = Cursors.Default
        Invalidate()
        Return True
    End Function

    Private Sub DrawConnectorPreview(graphics As Graphics)
        If _connectorTool = "" Then Return
        Dim hover = If(_connectorStarted, ConnectorTarget(_connectorCursor), Nothing)
        Using pen As New Pen(UiTheme.Blue, 2.5F) With {.DashStyle = Drawing2D.DashStyle.Dash}, brush As New SolidBrush(UiTheme.Blue)
            If _connectorStarted Then
                graphics.DrawLine(pen, _connectorStartScreen, _connectorCursor)
                graphics.FillEllipse(brush, _connectorStartScreen.X - 5, _connectorStartScreen.Y - 5, 10, 10)
                graphics.FillEllipse(brush, _connectorCursor.X - 5, _connectorCursor.Y - 5, 10, 10)
            End If
            Using font As New Font("Segoe UI", 9.0F, FontStyle.Bold)
                Dim text = If(_connectorStarted,
                    "Click the end item or a floor spot" & If(hover IsNot Nothing, "  ->  " & hover.Name, ""),
                    "Click the first item (or a floor spot) to start the " & _connectorTool.ToLowerInvariant() & ". Esc cancels.")
                graphics.DrawString(text, font, brush, 12.0F, 40.0F)
            End Using
        End Using
    End Sub
End Class
