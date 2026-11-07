Imports System.IO
Imports System.Linq
Imports System.Text.Json
Imports System.Text.Json.Serialization

Public NotInheritable Class AnalyzerEquipmentSettings
    Public Property AnalyzerId As String = String.Empty
    Public Property EquipmentName As String = String.Empty
    Public Property Manufacturer As String = String.Empty
    Public Property Model As String = String.Empty
    Public Property SerialNumber As String = String.Empty
    Public Property RatedKva As Double?
    Public Property PrimaryRatedVoltage As Double?
    Public Property SecondaryRatedVoltage As Double?
    Public Property ElectricalSide As String = String.Empty
    Public Property MeterManufacturer As String = String.Empty
    Public Property MeterModel As String = String.Empty
    Public Property MeterIdentity As String = String.Empty
    Public Property SiteName As String = String.Empty
    Public Property TemperatureSensorSource As String = String.Empty
    Public Property SelectedParameters As List(Of String) = New List(Of String)()

    Public Function Clone() As AnalyzerEquipmentSettings
        Return New AnalyzerEquipmentSettings With {
            .AnalyzerId = AnalyzerId,
            .EquipmentName = EquipmentName,
            .Manufacturer = Manufacturer,
            .Model = Model,
            .SerialNumber = SerialNumber,
            .RatedKva = RatedKva,
            .PrimaryRatedVoltage = PrimaryRatedVoltage,
            .SecondaryRatedVoltage = SecondaryRatedVoltage,
            .ElectricalSide = ElectricalSide,
            .MeterManufacturer = MeterManufacturer,
            .MeterModel = MeterModel,
            .MeterIdentity = MeterIdentity,
            .SiteName = SiteName,
            .TemperatureSensorSource = TemperatureSensorSource,
            .SelectedParameters = If(SelectedParameters Is Nothing, New List(Of String)(), SelectedParameters.ToList())
        }
    End Function
End Class

Public NotInheritable Class RecordingSettings
    Private Shared ReadOnly AnalyzerIds As String() = {"T1", "T2", "T3", "T4", "TOTAL"}
    Private Shared ReadOnly ParameterIds As String() = MeasurementCatalog.AllParameterIds()

    Public Property AutomaticDailyExcelEnabled As Boolean = True
    Public Property FactoryTimeZoneId As String = "Africa/Accra"
    Public Property DailyExportStabilizationMinutes As Integer = 5
    Public Property ExcelRetentionDays As Integer = 60
    Public Property AutomaticExcelDeletionEnabled As Boolean
    Public Property ExportFolderPath As String = "EOEMS_SCADA\Records"
    Public Property ContinuousRawRecordingEnabled As Boolean = True
    Public Const MinimumRecordingIntervalMilliseconds As Integer = 100
    Public Const MaximumRecordingIntervalMilliseconds As Integer = 10000
    Public Const MinimumPlanningWindowDays As Integer = 3
    Public Const MaximumRawRetentionDays As Integer = 90
    Public Const RecordedAnalyzerCount As Integer = 5
    Public Shared ReadOnly StandardRecordingIntervalsMilliseconds As Integer() = {100, 250, 500, 1000, 2000, 5000, 10000}
    Public Property HistorianRecordingIntervalMilliseconds As Integer = MinimumRecordingIntervalMilliseconds
    ' Settings saved before version 2 carried the old 10-second floor; they move to the live-view rate once.
    ' Version 3 adds calculated PF/kVA, peak/average/energy items and the extended TOTAL channels to saved selections once.
    Public Const CurrentRecordingPolicyVersion As Integer = 3
    Public Property RecordingPolicyVersion As Integer = CurrentRecordingPolicyVersion
    Public Property HistorianRetentionDays As Integer = 30

    Public Function IsRawRecordingDue(capturedUtc As DateTime, lastRecordedUtc As DateTime?) As Boolean
        If capturedUtc.Kind <> DateTimeKind.Utc Then Throw New ArgumentException("A UTC acquisition time is required.", NameOf(capturedUtc))
        Return ContinuousRawRecordingEnabled AndAlso
            (Not lastRecordedUtc.HasValue OrElse capturedUtc < lastRecordedUtc.Value OrElse
             (capturedUtc - lastRecordedUtc.Value).TotalMilliseconds >= HistorianRecordingIntervalMilliseconds)
    End Function

    ' Planning limits only; HistorianRetentionDays is retained as the serialized key, not a deletion policy.
    Public Shared Function MaximumRetentionDaysFor(intervalMilliseconds As Integer) As Integer
        If intervalMilliseconds <= 100 Then Return 8
        If intervalMilliseconds <= 250 Then Return 15
        If intervalMilliseconds <= 500 Then Return 23
        If intervalMilliseconds <= 1000 Then Return 30
        If intervalMilliseconds <= 2000 Then Return 45
        If intervalMilliseconds <= 5000 Then Return 60
        Return MaximumRawRetentionDays
    End Function
    Public Property Equipment As List(Of AnalyzerEquipmentSettings) = CreateDefaultEquipment()

    <JsonIgnore>
    Public ReadOnly Property DatabaseDirectory As String
        Get
            Return Path.Combine(AppContext.BaseDirectory, "EOEMSData", "Historian")
        End Get
    End Property

    <JsonIgnore>
    Public ReadOnly Property ExportRootDirectory As String
        Get
            Return ResolveExportRootDirectory()
        End Get
    End Property

    Public Function ResolveExportRootDirectory(Optional documentsDirectory As String = Nothing) As String
        If String.IsNullOrWhiteSpace(ExportFolderPath) Then
            Throw New InvalidDataException("Daily report folder cannot be empty.")
        End If
        If ExportFolderPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0 Then
            Throw New InvalidDataException("Daily report folder contains invalid path characters.")
        End If
        If Path.IsPathRooted(ExportFolderPath) Then Return Path.GetFullPath(ExportFolderPath)

        Dim documentsRoot = If(String.IsNullOrWhiteSpace(documentsDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            documentsDirectory)
        If String.IsNullOrWhiteSpace(documentsRoot) Then
            Throw New InvalidDataException("The current Windows Documents folder could not be resolved.")
        End If
        documentsRoot = Path.GetFullPath(documentsRoot)
        Dim documentsPrefix = documentsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) &
                              Path.DirectorySeparatorChar
        Dim resolved = Path.GetFullPath(Path.Combine(documentsRoot, ExportFolderPath))
        If Not String.Equals(resolved, documentsRoot, StringComparison.OrdinalIgnoreCase) AndAlso
           Not resolved.StartsWith(documentsPrefix, StringComparison.OrdinalIgnoreCase) Then
            Throw New InvalidDataException("A relative daily report folder must stay inside the current Documents folder.")
        End If
        Return resolved
    End Function

    Public Function MakePortableExportFolderPath(selectedPath As String,
                                                  Optional documentsDirectory As String = Nothing) As String
        If String.IsNullOrWhiteSpace(selectedPath) Then Throw New ArgumentException("Select a daily report folder.", NameOf(selectedPath))
        Dim fullSelection = Path.GetFullPath(selectedPath)
        Dim documentsRoot = If(String.IsNullOrWhiteSpace(documentsDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            documentsDirectory)
        If String.IsNullOrWhiteSpace(documentsRoot) Then
            Throw New InvalidDataException("The current Windows Documents folder could not be resolved.")
        End If
        documentsRoot = Path.GetFullPath(documentsRoot)
        Dim documentsPrefix = documentsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) &
                              Path.DirectorySeparatorChar
        If String.Equals(fullSelection, documentsRoot, StringComparison.OrdinalIgnoreCase) Then Return "."
        If fullSelection.StartsWith(documentsPrefix, StringComparison.OrdinalIgnoreCase) Then
            Return Path.GetRelativePath(documentsRoot, fullSelection)
        End If
        Return fullSelection
    End Function

    Public Sub Normalize()
        If String.IsNullOrWhiteSpace(FactoryTimeZoneId) Then FactoryTimeZoneId = "Africa/Accra"
        If HistorianRecordingIntervalMilliseconds < MinimumRecordingIntervalMilliseconds Then
            HistorianRecordingIntervalMilliseconds = MinimumRecordingIntervalMilliseconds
        End If
        ' Intervals slower than 10 s from older versions move to 10 s (more data, never less).
        If HistorianRecordingIntervalMilliseconds > MaximumRecordingIntervalMilliseconds Then
            HistorianRecordingIntervalMilliseconds = MaximumRecordingIntervalMilliseconds
        End If
        HistorianRetentionDays = Math.Clamp(HistorianRetentionDays, MinimumPlanningWindowDays,
            MaximumRetentionDaysFor(HistorianRecordingIntervalMilliseconds))
        If Equipment Is Nothing Then Equipment = CreateDefaultEquipment()
        Dim existing = Equipment.Where(Function(item) item IsNot Nothing).
            ToDictionary(Function(item) item.AnalyzerId, StringComparer.OrdinalIgnoreCase)
        Equipment = AnalyzerIds.Select(
            Function(analyzerId)
                Dim item As AnalyzerEquipmentSettings = Nothing
                If existing.TryGetValue(analyzerId, item) Then Return item
                Return CreateDefaultEquipment().Single(Function(defaultItem) defaultItem.AnalyzerId = analyzerId)
            End Function).ToList()
        Dim migrateToVersion3 = RecordingPolicyVersion < 3
        For Each item In Equipment
            If String.Equals(item.MeterIdentity?.Trim(), "PM5110", StringComparison.OrdinalIgnoreCase) Then
                If String.IsNullOrWhiteSpace(item.MeterModel) Then item.MeterModel = item.MeterIdentity.Trim()
                item.MeterIdentity = String.Empty
            End If
            If String.Equals(item.AnalyzerId, "TOTAL", StringComparison.OrdinalIgnoreCase) AndAlso
               String.Equals(item.MeterIdentity?.Trim(), "Calculated from T1–T4", StringComparison.Ordinal) Then
                item.MeterIdentity = String.Empty
            End If
            If item.SelectedParameters Is Nothing Then item.SelectedParameters = New List(Of String)()
            If migrateToVersion3 Then item.SelectedParameters.AddRange(MeasurementCatalog.AddedInPolicyVersion3(item.AnalyzerId))
            Dim analyzerId = item.AnalyzerId
            item.SelectedParameters = item.SelectedParameters.
                Where(Function(parameterId) MeasurementCatalog.Find(analyzerId, parameterId) IsNot Nothing).
                Distinct(StringComparer.Ordinal).
                OrderBy(Function(parameterId) Array.IndexOf(ParameterIds, parameterId)).
                ToList()
        Next
        If migrateToVersion3 Then RecordingPolicyVersion = CurrentRecordingPolicyVersion
    End Sub

    Public Sub Validate()
        Normalize()
        If HistorianRecordingIntervalMilliseconds < MinimumRecordingIntervalMilliseconds OrElse
           HistorianRecordingIntervalMilliseconds > MaximumRecordingIntervalMilliseconds Then
            Throw New InvalidDataException("Historian recording interval must be between 0.1 and 10 seconds.")
        End If
        If DailyExportStabilizationMinutes < 0 OrElse DailyExportStabilizationMinutes > 1439 Then
            Throw New InvalidDataException("Daily export time must be between 00:00 and 23:59.")
        End If
        If ExcelRetentionDays < 1 OrElse ExcelRetentionDays > 3650 Then
            Throw New InvalidDataException("Excel retention must be between 1 and 3,650 days.")
        End If
        ResolveFactoryTimeZone()
        ResolveExportRootDirectory()

        If Equipment.Count <> AnalyzerIds.Length OrElse
           Not AnalyzerIds.All(Function(id) Equipment.Any(Function(item) String.Equals(item.AnalyzerId, id, StringComparison.OrdinalIgnoreCase))) Then
            Throw New InvalidDataException("Recording configuration must include T1, T2, T3, T4, and TOTAL.")
        End If
        For Each item In Equipment
            ValidatePositiveMeasurement(item.RatedKva, "rated kVA")
            ValidatePositiveMeasurement(item.PrimaryRatedVoltage, "primary rated voltage")
            ValidatePositiveMeasurement(item.SecondaryRatedVoltage, "secondary rated voltage")
            If item.SelectedParameters.Any(Function(parameterId) MeasurementCatalog.Find(item.AnalyzerId, parameterId) Is Nothing) Then
                Throw New InvalidDataException("A selected recording parameter is not in the " & item.AnalyzerId & " measurement catalog.")
            End If
        Next
    End Sub

    Private Shared Sub ValidatePositiveMeasurement(value As Double?, fieldName As String)
        If value.HasValue AndAlso (Not Double.IsFinite(value.Value) OrElse value.Value <= 0) Then
            Throw New InvalidDataException("A configured " & fieldName & " must be a positive finite number.")
        End If
    End Sub

    Public Function GetAnalyzer(analyzerId As String) As AnalyzerEquipmentSettings
        Return Equipment.Single(Function(item) String.Equals(item.AnalyzerId, analyzerId, StringComparison.OrdinalIgnoreCase))
    End Function

    Public Function ResolveFactoryTimeZone() As TimeZoneInfo
        Try
            Return TimeZoneInfo.FindSystemTimeZoneById(FactoryTimeZoneId)
        Catch ex As TimeZoneNotFoundException
            If String.Equals(FactoryTimeZoneId, "Africa/Accra", StringComparison.Ordinal) Then
                Try
                    Return TimeZoneInfo.FindSystemTimeZoneById("Greenwich Standard Time")
                Catch windowsZoneException As TimeZoneNotFoundException
                    Throw New InvalidDataException("The Windows timezone equivalent for Africa/Accra is unavailable.", windowsZoneException)
                Catch windowsRuleException As InvalidTimeZoneException
                    Throw New InvalidDataException("The Windows timezone rules for Africa/Accra are invalid.", windowsRuleException)
                End Try
            End If
            Throw New InvalidDataException("Factory timezone is not available on this Windows installation: " & FactoryTimeZoneId, ex)
        Catch ex As InvalidTimeZoneException
            Throw New InvalidDataException("Factory timezone configuration is invalid: " & FactoryTimeZoneId, ex)
        End Try
    End Function

    Private Shared Function CreateDefaultEquipment() As List(Of AnalyzerEquipmentSettings)
        Dim items As New List(Of AnalyzerEquipmentSettings)()
        For index = 1 To 4
            items.Add(New AnalyzerEquipmentSettings With {
                .AnalyzerId = "T" & index.ToString(Globalization.CultureInfo.InvariantCulture),
                .EquipmentName = "Transformer " & index.ToString(Globalization.CultureInfo.InvariantCulture),
                .SiteName = "Twellium Industrial Company",
                .SelectedParameters = DefaultSelection("T" & index.ToString(Globalization.CultureInfo.InvariantCulture))
            })
        Next
        items.Add(New AnalyzerEquipmentSettings With {
            .AnalyzerId = "TOTAL",
            .EquipmentName = "Total Factory Load",
            .SiteName = "Twellium Industrial Company",
            .SelectedParameters = DefaultSelection("TOTAL")
        })
        Return items
    End Function

    Private Shared Function DefaultSelection(analyzerId As String) As List(Of String)
        Dim selection As New List(Of String) From {"PTOT_KW"}
        selection.AddRange(MeasurementCatalog.AddedInPolicyVersion3(analyzerId).Where(Function(id) id <> "PTOT_KW"))
        Return selection
    End Function
End Class

Public NotInheritable Class RecordingSettingsStore
    Private Shared ReadOnly Gate As New Object()
    Private Shared ReadOnly SerializerOptions As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True,
        .WriteIndented = True
    }

    Private Sub New()
    End Sub

    Public Shared Function SettingsPath(Optional baseDirectory As String = Nothing) As String
        Return System.IO.Path.Combine(If(baseDirectory, AppContext.BaseDirectory),
                                      "EOEMSData", "Configuration", "recording-settings.json")
    End Function

    Public Shared Function Load(Optional baseDirectory As String = Nothing) As RecordingSettings
        Dim path = SettingsPath(baseDirectory)
        Dim json As String = If(String.IsNullOrWhiteSpace(baseDirectory),
            PostgresDatabase.LoadDocument("recording-settings", path),
            If(File.Exists(path), File.ReadAllText(path), Nothing))
        If json Is Nothing Then
            Dim defaults As New RecordingSettings()
            defaults.Validate()
            Return defaults
        End If
        Try
            Dim settings = JsonSerializer.Deserialize(Of RecordingSettings)(json, SerializerOptions)
            If settings Is Nothing Then Throw New InvalidDataException("Recording settings file is empty.")
            If json.IndexOf("RecordingPolicyVersion", StringComparison.OrdinalIgnoreCase) < 0 Then
                If settings.HistorianRecordingIntervalMilliseconds = 10000 Then
                    settings.HistorianRecordingIntervalMilliseconds = RecordingSettings.MinimumRecordingIntervalMilliseconds
                End If
                settings.RecordingPolicyVersion = 1
            End If
            settings.Validate()
            Return settings
        Catch ex As JsonException
            Throw New InvalidDataException("Recording settings are malformed: " & ex.Message, ex)
        Catch ex As IOException
            Throw New InvalidDataException("Recording settings could not be read: " & ex.Message, ex)
        End Try
    End Function

    Public Shared Sub Save(settings As RecordingSettings,
                           username As String,
                           roleName As String,
                           previous As RecordingSettings,
                           Optional baseDirectory As String = Nothing)
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        If Not PlcConfigurationAccess.CanEdit(roleName, False) Then
            Throw New UnauthorizedAccessException("Only an authenticated administrator can edit recording settings.")
        End If
        settings.Validate()
        Dim path = SettingsPath(baseDirectory)
        Dim folder = System.IO.Path.GetDirectoryName(path)
        Directory.CreateDirectory(folder)
        Dim temporary = path & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Dim backup = path & "." & Guid.NewGuid().ToString("N") & ".bak"
        Dim auditPath = System.IO.Path.Combine(folder, "recording-settings-audit.jsonl")
        Dim auditRecord = JsonSerializer.Serialize(New With {
            .TimestampUtc = DateTime.UtcNow,
            .Username = If(username, String.Empty),
            .Action = "RecordingSettingsChanged",
            .Before = previous,
            .After = settings
        }, SerializerOptions)
        SyncLock Gate
            Try
                File.WriteAllText(temporary, JsonSerializer.Serialize(settings, SerializerOptions), New System.Text.UTF8Encoding(False))
                If File.Exists(path) Then
                    File.Replace(temporary, path, backup)
                Else
                    File.Move(temporary, path)
                End If
                File.AppendAllText(auditPath, auditRecord & Environment.NewLine, New System.Text.UTF8Encoding(False))
                If String.IsNullOrWhiteSpace(baseDirectory) Then
                    PostgresDatabase.TryWriteDocument("recording-settings", JsonSerializer.Serialize(settings, SerializerOptions), username)
                End If
            Finally
                If File.Exists(temporary) Then File.Delete(temporary)
                If File.Exists(backup) Then File.Delete(backup)
            End Try
        End SyncLock
    End Sub
End Class
