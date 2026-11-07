Imports System.Collections.Generic
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Globalization
Imports System.Linq
Imports System.Windows.Forms

' UPS & ATS monitoring page: factory summary plus one panel per ATS bank with a card per UPS.
' Values come only from read-only SNMP; anything not Good is shown as its quality state, never as zero.
Public NotInheritable Class UpsAtsView
    Inherits UserControl

    Friend Shared ReadOnly AlarmRed As Color = Color.FromArgb(200, 36, 36)
    Friend Shared ReadOnly WarnAmber As Color = Color.FromArgb(214, 140, 0)
    Friend Shared ReadOnly OkGreen As Color = Color.FromArgb(24, 132, 91)
    Friend Shared ReadOnly NoDataGrey As Color = Color.FromArgb(128, 142, 156)
    Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture

    Private ReadOnly _service As UpsAcquisitionService
    Private ReadOnly _historian As UpsHistorian
    Private ReadOnly _session As UserSession
    Private ReadOnly _openSettings As Action
    Private ReadOnly _summary As New UpsSummaryStrip()
    Private ReadOnly _banks As New List(Of AtsBankControl)()
    Private ReadOnly _subtitle As Label
    Private ReadOnly _refreshTimer As New Timer With {.Interval = 1000}
    Private ReadOnly _flashTimer As New Timer With {.Interval = 500}
    Private ReadOnly _bankGrid As TableLayoutPanel
    Private _lastVersion As Long = -1
    Private _lastSettings As UpsSettings
    Private _flashOn As Boolean
    Private _active As Boolean

    Public Sub New(service As UpsAcquisitionService, historian As UpsHistorian, session As UserSession, openSettings As Action)
        _service = service
        _historian = historian
        _session = session
        _openSettings = openSettings
        BackColor = UiTheme.Canvas
        DoubleBuffered = True

        _bankGrid = New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 2, .RowCount = 2, .BackColor = Color.Transparent, .Padding = New Padding(0)}
        _bankGrid.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50.0F))
        _bankGrid.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50.0F))
        _bankGrid.RowStyles.Add(New RowStyle(SizeType.Percent, 50.0F))
        _bankGrid.RowStyles.Add(New RowStyle(SizeType.Percent, 50.0F))
        Controls.Add(_bankGrid)

        _summary.Dock = DockStyle.Top
        _summary.Height = 96
        Controls.Add(_summary)

        Dim header As New Panel With {.Dock = DockStyle.Top, .Height = 36, .BackColor = Color.Transparent}
        _subtitle = UiTheme.Label(String.Empty, New Font("Segoe UI", 9.0F), UiTheme.MutedInk, False)
        _subtitle.Location = New Point(3, 8)
        _subtitle.Size = New Size(1100, 20)
        _subtitle.Anchor = AnchorStyles.Left Or AnchorStyles.Top Or AnchorStyles.Right
        header.Controls.Add(_subtitle)
        Dim fullScreenButton = ViewFullScreen.CreateButton(Me)
        fullScreenButton.Anchor = AnchorStyles.Top Or AnchorStyles.Right
        header.Controls.Add(fullScreenButton)
        AddHandler header.Resize, Sub(sender, e)
                                      fullScreenButton.Location = New Point(header.ClientSize.Width - fullScreenButton.Width - 4, 2)
                                      _subtitle.Width = Math.Max(100, fullScreenButton.Left - _subtitle.Left - 8)
                                  End Sub
        Controls.Add(header)

        AddHandler _refreshTimer.Tick, Sub(sender, e) RefreshData(False)
        AddHandler _flashTimer.Tick, AddressOf OnFlashTick
        AddHandler UpsSettingsStore.Changed, AddressOf OnSettingsChanged
        AddHandler AlarmSettingsStore.Changed, AddressOf OnSettingsChanged
        RebuildBanks()
        RefreshData(True)
    End Sub

    Public Sub SetPageActive(active As Boolean)
        _active = active
        If active Then
            RefreshData(True)
            _refreshTimer.Start()
            _flashTimer.Start()
        Else
            _refreshTimer.Stop()
            _flashTimer.Stop()
        End If
    End Sub

    Private Sub OnSettingsChanged(sender As Object, e As EventArgs)
        If IsDisposed OrElse Not IsHandleCreated Then Return
        BeginInvoke(Sub()
                        If IsDisposed Then Return
                        RebuildBanks()
                        RefreshData(True)
                    End Sub)
    End Sub

    Private Sub RebuildBanks()
        Dim settings = UpsSettingsStore.Current
        _lastSettings = settings
        _bankGrid.SuspendLayout()
        For Each bank In _banks
            _bankGrid.Controls.Remove(bank)
            bank.Dispose()
        Next
        _banks.Clear()
        Dim ordered = settings.Banks.OrderBy(Function(item) item.Number).ToList()
        _bankGrid.RowCount = Math.Max(1, CInt(Math.Ceiling(ordered.Count / 2.0R)))
        _bankGrid.RowStyles.Clear()
        For row = 0 To _bankGrid.RowCount - 1
            _bankGrid.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F / _bankGrid.RowCount))
        Next
        ' Every bank lays out the same number of card slots so all ATS panels and UPS cards share one size.
        Dim slots = Math.Max(1, ordered.Select(Function(bank) settings.Devices.Where(Function(device) device.AtsBank = bank.Number).Count()).DefaultIfEmpty(1).Max())
        For index = 0 To ordered.Count - 1
            Dim control As New AtsBankControl(ordered(index)) With {.Dock = DockStyle.Fill, .Margin = New Padding(4), .SlotCount = slots}
            AddHandler control.DeviceClicked, AddressOf OnDeviceClicked
            _banks.Add(control)
            _bankGrid.Controls.Add(control, index Mod 2, index \ 2)
        Next
        _bankGrid.ResumeLayout(True)
        _lastVersion = -1
    End Sub

    Private Sub OnDeviceClicked(sender As Object, deviceId As String)
        Dim device = UpsSettingsStore.Current.Devices.FirstOrDefault(Function(item) item.Id = deviceId)
        If device Is Nothing Then Return
        Dim form As New UpsDetailForm(device, _service, _historian)
        form.Show(FindForm())
    End Sub

    Private Sub OnFlashTick(sender As Object, e As EventArgs)
        _flashOn = Not _flashOn
        For Each bank In _banks
            bank.SetFlash(_flashOn)
        Next
    End Sub

    Private Sub RefreshData(force As Boolean)
        If IsDisposed Then Return
        Dim settings = UpsSettingsStore.Current
        If settings IsNot _lastSettings Then
            RebuildBanks()
            force = True
        End If
        Dim version = If(_service Is Nothing, 0L, _service.Version)
        ' Stale detection depends on time, so redraw at least every 5 s even without new data.
        If Not force AndAlso version = _lastVersion AndAlso DateTime.UtcNow.Second Mod 5 <> 0 Then Return
        _lastVersion = version
        Dim snapshots = If(_service Is Nothing, New Dictionary(Of String, UpsSnapshot)(), _service.GetSnapshots())
        Dim limits = AlarmSettingsStore.Current.Ups
        For Each bank In _banks
            bank.SetData(settings.Devices.Where(Function(device) device.AtsBank = bank.Definition.Number).ToList(), snapshots, limits)
        Next
        _summary.SetData(settings, snapshots, limits)
        If _service Is Nothing Then
            _subtitle.Text = "UPS acquisition is not running (offline preview or background services suppressed). No values are shown."
            _subtitle.ForeColor = WarnAmber
        Else
            Dim enabled = settings.Devices.Where(Function(device) device.Enabled).ToList()
            Dim good = Enumerable.Count(enabled, Function(device)
                                         Dim s As UpsSnapshot = Nothing
                                         Return snapshots.TryGetValue(device.Id, s) AndAlso s.IsGood
                                     End Function)
            Dim diagnostics = _service.GetDiagnostics()
            _subtitle.ForeColor = If(good = enabled.Count AndAlso enabled.Count > 0, UiTheme.MutedInk, WarnAmber)
            _subtitle.Text = "Read-only SNMP (RFC 1628 UPS-MIB)   •   " & good.ToString(Inv) & " of " & enabled.Count.ToString(Inv) &
                " units reporting   •   last poll " &
                If(diagnostics.LastPollUtc.HasValue, diagnostics.LastPollUtc.Value.ToLocalTime().ToString("HH:mm:ss", Inv), "—") &
                If(good < enabled.Count AndAlso diagnostics.Running, "   •   Automatic retry: up to 5 s between attempts (plus queue/read time)", "") &
                "   •   ATS source position: not monitored (no ATS telemetry)"
        End If
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            RemoveHandler UpsSettingsStore.Changed, AddressOf OnSettingsChanged
            RemoveHandler AlarmSettingsStore.Changed, AddressOf OnSettingsChanged
            _refreshTimer.Dispose()
            _flashTimer.Dispose()
        End If
        MyBase.Dispose(disposing)
    End Sub

    Friend Shared Function SeverityColor(severity As AlarmSeverity) As Color
        Select Case severity
            Case AlarmSeverity.ShedLoad, AlarmSeverity.Alarm : Return AlarmRed
            Case AlarmSeverity.PreAlarm : Return WarnAmber
            Case Else : Return OkGreen
        End Select
    End Function

    Friend Shared Function QualityColor(quality As UpsQuality) As Color
        Select Case quality
            Case UpsQuality.Good : Return OkGreen
            Case UpsQuality.Stale : Return WarnAmber
            Case UpsQuality.Disabled, UpsQuality.NoData : Return NoDataGrey
            Case Else : Return AlarmRed
        End Select
    End Function

    Friend Shared Function LoadColor(load As Double?, limits As EquipmentAlarmLimits) As Color
        If Not load.HasValue Then Return NoDataGrey
        If load.Value >= limits.LoadAlarmPercent Then Return AlarmRed
        If load.Value >= limits.LoadPreAlarmPercent Then Return WarnAmber
        Return OkGreen
    End Function

    Friend Shared Function RoundedRect(bounds As RectangleF, radius As Single) As GraphicsPath
        Dim path As New GraphicsPath()
        Dim d = radius * 2
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90)
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90)
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90)
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90)
        path.CloseFigure()
        Return path
    End Function

    Friend Shared Function Fmt(value As Double?, format As String, Optional suffix As String = "") As String
        If Not value.HasValue Then Return "—"
        Return value.Value.ToString(format, Inv) & suffix
    End Function
End Class

' Factory-wide KPI tiles. Totals include only units with Good quality and say how many that is.
Friend NotInheritable Class UpsSummaryStrip
    Inherits Control

    Private _tiles As New List(Of (Caption As String, Value As String, Note As String, Accent As Color))()

    Public Sub New()
        SetStyle(ControlStyles.OptimizedDoubleBuffer Or ControlStyles.AllPaintingInWmPaint Or ControlStyles.UserPaint Or ControlStyles.ResizeRedraw, True)
        Font = New Font("Segoe UI", 8.0F)
    End Sub

    Public Sub SetData(settings As UpsSettings, snapshots As IReadOnlyDictionary(Of String, UpsSnapshot), limits As EquipmentAlarmLimits)
        Dim inv = CultureInfo.InvariantCulture
        Dim enabled = settings.Devices.Where(Function(device) device.Enabled).ToList()
        Dim goods As New List(Of UpsSnapshot)()
        Dim faults = 0
        Dim alarms = 0
        Dim onBattery = 0
        Dim onBypass = 0
        For Each device In enabled
            Dim snapshot As UpsSnapshot = Nothing
            snapshots.TryGetValue(device.Id, snapshot)
            If snapshot IsNot Nothing AndAlso snapshot.IsGood Then
                goods.Add(snapshot)
                Dim source = snapshot.Value("OutputSource")
                If source.HasValue AndAlso CInt(source.Value) = 5 Then onBattery += 1
                If source.HasValue AndAlso CInt(source.Value) = 4 Then onBypass += 1
            Else
                faults += 1
            End If
            If UpsAlarmEvaluator.Worst(UpsAlarmEvaluator.Evaluate(snapshot, limits)) >= AlarmSeverity.Alarm AndAlso
               snapshot IsNot Nothing AndAlso snapshot.IsGood Then alarms += 1
        Next
        Dim kwValues = goods.Select(Function(item) item.AccountingOutputKw).Where(Function(item) item.HasValue).Select(Function(item) item.Value).ToList()
        Dim kvaValues = goods.Select(Function(item) item.AccountingOutputKva).Where(Function(item) item.HasValue).Select(Function(item) item.Value).ToList()
        Dim loads = goods.Select(Function(item) item.PhaseMax("OutputLoad")).Where(Function(item) item.HasValue).Select(Function(item) item.Value).ToList()
        Dim paired = goods.Where(Function(item) item.OutputKw.HasValue AndAlso item.OutputKvaDerived.HasValue AndAlso item.PowerFactorDerived.HasValue).ToList()
        Dim pairedKva = paired.Sum(Function(item) item.OutputKvaDerived.Value)
        Dim fleetPf = If(pairedKva > 0, paired.Sum(Function(item) item.OutputKw.Value) / pairedKva, CType(Nothing, Double?))
        Dim nameplate = settings.InstalledUpsCapacityKva()
        Dim isPartial = kwValues.Count < enabled.Count
        Dim reporting = goods.Count.ToString(inv) & " / " & enabled.Count.ToString(inv) & " units"
        Dim tiles As New List(Of (String, String, String, Color)) From {
            ("TOTAL UPS OUTPUT", If(kwValues.Count = 0, "—", kwValues.Sum().ToString("#,##0.0", inv) & " kW"),
                If(isPartial, "PARTIAL  •  " & reporting, reporting), If(isPartial, UpsAtsView.WarnAmber, UiTheme.Blue)),
            ("APPARENT POWER", If(kvaValues.Count = 0, "—", kvaValues.Sum().ToString("#,##0.0", inv) & " kVA"),
                kvaValues.Count.ToString(inv) & "/" & enabled.Count.ToString(inv) & " VA sources • PF " & UpsAtsView.Fmt(fleetPf, "0.00") & " (matched units)", UiTheme.Cyan),
            ("HIGHEST UNIT LOAD", If(loads.Count = 0, "—", loads.Max().ToString("0", inv) & " %"),
                "average " & If(loads.Count = 0, "—", loads.Average().ToString("0", inv) & " %"),
                UpsAtsView.LoadColor(If(loads.Count = 0, CType(Nothing, Double?), loads.Max()), limits)),
            ("ON BATTERY / BYPASS", onBattery.ToString(inv) & "  /  " & onBypass.ToString(inv),
                "of reporting units", If(onBattery > 0, UpsAtsView.AlarmRed, If(onBypass > 0, UpsAtsView.WarnAmber, UpsAtsView.OkGreen))),
            ("UNITS IN ALARM", alarms.ToString(inv), "load / voltage / battery / card alarms",
                If(alarms > 0, UpsAtsView.AlarmRed, UpsAtsView.OkGreen)),
            ("COMMUNICATION FAULTS", faults.ToString(inv), "timeout / offline / config",
                If(faults > 0, UpsAtsView.AlarmRed, UpsAtsView.OkGreen)),
            ("UPS NAMEPLATE TOTAL", nameplate.ToString("#,##0", inv) & " kVA", "sum of " & settings.Devices.Count.ToString(inv) & " registered UPS ratings", UiTheme.MutedInk)}
        _tiles = tiles.Select(Function(item) (item.Item1, item.Item2, item.Item3, item.Item4)).ToList()
        Invalidate()
    End Sub

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        Dim g = e.Graphics
        g.Clear(UiTheme.Canvas)
        g.SmoothingMode = SmoothingMode.AntiAlias
        If _tiles.Count = 0 Then Return
        Dim gap = 8.0F
        Dim width = (ClientSize.Width - gap * (_tiles.Count - 1) - 8) / _tiles.Count
        Using captionFont As New Font("Segoe UI", 7.5F, FontStyle.Bold),
              valueFont As New Font("Segoe UI", 17.0F, FontStyle.Bold),
              noteFont As New Font("Segoe UI", 8.0F)
            For index = 0 To _tiles.Count - 1
                Dim tile = _tiles(index)
                Dim bounds As New RectangleF(4 + index * (width + gap), 4, width, ClientSize.Height - 10)
                Using path = UpsAtsView.RoundedRect(bounds, 8), fill As New SolidBrush(UiTheme.Surface), border As New Pen(UiTheme.Border)
                    g.FillPath(fill, path)
                    g.DrawPath(border, path)
                End Using
                Using accent As New SolidBrush(tile.Accent)
                    g.FillRectangle(accent, bounds.X + 1, bounds.Y + 10, 4, bounds.Height - 20)
                End Using
                Dim textLeft = CInt(bounds.X + 14)
                Dim textWidth = CInt(bounds.Width - 20)
                TextRenderer.DrawText(g, tile.Caption, captionFont, New Rectangle(textLeft, CInt(bounds.Y + 8), textWidth, 16), UiTheme.MutedInk,
                                      TextFormatFlags.Left Or TextFormatFlags.EndEllipsis Or TextFormatFlags.SingleLine)
                TextRenderer.DrawText(g, tile.Value, valueFont, New Rectangle(textLeft, CInt(bounds.Y + 26), textWidth, 34),
                                      If(tile.Value = "—", UpsAtsView.NoDataGrey, UiTheme.Ink),
                                      TextFormatFlags.Left Or TextFormatFlags.EndEllipsis Or TextFormatFlags.SingleLine)
                TextRenderer.DrawText(g, tile.Note, noteFont, New Rectangle(textLeft, CInt(bounds.Bottom - 22), textWidth, 16),
                                      If(tile.Accent = UpsAtsView.AlarmRed OrElse tile.Accent = UpsAtsView.WarnAmber, tile.Accent, UiTheme.MutedInk),
                                      TextFormatFlags.Left Or TextFormatFlags.EndEllipsis Or TextFormatFlags.SingleLine)
            Next
        End Using
    End Sub
End Class

' One ATS bank: header with bank totals and a painted card per UPS (click opens the detail window).
Friend NotInheritable Class AtsBankControl
    Inherits Control

    Private NotInheritable Class CardState
        Public Device As UpsDevice
        Public Snapshot As UpsSnapshot
        Public Severity As AlarmSeverity
        Public AlarmText As String = String.Empty
        Public Bounds As Rectangle
    End Class

    Public Event DeviceClicked As EventHandler(Of String)

    Public ReadOnly Property Definition As AtsBankDefinition
    <System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public Property SlotCount As Integer = 1
    Private _cards As New List(Of CardState)()
    Private _limits As New EquipmentAlarmLimits()
    Private _flash As Boolean
    Private _hover As Integer = -1
    Private ReadOnly _toolTip As New ToolTip With {.InitialDelay = 400, .ReshowDelay = 200, .AutoPopDelay = 12000}

    Public Sub New(definition As AtsBankDefinition)
        Me.Definition = definition
        SetStyle(ControlStyles.OptimizedDoubleBuffer Or ControlStyles.AllPaintingInWmPaint Or ControlStyles.UserPaint Or ControlStyles.ResizeRedraw, True)
        Font = New Font("Segoe UI", 8.5F)
    End Sub

    Friend Shared Function FormatHeaderPower(runningKw As Double?, installedKva As Double) As String
        Return If(runningKw.HasValue, runningKw.Value.ToString("#,##0.0", CultureInfo.InvariantCulture) & " kW", "— kW") &
            "   |   " & If(Double.IsFinite(installedKva) AndAlso installedKva > 0,
                installedKva.ToString("#,##0", CultureInfo.InvariantCulture) & " kVA installed", "rating not configured")
    End Function

    Public Sub SetData(devices As IReadOnlyList(Of UpsDevice), snapshots As IReadOnlyDictionary(Of String, UpsSnapshot), limits As EquipmentAlarmLimits)
        _limits = limits
        _cards = devices.OrderBy(Function(device) device.Label, StringComparer.OrdinalIgnoreCase).Select(
            Function(device)
                Dim snapshot As UpsSnapshot = Nothing
                snapshots.TryGetValue(device.Id, snapshot)
                Dim alarms = UpsAlarmEvaluator.Evaluate(snapshot, limits).OrderByDescending(Function(item) item.Severity).
                    ThenByDescending(Function(item) item.CardAlarm IsNot Nothing).ToArray()
                Return New CardState With {
                    .Device = device, .Snapshot = snapshot, .Severity = UpsAlarmEvaluator.Worst(alarms),
                    .AlarmText = String.Join(Environment.NewLine, alarms.Select(Function(item) "• " & item.Text))}
            End Function).ToList()
        Invalidate()
    End Sub

    Public Sub SetFlash(flashOn As Boolean)
        If _flash = flashOn Then Return
        _flash = flashOn
        If _cards.Any(Function(card) card.Severity >= AlarmSeverity.Alarm) Then Invalidate()
    End Sub

    Protected Overrides Sub OnMouseMove(e As MouseEventArgs)
        MyBase.OnMouseMove(e)
        Dim index = _cards.FindIndex(Function(card) card.Bounds.Contains(e.Location))
        If index = _hover Then Return
        _hover = index
        Cursor = If(index >= 0, Cursors.Hand, Cursors.Default)
        If index >= 0 Then
            Dim card = _cards(index)
            Dim tip = card.Device.DisplayName & "  •  " & card.Device.IpAddress & "  •  " & card.Device.Model & Environment.NewLine &
                      "Identity: " & UpsQualityText.DescribeVerification(card.Device.Verification) &
                      If(card.Snapshot IsNot Nothing AndAlso card.Snapshot.SysName.Length > 0, "  (sysName '" & card.Snapshot.SysName & "')", "") &
                      If(String.IsNullOrWhiteSpace(card.Device.Notes), "", Environment.NewLine & card.Device.Notes) &
                      If(card.Snapshot IsNot Nothing AndAlso Not card.Snapshot.IsGood, Environment.NewLine & card.Snapshot.QualityDetail, "") &
                      If(card.AlarmText.Length > 0, Environment.NewLine & card.AlarmText, "") &
                      Environment.NewLine & "Click for full measurements."
            _toolTip.SetToolTip(Me, tip)
        Else
            _toolTip.SetToolTip(Me, Nothing)
        End If
        Invalidate()
    End Sub

    Protected Overrides Sub OnMouseLeave(e As EventArgs)
        MyBase.OnMouseLeave(e)
        _hover = -1
        Invalidate()
    End Sub

    Protected Overrides Sub OnMouseClick(e As MouseEventArgs)
        MyBase.OnMouseClick(e)
        Dim card = _cards.FirstOrDefault(Function(item) item.Bounds.Contains(e.Location))
        If card IsNot Nothing Then RaiseEvent DeviceClicked(Me, card.Device.Id)
    End Sub

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        Dim g = e.Graphics
        Dim inv = CultureInfo.InvariantCulture
        g.Clear(UiTheme.Canvas)
        g.SmoothingMode = SmoothingMode.AntiAlias
        Dim outer As New RectangleF(0.5F, 0.5F, ClientSize.Width - 1.5F, ClientSize.Height - 1.5F)
        Using path = UpsAtsView.RoundedRect(outer, 10), fill As New SolidBrush(UiTheme.Surface), border As New Pen(UiTheme.Border)
            g.FillPath(fill, path)
            g.DrawPath(border, path)
        End Using

        Dim enabledCards = _cards.Where(Function(card) card.Device.Enabled).ToList()
        Dim goodCards = enabledCards.Where(Function(card) card.Snapshot IsNot Nothing AndAlso card.Snapshot.IsGood).ToList()
        Dim kwValues = goodCards.Select(Function(card) card.Snapshot.AccountingOutputKw).Where(Function(item) item.HasValue).ToList()
        Dim kvaValues = goodCards.Select(Function(card) card.Snapshot.AccountingOutputKva).Where(Function(item) item.HasValue).ToList()
        Dim kw = kwValues.Sum(Function(item) item.Value)
        Dim kva = kvaValues.Sum(Function(item) item.Value)
        Dim isPartial = kwValues.Count < enabledCards.Count OrElse kvaValues.Count < enabledCards.Count
        Dim worst = If(_cards.Count = 0, AlarmSeverity.Normal, _cards.Max(Function(card) card.Severity))
        Dim installedKva = _cards.Where(Function(card) Double.IsFinite(card.Device.RatedKva) AndAlso card.Device.RatedKva > 0).
            Sum(Function(card) card.Device.RatedKva)

        Using titleFont As New Font("Segoe UI", 13.0F, FontStyle.Bold),
              smallFont As New Font("Segoe UI", 8.0F),
              smallBold As New Font("Segoe UI", 8.0F, FontStyle.Bold),
              totalFont As New Font("Segoe UI", 11.5F, FontStyle.Bold)
            Using accent As New SolidBrush(UpsAtsView.SeverityColor(worst))
                g.FillRectangle(accent, 12, 14, 4, 34)
            End Using
            TextRenderer.DrawText(g, Definition.Name, titleFont, New Point(22, 10), UiTheme.Ink)
            TextRenderer.DrawText(g, Definition.SourceDescription & "   •   Source: UNVERIFIED (no ATS telemetry)", smallFont,
                                  New Rectangle(23, 34, Math.Max(10, ClientSize.Width / 2), 16), UiTheme.MutedInk,
                                  TextFormatFlags.Left Or TextFormatFlags.EndEllipsis Or TextFormatFlags.SingleLine)
            Dim totalText = FormatHeaderPower(If(kwValues.Count > 0, CType(kw, Double?), Nothing), installedKva)
            Dim right As New Rectangle(ClientSize.Width / 2, 8, ClientSize.Width / 2 - 14, 24)
            TextRenderer.DrawText(g, totalText, totalFont, right, If(goodCards.Count = 0, UpsAtsView.NoDataGrey, UiTheme.Ink),
                                  TextFormatFlags.Right Or TextFormatFlags.SingleLine)
            Dim utilisation = If(kvaValues.Count = 0 OrElse installedKva <= 0, "—", (kva / installedKva * 100).ToString("0", inv) & " %")
            Dim note = goodCards.Count.ToString(inv) & "/" & enabledCards.Count.ToString(inv) & " reporting" & If(isPartial, " (PARTIAL)", "") &
                       "   •   measured " & If(kvaValues.Count = 0, "—", kva.ToString("#,##0.0", inv)) & " / " & installedKva.ToString("#,##0", inv) & " kVA UPS capacity" &
                       "   •   " & utilisation & " (" & kvaValues.Count.ToString(inv) & " VA sources)"
            TextRenderer.DrawText(g, note, If(isPartial, smallBold, smallFont), New Rectangle(right.X, 34, right.Width, 16),
                                  If(isPartial, UpsAtsView.WarnAmber, UiTheme.MutedInk), TextFormatFlags.Right Or TextFormatFlags.SingleLine)
        End Using

        If _cards.Count = 0 Then
            TextRenderer.DrawText(g, "No UPS registered on this ATS.", Font, New Rectangle(0, 60, ClientSize.Width, 40), UiTheme.MutedInk,
                                  TextFormatFlags.HorizontalCenter)
            Return
        End If
        Dim area As New Rectangle(12, 58, ClientSize.Width - 24, ClientSize.Height - 68)
        Dim slots = Math.Max(SlotCount, _cards.Count)
        Dim columns = If(slots <= 3, slots, CInt(Math.Ceiling(slots / 2.0R)))
        If area.Width / columns < 150 Then columns = Math.Max(1, area.Width \ 150)
        Dim rows = CInt(Math.Ceiling(slots / CDbl(columns)))
        Dim gap = 8
        Dim cardWidth = (area.Width - gap * (columns - 1)) \ columns
        Dim cardHeight = Math.Max(60, (area.Height - gap * (rows - 1)) \ rows)
        For index = 0 To _cards.Count - 1
            Dim card = _cards(index)
            card.Bounds = New Rectangle(area.X + (index Mod columns) * (cardWidth + gap), area.Y + (index \ columns) * (cardHeight + gap), cardWidth, cardHeight)
            PaintCard(g, card, index = _hover)
        Next
    End Sub

    Private Sub PaintCard(g As Graphics, card As CardState, hover As Boolean)
        Dim inv = CultureInfo.InvariantCulture
        Dim bounds = card.Bounds
        Dim snapshot = card.Snapshot
        Dim good = snapshot IsNot Nothing AndAlso snapshot.IsGood
        Dim quality = If(snapshot Is Nothing, If(card.Device.Enabled, UpsQuality.NoData, UpsQuality.Disabled), snapshot.Quality)
        Dim alarmed = card.Severity >= AlarmSeverity.Alarm
        Dim borderColor = If(alarmed, If(_flash, UpsAtsView.AlarmRed, Color.FromArgb(240, 180, 180)),
                          If(card.Severity = AlarmSeverity.PreAlarm, UpsAtsView.WarnAmber, If(hover, UiTheme.Blue, UiTheme.Border)))
        Dim background = If(alarmed AndAlso _flash, Color.FromArgb(253, 236, 236), If(good, UiTheme.SurfaceMuted, Color.FromArgb(244, 245, 247)))
        Using path = UpsAtsView.RoundedRect(New RectangleF(bounds.X, bounds.Y, bounds.Width, bounds.Height), 8),
              fill As New SolidBrush(background), border As New Pen(borderColor, If(alarmed OrElse card.Severity = AlarmSeverity.PreAlarm, 2.0F, 1.0F))
            g.FillPath(fill, path)
            g.DrawPath(border, path)
        End Using

        Using labelFont As New Font("Segoe UI", 11.0F, FontStyle.Bold),
              smallFont As New Font("Segoe UI", 7.5F),
              pillFont As New Font("Segoe UI", 7.0F, FontStyle.Bold),
              valueFont As New Font("Segoe UI", If(bounds.Height > 120, 16.0F, 13.0F), FontStyle.Bold),
              lineFont As New Font("Segoe UI", 8.0F),
              lineBold As New Font("Segoe UI", 8.0F, FontStyle.Bold)
            Dim x = bounds.X + 10
            Dim inner = bounds.Width - 20
            Dim compact = bounds.Height < 125
            Dim y = bounds.Y + If(compact, 5, 7)
            Dim label = card.Device.DisplayName
            TextRenderer.DrawText(g, label, labelFont, New Rectangle(x, y, inner - 70, 22), UiTheme.Ink,
                                  TextFormatFlags.Left Or TextFormatFlags.EndEllipsis Or TextFormatFlags.SingleLine)
            ' Quality pill
            Dim pillText = UpsQualityText.Describe(quality)
            Dim pillSize = TextRenderer.MeasureText(pillText, pillFont)
            Dim pill As New RectangleF(bounds.Right - 10 - pillSize.Width - 10, y + 2, pillSize.Width + 10, 17)
            Using pillPath = UpsAtsView.RoundedRect(pill, 8), pillBrush As New SolidBrush(UpsAtsView.QualityColor(quality))
                g.FillPath(pillBrush, pillPath)
            End Using
            TextRenderer.DrawText(g, pillText, pillFont, Rectangle.Round(pill), Color.White, TextFormatFlags.HorizontalCenter Or TextFormatFlags.VerticalCenter)
            y += If(compact, 20, 22)
            Dim identityFlag = If(card.Device.Verification = UpsVerification.Conflict, "  ⚠ ID conflict",
                               If(card.Device.Verification = UpsVerification.Provisional, "  ⚠ provisional", ""))
            If good AndAlso snapshot.PartialGroups.Count > 0 Then identityFlag &= "  ⚠ partial poll (" & String.Join(", ", snapshot.PartialGroups) & ")"
            TextRenderer.DrawText(g, card.Device.IpAddress & identityFlag, smallFont, New Rectangle(x, y, inner, 14),
                                  If(identityFlag.Length > 0, UpsAtsView.WarnAmber, UiTheme.MutedInk),
                                  TextFormatFlags.Left Or TextFormatFlags.EndEllipsis Or TextFormatFlags.SingleLine)
            y += If(compact, 14, 16)
            If Not good Then
                Dim message = If(quality = UpsQuality.Disabled, "Polling disabled",
                              If(snapshot Is Nothing, "Waiting for first poll…", snapshot.QualityDetail))
                Dim lastGood = If(snapshot IsNot Nothing AndAlso snapshot.LastGoodUtc.HasValue,
                                  "Last good " & snapshot.LastGoodUtc.Value.ToLocalTime().ToString("dd MMM HH:mm:ss", inv), "No good reading yet")
                TextRenderer.DrawText(g, "NO LIVE DATA", lineBold, New Rectangle(x, y + 2, inner, 16), UpsAtsView.QualityColor(quality), TextFormatFlags.Left)
                Dim messageTop = y + 19
                Dim messageLines = Math.Max(1, (bounds.Bottom - 22 - messageTop) \ 13)
                TextRenderer.DrawText(g, message, smallFont, New Rectangle(x, messageTop, inner, messageLines * 13), UiTheme.MutedInk,
                                      TextFormatFlags.Left Or TextFormatFlags.WordBreak Or TextFormatFlags.EndEllipsis)
                TextRenderer.DrawText(g, lastGood, smallFont, New Rectangle(x, bounds.Bottom - 20, inner, 14), UiTheme.MutedInk, TextFormatFlags.Left)
                Return
            End If
            Dim kwText = UpsAtsView.Fmt(snapshot.OutputKw, "0.0", " kW")
            TextRenderer.DrawText(g, kwText, valueFont, New Rectangle(x - 2, y, inner, 30), UiTheme.Ink, TextFormatFlags.Left Or TextFormatFlags.SingleLine)
            Dim pf = snapshot.PowerFactorDerived
            TextRenderer.DrawText(g, UpsAtsView.Fmt(snapshot.OutputKvaDerived, "0.0", " kVA") & "  •  PF " & UpsAtsView.Fmt(pf, "0.00"),
                                  smallFont, New Rectangle(x, y + 8, inner, 16), UiTheme.MutedInk, TextFormatFlags.Right Or TextFormatFlags.SingleLine)
            y += If(bounds.Height > 120, 32, 24)
            ' Load bar (highest phase)
            Dim load = snapshot.PhaseMax("OutputLoad")
            Dim barBounds As New Rectangle(x, y + 3, inner - 52, 8)
            Using back As New SolidBrush(Color.FromArgb(226, 233, 240))
                g.FillRectangle(back, barBounds)
            End Using
            If load.HasValue Then
                Using bar As New SolidBrush(UpsAtsView.LoadColor(load, _limits))
                    g.FillRectangle(bar, barBounds.X, barBounds.Y, CInt(barBounds.Width * Math.Min(1.0R, Math.Max(0, load.Value) / 100.0R)), barBounds.Height)
                End Using
            End If
            TextRenderer.DrawText(g, UpsAtsView.Fmt(load, "0", " %"), lineBold, New Rectangle(barBounds.Right + 4, y - 2, 48, 16),
                                  UpsAtsView.LoadColor(load, _limits), TextFormatFlags.Right Or TextFormatFlags.SingleLine)
            y += If(compact, 14, 16)
            If bounds.Bottom - y < 14 Then Return
            Dim source = snapshot.OutputSourceText
            Dim sourceValue = snapshot.Value("OutputSource")
            Dim sourceColor = If(sourceValue.HasValue AndAlso CInt(sourceValue.Value) = 3, UpsAtsView.OkGreen,
                              If(sourceValue.HasValue AndAlso CInt(sourceValue.Value) = 4, UpsAtsView.WarnAmber, UpsAtsView.AlarmRed))
            TextRenderer.DrawText(g, source, lineBold, New Rectangle(x, y, inner, 15), sourceColor, TextFormatFlags.Left Or TextFormatFlags.SingleLine)
            Dim voltageLabel = If(snapshot.OutputVoltageReference = "L-N", "V L-N ", "V ref? ")
            TextRenderer.DrawText(g, voltageLabel & UpsAtsView.Fmt(snapshot.PhaseAverage("OutputVoltage"), "0") & "  •  " &
                                  UpsAtsView.Fmt(snapshot.Value("OutputFrequency"), "0.0", " Hz"), lineFont,
                                  New Rectangle(x, y, inner, 15), UiTheme.MutedInk, TextFormatFlags.Right Or TextFormatFlags.SingleLine)
            y += If(compact, 14, 16)
            If bounds.Bottom - y < 14 Then Return
            TextRenderer.DrawText(g, "Battery " & snapshot.BatteryStatusText & "  " & UpsAtsView.Fmt(snapshot.Value("ChargePercent"), "0", " %") &
                                  "  •  " & UpsAtsView.Fmt(snapshot.Value("RuntimeMinutes"), "0", " min"), lineFont,
                                  New Rectangle(x, y, inner, 15), UiTheme.MutedInk, TextFormatFlags.Left Or TextFormatFlags.EndEllipsis Or TextFormatFlags.SingleLine)
            y += 16
            If card.AlarmText.Length > 0 AndAlso bounds.Bottom - y >= 16 Then
                Dim firstAlarm = card.AlarmText.Split({Environment.NewLine}, StringSplitOptions.None)(0)
                TextRenderer.DrawText(g, firstAlarm, lineBold, New Rectangle(x, y, inner, 15), UpsAtsView.SeverityColor(card.Severity),
                                      TextFormatFlags.Left Or TextFormatFlags.EndEllipsis Or TextFormatFlags.SingleLine)
            End If
        End Using
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then _toolTip.Dispose()
        MyBase.Dispose(disposing)
    End Sub
End Class
