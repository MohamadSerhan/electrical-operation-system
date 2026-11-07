Imports System.Drawing
Imports System.Globalization
Imports System.Linq
Imports System.Threading.Tasks
Imports System.Windows.Forms

Public NotInheritable Class TransformersView
    Inherits UserControl

    Private Shared ReadOnly CardAccentBlue As Color = UiTheme.Blue
    Private Shared ReadOnly LiveGreen As Color = UiTheme.Green
    Private Shared ReadOnly LiveAmber As Color = UiTheme.Amber
    Private Shared ReadOnly LiveRed As Color = UiTheme.Red
    Private Shared ReadOnly AlarmRed As Color = Color.FromArgb(185, 64, 64)
    Private Shared ReadOnly NotMappedInk As Color = UiTheme.MutedInk
    Private Shared ReadOnly TableBack As Color = Color.White
    Private Shared ReadOnly TableAlternateBack As Color = Color.FromArgb(246, 249, 252)
    Private Shared ReadOnly TableHeaderBack As Color = Color.FromArgb(232, 240, 247)
    Private Shared ReadOnly TableGridLine As Color = UiTheme.Border
    Private Shared ReadOnly TableHeaderInk As Color = UiTheme.ShellDeep
    Private Shared ReadOnly TableLabelInk As Color = UiTheme.MutedInk
    Private Shared ReadOnly TransformerMeasurementNames As String() = {
        "Load kW", "Load %", "Voltage L1-N", "Voltage L2-N", "Voltage L3-N",
        "Current L1", "Current L2", "Current L3", "Frequency", "Power Factor (calc.)",
        "Energy Today (calc.)", "Temperature"
    }
    ' Index >= 0 is a PLC value; -2 = calculated power factor; -3 = calculated energy today.
    Private Const RowPowerFactor As Integer = 9
    Private Const RowEnergyToday As Integer = 10
    Private Const RowTemperature As Integer = 11
    Private Const MeasurementHeaderHeight As Integer = 28
    Private Const MeasurementRowHeight As Integer = 25
    Private Shared ReadOnly TransformerMeasurementIndexes As Integer() = {10, 10, 0, 1, 2, 11, 12, 13, 8, -2, -3, 15}
    Private Shared ReadOnly TransformerMeasurementUnits As String() = {"kW", "%", "V", "V", "V", "A", "A", "A", "Hz", "", "kWh", "°C"}
    Private Shared ReadOnly MeasurementFormats As String() = {"0.0", "0.0", "0.0", "0.0", "0.0", "0.0", "0.0", "0.0", "0.00", "0.000", "0.0", "0.0"}
    Private Shared ReadOnly TotalMeasurementNames As String() = {
        "Total Load kW", "Total Load %", "Avg Voltage L1-N", "Avg Voltage L2-N",
        "Avg Voltage L3-N", "Total Current L1", "Total Current L2",
        "Total Current L3", "Avg Frequency", "Avg Power Factor (calc.)",
        "Energy Today (calc.)", "Avg Temperature"
    }
    Private Shared ReadOnly TotalMeasurementUnits As String() = {"kW", "%", "V", "V", "V", "A", "A", "A", "Hz", "", "kWh", "°C"}
    Private Shared ReadOnly ExpectedTransformerRows As Integer() = {0, 1, 2, 3, 4, 5, 6, 7, 8, 11}
    Private ReadOnly _energy As EnergyTodayTracker
    Private _lastTotals As Double?()

    Private ReadOnly _settings As PlcSettings
    Private ReadOnly _poll As Func(Of Task)
    Private ReadOnly _columns As New List(Of MeterColumnControls)()
    Private ReadOnly _lastGoodSnapshots As New Dictionary(Of String, MeterSnapshot)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _lastAcquisitionUtc As New Dictionary(Of String, DateTime)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _lastSuccessfulUpdateUtc As New Dictionary(Of String, DateTime)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _lastValidUpdateUtc As New Dictionary(Of String, DateTime)(StringComparer.OrdinalIgnoreCase)
    Private _lastSuccessfulTotalUpdateUtc As DateTime?
    Private _lastGoodTotalLoad As Double?
    Private ReadOnly _pollStatus As Label
    Private ReadOnly _lastPoll As Label
    Private ReadOnly _qualityGrid As DataGridView
    Private ReadOnly _historian As MeasurementHistorian
    Private ReadOnly _detailTip As New ToolTip()
    Private ReadOnly _openDetails As New Dictionary(Of Integer, TransformerDetailForm)()

    Public Sub New(settings As PlcSettings, status As PlcConnectionStatus, snapshots As IReadOnlyList(Of MeterSnapshot),     poll As Func(Of Task), Optional canRead As Boolean = True, Optional energy As EnergyTodayTracker = Nothing,
                   Optional historian As MeasurementHistorian = Nothing)
        _settings = settings
        _historian = historian
            _energy = energy
        _poll = poll
        BackColor = UiTheme.Canvas

        Dim root As New ScadaBackgroundPanel With {.Dock = DockStyle.Fill, .AutoScroll = True}
        Controls.Add(root)
        Dim banner As New ScadaPanel With {
            .Location = New Point(0, 0),
            .Size = New Size(Math.Max(600, root.ClientSize.Width - 4), 44),
            .BorderColor = UiTheme.Border,
            .Radius = 5,
            .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        }
        root.Controls.Add(banner)
        _pollStatus = UiTheme.Label(UiTheme.StatusText(status.Health), New Font("Segoe UI", 9.0F, FontStyle.Bold), UiTheme.StatusColor(status.Health), False)
        _pollStatus.Size = New Size(380, 20)
        _pollStatus.TextAlign = ContentAlignment.MiddleRight
        banner.Controls.Add(_pollStatus)
        _lastPoll = UiTheme.Label("", New Font("Segoe UI", 8.5F), UiTheme.MutedInk, False)
        _lastPoll.Size = New Size(380, 18)
        _lastPoll.TextAlign = ContentAlignment.MiddleRight
        _lastPoll.ForeColor = UiTheme.MutedInk
        banner.Controls.Add(_lastPoll)

        Dim readButton = UiTheme.Button("READ NOW / FC03", True)
        readButton.BackColor = UiTheme.Blue
        readButton.ForeColor = Color.White
        readButton.Size = New Size(142, 30)
        readButton.Enabled = canRead
        readButton.Location = New Point(Math.Max(0, banner.ClientSize.Width - readButton.Width - 52), 7)
        AddHandler readButton.Click, Async Sub(sender, e)
                                         readButton.Enabled = False
                                         Try
                                             Await _poll.Invoke()
                                         Finally
                                             readButton.Enabled = canRead
                                         End Try
                                     End Sub
        root.Controls.Add(readButton)
        Dim fullScreenButton = ViewFullScreen.CreateButton(Me)
        fullScreenButton.Top = 6
        root.Controls.Add(fullScreenButton)
        fullScreenButton.BringToFront()
        Dim columns As New TableLayoutPanel With {
            .Location = New Point(0, 50),
            .Size = New Size(Math.Max(600, root.ClientSize.Width - 4), 600),
            .ColumnCount = 5,
            .RowCount = 1,
            .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right,
            .BackColor = Color.Transparent,
            .Padding = Padding.Empty,
            .Margin = Padding.Empty
        }
        For index = 0 To 4
            columns.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 20.0F))
            Dim meter = CreateMeterColumn(index)
            _columns.Add(meter)
            columns.Controls.Add(meter.Panel, index, 0)
        Next
        columns.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        root.Controls.Add(columns)

        Dim panels As New TableLayoutPanel With {
            .Location = New Point(0, 662),
            .Size = New Size(Math.Max(600, root.ClientSize.Width - 4), 160),
            .ColumnCount = 2,
            .RowCount = 1,
            .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right,
            .BackColor = Color.Transparent,
            .Padding = Padding.Empty,
            .Margin = Padding.Empty
        }
        panels.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 55.0F))
        panels.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 45.0F))
        Dim mappingPanel = CreateTablePanel("MODBUS REGISTER MAPPING", 5)
        Dim mappingGrid = DirectCast(mappingPanel.Controls.OfType(Of DataGridView)().Single(), DataGridView)
        mappingGrid.Columns.Add("Transformer", "TRANSFORMER")
        mappingGrid.Columns.Add("Meter", "METER")
        mappingGrid.Columns.Add("Start", "MODBUS START BLOCK")
        mappingGrid.Columns.Add("Count", "NO. OF REGISTERS")
        mappingGrid.Columns.Add("Range", "REGISTER RANGE")
        SetColumnWeights(mappingGrid, {17.0F, 14.0F, 22.0F, 19.0F, 28.0F})
        For Each block In _settings.Blocks
            mappingGrid.Rows.Add(block.TransformerName, block.MeterModel,
                block.RegisterStart.ToString(CultureInfo.InvariantCulture),
                _settings.RegisterCount.ToString(CultureInfo.InvariantCulture),
                block.RegisterStart.ToString(CultureInfo.InvariantCulture) & "–" &
                (block.RegisterStart + _settings.RegisterCount - 1).ToString(CultureInfo.InvariantCulture))
        Next

        Dim qualityPanel = CreateTablePanel("DATA QUALITY", 4)
        _qualityGrid = DirectCast(qualityPanel.Controls.OfType(Of DataGridView)().Single(), DataGridView)
        _qualityGrid.Columns.Add("Transformer", "TRANSFORMER")
        _qualityGrid.Columns.Add("Communication", "COMMUNICATION STATUS")
        _qualityGrid.Columns.Add("Acquisition", "LAST READ / RTT")
        _qualityGrid.Columns.Add("Valid", "LAST VALID (UTC)")
        _qualityGrid.Columns.Add("Quality", "MEASUREMENT QUALITY")
        SetColumnWeights(_qualityGrid, {13.0F, 24.0F, 22.0F, 22.0F, 19.0F})
        For Each block In _settings.Blocks
            _qualityGrid.Rows.Add(block.TransformerName, "Not polled", "—", "—", "Not polled")
        Next
        panels.Controls.Add(mappingPanel, 0, 0)
        panels.Controls.Add(qualityPanel, 1, 0)
        root.Controls.Add(panels)

        Dim note = UiTheme.Label("Values are read-only FC03 floats from the PLC. Power factor = kW / kVA (kVA from phase V x I) and Energy Today = kW integrated over time (resets at midnight) are calculated, not meter registers. Total: currents summed, voltage/frequency/PF/temperature averaged, energy summed. TDEGC source is unverified.", New Font("Segoe UI", 7.5F), UiTheme.MutedInk, False)
        note.Location = New Point(2, 828)
        note.Size = New Size(Math.Max(0, root.ClientSize.Width - 8), 24)
        note.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        root.Controls.Add(note)

        AddHandler root.Resize, Sub(sender, e)
                                    banner.Width = Math.Max(600, root.ClientSize.Width - 4)
                                    columns.Width = Math.Max(600, root.ClientSize.Width - 4)
                                    panels.Width = Math.Max(600, root.ClientSize.Width - 4)
                                    note.Width = Math.Max(0, root.ClientSize.Width - 8)
                                    fullScreenButton.Left = Math.Max(0, banner.ClientSize.Width - fullScreenButton.Width - 6)
                                    readButton.Left = Math.Max(0, fullScreenButton.Left - readButton.Width - 8)
                                    _pollStatus.Left = Math.Max(0, readButton.Left - _pollStatus.Width - 10)
                                    _pollStatus.Top = 3
                                    _lastPoll.Left = _pollStatus.Left
                                    _lastPoll.Top = 23
                                End Sub
        SetReadings(snapshots)
        SetPlcStatus(status)
    End Sub

    Private Shared Function CreateTablePanel(title As String, columnCount As Integer) As ScadaPanel
        Dim panel As New ScadaPanel With {
            .Dock = DockStyle.Fill,
            .Margin = New Padding(0, 0, 4, 0),
            .BorderColor = UiTheme.Border,
            .Radius = 5,
            .Padding = Padding.Empty
        }
        Dim heading = UiTheme.Label(title, New Font("Segoe UI", 8.0F, FontStyle.Bold), UiTheme.Ink, False)
        heading.Location = New Point(10, 5)
        heading.Size = New Size(400, 18)
        heading.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        panel.Controls.Add(heading)
        Dim grid As New DataGridView With {
            .Location = New Point(7, 26),
            .Size = New Size(Math.Max(0, panel.ClientSize.Width - 14), 126),
            .BackgroundColor = TableBack,
            .BorderStyle = BorderStyle.None,
            .ReadOnly = True,
            .AllowUserToAddRows = False,
            .AllowUserToDeleteRows = False,
            .AllowUserToResizeRows = False,
            .AllowUserToResizeColumns = False,
            .RowHeadersVisible = False,
            .AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            .EnableHeadersVisualStyles = False,
            .SelectionMode = DataGridViewSelectionMode.CellSelect,
            .MultiSelect = False,
            .ScrollBars = ScrollBars.None,
            .TabStop = False
        }
        grid.GridColor = TableGridLine
        grid.CellBorderStyle = DataGridViewCellBorderStyle.Single
        grid.ColumnHeadersHeight = 24
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
        grid.ColumnHeadersDefaultCellStyle = New DataGridViewCellStyle With {
            .BackColor = TableHeaderBack,
            .ForeColor = TableHeaderInk,
            .Font = New Font("Segoe UI", 8.0F, FontStyle.Bold),
            .Alignment = DataGridViewContentAlignment.MiddleCenter,
            .SelectionBackColor = TableHeaderBack, .SelectionForeColor = TableHeaderInk
        }
        grid.DefaultCellStyle = New DataGridViewCellStyle With {
            .BackColor = TableBack,
            .ForeColor = UiTheme.Ink,
            .Font = New Font("Segoe UI", 8.5F),
            .Alignment = DataGridViewContentAlignment.MiddleCenter,
            .SelectionBackColor = TableBack,
            .SelectionForeColor = UiTheme.Ink
        }
        grid.AlternatingRowsDefaultCellStyle = New DataGridViewCellStyle With {.BackColor = TableAlternateBack, .SelectionBackColor = TableAlternateBack}
        grid.RowTemplate.Height = 23
        AddHandler panel.Resize, Sub(sender, e)
                                     grid.Width = Math.Max(0, panel.ClientSize.Width - 14)
                                     grid.Height = Math.Max(0, panel.ClientSize.Height - grid.Top - 8)
                                 End Sub
        panel.Controls.Add(grid)
        Return panel
    End Function

    Private Function CreateMeterColumn(index As Integer) As MeterColumnControls
        Dim isTotal = index = 4
        Dim panel As New ScadaPanel With {
            .Dock = DockStyle.Fill,
            .Margin = New Padding(3, 0, 3, 0),
            .BorderColor = UiTheme.Border,
            .Radius = 5,
            .Padding = Padding.Empty
        }
        Dim titleText = If(isTotal, "TOTAL LOAD", "Transformer " & (index + 1).ToString(CultureInfo.InvariantCulture))
        Dim title = UiTheme.Label(titleText, New Font("Segoe UI", 10.0F, FontStyle.Bold), UiTheme.BlueDark, False)
        title.Location = New Point(4, 3)
        title.Size = New Size(180, 18)
        title.TextAlign = ContentAlignment.MiddleCenter
        title.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        panel.Controls.Add(title)

        Dim status = UiTheme.Label("Not polled", New Font("Segoe UI", 7.0F, FontStyle.Bold), LiveAmber, False)
        status.Location = New Point(4, 37)
        status.Size = New Size(180, 14)
        status.TextAlign = ContentAlignment.MiddleCenter
        status.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        status.Visible = False
        panel.Controls.Add(status)

        Dim registerSubtitle = If(isTotal, "All Transformers",
            "Transformer " & (index + 1).ToString(CultureInfo.InvariantCulture) & "  •  FC03 " &
            _settings.Blocks(index).RegisterStart.ToString(CultureInfo.InvariantCulture) & "–" &
            (_settings.Blocks(index).RegisterStart + _settings.RegisterCount - 1).ToString(CultureInfo.InvariantCulture))
        Dim meterSubtitle = UiTheme.Label(registerSubtitle, New Font("Segoe UI", 7.0F), UiTheme.MutedInk, False)
        meterSubtitle.Location = New Point(4, 22)
        meterSubtitle.Size = New Size(180, 14)
        meterSubtitle.TextAlign = ContentAlignment.MiddleCenter
        meterSubtitle.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        panel.Controls.Add(meterSubtitle)

        Dim gaugeMaximum = If(isTotal, _settings.TotalMaxLoadKw, _settings.Blocks(index).MaxLoadKw)
        Dim gaugeLimits = If(isTotal, AlarmSettingsStore.Current.TotalLoad, AlarmSettingsStore.Current.Transformers)
        Dim gauge = New LoadGaugeControl With {
            .Location = New Point(4, 36),
            .Size = New Size(200, 146),
            .MaximumLoad = gaugeMaximum,
            .GreenZoneEndLoad = gaugeMaximum * gaugeLimits.LoadPreAlarmPercent / 100.0R,
            .RedZoneStartLoad = gaugeMaximum * gaugeLimits.LoadAlarmPercent / 100.0R,
            .ScaleValues = If(isTotal,
                New Double() {0.0R, 2000.0R, 4000.0R, 7000.0R, 10000.0R},
                New Double() {0.0R, 500.0R, 1000.0R, 1400.0R, 2000.0R, 2500.0R}),
            .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        }
        panel.Controls.Add(gauge)

        Dim value = UiTheme.Label("—", New Font("Segoe UI", 15.0F, FontStyle.Bold), UiTheme.Ink, False)
        value.Location = New Point(4, 186)
        value.Size = New Size(180, 30)
        value.TextAlign = ContentAlignment.MiddleCenter
        value.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        panel.Controls.Add(value)
        Dim maximum = If(isTotal, _settings.TotalMaxLoadKw, _settings.Blocks(index).MaxLoadKw)
        Dim maximumLabel = UiTheme.Label("MAX " & maximum.ToString("0.##", CultureInfo.InvariantCulture) & " kW", New Font("Segoe UI", 7.5F, FontStyle.Bold), UiTheme.MutedInk, False)
        maximumLabel.Location = New Point(4, 216)
        maximumLabel.Size = New Size(180, 18)
        maximumLabel.TextAlign = ContentAlignment.MiddleCenter
        maximumLabel.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        panel.Controls.Add(maximumLabel)

        Dim grid = CreateMeasurementTable(isTotal)
        Dim gridHeight = MeasurementHeaderHeight + TransformerMeasurementNames.Length * MeasurementRowHeight + 2
        grid.Location = New Point(2, 238)
        grid.Size = New Size(184, gridHeight)
        grid.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        panel.Controls.Add(grid)
        Dim update = UiTheme.Label("Last good: —", New Font("Segoe UI", 8.0F), UiTheme.MutedInk, False)
        update.Location = New Point(4, 238 + gridHeight + 4)
        update.Size = New Size(180, 22)
        update.TextAlign = ContentAlignment.MiddleCenter
        update.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        panel.Controls.Add(update)
        Dim openHandler As EventHandler = Sub(sender, e) OpenDetail(index)
        For Each clickable As Control In {title, meterSubtitle, gauge, value, maximumLabel}
            clickable.Cursor = Cursors.Hand
            AddHandler clickable.Click, openHandler
        Next
        _detailTip.SetToolTip(title, "Click to open history, alarms and trends")
        Return New MeterColumnControls With {.Panel = panel, .Gauge = gauge, .Value = value, .Status = status, .Maximum = maximumLabel, .Grid = grid, .LastGood = update}
    End Function

    Private Sub OpenDetail(index As Integer)
        Dim existing As TransformerDetailForm = Nothing
        If _openDetails.TryGetValue(index, existing) AndAlso Not existing.IsDisposed Then
            existing.Activate()
            Return
        End If
        Dim isTotal = index = 4
        Dim analyzerId = If(isTotal, "TOTAL", _settings.Blocks(index).TransformerName)
        Dim maxKw = If(isTotal, _settings.TotalMaxLoadKw, _settings.Blocks(index).MaxLoadKw)
        Dim column = _columns(index)
        Dim form As New TransformerDetailForm(If(isTotal, "TOTAL LOAD", "Transformer " & (index + 1).ToString(CultureInfo.InvariantCulture)), analyzerId, isTotal, maxKw, _historian,
            Function()
                Dim rows As New List(Of String())()
                For Each row As DataGridViewRow In column.Grid.Rows
                    rows.Add({Convert.ToString(row.Cells(0).Value, CultureInfo.InvariantCulture),
                              Convert.ToString(row.Cells(1).Value, CultureInfo.InvariantCulture),
                              Convert.ToString(row.Cells(2).Value, CultureInfo.InvariantCulture)})
                Next
                Return (CType(rows, IReadOnlyList(Of String())), column.Value.Text & "   •   " & column.LastGood.Text)
            End Function)
        AddHandler form.FormClosed, Sub(sender, e) _openDetails.Remove(index)
        _openDetails(index) = form
        form.Show(FindForm())
    End Sub

    Private Shared Function CreateMeasurementTable(isTotal As Boolean) As DataGridView
        Dim names = If(isTotal, TotalMeasurementNames, TransformerMeasurementNames)
        Dim units = If(isTotal, TotalMeasurementUnits, TransformerMeasurementUnits)
        Dim grid As New DataGridView With {
            .BackgroundColor = TableBack,
            .BorderStyle = BorderStyle.None,
            .ReadOnly = True,
            .AllowUserToAddRows = False,
            .AllowUserToDeleteRows = False,
            .AllowUserToResizeRows = False,
            .AllowUserToResizeColumns = False,
            .RowHeadersVisible = False,
            .ColumnHeadersVisible = True,
            .AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            .EnableHeadersVisualStyles = False,
            .SelectionMode = DataGridViewSelectionMode.CellSelect,
            .MultiSelect = False,
            .ScrollBars = ScrollBars.None,
            .TabStop = False
        }
        grid.ColumnHeadersHeight = MeasurementHeaderHeight
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
        grid.ColumnHeadersDefaultCellStyle = New DataGridViewCellStyle With {
            .BackColor = TableHeaderBack, .ForeColor = TableHeaderInk,
            .Font = New Font("Segoe UI", 9.0F, FontStyle.Bold), .Alignment = DataGridViewContentAlignment.MiddleCenter,
            .SelectionBackColor = TableHeaderBack, .SelectionForeColor = TableHeaderInk
        }
        grid.DefaultCellStyle = New DataGridViewCellStyle With {
            .BackColor = TableBack, .ForeColor = UiTheme.Ink,
            .SelectionBackColor = TableBack, .SelectionForeColor = UiTheme.Ink,
            .Font = New Font("Segoe UI", 9.5F), .Padding = New Padding(4, 0, 4, 0),
            .WrapMode = DataGridViewTriState.False
        }
        grid.AlternatingRowsDefaultCellStyle = New DataGridViewCellStyle With {
            .BackColor = TableAlternateBack, .SelectionBackColor = TableAlternateBack}
        grid.GridColor = TableGridLine
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal
        grid.RowTemplate.Height = MeasurementRowHeight
        grid.Columns.Add("Measurement", "PARAMETER")
        grid.Columns.Add("Value", "VALUE")
        grid.Columns.Add("Unit", "UNIT")
        grid.Columns(0).FillWeight = 47
        grid.Columns(1).FillWeight = 34
        grid.Columns(2).FillWeight = 19
        grid.Columns(0).DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft
        grid.Columns(1).DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter
        grid.Columns(2).DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter
        grid.Columns(0).MinimumWidth = 100
        grid.Columns(1).MinimumWidth = 44
        grid.Columns(2).MinimumWidth = 36
        grid.Columns(0).DefaultCellStyle.ForeColor = TableLabelInk
        grid.Columns(2).DefaultCellStyle.ForeColor = LiveGreen
        grid.Columns(2).DefaultCellStyle.Font = New Font("Segoe UI", 9.5F, FontStyle.Bold)
        grid.Columns(1).DefaultCellStyle.Font = New Font("Segoe UI", 11.0F, FontStyle.Bold)
        For row = 0 To names.Length - 1
            grid.Rows.Add(names(row), PlcModbus.MissingValue, units(row))
            grid.Rows(row).Cells(2).Style.ForeColor = LiveAmber
            grid.Rows(row).Cells(2).ToolTipText = If(isTotal, "Data quality: Not Mapped", "Data quality: Not polled")
        Next
        Return grid
    End Function

    Private Shared Sub SetColumnWeights(grid As DataGridView, weights As Single())
        For index = 0 To Math.Min(grid.Columns.Count, weights.Length) - 1
            grid.Columns(index).FillWeight = weights(index)
            grid.Columns(index).MinimumWidth = 32
        Next
    End Sub

    Public Sub SetReadings(snapshots As IReadOnlyList(Of MeterSnapshot))
        If snapshots Is Nothing Then snapshots = Array.Empty(Of MeterSnapshot)()
        For Each snapshot In snapshots
            If snapshot IsNot Nothing AndAlso snapshot.CapturedAtUtc.HasValue Then
                _lastAcquisitionUtc(snapshot.TransformerName) = snapshot.CapturedAtUtc.Value
                If snapshot.Quality = MeterQuality.Good Then
                    _lastSuccessfulUpdateUtc(snapshot.TransformerName) = snapshot.CapturedAtUtc.Value
                    If CountValidExpectedMeasurements(snapshot) = ExpectedTransformerRows.Length Then
                        _lastGoodSnapshots(snapshot.TransformerName) = snapshot
                        _lastValidUpdateUtc(snapshot.TransformerName) = snapshot.CapturedAtUtc.Value
                    End If
                End If
            End If
        Next
        For index = 0 To 3
            Dim meterName = _settings.Blocks(index).TransformerName
            Dim current = snapshots.FirstOrDefault(Function(item) item IsNot Nothing AndAlso
                String.Equals(item.TransformerName, meterName, StringComparison.OrdinalIgnoreCase))
            Dim stale = current Is Nothing OrElse current.Quality <> MeterQuality.Good
            Dim displaySnapshot = If(Not stale, current,
                If(_lastGoodSnapshots.ContainsKey(meterName), _lastGoodSnapshots(meterName), Nothing))
            Dim historicalValueShown = stale AndAlso displaySnapshot IsNot Nothing
            Dim lastGood = If(_lastSuccessfulUpdateUtc.ContainsKey(meterName),
                _lastSuccessfulUpdateUtc(meterName).ToString("HH:mm:ss 'UTC'", CultureInfo.InvariantCulture), "—")
            UpdateMeterColumn(_columns(index), current, displaySnapshot, historicalValueShown, _settings.Blocks(index).MaxLoadKw, lastGood,
                If(_energy IsNot Nothing, CType(_energy.GetKwh(index), Double?), Nothing))
            UpdateQualityRow(index, meterName, current, historicalValueShown)
        Next
        Dim currentTotal = LoadGaugeMath.CalculateTotalLoad(snapshots)
        If currentTotal.HasValue AndAlso snapshots.All(Function(snapshot) snapshot IsNot Nothing AndAlso snapshot.CapturedAtUtc.HasValue) Then
            _lastGoodTotalLoad = currentTotal
            _lastSuccessfulTotalUpdateUtc = snapshots.Min(Function(snapshot) snapshot.CapturedAtUtc.Value)
        End If
        Dim totalStale = Not currentTotal.HasValue AndAlso _lastGoodTotalLoad.HasValue
        Dim total = If(currentTotal.HasValue, currentTotal, If(totalStale, _lastGoodTotalLoad, Nothing))
        Dim totalMax = _settings.TotalMaxLoadKw
        Dim totalColumn = _columns(4)
        Dim totalLimits = AlarmSettingsStore.Current.TotalLoad
        totalColumn.Gauge.MaximumLoad = totalMax
        totalColumn.Maximum.Text = "MAX " & totalMax.ToString("0.##", CultureInfo.InvariantCulture) & " kW"
        Dim totalPercent = LoadGaugeMath.GetLoadPercent(total, totalMax)
        totalColumn.Gauge.LoadPercent = totalPercent
        totalColumn.Value.Text = If(total.HasValue, total.Value.ToString("0.0", CultureInfo.InvariantCulture) & " kW", PlcModbus.MissingValue)
        totalColumn.LastGood.Text = If(totalStale, "STALE • LAST GOOD: ", "LAST GOOD: ") & If(_lastSuccessfulTotalUpdateUtc.HasValue,
            _lastSuccessfulTotalUpdateUtc.Value.ToString("HH:mm:ss 'UTC'", CultureInfo.InvariantCulture), "—")
        Dim liveTotals = ComputeTotals(snapshots)
        If liveTotals IsNot Nothing Then _lastTotals = liveTotals
        Dim totals = If(liveTotals, _lastTotals)
        Dim liveVoltages As Double?() = If(liveTotals Is Nothing, Array.Empty(Of Double?)(), {liveTotals(2), liveTotals(3), liveTotals(4)})
        Dim liveTemperature = If(liveTotals Is Nothing, Nothing, liveTotals(11))
        Dim totalCondition = AlarmEvaluator.Evaluate(totalPercent, liveTemperature, liveVoltages, totalLimits)
        ApplyAlarmState(totalColumn, totalCondition, totalLimits, totalMax, total.HasValue, totalStale,
                        TotalQuality(snapshots), LoadSeverity(totalPercent, totalLimits, totalStale))
        If snapshots.Count = 4 AndAlso snapshots.All(Function(item) item IsNot Nothing AndAlso item.EcgOffConfirmed) Then
            ApplyPowerOff(totalColumn, "ECG OFF", "ALL FOUR TRANSFORMERS OFF")
        ElseIf snapshots.Count = 4 AndAlso snapshots.All(Function(item) item IsNot Nothing AndAlso item.EcgBackConfirmed) AndAlso
               total.HasValue AndAlso Not totalStale AndAlso totalCondition.Severity = AlarmSeverity.Normal Then
            ApplyEcgBack(totalColumn)
        End If
        For row = 0 To TotalMeasurementNames.Length - 1
            Dim rowValue = PlcModbus.MissingValue
            Dim rowQuality = "Not Mapped"
            If row = 0 AndAlso total.HasValue Then
                rowValue = total.Value.ToString("0.0", CultureInfo.InvariantCulture)
                rowQuality = If(totalStale, "Stale", "Valid")
            ElseIf row = 1 AndAlso total.HasValue Then
                Dim percent = LoadGaugeMath.GetLoadPercent(total, totalMax)
                rowValue = If(percent.HasValue, percent.Value.ToString("0.0", CultureInfo.InvariantCulture), PlcModbus.MissingValue)
                rowQuality = If(totalStale, "Stale", If(percent.HasValue, "Valid", "Invalid"))
            ElseIf row = RowEnergyToday Then
                If _energy IsNot Nothing Then
                    rowValue = _energy.GetTotalKwh().ToString(MeasurementFormats(row), CultureInfo.InvariantCulture)
                    rowQuality = "Valid"
                Else
                    rowQuality = "Not polled"
                End If
            ElseIf row >= 2 AndAlso totals IsNot Nothing AndAlso totals(row).HasValue Then
                rowValue = totals(row).Value.ToString(MeasurementFormats(row), CultureInfo.InvariantCulture)
                rowQuality = If(liveTotals Is Nothing, "Stale", "Valid")
            ElseIf Not total.HasValue Then
                rowQuality = TotalQuality(snapshots)
            End If
            Dim severity = AlarmSeverity.Normal
            Select Case row
                Case 0, 1
                    If Not totalStale Then severity = AlarmEvaluator.LoadSeverity(totalPercent, totalLimits)
                Case 2, 3, 4
                    If liveTotals IsNot Nothing AndAlso snapshots IsNot Nothing AndAlso
                       snapshots.All(Function(item) item IsNot Nothing AndAlso item.PowerState = TransformerPowerState.On) Then
                        severity = AlarmEvaluator.VoltageSeverity(liveTotals(row), totalLimits)
                    End If
                Case RowTemperature
                    severity = AlarmEvaluator.TemperatureSeverity(liveTemperature, totalLimits)
            End Select
            SetGridRow(totalColumn.Grid, row, rowValue, TotalMeasurementUnits(row), rowQuality,
                       If(row = RowTemperature AndAlso rowValue <> PlcModbus.MissingValue, "SOURCE UNVERIFIED", String.Empty), severity)
        Next
    End Sub

    Private Sub UpdateQualityRow(rowIndex As Integer, transformerName As String, snapshot As MeterSnapshot, stale As Boolean)
        Dim communication = If(snapshot Is Nothing, "Not polled",
            If(snapshot.Quality = MeterQuality.Good, "Online (FC03)",
               If(snapshot.Quality = MeterQuality.Timeout, "Timeout",
                  If(snapshot.Quality = MeterQuality.Offline, "Offline",
                     If(snapshot.Quality = MeterQuality.MappingError, "Mapping error",
                        If(snapshot.Quality = MeterQuality.Connecting, "Connecting", "Bad quality"))))))
        Dim quality = MeasurementQuality(snapshot, stale)
        Dim lastAcquisition = If(_lastAcquisitionUtc.ContainsKey(transformerName),
            _lastAcquisitionUtc(transformerName).ToString("HH:mm:ss.f 'UTC'", CultureInfo.InvariantCulture), "—")
        If snapshot IsNot Nothing AndAlso snapshot.ResponseLatencyMilliseconds.HasValue Then
            lastAcquisition &= " / " & snapshot.ResponseLatencyMilliseconds.Value.ToString("0.0", CultureInfo.InvariantCulture) & " ms"
        End If
        Dim lastValid = If(_lastValidUpdateUtc.ContainsKey(transformerName),
            _lastValidUpdateUtc(transformerName).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), "—")
        _qualityGrid.Rows(rowIndex).Cells(1).Value = communication
        _qualityGrid.Rows(rowIndex).Cells(2).Value = lastAcquisition
        _qualityGrid.Rows(rowIndex).Cells(3).Value = lastValid
        _qualityGrid.Rows(rowIndex).Cells(4).Value = quality
        _qualityGrid.Rows(rowIndex).Cells(1).Style.ForeColor = If(snapshot IsNot Nothing AndAlso snapshot.Quality = MeterQuality.Good, LiveGreen, LiveAmber)
        _qualityGrid.Rows(rowIndex).Cells(4).Style.ForeColor = If(quality.StartsWith("Complete", StringComparison.Ordinal), LiveGreen,
            If(quality.StartsWith("Stale", StringComparison.Ordinal), LiveAmber, LiveRed))
    End Sub

    Private Shared Sub UpdateMeterColumn(column As MeterColumnControls, current As MeterSnapshot, displaySnapshot As MeterSnapshot,
                                        stale As Boolean, maximum As Double, lastGood As String,
                                        energyToday As Double?)
        Dim limits = AlarmSettingsStore.Current.Transformers
        column.Gauge.MaximumLoad = maximum
        column.Maximum.Text = "MAX " & maximum.ToString("0.##", CultureInfo.InvariantCulture) & " kW"
        Dim load = GetMeasurementNumericValue(displaySnapshot, 10)
        Dim loadPercent = LoadGaugeMath.GetLoadPercent(load, maximum)
        column.Gauge.LoadPercent = loadPercent
        column.Value.Text = If(load.HasValue, load.Value.ToString("0.0", CultureInfo.InvariantCulture) & " kW", PlcModbus.MissingValue)
        Dim voltages = {GetMeasurementNumericValue(displaySnapshot, 0), GetMeasurementNumericValue(displaySnapshot, 1), GetMeasurementNumericValue(displaySnapshot, 2)}
        If current IsNot Nothing AndAlso current.PowerState = TransformerPowerState.Off Then voltages = New Double?() {Nothing, Nothing, Nothing}
        Dim temperature = GetMeasurementNumericValue(displaySnapshot, 15)
        Dim condition = AlarmEvaluator.Evaluate(loadPercent, temperature, voltages, limits)
        ApplyAlarmState(column, condition, limits, maximum, load.HasValue, stale, QualityText(current), LoadSeverity(loadPercent, limits, stale))
        If current IsNot Nothing AndAlso current.PowerState = TransformerPowerState.Off Then
            ApplyPowerOff(column, "TRANSFORMER OFF", current.PowerDetail)
        End If
        column.LastGood.Text = If(stale, "STALE • LAST GOOD: ", "LAST GOOD: ") & lastGood

        For row = 0 To TransformerMeasurementNames.Length - 1
            Dim reading = If(row = RowEnergyToday,
                If(energyToday.HasValue, energyToday.Value.ToString(MeasurementFormats(row), CultureInfo.InvariantCulture), PlcModbus.MissingValue),
                GetMeasurementValue(displaySnapshot, row, maximum))
            Dim quality = TransformerRowQuality(current, displaySnapshot, row, stale, reading)
            Dim temperatureStatus = If(row = RowTemperature, "SOURCE UNVERIFIED", String.Empty)
            Dim severity = AlarmSeverity.Normal
            If Not stale Then
                Select Case row
                    Case 0, 1
                        severity = AlarmEvaluator.LoadSeverity(loadPercent, limits)
                    Case 2, 3, 4
                        severity = AlarmEvaluator.VoltageSeverity(voltages(row - 2), limits)
                    Case RowTemperature
                        severity = AlarmEvaluator.TemperatureSeverity(temperature, limits)
                End Select
            End If
            SetGridRow(column.Grid, row, reading, TransformerMeasurementUnits(row), quality, temperatureStatus, severity)
        Next
    End Sub

    Private Shared Sub ApplyPowerOff(column As MeterColumnControls, title As String, detail As String)
        column.Status.Text = title & " - " & detail
        column.Status.ForeColor = AlarmRed
        column.Gauge.IndicatorText = title
        column.Gauge.IndicatorDetail = String.Empty
        column.Gauge.IndicatorColor = AlarmRed
        column.Gauge.IndicatorStyle = 2
        Dim panel = TryCast(column.Panel, ScadaPanel)
        If panel IsNot Nothing Then panel.BorderColor = AlarmRed
    End Sub

    Private Shared Sub ApplyEcgBack(column As MeterColumnControls)
        column.Status.Text = "ECG BACK - ALL FOUR TRANSFORMERS HAVE VOLTAGE"
        column.Status.ForeColor = LiveGreen
        column.Gauge.IndicatorText = "ECG BACK"
        column.Gauge.IndicatorDetail = "POWER RESTORED"
        column.Gauge.IndicatorColor = LiveGreen
        column.Gauge.IndicatorStyle = 0
        Dim panel = TryCast(column.Panel, ScadaPanel)
        If panel IsNot Nothing Then panel.BorderColor = LiveGreen
    End Sub

    Private Shared Function LoadSeverity(percent As Double?, limits As EquipmentAlarmLimits, stale As Boolean) As AlarmSeverity
        Return If(stale, AlarmSeverity.Normal, AlarmEvaluator.LoadSeverity(percent, limits))
    End Function

    ' Shows the alarm state in place of the old GREEN/YELLOW/RED zone word: badge in the gauge, panel border and kW colour.
    Private Shared Sub ApplyAlarmState(column As MeterColumnControls, condition As AlarmCondition, limits As EquipmentAlarmLimits,
                                       maximum As Double, hasValue As Boolean, stale As Boolean, noDataText As String,
                                       loadSeverity As AlarmSeverity)
        column.Gauge.GreenZoneEndLoad = maximum * limits.LoadPreAlarmPercent / 100.0R
        column.Gauge.RedZoneStartLoad = maximum * limits.LoadAlarmPercent / 100.0R

        Dim text As String
        Dim detail As String = String.Empty
        Dim color As Color
        Dim style As Integer
        If Not hasValue Then
            text = noDataText.ToUpperInvariant()
            detail = "NO VALID DATA"
            color = LiveAmber
            style = 1
        ElseIf stale Then
            text = "STALE DATA"
            detail = "LAST GOOD VALUE SHOWN"
            color = LiveAmber
            style = 1
        Else
            text = condition.Title
            detail = condition.Detail
            Select Case condition.Severity
                Case AlarmSeverity.Normal
                    color = LiveGreen
                    style = 0
                Case AlarmSeverity.PreAlarm
                    color = LiveAmber
                    style = 1
                Case Else
                    color = AlarmRed
                    style = 2
            End Select
        End If
        column.Status.Text = If(String.IsNullOrEmpty(detail), text, text & " · " & detail)
        column.Status.ForeColor = color
        column.Gauge.IndicatorText = text
        column.Gauge.IndicatorDetail = detail
        column.Gauge.IndicatorColor = color
        column.Gauge.IndicatorStyle = style

        Dim border = If(Not hasValue OrElse stale OrElse condition.Severity = AlarmSeverity.Normal, CardAccentBlue,
                        If(condition.Severity = AlarmSeverity.PreAlarm, LiveAmber, AlarmRed))
        Dim panel = TryCast(column.Panel, ScadaPanel)
        If panel IsNot Nothing AndAlso panel.BorderColor <> border Then panel.BorderColor = border
        column.Value.ForeColor = If(loadSeverity = AlarmSeverity.Normal, UiTheme.Ink,
                                    If(loadSeverity = AlarmSeverity.PreAlarm, LiveAmber, LiveRed))
    End Sub

    Private Shared Function TransformerRowQuality(current As MeterSnapshot, displaySnapshot As MeterSnapshot,
                                                  row As Integer, stale As Boolean, value As String) As String
        If row = RowEnergyToday Then Return If(value = PlcModbus.MissingValue, "Not polled", "Valid")
        If TransformerMeasurementIndexes(row) = -1 Then Return "Not Mapped"
        If stale Then Return If(displaySnapshot Is Nothing, QualityText(current), "Stale")
        If current Is Nothing Then Return "Not polled"
        If current.Quality <> MeterQuality.Good Then Return QualityText(current)
        If value = PlcModbus.MissingValue AndAlso TransformerMeasurementIndexes(row) = 8 AndAlso
           current.DecodedValues IsNot Nothing AndAlso PlcModbus.IsUnavailableOffFrequency(current.DecodedValues, 8) Then
            Return "Unavailable (no voltage)"
        End If
        Return If(value <> PlcModbus.MissingValue, "Valid", "Invalid")
    End Function

    Private Shared Function GetMeasurementValue(snapshot As MeterSnapshot, row As Integer, maximum As Double) As String
        If row = RowEnergyToday Then Return PlcModbus.MissingValue
        Dim value As Double? = Nothing
        If row = RowPowerFactor Then
            value = CalculatePowerFactor(snapshot)
        ElseIf row = 1 Then
            value = LoadGaugeMath.GetLoadPercent(GetMeasurementNumericValue(snapshot, 10), maximum)
        Else
            value = GetMeasurementNumericValue(snapshot, TransformerMeasurementIndexes(row))
        End If
        Return If(value.HasValue, value.Value.ToString(MeasurementFormats(row), CultureInfo.InvariantCulture), PlcModbus.MissingValue)
    End Function

    Private Shared Function ApparentPowerKva(snapshot As MeterSnapshot) As Double?
        If snapshot Is Nothing OrElse snapshot.Quality <> MeterQuality.Good Then Return Nothing
        Return ElectricalCalculations.ApparentPowerKva(snapshot.DecodedValues)
    End Function

    ' The PLC does not expose power factor, so it is calculated as kW / kVA with kVA = sum of phase V x I.
    Private Shared Function CalculatePowerFactor(snapshot As MeterSnapshot) As Double?
        Return ElectricalCalculations.PowerFactor(GetMeasurementNumericValue(snapshot, 10), ApparentPowerKva(snapshot))
    End Function

    Private Shared Function ComputeTotals(snapshots As IReadOnlyList(Of MeterSnapshot)) As Double?()
        If snapshots Is Nothing OrElse snapshots.Count < 4 Then Return Nothing
        Dim good = snapshots.Take(4).ToArray()
        If good.Any(Function(s) s Is Nothing OrElse s.Quality <> MeterQuality.Good) Then Return Nothing
        Dim result(11) As Double?
        For phase = 0 To 2
            Dim p = phase
            Dim currents = good.Select(Function(s) GetMeasurementNumericValue(s, 11 + p)).ToArray()
            If currents.All(Function(v) v.HasValue) Then result(5 + p) = currents.Sum(Function(v) v.Value)
            Dim voltages = good.Select(Function(s) GetMeasurementNumericValue(s, p)).ToArray()
            If voltages.All(Function(v) v.HasValue) Then result(2 + p) = voltages.Average(Function(v) v.Value)
        Next
        Dim frequencies = good.Select(Function(s) GetMeasurementNumericValue(s, 8)).ToArray()
        If frequencies.All(Function(v) v.HasValue) Then result(8) = frequencies.Average(Function(v) v.Value)
        Dim kw = good.Select(Function(s) GetMeasurementNumericValue(s, 10)).ToArray()
        Dim kva = good.Select(Function(s) ApparentPowerKva(s)).ToArray()
        If kw.All(Function(v) v.HasValue) AndAlso kva.All(Function(v) v.HasValue) AndAlso kva.Sum(Function(v) v.Value) > 0.001R Then
            result(9) = Math.Clamp(kw.Sum(Function(v) v.Value) / kva.Sum(Function(v) v.Value), 0.0R, 1.0R)
        End If
        Dim temperatures = good.Select(Function(s) GetMeasurementNumericValue(s, 15)).ToArray()
        If temperatures.All(Function(v) v.HasValue) Then result(11) = temperatures.Average(Function(v) v.Value)
        Return result
    End Function

    Private Shared Function GetMeasurementNumericValue(snapshot As MeterSnapshot, index As Integer) As Double?
        If snapshot Is Nothing OrElse snapshot.Quality <> MeterQuality.Good OrElse
            snapshot.DecodedValues Is Nothing OrElse index < 0 OrElse index >= snapshot.DecodedValues.Length Then Return Nothing
        Dim value = CDbl(snapshot.DecodedValues(index))
        Return If(Double.IsFinite(value), CType(value, Double?), Nothing)
    End Function

    Private Shared Sub SetGridRow(grid As DataGridView, row As Integer, value As String, unit As String,
                                  quality As String, Optional temperatureStatus As String = "",
                                  Optional severity As AlarmSeverity = AlarmSeverity.Normal)
        Dim unitCell = grid.Rows(row).Cells(2)
        If Not Equals(grid.Rows(row).Cells(1).Value, value) Then grid.Rows(row).Cells(1).Value = value
        If Not Equals(unitCell.Value, unit) Then unitCell.Value = unit
        Dim qualityColor As Color = If(String.Equals(quality, "Valid", StringComparison.Ordinal), LiveGreen,
            If(String.Equals(quality, "Not Mapped", StringComparison.Ordinal), NotMappedInk,
               If(String.Equals(quality, "Stale", StringComparison.Ordinal), LiveAmber, LiveRed)))
        Dim hasValue = value <> PlcModbus.MissingValue
        If hasValue AndAlso severity >= AlarmSeverity.Alarm Then
            qualityColor = LiveRed
        ElseIf hasValue AndAlso severity = AlarmSeverity.PreAlarm Then
            qualityColor = LiveAmber
        ElseIf String.Equals(temperatureStatus, "SOURCE UNVERIFIED", StringComparison.Ordinal) AndAlso
               Not String.Equals(quality, "Stale", StringComparison.Ordinal) Then
            qualityColor = UiTheme.Blue
        End If
        If unitCell.Style.ForeColor <> qualityColor Then unitCell.Style.ForeColor = qualityColor
        Dim valueCell = grid.Rows(row).Cells(1)
        If valueCell.Style.ForeColor <> qualityColor Then valueCell.Style.ForeColor = qualityColor
        Dim alarmNote = If(Not hasValue OrElse severity = AlarmSeverity.Normal, String.Empty,
            " | Alarm: " & If(severity = AlarmSeverity.PreAlarm, "pre-alarm (yellow)",
                              If(severity = AlarmSeverity.ShedLoad, "shed-load limit reached", "alarm limit exceeded")) &
            " — limits are set in Configuration › Alarm Limits")
        unitCell.ToolTipText = "Measurement quality: " & quality &
            If(String.IsNullOrEmpty(temperatureStatus), String.Empty, " | Temperature source: unverified PLC register") & alarmNote
    End Sub

    Private Shared Function CountValidExpectedMeasurements(snapshot As MeterSnapshot) As Integer
        If snapshot Is Nothing OrElse snapshot.Quality <> MeterQuality.Good Then Return 0
        Dim count = 0
        For Each row In ExpectedTransformerRows
            Dim valid = If(row = 1,
                LoadGaugeMath.GetLoadPercent(GetMeasurementNumericValue(snapshot, 10), 2500.0R).HasValue,
                GetMeasurementNumericValue(snapshot, TransformerMeasurementIndexes(row)).HasValue)
            If valid Then count += 1
        Next
        Return count
    End Function

    Private Shared Function MeasurementQuality(snapshot As MeterSnapshot, stale As Boolean) As String
        If stale Then Return "Stale — last-good values shown"
        If snapshot Is Nothing Then Return "Not polled"
        If snapshot.Quality <> MeterQuality.Good Then Return QualityText(snapshot)
        Dim valid = CountValidExpectedMeasurements(snapshot)
        Return If(valid = ExpectedTransformerRows.Length,
            $"Complete ({valid}/{ExpectedTransformerRows.Length} expected)", $"Partial ({valid}/{ExpectedTransformerRows.Length} expected)")
    End Function

    Private Shared Function ClampPercent(value As Double) As Double
        If Double.IsNaN(value) Then Return 0.0R
        If Double.IsPositiveInfinity(value) Then Return 100.0R
        If Double.IsNegativeInfinity(value) Then Return 0.0R
        Return Math.Clamp(value, 0.0R, 100.0R)
    End Function

    Public Sub SetPlcStatus(status As PlcConnectionStatus)
        If status Is Nothing Then Return
        _pollStatus.Text = UiTheme.StatusText(status)
        _pollStatus.ForeColor = UiTheme.StatusColor(status)
        _lastPoll.Text = "Poll " & If(status.LastPollUtc.HasValue, status.LastPollUtc.Value.ToString("HH:mm:ss 'UTC'", CultureInfo.InvariantCulture), "—") &
                         "  •  " & FormatInterval(status.RequestedIntervalMilliseconds, status.PollingIntervalSeconds)
    End Sub

    Private Shared Function FormatInterval(milliseconds As Integer, fallbackSeconds As Integer) As String
        Dim ms = If(milliseconds > 0, milliseconds, fallbackSeconds * 1000)
        Return If(ms < 1000, ms.ToString(CultureInfo.InvariantCulture) & " ms", (ms / 1000.0R).ToString("0.##", CultureInfo.InvariantCulture) & " s")
    End Function

    Private Shared Function QualityText(snapshot As MeterSnapshot) As String
        If snapshot Is Nothing Then Return "Not polled"
        Select Case snapshot.Quality
            Case MeterQuality.Good
                Return "Good"
            Case MeterQuality.Connecting
                Return "Connecting"
            Case MeterQuality.Timeout
                Return "Timeout"
            Case MeterQuality.Offline
                Return "Offline"
            Case MeterQuality.BadQuality
                Return "Bad quality"
            Case MeterQuality.MappingError
                Return "Mapping error"
            Case Else
                Return "Not polled"
        End Select
    End Function

    Private Shared Function TotalQuality(snapshots As IReadOnlyList(Of MeterSnapshot)) As String
        If snapshots Is Nothing OrElse snapshots.Count = 0 Then Return "Not polled"
        If snapshots.Any(Function(snapshot) snapshot Is Nothing OrElse snapshot.Quality <> MeterQuality.Good) Then
            Return String.Join("/", snapshots.Where(Function(snapshot) snapshot Is Nothing OrElse snapshot.Quality <> MeterQuality.Good).
                Select(Function(snapshot) QualityText(snapshot)).Distinct())
        End If
        Return "Bad data"
    End Function

    Private NotInheritable Class MeterColumnControls
        Public Property Panel As Control
        Public Property Gauge As LoadGaugeControl
        Public Property Value As Label
        Public Property Status As Label
        Public Property Maximum As Label
        Public Property Grid As DataGridView
        Public Property LastGood As Label
    End Class
End Class
