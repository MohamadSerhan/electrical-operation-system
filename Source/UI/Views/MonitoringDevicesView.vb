Imports System.ComponentModel
Imports System.Drawing
Imports System.IO
Imports System.Linq
Imports System.Text.Json
Imports System.Windows.Forms

Public NotInheritable Class MonitoringDevicesView
    Inherits UserControl

    Private ReadOnly _session As UserSession
    Private ReadOnly _lists As New Dictionary(Of String, BindingList(Of MonitoringDevice))
    Private ReadOnly _grids As New List(Of DataGridView)
    Private ReadOnly _message As New Label With {.Dock = DockStyle.Fill, .AutoEllipsis = True}
    Private _loadFailed As Boolean

    Public Sub New(session As UserSession, panels As FirePanelManager)
        _session = session
        BackColor = UiTheme.Canvas
        Dim editable = session IsNot Nothing AndAlso PlcConfigurationAccess.CanEdit(session.RoleName, session.IsOfflinePreview)
        Dim registry As New MonitoringDeviceRegistry
        Try
            registry = MonitoringDeviceStore.Load()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is UnauthorizedAccessException
            _loadFailed = True
            _message.Text = "Registry cannot be loaded; saving disabled: " & ex.Message
            _message.ForeColor = UiTheme.Red
        End Try
        Dim root As New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 1, .RowCount = 4, .Padding = New Padding(8)}
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 90))
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 44))
        Dim panelNames = String.Join(" / ", panels.Panels.Select(Function(panel) panel.Id & " = " & panel.Name))
        root.Controls.Add(New Label With {.Dock = DockStyle.Fill, .Text =
            "READ-ONLY MONITORING REGISTRY - add/delete rows; Save applies all tabs. IDs stay stable when renamed." & Environment.NewLine &
            "Fire: drawing tags are NOT panel addresses. Leave Addressing verified unchecked until panel/site/loop/address are confirmed. Drafts never report live status or trigger rooms. Panel IDs: " & panelNames & Environment.NewLine &
            "Zeta currently supplies history, NOT verified live fire status. Cameras/access: store IP and external device ID; video, door contact and personnel feeds require a commissioned vendor adapter. Never store passwords or tokens here."}, 0, 0)
        Dim tabs As New TabControl With {.Dock = DockStyle.Fill}
        For Each kind In {"Fire", "Camera", "Access", "Equipment"}
            Dim selectedKind = kind
            Dim list As New BindingList(Of MonitoringDevice)(registry.Devices.Where(Function(device) device.Kind = selectedKind).ToList())
            _lists.Add(kind, list)
            AddHandler list.AddingNew, Sub(sender, e) e.NewObject = New MonitoringDevice With {.Kind = selectedKind, .Commissioned = selectedKind <> "Fire"}
            Dim grid As New DataGridView With {.Dock = DockStyle.Fill, .AutoGenerateColumns = False,
                .AllowUserToAddRows = editable, .AllowUserToDeleteRows = editable, .ReadOnly = Not editable,
                .DataSource = list, .AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                .BackgroundColor = UiTheme.Surface, .RowHeadersWidth = 28, .SelectionMode = DataGridViewSelectionMode.CellSelect}
            AddColumn(grid, "Name", "Name", 170)
            AddColumn(grid, "Model", "Type / model", 180)
            AddColumn(grid, "Location", "Location", 170)
            If kind = "Fire" Then
                AddColumn(grid, "DrawingTag", "Drawing tag (not an address)", 160)
                AddColumn(grid, "DrawingReference", "Drawing reference", 220)
                grid.Columns.Add(New DataGridViewCheckBoxColumn With {.DataPropertyName = "Commissioned",
                    .Name = "Commissioned", .HeaderText = "Addressing verified", .Width = 100})
                AddColumn(grid, "PanelId", "Panel ID", 80)
                AddColumn(grid, "SiteId", "Cloud site ID (exact)", 140)
                AddColumn(grid, "Loop", "Loop", 65)
                AddColumn(grid, "Address", "Device address", 100)
                AddColumn(grid, "EventAddress", "Full event address (exact)", 180)
                AddColumn(grid, "Zone", "Event zone (exact)", 120)
            Else
                AddColumn(grid, "IpAddress", "IP address (no credentials)", 170)
                AddColumn(grid, "ExternalId", If(kind = "Access", "Door/controller external ID", If(kind = "Camera", "Camera/NVR external ID", "SCADA equipment external ID")), 180)
            End If
            AddColumn(grid, "Notes", "Commissioning notes", 260)
            AddColumn(grid, "Id", "Stable device ID", 250, True)
            AddHandler grid.DataError, Sub(sender, e)
                                           e.ThrowException = False
                                           _message.Text = "Invalid device field: " & e.Exception.Message
                                           _message.ForeColor = UiTheme.Red
                                       End Sub
            _grids.Add(grid)
            Dim page As New TabPage(If(kind = "Fire", "Fire devices", If(kind = "Camera", "Cameras", If(kind = "Access", "Doors & gates", "Other equipment"))))
            page.Controls.Add(grid)
            tabs.TabPages.Add(page)
        Next
        root.Controls.Add(tabs, 0, 1)
        root.Controls.Add(_message, 0, 2)
        Dim actions As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .WrapContents = False}
        Dim save = UiTheme.ActionButton("Save device registry", True)
        save.Width = 180
        save.Enabled = editable AndAlso Not _loadFailed
        AddHandler save.Click, AddressOf SaveRegistry
        actions.Controls.Add(save)
        actions.Controls.Add(New Label With {.AutoSize = True, .Text = "Map > Edit item: select Registered monitoring device. Building > Rooms: select temperature source."})
        root.Controls.Add(actions, 0, 3)
        Controls.Add(root)
    End Sub

    Private Shared Sub AddColumn(grid As DataGridView, name As String, caption As String, width As Integer, Optional readOnlyColumn As Boolean = False)
        grid.Columns.Add(New DataGridViewTextBoxColumn With {.DataPropertyName = name, .Name = name, .HeaderText = caption, .Width = width, .ReadOnly = readOnlyColumn, .MaxInputLength = 500})
    End Sub

    Private Sub SaveRegistry(sender As Object, e As EventArgs)
        If _loadFailed Then Return
        Try
            For Each grid In _grids
                If Not grid.EndEdit() Then Throw New InvalidDataException("Finish editing the invalid device field before saving.")
                DirectCast(BindingContext(grid.DataSource), CurrencyManager).EndCurrentEdit()
            Next
            Dim document As New MonitoringDeviceRegistry With {.Devices = _lists.Values.SelectMany(Function(items) items).Select(Function(item) item.Clone()).ToList()}
            MonitoringDeviceStore.Save(document, _session)
            _message.Text = "Device registry saved. Return to Factory Map to use the registered devices. No hardware commands were sent."
            _message.ForeColor = UiTheme.Green
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is UnauthorizedAccessException
            _message.Text = ex.Message
            _message.ForeColor = UiTheme.Red
        End Try
    End Sub
End Class
