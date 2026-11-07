Imports System.Globalization
Imports System.IO
Imports System.Linq

Public NotInheritable Class EquipmentHistoryTableRow
    Public Property Cells As String()
    Public Property Sources As IReadOnlyList(Of EquipmentHistoryRow)
End Class

Public NotInheritable Class EquipmentHistoryTable
    Public Property Headers As String()
    Public Property Rows As IReadOnlyList(Of EquipmentHistoryTableRow)
    ' Acquisition cycles where the equipment answered nothing valid (offline / timeout). They hold no measurement, so they are
    ' not listed as table rows; the count is kept so the view can state it instead of hiding it silently.
    Public Property CyclesWithoutReading As Integer

    Public Shared Function FormatMeasurement(value As Double?, Optional isState As Boolean = False) As String
        If Not value.HasValue Then Return ""
        Dim rounded = Math.Round(value.Value, 1, MidpointRounding.AwayFromZero)
        If rounded = 0 Then rounded = 0
        Return rounded.ToString(If(isState, "0", "0.0"), CultureInfo.InvariantCulture)
    End Function

    Public Shared Function BucketStart(utc As DateTime, intervalMilliseconds As Integer) As DateTime
        If utc.Kind <> DateTimeKind.Utc OrElse intervalMilliseconds <= 0 Then Throw New ArgumentException("UTC time and a positive interval are required.")
        Dim ticks = CLng(intervalMilliseconds) * TimeSpan.TicksPerMillisecond
        Return New DateTime(utc.Ticks - utc.Ticks Mod ticks, DateTimeKind.Utc)
    End Function

    Private Shared Function EventLabel(kind As String) As String
        Select Case kind
            Case "ObservedRaised" : Return "Raised"
            Case "ObservedCleared" : Return "Cleared"
            Case "ObservedActive" : Return "Observed active"
            Case "ObservedInactive" : Return "Observed inactive"
            Case "ActiveReminder" : Return "Still active (5 min)"
            Case "StateAtResume" : Return "Observed at resume"
            Case "StateUnknown" : Return "State unknown"
            Case "ObservedAcknowledged" : Return "Acknowledged"
            Case "ObservedUnacknowledged" : Return "Unacknowledged"
            Case "AcknowledgementUnknown" : Return "Acknowledgement unknown"
            Case "ConditionReplaced" : Return "Tracking replaced"
            Case "SeverityChanged" : Return "Severity / classification changed"
            Case Else : Return kind
        End Select
    End Function

    Public Shared Function Create(result As EquipmentHistoryResult) As EquipmentHistoryTable
        If result.Request.IsCombined Then Return CreateCombined(result)
        Dim metrics = result.Request.Metrics.Where(Function(metric) metric.Id <> "alarm:events").ToArray()
        Dim includeEvents = result.Request.RawOnly AndAlso result.Request.Metrics.Any(Function(metric) metric.Id = "alarm:events")
        Dim table As New EquipmentHistoryTable With {.Headers =
            {"UTC", "Row", "Interval ms"}.Concat(metrics.Select(Function(metric) metric.Name & If(metric.Unit.Length = 0, "", " [" & metric.Unit & "]"))).
                Concat(If(result.Request.RawOnly, {"Quality", "Acquisitions"},
                    {"Quality / coverage", "Alarms / faults observed", "Acquisitions"})).ToArray()}
        If includeEvents Then table.Headers = table.Headers.Concat({"Condition", "State", "Severity", "Event", "Acknowledgement", "Details"}).ToArray()
        Dim rows As New List(Of EquipmentHistoryTableRow)()
        Dim groups = result.Rows.GroupBy(Function(row) New With {
            Key .Utc = If(row.IsFullAcquisition AndAlso Not result.Request.IndividualAcquisitions, BucketStart(row.TimestampUtc, CInt(row.RecordedIntervalSeconds.Value * 1000)), row.TimestampUtc),
            Key .Milliseconds = If(row.IsFullAcquisition AndAlso Not result.Request.IndividualAcquisitions, CInt(row.RecordedIntervalSeconds.Value * 1000), 0),
            Key .Observation = If(result.Request.IndividualAcquisitions, row.ObservationId, String.Empty)}).
            OrderBy(Function(group) group.Key.Utc).ThenBy(Function(group) group.Key.Milliseconds)
        For Each group In groups
            Dim sources = group.OrderBy(Function(row) row.TimestampUtc).ToArray()
            Dim available = metrics.Select(Function(metric) sources.Where(Function(row) row.MetricId = metric.Id AndAlso
                row.Quality = "Good" AndAlso row.Value.HasValue AndAlso Double.IsFinite(row.Value.Value)).ToArray()).ToArray()
            If metrics.Length > 0 AndAlso available.All(Function(items) items.Length = 0) Then
                table.CyclesWithoutReading += 1
                Continue For
            End If
            Dim quality = String.Join("; ", sources.Select(Function(row) row.Quality).Distinct())
            If result.Request.RawOnly Then
                Dim sourceQuality = sources.Select(Function(row) row.AcquisitionQuality).Where(
                    Function(value) value.Length > 0 AndAlso Not value.Equals("Good", StringComparison.OrdinalIgnoreCase)).Distinct().ToArray()
                If sourceQuality.Length > 0 Then quality &= "; source " & String.Join(", ", sourceQuality)
            End If
            If group.Key.Milliseconds > 0 AndAlso (group.Key.Utc < result.Request.FromUtc OrElse
                group.Key.Utc.AddMilliseconds(group.Key.Milliseconds) > result.Request.ToUtc OrElse
                group.Key.Utc.AddMilliseconds(group.Key.Milliseconds) > result.GeneratedUtc) Then quality &= "; partial interval"
            Dim alarms = String.Join(" | ", sources.Select(Function(row) row.AlarmSummary).Distinct(StringComparer.Ordinal))
            Dim acquisitions = sources.Where(Function(row) row.MetricId = metrics(0).Id).Count()
            ' Saved readings carry no within-interval Min/Max, so a Min/Max row would only ever be blank.
            For Each kind In If(result.Request.IndividualAcquisitions OrElse group.Key.Milliseconds = 0 OrElse metrics.All(Function(metric) metric.IsIntervalPeak),
                {"Value"}, {"Value", "Min", "Max"})
                Dim cells As New List(Of String) From {group.Key.Utc.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
                    kind, If(group.Key.Milliseconds = 0, "", group.Key.Milliseconds.ToString(CultureInfo.InvariantCulture))}
                For index = 0 To metrics.Count - 1
                    Dim value As Double? = Nothing
                    Dim observations = available(index)
                    If observations.Length > 0 AndAlso (kind = "Value" OrElse group.Key.Milliseconds > 0) AndAlso
                        (Not metrics(index).IsIntervalPeak OrElse kind = "Value") Then
                        Select Case If(metrics(index).IsIntervalPeak, "Max", kind)
                            Case "Value" : value = observations.Last().Value
                            Case "Min" : value = observations.Min(Function(row) row.Value.Value)
                            Case "Max" : value = observations.Max(Function(row) row.Value.Value)
                        End Select
                    End If
                    cells.Add(FormatMeasurement(value, metrics(index).IsState))
                Next
                cells.Add(quality)
                If Not result.Request.RawOnly Then cells.Add(alarms)
                cells.Add(acquisitions.ToString(CultureInfo.InvariantCulture))
                If includeEvents Then cells.AddRange(Enumerable.Repeat("", 6))
                rows.Add(New EquipmentHistoryTableRow With {.Cells = cells.ToArray(), .Sources = sources})
            Next
        Next
        If includeEvents Then
            For Each item In result.AlarmEvents
                Dim cells As New List(Of String) From {item.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
                    If(item.EventKind = "ConditionReplaced", "Record note", item.EventCategory), ""}
                cells.AddRange(Enumerable.Repeat("", metrics.Length))
                cells.AddRange({item.Quality, "", item.ConditionName, item.EventState, item.EventSeverity,
                    EventLabel(item.EventKind), item.EventAcknowledged, item.EventDetails})
                rows.Add(New EquipmentHistoryTableRow With {.Cells = cells.ToArray(), .Sources = {item}})
            Next
            Dim eventStart = metrics.Length + 5
            Dim order = {0, 1}.Concat(Enumerable.Range(3, metrics.Length)).
                Concat({eventStart, eventStart + 1, eventStart + 3, eventStart + 2, 2, metrics.Length + 3,
                    metrics.Length + 4, eventStart + 4, eventStart + 5}).ToArray()
            table.Headers = order.Select(Function(index) table.Headers(index)).ToArray()
            For Each row In rows
                row.Cells = order.Select(Function(index) row.Cells(index)).ToArray()
            Next
        End If
        If includeEvents Then
            Dim keep = Enumerable.Range(0, table.Headers.Length).Where(
                Function(index) Not (metrics.Length = 0 AndAlso {"Interval ms", "Acquisitions"}.Contains(table.Headers(index))) AndAlso
                    Not (table.Headers(index) = "Acknowledgement" AndAlso rows.All(Function(row) row.Cells(index).Length = 0))).ToArray()
            table.Headers = keep.Select(Function(index) table.Headers(index)).ToArray()
            For Each row In rows
                row.Cells = keep.Select(Function(index) row.Cells(index)).ToArray()
            Next
        End If
        table.Rows = rows.OrderBy(Function(row) row.Cells(0), StringComparer.Ordinal).ToArray()
        ' Split "yyyy-MM-dd HH:mm:ss.fffffff" into a Date column and a Time column truncated to one decimal.
        table.Headers = {"Date (UTC)", "Time (UTC)"}.Concat(table.Headers.Skip(1)).ToArray()
        For Each row In table.Rows
            Dim stamp = row.Cells(0)
            row.Cells = {stamp.Substring(0, 10), stamp.Substring(11, 10)}.Concat(row.Cells.Skip(1)).ToArray()
        Next
        Return table
    End Function

    Private Shared Function CreateCombined(result As EquipmentHistoryResult) As EquipmentHistoryTable
        Dim table As New EquipmentHistoryTable With {
            .Headers = {"Date (UTC)", "Time (UTC)", "Equipment", "Measurement", "Unit", "Value", "Quality", "Acquisition UTC", "Details"}}
        Dim equipmentNames = result.Request.IncludedEquipment.ToDictionary(Function(asset) asset.Id, Function(asset) asset.Name, StringComparer.Ordinal)
        Dim metrics = result.Request.Metrics.ToDictionary(Function(metric) metric.Id, StringComparer.Ordinal)
        Dim rows As New List(Of EquipmentHistoryTableRow)()
        For Each observation In result.Rows
            Dim metric As HistoryMetric = Nothing
            If Not metrics.TryGetValue(observation.MetricId, metric) Then
                Throw New InvalidDataException("A combined history observation references an unknown measurement.")
            End If
            Dim equipmentName As String = Nothing
            If Not equipmentNames.TryGetValue(observation.EquipmentId, equipmentName) Then
                Throw New InvalidDataException("A combined history observation references unknown equipment.")
            End If
            rows.Add(New EquipmentHistoryTableRow With {
                .Cells = {observation.TimestampUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    observation.TimestampUtc.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture), equipmentName,
                    If(metric.SourceMetricName.Length > 0, metric.SourceMetricName, metric.Name), metric.Unit,
                    FormatMeasurement(observation.Value, metric.IsState), observation.Quality,
                    If(observation.AcquiredUtc.HasValue, observation.AcquiredUtc.Value.ToString("o", CultureInfo.InvariantCulture), ""),
                    observation.Evidence},
                .Sources = {observation}})
        Next
        For Each eventRow In result.AlarmEvents
            Dim equipmentName As String = Nothing
            If Not equipmentNames.TryGetValue(eventRow.EquipmentId, equipmentName) Then
                Throw New InvalidDataException("A combined alarm event references unknown equipment.")
            End If
            rows.Add(New EquipmentHistoryTableRow With {
                .Cells = {eventRow.TimestampUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    eventRow.TimestampUtc.ToString("HH:mm:ss.fffffff", CultureInfo.InvariantCulture), equipmentName,
                    eventRow.EventCategory & ": " & eventRow.ConditionName, "", eventRow.EventState, eventRow.Quality,
                    If(eventRow.AcquiredUtc.HasValue, eventRow.AcquiredUtc.Value.ToString("o", CultureInfo.InvariantCulture), ""),
                    EventLabel(eventRow.EventKind) & If(eventRow.EventSeverity.Length = 0, "", " | " & eventRow.EventSeverity) &
                    If(eventRow.EventAcknowledged.Length = 0, "", " | " & eventRow.EventAcknowledged) &
                    If(eventRow.EventDetails.Length = 0, "", " | " & eventRow.EventDetails)},
                .Sources = {eventRow}})
        Next
        table.Rows = rows.OrderBy(Function(row) row.Sources(0).TimestampUtc).
            ThenBy(Function(row) row.Cells(2), StringComparer.Ordinal).
            ThenBy(Function(row) row.Cells(3), StringComparer.Ordinal).ToArray()
        Return table
    End Function
End Class
