Imports System.Globalization

' Factory energy consumption: mains (ECG transformers) plus generators is the factory total. UPS, ATS and loads are fed from
' those sources, so they are listed as a breakdown and never added again. Generators and loads have no acquisition source yet;
' they stay "not connected" until rows of type Generator / Load appear in energy_shift.
Public NotInheritable Class FactoryEnergyView
    Inherits UserControl

    Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture
    Private ReadOnly _unit As New ComboBox()
    Private ReadOnly _summary As Label = UiTheme.Label("Loading energy…", New Font("Segoe UI", 10.0F, FontStyle.Bold), UiTheme.Ink, False)
    Private ReadOnly _shift As New ComboBox()
    Private ReadOnly _source As New ComboBox()
    Private ReadOnly _periods As New DataGridView()
    Private ReadOnly _breakdown As New DataGridView()
    Private ReadOnly _breakdownTitle As Label = UiTheme.Label("Breakdown", New Font("Segoe UI", 9.5F, FontStyle.Bold), UiTheme.Ink, False)
    Private ReadOnly _note As Label = UiTheme.Label(String.Empty, New Font("Segoe UI", 8.5F), UiTheme.Ink, False)
    Private ReadOnly _timer As New Timer With {.Interval = 30000}
    Private _cells As IReadOnlyList(Of EnergyAccounting.EnergyCell) = Array.Empty(Of EnergyAccounting.EnergyCell)()
    Private _loading As Boolean

    Private Shared ReadOnly Types As String() = {"Transformer", "Total", "UPS", "ATS", "Generator", "Load"}

    Public Sub New()
        Dock = DockStyle.Fill
        BackColor = UiTheme.Canvas
        Padding = New Padding(4)
        TransformerDetailForm.ConfigureGrid(_periods, {"Period", "Shift A kWh (06:00-18:00)", "Shift B kWh (18:00-06:00)", "Selected kWh",
                                                      "Factory total kWh", "Data coverage"})
        _periods.Columns(0).DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft
        _periods.Columns(0).FillWeight = 130
        _periods.Dock = DockStyle.Top
        _periods.Height = 300
        TransformerDetailForm.ConfigureGrid(_breakdown, {"Source", "Role", "Shift A kWh", "Shift B kWh", "Total kWh", "Note"})
        _breakdown.Columns(0).DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft
        _breakdown.Columns(1).DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft
        _breakdown.Columns(5).DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft
        _breakdown.Columns(5).FillWeight = 220
        _breakdown.Dock = DockStyle.Fill
        _unit.DropDownStyle = ComboBoxStyle.DropDownList
        _unit.Items.AddRange({"Per day", "Per week", "Per month", "Per year"})
        _unit.SelectedIndex = 0
        _unit.Dock = DockStyle.Right
        _unit.Width = 150
        _shift.DropDownStyle = ComboBoxStyle.DropDownList
        _shift.Items.AddRange({"All shifts", "Shift A", "Shift B"})
        _shift.SelectedIndex = 0
        _shift.Dock = DockStyle.Right
        _shift.Width = 110
        _source.DropDownStyle = ComboBoxStyle.DropDownList
        _source.Items.AddRange({"Total Factory", "Factory Loads", "Transformers", "ATSs", "UPSs"})
        _source.SelectedIndex = 0
        _source.Dock = DockStyle.Right
        _source.Width = 140
        AddHandler _unit.SelectedIndexChanged, Sub(sender, e) Reload()
        AddHandler _shift.SelectedIndexChanged, Sub(sender, e) Refilter()
        AddHandler _source.SelectedIndexChanged, Sub(sender, e) Refilter()
        AddHandler _periods.SelectionChanged, Sub(sender, e) FillBreakdown()
        AddHandler _timer.Tick, Sub(sender, e) Reload()
        _summary.Dock = DockStyle.Fill
        Dim header As New Panel With {.Dock = DockStyle.Top, .Height = 38, .Padding = New Padding(0, 4, 0, 4)}
        header.Controls.Add(_summary)
        header.Controls.Add(_unit)
        header.Controls.Add(_shift)
        header.Controls.Add(_source)
        _breakdownTitle.Dock = DockStyle.Top
        _breakdownTitle.Height = 30
        _note.Dock = DockStyle.Bottom
        _note.Height = 36
        _note.Text = "Factory total = mains (T1-T4) + generators. UPS, ATS and loads are supplied by those sources, so they are shown for information and are not added again. " &
            "Energy is integrated from the recorded kW and saved permanently per shift in PostgreSQL (A 06:00-18:00, B 18:00-06:00)."
        Controls.Add(_breakdown)
        Controls.Add(_breakdownTitle)
        Controls.Add(_note)
        Controls.Add(New Panel With {.Dock = DockStyle.Top, .Height = 8})
        Controls.Add(_periods)
        Controls.Add(header)
    End Sub

    Public Sub SetPageActive(active As Boolean)
        If active Then
            Reload()
            _timer.Start()
        Else
            _timer.Stop()
        End If
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then _timer.Dispose()
        MyBase.Dispose(disposing)
    End Sub

    Private Async Sub Reload()
        If _loading Then Return
        _loading = True
        Try
            Dim unit = EnergyAccounting.Units(Math.Max(0, _unit.SelectedIndex))
            Dim loaded = Await Task.Run(Function()
                                            Try
                                                EnergyAccounting.Recompute(1)
                                            Catch ex As Exception When IsExpected(ex)
                                            End Try
                                            Dim figures As New List(Of String)()
                                            For Each pair In {("Today", "day"), ("This week", "week"), ("This month", "month"), ("This year", "year")}
                                                figures.Add(pair.Item1 & " " & FactoryTotal(EnergyAccounting.ReadCells(pair.Item2, Types, 1)).ToString("#,##0", Inv) & " kWh")
                                            Next
                                            Return (Cells:=EnergyAccounting.ReadCells(unit, Types, 400), Figures:=figures)
                                        End Function)
            If IsDisposed Then Return
            _cells = loaded.Cells
            _summary.Text = "Factory consumption — " & String.Join("  •  ", loaded.Figures)
            FillPeriods(unit)
        Catch ex As Exception When IsExpected(ex)
            If Not IsDisposed Then _summary.Text = "Energy history unavailable: " & ex.Message
        Finally
            _loading = False
        End Try
    End Sub

    Private Shared Function IsExpected(ex As Exception) As Boolean
        Return TypeOf ex Is Npgsql.NpgsqlException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is TimeoutException OrElse
               TypeOf ex Is IO.IOException OrElse TypeOf ex Is Security.Cryptography.CryptographicException
    End Function

    Private Shared Function Sum(cells As IEnumerable(Of EnergyAccounting.EnergyCell), type As String, Optional shift As String = Nothing) As Double
        Return cells.Where(Function(cell) cell.EquipmentType = type AndAlso (shift Is Nothing OrElse cell.Shift = shift)).Sum(Function(cell) cell.Kwh)
    End Function

    Private Shared Function FactoryTotal(cells As IEnumerable(Of EnergyAccounting.EnergyCell)) As Double
        Return Sum(cells, "Total") + Sum(cells, "Generator")
    End Function

    Private Shared Function Kwh(value As Double) As String
        Return value.ToString("#,##0.0", Inv)
    End Function

    Private Sub Refilter()
        If _cells.Count = 0 AndAlso _periods.Rows.Count = 0 Then Return
        FillPeriods(EnergyAccounting.Units(Math.Max(0, _unit.SelectedIndex)))
    End Sub

    Private Function SelectedShift() As String
        Return If(_shift.SelectedIndex = 1, "A", If(_shift.SelectedIndex = 2, "B", Nothing))
    End Function

    Private Function SelectedTypes() As String()
        Select Case _source.SelectedIndex
            Case 1 : Return {"Load"}
            Case 2 : Return {"Transformer"}
            Case 3 : Return {"ATS"}
            Case 4 : Return {"UPS"}
            Case Else : Return {"Total", "Generator"}
        End Select
    End Function

    Private Function SumSelected(items As IEnumerable(Of EnergyAccounting.EnergyCell), shift As String) As String
        Dim types = SelectedTypes()
        Dim rows = items.Where(Function(cell) Array.IndexOf(types, cell.EquipmentType) >= 0 AndAlso (shift Is Nothing OrElse cell.Shift = shift)).ToArray()
        If rows.Length = 0 Then Return If(_source.SelectedIndex = 1, "not connected", "—")
        Return Kwh(rows.Sum(Function(cell) cell.Kwh))
    End Function

    Private Sub FillPeriods(unit As String)
        Dim selected As Date? = Nothing
        If _periods.CurrentRow IsNot Nothing AndAlso TypeOf _periods.CurrentRow.Tag Is Date Then selected = DirectCast(_periods.CurrentRow.Tag, Date)
        _periods.SuspendLayout()
        _periods.Rows.Clear()
        Dim shift = SelectedShift()
        For Each period In _cells.GroupBy(Function(cell) cell.Period).OrderByDescending(Function(group) group.Key)
            Dim items = period.ToArray()
            Dim index = _periods.Rows.Add(EnergyAccounting.PeriodLabel(unit, period.Key), SumSelected(items, "A"), SumSelected(items, "B"),
                SumSelected(items, shift), Kwh(FactoryTotal(items.Where(Function(cell) shift Is Nothing OrElse cell.Shift = shift))),
                EnergyAccounting.CoverageText(items.Where(Function(cell) cell.EquipmentType = "Total")))
            _periods.Rows(index).Tag = period.Key
            _periods.Rows(index).Cells(3).Style.Font = New Font("Segoe UI", 10.0F, FontStyle.Bold)
        Next
        _periods.ResumeLayout()
        If _periods.Rows.Count > 0 Then
            Dim target = _periods.Rows.Cast(Of DataGridViewRow)().FirstOrDefault(Function(row) selected.HasValue AndAlso TypeOf row.Tag Is Date AndAlso DirectCast(row.Tag, Date) = selected.Value)
            _periods.CurrentCell = If(target, _periods.Rows(0)).Cells(0)
        End If
        FillBreakdown()
    End Sub

    Private Sub FillBreakdown()
        _breakdown.Rows.Clear()
        If _periods.CurrentRow Is Nothing OrElse Not (TypeOf _periods.CurrentRow.Tag Is Date) Then Return
        Dim period = DirectCast(_periods.CurrentRow.Tag, Date)
        Dim shiftFilter = SelectedShift()
        Dim items = _cells.Where(Function(cell) cell.Period = period).ToArray()
        _breakdownTitle.Text = "Breakdown — " & _periods.CurrentRow.Cells(0).Value.ToString()
        Dim AddGroup = Sub(type As String, role As String, note As String)
                           For Each id In items.Where(Function(cell) cell.EquipmentType = type).Select(Function(cell) cell.EquipmentId).Distinct().OrderBy(Function(value) value, StringComparer.OrdinalIgnoreCase)
                               Dim rows = items.Where(Function(cell) cell.EquipmentType = type AndAlso cell.EquipmentId = id).ToArray()
                               _breakdown.Rows.Add(id, role, Kwh(rows.Where(Function(cell) cell.Shift = "A").Sum(Function(cell) cell.Kwh)),
                                                   Kwh(rows.Where(Function(cell) cell.Shift = "B").Sum(Function(cell) cell.Kwh)), Kwh(rows.Where(Function(cell) shiftFilter Is Nothing OrElse cell.Shift = shiftFilter).Sum(Function(cell) cell.Kwh)), note)
                           Next
                       End Sub
        Dim src = _source.SelectedIndex
        If src = 0 OrElse src = 2 Then AddGroup("Transformer", "Mains (ECG transformer)", "Counted in the factory total")
        If src = 0 Then
            If items.Any(Function(cell) cell.EquipmentType = "Generator") Then
                AddGroup("Generator", "Generator", "Counted in the factory total")
            Else
                _breakdown.Rows.Add("Generators G1-G12", "Generator", "—", "—", "—", "Not connected: no generator acquisition source, nothing is estimated")
            End If
        End If
        If src = 3 Then AddGroup("ATS", "ATS", "Sum of its downstream UPS (mains and generator share is not measured)")
        If src = 4 Then AddGroup("UPS", "UPS", "Included in the transformer energy")
        If src = 1 Then
            If items.Any(Function(cell) cell.EquipmentType = "Load") Then
                AddGroup("Load", "Load", "Included in the transformer energy")
            Else
                _breakdown.Rows.Add("Loads", "Load", "—", "—", "—", "Not connected: no load meter source")
            End If
        End If
    End Sub
End Class
