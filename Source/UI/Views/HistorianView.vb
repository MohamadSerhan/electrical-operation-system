Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Threading.Tasks
Imports System.Windows.Forms

Public NotInheritable Class HistorianView
    Inherits UserControl

    Private Shared ReadOnly AnalyzerIds As String() = {"T1", "T2", "T3", "T4", "TOTAL"}
    Private Shared ReadOnly BorderNeutral As Color = Color.FromArgb(190, 201, 211)

    Private NotInheritable Class AnalyzerDraft
        Public ReadOnly Selected As New HashSet(Of String)(StringComparer.Ordinal)
    End Class

    Private ReadOnly _session As UserSession
    Private ReadOnly _historian As MeasurementHistorian
    Private ReadOnly _scheduler As DailyHistorianExportScheduler
    Private ReadOnly _saveSettings As Func(Of RecordingSettings, Task)
    Private ReadOnly _savePlcSettings As Func(Of PlcSettings, Integer, Task)
    Private _plcSettings As PlcSettings
    Private _uiRefreshIntervalMilliseconds As Integer
    Private _actualUiRefreshRateHz As Double
    Private _acquisitionDirty As Boolean
    Private _settings As RecordingSettings
    Private ReadOnly _drafts As New Dictionary(Of String, AnalyzerDraft)(StringComparer.Ordinal)
    Private ReadOnly _analyzerButtons As New Dictionary(Of String, Button)(StringComparer.Ordinal)
    Private ReadOnly _canEdit As Boolean
    Private _selectedAnalyzer As String = "T1"
    Private _loading As Boolean
    Private _dirty As Boolean
    Private _baselineFingerprint As String = String.Empty
    Private _lastLiveRenderTicks As Long
    Private _lastDiagnosticsRenderTicks As Long
    Private _savingRecording As Boolean
    Private _savingAcquisition As Boolean
    Private _refreshing As Boolean
    Private _jobs As IReadOnlyList(Of DailyExportJob) = Array.Empty(Of DailyExportJob)()

    Private ReadOnly _root As New Panel()
    Private ReadOnly _stack As New TableLayoutPanel()
    Private ReadOnly _healthDetail As New Label()
    Private ReadOnly _jobsGrid As New DataGridView()
    Private ReadOnly _dailyExcel As New CheckBox()
    Private ReadOnly _timezone As New ComboBox()
    Private ReadOnly _exportTime As New DateTimePicker()
    Private ReadOnly _retentionDays As New NumericUpDown()
    Private ReadOnly _exportFolder As New TextBox()
    Private ReadOnly _continuousStatus As New Label()
    Private ReadOnly _resolutionToggle As IconButton = NewButton("Recording interval & planning window", Icons.ChevronDown, False)
    Private ReadOnly _resolutionPanel As New Panel()
    Private ReadOnly _resolutionSummary As New Label()
    Private ReadOnly _resolutionGrid As New DataGridView()
    Private ReadOnly _measurementHeading As New Label()
    Private ReadOnly _search As New TextBox()
    Private ReadOnly _groupByCategory As New CheckBox()
    Private ReadOnly _parameters As New ListView()
    Private ReadOnly _tabs As New FlatTabControl()
    Private ReadOnly _activeEvents As ActiveEventsView
    Private ReadOnly _equipmentHistory As EquipmentHistoryView
    Private ReadOnly _fireEvents As FireEventsView
    Private ReadOnly _acquisitionInterval As New ComboBox()
    Private ReadOnly _customAcquisitionIntervalMilliseconds As New NumericUpDown()
    Private ReadOnly _acquisitionMessage As New Label()
    Private ReadOnly _saveAcquisitionButton As IconButton = NewButton("Save Acquisition Interval", Icons.Save, True)
    Private ReadOnly _requestedIntervalValue As New Label()
    Private ReadOnly _actualIntervalValue As New Label()
    Private ReadOnly _validDataRateValue As New Label()
    Private ReadOnly _plcResponseValue As New Label()
    Private ReadOnly _fullCycleValue As New Label()
    Private ReadOnly _timeoutCountValue As New Label()
    Private ReadOnly _failedCycleValue As New Label()
    Private ReadOnly _lastGoodValue As New Label()
    Private ReadOnly _freshnessValue As New Label()
    Private ReadOnly _recordingIntervalValue As New Label()
    Private ReadOnly _displayRefreshValue As New Label()
    Private ReadOnly _liveQualityLabel As New Label()
    Private ReadOnly _liveMeasurementGrid As New DataGridView()
    Private ReadOnly _continuousRawRecording As New CheckBox()
    Private ReadOnly _intervalCombo As New ComboBox()
    Private ReadOnly _intervalChoicesMs As New List(Of Integer)()
    Private ReadOnly _retentionHint As New Label()
    Private ReadOnly _storageEstimateHint As New Label()
    Private _storageEstimate As RawRecordingStorageEstimate
    Private _storageEstimateError As String = "Open Raw Recording to measure stored row sizes."
    Private _refreshingStorageEstimate As Boolean
    Private _storageEstimateCheckedUtc As DateTime = DateTime.MinValue
    Private ReadOnly _message As New Label()
    Private ReadOnly _dirtyLabel As New Label()
    Private ReadOnly _saveButton As IconButton = NewButton("Save Recording Settings", Icons.Save, True)
    Private ReadOnly _refreshTimer As New System.Windows.Forms.Timer()
    Private _pageActive As Boolean
    Private _currentAcquisitionStatus As PlcConnectionStatus
    Private _currentLiveReadings As IReadOnlyList(Of MeterSnapshot) = Array.Empty(Of MeterSnapshot)()

    Public Sub New(settings As RecordingSettings,
                   session As UserSession,
                   historian As MeasurementHistorian,
                   scheduler As DailyHistorianExportScheduler,
                   saveSettings As Func(Of RecordingSettings, Task),
                   Optional pollingIntervalSeconds As Integer = 10,
                   Optional plcSettings As PlcSettings = Nothing,
                   Optional uiRefreshIntervalMilliseconds As Integer = 0,
                   Optional savePlcSettings As Func(Of PlcSettings, Integer, Task) = Nothing,
                   Optional acquisitionStatus As PlcConnectionStatus = Nothing,
                   Optional liveReadings As IReadOnlyList(Of MeterSnapshot) = Nothing,
                   Optional activeAlarms As ActiveAlarmService = Nothing,
                   Optional firePanels As FirePanelManager = Nothing)
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        If session Is Nothing Then Throw New ArgumentNullException(NameOf(session))
        If historian Is Nothing Then Throw New ArgumentNullException(NameOf(historian))
        If scheduler Is Nothing Then Throw New ArgumentNullException(NameOf(scheduler))
        If saveSettings Is Nothing Then Throw New ArgumentNullException(NameOf(saveSettings))
        _settings = settings
        _session = session
        _historian = historian
        _activeEvents = New ActiveEventsView(If(activeAlarms Is Nothing, Nothing, New Func(Of DateTime, ActiveAlarmSnapshot)(AddressOf activeAlarms.Read)))
        _equipmentHistory = New EquipmentHistoryView(EquipmentHistoryService.Create(historian, settings, Function() _settings))
        _fireEvents = New FireEventsView(firePanels, session)
        _scheduler = scheduler
        _saveSettings = saveSettings
        _savePlcSettings = savePlcSettings
        _plcSettings = If(plcSettings Is Nothing,
                          New PlcSettings With {.PollingIntervalSeconds = Math.Max(1, pollingIntervalSeconds)},
                          plcSettings.Clone())
        _plcSettings.Normalize()
        _uiRefreshIntervalMilliseconds = uiRefreshIntervalMilliseconds
        _currentAcquisitionStatus = acquisitionStatus
        _currentLiveReadings = If(liveReadings, Array.Empty(Of MeterSnapshot)())
        _canEdit = PlcConfigurationAccess.CanEdit(session.RoleName, session.IsOfflinePreview)
        BackColor = UiTheme.Canvas
        SetStyle(ControlStyles.OptimizedDoubleBuffer Or ControlStyles.AllPaintingInWmPaint, True)
        BuildDrafts()
        BuildLayout()
        LoadDraft()
        _loading = True
        ApplyPolicyFromSettings()
        _loading = False
        ApplyEditability()
        ApplyRawRecordingSettings()
        SelectAcquisitionInterval(_plcSettings.EffectivePollingIntervalMilliseconds)
        If acquisitionStatus IsNot Nothing Then
            SetAcquisitionSnapshot(acquisitionStatus, _currentLiveReadings, uiRefreshIntervalMilliseconds, 0)
        End If
        SetDirty(False)
        _refreshTimer.Interval = 30000
        AddHandler _refreshTimer.Tick, Async Sub(sender, e)
                                          Await RefreshHealthAsync()
                                      End Sub
        AddHandler HandleCreated, Async Sub(sender, e)
                                                If _pageActive Then Await RefreshHealthAsync()
                                            End Sub
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            _refreshTimer.Dispose()
        End If
        MyBase.Dispose(disposing)
    End Sub

    Public Sub SetPageActive(active As Boolean)
        If _pageActive = active Then Return
        _pageActive = active
        _activeEvents.SetPageActive(active AndAlso _tabs.SelectedTab Is _activeEvents.Parent)
        _fireEvents.SetPageActive(active AndAlso _tabs.SelectedTab Is _fireEvents.Parent)
        If active Then
            _refreshTimer.Start()
            If IsHandleCreated Then Call RefreshHealthAsync()
        Else
            _refreshTimer.Stop()
        End If
    End Sub

    Private Sub BuildDrafts()
        For Each analyzerId In AnalyzerIds
            Dim equipment = _settings.GetAnalyzer(analyzerId)
            Dim draft As New AnalyzerDraft()
            For Each parameterId In equipment.SelectedParameters
                draft.Selected.Add(parameterId)
            Next
            _drafts(analyzerId) = draft
        Next
    End Sub

    ' ------------------------------------------------------------------ layout

    Private Shared Function NewButton(text As String, glyph As String, primary As Boolean) As IconButton
        Return New IconButton With {.Text = text, .Glyph = glyph, .Primary = primary, .Height = 36}
    End Function

    Private Function NewCard(title As String, ByRef body As TableLayoutPanel, Optional glyph As String = Nothing, Optional note As String = Nothing) As RoundedPanel
        Dim card As New RoundedPanel With {
            .Dock = DockStyle.Top,
            .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .Padding = New Padding(16, 12, 16, 12),
            .Margin = New Padding(0, 0, 0, 12),
            .Radius = 12,
            .BackColor = UiTheme.Surface
        }
        body = New TableLayoutPanel With {
            .Dock = DockStyle.Top,
            .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .ColumnCount = 1,
            .BackColor = Color.Transparent
        }
        body.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        If Not String.IsNullOrEmpty(title) Then
            AddRow(body, Icons.SectionHeading(If(glyph, Icons.Info), title, note))
        End If
        card.Controls.Add(body)
        Return card
    End Function

    Private Shared Sub AddRow(table As TableLayoutPanel, control As Control)
        control.Dock = DockStyle.Top
        table.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        table.Controls.Add(control, 0, table.RowCount)
        table.RowCount += 1
    End Sub

    Private Shared Sub AddFixedRow(table As TableLayoutPanel, control As Control, height As Integer)
        control.Dock = DockStyle.Fill
        table.RowStyles.Add(New RowStyle(SizeType.Absolute, height))
        table.Controls.Add(control, 0, table.RowCount)
        table.RowCount += 1
    End Sub

    Private Shared Function WrapLabel(text As String, font As Font, color As Color, container As Control) As Label
        Dim label = UiTheme.Label(text, font, color)
        label.Margin = New Padding(0, 2, 0, 2)
        Dim fit As Action = Sub() label.MaximumSize = New Size(Math.Max(200, container.ClientSize.Width - 8), 0)
        AddHandler container.SizeChanged, Sub(sender, e) fit()
        fit()
        Return label
    End Function

    Private Shared Function Field(caption As String, input As Control) As TableLayoutPanel
        Dim table As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .AutoSize = True,
            .ColumnCount = 1,
            .Margin = New Padding(0, 0, 12, 4),
            .Padding = New Padding(0, 0, 0, 8),
            .BackColor = Color.Transparent
        }
        table.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        Dim label = UiTheme.Label(caption.ToUpperInvariant(), New Font("Segoe UI", 8.5F, FontStyle.Bold), UiTheme.MutedInk)
        label.Margin = New Padding(0, 0, 0, 3)
        table.Controls.Add(label, 0, 0)
        input.Dock = DockStyle.Top
        input.Margin = Padding.Empty
        table.Controls.Add(input, 0, 1)
        Return table
    End Function


    Private Shared Sub StyleInput(input As Control)
        input.Font = New Font("Segoe UI", 11.0F)
        input.ForeColor = UiTheme.Ink
    End Sub

    Private Sub BuildLayout()
        _root.Dock = DockStyle.Fill
        _root.AutoScroll = False
        _root.BackColor = UiTheme.Canvas
        _root.Padding = New Padding(0, 0, 0, 0)
        _stack.Dock = DockStyle.Fill
        _stack.AutoSize = False
        _stack.ColumnCount = 1
        _stack.RowCount = 4
        _stack.RowStyles.Add(New RowStyle(SizeType.Absolute, UiTheme.WorkspaceNavigationHeight))
        _stack.RowStyles.Add(New RowStyle(SizeType.Absolute, 46.0F))
        _stack.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        _stack.RowStyles.Add(New RowStyle(SizeType.Absolute, 62.0F))
        _stack.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        _stack.Padding = New Padding(0, 0, 4, 0)
        _stack.BackColor = Color.Transparent

        Dim tabStrip As New SegmentedTabStrip(_tabs) With {.Dock = DockStyle.Fill, .Margin = Padding.Empty}
        _stack.Controls.Add(tabStrip, 0, 0)

        Dim health = BuildHealthCard()
        health.Dock = DockStyle.Fill
        health.Margin = New Padding(0, 6, 0, 4)
        _stack.Controls.Add(health, 0, 1)

        _tabs.Dock = DockStyle.Fill
        _tabs.Font = New Font("Segoe UI", 9.5F, FontStyle.Bold)
        ' Native headers are hidden; the segmented strip below drives the selection.
        _tabs.Appearance = TabAppearance.FlatButtons
        _tabs.SizeMode = TabSizeMode.Fixed
        _tabs.ItemSize = New Size(0, 1)
        _tabs.Padding = New Point(0, 0)
        _tabs.TabPages.Add(BuildFillTab("Acquisition", BuildLiveAcquisitionCard()))
        _tabs.TabPages.Add(BuildWorkflowTab("Raw recording", BuildRawRecordingCard()))
        _tabs.TabPages.Add(BuildDailyReportsTab())
        _tabs.TabPages.Add(BuildFillTab("Active events", _activeEvents))
        _tabs.TabPages.Add(BuildFillTab("Equipment history", _equipmentHistory))
        _tabs.TabPages.Add(BuildFillTab("Fire Events", _fireEvents))
        _tabs.SelectedIndex = 4
        AddHandler _tabs.SelectedIndexChanged, Async Sub(sender, e)
                                                  If _tabs.SelectedIndex = 0 Then
                                                      _lastDiagnosticsRenderTicks = 0
                                                      _lastLiveRenderTicks = 0
                                                      AddAcquisitionSnapshot(_currentAcquisitionStatus, _currentLiveReadings,
                                                                             _uiRefreshIntervalMilliseconds, _actualUiRefreshRateHz)
                                                  End If
                                                  _activeEvents.SetPageActive(_pageActive AndAlso _tabs.SelectedTab Is _activeEvents.Parent)
                                                  _fireEvents.SetPageActive(_pageActive AndAlso _tabs.SelectedTab Is _fireEvents.Parent)
                                                  If _tabs.SelectedTab Is _activeEvents.Parent Then Await _activeEvents.RefreshRecordsAsync()
                                              End Sub
        _stack.Controls.Add(_tabs, 0, 2)
        Dim footer = BuildFooter()
        footer.Dock = DockStyle.Fill
        footer.Padding = New Padding(18, 6, 18, 6)
        footer.Margin = New Padding(0, 4, 0, 4)
        _stack.Controls.Add(footer, 0, 3)
        ' Policy and daily report measurement selections share the save bar.
        Dim UpdateFooter = Sub()
                               Dim applies = _tabs.SelectedIndex = 1 OrElse _tabs.SelectedIndex = 2
                               Dim fireHistory = _tabs.SelectedTab Is _fireEvents.Parent
                               footer.Visible = applies
                               health.Visible = Not fireHistory
                               _stack.RowStyles(1).Height = If(fireHistory, 0, 46)
                               _stack.RowStyles(3).Height = If(fireHistory, 0, 62)
                           End Sub
        AddHandler _tabs.SelectedIndexChanged, Sub(sender, e) UpdateFooter()
        UpdateFooter()
        _root.Controls.Add(_stack)
        Controls.Add(_root)
    End Sub

    Private Shared Function BuildFillTab(caption As String, card As Control) As TabPage
        Dim page As New TabPage(caption) With {.BackColor = UiTheme.Canvas, .Padding = New Padding(8, 8, 8, 4)}
        card.Dock = DockStyle.Fill
        page.Controls.Add(card)
        Return page
    End Function

    Private Function BuildDailyReportsTab() As TabPage
        Dim sections As New FlatTabControl With {.Dock = DockStyle.Fill, .Appearance = TabAppearance.FlatButtons,
            .SizeMode = TabSizeMode.Fixed, .ItemSize = New Size(0, 1)}
        sections.TabPages.Add(BuildStackedFillTab("Schedule & exports", BuildPolicyCard(), BuildJobsCard()))
        Dim measurements As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 2,
            .Padding = Padding.Empty,
            .BackColor = Color.Transparent}
        measurements.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        measurements.RowStyles.Add(New RowStyle(SizeType.Absolute, 40.0F))
        measurements.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        Dim selector As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .WrapContents = False}
        For Each analyzerId In AnalyzerIds
            Dim button = UiTheme.Button(analyzerId, False)
            button.Tag = analyzerId
            button.Size = New Size(If(analyzerId = "TOTAL", 72, 54), 32)
            button.Margin = New Padding(0, 0, 3, 0)
            AddHandler button.Click, AddressOf OnAnalyzerSelected
            _analyzerButtons.Add(analyzerId, button)
            selector.Controls.Add(button)
        Next
        measurements.Controls.Add(selector, 0, 0)
        measurements.Controls.Add(BuildMeasurementPanel(), 0, 1)
        ApplyAnalyzerButtonStyles()
        sections.TabPages.Add(BuildFillTab("Daily measurement selection", measurements))
        Dim host As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 2,
            .Padding = Padding.Empty,
            .BackColor = Color.Transparent}
        host.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        host.RowStyles.Add(New RowStyle(SizeType.Absolute, UiTheme.WorkspaceNavigationHeight))
        host.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        Dim navigation As New SegmentedTabStrip(sections) With {.Dock = DockStyle.Fill, .Margin = Padding.Empty}
        host.Controls.Add(navigation, 0, 0)
        host.Controls.Add(sections, 0, 1)
        Return BuildFillTab("Daily reports", host)
    End Function

    Private Shared Function BuildStackedFillTab(caption As String, topCard As Control, fillCard As Control) As TabPage
        Dim page As New TabPage(caption) With {.BackColor = UiTheme.Canvas, .Padding = New Padding(8, 8, 8, 4)}
        Dim layout As New TableLayoutPanel With {
            .Dock = DockStyle.Top,
            .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .ColumnCount = 1,
            .RowCount = 2,
            .BackColor = Color.Transparent}
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        layout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        layout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        topCard.Dock = DockStyle.Top
        fillCard.Dock = DockStyle.Top
        fillCard.Height = Math.Max(fillCard.Height, 280)
        fillCard.MinimumSize = New Size(0, 200)
        layout.Controls.Add(topCard, 0, 0)
        layout.Controls.Add(fillCard, 0, 1)
        Dim scroll As New Panel With {.Dock = DockStyle.Fill, .AutoScroll = True}
        scroll.Controls.Add(layout)
        page.Controls.Add(scroll)
        Return page
    End Function

    Private Function BuildWorkflowTab(caption As String, ParamArray cards As Control()) As TabPage
        Dim page As New TabPage(caption) With {
            .BackColor = UiTheme.Canvas,
            .Padding = New Padding(8)
        }
        Dim scroll As New Panel With {
            .Dock = DockStyle.Fill,
            .AutoScroll = True,
            .BackColor = UiTheme.Canvas
        }
        Dim content As New TableLayoutPanel With {
            .Dock = DockStyle.Top,
            .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .ColumnCount = 1,
            .BackColor = Color.Transparent,
            .Padding = New Padding(0, 0, 6, 8)
        }
        content.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        For Each card In cards
            AddRow(content, card)
        Next
        scroll.Controls.Add(content)
        page.Controls.Add(scroll)
        Return page
    End Function

    Private Function BuildHealthCard() As Control
        Dim body As TableLayoutPanel = Nothing
        Dim card = NewCard(String.Empty, body)
        card.Padding = New Padding(14, 3, 14, 3)
        Dim statusPanel As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 2,
            .BackColor = Color.Transparent,
            .Margin = Padding.Empty
        }
        statusPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        statusPanel.RowStyles.Add(New RowStyle(SizeType.Absolute, 18.0F))
        statusPanel.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        _continuousStatus.Dock = DockStyle.Fill
        _continuousStatus.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        _continuousStatus.UseMnemonic = False
        _continuousStatus.AutoEllipsis = True
        _healthDetail.Dock = DockStyle.Fill
        _healthDetail.Font = New Font("Segoe UI", 8.0F)
        _healthDetail.ForeColor = UiTheme.MutedInk
        _healthDetail.UseMnemonic = False
        _healthDetail.AutoEllipsis = True
        statusPanel.Controls.Add(_continuousStatus, 0, 0)
        statusPanel.Controls.Add(_healthDetail, 0, 1)
        AddFixedRow(body, statusPanel, 34)
        _healthDetail.Text = "Queue, dropped cycles, write errors, database size and most recent write are shown here."
        Return card
    End Function

    Private Function BuildLiveAcquisitionCard() As Control
        Dim body As TableLayoutPanel = Nothing
        Dim card = NewCard("LIVE PLC ACQUISITION SETTINGS", body, Icons.Refresh,
                           "Acquisition, historian recording and screen refresh have independent requested intervals.")
        _acquisitionInterval.DropDownStyle = ComboBoxStyle.DropDownList
        _acquisitionInterval.Items.AddRange({
            "0.1 second", "0.25 second", "0.5 second", "1 second",
            "2 seconds", "5 seconds", "10 seconds", "Custom interval"})
        StyleInput(_acquisitionInterval)
        AddHandler _acquisitionInterval.SelectedIndexChanged, AddressOf OnAcquisitionIntervalChanged

        _customAcquisitionIntervalMilliseconds.Minimum = PlcSettings.MinimumAcquisitionIntervalMilliseconds
        _customAcquisitionIntervalMilliseconds.Maximum = PlcSettings.MaximumAcquisitionIntervalMilliseconds
        _customAcquisitionIntervalMilliseconds.Increment = 1
        _customAcquisitionIntervalMilliseconds.ThousandsSeparator = True
        StyleInput(_customAcquisitionIntervalMilliseconds)
        AddHandler _customAcquisitionIntervalMilliseconds.ValueChanged,
            Sub(sender, e)
                If Not _loading AndAlso _acquisitionInterval.SelectedIndex = 7 Then SetAcquisitionDirty()
            End Sub

        _saveAcquisitionButton.Size = New Size(202, 28)
        _saveAcquisitionButton.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        AddHandler _saveAcquisitionButton.Click, AddressOf OnSaveAcquisitionClicked
        _acquisitionMessage.AutoSize = True
        _acquisitionMessage.Font = New Font("Segoe UI", 8.5F, FontStyle.Bold)
        _acquisitionMessage.ForeColor = UiTheme.MutedInk
        _acquisitionMessage.UseMnemonic = False

        Dim controls As New TableLayoutPanel With {
            .Dock = DockStyle.Top,
            .AutoSize = True,
            .ColumnCount = 4,
            .BackColor = Color.Transparent
        }
        controls.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 28.0F))
        controls.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 22.0F))
        controls.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 214.0F))
        controls.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50.0F))
        controls.RowStyles.Add(New RowStyle(SizeType.Absolute, 64.0F))
        _acquisitionInterval.Font = New Font("Segoe UI", 10.5F)
        _acquisitionInterval.IntegralHeight = False
        _acquisitionInterval.Margin = New Padding(0, 0, 0, 6)
        _customAcquisitionIntervalMilliseconds.Font = New Font("Segoe UI", 10.5F)
        controls.Controls.Add(Field("Requested acquisition interval", _acquisitionInterval), 0, 0)
        controls.Controls.Add(Field("Custom interval (100–10,000 ms)", _customAcquisitionIntervalMilliseconds), 1, 0)
        _saveAcquisitionButton.Anchor = AnchorStyles.Top Or AnchorStyles.Left
        _saveAcquisitionButton.Margin = New Padding(0, 21, 0, 0)
        _saveAcquisitionButton.Height = _customAcquisitionIntervalMilliseconds.PreferredSize.Height
        _customAcquisitionIntervalMilliseconds.Height = _customAcquisitionIntervalMilliseconds.PreferredSize.Height
        controls.Controls.Add(_saveAcquisitionButton, 2, 0)
        _acquisitionMessage.Dock = DockStyle.Fill
        _acquisitionMessage.Padding = New Padding(0, 28, 0, 0)
        _acquisitionMessage.TextAlign = ContentAlignment.TopLeft
        controls.Controls.Add(_acquisitionMessage, 3, 0)
        AddRow(body, controls)
        Dim noteLabel = WrapLabel(
            "The requested interval is an application cycle, not a guaranteed PM5110 update rate. The four FC03 blocks are read in sequence; source freshness cannot be verified without a PLC timestamp. No Modbus writes.",
            New Font("Segoe UI", 8.5F), UiTheme.Amber, body)
        noteLabel.Margin = New Padding(2, 0, 0, 10)
        AddRow(body, noteLabel)

        Dim metrics As New TableLayoutPanel With {
            .Dock = DockStyle.Top,
            .AutoSize = True,
            .ColumnCount = 6,
            .BackColor = Color.Transparent,
            .Margin = New Padding(0, 2, 0, 4)
        }
        For index = 0 To 5
            metrics.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F / 6.0F))
        Next
        AddAcquisitionMetric(metrics, 0, "Requested interval", _requestedIntervalValue)
        AddAcquisitionMetric(metrics, 1, "Actual cycle interval", _actualIntervalValue)
        AddAcquisitionMetric(metrics, 2, "Successful read rate", _validDataRateValue)
        AddAcquisitionMetric(metrics, 3, "PLC response time", _plcResponseValue)
        AddAcquisitionMetric(metrics, 4, "Four-transformer read cycle", _fullCycleValue)
        AddAcquisitionMetric(metrics, 5, "Timeout count", _timeoutCountValue)
        AddAcquisitionMetric(metrics, 6, "Failed cycles", _failedCycleValue)
        AddAcquisitionMetric(metrics, 7, "Last good acquisition", _lastGoodValue)
        AddAcquisitionMetric(metrics, 8, "Source freshness", _freshnessValue)
        AddAcquisitionMetric(metrics, 9, "Historian recording interval", _recordingIntervalValue)
        AddAcquisitionMetric(metrics, 10, "UI display refresh", _displayRefreshValue)
        AddRow(body, metrics)

        ConfigureLiveMeasurementGrid()
        _liveMeasurementGrid.Margin = New Padding(0, 2, 0, 2)
        _liveMeasurementGrid.Dock = DockStyle.Fill
        body.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        body.Controls.Add(_liveMeasurementGrid, 0, body.RowCount)
        body.RowCount += 1
        _liveQualityLabel.Dock = DockStyle.Top
        _liveQualityLabel.Height = 26
        _liveQualityLabel.Font = New Font("Segoe UI", 8.5F, FontStyle.Bold)
        _liveQualityLabel.ForeColor = UiTheme.MutedInk
        _liveQualityLabel.AutoEllipsis = True
        _liveQualityLabel.UseMnemonic = False
        AddFixedRow(body, _liveQualityLabel, 22)
        card.AutoSize = False
        card.Dock = DockStyle.Fill
        card.Margin = Padding.Empty
        card.Padding = New Padding(16, 10, 16, 6)
        body.AutoSize = False
        body.Dock = DockStyle.Fill
        Return card
    End Function

    Private Shared Sub AddAcquisitionMetric(table As TableLayoutPanel, index As Integer, caption As String, value As Label)
        Dim field = New Panel With {
            .Dock = DockStyle.Fill,
            .AutoSize = False,
            .Height = 46,
            .Margin = New Padding(3),
            .Padding = New Padding(8, 3, 6, 3),
            .BackColor = Color.FromArgb(246, 249, 252)
        }
        Dim title = UiTheme.Label(caption.ToUpperInvariant(), New Font("Segoe UI", 7.5F, FontStyle.Bold), UiTheme.MutedInk)
        title.AutoSize = False
        title.Dock = DockStyle.Bottom
        title.Height = 15
        title.TextAlign = ContentAlignment.MiddleLeft
        value.AutoSize = False
        value.Dock = DockStyle.Fill
        value.TextAlign = ContentAlignment.MiddleLeft
        value.Font = New Font("Segoe UI", 9.5F, FontStyle.Bold)
        value.ForeColor = UiTheme.Ink
        value.AutoEllipsis = True
        value.UseMnemonic = False
        field.Controls.Add(value)
        field.Controls.Add(title)
        table.Controls.Add(field, index Mod 6, index \ 6)
    End Sub

    Private Sub ConfigureLiveMeasurementGrid()
        _liveMeasurementGrid.Dock = DockStyle.Fill
        ' DataGridView double-buffering is protected; enable it to stop per-cell flicker on 10 Hz updates.
        GetType(DataGridView).GetProperty("DoubleBuffered", Reflection.BindingFlags.Instance Or Reflection.BindingFlags.NonPublic).
            SetValue(_liveMeasurementGrid, True)
        _liveMeasurementGrid.ReadOnly = True
        _liveMeasurementGrid.TabStop = False
        _liveMeasurementGrid.AllowUserToAddRows = False
        _liveMeasurementGrid.AllowUserToDeleteRows = False
        _liveMeasurementGrid.AllowUserToResizeRows = False
        _liveMeasurementGrid.RowHeadersVisible = False
        _liveMeasurementGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect
        _liveMeasurementGrid.MultiSelect = False
        _liveMeasurementGrid.BackgroundColor = UiTheme.Surface
        _liveMeasurementGrid.BorderStyle = BorderStyle.None
        _liveMeasurementGrid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal
        _liveMeasurementGrid.GridColor = UiTheme.Border
        _liveMeasurementGrid.EnableHeadersVisualStyles = False
        _liveMeasurementGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(235, 243, 249)
        _liveMeasurementGrid.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.Ink
        _liveMeasurementGrid.ColumnHeadersDefaultCellStyle.Font = New Font("Segoe UI", 8.5F, FontStyle.Bold)
        _liveMeasurementGrid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(235, 243, 249)
        _liveMeasurementGrid.ColumnHeadersDefaultCellStyle.SelectionForeColor = UiTheme.Ink
        _liveMeasurementGrid.ColumnHeadersHeight = 28
        _liveMeasurementGrid.RowTemplate.Height = 22
        _liveMeasurementGrid.ScrollBars = ScrollBars.Vertical
        _liveMeasurementGrid.DefaultCellStyle.Font = New Font("Segoe UI", 9.0F)
        _liveMeasurementGrid.DefaultCellStyle.ForeColor = UiTheme.Ink
        _liveMeasurementGrid.DefaultCellStyle.SelectionBackColor = UiTheme.Surface
        _liveMeasurementGrid.DefaultCellStyle.SelectionForeColor = UiTheme.Ink
        _liveMeasurementGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        _liveMeasurementGrid.Columns.Add(New DataGridViewTextBoxColumn With {
            .Name = "Measurement", .HeaderText = "Measurement / unit", .FillWeight = 34,
            .SortMode = DataGridViewColumnSortMode.NotSortable})
        For Each analyzerId In {"T1", "T2", "T3", "T4", "TOTAL"}
            _liveMeasurementGrid.Columns.Add(New DataGridViewTextBoxColumn With {
                .Name = analyzerId, .HeaderText = analyzerId, .FillWeight = If(analyzerId = "TOTAL", 15, 13),
                .SortMode = DataGridViewColumnSortMode.NotSortable})
        Next
        For Each parameter In HistorianReportExporter.GetParameterCatalog("T1").Where(Function(item) Not item.IsWindowStatistic)
            Dim totalSupported = MeasurementCatalog.Find("TOTAL", parameter.Id) IsNot Nothing
            _liveMeasurementGrid.Rows.Add(parameter.Label & " (" & parameter.Unit & ")",
                                          "—", "—", "—", "—", If(totalSupported, "—", "Not derived"))
        Next
        _liveQualityLabel.Text = "Waiting for the first four-transformer acquisition cycle."
    End Sub

    Private Shared Function FormatLiveChannel(value As Double, parameterIndex As Integer) As String
        If Not Double.IsFinite(value) Then Return PlcModbus.MissingValue
        If parameterIndex < MeasurementCatalog.PlcChannelCount Then Return CSng(value).ToString("R", CultureInfo.InvariantCulture)
        Return value.ToString(If(parameterIndex = MeasurementCatalog.PowerFactorIndex, "0.000", "0.##"), CultureInfo.InvariantCulture)
    End Function

    Private Sub AddLiveMeasurementSnapshot(readings As IReadOnlyList(Of MeterSnapshot))
        If readings Is Nothing OrElse readings.Count <> 4 OrElse _liveMeasurementGrid.Rows.Count <> MeasurementCatalog.StoredChannelCount Then Return
        For Each analyzerId In {"T1", "T2", "T3", "T4"}
            Dim snapshot = readings.SingleOrDefault(
                Function(item) item IsNot Nothing AndAlso String.Equals(item.TransformerName, analyzerId, StringComparison.OrdinalIgnoreCase))
            Dim header = analyzerId & " (" & If(snapshot Is Nothing, "Missing", snapshot.Quality.ToString()) & ")"
            Dim column = _liveMeasurementGrid.Columns(analyzerId)
            If Not String.Equals(column.HeaderText, header, StringComparison.Ordinal) Then column.HeaderText = header
            Dim channels = If(snapshot IsNot Nothing AndAlso snapshot.Quality = MeterQuality.Good,
                ElectricalCalculations.TransformerChannels(snapshot.DecodedValues),
                ElectricalCalculations.TransformerChannels(Nothing))
            For parameterIndex = 0 To MeasurementCatalog.StoredChannelCount - 1
                Dim text = FormatLiveChannel(channels(parameterIndex), parameterIndex)
                Dim cell = _liveMeasurementGrid.Rows(parameterIndex).Cells(analyzerId)
                If Not Equals(cell.Value, text) Then cell.Value = text
            Next
        Next
        Dim total = GetAlignedTotalLoad(readings)
        Dim totalHeader = If(total.HasValue, "TOTAL (Calculated)", "TOTAL (Unavailable)")
        If Not String.Equals(_liveMeasurementGrid.Columns("TOTAL").HeaderText, totalHeader, StringComparison.Ordinal) Then
            _liveMeasurementGrid.Columns("TOTAL").HeaderText = totalHeader
        End If
        Dim totalChannels = If(total.HasValue,
            ElectricalCalculations.TotalChannels(readings.Select(Function(item) CType(item.DecodedValues, IReadOnlyList(Of Single))).ToArray()),
            ElectricalCalculations.TotalChannels(Nothing))
        For Each parameter In MeasurementCatalog.GetCatalog("TOTAL").Where(Function(item) Not item.IsWindowStatistic)
            Dim totalText = FormatLiveChannel(totalChannels(parameter.Index), parameter.Index)
            Dim totalCell = _liveMeasurementGrid.Rows(parameter.Index).Cells("TOTAL")
            If Not Equals(totalCell.Value, totalText) Then totalCell.Value = totalText
        Next
        SetText(_liveQualityLabel, String.Join("   •   ", readings.Select(
            Function(item) item.TransformerName & ": " & item.Quality.ToString() &
                If(String.IsNullOrWhiteSpace(item.ErrorMessage), String.Empty, " — " & item.ErrorMessage))))
    End Sub

    Private Shared Function GetAlignedTotalLoad(readings As IReadOnlyList(Of MeterSnapshot)) As Double?
        If readings Is Nothing OrElse readings.Count <> 4 OrElse
           readings.Any(Function(item) item Is Nothing OrElse Not item.CapturedAtUtc.HasValue OrElse
               item.CapturedAtUtc.Value.Kind <> DateTimeKind.Utc) Then Return Nothing
        Dim timestamps = readings.Select(Function(item) item.CapturedAtUtc.Value).ToArray()
        If (timestamps.Max() - timestamps.Min()).TotalMilliseconds > LoadGaugeMath.MaximumTotalAlignmentSkewMilliseconds Then Return Nothing
        Return LoadGaugeMath.CalculateTotalLoad(readings)
    End Function

    Private Const LiveRenderIntervalMilliseconds As Integer = 100
    Private Const DiagnosticsRenderIntervalMilliseconds As Integer = 1000

    Private Shared Sub SetText(target As Label, text As String)
        If Not String.Equals(target.Text, text, StringComparison.Ordinal) Then target.Text = text
    End Sub

    Private Sub AddAcquisitionSnapshot(status As PlcConnectionStatus,
                                       readings As IReadOnlyList(Of MeterSnapshot),
                                       refreshIntervalMilliseconds As Integer,
                                       refreshRateHz As Double)
        _currentAcquisitionStatus = status
        _currentLiveReadings = If(readings, Array.Empty(Of MeterSnapshot)())
        Dim nowTicks = Stopwatch.GetTimestamp()
        ' Diagnostics are slow-moving; refresh them at 1 Hz so the status boxes stay visually stable.
        If _lastDiagnosticsRenderTicks = 0 OrElse
           Stopwatch.GetElapsedTime(_lastDiagnosticsRenderTicks, nowTicks).TotalMilliseconds >= DiagnosticsRenderIntervalMilliseconds Then
            _lastDiagnosticsRenderTicks = nowTicks
            RenderAcquisitionDiagnostics(status)
        End If
        AddLiveMeasurementSnapshot(_currentLiveReadings)
    End Sub

    Private _lastDiagnostics As AcquisitionDiagnosticsSnapshot

    Private Sub RenderAcquisitionDiagnostics(status As PlcConnectionStatus)
        ' Keep the last known diagnostics when a status carries none, so the boxes never fall back to placeholders between cycles.
        If status IsNot Nothing AndAlso status.Diagnostics IsNot Nothing Then _lastDiagnostics = status.Diagnostics
        Dim diagnostics = _lastDiagnostics
        SetText(_requestedIntervalValue, If(status IsNot Nothing AndAlso status.RequestedIntervalMilliseconds > 0,
            FormatInterval(status.RequestedIntervalMilliseconds),
            FormatInterval(_plcSettings.EffectivePollingIntervalMilliseconds)))
        SetText(_actualIntervalValue, If(diagnostics IsNot Nothing AndAlso diagnostics.AverageCycleIntervalMilliseconds.HasValue,
            diagnostics.AverageCycleIntervalMilliseconds.Value.ToString("0", CultureInfo.InvariantCulture) & " ms mean",
            "Measuring…"))
        SetText(_validDataRateValue, If(diagnostics IsNot Nothing AndAlso diagnostics.SuccessfulCycleRateHz.HasValue,
            diagnostics.SuccessfulCycleRateHz.Value.ToString("0.0", CultureInfo.InvariantCulture) & " reads/s",
            "Measuring…"))
        Dim responseTimes = _currentLiveReadings.Where(Function(item) item IsNot Nothing AndAlso
            item.Quality = MeterQuality.Good AndAlso item.ResponseLatencyMilliseconds.HasValue).
            Select(Function(item) item.ResponseLatencyMilliseconds.Value).ToArray()
        SetText(_plcResponseValue, If(responseTimes.Length > 0,
            responseTimes.Average().ToString("0.0", CultureInfo.InvariantCulture) & " ms per block",
            "No valid response"))
        SetText(_fullCycleValue, If(diagnostics IsNot Nothing AndAlso diagnostics.LastCycleDurationMilliseconds.HasValue,
            diagnostics.LastCycleDurationMilliseconds.Value.ToString("0.0", CultureInfo.InvariantCulture) & " ms",
            "Not measured"))
        SetText(_timeoutCountValue, If(diagnostics Is Nothing, "0", diagnostics.TimeoutCount.ToString("N0", CultureInfo.InvariantCulture)))
        SetText(_failedCycleValue, If(diagnostics Is Nothing, "0", diagnostics.FailedCycles.ToString("N0", CultureInfo.InvariantCulture)))
        SetText(_lastGoodValue, If(diagnostics IsNot Nothing AndAlso diagnostics.LastSuccessfulCycleCompletedUtc.HasValue,
            diagnostics.LastSuccessfulCycleCompletedUtc.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
            "Not yet"))
        SetText(_freshnessValue, "Unavailable — no source timestamp/counter")
        SetText(_recordingIntervalValue, If(_settings.ContinuousRawRecordingEnabled,
            FormatInterval(_settings.HistorianRecordingIntervalMilliseconds),
            "Paused"))
        SetText(_displayRefreshValue, "0.1 s values · 1 s status")
    End Sub

    Private Shared Function FormatInterval(milliseconds As Integer) As String
        If milliseconds Mod 1000 = 0 Then Return (milliseconds \ 1000).ToString(CultureInfo.InvariantCulture) & " s"
        Return milliseconds.ToString(CultureInfo.InvariantCulture) & " ms"
    End Function

    Public Sub SetAcquisitionSnapshot(status As PlcConnectionStatus,
                                      readings As IReadOnlyList(Of MeterSnapshot),
                                      refreshIntervalMilliseconds As Integer,
                                      refreshRateHz As Double)
        _uiRefreshIntervalMilliseconds = refreshIntervalMilliseconds
        _actualUiRefreshRateHz = refreshRateHz
        _currentAcquisitionStatus = status
        _currentLiveReadings = If(readings, Array.Empty(Of MeterSnapshot)())
        ' Hidden tabs are not painted; they are re-rendered when selected.
        If _pageActive AndAlso _tabs.SelectedIndex <> 0 Then Return
        Dim nowTicks = Stopwatch.GetTimestamp()
        If _lastLiveRenderTicks <> 0 AndAlso
           Stopwatch.GetElapsedTime(_lastLiveRenderTicks, nowTicks).TotalMilliseconds < LiveRenderIntervalMilliseconds Then Return
        _lastLiveRenderTicks = nowTicks
        AddAcquisitionSnapshot(status, readings, refreshIntervalMilliseconds, refreshRateHz)
    End Sub

    Public Sub UpdateAcquisitionSettings(settings As PlcSettings,
                                         refreshIntervalMilliseconds As Integer,
                                         status As PlcConnectionStatus,
                                         readings As IReadOnlyList(Of MeterSnapshot))
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        _plcSettings = settings.Clone()
        _plcSettings.Normalize()
        _uiRefreshIntervalMilliseconds = refreshIntervalMilliseconds
        SelectAcquisitionInterval(_plcSettings.EffectivePollingIntervalMilliseconds)
        UpdateRetentionHint()
        RefreshResolutionTierStates()
        _lastDiagnosticsRenderTicks = 0
        _lastDiagnostics = Nothing
        AddAcquisitionSnapshot(status, readings, refreshIntervalMilliseconds, _actualUiRefreshRateHz)
        _acquisitionDirty = False
        _acquisitionMessage.Text = "Acquisition settings saved."
    End Sub

    Private Sub SelectAcquisitionInterval(milliseconds As Integer)
        Dim presets = {100, 250, 500, 1000, 2000, 5000, 10000}
        Dim presetIndex = Array.IndexOf(presets, milliseconds)
        _loading = True
        Try
            _customAcquisitionIntervalMilliseconds.Value = Math.Min(
                _customAcquisitionIntervalMilliseconds.Maximum,
                Math.Max(_customAcquisitionIntervalMilliseconds.Minimum, milliseconds))
            _acquisitionInterval.SelectedIndex = If(presetIndex >= 0, presetIndex, 7)
            _customAcquisitionIntervalMilliseconds.Enabled = _acquisitionInterval.SelectedIndex = 7 AndAlso _canEdit AndAlso Not _savingAcquisition
        Finally
            _loading = False
        End Try
    End Sub

    Private Sub OnAcquisitionIntervalChanged(sender As Object, e As EventArgs)
        _customAcquisitionIntervalMilliseconds.Enabled = _acquisitionInterval.SelectedIndex = 7 AndAlso _canEdit AndAlso Not _savingAcquisition
        If Not _loading Then SetAcquisitionDirty()
    End Sub

    Private Sub SetAcquisitionDirty()
        _acquisitionDirty = True
        _acquisitionMessage.ForeColor = UiTheme.Amber
        _acquisitionMessage.Text = "Unsaved acquisition interval."
        _saveAcquisitionButton.Enabled = _canEdit AndAlso _savePlcSettings IsNot Nothing AndAlso Not _savingAcquisition
    End Sub

    Private Async Sub OnSaveAcquisitionClicked(sender As Object, e As EventArgs)
        If Not _canEdit OrElse _savePlcSettings Is Nothing OrElse _savingAcquisition Then Return
        Dim intervalMilliseconds As Integer
        If _acquisitionInterval.SelectedIndex >= 0 AndAlso _acquisitionInterval.SelectedIndex <= 6 Then
            intervalMilliseconds = {100, 250, 500, 1000, 2000, 5000, 10000}(_acquisitionInterval.SelectedIndex)
        ElseIf _acquisitionInterval.SelectedIndex = 7 Then
            intervalMilliseconds = CInt(_customAcquisitionIntervalMilliseconds.Value)
        Else
            ShowMessage("Select a supported PLC acquisition interval.", UiTheme.Red)
            Return
        End If
        Dim candidate = _plcSettings.Clone()
        candidate.RequestedAcquisitionIntervalMilliseconds = intervalMilliseconds
        candidate.PollingIntervalSeconds = CInt(Math.Ceiling(intervalMilliseconds / 1000.0R))
        Try
            candidate.Validate()
            _savingAcquisition = True
            _saveAcquisitionButton.Enabled = False
            _acquisitionInterval.Enabled = False
            _customAcquisitionIntervalMilliseconds.Enabled = False
            _acquisitionMessage.ForeColor = UiTheme.MutedInk
            _acquisitionMessage.Text = "Saving and applying requested interval…"
            Await _savePlcSettings(candidate, _uiRefreshIntervalMilliseconds)
            If IsDisposed Then Return
            _plcSettings = candidate
            _acquisitionDirty = False
            UpdateRetentionHint()
            _acquisitionMessage.ForeColor = UiTheme.Green
            _acquisitionMessage.Text = "Saved. Physical meter freshness remains unverified."
        Catch ex As InvalidDataException
            ShowAcquisitionSaveError(ex)
        Catch ex As UnauthorizedAccessException
            ShowAcquisitionSaveError(ex)
        Catch ex As IOException
            ShowAcquisitionSaveError(ex)
        Catch ex As ArgumentException
            ShowAcquisitionSaveError(ex)
        Catch ex As InvalidOperationException
            ShowAcquisitionSaveError(ex)
        Catch ex As System.Security.SecurityException
            ShowAcquisitionSaveError(ex)
        Catch ex As System.Text.Json.JsonException
            ShowAcquisitionSaveError(ex)
        Catch ex As Microsoft.Data.Sqlite.SqliteException
            ShowAcquisitionSaveError(ex)
        Catch ex As OperationCanceledException
            ShowAcquisitionSaveError(ex)
        Finally
            _savingAcquisition = False
            If Not IsDisposed Then
                _saveAcquisitionButton.Enabled = _canEdit AndAlso _savePlcSettings IsNot Nothing
                _acquisitionInterval.Enabled = _canEdit
                _customAcquisitionIntervalMilliseconds.Enabled = _canEdit AndAlso _acquisitionInterval.SelectedIndex = 7
            End If
        End Try
    End Sub

    Private Sub ShowAcquisitionSaveError(ex As Exception)
        If Not IsDisposed Then
            _acquisitionMessage.ForeColor = UiTheme.Red
            _acquisitionMessage.Text = "Not applied: " & ex.Message
        End If
    End Sub

    Private Function BuildJobsCard() As Control
        Dim body As TableLayoutPanel = Nothing
        Dim card = NewCard("RECENT AUTOMATIC EXPORT JOBS", body, Icons.List)
        Dim jobsPanel As New Panel With {
            .Dock = DockStyle.Fill,
            .MinimumSize = New Size(0, 120),
            .BackColor = Color.Transparent
        }
        ConfigureJobsGrid()
        Dim toolbar As New FlowLayoutPanel With {
            .Dock = DockStyle.Bottom, .Height = 42, .FlowDirection = FlowDirection.LeftToRight,
            .WrapContents = False, .BackColor = Color.Transparent, .Padding = New Padding(0, 6, 0, 0)}
        Dim refresh = MakeSmallButton("Refresh", Icons.Refresh, 82)
        Dim openReport = MakeSmallButton("Open Report", Icons.Document, 112)
        Dim details = MakeSmallButton("View Details", Icons.Info, 110)
        AddHandler refresh.Click, Async Sub(sender, e) Await RefreshHealthAsync()
        AddHandler openReport.Click, AddressOf OnOpenSelectedJobReport
        AddHandler details.Click, AddressOf OnViewJobDetails
        toolbar.Controls.AddRange({refresh, openReport, details})
        jobsPanel.Controls.Add(_jobsGrid)
        jobsPanel.Controls.Add(toolbar)
        card.AutoSize = False
        card.Margin = New Padding(0, 12, 0, 0)
        body.Dock = DockStyle.Fill
        body.AutoSize = False
        body.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        jobsPanel.Dock = DockStyle.Fill
        body.Controls.Add(jobsPanel, 0, body.RowCount)
        body.RowCount += 1
        Return card
    End Function

    Private Shared Function MakeSmallButton(text As String, glyph As String, width As Integer) As IconButton
        Dim result = NewButton(text, glyph, False)
        result.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        result.Size = New Size(width, 32)
        result.Margin = New Padding(0, 0, 6, 0)
        Return result
    End Function

    Private Sub ConfigureJobsGrid()
        _jobsGrid.Dock = DockStyle.Fill
        _jobsGrid.ReadOnly = True
        _jobsGrid.AllowUserToAddRows = False
        _jobsGrid.AllowUserToDeleteRows = False
        _jobsGrid.AllowUserToResizeRows = False
        _jobsGrid.RowHeadersVisible = False
        _jobsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect
        _jobsGrid.MultiSelect = False
        _jobsGrid.BackgroundColor = UiTheme.Surface
        _jobsGrid.BorderStyle = BorderStyle.None
        _jobsGrid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal
        _jobsGrid.GridColor = UiTheme.Border
        _jobsGrid.EnableHeadersVisualStyles = False
        _jobsGrid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None
        _jobsGrid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(235, 243, 249)
        _jobsGrid.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.Ink
        _jobsGrid.ColumnHeadersDefaultCellStyle.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        _jobsGrid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(235, 243, 249)
        _jobsGrid.ColumnHeadersDefaultCellStyle.Padding = New Padding(7, 0, 0, 0)
        _jobsGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
        _jobsGrid.ColumnHeadersHeight = 34
        _jobsGrid.RowTemplate.Height = 34
        _jobsGrid.DefaultCellStyle.Font = New Font("Segoe UI", 9.0F)
        _jobsGrid.DefaultCellStyle.ForeColor = UiTheme.Ink
        _jobsGrid.DefaultCellStyle.Padding = New Padding(7, 0, 0, 0)
        _jobsGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(224, 238, 252)
        _jobsGrid.DefaultCellStyle.SelectionForeColor = UiTheme.Ink
        _jobsGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        _jobsGrid.ShowCellToolTips = True
        AddJobColumn("DateTime", "Date & Time", 24)
        AddJobColumn("Analyzer", "Analyzer", 20)
        AddJobColumn("Status", "Status", 18)
        AddJobColumn("FileName", "File Path", 38)
        AddJobColumn("Size", "Size", 10)
        AddJobColumn("Error", "Error details", 18)
        _jobsGrid.Columns("Size").Visible = False
        _jobsGrid.Columns("Error").Visible = False
        AddHandler _jobsGrid.CellDoubleClick, AddressOf OnOpenSelectedJobReport
    End Sub

    Private Sub AddJobColumn(name As String, header As String, weight As Single)
        _jobsGrid.Columns.Add(New DataGridViewTextBoxColumn With {
            .Name = name, .HeaderText = header, .FillWeight = weight, .SortMode = DataGridViewColumnSortMode.NotSortable})
    End Sub

    Private Function BuildPolicyCard() As Control
        Dim body As TableLayoutPanel = Nothing
        Dim card = NewCard("AUTOMATIC DAILY EXCEL EXPORT", body, Icons.Settings)

        Dim grid As New TableLayoutPanel With {
            .Dock = DockStyle.Top, .AutoSize = True, .ColumnCount = 3, .BackColor = Color.Transparent}
        grid.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 40.0F))
        grid.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 35.0F))
        grid.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 25.0F))

        Dim automatic As New TableLayoutPanel With {
            .Dock = DockStyle.Fill, .AutoSize = True, .ColumnCount = 1, .BackColor = Color.Transparent, .Margin = New Padding(0, 0, 12, 4)}
        automatic.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        _dailyExcel.Text = "Enable daily automatic export"
        _dailyExcel.Font = New Font("Segoe UI", 10.0F, FontStyle.Bold)
        _dailyExcel.ForeColor = UiTheme.Ink
        _dailyExcel.AutoSize = True
        _dailyExcel.Margin = New Padding(0, 14, 0, 0)
        automatic.Controls.Add(_dailyExcel, 0, 0)
        Dim automaticNote = UiTheme.Label("Exports each completed factory day to Excel. Independent of raw recording.",
                                          New Font("Segoe UI", 8.5F), UiTheme.MutedInk)
        automaticNote.MaximumSize = New Size(380, 0)
        automaticNote.Margin = New Padding(22, 0, 0, 0)
        automatic.Controls.Add(automaticNote, 0, 1)
        grid.Controls.Add(automatic, 0, 0)

        _timezone.DropDownStyle = ComboBoxStyle.DropDownList
        StyleInput(_timezone)
        Dim zoneIds = TimeZoneInfo.GetSystemTimeZones().Select(Function(zone) zone.Id).ToList()
        If Not zoneIds.Contains(_settings.FactoryTimeZoneId, StringComparer.OrdinalIgnoreCase) Then zoneIds.Insert(0, _settings.FactoryTimeZoneId)
        If Not zoneIds.Contains("Africa/Accra", StringComparer.OrdinalIgnoreCase) Then zoneIds.Insert(0, "Africa/Accra")
        _timezone.Items.AddRange(zoneIds.Cast(Of Object)().ToArray())
        grid.Controls.Add(Field("Factory timezone", _timezone), 1, 0)

        _exportTime.Format = DateTimePickerFormat.Custom
        _exportTime.CustomFormat = "HH:mm"
        _exportTime.ShowUpDown = True
        StyleInput(_exportTime)
        grid.Controls.Add(Field("Daily export time (factory time)", _exportTime), 2, 0)
        AddRow(body, grid)
        AddRow(body, WrapLabel("Recording interval and raw data retention are set on the Raw Recording tab.",
                               New Font("Segoe UI", 8.5F), UiTheme.MutedInk, body))

        Dim folderCaption = UiTheme.Label("Daily Report Folder (relative path uses this user's documents)",
                                          New Font("Segoe UI", 8.5F), UiTheme.Ink)
        folderCaption.Margin = New Padding(0, 6, 0, 3)
        AddRow(body, folderCaption)
        Dim folderRow As New TableLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .ColumnCount = 3, .BackColor = Color.Transparent}
        folderRow.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        folderRow.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 112.0F))
        folderRow.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 112.0F))
        StyleInput(_exportFolder)
        _exportFolder.Dock = DockStyle.Fill
        _exportFolder.Margin = New Padding(0, 3, 8, 0)
        Dim browse = MakeSmallButton("Browse...", Icons.Folder, 104)
        browse.Dock = DockStyle.Fill
        browse.Name = "BrowseFolder"
        Dim openFolder = MakeSmallButton("Open Folder", Icons.Folder, 112)
        openFolder.Dock = DockStyle.Fill
        AddHandler browse.Click, AddressOf OnBrowseExportFolderClicked
        AddHandler openFolder.Click, AddressOf OnOpenRecordsFolder
        folderRow.Controls.Add(_exportFolder, 0, 0)
        folderRow.Controls.Add(browse, 1, 0)
        folderRow.Controls.Add(openFolder, 2, 0)
        AddRow(body, folderRow)
        AddRow(body, WrapLabel("Default: Documents\EOEMS_SCADA\Records. Relative paths follow the signed-in Windows user on each PC. " &
                               "Open Folder opens exactly this folder; the same folder is used for the automatic export table below. No report is deleted automatically.",
                               New Font("Segoe UI", 8.5F), UiTheme.MutedInk, body))

        Return card
    End Function

    Private Function BuildRawRecordingCard() As Control
        Dim body As TableLayoutPanel = Nothing
        Dim card = NewCard("CONTINUOUS RAW RECORDING", body, Icons.Database)
        Dim options As New TableLayoutPanel With {
            .Dock = DockStyle.Top,
            .AutoSize = True,
            .ColumnCount = 2,
            .BackColor = Color.Transparent
        }
        options.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50.0F))
        options.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50.0F))
        _continuousRawRecording.Text = "Continuous recording enabled"
        _continuousRawRecording.AutoSize = True
        _continuousRawRecording.Font = New Font("Segoe UI", 10.0F, FontStyle.Bold)
        _continuousRawRecording.ForeColor = UiTheme.Ink
        _continuousRawRecording.Margin = New Padding(0, 12, 12, 4)
        AddHandler _continuousRawRecording.CheckedChanged,
            Sub(sender, e)
                If Not _loading Then SetDirty(True)
            End Sub

        options.Controls.Add(_continuousRawRecording, 0, 0)
        Dim intervalNote = UiTheme.Label("Choose 0.1–10 s; plan 3–90 days. All acquired readings stay saved.",
                                         New Font("Segoe UI", 9.0F, FontStyle.Bold), UiTheme.MutedInk)
        intervalNote.Margin = New Padding(0, 14, 0, 0)
        options.Controls.Add(intervalNote, 1, 0)
        AddRow(body, options)

        Dim recordingGrid As New TableLayoutPanel With {
            .Dock = DockStyle.Top, .AutoSize = True, .ColumnCount = 2,
            .Padding = New Padding(0, 0, 0, 10), .BackColor = Color.Transparent}
        recordingGrid.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50.0F))
        recordingGrid.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50.0F))
        _intervalCombo.DropDownStyle = ComboBoxStyle.DropDownList
        StyleInput(_intervalCombo)
        For Each milliseconds In RecordingSettings.StandardRecordingIntervalsMilliseconds
            _intervalChoicesMs.Add(milliseconds)
            _intervalCombo.Items.Add(FormatInterval(milliseconds))
        Next
        AddHandler _intervalCombo.SelectedIndexChanged, AddressOf OnIntervalChanged
        Dim intervalField = Field("Raw recording interval", _intervalCombo)
        intervalField.Padding = New Padding(0, 0, 0, 12)
        recordingGrid.Controls.Add(intervalField, 0, 0)

        _retentionDays.Minimum = RecordingSettings.MinimumPlanningWindowDays
        _retentionDays.Maximum = RecordingSettings.MaximumRetentionDaysFor(RecordingSettings.MinimumRecordingIntervalMilliseconds)
        StyleInput(_retentionDays)
        AddHandler _retentionDays.ValueChanged,
            Sub(sender, e)
                UpdateRetentionHint()
                RefreshResolutionTierStates()
            End Sub
        Dim daysField = Field("Planning window (days; no deletion)", _retentionDays)
        daysField.Padding = New Padding(0, 0, 0, 12)
        recordingGrid.Controls.Add(daysField, 1, 0)
        AddRow(body, recordingGrid)
        _retentionHint.AutoSize = True
        _retentionHint.MaximumSize = New Size(1100, 0)
        _retentionHint.Font = New Font("Segoe UI", 9.0F)
        _retentionHint.ForeColor = UiTheme.MutedInk
        _retentionHint.Margin = New Padding(0, 2, 0, 6)
        _retentionHint.UseMnemonic = False
        AddRow(body, _retentionHint)
        AddRow(body, WrapLabel(
            "All acquired readings, quality and alarm evidence stay saved. Planning days never delete data. " &
            "Provision disk capacity and verified PostgreSQL/outbox backups.",
            New Font("Segoe UI", 9.0F), UiTheme.Amber, body))

        _resolutionToggle.Size = New Size(360, 30)
        _resolutionToggle.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        _resolutionToggle.FlatAppearance.BorderSize = 1
        _resolutionToggle.FlatAppearance.BorderColor = BorderNeutral
        _resolutionToggle.Margin = New Padding(0, 8, 0, 4)
        AddHandler _resolutionToggle.Click, AddressOf OnResolutionToggled
        Dim toggleHost As New Panel With {.AutoSize = True, .BackColor = Color.Transparent}
        toggleHost.Controls.Add(_resolutionToggle)
        AddRow(body, toggleHost)
        BuildResolutionPanel()
        AddRow(body, _resolutionPanel)
        _storageEstimateHint.AutoSize = True
        _storageEstimateHint.MaximumSize = New Size(1100, 0)
        _storageEstimateHint.Font = New Font("Segoe UI", 8.5F)
        _storageEstimateHint.ForeColor = UiTheme.MutedInk
        _storageEstimateHint.Margin = New Padding(0, 6, 0, 4)
        _storageEstimateHint.UseMnemonic = False
        AddRow(body, _storageEstimateHint)
        Return card
    End Function

    Private Sub ApplyRawRecordingSettings()
        _loading = True
        Try
            _continuousRawRecording.Checked = _settings.ContinuousRawRecordingEnabled
        Finally
            _loading = False
        End Try
    End Sub

    Private Sub BuildResolutionPanel()
        _resolutionPanel.Visible = False
        _resolutionPanel.Height = 334
        _resolutionPanel.BackColor = Color.Transparent
        _resolutionSummary.Dock = DockStyle.Top
        _resolutionSummary.Height = 54
        _resolutionSummary.Font = New Font("Segoe UI", 9.0F)
        _resolutionSummary.ForeColor = UiTheme.Ink
        _resolutionSummary.UseMnemonic = False
        _resolutionGrid.Dock = DockStyle.Fill
        _resolutionGrid.ReadOnly = True
        _resolutionGrid.AllowUserToAddRows = False
        _resolutionGrid.AllowUserToDeleteRows = False
        _resolutionGrid.AllowUserToResizeColumns = False
        _resolutionGrid.AllowUserToResizeRows = False
        _resolutionGrid.MultiSelect = False
        _resolutionGrid.RowHeadersVisible = False
        _resolutionGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect
        _resolutionGrid.BackgroundColor = UiTheme.Surface
        _resolutionGrid.BorderStyle = BorderStyle.None
        _resolutionGrid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal
        _resolutionGrid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None
        _resolutionGrid.GridColor = UiTheme.Border
        _resolutionGrid.EnableHeadersVisualStyles = False
        _resolutionGrid.ColumnHeadersDefaultCellStyle.BackColor = Icons.SidebarNavy
        _resolutionGrid.ColumnHeadersDefaultCellStyle.ForeColor = Color.White
        _resolutionGrid.ColumnHeadersDefaultCellStyle.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        _resolutionGrid.ColumnHeadersDefaultCellStyle.Padding = New Padding(8, 0, 8, 0)
        _resolutionGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
        _resolutionGrid.ColumnHeadersHeight = 34
        _resolutionGrid.RowTemplate.Height = 30
        _resolutionGrid.DefaultCellStyle.Font = New Font("Segoe UI", 9.0F)
        _resolutionGrid.DefaultCellStyle.ForeColor = UiTheme.Ink
        _resolutionGrid.DefaultCellStyle.Padding = New Padding(8, 0, 8, 0)
        _resolutionGrid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(222, 238, 250)
        _resolutionGrid.DefaultCellStyle.SelectionForeColor = UiTheme.Ink
        _resolutionGrid.AlternatingRowsDefaultCellStyle.BackColor = UiTheme.SurfaceMuted
        _resolutionGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        _resolutionGrid.ScrollBars = ScrollBars.None
        _resolutionGrid.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "Interval", .HeaderText = "RECORDING INTERVAL", .FillWeight = 16})
        _resolutionGrid.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "MaximumRetention", .HeaderText = "MAX. PLANNING DAYS", .FillWeight = 16})
        _resolutionGrid.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "SelectedSize", .HeaderText = "APPROX. SIZE / CHOSEN DAYS", .FillWeight = 25})
        _resolutionGrid.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "WorstCase", .HeaderText = "APPROX. SIZE / MAX. DAYS", .FillWeight = 25})
        _resolutionGrid.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "Selection", .HeaderText = "POLICY STATUS", .FillWeight = 18})
        RefreshResolutionTierStates()
        _resolutionPanel.Controls.Add(_resolutionGrid)
        _resolutionPanel.Controls.Add(_resolutionSummary)
    End Sub

    Private Sub RefreshResolutionTierStates()
        If _resolutionGrid.Columns.Count = 0 Then Return
        _resolutionGrid.Rows.Clear()
        Dim intervals = RecordingSettings.StandardRecordingIntervalsMilliseconds.ToList()
        Dim currentInterval = SelectedIntervalMilliseconds()
        If Not intervals.Contains(currentInterval) Then intervals.Add(currentInterval)
        intervals.Sort()
        For Each interval In intervals
            Dim isCurrent = interval = currentInterval
            Dim retention = If(isCurrent, "Selected interval", "Available")
            Dim maximumDays = RecordingSettings.MaximumRetentionDaysFor(interval)
            Dim days = Math.Min(CInt(_retentionDays.Value), maximumDays)
            Dim index = _resolutionGrid.Rows.Add(
                FormatPolicyInterval(interval),
                maximumDays.ToString(CultureInfo.InvariantCulture) & " days",
                FormatStorageEstimate(interval, days) & " / " & days.ToString(CultureInfo.InvariantCulture) & " d",
                FormatStorageEstimate(interval, maximumDays),
                retention)
            Dim row = _resolutionGrid.Rows(index)
            If isCurrent Then
                row.DefaultCellStyle.BackColor = Color.FromArgb(225, 242, 233)
                row.Cells("Selection").Style.ForeColor = UiTheme.Green
                row.Cells("Selection").Style.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
            End If
        Next
        _resolutionGrid.ClearSelection()
        Dim rateDescription = If(_storageEstimate IsNot Nothing AndAlso _storageEstimate.UsesObservedRates,
            "Recent sustained row cadence (median spacing) and average PostgreSQL row sizes from up to 64 rows per asset/table; app downtime does not reduce the forecast.",
            "Insufficient recent rows for a rate sample; configured PLC/UPS polling is used as a conservative fallback.")
        _storageEstimateHint.Text = If(_storageEstimate Is Nothing, "Storage estimate unavailable: " & _storageEstimateError,
            "Planning estimate for " & _storageEstimate.EquipmentCount.ToString(CultureInfo.InvariantCulture) & " equipment entries: " &
            rateDescription & " Transformer growth scales with the selected recording interval (never faster than the measured PLC cycle); UPS growth stays at the observed rate. " &
            "Includes 2x headroom for indexes and growth; excludes existing history, WAL, backups, exports and offline outbox. Not a guaranteed worst-case bound.")
    End Sub

    Private Function FormatStorageEstimate(interval As Integer, days As Integer) As String
        If _storageEstimate Is Nothing Then Return "Unavailable"
        Dim bytes = _storageEstimate.EstimateBytes(interval, days, _plcSettings.EffectivePollingIntervalMilliseconds)
        Return "~" & HistorianHealthEvaluator.FormatBytes(CLng(Math.Ceiling(bytes)))
    End Function

    Private Shared Function FormatPolicyInterval(milliseconds As Integer) As String
        Return (milliseconds / 1000.0R).ToString("0.##", CultureInfo.InvariantCulture) & " s"
    End Function




    Private Function BuildMeasurementPanel() As Control
        Dim panel As New RoundedPanel With {
            .Dock = DockStyle.Fill,
            .BackColor = UiTheme.Surface,
            .BorderColor = UiTheme.Border,
            .Radius = 12,
            .Padding = New Padding(8),
            .Margin = New Padding(3)}
        _measurementHeading.Dock = DockStyle.Top
        _measurementHeading.Height = 24
        _measurementHeading.Font = New Font("Segoe UI", 9.5F, FontStyle.Bold)
        _measurementHeading.ForeColor = Icons.AccentBlue
        _measurementHeading.UseMnemonic = False

        Dim tools As New TableLayoutPanel With {.Dock = DockStyle.Top, .Height = 34, .ColumnCount = 4, .BackColor = Color.Transparent}
        tools.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        tools.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 74.0F))
        tools.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 74.0F))
        tools.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 74.0F))
        _search.PlaceholderText = "Search parameters"
        StyleInput(_search)
        _search.Dock = DockStyle.Fill
        _search.Margin = New Padding(0, 3, 6, 0)
        AddHandler _search.TextChanged, Sub(sender, e) RebuildParameterList()
        Dim selectAll = MakeSmallButton("All", Icons.CheckCircle, 68)
        Dim selectNone = MakeSmallButton("None", Icons.Cancel, 68)
        _groupByCategory.Text = "Group"
        _groupByCategory.Checked = True
        _groupByCategory.AutoSize = True
        _groupByCategory.Font = New Font("Segoe UI", 9.0F)
        _groupByCategory.Margin = New Padding(4, 8, 0, 0)
        selectAll.Dock = DockStyle.Fill
        selectNone.Dock = DockStyle.Fill
        selectAll.Margin = New Padding(0, 2, 4, 2)
        selectNone.Margin = New Padding(0, 2, 4, 2)
        AddHandler selectAll.Click, Sub(sender, e) SetAllParameters(True)
        AddHandler selectNone.Click, Sub(sender, e) SetAllParameters(False)
        AddHandler _groupByCategory.CheckedChanged, Sub(sender, e) _parameters.ShowGroups = _groupByCategory.Checked
        tools.Controls.Add(_search, 0, 0)
        tools.Controls.Add(selectAll, 1, 0)
        tools.Controls.Add(selectNone, 2, 0)
        tools.Controls.Add(_groupByCategory, 3, 0)

        _parameters.Dock = DockStyle.Fill
        _parameters.View = View.Details
        _parameters.CheckBoxes = True
        _parameters.FullRowSelect = True
        _parameters.HideSelection = False
        _parameters.ShowGroups = True
        _parameters.HeaderStyle = ColumnHeaderStyle.Nonclickable
        _parameters.BackColor = UiTheme.SurfaceMuted
        _parameters.ForeColor = UiTheme.Ink
        _parameters.Font = New Font("Segoe UI", 10.0F)
        _parameters.BorderStyle = BorderStyle.FixedSingle
        _parameters.Columns.Add("Measurement", 190)
        _parameters.Columns.Add("Unit", 78)
        _parameters.Columns.Add("Source validation", 190)
        AddHandler _parameters.ItemCheck, AddressOf OnParameterItemCheck
        AddHandler _parameters.ItemChecked, AddressOf OnParameterItemChecked
        AddHandler _parameters.Resize, Sub(sender, e) ResizeParameterColumns()
        AddHandler _parameters.HandleCreated, Sub(sender, e) ResizeParameterColumns()
        ResizeParameterColumns()

        Dim unsupported As New RoundedPanel With {
            .Dock = DockStyle.Bottom,
            .Height = 48,
            .Padding = New Padding(7, 3, 7, 3),
            .Radius = 8,
            .BackColor = Color.FromArgb(238, 244, 251),
            .BorderColor = Color.FromArgb(211, 226, 247)
        }
        Dim unsupportedText = UiTheme.Label(
            "Daily Excel columns only; Equipment History raw recording is unchanged. Peaks, average kW and kWh here are calculated over the daily report window, not meter registers.",
            New Font("Segoe UI", 8.0F),
            UiTheme.MutedInk,
            False)
        unsupportedText.Dock = DockStyle.Fill
        unsupportedText.TextAlign = ContentAlignment.MiddleLeft
        unsupportedText.UseMnemonic = False
        unsupported.Controls.Add(unsupportedText)
        panel.Controls.Add(_parameters)
        panel.Controls.Add(unsupported)
        panel.Controls.Add(tools)
        panel.Controls.Add(IconHeading(_measurementHeading, Icons.List))
        Return panel
    End Function

    Private Sub ResizeParameterColumns()
        If _parameters.Columns.Count <> 3 OrElse _parameters.ClientSize.Width <= 0 Then Return
        Dim availableWidth = Math.Max(0, _parameters.ClientSize.Width - 8)
        Dim unitWidth = Math.Min(88, CInt(Math.Round(availableWidth * 0.18R)))
        Dim parameterWidth = CInt(Math.Round(availableWidth * 0.36R))
        _parameters.Columns(0).Width = parameterWidth
        _parameters.Columns(1).Width = unitWidth
        _parameters.Columns(2).Width = Math.Max(0, availableWidth - parameterWidth - unitWidth)
    End Sub



    Private Function BuildFooter() As Control
        Dim body As TableLayoutPanel = Nothing
        Dim card = NewCard(String.Empty, body)
        Dim row As New TableLayoutPanel With {.Dock = DockStyle.Top, .Height = 42, .ColumnCount = 3, .BackColor = Color.Transparent}
        row.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 240.0F))
        row.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 170.0F))
        row.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        _saveButton.Size = New Size(226, 38)
        _saveButton.Margin = New Padding(0)
        AddHandler _saveButton.Click, AddressOf OnSaveClicked
        _dirtyLabel.AutoSize = True
        _dirtyLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        _dirtyLabel.Margin = New Padding(0, 10, 0, 0)
        _message.AutoSize = False
        _message.Dock = DockStyle.Fill
        _message.TextAlign = ContentAlignment.MiddleLeft
        _message.AutoEllipsis = True
        _message.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        _message.ForeColor = UiTheme.MutedInk
        _message.Margin = New Padding(0)
        _message.UseMnemonic = False
        row.Controls.Add(_saveButton, 0, 0)
        row.Controls.Add(_dirtyLabel, 1, 0)
        row.Controls.Add(_message, 2, 0)
        AddRow(body, row)
        Return card
    End Function

    ' ------------------------------------------------------------------ analyzer state

    Private Sub OnAnalyzerSelected(sender As Object, e As EventArgs)
        Dim requested = TryCast(DirectCast(sender, Button).Tag, String)
        SelectAnalyzer(requested)
    End Sub

    Private Sub SelectAnalyzer(requested As String)
        If requested Is Nothing OrElse Not AnalyzerIds.Contains(requested) Then Return
        If String.Equals(requested, _selectedAnalyzer, StringComparison.Ordinal) Then
            ApplyAnalyzerButtonStyles()
            Return
        End If
        _selectedAnalyzer = requested
        ApplyAnalyzerButtonStyles()
        LoadDraft()
    End Sub

    ' Single-select segmented group: exactly one analyzer carries the selected style.
    Private Sub ApplyAnalyzerButtonStyles()
        For Each entry In _analyzerButtons
            Dim selected = String.Equals(entry.Key, _selectedAnalyzer, StringComparison.Ordinal)
            Dim actionButton = entry.Value
            actionButton.UseVisualStyleBackColor = False
            actionButton.BackColor = If(selected, Icons.AccentBlue, Color.White)
            actionButton.ForeColor = If(selected, Color.White, UiTheme.Ink)
            actionButton.Font = New Font("Segoe UI", 9.5F, If(selected, FontStyle.Bold, FontStyle.Regular))
            actionButton.FlatAppearance.BorderSize = 1
            actionButton.FlatAppearance.BorderColor = If(selected, Icons.AccentBlue, Icons.HairLine)
            actionButton.FlatAppearance.MouseOverBackColor = If(selected, Icons.AccentBlue, Color.FromArgb(238, 243, 249))
            actionButton.FlatAppearance.MouseDownBackColor = If(selected, Icons.AccentBlueDark, Color.FromArgb(222, 233, 245))
            actionButton.Invalidate()
        Next
    End Sub


    Private Sub LoadDraft()
        _loading = True
        Try
            ApplyEditability()
            RebuildParameterList()
        Finally
            _loading = False
        End Try
        UpdateMeasurementHeading()
    End Sub


    Private Sub ApplyEditability()
        _dailyExcel.Enabled = _canEdit
        _timezone.Enabled = _canEdit
        _exportTime.Enabled = _canEdit
        _retentionDays.Enabled = _canEdit
        _continuousRawRecording.Enabled = _canEdit
        _intervalCombo.Enabled = _canEdit
        _acquisitionInterval.Enabled = _canEdit AndAlso Not _savingAcquisition
        _customAcquisitionIntervalMilliseconds.Enabled = _canEdit AndAlso _acquisitionInterval.SelectedIndex = 7 AndAlso Not _savingAcquisition
        _saveAcquisitionButton.Enabled = _canEdit AndAlso _savePlcSettings IsNot Nothing AndAlso Not _savingAcquisition
        _exportFolder.ReadOnly = Not _canEdit
        _exportFolder.BackColor = If(_canEdit, Color.White, Color.FromArgb(240, 244, 247))
        _saveButton.Enabled = _canEdit AndAlso Not _savingRecording
        For Each control In FindButtons(Me, "BrowseFolder")
            control.Enabled = _canEdit
        Next
        If Not _canEdit Then
            _message.Text = "READ ONLY • Administrator role required to change recording and equipment settings."
            _message.ForeColor = UiTheme.Amber
        End If
    End Sub

    Private Shared Iterator Function FindButtons(parent As Control, name As String) As IEnumerable(Of Button)
        For Each child As Control In parent.Controls
            Dim actionButton = TryCast(child, Button)
            If actionButton IsNot Nothing AndAlso actionButton.Name = name Then Yield actionButton
            For Each nested In FindButtons(child, name)
                Yield nested
            Next
        Next
    End Function


    Private Sub RebuildParameterList()
        Dim previousLoading = _loading
        _loading = True
        Try
            Dim draft = _drafts(_selectedAnalyzer)
            Dim filter = _search.Text.Trim()
            _parameters.BeginUpdate()
            _parameters.Items.Clear()
            _parameters.Groups.Clear()
            Dim groups As New Dictionary(Of String, ListViewGroup)(StringComparer.Ordinal)
            Dim categoryOrder = {"Voltage", "Current", "Power", MeasurementCatalog.StatisticCategory, "Frequency", "Temperature", "Other"}
            Dim catalog = HistorianReportExporter.GetParameterCatalog(_selectedAnalyzer).
                OrderBy(Function(parameter) Array.IndexOf(categoryOrder, HistorianHealthEvaluator.GetParameterCategory(parameter.Id))).
                ThenBy(Function(parameter) parameter.Index)
            For Each parameter In catalog
                Dim category = HistorianHealthEvaluator.GetParameterCategory(parameter.Id)
                If filter.Length > 0 AndAlso
                   parameter.Label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 AndAlso
                   category.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 AndAlso
                   parameter.Id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 Then
                    Continue For
                End If
                Dim group As ListViewGroup = Nothing
                If Not groups.TryGetValue(category, group) Then
                    group = New ListViewGroup(category, category)
                    groups.Add(category, group)
                    _parameters.Groups.Add(group)
                End If
                Dim item As New ListViewItem(parameter.Label) With {
                    .Tag = parameter, .Group = group, .Checked = draft.Selected.Contains(parameter.Id)}
                item.SubItems.Add(parameter.Unit)
                item.SubItems.Add(parameter.Verification)
                _parameters.Items.Add(item)
            Next
            _parameters.ShowGroups = _groupByCategory.Checked
            _parameters.EndUpdate()
        Finally
            _loading = previousLoading
        End Try
        UpdateMeasurementHeading()
    End Sub

    Private Sub OnParameterItemCheck(sender As Object, e As ItemCheckEventArgs)
        If _loading Then Return
        If Not _canEdit Then e.NewValue = e.CurrentValue
    End Sub

    Private Sub OnParameterItemChecked(sender As Object, e As ItemCheckedEventArgs)
        If _loading Then Return
        Dim info = TryCast(e.Item.Tag, RecordingParameterInfo)
        If info Is Nothing Then Return
        Dim draft = _drafts(_selectedAnalyzer)
        Dim changed As Boolean
        If e.Item.Checked Then changed = draft.Selected.Add(info.Id) Else changed = draft.Selected.Remove(info.Id)
        If changed Then
            SetDirty(True)
            UpdateMeasurementHeading()
        End If
    End Sub

    Private Sub SetAllParameters(selected As Boolean)
        If Not _canEdit Then Return
        Dim draft = _drafts(_selectedAnalyzer)
        draft.Selected.Clear()
        If selected Then
            For Each parameter In HistorianReportExporter.GetParameterCatalog(_selectedAnalyzer)
                draft.Selected.Add(parameter.Id)
            Next
        End If
        RebuildParameterList()
        SetDirty(True)
    End Sub

    Private Sub UpdateMeasurementHeading()
        Dim total = HistorianReportExporter.GetParameterCatalog(_selectedAnalyzer).Count
        Dim selected = _drafts(_selectedAnalyzer).Selected.Count
        _measurementHeading.Text = "DAILY EXPORT MEASUREMENTS (" & _selectedAnalyzer & ")  •  " &
            selected.ToString(CultureInfo.InvariantCulture) & " of " & total.ToString(CultureInfo.InvariantCulture) & " • ONLY RECORDED VALUES ARE EXPORTED"
    End Sub

    Private Shared Function IconHeading(text As Label, glyph As String) As Control
        Dim host As New Panel With {.Dock = DockStyle.Top, .Height = 28, .BackColor = Color.Transparent}
        Dim icon = Icons.IconLabel(glyph, 13.0F, Icons.AccentBlue)
        icon.Dock = DockStyle.Left
        icon.Width = 28
        icon.TextAlign = ContentAlignment.MiddleLeft
        text.Dock = DockStyle.Fill
        text.TextAlign = ContentAlignment.MiddleLeft
        host.Controls.Add(text)
        host.Controls.Add(icon)
        Return host
    End Function

    ' ------------------------------------------------------------------ policy

    Private Sub ApplyPolicyFromSettings()
        _dailyExcel.Checked = _settings.AutomaticDailyExcelEnabled
        _timezone.Text = _settings.FactoryTimeZoneId
        _exportTime.Value = DateTime.Today.AddMinutes(Math.Min(1439, Math.Max(0, _settings.DailyExportStabilizationMinutes)))
        Dim wasLoading = _loading
        _loading = True
        Try
            Dim intervalMs = Math.Max(RecordingSettings.MinimumRecordingIntervalMilliseconds,
                                      Math.Min(RecordingSettings.MaximumRecordingIntervalMilliseconds, _settings.HistorianRecordingIntervalMilliseconds))
            Dim index = _intervalChoicesMs.IndexOf(intervalMs)
            If index < 0 Then
                index = _intervalChoicesMs.Count
                _intervalChoicesMs.Add(intervalMs)
                _intervalCombo.Items.Add(FormatPolicyInterval(intervalMs))
            End If
            _intervalCombo.SelectedIndex = index
            UpdateRetentionLimit()
            _retentionDays.Value = Math.Min(_retentionDays.Maximum, Math.Max(_retentionDays.Minimum, _settings.HistorianRetentionDays))
        Finally
            _loading = wasLoading
        End Try
        _exportFolder.Text = _settings.ExportFolderPath
        AddHandler _dailyExcel.CheckedChanged, AddressOf OnPolicyChanged
        AddHandler _timezone.TextChanged, AddressOf OnPolicyChanged
        AddHandler _exportTime.ValueChanged, AddressOf OnPolicyChanged
        AddHandler _retentionDays.ValueChanged, AddressOf OnPolicyChanged
        AddHandler _exportFolder.TextChanged, AddressOf OnPolicyChanged
    End Sub

    Private Function SelectedIntervalMilliseconds() As Integer
        Dim index = _intervalCombo.SelectedIndex
        If index < 0 OrElse index >= _intervalChoicesMs.Count Then Return RecordingSettings.MinimumRecordingIntervalMilliseconds
        Return _intervalChoicesMs(index)
    End Function

    Private Sub UpdateRetentionLimit()
        Dim interval = SelectedIntervalMilliseconds()
        Dim limit = RecordingSettings.MaximumRetentionDaysFor(interval)
        _retentionDays.Maximum = limit
        If _retentionDays.Value > limit Then _retentionDays.Value = limit
        UpdateRetentionHint()
        RefreshResolutionTierStates()
    End Sub

    Private Sub UpdateRetentionHint()
        Dim interval = SelectedIntervalMilliseconds()
        Dim text = "Planning range: " & RecordingSettings.MinimumPlanningWindowDays.ToString(CultureInfo.InvariantCulture) &
            "–" & RecordingSettings.MaximumRetentionDaysFor(interval).ToString(CultureInfo.InvariantCulture) &
            " days at " & FormatPolicyInterval(interval) & ". Value/Min/Max use this saved interval; every intermediate acquisition is preserved."
        Dim acquisition = If(_plcSettings Is Nothing, 0, _plcSettings.EffectivePollingIntervalMilliseconds)
        If acquisition > interval Then
            text &= Environment.NewLine & "Note: live PLC acquisition is " & FormatPolicyInterval(acquisition) &
                ", so saved rows cannot be closer than " & FormatPolicyInterval(acquisition) & ". Adjust Acquisition if faster observations are needed."
            _retentionHint.ForeColor = UiTheme.Amber
        Else
            _retentionHint.ForeColor = UiTheme.MutedInk
        End If
        _retentionHint.Text = text
    End Sub

    Private Sub OnIntervalChanged(sender As Object, e As EventArgs)
        UpdateRetentionLimit()
        If Not _loading Then SetDirty(True)
    End Sub

    Private Sub OnPolicyChanged(sender As Object, e As EventArgs)
        If _loading Then Return
        SetDirty(True)
    End Sub

    Private Sub OnResolutionToggled(sender As Object, e As EventArgs)
        _resolutionPanel.Visible = Not _resolutionPanel.Visible
        _resolutionToggle.Text = If(_resolutionPanel.Visible, "▾", "▸") & "  RECORDING INTERVAL & PLANNING WINDOW"
        If _resolutionPanel.Visible Then
            Call RefreshResolutionAsync()
            Call RefreshStorageEstimateAsync()
        End If
    End Sub

    Private Async Function RefreshStorageEstimateAsync() As Task
        If _refreshingStorageEstimate OrElse IsDisposed Then Return
        If _storageEstimate IsNot Nothing AndAlso DateTime.UtcNow - _storageEstimateCheckedUtc < TimeSpan.FromSeconds(30) Then Return
        _refreshingStorageEstimate = True
        Try
            If Not String.IsNullOrEmpty(UpsSettingsStore.LoadError) Then Throw New InvalidDataException(UpsSettingsStore.LoadError)
            Dim settings = UpsSettingsStore.Current.Clone()
            If Not String.IsNullOrEmpty(UpsSettingsStore.LoadError) Then Throw New InvalidDataException(UpsSettingsStore.LoadError)
            Dim estimate = Await Task.Run(Function() EquipmentRawHistory.ReadStorageEstimate(
                settings, _settings.HistorianRecordingIntervalMilliseconds))
            If IsDisposed Then Return
            _storageEstimate = estimate
            _storageEstimateCheckedUtc = DateTime.UtcNow
        Catch ex As Exception When TypeOf ex Is Npgsql.NpgsqlException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is InvalidOperationException
            If IsDisposed Then Return
            _storageEstimate = Nothing
            _storageEstimateError = ex.Message
        Finally
            _refreshingStorageEstimate = False
        End Try
        If Not IsDisposed Then RefreshResolutionTierStates()
    End Function

    Private Async Function RefreshResolutionAsync() As Task
        Dim selectedInterval = _settings.HistorianRecordingIntervalMilliseconds
        Dim configured = "PLC request: " & FormatInterval(_plcSettings.EffectivePollingIntervalMilliseconds) &
            "  •  Saved recording interval: " & FormatPolicyInterval(selectedInterval) &
            "  •  Full-resolution raw history: no automatic expiry"
        _resolutionSummary.Text = configured & Environment.NewLine & "Measured T1 spacing: measuring…"
        Dim summary As HistorianRangeSummary = Nothing
        Try
            Dim endUtc = DateTime.UtcNow
            Dim startUtc = endUtc.AddHours(-1)
            summary = Await Task.Run(Function() _historian.GetRangeSummary("T1", startUtc, endUtc))
        Catch ex As Exception
            If Not IsDisposed Then _resolutionSummary.Text = configured & Environment.NewLine & "Measured T1 spacing unavailable: " & ex.Message
            Return
        End Try
        If IsDisposed Then Return
        If summary Is Nothing OrElse summary.AvailableSampleCount < 2 OrElse Not summary.FirstSampleUtc.HasValue OrElse
           Not summary.LastSampleUtc.HasValue Then
            _resolutionSummary.Text = configured & Environment.NewLine & "Measured T1 spacing: not enough samples in the last 60 minutes."
            Return
        End If
        Dim span = (summary.LastSampleUtc.Value - summary.FirstSampleUtc.Value).TotalSeconds
        Dim mean = span / (summary.AvailableSampleCount - 1)
        _resolutionSummary.Text = configured & Environment.NewLine &
            "Measured T1 spacing: " & mean.ToString("0.00", CultureInfo.InvariantCulture) &
            " s mean over " & summary.AvailableSampleCount.ToString("N0", CultureInfo.InvariantCulture) &
            " samples. Actual spacing follows successful PLC cycles; missing readings are not filled."
    End Function

    Private Sub OnBrowseExportFolderClicked(sender As Object, e As EventArgs)
        If Not _canEdit Then Return
        Try
            Dim initialDirectory = _settings.ResolveExportRootDirectory()
            Using dialog As New FolderBrowserDialog With {
                .Description = "Choose where automatic daily Excel reports and manually exported reports are saved.",
                .SelectedPath = If(Directory.Exists(initialDirectory),
                                   initialDirectory,
                                   Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                .ShowNewFolderButton = True
            }
                If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
                _exportFolder.Text = _settings.MakePortableExportFolderPath(dialog.SelectedPath)
            End Using
        Catch ex As Exception
            ShowMessage("Could not select the report folder: " & ex.Message, UiTheme.Red)
        End Try
    End Sub

    ' Opens the folder currently shown in the box (what Browse selected), creating it if needed.
    Private Sub OnOpenRecordsFolder(sender As Object, e As EventArgs)
        Try
            Dim folder = New RecordingSettings With {.ExportFolderPath = _exportFolder.Text.Trim()}.ResolveExportRootDirectory()
            Directory.CreateDirectory(folder)
            OpenShell(folder)
        Catch ex As Exception
            ShowMessage("Could not open the records folder: " & ex.Message, UiTheme.Red)
        End Try
    End Sub

    ' ------------------------------------------------------------------ health

    Public Async Function RefreshHealthAsync() As Task
        If IsDisposed OrElse _refreshing Then Return
        _refreshing = True
        Try
            Dim snapshot = Await Task.Run(Function() CollectHealth())
            If IsDisposed Then Return
            ApplyHealth(snapshot)
            If _tabs.SelectedIndex = 1 Then Await RefreshStorageEstimateAsync()
        Catch ex As Exception
            If Not IsDisposed Then
                _healthDetail.ForeColor = UiTheme.Red
                _healthDetail.Text = "Historian diagnostics could not be read: " & ex.Message
            End If
        Finally
            _refreshing = False
        End Try
    End Function

    Private NotInheritable Class HealthSnapshot
        Public Property Diagnostics As HistorianDiagnosticsSnapshot
        Public Property DatabaseBytes As Long = -1
        Public Property Jobs As IReadOnlyList(Of DailyExportJob)
        Public Property SchedulerError As String = String.Empty
        Public Property FileSizes As Dictionary(Of String, Long)
        Public Property FileMetadataErrors As List(Of String)
    End Class

    Private Function CollectHealth() As HealthSnapshot
        Dim result As New HealthSnapshot With {.Diagnostics = _historian.GetDiagnostics()}
        result.DatabaseBytes = _historian.GetDatabaseSizeBytes()
        result.Jobs = _scheduler.GetRecentJobs(30)
        result.SchedulerError = _scheduler.LastError
        result.FileSizes = New Dictionary(Of String, Long)(StringComparer.OrdinalIgnoreCase)
        result.FileMetadataErrors = New List(Of String)()
        For Each job In result.Jobs
            If Not String.IsNullOrWhiteSpace(job.OutputPath) AndAlso Not result.FileSizes.ContainsKey(job.OutputPath) Then
                Try
                    Dim info As New FileInfo(job.OutputPath)
                    If info.Exists Then result.FileSizes(job.OutputPath) = info.Length
                Catch ex As IOException
                    result.FileMetadataErrors.Add("Could not inspect " & job.OutputPath & ": " & ex.Message)
                Catch ex As UnauthorizedAccessException
                    result.FileMetadataErrors.Add("Access denied while inspecting " & job.OutputPath & ": " & ex.Message)
                Catch ex As ArgumentException
                    result.FileMetadataErrors.Add("Invalid report path " & job.OutputPath & ": " & ex.Message)
                End Try
            End If
        Next
        Return result
    End Function

    Private Sub ApplyHealth(snapshot As HealthSnapshot)
        Dim diagnostics = snapshot.Diagnostics
        Dim expectedRecordingIntervalSeconds = CInt(Math.Ceiling(_settings.HistorianRecordingIntervalMilliseconds / 1000.0R))
        Dim health = HistorianHealthEvaluator.Evaluate(diagnostics, DateTime.UtcNow, expectedRecordingIntervalSeconds)
        Dim zone As TimeZoneInfo = Nothing
        Try
            zone = _settings.ResolveFactoryTimeZone()
        Catch ex As InvalidDataException
            zone = TimeZoneInfo.Utc
        End Try
        Dim stateColor = ColorForState(health.State)
        Dim recordingPaused = Not _settings.ContinuousRawRecordingEnabled
        _continuousStatus.Text = If(recordingPaused,
            "Transformer historian recording: PAUSED by configuration",
            "Transformer historian recording: " & health.Text & " — " & health.Detail)
        _continuousStatus.ForeColor = If(recordingPaused, UiTheme.Amber, stateColor)
        Dim lastWrite = If(diagnostics.LastWriteUtc.HasValue,
            diagnostics.LastWriteUtc.Value.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture),
            "not yet")
        Dim detail = "PostgreSQL " & If(snapshot.DatabaseBytes < 0,
            "unreachable", HistorianHealthEvaluator.FormatBytes(snapshot.DatabaseBytes)) &
            " • queue " & diagnostics.QueueDepth.ToString("N0", CultureInfo.InvariantCulture) &
            " • dropped " & diagnostics.DroppedCycles.ToString("N0", CultureInfo.InvariantCulture) &
            " • buffered " & diagnostics.SpoolRows.ToString("N0", CultureInfo.InvariantCulture) & "/" &
            diagnostics.SpoolCapacity.ToString("N0", CultureInfo.InvariantCulture) &
            If(diagnostics.SpoolDroppedRows > 0, " (LOST " & diagnostics.SpoolDroppedRows.ToString("N0", CultureInfo.InvariantCulture) & ")", String.Empty) &
            " • write errors " & diagnostics.WriteFailures.ToString("N0", CultureInfo.InvariantCulture) &
            " • last write " & lastWrite
        If Not String.IsNullOrWhiteSpace(diagnostics.LastError) AndAlso
           health.Detail.IndexOf(diagnostics.LastError, StringComparison.Ordinal) < 0 Then detail &= " • " & diagnostics.LastError
        If Not String.IsNullOrWhiteSpace(snapshot.SchedulerError) Then detail &= " • Automatic export: " & snapshot.SchedulerError
        If snapshot.FileMetadataErrors.Count > 0 Then detail &= " • " & snapshot.FileMetadataErrors(0)
        _healthDetail.ForeColor = If(String.IsNullOrWhiteSpace(diagnostics.LastError) AndAlso
                                     String.IsNullOrWhiteSpace(snapshot.SchedulerError) AndAlso
                                     snapshot.FileMetadataErrors.Count = 0,
                                     If(recordingPaused, UiTheme.Amber, stateColor), UiTheme.Red)
        _healthDetail.Text = detail

        _jobs = snapshot.Jobs
        Dim selectedIndex = If(_jobsGrid.CurrentRow Is Nothing, -1, _jobsGrid.CurrentRow.Index)
        _jobsGrid.Rows.Clear()
        For Each job In snapshot.Jobs
            Dim stamp As String
            Dim moment = If(job.CompletedAtUtc, job.StartedAtUtc)
            If moment.HasValue Then
                stamp = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(moment.Value, DateTimeKind.Utc), zone).
                    ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            Else
                stamp = job.LocalDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            End If
            Dim size = String.Empty
            Dim bytes As Long
            If Not String.IsNullOrWhiteSpace(job.OutputPath) AndAlso snapshot.FileSizes.TryGetValue(job.OutputPath, bytes) Then
                size = HistorianHealthEvaluator.FormatBytes(bytes)
            End If
            Dim fileName = If(String.IsNullOrWhiteSpace(job.OutputPath), String.Empty, job.OutputPath)
            Dim failed = job.Status.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0
            Dim succeeded = job.Status.IndexOf("succe", StringComparison.OrdinalIgnoreCase) >= 0
            Dim statusText = If(succeeded, ChrW(&H2714) & "  ", If(failed, ChrW(&H2716) & "  ", ChrW(&H25CF) & "  ")) & job.Status
            Dim rowIndex = _jobsGrid.Rows.Add(stamp, job.AnalyzerId, statusText, fileName, size, job.ErrorMessage)
            Dim row = _jobsGrid.Rows(rowIndex)
            row.Tag = job
            If Not String.IsNullOrWhiteSpace(job.OutputPath) Then row.Cells("FileName").ToolTipText = job.OutputPath
            If Not String.IsNullOrWhiteSpace(job.ErrorMessage) Then row.Cells("Error").ToolTipText = job.ErrorMessage
            row.Cells("Status").Style.ForeColor = If(failed, UiTheme.Red, If(succeeded, UiTheme.Green, UiTheme.Amber))
            row.Cells("Status").Style.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        Next
        _jobsGrid.Columns("Error").Visible = snapshot.Jobs.Any(Function(j) Not String.IsNullOrWhiteSpace(j.ErrorMessage))
        If selectedIndex >= 0 AndAlso selectedIndex < _jobsGrid.Rows.Count Then _jobsGrid.CurrentCell = _jobsGrid.Rows(selectedIndex).Cells(0)
    End Sub

    Private Shared Function ColorForState(state As RecordingHealthState) As Color
        Select Case state
            Case RecordingHealthState.Recording
                Return UiTheme.Green
            Case RecordingHealthState.Degraded, RecordingHealthState.NoData, RecordingHealthState.Stalled
                Return UiTheme.Amber
            Case Else
                Return UiTheme.Red
        End Select
    End Function

    Private Function SelectedJob() As DailyExportJob
        If _jobsGrid.CurrentRow Is Nothing Then Return Nothing
        Return TryCast(_jobsGrid.CurrentRow.Tag, DailyExportJob)
    End Function

    Private Sub OnOpenSelectedJobReport(sender As Object, e As EventArgs)
        Dim job = SelectedJob()
        If job Is Nothing Then
            ShowMessage("Select an export job first.", UiTheme.Amber)
            Return
        End If
        If String.IsNullOrWhiteSpace(job.OutputPath) OrElse Not File.Exists(job.OutputPath) Then
            ShowMessage("The report file for this job is not available: " & If(job.OutputPath, String.Empty), UiTheme.Amber)
            Return
        End If
        OpenShell(job.OutputPath)
    End Sub

    Private Sub OnViewJobDetails(sender As Object, e As EventArgs)
        Dim job = SelectedJob()
        If job Is Nothing Then
            ShowMessage("Select an export job first.", UiTheme.Amber)
            Return
        End If
        Dim text = "Analyzer: " & job.AnalyzerId & Environment.NewLine &
            "Local day: " & job.LocalDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) & Environment.NewLine &
            "Status: " & job.Status & Environment.NewLine &
            "Started (UTC): " & If(job.StartedAtUtc.HasValue, job.StartedAtUtc.Value.ToString("u", CultureInfo.InvariantCulture), "—") & Environment.NewLine &
            "Completed (UTC): " & If(job.CompletedAtUtc.HasValue, job.CompletedAtUtc.Value.ToString("u", CultureInfo.InvariantCulture), "—") & Environment.NewLine &
            "File: " & If(String.IsNullOrWhiteSpace(job.OutputPath), "—", job.OutputPath) & Environment.NewLine &
            "Error: " & If(String.IsNullOrWhiteSpace(job.ErrorMessage), "none", job.ErrorMessage)
        MessageBox.Show(Me, text, "Daily export job details", MessageBoxButtons.OK,
                        If(String.IsNullOrWhiteSpace(job.ErrorMessage), MessageBoxIcon.Information, MessageBoxIcon.Warning))
    End Sub



    ' ------------------------------------------------------------------ reports
















    Private Sub OpenShell(path As String)
        Try
            Process.Start(New ProcessStartInfo(path) With {.UseShellExecute = True})
        Catch ex As Exception
            ShowMessage("Could not open " & path & ": " & ex.Message, UiTheme.Red)
        End Try
    End Sub

    ' ------------------------------------------------------------------ save

    ' Dirty state is derived from the saved baseline, so spurious change events
    ' (e.g. ComboBox/NumericUpDown raising events when their handle is created) never flag "unsaved".
    Private Function CurrentFingerprint() As String
        Dim parts As New List(Of String) From {
            _dailyExcel.Checked.ToString(),
            _timezone.Text.Trim(),
            _exportTime.Value.ToString("HH:mm", CultureInfo.InvariantCulture),
            _retentionDays.Value.ToString(CultureInfo.InvariantCulture),
            _exportFolder.Text.Trim(),
            _continuousRawRecording.Checked.ToString(),
            SelectedIntervalMilliseconds().ToString(CultureInfo.InvariantCulture)
        }
        For Each analyzerId In AnalyzerIds
            parts.Add(analyzerId & "=" & String.Join(",", _drafts(analyzerId).Selected.OrderBy(Function(id) id, StringComparer.Ordinal)))
        Next
        Return String.Join("|", parts)
    End Function

    Private Sub SetDirty(value As Boolean)
        If Not value Then
            _baselineFingerprint = CurrentFingerprint()
        Else
            value = Not String.Equals(CurrentFingerprint(), _baselineFingerprint, StringComparison.Ordinal)
        End If
        _dirty = value
        _dirtyLabel.Text = If(value, "● Unsaved changes", "✔ All changes saved")
        _dirtyLabel.ForeColor = If(value, UiTheme.Amber, UiTheme.Green)
    End Sub

    Private Sub ShowMessage(text As String, color As Color)
        If IsDisposed Then Return
        _message.ForeColor = color
        _message.Text = text
        If _tabs.SelectedIndex = 0 Then
            _acquisitionMessage.ForeColor = color
            _acquisitionMessage.Text = text
        End If
    End Sub

    Private Function BuildCandidate() As RecordingSettings
        Dim candidate As New RecordingSettings With {
            .AutomaticDailyExcelEnabled = _dailyExcel.Checked,
            .FactoryTimeZoneId = _timezone.Text.Trim(),
            .DailyExportStabilizationMinutes = _exportTime.Value.Hour * 60 + _exportTime.Value.Minute,
            .ExcelRetentionDays = _settings.ExcelRetentionDays,
            .AutomaticExcelDeletionEnabled = False,
            .ExportFolderPath = _exportFolder.Text.Trim(),
            .ContinuousRawRecordingEnabled = _continuousRawRecording.Checked,
            .HistorianRetentionDays = CInt(_retentionDays.Value),
            .HistorianRecordingIntervalMilliseconds = SelectedIntervalMilliseconds()
        }
        candidate.Equipment.Clear()
        For Each analyzerId In AnalyzerIds
            Dim equipment = _settings.GetAnalyzer(analyzerId).Clone()
            equipment.SelectedParameters = _drafts(analyzerId).Selected.ToList()
            candidate.Equipment.Add(equipment)
        Next
        candidate.Validate()
        Return candidate
    End Function

    Private Shared Sub VerifyFolderWritable(folder As String)
        Directory.CreateDirectory(folder)
        Dim probe = Path.Combine(folder, ".eoems-write-test-" & Guid.NewGuid().ToString("N"))
        Try
            File.WriteAllText(probe, "probe")
        Finally
            If File.Exists(probe) Then File.Delete(probe)
        End Try
    End Sub

    Private Async Sub OnSaveClicked(sender As Object, e As EventArgs)
        If Not _canEdit OrElse _savingRecording Then Return
        _savingRecording = True
        _saveButton.Enabled = False
        Try
            Dim candidate = BuildCandidate()
            Dim savedFingerprint = CurrentFingerprint()
            If candidate.AutomaticDailyExcelEnabled Then
                Try
                    VerifyFolderWritable(candidate.ResolveExportRootDirectory())
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                    Throw New InvalidDataException("The daily report folder is not writable: " & ex.Message, ex)
                End Try
            End If
            Await _saveSettings(candidate)
            If IsDisposed Then Return
            _settings = candidate
            RefreshResolutionTierStates()
            _baselineFingerprint = savedFingerprint
            SetDirty(True)
            ShowMessage(If(_dirty, "Submitted settings saved. New edits are still unsaved.",
                "Recording settings saved and audited. PLC acquisition was not restarted."), If(_dirty, UiTheme.Amber, UiTheme.Green))
            Call RefreshHealthAsync()
            If _resolutionPanel.Visible Then Call RefreshResolutionAsync()
        Catch ex As Exception
            ShowMessage("Not saved: " & ex.Message, UiTheme.Red)
        Finally
            _savingRecording = False
            If Not IsDisposed Then _saveButton.Enabled = _canEdit
        End Try
    End Sub
End Class
