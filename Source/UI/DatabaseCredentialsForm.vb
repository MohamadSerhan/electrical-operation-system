Imports System.Drawing
Imports System.Diagnostics
Imports System.Globalization
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms

' Super Administrator only. Shows the state of the PostgreSQL service credential, never its value.
Public NotInheritable Class DatabaseCredentialsForm
    Inherits Form

    Private ReadOnly _authentication As PortableAuthenticationService
    Private ReadOnly _session As UserSession
    Private ReadOnly _host As New TextBox()
    Private ReadOnly _port As New TextBox()
    Private ReadOnly _database As New TextBox()
    Private ReadOnly _adminUser As New TextBox()
    Private ReadOnly _adminPassword As New TextBox()
    Private ReadOnly _eoemsPassword As New TextBox()
    Private ReadOnly _status As New Label()
    Private ReadOnly _message As New Label()
    Private ReadOnly _testButton As Button = UiTheme.Button("Test connection", False)
    Private ReadOnly _provisionButton As Button = UiTheme.Button("Provision / Repair")
    Private ReadOnly _rotateButton As Button = UiTheme.Button("Rotate Service Password", False)
    Private ReadOnly _autoButton As Button = UiTheme.Button("AUTOMATIC SETUP (no PostgreSQL password needed)")

    Public Sub New(settings As SqlSettings, session As UserSession)
        _authentication = New PortableAuthenticationService(settings)
        _session = session
        Text = "Database Credentials"
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.FixedDialog
        MinimizeBox = False
        MaximizeBox = False
        ShowInTaskbar = False
        ClientSize = New Size(500, 810)
        BackColor = UiTheme.Surface
        Font = New Font("Segoe UI", 9.0F)

        Dim title = UiTheme.Label("Database Credentials", New Font("Segoe UI", 19.0F, FontStyle.Bold), UiTheme.Ink)
        title.Location = New Point(28, 18)
        title.Size = New Size(440, 34)
        Controls.Add(title)

        _status.AutoSize = False
        _status.Location = New Point(30, 56)
        _status.Size = New Size(440, 66)
        _status.ForeColor = UiTheme.MutedInk
        _status.Font = New Font("Segoe UI", 8.5F)
        Controls.Add(_status)

        Dim current = PostgresDatabase.LoadSettings()
        _host.Text = current.Host
        _port.Text = current.Port.ToString(CultureInfo.InvariantCulture)
        _database.Text = current.Database
        _adminUser.Text = "postgres"
        AddField("YOUR EOEMS PASSWORD (AUTHORISES THE ACTION)", _eoemsPassword, 128, True)
        AddField("SERVER", _host, 190, False)
        AddField("PORT", _port, 252, False)
        AddField("DATABASE", _database, 314, False)
        AddField("POSTGRESQL ADMIN USER (PROVISION / REPAIR ONLY)", _adminUser, 376, False)
        AddField("POSTGRESQL ADMIN PASSWORD (USED ONCE, NEVER STORED)", _adminPassword, 438, True)

        _message.AutoSize = False
        _message.Location = New Point(30, 508)
        _message.Size = New Size(440, 70)
        _message.ForeColor = UiTheme.MutedInk
        Controls.Add(_message)

        _autoButton.Location = New Point(30, 590)
        _autoButton.Size = New Size(440, 44)
        AddHandler _autoButton.Click, AddressOf OnAutoSetupClicked
        Controls.Add(_autoButton)

        _testButton.Location = New Point(30, 646)
        _testButton.Size = New Size(210, 40)
        AddHandler _testButton.Click, AddressOf OnTestClicked
        Controls.Add(_testButton)

        _provisionButton.Location = New Point(260, 646)
        _provisionButton.Size = New Size(210, 40)
        AddHandler _provisionButton.Click, AddressOf OnProvisionClicked
        Controls.Add(_provisionButton)

        _rotateButton.Location = New Point(30, 698)
        _rotateButton.Size = New Size(440, 40)
        AddHandler _rotateButton.Click, AddressOf OnRotateClicked
        Controls.Add(_rotateButton)

        Dim close = UiTheme.Button("Close", False)
        close.Location = New Point(30, 750)
        close.Size = New Size(440, 40)
        close.DialogResult = DialogResult.Cancel
        Controls.Add(close)
        CancelButton = close

        AddHandler Shown, Async Sub(sender, e) Await RefreshStatusAsync()
    End Sub

    Private Sub AddField(caption As String, box As TextBox, top As Integer, secret As Boolean)
        Dim label = UiTheme.Label(caption, New Font("Segoe UI", 8.0F, FontStyle.Bold), UiTheme.MutedInk)
        label.Location = New Point(31, top)
        label.Size = New Size(440, 18)
        Controls.Add(label)
        box.Location = New Point(30, top + 20)
        box.Size = New Size(440, 30)
        box.Font = New Font("Segoe UI", 10.0F)
        box.MaxLength = 256
        box.UseSystemPasswordChar = secret
        Controls.Add(box)
    End Sub

    Private Async Function RefreshStatusAsync() As Task
        Dim settings = PostgresDatabase.LoadSettings()
        Dim failure = Await Task.Run(Function() PostgresDatabase.TestConnection(settings))
        _status.Text = "Server " & settings.Host & ":" & settings.Port.ToString(CultureInfo.InvariantCulture) &
            " • database " & settings.Database & vbCrLf &
            "Service role: " & settings.Username & " • credential: " &
            If(PostgresServiceAccount.IsProvisioned(), "random, Windows-protected", "NOT PROVISIONED") & vbCrLf &
            "Connection: " & If(failure Is Nothing, "OK (SCRAM-SHA-256)", "FAILED - " & Shorten(failure))
        _status.ForeColor = If(failure Is Nothing, UiTheme.Green, UiTheme.Red)
    End Function

    Private Shared Function Shorten(text As String) As String
        Return If(text.Length > 110, text.Substring(0, 110) & "…", text)
    End Function

    Private Sub SetBusy(busy As Boolean)
        _testButton.Enabled = Not busy
        _provisionButton.Enabled = Not busy
        _rotateButton.Enabled = Not busy
        _autoButton.Enabled = Not busy
    End Sub

    ' One Windows administrator approval: the script briefly trusts local postgres access, lets the
    ' app create the service role, then restores PostgreSQL's original authentication file.
    Private Async Sub OnAutoSetupClicked(sender As Object, e As EventArgs)
        SetBusy(True)
        Try
            If Not Await AuthorizeAsync() Then Return
            Dim script = IO.Path.Combine(AppContext.BaseDirectory, "Tools", "Setup-Database.ps1")
            If Not IO.File.Exists(script) Then
                ShowMessage("The setup script is missing from the installation.", True)
                Return
            End If
            Dim port As Integer
            Integer.TryParse(_port.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, port)
            If port < 1 OrElse port > 65535 Then port = 5432
            Dim resultPath = IO.Path.Combine(AppContext.BaseDirectory, "data", "db-setup-result.txt")
            IO.Directory.CreateDirectory(IO.Path.GetDirectoryName(resultPath))
            If IO.File.Exists(resultPath) Then IO.File.Delete(resultPath)
            ShowMessage("Approve the Windows administrator prompt. PostgreSQL restarts briefly; recording continues in the local buffer.", False)
            Dim start As New ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -File """ & script & """ -ExePath """ & Environment.ProcessPath &
                """ -ResultPath """ & resultPath & """ -Port " & port.ToString(CultureInfo.InvariantCulture)) With {
                .UseShellExecute = True, .Verb = "runas", .WindowStyle = ProcessWindowStyle.Hidden}
            Try
                Await Task.Run(Sub()
                                   Using setupProcess As Process = Process.Start(start)
                                       setupProcess.WaitForExit()
                                   End Using
                               End Sub)
            Catch ex As System.ComponentModel.Win32Exception
                ShowMessage("Windows administrator approval was cancelled. Nothing was changed.", True)
                Return
            End Try
            Dim outcome = If(IO.File.Exists(resultPath), IO.File.ReadAllText(resultPath).Trim(), "FAIL|The setup produced no result.")
            If IO.File.Exists(resultPath) Then IO.File.Delete(resultPath)
            Dim succeeded = outcome.StartsWith("OK|", StringComparison.Ordinal)
            PostgresDatabase.Reconfigure()
            Await _authentication.RecordAuditAsync(_session.Username, succeeded,
                "Automatic PostgreSQL service setup " & If(succeeded, "succeeded", "failed"), CancellationToken.None)
            ShowMessage(If(outcome.Contains("|"), outcome.Substring(outcome.IndexOf("|"c) + 1), outcome), Not succeeded, succeeded)
            Await RefreshStatusAsync()
        Finally
            SetBusy(False)
        End Try
    End Sub

    Private Sub ShowMessage(text As String, isError As Boolean, Optional isSuccess As Boolean = False)
        _message.ForeColor = If(isError, UiTheme.Red, If(isSuccess, UiTheme.Green, UiTheme.MutedInk))
        _message.Text = text
    End Sub

    Private Async Function AuthorizeAsync() As Task(Of Boolean)
        If _session.IsOfflinePreview OrElse Not _session.IsSuperAdministrator Then
            ShowMessage("Only the Super Administrator can manage database credentials.", True)
            Return False
        End If
        If Not Await _authentication.AuthorizeSuperAdministratorAsync(_session.UserId, _eoemsPassword.Text, CancellationToken.None) Then
            ShowMessage("Authorisation failed. Enter your current EOEMS password.", True)
            Return False
        End If
        Return True
    End Function

    Private Async Sub OnTestClicked(sender As Object, e As EventArgs)
        SetBusy(True)
        ShowMessage("Testing connection…", False)
        Await RefreshStatusAsync()
        ShowMessage(If(_status.ForeColor = UiTheme.Green, "Connection successful.", "Connection failed. Use Provision / Repair."),
                    _status.ForeColor <> UiTheme.Green, _status.ForeColor = UiTheme.Green)
        SetBusy(False)
    End Sub

    Private Async Sub OnProvisionClicked(sender As Object, e As EventArgs)
        Dim port As Integer
        If Not Integer.TryParse(_port.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, port) OrElse port < 1 OrElse port > 65535 Then
            ShowMessage("The port must be a number between 1 and 65535.", True)
            Return
        End If
        If String.IsNullOrWhiteSpace(_host.Text) Then
            ShowMessage("Enter the PostgreSQL server.", True)
            Return
        End If
        SetBusy(True)
        Try
            If Not Await AuthorizeAsync() Then Return
            ShowMessage("Provisioning the service role…", False)
            Dim host = _host.Text.Trim()
            Dim database = _database.Text.Trim()
            Dim user = _adminUser.Text
            Dim password = _adminPassword.Text
            Dim result = Await Task.Run(Function() PostgresServiceAccount.Provision(host, port, database, user, password))
            _adminPassword.Clear()
            Await _authentication.RecordAuditAsync(_session.Username, result.Succeeded,
                "PostgreSQL service role provisioning " & If(result.Succeeded, "succeeded", "failed"), CancellationToken.None)
            ShowMessage(result.Message, Not result.Succeeded, result.Succeeded)
            Await RefreshStatusAsync()
        Finally
            SetBusy(False)
        End Try
    End Sub

    Private Async Sub OnRotateClicked(sender As Object, e As EventArgs)
        SetBusy(True)
        Try
            If Not Await AuthorizeAsync() Then Return
            ShowMessage("Rotating the service password…", False)
            Dim user = _adminUser.Text
            Dim password = _adminPassword.Text
            Dim result = Await Task.Run(Function() PostgresServiceAccount.Rotate(user, password))
            _adminPassword.Clear()
            Await _authentication.RecordAuditAsync(_session.Username, result.Succeeded,
                "PostgreSQL service password rotation " & If(result.Succeeded, "succeeded", "failed"), CancellationToken.None)
            ShowMessage(result.Message, Not result.Succeeded, result.Succeeded)
            Await RefreshStatusAsync()
        Finally
            SetBusy(False)
        End Try
    End Sub
End Class
