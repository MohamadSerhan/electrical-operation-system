Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Windows.Forms

Public NotInheritable Class ScadaBackgroundPanel
    Inherits Panel

    Public Sub New()
        DoubleBuffered = True
        ResizeRedraw = True
        BackColor = UiTheme.Canvas
    End Sub

    Protected Overrides Sub OnPaintBackground(e As PaintEventArgs)
        Dim bounds = ClientRectangle
        If bounds.Width <= 0 OrElse bounds.Height <= 0 Then Return
        Using background As New LinearGradientBrush(bounds,
            UiTheme.Canvas, UiTheme.SurfaceMuted, 35.0F)
            e.Graphics.FillRectangle(background, bounds)
        End Using
        Using line As New Pen(UiTheme.Border, 1.0F)
            e.Graphics.DrawLine(line, 0, 0, bounds.Width, 0)
        End Using
    End Sub
End Class
