Imports System.Globalization
Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Text.Json
Imports System.Threading
Imports Npgsql
Imports NpgsqlTypes

Public NotInheritable Class RawRecordingStorageEstimate
    Public ReadOnly Property RawTransformerBytesPerCycle As Double
    Public ReadOnly Property LegacyTransformerBytesPerCycle As Double
    Public ReadOnly Property UpsBytesPerSecond As Double
    Public ReadOnly Property EquipmentCount As Integer
    Public ReadOnly Property ObservedRawTransformerBytesPerSecond As Double?
    Public ReadOnly Property ObservedLegacyBytesPerSecond As Double?
    Public ReadOnly Property ObservedUpsBytesPerSecond As Double?
    Public ReadOnly Property ObservedReferenceIntervalMilliseconds As Integer
    Public ReadOnly Property UsesObservedRates As Boolean
    ' Measured mean spacing of recorded transformer rows when the PLC, not the recording interval, limits the row rate.
    Public ReadOnly Property ObservedCycleMilliseconds As Double
    ' One row per recording interval plus an allowance for acquisitions that raise the interval peak.
    Public Const PeakRowAllowancePerInterval As Double = 1.0R

    Public Sub New(rawTransformerBytes As Double, legacyTransformerBytes As Double, upsBytesPerSecond As Double,
                   equipmentCount As Integer,
                   Optional observedRawTransformerBytesPerSecond As Double? = Nothing,
                   Optional observedLegacyBytesPerSecond As Double? = Nothing,
                   Optional observedUpsBytesPerSecond As Double? = Nothing,
                   Optional observedReferenceIntervalMilliseconds As Integer = 1000,
                   Optional observedCycleMilliseconds As Double = 0.0R)
        If Not Double.IsFinite(rawTransformerBytes) OrElse rawTransformerBytes <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(rawTransformerBytes))
        If Not Double.IsFinite(legacyTransformerBytes) OrElse legacyTransformerBytes <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(legacyTransformerBytes))
        If Not Double.IsFinite(upsBytesPerSecond) OrElse upsBytesPerSecond < 0 Then Throw New ArgumentOutOfRangeException(NameOf(upsBytesPerSecond))
        If equipmentCount < RecordingSettings.RecordedAnalyzerCount Then Throw New ArgumentOutOfRangeException(NameOf(equipmentCount))
        Dim observedRates = {observedRawTransformerBytesPerSecond, observedLegacyBytesPerSecond, observedUpsBytesPerSecond}
        If observedRates.Any(Function(rate) rate.HasValue <> observedRates(0).HasValue) OrElse
           observedRates.Any(Function(rate) rate.HasValue AndAlso
               (Not Double.IsFinite(rate.Value) OrElse rate.Value < 0)) Then
            Throw New ArgumentOutOfRangeException(NameOf(observedRawTransformerBytesPerSecond))
        End If
        If observedRates(0).HasValue AndAlso
           (observedReferenceIntervalMilliseconds < RecordingSettings.MinimumRecordingIntervalMilliseconds OrElse
            observedReferenceIntervalMilliseconds > RecordingSettings.MaximumRecordingIntervalMilliseconds) Then
            Throw New ArgumentOutOfRangeException(NameOf(observedReferenceIntervalMilliseconds))
        End If
        Me.RawTransformerBytesPerCycle = rawTransformerBytes
        Me.LegacyTransformerBytesPerCycle = legacyTransformerBytes
        Me.UpsBytesPerSecond = upsBytesPerSecond
        Me.EquipmentCount = equipmentCount
        Me.ObservedRawTransformerBytesPerSecond = observedRawTransformerBytesPerSecond
        Me.ObservedLegacyBytesPerSecond = observedLegacyBytesPerSecond
        Me.ObservedUpsBytesPerSecond = observedUpsBytesPerSecond
        Me.ObservedReferenceIntervalMilliseconds = observedReferenceIntervalMilliseconds
        Me.UsesObservedRates = observedRates(0).HasValue
        Me.ObservedCycleMilliseconds = If(Double.IsFinite(observedCycleMilliseconds) AndAlso observedCycleMilliseconds > 0, observedCycleMilliseconds, 0.0R)
    End Sub

    ' Transformer rows are written once per recording interval and never faster than the PLC can complete a cycle,
    ' so rows per second = 1 / max(recording interval, acquisition cycle). UPS rows grow at their own fixed rate.
    Public Function EstimateBytes(intervalMilliseconds As Integer, days As Integer, plcIntervalMilliseconds As Integer) As Double
        If intervalMilliseconds < RecordingSettings.MinimumRecordingIntervalMilliseconds OrElse
           intervalMilliseconds > RecordingSettings.MaximumRecordingIntervalMilliseconds Then Throw New ArgumentOutOfRangeException(NameOf(intervalMilliseconds))
        If days < RecordingSettings.MinimumPlanningWindowDays OrElse days > RecordingSettings.MaximumRetentionDaysFor(intervalMilliseconds) Then
            Throw New ArgumentOutOfRangeException(NameOf(days))
        End If
        If plcIntervalMilliseconds < PlcSettings.MinimumAcquisitionIntervalMilliseconds OrElse
           plcIntervalMilliseconds > PlcSettings.MaximumAcquisitionIntervalMilliseconds Then Throw New ArgumentOutOfRangeException(NameOf(plcIntervalMilliseconds))
        Dim cycleMilliseconds = Math.Max(CDbl(plcIntervalMilliseconds), ObservedCycleMilliseconds)
        Dim rowsPerSecond = 1000.0R / Math.Max(CDbl(intervalMilliseconds), cycleMilliseconds)
        Dim bytesPerSecond As Double
        If UsesObservedRates Then
            Dim referenceRowsPerSecond = 1000.0R / Math.Max(CDbl(ObservedReferenceIntervalMilliseconds), cycleMilliseconds)
            Dim transformerBytesPerSecond = ObservedRawTransformerBytesPerSecond.Value + ObservedLegacyBytesPerSecond.Value
            bytesPerSecond = transformerBytesPerSecond * rowsPerSecond / referenceRowsPerSecond + ObservedUpsBytesPerSecond.Value
        Else
            bytesPerSecond = (RawTransformerBytesPerCycle + LegacyTransformerBytesPerCycle) * rowsPerSecond + UpsBytesPerSecond
        End If
        Return bytesPerSecond * 86400 * days * 2
    End Function
End Class

' Equipment history helpers; the readings themselves live in measurement_samples and ups_samples.
Public NotInheritable Class EquipmentRawHistory
    Public Shared Function ReadStorageEstimate(settings As UpsSettings) As RawRecordingStorageEstimate
        Return ReadStorageEstimate(settings, 1000)
    End Function

    Public Shared Function ReadStorageEstimate(settings As UpsSettings,
                                               recordingIntervalMilliseconds As Integer) As RawRecordingStorageEstimate
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        settings.Validate()
        If recordingIntervalMilliseconds < RecordingSettings.MinimumRecordingIntervalMilliseconds OrElse
           recordingIntervalMilliseconds > RecordingSettings.MaximumRecordingIntervalMilliseconds Then
            Throw New ArgumentOutOfRangeException(NameOf(recordingIntervalMilliseconds))
        End If
        Dim rawTransformerBytes = 1.0R
        Dim legacyTransformerBytes = 0.0R
        Dim upsBytesPerSecond = 0.0R
        Dim observedRawTransformerBytesPerSecond = 0.0R
        Dim observedLegacyBytesPerSecond = 0.0R
        Dim observedUpsBytesPerSecond = 0.0R
        Dim observedRatesAvailable = True
        Dim t1RowsPerSecond = 0.0R
        Dim equipmentCount = RecordingSettings.RecordedAnalyzerCount
        Using connection = PostgresDatabase.Open()
            For Each id In {"T1", "T2", "T3", "T4", "TOTAL"}
                ' Recent hour only, so the rate reflects the recording interval currently in effect.
                Dim legacy = ReadStorageSample(connection, "measurement_samples",
                    "analyzer_id=@id", "captured_at", id, windowMinutes:=60)
                legacyTransformerBytes += legacy.MaximumBytes
                observedLegacyBytesPerSecond += legacy.AverageBytes * legacy.RowsPerSecond
                If id = "T1" AndAlso legacy.RowsPerSecond > 0 Then t1RowsPerSecond = legacy.RowsPerSecond
                observedRatesAvailable = observedRatesAvailable AndAlso
                    legacy.RowsInWindow >= MinimumObservedRowsForEstimate
            Next
            If settings.HistorianEnabled AndAlso settings.AcquisitionEnabled Then
                For Each device In settings.Devices.Where(Function(item) item.Enabled)
                    Dim legacy = ReadStorageSample(connection, "ups_samples",
                        "device_id=@id", "sampled_utc", device.Id)
                    upsBytesPerSecond += legacy.MaximumBytes / settings.HistorianIntervalSeconds
                    observedUpsBytesPerSecond += legacy.AverageBytes * legacy.RowsPerSecond
                    observedRatesAvailable = observedRatesAvailable AndAlso
                        legacy.RowsInWindow >= MinimumObservedRowsForEstimate
                    equipmentCount += 1
                Next
            End If
        End Using
        If Not observedRatesAvailable Then
            Return New RawRecordingStorageEstimate(rawTransformerBytes, legacyTransformerBytes, upsBytesPerSecond, equipmentCount)
        End If
        Return New RawRecordingStorageEstimate(rawTransformerBytes, legacyTransformerBytes, upsBytesPerSecond, equipmentCount,
            observedRawTransformerBytesPerSecond, observedLegacyBytesPerSecond, observedUpsBytesPerSecond,
            recordingIntervalMilliseconds, ObservedCycle(t1RowsPerSecond, recordingIntervalMilliseconds))
    End Function

    ' Recorded rows spaced wider than the recording interval mean the PLC cycle is the limit; otherwise the cycle is not measurable here.
    Private Shared Function ObservedCycle(rowsPerSecond As Double, recordingIntervalMilliseconds As Integer) As Double
        If rowsPerSecond <= 0 Then Return 0.0R
        Dim spacing = 1000.0R / rowsPerSecond
        Return If(spacing > recordingIntervalMilliseconds * 1.1R, spacing, 0.0R)
    End Function

    Private Const MinimumObservedRowsForEstimate As Long = 64

    Private NotInheritable Class StorageSample
        Public Property AverageBytes As Double
        Public Property MaximumBytes As Double
        Public Property RowsInWindow As Long
        Public Property RowsPerSecond As Double
    End Class

    Private Shared Function ReadStorageSample(connection As NpgsqlConnection, tableName As String, whereSql As String,
                                              timestampColumn As String, id As String, Optional source As String = Nothing,
                                              Optional referenceIntervalMilliseconds As Integer? = Nothing,
                                              Optional windowMinutes As Integer = 1440) As StorageSample
        Dim rateFilter = whereSql & " AND t." & timestampColumn & " >= now() - interval '" & windowMinutes.ToString(CultureInfo.InvariantCulture) & " minutes'"
        If referenceIntervalMilliseconds.HasValue Then rateFilter &= " AND t.interval_ms=@rateInterval"
        Dim sql =
            "WITH sampled AS (SELECT pg_column_size(t) AS bytes, t." & timestampColumn & " AS stamp FROM " &
            tableName & " t WHERE " & whereSql & " ORDER BY t." & timestampColumn & " DESC LIMIT 64)," &
            "gaps AS (SELECT extract(epoch FROM stamp-lag(stamp) OVER (ORDER BY stamp)) AS seconds FROM sampled) " &
            "SELECT (SELECT avg(bytes) FROM sampled) AS average_bytes," &
            "(SELECT max(bytes) FROM sampled) AS maximum_bytes," &
            "(SELECT count(*) FROM " & tableName & " t WHERE " & rateFilter & ") AS rows_in_window," &
            "(SELECT array_agg(seconds::double precision) FROM gaps WHERE seconds>0) AS recent_gaps"
        Using command As New NpgsqlCommand(sql, connection)
            command.CommandTimeout = 10
            command.Parameters.AddWithValue("id", id)
            If source IsNot Nothing Then command.Parameters.AddWithValue("source", source)
            If referenceIntervalMilliseconds.HasValue Then
                command.Parameters.AddWithValue("rateInterval", referenceIntervalMilliseconds.Value)
            End If
            Using reader = command.ExecuteReader()
                If Not reader.Read() OrElse reader.IsDBNull(0) OrElse reader.IsDBNull(1) Then
                    Throw New InvalidDataException("No stored row-size sample for " & id & "; estimates require raw and legacy records for each enabled asset.")
                End If
                Dim result As New StorageSample With {
                    .AverageBytes = Convert.ToDouble(reader.GetValue(0), CultureInfo.InvariantCulture),
                    .MaximumBytes = Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture),
                    .RowsInWindow = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture)}
                If Not reader.IsDBNull(3) Then
                    result.RowsPerSecond = SustainedRowsPerSecond(reader.GetFieldValue(Of Double())(3))
                End If
                Return result
            End Using
        End Using
    End Function

    Private Shared Function SustainedRowsPerSecond(gaps As IEnumerable(Of Double)) As Double
        Dim ordered = gaps.Where(Function(seconds) Double.IsFinite(seconds) AndAlso seconds > 0).OrderBy(Function(seconds) seconds).ToArray()
        If ordered.Length = 0 Then Return 0
        Dim middle = ordered.Length \ 2
        Dim medianSeconds = If(ordered.Length Mod 2 = 0, (ordered(middle - 1) + ordered(middle)) / 2, ordered(middle))
        Return 1.0R / medianSeconds
    End Function

    Public Shared Function AlarmMetrics(source As String) As IReadOnlyList(Of HistoryMetric)
        Dim conditions As New List(Of (Key As String, Name As String)) From {
            ("any", "Any recorded alarm / fault"), ("communication", "Communication fault"),
            ("load", "Load alarm"), ("temperature", "Temperature alarm"),
            ("voltage:L1", "Voltage L1 alarm"), ("voltage:L2", "Voltage L2 alarm"), ("voltage:L3", "Voltage L3 alarm")}
        If source = "ups" Then conditions.AddRange({
            ("on-battery", "On battery"), ("on-bypass", "On bypass"), ("output-off", "Output off"),
            ("battery-status", "Battery fault"), ("reported-alarms", "Reported UPS alarm"),
            ("card-any", "Card alarm active"), ("card-evidence", "Alarm evidence incomplete"),
            ("power-consistency", "Power consistency fault"), ("voltage-reference", "Voltage reference fault")})
        Return conditions.Select(Function(item) New HistoryMetric With {
            .Id = "alarm:" & item.Key, .Name = "Alarm / " & item.Name, .Unit = "0/1", .IsState = True, .IsAlarm = True,
            .Basis = "Recorded raw alarm state: 0 = inactive, 1 = active, blank = unknown/not recorded. " &
                "Not recalculated from today's thresholds; observed state, not exact device transition time."}).ToArray()
    End Function

    Public Shared Function AlarmValue(evidence As EquipmentAlarmEvidence, metricId As String) As Double?
        If evidence Is Nothing Then Return Nothing
        If evidence.Conditions Is Nothing OrElse evidence.Conditions.Values.Any(Function(item) item Is Nothing) Then
            Throw New InvalidDataException("Recorded alarm conditions are invalid.")
        End If
        Dim key = metricId.Substring("alarm:".Length)
        Dim state As RecordedAlarmState = Nothing
        If key <> "any" AndAlso key <> "card-any" Then
            If Not evidence.Conditions.TryGetValue(key, state) OrElse Not state.Active.HasValue Then Return Nothing
            Return If(state.Active.Value, 1.0R, 0.0R)
        End If
        Dim states = If(key = "card-any",
            evidence.Conditions.Where(Function(item) item.Key.StartsWith("card:", StringComparison.Ordinal)).Select(Function(item) item.Value).ToArray(),
            evidence.Conditions.Values.ToArray())
        If states.Any(Function(item) item.Active.GetValueOrDefault()) Then Return 1
        If Not evidence.Complete OrElse states.Any(Function(item) Not item.Active.HasValue) OrElse
            (key = "any" AndAlso states.Length = 0) Then Return Nothing
        Return 0
    End Function

    '  Equipment history reads the single saved recording (measurement_samples and ups_samples); nothing is recorded separately.
    Public Shared Function Read(request As EquipmentHistoryRequest, legacy As Func(Of EquipmentHistoryRequest, IReadOnlyList(Of EquipmentHistoryRow)),
                                token As CancellationToken) As IReadOnlyList(Of EquipmentHistoryRow)
        Dim measurements = request.Metrics.Where(Function(metric) metric.Id <> "alarm:events").ToArray()
        If measurements.Length = 0 Then Return Array.Empty(Of EquipmentHistoryRow)()
        token.ThrowIfCancellationRequested()
        If measurements.Length = request.Metrics.Count Then Return legacy(request)
        Return legacy(New EquipmentHistoryRequest With {.Equipment = request.Equipment, .Metrics = measurements,
            .FromUtc = request.FromUtc, .ToUtc = request.ToUtc, .IndividualAcquisitions = request.IndividualAcquisitions,
            .RawOnly = request.RawOnly})
    End Function

    Public Shared Function SummarizeAlarm(evidence As EquipmentAlarmEvidence) As String
        If evidence Is Nothing Then Return "Alarm evidence unavailable"
        If evidence.Conditions Is Nothing Then Throw New InvalidDataException("Recorded alarm conditions are missing.")
        Dim states = evidence.Conditions.Where(Function(item) Not item.Value.Active.HasValue OrElse item.Value.Active.Value).
            Select(Function(item) item.Key & ": " & If(item.Value.Active.HasValue, item.Value.Severity, "Unknown") & " " & item.Value.Detail).ToList()
        If Not evidence.Complete Then states.Add("Incomplete alarm evidence")
        Return If(states.Count = 0, "No active observed alarms", String.Join("; ", states))
    End Function
End Class
