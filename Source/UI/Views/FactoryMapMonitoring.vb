Imports System.Drawing
Imports System.IO
Imports System.Linq
Imports System.Text.Json
Imports System.Windows.Forms

Partial Public NotInheritable Class FactoryMapView
    Private _monitoringRegistry As New MonitoringDeviceRegistry
    Private _monitoringRevision As Integer = -1
    Private _monitoringLoadError As String = ""
    Private _firePanels As FirePanelManager
    Private _liveMonitoring As IReadOnlyDictionary(Of String, MonitoringSample) = New Dictionary(Of String, MonitoringSample)
    Private _monitoringSession As UserSession
    Private ReadOnly _fireAlertsButton As Button = UiTheme.ActionButton("Fire alerts (0)", False)
    Private _latchedMatches As New List(Of FireMapMatch)
    Private _latchedSignature As String = ""
    Private _latchedLocationEvidence As IReadOnlyDictionary(Of String, IReadOnlyList(Of FireMapEventEvidence)) =
        New Dictionary(Of String, IReadOnlyList(Of FireMapEventEvidence))

    Public Sub SetFirePanels(panels As FirePanelManager)
        _firePanels = panels
        RefreshMonitoring()
        LoadFireEventHistory(panels)
    End Sub

    Private Async Sub LoadFireEventHistory(panels As FirePanelManager)
        If panels Is Nothing Then Return
        Try
            For Each panel In panels.Panels
                Await panel.Service.PrepareMapEventHistoryAsync()
            Next
            If Not IsDisposed AndAlso ReferenceEquals(_firePanels, panels) Then
                RefreshMonitoring()
                If _canvas.SelectedAsset IsNot Nothing Then UpdateSelection(_canvas.SelectedAsset)
            End If
        Catch ex As OperationCanceledException
            If Not IsDisposed Then
                _layoutStatus.Text = "Loading stored map fire alerts was cancelled; existing latched alerts are retained."
            End If
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
            TypeOf ex Is JsonException OrElse TypeOf ex Is Security.Cryptography.CryptographicException OrElse TypeOf ex Is FormatException OrElse TypeOf ex Is Microsoft.Data.Sqlite.SqliteException
            If Not IsDisposed Then
                _layoutStatus.Text = "Stored fire events could not be loaded onto the map: " & ex.Message
                _layoutStatus.ForeColor = UiTheme.Red
            End If
        End Try
    End Sub

    Public Sub SetAlarmRatings(settings As PlcSettings)
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        _canvas.SetAlarmRatings(settings)
    End Sub

    Public Sub SetMonitoringSamples(samples As IReadOnlyDictionary(Of String, MonitoringSample))
        If samples Is Nothing Then Throw New ArgumentNullException(NameOf(samples))
        Dim copy As New Dictionary(Of String, MonitoringSample)(StringComparer.Ordinal)
        For Each entry In samples
            If entry.Value Is Nothing OrElse entry.Key <> entry.Value.DeviceId OrElse Not [Enum].IsDefined(entry.Value.Signal) Then Throw New ArgumentException("Monitoring samples require matching device IDs and a supported signal.", NameOf(samples))
            copy.Add(entry.Key, entry.Value.Clone())
        Next
        _liveMonitoring = copy
        RefreshMonitoring()
        UpdateSummary()
        UpdateSelection(_canvas.SelectedAsset)
    End Sub

    Private Sub RefreshMonitoring()
        If _monitoringRevision <> MonitoringDeviceStore.Revision Then
            _monitoringRevision = MonitoringDeviceStore.Revision
            Try
                _monitoringRegistry = MonitoringDeviceStore.Load()
                _monitoringLoadError = ""
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is UnauthorizedAccessException
                _monitoringRegistry = New MonitoringDeviceRegistry
                _monitoringLoadError = "Monitoring registry unavailable: " & ex.Message
                _layoutStatus.Text = _monitoringLoadError
            End Try
        End If
        Dim evidence As New Dictionary(Of String, IReadOnlyList(Of FireMapEventEvidence))
        If _firePanels IsNot Nothing Then
            For Each panel In _firePanels.Panels
                evidence.Add(panel.Id, panel.Service.ReadMapEventEvidence())
            Next
        End If
        _canvas.SetMonitoring(_monitoringRegistry, evidence, _liveMonitoring)
        RefreshLatchedEvents()
    End Sub

    Private Sub RefreshLatchedEvents()
        Dim pending As New List(Of (Panel As String, EventItem As FireMapEventEvidence))
        Try
            Dim related As New Dictionary(Of String, IReadOnlyList(Of FireMapEventEvidence))(StringComparer.Ordinal)
            If _firePanels IsNot Nothing Then
                For Each panel In _firePanels.Panels
                    pending.AddRange(panel.Service.ReadLatchedMapEvents().Select(Function(item) (panel.Id, item)))
                    related.Add(panel.Id, panel.Service.ReadMapLocationEvidence())
                Next
            End If
            Dim signature = String.Join("|", pending.Select(Function(entry) entry.Panel & ":" & entry.EventItem.SiteId & ":" & entry.EventItem.EventId & ":" &
                entry.EventItem.TimestampUtc.ToString() & ":" & entry.EventItem.Description & ":" & entry.EventItem.Message)) & "/" & _monitoringRevision &
                "/" & String.Join("|", _assets.Select(Function(asset) String.Join(":", {asset.Id, asset.Name, asset.Model, asset.MonitoringDeviceId, asset.RoomId, asset.Level, asset.IsPlaced.ToString()}))) &
                "/" & String.Join("|", _building.Select(Function(part) String.Join(":", {part.Id, part.RoomId, part.RoomName, part.Level, part.FirePanelId, part.FireSiteId, part.FireZone})))
            Dim sameContext = related.Count = _latchedLocationEvidence.Count AndAlso related.All(Function(entry)
                Dim previous As IReadOnlyList(Of FireMapEventEvidence) = Nothing
                Return _latchedLocationEvidence.TryGetValue(entry.Key, previous) AndAlso ReferenceEquals(previous, entry.Value)
            End Function)
            If signature = _latchedSignature AndAlso sameContext Then Return
            Dim singleSource = _firePanels IsNot Nothing AndAlso _firePanels.Panels.Count = 1 AndAlso pending.Select(Function(entry) entry.EventItem.SiteId).Distinct().Count() <= 1
            Dim resolved As New Dictionary(Of String, FireMapMatch)(StringComparer.Ordinal)
            Dim matches As New List(Of FireMapMatch)
            Dim locations = related.ToDictionary(Function(entry) entry.Key,
                Function(entry) FireEventLocation.ResolveRelated(entry.Value.Concat(pending.Where(Function(value) value.Panel = entry.Key).
                    Select(Function(value) value.EventItem)).DistinctBy(Function(item) item.EventKey).ToArray()))
            For Each entry In pending
                Dim item = entry.EventItem
                Dim location = locations(entry.Panel)(item.EventKey)
                Dim key = JsonSerializer.Serialize({entry.Panel, item.SiteId, item.Address, item.Loop, item.Zone, item.Description, item.Message,
                    item.IdentityConflict.ToString(), location.Display, location.Source, location.DescribedPanel})
                Dim target As FireMapMatch = Nothing
                If Not resolved.TryGetValue(key, target) Then
                    target = FireMapEventResolver.ResolveLocated(entry.Panel, item, _assets, _building, _monitoringRegistry, singleSource, location)
                    resolved.Add(key, target)
                End If
                matches.Add(New FireMapMatch With {.PanelId = entry.Panel, .Evidence = item, .AssetId = target.AssetId,
                    .RoomId = target.RoomId, .Level = target.Level, .MatchReason = target.MatchReason, .LocationInfo = location})
            Next
            _latchedMatches = matches
            _latchedSignature = signature
            _latchedLocationEvidence = related
            _canvas.SetEventAlerts(_latchedMatches)
            Dim unmapped = _latchedMatches.Where(Function(item) item.AssetId = "" AndAlso item.RoomId = "").Count()
            _fireAlertsButton.Text = "Fire alerts (" & _latchedMatches.Count & "; unmapped " & unmapped & ")"
            _fireAlertsButton.ForeColor = If(_latchedMatches.Count > 0, UiTheme.Amber, UiTheme.Ink)
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is Security.Cryptography.CryptographicException
            _layoutStatus.Text = "Map fire alert storage unavailable: " & ex.Message
            _layoutStatus.ForeColor = UiTheme.Red
            _fireAlertsButton.Text = "Fire alerts - STORAGE ERROR"
            _fireAlertsButton.ForeColor = UiTheme.Red
        End Try
    End Sub

    Private Sub ShowFireAlerts()
        RefreshMonitoring()
        Dim displayed = _latchedMatches.ToArray()
        Using dialog As New Form With {.Text = "Latched fire events - local map indications", .StartPosition = FormStartPosition.CenterParent,
            .Size = New Size(1120, 620), .MinimumSize = New Size(800, 420), .Font = Font, .BackColor = UiTheme.Canvas}
            Dim grid As New DataGridView With {.Dock = DockStyle.Fill, .ReadOnly = True, .MultiSelect = False,
                .SelectionMode = DataGridViewSelectionMode.FullRowSelect}
            TransformerDetailForm.ConfigureGrid(grid, {"Type", "Event UTC", "Source panel / site", "Address / loop / zone", "Area / Location", "Described Panel", "Description", "Map match"})
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            Dim detail As New TextBox With {.Dock = DockStyle.Bottom, .Height = 130, .Multiline = True, .ReadOnly = True, .ScrollBars = ScrollBars.Vertical}
            Dim notice As New Label With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(8),
                .Text = "FIRE: steady red. FAULT: blinking orange. Historical events latch until local reset, including across restart." & vbCrLf &
                    "Current panel condition remains UNKNOWN. Inferred room matches do not identify a specific detector; unmapped events require commissioning."}
            Dim actions As New FlowLayoutPanel With {.Dock = DockStyle.Bottom, .AutoSize = True, .Padding = New Padding(8)}
            Dim reset = UiTheme.ActionButton("Reset Map Alarms", False)
            reset.AutoSize = True
            reset.Enabled = _canEditMap AndAlso Not _editing AndAlso Not _buildingEditing
            Dim focus = UiTheme.ActionButton("Show on map", False)
            focus.AutoSize = True
            actions.Controls.AddRange({focus, reset})
            For Each match In displayed.OrderByDescending(Function(item) item.Evidence.Signal).ThenByDescending(Function(item) item.Evidence.TimestampUtc)
                Dim item = match.Evidence
                Dim row = grid.Rows.Add(If(item.Signal = MapSignal.Fire, "FIRE", "FAULT"), item.TimestampUtc?.ToString("u"),
                    match.PanelId & " / " & item.SiteId, item.Address & " / " & item.Loop & " / " & item.Zone,
                    If(match.LocationInfo, item.LocationInfo).Display, If(match.LocationInfo, item.LocationInfo).DescribedPanel, item.Description, match.MatchReason)
                grid.Rows(row).Tag = match
            Next
            AddHandler grid.SelectionChanged, Sub()
                                                  Dim match = TryCast(grid.CurrentRow?.Tag, FireMapMatch)
                                                  detail.Text = If(match Is Nothing, "", match.Indication().Detail)
                                                  focus.Enabled = match IsNot Nothing AndAlso (match.AssetId <> "" OrElse match.RoomId <> "")
                                              End Sub
            AddHandler focus.Click, Sub()
                                        Dim match = TryCast(grid.CurrentRow?.Tag, FireMapMatch)
                                        If match Is Nothing OrElse match.Level = "" Then Return
                                        _canvas.SetLevel(match.Level)
                                        If match.AssetId <> "" Then
                                            _canvas.SelectAsset(_assets.First(Function(asset) asset.Id = match.AssetId))
                                        Else
                                            _canvas.SelectBuildingPart(_building.First(Function(part) part.RoomId = match.RoomId AndAlso part.Level = match.Level))
                                        End If
                                        _canvas.FocusSelection()
                                        dialog.Close()
                                    End Sub
            AddHandler reset.Click, Async Sub()
                                        If MessageBox.Show(dialog, "Clear the currently received, latched events from this map?" & vbCrLf &
                                            "No reset/silence command will be sent to a fire panel. New event IDs will raise the indication again; an unchanged historical event will not." &
                                            vbCrLf & "Verified live alarms cannot be cleared here.", "Reset Map Alarms", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) <> DialogResult.Yes Then Return
                                        reset.Enabled = False
                                        Try
                                            If _firePanels IsNot Nothing Then
                                                For Each panel In _firePanels.Panels
                                                    Dim shown = displayed.Where(Function(item) item.PanelId = panel.Id).Select(Function(item) item.Evidence).ToArray()
                                                    Await Task.Run(Sub() panel.Service.ResetMapAlerts(_monitoringSession, shown))
                                                Next
                                            End If
                                            RefreshMonitoring()
                                            dialog.Close()
                                        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is Security.Cryptography.CryptographicException
                                            detail.Text = "Reset did not complete for all panels: " & ex.Message & vbCrLf & "Inspect remaining alerts; no panel command was sent."
                                            RefreshMonitoring()
                                            reset.Enabled = _canEditMap
                                        End Try
                                    End Sub
            dialog.Controls.Add(grid)
            dialog.Controls.Add(detail)
            dialog.Controls.Add(actions)
            dialog.Controls.Add(notice)
            If grid.Rows.Count > 0 Then
                grid.CurrentCell = grid.Rows(0).Cells(0)
                detail.Text = DirectCast(grid.Rows(0).Tag, FireMapMatch).Indication().Detail
            End If
            dialog.ShowDialog(Me)
        End Using
    End Sub

    Private Function ShowMonitoringDetails(asset As FactoryMapAsset) As Boolean
        If FactoryMapMeterLink.Supports(asset.Model) Then Return False
        If asset.Category = "Battery Rooms" Then
            Dim ups As UpsSnapshot = Nothing
            _upsSnapshots.TryGetValue(asset.ParentUpsId, ups)
            If ups IsNot Nothing AndAlso Not String.Equals(ups.DeviceId, asset.ParentUpsId, StringComparison.OrdinalIgnoreCase) Then ups = Nothing
            Dim indication = _canvas.Indication(asset)
            _selectedStatus.Text = indication.Text
            _selectedStatus.ForeColor = FactoryMapCanvas.SignalColor(indication.Signal)
            Dim mode = FactoryMapMonitoring.UpsBatteryMode(ups)
            _selectedDetails.Text = String.Join(Environment.NewLine, {
                "Rack: " & asset.Name, "Level / location: " & asset.Level & " / " & asset.Location,
                "Parent UPS: " & If(asset.ParentUpsId = "", "None selected", asset.ParentUpsId),
                "Battery mode: " & mode,
                "Voltage: " & UpsReading(ups, Function(item) item.Value("BatteryVoltage"), "V"),
                "Current (+ discharge / - charge): " & UpsReading(ups, Function(item) item.Value("BatteryCurrent"), "A"),
                "Temperature: " & UpsReading(ups, Function(item) item.Value("BatteryTemperature"), "C"),
                "Charge: " & UpsReading(ups, Function(item) item.Value("ChargePercent"), "%"),
                "Runtime: " & UpsReading(ups, Function(item) item.Value("RuntimeMinutes"), "min"),
                "Readings are the PARENT UPS battery bank, NOT individual rack/BMS measurements.",
                "Individual rack voltage/temperature needs a separate commissioned BMS source.",
                indication.Detail})
            Return True
        End If
        If asset.EquipmentId.StartsWith("UPS-", StringComparison.OrdinalIgnoreCase) Then Return False
        If asset.MonitoringDeviceId = "" AndAlso asset.CameraIpAddress = "" AndAlso Not {"CCTV", "Access Control", "Fire System"}.Contains(asset.Category) Then Return False
        Dim device = _monitoringRegistry.Devices.FirstOrDefault(Function(item) item.Id = asset.MonitoringDeviceId)
        Dim status = _canvas.Indication(asset)
        _selectedStatus.Text = status.Text
        _selectedStatus.ForeColor = FactoryMapCanvas.SignalColor(status.Signal)
        _selectedDetails.Text = String.Join(Environment.NewLine, {
            "Map item: " & asset.Name & " / " & asset.Model,
            "Level: " & asset.Level,
            "Registered device: " & If(device Is Nothing, If(asset.MonitoringDeviceId = "", "None", "Unavailable [" & asset.MonitoringDeviceId & "]"), device.Name & " [" & device.Id & "]"),
            "Drawing tag: " & If(device Is Nothing OrElse device.DrawingTag = "", "--", device.DrawingTag),
            "Drawing reference: " & If(device Is Nothing OrElse device.DrawingReference = "", "--", device.DrawingReference),
            "Addressing: " & If(device Is Nothing OrElse device.Kind <> "Fire", "--", If(device.Commissioned, "Verified", "NOT commissioned - drawing tag is not a panel address")),
            "IP address: " & If(asset.CameraIpAddress <> "", asset.CameraIpAddress, If(device Is Nothing OrElse device.IpAddress = "", "--", device.IpAddress)),
            "External camera / door ID: " & If(device Is Nothing OrElse device.ExternalId = "", "--", device.ExternalId),
            "Fire panel / loop / address: " & If(device Is Nothing OrElse device.Kind <> "Fire", "--", device.PanelId & " / " & device.Loop & " / " & device.Address),
            "Site / zone: " & If(device Is Nothing, "--", device.SiteId & " / " & device.Zone),
            "Location: " & If(device Is Nothing, asset.Location, device.Location),
            "Room reference: " & If(asset.RoomId = "", "None", If(_building.Any(Function(part) part.RoomId = asset.RoomId),
                _building.First(Function(part) part.RoomId = asset.RoomId).RoomName, "Unavailable [" & asset.RoomId & "]")),
            "Mount: " & _canvas.MountDescription(asset),
            status.Detail, _monitoringLoadError,
            "Read-only. No camera, door, reset, silence or suppression commands."})
        Return True
    End Function

    Private Function HasCurrentSource(asset As FactoryMapAsset) As Boolean
        If FactoryMapMeterLink.Supports(asset.Model) Then Return FindSnapshot(asset) IsNot Nothing
        Dim upsId = If(asset.Category = "Battery Rooms", asset.ParentUpsId, asset.EquipmentId)
        If upsId.StartsWith("UPS-", StringComparison.OrdinalIgnoreCase) Then
            Dim ups As UpsSnapshot = Nothing
            Return _upsSnapshots.TryGetValue(upsId, ups) AndAlso FactoryMapMonitoring.IsCurrentUps(ups) AndAlso String.Equals(ups.DeviceId, upsId, StringComparison.OrdinalIgnoreCase)
        End If
        If asset.MonitoringDeviceId <> "" Then
            Dim device = _monitoringRegistry.Devices.FirstOrDefault(Function(item) item.Id = asset.MonitoringDeviceId)
            Dim sample As MonitoringSample = Nothing
            Return FactoryMapMonitoring.DeviceMatches(asset, device) AndAlso (device.Kind <> "Fire" OrElse device.Commissioned) AndAlso
                _liveMonitoring.TryGetValue(asset.MonitoringDeviceId, sample) AndAlso sample.IsCurrent
        End If
        Return FindSnapshot(asset) IsNot Nothing
    End Function

    Private Sub ShowRoomDetails(part As FactoryBuildingPart)
        _selectedTitle.Text = If(part.RoomName = "", part.Name, part.RoomName & " / " & part.Name)
        _openTransformers.Visible = False
        Dim value = FactoryMapMonitoring.Temperature(part, _snapshots)
        Dim signal = AlarmSettingsStore.Current.Rooms.Evaluate(value)
        Dim roomFire = _canvas.RoomHasFire(part.RoomId)
        _selectedStatus.Text = If(roomFire, "FIRE - VERIFIED LIVE DEVICE", If(part.RoomId = "", "BUILDING ELEMENT", "ROOM TEMPERATURE / " & signal.ToString()))
        _selectedStatus.ForeColor = FactoryMapCanvas.SignalColor(If(roomFire, MapSignal.Fire, signal))
        Dim device = _monitoringRegistry.Devices.FirstOrDefault(Function(item) item.Id = part.MonitoringDeviceId)
        Dim sample As MonitoringSample = Nothing
        Dim doorText = "No commissioned live contact / personnel feed; drawing state is visual only."
        If device IsNot Nothing AndAlso device.Kind = "Access" AndAlso part.MonitoringDeviceId <> "" AndAlso _liveMonitoring.TryGetValue(part.MonitoringDeviceId, sample) AndAlso sample.IsCurrent Then
            Dim doorSignal = If(sample.Signal = MapSignal.Fire, MapSignal.Fault, sample.Signal)
            doorText = FactoryMapMonitoring.DoorState(sample) & " / " & doorSignal.ToString() & ": " & sample.Detail
            If Not roomFire Then
                _selectedStatus.Text = doorText
                _selectedStatus.ForeColor = FactoryMapCanvas.SignalColor(doorSignal)
            End If
        End If
        _selectedDetails.Text = String.Join(Environment.NewLine, {
            part.Kind & " / " & part.Material,
            "Temperature source: " & If(part.TemperatureSourceId = "", "None selected", part.TemperatureSourceId),
            "Room temperature: " & If(value.HasValue, value.Value.ToString("0.0") & " C", "-- (missing / stale / source not commissioned)"),
            "Fire panel / site / zone: " & part.FirePanelId & " / " & part.FireSiteId & " / " & part.FireZone,
            If(roomFire, "Fire status: FIRE reported by a verified current device sample.",
               "Fire status: UNKNOWN unless supplied by a commissioned live adapter; event history alone is not live state."),
            "Access device: " & If(device Is Nothing, If(part.MonitoringDeviceId = "", "None", "Unavailable"), device.Name),
            doorText, _canvas.RoomEventDetails(part.RoomId, part.Level),
            "Temperature alarms affect walls only. Verified fire affects walls AND floor; a device fault affects only its symbol."})
    End Sub
End Class

Partial Friend NotInheritable Class FactoryMapCanvas
    Private ReadOnly _blinkTimer As New Timer With {.Interval = 500}
    Private _blinkOn As Boolean = True
    Private _monitorPageActive As Boolean
    Private _alarmOverlayInvalidation As Boolean
    Private _assetSignals As New Dictionary(Of String, MapIndication)
    Private _buildingSignals As New Dictionary(Of String, MapSignal)
    Private _monitoringDevices As New MonitoringDeviceRegistry
    Private _deviceSamples As IReadOnlyDictionary(Of String, MonitoringSample) = New Dictionary(Of String, MonitoringSample)
    Private _doorSignature As String = ""
    Private _alarmRatings As IReadOnlyDictionary(Of String, Double) = New Dictionary(Of String, Double)(StringComparer.OrdinalIgnoreCase)
    Private _eventMatches As IReadOnlyList(Of FireMapMatch) = Array.Empty(Of FireMapMatch)()
    Private _eventAssetSignals As New Dictionary(Of String, MapIndication)

    Public Sub SetEventAlerts(matches As IReadOnlyList(Of FireMapMatch))
        Dim previouslyVisible = _assets.Where(Function(asset) ShouldShowAsset(asset)).Select(Function(asset) asset.Id).ToHashSet()
        _eventMatches = matches
        _eventAssetSignals = matches.Where(Function(item) item.AssetId <> "").GroupBy(Function(item) item.AssetId).
            ToDictionary(Function(group) group.Key, Function(group) group.OrderByDescending(Function(item) item.Evidence.Signal).
                ThenByDescending(Function(item) item.Evidence.TimestampUtc).First().Indication())
        If Not previouslyVisible.SetEquals(_assets.Where(Function(asset) ShouldShowAsset(asset)).Select(Function(asset) asset.Id)) Then
            Invalidate()
        Else
            InvalidateAlarmOverlay()
        End If
        UpdateBlinkTimer()
    End Sub

    Public Function RoomEventDetails(id As String, level As String) As String
        Return String.Join(Environment.NewLine & Environment.NewLine, _eventMatches.Where(Function(item) item.RoomId <> "" AndAlso item.RoomId = id AndAlso item.Level = level).
            Select(Function(item) item.Indication().Detail))
    End Function

    Public Sub SetAlarmRatings(settings As PlcSettings)
        _alarmRatings = settings.Blocks.ToDictionary(Function(block) block.TransformerName, Function(block) block.MaxLoadKw, StringComparer.OrdinalIgnoreCase)
    End Sub

    Friend Shared Function SignalColor(signal As MapSignal) As Color
        If signal = MapSignal.Fire OrElse signal = MapSignal.Fault Then Return UiTheme.Red
        If signal = MapSignal.Warning Then Return UiTheme.Amber
        Return If(signal = MapSignal.Normal, UiTheme.Green, UiTheme.MutedInk)
    End Function

    Public Function Indication(asset As FactoryMapAsset) As MapIndication
        Dim result As MapIndication = Nothing
        _assetSignals.TryGetValue(asset.Id, result)
        Dim eventResult As MapIndication = Nothing
        If _eventAssetSignals.TryGetValue(asset.Id, eventResult) Then
            If result Is Nothing OrElse eventResult.Signal >= result.Signal Then Return eventResult
        End If
        If result IsNot Nothing Then Return result
        Return New MapIndication(MapSignal.Unknown, "No verified monitoring source")
    End Function

    Public Function RoomHasFire(id As String) As Boolean
        Return id <> "" AndAlso _building.Any(Function(part) part.RoomId = id AndAlso
            _buildingSignals.ContainsKey(part.Id) AndAlso _buildingSignals(part.Id) = MapSignal.Fire)
    End Function

    Public Sub SetMonitoring(devices As MonitoringDeviceRegistry, fire As IReadOnlyDictionary(Of String, IReadOnlyList(Of FireMapEventEvidence)),
                             live As IReadOnlyDictionary(Of String, MonitoringSample))
        Dim previouslyVisible = _assets.Where(Function(asset) ShouldShowAsset(asset)).Select(Function(asset) asset.Id).ToHashSet()
        Dim oldSignals = SignalSignature()
        _monitoringDevices = devices
        _deviceSamples = live
        _assetSignals = _assets.ToDictionary(Function(asset) asset.Id, Function(asset) FactoryMapMonitoring.EvaluateAsset(asset, _snapshots, _upsSnapshots, devices, fire, live, _alarmRatings))
        _buildingSignals.Clear()
        For Each part In _building.Where(Function(item) {"Door", "Gate"}.Contains(item.Kind) AndAlso item.MonitoringDeviceId <> "")
            Dim device = devices.Devices.FirstOrDefault(Function(item) item.Id = part.MonitoringDeviceId AndAlso item.Kind = "Access")
            Dim sample As MonitoringSample = Nothing
            If device IsNot Nothing AndAlso live.TryGetValue(device.Id, sample) AndAlso sample.IsCurrent AndAlso sample.Signal >= MapSignal.Warning Then
                _buildingSignals(part.Id) = If(sample.Signal = MapSignal.Fire, MapSignal.Fault, sample.Signal)
            End If
        Next
        For Each room In _building.Where(Function(part) part.RoomId <> "").GroupBy(Function(part) part.RoomId)
            Dim member = room.First()
            Dim temperature = AlarmSettingsStore.Current.Rooms.Evaluate(FactoryMapMonitoring.Temperature(member, _snapshots))
            Dim hasFire = devices.Devices.Where(Function(device) device.Kind = "Fire" AndAlso device.Commissioned).Any(
                Function(device)
                    Dim sample As MonitoringSample = Nothing
                    If Not live.TryGetValue(device.Id, sample) OrElse Not sample.IsCurrent OrElse sample.Signal <> MapSignal.Fire Then Return False
                    Return _assets.Any(Function(asset) asset.Level = member.Level AndAlso asset.Category = "Fire System" AndAlso asset.RoomId = room.Key AndAlso asset.MonitoringDeviceId = device.Id) OrElse
                        (member.FirePanelId <> "" AndAlso member.FireSiteId <> "" AndAlso member.FireZone <> "" AndAlso
                         device.PanelId = member.FirePanelId AndAlso device.SiteId = member.FireSiteId AndAlso device.Zone = member.FireZone)
                End Function)
            For Each part In room
                If hasFire AndAlso {"Wall", "Floor"}.Contains(part.Kind) Then
                    _buildingSignals(part.Id) = MapSignal.Fire
                ElseIf part.Kind = "Wall" AndAlso (temperature = MapSignal.Warning OrElse temperature = MapSignal.Fault) Then
                    _buildingSignals(part.Id) = temperature
                End If
            Next
        Next
        Dim nowVisible = _assets.Where(Function(asset) ShouldShowAsset(asset)).Select(Function(asset) asset.Id).ToHashSet()
        Dim newDoors = String.Join("|", live.OrderBy(Function(entry) entry.Key).Select(Function(entry) entry.Key & ":" & If(entry.Value.IsCurrent, entry.Value.DoorOpen.ToString(), "?")))
        If Not previouslyVisible.SetEquals(nowVisible) OrElse _doorSignature <> newDoors Then
            Invalidate()
        ElseIf oldSignals <> SignalSignature() Then
            InvalidateAlarmOverlay()
        End If
        _doorSignature = newDoors
        UpdateBlinkTimer()
    End Sub

    Private Function SignalSignature() As String
        Return String.Join("|", _assetSignals.Select(Function(entry) entry.Key & ":" & CInt(entry.Value.Signal))) & "/" &
            String.Join("|", _buildingSignals.Select(Function(entry) entry.Key & ":" & CInt(entry.Value)))
    End Function

    Private Function ShouldShowAsset(asset As FactoryMapAsset) As Boolean
        Return asset.Level = ActiveLevel AndAlso (Not FactoryMapMonitoring.ConcealedFire(asset) OrElse EditMode OrElse BuildingEditMode OrElse Indication(asset).Signal >= MapSignal.Warning)
    End Function

    Private Function MountHeight(asset As FactoryMapAsset) As Single
        If FactoryMapMonitoring.IsCeiling(asset.Model) AndAlso asset.RoomId <> "" Then
            Dim walls = _building.Where(Function(part) part.Level = asset.Level AndAlso part.RoomId = asset.RoomId AndAlso part.Kind = "Wall").ToArray()
            If walls.Length > 0 Then Return walls.Max(Function(part) part.BaseMeters + part.HeightMeters)
        End If
        Dim height = If(asset.BaseHeightMeters > 0, asset.BaseHeightMeters, FactoryMapMonitoring.DefaultMountHeight(asset.Model))
        If FactoryMapMonitoring.IsWallMounted(asset.Model) Then
            Dim wall = MountWall(asset)
            If wall IsNot Nothing Then
                Dim physicalHeight = FactoryMapEquipmentSizes.Dimensions(asset).Height * asset.ScaleZ
                Return Math.Clamp(wall.BaseMeters + height, wall.BaseMeters,
                    Math.Max(wall.BaseMeters, wall.BaseMeters + wall.HeightMeters - physicalHeight))
            End If
        End If
        Return height
    End Function

    Private Function MountWall(asset As FactoryMapAsset) As FactoryBuildingPart
        If Not FactoryMapMonitoring.IsWallMounted(asset.Model) Then Return Nothing
        Dim best As FactoryBuildingPart = Nothing
        Dim nearest = Single.MaxValue
        For Each wall In _building
            If wall.Kind <> "Wall" OrElse wall.Level <> asset.Level OrElse
                (asset.RoomId <> "" AndAlso wall.RoomId <> asset.RoomId) Then Continue For
            Dim local = FactoryMapBuildingStore.LocalPoint(wall, New PointF(asset.Position.X * 1365, asset.Position.Y * 766))
            Dim endDistance = Math.Max(0, Math.Abs(local.X) - wall.Length / 2)
            Dim distance = local.Y * local.Y + endDistance * endDistance
            If distance < nearest Then
                nearest = distance
                best = wall
            End If
        Next
        Return best
    End Function

    Private Function MountedAsset(asset As FactoryMapAsset) As FactoryMapAsset
        Dim wall = MountWall(asset)
        If wall Is Nothing Then Return asset
        Dim local = FactoryMapBuildingStore.LocalPoint(wall, New PointF(asset.Position.X * 1365, asset.Position.Y * 766))
        Dim side = If(local.Y < 0, -1.0F, 1.0F)
        Dim size = FactoryMapEquipmentSizes.Dimensions(asset)
        Dim point = FactoryMapBuildingStore.WorldPoint(wall, Math.Clamp(local.X, -wall.Length / 2, wall.Length / 2),
            side * (wall.Depth / 2 + size.Width * asset.ScaleY * 4))
        Dim mounted = asset.Clone()
        mounted.Position = New PointF(point.X / 1365, point.Y / 766)
        mounted.Rotation = (wall.Rotation + If(side < 0, 180, 0)) Mod 360
        Return mounted
    End Function

    Friend Function MountDescription(asset As FactoryMapAsset) As String
        Dim height = MountHeight(asset).ToString("0.##") & " m"
        If FactoryMapMonitoring.IsCeiling(asset.Model) Then
            Dim followsRoom = asset.RoomId <> "" AndAlso _building.Any(Function(part) part.Kind = "Wall" AndAlso part.Level = asset.Level AndAlso part.RoomId = asset.RoomId)
            Return height & If(followsRoom, " / beneath room ceiling; follows wall height", " / configured ceiling height; verify on site")
        End If
        If FactoryMapMonitoring.IsWallMounted(asset.Model) Then
            Dim wall = MountWall(asset)
            Return height & If(wall Is Nothing, " / NO COMPATIBLE WALL - select a room with drawn walls",
                " / nearest drawn wall: " & wall.Name & "; face/height provisional, verify on site")
        End If
        Return height
    End Function

    Private Function LiveDoorOpen(id As String) As Boolean?
        Dim sample As MonitoringSample = Nothing
        Dim device = _monitoringDevices.Devices.FirstOrDefault(Function(item) item.Id = id AndAlso item.Kind = "Access")
        If device Is Nothing OrElse Not _deviceSamples.TryGetValue(id, sample) OrElse Not sample.IsCurrent Then Return Nothing
        Return sample.DoorOpen
    End Function

    Private Sub InitializeMonitoring()
        AddHandler _blinkTimer.Tick, Sub()
                                         _blinkOn = Not _blinkOn
                                         InvalidateAlarmOverlay()
                                     End Sub
    End Sub

    Private Sub UpdateBlinkTimer()
        Dim active = _monitorPageActive AndAlso Visible AndAlso Not IsDisposed AndAlso
            (_assets.Any(Function(asset) asset.Level = ActiveLevel AndAlso asset.IsPlaced AndAlso _visibleLayers.Contains(asset.Category) AndAlso
                Indication(asset).Signal >= MapSignal.Warning AndAlso Indication(asset).Signal <> MapSignal.Fire) OrElse
             _eventMatches.Any(Function(item) item.RoomId <> "" AndAlso item.Level = ActiveLevel AndAlso item.Evidence.Signal = MapSignal.Warning) OrElse
             _building.Any(Function(part) part.Level = ActiveLevel AndAlso _buildingSignals.ContainsKey(part.Id) AndAlso _buildingSignals(part.Id) <> MapSignal.Fire))
        _blinkTimer.Enabled = active
    End Sub

    Protected Overrides Sub OnVisibleChanged(e As EventArgs)
        MyBase.OnVisibleChanged(e)
        If _blinkTimer IsNot Nothing Then UpdateBlinkTimer()
    End Sub

    Private Sub InvalidateAlarmOverlay()
        _alarmOverlayInvalidation = True
        Try
            Invalidate()
        Finally
            _alarmOverlayInvalidation = False
        End Try
    End Sub

    Private Sub DrawAlarmOverlay(graphics As Graphics)
        Dim deviceBeacons As New List(Of (Center As PointF, Signal As MapSignal))
        For Each entry In _hitAreas
            Dim state = Indication(entry.Key)
            If state.Signal = MapSignal.Normal Then
                Dim normalBounds = entry.Value.GetBounds()
                Using brush As New SolidBrush(UiTheme.Green)
                    graphics.FillEllipse(brush, normalBounds.Right, normalBounds.Bottom - 5, 6, 6)
                End Using
            End If
            If state.Signal < MapSignal.Warning Then Continue For
            Dim bounds = entry.Value.GetBounds()
            bounds.Inflate(4, 4)
            Dim onPhase = state.Signal = MapSignal.Fire OrElse _blinkOn
            Using pen As New Pen(Color.FromArgb(If(onPhase, 240, 40), SignalColor(state.Signal)), If(onPhase, 3, 1.5F)),
                fill As New SolidBrush(Color.FromArgb(If(onPhase, 65, 8), SignalColor(state.Signal)))
                graphics.FillPath(fill, entry.Value)
                graphics.DrawEllipse(pen, bounds)
            End Using
            If entry.Key.Category = "Fire System" Then deviceBeacons.Add((
                New PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), state.Signal))
        Next
        For Each entry In _buildingHits
            Dim signal As MapSignal
            If Not _buildingSignals.TryGetValue(entry.Key.Id, signal) Then Continue For
            Dim onPhase = signal = MapSignal.Fire OrElse _blinkOn
            Using brush As New SolidBrush(Color.FromArgb(If(onPhase, 95, 25), SignalColor(signal))),
                pen As New Pen(Color.FromArgb(If(onPhase, 230, 100), SignalColor(signal)), If(onPhase, 2.5F, 1))
                graphics.FillPath(brush, entry.Value)
                graphics.DrawPath(pen, entry.Value)
            End Using
        Next
        For Each group In _eventMatches.Where(Function(item) item.RoomId <> "" AndAlso item.Level = ActiveLevel).GroupBy(Function(item) item.RoomId)
            Dim room = _building.FirstOrDefault(Function(part) part.RoomId = group.Key AndAlso part.Level = ActiveLevel AndAlso part.Kind = "Floor")
            If room Is Nothing Then room = _building.FirstOrDefault(Function(part) part.RoomId = group.Key AndAlso part.Level = ActiveLevel)
            If room Is Nothing Then Continue For
            Dim signal = group.Max(Function(item) item.Evidence.Signal)
            Dim addresses = String.Join(", ", group.Select(Function(item) item.Evidence.Address).Where(Function(address) address <> "").Distinct())
            If _projectedCorners.Length = 0 Then Continue For
            Dim offset = ProjectVector(room.X * 1365, room.Y * 766)
            Dim center As New PointF(_projectedCorners(0).X + offset.X * _fitScale * _zoom,
                _projectedCorners(0).Y + offset.Y * _fitScale * _zoom)
            If Not New RectangleF(-17, -17, ClientSize.Width + 34, ClientSize.Height + 34).Contains(center) Then Continue For
            DrawEventBeacon(graphics, center, signal,
                If(signal = MapSignal.Fire, "FIRE", "FAULT") & " " & addresses & " (area)")
        Next
        For Each beacon In deviceBeacons.OrderBy(Function(item) item.Signal)
            DrawEventBeacon(graphics, beacon.Center, beacon.Signal, "")
        Next
        Dim liveFire = _monitoringDevices.Devices.Any(
            Function(device)
                Dim sample As MonitoringSample = Nothing
                Return device.Kind = "Fire" AndAlso _deviceSamples.TryGetValue(device.Id, sample) AndAlso sample.IsCurrent
            End Function)
        Dim caption = If(liveFire, "Fire: verified device samples; ", "Fire: live condition UNKNOWN; ") &
            _eventMatches.Count & " latched events / " & _eventMatches.Where(Function(item) item.AssetId = "" AndAlso item.RoomId = "").Count() &
            " unmapped. See Fire alerts / local reset."
        Using font As New Font("Segoe UI", 8), ink As New SolidBrush(UiTheme.MutedInk), background As New SolidBrush(Color.FromArgb(238, UiTheme.Surface))
            Dim measured = graphics.MeasureString(caption, font)
            Dim unplaced = _assets.Any(Function(asset) asset.Level = ActiveLevel AndAlso Not asset.IsPlaced AndAlso _visibleLayers.Contains(asset.Category))
            Dim y = Math.Max(0, ClientSize.Height - measured.Height - 8 - If(unplaced, 24, 0))
            graphics.FillRectangle(background, 6, y, measured.Width + 10, measured.Height + 4)
            graphics.DrawString(caption, font, ink, 10, y + 2)
        End Using
    End Sub

    Private Sub DrawEventBeacon(graphics As Graphics, center As PointF, signal As MapSignal, text As String)
        Dim onPhase = signal = MapSignal.Fire OrElse _blinkOn
        Dim color = SignalColor(signal)
        Using halo As New SolidBrush(Color.FromArgb(If(onPhase, 85, 10), color)),
            fill As New SolidBrush(Color.FromArgb(If(onPhase, 255, 45), color)),
            outline As New Pen(Color.White, 2), font As New Font("Segoe UI", 9, FontStyle.Bold),
            ink As New SolidBrush(Color.White)
            graphics.FillEllipse(halo, center.X - 17, center.Y - 17, 34, 34)
            graphics.FillEllipse(fill, center.X - 10, center.Y - 10, 20, 20)
            graphics.DrawEllipse(outline, center.X - 10, center.Y - 10, 20, 20)
            graphics.DrawString("!", font, ink, center.X - 4, center.Y - 8)
            If text <> "" Then
                Dim size = graphics.MeasureString(text, font)
                Dim x = Math.Clamp(center.X + 13, 0, Math.Max(0, ClientSize.Width - size.Width - 6))
                Dim y = Math.Clamp(center.Y - 10, 0, Math.Max(0, ClientSize.Height - size.Height - 4))
                Using background As New SolidBrush(Color.FromArgb(235, UiTheme.ShellDeep))
                    graphics.FillRectangle(background, x, y, size.Width + 6, size.Height + 4)
                    graphics.DrawString(text, font, ink, x + 3, y + 2)
                End Using
            End If
        End Using
    End Sub
End Class
