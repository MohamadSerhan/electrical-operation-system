Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Threading
Imports System.Text.Json
Imports Npgsql
Imports NpgsqlTypes

Public Interface IEquipmentHistorySource
    ReadOnly Property Id As String
    Function GetEquipment() As IReadOnlyList(Of HistoryEquipment)
    Function Read(request As EquipmentHistoryRequest, token As CancellationToken) As IReadOnlyList(Of EquipmentHistoryRow)
End Interface

Public Interface IRecordedEquipmentHistorySource
    Function GetRecordedEquipment(token As CancellationToken) As IReadOnlyList(Of HistoryEquipment)
End Interface

Public NotInheritable Class EquipmentHistoryService
    Public Const MaximumRows As Integer = 100000
    Private ReadOnly _sources As IReadOnlyList(Of IEquipmentHistorySource)
    Private _recordedEquipment As IReadOnlyList(Of HistoryEquipment) = Array.Empty(Of HistoryEquipment)()

    Public Sub New(sources As IEnumerable(Of IEquipmentHistorySource))
        _sources = sources.ToArray()
        If _sources.Select(Function(source) source.Id).Distinct(StringComparer.Ordinal).Count() <> _sources.Count Then
            Throw New ArgumentException("History source IDs must be unique.")
        End If
    End Sub

    Public Shared Function Create(historian As MeasurementHistorian, settings As RecordingSettings,
                                  Optional currentSettings As Func(Of RecordingSettings) = Nothing) As EquipmentHistoryService
        Return New EquipmentHistoryService({New TransformerHistorySource(historian, If(currentSettings, Function() settings)),
            New UpsEquipmentHistorySource(), New EquipmentAlarmHistorySource()})
    End Function

    Public Function GetEquipment() As IReadOnlyList(Of HistoryEquipment)
        Dim equipment = _sources.SelectMany(Function(source) source.GetEquipment()).ToList()
        equipment.AddRange(_recordedEquipment.Where(Function(recorded) Not equipment.Any(Function(asset) asset.Id = recorded.Id)))
        If Not equipment.Any(Function(asset) asset.Category = "Generators") Then equipment.Add(New HistoryEquipment With {.Id = "GENERATORS", .Name = "Generators - not connected",
            .Category = "Generators", .IsConnected = False,
            .Description = "No generator acquisition source is configured. Running state, fuel, energy and alarms are unavailable, not zero."})
        If equipment.Select(Function(asset) asset.Id).Distinct(StringComparer.Ordinal).Count() <> equipment.Count Then
            Throw New InvalidDataException("Equipment history IDs must be unique across sources.")
        End If
        Return equipment
    End Function

    Public Function DiscoverRecordedEquipment(token As CancellationToken) As IReadOnlyList(Of HistoryEquipment)
        _recordedEquipment = _sources.OfType(Of IRecordedEquipmentHistorySource)().SelectMany(
            Function(source) source.GetRecordedEquipment(token)).ToArray()
        Return GetEquipment()
    End Function

    ' The history picker lists only the channels people actually read. Averages, line-line voltages, per-phase calculated kVA
    ' and the other near-duplicates stay in the Analyzer, Daily reports and the equipment detail pages.
    Private Shared ReadOnly TransformerRawChannels As String() = {"V_L1_N", "V_L2_N", "V_L3_N", "V_L1_L2", "V_L2_L3", "V_L3_L1", "FREQUENCY", "AVG_CURRENT", "PTOT_KW",
        "CURRENT_L1", "CURRENT_L2", "CURRENT_L3", "CURRENT_NEUTRAL", "TDEGC_C", "KVA_CALC", "PF_CALC"}
    Private Shared ReadOnly UpsRawChannels As String() = {"output_kw", "output_kva", "output_pf", "load_max_pct",
        "out_v_l1", "out_v_l2", "out_v_l3", "out_v_ll1", "out_v_ll2", "out_v_ll3", "out_i_l1", "out_i_l2", "out_i_l3", "out_w_l1", "out_w_l2", "out_w_l3",
        "in_v_l1", "in_v_l2", "in_v_l3", "in_i_l1", "in_i_l2", "in_i_l3", "out_freq_hz",
        "battery_charge_pct", "battery_runtime_min", "battery_voltage_v", "battery_temp_c"}

    Private Shared Function IsUsefulRawChannel(sourceId As String, metricId As String) As Boolean
        If sourceId = "ups" Then Return UpsRawChannels.Contains(metricId) OrElse UpsInputEstimates.IsEstimateKey(metricId)
        Return TransformerRawChannels.Contains(metricId)
    End Function

    Public Function GetRawEquipment() As IReadOnlyList(Of HistoryEquipment)
        Return _sources.Where(Function(source) {"transformer", "ups"}.Contains(source.Id)).
            SelectMany(Function(source) source.GetEquipment()).
            Where(Function(asset) (asset.SourceId = "transformer" AndAlso Not asset.Id.EndsWith(":15MIN", StringComparison.Ordinal)) OrElse
                (asset.SourceId = "ups" AndAlso Not asset.IsAggregate)).
            Select(Function(asset)
                       Dim measurements = asset.Metrics.Where(Function(metric) IsUsefulRawChannel(asset.SourceId, metric.Id)).ToArray()
                       Return New HistoryEquipment With {
                           .Id = asset.Id, .Name = asset.Name, .Category = If(asset.SourceId = "transformer", "Transformers", "UPS"),
                           .SourceId = asset.SourceId, .SourceKey = asset.SourceKey, .IsAggregate = asset.IsAggregate,
                           .IsConnected = asset.IsConnected,
                           .IntervalSeconds = If(asset.SourceId = "ups",
                               CDbl(UpsSettingsStore.Current.Devices.Single(Function(device) device.Id = asset.SourceKey).PollIntervalSeconds), asset.IntervalSeconds),
                           .Description = "Saved recording (same source as Daily reports and Energy). UTC is the host acquisition time; missing readings are never filled.",
                           .Metrics = {New HistoryMetric With {.Id = "alarm:events", .Name = "Alarm / Fault", .IsAlarm = True, .IsState = True,
                                   .Basis = "Named observed alarm/fault events: recorded when raised and when cleared. Unknown is never a clear."}}.
                               Concat(measurements).ToArray()
                       }
                   End Function).ToArray()
    End Function

    Public Function Read(request As EquipmentHistoryRequest, token As CancellationToken) As EquipmentHistoryResult
        If request Is Nothing OrElse request.Equipment Is Nothing Then Throw New ArgumentException("Select equipment.")
        Dim asset = If(request.RawOnly, GetRawEquipment(), GetEquipment()).SingleOrDefault(
            Function(item) item.Id = request.Equipment.Id AndAlso item.SourceId = request.Equipment.SourceId)
        If asset Is Nothing Then Throw New InvalidDataException("This equipment is not in the current or discovered historical catalog.")
        request = New EquipmentHistoryRequest With {.Equipment = asset, .Metrics = request.Metrics, .FromUtc = request.FromUtc,
            .ToUtc = request.ToUtc,             .IndividualAcquisitions = request.IndividualAcquisitions, .RawOnly = request.RawOnly,
                        .ThinSeconds = request.ThinSeconds}
        If Not request.RawOnly AndAlso Not request.Equipment.IsConnected Then Throw New InvalidOperationException(request.Equipment.Description)
        If request.FromUtc.Kind <> DateTimeKind.Utc OrElse request.ToUtc.Kind <> DateTimeKind.Utc OrElse request.ToUtc <= request.FromUtc Then
            Throw New InvalidDataException("Choose a positive UTC interval [from, to).")
        End If
        If request.Metrics Is Nothing OrElse request.Metrics.Count = 0 Then Throw New InvalidDataException("Select at least one recorded channel.")
        If request.Metrics.Any(Function(metric) metric Is Nothing) OrElse
           request.Metrics.Select(Function(metric) metric.Id).Distinct().Count() <> request.Metrics.Count OrElse
           request.Metrics.Any(Function(metric) Not request.Equipment.Metrics.Any(Function(known) known.Id = metric.Id)) Then
            Throw New InvalidDataException("The selected channel does not belong to this equipment.")
        End If
        request.Metrics = request.Metrics.Select(Function(metric) asset.Metrics.Single(Function(known) known.Id = metric.Id)).ToArray()
        If request.IndividualAcquisitions AndAlso request.Metrics.Any(Function(metric) metric.IsIntervalPeak) Then
            Throw New InvalidDataException("Interval peak channels require interval records. Uncheck Individual readings or deselect peak channels.")
        End If
        Dim source = _sources.Single(Function(item) item.Id = request.Equipment.SourceId)
        token.ThrowIfCancellationRequested()
        Dim rows As IReadOnlyList(Of EquipmentHistoryRow)
        Try
            rows = EquipmentRawHistory.Read(request, Function(part) source.Read(part, token), token)
        Catch ex As NpgsqlException When token.IsCancellationRequested
            Throw New OperationCanceledException("Equipment history read cancelled.", ex, token)
        End Try
        CheckLimit(rows.Count)
        Dim events As IReadOnlyList(Of EquipmentHistoryRow) = Array.Empty(Of EquipmentHistoryRow)()
        If (Not request.RawOnly OrElse request.Metrics.Any(Function(metric) metric.Id = "alarm:events")) AndAlso
            (({"transformer", "ups"}.Contains(asset.SourceId) AndAlso Not asset.IsAggregate AndAlso
            Not asset.Id.EndsWith(":15MIN", StringComparison.Ordinal)) OrElse (asset.SourceId = "transformer" AndAlso asset.Id = "TOTAL")) Then
            events = New EquipmentAlarmHistorySource().Read(New EquipmentHistoryRequest With {
                .Equipment = New HistoryEquipment With {.Id = asset.Id, .SourceKey = If(asset.SourceId = "ups", "UPS:", "Transformer:") & asset.SourceKey},
                .Metrics = {New HistoryMetric With {.Id = "alarm_state"}}, .RawOnly = request.RawOnly,
                .FromUtc = request.FromUtc, .ToUtc = request.ToUtc}, token)
        End If
        CheckLimit(rows.Count + events.Count)
        Return New EquipmentHistoryResult With {.Request = request, .Rows = rows, .AlarmEvents = events, .GeneratedUtc = DateTime.UtcNow}
    End Function

    Friend Shared Sub CheckLimit(count As Integer)
        If count > MaximumRows Then Throw New InvalidDataException("More than 100,000 observations match. Narrow the period or select fewer channels; no truncated report was produced.")
    End Sub
End Class

Public NotInheritable Class TransformerHistorySource
    Implements IEquipmentHistorySource
    Private ReadOnly _historian As MeasurementHistorian
    Private ReadOnly _settings As Func(Of RecordingSettings)
    Public Sub New(historian As MeasurementHistorian, settings As Func(Of RecordingSettings))
        _historian = historian
        _settings = settings
    End Sub
    Public ReadOnly Property Id As String Implements IEquipmentHistorySource.Id
        Get
            Return "transformer"
        End Get
    End Property
    Public Function GetEquipment() As IReadOnlyList(Of HistoryEquipment) Implements IEquipmentHistorySource.GetEquipment
        Dim settings = _settings()
        Dim equipment = {"T1", "T2", "T3", "T4", "TOTAL"}.Select(Function(key) New HistoryEquipment With {
            .Id = key, .Name = settings.GetAnalyzer(key).EquipmentName, .Category = If(key = "TOTAL", "Totals", "Transformers"),
            .SourceId = Id, .SourceKey = key, .IsAggregate = key = "TOTAL",
            .IntervalSeconds = settings.HistorianRecordingIntervalMilliseconds / 1000.0R,
            .Description = "PostgreSQL raw acquisition history; full resolution retained without automatic expiry. " &
                If(key = "TOTAL", "Stored validated same-cycle transformer total. Never combine with downstream UPS totals.", "PM5110 host-acquisition timestamps; physical source freshness unverified.") &
                " Missing/invalid acquisition cycles are represented in minute quality counters, not fabricated numeric rows.",
            .Metrics = MeasurementCatalog.GetCatalog(key).Where(Function(metric) Not metric.IsWindowStatistic).Select(
                Function(metric) New HistoryMetric With {.Id = metric.Id, .Name = metric.Label, .Unit = metric.Unit,
                    .Basis = metric.Verification, .IsPower = metric.Id = "PTOT_KW"}).
                Concat(PhasePowerMetrics()).Select(Function(metric) metric.WithCapacity(HistoryCapacity.Transformer(settings, key))).ToArray()
        }).ToArray()
        Return equipment.Concat(equipment.Select(Function(asset) New HistoryEquipment With {
            .Id = asset.Id & ":15MIN", .Name = asset.Name & " / 15-minute archive", .SourceKey = asset.SourceKey, .SourceId = Id,
            .Category = "Transformer archive (15 min)", .IntervalSeconds = 900,
            .Description = "Retained measurement_15min rollups of expired raw data, not live/raw samples. Only whole buckets inside [from, to) are returned. " &
                "Bucket mean = stored sum / sample count; means across buckets are unweighted. No energy is inferred from rollups; no raw timestamps or alarm evidence survive here.",
            .Metrics = asset.Metrics.Where(Function(metric) MeasurementCatalog.GetStoredIndex(metric.Id) >= 0).
                SelectMany(Function(metric) {"min", "max", "mean", "count"}.Select(
                Function(statistic) New HistoryMetric With {.Id = metric.Id & ":" & statistic,
                    .Name = metric.Name & " / bucket " & statistic, .Unit = If(statistic = "count", "samples", metric.Unit),
                    .Basis = "15-minute saved-sample " & statistic & "; no reconstruction of raw observations. Counts are channel sample counts."})).ToArray()
        })).ToArray()
    End Function

    Private Shared Function PhasePowerMetrics() As IEnumerable(Of HistoryMetric)
        Return Enumerable.Range(1, 3).SelectMany(Function(phase) New HistoryMetric() {
            New HistoryMetric With {.Id = "KVA_L" & phase & "_CALC", .Name = "Apparent power L" & phase & " (calc.)", .Unit = "kVA",
                .Basis = "Calculated from same-acquisition phase L-N voltage x current / 1000; TOTAL sums individual transformer phase kVA. Not active kW."},
            New HistoryMetric With {.Id = "KW_L" & phase, .Name = "Active power L" & phase & " (unavailable)", .Unit = "kW",
                .Basis = "No verified phase active-power register in the transformer map. Not inferred from voltage/current or total kW."}})
    End Function
    Public Function Read(request As EquipmentHistoryRequest, token As CancellationToken) As IReadOnlyList(Of EquipmentHistoryRow) Implements IEquipmentHistorySource.Read
        If request.Equipment.Id.EndsWith(":15MIN", StringComparison.Ordinal) Then Return ReadArchive(request, token)
        Dim rows As New List(Of EquipmentHistoryRow)()
        Dim invalidMinutes As New HashSet(Of DateTime)()
        Using connection = PostgresDatabase.Open()
            Using command As New NpgsqlCommand("SELECT bucket_start FROM sample_quality_minute WHERE analyzer_id=@id AND bad_samples>0 AND bucket_start>=@from AND bucket_start<@to", connection)
                command.Parameters.AddWithValue("id", request.Equipment.SourceKey)
                command.Parameters.AddWithValue("from", request.FromUtc.AddTicks(-(request.FromUtc.Ticks Mod TimeSpan.TicksPerMinute)))
                command.Parameters.AddWithValue("to", request.ToUtc)
                Using registration = token.Register(Sub() command.Cancel())
                    Using reader = command.ExecuteReader()
                        While reader.Read()
                            token.ThrowIfCancellationRequested()
                            invalidMinutes.Add(reader.GetDateTime(0))
                        End While
                    End Using
                End Using
            End Using
        End Using
        Dim selected = request.Metrics.Where(Function(metric) MeasurementCatalog.GetStoredIndex(metric.Id) >= 0).Select(Function(metric) metric.Id).ToArray()
        Dim extra = request.Metrics.Where(Function(metric) MeasurementCatalog.GetStoredIndex(metric.Id) < 0).ToArray()
        Dim requested = If(extra.Length > 0, MeasurementCatalog.GetCatalog(request.Equipment.SourceKey).
            Where(Function(metric) Not metric.IsWindowStatistic).Select(Function(metric) metric.Id).ToArray(), selected)
        Dim timestamps As New Dictionary(Of DateTime, EquipmentHistoryRow)()
        Dim lastKept As DateTime = DateTime.MinValue
        Dim thin = TimeSpan.FromSeconds(Math.Max(0, request.ThinSeconds))
        _historian.StreamRawMeasurements(request.Equipment.SourceKey, requested,
            request.FromUtc, request.ToUtc,
            Sub(point)
                If thin > TimeSpan.Zero Then
                    If point.TimestampUtc <> lastKept AndAlso point.TimestampUtc - lastKept < thin Then Return
                    lastKept = point.TimestampUtc
                End If
                Dim baseRow As New EquipmentHistoryRow With {.EquipmentId = request.Equipment.Id,
                    .TimestampUtc = point.TimestampUtc, .AcquiredUtc = point.TimestampUtc, .Quality = point.Quality,
                    .Evidence = "Legacy selected raw observation. Within-interval extrema were not captured."}
                timestamps(point.TimestampUtc) = baseRow
                If Not selected.Contains(point.ParameterId) Then Return
                EquipmentHistoryService.CheckLimit(rows.Count + 1)
                rows.Add(New EquipmentHistoryRow With {.EquipmentId = request.Equipment.Id, .MetricId = point.ParameterId,
                    .TimestampUtc = point.TimestampUtc, .AcquiredUtc = point.TimestampUtc, .Value = point.Value,
                    .EnergyEligible = Not invalidMinutes.Contains(point.TimestampUtc.AddTicks(-(point.TimestampUtc.Ticks Mod TimeSpan.TicksPerMinute))),
                    .Quality = point.Quality,
                    .AlarmSummary = If(point.AlarmEvidence Is Nothing, "Alarm evidence unavailable",
                        EquipmentRawHistory.SummarizeAlarm(JsonSerializer.Deserialize(Of EquipmentAlarmEvidence)(If(point.AlarmEvidence, "null")))),
                    .Evidence = point.SampleKind & "; cycle " & point.AcquisitionCycleId & "; session " & point.AcquisitionSessionId &
                        "; alarm evaluation: " & If(point.AlarmEvidence, "not recorded") &
                        If(invalidMinutes.Contains(point.TimestampUtc.AddTicks(-(point.TimestampUtc.Ticks Mod TimeSpan.TicksPerMinute))),
                            "; minute quality contains failed cycles: energy conservatively excluded for this minute.", "")})
            End Sub, token)
        For Each timestamp In timestamps.Values
            For Each metric In extra
                EquipmentHistoryService.CheckLimit(rows.Count + 1)
                rows.Add(New EquipmentHistoryRow With {.EquipmentId = request.Equipment.Id, .MetricId = metric.Id,
                    .TimestampUtc = timestamp.TimestampUtc, .AcquiredUtc = timestamp.AcquiredUtc,
                    .Quality = "Unavailable legacy channel", .EnergyEligible = False, .Evidence = timestamp.Evidence})
            Next
        Next
        rows = rows.OrderBy(Function(row) row.TimestampUtc).ThenBy(Function(row) row.MetricId).ToList()
        Return rows
    End Function

    Private Shared Function ReadArchive(request As EquipmentHistoryRequest, token As CancellationToken) As IReadOnlyList(Of EquipmentHistoryRow)
        Dim rows As New List(Of EquipmentHistoryRow)()
        Dim selected = MeasurementCatalog.GetCatalog(request.Equipment.SourceKey).Where(Function(parameter) Not parameter.IsWindowStatistic AndAlso
            request.Metrics.Any(Function(metric) metric.Id.StartsWith(parameter.Id & ":", StringComparison.Ordinal))).ToDictionary(Function(parameter) parameter.Index)
        Using connection = PostgresDatabase.Open()
            Using command As New NpgsqlCommand("SELECT parameter_index, bucket_start, min_value, max_value, sum_value, sample_count " &
                "FROM measurement_15min WHERE analyzer_id=@id AND parameter_index=ANY(@indexes) AND bucket_start>=@from " &
                "AND bucket_start + interval '15 minutes' <= @to ORDER BY bucket_start, parameter_index LIMIT 100001", connection)
                command.Parameters.AddWithValue("id", request.Equipment.SourceKey)
                command.Parameters.AddWithValue("indexes", selected.Keys.Select(Function(index) CShort(index)).ToArray())
                command.Parameters.AddWithValue("from", request.FromUtc)
                command.Parameters.AddWithValue("to", request.ToUtc)
                Using registration = token.Register(Sub() command.Cancel())
                    Using reader = command.ExecuteReader()
                        While reader.Read()
                            token.ThrowIfCancellationRequested()
                            Dim parameter = selected(CInt(reader.GetInt16(0)))
                            Dim count = reader.GetInt32(5)
                            For Each metric In request.Metrics.Where(Function(item) item.Id.StartsWith(parameter.Id & ":", StringComparison.Ordinal))
                                Dim statistic = metric.Id.Substring(parameter.Id.Length + 1)
                                Dim value As Double?
                                Select Case statistic
                                    Case "min" : value = reader.GetFloat(2)
                                    Case "max" : value = reader.GetFloat(3)
                                    Case "mean" : value = If(count > 0, CType(reader.GetDouble(4) / count, Double?), Nothing)
                                    Case "count" : value = count
                                End Select
                                EquipmentHistoryService.CheckLimit(rows.Count + 1)
                                rows.Add(New EquipmentHistoryRow With {.EquipmentId = request.Equipment.Id, .MetricId = metric.Id,
                                    .TimestampUtc = reader.GetDateTime(1), .Value = If(value.HasValue AndAlso Double.IsFinite(value.Value), value, Nothing),
                                    .Quality = If(value.HasValue AndAlso Double.IsFinite(value.Value), "Good", "Unavailable rollup"),
                                    .EnergyEligible = False, .RecordedIntervalSeconds = 900,
                                    .Evidence = "15-minute aggregate; stored sample count " & count & "; sum " &
                                        reader.GetDouble(4).ToString("0.###", CultureInfo.InvariantCulture) & ". Not a raw observation or metered energy."})
                            Next
                        End While
                    End Using
                End Using
            End Using
        End Using
        Return rows
    End Function
End Class

Public NotInheritable Class UpsEquipmentHistorySource
    Implements IEquipmentHistorySource, IRecordedEquipmentHistorySource
    Private Shared ReadOnly Columns As IReadOnlyDictionary(Of String, HistoryMetric) = BuildColumns()
    Private Shared ReadOnly JsonChannels As IReadOnlyDictionary(Of String, UpsOidDefinition) = BuildJsonChannels()
    Private Shared ReadOnly EstimateMetrics As IReadOnlyDictionary(Of String, HistoryMetric) =
        UpsInputEstimates.Metrics().ToDictionary(Function(metric) metric.Id, StringComparer.Ordinal)
    Private ReadOnly _schemaGate As New Object()
    Private _schemaConnectionString As String
    Public Shared ReadOnly DuplicateRawChannels As IReadOnlyDictionary(Of String, String) = BuildDuplicateRawChannels()

    Private Shared Function BuildDuplicateRawChannels() As IReadOnlyDictionary(Of String, String)
        Dim aliases As New Dictionary(Of String, String)(StringComparer.Ordinal)
        For Each pair In {("OutputFrequency", "out_freq_hz"), ("BatteryStatus", "battery_status"),
            ("ChargePercent", "battery_charge_pct"), ("RuntimeMinutes", "battery_runtime_min"),
            ("BatteryVoltage", "battery_voltage_v"), ("BatteryCurrent", "battery_current_a"),
            ("BatteryTemperature", "battery_temp_c"), ("SecondsOnBattery", "seconds_on_battery"),
            ("OutputSource", "output_source"), ("AlarmsPresent", "alarms_present")}
            aliases.Add("channel:" & pair.Item1, pair.Item2)
        Next
        For Each pair In {("OutputLoad", "load_l"), ("OutputVoltage", "out_v_l"), ("OutputCurrent", "out_i_l"),
            ("OutputPower", "out_w_l"), ("InputVoltage", "in_v_l"), ("InputCurrent", "in_i_l"),
            ("BypassVoltage", "bypass_v_l"), ("OutputVoltageLL", "out_v_ll")}
            For phase = 1 To 3
                aliases.Add("channel:" & pair.Item1 & ":L" & phase, pair.Item2 & phase)
            Next
        Next
        Return aliases
    End Function

    Private Sub EnsureSchema(connection As NpgsqlConnection, token As CancellationToken)
        SyncLock _schemaGate
            If _schemaConnectionString = connection.ConnectionString Then Return
            UpsHistorian.EnsureHistorySchema(connection, token)
            _schemaConnectionString = connection.ConnectionString
        End SyncLock
    End Sub

    Private Shared Function BuildJsonChannels() As IReadOnlyDictionary(Of String, UpsOidDefinition)
        Dim result As New Dictionary(Of String, UpsOidDefinition)(StringComparer.Ordinal)
        For Each definition In UpsOidMap.Definitions.Concat(UpsMeasurementMap.Candidates.Select(Function(item) item.Definition)).
            Where(Function(item) Not UpsOidMap.IdentityKeys.Contains(item.Key))
            For index = 0 To If(definition.PerPhase, 2, 0)
                Dim key = "channel:" & definition.Key & If(definition.PerPhase, ":L" & (index + 1), "")
                If Not result.ContainsKey(key) Then result.Add(key, definition)
            Next
        Next
        Return result
    End Function

    Private Shared Function AllMetrics() As IReadOnlyList(Of HistoryMetric)
        Return Columns.Values.Concat(JsonChannels.Select(Function(item) New HistoryMetric With {
            .Id = item.Key, .Name = "Additional measurement: " & item.Key.Substring(8), .Unit = item.Value.Unit,
            .IsState = {"BatteryStatus", "OutputSource", "AlarmsPresent", "InputLineBads", "InputLines", "OutputLines", "BypassLines"}.Contains(item.Value.Key),
            .MinimumMappingRevision = 2,
            .Basis = "Recorded numeric channel evidence, native scaled units; not accounting-validated. Available only for samples recorded after provenance capture was introduced."})).
            Concat(EstimateMetrics.Values).ToArray()
    End Function
    Public ReadOnly Property Id As String Implements IEquipmentHistorySource.Id
        Get
            Return "ups"
        End Get
    End Property
    Private Shared Function BuildColumns() As IReadOnlyDictionary(Of String, HistoryMetric)
        Dim result As New Dictionary(Of String, HistoryMetric)(StringComparer.Ordinal)
        Dim add = Sub(key As String, name As String, unit As String, sum As Boolean, state As Boolean)
                      result.Add(key, New HistoryMetric With {.Id = key, .Name = name, .Unit = unit, .CanSum = sum,
                          .IsPower = key = "output_kw", .IsState = state,
                          .MinimumMappingRevision = If(key.StartsWith("battery_", StringComparison.Ordinal) OrElse
                              {"seconds_on_battery", "output_source", "alarms_present"}.Contains(key), 1, 2),
                          .Basis = If(sum, "Sum only with complete historical membership; never a source/ATS meter.",
                              If(state, "Recorded state/count code; no arithmetic mean.", "Recorded card value; unsupported fields remain blank."))})
                  End Sub
        add("output_kw", "Validated output active power", "kW", True, False)
        add("output_kva", "Validated output apparent power", "kVA", True, False)
        add("output_pf", "Output power factor", "PF", False, False)
        add("load_max_pct", "Maximum phase load", "%", False, False)
        For Each pair In {("load_l", "Output phase load", "%"), ("out_v_l", "Output voltage", "V"),
                         ("out_v_ll", "Output line-line voltage", "V"), ("out_i_l", "Output current", "A"),
                         ("out_w_l", "Output phase power", "W"), ("in_v_l", "Input voltage", "V"),
                         ("in_i_l", "Input current", "A"), ("bypass_v_l", "Bypass voltage (stored reference)", "V")}
            For phase = 1 To 3
                add(pair.Item1 & phase, pair.Item2 & " " & phase, pair.Item3, False, False)
            Next
        Next
        For Each item In {("out_freq_hz", "Output frequency", "Hz"), ("in_freq_hz", "Input frequency", "Hz"),
                         ("battery_charge_pct", "Battery charge", "%"), ("battery_runtime_min", "Battery runtime estimate", "min"),
                         ("battery_voltage_v", "Battery voltage", "V"), ("battery_current_a", "Battery current", "A"),
                         ("battery_temp_c", "Battery temperature", "deg C"), ("seconds_on_battery", "Time on battery", "s"),
                         ("reported_output_kw", "Reported output power (not accounting-validated)", "kW")}
            add(item.Item1, item.Item2, item.Item3, False, False)
        Next
        add("battery_status", "Battery status (RFC1628 code)", "code", False, True)
        add("output_source", "Output source (RFC1628 code)", "code", False, True)
        add("alarms_present", "Reported alarm count (not alarm-event count)", "count", False, True)
        For phase = 1 To 3
            add("out_kva_l" & phase, "Output apparent power L" & phase & " (calc.)", "kVA", False, False)
            result("out_kva_l" & phase).Basis = "Calculated from same-acquisition output L-N voltage x current / 1000 only when the voltage reference is verified. Not active W."
        Next
        Return result
    End Function

    Public Shared Function RawParameterMappings() As IReadOnlyDictionary(Of String, String)
        Dim mappings As New Dictionary(Of String, String)(StringComparer.Ordinal)
        For Each pair In {"output_kw:kw", "output_kva:kva", "output_pf:pf", "load_max_pct:lmax",
            "out_freq_hz:of", "in_freq_hz:if", "battery_status:bs", "battery_charge_pct:bc",
            "battery_runtime_min:br", "battery_voltage_v:bvv", "battery_current_a:bi", "battery_temp_c:bt",
            "seconds_on_battery:sob", "output_source:src", "alarms_present:al", "reported_output_kw:reportedkw"}
            Dim parts = pair.Split(":"c)
            mappings.Add(parts(0), parts(1))
        Next
        For Each pair In {("load_l", "l"), ("out_v_l", "ov"), ("out_i_l", "oi"), ("out_w_l", "ow"),
            ("in_v_l", "iv"), ("in_i_l", "ii"), ("bypass_v_l", "bv"), ("out_v_ll", "ll")}
            For phase = 1 To 3
                mappings.Add(pair.Item1 & phase, pair.Item2 & phase)
            Next
        Next
        Return mappings
    End Function
    Public Function GetEquipment() As IReadOnlyList(Of HistoryEquipment) Implements IEquipmentHistorySource.GetEquipment
        Dim settings = UpsSettingsStore.Current
        Dim equipment As New List(Of HistoryEquipment)()
        For Each device In settings.Devices
            equipment.Add(New HistoryEquipment With {.Id = device.Id, .Name = "UPS " & device.DisplayName, .Category = "UPS",
                .SourceId = Id, .SourceKey = device.Id, .IntervalSeconds = settings.HistorianIntervalSeconds,
                .Description = device.Model & "; ATS bank " & device.AtsBank & "; PostgreSQL raw acquisition history, no automatic expiry; recording " & If(settings.HistorianEnabled, "enabled", "disabled") &
                    "; device polling " & If(device.Enabled, "enabled", "disabled") & ". Historical rows may exist even when currently disabled.",
                .Metrics = AllMetrics().Select(Function(metric) metric.WithUpsMetadata(device, settings.Bank(device.AtsBank))).ToArray()})
        Next
        For Each bank In settings.Banks
            equipment.Add(New HistoryEquipment With {.Id = "ATS-BANK-" & bank.Number, .Name = bank.Name & " - UPS aggregation",
                .Category = "ATS banks (derived)", .SourceId = Id, .BankNumber = bank.Number, .IsAggregate = True,
                .IntervalSeconds = settings.HistorianIntervalSeconds, .Description =
                    bank.SourceDescription & ". SUM OF DOWNSTREAM UPS ONLY; not ATS input power, source position or generator status. " &
                    "Requires complete membership saved with the historical rows. Older rows without membership evidence cannot prove totals.",
                .Metrics = Columns.Values.Where(Function(metric) metric.CanSum).ToArray()})
        Next
        equipment.Add(New HistoryEquipment With {.Id = "UPS-FLEET-TOTAL", .Name = "UPS fleet total (output)", .Category = "Totals",
            .SourceId = Id, .IsAggregate = True, .IntervalSeconds = settings.HistorianIntervalSeconds,
            .Description = "Complete historical UPS cohort output sum, not a site meter. Do not add to transformer totals (double counting). " &
                "Old rows lacking cohort evidence cannot prove totals; asynchronous device polls are not simultaneous measurements.",
            .Metrics = Columns.Values.Where(Function(metric) metric.CanSum).ToArray()})
        Return equipment
    End Function

    Public Function GetRecordedEquipment(token As CancellationToken) As IReadOnlyList(Of HistoryEquipment) Implements IRecordedEquipmentHistorySource.GetRecordedEquipment
        Dim equipment As New List(Of HistoryEquipment)()
        Using connection = PostgresDatabase.Open()
            EnsureSchema(connection, token)
            Using command As New NpgsqlCommand("SELECT DISTINCT ON (device_id) device_id, device_label, device_model, ats_bank FROM ups_samples ORDER BY device_id, sampled_utc DESC LIMIT 10001", connection)
                Using registration = token.Register(Sub() command.Cancel())
                    Using reader = command.ExecuteReader()
                        While reader.Read()
                            token.ThrowIfCancellationRequested()
                            If equipment.Count >= 10000 Then Throw New InvalidDataException("More than 10,000 recorded UPS assets. Historical discovery needs a narrower archive catalog.")
                            equipment.Add(New HistoryEquipment With {.Id = reader.GetString(0), .SourceKey = reader.GetString(0), .SourceId = Id,
                                .Name = If(reader.IsDBNull(1), reader.GetString(0), reader.GetString(1)) & " (recorded)",
                                .Category = "Archived UPS", .IntervalSeconds = UpsSettingsStore.Current.HistorianIntervalSeconds,
                                .Description = "Recorded UPS identity, no longer in the active registry. Last stored model: " &
                                    If(reader.IsDBNull(2), "unknown", reader.GetString(2)) & "; last ATS bank " & If(reader.IsDBNull(3), "unknown", reader.GetInt16(3).ToString(CultureInfo.InvariantCulture)) &
                                    ". Display names may have changed; the stable device ID identifies the history.",
                                .Metrics = AllMetrics()})
                        End While
                    End Using
                End Using
            End Using
        End Using
        Return equipment
    End Function

    Public Function Read(request As EquipmentHistoryRequest, token As CancellationToken) As IReadOnlyList(Of EquipmentHistoryRow) Implements IEquipmentHistorySource.Read
        Try
            Return ReadRows(request, token)
        Catch ex As NpgsqlException When token.IsCancellationRequested
            Throw New OperationCanceledException("UPS history read cancelled.", ex, token)
        End Try
    End Function

    Private Function ReadRows(request As EquipmentHistoryRequest, token As CancellationToken) As IReadOnlyList(Of EquipmentHistoryRow)
        Dim metrics = request.Metrics.Select(Function(metric)
                                                If Not Columns.ContainsKey(metric.Id) AndAlso Not JsonChannels.ContainsKey(metric.Id) AndAlso
                                                   Not EstimateMetrics.ContainsKey(metric.Id) Then Throw New InvalidDataException("Unknown UPS history channel.")
                                                Return metric
                                            End Function).ToArray()
        Dim wideMetrics = metrics.Where(Function(metric) Columns.ContainsKey(metric.Id)).ToArray()
        Dim raw As New List(Of UpsHistoryObservation)()
        Using connection = PostgresDatabase.Open()
            EnsureSchema(connection, token)
            Dim filter = If(request.Equipment.IsAggregate, If(request.Equipment.BankNumber.HasValue, " AND ats_bank = @bank", ""), " AND device_id = @id")
            Dim sql = "SELECT device_id, sampled_utc, quality, measurement_revision, acquired_utc, quality_detail, " &
                "out_v_reference, in_v_reference, bypass_v_reference, alarm_evidence::text, " &
                If(request.Equipment.BankNumber.HasValue, "bank_members", "fleet_members") &
                ", channel_evidence::text, recording_interval_seconds, device_label, device_model, measurement_map, alarm_evaluation::text" &
                If(wideMetrics.Length = 0, "", ", " & String.Join(", ", wideMetrics.Select(
                    Function(metric) If(metric.Id.StartsWith("out_kva_l", StringComparison.Ordinal), "NULL::double precision", metric.Id & "::double precision")))) &
                " FROM ups_samples WHERE sampled_utc >= @from AND sampled_utc < @to" & filter &
                " ORDER BY sampled_utc, device_id LIMIT " & If(request.ThinSeconds > 0, "20000000",
                    (If(request.Equipment.IsAggregate, EquipmentHistoryService.MaximumRows,
                    EquipmentHistoryService.MaximumRows \ metrics.Length) + 1).ToString(CultureInfo.InvariantCulture))
            Dim lastKept As New Dictionary(Of String, DateTime)()
            Using command As New NpgsqlCommand(sql, connection)
                command.Parameters.AddWithValue("from", request.FromUtc)
                command.Parameters.AddWithValue("to", request.ToUtc)
                If request.Equipment.BankNumber.HasValue Then command.Parameters.AddWithValue("bank", request.Equipment.BankNumber.Value)
                If Not request.Equipment.IsAggregate Then command.Parameters.AddWithValue("id", request.Equipment.SourceKey)
                Using registration = token.Register(Sub() command.Cancel())
                    Using reader = command.ExecuteReader()
                        While reader.Read()
                            token.ThrowIfCancellationRequested()
                            EquipmentHistoryService.CheckLimit((raw.Count + 1) * If(request.Equipment.IsAggregate, 1, metrics.Length))
                            Dim observation As New UpsHistoryObservation With {
                                .DeviceId = reader.GetString(0), .TimestampUtc = reader.GetDateTime(1), .Quality = reader.GetString(2),
                                .Revision = reader.GetInt32(3), .AcquiredUtc = If(reader.IsDBNull(4), CType(Nothing, DateTime?), reader.GetDateTime(4)),
                                .Evidence = "Output voltage: " & If(reader.IsDBNull(6), "unknown", reader.GetString(6)) &
                                    "; input: " & If(reader.IsDBNull(7), "unknown", reader.GetString(7)) &
                                    "; bypass: " & If(reader.IsDBNull(8), "unknown", reader.GetString(8)) &
                                    "; " & If(reader.IsDBNull(5), "", reader.GetString(5)) &
                                    "; alarm evidence: " & If(reader.IsDBNull(9), "not recorded", reader.GetString(9)) &
                                    "; recorded identity: " & If(reader.IsDBNull(13), "unknown", reader.GetString(13)) & " / " &
                                    If(reader.IsDBNull(14), "unknown", reader.GetString(14)) &
                                    "; mapping: " & If(reader.IsDBNull(15), "unknown", reader.GetString(15)) &
                                    "; numeric channel evidence: " & If(reader.IsDBNull(11), "not recorded", reader.GetString(11)) &
                                    "; alarm evaluation: " & If(reader.IsDBNull(16), "not recorded", reader.GetString(16)),
                                .RecordedIntervalSeconds = If(reader.IsDBNull(12), CType(Nothing, Double?), CDbl(reader.GetInt32(12))),
                                .AlarmSummary = If(reader.IsDBNull(16), "Alarm evidence unavailable",
                                    EquipmentRawHistory.SummarizeAlarm(JsonSerializer.Deserialize(Of EquipmentAlarmEvidence)(If(reader.IsDBNull(16), "null", reader.GetString(16))))),
                                .Members = If(reader.IsDBNull(10), Array.Empty(Of String)(), reader.GetFieldValue(Of String())(10))}
                            For index = 0 To wideMetrics.Length - 1
                                observation.Values(wideMetrics(index).Id) = If(reader.IsDBNull(index + 17), CType(Nothing, Double?), reader.GetDouble(index + 17))
                            Next
                            For Each metric In metrics.Where(Function(item) EstimateMetrics.ContainsKey(item.Id))
                                observation.Availability(metric.Id) = "Estimate was not recorded for this acquisition; older data is not backfilled."
                            Next
                            If Not reader.IsDBNull(11) Then
                                Using document = JsonDocument.Parse(reader.GetString(11))
                                    Dim estimates As JsonElement
                                    Dim version As JsonElement
                                    If document.RootElement.ValueKind = JsonValueKind.Object AndAlso
                                       document.RootElement.TryGetProperty("Estimates", estimates) AndAlso estimates.ValueKind = JsonValueKind.Object AndAlso
                                       estimates.TryGetProperty("Version", version) AndAlso version.ValueKind = JsonValueKind.Number AndAlso
                                       (version.GetInt32() = 1 OrElse version.GetInt32() = UpsInputEstimates.EvidenceVersion) Then
                                        For Each metric In metrics.Where(Function(item) EstimateMetrics.ContainsKey(item.Id))
                                            Dim recorded As JsonElement
                                            Dim value As JsonElement
                                            Dim number As Double
                                            Dim available = estimates.TryGetProperty("Values", recorded) AndAlso recorded.ValueKind = JsonValueKind.Object AndAlso
                                                recorded.TryGetProperty(metric.Id, value) AndAlso value.ValueKind = JsonValueKind.Number AndAlso
                                                value.TryGetDouble(number) AndAlso Double.IsFinite(number) AndAlso number >= 0
                                            observation.Values(metric.Id) = If(available, CType(number, Double?), Nothing)
                                            If Not available AndAlso estimates.TryGetProperty("Availability", recorded) AndAlso recorded.ValueKind = JsonValueKind.Object AndAlso
                                               recorded.TryGetProperty(metric.Id, value) AndAlso value.ValueKind = JsonValueKind.String Then
                                                observation.Availability(metric.Id) = value.GetString()
                                            End If
                                        Next
                                    End If
                                    Dim availability As JsonElement
                                    If document.RootElement.ValueKind = JsonValueKind.Object AndAlso
                                       document.RootElement.TryGetProperty("Availability", availability) AndAlso availability.ValueKind = JsonValueKind.Object Then
                                        For Each metric In metrics
                                            Dim aliasKey = DuplicateRawChannels.FirstOrDefault(Function(item) item.Value = metric.Id).Key
                                            Dim key = If(JsonChannels.ContainsKey(metric.Id), metric.Id.Substring(8),
                                                If(aliasKey IsNot Nothing, aliasKey.Substring(8), If(metric.Id = "in_freq_hz", "InputFrequency", "")))
                                            Dim reason As JsonElement
                                            If key.Length > 0 AndAlso availability.TryGetProperty(key, reason) AndAlso reason.ValueKind = JsonValueKind.String Then
                                                observation.Availability(metric.Id) = reason.GetString()
                                            End If
                                        Next
                                    End If
                                    For Each metric In metrics.Where(Function(item) JsonChannels.ContainsKey(item.Id))
                                        Dim definition = JsonChannels(metric.Id)
                                        Dim group As JsonElement
                                        Dim value As JsonElement
                                        Dim number As Double
                                        Dim available = document.RootElement.ValueKind = JsonValueKind.Object AndAlso
                                            document.RootElement.TryGetProperty(If(definition.PerPhase, "Phases", "Values"), group) AndAlso group.ValueKind = JsonValueKind.Object AndAlso
                                            group.TryGetProperty(definition.Key, value)
                                        If available AndAlso definition.PerPhase Then
                                            Dim phase = Integer.Parse(metric.Id.Substring(metric.Id.Length - 1), CultureInfo.InvariantCulture) - 1
                                            available = value.ValueKind = JsonValueKind.Array AndAlso value.GetArrayLength() > phase
                                            If available Then value = value(phase)
                                        End If
                                        observation.Values(metric.Id) = If(available AndAlso value.ValueKind = JsonValueKind.Number AndAlso
                                            value.TryGetDouble(number) AndAlso Double.IsFinite(number), CType(number, Double?), Nothing)
                                    Next
                                End Using
                            End If
                            raw.Add(observation)
                        End While
                    End Using
                End Using
            End Using
        End Using
        Return Project(request, raw)
    End Function

    Public Shared Function Project(request As EquipmentHistoryRequest, raw As IReadOnlyList(Of UpsHistoryObservation)) As IReadOnlyList(Of EquipmentHistoryRow)
        Dim rows As New List(Of EquipmentHistoryRow)()
        For Each bucket In raw.GroupBy(Function(item) item.TimestampUtc).OrderBy(Function(group) group.Key)
            For Each metric In request.Metrics
                Dim observations = bucket.ToArray()
                Dim valid = observations.Where(Function(item) String.Equals(item.Quality, "Good", StringComparison.OrdinalIgnoreCase) AndAlso item.Revision >= metric.MinimumMappingRevision AndAlso
                    item.Values.ContainsKey(metric.Id) AndAlso item.Values(metric.Id).HasValue AndAlso Double.IsFinite(item.Values(metric.Id).Value)).ToArray()
                Dim row As New EquipmentHistoryRow With {.EquipmentId = request.Equipment.Id, .MetricId = metric.Id,
                    .TimestampUtc = bucket.Key, .Members = valid.Length, .ExpectedMembers = 1,
                    .Evidence = String.Join(" | ", observations.Select(Function(item) item.DeviceId & ": " & item.Evidence)),
                    .AlarmSummary = String.Join(" | ", observations.Select(Function(item) item.AlarmSummary).Distinct())}
                If request.Equipment.IsAggregate Then
                    Dim expected = observations(0).Members.OrderBy(Function(key) key, StringComparer.Ordinal).ToArray()
                    row.ExpectedMembers = expected.Length
                    Dim cohortMatches = expected.Length > 0 AndAlso expected.Distinct(StringComparer.Ordinal).Count() = expected.Length AndAlso
                        observations.All(Function(item) item.Members.OrderBy(Function(key) key, StringComparer.Ordinal).SequenceEqual(expected)) AndAlso
                        observations.Select(Function(item) item.DeviceId).OrderBy(Function(key) key, StringComparer.Ordinal).SequenceEqual(expected)
                    Dim timed = valid.Where(Function(item) item.AcquiredUtc.HasValue).ToArray()
                    Dim interval = If(timed.Length > 0 AndAlso timed.All(Function(item) item.RecordedIntervalSeconds.HasValue),
                        timed.Min(Function(item) item.RecordedIntervalSeconds.Value), request.Equipment.IntervalSeconds)
                    Dim aligned = timed.Length = observations.Length AndAlso timed.Length > 0 AndAlso
                        (timed.Max(Function(item) item.AcquiredUtc.Value) - timed.Min(Function(item) item.AcquiredUtc.Value)).TotalSeconds <= interval AndAlso
                        timed.All(Function(item) Math.Abs((item.TimestampUtc - item.AcquiredUtc.Value).TotalSeconds) <= 2 * interval + 5)
                    row.Quality = If(expected.Length = 0, "Membership unknown", If(Not cohortMatches OrElse valid.Length <> expected.Length,
                        "Incomplete cohort", If(Not aligned, "Unaligned / stale sources", "Good")))
                    If row.Quality = "Good" Then
                        Dim total = valid.Sum(Function(item) item.Values(metric.Id).Value)
                        If Double.IsFinite(total) Then
                            row.Value = total
                        Else
                            row.Quality = "Non-finite aggregate"
                        End If
                        row.AcquiredUtc = timed.Min(Function(item) item.AcquiredUtc.Value)
                        row.SourceTimes = timed.ToDictionary(Function(item) item.DeviceId, Function(item) item.AcquiredUtc.Value)
                        row.RecordedIntervalSeconds = If(timed.All(Function(item) item.RecordedIntervalSeconds.HasValue),
                            CType(timed.Min(Function(item) item.RecordedIntervalSeconds.Value), Double?), Nothing)
                        row.EnergyEligible = row.RecordedIntervalSeconds.HasValue
                        row.Revision = timed.Min(Function(item) item.Revision)
                    End If
                    row.Evidence &= "; aggregate is a derived UPS output sum, not physical ATS metering."
                Else
                    Dim item = observations.Single()
                    row.Revision = item.Revision
                    row.AcquiredUtc = item.AcquiredUtc
                    row.RecordedIntervalSeconds = item.RecordedIntervalSeconds
                    row.EnergyEligible = item.RecordedIntervalSeconds.HasValue AndAlso Not metric.IsEstimate
                    row.Quality = If(Not String.Equals(item.Quality, "Good", StringComparison.OrdinalIgnoreCase), item.Quality, If(item.Revision < metric.MinimumMappingRevision, "Legacy mapping excluded",
                        If(valid.Length = 0, "Unavailable channel" &
                            If(item.Availability.ContainsKey(metric.Id), ": " & item.Availability(metric.Id), ""), "Good")))
                    If row.Quality = "Good" Then row.Value = item.Values(metric.Id)
                End If
                EquipmentHistoryService.CheckLimit(rows.Count + 1)
                rows.Add(row)
            Next
        Next
        Return rows
    End Function
End Class

Public NotInheritable Class UpsHistoryObservation
    Public Property DeviceId As String = String.Empty
    Public Property TimestampUtc As DateTime
    Public Property AcquiredUtc As DateTime?
    Public Property Quality As String = String.Empty
    Public Property Revision As Integer
    Public Property RecordedIntervalSeconds As Double?
    Public Property Members As String() = Array.Empty(Of String)()
    Public Property Evidence As String = String.Empty
    Public Property AlarmSummary As String = "Alarm evidence unavailable"
    Public ReadOnly Property Values As New Dictionary(Of String, Double?)(StringComparer.Ordinal)
    Public ReadOnly Property Availability As New Dictionary(Of String, String)(StringComparer.Ordinal)
End Class
