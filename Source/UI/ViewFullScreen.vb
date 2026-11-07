Imports System.Collections.Generic
Imports System.Drawing
Imports System.Linq
Imports System.Windows.Forms

''' <summary>Moves a whole page into its own non-modal window so the main app stays usable and several pages can be monitored on different screens.</summary>
Friend NotInheritable Class ViewFullScreen
    Private Shared ReadOnly Active As New Dictionary(Of Control, Form)

    Private Sub New()
    End Sub

    Public Shared Function CreateButton(view As Control) As IconButton
        Dim button = UiTheme.ActionButton("Full screen", False, ChrW(&HE740).ToString())
        button.AccessibleName = "Full screen"
        button.Text = ""
        button.Size = New Size(32, 32)
        AddHandler button.Click, Sub(sender, e) Toggle(view)
        Return button
    End Function

    Public Shared Function IsDetached(view As Control) As Boolean
        Return view IsNot Nothing AndAlso Active.ContainsKey(view)
    End Function

    Public Shared Function DetachedViews() As IReadOnlyList(Of Control)
        Return Active.Keys.ToList()
    End Function

    Public Shared Sub CloseAll()
        For Each window In Active.Values.ToList()
            window.Close()
        Next
    End Sub

    Private Shared Function PageTitle(view As Control) As String
        If TypeOf view Is TransformersView Then Return "Transformers"
        If TypeOf view Is UpsAtsView Then Return "UPS Monitoring"
        Return "Full screen"
    End Function

    Public Shared Sub Toggle(view As Control)
        Dim existing As Form = Nothing
        If Active.TryGetValue(view, existing) Then
            existing.Close()
            Return
        End If
        Dim parent = view.Parent
        If parent Is Nothing Then Return
        Dim index = parent.Controls.GetChildIndex(view)
        Dim dock = view.Dock
        Dim bounds = view.Bounds
        Dim anchor = view.Anchor
        Dim shell = view.FindForm()

        ' Prefer another monitor so the main app stays visible; fall back to the shell's own screen.
        Dim target = Screen.FromControl(shell)
        For Each candidate In Screen.AllScreens
            If Not candidate.DeviceName.Equals(target.DeviceName, StringComparison.Ordinal) Then
                target = candidate
                Exit For
            End If
        Next

        Dim window As New Form With {
            .Text = PageTitle(view) & " - Full screen",
            .StartPosition = FormStartPosition.Manual,
            .Bounds = target.WorkingArea,
            .BackColor = UiTheme.ShellDeep,
            .KeyPreview = True,
            .ShowInTaskbar = True
        }
        If shell IsNot Nothing Then window.Icon = shell.Icon
        Active(view) = window
        Dim wasVisible = view.Visible
        parent.Controls.Remove(view)
        view.Dock = DockStyle.Fill
        view.Visible = True
        window.Controls.Add(view)
        AddHandler window.KeyDown, Sub(sender, e)
                                       If e.KeyCode = Keys.Escape Then window.Close()
                                   End Sub
        AddHandler window.FormClosed, Sub(sender, e)
                                          Active.Remove(view)
                                          window.Controls.Remove(view)
                                          window.Dispose()
                                          If parent.IsDisposed OrElse view.IsDisposed Then Return
                                          view.Visible = False
                                          parent.Controls.Add(view)
                                          parent.Controls.SetChildIndex(view, Math.Min(index, parent.Controls.Count - 1))
                                          view.Anchor = anchor
                                          view.Dock = dock
                                          If dock = DockStyle.None Then view.Bounds = bounds
                                          Dim host = TryCast(parent, PageHostPanel)
                                          If host IsNot Nothing AndAlso host.IsShowingPlaceholderFor(view) Then host.ActivatePage(view)
                                      End Sub
        AddHandler window.Shown, Sub() view.Focus()
        window.Show()
        window.WindowState = FormWindowState.Maximized

        ' The shell may currently show this page; swap in the placeholder.
        Dim hostPanel = TryCast(parent, PageHostPanel)
        If hostPanel IsNot Nothing AndAlso wasVisible Then hostPanel.ActivatePage(view)
    End Sub
End Class
