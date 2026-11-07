Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Threading.Tasks
Imports System.Windows.Forms

Public NotInheritable Class EquipmentMasterDataEditorForm
    Inherits Form

    Private Shared ReadOnly EquipmentFields As (Caption As String, Key As String)() = {
        ("Equipment name", "EquipmentName"),
        ("Transformer manufacturer", "Manufacturer"),
        ("Transformer model", "Model"),
        ("Transformer serial number", "SerialNumber"),
        ("Rated capacity (kVA)", "RatedKva"),
        ("Primary rated voltage", "PrimaryRatedVoltage"),
        ("Secondary rated voltage", "SecondaryRatedVoltage"),
        ("Measurement side", "ElectricalSide"),
        ("Meter manufacturer", "MeterManufacturer"),
        ("Meter model", "MeterModel"),
        ("Meter serial / identity", "MeterIdentity"),
        ("Site / installation location", "SiteName"),
        ("Verified temperature sensor source", "TemperatureSensorSource")
    }
    Private Shared ReadOnly FactoryMetadataKeys As String() = {"EquipmentName", "SiteName"}

    Private ReadOnly _session As UserSession
    Private ReadOnly _saveSettings As Func(Of RecordingSettings, Task)
    Private ReadOnly _inputs As New Dictionary(Of String, TextBox)(StringComparer.Ordinal)
    Private ReadOnly _message As New Label()
    Private ReadOnly _dirtyLabel As New Label()
    Private ReadOnly _saveButton As New Button()
    Private ReadOnly _equipment As AnalyzerEquipmentSettings
    Private ReadOnly _analyzerId As String
    Private _settings As RecordingSettings
    Private _loading As Boolean
    Private _dirty As Boolean
    Private _saved As Boolean

    Public Sub New(settings As RecordingSettings,
                   analyzerId As String,
                   session As UserSession,
                   saveSettings As Func(Of RecordingSettings, Task))
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        If session Is Nothing Then Throw New ArgumentNullException(NameOf(session))
        If saveSettings Is Nothing Then Throw New ArgumentNullException(NameOf(saveSettings))
        If Not {"T1", "T2", "T3", "T4", "TOTAL"}.Contains(analyzerId, StringComparer.Ordinal) Then
            Throw New ArgumentException("Select a configured analyzer.", NameOf(analyzerId))
        End If

        _settings = settings
        _analyzerId = analyzerId
        _equipment = settings.GetAnalyzer(analyzerId)
        _session = session
        _saveSettings = saveSettings

        Text = "System Configuration - Equipment Information"
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.Sizable
        MinimizeBox = False
        MaximizeBox = False
        MinimumSize = New Size(720, 610)
        Size = New Size(900, 760)
        BackColor = UiTheme.Canvas
        Font = New Font("Segoe UI", 9.0F)

        BuildLayout()
        LoadEquipment()
    End Sub

    Public ReadOnly Property SavedSettings As RecordingSettings
        Get
            Return If(_saved, _settings, Nothing)
        End Get
    End Property

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        If Not _saved AndAlso _dirty AndAlso DialogResult <> DialogResult.OK Then
            Dim answer = MessageBox.Show(Me,
                "Discard unsaved equipment changes?",
                "Unsaved equipment changes",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning)
            If answer <> DialogResult.Yes Then
                e.Cancel = True
                Return
            End If
        End If
        MyBase.OnFormClosing(e)
    End Sub

    Private Sub BuildLayout()
        Dim header As New Panel With {
            .Dock = DockStyle.Top,
            .Height = 72,
            .Padding = New Padding(18, 10, 18, 4),
            .BackColor = UiTheme.Surface
        }
        Dim title = UiTheme.Label(
            If(_analyzerId = "TOTAL", "FACTORY EQUIPMENT INFORMATION", "TRANSFORMER EQUIPMENT INFORMATION (" & _analyzerId & ")"),
            New Font("Segoe UI", 12.0F, FontStyle.Bold),
            Icons.AccentBlue)
        title.Dock = DockStyle.Top
        title.Height = 26
        Dim subtitle = UiTheme.Label(
            "Equipment master data is shared with Recording & Historian and changes are audited.",
            New Font("Segoe UI", 9.0F),
            UiTheme.MutedInk)
        subtitle.Dock = DockStyle.Fill
        header.Controls.Add(subtitle)
        header.Controls.Add(title)
        Controls.Add(header)

        Dim fieldsPanel As New Panel With {
            .Dock = DockStyle.Fill,
            .AutoScroll = True,
            .Padding = New Padding(14, 10, 14, 10),
            .BackColor = UiTheme.Canvas
        }
        Controls.Add(fieldsPanel)

        Dim keys = If(_analyzerId = "TOTAL",
            FactoryMetadataKeys,
            EquipmentFields.Select(Function(field) field.Key).ToArray())
        Dim grid As New TableLayoutPanel With {
            .Dock = DockStyle.Top,
            .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .ColumnCount = 2,
            .RowCount = CInt(Math.Ceiling(keys.Length / 2.0R)),
            .BackColor = Color.Transparent,
            .Margin = Padding.Empty
        }
        grid.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50.0F))
        grid.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 50.0F))
        For row = 0 To grid.RowCount - 1
            grid.RowStyles.Add(New RowStyle(SizeType.Absolute, 72.0F))
        Next
        For index = 0 To keys.Length - 1
            Dim key = keys(index)
            Dim field = EquipmentFields.Single(Function(item) item.Key = key)
            grid.Controls.Add(CreateFieldCard(field.Caption, field.Key), index Mod 2, index \ 2)
        Next
        fieldsPanel.Controls.Add(grid)

        Dim footer As New Panel With {
            .Dock = DockStyle.Bottom,
            .Height = 74,
            .Padding = New Padding(18, 10, 18, 10),
            .BackColor = UiTheme.Surface
        }
        Dim cancelButton = UiTheme.Button("CANCEL", False)
        cancelButton.Size = New Size(112, 38)
        cancelButton.Dock = DockStyle.Right
        cancelButton.Margin = New Padding(8, 0, 0, 0)
        AddHandler cancelButton.Click, Sub(sender, e) Close()
        _saveButton.Text = "SAVE EQUIPMENT"
        _saveButton.Size = New Size(152, 38)
        _saveButton.Dock = DockStyle.Right
        _saveButton.FlatStyle = FlatStyle.Flat
        _saveButton.BackColor = UiTheme.Blue
        _saveButton.ForeColor = Color.White
        _saveButton.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        _saveButton.UseVisualStyleBackColor = False
        _saveButton.FlatAppearance.BorderSize = 0
        _saveButton.Enabled = PlcConfigurationAccess.CanEdit(_session.RoleName, _session.IsOfflinePreview)
        AddHandler _saveButton.Click, AddressOf OnSaveClicked

        _dirtyLabel.Dock = DockStyle.Left
        _dirtyLabel.AutoSize = True
        _dirtyLabel.TextAlign = ContentAlignment.MiddleLeft
        _dirtyLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        _message.Dock = DockStyle.Fill
        _message.Margin = New Padding(14, 0, 14, 0)
        _message.TextAlign = ContentAlignment.MiddleLeft
        _message.ForeColor = If(_saveButton.Enabled, UiTheme.MutedInk, UiTheme.Amber)
        _message.Text = If(_saveButton.Enabled,
            "Only administrator changes can be saved.",
            "READ ONLY • Administrator role required to edit equipment metadata.")
        footer.Controls.Add(_message)
        footer.Controls.Add(_dirtyLabel)
        footer.Controls.Add(cancelButton)
        footer.Controls.Add(_saveButton)
        Controls.Add(footer)
        AcceptButton = _saveButton
    End Sub

    Private Function CreateFieldCard(caption As String, key As String) As RoundedPanel
        Dim card As New RoundedPanel With {
            .Dock = DockStyle.Fill,
            .Margin = New Padding(4),
            .Padding = New Padding(10, 5, 10, 6),
            .Radius = 8,
            .BackColor = Color.FromArgb(250, 252, 255),
            .BorderColor = Color.FromArgb(213, 226, 244)
        }
        Dim label = UiTheme.Label(
            caption.ToUpperInvariant(),
            New Font("Segoe UI", 8.0F, FontStyle.Bold),
            Color.FromArgb(42, 75, 125))
        label.Dock = DockStyle.Top
        label.Height = 18
        Dim input As New TextBox With {
            .Name = key,
            .Dock = DockStyle.Top,
            .Height = 26,
            .Margin = Padding.Empty,
            .BorderStyle = BorderStyle.FixedSingle,
            .BackColor = Color.FromArgb(239, 244, 251),
            .ForeColor = UiTheme.Ink,
            .Font = New Font("Segoe UI", 9.5F),
            .MaxLength = 256
        }
        input.ReadOnly = Not PlcConfigurationAccess.CanEdit(_session.RoleName, _session.IsOfflinePreview)
        AddHandler input.TextChanged, AddressOf OnEquipmentInputChanged
        _inputs.Add(key, input)
        card.Controls.Add(input)
        card.Controls.Add(label)
        Return card
    End Function

    Private Sub LoadEquipment()
        _loading = True
        Try
            _inputs("EquipmentName").Text = _equipment.EquipmentName
            _inputs("SiteName").Text = _equipment.SiteName
            If _analyzerId <> "TOTAL" Then
                _inputs("Manufacturer").Text = _equipment.Manufacturer
                _inputs("Model").Text = _equipment.Model
                _inputs("SerialNumber").Text = _equipment.SerialNumber
                _inputs("RatedKva").Text = FormatNumber(_equipment.RatedKva)
                _inputs("PrimaryRatedVoltage").Text = FormatNumber(_equipment.PrimaryRatedVoltage)
                _inputs("SecondaryRatedVoltage").Text = FormatNumber(_equipment.SecondaryRatedVoltage)
                _inputs("ElectricalSide").Text = _equipment.ElectricalSide
                _inputs("MeterManufacturer").Text = _equipment.MeterManufacturer
                _inputs("MeterModel").Text = _equipment.MeterModel
                _inputs("MeterIdentity").Text = _equipment.MeterIdentity
                _inputs("TemperatureSensorSource").Text = _equipment.TemperatureSensorSource
            End If
        Finally
            _loading = False
        End Try
        SetDirty(False)
    End Sub

    Private Shared Function FormatNumber(value As Double?) As String
        Return If(value.HasValue, value.Value.ToString("0.###", CultureInfo.CurrentCulture), String.Empty)
    End Function

    Private Sub OnEquipmentInputChanged(sender As Object, e As EventArgs)
        If _loading Then Return
        SetDirty(True)
    End Sub

    Private Sub SetDirty(value As Boolean)
        _dirty = value
        _dirtyLabel.Text = If(value, "● Unsaved changes", "✔ No unsaved changes")
        _dirtyLabel.ForeColor = If(value, UiTheme.Amber, UiTheme.Green)
        _saveButton.Enabled = value AndAlso PlcConfigurationAccess.CanEdit(_session.RoleName, _session.IsOfflinePreview)
    End Sub

    Private Function BuildCandidate() As RecordingSettings
        Dim candidate As New RecordingSettings With {
            .AutomaticDailyExcelEnabled = _settings.AutomaticDailyExcelEnabled,
            .FactoryTimeZoneId = _settings.FactoryTimeZoneId,
            .DailyExportStabilizationMinutes = _settings.DailyExportStabilizationMinutes,
            .ExcelRetentionDays = _settings.ExcelRetentionDays,
            .AutomaticExcelDeletionEnabled = _settings.AutomaticExcelDeletionEnabled,
            .ExportFolderPath = _settings.ExportFolderPath,
            .ContinuousRawRecordingEnabled = _settings.ContinuousRawRecordingEnabled,
            .HistorianRecordingIntervalMilliseconds = _settings.HistorianRecordingIntervalMilliseconds,
            .HistorianRetentionDays = _settings.HistorianRetentionDays,
            .Equipment = _settings.Equipment.Select(Function(item) item.Clone()).ToList()
        }
        Dim equipment = candidate.GetAnalyzer(_analyzerId)
        equipment.EquipmentName = _inputs("EquipmentName").Text.Trim()
        equipment.SiteName = _inputs("SiteName").Text.Trim()
        If _analyzerId <> "TOTAL" Then
            equipment.Manufacturer = _inputs("Manufacturer").Text.Trim()
            equipment.Model = _inputs("Model").Text.Trim()
            equipment.SerialNumber = _inputs("SerialNumber").Text.Trim()
            equipment.RatedKva = ParseOptionalPositiveNumber(_inputs("RatedKva").Text, "Rated capacity (kVA)")
            equipment.PrimaryRatedVoltage = ParseOptionalPositiveNumber(_inputs("PrimaryRatedVoltage").Text, "Primary rated voltage")
            equipment.SecondaryRatedVoltage = ParseOptionalPositiveNumber(_inputs("SecondaryRatedVoltage").Text, "Secondary rated voltage")
            equipment.ElectricalSide = _inputs("ElectricalSide").Text.Trim()
            equipment.MeterManufacturer = _inputs("MeterManufacturer").Text.Trim()
            equipment.MeterModel = _inputs("MeterModel").Text.Trim()
            equipment.MeterIdentity = _inputs("MeterIdentity").Text.Trim()
            equipment.TemperatureSensorSource = _inputs("TemperatureSensorSource").Text.Trim()
        End If
        candidate.Validate()
        Return candidate
    End Function

    Private Shared Function ParseOptionalPositiveNumber(text As String, fieldName As String) As Double?
        If String.IsNullOrWhiteSpace(text) Then Return Nothing
        Dim value As Double
        If (Not Double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, value) AndAlso
            Not Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, value)) OrElse
            Not Double.IsFinite(value) OrElse value <= 0 Then
            Throw New InvalidDataException(fieldName & " must be a positive finite number, or left blank.")
        End If
        Return value
    End Function

    Private Async Sub OnSaveClicked(sender As Object, e As EventArgs)
        If Not PlcConfigurationAccess.CanEdit(_session.RoleName, _session.IsOfflinePreview) Then Return
        _saveButton.Enabled = False
        _message.Text = "Validating and saving equipment metadata…"
        _message.ForeColor = UiTheme.MutedInk
        Try
            Dim candidate = BuildCandidate()
            Await _saveSettings(candidate)
            _settings = candidate
            _saved = True
            If IsDisposed Then Return
            DialogResult = DialogResult.OK
            Close()
        Catch ex As InvalidDataException
            _message.Text = ex.Message
            _message.ForeColor = UiTheme.Red
        Catch ex As UnauthorizedAccessException
            _message.Text = "Permission denied while saving equipment metadata: " & ex.Message
            _message.ForeColor = UiTheme.Red
        Catch ex As IOException
            _message.Text = "Equipment metadata or its audit record could not be saved: " & ex.Message
            _message.ForeColor = UiTheme.Red
        Catch ex As InvalidOperationException
            _message.Text = "Equipment metadata could not be applied: " & ex.Message
            _message.ForeColor = UiTheme.Red
        Finally
            If Not IsDisposed Then _saveButton.Enabled = _dirty AndAlso
                PlcConfigurationAccess.CanEdit(_session.RoleName, _session.IsOfflinePreview)
        End Try
    End Sub
End Class
