Imports System.Drawing
Imports System.Windows.Forms

' Hosts one connection editor per fire panel, with a selector to switch, add, rename and remove panels.
Public NotInheritable Class FirePanelsView
    Inherits UserControl

    Private ReadOnly _manager As FirePanelManager
    Private ReadOnly _ownsManager As Boolean
    Private ReadOnly _session As UserSession
    Private ReadOnly _selector As New ComboBox
    Private ReadOnly _add As Button = UiTheme.ActionButton("Add panel", True)
    Private ReadOnly _rename As Button = UiTheme.ActionButton("Rename", False)
    Private ReadOnly _remove As Button = UiTheme.ActionButton("Remove", False)
    Private ReadOnly _message As New Label
    Private ReadOnly _host As New Panel
    Private ReadOnly _views As New Dictionary(Of String, FireConnectionSettingsView)
    Private _current As FirePanel
    Private _active As Boolean
    Private _updating As Boolean

    Public Sub New(manager As FirePanelManager, ownsManager As Boolean, session As UserSession)
        If manager Is Nothing Then Throw New ArgumentNullException(NameOf(manager))
        _manager = manager
        _ownsManager = ownsManager
        _session = session
        BackColor = UiTheme.Canvas
        Font = New Font("Segoe UI", 9.0F)
        SuspendLayout()
        Dim bar As New FlowLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .WrapContents = True,
            .BackColor = UiTheme.Surface, .Padding = New Padding(14, 8, 14, 4)}
        Dim caption As New Label With {.Text = "Fire panel", .AutoSize = True, .Margin = New Padding(0, 10, 8, 0), .Font = New Font("Segoe UI", 9.5F, FontStyle.Bold), .ForeColor = UiTheme.Blue}
        _selector.Name = "panel-selector"
        _selector.DropDownStyle = ComboBoxStyle.DropDownList
        _selector.Width = 260
        _selector.Font = New Font("Segoe UI", 9.5F)
        _selector.Margin = New Padding(0, 7, 12, 4)
        For Each action In {_add, _rename, _remove}
            action.Size = New Size(110, 36)
            action.Margin = New Padding(0, 3, 8, 4)
        Next
        _add.Name = "panel-add"
        _rename.Name = "panel-rename"
        _remove.Name = "panel-remove"
        _message.AutoSize = True
        _message.ForeColor = UiTheme.MutedInk
        _message.Margin = New Padding(8, 10, 0, 0)
        bar.Controls.AddRange({caption, _selector, _add, _rename, _remove, _message})
        _host.Dock = DockStyle.Fill
        _host.BackColor = UiTheme.Canvas
        Controls.Add(_host)
        Controls.Add(bar)
        AddHandler _selector.SelectedIndexChanged, Sub() If Not _updating Then ShowPanel(SelectedPanel())
        AddHandler _add.Click, AddressOf OnAdd
        AddHandler _rename.Click, AddressOf OnRename
        AddHandler _remove.Click, AddressOf OnRemove
        ResumeLayout(True)
        Refill(_manager.Panels(0))
    End Sub

    Public ReadOnly Property CurrentView As FireConnectionSettingsView
        Get
            Dim view As FireConnectionSettingsView = Nothing
            If _current IsNot Nothing Then _views.TryGetValue(_current.Id, view)
            Return view
        End Get
    End Property

    Public Sub SetPageActive(active As Boolean)
        _active = active
        For Each pair In _views
            pair.Value.SetPageActive(active AndAlso _current IsNot Nothing AndAlso pair.Key = _current.Id)
        Next
    End Sub

    Private Function SelectedPanel() As FirePanel
        Return TryCast(_selector.SelectedItem, FirePanelItem)?.Panel
    End Function

    Private Sub Refill(select_ As FirePanel)
        _updating = True
        _selector.BeginUpdate()
        _selector.Items.Clear()
        For Each panel In _manager.Panels
            _selector.Items.Add(New FirePanelItem(panel))
        Next
        _selector.EndUpdate()
        _updating = False
        For index = 0 To _selector.Items.Count - 1
            If DirectCast(_selector.Items(index), FirePanelItem).Panel Is select_ Then _selector.SelectedIndex = index
        Next
        If _selector.SelectedIndex < 0 AndAlso _selector.Items.Count > 0 Then _selector.SelectedIndex = 0
        ShowPanel(SelectedPanel())
    End Sub

    Private Sub ShowPanel(panel As FirePanel)
        If panel Is Nothing Then Return
        _current = panel
        Dim view As FireConnectionSettingsView = Nothing
        If Not _views.TryGetValue(panel.Id, view) Then
            view = New FireConnectionSettingsView(panel.Service, _session) With {.Dock = DockStyle.Fill, .Name = "fire-panel-" & panel.Id}
            _views.Add(panel.Id, view)
            _host.Controls.Add(view)
        End If
        view.Visible = True
        view.BringToFront()
        For Each pair In _views
            If pair.Key <> panel.Id Then pair.Value.Visible = False
            pair.Value.SetPageActive(_active AndAlso pair.Key = panel.Id)
        Next
        _remove.Enabled = Not panel.IsPrimary
    End Sub

    Private Sub Report(text As String, Optional isError As Boolean = False)
        _message.Text = text
        _message.ForeColor = If(isError, Color.Firebrick, UiTheme.MutedInk)
    End Sub

    Private Sub OnAdd(sender As Object, e As EventArgs)
        Dim name = PromptName("Add fire panel", "Panel name", "Fire Panel " & (_manager.Panels.Count + 1).ToString(Globalization.CultureInfo.InvariantCulture))
        If name Is Nothing Then Return
        Try
            Dim panel = _manager.AddPanel(name, _session)
            Refill(panel)
            Report("Panel added. Enter its connection settings, then Save and Connect.")
        Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is IO.IOException
            Report(ex.Message, True)
        End Try
    End Sub

    Private Sub OnRename(sender As Object, e As EventArgs)
        Dim panel = SelectedPanel()
        If panel Is Nothing Then Return
        Dim name = PromptName("Rename fire panel", "Panel name", panel.Name)
        If name Is Nothing Then Return
        Try
            _manager.Rename(panel, name, _session)
            Refill(panel)
            Report("Panel renamed.")
        Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is IO.IOException
            Report(ex.Message, True)
        End Try
    End Sub

    Private Async Sub OnRemove(sender As Object, e As EventArgs)
        Dim panel = SelectedPanel()
        If panel Is Nothing OrElse panel.IsPrimary Then Return
        If MessageBox.Show(Me, "Remove '" & panel.Name & "' and its saved connection settings and credentials?" & vbCrLf & "Other panels are not affected.",
            "Remove fire panel", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) <> DialogResult.Yes Then Return
        Try
            Await _manager.RemoveAsync(panel, _session)
            Dim view As FireConnectionSettingsView = Nothing
            If _views.TryGetValue(panel.Id, view) Then
                _views.Remove(panel.Id)
                _host.Controls.Remove(view)
                view.Dispose()
            End If
            Refill(_manager.Panels(0))
            Report("Panel removed.")
        Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is IO.IOException
            Report(ex.Message, True)
        End Try
    End Sub

    Friend Function PromptName(title As String, caption As String, initial As String) As String
        Using dialog As New Form With {.Text = title, .FormBorderStyle = FormBorderStyle.FixedDialog, .StartPosition = FormStartPosition.CenterParent,
            .MinimizeBox = False, .MaximizeBox = False, .ShowInTaskbar = False, .ClientSize = New Size(360, 110), .Font = Font}
            Dim label As New Label With {.Text = caption, .Left = 14, .Top = 14, .AutoSize = True}
            Dim input As New TextBox With {.Left = 14, .Top = 38, .Width = 330, .MaxLength = FirePanelManager.MaximumNameLength, .Text = initial}
            Dim ok As New Button With {.Text = "OK", .Left = 188, .Top = 72, .Width = 75, .DialogResult = DialogResult.OK}
            Dim cancel As New Button With {.Text = "Cancel", .Left = 269, .Top = 72, .Width = 75, .DialogResult = DialogResult.Cancel}
            dialog.Controls.AddRange({label, input, ok, cancel})
            dialog.AcceptButton = ok
            dialog.CancelButton = cancel
            If dialog.ShowDialog(Me) <> DialogResult.OK Then Return Nothing
            Return input.Text
        End Using
    End Function

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing AndAlso _ownsManager Then _manager.Dispose()
        MyBase.Dispose(disposing)
    End Sub

    Private NotInheritable Class FirePanelItem
        Public Sub New(panel As FirePanel)
            Me.Panel = panel
        End Sub

        Public ReadOnly Property Panel As FirePanel

        Public Overrides Function ToString() As String
            Return Panel.Name
        End Function
    End Class
End Class
