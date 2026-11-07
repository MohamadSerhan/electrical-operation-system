Imports System.ComponentModel
Imports System.Drawing
Imports System.Globalization
Imports System.Linq
Imports System.Windows.Forms

Public NotInheritable Class AnalyticsScatterChart
    Inherits Control

    Private Const MaximumRenderedPoints As Integer = 20000
    Private ReadOnly _toolTip As New ToolTip()
    Private _points As IReadOnlyList(Of AnalyticsPoint) = Array.Empty(Of AnalyticsPoint)()
    Private _renderedPoints As New List(Of AnalyticsPoint)()
    Private _regression As RegressionSummary
    Private _seriesTrendlines As IReadOnlyDictionary(Of String, RegressionSummary) =
        New Dictionary(Of String, RegressionSummary)(StringComparer.Ordinal)
    Private _showTrendline As Boolean
    Private _showMarkers As Boolean = True
    Private _showDensity As Boolean
    Private _showGrid As Boolean = True
    Private _chartType As AnalyticsChartType = AnalyticsChartType.Scatter
    Private _pollingIntervalSeconds As Integer = 10

    Public Sub New()
        DoubleBuffered = True
        BackColor = UiTheme.Surface
        ForeColor = UiTheme.Ink
        Font = New Font("Segoe UI", 8.0F)
        MinimumSize = New Size(280, 190)
        SetStyle(ControlStyles.ResizeRedraw Or ControlStyles.UserPaint Or ControlStyles.AllPaintingInWmPaint, True)
    End Sub

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property ChartTitle As String = String.Empty
    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property XAxisLabel As String = String.Empty
    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property YAxisLabel As String = String.Empty

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property ChartType As AnalyticsChartType
        Get
            Return _chartType
        End Get
        Set(value As AnalyticsChartType)
            _chartType = AnalyticsChartType.Scatter
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
    Public Property ShowDensity As Boolean
        Get
            Return _showDensity
        End Get
        Set(value As Boolean)
            _showDensity = value
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
    Public Property Points As IReadOnlyList(Of AnalyticsPoint)
        Get
            Return _points
        End Get
        Set(value As IReadOnlyList(Of AnalyticsPoint))
            _points = If(value, Array.Empty(Of AnalyticsPoint)())
            _regression = ElectricalAnalytics.CalculateRegression(_points)
            Invalidate()
        End Set
    End Property

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property ShowTrendline As Boolean
        Get
            Return _showTrendline
        End Get
        Set(value As Boolean)
            _showTrendline = value
            Invalidate()
        End Set
    End Property

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property SeriesTrendlines As IReadOnlyDictionary(Of String, RegressionSummary)
        Get
            Return _seriesTrendlines
        End Get
        Set(value As IReadOnlyDictionary(Of String, RegressionSummary))
            _seriesTrendlines = If(value, New Dictionary(Of String, RegressionSummary)(StringComparer.Ordinal))
            Invalidate()
        End Set
    End Property

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        MyBase.OnPaint(e)
        e.Graphics.Clear(BackColor)
        e.Graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias
        Using border As New Pen(UiTheme.Border, 1.0F)
            e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1))
        End Using
        If Width < 80 OrElse Height < 80 Then Return

        Using titleFont As New Font("Segoe UI", 9.0F, FontStyle.Bold)
            TextRenderer.DrawText(e.Graphics, ChartTitle, titleFont, New Rectangle(12, 5, Width - 24, 22),
                                  UiTheme.Ink, TextFormatFlags.EndEllipsis Or TextFormatFlags.VerticalCenter)
        End Using

        Dim valid = _points.Where(Function(point) point IsNot Nothing AndAlso
                                  Double.IsFinite(point.X) AndAlso Double.IsFinite(point.Y)).ToArray()
        If valid.Length = 0 Then
            TextRenderer.DrawText(e.Graphics, "No valid paired measurements in the selected period.",
                                  Font, ClientRectangle, UiTheme.MutedInk,
                                  TextFormatFlags.HorizontalCenter Or TextFormatFlags.VerticalCenter Or TextFormatFlags.WordBreak)
            _renderedPoints.Clear()
            Return
        End If

        Dim plot = New RectangleF(68.0F, 58.0F, Math.Max(20.0F, Width - 90.0F), Math.Max(20.0F, Height - 114.0F))
        DrawLegend(e.Graphics, valid)
        Dim minX = valid.Min(Function(point) point.X)
        Dim maxX = valid.Max(Function(point) point.X)
        Dim minY = valid.Min(Function(point) point.Y)
        Dim maxY = valid.Max(Function(point) point.Y)
        ExpandRange(minX, maxX)
        ExpandRange(minY, maxY)
        DrawGrid(e.Graphics, plot, minX, maxX, minY, maxY)

        If _showTrendline Then
            If _seriesTrendlines.Count > 0 Then
                For Each entry In _seriesTrendlines
                    Dim series = valid.Where(Function(point) String.Equals(point.SeriesName, entry.Key, StringComparison.Ordinal)).ToArray()
                    If series.Length = 0 OrElse entry.Value Is Nothing Then Continue For
                    DrawTrendline(e.Graphics, entry.Value, series.Min(Function(point) point.X),
                                  series.Max(Function(point) point.X), plot, minX, maxX, minY, maxY,
                                  GetSeriesColor(entry.Key))
                Next
            ElseIf _regression IsNot Nothing Then
                DrawTrendline(e.Graphics, _regression, minX, maxX, plot, minX, maxX, minY, maxY,
                              Color.FromArgb(220, 166, 72))
            End If
        End If

        Dim stride = Math.Max(1, CInt(Math.Ceiling(valid.Length / CDbl(MaximumRenderedPoints))))
        _renderedPoints = valid.Where(Function(point, index) index Mod stride = 0).ToList()
        Dim state = e.Graphics.Save()
        e.Graphics.SetClip(plot)
        If _showDensity Then
            DrawDensity(e.Graphics, plot, valid, minX, maxX, minY, maxY)
        Else
            DrawDataSeries(e.Graphics, plot, minX, maxX, minY, maxY)
        End If
        e.Graphics.Restore(state)
        DrawAxisLabels(e.Graphics, plot)
    End Sub

    Protected Overrides Sub OnMouseMove(e As MouseEventArgs)
        MyBase.OnMouseMove(e)
        Dim nearest As AnalyticsPoint = Nothing
        Dim nearestDistance = 12.0R * 12.0R
        Dim plot = New RectangleF(68.0F, 58.0F, Math.Max(20.0F, Width - 90.0F), Math.Max(20.0F, Height - 114.0F))
        If _renderedPoints.Count > 0 Then
            Dim minX = _points.Where(Function(point) point IsNot Nothing AndAlso Double.IsFinite(point.X)).Min(Function(point) point.X)
            Dim maxX = _points.Where(Function(point) point IsNot Nothing AndAlso Double.IsFinite(point.X)).Max(Function(point) point.X)
            Dim minY = _points.Where(Function(point) point IsNot Nothing AndAlso Double.IsFinite(point.Y)).Min(Function(point) point.Y)
            Dim maxY = _points.Where(Function(point) point IsNot Nothing AndAlso Double.IsFinite(point.Y)).Max(Function(point) point.Y)
            ExpandRange(minX, maxX)
            ExpandRange(minY, maxY)
            For Each point In _renderedPoints
                Dim position = MapPoint(point.X, point.Y, plot, minX, maxX, minY, maxY)
                Dim dx = position.X - e.X
                Dim dy = position.Y - e.Y
                Dim distance = dx * dx + dy * dy
                If distance < nearestDistance Then
                    nearestDistance = distance
                    nearest = point
                End If
            Next
        End If

        If nearest Is Nothing Then
            _toolTip.Hide(Me)
        Else
            _toolTip.Show(String.Format(CultureInfo.InvariantCulture,
                "{0} UTC{1}{2}: X={3:0.###}, Y={4:0.###}",
                nearest.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Environment.NewLine, nearest.SeriesName, nearest.X, nearest.Y) &
                If(nearest.IsObservedMaximum, Environment.NewLine & "Highest observed in selection; not a threshold alarm.", String.Empty) &
                If(String.IsNullOrWhiteSpace(nearest.Detail), String.Empty, Environment.NewLine & nearest.Detail),
                Me, e.Location.X + 12, e.Location.Y + 12, 4000)
        End If
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then _toolTip.Dispose()
        MyBase.Dispose(disposing)
    End Sub

    Private Shared Sub ExpandRange(ByRef minimum As Double, ByRef maximum As Double)
        If maximum > minimum Then
            Dim padding = (maximum - minimum) * 0.06R
            minimum -= padding
            maximum += padding
        Else
            Dim padding = Math.Max(0.5R, Math.Abs(minimum) * 0.05R)
            minimum -= padding
            maximum += padding
        End If
    End Sub

    Private Sub DrawDensity(graphics As Graphics, plot As RectangleF,
                            points As IReadOnlyList(Of AnalyticsPoint),
                            minX As Double, maxX As Double, minY As Double, maxY As Double)
        Const columns As Integer = 48
        Const rows As Integer = 28
        For Each group In points.GroupBy(Function(point) point.SeriesName, StringComparer.Ordinal)
            Dim counts(columns - 1, rows - 1) As Integer
            Dim maximumCount = 0
            For Each point In group
                Dim xFraction = If(maxX > minX, (point.X - minX) / (maxX - minX), 0.5R)
                Dim yFraction = If(maxY > minY, (point.Y - minY) / (maxY - minY), 0.5R)
                Dim column = Math.Clamp(CInt(Math.Floor(xFraction * columns)), 0, columns - 1)
                Dim row = Math.Clamp(CInt(Math.Floor((1.0R - yFraction) * rows)), 0, rows - 1)
                counts(column, row) += 1
                maximumCount = Math.Max(maximumCount, counts(column, row))
            Next
            If maximumCount = 0 Then Continue For
            Dim denominator = Math.Log(1.0R + maximumCount)
            For row = 0 To rows - 1
                For column = 0 To columns - 1
                    Dim count = counts(column, row)
                    If count = 0 Then Continue For
                    Dim opacity = CInt(32.0R + Math.Log(1.0R + count) / denominator * 178.0R)
                    Dim rectangle As New RectangleF(
                        plot.Left + plot.Width * column / columns,
                        plot.Top + plot.Height * row / rows,
                        plot.Width / columns + 0.5F,
                        plot.Height / rows + 0.5F)
                    Using brush As New SolidBrush(Color.FromArgb(opacity, GetSeriesColor(group.Key)))
                        graphics.FillRectangle(brush, rectangle)
                    End Using
                Next
            Next
        Next
    End Sub

    Private Sub DrawGrid(graphics As Graphics, plot As RectangleF, minX As Double, maxX As Double, minY As Double, maxY As Double)
        Using gridPen As New Pen(Color.FromArgb(222, 232, 240), 1.0F),
              axisPen As New Pen(Color.FromArgb(116, 139, 157), 1.0F),
              labelFont As New Font("Segoe UI", 8.0F)
            For tick = 0 To 4
                Dim fraction = tick / 4.0F
                Dim y = plot.Bottom - plot.Height * fraction
                If _showGrid Then graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y)
                Dim value = minY + (maxY - minY) * fraction
                TextRenderer.DrawText(graphics, value.ToString("0.##", CultureInfo.InvariantCulture), labelFont,
                    New Rectangle(2, CInt(y - 8), 60, 16), UiTheme.MutedInk,
                    TextFormatFlags.Right Or TextFormatFlags.VerticalCenter)
            Next
            For tick = 0 To 4
                Dim fraction = tick / 4.0F
                Dim x = plot.Left + plot.Width * fraction
                If _showGrid Then graphics.DrawLine(gridPen, x, plot.Top, x, plot.Bottom)
                Dim value = minX + (maxX - minX) * fraction
                TextRenderer.DrawText(graphics, value.ToString("0.##", CultureInfo.InvariantCulture), labelFont,
                    New Rectangle(CInt(x - 42), CInt(plot.Bottom + 2), 84, 16), UiTheme.MutedInk,
                    TextFormatFlags.HorizontalCenter Or TextFormatFlags.VerticalCenter)
            Next
            graphics.DrawRectangle(axisPen, plot.X, plot.Y, plot.Width, plot.Height)
        End Using
    End Sub

    Private Sub DrawAxisLabels(graphics As Graphics, plot As RectangleF)
        Using labelFont As New Font("Segoe UI", 8.0F, FontStyle.Bold)
            TextRenderer.DrawText(graphics, XAxisLabel, labelFont,
                New Rectangle(CInt(plot.Left), Height - 21, CInt(plot.Width), 18), UiTheme.MutedInk,
                TextFormatFlags.HorizontalCenter Or TextFormatFlags.VerticalCenter Or TextFormatFlags.EndEllipsis)
            Dim state = graphics.Save()
            graphics.TranslateTransform(15.0F, plot.Top + plot.Height / 2.0F)
            graphics.RotateTransform(-90.0F)
            Using brush As New SolidBrush(UiTheme.MutedInk),
                  format As New StringFormat With {
                      .Alignment = StringAlignment.Center,
                      .LineAlignment = StringAlignment.Center,
                      .Trimming = StringTrimming.EllipsisCharacter
                  }
                graphics.DrawString(YAxisLabel, labelFont, brush,
                    New RectangleF(-plot.Height / 2.0F, -9.0F, plot.Height, 18.0F), format)
            End Using
            graphics.Restore(state)
        End Using
    End Sub

    Private Sub DrawLegend(graphics As Graphics, points As IReadOnlyList(Of AnalyticsPoint))
        Dim names = points.Select(Function(point) point.SeriesName).Distinct(StringComparer.Ordinal).Take(4).ToArray()
        Dim hasMaximum = points.Any(Function(point) point.IsObservedMaximum)
        If names.Length = 0 AndAlso Not hasMaximum Then Return
        Dim x = 76
        Using font As New Font("Segoe UI", 8.0F)
            For Each seriesName In names
                Using brush As New SolidBrush(GetSeriesColor(seriesName))
                    graphics.FillEllipse(brush, x, 40, 6, 6)
                End Using
                Dim labelWidth = TextRenderer.MeasureText(seriesName, font).Width
                TextRenderer.DrawText(graphics, seriesName, font, New Point(x + 9, 34), UiTheme.MutedInk,
                                      TextFormatFlags.NoPadding Or TextFormatFlags.VerticalCenter)
                x += labelWidth + 24
            Next
            If hasMaximum Then
                Using brush As New SolidBrush(Color.FromArgb(220, 166, 72))
                    graphics.FillEllipse(brush, x, 40, 7, 7)
                End Using
                TextRenderer.DrawText(graphics, "Observed max", font, New Point(x + 10, 34), UiTheme.MutedInk,
                                      TextFormatFlags.NoPadding Or TextFormatFlags.VerticalCenter)
            End If
        End Using
    End Sub

    Private Shared Function GetSeriesColor(name As String) As Color
        Select Case If(name, String.Empty).ToUpperInvariant()
            Case "L1"
                Return Color.FromArgb(31, 111, 186)
            Case "L2"
                Return Color.FromArgb(22, 145, 127)
            Case "L3"
                Return Color.FromArgb(207, 132, 43)
            Case "TOTAL"
                Return Color.FromArgb(112, 91, 170)
            Case Else
                Return Color.FromArgb(190, 101, 78)
        End Select
    End Function

    Private Sub DrawDataSeries(graphics As Graphics, plot As RectangleF,
                               minX As Double, maxX As Double, minY As Double, maxY As Double)
        Dim groups = _renderedPoints.GroupBy(Function(point) point.SeriesName, StringComparer.Ordinal)
        Dim baselineY = CSng(plot.Bottom - Math.Clamp((0.0R - minY) / (maxY - minY), 0.0R, 1.0R) * plot.Height)
        For Each group In groups
            Dim points = group.OrderBy(Function(point) point.TimestampUtc).ToArray()
            Dim previous As AnalyticsPoint = Nothing
            Dim columnWidth = Math.Max(2.0F, plot.Width / Math.Max(1, points.Length) * 0.6F)
            For Each point In points
                Dim position = MapPoint(point.X, point.Y, plot, minX, maxX, minY, maxY)
                If _chartType = AnalyticsChartType.Column Then
                    Using brush As New SolidBrush(Color.FromArgb(185, GetSeriesColor(point.SeriesName)))
                        graphics.FillRectangle(brush, position.X - columnWidth / 2.0F,
                            Math.Min(position.Y, baselineY), columnWidth,
                            Math.Max(1.0F, Math.Abs(baselineY - position.Y)))
                    End Using
                ElseIf _chartType = AnalyticsChartType.Scatter Then
                    If _showMarkers Then DrawMarker(graphics, point, position)
                ElseIf previous IsNot Nothing AndAlso
                       point.TimestampUtc - previous.TimestampUtc <= TimeSpan.FromSeconds(_pollingIntervalSeconds * 2.5R) Then
                    Dim prior = MapPoint(previous.X, previous.Y, plot, minX, maxX, minY, maxY)
                    Using pen As New Pen(GetSeriesColor(point.SeriesName), 1.7F)
                        If _chartType = AnalyticsChartType.Area Then
                            Dim polygon = {
                                New PointF(prior.X, baselineY), prior, position,
                                New PointF(position.X, baselineY)
                            }
                            Using brush As New SolidBrush(Color.FromArgb(38, GetSeriesColor(point.SeriesName)))
                                graphics.FillPolygon(brush, polygon)
                            End Using
                        End If
                        If _chartType = AnalyticsChartType.StepLine Then
                            graphics.DrawLine(pen, prior, New PointF(position.X, prior.Y))
                            graphics.DrawLine(pen, New PointF(position.X, prior.Y), position)
                        Else
                            graphics.DrawLine(pen, prior, position)
                        End If
                    End Using
                End If
                If _showMarkers AndAlso _chartType <> AnalyticsChartType.Column AndAlso
                   _chartType <> AnalyticsChartType.Scatter Then DrawMarker(graphics, point, position)
                previous = point
            Next
        Next
    End Sub

    Private Shared Sub DrawMarker(graphics As Graphics, point As AnalyticsPoint, position As PointF)
        Dim markerColor = If(point.IsObservedMaximum, Color.FromArgb(220, 166, 72), GetSeriesColor(point.SeriesName))
        Dim diameter = If(point.IsObservedMaximum, 5.0F, 3.0F)
        Using brush As New SolidBrush(If(point.IsObservedMaximum, markerColor, Color.FromArgb(135, markerColor)))
            graphics.FillEllipse(brush, position.X - diameter / 2.0F, position.Y - diameter / 2.0F,
                                 diameter, diameter)
        End Using
        If point.IsObservedMaximum Then
            Using pen As New Pen(UiTheme.Surface, 1.0F)
                graphics.DrawEllipse(pen, position.X - diameter / 2.0F, position.Y - diameter / 2.0F,
                                     diameter, diameter)
            End Using
        End If
    End Sub

    Private Shared Function MapPoint(x As Double, y As Double, plot As RectangleF,
                                     minX As Double, maxX As Double, minY As Double, maxY As Double) As PointF
        Dim xFraction = If(maxX > minX, (x - minX) / (maxX - minX), 0.5R)
        Dim yFraction = If(maxY > minY, (y - minY) / (maxY - minY), 0.5R)
        Return New PointF(
            CSng(plot.Left + Math.Clamp(xFraction, 0.0R, 1.0R) * plot.Width),
            CSng(plot.Bottom - Math.Clamp(yFraction, 0.0R, 1.0R) * plot.Height))
    End Function

    Private Shared Sub DrawTrendline(graphics As Graphics, regression As RegressionSummary,
                                     firstX As Double, lastX As Double, plot As RectangleF,
                                     minX As Double, maxX As Double, minY As Double, maxY As Double,
                                     color As Color)
        Dim firstY = regression.Intercept + regression.Slope * firstX
        Dim lastY = regression.Intercept + regression.Slope * lastX
        If Not Double.IsFinite(firstY) OrElse Not Double.IsFinite(lastY) Then Return
        Dim first = MapPoint(firstX, Math.Clamp(firstY, minY, maxY), plot, minX, maxX, minY, maxY)
        Dim last = MapPoint(lastX, Math.Clamp(lastY, minY, maxY), plot, minX, maxX, minY, maxY)
        Using pen As New Pen(color, 1.5F)
            pen.DashStyle = Drawing2D.DashStyle.Dash
            graphics.DrawLine(pen, first, last)
        End Using
    End Sub
End Class
