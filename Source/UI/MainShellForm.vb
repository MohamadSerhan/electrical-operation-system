Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Drawing
Imports System.Globalization
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Imports EOEMS.Licensing

Friend NotInheritable Class PageHostPanel
    Inherits Panel

    Private _compositeChildren As Boolean = True

    Public Sub New()
        SetStyle(ControlStyles.OptimizedDoubleBuffer Or ControlStyles.AllPaintingInWmPaint Or ControlStyles.UserPaint, True)
        Padding = New Padding(12, 10, 12, 8)
    End Sub

    ' Form pages need child-tree buffering; the map already presents a complete double-buffered canvas.
    Protected Overrides ReadOnly Property CreateParams As CreateParams
        Get
            Dim cp = MyBase.CreateParams
            If _compositeChildren Then cp.ExStyle = cp.ExStyle Or &H2000000
            Return cp
        End Get
    End Property

    Public Sub ActivatePage(view As Control)
        If view Is Nothing Then Throw New ArgumentNullException(NameOf(view))

        SuspendLayout()
        Try
            If ViewFullScreen.IsDetached(view) Then
                If _placeholder Is Nothing Then
                    _placeholder = New Label With {
                        .Dock = DockStyle.Fill,
                        .TextAlign = ContentAlignment.MiddleCenter,
                        .Font = New Font("Segoe UI", 13.0F),
                        .ForeColor = UiTheme.MutedInk,
                        .BackColor = Color.Transparent
                    }
                    Controls.Add(_placeholder)
                End If
                _placeholder.Text = "This page is open in its own full screen window." & vbCrLf & "Close that window or press its full screen button to bring it back here."
                _placeholder.Visible = True
                _placeholderView = view
                _placeholder.BringToFront()
                For Each child As Control In Controls
                    If Not ReferenceEquals(child, _placeholder) Then child.Visible = False
                Next
                Return
            End If
            If _placeholder IsNot Nothing Then _placeholder.Visible = False
            Dim compositeChildren = Not TypeOf view Is FactoryMapView
            If _compositeChildren <> compositeChildren Then
                _compositeChildren = compositeChildren
                UpdateStyles()
            End If
            If view.Parent IsNot Me Then
                view.Visible = False
                view.Dock = DockStyle.Fill
                Controls.Add(view)
            End If

            view.BringToFront()
            view.Visible = True
            ' Hide the previous page only after the new one is in place so no blank frame is painted.
            For Each child As Control In Controls
                If Not ReferenceEquals(child, view) Then child.Visible = False
            Next
        Finally
            ResumeLayout(True)
        End Try

    End Sub

    Private _placeholder As Label
    Private _placeholderView As Control

    Public Function IsShowingPlaceholderFor(view As Control) As Boolean
        Return _placeholder IsNot Nothing AndAlso _placeholder.Visible AndAlso ReferenceEquals(_placeholderView, view)
    End Function
End Class

Public NotInheritable Class MainShellForm
    Inherits Form

    Private ReadOnly _settings As AppSettings
    Private ReadOnly _session As UserSession
    Private ReadOnly _plc As PlcConnectionService
    Private ReadOnly _fire As FireConnectionService
    Private ReadOnly _firePanels As FirePanelManager
    Private _recordingSettings As RecordingSettings
    Private _backupSettings As BackupSettings
    Private ReadOnly _historian As MeasurementHistorian
    Private ReadOnly _exportScheduler As DailyHistorianExportScheduler
    Private ReadOnly _backupService As BackupRecoveryService
    Private ReadOnly _configVersions As New ConfigVersionService()
    Private ReadOnly _ups As UpsAcquisitionService
    Private ReadOnly _upsHistorian As UpsHistorian
    Private _recordingOutbox As DurableRecordingOutbox
    Private _alarmRecorder As EquipmentAlarmRecorder
    Private _ecgOutageOpen As Boolean
    Private _ecgBackUntilUtc As DateTime = DateTime.MinValue
    Private _alarmRecordingError As String = String.Empty
    Private ReadOnly _pageHost As New PageHostPanel()
    Private ReadOnly _pageCache As New Dictionary(Of String, Control)(StringComparer.Ordinal)
    Private _sidebar As Panel
    Private ReadOnly _plcPill As New StatusPill()
    Private ReadOnly _trialPill As New Button()
    Private ReadOnly _footerStatus As New Label()
    Private ReadOnly _clockLabel As New Label()
    Private ReadOnly _activePageTitle As New Label()
    Private ReadOnly _navButtons As New Dictionary(Of String, Button)()
    Private ReadOnly _clockTimer As New System.Windows.Forms.Timer()
    Private ReadOnly _licenseTimer As New System.Windows.Forms.Timer()
    Private ReadOnly _uiRefreshTimer As New System.Windows.Forms.Timer()
    Private ReadOnly _accountingTimer As New System.Windows.Forms.Timer()
    Private _accountingBusy As Boolean
    Private ReadOnly _pendingUpdateGate As New Object()
    Private ReadOnly _recordingGate As New Object()
    Private ReadOnly _uiRenderTicks As New Queue(Of Long)()
    Private ReadOnly _energyToday As New EnergyTodayTracker()
    Private _lastHistorianRecordingUtc As DateTime?
    Private _pendingStatus As PlcConnectionStatus
    Private _pendingReadings As IReadOnlyList(Of MeterSnapshot)
    Private _hasPendingStatus As Boolean
    Private _hasPendingReadings As Boolean
    Private _uiRefreshRateHz As Double
    Private _lastDiagnosticsTicks As Long
    Private Shared ReadOnly DiagnosticsRefreshInterval As TimeSpan = TimeSpan.FromMilliseconds(250)
    Private _shutdownStarted As Boolean
    Private _shutdownComplete As Boolean
    Private _windowMinimized As Boolean
    Private _activePage As String = "Overview"
    Private _currentView As Control
    Private _lastMapRefreshTicks As Long
    Private _mapUpsReadTask As Task(Of (Snapshots As IReadOnlyDictionary(Of String, UpsSnapshot), [Error] As String))
    Private _mapUpsSnapshots As IReadOnlyDictionary(Of String, UpsSnapshot) = New Dictionary(Of String, UpsSnapshot)()
    Private _displayedMapUpsReadError As String = String.Empty

    ' Used by screenshot/UI tests so showing the shell never starts exports, backups or PLC polling.
    <System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public Property SuppressBackgroundServices As Boolean

    Public Sub New(settings As AppSettings, session As UserSession)
        _settings = settings
        _session = session
        _plc = New PlcConnectionService(settings.Plc)
        _fire = New FireConnectionService(allowCommunication:=False)
        _firePanels = New FirePanelManager(_fire)
        _recordingSettings = RecordingSettingsStore.Load()
        _backupSettings = BackupSettingsStore.Load()
        _historian = New MeasurementHistorian(recordingSettings:=_recordingSettings)
        _exportScheduler = New DailyHistorianExportScheduler(_historian, _recordingSettings)
        _backupService = New BackupRecoveryService(_historian, _backupSettings, _recordingSettings)
        If Not session.IsOfflinePreview Then
            _ups = New UpsAcquisitionService()
            _upsHistorian = New UpsHistorian(_ups)
        End If
        BuildLayout()
        AddHandler _plc.StatusChanged, AddressOf OnPlcStatusChanged
        AddHandler _plc.ReadingsChanged, AddressOf OnPlcReadingsChanged
        AddHandler _clockTimer.Tick, Sub(sender, e) _clockLabel.Text = DateTime.Now.ToString("dd MMM yyyy  •  HH:mm:ss")
        _clockTimer.Interval = 1000
        _clockTimer.Start()
        _licenseTimer.Interval = TimeSpan.FromMinutes(30).TotalMilliseconds
        AddHandler _licenseTimer.Tick, AddressOf OnLicenseRecheckTick
        _licenseTimer.Start()
        _uiRefreshTimer.Interval = GetUiRefreshInterval(settings.Ui.RefreshIntervalMilliseconds)
        AddHandler _uiRefreshTimer.Tick, AddressOf OnUiRefreshTick
        _uiRefreshTimer.Start()
        _accountingTimer.Interval = 20000
        AddHandler _accountingTimer.Tick, AddressOf OnAccountingTick
        _accountingTimer.Start()
        _clockLabel.Text = DateTime.Now.ToString("dd MMM yyyy  •  HH:mm:ss")
        RefreshTrialBanner()
        ShowPage("Overview")
    End Sub

    ' Keeps the permanent shift energy and ECG outage tables current (idempotent; the first run backfills all recorded history).
    Private Async Sub OnAccountingTick(sender As Object, e As EventArgs)
        _accountingTimer.Interval = 120000
        If _accountingBusy OrElse _session.IsOfflinePreview Then Return
        _accountingBusy = True
        Try
            Await Task.Run(Sub()
                               EnergyAccounting.Recompute()
                               EcgOutageStore.Sync()
                           End Sub)
        Catch ex As Exception When TypeOf ex Is Npgsql.NpgsqlException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is TimeoutException
        Finally
            _accountingBusy = False
        End Try
    End Sub

    Protected Overrides Sub OnFormClosed(e As FormClosedEventArgs)
        _clockTimer.Stop()
        _licenseTimer.Stop()
        _uiRefreshTimer.Stop()
        _accountingTimer.Stop()
        RemoveHandler _uiRefreshTimer.Tick, AddressOf OnUiRefreshTick
        RemoveHandler _plc.StatusChanged, AddressOf OnPlcStatusChanged
        RemoveHandler _plc.ReadingsChanged, AddressOf OnPlcReadingsChanged
        ViewFullScreen.CloseAll()
        For Each view In _pageCache.Values
            If Not view.IsDisposed Then
                If view.Parent Is _pageHost Then _pageHost.Controls.Remove(view)
                view.Dispose()
            End If
        Next
        _pageCache.Clear()
        _clockTimer.Dispose()
        _licenseTimer.Dispose()
        _accountingTimer.Dispose()
        _uiRefreshTimer.Dispose()
        If _prewarmTimer IsNot Nothing Then _prewarmTimer.Dispose()
        If _resumeTimer IsNot Nothing Then _resumeTimer.Dispose()
        _plc.Dispose()
        _firePanels.Dispose()
        _fire.Dispose()
        _backupService.Dispose()
        _configVersions.Dispose()
        If _upsHistorian IsNot Nothing Then _upsHistorian.Dispose()
        If _ups IsNot Nothing Then _ups.Dispose()
        If _recordingOutbox IsNot Nothing Then _recordingOutbox.Dispose()
        MyBase.OnFormClosed(e)
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        MyBase.OnFormClosing(e)
        If e.Cancel OrElse _shutdownComplete Then Return
        e.Cancel = True
        If _shutdownStarted Then Return
        _shutdownStarted = True
        _uiRefreshTimer.Stop()
        _clockTimer.Stop()
        CompleteShutdownAndCloseAsync()
    End Sub

    Private Async Sub CompleteShutdownAndCloseAsync()
        For Each analyzer In _pageCache.Values.OfType(Of OnlineAnalyzerView)()
            Await analyzer.FinishRecordingAsync()
        Next
        Await _fire.StopAsync()
        Await _firePanels.StopAsync()
        Await _plc.StopAsync()
        If Not String.IsNullOrEmpty(_plc.CurrentStatus.PowerLogError) Then
            MessageBox.Show(Me, "Some power-event records could not be saved. " & _plc.CurrentStatus.PowerLogError,
                "Power event recording error", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End If
        Await StopUpsServicesAsync()
        _energyToday.Flush()
        Await _exportScheduler.StopAsync()
        Await _backupService.StopAsync()
        Await _historian.StopAsync()
        If _recordingOutbox IsNot Nothing Then Await _recordingOutbox.StopAsync()
        _shutdownComplete = True
        Close()
    End Sub

    Protected Overrides Sub OnShown(e As EventArgs)
        MyBase.OnShown(e)
        If SuppressBackgroundServices Then Return
        StartPagePrewarm()
        Try
            _configVersions.Start()
        Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is UnauthorizedAccessException
            _footerStatus.Text = "Settings versioning is unavailable: " & ex.Message
        End Try
        _exportScheduler.Start()
        If _session.IsOfflinePreview Then
            _footerStatus.Text = "Offline preview — PLC communication is disabled."
            Return
        End If
        _backupService.Start()
        Call _fire.StartAsync()
        Call _firePanels.StartAsync()
        _recordingOutbox = New DurableRecordingOutbox(IO.Path.Combine(_recordingSettings.DatabaseDirectory, "recording-outbox.db"))
        _historian.AttachOutbox(_recordingOutbox)
        _alarmRecorder = New EquipmentAlarmRecorder(_recordingOutbox)
        _recordingOutbox.Start()
        ' UPS SNMP polling runs on its own tasks so it can never delay PLC acquisition.
        If _ups IsNot Nothing Then
            UpsAcquisitionService.Current = _ups
            UpsHistorian.Current = _upsHistorian
            _upsHistorian.Start(_recordingOutbox, _alarmRecorder, Function() _recordingSettings)
            _ups.Start()
        End If
        Call _plc.StartAsync()
    End Sub

    Private Async Function StopUpsServicesAsync() As Task
        Try
            If _ups IsNot Nothing Then Await _ups.StopAsync()
            If _upsHistorian IsNot Nothing Then Await _upsHistorian.StopAsync()
        Catch ex As Exception
            Debug.WriteLine("UPS shutdown: " & ex.Message)
        End Try
    End Function

    Private Sub BuildLayout()
        Text = "Electrical Operation Center | " & AppBuildInfo.VersionDisplay
        StartPosition = FormStartPosition.WindowsDefaultLocation
        MinimumSize = New Size(1100, 700)
        DoubleBuffered = True
        WindowState = FormWindowState.Maximized
        AutoScaleMode = AutoScaleMode.Dpi
        BackColor = UiTheme.Canvas
        Font = New Font("Segoe UI", 9.0F)
        Dim applicationIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath)
        If applicationIcon IsNot Nothing Then Icon = applicationIcon

        _pageHost.Dock = DockStyle.Fill
        _pageHost.BackColor = UiTheme.Canvas
        Controls.Add(_pageHost)

        _sidebar = DirectCast(BuildSidebar(), Panel)
        Controls.Add(_sidebar)
        Controls.Add(BuildFooter())
        Controls.Add(BuildHeader())
        AddHandler SizeChanged, AddressOf OnShellSizeChanged
        OnShellSizeChanged(Me, EventArgs.Empty)
    End Sub

    Private Function BuildHeader() As Control
        Dim header As New Panel With {.Dock = DockStyle.Top, .Height = 64, .BackColor = UiTheme.Surface}
        Dim divider As New Panel With {.Dock = DockStyle.Bottom, .Height = 1, .BackColor = Icons.HairLine}
        header.Controls.Add(divider)

        Dim logo As New BrandLogo With {.Location = New Point(20, 11)}
        header.Controls.Add(logo)
        Dim brand = UiTheme.Label("EOEMS SCADA", New Font("Segoe UI", 12.5F, FontStyle.Bold), Color.FromArgb(14, 42, 77))
        brand.Location = New Point(68, 11)
        header.Controls.Add(brand)
        Dim product = UiTheme.Label("POWER TRANSFORMERS  •  " & AppBuildInfo.VersionDisplay, New Font("Segoe UI", 7.5F, FontStyle.Bold), UiTheme.MutedInk)
        product.Location = New Point(69, 36)
        header.Controls.Add(product)

        _activePageTitle.Font = UiTheme.ShellPageTitleFont
        _activePageTitle.ForeColor = Color.FromArgb(14, 42, 77)
        _activePageTitle.AutoSize = False
        _activePageTitle.AutoEllipsis = True
        _activePageTitle.UseMnemonic = False
        _activePageTitle.TextAlign = ContentAlignment.MiddleLeft
        _activePageTitle.Size = New Size(300, 24)
        _activePageTitle.Location = New Point(232, 18)
        header.Controls.Add(_activePageTitle)
        header.Controls.Add(_plcPill)

        _trialPill.FlatStyle = FlatStyle.Flat
        _trialPill.Font = New Font("Segoe UI", 8.0F, FontStyle.Bold)
        _trialPill.ForeColor = Color.White
        _trialPill.BackColor = UiTheme.Amber
        _trialPill.FlatAppearance.BorderSize = 0
        _trialPill.Cursor = Cursors.Hand
        _trialPill.AutoSize = False
        _trialPill.Size = New Size(168, 26)
        _trialPill.Visible = False
        AddHandler _trialPill.Click, AddressOf OnTrialPillClicked
        header.Controls.Add(_trialPill)

        Dim probe As New IconButton With {.Text = "Read PLC now", .Glyph = Icons.Refresh, .Size = New Size(138, 34)}
        probe.Enabled = Not _session.IsOfflinePreview
        AddHandler probe.Click, AddressOf OnProbeClicked
        header.Controls.Add(probe)

        _clockLabel.AutoSize = False
        _clockLabel.TextAlign = ContentAlignment.MiddleRight
        _clockLabel.Font = New Font("Segoe UI", 8.5F)
        _clockLabel.ForeColor = UiTheme.MutedInk
        _clockLabel.UseMnemonic = False
        _clockLabel.Size = New Size(176, 30)
        header.Controls.Add(_clockLabel)

        Dim avatar = Icons.IconLabel(Icons.User, 16.0F, Color.FromArgb(14, 42, 77))
        header.Controls.Add(avatar)
        Dim userName = UiTheme.Label(_session.DisplayName, New Font("Segoe UI", 8.5F, FontStyle.Bold), UiTheme.Ink, False)
        userName.Size = New Size(150, 18)
        userName.TextAlign = ContentAlignment.BottomLeft
        header.Controls.Add(userName)
        Dim userRole = UiTheme.Label(_session.RoleDisplay, New Font("Segoe UI", 7.5F), UiTheme.MutedInk, False)
        userRole.Size = New Size(150, 16)
        header.Controls.Add(userRole)
        Dim chevron = Icons.IconLabel(Icons.ChevronDown, 8.0F, UiTheme.Ink)
        header.Controls.Add(chevron)

        AddHandler header.Resize, Sub(sender, e)
                                      Dim w = header.ClientSize.Width
                                      product.Visible = w >= 850
                                      Dim showUser = w >= 1100
                                      avatar.Visible = showUser
                                      userName.Visible = showUser
                                      userRole.Visible = showUser
                                      chevron.Visible = showUser
                                      Dim showClock = w >= 1000
                                      _clockLabel.Visible = showClock
                                      Dim right = w - 18
                                      If showUser Then
                                          chevron.Location = New Point(right - chevron.Width, 17)
                                          userName.Location = New Point(chevron.Left - userName.Width + 4, 12)
                                          userRole.Location = New Point(userName.Left, 31)
                                          avatar.Location = New Point(userName.Left - avatar.Width - 22, 15)
                                          right = avatar.Left - 14
                                      End If
                                      probe.Location = New Point(right - probe.Width, 15)
                                      Dim actionLeft = probe.Left
                                      If showClock Then
                                          _clockLabel.Location = New Point(probe.Left - _clockLabel.Width - 12, 17)
                                          actionLeft = _clockLabel.Left
                                      End If
                                      _plcPill.Location = New Point(Math.Max(brand.Right + 20, actionLeft - _plcPill.Width - 12), 17)
                                      If _trialPill.Visible Then
                                          _trialPill.Location = New Point(Math.Max(brand.Right + 20, _plcPill.Left - _trialPill.Width - 10), 19)
                                          _activePageTitle.Width = Math.Max(0, _trialPill.Left - _activePageTitle.Left - 16)
                                      Else
                                          _activePageTitle.Width = Math.Max(0, _plcPill.Left - _activePageTitle.Left - 16)
                                      End If
                                      _activePageTitle.Visible = w >= 1000 AndAlso _activePageTitle.Width >= 80
                                  End Sub
        Return header
    End Function

    Private Function BuildSidebar() As Control
        Dim sidebar As New Panel With {.Dock = DockStyle.Left, .Width = 200, .BackColor = Icons.SidebarNavy, .Padding = New Padding(0, 14, 0, 10)}
        Dim navigation As New FlowLayoutPanel With {
            .Dock = DockStyle.Fill,
            .FlowDirection = FlowDirection.TopDown,
            .WrapContents = False,
            .AutoScroll = False,
            .BackColor = Color.Transparent
        }
        AddNavigationGroup(navigation, "MONITORING", True)
        AddNavigationButton(navigation, "Overview", "Overview", Icons.Home)
        AddNavigationButton(navigation, "Factory Map", "Factory Map", Icons.Monitor)
        AddNavigationButton(navigation, "Transformers", "Transformers", Icons.Grid)
        AddNavigationButton(navigation, "Online Analyzer", "Online Analyzer", Icons.Chart)
        AddNavigationButton(navigation, "UPS Monitoring", "UPS Monitoring", Icons.Battery)
        AddNavigationButton(navigation, "Factory Energy", "Factory Energy", Icons.Monitor)
        AddNavigationGroup(navigation, "DATA & CONTROL", False)
        AddNavigationButton(navigation, "Configuration", "Configuration", Icons.Settings)
        AddNavigationButton(navigation, "UPS & SNMP", "SNMP Setting", Icons.Ethernet)
        AddNavigationButton(navigation, "Recording & Historian", "Historian / Reports", Icons.Database)
        sidebar.Controls.Add(navigation)

        Dim edge = New RoundedPanel With {.BackColor = Color.FromArgb(22, 54, 94), .BorderColor = Color.FromArgb(48, 82, 124), .Radius = 8, .Dock = DockStyle.Bottom, .Height = 76, .Padding = New Padding(10), .Margin = New Padding(10)}
        Dim label = UiTheme.Label("DATA GUARDRAIL", New Font("Segoe UI", 7.5F, FontStyle.Bold), Color.FromArgb(150, 190, 235))
        label.Location = New Point(11, 9)
        edge.Controls.Add(label)
        Dim body = UiTheme.Label("FC03 reads only. Missing data stays blank and quality-tagged.", New Font("Segoe UI", 7.5F), Icons.SidebarText, False)
        body.Location = New Point(11, 29)
        body.Size = New Size(edge.ClientSize.Width - 22, 35)
        body.Anchor = AnchorStyles.Top Or AnchorStyles.Left Or AnchorStyles.Right
        edge.Controls.Add(body)
        Dim edgeHost As New Panel With {.Dock = DockStyle.Bottom, .Height = 90, .Padding = New Padding(10, 6, 10, 8), .BackColor = Color.Transparent}
        edge.Dock = DockStyle.Fill
        edgeHost.Controls.Add(edge)
        sidebar.Controls.Add(edgeHost)
        AddHandler navigation.Resize, Sub(sender, e)
                                          For Each child As Control In navigation.Controls
                                              child.Width = Math.Max(0, navigation.ClientSize.Width - child.Margin.Horizontal)
                                          Next
                                      End Sub
        Return sidebar
    End Function

    Private Sub AddNavigationGroup(host As FlowLayoutPanel, title As String, first As Boolean)
        If Not first Then
            Dim rule As New Panel With {.Height = 1, .Width = 200, .BackColor = Color.FromArgb(44, 72, 110), .Margin = New Padding(20, 10, 20, 0)}
            rule.Width = 160
            host.Controls.Add(rule)
        End If
        Dim label = UiTheme.Label(title, New Font("Segoe UI", 7.5F, FontStyle.Bold), Icons.SidebarMuted, False)
        label.Size = New Size(200, 18)
        label.Margin = New Padding(0, If(first, 10, 12), 0, 4)
        label.Padding = New Padding(20, 0, 0, 0)
        label.TextAlign = ContentAlignment.MiddleLeft
        host.Controls.Add(label)
    End Sub

    Private Const WM_SIZE As Integer = &H5

    Protected Overrides Sub WndProc(ByRef m As Message)
        Dim pausePageUpdates = False
        Dim resumePageUpdates = False
        If m.Msg = WM_SIZE Then
            Dim minimized = m.WParam.ToInt32() = 1
            If minimized <> _windowMinimized Then
                _windowMinimized = minimized
                pausePageUpdates = minimized
                resumePageUpdates = Not minimized
            End If
        End If
        MyBase.WndProc(m)
        If _shutdownStarted Then Return
        If pausePageUpdates Then
            _uiRefreshTimer.Stop()
            _clockTimer.Stop()
            If _currentView IsNot Nothing Then SetPageActive(_currentView, False)
        ElseIf resumePageUpdates Then
            BeginInvoke(New MethodInvoker(AddressOf ResumePageUpdatesAfterLayout))
        End If
    End Sub

    Private _resumeTimer As System.Windows.Forms.Timer

    Private Sub ResumePageUpdatesAfterLayout()
        If _shutdownStarted OrElse _windowMinimized OrElse IsDisposed OrElse Disposing Then Return

        ' Paint the whole restored window (sidebar, header, current page) before any refresh work runs on the UI thread.
        Update()
        If _resumeTimer Is Nothing Then
            _resumeTimer = New System.Windows.Forms.Timer With {.Interval = 80}
            AddHandler _resumeTimer.Tick, AddressOf ResumeWorkAfterPaint
        End If
        _resumeTimer.Stop()
        _resumeTimer.Start()
    End Sub

    Private Sub ResumeWorkAfterPaint(sender As Object, e As EventArgs)
        _resumeTimer.Stop()
        If _shutdownStarted OrElse _windowMinimized OrElse IsDisposed OrElse Disposing Then Return

        If _currentView IsNot Nothing Then
            SetPageActive(_currentView, True)
            RefreshActivePageData()
        End If

        _clockLabel.Text = DateTime.Now.ToString("dd MMM yyyy  •  HH:mm:ss")
        _uiRefreshTimer.Start()
        _clockTimer.Start()
    End Sub

    Private Sub OnShellSizeChanged(sender As Object, e As EventArgs)
        If WindowState = FormWindowState.Minimized Then Return
        If _sidebar IsNot Nothing Then
            _sidebar.Width = If(ClientSize.Width < 1050, 180, 200)
        End If
    End Sub

    Private Sub AddNavigationButton(host As Control, key As String, text As String, glyph As String)
        Dim button As New NavButton With {.Text = text, .Tag = key, .Glyph = glyph, .Width = 200, .Height = 44, .Margin = Padding.Empty}
        AddHandler button.Click, AddressOf OnNavigationClicked
        host.Controls.Add(button)
        _navButtons(key) = button
    End Sub

    Private Function BuildFooter() As Control
        Dim footer As New Panel With {.Dock = DockStyle.Bottom, .Height = 31, .BackColor = UiTheme.Surface, .Padding = New Padding(20, 0, 20, 0)}
        Dim line As New Panel With {.Dock = DockStyle.Top, .Height = 1, .BackColor = UiTheme.Border}
        footer.Controls.Add(line)
        _footerStatus.AutoSize = False
        _footerStatus.Size = New Size(800, 24)
        _footerStatus.Location = New Point(24, 5)
        _footerStatus.Font = New Font("Segoe UI", 8.0F)
        _footerStatus.ForeColor = UiTheme.MutedInk
        _footerStatus.Text = "PLC polling starts after sign-in. Live values require a successful Modbus FC03 response."
        footer.Controls.Add(_footerStatus)
        Dim build As New LinkLabel With {
            .Text = "EOEMS " & AppBuildInfo.VersionDisplay & "  •  ABOUT",
            .Font = New Font("Segoe UI", 8.0F, FontStyle.Bold),
            .LinkColor = UiTheme.MutedInk,
            .ActiveLinkColor = UiTheme.Cyan,
            .VisitedLinkColor = UiTheme.MutedInk,
            .BackColor = Color.Transparent,
            .TextAlign = ContentAlignment.MiddleRight,
            .Cursor = Cursors.Hand,
            .TabStop = True,
            .UseMnemonic = False
        }
        build.Size = New Size(180, 20)
        build.Location = New Point(footer.Width - 200, 5)
        build.Anchor = AnchorStyles.Right Or AnchorStyles.Top
        AddHandler build.LinkClicked, Sub(sender, e)
                                          Using about As New AboutForm()
                                              about.ShowDialog(Me)
                                          End Using
                                      End Sub
        footer.Controls.Add(build)
        AddHandler footer.Resize, Sub(sender, e)
                                      build.Location = New Point(footer.ClientSize.Width - build.Width - 12, 5)
                                      _footerStatus.Width = Math.Max(120, build.Left - _footerStatus.Left - 16)
                                  End Sub
        Return footer
    End Function

    Private Sub OnNavigationClicked(sender As Object, e As EventArgs)
        Dim button = DirectCast(sender, Button)
        ShowPage(CStr(button.Tag))
    End Sub

    Private ReadOnly _prewarmQueue As New Queue(Of String)()
    Private _prewarmTimer As System.Windows.Forms.Timer

    ' Builds the heavy pages off-screen shortly after start so the first visit only shows a finished page.
    Private Sub StartPagePrewarm()
        For Each key In {"Configuration", "UPS & SNMP", "Recording & Historian", "UPS Monitoring", "Transformers", "Online Analyzer", "Factory Energy", "Factory Map"}
            _prewarmQueue.Enqueue(key)
        Next
        _prewarmTimer = New System.Windows.Forms.Timer With {.Interval = 250}
        AddHandler _prewarmTimer.Tick, AddressOf PrewarmNextPage
        _prewarmTimer.Start()
    End Sub

    Private Sub PrewarmNextPage(sender As Object, e As EventArgs)
        If IsDisposed OrElse _shutdownStarted OrElse _windowMinimized OrElse _prewarmQueue.Count = 0 Then
            If _prewarmQueue.Count = 0 OrElse IsDisposed Then _prewarmTimer.Stop()
            Return
        End If
        ' Page constructors can read storage and create many handles; never run them over the interactive map.
        If TypeOf _currentView Is FactoryMapView Then Return
        Dim key = _prewarmQueue.Dequeue()
        If _pageCache.ContainsKey(key) Then Return
        Try
            Dim view = CreatePage(key)
            view.Visible = False
            view.Dock = DockStyle.Fill
            _pageCache.Add(key, view)
            _pageHost.Controls.Add(view)
            PrepareHandles(view)
        Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is NullReferenceException
            Debug.WriteLine("Page prewarm skipped for " & key & ": " & ex.Message)
        End Try
    End Sub

    Private Shared Sub PrepareHandles(target As Control)
        Dim handle = target.Handle
        For Each child As Control In target.Controls
            PrepareHandles(child)
        Next
    End Sub

    Private Sub ShowPage(pageKey As String)
        If String.Equals(pageKey, "System Data", StringComparison.Ordinal) Then pageKey = "Configuration"
        If String.Equals(_activePage, pageKey, StringComparison.Ordinal) AndAlso _currentView IsNot Nothing Then Return
        Dim previousView = _currentView
        If previousView IsNot Nothing Then SetPageActive(previousView, False)

        Dim nextView As Control = Nothing
        If Not _pageCache.TryGetValue(pageKey, nextView) Then
            nextView = CreatePage(pageKey)
            nextView.Visible = False
            _pageCache.Add(pageKey, nextView)
        End If

        _activePage = pageKey
        _activePageTitle.Text = If(pageKey = "Configuration", "Configuration",
            If(pageKey = "Recording & Historian", "Historian / Reports",
            If(pageKey = "UPS & SNMP", "SNMP Setting", pageKey)))
        _activePageTitle.Visible = ClientSize.Width >= 1000
        For Each entry In _navButtons
            Dim selected = String.Equals(entry.Key, pageKey, StringComparison.Ordinal)
            DirectCast(entry.Value, NavButton).Selected = selected
        Next

        _currentView = nextView
        SetPageActive(_currentView, False)
        _pageHost.ActivatePage(_currentView)
        SetPageActive(_currentView, Not _windowMinimized)
        RefreshActivePageData()
    End Sub

    Private Function CreatePage(pageKey As String) As Control
        Select Case pageKey
            Case "Transformers"
                Return New TransformersView(_settings.Plc, _plc.CurrentStatus, _plc.CurrentReadings, AddressOf ProbeAsync, Not _session.IsOfflinePreview, _energyToday, _historian)
            Case "Factory Map"
                Dim map As New FactoryMapView(_session, _plc.CurrentReadings, Sub() ShowPage("Transformers"))
                map.SetFirePanels(_firePanels)
                Return map
            Case "Online Analyzer"
                Return New OnlineAnalyzerView(Function() _plc.CurrentReadings, AddressOf CurrentUpsSnapshots, Function() _recordingSettings)
            Case "UPS Monitoring"
                Return New UpsAtsView(_ups, _upsHistorian, _session, Sub() ShowPage("UPS & SNMP"))
            Case "Factory Energy"
                Return New FactoryEnergyView()
            Case "UPS & SNMP"
                Return New UpsSnmpSettingsView(_session)
            Case "Configuration"
                Return New ConfigurationView(
                    _settings.Plc, _settings.Ui.RefreshIntervalMilliseconds, _plc.CurrentStatus, _plc.CurrentReadings,
                    _session, _fire, _backupSettings, _backupService, _configVersions,
                    AddressOf SavePlcSettingsAsync, AddressOf SaveBackupSettingsAsync,
                    AddressOf PrepareRestoreAndShutdownAsync, Sub() Close(),
                    Sub() ShowPage("Transformers"), _firePanels)
            Case "Recording & Historian"
                Return New HistorianView(_recordingSettings, _session, _historian, _exportScheduler,
                    AddressOf SaveRecordingSettingsAsync, _settings.Plc.PollingIntervalSeconds,
                    _settings.Plc, _settings.Ui.RefreshIntervalMilliseconds,
                    AddressOf SavePlcSettingsAsync, _plc.CurrentStatus, _plc.CurrentReadings,
                    ActiveAlarmService.Create(_alarmRecorder, Function() _settings.Plc), _firePanels)
            Case Else
                Dim dashboard As New DashboardView(_settings.Plc, _plc.CurrentStatus, AddressOf ShowTransformers,
                    _historian, _exportScheduler,
                    Sub() ShowPage("Online Analyzer"),
                    Sub() ShowPage("System Data"),
                    Sub() ShowPage("Recording & Historian"),
                    _backupService, _ups)
                dashboard.SetReadings(_plc.CurrentReadings)
                dashboard.SetRuntimeDiagnostics(_plc.CurrentStatus, _historian.GetDiagnostics(), PlcReadingsLog.GetDiagnostics())
                Return dashboard
        End Select
    End Function

    ' License keys can expire while the app keeps running (trials, term licenses); re-check on a
    ' low frequency timer and close gracefully instead of letting a lapsed copy carry on silently.
    Private Sub OnLicenseRecheckTick(sender As Object, e As EventArgs)
        Dim status = LicenseGate.EvaluateCurrent()
        RefreshTrialBanner()
        If status.IsValid Then Return
        _licenseTimer.Stop()
        MessageBox.Show(Me, "This license is no longer valid: " & status.Reason & Environment.NewLine & Environment.NewLine &
                        "The application will now close. Register a valid license to continue.",
                        "License expired", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        Close()
    End Sub

    ' Shows (or hides) the amber trial pill in the header based on the gate's last evaluated
    ' status, and lets the user open the (non-mandatory) registration screen early from it.
    Private Sub RefreshTrialBanner()
        Dim status = LicenseGate.CurrentStatus
        Dim isActiveTrial = status IsNot Nothing AndAlso status.IsValid AndAlso
            status.Payload IsNot Nothing AndAlso status.Payload.Plan = LicensePlan.Trial
        If Not isActiveTrial Then
            _trialPill.Visible = False
            Return
        End If
        Dim days = If(status.DaysRemaining.HasValue, status.DaysRemaining.Value, 0)
        _trialPill.Text = "TRIAL • " & days & If(days = 1, " DAY LEFT", " DAYS LEFT")
        _trialPill.Visible = True
        _trialPill.Location = New Point(Math.Max(20, _plcPill.Left - _trialPill.Width - 10), 19)
        _activePageTitle.Width = Math.Max(0, _trialPill.Left - _activePageTitle.Left - 16)
    End Sub

    Private Sub OnTrialPillClicked(sender As Object, e As EventArgs)
        Using form As New LicenseRegistrationForm(LicenseGate.CurrentStatus, isMandatory:=False)
            form.ShowDialog(Me)
        End Using
        RefreshTrialBanner()
    End Sub

    Private Sub SetPageActive(view As Control, active As Boolean)
        If ViewFullScreen.IsDetached(view) Then active = True
        If TypeOf view Is OnlineAnalyzerView Then
            DirectCast(view, OnlineAnalyzerView).SetPageActive(active)
        ElseIf TypeOf view Is HistorianView Then
            DirectCast(view, HistorianView).SetPageActive(active)
        ElseIf TypeOf view Is UpsAtsView Then
            DirectCast(view, UpsAtsView).SetPageActive(active)
        ElseIf TypeOf view Is FactoryEnergyView Then
            DirectCast(view, FactoryEnergyView).SetPageActive(active)
        ElseIf TypeOf view Is FactoryMapView Then
            DirectCast(view, FactoryMapView).SetPageActive(active)
        ElseIf TypeOf view Is UpsSnmpSettingsView Then
            DirectCast(view, UpsSnmpSettingsView).SetPageActive(active)
        ElseIf TypeOf view Is ConfigurationView Then
            DirectCast(view, ConfigurationView).SetPageActive(active)
        End If
    End Sub

    Private Sub RefreshActivePageData()
        If IsDisposed OrElse Disposing OrElse _currentView Is Nothing OrElse _currentView.IsDisposed Then Return
        Dim status = _plc.CurrentStatus
        Dim readings = _plc.CurrentReadings
        _plcPill.SetStatus(status)
        If TypeOf _currentView Is DashboardView Then
            Dim historian = _historian.GetDiagnostics()
            Dim operationalLog = PlcReadingsLog.GetDiagnostics()
            DirectCast(_currentView, DashboardView).SetPlcStatus(status)
            DirectCast(_currentView, DashboardView).SetReadings(readings)
            DirectCast(_currentView, DashboardView).SetRuntimeDiagnostics(status, historian, operationalLog)
            DirectCast(_currentView, DashboardView).SetSnmpStatus(_ups)
        ElseIf TypeOf _currentView Is TransformersView Then
            DirectCast(_currentView, TransformersView).SetPlcStatus(status)
            DirectCast(_currentView, TransformersView).SetReadings(readings)
        ElseIf TypeOf _currentView Is FactoryMapView Then
            DirectCast(_currentView, FactoryMapView).SetAlarmRatings(_settings.Plc)
            DirectCast(_currentView, FactoryMapView).SetReadings(readings, CurrentMapUpsSnapshots())
        ElseIf TypeOf _currentView Is ConfigurationView Then
            Dim historian = _historian.GetDiagnostics()
            Dim operationalLog = PlcReadingsLog.GetDiagnostics()
            DirectCast(_currentView, ConfigurationView).UpdateDiagnostics(status, readings, _uiRefreshRateHz, historian, operationalLog)
        End If
    End Sub

    Private Function CurrentUpsSnapshots() As IReadOnlyDictionary(Of String, UpsSnapshot)
        If _ups Is Nothing Then Return New Dictionary(Of String, UpsSnapshot)()
        Return _ups.GetSnapshots()
    End Function

    Private Function CurrentMapUpsSnapshots() As IReadOnlyDictionary(Of String, UpsSnapshot)
        If _ups Is Nothing Then Return _mapUpsSnapshots
        If _mapUpsReadTask IsNot Nothing AndAlso _mapUpsReadTask.IsCompleted Then
            Dim result = _mapUpsReadTask.GetAwaiter().GetResult()
            _mapUpsSnapshots = result.Snapshots
            _mapUpsReadTask = Nothing
            If result.Error <> _displayedMapUpsReadError Then
                _displayedMapUpsReadError = result.Error
                _footerStatus.Text = If(result.Error.Length > 0, "MAP DATA ERROR: " & result.Error, _plc.CurrentStatus.Detail)
            End If
        End If
        If _mapUpsReadTask Is Nothing Then _mapUpsReadTask = ReadMapUpsSnapshotsAsync()
        Return _mapUpsSnapshots
    End Function

    Private Async Function ReadMapUpsSnapshotsAsync() As Task(Of (Snapshots As IReadOnlyDictionary(Of String, UpsSnapshot), [Error] As String))
        Try
            ' Connection validation reads protected profile files; never wait for it on the map UI thread.
            Dim snapshots = Await Task.Run(AddressOf CurrentUpsSnapshots).ConfigureAwait(False)
            Return (snapshots, String.Empty)
        Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is InvalidOperationException
            Return (New Dictionary(Of String, UpsSnapshot)(), "UPS snapshot refresh failed; current data unavailable: " & ex.Message)
        End Try
    End Function

    Private Sub ShowTransformers()
        ShowPage("Transformers")
    End Sub

    Private Async Sub OnProbeClicked(sender As Object, e As EventArgs)
        Await ProbeAsync()
    End Sub

    Private Async Function ProbeAsync() As Threading.Tasks.Task
        Try
            Await _plc.PollNowAsync()
        Catch ex As OperationCanceledException When IsDisposed
        End Try
    End Function

    Private Async Function SavePlcSettingsAsync(settings As PlcSettings, refreshIntervalMilliseconds As Integer) As Task
        If Not PlcConfigurationAccess.CanEdit(_session.RoleName, _session.IsOfflinePreview) Then
            Throw New UnauthorizedAccessException("Only an authenticated administrator can edit PLC acquisition settings.")
        End If
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        settings.Validate()
        Dim priorUiSettings As New UiSettings With {
            .EnableOfflinePreview = _settings.Ui.EnableOfflinePreview,
            .RefreshIntervalMilliseconds = _settings.Ui.RefreshIntervalMilliseconds
        }
        Dim updatedUiSettings As New UiSettings With {
            .EnableOfflinePreview = _settings.Ui.EnableOfflinePreview,
            .RefreshIntervalMilliseconds = refreshIntervalMilliseconds
        }
        PlcSettingsStore.Save(settings, _session.Username, _settings.Plc)
        UiSettingsStore.Save(updatedUiSettings, _session.Username, priorUiSettings)
        _settings.Plc = settings
        _settings.Ui.RefreshIntervalMilliseconds = refreshIntervalMilliseconds
        _uiRefreshTimer.Interval = GetUiRefreshInterval(refreshIntervalMilliseconds)
        InvalidateCachedPages("Overview", "Transformers", "Configuration", "Recording & Historian")
        Await _plc.ConfigureAsync(settings)
        Dim configurationView As Control = Nothing
        If _pageCache.TryGetValue("Configuration", configurationView) AndAlso TypeOf configurationView Is ConfigurationView Then
            DirectCast(configurationView, ConfigurationView).UpdatePlcSettings(settings, refreshIntervalMilliseconds)
        End If
        Dim historianView As Control = Nothing
        If _pageCache.TryGetValue("Recording & Historian", historianView) AndAlso TypeOf historianView Is HistorianView Then
            DirectCast(historianView, HistorianView).UpdateAcquisitionSettings(
                settings, refreshIntervalMilliseconds, _plc.CurrentStatus, _plc.CurrentReadings)
        End If
    End Function

    Private Sub InvalidateCachedPages(ParamArray pageKeys As String())
        For Each pageKey In pageKeys
            Dim page As Control = Nothing
            If String.Equals(pageKey, _activePage, StringComparison.Ordinal) OrElse
               Not _pageCache.TryGetValue(pageKey, page) Then Continue For
            _pageCache.Remove(pageKey)
            _pageHost.Controls.Remove(page)
            page.Dispose()
        Next
    End Sub

    Private Function SaveRecordingSettingsAsync(settings As RecordingSettings) As Task
        If Not PlcConfigurationAccess.CanEdit(_session.RoleName, _session.IsOfflinePreview) Then
            Throw New UnauthorizedAccessException("Only an authenticated administrator can edit historian recording settings.")
        End If
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        RecordingSettingsStore.Save(settings, _session.Username, _session.RoleName, _recordingSettings)
        SyncLock _recordingGate
            _recordingSettings = settings
        End SyncLock
        _exportScheduler.UpdateSettings(settings)
        _historian.UpdateRetention(settings.HistorianRetentionDays)
        _backupService.UpdateSettings(_backupSettings, settings)
        InvalidateCachedPages("Recording & Historian")
        Return Task.CompletedTask
    End Function

    Private Function SaveBackupSettingsAsync(settings As BackupSettings) As Task
        BackupSettingsStore.Save(settings, _session.Username, _session.RoleName, _backupSettings)
        _backupSettings = settings
        _backupService.UpdateSettings(settings, _recordingSettings)
        Return Task.CompletedTask
    End Function

    Private Async Function PrepareRestoreAndShutdownAsync(backupPath As String) As Task(Of RestorePreparationResult)
        Try
            Await _plc.StopAsync()
            Await StopUpsServicesAsync()
            Await PlcReadingsLog.StopAsync()
            Await _exportScheduler.StopAsync()
            Await _backupService.StopAsync()
            Return Await _backupService.PrepareRestoreAsync(backupPath)
        Catch ex As Exception
            MessageBox.Show(Me,
                            "Restore preparation failed after acquisition and logging were stopped for a consistent rollback snapshot. The application will close; restart it to resume normal operation." &
                            Environment.NewLine & Environment.NewLine & ex.Message,
                            "Restore preparation failed",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error)
            Close()
            Throw
        End Try
    End Function

    Private Sub OnPlcStatusChanged(sender As Object, status As PlcConnectionStatus)
        SyncLock _pendingUpdateGate
            _pendingStatus = status
            _hasPendingStatus = True
        End SyncLock
    End Sub

    Private Sub OnPlcReadingsChanged(sender As Object, snapshots As IReadOnlyList(Of MeterSnapshot))
        _energyToday.Update(snapshots)
        If _alarmRecorder IsNot Nothing AndAlso snapshots IsNot Nothing AndAlso snapshots.Count = 4 Then
            Try
                Dim policy = AlarmSettingsStore.Current
                For index = 0 To 3
                    Dim snapshot = snapshots(index)
                    Dim state = EquipmentAlarmRecorder.TransformerEvidence(snapshot.DecodedValues, snapshot.Quality.ToString(),
                        policy.Transformers, _settings.Plc.Blocks(index).MaxLoadKw, powerState:=snapshot.PowerState,
                        voltageEvaluable:=snapshot.PowerState = TransformerPowerState.On,
                        ecgOff:=If(snapshots.All(Function(item) item.Quality = MeterQuality.Good AndAlso item.PowerState <> TransformerPowerState.Unknown),
                            CType(snapshot.EcgOffConfirmed, Boolean?), Nothing))
                    state.Conditions.Remove("ecg-off")
                    _alarmRecorder.Observe("Transformer", snapshot.TransformerName, snapshot.CapturedAtUtc, state)
                Next
                Dim good = snapshots.All(Function(snapshot) snapshot.Quality = MeterQuality.Good AndAlso snapshot.DecodedValues IsNot Nothing)
                Dim totalChannels As Single() = Nothing
                Dim temperature As Double? = Nothing
                If good AndAlso LoadGaugeMath.CalculateTotalLoad(snapshots).HasValue Then
                    totalChannels = ElectricalCalculations.TotalChannels(snapshots.Select(Function(snapshot) CType(snapshot.DecodedValues, IReadOnlyList(Of Single))).ToArray()).
                        Select(Function(value) CSng(value)).ToArray()
                    temperature = snapshots.Where(Function(snapshot) Single.IsFinite(snapshot.DecodedValues(15))).
                        Select(Function(snapshot) CType(snapshot.DecodedValues(15), Double?)).DefaultIfEmpty(Nothing).Max()
                End If
                Dim trusted = snapshots.All(Function(item) item.Quality = MeterQuality.Good AndAlso item.PowerState <> TransformerPowerState.Unknown)
                ' One outage = one ECG off until all four transformers are confirmed on again; unreadable or in-between data never closes it.
                If trusted Then
                    If snapshots.All(Function(item) item.EcgOffConfirmed) Then
                        _ecgOutageOpen = True
                    ElseIf _ecgOutageOpen AndAlso snapshots.All(Function(item) item.PowerState = TransformerPowerState.On) Then
                        _ecgOutageOpen = False
                        _ecgBackUntilUtc = DateTime.UtcNow.AddSeconds(PowerEventTracker.EcgBackSeconds)
                    End If
                    If _ecgOutageOpen Then _ecgBackUntilUtc = DateTime.MinValue
                End If
                Dim totalState = EquipmentAlarmRecorder.TransformerEvidence(totalChannels, If(totalChannels Is Nothing, "BadQuality", "Good"),
                    policy.TotalLoad, _settings.Plc.TotalMaxLoadKw, temperature,
                    voltageEvaluable:=snapshots.All(Function(item) item.PowerState = TransformerPowerState.On),
                    ecgOff:=If(trusted, CType(_ecgOutageOpen, Boolean?), Nothing),
                    ecgBack:=If(trusted, CType(DateTime.UtcNow < _ecgBackUntilUtc, Boolean?), Nothing))
                totalState.Conditions.Remove("transformer-off")
                _alarmRecorder.Observe("Transformer", "TOTAL", snapshots.Max(Function(snapshot) snapshot.CapturedAtUtc), totalState)
                Threading.Volatile.Write(_alarmRecordingError, String.Empty)
            Catch ex As Exception When TypeOf ex Is Microsoft.Data.Sqlite.SqliteException OrElse TypeOf ex Is IO.IOException OrElse
                                       TypeOf ex Is UnauthorizedAccessException
                Threading.Volatile.Write(_alarmRecordingError, "Transformer raw/alarm recording failed: " & ex.Message)
            End Try
        End If
        If snapshots IsNot Nothing AndAlso snapshots.Count = 4 AndAlso snapshots(0).AcquisitionCycleId > 0 Then
            Dim capturedAt = snapshots.Where(Function(snapshot) snapshot IsNot Nothing AndAlso snapshot.CapturedAtUtc.HasValue).
                Select(Function(snapshot) snapshot.CapturedAtUtc.Value).DefaultIfEmpty(DateTime.MinValue).Max()
            If capturedAt <> DateTime.MinValue Then
                SyncLock _recordingGate
                    If _recordingSettings.IsRawRecordingDue(capturedAt, _lastHistorianRecordingUtc) Then
                        If _historian.TryEnqueue(snapshots(0).AcquisitionCycleId, snapshots) Then
                            _lastHistorianRecordingUtc = capturedAt
                        End If
                    End If
                End SyncLock
            End If
        End If
        SyncLock _pendingUpdateGate
            _pendingReadings = snapshots
            _hasPendingReadings = True
        End SyncLock
    End Sub

    Private Sub OnUiRefreshTick(sender As Object, e As EventArgs)
        If WindowState = FormWindowState.Minimized Then Return
        If TypeOf _currentView Is FactoryMapView Then
            Dim mapTicks = Stopwatch.GetTimestamp()
            If Stopwatch.GetElapsedTime(_lastMapRefreshTicks, mapTicks) >= DiagnosticsRefreshInterval Then
                _lastMapRefreshTicks = mapTicks
                DirectCast(_currentView, FactoryMapView).SetAlarmRatings(_settings.Plc)
                DirectCast(_currentView, FactoryMapView).SetReadings(_plc.CurrentReadings, CurrentMapUpsSnapshots())
            End If
        End If
        Dim status As PlcConnectionStatus = Nothing
        Dim readings As IReadOnlyList(Of MeterSnapshot) = Nothing
        Dim hasStatus As Boolean
        Dim hasReadings As Boolean
        SyncLock _pendingUpdateGate
            hasStatus = _hasPendingStatus
            hasReadings = _hasPendingReadings
            If Not hasStatus AndAlso Not hasReadings Then Return
            status = _pendingStatus
            readings = _pendingReadings
            _hasPendingStatus = False
            _hasPendingReadings = False
        End SyncLock

        Dim nowTicks = Stopwatch.GetTimestamp()
        _uiRenderTicks.Enqueue(nowTicks)
        While _uiRenderTicks.Count > 1 AndAlso Stopwatch.GetElapsedTime(_uiRenderTicks.Peek(), nowTicks) > TimeSpan.FromMinutes(1)
            _uiRenderTicks.Dequeue()
        End While
        If _uiRenderTicks.Count > 1 Then
            Dim interval = Stopwatch.GetElapsedTime(_uiRenderTicks.Peek(), _uiRenderTicks.Last()).TotalSeconds
            If interval > 0 Then _uiRefreshRateHz = (_uiRenderTicks.Count - 1) / interval
        End If

        If hasStatus AndAlso status IsNot Nothing Then
            _plcPill.SetStatus(status)
            If TypeOf _currentView Is DashboardView Then DirectCast(_currentView, DashboardView).SetPlcStatus(status)
            If TypeOf _currentView Is TransformersView Then DirectCast(_currentView, TransformersView).SetPlcStatus(status)
            If TypeOf _currentView Is SystemDataView Then DirectCast(_currentView, SystemDataView).SetPlcStatus(status)
        End If
        If hasReadings AndAlso TypeOf _currentView Is TransformersView Then
            DirectCast(_currentView, TransformersView).SetReadings(readings)
        End If
        If hasReadings AndAlso TypeOf _currentView Is DashboardView Then
            DirectCast(_currentView, DashboardView).SetReadings(readings)
        End If
        If hasReadings AndAlso TypeOf _currentView Is SystemDataView Then
            DirectCast(_currentView, SystemDataView).SetReadings(readings)
        End If
        For Each detached In ViewFullScreen.DetachedViews()
            If ReferenceEquals(detached, _currentView) Then Continue For
            If TypeOf detached Is TransformersView Then
                If hasStatus AndAlso status IsNot Nothing Then DirectCast(detached, TransformersView).SetPlcStatus(status)
                If hasReadings Then DirectCast(detached, TransformersView).SetReadings(readings)
            End If
        Next

        Dim displayedStatus = If(status, _plc.CurrentStatus)
        ' Diagnostics, footer text and diagnostic panels change slowly; refresh them at most ~4 Hz.
        If Stopwatch.GetElapsedTime(_lastDiagnosticsTicks, nowTicks) >= DiagnosticsRefreshInterval Then
            _lastDiagnosticsTicks = nowTicks
            Dim historian = _historian.GetDiagnostics()
            Dim operationalLog = PlcReadingsLog.GetDiagnostics()
            If TypeOf _currentView Is DashboardView Then
                DirectCast(_currentView, DashboardView).SetRuntimeDiagnostics(displayedStatus, historian, operationalLog)
            End If
            _footerStatus.Text = displayedStatus.Detail &
                "  •  UI " & _uiRefreshRateHz.ToString("0.0", CultureInfo.InvariantCulture) & " Hz" &
                "  •  History queue " & historian.QueueDepth.ToString(CultureInfo.InvariantCulture) &
                "  •  dropped " & historian.DroppedCycles.ToString(CultureInfo.InvariantCulture) &
                "  •  Attempt-log queue " & operationalLog.QueueDepth.ToString(CultureInfo.InvariantCulture) &
                "  •  dropped " & operationalLog.DroppedAttempts.ToString(CultureInfo.InvariantCulture)
            If _recordingOutbox IsNot Nothing Then
                _footerStatus.Text &= "  •  UPS/alarm buffer " & _recordingOutbox.PendingCount.ToString(CultureInfo.InvariantCulture)
                Dim recordingError = Threading.Volatile.Read(_alarmRecordingError)
                If recordingError.Length = 0 Then recordingError = _recordingOutbox.LastError
                If recordingError.Length > 0 Then _footerStatus.Text &= "  •  RECORDING ERROR: " & recordingError
            End If
            If TypeOf _currentView Is FactoryMapView AndAlso _displayedMapUpsReadError.Length > 0 Then
                _footerStatus.Text &= "  •  MAP DATA ERROR: " & _displayedMapUpsReadError
            End If
            If TypeOf _currentView Is SystemDataView Then
                DirectCast(_currentView, SystemDataView).SetRuntimeDiagnostics(displayedStatus, _uiRefreshRateHz, historian, operationalLog)
            End If
        End If
        If TypeOf _currentView Is HistorianView Then
            DirectCast(_currentView, HistorianView).SetAcquisitionSnapshot(
                displayedStatus, If(hasReadings, readings, _plc.CurrentReadings),
                _settings.Ui.RefreshIntervalMilliseconds, _uiRefreshRateHz)
        End If
    End Sub

    Private Shared Function GetUiRefreshInterval(configuredMilliseconds As Integer) As Integer
        Return If(configuredMilliseconds = 0, 100, Math.Max(1, configuredMilliseconds))
    End Function
End Class
