Imports System.Diagnostics
Imports System.Globalization
Imports System.Runtime.InteropServices
Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions

' Online power analyzer: pick the equipment and channels, the sampling rate, an optional start trigger and a stop time,
' then watch the live trace and statistics while every new reading is written to a CSV file in the chosen folder.
' UPS selected-channel requests run in the background service; missing readings are never filled or repeated.
Public NotInheritable Class OnlineAnalyzerView
    Inherits UserControl

    Private Enum RunState
        Idle
        Armed
        Recording
    End Enum

    Private NotInheritable Class EquipmentItem
        Public Property Id As String = String.Empty
        Public Property Name As String = String.Empty
        Public Property IsUps As Boolean
        Public Overrides Function ToString() As String
            Return Name
        End Function
    End Class

    Private NotInheritable Class ChannelItem
        Public Property Label As String = String.Empty
        Public Property Unit As String = String.Empty
        Public Property IsUps As Boolean
        Public Property TransformerIndex As Integer = -1
        Public Property UpsKey As String = String.Empty
        Public Property UpsPhase As Integer = -1
        ' "LN" = phase to neutral, "LL" = phase to phase; only used by UPS voltage channels.
        Public Property VoltageMode As String = String.Empty
        Public Overrides Function ToString() As String
            Return If(IsUps, "UPS · ", "Transformer · ") & Label & If(Unit.Length = 0, "", " [" & Unit & "]")
        End Function
    End Class

    Private NotInheritable Class Reading
        Public Property CapturedUtc As DateTime
        Public Property Quality As String = String.Empty
        Public Property Channels As Double()
        Public Property Ups As UpsSnapshot
    End Class

    Private NotInheritable Class EventRecord
        Public Property Number As Integer
        Public Property TimeUtc As DateTime
        Public Property Series As SeriesState
        Public Property Kind As String = String.Empty
        Public Property Before As Double
        Public Property After As Double
        Public Property ShotPath As String = String.Empty
        Public Property DueUtc As DateTime
        Public Property Row As DataGridViewRow
    End Class

    Private NotInheritable Class SeriesState
        Public Property Equipment As EquipmentItem
        Public Property Channel As ChannelItem
        Public Property Name As String = String.Empty
        Public Property Color As Color
        Public ReadOnly Points As New List(Of AnalyticsTrendPoint)()
        Public Recorded As New List(Of ReportPoint)()
        Public RecordedChunks As New List(Of String)()
        Public RecordedTotal As Integer
        Public FirstRecordedUtc As DateTime?
        Public Count As Long
        Public Mean As Double
        Public M2 As Double
        Public Minimum As Double = Double.PositiveInfinity
        Public Maximum As Double = Double.NegativeInfinity
        Public Last As Double?
        Public ReadingQuality As String = String.Empty
        Public LastUnavailableReason As String = String.Empty
        Public LastEventUtc As DateTime

        Public Sub Accumulate(value As Double)
            Count += 1
            Dim delta = value - Mean
            Mean += delta / Count
            M2 += delta * (value - Mean)
            If value < Minimum Then Minimum = value
            If value > Maximum Then Maximum = value
        End Sub

        Public Sub ResetStatistics()
            Count = 0
            Mean = 0
            M2 = 0
            Minimum = Double.PositiveInfinity
            Maximum = Double.NegativeInfinity
        End Sub
    End Class

    Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture
    Private Shared ReadOnly IntervalMs As Integer() = {25, 100, 250, 500, 1000, 2000, 5000, 10000, 30000, 60000}
    Private Shared ReadOnly IntervalText As String() = {"0.025 s", "0.1 s", "0.25 s", "0.5 s", "1 s", "2 s", "5 s", "10 s", "30 s", "60 s"}
    Private Shared ReadOnly DurationSeconds As Integer() = {0, 60, 300, 900, 1800, 3600, 14400, 28800, 86400, 172800}
    Private Shared ReadOnly DurationText As String() = {"Until I press Stop", "1 minute", "5 minutes", "15 minutes", "30 minutes", "1 hour",
                                                         "4 hours", "8 hours", "24 hours", "48 hours"}
    Private Shared ReadOnly EventKindText As String() = {"Sudden change (% of recent average)", "Sudden change (units between samples)",
                                                         "Rises above level", "Falls below level"}
    Private Shared ReadOnly EventGapSeconds As Integer() = {1, 5, 10, 30, 60}
    Private Shared ReadOnly EventGapText As String() = {"1 s", "5 s", "10 s", "30 s", "60 s"}
    Private Shared ReadOnly EventSpanSeconds As Integer() = {10, 30, 60, 300, 900}
    Private Shared ReadOnly EventSpanText As String() = {"10 s", "30 s", "1 min", "5 min", "15 min"}
    Private Const MaximumScreenshots As Integer = 500
    Private Shared ReadOnly WindowSeconds As Integer() = {1, 2, 5, 10, 30, 60, 120, 300, 900, 1800, 3600, 7200, 14400, 28800, 43200, 86400}
    Private Shared ReadOnly WindowText As String() = {"Last 1 s", "Last 2 s", "Last 5 s", "Last 10 s", "Last 30 s", "Last 1 min", "Last 2 min", "Last 5 min", "Last 15 min", "Last 30 min", "Last 1 hour", "Last 2 hours", "Last 4 hours", "Last 8 hours", "Last 12 hours", "Last 1 day"}
    Private Shared ReadOnly Palette As Color() = {
        Color.FromArgb(24, 101, 204), Color.FromArgb(200, 70, 60), Color.FromArgb(24, 132, 91), Color.FromArgb(183, 119, 31),
        Color.FromArgb(124, 77, 190), Color.FromArgb(13, 148, 160), Color.FromArgb(214, 96, 154), Color.FromArgb(96, 125, 139),
        Color.FromArgb(121, 85, 72), Color.FromArgb(0, 121, 107), Color.FromArgb(255, 111, 0), Color.FromArgb(63, 81, 181)}
    Private Const MaximumPointsPerSeries As Integer = 40000
    Private Const MaximumSeriesPoints As Integer = 3600

    Private ReadOnly _transformers As Func(Of IReadOnlyList(Of MeterSnapshot))
    Private ReadOnly _upsSnapshots As Func(Of IReadOnlyDictionary(Of String, UpsSnapshot))
    Private ReadOnly _recordingSettings As Func(Of RecordingSettings)
    Private ReadOnly _equipmentList As New CheckedListBox()
    Private ReadOnly _channelList As New CheckedListBox()
    Private ReadOnly _quickSelect As New ComboBox()
    Private ReadOnly _includeInputEstimates As New CheckBox With {
        .Text = "Add separate estimated input V / kVA", .Checked = True, .AutoSize = True}
    Private ReadOnly _interval As New ComboBox()
    Private ReadOnly _duration As New ComboBox()
    Private ReadOnly _triggerMode As New ComboBox()
    Private ReadOnly _triggerSeries As New ComboBox()
    Private ReadOnly _triggerLevel As New TextBox()
    Private ReadOnly _eventEnable As New CheckBox()
    Private ReadOnly _eventMode As New ComboBox()
    Private ReadOnly _eventWatch As New ComboBox()
    Private ReadOnly _eventLevel As New TextBox()
    Private ReadOnly _eventGap As New ComboBox()
    Private ReadOnly _eventSpan As New ComboBox()
    Private ReadOnly _eventShots As New CheckBox()
    Private ReadOnly _eventGrid As New DataGridView()
    Private ReadOnly _pendingShots As New List(Of EventRecord)()
    Private ReadOnly _saveToFile As New CheckBox()
    Private ReadOnly _folder As New TextBox()
    Private ReadOnly _fileName As New TextBox()
    Private ReadOnly _start As Button = UiTheme.Button("Start", True)
    Private ReadOnly _pause As Button = UiTheme.Button("Pause", False)
    Private ReadOnly _stop As Button = UiTheme.Button("Stop", False)
    Private ReadOnly _clear As Button = UiTheme.Button("Clear", False)
    Private ReadOnly _browse As Button = UiTheme.Button("Browse…", False)
    Private ReadOnly _openFolder As Button = UiTheme.Button("Open folder", False)
    Private ReadOnly _exportPdf As Button = UiTheme.Button("Export PDF", False)
    Private ReadOnly _checkedChannels As New HashSet(Of ChannelItem)()
    Private _totalIndexes As HashSet(Of Integer)
    Private _rebuildPending As Boolean
    Private ReadOnly _status As Label = UiTheme.Label(String.Empty,
        New Font("Segoe UI", 9.5F, FontStyle.Bold), UiTheme.Ink, False)
    Private ReadOnly _unitChoice As New ComboBox()
    Private ReadOnly _windowChoice As New ComboBox()
    Private ReadOnly _chart As New AnalyticsTrendChart()
    Private ReadOnly _chartScroll As New Panel With {.Dock = DockStyle.Fill, .AutoScroll = True}
    Private ReadOnly _chartStack As New TableLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = False, .ColumnCount = 1,
        .Padding = New Padding(0, 0, 0, 32)}
    Private ReadOnly _chartGroups As New List(Of Tuple(Of AnalyticsTrendChart, SeriesState()))()
    Private ReadOnly _graphInfo As New Label With {.Dock = DockStyle.Top, .Height = 34, .AutoEllipsis = True}
    Private ReadOnly _workspaceTabs As New FlatTabControl With {.Dock = DockStyle.Fill}
    Private ReadOnly _hideSelection As New CheckBox With {.Text = "Expand workspace", .Appearance = Appearance.Button, .AutoSize = True}
    Private Const AllGraphUnits As String = "All selected channels"
    Private ReadOnly _grid As New DataGridView()
    Private ReadOnly _timer As New Timer With {.Interval = 15}
    Private _analyzerService As UpsAcquisitionService
    Private _upsTiming As String = String.Empty
    Private ReadOnly _series As New List(Of SeriesState)()
    Private ReadOnly _activeEquipment As New List(Of EquipmentItem)()
    Private ReadOnly _lastCapture As New Dictionary(Of String, DateTime)(StringComparer.Ordinal)
    Private ReadOnly _quality As New Dictionary(Of String, String)(StringComparer.Ordinal)
    Private ReadOnly _channels As New List(Of ChannelItem)()
    Private _state As RunState = RunState.Idle
    Private _paused As Boolean
    Private _pageActive As Boolean
    Private _writer As StreamWriter
    Private _filePath As String = String.Empty
    Private _rows As Long
    Private _runStartUtc As DateTime
    Private _lastSampleUtc As DateTime
    Private _lastRefreshUtc As DateTime
    Private ReadOnly _displayClock As Stopwatch = Stopwatch.StartNew()
    Private _lastFrameMilliseconds As Long
    Private _plotDirty As Boolean = True
    Private _traceStartUtc As DateTime
    Private _plotNowUtc As DateTime
    Private _revealFromUtc As DateTime
    Private _revealTargetUtc As DateTime
    Private _revealStartedMilliseconds As Long
    Private _intervalMs As Integer = 1000
    Private _durationSeconds As Integer
    Private _triggerState As SeriesState
    Private _triggerAbove As Boolean
    Private _triggerThreshold As Double
    Private _updatingChecks As Boolean
    Private _eventActive As Boolean
    Private _eventKind As Integer
    Private _eventThreshold As Double
    Private _eventGapLimit As Integer
    Private _eventSpanLimit As Integer
    Private _eventWatchState As SeriesState
    Private _eventDir As String = String.Empty
    Private _eventWriter As StreamWriter
    Private _eventCount As Integer
    Private _shotCount As Integer

    Public Sub New(transformers As Func(Of IReadOnlyList(Of MeterSnapshot)),
                   upsSnapshots As Func(Of IReadOnlyDictionary(Of String, UpsSnapshot)),
                   recordingSettings As Func(Of RecordingSettings))
        If transformers Is Nothing Then Throw New ArgumentNullException(NameOf(transformers))
        If upsSnapshots Is Nothing Then Throw New ArgumentNullException(NameOf(upsSnapshots))
        If recordingSettings Is Nothing Then Throw New ArgumentNullException(NameOf(recordingSettings))
        _transformers = transformers
        _upsSnapshots = upsSnapshots
        _recordingSettings = recordingSettings
        Dock = DockStyle.Fill
        BackColor = UiTheme.Canvas
        Padding = New Padding(4)
        BuildCatalog()
        BuildLayout()
        LoadFolder()
        SetControlsForState()
        AddHandler _timer.Tick, AddressOf OnTick
        _timer.Start()
    End Sub

    Public Sub SetPageActive(active As Boolean)
        _pageActive = active
        If active Then RefreshDisplay()
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            _timer.Stop()
            _analyzerService?.SetAnalyzerPolling(Nothing, 0)
            CloseFile()
            CloseEvents()
            For Each state In _series
                DeleteReportChunks(state.RecordedChunks)
            Next
            If _lastReport IsNot Nothing AndAlso Not Object.ReferenceEquals(_lastReport, _reportSnapshotInFlight) Then
                DeleteReportChunks(_lastReport.Chunks.Values.SelectMany(Function(paths) paths))
            End If
            _timer.Dispose()
            If _chart.Parent Is Nothing Then _chart.Dispose()
        End If
        MyBase.Dispose(disposing)
    End Sub

    Private Sub BuildCatalog()
        _totalIndexes = MeasurementCatalog.GetCatalog("TOTAL").Where(Function(item) item.Index >= 0).Select(Function(item) item.Index).ToHashSet()
        For Each info In MeasurementCatalog.GetCatalog("T1").Where(Function(item) item.Index >= 0)
            _channels.Add(New ChannelItem With {.Label = info.Label, .Unit = info.Unit, .TransformerIndex = info.Index})
        Next
        ' Totals are calculated by the UPS snapshot from the card values (a unit that reports no per-phase kW still reports the total).
        For Each total In {("Output Power (total)", "kW", "@OutputKw"), ("Output Apparent Power (total)", "kVA", "@OutputKva"),
                           ("Output Power Factor (total)", "", "@OutputPf"), ("Output Load (highest phase)", "%", "@OutputLoadMax")}
            _channels.Add(New ChannelItem With {.IsUps = True, .Label = total.Item1, .Unit = total.Item2, .UpsKey = total.Item3})
        Next
        For Each estimate In UpsInputEstimates.Metrics()
            _channels.Add(New ChannelItem With {.IsUps = True, .Label = estimate.Name, .Unit = estimate.Unit, .UpsKey = estimate.Id})
        Next
        ' State and counter objects belong to the events and history pages, and bypass is not an analyzer signal.
        Dim skipped = {"InputLineBads", "InputLines", "OutputLines", "BypassLines", "RatedInputVoltage", "RatedOutputVa", "RatedOutputW",
                       "BatteryStatus", "SecondsOnBattery", "RuntimeMinutes", "AlarmsPresent", "OutputSource", "BypassVoltage", "BypassFrequency"}
        For Each definition In UpsOidMap.Definitions.Where(Function(item) Not UpsOidMap.IdentityKeys.Contains(item.Key) AndAlso
                                                                 Not skipped.Contains(item.Key))
            Dim label = Regex.Replace(definition.Key, "(?<=[a-z])(?=[A-Z])", " ")
            If definition.Key = "InputVoltage" OrElse definition.Key = "OutputVoltage" Then
                Dim side = If(definition.Key = "InputVoltage", "Input", "Output")
                Dim pairs = {"L1-L2", "L2-L3", "L3-L1"}
                For phase = 0 To 2
                    _channels.Add(New ChannelItem With {.IsUps = True, .Label = side & " Voltage L" & (phase + 1) & "-N", .Unit = "V",
                        .UpsKey = definition.Key, .UpsPhase = phase, .VoltageMode = "LN"})
                Next
                For phase = 0 To 2
                    _channels.Add(New ChannelItem With {.IsUps = True, .Label = side & " Voltage " & pairs(phase), .Unit = "V",
                        .UpsKey = definition.Key, .UpsPhase = phase, .VoltageMode = "LL"})
                Next
            ElseIf definition.PerPhase Then
                For phase = 0 To 2
                    _channels.Add(New ChannelItem With {.IsUps = True, .Label = label & " L" & (phase + 1), .Unit = definition.Unit,
                        .UpsKey = definition.Key, .UpsPhase = phase})
                Next
            Else
                _channels.Add(New ChannelItem With {.IsUps = True, .Label = label, .Unit = definition.Unit, .UpsKey = definition.Key})
            End If
        Next
    End Sub

    Private Sub BuildLayout()
        For Each id In {"T1", "T2", "T3", "T4"}
            _equipmentList.Items.Add(New EquipmentItem With {.Id = id, .Name = "Transformer " & id})
        Next
        _equipmentList.Items.Add(New EquipmentItem With {.Id = "TOTAL", .Name = "Total (T1-T4 combined)"})
        For Each device In UpsSettingsStore.Current.Devices
            _equipmentList.Items.Add(New EquipmentItem With {.Id = device.Id, .Name = "UPS " & device.DisplayName, .IsUps = True})
        Next
        _equipmentList.CheckOnClick = True
        _channelList.CheckOnClick = True
        _equipmentList.Height = 128
        _channelList.Height = 210
        _quickSelect.DropDownStyle = ComboBoxStyle.DropDownList
        _quickSelect.Items.AddRange({"Quick select channels…", "Voltages", "Currents", "Power (kW / kVA / PF)", "Frequency", "Battery", "Clear all", "UPS power balance evidence", "UPS input estimates (balanced assumption)"})
        _quickSelect.SelectedIndex = 0
        _interval.DropDownStyle = ComboBoxStyle.DropDownList
        _interval.Items.AddRange(IntervalText)
        _interval.SelectedIndex = 1
        _duration.DropDownStyle = ComboBoxStyle.DropDownList
        _duration.Items.AddRange(DurationText)
        _duration.SelectedIndex = 0
        _triggerMode.DropDownStyle = ComboBoxStyle.DropDownList
        _triggerMode.Items.AddRange({"Start immediately", "Start when value rises above", "Start when value falls below"})
        _triggerMode.SelectedIndex = 0
        _triggerSeries.DropDownStyle = ComboBoxStyle.DropDownList
        _triggerLevel.Text = "0"
        _eventEnable.Text = "Capture events (spike / drop / sudden change)"
        _eventEnable.AutoSize = True
        _eventEnable.Checked = True
        _eventMode.DropDownStyle = ComboBoxStyle.DropDownList
        _eventMode.Items.AddRange(EventKindText)
        _eventMode.SelectedIndex = 0
        _eventWatch.DropDownStyle = ComboBoxStyle.DropDownList
        _eventLevel.Text = "10"
        _eventGap.DropDownStyle = ComboBoxStyle.DropDownList
        _eventGap.Items.AddRange(EventGapText)
        _eventGap.SelectedIndex = 2
        _eventSpan.DropDownStyle = ComboBoxStyle.DropDownList
        _eventSpan.Items.AddRange(EventSpanText)
        _eventSpan.SelectedIndex = 1
        _eventShots.Text = "Save a screenshot of the signal for each event"
        _eventShots.AutoSize = True
        _eventShots.Checked = True
        _saveToFile.Text = "Save readings to a CSV file"
        _saveToFile.Checked = True
        _saveToFile.AutoSize = True
        _fileName.Text = "Analyzer"
        _folder.ReadOnly = False

        AddHandler _equipmentList.ItemCheck, Sub(sender, e)
                                                 RefreshChannelList(CheckedEquipment(e.Index, e.NewValue = CheckState.Checked))
                                                 ScheduleTriggerRebuild()
                                             End Sub
        AddHandler _channelList.ItemCheck, Sub(sender, e)
                                               Dim channel = TryCast(_channelList.Items(e.Index), ChannelItem)
                                               If channel IsNot Nothing Then
                                                   If e.NewValue = CheckState.Checked Then _checkedChannels.Add(channel) Else _checkedChannels.Remove(channel)
                                               End If
                                               If Not _updatingChecks Then ScheduleTriggerRebuild()
                                           End Sub
        AddHandler _quickSelect.SelectedIndexChanged, AddressOf OnQuickSelect
        AddHandler _start.Click, AddressOf OnStartClicked
        AddHandler _pause.Click, Sub(sender, e)
                                     _paused = Not _paused
                                     If _paused Then
                                         _analyzerService?.SetAnalyzerPolling(Nothing, 0)
                                     Else
                                         ConfigureAnalyzerPolling()
                                     End If
                                     SetControlsForState()
                                     UpdateStatus()
                                     RefreshDisplay(True)
                                 End Sub
        AddHandler _stop.Click, Sub(sender, e) StopRun("Stopped by user.")
        AddHandler _clear.Click, Sub(sender, e) ClearDisplay()
        AddHandler _browse.Click, AddressOf OnBrowseClicked
        AddHandler _openFolder.Click, AddressOf OnOpenFolderClicked
        AddHandler _exportPdf.Click, AddressOf OnExportPdfClicked
        _exportPdf.Enabled = False
        AddHandler _unitChoice.SelectedIndexChanged, Sub(sender, e)
                                                        BuildGraphGroups()
                                                        RefreshDisplay(True)
                                                    End Sub
        AddHandler _windowChoice.SelectedIndexChanged, Sub(sender, e) RefreshDisplay(True)

        _unitChoice.DropDownStyle = ComboBoxStyle.DropDownList
        _unitChoice.Width = 170
        _windowChoice.DropDownStyle = ComboBoxStyle.DropDownList
        _windowChoice.Items.AddRange(WindowText)
        _windowChoice.SelectedIndex = 5
        _windowChoice.Width = 120

        Dim left As New TableLayoutPanel With {.Dock = DockStyle.Left, .Width = 330,         .ColumnCount = 1, .RowCount = 7, .Padding = New Padding(0, 0, 6, 0)}
        left.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        left.RowStyles.Add(New RowStyle(SizeType.Absolute, 26))
        left.RowStyles.Add(New RowStyle(SizeType.Percent, 34))
        left.RowStyles.Add(New RowStyle(SizeType.Absolute, 26))
        left.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        left.RowStyles.Add(New RowStyle(SizeType.Percent, 66))
                left.RowStyles.Add(New RowStyle(SizeType.Absolute, 0))
                left.RowStyles.Add(New RowStyle(SizeType.Absolute, 100))
        _quickSelect.Margin = New Padding(0, 0, 0, 2)
        left.Controls.Add(Heading("1. Equipment to analyze"), 0, 0)
        left.Controls.Add(_equipmentList, 0, 1)
        left.Controls.Add(Heading("2. Channels of the selected equipment"), 0, 2)
        Dim channelOptions As New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 1, .RowCount = 2,
            .AutoSize = True, .Margin = Padding.Empty}
        channelOptions.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        channelOptions.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        channelOptions.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        channelOptions.Controls.Add(_quickSelect, 0, 0)
        channelOptions.Controls.Add(_includeInputEstimates, 0, 1)
        left.Controls.Add(channelOptions, 0, 3)
        left.Controls.Add(_channelList, 0, 4)
        Dim actionBar As New FlowLayoutPanel With {.AutoSize = True, .WrapContents = False, .Margin = Padding.Empty}
        For Each action In {_start, _pause, _stop, _clear, _exportPdf}
            action.Size = New Size(If(action Is _exportPdf, 86, 52), 30)
            action.Margin = New Padding(0, 0, 4, 0)
            actionBar.Controls.Add(action)
        Next
        Dim saveBox As New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 3, .RowCount = 3, .Padding = New Padding(0, 4, 0, 0)}
        saveBox.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        saveBox.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        saveBox.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 100))
        saveBox.RowStyles.Add(New RowStyle(SizeType.Absolute, 26))
        saveBox.RowStyles.Add(New RowStyle(SizeType.Absolute, 32))
        saveBox.RowStyles.Add(New RowStyle(SizeType.Absolute, 32))
        _saveToFile.Margin = New Padding(0, 3, 0, 0)
        saveBox.Controls.Add(_saveToFile, 0, 0)
        saveBox.SetColumnSpan(_saveToFile, 3)
        Dim folderLabel = UiTheme.Label("Folder", New Font("Segoe UI", 9.0F), UiTheme.MutedInk, True)
        folderLabel.Anchor = AnchorStyles.Left
        saveBox.Controls.Add(folderLabel, 0, 1)
        _folder.Dock = DockStyle.None
        _folder.Anchor = AnchorStyles.Left Or AnchorStyles.Right
        _folder.Margin = New Padding(8, 3, 6, 3)
        saveBox.Controls.Add(_folder, 1, 1)
        _browse.Dock = DockStyle.Fill
        _browse.Margin = New Padding(0, 2, 0, 2)
        saveBox.Controls.Add(_browse, 2, 1)
        Dim fileLabel = UiTheme.Label("File name", New Font("Segoe UI", 9.0F), UiTheme.MutedInk, True)
        fileLabel.Anchor = AnchorStyles.Left
        saveBox.Controls.Add(fileLabel, 0, 2)
        _fileName.Anchor = AnchorStyles.Left Or AnchorStyles.Right
        _fileName.Margin = New Padding(8, 3, 6, 3)
        saveBox.Controls.Add(_fileName, 1, 2)
        _openFolder.Dock = DockStyle.Fill
        _openFolder.Margin = New Padding(0, 2, 0, 2)
        saveBox.Controls.Add(_openFolder, 2, 2)
        left.Controls.Add(saveBox, 0, 6)
        For Each item In {CType(_equipmentList, Control), _quickSelect, _channelList}
            item.Dock = DockStyle.Fill
        Next

        Dim samplingPage = OptionsPage("Sampling & start")
        AddInline(samplingPage, "Sample every", _interval, 80)
        AddInline(samplingPage, "Record for", _duration, 150)
        AddInline(samplingPage, "Start", _triggerMode, 200)
        AddInline(samplingPage, "on", _triggerSeries, 260)
        AddInline(samplingPage, "level", _triggerLevel, 70)
        Dim eventPage = OptionsPage("Event capture")
        _eventEnable.Margin = New Padding(4, 6, 0, 0)
        _eventShots.Margin = New Padding(12, 6, 0, 0)
        eventPage.Controls.Add(_eventEnable)
        AddInline(eventPage, "Detect", _eventMode, 230)
        AddInline(eventPage, "Watch", _eventWatch, 260)
        AddInline(eventPage, "Threshold", _eventLevel, 60)
        AddInline(eventPage, "Min. gap", _eventGap, 65)
        AddInline(eventPage, "Screenshot span", _eventSpan, 70)
        eventPage.Controls.Add(_eventShots)
        Dim chartBar As New FlowLayoutPanel With {.AutoSize = True, .WrapContents = False, .Margin = Padding.Empty}
        Dim showLabel = UiTheme.Label("Graph filter:", New Font("Segoe UI", 9.0F), UiTheme.MutedInk, True)
        showLabel.Margin = New Padding(8, 7, 4, 0)
        chartBar.Controls.Add(showLabel)
        _unitChoice.Margin = New Padding(0, 3, 6, 0)
        _windowChoice.Margin = New Padding(0, 3, 0, 0)
        chartBar.Controls.Add(_unitChoice)
        chartBar.Controls.Add(_windowChoice)

        _status.Dock = DockStyle.Top
        _status.Height = 0
        _status.Visible = False
        AddHandler _status.TextChanged, Sub(sender, e)
                                            _status.Visible = _status.Text.Length > 0
                                            _status.Height = If(_status.Text.Length > 0, 28, 0)
                                        End Sub
        ConfigureLiveChart(_chart)
        _chartStack.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        _chartStack.Controls.Add(_chart)
        _chartScroll.Controls.Add(_chartStack)
        AddHandler _chartStack.SizeChanged, Sub(sender, e) ResizeGraphRows()
        _graphInfo.ForeColor = UiTheme.MutedInk
        _graphInfo.Height = 0
        _graphInfo.Visible = False
        AddHandler _graphInfo.TextChanged, Sub(sender, e)
                                               _graphInfo.Visible = _graphInfo.Text.Length > 0
                                               _graphInfo.Height = If(_graphInfo.Text.Length > 0, 34, 0)
                                           End Sub
        TransformerDetailForm.ConfigureGrid(_grid, {"Channel", "Unit", "Last", "Minimum", "Maximum", "Average", "Std deviation", "Samples", "Reading quality"})
        _grid.Columns(0).DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft
        _grid.Columns(0).FillWeight = 220
        _grid.Dock = DockStyle.Fill
        ConfigureWorkspaceGrid(_grid, {310, 70, 100, 100, 100, 100, 120, 100, 260})
        _grid.Columns(0).Frozen = True
        TransformerDetailForm.ConfigureGrid(_eventGrid, {"#", "Time", "Channel", "Event", "Before", "After", "Screenshot"})
        _eventGrid.Columns(2).DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft
        _eventGrid.Columns(2).FillWeight = 200
        _eventGrid.Columns(3).DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft
        _eventGrid.Columns(3).FillWeight = 170
        _eventGrid.Columns(6).DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft
        _eventGrid.Columns(6).FillWeight = 200
        _eventGrid.Dock = DockStyle.Fill
        ConfigureWorkspaceGrid(_eventGrid, {60, 170, 310, 190, 110, 110, 300})
        _eventGrid.Visible = False
        AddHandler _eventGrid.CellDoubleClick, AddressOf OnEventDoubleClick

        Dim right As New Panel With {.Dock = DockStyle.Fill}
        Dim graphs As New TabPage("Graphs") With {.BackColor = UiTheme.Surface, .Padding = New Padding(4)}
        graphs.Controls.Add(_chartScroll)
        graphs.Controls.Add(_graphInfo)
        Dim readings As New TabPage("Readings") With {.BackColor = UiTheme.Surface, .Padding = New Padding(4)}
        readings.Controls.Add(_grid)
        Dim events As New TabPage("Events") With {.BackColor = UiTheme.Surface, .Padding = New Padding(4)}
        events.Controls.Add(_eventGrid)
        events.Controls.Add(New Label With {.Dock = DockStyle.Top, .Height = 32,
            .Text = "Recorded events appear here. Double-click an event to open its captured evidence.",
            .ForeColor = UiTheme.MutedInk, .AutoEllipsis = True})
        _workspaceTabs.TabPages.Insert(0, graphs)
        _workspaceTabs.TabPages.Insert(1, readings)
        _workspaceTabs.TabPages.Insert(2, events)
        _workspaceTabs.SelectedIndex = 0
        Dim workspace As New Panel With {.Dock = DockStyle.Fill}
        workspace.Controls.Add(_workspaceTabs)
        workspace.Controls.Add(New SegmentedTabStrip(_workspaceTabs))
        right.Controls.Add(workspace)
        right.Controls.Add(_status)
        Dim viewBar As New FlowLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .WrapContents = True}
        viewBar.Controls.Add(_hideSelection)
        viewBar.Controls.Add(actionBar)
        viewBar.Controls.Add(chartBar)
        right.Controls.Add(viewBar)
        right.BackColor = UiTheme.Surface
        right.Padding = New Padding(12, 8, 12, 8)
        AddHandler right.Paint, Sub(sender, e) e.Graphics.DrawRectangle(New Pen(UiTheme.Border), 0, 0, right.Width - 1, right.Height - 1)
        left.Dock = DockStyle.Fill
        left.Padding = New Padding(0)
        Dim leftCard As New Panel With {.Dock = DockStyle.Left, .Width = 350, .BackColor = UiTheme.Surface, .Padding = New Padding(12, 8, 12, 8)}
        AddHandler leftCard.Paint, Sub(sender, e) e.Graphics.DrawRectangle(New Pen(UiTheme.Border), 0, 0, leftCard.Width - 1, leftCard.Height - 1)
        leftCard.Controls.Add(left)
        For Each action In {_pause, _stop, _clear, _browse, _openFolder, _exportPdf}
            action.FlatAppearance.BorderSize = 1
            action.FlatAppearance.BorderColor = UiTheme.Border
        Next
        _status.Font = New Font("Segoe UI", 10.0F, FontStyle.Bold)
        _status.ForeColor = UiTheme.Ink
        _equipmentList.BorderStyle = BorderStyle.FixedSingle
        _channelList.BorderStyle = BorderStyle.FixedSingle
        _equipmentList.Font = New Font("Segoe UI", 9.5F)
        _channelList.Font = New Font("Segoe UI", 9.5F)
        Dim gap As New Panel With {.Dock = DockStyle.Left, .Width = 8}
        AddHandler _hideSelection.CheckedChanged, Sub(sender, e)
                                                     leftCard.Visible = Not _hideSelection.Checked
                                                     gap.Visible = Not _hideSelection.Checked
                                                     _hideSelection.Text = If(_hideSelection.Checked, "Show equipment / channels", "Expand workspace")
                                                 End Sub
        Controls.Add(right)
        Controls.Add(gap)
        Controls.Add(leftCard)
    End Sub

    Private Shared Sub ConfigureWorkspaceGrid(grid As DataGridView, widths As Integer())
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None
        grid.ScrollBars = ScrollBars.Both
        grid.ColumnHeadersHeight = 44
        grid.RowTemplate.Height = 30
        For index = 0 To widths.Length - 1
            grid.Columns(index).AutoSizeMode = DataGridViewAutoSizeColumnMode.None
            grid.Columns(index).Width = widths(index)
            grid.Columns(index).MinimumWidth = Math.Min(widths(index), 80)
        Next
    End Sub

    Private Function OptionsPage(title As String) As FlowLayoutPanel
        Dim page As New TabPage(title) With {.BackColor = UiTheme.Surface}
        Dim host As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .AutoScroll = True, .WrapContents = True, .Padding = New Padding(4, 2, 4, 0)}
        page.Controls.Add(host)
        _workspaceTabs.TabPages.Add(page)
        Return host
    End Function

    Private Shared Sub AddInline(host As FlowLayoutPanel, caption As String, input As Control, width As Integer)
        Dim label = UiTheme.Label(caption, New Font("Segoe UI", 9.0F), UiTheme.MutedInk, True)
        label.Margin = New Padding(10, 7, 4, 0)
        input.Width = width
        input.Margin = New Padding(0, 3, 0, 0)
        Dim pair As New FlowLayoutPanel With {.AutoSize = True, .WrapContents = False, .Margin = New Padding(0)}
        pair.Controls.Add(label)
        pair.Controls.Add(input)
        host.Controls.Add(pair)
    End Sub

    Private Function CheckedEquipment(changedIndex As Integer, changedState As Boolean) As List(Of EquipmentItem)
        Dim result As New List(Of EquipmentItem)()
        For index = 0 To _equipmentList.Items.Count - 1
            Dim isOn = If(index = changedIndex, changedState, _equipmentList.GetItemChecked(index))
            If isOn Then result.Add(DirectCast(_equipmentList.Items(index), EquipmentItem))
        Next
        Return result
    End Function

    Private Sub RefreshChannelList(selected As List(Of EquipmentItem))
        Dim hasUps = selected.Any(Function(item) item.IsUps)
        Dim transformers = selected.Where(Function(item) Not item.IsUps).ToList()
        Dim onlyTotal = transformers.Count > 0 AndAlso transformers.All(Function(item) item.Id = "TOTAL")
        _updatingChecks = True
        _channelList.BeginUpdate()
        Try
            _channelList.Items.Clear()
            For Each channel In _channels
                If channel.IsUps Then
                    If Not hasUps Then Continue For
                Else
                    If transformers.Count = 0 Then Continue For
                    If onlyTotal AndAlso Not _totalIndexes.Contains(channel.TransformerIndex) Then Continue For
                End If
                Dim position = _channelList.Items.Add(channel)
                If _checkedChannels.Contains(channel) Then _channelList.SetItemChecked(position, True)
            Next
        Finally
            _channelList.EndUpdate()
            _updatingChecks = False
        End Try
    End Sub

    Private Sub ScheduleTriggerRebuild()
        If _rebuildPending OrElse Not IsHandleCreated Then Return
        _rebuildPending = True
        BeginInvoke(Sub()
                        _rebuildPending = False
                        RebuildTriggerList()
                    End Sub)
    End Sub

    Private Shared Function Heading(text As String) As Label
        Dim label = UiTheme.Label(text, New Font("Segoe UI", 9.5F, FontStyle.Bold), UiTheme.Blue, False)
        label.Height = 24
        label.Dock = DockStyle.Fill
        label.TextAlign = ContentAlignment.BottomLeft
        label.Margin = New Padding(0, 0, 0, 2)
        Return label
    End Function

    Private Shared Function Labeled(caption As String, input As Control) As Control
        Dim row As New TableLayoutPanel With {.ColumnCount = 2, .RowCount = 1, .Height = 28}
        row.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 95))
        row.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        Dim label = UiTheme.Label(caption, New Font("Segoe UI", 9.0F), UiTheme.MutedInk, False)
        label.Dock = DockStyle.Fill
        label.TextAlign = ContentAlignment.MiddleLeft
        input.Dock = DockStyle.Fill
        row.Controls.Add(label, 0, 0)
        row.Controls.Add(input, 1, 0)
        Return row
    End Function

    Private Sub OnQuickSelect(sender As Object, e As EventArgs)
        If _quickSelect.SelectedIndex <= 0 Then Return
        Dim mode = _quickSelect.SelectedItem.ToString()
        _updatingChecks = True
        For index = 0 To _channelList.Items.Count - 1
            Dim channel = DirectCast(_channelList.Items(index), ChannelItem)
            Dim match As Boolean
            Select Case mode
                Case "UPS input estimates (balanced assumption)"
                    match = channel.IsUps AndAlso (UpsInputEstimates.IsEstimateKey(channel.UpsKey) OrElse
                        {"InputVoltage", "InputCurrent"}.Contains(channel.UpsKey))
                Case "Voltages"
                    match = channel.Unit = "V" AndAlso Not channel.Label.Contains("Battery")
                Case "Currents"
                    match = channel.Unit = "A" AndAlso Not channel.Label.Contains("Battery")
                Case "Power (kW / kVA / PF)"
                    match = {"kW", "kVA", "PF", "W"}.Contains(channel.Unit) OrElse channel.Label.Contains("Power Factor")
                Case "Frequency"
                    match = channel.Unit = "Hz"
                Case "Battery"
                    match = channel.Label.StartsWith("Battery", StringComparison.Ordinal) OrElse channel.Label.StartsWith("Charge", StringComparison.Ordinal) OrElse
                        channel.Label.StartsWith("Runtime", StringComparison.Ordinal)
                Case "UPS power balance evidence"
                    match = channel.IsUps AndAlso {"@OutputKw", "@OutputKva", "@OutputPf", "@OutputLoadMax",
                        "InputPower", "OutputPower", "InputCurrent", "OutputCurrent", "InputVoltage", "OutputVoltage",
                        "BatteryVoltage", "BatteryCurrent", "BatteryTemperature"}.Contains(channel.UpsKey)
                Case Else
                    match = False
            End Select
            If UpsInputEstimates.IsEstimateKey(channel.UpsKey) AndAlso mode <> "UPS input estimates (balanced assumption)" Then match = False
            _channelList.SetItemChecked(index, match)
        Next
        _updatingChecks = False
        If mode = "Clear all" Then _checkedChannels.Clear()
        _quickSelect.SelectedIndex = 0
        RebuildTriggerList()
    End Sub

    Private Function SelectedSeriesSpecs() As List(Of (Equipment As EquipmentItem, Channel As ChannelItem))
        Dim result As New List(Of (Equipment As EquipmentItem, Channel As ChannelItem))()
        For Each equipment In _equipmentList.CheckedItems.Cast(Of EquipmentItem)()
            For Each channel In _channelList.CheckedItems.Cast(Of ChannelItem)()
                If channel.IsUps <> equipment.IsUps Then Continue For
                If Not channel.IsUps AndAlso equipment.Id = "TOTAL" AndAlso Not _totalIndexes.Contains(channel.TransformerIndex) Then Continue For
                result.Add((equipment, channel))
            Next
        Next
        Return result
    End Function

    Private Shared Function SeriesName(equipment As EquipmentItem, channel As ChannelItem) As String
        Return equipment.Id & " | " & channel.Label & If(channel.Unit.Length = 0, "", " [" & channel.Unit & "]")
    End Function

    Private Sub RebuildTriggerList()
        If _state <> RunState.Idle OrElse IsDisposed Then Return
        Dim previous = TryCast(_triggerSeries.SelectedItem, String)
        _triggerSeries.Items.Clear()
        For Each spec In SelectedSeriesSpecs()
            _triggerSeries.Items.Add(SeriesName(spec.Equipment, spec.Channel))
        Next
        If previous IsNot Nothing AndAlso _triggerSeries.Items.Contains(previous) Then
            _triggerSeries.SelectedItem = previous
        ElseIf _triggerSeries.Items.Count > 0 Then
            _triggerSeries.SelectedIndex = 0
        End If
        Dim watched = TryCast(_eventWatch.SelectedItem, String)
        _eventWatch.Items.Clear()
        _eventWatch.Items.Add("All selected channels")
        For Each item In _triggerSeries.Items
            _eventWatch.Items.Add(item)
        Next
        If watched IsNot Nothing AndAlso _eventWatch.Items.Contains(watched) Then
            _eventWatch.SelectedItem = watched
        Else
            _eventWatch.SelectedIndex = 0
        End If
    End Sub

    Private Sub OnStartClicked(sender As Object, e As EventArgs)
        If _state <> RunState.Idle Then Return
        If _includeInputEstimates.Checked AndAlso _channelList.CheckedItems.Cast(Of ChannelItem)().
            Any(Function(channel) channel.IsUps AndAlso {"InputVoltage", "InputCurrent", "InputPower"}.Contains(channel.UpsKey)) Then
            For index = 0 To _channelList.Items.Count - 1
                If UpsInputEstimates.IsEstimateKey(DirectCast(_channelList.Items(index), ChannelItem).UpsKey) Then
                    _channelList.SetItemChecked(index, True)
                End If
            Next
        End If
        RebuildTriggerList()
        Dim specs = SelectedSeriesSpecs()
        If specs.Count = 0 Then
            _status.Text = "Choose at least one equipment and one matching channel (transformer channels for T1-T4/Total, UPS channels for UPS)."
            Return
        End If
        Dim threshold As Double
        Dim triggered = _triggerMode.SelectedIndex > 0
        If triggered Then
            If Not Double.TryParse(_triggerLevel.Text.Trim(), NumberStyles.Float, Inv, threshold) AndAlso
               Not Double.TryParse(_triggerLevel.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, threshold) Then
                _status.Text = "The trigger level must be a number."
                Return
            End If
            If _triggerSeries.SelectedIndex < 0 Then
                _status.Text = "Choose the channel the trigger watches."
                Return
            End If
        End If
        Dim eventThreshold As Double
        If _eventEnable.Checked Then
            If Not ParseNumber(_eventLevel.Text, eventThreshold) Then
                _status.Text = "The event threshold must be a number."
                Return
            End If
            If _eventMode.SelectedIndex <= 1 AndAlso eventThreshold <= 0 Then
                _status.Text = "A sudden-change threshold must be greater than zero."
                Return
            End If
            If _folder.Text.Trim().Length = 0 Then
                _status.Text = "Choose a folder: event screenshots are saved there."
                Return
            End If
        End If
        _series.Clear()
        _traceStartUtc = DateTime.MinValue
        _plotNowUtc = DateTime.MinValue
        _revealFromUtc = DateTime.MinValue
        _revealTargetUtc = DateTime.MinValue
        _plotDirty = True
        _activeEquipment.Clear()
        _lastCapture.Clear()
        _quality.Clear()
        For Each spec In specs
            _series.Add(New SeriesState With {.Equipment = spec.Equipment, .Channel = spec.Channel,
                .Name = SeriesName(spec.Equipment, spec.Channel), .Color = Palette(_series.Count Mod Palette.Length)})
            If Not _activeEquipment.Contains(spec.Equipment) Then _activeEquipment.Add(spec.Equipment)
        Next
        _intervalMs = IntervalMs(Math.Max(0, _interval.SelectedIndex))
        _upsTiming = String.Empty
        _durationSeconds = DurationSeconds(Math.Max(0, _duration.SelectedIndex))
        _triggerState = If(triggered, _series.First(Function(item) item.Name = CStr(_triggerSeries.SelectedItem)), Nothing)
        _triggerAbove = _triggerMode.SelectedIndex = 1
        _triggerThreshold = threshold
        CloseEvents()
        _pendingShots.Clear()
        _eventGrid.Rows.Clear()
        _eventGrid.Visible = False
        _eventCount = 0
        _shotCount = 0
        _eventActive = _eventEnable.Checked
        _eventKind = Math.Max(0, _eventMode.SelectedIndex)
        _eventThreshold = eventThreshold
        _eventGapLimit = EventGapSeconds(Math.Max(0, _eventGap.SelectedIndex))
        _eventSpanLimit = EventSpanSeconds(Math.Max(0, _eventSpan.SelectedIndex))
        _eventWatchState = If(_eventWatch.SelectedIndex > 0, _series.FirstOrDefault(Function(item) item.Name = CStr(_eventWatch.SelectedItem)), Nothing)
        If _eventActive Then
            Dim eventName = Regex.Replace(If(_fileName.Text.Trim().Length = 0, "Analyzer", _fileName.Text.Trim()), "[^\w\- ]", "_")
            _eventDir = Path.Combine(_folder.Text.Trim(), eventName & "_" & DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv) & "_events")
        End If
        _chart.PollingIntervalSeconds = Math.Max(5, CInt(Math.Ceiling(_intervalMs * 4 / 1000.0R)))
        _paused = False
        _lastSampleUtc = DateTime.MinValue
        FillUnitChoices()
        FillGrid()
        If triggered Then
            _state = RunState.Armed
        ElseIf Not BeginRecording() Then
            _state = RunState.Idle
        End If
        SetControlsForState()
        Try
            If _state <> RunState.Idle Then
                _workspaceTabs.SelectedIndex = 0
                _analyzerService = If(_activeEquipment.Any(Function(item) item.IsUps), UpsAcquisitionService.Current, Nothing)
                ConfigureAnalyzerPolling()
                Sample()
            End If
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is ObjectDisposedException
            StopRun("file write failed: " & ex.Message)
        End Try
        UpdateStatus()
        RefreshDisplay(True)
    End Sub

    Private Sub ConfigureAnalyzerPolling()
        _analyzerService?.SetAnalyzerPolling(_series.Where(Function(item) item.Equipment.IsUps).
            GroupBy(Function(item) item.Equipment.Id).ToDictionary(Function(group) group.Key,
                Function(group) group.Select(Function(item) AnalyzerKey(item.Channel)).Distinct(StringComparer.Ordinal).ToArray()), _intervalMs)
    End Sub

    Private Shared Function AnalyzerKey(channel As ChannelItem) As String
        If channel.UpsKey = "OutputVoltage" AndAlso channel.VoltageMode = "LL" Then Return "OutputVoltageLL"
        Return channel.UpsKey
    End Function

    Private Function BeginRecording() As Boolean
        _rows = 0
        _recordingFailure = String.Empty
        For Each item In _series
            item.ResetStatistics()
            DeleteReportChunks(item.RecordedChunks)
            item.RecordedChunks.Clear()
            item.Recorded = New List(Of ReportPoint)()
            item.RecordedTotal = 0
            item.FirstRecordedUtc = Nothing
        Next
        If _saveToFile.Checked Then
            If Not OpenFile() Then Return False
        Else
            _filePath = String.Empty
        End If
        _runStartUtc = DateTime.UtcNow
        _state = RunState.Recording
        Return True
    End Function

    Private Function OpenFile() As Boolean
        Try
            Dim folder = _folder.Text.Trim()
            If folder.Length = 0 Then Throw New ArgumentException("Choose a folder to save the file in.")
            Directory.CreateDirectory(folder)
            Dim name = Regex.Replace(If(_fileName.Text.Trim().Length = 0, "Analyzer", _fileName.Text.Trim()), "[^\w\- ]", "_")
            _filePath = Path.Combine(folder, name & "_" & DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv) & ".csv")
            _writer = New StreamWriter(New FileStream(_filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), New UTF8Encoding(True))
            Dim header As New List(Of String) From {"Timestamp UTC", "Timestamp local"}
            header.AddRange(_series.Select(Function(item) item.Name))
            header.AddRange(_activeEquipment.Select(Function(item) item.Id & " | reading quality"))
            header.AddRange(_activeEquipment.Select(Function(item) item.Id & " | acquisition UTC"))
            _writer.WriteLine(String.Join(",", header.Select(AddressOf Csv)))
            _writer.Flush()
            SaveFolder(folder)
            Return True
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
                                   TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException
            CloseFile()
            _status.Text = "Cannot create the file: " & ex.Message
            Return False
        End Try
    End Function

    Private Sub CloseFile()
        If _writer Is Nothing Then Return
        Dim writer = _writer
        _writer = Nothing
        Try
            Using writer
                writer.Flush()
            End Using
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            _recordingFailure = "CSV finalization failed: " & ex.Message
            Trace.TraceError(_recordingFailure)
            _status.Text = _recordingFailure
        End Try
    End Sub

    Private Const MaxReportPoints As Integer = 200000

    ' Spill complete timestamp/value blocks instead of silently thinning statistical evidence.
    Private Shared Sub ThinRecorded(state As SeriesState)
        Dim path As String = IO.Path.Combine(IO.Path.GetTempPath(), "EOEMS-analyzer-report-" & Guid.NewGuid().ToString("N") & ".bin")
        Dim complete = False
        Try
            Using writer As New BinaryWriter(New FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                For Each point In state.Recorded
                    writer.Write(point.T.Ticks)
                    writer.Write(point.V)
                Next
            End Using
            state.RecordedChunks.Add(path)
            state.Recorded = New List(Of ReportPoint)()
            complete = True
        Finally
            If Not complete AndAlso File.Exists(path) Then File.Delete(path)
        End Try
    End Sub

    Private Shared Sub DeleteReportChunks(paths As IEnumerable(Of String))
        For Each path In paths
            Try
                File.Delete(path)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Trace.TraceWarning("Analyzer report evidence cleanup failed: " & ex.Message)
            End Try
        Next
    End Sub

    Private NotInheritable Class ReportSnapshot
        Public Property Result As EquipmentHistoryResult
        Public ReadOnly Series As New List(Of Tuple(Of HistoryMetric, Integer, List(Of ReportPoint)))()
        Public ReadOnly Chunks As New Dictionary(Of String, List(Of String))()
        Public Property PdfPath As String = String.Empty
        Public Property RecordingWarning As String = String.Empty
    End Class

    Private _lastReport As ReportSnapshot
    Private _reportBusy As Boolean
    Private _reportSnapshotInFlight As ReportSnapshot
    Private _reportTask As Task = Task.CompletedTask
    Private _recordingFailure As String = String.Empty

    ' Takes over the recorded points of the run that just ended, so the next run can start with fresh lists.
    Private Sub CaptureReport()
        If _rows = 0 Then Return
        Dim endUtc = DateTime.UtcNow
        Dim recordingSettings = _recordingSettings()
        If recordingSettings Is Nothing Then Throw New InvalidOperationException("Analyzer equipment ratings could not be loaded for the report.")
        Dim ids = _activeEquipment.Select(Function(item) item.Id).ToList()
        Dim equipment As New HistoryEquipment With {.Id = "ANALYZER", .Name = "Online Analyzer - " & String.Join(", ", ids.Take(6)) & If(ids.Count > 6, " +" & (ids.Count - 6).ToString(Inv), ""),
            .Category = "Online analyzer recording", .IntervalSeconds = _intervalMs / 1000.0R,
            .Description = "This report covers only the equipment and channels selected for this recording. UPS output is reported per UPS; factory demand is represented by TOTAL (T1-T4), when selected. UPS outputs and transformer totals overlap electrically and must not be added together."}
        Dim snapshot As New ReportSnapshot With {.RecordingWarning = _recordingFailure}
        If _recordingFailure.Length > 0 Then equipment.Description &= " Recording warning: " & _recordingFailure
        Dim metrics As New List(Of HistoryMetric)()
        For index = 0 To _series.Count - 1
            Dim state = _series(index)
            Dim rating = RatedCapacity(state.Equipment, recordingSettings)
            Dim measurementBasis = If(state.Equipment.IsUps,
                "UPS " & state.Channel.Label & " telemetry; individual UPS only. AC input/output, bypass and battery/DC measurement points must not be mixed.",
                If(state.Equipment.Id = "TOTAL",
                    "Factory aggregate of T1-T4; it overlaps downstream UPS outputs.",
                    If(state.Channel.Unit = "kVA", "Calculated apparent power from phase V x I; scaling must be verified.", "Transformer meter reading.")))
            Dim estimate = UpsInputEstimates.Metrics().FirstOrDefault(Function(item) item.Id = state.Channel.UpsKey)
            If estimate IsNot Nothing Then measurementBasis = estimate.Basis
            If state.LastUnavailableReason.Length > 0 Then measurementBasis &=
                " Last recorded unavailable-channel reason (not a diagnosis for the entire period): " & state.LastUnavailableReason
            If state.Equipment.IsUps Then measurementBasis &=
                " Live dashed links bridge delayed/missed communication for display only. Recorded timestamps, missing samples, statistics and report continuity are not interpolated or filled."
            Dim device As UpsDevice = Nothing
            If state.Equipment.IsUps Then
                device = UpsSettingsStore.Current.Devices.FirstOrDefault(Function(item) item.Id = state.Equipment.Id)
                If device IsNot Nothing Then measurementBasis &= " Current registry model: " & If(device.Model.Length > 0, device.Model, "not specified") &
                    "; configuration verification: " & device.Verification.ToString() & ". Current metadata is not a historical datasheet or approved electrical operating limit."
            End If
            Dim metric As New HistoryMetric With {
                .Id = "S" & index.ToString(Inv),
                .Name = state.Equipment.Id & " | " & state.Channel.Label,
                .Unit = state.Channel.Unit,
                .IsEstimate = estimate IsNot Nothing,
                .EquipmentId = state.Equipment.Id,
                .SourceMetricId = If(state.Equipment.IsUps, state.Channel.UpsKey, state.Channel.TransformerIndex.ToString(Inv)),
                .SourceMetricName = state.Channel.Label,
                .SourcePhase = If(state.Equipment.IsUps, state.Channel.UpsPhase + 1, 0),
                .VoltageReference = If(estimate IsNot Nothing, estimate.VoltageReference,
                    If(state.Channel.VoltageMode = "LN", "L-N", If(state.Channel.VoltageMode = "LL", "L-L", String.Empty))),
                .EquipmentModel = If(device Is Nothing, String.Empty, device.Model),
                .ModelBasis = If(device Is Nothing, String.Empty, "Current registry (" & device.Verification.ToString() & "); not historical model verification or a datasheet"),
                .AtsBankNumber = If(device Is Nothing, CType(Nothing, Integer?), device.AtsBank),
                .ConfiguredSourceDescription = If(device Is Nothing OrElse UpsSettingsStore.Current.Bank(device.AtsBank) Is Nothing,
                    String.Empty, UpsSettingsStore.Current.Bank(device.AtsBank).SourceDescription),
                .TopologyBasis = If(device Is Nothing, String.Empty, "Current configured ATS membership/source description only; approved time-valid wiring and active ATS source are not recorded"),
                .Basis = measurementBasis,
                .RatedCapacityKva = rating.Item1,
                .CapacityBasis = rating.Item2
            }
            metrics.Add(metric)
            snapshot.Series.Add(Tuple.Create(metric, state.RecordedTotal, state.Recorded))
            snapshot.Chunks(metric.Id) = state.RecordedChunks
            state.RecordedChunks = New List(Of String)()
            state.Recorded = New List(Of ReportPoint)()
            state.RecordedTotal = 0
        Next
        equipment.Metrics = metrics
        Dim first = _series.Select(Function(item) If(item.FirstRecordedUtc, _runStartUtc)).DefaultIfEmpty(_runStartUtc).Min()
        Dim request As New EquipmentHistoryRequest With {.Equipment = equipment, .Metrics = metrics, .FromUtc = If(first < _runStartUtc, first, _runStartUtc), .ToUtc = endUtc}
        snapshot.Result = New EquipmentHistoryResult With {.Request = request, .GeneratedUtc = endUtc}
        Dim baseName = If(_filePath.Length > 0, Path.ChangeExtension(_filePath, Nothing).TrimEnd("."c), String.Empty)
        If baseName.Length = 0 Then
            Dim name = Regex.Replace(If(_fileName.Text.Trim().Length = 0, "Analyzer", _fileName.Text.Trim()), "[^\w\- ]", "_")
            baseName = Path.Combine(_folder.Text.Trim(), name & "_" & DateTime.Now.ToString("yyyyMMdd_HHmmss", Inv))
        End If
        snapshot.PdfPath = baseName & "_report.pdf"
        If _lastReport IsNot Nothing AndAlso Not Object.ReferenceEquals(_lastReport, _reportSnapshotInFlight) Then
            DeleteReportChunks(_lastReport.Chunks.Values.SelectMany(Function(paths) paths))
        End If
        _lastReport = snapshot
    End Sub

    Private Shared Iterator Function ReadReportSeries(snapshot As ReportSnapshot) As IEnumerable(Of Tuple(Of HistoryMetric, Integer, List(Of ReportPoint)))
        For Each series In snapshot.Series
            Dim points As New List(Of ReportPoint)()
            For Each path In snapshot.Chunks(series.Item1.Id)
                Using reader As New BinaryReader(File.OpenRead(path))
                    If reader.BaseStream.Length Mod 16 <> 0 Then Throw New InvalidDataException("Analyzer report evidence block is incomplete; no report was generated.")
                    While reader.BaseStream.Position < reader.BaseStream.Length
                        points.Add(New ReportPoint(New DateTime(reader.ReadInt64(), DateTimeKind.Utc), reader.ReadDouble()))
                    End While
                End Using
            Next
            points.AddRange(series.Item3)
            If points.Count <> series.Item2 Then Throw New InvalidDataException("Analyzer report count does not match preserved acquisition evidence.")
            Yield Tuple.Create(series.Item1, series.Item2, points)
        Next
    End Function

    Private Shared Function RatedCapacity(item As EquipmentItem, settings As RecordingSettings) As Tuple(Of Double?, String)
        If item.IsUps Then
            Dim device = UpsSettingsStore.Current.Devices.FirstOrDefault(Function(candidate) String.Equals(candidate.Id, item.Id, StringComparison.Ordinal))
            Return HistoryCapacity.Ups(device)
        End If
        Return HistoryCapacity.Transformer(settings, item.Id)
    End Function

    Private Sub WriteReportAsync(automatic As Boolean)
        If _lastReport Is Nothing OrElse _reportBusy Then Return
        _reportTask = WriteReportCoreAsync(automatic)
    End Sub

    Public Async Function FinishRecordingAsync() As Task
        If _state <> RunState.Idle Then StopRun("application is closing.")
        Dim pending As Task
        Do
            pending = _reportTask
            Await pending
        Loop While Not Object.ReferenceEquals(pending, _reportTask)
    End Function

    Private Async Function WriteReportCoreAsync(automatic As Boolean) As Task
        Dim snapshot = _lastReport
        If snapshot Is Nothing OrElse _reportBusy Then Return
        _reportBusy = True
        _reportSnapshotInFlight = snapshot
        _exportPdf.Enabled = False
        Try
            Await Task.Run(Sub()
                               Directory.CreateDirectory(Path.GetDirectoryName(snapshot.PdfPath))
                               Dim target = snapshot.PdfPath
                               Dim copy = 1
                               While File.Exists(target)
                                   copy += 1
                                   target = Path.ChangeExtension(snapshot.PdfPath, Nothing).TrimEnd("."c) & "_" & copy.ToString(Inv) & ".pdf"
                               End While
                               snapshot.PdfPath = target
                               Dim analysis = EquipmentAnalysis.BuildFromSeries(snapshot.Result, ReadReportSeries(snapshot))
                               Dim temporary = target & "." & Guid.NewGuid().ToString("N") & ".partial"
                               Try
                                   EquipmentReportRenderer.WritePdf(analysis, temporary)
                                   File.Move(temporary, target)
                               Finally
                                   If File.Exists(temporary) Then File.Delete(temporary)
                               End Try
                           End Sub)
            If IsHandleCreated AndAlso Not IsDisposed AndAlso _state = RunState.Idle Then
                _status.Text = "Idle - PDF analysis report saved to " & snapshot.PdfPath &
                    If(snapshot.RecordingWarning.Length = 0, "", " WARNING: " & snapshot.RecordingWarning)
            End If
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is InvalidOperationException OrElse
                                   TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException OrElse TypeOf ex Is ExternalException
            _status.Text = "Could not create the PDF report: " & ex.Message
        Finally
            If IsDisposed OrElse Not Object.ReferenceEquals(snapshot, _lastReport) Then DeleteReportChunks(snapshot.Chunks.Values.SelectMany(Function(paths) paths))
            _reportBusy = False
            _reportSnapshotInFlight = Nothing
            If Not IsDisposed Then SetControlsForState()
            If Not IsDisposed AndAlso _state = RunState.Idle AndAlso Not Object.ReferenceEquals(snapshot, _lastReport) Then WriteReportAsync(True)
        End Try
    End Function

    Private Sub OnExportPdfClicked(sender As Object, e As EventArgs)
        If _state <> RunState.Idle OrElse _lastReport Is Nothing Then Return
        WriteReportAsync(False)
    End Sub

    Private Sub StopRun(message As String)
        If _state = RunState.Idle Then Return
        If _analyzerService IsNot Nothing Then
            If _analyzerService.AnalyzerDroppedSamples > 0 Then _recordingFailure =
                "Analyzer response buffer overflow: " & _analyzerService.AnalyzerDroppedSamples.ToString(Inv) & " responses lost."
            Dim pending = _analyzerService.StopAnalyzerPollingAndDrain()
            If _state = RunState.Recording AndAlso Not _paused Then
                Try
                    For Each snapshot In pending.OrderBy(Function(item) item.TimestampUtc)
                        SampleReadings(Array.Empty(Of MeterSnapshot)(), New Dictionary(Of String, UpsSnapshot)(StringComparer.OrdinalIgnoreCase) From {{snapshot.DeviceId, snapshot}})
                    Next
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is InvalidOperationException
                    _recordingFailure = "Recording finalization failed: " & ex.Message
                    Trace.TraceError(_recordingFailure)
                End Try
            End If
        End If
        _analyzerService = Nothing
        FlushShots()
        CloseEvents()
        CloseFile()
        Dim saved = If(_filePath.Length > 0 AndAlso _rows > 0 AndAlso _recordingFailure.Length = 0,
            " " & _rows.ToString(Inv) & " rows saved to " & _filePath, "")
        If _eventCount > 0 Then saved &= " " & _eventCount.ToString(Inv) & " events in " & _eventDir
        Dim hadRecording = _state = RunState.Recording AndAlso _rows > 0
        If hadRecording Then CaptureReport()
        _state = RunState.Idle
        _paused = False
        SetControlsForState()
        _status.Text = "Idle - " & message & saved & If(_recordingFailure.Length = 0, "", " WARNING: " & _recordingFailure)
        RebuildTriggerList()
        RefreshDisplay(True)
        If hadRecording Then WriteReportAsync(True)
    End Sub

    Private Sub ClearDisplay()
        For Each item In _series
            item.Points.Clear()
            item.Last = Nothing
            If _state <> RunState.Recording Then item.ResetStatistics()
        Next
        _traceStartUtc = DateTime.MinValue
        _plotNowUtc = DateTime.MinValue
        _revealFromUtc = DateTime.MinValue
        _revealTargetUtc = DateTime.MinValue
        _plotDirty = True
        FillGrid()
        RefreshDisplay(True)
    End Sub

    Private Sub OnBrowseClicked(sender As Object, e As EventArgs)
        Using dialog As New FolderBrowserDialog With {.Description = "Choose where the analyzer files are saved",
                                                      .UseDescriptionForTitle = True, .SelectedPath = _folder.Text}
            If dialog.ShowDialog(FindForm()) = DialogResult.OK Then
                _folder.Text = dialog.SelectedPath
                SaveFolder(dialog.SelectedPath)
            End If
        End Using
    End Sub

    Private Sub OnOpenFolderClicked(sender As Object, e As EventArgs)
        Try
            Directory.CreateDirectory(_folder.Text.Trim())
            Process.Start(New ProcessStartInfo("explorer.exe", """" & _folder.Text.Trim() & """") With {.UseShellExecute = True})
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
                                   TypeOf ex Is ArgumentException OrElse TypeOf ex Is ComponentModel.Win32Exception
            _status.Text = "Cannot open the folder: " & ex.Message
        End Try
    End Sub

    Private Shared ReadOnly SettingsFile As String = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EOEMS_SCADA", "online-analyzer-folder.txt")

    Private Sub LoadFolder()
        Dim folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "EOEMS_SCADA", "Online Analyzer")
        Try
            If File.Exists(SettingsFile) Then
                Dim saved = File.ReadAllText(SettingsFile).Trim()
                If saved.Length > 0 Then folder = saved
            End If
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
        End Try
        _folder.Text = folder
    End Sub

    Private Shared Sub SaveFolder(folder As String)
        Try
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile))
            File.WriteAllText(SettingsFile, folder)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
        End Try
    End Sub

    Private Sub SetControlsForState()
        Dim idle = _state = RunState.Idle
        _equipmentList.Enabled = idle
        _channelList.Enabled = idle
        _quickSelect.Enabled = idle
        _includeInputEstimates.Enabled = idle
        _interval.Enabled = idle
        _duration.Enabled = idle
        _triggerMode.Enabled = idle
        _triggerSeries.Enabled = idle
        _triggerLevel.Enabled = idle
        For Each control In {CType(_eventEnable, Control), _eventMode, _eventWatch, _eventLevel, _eventGap, _eventSpan, _eventShots}
            control.Enabled = idle
        Next
        _saveToFile.Enabled = idle
        _folder.Enabled = idle
        _browse.Enabled = idle
        _fileName.Enabled = idle
        _start.Enabled = idle
        _pause.Enabled = Not idle
        _pause.Text = If(_paused, "Resume", "Pause")
        _stop.Enabled = Not idle
        _exportPdf.Enabled = idle AndAlso _lastReport IsNot Nothing AndAlso Not _reportBusy
    End Sub

    Private Sub UpdateStatus()
        Select Case _state
            Case RunState.Idle
                Return
            Case RunState.Armed
                _status.Text = If(_paused, "Paused", "Armed") & " - waiting for " & _triggerState.Name & If(_triggerAbove, " to rise above ", " to fall below ") &
                    _triggerThreshold.ToString("0.###", Inv) & " (the live trace is already running)."
            Case Else
                Dim elapsed = DateTime.UtcNow - _runStartUtc
                _status.Text = If(_paused, "Paused", "Recording") & " - " & elapsed.ToString("hh\:mm\:ss", Inv) & "   rows: " & _rows.ToString(Inv) &
                    "   target: " & (_intervalMs / 1000.0R).ToString("0.###", Inv) & " s" & _upsTiming &
                    If(_series.Any(Function(item) item.ReadingQuality.StartsWith("Unavailable", StringComparison.Ordinal)),
                       "   Selected channels unavailable; see Reading quality.", "") &
                    If(_rows = 0, "   waiting for a fresh acquisition from " &
                        String.Join(", ", _activeEquipment.Take(3).Select(Function(item) item.Name)) &
                        If(_activeEquipment.Count > 3, " + " & (_activeEquipment.Count - 3).ToString(Inv) & " more", ""), "") &
                    If(_durationSeconds > 0, "   stops after " & TimeSpan.FromSeconds(_durationSeconds).ToString("hh\:mm\:ss", Inv), "")
        End Select
    End Sub

    Private Sub OnTick(sender As Object, e As EventArgs)
        If _state = RunState.Idle OrElse _paused Then Return
        Try
            Sample()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is InvalidOperationException
            _recordingFailure = "Recording failed: " & ex.Message
            StopRun(_recordingFailure)
            Return
        End Try
        ProcessDueShots()
        If _state = RunState.Recording AndAlso _durationSeconds > 0 AndAlso (DateTime.UtcNow - _runStartUtc).TotalSeconds >= _durationSeconds Then
            StopRun("the selected recording time finished.")
            Return
        End If
        If (DateTime.UtcNow - _lastRefreshUtc).TotalMilliseconds >= 500 Then
            _lastRefreshUtc = DateTime.UtcNow
            UpdateStatus()
            If _pageActive Then RefreshDisplay()
        End If
        If _pageActive AndAlso _displayClock.ElapsedMilliseconds - _lastFrameMilliseconds >= 30 Then
            _lastFrameMilliseconds = _displayClock.ElapsedMilliseconds
            RefreshChart()
        End If
    End Sub

    Private Sub Sample()
        Dim now = DateTime.UtcNow
        If _analyzerService Is Nothing AndAlso (now - _lastSampleUtc).TotalMilliseconds < _intervalMs Then Return
        Dim transformers = If(_activeEquipment.Any(Function(item) Not item.IsUps), _transformers(), Array.Empty(Of MeterSnapshot)())
        Dim ups = If(_analyzerService IsNot Nothing, _analyzerService.GetAnalyzerSnapshots(), _upsSnapshots())
        If _analyzerService IsNot Nothing Then
            Dim selected = _activeEquipment.Where(Function(item) item.IsUps AndAlso ups.ContainsKey(item.Id)).
                Select(Function(item) ups(item.Id)).ToArray()
            Dim good = selected.Where(Function(snapshot) snapshot.IsGood).ToArray()
            _upsTiming = If(good.Length = 0, "   UPS: waiting / no valid response",
                "   UPS request: " & good.Max(Function(snapshot) snapshot.RoundTripMilliseconds.GetValueOrDefault()).ToString("0", Inv) &
                " ms; age: " & Math.Max(0, (now - good.Min(Function(snapshot) snapshot.TimestampUtc)).TotalMilliseconds).ToString("0", Inv) & " ms")
            If selected.Any(Function(snapshot) Not snapshot.IsGood) Then
                _upsTiming &= "   " & String.Join("; ", selected.Where(Function(snapshot) Not snapshot.IsGood).
                    Select(Function(snapshot) snapshot.DeviceId & ": " & snapshot.QualityDetail))
            End If
        End If
        If _analyzerService Is Nothing Then
            SampleReadings(transformers, ups)
        Else
            If _analyzerService.AnalyzerDroppedSamples > 0 Then
                Throw New InvalidOperationException("Analyzer response buffer overflow: " &
                    _analyzerService.AnalyzerDroppedSamples.ToString(Inv) & " responses lost. Recording stopped; shorten the run or reduce selected channels/rate.")
            End If
            SampleReadings(transformers, New Dictionary(Of String, UpsSnapshot)())
            For Each snapshot In _analyzerService.DrainAnalyzerSnapshots().OrderBy(Function(item) item.TimestampUtc)
                SampleReadings(Array.Empty(Of MeterSnapshot)(), New Dictionary(Of String, UpsSnapshot)(StringComparer.OrdinalIgnoreCase) From {{snapshot.DeviceId, snapshot}})
            Next
        End If
    End Sub

    Private Sub SampleReadings(transformers As IReadOnlyList(Of MeterSnapshot), ups As IReadOnlyDictionary(Of String, UpsSnapshot))
        Dim now = DateTime.UtcNow
        Dim fresh As New Dictionary(Of String, Reading)(StringComparer.Ordinal)
        For Each item In _activeEquipment
            Dim reading = Acquire(item, transformers, ups)
            If reading Is Nothing Then Continue For
            Dim previous As DateTime
            If _lastCapture.TryGetValue(item.Id, previous) AndAlso reading.CapturedUtc <= previous Then Continue For
            _lastCapture(item.Id) = reading.CapturedUtc
            fresh(item.Id) = reading
            _quality(item.Id) = reading.Quality
        Next
        If fresh.Count = 0 Then Return
        _lastSampleUtc = now
        Dim stamp = fresh.Values.Max(Function(item) item.CapturedUtc)
        If _traceStartUtc = DateTime.MinValue Then _traceStartUtc = fresh.Values.Min(Function(item) item.CapturedUtc)
        If stamp > _revealTargetUtc Then
            _revealFromUtc = If(_revealTargetUtc = DateTime.MinValue, _traceStartUtc, RevealTime())
            _revealTargetUtc = stamp
            _revealStartedMilliseconds = _displayClock.ElapsedMilliseconds
        End If
        _plotDirty = True
        Dim values(_series.Count - 1) As Double?
        For index = 0 To _series.Count - 1
            Dim state = _series(index)
            Dim reading As Reading = Nothing
            If Not fresh.TryGetValue(state.Equipment.Id, reading) Then Continue For
            values(index) = ValueOf(reading, state.Channel)
            If values(index).HasValue AndAlso Not Double.IsFinite(values(index).Value) Then values(index) = Nothing
            state.Last = values(index)
            state.ReadingQuality = ChannelQuality(reading, state.Channel, values(index))
            state.Points.Add(New AnalyticsTrendPoint With {.TimestampUtc = reading.CapturedUtc, .Value = values(index), .IsValid = values(index).HasValue,
                .IsCommunicationGap = reading.Ups IsNot Nothing AndAlso Not values(index).HasValue AndAlso
                    (reading.Ups.Quality = UpsQuality.Timeout OrElse reading.Ups.Quality = UpsQuality.Offline)})
            TrimPoints(state)
        Next
        For Each item In fresh
            Dim missing = _series.Where(Function(state) state.Equipment.Id = item.Key AndAlso
                state.ReadingQuality.StartsWith("Unavailable", StringComparison.Ordinal)).
                Select(Function(state) state.Channel.Label & ": " & state.ReadingQuality).ToArray()
            If missing.Length > 0 Then item.Value.Quality &= "; unavailable: " & String.Join(" | ", missing)
            If _series.Any(Function(state) state.Equipment.Id = item.Key AndAlso UpsInputEstimates.IsEstimateKey(state.Channel.UpsKey)) Then
                item.Value.Quality &= "; " & UpsInputEstimates.Assumptions
            End If
            _quality(item.Key) = item.Value.Quality
        Next
        If _eventActive Then DetectEvents(values)
        If _state = RunState.Armed Then
            Dim triggerIndex = _series.IndexOf(_triggerState)
            Dim value = values(triggerIndex)
            If value.HasValue AndAlso If(_triggerAbove, value.Value > _triggerThreshold, value.Value < _triggerThreshold) Then
                If Not BeginRecording() Then
                    StopRun(_status.Text)
                    Return
                End If
            Else
                Return
            End If
        End If
        If _state <> RunState.Recording Then Return
        For index = 0 To _series.Count - 1
            Dim state = _series(index)
            Dim reading As Reading = Nothing
            If Not fresh.TryGetValue(state.Equipment.Id, reading) Then Continue For
            state.RecordedTotal += 1
            If Not values(index).HasValue Then state.LastUnavailableReason = state.ReadingQuality
            If Not state.FirstRecordedUtc.HasValue Then state.FirstRecordedUtc = reading.CapturedUtc
            If values(index).HasValue Then
                state.Accumulate(values(index).Value)
            End If
            state.Recorded.Add(New ReportPoint(reading.CapturedUtc, If(values(index), Double.NaN)))
        Next
        _rows += 1
        If _writer Is Nothing Then
            For Each state In _series
                If state.Recorded.Count >= MaxReportPoints Then ThinRecorded(state)
            Next
            Return
        End If
        Dim fields As New List(Of String) From {
            stamp.ToString("o", Inv), stamp.ToLocalTime().ToString("o", Inv)}
        fields.AddRange(values.Select(Function(value) If(value.HasValue, value.Value.ToString("0.####", Inv), "")))
        fields.AddRange(_activeEquipment.Select(Function(item) If(fresh.ContainsKey(item.Id), fresh(item.Id).Quality, "")))
        fields.AddRange(_activeEquipment.Select(Function(item) If(fresh.ContainsKey(item.Id), fresh(item.Id).CapturedUtc.ToString("o", Inv), "")))
        _writer.WriteLine(String.Join(",", fields.Select(AddressOf Csv)))
        _writer.Flush()
        For Each state In _series
            If state.Recorded.Count >= MaxReportPoints Then ThinRecorded(state)
        Next
    End Sub

    Private Shared Function ParseNumber(text As String, ByRef value As Double) As Boolean
        Return Double.TryParse(text.Trim(), NumberStyles.Float, Inv, value) OrElse
               Double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, value)
    End Function

    Private Shared Function PriorValues(state As SeriesState, count As Integer) As List(Of Double)
        Dim result As New List(Of Double)(count)
        For index = state.Points.Count - 2 To 0 Step -1
            If state.Points(index).IsValid Then result.Add(state.Points(index).Value)
            If result.Count >= count Then Exit For
        Next
        Return result
    End Function

    Private Sub DetectEvents(values As Double?())
        For index = 0 To _series.Count - 1
            If Not values(index).HasValue Then Continue For
            Dim state = _series(index)
            If _eventWatchState IsNot Nothing AndAlso Not Object.ReferenceEquals(state, _eventWatchState) Then Continue For
            Dim prior = PriorValues(state, If(_eventKind = 0, 5, 1))
            If prior.Count = 0 Then Continue For
            Dim value = values(index).Value
            Dim stamp = state.Points(state.Points.Count - 1).TimestampUtc
            If state.LastEventUtc <> DateTime.MinValue AndAlso (stamp - state.LastEventUtc).TotalSeconds < _eventGapLimit Then Continue For
            Dim before = prior.Average()
            Dim kind As String = Nothing
            Select Case _eventKind
                Case 0
                    If Math.Abs(before) > 0.000000001 Then
                        Dim percent = (value - before) / Math.Abs(before) * 100.0R
                        If Math.Abs(percent) >= _eventThreshold Then kind = "Sudden change " & percent.ToString("+0.#;-0.#", Inv) & " %"
                    End If
                Case 1
                    Dim delta = value - before
                    If Math.Abs(delta) >= _eventThreshold Then kind = "Sudden change " & delta.ToString("+0.###;-0.###", Inv) & " " & state.Channel.Unit
                Case 2
                    If value > _eventThreshold AndAlso before <= _eventThreshold Then kind = "Rose above " & _eventThreshold.ToString("0.###", Inv)
                Case Else
                    If value < _eventThreshold AndAlso before >= _eventThreshold Then kind = "Fell below " & _eventThreshold.ToString("0.###", Inv)
            End Select
            If kind IsNot Nothing Then RegisterEvent(state, kind, before, value, stamp)
        Next
    End Sub

    Private Sub RegisterEvent(state As SeriesState, kind As String, before As Double, after As Double, stamp As DateTime)
        state.LastEventUtc = stamp
        _eventCount += 1
        Dim record As New EventRecord With {.Number = _eventCount, .TimeUtc = stamp, .Series = state, .Kind = kind, .Before = before, .After = after}
        Dim shot = _eventShots.Checked AndAlso _shotCount < MaximumScreenshots
        Dim shotName = String.Empty
        If shot Then
            _shotCount += 1
            shotName = "Event_" & _eventCount.ToString("0000", Inv) & "_" & Regex.Replace(state.Name, "[^\w\-]+", "_") & "_" &
                stamp.ToLocalTime().ToString("HHmmss", Inv) & ".png"
            record.ShotPath = Path.Combine(_eventDir, shotName)
            record.DueUtc = DateTime.UtcNow.AddSeconds(_eventSpanLimit * 0.3R)
            _pendingShots.Add(record)
        End If
        If _eventGrid.Rows.Count >= 1000 Then _eventGrid.Rows.RemoveAt(0)
        _eventGrid.Visible = True
        Dim rowIndex = _eventGrid.Rows.Add(_eventCount.ToString(Inv), stamp.ToLocalTime().ToString("HH:mm:ss.f", Inv), state.Name, kind,
            before.ToString("0.###", Inv), after.ToString("0.###", Inv), If(shot, "capturing…", If(_eventShots.Checked, "limit reached", "")))
        record.Row = _eventGrid.Rows(rowIndex)
        If _eventGrid.IsHandleCreated AndAlso _eventGrid.Visible AndAlso _eventGrid.DisplayedRowCount(False) > 0 Then
            _eventGrid.FirstDisplayedScrollingRowIndex = rowIndex
        End If
        Try
            Directory.CreateDirectory(_eventDir)
            If _eventWriter Is Nothing Then
                _eventWriter = New StreamWriter(New FileStream(Path.Combine(_eventDir, "events.csv"), FileMode.Append, FileAccess.Write, FileShare.Read), New UTF8Encoding(True))
                If _eventWriter.BaseStream.Length = 0 Then
                    _eventWriter.WriteLine(String.Join(",", {"Event", "Timestamp UTC", "Timestamp local", "Equipment", "Channel", "Unit", "Type",
                        "Value before", "Value after", "Screenshot"}.Select(AddressOf Csv)))
                End If
            End If
            _eventWriter.WriteLine(String.Join(",", {_eventCount.ToString(Inv), stamp.ToString("yyyy-MM-dd HH:mm:ss.f", Inv),
                stamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.f", Inv), state.Equipment.Id, state.Channel.Label, state.Channel.Unit, kind,
                before.ToString("0.####", Inv), after.ToString("0.####", Inv), shotName}.Select(AddressOf Csv)))
            _eventWriter.Flush()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException
            _eventActive = False
            _pendingShots.Clear()
            _status.Text = "Event capture stopped, cannot write to " & _eventDir & ": " & ex.Message
        End Try
    End Sub

    Private Sub CloseEvents()
        If _eventWriter Is Nothing Then Return
        Try
            _eventWriter.Flush()
            _eventWriter.Dispose()
        Catch ex As IOException
        End Try
        _eventWriter = Nothing
    End Sub

    Private Sub ProcessDueShots()
        If _pendingShots.Count = 0 Then Return
        Dim now = DateTime.UtcNow
        Dim due = _pendingShots.Where(Function(item) item.DueUtc <= now).ToList()
        For Each record In due
            _pendingShots.Remove(record)
            RenderShot(record)
        Next
    End Sub

    Private Sub FlushShots()
        For Each record In _pendingShots.ToList()
            RenderShot(record)
        Next
        _pendingShots.Clear()
    End Sub

    Private Sub RenderShot(record As EventRecord)
        Dim result As String
        Try
            Dim unit = record.Series.Channel.Unit
            Dim spanSeconds = _eventSpanLimit
            Dim startUtc = record.TimeUtc.AddSeconds(-spanSeconds * 0.7R)
            Dim endUtc = record.TimeUtc.AddSeconds(spanSeconds * 0.3R)
            Using chart As New AnalyticsTrendChart With {.Width = 1200, .Height = 560, .UseLocalTime = True, .XAxisLabel = "Time (local)",
                                                          .YAxisLabel = unit, .ChartType = AnalyticsChartType.Line, .ShowMarkers = spanSeconds <= 30,
                                                          .PollingIntervalSeconds = Math.Max(5, spanSeconds \ 100)}
                chart.ChartTitle = "Event " & record.Number.ToString(Inv) & " - " & record.Series.Name
                chart.Series = _series.Where(Function(item) item.Channel.Unit = unit).Select(Function(item) New AnalyticsTrendSeries With {
                    .Name = item.Name, .Color = item.Color,
                    .Points = item.Points.Where(Function(point) point.TimestampUtc >= startUtc AndAlso point.TimestampUtc <= endUtc).ToArray()}).ToArray()
                chart.WindowStartUtc = startUtc
                chart.WindowEndUtc = endUtc
                Using plot As New Bitmap(chart.Width, chart.Height)
                    chart.DrawToBitmap(plot, New Rectangle(0, 0, chart.Width, chart.Height))
                    Using image As New Bitmap(plot.Width, plot.Height + 44)
                        Using canvas = Graphics.FromImage(image)
                            canvas.Clear(Color.White)
                            Using font As New Font("Segoe UI", 11.0F, FontStyle.Bold), brush As New SolidBrush(Color.FromArgb(180, 40, 40))
                                canvas.DrawString("Event " & record.Number.ToString(Inv) & " at " & record.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.f", Inv) &
                                    "  |  " & record.Series.Name & "  |  " & record.Kind & "  |  " & record.Before.ToString("0.###", Inv) & " -> " &
                                    record.After.ToString("0.###", Inv) & "  (event at 70 % of the time axis)", font, brush, 8, 10)
                            End Using
                            canvas.DrawImage(plot, 0, 44)
                        End Using
                        Directory.CreateDirectory(Path.GetDirectoryName(record.ShotPath))
                        image.Save(record.ShotPath, Imaging.ImageFormat.Png)
                    End Using
                End Using
            End Using
            result = Path.GetFileName(record.ShotPath)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException OrElse
                                   TypeOf ex Is System.Runtime.InteropServices.ExternalException OrElse TypeOf ex Is InvalidOperationException
            result = "failed: " & ex.Message
            record.ShotPath = String.Empty
        End Try
        If record.Row IsNot Nothing AndAlso record.Row.DataGridView IsNot Nothing Then
            record.Row.Cells(6).Value = result
            record.Row.Tag = record.ShotPath
        End If
    End Sub

    Private Sub OnEventDoubleClick(sender As Object, e As DataGridViewCellEventArgs)
        If e.RowIndex < 0 Then Return
        Dim path = TryCast(_eventGrid.Rows(e.RowIndex).Tag, String)
        If String.IsNullOrEmpty(path) OrElse Not File.Exists(path) Then Return
        Try
            Process.Start(New ProcessStartInfo(path) With {.UseShellExecute = True})
        Catch ex As Exception When TypeOf ex Is ComponentModel.Win32Exception OrElse TypeOf ex Is IOException
            _status.Text = "Cannot open the screenshot: " & ex.Message
        End Try
    End Sub

    Private Sub TrimPoints(state As SeriesState)
        Dim cutoff = DateTime.UtcNow.AddSeconds(-WindowSeconds(WindowSeconds.Length - 1) - 30)
        Dim stale = 0
        While stale < state.Points.Count AndAlso state.Points(stale).TimestampUtc < cutoff
            stale += 1
        End While
        If stale > 0 Then state.Points.RemoveRange(0, stale)
        If state.Points.Count > MaximumPointsPerSeries Then
            ' Keep a full day in memory: thin the oldest half to every second point instead of dropping it.
            Dim half = state.Points.Count \ 2
            Dim thinned As New List(Of AnalyticsTrendPoint)(MaximumPointsPerSeries)
            For index = 0 To half - 1 Step 2
                thinned.Add(state.Points(index))
            Next
            thinned.AddRange(state.Points.Skip(half))
            state.Points.Clear()
            state.Points.AddRange(thinned)
        End If
    End Sub

    Private Shared Function Acquire(item As EquipmentItem, transformers As IReadOnlyList(Of MeterSnapshot),
                                    ups As IReadOnlyDictionary(Of String, UpsSnapshot)) As Reading
        If item.IsUps Then
            Dim snapshot As UpsSnapshot = Nothing
            If ups Is Nothing OrElse Not ups.TryGetValue(item.Id, snapshot) OrElse snapshot Is Nothing Then Return Nothing
            Return New Reading With {.CapturedUtc = snapshot.TimestampUtc,
                .Quality = snapshot.Quality.ToString() & If(snapshot.IsGood, "", ": " & snapshot.QualityDetail), .Ups = snapshot}
        End If
        If transformers Is Nothing Then Return Nothing
        If item.Id = "TOTAL" Then
            Dim four = transformers.Where(Function(snapshot) {"T1", "T2", "T3", "T4"}.Contains(snapshot.TransformerName)).
                OrderBy(Function(snapshot) snapshot.TransformerName).ToList()
            If four.Count <> 4 OrElse four.Select(Function(snapshot) snapshot.TransformerName).Distinct().Count() <> 4 OrElse
                four.Any(Function(snapshot) Not CapturedOf(snapshot).HasValue) Then Return Nothing
            Dim captured = four.Max(Function(snapshot) CapturedOf(snapshot).Value)
            If four.Any(Function(snapshot) snapshot.AcquisitionCycleId > 0) AndAlso
                (four.Any(Function(snapshot) snapshot.AcquisitionCycleId <= 0) OrElse four.Select(Function(snapshot) snapshot.AcquisitionCycleId).Distinct().Count() <> 1) Then
                Return New Reading With {.CapturedUtc = captured, .Quality = "Unavailable: transformers belong to different acquisition cycles"}
            End If
            If (captured - four.Min(Function(snapshot) CapturedOf(snapshot).Value)).TotalSeconds > 2 Then
                Return New Reading With {.CapturedUtc = captured, .Quality = "Unavailable: transformer acquisition timestamps are not aligned"}
            End If
            If four.All(Function(snapshot) snapshot.Quality = MeterQuality.Good AndAlso snapshot.DecodedValues IsNot Nothing) Then
                Return New Reading With {.CapturedUtc = captured, .Quality = "Good",
                    .Channels = ElectricalCalculations.TotalChannels(four.Select(Function(snapshot) CType(snapshot.DecodedValues, IReadOnlyList(Of Single))).ToArray())}
            End If
            Return New Reading With {.CapturedUtc = captured, .Quality = "Not all transformers good"}
        End If
        Dim own = transformers.FirstOrDefault(Function(snapshot) snapshot.TransformerName = item.Id)
        If own Is Nothing OrElse Not CapturedOf(own).HasValue Then Return Nothing
        If own.Quality = MeterQuality.Good AndAlso own.DecodedValues IsNot Nothing Then
            Return New Reading With {.CapturedUtc = CapturedOf(own).Value, .Quality = "Good", .Channels = ElectricalCalculations.TransformerChannels(own.DecodedValues)}
        End If
        Return New Reading With {.CapturedUtc = CapturedOf(own).Value, .Quality = own.Quality.ToString()}
    End Function

    Private Shared Function CapturedOf(snapshot As MeterSnapshot) As DateTime?
        Return If(snapshot.CapturedAtUtc, snapshot.AttemptStartedAtUtc)
    End Function

    Private Shared Function ValueOf(reading As Reading, channel As ChannelItem) As Double?
        If channel.IsUps Then
            If reading.Ups Is Nothing OrElse Not reading.Ups.IsGood Then Return Nothing
            If channel.UpsKey.StartsWith("@", StringComparison.Ordinal) Then
                If UpsInputEstimates.IsEstimateKey(channel.UpsKey) Then Return UpsInputEstimates.Calculate(reading.Ups, channel.UpsKey)
                Select Case channel.UpsKey
                    Case "@OutputKw" : Return reading.Ups.OutputKw
                    Case "@OutputKva" : Return reading.Ups.OutputKvaDerived
                    Case "@OutputPf" : Return reading.Ups.PowerFactorDerived
                    Case "@OutputLoadMax" : Return reading.Ups.PhaseMax("OutputLoad")
                End Select
                Return Nothing
            End If
            If channel.UpsPhase < 0 Then Return reading.Ups.Value(channel.UpsKey)
            Dim phases As Double?() = Nothing
            Dim key = channel.UpsKey
            If channel.VoltageMode.Length > 0 Then
                ' The card reports either phase-to-neutral or phase-to-phase; never convert or guess one from the other.
                Dim explicitKey = If(key = "InputVoltage", "InputVoltage" & channel.VoltageMode,
                    If(channel.VoltageMode = "LL", "OutputVoltageLL", String.Empty))
                If reading.Ups.Phases IsNot Nothing AndAlso reading.Ups.Phases.TryGetValue(explicitKey, phases) AndAlso
                   phases IsNot Nothing AndAlso channel.UpsPhase < phases.Length Then Return phases(channel.UpsPhase)
                Dim reference = If(key = "InputVoltage", reading.Ups.InputVoltageReference, reading.Ups.OutputVoltageReference)
                If channel.VoltageMode = "LN" Then
                    If reference <> "L-N" Then Return Nothing
                ElseIf key = "OutputVoltage" Then
                    key = "OutputVoltageLL"
                ElseIf Not reference.StartsWith("L-L", StringComparison.Ordinal) Then
                    Return Nothing
                End If
            End If
            If reading.Ups.Phases IsNot Nothing AndAlso reading.Ups.Phases.TryGetValue(key, phases) AndAlso
               phases IsNot Nothing AndAlso channel.UpsPhase < phases.Length Then Return phases(channel.UpsPhase)
            Return Nothing
        End If
        If reading.Channels Is Nothing OrElse channel.TransformerIndex >= reading.Channels.Length Then Return Nothing
        Dim value = reading.Channels(channel.TransformerIndex)
        Return If(Double.IsFinite(value), CType(value, Double?), Nothing)
    End Function

    Private Shared Function ChannelQuality(reading As Reading, channel As ChannelItem, value As Double?) As String
        If reading.Ups IsNot Nothing AndAlso UpsInputEstimates.IsEstimateKey(channel.UpsKey) Then
            Return If(value.HasValue, "Good - ESTIMATED (calculated; not measured)",
                "Unavailable: " & UpsInputEstimates.MissingReason(reading.Ups, channel.UpsKey))
        End If
        If value.HasValue OrElse reading.Ups Is Nothing OrElse Not reading.Ups.IsGood Then Return reading.Quality
        If channel.UpsKey.StartsWith("@", StringComparison.Ordinal) Then
            Return "Unavailable: required measured total or complete valid phases are absent, or the derived ratio is invalid. " &
                If(reading.Ups.PowerConsistencyIssue.Length > 0, reading.Ups.PowerConsistencyIssue, "Missing is not measured zero.")
        End If
        Return "Unavailable: " & reading.Ups.MissingChannelReason(channel.UpsKey, channel.UpsPhase,
            If(channel.VoltageMode = "LN", "L-N", If(channel.VoltageMode = "LL", "L-L", ""))) &
            If(channel.UpsKey = "InputPower",
                " Select Estimated Input Apparent Power (V x I) for calculated kVA, not measured watts. Input kW cannot be inferred from V x I alone or output power factor.", "")
    End Function

    Private Shared Function Csv(field As String) As String
        If field Is Nothing Then Return String.Empty
        If field.IndexOfAny({","c, """"c, ControlChars.Cr, ControlChars.Lf}) < 0 Then Return field
        Return """" & field.Replace("""", """""") & """"
    End Function

    Private Sub FillUnitChoices()
        _unitChoice.Items.Clear()
        _unitChoice.Items.Add(AllGraphUnits)
        For Each unit In _series.Select(Function(item) If(item.Channel.Unit.Length = 0, "(no unit)", item.Channel.Unit)).Distinct()
            _unitChoice.Items.Add(unit)
        Next
        _unitChoice.SelectedIndex = 0
        BuildGraphGroups()
    End Sub

    Private Sub ConfigureLiveChart(chart As AnalyticsTrendChart)
        chart.Dock = DockStyle.Fill
        chart.ChartType = AnalyticsChartType.Line
        chart.ShowMarkers = True
        chart.UseLocalTime = True
        chart.XAxisLabel = "Time (local)"
        chart.MinimumZoomSeconds = 1
        chart.ExternalWheelZoom = True
        chart.ShowInteractionHints = False
        chart.TimestampDescription = "Actual host acquisition time for this equipment; not a verified internal meter timestamp."
        AddHandler chart.WheelZoomRequested, Sub(sender, zoomIn)
                                                If _chartGroups.Count > 1 AndAlso (ModifierKeys And Keys.Control) = Keys.None Then
                                                    _chartScroll.AutoScrollPosition = New Point(0, Math.Max(0, -_chartScroll.AutoScrollPosition.Y + If(zoomIn, -120, 120)))
                                                    Return
                                                End If
                                                Dim target = _windowChoice.SelectedIndex + If(zoomIn, -1, 1)
                                                If target >= 0 AndAlso target < WindowSeconds.Length Then _windowChoice.SelectedIndex = target
                                            End Sub
    End Sub

    Private Shared Function GraphFamily(channel As ChannelItem) As String
        Dim label = channel.Label
        Dim family = If(channel.IsUps, "UPS", "Transformer")
        If UpsInputEstimates.IsEstimateKey(channel.UpsKey) Then Return family &
            If(channel.UpsKey = UpsInputEstimates.VoltageKey, " input ESTIMATED (L-L equivalent)", " input ESTIMATED (V x I)") &
            " [" & channel.Unit & "]"
        If label.StartsWith("Input ", StringComparison.Ordinal) Then family &= " input"
        If label.StartsWith("Output ", StringComparison.Ordinal) Then family &= " output"
        If label.Contains("Battery", StringComparison.Ordinal) OrElse label.Contains("Charge", StringComparison.Ordinal) Then family &= " battery / DC"
        If label.Contains("DC bus", StringComparison.OrdinalIgnoreCase) OrElse channel.UpsKey.Contains("DcBus", StringComparison.OrdinalIgnoreCase) Then family &= " DC bus"
        If label.Contains("Voltage", StringComparison.Ordinal) Then
            family &= " voltage" & If(label.Contains("-N", StringComparison.Ordinal), " L-N",
                If(channel.VoltageMode = "LL" OrElse Regex.IsMatch(label, "L[123]-L[123]"), " L-L", ""))
        ElseIf channel.Unit = "A" Then
            family &= " current"
        Else
            family &= " " & Regex.Replace(label, "\s+L[123](?:-[NL123]+)?$", "")
        End If
        Return family & " [" & If(channel.Unit.Length = 0, "no unit", channel.Unit) & "]"
    End Function

    Private Sub BuildGraphGroups()
        If _chartStack Is Nothing OrElse IsDisposed Then Return
        _chartStack.SuspendLayout()
        Try
            _chartStack.Controls.Clear()
            For Each group In _chartGroups
                If group.Item1 IsNot _chart Then group.Item1.Dispose()
            Next
            _chartGroups.Clear()
            _chartStack.RowStyles.Clear()
            Dim unit = TryCast(_unitChoice.SelectedItem, String)
            Dim selected = _series.Where(Function(item) unit = AllGraphUnits OrElse
                unit = If(item.Channel.Unit.Length = 0, "(no unit)", item.Channel.Unit)).ToArray()
            For Each family In selected.GroupBy(Function(item) item.Equipment.Id & " | " & GraphFamily(item.Channel)).OrderBy(Function(group) GraphOrder(group.First().Channel))
                Dim members = family.ToArray()
                For offset = 0 To members.Length - 1 Step 6
                    Dim chunk = members.Skip(offset).Take(6).ToArray()
                    Dim chart = If(_chartGroups.Count = 0, _chart, New AnalyticsTrendChart())
                    If chart IsNot _chart Then ConfigureLiveChart(chart)
                    chart.ChartTitle = family.Key & If(members.Length > 6, " - group " & (offset \ 6 + 1).ToString(Inv), "")
                    chart.Tag = chart.ChartTitle
                    chart.YAxisLabel = chunk(0).Channel.Unit
                    _chartStack.RowStyles.Add(New RowStyle(SizeType.Absolute, 360))
                    _chartStack.Controls.Add(chart, 0, _chartGroups.Count)
                    _chartGroups.Add(Tuple.Create(chart, chunk))
                Next
            Next
            _chartStack.RowCount = _chartGroups.Count
            _graphInfo.Text = If(_series.Count = 0, String.Empty, selected.Length.ToString(Inv) & " / " & _series.Count.ToString(Inv) & " selected signals in " &
                _chartGroups.Count.ToString(Inv) & " graphs. Wheel: scroll all graphs; Ctrl+wheel: zoom. Equipment, input, output and battery/DC are separate." &
                If(unit = AllGraphUnits, "", " Filter active: " & unit & ". Choose All selected channels to restore all."))
            _plotDirty = True
        Finally
            _chartStack.ResumeLayout(True)
        End Try
        ResizeGraphRows()
    End Sub

    Private Sub ResizeGraphRows()
        If _chartStack.RowStyles.Count <> _chartGroups.Count Then Return
        Dim total = _chartStack.Padding.Vertical
        For index = 0 To _chartGroups.Count - 1
            Dim chart = _chartGroups(index).Item1
            Dim height = Math.Max(360, chart.MinimumReadableHeight) + chart.Margin.Vertical
            If _chartStack.RowStyles(index).Height <> height Then _chartStack.RowStyles(index).Height = height
            total += height
        Next
        _chartStack.Height = total
        _chartScroll.AutoScrollMinSize = New Size(0, total)
    End Sub

    Private Sub FillGrid()
        _grid.Rows.Clear()
        For Each item In _series
            _grid.Rows.Add(item.Name, item.Channel.Unit, "", "", "", "", "", "", "")
        Next
    End Sub

    Private Shared Function Downsample(points As List(Of AnalyticsTrendPoint)) As AnalyticsTrendPoint()
        If points.Count <= MaximumSeriesPoints Then Return points.ToArray()
        Dim selected As New SortedSet(Of Integer) From {0, points.Count - 1}
        Dim buckets = MaximumSeriesPoints \ 2
        For bucket = 0 To buckets - 1
            Dim first = CInt(Math.Floor(bucket * points.Count / CDbl(buckets)))
            Dim last = CInt(Math.Floor((bucket + 1) * points.Count / CDbl(buckets))) - 1
            Dim low = -1
            Dim high = -1
            For index = first To last
                If Not points(index).IsValid OrElse Not points(index).Value.HasValue OrElse Not Double.IsFinite(points(index).Value.Value) Then Continue For
                If low < 0 OrElse points(index).Value.Value < points(low).Value.Value Then low = index
                If high < 0 OrElse points(index).Value.Value > points(high).Value.Value Then high = index
            Next
            If low >= 0 Then selected.Add(low)
            If high >= 0 Then selected.Add(high)
        Next
        For index = 1 To points.Count - 1
            Dim previousValid = points(index - 1).IsValid AndAlso points(index - 1).Value.HasValue AndAlso Double.IsFinite(points(index - 1).Value.Value)
            Dim currentValid = points(index).IsValid AndAlso points(index).Value.HasValue AndAlso Double.IsFinite(points(index).Value.Value)
            If previousValid = currentValid AndAlso points(index - 1).IsCommunicationGap = points(index).IsCommunicationGap Then Continue For
            selected.Add(index - 1)
            selected.Add(index)
        Next
        Return selected.Select(Function(index) points(index)).ToArray()
    End Function

    Private Shared Function GraphLegendName(item As SeriesState) As String
        Dim phase = Regex.Match(item.Channel.Label, "L[123](?:-[NL123]+)?$")
        If Not phase.Success Then Return item.Name
        Dim equipment = If(item.Equipment.Id.StartsWith("UPS-", StringComparison.Ordinal), item.Equipment.Id.Substring(4), item.Equipment.Id)
        Return equipment & " " & phase.Value
    End Function

    Private Sub RefreshDisplay(Optional force As Boolean = False)
        If IsDisposed Then Return
        For index = 0 To Math.Min(_series.Count, _grid.Rows.Count) - 1
            Dim item = _series(index)
            Dim row = _grid.Rows(index)
            row.Cells(2).Value = If(item.Last.HasValue, item.Last.Value.ToString("0.###", Inv), "")
            Dim has = item.Count > 0
            row.Cells(3).Value = If(has, item.Minimum.ToString("0.###", Inv), "")
            row.Cells(4).Value = If(has, item.Maximum.ToString("0.###", Inv), "")
            row.Cells(5).Value = If(has, item.Mean.ToString("0.###", Inv), "")
            row.Cells(6).Value = If(item.Count > 1, Math.Sqrt(item.M2 / (item.Count - 1)).ToString("0.###", Inv), "")
            row.Cells(7).Value = item.Count.ToString(Inv)
            row.Cells(8).Value = item.ReadingQuality
        Next
        RefreshChart(force)
    End Sub

    Private Function RevealTime() As DateTime
        If _revealTargetUtc <= _revealFromUtc Then Return _revealTargetUtc
        Dim fraction = Math.Min(1.0R, Math.Max(0.0R, (_displayClock.ElapsedMilliseconds - _revealStartedMilliseconds) /
            CDbl(Math.Clamp(_intervalMs, 25, 120))))
        Return _revealFromUtc.AddTicks(CLng((_revealTargetUtc - _revealFromUtc).Ticks * fraction))
    End Function

    Private Sub RefreshChart(Optional force As Boolean = False)
        If IsDisposed Then Return
        Dim windowIndex = Math.Max(0, _windowChoice.SelectedIndex)
        If _state <> RunState.Idle AndAlso Not _paused OrElse _plotNowUtc = DateTime.MinValue Then _plotNowUtc = DateTime.UtcNow
        Dim origin = If(_traceStartUtc = DateTime.MinValue, _plotNowUtc, _traceStartUtc)
        Dim endUtc = If(_plotNowUtc > origin.AddSeconds(WindowSeconds(windowIndex)), _plotNowUtc, origin.AddSeconds(WindowSeconds(windowIndex)))
        Dim startUtc = endUtc.AddSeconds(-WindowSeconds(windowIndex))
        If force OrElse _plotDirty Then
            For Each group In _chartGroups
                group.Item1.PollingIntervalSeconds = Math.Max(Math.Max(5, CInt(Math.Ceiling(_intervalMs * 4 / 1000.0R))), WindowSeconds(windowIndex) \ 200)
                group.Item1.Series = group.Item2.Select(Function(item) New AnalyticsTrendSeries With {
                    .Name = GraphLegendName(item), .Color = item.Color,
                    .ExpectedIntervalSeconds = _intervalMs / 1000.0R, .ConnectCommunicationGaps = item.Equipment.IsUps,
                    .Points = Downsample(item.Points.Where(Function(point) point.TimestampUtc >= startUtc.AddMilliseconds(-Math.Max(1000, _intervalMs))).ToList())}).ToArray()
                group.Item1.XAxisLabel = CadenceLabel(group.Item2)
                Dim valid = group.Item1.Series.Where(Function(item) item.Points.Any(Function(point) point.IsValid AndAlso point.Value.HasValue AndAlso Double.IsFinite(point.Value.Value))).Count()
                group.Item1.ChartTitle = CStr(group.Item1.Tag) &
                    If(group.Item2.All(Function(item) UpsInputEstimates.IsEstimateKey(item.Channel.UpsKey)), " | ESTIMATED ", " | measured ") &
                    valid.ToString(Inv) & "/" &
                    group.Item2.Length.ToString(Inv) & If(valid < group.Item2.Length, " (see table quality)", "")
                group.Item1.EmptyDataMessage = If(valid > 0, String.Empty,
                    String.Join(Environment.NewLine & Environment.NewLine, group.Item2.Select(Function(item) If(String.IsNullOrWhiteSpace(item.ReadingQuality),
                            item.Channel.Label & ": Waiting for the first acquisition.",
                            If(item.Last.HasValue, item.Channel.Label & ": No valid measurements in the selected time window.",
                                Regex.Replace(item.ReadingQuality, "\bL[123](?=:)", "phase")))).Distinct()))
            Next
            _plotDirty = False
        End If
        ' Reveal only the line between already acquired samples; animation never creates recorded values.
        For Each group In _chartGroups
            group.Item1.TraceRevealUtc = If(_state <> RunState.Idle AndAlso Not _paused AndAlso _revealTargetUtc <> DateTime.MinValue,
                CType(RevealTime(), DateTime?), Nothing)
            group.Item1.WindowStartUtc = startUtc
            group.Item1.WindowEndUtc = endUtc
        Next
    End Sub

    Private Function CadenceLabel(states As SeriesState()) As String
        Dim gaps As New List(Of Double)()
        For Each state In states
            Dim newer As DateTime? = Nothing
            Dim count = 0
            For index = state.Points.Count - 1 To 0 Step -1
                Dim point = state.Points(index)
                If Not point.IsValid OrElse Not point.Value.HasValue Then Continue For
                If newer.HasValue Then
                    Dim seconds = (newer.Value - point.TimestampUtc).TotalSeconds
                    If seconds > 0 Then gaps.Add(seconds)
                End If
                newer = point.TimestampUtc
                count += 1
                If count >= 100 Then Exit For
            Next
        Next
        gaps.Sort()
        Return "Time (local) | target " & (_intervalMs / 1000.0R).ToString("0.###", Inv) &
            " s | actual " & If(gaps.Count = 0, "pending", gaps(gaps.Count \ 2).ToString("0.###", Inv) & " s") &
            If(gaps.Count = 0, "", " | max gap " & gaps(gaps.Count - 1).ToString("0.###", Inv) & " s") &
            If(states.Any(Function(state) state.Equipment.IsUps), " | dashed = missed reads", "")
    End Function

    Private Shared Function GraphOrder(channel As ChannelItem) As Integer
        If Not channel.IsUps Then Return 0
        Select Case channel.UpsKey
            Case "InputVoltage" : Return If(channel.VoltageMode = "LN", 10, 11)
            Case "InputCurrent" : Return 12
            Case "OutputVoltage" : Return If(channel.VoltageMode = "LN", 20, 21)
            Case "OutputCurrent" : Return 22
            Case "OutputPower", "@OutputKw", "@OutputKva", "@OutputPf", "@OutputLoadMax" : Return 23
        End Select
        If channel.Label.StartsWith("Battery", StringComparison.Ordinal) Then Return 30
        Return 40
    End Function
End Class
