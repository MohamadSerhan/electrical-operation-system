Imports System.Drawing
Imports System.Threading
Imports System.Windows.Forms

Public NotInheritable Class ChangePasswordForm
    Inherits Form

    Private ReadOnly _authentication As PortableAuthenticationService
    Private ReadOnly _session As UserSession
    Private ReadOnly _currentPassword As New TextBox()
    Private ReadOnly _newPassword As New TextBox()
    Private ReadOnly _confirmPassword As New TextBox()
    Private ReadOnly _message As New Label()
    Private ReadOnly _saveButton As Button = UiTheme.Button("Change password")

    Public Sub New(settings As SqlSettings, session As UserSession)
        _authentication = New PortableAuthenticationService(settings)
        _session = session
        BuildLayout()
    End Sub

    Private Sub BuildLayout()
        Text = "Change password"
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.FixedDialog
        MinimizeBox = False
        MaximizeBox = False
        ShowInTaskbar = False
        ClientSize = New Size(460, 410)
        BackColor = UiTheme.Surface
        Font = New Font("Segoe UI", 9.0F)

        Dim title = UiTheme.Label("Change your password", New Font("Segoe UI", 19.0F, FontStyle.Bold), UiTheme.Ink)
        title.Location = New Point(28, 24)
        title.Size = New Size(400, 34)
        Controls.Add(title)

        AddPasswordField("CURRENT PASSWORD", _currentPassword, 82)
        AddPasswordField("NEW PASSWORD (12 CHARACTERS MINIMUM)", _newPassword, 158)
        AddPasswordField("CONFIRM NEW PASSWORD", _confirmPassword, 234)

        _message.AutoSize = False
        _message.Location = New Point(30, 300)
        _message.Size = New Size(400, 38)
        _message.ForeColor = UiTheme.MutedInk
        Controls.Add(_message)

        _saveButton.Location = New Point(30, 350)
        _saveButton.Size = New Size(190, 40)
        AddHandler _saveButton.Click, AddressOf OnChangeClicked
        Controls.Add(_saveButton)

        Dim cancel = UiTheme.Button("Cancel", False)
        cancel.Location = New Point(238, 350)
        cancel.Size = New Size(190, 40)
        cancel.DialogResult = DialogResult.Cancel
        Controls.Add(cancel)
        CancelButton = cancel
        AcceptButton = _saveButton
    End Sub

    Private Sub AddPasswordField(caption As String, box As TextBox, top As Integer)
        Dim label = UiTheme.Label(caption, New Font("Segoe UI", 8.0F, FontStyle.Bold), UiTheme.MutedInk)
        label.Location = New Point(31, top)
        label.Size = New Size(390, 18)
        Controls.Add(label)

        box.Location = New Point(30, top + 23)
        box.Size = New Size(400, 34)
        box.Font = New Font("Segoe UI", 10.0F)
        box.UseSystemPasswordChar = True
        box.MaxLength = 256
        Controls.Add(box)
    End Sub

    Private Async Sub OnChangeClicked(sender As Object, e As EventArgs)
        If Not String.Equals(_newPassword.Text, _confirmPassword.Text, StringComparison.Ordinal) Then
            ShowError("The new passwords do not match.")
            Return
        End If

        _saveButton.Enabled = False
        _message.ForeColor = UiTheme.MutedInk
        _message.Text = "Updating password…"
        Dim result = Await _authentication.ChangePasswordAsync(_session.UserId, _currentPassword.Text, _newPassword.Text, CancellationToken.None)
        _saveButton.Enabled = True

        If Not result.Succeeded Then
            ShowError(result.Message)
            Return
        End If

        _message.ForeColor = UiTheme.Green
        _message.Text = result.Message
        _currentPassword.Clear()
        _newPassword.Clear()
        _confirmPassword.Clear()
        DialogResult = DialogResult.OK
        Close()
    End Sub

    Private Sub ShowError(message As String)
        _message.ForeColor = UiTheme.Red
        _message.Text = message
    End Sub
End Class
