Imports System.Drawing
Imports System.Windows.Forms

Public NotInheritable Class ConfigurationView
    Inherits UserControl

    Private ReadOnly _tabs As New FlatTabControl()
    Private ReadOnly _sectionPreloadTimer As New System.Windows.Forms.Timer With {.Interval = 30}
    Private ReadOnly _session As UserSession
    Private ReadOnly _fireService As FireConnectionService
    Private ReadOnly _backupSettings As BackupSettings
    Private ReadOnly _backupService As BackupRecoveryService
    Private ReadOnly _configVersions As ConfigVersionService
    Private ReadOnly _savePlcSettings As Func(Of PlcSettings, Integer, Task)
    Private ReadOnly _saveBackupSettings As Func(Of BackupSettings, Task)
    Private ReadOnly _prepareRestore As Func(Of String, Task(Of RestorePreparationResult))
    Private ReadOnly _restorePrepared As Action
    Private ReadOnly _openTransformers As Action
    Private _snapshots As IReadOnlyList(Of MeterSnapshot)
    Private _plc As PlcSettings
    Private _refreshIntervalMilliseconds As Integer
    Private _status As PlcConnectionStatus
    Private _uiRefreshRateHz As Double
    Private _historian As HistorianDiagnosticsSnapshot = New HistorianDiagnosticsSnapshot()
    Private _operationalLog As OperationalLogDiagnosticsSnapshot = New OperationalLogDiagnosticsSnapshot()
    Private _plcSettings As PlcSettingsView
    Private _alarms As AlarmSettingsView
    Private _fire As FirePanelsView
    Private ReadOnly _firePanels As FirePanelManager
    Private ReadOnly _ownsFirePanels As Boolean
    Private _diagnostics As SystemDataView
    Private _backup As BackupRecoveryView
    Private _versions As ConfigVersionsView
    Private _pageActive As Boolean
    Private _nextSectionToPreload As Integer = 1

    Public Sub New(plc As PlcSettings,
                   refreshIntervalMilliseconds As Integer,
                   status As PlcConnectionStatus,
                   snapshots As IReadOnlyList(Of MeterSnapshot),
                   session As UserSession,
                   fireService As FireConnectionService,
                   backupSettings As BackupSettings,
                   backupService As BackupRecoveryService,
                   configVersions As ConfigVersionService,
                   savePlcSettings As Func(Of PlcSettings, Integer, Task),
                   saveBackupSettings As Func(Of BackupSettings, Task),
                   prepareRestore As Func(Of String, Task(Of RestorePreparationResult)),
                   restorePrepared As Action,
                   openTransformers As Action,
                   Optional firePanels As FirePanelManager = Nothing)
        If plc Is Nothing Then Throw New ArgumentNullException(NameOf(plc))
        If status Is Nothing Then Throw New ArgumentNullException(NameOf(status))
        If session Is Nothing Then Throw New ArgumentNullException(NameOf(session))
        If fireService Is Nothing Then Throw New ArgumentNullException(NameOf(fireService))
        If backupSettings Is Nothing Then Throw New ArgumentNullException(NameOf(backupSettings))
        If backupService Is Nothing Then Throw New ArgumentNullException(NameOf(backupService))
        If configVersions Is Nothing Then Throw New ArgumentNullException(NameOf(configVersions))
        If savePlcSettings Is Nothing Then Throw New ArgumentNullException(NameOf(savePlcSettings))
        If saveBackupSettings Is Nothing Then Throw New ArgumentNullException(NameOf(saveBackupSettings))
        If prepareRestore Is Nothing Then Throw New ArgumentNullException(NameOf(prepareRestore))
        If restorePrepared Is Nothing Then Throw New ArgumentNullException(NameOf(restorePrepared))
        If openTransformers Is Nothing Then Throw New ArgumentNullException(NameOf(openTransformers))

        _plc = plc.Clone()
        _refreshIntervalMilliseconds = refreshIntervalMilliseconds
        _status = status
        _snapshots = If(snapshots, Array.Empty(Of MeterSnapshot)())
        _session = session
        _fireService = fireService
        _firePanels = If(firePanels, New FirePanelManager(fireService))
        _ownsFirePanels = firePanels Is Nothing
        _backupSettings = backupSettings
        _backupService = backupService
        _configVersions = configVersions
        _savePlcSettings = savePlcSettings
        _saveBackupSettings = saveBackupSettings
        _prepareRestore = prepareRestore
        _restorePrepared = restorePrepared
        _openTransformers = openTransformers

        BackColor = UiTheme.Canvas
        SetStyle(ControlStyles.OptimizedDoubleBuffer Or ControlStyles.AllPaintingInWmPaint, True)
        BuildLayout()
    End Sub

    Private Sub BuildLayout()
        Dim root As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 2,
            .Padding = Padding.Empty,
            .BackColor = UiTheme.Canvas}
        root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, UiTheme.WorkspaceNavigationHeight))
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))

        _tabs.Dock = DockStyle.Fill
        _tabs.Margin = Padding.Empty
        _tabs.Appearance = TabAppearance.FlatButtons
        _tabs.SizeMode = TabSizeMode.Fixed
        _tabs.ItemSize = New Size(0, 1)
        _tabs.Padding = New Point(0, 0)
        For Each caption In {"PLC system", "Alarm limits", "Fire system", "System diagnostics", "Backup & recovery", "Settings history", "Device registry"}
            _tabs.TabPages.Add(New TabPage(caption) With {
                .BackColor = UiTheme.Canvas,
                .Padding = New Padding(4, 6, 4, 2)})
        Next
        _tabs.SelectedIndex = 0

        Dim navigation As New SegmentedTabStrip(_tabs) With {.Dock = DockStyle.Fill, .Margin = Padding.Empty}
        root.Controls.Add(navigation, 0, 0)
        root.Controls.Add(_tabs, 0, 1)
        Controls.Add(root)
        AddHandler _tabs.SelectedIndexChanged, AddressOf OnSectionChanged
        AddHandler _sectionPreloadTimer.Tick, AddressOf PreloadNextSection
        EnsureSectionLoaded(0)
    End Sub

    Private Sub OnSectionChanged(sender As Object, e As EventArgs)
        EnsureSectionLoaded(_tabs.SelectedIndex)
        UpdateSectionActivity()
    End Sub

    Private Sub EnsureSectionLoaded(index As Integer)
        If index < 0 OrElse index >= _tabs.TabPages.Count Then Return
        Dim page = _tabs.TabPages(index)
        If page.Controls.Count > 0 Then Return

        Dim content As Control
        Select Case index
            Case 0
                _plcSettings = New PlcSettingsView(_plc, _refreshIntervalMilliseconds, _session, _savePlcSettings)
                content = _plcSettings
            Case 1
                _alarms = New AlarmSettingsView(_session)
                content = _alarms
            Case 2
                _fire = New FirePanelsView(_firePanels, _ownsFirePanels, _session)
                content = _fire
            Case 3
                _diagnostics = New SystemDataView(_plc, _status, _openTransformers, _snapshots)
                _diagnostics.SetRuntimeDiagnostics(_status, _uiRefreshRateHz, _historian, _operationalLog)
                content = _diagnostics
            Case 4
                _backup = New BackupRecoveryView(_backupSettings, _session, _backupService,
                    _saveBackupSettings, _prepareRestore, _restorePrepared)
                content = _backup
            Case 5
                _versions = New ConfigVersionsView(_configVersions, _session, _restorePrepared)
                content = _versions
            Case 6
                content = New MonitoringDevicesView(_session, _firePanels)
            Case Else
                Throw New InvalidOperationException("Unknown Configuration section.")
        End Select

        content.Dock = DockStyle.Fill
        content.Margin = Padding.Empty
        page.Controls.Add(content)
    End Sub

    Public Sub SetPageActive(active As Boolean)
        _pageActive = active
        UpdateSectionActivity()
        If active AndAlso _nextSectionToPreload < _tabs.TabPages.Count Then
            _sectionPreloadTimer.Start()
        Else
            _sectionPreloadTimer.Stop()
        End If
    End Sub

    Private Sub PreloadNextSection(sender As Object, e As EventArgs)
        While _nextSectionToPreload < _tabs.TabPages.Count
            Dim index = _nextSectionToPreload
            _nextSectionToPreload += 1
            If _tabs.TabPages(index).Controls.Count > 0 Then Continue While
            EnsureSectionLoaded(index)
            UpdateSectionActivity()
            _tabs.PrepareHandles()
            Return
        End While
        _sectionPreloadTimer.Stop()
    End Sub

    Private Sub UpdateSectionActivity()
        Dim selected = _tabs.SelectedTab
        If _fire IsNot Nothing Then _fire.SetPageActive(_pageActive AndAlso selected Is _fire.Parent)
        If _backup IsNot Nothing Then _backup.SetPageActive(_pageActive AndAlso selected Is _backup.Parent)
        If _versions IsNot Nothing Then _versions.SetPageActive(_pageActive AndAlso selected Is _versions.Parent)
    End Sub

    Public Sub UpdateDiagnostics(status As PlcConnectionStatus,
                                 snapshots As IReadOnlyList(Of MeterSnapshot),
                                 uiRefreshRateHz As Double,
                                 historian As HistorianDiagnosticsSnapshot,
                                 operationalLog As OperationalLogDiagnosticsSnapshot)
        If IsDisposed Then Return
        If status IsNot Nothing Then _status = status
        If snapshots IsNot Nothing Then _snapshots = snapshots
        _uiRefreshRateHz = uiRefreshRateHz
        _historian = historian
        _operationalLog = operationalLog
        If _diagnostics Is Nothing OrElse _diagnostics.IsDisposed Then Return
        _diagnostics.SetReadings(_snapshots)
        _diagnostics.SetRuntimeDiagnostics(_status, _uiRefreshRateHz, _historian, _operationalLog)
    End Sub

    Public Sub UpdatePlcSettings(settings As PlcSettings, refreshIntervalMilliseconds As Integer)
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        If IsDisposed Then Return
        _plc = settings.Clone()
        _refreshIntervalMilliseconds = refreshIntervalMilliseconds
        If _plcSettings IsNot Nothing AndAlso Not _plcSettings.IsDisposed Then
            _plcSettings.LoadSettings(_plc, _refreshIntervalMilliseconds)
        End If
        If _diagnostics IsNot Nothing AndAlso Not _diagnostics.IsDisposed Then
            _diagnostics.UpdatePlcSettings(_plc)
            _diagnostics.SetPlcStatus(_status)
        End If
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            _sectionPreloadTimer.Stop()
            _sectionPreloadTimer.Dispose()
            If _ownsFirePanels AndAlso _fire Is Nothing Then _firePanels.Dispose()
        End If
        MyBase.Dispose(disposing)
    End Sub
End Class
