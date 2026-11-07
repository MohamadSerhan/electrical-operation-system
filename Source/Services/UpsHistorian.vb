Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Text.Json
Imports Npgsql
Imports NpgsqlTypes

Public NotInheritable Class UpsHistorianDiagnostics
    Public Property Enabled As Boolean
    Public Property LastWriteUtc As DateTime?
    Public Property LastRowsWritten As Integer
    Public Property LastRowsVerified As Integer
    Public Property TotalRowsWritten As Long
    Public Property LastError As String = String.Empty
    Public Property LastRetentionUtc As DateTime?
    Public Property BufferedCommands As Long
End Class

Public NotInheritable Class UpsTrendSample
    Public Property TimestampUtc As DateTime
    Public Property Quality As String = String.Empty
    Public Property OutputKw As Double?
    Public Property LoadMaxPercent As Double?
    Public Property BatteryChargePercent As Double?
    Public Property MeasurementRevision As Integer
End Class

Public NotInheritable Class UpsColumnSample
    Public Property TimestampUtc As DateTime
    Public Property Values As Double?()
End Class

Public NotInheritable Class UpsAlarmEvent
    Public Property EventId As Guid
    Public Property TimestampUtc As DateTime
    Public Property Condition As String = String.Empty
    Public Property Category As String = String.Empty
    Public Property State As String = String.Empty
    Public Property Severity As String = String.Empty
    Public Property Acknowledged As String = String.Empty
    Public Property Detail As String = String.Empty
End Class

' Records one row per UPS per interval into PostgreSQL table ups_samples (wide row with quality).
' Non-good polls are recorded with their quality and NULL values so gaps are explicit, never zero.
Public NotInheritable Class UpsHistorian
    Implements IDisposable

    Private Const SchemaSql As String = "
CREATE TABLE IF NOT EXISTS ups_samples (
    device_id text NOT NULL,
    sampled_utc timestamptz NOT NULL,
    ats_bank smallint,
    quality text NOT NULL,
    output_kw double precision,
    load_max_pct double precision,
    load_l1 double precision, load_l2 double precision, load_l3 double precision,
    out_v_l1 double precision, out_v_l2 double precision, out_v_l3 double precision,
    out_i_l1 double precision, out_i_l2 double precision, out_i_l3 double precision,
    out_w_l1 double precision, out_w_l2 double precision, out_w_l3 double precision,
    out_freq_hz double precision,
    in_v_l1 double precision, in_v_l2 double precision, in_v_l3 double precision,
    in_i_l1 double precision, in_i_l2 double precision, in_i_l3 double precision,
    in_freq_hz double precision,
    bypass_v_l1 double precision, bypass_v_l2 double precision, bypass_v_l3 double precision,
    battery_status smallint,
    battery_charge_pct double precision,
    battery_runtime_min double precision,
    battery_voltage_v double precision,
    battery_current_a double precision,
    battery_temp_c double precision,
    seconds_on_battery double precision,
    output_source smallint,
    alarms_present integer,
    PRIMARY KEY (device_id, sampled_utc)
);
CREATE INDEX IF NOT EXISTS ix_ups_samples_time ON ups_samples (sampled_utc);
ALTER TABLE ups_samples ADD COLUMN IF NOT EXISTS measurement_revision integer NOT NULL DEFAULT 1;
ALTER TABLE ups_samples ADD COLUMN IF NOT EXISTS measurement_map text;
ALTER TABLE ups_samples ADD COLUMN IF NOT EXISTS out_v_reference text;
ALTER TABLE ups_samples ADD COLUMN IF NOT EXISTS in_v_reference text;
ALTER TABLE ups_samples ADD COLUMN IF NOT EXISTS bypass_v_reference text;
ALTER TABLE ups_samples ADD COLUMN IF NOT EXISTS output_kva double precision;
ALTER TABLE ups_samples ADD COLUMN IF NOT EXISTS output_pf double precision;
ALTER TABLE ups_samples ADD COLUMN IF NOT EXISTS out_v_ll1 double precision;
ALTER TABLE ups_samples ADD COLUMN IF NOT EXISTS out_v_ll2 double precision;
ALTER TABLE ups_samples ADD COLUMN IF NOT EXISTS out_v_ll3 double precision;
ALTER TABLE ups_samples ADD COLUMN IF NOT EXISTS reported_output_kw double precision;
ALTER TABLE ups_samples ADD COLUMN IF NOT EXISTS quality_detail text;
ALTER TABLE ups_samples ADD COLUMN IF NOT EXISTS acquired_utc timestamptz;
ALTER TABLE ups_samples ADD COLUMN IF NOT EXISTS device_label text;
ALTER TABLE ups_samples ADD COLUMN IF NOT EXISTS device_model text;
ALTER TABLE ups_samples ADD COLUMN IF NOT EXISTS bank_members text[];
ALTER TABLE ups_samples ADD COLUMN IF NOT EXISTS fleet_members text[];
ALTER TABLE ups_samples ADD COLUMN IF NOT EXISTS alarm_evidence jsonb;
ALTER TABLE ups_samples ADD COLUMN IF NOT EXISTS channel_evidence jsonb;
ALTER TABLE ups_samples ADD COLUMN IF NOT EXISTS recording_interval_seconds integer;
ALTER TABLE ups_samples ADD COLUMN IF NOT EXISTS alarm_evaluation jsonb;"

    Private ReadOnly _acquisition As UpsAcquisitionService
    Private ReadOnly _diagnosticsGate As New Object()
    Private ReadOnly _diagnostics As New UpsHistorianDiagnostics()
    Private _cancellation As CancellationTokenSource
    Private _loop As Task
    Private _schemaReady As Boolean
    Private _outbox As DurableRecordingOutbox
    Private _alarms As EquipmentAlarmRecorder
    Private _recordingSettings As Func(Of RecordingSettings)
    Private _ownsOutbox As Boolean
    Private _lastSlot As DateTime?
    Private _lastInterval As Integer

    Public Shared Property Current As UpsHistorian

    Public Sub New(acquisition As UpsAcquisitionService)
        _acquisition = acquisition
    End Sub

    Public Function GetDiagnostics() As UpsHistorianDiagnostics
        SyncLock _diagnosticsGate
            Return New UpsHistorianDiagnostics With {
                .Enabled = UpsSettingsStore.Current.HistorianEnabled,
                .LastWriteUtc = If(_outbox Is Nothing, Nothing, _outbox.LastUpsWriteUtc),
                .LastRowsWritten = If(_outbox Is Nothing, 0, _outbox.LastUpsRows),
                .LastRowsVerified = If(_outbox Is Nothing, 0, _outbox.LastUpsRows),
                .TotalRowsWritten = If(_outbox Is Nothing, 0, _outbox.TotalUpsRows),
                .LastError = If(_outbox IsNot Nothing AndAlso _outbox.LastError.Length > 0, _outbox.LastError, _diagnostics.LastError),
                .BufferedCommands = If(_outbox Is Nothing, 0, _outbox.PendingCount),
                .LastRetentionUtc = _diagnostics.LastRetentionUtc}
        End SyncLock
    End Function

    Public Sub Start(Optional outbox As DurableRecordingOutbox = Nothing, Optional alarms As EquipmentAlarmRecorder = Nothing,
                     Optional recordingSettings As Func(Of RecordingSettings) = Nothing)
        If _loop IsNot Nothing AndAlso Not _loop.IsCompleted Then Return
        _ownsOutbox = outbox Is Nothing
        _outbox = If(outbox, New DurableRecordingOutbox(System.IO.Path.Combine(AppContext.BaseDirectory, "EOEMSData", "Historian", "recording-outbox.db")))
        _alarms = If(alarms, New EquipmentAlarmRecorder(_outbox))
        _recordingSettings = If(recordingSettings, Function() New RecordingSettings())
        _outbox.Start()
        AddHandler _acquisition.SnapshotRecorded, AddressOf OnSnapshotRecorded
        _cancellation = New CancellationTokenSource()
        Dim token = _cancellation.Token
        _loop = Task.Run(Function() RunAsync(token))
    End Sub

    Public Async Function StopAsync() As Task
        If _cancellation Is Nothing Then Return
        RemoveHandler _acquisition.SnapshotRecorded, AddressOf OnSnapshotRecorded
        _cancellation.Cancel()
        If _loop IsNot Nothing Then Await _loop.ConfigureAwait(False)
        If _ownsOutbox Then Await _outbox.StopAsync().ConfigureAwait(False)
    End Function

    Private Sub OnSnapshotRecorded(snapshot As UpsSnapshot)
        Try
            Dim settings = UpsSettingsStore.Current
            Dim device = settings.Devices.SingleOrDefault(Function(item) item.Id = snapshot.DeviceId)
            Dim recording = _recordingSettings()
            _alarms.Observe("UPS", snapshot.DeviceId, snapshot.TimestampUtc,
                EquipmentAlarmRecorder.UpsEvidence(snapshot, AlarmSettingsStore.Current.Ups))
        Catch ex As Exception When TypeOf ex Is Microsoft.Data.Sqlite.SqliteException OrElse TypeOf ex Is System.IO.IOException OrElse
                                   TypeOf ex Is UnauthorizedAccessException
            SyncLock _diagnosticsGate
                _diagnostics.LastError = "UPS raw/alarm recording failed: " & ex.Message
            End SyncLock
        End Try
    End Sub

    Private Async Function RunAsync(token As CancellationToken) As Task
        While Not token.IsCancellationRequested
            Dim settings = UpsSettingsStore.Current
            Dim interval = Math.Max(10, settings.HistorianIntervalSeconds)
            If settings.HistorianEnabled AndAlso settings.AcquisitionEnabled Then
                Try
                    Dim slot = SlotAt(DateTime.UtcNow, interval)
                    If _lastInterval <> interval Then _lastSlot = Nothing
                    _lastInterval = interval
                    If Not _lastSlot.HasValue OrElse slot > _lastSlot.Value Then
                        Dim first = If(_lastSlot.HasValue, _lastSlot.Value.AddSeconds(interval), slot)
                        While first <= slot
                            CaptureSlot(settings, first, first < slot)
                            _lastSlot = first
                            first = first.AddSeconds(interval)
                        End While
                    End If
                Catch ex As Exception When TypeOf ex Is NpgsqlException OrElse TypeOf ex Is InvalidOperationException OrElse
                                           TypeOf ex Is TimeoutException OrElse TypeOf ex Is System.IO.IOException OrElse
                                           TypeOf ex Is Microsoft.Data.Sqlite.SqliteException OrElse TypeOf ex Is UnauthorizedAccessException
                    SyncLock _diagnosticsGate
                        _diagnostics.LastError = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) & "  " & ex.Message
                    End SyncLock
                End Try
            Else
                _lastSlot = Nothing
            End If
            Try
                Await Task.Delay(100, token).ConfigureAwait(False)
            Catch ex As OperationCanceledException
                Exit While
            End Try
        End While
    End Function

    Public Shared Function SlotAt(utc As DateTime, intervalSeconds As Integer) As DateTime
        If utc.Kind <> DateTimeKind.Utc Then Throw New ArgumentException("A UTC slot time is required.", NameOf(utc))
        If intervalSeconds < 10 OrElse intervalSeconds > 3600 Then Throw New ArgumentOutOfRangeException(NameOf(intervalSeconds))
        Dim ticks = TimeSpan.TicksPerSecond * intervalSeconds
        Return New DateTime(utc.Ticks - utc.Ticks Mod ticks, DateTimeKind.Utc)
    End Function

    Private Sub CaptureSlot(settings As UpsSettings, stamp As DateTime, missed As Boolean)
        Dim snapshots = _acquisition.GetSnapshots()
        Dim commands As New List(Of RecordedCommand)()
        For Each device In settings.Devices.Where(Function(item) item.Enabled)
            Dim snapshot As UpsSnapshot = Nothing
            If Not missed Then snapshots.TryGetValue(device.Id, snapshot)
            If missed Then snapshot = New UpsSnapshot With {
                .DeviceId = device.Id, .Quality = UpsQuality.NoData,
                .QualityDetail = "Recording deadline missed; no historical acquisition is invented."}
            commands.Add(CreateRecordedRow(device, snapshot, stamp, settings, includeAlarmEvidence:=False))
        Next
        _outbox.Enqueue(commands)
    End Sub

    Private Sub EnsureSchema(connection As NpgsqlConnection)
        If _schemaReady Then Return
        EnsureHistorySchema(connection)
        _schemaReady = True
    End Sub

    Public Shared Sub EnsureHistorySchema(connection As NpgsqlConnection, Optional token As CancellationToken = Nothing)
        token.ThrowIfCancellationRequested()
        Using command As New NpgsqlCommand(SchemaSql, connection)
            Using registration = token.Register(Sub() command.Cancel())
                command.ExecuteNonQuery()
            End Using
        End Using
    End Sub

    Private Shared Function InsertRow(connection As NpgsqlConnection, transaction As NpgsqlTransaction, device As UpsDevice,
                                      snapshot As UpsSnapshot, stamp As DateTime, Optional settings As UpsSettings = Nothing) As Integer
        Return CreateRecordedRow(device, snapshot, stamp, settings).Execute(connection, transaction)
    End Function

    Public Shared Function CreateRecordedRow(device As UpsDevice, snapshot As UpsSnapshot, stamp As DateTime,
                                            Optional settings As UpsSettings = Nothing,
                                            Optional includeAlarmEvidence As Boolean = True) As RecordedCommand
        Const sql As String = "INSERT INTO ups_samples (device_id, sampled_utc, ats_bank, quality, output_kw, load_max_pct,
 load_l1, load_l2, load_l3, out_v_l1, out_v_l2, out_v_l3, out_i_l1, out_i_l2, out_i_l3, out_w_l1, out_w_l2, out_w_l3, out_freq_hz,
 in_v_l1, in_v_l2, in_v_l3, in_i_l1, in_i_l2, in_i_l3, in_freq_hz, bypass_v_l1, bypass_v_l2, bypass_v_l3,
 battery_status, battery_charge_pct, battery_runtime_min, battery_voltage_v, battery_current_a, battery_temp_c, seconds_on_battery,
 output_source, alarms_present, measurement_revision, measurement_map, out_v_reference, in_v_reference, bypass_v_reference,
 output_kva, output_pf, out_v_ll1, out_v_ll2, out_v_ll3, reported_output_kw, quality_detail,
 acquired_utc, device_label, device_model, bank_members, fleet_members, alarm_evidence, channel_evidence, recording_interval_seconds, alarm_evaluation)
 VALUES (@id, @t, @bank, @q, @kw, @lmax, @l1, @l2, @l3, @ov1, @ov2, @ov3, @oi1, @oi2, @oi3, @ow1, @ow2, @ow3, @of,
 @iv1, @iv2, @iv3, @ii1, @ii2, @ii3, @if, @bv1, @bv2, @bv3, @bs, @bc, @br, @bvv, @bi, @bt, @sob, @src, @al,
 @rev, @map, @ovref, @ivref, @bvref, @kva, @pf, @ll1, @ll2, @ll3, @reportedkw, @detail,
 @acquired, @label, @model, @bankmembers, @fleetmembers, @alarmevidence, @channels, @interval, @evaluation)
 ON CONFLICT (device_id, sampled_utc) DO NOTHING"
        Dim good = snapshot IsNot Nothing AndAlso snapshot.IsGood
        Dim pick = Function(value As Double?) As Object
                       Return If(good AndAlso value.HasValue AndAlso Double.IsFinite(value.Value), CObj(value.Value), DBNull.Value)
                   End Function
        Using command As New NpgsqlCommand(sql)
            command.Parameters.AddWithValue("id", device.Id)
            command.Parameters.AddWithValue("t", NpgsqlDbType.TimestampTz, stamp)
            command.Parameters.AddWithValue("bank", CShort(device.AtsBank))
            command.Parameters.AddWithValue("q", UpsQualityText.Describe(If(snapshot Is Nothing, UpsQuality.NoData, snapshot.Quality)))
            Dim s = If(snapshot, New UpsSnapshot())
            command.Parameters.AddWithValue("evaluation", NpgsqlDbType.Jsonb,
                If(includeAlarmEvidence, CObj(JsonSerializer.Serialize(EquipmentAlarmRecorder.UpsEvidence(snapshot, AlarmSettingsStore.Current.Ups))), DBNull.Value))
            command.Parameters.AddWithValue("acquired", NpgsqlDbType.TimestampTz,
                If(snapshot IsNot Nothing AndAlso s.TimestampUtc.Kind = DateTimeKind.Utc AndAlso s.TimestampUtc <> DateTime.MinValue,
                   CObj(s.TimestampUtc), DBNull.Value))
            command.Parameters.AddWithValue("label", device.Label)
            command.Parameters.AddWithValue("model", device.Model)
            command.Parameters.AddWithValue("interval", NpgsqlDbType.Integer,
                If(settings Is Nothing, CObj(DBNull.Value), settings.HistorianIntervalSeconds))
            command.Parameters.AddWithValue("bankmembers", NpgsqlDbType.Array Or NpgsqlDbType.Text,
                If(settings Is Nothing, CObj(DBNull.Value), settings.Devices.Where(Function(item) item.AtsBank = device.AtsBank).
                    Select(Function(item) item.Id).OrderBy(Function(key) key, StringComparer.Ordinal).ToArray()))
            command.Parameters.AddWithValue("fleetmembers", NpgsqlDbType.Array Or NpgsqlDbType.Text,
                If(settings Is Nothing, CObj(DBNull.Value), settings.Devices.Select(Function(item) item.Id).
                    OrderBy(Function(key) key, StringComparer.Ordinal).ToArray()))
            command.Parameters.AddWithValue("alarmevidence", NpgsqlDbType.Jsonb, If(includeAlarmEvidence, CObj(JsonSerializer.Serialize(New With {
                .Complete = s.AlarmDetailsComplete, .Status = s.AlarmDetailsStatus, .Alarms = s.CardAlarms})), DBNull.Value))
            command.Parameters.AddWithValue("channels", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(New With {
                .Values = If(good, s.Values.ToDictionary(Function(item) item.Key,
                    Function(item) If(Double.IsFinite(item.Value), CType(item.Value, Double?), Nothing)),
                    New Dictionary(Of String, Double?)()),
                .Phases = If(good, s.Phases.ToDictionary(Function(item) item.Key,
                    Function(item) item.Value.Select(Function(value) If(value.HasValue AndAlso Double.IsFinite(value.Value), value, Nothing)).ToArray()),
                    New Dictionary(Of String, Double?())()),
                .NumericEvidenceRule = "Non-finite numeric values are stored as null, never zero.",
                .Sources = s.MeasurementSources, .InvalidFields = s.InvalidFields, .PartialGroups = s.PartialGroups,
                .Availability = s.ChannelAvailability(), .Estimates = UpsInputEstimates.RecordedEvidence(s)}))
            command.Parameters.AddWithValue("rev", s.MeasurementRevision)
            command.Parameters.AddWithValue("map", s.MeasurementMap)
            command.Parameters.AddWithValue("ovref", s.OutputVoltageReference)
            command.Parameters.AddWithValue("ivref", s.InputVoltageReference)
            command.Parameters.AddWithValue("bvref", s.BypassVoltageReference)
            command.Parameters.AddWithValue("kva", NpgsqlDbType.Double, pick(s.OutputKvaDerived))
            command.Parameters.AddWithValue("pf", NpgsqlDbType.Double, pick(s.PowerFactorDerived))
            command.Parameters.AddWithValue("kw", NpgsqlDbType.Double, pick(s.AccountingOutputKw))
            command.Parameters.AddWithValue("reportedkw", NpgsqlDbType.Double, pick(s.OutputKw))
            command.Parameters.AddWithValue("detail", s.QualityDetail)
            command.Parameters.AddWithValue("lmax", NpgsqlDbType.Double, pick(s.PhaseMax("OutputLoad")))
            Dim phaseParameters = {("l", "OutputLoad"), ("ov", "OutputVoltage"), ("oi", "OutputCurrent"), ("ow", "OutputPower"),
                                   ("iv", "InputVoltage"), ("ii", "InputCurrent"), ("bv", "BypassVoltage"), ("ll", "OutputVoltageLL")}
            For Each entry In phaseParameters
                For index = 0 To 2
                    command.Parameters.AddWithValue(entry.Item1 & (index + 1).ToString(CultureInfo.InvariantCulture), NpgsqlDbType.Double, pick(s.Phase(entry.Item2, index)))
                Next
            Next
            command.Parameters.AddWithValue("of", NpgsqlDbType.Double, pick(s.Value("OutputFrequency")))
            command.Parameters.AddWithValue("if", NpgsqlDbType.Double, pick(If(s.Value("InputFrequency"), s.Phase("InputFrequency", 0))))
            Dim status = s.Value("BatteryStatus")
            command.Parameters.AddWithValue("bs", NpgsqlDbType.Smallint, If(good AndAlso status.HasValue, CObj(CShort(status.Value)), DBNull.Value))
            command.Parameters.AddWithValue("bc", NpgsqlDbType.Double, pick(s.Value("ChargePercent")))
            command.Parameters.AddWithValue("br", NpgsqlDbType.Double, pick(s.Value("RuntimeMinutes")))
            command.Parameters.AddWithValue("bvv", NpgsqlDbType.Double, pick(s.Value("BatteryVoltage")))
            command.Parameters.AddWithValue("bi", NpgsqlDbType.Double, pick(s.Value("BatteryCurrent")))
            command.Parameters.AddWithValue("bt", NpgsqlDbType.Double, pick(s.Value("BatteryTemperature")))
            command.Parameters.AddWithValue("sob", NpgsqlDbType.Double, pick(s.Value("SecondsOnBattery")))
            Dim source = s.Value("OutputSource")
            command.Parameters.AddWithValue("src", NpgsqlDbType.Smallint, If(good AndAlso source.HasValue, CObj(CShort(source.Value)), DBNull.Value))
            Dim alarms = s.Value("AlarmsPresent")
            command.Parameters.AddWithValue("al", NpgsqlDbType.Integer, If(good AndAlso alarms.HasValue, CObj(CInt(alarms.Value)), DBNull.Value))
            Return RecordedCommand.Capture(command)
        End Using
    End Function

    Private Shared ReadOnly TrendColumns As New HashSet(Of String)(StringComparer.Ordinal) From {
        "output_kw", "load_max_pct", "out_v_l1", "out_v_l2", "out_v_l3", "out_i_l1", "out_i_l2", "out_i_l3",
        "in_v_l1", "in_v_l2", "in_v_l3", "in_i_l1", "in_i_l2", "in_i_l3", "bypass_v_l1", "bypass_v_l2", "bypass_v_l3",
        "out_freq_hz", "in_freq_hz", "battery_charge_pct", "battery_runtime_min", "battery_voltage_v", "battery_current_a",
        "battery_temp_c", "load_l1", "load_l2", "load_l3", "output_kva", "output_pf"}

    ' Reads any whitelisted numeric columns; with bucketSeconds > 0 values are averaged per time bucket to keep long ranges light.
    Public Function ReadColumns(deviceId As String, fromUtc As DateTime, toUtc As DateTime, columns As String(), bucketSeconds As Integer) As IReadOnlyList(Of UpsColumnSample)
        For Each column In columns
            If Not TrendColumns.Contains(column) Then Throw New ArgumentException("Unsupported trend column: " & column)
        Next
        Dim samples As New List(Of UpsColumnSample)()
        Using connection = PostgresDatabase.Open()
            EnsureSchema(connection)
            Dim sql As String
            If bucketSeconds > 0 Then
                sql = "SELECT to_timestamp(floor(extract(epoch FROM sampled_utc)/@b)*@b) AS t, " &
                    String.Join(", ", columns.Select(Function(column) "avg(" & column & ")")) &
                    " FROM ups_samples WHERE device_id=@id AND sampled_utc>=@f AND sampled_utc<=@t GROUP BY 1 ORDER BY 1"
            Else
                sql = "SELECT sampled_utc, " & String.Join(", ", columns) &
                    " FROM ups_samples WHERE device_id=@id AND sampled_utc>=@f AND sampled_utc<=@t ORDER BY sampled_utc"
            End If
            Using command As New NpgsqlCommand(sql, connection)
                command.CommandTimeout = 60
                command.Parameters.AddWithValue("id", deviceId)
                command.Parameters.AddWithValue("f", NpgsqlDbType.TimestampTz, fromUtc)
                command.Parameters.AddWithValue("t", NpgsqlDbType.TimestampTz, toUtc)
                If bucketSeconds > 0 Then command.Parameters.AddWithValue("b", NpgsqlDbType.Double, CDbl(bucketSeconds))
                Using reader = command.ExecuteReader()
                    While reader.Read()
                        Dim values(columns.Length - 1) As Double?
                        For index = 0 To columns.Length - 1
                            values(index) = If(reader.IsDBNull(index + 1), CType(Nothing, Double?), Convert.ToDouble(reader.GetValue(index + 1), CultureInfo.InvariantCulture))
                        Next
                        samples.Add(New UpsColumnSample With {.TimestampUtc = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc), .Values = values})
                    End While
                End Using
            End Using
        End Using
        Return samples
    End Function

    Public Shared Function ReadAlarmEvents(deviceId As String, fromUtc As DateTime, toUtc As DateTime, limit As Integer,
                                          Optional equipmentType As String = "UPS", Optional before As UpsAlarmEvent = Nothing,
                                          Optional cancellation As CancellationToken = Nothing) As IReadOnlyList(Of UpsAlarmEvent)
        If fromUtc.Kind <> DateTimeKind.Utc OrElse toUtc.Kind <> DateTimeKind.Utc OrElse fromUtc >= toUtc Then
            Throw New ArgumentException("Alarm history requires an increasing UTC time range.")
        End If
        If limit < 1 OrElse limit > 1000 Then Throw New ArgumentOutOfRangeException(NameOf(limit))
        cancellation.ThrowIfCancellationRequested()
        Dim events As New List(Of UpsAlarmEvent)()
        Using connection = PostgresDatabase.Open()
            Using exists As New NpgsqlCommand("SELECT to_regclass('public.equipment_alarm_history') IS NOT NULL", connection)
                If Not CBool(exists.ExecuteScalar()) Then Throw New IO.InvalidDataException("Alarm history store is not initialized; history is unavailable, not normal.")
            End Using
            Using command As New NpgsqlCommand(
                "SELECT observed_utc,event_kind,alarm_key,current_state::text,event_id FROM equipment_alarm_history " &
                "WHERE equipment_type=@type AND equipment_id=@id AND observed_utc>=@f AND observed_utc<@t " &
                "AND left(alarm_key,2)<>'__' AND NOT(event_kind='ObservedInactive' AND previous_state IS NULL) " &
                If(before Is Nothing, "", "AND (observed_utc,event_id)<(@before,@event) ") &
                "ORDER BY observed_utc DESC,event_id DESC LIMIT @n", connection)
                command.CommandTimeout = 15
                command.Parameters.AddWithValue("type", equipmentType)
                command.Parameters.AddWithValue("id", deviceId)
                command.Parameters.AddWithValue("f", NpgsqlDbType.TimestampTz, fromUtc)
                command.Parameters.AddWithValue("t", NpgsqlDbType.TimestampTz, toUtc)
                command.Parameters.AddWithValue("n", limit)
                If before IsNot Nothing Then
                    command.Parameters.AddWithValue("before", NpgsqlDbType.TimestampTz, before.TimestampUtc)
                    command.Parameters.AddWithValue("event", before.EventId)
                End If
                Using registration = cancellation.Register(Sub() command.Cancel()),
                      reader = command.ExecuteReader()
                    While reader.Read()
                        cancellation.ThrowIfCancellationRequested()
                        Dim key = reader.GetString(2)
                        Dim state = If(reader.IsDBNull(3), Nothing, JsonSerializer.Deserialize(Of RecordedAlarmState)(reader.GetString(3)))
                        If state IsNot Nothing Then
                            Dim evidence As New EquipmentAlarmEvidence()
                            evidence.Conditions(key) = state
                            EquipmentAlarmRecorder.SetLabels(evidence)
                        End If
                        Dim kind = reader.GetString(1)
                        Dim known = state IsNot Nothing AndAlso state.Active.HasValue
                        events.Add(New UpsAlarmEvent With {
                            .EventId = reader.GetGuid(4),
                            .TimestampUtc = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                            .Condition = If(state Is Nothing OrElse String.IsNullOrWhiteSpace(state.Name), key, state.Name),
                            .Category = If(state Is Nothing, String.Empty, state.Category),
                            .State = If(Not known, "Unknown", If(state.Active.Value, "ACTIVE", If(kind = "ObservedCleared", "Cleared", "Normal"))),
                            .Severity = If(Not known, "—", If(state.Active.Value, state.Severity, "Normal")),
                            .Acknowledged = If(state Is Nothing OrElse Not state.Acknowledged.HasValue, String.Empty, If(state.Acknowledged.Value, "Acknowledged", "Unacknowledged")),
                            .Detail = If(state Is Nothing, String.Empty, state.Detail)})
                    End While
                End Using
            End Using
        End Using
        cancellation.ThrowIfCancellationRequested()
        Return events
    End Function

    Public Function ReadTrend(deviceId As String, fromUtc As DateTime, toUtc As DateTime, Optional bucketSeconds As Integer = 0) As IReadOnlyList(Of UpsTrendSample)
        Dim samples As New List(Of UpsTrendSample)()
        Using connection = PostgresDatabase.Open()
            EnsureSchema(connection)
            Dim sql = If(bucketSeconds > 0,
                "SELECT to_timestamp(floor(extract(epoch FROM sampled_utc)/@b)*@b) AS t, 'Good', avg(CASE WHEN measurement_revision >= 2 THEN output_kw END), avg(load_max_pct), avg(battery_charge_pct), min(measurement_revision) FROM ups_samples " &
                "WHERE device_id = @id AND sampled_utc >= @f AND sampled_utc <= @t GROUP BY 1 ORDER BY 1",
                "SELECT sampled_utc, quality, CASE WHEN measurement_revision >= 2 THEN output_kw END, load_max_pct, battery_charge_pct, measurement_revision FROM ups_samples " &
                "WHERE device_id = @id AND sampled_utc >= @f AND sampled_utc <= @t ORDER BY sampled_utc")
            Using command As New NpgsqlCommand(sql, connection)
                command.CommandTimeout = 60
                If bucketSeconds > 0 Then command.Parameters.AddWithValue("b", NpgsqlDbType.Double, CDbl(bucketSeconds))
                command.Parameters.AddWithValue("id", deviceId)
                command.Parameters.AddWithValue("f", NpgsqlDbType.TimestampTz, fromUtc)
                command.Parameters.AddWithValue("t", NpgsqlDbType.TimestampTz, toUtc)
                Using reader = command.ExecuteReader()
                    While reader.Read()
                        samples.Add(New UpsTrendSample With {
                            .TimestampUtc = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                            .Quality = reader.GetString(1),
                            .OutputKw = If(reader.IsDBNull(2), CType(Nothing, Double?), reader.GetDouble(2)),
                            .LoadMaxPercent = If(reader.IsDBNull(3), CType(Nothing, Double?), reader.GetDouble(3)),
                            .BatteryChargePercent = If(reader.IsDBNull(4), CType(Nothing, Double?), reader.GetDouble(4)),
                            .MeasurementRevision = reader.GetInt32(5)})
                    End While
                End Using
            End Using
        End Using
        Return samples
    End Function

    ' Energy (kWh) integrated from historian kW samples (trapezoidal). Gaps longer than three
    ' sample intervals are not bridged, so missing data lowers – never invents – energy.
    Public Shared Function IntegrateKwh(samples As IReadOnlyList(Of UpsTrendSample), intervalSeconds As Integer,
                                        ByRef coveredHours As Double) As Double?
        coveredHours = 0
        Dim energy = 0.0R
        Dim any = False
        Dim maxGap = TimeSpan.FromSeconds(Math.Max(10, intervalSeconds) * 3)
        For index = 1 To samples.Count - 1
            Dim a = samples(index - 1)
            Dim b = samples(index)
            If a.MeasurementRevision < UpsMeasurementMap.Revision OrElse b.MeasurementRevision < UpsMeasurementMap.Revision OrElse
               a.MeasurementRevision <> b.MeasurementRevision Then Continue For
            If Not a.OutputKw.HasValue OrElse Not b.OutputKw.HasValue Then Continue For
            Dim gap = b.TimestampUtc - a.TimestampUtc
            If gap <= TimeSpan.Zero OrElse gap > maxGap Then Continue For
            energy += (a.OutputKw.Value + b.OutputKw.Value) / 2.0R * gap.TotalHours
            coveredHours += gap.TotalHours
            any = True
        Next
        Return If(any, energy, CType(Nothing, Double?))
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        Task.Run(Function() StopAsync()).GetAwaiter().GetResult()
        If _cancellation IsNot Nothing Then
            _cancellation.Cancel()
            _cancellation.Dispose()
            _cancellation = Nothing
        End If
        If _ownsOutbox AndAlso _outbox IsNot Nothing Then _outbox.Dispose()
    End Sub
End Class
