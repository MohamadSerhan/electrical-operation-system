Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Threading.Tasks

Public NotInheritable Class LegacyFireConnectionSettingsView
    Inherits UserControl

    Private ReadOnly _service As FireConnectionService
    Private ReadOnly _session As UserSession
    Private ReadOnly _timer As New System.Windows.Forms.Timer With {.Interval = 1000}
    Private ReadOnly _layoutTimer As New System.Windows.Forms.Timer With {.Interval = 120}
    Private ReadOnly _type As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _bmsType As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _state As New Label
    Private ReadOnly _summary As New Label
    Private ReadOnly _diagnostics As New Label
    Private ReadOnly _message As New Label
    Private ReadOnly _endpoint As New TextBox
    Private ReadOnly _port As NumericUpDown = Number(1, 65535)
    Private ReadOnly _unit As NumericUpDown = Number(1, 247)
    Private ReadOnly _timeout As NumericUpDown = Number(250, 30000)
    Private ReadOnly _retries As NumericUpDown = Number(0, 5)
    Private ReadOnly _retryInterval As NumericUpDown = Number(5, 300)
    Private ReadOnly _poll As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _freshness As NumericUpDown = Number(2, 600)
    Private ReadOnly _address As NumericUpDown = Number(0, 65535)
    Private ReadOnly _count As NumericUpDown = Number(1, 125)
    Private ReadOnly _verified As New CheckBox With {.Text = "Vendor FC04 mapping reviewed (test required)", .AutoSize = True}
    Private ReadOnly _mappingStatus As New Label With {.AutoSize = True}
    Private ReadOnly _modeNotice As New Label With {.AutoSize = True, .ForeColor = UiTheme.MutedInk, .Padding = New Padding(8, 3, 8, 0)}
    Private ReadOnly _authentication As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _role As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _connectionDirection As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _bmsReference As New TextBox With {.MaxLength = 256, .PlaceholderText = "Vendor protocol specification / version"}
    Private ReadOnly _siteIdentifier As New TextBox With {.MaxLength = 256}
    Private ReadOnly _panelNetworkMode As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _panelNetworkAddress As New TextBox With {.MaxLength = 32}
    Private ReadOnly _panelNetworkName As New TextBox With {.MaxLength = 128}
    Private ReadOnly _panelNetworkNotice As New Label With {.AutoSize = True}
    Private ReadOnly _reference As New TextBox With {.MaxLength = 256, .PlaceholderText = "Vendor mapping document / version"}
    Private ReadOnly _autoConnect As New CheckBox With {.Text = "Connect on SCADA startup", .AutoSize = True}
    Private ReadOnly _autoReconnect As New CheckBox With {.Text = "Automatic reconnection", .AutoSize = True}
    Private ReadOnly _cloud As New TextBox
    Private ReadOnly _api As New TextBox With {.PlaceholderText = "Official HTTPS endpoint only (not implemented)"}
    Private ReadOnly _username As New TextBox With {.MaxLength = 256, .PlaceholderText = "Blank retains saved username"}
    Private ReadOnly _password As New TextBox With {.UseSystemPasswordChar = True, .MaxLength = 1024, .PlaceholderText = "Blank retains saved password"}
    Private ReadOnly _clearCredentials As New CheckBox With {.Text = "Remove this mode's credentials on Save", .AutoSize = True}
    Private ReadOnly _credentials As New Label With {.AutoSize = True}
    Private ReadOnly _dynamic As New Panel
    Private ReadOnly _dynamicAdvanced As New Panel
    Private ReadOnly _columns As New TableLayoutPanel
    Private ReadOnly _common As TableLayoutPanel = Fields()
    Private ReadOnly _advanced As New TableLayoutPanel
    Private ReadOnly _advancedCommon As TableLayoutPanel = Fields()
    Private ReadOnly _selector As TableLayoutPanel = Fields()
    Private ReadOnly _content As New TableLayoutPanel
    Private ReadOnly _scroll As New Panel With {.Dock = DockStyle.Fill, .AutoScroll = True}
    Private ReadOnly _testValues As New Dictionary(Of String, Label)
    Private ReadOnly _monitorValues As New Dictionary(Of String, Label)
    Private ReadOnly _buttons As New FlowLayoutPanel
    Private ReadOnly _editable As New List(Of Control)
    Private _draft As FireConnectionSettings
    Private _lastType As FireConnectionType
    Private _loading As Boolean
    Private _busy As Boolean
    Private _statusRendered As Boolean
    Private ReadOnly _pendingUsername As New Dictionary(Of FireConnectionType, String)
    Private ReadOnly _pendingPassword As New Dictionary(Of FireConnectionType, String)
    Private ReadOnly _pendingClear As New Dictionary(Of FireConnectionType, Boolean)
    Private Const MinimumLabelWrapWidth As Integer = 120
    Private ReadOnly _fixedMode As FireConnectionType?
    Private _detachedDiagnostics As Control
    Public Event BusyChanged As EventHandler

    Public ReadOnly Property IsBusy As Boolean
        Get
            Return _busy
        End Get
    End Property

    Public Sub New(service As FireConnectionService, session As UserSession, Optional fixedMode As FireConnectionType? = Nothing)
        If service Is Nothing Then Throw New ArgumentNullException(NameOf(service))
        _service = service
        _session = session
        _fixedMode = fixedMode
        _draft = service.ReadSettings()
        BackColor = UiTheme.Canvas
        Font = New Font("Segoe UI", 9.0F)
        AutoScroll = False
        Dim root = _content
        SuspendLayout()
        root.SuspendLayout()
        _scroll.SuspendLayout()
        root.Dock = DockStyle.Top
        root.AutoSize = True
        root.AutoSizeMode = AutoSizeMode.GrowAndShrink
        root.ColumnCount = 1
        root.Padding = If(_fixedMode.HasValue, New Padding(6), New Padding(14, 6, 14, 14))
        root.BackColor = UiTheme.Canvas
        root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        _scroll.BackColor = UiTheme.Canvas
        ' The overview status box is intentionally not shown; state/summary stay as hidden data for diagnostics.
        _state.AutoSize = True
        _summary.AutoSize = True
        If _fixedMode.HasValue Then
            _state.Font = New Font("Segoe UI", 10, FontStyle.Bold)
            AddRow(root, FireConnectionLayout.Status(_state, _summary))
        End If
        _type.Items.AddRange({"Local Modbus TCP", "Legacy Cloud metadata (not primary)", "Zeta BMS Server"})
        _bmsType.Items.AddRange({"Modbus TCP (independent verification required)", "Zeta Vendor BMS Protocol (unsupported)"})
        _authentication.Items.AddRange({"Not configured", "None", "Username / password", "API token"})
        _role.Items.AddRange({"Unverified", "Client", "Server"})
        _connectionDirection.Items.AddRange({"Unverified", "Panel initiates outbound connection (reference only)", "SCADA connects to server (reference only)"})
        _panelNetworkMode.Items.AddRange({"Disabled", "Enabled Local", "Enabled Global"})
        _poll.Items.AddRange({1, 2, 5, 10, 30}.Cast(Of Object).ToArray())
        Dim selector = _selector
        selector.SuspendLayout()
        AddField(selector, "Connection Method", _type)
        selector.ResumeLayout(True)
        ConfigureColumns(_columns)
        _columns.Controls.Add(_common, 0, 0)
        _dynamic.Dock = DockStyle.Fill
        _dynamic.AutoSize = True
        _columns.Controls.Add(_dynamic, 1, 0)
        ConfigureColumns(_advanced)
        _dynamicAdvanced.Dock = DockStyle.Fill
        _dynamicAdvanced.AutoSize = True
        _advanced.Controls.Add(_dynamicAdvanced, 0, 0)
        _advanced.Controls.Add(_advancedCommon, 1, 0)
        _modeNotice.Padding = New Padding(0, 2, 0, 8)
        _modeNotice.BackColor = Color.FromArgb(240, 245, 252)
        _modeNotice.Padding = New Padding(8, 6, 8, 6)
        _modeNotice.Margin = New Padding(0, 2, 0, 4)
        Dim panelNetwork = Fields()
        panelNetwork.SuspendLayout()
        AddField(panelNetwork, "Panel network mode", _panelNetworkMode)
        AddField(panelNetwork, "Panel network address", _panelNetworkAddress)
        AddField(panelNetwork, "Panel network name", _panelNetworkName)
        _panelNetworkNotice.Dock = DockStyle.Fill
        _panelNetworkNotice.MaximumSize = New Size(900, 0)
        _panelNetworkNotice.ForeColor = UiTheme.MutedInk
        _panelNetworkNotice.Text = "Disabled: panel-network connection is disabled. Enabled Local: this panel does not receive remote-panel alarms/faults. Enabled Global: this panel receives remote-panel alarms/faults. These are panel-network modes, not SCADA connection types. MANUALLY RECORDED — NOT LIVE VERIFIED; SCADA cannot change the physical mode. Scope: This Panel Only; other panels require separately documented addressing/mappings. SCADA source IP reference: 192.168.105.29."
        AddNote(panelNetwork, _panelNetworkNotice)
        panelNetwork.ResumeLayout(True)
        If _fixedMode.HasValue Then
            _modeNotice.Padding = New Padding(8, 0, 8, 0)
            panelNetwork.Controls.Remove(_panelNetworkNotice)
            Dim networkFields = {_panelNetworkMode, DirectCast(_panelNetworkAddress, Control), _panelNetworkName}
            panelNetwork.SuspendLayout()
            For Each networkInput As Control In networkFields
                panelNetwork.Controls.Remove(networkInput)
            Next
            For Each captionControl As Control In panelNetwork.Controls.Cast(Of Control).ToArray()
                captionControl.Dispose()
            Next
            panelNetwork.Controls.Clear()
            panelNetwork.ColumnStyles.Clear()
            panelNetwork.RowStyles.Clear()
            panelNetwork.ColumnCount = 3
            panelNetwork.RowCount = 2
            Dim captions = {"Network mode", "Address", "Name"}
            For index = 0 To 2
                panelNetwork.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F / 3))
                panelNetwork.Controls.Add(New Label With {.Text = captions(index), .AutoSize = True}, index, 0)
                networkFields(index).Dock = DockStyle.Fill
                networkFields(index).MaximumSize = Size.Empty
                panelNetwork.Controls.Add(networkFields(index), index, 1)
            Next
            panelNetwork.ResumeLayout(True)
            Dim cards As New TableLayoutPanel
            ConfigureColumns(cards)
            cards.Name = "connection-cards"
            Dim connection = FireConnectionLayout.Card("Connection settings", _common, _dynamic,
                                                       Heading("Panel network - reference only"), panelNetwork, _panelNetworkNotice)
            Dim protocol = FireConnectionLayout.Card(If(_fixedMode.Value = FireConnectionType.BmsVendor, "", "Protocol, authentication and recovery"),
                                                      selector, _dynamicAdvanced, _advancedCommon)
            cards.Controls.Add(connection, 0, 0)
            cards.Controls.Add(protocol, 1, 0)
            AddRow(root, _modeNotice)
            AddRow(root, cards)
            AddHandler SizeChanged, Sub()
                                       Dim narrow = ClientSize.Width < 850
                                       cards.ColumnCount = If(narrow, 1, 2)
                                       cards.SetCellPosition(protocol, New TableLayoutPanelCellPosition(If(narrow, 0, 1), If(narrow, 1, 0)))
                                       cards.ColumnStyles(0).Width = If(narrow, 100, 50)
                                   End Sub
            _panelNetworkNotice.Text = "Reference only; not live verified. Editing these fields does not configure the panel."
            Dim help As New ToolTip With {.AutoPopDelay = 30000}
            help.SetToolTip(_panelNetworkMode, "Disabled: no panel-network connection. Enabled Local: this panel does not receive remote-panel alarms/faults. Enabled Global: this panel receives remote-panel alarms/faults. These are panel-network modes, not SCADA connection types. Manually recorded, not live verified; SCADA cannot change the physical mode. Scope: This Panel Only; other panels require separately documented addressing/mappings. SCADA source IP reference: 192.168.105.29.")
            AddHandler Disposed, Sub() help.Dispose()
        Else
            AddSection(root, "CONNECTION SETTINGS", True, selector, _modeNotice, _columns,
                       Heading("ADVANCED SETTINGS"), _advanced, Heading("PANEL NETWORK INFORMATION - REFERENCE ONLY"), panelNetwork)
        End If
        Dim diagnosticColumns As New TableLayoutPanel
        ConfigureColumns(diagnosticColumns)
        Dim lastTest = Fields()
        lastTest.SuspendLayout()
        AddNote(lastTest, Heading("A. LAST CONNECTION TEST (DRAFT TARGET)"))
        For Each key In {"Target Host", "Target Port", "Destination Address", "Source Address", "Source Interface", "Source Evidence", "Connection Type", "IP Reachability", "TCP Transport", "Protocol Verification", "Failure Stage", "Register Mapping", "Fire Status", "Last Test Timestamp"}
            AddDiagnostic(lastTest, _testValues, key)
        Next
        lastTest.ResumeLayout(True)
        Dim monitoring = Fields()
        monitoring.SuspendLayout()
        AddNote(monitoring, Heading("B. ACTIVE MONITORING (SAVED TARGET)"))
        For Each key In {"Target Host", "Target Port", "Connection Type", "Monitoring State", "Protocol Health", "TCP Transport", "Last Valid Response", "Data Freshness", "Fire Status", "Consecutive Failures", "Last Communication Error", "Commissioning Requirement"}
            AddDiagnostic(monitoring, _monitorValues, key)
        Next
        monitoring.ResumeLayout(True)
        diagnosticColumns.Controls.Add(lastTest, 0, 0)
        diagnosticColumns.Controls.Add(monitoring, 1, 0)
        _diagnostics.AutoSize = True
        _diagnostics.Padding = New Padding(8)
        _diagnostics.BackColor = UiTheme.Surface
        If _fixedMode.HasValue Then
            _detachedDiagnostics = diagnosticColumns
        Else
            AddSection(root, "COMMUNICATION DIAGNOSTICS", diagnosticColumns, _diagnostics)
        End If
        _buttons.Dock = DockStyle.Top
        _buttons.AutoSize = True
        _buttons.WrapContents = True
        AddButton("Test Connection", "test", Async Function()
            Await _service.TestAsync(ReadDraft(validateRegisters:=False), _session)
            Return _service.ReadStatus().LastTest.PrimaryError
        End Function)
        AddButton("Test Authentication", "auth-test", Async Function()
            Return Await _service.TestAsync(ReadDraft(), _session)
        End Function)
        AddButton("Test Data Retrieval", "data-test", Async Function()
            Return Await _service.TestAsync(ReadDraft(), _session)
        End Function)
        AddButton("Save Settings", "save", Async Function()
            Await _service.SaveAsync(ReadDraft(), _username.Text, _password.Text, _clearCredentials.Checked, _session)
            _username.Clear()
            _password.Clear()
            _clearCredentials.Checked = False
            _draft = _service.ReadSettings()
            RefreshMappingStatus()
            _pendingUsername.Remove(_lastType)
            _pendingPassword.Remove(_lastType)
            _pendingClear.Remove(_lastType)
            Return "Settings saved locally and applied. Panel configuration and history are unchanged."
        End Function)
        AddButton("Connect", "connect", Async Function()
            EnsureSaved()
            Await _service.ConnectAsync(_session)
            Return "Connection requested using saved settings. ONLINE requires valid fresh FC04 data."
        End Function)
        AddButton("Disconnect", "disconnect", Async Function()
            Await _service.DisconnectAsync(_session)
            Return "Monitoring disconnected; fire panel status is UNKNOWN."
        End Function)
        AddButton("Reconnect", "reconnect", Async Function()
            EnsureSaved()
            Await _service.ConnectAsync(_session)
            Return "Reconnection requested using saved settings."
        End Function)
        AddButton("Reset to Defaults", "defaults", Async Function()
            If MessageBox.Show(Me, "Restore connection settings to defaults, disconnect monitoring and remove saved cloud credentials?" &
                vbCrLf & "This does not reset the fire panel or delete any fire alarm history.", "Reset connection settings only",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) <> DialogResult.Yes Then Return "Reset cancelled."
            Await _service.DisconnectAsync(_session)
            Await _service.SaveAsync(New FireConnectionSettings, Nothing, Nothing, True, _session, clearAllCredentials:=True)
            _pendingUsername.Clear()
            _pendingPassword.Clear()
            _pendingClear.Clear()
            LoadDraft()
            Return "Connection defaults saved. Panel configuration and all history are unchanged."
        End Function)
        If _fixedMode.HasValue Then
            AddButton("Clear Diagnostic Logs", "clear-diagnostics", Async Function()
                                                                     Await _service.ClearDiagnosticLogsAsync(_session)
                                                                     Return "Diagnostics cleared. Historical fire events retained."
                                                                 End Function)
        End If
        Dim copyButton = UiTheme.ActionButton("Copy Diagnostics", False)
        copyButton.Name = "copy"
        copyButton.Size = New Size(160, 36)
        copyButton.Margin = New Padding(0, 3, 8, 4)
        AddHandler copyButton.Click, Sub()
            Try
                RenderStatus()
                Clipboard.SetText(If(_fixedMode.HasValue, _service.DiagnosticExport(), DiagnosticText()))
                _message.Text = "Diagnostic results copied (credentials excluded)."
            Catch ex As System.Runtime.InteropServices.ExternalException
                _message.Text = "Copy failed: clipboard is unavailable. " & ex.Message
                _message.ForeColor = Color.Firebrick
            End Try
        End Sub
        _buttons.Controls.Add(copyButton)
        AddButton("Export Diagnostics", "export", Async Function()
            Using dialog As New SaveFileDialog With {
                .Filter = "Fire communication diagnostics (JSON)|*.json", .DefaultExt = "json", .AddExtension = True,
                .FileName = "EOEMS_Fire_Diagnostics_" & DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) & ".json",
                .OverwritePrompt = True}
                If dialog.ShowDialog(Me) <> DialogResult.OK Then Return "Diagnostic export cancelled."
                Await _service.ExportDiagnosticsAsync(dialog.FileName)
                Return "Diagnostics exported (credentials excluded): " & dialog.FileName
            End Using
        End Function)
        _message.AutoSize = True
        _message.ForeColor = UiTheme.MutedInk
        _message.Text = "Test checks the draft target; Connect uses saved, commissioned settings. Fire status remains UNKNOWN until manufacturer definitions can be decoded."
        _scroll.Controls.Add(root)
        If _fixedMode.HasValue Then FireConnectionLayout.FillViewport(_scroll, root)
        Dim footer As New TableLayoutPanel With {.Dock = DockStyle.Bottom, .AutoSize = True, .ColumnCount = 1, .RowCount = 2,
            .BackColor = UiTheme.Surface, .Padding = New Padding(8, 4, 8, 2)}
        footer.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        footer.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        footer.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        footer.Controls.Add(_buttons, 0, 0)
        footer.Controls.Add(_message, 0, 1)
        Controls.Add(_scroll)
        If _fixedMode.HasValue Then
            Controls.Add(FireConnectionLayout.Footer(_buttons, _message))
            footer.Dispose()
        Else
            Controls.Add(footer)
        End If
        AddHandler SizeChanged, AddressOf ScheduleConstrainLabels
        AddHandler HandleCreated, AddressOf ScheduleConstrainLabels
        AddHandler _scroll.SizeChanged, AddressOf ScheduleConstrainLabels
        AddHandler _scroll.Layout, Sub()
                                      If _fixedMode.HasValue AndAlso Visible Then ConstrainLabels()
                                  End Sub
        AddHandler _type.SelectedIndexChanged, Sub() ChangeType()
        AddHandler _bmsType.SelectedIndexChanged, Sub() ChangeType()
        AddHandler _timer.Tick, Sub()
                                     If Not _layoutTimer.Enabled Then RenderStatus()
                                 End Sub
        AddHandler _layoutTimer.Tick, AddressOf OnLayoutSettled
        AddHandler _endpoint.TextChanged, Sub() InvalidateMapping()
        AddHandler _port.ValueChanged, Sub() InvalidateMapping()
        AddHandler _unit.ValueChanged, Sub() InvalidateMapping()
        AddHandler _address.ValueChanged, Sub() InvalidateMapping()
        AddHandler _count.ValueChanged, Sub() InvalidateMapping()
        AddHandler _reference.TextChanged, Sub() InvalidateMapping()
        _scroll.ResumeLayout(True)
        root.ResumeLayout(True)
        ResumeLayout(True)
        LoadDraft()
        RenderStatus()
    End Sub

    Public Sub SetPageActive(active As Boolean)
        _timer.Enabled = active
        If active Then
            ScheduleConstrainLabels(Nothing, EventArgs.Empty)
            RenderStatus()
        Else
            _layoutTimer.Stop()
        End If
    End Sub

    Private Shared Function Number(minimum As Integer, maximum As Integer) As NumericUpDown
        Return New NumericUpDown With {.Minimum = minimum, .Maximum = maximum, .DecimalPlaces = 0}
    End Function

    Private Shared Function Fields() As TableLayoutPanel
        Dim table As New TableLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink, .ColumnCount = 2, .RowCount = 0, .Padding = New Padding(5)}
        table.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 170))
        table.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        Return table
    End Function

    Private Shared Sub ConfigureColumns(table As TableLayoutPanel)
        table.Dock = DockStyle.Top
        table.AutoSize = True
        table.AutoSizeMode = AutoSizeMode.GrowAndShrink
        table.ColumnCount = 2
        table.RowCount = 1
        table.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50))
        table.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50))
        table.RowStyles.Add(New RowStyle(SizeType.AutoSize))
    End Sub

    Private Shared Sub AddRow(table As TableLayoutPanel, control As Control)
        Dim row = table.RowCount
        table.RowCount += 1
        table.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        control.Dock = DockStyle.Fill
        table.Controls.Add(control, 0, row)
    End Sub

    Private Shared Sub AddNote(table As TableLayoutPanel, label As Label)
        label.AutoSize = True
        label.Dock = DockStyle.Fill
        Dim row = table.RowCount
        table.RowCount += 1
        table.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        table.Controls.Add(label, 0, row)
        table.SetColumnSpan(label, table.ColumnCount)
    End Sub

    Private Sub AddDiagnostic(table As TableLayoutPanel, values As Dictionary(Of String, Label), key As String)
        Dim value As New Label With {.AutoSize = True, .Text = "NOT TESTED", .Padding = New Padding(0, 6, 0, 0)}
        AddField(table, key, value)
        _editable.Remove(value)
        values.Add(key, value)
    End Sub

    Private _constraining As Boolean

    ' Wrap widths are derived once per table; changing MaximumSize re-enters SizeChanged, so the pass is guarded and batched.
    Private Sub ConstrainLabels()
        If _constraining OrElse Not IsHandleCreated Then Return
        Dim owner = FindForm()
        If owner IsNot Nothing AndAlso owner.WindowState = FormWindowState.Minimized Then Return
        _constraining = True
        SuspendLayout()
        Try
            For Each group In Descendants(Me).OfType(Of Label)().
                Where(Function(item) TypeOf item.Parent Is TableLayoutPanel).GroupBy(Function(item) DirectCast(item.Parent, TableLayoutPanel))
                Dim table = group.Key
                Dim widths = table.GetColumnWidths()
                table.SuspendLayout()
                For Each wrappedLabel In group
                    Dim position = table.GetPositionFromControl(wrappedLabel)
                    If position.Column < 0 OrElse position.Column >= widths.Length Then Continue For
                    Dim width = widths.Skip(position.Column).Take(table.GetColumnSpan(wrappedLabel)).Sum() -
                        wrappedLabel.Margin.Horizontal - table.Padding.Horizontal
                    If width < MinimumLabelWrapWidth Then Continue For
                    Dim maximum As New Size(width, 0)
                    If wrappedLabel.MaximumSize <> maximum Then wrappedLabel.MaximumSize = maximum
                Next
                If _fixedMode.HasValue AndAlso table.ColumnCount = 2 Then
                    FireConnectionLayout.FitFields(table)
                End If
                table.ResumeLayout(True)
            Next
        Finally
            ResumeLayout(True)
            _constraining = False
        End Try
    End Sub

    Private Sub ScheduleConstrainLabels(sender As Object, e As EventArgs)
        If IsDisposed OrElse _constraining OrElse Not _timer.Enabled Then Return
        Dim owner = FindForm()
        If owner IsNot Nothing AndAlso owner.WindowState = FormWindowState.Minimized Then Return
        _layoutTimer.Stop()
        _layoutTimer.Start()
    End Sub

    Private Sub OnLayoutSettled(sender As Object, e As EventArgs)
        _layoutTimer.Stop()
        If _timer.Enabled Then ConstrainLabels()
    End Sub

    Private Shared Iterator Function Descendants(parent As Control) As IEnumerable(Of Control)
        For Each child As Control In parent.Controls
            Yield child
            For Each descendant In Descendants(child)
                Yield descendant
            Next
        Next
    End Function

    Private Sub AddField(table As TableLayoutPanel, caption As String, input As Control)
        If _fixedMode.HasValue Then
            table.Padding = Padding.Empty
            table.ColumnStyles(0).SizeType = SizeType.Percent
            table.ColumnStyles(0).Width = 38
            table.ColumnStyles(1).Width = 62
        End If
        Dim row = table.RowCount
        table.RowCount += 1
        table.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        Dim label As New Label With {.Text = caption, .Dock = DockStyle.Fill, .AutoSize = True, .Padding = New Padding(0, 4, 0, 0), .ForeColor = UiTheme.Ink}
        input.Dock = DockStyle.Fill
        If TypeOf input Is CheckBox Then
            input.AutoSize = False
            input.Height = If(_fixedMode.HasValue, 24, 28)
        End If
        input.Visible = True
        input.Margin = New Padding(0, 2, 5, 2)
        If TypeOf input Is TextBox OrElse TypeOf input Is ComboBox OrElse TypeOf input Is NumericUpDown Then
            input.MaximumSize = If(_fixedMode.HasValue, Size.Empty, New Size(340, 0))
        End If
        If TypeOf input Is TextBox OrElse TypeOf input Is ComboBox OrElse TypeOf input Is NumericUpDown Then
            input.Font = New Font("Segoe UI", If(_fixedMode.HasValue, 9.0F, 9.5F))
        End If
        table.Controls.Add(label, 0, row)
        table.Controls.Add(input, 1, row)
        If _fixedMode.HasValue AndAlso TypeOf input Is Label Then
            RemoveHandler input.TextChanged, AddressOf ScheduleConstrainLabels
            AddHandler input.TextChanged, AddressOf ScheduleConstrainLabels
        End If
        If Not _editable.Contains(input) Then _editable.Add(input)
    End Sub

    Private Function Heading(text As String) As Label
        Return New Label With {.Dock = DockStyle.Fill, .AutoSize = True, .Padding = If(_fixedMode.HasValue, Padding.Empty, New Padding(0, 4, 0, 3)), .Text = text, .TextAlign = ContentAlignment.MiddleLeft,
            .Margin = If(_fixedMode.HasValue, New Padding(0, 3, 0, 3), New Padding(3)),
            .Font = New Font("Segoe UI", 9.5F, FontStyle.Bold), .ForeColor = Color.FromArgb(24, 52, 94)}
    End Function

    ' Each group of settings is a white rounded card on the canvas, with a blue title and a thin rule.
    Private Sub AddSection(root As TableLayoutPanel, title As String, ParamArray content As Control())
        AddSection(root, title, False, content)
    End Sub

    Private Sub AddSection(root As TableLayoutPanel, title As String, openByDefault As Boolean, ParamArray content As Control())
        Dim card As New RoundedPanel With {
            .Dock = DockStyle.Fill,
            .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .BackColor = UiTheme.Surface,
            .Padding = New Padding(14, 6, 14, 6),
            .Margin = New Padding(0, 0, 0, 6)}
        Dim stack As New TableLayoutPanel With {
            .Dock = DockStyle.Top,
            .AutoSize = True,
            .ColumnCount = 1,
            .BackColor = Color.Transparent}
        stack.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        Dim caption As New Label With {
            .AutoSize = True,
            .Dock = DockStyle.Fill,
            .Padding = New Padding(0, 4, 0, 6),
            .Text = ChrW(&H25BE) & "  " & title,
            .TextAlign = ContentAlignment.MiddleLeft,
            .Cursor = Cursors.Hand,
            .Font = New Font("Segoe UI", 9.5F, FontStyle.Bold),
            .ForeColor = UiTheme.Blue}
        AddRow(stack, caption)
        Dim rule As New Panel With {.Height = 1, .BackColor = UiTheme.Border, .Margin = New Padding(0, 0, 0, 6)}
        AddRow(stack, rule)
        For Each item In content
            AddRow(stack, item)
        Next
        Dim collapsed = Not (openByDefault OrElse CardCollapse.StartExpanded)
        Dim apply As Action(Of Boolean) = Sub(expand)
            collapsed = Not expand
            stack.SuspendLayout()
            For index = 2 To stack.Controls.Count - 1
                stack.Controls(index).Visible = expand
            Next
            caption.Text = ChrW(If(expand, &H25BE, &H25B8)) & "  " & title
            stack.ResumeLayout(True)
            If expand Then ScheduleConstrainLabels(Nothing, EventArgs.Empty)
        End Sub
        AddHandler caption.Click, Sub() apply(collapsed)
        CardCollapse.Register(card, apply)
        card.Controls.Add(stack)
        AddRow(root, card)
        apply(Not collapsed)
    End Sub

    Private Sub AddButton(text As String, name As String, action As Func(Of Task(Of String)))
        Dim button = UiTheme.ActionButton(text, name = "save")
        button.Name = name
        button.Size = New Size(Math.Max(112, TextRenderer.MeasureText(text, button.Font).Width + 32), 36)
        button.Margin = New Padding(0, 3, 8, 4)
        _buttons.Controls.Add(button)
        AddHandler button.Click, Async Sub() Await RunActionAsync(action)
    End Sub

    Private Async Function RunActionAsync(action As Func(Of Task(Of String))) As Task
        If _busy Then Return
        _busy = True
        RaiseEvent BusyChanged(Me, EventArgs.Empty)
        ApplyPermissions()
        _message.Text = "Working..."
        Try
            _message.Text = Await action()
            _message.ForeColor = UiTheme.Ink
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is InvalidOperationException OrElse
            TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is System.Security.Cryptography.CryptographicException OrElse
            TypeOf ex Is System.Text.Json.JsonException OrElse TypeOf ex Is FormatException OrElse TypeOf ex Is OperationCanceledException
            _message.Text = "Action failed: " & If(TypeOf ex Is System.Security.Cryptography.CryptographicException, "Protected credentials cannot be accessed by this Windows account.", ex.Message)
            _message.ForeColor = Color.Firebrick
        Finally
            _busy = False
            RaiseEvent BusyChanged(Me, EventArgs.Empty)
            If Not IsDisposed Then
                ApplyPermissions()
                RenderStatus()
            End If
        End Try
    End Function

    Private Function SelectedType() As FireConnectionType
        If _type.SelectedIndex = 1 Then Return FireConnectionType.ZetaCloud
        If _type.SelectedIndex = 2 Then Return If(_bmsType.SelectedIndex = 1, FireConnectionType.BmsVendor, FireConnectionType.BmsModbus)
        Return FireConnectionType.LocalModbus
    End Function

    Private Sub CaptureEndpoint()
        If _lastType = FireConnectionType.LocalModbus Then
            _draft.LocalHost = _endpoint.Text.Trim()
            _draft.LocalPort = CInt(_port.Value)
        ElseIf _lastType = FireConnectionType.BmsModbus Then
            _draft.BmsModbusHost = _endpoint.Text.Trim()
            _draft.BmsModbusPort = CInt(_port.Value)
        ElseIf _lastType = FireConnectionType.BmsVendor Then
            _draft.BmsHost = _endpoint.Text.Trim()
            _draft.BmsPort = CInt(_port.Value)
        End If
    End Sub

    Private Sub ChangeType()
        If _loading Then Return
        CaptureEndpoint()
        CaptureCommon(_draft)
        _draft.CaptureProfile(_lastType)
        CaptureAuthentication()
        _lastType = SelectedType()
        _draft.SelectProfile(_lastType)
        _loading = True
        _endpoint.Text = _draft.Host()
        _port.Value = _draft.Port()
        LoadCommon()
        LoadAuthentication()
        _loading = False
        BuildDynamic()
    End Sub

    Private Sub InvalidateMapping()
        If Not _loading Then
            _verified.Checked = False
            _draft.MappingProof = String.Empty
            _mappingStatus.Text = "NOT VERIFIED - review documentation, Test Connection, then Save."
        End If
    End Sub

    Private Sub BuildDynamic()
        Dim tables As Control() = {_scroll, _content, _columns, _advanced, _common, _advancedCommon, _selector, _dynamic, _dynamicAdvanced}
        For Each table In tables
            table.SuspendLayout()
        Next
        Try
            BuildDynamicCore()
        Finally
            For Each table In tables.Reverse()
                table.ResumeLayout(True)
            Next
            _content.PerformLayout()
            ConstrainLabels()
        End Try
    End Sub

    Private Sub BuildDynamicCore()
        ' Reparent persistent inputs before disposing only the temporary layout.
        For Each control In {_endpoint, DirectCast(_port, Control), _unit, _address, _count, _verified, _reference,
            _cloud, _api, _username, _password, _clearCredentials, _credentials, _authentication, _role,
            _connectionDirection, _bmsReference, _siteIdentifier, _mappingStatus, _bmsType,
            _timeout, _retries, _retryInterval, _poll, _freshness, _autoConnect, _autoReconnect}
            If control.Parent IsNot Nothing Then control.Parent.Controls.Remove(control)
            control.Visible = False
        Next
        For Each table In {_common, _advancedCommon, _selector}
            For Each child In table.Controls.Cast(Of Control).ToArray()
                If child Is _type Then Continue For
                child.Dispose()
            Next
            If table Is _selector AndAlso _type.Parent IsNot Nothing Then _type.Parent.Controls.Remove(_type)
            table.Controls.Clear()
            table.RowStyles.Clear()
            table.RowCount = 0
        Next
        For Each panel In {_dynamic, _dynamicAdvanced}
            For Each child In panel.Controls.Cast(Of Control).ToArray()
                child.Dispose()
            Next
            panel.Controls.Clear()
        Next
        If Not _fixedMode.HasValue Then AddField(_selector, "Connection Method", _type)
        If _type.SelectedIndex = 2 Then AddField(_selector, "BMS protocol type", _bmsType)
        If _fixedMode = FireConnectionType.BmsVendor Then
            For Each caption As Label In _selector.Controls.OfType(Of Label)()
                caption.ForeColor = UiTheme.Blue
                caption.Font = New Font("Segoe UI", 9, FontStyle.Bold)
            Next
        End If
        Dim fields = LegacyFireConnectionSettingsView.Fields()
        Dim advancedFields = LegacyFireConnectionSettingsView.Fields()
        fields.SuspendLayout()
        advancedFields.SuspendLayout()
        Dim modbus = SelectedType() = FireConnectionType.LocalModbus OrElse SelectedType() = FireConnectionType.BmsModbus
        Dim cloudSelected = SelectedType() = FireConnectionType.ZetaCloud
        AddField(_common, "Timeout (ms)", _timeout)
        If Not cloudSelected Then AddField(_common, "Retry count", _retries)
        If modbus OrElse cloudSelected Then AddField(_common, "Polling interval (sec)", _poll)
        If cloudSelected Then
            AddField(fields, "Cloud server URL", _cloud)
            AddField(fields, "Official API endpoint", _api)
            AddField(fields, "Site identifier", _siteIdentifier)
        Else
            AddField(fields, "Panel / BMS host", _endpoint)
            AddField(fields, "TCP port (unverified)", _port)
            If SelectedType() = FireConnectionType.BmsVendor Then
                AddField(fields, "Connection role", _role)
                AddField(fields, "Connection direction", _connectionDirection)
                AddField(advancedFields, "Protocol documentation", _bmsReference)
            Else
                AddField(fields, "Modbus Unit ID", _unit)
                AddField(advancedFields, "FC04 start (zero-based)", _address)
                AddField(advancedFields, "Register count", _count)
                AddField(advancedFields, "Mapping reference", _reference)
                AddField(advancedFields, "Approved read range", _verified)
                AddField(advancedFields, "FC04 test evidence", _mappingStatus)
            End If
        End If
        If Not modbus Then
            AddField(advancedFields, "Authentication type", _authentication)
            AddField(advancedFields, "Username", _username)
            AddField(advancedFields, "Password / API token", _password)
            AddField(advancedFields, "Credentials", _credentials)
            AddField(advancedFields, "Credential removal", _clearCredentials)
            AddField(advancedFields, "Protocol verification", _mappingStatus)
        End If
        If Not cloudSelected Then AddField(_advancedCommon, "Retry interval (sec)", _retryInterval)
        If modbus Then
            AddField(_advancedCommon, "Data freshness (sec)", _freshness)
            AddField(_advancedCommon, "Startup", _autoConnect)
            AddField(_advancedCommon, "Recovery", _autoReconnect)
        End If
        AddNote(_advancedCommon, New Label With {.ForeColor = UiTheme.MutedInk, .Text = If(modbus,
            If(_fixedMode.HasValue,
            "Read-only FC04; approved mapping required. Raw response is not fire status. No panel commands.",
            "FC04 is read-only. Address/count are not a Zeta status map until supplied by the manufacturer. Review the approved range before testing. A valid response proves framing, not fire status. Retries use the configured interval; automatic recovery does not send control commands."),
            "Metadata only. Official protocol, authentication, payload and communication-direction documentation are required before a driver can be enabled. No website login or vendor packet is sent.")})
        fields.ResumeLayout(True)
        advancedFields.ResumeLayout(True)
        _dynamic.Controls.Add(fields)
        _dynamicAdvanced.Controls.Add(advancedFields)
        _modeNotice.Text = If(cloudSelected,
            "CLOUD API ACCESS NOT VERIFIED / UNSUPPORTED - Official API documentation and authorized access required. No website login is attempted.",
            If(SelectedType() = FireConnectionType.BmsVendor,
            "BMS PROTOCOL NOT VERIFIED / UNSUPPORTED - Panel CONNECTED is not SCADA access. Port 3333 is not assumed to be Modbus. No client/listener is started.",
            "READ-ONLY FC04. The port is an editable commissioning value; 502 is not verified for SCM-LAN. Test checks IP/TCP without a register map; FC04 requires an approved mapping. Fire status remains UNKNOWN."))
        For Each actionButton As Button In _buttons.Controls.OfType(Of Button)()
            If actionButton.Name = "auth-test" OrElse actionButton.Name = "data-test" Then actionButton.Visible = cloudSelected
        Next
        RefreshMappingStatus()
        ApplyPermissions()
    End Sub

    Private Sub RefreshMappingStatus()
        _mappingStatus.Text = If(SelectedType() = FireConnectionType.ZetaCloud, "API NOT CONFIGURED / UNSUPPORTED - official API/access required.",
            If(SelectedType() = FireConnectionType.BmsVendor, If(_fixedMode.HasValue, "UNSUPPORTED - documented protocol / role required.", "UNVERIFIED / UNSUPPORTED - documented protocol and role required."),
            If(_draft.MappingVerified AndAlso FireConnectionStore.HasMappingProof(_draft),
            "FC04 RESPONSE VERIFIED; REGISTER MEANING / FIRE STATUS NOT DECODED.",
            "NOT VERIFIED - review documentation, Test Connection, then Save.")))
    End Sub

    Private Sub LoadDraft()
        _loading = True
        _draft = _service.ReadSettings()
        If _fixedMode.HasValue Then
            Dim mode = _fixedMode.Value
            If mode = FireConnectionType.BmsVendor AndAlso _draft.ConnectionType = FireConnectionType.BmsModbus Then mode = FireConnectionType.BmsModbus
            If _draft.ConnectionType <> mode Then _draft.SelectProfile(mode)
        End If
        If _draft.ConnectionType = FireConnectionType.ZetaCloud AndAlso _draft.CloudApiEndpoint = ZetaCloudEventClient.EventRoute Then _draft.SelectProfile(FireConnectionType.LocalModbus)
        _lastType = _draft.ConnectionType
        _type.SelectedIndex = If(_draft.ConnectionType = FireConnectionType.LocalModbus, 0, If(_draft.ConnectionType = FireConnectionType.ZetaCloud, 1, 2))
        _bmsType.SelectedIndex = If(_draft.ConnectionType = FireConnectionType.BmsModbus, 0, 1)
        _endpoint.Text = _draft.Host()
        _port.Value = _draft.Port()
        LoadCommon()
        _cloud.Text = _draft.CloudUrl
        _api.Text = _draft.CloudApiEndpoint
        _siteIdentifier.Text = _draft.CloudSiteIdentifier
        _panelNetworkMode.SelectedItem = _draft.PanelNetworkMode
        _panelNetworkAddress.Text = _draft.PanelNetworkAddress
        _panelNetworkName.Text = _draft.PanelNetworkName
        LoadAuthentication()
        _loading = False
        BuildDynamic()
    End Sub

    Private Sub LoadCommon()
        _unit.Value = _draft.UnitId
        _timeout.Value = _draft.TimeoutMilliseconds
        _retries.Value = _draft.RetryCount
        _retryInterval.Value = _draft.RetryIntervalSeconds
        If Not _poll.Items.Contains(_draft.PollSeconds) Then _poll.Items.Add(_draft.PollSeconds)
        _poll.SelectedItem = _draft.PollSeconds
        _freshness.Value = _draft.FreshnessSeconds
        _address.Value = _draft.StartAddress
        _count.Value = _draft.RegisterCount
        _reference.Text = _draft.MappingReference
        _verified.Checked = _draft.MappingVerified
        _autoConnect.Checked = _draft.AutoConnect
        _autoReconnect.Checked = _draft.AutoReconnect
    End Sub

    Private Sub CaptureAuthentication()
        _draft.CloudUrl = _cloud.Text.Trim()
        _draft.CloudApiEndpoint = _api.Text.Trim()
        _draft.CloudSiteIdentifier = _siteIdentifier.Text.Trim()
        _draft.BmsProtocolReference = _bmsReference.Text.Trim()
        If _lastType = FireConnectionType.ZetaCloud Then _draft.CloudAuthentication = CStr(_authentication.SelectedItem)
        If _lastType = FireConnectionType.BmsVendor Then
            _draft.BmsAuthentication = CStr(_authentication.SelectedItem)
            _draft.BmsRole = CStr(_role.SelectedItem)
            _draft.BmsConnectionDirection = CStr(_connectionDirection.SelectedItem)
        End If
        _pendingUsername(_lastType) = _username.Text
        _pendingPassword(_lastType) = _password.Text
        _pendingClear(_lastType) = _clearCredentials.Checked
    End Sub

    Private Sub LoadAuthentication()
        _authentication.SelectedItem = If(_lastType = FireConnectionType.BmsVendor, _draft.BmsAuthentication, _draft.CloudAuthentication)
        _role.SelectedItem = _draft.BmsRole
        _connectionDirection.SelectedItem = _draft.BmsConnectionDirection
        _bmsReference.Text = _draft.BmsProtocolReference
        _username.Text = If(_pendingUsername.ContainsKey(_lastType), _pendingUsername(_lastType), String.Empty)
        _password.Text = If(_pendingPassword.ContainsKey(_lastType), _pendingPassword(_lastType), String.Empty)
        _clearCredentials.Checked = _pendingClear.ContainsKey(_lastType) AndAlso _pendingClear(_lastType)
    End Sub

    Private Function ReadDraft(Optional validateRegisters As Boolean = True) As FireConnectionSettings
        CaptureEndpoint()
        CaptureAuthentication()
        Dim result = _draft.Copy()
        result.ConnectionType = SelectedType()
        CaptureCommon(result)
        result.CaptureProfile(result.ConnectionType)
        result.Validate(validateRegisters)
        Return result
    End Function

    Private Sub CaptureCommon(result As FireConnectionSettings)
        result.UnitId = CInt(_unit.Value)
        result.TimeoutMilliseconds = CInt(_timeout.Value)
        result.RetryCount = CInt(_retries.Value)
        result.RetryIntervalSeconds = CInt(_retryInterval.Value)
        result.PollSeconds = CInt(_poll.SelectedItem)
        result.FreshnessSeconds = CInt(_freshness.Value)
        result.StartAddress = CInt(_address.Value)
        result.RegisterCount = CInt(_count.Value)
        result.MappingReference = _reference.Text.Trim()
        result.MappingVerified = _verified.Checked
        result.AutoConnect = _autoConnect.Checked
        result.AutoReconnect = _autoReconnect.Checked
        result.CloudUrl = _cloud.Text.Trim()
        result.CloudApiEndpoint = _api.Text.Trim()
        result.PanelNetworkMode = CStr(_panelNetworkMode.SelectedItem)
        result.PanelNetworkAddress = _panelNetworkAddress.Text.Trim()
        result.PanelNetworkName = _panelNetworkName.Text.Trim()
    End Sub

    Private Sub EnsureSaved()
        Dim saved = _service.ReadSettings()
        saved.CaptureProfile(saved.ConnectionType)
        If System.Text.Json.JsonSerializer.Serialize(ReadDraft()) <> System.Text.Json.JsonSerializer.Serialize(saved) OrElse
            _username.Text.Length > 0 OrElse _password.Text.Length > 0 OrElse _clearCredentials.Checked Then
            Throw New InvalidOperationException("Save settings before connecting. Test Connection can test an unsaved draft without changing monitoring.")
        End If
    End Sub

    Private Sub ApplyPermissions()
        Dim canEdit = Not _busy AndAlso PlcConfigurationAccess.CanEdit(_session.RoleName, _session.IsOfflinePreview)
        For Each field As Control In _editable
            field.Enabled = canEdit
        Next
        For Each actionButton As Button In _buttons.Controls.OfType(Of Button)()
            actionButton.Enabled = canEdit OrElse actionButton.Name = "copy" OrElse (Not _busy AndAlso actionButton.Name = "export")
        Next
    End Sub

    Private Sub RenderStatus()
        If _statusRendered Then
            RenderStatusCore()
            Return
        End If

        Dim layouts As New HashSet(Of Control)()
        For Each diagnosticLabel As Control In _monitorValues.Values.Concat(_testValues.Values)
            If diagnosticLabel.Parent IsNot Nothing Then layouts.Add(diagnosticLabel.Parent)
        Next
        For Each control As Control In {_state, _summary, _diagnostics, _credentials}
            If control.Parent IsNot Nothing Then layouts.Add(control.Parent)
        Next

        For Each parentControl In layouts
            parentControl.SuspendLayout()
        Next
        Try
            RenderStatusCore()
            _statusRendered = True
        Finally
            For Each parentControl In layouts.Reverse()
                parentControl.ResumeLayout(True)
            Next
        End Try
    End Sub

    Private Sub RenderStatusCore()
        Dim status = _service.ReadStatus()
        Dim dataKind = If(status.ConnectionType = FireConnectionType.ZetaCloud, "HISTORICAL CLOUD EVENTS", "READ-ONLY RAW DATA")
        Dim stateText = If(status.Monitoring, status.State & " (" & dataKind & ")", "MONITORING DISCONNECTED (" & status.State & ")") & " | FIRE STATUS UNKNOWN"
        If _state.Text <> stateText Then _state.Text = stateText
        Dim stateColor As Color
        Select Case status.State
            Case "ONLINE" : stateColor = Color.SeaGreen
            Case "CONNECTING" : stateColor = Color.RoyalBlue
            Case "OFFLINE" : stateColor = Color.Firebrick
            Case "DEGRADED" : stateColor = Color.DarkGoldenrod
            Case Else : stateColor = Color.SlateGray
        End Select
        If _state.ForeColor <> stateColor Then _state.ForeColor = stateColor
        Dim interfaceName = If(status.ConnectionType = FireConnectionType.LocalModbus, "Modbus TCP - Local",
            If(status.ConnectionType = FireConnectionType.ZetaCloud, "Zeta Remote Cloud",
            If(status.ConnectionType = FireConnectionType.BmsModbus, "BMS - Modbus TCP", "BMS - Zeta vendor protocol (unavailable)")))
        Dim summaryText = "Zeta SmartConnect Multi-loop (SCM-LAN, build 001) | Saved interface: " & interfaceName & " | " & status.Endpoint &
            vbCrLf & "A draft test does not start monitoring, change saved settings, or operate the panel."
        If _summary.Text <> summaryText Then _summary.Text = summaryText
        Dim settings = _service.ReadSettings()
        Dim freshness = If(Not status.Monitoring, "UNAVAILABLE (disconnected)",
            If(status.State = "ONLINE" AndAlso status.LastDataUtc.HasValue AndAlso
                (DateTime.UtcNow - status.LastDataUtc.GetValueOrDefault()).TotalSeconds <= settings.FreshnessSeconds, "FRESH (raw registers)", "STALE / UNAVAILABLE"))
        SetDiagnostic(_monitorValues, "Target Host", If(settings.ConnectionType = FireConnectionType.ZetaCloud, settings.CloudUrl, settings.Host()))
        SetDiagnostic(_monitorValues, "Target Port", If(settings.ConnectionType = FireConnectionType.ZetaCloud, New Uri(settings.CloudUrl).Port, settings.Port()).ToString(CultureInfo.InvariantCulture))
        SetDiagnostic(_monitorValues, "Connection Type", interfaceName)
        SetDiagnostic(_monitorValues, "Monitoring State", If(status.Monitoring, status.State, "DISCONNECTED"))
        SetDiagnostic(_monitorValues, "Protocol Health", If(status.Monitoring, status.ProtocolHealth, "NOT ACTIVE"))
        SetDiagnostic(_monitorValues, "TCP Transport", If(status.Monitoring, status.TransportHealth, "DISCONNECTED"))
        SetDiagnostic(_monitorValues, "Last Valid Response", Timestamp(status.LastDataUtc) &
            If(status.ResponseMilliseconds.HasValue, " (" & status.ResponseMilliseconds.Value.ToString("0", CultureInfo.InvariantCulture) & " ms, raw registers only)", String.Empty))
        SetDiagnostic(_monitorValues, "Data Freshness", freshness)
        SetDiagnostic(_monitorValues, "Fire Status", "UNKNOWN - interpretation not implemented")
        SetDiagnostic(_monitorValues, "Consecutive Failures", status.ConsecutiveFailures.ToString(CultureInfo.InvariantCulture))
        SetDiagnostic(_monitorValues, "Last Communication Error", If(status.LastError.Length > 0, status.LastError, "None"))
        SetDiagnostic(_monitorValues, "Commissioning Requirement", If(settings.UnavailableReason().Length > 0, settings.UnavailableReason(), "FC04 raw polling commissioned; fire-status decoding unavailable"))
        Dim test = status.LastTest
        SetDiagnostic(_testValues, "Target Host", If(test Is Nothing, "-", test.Host))
        SetDiagnostic(_testValues, "Target Port", If(test Is Nothing, "-", test.Port.ToString(CultureInfo.InvariantCulture)))
        SetDiagnostic(_testValues, "Destination Address", If(test Is Nothing, "NOT RECORDED", test.DestinationAddress))
        SetDiagnostic(_testValues, "Source Address", If(test Is Nothing, "NOT RECORDED", test.SourceAddress))
        SetDiagnostic(_testValues, "Source Interface", If(test Is Nothing, "NOT RECORDED", test.SourceInterface))
        SetDiagnostic(_testValues, "Source Evidence", If(test Is Nothing, "NOT RECORDED", test.SourceEvidence))
        SetDiagnostic(_testValues, "Connection Type", If(test Is Nothing, "-", test.ConnectionType.ToString()))
        SetDiagnostic(_testValues, "IP Reachability", If(test Is Nothing, "NOT TESTED", test.Reachability.ToString()))
        SetDiagnostic(_testValues, "TCP Transport", If(test Is Nothing, "NOT TESTED", test.Transport.ToString()))
        SetDiagnostic(_testValues, "Protocol Verification", If(test Is Nothing, "NOT TESTED", test.Protocol.ToString()))
        SetDiagnostic(_testValues, "Failure Stage", If(test Is Nothing, "NOT RECORDED", test.FailureStage))
        SetDiagnostic(_testValues, "Register Mapping", If(test Is Nothing, "NOT VERIFIED", test.RegisterMapping))
        SetDiagnostic(_testValues, "Fire Status", "UNKNOWN")
        SetDiagnostic(_testValues, "Last Test Timestamp", If(test Is Nothing, "-", If(test.IsReference, "Supplied prior test - timestamp not provided", Timestamp(test.TimestampUtc))))
        Dim diagnosticsText = "LAST TEST PRIMARY RESULT: " & If(test Is Nothing, "No test performed.", test.PrimaryError) &
            If(status.EventLogError.Length > 0, vbCrLf & status.EventLogError, String.Empty)
        If _diagnostics.Text <> diagnosticsText Then _diagnostics.Text = diagnosticsText
        Dim diagnosticsColor = If(test IsNot Nothing AndAlso test.Transport.State <> "CONNECTED", Color.Firebrick, UiTheme.Ink)
        If _diagnostics.ForeColor <> diagnosticsColor Then _diagnostics.ForeColor = diagnosticsColor
        Dim modeHasCredentials = If(SelectedType() = FireConnectionType.BmsVendor, status.HasBmsCredentials, status.HasCloudCredentials)
        Dim credentialsText = If(modeHasCredentials, "Protected credentials saved for this mode; blank retains them.", "No saved credentials for this mode.")
        If _credentials.Text <> credentialsText Then _credentials.Text = credentialsText
    End Sub

    Private Shared Sub SetDiagnostic(values As Dictionary(Of String, Label), key As String, text As String)
        Dim label = values(key)
        If label.Text <> text Then label.Text = text
        Dim diagnosticColor As Color = If(text.StartsWith("SUCCESS", StringComparison.Ordinal) OrElse text.StartsWith("CONNECTED", StringComparison.Ordinal) OrElse text.StartsWith("FRESH", StringComparison.Ordinal),
            Color.SeaGreen, If(text.Contains("FAILED") OrElse text.Contains("REFUSED") OrElse text.Contains("TIMEOUT") OrElse text.Contains("UNREACHABLE"), Color.Firebrick, UiTheme.MutedInk))
        If label.ForeColor <> diagnosticColor Then label.ForeColor = diagnosticColor
    End Sub

    Private Function DiagnosticText() As String
        Dim status = _service.ReadStatus()
        Return "LAST CONNECTION TEST" & vbCrLf & If(status.LastTest Is Nothing, "No test performed.", status.LastTest.Summary()) &
            vbCrLf & vbCrLf & "ACTIVE MONITORING" & vbCrLf &
            String.Join(vbCrLf, _monitorValues.Select(Function(pair) pair.Key & ": " & pair.Value.Text)) &
            vbCrLf & vbCrLf & "CONNECTION EVENTS (UTC)" & vbCrLf &
            String.Join(vbCrLf, status.Events.Select(Function(item) item.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) & "  " & item.Message))
    End Function

    Private Shared Function Timestamp(value As DateTime?) As String
        Return If(value.HasValue, value.GetValueOrDefault().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture), "-")
    End Function

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            _timer.Stop()
            _timer.Dispose()
            _layoutTimer.Stop()
            _layoutTimer.Dispose()
            _detachedDiagnostics?.Dispose()
            If _fixedMode.HasValue Then
                _diagnostics.Dispose()
                _columns.Dispose()
                _advanced.Dispose()
            End If
            For Each control In _editable.Where(Function(item) item.Parent Is Nothing)
                control.Dispose()
            Next
        End If
        MyBase.Dispose(disposing)
    End Sub
End Class
