Imports System.Drawing
Imports System.Windows.Forms

Public NotInheritable Class SegmentedTabStrip
    Inherits Control

    Private ReadOnly _tabControl As TabControl
    Private _hover As Integer = -1

    Public Sub New(tabControl As TabControl)
        If tabControl Is Nothing Then Throw New ArgumentNullException(NameOf(tabControl))
        _tabControl = tabControl
        Dock = DockStyle.Top
        Height = UiTheme.WorkspaceNavigationHeight
        Cursor = Cursors.Hand
        Font = New Font("Segoe UI", 10.0F, FontStyle.Bold)
        TabStop = True
        SetStyle(ControlStyles.OptimizedDoubleBuffer Or ControlStyles.AllPaintingInWmPaint Or
                 ControlStyles.UserPaint Or ControlStyles.ResizeRedraw Or ControlStyles.Selectable, True)
        AddHandler _tabControl.SelectedIndexChanged, AddressOf OnTabSelectionChanged
    End Sub

    Private Sub OnTabSelectionChanged(sender As Object, e As EventArgs)
        Invalidate()
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then RemoveHandler _tabControl.SelectedIndexChanged, AddressOf OnTabSelectionChanged
        MyBase.Dispose(disposing)
    End Sub

    Private Function SegmentWidth() As Integer
        Return Math.Max(60, Math.Min(230, (Width - 8) \ Math.Max(1, _tabControl.TabCount)))
    End Function

    Private Function HitTest(x As Integer) As Integer
        Dim index = (x - 4) \ SegmentWidth()
        Return If(x >= 4 AndAlso index >= 0 AndAlso index < _tabControl.TabCount, index, -1)
    End Function

    Protected Overrides Sub OnMouseMove(e As MouseEventArgs)
        MyBase.OnMouseMove(e)
        Dim index = HitTest(e.X)
        If index <> _hover Then
            _hover = index
            Invalidate()
        End If
    End Sub

    Protected Overrides Sub OnMouseLeave(e As EventArgs)
        MyBase.OnMouseLeave(e)
        _hover = -1
        Invalidate()
    End Sub

    Protected Overrides Sub OnMouseDown(e As MouseEventArgs)
        If e.Button = MouseButtons.Left Then Focus()
        MyBase.OnMouseDown(e)
    End Sub

    Protected Overrides Sub OnMouseClick(e As MouseEventArgs)
        MyBase.OnMouseClick(e)
        Dim index = HitTest(e.X)
        If index >= 0 Then _tabControl.SelectedIndex = index
    End Sub

    Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
        MyBase.OnKeyDown(e)
        If (e.KeyCode = Keys.Left OrElse e.KeyCode = Keys.Right) AndAlso _tabControl.TabCount > 0 Then
            Dim direction = If(e.KeyCode = Keys.Right, 1, -1)
            _tabControl.SelectedIndex = (_tabControl.SelectedIndex + direction + _tabControl.TabCount) Mod _tabControl.TabCount
            e.Handled = True
        End If
    End Sub

    Private Shared Function RoundedPath(bounds As Rectangle, radius As Integer) As Drawing2D.GraphicsPath
        Dim diameter = radius * 2
        Dim path As New Drawing2D.GraphicsPath()
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90)
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90)
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90)
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90)
        path.CloseFigure()
        Return path
    End Function

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        e.Graphics.Clear(UiTheme.Canvas)
        e.Graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias
        Dim segment = SegmentWidth()
        For index = 0 To _tabControl.TabCount - 1
            Dim selected = index = _tabControl.SelectedIndex
            Dim bounds As New Rectangle(4 + index * segment, 4, segment - 6, Height - 8)
            If selected OrElse index = _hover Then
                Using path = RoundedPath(bounds, 9)
                    Using brush As New SolidBrush(If(selected, UiTheme.Surface, Color.FromArgb(232, 239, 248)))
                        e.Graphics.FillPath(brush, path)
                    End Using
                    If selected Then
                        Using pen As New Pen(Color.FromArgb(190, 207, 231), 1.0F)
                            e.Graphics.DrawPath(pen, path)
                        End Using
                    End If
                End Using
            End If
            If selected Then
                Using accent As New SolidBrush(UiTheme.Blue)
                    e.Graphics.FillRectangle(accent, bounds.X + 16, bounds.Bottom - 3, bounds.Width - 32, 3)
                End Using
            End If
            Using tabFont As New Font("Segoe UI", If(segment < 150, 8.0F, 10.0F), FontStyle.Bold)
                TextRenderer.DrawText(e.Graphics, _tabControl.TabPages(index).Text, tabFont, Rectangle.Inflate(bounds, -4, 0),
                                      If(selected, UiTheme.Blue, UiTheme.MutedInk),
                                      TextFormatFlags.HorizontalCenter Or TextFormatFlags.VerticalCenter Or TextFormatFlags.SingleLine Or TextFormatFlags.NoPrefix Or TextFormatFlags.EndEllipsis)
            End Using
        Next
    End Sub
End Class
