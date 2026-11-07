Imports System.Linq

Public NotInheritable Class HistoryEquipment
    Public Property Id As String = String.Empty
    Public Property Name As String = String.Empty
    Public Property Category As String = String.Empty
    Public Property SourceId As String = String.Empty
    Public Property SourceKey As String = String.Empty
    Public Property Description As String = String.Empty
    Public Property BankNumber As Integer?
    Public Property IsAggregate As Boolean
    Public Property IsCombinedSelection As Boolean
    Public Property IsConnected As Boolean = True
    Public Property IntervalSeconds As Double
    Public Property Metrics As IReadOnlyList(Of HistoryMetric) = Array.Empty(Of HistoryMetric)()

    Public Overrides Function ToString() As String
        Return Name & "  [" & Id & "]"
    End Function
End Class

Public NotInheritable Class HistoryMetric
    Public Property Id As String = String.Empty
    Public Property Name As String = String.Empty
    Public Property Unit As String = String.Empty
    Public Property Basis As String = String.Empty
    Public Property RatedCapacityKva As Double?
    Public Property CapacityBasis As String = String.Empty
    Public Property IsPower As Boolean
    Public Property IsEstimate As Boolean
    Public Property IsState As Boolean
    Public Property CanSum As Boolean
    Public Property MinimumMappingRevision As Integer
    Public Property SourceId As String = String.Empty
    Public Property SourceMetricId As String = String.Empty
    Public Property SourceMetricName As String = String.Empty
    Public Property EquipmentId As String = String.Empty
    Public Property EquipmentModel As String = String.Empty
    Public Property ModelBasis As String = String.Empty
    Public Property SourcePhase As Integer
    Public Property VoltageReference As String = String.Empty
    Public Property AtsBankNumber As Integer?
    Public Property ConfiguredSourceDescription As String = String.Empty
    Public Property TopologyBasis As String = String.Empty
    Public Property IsAlarm As Boolean
    Public Property PeakSourceMetricId As String = String.Empty
    Public Function WithCapacity(rating As Tuple(Of Double?, String)) As HistoryMetric
        Dim copy = DirectCast(MemberwiseClone(), HistoryMetric)
        copy.RatedCapacityKva = rating.Item1
        copy.CapacityBasis = rating.Item2
        Return copy
    End Function
    Public Function WithUpsMetadata(device As UpsDevice, Optional bank As AtsBankDefinition = Nothing) As HistoryMetric
        Dim copy = WithCapacity(HistoryCapacity.Ups(device))
        copy.EquipmentModel = device.Model
        copy.ModelBasis = "Current registry (" & device.Verification.ToString() & "); not historical model verification or a datasheet"
        copy.AtsBankNumber = device.AtsBank
        copy.ConfiguredSourceDescription = If(bank Is Nothing, String.Empty, bank.SourceDescription)
        copy.TopologyBasis = "Current configured ATS membership/source description only; approved time-valid wiring and active ATS source are not recorded"
        Return copy
    End Function
    Public ReadOnly Property IsIntervalPeak As Boolean
        Get
            Return PeakSourceMetricId.Length > 0
        End Get
    End Property
    Public Overrides Function ToString() As String
        Return Name & If(Unit.Length > 0, " (" & Unit & ")", "")
    End Function
End Class

Public NotInheritable Class HistoryCapacity
    Public Shared Function Transformer(settings As RecordingSettings, id As String) As Tuple(Of Double?, String)
        If id <> "TOTAL" Then
            Dim rated = settings.GetAnalyzer(id).RatedKva
            Return Tuple.Create(rated, If(rated.HasValue, "Current analyzer master data; historical nameplate changes not tracked here", "Transformer rated kVA is not configured."))
        End If
        Dim individual = {"T1", "T2", "T3", "T4"}.Select(Function(key) settings.GetAnalyzer(key).RatedKva).ToArray()
        If individual.All(Function(value) value.HasValue AndAlso Double.IsFinite(value.Value) AndAlso value.Value > 0) Then
            Dim total = individual.Sum(Function(value) value.Value)
            Dim configured = settings.GetAnalyzer("TOTAL").RatedKva
            Dim basis = "Sum of current T1-T4 nameplate ratings; not a switchgear or system limit"
            If configured.HasValue AndAlso Math.Abs(configured.Value - total) > Math.Max(1, total * 0.01R) Then
                basis &= "; TOTAL master value " & EquipmentAnalysis.Num(configured.Value) & " kVA differs from this sum"
            End If
            Return Tuple.Create(CType(total, Double?), basis)
        End If
        Dim aggregate = settings.GetAnalyzer("TOTAL").RatedKva
        If aggregate.HasValue AndAlso Double.IsFinite(aggregate.Value) AndAlso aggregate.Value > 0 Then
            Return Tuple.Create(aggregate, "Current TOTAL master data; one or more T1-T4 ratings missing; historical rating unverified")
        End If
        Return Tuple.Create(CType(Nothing, Double?), "No complete factory rating: " &
            individual.Count(Function(value) value.HasValue AndAlso Double.IsFinite(value.Value) AndAlso value.Value > 0).ToString() & " of 4 transformer ratings configured")
    End Function

    Public Shared Function Ups(device As UpsDevice) As Tuple(Of Double?, String)
        If device Is Nothing OrElse Not Double.IsFinite(device.RatedKva) OrElse device.RatedKva <= 0 Then
            Return Tuple.Create(CType(Nothing, Double?), "UPS rated kVA not available in the current registry")
        End If
        Return Tuple.Create(CType(device.RatedKva, Double?), "Current UPS registry (" & device.Verification.ToString() & "); historical rating unverified")
    End Function
End Class

Public NotInheritable Class EquipmentHistoryRow
    Public Property EquipmentId As String = String.Empty
    Public Property MetricId As String = String.Empty
    Public Property TimestampUtc As DateTime
    Public Property AcquiredUtc As DateTime?
    Public Property Value As Double?
    Public Property Quality As String = String.Empty
    Public Property Evidence As String = String.Empty
    Public Property Members As Integer = 1
    Public Property ExpectedMembers As Integer = 1
    Public Property Revision As Integer
    Public Property RecordedIntervalSeconds As Double?
    Public Property EnergyEligible As Boolean = True
    Public Property SourceTimes As IReadOnlyDictionary(Of String, DateTime)
    Public Property IsFullAcquisition As Boolean
    Public Property ObservationId As String = String.Empty
    Public Property AlarmSummary As String = "Alarm evidence unavailable"
    Public Property AcquisitionQuality As String = String.Empty
    Public Property EventKind As String = String.Empty
    Public Property ConditionKey As String = String.Empty
    Public Property ConditionName As String = String.Empty
    Public Property EventCategory As String = String.Empty
    Public Property EventState As String = String.Empty
    Public Property EventSeverity As String = String.Empty
    Public Property EventAcknowledged As String = String.Empty
    Public Property EventDetails As String = String.Empty
End Class

Public NotInheritable Class EquipmentHistoryRequest
    Public Property Equipment As HistoryEquipment
    Public Property Metrics As IReadOnlyList(Of HistoryMetric)
    Public Property FromUtc As DateTime
    Public Property ToUtc As DateTime
    Public Property IndividualAcquisitions As Boolean
    Public Property RawOnly As Boolean
    Public Property ThinSeconds As Integer
    Public Property IsCombined As Boolean
    Public Property IncludedEquipment As IReadOnlyList(Of HistoryEquipment) = Array.Empty(Of HistoryEquipment)()
End Class

Public NotInheritable Class EquipmentHistoryResult
    Public Property Request As EquipmentHistoryRequest
    Public Property GeneratedUtc As DateTime = DateTime.UtcNow
    Public Property Rows As IReadOnlyList(Of EquipmentHistoryRow) = Array.Empty(Of EquipmentHistoryRow)()
    Public Property AlarmEvents As IReadOnlyList(Of EquipmentHistoryRow) = Array.Empty(Of EquipmentHistoryRow)()
    Public ReadOnly Property Statistics As IReadOnlyList(Of EquipmentHistoryStatistics)
        Get
            If Request.IsCombined Then
                Return Request.IncludedEquipment.SelectMany(
                    Function(asset)
                        Return Request.Metrics.Where(Function(metric) metric.EquipmentId = asset.Id AndAlso Not metric.IsAlarm).
                            Select(Function(metric)
                                       Dim rows = Me.Rows.Where(Function(row) row.EquipmentId = asset.Id AndAlso row.MetricId = metric.Id).ToArray()
                                       Dim statistic = EquipmentHistoryStatistics.Calculate(metric, rows, asset.IntervalSeconds)
                                       statistic.EquipmentId = asset.Id
                                       statistic.EquipmentName = asset.Name
                                       Return statistic
                                   End Function)
                    End Function).ToArray()
            End If
            Return Request.Metrics.Where(Function(metric) Not metric.IsAlarm).Select(Function(metric) EquipmentHistoryStatistics.Calculate(
                metric, GetObservations(metric), Request.Equipment.IntervalSeconds)).ToArray()
        End Get
    End Property

    Public Function GetObservations(metric As HistoryMetric) As IReadOnlyList(Of EquipmentHistoryRow)
        Dim selected = Rows.Where(Function(row) row.MetricId = metric.Id).ToArray()
        If Not metric.IsIntervalPeak Then Return selected
        Return selected.GroupBy(Function(row) New With {
            Key .EquipmentId = If(Request.IsCombined, row.EquipmentId, String.Empty),
            Key .Utc = EquipmentHistoryTable.BucketStart(row.TimestampUtc, CInt(row.RecordedIntervalSeconds.Value * 1000)),
            Key .Interval = row.RecordedIntervalSeconds.Value}).
            OrderBy(Function(group) group.Key.Utc).Select(
                Function(group)
                    Dim valid = group.Where(Function(row) row.Quality = "Good" AndAlso row.Value.HasValue AndAlso Double.IsFinite(row.Value.Value)).ToArray()
                    Return New EquipmentHistoryRow With {.EquipmentId = If(Request.IsCombined, group.First().EquipmentId, Request.Equipment.Id), .MetricId = metric.Id,
                        .TimestampUtc = If(group.Key.Utc < Request.FromUtc, Request.FromUtc, group.Key.Utc),
                        .RecordedIntervalSeconds = group.Key.Interval,
                        .Value = If(valid.Length > 0, CType(valid.Max(Function(row) row.Value.Value), Double?), Nothing),
                        .Quality = If(valid.Length > 0, "Good", "Unavailable interval peak"), .EnergyEligible = False}
                End Function).ToArray()
    End Function
End Class

Public NotInheritable Class EquipmentHistoryStatistics
    Public Property EquipmentId As String = String.Empty
    Public Property EquipmentName As String = String.Empty
    Public Property Metric As HistoryMetric
    Public Property ValidCount As Integer
    Public Property MissingCount As Integer
    Public Property Minimum As Double?
    Public Property Maximum As Double?
    Public Property Mean As Double?
    Public Property EnergyKwh As Double?
    Public Property IntegratedSeconds As Double
    Public Property GapCount As Integer

    Public Shared Function Calculate(metric As HistoryMetric, rows As IReadOnlyList(Of EquipmentHistoryRow),
                                    intervalSeconds As Double) As EquipmentHistoryStatistics
        Dim result As New EquipmentHistoryStatistics With {.Metric = metric}
        Dim good = rows.Where(Function(row) row.Value.HasValue AndAlso Double.IsFinite(row.Value.Value) AndAlso
            row.Quality = "Good").OrderBy(Function(row) row.TimestampUtc).ToArray()
        result.ValidCount = good.Length
        result.MissingCount = rows.Count - good.Length
        If good.Length = 0 Then Return result
        result.Minimum = good.Min(Function(row) row.Value.Value)
        result.Maximum = good.Max(Function(row) row.Value.Value)
        If Not metric.IsState Then result.Mean = good.Average(Function(row) row.Value.Value)
        Dim previous As EquipmentHistoryRow = Nothing
        Dim integral As Double
        For Each row In rows.OrderBy(Function(item) item.TimestampUtc)
            If row.Quality <> "Good" OrElse Not row.Value.HasValue OrElse Not row.AcquiredUtc.HasValue OrElse Not row.EnergyEligible OrElse
               Not Double.IsFinite(row.Value.Value) Then
                If previous IsNot Nothing Then result.GapCount += 1
                previous = Nothing
                Continue For
            End If
            If previous IsNot Nothing Then
                Dim seconds = (row.TimestampUtc - previous.TimestampUtc).TotalSeconds
                Dim sourceSeconds = (row.AcquiredUtc.Value - previous.AcquiredUtc.Value).TotalSeconds
                Dim allowedInterval = Math.Min(If(row.RecordedIntervalSeconds, intervalSeconds), If(previous.RecordedIntervalSeconds, intervalSeconds))
                Dim sourcesAdvance = row.SourceTimes Is Nothing AndAlso previous.SourceTimes Is Nothing
                If row.SourceTimes IsNot Nothing AndAlso previous.SourceTimes IsNot Nothing Then
                    sourcesAdvance = row.SourceTimes.Count = previous.SourceTimes.Count AndAlso row.SourceTimes.All(
                        Function(item) previous.SourceTimes.ContainsKey(item.Key) AndAlso item.Value > previous.SourceTimes(item.Key) AndAlso
                            (item.Value - previous.SourceTimes(item.Key)).TotalSeconds <= Math.Max(2, 2 * allowedInterval))
                End If
                If sourcesAdvance AndAlso seconds > 0 AndAlso seconds <= Math.Max(2, 2 * allowedInterval) AndAlso sourceSeconds > 0 AndAlso
                   sourceSeconds <= Math.Max(2, 2 * allowedInterval) Then
                    result.IntegratedSeconds += seconds
                    integral += (row.Value.Value + previous.Value.Value) / 2 * seconds / 3600
                Else
                    result.GapCount += 1
                End If
            End If
            previous = row
        Next
        If metric.IsPower AndAlso result.IntegratedSeconds > 0 Then result.EnergyKwh = integral
        Return result
    End Function
End Class
