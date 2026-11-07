Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Windows.Forms

' Configuration › Alarm Limits: operator annunciation limits per equipment group.
Public NotInheritable Class AlarmSettingsView
    Inherits UserControl

    Private Shared ReadOnly PreAlarmInk As Color = Color.FromArgb(176, 112, 10)
    Private Shared ReadOnly AlarmInk As Color = Color.FromArgb(196, 40, 40)
    Private Shared ReadOnly ActiveInk As Color = Color.FromArgb(22, 128, 82)

    Private ReadOnly _session As UserSession
    Private ReadOnly _canEdit As Boolean
    Private ReadOnly _inputs As New Dictionary(Of String, NumericUpDown)(StringComparer.Ordinal)
    Private ReadOnly _message As Label
    Private ReadOnly _groups As (Key As String, Title As String, Chip As String, Active As Boolean)() = {
        ("Transformers", "TRANSFORMERS  (T1 – T4)", "ACTIVE  •  APPLIED TO EACH TRANSFORMER", True),
        ("TotalLoad", "TOTAL LOAD", "ACTIVE  •  TOTAL OF ALL TRANSFORMERS", True),
        ("Generators", "GENERATORS", "PREPARED  •  NOT CONNECTED YET", False),
        ("Ups", "UPS", "ACTIVE  •  APPLIED TO EACH UPS (SNMP)", True),
        ("Loads", "LOADS / FEEDERS", "PREPARED  •  NOT CONNECTED YET", False)
    }

    Public Sub New(session As UserSession)
        _session = session
        _canEdit = session IsNot Nothing AndAlso PlcConfigurationAccess.CanEdit(session.RoleName, session.IsOfflinePreview)
        BackColor = UiTheme.Canvas
        _message = UiTheme.Label(String.Empty, New Font("Segoe UI", 9.0F, FontStyle.Bold), UiTheme.MutedInk, False)

        Dim root As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 2,
            .BackColor = UiTheme.Canvas}
        root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 56.0F))
        Dim scroller As New Panel With {.Dock = DockStyle.Fill, .AutoScroll = True, .BackColor = UiTheme.Canvas}
        Dim layout As New TableLayoutPanel With {
            .Dock = DockStyle.Top,
            .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .ColumnCount = 1,
            .BackColor = Color.Transparent,
            .Padding = New Padding(2)}
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        layout.RowCount = 2
        layout.RowStyles.Add(New RowStyle(SizeType.Absolute, 30.0F))
        layout.RowStyles.Add(New RowStyle(SizeType.AutoSize))

        Dim heading = BuildHeading()
        layout.Controls.Add(heading, 0, 0)

        Dim groups As New FlowLayoutPanel With {
            .Dock = DockStyle.Top,
            .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .FlowDirection = FlowDirection.TopDown,
            .WrapContents = False,
            .BackColor = Color.Transparent,
            .Margin = Padding.Empty}
        Dim fitGroupWidths = Sub()
                                 For Each child As Control In groups.Controls
                                     child.Width = Math.Max(360, groups.ClientSize.Width - child.Margin.Horizontal)
                                 Next
                             End Sub
        AddHandler groups.Resize, Sub(sender, e) fitGroupWidths()
        AddHandler groups.ControlAdded, Sub(sender, e) e.Control.Width = Math.Max(360, groups.ClientSize.Width - e.Control.Margin.Horizontal)
        layout.Controls.Add(groups, 0, 1)
        Dim settings = AlarmSettingsStore.Current
        For index = 0 To _groups.Length - 1
            Dim card = BuildGroupCard(_groups(index), GetLimits(settings, _groups(index).Key))
            groups.Controls.Add(card)
        Next
        groups.Controls.Add(BuildRoomLimits(settings.Rooms))
        groups.Controls.Add(BuildLegendCard())
        fitGroupWidths()

        scroller.Controls.Add(layout)
        root.Controls.Add(scroller, 0, 0)
        root.Controls.Add(BuildActions(), 0, 1)
        Controls.Add(root)

        If Not String.IsNullOrEmpty(AlarmSettingsStore.LoadError) Then ShowMessage(AlarmSettingsStore.LoadError, AlarmInk)
    End Sub

    Private Const CardExpandedHeight As Integer = 200
    Private Const CardCollapsedHeight As Integer = 48

    Private Function BuildRoomLimits(limits As RoomTemperatureLimits) As Control
        Dim card As New RoundedPanel With {.Height = 200, .Padding = New Padding(12), .BackColor = UiTheme.Surface, .Margin = New Padding(4)}
        Dim body = BuildBlock("ROOM TEMPERATURE - map annunciation only", "Rooms", {
            ("LowAlarmC", "Low alarm", "C", limits.LowAlarmC, AlarmInk),
            ("LowWarningC", "Low warning", "C", limits.LowWarningC, PreAlarmInk),
            ("HighWarningC", "High warning", "C", limits.HighWarningC, PreAlarmInk),
            ("HighAlarmC", "High alarm", "C", limits.HighAlarmC, AlarmInk)})
        FillRoomInputs(limits)
        card.Controls.Add(body)
        Return card
    End Function

    ' Header click (title, chip or arrow) opens/closes the card so unused groups can be folded away.
    Private Shared Sub MakeCollapsible(card As Control, header As Control, body As Control, startExpanded As Boolean)
        CardCollapse.Attach(card, header, body)
    End Sub

    ' The Configuration page already shows the page title and access state; only the explanatory note remains here.
    Private Function BuildHeading() As Control
        Dim note = UiTheme.Label(
            "Operator warning limits shown on the live pages (not protection relay settings). Changes are role-controlled and audited.",
            New Font("Segoe UI", 9.0F), UiTheme.MutedInk, False)
        note.Dock = DockStyle.Fill
        note.TextAlign = ContentAlignment.MiddleLeft
        note.Margin = New Padding(6, 0, 4, 0)
        Return note
    End Function

    Private Function BuildGroupCard(group As (Key As String, Title As String, Chip As String, Active As Boolean),
                                    limits As EquipmentAlarmLimits) As Control
        Dim card As New RoundedPanel With {
            .BackColor = UiTheme.Surface,
            .Padding = New Padding(12, 8, 12, 8),
            .Margin = New Padding(4),
            .Height = CardExpandedHeight}
        Dim body As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 3,
            .RowCount = 1,
            .BackColor = Color.Transparent}
        body.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 30.0F))
        body.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 35.0F))
        body.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 35.0F))
        body.Controls.Add(BuildBlock("VOLTAGE L-N", group.Key, {
            ("UnderVoltageV", "Under voltage", "V", limits.UnderVoltageV, AlarmInk),
            ("OverVoltageV", "Over voltage", "V", limits.OverVoltageV, AlarmInk)}), 0, 0)
        body.Controls.Add(BuildBlock("LOAD", group.Key, {
            ("LoadPreAlarmPercent", "Pre-alarm", "%", limits.LoadPreAlarmPercent, PreAlarmInk),
            ("LoadAlarmPercent", "Alarm", "%", limits.LoadAlarmPercent, AlarmInk),
            ("LoadShedPercent", "Shed load", "%", limits.LoadShedPercent, AlarmInk)}), 1, 0)
        body.Controls.Add(BuildBlock("TEMPERATURE", group.Key, {
            ("TemperaturePreAlarmC", "Pre-alarm", "°C", limits.TemperaturePreAlarmC, PreAlarmInk),
            ("TemperatureAlarmC", "Over temp.", "°C", limits.TemperatureAlarmC, AlarmInk),
            ("TemperatureShedC", "Shed load", "°C", limits.TemperatureShedC, AlarmInk)}), 2, 0)
        card.Controls.Add(body)

        Dim header As New Panel With {.Dock = DockStyle.Top, .Height = 30, .BackColor = Color.Transparent}
        Dim title = Icons.SectionHeading(If(group.Active, Icons.Warning, Icons.Equipment), group.Title)
        title.Dock = DockStyle.Left
        header.Controls.Add(title)
        Dim chip = UiTheme.Label(group.Chip, New Font("Segoe UI", 7.5F, FontStyle.Bold),
                                 If(group.Active, ActiveInk, UiTheme.MutedInk), False)
        chip.TextAlign = ContentAlignment.MiddleRight
        chip.Width = 300
        chip.Dock = DockStyle.Right
        header.Controls.Add(chip)
        card.Controls.Add(header)
        MakeCollapsible(card, header, body, group.Active)
        Return card
    End Function

    Private Function BuildBlock(caption As String, groupKey As String,
                                fields As (Key As String, Caption As String, Unit As String, Value As Double, Ink As Color)()) As Control
        Dim block As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 3,
            .RowCount = fields.Length + 2,
            .BackColor = UiTheme.SurfaceMuted,
            .Margin = New Padding(3),
            .Padding = New Padding(6, 4, 6, 4)}
        block.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        block.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 64.0F))
        block.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 26.0F))
        block.RowStyles.Add(New RowStyle(SizeType.Absolute, 22.0F))
        Dim heading = UiTheme.Label(caption, New Font("Segoe UI", 8.0F, FontStyle.Bold), UiTheme.Blue, False)
        heading.Dock = DockStyle.Fill
        heading.TextAlign = ContentAlignment.MiddleLeft
        block.Controls.Add(heading, 0, 0)
        block.SetColumnSpan(heading, 3)
        For index = 0 To fields.Length - 1
            Dim field = fields(index)
            block.RowStyles.Add(New RowStyle(SizeType.Absolute, 32.0F))
            Dim label = UiTheme.Label(field.Caption, New Font("Segoe UI", 9.0F, FontStyle.Bold), field.Ink, False)
            label.Dock = DockStyle.Fill
            label.TextAlign = ContentAlignment.MiddleLeft
            block.Controls.Add(label, 0, index + 1)
            Dim input As New NumericUpDown With {
                .DecimalPlaces = 1,
                .Minimum = 0D,
                .Maximum = 100000D,
                .Increment = 0.1D,
                .Dock = DockStyle.Fill,
                .Margin = New Padding(0, 4, 2, 4),
                .Enabled = _canEdit,
                .BorderStyle = BorderStyle.FixedSingle,
                .BackColor = Color.White,
                .ForeColor = UiTheme.Ink,
                .Font = New Font("Segoe UI", 10.0F, FontStyle.Bold),
                .TextAlign = HorizontalAlignment.Right,
                .ThousandsSeparator = False}
            input.Value = ClampValue(field.Value)
            _inputs(groupKey & "." & field.Key) = input
            block.Controls.Add(input, 1, index + 1)
            Dim unit = UiTheme.Label(field.Unit, New Font("Segoe UI", 8.5F, FontStyle.Bold), UiTheme.MutedInk, False)
            unit.Dock = DockStyle.Fill
            unit.TextAlign = ContentAlignment.MiddleLeft
            block.Controls.Add(unit, 2, index + 1)
        Next
        block.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        Return block
    End Function

    Private Shared Function BuildLegendCard() As Control
        Dim card As New RoundedPanel With {
            .BackColor = UiTheme.Surface,
            .Padding = New Padding(14, 8, 14, 8),
            .Margin = New Padding(4),
            .Height = CardExpandedHeight}
        Dim lines As New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 2, .RowCount = 5, .BackColor = Color.Transparent}
        lines.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 120.0F))
        lines.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        AddLegendRow(lines, 0, "NORMAL", ActiveInk, "Value inside all limits.")
        AddLegendRow(lines, 1, "PRE-ALARM", PreAlarmInk, "Yellow badge and yellow value: load or temperature reached the pre-alarm limit.")
        AddLegendRow(lines, 2, "ALARM", AlarmInk, "Red badge flashing every 0.5 s: overload, over temperature, or voltage out of range.")
        AddLegendRow(lines, 3, "SHED LOAD", AlarmInk, "Red flashing SHED LOAD warning: reduce load immediately.")
        AddLegendRow(lines, 4, "TEMPERATURE", UiTheme.MutedInk, "Pre-alarm from the pre-alarm value; over temperature above the alarm value.")
        card.Controls.Add(lines)
        Dim title = Icons.SectionHeading(Icons.Info, "HOW ALARMS ARE SHOWN")
        Dim legendHeader As New Panel With {.Dock = DockStyle.Top, .Height = 30, .BackColor = Color.Transparent}
        title.Dock = DockStyle.Left
        legendHeader.Controls.Add(title)
        card.Controls.Add(legendHeader)
        MakeCollapsible(card, legendHeader, lines, True)
        Return card
    End Function

    Private Shared Sub AddLegendRow(table As TableLayoutPanel, row As Integer, name As String, ink As Color, text As String)
        table.RowStyles.Add(New RowStyle(SizeType.Percent, 20.0F))
        Dim tag = UiTheme.Label(name, New Font("Segoe UI", 8.5F, FontStyle.Bold), ink, False)
        tag.Dock = DockStyle.Fill
        tag.TextAlign = ContentAlignment.MiddleLeft
        table.Controls.Add(tag, 0, row)
        Dim description = UiTheme.Label(text, New Font("Segoe UI", 8.5F), UiTheme.Ink, False)
        description.Dock = DockStyle.Fill
        description.TextAlign = ContentAlignment.MiddleLeft
        table.Controls.Add(description, 1, row)
    End Sub

    Private Function BuildActions() As Control
        Dim row As New FlowLayoutPanel With {
            .Dock = DockStyle.Fill,
            .Height = 50,
            .FlowDirection = FlowDirection.LeftToRight,
            .WrapContents = False,
            .BackColor = Color.Transparent,
            .Padding = New Padding(4, 8, 4, 0)}
        If _canEdit Then
            Dim save = UiTheme.ActionButton("Save alarm limits", True, Icons.Save)
            save.Size = New Size(170, 36)
            save.Margin = New Padding(0, 0, 8, 0)
            AddHandler save.Click, AddressOf OnSaveClicked
            row.Controls.Add(save)
            Dim defaults = UiTheme.ActionButton("Restore defaults", False, Icons.Refresh)
            defaults.Size = New Size(160, 36)
            defaults.Margin = New Padding(0, 0, 12, 0)
            AddHandler defaults.Click, Sub(sender, e)
                                           FillInputs(New AlarmSettings())
                                           ShowMessage("Default limits loaded. Press Save alarm limits to apply them.", PreAlarmInk)
                                       End Sub
            row.Controls.Add(defaults)
        End If
        _message.Size = New Size(760, 34)
        _message.TextAlign = ContentAlignment.MiddleLeft
        row.Controls.Add(_message)
        Return row
    End Function

    Private Sub FillInputs(settings As AlarmSettings)
        FillRoomInputs(settings.Rooms)
        For Each group In _groups
            Dim limits = GetLimits(settings, group.Key)
            SetInput(group.Key, "UnderVoltageV", limits.UnderVoltageV)
            SetInput(group.Key, "OverVoltageV", limits.OverVoltageV)
            SetInput(group.Key, "LoadPreAlarmPercent", limits.LoadPreAlarmPercent)
            SetInput(group.Key, "LoadAlarmPercent", limits.LoadAlarmPercent)
            SetInput(group.Key, "LoadShedPercent", limits.LoadShedPercent)
            SetInput(group.Key, "TemperaturePreAlarmC", limits.TemperaturePreAlarmC)
            SetInput(group.Key, "TemperatureAlarmC", limits.TemperatureAlarmC)
            SetInput(group.Key, "TemperatureShedC", limits.TemperatureShedC)
        Next
    End Sub

    Private Sub FillRoomInputs(limits As RoomTemperatureLimits)
        Dim fields = {("LowAlarmC", limits.LowAlarmC), ("LowWarningC", limits.LowWarningC), ("HighWarningC", limits.HighWarningC), ("HighAlarmC", limits.HighAlarmC)}
        For Each field In fields
            Dim input = _inputs("Rooms." & field.Item1)
            input.Minimum = -50
            input.Maximum = 150
            input.Value = CDec(field.Item2)
        Next
    End Sub

    Private Shared Function ClampValue(value As Double) As Decimal
        If Double.IsNaN(value) OrElse Double.IsInfinity(value) Then Return 0D
        Return CDec(Math.Max(0.0R, Math.Min(100000.0R, value)))
    End Function

    Private Sub SetInput(groupKey As String, fieldKey As String, value As Double)
        _inputs(groupKey & "." & fieldKey).Value = ClampValue(value)
    End Sub

    Private Shared Function GetLimits(settings As AlarmSettings, key As String) As EquipmentAlarmLimits
        Select Case key
            Case "Transformers" : Return settings.Transformers
            Case "TotalLoad" : Return settings.TotalLoad
            Case "Generators" : Return settings.Generators
            Case "Ups" : Return settings.Ups
            Case Else : Return settings.Loads
        End Select
    End Function

    Private Sub OnSaveClicked(sender As Object, e As EventArgs)
        Dim candidate As New AlarmSettings()
        candidate.Rooms.LowAlarmC = CDbl(_inputs("Rooms.LowAlarmC").Value)
        candidate.Rooms.LowWarningC = CDbl(_inputs("Rooms.LowWarningC").Value)
        candidate.Rooms.HighWarningC = CDbl(_inputs("Rooms.HighWarningC").Value)
        candidate.Rooms.HighAlarmC = CDbl(_inputs("Rooms.HighAlarmC").Value)
        For Each group In _groups
            Dim limits = GetLimits(candidate, group.Key)
            Dim ok = TryRead(group, "UnderVoltageV", Sub(v) limits.UnderVoltageV = v) AndAlso
                     TryRead(group, "OverVoltageV", Sub(v) limits.OverVoltageV = v) AndAlso
                     TryRead(group, "LoadPreAlarmPercent", Sub(v) limits.LoadPreAlarmPercent = v) AndAlso
                     TryRead(group, "LoadAlarmPercent", Sub(v) limits.LoadAlarmPercent = v) AndAlso
                     TryRead(group, "LoadShedPercent", Sub(v) limits.LoadShedPercent = v) AndAlso
                     TryRead(group, "TemperaturePreAlarmC", Sub(v) limits.TemperaturePreAlarmC = v) AndAlso
                     TryRead(group, "TemperatureAlarmC", Sub(v) limits.TemperatureAlarmC = v) AndAlso
                     TryRead(group, "TemperatureShedC", Sub(v) limits.TemperatureShedC = v)
            If Not ok Then Return
        Next
        Try
            AlarmSettingsStore.Save(candidate, _session.Username, _session.RoleName, _session.IsOfflinePreview)
            ShowMessage("Alarm limits saved " & DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) &
                        ". The live pages use them immediately.", ActiveInk)
        Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is IOException
            ShowMessage(ex.Message, AlarmInk)
        End Try
    End Sub

    Private Function TryRead(group As (Key As String, Title As String, Chip As String, Active As Boolean),
                             fieldKey As String, assign As Action(Of Double)) As Boolean
        Dim input = _inputs(group.Key & "." & fieldKey)
        Dim value As Double = CDbl(input.Value)
        assign(value)
        Return True
    End Function

    Private Sub ShowMessage(text As String, ink As Color)
        _message.Text = text
        _message.ForeColor = ink
    End Sub
End Class
