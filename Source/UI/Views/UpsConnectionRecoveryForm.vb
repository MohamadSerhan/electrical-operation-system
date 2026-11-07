Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms

Public NotInheritable Class UpsConnectionRecoveryForm
    Inherits Form

    Private ReadOnly _session As UserSession
    Private ReadOnly _profile As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 160}
    Private ReadOnly _verify As Button = UiTheme.Button("Verify Existing Credential and Test C04", True)
    Private ReadOnly _retest As Button = UiTheme.Button("Card Configured Locally - Retest C04", False)
    Private ReadOnly _status As New TextBox With {
        .Dock = DockStyle.Fill, .Multiline = True, .ReadOnly = True, .ScrollBars = ScrollBars.Vertical,
        .BackColor = UiTheme.Surface, .BorderStyle = BorderStyle.FixedSingle}
    Private ReadOnly _cancel As Button = UiTheme.Button("Cancel Test", False)
    Private _cancellation As CancellationTokenSource

    Public Sub New(session As UserSession)
        If session Is Nothing OrElse String.IsNullOrWhiteSpace(session.UserId) OrElse String.IsNullOrWhiteSpace(session.Username) OrElse
           Not PlcConfigurationAccess.CanEdit(session.RoleName, session.IsOfflinePreview) Then
            Throw New UnauthorizedAccessException("Only an authenticated administrator can restore a UPS connection.")
        End If
        _session = session
        Text = "Restore UPS Connection - Use Existing Credential"
        Size = New Drawing.Size(1040, 780)
        MinimumSize = New Drawing.Size(780, 600)
        StartPosition = FormStartPosition.CenterParent
        BackColor = UiTheme.Canvas
        Padding = New Padding(14)
        _profile.Items.Add("default")
        _profile.SelectedIndex = 0
        _profile.Enabled = False
        _verify.AutoSize = True
        _retest.AutoSize = True
        _retest.Enabled = False
        _cancel.AutoSize = True
        _cancel.Enabled = False
        Dim heading = UiTheme.Label("C04  |  192.168.107.16  |  Existing credential only", New Drawing.Font("Segoe UI", 13, Drawing.FontStyle.Bold), UiTheme.Ink, False)
        heading.Dock = DockStyle.Top
        heading.Height = 36
        Dim procedure As New TextBox With {
            .Dock = DockStyle.Top, .Height = 250, .Multiline = True, .ReadOnly = True, .ScrollBars = ScrollBars.Vertical,
            .Text = UpsConnectionRecovery.LocalProcedure, .BackColor = UiTheme.Surface, .BorderStyle = BorderStyle.FixedSingle}
        Dim bar As New FlowLayoutPanel With {.Dock = DockStyle.Top, .Height = 70, .AutoScroll = True, .WrapContents = True}
        bar.Controls.AddRange({_profile, _verify, _retest, _cancel})
        Controls.Add(_status)
        Controls.Add(bar)
        Controls.Add(procedure)
        Controls.Add(heading)
        _status.Text = "No secret will be displayed, copied to the clipboard or exported." & Environment.NewLine &
            "This workflow never writes credentials, registry assignments or card settings. Click Verify to check DPAPI, C01/C03 and C04."
        AddHandler _verify.Click, AddressOf OnVerify
        AddHandler _retest.Click, AddressOf OnVerify
        AddHandler _cancel.Click, Sub(sender, e)
                                      If _cancellation IsNot Nothing Then _cancellation.Cancel()
                                  End Sub
        AddHandler FormClosing, Sub(sender, e)
                                    If _cancellation IsNot Nothing Then _cancellation.Cancel()
                                End Sub
    End Sub

    Private Async Sub OnVerify(sender As Object, e As EventArgs)
        If _cancellation IsNot Nothing Then Return
        If sender Is _retest AndAlso MessageBox.Show(Me,
            "Confirm that an authorized administrator has restored C04's matching approved read-only community locally." &
            Environment.NewLine & "EOEMS will only retest C01/C03 and C04; no profile or card setting will be changed.",
            "Authorize read-only retest", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) <> DialogResult.OK Then Return
        Dim cancellation As New CancellationTokenSource()
        _cancellation = cancellation
        _verify.Enabled = False
        _retest.Enabled = False
        _cancel.Enabled = True
        _status.Text = "Checking the stored credential internally, then reading C01/C03 and C04. No secret output. Please wait..."
        Try
            Dim result = Await UpsConnectionRecovery.VerifyAsync(_session, cancellation.Token)
            If IsDisposed Then Return
            _status.Text = Describe(result)
            _retest.Enabled = result.CredentialAvailable AndAlso result.DpapiDecryptionSuccessful AndAlso result.ProfileAssigned AndAlso
                result.ProfilePreserved AndAlso result.ProfileVersion = "v2c" AndAlso result.ReferencesVerified
        Catch ex As OperationCanceledException
            If Not IsDisposed Then _status.Text = "Verification cancelled. No repair is confirmed and no settings were changed."
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
            If Not IsDisposed Then _status.Text = "Recovery blocked: " & ex.Message & Environment.NewLine &
                "Do not overwrite default. Investigate authorized protected backups under the original Windows account."
        Finally
            _cancellation = Nothing
            cancellation.Dispose()
            If Not IsDisposed Then
                _verify.Enabled = True
                _cancel.Enabled = False
            End If
        End Try
    End Sub

    Private Shared Function Describe(result As UpsConnectionRecoveryResult) As String
        Dim yesNo = Function(value As Boolean) If(value, "Yes", "No")
        Dim text As New StringBuilder()
        text.AppendLine("Credential available: " & yesNo(result.CredentialAvailable))
        text.AppendLine("DPAPI decryption successful: " & yesNo(result.DpapiDecryptionSuccessful))
        text.AppendLine("Profile assigned: " & yesNo(result.ProfileAssigned))
        text.AppendLine("Profile: " & result.ProfileName & " | Version: " & result.ProfileVersion)
        text.AppendLine("Working profile preserved: " & yesNo(result.ProfilePreserved))
        For Each test In result.ReferenceTests
            Dim poll = result.ReferencePolls.FirstOrDefault(Function(item) item.DeviceId = test.DeviceId)
            text.AppendLine(test.DeviceId & ": " & UpsConnectionDiagnostics.Describe(test.Finding) &
                " | Electrical read: " & If(poll IsNot Nothing AndAlso poll.IsGood, "Good", "not verified") &
                If(poll IsNot Nothing AndAlso poll.IsGood, " | Successful read UTC: " & poll.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), ""))
        Next
        If result.TargetTest IsNot Nothing Then
            text.AppendLine("C04: " & UpsConnectionDiagnostics.Describe(result.TargetTest.Finding))
            text.AppendLine("  " & result.TargetTest.Summary)
            text.AppendLine("  sysName: " & result.TargetTest.SysName & " | sysObjectID: " & result.TargetTest.SysObjectId)
        End If
        text.AppendLine("Identity read: " & yesNo(result.IdentityRead) & " | Mapping verified: " & yesNo(result.MappingVerified) &
            " | Electrical reads verified: " & yesNo(result.MeasurementsRead))
        text.AppendLine("Matches prior C04 sysName/sysObjectID: " & yesNo(result.PreviousIdentityMatches) &
            " (UPS - 11 / 1.3.6.1.4.1.476.1.42; not an independent serial-number check)")
        text.AppendLine("C04 read-only connection verified: " & yesNo(result.ConnectionVerified))
        text.AppendLine(result.Detail)
        text.AppendLine()
        text.AppendLine("Historian success is separate: after a verified read, check a fresh normal acquisition poll and a GOOD C04 history sample when historian is enabled.")
        Return text.ToString()
    End Function
End Class
