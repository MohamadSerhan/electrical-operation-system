Imports System.Drawing
Imports System.Globalization
Imports System.Windows.Forms

Public NotInheritable Class PlcSettingsView
    Inherits UserControl

    Private ReadOnly _session As UserSession
    Private ReadOnly _save As Func(Of PlcSettings, Integer, Task)
    Private ReadOnly _host As New TextBox()
    Private ReadOnly _port As New NumericUpDown()
    Private ReadOnly _unitId As New NumericUpDown()
    Private ReadOnly _connectTimeout As New NumericUpDown()
    Private ReadOnly _readTimeout As New NumericUpDown()
    Private ReadOnly _swapWords As New CheckBox With {.Text = "Swap 16-bit float words"}
    Private ReadOnly _acquisitionInterval As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _customInterval As New NumericUpDown()
    Private ReadOnly _displayRefresh As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private _saveButton As Button
    Private ReadOnly _status As New Label()
    Private ReadOnly _canEdit As Boolean
    Private _settings As PlcSettings
    Private _loading As Boolean
    Private _saving As Boolean
    Private _dirty As Boolean

    Private _primarySettings As PlcSettings
    Private _refreshMilliseconds As Integer
    Private ReadOnly _profiles As PlcProfileStore
    Private _activeProfile As PlcProfile
    Private ReadOnly _selector As New ComboBox
    Private _addPlc As Button
    Private _renamePlc As Button
    Private _removePlc As Button
    Private ReadOnly _plcMessage As New Label

    Public Sub New(settings As PlcSettings,
                   refreshIntervalMilliseconds As Integer,
                   session As UserSession,
                   save As Func(Of PlcSettings, Integer, Task),
                   Optional profiles As PlcProfileStore = Nothing)
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        If session Is Nothing Then Throw New ArgumentNullException(NameOf(session))
        If save Is Nothing Then Throw New ArgumentNullException(NameOf(save))
        _settings = settings.Clone()
        _primarySettings = settings.Clone()
        _refreshMilliseconds = refreshIntervalMilliseconds
        _profiles = If(profiles, New PlcProfileStore())
        _activeProfile = _profiles.Profiles(0)
        _session = session
        _save = save
        _canEdit = PlcConfigurationAccess.CanEdit(session.RoleName, session.IsOfflinePreview)
        BackColor = UiTheme.Canvas

        ConfigureInputs()
        ApplySettings(_settings, refreshIntervalMilliseconds)
        BuildLayout()
        AddHandlers()
        RefillProfiles(_activeProfile)
    End Sub

    Private Sub ConfigureInputs()
        ConfigureNumeric(_port, 1, 65535)
        ConfigureNumeric(_unitId, 0, 247)
        ConfigureNumeric(_connectTimeout, 250, 30000)
        ConfigureNumeric(_readTimeout, 250, 10000)
        ConfigureNumeric(_customInterval, PlcSettings.MinimumAcquisitionIntervalMilliseconds,
                         PlcSettings.MaximumAcquisitionIntervalMilliseconds)
        _customInterval.Increment = 50
        _acquisitionInterval.Items.AddRange({
            "0.1 second", "0.25 second", "0.5 second", "1 second",
            "2 seconds", "5 seconds", "10 seconds", "Custom interval"})
        _displayRefresh.Items.AddRange({"Auto (100 ms)", "10 ms", "25 ms", "50 ms", "100 ms", "250 ms"})
        For Each fieldControl As Control In New Control() {_host, _port, _unitId, _connectTimeout, _readTimeout, _swapWords,
                                                           _acquisitionInterval, _customInterval, _displayRefresh}
            fieldControl.Enabled = _canEdit
        Next
        _host.BorderStyle = BorderStyle.FixedSingle
        _host.Font = New Font("Segoe UI", 9.5F)
        For Each numericInput As NumericUpDown In New NumericUpDown() {_port, _unitId, _connectTimeout, _readTimeout, _customInterval}
            numericInput.Dock = DockStyle.Fill
            numericInput.Font = New Font("Segoe UI", 9.5F)
            numericInput.ThousandsSeparator = True
        Next
        For Each comboInput As ComboBox In New ComboBox() {_acquisitionInterval, _displayRefresh}
            comboInput.Dock = DockStyle.Fill
            comboInput.Font = New Font("Segoe UI", 9.5F)
            comboInput.IntegralHeight = False
        Next
        _swapWords.AutoSize = True
        _swapWords.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        _saveButton = UiTheme.ActionButton("Save PLC & display", True, Icons.Save)
        _saveButton.Size = New Size(190, 36)
        _saveButton.Enabled = _canEdit
        _status.Dock = DockStyle.Fill
        _status.AutoEllipsis = True
        _status.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        _status.ForeColor = If(_canEdit, UiTheme.MutedInk, UiTheme.Amber)
        _status.Text = If(_canEdit, "Settings unchanged.", "READ ONLY  |  Administrator role required to edit PLC settings.")
    End Sub

    Private Shared Sub ConfigureNumeric(input As NumericUpDown, minimum As Integer, maximum As Integer)
        input.Minimum = minimum
        input.Maximum = maximum
    End Sub

    Private Sub BuildLayout()
        Dim root As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 2,
            .Padding = Padding.Empty,
            .BackColor = UiTheme.Canvas}
        root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 56.0F))

        Dim scroll As New Panel With {.Dock = DockStyle.Fill, .AutoScroll = True, .BackColor = UiTheme.Canvas}
        Dim content As New TableLayoutPanel With {
            .Dock = DockStyle.Top,
            .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .ColumnCount = 1,
            .Padding = New Padding(4),
            .BackColor = Color.Transparent}
        content.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        Dim note = UiTheme.Label(
            "PLC communication is read-only Modbus FC03. The register map is fixed and cannot be edited here.",
            New Font("Segoe UI", 9.0F, FontStyle.Bold), UiTheme.MutedInk, False)
        note.Dock = DockStyle.Fill
        note.Margin = New Padding(8, 3, 8, 8)
        content.Controls.Add(note)
        content.Controls.Add(BuildConnectionCard())
        content.Controls.Add(BuildAcquisitionCard())
        content.Controls.Add(BuildRegisterCard())
        scroll.Controls.Add(content)
        root.Controls.Add(scroll, 0, 0)

        Dim footer As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 2,
            .RowCount = 1,
            .Padding = New Padding(8, 4, 8, 4),
            .BackColor = UiTheme.Surface}
        footer.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 202.0F))
        footer.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        AddHandler _saveButton.Click, AddressOf OnSaveClicked
        footer.Controls.Add(_saveButton, 0, 0)
        footer.Controls.Add(_status, 1, 0)
        root.Controls.Add(footer, 0, 1)
        Controls.Add(root)
        Controls.Add(BuildPlcBar())
    End Sub

    ' PLC 1 is the live PLC. Extra PLCs are stored configuration profiles until multi-PLC acquisition is implemented.
    Private Function BuildPlcBar() As Control
        Dim bar As New FlowLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .WrapContents = True,
            .BackColor = UiTheme.Surface, .Padding = New Padding(14, 8, 14, 4)}
        Dim caption As New Label With {.Text = "PLC", .AutoSize = True, .Margin = New Padding(0, 10, 8, 0),
            .Font = New Font("Segoe UI", 9.5F, FontStyle.Bold), .ForeColor = UiTheme.Blue}
        _selector.Name = "plc-selector"
        _selector.DropDownStyle = ComboBoxStyle.DropDownList
        _selector.Width = 260
        _selector.Font = New Font("Segoe UI", 9.5F)
        _selector.Margin = New Padding(0, 7, 12, 4)
        _addPlc = UiTheme.ActionButton("Add PLC", True)
        _renamePlc = UiTheme.ActionButton("Rename", False)
        _removePlc = UiTheme.ActionButton("Remove", False)
        _addPlc.Name = "plc-add"
        _renamePlc.Name = "plc-rename"
        _removePlc.Name = "plc-remove"
        For Each action In {_addPlc, _renamePlc, _removePlc}
            action.Size = New Size(110, 36)
            action.Margin = New Padding(0, 3, 8, 4)
            action.Enabled = _canEdit
        Next
        _plcMessage.AutoSize = True
        _plcMessage.ForeColor = UiTheme.MutedInk
        _plcMessage.Margin = New Padding(8, 10, 0, 0)
        AddHandler _selector.SelectedIndexChanged, AddressOf OnProfileSelected
        AddHandler _addPlc.Click, AddressOf OnAddPlc
        AddHandler _renamePlc.Click, AddressOf OnRenamePlc
        AddHandler _removePlc.Click, AddressOf OnRemovePlc
        bar.Controls.AddRange({caption, _selector, _addPlc, _renamePlc, _removePlc, _plcMessage})
        Return bar
    End Function

    Private Sub RefillProfiles(select_ As PlcProfile)
        _selector.BeginUpdate()
        RemoveHandler _selector.SelectedIndexChanged, AddressOf OnProfileSelected
        _selector.Items.Clear()
        For Each profile In _profiles.Profiles
            _selector.Items.Add(New PlcItem(profile))
        Next
        For index = 0 To _selector.Items.Count - 1
            If DirectCast(_selector.Items(index), PlcItem).Profile Is select_ Then _selector.SelectedIndex = index
        Next
        AddHandler _selector.SelectedIndexChanged, AddressOf OnProfileSelected
        _selector.EndUpdate()
        ShowProfile(select_)
    End Sub

    Private Sub ShowProfile(profile As PlcProfile)
        _activeProfile = profile
        If profile.IsPrimary Then
            ApplySettings(_primarySettings, _refreshMilliseconds)
            _displayRefresh.Enabled = _canEdit
            _plcMessage.Text = "Live PLC used for acquisition."
        Else
            ApplySettings(profile.Settings, _refreshMilliseconds)
            _host.Text = profile.Settings.Host
            _displayRefresh.Enabled = False
            _plcMessage.Text = "Stored configuration only. Live acquisition currently uses PLC 1."
        End If
        SetDirty(False)
        _removePlc.Enabled = _canEdit AndAlso Not profile.IsPrimary
        _saveButton.Text = If(profile.IsPrimary, "Save PLC & display", "Save PLC settings")
    End Sub

    Private Sub OnProfileSelected(sender As Object, e As EventArgs)
        Dim item = TryCast(_selector.SelectedItem, PlcItem)
        If item Is Nothing OrElse item.Profile Is _activeProfile Then Return
        If _dirty AndAlso MessageBox.Show(Me, "Discard unsaved changes to " & _activeProfile.Name & "?", "PLC system",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) <> DialogResult.Yes Then
            RefillProfiles(_activeProfile)
            Return
        End If
        ShowProfile(item.Profile)
    End Sub

    Private Sub OnAddPlc(sender As Object, e As EventArgs)
        Dim name = NameDialog.Prompt(Me, "Add PLC", "PLC name", "PLC " & (_profiles.Profiles.Count + 1).ToString(CultureInfo.InvariantCulture),
                                     PlcProfileStore.MaximumNameLength, Font)
        If name Is Nothing Then Return
        Try
            Dim profile = _profiles.Add(name, _primarySettings)
            RefillProfiles(profile)
            _plcMessage.Text = "PLC added. Enter its address and settings, then save."
        Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is UnauthorizedAccessException
            _plcMessage.Text = "PLC was not added: " & ex.Message
        End Try
    End Sub

    Private Sub OnRenamePlc(sender As Object, e As EventArgs)
        Dim name = NameDialog.Prompt(Me, "Rename PLC", "PLC name", _activeProfile.Name, PlcProfileStore.MaximumNameLength, Font)
        If name Is Nothing Then Return
        Try
            _profiles.Rename(_activeProfile, name)
            RefillProfiles(_activeProfile)
            _plcMessage.Text = "PLC renamed."
        Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is UnauthorizedAccessException
            _plcMessage.Text = "PLC was not renamed: " & ex.Message
        End Try
    End Sub

    Private Sub OnRemovePlc(sender As Object, e As EventArgs)
        If _activeProfile.IsPrimary Then Return
        If MessageBox.Show(Me, "Remove '" & _activeProfile.Name & "' and its stored settings?", "Remove PLC",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) <> DialogResult.Yes Then Return
        Try
            _profiles.Remove(_activeProfile)
            RefillProfiles(_profiles.Profiles(0))
            _plcMessage.Text = "PLC removed."
        Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is UnauthorizedAccessException
            _plcMessage.Text = "PLC was not removed: " & ex.Message
        End Try
    End Sub

    Private NotInheritable Class PlcItem
        Public Sub New(profile As PlcProfile)
            Me.Profile = profile
        End Sub

        Public ReadOnly Property Profile As PlcProfile

        Public Overrides Function ToString() As String
            Return Profile.Name
        End Function
    End Class

    Private Function BuildConnectionCard() As Control
        Dim fields As New TableLayoutPanel With {
            .Dock = DockStyle.Top,
            .AutoSize = True,
            .ColumnCount = 3,
            .Padding = New Padding(8, 4, 8, 8),
            .BackColor = Color.Transparent}
        fields.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50.0F))
        fields.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 25.0F))
        fields.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 25.0F))
        AddField(fields, "PLC IPv4 address", _host, 0, 0)
        AddField(fields, "Modbus TCP port", _port, 1, 0)
        AddField(fields, "Unit ID", _unitId, 2, 0)
        AddField(fields, "Connect timeout (ms)", _connectTimeout, 0, 1)
        AddField(fields, "Read timeout (ms)", _readTimeout, 1, 1)
        _swapWords.Dock = DockStyle.Fill
        fields.Controls.Add(_swapWords, 2, 1)
        Return CreateCard("PLC CONNECTION", fields, 220)
    End Function

    Private Function BuildAcquisitionCard() As Control
        Dim fields As New TableLayoutPanel With {
            .Dock = DockStyle.Top,
            .AutoSize = True,
            .ColumnCount = 3,
            .Padding = New Padding(8, 4, 8, 8),
            .BackColor = Color.Transparent}
        fields.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 40.0F))
        fields.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 30.0F))
        fields.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 30.0F))
        AddField(fields, "Requested acquisition interval", _acquisitionInterval, 0, 0)
        AddField(fields, "Custom interval (100–10,000 ms)", _customInterval, 1, 0)
        AddField(fields, "Display refresh interval", _displayRefresh, 2, 0)
        Dim explanation = UiTheme.Label(
            "Acquisition, historian recording and screen refresh are independent. The requested interval is not a guaranteed meter update rate; the four FC03 blocks are read in sequence.",
            New Font("Segoe UI", 8.5F), UiTheme.MutedInk, False)
        explanation.Dock = DockStyle.Fill
        explanation.Margin = New Padding(4, 8, 4, 2)
        fields.RowCount = 2
        fields.RowStyles.Add(New RowStyle(SizeType.Absolute, 64.0F))
        fields.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        fields.Controls.Add(explanation, 0, 1)
        fields.SetColumnSpan(explanation, 3)
        Return CreateCard("ACQUISITION & DISPLAY", fields, 200)
    End Function

    Private Shared Function BuildRegisterCard() As Control
        Dim grid As New DataGridView With {
            .Dock = DockStyle.Fill,
            .ReadOnly = True,
            .AllowUserToAddRows = False,
            .AllowUserToDeleteRows = False,
            .AllowUserToResizeRows = False,
            .MultiSelect = False,
            .RowHeadersVisible = False,
            .SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            .AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            .BackgroundColor = UiTheme.Surface,
            .BorderStyle = BorderStyle.None,
            .ColumnHeadersHeight = 28,
            .RowTemplate = New DataGridViewRow With {.Height = 27}}
        UiTheme.StyleReadOnlyGrid(grid)
        grid.Columns.Add("Equipment", "METER")
        grid.Columns.Add("Model", "MODEL")
        grid.Columns.Add("Start", "ZERO-BASED REGISTER START")
        grid.Columns.Add("Count", "REGISTER COUNT")
        grid.Rows.Add("T1", "PM5110", "2000", "32")
        grid.Rows.Add("T2", "PM5110", "2050", "32")
        grid.Rows.Add("T3", "PM5110", "2100", "32")
        grid.Rows.Add("T4", "PM5110", "2150", "32")
        grid.ClearSelection()
        Dim host As New Panel With {.Dock = DockStyle.Fill, .Padding = New Padding(8, 4, 8, 8)}
        host.Controls.Add(grid)
        Return CreateCard("FIXED PM5110 REGISTER MAP · READ ONLY", host, 220)
    End Function

    Private Shared Function CreateCard(title As String, content As Control, Optional height As Integer = 168) As Control
        Dim card As New RoundedPanel With {
            .Dock = DockStyle.Top,
            .BackColor = UiTheme.Surface,
            .Padding = New Padding(10, 8, 10, 8),
            .Margin = New Padding(4, 3, 4, 8),
            .Height = height}
        Dim layout As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 2,
            .BackColor = Color.Transparent}
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, 30.0F))
        layout.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        Dim heading = Icons.SectionHeading(Icons.Monitor, title)
        heading.Dock = DockStyle.Fill
        heading.Margin = Padding.Empty
        content.Dock = DockStyle.Fill
        content.Margin = Padding.Empty
        layout.Controls.Add(heading, 0, 0)
        layout.Controls.Add(content, 0, 1)
        card.Controls.Add(layout)
        CardCollapse.Attach(card, heading, True, content)
        Return card
    End Function

    Private Shared Sub AddField(parent As TableLayoutPanel, caption As String, input As Control, column As Integer, row As Integer)
        Dim field As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 2,
            .Padding = New Padding(4, 2, 8, 2),
            .BackColor = Color.Transparent}
        field.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        field.RowStyles.Add(New RowStyle(SizeType.Absolute, 20.0F))
        field.RowStyles.Add(New RowStyle(SizeType.Absolute, 32.0F))
        Dim label = UiTheme.Label(caption, New Font("Segoe UI", 8.0F, FontStyle.Bold), UiTheme.MutedInk, False)
        label.Dock = DockStyle.Fill
        label.AutoEllipsis = True
        input.Dock = DockStyle.Fill
        field.Controls.Add(label, 0, 0)
        field.Controls.Add(input, 0, 1)
        parent.Controls.Add(field, column, row)
    End Sub

    Private Sub AddHandlers()
        AddHandler _host.TextChanged, AddressOf OnInputChanged
        For Each numericInput As NumericUpDown In New NumericUpDown() {_port, _unitId, _connectTimeout, _readTimeout, _customInterval}
            AddHandler numericInput.ValueChanged, AddressOf OnInputChanged
        Next
        AddHandler _swapWords.CheckedChanged, AddressOf OnInputChanged
        AddHandler _acquisitionInterval.SelectedIndexChanged, AddressOf OnIntervalChanged
        AddHandler _displayRefresh.SelectedIndexChanged, AddressOf OnInputChanged
    End Sub

    Public Sub LoadSettings(settings As PlcSettings, refreshIntervalMilliseconds As Integer)
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        _primarySettings = settings.Clone()
        _refreshMilliseconds = refreshIntervalMilliseconds
        If _activeProfile IsNot Nothing AndAlso Not _activeProfile.IsPrimary Then Return
        ApplySettings(settings, refreshIntervalMilliseconds)
    End Sub

    Private Sub ApplySettings(settings As PlcSettings, refreshIntervalMilliseconds As Integer)
        _settings = settings.Clone()
        _settings.Normalize()
        _loading = True
        Try
            _host.Text = _settings.Host
            _port.Value = ClampValue(_port, _settings.Port)
            _unitId.Value = ClampValue(_unitId, _settings.UnitId)
            _connectTimeout.Value = ClampValue(_connectTimeout, _settings.ConnectTimeoutMilliseconds)
            _readTimeout.Value = ClampValue(_readTimeout, _settings.ReadTimeoutMilliseconds)
            _swapWords.Checked = _settings.SwapFloatWords
            SelectAcquisitionInterval(_settings.EffectivePollingIntervalMilliseconds)
            Dim refreshIndex = Array.IndexOf({0, 10, 25, 50, 100, 250}, refreshIntervalMilliseconds)
            _displayRefresh.SelectedIndex = If(refreshIndex >= 0, refreshIndex, 0)
        Finally
            _loading = False
        End Try
        SetDirty(False)
    End Sub

    Private Shared Function ClampValue(input As NumericUpDown, value As Integer) As Decimal
        Return Math.Min(input.Maximum, Math.Max(input.Minimum, CDec(value)))
    End Function

    Private Sub SelectAcquisitionInterval(milliseconds As Integer)
        Dim presets = {100, 250, 500, 1000, 2000, 5000, 10000}
        Dim index = Array.IndexOf(presets, milliseconds)
        _acquisitionInterval.SelectedIndex = If(index >= 0, index, 7)
        _customInterval.Value = ClampValue(_customInterval, milliseconds)
        _customInterval.Enabled = _canEdit AndAlso _acquisitionInterval.SelectedIndex = 7
    End Sub

    Private Sub OnIntervalChanged(sender As Object, e As EventArgs)
        _customInterval.Enabled = _canEdit AndAlso _acquisitionInterval.SelectedIndex = 7
        OnInputChanged(sender, e)
    End Sub

    Private Sub OnInputChanged(sender As Object, e As EventArgs)
        If Not _loading Then SetDirty(True)
    End Sub

    Private Sub SetDirty(dirty As Boolean)
        _dirty = dirty
        _saveButton.Enabled = _canEdit AndAlso dirty AndAlso Not _saving
        If dirty Then
            _status.Text = "Unsaved PLC/display changes."
            _status.ForeColor = UiTheme.Amber
        ElseIf _canEdit Then
            _status.Text = "Settings unchanged."
            _status.ForeColor = UiTheme.MutedInk
        End If
    End Sub

    Private Async Sub OnSaveClicked(sender As Object, e As EventArgs)
        If Not _canEdit OrElse Not _dirty OrElse _saving Then Return
        Try
            Dim updated = _settings.Clone()
            updated.Host = _host.Text.Trim()
            updated.Port = CInt(_port.Value)
            updated.UnitId = CInt(_unitId.Value)
            updated.ConnectTimeoutMilliseconds = CInt(_connectTimeout.Value)
            updated.ReadTimeoutMilliseconds = CInt(_readTimeout.Value)
            updated.SwapFloatWords = _swapWords.Checked
            Dim interval = If(_acquisitionInterval.SelectedIndex = 7,
                CInt(_customInterval.Value),
                {100, 250, 500, 1000, 2000, 5000, 10000}(_acquisitionInterval.SelectedIndex))
            updated.RequestedAcquisitionIntervalMilliseconds = interval
            updated.PollingIntervalSeconds = CInt(Math.Ceiling(interval / 1000.0R))
            updated.Validate()
            If Not _activeProfile.IsPrimary Then
                _profiles.UpdateSettings(_activeProfile, updated)
                _settings = updated
                SetDirty(False)
                _status.Text = "PLC settings stored. Live acquisition currently uses PLC 1."
                _status.ForeColor = UiTheme.Green
                Return
            End If
            Dim refreshInterval = {0, 10, 25, 50, 100, 250}(_displayRefresh.SelectedIndex)
            Dim uiValidation As New UiSettings With {.RefreshIntervalMilliseconds = refreshInterval}
            uiValidation.Validate()
            _saving = True
            _saveButton.Enabled = False
            _status.Text = "Saving and applying settings…"
            _status.ForeColor = UiTheme.MutedInk
            Await _save(updated, refreshInterval)
            _settings = updated
            _primarySettings = updated.Clone()
            SetDirty(False)
            _status.Text = "PLC and display settings saved."
            _status.ForeColor = UiTheme.Green
        Catch ex As Exception
            _status.Text = "Settings were not saved: " & ex.Message
            _status.ForeColor = UiTheme.Red
        Finally
            _saving = False
            _saveButton.Enabled = _canEdit AndAlso _dirty
        End Try
    End Sub
End Class
