Imports System.ComponentModel
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Windows.Forms

Public NotInheritable Class ScadaPanel
    Inherits Panel

    Private _borderColor As Color = UiTheme.Border
    Private _radius As Integer = 9

    Public Sub New()
        SetStyle(ControlStyles.SupportsTransparentBackColor, True)
        DoubleBuffered = True
        ResizeRedraw = True
        BackColor = Color.Transparent
        Padding = Padding.Empty
    End Sub

    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property BorderColor As Color
        Get
            Return _borderColor
        End Get
        Set(value As Color)
            _borderColor = value
            Invalidate()
        End Set
    End Property

    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property Radius As Integer
        Get
            Return _radius
        End Get
        Set(value As Integer)
            _radius = Math.Max(0, value)
            Invalidate()
        End Set
    End Property

    Protected Overrides Sub OnPaintBackground(e As PaintEventArgs)
        If ClientSize.Width < 2 OrElse ClientSize.Height < 2 Then Return
        MyBase.OnPaintBackground(e)
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias
        Dim rect As New Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1)
        Using path = CreateRoundedPath(rect, _radius)
            Using fill As New LinearGradientBrush(rect,
                Color.White, UiTheme.SurfaceMuted, LinearGradientMode.Vertical)
                e.Graphics.FillPath(fill, path)
            End Using
            Using outline As New Pen(_borderColor, 1.0F)
                e.Graphics.DrawPath(outline, path)
            End Using
        End Using
    End Sub

    Private Shared Function CreateRoundedPath(bounds As Rectangle, radius As Integer) As GraphicsPath
        Dim path As New GraphicsPath()
        Dim diameter = Math.Min(Math.Min(radius * 2, bounds.Width), bounds.Height)
        If diameter <= 1 Then
            path.AddRectangle(bounds)
            Return path
        End If
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90)
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90)
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90)
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90)
        path.CloseFigure()
        Return path
    End Function
End Class
