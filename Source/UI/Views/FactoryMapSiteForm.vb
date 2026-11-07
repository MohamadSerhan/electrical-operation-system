Imports System.Drawing
Imports System.IO
Imports System.Windows.Forms

Friend NotInheritable Class FactoryMapSiteForm
    Inherits Form

    Private ReadOnly _width As New NumericUpDown With {.Minimum = 100, .Maximum = 400, .Increment = 25, .Dock = DockStyle.Fill}
    Private ReadOnly _depth As New NumericUpDown With {.Minimum = 100, .Maximum = 400, .Increment = 25, .Dock = DockStyle.Fill}
    Private _color As String
    Private _settings As FactoryMapSiteSettings

    Public ReadOnly Property Settings As FactoryMapSiteSettings
        Get
            Return _settings
        End Get
    End Property

    Public Sub New(settings As FactoryMapSiteSettings)
        Text = "Factory Site - Expand Ground"
        Size = New Size(540, 330)
        MinimumSize = Size
        StartPosition = FormStartPosition.CenterParent
        Font = New Font("Segoe UI", 9)
        BackColor = UiTheme.Surface
        _color = settings.GroundColor
        _width.Value = settings.WidthPercent
        _depth.Value = settings.DepthPercent
        Dim root As New TableLayoutPanel With {.Dock = DockStyle.Fill, .Padding = New Padding(16), .ColumnCount = 2, .RowCount = 5}
        root.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 180))
        root.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        For index = 0 To 2
            root.RowStyles.Add(New RowStyle(SizeType.Absolute, 40))
        Next
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 44))
        root.Controls.Add(New Label With {.Text = "Site length (%)", .Dock = DockStyle.Fill}, 0, 0)
        root.Controls.Add(_width, 1, 0)
        root.Controls.Add(New Label With {.Text = "Site depth (%)", .Dock = DockStyle.Fill}, 0, 1)
        root.Controls.Add(_depth, 1, 1)
        Dim colorButton = UiTheme.ActionButton("Ground color")
        colorButton.Dock = DockStyle.Fill
        AddHandler colorButton.Click, Sub(sender, e)
                                          Using chooser As New ColorDialog With {.FullOpen = True, .Color = ColorTranslator.FromHtml(_color)}
                                              If chooser.ShowDialog(Me) <> DialogResult.OK Then Return
                                              _color = "#" & chooser.Color.R.ToString("X2") & chooser.Color.G.ToString("X2") & chooser.Color.B.ToString("X2")
                                              colorButton.Text = _color
                                          End Using
                                      End Sub
        root.Controls.Add(colorButton, 1, 2)
        Dim note As New Label With {.Text = "100% = original footprint. Expansion adds ground right/bottom without moving or resizing items. Positions use ORIGINAL-footprint percentages (up to 400%). Ground also grows to include placed item centers. Site settings save separately from Building and Edit Map.", .Dock = DockStyle.Fill}
        root.Controls.Add(note, 0, 3)
        root.SetColumnSpan(note, 2)
        Dim buttons As New FlowLayoutPanel With {.Dock = DockStyle.Fill, .FlowDirection = FlowDirection.RightToLeft}
        Dim save = UiTheme.ActionButton("Save site", True)
        save.Width = 110
        Dim cancel = UiTheme.ActionButton("Cancel")
        cancel.Width = 100
        cancel.DialogResult = DialogResult.Cancel
        AddHandler save.Click, Sub(sender, e)
                                  _settings = New FactoryMapSiteSettings With {.WidthPercent = CInt(_width.Value), .DepthPercent = CInt(_depth.Value), .GroundColor = _color}
                                  _settings.Validate()
                                  DialogResult = DialogResult.OK
                              End Sub
        buttons.Controls.Add(save)
        buttons.Controls.Add(cancel)
        root.Controls.Add(buttons, 0, 4)
        root.SetColumnSpan(buttons, 2)
        Controls.Add(root)
        AcceptButton = save
        CancelButton = cancel
    End Sub
End Class
