Imports System.Collections.Generic
Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms

' SNMP settings: registry, card access, read-only discovery and diagnostics.
Public NotInheritable Class UpsSnmpSettingsView
    Inherits UserControl

    Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture
    Private ReadOnly _session As UserSession
    Private ReadOnly _canEdit As Boolean
    Private ReadOnly _tabs As New FlatTabControl()
    Private ReadOnly _registry As New DataGridView()
    Private ReadOnly _banks As New DataGridView()
    Private ReadOnly _acquisitionEnabled As New CheckBox With {.Text = "Acquisition enabled", .AutoSize = True}
    Private ReadOnly _historianEnabled As New CheckBox With {.Text = "Historian enabled", .AutoSize = True}
    Private ReadOnly _concurrency As New NumericUpDown With {.Minimum = 1, .Maximum = 16, .Width = 56}
    Private ReadOnly _historianInterval As New NumericUpDown With {.Minimum = 10, .Maximum = 3600, .Width = 70}
    Private ReadOnly _retention As New NumericUpDown With {.Minimum = 7, .Maximum = 3650, .Width = 70}
    Private ReadOnly _registryMessage As Label
    Private ReadOnly _profiles As New DataGridView()
    Private ReadOnly _cardTabs As New FlatTabControl()
    Private ReadOnly _cardWebSummary As New Label With {.AutoSize = True}
    Private ReadOnly _cardWebStatus As New Label With {.AutoSize = True}
    Private ReadOnly _cardSnmpSummary As New Label With {.AutoSize = True}
    Private ReadOnly _cardStatus As New Label With {.AutoSize = True}
    Private ReadOnly _webProtocol As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 100}
    Private _loadingWebProtocol As Boolean
    Private _webPasswordSaved As Boolean
    Private ReadOnly _profileName As New TextBox With {.Width = 180}
    Private ReadOnly _profileVersion As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 90}
    Private ReadOnly _community As New TextBox With {.Width = 220, .UseSystemPasswordChar = True}
    Private ReadOnly _confirmCommunity As New TextBox With {.Width = 220, .UseSystemPasswordChar = True}
    Private ReadOnly _credentialDevice As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 180, .DisplayMember = "DisplayName"}
    Private ReadOnly _deviceIdentity As New Label With {.AutoSize = True}
    Private ReadOnly _credentialSource As New Label With {.AutoSize = True}
    Private ReadOnly _webUrl As New TextBox With {.Width = 280}
    Private ReadOnly _webUser As New TextBox With {.Width = 180}
    Private ReadOnly _webPassword As New TextBox With {.Width = 200, .UseSystemPasswordChar = True}
    Private ReadOnly _showWebPassword As New CheckBox With {.Text = "Show entered password", .AutoSize = True}
    Private ReadOnly _webMessage As New Label With {.AutoSize = True}
    Private ReadOnly _revealTimer As New System.Windows.Forms.Timer With {.Interval = 15000}
    Private ReadOnly _profileSummary As New Label With {.AutoSize = True}
    Private _communityHeading As Label
    Private _v3Heading As Label
    Private _webCancellation As CancellationTokenSource
    Private ReadOnly _v3User As New TextBox With {.Width = 180}
    Private ReadOnly _authProtocol As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 90}
    Private ReadOnly _authSecret As New TextBox With {.Width = 220, .UseSystemPasswordChar = True}
    Private ReadOnly _privProtocol As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 90}
    Private ReadOnly _privSecret As New TextBox With {.Width = 220, .UseSystemPasswordChar = True}
    Private ReadOnly _securityMode As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 180}
    Private ReadOnly _profilePort As New NumericUpDown With {.Minimum = 1, .Maximum = 65535, .Value = 161, .Width = 90}
    Private ReadOnly _profileTimeout As New NumericUpDown With {.Minimum = 200, .Maximum = 10000, .Value = 2000, .Width = 90}
    Private ReadOnly _profileRetries As New NumericUpDown With {.Minimum = 0, .Maximum = 3, .Value = 1, .Width = 60}
    Private ReadOnly _showSecrets As New CheckBox With {.Text = "Show entered passwords", .AutoSize = True}
    Private ReadOnly _assignProfile As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 150}
    Private ReadOnly _assignmentGroup As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 200}
    Private _editingProfile As String = String.Empty
    Private _loadingProfile As Boolean
    Private _loadingProfiles As Boolean
    Private ReadOnly _credentialCaptions As New Dictionary(Of Control, Control)()
    Private ReadOnly _credentialMessage As Label
    Private ReadOnly _scanRegistered As New RadioButton With {.Text = "Registered units only (recommended)", .AutoSize = True, .Checked = True}
    Private ReadOnly _scanRange As New RadioButton With {.Text = "IP range (administrator, max 254 hosts):", .AutoSize = True}
    Private ReadOnly _rangeFrom As New TextBox With {.Width = 120, .Text = "192.168.107.1"}
    Private ReadOnly _rangeTo As New TextBox With {.Width = 120, .Text = "192.168.107.30"}
    Private ReadOnly _scanProfile As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 140}
    Private ReadOnly _scanButton As Button
    Private ReadOnly _scanResults As New DataGridView()
    Private ReadOnly _scanMessage As Label
    Private ReadOnly _diagnostics As New DataGridView()
    Private ReadOnly _diagnosticsSummary As Label
    Private ReadOnly _diagnosticsMessage As Label
    Private ReadOnly _oidGrid As New DataGridView()
    Private ReadOnly _auditGrid As New DataGridView()
    Private ReadOnly _oidHeader As Label
    Private ReadOnly _detailBox As New TextBox()
    Private ReadOnly _diagnosticButtons As New List(Of Button)()
    Private _cancelTestsButton As Button
    Private _testCancellation As CancellationTokenSource
    Private _selectedDiagnosticId As String = String.Empty
    Private _refreshingDiagnostics As Boolean
    Private _compareFirst As Boolean
    Private _shownAudit As UpsOidAudit
    Private ReadOnly _diagnosticsTimer As New System.Windows.Forms.Timer With {.Interval = 2000}
    Private _working As UpsSettings
    Private _scanCancellation As CancellationTokenSource
    Private _active As Boolean

    Public Sub New(session As UserSession)
        _session = session
        _canEdit = session IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(session.UserId) AndAlso
            Not String.IsNullOrWhiteSpace(session.Username) AndAlso PlcConfigurationAccess.CanEdit(session.RoleName, session.IsOfflinePreview)
        BackColor = UiTheme.Canvas
        _registryMessage = MessageLabel()
        _credentialMessage = MessageLabel()
        _scanMessage = MessageLabel()
        _diagnosticsSummary = MessageLabel()
        _diagnosticsMessage = MessageLabel()
        _oidHeader = UiTheme.Label(String.Empty, New Font("Segoe UI", 8.0F, FontStyle.Bold), UiTheme.MutedInk, False)
        _oidHeader.AutoEllipsis = True
        _scanButton = UiTheme.ActionButton("Scan (read-only)", True, Icons.Search)

        _tabs.Dock = DockStyle.Fill
        _tabs.Appearance = TabAppearance.FlatButtons
        _tabs.SizeMode = TabSizeMode.Fixed
        _tabs.ItemSize = New Size(0, 1)
        _tabs.TabPages.Add(BuildRegistryTab())
        _tabs.TabPages.Add(BuildCredentialsTab())
        _tabs.TabPages.Add(BuildDiscoveryTab())
        _tabs.TabPages.Add(BuildDiagnosticsTab())
        Controls.Add(_tabs)
        Controls.Add(New SegmentedTabStrip(_tabs))

        AddHandler _tabs.SelectedIndexChanged, Sub(sender, e) UpdateTimer()
        AddHandler _diagnosticsTimer.Tick, Sub(sender, e) RefreshDiagnostics()
        AddHandler _revealTimer.Tick, Sub(sender, e) MaskEnteredSecrets()
        AddHandler _tabs.SelectedIndexChanged, Sub(sender, e) MaskEnteredSecrets()
        AddHandler SnmpCredentialStore.Changed, AddressOf OnCredentialsChanged
        Try
            LoadWorkingCopy()
            LoadProfiles()
            RefreshCredentialDevices()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
            ShowMessage(_credentialMessage, ex.Message, UpsAtsView.AlarmRed)
            ShowMessage(_registryMessage, ex.Message, UpsAtsView.AlarmRed)
            _tabs.TabPages(0).Enabled = False
            _tabs.TabPages(1).Enabled = False
        End Try
        FillOidMap()
        If Not String.IsNullOrEmpty(UpsSettingsStore.LoadError) Then ShowMessage(_registryMessage, UpsSettingsStore.LoadError, UpsAtsView.AlarmRed)
    End Sub

    Public Sub SetPageActive(active As Boolean)
        _active = active
        UpdateTimer()
    End Sub

    Private Sub UpdateTimer()
        Dim run = _active AndAlso _tabs.SelectedIndex = 3
        If run Then
            RefreshDiagnostics()
            _diagnosticsTimer.Start()
        Else
            _diagnosticsTimer.Stop()
        End If
    End Sub

    Private Shared Function MessageLabel() As Label
        Dim label = UiTheme.Label(String.Empty, New Font("Segoe UI", 9.0F, FontStyle.Bold), UiTheme.MutedInk, False)
        label.AutoEllipsis = True
        Return label
    End Function

    Private Shared Sub ShowMessage(label As Label, text As String, color As Color)
        label.Text = text
        label.ForeColor = color
    End Sub

    Private Shared Function NewTab(title As String) As TabPage
        Return New TabPage(title) With {.BackColor = UiTheme.Canvas, .Padding = New Padding(6, 8, 6, 4)}
    End Function

    Private Shared Sub StyleGrid(grid As DataGridView, editable As Boolean)
        grid.Dock = DockStyle.Fill
        grid.ReadOnly = Not editable
        grid.AllowUserToAddRows = False
        grid.AllowUserToDeleteRows = False
        grid.AllowUserToResizeRows = False
        grid.RowHeadersVisible = False
        grid.SelectionMode = If(editable, DataGridViewSelectionMode.CellSelect, DataGridViewSelectionMode.FullRowSelect)
        grid.MultiSelect = False
        grid.BackgroundColor = UiTheme.Surface
        grid.BorderStyle = BorderStyle.None
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal
        grid.GridColor = UiTheme.Border
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        grid.EnableHeadersVisualStyles = False
        grid.ColumnHeadersDefaultCellStyle.BackColor = UiTheme.SurfaceMuted
        grid.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.Ink
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = UiTheme.SurfaceMuted
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = UiTheme.Ink
        grid.ColumnHeadersDefaultCellStyle.Font = New Font("Segoe UI", 8.5F, FontStyle.Bold)
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
        grid.ColumnHeadersHeight = 40
        grid.ColumnHeadersDefaultCellStyle.Padding = New Padding(6, 0, 6, 0)
        grid.DefaultCellStyle.Font = New Font("Segoe UI", 9.5F)
        grid.DefaultCellStyle.ForeColor = UiTheme.Ink
        grid.DefaultCellStyle.Padding = New Padding(6, 0, 6, 0)
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(214, 230, 250)
        grid.DefaultCellStyle.SelectionForeColor = UiTheme.Ink
        grid.AlternatingRowsDefaultCellStyle.BackColor = UiTheme.SurfaceMuted
        grid.RowTemplate.Height = 34
        AddHandler grid.DataError, Sub(sender, e)
                                      System.Diagnostics.Trace.TraceError("SNMP settings table error: " & e.Exception.Message)
                                      If e.RowIndex >= 0 AndAlso e.RowIndex < grid.RowCount Then grid.Rows(e.RowIndex).ErrorText = e.Exception.Message
                                      e.ThrowException = False
                                  End Sub
    End Sub

    Private Function Toolbar(ParamArray items As Control()) As FlowLayoutPanel
        Dim bar As New FlowLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .WrapContents = True, .BackColor = Color.Transparent, .Padding = New Padding(0, 5, 0, 5)}
        For Each item In items
            item.Margin = New Padding(0, 3, 8, 3)
            bar.Controls.Add(item)
        Next
        Return bar
    End Function

    Private Function SmallButton(text As String, primary As Boolean, handler As EventHandler, Optional requiresAdmin As Boolean = True) As Button
        Dim button = UiTheme.ActionButton(text, primary)
        button.AutoSize = False
        button.Size = New Size(Math.Max(110, TextRenderer.MeasureText(text, button.Font).Width + 28), 36)
        button.Enabled = Not requiresAdmin OrElse _canEdit
        AddHandler button.Click, handler
        Return button
    End Function

    Private Shared Function SettingsSection(title As String, body As Control) As Control
        Dim section As New TableLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink, .ColumnCount = 1, .BackColor = UiTheme.Surface,
            .Padding = New Padding(14, 12, 14, 12), .Margin = New Padding(0, 0, 0, 12)}
        section.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        Dim heading = UiTheme.Label(title, New Font("Segoe UI", 10.5F, FontStyle.Bold), UiTheme.Ink)
        heading.Margin = New Padding(0, 0, 0, 10)
        section.Controls.Add(heading)
        body.Dock = DockStyle.Top
        body.Margin = Padding.Empty
        section.Controls.Add(body)
        Return section
    End Function

    Private Shared Sub ReadableColumns(grid As DataGridView, ParamArray widths As Integer())
        For index = 0 To Math.Min(widths.Length, grid.Columns.Count) - 1
            grid.Columns(index).MinimumWidth = widths(index)
        Next
    End Sub

    ' ---------- Registry ----------
    Private Function BuildRegistryTab() As TabPage
        Dim page = NewTab("UPS Registry")
        StyleGrid(_registry, _canEdit)
        AddRegistryColumns()
        Dim registerTabs As New FlatTabControl With {.Dock = DockStyle.Fill, .Appearance = TabAppearance.FlatButtons,
            .SizeMode = TabSizeMode.Fixed, .ItemSize = New Size(0, 1)}
        Dim units As New TabPage("UPS units") With {.BackColor = UiTheme.Canvas, .Padding = New Padding(4)}
        Dim bankPanel As New TabPage("ATS banks") With {.BackColor = UiTheme.Canvas, .Padding = New Padding(4)}
        units.Controls.Add(_registry)
        registerTabs.TabPages.AddRange({units, bankPanel})
        Dim split As New Panel With {.Dock = DockStyle.Fill, .BackColor = UiTheme.Canvas}
        split.Controls.Add(registerTabs)
        split.Controls.Add(New SegmentedTabStrip(registerTabs))
        StyleGrid(_banks, _canEdit)
        _banks.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "Number", .HeaderText = "ATS #", .ReadOnly = True, .FillWeight = 40})
        _banks.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "Name", .HeaderText = "Name", .FillWeight = 80})
        _banks.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "Nameplate", .HeaderText = "ATS / bus rating kVA", .FillWeight = 70})
        _banks.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "Source", .HeaderText = "Source description (ATS position is NOT monitored)", .FillWeight = 260})
        Dim banksHeader = UiTheme.Label("ATS BANKS", New Font("Segoe UI", 8.0F, FontStyle.Bold), UiTheme.MutedInk, False)
        banksHeader.Dock = DockStyle.Top
        banksHeader.Height = 34
        banksHeader.Text = "ATS / source-bus rating (separate from the sum of UPS nameplate ratings). Physical ATS switch position is not monitored."
        bankPanel.Controls.Add(_banks)
        bankPanel.Controls.Add(banksHeader)
        page.Controls.Add(split)

        Dim options = Toolbar()
        Dim optionLabel = Function(text As String) As Label
                              Dim l = UiTheme.Label(text, New Font("Segoe UI", 9.0F), UiTheme.MutedInk)
                              l.Margin = New Padding(14, 7, 4, 0)
                              Return l
                          End Function
        _acquisitionEnabled.Margin = New Padding(0, 6, 8, 0)
        _historianEnabled.Margin = New Padding(14, 6, 8, 0)
        options.Controls.AddRange({_acquisitionEnabled, optionLabel("Parallel polls"), _concurrency, _historianEnabled,
                                   optionLabel("Legacy snapshot every (s)"), _historianInterval, optionLabel("Legacy days (no expiry)"), _retention})
        For Each control As Control In {_acquisitionEnabled, _historianEnabled, _concurrency, _historianInterval, _retention}
            control.Enabled = _canEdit
        Next
        _concurrency.Margin = New Padding(0, 4, 0, 0)
        _historianInterval.Margin = New Padding(0, 4, 0, 0)
        _retention.Margin = New Padding(0, 4, 0, 0)
        page.Controls.Add(options)

        _registryMessage.Dock = DockStyle.Bottom
        _registryMessage.Height = 48
        _registryMessage.Padding = New Padding(8, 4, 8, 4)
        _registryMessage.TextAlign = ContentAlignment.MiddleLeft
        page.Controls.Add(_registryMessage)
        Dim advanced As New CheckBox With {.Text = "Advanced columns", .AutoSize = True, .Margin = New Padding(12, 8, 0, 0)}
        AddHandler advanced.CheckedChanged, Sub(sender, e)
                                               For Each columnName As String In {"Port", "Rated", "Timeout", "Retries", "Notes", "ProfileTransport"}
                                                   _registry.Columns(columnName).Visible = advanced.Checked
                                               Next
                                           End Sub
        Dim assignment = Toolbar(_assignProfile, _assignmentGroup,
            SmallButton("Stage profile assignment", False, AddressOf OnAssignProfile))
        assignment.Visible = False
        Dim assignToggle As New CheckBox With {.Text = "Bulk profile assignment", .AutoSize = True, .Margin = New Padding(12, 8, 0, 0)}
        AddHandler assignToggle.CheckedChanged, Sub(sender, e) assignment.Visible = assignToggle.Checked
        page.Controls.Add(assignment)
        page.Controls.Add(Toolbar(
            SmallButton("Add UPS", False, AddressOf OnAddUnit),
            SmallButton("Remove selected", False, AddressOf OnRemoveUnit),
            SmallButton("Test selected", False, AddressOf OnTestSelected, False),
            SmallButton("Revert", False, Sub(sender, e) LoadWorkingCopy(), False),
            SmallButton("Save registry", True, AddressOf OnSaveRegistry), advanced, assignToggle))
        _registry.MultiSelect = True
        _registry.SelectionMode = DataGridViewSelectionMode.FullRowSelect
        ReadableColumns(_registry, 95, 75, 55, 125, 65, 130, 150, 85, 50, 80, 105, 65, 115, 220, 110)
        For Each columnName As String In {"Port", "Rated", "Timeout", "Retries", "Notes", "ProfileTransport"}
            _registry.Columns(columnName).Visible = False
        Next
        ReadableColumns(_banks, 65, 130, 130, 360)
        Return page
    End Function

    Private Sub AddRegistryColumns()
        _registry.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "Id", .HeaderText = "ID (fixed)", .FillWeight = 70})
        _registry.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "Label", .HeaderText = "Label", .FillWeight = 70})
        Dim ats As New DataGridViewComboBoxColumn With {.Name = "Ats", .HeaderText = "ATS", .FillWeight = 40, .FlatStyle = FlatStyle.Flat}
        _registry.Columns.Add(ats)
        _registry.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "Ip", .HeaderText = "IP address", .FillWeight = 90})
        _registry.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "Port", .HeaderText = "Port", .FillWeight = 40})
        _registry.Columns.Add(New DataGridViewComboBoxColumn With {.Name = "Profile", .HeaderText = "Credential", .FillWeight = 70, .FlatStyle = FlatStyle.Flat})
        _registry.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "Model", .HeaderText = "Model", .FillWeight = 120})
        _registry.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "Rated", .HeaderText = "Rated kVA", .FillWeight = 50})
        _registry.Columns.Add(New DataGridViewCheckBoxColumn With {.Name = "Enabled", .HeaderText = "Poll", .FillWeight = 32})
        _registry.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "Interval", .HeaderText = "Every (s)", .FillWeight = 45})
        _registry.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "Timeout", .HeaderText = "Timeout (ms)", .FillWeight = 55})
        _registry.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "Retries", .HeaderText = "Retries", .FillWeight = 40})
        Dim verification As New DataGridViewComboBoxColumn With {.Name = "Verification", .HeaderText = "Identity", .FillWeight = 75, .FlatStyle = FlatStyle.Flat}
        verification.Items.AddRange([Enum].GetNames(GetType(UpsVerification)))
        _registry.Columns.Add(verification)
        _registry.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "Notes", .HeaderText = "Notes", .FillWeight = 220})
        _registry.Columns.Add(New DataGridViewCheckBoxColumn With {.Name = "ProfileTransport", .HeaderText = "Profile timing / port", .FillWeight = 55})
        AddHandler _registry.CellBeginEdit, Sub(sender, e)
                                                ' The ID is the historian key: editable only on rows that were never saved.
                                                If e.ColumnIndex = 0 AndAlso Not Equals(_registry.Rows(e.RowIndex).Tag, "new") Then e.Cancel = True
                                            End Sub
        AddHandler _registry.CellValueChanged, Sub(sender, e)
                                                  If e.RowIndex >= 0 AndAlso e.ColumnIndex = _registry.Columns("Profile").Index AndAlso
                                                     _registry.Rows(e.RowIndex).Tag IsNot Nothing Then
                                                      _registry.Rows(e.RowIndex).Cells("ProfileTransport").Value = True
                                                  End If
                                              End Sub
    End Sub

    Private Sub LoadWorkingCopy()
        _working = UpsSettingsStore.Current.Clone()
        _acquisitionEnabled.Checked = _working.AcquisitionEnabled
        _historianEnabled.Checked = _working.HistorianEnabled
        _concurrency.Value = Math.Max(_concurrency.Minimum, Math.Min(_concurrency.Maximum, _working.MaxConcurrentPolls))
        _historianInterval.Value = Math.Max(_historianInterval.Minimum, Math.Min(_historianInterval.Maximum, _working.HistorianIntervalSeconds))
        _retention.Value = Math.Max(_retention.Minimum, Math.Min(_retention.Maximum, _working.HistorianRetentionDays))
        _retention.Enabled = False
        RefreshComboItems()
        _registry.Rows.Clear()
        For Each device In _working.Devices.OrderBy(Function(item) item.AtsBank).ThenBy(Function(item) item.Label, StringComparer.OrdinalIgnoreCase)
            AddRegistryRow(device, False)
        Next
        _banks.Rows.Clear()
        For Each bank In _working.Banks.OrderBy(Function(item) item.Number)
            _banks.Rows.Add(bank.Number.ToString(Inv), bank.Name, bank.NameplateKva.ToString("R", Inv), bank.SourceDescription)
        Next
        ShowMessage(_registryMessage, _working.Devices.Count & " UPS registered  •  " &
                    _working.Devices.Where(Function(item) item.Verification <> UpsVerification.Verified).Count() & " need identity confirmation", UiTheme.MutedInk)
    End Sub

    Private Sub OpenDiagnosticsAndTest(failedOnly As Boolean)
        _tabs.SelectedIndex = 3
        RunConnectionTests(If(failedOnly, FailedDevices(), UpsSettingsStore.Current.Devices.ToList()))
    End Sub

    Private Sub OnRestoreConnection(sender As Object, e As EventArgs)
        If Not _canEdit Then
            ShowMessage(_registryMessage, "Only an authenticated administrator can restore a UPS connection.", UpsAtsView.AlarmRed)
            Return
        End If
        If _testCancellation IsNot Nothing OrElse _scanCancellation IsNot Nothing Then
            ShowMessage(_registryMessage, "Wait for or cancel the current network test before starting recovery.", UpsAtsView.WarnAmber)
            Return
        End If
        Dim id = If(_tabs.SelectedIndex = 3, _selectedDiagnosticId,
                    If(_registry.CurrentRow Is Nothing, String.Empty, Convert.ToString(_registry.CurrentRow.Cells("Id").Value)))
        If id <> "UPS-C04" Then
            Dim message = If(_tabs.SelectedIndex = 3, _diagnosticsMessage, _registryMessage)
            ShowMessage(message, "Select C04 (192.168.107.16) for this existing-default recovery workflow.", UpsAtsView.WarnAmber)
            Return
        End If
        Try
            Using recovery As New UpsConnectionRecoveryForm(_session)
                recovery.ShowDialog(Me)
            End Using
            RefreshDiagnostics()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
            ShowMessage(_diagnosticsMessage, "Recovery blocked: " & ex.Message, UpsAtsView.AlarmRed)
        End Try
    End Sub

    Private Sub OnAssignProfile(sender As Object, e As EventArgs)
        If Not _canEdit Then Return
        Dim name = Convert.ToString(_assignProfile.SelectedItem)
        If String.IsNullOrEmpty(name) Then
            ShowMessage(_registryMessage, "Select a saved credential profile first.", UpsAtsView.WarnAmber)
            Return
        End If
        Dim group = Convert.ToString(_assignmentGroup.SelectedItem)
        Dim rows = _registry.Rows.Cast(Of DataGridViewRow)().Where(
            Function(row)
                If group.StartsWith("ATS ", StringComparison.Ordinal) Then Return Convert.ToString(row.Cells("Ats").Value) = group.Substring(4)
                If group.StartsWith("Model contains ", StringComparison.Ordinal) Then Return Convert.ToString(row.Cells("Model").Value).IndexOf(group.Substring(15), StringComparison.OrdinalIgnoreCase) >= 0
                Return row.Selected
            End Function).ToList()
        For Each row In rows
            row.Cells("Profile").Value = name
            row.Cells("ProfileTransport").Value = True
        Next
        ShowMessage(_registryMessage, rows.Count & " assignments staged. Save registry to persist; working units require confirmation.", UiTheme.Blue)
    End Sub

    Private Sub RefreshComboItems()
        Dim ats = DirectCast(_registry.Columns("Ats"), DataGridViewComboBoxColumn)
        ats.Items.Clear()
        For Each bank In _working.Banks.OrderBy(Function(item) item.Number)
            ats.Items.Add(bank.Number.ToString(Inv))
        Next
        Dim selectedGroup = Convert.ToString(_assignmentGroup.SelectedItem)
        _assignmentGroup.Items.Clear()
        _assignmentGroup.Items.Add("Selected UPS")
        For Each bank In _working.Banks.OrderBy(Function(item) item.Number)
            _assignmentGroup.Items.Add("ATS " & bank.Number.ToString(Inv))
        Next
        _assignmentGroup.Items.AddRange({"Model contains NXL", "Model contains EXL"})
        _assignmentGroup.SelectedIndex = Math.Max(0, _assignmentGroup.Items.IndexOf(selectedGroup))
        Dim profile = DirectCast(_registry.Columns("Profile"), DataGridViewComboBoxColumn)
        profile.Items.Clear()
        Dim names = SnmpCredentialStore.ListProfiles().Select(Function(item) item.Name).
            Concat(_working.Devices.Select(Function(item) item.CredentialProfile)).
            Concat({"default"}).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(Function(item) item).ToArray()
        profile.Items.AddRange(names)
        Dim assignment = Convert.ToString(_assignProfile.SelectedItem)
        _assignProfile.Items.Clear()
        _assignProfile.Items.AddRange(SnmpCredentialStore.ListProfiles().Select(Function(item) item.Name).ToArray())
        If _assignProfile.Items.Contains(assignment) Then _assignProfile.SelectedItem = assignment
        If _assignProfile.SelectedIndex < 0 AndAlso _assignProfile.Items.Count > 0 Then _assignProfile.SelectedIndex = 0
        _scanProfile.Items.Clear()
        _scanProfile.Items.AddRange(names)
        If _scanProfile.Items.Count > 0 Then _scanProfile.SelectedIndex = Math.Max(0, Array.IndexOf(names, "default"))
    End Sub

    Private Sub AddRegistryRow(device As UpsDevice, isNew As Boolean)
        Dim index = _registry.Rows.Add(device.Id, device.Label, device.AtsBank.ToString(Inv), device.IpAddress, device.Port.ToString(Inv),
                                       device.CredentialProfile, device.Model, device.RatedKva.ToString("R", Inv), device.Enabled,
                                       device.PollIntervalSeconds.ToString(Inv), device.TimeoutMilliseconds.ToString(Inv), device.Retries.ToString(Inv),
                                       device.Verification.ToString(), device.Notes, device.UseProfileTransport)
        _registry.Rows(index).Tag = If(isNew, "new", "saved")
        _registry.Rows(index).Cells(0).Style.ForeColor = UiTheme.MutedInk
        If device.Verification <> UpsVerification.Verified Then _registry.Rows(index).Cells("Verification").Style.ForeColor = UpsAtsView.WarnAmber
    End Sub

    Private Function ReadRegistry() As UpsSettings
        If Not _registry.EndEdit() OrElse Not _banks.EndEdit() Then
            Throw New InvalidDataException("A table edit could not be committed. Correct the highlighted cell before saving or testing.")
        End If
        Dim result = _working.Clone()
        result.AcquisitionEnabled = _acquisitionEnabled.Checked
        result.HistorianEnabled = _historianEnabled.Checked
        result.MaxConcurrentPolls = CInt(_concurrency.Value)
        result.HistorianIntervalSeconds = CInt(_historianInterval.Value)
        result.HistorianRetentionDays = CInt(_retention.Value)
        result.Devices = New List(Of UpsDevice)()
        For Each row As DataGridViewRow In _registry.Rows
            Dim text = Function(column As String) Convert.ToString(row.Cells(column).Value, Inv).Trim()
            Dim number = Function(column As String) As Double
                             Dim value As Double
                             If Not Double.TryParse(text(column), NumberStyles.Float, Inv, value) OrElse Not Double.IsFinite(value) Then
                                 Throw New InvalidDataException("Row " & (row.Index + 1) & ": '" & _registry.Columns(column).HeaderText & "' must be a finite number.")
                             End If
                             Return value
                         End Function
            Dim verification As UpsVerification
            If Not [Enum].TryParse(text("Verification"), verification) OrElse Not [Enum].IsDefined(verification) Then
                Throw New InvalidDataException("Row " & (row.Index + 1) & ": select a valid identity verification status.")
            End If
            Dim whole = Function(column As String) As Integer
                            Dim value = number(column)
                            If value < Integer.MinValue OrElse value > Integer.MaxValue OrElse value <> Math.Truncate(value) Then
                                Throw New InvalidDataException("Row " & (row.Index + 1) & ": '" & _registry.Columns(column).HeaderText & "' must be a whole number within its supported range.")
                            End If
                            Return CInt(value)
                        End Function
            result.Devices.Add(New UpsDevice With {
                .Id = text("Id"), .Label = text("Label"), .AtsBank = whole("Ats"), .IpAddress = text("Ip"), .Port = whole("Port"),
                .CredentialProfile = text("Profile"), .Model = text("Model"), .RatedKva = number("Rated"),
                .UseProfileTransport = CBool(If(row.Cells("ProfileTransport").Value, False)),
                .Enabled = CBool(If(row.Cells("Enabled").Value, False)), .PollIntervalSeconds = whole("Interval"),
                .TimeoutMilliseconds = whole("Timeout"), .Retries = whole("Retries"), .Verification = verification, .Notes = text("Notes")})
        Next
        For Each row As DataGridViewRow In _banks.Rows
            Dim bank = result.Bank(CInt(row.Cells("Number").Value))
            If bank Is Nothing Then Continue For
            bank.Name = Convert.ToString(row.Cells("Name").Value, Inv).Trim()
            Dim nameplate As Double
            If Not Double.TryParse(Convert.ToString(row.Cells("Nameplate").Value, Inv), NumberStyles.Float, Inv, nameplate) OrElse
               Not Double.IsFinite(nameplate) OrElse nameplate <= 0 Then
                Throw New InvalidDataException(bank.Name & ": nameplate kVA must be a positive number.")
            End If
            bank.NameplateKva = nameplate
            bank.SourceDescription = Convert.ToString(row.Cells("Source").Value, Inv).Trim()
        Next
        result.Validate()
        Return result
    End Function

    Private Sub OnAddUnit(sender As Object, e As EventArgs)
        AddUnitRow(New UpsDevice With {.Id = NextId(), .Label = "NEW", .AtsBank = 1, .IpAddress = "192.168.107.", .CredentialProfile = "default",
                                       .Model = "", .RatedKva = 500, .Verification = UpsVerification.Unverified, .Notes = "Added manually"})
    End Sub

    Private Sub AddUnitRow(device As UpsDevice)
        AddRegistryRow(device, True)
        _registry.CurrentCell = _registry.Rows(_registry.RowCount - 1).Cells("Label")
        ShowMessage(_registryMessage, "Row added – complete it and press Save registry.", UiTheme.Blue)
    End Sub

    Private Function NextId() As String
        Dim used As New HashSet(Of String)(_registry.Rows.Cast(Of DataGridViewRow)().Select(Function(row) Convert.ToString(row.Cells("Id").Value, Inv)), StringComparer.OrdinalIgnoreCase)
        Dim n = 1
        While used.Contains("UPS-X" & n.ToString("00", Inv))
            n += 1
        End While
        Return "UPS-X" & n.ToString("00", Inv)
    End Function

    Private Sub OnRemoveUnit(sender As Object, e As EventArgs)
        If _registry.CurrentRow Is Nothing Then Return
        Dim id = Convert.ToString(_registry.CurrentRow.Cells("Id").Value, Inv)
        If MessageBox.Show(Me, "Remove " & id & " from the registry?" & Environment.NewLine &
                           "Its historian records are kept. The change applies when you press Save registry.",
                           "Remove UPS", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) <> DialogResult.OK Then Return
        _registry.Rows.Remove(_registry.CurrentRow)
        ShowMessage(_registryMessage, id & " removed (not saved yet).", UpsAtsView.WarnAmber)
    End Sub

    Private Sub OnSaveRegistry(sender As Object, e As EventArgs)
        If Not _canEdit Then
            ShowMessage(_registryMessage, "Read only: administrator access is required to save the registry.", UpsAtsView.AlarmRed)
            Return
        End If
        Try
            Dim settings = ReadRegistry()
            Dim affected = UpsSettingsStore.ProtectedAssignmentChanges(settings)
            If Not ConfirmWorkingChange(affected) Then Return
            UpsSettingsStore.Save(settings, _session.Username, _session.RoleName, _session.IsOfflinePreview, affected.Count > 0)
            LoadWorkingCopy()
            RefreshCredentialDevices()
            ShowMessage(_registryMessage, "Saved and audited " & DateTime.Now.ToString("HH:mm:ss", Inv) & " – acquisition picks up the change automatically.", UpsAtsView.OkGreen)
        Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is IOException
            ShowMessage(_registryMessage, ex.Message, UpsAtsView.AlarmRed)
        End Try
    End Sub

    Private Function ConfirmWorkingChange(devices As IReadOnlyList(Of String)) As Boolean
        Return devices.Count = 0 OrElse (_canEdit AndAlso MessageBox.Show(Me,
            "This changes a protected connection or a shared profile. Affected UPS devices: " & String.Join(", ", devices) &
            Environment.NewLine & "Existing protected settings are backed up. Confirm this administrator-authorized change?",
            "Protect working UPS connections", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) = DialogResult.Yes)
    End Function

    Private Async Sub OnTestSelected(sender As Object, e As EventArgs)
        If _registry.CurrentRow Is Nothing OrElse _testCancellation IsNot Nothing OrElse _scanCancellation IsNot Nothing Then Return
        If _registry.SelectedRows.Count > 1 Then
            Try
                Dim selected As New HashSet(Of String)(_registry.SelectedRows.Cast(Of DataGridViewRow)().
                    Select(Function(item) Convert.ToString(item.Cells("Id").Value)), StringComparer.OrdinalIgnoreCase)
                Dim devices = ReadRegistry().Devices.Where(Function(item) selected.Contains(item.Id)).ToList()
                _tabs.SelectedIndex = 3
                RunConnectionTests(devices)
            Catch ex As InvalidDataException
                ShowMessage(_registryMessage, ex.Message, UpsAtsView.AlarmRed)
            End Try
            Return
        End If
        Dim row = _registry.CurrentRow
        Dim ip = Convert.ToString(row.Cells("Ip").Value, Inv).Trim()
        Dim port As Integer
        Dim timeout As Integer
        Dim address As IPAddress = Nothing
        If Not IPAddress.TryParse(ip, address) OrElse Not Integer.TryParse(Convert.ToString(row.Cells("Port").Value, Inv), port) OrElse
           Not Integer.TryParse(Convert.ToString(row.Cells("Timeout").Value, Inv), timeout) Then
            ShowMessage(_registryMessage, "Enter a valid IP, port and timeout first.", UpsAtsView.AlarmRed)
            Return
        End If
        Dim profile = Convert.ToString(row.Cells("Profile").Value, Inv)
        Dim device As UpsDevice
        Try
            device = ReadRegistry().Devices.First(Function(item) item.Id = Convert.ToString(row.Cells("Id").Value, Inv))
        Catch ex As InvalidDataException
            ShowMessage(_registryMessage, ex.Message, UpsAtsView.AlarmRed)
            Return
        End Try
        ShowMessage(_registryMessage, "Testing " & ip & " (ping, read-only GET, SNMPv3 engine probe)…", UiTheme.Blue)
        Dim result As UpsConnectionTest
        Dim cancellation As New CancellationTokenSource()
        _testCancellation = cancellation
        Try
            result = Await Task.Run(Function() UpsConnectionDiagnostics.TestDevice(device, cancellation.Token))
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
            ShowMessage(_registryMessage, ex.Message, UpsAtsView.AlarmRed)
            Return
        Finally
            cancellation.Dispose()
            _testCancellation = Nothing
        End Try
        If IsDisposed Then Return
        If result.IsConnected Then
            ShowMessage(_registryMessage, ip & " " & UpsConnectionDiagnostics.Describe(result.Finding) & " in " & UpsConnectionDiagnostics.FormatMs(result.LatencyMilliseconds) &
                        "  •  sysName '" & result.SysName & "'  •  " & (result.Manufacturer & " " & result.Model).Trim(), UpsAtsView.OkGreen)
            If UpsAcquisitionService.Current IsNot Nothing Then UpsAcquisitionService.Current.PollNow(device.Id)
        Else
            ShowMessage(_registryMessage, ip & ": " & UpsConnectionDiagnostics.Describe(result.Finding) & " – details and card checklist on the Diagnostics tab.", UpsAtsView.AlarmRed)
        End If
    End Sub

    ' ---------- Credentials ----------
    Private Function BuildCredentialsTab() As TabPage
        Dim page = NewTab("Card Settings")
        _cardTabs.Dock = DockStyle.Fill
        _cardTabs.Font = New Font("Segoe UI", 10.0F)
        _cardTabs.Appearance = TabAppearance.FlatButtons
        _cardTabs.SizeMode = TabSizeMode.Fixed
        _cardTabs.ItemSize = New Size(0, 1)
        _cardTabs.Padding = Point.Empty
        For Each tabCaption In {"Overview", "Web Login", "SNMP Profile", "Profile Library"}
            _cardTabs.TabPages.Add(New TabPage(tabCaption) With {.BackColor = UiTheme.Canvas, .Padding = New Padding(12), .AutoScroll = True})
        Next
        AddHandler _cardTabs.SelectedIndexChanged, Sub(sender, e)
                                                      MaskEnteredSecrets()
                                                      If _cardTabs.SelectedIndex <> 0 Then Return
                                                      Try
                                                          RefreshCardOverview()
                                                      Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
                                                          ShowMessage(_credentialMessage, ex.Message, UiTheme.Red)
                                                      End Try
                                                  End Sub
        page.Controls.Add(_cardTabs)
        page.Controls.Add(New SegmentedTabStrip(_cardTabs))
        AddHandler _credentialDevice.SelectedIndexChanged, Sub(sender, e) LoadCredentialDevice()
        _deviceIdentity.ForeColor = UiTheme.Ink
        _credentialDevice.Width = 210
        _deviceIdentity.Margin = New Padding(12, 9, 8, 0)
        Dim selector = Toolbar(UiTheme.Label("UPS CARD", New Font("Segoe UI", 9.0F, FontStyle.Bold), UiTheme.MutedInk), _credentialDevice, _deviceIdentity)
        selector.Padding = New Padding(8, 8, 8, 12)
        page.Controls.Add(selector)
        _cardTabs.TabPages(0).Controls.Add(BuildCardOverview())
        _cardTabs.TabPages(1).Controls.Add(BuildWebCardSection())
        StyleGrid(_profiles, False)
        _profiles.Dock = DockStyle.Fill
        For Each column In {"Profile", "Version", "v3 user", "Auth", "Privacy", "Community / secrets", "Last change"}
            _profiles.Columns.Add(column.Replace(" ", "").Replace("/", ""), column)
        Next
        ReadableColumns(_profiles, 130, 65, 110, 75, 75, 170, 190)
        Dim profileWeights As Single() = {120, 60, 110, 75, 75, 180, 220}
        For index = 0 To profileWeights.Length - 1
            _profiles.Columns(index).FillWeight = profileWeights(index)
        Next
        _profileVersion.Items.AddRange(SnmpCredentialStore.Versions)
        _authProtocol.Items.AddRange(SnmpCredentialStore.AuthProtocols)
        _privProtocol.Items.AddRange(SnmpCredentialStore.PrivProtocols)
        _securityMode.Items.AddRange({"NoAuthNoPriv", "AuthNoPriv", "AuthPriv"})
        _securityMode.SelectedItem = "AuthPriv"
        _profileVersion.SelectedItem = "v2c"
        _authProtocol.SelectedItem = "SHA256"
        _privProtocol.SelectedItem = "AES128"
        AddHandler _profileVersion.SelectedIndexChanged, Sub(sender, e) UpdateCredentialFields()
        AddHandler _securityMode.SelectedIndexChanged, Sub(sender, e) UpdateCredentialFields()
        AddHandler _authProtocol.SelectedIndexChanged, Sub(sender, e) UpdateCredentialFields()
        AddHandler _privProtocol.SelectedIndexChanged, Sub(sender, e) UpdateCredentialFields()
        AddHandler _showSecrets.CheckedChanged, Sub(sender, e)
                                                   For Each secret In {_community, _confirmCommunity, _authSecret, _privSecret}
                                                       secret.UseSystemPasswordChar = Not (_canEdit AndAlso _showSecrets.Checked)
                                                   Next
                                                   If _showSecrets.Checked Then _revealTimer.Start()
                                               End Sub
        AddHandler _profiles.SelectionChanged, Sub(sender, e) LoadSelectedProfile()
        AddHandler _profiles.CellDoubleClick, Sub(sender, e)
                                                 If e.RowIndex >= 0 Then _cardTabs.SelectedIndex = 2
                                             End Sub

        Dim form As New TableLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .ColumnCount = 4, .RowCount = 11, .BackColor = UiTheme.Surface, .MaximumSize = New Size(1000, 0)}
        For rowIndex = 0 To 10
            form.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        Next
        form.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 135))
        form.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50))
        form.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 145))
        form.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50))
        Dim caption = Function(text As String) As Label
                          Dim l = UiTheme.Label(text, New Font("Segoe UI", 10.0F), UiTheme.MutedInk)
                          l.Margin = New Padding(0, 8, 8, 10)
                          Return l
                      End Function
        form.Controls.Add(caption("Profile name"), 0, 0) : form.Controls.Add(_profileName, 1, 0)
        form.Controls.Add(caption("SNMP version"), 2, 0) : form.Controls.Add(_profileVersion, 3, 0)
        _communityHeading = caption("Read-only community")
        _communityHeading.ForeColor = UiTheme.Blue
        form.Controls.Add(_communityHeading, 0, 1) : form.SetColumnSpan(_communityHeading, 4)
        form.Controls.Add(caption("Access community"), 0, 2) : form.Controls.Add(_community, 1, 2)
        form.Controls.Add(caption("Confirm community"), 2, 2) : form.Controls.Add(_confirmCommunity, 3, 2)
        _v3Heading = caption("SNMPv3 authentication")
        _v3Heading.ForeColor = UiTheme.Blue
        form.Controls.Add(_v3Heading, 0, 3) : form.SetColumnSpan(_v3Heading, 4)
        form.Controls.Add(caption("v3 user name"), 0, 4) : form.Controls.Add(_v3User, 1, 4)
        form.Controls.Add(caption("Security level"), 2, 4) : form.Controls.Add(_securityMode, 3, 4)
        form.Controls.Add(caption("Authentication"), 0, 5) : form.Controls.Add(_authProtocol, 1, 5)
        form.Controls.Add(caption("Auth password"), 2, 5) : form.Controls.Add(_authSecret, 3, 5)
        form.Controls.Add(caption("Privacy"), 0, 6) : form.Controls.Add(_privProtocol, 1, 6)
        form.Controls.Add(caption("Privacy password"), 2, 6) : form.Controls.Add(_privSecret, 3, 6)
        form.Controls.Add(caption("UDP port"), 0, 7) : form.Controls.Add(_profilePort, 1, 7)
        Dim transport As New FlowLayoutPanel With {.AutoSize = True, .Dock = DockStyle.Fill, .WrapContents = True}
        transport.Controls.AddRange({caption("Timeout ms"), _profileTimeout, caption("Retries"), _profileRetries})
        form.Controls.Add(transport, 2, 7) : form.SetColumnSpan(transport, 2)
        _credentialSource.ForeColor = UiTheme.MutedInk
        form.Controls.Add(_credentialSource, 0, 8) : form.SetColumnSpan(_credentialSource, 2)
        form.Controls.Add(_showSecrets, 2, 8) : form.SetColumnSpan(_showSecrets, 2)
        Dim hint = UiTheme.Label("Leave passwords blank to keep saved values. Changes apply in EOEMS only, not on the card.",
                                 New Font("Segoe UI", 9.0F), UiTheme.MutedInk, False)
        hint.Dock = DockStyle.Fill
        hint.AutoSize = True
        hint.MaximumSize = New Size(800, 0)
        form.Controls.Add(hint, 0, 9)
        form.SetColumnSpan(hint, 4)
        _profileSummary.ForeColor = UiTheme.Blue
        _profileSummary.Margin = New Padding(0, 8, 0, 8)
        _profileSummary.Dock = DockStyle.Fill
        form.Controls.Add(_profileSummary, 0, 10)
        form.SetColumnSpan(_profileSummary, 4)
        For Each field As Control In {_community, _confirmCommunity, _v3User, _authProtocol, _authSecret, _privProtocol, _privSecret, _securityMode}
            Dim position = form.GetPositionFromControl(field)
            _credentialCaptions(field) = form.GetControlFromPosition(position.Column - 1, position.Row)
        Next
        For Each control As Control In {_profileName, _profileVersion, _community, _confirmCommunity, _v3User, _authProtocol, _authSecret, _privProtocol, _privSecret,
                                       _securityMode, _profilePort, _profileTimeout, _profileRetries, _showSecrets}
            control.Enabled = _canEdit
            control.Font = New Font("Segoe UI", 10.0F)
            control.Margin = New Padding(0, 4, 16, 6)
            If TypeOf control Is TextBox OrElse TypeOf control Is ComboBox Then control.Dock = DockStyle.Fill
        Next

        _credentialMessage.AutoSize = False
        _credentialMessage.Height = 66
        _credentialMessage.Padding = New Padding(12, 6, 12, 6)
        _credentialMessage.BackColor = UiTheme.Surface
        _credentialMessage.Visible = False
        AddHandler _credentialMessage.TextChanged, Sub(sender, e) _credentialMessage.Visible = _credentialMessage.Text.Length > 0
        _credentialMessage.Margin = New Padding(0, 8, 0, 0)
        _credentialMessage.AutoEllipsis = False
        _credentialMessage.TextAlign = ContentAlignment.MiddleLeft
        _credentialMessage.Dock = DockStyle.Bottom
        Dim actions = Toolbar(
            SmallButton("Save Profile", True, AddressOf OnSaveProfile),
            SmallButton("Assign to This UPS", False, AddressOf OnAssignCredentialDevice),
            SmallButton("Test SNMP Connection", False, AddressOf OnTestProfile))
        actions.Dock = DockStyle.Bottom
        actions.Padding = New Padding(12, 8, 12, 8)
        Dim editor As New TableLayoutPanel With {.AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink, .ColumnCount = 1, .Dock = DockStyle.Top}
        editor.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        editor.Controls.Add(form)
        Dim profileScroll As New Panel With {.Dock = DockStyle.Fill, .AutoScroll = True}
        profileScroll.Controls.Add(SettingsSection("SNMP profile", editor))
        _cardTabs.TabPages(2).AutoScroll = False
        _cardTabs.TabPages(2).Controls.Add(profileScroll)
        _cardTabs.TabPages(2).Controls.Add(actions)
        page.Controls.Add(_credentialMessage)
        Dim library = _cardTabs.TabPages(3)
        library.Controls.Add(_profiles)
        library.Controls.Add(Toolbar(
            SmallButton("New Profile", True, Sub(sender, e)
                                                 ClearProfileForm()
                                                 _cardTabs.SelectedIndex = 2
                                             End Sub),
            SmallButton("Edit Selected", False, Sub(sender, e)
                                                   LoadSelectedProfile()
                                                   _cardTabs.SelectedIndex = 2
                                               End Sub, False),
            SmallButton("Duplicate", False, AddressOf OnDuplicateProfile),
            SmallButton("Load Assigned", False, Sub(sender, e) SelectAssignedProfile(), False),
            SmallButton("Delete Unused", False, AddressOf OnDeleteProfile)))
        UpdateCredentialFields()
        Return page
    End Function

    Private Function BuildCardOverview() As Control
        Dim body As New TableLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink, .ColumnCount = 1, .MaximumSize = New Size(1050, 0)}
        body.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        body.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        body.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        Dim summary = Function(label As Label) As Control
                          label.Dock = DockStyle.Top
                          label.Font = New Font("Segoe UI", 10.0F)
                          label.ForeColor = UiTheme.MutedInk
                          label.Margin = New Padding(0, 0, 0, 12)
                          AddHandler body.SizeChanged, Sub(sender, e) label.MaximumSize = New Size(Math.Max(260, body.ClientSize.Width - 32), 0)
                          Return label
                      End Function
        Dim web As New TableLayoutPanel With {.AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink, .ColumnCount = 1}
        web.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        For row = 0 To 2
            web.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        Next
        _cardWebStatus.Visible = False
        AddHandler _cardWebStatus.TextChanged, Sub(sender, e) _cardWebStatus.Visible = _cardWebStatus.Text.Length > 0
        web.Controls.Add(summary(_cardWebSummary))
        web.Controls.Add(summary(_cardWebStatus))
        Dim webActions = Toolbar(
            SmallButton("Open Web Card", True, AddressOf OnOpenWebCard),
            SmallButton("Edit Web Login", False, Sub(sender, e) _cardTabs.SelectedIndex = 1))
        webActions.AutoSize = False
        webActions.Height = 48
        webActions.WrapContents = False
        webActions.AutoScroll = True
        web.Controls.Add(webActions)
        body.Controls.Add(SettingsSection("Local web interface", web))
        Dim snmp As New TableLayoutPanel With {.AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink, .ColumnCount = 1}
        snmp.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        For row = 0 To 2
            snmp.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        Next
        snmp.Controls.Add(summary(_cardSnmpSummary))
        snmp.Controls.Add(summary(_cardStatus))
        Dim snmpActions = Toolbar(
            SmallButton("Test Connection", True, Sub(sender, e)
                                                   SelectAssignedProfile()
                                                   OnTestProfile(sender, e)
                                               End Sub),
            SmallButton("Edit SNMP Profile", False, Sub(sender, e)
                                                       SelectAssignedProfile()
                                                       _cardTabs.SelectedIndex = 2
                                                   End Sub),
            SmallButton("Manage Profiles", False, Sub(sender, e) _cardTabs.SelectedIndex = 3, False))
        snmpActions.AutoSize = False
        snmpActions.Height = 48
        snmpActions.WrapContents = False
        snmpActions.AutoScroll = True
        snmp.Controls.Add(snmpActions)
        body.Controls.Add(SettingsSection("SNMP monitoring", snmp))
        Return body
    End Function

    Private Function BuildWebCardSection() As Control
        Dim table As New TableLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink, .ColumnCount = 2,
            .MaximumSize = New Size(760, 0), .Padding = New Padding(0, 4, 0, 0)}
        table.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 155))
        table.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        Dim caption = Function(text As String) New Label With {.Text = text, .AutoSize = True, .Font = New Font("Segoe UI", 10.0F),
            .ForeColor = UiTheme.MutedInk, .Margin = New Padding(0, 8, 12, 14)}
        _webProtocol.Items.AddRange({"HTTP", "HTTPS"})
        AddHandler _webProtocol.SelectedIndexChanged, AddressOf OnWebProtocolChanged
        AddHandler _webUrl.TextChanged, Sub(sender, e) SyncWebProtocol()
        table.Controls.Add(caption("Protocol"), 0, 0)
        table.Controls.Add(_webProtocol, 1, 0)
        table.Controls.Add(caption("Card URL"), 0, 1)
        table.Controls.Add(_webUrl, 1, 1)
        table.Controls.Add(caption("Username"), 0, 2)
        table.Controls.Add(_webUser, 1, 2)
        table.Controls.Add(caption("Password"), 0, 3)
        table.Controls.Add(_webPassword, 1, 3)
        table.Controls.Add(_showWebPassword, 1, 4)
        Dim actions = Toolbar(
            SmallButton("Save Web Login", True, AddressOf OnSaveWebCredentials),
            SmallButton("Open in Browser", False, AddressOf OnOpenWebCard))
        table.Controls.Add(actions, 1, 5)
        _webMessage.ForeColor = UiTheme.MutedInk
        _webMessage.Font = New Font("Segoe UI", 9.5F)
        _webMessage.Dock = DockStyle.Fill
        _webMessage.Margin = New Padding(0, 12, 0, 0)
        AddHandler table.SizeChanged, Sub(sender, e) _webMessage.MaximumSize = New Size(Math.Max(260, table.ClientSize.Width - 16), 0)
        table.Controls.Add(_webMessage, 0, 6) : table.SetColumnSpan(_webMessage, 2)
        Dim note = caption("HTTP is available on these local cards but does not encrypt browser login. EOEMS never sends your saved web password.")
        note.Font = New Font("Segoe UI", 9.0F)
        note.MaximumSize = New Size(700, 0)
        table.Controls.Add(note, 0, 7) : table.SetColumnSpan(note, 2)
        For Each control As Control In {_webUrl, _webUser, _webPassword, _showWebPassword, _webProtocol}
            control.Enabled = _canEdit
            control.Margin = New Padding(0, 4, 0, 14)
            control.Font = New Font("Segoe UI", 10.0F)
            If control IsNot _webProtocol Then control.Dock = DockStyle.Fill
        Next
        AddHandler _showWebPassword.CheckedChanged, Sub(sender, e)
                                                       _webPassword.UseSystemPasswordChar = Not (_canEdit AndAlso _showWebPassword.Checked)
                                                       If _showWebPassword.Checked Then _revealTimer.Start()
                                                   End Sub
        Dim section = SettingsSection("Web login - selected card only", table)
        section.MaximumSize = New Size(820, 0)
        Return section
    End Function

    Private Sub SyncWebProtocol()
        If _loadingWebProtocol Then Return
        Dim address As Uri = Nothing
        If Not Uri.TryCreate(_webUrl.Text.Trim(), UriKind.Absolute, address) OrElse
            (address.Scheme <> Uri.UriSchemeHttp AndAlso address.Scheme <> Uri.UriSchemeHttps) Then Return
        _loadingWebProtocol = True
        Try
            _webProtocol.SelectedItem = address.Scheme.ToUpperInvariant()
        Finally
            _loadingWebProtocol = False
        End Try
    End Sub

    Private Sub OnWebProtocolChanged(sender As Object, e As EventArgs)
        If _loadingWebProtocol Then Return
        Dim device = SelectedCredentialDevice()
        If device Is Nothing OrElse _webProtocol.SelectedItem Is Nothing Then Return
        Try
            Dim address = WebCardCredentialStore.ManagementUri(device.Id, _webUrl.Text.Trim())
            _webUrl.Text = New UriBuilder(address) With {
                .Scheme = Convert.ToString(_webProtocol.SelectedItem).ToLowerInvariant(),
                .Port = If(address.IsDefaultPort, -1, address.Port)}.Uri.AbsoluteUri
            ShowWebMessage("Protocol changed in the editor. Save Web Login to keep this address.", UiTheme.Amber)
        Catch ex As InvalidDataException
            ShowWebMessage(ex.Message, UiTheme.Red)
        End Try
    End Sub

    Private Sub ShowWebMessage(text As String, color As Color)
        ShowMessage(_webMessage, text, color)
        ShowMessage(_cardWebStatus, text, color)
    End Sub

    Private Sub RefreshCardOverview()
        Dim device = SelectedCredentialDevice()
        If device Is Nothing Then
            _cardWebSummary.Text = "Select a registered UPS."
            _cardSnmpSummary.Text = String.Empty
            _cardStatus.Text = String.Empty
            Return
        End If
        _cardWebSummary.Text = _webUrl.Text & Environment.NewLine &
            "User: " & If(_webUser.Text.Length = 0, "not saved", _webUser.Text) &
            "   |   Password: " & If(_webPasswordSaved, "saved", "not saved")
        Dim profile = SnmpCredentialStore.ListProfiles().FirstOrDefault(
            Function(item) String.Equals(item.Name, device.CredentialProfile, StringComparison.OrdinalIgnoreCase))
        If profile Is Nothing Then
            _cardSnmpSummary.Text = "Assigned profile '" & device.CredentialProfile & "' is missing."
        Else
            Dim effective = SnmpCredentialStore.EffectiveDevice(device)
            _cardSnmpSummary.Text = profile.Name & "   /   " & profile.Version & "   /   " &
                If(profile.IsConfigured, "Credentials saved", "Credentials incomplete") & Environment.NewLine &
                "UDP " & effective.Port & "   |   Timeout " & effective.TimeoutMilliseconds & " ms   |   Retries " & effective.Retries
        End If
        Dim test = UpsConnectionDiagnostics.LastTest(device.Id)
        _cardStatus.Text = If(test Is Nothing, "Not tested in this session.", UpsConnectionDiagnostics.Describe(test.Finding))
        _cardStatus.ForeColor = If(test IsNot Nothing AndAlso test.IsConnected, UiTheme.Green, UiTheme.Amber)
    End Sub

    Private Sub MaskEnteredSecrets()
        _showSecrets.Checked = False
        _showWebPassword.Checked = False
        _revealTimer.Stop()
    End Sub

    Private Function SelectedCredentialDevice() As UpsDevice
        Dim selected = TryCast(_credentialDevice.SelectedItem, UpsDevice)
        Return If(selected Is Nothing, Nothing, UpsSettingsStore.Current.Devices.SingleOrDefault(Function(item) item.Id = selected.Id))
    End Function

    Private Sub RefreshCredentialDevices()
        Dim previous = SelectedCredentialDevice()
        _credentialDevice.Items.Clear()
        _credentialDevice.Items.AddRange(UpsSettingsStore.Current.Devices.ToArray())
        Dim selected = If(previous Is Nothing, Nothing, UpsSettingsStore.Current.Devices.SingleOrDefault(Function(item) item.Id = previous.Id))
        If selected IsNot Nothing Then _credentialDevice.SelectedItem = selected
        If _credentialDevice.SelectedIndex < 0 AndAlso _credentialDevice.Items.Count > 0 Then _credentialDevice.SelectedIndex = 0
    End Sub

    Private Sub LoadCredentialDevice()
        MaskEnteredSecrets()
        _webPassword.Clear()
        _webPassword.PlaceholderText = String.Empty
        _webUser.Clear()
        _webUrl.Clear()
        _webMessage.Text = String.Empty
        _cardWebStatus.Text = String.Empty
        _webPasswordSaved = False
        _credentialMessage.Text = String.Empty
        _deviceIdentity.Text = String.Empty
        ClearProfileForm(False)
        Dim device = SelectedCredentialDevice()
        If device Is Nothing Then
            RefreshCardOverview()
            Return
        End If
        _deviceIdentity.Text = device.IpAddress & "   |   ATS " & device.AtsBank & "   |   Profile: " & device.CredentialProfile
        _credentialSource.Text = "Manager: " & UpsConnectionDiagnostics.SourceAddressFor(New IPEndPoint(IPAddress.Parse(device.IpAddress), device.Port))
        Try
            If _canEdit Then
                Dim login = WebCardCredentialStore.Load(device.Id, _session)
                _webMessage.ForeColor = UiTheme.MutedInk
                _webUrl.Text = login.ManagementUrl
                _webUser.Text = login.Username
                _webPasswordSaved = login.HasPassword
                _webPassword.PlaceholderText = If(login.HasPassword, "Saved password - leave blank to keep", "No saved password")
                _webMessage.Text = If(login.HasPassword, "Saved web login loaded. ", "No saved web login. ") &
                    "Sign in in your browser after opening the card."
            Else
                _webMessage.Text = "Administrator access is required to manage Web Card login credentials."
            End If
            SelectAssignedProfile()
            RefreshCardOverview()
            If Not SnmpCredentialStore.ListProfiles().Any(Function(profile) String.Equals(profile.Name, device.CredentialProfile, StringComparison.OrdinalIgnoreCase)) Then
                ShowMessage(_credentialMessage, "Assigned profile '" & device.CredentialProfile & "' is missing. Create or assign a saved profile before testing.", UiTheme.Red)
                Return
            End If
            Dim test = UpsConnectionDiagnostics.LastTest(device.Id)
            ShowMessage(_credentialMessage, If(test Is Nothing, String.Empty,
                device.Id & ": " & UpsConnectionDiagnostics.Describe(test.Finding) & Environment.NewLine & test.Summary),
                If(test IsNot Nothing AndAlso test.IsConnected, UpsAtsView.OkGreen, UpsAtsView.WarnAmber))
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
            ShowWebMessage(ex.Message, UpsAtsView.AlarmRed)
        End Try
    End Sub

    Private Sub SelectAssignedProfile()
        Dim device = SelectedCredentialDevice()
        If device Is Nothing Then Return
        SelectProfile(device.CredentialProfile)
    End Sub

    Private Sub SelectProfile(name As String)
        For Each row As DataGridViewRow In _profiles.Rows
            If String.Equals(Convert.ToString(row.Cells(0).Value), name, StringComparison.OrdinalIgnoreCase) Then
                _profiles.CurrentCell = row.Cells(0)
                LoadSelectedProfile()
                Return
            End If
        Next
        ClearProfileForm()
    End Sub

    Private Sub OnSaveWebCredentials(sender As Object, e As EventArgs)
        Try
            Dim device = SelectedCredentialDevice()
            If device Is Nothing Then Throw New InvalidDataException("Select a registered UPS.")
            WebCardCredentialStore.Save(device.Id, _webUrl.Text.Trim(), _webUser.Text, If(_webPassword.Text.Length = 0, Nothing, _webPassword.Text), _session)
            _webPassword.Clear()
            _webPassword.PlaceholderText = "Saved password - leave blank to keep"
            _webPasswordSaved = True
            MaskEnteredSecrets()
            RefreshCardOverview()
            ShowWebMessage("Web login saved for " & device.Id & ". Sign in in your browser; no card settings were changed.", UiTheme.Green)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is System.Security.Cryptography.CryptographicException
            ShowWebMessage(ex.Message, UpsAtsView.AlarmRed)
        End Try
    End Sub

    Private Async Sub OnOpenWebCard(sender As Object, e As EventArgs)
        If _webCancellation IsNot Nothing Then Return
        Dim button = TryCast(sender, Button)
        Dim cancellation As New CancellationTokenSource()
        _webCancellation = cancellation
        If button IsNot Nothing Then button.Enabled = False
        Try
            WebCardCredentialStore.RequireAdministrator(_session)
            Dim device = SelectedCredentialDevice()
            If device Is Nothing Then Throw New InvalidDataException("Select a registered UPS.")
            Dim url = WebCardCredentialStore.ManagementUri(device.Id, _webUrl.Text.Trim())
            ShowWebMessage("Checking the local card web service...", UiTheme.Blue)
            Dim check = Await WebCardConnection.CheckForOpeningAsync(device.Id, url.AbsoluteUri, cancellation.Token)
            If Not WebSelectionMatches(device.Id, url) Then Return
            If check.Responds AndAlso check.Address.Scheme <> url.Scheme Then
                If MessageBox.Show(Me, "The card responds at " & check.Address.AbsoluteUri & ", not at its saved HTTPS address." & Environment.NewLine &
                    "HTTP does not encrypt browser login, even on a local network." & Environment.NewLine &
                    "Open HTTP under your site's management-network policy? No saved credentials are sent automatically." &
                    Environment.NewLine & "Save Web Login afterwards to keep the working address.",
                    "Open local card over HTTP", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) <> DialogResult.Yes Then
                    ShowWebMessage("HTTP is available. Opening cancelled; saved address unchanged.", UiTheme.Amber)
                    Return
                End If
                If Not WebSelectionMatches(device.Id, url) Then Return
                url = check.Address
                _webUrl.Text = url.AbsoluteUri
                RefreshCardOverview()
            ElseIf check.RequiresBrowserReview Then
                If MessageBox.Show(Me, check.Detail & Environment.NewLine & Environment.NewLine &
                    "HTTPS could not be verified. Open the browser to inspect its certificate or TLS warning?" & Environment.NewLine &
                    "Certificate validation has not been disabled. Do not proceed past a browser warning without site approval.",
                    "Review HTTPS in browser", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) <> DialogResult.OK Then
                    ShowWebMessage("HTTPS verification failed; browser opening cancelled.", UiTheme.Amber)
                    Return
                End If
            ElseIf Not check.Responds Then
                Throw New IOException(check.Detail & " Verify the card IP, port and web service.")
            ElseIf url.Scheme = Uri.UriSchemeHttp Then
                If MessageBox.Show(Me, "The card responds over HTTP. HTTP does not encrypt your browser login." & Environment.NewLine &
                    "Open only if your site's management-network policy permits it. Sign in manually; stored credentials are not sent.",
                    "Open Web Card", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) <> DialogResult.OK Then
                    ShowWebMessage("Card replied; browser opening cancelled.", UiTheme.Amber)
                    Return
                End If
            End If
            If Not WebSelectionMatches(device.Id, url) Then Return
            System.Diagnostics.Process.Start(New System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) With {.UseShellExecute = True})
            ShowWebMessage(If(check.RequiresBrowserReview, "HTTPS not verified. Browser opened for review at ", "Web server reached. Browser opened at ") & url.AbsoluteUri &
                " Sign in there; this does not verify SNMP." & If(url.Scheme = Uri.UriSchemeHttp, " Save Web Login to retain this URL.", ""),
                If(check.RequiresBrowserReview, UiTheme.Amber, UiTheme.Green))
        Catch ex As OperationCanceledException
            If Not IsDisposed Then ShowWebMessage("Web connection check cancelled.", UiTheme.Amber)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse
            TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is System.ComponentModel.Win32Exception
            If Not IsDisposed Then ShowWebMessage(ex.Message, UpsAtsView.AlarmRed)
        Finally
            _webCancellation = Nothing
            cancellation.Dispose()
            If Not IsDisposed AndAlso button IsNot Nothing Then button.Enabled = _canEdit
        End Try
    End Sub

    Private Function WebSelectionMatches(deviceId As String, url As Uri) As Boolean
        If IsDisposed Then Return False
        Dim selected = SelectedCredentialDevice()
        Dim current As Uri = Nothing
        Return selected IsNot Nothing AndAlso selected.Id = deviceId AndAlso
            Uri.TryCreate(_webUrl.Text.Trim(), UriKind.Absolute, current) AndAlso current = url
    End Function

    Private Sub OnAssignCredentialDevice(sender As Object, e As EventArgs)
        Try
            Dim device = SelectedCredentialDevice()
            If device Is Nothing OrElse String.IsNullOrEmpty(_editingProfile) Then Throw New InvalidDataException("Select a UPS and save a profile first.")
            If _community.Text.Length > 0 OrElse _authSecret.Text.Length > 0 OrElse _privSecret.Text.Length > 0 Then Throw New InvalidDataException("Save or discard entered secrets before assigning the saved profile.")
            If MessageBox.Show(Me, "Assign saved profile '" & _editingProfile & "' ONLY to " & device.Id & " (" & device.IpAddress & ")?" &
                Environment.NewLine & "Profile UDP port/timeout/retries will apply. Other devices and card settings will not change." &
                If(SnmpCredentialStore.IsProtectedDevice(device), Environment.NewLine & "This is a protected working-device assignment; confirm the intentional change.", ""),
                "Confirm device-only assignment", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) <> DialogResult.Yes Then Return
            UpsCredentialConfiguration.AssignProfile(device.Id, _editingProfile, _session, True)
            LoadWorkingCopy()
            RefreshCredentialDevices()
            ShowMessage(_credentialMessage, device.Id & " assignment saved. Run Test SNMP Connection to verify identity and measurements.", UiTheme.Blue)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
            ShowMessage(_credentialMessage, ex.Message, UpsAtsView.AlarmRed)
        End Try
    End Sub

    Private Sub UpdateCredentialFields()
        If _loadingProfile Then Return
        _loadingProfile = True
        Try
        Dim v3 = String.Equals(Convert.ToString(_profileVersion.SelectedItem), "v3", StringComparison.Ordinal)
        _communityHeading.Visible = Not v3
        _v3Heading.Visible = v3
        Dim mode = Convert.ToString(_securityMode.SelectedItem)
        Dim auth = v3 AndAlso mode <> "NoAuthNoPriv"
        Dim privacy = v3 AndAlso mode = "AuthPriv"
        If Not auth Then
            _authProtocol.SelectedItem = "None"
        ElseIf Convert.ToString(_authProtocol.SelectedItem) = "None" Then
            _authProtocol.SelectedItem = "SHA256"
        End If
        If Not privacy Then
            _privProtocol.SelectedItem = "None"
        ElseIf Convert.ToString(_privProtocol.SelectedItem) = "None" Then
            _privProtocol.SelectedItem = "AES128"
        End If
        _community.Visible = Not v3
        _community.Enabled = _canEdit AndAlso Not v3
        _confirmCommunity.Visible = Not v3
        _confirmCommunity.Enabled = _canEdit AndAlso Not v3
        For Each control As Control In {_v3User, _securityMode}
            control.Visible = v3
            control.Enabled = _canEdit AndAlso v3
        Next
        For Each control As Control In {_authProtocol, _authSecret}
            control.Visible = auth
            control.Enabled = _canEdit AndAlso auth
        Next
        For Each control As Control In {_privProtocol, _privSecret}
            control.Visible = privacy
            control.Enabled = _canEdit AndAlso privacy
        Next
        For Each pair In _credentialCaptions
            pair.Value.Visible = If(pair.Key Is _community OrElse pair.Key Is _confirmCommunity, Not v3,
                If(pair.Key Is _v3User OrElse pair.Key Is _securityMode, v3,
                   If(pair.Key Is _authProtocol OrElse pair.Key Is _authSecret, auth, privacy)))
        Next
        Finally
            _loadingProfile = False
        End Try
    End Sub

    Private Sub LoadProfiles()
        Dim selectedName = _editingProfile
        _loadingProfiles = True
        Try
        _profiles.Rows.Clear()
        For Each profile In SnmpCredentialStore.ListProfiles()
            Dim secrets = If(profile.Version = "v3",
                             "auth " & If(profile.HasAuthSecret, "set", "—") & " / priv " & If(profile.HasPrivSecret, "set", "—"),
                             If(profile.HasCommunity, "community set (hidden)", "NO COMMUNITY"))
            _profiles.Rows.Add(profile.Name, profile.Version, profile.UserName, profile.AuthProtocol, profile.PrivProtocol, secrets,
                               If(profile.UpdatedUtc.HasValue, profile.UpdatedUtc.Value.ToLocalTime().ToString("dd MMM yyyy HH:mm", Inv) & "  by " & profile.UpdatedBy, ""))
        Next
        If _profiles.RowCount = 0 Then
            ShowMessage(_credentialMessage, "No credential profile yet – create 'default' with the site community so polling can start.", UpsAtsView.WarnAmber)
        End If
        Finally
            _loadingProfiles = False
        End Try
        If _profiles.RowCount > 0 Then SelectProfile(If(selectedName.Length > 0, selectedName, Convert.ToString(_profiles.Rows(0).Cells(0).Value)))
    End Sub

    Private Sub LoadSelectedProfile()
        If _loadingProfiles Then Return
        If _profiles.CurrentRow Is Nothing Then Return
        Dim name = Convert.ToString(_profiles.CurrentRow.Cells(0).Value, Inv)
        Dim profile = SnmpCredentialStore.ListProfiles().FirstOrDefault(Function(item) item.Name = name)
        If profile Is Nothing Then Return
        _profileName.Text = profile.Name
        _editingProfile = profile.Name
        _profileName.ReadOnly = True
        _loadingProfile = True
        _profileVersion.SelectedItem = profile.Version
        _v3User.Text = profile.UserName
        _authProtocol.SelectedItem = profile.AuthProtocol
        _privProtocol.SelectedItem = profile.PrivProtocol
        _securityMode.SelectedItem = profile.SecurityMode
        _profilePort.Value = profile.Port
        _profileTimeout.Value = profile.TimeoutMilliseconds
        _profileRetries.Value = profile.Retries
        _loadingProfile = False
        _showSecrets.Checked = False
        _community.Text = String.Empty
        _confirmCommunity.Text = String.Empty
        _authSecret.Text = String.Empty
        _privSecret.Text = String.Empty
        _community.PlaceholderText = If(profile.HasCommunity, "Saved community - leave blank to keep", "Enter access community")
        _confirmCommunity.PlaceholderText = "Confirm only when replacing"
        _authSecret.PlaceholderText = If(profile.HasAuthSecret, "Saved password - leave blank to keep", "Enter auth password")
        _privSecret.PlaceholderText = If(profile.HasPrivSecret, "Saved password - leave blank to keep", "Enter privacy password")
        Dim users = UpsSettingsStore.Current.Devices.Where(Function(item) String.Equals(item.CredentialProfile, name, StringComparison.OrdinalIgnoreCase)).ToList()
        Dim device = SelectedCredentialDevice()
        Dim assigned = device IsNot Nothing AndAlso String.Equals(device.CredentialProfile, name, StringComparison.OrdinalIgnoreCase)
        Dim savedSecrets = If(profile.Version = "v3",
            "Auth password: " & If(profile.HasAuthSecret, "saved", "not set") & " / Privacy password: " & If(profile.HasPrivSecret, "saved", "not set"),
            "Community: " & If(profile.HasCommunity, "saved (hidden)", "not set"))
        _profileSummary.Text = savedSecrets & "   |   " & If(assigned, "Assigned profile", "Unassigned profile") &
            "   |   " & users.Count & " UPS use this profile." &
            If(users.Count > 1, " Saving changes affects all of them.", "")
        If assigned Then
            Dim effective = SnmpCredentialStore.EffectiveDevice(device)
            _profileSummary.Text &= Environment.NewLine & "Monitoring: UDP " & effective.Port & " / " & effective.TimeoutMilliseconds &
                " ms / " & effective.Retries & " retries (" & If(device.UseProfileTransport, "profile transport", "device transport") & ")."
        End If
        UpdateCredentialFields()
    End Sub

    Private Sub ClearProfileForm(Optional focusName As Boolean = True)
        _profiles.ClearSelection()
        _editingProfile = String.Empty
        _profileName.ReadOnly = False
        _profileName.Text = String.Empty
        _profileVersion.SelectedItem = "v2c"
        _community.Text = String.Empty
        _confirmCommunity.Text = String.Empty
        _v3User.Text = String.Empty
        _authSecret.Text = String.Empty
        _privSecret.Text = String.Empty
        _showSecrets.Checked = False
        _community.PlaceholderText = "Enter access community"
        _confirmCommunity.PlaceholderText = "Confirm new community"
        _authSecret.PlaceholderText = "Enter auth password"
        _privSecret.PlaceholderText = "Enter privacy password"
        _profileSummary.Text = "New profile - save, then assign to the intended UPS."
        _profilePort.Value = 161
        _profileTimeout.Value = 2000
        _profileRetries.Value = 1
        If focusName Then _profileName.Focus()
    End Sub

    Private Sub OnSaveProfile(sender As Object, e As EventArgs)
        Try
            If String.IsNullOrEmpty(_editingProfile) AndAlso SnmpCredentialStore.ListProfiles().Any(
                Function(item) String.Equals(item.Name, _profileName.Text.Trim(), StringComparison.OrdinalIgnoreCase)) Then
                Throw New InvalidDataException("Profile already exists. Select its row in Saved profiles to edit it.")
            End If
            If Convert.ToString(_profileVersion.SelectedItem) <> "v3" AndAlso
               Not String.Equals(_community.Text, _confirmCommunity.Text, StringComparison.Ordinal) Then
                Throw New InvalidDataException("Enter and confirm the same approved access community. Saved communities are never displayed.")
            End If
            Dim affected = SnmpCredentialStore.ConfirmationUsers(_profileName.Text.Trim())
            If Not ConfirmWorkingChange(affected) Then Return
            SnmpCredentialStore.Save(_profileName.Text, Convert.ToString(_profileVersion.SelectedItem), If(_community.Text.Length = 0, Nothing, _community.Text),
                                     _v3User.Text, Convert.ToString(_authProtocol.SelectedItem), If(_authSecret.Text.Length = 0, Nothing, _authSecret.Text),
                                     Convert.ToString(_privProtocol.SelectedItem), If(_privSecret.Text.Length = 0, Nothing, _privSecret.Text), _session,
                                     CInt(_profilePort.Value), CInt(_profileTimeout.Value), CInt(_profileRetries.Value), affected.Count > 0)
            _editingProfile = _profileName.Text.Trim()
            _profileName.ReadOnly = True
            _community.Text = String.Empty
            _confirmCommunity.Text = String.Empty
            _authSecret.Text = String.Empty
            _privSecret.Text = String.Empty
            MaskEnteredSecrets()
            ShowMessage(_credentialMessage, "Profile '" & _profileName.Text.Trim() & "' saved (encrypted) and audited. Card settings were NOT changed. Assign to the intended UPS, then test.", UpsAtsView.OkGreen)
        Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
                                   TypeOf ex Is IOException OrElse TypeOf ex Is System.Security.Cryptography.CryptographicException
            ShowMessage(_credentialMessage, ex.Message, UpsAtsView.AlarmRed)
        End Try
    End Sub

    Private Sub OnDuplicateProfile(sender As Object, e As EventArgs)
        Dim source = _editingProfile
        If String.IsNullOrEmpty(source) Then
            ShowMessage(_credentialMessage, "Select a saved profile first.", UpsAtsView.WarnAmber)
            Return
        End If
        Using dialog As New Form With {.Text = "Duplicate profile (secrets remain hidden)", .Size = New Size(420, 160), .StartPosition = FormStartPosition.CenterParent}
            Dim name As New TextBox With {.Dock = DockStyle.Top, .MaxLength = 40, .Text = source & "-copy"}
            Dim save As New Button With {.Text = "Duplicate", .Dock = DockStyle.Bottom, .DialogResult = DialogResult.OK}
            dialog.Controls.Add(name)
            dialog.Controls.Add(save)
            dialog.AcceptButton = save
            If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
            Try
                SnmpCredentialStore.Duplicate(source, name.Text, _session)
                ShowMessage(_credentialMessage, "Profile duplicated without displaying secrets.", UpsAtsView.OkGreen)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
                ShowMessage(_credentialMessage, ex.Message, UpsAtsView.AlarmRed)
            End Try
        End Using
    End Sub

    Private Async Sub OnTestProfile(sender As Object, e As EventArgs)
        If Not _canEdit Then
            ShowMessage(_credentialMessage, "Only an authenticated administrator can authorize a credential profile test.", UpsAtsView.AlarmRed)
            Return
        End If
        If _testCancellation IsNot Nothing OrElse _scanCancellation IsNot Nothing Then
            ShowMessage(_credentialMessage, "Wait for the current network test to finish or cancel it first.", UpsAtsView.WarnAmber)
            Return
        End If
        If SelectedCredentialDevice() Is Nothing OrElse String.IsNullOrEmpty(_editingProfile) Then
            ShowMessage(_credentialMessage, "Save a profile and select a registered UPS first.", UpsAtsView.WarnAmber)
            Return
        End If
        Dim device = SelectedCredentialDevice()
        If device Is Nothing Then Return
        If MessageBox.Show(Me, "Test saved profile '" & _editingProfile & "' against " & device.DisplayName & " (" & device.IpAddress &
                           ")? No assignment will change; unsaved editor values are not used. Assigned profiles use the saved device transport; other profiles use profile transport.", "Authorize profile test",
                           MessageBoxButtons.OKCancel, MessageBoxIcon.Question) <> DialogResult.OK Then Return
        Dim target = device.Clone()
        Dim assigned = String.Equals(device.CredentialProfile, _editingProfile, StringComparison.OrdinalIgnoreCase)
        If Not assigned Then target.Id = String.Empty
        target.CredentialProfile = _editingProfile
        If Not assigned Then target.UseProfileTransport = True
        Dim testedProfile = _editingProfile
        Dim button = TryCast(sender, Button)
        If button IsNot Nothing Then button.Enabled = False
        Dim cancellation As New CancellationTokenSource()
        _testCancellation = cancellation
        ShowMessage(_credentialMessage, "Testing saved profile on " & device.IpAddress & " (read only)...", UiTheme.Blue)
        Try
            Dim testStamp = SnmpCredentialStore.ConnectionStamp(SnmpCredentialStore.EffectiveDevice(target))
            Dim evidence = Await Task.Run(Function()
                Dim test = UpsConnectionDiagnostics.TestDevice(target, cancellation.Token)
                cancellation.Token.ThrowIfCancellationRequested()
                Dim poll = If(test.IsConnected, UpsAcquisitionService.PollDevice(target, 0, Nothing), Nothing)
                Return (Test:=test, Poll:=poll)
            End Function)
            If IsDisposed Then Return
            Dim verified = evidence.Test.IsConnected AndAlso evidence.Test.SysName.Length > 0 AndAlso evidence.Test.SysObjectId.Length > 0 AndAlso
                evidence.Test.SupportsUpsMib AndAlso evidence.Test.MappingVerified AndAlso UpsCredentialConfiguration.HasElectricalRead(evidence.Poll)
            Dim settingsUnchanged = testStamp = SnmpCredentialStore.ConnectionStamp(SnmpCredentialStore.EffectiveDevice(target))
            Dim matchesC04 = device.Id <> "UPS-C04" OrElse (evidence.Test.SysName = "UPS - 11" AndAlso evidence.Test.SysObjectId = "1.3.6.1.4.1.476.1.42")
            verified = verified AndAlso matchesC04 AndAlso settingsUnchanged
            ShowMessage(_credentialMessage, evidence.Test.Summary & Environment.NewLine &
                "Identity: " & evidence.Test.SysName & " | " & evidence.Test.SysObjectId &
                " | Electrical read: " & If(evidence.Poll IsNot Nothing AndAlso evidence.Poll.IsGood, "Good", "not verified") &
                If(Not matchesC04 AndAlso evidence.Test.SysName.Length > 0 AndAlso evidence.Test.SysObjectId.Length > 0,
                   " | Prior C04 identity does not match; verify physical card.", "") &
                If(Not settingsUnchanged, " | Profile changed during test; rerun.", "") &
                " | " & If(verified, "Read-only connection verified.", "Connection acceptance pending.") &
                " Historian must be verified separately.", If(verified, UpsAtsView.OkGreen, UpsAtsView.WarnAmber))
            Dim current = FindDevice(device.Id)
            If verified AndAlso current IsNot Nothing AndAlso current.Enabled AndAlso
               current.IpAddress = device.IpAddress AndAlso
               String.Equals(current.CredentialProfile, testedProfile, StringComparison.OrdinalIgnoreCase) AndAlso
               testStamp = SnmpCredentialStore.ConnectionStamp(SnmpCredentialStore.EffectiveDevice(current)) AndAlso
               UpsSettingsStore.Current.AcquisitionEnabled AndAlso UpsAcquisitionService.Current IsNot Nothing AndAlso UpsAcquisitionService.Current.IsRunning Then
                UpsAcquisitionService.Current.PollNow(device.Id)
            End If
        Catch ex As OperationCanceledException
            If Not IsDisposed Then ShowMessage(_credentialMessage, "Test cancelled; connection not verified.", UpsAtsView.WarnAmber)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
            If Not IsDisposed Then ShowMessage(_credentialMessage, ex.Message, UpsAtsView.AlarmRed)
        Finally
            cancellation.Dispose()
            _testCancellation = Nothing
            If Not IsDisposed Then
                If button IsNot Nothing Then button.Enabled = _canEdit
                Try
                    RefreshCardOverview()
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
                    ShowMessage(_credentialMessage, ex.Message, UiTheme.Red)
                End Try
            End If
        End Try
    End Sub

    Private Sub OnDeleteProfile(sender As Object, e As EventArgs)
        Dim name = _profileName.Text.Trim()
        If name.Length = 0 Then Return
        Dim users = UpsSettingsStore.Current.Devices.Where(Function(item) String.Equals(item.CredentialProfile, name, StringComparison.OrdinalIgnoreCase)).Select(Function(item) item.Id).ToList()
        users.AddRange(_registry.Rows.Cast(Of DataGridViewRow)().Where(
            Function(row) String.Equals(Convert.ToString(row.Cells("Profile").Value), name, StringComparison.OrdinalIgnoreCase)).
            Select(Function(row) Convert.ToString(row.Cells("Id").Value)).Except(users, StringComparer.OrdinalIgnoreCase))
        If users.Count > 0 Then
            ShowMessage(_credentialMessage, "Cannot remove an assigned profile. Reassign: " & String.Join(", ", users), UpsAtsView.AlarmRed)
            Return
        End If
        Dim warning = String.Empty
        If MessageBox.Show(Me, "Delete credential profile '" & name & "'?" & warning, "Delete profile", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) <> DialogResult.OK Then Return
        Try
            SnmpCredentialStore.Delete(name, _session)
            ClearProfileForm()
            ShowMessage(_credentialMessage, "Profile '" & name & "' deleted.", UpsAtsView.WarnAmber)
        Catch ex As Exception When TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is IOException
            ShowMessage(_credentialMessage, ex.Message, UpsAtsView.AlarmRed)
        End Try
    End Sub

    Private Sub OnCredentialsChanged(sender As Object, e As EventArgs)
        If IsDisposed OrElse Not IsHandleCreated Then Return
        BeginInvoke(Sub()
                        If IsDisposed Then Return
                        Try
                            LoadProfiles()
                            RefreshComboItems()
                            RefreshCardOverview()
                        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
                            ShowMessage(_credentialMessage, ex.Message, UpsAtsView.AlarmRed)
                        End Try
                    End Sub)
    End Sub

    ' ---------- Discovery ----------
    Private Function BuildDiscoveryTab() As TabPage
        Dim page = NewTab("Discovery")
        StyleGrid(_scanResults, False)
        For Each column In {"IP address", "Result", "SNMP version", "Round trip", "sysName", "Manufacturer / model", "UPS-MIB", "sysObjectID", "sysDescr", "Registered as", "Detail"}
            _scanResults.Columns.Add(column.Replace(" ", "").Replace("/", "").Replace("-", ""), column)
        Next
        _scanResults.Columns(10).FillWeight = 180
        _scanResults.Columns(8).FillWeight = 160
        _scanRange.Enabled = _canEdit
        AddHandler _scanRange.CheckedChanged, Sub(sender, e) _scanButton.Text = If(_scanRange.Checked, "Scan IP range", "Scan Registered UPS")
        _rangeFrom.Enabled = _canEdit
        _rangeTo.Enabled = _canEdit
        AddHandler _scanButton.Click, AddressOf OnScan
        _scanButton.Size = New Size(150, 32)
        Dim cancel = SmallButton("Cancel", False, Sub(sender, e) If _scanCancellation IsNot Nothing Then _scanCancellation.Cancel(), False)
        Dim addSelected = SmallButton("Add selected to registry", False, AddressOf OnAddDiscovered)
        Dim profileLabel = UiTheme.Label("Credential", New Font("Segoe UI", 9.0F), UiTheme.MutedInk)
        profileLabel.Margin = New Padding(10, 9, 4, 0)
        Dim rangeDash = UiTheme.Label("–", New Font("Segoe UI", 9.0F), UiTheme.MutedInk)
        rangeDash.Margin = New Padding(2, 7, 2, 0)
        _scanRegistered.Margin = New Padding(0, 7, 14, 0)
        _scanRange.Margin = New Padding(0, 7, 4, 0)
        _rangeFrom.Margin = New Padding(0, 5, 0, 0)
        _rangeTo.Margin = New Padding(0, 5, 0, 0)
        _scanProfile.Margin = New Padding(0, 5, 10, 0)
        Dim modeBar = Toolbar(_scanRegistered, _scanRange, _rangeFrom, rangeDash, _rangeTo, profileLabel, _scanProfile)
        Dim updateRange = Sub()
                              _rangeFrom.Enabled = _canEdit AndAlso _scanRange.Checked
                              _rangeTo.Enabled = _canEdit AndAlso _scanRange.Checked
                              _scanProfile.Enabled = _canEdit AndAlso _scanRange.Checked
                          End Sub
        AddHandler _scanRange.CheckedChanged, Sub(sender, e) updateRange()
        updateRange()
        _scanMessage.Width = 520
        _scanMessage.Height = 30
        _scanMessage.TextAlign = ContentAlignment.MiddleLeft
        page.Controls.Add(_scanResults)
        _scanMessage.Dock = DockStyle.Top
        _scanMessage.Height = 42
        page.Controls.Add(_scanMessage)
        page.Controls.Add(Toolbar(_scanButton, cancel, addSelected))
        page.Controls.Add(modeBar)
        page.Controls.Add(Toolbar(
            SmallButton("Test Selected", False, AddressOf OnTestDiscoverySelected, False),
            SmallButton("Discover OIDs", False, AddressOf OnDiscoveryOids, False),
            SmallButton("Compare with Known-Good", False, AddressOf OnDiscoveryCompare, False)))
        ReadableColumns(_scanResults, 135, 155, 80, 100, 150, 170, 80, 180, 260, 120, 300)
        _scanButton.Text = "Scan Registered UPS"
        ShowMessage(_scanMessage, "Discovery sends only SNMP GET for the system group and UPS-MIB identity (no SET, no walk of other MIBs).", UiTheme.MutedInk)
        Return page
    End Function

    Private Async Sub OnScan(sender As Object, e As EventArgs)
        If _scanCancellation IsNot Nothing OrElse _testCancellation IsNot Nothing Then Return
        If _scanRegistered.Checked Then
            OpenDiagnosticsAndTest(False)
            Return
        End If
        Dim targets As New List(Of (Ip As String, Port As Integer, Profile As String))()
        Dim selectedProfile = Convert.ToString(_scanProfile.SelectedItem)
        If _scanRegistered.Checked Then
            For Each device In UpsSettingsStore.Current.Devices
                targets.Add((device.IpAddress, device.Port, device.CredentialProfile))
            Next
        Else
            If Not _canEdit Then Return
            Dim first As IPAddress = Nothing
            Dim last As IPAddress = Nothing
            If Not IPAddress.TryParse(_rangeFrom.Text.Trim(), first) OrElse Not IPAddress.TryParse(_rangeTo.Text.Trim(), last) OrElse
               first.AddressFamily <> Net.Sockets.AddressFamily.InterNetwork OrElse last.AddressFamily <> Net.Sockets.AddressFamily.InterNetwork Then
                ShowMessage(_scanMessage, "Enter a valid IPv4 range.", UpsAtsView.AlarmRed)
                Return
            End If
            Dim a = first.GetAddressBytes()
            Dim b = last.GetAddressBytes()
            If a(0) <> b(0) OrElse a(1) <> b(1) OrElse a(2) <> b(2) OrElse b(3) < a(3) Then
                ShowMessage(_scanMessage, "The range must stay inside one /24 network and end after it starts.", UpsAtsView.AlarmRed)
                Return
            End If
            Dim count = b(3) - a(3) + 1
            If count > 254 OrElse a(3) = 0 OrElse b(3) = 255 Then
                ShowMessage(_scanMessage, "Select at most 254 host addresses, excluding .0 and .255.", UpsAtsView.AlarmRed)
                Return
            End If
            If MessageBox.Show(Me, "Send read-only SNMP GET requests to " & count & " addresses (" & first.ToString() & " – " & last.ToString() & ")" &
                               " using credential profile '" & selectedProfile & "'?" & Environment.NewLine & Environment.NewLine &
                               "Only scan networks you are authorised to scan. Maximum 4 requests in parallel (or the lower configured limit).",
                               "Confirm network scan", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) <> DialogResult.OK Then Return
            For host = CInt(a(3)) To CInt(b(3))
                targets.Add((a(0) & "." & a(1) & "." & a(2) & "." & host, 161, selectedProfile))
            Next
        End If
        _scanResults.Rows.Clear()
        Dim cancellation As New CancellationTokenSource()
        _scanCancellation = cancellation
        _scanButton.Enabled = False
        Dim started = DateTime.UtcNow
        Dim completed = 0
        ' Results are collected and the grid is filled once at the end: adding rows while the grid repaints
        ' during the scan triggered an internal DataGridView paint failure (red X).
        Dim collected As New List(Of UpsProbeResult)()
        Try
            Dim progress As New Progress(Of UpsProbeResult)(
                Sub(result)
                    Dim apply = Sub()
                                    If IsDisposed Then Return
                                    completed += 1
                                    collected.Add(result)
                                    ShowMessage(_scanMessage, "Scanning… " & completed & " / " & targets.Count, UiTheme.Blue)
                                End Sub

                    If IsDisposed OrElse Not IsHandleCreated Then Return
                    If InvokeRequired Then BeginInvoke(apply) Else apply()
                End Sub)
            ' Registered units are probed with their own credential profile; a range scan uses the selected profile.
            Dim groups = targets.GroupBy(Function(item) (item.Port, item.Profile)).ToList()
            For Each group In groups
                Await UpsAcquisitionService.ScanAsync(group.Select(Function(item) item.Ip).ToList(), group.Key.Port, group.Key.Profile, 1500, progress, cancellation.Token)
            Next
            Await Task.Delay(50)
            If IsDisposed Then Return
            ShowScanResults(collected)
            Dim responders = collected.Where(Function(item) item.Responds).Count()
            ShowMessage(_scanMessage, "Done in " & (DateTime.UtcNow - started).TotalSeconds.ToString("0.0", Inv) & " s  •  " & responders & " of " & targets.Count &
                        " answered SNMP.", If(responders = targets.Count, UpsAtsView.OkGreen, UpsAtsView.WarnAmber))
        Catch ex As OperationCanceledException
            If Not IsDisposed Then
                ShowScanResults(collected)
                ShowMessage(_scanMessage, "Scan cancelled after " & completed & " addresses.", UpsAtsView.WarnAmber)
            End If
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
            If Not IsDisposed Then ShowMessage(_scanMessage, ex.Message, UpsAtsView.AlarmRed)
        Finally
            cancellation.Dispose()
            _scanCancellation = Nothing
            If Not IsDisposed Then _scanButton.Enabled = True
        End Try
    End Sub

    Private Function SelectDiscoveryDevice() As Boolean
        If _scanResults.CurrentRow Is Nothing Then
            ShowMessage(_scanMessage, "Select a registered UPS result first, or use Test Assigned Credentials.", UpsAtsView.WarnAmber)
            Return False
        End If
        Dim result = TryCast(_scanResults.CurrentRow.Tag, UpsProbeResult)
        Dim device = If(result Is Nothing, Nothing, UpsSettingsStore.Current.Devices.FirstOrDefault(Function(item) item.IpAddress = result.IpAddress))
        If device Is Nothing Then
            ShowMessage(_scanMessage, "This result is not registered. Register and assign approved credentials first.", UpsAtsView.WarnAmber)
            Return False
        End If
        _selectedDiagnosticId = device.Id
        _tabs.SelectedIndex = 3
        Return True
    End Function

    Private Sub OnTestDiscoverySelected(sender As Object, e As EventArgs)
        If SelectDiscoveryDevice() Then RunConnectionTests(SelectedDiagnosticDevices())
    End Sub

    Private Sub OnDiscoveryOids(sender As Object, e As EventArgs)
        If SelectDiscoveryDevice() Then OnDiscoverOids(sender, e)
    End Sub

    Private Sub OnDiscoveryCompare(sender As Object, e As EventArgs)
        If SelectDiscoveryDevice() Then OnCompareKnownGood(sender, e)
    End Sub

    Private Sub AddScanRow(result As UpsProbeResult, registry As IReadOnlyList(Of UpsDevice))
        Dim registered = registry.FirstOrDefault(Function(item) item.IpAddress = result.IpAddress)
        Dim registeredText = If(registered Is Nothing, "not registered", registered.Id & " (" & registered.Label & ")")
        Dim index = _scanResults.Rows.Add(If(result.IpAddress, ""), If(result.Responds, "SNMP OK", UpsQualityText.Describe(result.Quality)),
                                          If(result.Responds, result.ProtocolVersion, "—"),
                                          If(UpsAtsView.Fmt(result.RoundTripMilliseconds, "0", " ms"), ""), If(result.SysName, ""),
                                          (result.Manufacturer & " " & result.Model).Trim(), If(result.Responds, If(result.SupportsUpsMib, "Yes", "No"), "—"),
                                          If(result.SysObjectId, ""), If(result.SysDescr, ""), registeredText, If(result.Detail, ""))
        Dim row = _scanResults.Rows(index)
        row.Tag = result
        row.Cells(1).Style.ForeColor = If(result.Responds, UpsAtsView.OkGreen, UpsAtsView.AlarmRed)
        If result.Responds AndAlso registered Is Nothing Then row.Cells(9).Style.ForeColor = UiTheme.Blue
    End Sub

    Private Sub ShowScanResults(collected As IEnumerable(Of UpsProbeResult))
        Dim results = collected.Where(Function(item) item IsNot Nothing).OrderBy(
            Function(item)
                Dim address As IPAddress = Nothing
                If Not IPAddress.TryParse(item.IpAddress, address) Then Return Long.MaxValue
                Dim bytes = address.GetAddressBytes()
                Return (CLng(bytes(2)) << 8) Or bytes(3)
            End Function).ToList()
        Dim registry = UpsSettingsStore.Current.Devices
        _scanResults.SuspendLayout()
        _scanResults.Rows.Clear()
        For Each result In results
            AddScanRow(result, registry)
        Next
        _scanResults.ResumeLayout()
        _scanResults.ClearSelection()
        _scanResults.CurrentCell = Nothing
    End Sub

    Private Sub OnAddDiscovered(sender As Object, e As EventArgs)
        If _scanResults.CurrentRow Is Nothing Then Return
        Dim result = TryCast(_scanResults.CurrentRow.Tag, UpsProbeResult)
        If result Is Nothing OrElse Not result.Responds Then
            ShowMessage(_scanMessage, "Select a unit that answered SNMP.", UpsAtsView.AlarmRed)
            Return
        End If
        Dim exists = _registry.Rows.Cast(Of DataGridViewRow)().Any(Function(row) Convert.ToString(row.Cells("Ip").Value).Trim() = result.IpAddress)
        If exists Then
            ShowMessage(_scanMessage, result.IpAddress & " is already in the registry.", UpsAtsView.WarnAmber)
            Return
        End If
        _tabs.SelectedIndex = 0
        AddUnitRow(New UpsDevice With {
            .Id = NextId(), .Label = If(result.SysName.Length > 0, result.SysName, result.IpAddress), .AtsBank = 1, .IpAddress = result.IpAddress,
            .CredentialProfile = Convert.ToString(_scanProfile.SelectedItem), .Model = (result.Manufacturer & " " & result.Model).Trim(),
            .RatedKva = 500, .Verification = UpsVerification.Provisional,
            .Notes = "Discovered " & DateTime.Now.ToString("dd MMM yyyy", Inv) & "; sysName '" & result.SysName & "'. Confirm ATS bank, label and rating."})
    End Sub

    ' ---------- Diagnostics & OID map ----------
    Private Function BuildDiagnosticsTab() As TabPage
        Dim page = NewTab("Diagnostics")
        StyleGrid(_diagnostics, False)
        For Each column In {"Id|ID|58", "Label|Label|45", "Ip|IP|75", "Registered|Registered|50", "Identity|Identity verified|65",
                            "Version|Responding SNMP|75", "Connection|Current SNMP connection|90", "Mapping|Measurement mapping|105", "DataQuality|Electrical data quality|80",
                            "LastGood|Last successful poll|80", "RoundTrip|Round trip|42", "Failures|Fails|30",
                            "LastFailure|Last failure reason|150", "Classification|Classification|140", "LastTest|Last connection test|115"}
            Dim parts = column.Split("|"c)
            _diagnostics.Columns.Add(New DataGridViewTextBoxColumn With {.Name = parts(0), .HeaderText = parts(1), .FillWeight = Single.Parse(parts(2), Inv)})
        Next
        _diagnostics.Columns.Add("Ats", "ATS")
        _diagnostics.Columns.Add("Profile", "Assigned profile")
        _diagnostics.Columns.Add("Configured", "Profile configured")
        For Each columnName In {"Label", "Registered", "Identity", "Failures", "LastFailure", "Classification", "LastTest", "Configured"}
            _diagnostics.Columns(columnName).Visible = False
        Next
        ReadableColumns(_diagnostics, 100, 90, 135, 80, 100, 105, 230, 200, 150, 155, 100, 60, 200, 200, 180, 55, 130, 80)
        AddHandler _diagnostics.SelectionChanged, Sub(sender, e)
                                                      If _diagnostics.CurrentRow Is Nothing OrElse _refreshingDiagnostics Then Return
                                                      _selectedDiagnosticId = Convert.ToString(_diagnostics.CurrentRow.Cells("Id").Value, Inv)
                                                      UpdateDiagnosticDetail()
                                                  End Sub
        StyleGrid(_oidGrid, False)
        For Each column In {"Measurement", "OID (RFC 1628)", "Unit", "Scale", "Per phase", "Valid range"}
            _oidGrid.Columns.Add(column.Replace(" ", "").Replace("(", "").Replace(")", ""), column)
        Next
        _oidGrid.Columns(1).FillWeight = 200
        ReadableColumns(_oidGrid, 180, 240, 65, 70, 70, 110)
        StyleGrid(_auditGrid, False)
        For Each column In {"Key|Measurement|80", "Oid|OID read|150", "Status|Result|120", "Raw|Raw value|70", "Scaled|Scaled value|80"}
            Dim parts = column.Split("|"c)
            _auditGrid.Columns.Add(New DataGridViewTextBoxColumn With {.Name = parts(0), .HeaderText = parts(1), .FillWeight = Single.Parse(parts(2), Inv)})
        Next
        _auditGrid.Visible = False
        ReadableColumns(_auditGrid, 160, 240, 160, 100, 110)

        _detailBox.Dock = DockStyle.Fill
        _detailBox.Multiline = True
        _detailBox.ReadOnly = True
        _detailBox.ScrollBars = ScrollBars.Vertical
        _detailBox.WordWrap = True
        _detailBox.BorderStyle = BorderStyle.None
        _detailBox.BackColor = UiTheme.Surface
        _detailBox.ForeColor = UiTheme.Ink
        _detailBox.Font = New Font("Consolas", 9.0F)
        Dim detailHeader = UiTheme.Label("SELECTED UPS - connection evidence and next steps",
                                         New Font("Segoe UI", 8.0F, FontStyle.Bold), UiTheme.MutedInk, False)
        detailHeader.Dock = DockStyle.Top
        detailHeader.Height = 22
        _oidHeader.Dock = DockStyle.Top
        _oidHeader.Height = 22

        Dim lower As New SplitContainer With {.Dock = DockStyle.Fill, .Orientation = Orientation.Vertical, .SplitterWidth = 6, .BackColor = UiTheme.Canvas}
        lower.Panel1.Controls.Add(_detailBox)
        lower.Panel1.Controls.Add(detailHeader)
        lower.Panel2.Controls.Add(_oidGrid)
        lower.Panel2.Controls.Add(_auditGrid)
        lower.Panel2.Controls.Add(_oidHeader)
        Dim split As New SplitContainer With {.Dock = DockStyle.Fill, .Orientation = Orientation.Horizontal, .SplitterWidth = 6, .BackColor = UiTheme.Canvas}
        split.Panel1.Controls.Add(_diagnostics)
        split.Panel2.Controls.Add(lower)
        page.Controls.Add(split)
        AddHandler split.SizeChanged, Sub(sender, e)
                                          If split.Height > 300 Then split.SplitterDistance = Math.Max(150, CInt(split.Height * 0.52))
                                      End Sub
        AddHandler lower.SizeChanged, Sub(sender, e)
                                          If lower.Width > 400 Then lower.SplitterDistance = Math.Max(200, CInt(lower.Width * 0.5))
                                      End Sub

        _diagnosticsMessage.Dock = DockStyle.Top
        _diagnosticsMessage.Height = 24
        page.Controls.Add(_diagnosticsMessage)
        _diagnosticButtons.AddRange({
            SmallButton("Test Selected", True, Sub(sender, e) RunConnectionTests(SelectedDiagnosticDevices()), False),
            SmallButton("Test All Registered", False, Sub(sender, e) RunConnectionTests(UpsSettingsStore.Current.Devices.ToList()), False),
            SmallButton("Recheck Failed Only", False, Sub(sender, e) RunConnectionTests(FailedDevices()), False)})
        _cancelTestsButton = SmallButton("Cancel", False, Sub(sender, e)
                                                              If _testCancellation IsNot Nothing Then _testCancellation.Cancel()
                                                              If _webCancellation IsNot Nothing Then _webCancellation.Cancel()
                                                          End Sub, False)
        _cancelTestsButton.Enabled = False
        Dim tools As New ContextMenuStrip()
        tools.Items.Add("Discover Supported OIDs", Nothing, AddressOf OnDiscoverOids)
        tools.Items.Add("Compare With Known-Good", Nothing, AddressOf OnCompareKnownGood)
        tools.Items.Add(New ToolStripSeparator())
        tools.Items.Add("Copy Diagnostics", Nothing, AddressOf OnCopyDiagnostics)
        tools.Items.Add("Export Connection Report", Nothing, AddressOf OnExportReport)
        Dim detailedColumns As New ToolStripMenuItem("Show detailed columns") With {.CheckOnClick = True}
        AddHandler detailedColumns.CheckedChanged, Sub(sender, e)
                                                       For Each columnName In {"Label", "Registered", "Identity", "Failures", "LastFailure", "Classification", "LastTest", "Configured"}
                                                           _diagnostics.Columns(columnName).Visible = detailedColumns.Checked
                                                       Next
                                                   End Sub
        tools.Items.Add(detailedColumns)
        Dim recovery = tools.Items.Add("C04 recovery procedure", Nothing, AddressOf OnRestoreConnection)
        AddHandler tools.Opening, Sub(sender, e) recovery.Visible = _canEdit AndAlso _selectedDiagnosticId = "UPS-C04"
        Dim toolsButton = SmallButton("More Tools", False, Sub(sender, e)
                                                              Dim source = DirectCast(sender, Button)
                                                              tools.Show(source, New Point(0, source.Height))
                                                          End Sub, False)
        toolsButton.ContextMenuStrip = tools
        _diagnosticButtons.Add(toolsButton)
        Dim items As New List(Of Control)(_diagnosticButtons)
        items.Insert(3, _cancelTestsButton)
        page.Controls.Add(Toolbar(items.ToArray()))
        _diagnosticsSummary.Dock = DockStyle.Top
        _diagnosticsSummary.Height = 26
        page.Controls.Add(_diagnosticsSummary)
        ShowOidMapHeader()
        Return page
    End Function

    Private Sub ShowOidMapHeader()
        _oidHeader.Text = "OID MAP – RFC1628 plus per-card Vertiv label/unit-verified measurements. RFC voltage reference is unspecified; NXL repeated phase totals are rejected. Discover Supported OIDs audits the selected card."
        _oidHeader.ForeColor = UiTheme.MutedInk
        _auditGrid.Visible = False
        _oidGrid.Visible = True
    End Sub

    Private Sub FillOidMap()
        _oidGrid.Rows.Clear()
        For Each definition In UpsOidMap.Definitions
            Dim range = If(definition.MinValid = Double.MinValue, "", definition.MinValid.ToString("0.##", Inv) & " … " &
                        If(definition.MaxValid = Double.MaxValue, "∞", definition.MaxValid.ToString("0.##", Inv)))
            _oidGrid.Rows.Add(definition.Description, definition.Oid & If(definition.PerPhase, "<line>", ""), definition.Unit,
                              "× " & definition.Scale.ToString("0.###", Inv), If(definition.PerPhase, "yes", ""), range)
        Next
        For Each binding In UpsMeasurementMap.Candidates
            Dim definition = binding.Definition
            _oidGrid.Rows.Add("Vertiv: " & binding.Label, definition.Oid, definition.Unit,
                              "× " & definition.Scale.ToString("0.###", Inv), If(definition.PerPhase, "yes", ""),
                              "Requires matching card label + " & If(binding.CardUnit.Length = 0, "dimensionless", binding.CardUnit))
        Next
        _oidGrid.Rows.Add("Apparent power", "Card system VA preferred; otherwise Σ verified L-N V × I / 1000", "kVA", "", "", "")
        _oidGrid.Rows.Add("System power factor (DERIVED)", "System kW ÷ system kVA (matched source); never clamped", "", "", "", "0 … 1; outside rejected")
        AddHandler _oidGrid.VisibleChanged, Sub(sender, e) _oidGrid.ClearSelection()
    End Sub

    Private Function FindDevice(id As String) As UpsDevice
        Return UpsSettingsStore.Current.Devices.FirstOrDefault(Function(item) String.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase))
    End Function

    Private Function SelectedDiagnosticDevices() As IReadOnlyList(Of UpsDevice)
        Dim device = FindDevice(_selectedDiagnosticId)
        If device Is Nothing Then
            ShowMessage(_diagnosticsMessage, "Select a unit in the table first.", UpsAtsView.WarnAmber)
            Return Array.Empty(Of UpsDevice)()
        End If
        Return {device}
    End Function

    ' Units whose last connection test failed, or untested units whose live poll is not Good.
    Private Function FailedDevices() As IReadOnlyList(Of UpsDevice)
        Dim service = UpsAcquisitionService.Current
        Dim snapshots = If(service Is Nothing, Nothing, service.GetSnapshots())
        Return UpsSettingsStore.Current.Devices.Where(Function(device)
                                                          Dim test = UpsConnectionDiagnostics.LastTest(device.Id)
                                                          If test IsNot Nothing Then Return Not test.IsConnected
                                                          Dim snapshot As UpsSnapshot = Nothing
                                                          Return snapshots Is Nothing OrElse Not snapshots.TryGetValue(device.Id, snapshot) OrElse Not snapshot.IsGood
                                                      End Function).ToList()
    End Function

    Private Sub SetTestsRunning(running As Boolean)
        For Each item In _diagnosticButtons
            item.Enabled = Not running
        Next
        _cancelTestsButton.Enabled = running
    End Sub

    Private Async Sub RunConnectionTests(devices As IReadOnlyList(Of UpsDevice))
        If devices.Count = 0 Then
            ShowMessage(_diagnosticsMessage, "No devices require testing.", UiTheme.MutedInk)
            Return
        End If
        If _testCancellation IsNot Nothing OrElse _scanCancellation IsNot Nothing Then
            ShowMessage(_diagnosticsMessage, "A network test is already running. Wait or cancel it first.", UpsAtsView.WarnAmber)
            Return
        End If
        _testCancellation = New CancellationTokenSource()
        SetTestsRunning(True)
        Dim total = devices.Count
        Dim done = 0
        ShowMessage(_diagnosticsMessage, "Testing " & total & " unit(s), " & UpsConnectionDiagnostics.MaxParallelTests & " in parallel (read-only)…", UiTheme.Blue)
        Dim progress As New Progress(Of UpsConnectionTest)(Sub(result)
                                                                done += 1
                                                                If IsDisposed Then Return
                                                                ShowMessage(_diagnosticsMessage, "Tested " & done & "/" & total & "  •  last: " & result.DeviceId & " " &
                                                                            result.IpAddress & " – " & UpsConnectionDiagnostics.Describe(result.Finding), UiTheme.Blue)
                                                                RefreshDiagnostics()
                                                            End Sub)
        Try
            Dim results = Await UpsConnectionDiagnostics.TestManyAsync(devices, progress, _testCancellation.Token)
            If IsDisposed Then Return
            Dim connected = results.Where(Function(item) item.IsConnected).Count()
            Dim groups = results.Where(Function(item) Not item.IsConnected).GroupBy(Function(item) item.Finding).
                Select(Function(group) group.Count().ToString(Inv) & " " & UpsConnectionDiagnostics.Describe(group.Key).ToLowerInvariant())
            Dim cancelled = _testCancellation.IsCancellationRequested
            ShowMessage(_diagnosticsMessage, If(cancelled, "Cancelled. ", "") & results.Count & "/" & total & " tested: " & connected & " connected" &
                        If(groups.Any(), ", " & String.Join(", ", groups), "") & ".  Select a unit for evidence and the card checklist.",
                        If(connected = results.Count AndAlso Not cancelled, UpsAtsView.OkGreen, UpsAtsView.WarnAmber))
        Finally
            _testCancellation.Dispose()
            _testCancellation = Nothing
            If Not IsDisposed Then
                SetTestsRunning(False)
                RefreshDiagnostics()
                UpdateDiagnosticDetail(True)
            End If
        End Try
    End Sub

    Private Async Sub OnDiscoverOids(sender As Object, e As EventArgs)
        Dim devices = SelectedDiagnosticDevices()
        If devices.Count = 0 OrElse _testCancellation IsNot Nothing Then Return
        Dim device = devices(0).Clone()
        _testCancellation = New CancellationTokenSource()
        SetTestsRunning(True)
        ShowMessage(_diagnosticsMessage, "Reading every OID of the map from " & device.Id & " " & device.IpAddress & " one by one (read-only GET)…", UiTheme.Blue)
        Try
            Dim token = _testCancellation.Token
            Dim audit = Await Task.Run(Function() UpsConnectionDiagnostics.AuditOids(device, token))
            If IsDisposed Then Return
            ShowMessage(_diagnosticsMessage, device.Id & ": " & UpsConnectionDiagnostics.MappingText(audit) & If(audit.Aborted.Length > 0, " – " & audit.Aborted, ""),
                        If(audit.MappingVerified, UpsAtsView.OkGreen, UpsAtsView.WarnAmber))
        Finally
            _testCancellation.Dispose()
            _testCancellation = Nothing
            If Not IsDisposed Then
                SetTestsRunning(False)
                RefreshDiagnostics()
                UpdateDiagnosticDetail(True)
            End If
        End Try
    End Sub

    ' Tests the selected unit and (if needed) a Verified reference, then shows the side-by-side comparison first.
    Private Async Sub OnCompareKnownGood(sender As Object, e As EventArgs)
        Dim devices = SelectedDiagnosticDevices()
        If devices.Count = 0 OrElse _testCancellation IsNot Nothing Then Return
        Dim targets As New List(Of UpsDevice)(devices)
        Dim registry = UpsSettingsStore.Current.Devices
        If UpsConnectionDiagnostics.KnownGoodReference(registry) Is Nothing Then
            targets.AddRange(registry.Where(Function(item) item.Verification = UpsVerification.Verified AndAlso Not targets.Contains(item)))
        End If
        If UpsConnectionDiagnostics.LastTest(devices(0).Id) IsNot Nothing AndAlso targets.Count = 1 Then
            _compareFirst = True
            UpdateDiagnosticDetail(True)
            Return
        End If
        _compareFirst = True
        RunConnectionTests(targets)
        Await Task.CompletedTask
    End Sub

    Private Sub OnCopyDiagnostics(sender As Object, e As EventArgs)
        Try
            Clipboard.SetText(BuildReport(False))
            ShowMessage(_diagnosticsMessage, "Connection report copied to the clipboard (no credentials included).", UpsAtsView.OkGreen)
        Catch ex As Runtime.InteropServices.ExternalException
            ShowMessage(_diagnosticsMessage, "Clipboard is busy – try again.", UpsAtsView.AlarmRed)
        End Try
    End Sub

    Private Sub OnExportReport(sender As Object, e As EventArgs)
        Using dialog As New SaveFileDialog With {
            .Title = "Export UPS SNMP connection report",
            .Filter = "Markdown report (*.md)|*.md|CSV table (*.csv)|*.csv",
            .FileName = "EOEMS_UPS_ConnectionReport_" & DateTime.Now.ToString("yyyyMMdd_HHmm", Inv),
            .InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)}
            If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
            Try
                Dim csv = dialog.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                File.WriteAllText(dialog.FileName, BuildReport(csv), New System.Text.UTF8Encoding(True))
                ShowMessage(_diagnosticsMessage, "Report written to " & dialog.FileName, UpsAtsView.OkGreen)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
                ShowMessage(_diagnosticsMessage, "Export failed: " & ex.Message, UpsAtsView.AlarmRed)
            End Try
        End Using
    End Sub

    Private Function BuildReport(csv As Boolean) As String
        Dim service = UpsAcquisitionService.Current
        Dim snapshots = If(service Is Nothing, Nothing, service.GetSnapshots())
        Dim devices = UpsSettingsStore.Current.Devices.ToList()
        Return If(csv, UpsConnectionDiagnostics.BuildCsvReport(devices, snapshots), UpsConnectionDiagnostics.BuildMarkdownReport(devices, snapshots))
    End Function

    Private Shared Function ConnectionText(device As UpsDevice, snapshot As UpsSnapshot) As String
        If Not device.Enabled Then Return "Not polled (disabled)"
        If snapshot Is Nothing Then Return "Unknown (no poll yet)"
        Select Case snapshot.Quality
            Case UpsQuality.Good, UpsQuality.NotSupported : Return "SNMP Responding"
            Case UpsQuality.Invalid : Return "Invalid response / data"
            Case UpsQuality.Stale : Return "Unknown (no fresh poll)"
            Case UpsQuality.Timeout, UpsQuality.Offline : Return "SNMP Timeout — Cause Unknown"
            Case UpsQuality.AuthFailure : Return "Rejected (authentication)"
            Case UpsQuality.ConfigError : Return "Configuration error"
            Case UpsQuality.Disabled : Return "Not polled"
            Case Else : Return "Unknown (no poll yet)"
        End Select
    End Function

    Private Sub RefreshDiagnostics()
        If IsDisposed Then Return
        Dim service = UpsAcquisitionService.Current
        Dim historian = UpsHistorian.Current
        If service Is Nothing Then
            ShowMessage(_diagnosticsSummary, "UPS acquisition is not running in this session (offline preview). Connection tests still work.", UpsAtsView.WarnAmber)
        Else
            Dim acquisition = service.GetDiagnostics()
            Dim history = If(historian Is Nothing, Nothing, historian.GetDiagnostics())
            Dim historianText = If(history Is Nothing, "historian not running",
                "historian " & If(history.Enabled, "on", "OFF") & " • last write " &
                If(history.LastWriteUtc.HasValue, history.LastWriteUtc.Value.ToLocalTime().ToString("HH:mm:ss", Inv) & " (" & history.LastRowsVerified & "/" & history.LastRowsWritten & " rows verified)", "—") &
                If(String.IsNullOrEmpty(history.LastError), "", " • " & history.LastError))
            ShowMessage(_diagnosticsSummary,
                "Acquisition " & If(acquisition.Running, "running", "STOPPED") & " • polls OK " & acquisition.PollsSucceeded & " / failed " & acquisition.PollsFailed &
                " • in flight " & acquisition.InFlight & "/" & acquisition.MaxConcurrency & " • " & historianText,
                If(acquisition.Running AndAlso (history Is Nothing OrElse String.IsNullOrEmpty(history.LastError)), UiTheme.MutedInk, UpsAtsView.WarnAmber))
        End If
        Dim snapshots = If(service Is Nothing, New Dictionary(Of String, UpsSnapshot)(), service.GetSnapshots())
        Dim devices = UpsSettingsStore.Current.Devices.OrderBy(Function(item) item.AtsBank).ThenBy(Function(item) item.Label, StringComparer.OrdinalIgnoreCase).ToList()
        Dim profiles As IReadOnlyList(Of SnmpCredentialSummary)
        Try
            profiles = SnmpCredentialStore.ListProfiles()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
            ShowMessage(_diagnosticsMessage, ex.Message, UpsAtsView.AlarmRed)
            Return
        End Try
        _refreshingDiagnostics = True
        Try
            Dim rebuild = _diagnostics.RowCount <> devices.Count OrElse
                          devices.Where(Function(device, index) Not String.Equals(Convert.ToString(_diagnostics.Rows(index).Cells("Id").Value, Inv), device.Id, StringComparison.OrdinalIgnoreCase)).Any()
            If rebuild Then
                _diagnostics.Rows.Clear()
                For Each device In devices
                    _diagnostics.Rows.Add(device.Id)
                Next
            End If
            For index = 0 To devices.Count - 1
                Dim device = devices(index)
                Dim snapshot As UpsSnapshot = Nothing
                snapshots.TryGetValue(device.Id, snapshot)
                Dim quality = If(snapshot Is Nothing, UpsQuality.NoData, snapshot.Quality)
                Dim test = UpsConnectionDiagnostics.LastTest(device.Id)
                Dim audit = UpsConnectionDiagnostics.LastAudit(device.Id)
                Dim connection = ConnectionText(device, snapshot)
                Dim capability = UpsCapabilityProbe.CurrentEvidence(device)
                Dim mapping = If(capability IsNot Nothing AndAlso capability.IdentityAnswered, UpsCapabilityProbe.MappingText(capability),
                                 If(audit IsNot Nothing, UpsConnectionDiagnostics.MappingText(audit),
                                    If(snapshot IsNot Nothing AndAlso snapshot.IsGood, "Not probed (live values present)", "Not probed")))
                Dim version = If(snapshot IsNot Nothing AndAlso snapshot.IsGood AndAlso snapshot.ProtocolVersion.Length > 0, snapshot.ProtocolVersion,
                                 If(test Is Nothing, "—", test.RespondingVersion))
                Dim classification = UpsConnectionDiagnostics.Classification(device, test, snapshot)
                Dim dataQuality = If(snapshot Is Nothing, "NO DATA",
                                     UpsQualityText.Describe(quality) & If(snapshot.IsGood AndAlso snapshot.InvalidFields.Count > 0, " (" & snapshot.InvalidFields.Count & " ignored)", ""))
                Dim lastFailure = If(snapshot Is Nothing OrElse String.IsNullOrEmpty(snapshot.LastFailureReason), "—",
                                     If(snapshot.LastFailureUtc.HasValue, snapshot.LastFailureUtc.Value.ToLocalTime().ToString("HH:mm:ss", Inv) & "  ", "") & snapshot.LastFailureReason)
                Dim lastTest = If(test Is Nothing, "Not tested",
                                  UpsConnectionDiagnostics.Describe(test.Finding) & "  " & test.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", Inv))
                Dim row = _diagnostics.Rows(index)
                Dim profile = profiles.FirstOrDefault(Function(item) String.Equals(item.Name, device.CredentialProfile, StringComparison.OrdinalIgnoreCase))
                Dim configured = profile IsNot Nothing AndAlso profile.IsConfigured
                If Not configured Then connection = "Credentials Not Configured"
                If test IsNot Nothing AndAlso (snapshot Is Nothing OrElse test.TimestampUtc > snapshot.TimestampUtc) Then
                    connection = UpsConnectionDiagnostics.Describe(test.Finding)
                    If Not test.IsConnected Then lastFailure = test.Summary
                End If
                If configured AndAlso (snapshot IsNot Nothing AndAlso snapshot.IsGood OrElse test IsNot Nothing AndAlso test.IsConnected) AndAlso
                   capability IsNot Nothing AndAlso capability.IdentityAnswered Then
                    connection = If(capability.FullyMapped, "Connected and Fully Mapped", "Connected but Partially Mapped")
                End If
                Dim values As Object() = {device.Id, device.Label, device.IpAddress, If(device.Enabled, "Yes", "Yes (polling off)"),
                    UpsQualityText.DescribeVerification(device.Verification), version, connection, mapping, dataQuality,
                    If(snapshot IsNot Nothing AndAlso snapshot.LastGoodUtc.HasValue, snapshot.LastGoodUtc.Value.ToLocalTime().ToString("dd MMM HH:mm:ss", Inv), "never"),
                    UpsAtsView.Fmt(If(snapshot Is Nothing, If(test Is Nothing, Nothing, test.LatencyMilliseconds), snapshot.RoundTripMilliseconds), "0", " ms"),
                    If(snapshot Is Nothing, "", snapshot.ConsecutiveFailures.ToString(Inv)), lastFailure, classification, lastTest,
                    device.AtsBank, device.CredentialProfile, If(configured, "Yes", "No")}
                For column = 0 To values.Length - 1
                    If Not Equals(row.Cells(column).Value, values(column)) Then row.Cells(column).Value = values(column)
                Next
                row.Cells("Connection").Style.ForeColor = If(connection.StartsWith("Connected", StringComparison.Ordinal) OrElse connection = "SNMP Responding", UpsAtsView.OkGreen,
                    If(connection.StartsWith("Not polled", StringComparison.Ordinal) OrElse connection.StartsWith("Unknown", StringComparison.Ordinal), UpsAtsView.WarnAmber, UpsAtsView.AlarmRed))
                row.Cells("DataQuality").Style.ForeColor = UpsAtsView.QualityColor(quality)
                row.Cells("Mapping").ToolTipText = If(capability Is Nothing, mapping,
                    mapping & If(capability.UnavailableParameters.Count = 0, "",
                        Environment.NewLine & "Unavailable: " & String.Join(", ", capability.UnavailableParameters)) &
                    Environment.NewLine & "GOOD describes accepted readings, not support for every optional parameter. Select this UPS for OID details.")
                row.Cells("Identity").Style.ForeColor = If(device.Verification = UpsVerification.Verified, UpsAtsView.OkGreen, UpsAtsView.WarnAmber)
                row.Cells("Mapping").Style.ForeColor = If(capability IsNot Nothing AndAlso capability.FullyMapped, UpsAtsView.OkGreen,
                    If(capability IsNot Nothing AndAlso capability.IdentityAnswered, UpsAtsView.WarnAmber, UiTheme.MutedInk))
                row.Cells("Classification").Style.ForeColor = If(classification.StartsWith("Reachable and", StringComparison.Ordinal), UpsAtsView.OkGreen,
                    If(classification.StartsWith("Reachable", StringComparison.Ordinal) OrElse classification = "Not tested", UpsAtsView.WarnAmber, UpsAtsView.AlarmRed))
                row.Cells("LastTest").Style.ForeColor = If(test Is Nothing, UiTheme.MutedInk, If(test.IsConnected, UpsAtsView.OkGreen, UpsAtsView.AlarmRed))
                Dim selectRow = String.Equals(device.Id, _selectedDiagnosticId, StringComparison.OrdinalIgnoreCase)
                If selectRow AndAlso Not row.Selected Then
                    _diagnostics.CurrentCell = row.Cells(0)
                    row.Selected = True
                End If
            Next
            If String.IsNullOrEmpty(_selectedDiagnosticId) Then _diagnostics.ClearSelection()
        Finally
            _refreshingDiagnostics = False
        End Try
        UpdateDiagnosticDetail()
    End Sub

    Private Sub UpdateDiagnosticDetail(Optional force As Boolean = False)
        If IsDisposed Then Return
        Dim device = FindDevice(_selectedDiagnosticId)
        Dim text As New System.Text.StringBuilder()
        If device Is Nothing Then
            text.AppendLine("Select a unit in the table above.")
            text.AppendLine()
            text.AppendLine("Test Selected / Test All Registered / Recheck Failed Only run a layered, read-only test:")
            text.AppendLine("  1. ICMP ping            – is the host on the network?")
            text.AppendLine("  2. Configured SNMP GET  – the exact request the poller sends (profile, version, port).")
            text.AppendLine("  3. SNMPv3 engine probe  – credential-free; proves whether ANY SNMP agent listens on UDP 161.")
            text.AppendLine("  4. TCP 80 (only if ping fails) – host reachability only; HTTP never proves SNMP.")
            text.AppendLine("Nothing is written to the cards and no credential is guessed.")
        Else
            Dim test = UpsConnectionDiagnostics.LastTest(device.Id)
            Dim service = UpsAcquisitionService.Current
            Dim snapshot = If(service Is Nothing, Nothing, service.GetSnapshot(device.Id))
            text.AppendLine(device.Id & "  " & device.Label & "  •  ATS " & device.AtsBank & "  •  " & device.IpAddress & ":" & device.Port &
                            "  •  profile '" & device.CredentialProfile & "'  •  timeout " & device.TimeoutMilliseconds & " ms × " & (device.Retries + 1))
            text.AppendLine("Identity: " & UpsQualityText.DescribeVerification(device.Verification) & If(device.Notes.Length > 0, " – " & device.Notes, ""))
            text.AppendLine("Live poll: " & ConnectionText(device, snapshot) & If(snapshot Is Nothing, "", " – " & snapshot.QualityDetail))
            If snapshot IsNot Nothing AndAlso snapshot.IsGood Then
                text.AppendLine("CARD ALARMS: " & snapshot.AlarmDetailsStatus)
                For Each alarm In snapshot.CardAlarms
                    text.AppendLine("  " & alarm.StateText & " - " & alarm.Name & " (" & alarm.CardType & " / " & alarm.CardSeverity &
                                    ") - " & alarm.Source & " " & alarm.DescriptionOid)
                Next
            End If
            text.AppendLine()
            If test Is Nothing Then
                text.AppendLine("CONNECTION TEST: not run in this session.")
            Else
                Dim compare = UpsConnectionDiagnostics.Compare(test, UpsConnectionDiagnostics.KnownGoodReference(UpsSettingsStore.Current.Devices))
                If _compareFirst Then
                    text.AppendLine("KNOWN-GOOD COMPARISON")
                    For Each line In compare
                        text.AppendLine(line)
                    Next
                    text.AppendLine()
                End If
                text.AppendLine("CONNECTION TEST " & test.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", Inv) & "  (" & test.ElapsedMilliseconds.ToString("0", Inv) & " ms)")
                text.AppendLine("  Finding     : " & UpsConnectionDiagnostics.Describe(test.Finding))
                text.AppendLine("  Source IP   : " & test.SourceAddress)
                text.AppendLine("  Ping        : " & test.PingReplies & "/" & test.PingAttempts & If(test.PingMilliseconds.HasValue, "  (" & test.PingMilliseconds.Value.ToString("0", Inv) & " ms)", ""))
                If test.HttpPortOpen.HasValue Then text.AppendLine("  TCP 80      : " & If(test.HttpPortOpen.Value, "open (reachability only)", "no answer"))
                text.AppendLine("  SNMP " & test.ProfileVersion.PadRight(7) & ": " & If(test.SnmpResponds, "answered in " & UpsConnectionDiagnostics.FormatMs(test.LatencyMilliseconds), test.SnmpDetail))
                If test.AlternateVersion.Length > 0 Then text.AppendLine("  SNMP " & test.AlternateVersion.PadRight(7) & ": " & test.AlternateVersionResult & "  (same credential, one GET)")
                If Not test.IsConnected Then text.AppendLine("  v3 engine   : " & test.EngineProbe & If(test.EngineId.Length > 0, "  " & test.EngineId, ""))
                If test.SysName.Length > 0 OrElse test.SysObjectId.Length > 0 Then
                    text.AppendLine("  sysName     : " & test.SysName)
                    text.AppendLine("  sysObjectID : " & test.SysObjectId)
                    text.AppendLine("  Identity    : " & (test.Manufacturer & " " & test.Model).Trim() & If(test.SupportsUpsMib, "  (RFC 1628 UPS-MIB present)", "  (no UPS-MIB identity)"))
                End If
                text.AppendLine()
                text.AppendLine(test.Summary)
                If test.PossibleCauses.Count > 0 Then
                    text.AppendLine()
                    text.AppendLine("POSSIBLE CAUSES (not yet distinguished – confirm on the card):")
                    For Each cause In test.PossibleCauses
                        text.AppendLine("  • " & cause)
                    Next
                End If
                If Not test.IsConnected Then
                    If Not _compareFirst Then
                        text.AppendLine()
                        text.AppendLine("KNOWN-GOOD COMPARISON")
                        For Each line In compare
                            text.AppendLine(line)
                        Next
                    End If
                    text.AppendLine()
                    text.AppendLine("CARD CHECKLIST (EOEMS never changes card settings):")
                    For Each item In UpsConnectionDiagnostics.Checklist(test)
                        text.AppendLine("  " & item)
                    Next
                End If
            End If
            text.AppendLine()
            text.AppendLine("CLASSIFICATION : " & UpsConnectionDiagnostics.Classification(device, test, snapshot))
            text.AppendLine("REQUIRED ACTION: " & UpsConnectionDiagnostics.RequiredAction(device, test, snapshot))
            Dim capability = UpsCapabilityProbe.CurrentEvidence(device)
            If capability IsNot Nothing AndAlso capability.IdentityAnswered Then
                text.AppendLine()
                text.AppendLine("CAPABILITY PROBE " & capability.VerifiedUtc.ToLocalTime().ToString("HH:mm:ss", Inv) & "  (" & capability.ProtocolVersion & ", " &
                                UpsConnectionDiagnostics.FormatMs(capability.ElapsedMilliseconds) & ")  –  " & UpsCapabilityProbe.MappingText(capability))
                If capability.Aborted.Length > 0 Then text.AppendLine("  " & capability.Aborted)
                For Each group In capability.Groups
                    text.AppendLine("  " & group.Name.PadRight(22) & group.Supported.ToString(Inv) & "/" & group.Requested.ToString(Inv) & "  " & group.Status &
                                    "  " & group.Milliseconds.ToString("0", Inv) & " ms")
                Next
                For Each line In capability.IndexEvidence
                    text.AppendLine("  " & line)
                Next
                If capability.UnsupportedOids.Count > 0 Then
                    text.AppendLine("  Unsupported (shown as '—', never 0):")
                    For Each item In capability.UnsupportedOids
                        text.AppendLine("    " & item)
                    Next
                End If
            End If
            If snapshot IsNot Nothing AndAlso snapshot.PartialGroups.Count > 0 Then
                text.AppendLine()
                text.AppendLine("LAST POLL PARTIAL: " & String.Join("; ", snapshot.PartialGroups) & "  (other values kept)")
            End If
        End If
        Dim content = text.ToString().Replace(vbLf, vbCrLf).Replace(vbCr & vbCrLf, vbCrLf)
        If force OrElse content <> _detailBox.Text Then _detailBox.Text = content
        ShowAudit(If(device Is Nothing, Nothing, UpsConnectionDiagnostics.LastAudit(device.Id)))
    End Sub

    Private Sub ShowAudit(audit As UpsOidAudit)
        If audit Is Nothing Then
            If _auditGrid.Visible Then ShowOidMapHeader()
            _shownAudit = Nothing
            Return
        End If
        If ReferenceEquals(audit, _shownAudit) Then Return
        _shownAudit = audit
        _auditGrid.Rows.Clear()
        For Each check In audit.Checks
            Dim index = _auditGrid.Rows.Add(check.Key, check.Oid, check.Status, check.RawValue, check.ScaledValue)
            _auditGrid.Rows(index).Cells("Status").Style.ForeColor = If(check.Present, If(check.InRange, UpsAtsView.OkGreen, UpsAtsView.WarnAmber), UiTheme.MutedInk)
        Next
        _auditGrid.ClearSelection()
        _oidHeader.Text = "OID AUDIT  " & audit.DeviceId & "  " & audit.IpAddress & "  " & audit.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", Inv) &
                          "  –  " & UpsConnectionDiagnostics.MappingText(audit) & If(audit.SysObjectId.Length > 0, "  –  sysObjectID " & audit.SysObjectId, "") &
                          If(audit.Aborted.Length > 0, "  –  " & audit.Aborted, "") & "  –  missing objects are shown as '—' on the dashboard, never 0."
        _oidHeader.ForeColor = If(audit.MappingVerified, UpsAtsView.OkGreen, UpsAtsView.WarnAmber)
        _oidGrid.Visible = False
        _auditGrid.Visible = True
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            RemoveHandler SnmpCredentialStore.Changed, AddressOf OnCredentialsChanged
            _diagnosticsTimer.Dispose()
            _revealTimer.Dispose()
            If _scanCancellation IsNot Nothing Then _scanCancellation.Cancel()
            If _testCancellation IsNot Nothing Then _testCancellation.Cancel()
            If _webCancellation IsNot Nothing Then _webCancellation.Cancel()
        End If
        MyBase.Dispose(disposing)
    End Sub
End Class
