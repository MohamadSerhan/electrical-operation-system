Option Strict On

Imports System.Diagnostics
Imports System.Drawing
Imports System.Linq
Imports System.Windows.Forms
Imports EOEMS.Licensing

''' <summary>Shown when no valid paid license and no active trial are present (mandatory - blocks
''' startup), or opened voluntarily from the shell while a trial is still running (non-mandatory).
''' Displays this machine's Request Code and trial status, lets the customer fill in their name,
''' email and desired plan to generate a request file/email for the vendor, accepts the license
''' key text the vendor issues back with the Keygen tool, and (in mandatory mode) blocks the app
''' from starting until a valid key is registered or the user exits.</summary>
Friend NotInheritable Class LicenseRegistrationForm
    Inherits Form

    Private Const VendorEmail As String = "Mohamad.t.serhan@gmail.com"

    ''' <summary>Wraps a plan so the combo box shows a friendly name while keeping the enum value.</summary>
    Private NotInheritable Class PlanItem
        Public ReadOnly Plan As LicensePlan
        Public Sub New(plan As LicensePlan)
            Plan = plan
        End Sub
        Public Overrides Function ToString() As String
            Return LicensePayload.PlanDisplayName(Plan)
        End Function
    End Class

    Private ReadOnly _isMandatory As Boolean
    Private ReadOnly _requestCodeBox As New TextBox()
    Private ReadOnly _nameBox As New TextBox()
    Private ReadOnly _emailBox As New TextBox()
    Private ReadOnly _planCombo As New ComboBox()
    Private ReadOnly _requestPreviewBox As New TextBox()
    Private ReadOnly _saveRequestButton As Button
    Private ReadOnly _emailRequestButton As Button
    Private ReadOnly _keyBox As New TextBox()
    Private ReadOnly _statusLabel As New Label()
    Private ReadOnly _detailsLabel As New Label()
    Private ReadOnly _registerButton As Button
    Private ReadOnly _copyButton As Button
    Private ReadOnly _exitButton As Button

    Private _pendingRequest As LicenseRequest

    Public Sub New(initialStatus As LicenseStatus, isMandatory As Boolean)
        _isMandatory = isMandatory
        Text = "Electrical Operation Center | License Registration"
        StartPosition = FormStartPosition.CenterScreen
        FormBorderStyle = FormBorderStyle.FixedDialog
        MaximizeBox = False
        MinimizeBox = False
        ShowInTaskbar = True
        ClientSize = New Size(640, 840)
        BackColor = UiTheme.ShellDeep
        Font = New Font("Segoe UI", 9.5F)
        Try
            Dim applicationIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
            If applicationIcon IsNot Nothing Then Icon = applicationIcon
        Catch
        End Try

        Dim isTrial = initialStatus.Payload IsNot Nothing AndAlso initialStatus.Payload.Plan = LicensePlan.Trial
        Dim titleText As String
        Dim introText As String
        If isTrial AndAlso initialStatus.IsValid Then
            Dim days = If(initialStatus.DaysRemaining.HasValue, initialStatus.DaysRemaining.Value, 0)
            titleText = "TRIAL ACTIVE - " & days & If(days = 1, " DAY LEFT", " DAYS LEFT")
            introText = "This computer is running a free 14-day trial. You can keep working until the trial ends, or request a " &
                "license now so there is no interruption. Fill in your details below and either save a request file or email " &
                "it to your vendor; they will send back a License Key to paste in and register below."
        ElseIf isTrial AndAlso Not initialStatus.IsValid Then
            titleText = "YOUR 14-DAY TRIAL HAS EXPIRED"
            introText = "The free trial period for this computer has ended. Fill in your details below and either save a " &
                "request file or email it to your vendor; they will send back a License Key to paste in and register below."
        Else
            titleText = "SOFTWARE LICENSE REQUIRED"
            introText = "This copy of Electrical Operation Center is not registered on this computer. Fill in your details " &
                "below and either save a request file or email it to your vendor; they will send back a License Key to " &
                "paste in and register below."
        End If

        Dim title = UiTheme.Label(titleText, New Font("Segoe UI", 15.0F, FontStyle.Bold), Color.White, False)
        title.Location = New Point(24, 20)
        title.Size = New Size(592, 30)
        Controls.Add(title)

        Dim intro = UiTheme.Label(introText, New Font("Segoe UI", 9.0F), Color.FromArgb(205, 224, 242), False)
        intro.Location = New Point(24, 56)
        intro.Size = New Size(592, 56)
        Controls.Add(intro)

        Dim requestLabel = UiTheme.Label("This computer's Request Code", New Font("Segoe UI", 9.5F, FontStyle.Bold), UiTheme.Cyan, False)
        requestLabel.Location = New Point(24, 118)
        requestLabel.Size = New Size(400, 20)
        Controls.Add(requestLabel)

        _requestCodeBox.Location = New Point(24, 140)
        _requestCodeBox.Size = New Size(468, 26)
        _requestCodeBox.Font = New Font("Consolas", 12.0F, FontStyle.Bold)
        _requestCodeBox.ReadOnly = True
        _requestCodeBox.BackColor = Color.White
        _requestCodeBox.Text = MachineFingerprint.Compute()
        Controls.Add(_requestCodeBox)

        _copyButton = UiTheme.Button("COPY", False)
        _copyButton.Location = New Point(500, 138)
        _copyButton.Size = New Size(116, 30)
        AddHandler _copyButton.Click, Sub()
                                          Try
                                              Clipboard.SetText(_requestCodeBox.Text)
                                              _statusLabel.ForeColor = UiTheme.Cyan
                                              _statusLabel.Text = "Request code copied to the clipboard."
                                          Catch
                                          End Try
                                      End Sub
        Controls.Add(_copyButton)

        ' ----- Request-a-license section -----
        Dim requestSectionLabel = UiTheme.Label("REQUEST A LICENSE", New Font("Segoe UI", 10.5F, FontStyle.Bold), Color.White, False)
        requestSectionLabel.Location = New Point(24, 186)
        requestSectionLabel.Size = New Size(400, 22)
        Controls.Add(requestSectionLabel)

        Dim nameLabel = UiTheme.Label("Your name", New Font("Segoe UI", 9.0F, FontStyle.Bold), UiTheme.Cyan, False)
        nameLabel.Location = New Point(24, 214)
        nameLabel.Size = New Size(280, 18)
        Controls.Add(nameLabel)
        _nameBox.Location = New Point(24, 234)
        _nameBox.Size = New Size(592, 26)
        Controls.Add(_nameBox)

        Dim emailLabel = UiTheme.Label("Your email", New Font("Segoe UI", 9.0F, FontStyle.Bold), UiTheme.Cyan, False)
        emailLabel.Location = New Point(24, 266)
        emailLabel.Size = New Size(280, 18)
        Controls.Add(emailLabel)
        _emailBox.Location = New Point(24, 286)
        _emailBox.Size = New Size(592, 26)
        Controls.Add(_emailBox)

        Dim planLabel = UiTheme.Label("Plan you want to purchase", New Font("Segoe UI", 9.0F, FontStyle.Bold), UiTheme.Cyan, False)
        planLabel.Location = New Point(24, 318)
        planLabel.Size = New Size(280, 18)
        Controls.Add(planLabel)
        _planCombo.DropDownStyle = ComboBoxStyle.DropDownList
        _planCombo.Location = New Point(24, 338)
        _planCombo.Size = New Size(592, 28)
        For Each plan As LicensePlan In {LicensePlan.SixMonths, LicensePlan.OneYear, LicensePlan.TwoYears, LicensePlan.FiveYears, LicensePlan.TenYears, LicensePlan.Lifetime}
            _planCombo.Items.Add(New PlanItem(plan))
        Next
        _planCombo.SelectedIndex = 0
        Controls.Add(_planCombo)

        Dim generateButton = UiTheme.Button("GENERATE REQUEST", False)
        generateButton.Location = New Point(24, 378)
        generateButton.Size = New Size(194, 34)
        AddHandler generateButton.Click, AddressOf OnGenerateRequestClick
        Controls.Add(generateButton)

        _saveRequestButton = UiTheme.Button("SAVE REQUEST FILE...", False)
        _saveRequestButton.Location = New Point(226, 378)
        _saveRequestButton.Size = New Size(194, 34)
        _saveRequestButton.Enabled = False
        AddHandler _saveRequestButton.Click, AddressOf OnSaveRequestClick
        Controls.Add(_saveRequestButton)

        _emailRequestButton = UiTheme.Button("EMAIL REQUEST", False)
        _emailRequestButton.Location = New Point(428, 378)
        _emailRequestButton.Size = New Size(188, 34)
        _emailRequestButton.Enabled = False
        AddHandler _emailRequestButton.Click, AddressOf OnEmailRequestClick
        Controls.Add(_emailRequestButton)

        _requestPreviewBox.Location = New Point(24, 420)
        _requestPreviewBox.Size = New Size(592, 90)
        _requestPreviewBox.Multiline = True
        _requestPreviewBox.ReadOnly = True
        _requestPreviewBox.ScrollBars = ScrollBars.Vertical
        _requestPreviewBox.Font = New Font("Consolas", 8.5F)
        _requestPreviewBox.BackColor = Color.FromArgb(236, 242, 247)
        Controls.Add(_requestPreviewBox)

        ' ----- Register-a-key section -----
        Dim keyLabel = UiTheme.Label("License Key (paste the key your vendor sends back)", New Font("Segoe UI", 9.5F, FontStyle.Bold), UiTheme.Cyan, False)
        keyLabel.Location = New Point(24, 522)
        keyLabel.Size = New Size(500, 20)
        Controls.Add(keyLabel)

        _keyBox.Location = New Point(24, 544)
        _keyBox.Size = New Size(592, 132)
        _keyBox.Multiline = True
        _keyBox.ScrollBars = ScrollBars.Vertical
        _keyBox.Font = New Font("Consolas", 9.5F)
        _keyBox.Text = LicenseStore.LoadRawKey()
        Controls.Add(_keyBox)

        _statusLabel.Location = New Point(24, 684)
        _statusLabel.Size = New Size(592, 44)
        _statusLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        _statusLabel.ForeColor = UiTheme.Amber
        _statusLabel.Text = If(String.IsNullOrEmpty(initialStatus.Reason), "Enter a license key to continue.", initialStatus.Reason)
        Controls.Add(_statusLabel)

        _detailsLabel.Location = New Point(24, 728)
        _detailsLabel.Size = New Size(592, 24)
        _detailsLabel.Font = New Font("Segoe UI", 8.5F)
        _detailsLabel.ForeColor = Color.FromArgb(180, 198, 216)
        Controls.Add(_detailsLabel)
        ShowPayloadDetails(initialStatus.Payload)

        _registerButton = UiTheme.Button("REGISTER", True)
        _registerButton.Location = New Point(24, 764)
        _registerButton.Size = New Size(180, 38)
        AddHandler _registerButton.Click, AddressOf OnRegisterClick
        Controls.Add(_registerButton)

        _exitButton = UiTheme.Button(If(_isMandatory, "EXIT", "CLOSE"), False)
        _exitButton.Location = New Point(436, 764)
        _exitButton.Size = New Size(180, 38)
        AddHandler _exitButton.Click, Sub() DialogResult = DialogResult.Cancel
        Controls.Add(_exitButton)
    End Sub

    Private Sub OnGenerateRequestClick(sender As Object, e As EventArgs)
        If String.IsNullOrWhiteSpace(_nameBox.Text) OrElse String.IsNullOrWhiteSpace(_emailBox.Text) Then
            _statusLabel.ForeColor = UiTheme.Red
            _statusLabel.Text = "Enter your name and email before generating a request."
            Return
        End If
        _pendingRequest = New LicenseRequest With {
            .RequestCode = _requestCodeBox.Text,
            .Licensee = _nameBox.Text.Trim(),
            .Email = _emailBox.Text.Trim(),
            .RequestedPlan = DirectCast(_planCombo.SelectedItem, PlanItem).Plan,
            .RequestedUtc = DateTime.UtcNow
        }
        _requestPreviewBox.Text = _pendingRequest.ToDisplayText()
        _saveRequestButton.Enabled = True
        _emailRequestButton.Enabled = True
        _statusLabel.ForeColor = UiTheme.Cyan
        _statusLabel.Text = "Request generated. Save it to a file or email it to your vendor."
    End Sub

    Private Sub OnSaveRequestClick(sender As Object, e As EventArgs)
        If _pendingRequest Is Nothing Then Return
        Dim safeName = String.Join("_", _pendingRequest.Licensee.Split(IO.Path.GetInvalidFileNameChars()))
        Dim shortCode = New String(_pendingRequest.RequestCode.Where(Function(c) Char.IsLetterOrDigit(c)).ToArray())
        If shortCode.Length > 8 Then shortCode = shortCode.Substring(0, 8)
        Using dialog As New SaveFileDialog With {
            .Filter = "License request (*.eoreq)|*.eoreq|All files (*.*)|*.*",
            .FileName = $"LicenseRequest_{safeName}_{shortCode}.eoreq"
        }
            If dialog.ShowDialog(Me) = DialogResult.OK Then
                IO.File.WriteAllText(dialog.FileName, _pendingRequest.ToFileText())
                _statusLabel.ForeColor = UiTheme.Green
                _statusLabel.Text = "Request file saved to " & dialog.FileName & ". Send this file to your vendor."
            End If
        End Using
    End Sub

    Private Sub OnEmailRequestClick(sender As Object, e As EventArgs)
        If _pendingRequest Is Nothing Then Return
        Try
            Dim subject = $"License request - {_pendingRequest.Licensee} - {LicensePayload.PlanDisplayName(_pendingRequest.RequestedPlan)}"
            Dim body = _pendingRequest.ToDisplayText() & Environment.NewLine & Environment.NewLine &
                "(If your email client does not open, use SAVE REQUEST FILE instead and send it as an attachment.)"
            Dim mailUri = $"mailto:{VendorEmail}?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}"
            Process.Start(New ProcessStartInfo(mailUri) With {.UseShellExecute = True})
            _statusLabel.ForeColor = UiTheme.Green
            _statusLabel.Text = "Opening your email client with the request pre-filled…"
        Catch ex As Exception
            _statusLabel.ForeColor = UiTheme.Red
            _statusLabel.Text = "Could not open an email client (" & ex.Message & "). Use SAVE REQUEST FILE instead."
        End Try
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
