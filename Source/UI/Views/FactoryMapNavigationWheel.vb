Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Windows.Forms

Friend NotInheritable Class FactoryMapNavigationWheel
    Implements IDisposable

    Private ReadOnly _owner As Control
    Private ReadOnly _command As Action(Of String)
    Private ReadOnly _drag As Action(Of String, Single, Single)
    Private ReadOnly _timer As New System.Windows.Forms.Timer With {.Interval = 40}
    Private _lastMotion As Long
    Private _alpha As Integer
    Private _hover As String = ""
    Private _gesture As String = ""
    Private _last As Point
    Private _inside As Boolean

    Public Sub New(owner As Control, command As Action(Of String), drag As Action(Of String, Single, Single))
        _owner = owner
        _command = command
        _drag = drag
        AddHandler _timer.Tick, Sub() AdvanceFade(Environment.TickCount64)
    End Sub

    Public ReadOnly Property Bounds As Rectangle
        Get
            Return New Rectangle(Math.Max(4, _owner.ClientSize.Width - 156), 36, 144, 164)
        End Get
    End Property

    Public ReadOnly Property Active As Boolean
        Get
            Return _gesture <> ""
        End Get
    End Property

    Public Sub AdvanceFade(now As Long)
        Dim opacity = If(Active, 205, CInt(Math.Clamp(205.0R * (1 - (now - _lastMotion - 1400) / 360.0R), 0, 205)))
        If opacity <> _alpha Then
            _alpha = opacity
            _owner.Invalidate(Bounds)
        End If
        If _alpha = 0 Then _timer.Stop()
    End Sub

    Public Function Hit(point As Point) As String
        If _alpha = 0 Then Return ""
        Dim box = Bounds
        Dim x = point.X - (box.X + 72), y = point.Y - (box.Y + 68)
        Dim radius = Math.Sqrt(x * x + y * y)
        If radius > 66 Then Return ""
        If radius < 14 Then Return "Orbit"
        Dim sector = (CInt(Math.Floor((Math.Atan2(y, x) + Math.PI / 4 + Math.PI * 2) / (Math.PI / 2))) Mod 4)
        Return If(radius > 44, {"East", "South", "West", "North"}(sector), {"Top", "Pan", "Fit", "Orbit"}(sector))
    End Function

    Public Function PointerMove(e As MouseEventArgs) As Boolean
        _lastMotion = Environment.TickCount64
        If _alpha <> 205 Then
            _alpha = 205
            _owner.Invalidate(Bounds)
        End If
        _timer.Start()
        Dim hover = Hit(e.Location)
        _inside = hover <> ""
        If hover <> _hover Then
            _hover = hover
            _owner.Invalidate(Bounds)
        End If
        If Active Then
            _drag(_gesture, e.X - _last.X, e.Y - _last.Y)
            _last = e.Location
            Return True
        End If
        Return _inside
    End Function

    Public Function PointerDown(e As MouseEventArgs) As Boolean
        If e.Button <> MouseButtons.Left Then Return False
        Dim action = Hit(e.Location)
        If action = "" Then Return False
        If action = "Orbit" OrElse action = "Pan" Then
            _gesture = action
            _last = e.Location
        Else
            _command(action)
        End If
        Return True
    End Function

    Public Sub Release()
        _gesture = ""
        _inside = False
        _lastMotion = Environment.TickCount64
    End Sub

    Public Sub Suspend()
        _timer.Stop()
        _gesture = ""
        _hover = ""
        _inside = False
        _alpha = 0
        _owner.Invalidate(Bounds)
    End Sub

    Public Sub Leave()
        _inside = False
        _hover = ""
        _owner.Invalidate(Bounds)
    End Sub

    Public Sub Draw(graphics As Graphics, yaw As Single)
        If _alpha = 0 Then Return
        Dim box = Bounds
        Dim center As New PointF(box.X + 72, box.Y + 68)
        Using background As New SolidBrush(Color.FromArgb(_alpha, 235, 242, 248)),
              inner As New SolidBrush(Color.FromArgb(_alpha, 215, 229, 241)),
              outline As New Pen(Color.FromArgb(_alpha, 80, 111, 139), 1),
              ink As New SolidBrush(Color.FromArgb(Math.Min(255, _alpha + 35), 32, 63, 90)),
              highlight As New SolidBrush(Color.FromArgb(Math.Min(235, _alpha), 123, 184, 232)),
              font As New Font("Segoe UI", 8, FontStyle.Bold),
              format As New StringFormat With {.Alignment = StringAlignment.Center, .LineAlignment = StringAlignment.Center}
            graphics.FillEllipse(background, center.X - 66, center.Y - 66, 132, 132)
            graphics.FillEllipse(inner, center.X - 44, center.Y - 44, 88, 88)
            For index = 0 To 3
                Dim action = {"Top", "Pan", "Fit", "Orbit"}(index)
                If action = _hover OrElse action = _gesture Then graphics.FillPie(highlight, center.X - 44, center.Y - 44, 88, 88, index * 90 - 45, 90)
            Next
            graphics.DrawEllipse(outline, center.X - 66, center.Y - 66, 132, 132)
            graphics.DrawEllipse(outline, center.X - 44, center.Y - 44, 88, 88)
            For Each direction In {-1, 1}
                graphics.DrawLine(outline, center.X + direction * 31, center.Y - 31, center.X - direction * 31, center.Y + 31)
            Next
            For Each caption As (String, Integer, Integer) In {("N", 0, -55), ("E", 55, 0), ("S", 0, 55), ("W", -55, 0),
                               ("ORBIT", 0, -28), ("PAN", 0, 28), ("TOP", 28, 0), ("FIT", -28, 0)}
                graphics.DrawString(caption.Item1, font, ink, center.X + caption.Item2, center.Y + caption.Item3, format)
            Next
            graphics.FillEllipse(background, center.X - 14, center.Y - 14, 28, 28)
            graphics.DrawEllipse(outline, center.X - 14, center.Y - 14, 28, 28)
            graphics.DrawString("3D", font, ink, center, format)
            Dim angle = yaw - Math.PI / 2
            Dim px = center.X + CSng(Math.Cos(angle) * 64), py = center.Y + CSng(Math.Sin(angle) * 64)
            graphics.FillEllipse(highlight, px - 3, py - 3, 6, 6)
            graphics.DrawString(If(_hover = "Orbit", "Drag to orbit", If(_hover = "Pan", "Drag to pan", "N E S W / Top / Fit")),
                                font, ink, New RectangleF(box.X, box.Y + 137, box.Width, 22), format)
        End Using
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        _timer.Dispose()
    End Sub
End Class
