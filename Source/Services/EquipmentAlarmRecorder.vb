Imports System.Globalization
Imports System.Linq
Imports System.Text.Json
Imports Npgsql
Imports NpgsqlTypes

Public NotInheritable Class RecordedAlarmState
    Public Property Name As String = String.Empty
    Public Property Category As String = String.Empty
    Public Property Active As Boolean?
    <System.Text.Json.Serialization.JsonIgnore(Condition:=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)>
    Public Property LastKnownActive As Boolean?
    Public Property Acknowledged As Boolean?
    Public Property Severity As String = "Unknown"
    Public Property Detail As String = String.Empty
End Class

Public NotInheritable Class EquipmentAlarmEvidence
    Public Property Quality As String = String.Empty
    Public Property Complete As Boolean
    Public Property Policy As EquipmentAlarmLimits
    Public Property RatedLoadKw As Double?
    Public Property Conditions As SortedDictionary(Of String, RecordedAlarmState) = New SortedDictionary(Of String, RecordedAlarmState)(StringComparer.Ordinal)
End Class

Public NotInheritable Class SavedEquipmentAlarmState
    Public Property SessionId As Guid
    Public Property ObservedUtc As DateTime
    Public Property AcquiredUtc As DateTime?
    Public Property Evidence As EquipmentAlarmEvidence
    Public Property LastRecordedUtc As New Dictionary(Of String, DateTime)(StringComparer.Ordinal)
End Class

' Events describe host observations, never exact device raise/clear times. Unknown evidence cannot clear an alarm.
Public NotInheritable Class EquipmentAlarmRecorder
    Private ReadOnly _outbox As DurableRecordingOutbox
    Private ReadOnly _session As Guid = Guid.NewGuid()
    Private ReadOnly _gate As New Object()
    Private ReadOnly _states As New Dictionary(Of String, SavedEquipmentAlarmState)(StringComparer.Ordinal)
    Private ReadOnly _utcNow As Func(Of DateTime)

    Public Sub New(outbox As DurableRecordingOutbox, Optional utcNow As Func(Of DateTime) = Nothing)
        _outbox = outbox
        _utcNow = If(utcNow, Function() DateTime.UtcNow)
    End Sub

    Public Function ReadCurrentState(equipmentType As String, equipmentId As String) As (State As SavedEquipmentAlarmState, CurrentSession As Boolean, ReadUtc As DateTime)
        SyncLock _gate
            Dim state As SavedEquipmentAlarmState = Nothing
            If Not _states.TryGetValue(equipmentType & ":" & equipmentId, state) Then
                Dim json = _outbox.ReadAlarmState(equipmentType & ":" & equipmentId)
                If json Is Nothing Then Return (Nothing, False, _utcNow())
                state = JsonSerializer.Deserialize(Of SavedEquipmentAlarmState)(json)
                If state IsNot Nothing Then _states(equipmentType & ":" & equipmentId) = state
            End If
            If state Is Nothing Then Throw New IO.InvalidDataException("Saved alarm state is missing.")
            Return (JsonSerializer.Deserialize(Of SavedEquipmentAlarmState)(JsonSerializer.Serialize(state)), state.SessionId = _session, _utcNow())
        End SyncLock
    End Function

    Public Shared Sub EnsureSchema(connection As NpgsqlConnection)
        Using command As New NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS equipment_alarm_history(" &
            "event_id uuid PRIMARY KEY, equipment_type text NOT NULL,equipment_id text NOT NULL," &
            "observed_utc timestamptz NOT NULL,acquired_utc timestamptz,session_id uuid NOT NULL," &
            "event_kind text NOT NULL,alarm_key text NOT NULL,previous_state jsonb,current_state jsonb," &
            "evidence jsonb NOT NULL);" &
            "CREATE INDEX IF NOT EXISTS ix_equipment_alarm_history_asset_time " &
            "ON equipment_alarm_history(equipment_type,equipment_id,observed_utc);" &
            "CREATE INDEX IF NOT EXISTS ix_equipment_alarm_history_asset_page " &
            "ON equipment_alarm_history(equipment_type,equipment_id,observed_utc DESC,event_id DESC);" &
            "CREATE OR REPLACE VIEW equipment_alarm_latest AS SELECT DISTINCT ON(equipment_type,equipment_id,alarm_key) " &
            "* FROM equipment_alarm_history ORDER BY equipment_type,equipment_id,alarm_key,observed_utc DESC,event_id DESC;", connection)
            command.ExecuteNonQuery()
        End Using
    End Sub

    Private Shared Function State(severity As AlarmSeverity, detail As String, known As Boolean) As RecordedAlarmState
        Return New RecordedAlarmState With {.Active = If(known, CType(severity <> AlarmSeverity.Normal, Boolean?), Nothing),
            .Severity = If(known, severity.ToString(), "Unknown"), .Detail = detail}
    End Function

    Public Shared Function TransformerEvidence(values As IReadOnlyList(Of Single), quality As String,
                                               limits As EquipmentAlarmLimits, maximumKw As Double,
                                               Optional totalTemperature As Double? = Nothing,
                                               Optional powerState As TransformerPowerState = TransformerPowerState.Unknown,
                                               Optional ecgOff As Boolean? = Nothing,
                                               Optional ecgBack As Boolean? = Nothing,
                                               Optional voltageEvaluable As Boolean = True) As EquipmentAlarmEvidence
        Dim good = quality = "Good"
        Dim pick As Func(Of Integer, Double?) =
            Function(index) If(good AndAlso values IsNot Nothing AndAlso index < values.Count AndAlso Single.IsFinite(values(index)),
                               CType(values(index), Double?), Nothing)
        Dim load = LoadGaugeMath.GetLoadPercent(pick(MeasurementCatalog.ActivePowerIndex), maximumKw)
        Dim temperature = If(totalTemperature, pick(15))
        Dim result As New EquipmentAlarmEvidence With {
            .Quality = quality, .Complete = good, .Policy = limits.Clone(), .RatedLoadKw = maximumKw}
        result.Conditions("communication") = State(If(good, AlarmSeverity.Normal, AlarmSeverity.Alarm), quality, True)
        result.Conditions("load") = State(AlarmEvaluator.LoadSeverity(load, limits), "Load percent: " & Number(load, 2), load.HasValue)
        result.Conditions("temperature") = State(AlarmEvaluator.TemperatureSeverity(temperature, limits),
            "Temperature C: " & Number(temperature), temperature.HasValue)
        For index = 0 To 2
            Dim voltage = pick(index)
            AddVoltageConditions(result, index, voltage, limits, voltageEvaluable)
        Next
        result.Conditions("transformer-off") = New RecordedAlarmState With {
            .Active = If(good AndAlso powerState <> TransformerPowerState.Unknown,
                CType(powerState = TransformerPowerState.Off, Boolean?), Nothing),
            .Severity = "Alarm", .Detail = "All phases <20V"}
        result.Conditions("ecg-off") = New RecordedAlarmState With {
            .Active = ecgOff, .Severity = "Alarm", .Detail = "All four transformers off"}
        If ecgBack.HasValue Then result.Conditions("ecg-back") = New RecordedAlarmState With {
            .Active = ecgBack, .Severity = "Info", .Detail = "All transformers on."}
        SetLabels(result)
        Return result
    End Function

    Public Shared Function UpsEvidence(snapshot As UpsSnapshot, limits As EquipmentAlarmLimits) As EquipmentAlarmEvidence
        Dim s = If(snapshot, New UpsSnapshot())
        Dim result As New EquipmentAlarmEvidence With {
            .Quality = UpsQualityText.Describe(s.Quality), .Complete = s.IsGood AndAlso s.AlarmDetailsComplete, .Policy = limits.Clone()}
        result.Conditions("communication") = State(If(s.IsGood, AlarmSeverity.Normal, AlarmSeverity.Alarm), UpsQualityText.Describe(s.Quality), True)
        Dim load = If(s.IsGood, s.PhaseMax("OutputLoad"), Nothing)
        result.Conditions("load") = State(AlarmEvaluator.LoadSeverity(load, limits), "Load percent: " & Number(load, 2), load.HasValue)
        Dim temperature = If(s.IsGood, s.Value("BatteryTemperature"), Nothing)
        Dim heat = If(Not temperature.HasValue, AlarmSeverity.Normal,
            If(temperature >= limits.TemperatureShedC, AlarmSeverity.ShedLoad,
            If(temperature >= limits.TemperatureAlarmC, AlarmSeverity.Alarm,
            If(temperature >= limits.TemperaturePreAlarmC, AlarmSeverity.PreAlarm, AlarmSeverity.Normal))))
        result.Conditions("temperature") = State(heat, "Battery temperature C: " & Number(temperature), temperature.HasValue)
        For index = 0 To 2
            Dim voltage = If(s.IsGood AndAlso s.OutputVoltageReference = "L-N", s.Phase("OutputVoltage", index), Nothing)
            AddVoltageConditions(result, index, voltage, limits)
        Next
        Dim source = If(s.IsGood, s.Value("OutputSource"), Nothing)
        For Each entry In New (String, Integer)() {("on-battery", 5), ("on-bypass", 4), ("output-off", 2)}
            result.Conditions(entry.Item1) = New RecordedAlarmState With {
                .Active = If(source.HasValue, CType(source = entry.Item2, Boolean?), Nothing),
                .Severity = If(entry.Item2 = 4, "PreAlarm", "Alarm"), .Detail = "Output source: " & Number(source)}
        Next
        Dim battery = If(s.IsGood, s.Value("BatteryStatus"), Nothing)
        result.Conditions("battery-status") = New RecordedAlarmState With {
            .Active = If(battery.HasValue, CType(battery >= 3, Boolean?), Nothing), .Severity = "Alarm", .Detail = s.BatteryStatusText}
        For Each item In UpsAlarmEvaluator.Evaluate(s, limits).Where(Function(alarm) alarm.CardAlarm IsNot Nothing)
            Dim card = item.CardAlarm
            Dim key = "card:" & card.Source & ":" & card.DescriptionOid
            If result.Conditions.ContainsKey(key) Then key &= ":" & card.Id
            result.Conditions(key) = New RecordedAlarmState With {
                .Active = If(s.IsGood AndAlso Not card.EvidenceConflict, card.Active, Nothing),
                .Acknowledged = card.Acknowledged, .Severity = item.Severity.ToString(), .Detail = card.Name,
                .Name = card.Name, .Category = CardCategory(card.CardType, card.Name)}
        Next
        result.Conditions("card-evidence") = New RecordedAlarmState With {
            .Active = Not result.Complete,
            .Severity = "PreAlarm", .Detail = s.AlarmDetailsStatus}
        result.Conditions("power-consistency") = New RecordedAlarmState With {
            .Active = If(s.IsGood, CType(s.PowerConsistencyIssue.Length > 0, Boolean?), Nothing),
            .Severity = "PreAlarm", .Detail = s.PowerConsistencyIssue}
        result.Conditions("voltage-reference") = New RecordedAlarmState With {
            .Active = If(s.IsGood, CType(s.OutputVoltageReference <> "L-N", Boolean?), Nothing),
            .Severity = "PreAlarm", .Detail = s.OutputVoltageReference}
        Dim count = If(s.IsGood, s.Value("AlarmsPresent"), Nothing)
        result.Conditions("reported-alarms") = New RecordedAlarmState With {
            .Active = If(count.HasValue, CType(count > 0, Boolean?), Nothing),
            .Severity = "Alarm", .Detail = "Reported alarm count: " & Number(count)}
        SetLabels(result)
        Return result
    End Function

    Private Shared Sub AddVoltageConditions(evidence As EquipmentAlarmEvidence, phase As Integer, voltage As Double?, limits As EquipmentAlarmLimits,
                                            Optional evaluable As Boolean = True)
        For Each direction As String In {"under", "over"}
            ' A de-energised or partly energised source has no meaningful voltage limit: it is not an under/over-voltage.
            evidence.Conditions(direction & "-voltage:L" & (phase + 1)) = New RecordedAlarmState With {
                .Active = If(Not evaluable, CType(False, Boolean?), If(voltage.HasValue, CType(If(direction = "under", voltage.GetValueOrDefault() < limits.UnderVoltageV,
                    voltage.GetValueOrDefault() > limits.OverVoltageV), Boolean?), Nothing)),
                .Severity = "Alarm", .Detail = "L-N voltage V: " & Number(voltage)}
        Next
    End Sub

    Private Shared Function CardCategory(cardType As String, name As String) As String
        If cardType.Equals("Fault", StringComparison.OrdinalIgnoreCase) OrElse
            name.Contains("failure", StringComparison.OrdinalIgnoreCase) OrElse
            name.Contains("failed", StringComparison.OrdinalIgnoreCase) OrElse
            name.Contains("fault", StringComparison.OrdinalIgnoreCase) Then Return "Fault"
        If {"Warning", "Alarm"}.Contains(cardType, StringComparer.OrdinalIgnoreCase) Then Return "Alarm"
        Return "Unclassified"
    End Function

    Public Shared Sub SetLabels(evidence As EquipmentAlarmEvidence)
        If evidence Is Nothing OrElse evidence.Conditions Is Nothing Then Throw New IO.InvalidDataException("Recorded alarm evidence is missing.")
        For Each entry In evidence.Conditions
            Dim state = entry.Value
            If state Is Nothing OrElse state.Name Is Nothing OrElse state.Category Is Nothing OrElse state.Detail Is Nothing OrElse
                state.Severity Is Nothing Then Throw New IO.InvalidDataException("Invalid recorded alarm condition: " & entry.Key)
            If state.Name.Length = 0 Then state.Name = ConditionName(entry.Key, state.Detail)
            If state.Category.Length = 0 Then state.Category = If(entry.Key.StartsWith("card:", StringComparison.Ordinal),
                CardCategory("", state.Detail), If({"communication", "transformer-off", "ecg-off", "output-off"}.Contains(entry.Key), "Fault", "Alarm"))
        Next
    End Sub

    Public Shared Function ConditionName(key As String, detail As String) As String
        If key.StartsWith("card:", StringComparison.Ordinal) Then Return If(detail.Length > 0, detail, key)
        If key.StartsWith("under-voltage:", StringComparison.Ordinal) Then Return "Under voltage " & key.Split(":"c)(1)
        If key.StartsWith("over-voltage:", StringComparison.Ordinal) Then Return "Over voltage " & key.Split(":"c)(1)
        If key.StartsWith("voltage:", StringComparison.Ordinal) Then Return "Voltage outside limits " & key.Split(":"c)(1) & " (legacy)"
        Select Case key
            Case "communication" : Return "Communication failure"
            Case "transformer-off" : Return "Transformer off"
            Case "ecg-off" : Return "ECG off"
            Case "ecg-back" : Return "ECG back"
            Case "load" : Return "High load / overload"
            Case "temperature" : Return "High temperature"
            Case "on-battery" : Return "UPS on battery"
            Case "on-bypass" : Return "UPS on bypass"
            Case "output-off" : Return "UPS output off"
            Case "battery-status" : Return "Battery low / depleted"
            Case "card-evidence" : Return "Card alarm details unavailable"
            Case "power-consistency" : Return "Power measurement inconsistency"
            Case "voltage-reference" : Return "Voltage reference unverified"
            Case "reported-alarms" : Return "UPS reports alarms (details unavailable)"
            Case Else : Return key
        End Select
    End Function

    Private Shared Function Number(value As Double?, Optional decimals As Integer = 1) As String
        Return If(value.HasValue, Math.Round(value.Value, decimals, MidpointRounding.AwayFromZero).ToString(If(decimals = 2, "0.##", "0.#"), CultureInfo.InvariantCulture), "unknown")
    End Function

    Public Sub Observe(equipmentType As String, equipmentId As String, acquiredUtc As DateTime?, evidence As EquipmentAlarmEvidence)
        Dim equipment = equipmentType & ":" & equipmentId
        SyncLock _gate
            Dim previous As SavedEquipmentAlarmState = Nothing
            If Not _states.TryGetValue(equipment, previous) Then
                Dim json = _outbox.ReadAlarmState(equipment)
                If json IsNot Nothing Then previous = JsonSerializer.Deserialize(Of SavedEquipmentAlarmState)(json)
            End If
            If previous IsNot Nothing Then SetLabels(previous.Evidence)
            Dim now = _utcNow()
            If now.Kind <> DateTimeKind.Utc Then Throw New IO.InvalidDataException("Alarm recording clock must be UTC.")
            SetLabels(evidence)
            ' A single failed poll is not a communication failure: ignore it until it lasts CommunicationDebounce.
            Dim commNow As RecordedAlarmState = Nothing
            If evidence.Conditions.TryGetValue("communication", commNow) AndAlso commNow.Active = True AndAlso previous IsNot Nothing Then
                Dim commBefore As RecordedAlarmState = Nothing
                previous.Evidence.Conditions.TryGetValue("communication", commBefore)
                If commBefore IsNot Nothing AndAlso commBefore.Active = False Then
                    Dim since As DateTime
                    If Not _badSince.TryGetValue(equipment, since) Then
                        since = now
                        _badSince(equipment) = since
                    End If
                    If now - since < CommunicationDebounce Then Return
                End If
            Else
                _badSince.Remove(equipment)
            End If
            Dim recorded =  If(previous Is Nothing, New Dictionary(Of String, DateTime)(StringComparer.Ordinal),
                New Dictionary(Of String, DateTime)(previous.LastRecordedUtc, StringComparer.Ordinal))
            Dim resumed = previous IsNot Nothing AndAlso previous.SessionId <> _session
            Dim commands As New List(Of RecordedCommand)()
            If previous Is Nothing Then
                commands.Add(EventCommand(equipmentType, equipmentId, acquiredUtc, now, "__observation",
                    If(previous Is Nothing, "MonitoringStarted", "MonitoringResumed"),
                    Nothing, Nothing, evidence))
            End If
            If previous IsNot Nothing AndAlso (JsonSerializer.Serialize(previous.Evidence.Policy) <> JsonSerializer.Serialize(evidence.Policy) OrElse
                                               Not Nullable.Equals(previous.Evidence.RatedLoadKw, evidence.RatedLoadKw)) Then
                commands.Add(EventCommand(equipmentType, equipmentId, acquiredUtc, now, "__policy", "PolicyChanged", Nothing, Nothing, evidence))
            End If
            Dim conditions = evidence.Conditions
            If previous IsNot Nothing Then
                For Each entry In previous.Evidence.Conditions
                    If conditions.ContainsKey(entry.Key) OrElse HeldKeys.Contains(entry.Key) OrElse entry.Key.StartsWith("voltage:", StringComparison.Ordinal) Then Continue For
                    Dim knownAbsent = evidence.Complete AndAlso entry.Key.StartsWith("card:", StringComparison.Ordinal)
                    conditions(entry.Key) = New RecordedAlarmState With {
                        .Active = If(knownAbsent, CType(False, Boolean?), Nothing),
                        .Severity = entry.Value.Severity, .Name = entry.Value.Name, .Category = entry.Value.Category,
                        .Detail = If(knownAbsent, "Not present in complete card table.", "Current evidence incomplete; state unknown.")}
                Next
            End If
            For Each entry In conditions
                If equipmentType = "UPS" Then
                    Select Case entry.Key
                        Case "card-evidence", "voltage-reference", "power-consistency"
                            Continue For
                    End Select
                End If
                Dim before As RecordedAlarmState = Nothing
                If previous IsNot Nothing Then previous.Evidence.Conditions.TryGetValue(entry.Key, before)
                Dim raisedOnly = RaisedOnlyKeys.Contains(entry.Key)
                ' An intermediate or unreadable voltage must not flip these states to Unknown and back; keep the last known state.
                If HeldKeys.Contains(entry.Key) AndAlso Not entry.Value.Active.HasValue Then entry.Value.Active = If(before Is Nothing, False, before.Active.GetValueOrDefault())
                Dim onceOnly = IsOnceKey(entry.Key)
                If Not entry.Value.Active.HasValue Then
                    ' An unreadable/incomplete poll stays "unknown" in the live state only; it never becomes a history row.
                    If before IsNot Nothing Then entry.Value.LastKnownActive = If(before.Active, before.LastKnownActive)
                    Continue For
                End If
                Dim beforeActive = before IsNot Nothing AndAlso If(before.Active, before.LastKnownActive).GetValueOrDefault()
                If onceOnly AndAlso entry.Key <> "communication" Then
                    ' A change of state must persist for ValueDebounce before it is recorded; momentary spikes and startup ramps are ignored.
                    Dim baseline = beforeActive
                    Dim pendingKey = equipment & "|" & entry.Key
                    If entry.Value.Active.GetValueOrDefault() <> baseline Then
                        Dim since As DateTime
                        If Not _pendingSince.TryGetValue(pendingKey, since) Then
                            since = now
                            _pendingSince(pendingKey) = since
                        End If
                        ' Card tables are intermittently incomplete, so a card alarm must stay absent much longer to count as cleared.
                        Dim hold = If(baseline AndAlso entry.Key.StartsWith("card:", StringComparison.Ordinal), CardClearDebounce, ValueDebounce)
                        If now - since < hold Then
                            If before IsNot Nothing Then
                                entry.Value.Active = before.Active
                                entry.Value.Acknowledged = before.Acknowledged
                                entry.Value.Severity = before.Severity
                                entry.Value.Detail = before.Detail
                            Else
                                entry.Value.Active = False
                                entry.Value.Severity = "Normal"
                            End If
                            Continue For
                        Else
                            _pendingSince.Remove(pendingKey)
                        End If
                    Else
                        _pendingSince.Remove(pendingKey)
                    End If
                End If
                ' Never write "Normal" rows: only a raise, and the clear of a raise that was recorded.
                If Not entry.Value.Active.GetValueOrDefault() AndAlso Not beforeActive Then Continue For
                ' Acknowledge or severity changes of an alarm that is still active are not new events.
                If onceOnly AndAlso before IsNot Nothing AndAlso before.Active.HasValue AndAlso before.Active = entry.Value.Active Then Continue For
                ' Returning to the state that was last recorded before an unknown gap is not a new event.
                If before IsNot Nothing AndAlso Not before.Active.HasValue AndAlso before.LastKnownActive.HasValue AndAlso
                    before.LastKnownActive = entry.Value.Active Then Continue For
                entry.Value.LastKnownActive = If(entry.Value.Active.HasValue, Nothing, If(before Is Nothing, Nothing, If(before.Active, before.LastKnownActive)))
                Dim replaced = entry.Key.StartsWith("voltage:", StringComparison.Ordinal) OrElse
                    (entry.Key = "reported-alarms" AndAlso evidence.Complete AndAlso conditions.Keys.Any(
                        Function(key) key.StartsWith("card:", StringComparison.Ordinal)))
                If replaced Then
                    entry.Value.Active = Nothing
                    entry.Value.Severity = "Unknown"
                    entry.Value.Detail = "Replaced by named under/over-voltage or card conditions; this is not a confirmed clear."
                    Continue For
                End If
                Dim unchanged = before IsNot Nothing AndAlso SameState(before, entry.Value)
                Dim last As DateTime
                Dim hasTimer = recorded.TryGetValue(entry.Key, last)
                ' Value alarms (temperature, voltage, load) are logged once when raised and once when cleared: no reminders.
                Dim due = False
                Dim needsTimer = False
                If unchanged AndAlso Not due AndAlso Not needsTimer Then Continue For
                Dim kind = If(unchanged, If(needsTimer, "StateAtResume", "ActiveReminder"), Transition(before, entry.Value))
                ' ECG / transformer-off are logged once when they start only: no clears, reminders or resume rows.
                If raisedOnly AndAlso (unchanged OrElse Not entry.Value.Active.GetValueOrDefault() OrElse
                                       (before IsNot Nothing AndAlso before.Active.GetValueOrDefault())) Then Continue For
                commands.Add(EventCommand(equipmentType, equipmentId, acquiredUtc, now, entry.Key, kind, before, entry.Value, evidence))
                recorded(entry.Key) = now
            Next
            Dim saved As New SavedEquipmentAlarmState With {.SessionId = _session, .ObservedUtc = now, .AcquiredUtc = acquiredUtc,
                .Evidence = evidence, .LastRecordedUtc = recorded}
            If commands.Count > 0 Then _outbox.Enqueue(commands, equipment, JsonSerializer.Serialize(saved))
            _states(equipment) = saved
        End SyncLock
    End Sub

    Private ReadOnly _badSince As New Dictionary(Of String, DateTime)(StringComparer.Ordinal)

    Private ReadOnly _pendingSince As New Dictionary(Of String, DateTime)(StringComparer.Ordinal)

    Public Property ValueDebounce As TimeSpan = TimeSpan.FromSeconds(10)

    Public Property CardClearDebounce As TimeSpan = TimeSpan.FromMinutes(2)

    Public Property CommunicationDebounce As TimeSpan = TimeSpan.FromSeconds(15)

    Private Shared ReadOnly RaisedOnlyKeys As String() = {"ecg-off", "ecg-back"}
    Private Shared ReadOnly HeldKeys As String() = {"ecg-off", "ecg-back", "transformer-off"}

    Private Shared Function IsOnceKey(key As String) As Boolean
        Return Not key.StartsWith("__", StringComparison.Ordinal) AndAlso Not HeldKeys.Contains(key)
    End Function

    Private Shared Function SameState(before As RecordedAlarmState, after As RecordedAlarmState) As Boolean
        Return Nullable.Equals(before.Active, after.Active) AndAlso Nullable.Equals(before.Acknowledged, after.Acknowledged) AndAlso
            before.Severity = after.Severity AndAlso before.Category = after.Category AndAlso before.Name = after.Name
    End Function

    Private Shared Function Transition(before As RecordedAlarmState, after As RecordedAlarmState) As String
        If Not after.Active.HasValue Then Return "StateUnknown"
        If before Is Nothing OrElse Not before.Active.HasValue Then Return If(after.Active.Value, "ObservedActive", "ObservedInactive")
        If before.Active <> after.Active Then Return If(after.Active.Value, "ObservedRaised", "ObservedCleared")
        If Not Nullable.Equals(before.Acknowledged, after.Acknowledged) Then
            Return If(Not after.Acknowledged.HasValue, "AcknowledgementUnknown",
                      If(after.Acknowledged.Value, "ObservedAcknowledged", "ObservedUnacknowledged"))
        End If
        Return "SeverityChanged"
    End Function

    Private Function EventCommand(equipmentType As String, equipmentId As String, acquired As DateTime?, observed As DateTime,
                                  key As String, kind As String, before As RecordedAlarmState, after As RecordedAlarmState,
                                  evidence As EquipmentAlarmEvidence) As RecordedCommand
        Using command As New NpgsqlCommand(
            "INSERT INTO equipment_alarm_history(event_id,equipment_type,equipment_id,observed_utc,acquired_utc,session_id," &
            "event_kind,alarm_key,previous_state,current_state,evidence) VALUES(@event,@type,@id,@observed,@acquired,@session,@kind,@key,@before,@after,@evidence) " &
            "ON CONFLICT(event_id) DO NOTHING")
            command.Parameters.AddWithValue("event", Guid.NewGuid())
            command.Parameters.AddWithValue("type", equipmentType)
            command.Parameters.AddWithValue("id", equipmentId)
            command.Parameters.AddWithValue("observed", observed)
            command.Parameters.AddWithValue("acquired", NpgsqlDbType.TimestampTz, If(acquired.HasValue, CObj(acquired.Value), DBNull.Value))
            command.Parameters.AddWithValue("session", _session)
            command.Parameters.AddWithValue("kind", kind)
            command.Parameters.AddWithValue("key", key)
            command.Parameters.AddWithValue("before", NpgsqlDbType.Jsonb, If(before Is Nothing, CObj(DBNull.Value), JsonSerializer.Serialize(before)))
            command.Parameters.AddWithValue("after", NpgsqlDbType.Jsonb, If(after Is Nothing, CObj(DBNull.Value), JsonSerializer.Serialize(after)))
            Dim savedEvidence = evidence
            If Not key.StartsWith("__", StringComparison.Ordinal) Then
                ' Limits are not repeated in every alarm row; only PolicyChanged rows keep them.
                savedEvidence = New EquipmentAlarmEvidence With {.Quality = evidence.Quality, .Complete = evidence.Complete,
                    .Policy = Nothing, .RatedLoadKw = evidence.RatedLoadKw}
                savedEvidence.Conditions(key) = after
            End If
            command.Parameters.AddWithValue("evidence", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(savedEvidence))
            Return RecordedCommand.Capture(command)
        End Using
    End Function
End Class
