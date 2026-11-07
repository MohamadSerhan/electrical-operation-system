Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Threading.Tasks
Imports System.Windows.Forms

Public NotInheritable Class ConfigVersionsView
    Inherits UserControl

    Private ReadOnly _service As ConfigVersionService
    Private ReadOnly _canEdit As Boolean
    Private ReadOnly _restored As Action
    Private ReadOnly _grid As New DataGridView()
    Private ReadOnly _status As New Label()
    Private ReadOnly _refreshTimer As New System.Windows.Forms.Timer() With {.Interval = 5000}
    Private _pageActive As Boolean
    Private _selectNewest As Boolean

    Public Sub New(service As ConfigVersionService, session As UserSession, restored As Action)
        If service Is Nothing Then Throw New ArgumentNullException(NameOf(service))
        If session Is Nothing Then Throw New ArgumentNullException(NameOf(session))
        If restored Is Nothing Then Throw New ArgumentNullException(NameOf(restored))
        _service = service
        _restored = restored
        _canEdit = PlcConfigurationAccess.CanEdit(session.RoleName, session.IsOfflinePreview)
        BackColor = UiTheme.Canvas
        BuildLayout()
        AddHandler _refreshTimer.Tick, Sub(sender, e) RefreshList()
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then _refreshTimer.Dispose()
        MyBase.Dispose(disposing)
    End Sub

    Public Sub SetPageActive(active As Boolean)
        If _pageActive = active Then Return
        _pageActive = active
        If active Then
            _refreshTimer.Start()
            RefreshList()
        Else
            _refreshTimer.Stop()
        End If
    End Sub

    Private Sub BuildLayout()
        Dim root As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .BackColor = UiTheme.Canvas,
            .ColumnCount = 1,
            .RowCount = 3,
            .Padding = New Padding(2)}
        root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 30.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 64.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        Controls.Add(root)

        Dim subtitle = UiTheme.Label("Every change to the PLC, alarm, recording, UPS/SNMP and backup settings is saved as a version. Restore any earlier version; the default version is never deleted.",
            New Font("Segoe UI", 9.0F), UiTheme.MutedInk, False)
        subtitle.Dock = DockStyle.Fill
        subtitle.TextAlign = ContentAlignment.MiddleLeft
        subtitle.Padding = New Padding(6, 0, 0, 0)
        root.Controls.Add(subtitle, 0, 0)

        Dim actions As New FlowLayoutPanel With {
            .Dock = DockStyle.Fill,
            .WrapContents = True,
            .Padding = New Padding(4, 8, 4, 2),
            .BackColor = Color.Transparent}
        actions.Controls.Add(MakeButton("Save version now", False, Icons.Save, 156, AddressOf OnSaveClicked, True))
        actions.Controls.Add(MakeButton("Restore selected", True, Icons.Refresh, 160, AddressOf OnRestoreClicked, True))
        actions.Controls.Add(MakeButton("Delete selected", False, Icons.Cancel, 156, AddressOf OnDeleteClicked, True))
        actions.Controls.Add(MakeButton("Open folder", False, Icons.Folder, 138, AddressOf OnOpenFolderClicked, False))
        root.Controls.Add(actions, 0, 1)

        _grid.Dock = DockStyle.Fill
        _grid.ReadOnly = True
        _grid.AllowUserToAddRows = False
        _grid.AllowUserToDeleteRows = False
        _grid.AllowUserToResizeRows = False
        _grid.MultiSelect = False
        _grid.RowHeadersVisible = False
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        _grid.ColumnHeadersHeight = 30
        _grid.RowTemplate.Height = 29
        _grid.DefaultCellStyle.Font = New Font("Segoe UI", 9.0F)
        _grid.ColumnHeadersDefaultCellStyle.Font = New Font("Segoe UI", 8.5F, FontStyle.Bold)
        UiTheme.StyleReadOnlyGrid(_grid)
        _grid.Columns.Add("Created", "SAVED (UTC)")
        _grid.Columns.Add("Reason", "REASON")
        _grid.Columns.Add("Files", "FILES")
        _grid.Columns.Add("Size", "SIZE")
        _grid.Columns.Add("FilePath", "PATH")
        _grid.Columns("FilePath").Visible = False
        _grid.Columns("Created").FillWeight = 26
        _grid.Columns("Reason").FillWeight = 54
        _grid.Columns("Files").FillWeight = 8
        _grid.Columns("Size").FillWeight = 12

        _status.Dock = DockStyle.Bottom
        _status.Height = 26
        _status.Font = New Font("Segoe UI", 8.5F, FontStyle.Bold)
        _status.ForeColor = UiTheme.Ink
        _status.TextAlign = ContentAlignment.MiddleLeft

        Dim card As New RoundedPanel With {
            .Dock = DockStyle.Fill,
            .BackColor = UiTheme.Surface,
            .Padding = New Padding(12, 9, 12, 9),
            .Margin = New Padding(4)}
        card.Controls.Add(_grid)
        card.Controls.Add(_status)
        Dim heading = Icons.SectionHeading(Icons.SaveLocal, "SETTINGS VERSIONS")
        heading.Dock = DockStyle.Top
        card.Controls.Add(heading)
        root.Controls.Add(card, 0, 2)
        If Not _canEdit Then SetStatus("READ ONLY  |  Administrator role required to save, restore or delete versions.", UiTheme.Amber)
    End Sub

    Private Function MakeButton(text As String, primary As Boolean, glyph As String, width As Integer,
                                handler As EventHandler, needsEdit As Boolean) As Control
        Dim button = UiTheme.ActionButton(text, primary, glyph)
        button.Size = New Size(width, 36)
        button.Margin = New Padding(4)
        If needsEdit Then button.Enabled = _canEdit
        AddHandler button.Click, handler
        Return button
    End Function

    Private Function SelectedPath() As String
        If _grid.SelectedRows.Count = 0 Then Return String.Empty
        Return Convert.ToString(_grid.SelectedRows(0).Cells("FilePath").Value, CultureInfo.InvariantCulture)
    End Function

    Private Sub RefreshList()
        If IsDisposed OrElse Not _pageActive Then Return
        Try
            Dim selected = SelectedPath()
            Dim versions = _service.ListVersions()
            If VersionRowsMatch(versions) Then Return
            _grid.Rows.Clear()
            For Each version In versions
                Dim reason = If(version.IsDefault, "DEFAULT — protected. ", String.Empty) & version.Reason
                Dim row = _grid.Rows(_grid.Rows.Add(
                    version.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    reason,
                    version.FileCount.ToString(CultureInfo.InvariantCulture),
                    (version.SizeBytes / 1024.0R).ToString("0.0", CultureInfo.InvariantCulture) & " KiB",
                    version.FilePath))
                If version.IsDefault Then row.DefaultCellStyle.ForeColor = UiTheme.BlueDark
                If Not _selectNewest AndAlso String.Equals(version.FilePath, selected, StringComparison.OrdinalIgnoreCase) Then row.Selected = True
            Next
            If _grid.Rows.Count > 0 AndAlso (_selectNewest OrElse _grid.SelectedRows.Count = 0) Then
                _grid.ClearSelection()
                _grid.Rows(0).Selected = True
            End If
            _selectNewest = False
        Catch ex As Exception
            SetStatus("Settings versions could not be read: " & ex.Message, UiTheme.Red)
        End Try
    End Sub

    Private Function VersionRowsMatch(versions As IReadOnlyList(Of ConfigVersionInfo)) As Boolean
        If _grid.Rows.Count <> versions.Count Then Return False
        For index = 0 To versions.Count - 1
            Dim version = versions(index)
            Dim expected = {
                version.CreatedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                If(version.IsDefault, "DEFAULT — protected. ", String.Empty) & version.Reason,
                version.FileCount.ToString(CultureInfo.InvariantCulture),
                (version.SizeBytes / 1024.0R).ToString("0.0", CultureInfo.InvariantCulture) & " KiB",
                version.FilePath
            }
            For column = 0 To expected.Length - 1
                If Not String.Equals(Convert.ToString(_grid.Rows(index).Cells(column).Value, CultureInfo.InvariantCulture),
                                     expected(column), StringComparison.Ordinal) Then Return False
            Next
        Next
        Return True
    End Function

    Private Sub OnSaveClicked(sender As Object, e As EventArgs)
        Try
            Dim saved = _service.SaveVersion("Saved manually")
            _selectNewest = True
            SetStatus(If(saved Is Nothing, "Nothing changed since the latest version.", "Version saved: " & Path.GetFileName(saved)),
                      If(saved Is Nothing, UiTheme.Amber, UiTheme.Green))
            RefreshList()
        Catch ex As Exception
            SetStatus("Version was not saved: " & ex.Message, UiTheme.Red)
        End Try
    End Sub

    Private Sub OnRestoreClicked(sender As Object, e As EventArgs)
        Dim path = SelectedPath()
        If String.IsNullOrWhiteSpace(path) Then
            SetStatus("Select a version before restoring.", UiTheme.Amber)
            Return
        End If
        If MessageBox.Show(Me,
                           "Restore these settings? The current settings are saved as a new version first, so you can come back to them. The application will close; start it again to load the restored settings." &
                           Environment.NewLine & Environment.NewLine & System.IO.Path.GetFileName(path),
                           "Restore settings version", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                           MessageBoxDefaultButton.Button2) <> DialogResult.Yes Then Return
        Try
            Dim count = _service.RestoreVersion(path)
            SetStatus(count.ToString(CultureInfo.InvariantCulture) & " settings files restored.", UiTheme.Green)
            MessageBox.Show(Me, "Settings restored. The application will now close; start it again to apply them.",
                            "Settings restored", MessageBoxButtons.OK, MessageBoxIcon.Information)
            _restored()
        Catch ex As Exception
            SetStatus("Restore failed: " & ex.Message, UiTheme.Red)
        End Try
    End Sub

    Private Sub OnDeleteClicked(sender As Object, e As EventArgs)
        Dim path = SelectedPath()
        If String.IsNullOrWhiteSpace(path) Then
            SetStatus("Select a version before deleting.", UiTheme.Amber)
            Return
        End If
        If String.Equals(System.IO.Path.GetFileName(path), ConfigVersionService.DefaultFileName, StringComparison.OrdinalIgnoreCase) Then
            SetStatus("The default version is protected and cannot be deleted.", UiTheme.Amber)
            Return
        End If
        If MessageBox.Show(Me, "Delete this settings version permanently?" & Environment.NewLine & Environment.NewLine &
                           System.IO.Path.GetFileName(path), "Delete settings version", MessageBoxButtons.YesNo,
                           MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) <> DialogResult.Yes Then Return
        Try
            _service.DeleteVersion(path)
            SetStatus("Version deleted.", UiTheme.Green)
            RefreshList()
        Catch ex As Exception
            SetStatus("Delete failed: " & ex.Message, UiTheme.Red)
        End Try
    End Sub

    Private Sub OnOpenFolderClicked(sender As Object, e As EventArgs)
        Try
            Directory.CreateDirectory(_service.VersionsDirectory)
            Process.Start(New ProcessStartInfo("explorer.exe", """" & _service.VersionsDirectory & """") With {.UseShellExecute = True})
        Catch ex As Exception
            SetStatus("Folder could not be opened: " & ex.Message, UiTheme.Red)
        End Try
    End Sub

    Private Sub SetStatus(message As String, color As Color)
        _status.Text = message
        _status.ForeColor = color
    End Sub
End Class
