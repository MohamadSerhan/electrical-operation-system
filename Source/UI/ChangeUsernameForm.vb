Imports System.Drawing
Imports System.Threading
Imports System.Windows.Forms

Public NotInheritable Class ChangeUsernameForm
    Inherits Form

    Private ReadOnly _authentication As PortableAuthenticationService
    Private ReadOnly _session As UserSession
    Private ReadOnly _newName As New TextBox()
    Private ReadOnly _password As New TextBox()
    Private ReadOnly _message As New Label()
    Private ReadOnly _saveButton As Button = UiTheme.Button("Change user name")

    Public Sub New(settings As SqlSettings, session As UserSession)
        _authentication = New PortableAuthenticationService(settings)
        _session = session
        Text = "Change user name"
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.FixedDialog
        MinimizeBox = False
        MaximizeBox = False
        ShowInTaskbar = False
        ClientSize = New Size(460, 330)
        BackColor = UiTheme.Surface
        Font = New Font("Segoe UI", 9.0F)

        Dim title = UiTheme.Label("Change your user name", New Font("Segoe UI", 19.0F, FontStyle.Bold), UiTheme.Ink)
        title.Location = New Point(28, 24)
        title.Size = New Size(400, 34)
        Controls.Add(title)
        AddField("NEW USER NAME (CURRENT: " & session.Username.ToUpperInvariant() & ")", _newName, 76, False)
        AddField("CURRENT PASSWORD", _password, 150, True)

        _message.AutoSize = False
        _message.Location = New Point(30, 222)
        _message.Size = New Size(400, 38)
        _message.ForeColor = UiTheme.MutedInk
        Controls.Add(_message)

        _saveButton.Location = New Point(30, 272)
        _saveButton.Size = New Size(190, 40)
        AddHandler _saveButton.Click, AddressOf OnSaveClicked
        Controls.Add(_saveButton)
        Dim cancel = UiTheme.Button("Cancel", False)
        cancel.Location = New Point(238, 272)
        cancel.Size = New Size(190, 40)
        cancel.DialogResult = DialogResult.Cancel
        Controls.Add(cancel)
        CancelButton = cancel
        AcceptButton = _saveButton
    End Sub

    Private Sub AddField(caption As String, box As TextBox, top As Integer, secret As Boolean)
        Dim label = UiTheme.Label(caption, New Font("Segoe UI", 8.0F, FontStyle.Bold), UiTheme.MutedInk)
        label.Location = New Point(31, top)
        label.Size = New Size(400, 18)
        Controls.Add(label)
        box.Location = New Point(30, top + 23)
        box.Size = New Size(400, 34)
        box.Font = New Font("Segoe UI", 10.0F)
        box.UseSystemPasswordChar = secret
        box.MaxLength = 256
        Controls.Add(box)
    End Sub

    Private Async Sub OnSaveClicked(sender As Object, e As EventArgs)
        _saveButton.Enabled = False
        _message.ForeColor = UiTheme.MutedInk
        _message.Text = "Updating user name…"
        Dim result = Await _authentication.RenameLoginAsync(_session.UserId, _password.Text, _newName.Text, CancellationToken.None)
        _saveButton.Enabled = True
        If Not result.Succeeded Then
            _message.ForeColor = UiTheme.Red
            _message.Text = result.Message
            Return
        End If
        _session.Username = _newName.Text.Trim()
        DialogResult = DialogResult.OK
        Close()
    End Sub
End Class
