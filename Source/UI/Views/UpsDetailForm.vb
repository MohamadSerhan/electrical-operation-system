Imports System.Collections.Generic
Imports System.Drawing
Imports System.Globalization
Imports System.Linq
Imports CancellationTokenSource = System.Threading.CancellationTokenSource
Imports System.Threading.Tasks
Imports System.Windows.Forms

' Engineering view of one UPS: every value with phase, unit, source OID and scale; identity, quality, alarms, trend and energy.
Public NotInheritable Class UpsDetailForm
    Inherits Form

    Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture
    Private ReadOnly _device As UpsDevice
    Private ReadOnly _service As UpsAcquisitionService
    Private ReadOnly _historian As UpsHistorian
    Private ReadOnly _measurements As New DataGridView()
    Private ReadOnly _identity As New DataGridView()
    Private ReadOnly _alarms As New DataGridView()
    Private ReadOnly _alarmHistory As New DataGridView()
    Private ReadOnly _range As New ComboBox()
    Private _alarmHistoryNote As Label
    Private Const AlarmPageSize As Integer = 200
    Private ReadOnly _alarmRange As New ComboBox()
    Private ReadOnly _alarmOlder As New Button With {.Text = "Older", .Width = 75, .Dock = DockStyle.Right}
    Private ReadOnly _alarmNewer As New Button With {.Text = "Newer", .Width = 75, .Dock = DockStyle.Right}
    Private ReadOnly _alarmCursors As New List(Of UpsAlarmEvent) From {Nothing}
    Private _alarmPage As Integer
    Private _alarmEndUtc As DateTime
    Private _alarmLast As UpsAlarmEvent
    Private _alarmCancellation As CancellationTokenSource
    Private ReadOnly _chart As New AnalyticsTrendChart()
    Private ReadOnly _energy As Label
    Private ReadOnly _status As Label
    Private ReadOnly _timer As New Timer With {.Interval = 2000}
    Private _lastVersion As Long = -1
    Private _trendLoading As Boolean
    Private _trendDirty As Boolean
    Private ReadOnly _metric As New ComboBox()
    Private ReadOnly _energyGrid As New DataGridView()
    Private ReadOnly _energyUnit As New ComboBox()
    Private _energyNote As Label
    Private _energyLoading As Boolean

    Public Sub New(device As UpsDevice, service As UpsAcquisitionService, historian As UpsHistorian)
        _device = device
        _service = service
        _historian = historian
        Text = "UPS " & device.DisplayName & "  •  " & device.IpAddress & "  •  ATS " & device.AtsBank.ToString(Inv)
        StartPosition = FormStartPosition.CenterParent
        Size = New Size(1120, 780)
        MinimumSize = New Size(820, 560)
        BackColor = UiTheme.Canvas
        Font = New Font("Segoe UI", 9.0F)
        ShowInTaskbar = False

        _status = UiTheme.Label(String.Empty, New Font("Segoe UI", 9.5F, FontStyle.Bold), UiTheme.Ink, False)
        _status.Dock = DockStyle.Fill
        _status.Padding = New Padding(12, 6, 12, 0)
        _range.DropDownStyle = ComboBoxStyle.DropDownList
        _range.Items.AddRange({"Last 24 hours", "Last 7 days", "Last 30 days"})
        _range.SelectedIndex = 0
        _range.Dock = DockStyle.Fill
        _range.Visible = False
        Dim rangeHost As New Panel With {.Dock = DockStyle.Right, .Width = 170, .Padding = New Padding(6, 4, 12, 0)}
        rangeHost.Controls.Add(_range)
        Dim topBar As New Panel With {.Dock = DockStyle.Top, .Height = 32}
        topBar.Controls.Add(_status)
        topBar.Controls.Add(rangeHost)
        _metric.DropDownStyle = ComboBoxStyle.DropDownList
        For Each preset In Presets
            _metric.Items.Add(preset.Name)
        Next
        _metric.SelectedIndex = 0
        _metric.Dock = DockStyle.Fill
        _metric.Visible = False
        Dim metricHost As New Panel With {.Dock = DockStyle.Right, .Width = 270, .Padding = New Padding(6, 4, 0, 0)}
        metricHost.Controls.Add(_metric)
        topBar.Controls.Add(metricHost)

        Dim tabs As New FlatTabControl With {.Dock = DockStyle.Fill, .Appearance = TabAppearance.FlatButtons, .SizeMode = TabSizeMode.Fixed, .ItemSize = New Size(0, 1)}
        tabs.TabPages.Add(MakeTab("Measurements", _measurements))
        tabs.TabPages.Add(MakeTab("Identity & Quality", _identity))
        Dim alarmTab = New TabPage("Alarms") With {.BackColor = UiTheme.Canvas, .Padding = New Padding(10)}
        _alarms.Dock = DockStyle.Fill
        ConfigureGrid(_alarms, {"State", "Alarm / condition", "Card type / severity", "Acknowledgement", "Occurred at uptime", "Source / OID"})
        _alarms.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells
        _alarms.DefaultCellStyle.WrapMode = DataGridViewTriState.True
        _alarms.Columns(0).MinimumWidth = 100
        _alarms.Columns(1).MinimumWidth = 240
        _alarms.Columns(1).FillWeight = 220
        _alarms.Columns(5).MinimumWidth = 220
        For Each column As DataGridViewColumn In _alarms.Columns
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft
        Next
        alarmTab.Controls.Add(_alarms)
        tabs.TabPages.Add(alarmTab)
        Dim historyTab = New TabPage("Alarm History") With {.BackColor = UiTheme.Canvas, .Padding = New Padding(10)}
        _alarmHistory.Dock = DockStyle.Fill
        ConfigureGrid(_alarmHistory, {"Time", "Alarm / condition", "State", "Severity", "Acknowledgement", "Detail"})
        _alarmHistory.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None
        _alarmHistory.RowTemplate.Height = 30
        _alarmHistory.ShowCellToolTips = True
        For Each column As DataGridViewColumn In _alarmHistory.Columns
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft
        Next
        _alarmHistory.Columns(0).FillWeight = 120
        _alarmHistory.Columns(1).FillWeight = 190
        _alarmHistory.Columns(2).FillWeight = 80
        _alarmHistory.Columns(3).FillWeight = 80
        _alarmHistory.Columns(4).FillWeight = 110
        _alarmHistory.Columns(5).FillWeight = 260
        _alarmHistoryNote = UiTheme.Label("Select a period to read saved alarm changes.", New Font("Segoe UI", 9.0F, FontStyle.Bold), UiTheme.MutedInk, False)
        _alarmHistoryNote.Dock = DockStyle.Fill
        _alarmRange.DropDownStyle = ComboBoxStyle.DropDownList
        _alarmRange.Items.AddRange({"Day (24 hours)", "Week (7 days)", "Month (30 days)", "All"})
        _alarmRange.SelectedIndex = 0
        _alarmRange.Dock = DockStyle.Right
        _alarmRange.Width = 155
        _alarmNewer.Enabled = False
        _alarmOlder.Enabled = False
        Dim alarmHeader As New Panel With {.Dock = DockStyle.Top, .Height = 38}
        alarmHeader.Controls.Add(_alarmHistoryNote)
        alarmHeader.Controls.Add(_alarmRange)
        alarmHeader.Controls.Add(_alarmNewer)
        alarmHeader.Controls.Add(_alarmOlder)
        AddHandler _alarmRange.SelectedIndexChanged, Sub(sender, e) LoadAlarmHistoryAsync()
        AddHandler _alarmOlder.Click, Sub(sender, e)
                                          _alarmCursors.Add(_alarmLast)
                                          _alarmPage += 1
                                          LoadAlarmHistoryAsync(False)
                                      End Sub
        AddHandler _alarmNewer.Click, Sub(sender, e)
                                          _alarmCursors.RemoveAt(_alarmPage)
                                          _alarmPage -= 1
                                          LoadAlarmHistoryAsync(False)
                                      End Sub
        historyTab.Controls.Add(_alarmHistory)
        historyTab.Controls.Add(alarmHeader)
        tabs.TabPages.Add(historyTab)
        Dim trendTab = New TabPage("Trend & Energy") With {.BackColor = UiTheme.Canvas, .Padding = New Padding(10)}
        _chart.Dock = DockStyle.Fill
        _chart.ChartTitle = "Output kW and highest-phase load % (historian)"
        trendTab.Controls.Add(_chart)
        _energy = UiTheme.Label("Loading historian…", New Font("Segoe UI", 9.5F, FontStyle.Bold), UiTheme.Ink, False)
        _energy.Dock = DockStyle.Top
        _energy.Height = 44
        trendTab.Controls.Add(_energy)
        tabs.TabPages.Add(trendTab)
        Dim energyTab = New TabPage("Energy kWh") With {.BackColor = UiTheme.Canvas, .Padding = New Padding(10)}
        ConfigureGrid(_energyGrid, {"Period", "Shift A kWh (06:00-18:00)", "Shift B kWh (18:00-06:00)", "Total kWh", "Data coverage"})
        _energyGrid.Columns(0).DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft
        _energyGrid.Dock = DockStyle.Fill
        _energyUnit.DropDownStyle = ComboBoxStyle.DropDownList
        _energyUnit.Items.AddRange({"Per day", "Per week", "Per month", "Per year"})
        _energyUnit.SelectedIndex = 0
        _energyUnit.Dock = DockStyle.Right
        _energyUnit.Width = 150
        AddHandler _energyUnit.SelectedIndexChanged, Sub(sender, e) LoadEnergyAsync()
        _energyNote = UiTheme.Label("Loading energy…", New Font("Segoe UI", 9.5F, FontStyle.Bold), UiTheme.Ink, False)
        _energyNote.Dock = DockStyle.Fill
        Dim energyHeader As New Panel With {.Dock = DockStyle.Top, .Height = 34, .Padding = New Padding(0, 4, 0, 4)}
        energyHeader.Controls.Add(_energyNote)
        energyHeader.Controls.Add(_energyUnit)
        energyTab.Controls.Add(_energyGrid)
        energyTab.Controls.Add(energyHeader)
        tabs.TabPages.Add(energyTab)
        AddHandler tabs.SelectedIndexChanged, Sub(sender, e)
                                                  If tabs.SelectedTab Is energyTab Then LoadEnergyAsync() Else LoadHistoryFor(tabs.SelectedTab, trendTab, historyTab)
                                              End Sub
        AddHandler _range.SelectedIndexChanged, Sub(sender, e) LoadHistoryFor(tabs.SelectedTab, trendTab, historyTab)
        AddHandler _metric.SelectedIndexChanged, Sub(sender, e) LoadHistoryFor(tabs.SelectedTab, trendTab, historyTab)

        Controls.Add(tabs)
        Controls.Add(New SegmentedTabStrip(tabs))
        Controls.Add(topBar)
        ConfigureGrid(_measurements, {"Parameter", "L1", "L2", "L3", "Total / value", "Unit", "Source"})
        _measurements.Columns(0).MinimumWidth = 210
        _measurements.Columns(0).DefaultCellStyle.WrapMode = DataGridViewTriState.True
        _measurements.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells
        For index = 1 To 3
            _measurements.Columns(index).MinimumWidth = 65
        Next
        _measurements.Columns(4).MinimumWidth = 140
        _measurements.Columns(5).MinimumWidth = 40
        _measurements.Columns(6).MinimumWidth = 180
        Dim weights = {240.0F, 80.0F, 80.0F, 80.0F, 140.0F, 55.0F, 280.0F}
        For index = 0 To weights.Length - 1
            _measurements.Columns(index).FillWeight = weights(index)
        Next
        ConfigureGrid(_identity, {"Item", "Value"})
        _identity.Columns(1).FillWeight = 300
        _identity.DefaultCellStyle.WrapMode = DataGridViewTriState.True
        _identity.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells
        AddHandler _timer.Tick, Sub(sender, e) RefreshData(False)
        RefreshData(True)
        _timer.Start()
    End Sub

    Private Shared Function MakeTab(title As String, grid As DataGridView) As TabPage
        Dim page As New TabPage(title) With {.BackColor = UiTheme.Canvas, .Padding = New Padding(10)}
        grid.Dock = DockStyle.Fill
        page.Controls.Add(grid)
        Return page
    End Function

    Private Shared Sub ConfigureGrid(grid As DataGridView, columns As String())
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
        grid.Columns(grid.Columns.Count - 1).DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft
        grid.Columns(grid.Columns.Count - 1).DefaultCellStyle.ForeColor = UiTheme.MutedInk
    End Sub

    Private Sub RefreshData(force As Boolean)
        If IsDisposed Then Return
        If _service Is Nothing Then
            _status.Text = "UPS acquisition is not running – no live values."
            _status.ForeColor = UpsAtsView.WarnAmber
            FillAlarms(Nothing)
            Return
        End If
        If Not force AndAlso _service.Version = _lastVersion Then Return
        _lastVersion = _service.Version
        Dim current = _service.GetSnapshot(_device.Id)
        Dim shown = If(current IsNot Nothing AndAlso current.IsGood, current, If(current Is Nothing, Nothing, current.LastGood))
        Dim isLive = current IsNot Nothing AndAlso current.IsGood
        If current Is Nothing Then
            _status.Text = "Waiting for the first poll…"
            _status.ForeColor = UiTheme.MutedInk
        ElseIf isLive Then
            _status.Text = "LIVE  •  " & current.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", Inv) & "  •  round trip " &
                UpsAtsView.Fmt(current.RoundTripMilliseconds, "0", " ms") & "  •  " & current.QualityDetail
            _status.ForeColor = UpsAtsView.OkGreen
        Else
            _status.Text = UpsQualityText.Describe(current.Quality) & "  •  " & current.QualityDetail &
                If(shown IsNot Nothing, "   —   table shows LAST GOOD values from " & shown.TimestampUtc.ToLocalTime().ToString("dd MMM HH:mm:ss", Inv) & " (not live)", "")
            _status.ForeColor = UpsAtsView.QualityColor(current.Quality)
        End If
        FillMeasurements(shown, isLive)
        FillIdentity(current, shown)
        FillAlarms(current)
    End Sub

    Private Sub FillAlarms(current As UpsSnapshot)
        _alarms.Rows.Clear()
        If current Is Nothing OrElse current.Quality = UpsQuality.Disabled Then
            _alarms.Rows.Add("NOT VERIFIED", "No current card alarm reading.", "", "", "", "Acquisition unavailable or disabled")
            Return
        End If
        Dim alarms = UpsAlarmEvaluator.Evaluate(current, AlarmSettingsStore.Current.Ups)
        If alarms.Count = 0 Then
            _alarms.Rows.Add("CLEAR", "No active card alarms or local limit violations.", "", "", "", "Configuration > Alarm Limits > UPS")
        Else
            For Each item In alarms.OrderByDescending(Function(alarm) alarm.Severity).ThenByDescending(Function(alarm) alarm.CardAlarm IsNot Nothing)
                Dim alarm = item.CardAlarm
                Dim index = If(alarm Is Nothing,
                    _alarms.Rows.Add(item.Severity.ToString().ToUpperInvariant(), item.Text, "", "", "", "EOEMS evaluation / read status"),
                    _alarms.Rows.Add(alarm.StateText, alarm.Name, alarm.CardType & " / " & alarm.CardSeverity,
                        If(Not alarm.Acknowledged.HasValue, "Not provided", If(alarm.Acknowledged.Value, "Acknowledged", "Not acknowledged")),
                        If(alarm.OccurredAtUptime.Length = 0, "Not provided", alarm.OccurredAtUptime),
                        alarm.Source & " | " & alarm.DescriptionOid & " | row " & alarm.Id))
                _alarms.Rows(index).DefaultCellStyle.ForeColor = If(item.Severity = AlarmSeverity.Normal, UiTheme.Ink,
                    If(item.Severity >= AlarmSeverity.Alarm, UpsAtsView.AlarmRed, UpsAtsView.WarnAmber))
                _alarms.Rows(index).Cells(5).ToolTipText = If(alarm Is Nothing, item.Text,
                    alarm.TableOid & Environment.NewLine & "Occurrence time is the card's uptime, not a calendar timestamp." &
                    If(alarm.EvidenceConflict, Environment.NewLine & current.AlarmDetailsStatus, String.Empty))
            Next
        End If
        _alarms.ClearSelection()
    End Sub

    Private Sub FillMeasurements(snapshot As UpsSnapshot, isLive As Boolean)
        Dim scroll = _measurements.FirstDisplayedScrollingRowIndex
        _measurements.Rows.Clear()
        _measurements.DefaultCellStyle.ForeColor = If(isLive, UiTheme.Ink, UpsAtsView.NoDataGrey)
        If snapshot Is Nothing Then
            _measurements.Rows.Add("No reading available", "—", "—", "—", "—", "", "")
            Return
        End If
        Dim section = Sub(title As String)
                          Dim index = _measurements.Rows.Add(title, "", "", "", "", "", "")
                          _measurements.Rows(index).DefaultCellStyle.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
                          _measurements.Rows(index).DefaultCellStyle.BackColor = Color.FromArgb(232, 239, 248)
                      End Sub
        Dim sourceCaption = Function(key As String) As String
                                If Not snapshot.Value(key).HasValue AndAlso
                                   Not Enumerable.Range(0, 3).Any(Function(index) snapshot.Phase(key, index).HasValue) Then
                                    Return snapshot.MissingChannelReason(key)
                                End If
                                If Not snapshot.MeasurementSources.ContainsKey(key) Then Return snapshot.MeasurementSource(key)
                                Dim source = snapshot.MeasurementSource(key)
                                Dim bindings = UpsMeasurementMap.Candidates.Where(Function(item) source.Contains(item.Definition.Oid & " ×", StringComparison.Ordinal)).ToList()
                                If bindings.Count = 0 Then Return "RFC1628: " & source
                                Return "Vertiv FLEX: " & String.Join("/", bindings.Select(Function(item) item.Index.Split("."c).Last())) &
                                    " ×" & If(bindings.Count = 0, "?", bindings(0).Definition.Scale.ToString("0.###", Inv))
                            End Function
        Dim phaseRow = Sub(title As String, key As String, format As String, totalKind As String, divisor As Double, unitOverride As String)
                           Dim definition = UpsOidMap.Find(key)
                           Dim cell = Function(index As Integer) As String
                                          Dim value = snapshot.Phase(key, index)
                                          Return UpsAtsView.Fmt(If(value.HasValue, value.Value / divisor, CType(Nothing, Double?)), format)
                                      End Function
                           Dim total As Double? = Nothing
                           Select Case totalKind
                               Case "sum" : total = If(snapshot.Value(key), snapshot.PhaseSum(key))
                               Case "avg" : total = snapshot.PhaseAverage(key)
                               Case "max" : total = snapshot.PhaseMax(key)
                           End Select
                           Dim totalText = If(totalKind.Length = 0, "", UpsAtsView.Fmt(If(total.HasValue, total.Value / divisor, CType(Nothing, Double?)), format) & " (" & totalKind & ")")
                           Dim row = _measurements.Rows.Add(title, cell(0), cell(1), cell(2), totalText, If(unitOverride, definition.Unit), sourceCaption(key))
                           _measurements.Rows(row).Cells(6).ToolTipText = snapshot.MeasurementSource(key)
                       End Sub
        Dim scalarRow = Sub(title As String, key As String, format As String)
                            Dim definition = UpsOidMap.Find(key)
                            Dim row = _measurements.Rows.Add(title, "", "", "", UpsAtsView.Fmt(snapshot.Value(key), format), definition.Unit, sourceCaption(key))
                            _measurements.Rows(row).Cells(6).ToolTipText = snapshot.MeasurementSource(key)
                        End Sub
        Dim textRow = Sub(title As String, value As String, unit As String, source As String)
                          _measurements.Rows.Add(title, "", "", "", value, unit, source)
                      End Sub

        section("OUTPUT")
        textRow("Output source", snapshot.OutputSourceText, "", UpsOidMap.Find("OutputSource").Oid)
        phaseRow("Output voltage (" & snapshot.OutputVoltageReference & ")", "OutputVoltage", "0.0", "avg", 1.0R, Nothing)
        If snapshot.Phases.ContainsKey("OutputVoltageLL") Then phaseRow("Output voltage L-L (AB / BC / CA)", "OutputVoltageLL", "0.0", "", 1.0R, Nothing)
        phaseRow("Output current (not an arithmetic total)", "OutputCurrent", "0.0", "", 1.0R, Nothing)
        If snapshot.Phases.ContainsKey("OutputPower") Then
            phaseRow("Output active power (phase / sum)", "OutputPower", "0.0", "sum", 1000.0R, "kW")
        ElseIf snapshot.MeasurementSources.ContainsKey("OutputPower") Then
            textRow("System output active power", UpsAtsView.Fmt(snapshot.OutputKw, "0.0"), "kW",
                    sourceCaption("OutputPower") & "; no phase kW")
            _measurements.Rows(_measurements.Rows.Count - 1).Cells(6).ToolTipText = snapshot.MeasurementSource("OutputPower")
        Else
            phaseRow("Output active power (phase / sum)", "OutputPower", "0.0", "sum", 1000.0R, "kW")
        End If
        If snapshot.Phases.ContainsKey("OutputApparentVa") Then
            phaseRow("Output apparent power (phase / sum)", "OutputApparentVa", "0.0", "sum", 1000.0R, "kVA")
        Else
            textRow("Output apparent power", UpsAtsView.Fmt(snapshot.OutputKvaDerived, "0.0"), "kVA",
                    If(snapshot.MeasurementSources.ContainsKey("OutputApparentVa"), sourceCaption("OutputApparentVa"),
                       "DERIVED: Σ verified L-N V × I"))
            _measurements.Rows(_measurements.Rows.Count - 1).Cells(6).ToolTipText = snapshot.MeasurementSource("OutputApparentVa")
        End If
        textRow("System output power factor", UpsAtsView.Fmt(snapshot.PowerFactorDerived, "0.00"), "", "DERIVED: system kW ÷ kVA; outside 0–1 rejected")
        If snapshot.Phases.ContainsKey("OutputPhasePf") Then phaseRow("Output phase power factor (card)", "OutputPhasePf", "0.00", "", 1.0R, Nothing)
        phaseRow("Output load (active power)", "OutputLoad", "0", "max", 1.0R, Nothing)
        If snapshot.Phases.ContainsKey("OutputVaLoad") Then phaseRow("Output load (apparent power / VA)", "OutputVaLoad", "0", "max", 1.0R, Nothing)
        scalarRow("Output frequency", "OutputFrequency", "0.00")
        Dim rated = snapshot.Value("RatedOutputVa")
        textRow("Rated output (card)", If(rated.HasValue, (rated.Value / 1000).ToString("0", Inv), "—"), "kVA", UpsOidMap.Find("RatedOutputVa").Oid)
        textRow("Rated output (registry)", _device.RatedKva.ToString("0", Inv), "kVA", "UPS registry")
        section("INPUT")
        phaseRow("Input voltage (" & snapshot.InputVoltageReference & ")", "InputVoltage", "0.0", "avg", 1.0R, Nothing)
        phaseRow("Input current", "InputCurrent", "0.0", "", 1.0R, Nothing)
        phaseRow("Input true power", "InputPower", "0.0", "sum", 1000.0R, "kW")
        For Each estimate In UpsInputEstimates.Metrics()
            Dim estimatedValue = UpsInputEstimates.Calculate(snapshot, estimate.Id)
            textRow(estimate.Name, UpsAtsView.Fmt(estimatedValue, "0.0"), estimate.Unit,
                estimate.Basis & If(estimatedValue.HasValue, "", " " & UpsInputEstimates.MissingReason(snapshot, estimate.Id)))
        Next
        If snapshot.MeasurementSources.ContainsKey("InputFrequency") Then
            scalarRow("Input frequency", "InputFrequency", "0.00")
        Else
            phaseRow("Input frequency", "InputFrequency", "0.0", "", 1.0R, Nothing)
        End If
        scalarRow("Input line bad events (counter)", "InputLineBads", "0")
        section("BYPASS")
        phaseRow("Bypass voltage (" & snapshot.BypassVoltageReference & ")", "BypassVoltage", "0.0", "avg", 1.0R, Nothing)
        scalarRow("Bypass frequency", "BypassFrequency", "0.00")
        section("BATTERY")
        textRow("Battery status", snapshot.BatteryStatusText, "", UpsOidMap.Find("BatteryStatus").Oid)
        scalarRow("Charge remaining", "ChargePercent", "0")
        scalarRow("Estimated runtime", "RuntimeMinutes", "0")
        scalarRow("Battery voltage", "BatteryVoltage", "0.0")
        scalarRow("Battery current (+ discharge / − charge)", "BatteryCurrent", "0.0")
        scalarRow("Battery temperature", "BatteryTemperature", "0")
        scalarRow("Time on battery", "SecondsOnBattery", "0")
        If snapshot.MeasurementSources.ContainsKey("BatteryCabinetVoltage") Then scalarRow("Battery cabinet voltage (card)", "BatteryCabinetVoltage", "0.0")
        If snapshot.MeasurementSources.ContainsKey("BatteryCabinetTemperature") Then scalarRow("Battery cabinet temperature (card)", "BatteryCabinetTemperature", "0.0")
        If snapshot.MeasurementSources.ContainsKey("BatteryDischargeHours") Then
            scalarRow("Total discharge time (lifetime, not runtime)", "BatteryDischargeHours", "0")
            scalarRow("Battery discharge power", "BatteryDischargePower", "0")
            scalarRow("Battery amp-hours consumed", "BatteryAhConsumed", "0")
        End If
        If {"DcBusVoltage", "DcBusCurrent", "InletTemperature"}.Any(Function(key) snapshot.MeasurementSources.ContainsKey(key)) Then
            section("DC BUS / ENVIRONMENT (not battery current)")
            scalarRow("DC bus voltage", "DcBusVoltage", "0")
            scalarRow("DC bus current", "DcBusCurrent", "0")
            scalarRow("Inlet air temperature", "InletTemperature", "0")
        End If
        section("ALARMS")
        scalarRow("Alarms present (card)", "AlarmsPresent", "0")
        If snapshot.InvalidFields.Count > 0 Then
            textRow("Out-of-range values ignored", String.Join(", ", snapshot.InvalidFields), "", "validation")
        End If
        If scroll > 0 AndAlso scroll < _measurements.RowCount Then _measurements.FirstDisplayedScrollingRowIndex = scroll
        _measurements.ClearSelection()
    End Sub

    Private Sub FillIdentity(current As UpsSnapshot, shown As UpsSnapshot)
        _identity.Rows.Clear()
        Dim add = Sub(item As String, value As String) _identity.Rows.Add(item, If(String.IsNullOrEmpty(value), "—", value))
        add("Registry ID (immutable)", _device.Id)
        add("Label", _device.Label)
        add("ATS bank", _device.AtsBank.ToString(Inv))
        add("IP address / port", _device.IpAddress & " : " & _device.Port.ToString(Inv))
        add("Credential profile", _device.CredentialProfile & "  (secrets are never displayed)")
        add("Registry model", _device.Model)
        add("Identity verification", UpsQualityText.DescribeVerification(_device.Verification))
        add("Registry notes", _device.Notes)
        add("Poll interval / timeout / retries", _device.PollIntervalSeconds & " s / " & _device.TimeoutMilliseconds & " ms / " & _device.Retries)
        Dim source = If(shown, current)
        If source IsNot Nothing Then
            add("Measurement mapping", source.MeasurementMap)
            add("Measurement revision", source.MeasurementRevision.ToString(Inv))
            add("sysName (device)", source.SysName)
            add("sysDescr", source.SysDescr)
            add("sysObjectID", source.SysObjectId & If(source.SysObjectId.StartsWith("1.3.6.1.4.1.476.", StringComparison.Ordinal), "  (Liebert / Vertiv enterprise)", ""))
            add("sysUpTime", source.SysUpTime)
            add("Manufacturer (UPS-MIB)", source.Manufacturer)
            add("Model (UPS-MIB)", source.IdentModel)
            add("UPS firmware", source.UpsSoftware)
            add("Agent / card firmware", source.AgentSoftware)
            add("Card alarm detail status", source.AlarmDetailsStatus)
            For Each unsupported In source.UnsupportedOids
                add("Unavailable card parameter (not zero)", unsupported)
            Next
        End If
        If current IsNot Nothing Then
            add("Current quality", UpsQualityText.Describe(current.Quality) & "  –  " & current.QualityDetail)
            add("Consecutive failures", current.ConsecutiveFailures.ToString(Inv))
            add("Last good reading", If(current.LastGoodUtc.HasValue, current.LastGoodUtc.Value.ToLocalTime().ToString("dd MMM yyyy HH:mm:ss", Inv), "never"))
        End If
    End Sub

    Private Function RangeHours() As Integer
        Return {24, 168, 720}(Math.Max(0, _range.SelectedIndex))
    End Function

    Private Async Sub LoadEnergyAsync()
        If _energyLoading Then Return
        _energyLoading = True
        _range.Visible = False
        _metric.Visible = False
        Try
            Dim unit = EnergyAccounting.Units(Math.Max(0, _energyUnit.SelectedIndex))
            Dim id = _device.Id
            Dim cells = Await Task.Run(Function()
                                           Try
                                               EnergyAccounting.Recompute(1)
                                           Catch ex As Exception When TypeOf ex Is Npgsql.NpgsqlException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is TimeoutException
                                           End Try
                                           Return EnergyAccounting.ReadCells(unit, {"UPS"}, 400).Where(Function(cell) cell.EquipmentId = id).ToArray()
                                       End Function)
            If IsDisposed Then Return
            Dim text = Function(items As IEnumerable(Of EnergyAccounting.EnergyCell)) As String
                           Dim list = items.ToArray()
                           Return If(list.Length = 0, "—", list.Sum(Function(cell) cell.Kwh).ToString("#,##0.0", Inv))
                       End Function
            _energyGrid.SuspendLayout()
            _energyGrid.Rows.Clear()
            For Each period In cells.GroupBy(Function(cell) cell.Period).OrderByDescending(Function(group) group.Key)
                Dim items = period.ToArray()
                _energyGrid.Rows.Add(EnergyAccounting.PeriodLabel(unit, period.Key), text(items.Where(Function(cell) cell.Shift = "A")),
                                     text(items.Where(Function(cell) cell.Shift = "B")), text(items), EnergyAccounting.CoverageText(items))
            Next
            _energyGrid.ResumeLayout()
            _energyNote.Text = "Energy of " & _device.DisplayName & " from its recorded output kW (good readings only), saved per shift (A 06:00-18:00, B 18:00-06:00) in PostgreSQL."
        Catch ex As Exception When TypeOf ex Is Npgsql.NpgsqlException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is TimeoutException
            If Not IsDisposed Then _energyNote.Text = "Energy history unavailable: " & ex.Message
        Finally
            _energyLoading = False
        End Try
    End Sub

    Private Sub LoadHistoryFor(selected As TabPage, trendTab As TabPage, historyTab As TabPage)
        _range.Visible = selected Is trendTab
        _metric.Visible = selected Is trendTab
        If selected Is trendTab Then
            If _trendLoading Then _trendDirty = True Else LoadTrendAsync()
        End If
        If selected Is historyTab Then LoadAlarmHistoryAsync()
    End Sub

    Private Async Sub LoadAlarmHistoryAsync(Optional reset As Boolean = True)
        If _historian Is Nothing Then
            _alarmHistoryNote.Text = "UPS historian is not running."
            Return
        End If
        If reset Then
            _alarmCursors.Clear()
            _alarmCursors.Add(Nothing)
            _alarmPage = 0
            _alarmEndUtc = DateTime.UtcNow
        End If
        If _alarmCancellation IsNot Nothing Then _alarmCancellation.Cancel()
        Dim request As New CancellationTokenSource()
        _alarmCancellation = request
        Dim page = _alarmPage
        Dim before = _alarmCursors(page)
        Dim toUtc = _alarmEndUtc
        Dim fromUtc = If(_alarmRange.SelectedIndex = 3, DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
                         toUtc.AddDays(-New Integer() {1, 7, 30}(Math.Max(0, Math.Min(2, _alarmRange.SelectedIndex)))))
        _alarmOlder.Enabled = False
        _alarmNewer.Enabled = False
        _alarmHistory.Rows.Clear()
        _alarmHistoryNote.Text = "Loading alarm history..."
        Try
            Dim events = Await Task.Run(Function() UpsHistorian.ReadAlarmEvents(_device.Id, fromUtc, toUtc, AlarmPageSize + 1,
                                                                                before:=before, cancellation:=request.Token), request.Token)
            If IsDisposed OrElse request.IsCancellationRequested Then Return
            _alarmHistory.SuspendLayout()
            Try
                For Each item In events.Take(AlarmPageSize)
                    Dim index = _alarmHistory.Rows.Add(item.TimestampUtc.ToLocalTime().ToString("dd MMM yyyy HH:mm:ss", Inv), item.Condition, item.State, item.Severity, item.Acknowledged, item.Detail)
                    _alarmHistory.Rows(index).Cells(5).ToolTipText = item.Detail
                    If item.State = "ACTIVE" AndAlso item.Severity <> "Normal" Then
                        _alarmHistory.Rows(index).Cells(2).Style.ForeColor = UiTheme.Red
                    End If
                Next
            Finally
                _alarmHistory.ResumeLayout()
            End Try
            _alarmLast = events.Take(AlarmPageSize).LastOrDefault()
            _alarmOlder.Enabled = events.Count > AlarmPageSize
            _alarmNewer.Enabled = page > 0
            _alarmHistoryNote.Text = If(events.Count = 0, "No alarm events recorded in this period.",
                "Page " & (page + 1).ToString(Inv) & " - " & Math.Min(events.Count, AlarmPageSize).ToString(Inv) & " saved observations (newest first).")
        Catch ex As OperationCanceledException When request.IsCancellationRequested
        Catch ex As Npgsql.NpgsqlException When request.IsCancellationRequested
        Catch ex As Exception When TypeOf ex Is Npgsql.NpgsqlException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is TimeoutException OrElse
                                   TypeOf ex Is IO.IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is Security.Cryptography.CryptographicException
            If Not IsDisposed AndAlso Not request.IsCancellationRequested Then
                _alarmHistoryNote.Text = "Alarm history unavailable: " & ex.Message
                _alarmNewer.Enabled = page > 0
            End If
        Finally
            If _alarmCancellation Is request Then _alarmCancellation = Nothing
            request.Dispose()
        End Try
    End Sub

    Private Async Sub LoadTrendAsync()
        If _trendLoading Then Return
        If _historian Is Nothing Then
            _energy.Text = "UPS historian is not running."
            Return
        End If
        _trendLoading = True
        Try
            Dim toUtc = DateTime.UtcNow
            Dim fromUtc = toUtc.AddHours(-RangeHours())
            Dim bucket = BucketSeconds()
            _chart.UseLocalTime = True
            _chart.XAxisLabel = "Time (local)" & If(bucket > 0, " – averaged per " & CoverageText(bucket / 3600.0R), "")
            If _metric.SelectedIndex > 0 Then
                Await LoadMetricTrendAsync(fromUtc, toUtc, bucket)
                Return
            End If
            _chart.YAxisLabel = "kW  /  load %"
            Dim samples = Await Task.Run(Function() _historian.ReadTrend(_device.Id, fromUtc, toUtc, bucket))
            If IsDisposed Then Return
            Dim interval = If(bucket > 0, bucket, UpsSettingsStore.Current.HistorianIntervalSeconds)
            Dim covered As Double
            Dim kwh = UpsHistorian.IntegrateKwh(samples, interval, covered)
            Dim todayStart = DateTime.Today.ToUniversalTime()
            Dim todayCovered As Double
            Dim kwhToday = UpsHistorian.IntegrateKwh(samples.Where(Function(item) item.TimestampUtc >= todayStart).ToList(), interval, todayCovered)
            Dim peak = samples.Where(Function(item) item.OutputKw.HasValue).Select(Function(item) item.OutputKw.Value).DefaultIfEmpty().Max()
            Dim goodCount = samples.Where(Function(item) item.OutputKw.HasValue).Count()
            _energy.Text = "Energy today: " & UpsAtsView.Fmt(kwhToday, "#,##0.0", " kWh") & " (" & CoverageText(todayCovered) & " covered)" &
                "    •    " & _range.Text.ToLowerInvariant() & ": " & UpsAtsView.Fmt(kwh, "#,##0.0", " kWh") & " (" & CoverageText(covered) & " covered)" &
                "    •    peak " & If(goodCount = 0, "—", peak.ToString("0.0", Inv) & " kW") &
                Environment.NewLine & "Revision 2+ kW samples only; older unverified power is excluded, not rewritten. Gaps are not filled."
            _chart.WindowStartUtc = fromUtc
            _chart.WindowEndUtc = toUtc
            _chart.PollingIntervalSeconds = interval
            _chart.Series = New List(Of AnalyticsTrendSeries) From {
                New AnalyticsTrendSeries With {.Name = "Output kW", .Color = UiTheme.Blue,
                    .Points = samples.Select(Function(item) New AnalyticsTrendPoint With {.TimestampUtc = item.TimestampUtc, .Value = item.OutputKw, .IsValid = item.OutputKw.HasValue}).ToList()},
                New AnalyticsTrendSeries With {.Name = "Load % (max phase)", .Color = UpsAtsView.WarnAmber,
                    .Points = samples.Select(Function(item) New AnalyticsTrendPoint With {.TimestampUtc = item.TimestampUtc, .Value = item.LoadMaxPercent, .IsValid = item.LoadMaxPercent.HasValue}).ToList()}}
        Catch ex As Exception When TypeOf ex Is Npgsql.NpgsqlException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is TimeoutException OrElse
                                   TypeOf ex Is IO.IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is Security.Cryptography.CryptographicException
            If Not IsDisposed Then _energy.Text = "Historian unavailable: " & ex.Message
        Finally
            _trendLoading = False
            If _trendDirty Then
                _trendDirty = False
                LoadTrendAsync()
            End If
        End Try
    End Sub

    Private Function BucketSeconds() As Integer
        Dim hours = RangeHours()
        Return If(hours <= 24, 0, CInt(Math.Ceiling(hours * 3600.0R / 3000.0R)))
    End Function

    Private Class MetricPreset
        Public Name As String
        Public Unit As String
        Public Columns As String()
        Public Labels As String()
        Public Format As String = "0.0"
    End Class

    Private Shared ReadOnly Presets As MetricPreset() = {
        New MetricPreset With {.Name = "Power: output kW + load %"},
        New MetricPreset With {.Name = "Output voltage (V)", .Unit = "V", .Columns = {"out_v_l1", "out_v_l2", "out_v_l3"}, .Labels = {"L1", "L2", "L3"}},
        New MetricPreset With {.Name = "Output current (A)", .Unit = "A", .Columns = {"out_i_l1", "out_i_l2", "out_i_l3"}, .Labels = {"L1", "L2", "L3"}},
        New MetricPreset With {.Name = "Input voltage (V)", .Unit = "V", .Columns = {"in_v_l1", "in_v_l2", "in_v_l3"}, .Labels = {"L1", "L2", "L3"}},
        New MetricPreset With {.Name = "Input current (A)", .Unit = "A", .Columns = {"in_i_l1", "in_i_l2", "in_i_l3"}, .Labels = {"L1", "L2", "L3"}},
        New MetricPreset With {.Name = "Bypass voltage (V)", .Unit = "V", .Columns = {"bypass_v_l1", "bypass_v_l2", "bypass_v_l3"}, .Labels = {"L1", "L2", "L3"}},
        New MetricPreset With {.Name = "Load per phase (%)", .Unit = "%", .Columns = {"load_l1", "load_l2", "load_l3"}, .Labels = {"L1", "L2", "L3"}},
        New MetricPreset With {.Name = "Frequency (Hz)", .Unit = "Hz", .Columns = {"out_freq_hz", "in_freq_hz"}, .Labels = {"Output", "Input"}, .Format = "0.00"},
        New MetricPreset With {.Name = "Apparent power kVA / power factor", .Unit = "kVA", .Columns = {"output_kva"}, .Labels = {"Output kVA"}},
        New MetricPreset With {.Name = "Battery charge (%)", .Unit = "%", .Columns = {"battery_charge_pct"}, .Labels = {"Charge"}},
        New MetricPreset With {.Name = "Battery runtime (min)", .Unit = "min", .Columns = {"battery_runtime_min"}, .Labels = {"Runtime"}},
        New MetricPreset With {.Name = "Battery voltage / current", .Unit = "V / A", .Columns = {"battery_voltage_v", "battery_current_a"}, .Labels = {"Voltage (V)", "Current (A)"}},
        New MetricPreset With {.Name = "Battery temperature (°C)", .Unit = "°C", .Columns = {"battery_temp_c"}, .Labels = {"Temperature"}}}

    Private Async Function LoadMetricTrendAsync(fromUtc As DateTime, toUtc As DateTime, bucket As Integer) As Task
        Dim preset = Presets(_metric.SelectedIndex)
        Dim samples = Await Task.Run(Function() _historian.ReadColumns(_device.Id, fromUtc, toUtc, preset.Columns, bucket))
        If IsDisposed Then Return
        Dim colors = {UiTheme.Blue, UpsAtsView.WarnAmber, Color.FromArgb(22, 150, 90)}
        Dim series As New List(Of AnalyticsTrendSeries)()
        Dim summary As New List(Of String)()
        For index = 0 To preset.Columns.Length - 1
            Dim column = index
            Dim values = samples.Where(Function(item) item.Values(column).HasValue).Select(Function(item) item.Values(column).Value).ToList()
            series.Add(New AnalyticsTrendSeries With {.Name = preset.Labels(index), .Color = colors(index Mod colors.Length),
                .Points = samples.Select(Function(item) New AnalyticsTrendPoint With {.TimestampUtc = item.TimestampUtc, .Value = item.Values(column), .IsValid = item.Values(column).HasValue}).ToList()})
            summary.Add(preset.Labels(index) & ": " & If(values.Count = 0, "no data",
                "min " & values.Min().ToString(preset.Format, Inv) & "  avg " & values.Average().ToString(preset.Format, Inv) & "  max " & values.Max().ToString(preset.Format, Inv)))
        Next
        _energy.Text = preset.Name & "  •  " & _range.Text.ToLowerInvariant() & Environment.NewLine & String.Join("    •    ", summary)
        _chart.WindowStartUtc = fromUtc
        _chart.WindowEndUtc = toUtc
        _chart.PollingIntervalSeconds = If(bucket > 0, bucket, Math.Max(1, UpsSettingsStore.Current.HistorianIntervalSeconds))
        _chart.YAxisLabel = preset.Unit
        _chart.Series = series
    End Function

    Private Shared Function CoverageText(hours As Double) As String
        Return If(hours < 1.0R, Math.Round(hours * 60.0R).ToString("0", Inv) & " min", hours.ToString("0.0", Inv) & " h")
    End Function

    Protected Overrides Sub OnFormClosed(e As FormClosedEventArgs)
        If _alarmCancellation IsNot Nothing Then _alarmCancellation.Cancel()
        _timer.Stop()
        _timer.Dispose()
        MyBase.OnFormClosed(e)
    End Sub
End Class
