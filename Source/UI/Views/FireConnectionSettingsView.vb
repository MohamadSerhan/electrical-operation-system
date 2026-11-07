Imports System.Globalization
Imports System.IO
Imports System.Security.Cryptography
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Data.Sqlite

Public NotInheritable Class FireConnectionSettingsView
    Inherits UserControl

    Private ReadOnly _service As FireConnectionService
    Private ReadOnly _session As UserSession
    Private ReadOnly _timer As New System.Windows.Forms.Timer With {.Interval = 1000}
    Private ReadOnly _state As New Label With {.AutoSize = True, .Font = New Font("Segoe UI", 10, FontStyle.Bold)}
    Private ReadOnly _summary As New Label With {.AutoSize = True}
    Private ReadOnly _message As New Label With {.AutoSize = True}
    Private ReadOnly _site As New TextBox With {.MaxLength = 128}
    Private ReadOnly _siteName As New TextBox With {.MaxLength = 128}
    Private ReadOnly _mac As New Label With {.AutoSize = True}
    Private ReadOnly _cloud As New TextBox With {.ReadOnly = True}
    Private ReadOnly _api As New TextBox With {.ReadOnly = True}
    Private ReadOnly _token As New TextBox With {.UseSystemPasswordChar = True, .MaxLength = 8192, .PlaceholderText = "Login session token for claims/auth, not its returned claims token; blank retains saved token"}
    Private ReadOnly _tokenState As New Label With {.AutoSize = True}
    Private ReadOnly _poll As New NumericUpDown With {.Minimum = 30, .Maximum = 570, .Value = 30}
    Private ReadOnly _timeout As New NumericUpDown With {.Minimum = 1, .Maximum = 30, .Value = 10}
    Private ReadOnly _freshness As New NumericUpDown With {.Minimum = 30, .Maximum = 600, .Value = 90}
    Private ReadOnly _autoConnect As New CheckBox With {.AutoSize = True, .Text = "Connect automatically on SCADA startup"}
    Private ReadOnly _autoReconnect As New CheckBox With {.AutoSize = True, .Text = "Automatic reconnection", .Checked = True}
    Private ReadOnly _showToken As New CheckBox With {.AutoSize = True, .Text = "Show new token"}
    Private ReadOnly _buttons As New FlowLayoutPanel With {.AutoSize = True, .Dock = DockStyle.Fill, .WrapContents = True}
    Private ReadOnly _columns As New TableLayoutPanel With {.AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink, .Dock = DockStyle.Fill, .ColumnCount = 2, .RowCount = 1}
    Private ReadOnly _connectionType As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 300}
    Private ReadOnly _connectionHost As New Panel With {.Dock = DockStyle.Fill}
    Private ReadOnly _editable As New List(Of Control)
    Private _draft As FireConnectionSettings
    Private _busy As Boolean
    Private _layingOut As Boolean
    Private _active As Boolean
    Private _cancellation As CancellationTokenSource
    Private ReadOnly _alternatives As New Dictionary(Of Integer, LegacyFireConnectionSettingsView)
    Private ReadOnly _alternativeVersions As New Dictionary(Of Integer, String)
    Private ReadOnly _scroll As New Panel With {.Dock = DockStyle.Fill, .AutoScroll = True}

    Public Sub New(service As FireConnectionService, session As UserSession)
        If service Is Nothing Then Throw New ArgumentNullException(NameOf(service))
        _service = service
        _session = session
        _draft = service.ReadSettings()
        If _draft.ConnectionType <> FireConnectionType.ZetaCloud Then _draft.SelectProfile(FireConnectionType.ZetaCloud)
        _draft.CloudUrl = "https://www.zetaremote.com"
        _draft.CloudApiEndpoint = ZetaCloudEventClient.EventRoute
        If _draft.CloudSiteIdentifier.Length = 0 Then _draft.CloudSiteIdentifier = "78392d300758"
        If _draft.CloudSiteName.Length = 0 Then _draft.CloudSiteName = "Twellium-Beverage"
        BackColor = UiTheme.Canvas
        Font = New Font("Segoe UI", 9)

        Dim content As New TableLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .ColumnCount = 1, .Padding = New Padding(6)}
        content.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        Dim status = FireConnectionLayout.Status(_state, _summary)
        AddRow(content, status)
        AddRow(content, New Label With {
            .Text = "Read-only cloud event connector. Authorized API/token access is required; event history does not report the live fire panel condition.",
            .AutoSize = True, .Dock = DockStyle.Fill, .ForeColor = UiTheme.MutedInk,
            .BackColor = Color.FromArgb(240, 245, 252), .Padding = New Padding(8, 0, 8, 0),
            .Margin = New Padding(0, 2, 0, 4)})
        _columns.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50))
        _columns.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50))
        Dim panel = Card("Panel and site information")
        Field(panel, "Manufacturer / model", TextLabel("Zeta Alarm Systems - SmartConnect Multi-loop"))
        Field(panel, "Communication module", TextLabel("SCM-LAN"))
        Field(panel, "Site name", _siteName)
        Field(panel, "Site ID (observed)", _site)
        Field(panel, "Observed site MAC (reference)", _mac)
        Field(panel, "Integration type", TextLabel("Zeta Remote Cloud API - primary"))
        Field(panel, "HTTP / authentication", TextLabel("POST claims/auth {}, then GET events (last 30 UTC days)"))
        Field(panel, "Automatic connection", _autoConnect)
        Field(panel, "Recovery", _autoReconnect)
        Dim api = Card("Cloud API and authentication")
        Field(api, "Approved cloud server", _cloud)
        Field(api, "Observed event endpoint", _api)
        Field(api, "New / replacement login session token", _token)
        Dim secretControls As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .AutoSize = True}
        secretControls.Controls.Add(_showToken)
        secretControls.Controls.Add(Action("Replace Token", "replace-token", Sub()
                                                                                 _token.Clear()
                                                                                 _token.Focus()
                                                                             End Sub))
        secretControls.Controls.Add(Action("Clear Token", "clear-token", Async Sub()
                                                                             If MessageBox.Show(Me, "Remove the protected cloud token and saved Zeta login? Automatic login recovery will be disabled. Historical events and alternative-protocol credentials are retained.", "Clear cloud token", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) = DialogResult.Yes Then
                                                                                 Await ExecuteAsync(Async Function()
                                                                                                        Await _service.SaveAsync(_service.ReadSettings(), "", "", False, _session, clearCloudToken:=True)
                                                                                                        _token.Clear()
                                                                                                        Return "Cloud token and saved login cleared; historical events retained."
                                                                                                    End Function)
                                                                             End If
                                                                         End Sub))
        Field(api, "Token management", secretControls)
        Field(api, "Protected credential", _tokenState)
        Field(api, "Polling interval (seconds)", _poll)
        Field(api, "Request timeout (seconds)", _timeout)
        Field(api, "Freshness threshold (seconds)", _freshness)
        _columns.Controls.Add(panel, 0, 0)
        _columns.Controls.Add(api, 1, 0)
        AddRow(content, _columns)
        _editable.AddRange({_site, _siteName, _token, _showToken, _poll, _timeout, _freshness, _autoConnect, _autoReconnect})
        _buttons.Controls.Add(Action("Save Settings", "save", Async Sub() Await SaveAsync()))
        _buttons.Controls.Add(Action("Sign in to Zeta", "zeta-sign-in", AddressOf SignInToZeta))
        _buttons.Controls.Add(Action("Test Connection", "test-connection", Async Sub() Await TestAsync(True, False)))
        _buttons.Controls.Add(Action("Test Authentication", "test-auth", Async Sub() Await TestAsync(False, False)))
        _buttons.Controls.Add(Action("Test Event Retrieval", "test-events", Async Sub() Await TestAsync(False, True)))
        _buttons.Controls.Add(Action("Connect", "connect", Async Sub() Await ExecuteAsync(Async Function()
                                                                                              EnsureSaved()
                                                                                              Await _service.ConnectAsync(_session)
                                                                                              Return "Background event polling started. Fire panel condition remains UNKNOWN."
                                                                                          End Function)))
        _buttons.Controls.Add(Action("Disconnect", "disconnect", Async Sub() Await ExecuteAsync(Async Function()
                                                                                                    Await _service.DisconnectAsync(_session)
                                                                                                    Return "SCADA connector disconnected; no panel or vendor service was changed."
                                                                                                End Function)))
        _buttons.Controls.Add(Action("Reconnect", "reconnect", Async Sub() Await ExecuteAsync(Async Function()
                                                                                                  EnsureSaved()
                                                                                                  Await _service.DisconnectAsync(_session)
                                                                                                  Await _service.ConnectAsync(_session)
                                                                                                  Return "Cloud event connector reconnecting."
                                                                                              End Function)))
        _buttons.Controls.Add(Action("Clear Diagnostic Logs", "clear-diagnostics", Async Sub() Await ExecuteAsync(Async Function()
                                                                                                                      Await _service.ClearDiagnosticLogsAsync(_session)
                                                                                                                      Return "Diagnostics cleared. Historical fire events retained."
                                                                                                                  End Function)))
        _buttons.Controls.Add(Action("Copy Diagnostics", "copy-diagnostics", AddressOf CopyDiagnostics))
        _buttons.Controls.Add(Action("Cancel Test", "cancel", Sub() _cancellation?.Cancel()))
        _message.Text = "Cloud ONLINE means validated event synchronization, not FIRE PANEL NORMAL. Not a sole life-safety annunciation source."
        _scroll.Controls.Add(content)
        FireConnectionLayout.FillViewport(_scroll, content)
        _connectionHost.Controls.Add(_scroll)
        _connectionHost.Controls.Add(FireConnectionLayout.Footer(_buttons, _message))
        Controls.Add(_connectionHost)
        Dim selector As New FlowLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(10, 5, 10, 5)}
        selector.Controls.Add(TextLabel("Connection type", True))
        _connectionType.Items.AddRange({"Zeta Remote Cloud API", "Local Modbus TCP", "Zeta BMS"})
        _connectionType.SelectedIndex = 0
        selector.Controls.Add(_connectionType)
        Controls.Add(selector)
        AddHandler _connectionType.SelectedIndexChanged, AddressOf SelectConnection
        AddHandler _site.TextChanged, Sub() _mac.Text = If(_site.Text = "78392d300758", "78:39:2D:30:07:58 (not live-verified)", "Not verified for edited site")
        AddHandler _showToken.CheckedChanged, Sub() _token.UseSystemPasswordChar = Not _showToken.Checked
        AddHandler _timer.Tick, AddressOf RefreshStatus
        Dim fit As Action = Sub()
                                If _layingOut OrElse Not IsHandleCreated OrElse Not _scroll.Visible Then Return
                                _layingOut = True
                                Try
                                    FireConnectionLayout.FitFields(panel)
                                    FireConnectionLayout.FitFields(api)
                                Finally
                                    _layingOut = False
                                End Try
                            End Sub
        AddHandler _columns.SizeChanged, Sub() fit()
        AddHandler _scroll.VisibleChanged, Sub() fit()
        AddHandler _tokenState.TextChanged, Sub() fit()
        AddHandler Resize, Sub()
                               Dim narrow = ClientSize.Width < 850
                               _columns.ColumnCount = If(narrow, 1, 2)
                               _columns.SetCellPosition(api, New TableLayoutPanelCellPosition(If(narrow, 0, 1), If(narrow, 1, 0)))
                               _columns.ColumnStyles(0).Width = If(narrow, 100, 50)
                               _summary.MaximumSize = New Size(Math.Max(300, ClientSize.Width - 65), 0)
                               _state.MaximumSize = _summary.MaximumSize
                           End Sub
        LoadDraft()
        RefreshStatus(Nothing, EventArgs.Empty)
        ApplyPermissions()
    End Sub

    Protected Overrides Sub OnLoad(e As EventArgs)
        MyBase.OnLoad(e)
        BeginInvoke(Sub()
                        If Not IsDisposed Then _scroll.AutoScrollPosition = Point.Empty
                    End Sub)
    End Sub

    Private Shared Function TextLabel(text As String, Optional bold As Boolean = False) As Label
        Return New Label With {.Text = text, .AutoSize = True, .ForeColor = UiTheme.Ink,
            .Font = New Font("Segoe UI", 9, If(bold, FontStyle.Bold, FontStyle.Regular)), .Margin = New Padding(4, 5, 4, 5)}
    End Function

    Private Shared Sub AddRow(table As TableLayoutPanel, control As Control)
        Dim row = table.RowCount
        table.RowCount += 1
        table.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        table.Controls.Add(control, 0, row)
    End Sub

    Private Shared Function Card(title As String) As TableLayoutPanel
        Dim table As New TableLayoutPanel With {.ColumnCount = 2, .AutoSize = True, .Dock = DockStyle.Fill, .BackColor = UiTheme.Surface, .Padding = New Padding(10, 6, 10, 6), .Margin = New Padding(3)}
        table.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 38))
        table.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 62))
        Dim heading = TextLabel(title, True)
        heading.ForeColor = UiTheme.Blue
        heading.Margin = New Padding(0, 0, 0, 6)
        table.Controls.Add(heading, 0, 0)
        table.SetColumnSpan(heading, 2)
        table.RowCount = 1
        table.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        Return table
    End Function

    Private Shared Sub Field(table As TableLayoutPanel, text As String, input As Control)
        Dim row = table.RowCount
        table.RowCount += 1
        table.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        Dim caption = TextLabel(text)
        caption.Margin = New Padding(0, 2, 5, 2)
        table.Controls.Add(caption, 0, row)
        input.Margin = New Padding(0, 2, 5, 2)
        input.Dock = DockStyle.Fill
        If TypeOf input Is CheckBox Then input.Dock = DockStyle.Top
        table.Controls.Add(input, 1, row)
    End Sub

    Private Function Action(text As String, name As String, handler As Action) As Button
        Dim button = UiTheme.ActionButton(text, name = "save")
        button.Name = name
        button.AutoSize = True
        button.Margin = New Padding(3)
        button.Height = 30
        AddHandler button.Click, Sub() handler()
        Return button
    End Function

    Private Sub LoadDraft()
        _site.Text = _draft.CloudSiteIdentifier
        _siteName.Text = _draft.CloudSiteName
        _cloud.Text = _draft.CloudUrl
        _api.Text = ZetaCloudEventClient.EventRoute
        _poll.Value = Math.Clamp(_draft.PollSeconds, 30, 570)
        _timeout.Value = Math.Clamp(_draft.TimeoutMilliseconds / 1000D, 1D, 30D)
        _freshness.Value = Math.Clamp(_draft.FreshnessSeconds, 30, 600)
        _autoConnect.Checked = _draft.AutoConnect
        _autoReconnect.Checked = _draft.AutoReconnect
    End Sub

    Private Function ReadDraft() As FireConnectionSettings
        Dim settings = _service.ReadSettings()
        If settings.ConnectionType <> FireConnectionType.ZetaCloud Then settings.SelectProfile(FireConnectionType.ZetaCloud)
        settings.ConnectionType = FireConnectionType.ZetaCloud
        settings.CloudUrl = _cloud.Text.Trim()
        settings.CloudApiEndpoint = _api.Text
        settings.CloudSiteIdentifier = _site.Text.Trim()
        settings.CloudSiteName = _siteName.Text.Trim()
        settings.CloudAuthentication = "API token"
        settings.PollSeconds = CInt(_poll.Value)
        settings.TimeoutMilliseconds = CInt(_timeout.Value * 1000)
        settings.FreshnessSeconds = CInt(_freshness.Value)
        settings.AutoConnect = _autoConnect.Checked
        settings.AutoReconnect = _autoReconnect.Checked
        settings.MappingVerified = False
        settings.MappingProof = String.Empty
        settings.CaptureProfile(FireConnectionType.ZetaCloud)
        settings.Validate()
        Return settings
    End Function

    Private Sub EnsureSaved()
        If JsonSerializer.Serialize(ReadDraft()) <> JsonSerializer.Serialize(_service.ReadSettings()) OrElse _token.Text.Length > 0 Then Throw New InvalidOperationException("Save settings and any replacement token before Connect, Reconnect or authenticated tests.")
    End Sub

    Private Async Function SaveAsync() As Task
        Await ExecuteAsync(Async Function()
                               Await _service.SaveAsync(ReadDraft(), "", "", False, _session, cloudToken:=_token.Text)
                               _token.Clear()
                               _showToken.Checked = False
                               _draft = _service.ReadSettings()
                               Return "Settings saved securely. No fire panel settings were changed; verify site ID and API contract during authorized testing."
                           End Function)
    End Function

    Private Sub SignInToZeta()
        Using dialog As New ZetaSignInDialog(Async Function(email, password, cancellation)
                                                 Dim settings = ReadDraft()
                                                 settings.AutoConnect = True
                                                 settings.AutoReconnect = True
                                                 Dim result = Await _service.SignInCloudAsync(settings, email, password, _session, cancellation)
                                                 _token.Clear()
                                                 _showToken.Checked = False
                                                 _draft = _service.ReadSettings()
                                                 _autoConnect.Checked = _draft.AutoConnect
                                                 _autoReconnect.Checked = _draft.AutoReconnect
                                                 RefreshStatus(Nothing, EventArgs.Empty)
                                                 Return If(result.Success, result.Events.Count.ToString(CultureInfo.InvariantCulture) &
                                                     " validated historical events synchronized. Live panel: UNKNOWN.",
                                                     result.ErrorMessage & " Stage: " & result.Stage)
                                             End Function)
            dialog.ShowDialog(Me)
        End Using
    End Sub

    Private Async Function TestAsync(connectivityOnly As Boolean, retrieveEvents As Boolean) As Task
        Await ExecuteAsync(Async Function()
                               If Not connectivityOnly Then EnsureSaved()
                               Dim result = Await _service.TestCloudAsync(ReadDraft(), connectivityOnly, retrieveEvents, _session, _cancellation.Token)
                               Dim detail = If(connectivityOnly, result.ErrorMessage, If(result.Success,
                                   If(retrieveEvents, result.Events.Count.ToString(CultureInfo.InvariantCulture) & " validated event(s) synchronized. ", "Authorized event response JSON validated. "), result.ErrorMessage))
                               Return detail & " HTTP " & If(result.HttpStatus.HasValue, result.HttpStatus.Value.ToString(CultureInfo.InvariantCulture), "unavailable") &
                                   "; stage: " & result.Stage & "; origin: " & If(result.ErrorOrigin.Length = 0, "none", result.ErrorOrigin) &
                                   "; " & result.ResponseMilliseconds.ToString("0", CultureInfo.InvariantCulture) & " ms. Last returned event UTC: " &
                                   If(result.Events.Any(Function(item) item.TimestampUtc.HasValue),
                                      result.Events.Where(Function(item) item.TimestampUtc.HasValue).Max(Function(item) item.TimestampUtc.Value).ToString("O"), "not returned")
                           End Function)
    End Function

    Private Async Function ExecuteAsync(work As Func(Of Task(Of String))) As Task
        If _busy Then Return
        _busy = True
        _cancellation = New CancellationTokenSource
        ApplyPermissions()
        _message.Text = "Working..."
        Try
            _message.Text = Await work()
            _message.ForeColor = UiTheme.Ink
        Catch ex As OperationCanceledException
            _message.Text = "Request cancelled. No fire panel operation was sent."
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
            TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is CryptographicException OrElse TypeOf ex Is SqliteException OrElse TypeOf ex Is FormatException OrElse TypeOf ex Is JsonException
            _message.Text = If(TypeOf ex Is CryptographicException OrElse TypeOf ex Is SqliteException, "Protected local storage could not be accessed. Check the original Windows account and file permissions.", ex.Message)
            _message.ForeColor = Color.Firebrick
        Finally
            _busy = False
            _cancellation.Dispose()
            _cancellation = Nothing
            If Not IsDisposed Then
                ApplyPermissions()
                RefreshStatus(Nothing, EventArgs.Empty)
            End If
        End Try
    End Function

    Private Sub ApplyPermissions()
        _connectionType.Enabled = Not _busy
        Dim edit = _session IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(_session.UserId) AndAlso
            Not String.IsNullOrWhiteSpace(_session.Username) AndAlso PlcConfigurationAccess.CanEdit(_session.RoleName, _session.IsOfflinePreview)
        For Each control In _editable
            control.Enabled = edit AndAlso Not _busy
        Next
        For Each button As Button In _buttons.Controls.OfType(Of Button)()
            button.Enabled = If(button.Name = "cancel", _busy, If(button.Name = "copy-diagnostics", Not _busy, edit AndAlso Not _busy))
        Next
        For Each control In Descendants(Me).Where(Function(item) {"replace-token", "clear-token", "refresh-events"}.Contains(item.Name))
            control.Enabled = edit AndAlso Not _busy
        Next
    End Sub

    Private Shared Iterator Function Descendants(parent As Control) As IEnumerable(Of Control)
        For Each child As Control In parent.Controls
            Yield child
            For Each descendant In Descendants(child)
                Yield descendant
            Next
        Next
    End Function

    Private Sub RefreshStatus(sender As Object, e As EventArgs)
        Dim status = _service.ReadStatus()
        Dim saved = _service.ReadSettings()
        Dim cloudActive = status.ConnectionType = FireConnectionType.ZetaCloud
        Dim state = If(Not cloudActive, "NOT CONFIGURED (saved connection is an alternative protocol)", status.State)
        _state.Text = state & " | Cloud event connector only | Live fire panel: UNKNOWN"
        _state.ForeColor = If(state = "ONLINE", Color.SeaGreen, If(state = "DEGRADED", Color.DarkGoldenrod, UiTheme.Ink))
        _summary.Text = "Saved server: " & saved.CloudUrl & " | Saved site ID: " & If(saved.CloudSiteIdentifier.Length = 0, "not configured", saved.CloudSiteIdentifier) & " | Auth: " & status.CloudAuthentication &
            Environment.NewLine & "Last successful request: " & Stamp(status.LastSuccessUtc) & " | Last synchronization: " & Stamp(status.LastDataUtc) &
            " | HTTP: " & If(status.HttpStatus.HasValue, status.HttpStatus.Value.ToString(CultureInfo.InvariantCulture), "not tested") &
            " | Response: " & If(status.ResponseMilliseconds.HasValue, status.ResponseMilliseconds.Value.ToString("0", CultureInfo.InvariantCulture) & " ms", "not tested") &
            Environment.NewLine & "Saved polling: " & saved.PollSeconds.ToString(CultureInfo.InvariantCulture) & " s | Consecutive failures: " &
            status.ConsecutiveFailures.ToString(CultureInfo.InvariantCulture) &
            " | Session expiry: " & If(status.CloudSessionExpiresUtc.HasValue, status.CloudSessionExpiresUtc.GetValueOrDefault().ToString("u"), "not available") &
            " | Stage: " & status.CloudStage & " | Origin: " & If(status.CloudErrorOrigin.Length = 0, "none", status.CloudErrorOrigin) &
            " | Last error: " & If(status.LastError.Length = 0, "none recorded", status.LastError)
        _tokenState.Text = If(status.CloudCredentialRenewalRequired, "Zeta rejected authorization. Verify account/site permission or update the saved login, then reconnect.",
            If(status.HasSavedCloudLogin, "Login and token encrypted with Windows DPAPI; automatic renewal follows Recovery setting.",
               If(status.HasCloudToken, "Session token saved with Windows DPAPI; sign in once to enable encrypted login recovery.", "NOT CONFIGURED - sign in to save a protected login.")))
    End Sub

    Private Shared Function Stamp(value As DateTime?) As String
        Return If(value.HasValue, value.Value.ToString("u", CultureInfo.InvariantCulture), "not available")
    End Function

    Private Sub CopyDiagnostics()
        Try
            Clipboard.SetText(_service.DiagnosticExport())
            _message.Text = "Diagnostics copied; credentials excluded."
            _message.ForeColor = UiTheme.Ink
        Catch ex As System.Runtime.InteropServices.ExternalException
            _message.Text = "Copy failed: clipboard is unavailable."
            _message.ForeColor = Color.Firebrick
        End Try
    End Sub

    Private Sub SelectConnection(sender As Object, e As EventArgs)
        Dim selected = _connectionType.SelectedIndex
        _scroll.Visible = selected = 0
        _buttons.Parent.Visible = selected = 0
        For Each pair In _alternatives
            pair.Value.Visible = pair.Key = selected
            pair.Value.SetPageActive(_active AndAlso pair.Key = selected)
        Next
        If selected > 0 Then
            Dim version = JsonSerializer.Serialize(_service.ReadSettings())
            If _alternatives.ContainsKey(selected) AndAlso _alternativeVersions(selected) <> version Then
                Dim previous = _alternatives(selected)
                _connectionHost.Controls.Remove(previous)
                _alternatives.Remove(selected)
                previous.Dispose()
            End If
            If Not _alternatives.ContainsKey(selected) Then
                Dim mode = If(selected = 1, FireConnectionType.LocalModbus, FireConnectionType.BmsVendor)
                Dim editor As New LegacyFireConnectionSettingsView(_service, _session, mode) With {.Dock = DockStyle.Fill}
                AddHandler editor.BusyChanged, Sub() _connectionType.Enabled = Not editor.IsBusy
                _alternatives.Add(selected, editor)
                _alternativeVersions(selected) = version
                _connectionHost.Controls.Add(editor)
            End If
            _alternatives(selected).BringToFront()
            _alternatives(selected).SetPageActive(_active)
        Else
            _scroll.BringToFront()
            RefreshStatus(Nothing, EventArgs.Empty)
        End If
        _timer.Enabled = _active AndAlso selected = 0
    End Sub

    Public Sub SetPageActive(active As Boolean)
        _active = active
        _timer.Enabled = active AndAlso _connectionType.SelectedIndex = 0
        For Each pair In _alternatives
            pair.Value.SetPageActive(active AndAlso pair.Key = _connectionType.SelectedIndex)
        Next
        If active Then RefreshStatus(Nothing, EventArgs.Empty)
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            _timer.Stop()
            _timer.Dispose()
            _cancellation?.Cancel()
        End If
        MyBase.Dispose(disposing)
    End Sub
End Class
