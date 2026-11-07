Imports System.Threading
Imports System.Threading.Tasks

Friend NotInheritable Class ZetaSignInDialog
    Inherits Form

    Private ReadOnly _email As New TextBox With {.Dock = DockStyle.Fill, .MaxLength = 320}
    Private ReadOnly _password As New TextBox With {.Dock = DockStyle.Fill, .MaxLength = 4096, .UseSystemPasswordChar = True}
    Private ReadOnly _signIn As Button = UiTheme.ActionButton("Sign in and sync", True)
    Private ReadOnly _cancel As Button = UiTheme.ActionButton("Cancel", False)
    Private ReadOnly _message As New Label With {.AutoSize = True, .Dock = DockStyle.Fill}
    Private ReadOnly _work As Func(Of String, String, CancellationToken, Task(Of String))
    Private _cancellation As CancellationTokenSource
    Private _busy As Boolean

    Friend Sub New(work As Func(Of String, String, CancellationToken, Task(Of String)))
        _work = work
        Text = "Sign in to Zeta Remote"
        Font = New Font("Segoe UI", 9)
        BackColor = UiTheme.Canvas
        ClientSize = New Size(560, 380)
        FormBorderStyle = FormBorderStyle.FixedDialog
        StartPosition = FormStartPosition.CenterParent
        MinimizeBox = False
        MaximizeBox = False
        ShowInTaskbar = False
        Dim table As New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 1, .Padding = New Padding(16)}
        table.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        Dim row = 0
        For Each control As Control In {
            New Label With {.AutoSize = True, .MaximumSize = New Size(500, 0),
                .Text = "Sends login to https://www.zetaremote.com. Email, password and token are saved encrypted for this Windows account. Successful login enables startup connection and automatic recovery (changeable in settings). No panel commands."},
            New Label With {.AutoSize = True, .Text = "Zeta email"}, _email,
            New Label With {.AutoSize = True, .Text = "Zeta password"}, _password, _message}
            table.RowStyles.Add(New RowStyle(SizeType.AutoSize))
            table.Controls.Add(control, 0, row)
            row += 1
        Next
        _message.MaximumSize = New Size(520, 0)
        Dim actions As New FlowLayoutPanel With {.Dock = DockStyle.Bottom, .Height = 46, .Padding = New Padding(12, 4, 12, 4)}
        _signIn.AutoSize = True
        _cancel.AutoSize = True
        actions.Controls.AddRange({_signIn, _cancel})
        Controls.Add(table)
        Controls.Add(actions)
        AcceptButton = _signIn
        CancelButton = _cancel
        AddHandler _signIn.Click, AddressOf SignIn
        AddHandler _cancel.Click, Sub()
                                      If _busy Then
                                          _cancellation.Cancel()
                                      Else
                                          Close()
                                      End If
                                  End Sub
        AddHandler FormClosing, Sub(sender, e)
                                    If _busy Then
                                        e.Cancel = True
                                        _cancellation.Cancel()
                                    End If
                                End Sub
    End Sub

    Private Async Sub SignIn(sender As Object, e As EventArgs)
        If _busy Then Return
        If String.IsNullOrWhiteSpace(_email.Text) OrElse _password.Text.Length = 0 Then
            _message.Text = "Enter your Zeta email and password."
            Return
        End If
        _busy = True
        _signIn.Enabled = False
        _email.Enabled = False
        _password.Enabled = False
        _cancellation = New CancellationTokenSource()
        Dim email = _email.Text
        Dim password = _password.Text
        _password.Clear()
        _message.Text = "Signing in to Zeta..."
        Try
            _message.Text = Await _work(email, password, _cancellation.Token)
        Catch ex As OperationCanceledException
            _message.Text = "Sign-in cancelled. No panel operation was sent."
        Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is IO.IOException OrElse
            TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is Security.Cryptography.CryptographicException
            _message.Text = "Sign-in could not complete. Check connection, permissions and protected storage, then retry manually."
        Finally
            email = Nothing
            password = Nothing
            _password.Clear()
            _busy = False
            _signIn.Enabled = True
            _email.Enabled = True
            _password.Enabled = True
            _cancellation.Dispose()
            _cancellation = Nothing
        End Try
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            _password.Clear()
            _email.Clear()
            _cancellation?.Cancel()
        End If
        MyBase.Dispose(disposing)
    End Sub
End Class
