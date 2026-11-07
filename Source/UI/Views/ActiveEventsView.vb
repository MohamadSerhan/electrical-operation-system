Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text

Public NotInheritable Class ActiveEventsView
    Inherits UserControl

    Private ReadOnly _read As Func(Of DateTime, ActiveAlarmSnapshot)
    Private ReadOnly _timer As New System.Windows.Forms.Timer With {.Interval = 2000}
    Private ReadOnly _group As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 155}
    Private ReadOnly _state As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 155}
    Private ReadOnly _search As New TextBox With {.Width = 220, .PlaceholderText = "Search equipment or condition"}
    Private ReadOnly _grid As New DataGridView
    Private ReadOnly _summary As New Label
    Private ReadOnly _coverage As New Label
    Private ReadOnly _message As New Label
    Private ReadOnly _detail As New TextBox With {.Multiline = True, .ReadOnly = True, .ScrollBars = ScrollBars.Vertical}
    Private ReadOnly _refresh As Button = UiTheme.ActionButton("Refresh", False, Icons.Refresh)
    Private ReadOnly _export As Button = UiTheme.ActionButton("Export CSV", True, Icons.Document)
    Private _snapshot As ActiveAlarmSnapshot
    Private _rows As IReadOnlyList(Of ActiveAlarmRow) = Array.Empty(Of ActiveAlarmRow)()
    Private _busy As Boolean
    Public Shared ReadOnly Headers As String() = {"Equipment", "Type", "Condition", "State", "Severity", "Last observed UTC", "Details"}

    Public Sub New(Optional read As Func(Of DateTime, ActiveAlarmSnapshot) = Nothing)
        _read = read
        Dock = DockStyle.Fill
        BackColor = UiTheme.Canvas
        Font = New Font("Segoe UI", 9.0F)
        Dim layout As New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 1, .RowCount = 7, .Padding = New Padding(10)}
        layout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        For Each rowHeight As Integer In {30, 76, 80, 32, 0, 76, 38}
            layout.RowStyles.Add(New RowStyle(If(rowHeight = 0, SizeType.Percent, SizeType.Absolute), If(rowHeight = 0, 100, rowHeight)))
        Next
        Dim heading = UiTheme.Label("ACTIVE EVENTS  |  Alarms, faults & monitoring status", New Font(Font, FontStyle.Bold), UiTheme.Ink)
        heading.Dock = DockStyle.Fill
        layout.Controls.Add(heading, 0, 0)
        Dim filters As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .WrapContents = True, .Margin = Padding.Empty}
        _group.Items.AddRange({"All equipment", "Transformers / ECG", "UPS", "Fire alarm", "Generators"})
        _group.SelectedIndex = 0
        _state.Items.AddRange({"Active + unknown", "Active only", "Unknown only"})
        _state.SelectedIndex = 0
        _refresh.Size = New Size(96, 30)
        _export.Size = New Size(108, 30)
        _export.Enabled = False
        For Each control As Control In {_group, _state, _search, _refresh, _export}
            control.Margin = New Padding(0, 4, 8, 4)
            filters.Controls.Add(control)
        Next
        layout.Controls.Add(filters, 0, 1)
        _coverage.Dock = DockStyle.Fill
        _coverage.ForeColor = UiTheme.MutedInk
        _coverage.Padding = New Padding(6)
        _coverage.BackColor = UiTheme.SurfaceMuted
        _coverage.Text = "Fire alarm and generators have no alarm/fault acquisition source. Fire connection diagnostics alone do not establish alarm monitoring. No normal state is assumed."
        layout.Controls.Add(_coverage, 0, 2)
        _summary.Dock = DockStyle.Fill
        _summary.TextAlign = ContentAlignment.MiddleLeft
        _summary.Font = New Font(Font, FontStyle.Bold)
        _summary.Text = "Waiting for current monitoring observations."
        layout.Controls.Add(_summary, 0, 3)
        _grid.Dock = DockStyle.Fill
        _grid.ReadOnly = True
        _grid.AllowUserToAddRows = False
        _grid.AllowUserToDeleteRows = False
        _grid.AllowUserToResizeRows = False
        _grid.RowHeadersVisible = False
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect
        _grid.MultiSelect = False
        _grid.BackgroundColor = UiTheme.Surface
        _grid.BorderStyle = BorderStyle.None
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal
        _grid.GridColor = UiTheme.Border
        _grid.EnableHeadersVisualStyles = False
        _grid.ColumnHeadersDefaultCellStyle.BackColor = UiTheme.SurfaceMuted
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = UiTheme.SurfaceMuted
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.Ink
        _grid.ColumnHeadersDefaultCellStyle.Font = New Font(Font, FontStyle.Bold)
        _grid.ColumnHeadersHeight = 38
        _grid.RowTemplate.Height = 32
        _grid.DefaultCellStyle.Padding = New Padding(5, 2, 5, 2)
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(224, 238, 252)
        _grid.DefaultCellStyle.SelectionForeColor = UiTheme.Ink
        Dim widths = {155, 94, 250, 85, 88, 188, 340}
        For index = 0 To Headers.Length - 1
            _grid.Columns.Add(New DataGridViewTextBoxColumn With {.Name = Headers(index), .HeaderText = Headers(index),
                .Width = widths(index), .SortMode = DataGridViewColumnSortMode.NotSortable})
        Next
        _grid.Columns("Condition").AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        _grid.Columns("Condition").MinimumWidth = 200
        _grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableAlwaysIncludeHeaderText
        layout.Controls.Add(_grid, 0, 4)
        _detail.Dock = DockStyle.Fill
        _detail.BackColor = UiTheme.Surface
        _detail.ForeColor = UiTheme.Ink
        _detail.Text = "Select a condition to read its source details. A confirmed clear removes it automatically; no manual reset is required."
        layout.Controls.Add(_detail, 0, 5)
        _message.Dock = DockStyle.Fill
        _message.ForeColor = UiTheme.MutedInk
        _message.Text = "Live view refreshes every 2 seconds while open. Raised/cleared history remains in Equipment History."
        layout.Controls.Add(_message, 0, 6)
        Controls.Add(layout)
        AddHandler _group.SelectedIndexChanged, Sub(sender, e) RenderSnapshot()
        AddHandler _state.SelectedIndexChanged, Sub(sender, e) RenderSnapshot()
        AddHandler _search.TextChanged, Sub(sender, e) RenderSnapshot()
        AddHandler _grid.SelectionChanged, Sub(sender, e) ShowDetails()
        AddHandler _refresh.Click, Async Sub(sender, e) Await RefreshRecordsAsync()
        AddHandler _timer.Tick, Async Sub(sender, e) Await RefreshRecordsAsync()
        AddHandler _export.Click, AddressOf ExportRecords
    End Sub

    Public Sub SetPageActive(active As Boolean)
        _timer.Enabled = active
        If active Then Call RefreshRecordsAsync()
    End Sub

    Public Async Function RefreshRecordsAsync() As Task
        If _busy OrElse IsDisposed Then Return
        _busy = True
        _refresh.Enabled = False
        Try
            If _read Is Nothing Then Throw New InvalidOperationException("Live alarm monitoring is not attached in this session.")
            Dim snapshot = Await Task.Run(Function() _read(DateTime.UtcNow))
            If snapshot Is Nothing Then Throw New InvalidDataException("Active alarm snapshot is unavailable.")
            If IsDisposed Then Return
            _snapshot = snapshot
            RenderSnapshot()
            _message.Text = "Snapshot " & snapshot.CheckedUtc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) &
                ". Clears reset automatically; unknown is not a clear. History: Equipment History / Alarm / Fault."
            _message.ForeColor = UiTheme.MutedInk
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidOperationException OrElse
            TypeOf ex Is ArgumentException OrElse TypeOf ex Is Microsoft.Data.Sqlite.SqliteException OrElse
            TypeOf ex Is System.Text.Json.JsonException OrElse TypeOf ex Is UnauthorizedAccessException
            If IsDisposed Then Return
            _snapshot = Nothing
            _rows = Array.Empty(Of ActiveAlarmRow)()
            _grid.Rows.Clear()
            _export.Enabled = False
            _summary.Text = "Active state UNAVAILABLE - not proof of normal."
            _coverage.Text = "Current source coverage could not be verified. Fire alarm and generators have no connected acquisition source."
            _detail.Text = ""
            _message.Text = "Could not read active conditions: " & ex.Message
            _message.ForeColor = UiTheme.Red
        Finally
            _busy = False
            If Not IsDisposed Then _refresh.Enabled = True
        End Try
    End Function

    Private Sub RenderSnapshot()
        If _snapshot Is Nothing Then Return
        Dim selected = If(_grid.CurrentRow Is Nothing OrElse _grid.CurrentRow.Index >= _rows.Count, Nothing, _rows(_grid.CurrentRow.Index))
        Dim group = CStr(_group.SelectedItem)
        Dim search = _search.Text.Trim()
        _rows = _snapshot.Rows.Where(Function(row)
                                        Return (group = "All equipment" OrElse (group = "Transformers / ECG" AndAlso row.EquipmentType = "Transformer") OrElse
                                            row.EquipmentType = group) AndAlso
                                            (_state.SelectedIndex = 0 OrElse row.State = If(_state.SelectedIndex = 1, "Active", "Unknown")) AndAlso
                                            (search.Length = 0 OrElse (row.EquipmentId & " " & row.EquipmentName & " " & row.Condition & " " & row.Detail).
                                                Contains(search, StringComparison.OrdinalIgnoreCase))
                                    End Function).ToArray()
        _grid.Rows.Clear()
        For Each row In _rows
            Dim index = _grid.Rows.Add(Cells(row))
            _grid.Rows(index).DefaultCellStyle.BackColor = If(row.State = "Unknown", Color.FromArgb(245, 246, 248),
                If(row.Category = "Fault", Color.FromArgb(255, 239, 239), Color.FromArgb(255, 248, 229)))
            For Each cell As DataGridViewCell In _grid.Rows(index).Cells
                cell.ToolTipText = row.Detail
            Next
        Next
        _grid.ClearSelection()
        If selected IsNot Nothing Then
            Dim index = _rows.ToList().FindIndex(Function(row) row.EquipmentType = selected.EquipmentType AndAlso
                row.EquipmentId = selected.EquipmentId AndAlso row.ConditionKey = selected.ConditionKey)
            If index >= 0 Then _grid.CurrentCell = _grid.Rows(index).Cells(0)
        End If
        _coverage.Text = _snapshot.Coverage
        _summary.Text = _rows.Where(Function(row) row.State = "Active").Count() & " active  |  " &
            _rows.Where(Function(row) row.State = "Unknown").Count() & " unknown / unconfirmed  |  " & _rows.Count & " shown"
        _export.Enabled = _rows.Count > 0
        If _rows.Count = 0 Then
            _detail.Text = If(group = "Fire alarm" OrElse group = "Generators",
                group & ": NOT CONNECTED. No acquisition source is configured; alarm state is unavailable, not normal.",
                "No matching active/unconfirmed conditions. This does not establish that every device signal is supported or healthy.")
        Else
            ShowDetails()
        End If
    End Sub

    Private Sub ShowDetails()
        If _grid.CurrentRow Is Nothing OrElse _grid.CurrentRow.Index >= _rows.Count Then Return
        Dim row = _rows(_grid.CurrentRow.Index)
        _detail.Text = row.EquipmentName & " [" & row.EquipmentId & "] | " & row.Category & " | " & row.Condition & " | " & row.State &
            Environment.NewLine & row.Detail
    End Sub

    Public Shared Function Cells(row As ActiveAlarmRow) As String()
        Return {If(row.EquipmentName = row.EquipmentId, row.EquipmentId, row.EquipmentName & " [" & row.EquipmentId & "]"),
            row.Category, row.Condition, row.State, row.Severity,
            If(row.ObservedUtc.HasValue, row.ObservedUtc.Value.ToString("yyyy-MM-dd HH:mm:ss.f", CultureInfo.InvariantCulture), ""),
            row.Detail}
    End Function

    Public Shared Sub ExportCsv(path As String, rows As IReadOnlyList(Of ActiveAlarmRow))
        If File.Exists(path) Then Throw New IOException("The export file already exists; choose a new filename.")
        Dim temporary = path & "." & Guid.NewGuid().ToString("N") & ".partial"
        Try
            Using writer As New StreamWriter(temporary, False, New UTF8Encoding(True))
                Dim csv = Function(values As IEnumerable(Of String)) String.Join(",", values.Select(
                    Function(value)
                        Dim safe = If(value, "")
                        If "=+-@".Contains(safe.TrimStart().FirstOrDefault()) Then safe = "'" & safe
                        Return """" & safe.Replace("""", """""") & """"
                    End Function))
                writer.WriteLine(csv(Headers))
                For Each row In rows
                    writer.WriteLine(csv(Cells(row)))
                Next
            End Using
            File.Move(temporary, path)
        Finally
            If File.Exists(temporary) Then File.Delete(temporary)
        End Try
    End Sub

    Private Sub ExportRecords(sender As Object, e As EventArgs)
        If _snapshot Is Nothing OrElse _rows.Count = 0 Then Return
        Using dialog As New SaveFileDialog With {.Filter = "CSV snapshot|*.csv",
            .FileName = "EOEMS_Active_Events_" & _snapshot.CheckedUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) & ".csv"}
            If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
            Try
                ExportCsv(dialog.FileName, _rows)
                _message.Text = "Displayed snapshot exported: " & dialog.FileName
                _message.ForeColor = UiTheme.Green
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is ArgumentException
                _message.Text = "Export failed: " & ex.Message
                _message.ForeColor = UiTheme.Red
            End Try
        End Using
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then _timer.Dispose()
        MyBase.Dispose(disposing)
    End Sub
End Class
