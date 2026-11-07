Imports System.Globalization
Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Data.Sqlite

Public NotInheritable Class FireEventsView
    Inherits UserControl

    Private ReadOnly _manager As FirePanelManager
    Private ReadOnly _session As UserSession
    Private ReadOnly _panels As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 210, .DisplayMember = NameOf(FirePanel.Name)}
    Private ReadOnly _search As New TextBox With {.Width = 200, .PlaceholderText = "Search stored fire events"}
    Private ReadOnly _filter As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 130}
    Private ReadOnly _grid As New DataGridView With {.Dock = DockStyle.Fill}
    Private ReadOnly _info As New Label With {.AutoSize = True}
    Private ReadOnly _message As New Label With {.AutoSize = True, .Dock = DockStyle.Bottom}
    Private ReadOnly _timer As New System.Windows.Forms.Timer With {.Interval = 2000}
    Private ReadOnly _sync As Button = UiTheme.ActionButton("Sync Cloud Events", False)
    Private ReadOnly _cancel As Button = UiTheme.ActionButton("Cancel", False)
    Private _events As IReadOnlyList(Of ZetaFireEvent) = Array.Empty(Of ZetaFireEvent)()
    Private _locations As IReadOnlyDictionary(Of String, FireEventLocation) = New Dictionary(Of String, FireEventLocation)
    Private _loading As Boolean
    Private _active As Boolean
    Private _refill As Boolean
    Private _revision As Integer
    Private _loadedRevision As Integer = -1
    Private _loadedSync As DateTime?
    Private _cancellation As CancellationTokenSource

    Public Sub New(manager As FirePanelManager, session As UserSession)
        _manager = manager
        _session = session
        BackColor = UiTheme.Canvas
        Font = New Font("Segoe UI", 9)
        Dim toolbar As New FlowLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .WrapContents = True, .Padding = New Padding(4)}
        _filter.Items.AddRange({"All event types", "Fire", "Fault", "System", "Service"})
        _filter.SelectedIndex = 0
        Dim refresh = MakeButton("Refresh Events", "refresh-events", Async Sub() Await RefreshEventsAsync())
        Dim details = MakeButton("Event Details", "event-details", AddressOf ShowDetails)
        Dim export = MakeButton("Export CSV", "export-events", Async Sub() Await ExportAsync())
        _sync.Name = "sync-events"
        _sync.AutoSize = True
        _cancel.Name = "cancel-sync"
        _cancel.AutoSize = True
        _cancel.Enabled = False
        toolbar.Controls.AddRange({_panels, _search, _filter, refresh, _sync, _cancel, details, export, _info})
        Dim notice As New Label With {.Dock = DockStyle.Top, .AutoSize = True, .Padding = New Padding(6),
            .Text = "Fire Events - historical records only. Current fire panel condition: UNKNOWN. Latest 200 stored events for the selected panel/site; no live alarm state is inferred."}
        TransformerDetailForm.ConfigureGrid(_grid, {"Event Timestamp (UTC)", "Event Type", "Zone", "Device Address", "Area / Location", "Described Panel", "Description", "Event Message", "Source", "Event ID"})
        _grid.ReadOnly = True
        _grid.MultiSelect = False
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None
        For Each column As DataGridViewColumn In _grid.Columns
            column.Width = If(column.Index = 7, 270, If(column.Index = 4 OrElse column.Index = 5, 210, If(column.Index = 0, 180, 150)))
            column.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft
        Next
        _grid.Columns(0).Frozen = True
        Controls.Add(_grid)
        Controls.Add(toolbar)
        Controls.Add(notice)
        Controls.Add(_message)
        AddHandler _search.TextChanged, Sub() FilterEvents()
        AddHandler _filter.SelectedIndexChanged, Sub() FilterEvents()
        AddHandler _grid.CellDoubleClick, Sub() ShowDetails()
        AddHandler _panels.SelectedIndexChanged, Async Sub()
                                                    If _refill Then Return
                                                    _revision += 1
                                                    _events = Array.Empty(Of ZetaFireEvent)()
                                                    _locations = New Dictionary(Of String, FireEventLocation)
                                                    FilterEvents()
                                                    ApplyPermissions()
                                                    If _active Then Await RefreshEventsAsync()
                                                End Sub
        AddHandler _sync.Click, Async Sub() Await SynchronizeAsync()
        AddHandler _cancel.Click, Sub() _cancellation?.Cancel()
        AddHandler _timer.Tick, Async Sub()
                                   Dim panel = TryCast(_panels.SelectedItem, FirePanel)
                                   If panel Is Nothing Then Return
                                   Dim savedSite = panel.Service.ReadSettings().CloudSiteIdentifier
                                   Dim sync = EventSynchronization(panel)
                                   If _loadedRevision <> _revision OrElse Not Nullable.Equals(sync, _loadedSync) OrElse
                                       CStr(_panels.Tag) <> savedSite Then Await RefreshEventsAsync()
                                   ApplyPermissions()
                               End Sub
        If _manager IsNot Nothing Then AddHandler _manager.Changed, AddressOf PanelsChanged
        RefillPanels()
    End Sub

    Private Shared Function MakeButton(text As String, name As String, handler As Action) As Button
        Dim result = UiTheme.ActionButton(text, False)
        result.Name = name
        result.AutoSize = True
        AddHandler result.Click, Sub() handler()
        Return result
    End Function

    Private Sub RefillPanels()
        Dim previous = TryCast(_panels.SelectedItem, FirePanel)?.Id
        _refill = True
        _panels.Items.Clear()
        If _manager IsNot Nothing Then
            For Each panel In _manager.Panels
                _panels.Items.Add(panel)
                If panel.Id = previous Then _panels.SelectedItem = panel
            Next
        End If
        If _panels.SelectedIndex < 0 AndAlso _panels.Items.Count > 0 Then _panels.SelectedIndex = 0
        _refill = False
        _revision += 1
        _events = Array.Empty(Of ZetaFireEvent)()
        _locations = New Dictionary(Of String, FireEventLocation)
        FilterEvents()
        ApplyPermissions()
        If _panels.Items.Count = 0 Then _info.Text = "No fire panel service is available."
    End Sub

    Private Async Sub PanelsChanged(sender As Object, e As EventArgs)
        If IsDisposed Then Return
        If InvokeRequired Then
            BeginInvoke(Sub() PanelsChanged(sender, e))
            Return
        End If
        RefillPanels()
        If _active Then Await RefreshEventsAsync()
    End Sub

    Private Sub ApplyPermissions()
        Dim panel = TryCast(_panels.SelectedItem, FirePanel)
        _sync.Enabled = _cancellation Is Nothing AndAlso panel IsNot Nothing AndAlso
            panel.Service.ReadSettings().ConnectionType = FireConnectionType.ZetaCloud AndAlso
            _session IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(_session.UserId) AndAlso
            Not String.IsNullOrWhiteSpace(_session.Username) AndAlso PlcConfigurationAccess.CanEdit(_session.RoleName, _session.IsOfflinePreview)
        _panels.Enabled = _cancellation Is Nothing
        _cancel.Enabled = _cancellation IsNot Nothing
    End Sub

    Public Async Function RefreshEventsAsync() As Task
        If _loading Then Return
        Dim panel = TryCast(_panels.SelectedItem, FirePanel)
        If panel Is Nothing Then Return
        _loading = True
        Dim revision = _revision
        Dim site = panel.Service.ReadSettings().CloudSiteIdentifier
        Dim sync = EventSynchronization(panel)
        Try
            Dim data = Await Task.Run(Function()
                                          Dim history = panel.Service.ReadCloudEvents(site, 10000)
                                          Dim locations = FireEventLocation.ResolveRelated(history.Select(Function(record) New FireMapEventEvidence(record)).ToArray())
                                          Return (Events:=history.Take(200).ToArray(), Locations:=locations, Count:=panel.Service.CloudEventCount(site))
                                      End Function)
            If IsDisposed OrElse revision <> _revision OrElse panel.Service.ReadSettings().CloudSiteIdentifier <> site Then Return
            _events = data.Events
            _locations = data.Locations
            _loadedRevision = revision
            _loadedSync = sync
            _panels.Tag = site
            _info.Text = "Site: " & If(site.Length = 0, "not configured", site) & " | Stored: " & data.Count.ToString(CultureInfo.InvariantCulture) &
                " | Last synchronization: " & If(sync.HasValue, sync.Value.ToString("u"), "not available")
            _message.Text = String.Empty
            FilterEvents()
        Catch ex As Exception When TypeOf ex Is SqliteException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is CryptographicException OrElse
            TypeOf ex Is JsonException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is FormatException
            If Not IsDisposed AndAlso revision = _revision Then
                _message.Text = "Cannot read protected fire history; check the original Windows account and file permissions. No history was removed."
                _message.ForeColor = Color.Firebrick
            End If
        Finally
            _loading = False
        End Try
    End Function

    Private Shared Function EventSynchronization(panel As FirePanel) As DateTime?
        Dim status = panel.Service.ReadStatus()
        Return If(status.ConnectionType = FireConnectionType.ZetaCloud, status.LastDataUtc, Nothing)
    End Function

    Private Async Function SynchronizeAsync() As Task
        If _cancellation IsNot Nothing Then Return
        Dim panel = TryCast(_panels.SelectedItem, FirePanel)
        If panel Is Nothing Then Return
        _cancellation = New CancellationTokenSource
        ApplyPermissions()
        Try
            Dim result = Await panel.Service.TestCloudAsync(panel.Service.ReadSettings(), False, True, _session, _cancellation.Token)
            Await RefreshEventsAsync()
            If Not IsDisposed Then
                _message.Text = If(result.Success, result.Events.Count.ToString(CultureInfo.InvariantCulture) & " validated event(s) synchronized. Live panel: UNKNOWN.", result.ErrorMessage)
                _message.ForeColor = If(result.Success, UiTheme.Ink, Color.Firebrick)
            End If
        Catch ex As OperationCanceledException
            If Not IsDisposed Then _message.Text = "Cloud synchronization cancelled."
        Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
            TypeOf ex Is CryptographicException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is FormatException
            If Not IsDisposed Then
                _message.Text = "Cloud synchronization failed: " & If(TypeOf ex Is CryptographicException, "protected credentials are unavailable.", ex.Message)
                _message.ForeColor = Color.Firebrick
            End If
        Finally
            _cancellation.Dispose()
            _cancellation = Nothing
            If Not IsDisposed Then ApplyPermissions()
        End Try
    End Function

    Private Function FilteredEvents() As IEnumerable(Of ZetaFireEvent)
        Dim text = _search.Text
        Dim filter = CStr(_filter.SelectedItem)
        Return _events.Where(Function(item) (filter = "All event types" OrElse String.Equals(item.EventType, filter, StringComparison.OrdinalIgnoreCase)) AndAlso
            (text.Length = 0 OrElse String.Join(" ", {item.EventId, item.EventType, item.Zone, item.DeviceAddress, item.Description, item.Message,
                LocationFor(item).Display, LocationFor(item).DescribedPanel, LocationFor(item).DeviceType}).
                Contains(text, StringComparison.OrdinalIgnoreCase)))
    End Function

    Private Function LocationFor(record As ZetaFireEvent) As FireEventLocation
        Dim result As FireEventLocation = Nothing
        Return If(_locations.TryGetValue(FireEventIdentity.Key(record), result), result, FireEventLocation.Parse(record.Description, record.Message))
    End Function

    Private Sub FilterEvents()
        _grid.Rows.Clear()
        For Each record In FilteredEvents()
            Dim identity = FireEventIdentity.Parse(record)
            Dim location = LocationFor(record)
            Dim row = _grid.Rows.Add(If(record.TimestampUtc.HasValue, record.TimestampUtc.Value.ToString("u"), ""), record.EventType, identity.Zone,
                identity.Address, location.Display, location.DescribedPanel, record.Description, record.Message, record.Source, record.EventId)
            _grid.Rows(row).Tag = record
            _grid.Rows(row).Cells(3).ToolTipText = "Address source: " & identity.AddressSource &
                If(identity.Conflict, " - CONFLICT: automatic map matching blocked", "") & "; raw address: " & record.DeviceAddress
            _grid.Rows(row).Cells(4).ToolTipText = location.Source & "; " & If(location.Conflict, "CONFLICT - no inferred map location", "inferred, not commissioned") &
                "; device type: " & location.DeviceType
            _grid.Rows(row).Cells(5).ToolTipText = "Electrical/location label from the description; not the configured source fire panel ID."
        Next
    End Sub

    Private Sub ShowDetails()
        Dim record = TryCast(_grid.CurrentRow?.Tag, ZetaFireEvent)
        If record Is Nothing Then
            _message.Text = "Select a stored historical event first."
            Return
        End If
        Dim identity = FireEventIdentity.Parse(record)
        Dim location = LocationFor(record)
        MessageBox.Show(Me, JsonSerializer.Serialize(record, New JsonSerializerOptions With {.WriteIndented = True}) &
            vbCrLf & "Resolved address / zone / loop: " & identity.Address & " / " & identity.Zone & " / " & identity.Loop &
            vbCrLf & "Address source: " & identity.AddressSource & "; conflicting identity: " & identity.Conflict.ToString() &
            vbCrLf & "Area / location: " & location.Display & "; described panel: " & location.DescribedPanel &
            vbCrLf & "Location source: " & location.Source & "; described device type: " & location.DeviceType &
            vbCrLf & "Described electrical panels are location labels, not source fire-panel identities. Room-only evidence cannot identify a unique sensor." &
            vbCrLf & "Historical event only; current fire panel status remains UNKNOWN.", "Fire Event", MessageBoxButtons.OK, MessageBoxIcon.Information)
    End Sub

    Private Async Function ExportAsync() As Task
        Using dialog As New SaveFileDialog With {.Filter = "CSV files|*.csv", .FileName = "Fire-events.csv", .OverwritePrompt = True}
            If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
            Dim rows = FilteredEvents().ToArray()
            Try
                If _manager IsNot Nothing Then
                    For Each panel In _manager.Panels
                        panel.Service.ValidateExportPath(dialog.FileName)
                    Next
                End If
                Await File.WriteAllTextAsync(dialog.FileName, ExportText(rows), New UTF8Encoding(True))
                If Not IsDisposed Then _message.Text = "Exported displayed historical events; credentials excluded."
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is InvalidOperationException
                If Not IsDisposed Then _message.Text = "Could not export fire events: " & ex.Message
            End Try
        End Using
    End Function

    Private Function ExportText(rows As IEnumerable(Of ZetaFireEvent)) As String
        Dim text As New StringBuilder("Event Timestamp UTC,Event Type,Zone,Device Address,Area / Location,Described Panel,Description,Event Message,Source,Event ID,Address source,Raw zone,Raw device address,Identity conflict,Location source,Location conflict,Described device type" & vbCrLf)
        For Each record In rows
            Dim identity = FireEventIdentity.Parse(record)
            Dim location = LocationFor(record)
            text.AppendLine(String.Join(",", {If(record.TimestampUtc.HasValue, record.TimestampUtc.Value.ToString("O"), ""), record.EventType,
                identity.Zone, identity.Address, location.Display, location.DescribedPanel, record.Description, record.Message, record.Source, record.EventId,
                identity.AddressSource, record.Zone, record.DeviceAddress, identity.Conflict.ToString(), location.Source, location.Conflict.ToString(), location.DeviceType}.Select(AddressOf Csv)))
        Next
        Return text.ToString()
    End Function

    Private Shared Function Csv(value As String) As String
        If value.Length > 0 AndAlso "=+-@".Contains(value.TrimStart().FirstOrDefault()) Then value = "'" & value
        Return """" & value.Replace("""", """""") & """"
    End Function

    Public Sub SetPageActive(active As Boolean)
        If _active = active Then Return
        _active = active
        _timer.Enabled = active
        If active Then Call RefreshEventsAsync()
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            _timer.Dispose()
            _cancellation?.Cancel()
            If _manager IsNot Nothing Then RemoveHandler _manager.Changed, AddressOf PanelsChanged
        End If
        MyBase.Dispose(disposing)
    End Sub
End Class
