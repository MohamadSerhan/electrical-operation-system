Imports System.Drawing
Imports System.IO
Imports System.Linq
Imports System.Windows.Forms

Friend NotInheritable Class FactoryMapItemForm
    Inherits Form

    Friend Shared ReadOnly Shapes As New Dictionary(Of String, String()) From {
        {"Generators", {"Caterpillar generator", "Caterpillar generator 810 kVA", "Caterpillar generator 1500 kVA"}},
        {"Transformers", {"Transformer"}},
        {"UPS", {"Vertiv EXL 500 kVA", "Vertiv NXL 800 kVA"}},
        {"Battery Rooms", {"Battery rack - 4 levels"}},
        {"Compressors", {"HP compressor 40 bar", "HP compressor 40 bar with control and power panels",
                         "Atlas Copco compressor 7 bar", "Atlas Copco compressor 7 bar with control and power panels",
                         "Refrigerated air dryer", "Compressed air filter bank", "Compressor control panel", "Compressor power panel"}},
        {"Chillers", {"Chiller", "Chiller with control and power panels", "Hansa chiller", "Hansa chiller with control and power panels", "Air-cooled screw chiller", "Water-cooled screw chiller",
                      "Centrifugal water-cooled chiller", "Chiller pump and buffer skid", "Chiller control panel", "Chiller power panel",
                      "Hansa chiller cooling tower", "HP compressor cooling tower 40 bar"}},
        {"HVAC", {"Indoor wall AC 1.5 HP", "Indoor wall AC 2.5 HP", "Indoor cassette AC 5 HP", "Indoor ducted AC 10 HP", "Indoor floor AC 20 HP",
                  "Outdoor AC 1.5 HP", "Outdoor AC 2.5 HP", "Outdoor AC 5 HP", "Outdoor AC 10 HP", "Outdoor AC 20 HP",
                  "VRV outdoor system", "VRV indoor cassette", "Air handling unit"}},
        {"Production Lines", {FactoryMapCustomEquipmentStore.SidelModelName, "PET Combi - still water", "PET Combi - carbonated drinks",
                             "Can filler and seamer - carbonated drinks", "Beverage line with filler", "Conveyor", "PET bottling line", "Can filling line", "Glass bottling line"}},
        {"Water Systems", {"Ultrafiltration skid", "Ultrafiltration twin rack", "Reverse osmosis skid", "Sand filter vessel", "Activated carbon filter",
                           "Water softener skid", "UV sterilizer skid", "Chemical dosing skid", "Water booster pump skid", "Water treatment control panel",
                           "Stainless water tank", "Stainless process vat", "CIP cleaning skid",
                           "CIP station - 1 tank", "CIP station - 2 tanks", "CIP station - 3 tanks",
                           "Water treatment process skid"}},
        {"Filler Room", {"PET bottle blower", "Bottle rinser", "Rotary bottle filler", "Bottle capper", "Bottle labeler", "Shrink wrapper",
                         "Bottle accumulation table", "Filler control panel", "Air conveyor", "Conveyor corner",
                         "Syrup mixing skid", "Beverage carbonator", "Krones-style mixing and carbonation skid",
                         "CIP station - 1 tank", "CIP station - 2 tanks", "CIP station - 3 tanks",
                         "Plate pasteurizer", "Bottle inspection station", "Date coding station",
                         "Case packer", "Palletizer", "Pallet stretch wrapper", "Preform hopper",
                         "Preform cage and tipper", "Preform roller sorter and bridge", "Can end magazine", "Empty-can inverter and rinser",
                         "Preform elevator", "Cap elevator", "Cap sorter and chute", "Cap sterilizer", "Bottle unscrambler",
                         "Rinser filler capper monoblock", "Air conveyor blower", "HEPA filler canopy",
                         "Product buffer tank", "Sanitary product pump", "Sanitary valve manifold", "CIP return skid",
                         "Bottle air knife dryer", "Bottle reject station", "Inline checkweigher", "Inline metal detector",
                         "Can seamer", "Empty container depalletizer", "Returnable bottle washer", "Foam cleaning station"}},
        {"Electrical Panels", {"Wall panel", "Wall panel with energy meter", "Floor panel", "Floor panel with energy meter",
                               "Switchboard lineup", "Switchboard lineup with energy meters", "ATS cabinet", "Motor control center",
                               "ACB panel with energy meter", "ATS panel with energy meter", "MTS panel with energy meter", "Load panel with energy meter",
                               "RMU switchgear", "Eaton RMU switchgear", "SF6 circuit breaker", "HV voltage regulator", "Eaton HV voltage regulator",
                               "HV utility pole", "HV lattice transmission tower", "HV pole fuse cutouts", "HV insulator assembly"}},
        {"Motors & Drives", {"Electric motor", "Variable frequency drive"}},
        {"Pumps & Tanks", {"Centrifugal pump", "Twin pump skid", "Vertical storage tank", "Horizontal air receiver"}},
        {"Sensors & Controls", {"PLC cabinet", "HMI console", "Energy meter", "Pressure transmitter", "Temperature sensor", "Stack beacon"}},
        {"Utilities", {"Utility cabinet", "Cooling tower", "Ventilation fan", "Pipe section", "Isolation valve", "Cable tray",
                       "Steam boiler", "Forklift", "Pallet rack", "Pallet stack", "Loading dock", "Safety bollard",
                       "Office desk", "Office chair", "Office cabinet"}},
        {"CCTV", {"Camera", "Dome camera"}},
        {"Access Control", {"Access reader", "Single swing gate", "Double swing gate", "Sliding gate", "Rapid roll-up gate", "Glass entry door", "Wood entry door"}},
        {"Fire System", {"Fire panel", "Fire extinguisher", "Fire hydrant", "Smoke detector", "Zeta fire panel",
                         "Zeta smoke detector", "Zeta heat detector", "Zeta multi-sensor detector", "Zeta manual call point",
                         "Zeta sounder", "Zeta sounder beacon", "Zeta siren", "Zeta beam detector", "Zeta interface module", "Zeta isolator module",
                         "Zeta repeater panel", "Fire suppression control panel", "CO2 cylinder bank", "FM200 cylinder bank"}}
    }

    Private ReadOnly _original As FactoryMapAsset
    Private ReadOnly _catalog As List(Of FactoryMapAsset)
    Private ReadOnly _name As New TextBox With {.MaxLength = 120}
    Private ReadOnly _category As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _shape As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .DropDownWidth = 540, .MaxDropDownItems = 18,
                                                  .AutoCompleteSource = AutoCompleteSource.ListItems, .AutoCompleteMode = AutoCompleteMode.SuggestAppend}
    Private ReadOnly _link As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _meter As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .DropDownWidth = 500}
    Private ReadOnly _monitoring As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .DropDownWidth = 560}
    Private ReadOnly _parentUps As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .DropDownWidth = 500}
    Private ReadOnly _room As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .DropDownWidth = 500}
    Private ReadOnly _cameraIp As New TextBox With {.MaxLength = 120}
    Private ReadOnly _mountHeight As New NumericUpDown With {.Maximum = 100, .DecimalPlaces = 2, .Increment = 0.1D}
    Private _monitoringDevices As New List(Of MonitoringDevice)
    Private _roomChoices As New List(Of FactoryBuildingPart)
    Private _upsChoices As New List(Of FactoryMapAsset)
    Private _meterChoices As New List(Of KeyValuePair(Of String, String))
    Private ReadOnly _placed As New CheckBox With {.Text = "Placed on map", .AutoSize = True}
    Private ReadOnly _x As New NumericUpDown With {.Maximum = 400, .DecimalPlaces = 2, .Increment = 0.5D}
    Private ReadOnly _y As New NumericUpDown With {.Maximum = 400, .DecimalPlaces = 2, .Increment = 0.5D}
    Private ReadOnly _length As New NumericUpDown With {.Minimum = 20, .Maximum = 400, .Increment = 10}
    Private ReadOnly _rotation As New NumericUpDown With {.Minimum = 0, .Maximum = 359, .Increment = 15}
    Private ReadOnly _mirrorHorizontal As New CheckBox With {.Text = "Mirror horizontal (local X)", .AutoSize = True}
    Private ReadOnly _mirrorVertical As New CheckBox With {.Text = "Mirror vertical (local Y)", .AutoSize = True}
    Private ReadOnly _scaleX As New NumericUpDown With {.Minimum = 10, .Maximum = 500, .Value = 100, .Increment = 10}
    Private ReadOnly _scaleY As New NumericUpDown With {.Minimum = 10, .Maximum = 500, .Value = 100, .Increment = 10}
    Private ReadOnly _scaleZ As New NumericUpDown With {.Minimum = 10, .Maximum = 500, .Value = 100, .Increment = 10}
    Private ReadOnly _nominalSize As New Label With {.TextAlign = ContentAlignment.MiddleLeft}
    Private ReadOnly _colorButton As New Button With {.Text = "Choose color...", .AutoSize = True}
    Private ReadOnly _originalColor As New CheckBox With {.Text = "Original colors", .AutoSize = True, .Checked = True}
    Private ReadOnly _open As New CheckBox With {.Text = "Open (visual only)", .AutoSize = True}
    Private ReadOnly _direction As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _preview As New FactoryMapItemPreview With {.Dock = DockStyle.Fill}
    Private _bodyColor As String = String.Empty
    Private _result As FactoryMapAsset

    Public ReadOnly Property Asset As FactoryMapAsset
        Get
            Return _result
        End Get
    End Property

    Public Sub New(asset As FactoryMapAsset, catalog As List(Of FactoryMapAsset))
        _original = asset
        _catalog = catalog
        Text = "Map Item - Resize, Rotate, Color and Equipment Shape"
        StartPosition = FormStartPosition.CenterParent
        Size = New Size(980, 850)
        MinimumSize = New Size(900, 600)
        MinimizeBox = False
        MaximizeBox = False
        BackColor = UiTheme.Surface
        Font = New Font("Segoe UI", 9.0F)
        Dim body As New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 2, .RowCount = 1}
        body.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 65))
        body.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 35))
        body.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        Dim scrolling As New Panel With {.Dock = DockStyle.Fill, .AutoScroll = True}
        Dim fields As New TableLayoutPanel With {
            .Dock = DockStyle.Top, .Padding = New Padding(16), .ColumnCount = 2,
            .AutoSize = True
        }
        fields.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 205))
        fields.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        Dim colors As New FlowLayoutPanel With {.AutoSize = True, .WrapContents = False}
        colors.Controls.Add(_originalColor)
        colors.Controls.Add(_colorButton)
        Dim inputs As (Caption As String, Input As Control)() = {
            ("Display name", _name), ("Link to system equipment", _link), ("Independent panel meter", _meter),
            ("Registered monitoring device", _monitoring), ("Battery rack belongs to UPS", _parentUps),
            ("Camera IP (optional override)", _cameraIp), ("Room / ceiling / wall reference", _room), ("Mount / fallback ceiling height (m)", _mountHeight),
            ("Category", _category), ("Schematic 3D shape", _shape),
            ("Placement", _placed), ("Map X (%)", _x), ("Map Y (%)", _y),
            ("Conveyor length (plan units)", _length), ("Rotation (degrees)", _rotation),
            ("Horizontal reflection", _mirrorHorizontal), ("Vertical reflection", _mirrorVertical),
            ("Length (%)", _scaleX), ("Width (%)", _scaleY), ("Height (%)", _scaleZ),
            ("Approx. size L x W x H (m)", _nominalSize),
            ("Body / enclosure color", colors), ("Gate / door state", _open), ("Gate / door opening direction", _direction)
        }
        fields.RowCount = inputs.Length + 1
        For index = 0 To inputs.Length - 1
            fields.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
            fields.Controls.Add(New Label With {.Text = inputs(index).Caption, .Dock = DockStyle.Fill, .TextAlign = ContentAlignment.MiddleLeft}, 0, index)
            inputs(index).Input.Dock = DockStyle.Fill
            fields.Controls.Add(inputs(index).Input, 1, index)
        Next
        Dim note As New Label With {
            .Text = "Zeta-style 3D enclosures at the common metre scale; zoom in to see small devices. Smoke/heat/multisensors hang beneath the room ceiling. Wall devices align to the nearest wall in this room (same level), or nearest level wall without a room. Sizes/heights/faces are provisional: verify the installed variant on site. Normal mode hides quiet devices. History is not live fire. Battery readings belong to the parent UPS bank.",
            .Dock = DockStyle.Fill
        }
        fields.RowStyles.Add(New RowStyle(SizeType.Absolute, 100))
        fields.Controls.Add(note, 0, inputs.Length)
        fields.SetColumnSpan(note, 2)
        Dim buttons As New FlowLayoutPanel With {.Dock = DockStyle.Bottom, .Height = 48, .FlowDirection = FlowDirection.RightToLeft, .Padding = New Padding(8)}
        Dim apply = UiTheme.ActionButton("Apply", True)
        Dim cancel = UiTheme.ActionButton("Cancel", False)
        apply.Width = 100
        cancel.Width = 100
        cancel.DialogResult = DialogResult.Cancel
        AddHandler apply.Click, AddressOf ApplyItem
        buttons.Controls.Add(apply)
        buttons.Controls.Add(cancel)
        scrolling.Controls.Add(fields)
        body.Controls.Add(scrolling, 0, 0)
        Dim previewPanel As New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 1, .RowCount = 3, .Padding = New Padding(8)}
        previewPanel.RowStyles.Add(New RowStyle(SizeType.Absolute, 48))
        previewPanel.RowStyles.Add(New RowStyle(SizeType.Absolute, 80))
        previewPanel.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        previewPanel.Controls.Add(New Label With {.Text = "LIVE 3D PREVIEW" & Environment.NewLine & "Resize, rotate and recolor before Apply", .Dock = DockStyle.Fill}, 0, 0)
        Dim presets As New FlowLayoutPanel With {.Dock = DockStyle.Fill}
        Dim nxl = UiTheme.ActionButton("NXL 800 kVA", False)
        nxl.Width = 130
        AddHandler nxl.Click, Sub(sender, e)
                                  _category.SelectedItem = "UPS"
                                  _shape.SelectedItem = "Vertiv NXL 800 kVA"
                              End Sub
        presets.Controls.Add(nxl)
        Dim resetSize = UiTheme.ActionButton("100% size", False)
        resetSize.Width = 95
        AddHandler resetSize.Click, Sub(sender, e)
                                        _scaleX.Value = 100
                                        _scaleY.Value = 100
                                        _scaleZ.Value = 100
                                    End Sub
        presets.Controls.Add(resetSize)
        For Each degrees In {0, 90, 180, 270}
            Dim heading = degrees
            Dim turn = UiTheme.ActionButton(heading.ToString() & " deg")
            turn.Width = 66
            AddHandler turn.Click, Sub(sender, e) _rotation.Value = heading
            presets.Controls.Add(turn)
        Next
        presets.WrapContents = True
        previewPanel.Controls.Add(presets, 0, 1)
        previewPanel.Controls.Add(_preview, 0, 2)
        body.Controls.Add(previewPanel, 1, 0)
        Controls.Add(body)
        Controls.Add(buttons)
        AcceptButton = apply
        CancelButton = cancel

        _name.Text = asset.Name
        _cameraIp.Text = asset.CameraIpAddress
        _mountHeight.Value = CDec(asset.BaseHeightMeters)
        _upsChoices = catalog.Where(Function(item) item.Category = "UPS").ToList()
        FillLinks(_parentUps, _upsChoices.Select(Function(item) New KeyValuePair(Of String, String)(item.EquipmentId, item.Name)).ToList(), asset.ParentUpsId)
        ConfigureMonitoring(New MonitoringDeviceRegistry, New List(Of FactoryBuildingPart))
        _direction.Items.AddRange(FactoryMapBuildingStore.Directions)
        _direction.SelectedItem = asset.OpeningDirection
        _open.Checked = asset.IsOpen
        AddHandler _open.CheckedChanged, Sub(sender, e) UpdatePreview()
        AddHandler _direction.SelectedIndexChanged, Sub(sender, e) UpdatePreview()
        _category.Items.AddRange(Shapes.Keys.Cast(Of Object)().ToArray())
        AddHandler _category.SelectedIndexChanged, Sub(sender, e)
                                                      _shape.Items.Clear()
                                                      _shape.Items.AddRange(Shapes(CStr(_category.SelectedItem)))
                                                      If asset.CustomEquipment IsNot Nothing AndAlso CStr(_category.SelectedItem) = "Utilities" Then _shape.Items.Add(FactoryMapCustomEquipmentStore.ModelName)
                                                      _shape.SelectedIndex = 0
                                                  End Sub
        _category.SelectedItem = asset.Category
        _shape.SelectedItem = asset.Model
        If asset.CustomEquipment IsNot Nothing Then
            _category.Enabled = False
            _shape.Enabled = False
        End If
        If _shape.SelectedIndex < 0 Then _shape.SelectedIndex = 0
        _link.Items.Add("None - visual item only")
        _link.DropDownWidth = 500
        _link.Items.AddRange(catalog.Select(Function(item) CObj(item.Name & " [" & item.EquipmentId & "] - " & item.Model)).ToArray())
        Dim linked = catalog.FindIndex(Function(item) String.Equals(item.EquipmentId, asset.EquipmentId, StringComparison.OrdinalIgnoreCase))
        _link.SelectedIndex = linked + 1
        If linked < 0 AndAlso Not String.IsNullOrEmpty(asset.EquipmentId) Then
            _link.Items.Add("Unavailable equipment [" & asset.EquipmentId & "]")
            _link.SelectedIndex = _link.Items.Count - 1
        End If
        AddHandler _link.SelectedIndexChanged, AddressOf LinkChanged
        SetMeterChoices(New List(Of KeyValuePair(Of String, String)))
        AddHandler _shape.SelectedIndexChanged, Sub(sender, e) UpdateConveyorField()
        _placed.Checked = asset.IsPlaced
        _x.Value = CDec(asset.Position.X * 100)
        _y.Value = CDec(asset.Position.Y * 100)
        _length.Value = CDec(asset.ConveyorLength)
        _rotation.Value = CDec(asset.Rotation)
        _mirrorHorizontal.Checked = asset.MirrorHorizontal
        _mirrorVertical.Checked = asset.MirrorVertical
        AddHandler _mirrorHorizontal.CheckedChanged, Sub(sender, e) UpdatePreview()
        AddHandler _mirrorVertical.CheckedChanged, Sub(sender, e) UpdatePreview()
        _scaleX.Value = CDec(asset.ScaleX * 100)
        _scaleY.Value = CDec(asset.ScaleY * 100)
        _scaleZ.Value = CDec(asset.ScaleZ * 100)
        _bodyColor = If(String.IsNullOrEmpty(asset.BodyColor), "#4682B4", asset.BodyColor)
        _originalColor.Checked = String.IsNullOrEmpty(asset.BodyColor)
        If Not _originalColor.Checked Then
            _colorButton.BackColor = ColorTranslator.FromHtml(_bodyColor)
            _colorButton.ForeColor = If(_colorButton.BackColor.GetBrightness() < 0.5F, Color.White, Color.Black)
        End If
        AddHandler _colorButton.Click, AddressOf ChooseColor
        AddHandler _originalColor.CheckedChanged, Sub(sender, e) UpdatePreview()
        For Each numeric As NumericUpDown In {_rotation, _length, _scaleX, _scaleY, _scaleZ}
            AddHandler numeric.ValueChanged, Sub(sender, e) UpdatePreview()
        Next
        AddHandler _shape.SelectedIndexChanged, Sub(sender, e) UpdatePreview()
        UpdateConveyorField()
        UpdatePreview()
    End Sub

    Public Sub ConfigureMonitoring(registry As MonitoringDeviceRegistry, building As List(Of FactoryBuildingPart))
        _monitoringDevices = registry.Devices
        _roomChoices = building.Where(Function(part) part.RoomId <> "").GroupBy(Function(part) part.RoomId).Select(Function(group) group.First()).ToList()
        FillLinks(_monitoring, _monitoringDevices.Select(Function(device) New KeyValuePair(Of String, String)(device.Id, device.Kind & " / " & device.Name & " / " & device.Location)).ToList(), _original.MonitoringDeviceId)
        FillLinks(_room, _roomChoices.Select(Function(part) New KeyValuePair(Of String, String)(part.RoomId, part.RoomName)).ToList(), _original.RoomId)
    End Sub

    Friend Shared Sub FillLinks(combo As ComboBox, choices As List(Of KeyValuePair(Of String, String)), current As String)
        combo.Items.Clear()
        combo.Items.Add("None")
        combo.Items.AddRange(choices.Select(Function(item) CObj(item.Value & " [" & item.Key & "]")).ToArray())
        combo.SelectedIndex = choices.FindIndex(Function(item) item.Key = current) + 1
        If current <> "" AndAlso combo.SelectedIndex = 0 Then
            combo.Items.Add("Unavailable [" & current & "]")
            combo.SelectedIndex = combo.Items.Count - 1
        End If
    End Sub

    Friend Shared Function SelectedLink(combo As ComboBox, ids As List(Of String), previous As String) As String
        If combo.SelectedIndex <= 0 Then Return ""
        Return If(combo.SelectedIndex <= ids.Count, ids(combo.SelectedIndex - 1), previous)
    End Function

    Public Sub SetMeterChoices(choices As List(Of KeyValuePair(Of String, String)))
        _meterChoices = choices
        _meter.Items.Clear()
        _meter.Items.Add("None - no independent meter")
        For Each choice In choices
            _meter.Items.Add(choice.Value & " [" & choice.Key & "]")
        Next
        Dim selected = choices.FindIndex(Function(choice) choice.Key = _original.MeterSourceId)
        _meter.SelectedIndex = selected + 1
        If selected < 0 AndAlso _original.MeterSourceId <> "" Then
            _meter.Items.Add("Unavailable meter [" & _original.MeterSourceId & "] - retained, no fallback")
            _meter.SelectedIndex = _meter.Items.Count - 1
        End If
    End Sub

    Private Sub ChooseColor(sender As Object, e As EventArgs)
        Using dialog As New ColorDialog With {.FullOpen = True, .Color = If(String.IsNullOrEmpty(_bodyColor), Color.SteelBlue, ColorTranslator.FromHtml(_bodyColor))}
            If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
            _bodyColor = "#" & dialog.Color.R.ToString("X2") & dialog.Color.G.ToString("X2") & dialog.Color.B.ToString("X2")
            _colorButton.BackColor = dialog.Color
            _colorButton.ForeColor = If(dialog.Color.GetBrightness() < 0.5F, Color.White, Color.Black)
            _originalColor.Checked = False
            UpdatePreview()
        End Using
    End Sub

    Private Sub ApplyAppearance(item As FactoryMapAsset)
        item.ScaleX = CSng(_scaleX.Value / 100D)
        item.ScaleY = CSng(_scaleY.Value / 100D)
        item.ScaleZ = CSng(_scaleZ.Value / 100D)
        item.Rotation = CSng(_rotation.Value)
        item.MirrorHorizontal = _mirrorHorizontal.Checked
        item.MirrorVertical = _mirrorVertical.Checked
        item.ConveyorLength = CSng(_length.Value)
        item.BodyColor = If(_originalColor.Checked, String.Empty, _bodyColor)
        item.IsOpen = _open.Checked
        item.OpeningDirection = CStr(_direction.SelectedItem)
    End Sub

    Private Sub UpdatePreview()
        If _shape.SelectedItem Is Nothing Then Return
        Dim opening = FactoryMapEquipmentRenderer.IsOpeningModel(CStr(_shape.SelectedItem))
        _open.Enabled = opening
        _direction.Enabled = opening
        _meter.Enabled = FactoryMapMeterLink.Supports(CStr(_shape.SelectedItem))
        _parentUps.Enabled = CStr(_category.SelectedItem) = "Battery Rooms"
        _cameraIp.Enabled = CStr(_category.SelectedItem) = "CCTV"
        _monitoring.Enabled = True
        Dim item = _original.Clone()
        item.Model = CStr(_shape.SelectedItem)
        item.Category = CStr(_category.SelectedItem)
        If _mountHeight.Value = 0 Then _mountHeight.Value = CDec(FactoryMapMonitoring.DefaultMountHeight(item.Model))
        ApplyAppearance(item)
        Dim size = FactoryMapEquipmentSizes.Dimensions(item)
        _nominalSize.Text = String.Format(Globalization.CultureInfo.InvariantCulture, "{0:0.##} x {1:0.##} x {2:0.##}",
                                         size.Length * item.ScaleX, size.Width * item.ScaleY, size.Height * item.ScaleZ)
        _preview.SetAsset(item)
    End Sub

    Private Sub UpdateConveyorField()
        _length.Enabled = {"Conveyor", "Beverage line with filler", "Air conveyor", "Conveyor corner", "PET bottling line", "Can filling line", "Glass bottling line"}.Contains(CStr(_shape.SelectedItem))
    End Sub

    Private Sub LinkChanged(sender As Object, e As EventArgs)
        If _link.SelectedIndex <= 0 OrElse _link.SelectedIndex > _catalog.Count Then Return
        If _original.CustomEquipment IsNot Nothing Then Return
        If _shape.SelectedItem IsNot Nothing AndAlso FactoryMapMeterLink.Supports(CStr(_shape.SelectedItem)) Then Return
        Dim equipment = _catalog(_link.SelectedIndex - 1)
        _category.SelectedItem = equipment.Category
        _shape.SelectedItem = equipment.Model
    End Sub

    Private Sub ApplyItem(sender As Object, e As EventArgs)
        Try
            Dim item = _original.Clone()
            item.Name = _name.Text.Trim()
            item.Category = CStr(_category.SelectedItem)
            item.Model = CStr(_shape.SelectedItem)
            item.IsPlaced = _placed.Checked
            item.Position = New PointF(CSng(_x.Value / 100D), CSng(_y.Value / 100D))
            ApplyAppearance(item)
            item.ParentUpsId = If(item.Category = "Battery Rooms", SelectedLink(_parentUps, _upsChoices.Select(Function(ups) ups.EquipmentId).ToList(), _original.ParentUpsId), "")
            item.CameraIpAddress = If(item.Category = "CCTV", _cameraIp.Text.Trim(), "")
            item.MonitoringDeviceId = If(_monitoring.Enabled, SelectedLink(_monitoring, _monitoringDevices.Select(Function(device) device.Id).ToList(), _original.MonitoringDeviceId), "")
            item.RoomId = SelectedLink(_room, _roomChoices.Select(Function(room) room.RoomId).ToList(), _original.RoomId)
            item.BaseHeightMeters = CSng(_mountHeight.Value)
            If FactoryMapMonitoring.IsCeiling(item.Model) AndAlso item.RoomId = "" AndAlso item.BaseHeightMeters <= 0 Then Throw New InvalidDataException("Select the detector's room to follow its ceiling, or enter a mounting height above zero.")
            If item.MonitoringDeviceId <> "" Then
                Dim device = _monitoringDevices.FirstOrDefault(Function(value) value.Id = item.MonitoringDeviceId)
                Dim expected = If(item.Category = "CCTV", "Camera", If(item.Category = "Fire System", "Fire", If(item.Category = "Access Control", "Access", "Equipment")))
                If device IsNot Nothing AndAlso device.Kind <> expected Then Throw New InvalidDataException("Choose a registered " & expected & " device for this shape.")
            End If
            If _link.SelectedIndex = 0 Then
                item.EquipmentId = String.Empty
                item.Source = "No linked acquisition source"
            ElseIf _link.SelectedIndex <= _catalog.Count Then
                Dim equipment = _catalog(_link.SelectedIndex - 1)
                If equipment.Category <> item.Category AndAlso Not FactoryMapMeterLink.Supports(item.Model) AndAlso item.CustomEquipment Is Nothing Then Throw New InvalidDataException("The selected category does not match the linked equipment. Choose 'None - visual item only' or select the linked equipment's category. Metered panels and custom image symbols can be attached to equipment in any category.")
                item.EquipmentId = equipment.EquipmentId
                item.Source = equipment.Source
                item.Location = equipment.Location
            End If
            item.MeterSourceId = If(FactoryMapMeterLink.Supports(item.Model), _original.MeterSourceId, "")
            If _meter.SelectedIndex = 0 Then item.MeterSourceId = ""
            If _meter.Enabled AndAlso _meter.SelectedIndex > 0 AndAlso _meter.SelectedIndex <= _meterChoices.Count Then
                item.MeterSourceId = _meterChoices(_meter.SelectedIndex - 1).Key
            End If
            FactoryMapLayoutStore.ValidateAssets({item})
            _result = item
            DialogResult = DialogResult.OK
        Catch ex As InvalidDataException
            MessageBox.Show(Me, ex.Message, "Invalid map item", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Try
    End Sub
End Class

Friend NotInheritable Class FactoryMapItemPreview
    Inherits Control

    Private _asset As FactoryMapAsset
    Private ReadOnly _customRenderer As New FactoryMapCustomEquipmentRenderer()

    Private Sub DrawPreview(graphics As Graphics, project As Func(Of Single, Single, Single, PointF), hit As Drawing2D.GraphicsPath)
        If _asset.Model = FactoryMapCustomEquipmentStore.ModelName Then
            _customRenderer.Draw(graphics, _asset, project, hit)
        Else
            FactoryMapEquipmentRenderer.Draw(graphics, _asset, project, hit)
        End If
    End Sub

    Public Sub New()
        DoubleBuffered = True
        BackColor = Color.FromArgb(242, 246, 249)
    End Sub

    Public Sub SetAsset(asset As FactoryMapAsset)
        _asset = asset
        Invalidate()
    End Sub

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        MyBase.OnPaint(e)
        If _asset Is Nothing OrElse ClientSize.Width < 20 OrElse ClientSize.Height < 20 Then Return
        e.Graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias
        Dim project As Func(Of Single, Single, Single, PointF) =
            Function(x, y, z)
                Dim plan = FactoryMapPlacement.RotatePoint(x * _asset.ScaleX, y * _asset.ScaleY,
                    _asset.Rotation + If(_asset.Category = "Generators", 90, 0))
                Dim rx = plan.X
                Dim ry = plan.Y
                Return New PointF((rx - ry) * 0.707F, (rx + ry) * 0.354F - z * _asset.ScaleZ * 0.866F)
            End Function
        Using boundsPath As New Drawing2D.GraphicsPath(Drawing2D.FillMode.Winding),
              scratch As New Bitmap(1, 1), measuring = Graphics.FromImage(scratch)
            DrawPreview(measuring, project, boundsPath)
            Dim bounds = boundsPath.GetBounds()
            Dim availableWidth = Math.Max(1, ClientSize.Width - 32)
            Dim availableHeight = Math.Max(1, ClientSize.Height - 64)
            Dim scale = Math.Min(availableWidth / Math.Max(1, bounds.Width), availableHeight / Math.Max(1, bounds.Height))
            If _asset.CustomEquipment Is Nothing Then scale = Math.Min(4.0F, scale)
            Dim screenProject As Func(Of Single, Single, Single, PointF) =
                Function(x, y, z)
                    Dim point = project(x, y, z)
                    Return New PointF(ClientSize.Width / 2 + (point.X - bounds.Left - bounds.Width / 2) * scale,
                                      ClientSize.Height / 2 + (point.Y - bounds.Top - bounds.Height / 2) * scale)
                End Function
            Using hitArea As New Drawing2D.GraphicsPath(Drawing2D.FillMode.Winding)
                DrawPreview(e.Graphics, screenProject, hitArea)
            End Using
        End Using
        TextRenderer.DrawText(e.Graphics, If(_asset.CustomEquipment Is Nothing, _asset.Model,
            _asset.CustomEquipment.Name & If(_asset.CustomEquipment.GeometryModel = FactoryMapCustomEquipmentStore.CadModelName,
                If(_asset.CustomEquipment.CadGeometry.IsPlan, " (imported 2D CAD outlines)", " (imported 3D geometry)"),
                If(_asset.CustomEquipment.GeometryModel = "", " (image-based silhouette)", " (modeled 3D equipment)"))),
            Font, New Rectangle(4, 4, ClientSize.Width - 8, 40), UiTheme.Ink,
                              TextFormatFlags.WordBreak Or TextFormatFlags.HorizontalCenter)
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then _customRenderer.Dispose()
        MyBase.Dispose(disposing)
    End Sub
End Class
