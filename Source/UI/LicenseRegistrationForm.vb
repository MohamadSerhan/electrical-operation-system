Option Strict On

Imports System.Drawing
Imports System.Windows.Forms
Imports EOEMS.Licensing

''' <summary>Shown when no valid license is active. Displays this machine's Request Code to send
''' to the vendor, accepts the license key text they issue back with the Keygen tool, and blocks
''' the app from starting until a valid key is registered (or the user exits).</summary>
Friend NotInheritable Class LicenseRegistrationForm
    Inherits Form

    Private ReadOnly _requestCodeBox As New TextBox()
    Private ReadOnly _keyBox As New TextBox()
    Private ReadOnly _statusLabel As New Label()
    Private ReadOnly _detailsLabel As New Label()
    Private ReadOnly _registerButton As Button
    Private ReadOnly _copyButton As Button
    Private ReadOnly _exitButton As Button

    Public Sub New(initialStatus As LicenseStatus)
        Text = "Electrical Operation Center | License Registration"
        StartPosition = FormStartPosition.CenterScreen
        FormBorderStyle = FormBorderStyle.FixedDialog
        MaximizeBox = False
        MinimizeBox = False
        ShowInTaskbar = True
        ClientSize = New Size(620, 520)
        BackColor = UiTheme.ShellDeep
        Font = New Font("Segoe UI", 9.5F)
        Try
            Dim applicationIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            If applicationIcon IsNot Nothing Then Icon = applicationIcon
        Catch
        End Try

        Dim title = UiTheme.Label("SOFTWARE LICENSE REQUIRED", New Font("Segoe UI", 15.0F, FontStyle.Bold), Color.White, False)
        title.Location = New Point(24, 20)
        title.Size = New Size(570, 30)
        Controls.Add(title)

        Dim intro = UiTheme.Label(
            "This copy of Electrical Operation Center is not registered on this computer. Send the Request Code " &
            "below to your vendor; they will generate a License Key for this machine using the Keygen tool. Paste " &
            "the key you receive into the box below and click Register.",
            New Font("Segoe UI", 9.0F), Color.FromArgb(205, 224, 242), False)
        intro.Location = New Point(24, 56)
        intro.Size = New Size(570, 56)
        Controls.Add(intro)

        Dim requestLabel = UiTheme.Label("This computer's Request Code", New Font("Segoe UI", 9.5F, FontStyle.Bold), UiTheme.Cyan, False)
        requestLabel.Location = New Point(24, 118)
        requestLabel.Size = New Size(400, 20)
        Controls.Add(requestLabel)

        _requestCodeBox.Location = New Point(24, 140)
        _requestCodeBox.Size = New Size(460, 26)
        _requestCodeBox.Font = New Font("Consolas", 12.0F, FontStyle.Bold)
        _requestCodeBox.ReadOnly = True
        _requestCodeBox.BackColor = Color.White
        _requestCodeBox.Text = MachineFingerprint.Compute()
        Controls.Add(_requestCodeBox)

        _copyButton = UiTheme.Button("COPY", False)
        _copyButton.Location = New Point(492, 138)
        _copyButton.Size = New Size(100, 30)
        AddHandler _copyButton.Click, Sub()
                                          Try
                                              Clipboard.SetText(_requestCodeBox.Text)
                                              _statusLabel.ForeColor = UiTheme.Cyan
                                              _statusLabel.Text = "Request code copied to the clipboard."
                                          Catch
                                          End Try
                                      End Sub
        Controls.Add(_copyButton)

        Dim keyLabel = UiTheme.Label("License Key", New Font("Segoe UI", 9.5F, FontStyle.Bold), UiTheme.Cyan, False)
        keyLabel.Location = New Point(24, 186)
        keyLabel.Size = New Size(400, 20)
        Controls.Add(keyLabel)

        _keyBox.Location = New Point(24, 208)
        _keyBox.Size = New Size(568, 180)
        _keyBox.Multiline = True
        _keyBox.ScrollBars = ScrollBars.Vertical
        _keyBox.Font = New Font("Consolas", 9.5F)
        _keyBox.Text = LicenseStore.LoadRawKey()
        Controls.Add(_keyBox)

        _statusLabel.Location = New Point(24, 396)
        _statusLabel.Size = New Size(568, 44)
        _statusLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        _statusLabel.ForeColor = UiTheme.Amber
        _statusLabel.Text = If(String.IsNullOrEmpty(initialStatus.Reason), "Enter a license key to continue.", initialStatus.Reason)
        Controls.Add(_statusLabel)

        _detailsLabel.Location = New Point(24, 440)
        _detailsLabel.Size = New Size(568, 24)
        _detailsLabel.Font = New Font("Segoe UI", 8.5F)
        _detailsLabel.ForeColor = Color.FromArgb(180, 198, 216)
        Controls.Add(_detailsLabel)
        ShowPayloadDetails(initialStatus.Payload)

        _registerButton = UiTheme.Button("REGISTER", True)
        _registerButton.Location = New Point(24, 472)
        _registerButton.Size = New Size(160, 36)
        AddHandler _registerButton.Click, AddressOf OnRegisterClick
        Controls.Add(_registerButton)

        _exitButton = UiTheme.Button("EXIT", False)
        _exitButton.Location = New Point(456, 472)
        _exitButton.Size = New Size(136, 36)
        AddHandler _exitButton.Click, Sub() DialogResult = DialogResult.Cancel
        Controls.Add(_exitButton)
    End Sub

    Private Sub OnRegisterClick(sender As Object, e As EventArgs)
        Dim keyText = _keyBox.Text
        Dim status = LicenseStore.Evaluate(keyText)
        ShowPayloadDetails(status.Payload)
        If status.IsValid Then
            LicenseStore.SaveRawKey(keyText)
            _statusLabel.ForeColor = UiTheme.Green
            _statusLabel.Text = "License registered. Starting the application…"
            DialogResult = DialogResult.OK
            Return
        End If
        _statusLabel.ForeColor = UiTheme.Red
        _statusLabel.Text = status.Reason
    End Sub

    Private Sub ShowPayloadDetails(payload As LicensePayload)
        If payload Is Nothing Then
            _detailsLabel.Text = String.Empty
            Return
        End If
        Dim expiry = If(payload.ExpiresUtc.HasValue, payload.ExpiresUtc.Value.ToLocalTime().ToString("yyyy-MM-dd"), "Never (lifetime)")
        _detailsLabel.Text = $"Licensee: {payload.Licensee}   Plan: {LicensePayload.PlanDisplayName(payload.Plan)}   Expires: {expiry}"
    End Sub
End Class
