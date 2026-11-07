Imports System.Drawing
Imports System.IO
Imports System.Linq
Imports System.Text.Json
Imports System.Windows.Forms

Friend NotInheritable Class FactoryMapEquipmentLibraryForm
    Inherits Form

    Private NotInheritable Class LibraryEntry
        Public Asset As FactoryMapAsset
        Public Overrides Function ToString() As String
            Return If(Asset.CustomEquipment Is Nothing, Asset.Model, Asset.CustomEquipment.Name & " [uploaded]") & " - " & Asset.Category
        End Function
    End Class

    Private ReadOnly _entries As New List(Of LibraryEntry)
    Private ReadOnly _search As New TextBox With {.Dock = DockStyle.Fill, .PlaceholderText = "Search equipment name, model or category..."}
    Private ReadOnly _category As New ComboBox With {.Dock = DockStyle.Fill, .DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _list As New ListBox With {.Dock = DockStyle.Fill, .HorizontalScrollbar = True, .IntegralHeight = False}
    Private ReadOnly _preview As New FactoryMapItemPreview With {.Dock = DockStyle.Fill}
    Private ReadOnly _description As New Label With {.Dock = DockStyle.Fill}
    Private ReadOnly _message As New Label With {.Dock = DockStyle.Fill, .ForeColor = UiTheme.MutedInk, .AutoEllipsis = True}
    Private ReadOnly _place As Button = UiTheme.ActionButton("Place selected", True)
    Private ReadOnly _delete As Button = UiTheme.ActionButton("Delete uploaded")
    Private ReadOnly _directory As String
    Private _result As FactoryMapAsset

    Public ReadOnly Property SelectedTemplate As FactoryMapAsset
        Get
            Return _result
        End Get
    End Property

    Public Sub New(Optional directory As String = Nothing)
        _directory = directory
        Text = "Equipment Library - Search, Preview and Import"
        Size = New Size(1000, 720)
        MinimumSize = New Size(800, 560)
        StartPosition = FormStartPosition.CenterParent
        Font = New Font("Segoe UI", 9)
        BackColor = UiTheme.Surface
        Dim root As New TableLayoutPanel With {.Dock = DockStyle.Fill, .Padding = New Padding(12), .ColumnCount = 2, .RowCount = 5}
        root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 58))
        root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 42))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 66))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 44))
        root.Controls.Add(_search, 0, 0)
        root.SetColumnSpan(_search, 2)
        root.Controls.Add(_category, 0, 1)
        _category.Items.Add("All equipment")
        _category.Items.Add("Uploaded equipment")
        _category.Items.AddRange(FactoryMapItemForm.Shapes.Keys.Cast(Of Object)().ToArray())
        root.Controls.Add(_list, 0, 2)
        root.Controls.Add(_preview, 1, 2)
        root.Controls.Add(_description, 1, 1)
        root.Controls.Add(_message, 0, 3)
        root.SetColumnSpan(_message, 2)
        Dim actions As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .FlowDirection = FlowDirection.RightToLeft, .WrapContents = False}
        Dim cancel = UiTheme.ActionButton("Cancel")
        cancel.DialogResult = DialogResult.Cancel
        Dim upload = UiTheme.ActionButton("Import OBJ / CAD")
        upload.Width = 155
        _place.Width = 140
        _delete.Width = 125
        actions.Controls.Add(cancel)
        actions.Controls.Add(_place)
        actions.Controls.Add(upload)
        actions.Controls.Add(_delete)
        For Each button As Button In actions.Controls.OfType(Of Button)()
            button.Height = 30
        Next
        root.Controls.Add(actions, 0, 4)
        root.SetColumnSpan(actions, 2)
        Controls.Add(root)
        AcceptButton = _place
        CancelButton = cancel
        For Each category In FactoryMapItemForm.Shapes
            For Each model In category.Value
                _entries.Add(New LibraryEntry With {.Asset = New FactoryMapAsset With {
                    .Name = model, .Category = category.Key, .Model = model, .Location = "User placed", .Source = "No linked acquisition source",
                    .BaseHeightMeters = FactoryMapMonitoring.DefaultMountHeight(model)
                }})
            Next
        Next
        Try
            For Each definition In FactoryMapCustomEquipmentStore.Load(_directory)
                _entries.Add(New LibraryEntry With {.Asset = FactoryMapCustomEquipmentStore.CreateAsset(definition)})
            Next
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is InvalidDataException
            ShowLibraryError("Custom equipment library could not be loaded. Built-in equipment is still available.", ex)
        End Try
        AddHandler _search.TextChanged, Sub() FilterEntries()
        AddHandler _category.SelectedIndexChanged, Sub() FilterEntries()
        AddHandler _list.SelectedIndexChanged, Sub() PreviewSelection()
        AddHandler _list.DoubleClick, Sub() ChooseSelection()
        AddHandler _place.Click, Sub() ChooseSelection()
        AddHandler _delete.Click, Sub() DeleteSelection()
        AddHandler upload.Click, Sub() ImportEquipment()
        _category.SelectedIndex = 0
    End Sub

    Private Sub ShowLibraryError(message As String, ex As Exception)
        _message.ForeColor = UiTheme.Red
        _message.Text = message & Environment.NewLine & ex.Message
        MessageBox.Show(Me, _message.Text, "Equipment library", MessageBoxButtons.OK, MessageBoxIcon.Error)
    End Sub

    Private Sub FilterEntries()
        Dim selected = TryCast(_list.SelectedItem, LibraryEntry)
        Dim query = _search.Text.Trim()
        Dim category = CStr(_category.SelectedItem)
        _list.BeginUpdate()
        _list.Items.Clear()
        For Each entry In _entries.Where(Function(item) (query = "" OrElse item.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)) AndAlso
            (category = "All equipment" OrElse (category = "Uploaded equipment" AndAlso item.Asset.CustomEquipment IsNot Nothing) OrElse category = item.Asset.Category)).
            OrderBy(Function(item) item.ToString())
            _list.Items.Add(entry)
        Next
        If selected IsNot Nothing AndAlso _list.Items.Contains(selected) Then
            _list.SelectedItem = selected
        ElseIf _list.Items.Count > 0 Then
            _list.SelectedIndex = 0
        Else
            PreviewSelection()
        End If
        _list.EndUpdate()
    End Sub

    Private Sub PreviewSelection()
        Dim entry = TryCast(_list.SelectedItem, LibraryEntry)
        _place.Enabled = entry IsNot Nothing
        _delete.Enabled = entry IsNot Nothing AndAlso entry.Asset.CustomEquipment IsNot Nothing
        _preview.SetAsset(If(entry Is Nothing, Nothing, entry.Asset))
        If entry Is Nothing Then
            _description.Text = "No matching equipment."
            Return
        End If
        Dim size = FactoryMapEquipmentSizes.Dimensions(entry.Asset)
        _description.Text = $"{size.Length:0.##} x {size.Width:0.##} x {size.Height:0.##} m"
        If _message.ForeColor <> UiTheme.Red Then
            _message.Text = If(entry.Asset.CustomEquipment Is Nothing,
                "Place at the center of the visible map, then drag to move. Handles resize / rotate; Edit Item sets exact properties and system links.",
                If(entry.Asset.CustomEquipment.GeometryModel = FactoryMapCustomEquipmentStore.CadModelName,
                    entry.Asset.CustomEquipment.ImportNotes,
                If(entry.Asset.CustomEquipment.GeometryModel <> "",
                    "Hand-built equipment geometry renders in 2D and 3D. The photo is retained as a reference, not drawn on the machine. Overall dimensions are user supplied; internal parts are schematic.",
                    "Legacy image-based silhouette only, not reconstructed equipment. Import OBJ/CAD geometry or select the built-in Sidel template.")))
        End If
    End Sub

    Private Sub ChooseSelection()
        Dim entry = TryCast(_list.SelectedItem, LibraryEntry)
        If entry Is Nothing Then Return
        _result = entry.Asset.Clone()
        DialogResult = DialogResult.OK
    End Sub

    Private Sub ImportEquipment()
        Using dialog As New FactoryMapCustomEquipmentForm(True)
            If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
            Try
                FactoryMapCustomEquipmentStore.Save(dialog.Definition, _directory)
                AddCustomDefinition(dialog.Definition)
                _message.ForeColor = UiTheme.MutedInk
                _message.Text = "Equipment generated and saved to the reusable library. Choose Place selected to add it to this map."
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is InvalidDataException
                ShowLibraryError("The uploaded equipment could not be saved.", ex)
            End Try
        End Using
    End Sub

    Private Sub DeleteSelection()
        Dim entry = TryCast(_list.SelectedItem, LibraryEntry)
        If entry Is Nothing OrElse entry.Asset.CustomEquipment Is Nothing Then Return
        Dim name = entry.Asset.CustomEquipment.Name
        If MessageBox.Show(Me,
            $"Delete '{name}' from the reusable equipment library? Existing map copies will remain. This cannot be undone.",
            "Delete uploaded equipment", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) <> DialogResult.Yes Then Return
        Try
            RemoveCustomDefinition(entry.Asset.CustomEquipment)
            _message.ForeColor = UiTheme.MutedInk
            _message.Text = $"Deleted '{name}' from the reusable library. Existing map copies are unchanged."
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is InvalidDataException
            ShowLibraryError("The uploaded equipment could not be deleted.", ex)
        End Try
    End Sub

    Friend Sub RemoveCustomDefinition(definition As FactoryMapCustomEquipment)
        FactoryMapCustomEquipmentStore.Delete(definition, _directory)
        _entries.RemoveAll(Function(entry) entry.Asset.CustomEquipment IsNot Nothing AndAlso entry.Asset.CustomEquipment.Id = definition.Id)
        FilterEntries()
    End Sub

    Friend Sub AddCustomDefinition(definition As FactoryMapCustomEquipment)
        Dim entry As New LibraryEntry With {.Asset = FactoryMapCustomEquipmentStore.CreateAsset(definition)}
        _entries.Add(entry)
        _search.Clear()
        _category.SelectedItem = "Uploaded equipment"
        FilterEntries()
        _list.SelectedItem = entry
    End Sub
End Class

Friend NotInheritable Class FactoryMapCustomEquipmentForm
    Inherits Form

    Private ReadOnly _name As New TextBox With {.Dock = DockStyle.Fill, .MaxLength = 120}
    Private ReadOnly _geometry As New ComboBox With {.Dock = DockStyle.Fill, .DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _length As NumericUpDown = DimensionInput(2)
    Private ReadOnly _width As NumericUpDown = DimensionInput(1)
    Private ReadOnly _height As NumericUpDown = DimensionInput(1)
    Private ReadOnly _generatedPreview As New FactoryMapItemPreview With {.Dock = DockStyle.Fill}
    Private ReadOnly _error As New Label With {.Dock = DockStyle.Fill, .ForeColor = UiTheme.Red}
    Private ReadOnly _cadImport As Boolean
    Private _sourcePath As String
    Private _definition As FactoryMapCustomEquipment

    Public ReadOnly Property Definition As FactoryMapCustomEquipment
        Get
            Return _definition
        End Get
    End Property

    Public Sub New()
        Me.New(False)
    End Sub

    Public Sub New(cadImport As Boolean)
        _cadImport = cadImport
        Text = If(cadImport, "Import Equipment from OBJ / DXF / DWG", "Generate Equipment from PNG/JPG")
        Size = New Size(760, 650)
        MinimumSize = New Size(650, 540)
        StartPosition = FormStartPosition.CenterParent
        Font = New Font("Segoe UI", 9)
        Dim root As New TableLayoutPanel With {.Dock = DockStyle.Fill, .Padding = New Padding(14), .ColumnCount = 2, .RowCount = 9}
        root.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 170))
        root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        Dim upload = UiTheme.ActionButton(If(cadImport, "Choose OBJ / DXF / DWG...", "Choose PNG/JPG..."))
        upload.Dock = DockStyle.Fill
        AddHandler upload.Click, Sub() ChooseImage()
        root.Controls.Add(upload, 0, 0)
        root.SetColumnSpan(upload, 2)
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 42))
        _geometry.Items.AddRange(If(cadImport, New Object() {"Z up (CAD default)", "Y up (common OBJ)"},
                                   New Object() {"Image-based silhouette", FactoryMapCustomEquipmentStore.SidelModelName}))
        _geometry.SelectedIndex = 0
        Dim rows As (Name As String, Input As Control)() = {("Equipment name", _name), (If(cadImport, "Source up axis", "Equipment model"), _geometry),
            ("Length (metres)", _length), ("Width (metres)", _width), ("Height (metres)", _height)}
        For index = 0 To rows.Length - 1
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 36))
            root.Controls.Add(New Label With {.Dock = DockStyle.Fill, .Text = rows(index).Name, .TextAlign = ContentAlignment.MiddleLeft}, 0, index + 1)
            root.Controls.Add(rows(index).Input, 1, index + 1)
        Next
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        root.Controls.Add(_generatedPreview, 0, 6)
        root.SetColumnSpan(_generatedPreview, 2)
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 70))
        root.Controls.Add(_error, 0, 7)
        root.SetColumnSpan(_error, 2)
        _error.ForeColor = UiTheme.MutedInk
        _error.Text = "Choose the hand-built Sidel model for actual 3D machine geometry; the uploaded photo is retained only as a reference. Image-based silhouette is not a real machine model. The Sidel model's detailed parts are schematic, with an 8 x 2.5 x 2.5 m overall size."
        If cadImport Then _error.Text = "Import polygon OBJ, or supported DXF/DWG geometry. CAD 2D outlines remain flat. Set actual dimensions in metres. Solids must be exported as polygon meshes/3DFACE; textures and annotations are not imported."
        Dim actions As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .FlowDirection = FlowDirection.RightToLeft}
        Dim save = UiTheme.ActionButton("Generate equipment", True)
        save.Width = 175
        Dim cancel = UiTheme.ActionButton("Cancel")
        cancel.DialogResult = DialogResult.Cancel
        actions.Controls.Add(cancel)
        actions.Controls.Add(save)
        For Each button As Button In actions.Controls.OfType(Of Button)()
            button.Height = 30
        Next
        AddHandler save.Click, Sub() ApplyDefinition()
        AddHandler _geometry.SelectedIndexChanged,
            Sub()
                If _cadImport Then
                    If _sourcePath IsNot Nothing Then
                        Try
                            LoadGeometry(_sourcePath)
                        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                            _error.ForeColor = UiTheme.Red
                            _error.Text = ex.Message
                        End Try
                    End If
                    Return
                End If
                If _geometry.SelectedIndex = 1 Then
                    _length.Value = 8
                    _width.Value = 2.5D
                    _height.Value = 2.5D
                End If
                RefreshGeneratedPreview()
            End Sub
        AddHandler _length.ValueChanged, Sub() RefreshGeneratedPreview()
        AddHandler _width.ValueChanged, Sub() RefreshGeneratedPreview()
        AddHandler _height.ValueChanged, Sub() RefreshGeneratedPreview()
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 42))
        root.Controls.Add(actions, 0, 8)
        root.SetColumnSpan(actions, 2)
        Controls.Add(root)
        AcceptButton = save
        CancelButton = cancel
    End Sub

    Private Shared Function DimensionInput(value As Decimal) As NumericUpDown
        Return New NumericUpDown With {.Dock = DockStyle.Fill, .Minimum = 0.05D, .Maximum = 100, .DecimalPlaces = 2, .Increment = 0.1D, .Value = value}
    End Function

    Private Sub ChooseImage()
        Using dialog As New OpenFileDialog With {.Filter = If(_cadImport, "Equipment models (*.obj;*.dxf;*.dwg)|*.obj;*.dxf;*.dwg|OBJ mesh (*.obj)|*.obj|AutoCAD DXF (*.dxf)|*.dxf|AutoCAD DWG (*.dwg)|*.dwg",
            "Equipment images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg"), .CheckFileExists = True, .Multiselect = False}
            If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
            Try
                If _cadImport Then
                    LoadGeometry(dialog.FileName)
                Else
                    LoadImage(dialog.FileName)
                End If
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is InvalidDataException
                _error.ForeColor = UiTheme.Red
                _error.Text = ex.Message
            End Try
        End Using
    End Sub

    Friend Sub LoadGeometry(path As String)
        _definition = Nothing
        _generatedPreview.SetAsset(Nothing)
        _sourcePath = path
        Dim definition = FactoryMapCadImporter.ImportFile(path, _geometry.SelectedIndex = 1)
        _name.Text = definition.Name
        _length.Value = CDec(definition.LengthMeters)
        _width.Value = CDec(definition.WidthMeters)
        _height.Minimum = If(definition.CadGeometry.IsPlan, 0D, 0.05D)
        _height.Value = CDec(definition.HeightMeters)
        _height.Enabled = Not definition.CadGeometry.IsPlan
        _definition = definition
        _sourcePath = path
        RefreshGeneratedPreview()
        _error.ForeColor = UiTheme.MutedInk
        _error.Text = definition.ImportNotes
    End Sub

    Friend Sub LoadImage(path As String)
        Dim definition = FactoryMapCustomEquipmentStore.ImportImage(path)
        _definition = definition
        _name.Text = definition.Name
        RefreshGeneratedPreview()
        _error.ForeColor = UiTheme.MutedInk
        _error.Text = "Photo loaded. Select the Sidel equipment model to build actual machine bodies, rotary sections, conveyor, elevator, platform, stairs and supports. Its geometry is drawn in both 2D and 3D; the photo is not pasted on it."
    End Sub

    Private Sub RefreshGeneratedPreview()
        If _definition Is Nothing Then Return
        Dim candidate = _definition.Clone()
        If Not _cadImport Then candidate.GeometryModel = If(_geometry.SelectedIndex = 1, FactoryMapCustomEquipmentStore.SidelModelName, "")
        candidate.LengthMeters = CSng(_length.Value)
        candidate.WidthMeters = CSng(_width.Value)
        candidate.HeightMeters = CSng(_height.Value)
        _generatedPreview.SetAsset(FactoryMapCustomEquipmentStore.CreateAsset(candidate))
    End Sub

    Private Sub ApplyDefinition()
        Try
            If _definition Is Nothing Then Throw New InvalidDataException(If(_cadImport, "Choose an OBJ/DXF/DWG model first.", "Choose a PNG/JPG image first."))
            Dim candidate = _definition.Clone()
            candidate.Name = _name.Text.Trim()
            If Not _cadImport Then candidate.GeometryModel = If(_geometry.SelectedIndex = 1, FactoryMapCustomEquipmentStore.SidelModelName, "")
            candidate.LengthMeters = CSng(_length.Value)
            candidate.WidthMeters = CSng(_width.Value)
            candidate.HeightMeters = CSng(_height.Value)
            FactoryMapCustomEquipmentStore.Validate(candidate)
            _definition = candidate
            DialogResult = DialogResult.OK
        Catch ex As InvalidDataException
            _error.ForeColor = UiTheme.Red
            _error.Text = ex.Message
        End Try
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            _generatedPreview.SetAsset(Nothing)
        End If
        MyBase.Dispose(disposing)
    End Sub
End Class
