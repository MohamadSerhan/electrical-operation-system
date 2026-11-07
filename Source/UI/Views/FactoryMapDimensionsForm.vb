Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Windows.Forms

Friend NotInheritable Class FactoryMapDimensionsForm
    Inherits Form

    Private ReadOnly _grid As New DataGridView()
    Private ReadOnly _note As Label
    Public Property Saved As Boolean
        Get
            Return _saved
        End Get
        Private Set(value As Boolean)
            _saved = value
        End Set
    End Property
    Public Property Dimensions As FactoryMapDimensions
        Get
            Return _dimensions
        End Get
        Private Set(value As FactoryMapDimensions)
            _dimensions = value
        End Set
    End Property
    Private _saved As Boolean
    Private _dimensions As FactoryMapDimensions

    Public Sub New()
        Text = "Factory Map — Provisional Dimensions"
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.Sizable
        MinimumSize = New Size(680, 460)
        Size = New Size(760, 520)
        ShowInTaskbar = False
        BackColor = UiTheme.Canvas

        Dim root As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 3,
            .Padding = New Padding(12),
            .BackColor = UiTheme.Canvas
        }
        root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 48.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 48.0F))
        Controls.Add(root)

        _note = UiTheme.Label("UNVERIFIED visualization assumptions, not surveyed dimensions. " &
                              "Equipment rows scale the per-model nominal sizes relative to the original category defaults. " &
                              "Wall rows seed new building layouts only; edit existing walls in Building > Properties.",
                              New Font("Segoe UI", 9.0F, FontStyle.Bold), UiTheme.Amber, False)
        _note.Dock = DockStyle.Fill
        _note.AutoEllipsis = True
        root.Controls.Add(_note, 0, 0)

        Dim existing = FactoryMapDimensionStore.Load()
        ConfigureGrid()
        For Each component In existing.Components
            _grid.Rows.Add(component.Name,
                If(component.LengthMeters = 0, String.Empty, component.LengthMeters.ToString("0.##", CultureInfo.InvariantCulture)),
                component.WidthMeters.ToString("0.##", CultureInfo.InvariantCulture),
                component.HeightMeters.ToString("0.##", CultureInfo.InvariantCulture))
        Next
        root.Controls.Add(_grid, 0, 1)

        Dim actions As New FlowLayoutPanel With {
            .Dock = DockStyle.Fill,
            .FlowDirection = FlowDirection.RightToLeft,
            .WrapContents = False,
            .BackColor = UiTheme.Canvas,
            .Padding = New Padding(0, 5, 0, 0)
        }
        Dim save = UiTheme.Button("Save provisional dimensions", True)
        save.Width = 210
        save.Height = 36
        AddHandler save.Click, AddressOf SaveDimensions
        Dim cancel = UiTheme.Button("Cancel", False)
        cancel.Width = 90
        cancel.Height = 36
        AddHandler cancel.Click, Sub(sender, e) Close()
        actions.Controls.Add(save)
        actions.Controls.Add(cancel)
        root.Controls.Add(actions, 0, 2)
    End Sub

    Private Sub ConfigureGrid()
        _grid.Dock = DockStyle.Fill
        _grid.AllowUserToAddRows = False
        _grid.AllowUserToDeleteRows = False
        _grid.AllowUserToResizeRows = False
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        _grid.RowHeadersVisible = False
        _grid.SelectionMode = DataGridViewSelectionMode.CellSelect
        _grid.BackgroundColor = Color.White
        _grid.EnableHeadersVisualStyles = False
        _grid.ColumnHeadersDefaultCellStyle.BackColor = UiTheme.SurfaceMuted
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = UiTheme.Ink
        _grid.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "Component", .HeaderText = "Component", .ReadOnly = True, .FillWeight = 140})
        _grid.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "Length", .HeaderText = "Length (m)", .FillWeight = 90})
        _grid.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "Width", .HeaderText = "Width (m)", .FillWeight = 90})
        _grid.Columns.Add(New DataGridViewTextBoxColumn With {.Name = "Height", .HeaderText = "Height (m)", .FillWeight = 90})
    End Sub

    Private Sub SaveDimensions(sender As Object, e As EventArgs)
        Try
            If _grid.IsCurrentCellInEditMode Then _grid.EndEdit()
            Dim dimensions = New FactoryMapDimensions()
            For Each row As DataGridViewRow In _grid.Rows
                Dim name = Convert.ToString(row.Cells("Component").Value, CultureInfo.InvariantCulture)
                dimensions.Components.Add(New FactoryMapComponentDimension With {
                    .Name = name,
                    .LengthMeters = ParseDimension(row.Cells("Length").Value, name, "length", allowBlank:=True),
                    .WidthMeters = ParseDimension(row.Cells("Width").Value, name, "width"),
                    .HeightMeters = ParseDimension(row.Cells("Height").Value, name, "height")
                })
            Next
            dimensions.Validate()
            FactoryMapDimensionStore.Save(dimensions)
            Me.Dimensions = dimensions
            Me.Saved = True
            Close()
        Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is FormatException OrElse
                                  TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            MessageBox.Show(Me, "Provisional dimensions were not saved." & Environment.NewLine & ex.Message,
                            "Invalid or unavailable dimensions", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Shared Function ParseDimension(value As Object, component As String, dimension As String,
                                           Optional allowBlank As Boolean = False) As Double
        Dim text = Convert.ToString(value, CultureInfo.InvariantCulture)
        If allowBlank AndAlso String.IsNullOrWhiteSpace(text) Then Return 0
        Dim parsed As Double
        If Not Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, parsed) OrElse
           Not Double.IsFinite(parsed) Then
            Throw New FormatException(component & " " & dimension & " must be a finite number in meters.")
        End If
        Return parsed
    End Function
End Class
