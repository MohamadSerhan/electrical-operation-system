Imports System.Drawing
Imports System.Windows.Forms

' TabControl that never erases to the system colour when switching pages, so tab changes do not flash.
Public NotInheritable Class FlatTabControl
    Inherits TabControl

    Private Const WM_ERASEBKGND As Integer = &H14
    Private Const TCM_ADJUSTRECT As Integer = &H1328

    Public Sub New()
        SetStyle(ControlStyles.OptimizedDoubleBuffer Or ControlStyles.AllPaintingInWmPaint, True)
        DoubleBuffered = True
    End Sub

    Protected Overrides Sub WndProc(ByRef m As Message)
        If m.Msg = WM_ERASEBKGND Then
            m.Result = New IntPtr(1)
            Return
        End If
        ' Header strip is never shown (SegmentedTabStrip navigates), so let pages fill the whole control and leave no 1px header residue.
        If m.Msg = TCM_ADJUSTRECT AndAlso Not DesignMode Then
            m.Result = IntPtr.Zero
            Return
        End If
        MyBase.WndProc(m)
    End Sub

    Protected Overrides Sub OnVisibleChanged(e As EventArgs)
        MyBase.OnVisibleChanged(e)
        If Visible AndAlso IsHandleCreated Then BeginInvoke(New MethodInvoker(AddressOf PrepareHandles))
    End Sub

    ' Creates native handles and layout for hidden tab pages now, so the first click on a tab does not build them on screen.
    Public Sub PrepareHandles()
        If IsDisposed OrElse Not IsHandleCreated Then Return
        For Each page As TabPage In TabPages
            CreateHandles(page)
        Next
    End Sub

    Private Shared Sub CreateHandles(target As Control)
        If target.IsDisposed Then Return
        Dim handle = target.Handle
        For Each child As Control In target.Controls
            CreateHandles(child)
        Next
    End Sub

    Protected Overrides Sub OnPaintBackground(pevent As PaintEventArgs)
        pevent.Graphics.Clear(If(Parent IsNot Nothing, Parent.BackColor, UiTheme.Canvas))
    End Sub
End Class
