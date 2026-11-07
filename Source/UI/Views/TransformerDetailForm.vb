Imports System.Collections.Generic
Imports System.Drawing
Imports System.Globalization
Imports System.Linq
Imports System.Threading.Tasks
Imports System.Windows.Forms

' Detail window for one transformer (T1-T4) or the TOTAL load: live values, recorded alarm history and zoomable historian trends.
Public NotInheritable Class TransformerDetailForm
    Inherits Form

    Public Delegate Function LiveProvider() As (Rows As IReadOnlyList(Of String()), Status As String)

    Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture
    Private ReadOnly _analyzerId As String
    Private ReadOnly _isTotal As Boolean
    Private ReadOnly _maxKw As Double
    Private ReadOnly _historian As MeasurementHistorian
    Private ReadOnly _live As LiveProvider
    Private ReadOnly _presets As List(Of Preset)
    Private ReadOnly _measurements As New DataGridView()
    Private ReadOnly _alarmHistory As New DataGridView()
    Private ReadOnly _alarmNote As Label
    Private ReadOnly _range As New ComboBox()
    Private ReadOnly _metric As New ComboBox()
    Private ReadOnly _chart As New AnalyticsTrendChart()
    Private ReadOnly _summary As Label
    Private ReadOnly _status As Label
    Private ReadOnly _timer As New Timer With {.Interval = 1000}
    Private ReadOnly _ecgMode As New ComboBox()
    Private ReadOnly _ecgSummary As New DataGridView()
    Private ReadOnly _ecgLog As New DataGridView()
    Private ReadOnly _ecgNote As Label = UiTheme.Label("Loading ECG outages…", New Font("Segoe UI", 9.5F, FontStyle.Bold), UiTheme.Ink, False)
    Private _ecgTab As TabPage
    Private _energyTab As TabPage
    Private _energyLoading As Boolean
    Private ReadOnly _energyGrid As New DataGridView()
    Private ReadOnly _energyUnit As New ComboBox()
    Private ReadOnly _energyNote As Label = UiTheme.Label("Loading energy…", New Font("Segoe UI", 9.5F, FontStyle.Bold), UiTheme.Ink, False)
    Private _ecgLoading As Boolean
    Private _ecgRecords As IReadOnlyList(Of PowerEventRecord) = Array.Empty(Of PowerEventRecord)()
    Private _ecgPeriods As New Dictionary(Of String, IReadOnlyList(Of EcgOutageStore.PeriodRow))()
    Private Shared ReadOnly EcgKinds As String() = {"day", "week", "month", "year"}
    Private _trendLoading As Boolean
    Private _trendDirty As Boolean
    Private _alarmLoading As Boolean

    Private NotInheritable Class Preset
        Public Name As String
        Public Unit As String
        Public Ids As String()
        Public Labels As String()
        Public Format As String = "0.0"
        Public IsPower As Boolean
    End Class

    Public Sub New(title As String, analyzerId As String, isTotal As Boolean, maxKw As Double,
                   historian As MeasurementHistorian, live As LiveProvider)
        _analyzerId = analyzerId
        _isTotal = isTotal
        _maxKw = maxKw
        _historian = historian
        _live = live
        _presets = BuildPresets(isTotal)
        Text = title & "  •  detail"
        StartPosition = FormStartPosition.CenterParent
        Size = New Size(1120, 780)
        MinimumSize = New Size(820, 560)
        BackColor = UiTheme.Canvas
        Font = New Font("Segoe UI", 9.0F)
        ShowInTaskbar = False
        DoubleBuffered = True

        _status = UiTheme.Label(title, New Font("Segoe UI", 9.5F, FontStyle.Bold), UiTheme.Ink, False)
        _status.Dock = DockStyle.Fill
        _status.Padding = New Padding(12, 6, 12, 0)
        _range.DropDownStyle = ComboBoxStyle.DropDownList
        _range.Items.AddRange({"Last hour", "Last 6 hours", "Last 24 hours", "Last 7 days", "Last 30 days"})
        _range.SelectedIndex = 2
        _range.Dock = DockStyle.Fill
        _range.Visible = False
        Dim rangeHost As New Panel With {.Dock = DockStyle.Right, .Width = 170, .Padding = New Padding(6, 4, 12, 0)}
        rangeHost.Controls.Add(_range)
        _metric.DropDownStyle = ComboBoxStyle.DropDownList
        For Each preset In _presets
            _metric.Items.Add(preset.Name)
        Next
        _metric.SelectedIndex = 0
        _metric.Dock = DockStyle.Fill
        _metric.Visible = False
        Dim metricHost As New Panel With {.Dock = DockStyle.Right, .Width = 290, .Padding = New Padding(6, 4, 0, 0)}
        metricHost.Controls.Add(_metric)
        Dim topBar As New Panel With {.Dock = DockStyle.Top, .Height = 32}
        topBar.Controls.Add(_status)
        topBar.Controls.Add(rangeHost)
        topBar.Controls.Add(metricHost)

        Dim tabs As New FlatTabControl With {.Dock = DockStyle.Fill, .Appearance = TabAppearance.FlatButtons, .SizeMode = TabSizeMode.Fixed, .ItemSize = New Size(0, 1)}
        ConfigureGrid(_measurements, {"Parameter", "Value", "Unit"})
        _measurements.Columns(0).FillWeight = 200
        _measurements.Columns(0).DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft
        Dim liveTab As New TabPage("Live Measurements") With {.BackColor = UiTheme.Canvas, .Padding = New Padding(10)}
        _measurements.Dock = DockStyle.Fill
        liveTab.Controls.Add(_measurements)
        tabs.TabPages.Add(liveTab)

        Dim historyTab As New TabPage("Alarm History") With {.BackColor = UiTheme.Canvas, .Padding = New Padding(10)}
        ConfigureGrid(_alarmHistory, {"Time", "Alarm / condition", "State", "Severity", "Acknowledgement", "Detail"})
        _alarmHistory.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells
        _alarmHistory.DefaultCellStyle.WrapMode = DataGridViewTriState.True
        For Each column As DataGridViewColumn In _alarmHistory.Columns
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft
        Next
        Dim alarmWeights = {120.0F, 190.0F, 80.0F, 80.0F, 110.0F, 260.0F}
        For index = 0 To alarmWeights.Length - 1
            _alarmHistory.Columns(index).FillWeight = alarmWeights(index)
        Next
        _alarmHistory.Dock = DockStyle.Fill
        _alarmNote = UiTheme.Label("Loading alarm history…", New Font("Segoe UI", 9.0F, FontStyle.Bold), UiTheme.MutedInk, False)
        _alarmNote.Dock = DockStyle.Top
        _alarmNote.Height = 28
        historyTab.Controls.Add(_alarmHistory)
        historyTab.Controls.Add(_alarmNote)
        tabs.TabPages.Add(historyTab)

        Dim trendTab As New TabPage("Trend & Energy") With {.BackColor = UiTheme.Canvas, .Padding = New Padding(10)}
        _chart.Dock = DockStyle.Fill
        trendTab.Controls.Add(_chart)
        _summary = UiTheme.Label("Loading historian…", New Font("Segoe UI", 9.5F, FontStyle.Bold), UiTheme.Ink, False)
        _summary.Dock = DockStyle.Top
        _summary.Height = 44
        trendTab.Controls.Add(_summary)
        tabs.TabPages.Add(trendTab)

        tabs.TabPages.Add(BuildEnergyTab())
        If isTotal Then tabs.TabPages.Add(BuildEcgTab())

        AddHandler tabs.SelectedIndexChanged, Sub(sender, e) LoadHistoryFor(tabs.SelectedTab, trendTab, historyTab)
        AddHandler _range.SelectedIndexChanged, Sub(sender, e) LoadHistoryFor(tabs.SelectedTab, trendTab, historyTab)
        AddHandler _metric.SelectedIndexChanged, Sub(sender, e) LoadHistoryFor(tabs.SelectedTab, trendTab, historyTab)

        Controls.Add(tabs)
        Controls.Add(New SegmentedTabStrip(tabs))
        Controls.Add(topBar)
        AddHandler _timer.Tick, Sub(sender, e) RefreshLive()
        AddHandler Shown, Sub(sender, e) RefreshLive()
        _timer.Start()
    End Sub

    Private Shared Function BuildPresets(isTotal As Boolean) As List(Of Preset)
        Dim list As New List(Of Preset) From {
            New Preset With {.Name = "Power: active kW + load %", .IsPower = True, .Ids = {"PTOT_KW"}, .Labels = {"Active kW"}, .Unit = "kW  /  load %"},
            New Preset With {.Name = If(isTotal, "Average voltage L-N (V)", "Voltage L-N (V)"), .Unit = "V", .Ids = {"V_L1_N", "V_L2_N", "V_L3_N"}, .Labels = {"L1-N", "L2-N", "L3-N"}}}
        If Not isTotal Then
            list.Add(New Preset With {.Name = "Voltage L-L (V)", .Unit = "V", .Ids = {"V_L1_L2", "V_L2_L3", "V_L3_L1"}, .Labels = {"L1-L2", "L2-L3", "L3-L1"}})
        End If
        list.Add(New Preset With {.Name = If(isTotal, "Total current per phase (A)", "Current per phase (A)"), .Unit = "A", .Ids = {"CURRENT_L1", "CURRENT_L2", "CURRENT_L3"}, .Labels = {"L1", "L2", "L3"}})
        If Not isTotal Then
            list.Add(New Preset With {.Name = "Neutral current (A)", .Unit = "A", .Ids = {"CURRENT_NEUTRAL"}, .Labels = {"Neutral"}})
            list.Add(New Preset With {.Name = "Frequency (Hz)", .Unit = "Hz", .Ids = {"FREQUENCY"}, .Labels = {"Frequency"}, .Format = "0.00"})
        End If
        list.Add(New Preset With {.Name = "Apparent power (kVA, calc.)", .Unit = "kVA", .Ids = {"KVA_CALC"}, .Labels = {"kVA"}})
        list.Add(New Preset With {.Name = "Power factor (calc.)", .Unit = "PF", .Ids = {"PF_CALC"}, .Labels = {"PF"}, .Format = "0.000"})
        If Not isTotal Then
            list.Add(New Preset With {.Name = "Temperature (TDEGC, source unverified)", .Unit = "°C", .Ids = {"TDEGC_C"}, .Labels = {"Temperature"}})
        End If
        Return list
    End Function

    Friend Shared Sub ConfigureGrid(grid As DataGridView, columns As String())
        grid.ReadOnly = True
        grid.AllowUserToAddRows = False
        grid.AllowUserToDeleteRows = False
        grid.AllowUserToResizeRows = False
        grid.RowHeadersVisible = False
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect
        grid.BackgroundColor = UiTheme.Surface
        grid.BorderStyle = BorderStyle.None
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        grid.EnableHeadersVisualStyles = False
        grid.ColumnHeadersDefaultCellStyle.BackColor = UiTheme.SurfaceMuted
        grid.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.Ink
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = UiTheme.SurfaceMuted
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = UiTheme.Ink
        grid.ColumnHeadersDefaultCellStyle.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        grid.ColumnHeadersHeight = 32
        grid.DefaultCellStyle.Font = New Font("Segoe UI", 10.0F)
        grid.DefaultCellStyle.ForeColor = UiTheme.Ink
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(225, 236, 250)
        grid.DefaultCellStyle.SelectionForeColor = UiTheme.Ink
        grid.AlternatingRowsDefaultCellStyle.BackColor = UiTheme.SurfaceMuted
        grid.RowTemplate.Height = 28
        For Each columnName In columns
            grid.Columns.Add(columnName.Replace(" ", ""), columnName)
        Next
        For index = 1 To grid.Columns.Count - 1
            grid.Columns(index).DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight
        Next
    End Sub

    Private Sub RefreshLive()
        If IsDisposed OrElse _live Is Nothing Then Return
        Dim result = _live.Invoke()
        _status.Text = Text.Replace("  •  detail", "") & "   •   " & result.Status
        If result.Rows Is Nothing Then Return
        If _measurements.Rows.Count <> result.Rows.Count Then
            _measurements.Rows.Clear()
            For Each row In result.Rows
                _measurements.Rows.Add(row(0), row(1), row(2))
            Next
            Return
        End If
        For index = 0 To result.Rows.Count - 1
            Dim cell = _measurements.Rows(index).Cells(1)
            If Not Equals(cell.Value, result.Rows(index)(1)) Then cell.Value = result.Rows(index)(1)
        Next
    End Sub

    Private Function RangeHours() As Integer
        Return {1, 6, 24, 168, 720}(Math.Max(0, _range.SelectedIndex))
    End Function

    ' Raw rows for short ranges; longer ranges are averaged in SQL so the chart stays around 3000 points.
    Private Function BucketSeconds() As Integer
        Dim hours = RangeHours()
        Return If(hours <= 1, 0, CInt(Math.Ceiling(hours * 3600.0R / 3000.0R)))
    End Function

    Private Sub LoadHistoryFor(selected As TabPage, trendTab As TabPage, historyTab As TabPage)
        _range.Visible = selected Is trendTab OrElse selected Is historyTab
        _metric.Visible = selected Is trendTab
        If _ecgTab IsNot Nothing AndAlso selected Is _ecgTab Then LoadEcgOutagesAsync()
        If _energyTab IsNot Nothing AndAlso selected Is _energyTab Then LoadEnergyAsync()
        If selected Is trendTab Then
            If _trendLoading Then _trendDirty = True Else LoadTrendAsync()
        End If
        If selected Is historyTab Then LoadAlarmHistoryAsync()
    End Sub

    Private Async Sub LoadAlarmHistoryAsync()
        If _alarmLoading Then Return
        _alarmLoading = True
        Try
            Dim toUtc = DateTime.UtcNow
            Dim fromUtc = toUtc.AddHours(-RangeHours())
            Dim id = _analyzerId
            Dim events = Await Task.Run(Function() UpsHistorian.ReadAlarmEvents(id, fromUtc, toUtc, 1000, "Transformer"))
            If IsDisposed Then Return
            _alarmHistory.SuspendLayout()
            _alarmHistory.Rows.Clear()
            Dim bold As New Font("Segoe UI", 10.0F, FontStyle.Bold)
            For Each item In events
                Dim index = _alarmHistory.Rows.Add(item.TimestampUtc.ToLocalTime().ToString("dd MMM yyyy HH:mm:ss", Inv), item.Condition, item.State, item.Severity, item.Acknowledged, item.Detail)
                If item.State = "ACTIVE" Then
                    _alarmHistory.Rows(index).Cells(2).Style.ForeColor = UiTheme.Red
                    _alarmHistory.Rows(index).Cells(2).Style.Font = bold
                End If
            Next
            _alarmHistory.ResumeLayout()
            _alarmNote.Text = If(events.Count = 0, "No alarm events recorded in this period.",
                events.Count.ToString(Inv) & " recorded alarm changes (newest first). Saved observations, not exact device event times.")
        Catch ex As Exception When IsExpectedFailure(ex)
            If Not IsDisposed Then _alarmNote.Text = "Alarm history unavailable: " & ex.Message
        Finally
            _alarmLoading = False
        End Try
    End Sub

    Private Function BuildEnergyTab() As TabPage
        _energyTab = New TabPage("Energy kWh") With {.BackColor = UiTheme.Canvas, .Padding = New Padding(10)}
        ConfigureGrid(_energyGrid, If(_isTotal, {"Period", "T1 kWh", "T2 kWh", "T3 kWh", "T4 kWh", "Factory total kWh", "Shift A kWh (06:00-18:00)", "Shift B kWh (18:00-06:00)", "Data coverage"},
                                      {"Period", "Shift A kWh (06:00-18:00)", "Shift B kWh (18:00-06:00)", "Total kWh", "Data coverage"}))
        _energyGrid.Columns(0).DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft
        _energyGrid.Columns(0).FillWeight = 130
        _energyGrid.Dock = DockStyle.Fill
        _energyUnit.DropDownStyle = ComboBoxStyle.DropDownList
        _energyUnit.Items.AddRange({"Per day", "Per week", "Per month", "Per year"})
        _energyUnit.SelectedIndex = 0
        _energyUnit.Dock = DockStyle.Right
        _energyUnit.Width = 150
        AddHandler _energyUnit.SelectedIndexChanged, Sub(sender, e) LoadEnergyAsync()
        _energyNote.Dock = DockStyle.Fill
        Dim header As New Panel With {.Dock = DockStyle.Top, .Height = 34, .Padding = New Padding(0, 4, 0, 4)}
        header.Controls.Add(_energyNote)
        header.Controls.Add(_energyUnit)
        _energyTab.Controls.Add(_energyGrid)
        _energyTab.Controls.Add(header)
        Return _energyTab
    End Function

    ' Energy is integrated from the recorded kW and stored per shift; this page only sums the saved shift rows.
    Private Async Sub LoadEnergyAsync()
        If _energyLoading Then Return
        _energyLoading = True
        Try
            Dim unit = EnergyAccounting.Units(Math.Max(0, _energyUnit.SelectedIndex))
            Dim cells = Await Task.Run(Function()
                                           Try
                                               EnergyAccounting.Recompute(1)
                                           Catch ex As Exception When IsExpectedFailure(ex)
                                           End Try
                                           Return EnergyAccounting.ReadCells(unit, {"Transformer", "Total"}, 400)
                                       End Function)
            If IsDisposed Then Return
            _energyGrid.SuspendLayout()
            _energyGrid.Rows.Clear()
            For Each period In cells.GroupBy(Function(cell) cell.Period).OrderByDescending(Function(group) group.Key)
                Dim row As New List(Of String) From {EnergyAccounting.PeriodLabel(unit, period.Key)}
                If Not _isTotal Then
                    Dim own = period.Where(Function(cell) cell.EquipmentType = "Transformer" AndAlso cell.EquipmentId = _analyzerId).ToArray()
                    row.Add(KwhText(own.Where(Function(cell) cell.Shift = "A")))
                    row.Add(KwhText(own.Where(Function(cell) cell.Shift = "B")))
                    row.Add(KwhText(own))
                    row.Add(EnergyAccounting.CoverageText(own))
                    _energyGrid.Rows.Add(row.ToArray())
                    Continue For
                End If
                For Each id In {"T1", "T2", "T3", "T4"}
                    row.Add(KwhText(period.Where(Function(cell) cell.EquipmentType = "Transformer" AndAlso cell.EquipmentId = id)))
                Next
                Dim total = period.Where(Function(cell) cell.EquipmentType = "Total").ToArray()
                row.Add(KwhText(total))
                row.Add(KwhText(total.Where(Function(cell) cell.Shift = "A")))
                row.Add(KwhText(total.Where(Function(cell) cell.Shift = "B")))
                row.Add(EnergyAccounting.CoverageText(total))
                _energyGrid.Rows.Add(row.ToArray())
            Next
            _energyGrid.ResumeLayout()
            _energyNote.Text = If(_isTotal, "Energy per transformer and factory total", "Energy of " & _analyzerId) &
                ", saved per shift (A 06:00-18:00, B 18:00-06:00) in PostgreSQL. The current shift updates every 2 minutes."
        Catch ex As Exception When IsExpectedFailure(ex)
            If Not IsDisposed Then _energyNote.Text = "Energy history unavailable: " & ex.Message
        Finally
            _energyLoading = False
        End Try
    End Sub

    Private Shared Function KwhText(cells As IEnumerable(Of EnergyAccounting.EnergyCell)) As String
        Dim list = cells.ToArray()
        Return If(list.Length = 0, "—", list.Sum(Function(cell) cell.Kwh).ToString("#,##0.0", Inv))
    End Function
    Private Function BuildEcgTab() As TabPage
        _ecgTab = New TabPage("ECG Outages") With {.BackColor = UiTheme.Canvas, .Padding = New Padding(10)}
        ConfigureGrid(_ecgSummary, {"Period", "Outages", "Total off time", "Longest outage"})
        _ecgSummary.Columns(0).DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft
        _ecgSummary.Dock = DockStyle.Top
        _ecgSummary.Height = 250
        ConfigureGrid(_ecgLog, {"#", "ECG off (time off)", "ECG back (time back)", "Duration", "Result"})
        _ecgLog.Columns(0).FillWeight = 30
        _ecgLog.Columns(4).FillWeight = 140
        _ecgLog.Dock = DockStyle.Fill
        _ecgMode.DropDownStyle = ComboBoxStyle.DropDownList
        _ecgMode.Items.AddRange({"Per day", "Per week", "Per month", "Per year"})
        _ecgMode.SelectedIndex = 0
        _ecgMode.Dock = DockStyle.Right
        _ecgMode.Width = 150
        AddHandler _ecgMode.SelectedIndexChanged, Sub(sender, e) FillEcgSummary()
        _ecgNote.Dock = DockStyle.Fill
        Dim header As New Panel With {.Dock = DockStyle.Top, .Height = 34, .Padding = New Padding(0, 4, 0, 4)}
        header.Controls.Add(_ecgNote)
        header.Controls.Add(_ecgMode)
        Dim gap As New Panel With {.Dock = DockStyle.Top, .Height = 8}
        _ecgTab.Controls.Add(_ecgLog)
        _ecgTab.Controls.Add(gap)
        _ecgTab.Controls.Add(_ecgSummary)
        _ecgTab.Controls.Add(header)
        Return _ecgTab
    End Function

    Private Async Sub LoadEcgOutagesAsync()
        If _ecgLoading Then Return
        _ecgLoading = True
        Try
            Dim loaded = Await Task.Run(Function()
                                            EcgOutageStore.Sync()
                                            Dim periods As New Dictionary(Of String, IReadOnlyList(Of EcgOutageStore.PeriodRow))()
                                            For Each kind In EcgKinds
                                                periods(kind) = EcgOutageStore.ReadPeriods(kind)
                                            Next
                                            Return (Outages:=EcgOutageStore.ReadOutages(), Periods:=periods)
                                        End Function)
            If IsDisposed Then Return
            _ecgRecords = loaded.Outages
            _ecgPeriods = loaded.Periods
            FillEcgLog()
            FillEcgSummary()
        Catch ex As Exception When IsExpectedFailure(ex) OrElse TypeOf ex Is Microsoft.Data.Sqlite.SqliteException OrElse TypeOf ex Is IO.InvalidDataException
            If Not IsDisposed Then _ecgNote.Text = "ECG outage history unavailable: " & ex.Message
        Finally
            _ecgLoading = False
        End Try
    End Sub
    Private Shared Function EcgEnd(item As PowerEventRecord) As DateTime
        Return If(item.EndUtc, DateTime.UtcNow)
    End Function

    Private Shared Function FormatSpan(span As TimeSpan) As String
        If span < TimeSpan.Zero Then span = TimeSpan.Zero
        Return CInt(Math.Floor(span.TotalHours)).ToString(Inv) & " h " & span.Minutes.ToString("00", Inv) & " m " & span.Seconds.ToString("00", Inv) & " s"
    End Function

    Private Sub FillEcgLog()
        _ecgLog.SuspendLayout()
        _ecgLog.Rows.Clear()
        Dim number = _ecgRecords.Count
        For Each item In _ecgRecords
            Dim ongoing = Not item.EndUtc.HasValue
            Dim back = If(ongoing, "— still off —", item.EndUtc.Value.ToLocalTime().ToString("dd MMM yyyy HH:mm:ss", Inv))
            Dim result = If(ongoing, "ECG OFF now", If(item.EndReason, String.Empty))
            Dim index = _ecgLog.Rows.Add(number.ToString(Inv), item.StartUtc.ToLocalTime().ToString("dd MMM yyyy HH:mm:ss", Inv), back,
                                         FormatSpan(EcgEnd(item) - item.StartUtc), result)
            If ongoing Then _ecgLog.Rows(index).DefaultCellStyle.ForeColor = UiTheme.Red
            number -= 1
        Next
        _ecgLog.ResumeLayout()
    End Sub

    ' Totals come from the permanent ecg_outage_period table, so they stay correct whatever happens to the alarm log.
    Private Sub FillEcgSummary()
        Dim mode = Math.Max(0, _ecgMode.SelectedIndex)
        Dim kind = EcgKinds(mode)
        _ecgSummary.SuspendLayout()
        _ecgSummary.Rows.Clear()
        _ecgSummary.Columns(0).HeaderText = {"Day", "Week", "Month", "Year"}(mode)
        Dim rows As IReadOnlyList(Of EcgOutageStore.PeriodRow) = Nothing
        If _ecgPeriods.TryGetValue(kind, rows) Then
            For Each row In rows
                Dim label = {row.Start.ToString("ddd dd MMM yyyy", Inv), "Week of " & row.Start.ToString("dd MMM yyyy", Inv),
                             row.Start.ToString("MMMM yyyy", Inv), row.Start.ToString("yyyy", Inv)}(mode)
                _ecgSummary.Rows.Add(label, row.Outages.ToString(Inv), FormatSpan(TimeSpan.FromSeconds(row.TotalSeconds)),
                                     If(row.Outages > 0, FormatSpan(TimeSpan.FromSeconds(row.LongestSeconds)), "—"))
            Next
        End If
        _ecgSummary.ResumeLayout()
        Dim now = DateTime.Now
        Dim todayCount = _ecgRecords.AsEnumerable().Count(Function(item) item.StartUtc.ToLocalTime().Date = now.Date)
        Dim monthCount = _ecgRecords.AsEnumerable().Count(Function(item) item.StartUtc.ToLocalTime().Year = now.Year AndAlso item.StartUtc.ToLocalTime().Month = now.Month)
        Dim yearCount = _ecgRecords.AsEnumerable().Count(Function(item) item.StartUtc.ToLocalTime().Year = now.Year)
        _ecgNote.Text = "ECG outages — today " & todayCount.ToString(Inv) & "  •  this month " & monthCount.ToString(Inv) &
            "  •  this year " & yearCount.ToString(Inv) & "  •  total recorded " & _ecgRecords.Count.ToString(Inv)
    End Sub
    Private Shared Function IsExpectedFailure(ex As Exception) As Boolean
        Return TypeOf ex Is Npgsql.NpgsqlException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is TimeoutException OrElse
               TypeOf ex Is IO.IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is Security.Cryptography.CryptographicException
    End Function

    Private Async Sub LoadTrendAsync()
        If _trendLoading Then Return
        If _historian Is Nothing Then
            _summary.Text = "Historian is not running."
            Return
        End If
        _trendLoading = True
        Try
            Dim preset = _presets(Math.Max(0, _metric.SelectedIndex))
            Dim toUtc = DateTime.UtcNow
            Dim fromUtc = toUtc.AddHours(-RangeHours())
            Dim bucket = BucketSeconds()
            Dim id = _analyzerId
            Dim ids = preset.Ids
            Dim samples = Await Task.Run(Function() _historian.ReadSeries(id, ids, fromUtc, toUtc, bucket))
            If IsDisposed Then Return
            Dim colors = {UiTheme.Blue, UpsAtsView.WarnAmber, Color.FromArgb(22, 150, 90)}
            Dim series As New List(Of AnalyticsTrendSeries)()
            Dim parts As New List(Of String)()
            For index = 0 To ids.Length - 1
                Dim column = index
                Dim values = samples.Where(Function(item) item.Values(column).HasValue).Select(Function(item) item.Values(column).Value).ToList()
                series.Add(New AnalyticsTrendSeries With {.Name = preset.Labels(index), .Color = colors(index Mod colors.Length),
                    .Points = samples.Select(Function(item) New AnalyticsTrendPoint With {.TimestampUtc = item.TimestampUtc, .Value = item.Values(column), .IsValid = item.Values(column).HasValue}).ToList()})
                parts.Add(preset.Labels(index) & ": " & If(values.Count = 0, "no data",
                    "min " & values.Min().ToString(preset.Format, Inv) & "  avg " & values.Average().ToString(preset.Format, Inv) & "  max " & values.Max().ToString(preset.Format, Inv)))
            Next
            If preset.IsPower Then
                Dim limit = _maxKw
                series.Add(New AnalyticsTrendSeries With {.Name = "Load % of " & limit.ToString("0.##", Inv) & " kW", .Color = UpsAtsView.WarnAmber,
                    .Points = samples.Select(Function(item) New AnalyticsTrendPoint With {.TimestampUtc = item.TimestampUtc,
                        .Value = If(item.Values(0).HasValue AndAlso limit > 0, item.Values(0).Value / limit * 100.0R, CType(Nothing, Double?)),
                        .IsValid = item.Values(0).HasValue AndAlso limit > 0}).ToList()})
                Dim gapSeconds = Math.Max(900, bucket * 3)
                Dim kwh = IntegrateKwh(samples, gapSeconds)
                parts.Add("Energy (integrated from recorded kW): " & If(kwh.HasValue, kwh.Value.ToString("#,##0.0", Inv) & " kWh", "—"))
            End If
            _summary.Text = preset.Name & "  •  " & _range.Text.ToLowerInvariant() & If(bucket > 0, "  •  averaged per " & bucket.ToString(Inv) & " s", "") &
                Environment.NewLine & String.Join("    •    ", parts)
            _chart.ChartTitle = preset.Name & " (historian)"
            _chart.UseLocalTime = True
            _chart.XAxisLabel = "Time (local)"
            _chart.YAxisLabel = preset.Unit
            _chart.WindowStartUtc = fromUtc
            _chart.WindowEndUtc = toUtc
            ' Values are stored on change, so a flat signal has long gaps that are not outages.
            _chart.PollingIntervalSeconds = Math.Max(900, bucket * 3)
            _chart.MinimumZoomSeconds = 30
            _chart.Series = series
        Catch ex As Exception When IsExpectedFailure(ex)
            If Not IsDisposed Then _summary.Text = "Historian unavailable: " & ex.Message
        Finally
            _trendLoading = False
            If _trendDirty AndAlso Not IsDisposed Then
                _trendDirty = False
                LoadTrendAsync()
            End If
        End Try
    End Sub

    ' Trapezoidal kWh from recorded kW; gaps longer than gapSeconds are not bridged.
    Private Shared Function IntegrateKwh(samples As IReadOnlyList(Of UpsColumnSample), gapSeconds As Integer) As Double?
        Dim total = 0.0R
        Dim any = False
        For index = 1 To samples.Count - 1
            Dim a = samples(index - 1)
            Dim b = samples(index)
            If Not a.Values(0).HasValue OrElse Not b.Values(0).HasValue Then Continue For
            Dim seconds = (b.TimestampUtc - a.TimestampUtc).TotalSeconds
            If seconds <= 0 OrElse seconds > gapSeconds Then Continue For
            total += (a.Values(0).Value + b.Values(0).Value) / 2.0R * seconds / 3600.0R
            any = True
        Next
        Return If(any, CType(total, Double?), Nothing)
    End Function

    Protected Overrides Sub OnFormClosed(e As FormClosedEventArgs)
        _timer.Stop()
        _timer.Dispose()
        MyBase.OnFormClosed(e)
    End Sub
End Class
