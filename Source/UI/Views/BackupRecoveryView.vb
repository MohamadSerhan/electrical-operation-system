Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Security
Imports System.Threading.Tasks
Imports System.Windows.Forms

Public NotInheritable Class BackupRecoveryView
    Inherits UserControl

    Private ReadOnly _session As UserSession
    Private ReadOnly _service As BackupRecoveryService
    Private ReadOnly _saveSettings As Func(Of BackupSettings, Task)
    Private ReadOnly _prepareRestore As Func(Of String, Task(Of RestorePreparationResult))
    Private ReadOnly _restorePrepared As Action
    Private ReadOnly _canEdit As Boolean
    Private ReadOnly _automatic As New CheckBox()
    Private ReadOnly _folder As New TextBox()
    Private ReadOnly _backupTime As New TextBox()
    Private ReadOnly _retention As New TextBox()
    Private ReadOnly _status As New Label()
    Private ReadOnly _backups As New DataGridView()
    Private ReadOnly _lastBackupMetric As New Label()
    Private ReadOnly _nextBackupMetric As New Label()
    Private ReadOnly _archiveCountMetric As New Label()
    Private ReadOnly _retentionMetric As New Label()
    Private ReadOnly _refreshTimer As New System.Windows.Forms.Timer()
    Private _settings As BackupSettings
    Private _busy As Boolean
    Private _pageActive As Boolean
    Private _refreshingBackups As Boolean
    Private _selectNewest As Boolean

    Public Sub New(settings As BackupSettings,
                   session As UserSession,
                   service As BackupRecoveryService,
                   saveSettings As Func(Of BackupSettings, Task),
                   prepareRestore As Func(Of String, Task(Of RestorePreparationResult)),
                   restorePrepared As Action)
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        If session Is Nothing Then Throw New ArgumentNullException(NameOf(session))
        If service Is Nothing Then Throw New ArgumentNullException(NameOf(service))
        If saveSettings Is Nothing Then Throw New ArgumentNullException(NameOf(saveSettings))
        If prepareRestore Is Nothing Then Throw New ArgumentNullException(NameOf(prepareRestore))
        If restorePrepared Is Nothing Then Throw New ArgumentNullException(NameOf(restorePrepared))
        _settings = settings
        _session = session
        _service = service
        _saveSettings = saveSettings
        _prepareRestore = prepareRestore
        _restorePrepared = restorePrepared
        _canEdit = PlcConfigurationAccess.CanEdit(session.RoleName, session.IsOfflinePreview)
        BackColor = UiTheme.Canvas
        BuildModernLayout()
        _refreshTimer.Interval = 5000
        AddHandler _refreshTimer.Tick, Sub(sender, e) RefreshStatus()
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
            RefreshStatus()
        Else
            _refreshTimer.Stop()
        End If
    End Sub


    Private Sub BuildModernLayout()
        Dim root As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .BackColor = UiTheme.Canvas,
            .ColumnCount = 1,
            .RowCount = 5,
            .Padding = New Padding(2),
            .GrowStyle = TableLayoutPanelGrowStyle.FixedSize}
        root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 30.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 110.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 178.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 130.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        Controls.Add(root)

        Dim heading As New Panel With {.Dock = DockStyle.Fill, .BackColor = Color.Transparent}
        Dim subtitle = UiTheme.Label("Verified local recovery points with explicit restore scope. Authentication data is excluded.",
            New Font("Segoe UI", 9.0F), UiTheme.MutedInk, False)
        subtitle.Dock = DockStyle.Fill
        subtitle.TextAlign = ContentAlignment.MiddleLeft
        subtitle.Padding = New Padding(6, 0, 0, 0)
        heading.Controls.Add(subtitle)
        root.Controls.Add(heading, 0, 0)

        Dim metrics As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 4,
            .RowCount = 1,
            .Padding = New Padding(4),
            .BackColor = Color.Transparent}
        For index = 0 To 3
            metrics.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 25.0F))
        Next
        metrics.Controls.Add(CreateMetricCell("LAST SUCCESS", _lastBackupMetric), 0, 0)
        metrics.Controls.Add(CreateMetricCell("BACKUP MODE", _nextBackupMetric), 1, 0)
        metrics.Controls.Add(CreateMetricCell("ARCHIVES RETAINED", _archiveCountMetric), 2, 0)
        metrics.Controls.Add(CreateMetricCell("OLD BACKUPS", _retentionMetric), 3, 0)
        root.Controls.Add(CreateBackupCard("BACKUP HEALTH", metrics), 0, 1)

        Dim policyContent As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 3,
            .RowCount = 4,
            .Padding = New Padding(8, 2, 8, 6),
            .BackColor = Color.Transparent}
        policyContent.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 56.0F))
        policyContent.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 22.0F))
        policyContent.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 22.0F))
        policyContent.RowStyles.Add(New RowStyle(SizeType.Absolute, 31.0F))
        policyContent.RowStyles.Add(New RowStyle(SizeType.Absolute, 18.0F))
        policyContent.RowStyles.Add(New RowStyle(SizeType.Absolute, 30.0F))
        policyContent.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))

        _automatic.Text = "Automatically back up before an application update"
        _automatic.Checked = _settings.AutomaticBackupEnabled
        _automatic.Enabled = _canEdit
        _automatic.ForeColor = UiTheme.Ink
        _automatic.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        _automatic.Dock = DockStyle.Fill
        policyContent.Controls.Add(_automatic, 0, 0)
        policyContent.SetColumnSpan(_automatic, 3)

        AddPolicyCaption(policyContent, "BACKUP FOLDER", 0, 1)
        ConfigurePolicyInput(_folder, _settings.BackupFolderPath, _canEdit)
        policyContent.Controls.Add(_folder, 0, 2)
        policyContent.SetColumnSpan(_folder, 3)

        Dim warning = UiTheme.Label("Backups are made only when you press ""Back up now"" or automatically once after a new version is installed. Old backups are never deleted automatically.",
            New Font("Segoe UI", 8.0F, FontStyle.Bold), UiTheme.Amber, False)
        warning.Dock = DockStyle.Fill
        warning.TextAlign = ContentAlignment.MiddleLeft
        policyContent.Controls.Add(warning, 0, 3)
        policyContent.SetColumnSpan(warning, 3)
        root.Controls.Add(CreateBackupCard("BACKUP POLICY", policyContent), 0, 2)

        Dim actions As New FlowLayoutPanel With {
            .Dock = DockStyle.Fill,
            .FlowDirection = FlowDirection.LeftToRight,
            .WrapContents = True,
            .Padding = New Padding(8, 5, 8, 3),
            .BackColor = Color.Transparent}
        Dim saveButton = UiTheme.ActionButton("Save policy", False, Icons.Save)
        saveButton.Size = New Size(132, 36)
        saveButton.Margin = New Padding(4)
        saveButton.Enabled = _canEdit
        AddHandler saveButton.Click, AddressOf OnSaveClicked
        actions.Controls.Add(saveButton)
        Dim backupButton = UiTheme.ActionButton("Back up now", True, Icons.Database)
        backupButton.Size = New Size(142, 36)
        backupButton.Margin = New Padding(4)
        backupButton.Enabled = _canEdit AndAlso Not _session.IsOfflinePreview
        AddHandler backupButton.Click, AddressOf OnBackupClicked
        actions.Controls.Add(backupButton)
        Dim verifyButton = UiTheme.ActionButton("Verify selected", False, Icons.CheckCircle)
        verifyButton.Size = New Size(156, 36)
        verifyButton.Margin = New Padding(4)
        verifyButton.Enabled = _canEdit
        AddHandler verifyButton.Click, AddressOf OnVerifyClicked
        actions.Controls.Add(verifyButton)
        Dim restoreButton = UiTheme.ActionButton("Restore selected", False, Icons.Refresh)
        restoreButton.Size = New Size(156, 36)
        restoreButton.Margin = New Padding(4)
        restoreButton.Enabled = _canEdit AndAlso Not _session.IsOfflinePreview
        AddHandler restoreButton.Click, AddressOf OnReviewRestoreClicked
        actions.Controls.Add(restoreButton)
        Dim deleteButton = UiTheme.ActionButton("Delete selected", False, Icons.Cancel)
        deleteButton.Size = New Size(156, 36)
        deleteButton.Margin = New Padding(4)
        deleteButton.Enabled = _canEdit
        AddHandler deleteButton.Click, AddressOf OnDeleteClicked
        actions.Controls.Add(deleteButton)
        Dim openFolderButton = UiTheme.ActionButton("Open folder", False, Icons.Folder)
        openFolderButton.Size = New Size(138, 36)
        openFolderButton.Margin = New Padding(4)
        AddHandler openFolderButton.Click, AddressOf OnOpenFolderClicked
        actions.Controls.Add(openFolderButton)
        root.Controls.Add(CreateBackupCard("ACTIONS", actions), 0, 3)

        _backups.Dock = DockStyle.Fill
        _backups.ReadOnly = True
        _backups.AllowUserToAddRows = False
        _backups.AllowUserToDeleteRows = False
        _backups.AllowUserToResizeRows = False
        _backups.MultiSelect = False
        _backups.SelectionMode = DataGridViewSelectionMode.FullRowSelect
        _backups.RowHeadersVisible = False
        _backups.BackgroundColor = UiTheme.Surface
        _backups.BorderStyle = BorderStyle.FixedSingle
        _backups.DefaultCellStyle.BackColor = UiTheme.Surface
        _backups.DefaultCellStyle.ForeColor = UiTheme.Ink
        _backups.DefaultCellStyle.SelectionBackColor = UiTheme.BlueDark
        _backups.DefaultCellStyle.SelectionForeColor = Color.White
        _backups.ColumnHeadersDefaultCellStyle.BackColor = UiTheme.SurfaceMuted
        _backups.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.Ink
        _backups.ColumnHeadersDefaultCellStyle.Font = New Font("Segoe UI", 8.5F, FontStyle.Bold)
        _backups.ColumnHeadersHeight = 30
        _backups.DefaultCellStyle.Font = New Font("Segoe UI", 9.0F)
        _backups.RowTemplate.Height = 29
        UiTheme.StyleReadOnlyGrid(_backups)
        _backups.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        If _backups.Columns.Count = 0 Then
            _backups.Columns.Add("FileName", "BACKUP FILE")
            _backups.Columns.Add("Timestamp", "LAST MODIFIED (UTC)")
            _backups.Columns.Add("Size", "SIZE")
            _backups.Columns.Add("FilePath", "PATH")
        End If
        _backups.Columns("FilePath").Visible = False

        Dim archiveContent As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 2,
            .Padding = New Padding(6, 2, 6, 6),
            .BackColor = Color.Transparent}
        archiveContent.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        archiveContent.RowStyles.Add(New RowStyle(SizeType.Absolute, 42.0F))
        archiveContent.Controls.Add(_backups, 0, 0)
        _status.Dock = DockStyle.Fill
        _status.Font = New Font("Segoe UI", 8.5F, FontStyle.Bold)
        _status.ForeColor = UiTheme.Ink
        _status.TextAlign = ContentAlignment.MiddleLeft
        archiveContent.Controls.Add(_status, 0, 1)
        root.Controls.Add(CreateBackupCard("AVAILABLE BACKUP VERSIONS", archiveContent), 0, 4)

        If Not _canEdit Then
            SetStatus("READ ONLY  |  Administrator role required to change backup settings or run backup / restore actions.", UiTheme.Amber)
        End If
    End Sub

    Private Shared Function CreateBackupCard(title As String, content As Control) As RoundedPanel
        Dim card As New RoundedPanel With {
            .Dock = DockStyle.Fill,
            .BackColor = UiTheme.Canvas,
            .BorderColor = UiTheme.Canvas,
            .Padding = New Padding(12, 9, 12, 9),
            .Margin = New Padding(4)}
        content.Dock = DockStyle.Fill
        card.Controls.Add(content)
        Dim heading = Icons.SectionHeading(Icons.SaveLocal, title)
        heading.Dock = DockStyle.Top
        card.Controls.Add(heading)
        Return card
    End Function

    Private Shared Sub AddPolicyCaption(parent As TableLayoutPanel, text As String, column As Integer, row As Integer)
        Dim label = UiTheme.Label(text, New Font("Segoe UI", 8.0F, FontStyle.Bold), UiTheme.MutedInk, False)
        label.Dock = DockStyle.Fill
        label.TextAlign = ContentAlignment.MiddleLeft
        parent.Controls.Add(label, column, row)
    End Sub

    Private Shared Sub ConfigurePolicyInput(input As TextBox, value As String, enabled As Boolean)
        input.Text = value
        input.Dock = DockStyle.Fill
        input.ReadOnly = Not enabled
        input.BorderStyle = BorderStyle.FixedSingle
        input.BackColor = If(enabled, Color.White, Color.FromArgb(229, 236, 245))
        input.ForeColor = UiTheme.Ink
        input.Font = New Font("Segoe UI", 9.5F)
        input.Margin = New Padding(3, 0, 6, 3)
    End Sub

    Private Shared Function CreateMetricCell(caption As String, value As Label) As Control
        Dim cell As New Panel With {.Dock = DockStyle.Fill, .Margin = New Padding(4, 0, 10, 0), .BackColor = UiTheme.SurfaceMuted}
        Dim title = UiTheme.Label(caption, New Font("Segoe UI", 8.0F, FontStyle.Bold), UiTheme.MutedInk, False)
        title.Location = New Point(8, 3)
        title.Size = New Size(180, 14)
        title.AutoEllipsis = True
        cell.Controls.Add(title)
        value.Location = New Point(8, 20)
        value.Size = New Size(180, 30)
        value.Font = New Font("Segoe UI", 10.0F, FontStyle.Bold)
        value.ForeColor = UiTheme.Ink
        value.AutoEllipsis = True
        cell.Controls.Add(value)
        AddHandler cell.Resize, Sub(sender, e)
                                    title.Width = Math.Max(0, cell.ClientSize.Width - 16)
                                    value.Width = Math.Max(0, cell.ClientSize.Width - 16)
                                End Sub
        Return cell
    End Function

    Private Async Sub OnSaveClicked(sender As Object, e As EventArgs)
        If Not _canEdit OrElse _busy Then Return
        Try
            Dim candidate As New BackupSettings With {
                .AutomaticBackupEnabled = _automatic.Checked,
                .BackupFolderPath = _folder.Text.Trim(),
                .DailyBackupTime = _settings.DailyBackupTime,
                .RetentionDays = _settings.RetentionDays
            }
            candidate.Validate()
            Await _saveSettings(candidate)
            _settings = candidate
            SetStatus("Backup policy saved. Authentication data remains excluded.", UiTheme.Green)
            RefreshStatus()
        Catch ex As Exception
            SetStatus("Backup settings were not saved: " & ex.Message, UiTheme.Red)
        End Try
    End Sub

    Private Async Sub OnBackupClicked(sender As Object, e As EventArgs)
        If Not _canEdit OrElse _session.IsOfflinePreview OrElse _busy Then Return
        Await RunBackupActionAsync(
            Async Function() As Task
                Dim path = Await _service.CreateBackupAsync()
                _selectNewest = True
                SetStatus("Backup created and verified: " & path, UiTheme.Green)
            End Function)
    End Sub

    Private Async Sub OnVerifyClicked(sender As Object, e As EventArgs)
        If Not _canEdit OrElse _busy Then Return
        Dim path = GetSelectedBackupPath()
        If String.IsNullOrWhiteSpace(path) Then
            SetStatus("Select a backup before verifying it.", UiTheme.Amber)
            Return
        End If
        Await RunBackupActionAsync(
            Async Function() As Task
                Dim result = Await _service.VerifyBackupAsync(path)
                SetStatus("VERIFIED • " & result.Message & " • " &
                          String.Join(", ", result.PayloadEntries), UiTheme.Green)
            End Function)
    End Sub

    Private Async Sub OnReviewRestoreClicked(sender As Object, e As EventArgs)
        If Not _canEdit OrElse _session.IsOfflinePreview OrElse _busy Then Return
        Dim path = GetSelectedBackupPath()
        If String.IsNullOrWhiteSpace(path) Then
            SetStatus("Select a backup before restoring.", UiTheme.Amber)
            Return
        End If
        Dim confirmation = MessageBox.Show(
            Me,
            "This operation will replace the current historian database and MeterLogs with the selected backup. Current app/PLC settings and the authentication database will not be changed. A verified rollback archive will be created first; the application will close, then apply the restore before the next sign-in." &
            Environment.NewLine & Environment.NewLine & System.IO.Path.GetFileName(path) & Environment.NewLine & Environment.NewLine &
            "Continue?",
            "Confirm historian and MeterLogs restore",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2)
        If confirmation <> DialogResult.Yes Then Return
        Await RunBackupActionAsync(
            Async Function() As Task
                Dim result = Await _prepareRestore(path)
                SetStatus(result.Message & " Rollback archive: " & result.RollbackBackupPath, UiTheme.Green)
                MessageBox.Show(Me,
                                "Restore is staged and the verified rollback archive is ready. The application will now close. Start it again to apply the restore before historian or MeterLogs are opened.",
                                "Restore staged",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Information)
                _restorePrepared()
            End Function)
    End Sub

    Private Async Sub OnDeleteClicked(sender As Object, e As EventArgs)
        If Not _canEdit OrElse _busy Then Return
        Dim path = GetSelectedBackupPath()
        If String.IsNullOrWhiteSpace(path) Then
            SetStatus("Select a backup before deleting it.", UiTheme.Amber)
            Return
        End If
        If MessageBox.Show(Me, "Delete this backup permanently?" & Environment.NewLine & Environment.NewLine &
                           System.IO.Path.GetFileName(path), "Delete backup", MessageBoxButtons.YesNo,
                           MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) <> DialogResult.Yes Then Return
        Await RunBackupActionAsync(
            Function() As Task
                _service.DeleteBackup(path)
                SetStatus("Backup deleted: " & System.IO.Path.GetFileName(path), UiTheme.Green)
                Return Task.CompletedTask
            End Function)
    End Sub

    Private Sub OnOpenFolderClicked(sender As Object, e As EventArgs)
        Try
            Dim folder = _settings.ResolveBackupFolder()
            System.IO.Directory.CreateDirectory(folder)
            Process.Start(New ProcessStartInfo("explorer.exe", """" & folder & """") With {.UseShellExecute = True})
        Catch ex As Exception
            SetStatus("Backup folder could not be opened: " & ex.Message, UiTheme.Red)
        End Try
    End Sub

    Private Async Function RunBackupActionAsync(action As Func(Of Task)) As Task
        If _busy Then Return
        _busy = True
        Try
            Await action()
        Catch ex As Exception
            SetStatus("Backup operation failed: " & ex.Message, UiTheme.Red)
        Finally
            _busy = False
            RefreshStatus()
        End Try
    End Function

    Private Function GetSelectedBackupPath() As String
        If _backups.SelectedRows.Count = 0 Then Return String.Empty
        Return Convert.ToString(_backups.SelectedRows(0).Cells("FilePath").Value, CultureInfo.InvariantCulture)
    End Function

    Private Sub RefreshStatus()
        If IsDisposed Then Return
        Try
            Dim backupFolder = _settings.ResolveBackupFolder()
            Dim lastBackup = If(_service.LastBackupUtc.HasValue,
                _service.LastBackupUtc.Value.ToString("u", CultureInfo.InvariantCulture), "never")
            Dim nextBackup = If(_settings.AutomaticBackupEnabled,
                "before each update + manual",
                "manual only")
            _lastBackupMetric.Text = lastBackup
            _nextBackupMetric.Text = nextBackup
            _retentionMetric.Text = "Never deleted automatically"
            Dim errorText = If(String.IsNullOrWhiteSpace(_service.LastError), String.Empty, " • ERROR: " & _service.LastError)
            If _canEdit Then
                SetStatus("Last verified backup: " & lastBackup & " • mode: " & nextBackup &
                          " • folder: " & backupFolder &
                          " • authentication DB excluded" & errorText,
                          If(String.IsNullOrWhiteSpace(_service.LastError), UiTheme.Ink, UiTheme.Red))
            End If
            RefreshBackupList()
        Catch ex As Exception
            SetStatus("Backup status could not be read: " & ex.Message, UiTheme.Red)
        End Try
    End Sub

    Private Async Sub RefreshBackupList()
        If IsDisposed OrElse Not _pageActive OrElse _refreshingBackups Then Return
        _refreshingBackups = True
        Dim selectedPath = GetSelectedBackupPath()
        Try
            Dim artifacts = Await Task.Run(Function() _service.GetRecentBackups(100))
            If IsDisposed OrElse Not _pageActive Then Return
            If BackupRowsMatch(artifacts) Then
                _archiveCountMetric.Text = artifacts.Count.ToString(CultureInfo.InvariantCulture)
                Return
            End If
            _backups.Rows.Clear()
            _archiveCountMetric.Text = artifacts.Count.ToString(CultureInfo.InvariantCulture)
            For Each artifact In artifacts
                Dim row = _backups.Rows(_backups.Rows.Add(
                    artifact.FileName,
                    artifact.LastWriteUtc.ToString("u", CultureInfo.InvariantCulture),
                    (artifact.SizeBytes / 1024.0R / 1024.0R).ToString("0.00", CultureInfo.InvariantCulture) & " MiB",
                    artifact.FilePath))
                If Not _selectNewest AndAlso String.Equals(artifact.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase) Then row.Selected = True
            Next
            If _backups.Rows.Count > 0 AndAlso (_selectNewest OrElse _backups.SelectedRows.Count = 0) Then
                _backups.ClearSelection()
                _backups.Rows(0).Selected = True
            End If
            _selectNewest = False
        Catch ex As IOException
            SetStatus("Backup list could not be read: " & ex.Message, UiTheme.Red)
        Catch ex As UnauthorizedAccessException
            SetStatus("Backup list access failed: " & ex.Message, UiTheme.Red)
        Catch ex As SecurityException
            SetStatus("Backup list access failed: " & ex.Message, UiTheme.Red)
        Finally
            _refreshingBackups = False
        End Try
    End Sub

    Private Function BackupRowsMatch(artifacts As IReadOnlyList(Of BackupArtifactInfo)) As Boolean
        If _backups.Rows.Count <> artifacts.Count Then Return False
        For index = 0 To artifacts.Count - 1
            Dim artifact = artifacts(index)
            Dim row = _backups.Rows(index)
            Dim expected = {
                artifact.FileName,
                artifact.LastWriteUtc.ToString("u", CultureInfo.InvariantCulture),
                (artifact.SizeBytes / 1024.0R / 1024.0R).ToString("0.00", CultureInfo.InvariantCulture) & " MiB",
                artifact.FilePath
            }
            For column = 0 To expected.Length - 1
                If Not String.Equals(Convert.ToString(row.Cells(column).Value, CultureInfo.InvariantCulture),
                                     expected(column), StringComparison.Ordinal) Then Return False
            Next
        Next
        Return True
    End Function

    Private Sub SetStatus(message As String, color As Color)
        _status.Text = message
        _status.ForeColor = color
    End Sub
End Class
