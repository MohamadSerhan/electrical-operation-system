Imports System.Drawing
Imports System.Globalization
Imports System.Windows.Forms

Friend NotInheritable Class FactoryMapConnectorForm
    Inherits Form

    Private ReadOnly _name As New TextBox With {.Width = 220, .MaxLength = 120}
    Private ReadOnly _kind As New ComboBox With {.Width = 220, .DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _thickness As New NumericUpDown With {.Width = 120, .Minimum = 2, .Maximum = 3000, .DecimalPlaces = 0}
    Private ReadOnly _height As New NumericUpDown With {.Width = 120, .Minimum = 0, .Maximum = 10000, .DecimalPlaces = 0}
    Private ReadOnly _style As New ComboBox With {.Width = 220, .DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _count As New ComboBox With {.Width = 120, .DropDownStyle = ComboBoxStyle.DropDownList}
    Private ReadOnly _depth As New NumericUpDown With {.Width = 120, .Minimum = 5, .Maximum = 1000, .DecimalPlaces = 0}
    Private ReadOnly _sizeLabel As New Label With {.AutoSize = True, .Margin = New Padding(3, 8, 12, 3)}
    Private ReadOnly _styleLabel As New Label With {.Text = "Tray type", .AutoSize = True, .Margin = New Padding(3, 8, 12, 3)}
    Private ReadOnly _countLabel As New Label With {.Text = "Number of cables", .AutoSize = True, .Margin = New Padding(3, 8, 12, 3)}
    Private ReadOnly _depthLabel As New Label With {.Text = "Tray side height (mm)", .AutoSize = True, .Margin = New Padding(3, 8, 12, 3)}
    Private ReadOnly _swatch As New Button With {.Width = 220, .Height = 28, .FlatStyle = FlatStyle.Flat}
    Private _color As Color

    Public ReadOnly Property ConnectorName As String
        Get
            Return _name.Text.Trim()
        End Get
    End Property

    Public ReadOnly Property Kind As String
        Get
            Return CStr(_kind.SelectedItem)
        End Get
    End Property

    Public ReadOnly Property ColorHex As String
        Get
            Return "#" & _color.R.ToString("X2", CultureInfo.InvariantCulture) & _color.G.ToString("X2", CultureInfo.InvariantCulture) &
                _color.B.ToString("X2", CultureInfo.InvariantCulture)
        End Get
    End Property

    Public ReadOnly Property ThicknessMeters As Single
        Get
            Return CSng(_thickness.Value) / 1000.0F
        End Get
    End Property

    Public ReadOnly Property HeightMeters As Single
        Get
            Return CSng(_height.Value) / 1000.0F
        End Get
    End Property

    Public ReadOnly Property StyleName As String
        Get
            Return If(Kind = FactoryMapConnectors.CableTrayKind, If(_style.SelectedIndex = 0, FactoryMapConnectors.MeshStyle, FactoryMapConnectors.NormalStyle), "")
        End Get
    End Property

    Public ReadOnly Property CableCount As Integer
        Get
            Return If(Kind = FactoryMapConnectors.CableKind, _count.SelectedIndex + 1, 1)
        End Get
    End Property

    Public ReadOnly Property DepthMeters As Single
        Get
            Return CSng(_depth.Value) / 1000.0F
        End Get
    End Property

    Public Sub New(title As String, kind As String, name As String, colorHex As String, thicknessMeters As Single, heightMeters As Single,
                   Optional style As String = "", Optional count As Integer = 1, Optional depthMeters As Single = 0.06F)
        Text = title
        FormBorderStyle = FormBorderStyle.FixedDialog
        StartPosition = FormStartPosition.CenterParent
        MaximizeBox = False
        MinimizeBox = False
        ShowInTaskbar = False
        AutoSize = True
        AutoSizeMode = AutoSizeMode.GrowAndShrink
        Padding = New Padding(12)
        _kind.Items.AddRange({FactoryMapConnectors.PipeKind, FactoryMapConnectors.ConveyorKind, FactoryMapConnectors.CableTrayKind, FactoryMapConnectors.CableKind})
        _style.Items.AddRange({FactoryMapConnectors.MeshStyle & " (wire mesh)", FactoryMapConnectors.NormalStyle & " (solid)"})
        _style.SelectedIndex = If(String.Equals(style, FactoryMapConnectors.MeshStyle, StringComparison.OrdinalIgnoreCase), 0, 1)
        _count.Items.AddRange({"Single", "x2", "x3", "x4", "x5"})
        _count.SelectedIndex = Math.Clamp(count, 1, 5) - 1
        _kind.SelectedItem = kind
        _name.Text = name
        Try
            _color = ColorTranslator.FromHtml(colorHex)
        Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is FormatException
            _color = Color.SteelBlue
        End Try
        _thickness.Value = Math.Clamp(CDec(Math.Round(thicknessMeters * 1000)), _thickness.Minimum, _thickness.Maximum)
        _height.Value = Math.Clamp(CDec(Math.Round(heightMeters * 1000)), _height.Minimum, _height.Maximum)
        _depth.Value = Math.Clamp(CDec(Math.Round(depthMeters * 1000)), _depth.Minimum, _depth.Maximum)
        UpdateSwatch()
        AddHandler _swatch.Click, AddressOf PickColor
        AddHandler _kind.SelectedIndexChanged, AddressOf KindChanged

        Dim table As New TableLayoutPanel With {.ColumnCount = 2, .AutoSize = True, .Dock = DockStyle.Fill}
        Dim rows = {(CType(New Label With {.Text = "Name", .AutoSize = True, .Margin = New Padding(3, 8, 12, 3)}, Control), CType(_name, Control)),
            (New Label With {.Text = "Type", .AutoSize = True, .Margin = New Padding(3, 8, 12, 3)}, _kind),
            (New Label With {.Text = "Colour", .AutoSize = True, .Margin = New Padding(3, 8, 12, 3)}, _swatch),
            (_sizeLabel, _thickness),
            (_styleLabel, _style),
            (_countLabel, _count),
            (_depthLabel, _depth),
            (New Label With {.Text = "Height above floor (mm)", .AutoSize = True, .Margin = New Padding(3, 8, 12, 3)}, _height)}
        For Each row In rows
            row.Item2.Margin = New Padding(3, 5, 3, 3)
            table.Controls.Add(row.Item1)
            table.Controls.Add(row.Item2)
        Next
        Dim ok As New Button With {.Text = "OK", .DialogResult = DialogResult.OK, .Width = 80}
        Dim cancel As New Button With {.Text = "Cancel", .DialogResult = DialogResult.Cancel, .Width = 80}
        Dim buttons As New FlowLayoutPanel With {.FlowDirection = FlowDirection.RightToLeft, .AutoSize = True, .Dock = DockStyle.Bottom}
        buttons.Controls.AddRange({cancel, ok})
        Controls.Add(table)
        Controls.Add(buttons)
        AcceptButton = ok
        CancelButton = cancel
        AddHandler ok.Click, AddressOf ValidateInput
        UpdateFieldVisibility()
    End Sub

    Private Sub KindChanged(sender As Object, e As EventArgs)
        ' Offer sensible defaults only if the user has not customised the values yet.
        Dim defaults = New Dictionary(Of String, Integer) From {
            {FactoryMapConnectors.PipeKind, 120}, {FactoryMapConnectors.ConveyorKind, 450},
            {FactoryMapConnectors.CableTrayKind, 300}, {FactoryMapConnectors.CableKind, 16}}
        If defaults.ContainsValue(CInt(_thickness.Value)) Then _thickness.Value = defaults(Kind)
        UpdateFieldVisibility()
    End Sub

    Private Sub UpdateFieldVisibility()
        Dim tray = Kind = FactoryMapConnectors.CableTrayKind
        Dim cable = Kind = FactoryMapConnectors.CableKind
        _sizeLabel.Text = If(tray, "Tray width (mm)", If(cable, "Cable diameter (mm)", "Thickness / width (mm)"))
        _styleLabel.Visible = tray : _style.Visible = tray
        _depthLabel.Visible = tray : _depth.Visible = tray
        _countLabel.Visible = cable : _count.Visible = cable
    End Sub

    Private Sub PickColor(sender As Object, e As EventArgs)
        Using dialog As New ColorDialog With {.Color = _color, .FullOpen = True}
            If dialog.ShowDialog(Me) <> DialogResult.OK Then Return
            _color = dialog.Color
            UpdateSwatch()
        End Using
    End Sub

    Private Sub UpdateSwatch()
        _swatch.BackColor = _color
        _swatch.ForeColor = If(_color.GetBrightness() > 0.55F, Color.Black, Color.White)
        _swatch.Text = ColorHex & "  (click to change)"
    End Sub

    Private Sub ValidateInput(sender As Object, e As EventArgs)
        If _name.Text.Trim().Length = 0 Then
            MessageBox.Show(Me, "Enter a name.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning)
            DialogResult = DialogResult.None
        End If
    End Sub
End Class
