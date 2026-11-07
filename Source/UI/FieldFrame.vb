Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Windows.Forms

' Rounded input frame around a borderless TextBox; the border turns blue while the box has focus.
Public NotInheritable Class FieldFrame
    Inherits Panel

    Private ReadOnly _input As TextBox

    Public Sub New(input As TextBox)
        _input = input
        SetStyle(ControlStyles.AllPaintingInWmPaint Or ControlStyles.OptimizedDoubleBuffer Or ControlStyles.ResizeRedraw Or ControlStyles.UserPaint, True)
        Height = 34
        input.BorderStyle = BorderStyle.None
        input.BackColor = If(input.ReadOnly, Color.FromArgb(240, 244, 248), Color.White)
        BackColor = input.BackColor
        Controls.Add(input)
        AddHandler input.GotFocus, Sub() Invalidate()
        AddHandler input.LostFocus, Sub() Invalidate()
        AddHandler input.ReadOnlyChanged, Sub()
                                              input.BackColor = If(input.ReadOnly, Color.FromArgb(240, 244, 248), Color.White)
                                              BackColor = input.BackColor
                                              Invalidate()
                                          End Sub
        AddHandler Click, Sub() input.Focus()
    End Sub

    Protected Overrides Sub OnLayout(e As LayoutEventArgs)
        MyBase.OnLayout(e)
        _input.Width = Math.Max(20, ClientSize.Width - 20)
        _input.Location = New Point(10, Math.Max(0, (ClientSize.Height - _input.Height) \ 2))
    End Sub

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        Dim parentColor = If(Parent IsNot Nothing, Parent.BackColor, UiTheme.Surface)
        e.Graphics.Clear(parentColor)
        If ClientSize.Width < 4 OrElse ClientSize.Height < 4 Then Return
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias
        Dim bounds As New Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1)
        Using path As New GraphicsPath()
            Const radius As Integer = 8
            path.AddArc(bounds.Left, bounds.Top, radius, radius, 180, 90)
            path.AddArc(bounds.Right - radius, bounds.Top, radius, radius, 270, 90)
            path.AddArc(bounds.Right - radius, bounds.Bottom - radius, radius, radius, 0, 90)
            path.AddArc(bounds.Left, bounds.Bottom - radius, radius, radius, 90, 90)
            path.CloseFigure()
            Using fill As New SolidBrush(_input.BackColor)
                e.Graphics.FillPath(fill, path)
            End Using
            Dim focused = _input.Focused
            Using border As New Pen(If(focused, UiTheme.Blue, Color.FromArgb(196, 210, 224)), If(focused, 1.6F, 1.0F))
                e.Graphics.DrawPath(border, path)
            End Using
        End Using
    End Sub
End Class
