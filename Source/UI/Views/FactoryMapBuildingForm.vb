Imports System.Drawing
Imports System.IO
Imports System.Linq
Imports System.Windows.Forms

Friend NotInheritable Class FactoryMapBuildingForm
    Inherits Form

    Private ReadOnly _original As FactoryBuildingPart
    Private ReadOnly _parts As List(Of FactoryBuildingPart)
    Private ReadOnly _name As New TextBox With {.MaxLength = 120}
    Private ReadOnly _kind As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _style As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _material As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _wall As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .DropDownWidth = 400}
    Private ReadOnly _direction As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _open As New CheckBox With {.Text = "Open (visual state only)", .AutoSize = True}
    Private ReadOnly _access As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .DropDownWidth = 500}
    Private _accessDevices As New List(Of MonitoringDevice)
    Private _registryError As String = ""
    Private ReadOnly _x As NumericUpDown = Number(0, 400, 50, 2)
    Private ReadOnly _y As NumericUpDown = Number(0, 400, 50, 2)
    Private ReadOnly _length As NumericUpDown = Number(2, 5460, 80, 1)
    Private ReadOnly _depth As NumericUpDown = Number(0.2D, 800, 2, 1)
    Private ReadOnly _height As NumericUpDown = Number(0.01D, 100, 3, 2)
    Private ReadOnly _base As NumericUpDown = Number(0, 100, 0, 2)
    Private ReadOnly _rotation As NumericUpDown = Number(0, 360, 0, 2)
    Private ReadOnly _mirrorHorizontal As New CheckBox With {.Text = "Mirror horizontal (local X)", .AutoSize = True}
    Private ReadOnly _mirrorVertical As New CheckBox With {.Text = "Mirror vertical (local Y)", .AutoSize = True}
    Private ReadOnly _offset As NumericUpDown = Number(-682, 682, 0, 1)
    Private ReadOnly _bend As NumericUpDown = Number(5, 170, 90, 1)
    Private ReadOnly _colorButton As Button = UiTheme.ActionButton("Choose color")
    Private ReadOnly _preview As New FactoryBuildingPreview With {.Dock = DockStyle.Fill}
    Private ReadOnly _error As New Label With {.Dock = DockStyle.Bottom, .Height = 42, .ForeColor = UiTheme.Red, .Padding = New Padding(10)}
    Private _color As String
    Private _loading As Boolean = True
    Private _result As FactoryBuildingPart

    Public ReadOnly Property Part As FactoryBuildingPart
        Get
            Return _result
        End Get
    End Property

    Public Sub New(part As FactoryBuildingPart, parts As List(Of FactoryBuildingPart), isNew As Boolean)
        _original = part
        _parts = parts
        _color = part.BodyColor
        Text = "Building - Walls, Doors, Windows, Gates and Room Floors"
        Size = New Size(1000, 850)
        MinimumSize = New Size(900, 650)
        StartPosition = FormStartPosition.CenterParent
        Font = New Font("Segoe UI", 9)
        BackColor = UiTheme.Surface
        Dim content As New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 2, .RowCount = 1}
        content.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 64))
        content.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 36))
        content.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        Dim scroll As New Panel With {.Dock = DockStyle.Fill, .AutoScroll = True}
        Dim fields As New TableLayoutPanel With {.Dock = DockStyle.Top, .AutoSize = True, .ColumnCount = 2, .Padding = New Padding(14)}
        fields.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 205))
        fields.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        Dim rows As (Name As String, Input As Control)() = {
            ("Room / element name", _name), ("Building element", _kind), ("Style / shape", _style),
            ("Material / floor finish", _material), ("Color (customizable)", _colorButton),
            ("Attach opening to wall", _wall), ("Opening offset (plan units)", _offset),
            ("Center X (%)", _x), ("Center Y (%)", _y), ("Length (plan units)", _length),
            ("Depth / thickness (plan units)", _depth), ("Road bend angle (degrees)", _bend), ("Height (visual meters)", _height),
            ("Sill / base (visual meters)", _base), ("Rotation (degrees)", _rotation),
            ("Horizontal reflection", _mirrorHorizontal), ("Vertical reflection", _mirrorVertical),
            ("Hinge / opening direction", _direction), ("Door / gate / window state", _open), ("Registered access point", _access)
        }
        fields.RowCount = rows.Length + 1
        For index = 0 To rows.Length - 1
            fields.RowStyles.Add(New RowStyle(SizeType.Absolute, 35))
            fields.Controls.Add(New Label With {.Text = rows(index).Name, .Dock = DockStyle.Fill, .TextAlign = ContentAlignment.MiddleLeft}, 0, index)
            rows(index).Input.Dock = DockStyle.Fill
            fields.Controls.Add(rows(index).Input, 1, index)
        Next
        Dim note As New Label With {
            .Text = "Drag to move; square handles resize, round handle rotates. Ctrl+C/V copies, Ctrl+D duplicates, Delete removes. Curved roads: Length is centerline radius, Depth is road width; choose left/right and bend angle. Copied walls include openings. Rapid curtains roll UP. Use Marking for walkways and keep-clear zones. Units are not surveyed meters.",
            .Dock = DockStyle.Fill
        }
        fields.RowStyles.Add(New RowStyle(SizeType.Absolute, 100))
        fields.Controls.Add(note, 0, rows.Length)
        fields.SetColumnSpan(note, 2)
        scroll.Controls.Add(fields)
        content.Controls.Add(scroll, 0, 0)
        content.Controls.Add(_preview, 1, 0)
        Dim buttons As New FlowLayoutPanel With {.Dock = DockStyle.Bottom, .Height = 50, .FlowDirection = FlowDirection.RightToLeft, .Padding = New Padding(8)}
        Dim apply = UiTheme.ActionButton("Apply", True)
        apply.Width = 110
        Dim cancel = UiTheme.ActionButton("Cancel")
        cancel.Width = 100
        cancel.DialogResult = DialogResult.Cancel
        AddHandler apply.Click, AddressOf ApplyPart
        buttons.Controls.Add(apply)
        buttons.Controls.Add(cancel)
        Controls.Add(content)
        Controls.Add(_error)
        Controls.Add(buttons)
        AcceptButton = apply
        CancelButton = cancel
        Try
            _accessDevices = MonitoringDeviceStore.Load().Devices.Where(Function(device) device.Kind = "Access").ToList()
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is System.Text.Json.JsonException OrElse TypeOf ex Is UnauthorizedAccessException
            _registryError = "Monitoring registry could not be loaded: " & ex.Message
            _error.Text = _registryError
            apply.Enabled = False
        End Try
        FactoryMapItemForm.FillLinks(_access, _accessDevices.Select(Function(device) New KeyValuePair(Of String, String)(device.Id, device.Name)).ToList(), part.MonitoringDeviceId)
        _kind.Items.AddRange(FactoryMapBuildingStore.Styles.Keys.Cast(Of Object)().ToArray())
        _kind.SelectedItem = part.Kind
        _kind.Enabled = isNew
        AddHandler _kind.SelectedIndexChanged, AddressOf KindChanged
        FillChoices()
        _style.SelectedItem = part.Style
        _material.SelectedItem = part.Material
        _wall.Items.Add("None - free placement")
        _wall.Items.AddRange(parts.Where(Function(item) item.Kind = "Wall" AndAlso item.Id <> part.Id).Cast(Of Object)().ToArray())
        _wall.SelectedIndex = 0
        For index = 1 To _wall.Items.Count - 1
            If DirectCast(_wall.Items(index), FactoryBuildingPart).Id = part.ParentWallId Then _wall.SelectedIndex = index
        Next
        _direction.Items.AddRange(FactoryMapBuildingStore.Directions)
        _direction.SelectedItem = part.OpeningDirection
        _name.Text = part.Name
        Dim effective = FactoryMapBuildingStore.Resolve(part, parts)
        _x.Value = CDec(effective.X * 100)
        _y.Value = CDec(effective.Y * 100)
        _length.Value = CDec(part.Length)
        _depth.Value = CDec(effective.Depth)
        _height.Value = CDec(part.HeightMeters)
        _base.Value = CDec(part.BaseMeters)
        _rotation.Value = CDec(effective.Rotation)
        _offset.Value = CDec(part.Offset)
        _bend.Value = CDec(part.RoadBendDegrees)
        _open.Checked = part.IsOpen
        _mirrorHorizontal.Checked = part.MirrorHorizontal
        _mirrorVertical.Checked = part.MirrorVertical
        _colorButton.BackColor = ColorTranslator.FromHtml(_color)
        AddHandler _colorButton.Click, Sub(sender, e)
                                          Using dialog As New ColorDialog With {.FullOpen = True, .Color = ColorTranslator.FromHtml(_color)}
                                              If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
                                              _color = "#" & dialog.Color.R.ToString("X2") & dialog.Color.G.ToString("X2") & dialog.Color.B.ToString("X2")
                                              _colorButton.Text = "Color " & _color
                                              RefreshPreview()
                                          End Using
                                      End Sub
        For Each numeric As NumericUpDown In {_x, _y, _length, _depth, _height, _base, _rotation, _offset, _bend}
            AddHandler numeric.ValueChanged, Sub(sender, e) RefreshPreview()
        Next
        AddHandler _material.SelectedIndexChanged, Sub(sender, e)
                                                      If _loading Then Return
                                                      _color = FactoryMapBuildingStore.MaterialColor(CStr(_material.SelectedItem))
                                                      RefreshPreview()
                                                  End Sub
        AddHandler _style.SelectedIndexChanged, Sub(sender, e)
                                                   If _loading Then Return
                                                   If CStr(_kind.SelectedItem) = "Marking" Then
                                                       Dim broad = {"Walkway", "Keep clear hatching"}.Contains(CStr(_style.SelectedItem))
                                                       _depth.Value = If(broad, Math.Max(20D, _depth.Value), Math.Min(2D, _depth.Value))
                                                   End If
                                                   RefreshPreview()
                                               End Sub
        For Each combo As ComboBox In {_direction, _wall}
            AddHandler combo.SelectedIndexChanged, Sub(sender, e) RefreshPreview()
        Next
        AddHandler _open.CheckedChanged, Sub(sender, e) RefreshPreview()
        AddHandler _mirrorHorizontal.CheckedChanged, Sub(sender, e) RefreshPreview()
        AddHandler _mirrorVertical.CheckedChanged, Sub(sender, e) RefreshPreview()
        _loading = False
        RefreshPreview()
    End Sub

    Private Shared Function Number(minimum As Decimal, maximum As Decimal, value As Decimal, places As Integer) As NumericUpDown
        Return New NumericUpDown With {.Minimum = minimum, .Maximum = maximum, .Value = value, .DecimalPlaces = places, .Increment = If(places = 2, 0.1D, 1D)}
    End Function

    Private Sub FillChoices()
        Dim kind = CStr(_kind.SelectedItem)
        _style.Items.Clear()
        _style.Items.AddRange(FactoryMapBuildingStore.Styles(kind))
        _style.SelectedIndex = 0
        _material.Items.Clear()
        _material.Items.AddRange(FactoryMapBuildingStore.Materials(kind))
        _material.SelectedIndex = 0
    End Sub

    Private Sub KindChanged(sender As Object, e As EventArgs)
        _loading = True
        FillChoices()
        Dim kind = CStr(_kind.SelectedItem)
        _name.Text = "New " & kind.ToLowerInvariant()
        _height.Value = If(kind = "Floor", 0.02D, If(kind = "Window", 1.2D, 2.5D))
        _base.Value = If(kind = "Window", 1D, 0D)
        _length.Value = If(kind = "Road", 160D, If(kind = "Wall" OrElse kind = "Floor" OrElse kind = "Stairs", 80D, If(kind = "Gate", 40D, 18D)))
        _depth.Value = If(kind = "Road", 38D, If(kind = "Floor", 60D, If(kind = "Stairs", 40D, 2D)))
        If kind = "Road" Then _height.Value = 0.02D
        If kind = "Marking" Then
            _height.Value = 0.03D
            _length.Value = 100
            _depth.Value = 2
        End If
        _wall.SelectedIndex = 0
        _color = FactoryMapBuildingStore.MaterialColor(CStr(_material.SelectedItem))
        _loading = False
        RefreshPreview()
    End Sub

    Private Function ReadPart() As FactoryBuildingPart
        Dim result = _original.Clone()
        result.Name = _name.Text.Trim()
        result.Kind = CStr(_kind.SelectedItem)
        result.Style = CStr(_style.SelectedItem)
        result.Material = CStr(_material.SelectedItem)
        result.BodyColor = _color
        result.Position = New PointF(CSng(_x.Value / 100), CSng(_y.Value / 100))
        result.Length = CSng(_length.Value)
        result.Depth = CSng(_depth.Value)
        result.HeightMeters = CSng(_height.Value)
        result.BaseMeters = CSng(_base.Value)
        result.Rotation = FactoryMapBuildingStore.NormalizeAngle(CSng(_rotation.Value))
        result.MirrorHorizontal = _mirrorHorizontal.Checked
        result.MirrorVertical = _mirrorVertical.Checked
        result.Offset = CSng(_offset.Value)
        result.RoadBendDegrees = CSng(_bend.Value)
        result.ParentWallId = If(_wall.SelectedIndex > 0 AndAlso {"Door", "Gate", "Window"}.Contains(result.Kind),
                                DirectCast(_wall.SelectedItem, FactoryBuildingPart).Id, String.Empty)
        If _original.ParentWallId <> "" AndAlso result.ParentWallId = "" Then
            Dim previousWall = _parts.Single(Function(part) part.Id = _original.ParentWallId)
            result.BaseMeters += previousWall.BaseMeters
            result.MirrorHorizontal = result.MirrorHorizontal Xor previousWall.MirrorHorizontal
            result.MirrorVertical = result.MirrorVertical Xor previousWall.MirrorVertical
        End If
        If result.ParentWallId <> "" Then
            Dim parent = DirectCast(_wall.SelectedItem, FactoryBuildingPart)
            result.RoomId = parent.RoomId
            result.RoomName = parent.RoomName
            result.TemperatureSourceId = parent.TemperatureSourceId
            result.TemperatureSourceConfirmed = parent.TemperatureSourceConfirmed
            result.FirePanelId = parent.FirePanelId
            result.FireSiteId = parent.FireSiteId
            result.FireZone = parent.FireZone
        End If
        result.OpeningDirection = CStr(_direction.SelectedItem)
        result.IsOpen = _open.Checked
        result.MonitoringDeviceId = If({"Door", "Gate"}.Contains(result.Kind), FactoryMapItemForm.SelectedLink(_access, _accessDevices.Select(Function(device) device.Id).ToList(), _original.MonitoringDeviceId), "")
        Return result
    End Function

    Private Sub RefreshPreview()
        If _loading Then Return
        _colorButton.Text = "Color " & _color
        Dim opening = {"Door", "Gate", "Window"}.Contains(CStr(_kind.SelectedItem))
        _access.Enabled = {"Door", "Gate"}.Contains(CStr(_kind.SelectedItem))
        _wall.Enabled = opening
        Dim attached = opening AndAlso _wall.SelectedIndex > 0
        _offset.Enabled = attached
        _x.Enabled = Not attached
        _y.Enabled = Not attached
        _rotation.Enabled = Not attached
        _depth.Enabled = Not attached
        _direction.Enabled = opening AndAlso CStr(_style.SelectedItem) <> "Rapid roll-up" AndAlso CStr(_style.SelectedItem) <> "Fixed" AndAlso CStr(_style.SelectedItem) <> "Open hole"
        _open.Enabled = opening AndAlso CStr(_style.SelectedItem) <> "Open hole" AndAlso Not (CStr(_kind.SelectedItem) = "Window" AndAlso CStr(_style.SelectedItem) = "Fixed")
        If Not _open.Enabled AndAlso _open.Checked Then _open.Checked = False
        Dim part = ReadPart()
        _bend.Enabled = FactoryMapRoadGeometry.IsCurve(part)
        Dim preview = FactoryMapBuildingStore.Resolve(part, _parts).Clone()
        preview.ParentWallId = String.Empty
        _preview.SetPart(preview)
    End Sub

    Private Sub ApplyPart(sender As Object, e As EventArgs)
        If _registryError <> "" Then Return
        Try
            Dim part = ReadPart()
            FactoryMapBuildingStore.Validate(_parts.Where(Function(item) item.Id <> part.Id).Concat({part}))
            _result = part
            DialogResult = DialogResult.OK
        Catch ex As InvalidDataException
            _error.Text = ex.Message
        End Try
    End Sub
End Class

Friend NotInheritable Class FactoryBuildingPreview
    Inherits Control

    Private _part As FactoryBuildingPart

    Public Sub New()
        DoubleBuffered = True
        BackColor = UiTheme.SurfaceMuted
    End Sub

    Public Sub SetPart(part As FactoryBuildingPart)
        _part = part
        Invalidate()
    End Sub

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        MyBase.OnPaint(e)
        If _part Is Nothing OrElse Width < 30 OrElse Height < 30 Then Return
        e.Graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias
        Dim part = _part.Clone()
        part.X = 0
        part.Y = 0
        Dim projection As Func(Of Single, Single, Single, PointF) = Function(x, y, z) New PointF((x - y) * 0.707F, (x + y) * 0.354F - z * 0.866F)
        Using scratch As New Bitmap(1, 1), measuring = Graphics.FromImage(scratch), path As New Drawing2D.GraphicsPath(Drawing2D.FillMode.Winding)
            FactoryMapBuildingRenderer.Draw(measuring, part, {part}, 40, projection, path)
            Dim bounds = path.GetBounds()
            Dim scale = Math.Min(5.0F, Math.Min((Width - 35) / Math.Max(1, bounds.Width), (Height - 100) / Math.Max(1, bounds.Height)))
            Dim screen As Func(Of Single, Single, Single, PointF) =
                Function(x, y, z)
                    Dim point = projection(x, y, z)
                    Return New PointF(Width / 2 + (point.X - bounds.Left - bounds.Width / 2) * scale, Height / 2 + (point.Y - bounds.Top - bounds.Height / 2) * scale)
                End Function
            Using hit As New Drawing2D.GraphicsPath(Drawing2D.FillMode.Winding)
                FactoryMapBuildingRenderer.Draw(e.Graphics, part, {part}, 40, screen, hit)
            End Using
        End Using
        TextRenderer.DrawText(e.Graphics, "BUILDING PREVIEW" & Environment.NewLine & part.Kind & " / " & part.Style & " / " & part.Material,
                              Font, New Rectangle(8, 12, Width - 16, 70), UiTheme.Ink, TextFormatFlags.WordBreak Or TextFormatFlags.HorizontalCenter)
    End Sub
End Class
