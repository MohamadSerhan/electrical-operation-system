Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text.Json
Imports System.Windows.Forms

Friend NotInheritable Class FactoryMapAsset
    Public Property Id As String
    Public Property Name As String
    Public Property Category As String
    Public Property Location As String
    Public Property Source As String
    Public Property Position As PointF
    Public Property IsPlaced As Boolean = True
    Public Property EquipmentId As String = String.Empty
    Public Property MeterSourceId As String = String.Empty
    Public Property Model As String = String.Empty
    Public Property ConveyorLength As Single = 60.0F
    Public Property Rotation As Single
    Public Property MirrorHorizontal As Boolean
    Public Property MirrorVertical As Boolean
    Public Property ScaleX As Single = 1.0F
    Public Property ScaleY As Single = 1.0F
    Public Property ScaleZ As Single = 1.0F
    Public Property BodyColor As String = String.Empty
    Public Property IsOpen As Boolean
    Public Property OpeningDirection As String = "Left inward"
    Public Property MonitoringDeviceId As String = String.Empty
    Public Property ParentUpsId As String = String.Empty
    Public Property CameraIpAddress As String = String.Empty
    Public Property RoomId As String = String.Empty
    Public Property BaseHeightMeters As Single
    Public Property Level As String = "Ground"
    Public Property CustomEquipment As FactoryMapCustomEquipment
    Public Property ConnectorKind As String = String.Empty
    Public Property ConnectorFromId As String = String.Empty
    Public Property ConnectorToId As String = String.Empty
    Public Property ConnectorFromPoint As PointF
    Public Property ConnectorToPoint As PointF
    Public Property ConnectorThicknessMeters As Single = 0.12F
    Public Property ConnectorHeightMeters As Single = 0.75F
    Public Property ConnectorStyle As String = String.Empty
    Public Property ConnectorCount As Integer = 1
    Public Property ConnectorDepthMeters As Single = 0.06F
    Friend Property ConnectorSignature As String = String.Empty

    Public Function Clone() As FactoryMapAsset
        Dim result = DirectCast(MemberwiseClone(), FactoryMapAsset)
        result.CustomEquipment = CustomEquipment?.Clone()
        Return result
    End Function

    Public Overrides Function ToString() As String
        Return Name & " - " & If(CustomEquipment Is Nothing, Model, CustomEquipment.Name) & If(IsPlaced, "", " (unplaced)")
    End Function
End Class

Partial Public NotInheritable Class FactoryMapView
    Inherits UserControl

    Private Shared ReadOnly MapCategories As String() = {
        "Generators", "Transformers", "UPS", "Battery Rooms", "Compressors", "Chillers",
        "Production Lines", "Water Systems", "Filler Room", "HVAC", "Electrical Panels", "Motors & Drives", "Pumps & Tanks", "Sensors & Controls",
        "Utilities", "CCTV", "Access Control", "Fire System"
    }

    Private ReadOnly _assets As List(Of FactoryMapAsset)
    Private ReadOnly _canvas As FactoryMapCanvas
    Private ReadOnly _editButton As Button
    Private _saveButton As Button
    Private _cancelButton As Button
    Private _layoutStatus As Label
    Private _factoryLoadValue As Label
    Private _mappedValue As Label
    Private _placedValue As Label
    Private _connectedValue As Label
    Private ReadOnly _layerMenu As New ContextMenuStrip()
    Private ReadOnly _toolbarTips As New ToolTip()
    Private _selectedTitle As Label
    Private _selectedStatus As Label
    Private _selectedDetails As Label
    Private _openTransformers As Button
    Private ReadOnly _openTransformersAction As Action
    Private _savedAssets As List(Of FactoryMapAsset)
    Private _editTools As FlowLayoutPanel
    Private _editRow As RowStyle
    Private ReadOnly _mapPanel As TableLayoutPanel
    Private _assetPicker As ComboBox
    Private ReadOnly _loadError As String
    Private ReadOnly _canEditMap As Boolean
    Private _snapshots As IReadOnlyList(Of MeterSnapshot) = Array.Empty(Of MeterSnapshot)()
    Private _upsSnapshots As IReadOnlyDictionary(Of String, UpsSnapshot) = New Dictionary(Of String, UpsSnapshot)()
    Private _editing As Boolean
    Private _buildingEditing As Boolean
    Private _buildingButton As Button
    Private _buildingTools As FlowLayoutPanel
    Private _buildingPicker As ComboBox
    Private _building As List(Of FactoryBuildingPart)
    Private _savedBuilding As List(Of FactoryBuildingPart)
    Private _buildingError As String = String.Empty
    Private _copiedAsset As FactoryMapAsset
    Private _copiedBuilding As List(Of FactoryBuildingPart)
    Private _pasteCount As Integer
    Private _pageActive As Boolean

    Public Sub New(session As UserSession, snapshots As IReadOnlyList(Of MeterSnapshot), openTransformers As Action)
        If session Is Nothing Then Throw New ArgumentNullException(NameOf(session))
        SuspendLayout()
        _openTransformersAction = openTransformers
        _canEditMap = PlcConfigurationAccess.CanEdit(session.RoleName, session.IsOfflinePreview)
        _monitoringSession = session
        _assets = CreateAssets()
        Try
            Dim loaded = FactoryMapLayoutStore.LoadAssets(_assets)
            _assets.Clear()
            _assets.AddRange(loaded)
        Catch ex As IOException
            _loadError = ex.Message
        Catch ex As JsonException
            _loadError = ex.Message
        Catch ex As InvalidDataException
            _loadError = ex.Message
        Catch ex As UnauthorizedAccessException
            _loadError = ex.Message
        End Try

        Dock = DockStyle.Fill
        BackColor = UiTheme.Canvas
        _snapshots = If(snapshots, Array.Empty(Of MeterSnapshot)())

        Dim root As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 2,
            .Padding = New Padding(8, 6, 8, 6),
            .BackColor = UiTheme.Canvas
        }
        root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 78.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        Controls.Add(root)
        root.Controls.Add(CreateKpiPanel(), 0, 0)

        Dim workspace As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 2,
            .RowCount = 1,
            .BackColor = UiTheme.Canvas,
            .Padding = New Padding(0, 6, 0, 0)
        }
        workspace.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 76.0F))
        workspace.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 24.0F))
        workspace.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        root.Controls.Add(workspace, 0, 1)

        Dim mapPanel As New TableLayoutPanel With {
            .Dock = DockStyle.Fill, .BackColor = UiTheme.Surface, .Padding = New Padding(8),
            .ColumnCount = 1, .RowCount = 5
        }
        _mapPanel = mapPanel
        mapPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        mapPanel.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        mapPanel.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        mapPanel.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        _editRow = New RowStyle(SizeType.Absolute, 0)
        mapPanel.RowStyles.Add(_editRow)
        mapPanel.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
        workspace.Controls.Add(mapPanel, 0, 0)
        Dim toolbar As New FlowLayoutPanel With {
            .Dock = DockStyle.Fill,
            .AutoSize = False, .Height = 54, .AutoScroll = True,
            .WrapContents = False,
            .BackColor = UiTheme.Surface,
            .Padding = New Padding(0, 0, 0, 4), .Margin = Padding.Empty
        }
        Dim toolbarRow As New TableLayoutPanel With {
            .Dock = DockStyle.Fill, .Height = 54, .ColumnCount = 3, .RowCount = 1,
            .Margin = Padding.Empty, .Padding = Padding.Empty
        }
        toolbarRow.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        toolbarRow.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 140))
        toolbarRow.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 36))
        toolbarRow.RowStyles.Add(New RowStyle(SizeType.Absolute, 54))
        toolbarRow.Controls.Add(toolbar, 0, 0)
        mapPanel.Controls.Add(toolbarRow, 0, 0)
        AddToolbarButton(toolbar, "Top View", AddressOf ShowTopView)
        AddToolbarButton(toolbar, "2D Plan", Sub() _canvas.SetPlanView())
        AddToolbarButton(toolbar, "3D View", AddressOf ShowIsometricView)
        AddToolbarButton(toolbar, "Fit Factory", AddressOf FitFactory)
        AddToolbarButton(toolbar, "+", Sub() _canvas.ZoomBy(1.2F))
        AddToolbarButton(toolbar, "-", Sub() _canvas.ZoomBy(1.0F / 1.2F))
        AddToolbarButton(toolbar, "Reset Camera", AddressOf ResetView)
        AddToolbarButton(toolbar, "Focus", Sub() _canvas.FocusSelection())
        Dim displayTools As New FlowLayoutPanel With {
            .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .WrapContents = False, .FlowDirection = FlowDirection.TopDown, .BackColor = UiTheme.SurfaceMuted, .Padding = New Padding(5, 4, 5, 4),
            .Margin = New Padding(0, 0, 0, 6)
        }
        Dim levelPicker As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 160}
        levelPicker.Items.AddRange(FactoryMapBuildingStore.Levels)
        levelPicker.SelectedIndex = 0
        AddHandler levelPicker.SelectedIndexChanged,
            Sub()
                If _canvas Is Nothing Then Return
                _canvas.SetLevel(CStr(levelPicker.SelectedItem))
                If _assetPicker IsNot Nothing Then RefreshAssetPicker(Nothing)
                If _buildingPicker IsNot Nothing Then RefreshBuildingPicker(Nothing)
            End Sub
        displayTools.Controls.Add(New Label With {.Text = "Building level", .AutoSize = True})
        displayTools.Controls.Add(levelPicker)
        Dim floorToggle As New CheckBox With {
            .Text = "Site ground", .AutoSize = True, .Checked = True, .Margin = New Padding(4, 9, 8, 0), .ForeColor = UiTheme.Ink
        }
        AddHandler floorToggle.CheckedChanged, Sub(sender, e) _canvas.SetLayerVisible("Factory Plan / Rooms / Walls", floorToggle.Checked)
        displayTools.Controls.Add(floorToggle)
        Dim wallsToggle As New CheckBox With {
            .Text = "3D walls", .AutoSize = True, .Checked = True, .Margin = New Padding(4, 9, 8, 0), .ForeColor = UiTheme.Ink
        }
        AddHandler wallsToggle.CheckedChanged, Sub(sender, e) _canvas.ShowWalls = wallsToggle.Checked
        displayTools.Controls.Add(wallsToggle)
        Dim floorsToggle As New CheckBox With {.Text = "Room floors", .AutoSize = True, .Checked = True, .Margin = New Padding(4, 9, 8, 0)}
        AddHandler floorsToggle.CheckedChanged, Sub(sender, e) _canvas.ShowFloors = floorsToggle.Checked
        displayTools.Controls.Add(floorsToggle)
        Dim openingsToggle As New CheckBox With {.Text = "Openings / stairs", .AutoSize = True, .Checked = True, .Margin = New Padding(4, 9, 8, 0)}
        AddHandler openingsToggle.CheckedChanged, Sub(sender, e) _canvas.ShowOpenings = openingsToggle.Checked
        displayTools.Controls.Add(openingsToggle)
        Dim opacityLabel = UiTheme.Label("Wall opacity", New Font("Segoe UI", 8.5F), UiTheme.MutedInk)
        opacityLabel.Margin = New Padding(10, 9, 0, 0)
        displayTools.Controls.Add(opacityLabel)
        Dim opacity As New TrackBar With {
            .Minimum = 5, .Maximum = 60, .Value = 22, .TickStyle = TickStyle.None, .AutoSize = False,
            .Width = 100, .Height = 28, .SmallChange = 5, .Margin = New Padding(0, 4, 8, 0)
        }
        AddHandler opacity.ValueChanged, Sub(sender, e) _canvas.WallOpacity = opacity.Value
        AddHandler wallsToggle.CheckedChanged, Sub(sender, e) opacity.Enabled = wallsToggle.Checked
        displayTools.Controls.Add(opacity)
        Dim layersButton = UiTheme.ActionButton("Layers")
        layersButton.Width = 82
        layersButton.Height = 30
        layersButton.Margin = New Padding(2)
        AddHandler layersButton.Click, Sub(sender, e) _layerMenu.Show(layersButton, New Point(0, layersButton.Height))
        _toolbarTips.SetToolTip(layersButton, "Layers, wall opacity, dimensions and site size")
        _layerMenu.Items.Add(New ToolStripControlHost(displayTools))
        Dim roadsToggle As New ToolStripMenuItem("Roads") With {.Checked = True, .CheckOnClick = True}
        AddHandler roadsToggle.CheckedChanged, Sub(sender, e) _canvas.SetLayerVisible("Roads", roadsToggle.Checked)
        _layerMenu.Items.Add(roadsToggle)
        Dim snapToggle As New ToolStripMenuItem("Snap nearby edges (Alt bypass)") With {.Checked = True, .CheckOnClick = True}
        AddHandler snapToggle.CheckedChanged, Sub(sender, e) _canvas.SnapEnabled = snapToggle.Checked
        _layerMenu.Items.Add(snapToggle)

        _editButton = UiTheme.ActionButton("Edit Map", True)
        _editButton.Width = 78
        _editButton.Height = 30
        _editButton.Margin = New Padding(10, 2, 4, 2)
        _editButton.Visible = _canEditMap
        AddHandler _editButton.Click, AddressOf ToggleEditMode
        toolbar.Controls.Add(_editButton)
        _buildingButton = UiTheme.ActionButton("Building", False)
        _buildingButton.Width = 72
        _buildingButton.Height = 30
        _buildingButton.Margin = New Padding(2)
        _buildingButton.Visible = _canEditMap
        AddHandler _buildingButton.Click, AddressOf BeginBuildingEdit
        toolbar.Controls.Add(_buildingButton)
        toolbar.Controls.Add(layersButton)
        AddToolbarButton(toolbar, "Undo", Sub() HandleEditCommand("Undo"))
        _undoButton = DirectCast(toolbar.Controls(toolbar.Controls.Count - 1), Button)
        AddToolbarButton(toolbar, "Redo", Sub() HandleEditCommand("Redo"))
        _redoButton = DirectCast(toolbar.Controls(toolbar.Controls.Count - 1), Button)
        toolbar.Controls.SetChildIndex(_undoButton, 2)
        toolbar.Controls.SetChildIndex(_redoButton, 3)
        _toolbarTips.SetToolTip(_undoButton, "Undo the latest edit (Ctrl+Z)")
        _toolbarTips.SetToolTip(_redoButton, "Redo (Ctrl+Y / Ctrl+Shift+Z)")
        UpdateHistoryButtons()
        Dim visibleLevel As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 130, .AccessibleName = "Visible floor / level"}
        visibleLevel.Items.AddRange(FactoryMapBuildingStore.Levels)
        visibleLevel.SelectedIndex = 0
        AddHandler visibleLevel.SelectedIndexChanged, Sub() levelPicker.SelectedItem = visibleLevel.SelectedItem
        AddHandler levelPicker.SelectedIndexChanged, Sub() visibleLevel.SelectedItem = levelPicker.SelectedItem
        toolbarRow.Controls.Add(visibleLevel, 1, 0)
        Dim dimensionsAction As New ToolStripMenuItem("Dimensions...")
        AddHandler dimensionsAction.Click, Sub() EditDimensions(Me, EventArgs.Empty)
        _layerMenu.Items.Add(dimensionsAction)
        Dim planAction As New ToolStripMenuItem("Exact plan view (2D)")
        AddHandler planAction.Click, Sub() _canvas.SetPlanView()
        _layerMenu.Items.Add(planAction)
        If _canEditMap Then
            Dim siteAction As New ToolStripMenuItem("Site size...")
            AddHandler siteAction.Click, Sub() EditSite()
            _layerMenu.Items.Add(siteAction)
        End If
        _layerMenu.Items.Add(New ToolStripSeparator())
        AddToolbarButton(toolbar, "Full screen", AddressOf ShowFullScreen)
        Dim fullscreenButton = toolbar.Controls(toolbar.Controls.Count - 1)
        toolbar.Controls.Remove(fullscreenButton)
        toolbarRow.Controls.Add(fullscreenButton, 2, 0)
        _toolbarTips.SetToolTip(fullscreenButton, "Full screen (Esc to return)")

        For Each category In MapCategories
            Dim toggle As New ToolStripMenuItem(category) With {.Checked = True, .CheckOnClick = True}
            AddHandler toggle.CheckedChanged, Sub(sender, e) _canvas.SetLayerVisible(category, toggle.Checked)
            _layerMenu.Items.Add(toggle)
        Next
        _layoutStatus = UiTheme.Label(String.Empty, New Font("Segoe UI", 8.0F), UiTheme.Amber, False)
        _layoutStatus.Dock = DockStyle.Bottom
        _layoutStatus.Height = 22
        _layoutStatus.AutoEllipsis = True
        _layoutStatus.TextAlign = ContentAlignment.MiddleLeft
        If Not String.IsNullOrWhiteSpace(_loadError) Then
            _layoutStatus.Text = "Saved map layout could not be loaded; editing is disabled. " & _loadError
            _editButton.Enabled = False
        Else
            _layoutStatus.Text = "Wheel: smooth zoom | Drag / WASD: pan | Right-drag / Q,E: orbit | F: focus | Home: fit | Full screen: Esc returns"
            _layoutStatus.ForeColor = UiTheme.MutedInk
        End If
        mapPanel.Controls.Add(_layoutStatus, 0, 4)

        _canvas = New FactoryMapCanvas(_assets)
        _canvas.Dock = DockStyle.Fill
        _canvas.EditMode = False
        _canvas.SetSnapshots(_snapshots)
        AddHandler _canvas.AssetSelected, AddressOf OnAssetSelected
        AddHandler _canvas.AssetMoved, AddressOf OnAssetMoved
        AddHandler _canvas.ConnectorDrawn, AddressOf OnConnectorDrawn
        AddHandler _canvas.EditCommand, AddressOf HandleEditCommand
        AddHandler _canvas.EditCommitted, AddressOf RecordEditHistory
        _building = FactoryMapBuildingStore.Defaults()
        Try
            _building = FactoryMapBuildingStore.Load()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
            _buildingError = ex.Message
            _buildingButton.Enabled = False
            _layoutStatus.Text = "Building layout could not be loaded. Building editing is disabled; provisional traces are displayed. " & ex.Message
            _layoutStatus.ForeColor = UiTheme.Red
        End Try
        _canvas.SetBuildingParts(_building)
        AddHandler _canvas.BuildingSelected, AddressOf OnBuildingSelected
        AddHandler _canvas.BuildingChanged, Sub(message) _layoutStatus.Text = message
        mapPanel.Controls.Add(_canvas, 0, 2)

        workspace.Controls.Add(CreateRightPanel(), 1, 0)
        UpdateSummary()
        UpdateSelection(Nothing)
        RefreshMonitoring()
        ResumeLayout(True)
    End Sub

    Public Sub SetReadings(snapshots As IReadOnlyList(Of MeterSnapshot), Optional upsSnapshots As IReadOnlyDictionary(Of String, UpsSnapshot) = Nothing)
        _snapshots = If(snapshots, Array.Empty(Of MeterSnapshot)())
        If upsSnapshots IsNot Nothing Then _upsSnapshots = upsSnapshots
        _canvas.SetSnapshots(_snapshots)
        _canvas.SetUpsSnapshots(_upsSnapshots)
        RefreshMonitoring()
        UpdateSummary()
        If _canvas.SelectedAsset IsNot Nothing Then UpdateSelection(_canvas.SelectedAsset)
        If _canvas.SelectedBuildingPart IsNot Nothing AndAlso Not _buildingEditing Then ShowRoomDetails(_canvas.SelectedBuildingPart)
    End Sub

    Public Sub SetPageActive(active As Boolean)
        If _pageActive = active Then Return
        _pageActive = active
        _canvas.SetPageActive(active)
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            _layerMenu.Dispose()
            _toolbarTips.Dispose()
            If _canvas IsNot Nothing Then _canvas.Dispose()
        End If
        MyBase.Dispose(disposing)
    End Sub

    Private Function CreateKpiPanel() As Control
        Dim cards As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 4,
            .RowCount = 1,
            .BackColor = UiTheme.Canvas,
            .Padding = Padding.Empty
        }
        For index = 0 To 3
            cards.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 25.0F))
        Next
        cards.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        Dim names = {"Factory Load", "Map Equipment", "Placed / Unplaced", "Live Data"}
        Dim units = {"kVA - verified T1-T4", "mapped visual items", "placement status", "Good linked acquisitions"}
        For index = 0 To names.Length - 1
            Dim card As New ScadaPanel With {
                .Dock = DockStyle.Fill,
                .BackColor = UiTheme.Surface,
                .Margin = New Padding(2, 1, 4, 2),
                .Padding = New Padding(12, 7, 10, 5), .Radius = 5
            }
            Dim title = UiTheme.Label(names(index).ToUpperInvariant(), New Font("Segoe UI", 7.0F, FontStyle.Bold), UiTheme.MutedInk, False)
            title.Dock = DockStyle.Top
            title.Height = 18
            title.AutoEllipsis = True
            Dim value = UiTheme.Label("--", New Font("Segoe UI", 14.0F, FontStyle.Bold), UiTheme.ShellDeep, False)
            value.Dock = DockStyle.Fill
            value.TextAlign = ContentAlignment.MiddleLeft
            If index = 0 Then _factoryLoadValue = value
            If index = 1 Then _mappedValue = value
            If index = 2 Then _placedValue = value
            If index = 3 Then _connectedValue = value
            Dim unit = UiTheme.Label(units(index), New Font("Segoe UI", 7.5F), UiTheme.MutedInk, False)
            unit.Dock = DockStyle.Bottom
            unit.Height = 16
            card.Controls.Add(value)
            card.Controls.Add(unit)
            card.Controls.Add(title)
            cards.Controls.Add(card, index, 0)
        Next
        Return cards
    End Function

    Private Function CreateRightPanel() As Control
        Dim panel As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 2,
            .BackColor = UiTheme.Canvas,
            .Padding = New Padding(8, 0, 0, 0)
        }
        panel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        panel.RowStyles.Add(New RowStyle(SizeType.Percent, 46.0F))
        panel.RowStyles.Add(New RowStyle(SizeType.Percent, 54.0F))

        Dim summary As New ScadaPanel With {.Dock = DockStyle.Fill, .BackColor = UiTheme.Surface, .Padding = New Padding(12), .Radius = 5}
        Dim heading = UiTheme.Label("EQUIPMENT  /  LIVE : TOTAL", New Font("Segoe UI", 9.0F, FontStyle.Bold), UiTheme.ShellDeep, False)
        heading.Dock = DockStyle.Top
        heading.Height = 27
        Dim summaryScroll As New Panel With {.Dock = DockStyle.Fill, .AutoScroll = True}
        Dim summaryCategories = {"Transformers", "Generators", "UPS", "Battery Rooms", "Compressors", "Chillers",
            "Production Lines", "Can Line", "Water Systems", "Filler Room", "HVAC", "Electrical Panels", "Motors & Drives", "Pumps & Tanks", "Sensors & Controls",
            "Utilities", "CCTV", "Access Control", "Fire System"}
        Dim categories As New TableLayoutPanel With {
            .Dock = DockStyle.Top, .AutoSize = True,
            .ColumnCount = 2,
            .RowCount = summaryCategories.Length,
            .BackColor = UiTheme.Surface,
            .Padding = New Padding(0, 3, 0, 0)
        }
        categories.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 68.0F))
        categories.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 32.0F))
        For index = 0 To summaryCategories.Length - 1
            categories.RowStyles.Add(New RowStyle(SizeType.Absolute, 25))
        Next
        For Each category In summaryCategories
            Dim nameLabel = UiTheme.Label(category, New Font("Segoe UI", 8.5F), UiTheme.Ink, False)
            nameLabel.AutoEllipsis = True
            nameLabel.Dock = DockStyle.Fill
            nameLabel.TextAlign = ContentAlignment.MiddleLeft
            Dim countLabel = UiTheme.Label("0 / 0", New Font("Segoe UI", 8.5F, FontStyle.Bold), UiTheme.MutedInk, False)
            countLabel.Name = "count_" & category.Replace(" ", "_").Replace("/", "_")
            countLabel.Dock = DockStyle.Fill
            countLabel.TextAlign = ContentAlignment.MiddleRight
            Dim row = categories.Controls.Count \ 2
            categories.Controls.Add(nameLabel, 0, row)
            categories.Controls.Add(countLabel, 1, row)
        Next
        summaryScroll.Controls.Add(categories)
        summary.Controls.Add(summaryScroll)
        summary.Controls.Add(heading)
        _fireAlertsButton.Dock = DockStyle.Bottom
        _fireAlertsButton.Height = 30
        AddHandler _fireAlertsButton.Click, Sub() ShowFireAlerts()
        summary.Controls.Add(_fireAlertsButton)
        panel.Controls.Add(summary, 0, 0)

        Dim details As New ScadaPanel With {.Dock = DockStyle.Fill, .BackColor = UiTheme.Surface, .Padding = New Padding(12), .Margin = New Padding(0, 8, 0, 0), .Radius = 5}
        _selectedTitle = UiTheme.Label("SELECT EQUIPMENT", New Font("Segoe UI", 9.0F, FontStyle.Bold), UiTheme.Ink, False)
        _selectedTitle.Dock = DockStyle.Top
        _selectedTitle.Height = 40
        _selectedTitle.AutoEllipsis = True
        _selectedStatus = UiTheme.Label("Unknown / No Data", New Font("Segoe UI", 8.5F, FontStyle.Bold), UiTheme.MutedInk, False)
        _selectedStatus.Dock = DockStyle.Top
        _selectedStatus.Height = 24
        _selectedDetails = UiTheme.Label("Select a marker on the map to inspect its available details.", New Font("Segoe UI", 8.0F), UiTheme.MutedInk, False)
        Dim detailScroll As New Panel With {.Dock = DockStyle.Fill, .AutoScroll = True}
        _selectedDetails.Dock = DockStyle.Top
        _selectedDetails.AutoSize = True
        _selectedDetails.Font = New Font("Segoe UI", 8.5F)
        AddHandler detailScroll.SizeChanged, Sub(sender, e)
                                                _selectedDetails.MaximumSize = New Size(Math.Max(60, detailScroll.ClientSize.Width - 20), 0)
                                            End Sub
        detailScroll.Controls.Add(_selectedDetails)
        _openTransformers = UiTheme.ActionButton("Open Transformers", False)
        _openTransformers.Dock = DockStyle.Bottom
        _openTransformers.Height = 31
        _openTransformers.Visible = False
        AddHandler _openTransformers.Click, Sub(sender, e)
                                                If _openTransformersAction IsNot Nothing Then _openTransformersAction.Invoke()
                                            End Sub
        details.Controls.Add(detailScroll)
        details.Controls.Add(_openTransformers)
        details.Controls.Add(_selectedStatus)
        details.Controls.Add(_selectedTitle)
        panel.Controls.Add(details, 0, 1)
        Return panel
    End Function

    Private Shared Sub AddToolbarButton(host As Control, text As String, action As Action)
        Dim button = UiTheme.ActionButton(text, False, If(text = "Full screen", ChrW(&HE740).ToString(), Nothing))
        button.AccessibleName = text
        If text = "Full screen" Then button.Text = ""
        button.Width = If(text = "+" OrElse text = "-" OrElse text = "Full screen", 32, If(text = "Reset Camera", 96, If(text = "Dimensions", 90, 72)))
        button.Height = 32
        button.Margin = New Padding(2)
        AddHandler button.Click, Sub(sender, e) action.Invoke()
        host.Controls.Add(button)
    End Sub

    Private Shared Function CreateAssets() As List(Of FactoryMapAsset)
        Dim assets As New List(Of FactoryMapAsset)()
        Dim generatorIds = {"G6", "G7", "G8", "G1", "G2", "G3", "G4", "G5", "G10", "G11", "G12", "G9"}
        Dim generatorRows = {273.0F, 306.0F, 338.0F, 370.0F, 402.0F, 434.0F, 466.0F, 498.0F, 560.0F, 592.0F, 624.0F, 656.0F}
        For index = 0 To generatorIds.Length - 1
            assets.Add(New FactoryMapAsset With {
                .Id = "GEN-" & generatorIds(index),
                .Name = "Generator " & generatorIds(index),
                .Category = "Generators",
                .Location = "Generator bank, physical position " & (index + 1).ToString(CultureInfo.InvariantCulture),
                .Source = "No verified generator data source configured",
                .Position = New PointF(0.057F, generatorRows(index) / 766.0F)
            })
        Next

        Dim compressorPositions As PointF() = {
            New PointF(133.0F / 1365.0F, 544.0F / 766.0F),
            New PointF(133.0F / 1365.0F, 514.0F / 766.0F),
            New PointF(133.0F / 1365.0F, 390.0F / 766.0F),
            New PointF(183.0F / 1365.0F, 465.0F / 766.0F),
            New PointF(133.0F / 1365.0F, 482.0F / 766.0F),
            New PointF(133.0F / 1365.0F, 420.0F / 766.0F)
        }
        For index = 1 To 6
            assets.Add(New FactoryMapAsset With {
                .Id = "COMP-" & index.ToString(CultureInfo.InvariantCulture),
                .Name = "Compressor " & index.ToString(CultureInfo.InvariantCulture),
                .Category = "Compressors",
                .Location = "Compressor Room",
                .Source = "No verified compressor data source configured",
                .Position = compressorPositions(index - 1)
            })
        Next

        AddUnplacedAssets(assets, "CHILLER-", "Chiller ", "Chillers", "Position not assigned; the approved map does not clearly identify chiller locations.", 3, "No verified chiller data source configured")

        AddAssetRow(assets, "UPS", "D", "Panel Room, upper row", 4, 0.173F, 0.566F, 0.024F)
        AddAssetRow(assets, "UPS", "C", "Panel Room, second row", 5, 0.173F, 0.597F, 0.024F)
        AddAssetColumn(assets, "UPS", "A", "Panel Room, lower left column", {"A3", "A2", "A1"}, 0.185F, 0.647F, 0.03F)
        AddAssetColumn(assets, "UPS", "B", "Panel Room, lower right column", {"B3", "B2", "B1"}, 0.232F, 0.647F, 0.03F)

        Dim batteryPositions = {
            New PointF(159.0F / 1365.0F, 603.0F / 766.0F),
            New PointF(209.0F / 1365.0F, 603.0F / 766.0F),
            New PointF(184.0F / 1365.0F, 636.0F / 766.0F),
            New PointF(214.0F / 1365.0F, 666.0F / 766.0F)
        }
        For index = 1 To 4
            assets.Add(New FactoryMapAsset With {
                .Id = "BATTERY-ROOM-B" & index.ToString(CultureInfo.InvariantCulture),
                .Name = "Battery Room B" & index.ToString(CultureInfo.InvariantCulture),
                .Category = "Battery Rooms",
                .Location = "Battery room area",
                .Source = "No verified battery-room data source configured",
                .Position = batteryPositions(index - 1)
            })
        Next

        For Each item In {
            (Id:="TRANSFORMER-T1", Name:="Transformer T1", X:=267.0F, Y:=610.0F),
            (Id:="TRANSFORMER-T3", Name:="Transformer T3", X:=308.0F, Y:=610.0F),
            (Id:="TRANSFORMER-T4", Name:="Transformer T4", X:=351.0F, Y:=610.0F),
            (Id:="TRANSFORMER-T2", Name:="Transformer T2", X:=267.0F, Y:=652.0F)
        }
            assets.Add(New FactoryMapAsset With {
                .Id = item.Id,
                .Name = item.Name,
                .Category = "Transformers",
                .Location = "Transformer Area",
                .Source = "Existing PM5110 Modbus TCP acquisition",
                .Position = New PointF(item.X / 1365.0F, item.Y / 766.0F)
            })
        Next

        Dim linePositions = {
            New PointF(606.0F / 1365.0F, 522.0F / 766.0F),
            New PointF(606.0F / 1365.0F, 435.0F / 766.0F),
            New PointF(606.0F / 1365.0F, 360.0F / 766.0F),
            New PointF(604.0F / 1365.0F, 74.0F / 766.0F),
            New PointF(639.0F / 1365.0F, 194.0F / 766.0F),
            New PointF(1163.0F / 1365.0F, 186.0F / 766.0F)
        }
        For index = 1 To 6
            assets.Add(New FactoryMapAsset With {
                .Id = "LINE-PET" & index.ToString(CultureInfo.InvariantCulture),
                .Name = "PET-" & index.ToString(CultureInfo.InvariantCulture),
                .Category = "Production Lines",
                .Location = "PET-" & index.ToString(CultureInfo.InvariantCulture) & " production area",
                .Source = "No verified production data source configured",
                .Position = linePositions(index - 1)
            })
        Next
        assets.Add(New FactoryMapAsset With {
            .Id = "CANLINE-1",
            .Name = "Canline-1",
            .Category = "Production Lines",
            .Location = "Central-left production area",
            .Source = "No verified production data source configured",
            .Position = New PointF(618.0F / 1365.0F, 360.0F / 766.0F)
        })

        For Each asset In assets
            asset.EquipmentId = asset.Id
            Select Case asset.Category
                Case "Generators" : asset.Model = "Caterpillar generator"
                Case "Compressors" : asset.Model = "HP compressor 40 bar"
                Case "Chillers" : asset.Model = "Chiller"
                Case "Battery Rooms" : asset.Model = "Battery rack - 4 levels"
                Case "Production Lines" : asset.Model = "Beverage line with filler"
                Case "Transformers" : asset.Model = "Transformer"
                Case "UPS"
                    Dim suffix = asset.Id.Substring(4)
                    asset.EquipmentId = "UPS-" & suffix.Substring(0, 1) & Integer.Parse(suffix.Substring(1), CultureInfo.InvariantCulture).ToString("00", CultureInfo.InvariantCulture)
                    asset.Model = If(suffix.StartsWith("A", StringComparison.Ordinal) OrElse suffix.StartsWith("B", StringComparison.Ordinal),
                                     "Vertiv NXL 800 kVA", "Vertiv EXL 500 kVA")
            End Select
        Next
        Return assets
    End Function

    Friend Shared Function EquipmentCatalog() As List(Of FactoryMapAsset)
        Dim catalog = CreateAssets().Where(Function(asset) asset.Category <> "UPS").ToList()
        For Each device In UpsSettingsStore.Current.Devices
            catalog.Add(New FactoryMapAsset With {
                .Id = device.Id, .EquipmentId = device.Id, .Name = device.DisplayName,
                .Category = "UPS", .Location = "ATS bank " & device.AtsBank.ToString(CultureInfo.InvariantCulture),
                .Source = "Configured UPS registry: " & device.Model,
                .Model = If(device.Model.Contains("NXL", StringComparison.OrdinalIgnoreCase), "Vertiv NXL 800 kVA", "Vertiv EXL 500 kVA")
            })
        Next
        Return catalog
    End Function

    Private Shared Sub AddUnplacedAssets(assets As List(Of FactoryMapAsset), idPrefix As String, namePrefix As String,
                                         category As String, location As String, count As Integer, source As String)
        For index = 1 To count
            Dim suffix = "C" & index.ToString(CultureInfo.InvariantCulture)
            assets.Add(New FactoryMapAsset With {
                .Id = idPrefix & suffix,
                .Name = namePrefix & suffix,
                .Category = category,
                .Location = location,
                .Source = source,
                .IsPlaced = False
            })
        Next
    End Sub

    Private Shared Sub AddAssetRow(assets As List(Of FactoryMapAsset), idPrefix As String, group As String, location As String,
                                   count As Integer, startX As Single, y As Single, stepX As Single)
        For index = 1 To count
            Dim name = group & index.ToString(CultureInfo.InvariantCulture)
            assets.Add(New FactoryMapAsset With {
                .Id = idPrefix & "-" & name,
                .Name = name,
                .Category = "UPS",
                .Location = location,
                .Source = "No verified UPS data source configured for this map asset",
                .Position = New PointF(startX + ((index - 1) * stepX), y)
            })
        Next
    End Sub

    Private Shared Sub AddAssetColumn(assets As List(Of FactoryMapAsset), idPrefix As String, group As String, location As String,
                                      names As String(), x As Single, startY As Single, stepY As Single)
        For index = 0 To names.Length - 1
            assets.Add(New FactoryMapAsset With {
                .Id = idPrefix & "-" & names(index),
                .Name = names(index),
                .Category = "UPS",
                .Location = location,
                .Source = "No verified UPS data source configured for this map asset",
                .Position = New PointF(x, startY + (index * stepY))
            })
        Next
    End Sub

    Private Sub UpdateSummary()
        Dim expectedTransformers = New HashSet(Of String)({"T1", "T2", "T3", "T4"}, StringComparer.OrdinalIgnoreCase)
        For Each category In {
            "Transformers", "Generators", "UPS", "Battery Rooms", "Compressors", "Chillers",
            "Production Lines", "Can Line", "Water Systems", "Filler Room", "HVAC", "Electrical Panels", "Motors & Drives", "Pumps & Tanks", "Sensors & Controls",
            "Utilities", "CCTV", "Access Control", "Fire System"
        }
            Dim name = "count_" & category.Replace(" ", "_").Replace("/", "_")
            Dim label = FindControl(Me, name)
            Dim mapped = _assets.Where(Function(asset) asset.Category = category).ToArray()
            If category = "Production Lines" Then mapped = mapped.Where(Function(asset) asset.EquipmentId <> "CANLINE-1").ToArray()
            If category = "Can Line" Then mapped = _assets.Where(Function(asset) asset.EquipmentId = "CANLINE-1").ToArray()
            Dim connected = mapped.Count(Function(asset) HasCurrentSource(asset))
            If label IsNot Nothing Then label.Text = connected.ToString(CultureInfo.InvariantCulture) & " / " & mapped.Length.ToString(CultureInfo.InvariantCulture)
        Next
        _mappedValue.Text = _assets.Count.ToString(CultureInfo.InvariantCulture)
        Dim placed = _assets.Where(Function(asset) asset.IsPlaced).Count()
        _placedValue.Text = placed.ToString(CultureInfo.InvariantCulture) & " / " & (_assets.Count - placed).ToString(CultureInfo.InvariantCulture)
        _connectedValue.Text = _assets.Where(Function(asset) HasCurrentSource(asset)).Count().ToString(CultureInfo.InvariantCulture)
        Dim transformerSnapshots = expectedTransformers.Select(Function(name) _snapshots.FirstOrDefault(Function(snapshot) snapshot IsNot Nothing AndAlso
            FactoryMapMonitoring.IsCurrentMeter(snapshot) AndAlso String.Equals(snapshot.TransformerName, name, StringComparison.OrdinalIgnoreCase))).ToArray()
        If transformerSnapshots.All(Function(snapshot) snapshot IsNot Nothing) Then
            Dim readings = transformerSnapshots.Select(Function(snapshot) ElectricalCalculations.ApparentPowerKva(snapshot.DecodedValues)).ToArray()
            If readings.All(Function(value) value.HasValue AndAlso Double.IsFinite(value.Value)) Then
                _factoryLoadValue.Text = readings.Sum(Function(value) value.Value).ToString("#,##0.0", CultureInfo.InvariantCulture)
                Return
            End If
        End If
        _factoryLoadValue.Text = "--"
    End Sub

    Private Shared Function FindControl(parent As Control, name As String) As Label
        For Each child As Control In parent.Controls
            If String.Equals(child.Name, name, StringComparison.Ordinal) Then Return TryCast(child, Label)
            Dim nested = FindControl(child, name)
            If nested IsNot Nothing Then Return nested
        Next
        Return Nothing
    End Function

    Private Sub OnAssetSelected(asset As FactoryMapAsset)
        If _editing AndAlso _canvas.SelectedAssets.Count <= 1 AndAlso Not ReferenceEquals(_assetPicker.SelectedItem, asset) Then _assetPicker.SelectedItem = asset
        UpdateSelection(asset)
    End Sub

    Private Sub UpdateSelection(asset As FactoryMapAsset)
        _openTransformers.Visible = asset IsNot Nothing AndAlso asset.EquipmentId.StartsWith("TRANSFORMER-", StringComparison.OrdinalIgnoreCase)
        If _editing AndAlso _canvas.SelectedAssets.Count > 1 Then
            _openTransformers.Visible = False
            _selectedTitle.Text = _canvas.SelectedAssets.Count & " EQUIPMENT ITEMS SELECTED"
            _selectedStatus.Text = "GROUP MOVE"
            _selectedStatus.ForeColor = UiTheme.Blue
            _selectedDetails.Text = "Drag any selected item to move the whole selection. Arrows nudge the group. Ctrl+drag selects a new region; drag empty space to pan."
            Return
        End If
        If asset Is Nothing Then
            _selectedTitle.Text = "SELECT EQUIPMENT"
            _selectedStatus.Text = "Unknown / No Data"
            _selectedDetails.Text = "Select a marker on the map to inspect its available details."
            Return
        End If

        _selectedTitle.Text = asset.Name.ToUpperInvariant()
        If ShowMonitoringDetails(asset) Then Return
        If FactoryMapMeterLink.Supports(asset.Model) Then
            Dim meter = FindSnapshot(asset)
            _selectedStatus.Text = If(meter Is Nothing, "Meter unavailable / No good data", "Connected meter")
            _selectedStatus.ForeColor = If(meter Is Nothing, UiTheme.MutedInk, UiTheme.Green)
            Dim indication = _canvas.Indication(asset)
            If indication.Signal >= MapSignal.Warning Then
                _selectedStatus.Text = indication.Text
                _selectedStatus.ForeColor = FactoryMapCanvas.SignalColor(indication.Signal)
            ElseIf indication.Signal = MapSignal.Unknown AndAlso meter IsNot Nothing Then
                _selectedStatus.Text &= " / alarm data unknown"
                _selectedStatus.ForeColor = UiTheme.MutedInk
            End If
            _selectedDetails.Text = String.Join(Environment.NewLine, {
                "Map ID: " & asset.Id, "Attached equipment: " & If(asset.EquipmentId = "", "None", asset.EquipmentId),
                "Independent meter: " & If(asset.MeterSourceId = "", "None selected", asset.MeterSourceId), "Shape: " & asset.Model,
                "Latest poll: " & If(meter Is Nothing OrElse Not meter.CapturedAtUtc.HasValue, "--", meter.CapturedAtUtc.GetValueOrDefault().ToLocalTime().ToString("g")),
                "Load: " & Measurement(meter, 10, "kW"), "Voltage L1-N: " & Measurement(meter, 0, "V"),
                "Current L1: " & Measurement(meter, 11, "A"), "Frequency: " & Measurement(meter, 8, "Hz"),
                "Apparent load: " & If(meter Is Nothing, "--", ApparentPower(meter)),
                _canvas.Indication(asset).Detail,
                "Read-only existing PLC meter source. No control commands or new meters created.",
                "Missing / bad-quality sources show --; equipment readings are not substituted."
            })
            Return
        End If
        If asset.EquipmentId.StartsWith("UPS-", StringComparison.OrdinalIgnoreCase) Then
            Dim ups As UpsSnapshot = Nothing
            _upsSnapshots.TryGetValue(asset.EquipmentId, ups)
            If ups IsNot Nothing AndAlso Not String.Equals(ups.DeviceId, asset.EquipmentId, StringComparison.OrdinalIgnoreCase) Then ups = Nothing
            _selectedStatus.Text = _canvas.Indication(asset).Text
            _selectedStatus.ForeColor = FactoryMapCanvas.SignalColor(_canvas.Indication(asset).Signal)
            _selectedDetails.Text = String.Join(Environment.NewLine, {
                "Map ID: " & asset.Id,
                "Linked equipment: " & asset.EquipmentId,
                "Level / location: " & asset.Level & " / " & asset.Location,
                "Shape: " & asset.Model & " (schematic)",
                "Source: Existing UPS SNMP acquisition",
                "Latest poll: " & If(ups Is Nothing, "--", ups.TimestampUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)),
                "Output: " & UpsReading(ups, Function(item) item.AccountingOutputKva, "kVA"),
                "Load: " & UpsReading(ups, Function(item) item.PhaseMax("OutputLoad"), "%"),
                "Battery charge: " & UpsReading(ups, Function(item) item.Value("ChargePercent"), "%"),
                "Runtime: " & UpsReading(ups, Function(item) item.Value("RuntimeMinutes"), "min"),
                "Battery voltage: " & UpsReading(ups, Function(item) item.Value("BatteryVoltage"), "V"),
                "Battery current: " & UpsReading(ups, Function(item) item.Value("BatteryCurrent"), "A"),
                "Battery temperature: " & UpsReading(ups, Function(item) item.Value("BatteryTemperature"), "C"),
                "Map alarm: " & _canvas.Indication(asset).Text,
                _canvas.Indication(asset).Detail,
                If(ups Is Nothing, "No acquired data.", ups.QualityDetail),
                "Details / alarms / history: UPS Monitoring and Historian"
            })
            Return
        End If
        Dim snapshot = FindSnapshot(asset)
        If snapshot Is Nothing Then
            _selectedStatus.Text = "Unknown / Not Connected"
            _selectedStatus.ForeColor = UiTheme.MutedInk
            _selectedDetails.Text = String.Join(Environment.NewLine, {
                "Map ID: " & asset.Id,
                "Linked equipment: " & If(String.IsNullOrEmpty(asset.EquipmentId), "None (visual only)", asset.EquipmentId),
                "Shape: " & asset.Model & " (schematic)",
                "Category: " & asset.Category,
                "Location: " & asset.Location,
                "Communication source: " & asset.Source,
                "Latest reading: --",
                "Electrical parameters: No Data",
                "Energy / alarms / history: No verified source"
            })
            Return
        End If

        _selectedStatus.Text = "Connected • " & SnapshotState(snapshot)
        _selectedStatus.ForeColor = If(snapshot.PowerState = TransformerPowerState.On AndAlso snapshot.Quality = MeterQuality.Good,
            UiTheme.Green, UiTheme.MutedInk)
        If _canvas.Indication(asset).Signal >= MapSignal.Warning Then
            _selectedStatus.Text = _canvas.Indication(asset).Text
            _selectedStatus.ForeColor = FactoryMapCanvas.SignalColor(_canvas.Indication(asset).Signal)
        ElseIf _canvas.Indication(asset).Signal = MapSignal.Unknown Then
            _selectedStatus.Text &= " / alarm data unknown"
            _selectedStatus.ForeColor = UiTheme.MutedInk
        End If
        _selectedDetails.Text = String.Join(Environment.NewLine, {
            "Map ID: " & asset.Id,
            "Linked equipment: " & asset.EquipmentId,
            "Category: " & asset.Category,
            "Location: " & asset.Location,
            "Communication source: Existing PM5110 / Modbus TCP",
            "Latest reading: " & If(snapshot.CapturedAtUtc.HasValue, snapshot.CapturedAtUtc.Value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), "--"),
            "Load: " & Measurement(snapshot, 10, "kW"),
            "Voltage L1-N: " & Measurement(snapshot, 0, "V"),
            "Voltage L2-N: " & Measurement(snapshot, 1, "V"),
            "Voltage L3-N: " & Measurement(snapshot, 2, "V"),
            "Frequency: " & Measurement(snapshot, 8, "Hz"),
            "Current L1: " & Measurement(snapshot, 11, "A"),
            "Current L2: " & Measurement(snapshot, 12, "A"),
            "Current L3: " & Measurement(snapshot, 13, "A"),
            "Apparent load: " & ApparentPower(snapshot),
            "Temperature: " & Measurement(snapshot, 15, "°C"),
            _canvas.Indication(asset).Detail,
            "Energy / alarms / history: see Transformers and Historian pages"
        })
    End Sub

    Private Shared Function UpsReading(snapshot As UpsSnapshot, readValue As Func(Of UpsSnapshot, Double?), unit As String) As String
        If Not FactoryMapMonitoring.IsCurrentUps(snapshot) Then Return "--"
        Dim value = readValue(snapshot)
        If Not value.HasValue OrElse Not Double.IsFinite(value.Value) Then Return "--"
        Return value.Value.ToString("0.0", CultureInfo.InvariantCulture) & " " & unit
    End Function

    Private Function FindSnapshot(asset As FactoryMapAsset) As MeterSnapshot
        Return FactoryMapMeterLink.Find(asset, _snapshots)
    End Function

    Private Shared Function SnapshotState(snapshot As MeterSnapshot) As String
        If snapshot Is Nothing Then Return "Unknown"
        Select Case snapshot.PowerState
            Case TransformerPowerState.On : Return "Verified On"
            Case TransformerPowerState.Off : Return "Verified Off"
            Case Else : Return "State Unknown"
        End Select
    End Function

    Private Shared Function Measurement(snapshot As MeterSnapshot, index As Integer, unit As String) As String
        Dim value = FactoryMapMonitoring.MeterValue(snapshot, index)
        If Not value.HasValue OrElse Math.Abs(value.Value) > 100000000 Then Return "--"
        Return value.Value.ToString("0.0", CultureInfo.InvariantCulture) & " " & unit
    End Function

    Private Shared Function ApparentPower(snapshot As MeterSnapshot) As String
        If Not FactoryMapMonitoring.IsCurrentMeter(snapshot) Then Return "--"
        Dim kva = ElectricalCalculations.ApparentPowerKva(snapshot.DecodedValues)
        If Not kva.HasValue OrElse Not Double.IsFinite(kva.Value) Then Return "--"
        Return kva.Value.ToString("0.0", CultureInfo.InvariantCulture) & " kVA"
    End Function

    Private Sub ToggleEditMode(sender As Object, e As EventArgs)
        If Not _canEditMap OrElse _buildingEditing OrElse Not String.IsNullOrWhiteSpace(_loadError) Then Return
        If _editing Then Return
        CreateEquipmentTools()
        _savedAssets = _assets.Select(Function(asset) asset.Clone()).ToList()
        _editing = True
        BeginEditHistory()
        _canvas.EditMode = _editing
        _editButton.Text = If(_editing, "Editing…", "Edit Map")
        _saveButton.Visible = _editing
        _cancelButton.Visible = _editing
        _editTools.Visible = _editing
        _editRow.Height = If(_editing, 42, 0)
        RefreshAssetPicker(_canvas.SelectedAsset)
        _editButton.Enabled = Not _editing
        _buildingButton.Enabled = Not _editing AndAlso _buildingError = ""
        _layoutStatus.Text = If(_editing, "Ctrl+drag: select region | Drag empty space: pan | Drag selected item: move group | Arrows: nudge | Ctrl+C/V: copy/paste | Ctrl+D: duplicate | Delete", String.Empty)
        _layoutStatus.ForeColor = UiTheme.Amber
    End Sub

    Private Sub CreateEquipmentTools()
        _editTools = New FlowLayoutPanel With {
            .Dock = DockStyle.Fill, .Height = 42, .WrapContents = False,
            .AutoScroll = True, .BackColor = UiTheme.Surface
        }
        _assetPicker = New ComboBox With {
            .DropDownStyle = ComboBoxStyle.DropDownList, .Width = 175, .DropDownWidth = 500, .Margin = New Padding(3, 5, 3, 3),
            .AutoCompleteSource = AutoCompleteSource.ListItems, .AutoCompleteMode = AutoCompleteMode.SuggestAppend
        }
        AddHandler _assetPicker.SelectedIndexChanged, Sub(sender, e) _canvas.SelectAsset(TryCast(_assetPicker.SelectedItem, FactoryMapAsset))
        _editTools.Controls.Add(_assetPicker)
        AddToolbarButton(_editTools, "Add Item", AddressOf AddMapItem)
        AddToolbarButton(_editTools, "Edit Item", AddressOf EditMapItem)
        AddToolbarButton(_editTools, "Delete Item", AddressOf DeleteMapItem)
        AddToolbarButton(_editTools, "Duplicate", AddressOf DuplicateMapItem)
        AddToolbarButton(_editTools, "Draw Pipe", Sub() BeginConnectorTool(FactoryMapConnectors.PipeKind))
        AddToolbarButton(_editTools, "Draw Conveyor", Sub() BeginConnectorTool(FactoryMapConnectors.ConveyorKind))
        AddToolbarButton(_editTools, "Draw Cable Tray", Sub() BeginConnectorTool(FactoryMapConnectors.CableTrayKind))
        AddToolbarButton(_editTools, "Draw Cable", Sub() BeginConnectorTool(FactoryMapConnectors.CableKind))
        _saveButton = UiTheme.ActionButton("Save", True)
        _cancelButton = UiTheme.ActionButton("Cancel", False)
        For Each button As Button In {_saveButton, _cancelButton}
            button.Height = 30
            button.Margin = New Padding(2)
            _editTools.Controls.Add(button)
        Next
        AddHandler _saveButton.Click, AddressOf SaveLayout
        AddHandler _cancelButton.Click, AddressOf CancelLayout
        For Each action As Button In _editTools.Controls.OfType(Of Button)()
            action.Width = If(action.Text.StartsWith("Draw ", StringComparison.Ordinal), 118, 82)
        Next
        _mapPanel.Controls.Add(_editTools, 0, 3)
    End Sub

    Private Sub ReleaseEquipmentTools()
        _canvas.SetConnectorTool("")
        If _editTools IsNot Nothing Then
            _mapPanel.Controls.Remove(_editTools)
            _editTools.Dispose()
        End If
        _editTools = Nothing
        _assetPicker = Nothing
        _saveButton = Nothing
        _cancelButton = Nothing
        _savedAssets = Nothing
    End Sub

    Private Sub SaveLayout(sender As Object, e As EventArgs)
        If Not _editing Then Return
        Try
            If Not _canEditMap Then Throw New UnauthorizedAccessException("Only an authenticated administrator can edit the factory map.")
            FactoryMapLayoutStore.SaveAssets(_assets)
            _editing = False
            EndEditHistory()
            _canvas.EditMode = False
            _editButton.Text = "Edit Map"
            _editButton.Enabled = True
            _buildingButton.Enabled = _buildingError = ""
            ReleaseEquipmentTools()
            _editRow.Height = 0
            UpdateSummary()
            _layoutStatus.Text = "Map layout saved. A previous version was retained as a backup."
            _layoutStatus.ForeColor = UiTheme.Green
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is InvalidDataException
            MessageBox.Show(Me, "The map could not be saved. Your edits remain available to retry; the saved map is unchanged." & Environment.NewLine & ex.Message,
                            "Factory Map Save Failed", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub CancelLayout(sender As Object, e As EventArgs)
        If Not _editing Then Return
        _assets.Clear()
        _assets.AddRange(_savedAssets.Select(Function(asset) asset.Clone()))
        _canvas.SelectAsset(Nothing)
        _canvas.Invalidate()
        _editing = False
        EndEditHistory()
        _canvas.EditMode = False
        _editButton.Text = "Edit Map"
        _editButton.Enabled = True
        _buildingButton.Enabled = _buildingError = ""
        ReleaseEquipmentTools()
        _editRow.Height = 0
        UpdateSummary()
        _layoutStatus.Text = "Unsaved map changes were cancelled."
        _layoutStatus.ForeColor = UiTheme.MutedInk
    End Sub

    Private Sub RefreshAssetPicker(selected As FactoryMapAsset)
        _assetPicker.BeginUpdate()
        _assetPicker.Items.Clear()
        _assetPicker.Items.AddRange(_assets.Where(Function(asset) asset.Level = _canvas.ActiveLevel).Cast(Of Object)().ToArray())
        _assetPicker.SelectedItem = selected
        _assetPicker.EndUpdate()
    End Sub

    Private Sub AddMapItem()
        If Not _editing OrElse Not _canEditMap Then Return
        Using dialog As New FactoryMapEquipmentLibraryForm()
            If dialog.ShowDialog(_canvas.FindForm()) <> DialogResult.OK Then Return
            PlaceLibraryItem(dialog.SelectedTemplate)
        End Using
    End Sub

    Private Sub PlaceLibraryItem(template As FactoryMapAsset)
        If Not _editing OrElse Not _canEditMap Then Return
        Dim asset = template.Clone()
        asset.Id = "MAP-" & Guid.NewGuid().ToString("N")
        asset.Level = _canvas.ActiveLevel
        asset.Position = _canvas.PlacementCenter()
        asset.IsPlaced = True
        FactoryMapLayoutStore.ValidateAssets({asset})
        _assets.Add(asset)
        For Each toggle As ToolStripMenuItem In _layerMenu.Items.OfType(Of ToolStripMenuItem)()
            If toggle.Text = asset.Category Then toggle.Checked = True
        Next
        _canvas.SetLayerVisible(asset.Category, True)
        RefreshAssetPicker(asset)
        _canvas.SelectAsset(asset)
        MapItemsChanged()
        _layoutStatus.Text = "Added at view center. Drag to move; handles resize / rotate; Edit Item links equipment or sets properties; Ctrl+D adds another; Ctrl+Z undoes."
        _canvas.Focus()
    End Sub

    Private Function ConnectorDefaults(kind As String) As (Color As String, Thickness As Single, Height As Single)
        Select Case kind
            Case FactoryMapConnectors.PipeKind : Return ("#79A9BA", 0.12F, 0.75F)
            Case FactoryMapConnectors.CableTrayKind : Return ("#9AA3A8", 0.3F, 2.5F)
            Case FactoryMapConnectors.CableKind : Return ("#222222", 0.016F, 2.6F)
            Case Else : Return ("#87A9B6", 0.45F, 0.75F)
        End Select
    End Function

    Private Sub BeginConnectorTool(kind As String)
        If Not _editing OrElse Not _canEditMap Then Return
        _canvas.SetConnectorTool(If(_canvas.ConnectorTool = kind, "", kind))
        _layoutStatus.Text = If(_canvas.ConnectorTool = "", "Draw tool off.",
            "Click the first item (or a floor spot), then the second. The " & kind.ToLowerInvariant() & " stays attached and follows when items move.")
        _canvas.Focus()
    End Sub

    Private Sub OnConnectorDrawn(kind As String, fromAsset As FactoryMapAsset, fromPoint As PointF, toAsset As FactoryMapAsset, toPoint As PointF)
        If Not _editing OrElse Not _canEditMap Then Return
        Dim defaults = ConnectorDefaults(kind)
        Using dialog As New FactoryMapConnectorForm("New " & kind.ToLowerInvariant(), kind, kind, defaults.Color, defaults.Thickness, defaults.Height)
            If dialog.ShowDialog(_canvas.FindForm()) <> DialogResult.OK Then Return
            Dim connector = FactoryMapConnectors.Create(dialog.Kind, dialog.ConnectorName, fromAsset, fromPoint, toAsset, toPoint,
                dialog.ColorHex, dialog.ThicknessMeters, dialog.HeightMeters, _assets, dialog.StyleName, dialog.CableCount, dialog.DepthMeters)
            connector.Level = _canvas.ActiveLevel
            connector.RoomId = If(fromAsset?.RoomId, If(toAsset?.RoomId, ""))
            FactoryMapLayoutStore.ValidateAssets(_assets.Concat({connector}))
            _assets.Add(connector)
            _canvas.SetLayerVisible(connector.Category, True)
            RefreshAssetPicker(connector)
            _canvas.SelectAsset(connector)
            MapItemsChanged()
        End Using
    End Sub

    Private Sub EditConnector(connector As FactoryMapAsset)
        Using dialog As New FactoryMapConnectorForm("Edit " & connector.ConnectorKind.ToLowerInvariant(), connector.ConnectorKind, connector.Name,
                connector.BodyColor, connector.ConnectorThicknessMeters, connector.ConnectorHeightMeters,
                connector.ConnectorStyle, connector.ConnectorCount, connector.ConnectorDepthMeters)
            If dialog.ShowDialog(_canvas.FindForm()) <> DialogResult.OK Then Return
            connector.Name = dialog.ConnectorName
            connector.ConnectorKind = dialog.Kind
            connector.ConnectorStyle = dialog.StyleName
            connector.ConnectorCount = dialog.CableCount
            connector.ConnectorDepthMeters = dialog.DepthMeters
            connector.BodyColor = dialog.ColorHex
            connector.ConnectorThicknessMeters = dialog.ThicknessMeters
            connector.ConnectorHeightMeters = dialog.HeightMeters
            FactoryMapConnectors.Rebuild(connector, _assets)
            RefreshAssetPicker(connector)
        End Using
        MapItemsChanged()
    End Sub

    Private Sub EditMapItem()
        If Not _editing OrElse Not _canEditMap Then Return
        Dim asset = _canvas.SelectedAsset
        If asset Is Nothing Then
            _layoutStatus.Text = "Select an item on the map or in the item list first."
            Return
        End If
        If FactoryMapConnectors.IsConnector(asset) Then
            EditConnector(asset)
            Return
        End If
        Using dialog As New FactoryMapItemForm(asset, EquipmentCatalog())
            If Not ConfigureMeters(dialog) Then Return
            If dialog.ShowDialog(_canvas.FindForm()) <> DialogResult.OK Then Return
            _assets(_assets.IndexOf(asset)) = dialog.Asset
            RefreshAssetPicker(dialog.Asset)
        End Using
        MapItemsChanged()
    End Sub

    Private Function ConfigureMeters(dialog As FactoryMapItemForm) As Boolean
        Try
            Dim settings = PlcSettingsStore.Load()
            Dim choices As New List(Of KeyValuePair(Of String, String))
            If settings IsNot Nothing Then
                For Each block In settings.Blocks
                    choices.Add(New KeyValuePair(Of String, String)(block.TransformerName,
                        block.TransformerName & " / " & block.MeterModel & " / register " & block.RegisterStart))
                Next
            End If
            dialog.SetMeterChoices(choices)
            dialog.ConfigureMonitoring(MonitoringDeviceStore.Load(), _building.Where(Function(part) part.Level = _canvas.ActiveLevel).ToList())
            Return True
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is UnauthorizedAccessException
            MessageBox.Show(_canvas.FindForm(), "Configured meters could not be loaded. No meter choices have been invented." & Environment.NewLine & ex.Message,
                            "Map meter catalog", MessageBoxButtons.OK, MessageBoxIcon.Error)
            Return False
        End Try
    End Function

    Private Sub DeleteMapItem()
        If Not _editing OrElse Not _canEditMap Then Return
        Dim asset = _canvas.SelectedAsset
        If asset Is Nothing Then
            _layoutStatus.Text = "Select an item to delete first."
            Return
        End If
        If MessageBox.Show(_canvas.FindForm(), "Remove " & asset.Name & " from the map only? System equipment and history will not be deleted.",
                           "Delete map item", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) <> DialogResult.Yes Then Return
        _assets.Remove(asset)
        _canvas.SelectAsset(Nothing)
        RefreshAssetPicker(Nothing)
        MapItemsChanged()
    End Sub

    Private Sub DuplicateMapItem()
        If Not _editing OrElse Not _canEditMap Then Return
        If CopySelection() Then PasteSelection()
    End Sub

    Private Sub HandleEditCommand(command As String)
        If Not _canEditMap OrElse (Not _editing AndAlso Not _buildingEditing) Then Return
        _canvas.CompleteEditGesture()
        Select Case command
            Case "Undo" : RestoreEditHistory(-1)
            Case "Redo" : RestoreEditHistory(1)
            Case "Cut" : CutSelection()
            Case "Copy" : CopySelection()
            Case "Paste" : PasteSelection()
            Case "Duplicate"
                If CopySelection() Then PasteSelection()
            Case "Delete"
                If _buildingEditing Then DeleteBuildingPart() Else DeleteMapItem()
            Case "Properties"
                If _buildingEditing Then EditBuildingPart(False) Else EditMapItem()
        End Select
    End Sub

    Private Function CopySelection() As Boolean
        If _editing AndAlso _canvas.SelectedAsset IsNot Nothing Then
            _copiedAsset = _canvas.SelectedAsset.Clone()
            _copiedBuilding = Nothing
        ElseIf _buildingEditing AndAlso _canvas.SelectedBuildingPart IsNot Nothing Then
            Dim selected = _canvas.SelectedBuildingPart
            _copiedBuilding = New List(Of FactoryBuildingPart) From {FactoryMapBuildingStore.Resolve(selected, _building).Clone()}
            _copiedBuilding(0).ParentWallId = ""
            If selected.Kind = "Wall" Then _copiedBuilding.AddRange(_building.Where(Function(part) part.ParentWallId = selected.Id).Select(Function(part) part.Clone()))
            _copiedAsset = Nothing
        Else
            _layoutStatus.Text = "Select an element before Copy."
            Return False
        End If
        _pasteCount = 0
        _layoutStatus.Text = "Copied. Ctrl+V pastes nearby; drag to place. Equipment and meter links are not copied."
        Return True
    End Function

    Private Sub PasteSelection()
        If _editing AndAlso _copiedAsset IsNot Nothing Then
            Dim item = _copiedAsset.Clone()
            _pasteCount += 1
            item.Id = "MAP-" & Guid.NewGuid().ToString("N")
            item.Name = item.Name.Substring(0, Math.Min(item.Name.Length, 113)) & " (copy)"
            item.EquipmentId = ""
            item.MeterSourceId = ""
            item.MonitoringDeviceId = ""
            item.ParentUpsId = ""
            item.CameraIpAddress = ""
            If item.Level <> _canvas.ActiveLevel Then item.RoomId = ""
            item.Level = _canvas.ActiveLevel
            item.Source = "No linked acquisition source"
            item.Position = New PointF(Math.Min(4, item.Position.X + _pasteCount * 0.02F), Math.Min(4, item.Position.Y + _pasteCount * 0.02F))
            _assets.Add(item)
            RefreshAssetPicker(item)
            MapItemsChanged()
        ElseIf _buildingEditing AndAlso _copiedBuilding IsNot Nothing Then
            Dim copies = _copiedBuilding.Select(Function(part) part.Clone()).ToList()
            Dim ids = copies.ToDictionary(Function(part) part.Id, Function(part) Guid.NewGuid().ToString("N"))
            Dim offset = (_pasteCount + 1) * 0.02F
            For Each part In copies
                Dim originalId = part.Id
                part.Id = ids(originalId)
                part.Level = _canvas.ActiveLevel
                part.Name = part.Name.Substring(0, Math.Min(part.Name.Length, 113)) & " (copy)"
                part.RoomId = ""
                part.RoomName = ""
                part.MonitoringDeviceId = ""
                part.TemperatureSourceId = ""
                part.TemperatureSourceConfirmed = False
                part.FirePanelId = ""
                part.FireSiteId = ""
                part.FireZone = ""
                If part.ParentWallId <> "" Then
                    part.ParentWallId = ids(part.ParentWallId)
                Else
                    part.Position = New PointF(Math.Min(4, part.X + offset), Math.Min(4, part.Y + offset))
                End If
            Next
            Try
                FactoryMapBuildingStore.Validate(_building.Concat(copies))
            Catch ex As InvalidDataException
                _layoutStatus.Text = "Paste rejected: " & ex.Message
                Return
            End Try
            _pasteCount += 1
            _building.AddRange(copies)
            RecordEditHistory()
            RefreshBuildingPicker(copies(0))
            _canvas.Invalidate()
            _layoutStatus.Text = "Building copy created, including attached openings. Save to persist; Cancel to restore."
        Else
            _layoutStatus.Text = "Copy equipment in Edit Map, or a building element in Building, before pasting in the same mode."
        End If
        _canvas.Focus()
    End Sub

    Private Sub MapItemsChanged()
        RecordEditHistory()
        _canvas.Invalidate()
        UpdateSummary()
        _layoutStatus.Text = "Map items changed. Choose Save to persist or Cancel to restore the previous map."
    End Sub

    Private Sub OnAssetMoved(asset As FactoryMapAsset)
        If _editing AndAlso asset IsNot Nothing Then _layoutStatus.Text = "Position changed for " &
            If(_canvas.SelectedAssets.Count > 1, _canvas.SelectedAssets.Count & " selected items", asset.Id) & ". Choose Save to persist or Cancel to revert."
    End Sub

    Private Sub BeginBuildingEdit(sender As Object, e As EventArgs)
        If Not _canEditMap OrElse _editing OrElse _buildingEditing OrElse _buildingError <> "" Then Return
        CreateBuildingTools()
        _savedBuilding = _building.Select(Function(part) part.Clone()).ToList()
        _buildingEditing = True
        BeginEditHistory()
        _canvas.EditMode = False
        _canvas.BuildingEditMode = True
        _canvas.SelectAsset(Nothing)
        _buildingButton.Enabled = False
        _editButton.Enabled = False
        _buildingTools.Visible = True
        _editRow.Height = 42
        RefreshBuildingPicker(Nothing)
        _layoutStatus.Text = "BUILDING: drag / handles: move, resize, rotate | Ctrl+C/V: copy/paste | Ctrl+D: duplicate | Delete | Right-click: actions"
        _layoutStatus.ForeColor = UiTheme.Amber
        _canvas.Focus()
    End Sub

    Private Sub CreateBuildingTools()
        _buildingTools = New FlowLayoutPanel With {
            .Dock = DockStyle.Fill, .AutoSize = True, .WrapContents = True, .BackColor = UiTheme.SurfaceMuted
        }
        _buildingPicker = New ComboBox With {
            .DropDownStyle = ComboBoxStyle.DropDownList, .Width = 175, .DropDownWidth = 500, .Margin = New Padding(3, 5, 3, 3)
        }
        AddHandler _buildingPicker.SelectedIndexChanged, Sub(sender, e) _canvas.SelectBuildingPart(TryCast(_buildingPicker.SelectedItem, FactoryBuildingPart))
        _buildingTools.Controls.Add(_buildingPicker)
        AddToolbarButton(_buildingTools, "Add", Sub() EditBuildingPart(True))
        AddToolbarButton(_buildingTools, "Properties", Sub() EditBuildingPart(False))
        AddToolbarButton(_buildingTools, "Delete", AddressOf DeleteBuildingPart)
        AddToolbarButton(_buildingTools, "Rooms", AddressOf EditRooms)
        AddToolbarButton(_buildingTools, "Save", AddressOf SaveBuilding)
        AddToolbarButton(_buildingTools, "Cancel", AddressOf CancelBuilding)
        CreateDrawingTools()
        For Each action As Button In _buildingTools.Controls.OfType(Of Button)()
            action.Width = 76
        Next
        _mapPanel.Controls.Add(_buildingTools, 0, 3)
        _editRow.SizeType = SizeType.AutoSize
    End Sub

    Private Sub ReleaseBuildingTools()
        If _buildingTools IsNot Nothing Then
            _mapPanel.Controls.Remove(_buildingTools)
            _buildingTools.Dispose()
        End If
        _buildingTools = Nothing
        _buildingPicker = Nothing
        _savedBuilding = Nothing
        _editRow.SizeType = SizeType.Absolute
    End Sub

    Private Sub RefreshBuildingPicker(selected As FactoryBuildingPart)
        _buildingPicker.BeginUpdate()
        _buildingPicker.Items.Clear()
        _buildingPicker.Items.AddRange(_building.Where(Function(part) part.Level = _canvas.ActiveLevel).OrderBy(Function(part) part.Kind).ThenBy(Function(part) part.Name).Cast(Of Object)().ToArray())
        _buildingPicker.SelectedItem = selected
        _buildingPicker.EndUpdate()
    End Sub

    Private Sub OnBuildingSelected(part As FactoryBuildingPart)
        If Not _buildingEditing Then
            If part IsNot Nothing Then ShowRoomDetails(part)
            Return
        End If
        _selectedTitle.Text = If(part Is Nothing, "Building editor", If(part.RoomName = "", part.Name, part.RoomName & " / " & part.Name))
        _selectedStatus.Text = "VISUAL ONLY"
        _selectedStatus.ForeColor = UiTheme.Amber
        _selectedDetails.Text = If(part Is Nothing, "Select a wall, door, window, gate or floor. Use the list for overlapping elements.",
            part.Kind & " / " & part.Material & Environment.NewLine & part.Style & Environment.NewLine &
            "Drag to move. Handles resize / rotate." & Environment.NewLine & "Properties for size, height, color and opening direction." &
            Environment.NewLine & "No building-control commands are sent.")
        _openTransformers.Visible = False
        If Not ReferenceEquals(_buildingPicker.SelectedItem, part) Then _buildingPicker.SelectedItem = part
    End Sub

    Private Sub EditBuildingPart(isNew As Boolean)
        If Not _buildingEditing OrElse Not _canEditMap Then Return
        Dim part = _canvas.SelectedBuildingPart
        If Not isNew AndAlso part Is Nothing Then
            _layoutStatus.Text = "Select a building element on the map or in the building list."
            Return
        End If
        If isNew Then part = New FactoryBuildingPart With {.Level = _canvas.ActiveLevel}
        Using dialog As New FactoryMapBuildingForm(part, _building.Where(Function(item) item.Level = _canvas.ActiveLevel).ToList(), isNew)
            If dialog.ShowDialog(_canvas.FindForm()) <> DialogResult.OK Then Return
            If isNew Then
                If _canvas.SnapEnabled Then FactoryMapPlacement.AttachOpening(dialog.Part, _building, 10)
                _building.Add(dialog.Part)
            Else
                _building(_building.IndexOf(part)) = dialog.Part
            End If
            RefreshBuildingPicker(dialog.Part)
        End Using
        RecordEditHistory()
        _canvas.Invalidate()
        _layoutStatus.Text = "Building changed. Drag the selected element to place it; Save persists, Cancel restores."
        _canvas.Focus()
    End Sub

    Private Sub EditRooms()
        If Not _buildingEditing OrElse Not _canEditMap Then Return
        Dim selectedId = If(_canvas.SelectedBuildingPart Is Nothing, "", _canvas.SelectedBuildingPart.Id)
        Using dialog As New FactoryMapRoomForm(_building.Where(Function(part) part.Level = _canvas.ActiveLevel).ToList(),
                                              _canvas.SelectedBuildingPart, _snapshots.Select(Function(snapshot) "PLC:" & snapshot.TransformerName))
            If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
            _building.RemoveAll(Function(part) part.Level = _canvas.ActiveLevel)
            _building.AddRange(dialog.Parts)
        End Using
        Dim selected = _building.FirstOrDefault(Function(part) part.Id = selectedId)
        RefreshBuildingPicker(selected)
        _canvas.SelectBuildingPart(selected)
        RecordEditHistory()
        _layoutStatus.Text = "Room created / updated. Save Building to persist; Cancel restores previous rooms."
        _canvas.Focus()
    End Sub

    Private Sub EditSite()
        If Not _canEditMap Then Return
        Try
            Using dialog As New FactoryMapSiteForm(FactoryMapSiteStore.Load())
                If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
                Dim width = dialog.Settings.WidthPercent / 100.0F
                Dim depth = dialog.Settings.DepthPercent / 100.0F
                If _assets.Any(Function(asset) asset.IsPlaced AndAlso (asset.Position.X > width OrElse asset.Position.Y > depth)) OrElse
                    _building.Any(Function(part) FactoryMapBuildingStore.Resolve(part, _building).X > width OrElse
                        FactoryMapBuildingStore.Resolve(part, _building).Y > depth) Then Throw New InvalidDataException("The site cannot hide existing item centers. Choose a larger site or move the items first.")
                FactoryMapSiteStore.Save(dialog.Settings)
                _canvas.SetSite(dialog.Settings)
            End Using
            _layoutStatus.Text = "Site saved separately. Existing coordinates and sizes are unchanged."
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is UnauthorizedAccessException
            MessageBox.Show(Me, "Site settings could not be saved." & Environment.NewLine & ex.Message, "Factory site", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub DeleteBuildingPart()
        If Not _buildingEditing OrElse Not _canEditMap Then Return
        Dim part = _canvas.SelectedBuildingPart
        If part Is Nothing Then
            _layoutStatus.Text = "Select a building element to delete."
            Return
        End If
        Dim children = _building.Where(Function(item) item.ParentWallId = part.Id).ToArray()
        Dim message = "Delete " & part.Name & " from this visual building layout?"
        If children.Length > 0 Then message &= Environment.NewLine & children.Length & " attached opening(s) will also be deleted."
        If MessageBox.Show(_canvas.FindForm(), message, "Delete building element", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) <> DialogResult.Yes Then Return
        _building.RemoveAll(Function(item) item.Id = part.Id OrElse item.ParentWallId = part.Id)
        RecordEditHistory()
        RefreshBuildingPicker(Nothing)
        _canvas.SelectBuildingPart(Nothing)
        _canvas.Invalidate()
        _layoutStatus.Text = "Building element deleted. Save to persist or Cancel to restore."
    End Sub

    Private Sub SaveBuilding()
        If Not _buildingEditing OrElse Not _canEditMap Then Return
        Try
            FactoryMapBuildingStore.Save(_building)
            FinishBuildingEdit()
            _layoutStatus.Text = "Building saved separately from equipment. Previous saved version retained as backup."
            _layoutStatus.ForeColor = UiTheme.Green
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
            MessageBox.Show(Me, "Building could not be saved. Edits remain available to correct or retry." & Environment.NewLine & ex.Message,
                            "Building save failed", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub CancelBuilding()
        If Not _buildingEditing Then Return
        _building.Clear()
        _building.AddRange(_savedBuilding.Select(Function(part) part.Clone()))
        FinishBuildingEdit()
        _layoutStatus.Text = "Unsaved building changes cancelled. Equipment is unchanged."
        _layoutStatus.ForeColor = UiTheme.MutedInk
    End Sub

    Private Sub FinishBuildingEdit()
        _buildingEditing = False
        EndEditHistory()
        _canvas.BuildingEditMode = False
        _canvas.SelectBuildingPart(Nothing)
        ReleaseBuildingTools()
        _editRow.Height = 0
        _buildingButton.Enabled = True
        _editButton.Enabled = _loadError = ""
        UpdateSelection(Nothing)
        _canvas.Invalidate()
    End Sub

    Private Sub ResetView()
        _canvas.ResetView()
    End Sub

    Private Sub ShowTopView()
        _canvas.SetTopView()
    End Sub

    Private Sub ShowIsometricView()
        _canvas.SetIsometricView()
    End Sub

    Private Sub FitFactory()
        _canvas.FitFactory()
    End Sub

    Private Sub EditDimensions(sender As Object, e As EventArgs)
        Try
            Using dialog As New FactoryMapDimensionsForm()
                dialog.ShowDialog(Me)
                If dialog.Saved Then _canvas.SetDimensions(dialog.Dimensions)
            End Using
        Catch ex As IOException
            MessageBox.Show(Me, "Saved provisional dimensions could not be opened." & Environment.NewLine & ex.Message,
                            "Factory Map Dimensions", MessageBoxButtons.OK, MessageBoxIcon.Error)
        Catch ex As JsonException
            MessageBox.Show(Me, "Saved provisional dimensions are invalid." & Environment.NewLine & ex.Message,
                            "Factory Map Dimensions", MessageBoxButtons.OK, MessageBoxIcon.Error)
        Catch ex As InvalidDataException
            MessageBox.Show(Me, "Saved provisional dimensions are invalid." & Environment.NewLine & ex.Message,
                            "Factory Map Dimensions", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub ShowFullScreen()
        Dim mapPanel = _canvas.Parent
        Dim mapGrid = TryCast(mapPanel, TableLayoutPanel)
        If mapPanel Is Nothing Then Return
        mapPanel.Controls.Remove(_canvas)
        Using fullScreen As New Form With {
            .Text = "Factory Map — Digital Twin",
            .FormBorderStyle = FormBorderStyle.None,
            .WindowState = FormWindowState.Maximized,
            .BackColor = UiTheme.ShellDeep,
            .KeyPreview = True
        }
            Dim close As New Button With {.Text = "Esc - Return to page   |   Wheel: zoom   |   WASD: move   |   Q / E: orbit   |   F: focus", .Dock = DockStyle.Top, .Height = 38, .BackColor = UiTheme.ShellDeep, .ForeColor = Color.White, .FlatStyle = FlatStyle.Flat}
            close.FlatAppearance.BorderSize = 0
            AddHandler close.Click, Sub(sender, e) fullScreen.Close()
            fullScreen.Controls.Add(_canvas)
            fullScreen.Controls.Add(close)
            AddHandler fullScreen.KeyDown, Sub(sender, e)
                                               If e.KeyCode = Keys.Escape Then fullScreen.Close()
                                           End Sub
            Try
                AddHandler fullScreen.Shown, Sub() _canvas.Focus()
                fullScreen.ShowDialog(Me)
            Finally
                fullScreen.Controls.Remove(_canvas)
                If mapGrid IsNot Nothing Then
                    mapGrid.Controls.Add(_canvas, 0, 2)
                Else
                    mapPanel.Controls.Add(_canvas)
                End If
                _canvas.Dock = DockStyle.Fill
                _canvas.BringToFront()
                _canvas.Focus()
            End Try
        End Using
    End Sub
End Class

Partial Friend NotInheritable Class FactoryMapCanvas
    Inherits Control

    Private ReadOnly _assets As List(Of FactoryMapAsset)
    Private Shared ReadOnly ReferenceSize As New Size(1365, 766)
    Private _site As New FactoryMapSiteSettings()
    Private ReadOnly _toolTip As New ToolTip()
    Private _editMenu As ContextMenuStrip
    Private ReadOnly _zoomTimer As New System.Windows.Forms.Timer With {.Interval = 16}
    Private _targetZoom As Single = 1
    Private _zoomAnchor As Point
    Private _zoomStart As Single
    Private ReadOnly _zoomClock As New Diagnostics.Stopwatch()
    Private ReadOnly _visibleLayers As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private _snapshots As IReadOnlyList(Of MeterSnapshot) = Array.Empty(Of MeterSnapshot)()
    Private _upsSnapshots As IReadOnlyDictionary(Of String, UpsSnapshot) = New Dictionary(Of String, UpsSnapshot)()
    Private Shared ReadOnly DefaultDimensions As FactoryMapDimensions = FactoryMapDimensions.Defaults()
    Private _selectedAsset As FactoryMapAsset
    Private ReadOnly _selectedAssets As New HashSet(Of FactoryMapAsset)
    Private ReadOnly _groupDragOrigins As New Dictionary(Of FactoryMapAsset, PointF)
    Private _regionSelecting As Boolean
    Private _regionStart As Point
    Private _regionEnd As Point
    Private _groupDragAnchor As PointF
    Private _zoom As Single = 1.0F
    Private _pan As PointF
    Private _yaw As Single = CSng(Math.PI / 4.0R)
    Private _elevation As Single = CSng(Math.PI / 6.0R)
    Private _fitScale As Single = 1.0F
    Private _viewMode As String = "Isometric"
    Private _draggingAsset As Boolean
    Private _panning As Boolean
    Private _rotating As Boolean
    Private _leftDragging As Boolean
    Private _pointerMoved As Boolean
    Private _lastMouse As Point
    Private _dragOffset As PointF
    Private _dimensions As FactoryMapDimensions
    Private _dimensionWarning As String = String.Empty
    Private _projectedCorners As PointF()
    Private ReadOnly _hitAreas As New Dictionary(Of FactoryMapAsset, Drawing2D.GraphicsPath)()
    Private ReadOnly _assetHitGeometry As New Dictionary(Of FactoryMapAsset, FactoryMapHitGeometry)()
    Private _building As List(Of FactoryBuildingPart)
    Private ReadOnly _buildingHits As New Dictionary(Of FactoryBuildingPart, Drawing2D.GraphicsPath)()
    Private ReadOnly _buildingHitGeometry As New Dictionary(Of FactoryBuildingPart, FactoryMapHitGeometry)()
    Private ReadOnly _handles As New Dictionary(Of String, RectangleF)()
    Private _selectedBuilding As FactoryBuildingPart
    Private _buildingEditMode As Boolean
    Private _editMode As Boolean
    Private _dragBuilding As Boolean
    Private _dragHandle As String = ""
    Private _hoverKey As String = ""
    Private ReadOnly _footprints As New Dictionary(Of String, RectangleF)()
    Private ReadOnly _renderCache As New FactoryMapRenderCache()
    Private _renderViewport As Rectangle
    Private ReadOnly _navigation As FactoryMapNavigationWheel
    Private _sceneImage As Bitmap
    Private _overlayRepaint As Boolean
    Private _scenePaintCount As Integer
    Private _sceneDirty As Boolean = True
    Private ReadOnly _presentationTimer As New System.Windows.Forms.Timer With {.Interval = 16}
    Private _displayedStatuses As String() = Array.Empty(Of String)()
    Private _navigationPointer As Boolean
    Private _snapEnabled As Boolean = True
    Private _showWalls As Boolean = True
    Private _showFloors As Boolean = True
    Private _showOpenings As Boolean = True
    Private _wallOpacity As Integer = 22
    Public ReadOnly Property ActiveLevel As String = "Ground"

    Public Sub SetLevel(level As String)
        If Not FactoryMapBuildingStore.Levels.Contains(level) Then Throw New ArgumentException("Unsupported building level.", NameOf(level))
        If ActiveLevel = level Then Return
        EndPointerGesture()
        _ActiveLevel = level
        SelectAsset(Nothing)
        SelectBuildingPart(Nothing)
        ClearHitAreas()
        UpdateBlinkTimer()
        FitFactory()
    End Sub

    Public Event AssetSelected(asset As FactoryMapAsset)
    Public Event AssetMoved(asset As FactoryMapAsset)
    Public Event BuildingSelected(part As FactoryBuildingPart)
    Public Event BuildingChanged(message As String)
    Public Event EditCommand(command As String)
    Public Event EditCommitted()

    <System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public Property EditMode As Boolean
        Get
            Return _editMode
        End Get
        Set(value As Boolean)
            If Not value Then CompleteEditGesture()
            _editMode = value
            _selectedAssets.Clear()
            If _selectedAsset IsNot Nothing Then _selectedAssets.Add(_selectedAsset)
            ReleaseInactiveEditingResources()
            Invalidate()
        End Set
    End Property
    <System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public Property SnapEnabled As Boolean
        Get
            Return _snapEnabled
        End Get
        Set(value As Boolean)
            _snapEnabled = value
        End Set
    End Property
    <System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public Property BuildingEditMode As Boolean
        Get
            Return _buildingEditMode
        End Get
        Set(value As Boolean)
            If Not value Then
                CompleteEditGesture()
                _drawingTool = "Select"
            End If
            _buildingEditMode = value
            If value Then EditMode = False
            _handles.Clear()
            ReleaseInactiveEditingResources()
            Invalidate()
        End Set
    End Property
    <System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public Property ShowFloors As Boolean
        Get
            Return _showFloors
        End Get
        Set(value As Boolean)
            _showFloors = value
            Invalidate()
        End Set
    End Property
    <System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public Property ShowOpenings As Boolean
        Get
            Return _showOpenings
        End Get
        Set(value As Boolean)
            _showOpenings = value
            Invalidate()
        End Set
    End Property
    <System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public Property ShowWalls As Boolean
        Get
            Return _showWalls
        End Get
        Set(value As Boolean)
            _showWalls = value
            Invalidate()
        End Set
    End Property
    <System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)>
    Public Property WallOpacity As Integer
        Get
            Return _wallOpacity
        End Get
        Set(value As Integer)
            If value < 5 OrElse value > 60 Then Throw New ArgumentOutOfRangeException(NameOf(value), "Wall opacity must be 5-60 percent.")
            _wallOpacity = value
            Invalidate()
        End Set
    End Property
    Public ReadOnly Property SelectedAsset As FactoryMapAsset
        Get
            Return _selectedAsset
        End Get
    End Property
    Public ReadOnly Property SelectedAssets As IReadOnlyCollection(Of FactoryMapAsset)
        Get
            Return _selectedAssets.ToArray()
        End Get
    End Property
    Public ReadOnly Property SelectedBuildingPart As FactoryBuildingPart
        Get
            Return _selectedBuilding
        End Get
    End Property

    Public Sub SetBuildingParts(parts As List(Of FactoryBuildingPart))
        FactoryMapBuildingStore.Validate(parts)
        _building = parts
        SelectBuildingPart(Nothing)
    End Sub

    Public Sub SelectBuildingPart(part As FactoryBuildingPart)
        _selectedBuilding = part
        If part IsNot Nothing Then
            _selectedAsset = Nothing
            _selectedAssets.Clear()
        End If
        _handles.Clear()
        RaiseEvent BuildingSelected(part)
        Invalidate()
    End Sub

    Public Sub SelectAsset(asset As FactoryMapAsset)
        _selectedAsset = asset
        _selectedAssets.Clear()
        If asset IsNot Nothing Then _selectedAssets.Add(asset)
        _handles.Clear()
        RaiseEvent AssetSelected(asset)
        Invalidate()
    End Sub

    Friend Sub SelectAssetGroup(items As IEnumerable(Of FactoryMapAsset))
        _selectedAssets.Clear()
        For Each item In items
            If _assets.Contains(item) AndAlso item.IsPlaced AndAlso item.Level = ActiveLevel AndAlso _visibleLayers.Contains(item.Category) Then
                _selectedAssets.Add(item)
            End If
        Next
        _selectedAsset = _assets.FirstOrDefault(Function(item) _selectedAssets.Contains(item))
        _handles.Clear()
        RaiseEvent AssetSelected(_selectedAsset)
        Invalidate()
    End Sub

    Public Sub New(assets As List(Of FactoryMapAsset))
        If assets Is Nothing Then Throw New ArgumentNullException(NameOf(assets))
        _assets = assets
        _dimensions = FactoryMapDimensions.Defaults()
        Try
            _dimensions = FactoryMapDimensionStore.Load()
        Catch ex As IOException
            _dimensionWarning = ex.Message
        Catch ex As JsonException
            _dimensionWarning = ex.Message
        Catch ex As InvalidDataException
            _dimensionWarning = ex.Message
        End Try
        _building = FactoryMapBuildingStore.Defaults(_dimensions)
        For Each category In {"Generators", "Transformers", "UPS", "Battery Rooms", "Compressors", "Chillers", "Production Lines",
            "Water Systems", "Filler Room", "HVAC", "Electrical Panels", "Motors & Drives", "Pumps & Tanks", "Sensors & Controls", "Utilities", "CCTV", "Access Control", "Fire System", "Factory Plan / Rooms / Walls"}
            _visibleLayers.Add(category)
        Next
        _visibleLayers.Add("Roads")
        Try
            _site = FactoryMapSiteStore.Load()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is UnauthorizedAccessException
            _dimensionWarning &= " Site settings unavailable; original ground displayed. " & ex.Message
        End Try
        SetStyle(ControlStyles.UserPaint Or ControlStyles.AllPaintingInWmPaint Or ControlStyles.OptimizedDoubleBuffer Or ControlStyles.ResizeRedraw, True)
        SetStyle(ControlStyles.Selectable, True)
        TabStop = True
        BackColor = Color.White
        Cursor = Cursors.Hand
        _toolTip.AutoPopDelay = 12000
        AddHandler _zoomTimer.Tick, Sub() AdvanceZoom()
        AddHandler _presentationTimer.Tick, AddressOf PresentPendingFrame
        _navigation = New FactoryMapNavigationWheel(Me, AddressOf NavigateView, AddressOf NavigateDrag)
        InitializeMonitoring()
    End Sub

    Private Sub ShowEditMenu(location As Point)
        If _editMenu Is Nothing Then
            _editMenu = New ContextMenuStrip()
            For Each entry In {("Undo", "Ctrl+Z"), ("Redo", "Ctrl+Y"), ("Properties", "Enter / F2"), ("Cut", "Ctrl+X"), ("Copy", "Ctrl+C"), ("Paste", "Ctrl+V"), ("Duplicate", "Ctrl+D"), ("Delete", "Delete")}
                Dim action = entry.Item1
                Dim menuItem As New ToolStripMenuItem(action) With {.ShortcutKeyDisplayString = entry.Item2}
                AddHandler menuItem.Click, Sub() RaiseEvent EditCommand(action)
                _editMenu.Items.Add(menuItem)
            Next
        End If
        _editMenu.Show(Me, location)
    End Sub

    Private Sub ReleaseInactiveEditingResources()
        If EditMode OrElse BuildingEditMode Then Return
        If _editMenu IsNot Nothing Then _editMenu.Dispose()
        _editMenu = Nothing
        _footprints.Clear()
    End Sub

    Private Sub NavigateView(command As String)
        _zoomTimer.Stop()
        Select Case command
            Case "Top" : SetTopView()
            Case "Fit" : ResetView()
            Case Else
                _yaw = CSng(If(command = "North", 0, If(command = "East", Math.PI / 2, If(command = "South", Math.PI, -Math.PI / 2))))
                _elevation = CSng(Math.PI / 6)
                _viewMode = command
                Invalidate()
        End Select
    End Sub

    Private Sub NavigateDrag(mode As String, dx As Single, dy As Single)
        If mode = "Pan" Then
            _pan = New PointF(_pan.X + dx, _pan.Y + dy)
        Else
            _yaw += dx * 0.0045F
            _elevation = Math.Clamp(_elevation + dy * 0.0035F, CSng(Math.PI / 12), CSng(Math.PI / 2))
            _viewMode = "Custom"
        End If
        Invalidate()
    End Sub

    Public Sub SetSnapshots(snapshots As IReadOnlyList(Of MeterSnapshot))
        _snapshots = If(snapshots, Array.Empty(Of MeterSnapshot)())
        RefreshDisplayedStatuses()
    End Sub

    Public Sub SetUpsSnapshots(snapshots As IReadOnlyDictionary(Of String, UpsSnapshot))
        _upsSnapshots = snapshots
        RefreshDisplayedStatuses()
    End Sub

    Private Sub RefreshDisplayedStatuses()
        Dim statuses = _assets.Select(Function(asset) StatusText(asset)).ToArray()
        If _displayedStatuses.SequenceEqual(statuses) Then Return
        _displayedStatuses = statuses
        Invalidate()
    End Sub

    Public Sub SetPageActive(active As Boolean)
        _monitorPageActive = active
        UpdateBlinkTimer()
        If active Then Return
        _presentationTimer.Stop()
        _zoomTimer.Stop()
        _zoomClock.Stop()
        _navigation.Suspend()
    End Sub

    Public Sub SetLayerVisible(category As String, visible As Boolean)
        If visible Then
            _visibleLayers.Add(category)
        Else
            _visibleLayers.Remove(category)
            If _selectedAssets.Any(Function(item) item.Category = category) Then
                CompleteEditGesture()
                SelectAsset(Nothing)
            End If
        End If
        Invalidate()
    End Sub

    Public Sub SetDimensions(dimensions As FactoryMapDimensions)
        If dimensions Is Nothing Then Throw New ArgumentNullException(NameOf(dimensions))
        _dimensions = dimensions
        Invalidate()
    End Sub

    Public Sub ZoomBy(factor As Single)
        _zoomTimer.Stop()
        ZoomAt(factor, New Point(ClientSize.Width \ 2, ClientSize.Height \ 2))
    End Sub

    Public Sub FocusSelection()
        _zoomTimer.Stop()
        Dim position As PointF
        Dim hit As Drawing2D.GraphicsPath = Nothing
        If BuildingEditMode AndAlso _selectedBuilding IsNot Nothing Then
            position = FactoryMapBuildingStore.Resolve(_selectedBuilding, _building).Position
            _buildingHits.TryGetValue(_selectedBuilding, hit)
        ElseIf _selectedAsset IsNot Nothing AndAlso _selectedAsset.IsPlaced Then
            position = _selectedAsset.Position
            _hitAreas.TryGetValue(_selectedAsset, hit)
        Else
            FitFactory()
            Return
        End If
        If hit IsNot Nothing Then
            Dim box = hit.GetBounds()
            Dim factor = Math.Min(ClientSize.Width * 0.65F / Math.Max(1, box.Width), ClientSize.Height * 0.65F / Math.Max(1, box.Height))
            _zoom = Math.Clamp(_zoom * factor, 0.2F, 40)
        Else
            _zoom = Math.Max(4, _zoom)
        End If
        Dim bounds = GetProjectedBounds()
        Dim scale = Math.Min(Math.Max(1, ClientSize.Width - 32) / bounds.Width, Math.Max(1, ClientSize.Height - 32) / bounds.Height) * _zoom
        Dim point = ProjectMapPoint(position.X * 1365, position.Y * 766)
        _pan = New PointF((bounds.Left + bounds.Width / 2 - point.X) * scale, (bounds.Top + bounds.Height / 2 - point.Y) * scale)
        Invalidate()
        Focus()
    End Sub

    Public Sub SetSite(settings As FactoryMapSiteSettings)
        settings.Validate()
        _site = settings
        FitFactory()
    End Sub

    Private Sub ZoomAt(factor As Single, point As Point)
        If Not Single.IsFinite(factor) OrElse factor <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(factor))
        Dim oldZoom = _zoom
        _zoom = Math.Clamp(_zoom * factor, 0.2F, 40.0F)
        Dim ratio = _zoom / oldZoom
        _pan = New PointF((point.X - ClientSize.Width / 2.0F) * (1 - ratio) + _pan.X * ratio,
                          (point.Y - ClientSize.Height / 2.0F) * (1 - ratio) + _pan.Y * ratio)
        Invalidate()
    End Sub

    Public Sub ResetView()
        _zoomTimer.Stop()
        _zoom = 1.0F
        _pan = PointF.Empty
        SetIsometricView()
    End Sub

    Public Sub SetTopView()
        _zoomTimer.Stop()
        _viewMode = "Top 3D"
        _yaw = 0.0F
        _elevation = CSng(70 * Math.PI / 180)
        _zoom = 1.0F
        _pan = PointF.Empty
        Invalidate()
    End Sub

    Public Sub SetPlanView()
        SetTopView()
        _viewMode = "Plan"
        _elevation = CSng(Math.PI / 2)
        Invalidate()
    End Sub

    Public Sub SetIsometricView()
        _zoomTimer.Stop()
        _viewMode = "Isometric"
        _yaw = CSng(Math.PI / 4.0R)
        _elevation = CSng(Math.PI / 6.0R)
        _zoom = 1.0F
        _pan = PointF.Empty
        Invalidate()
    End Sub

    Public Sub FitFactory()
        _zoomTimer.Stop()
        _zoom = 1.0F
        _pan = PointF.Empty
        Invalidate()
    End Sub

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        MyBase.OnPaint(e)
        If ClientSize.Width <= 0 OrElse ClientSize.Height <= 0 Then Return
        If _sceneImage Is Nothing OrElse _sceneImage.Size <> ClientSize Then
            If _sceneImage IsNot Nothing Then _sceneImage.Dispose()
            _sceneImage = New Bitmap(ClientSize.Width, ClientSize.Height, Imaging.PixelFormat.Format32bppPArgb)
            _overlayRepaint = False
        End If
        If Not _overlayRepaint Then
            Using graphics = Drawing.Graphics.FromImage(_sceneImage)
                graphics.Clear(UiTheme.SurfaceMuted)
                _renderViewport = ClientRectangle
                PaintScene(graphics)
            End Using
            _scenePaintCount += 1
            _sceneDirty = False
        End If
        _overlayRepaint = False
        e.Graphics.DrawImageUnscaled(_sceneImage, Point.Empty)
        e.Graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias
        DrawMapOverlay(e.Graphics)
        If _regionSelecting Then
            Dim region = SelectionRegion()
            Using fill As New SolidBrush(Color.FromArgb(35, UiTheme.Blue)), outline As New Pen(UiTheme.Blue, 1.5F)
                outline.DashStyle = Drawing2D.DashStyle.Dash
                e.Graphics.FillRectangle(fill, region)
                e.Graphics.DrawRectangle(outline, region.X, region.Y, region.Width, region.Height)
            End Using
        End If
    End Sub

    Protected Overrides Sub OnInvalidated(e As InvalidateEventArgs)
        Dim overlayOnly = _alarmOverlayInvalidation OrElse (_navigation IsNot Nothing AndAlso e.InvalidRect = _navigation.Bounds)
        If Not overlayOnly Then _sceneDirty = True
        _overlayRepaint = overlayOnly AndAlso Not _sceneDirty AndAlso _sceneImage IsNot Nothing AndAlso _sceneImage.Size = ClientSize
        MyBase.OnInvalidated(e)
        If _monitorPageActive AndAlso Visible AndAlso IsHandleCreated Then _presentationTimer.Start()
    End Sub

    Private Sub PresentPendingFrame(sender As Object, e As EventArgs)
        _presentationTimer.Stop()
        ' Coalesce input, but do not leave frame delivery waiting indefinitely for an idle WM_PAINT.
        If _monitorPageActive AndAlso Visible AndAlso IsHandleCreated Then Update()
    End Sub

    Private Sub PaintScene(graphics As Graphics)
        Dim e As New PaintEventArgs(graphics, _renderViewport)
        e.Graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias
        _renderCache.SetCamera(_yaw, _elevation)
        SyncConnectors()
        _renderCache.RetainNativeImages = Not _zoomTimer.Enabled AndAlso Not _rotating
        Dim siteSize = GetSiteSize()
        Dim viewCorners = {
            ProjectMapPoint(0.0F, 0.0F),
            ProjectMapPoint(siteSize.Width, 0.0F),
            ProjectMapPoint(0.0F, siteSize.Height),
            ProjectMapPoint(siteSize.Width, siteSize.Height)
        }
        Dim viewBounds = GetProjectedBounds()
        Dim minX = viewBounds.Left
        Dim maxX = viewBounds.Right
        Dim minY = viewBounds.Top
        Dim maxY = viewBounds.Bottom
        If maxX <= minX OrElse maxY <= minY Then Return
        _fitScale = Math.Min(Math.Max(1, ClientSize.Width - 32) / (maxX - minX),
                             Math.Max(1, ClientSize.Height - 32) / (maxY - minY))
        Dim scale = _fitScale * _zoom
        Dim originX = (ClientSize.Width - ((minX + maxX) * scale)) / 2.0F + _pan.X
        Dim originY = (ClientSize.Height - ((minY + maxY) * scale)) / 2.0F + _pan.Y
        Dim screenCorners = viewCorners.Select(Function(point) New PointF(originX + point.X * scale, originY + point.Y * scale)).ToArray()
        _projectedCorners = screenCorners
        Using shadow As New SolidBrush(Color.FromArgb(22, UiTheme.ShellDeep))
            e.Graphics.FillPolygon(shadow, {New PointF(screenCorners(0).X + 3, screenCorners(0).Y + 5),
                New PointF(screenCorners(1).X + 3, screenCorners(1).Y + 5), New PointF(screenCorners(3).X + 3, screenCorners(3).Y + 5),
                New PointF(screenCorners(2).X + 3, screenCorners(2).Y + 5)})
        End Using
        If _visibleLayers.Contains("Factory Plan / Rooms / Walls") Then
            Using ground As New SolidBrush(ColorTranslator.FromHtml(_site.GroundColor))
                e.Graphics.FillPolygon(ground, {screenCorners(0), screenCorners(1), screenCorners(3), screenCorners(2)})
            End Using
        End If
        ClearHitAreas()
        Dim scene As New List(Of (Depth As Single, Paint As Action))
        Dim yawCosine = CSng(Math.Cos(_yaw)), yawSine = CSng(Math.Sin(_yaw))
        Dim elevationSine = CSng(Math.Sin(_elevation)), elevationCosine = CSng(Math.Cos(_elevation))
        Dim project As Func(Of Single, Single, Single, PointF) =
            Function(x, y, z)
                Dim px = x - ReferenceSize.Width / 2, py = y - ReferenceSize.Height / 2
                Return New PointF(originX + (px * yawCosine - py * yawSine) * scale,
                    originY + ((px * yawSine + py * yawCosine) * elevationSine - z * elevationCosine) * scale)
            End Function
        Dim visibleBuilding = _building.Where(Function(item) item.Level = ActiveLevel).ToArray()
        For Each part In visibleBuilding.Where(Function(item) item.Kind = "Road" AndAlso _visibleLayers.Contains("Roads"))
            DrawBuilding(e.Graphics, part, project)
        Next
        ' Larger floors first so a big floor's background never covers smaller floors drawn inside or beside it.
        For Each part In visibleBuilding.Where(Function(item) item.Kind = "Floor" AndAlso ShowFloors).
                OrderByDescending(Function(item) item.Length * item.Depth)
            DrawBuilding(e.Graphics, part, project)
        Next
        For Each part In visibleBuilding.Where(Function(item) item.Kind = "Marking" AndAlso ShowFloors)
            DrawBuilding(e.Graphics, part, project)
        Next
        For Each part In visibleBuilding.Where(Function(item) (item.Kind = "Wall" AndAlso ShowWalls) OrElse
            (item.Kind <> "Floor" AndAlso item.Kind <> "Marking" AndAlso item.Kind <> "Road" AndAlso item.Kind <> "Wall" AndAlso ShowOpenings))
            Dim resolved = FactoryMapBuildingStore.Resolve(part, _building)
            Dim depth = ProjectMapPoint(resolved.X * 1365, resolved.Y * 766).Y
            scene.Add((depth, Sub() DrawBuilding(e.Graphics, part, project)))
        Next
        For Each asset In _assets
            If Not asset.IsPlaced OrElse Not _visibleLayers.Contains(asset.Category) OrElse Not ShouldShowAsset(asset) Then Continue For
            Dim mounted = MountedAsset(asset)
            Dim depth = ProjectMapPoint(mounted.Position.X * 1365, mounted.Position.Y * 766).Y
            scene.Add((depth, Sub() DrawAsset(e.Graphics, asset, originX, originY, scale)))
        Next
        For Each part In scene.OrderBy(Function(item) item.Depth)
            part.Paint()
        Next
        DrawBuildingHandles(e.Graphics, project)
        DrawAssetHandles(e.Graphics, project)
        DrawDrawingPreview(e.Graphics, project)
    End Sub

    Private Sub DrawMapOverlay(graphics As Graphics)
        Dim e As New PaintEventArgs(graphics, ClientRectangle)
        DrawAlarmOverlay(graphics)
        DrawUnplacedChillers(graphics)
        Using font As New Font("Segoe UI", 8.0F), brush As New SolidBrush(UiTheme.MutedInk), background As New SolidBrush(Color.FromArgb(238, UiTheme.Surface))
            Dim drawingBased = _building.Any(Function(part) part.Id.StartsWith("DRAW-", StringComparison.Ordinal))
            Dim caption = ActiveLevel.ToUpperInvariant() & "  |  " & _viewMode.ToUpperInvariant() & "  |  " &
                If(drawingBased, "DRAWING TRACES / SCHEMATIC EQUIPMENT / VERIFY ON SITE", "PROVISIONAL ROOM TRACES / SCHEMATIC EQUIPMENT")
            Dim measured = e.Graphics.MeasureString(caption, font)
            e.Graphics.FillRectangle(background, 6, 5, measured.Width + 12, measured.Height + 7)
            e.Graphics.DrawString(caption, font, brush, 12.0F, 8.0F)
            If Not String.IsNullOrWhiteSpace(_dimensionWarning) Then
                e.Graphics.DrawString("Dimension settings unavailable; displaying defaults. " & _dimensionWarning, font, brush, 8.0F, 22.0F)
            End If
        End Using
        _navigation.Draw(e.Graphics, _yaw)
        DrawConnectorPreview(e.Graphics)
    End Sub

    Protected Overrides Sub OnMouseWheel(e As MouseEventArgs)
        MyBase.OnMouseWheel(e)
        If _draggingAsset OrElse _dragBuilding Then Return
        If (ModifierKeys And Keys.Shift) = Keys.Shift Then
            _zoomTimer.Stop()
            _pan.X += e.Delta / 3.0F
            Invalidate()
            Return
        End If
        _targetZoom = Math.Clamp(If(_zoomTimer.Enabled, _targetZoom, _zoom) * CSng(Math.Pow(1.16, e.Delta / 120.0)), 0.2F, 40)
        _zoomAnchor = e.Location
        _zoomStart = _zoom
        _zoomClock.Restart()
        _zoomTimer.Start()
    End Sub

    Private Sub AdvanceZoom()
        Dim progress = Math.Min(1, _zoomClock.Elapsed.TotalMilliseconds / 140)
        Dim eased = 1 - (1 - progress) ^ 3
        Dim nextZoom = CSng(_zoomStart + (_targetZoom - _zoomStart) * eased)
        If progress >= 1 Then
            _zoomTimer.Stop()
            _zoomClock.Stop()
        End If
        ZoomAt(nextZoom / _zoom, _zoomAnchor)
    End Sub

    Protected Overrides Sub OnMouseDoubleClick(e As MouseEventArgs)
        MyBase.OnMouseDoubleClick(e)
        If e.Button <> MouseButtons.Left Then Return
        If _navigation.Hit(e.Location) <> "" Then Return
        EndPointerGesture()
        Capture = False
        If (EditMode AndAlso _selectedAsset IsNot Nothing) OrElse (BuildingEditMode AndAlso _selectedBuilding IsNot Nothing) Then
            RaiseEvent EditCommand("Properties")
        Else
            _zoomTimer.Stop()
            ZoomAt(1.5F, e.Location)
        End If
    End Sub

    Protected Overrides Sub OnMouseDown(e As MouseEventArgs)
        MyBase.OnMouseDown(e)
        _zoomTimer.Stop()
        Focus()
        _lastMouse = e.Location
        _pointerMoved = False
        If e.Button <> MouseButtons.None Then Capture = True
        _toolTip.SetToolTip(Me, String.Empty)
        _hoverKey = ""
        If _navigation.PointerDown(e) Then
            _navigationPointer = True
            Return
        End If
        If e.Button = MouseButtons.Middle Then
            _panning = True
            Cursor = Cursors.SizeAll
            Return
        End If
        If e.Button = MouseButtons.Right Then
            _rotating = True
            Cursor = Cursors.Hand
            Return
        End If
        If e.Button = MouseButtons.Left Then
            If EditMode AndAlso (ModifierKeys And Keys.Control) = Keys.Control Then
                SelectAsset(Nothing)
                _regionSelecting = True
                _regionStart = e.Location
                _regionEnd = e.Location
                Cursor = Cursors.Cross
                Return
            End If
            If EditMode AndAlso _connectorTool <> "" AndAlso (ModifierKeys And Keys.Shift) <> Keys.Shift Then
                ConnectorClick(e.Location)
                Return
            End If
            If BuildingEditMode AndAlso _drawingTool <> "Select" Then
                BeginDrawing(e.Location)
                Return
            End If
            If (ModifierKeys And Keys.Shift) = Keys.Shift Then
                _leftDragging = True
                _rotating = True
            ElseIf BuildingEditMode Then
                _dragHandle = _handles.FirstOrDefault(Function(entry) entry.Value.Contains(e.Location)).Key
                If String.IsNullOrEmpty(_dragHandle) Then SelectBuildingPart(FindBuildingAt(e.Location))
                _dragBuilding = _selectedBuilding IsNot Nothing
                If _dragBuilding Then
                    Dim resolved = FactoryMapBuildingStore.Resolve(_selectedBuilding, _building)
                    Dim anchor = ScreenToMapAtHeight(e.Location, resolved.BaseMeters)
                    _dragOffset = New PointF(resolved.X - anchor.X, resolved.Y - anchor.Y)
                    BeginHandleDrag(e.Location, resolved.BaseMeters)
                Else
                    _leftDragging = True
                End If
            ElseIf EditMode Then
                _dragHandle = _handles.FirstOrDefault(Function(entry) entry.Value.Contains(e.Location)).Key
                If FactoryMapConnectors.IsConnector(_selectedAsset) Then _dragHandle = ""
                If String.IsNullOrEmpty(_dragHandle) Then
                    Dim clicked = FindAssetAt(e.Location)
                    If clicked Is Nothing Then
                        _leftDragging = True
                        Cursor = Cursors.SizeAll
                        Return
                    End If
                    If Not _selectedAssets.Contains(clicked) Then SelectAsset(clicked)
                    _selectedAsset = clicked
                End If
                _draggingAsset = _selectedAsset IsNot Nothing AndAlso Not FactoryMapConnectors.IsConnector(_selectedAsset)
                If _draggingAsset Then
                    Dim anchor = ScreenToMapAtHeight(e.Location, MountHeight(_selectedAsset))
                    _dragOffset = New PointF(_selectedAsset.Position.X - anchor.X, _selectedAsset.Position.Y - anchor.Y)
                    _groupDragAnchor = anchor
                    _groupDragOrigins.Clear()
                    For Each item In _selectedAssets
                        _groupDragOrigins.Add(item, item.Position)
                    Next
                    BeginHandleDrag(e.Location, MountHeight(_selectedAsset))
                Else
                    _leftDragging = True
                End If
                RaiseEvent AssetSelected(_selectedAsset)
                Invalidate()
            Else
                _leftDragging = True
                _rotating = (ModifierKeys And Keys.Shift) = Keys.Shift
            End If
            If _rotating OrElse _draggingAsset OrElse _dragBuilding Then Cursor = Cursors.Hand
        End If
    End Sub

    Protected Overrides Sub OnMouseMove(e As MouseEventArgs)
        MyBase.OnMouseMove(e)
        ConnectorMove(e.Location)
        If _regionSelecting Then
            _regionEnd = e.Location
            Cursor = Cursors.Cross
            InvalidateAlarmOverlay()
            Return
        End If
        If _drawingActive Then
            UpdateDrawing(e.Location)
            Return
        End If
        Dim overNavigation = _navigation.PointerMove(e)
        If _navigation.Active Then Return
        If overNavigation AndAlso Not (_panning OrElse _leftDragging OrElse _rotating OrElse _dragBuilding OrElse _draggingAsset) Then
            _toolTip.SetToolTip(Me, String.Empty)
            _hoverKey = ""
            Cursor = Cursors.Hand
            Return
        End If
        If _leftDragging AndAlso (ModifierKeys And Keys.Shift) = Keys.Shift Then _rotating = True
        If e.Location <> _lastMouse Then _pointerMoved = True
        If _panning Then
            _pan = New PointF(_pan.X + e.X - _lastMouse.X, _pan.Y + e.Y - _lastMouse.Y)
            _lastMouse = e.Location
            Invalidate()
            Return
        End If
        If (_rotating AndAlso (e.Button = MouseButtons.Right OrElse _leftDragging)) Then
            _yaw += CSng((e.X - _lastMouse.X) * 0.0045R)
            _elevation = Math.Max(CSng(Math.PI / 12.0R), Math.Min(CSng(Math.PI / 2.0R),
                _elevation + CSng((e.Y - _lastMouse.Y) * 0.0035R)))
            _viewMode = "Custom"
            _lastMouse = e.Location
            Invalidate()
            Return
        End If
        If _leftDragging AndAlso Not _rotating Then
            _pan = New PointF(_pan.X + e.X - _lastMouse.X, _pan.Y + e.Y - _lastMouse.Y)
            _lastMouse = e.Location
            Invalidate()
            Return
        End If
        If _dragBuilding AndAlso _selectedBuilding IsNot Nothing Then
            MoveBuilding(e.Location)
            Return
        End If
        If _draggingAsset AndAlso _selectedAsset IsNot Nothing AndAlso _fitScale > 0 Then
            If Not String.IsNullOrEmpty(_dragHandle) Then
                TransformAsset(e.Location)
                Return
            End If
            Dim anchor = ScreenToMapAtHeight(e.Location, MountHeight(_selectedAsset))
            If _groupDragOrigins.Count > 1 Then
                MoveEquipmentGroup(_groupDragOrigins, New PointF(anchor.X - _groupDragAnchor.X, anchor.Y - _groupDragAnchor.Y), True)
                Return
            End If
            Dim position = New PointF(
                Math.Max(0.0F, Math.Min(4.0F, anchor.X + _dragOffset.X)),
                Math.Max(0.0F, Math.Min(4.0F, anchor.Y + _dragOffset.Y)))
            _selectedAsset.Position = SnapAsset(_selectedAsset, position)
            RaiseEvent AssetMoved(_selectedAsset)
            Invalidate()
            Return
        End If
        Dim asset = If(BuildingEditMode, Nothing, FindAssetAt(e.Location))
        Dim building = If(asset Is Nothing, FindBuildingAt(e.Location), Nothing)
        Dim key = If(asset IsNot Nothing, "A:" & asset.Id, If(building IsNot Nothing, "B:" & building.Id, ""))
        If key <> _hoverKey Then
            _hoverKey = key
            _toolTip.SetToolTip(Me, If(asset IsNot Nothing, asset.Name & " | " & asset.Model & " | " & asset.Id,
                If(building IsNot Nothing, building.ToString() & " / " & building.Material, String.Empty)))
        End If
        Cursor = If((BuildingEditMode OrElse EditMode) AndAlso _handles.Any(Function(entry) entry.Value.Contains(e.Location)), Cursors.Cross,
                    If((EditMode AndAlso asset IsNot Nothing) OrElse (BuildingEditMode AndAlso building IsNot Nothing), Cursors.SizeAll, Cursors.Hand))
        If BuildingEditMode AndAlso _drawingTool <> "Select" Then Cursor = Cursors.Cross
    End Sub

    Private Function ScreenToMap(point As Point) As PointF
        Dim bounds = GetProjectedBounds()
        Dim scale = Math.Min(Math.Max(1, ClientSize.Width - 32) / bounds.Width,
                             Math.Max(1, ClientSize.Height - 32) / bounds.Height) * _zoom
        Dim originX = (ClientSize.Width - (bounds.Left + bounds.Right) * scale) / 2.0F + _pan.X
        Dim originY = (ClientSize.Height - (bounds.Top + bounds.Bottom) * scale) / 2.0F + _pan.Y
        Dim localX = (point.X - originX) / scale
        Dim localY = (point.Y - originY) / scale
        Dim sinElevation = Math.Max(0.01F, CSng(Math.Sin(_elevation)))
        Dim cosine = CSng(Math.Cos(_yaw))
        Dim sine = CSng(Math.Sin(_yaw))
        Return New PointF((localX * cosine + localY * sine / sinElevation + ReferenceSize.Width / 2.0F) / ReferenceSize.Width,
                          (-localX * sine + localY * cosine / sinElevation + ReferenceSize.Height / 2.0F) / ReferenceSize.Height)
    End Function

    Protected Overrides Sub OnMouseUp(e As MouseEventArgs)
        MyBase.OnMouseUp(e)
        If _regionSelecting AndAlso e.Button = MouseButtons.Left Then
            _regionEnd = e.Location
            Dim region = SelectionRegion()
            Dim items As New List(Of FactoryMapAsset)
            If region.Width >= 4 AndAlso region.Height >= 4 Then
                For Each entry In _hitAreas
                    If _assets.Contains(entry.Key) AndAlso entry.Key.IsPlaced AndAlso entry.Key.Level = ActiveLevel AndAlso
                        ShouldShowAsset(entry.Key) AndAlso _visibleLayers.Contains(entry.Key.Category) AndAlso
                        region.Contains(entry.Value.GetBounds()) Then items.Add(entry.Key)
                Next
            End If
            SelectAssetGroup(items)
        End If
        If _drawingActive AndAlso e.Button = MouseButtons.Left Then
            FinishDrawing(e.Location)
        End If
        If e.Button = MouseButtons.Left AndAlso ConnectorDragEnds(e.Location) Then
            Capture = False
            Return
        End If
        If _navigationPointer Then
            EndPointerGesture()
            Capture = False
            Return
        End If
        If e.Button = MouseButtons.Right AndAlso Not _pointerMoved AndAlso (EditMode OrElse BuildingEditMode) Then
            If BuildingEditMode Then
                SelectBuildingPart(FindBuildingAt(e.Location))
            Else
                SelectAsset(FindAssetAt(e.Location))
            End If
            ShowEditMenu(e.Location)
        End If
        If _leftDragging AndAlso Not _pointerMoved AndAlso Not EditMode AndAlso Not BuildingEditMode Then
            _selectedAsset = FindAssetAt(e.Location)
            _selectedBuilding = If(_selectedAsset Is Nothing, FindBuildingAt(e.Location), Nothing)
            RaiseEvent AssetSelected(_selectedAsset)
            If _selectedBuilding IsNot Nothing Then RaiseEvent BuildingSelected(_selectedBuilding)
            Invalidate()
        End If
        EndPointerGesture()
        Capture = False
    End Sub

    Protected Overrides Sub OnMouseCaptureChanged(e As EventArgs)
        MyBase.OnMouseCaptureChanged(e)
        If Not Capture Then EndPointerGesture()
    End Sub

    Private Sub EndPointerGesture()
        Dim commit = _dragBuilding OrElse _draggingAsset
        Dim wasMoving = _regionSelecting OrElse _panning OrElse _rotating OrElse _leftDragging OrElse _dragBuilding OrElse _draggingAsset OrElse _navigation.Active OrElse _drawingActive
        _regionSelecting = False
        _groupDragOrigins.Clear()
        _drawingActive = False
        _navigationPointer = False
        _navigation.Release()
        _panning = False
        _draggingAsset = False
        _dragBuilding = False
        _dragHandle = ""
        _rotating = False
        _leftDragging = False
        _pointerMoved = False
        Cursor = Cursors.Hand
        If wasMoving Then Invalidate()
        If commit Then RaiseEvent EditCommitted()
    End Sub

    Protected Overrides Sub OnMouseLeave(e As EventArgs)
        MyBase.OnMouseLeave(e)
        _navigation.Leave()
        If Capture Then Return
        _hoverKey = ""
        _toolTip.SetToolTip(Me, String.Empty)
    End Sub

    Protected Overrides Function IsInputKey(keyData As Keys) As Boolean
        Select Case keyData And Keys.KeyCode
            Case Keys.Left, Keys.Right, Keys.Up, Keys.Down, Keys.Enter, Keys.Delete : Return True
        End Select
        Return MyBase.IsInputKey(keyData)
    End Function

    Protected Overrides Sub OnKeyDown(e As KeyEventArgs)
        MyBase.OnKeyDown(e)
        If EditMode OrElse BuildingEditMode Then
            If e.KeyCode = Keys.Escape Then
                If CancelConnectorTool() Then
                    e.SuppressKeyPress = True
                    Return
                End If
                _drawingActive = False
                CompleteEditGesture()
                If EditMode Then SelectAsset(Nothing)
                _handles.Clear()
                Invalidate()
                e.SuppressKeyPress = True
                Return
            End If
            Dim command = EditShortcut(e.KeyData)
            If command = "" Then command = If(e.KeyCode = Keys.Delete, "Delete", If(e.KeyCode = Keys.F2 OrElse e.KeyCode = Keys.Enter, "Properties", ""))
            If command <> "" Then
                RaiseEvent EditCommand(command)
                e.Handled = True
                e.SuppressKeyPress = True
                Return
            End If
        End If
        Select Case e.KeyCode
            Case Keys.Add, Keys.Oemplus : ZoomBy(1.2F)
            Case Keys.Subtract, Keys.OemMinus : ZoomBy(1 / 1.2F)
            Case Keys.Home : FitFactory()
            Case Keys.F : FocusSelection()
            Case Keys.Q, Keys.E
                _yaw += If(e.KeyCode = Keys.Q, -0.08F, 0.08F)
                _viewMode = "Custom"
                Invalidate()
            Case Else
                HandleMovementKey(e)
                Return
        End Select
        e.Handled = True
        e.SuppressKeyPress = True
    End Sub

    Private Sub HandleMovementKey(e As KeyEventArgs)
        _zoomTimer.Stop()
        If {Keys.Left, Keys.Right, Keys.Up, Keys.Down}.Contains(e.KeyCode) AndAlso
            ((EditMode AndAlso _selectedAsset IsNot Nothing) OrElse (BuildingEditMode AndAlso _selectedBuilding IsNot Nothing)) AndAlso Not e.Control Then
            Dim amount = If(e.Shift, 5.0F, 1.0F)
            Dim delta As New PointF(If(e.KeyCode = Keys.Left, -amount, If(e.KeyCode = Keys.Right, amount, 0)),
                                    If(e.KeyCode = Keys.Up, -amount, If(e.KeyCode = Keys.Down, amount, 0)))
            NudgeSelected(delta)
            RaiseEvent EditCommitted()
            e.Handled = True
            e.SuppressKeyPress = True
            Return
        End If
        Dim stepSize = If(e.Shift, 90.0F, 30.0F)
        Select Case e.KeyCode
            Case Keys.Left, Keys.A : _pan.X += stepSize
            Case Keys.Right, Keys.D : _pan.X -= stepSize
            Case Keys.Up, Keys.W : _pan.Y += stepSize
            Case Keys.Down, Keys.S : _pan.Y -= stepSize
            Case Else : Return
        End Select
        e.Handled = True
        e.SuppressKeyPress = True
        Invalidate()
    End Sub

    Private Function SelectionRegion() As RectangleF
        Return RectangleF.FromLTRB(Math.Min(_regionStart.X, _regionEnd.X), Math.Min(_regionStart.Y, _regionEnd.Y),
            Math.Max(_regionStart.X, _regionEnd.X), Math.Max(_regionStart.Y, _regionEnd.Y))
    End Function

    Private Sub MoveEquipmentGroup(origins As Dictionary(Of FactoryMapAsset, PointF), delta As PointF, snap As Boolean)
        Dim minX = origins.Values.Min(Function(position) position.X)
        Dim maxX = origins.Values.Max(Function(position) position.X)
        Dim minY = origins.Values.Min(Function(position) position.Y)
        Dim maxY = origins.Values.Max(Function(position) position.Y)
        delta = New PointF(Math.Clamp(delta.X, -minX, 4 - maxX), Math.Clamp(delta.Y, -minY, 4 - maxY))
        If snap AndAlso SnapEnabled AndAlso (ModifierKeys And Keys.Alt) <> Keys.Alt Then
            Dim bounds = origins.Select(Function(entry) AssetBounds(entry.Key,
                New PointF(entry.Value.X + delta.X, entry.Value.Y + delta.Y))).Aggregate(Function(a, b) RectangleF.Union(a, b))
            Dim targets = _assets.Where(Function(item) Not origins.ContainsKey(item) AndAlso item.IsPlaced AndAlso
                item.Level = ActiveLevel AndAlso _visibleLayers.Contains(item.Category)).Select(Function(item) AssetBounds(item, item.Position))
            Dim offset = FactoryMapPlacement.SnapOffset(bounds, targets, SnapTolerance())
            delta = New PointF(Math.Clamp(delta.X + offset.X / 1365, -minX, 4 - maxX),
                Math.Clamp(delta.Y + offset.Y / 766, -minY, 4 - maxY))
        End If
        For Each entry In origins
            entry.Key.Position = New PointF(entry.Value.X + delta.X, entry.Value.Y + delta.Y)
        Next
        RaiseEvent AssetMoved(_selectedAsset)
        Invalidate()
    End Sub

    Private Sub NudgeSelected(delta As PointF)
        If BuildingEditMode AndAlso _selectedBuilding IsNot Nothing Then
            Dim candidate = _selectedBuilding.Clone()
            If candidate.ParentWallId <> "" Then
                Dim wall = _building.Single(Function(part) part.Id = candidate.ParentWallId)
                Dim angle = wall.Rotation * Math.PI / 180
                candidate.Offset += CSng(delta.X * Math.Cos(angle) + delta.Y * Math.Sin(angle)) * If(wall.MirrorHorizontal, -1, 1)
            Else
                candidate.Position = New PointF(Math.Clamp(candidate.X + delta.X / 1365, 0, 4), Math.Clamp(candidate.Y + delta.Y / 766, 0, 4))
            End If
            ApplyBuildingCandidate(candidate)
        ElseIf EditMode AndAlso _selectedAsset IsNot Nothing Then
            If _selectedAssets.Count > 1 Then
                MoveEquipmentGroup(_selectedAssets.ToDictionary(Function(item) item, Function(item) item.Position),
                    New PointF(delta.X / 1365, delta.Y / 766), False)
                Return
            End If
            _selectedAsset.Position = New PointF(Math.Clamp(_selectedAsset.Position.X + delta.X / 1365, 0, 4),
                                                Math.Clamp(_selectedAsset.Position.Y + delta.Y / 766, 0, 4))
            RaiseEvent AssetMoved(_selectedAsset)
            Invalidate()
        End If
    End Sub

    Private Function AssetBounds(asset As FactoryMapAsset, position As PointF) As RectangleF
        Dim component = _dimensions.Components.FirstOrDefault(Function(item) item.Name = DimensionName(asset.Category))
        Dim defaults = DefaultDimensions.Components.FirstOrDefault(Function(item) item.Name = DimensionName(asset.Category))
        Dim effective = asset.Clone()
        If component IsNot Nothing AndAlso defaults IsNot Nothing Then
            effective.ScaleX *= CSng(component.LengthMeters / defaults.LengthMeters)
            effective.ScaleY *= CSng(component.WidthMeters / defaults.WidthMeters)
        End If
        Dim key = String.Join("|", {effective.Model, effective.Category, effective.Rotation.ToString(CultureInfo.InvariantCulture),
            effective.MirrorHorizontal.ToString(), effective.MirrorVertical.ToString(),
            effective.ScaleX.ToString(CultureInfo.InvariantCulture), effective.ScaleY.ToString(CultureInfo.InvariantCulture),
            effective.ConveyorLength.ToString(CultureInfo.InvariantCulture), effective.IsOpen.ToString(), effective.OpeningDirection})
        If effective.CustomEquipment IsNot Nothing Then
            key &= "|" & effective.CustomEquipment.LengthMeters.ToString(CultureInfo.InvariantCulture) & "|" &
                effective.CustomEquipment.WidthMeters.ToString(CultureInfo.InvariantCulture)
        End If
        Dim bounds As RectangleF
        If Not _footprints.TryGetValue(key, bounds) Then
            bounds = FactoryMapPlacement.AssetFootprint(effective)
            If _footprints.Count > 500 Then _footprints.Clear()
            _footprints.Add(key, bounds)
        End If
        bounds.Offset(position.X * 1365, position.Y * 766)
        Return bounds
    End Function

    Private Function SnapTolerance() As Single
        Return Math.Min(10, 7 / Math.Max(0.1F, _fitScale * _zoom))
    End Function

    Private Function SnapAsset(asset As FactoryMapAsset, position As PointF) As PointF
        If Not SnapEnabled OrElse (ModifierKeys And Keys.Alt) = Keys.Alt Then Return position
        Dim moving = AssetBounds(asset, position)
        Dim targets = _assets.Where(Function(other) other IsNot asset AndAlso other.Level = asset.Level AndAlso other.IsPlaced AndAlso _visibleLayers.Contains(other.Category)).
            Select(Function(other) AssetBounds(other, other.Position))
        Dim delta = FactoryMapPlacement.SnapOffset(moving, targets, SnapTolerance())
        Return New PointF(Math.Clamp(position.X + delta.X / 1365, 0, 4), Math.Clamp(position.Y + delta.Y / 766, 0, 4))
    End Function

    Private Sub SnapBuilding(candidate As FactoryBuildingPart)
        If Not SnapEnabled OrElse (ModifierKeys And Keys.Alt) = Keys.Alt OrElse candidate.ParentWallId <> "" Then Return
        Dim tolerance = SnapTolerance()
        If {"Door", "Gate", "Window"}.Contains(candidate.Kind) Then
            FactoryMapPlacement.AttachOpening(candidate, _building, tolerance)
            Return
        End If
        If candidate.Kind = "Wall" Then
            Dim best = tolerance
            Dim delta As PointF
            For Each other In _building.Where(Function(part) part.Level = candidate.Level AndAlso part.Kind = "Wall" AndAlso part.Id <> candidate.Id)
                Dim start = FactoryMapBuildingStore.WorldPoint(other, -other.Length / 2, 0)
                Dim finish = FactoryMapBuildingStore.WorldPoint(other, other.Length / 2, 0)
                For Each endX In {-candidate.Length / 2, candidate.Length / 2}
                    Dim endpoint = FactoryMapBuildingStore.WorldPoint(candidate, endX, 0)
                    Dim dx = finish.X - start.X
                    Dim dy = finish.Y - start.Y
                    Dim t = Math.Clamp(((endpoint.X - start.X) * dx + (endpoint.Y - start.Y) * dy) / (dx * dx + dy * dy), 0, 1)
                    Dim nearest As New PointF(start.X + t * dx, start.Y + t * dy)
                    For Each target In {start, finish, nearest}
                        Dim distance = CSng(Math.Sqrt((endpoint.X - target.X) ^ 2 + (endpoint.Y - target.Y) ^ 2))
                        If distance < best Then
                            best = distance
                            delta = New PointF(target.X - endpoint.X, target.Y - endpoint.Y)
                        End If
                    Next
                Next
            Next
            If best = tolerance Then
                delta = FactoryMapPlacement.SnapOffset(FactoryMapPlacement.BuildingFootprint(candidate),
                    _building.Where(Function(part) part.Level = candidate.Level AndAlso part.Kind = "Wall" AndAlso part.Id <> candidate.Id).
                        Select(Function(part) FactoryMapPlacement.BuildingFootprint(part)), tolerance)
            End If
            candidate.Position = New PointF(Math.Clamp(candidate.X + delta.X / 1365, 0, 4), Math.Clamp(candidate.Y + delta.Y / 766, 0, 4))
        Else
            Dim delta = FactoryMapPlacement.SnapOffset(FactoryMapPlacement.BuildingFootprint(candidate),
                _building.Where(Function(part) part.Id <> candidate.Id AndAlso part.Level = candidate.Level AndAlso part.Kind = candidate.Kind AndAlso part.ParentWallId = "").
                    Select(Function(part) FactoryMapPlacement.BuildingFootprint(part)), tolerance)
            candidate.Position = New PointF(Math.Clamp(candidate.X + delta.X / 1365, 0, 4), Math.Clamp(candidate.Y + delta.Y / 766, 0, 4))
        End If
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            ClearHitAreas()
            _toolTip.Dispose()
            If _editMenu IsNot Nothing Then _editMenu.Dispose()
            _zoomTimer.Dispose()
            _presentationTimer.Dispose()
            _blinkTimer.Dispose()
            _renderCache.Dispose()
            If _sceneImage IsNot Nothing Then _sceneImage.Dispose()
            _navigation.Dispose()
        End If
        MyBase.Dispose(disposing)
    End Sub

    Private Sub DrawAsset(graphics As Graphics, asset As FactoryMapAsset, originX As Single, originY As Single, scale As Single)
        Dim renderAsset = MountedAsset(asset)
        Dim center = ProjectMapPoint(renderAsset.Position.X * 1365, renderAsset.Position.Y * 766)
        center = New PointF(originX + center.X * scale, originY + center.Y * scale)
        center.Y -= MountHeight(asset) * 8 * CSng(Math.Cos(_elevation)) * scale
        Dim component = _dimensions.Components.FirstOrDefault(Function(item) String.Equals(item.Name, DimensionName(asset.Category), StringComparison.OrdinalIgnoreCase))
        Dim defaults = DefaultDimensions.Components.FirstOrDefault(Function(item) item.Name = DimensionName(asset.Category))
        Dim sizeX = If(component Is Nothing OrElse defaults Is Nothing, 1.0F, CSng(component.LengthMeters / Math.Max(0.1R, defaults.LengthMeters)))
        Dim sizeY = If(component Is Nothing OrElse defaults Is Nothing, 1.0F, CSng(component.WidthMeters / Math.Max(0.1R, defaults.WidthMeters)))
        Dim sizeZ = If(component Is Nothing OrElse defaults Is Nothing, 1.0F, CSng(component.HeightMeters / Math.Max(0.1R, defaults.HeightMeters)))
        sizeX *= asset.ScaleX
        sizeY *= asset.ScaleY
        sizeZ *= asset.ScaleZ
        Dim rotation = (renderAsset.Rotation + If(asset.Category = "Generators", 90, 0)) * Math.PI / 180
        Dim turnCos = CSng(Math.Cos(rotation)), turnSin = CSng(Math.Sin(rotation))
        Dim yawCos = CSng(Math.Cos(_yaw)), yawSin = CSng(Math.Sin(_yaw))
        Dim elevationSin = CSng(Math.Sin(_elevation)), elevationCos = CSng(Math.Cos(_elevation))
        Dim project As Func(Of Single, Single, Single, PointF) =
            Function(x, y, z)
                Dim px = x * sizeX * turnCos - y * sizeY * turnSin
                Dim py = x * sizeX * turnSin + y * sizeY * turnCos
                Return New PointF(px * yawCos - py * yawSin, (px * yawSin + py * yawCos) * elevationSin - z * sizeZ * elevationCos)
            End Function
        Dim doorOpen = LiveDoorOpen(asset.MonitoringDeviceId)
        If Not EditMode AndAlso FactoryMapEquipmentRenderer.IsOpeningModel(asset.Model) AndAlso doorOpen.HasValue Then
            renderAsset = asset.Clone()
            renderAsset.IsOpen = doorOpen.Value
        End If
        Dim presentationScale = scale * FirePresentationFactor(asset)
        If _elevation >= Math.PI / 3 AndAlso asset.Category <> "Fire System" AndAlso
            Not FactoryMapEquipmentRenderer.IsOpeningModel(asset.Model) AndAlso MountHeight(asset) <= 0.1F Then
            Dim dimensions = FactoryMapEquipmentSizes.Dimensions(renderAsset)
            Dim halfLength = dimensions.Length * 4, halfWidth = dimensions.Width * 4
            Dim points = {project(-halfLength, -halfWidth, 0), project(halfLength, -halfWidth, 0),
                          project(halfLength, halfWidth, 0), project(-halfLength, halfWidth, 0)}
            For index = 0 To points.Length - 1
                points(index) = New PointF(center.X + points(index).X * scale, center.Y + points(index).Y * scale)
            Next
            Dim bounds = RectangleF.FromLTRB(points.Min(Function(p) p.X), points.Min(Function(p) p.Y),
                                            points.Max(Function(p) p.X), points.Max(Function(p) p.Y))
            If bounds.IntersectsWith(_renderViewport) Then
                Using path As New Drawing2D.GraphicsPath()
                    path.AddPolygon(points)
                    Dim state = graphics.Save()
                    Try
                        Dim offset = Math.Min(5.0F, Math.Max(0.7F, 1.8F * scale))
                        graphics.TranslateTransform(offset, offset)
                        Using shadow As New SolidBrush(Color.FromArgb(35, 25, 36, 44)),
                              edge As New Pen(Color.FromArgb(18, 25, 36, 44), Math.Min(6.0F, Math.Max(1, 2 * scale)))
                            graphics.FillPath(shadow, path)
                            graphics.DrawPath(edge, path)
                        End Using
                    Finally
                        graphics.Restore(state)
                    End Try
                End Using
            End If
        End If
        Dim hitArea = _renderCache.Draw(graphics, renderAsset, project, sizeX, sizeY, sizeZ, center, presentationScale, _renderViewport)
        If hitArea Is Nothing Then Return
        If FactoryMapMonitoring.ConcealedFire(asset) AndAlso hitArea.GetBounds().Width < 12 Then
            Dim marker = hitArea.GetBounds()
            Dim mid As New PointF(marker.X + marker.Width / 2, marker.Y + marker.Height / 2)
            hitArea.AddEllipse(mid.X - 6, mid.Y - 6, 12, 12)
        End If
        _hitAreas.Add(asset, hitArea)
        _assetHitGeometry.Add(asset, New FactoryMapHitGeometry(hitArea))
        Dim modelBounds = hitArea.GetBounds()
        If ReferenceEquals(asset, _selectedAsset) OrElse (EditMode AndAlso _selectedAssets.Contains(asset)) Then
            Using outline As New Pen(UiTheme.Blue, 2.0F)
                graphics.DrawRectangle(outline, modelBounds.X - 2, modelBounds.Y - 2, modelBounds.Width + 4, modelBounds.Height + 4)
            End Using
        End If
    End Sub

    Friend Shared Function FirePresentationFactor(asset As FactoryMapAsset) As Single
        Return If(asset.Category = "Fire System", 1.25F, 1.0F)
    End Function

    Private Sub DrawBuilding(graphics As Graphics, part As FactoryBuildingPart, project As Func(Of Single, Single, Single, PointF))
        Dim resolved = FactoryMapBuildingStore.Resolve(part, _building)
        Dim overhang = If({"Door", "Gate", "Window"}.Contains(part.Kind), part.Length, 0)
        Dim halfLength = part.Length / 2 + overhang
        Dim halfDepth = part.Depth / 2 + overhang
        If FactoryMapRoadGeometry.IsCurve(part) Then
            halfLength = part.Length + part.Depth / 2
            halfDepth = halfLength
        End If
        Dim baseHeight = resolved.BaseMeters * 8
        Dim center = project(resolved.X * 1365, resolved.Y * 766, baseHeight)
        Dim roof = project(resolved.X * 1365, resolved.Y * 766, (resolved.BaseMeters + resolved.HeightMeters + 1) * 8)
        Dim alongWorld = FactoryMapBuildingStore.WorldPoint(resolved, halfLength, 0)
        Dim acrossWorld = FactoryMapBuildingStore.WorldPoint(resolved, 0, halfDepth)
        Dim along = project(alongWorld.X, alongWorld.Y, baseHeight)
        Dim across = project(acrossWorld.X, acrossWorld.Y, baseHeight)
        ' Affine projection bounds are the sum of the projected half-axis extents.
        Dim halfWidth = Math.Abs(along.X - center.X) + Math.Abs(across.X - center.X)
        Dim halfHeight = Math.Abs(along.Y - center.Y) + Math.Abs(across.Y - center.Y)
        Dim left = Math.Min(center.X, roof.X) - halfWidth, right = Math.Max(center.X, roof.X) + halfWidth
        Dim top = Math.Min(center.Y, roof.Y) - halfHeight, bottom = Math.Max(center.Y, roof.Y) + halfHeight
        Dim bounds = RectangleF.FromLTRB(left - 8, top - 8, right + 8, bottom + 8)
        If Not bounds.IntersectsWith(_renderViewport) Then Return
        Dim hit As New Drawing2D.GraphicsPath(Drawing2D.FillMode.Winding)
        Dim renderPart = part
        Dim doorOpen = LiveDoorOpen(part.MonitoringDeviceId)
        If Not BuildingEditMode AndAlso doorOpen.HasValue AndAlso {"Door", "Gate"}.Contains(part.Kind) Then
            renderPart = part.Clone()
            renderPart.IsOpen = doorOpen.Value
        End If
        FactoryMapBuildingRenderer.Draw(graphics, renderPart, _building, WallOpacity, project, hit)
        _buildingHits.Add(part, hit)
        _buildingHitGeometry.Add(part, New FactoryMapHitGeometry(hit))
        If BuildingEditMode AndAlso _selectedBuilding IsNot Nothing AndAlso
            (ReferenceEquals(part, _selectedBuilding) OrElse (part.RoomId <> "" AndAlso part.RoomId = _selectedBuilding.RoomId)) Then
            Using pen As New Pen(UiTheme.Blue, If(ReferenceEquals(part, _selectedBuilding), 2.2F, 1.2F))
                graphics.DrawPath(pen, hit)
            End Using
        End If
    End Sub

    Private Sub DrawBuildingHandles(graphics As Graphics, project As Func(Of Single, Single, Single, PointF))
        _handles.Clear()
        If Not BuildingEditMode OrElse _selectedBuilding Is Nothing OrElse Not _buildingHits.ContainsKey(_selectedBuilding) Then Return
        Dim part = FactoryMapBuildingStore.Resolve(_selectedBuilding, _building)
        Dim center = project(part.X * 1365, part.Y * 766, part.BaseMeters * 8)
        Dim handlePositions As New Dictionary(Of String, PointF) From {{"Length", FactoryMapBuildingStore.WorldPoint(part, part.Length / 2, 0)}}
        If part.Kind = "Wall" Then handlePositions.Add("Start", FactoryMapBuildingStore.WorldPoint(part, -part.Length / 2, 0))
        If part.ParentWallId = "" Then
            handlePositions.Add("Rotate", FactoryMapBuildingStore.WorldPoint(part, 0, -Math.Max(12, part.Depth / 2 + 12)))
            If {"Wall", "Door", "Gate", "Window", "Floor", "Stairs", "Road", "Marking"}.Contains(part.Kind) Then handlePositions.Add("Depth", FactoryMapBuildingStore.WorldPoint(part, 0, part.Depth / 2))
        End If
        PaintTransformHandles(graphics, center, handlePositions, part.BaseMeters, project)
    End Sub

    Private Sub MoveBuilding(point As Point)
        Dim candidate = _selectedBuilding.Clone()
        Dim resolved = FactoryMapBuildingStore.Resolve(candidate, _building)
        Dim anchor = ScreenToMapAtHeight(point, resolved.BaseMeters)
        If Not String.IsNullOrEmpty(_dragHandle) Then anchor = New PointF(anchor.X + _handleAnchorOffset.X, anchor.Y + _handleAnchorOffset.Y)
        Dim dx = (anchor.X - resolved.X) * 1365
        Dim dy = (anchor.Y - resolved.Y) * 766
        Dim radians = resolved.Rotation * Math.PI / 180
        If _dragHandle = "Rotate" Then
            candidate.Rotation = FactoryMapBuildingStore.NormalizeAngle(CSng(Math.Atan2(dy, dx) * 180 / Math.PI + 90) +
                If(candidate.MirrorVertical, 180, 0))
        ElseIf _dragHandle = "Length" Then
            If candidate.Kind = "Wall" Then
                ResizeWallEndpoint(candidate, anchor, False)
            Else
                candidate.Length = Math.Max(2, Math.Min(5460, CSng(Math.Abs(dx * Math.Cos(radians) + dy * Math.Sin(radians)) * 2)))
            End If
        ElseIf _dragHandle = "Start" Then
            ResizeWallEndpoint(candidate, anchor, True)
        ElseIf _dragHandle = "Depth" Then
            candidate.Depth = Math.Max(0.2F, Math.Min(766, CSng(Math.Abs(-dx * Math.Sin(radians) + dy * Math.Cos(radians)) * 2)))
        ElseIf candidate.ParentWallId <> "" Then
            Dim wall = _building.Single(Function(item) item.Id = candidate.ParentWallId)
            Dim wallRadians = wall.Rotation * Math.PI / 180
            Dim wallDx = (anchor.X + _dragOffset.X - wall.X) * 1365
            Dim wallDy = (anchor.Y + _dragOffset.Y - wall.Y) * 766
            Dim offWall = CSng(-wallDx * Math.Sin(wallRadians) + wallDy * Math.Cos(wallRadians))
            If Math.Abs(offWall) > Math.Max(12, SnapTolerance() * 2 + wall.Depth / 2) OrElse Not SnapEnabled OrElse (ModifierKeys And Keys.Alt) = Keys.Alt Then
                candidate = resolved.Clone()
                candidate.ParentWallId = ""
                candidate.Position = New PointF(Math.Clamp(anchor.X + _dragOffset.X, 0, 4), Math.Clamp(anchor.Y + _dragOffset.Y, 0, 4))
                SnapBuilding(candidate)
            Else
                Dim limit = (wall.Length - candidate.Length) / 2
                candidate.Offset = Math.Clamp(CSng(wallDx * Math.Cos(wallRadians) + wallDy * Math.Sin(wallRadians)) *
                    If(wall.MirrorHorizontal, -1, 1), -limit, limit)
            End If
        Else
            candidate.Position = New PointF(Math.Max(0, Math.Min(4, anchor.X + _dragOffset.X)), Math.Max(0, Math.Min(4, anchor.Y + _dragOffset.Y)))
            SnapBuilding(candidate)
        End If
        ApplyBuildingCandidate(candidate)
    End Sub

    Private Sub ApplyBuildingCandidate(candidate As FactoryBuildingPart)
        Try
            FactoryMapBuildingStore.Validate(_building.Where(Function(item) item.Id <> candidate.Id).Concat({candidate}))
            _selectedBuilding.Position = candidate.Position
            _selectedBuilding.Length = candidate.Length
            _selectedBuilding.Depth = candidate.Depth
            _selectedBuilding.Rotation = candidate.Rotation
            _selectedBuilding.MirrorHorizontal = candidate.MirrorHorizontal
            _selectedBuilding.MirrorVertical = candidate.MirrorVertical
            _selectedBuilding.Offset = candidate.Offset
            _selectedBuilding.ParentWallId = candidate.ParentWallId
            _selectedBuilding.BaseMeters = candidate.BaseMeters
            _selectedBuilding.RoomId = candidate.RoomId
            _selectedBuilding.RoomName = candidate.RoomName
            _selectedBuilding.TemperatureSourceId = candidate.TemperatureSourceId
            _selectedBuilding.TemperatureSourceConfirmed = candidate.TemperatureSourceConfirmed
            _selectedBuilding.FirePanelId = candidate.FirePanelId
            _selectedBuilding.FireSiteId = candidate.FireSiteId
            _selectedBuilding.FireZone = candidate.FireZone
            RaiseEvent BuildingChanged(If({"Door", "Window", "Gate"}.Contains(candidate.Kind) AndAlso candidate.ParentWallId = "",
                "Free opening: drag near a same-floor wall with enough length/height and no overlapping openings. Alt bypasses attachment.",
                "Building geometry changed. Save to persist; Cancel to restore. Properties edits height, materials and opening state."))
            Invalidate()
        Catch ex As InvalidDataException
            RaiseEvent BuildingChanged(ex.Message)
        End Try
    End Sub

    Private Function FindBuildingAt(point As Point) As FactoryBuildingPart
        For Each entry In _buildingHits.Reverse()
            If Not _building.Contains(entry.Key) Then Continue For
            If (entry.Key.Kind = "Wall" AndAlso Not ShowWalls) OrElse ({"Floor", "Marking"}.Contains(entry.Key.Kind) AndAlso Not ShowFloors) OrElse
                (entry.Key.Kind = "Road" AndAlso Not _visibleLayers.Contains("Roads")) OrElse
                (Not {"Wall", "Floor", "Road", "Marking"}.Contains(entry.Key.Kind) AndAlso Not ShowOpenings) Then Continue For
            If _buildingHitGeometry(entry.Key).Contains(point, entry.Key.Kind <> "Floor") Then Return entry.Key
        Next
        Return Nothing
    End Function

    Private Sub DrawUnplacedChillers(graphics As Graphics)
        Dim pending = _assets.Where(Function(asset) asset.Level = ActiveLevel AndAlso Not asset.IsPlaced AndAlso _visibleLayers.Contains(asset.Category)).ToArray()
        If pending.Length = 0 Then Return
        Dim text = pending.Length & " unplaced item(s). Use Edit Map > item list > Edit Item to place them."
        Using font As New Font("Segoe UI", 8.0F, FontStyle.Bold), background As New SolidBrush(Color.FromArgb(238, 255, 255, 255)),
              foreground As New SolidBrush(UiTheme.Amber), outline As New Pen(UiTheme.Amber, 1.0F)
            Dim measured = graphics.MeasureString(text, font)
            Dim box As New RectangleF(8, Math.Max(28, ClientSize.Height - measured.Height - 14), Math.Min(ClientSize.Width - 16, measured.Width + 16), measured.Height + 8)
            graphics.FillRectangle(background, box)
            graphics.DrawRectangle(outline, box.X, box.Y, box.Width, box.Height)
            graphics.DrawString(text, font, foreground, box.X + 8, box.Y + 4)
        End Using
    End Sub

    Private Function ProjectMapPoint(x As Single, y As Single) As PointF
        Dim centeredX = x - ReferenceSize.Width / 2.0F
        Dim centeredY = y - ReferenceSize.Height / 2.0F
        Dim cosine = CSng(Math.Cos(_yaw))
        Dim sine = CSng(Math.Sin(_yaw))
        Dim verticalScale = CSng(Math.Sin(_elevation))
        Return New PointF(centeredX * cosine - centeredY * sine,
                          (centeredX * sine + centeredY * cosine) * verticalScale)
    End Function

    Private Function ProjectVector(x As Single, y As Single) As PointF
        Dim cosine = CSng(Math.Cos(_yaw))
        Dim sine = CSng(Math.Sin(_yaw))
        Dim verticalScale = CSng(Math.Sin(_elevation))
        Return New PointF(x * cosine - y * sine, (x * sine + y * cosine) * verticalScale)
    End Function

    Private Function GetSiteSize() As SizeF
        Dim width = 1365 * _site.WidthPercent / 100.0F
        Dim depth = 766 * _site.DepthPercent / 100.0F
        For Each asset In _assets.Where(Function(item) item.IsPlaced)
            width = Math.Max(width, asset.Position.X * 1365)
            depth = Math.Max(depth, asset.Position.Y * 766)
        Next
        For Each part In _building
            Dim resolved = FactoryMapBuildingStore.Resolve(part, _building)
            width = Math.Max(width, resolved.X * 1365)
            depth = Math.Max(depth, resolved.Y * 766)
        Next
        Return New SizeF(width, depth)
    End Function

    ' Camera framing: the factory itself (walls, floors, placed items) plus a small margin, not the whole site ground and roads.
    Private Function GetFrameRect() As RectangleF
        Dim minX = Single.MaxValue, minY = Single.MaxValue, maxX = Single.MinValue, maxY = Single.MinValue
        For Each part In _building
            If part.ParentWallId <> "" OrElse (part.Kind <> "Wall" AndAlso part.Kind <> "Floor") Then Continue For
            Dim radians = part.Rotation * CSng(Math.PI) / 180.0F
            Dim halfLength = part.Length / 2.0F, halfDepth = Math.Max(1.0F, part.Depth) / 2.0F
            Dim extentX = Math.Abs(CSng(Math.Cos(radians))) * halfLength + Math.Abs(CSng(Math.Sin(radians))) * halfDepth
            Dim extentY = Math.Abs(CSng(Math.Sin(radians))) * halfLength + Math.Abs(CSng(Math.Cos(radians))) * halfDepth
            minX = Math.Min(minX, part.X * 1365 - extentX) : maxX = Math.Max(maxX, part.X * 1365 + extentX)
            minY = Math.Min(minY, part.Y * 766 - extentY) : maxY = Math.Max(maxY, part.Y * 766 + extentY)
        Next
        For Each asset In _assets
            If Not asset.IsPlaced Then Continue For
            minX = Math.Min(minX, asset.Position.X * 1365) : maxX = Math.Max(maxX, asset.Position.X * 1365)
            minY = Math.Min(minY, asset.Position.Y * 766) : maxY = Math.Max(maxY, asset.Position.Y * 766)
        Next
        If minX >= maxX OrElse minY >= maxY Then
            Dim site = GetSiteSize()
            Return New RectangleF(0, 0, site.Width, site.Height)
        End If
        Const margin As Single = 14.0F
        Return RectangleF.FromLTRB(minX - margin, minY - margin, maxX + margin, maxY + margin)
    End Function

    Private Function GetProjectedBounds() As RectangleF
        Dim frame = GetFrameRect()
        Dim corners = {
            ProjectMapPoint(frame.Left, frame.Top),
            ProjectMapPoint(frame.Right, frame.Top),
            ProjectMapPoint(frame.Left, frame.Bottom),
            ProjectMapPoint(frame.Right, frame.Bottom)
        }
        Dim left = corners.Min(Function(point) point.X)
        Dim wallHeight = CSng(_dimensions.Components.Where(Function(item) item.Name = "External Wall" OrElse item.Name = "Internal Wall").
            Max(Function(item) item.HeightMeters) * 8 * Math.Cos(_elevation))
        If _building.Count > 0 Then wallHeight = Math.Max(wallHeight, _building.Max(Function(part) part.HeightMeters + part.BaseMeters) * 8 * CSng(Math.Cos(_elevation)))
        Dim top = corners.Min(Function(point) point.Y) - Math.Max(0, wallHeight)
        Return New RectangleF(left, top, corners.Max(Function(point) point.X) - left, corners.Max(Function(point) point.Y) - top)
    End Function

    Private Shared Function DimensionName(category As String) As String
        Select Case category
            Case "Generators" : Return "Generator"
            Case "Compressors" : Return "Compressor"
            Case "Chillers" : Return "Chiller"
            Case "UPS" : Return "UPS Cabinet"
            Case "Transformers" : Return "Transformer"
            Case "Battery Rooms" : Return "Battery Cabinet"
            Case Else : Return String.Empty
        End Select
    End Function

    Private Function FindAssetAt(point As Point) As FactoryMapAsset
        For Each entry In _hitAreas.Reverse()
            If Not _assets.Contains(entry.Key) OrElse Not entry.Key.IsPlaced OrElse Not _visibleLayers.Contains(entry.Key.Category) OrElse Not ShouldShowAsset(entry.Key) Then Continue For
            If _assetHitGeometry(entry.Key).Contains(point) Then Return entry.Key
        Next
        Return Nothing
    End Function

    Private Sub ClearHitAreas()
        For Each area In _hitAreas.Values
            area.Dispose()
        Next
        _hitAreas.Clear()
        _assetHitGeometry.Clear()
        For Each area In _buildingHits.Values
            area.Dispose()
        Next
        _buildingHits.Clear()
        _buildingHitGeometry.Clear()
        _handles.Clear()
    End Sub

    Private Function StatusText(asset As FactoryMapAsset) As String
        If FactoryMapMeterLink.Supports(asset.Model) Then Return If(FactoryMapMeterLink.Find(asset, _snapshots) Is Nothing, "Unknown / Not Connected", "Good acquisition")
        Dim upsId = If(asset.Category = "Battery Rooms", asset.ParentUpsId, asset.EquipmentId)
        If upsId.StartsWith("UPS-", StringComparison.OrdinalIgnoreCase) Then
            Dim ups As UpsSnapshot = Nothing
            If Not _upsSnapshots.TryGetValue(upsId, ups) OrElse ups Is Nothing Then Return "Unknown / Not Connected"
            If Not String.Equals(ups.DeviceId, upsId, StringComparison.OrdinalIgnoreCase) Then Return "Unknown / Source mismatch"
            If Not ups.IsGood Then Return ups.Quality.ToString()
            Return If(FactoryMapMonitoring.IsCurrentUps(ups), "Good acquisition", "Stale / No current data")
        End If
        If Not asset.EquipmentId.StartsWith("TRANSFORMER-", StringComparison.OrdinalIgnoreCase) Then Return "Unknown / Not Connected"
        Dim snapshot = FactoryMapMeterLink.Find(asset, _snapshots)
        If snapshot Is Nothing Then Return "Unknown / Not Connected"
        Select Case snapshot.PowerState
            Case TransformerPowerState.On : Return "Verified On"
            Case TransformerPowerState.Off : Return "Verified Off"
            Case Else : Return "State Unknown"
        End Select
    End Function
End Class
