Imports System.Drawing
Imports System.IO
Imports System.Linq
Imports System.Windows.Forms

Friend NotInheritable Class FactoryMapRoomForm
    Inherits Form

    Private ReadOnly _parts As List(Of FactoryBuildingPart)
    Private ReadOnly _rooms As New ComboBox With {.Dock = DockStyle.Fill, .DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _name As New TextBox With {.Dock = DockStyle.Fill, .MaxLength = 120}
    Private ReadOnly _members As New CheckedListBox With {.Dock = DockStyle.Fill, .CheckOnClick = True, .HorizontalScrollbar = True}
    Private ReadOnly _message As New Label With {.Dock = DockStyle.Fill, .ForeColor = UiTheme.Red}
    Private ReadOnly _roomIds As New List(Of String) From {""}
    Private ReadOnly _selected As FactoryBuildingPart
    Private ReadOnly _temperature As New ComboBox With {.Dock = DockStyle.Fill, .MaxLength = 120}
    Private ReadOnly _confirmed As New CheckBox With {.Dock = DockStyle.Fill, .Text = "Commissioned: this PLC TDEGC channel measures THIS ROOM (not equipment)"}
    Private ReadOnly _firePanel As New TextBox With {.Dock = DockStyle.Fill, .MaxLength = 120}
    Private ReadOnly _fireSite As New TextBox With {.Dock = DockStyle.Fill, .MaxLength = 120}
    Private ReadOnly _fireZone As New TextBox With {.Dock = DockStyle.Fill, .MaxLength = 120}
    Private _result As List(Of FactoryBuildingPart)

    Public ReadOnly Property Parts As List(Of FactoryBuildingPart)
        Get
            Return _result
        End Get
    End Property

    Public Sub New(parts As List(Of FactoryBuildingPart), selected As FactoryBuildingPart)
        Me.New(parts, selected, Nothing)
    End Sub

    Public Sub New(parts As List(Of FactoryBuildingPart), selected As FactoryBuildingPart, temperatureSources As IEnumerable(Of String))
        _parts = parts
        _selected = selected
        Text = "Rooms - Create, Name and Rename"
        Size = New Size(900, 850)
        MinimumSize = New Size(750, 700)
        StartPosition = FormStartPosition.CenterParent
        Font = New Font("Segoe UI", 9)
        BackColor = UiTheme.Surface
        Dim root As New TableLayoutPanel With {.Dock = DockStyle.Fill, .Padding = New Padding(14), .ColumnCount = 1, .RowCount = 8}
        For Each rowHeight As Integer In {38, 38, 62, 210}
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, rowHeight))
        Next
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 26))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 42))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 44))
        root.Controls.Add(_rooms, 0, 0)
        root.Controls.Add(_name, 0, 1)
        root.Controls.Add(New Label With {.Text = "Choose Create new room or an existing room to rename. Check its walls and floor below. A room needs at least 3 walls, OR a floor and 2 walls. Members cannot belong to another room. Apply changes the current Building edit only; Save persists, Cancel restores.", .Dock = DockStyle.Fill}, 0, 2)
        _members.Items.AddRange(parts.Where(Function(part) part.Kind = "Wall" OrElse part.Kind = "Floor").OrderBy(Function(part) part.Name).Cast(Of Object)().ToArray())
        Dim monitoring As New TableLayoutPanel With {.Dock = DockStyle.Fill, .ColumnCount = 2, .RowCount = 6}
        monitoring.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 190))
        monitoring.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        Dim rows As (Caption As String, Input As Control)() = {
            ("Temperature source", _temperature), ("Source verification", _confirmed), ("Fire panel ID (exact)", _firePanel),
            ("Cloud site ID (exact)", _fireSite), ("Fire zone (exact)", _fireZone)}
        For index = 0 To rows.Length - 1
            monitoring.RowStyles.Add(New RowStyle(SizeType.Absolute, 32))
            monitoring.Controls.Add(New Label With {.Text = rows(index).Caption, .Dock = DockStyle.Fill, .TextAlign = ContentAlignment.MiddleLeft}, 0, index)
            monitoring.Controls.Add(rows(index).Input, 1, index)
        Next
        Dim notice As New Label With {.Dock = DockStyle.Fill, .Text = "Source format PLC:<configured meter name>, TDEGC channel 15. Unverified/missing/stale data never means normal. Temperature blinks WALLS only. Only a verified FIRE state may blink walls + floor red; historical events and device faults do not."}
        monitoring.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        monitoring.Controls.Add(notice, 0, 5)
        monitoring.SetColumnSpan(notice, 2)
        root.Controls.Add(monitoring, 0, 3)
        If temperatureSources IsNot Nothing Then _temperature.Items.AddRange(temperatureSources.Cast(Of Object)().ToArray())
        root.Controls.Add(_members, 0, 4)
        Dim selectRelated As New CheckBox With {.Text = "Select all segments with the selected element's name", .Dock = DockStyle.Fill}
        AddHandler selectRelated.CheckedChanged, Sub(sender, e)
                                                    If _selected Is Nothing Then Return
                                                    For index = 0 To _members.Items.Count - 1
                                                        Dim member = DirectCast(_members.Items(index), FactoryBuildingPart)
                                                        If member.Name = _selected.Name Then _members.SetItemChecked(index, selectRelated.Checked)
                                                    Next
                                                End Sub
        root.Controls.Add(selectRelated, 0, 5)
        root.Controls.Add(_message, 0, 6)
        Dim buttons As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .FlowDirection = FlowDirection.RightToLeft}
        Dim apply = UiTheme.ActionButton("Apply", True)
        apply.Width = 110
        Dim cancel = UiTheme.ActionButton("Cancel")
        cancel.Width = 100
        cancel.DialogResult = DialogResult.Cancel
        AddHandler apply.Click, AddressOf ApplyRoom
        buttons.Controls.Add(apply)
        buttons.Controls.Add(cancel)
        root.Controls.Add(buttons, 0, 7)
        Controls.Add(root)
        AcceptButton = apply
        CancelButton = cancel
        _rooms.Items.Add("Create new room")
        For Each room In parts.Where(Function(part) part.RoomId <> "").GroupBy(Function(part) part.RoomId).OrderBy(Function(group) group.First().RoomName)
            _roomIds.Add(room.Key)
            _rooms.Items.Add(room.First().RoomName)
        Next
        AddHandler _rooms.SelectedIndexChanged, AddressOf RoomChanged
        _rooms.SelectedIndex = If(selected IsNot Nothing AndAlso selected.RoomId <> "", _roomIds.IndexOf(selected.RoomId), 0)
    End Sub

    Private Sub RoomChanged(sender As Object, e As EventArgs)
        Dim id = _roomIds(_rooms.SelectedIndex)
        _name.Text = If(id = "", "New room", _parts.First(Function(part) part.RoomId = id).RoomName)
        Dim source = If(id = "", Nothing, _parts.First(Function(part) part.RoomId = id))
        _temperature.Text = If(source Is Nothing, "", source.TemperatureSourceId)
        _confirmed.Checked = source IsNot Nothing AndAlso source.TemperatureSourceConfirmed
        _firePanel.Text = If(source Is Nothing, "", source.FirePanelId)
        _fireSite.Text = If(source Is Nothing, "", source.FireSiteId)
        _fireZone.Text = If(source Is Nothing, "", source.FireZone)
        For index = 0 To _members.Items.Count - 1
            Dim part = DirectCast(_members.Items(index), FactoryBuildingPart)
            _members.SetItemChecked(index, If(id <> "", part.RoomId = id, ReferenceEquals(part, _selected)))
        Next
    End Sub

    Private Sub ApplyRoom(sender As Object, e As EventArgs)
        Try
            Dim name = _name.Text.Trim()
            If name = "" Then Throw New InvalidDataException("Enter a room name.")
            Dim temperatureSource = _temperature.Text.Trim()
            If temperatureSource <> "" AndAlso (Not temperatureSource.StartsWith("PLC:", StringComparison.Ordinal) OrElse temperatureSource.Length <= 4) Then Throw New InvalidDataException("Choose a PLC temperature source using PLC:<configured meter name>, or leave empty.")
            If _confirmed.Checked AndAlso temperatureSource = "" Then Throw New InvalidDataException("Choose a temperature source before confirming commissioning.")
            If _fireZone.Text.Trim() <> "" AndAlso (_firePanel.Text.Trim() = "" OrElse _fireSite.Text.Trim() = "") Then Throw New InvalidDataException("Fire zone matching needs the exact panel and cloud site ID.")
            Dim selected = _members.CheckedItems.Cast(Of FactoryBuildingPart)().ToArray()
            Dim walls = selected.Where(Function(part) part.Kind = "Wall").Count()
            If walls < 3 AndAlso Not (walls >= 2 AndAlso selected.Any(Function(part) part.Kind = "Floor")) Then
                Throw New InvalidDataException("Select at least 3 walls, or a floor with at least 2 walls.")
            End If
            Dim existingId = _roomIds(_rooms.SelectedIndex)
            If selected.Any(Function(part) part.RoomId <> "" AndAlso part.RoomId <> existingId) Then Throw New InvalidDataException("A selected element belongs to another room. Edit that room first to remove it.")
            Dim id = If(existingId = "", Guid.NewGuid().ToString("N"), existingId)
            Dim selectedIds = selected.Select(Function(part) part.Id).ToHashSet()
            _result = _parts.Select(Function(part) part.Clone()).ToList()
            For Each part In _result
                If selectedIds.Contains(part.Id) OrElse (part.ParentWallId <> "" AndAlso selectedIds.Contains(part.ParentWallId)) Then
                    part.RoomId = id
                    part.RoomName = name
                    part.TemperatureSourceId = temperatureSource
                    part.TemperatureSourceConfirmed = _confirmed.Checked
                    part.FirePanelId = _firePanel.Text.Trim()
                    part.FireSiteId = _fireSite.Text.Trim()
                    part.FireZone = _fireZone.Text.Trim()
                ElseIf part.RoomId = existingId AndAlso existingId <> "" Then
                    part.RoomId = ""
                    part.RoomName = ""
                    part.TemperatureSourceId = ""
                    part.TemperatureSourceConfirmed = False
                    part.FirePanelId = ""
                    part.FireSiteId = ""
                    part.FireZone = ""
                End If
            Next
            FactoryMapBuildingStore.Validate(_result)
            DialogResult = DialogResult.OK
        Catch ex As InvalidDataException
            _message.Text = ex.Message
        End Try
    End Sub
End Class
