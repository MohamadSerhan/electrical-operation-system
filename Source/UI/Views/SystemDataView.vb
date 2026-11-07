Imports System.Drawing
Imports System.Globalization
Imports System.Linq
Imports System.Windows.Forms

Public NotInheritable Class SystemDataView
    Inherits UserControl

    Private _plc As PlcSettings
    Private ReadOnly _openTransformers As Action
    Private ReadOnly _status As New Label()
    Private ReadOnly _statusDetail As New Label()
    Private ReadOnly _blockGrid As New DataGridView()
    Private ReadOnly _mapGrid As New DataGridView()
    Private ReadOnly _blockRows As New Dictionary(Of String, DataGridViewRow)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _runtimeValues As New Dictionary(Of String, Label)(StringComparer.Ordinal)
    Private ReadOnly _infoValues As New Dictionary(Of String, Label)(StringComparer.Ordinal)

    Public Sub New(plc As PlcSettings,
                   status As PlcConnectionStatus,
                   openTransformers As Action,
                   Optional snapshots As IReadOnlyList(Of MeterSnapshot) = Nothing)
        If plc Is Nothing Then Throw New ArgumentNullException(NameOf(plc))
        If status Is Nothing Then Throw New ArgumentNullException(NameOf(status))
        If openTransformers Is Nothing Then Throw New ArgumentNullException(NameOf(openTransformers))

        _plc = plc
        _openTransformers = openTransformers
        BackColor = UiTheme.Canvas
        SetStyle(ControlStyles.OptimizedDoubleBuffer Or ControlStyles.AllPaintingInWmPaint, True)
        BuildLayout()
        SetPlcStatus(status)
        SetReadings(snapshots)
    End Sub

    Private Sub BuildLayout()
        Dim root As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 3,
            .Padding = New Padding(2),
            .BackColor = UiTheme.Canvas}
        root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 72.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 48.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 52.0F))
        Controls.Add(root)

        Dim statusCard As New RoundedPanel With {
            .Dock = DockStyle.Fill,
            .BackColor = UiTheme.Surface,
            .Padding = New Padding(14, 8, 14, 8),
            .Margin = New Padding(4)}
        Dim statusLayout As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 2,
            .RowCount = 2,
            .BackColor = Color.Transparent}
        statusLayout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        statusLayout.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 174.0F))
        statusLayout.RowStyles.Add(New RowStyle(SizeType.Absolute, 28.0F))
        statusLayout.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        _status.Dock = DockStyle.Fill
        _status.Font = New Font("Segoe UI", 13.0F, FontStyle.Bold)
        _status.TextAlign = ContentAlignment.MiddleLeft
        _statusDetail.Dock = DockStyle.Fill
        _statusDetail.Font = New Font("Segoe UI", 8.5F)
        _statusDetail.ForeColor = UiTheme.MutedInk
        _statusDetail.AutoEllipsis = True
        statusLayout.Controls.Add(_status, 0, 0)
        statusLayout.SetColumnSpan(_status, 2)
        statusLayout.Controls.Add(_statusDetail, 0, 1)

        Dim liveViewButton = UiTheme.ActionButton("Open transformer view", False, Icons.Monitor)
        liveViewButton.Dock = DockStyle.Fill
        liveViewButton.Margin = New Padding(8, 2, 0, 0)
        AddHandler liveViewButton.Click, Sub(sender, e) _openTransformers.Invoke()
        statusLayout.Controls.Add(liveViewButton, 1, 1)
        statusCard.Controls.Add(statusLayout)
        root.Controls.Add(statusCard, 0, 0)

        Dim upper As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 2,
            .RowCount = 1,
            .BackColor = Color.Transparent}
        upper.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 53.0F))
        upper.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 47.0F))
        root.Controls.Add(upper, 0, 1)

        Dim communication As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 2,
            .RowCount = 3,
            .Padding = New Padding(4),
            .BackColor = Color.Transparent}
        communication.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50.0F))
        communication.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50.0F))
        For index = 0 To 2
            communication.RowStyles.Add(New RowStyle(SizeType.Percent, 33.333F))
        Next
        AddInfoTile(communication, "PLC MODEL", _plc.Model, 0, 0)
        AddInfoTile(communication, "ENDPOINT", _plc.Host & ":" & _plc.Port.ToString(CultureInfo.InvariantCulture), 1, 0)
        AddInfoTile(communication, "UNIT / FUNCTION", _plc.UnitId.ToString(CultureInfo.InvariantCulture) & " / FC03", 0, 1)
        AddInfoTile(communication, "POLL / TIMEOUT", _plc.PollingIntervalSeconds.ToString(CultureInfo.InvariantCulture) & " s / " &
                    _plc.ReadTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture) & " ms", 1, 1)
        AddInfoTile(communication, "FLOAT WORD ORDER", If(_plc.SwapFloatWords, "16-bit word swap", "No word swap"), 0, 2)
        AddInfoTile(communication, "SOURCE CLOCK", "Not provided by meter", 1, 2)
        upper.Controls.Add(CreateCard("Connection", communication), 0, 0)

        ConfigureGrid(_blockGrid)
        _blockGrid.RowTemplate.Height = 36
        _blockGrid.Columns.Add("Block", "METER / BLOCK")
        _blockGrid.Columns.Add("Quality", "QUALITY / DETAIL")
        _blockGrid.Columns.Add("Capture", "CAPTURE (UTC) / RESPONSE")
        _blockGrid.Columns("Block").FillWeight = 25.0F
        _blockGrid.Columns("Quality").FillWeight = 37.0F
        _blockGrid.Columns("Capture").FillWeight = 38.0F
        _blockGrid.DefaultCellStyle.WrapMode = DataGridViewTriState.True
        For Each block In _plc.Blocks
            Dim index = _blockGrid.Rows.Add(block.TransformerName, "Not checked", "—")
            _blockRows.Add(block.TransformerName, _blockGrid.Rows(index))
        Next
        upper.Controls.Add(CreateCard("Meter acquisition", _blockGrid), 1, 0)

        Dim lower As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 2,
            .RowCount = 1,
            .BackColor = Color.Transparent}
        lower.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 61.0F))
        lower.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 39.0F))
        root.Controls.Add(lower, 0, 2)

        ConfigureGrid(_mapGrid)
        _mapGrid.RowTemplate.Height = 23
        _mapGrid.ColumnHeadersHeight = 28
        _mapGrid.Columns.Add("Offset", "OFFSET")
        _mapGrid.Columns.Add("Tag", "PLC TAG")
        _mapGrid.Columns.Add("Field", "APPLICATION FIELD")
        _mapGrid.Columns("Offset").FillWeight = 14.0F
        _mapGrid.Columns("Tag").FillWeight = 36.0F
        _mapGrid.Columns("Field").FillWeight = 50.0F
        Dim tags = {"VAN", "VBN", "VCN", "VLNAVG", "VAB", "VBC", "VCA", "VLLAVG", "FREQ", "IAVG", "PTOTAL", "IA", "IB", "IC", "IN", "DEGC"}
        For index = 0 To tags.Length - 1
            _mapGrid.Rows.Add(index.ToString(CultureInfo.InvariantCulture), tags(index), PlcModbus.FieldNames(index))
        Next
        _mapGrid.CurrentCell = Nothing
        lower.Controls.Add(CreateCard("PM5110 register map · FC03", _mapGrid), 0, 0)

        Dim runtime As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 2,
            .RowCount = 7,
            .Padding = New Padding(8, 4, 8, 4),
            .BackColor = Color.Transparent}
        runtime.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 56.0F))
        runtime.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 44.0F))
        For index = 0 To 6
            runtime.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F / 7.0F))
        Next
        AddRuntimeMetric(runtime, "cycle", "Last cycle", 0, 0)
        AddRuntimeMetric(runtime, "p95", "P95 cycle", 1, 0)
        AddRuntimeMetric(runtime, "rate", "Good cycles", 0, 1)
        AddRuntimeMetric(runtime, "transactions", "FC03 success / fail", 1, 1)
        AddRuntimeMetric(runtime, "retry", "Retries / timeouts", 0, 2)
        AddRuntimeMetric(runtime, "ui", "UI render rate", 1, 2)
        AddRuntimeMetric(runtime, "historian", "Historian queue / dropped", 0, 3)
        AddRuntimeMetric(runtime, "historian-write", "Last historian write", 1, 3)
        AddRuntimeMetric(runtime, "historian-state", "Historian status", 0, 4)
        AddRuntimeMetric(runtime, "log-queue", "Attempt-log queue / dropped", 1, 4)
        AddRuntimeMetric(runtime, "log-failures", "Attempt-log write failures", 0, 5)
        AddRuntimeMetric(runtime, "log-state", "Attempt-log status", 1, 5)
        AddRuntimeMetric(runtime, "build", "Build", 0, 6)
        runtime.SetColumnSpan(runtime.GetControlFromPosition(0, 6), 2)
        lower.Controls.Add(CreateCard("Runtime health", runtime), 1, 0)

    End Sub

    Private Shared Function CreateCard(title As String, content As Control) As RoundedPanel
        Dim card As New RoundedPanel With {
            .Dock = DockStyle.Fill,
            .BackColor = UiTheme.Canvas,
            .BorderColor = UiTheme.Canvas,
            .Padding = New Padding(10, 8, 10, 8),
            .Margin = New Padding(4)}
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
        Return card
    End Function

    Private Sub AddInfoTile(parent As TableLayoutPanel, caption As String, value As String, column As Integer, row As Integer)
        Dim cell As New Panel With {
            .Dock = DockStyle.Fill,
            .Margin = New Padding(3),
            .Padding = New Padding(7, 4, 7, 3),
            .BackColor = UiTheme.SurfaceMuted}
        Dim layout As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 2,
            .BackColor = Color.Transparent}
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, 17.0F))
        layout.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        Dim heading = UiTheme.Label(caption, New Font("Segoe UI", 7.5F, FontStyle.Bold), UiTheme.MutedInk, False)
        heading.Dock = DockStyle.Fill
        heading.AutoEllipsis = True
        Dim valueLabel = UiTheme.Label(value, New Font("Segoe UI", 9.0F, FontStyle.Bold), UiTheme.Ink, False)
        valueLabel.Dock = DockStyle.Fill
        valueLabel.AutoEllipsis = True
        _infoValues.Add(caption, valueLabel)
        layout.Controls.Add(heading, 0, 0)
        layout.Controls.Add(valueLabel, 0, 1)
        cell.Controls.Add(layout)
        parent.Controls.Add(cell, column, row)
    End Sub

    Private Sub AddRuntimeMetric(parent As TableLayoutPanel, key As String, caption As String, column As Integer, row As Integer)
        Dim cell As New Panel With {
            .Dock = DockStyle.Fill,
            .Margin = New Padding(3),
            .Padding = New Padding(6, 2, 6, 2),
            .BackColor = UiTheme.SurfaceMuted}
        Dim layout As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 2,
            .BackColor = Color.Transparent}
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, 15.0F))
        layout.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        Dim heading = UiTheme.Label(caption.ToUpperInvariant(), New Font("Segoe UI", 7.0F, FontStyle.Bold), UiTheme.MutedInk, False)
        heading.Dock = DockStyle.Fill
        heading.AutoEllipsis = True
        Dim value = UiTheme.Label("—", New Font("Segoe UI", 8.5F, FontStyle.Bold), UiTheme.Ink, False)
        value.Dock = DockStyle.Fill
        value.AutoEllipsis = True
        _runtimeValues.Add(key, value)
        layout.Controls.Add(heading, 0, 0)
        layout.Controls.Add(value, 0, 1)
        cell.Controls.Add(layout)
        parent.Controls.Add(cell, column, row)
    End Sub

    Private Shared Sub ConfigureGrid(grid As DataGridView)
        grid.Dock = DockStyle.Fill
        grid.ReadOnly = True
        grid.AllowUserToAddRows = False
        grid.AllowUserToDeleteRows = False
        grid.AllowUserToResizeRows = False
        grid.MultiSelect = False
        grid.RowHeadersVisible = False
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect
        grid.BackgroundColor = UiTheme.Surface
        grid.BorderStyle = BorderStyle.None
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal
        grid.GridColor = UiTheme.Border
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        grid.EnableHeadersVisualStyles = False
        UiTheme.StyleReadOnlyGrid(grid)
    End Sub

    Public Sub SetReadings(snapshots As IReadOnlyList(Of MeterSnapshot))
        If IsDisposed OrElse _blockGrid.IsDisposed Then Return

        For Each block In _plc.Blocks
            Dim row As DataGridViewRow = Nothing
            If Not _blockRows.TryGetValue(block.TransformerName, row) Then Continue For
            Dim snapshot = If(snapshots Is Nothing, Nothing,
                snapshots.FirstOrDefault(Function(item) item IsNot Nothing AndAlso
                    String.Equals(item.TransformerName, block.TransformerName, StringComparison.OrdinalIgnoreCase)))
            Dim result = If(snapshot Is Nothing, "Not checked", snapshot.Quality.ToString())
            If snapshot IsNot Nothing AndAlso
               snapshot.Quality <> MeterQuality.Good AndAlso
               Not String.IsNullOrWhiteSpace(snapshot.ErrorMessage) Then
                result &= " · " & snapshot.ErrorMessage
            End If
            Dim captured = If(snapshot IsNot Nothing AndAlso snapshot.CapturedAtUtc.HasValue,
                snapshot.CapturedAtUtc.Value.ToString("yyyy-MM-dd HH:mm:ss.f", CultureInfo.InvariantCulture) & " UTC",
                "—")
            Dim response = If(snapshot IsNot Nothing AndAlso snapshot.ResponseLatencyMilliseconds.HasValue,
                snapshot.ResponseLatencyMilliseconds.Value.ToString("0.0", CultureInfo.InvariantCulture) & " ms",
                "—")
            SetCellValue(row.Cells(0), block.TransformerName & Environment.NewLine &
                block.RegisterStart.ToString(CultureInfo.InvariantCulture) & "–" &
                (block.RegisterStart + _plc.RegisterCount - 1).ToString(CultureInfo.InvariantCulture))
            SetCellValue(row.Cells(1), result)
            SetCellValue(row.Cells(2), captured & Environment.NewLine & response)
        Next
    End Sub

    Public Sub SetPlcStatus(status As PlcConnectionStatus)
        If status Is Nothing OrElse IsDisposed Then Return
        SetLabelText(_status, UiTheme.StatusText(status))
        Dim statusColor = UiTheme.StatusColor(status)
        If _status.ForeColor <> statusColor Then _status.ForeColor = statusColor
        SetLabelText(_statusDetail, status.Detail)
    End Sub

    Public Sub UpdatePlcSettings(settings As PlcSettings)
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        _plc = settings.Clone()
        SetLabelText(_infoValues("PLC MODEL"), _plc.Model)
        SetLabelText(_infoValues("ENDPOINT"), _plc.Host & ":" & _plc.Port.ToString(CultureInfo.InvariantCulture))
        SetLabelText(_infoValues("UNIT / FUNCTION"), _plc.UnitId.ToString(CultureInfo.InvariantCulture) & " / FC03")
        SetLabelText(_infoValues("POLL / TIMEOUT"), FormatInterval(_plc.EffectivePollingIntervalMilliseconds) & " / " &
            _plc.ReadTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture) & " ms")
        SetLabelText(_infoValues("FLOAT WORD ORDER"), If(_plc.SwapFloatWords, "16-bit word swap", "No word swap"))
    End Sub

    Private Shared Function FormatInterval(milliseconds As Integer) As String
        If milliseconds Mod 1000 = 0 Then Return (milliseconds \ 1000).ToString(CultureInfo.InvariantCulture) & " s"
        Return milliseconds.ToString(CultureInfo.InvariantCulture) & " ms"
    End Function

    Public Sub SetRuntimeDiagnostics(status As PlcConnectionStatus,
                                     uiRefreshRateHz As Double,
                                     historian As HistorianDiagnosticsSnapshot,
                                     operationalLog As OperationalLogDiagnosticsSnapshot)
        If status Is Nothing OrElse IsDisposed Then Return
        SetPlcStatus(status)
        Dim metrics = status.Diagnostics
        SetLabelText(_runtimeValues("cycle"), FormatMilliseconds(metrics.LastCycleDurationMilliseconds))
        SetLabelText(_runtimeValues("p95"), FormatMilliseconds(metrics.P95CycleDurationMilliseconds))
        SetLabelText(_runtimeValues("rate"), If(metrics.SuccessfulCycleRateHz.HasValue,
            metrics.SuccessfulCycleRateHz.Value.ToString("0.000", CultureInfo.InvariantCulture) & " cycles/s",
            "Pending"))
        SetLabelText(_runtimeValues("transactions"),
            metrics.SuccessfulTransactions.ToString(CultureInfo.InvariantCulture) & " / " &
            metrics.FailedTransactions.ToString(CultureInfo.InvariantCulture))
        SetLabelText(_runtimeValues("retry"),
            metrics.RetryCount.ToString(CultureInfo.InvariantCulture) & " / " &
            metrics.TimeoutCount.ToString(CultureInfo.InvariantCulture))
        SetLabelText(_runtimeValues("ui"), uiRefreshRateHz.ToString("0.0", CultureInfo.InvariantCulture) & " Hz")
        SetLabelText(_runtimeValues("historian"),
            historian.QueueDepth.ToString(CultureInfo.InvariantCulture) & " / " &
            historian.DroppedCycles.ToString(CultureInfo.InvariantCulture))
        SetLabelText(_runtimeValues("historian-write"), FormatMilliseconds(historian.LastWriteLatencyMilliseconds))
        SetLabelText(_runtimeValues("historian-state"), If(String.IsNullOrWhiteSpace(historian.LastError), "Healthy", "ERROR: " & historian.LastError))
        SetLabelText(_runtimeValues("log-queue"),
            operationalLog.QueueDepth.ToString(CultureInfo.InvariantCulture) & " / " &
            operationalLog.DroppedAttempts.ToString(CultureInfo.InvariantCulture))
        SetLabelText(_runtimeValues("log-failures"), operationalLog.WriteFailures.ToString(CultureInfo.InvariantCulture))
        SetLabelText(_runtimeValues("log-state"), If(String.IsNullOrWhiteSpace(operationalLog.LastError), "Healthy", "ERROR: " & operationalLog.LastError))
        SetLabelText(_runtimeValues("build"), AppBuildInfo.VersionDisplay & " · " & AppBuildInfo.BuildIdentifier)
    End Sub

    Private Shared Sub SetCellValue(cell As DataGridViewCell, value As String)
        If Not String.Equals(Convert.ToString(cell.Value, CultureInfo.InvariantCulture), value, StringComparison.Ordinal) Then
            cell.Value = value
        End If
    End Sub

    Private Shared Sub SetLabelText(label As Label, value As String)
        If Not String.Equals(label.Text, value, StringComparison.Ordinal) Then label.Text = value
    End Sub

    Private Shared Function FormatMilliseconds(value As Double?) As String
        Return If(value.HasValue, value.Value.ToString("0.0", CultureInfo.InvariantCulture) & " ms", "—")
    End Function
End Class
