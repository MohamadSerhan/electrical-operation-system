Imports System.Globalization
Imports System.Linq
Imports System.Text.Json
Imports System.Threading
Imports Npgsql

Public NotInheritable Class EquipmentAlarmHistorySource
    Implements IEquipmentHistorySource, IRecordedEquipmentHistorySource

    Public ReadOnly Property Id As String Implements IEquipmentHistorySource.Id
        Get
            Return "equipment-alarms"
        End Get
    End Property

    Private Function Asset(equipmentType As String, equipmentId As String) As HistoryEquipment
        Return New HistoryEquipment With {
            .Id = equipmentType & ":" & equipmentId & ":ALARMS", .SourceKey = equipmentType & ":" & equipmentId,
            .Name = equipmentId & " / observed alarm history", .Category = "Alarm history", .SourceId = Id,
            .Description = "Observed per-condition state changes, card acknowledgements, policy changes and monitoring boundaries. " &
                "Not exact device event times; gaps and incomplete evidence never imply a clear. No pre-upgrade events are invented.",
            .Metrics = {New HistoryMetric With {.Id = "alarm_state", .Name = "Observed alarm state", .Unit = "severity code", .IsState = True,
                .Basis = "0 normal/inactive, 1 pre-alarm, 2 alarm, 3 shed load. Unknown and monitoring/policy events have no numeric value. " &
                    "Counts are saved observations, not incident or availability totals. Full event identity and evidence are included in each row."}}}
    End Function

    Public Shared Function LastEventUtc(sourceKey As String) As DateTime?
        Dim key = sourceKey.Split(":"c, 2)
        Using connection = PostgresDatabase.Open()
            Using command As New NpgsqlCommand("SELECT max(observed_utc) FROM equipment_alarm_history WHERE equipment_type=@type AND equipment_id=@id " &
                "AND left(alarm_key,2)<>'__'", connection)
                command.Parameters.AddWithValue("type", key(0))
                command.Parameters.AddWithValue("id", key(1))
                Dim value = command.ExecuteScalar()
                Return If(value Is Nothing OrElse value Is DBNull.Value, CType(Nothing, DateTime?), CType(value, DateTime))
            End Using
        End Using
    End Function

    Public Function GetEquipment() As IReadOnlyList(Of HistoryEquipment) Implements IEquipmentHistorySource.GetEquipment
        Return {"T1", "T2", "T3", "T4", "TOTAL"}.Select(Function(key) Asset("Transformer", key)).
            Concat(UpsSettingsStore.Current.Devices.Select(Function(device) Asset("UPS", device.Id))).ToArray()
    End Function

    Public Function GetRecordedEquipment(token As CancellationToken) As IReadOnlyList(Of HistoryEquipment) Implements IRecordedEquipmentHistorySource.GetRecordedEquipment
        Dim result As New List(Of HistoryEquipment)()
        Using connection = PostgresDatabase.Open()
            EquipmentAlarmRecorder.EnsureSchema(connection)
            Using command As New NpgsqlCommand("SELECT DISTINCT equipment_type,equipment_id FROM equipment_alarm_history ORDER BY 1,2", connection)
                Using registration = token.Register(Sub() command.Cancel()), reader = command.ExecuteReader()
                    While reader.Read()
                        token.ThrowIfCancellationRequested()
                        result.Add(Asset(reader.GetString(0), reader.GetString(1)))
                    End While
                End Using
            End Using
        End Using
        Return result
    End Function

    Public Function Read(request As EquipmentHistoryRequest, token As CancellationToken) As IReadOnlyList(Of EquipmentHistoryRow) Implements IEquipmentHistorySource.Read
        Dim key = request.Equipment.SourceKey.Split(":"c, 2)
        Dim rows As New List(Of EquipmentHistoryRow)()
        Using connection = PostgresDatabase.Open()
            If request.RawOnly Then
                Using exists As New NpgsqlCommand("SELECT to_regclass('public.equipment_alarm_history') IS NOT NULL", connection)
                    If Not CBool(exists.ExecuteScalar()) Then Throw New IO.InvalidDataException("Alarm/fault event store is not initialized; alarm history is unavailable, not normal.")
                End Using
            Else
                EquipmentAlarmRecorder.EnsureSchema(connection)
            End If
            Using command As New NpgsqlCommand(
                "SELECT observed_utc,acquired_utc,event_kind,alarm_key,previous_state::text,current_state::text,evidence::text,event_id,session_id " &
                "FROM equipment_alarm_history WHERE equipment_type=@type AND equipment_id=@id AND observed_utc>=@from AND observed_utc<@to " &
                "AND (@clean=false OR (left(alarm_key,2)<>'__' AND NOT(event_kind='ObservedInactive' AND previous_state IS NULL))) " &
                "ORDER BY observed_utc,event_id LIMIT 100001", connection)
                command.Parameters.AddWithValue("type", key(0))
                command.Parameters.AddWithValue("id", key(1))
                command.Parameters.AddWithValue("from", request.FromUtc)
                command.Parameters.AddWithValue("to", request.ToUtc)
                command.Parameters.AddWithValue("clean", request.RawOnly)
                Using registration = token.Register(Sub() command.Cancel()), reader = command.ExecuteReader()
                    While reader.Read()
                        token.ThrowIfCancellationRequested()
                        EquipmentHistoryService.CheckLimit(rows.Count + 1)
                        Dim value As Double? = Nothing
                        Dim state = If(reader.IsDBNull(5), Nothing, JsonSerializer.Deserialize(Of RecordedAlarmState)(reader.GetString(5)))
                        Dim condition = reader.GetString(3)
                        Dim kind = reader.GetString(2)
                        If state IsNot Nothing Then
                            Dim evidence As New EquipmentAlarmEvidence()
                            evidence.Conditions(condition) = state
                            EquipmentAlarmRecorder.SetLabels(evidence)
                            ' A "Good" communication row means the link is back: show it as restored, not as a failure that is cleared.
                            If condition = "communication" AndAlso state.Active = False Then
                                state.Name = "Communication restored"
                                state.Detail = "Communication restored"
                            ElseIf condition = "transformer-off" AndAlso state.Active = False Then
                                state.Name = "Transformer on"
                                state.Detail = "Power restored"
                            End If
                        End If
                        If state IsNot Nothing AndAlso state.Active.HasValue Then
                            Dim severity As AlarmSeverity
                            If Not state.Active.Value Then
                                value = 0
                            ElseIf [Enum].TryParse(state.Severity, severity) Then
                                value = CInt(severity)
                            End If
                        End If
                        rows.Add(New EquipmentHistoryRow With {
                            .EquipmentId = request.Equipment.Id, .MetricId = "alarm_state", .TimestampUtc = reader.GetDateTime(0),
                            .AcquiredUtc = If(reader.IsDBNull(1), CType(Nothing, DateTime?), reader.GetDateTime(1)),
                            .Value = value, .Quality = If(request.RawOnly,
                                If(state Is Nothing OrElse Not state.Active.HasValue, "State unavailable", "Observed"),
                                If(value.HasValue, "Good", "Evidence only")), .EnergyEligible = False,
                            .ObservationId = reader.GetGuid(7).ToString("N"), .ConditionKey = condition,
                            .ConditionName = If(state Is Nothing, condition, state.Name),
                            .EventCategory = If(state Is Nothing, "Unclassified", state.Category),
                            .EventState = If(state Is Nothing OrElse Not state.Active.HasValue, "Unknown",
                                If(state.Active.Value, "Active", If(kind = "ObservedCleared", "Cleared", "Inactive"))),
                            .EventSeverity = If(state Is Nothing OrElse Not state.Active.HasValue, "Unknown",
                                If(Not state.Active.Value, "Normal", state.Severity)),
                            .EventAcknowledged = If(state Is Nothing OrElse Not state.Acknowledged.HasValue, "",
                                If(state.Acknowledged.Value, "Acknowledged", "Unacknowledged")),
                            .EventKind = kind,
                            .EventDetails = If(state Is Nothing, "", state.Detail),
                            .AlarmSummary = reader.GetString(2) & "; condition " & reader.GetString(3) &
                                "; current: " & If(reader.IsDBNull(5), "unknown", reader.GetString(5)),
                            .Evidence = reader.GetString(2) & "; condition " & reader.GetString(3) &
                                "; previous: " & If(reader.IsDBNull(4), "unknown", reader.GetString(4)) &
                                "; current: " & If(reader.IsDBNull(5), "unknown", reader.GetString(5)) &
                                "; evidence: " & reader.GetString(6) & "; event " & reader.GetGuid(7).ToString() &
                                "; session " & reader.GetGuid(8).ToString()})
                    End While
                End Using
            End Using
        End Using
        Return rows
    End Function
End Class
