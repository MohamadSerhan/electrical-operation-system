Imports System.Drawing
Imports System.Globalization
Imports System.Linq
Imports System.Windows.Forms

Public NotInheritable Class DashboardView
    Inherits UserControl

    Private ReadOnly _plc As PlcSettings
    Private ReadOnly _openTransformers As Action
    Private ReadOnly _openAnalytics As Action
    Private ReadOnly _openDiagnostics As Action
    Private ReadOnly _openHistorian As Action
    Private ReadOnly _historian As MeasurementHistorian
    Private ReadOnly _scheduler As DailyHistorianExportScheduler
    Private ReadOnly _backupService As BackupRecoveryService
    Private ReadOnly _upsService As UpsAcquisitionService
    Private ReadOnly _plcState As New Label()
    Private ReadOnly _plcDetail As New Label()
    Private ReadOnly _validBlocksValue As New Label()
    Private ReadOnly _totalPowerValue As New Label()
    Private ReadOnly _lastReadValue As New Label()
    Private ReadOnly _snmpUnitsValue As New Label()
    Private ReadOnly _historianSummary As New Label()
    Private ReadOnly _exportSummary As New Label()
    Private ReadOnly _backupSummary As New Label()
    Private ReadOnly _readinessSummary As New Label()

    Public Sub New(plc As PlcSettings,
                   status As PlcConnectionStatus,
                   openTransformers As Action,
                   historian As MeasurementHistorian,
                   scheduler As DailyHistorianExportScheduler,
                   Optional openAnalytics As Action = Nothing,
                   Optional openDiagnostics As Action = Nothing,
                   Optional openHistorian As Action = Nothing,
                   Optional backupService As BackupRecoveryService = Nothing,
                   Optional upsService As UpsAcquisitionService = Nothing)
        If plc Is Nothing Then Throw New ArgumentNullException(NameOf(plc))
        If historian Is Nothing Then Throw New ArgumentNullException(NameOf(historian))
        If scheduler Is Nothing Then Throw New ArgumentNullException(NameOf(scheduler))
        _plc = plc
        _openTransformers = openTransformers
        _openAnalytics = openAnalytics
        _openDiagnostics = openDiagnostics
        _openHistorian = openHistorian
        _historian = historian
        _scheduler = scheduler
        _backupService = backupService
        _upsService = upsService
        BackColor = UiTheme.Canvas
        BuildLayout()
        SetPlcStatus(status)
        SetSnmpStatus(_upsService)
    End Sub

    Private Sub BuildLayout()
        Dim root As New Panel With {.Dock = DockStyle.Fill, .AutoScroll = True, .Padding = New Padding(0, 0, 6, 0), .BackColor = UiTheme.Canvas}
        Controls.Add(root)

        Dim quickActions As New TableLayoutPanel With {
            .Location = New Point(0, 0),
            .Size = New Size(Math.Max(0, root.ClientSize.Width - 8), 36),
            .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right,
            .ColumnCount = 4,
            .RowCount = 1,
            .BackColor = Color.Transparent
        }
        For index = 0 To 3
            quickActions.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 25.0F))
        Next
        quickActions.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        Dim quickButtons = {
            AddQuickAction(quickActions, "Open transformers", _openTransformers, 0, True),
            AddQuickAction(quickActions, "Open online analyzer", _openAnalytics, 1),
            AddQuickAction(quickActions, "Open diagnostics", _openDiagnostics, 2),
            AddQuickAction(quickActions, "Open historian", _openHistorian, 3)
        }
        root.Controls.Add(quickActions)

        Dim metrics As New TableLayoutPanel With {
            .Location = New Point(0, 47),
            .Size = New Size(Math.Max(0, root.ClientSize.Width - 8), 112),
            .RowCount = 1,
            .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        }
        Dim metricCards = {
            MetricCard("PLC COMMUNICATION", _plcState, "FC03 connection status", UiTheme.Blue),
            MetricCard("VALID METER BLOCKS", _validBlocksValue, "Current acquisition cycle", UiTheme.Green),
            MetricCard("TOTAL ACTIVE POWER", _totalPowerValue, "PTOT only · valid cycle required", UiTheme.Cyan),
            MetricCard("LAST HOST READ", _lastReadValue, "Not a meter source timestamp", UiTheme.Amber),
            MetricCard("SNMP UPS UNITS", _snmpUnitsValue, "Good / enabled · read-only", UiTheme.Cyan)
        }
        metrics.ColumnCount = metricCards.Length
        For index = 0 To metricCards.Length - 1
            metrics.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F / metricCards.Length))
        Next
        metrics.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        For index = 0 To metricCards.Length - 1
            metrics.Controls.Add(metricCards(index), index, 0)
        Next
        root.Controls.Add(metrics)

        Dim healthCard As New RoundedPanel With {
            .Location = New Point(0, 175),
            .Size = New Size(540, 205),
            .BackColor = UiTheme.Surface,
            .Anchor = AnchorStyles.Top Or AnchorStyles.Left
        }
        AddCardHeading(healthCard, "Communication & completeness", 16, 15)
        _plcDetail.Location = New Point(16, 44)
        _plcDetail.Size = New Size(500, 44)
        _plcDetail.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        _plcDetail.Font = New Font("Segoe UI", 9.0F)
        _plcDetail.ForeColor = UiTheme.MutedInk
        _plcDetail.AutoEllipsis = True
        healthCard.Controls.Add(_plcDetail)
        AddInfoRow(healthCard, "Configured PLC", _plc.Model & " · Modbus TCP · FC03", 96, 502)
        AddInfoRow(healthCard, "Read endpoint", _plc.Host & ":" & _plc.Port.ToString(CultureInfo.InvariantCulture), 126, 502)
        AddInfoRow(healthCard, "Meter source freshness", "Unverified · no PLC timestamp/sequence", 156, 502, UiTheme.Amber)
        root.Controls.Add(healthCard)

        Dim historyCard As New RoundedPanel With {
            .Location = New Point(556, 175),
            .Size = New Size(520, 205),
            .BackColor = UiTheme.Surface,
            .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        }
        AddCardHeading(historyCard, "Historian, exports & backup", 16, 15)
        _historianSummary.Location = New Point(16, 48)
        _historianSummary.Size = New Size(480, 44)
        _historianSummary.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        _historianSummary.Font = New Font("Segoe UI", 8.5F)
        _historianSummary.ForeColor = UiTheme.Ink
        historyCard.Controls.Add(_historianSummary)
        _exportSummary.Location = New Point(16, 98)
        _exportSummary.Size = New Size(480, 48)
        _exportSummary.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        _exportSummary.Font = New Font("Segoe UI", 8.5F)
        _exportSummary.ForeColor = UiTheme.MutedInk
        _exportSummary.AutoEllipsis = True
        historyCard.Controls.Add(_exportSummary)
        _backupSummary.Location = New Point(16, 151)
        _backupSummary.Size = New Size(480, 36)
        _backupSummary.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        _backupSummary.Font = New Font("Segoe UI", 8.5F, FontStyle.Bold)
        _backupSummary.ForeColor = UiTheme.MutedInk
        _backupSummary.AutoEllipsis = True
        historyCard.Controls.Add(_backupSummary)
        root.Controls.Add(historyCard)

        Dim eventsCard As New RoundedPanel With {
            .Location = New Point(0, 396),
            .Size = New Size(540, 118),
            .BackColor = UiTheme.Surface,
            .Anchor = AnchorStyles.Top Or AnchorStyles.Left
        }
        AddCardHeading(eventsCard, "Alarms & events", 16, 15)
        Dim eventsNote = UiTheme.Label("No alarm/event center is configured. This page will not show fabricated electrical alarms.", New Font("Segoe UI", 9.0F), UiTheme.MutedInk, False)
        eventsNote.Location = New Point(16, 48)
        eventsNote.Size = New Size(500, 42)
        eventsNote.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        eventsCard.Controls.Add(eventsNote)
        root.Controls.Add(eventsCard)

        Dim readinessCard As New RoundedPanel With {
            .Location = New Point(556, 396),
            .Size = New Size(520, 118),
            .BackColor = UiTheme.Surface,
            .Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        }
        AddCardHeading(readinessCard, "Operator readiness", 16, 15)
        _readinessSummary.Location = New Point(16, 47)
        _readinessSummary.Size = New Size(480, 55)
        _readinessSummary.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        _readinessSummary.Font = New Font("Segoe UI", 8.5F)
        _readinessSummary.ForeColor = UiTheme.MutedInk
        readinessCard.Controls.Add(_readinessSummary)
        root.Controls.Add(readinessCard)

        AddHandler root.Resize, Sub(sender, e)
                                    Dim contentWidth = Math.Max(0, root.ClientSize.Width - 8)
                                    Dim compact = contentWidth < 760
                                    quickActions.Width = contentWidth
                                    metrics.Width = contentWidth
                                    If compact Then
                                        quickActions.ColumnCount = 2
                                        quickActions.RowCount = 2
                                        quickActions.ColumnStyles.Clear()
                                        quickActions.RowStyles.Clear()
                                        quickActions.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50.0F))
                                        quickActions.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50.0F))
                                        quickActions.RowStyles.Add(New RowStyle(SizeType.Percent, 50.0F))
                                        quickActions.RowStyles.Add(New RowStyle(SizeType.Percent, 50.0F))
                                        Dim metricColumns = 2
                                        metrics.ColumnCount = metricColumns
                                        metrics.RowCount = CInt(Math.Ceiling(metricCards.Length / CDbl(metricColumns)))
                                        metrics.ColumnStyles.Clear()
                                        metrics.RowStyles.Clear()
                                        For index = 0 To metricColumns - 1
                                            metrics.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F / metricColumns))
                                        Next
                                        For index = 0 To metrics.RowCount - 1
                                            metrics.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F / metrics.RowCount))
                                        Next
                                        For index = 0 To quickButtons.Length - 1
                                            quickActions.SetCellPosition(quickButtons(index),
                                                New TableLayoutPanelCellPosition(index Mod 2, index \ 2))
                                        Next
                                        For index = 0 To metricCards.Length - 1
                                            metrics.SetCellPosition(metricCards(index),
                                                New TableLayoutPanelCellPosition(index Mod metricColumns, index \ metricColumns))
                                        Next
                                        quickActions.Height = 68
                                        metrics.Location = New Point(0, 83)
                                        metrics.Height = metrics.RowCount * 112
                                        healthCard.Location = New Point(0, metrics.Bottom + 16)
                                        healthCard.Width = contentWidth
                                        historyCard.Location = New Point(0, healthCard.Bottom + 16)
                                        historyCard.Width = contentWidth
                                        eventsCard.Location = New Point(0, historyCard.Bottom + 16)
                                        eventsCard.Width = contentWidth
                                        readinessCard.Location = New Point(0, eventsCard.Bottom + 12)
                                        readinessCard.Left = 0
                                        readinessCard.Width = contentWidth
                                    Else
                                        quickActions.ColumnCount = 4
                                        quickActions.RowCount = 1
                                        quickActions.ColumnStyles.Clear()
                                        quickActions.RowStyles.Clear()
                                        For index = 0 To 3
                                            quickActions.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 25.0F))
                                        Next
                                        quickActions.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
                                        metrics.ColumnCount = metricCards.Length
                                        metrics.RowCount = 1
                                        metrics.ColumnStyles.Clear()
                                        metrics.RowStyles.Clear()
                                        For index = 0 To metricCards.Length - 1
                                            metrics.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F / metricCards.Length))
                                        Next
                                        metrics.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
                                        For index = 0 To quickButtons.Length - 1
                                            quickActions.SetCellPosition(quickButtons(index),
                                                New TableLayoutPanelCellPosition(index, 0))
                                        Next
                                        For index = 0 To metricCards.Length - 1
                                            metrics.SetCellPosition(metricCards(index),
                                                New TableLayoutPanelCellPosition(index, 0))
                                        Next
                                        quickActions.Height = 36
                                        metrics.Location = New Point(0, 47)
                                        metrics.Height = 112
                                        Dim columnWidth = Math.Max(0, (contentWidth - 16) \ 2)
                                        healthCard.Location = New Point(0, 175)
                                        healthCard.Width = columnWidth
                                        historyCard.Location = New Point(columnWidth + 16, 175)
                                        historyCard.Width = Math.Max(0, contentWidth - columnWidth - 16)
                                        eventsCard.Location = New Point(0, 396)
                                        eventsCard.Width = columnWidth
                                        readinessCard.Location = New Point(columnWidth + 16, 396)
                                        readinessCard.Width = Math.Max(0, contentWidth - columnWidth - 16)
                                    End If
                                    _plcDetail.Width = Math.Max(0, healthCard.ClientSize.Width - 32)
                                    _historianSummary.Width = Math.Max(0, historyCard.ClientSize.Width - 32)
                                    _exportSummary.Width = Math.Max(0, historyCard.ClientSize.Width - 32)
                                    _backupSummary.Width = Math.Max(0, historyCard.ClientSize.Width - 32)
                                    eventsNote.Width = Math.Max(0, eventsCard.ClientSize.Width - 32)
                                    _readinessSummary.Width = Math.Max(0, readinessCard.ClientSize.Width - 32)
                                End Sub
        _readinessSummary.Text = "Confirm PLC addressing and word order with the approved site project. No source sample timestamp is available."
    End Sub

    Private Shared Function AddQuickAction(parent As TableLayoutPanel,
                                           caption As String,
                                           action As Action,
                                           column As Integer,
                                           Optional primary As Boolean = False) As Button
        Dim button = UiTheme.Button(caption, primary)
        button.Dock = DockStyle.Fill
        button.Margin = New Padding(0, 0, 8, 0)
        button.Enabled = action IsNot Nothing
        If action IsNot Nothing Then AddHandler button.Click, Sub(sender, e) action.Invoke()
        parent.Controls.Add(button, column, 0)
        Return button
    End Function

    Private Shared Function MetricCard(caption As String, value As Label, detail As String, accent As Color) As Control
        Dim card As New RoundedPanel With {.Dock = DockStyle.Fill, .Margin = New Padding(0, 0, 12, 0), .BackColor = UiTheme.Surface}
        Dim bar As New Panel With {.BackColor = accent, .Location = New Point(16, 16), .Size = New Size(28, 3)}
        card.Controls.Add(bar)
        Dim label = UiTheme.Label(caption, New Font("Segoe UI", 7.5F, FontStyle.Bold), UiTheme.MutedInk, False)
        label.AutoEllipsis = True
        label.Location = New Point(16, 28)
        label.Size = New Size(Math.Max(0, card.ClientSize.Width - 32), 17)
        card.Controls.Add(label)
        value.Font = New Font("Segoe UI", 15.0F, FontStyle.Bold)
        value.Name = caption
        value.ForeColor = UiTheme.Ink
        value.AutoSize = False
        value.AutoEllipsis = True
        value.Location = New Point(16, 48)
        value.Size = New Size(Math.Max(0, card.ClientSize.Width - 32), 26)
        value.Text = "—"
        card.Controls.Add(value)
        Dim description = UiTheme.Label(detail, New Font("Segoe UI", 7.5F), UiTheme.MutedInk, False)
        description.AutoEllipsis = True
        description.Location = New Point(16, 81)
        description.Size = New Size(Math.Max(0, card.ClientSize.Width - 32), 20)
        card.Controls.Add(description)
        AddHandler card.Resize, Sub(sender, e)
                                    Dim width = Math.Max(0, card.ClientSize.Width - 32)
                                    label.Width = width
                                    value.Width = width
                                    description.Width = width
                                End Sub
        Return card
    End Function

    Private Shared Sub AddCardHeading(card As Control, text As String, x As Integer, y As Integer)
        Dim heading = UiTheme.Label(text, New Font("Segoe UI", 10.0F, FontStyle.Bold), UiTheme.Ink)
        heading.Location = New Point(x, y)
        card.Controls.Add(heading)
    End Sub

    Private Shared Sub AddInfoRow(card As Control, caption As String, value As String, top As Integer, width As Integer, Optional color As Color? = Nothing)
        Dim label = UiTheme.Label(caption, New Font("Segoe UI", 8.5F), UiTheme.MutedInk, False)
        label.Location = New Point(16, top)
        label.Size = New Size(CInt(width * 0.40R), 20)
        label.AutoEllipsis = True
        card.Controls.Add(label)
        Dim content = UiTheme.Label(value, New Font("Segoe UI", 8.0F, FontStyle.Bold), If(color.HasValue, color.Value, UiTheme.Ink), False)
        content.Location = New Point(CInt(width * 0.42R), top)
        content.Size = New Size(CInt(width * 0.56R), 20)
        content.TextAlign = ContentAlignment.MiddleRight
        content.AutoEllipsis = True
        card.Controls.Add(content)
        AddHandler card.Resize, Sub(sender, e)
                                    Dim available = Math.Max(0, card.ClientSize.Width - 32)
                                    label.Width = CInt(available * 0.38R)
                                    content.Left = 16 + CInt(available * 0.42R)
                                    content.Width = CInt(available * 0.54R)
                                End Sub
    End Sub

    Public Sub SetPlcStatus(status As PlcConnectionStatus)
        _plcState.Text = UiTheme.StatusText(status)
        _plcState.ForeColor = UiTheme.StatusColor(status)
        _plcDetail.Text = status.Detail
        _lastReadValue.Text = If(status.LastPollUtc.HasValue,
            status.LastPollUtc.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            "—")
        _readinessSummary.Text = If(status.Health = PlcHealth.Connected,
            "Action required: confirm source freshness, register addressing and scaling against approved site records.",
            "Action required: review PLC communication — " & status.Detail &
                " Source sample freshness remains unavailable without PLC timestamp/sequence metadata.")
    End Sub

    Public Sub SetReadings(snapshots As IReadOnlyList(Of MeterSnapshot))
        If snapshots Is Nothing Then Return
        Dim validBlocks = _plc.Blocks.Where(
            Function(block)
                Dim snapshot = snapshots.FirstOrDefault(Function(item) item IsNot Nothing AndAlso
                    String.Equals(item.TransformerName, block.TransformerName, StringComparison.OrdinalIgnoreCase))
                Return HasValidActivePower(snapshot)
            End Function).Count()
        _validBlocksValue.Text = validBlocks.ToString(CultureInfo.InvariantCulture) & " / 4"
        Dim total = LoadGaugeMath.CalculateTotalLoad(snapshots)
        _totalPowerValue.Text = If(total.HasValue,
            total.Value.ToString("0.0", CultureInfo.InvariantCulture) & " kW",
            "—")
        _totalPowerValue.ForeColor = If(total.HasValue, UiTheme.Ink, UiTheme.MutedInk)
    End Sub

    Public Sub SetSnmpStatus(service As UpsAcquisitionService)
        Dim settings = UpsSettingsStore.Current
        If Not String.IsNullOrWhiteSpace(UpsSettingsStore.LoadError) Then
            _snmpUnitsValue.Text = "CONFIG ERROR"
            _snmpUnitsValue.ForeColor = UiTheme.Red
            Return
        End If

        If service Is Nothing Then
            _snmpUnitsValue.Text = "OFFLINE"
            _snmpUnitsValue.ForeColor = UiTheme.Amber
            Return
        End If

        If Not service.IsRunning Then
            _snmpUnitsValue.Text = "WAITING"
            _snmpUnitsValue.ForeColor = UiTheme.Amber
            Return
        End If

        Dim enabled = settings.Devices.Where(Function(device) device.Enabled).ToList()
        If enabled.Count = 0 Then
            _snmpUnitsValue.Text = "0 / 0"
            _snmpUnitsValue.ForeColor = UiTheme.MutedInk
            Return
        End If

        Dim snapshots = service.GetSnapshots()
        Dim good = Enumerable.Count(enabled, Function(device)
                                                 Dim snapshot As UpsSnapshot = Nothing
                                                 Return snapshots.TryGetValue(device.Id, snapshot) AndAlso snapshot.IsGood
                                             End Function)
        _snmpUnitsValue.Text = good.ToString(CultureInfo.InvariantCulture) & " / " &
                               enabled.Count.ToString(CultureInfo.InvariantCulture)
        _snmpUnitsValue.ForeColor = If(good = enabled.Count, UiTheme.Green,
                                       If(good > 0, UiTheme.Amber, UiTheme.Red))
    End Sub

    Public Sub SetRuntimeDiagnostics(status As PlcConnectionStatus,
                                     historian As HistorianDiagnosticsSnapshot,
                                     operationalLog As OperationalLogDiagnosticsSnapshot)
        SetPlcStatus(status)
        _historianSummary.Text =
            "Historian queue " & historian.QueueDepth.ToString(CultureInfo.InvariantCulture) &
            " · dropped " & historian.DroppedCycles.ToString(CultureInfo.InvariantCulture) &
            " · write failures " & historian.WriteFailures.ToString(CultureInfo.InvariantCulture) &
            If(String.IsNullOrWhiteSpace(historian.LastError), String.Empty, " · ERROR: " & historian.LastError) &
            Environment.NewLine &
            "Operational-log queue " & operationalLog.QueueDepth.ToString(CultureInfo.InvariantCulture) &
            " · dropped " & operationalLog.DroppedAttempts.ToString(CultureInfo.InvariantCulture)
        Try
            Dim jobs = _scheduler.GetRecentJobs(100)
            Dim latest = jobs.OrderByDescending(Function(job) If(job.CompletedAtUtc, job.StartedAtUtc).GetValueOrDefault(DateTime.MinValue)).FirstOrDefault()
            Dim lastSuccessful = jobs.
                Where(Function(job) String.Equals(job.Status, "Succeeded", StringComparison.OrdinalIgnoreCase)).
                OrderByDescending(Function(job) If(job.CompletedAtUtc, job.StartedAtUtc).GetValueOrDefault(DateTime.MinValue)).
                FirstOrDefault()
            If lastSuccessful Is Nothing Then
                _exportSummary.Text = "Last successful daily export: none recorded."
            Else
                Dim completed = If(lastSuccessful.CompletedAtUtc.HasValue,
                    lastSuccessful.CompletedAtUtc.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                    lastSuccessful.LocalDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                _exportSummary.Text = "Last successful daily export: " & lastSuccessful.AnalyzerId & " · " & completed
            End If
            If latest IsNot Nothing AndAlso
               String.Equals(latest.Status, "Failed", StringComparison.OrdinalIgnoreCase) AndAlso
               Not String.IsNullOrWhiteSpace(latest.ErrorMessage) Then
                _exportSummary.Text &= Environment.NewLine & "Latest export failure: " & latest.ErrorMessage
            End If
        Catch ex As Exception
            _exportSummary.Text = "Scheduled report status unavailable: " & ex.Message
        End Try
        If _backupService Is Nothing Then
            _backupSummary.Text = "Backup status unavailable."
            _backupSummary.ForeColor = UiTheme.Amber
        ElseIf _backupService.LastBackupUtc.HasValue Then
            _backupSummary.Text = "Last verified backup: " &
                _backupService.LastBackupUtc.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            _backupSummary.ForeColor = UiTheme.Green
        ElseIf Not String.IsNullOrWhiteSpace(_backupService.LastError) Then
            _backupSummary.Text = "No successful backup recorded · " & _backupService.LastError
            _backupSummary.ForeColor = UiTheme.Red
        Else
            _backupSummary.Text = "No successful backup recorded."
            _backupSummary.ForeColor = UiTheme.MutedInk
        End If
    End Sub

    Private Shared Function HasValidActivePower(snapshot As MeterSnapshot) As Boolean
        Return snapshot IsNot Nothing AndAlso
               snapshot.Quality = MeterQuality.Good AndAlso
               snapshot.DecodedValues IsNot Nothing AndAlso
               snapshot.DecodedValues.Length > 10 AndAlso
               Single.IsFinite(snapshot.DecodedValues(10))
    End Function
End Class
