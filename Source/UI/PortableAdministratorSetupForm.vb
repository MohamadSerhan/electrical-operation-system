Imports System.Drawing
Imports System.Threading
Imports System.Windows.Forms

Public NotInheritable Class PortableAdministratorSetupForm
    Inherits Form

    Private ReadOnly _authentication As PortableAuthenticationService
    Private ReadOnly _login As New TextBox()
    Private ReadOnly _name As New TextBox()
    Private ReadOnly _password As New TextBox()
    Private ReadOnly _confirm As New TextBox()
    Private ReadOnly _message As New Label()
    Private ReadOnly _create As Button = UiTheme.Button("Create administrator")

    Public Sub New(settings As SqlSettings)
        _authentication = New PortableAuthenticationService(settings)
        Text = "Create portable administrator"
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.FixedDialog
        MinimizeBox = False
        MaximizeBox = False
        ShowInTaskbar = False
        ClientSize = New Size(470, 500)
        BackColor = UiTheme.Surface
        Font = New Font("Segoe UI", 9.0F)
        Dim title = UiTheme.Label("Create the first portable administrator", New Font("Segoe UI", 16.0F, FontStyle.Bold), UiTheme.Ink)
        title.Location = New Point(28, 22) : title.Size = New Size(410, 32) : Controls.Add(title)
        Dim note = UiTheme.Label("This creates data\\eoems.db beside the application. Keep the complete application folder together when moving it.", New Font("Segoe UI", 8.2F), UiTheme.MutedInk)
        note.Location = New Point(30, 60) : note.Size = New Size(405, 42) : Controls.Add(note)
        AddField("ADMIN USER ID", _login, 122, False)
        AddField("DISPLAY NAME", _name, 194, False)
        AddField("PASSWORD (12 CHARACTERS MINIMUM)", _password, 266, True)
        AddField("CONFIRM PASSWORD", _confirm, 338, True)
        _message.Location = New Point(30, 406) : _message.Size = New Size(408, 38) : _message.ForeColor = UiTheme.MutedInk : Controls.Add(_message)
        _create.Location = New Point(30, 452) : _create.Size = New Size(196, 36) : AddHandler _create.Click, AddressOf OnCreateClicked : Controls.Add(_create)
        Dim cancel = UiTheme.Button("Cancel", False)
        cancel.Location = New Point(242, 452) : cancel.Size = New Size(196, 36) : cancel.DialogResult = DialogResult.Cancel : Controls.Add(cancel)
        AcceptButton = _create : CancelButton = cancel
    End Sub

    Private Sub AddField(caption As String, box As TextBox, top As Integer, secret As Boolean)
        Dim label = UiTheme.Label(caption, New Font("Segoe UI", 8.0F, FontStyle.Bold), UiTheme.MutedInk)
        label.Location = New Point(30, top) : label.Size = New Size(405, 18) : Controls.Add(label)
        box.Location = New Point(30, top + 23) : box.Size = New Size(408, 32) : box.Font = New Font("Segoe UI", 10.0F) : box.MaxLength = 256 : box.UseSystemPasswordChar = secret : Controls.Add(box)
    End Sub

    Private Async Sub OnCreateClicked(sender As Object, e As EventArgs)
        If Not String.Equals(_password.Text, _confirm.Text, StringComparison.Ordinal) Then
            _message.ForeColor = UiTheme.Red : _message.Text = "The passwords do not match." : Return
        End If
        _create.Enabled = False : _message.ForeColor = UiTheme.MutedInk : _message.Text = "Creating portable administrator…"
        Dim result = Await _authentication.CreateFirstPortableAdministratorAsync(_login.Text, _name.Text, _password.Text, CancellationToken.None)
        _create.Enabled = True
        If result.Succeeded Then
            _message.ForeColor = UiTheme.Green : _message.Text = result.Message
            DialogResult = DialogResult.OK : Close()
        Else
            _message.ForeColor = UiTheme.Red : _message.Text = result.Message
        End If
    End Sub
End Class
