Imports System.Diagnostics
Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms

Public NotInheritable Class EquipmentHistoryView
    Inherits UserControl

    Private ReadOnly _service As EquipmentHistoryService
    Private ReadOnly _category As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _equipment As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .DropDownHeight = 320}
    Private ReadOnly _metric As New CheckedListBox With {.CheckOnClick = True, .IntegralHeight = False, .BorderStyle = BorderStyle.None}
    Private ReadOnly _trendMetric As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private _trendField As Control
    Private ReadOnly _channelSearch As New TextBox With {.PlaceholderText = "Search measurements or alarms"}
    Private ReadOnly _channelKind As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _channelCount As New Label()
    Private ReadOnly _allChannels As New CheckBox With {.Text = "Select visible", .AutoSize = True}
    Private ReadOnly _clearChannels As Button = UiTheme.ActionButton("Clear", False)
    Private ReadOnly _individual As New CheckBox With {.Text = "Individual readings", .AutoSize = True}
    Private ReadOnly _quickRange As New ComboBox()
    Private ReadOnly _periodButton As New Button()
    Private ReadOnly _resolution As New ComboBox()
    Private ReadOnly _from As New DateTimePicker()
    Private ReadOnly _to As New DateTimePicker()
    Private ReadOnly _fromTime As New DateTimePicker()
    Private ReadOnly _toTime As New DateTimePicker()
    Private ReadOnly _load As Button = UiTheme.ActionButton("Load records", True, Icons.Search)
    Private ReadOnly _cancel As Button = UiTheme.ActionButton("Cancel", False)
    Private ReadOnly _message As New Label()
    Private ReadOnly _summary As New Label()
    Private ReadOnly _hideSelection As New CheckBox With {.Text = "Expand workspace", .Appearance = Appearance.Button, .AutoSize = True}
    Private ReadOnly _records As New DataGridView()
    Private ReadOnly _statistics As New DataGridView()
    Private ReadOnly _chart As New AnalyticsTrendChart()
    Private ReadOnly _alarmChart As New AnalyticsTrendChart()
    Private ReadOnly _comparisonYTime As New AnalyticsTrendChart()
    Private ReadOnly _comparisonXTime As New AnalyticsTrendChart()
    Private ReadOnly _comparisonMode As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 250}
    Private ReadOnly _showComparisonTime As New CheckBox With {.Text = "Show both channels over time", .AutoSize = True, .Checked = True}
    Private _trendHost As TableLayoutPanel
    Private _trendPage As TabPage
    Private _syncingComparisonTime As Boolean
    Private ReadOnly _xChannel As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 210}
    Private ReadOnly _yChannel As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 210}
    Private ReadOnly _swapAxes As Button = UiTheme.ActionButton("Swap X / Y", False)
    Private ReadOnly _axisInfo As New Label With {.AutoSize = True, .ForeColor = UiTheme.MutedInk, .MaximumSize = New Size(650, 0)}
    Private _updatingAxes As Boolean
    Private ReadOnly _trendTimer As New System.Windows.Forms.Timer With {.Interval = 30}
    Private ReadOnly _trendClock As New Stopwatch()
    Private _trendStartUtc As DateTime
    Private _trendEndUtc As DateTime
    Private Const TrendRevealMilliseconds As Double = 1200
    Private ReadOnly _analysisLayout As New TableLayoutPanel()
    Private ReadOnly _exports As New List(Of Button)()
    Private ReadOnly _tips As New ToolTip With {.AutoPopDelay = 20000}
    Private ReadOnly _selectedChannels As New HashSet(Of String)(StringComparer.Ordinal)
    Private ReadOnly _channelOptions As New List(Of HistoryChannelSelection)()
    Private _assets As IReadOnlyList(Of HistoryEquipment) = Array.Empty(Of HistoryEquipment)()
    Private _result As EquipmentHistoryResult
    Private _cancellation As CancellationTokenSource
    Private _loading As Boolean
    Private _catalogLoading As Boolean
    Private _catalogRefreshPending As Boolean
    Private _version As Integer
    Private _renderedRows As IReadOnlyList(Of EquipmentHistoryTableRow) = Array.Empty(Of EquipmentHistoryTableRow)()
    Private _updatingChannels As Boolean
    Private _exporting As Boolean

    Private Const CombinedEquipmentId As String = "__ALL_EQUIPMENT__"

    Private NotInheritable Class HistoryChannelSelection
        Public Property Key As String = String.Empty
        Public Property SourceId As String = String.Empty
        Public Property SourceLabel As String = String.Empty
        Public Property EquipmentId As String = String.Empty
        Public Property Metric As HistoryMetric

        Public Overrides Function ToString() As String
            Return Metric.Name & If(Metric.Unit.Length = 0, "", " [" & Metric.Unit & "]") &
                If(SourceLabel.Length = 0, "", " - " & SourceLabel)
        End Function
    End Class

    Public Sub New(service As EquipmentHistoryService)
        _service = service
        Dock = DockStyle.Fill
        BackColor = UiTheme.Canvas
        Font = New Font("Segoe UI", 9.0F)
        BuildLayout()
        AddHandler _xChannel.SelectedIndexChanged, AddressOf AxisChanged
        AddHandler _yChannel.SelectedIndexChanged, AddressOf AxisChanged
        AddHandler _comparisonMode.SelectedIndexChanged, Sub(sender, e)
                                                           If _result IsNot Nothing AndAlso TypeOf _xChannel.SelectedItem Is HistoryMetric Then RenderTrend()
                                                       End Sub
        AddHandler _showComparisonTime.CheckedChanged, Sub(sender, e)
                                                          If _result IsNot Nothing Then RenderTrend()
                                                      End Sub
        AddHandler _comparisonYTime.TimeViewChanged, Sub(sender, e) SynchronizeComparisonTime(_comparisonYTime, _comparisonXTime)
        AddHandler _comparisonXTime.TimeViewChanged, Sub(sender, e) SynchronizeComparisonTime(_comparisonXTime, _comparisonYTime)
        AddHandler _swapAxes.Click, Sub(sender, e)
                                       Dim x = TryCast(_xChannel.SelectedItem, HistoryMetric)
                                       Dim y = TryCast(_yChannel.SelectedItem, HistoryMetric)
                                       If x Is Nothing OrElse y Is Nothing Then Return
                                       _updatingAxes = True
                                       Try
                                           _xChannel.SelectedItem = y
                                           _yChannel.SelectedItem = x
                                       Finally
                                           _updatingAxes = False
                                       End Try
                                       RenderTrend()
                                   End Sub
        AddHandler _trendTimer.Tick, Sub(sender, e) AdvanceTrendReveal(_trendClock.Elapsed.TotalMilliseconds)
        AddHandler _chart.MouseWheel, Sub(sender, e) FinishTrendReveal()
        AddHandler _chart.MouseDown, Sub(sender, e) FinishTrendReveal()
        AddHandler _chart.MouseDoubleClick, Sub(sender, e) FinishTrendReveal()
        AddHandler VisibleChanged, Sub(sender, e)
                                       If Not Visible Then FinishTrendReveal()
                                   End Sub
        AddHandler _category.SelectedIndexChanged, Sub(sender, e)
                                                      If Not _catalogLoading Then LoadEquipment()
                                                  End Sub
        AddHandler _equipment.SelectedIndexChanged, Sub(sender, e)
                                                       If Not _catalogLoading Then LoadChannels()
                                                   End Sub
        AddHandler _metric.ItemCheck, AddressOf ChannelChecked
        AddHandler _channelSearch.TextChanged, Sub(sender, e) FilterChannels()
        AddHandler _channelKind.SelectedIndexChanged, Sub(sender, e) FilterChannels()
        AddHandler _allChannels.CheckedChanged, Sub(sender, e)
                                                   If _updatingChannels Then Return
                                                   Dim selected = _allChannels.Checked
                                                   For index = 0 To _metric.Items.Count - 1
                                                       _metric.SetItemChecked(index, selected)
                                                   Next
                                               End Sub
        AddHandler _clearChannels.Click, Sub(sender, e)
                                            _selectedChannels.Clear()
                                            FilterChannels()
                                            ClearResult()
                                            SetBusy(False)
                                        End Sub
        AddHandler _from.ValueChanged, Sub(sender, e) ClearResult()
        AddHandler _to.ValueChanged, Sub(sender, e) ClearResult()
        AddHandler _fromTime.ValueChanged, Sub(sender, e) ClearResult()
        AddHandler _toTime.ValueChanged, Sub(sender, e) ClearResult()
        AddHandler _individual.CheckedChanged, Sub(sender, e) ClearResult()
        AddHandler _trendMetric.SelectedIndexChanged, Sub(sender, e)
                                                           If _result IsNot Nothing AndAlso _result.Request.IsCombined Then RenderTrend()
                                                       End Sub
        AddHandler _load.Click, Async Sub(sender, e) Await LoadRecordsAsync()
        AddHandler UpsSettingsStore.Changed, AddressOf OnUpsSettingsChanged
        AddHandler HandleCreated, AddressOf OnHandleCreatedForCatalog
        AddHandler _cancel.Click, Sub(sender, e)
                                     If _cancellation IsNot Nothing Then _cancellation.Cancel()
                                 End Sub
        ReloadCatalog()
    End Sub

    Private Sub SetPeriod(fromUtc As DateTime, toUtc As DateTime)
        For Each entry In {(_from, _fromTime, fromUtc), (_to, _toTime, toUtc)}
            Dim value = New DateTime(entry.Item3.Ticks - entry.Item3.Ticks Mod TimeSpan.TicksPerSecond)
            entry.Item1.Value = value
            entry.Item2.Value = value
        Next
    End Sub

    Private Const ThinBudget As Integer = 80000
    Private Const MaxPeriodDays As Integer = 366

    Private Function SelectedMeasurementCount() As Integer
        Dim asset = TryCast(_equipment.SelectedItem, HistoryEquipment)
        If asset Is Nothing Then Return 1
        If asset.IsCombinedSelection Then
            Return Math.Max(1, _assets.Sum(Function(item) item.Metrics.
                Where(Function(metric) Not metric.IsAlarm AndAlso _selectedChannels.Contains(ChannelKey(item, metric, True))).Count()))
        End If
        Return Math.Max(1, asset.Metrics.Where(Function(metric) _selectedChannels.Contains(metric.Id) AndAlso Not metric.IsAlarm).Count())
    End Function

    Private Shared Function ChannelKey(asset As HistoryEquipment, metric As HistoryMetric, combined As Boolean) As String
        Return If(combined, asset.SourceId & "|" & metric.Id & "|" & metric.Unit, metric.Id)
    End Function

    Private Shared Function CombinedMetricId(asset As HistoryEquipment, metric As HistoryMetric) As String
        Return CombinedMetricId(asset, metric.Id)
    End Function

    Private Shared Function CombinedMetricId(asset As HistoryEquipment, metricId As String) As String
        Return asset.Id & "|" & metricId
    End Function

    Private Shared Function ComputeThinSeconds(spanSeconds As Double, channels As Integer) As Integer
        Dim seconds = CInt(Math.Ceiling(spanSeconds * Math.Max(1, channels) / ThinBudget))
        Return If(seconds > 2, seconds, 0)
    End Function

    Private Sub UpdatePeriodText()
        _periodButton.Text = _from.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) & "  ->  " &
            _to.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) & "   (pick days)"
    End Sub

    Private Sub ShowCalendarPopup()
        Dim today = DateTime.UtcNow.Date
        Dim calendar As New MonthCalendar With {.CalendarDimensions = New Size(2, 1), .MaxSelectionCount = MaxPeriodDays,
            .MaxDate = today, .ShowToday = False, .ShowTodayCircle = False, .Margin = Padding.Empty}
        Dim start = If(_from.Value.Date > today, today, _from.Value.Date)
        Dim finish = If(_to.Value.Date > today, today, _to.Value.Date)
        If finish < start Then finish = start
        calendar.SetSelectionRange(start, finish)
        Dim info As New Label With {.AutoSize = False, .Width = calendar.Width, .Height = 58, .Font = New Font("Segoe UI", 9.0F),
            .ForeColor = UiTheme.MutedInk}
        Dim refresh = Sub()
                          Dim days = CInt((calendar.SelectionEnd.Date - calendar.SelectionStart.Date).TotalDays) + 1
                          Dim span = days * 86400.0R
                          Dim thinStep = ComputeThinSeconds(span, SelectedMeasurementCount())
                          info.Text = "From " & calendar.SelectionStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) & " to " &
                              calendar.SelectionEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) & " = " & days & " day(s)  (maximum " & MaxPeriodDays & ")" &
                              Environment.NewLine & If(thinStep > 0, "Large period: one reading every " & thinStep & " s will be loaded.", "All readings will be loaded.") &
                              Environment.NewLine & "Click the first day, then Shift+click the last day (or drag)."
                      End Sub
        AddHandler calendar.DateChanged, Sub(sender, e) refresh()
        refresh()
        Dim ok As New Button With {.Text = "Apply period", .Width = 120, .Height = 30}
        Dim panel As New FlowLayoutPanel With {.FlowDirection = FlowDirection.TopDown, .WrapContents = False, .AutoSize = True,
            .Padding = New Padding(6), .BackColor = Color.White}
        panel.Controls.Add(calendar)
        panel.Controls.Add(info)
        panel.Controls.Add(ok)
        Dim host As New ToolStripControlHost(panel) With {.Margin = Padding.Empty, .Padding = Padding.Empty, .AutoSize = True}
        Dim drop As New ToolStripDropDown With {.AutoClose = True, .Padding = Padding.Empty}
        drop.Items.Add(host)
        AddHandler ok.Click, Sub(sender, e)
                                 _from.Value = calendar.SelectionStart.Date
                                 _to.Value = calendar.SelectionEnd.Date
                                 _fromTime.Value = calendar.SelectionStart.Date
                                 _toTime.Value = calendar.SelectionStart.Date.AddHours(23).AddMinutes(59).AddSeconds(59)
                                 drop.Close()
                             End Sub
        drop.Show(_periodButton, New Point(0, _periodButton.Height))
    End Sub

    Private Shared Function CombineUtc(datePicker As DateTimePicker, timePicker As DateTimePicker) As DateTime
        Return DateTime.SpecifyKind(datePicker.Value.Date + timePicker.Value.TimeOfDay, DateTimeKind.Utc)
    End Function

    Private Sub BuildLayout()
        Dim root As New TableLayoutPanel With {
            .Dock = DockStyle.Fill, .ColumnCount = 1, .RowCount = 3, .Padding = New Padding(4),
            .BackColor = UiTheme.Canvas}
        root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        root.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 56))
        ' One wrapping row: type, equipment, period, quick range, step, options and Load.
        Dim filters As New FlowLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .WrapContents = True,
            .Padding = New Padding(12, 8, 8, 4), .Margin = New Padding(0, 0, 0, 6), .BackColor = UiTheme.Surface}
        _category.Width = 130
        _equipment.Width = 220
        filters.Controls.Add(Field("EQUIPMENT TYPE", _category))
        filters.Controls.Add(Field("EQUIPMENT", _equipment))
        For Each picker In {_from, _to}
            picker.Format = DateTimePickerFormat.Custom
            picker.CustomFormat = "yyyy-MM-dd"
            picker.Width = 104
            picker.Font = Font
        Next
        For Each picker In {_fromTime, _toTime}
            picker.Format = DateTimePickerFormat.Custom
            picker.CustomFormat = "HH:mm:ss"
            picker.ShowUpDown = True
            picker.Width = 78
            picker.Font = Font
        Next
        SetPeriod(DateTime.UtcNow.AddMinutes(-15), DateTime.UtcNow)
        For Each entry In New (String, DateTimePicker, DateTimePicker)() {("FROM  DATE / TIME (UTC)", _from, _fromTime), ("TO  DATE / TIME (UTC)", _to, _toTime)}
            Dim pair As New FlowLayoutPanel With {.AutoSize = True, .WrapContents = False, .Margin = Padding.Empty, .Padding = Padding.Empty}
            entry.Item2.Margin = New Padding(0, 0, 4, 0)
            entry.Item3.Margin = Padding.Empty
            pair.Controls.Add(entry.Item2)
            pair.Controls.Add(entry.Item3)
            filters.Controls.Add(Field(entry.Item1, pair))
        Next

        _quickRange.DropDownStyle = ComboBoxStyle.DropDownList
        _quickRange.Items.AddRange({"Quick range�", "Last 15 minutes", "Last hour", "Last 6 hours", "Last 24 hours", "Last 7 days"})
        _quickRange.SelectedIndex = 0
        _quickRange.Width = 120
        AddHandler _quickRange.SelectedIndexChanged, Sub(sender, e)
                                                         Dim minutes = {0, 15, 60, 360, 1440, 10080}(Math.Max(0, _quickRange.SelectedIndex))
                                                         If minutes = 0 Then Return
                                                         SetPeriod(DateTime.UtcNow.AddMinutes(-minutes), DateTime.UtcNow)
                                                         _quickRange.SelectedIndex = 0
                                                     End Sub
        filters.Controls.Add(Field("QUICK RANGE", _quickRange))
        _resolution.DropDownStyle = ComboBoxStyle.DropDownList
        _resolution.Items.AddRange({"Auto", "All readings", "1 s", "2 s", "5 s", "10 s", "30 s", "1 min", "5 min", "15 min"})
        _resolution.SelectedIndex = 0
        _resolution.Width = 90
        _tips.SetToolTip(_resolution, "Seconds between loaded readings. Auto picks the step that fits the limit; larger steps allow longer periods.")
        filters.Controls.Add(Field("STEP", _resolution))

        Dim periodActions As New FlowLayoutPanel With {.AutoSize = True, .WrapContents = False,
            .Margin = New Padding(0, 0, 0, 4), .Padding = New Padding(0, 18, 0, 0)}
        _individual.Margin = New Padding(2, 6, 10, 0)
        periodActions.Controls.Add(_individual)
        For Each button As Button In {_load, _cancel}
            button.Size = New Size(If(button Is _load, 116, 76), 30)
            button.Margin = New Padding(4, 0, 0, 0)
            periodActions.Controls.Add(button)
        Next
        _cancel.Visible = False
        filters.Controls.Add(periodActions)
        root.Controls.Add(filters, 0, 0)

        Dim workspace As New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 2, .RowCount = 1, .Margin = Padding.Empty}
        workspace.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 250))
        workspace.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        workspace.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        Dim channels As New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 1, .RowCount = 7,
            .BackColor = UiTheme.Surface, .Padding = New Padding(8), .Margin = New Padding(0, 0, 8, 0)}
        channels.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        For index = 0 To 4
            channels.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        Next
        channels.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        channels.RowStyles.Add(New RowStyle(SizeType.Absolute, 20))
        Dim title = UiTheme.Label("CHANNEL / TREND (CHECK TO INCLUDE)", New Font("Segoe UI", 8.0F, FontStyle.Bold), UiTheme.Ink)
        title.Dock = DockStyle.Fill
        channels.Controls.Add(title, 0, 0)
        _channelSearch.Dock = DockStyle.Fill
        _channelSearch.Margin = New Padding(0, 2, 0, 4)
        channels.Controls.Add(_channelSearch, 0, 1)
        _channelKind.Items.AddRange({"All channels", "Measurements", "Alarms / faults", "Interval peaks"})
        _channelKind.SelectedIndex = 0
        _channelKind.Dock = DockStyle.Fill
        _channelKind.Margin = New Padding(0, 2, 0, 4)
        channels.Controls.Add(_channelKind, 0, 2)
        _trendMetric.Dock = DockStyle.Fill
        _trendMetric.Margin = New Padding(0, 2, 0, 4)
        _trendField = Field("TREND CHANNEL (same measurement / unit)", _trendMetric)
        _trendField.Dock = DockStyle.Top
        _trendField.Margin = New Padding(0, 2, 0, 4)
        _trendField.Visible = False
        channels.Controls.Add(_trendField, 0, 3)
        Dim selection As New FlowLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink, .WrapContents = False, .Margin = Padding.Empty,
            .Padding = New Padding(0, 2, 0, 4)}
        _allChannels.Margin = New Padding(0, 7, 8, 0)
        _clearChannels.Size = New Size(60, 28)
        _clearChannels.Margin = Padding.Empty
        selection.Controls.Add(_allChannels)
        selection.Controls.Add(_clearChannels)
        channels.Controls.Add(selection, 0, 4)
        _metric.Dock = DockStyle.Fill
        _metric.HorizontalScrollbar = True
        _metric.Margin = New Padding(0, 6, 0, 2)
        _metric.Font = Font
        channels.Controls.Add(_metric, 0, 5)
        _channelCount.Dock = DockStyle.Fill
        _channelCount.TextAlign = ContentAlignment.MiddleLeft
        _channelCount.ForeColor = UiTheme.MutedInk
        channels.Controls.Add(_channelCount, 0, 6)
        workspace.Controls.Add(channels, 0, 0)

        Dim content As New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 1, .RowCount = 2,
            .BackColor = UiTheme.Surface, .Margin = Padding.Empty, .Padding = New Padding(4)}
        content.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        content.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
        content.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        _summary.Dock = DockStyle.Fill
        _summary.Font = New Font(Font, FontStyle.Bold)
        _summary.TextAlign = ContentAlignment.MiddleLeft
        _summary.AutoEllipsis = True
        Dim viewBar As New Panel With {.Dock = DockStyle.Fill, .Margin = Padding.Empty}
        _hideSelection.Dock = DockStyle.Left
        viewBar.Controls.Add(_summary)
        viewBar.Controls.Add(_hideSelection)
        content.Controls.Add(viewBar, 0, 0)
        Dim tabs As New FlatTabControl With {.Dock = DockStyle.Fill, .Appearance = TabAppearance.FlatButtons,
            .SizeMode = TabSizeMode.Fixed, .ItemSize = New Size(0, 1)}
        Dim records As New TabPage("Records") With {.Padding = New Padding(2), .BackColor = UiTheme.Surface}
        Dim analysis As New TabPage("Graphs") With {.Padding = New Padding(2), .BackColor = UiTheme.Surface}
        Dim statistics As New TabPage("Statistics") With {.Padding = New Padding(2), .BackColor = UiTheme.Surface}
        ConfigureGrid(_records, {"Date (UTC)", "Time (UTC)", "Row"})
        _records.VirtualMode = True
        _records.AllowUserToOrderColumns = False
        AddHandler _records.CellValueNeeded, AddressOf RecordCellValue
        AddHandler _records.CellFormatting, AddressOf FormatRecordCell
        AddHandler _records.CellToolTipTextNeeded,
            Sub(sender, e)
                If e.RowIndex >= 0 AndAlso e.RowIndex < _renderedRows.Count AndAlso e.ColumnIndex >= 0 Then
                    Dim row = _renderedRows(e.RowIndex)
                    e.ToolTipText = row.Cells(e.ColumnIndex)
                    If _records.Columns(e.ColumnIndex).Name = "Time (UTC)" Then
                        e.ToolTipText = row.Cells(0) & " " & row.Cells(1) & " UTC (saved timestamp, shown to 0.1 s)"
                    ElseIf row.Sources.Count = 1 AndAlso row.Sources(0).EventKind.Length > 0 AndAlso
                        (_records.Columns(e.ColumnIndex).Name = "Condition" OrElse _records.Columns(e.ColumnIndex).Name = "Details") Then
                        e.ToolTipText = row.Sources(0).ConditionName & Environment.NewLine &
                            row.Sources(0).EventDetails
                        If _records.Columns(e.ColumnIndex).Name = "Details" Then e.ToolTipText &= Environment.NewLine & row.Sources(0).Evidence
                    End If
                End If
            End Sub
        records.Controls.Add(_records)
        ConfigureGrid(_statistics, {"Equipment", "Channel", "Valid", "Unknown / missing", "Minimum", "Maximum", "Mean", "kWh (calc.)"})
        _analysisLayout.Dock = DockStyle.Fill
        _analysisLayout.ColumnCount = 1
        _analysisLayout.RowCount = 5
        _analysisLayout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        _analysisLayout.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        _analysisLayout.RowStyles.Add(New RowStyle(SizeType.Absolute, 0))
        _analysisLayout.RowStyles.Add(New RowStyle(SizeType.Absolute, 0))
        _analysisLayout.RowStyles.Add(New RowStyle(SizeType.Absolute, 0))
        _analysisLayout.RowStyles.Add(New RowStyle(SizeType.Absolute, 0))
        _chart.Dock = DockStyle.Fill
        _chart.ShowMarkers = True
        _alarmChart.Dock = DockStyle.Fill
        _alarmChart.Visible = False
        _alarmChart.ShowMarkers = True
        _analysisLayout.Controls.Add(_chart, 0, 0)
        _analysisLayout.Controls.Add(_alarmChart, 0, 1)
        statistics.Controls.Add(_statistics)
        For Each chart In {_comparisonYTime, _comparisonXTime}
            chart.Dock = DockStyle.Fill
            chart.ShowMarkers = False
            chart.Visible = False
        Next
        _analysisLayout.Controls.Add(_comparisonYTime, 0, 2)
        _analysisLayout.Controls.Add(_comparisonXTime, 0, 3)
        _comparisonMode.Items.AddRange({"Mean response + observed range", "Chronological path", "Raw acquisition pairs"})
        _comparisonMode.SelectedIndex = 0
        Dim trendHost As New TableLayoutPanel With {.Dock = DockStyle.Top, .ColumnCount = 1, .RowCount = 2,
            .Margin = Padding.Empty}
        _trendHost = trendHost
        _trendPage = analysis
        trendHost.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        trendHost.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        trendHost.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        Dim axes As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .AutoSize = True, .WrapContents = True,
            .Margin = Padding.Empty, .Padding = New Padding(4)}
        axes.Controls.Add(Field("Y CHANNEL (vertical)", _yChannel))
        axes.Controls.Add(Field("X CHANNEL (horizontal)", _xChannel))
        _swapAxes.Size = New Size(106, 28)
        _swapAxes.Margin = New Padding(6, 19, 6, 4)
        axes.Controls.Add(_swapAxes)
        axes.Controls.Add(Field("COMPARISON VIEW", _comparisonMode))
        _showComparisonTime.Margin = New Padding(6, 24, 6, 4)
        axes.Controls.Add(_showComparisonTime)
        _axisInfo.Margin = New Padding(6, 5, 6, 4)
        axes.SetFlowBreak(_showComparisonTime, True)
        axes.Controls.Add(_axisInfo)
        trendHost.Controls.Add(axes, 0, 0)
        trendHost.Controls.Add(_analysisLayout, 0, 1)
        AddHandler _hideSelection.CheckedChanged, Sub(sender, e)
                                                     channels.Visible = Not _hideSelection.Checked
                                                     workspace.ColumnStyles(0).Width = If(_hideSelection.Checked, 0, 250)
                                                     filters.Visible = Not _hideSelection.Checked
                                                     axes.Visible = Not _hideSelection.Checked
                                                     _hideSelection.Text = If(_hideSelection.Checked, "Show controls", "Expand workspace")
                                                     trendHost.PerformLayout()
                                                     UpdateComparisonLayout()
                                                 End Sub
        analysis.AutoScroll = True
        AddHandler analysis.SizeChanged, Sub(sender, e) UpdateComparisonLayout()
        trendHost.Height = 480
        analysis.Controls.Add(trendHost)
        RefreshAxisChoices()
        tabs.TabPages.AddRange({records, analysis, statistics})
        Dim tabHost As New Panel With {.Dock = DockStyle.Fill}
        tabHost.Controls.Add(tabs)
        tabHost.Controls.Add(New SegmentedTabStrip(tabs))
        content.Controls.Add(tabHost, 0, 1)
        workspace.Controls.Add(content, 1, 0)
        root.Controls.Add(workspace, 0, 1)

        Dim footer As New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 1, .RowCount = 2, .Margin = Padding.Empty}
        footer.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        footer.RowStyles.Add(New RowStyle(SizeType.Absolute, 38))
        footer.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        Dim actions As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .WrapContents = False, .Margin = Padding.Empty}
        For Each format As String In {"CSV", "Excel", "PDF"}
            Dim button = UiTheme.ActionButton("Export " & format, False, Icons.Document)
            button.Size = New Size(120, 34)
            button.Enabled = False
            AddHandler button.Click, Async Sub(sender, e) Await ExportAsync(format)
            _exports.Add(button)
            actions.Controls.Add(button)
        Next
        Dim note = UiTheme.Label("Raw measurements + named Alarm / Fault events", New Font("Segoe UI", 8.0F), UiTheme.MutedInk)
        note.Margin = New Padding(10, 10, 0, 0)
        actions.Controls.Add(note)
        footer.Controls.Add(actions, 0, 0)
        _message.Dock = DockStyle.Fill
        _message.AutoEllipsis = True
        _message.UseMnemonic = False
        _message.ForeColor = UiTheme.MutedInk
        footer.Controls.Add(_message, 0, 1)
        root.Controls.Add(footer, 0, 2)
        _tips.SetToolTip(_individual, "Unchecked: Value/Min/Max per saved interval. Checked: every individual raw acquisition.")
        _tips.SetToolTip(_metric, "Choose measurements and one Alarm / Fault option. Named events use the saved policy, not current thresholds.")
        _tips.SetToolTip(_to, "The end time is exclusive. Measurements are raw acquisitions; selected alarm/fault events are saved observed changes/reminders.")
        Controls.Add(root)
    End Sub

    Public Sub ReloadCatalog()
        If _loading OrElse _exporting OrElse IsDisposed Then
            _catalogRefreshPending = True
            Return
        End If
        _catalogRefreshPending = False
        Dim previous = TryCast(_equipment.SelectedItem, HistoryEquipment)
        Dim priorCategory = Convert.ToString(_category.SelectedItem)
        Dim assets = _service.GetRawEquipment()
        If _assets.Count = assets.Count AndAlso _assets.Zip(assets).All(Function(pair) pair.First.Id = pair.Second.Id AndAlso
            pair.First.Name = pair.Second.Name AndAlso pair.First.IntervalSeconds = pair.Second.IntervalSeconds AndAlso
            pair.First.Metrics.Select(Function(metric) metric.Id).SequenceEqual(pair.Second.Metrics.Select(Function(metric) metric.Id))) Then Return
        _assets = assets
        _catalogLoading = True
        Try
            _category.Items.Clear()
            _category.Items.Add("All equipment")
            _category.Items.AddRange(_assets.Select(Function(asset) asset.Category).Distinct().Cast(Of Object)().ToArray())
            _category.SelectedIndex = Math.Max(0, _category.Items.IndexOf(priorCategory))
        Finally
            _catalogLoading = False
        End Try
        LoadEquipment(If(previous Is Nothing, Nothing, previous.Id))
    End Sub

    Private Shared Function Field(caption As String, input As Control) As Control
        Dim panel As New TableLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .ColumnCount = 1, .RowCount = 2,
            .Margin = New Padding(0, 0, 10, 4), .Padding = New Padding(0, 0, 0, 5)}
        panel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        panel.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        panel.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        Dim label = UiTheme.Label(caption, New Font("Segoe UI", 8.0F, FontStyle.Bold), UiTheme.MutedInk)
        label.Margin = New Padding(0, 0, 0, 4)
        panel.Controls.Add(label, 0, 0)
        input.Dock = DockStyle.Top
        input.Margin = Padding.Empty
        input.Font = New Font("Segoe UI", 9.5F)
        panel.Controls.Add(input, 0, 1)
        Return panel
    End Function

    Private Shared Sub ConfigureGrid(grid As DataGridView, columns As String())
        grid.Dock = DockStyle.Fill
        grid.ReadOnly = True
        grid.AllowUserToAddRows = False
        grid.AllowUserToDeleteRows = False
        grid.RowHeadersVisible = False
        grid.BackgroundColor = UiTheme.Surface
        grid.BorderStyle = BorderStyle.None
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal
        grid.GridColor = UiTheme.Border
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None
        grid.EnableHeadersVisualStyles = False
        grid.ColumnHeadersHeight = If(columns.Any(Function(caption) caption.StartsWith("Interval peak - ", StringComparison.Ordinal)), 60, 44)
        grid.ColumnHeadersDefaultCellStyle.BackColor = UiTheme.SurfaceMuted
        grid.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.Ink
        grid.ColumnHeadersDefaultCellStyle.Font = New Font("Segoe UI", 8.5F, FontStyle.Bold)
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = UiTheme.SurfaceMuted
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = UiTheme.Ink
        grid.ColumnHeadersDefaultCellStyle.Padding = New Padding(6, 2, 6, 2)
        grid.DefaultCellStyle.ForeColor = UiTheme.Ink
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(224, 238, 252)
        grid.DefaultCellStyle.SelectionForeColor = UiTheme.Ink
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 253)
        grid.RowTemplate.Height = 30
        grid.DefaultCellStyle.Padding = New Padding(6, 2, 6, 2)
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect
        grid.MultiSelect = False
        grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText
        UiTheme.StyleReadOnlyGrid(grid)
        For Each caption As String In columns
            Dim width As Integer
            Select Case caption
                Case "Date (UTC)" : width = 104
                Case "Time (UTC)" : width = 100
                Case "Equipment" : width = 190
                Case "Measurement" : width = 220
                Case "Unit" : width = 78
                Case "Value" : width = 90
                Case "Acquisition UTC" : width = 210
                Case "Details" : width = 320
                Case "Row" : width = 82
                Case "Condition" : width = 220
                Case "State", "Severity" : width = 88
                Case "Event" : width = 164
                Case "Quality" : width = 136
                Case "Interval ms", "Acquisitions" : width = 96
                Case "Acknowledgement" : width = 150
                Case Else : width = 145
            End Select
            grid.Columns.Add(New DataGridViewTextBoxColumn With {.HeaderText = caption, .Name = caption,
                .Width = width,
                .MinimumWidth = 60, .SortMode = DataGridViewColumnSortMode.NotSortable})
        Next
        If columns.Contains("Condition") Then
            Dim condition = grid.Columns("Condition")
            condition.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
            condition.MinimumWidth = 180
            condition.FillWeight = 100
        End If
    End Sub

    Private Sub LoadEquipment(Optional priorId As String = Nothing)
        Dim category = Convert.ToString(_category.SelectedItem)
        _equipment.Items.Clear()
        If category = "All equipment" Then _equipment.Items.Add(New HistoryEquipment With {
            .Id = CombinedEquipmentId, .Name = "All equipment (combined)", .Category = category,
            .SourceId = "combined", .IsCombinedSelection = True, .Description = "Combined raw history; channels remain separated by equipment and unit."})
        _equipment.Items.AddRange(_assets.Where(Function(asset) category = "All equipment" OrElse asset.Category = category).Cast(Of Object)().ToArray())
        If _equipment.Items.Count > 0 Then
            Dim match = _equipment.Items.Cast(Of HistoryEquipment)().FirstOrDefault(Function(asset) asset.Id = priorId)
            _equipment.SelectedItem = If(match, _equipment.Items(0))
        Else
            LoadChannels()
        End If
    End Sub

    Private Sub LoadChannels()
        ClearResult()
        _selectedChannels.Clear()
        Dim asset = TryCast(_equipment.SelectedItem, HistoryEquipment)
        _channelOptions.Clear()
        If asset IsNot Nothing AndAlso asset.IsCombinedSelection Then
            For Each group In _assets.SelectMany(Function(item) item.Metrics.Select(Function(metric) New With {
                .Asset = item, .Metric = metric, .Key = ChannelKey(item, metric, True)})).
                GroupBy(Function(item) item.Key, StringComparer.Ordinal)
                Dim first = group.First()
                _channelOptions.Add(New HistoryChannelSelection With {
                    .Key = first.Key, .SourceId = first.Asset.SourceId, .SourceLabel = first.Asset.Category,
                    .Metric = first.Metric})
            Next
            For Each optionItem In _channelOptions.Where(Function(item) Not item.Metric.IsAlarm)
                _selectedChannels.Add(optionItem.Key)
            Next
            _trendField.Visible = True
            _trendMetric.Items.Clear()
            _trendMetric.Items.AddRange(_assets.SelectMany(Function(item) item.Metrics.Where(Function(metric) Not metric.IsAlarm).
                Select(Function(metric) New HistoryChannelSelection With {.Key = ChannelKey(item, metric, True),
                    .SourceId = item.SourceId, .SourceLabel = item.Id & " - " & item.Name, .EquipmentId = item.Id, .Metric = metric})).
                Cast(Of Object)().ToArray())
            If _trendMetric.Items.Count > 0 Then _trendMetric.SelectedIndex = 0
        ElseIf asset IsNot Nothing Then
            Dim first = asset.Metrics.FirstOrDefault(Function(metric) metric.IsPower)
            If first Is Nothing Then first = asset.Metrics.FirstOrDefault()
            If first IsNot Nothing Then _selectedChannels.Add(first.Id)
            _channelOptions.AddRange(asset.Metrics.Select(Function(metric) New HistoryChannelSelection With {
                .Key = metric.Id, .SourceId = asset.SourceId, .Metric = metric}))
            _trendField.Visible = False
            _trendMetric.Items.Clear()
        Else
            _trendField.Visible = False
            _trendMetric.Items.Clear()
        End If
        _channelSearch.Clear()
        _channelKind.SelectedIndex = 0
        FilterChannels()
        SetBusy(False)
    End Sub

    Private Sub FilterChannels()
        If _updatingChannels Then Return
        _updatingChannels = True
        Try
            _metric.Items.Clear()
            If _equipment.SelectedItem IsNot Nothing Then
                Dim search = _channelSearch.Text.Trim()
                For Each optionItem In _channelOptions.Where(Function(candidate) (search.Length = 0 OrElse
                    candidate.Metric.Name.Contains(search, StringComparison.OrdinalIgnoreCase) OrElse
                    candidate.Metric.Id.Contains(search, StringComparison.OrdinalIgnoreCase) OrElse
                    candidate.SourceLabel.Contains(search, StringComparison.OrdinalIgnoreCase)) AndAlso
                    (_channelKind.SelectedIndex = 0 OrElse
                        (_channelKind.SelectedIndex = 1 AndAlso Not candidate.Metric.IsAlarm) OrElse
                        (_channelKind.SelectedIndex = 2 AndAlso candidate.Metric.IsAlarm) OrElse
                        (_channelKind.SelectedIndex = 3 AndAlso candidate.Metric.IsIntervalPeak)))
                    _metric.Items.Add(optionItem, _selectedChannels.Contains(optionItem.Key))
                Next
            End If
            _allChannels.Checked = _metric.Items.Count > 0 AndAlso _metric.Items.Cast(Of HistoryChannelSelection)().
                All(Function(item) _selectedChannels.Contains(item.Key))
            UpdateChannelCount()
        Finally
            _updatingChannels = False
        End Try
    End Sub

    Private Sub ChannelChecked(sender As Object, e As ItemCheckEventArgs)
        If _updatingChannels Then Return
        Dim optionItem = DirectCast(_metric.Items(e.Index), HistoryChannelSelection)
        If e.NewValue = CheckState.Checked Then
            _selectedChannels.Add(optionItem.Key)
        Else
            _selectedChannels.Remove(optionItem.Key)
        End If
        _updatingChannels = True
        Try
            _allChannels.Checked = _metric.Items.Count > 0 AndAlso _metric.Items.Cast(Of HistoryChannelSelection)().
                All(Function(item) _selectedChannels.Contains(item.Key))
        Finally
            _updatingChannels = False
        End Try
        UpdateChannelCount()
        ClearResult()
        SetBusy(False)
    End Sub

    Private Sub UpdateChannelCount()
        _channelCount.Text = _selectedChannels.Count.ToString(CultureInfo.InvariantCulture) & " selected  /  " &
            _metric.Items.Count.ToString(CultureInfo.InvariantCulture) & " shown"
    End Sub

    Private Sub ClearResult()
        FinishTrendReveal()
        _version += 1
        _result = Nothing
        RefreshAxisChoices()
        _renderedRows = Array.Empty(Of EquipmentHistoryTableRow)()
        _records.RowCount = 0
        _statistics.Rows.Clear()
        _chart.Series = Array.Empty(Of AnalyticsTrendSeries)()
        _chart.NumericXAxis = False
        UpdateComparisonLayout()
        _alarmChart.Series = Array.Empty(Of AnalyticsTrendSeries)()
        _summary.Text = "Select channels, then load raw records."
        _message.Text = "Each row is one saved reading. Alarm / Fault: named alarms recorded when raised and when cleared; an empty list means none occurred in this period."
        For Each button As Button In _exports
            button.Enabled = False
        Next
    End Sub

    Public Async Function LoadRecordsAsync() As Task
        If _loading OrElse _exporting OrElse IsDisposed Then Return
        Dim asset = TryCast(_equipment.SelectedItem, HistoryEquipment)
        If asset Is Nothing Then Return
        Dim selectedOptions = _channelOptions.Where(Function(optionItem) _selectedChannels.Contains(optionItem.Key)).ToArray()
        If selectedOptions.Length = 0 Then
            _message.Text = "Select at least one measurement or alarm channel."
            _message.ForeColor = UiTheme.Amber
            Return
        End If
        Dim combined = asset.IsCombinedSelection
        Dim metrics = If(combined,
            Array.Empty(Of HistoryMetric)(),
            asset.Metrics.Where(Function(metric) selectedOptions.Any(Function(optionItem) optionItem.Metric.Id = metric.Id)).ToArray())
        If Not combined AndAlso metrics.Length = 0 Then
            _message.Text = "Select at least one measurement or alarm channel."
            _message.ForeColor = UiTheme.Amber
            Return
        End If
        Dim request As New EquipmentHistoryRequest With {.Equipment = asset, .Metrics = metrics, .RawOnly = True,
            .FromUtc = CombineUtc(_from, _fromTime), .ToUtc = CombineUtc(_to, _toTime),
            .IndividualAcquisitions = _individual.Checked}
        If request.ToUtc > DateTime.UtcNow Then request.ToUtc = DateTime.UtcNow
        If request.ToUtc - request.FromUtc > TimeSpan.FromDays(MaxPeriodDays) Then
            _message.Text = "Maximum period is " & MaxPeriodDays & " days."
            _message.ForeColor = UiTheme.Amber
            Return
        End If
        If combined OrElse Not asset.Metrics.Any(Function(metric) metrics.Contains(metric) AndAlso metric.IsIntervalPeak) Then
            Dim chosen = {-1, 0, 1, 2, 5, 10, 30, 60, 300, 900}(Math.Max(0, _resolution.SelectedIndex))
            request.ThinSeconds = If(chosen < 0, ComputeThinSeconds((request.ToUtc - request.FromUtc).TotalSeconds, SelectedMeasurementCount()), chosen)
        End If
        ClearResult()
        Dim version = _version
        Dim cancellation As New CancellationTokenSource()
        _cancellation = cancellation
        SetBusy(True)
        _message.Text = "Reading saved raw acquisitions..."
        Try
            Dim result = Await Task.Run(Function()
                                            If combined Then Return ReadCombinedHistory(request, selectedOptions, cancellation.Token)
                                            Return _service.Read(request, cancellation.Token)
                                        End Function, cancellation.Token)
            If IsDisposed OrElse version <> _version Then Return
            _result = result
            Dim table = EquipmentHistoryTable.Create(result)
            _records.Columns.Clear()
            ConfigureGrid(_records, table.Headers)
            _renderedRows = table.Rows
            _records.RowCount = table.Rows.Count
            _records.Columns("Time (UTC)").Frozen = True
            _records.ClearSelection()
            _statistics.Columns.Clear()
            ConfigureGrid(_statistics, {"Equipment", "Channel", "Valid", "Unknown / missing", "Minimum", "Maximum", "Mean", "kWh (calc.)"})
            For Each item In result.Statistics
                _statistics.Rows.Add(If(item.EquipmentName.Length > 0, item.EquipmentName, result.Request.Equipment.Name),
                    If(item.Metric.SourceMetricName.Length > 0, item.Metric.SourceMetricName, item.Metric.Name),
                    item.ValidCount, item.MissingCount,
                    EquipmentHistoryTable.FormatMeasurement(item.Minimum, item.Metric.IsState),
                    EquipmentHistoryTable.FormatMeasurement(item.Maximum, item.Metric.IsState),
                    EquipmentHistoryTable.FormatMeasurement(item.Mean), EquipmentHistoryTable.FormatMeasurement(item.EnergyKwh))
            Next
            Dim acquisitions = result.Rows.Select(Function(row) row.EquipmentId & "|" &
                row.TimestampUtc.Ticks.ToString(CultureInfo.InvariantCulture) & "|" & row.ObservationId).Distinct().Count()
            Dim measurements = result.Request.Metrics.Where(Function(metric) Not metric.IsAlarm).ToArray()
            Dim parts As New List(Of String)
            If combined Then parts.Add(result.Request.IncludedEquipment.Count.ToString("N0") & " assets")
            If measurements.Length > 0 Then parts.Add(acquisitions.ToString("N0") & " raw readings / " & measurements.Length &
                If(measurements.Length = 1, " measurement channel", " measurement channels"))
            If result.Request.Metrics.Any(Function(metric) metric.IsAlarm) Then
                Dim alarmText = result.AlarmEvents.Count.ToString("N0") & " alarm/fault entries"
                If result.AlarmEvents.Count = 0 AndAlso Not combined Then
                    Dim last As DateTime? = Nothing
                    Try
                        last = Await Task.Run(Function() EquipmentAlarmHistorySource.LastEventUtc(If(asset.SourceId = "ups", "UPS:", "Transformer:") & asset.SourceKey))
                    Catch ex As Exception When TypeOf ex Is Npgsql.NpgsqlException OrElse TypeOf ex Is InvalidOperationException
                    End Try
                    alarmText &= If(last.HasValue, " (none in this period; last recorded " & last.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) & " UTC)",
                        " (no alarm has ever been recorded for this equipment)")
                End If
                parts.Add(alarmText)
            End If
            parts.Add(table.Rows.Count.ToString("N0") & " table rows")
            If request.ThinSeconds > 0 Then parts.Add("long period: one reading every " & request.ThinSeconds & " s")
            If table.CyclesWithoutReading > 0 Then parts.Add(table.CyclesWithoutReading.ToString("N0") & " cycles with no reading (offline / timeout) not listed")
            _summary.Text = String.Join("  |  ", parts)
            _tips.SetToolTip(_summary, "Selected: " & String.Join(", ", selectedOptions.Select(Function(optionItem) optionItem.ToString())) &
                Environment.NewLine & If(combined,
                    "Combined table is chronological and keeps each equipment/channel separate. The trend uses only the selected compatible channel.",
                    "Only checked channels are loaded. Alarm/fault entries include unknown states, reminders and tracking notes; they are not incident counts."))
            _individual.Enabled = measurements.Length > 0
            _message.Text = If(table.Rows.Count = 0,
                If(combined, "No selected records for these assets/channels in this period.", "No selected records in this period. No alarm events does not prove normal; generators remain unconnected."),
                If(measurements.Length = 0, "Alarm / Fault only. Check measurement channels to add voltage, current or power. ",
                    If(measurements.Any(Function(metric) metric.IsIntervalPeak), "Interval peak = observed maximum; shown on Value row. ", "Measurements: 1 decimal. ")) &
                    "Unknown is not cleared; entries are observations, not incident counts.")
            _message.ForeColor = If(table.Rows.Count = 0, UiTheme.Amber, UiTheme.Green)
            RenderTrend()
        Catch ex As OperationCanceledException
            If Not IsDisposed Then _message.Text = "Read cancelled."
        Catch ex As InvalidDataException
            If Not IsDisposed Then
                _message.ForeColor = UiTheme.Amber
                _message.Text = If(ex.Message.StartsWith("More than 100,000", StringComparison.Ordinal),
                    "Too many records for this period (over 100,000). Choose a larger STEP (seconds), a shorter period or fewer channels.", ex.Message)
                _summary.Text = "Nothing loaded."
            End If
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is Npgsql.NpgsqlException OrElse
            TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is TimeoutException OrElse
            TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is System.Text.Json.JsonException
            If Not IsDisposed Then
                _message.ForeColor = UiTheme.Red
                _message.Text = "Raw history unavailable: " & ex.Message
            End If
        Finally
            _cancellation = Nothing
            cancellation.Dispose()
            If Not IsDisposed Then SetBusy(False)
            If _catalogRefreshPending AndAlso Not IsDisposed Then ReloadCatalog()
        End Try
    End Function

    Private Function ReadCombinedHistory(request As EquipmentHistoryRequest,
                                        selectedOptions As IReadOnlyList(Of HistoryChannelSelection),
                                        token As CancellationToken) As EquipmentHistoryResult
        Dim included As New List(Of HistoryEquipment)()
        Dim metrics As New List(Of HistoryMetric)()
        Dim rows As New List(Of EquipmentHistoryRow)()
        Dim alarmEvents As New List(Of EquipmentHistoryRow)()
        Dim generatedUtc = DateTime.MinValue
        For Each equipment In _assets
            token.ThrowIfCancellationRequested()
            Dim selected = equipment.Metrics.Where(Function(metric) selectedOptions.Any(
                Function(optionItem) optionItem.SourceId = equipment.SourceId AndAlso
                    optionItem.Metric.Id = metric.Id AndAlso optionItem.Metric.Unit = metric.Unit)).ToArray()
            If selected.Length = 0 Then Continue For
            Dim equipmentRequest As New EquipmentHistoryRequest With {
                .Equipment = equipment, .Metrics = selected, .RawOnly = True,
                .FromUtc = request.FromUtc, .ToUtc = request.ToUtc,
                .IndividualAcquisitions = request.IndividualAcquisitions, .ThinSeconds = request.ThinSeconds}
            Dim equipmentResult = _service.Read(equipmentRequest, token)
            included.Add(equipment)
            generatedUtc = If(equipmentResult.GeneratedUtc > generatedUtc, equipmentResult.GeneratedUtc, generatedUtc)
            For Each metric In equipmentResult.Request.Metrics
                metrics.Add(CloneCombinedMetric(metric, equipment))
            Next
            For Each row In equipmentResult.Rows
                row.MetricId = CombinedMetricId(equipment, row.MetricId)
                row.EquipmentId = equipment.Id
                rows.Add(row)
            Next
            For Each alarm In equipmentResult.AlarmEvents
                alarm.EquipmentId = equipment.Id
                alarmEvents.Add(alarm)
            Next
            EquipmentHistoryService.CheckLimit(rows.Count + alarmEvents.Count)
        Next
        If included.Count = 0 Then Throw New InvalidDataException("No selected channels match the equipment in this view.")
        Dim combinedEquipment As New HistoryEquipment With {
            .Id = CombinedEquipmentId, .Name = "All equipment", .Category = "Combined history", .SourceId = "combined",
            .Description = "Chronological raw measurements from the selected assets. Each asset and channel remains separate."}
        Dim combinedRequest As New EquipmentHistoryRequest With {
            .Equipment = combinedEquipment, .Metrics = metrics, .FromUtc = request.FromUtc, .ToUtc = request.ToUtc,
            .IndividualAcquisitions = request.IndividualAcquisitions, .RawOnly = True, .ThinSeconds = request.ThinSeconds,
            .IsCombined = True, .IncludedEquipment = included}
        combinedEquipment.Metrics = metrics
        Return New EquipmentHistoryResult With {
            .Request = combinedRequest, .Rows = rows, .AlarmEvents = alarmEvents, .GeneratedUtc = generatedUtc}
    End Function

    Private Shared Function CloneCombinedMetric(metric As HistoryMetric, equipment As HistoryEquipment) As HistoryMetric
        Return New HistoryMetric With {
            .Id = CombinedMetricId(equipment, metric.Id), .Name = equipment.Id & " | " & metric.Name,
            .SourceMetricName = metric.Name, .Unit = metric.Unit, .Basis = metric.Basis,
            .EquipmentModel = metric.EquipmentModel, .ModelBasis = metric.ModelBasis, .SourcePhase = metric.SourcePhase,
            .VoltageReference = metric.VoltageReference,
            .AtsBankNumber = metric.AtsBankNumber, .ConfiguredSourceDescription = metric.ConfiguredSourceDescription, .TopologyBasis = metric.TopologyBasis,
            .RatedCapacityKva = metric.RatedCapacityKva, .CapacityBasis = metric.CapacityBasis, .IsPower = metric.IsPower,
            .IsState = metric.IsState, .CanSum = metric.CanSum, .MinimumMappingRevision = metric.MinimumMappingRevision,
            .IsEstimate = metric.IsEstimate,
            .SourceId = equipment.SourceId, .SourceMetricId = metric.Id, .EquipmentId = equipment.Id,
            .IsAlarm = metric.IsAlarm, .PeakSourceMetricId = metric.PeakSourceMetricId}
    End Function

    Private Sub SetBusy(busy As Boolean)
        _loading = busy
        For Each control As Control In {_category, _equipment, _metric, _trendMetric, _channelSearch, _channelKind, _from, _to, _fromTime, _toTime, _allChannels, _clearChannels, _individual, _resolution}
            control.Enabled = Not busy AndAlso Not _exporting
        Next
        SetAxisAvailability()
        If Not busy AndAlso Not _exporting Then
            Dim asset = TryCast(_equipment.SelectedItem, HistoryEquipment)
            Dim peaks = _channelOptions.Any(Function(optionItem) _selectedChannels.Contains(optionItem.Key) AndAlso optionItem.Metric.IsIntervalPeak)
            Dim measurements = _channelOptions.Any(Function(optionItem) _selectedChannels.Contains(optionItem.Key) AndAlso Not optionItem.Metric.IsAlarm)
            If peaks Then _individual.Checked = False
            _individual.Enabled = Not peaks AndAlso asset IsNot Nothing AndAlso measurements
            _tips.SetToolTip(_individual, If(peaks, "Interval peaks require interval records, not individual readings.", "Show every saved acquisition instead of interval Value / Min / Max."))
        End If
        _load.Enabled = Not busy AndAlso Not _exporting AndAlso _equipment.SelectedItem IsNot Nothing
        _cancel.Visible = busy
        _cancel.Enabled = busy
        For Each button As Button In _exports
            button.Enabled = Not busy AndAlso Not _exporting AndAlso _result IsNot Nothing AndAlso _renderedRows.Count > 0
        Next
    End Sub

    Private Sub RenderTrend()
        If _result Is Nothing Then Return
        FinishTrendReveal()
        RefreshAxisChoices()
        _chart.NumericXAxis = False
        UpdateComparisonLayout()
        If Not _result.Request.IsCombined AndAlso TypeOf _xChannel.SelectedItem Is HistoryMetric Then
            RenderChannelComparison()
            Return
        End If
        If _result.Request.IsCombined Then
            RenderCombinedTrend()
            StartTrendReveal()
            Return
        End If
        Dim metrics = _result.Request.Metrics.Where(Function(metric) Not metric.IsAlarm).ToArray()
        Dim includeEvents = _result.Request.Metrics.Any(Function(metric) metric.Id = "alarm:events")
        _statistics.Visible = metrics.Length > 0
        ' A "To" time in the future would leave empty space on the axis; end the plot at the data that exists.
        Dim plotEnd = If(_result.GeneratedUtc < _result.Request.ToUtc AndAlso _result.GeneratedUtc > _result.Request.FromUtc,
            _result.GeneratedUtc, _result.Request.ToUtc)
        _analysisLayout.RowStyles(4).Height = 0
        _chart.ChartTitle = _result.Request.Equipment.Name & " - raw measurements"
        _chart.YAxisLabel = If(metrics.Select(Function(metric) metric.Unit).Distinct().Count() = 1,
            metrics(0).Unit, "Native units (see legend); alarms 0/1")
        _chart.XAxisLabel = "UTC  " & _result.Request.FromUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) & "  to  " &
            plotEnd.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        _chart.TimestampDescription = "Interval peaks are at interval start, or report start for a partial interval. No between-poll peaks or continuous alarm state are invented. Unknowns remain in Records."
        _chart.ResetZoom()
        _alarmChart.ResetZoom()
        _chart.WindowStartUtc = _result.Request.FromUtc
        _chart.WindowEndUtc = plotEnd
        _chart.PollingIntervalSeconds = CInt(Math.Ceiling(_result.Request.Equipment.IntervalSeconds))
        _chart.ChartType = AnalyticsChartType.Line
        Dim colors = {UiTheme.Blue, UiTheme.Green, UiTheme.Amber, UiTheme.Red, Color.Purple, Color.Teal}
        Dim selectedY = TryCast(_yChannel.SelectedItem, HistoryMetric)
        Dim shownMetrics = If(selectedY Is Nothing, metrics, metrics.Where(Function(metric) metric.Id = selectedY.Id).ToArray())
        _chart.YAxisLabel = If(selectedY Is Nothing, _chart.YAxisLabel, selectedY.Unit)
        _chart.Series = shownMetrics.Select(Function(metric, index) New AnalyticsTrendSeries With {
            .Name = metric.Name & " [" & metric.Unit & "]", .Color = colors(index Mod colors.Length), .IsState = metric.IsState,
            .ExpectedIntervalSeconds = Math.Max(_result.Request.Equipment.IntervalSeconds, _result.Request.ThinSeconds),
            .Points = _result.GetObservations(metric).Select(Function(row) New AnalyticsTrendPoint With {
                .TimestampUtc = row.TimestampUtc, .Value = row.Value, .IsValid = row.Quality = "Good" AndAlso row.Value.HasValue}).ToArray()}).ToArray()
        Dim eventSeries = _result.AlarmEvents.GroupBy(Function(row) row.ConditionKey).Select(Function(group, index) New AnalyticsTrendSeries With {
            .Name = group.First().EventCategory & ": " & group.First().ConditionName,
            .Color = colors(index Mod colors.Length),
            .Points = group.Select(Function(row) New AnalyticsTrendPoint With {
                .TimestampUtc = row.TimestampUtc, .Value = If(row.EventState = "Active", 1.0R,
                    If(row.EventState = "Cleared" OrElse row.EventState = "Inactive", 0.0R, CType(Nothing, Double?))),
                .IsValid = row.EventState <> "Unknown"}).ToArray()}).ToArray()
        Dim mixed = includeEvents AndAlso metrics.Length > 0
        _alarmChart.Visible = mixed
        _analysisLayout.RowStyles(1).Height = If(mixed, 190, 0)
        If mixed Then
            _alarmChart.Series = eventSeries
            Dim units = metrics.Where(Function(metric) Not metric.IsAlarm).Select(Function(metric) metric.Unit).Distinct().ToArray()
            _chart.YAxisLabel = If(selectedY IsNot Nothing, selectedY.Unit, If(units.Length = 1, units(0), "Native measurement units (see legend)"))
            _chart.ChartTitle = _result.Request.Equipment.Name & " - raw measurements"
            _alarmChart.ChartTitle = "Alarm / Fault events (markers only)"
            _alarmChart.YAxisLabel = "0 inactive / 1 active"
            _alarmChart.XAxisLabel = "Observed event UTC"
            _alarmChart.TimestampDescription = _chart.TimestampDescription
            _alarmChart.WindowStartUtc = _chart.WindowStartUtc
            _alarmChart.WindowEndUtc = _chart.WindowEndUtc
            _alarmChart.PollingIntervalSeconds = _chart.PollingIntervalSeconds
            _alarmChart.ChartType = AnalyticsChartType.Scatter
        Else
            _alarmChart.Series = Array.Empty(Of AnalyticsTrendSeries)()
            If includeEvents AndAlso metrics.Length = 0 Then
                _chart.Series = eventSeries
                _chart.ChartType = AnalyticsChartType.Scatter
                _chart.ChartTitle = "Alarm / Fault events (markers only)"
                _chart.YAxisLabel = "0 inactive / 1 active"
                _chart.XAxisLabel = "Observed event UTC"
            End If
        End If
        UpdateComparisonLayout()
        StartTrendReveal()
    End Sub

    Private Sub AxisChanged(sender As Object, e As EventArgs)
        If _updatingAxes OrElse _result Is Nothing Then Return
        If TypeOf _xChannel.SelectedItem Is HistoryMetric AndAlso Not TypeOf _yChannel.SelectedItem Is HistoryMetric Then
            _updatingAxes = True
            Try
                Dim x = DirectCast(_xChannel.SelectedItem, HistoryMetric)
                Dim y = _yChannel.Items.OfType(Of HistoryMetric)().FirstOrDefault(Function(metric) metric.Id <> x.Id)
                _yChannel.SelectedItem = If(y, x)
            Finally
                _updatingAxes = False
            End Try
        End If
        RenderTrend()
    End Sub

    Private Sub RefreshAxisChoices()
        Dim x = TryCast(_xChannel.SelectedItem, HistoryMetric)
        Dim y = TryCast(_yChannel.SelectedItem, HistoryMetric)
        _updatingAxes = True
        Try
            _xChannel.Items.Clear()
            _yChannel.Items.Clear()
            _xChannel.Items.Add("Time (UTC)")
            _yChannel.Items.Add("All checked measurements")
            If _result IsNot Nothing AndAlso Not _result.Request.IsCombined Then
                For Each metric In _result.Request.Metrics.Where(Function(item) Not item.IsAlarm)
                    _yChannel.Items.Add(metric)
                    If Not metric.IsIntervalPeak Then _xChannel.Items.Add(metric)
                Next
            End If
            _xChannel.SelectedItem = _xChannel.Items.OfType(Of HistoryMetric)().FirstOrDefault(Function(item) x IsNot Nothing AndAlso item.Id = x.Id)
            If _xChannel.SelectedIndex < 0 Then _xChannel.SelectedIndex = 0
            _yChannel.SelectedItem = _yChannel.Items.OfType(Of HistoryMetric)().FirstOrDefault(Function(item) y IsNot Nothing AndAlso item.Id = y.Id)
            If _yChannel.SelectedIndex < 0 Then _yChannel.SelectedIndex = 0
        Finally
            _updatingAxes = False
        End Try
        SetAxisAvailability()
        _axisInfo.Text = If(_result IsNot Nothing AndAlso _result.Request.IsCombined,
            "Channel vs channel is available for one equipment only; combined history stays on Time.",
            "Default X: Time. Check both channels, load records, then choose X and Y. Comparisons use matching saved acquisitions only.")
    End Sub

    Private Sub SetAxisAvailability()
        Dim available = Not _loading AndAlso Not _exporting AndAlso _result IsNot Nothing AndAlso Not _result.Request.IsCombined
        _xChannel.Enabled = available AndAlso _xChannel.Items.Count > 1
        _yChannel.Enabled = available AndAlso _yChannel.Items.Count > 1
        _swapAxes.Enabled = available AndAlso TypeOf _xChannel.SelectedItem Is HistoryMetric AndAlso
            TypeOf _yChannel.SelectedItem Is HistoryMetric AndAlso Not DirectCast(_yChannel.SelectedItem, HistoryMetric).IsIntervalPeak
        _comparisonMode.Enabled = available AndAlso TypeOf _xChannel.SelectedItem Is HistoryMetric
        _showComparisonTime.Enabled = _comparisonMode.Enabled
    End Sub

    Private Sub RenderChannelComparison()
        Dim x = DirectCast(_xChannel.SelectedItem, HistoryMetric)
        Dim y = TryCast(_yChannel.SelectedItem, HistoryMetric)
        _chart.ResetZoom()
        _chart.NumericXAxis = True
        _chart.ComparisonStyle = If(_comparisonMode.SelectedIndex = 1, ChannelComparisonStyle.TimePath,
            If(_comparisonMode.SelectedIndex = 2, ChannelComparisonStyle.RawPoints, ChannelComparisonStyle.MeanResponse))
        _chart.ChartType = AnalyticsChartType.Scatter
        _chart.ChartTitle = _result.Request.Equipment.Name & " - " & If(y Is Nothing, "Select Y", y.Name) & " vs " & x.Name
        _chart.XAxisLabel = x.ToString()
        _chart.YAxisLabel = If(y Is Nothing, "", y.ToString())
        _chart.WindowStartUtc = _result.Request.FromUtc
        _chart.WindowEndUtc = _result.Request.ToUtc
        _chart.TimestampDescription = "Paired from the same saved acquisition. No interpolation or nearest-time matching."
        _alarmChart.Visible = False
        _analysisLayout.RowStyles(1).Height = 0
        Dim pairs = If(y Is Nothing OrElse y.IsIntervalPeak, Array.Empty(Of AnalyticsTrendPoint)(),
            PairChannels(_result, x, y))
        _chart.Series = {New AnalyticsTrendSeries With {.Name = If(y Is Nothing, "", y.Name) & " vs " & x.Name,
            .Color = UiTheme.Blue, .Points = pairs, .ComparisonBins = ChannelComparisonBin.Build(pairs),
            .ExpectedIntervalSeconds = Math.Max(_result.Request.Equipment.IntervalSeconds, _result.Request.ThinSeconds)}}
        _axisInfo.Text = If(y IsNot Nothing AndAlso y.IsIntervalPeak,
            "Interval peaks cannot be paired as simultaneous measurements. Select a raw Y channel.",
            pairs.Length.ToString("N0", CultureInfo.InvariantCulture) &
            " matched saved acquisitions. " & pairs.Select(Function(point) point.XValue.Value).Distinct().Count().ToString("N0", CultureInfo.InvariantCulture) &
            " distinct X levels. " & If(_chart.ComparisonStyle = ChannelComparisonStyle.MeanResponse,
                "Green = sample-weighted mean by X level (up to 12) or 12 equal-width X bins; bars = observed min-max, not confidence limits. n < 3: unconnected grey mean. ",
                If(_chart.ComparisonStyle = ChannelComparisonStyle.TimePath,
                    "Path follows acquisition time, not X order; blue to orange. Missing pairs and recording gaps break the path. ",
                    "Transparent raw pairs preserve the measured values. ")) &
            "Association is not causation. Temperature may lag load; compare the aligned time traces. No assumed lag correction." &
            If((x.Id & " " & If(y Is Nothing, "", y.Id)).Contains("TDEGC", StringComparison.OrdinalIgnoreCase),
                " TDEGC sensor source is unverified; do not assume winding/hot-spot temperature.", ""))
        If y IsNot Nothing Then
            _syncingComparisonTime = True
            Try
                ConfigureComparisonTime(_comparisonYTime, y, pairs, False)
                ConfigureComparisonTime(_comparisonXTime, x, pairs, True)
            Finally
                _syncingComparisonTime = False
            End Try
        End If
        UpdateComparisonLayout()
    End Sub

    Private Sub UpdateComparisonLayout()
        If _trendHost Is Nothing Then Return
        Dim show = _chart.NumericXAxis AndAlso _showComparisonTime.Checked
        _comparisonYTime.Visible = show
        _comparisonXTime.Visible = show
        _analysisLayout.RowStyles(2).Height = If(show, 280 + _comparisonYTime.Margin.Vertical, 0)
        _analysisLayout.RowStyles(3).Height = If(show, 280 + _comparisonXTime.Margin.Vertical, 0)
        Dim axesHeight = _trendHost.GetRowHeights().FirstOrDefault()
        Dim chartHeight = Math.Max(360, _chart.MinimumReadableHeight) + _chart.Margin.Vertical + _analysisLayout.Margin.Vertical +
            CInt(_analysisLayout.RowStyles(1).Height + _analysisLayout.RowStyles(2).Height + _analysisLayout.RowStyles(3).Height)
        _trendHost.Height = Math.Max(axesHeight + chartHeight, _trendPage.ClientSize.Height - _trendPage.Padding.Vertical)
    End Sub

    Private Sub ConfigureComparisonTime(chart As AnalyticsTrendChart, metric As HistoryMetric,
                                        pairs As IReadOnlyList(Of AnalyticsTrendPoint), useX As Boolean)
        chart.ResetZoom()
        chart.ChartTitle = metric.Name & " over time - paired readings (shared time zoom)"
        chart.YAxisLabel = metric.ToString()
        chart.XAxisLabel = "Time (UTC) - wheel zoom / drag pans both time traces"
        chart.TimestampDescription = "Same matched acquisition pairs as the comparison; independent native Y scale."
        chart.ChartType = AnalyticsChartType.Line
        chart.WindowStartUtc = _result.Request.FromUtc
        chart.WindowEndUtc = _result.Request.ToUtc
        chart.PollingIntervalSeconds = Math.Max(1, CInt(Math.Ceiling(_result.Request.Equipment.IntervalSeconds)))
        chart.Series = {New AnalyticsTrendSeries With {.Name = metric.Name, .Color = If(useX, UiTheme.Amber, UiTheme.Blue),
            .IsState = metric.IsState,
            .ExpectedIntervalSeconds = Math.Max(_result.Request.Equipment.IntervalSeconds, _result.Request.ThinSeconds),
            .Points = pairs.Select(Function(point) New AnalyticsTrendPoint With {
                .TimestampUtc = point.TimestampUtc, .Value = If(useX, point.XValue, point.Value), .IsValid = True,
                .ConnectFromPrevious = point.ConnectFromPrevious}).ToArray()}}
    End Sub

    Private Sub SynchronizeComparisonTime(source As AnalyticsTrendChart, target As AnalyticsTrendChart)
        If _syncingComparisonTime OrElse Not _chart.NumericXAxis Then Return
        _syncingComparisonTime = True
        Try
            target.SetTimeView(source.ViewStartUtc, source.ViewEndUtc)
        Finally
            _syncingComparisonTime = False
        End Try
    End Sub

    Private Shared Function PairChannels(result As EquipmentHistoryResult, x As HistoryMetric, y As HistoryMetric) As AnalyticsTrendPoint()
        Return ChannelComparisonStudy.Create(result, x, y, ChannelComparisonStyle.RawPoints, False).Pairs
    End Function

    Private Sub StartTrendReveal()
        FinishTrendReveal()
        If Not Visible OrElse _chart.ChartType <> AnalyticsChartType.Line Then Return
        Dim first = DateTime.MaxValue
        Dim last = DateTime.MinValue
        For Each series In _chart.Series
            For Each point In series.Points
                If Not point.IsValid OrElse Not point.Value.HasValue OrElse Not Double.IsFinite(point.Value.Value) OrElse
                    point.TimestampUtc < _chart.WindowStartUtc OrElse point.TimestampUtc > _chart.WindowEndUtc Then Continue For
                If point.TimestampUtc < first Then first = point.TimestampUtc
                If point.TimestampUtc > last Then last = point.TimestampUtc
            Next
        Next
        If last <= first Then Return
        _trendStartUtc = first
        _trendEndUtc = last
        _chart.TraceRevealUtc = first
        _trendClock.Restart()
        _trendTimer.Start()
    End Sub

    Private Sub AdvanceTrendReveal(elapsedMilliseconds As Double)
        If Not _trendTimer.Enabled Then Return
        If Not Visible OrElse elapsedMilliseconds >= TrendRevealMilliseconds Then
            FinishTrendReveal()
            Return
        End If
        Dim fraction = Math.Clamp(elapsedMilliseconds / TrendRevealMilliseconds, 0.0R, 1.0R)
        _chart.TraceRevealUtc = _trendStartUtc.AddTicks(CLng((_trendEndUtc - _trendStartUtc).Ticks * fraction))
    End Sub

    Private Sub FinishTrendReveal()
        _trendTimer.Stop()
        _trendClock.Stop()
        _chart.TraceRevealUtc = Nothing
    End Sub

    Private Sub RenderCombinedTrend()
        Dim selected = TryCast(_trendMetric.SelectedItem, HistoryChannelSelection)
        Dim metrics = _result.Request.Metrics.Where(Function(metric) Not metric.IsAlarm).ToArray()
        Dim includeEvents = _result.Request.Metrics.Any(Function(metric) metric.IsAlarm)
        _statistics.Visible = metrics.Length > 0
        Dim plotEnd = If(_result.GeneratedUtc < _result.Request.ToUtc AndAlso _result.GeneratedUtc > _result.Request.FromUtc,
            _result.GeneratedUtc, _result.Request.ToUtc)
        _analysisLayout.RowStyles(4).Height = 0
        _chart.ResetZoom()
        _alarmChart.ResetZoom()
        _chart.WindowStartUtc = _result.Request.FromUtc
        _chart.WindowEndUtc = plotEnd
        _chart.XAxisLabel = "UTC  " & _result.Request.FromUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) & "  to  " &
            plotEnd.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        _chart.TimestampDescription = "Only the selected equipment is charted. Cross-equipment comparisons require verified electrical topology and are not inferred from units or labels."
        Dim colors = {UiTheme.Blue, UiTheme.Green, UiTheme.Amber, UiTheme.Red, Color.Purple, Color.Teal}
        Dim compatible = If(selected Is Nothing, Array.Empty(Of HistoryMetric)(),
            metrics.Where(Function(metric) metric.EquipmentId = selected.EquipmentId AndAlso metric.SourceId = selected.SourceId AndAlso
                metric.SourceMetricId = selected.Metric.Id AndAlso metric.Unit = selected.Metric.Unit).ToArray())
        _chart.Series = compatible.Select(Function(metric, index)
            Dim equipment = _result.Request.IncludedEquipment.Single(Function(item) item.Id = metric.EquipmentId)
            Return New AnalyticsTrendSeries With {
                .Name = equipment.Name, .Color = colors(index Mod colors.Length), .IsState = metric.IsState,
                .ExpectedIntervalSeconds = Math.Max(equipment.IntervalSeconds, _result.Request.ThinSeconds),
                .Points = _result.Rows.Where(Function(row) row.MetricId = metric.Id).Select(Function(row) New AnalyticsTrendPoint With {
                    .TimestampUtc = row.TimestampUtc, .Value = row.Value, .IsValid = row.Quality = "Good" AndAlso row.Value.HasValue}).ToArray()}
        End Function).ToArray()
        _chart.ChartType = AnalyticsChartType.Line
        _chart.ChartTitle = If(selected Is Nothing, "Choose one equipment channel", selected.EquipmentId & " | " & selected.Metric.Name)
        _chart.YAxisLabel = If(selected Is Nothing, "", selected.Metric.Unit)
        _chart.PollingIntervalSeconds = CInt(Math.Ceiling(_result.Request.IncludedEquipment.
            Where(Function(asset) selected IsNot Nothing AndAlso asset.Id = selected.EquipmentId).
            Select(Function(asset) asset.IntervalSeconds).DefaultIfEmpty(1.0R).Min()))

        Dim alarmSeries = If(includeEvents, _result.AlarmEvents.Where(Function(row) selected IsNot Nothing AndAlso row.EquipmentId = selected.EquipmentId).GroupBy(Function(row) New With {
            Key .EquipmentId = row.EquipmentId, Key .ConditionKey = row.ConditionKey}).
            Select(Function(group, index)
                       Dim equipment = _result.Request.IncludedEquipment.Single(Function(item) item.Id = group.Key.EquipmentId)
                       Return New AnalyticsTrendSeries With {
                           .Name = equipment.Name & " - " & group.First().ConditionName,
                           .Color = colors(index Mod colors.Length),
                           .Points = group.Select(Function(row) New AnalyticsTrendPoint With {
                               .TimestampUtc = row.TimestampUtc,
                               .Value = If(row.EventState = "Active", 1.0R,
                                   If(row.EventState = "Cleared" OrElse row.EventState = "Inactive", 0.0R, CType(Nothing, Double?))),
                               .IsValid = row.EventState <> "Unknown"}).ToArray()}
                   End Function).ToArray(), Array.Empty(Of AnalyticsTrendSeries)())
        _alarmChart.Visible = includeEvents AndAlso compatible.Length > 0
        _analysisLayout.RowStyles(1).Height = If(_alarmChart.Visible, 190, 0)
        _alarmChart.Series = alarmSeries
        If _alarmChart.Visible Then
            _alarmChart.ChartTitle = "Alarm / Fault events (observed markers)"
            _alarmChart.YAxisLabel = "0 inactive / 1 active"
            _alarmChart.XAxisLabel = "Observed event UTC"
            _alarmChart.TimestampDescription = _chart.TimestampDescription
            _alarmChart.WindowStartUtc = _chart.WindowStartUtc
            _alarmChart.WindowEndUtc = _chart.WindowEndUtc
            _alarmChart.PollingIntervalSeconds = _chart.PollingIntervalSeconds
            _alarmChart.ChartType = AnalyticsChartType.Scatter
        End If
    End Sub

    Private Sub RecordCellValue(sender As Object, e As DataGridViewCellValueEventArgs)
        If e.RowIndex >= 0 AndAlso e.RowIndex < _renderedRows.Count Then e.Value = _renderedRows(e.RowIndex).Cells(e.ColumnIndex)
    End Sub

    Private Sub FormatRecordCell(sender As Object, e As DataGridViewCellFormattingEventArgs)
        If e.RowIndex < 0 OrElse e.RowIndex >= _renderedRows.Count Then Return
        Dim row = _renderedRows(e.RowIndex)
        Dim source = row.Sources.First()
        Dim isEvent = source.EventKind.Length > 0
        If isEvent Then
            If source.EventState = "Unknown" OrElse source.EventKind = "ConditionReplaced" Then
                e.CellStyle.BackColor = Color.FromArgb(245, 246, 248)
                e.CellStyle.ForeColor = UiTheme.MutedInk
            ElseIf source.EventState = "Active" Then
                e.CellStyle.BackColor = If(source.EventCategory = "Fault", Color.FromArgb(255, 239, 239), Color.FromArgb(255, 248, 229))
            Else
                e.CellStyle.BackColor = Color.FromArgb(237, 248, 241)
            End If
        ElseIf row.Cells(2) = "Min" OrElse row.Cells(2) = "Max" Then
            e.CellStyle.ForeColor = UiTheme.MutedInk
        End If
        Dim caption = _records.Columns(e.ColumnIndex).Name
        If caption = "Interval ms" OrElse caption = "Acquisitions" OrElse caption = "Value" OrElse
            (_result IsNot Nothing AndAlso _result.Request.Metrics.Any(Function(metric) Not metric.IsAlarm AndAlso
                caption = metric.Name & If(metric.Unit.Length = 0, "", " [" & metric.Unit & "]"))) Then
            e.CellStyle.Alignment = DataGridViewContentAlignment.MiddleRight
        End If
    End Sub

    Private Async Function ExportAsync(format As String) As Task
        If _result Is Nothing OrElse _loading OrElse _exporting Then Return
        Dim result = _result
        Dim extension = If(format = "Excel", "xlsx", format.ToLowerInvariant())
        Dim exportId = If(result.Request.IsCombined, "ALL_EQUIPMENT", result.Request.Equipment.Id)
        Using dialog As New SaveFileDialog With {.Filter = format & " report|*." & extension,
            .FileName = "EOEMS_" & String.Concat(exportId.Select(Function(ch) If(Path.GetInvalidFileNameChars().Contains(ch), "_"c, ch))) &
                "_" & DateTime.UtcNow.ToString("yyyyMMdd_HHmmss") & "." & extension, .OverwritePrompt = True}
            If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
            _exporting = True
            SetBusy(False)
            Try
                Dim comparison As ChannelComparisonStudy = Nothing
                If format = "PDF" AndAlso _chart.NumericXAxis Then
                    comparison = New ChannelComparisonStudy With {.X = TryCast(_xChannel.SelectedItem, HistoryMetric),
                        .Y = TryCast(_yChannel.SelectedItem, HistoryMetric), .Style = _chart.ComparisonStyle,
                        .IncludeTimeTraces = _showComparisonTime.Checked}
                End If
                Await Task.Run(Sub() HistorianReportExporter.ExportEquipmentHistory(result, dialog.FileName, format, comparison))
                If Not IsDisposed Then
                    _message.ForeColor = UiTheme.Green
                    _message.Text = "Report created: " & dialog.FileName
                End If
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidOperationException OrElse
                TypeOf ex Is ArgumentException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is System.Xml.XmlException
                If Not IsDisposed Then
                    _message.ForeColor = UiTheme.Red
                    _message.Text = "Export failed: " & ex.Message
                End If
            Finally
                _exporting = False
                If Not IsDisposed Then SetBusy(False)
                If _catalogRefreshPending AndAlso Not IsDisposed Then ReloadCatalog()
            End Try
        End Using
    End Function

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            FinishTrendReveal()
            _trendTimer.Dispose()
            RemoveHandler UpsSettingsStore.Changed, AddressOf OnUpsSettingsChanged
            If _cancellation IsNot Nothing Then _cancellation.Cancel()
            _tips.Dispose()
        End If
        MyBase.Dispose(disposing)
    End Sub

    Private Sub OnUpsSettingsChanged(sender As Object, e As EventArgs)
        If IsDisposed Then Return
        If Not IsHandleCreated Then
            _catalogRefreshPending = True
            Return
        End If
        BeginInvoke(Sub()
                        If Not IsDisposed Then ReloadCatalog()
                    End Sub)
    End Sub

    Private Sub OnHandleCreatedForCatalog(sender As Object, e As EventArgs)
        If _catalogRefreshPending Then ReloadCatalog()
    End Sub
End Class
