Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.ComponentModel
Imports System.Globalization
Imports System.Windows.Forms

Public NotInheritable Class LoadGaugeControl
    Inherits Control

    Private Shared ReadOnly MeterGreen As Color = Color.FromArgb(46, 160, 115)
    Private Shared ReadOnly MeterYellow As Color = Color.FromArgb(224, 169, 69)
    Private Shared ReadOnly MeterRed As Color = Color.FromArgb(205, 91, 91)
    Private Shared ReadOnly TrackColor As Color = Color.FromArgb(224, 232, 240)
    Private _percent As Double?
    Private _maximum As Double = 2500.0R
    Private _greenZoneEndLoad As Double = 1400.0R
    Private _redZoneStartLoad As Double = 2000.0R
    Private _scaleValues As Double() = {0.0R, 500.0R, 1000.0R, 1400.0R, 2000.0R, 2500.0R}
    Private _indicatorText As String = "Not polled"
    Private _indicatorColor As Color = UiTheme.MutedInk
    Private _indicatorDetail As String = String.Empty
    Private _indicatorStyle As Integer
    Private _blinkOn As Boolean = True
    Private ReadOnly _blinkTimer As New Timer With {.Interval = 500}

    Public Sub New()
        AddHandler _blinkTimer.Tick, Sub(sender, e)
                                         _blinkOn = Not _blinkOn
                                         Invalidate()
                                     End Sub
        SetStyle(ControlStyles.AllPaintingInWmPaint Or ControlStyles.OptimizedDoubleBuffer Or
                 ControlStyles.SupportsTransparentBackColor Or ControlStyles.ResizeRedraw Or
                 ControlStyles.UserPaint, True)
        BackColor = Color.Transparent
        ForeColor = UiTheme.Ink
        MinimumSize = New Size(120, 100)
    End Sub

    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property LoadPercent As Double?
        Get
            Return _percent
        End Get
        Set(value As Double?)
            _percent = If(value.HasValue AndAlso Double.IsFinite(value.Value), value, Nothing)
            Invalidate()
        End Set
    End Property

    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property MaximumLoad As Double
        Get
            Return _maximum
        End Get
        Set(value As Double)
            _maximum = If(Double.IsFinite(value) AndAlso value > 0.0R, value, 2500.0R)
            Invalidate()
        End Set
    End Property

    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property GreenZoneEndLoad As Double
        Get
            Return _greenZoneEndLoad
        End Get
        Set(value As Double)
            _greenZoneEndLoad = If(Double.IsFinite(value) AndAlso value >= 0.0R, value, 1400.0R)
            Invalidate()
        End Set
    End Property

    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property RedZoneStartLoad As Double
        Get
            Return _redZoneStartLoad
        End Get
        Set(value As Double)
            _redZoneStartLoad = If(Double.IsFinite(value) AndAlso value >= 0.0R, value, 2000.0R)
            Invalidate()
        End Set
    End Property

    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property ScaleValues As Double()
        Get
            Return DirectCast(_scaleValues.Clone(), Double())
        End Get
        Set(value As Double())
            If value Is Nothing OrElse value.Length = 0 OrElse
                value.Any(Function(item) Not Double.IsFinite(item) OrElse item < 0.0R OrElse item > _maximum) Then
                _scaleValues = {0.0R, 500.0R, 1000.0R, 1400.0R, 2000.0R, 2500.0R}
            Else
                _scaleValues = DirectCast(value.Clone(), Double())
            End If
            Invalidate()
        End Set
    End Property

    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property IndicatorText As String
        Get
            Return _indicatorText
        End Get
        Set(value As String)
            value = If(value, String.Empty)
            If String.Equals(_indicatorText, value, StringComparison.Ordinal) Then Return
            _indicatorText = value
            Invalidate()
        End Set
    End Property

    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property IndicatorColor As Color
        Get
            Return _indicatorColor
        End Get
        Set(value As Color)
            If _indicatorColor = value Then Return
            _indicatorColor = value
            Invalidate()
        End Set
    End Property

    ' Secondary line under the status badge (for example the value that raised the alarm).
    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property IndicatorDetail As String
        Get
            Return _indicatorDetail
        End Get
        Set(value As String)
            value = If(value, String.Empty)
            If String.Equals(_indicatorDetail, value, StringComparison.Ordinal) Then Return
            _indicatorDetail = value
            Invalidate()
        End Set
    End Property

    ' Badge style: 0 = text only, 1 = outlined badge (pre-alarm), 2 = filled badge blinking every 500 ms (alarm).
    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property IndicatorStyle As Integer
        Get
            Return _indicatorStyle
        End Get
        Set(value As Integer)
            value = Math.Clamp(value, 0, 2)
            If _indicatorStyle = value Then Return
            _indicatorStyle = value
            If value = 2 Then
                _blinkOn = True
                _blinkTimer.Start()
            Else
                _blinkTimer.Stop()
                _blinkOn = True
            End If
            Invalidate()
        End Set
    End Property

    Public ReadOnly Property IsBlinking As Boolean
        Get
            Return _blinkTimer.Enabled
        End Get
    End Property

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            _blinkTimer.Stop()
            _blinkTimer.Dispose()
        End If
        MyBase.Dispose(disposing)
    End Sub

    Public ReadOnly Property NeedleAngleDegrees As Double
        Get
            If Not _percent.HasValue Then Return 90.0R
            Return 180.0R - Math.Clamp(_percent.Value, 0.0R, 100.0R) * 1.8R
        End Get
    End Property

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        MyBase.OnPaint(e)
        If ClientSize.Width <= 0 OrElse ClientSize.Height <= 0 Then Return

        Dim radius = Math.Min((ClientSize.Width - 28.0R) / 2.0R, ClientSize.Height - 20.0R)
        If Not Double.IsFinite(radius) OrElse radius <= 18.0R Then Return

        Dim graphics = e.Graphics
        graphics.SmoothingMode = SmoothingMode.AntiAlias
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality
        graphics.CompositingQuality = CompositingQuality.HighQuality

        Dim centerX = ClientSize.Width / 2.0R
        Dim baseline = ClientSize.Height - 10.0R
        Dim faceBounds = New RectangleF(CSng(centerX - radius), CSng(baseline - radius),
                                        CSng(radius * 2.0R), CSng(radius * 2.0R))
        Dim arcRadius = radius - 8.0R
        Dim arcBounds = New RectangleF(CSng(centerX - arcRadius), CSng(baseline - arcRadius),
                                       CSng(arcRadius * 2.0R), CSng(arcRadius * 2.0R))

        Using facePath = CreateDialPath(faceBounds)
            Using faceBrush As New LinearGradientBrush(faceBounds,
                Color.White, Color.FromArgb(246, 249, 252), LinearGradientMode.Vertical)
                graphics.FillPath(faceBrush, facePath)
            End Using
        End Using

        DrawArc(graphics, arcBounds, 0.0R, 100.0R, TrackColor, 11.0F)

        Dim greenEnd = ClampPercent(_greenZoneEndLoad / _maximum * 100.0R)
        Dim redStart = Math.Max(greenEnd, ClampPercent(_redZoneStartLoad / _maximum * 100.0R))
        DrawArc(graphics, arcBounds, 0.0R, greenEnd, MeterGreen, 8.0F)
        DrawArc(graphics, arcBounds, greenEnd, redStart, MeterYellow, 8.0F)
        DrawArc(graphics, arcBounds, redStart, 100.0R, MeterRed, 8.0F)

        DrawScale(graphics, centerX, baseline, arcRadius)
        Dim angleRadians = NeedleAngleDegrees * Math.PI / 180.0R
        Dim needleEnd = PolarPoint(centerX, baseline, arcRadius - 4.0R, angleRadians)
        DrawNeedle(graphics, CSng(centerX), CSng(baseline), needleEnd, angleRadians)
        DrawHub(graphics, CSng(centerX), CSng(baseline))
        DrawIndicator(graphics, CSng(centerX), CSng(baseline), CSng(arcRadius))
    End Sub

    Private Shared Function CreateDialPath(bounds As RectangleF) As GraphicsPath
        Dim path As New GraphicsPath()
        path.AddArc(bounds, 180.0F, 180.0F)
        Dim baseline = bounds.Top + bounds.Height / 2.0F
        path.AddLine(bounds.Right, baseline, bounds.Left, baseline)
        path.CloseFigure()
        Return path
    End Function

    Private Shared Sub DrawArc(graphics As Graphics, bounds As RectangleF, startPercent As Double,
                               endPercent As Double, color As Color, width As Single)
        Dim start = ClampPercent(startPercent)
        Dim finish = ClampPercent(endPercent)
        If finish <= start Then Return

        Dim startAngle = CSng(180.0R + start * 1.8R)
        Dim sweepAngle = CSng((finish - start) * 1.8R)
        Using glowPen As New Pen(Color.FromArgb(34, color), width + 6.0F)
            glowPen.StartCap = LineCap.Flat
            glowPen.EndCap = LineCap.Flat
            graphics.DrawArc(glowPen, bounds, startAngle, sweepAngle)
        End Using
        Using zonePen As New Pen(color, width)
            zonePen.StartCap = LineCap.Flat
            zonePen.EndCap = LineCap.Flat
            graphics.DrawArc(zonePen, bounds, startAngle, sweepAngle)
        End Using
    End Sub

    Private Sub DrawScale(graphics As Graphics, centerX As Double, baseline As Double, radius As Double)
        Dim majorValues = _scaleValues.
            Where(Function(value) value <= _maximum).
            Distinct().
            OrderBy(Function(value) value).
            ToArray()
        Dim majorPercents = majorValues.
            Select(Function(value) value / _maximum * 100.0R).
            ToArray()

        Using minorPen As New Pen(Color.FromArgb(190, 172, 187, 202), 1.0F)
            Using majorPen As New Pen(Color.FromArgb(255, 95, 116, 137), 1.6F)
                For tick = 0 To 20
                    Dim percent = tick * 5.0R
                    Dim isMajor = majorPercents.Any(Function(mark) Math.Abs(mark - percent) < 0.25R)
                    Dim angle = (180.0R - percent * 1.8R) * Math.PI / 180.0R
                    Dim innerRadius = radius - If(isMajor, 15.0R, 9.0R)
                    Dim inner = PolarPoint(centerX, baseline, innerRadius, angle)
                    Dim outer = PolarPoint(centerX, baseline, radius - 3.0R, angle)
                    graphics.DrawLine(If(isMajor, majorPen, minorPen), inner, outer)
                Next

                For Each value In majorValues
                    Dim percent = value / _maximum * 100.0R
                    Dim angle = (180.0R - percent * 1.8R) * Math.PI / 180.0R
                    Dim labelRadius = radius * 0.70R
                    If majorValues.Length > 1 AndAlso
                        majorValues.Any(Function(other) other <> value AndAlso Math.Abs(other / _maximum * 100.0R - percent) < 18.0R) Then
                        labelRadius = radius * 0.82R
                    End If
                    Dim labelPoint = PolarPoint(centerX, baseline, labelRadius, angle)
                    Dim fontSize = CSng(Math.Clamp(radius / 10.0R, 7.0R, 10.0R))
                    Using labelFont As New Font("Segoe UI", fontSize, FontStyle.Bold)
                        Using labelBrush As New SolidBrush(UiTheme.Ink)
                            Using centered As New StringFormat With {
                                .Alignment = StringAlignment.Center,
                                .LineAlignment = StringAlignment.Center
                            }
                                Dim label = value.ToString("0", CultureInfo.InvariantCulture)
                                Dim labelBounds = New RectangleF(CSng(labelPoint.X - 30.0R),
                                    CSng(labelPoint.Y - 11.0R), 60.0F, 22.0F)
                                Using shadowBrush As New SolidBrush(Color.FromArgb(35, 83, 104, 123))
                                    Dim shadowBounds = labelBounds
                                    shadowBounds.Offset(1.0F, 1.0F)
                                    graphics.DrawString(label, labelFont, shadowBrush, shadowBounds, centered)
                                End Using
                                graphics.DrawString(label, labelFont, labelBrush, labelBounds, centered)
                            End Using
                        End Using
                    End Using
                Next
            End Using
        End Using
    End Sub

    Private Sub DrawIndicator(Graphics As Graphics, centerX As Single, baseline As Single, radius As Single)
        If String.IsNullOrEmpty(_indicatorText) Then Return
        Dim compact = radius < 65.0F
        Using statusFont As New Font("Segoe UI", If(compact, 7.5F, 8.5F), FontStyle.Bold)
            Using centered As New StringFormat With {
                .Alignment = StringAlignment.Center,
                .LineAlignment = StringAlignment.Center,
                .Trimming = StringTrimming.EllipsisCharacter,
                .FormatFlags = StringFormatFlags.NoWrap
            }
                Dim textSize = Graphics.MeasureString(_indicatorText, statusFont)
                Dim badgeHeight = Math.Max(17.0F, radius * 0.23F)
                Dim badgeWidth = Math.Min(radius * 1.55F, textSize.Width + 16.0F)
                Dim badge As New RectangleF(centerX - badgeWidth / 2.0F, baseline - radius * 0.54F, badgeWidth, badgeHeight)
                Dim filled = _indicatorStyle = 2 AndAlso _blinkOn
                Using path = RoundedRectangle(badge, badgeHeight / 2.0F)
                    Using backdrop As New SolidBrush(If(filled, _indicatorColor, Color.FromArgb(245, 255, 255, 255)))
                        Graphics.FillPath(backdrop, path)
                    End Using
                    If _indicatorStyle > 0 Then
                        Using outline As New Pen(_indicatorColor, 1.4F)
                            Graphics.DrawPath(outline, path)
                        End Using
                    End If
                End Using
                Using statusBrush As New SolidBrush(If(filled, Color.White, _indicatorColor))
                    Graphics.DrawString(_indicatorText, statusFont, statusBrush, badge, centered)
                End Using

                If Not String.IsNullOrEmpty(_indicatorDetail) Then
                    Using detailFont As New Font("Segoe UI", If(compact, 6.5F, 7.5F), FontStyle.Bold)
                        Dim detailSize = Graphics.MeasureString(_indicatorDetail, detailFont)
                        Dim detailWidth = Math.Min(radius * 1.6F, detailSize.Width + 10.0F)
                        Dim detailBounds As New RectangleF(centerX - detailWidth / 2.0F, badge.Bottom + 1.0F, detailWidth, 13.0F)
                        Using detailPath = RoundedRectangle(detailBounds, 6.0F)
                            Using detailBackdrop As New SolidBrush(Color.FromArgb(245, 232, 240, 247))
                                Graphics.FillPath(detailBackdrop, detailPath)
                            End Using
                        End Using
                        Using detailBrush As New SolidBrush(UiTheme.MutedInk)
                            Graphics.DrawString(_indicatorDetail, detailFont, detailBrush, detailBounds, centered)
                        End Using
                    End Using
                End If
            End Using
        End Using
    End Sub

    Private Shared Function RoundedRectangle(bounds As RectangleF, cornerRadius As Single) As GraphicsPath
        Dim diameter = Math.Min(cornerRadius * 2.0F, Math.Min(bounds.Width, bounds.Height))
        Dim path As New GraphicsPath()
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180.0F, 90.0F)
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270.0F, 90.0F)
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0.0F, 90.0F)
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90.0F, 90.0F)
        path.CloseFigure()
        Return path
    End Function

    Private Shared Function ClampPercent(value As Double) As Double
        If Not Double.IsFinite(value) Then Return 0.0R
        Return Math.Clamp(value, 0.0R, 100.0R)
    End Function

    Private Shared Function PolarPoint(centerX As Double, baseline As Double, radius As Double, angle As Double) As PointF
        Return New PointF(CSng(centerX + Math.Cos(angle) * radius),
                          CSng(baseline - Math.Sin(angle) * radius))
    End Function

    Private Shared Sub DrawNeedle(graphics As Graphics, centerX As Single, baseline As Single, tip As PointF, angle As Double)
        Dim directionX = CSng(Math.Cos(angle))
        Dim directionY = -CSng(Math.Sin(angle))
        Dim perpendicularX = -directionY
        Dim perpendicularY = directionX
        Dim deltaX = CDbl(tip.X) - centerX
        Dim deltaY = CDbl(tip.Y) - baseline
        Dim length = CSng(Math.Sqrt(deltaX * deltaX + deltaY * deltaY))
        If Not Single.IsFinite(length) OrElse length <= 0.0F Then Return

        Dim rearOffset = Math.Min(8.0F, length * 0.15F)
        Dim rearX = centerX - directionX * rearOffset
        Dim rearY = baseline - directionY * rearOffset
        Dim halfWidth = Math.Clamp(length * 0.05F, 3.5F, 5.5F)
        Dim points = {
            New PointF(rearX + perpendicularX * halfWidth, rearY + perpendicularY * halfWidth),
            tip,
            New PointF(rearX - perpendicularX * halfWidth, rearY - perpendicularY * halfWidth),
            New PointF(centerX - perpendicularX * halfWidth * 0.45F,
                       baseline - perpendicularY * halfWidth * 0.45F)
        }
        Using needlePath As New GraphicsPath()
            needlePath.AddPolygon(points)
            Using needleBrush As New SolidBrush(Color.FromArgb(218, 38, 38))
                graphics.FillPath(needleBrush, needlePath)
            End Using
            Using needleEdge As New Pen(Color.FromArgb(155, 25, 25), 1.0F)
                graphics.DrawPath(needleEdge, needlePath)
            End Using
        End Using
    End Sub

    Private Shared Sub DrawHub(graphics As Graphics, centerX As Single, baseline As Single)
        Dim outer As New RectangleF(centerX - 5.5F, baseline - 5.5F, 11.0F, 11.0F)
        Using shadow As New SolidBrush(Color.FromArgb(45, 54, 72, 90))
            graphics.FillEllipse(shadow, New RectangleF(outer.X + 1.0F, outer.Y + 1.5F, outer.Width, outer.Height))
        End Using
        Using rim As New SolidBrush(Color.FromArgb(238, 194, 207, 220))
            graphics.FillEllipse(rim, outer)
        End Using
        Dim core As New RectangleF(centerX - 3.2F, baseline - 3.2F, 6.4F, 6.4F)
        Using hub As New SolidBrush(Color.FromArgb(255, 88, 79))
            graphics.FillEllipse(hub, core)
        End Using
    End Sub
End Class
