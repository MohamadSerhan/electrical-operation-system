Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.ComponentModel
Imports System.Windows.Forms

Public Class RoundedPanel
    Inherits Panel

    Private _radius As Integer = 12
    Private _borderColor As Color = UiTheme.Border

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

    Public Sub New()
        DoubleBuffered = True
        ResizeRedraw = True
        BackColor = UiTheme.Surface
        Padding = New Padding(18)
    End Sub

    Protected Overrides Sub OnPaintBackground(e As PaintEventArgs)
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias
        Dim backgroundColor = UiTheme.Canvas
        Dim ancestor = Parent
        While ancestor IsNot Nothing
            If ancestor.BackColor.A = Byte.MaxValue Then
                backgroundColor = ancestor.BackColor
                Exit While
            End If
            ancestor = ancestor.Parent
        End While
        e.Graphics.Clear(backgroundColor)
        Dim rect = New Rectangle(0, 0, Width - 1, Height - 1)
        Using path = RoundedPath(rect, _radius)
            Using fill As New SolidBrush(BackColor)
                e.Graphics.FillPath(fill, path)
            End Using
            Using pen As New Pen(_borderColor, 1.1F)
                e.Graphics.DrawPath(pen, path)
            End Using
        End Using
    End Sub

    Private Shared Function RoundedPath(rect As Rectangle, radius As Integer) As GraphicsPath
        Dim path As New GraphicsPath()
        Dim diameter = Math.Min(Math.Min(radius * 2, rect.Width), rect.Height)
        If diameter <= 1 Then
            path.AddRectangle(rect)
            Return path
        End If
        path.AddArc(rect.Left, rect.Top, diameter, diameter, 180, 90)
        path.AddArc(rect.Right - diameter, rect.Top, diameter, diameter, 270, 90)
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90)
        path.AddArc(rect.Left, rect.Bottom - diameter, diameter, diameter, 90, 90)
        path.CloseFigure()
        Return path
    End Function
End Class
