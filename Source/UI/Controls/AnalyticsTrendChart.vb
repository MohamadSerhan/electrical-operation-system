Imports System.ComponentModel
Imports System.Drawing
Imports System.Globalization
Imports System.Linq
Imports System.Windows.Forms

Public Enum AnalyticsChartType
    Line
    Scatter
    Area
    Column
    StepLine
    StackedArea
    ContributionPercent
    Donut
End Enum

Public NotInheritable Class AnalyticsTrendPoint
    Public Property TimestampUtc As DateTime
    Public Property XValue As Double?
    Public Property ConnectFromPrevious As Boolean = True
    Public Property Value As Double?
    Public Property IsValid As Boolean
    Public Property IsCommunicationGap As Boolean
End Class

Public NotInheritable Class AnalyticsTrendSeries
    Public Property Name As String = String.Empty
    Public Property Color As Color
    Public Property Points As IReadOnlyList(Of AnalyticsTrendPoint) = Array.Empty(Of AnalyticsTrendPoint)()
    Public Property IsState As Boolean
    Public Property ExpectedIntervalSeconds As Double?
    Public Property ConnectCommunicationGaps As Boolean
    Public Property ComparisonBins As IReadOnlyList(Of ChannelComparisonBin) = Array.Empty(Of ChannelComparisonBin)()
End Class

Public NotInheritable Class AnalyticsTrendChart
    Inherits Control

    Private NotInheritable Class HitTarget
        Public Property Point As AnalyticsTrendPoint
        Public Property SeriesName As String
        Public Property Location As PointF
        Public Property Description As String = String.Empty
    End Class

    Private Const MaximumRenderedPoints As Integer = 20000
    Private ReadOnly _toolTip As New ToolTip()
    Private _series As IReadOnlyList(Of AnalyticsTrendSeries) = Array.Empty(Of AnalyticsTrendSeries)()
    Private _windowStartUtc As DateTime
    Private _windowEndUtc As DateTime
    Private _fullStartUtc As DateTime
    Private _fullEndUtc As DateTime
    Private _zoomed As Boolean
    Private _dragging As Boolean
    Private _dragOriginX As Integer
    Private _dragStartUtc As DateTime
    Private _useLocalTime As Boolean
    Private _pollingIntervalSeconds As Integer = 10
    Private _minimumZoomSeconds As Integer
    Private _traceRevealUtc As DateTime?
    Private _minimumX As Double
    Private _maximumX As Double
    Private _fullMinimumX As Double
    Private _fullMaximumX As Double
    Private _numericZoomed As Boolean
    Private _dragMinimumX As Double

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property NumericXAxis As Boolean

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property ComparisonStyle As ChannelComparisonStyle

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property ShowInteractionHints As Boolean = True

    Public Event TimeViewChanged As EventHandler

    <Browsable(False)>
    Public ReadOnly Property ViewStartUtc As DateTime
        Get
            Return _windowStartUtc
        End Get
    End Property

    <Browsable(False)>
    Public ReadOnly Property ViewEndUtc As DateTime
        Get
            Return _windowEndUtc
        End Get
    End Property

    Public Sub SetTimeView(startUtc As DateTime, endUtc As DateTime)
        If NumericXAxis OrElse endUtc <= startUtc Then Return
        SetView(startUtc, endUtc - startUtc)
        Invalidate()
    End Sub

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property TraceRevealUtc As DateTime?
        Get
            Return _traceRevealUtc
        End Get
        Set(value As DateTime?)
            If Nullable.Equals(_traceRevealUtc, value) Then Return
            _traceRevealUtc = value
            Invalidate()
        End Set
    End Property

    ' Overrides the smallest zoom span when the gap limit (polling interval) is much larger than the data resolution.
    <System.ComponentModel.Browsable(False), System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public Property MinimumZoomSeconds As Integer
        Get
            Return _minimumZoomSeconds
        End Get
        Set(value As Integer)
            _minimumZoomSeconds = Math.Max(0, value)
        End Set
    End Property
    Private _showMarkers As Boolean
    Private _showGrid As Boolean = True
    Private _chartType As AnalyticsChartType = AnalyticsChartType.Line
    Private ReadOnly _hitTargets As New List(Of HitTarget)()

    Public Sub New()
        DoubleBuffered = True
        BackColor = UiTheme.Surface
        ForeColor = UiTheme.Ink
        Font = New Font("Segoe UI", 8.0F)
        MinimumSize = New Size(260, 180)
        SetStyle(ControlStyles.ResizeRedraw Or ControlStyles.UserPaint Or ControlStyles.AllPaintingInWmPaint, True)
    End Sub

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property ChartTitle As String = String.Empty

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property YAxisLabel As String = String.Empty

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property XAxisLabel As String = "Host acquisition time (UTC)"

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property TimestampDescription As String = "Host-side acquisition timestamp; source freshness is unavailable."

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property EmptyDataMessage As String = String.Empty

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property Series As IReadOnlyList(Of AnalyticsTrendSeries)
        Get
            Return _series
        End Get
        Set(value As IReadOnlyList(Of AnalyticsTrendSeries))
            _series = If(value, Array.Empty(Of AnalyticsTrendSeries)())
            Invalidate()
        End Set
    End Property

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property WindowStartUtc As DateTime
        Get
            Return _fullStartUtc
        End Get
        Set(value As DateTime)
            _fullStartUtc = value
            ApplyFullWindow()
            Invalidate()
        End Set
    End Property

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property WindowEndUtc As DateTime
        Get
            Return _fullEndUtc
        End Get
        Set(value As DateTime)
            _fullEndUtc = value
            ApplyFullWindow()
            Invalidate()
        End Set
    End Property

    ' Axis labels and tooltips use local time when set; otherwise UTC.
    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property UseLocalTime As Boolean
        Get
            Return _useLocalTime
        End Get
        Set(value As Boolean)
            _useLocalTime = value
            Invalidate()
        End Set
    End Property

    Private Sub ApplyFullWindow()
        Dim fullSpan = _fullEndUtc - _fullStartUtc
        If Not _zoomed OrElse fullSpan <= TimeSpan.Zero Then
            _windowStartUtc = _fullStartUtc
            _windowEndUtc = _fullEndUtc
            _zoomed = False
            Return
        End If
        Dim span = _windowEndUtc - _windowStartUtc
        If span >= fullSpan OrElse span <= TimeSpan.Zero Then
            _zoomed = False
            _windowStartUtc = _fullStartUtc
            _windowEndUtc = _fullEndUtc
            Return
        End If
        SetView(_windowStartUtc, span)
    End Sub

    Private Sub SetView(start As DateTime, span As TimeSpan)
        Dim fullSpan = _fullEndUtc - _fullStartUtc
        If span >= fullSpan Then
            ResetZoom()
            Return
        End If
        If start < _fullStartUtc Then start = _fullStartUtc
        If start + span > _fullEndUtc Then start = _fullEndUtc - span
        _windowStartUtc = start
        _windowEndUtc = start + span
        _zoomed = True
    End Sub

    Public Sub ResetZoom()
        _dragging = False
        _numericZoomed = False
        _minimumX = _fullMinimumX
        _maximumX = _fullMaximumX
        _zoomed = False
        _windowStartUtc = _fullStartUtc
        _windowEndUtc = _fullEndUtc
        Cursor = Cursors.Default
        Invalidate()
        RaiseEvent TimeViewChanged(Me, EventArgs.Empty)
    End Sub

    Private Function PlotRect() As RectangleF
        Dim top = If(NumericXAxis, 58, Math.Max(58, 33 + LegendRows() * 22 + 4))
        Return New RectangleF(72.0F, top, Math.Max(20.0F, Width - 90.0F), Math.Max(20.0F, Height - top - 52))
    End Function

    Private Function LegendColumns() As Integer
        Return Math.Max(1, (Width - 96) \ 220)
    End Function

    Private Function LegendRows() As Integer
        If _series.Count < 2 Then Return 0
        Return CInt(Math.Ceiling(_series.Count / CDbl(LegendColumns())))
    End Function

    Public ReadOnly Property MinimumReadableHeight As Integer
        Get
            Return 300 + CInt(PlotRect().Top)
        End Get
    End Property

    Private Function ShownTime(value As DateTime) As DateTime
        Return If(_useLocalTime, value.ToLocalTime(), value)
    End Function

    ' When set, the owner decides the visible span: the wheel only raises WheelZoomRequested.
    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property ExternalWheelZoom As Boolean

    Public Event WheelZoomRequested(sender As Object, zoomIn As Boolean)

    ' When set, the plain wheel scrolls the containing page and Ctrl+wheel zooms.
    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property WheelZoomNeedsCtrl As Boolean

    Protected Overrides Sub OnMouseWheel(e As MouseEventArgs)
        MyBase.OnMouseWheel(e)
        If WheelZoomNeedsCtrl AndAlso (Control.ModifierKeys And Keys.Control) = 0 Then
            Dim host = Parent
            Do While host IsNot Nothing AndAlso Not TypeOf host Is ScrollableControl OrElse (TypeOf host Is ScrollableControl AndAlso Not DirectCast(host, ScrollableControl).AutoScroll)
                host = host.Parent
            Loop
            Dim scroller = TryCast(host, ScrollableControl)
            If scroller IsNot Nothing Then scroller.AutoScrollPosition = New Point(-scroller.AutoScrollPosition.X, -scroller.AutoScrollPosition.Y - e.Delta)
            Return
        End If
        If NumericXAxis Then
            Dim numericPlot = PlotRect()
            If Not numericPlot.Contains(e.Location) OrElse e.Delta = 0 OrElse _maximumX <= _minimumX Then Return
            Dim numericFraction = (e.X - numericPlot.Left) / numericPlot.Width
            Dim numericSpan = _maximumX - _minimumX
            Dim newNumericSpan = Math.Clamp(numericSpan * If(e.Delta > 0, 0.7R, 1.0R / 0.7R),
                (_fullMaximumX - _fullMinimumX) / 1000000.0R, _fullMaximumX - _fullMinimumX)
            SetNumericView(_minimumX + numericSpan * numericFraction - newNumericSpan * numericFraction, newNumericSpan)
            Invalidate()
            _toolTip.Hide(Me)
            Return
        End If
        If ExternalWheelZoom Then
            If e.Delta <> 0 AndAlso PlotRect().Contains(e.Location) Then RaiseEvent WheelZoomRequested(Me, e.Delta > 0)
            Return
        End If
        Dim fullSpan = _fullEndUtc - _fullStartUtc
        Dim plot = PlotRect()
        If fullSpan <= TimeSpan.Zero OrElse _chartType = AnalyticsChartType.Donut OrElse Not plot.Contains(e.Location) Then Return
        Dim fraction = (e.X - plot.Left) / plot.Width
        Dim span = _windowEndUtc - _windowStartUtc
        Dim anchor = _windowStartUtc.AddTicks(CLng(span.Ticks * fraction))
        ' Discrete zoom levels: 30 s, 1 min, 2 min, 5 min, 15 min, 30 min, 1 h ... one wheel notch = one level.
        Dim levels = {30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400, 172800, 345600, 604800, 1209600, 2592000}
        Dim minimumSeconds = If(_minimumZoomSeconds > 0, _minimumZoomSeconds, Math.Max(30, _pollingIntervalSeconds * 4))
        Dim current = span.TotalSeconds
        Dim newSpan As TimeSpan
        If e.Delta > 0 Then
            Dim smaller = levels.Where(Function(level) level < current * 0.995R AndAlso level >= minimumSeconds).ToList()
            If smaller.Count = 0 Then Return
            newSpan = TimeSpan.FromSeconds(smaller.Max())
        Else
            Dim larger = levels.Where(Function(level) level > current * 1.005R).ToList()
            newSpan = If(larger.Count = 0, fullSpan, TimeSpan.FromSeconds(larger.Min()))
        End If
        If newSpan >= fullSpan Then
            ResetZoom()
        Else
            SetView(anchor.AddTicks(-CLng(newSpan.Ticks * fraction)), newSpan)
            Invalidate()
        End If
        _toolTip.Hide(Me)
        RaiseEvent TimeViewChanged(Me, EventArgs.Empty)
    End Sub

    Protected Overrides Sub OnMouseDown(e As MouseEventArgs)
        MyBase.OnMouseDown(e)
        If NumericXAxis Then
            If e.Button = MouseButtons.Left AndAlso _numericZoomed AndAlso PlotRect().Contains(e.Location) Then
                _dragging = True
                _dragOriginX = e.X
                _dragMinimumX = _minimumX
                Cursor = Cursors.SizeWE
                _toolTip.Hide(Me)
            End If
            Return
        End If
        If e.Button = MouseButtons.Left AndAlso _zoomed AndAlso PlotRect().Contains(e.Location) Then
            _dragging = True
            _dragOriginX = e.X
            _dragStartUtc = _windowStartUtc
            Cursor = Cursors.SizeWE
            _toolTip.Hide(Me)
        End If
    End Sub

    Protected Overrides Sub OnMouseUp(e As MouseEventArgs)
        MyBase.OnMouseUp(e)
        If _dragging Then
            _dragging = False
            Cursor = If(_zoomed OrElse (NumericXAxis AndAlso _numericZoomed), Cursors.Hand, Cursors.Default)
        End If
    End Sub

    Protected Overrides Sub OnMouseDoubleClick(e As MouseEventArgs)
        MyBase.OnMouseDoubleClick(e)
        If _zoomed OrElse _numericZoomed Then ResetZoom()
    End Sub

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property PollingIntervalSeconds As Integer
        Get
            Return _pollingIntervalSeconds
        End Get
        Set(value As Integer)
            _pollingIntervalSeconds = Math.Max(1, value)
            Invalidate()
        End Set
    End Property

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property ShowMarkers As Boolean
        Get
            Return _showMarkers
        End Get
        Set(value As Boolean)
            _showMarkers = value
            Invalidate()
        End Set
    End Property

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property ShowGrid As Boolean
        Get
            Return _showGrid
        End Get
        Set(value As Boolean)
            _showGrid = value
            Invalidate()
        End Set
    End Property

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property ChartType As AnalyticsChartType
        Get
            Return _chartType
        End Get
        Set(value As AnalyticsChartType)
            _chartType = value
            Invalidate()
        End Set
    End Property

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        MyBase.OnPaint(e)
        _hitTargets.Clear()
        e.Graphics.Clear(BackColor)
        e.Graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias
        Using border As New Pen(UiTheme.Border, 1.0F)
            e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1))
        End Using
        If Width < 100 OrElse Height < 100 Then Return

        Dim hint = If(NumericXAxis, "Wheel: zoom X - drag: pan - double-click: reset",
            If(_zoomed, "Zoomed – drag to pan • wheel to zoom • double-click to reset", "Wheel: zoom time • double-click: reset"))
        Using titleFont As New Font("Segoe UI", 9.0F, FontStyle.Bold),
              hintFont As New Font("Segoe UI", 8.0F)
            Dim hintWidth = If(ShowInteractionHints AndAlso Width > 640, 340, 0)
            TextRenderer.DrawText(e.Graphics, ChartTitle, titleFont, New Rectangle(12, 5, Math.Max(60, Width - 24 - hintWidth), 22),
                                  UiTheme.Ink, TextFormatFlags.EndEllipsis Or TextFormatFlags.VerticalCenter)
            If hintWidth > 0 Then
                TextRenderer.DrawText(e.Graphics, hint, hintFont, New Rectangle(Width - 12 - hintWidth, 5, hintWidth, 22),
                                      If(_zoomed, UiTheme.Blue, UiTheme.MutedInk),
                                      TextFormatFlags.Right Or TextFormatFlags.VerticalCenter)
            End If
        End Using

        Dim valid = _series.
            Where(Function(item) item IsNot Nothing).
            SelectMany(Function(item) If(item.Points, Array.Empty(Of AnalyticsTrendPoint)()).
                Where(Function(point) point IsNot Nothing AndAlso point.IsValid AndAlso IsFinite(point.Value) AndAlso
                    point.TimestampUtc >= _windowStartUtc AndAlso point.TimestampUtc <= _windowEndUtc AndAlso
                    (Not NumericXAxis OrElse IsFinite(point.XValue))).
                Select(Function(point) New With {.Series = item, .Point = point})).
            ToArray()
        If valid.Length = 0 OrElse _windowEndUtc <= _windowStartUtc Then
            Dim message = If(Not String.IsNullOrWhiteSpace(EmptyDataMessage), EmptyDataMessage,
                If(NumericXAxis, "No matching valid channel pairs in the same saved acquisition.",
                    "No valid timestamped measurements in this period."))
            TextRenderer.DrawText(e.Graphics, message,
                                  Font, New Rectangle(24, 38, Math.Max(1, Width - 48), Math.Max(1, Height - 60)), UiTheme.MutedInk,
                                  TextFormatFlags.HorizontalCenter Or TextFormatFlags.VerticalCenter Or TextFormatFlags.WordBreak)
            Return
        End If

        Dim minValue = valid.Min(Function(item) item.Point.Value.Value)
        Dim maxValue = valid.Max(Function(item) item.Point.Value.Value)
        If _chartType = AnalyticsChartType.ContributionPercent Then
            minValue = 0.0R
            maxValue = 100.0R
        ElseIf maxValue <= minValue Then
            Dim padding = Math.Max(0.5R, Math.Abs(minValue) * 0.05R)
            minValue -= padding
            maxValue += padding
        Else
            Dim padding = (maxValue - minValue) * 0.08R
            minValue -= padding
            maxValue += padding
        End If

        Dim plot = PlotRect()
        If NumericXAxis Then
            _fullMinimumX = valid.Min(Function(item) item.Point.XValue.Value)
            _fullMaximumX = valid.Max(Function(item) item.Point.XValue.Value)
            Dim padding = If(_fullMaximumX > _fullMinimumX, (_fullMaximumX - _fullMinimumX) * 0.08R,
                Math.Max(0.5R, Math.Abs(_fullMinimumX) * 0.05R))
            _fullMinimumX -= padding
            _fullMaximumX += padding
            If Not _numericZoomed Then
                _minimumX = _fullMinimumX
                _maximumX = _fullMaximumX
            End If
        End If
        If NumericXAxis Then
            Dim key = If(ComparisonStyle = ChannelComparisonStyle.MeanResponse,
                "Pale dots: saved pairs   Green: sample mean   Bars: observed min-max   n: sample count",
                If(ComparisonStyle = ChannelComparisonStyle.TimePath,
                    "Chronological path: blue (start) to orange (end); arrows show direction; gaps stay open",
                    "Raw saved pairs; overlapping points appear darker"))
            TextRenderer.DrawText(e.Graphics, key, Font, New Rectangle(74, 30, Math.Max(20, Width - 90), 22),
                UiTheme.MutedInk, TextFormatFlags.EndEllipsis Or TextFormatFlags.VerticalCenter)
        Else
            DrawLegend(e.Graphics)
        End If
        If _chartType = AnalyticsChartType.Donut Then
            DrawDonut(e.Graphics, plot)
            Return
        End If
        DrawGrid(e.Graphics, plot, minValue, maxValue)
        Dim state = e.Graphics.Save()
        e.Graphics.SetClip(plot)
        Dim percentageDrawn = True
        If NumericXAxis Then
            For Each item In _series
                If item Is Nothing Then Continue For
                DrawComparison(e.Graphics, plot, item, minValue, maxValue)
            Next
        ElseIf _chartType = AnalyticsChartType.StackedArea Then
            DrawStackedArea(e.Graphics, plot, minValue, maxValue)
        ElseIf _chartType = AnalyticsChartType.ContributionPercent Then
            percentageDrawn = DrawPercentageStackedArea(e.Graphics, plot)
        Else
            For seriesIndex = 0 To _series.Count - 1
                Dim item = _series(seriesIndex)
                If item IsNot Nothing Then DrawSeries(e.Graphics, plot, item, minValue, maxValue, seriesIndex)
            Next
        End If
        e.Graphics.Restore(state)
        If Not percentageDrawn Then
            TextRenderer.DrawText(e.Graphics,
                "Percentage contribution requires aligned, nonnegative series with a positive total.",
                Font, Rectangle.Round(plot), UiTheme.Red,
                TextFormatFlags.HorizontalCenter Or TextFormatFlags.VerticalCenter Or TextFormatFlags.WordBreak)
        End If
        DrawAxisLabels(e.Graphics, plot)
    End Sub

    Protected Overrides Sub OnMouseMove(e As MouseEventArgs)
        MyBase.OnMouseMove(e)
        If _dragging AndAlso NumericXAxis Then
            Dim span = _maximumX - _minimumX
            SetNumericView(_dragMinimumX - (e.X - _dragOriginX) / CDbl(PlotRect().Width) * span, span)
            Invalidate()
            Return
        End If
        If _dragging Then
            Dim span = _windowEndUtc - _windowStartUtc
            Dim plot = PlotRect()
            Dim shift = TimeSpan.FromTicks(CLng(-(e.X - _dragOriginX) / CDbl(plot.Width) * span.Ticks))
            SetView(_dragStartUtc + shift, span)
            Invalidate()
            RaiseEvent TimeViewChanged(Me, EventArgs.Empty)
            Return
        End If
        Cursor = If(If(NumericXAxis, _numericZoomed, _zoomed) AndAlso PlotRect().Contains(e.Location), Cursors.Hand, Cursors.Default)
        Dim nearest As HitTarget = Nothing
        Dim nearestDistance = 14.0R * 14.0R
        For Each target In _hitTargets
            Dim dx = target.Location.X - e.X
            Dim dy = target.Location.Y - e.Y
            Dim distance = dx * dx + dy * dy
            If distance < nearestDistance Then
                nearest = target
                nearestDistance = distance
            End If
        Next
        If nearest Is Nothing Then
            _toolTip.Hide(Me)
        Else
            Dim value = If(IsFinite(nearest.Point.Value),
                nearest.Point.Value.Value.ToString("0.###", CultureInfo.InvariantCulture), "—")
            Dim units = If(_chartType = AnalyticsChartType.ContributionPercent, "contribution (%)", YAxisLabel)
            _toolTip.Show(If(nearest.Description.Length = 0,
                ShownTime(nearest.Point.TimestampUtc).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) &
                If(_useLocalTime, " (local)", " UTC"), "Observed bin summary") &
                Environment.NewLine & nearest.SeriesName & ": " & value & " " & units &
                If(NumericXAxis, Environment.NewLine & XAxisLabel & ": " &
                    nearest.Point.XValue.GetValueOrDefault().ToString("0.###", CultureInfo.InvariantCulture), "") &
                If(nearest.Description.Length = 0, "", Environment.NewLine & nearest.Description) &
                Environment.NewLine & TimestampDescription,
                Me, e.Location.X + 12, e.Location.Y + 12, 3500)
        End If
    End Sub

    Private Function NumericPoint(x As Double, y As Double, plot As RectangleF, minimum As Double, maximum As Double) As PointF
        Return New PointF(CSng(plot.Left + (x - _minimumX) / (_maximumX - _minimumX) * plot.Width),
            MapY(y, minimum, maximum, plot))
    End Function

    Private Sub DrawComparison(graphics As Graphics, plot As RectangleF, series As AnalyticsTrendSeries,
                               minimum As Double, maximum As Double)
        Dim points = series.Points.Where(Function(point) point IsNot Nothing AndAlso point.IsValid AndAlso
            IsFinite(point.XValue) AndAlso IsFinite(point.Value) AndAlso
            point.TimestampUtc >= _windowStartUtc AndAlso point.TimestampUtc <= _windowEndUtc).
            OrderBy(Function(point) point.TimestampUtc).ToArray()
        Using brush As New SolidBrush(Color.FromArgb(If(ComparisonStyle = ChannelComparisonStyle.MeanResponse, 35, 85), series.Color)),
              pathPen As New Pen(series.Color, 1.4F),
              arrow As New Drawing2D.AdjustableArrowCap(3, 4)
            Dim previous As AnalyticsTrendPoint = Nothing
            For index = 0 To points.Length - 1
                Dim point = points(index)
                Dim location = NumericPoint(point.XValue.Value, point.Value.Value, plot, minimum, maximum)
                If ComparisonStyle = ChannelComparisonStyle.TimePath Then
                    Dim fraction = index / CDbl(Math.Max(1, points.Length - 1))
                    Dim pathColor = Color.FromArgb(220, CInt(36 + 195 * fraction), CInt(107 + 9 * fraction), CInt(210 - 178 * fraction))
                    brush.Color = pathColor
                    pathPen.Color = pathColor
                    Dim gap = If(previous Is Nothing, TimeSpan.Zero, point.TimestampUtc - previous.TimestampUtc)
                    If previous IsNot Nothing AndAlso point.ConnectFromPrevious AndAlso gap > TimeSpan.Zero AndAlso
                        gap.TotalSeconds <= If(series.ExpectedIntervalSeconds, CDbl(_pollingIntervalSeconds)) * 2.5R Then
                        pathPen.EndCap = Drawing2D.LineCap.Flat
                        If index Mod Math.Max(1, points.Length \ 10) = 0 Then pathPen.CustomEndCap = arrow
                        graphics.DrawLine(pathPen, NumericPoint(previous.XValue.Value, previous.Value.Value, plot, minimum, maximum), location)
                    End If
                End If
                graphics.FillEllipse(brush, location.X - 2.0F, location.Y - 2.0F, 4.0F, 4.0F)
                If point.XValue.Value >= _minimumX AndAlso point.XValue.Value <= _maximumX Then
                    _hitTargets.Add(New HitTarget With {.Point = point, .SeriesName = series.Name, .Location = location})
                End If
                previous = point
            Next
        End Using
        If ComparisonStyle <> ChannelComparisonStyle.MeanResponse Then Return
        Dim prior As ChannelComparisonBin = Nothing
        Using meanPen As New Pen(UiTheme.Green, 2.5F), rangePen As New Pen(Color.FromArgb(165, UiTheme.Green), 2.0F),
              meanBrush As New SolidBrush(UiTheme.Green), sparseBrush As New SolidBrush(UiTheme.MutedInk)
            For Each bin In series.ComparisonBins
                Dim location = NumericPoint(bin.MeanX, bin.MeanY, plot, minimum, maximum)
                If prior IsNot Nothing AndAlso prior.Count >= 3 AndAlso bin.Count >= 3 AndAlso bin.Index = prior.Index + 1 Then
                    graphics.DrawLine(meanPen, NumericPoint(prior.MeanX, prior.MeanY, plot, minimum, maximum), location)
                End If
                Dim top = NumericPoint(bin.MeanX, bin.MaximumY, plot, minimum, maximum)
                Dim bottom = NumericPoint(bin.MeanX, bin.MinimumY, plot, minimum, maximum)
                graphics.DrawLine(rangePen, top, bottom)
                graphics.DrawLine(rangePen, top.X - 5, top.Y, top.X + 5, top.Y)
                graphics.DrawLine(rangePen, bottom.X - 5, bottom.Y, bottom.X + 5, bottom.Y)
                graphics.FillEllipse(If(bin.Count >= 3, meanBrush, sparseBrush), location.X - 4.5F, location.Y - 4.5F, 9.0F, 9.0F)
                If location.X >= plot.Left AndAlso location.X <= plot.Right Then
                    TextRenderer.DrawText(graphics, "n=" & bin.Count.ToString(CultureInfo.InvariantCulture), Font,
                        New Rectangle(CInt(location.X - 30), CInt(Math.Max(plot.Top, top.Y - 18)), 60, 16),
                        UiTheme.Green, TextFormatFlags.HorizontalCenter Or TextFormatFlags.VerticalCenter)
                    Dim description = "Summary, not an individual measurement" & Environment.NewLine &
                        "X range: " & bin.MinimumX.ToString("G5", CultureInfo.InvariantCulture) & " to " & bin.MaximumX.ToString("G5", CultureInfo.InvariantCulture) &
                        Environment.NewLine & "Y mean: " & bin.MeanY.ToString("G5", CultureInfo.InvariantCulture) &
                        "; observed range: " & bin.MinimumY.ToString("G5", CultureInfo.InvariantCulture) & " to " & bin.MaximumY.ToString("G5", CultureInfo.InvariantCulture) &
                        "; n=" & bin.Count.ToString(CultureInfo.InvariantCulture)
                    _hitTargets.Insert(0, New HitTarget With {.Point = New AnalyticsTrendPoint With {
                        .TimestampUtc = _windowStartUtc, .XValue = bin.MeanX, .Value = bin.MeanY, .IsValid = True},
                        .Location = location, .SeriesName = "Sample-weighted mean", .Description = description})
                End If
                prior = bin
            Next
        End Using
    End Sub

    Private Sub SetNumericView(minimum As Double, span As Double)
        span = Math.Min(span, _fullMaximumX - _fullMinimumX)
        _minimumX = Math.Clamp(minimum, _fullMinimumX, _fullMaximumX - span)
        _maximumX = _minimumX + span
        _numericZoomed = span < (_fullMaximumX - _fullMinimumX) * 0.999999R
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then _toolTip.Dispose()
        MyBase.Dispose(disposing)
    End Sub

    Private Sub DrawSeries(graphics As Graphics,
                           plot As RectangleF,
                           series As AnalyticsTrendSeries,
                           minimum As Double,
                           maximum As Double,
                           seriesIndex As Integer)
        Dim points = If(series.Points, Array.Empty(Of AnalyticsTrendPoint)()).
            Where(Function(point) point IsNot Nothing AndAlso
                point.TimestampUtc >= _windowStartUtc AndAlso point.TimestampUtc <= _windowEndUtc).
            OrderBy(Function(point) point.TimestampUtc).
            ToArray()
        If points.Length = 0 Then Return

        Dim selected = SelectRenderPointIndices(points)

        Dim gapLimit = TimeSpan.FromSeconds(If(series.ExpectedIntervalSeconds, CDbl(_pollingIntervalSeconds)) * 2.5R)
        ' Rendering fewer points must not turn continuous source readings into apparent gaps.
        Dim breaks(points.Length - 1) As Integer
        Dim communicationGaps(points.Length - 1) As Integer
        Dim priorEvidence = -1
        For index = 0 To points.Length - 1
            If index > 0 Then
                breaks(index) = breaks(index - 1)
                communicationGaps(index) = communicationGaps(index - 1)
            End If
            If series.ConnectCommunicationGaps AndAlso points(index).IsCommunicationGap Then
                communicationGaps(index) += 1
                Continue For
            End If
            If priorEvidence < 0 Then
                priorEvidence = index
                Continue For
            End If
            Dim elapsed = points(index).TimestampUtc - points(priorEvidence).TimestampUtc
            If Not points(priorEvidence).IsValid OrElse Not IsFinite(points(priorEvidence).Value) OrElse
                Not points(index).IsValid OrElse Not IsFinite(points(index).Value) OrElse
                Not points(index).ConnectFromPrevious OrElse elapsed <= TimeSpan.Zero OrElse
                (elapsed > gapLimit AndAlso Not series.ConnectCommunicationGaps) Then breaks(index) += 1
            priorEvidence = index
        Next
        Dim previous As AnalyticsTrendPoint = Nothing
        Dim previousIndex = -1
        Dim reveal = If(_chartType = AnalyticsChartType.Line, _traceRevealUtc, Nothing)
        Dim head As PointF? = Nothing
        Dim graphicsState = graphics.Save()
        If reveal.HasValue Then
            Dim revealX = MapPoint(reveal.Value, minimum, plot, minimum, maximum).X
            graphics.SetClip(New RectangleF(plot.Left, plot.Top, Math.Max(0.0F, Math.Min(plot.Width, revealX - plot.Left)), plot.Height),
                Drawing2D.CombineMode.Intersect)
        End If
        Dim baselineY = MapY(0.0R, minimum, maximum, plot)
        Dim seriesCount = Math.Max(1, _series.Where(Function(candidate) candidate IsNot Nothing).Count())
        Dim columnWidth = Math.Max(1.0F, plot.Width / Math.Max(1, selected.Count) / seriesCount * 0.72F)
        Using stroke As New Pen(series.Color, 1.8F) With {.StartCap = Drawing2D.LineCap.Round, .EndCap = Drawing2D.LineCap.Round},
              gapStroke As New Pen(series.Color, 1.8F) With {.DashStyle = Drawing2D.DashStyle.Dash},
              marker As New SolidBrush(series.Color),
              tracePath As New Drawing2D.GraphicsPath()
            For Each index In selected
                Dim point = points(index)
                If series.ConnectCommunicationGaps AndAlso point.IsCommunicationGap Then Continue For
                If Not point.IsValid OrElse Not IsFinite(point.Value) Then
                    previous = Nothing
                    Continue For
                End If
                Dim location = MapPoint(point.TimestampUtc, point.Value.Value, plot, minimum, maximum)
                If _chartType = AnalyticsChartType.Column AndAlso seriesCount > 1 Then
                    location.X += CSng((seriesIndex - (seriesCount - 1) / 2.0R) * columnWidth)
                End If
                If previous IsNot Nothing Then
                    If breaks(index) <> breaks(previousIndex) Then
                        previous = Nothing
                    End If
                End If
                If previous Is Nothing Then tracePath.StartFigure()

                If _chartType = AnalyticsChartType.Scatter Then
                    Using brush As New SolidBrush(Color.FromArgb(135, series.Color))
                        graphics.FillEllipse(brush, location.X - 2.0F, location.Y - 2.0F, 4.0F, 4.0F)
                    End Using
                ElseIf _chartType = AnalyticsChartType.Column Then
                    Dim top = Math.Min(location.Y, baselineY)
                    Dim height = Math.Max(1.0F, Math.Abs(baselineY - location.Y))
                    Using brush As New SolidBrush(Color.FromArgb(185, series.Color))
                        graphics.FillRectangle(brush, location.X - columnWidth / 2.0F, top, columnWidth, height)
                    End Using
                ElseIf _chartType = AnalyticsChartType.Area Then
                    If previous IsNot Nothing Then
                        Dim priorLocation = MapPoint(previous.TimestampUtc, previous.Value.Value, plot, minimum, maximum)
                        Dim polygon = {
                            New PointF(priorLocation.X, baselineY),
                            priorLocation,
                            location,
                            New PointF(location.X, baselineY)
                        }
                        Using brush As New SolidBrush(Color.FromArgb(38, series.Color))
                            graphics.FillPolygon(brush, polygon)
                        End Using
                        graphics.DrawLine(stroke, priorLocation, location)
                    End If
                ElseIf previous IsNot Nothing Then
                    Dim priorLocation = MapPoint(previous.TimestampUtc, previous.Value.Value, plot, minimum, maximum)
                    Dim bridge = series.ConnectCommunicationGaps AndAlso
                        (communicationGaps(index) <> communicationGaps(previousIndex) OrElse point.TimestampUtc - previous.TimestampUtc > gapLimit)
                    If _chartType = AnalyticsChartType.StepLine OrElse series.IsState Then
                        graphics.DrawLine(If(bridge, gapStroke, stroke), priorLocation, New PointF(location.X, priorLocation.Y))
                        graphics.DrawLine(If(bridge, gapStroke, stroke), New PointF(location.X, priorLocation.Y), location)
                    ElseIf reveal.HasValue AndAlso Not bridge Then
                        tracePath.AddLine(priorLocation, location)
                    Else
                        If bridge Then tracePath.StartFigure()
                        graphics.DrawLine(If(bridge, gapStroke, stroke), priorLocation, location)
                    End If
                End If

                If _showMarkers AndAlso (selected.Count <= 120 OrElse previous Is Nothing) AndAlso
                    _chartType <> AnalyticsChartType.Column AndAlso _chartType <> AnalyticsChartType.Scatter Then
                    graphics.FillEllipse(marker, location.X - 2.0F, location.Y - 2.0F, 4.0F, 4.0F)
                End If
                If Not reveal.HasValue OrElse point.TimestampUtc <= reveal.Value Then
                    _hitTargets.Add(New HitTarget With {
                        .Point = point,
                        .SeriesName = series.Name,
                        .Location = location
                    })
                    head = location
                ElseIf previous IsNot Nothing AndAlso previous.TimestampUtc <= reveal.Value Then
                    Dim fraction = (reveal.Value - previous.TimestampUtc).TotalSeconds / (point.TimestampUtc - previous.TimestampUtc).TotalSeconds
                    Dim priorLocation = MapPoint(previous.TimestampUtc, previous.Value.Value, plot, minimum, maximum)
                    head = New PointF(CSng(priorLocation.X + (location.X - priorLocation.X) * fraction),
                        If(series.IsState, priorLocation.Y, CSng(priorLocation.Y + (location.Y - priorLocation.Y) * fraction)))
                End If
                previous = point
                previousIndex = index
                If reveal.HasValue AndAlso point.TimestampUtc > reveal.Value Then Exit For
            Next
            If tracePath.PointCount > 1 Then graphics.DrawPath(stroke, tracePath)
        End Using
        graphics.Restore(graphicsState)
        If reveal.HasValue AndAlso head.HasValue Then
            Using brush As New SolidBrush(series.Color)
                graphics.FillEllipse(brush, head.Value.X - 3.0F, head.Value.Y - 3.0F, 6.0F, 6.0F)
            End Using
        End If
    End Sub

    Private Sub DrawStackedArea(graphics As Graphics, plot As RectangleF, minimum As Double, maximum As Double)
        Dim series = _series.Where(Function(item) item IsNot Nothing).
            Select(Function(item) New With {
                .Definition = item,
                .Points = If(item.Points, Array.Empty(Of AnalyticsTrendPoint)()).
                    Where(Function(point) point IsNot Nothing AndAlso point.TimestampUtc >= _windowStartUtc AndAlso
                        point.TimestampUtc <= _windowEndUtc).
                    OrderBy(Function(point) point.TimestampUtc).ToArray()
            }).ToArray()
        If series.Length = 0 Then Return
        Dim timestamps = series(0).Points.Select(Function(point) point.TimestampUtc).ToArray()
        If series.Any(Function(item) item.Points.Length <> timestamps.Length OrElse
            Not item.Points.Select(Function(point) point.TimestampUtc).SequenceEqual(timestamps)) Then
            TextRenderer.DrawText(graphics, "Stacked area requires timestamp-aligned series.",
                Font, ClientRectangle, UiTheme.Red, TextFormatFlags.HorizontalCenter Or TextFormatFlags.Bottom)
            Return
        End If

        Dim gapLimit = TimeSpan.FromSeconds(_pollingIntervalSeconds * 2.5R)
        For pointIndex = 1 To timestamps.Length - 1
            Dim hasInvalidPoint = False
            For Each item In series
                If Not item.Points(pointIndex - 1).IsValid OrElse Not IsFinite(item.Points(pointIndex - 1).Value) OrElse
                   Not item.Points(pointIndex).IsValid OrElse Not IsFinite(item.Points(pointIndex).Value) Then
                    hasInvalidPoint = True
                    Exit For
                End If
            Next
            If timestamps(pointIndex) - timestamps(pointIndex - 1) > gapLimit OrElse hasInvalidPoint Then Continue For
            Dim positivePrevious = 0.0R
            Dim positiveCurrent = 0.0R
            Dim negativePrevious = 0.0R
            Dim negativeCurrent = 0.0R
            For Each item In series
                Dim valuePrevious = item.Points(pointIndex - 1).Value.Value
                Dim valueCurrent = item.Points(pointIndex).Value.Value
                Dim lowerPrevious As Double
                Dim lowerCurrent As Double
                Dim upperPrevious As Double
                Dim upperCurrent As Double
                If valuePrevious >= 0 Then
                    lowerPrevious = positivePrevious
                    positivePrevious += valuePrevious
                    upperPrevious = positivePrevious
                Else
                    lowerPrevious = negativePrevious
                    negativePrevious += valuePrevious
                    upperPrevious = negativePrevious
                End If
                If valueCurrent >= 0 Then
                    lowerCurrent = positiveCurrent
                    positiveCurrent += valueCurrent
                    upperCurrent = positiveCurrent
                Else
                    lowerCurrent = negativeCurrent
                    negativeCurrent += valueCurrent
                    upperCurrent = negativeCurrent
                End If
                Dim first = MapPoint(timestamps(pointIndex - 1), upperPrevious, plot, minimum, maximum)
                Dim second = MapPoint(timestamps(pointIndex), upperCurrent, plot, minimum, maximum)
                Dim lowerSecond = MapPoint(timestamps(pointIndex), lowerCurrent, plot, minimum, maximum)
                Dim lowerFirst = MapPoint(timestamps(pointIndex - 1), lowerPrevious, plot, minimum, maximum)
                Using brush As New SolidBrush(Color.FromArgb(105, item.Definition.Color))
                    graphics.FillPolygon(brush, {first, second, lowerSecond, lowerFirst})
                End Using
                Using pen As New Pen(item.Definition.Color, 1.4F)
                    graphics.DrawLine(pen, first, second)
                End Using
            Next
        Next
    End Sub

    Private Function DrawPercentageStackedArea(graphics As Graphics, plot As RectangleF) As Boolean
        Dim series = _series.Where(Function(item) item IsNot Nothing).
            Select(Function(item) New With {
                .Definition = item,
                .Points = If(item.Points, Array.Empty(Of AnalyticsTrendPoint)()).
                    Where(Function(point) point IsNot Nothing AndAlso point.TimestampUtc >= _windowStartUtc AndAlso
                        point.TimestampUtc <= _windowEndUtc).
                    OrderBy(Function(point) point.TimestampUtc).ToArray()
            }).ToArray()
        If series.Length = 0 Then Return False
        Dim timestamps = series(0).Points.Select(Function(point) point.TimestampUtc).ToArray()
        If series.Any(Function(item) item.Points.Length <> timestamps.Length OrElse
            Not item.Points.Select(Function(point) point.TimestampUtc).SequenceEqual(timestamps)) Then Return False
        For Each item In series
            For Each point In item.Points
                If point.IsValid AndAlso IsFinite(point.Value) AndAlso point.Value.Value < 0 Then Return False
            Next
        Next

        Dim gapLimit = TimeSpan.FromSeconds(_pollingIntervalSeconds * 2.5R)
        For pointIndex = 1 To timestamps.Length - 1
            If timestamps(pointIndex) - timestamps(pointIndex - 1) > gapLimit Then Continue For
            Dim previousTotal = 0.0R
            Dim currentTotal = 0.0R
            Dim hasInvalidPoint = False
            For Each item In series
                Dim previousPoint = item.Points(pointIndex - 1)
                Dim currentPoint = item.Points(pointIndex)
                If Not previousPoint.IsValid OrElse Not IsFinite(previousPoint.Value) OrElse
                   Not currentPoint.IsValid OrElse Not IsFinite(currentPoint.Value) Then
                    hasInvalidPoint = True
                    Exit For
                End If
                previousTotal += previousPoint.Value.Value
                currentTotal += currentPoint.Value.Value
            Next
            If hasInvalidPoint Then Continue For
            If previousTotal <= 0 OrElse currentTotal <= 0 Then Continue For

            Dim previousCumulative = 0.0R
            Dim currentCumulative = 0.0R
            For Each item In series
                Dim previousLower = previousCumulative
                Dim currentLower = currentCumulative
                previousCumulative += item.Points(pointIndex - 1).Value.Value / previousTotal * 100.0R
                currentCumulative += item.Points(pointIndex).Value.Value / currentTotal * 100.0R
                Dim upperPrevious = MapPoint(timestamps(pointIndex - 1), previousCumulative,
                    plot, 0.0R, 100.0R)
                Dim upperCurrent = MapPoint(timestamps(pointIndex), currentCumulative,
                    plot, 0.0R, 100.0R)
                Dim lowerCurrentPoint = MapPoint(timestamps(pointIndex), currentLower,
                    plot, 0.0R, 100.0R)
                Dim lowerPreviousPoint = MapPoint(timestamps(pointIndex - 1), previousLower,
                    plot, 0.0R, 100.0R)
                Using brush As New SolidBrush(Color.FromArgb(110, item.Definition.Color))
                    graphics.FillPolygon(brush, {upperPrevious, upperCurrent, lowerCurrentPoint, lowerPreviousPoint})
                End Using
                Using pen As New Pen(item.Definition.Color, 1.2F)
                    graphics.DrawLine(pen, upperPrevious, upperCurrent)
                End Using
            Next
        Next
        Return True
    End Function

    Private Sub DrawDonut(graphics As Graphics, plot As RectangleF)
        Dim timestamp As DateTime
        Dim values As Double() = Array.Empty(Of Double)()
        Dim total As Double
        Dim availableSeries = _series.Where(Function(item) item IsNot Nothing).ToArray()
        If Not TryGetLatestAlignedValues(timestamp, values, total) OrElse values.Any(Function(value) value < 0) OrElse total <= 0 Then
            TextRenderer.DrawText(graphics,
                "Donut requires nonnegative contributions and a positive total at the latest aligned cycle.",
                Font, Rectangle.Round(plot), UiTheme.MutedInk,
                TextFormatFlags.HorizontalCenter Or TextFormatFlags.VerticalCenter Or TextFormatFlags.WordBreak)
            Return
        End If

        Dim diameter = Math.Min(plot.Width * 0.72F, plot.Height * 0.86F)
        Dim bounds As New RectangleF(plot.Left + (plot.Width - diameter) / 2.0F,
                                     plot.Top + (plot.Height - diameter) / 2.0F,
                                     diameter, diameter)
        Dim startAngle = -90.0F
        For index = 0 To availableSeries.Length - 1
            Dim sweep = CSng(values(index) / total * 360.0R)
            Using brush As New SolidBrush(Color.FromArgb(205, availableSeries(index).Color))
                graphics.FillPie(brush, bounds, startAngle, sweep)
            End Using
            startAngle += sweep
        Next

        Dim holeDiameter = diameter * 0.56F
        Dim hole As New RectangleF(bounds.Left + (diameter - holeDiameter) / 2.0F,
                                   bounds.Top + (diameter - holeDiameter) / 2.0F,
                                   holeDiameter, holeDiameter)
        Using brush As New SolidBrush(BackColor)
            graphics.FillEllipse(brush, hole)
        End Using
        Using centerFont As New Font("Segoe UI", 10.0F, FontStyle.Bold)
            TextRenderer.DrawText(graphics, total.ToString("0.###", CultureInfo.InvariantCulture) & " kW",
                centerFont, Rectangle.Round(hole), UiTheme.Ink,
                TextFormatFlags.HorizontalCenter Or TextFormatFlags.VerticalCenter)
        End Using
        TextRenderer.DrawText(graphics,
            "Latest aligned cycle: " & timestamp.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
            Font, New Rectangle(CInt(plot.Left), CInt(plot.Bottom + 2), CInt(plot.Width), 18),
            Color.FromArgb(81, 98, 114), TextFormatFlags.HorizontalCenter Or TextFormatFlags.VerticalCenter)
    End Sub

    Private Function TryGetLatestAlignedValues(ByRef timestamp As DateTime,
                                               ByRef values As Double(),
                                               ByRef total As Double) As Boolean
        values = Array.Empty(Of Double)()
        total = 0.0R
        timestamp = DateTime.MinValue
        Dim available = _series.Where(Function(item) item IsNot Nothing).ToArray()
        If available.Length < 2 Then Return False
        Dim candidateTimes = available(0).Points.
            Where(Function(point) point IsNot Nothing AndAlso point.TimestampUtc >= _windowStartUtc AndAlso
                point.TimestampUtc <= _windowEndUtc).
            Select(Function(point) point.TimestampUtc).
            Distinct().
            OrderByDescending(Function(value) value)
        For Each candidate In candidateTimes
            Dim candidateValues As New List(Of Double)()
            Dim complete = True
            For Each item In available
                Dim point = item.Points.FirstOrDefault(Function(value) value IsNot Nothing AndAlso
                    value.TimestampUtc = candidate)
                If point Is Nothing OrElse Not point.IsValid OrElse Not IsFinite(point.Value) Then
                    complete = False
                    Exit For
                End If
                candidateValues.Add(point.Value.Value)
            Next
            If Not complete Then Continue For
            timestamp = candidate
            values = candidateValues.ToArray()
            total = values.Sum()
            If Double.IsFinite(total) Then Return True
        Next
        Return False
    End Function

    Private Shared Function SelectRenderPointIndices(points As IReadOnlyList(Of AnalyticsTrendPoint)) As List(Of Integer)
        If points.Count <= MaximumRenderedPoints Then Return Enumerable.Range(0, points.Count).ToList()
        Dim selectedIndices As New SortedSet(Of Integer) From {0, points.Count - 1}
        Dim bucketCount = Math.Max(1, MaximumRenderedPoints \ 2)
        Dim bucketSize = CInt(Math.Ceiling(points.Count / CDbl(bucketCount)))
        For bucketStart = 0 To points.Count - 1 Step bucketSize
            Dim bucketEnd = Math.Min(points.Count - 1, bucketStart + bucketSize - 1)
            Dim minimumIndex = -1
            Dim maximumIndex = -1
            For index = bucketStart To bucketEnd
                Dim point = points(index)
                If Not point.IsValid OrElse Not IsFinite(point.Value) Then
                    selectedIndices.Add(index)
                    Continue For
                End If
                If minimumIndex < 0 OrElse point.Value.Value < points(minimumIndex).Value.Value Then minimumIndex = index
                If maximumIndex < 0 OrElse point.Value.Value > points(maximumIndex).Value.Value Then maximumIndex = index
            Next
            If minimumIndex >= 0 Then selectedIndices.Add(minimumIndex)
            If maximumIndex >= 0 Then selectedIndices.Add(maximumIndex)
        Next
        Return selectedIndices.ToList()
    End Function

    Private Shared Function FormatAxisTick(value As Double, stepSize As Double) As String
        If Math.Abs(value) < stepSize * 0.00000001R Then value = 0
        Dim decimals = CInt(Math.Max(0, Math.Ceiling(-Math.Log10(stepSize))))
        Dim format = If(decimals = 0, "#,##0",
            If(decimals <= 10, "0." & New String("0"c, decimals), "G17"))
        Return value.ToString(format, CultureInfo.InvariantCulture)
    End Function

    Private Sub DrawGrid(graphics As Graphics, plot As RectangleF, minimum As Double, maximum As Double)
        Using gridPen As New Pen(Color.FromArgb(222, 232, 240), 1.0F),
              axisPen As New Pen(Color.FromArgb(116, 139, 157), 1.0F),
              labelFont As New Font("Segoe UI", 8.0F)
            Dim yStep = NiceStep((maximum - minimum) / Math.Max(3.0R, plot.Height / 17.0R))
            Dim firstY = Math.Ceiling(minimum / yStep) * yStep
            Dim yValue = firstY
            Dim guard = 0
            While yValue <= maximum + yStep * 0.001R AndAlso             guard < 120
                Dim y = MapY(yValue, minimum, maximum, plot)
                If _showGrid Then graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y)
                TextRenderer.DrawText(graphics, FormatAxisTick(yValue, yStep), labelFont,
                    New Rectangle(2, CInt(y - 8), 64, 16), UiTheme.MutedInk,
                    TextFormatFlags.Right Or TextFormatFlags.VerticalCenter)
                yValue += yStep
                guard += 1
            End While

            If NumericXAxis Then
                Dim stepX = NiceStep((_maximumX - _minimumX) / Math.Max(2.0R, plot.Width / 90.0R))
                Dim xValue = Math.Ceiling(_minimumX / stepX) * stepX
                Dim xGuard = 0
                While xValue <= _maximumX AndAlso xGuard < 100
                    Dim x = CSng(plot.Left + (xValue - _minimumX) / (_maximumX - _minimumX) * plot.Width)
                    If _showGrid Then graphics.DrawLine(gridPen, x, plot.Top, x, plot.Bottom)
                    TextRenderer.DrawText(graphics, xValue.ToString("G5", CultureInfo.InvariantCulture), labelFont,
                        New Rectangle(CInt(x - 40), CInt(plot.Bottom + 2), 80, 16), UiTheme.MutedInk,
                        TextFormatFlags.HorizontalCenter Or TextFormatFlags.VerticalCenter)
                    xValue += stepX
                    xGuard += 1
                End While
                graphics.DrawRectangle(axisPen, plot.X, plot.Y, plot.Width, plot.Height)
                Return
            End If
            Dim spanSeconds = (_windowEndUtc - _windowStartUtc).TotalSeconds
            Dim steps = {1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400, 172800, 604800}
            Dim shownStart = ShownTime(_windowStartUtc)
            Dim shownEnd = ShownTime(_windowEndUtc)
            Dim crossesDay = shownStart.Date <> shownEnd.Date
            Dim stepSeconds = steps(steps.Length - 1)
            For Each candidate In steps
                Dim width = If(candidate < 60, 52, If(candidate >= 86400, 50, If(crossesDay, 92, 40)))
                If spanSeconds / candidate <= Math.Max(2.0R, plot.Width / (width + 8.0R)) Then
                    stepSeconds = candidate
                    Exit For
                End If
            Next
            Dim tickFormat = If(stepSeconds < 60, "HH:mm:ss", If(stepSeconds >= 86400, "dd MMM", If(crossesDay, "dd MMM HH:mm", "HH:mm")))
            Dim labelWidth = If(tickFormat = "dd MMM HH:mm", 92, If(tickFormat = "HH:mm:ss", 52, If(tickFormat = "dd MMM", 50, 40)))
            Dim origin As New DateTime(shownStart.Year, shownStart.Month, shownStart.Day, 0, 0, 0, shownStart.Kind)
            Dim firstTick = origin.AddSeconds(Math.Ceiling((shownStart - origin).TotalSeconds / stepSeconds) * stepSeconds)
            Dim tickTime = firstTick
            guard = 0
            While tickTime <= shownEnd AndAlso             guard < 200
                Dim fraction = (tickTime - shownStart).TotalSeconds / spanSeconds
                Dim x = CSng(plot.Left + plot.Width * fraction)
                If _showGrid Then graphics.DrawLine(gridPen, x, plot.Top, x, plot.Bottom)
                Dim left = Math.Min(CInt(plot.Right) - labelWidth + 10, Math.Max(CInt(plot.Left) - 10, CInt(x - labelWidth / 2.0F)))
                TextRenderer.DrawText(graphics, tickTime.ToString(tickFormat, CultureInfo.InvariantCulture),
                    labelFont, New Rectangle(left, CInt(plot.Bottom + 2), labelWidth, 16), UiTheme.MutedInk,
                    TextFormatFlags.HorizontalCenter Or TextFormatFlags.VerticalCenter)
                tickTime = tickTime.AddSeconds(stepSeconds)
                guard += 1
            End While
            graphics.DrawRectangle(axisPen, plot.X, plot.Y, plot.Width, plot.Height)
        End Using
    End Sub

    Private Shared Function NiceStep(rough As Double) As Double
        If rough <= 0 OrElse Double.IsNaN(rough) OrElse Double.IsInfinity(rough) Then Return 1.0R
        Dim magnitude = Math.Pow(10.0R, Math.Floor(Math.Log10(rough)))
        Dim normalized = rough / magnitude
        Dim nice = If(normalized <= 1.0R, 1.0R, If(normalized <= 2.0R, 2.0R, If(normalized <= 5.0R, 5.0R, 10.0R)))
        Return nice * magnitude
    End Function

    Private Sub DrawAxisLabels(graphics As Graphics, plot As RectangleF)
        Using font As New Font("Segoe UI", 8.0F, FontStyle.Bold)
            TextRenderer.DrawText(graphics, XAxisLabel, font,
                New Rectangle(CInt(plot.Left), Height - 20, CInt(plot.Width), 16), UiTheme.MutedInk,
                TextFormatFlags.HorizontalCenter Or TextFormatFlags.VerticalCenter)
            Dim state = graphics.Save()
            graphics.TranslateTransform(15.0F, plot.Top + plot.Height / 2.0F)
            graphics.RotateTransform(-90.0F)
            Using brush As New SolidBrush(UiTheme.MutedInk),
                  format As New StringFormat With {
                      .Alignment = StringAlignment.Center,
                      .LineAlignment = StringAlignment.Center,
                      .Trimming = StringTrimming.EllipsisCharacter
                  }
                Dim label = If(_chartType = AnalyticsChartType.ContributionPercent,
                    "Transformer contribution (%)", YAxisLabel)
                graphics.DrawString(label, font, brush,
                    New RectangleF(-plot.Height / 2.0F, -9.0F, plot.Height, 18.0F), format)
            End Using
            graphics.Restore(state)
        End Using
    End Sub

    Private Sub DrawLegend(graphics As Graphics)
        If _series.Count < 2 Then Return
        Using font As New Font("Segoe UI", 8.0F)
            Dim items = _series.Where(Function(candidate) candidate IsNot Nothing).ToArray()
            Dim columns = Math.Min(items.Length, LegendColumns())
            Dim itemWidth = Math.Max(20.0F, (Width - 96.0F) / columns)
            For index = 0 To items.Length - 1
                Dim item = items(index)
                Dim x = 78 + CInt((index Mod columns) * itemWidth)
                Dim y = 33 + (index \ columns) * 22
                Using pen As New Pen(item.Color, 2.0F)
                    graphics.DrawLine(pen, x, y + 9, x + 14, y + 9)
                End Using
                TextRenderer.DrawText(graphics, item.Name, font,
                    New Rectangle(x + 18, y, Math.Max(1, CInt(itemWidth) - 22), 18), UiTheme.MutedInk,
                    TextFormatFlags.EndEllipsis Or TextFormatFlags.VerticalCenter)
            Next
        End Using
    End Sub

    Private Function MapPoint(timestamp As DateTime,
                              value As Double,
                              plot As RectangleF,
                              minimum As Double,
                              maximum As Double) As PointF
        Dim timeRange = (_windowEndUtc - _windowStartUtc).Ticks
        Dim fraction = If(timeRange > 0, (timestamp - _windowStartUtc).Ticks / CDbl(timeRange), 0.5R)
        Return New PointF(CSng(plot.Left + Math.Clamp(fraction, 0.0R, 1.0R) * plot.Width),
                          MapY(value, minimum, maximum, plot))
    End Function

    Private Shared Function MapY(value As Double, minimum As Double, maximum As Double, plot As RectangleF) As Single
        Dim fraction = If(maximum > minimum, (value - minimum) / (maximum - minimum), 0.5R)
        Return CSng(plot.Bottom - Math.Clamp(fraction, 0.0R, 1.0R) * plot.Height)
    End Function

    Private Shared Function IsFinite(value As Double?) As Boolean
        Return value.HasValue AndAlso Double.IsFinite(value.Value)
    End Function
End Class
