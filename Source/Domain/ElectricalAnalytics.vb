Imports System.Globalization
Imports System.Linq

Public NotInheritable Class HistoricalMeterSample
    Public Property TransformerName As String = String.Empty
    Public Property TimestampUtc As DateTime
    Public Property Quality As String = String.Empty
    Public Property ActivePowerKw As Double?
    Public Property VoltageL1N As Double?
    Public Property VoltageL2N As Double?
    Public Property VoltageL3N As Double?
    Public Property CurrentL1 As Double?
    Public Property CurrentL2 As Double?
    Public Property CurrentL3 As Double?
    Public Property TemperatureC As Double?

    Public ReadOnly Property IsGood As Boolean
        Get
            Return String.Equals(Quality, NameOf(MeterQuality.Good), StringComparison.OrdinalIgnoreCase)
        End Get
    End Property
End Class

Public NotInheritable Class AnalyticsPoint
    Public Property TimestampUtc As DateTime
    Public Property SeriesName As String = String.Empty
    Public Property X As Double
    Public Property Y As Double
    Public Property Detail As String = String.Empty
    Public Property IsObservedMaximum As Boolean
End Class

Public NotInheritable Class RegressionSummary
    Public Property SampleCount As Integer
    Public Property Slope As Double
    Public Property Intercept As Double
    Public Property Correlation As Double
End Class

Public NotInheritable Class AnalyticsTotalCycle
    Public Property AcquisitionSessionId As String = String.Empty
    Public Property AcquisitionCycleId As Long
    Public Property TimestampUtc As DateTime
    Public Property TotalActivePowerKw As Double
    Public Property TransformerValuesKw As IReadOnlyDictionary(Of String, Double) =
        New Dictionary(Of String, Double)(StringComparer.OrdinalIgnoreCase)
End Class

Public Module ElectricalAnalytics
    Public Const MinimumSamplesForStatistics As Integer = 10
    Public Const MinimumRollingCoverage As Double = 0.8R
    Public Const TotalDisplayMaximumKw As Double = 10000.0R

    Public Function BuildAlignedTotalCycles(points As IEnumerable(Of HistorianMeasurementPoint)) As List(Of AnalyticsTotalCycle)
        Dim result As New List(Of AnalyticsTotalCycle)()
        If points Is Nothing Then Return result
        Dim grouped = points.Where(Function(point) point IsNot Nothing AndAlso
                String.Equals(point.ParameterId, "PTOT_KW", StringComparison.Ordinal) AndAlso
                String.Equals(point.Quality, "Good", StringComparison.OrdinalIgnoreCase) AndAlso
                point.AcquisitionCycleId > 0 AndAlso Not String.IsNullOrWhiteSpace(point.AcquisitionSessionId) AndAlso
                Double.IsFinite(point.Value)).
            GroupBy(Function(point) point.AcquisitionSessionId & ":" &
                point.AcquisitionCycleId.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal)

        For Each cycle In grouped
            Dim total = cycle.Where(Function(point) String.Equals(point.AnalyzerId, "TOTAL", StringComparison.OrdinalIgnoreCase)).
                OrderByDescending(Function(point) point.TimestampUtc).FirstOrDefault()
            If total Is Nothing OrElse Not String.Equals(total.SampleKind, "Calculated", StringComparison.OrdinalIgnoreCase) Then Continue For

            Dim constituentValues As New Dictionary(Of String, Double)(StringComparer.OrdinalIgnoreCase)
            Dim constituentTimestamps As New List(Of DateTime)()
            Dim complete = True
            For Each analyzer In {"T1", "T2", "T3", "T4"}
                Dim constituent = cycle.Where(Function(point) String.Equals(point.AnalyzerId, analyzer, StringComparison.OrdinalIgnoreCase)).
                    OrderByDescending(Function(point) point.TimestampUtc).FirstOrDefault()
                If constituent Is Nothing OrElse Not String.Equals(constituent.SampleKind, "Measured", StringComparison.OrdinalIgnoreCase) Then
                    complete = False
                    Exit For
                End If
                constituentValues(analyzer) = constituent.Value
                constituentTimestamps.Add(constituent.TimestampUtc)
            Next
            If Not complete Then Continue For
            If constituentTimestamps.Max() - constituentTimestamps.Min() >
               TimeSpan.FromMilliseconds(LoadGaugeMath.MaximumTotalAlignmentSkewMilliseconds) OrElse
               total.TimestampUtc <> constituentTimestamps.Max() Then Continue For
            Dim constituentSum = constituentValues.Values.Sum()
            If Not Double.IsFinite(constituentSum) OrElse
               Math.Abs(constituentSum - total.Value) > Math.Max(0.000001R, Math.Abs(total.Value) * 0.000000001R) Then Continue For
            result.Add(New AnalyticsTotalCycle With {
                .AcquisitionSessionId = total.AcquisitionSessionId,
                .AcquisitionCycleId = total.AcquisitionCycleId,
                .TimestampUtc = total.TimestampUtc,
                .TotalActivePowerKw = total.Value,
                .TransformerValuesKw = constituentValues
            })
        Next
        Return result.OrderBy(Function(cycle) cycle.TimestampUtc).ToList()
    End Function

    Public Function CalculateCoverage(samples As IEnumerable(Of HistoricalMeterSample),
                                      period As TimeSpan, pollingIntervalSeconds As Integer) As Double?
        If samples Is Nothing OrElse period <= TimeSpan.Zero OrElse pollingIntervalSeconds < 1 Then Return Nothing
        Return CalculateTimestampCoverage(samples.
            Where(Function(sample) sample IsNot Nothing AndAlso sample.IsGood).
            Select(Function(sample) sample.TimestampUtc), period, pollingIntervalSeconds)
    End Function

    Public Function CalculatePairCoverage(points As IEnumerable(Of AnalyticsPoint),
                                          period As TimeSpan, pollingIntervalSeconds As Integer) As Double?
        If points Is Nothing OrElse period <= TimeSpan.Zero OrElse pollingIntervalSeconds < 1 Then Return Nothing
        Return CalculateTimestampCoverage(points.
            Where(Function(point) point IsNot Nothing AndAlso Double.IsFinite(point.X) AndAlso Double.IsFinite(point.Y)).
            Select(Function(point) point.TimestampUtc), period, pollingIntervalSeconds)
    End Function

    Private Function CalculateTimestampCoverage(timestamps As IEnumerable(Of DateTime),
                                                period As TimeSpan,
                                                pollingIntervalSeconds As Integer) As Double?
        Dim expected = Math.Ceiling(period.TotalSeconds / pollingIntervalSeconds)
        If Not Double.IsFinite(expected) OrElse expected <= 0 Then Return Nothing
        Dim uniqueCount = CountExpectedPollSlots(timestamps, pollingIntervalSeconds)
        Return Math.Min(100.0R, uniqueCount / expected * 100.0R)
    End Function

    Public Function CountExpectedPollSlots(timestamps As IEnumerable(Of DateTime),
                                           pollingIntervalSeconds As Integer) As Integer
        If timestamps Is Nothing OrElse pollingIntervalSeconds < 1 Then Return 0
        Dim uniqueCount = 0
        Dim lastCounted As DateTime? = Nothing
        Dim minimumSpacing = TimeSpan.FromSeconds(pollingIntervalSeconds * 0.5R)
        For Each timestamp In timestamps.OrderBy(Function(value) value)
            If Not lastCounted.HasValue OrElse timestamp - lastCounted.Value >= minimumSpacing Then
                uniqueCount += 1
                lastCounted = timestamp
            End If
        Next
        Return uniqueCount
    End Function

    Public Function CalculateMedianCadenceSeconds(timestamps As IEnumerable(Of DateTime)) As Double?
        If timestamps Is Nothing Then Return Nothing
        Dim ordered = timestamps.Distinct().OrderBy(Function(value) value).ToArray()
        If ordered.Length < 2 Then Return Nothing
        Dim intervals = ordered.Skip(1).
            Select(Function(timestamp, index) (timestamp - ordered(index)).TotalSeconds).
            Where(Function(seconds) seconds > 0 AndAlso Double.IsFinite(seconds)).
            OrderBy(Function(seconds) seconds).
            ToArray()
        If intervals.Length = 0 Then Return Nothing
        Dim middle = intervals.Length \ 2
        If intervals.Length Mod 2 = 0 Then Return (intervals(middle - 1) + intervals(middle)) / 2.0R
        Return intervals(middle)
    End Function

    Public Function CreateLoadTemperaturePoints(samples As IEnumerable(Of HistoricalMeterSample)) As List(Of AnalyticsPoint)
        Dim points As New List(Of AnalyticsPoint)()
        If samples Is Nothing Then Return points
        Dim validSamples = samples.Where(Function(sample) sample IsNot Nothing AndAlso sample.IsGood AndAlso
                                          IsFinite(sample.ActivePowerKw) AndAlso IsFinite(sample.TemperatureC)).ToArray()
        If validSamples.Length = 0 Then Return points
        Dim highestObservedTemperature = validSamples.Max(Function(sample) sample.TemperatureC.Value)
        For Each sample In validSamples
            points.Add(New AnalyticsPoint With {
                .TimestampUtc = sample.TimestampUtc,
                .SeriesName = sample.TransformerName,
                .X = sample.ActivePowerKw.Value,
                .Y = sample.TemperatureC.Value,
                .Detail = If(sample.TemperatureC.Value = highestObservedTemperature,
                    "Highest observed TDEGC in selected period; not an alarm or physical maximum guarantee.",
                    "Active power / TDEGC observation"),
                .IsObservedMaximum = sample.TemperatureC.Value = highestObservedTemperature
            })
        Next
        Return points
    End Function

    Public Function CreateRollingLoadTemperaturePoints(samples As IEnumerable(Of HistoricalMeterSample),
                                                       window As TimeSpan,
                                                       pollingIntervalSeconds As Integer) As List(Of AnalyticsPoint)
        Dim points As New List(Of AnalyticsPoint)()
        If samples Is Nothing OrElse window <= TimeSpan.Zero OrElse pollingIntervalSeconds < 1 Then Return points

        Dim ordered = samples.
            Where(Function(sample) sample IsNot Nothing AndAlso sample.IsGood).
            OrderBy(Function(sample) sample.TimestampUtc).
            ToArray()
        For Each target In ordered
            If Not IsFinite(target.TemperatureC) Then Continue For
            Dim averageLoad = GetTimeWeightedLoadAverage(ordered, target.TimestampUtc, window, pollingIntervalSeconds)
            If Not averageLoad.HasValue Then Continue For
            points.Add(New AnalyticsPoint With {
                .TimestampUtc = target.TimestampUtc,
                .SeriesName = target.TransformerName,
                .X = averageLoad.Value,
                .Y = target.TemperatureC.Value,
                .Detail = "Time-weighted preceding load average"
            })
        Next
        Return points
    End Function

    Public Function CreateCurrentVoltagePoints(samples As IEnumerable(Of HistoricalMeterSample),
                                               phase As Integer) As List(Of AnalyticsPoint)
        Dim points As New List(Of AnalyticsPoint)()
        If samples Is Nothing OrElse phase < 1 OrElse phase > 3 Then Return points
        For Each sample In samples
            If sample Is Nothing OrElse Not sample.IsGood Then Continue For
            Dim current = GetPhaseCurrent(sample, phase)
            Dim voltage = GetPhaseVoltage(sample, phase)
            If Not IsFinite(current) OrElse Not IsFinite(voltage) Then Continue For
            points.Add(New AnalyticsPoint With {
                .TimestampUtc = sample.TimestampUtc,
                .SeriesName = "L" & phase.ToString(CultureInfo.InvariantCulture),
                .X = current.Value,
                .Y = voltage.Value,
                .Detail = "Same-block phase current / matching L-N voltage"
            })
        Next
        Return points
    End Function

    Public Function CreatePowerVoltagePoints(samples As IEnumerable(Of HistoricalMeterSample),
                                             phase As Integer) As List(Of AnalyticsPoint)
        Dim points As New List(Of AnalyticsPoint)()
        If samples Is Nothing OrElse phase < 1 OrElse phase > 3 Then Return points
        For Each sample In samples
            If sample Is Nothing OrElse Not sample.IsGood Then Continue For
            Dim voltage = GetPhaseVoltage(sample, phase)
            If Not IsFinite(sample.ActivePowerKw) OrElse Not IsFinite(voltage) Then Continue For
            points.Add(New AnalyticsPoint With {
                .TimestampUtc = sample.TimestampUtc,
                .SeriesName = "L" & phase.ToString(CultureInfo.InvariantCulture),
                .X = sample.ActivePowerKw.Value,
                .Y = voltage.Value,
                .Detail = "Active power / phase L-N voltage"
            })
        Next
        Return points
    End Function

    Public Function CalculateRegression(points As IReadOnlyCollection(Of AnalyticsPoint)) As RegressionSummary
        If points Is Nothing OrElse points.Count < MinimumSamplesForStatistics Then Return Nothing
        Dim finitePoints = points.Where(Function(point) point IsNot Nothing AndAlso
                                        Double.IsFinite(point.X) AndAlso Double.IsFinite(point.Y)).ToArray()
        If finitePoints.Length < MinimumSamplesForStatistics Then Return Nothing

        Dim meanX = finitePoints.Average(Function(point) point.X)
        Dim meanY = finitePoints.Average(Function(point) point.Y)
        Dim sumXX As Double = 0
        Dim sumYY As Double = 0
        Dim sumXY As Double = 0
        For Each point In finitePoints
            Dim deltaX = point.X - meanX
            Dim deltaY = point.Y - meanY
            sumXX += deltaX * deltaX
            sumYY += deltaY * deltaY
            sumXY += deltaX * deltaY
        Next
        If Not Double.IsFinite(sumXX) OrElse Not Double.IsFinite(sumYY) OrElse
           Not Double.IsFinite(sumXY) OrElse sumXX <= 0 OrElse sumYY <= 0 Then Return Nothing

        Dim denominator = Math.Sqrt(sumXX) * Math.Sqrt(sumYY)
        If Not Double.IsFinite(denominator) OrElse denominator <= 0 Then Return Nothing
        Dim correlation = Math.Clamp(sumXY / denominator, -1.0R, 1.0R)
        Dim slope = sumXY / sumXX
        Dim intercept = meanY - slope * meanX
        If Not Double.IsFinite(slope) OrElse Not Double.IsFinite(intercept) OrElse Not Double.IsFinite(correlation) Then Return Nothing
        Return New RegressionSummary With {
            .SampleCount = finitePoints.Length,
            .Slope = slope,
            .Intercept = intercept,
            .Correlation = correlation
        }
    End Function

    Public Function GetPhaseCurrent(sample As HistoricalMeterSample, phase As Integer) As Double?
        If sample Is Nothing Then Return Nothing
        Select Case phase
            Case 1
                Return sample.CurrentL1
            Case 2
                Return sample.CurrentL2
            Case 3
                Return sample.CurrentL3
            Case Else
                Return Nothing
        End Select
    End Function

    Public Function GetPhaseVoltage(sample As HistoricalMeterSample, phase As Integer) As Double?
        If sample Is Nothing Then Return Nothing
        Select Case phase
            Case 1
                Return sample.VoltageL1N
            Case 2
                Return sample.VoltageL2N
            Case 3
                Return sample.VoltageL3N
            Case Else
                Return Nothing
        End Select
    End Function

    Public Function IsFinite(value As Double?) As Boolean
        Return value.HasValue AndAlso Double.IsFinite(value.Value)
    End Function

    Private Function GetTimeWeightedLoadAverage(samples As IReadOnlyList(Of HistoricalMeterSample),
                                                timestamp As DateTime,
                                                window As TimeSpan,
                                                pollingIntervalSeconds As Integer) As Double?
        Dim start = timestamp - window
        Dim maximumGap = TimeSpan.FromSeconds(pollingIntervalSeconds * 2.5R)
        Dim prior As HistoricalMeterSample = Nothing
        For Each sample In samples
            If sample.TimestampUtc > start Then Exit For
            If sample.TimestampUtc <= start AndAlso IsFinite(sample.ActivePowerKw) Then prior = sample
        Next

        Dim cursor = start
        Dim coveredSeconds As Double = 0
        Dim weightedLoad As Double = 0
        For Each sample In samples
            If sample.TimestampUtc <= start Then Continue For
            If sample.TimestampUtc > timestamp Then Exit For
            If prior IsNot Nothing AndAlso IsFinite(prior.ActivePowerKw) Then
                Dim gap = sample.TimestampUtc - prior.TimestampUtc
                Dim duration = sample.TimestampUtc - cursor
                If gap <= maximumGap AndAlso duration > TimeSpan.Zero Then
                    Dim seconds = duration.TotalSeconds
                    coveredSeconds += seconds
                    weightedLoad += prior.ActivePowerKw.Value * seconds
                End If
            End If
            cursor = sample.TimestampUtc
            prior = sample
        Next
        If prior IsNot Nothing AndAlso IsFinite(prior.ActivePowerKw) AndAlso timestamp > cursor Then
            Dim gap = timestamp - prior.TimestampUtc
            If gap <= maximumGap Then
                Dim seconds = (timestamp - cursor).TotalSeconds
                coveredSeconds += seconds
                weightedLoad += prior.ActivePowerKw.Value * seconds
            End If
        End If

        Dim coverage = coveredSeconds / window.TotalSeconds
        If Not Double.IsFinite(coverage) OrElse coverage < MinimumRollingCoverage OrElse coveredSeconds <= 0 Then Return Nothing
        Dim average = weightedLoad / coveredSeconds
        Return If(Double.IsFinite(average), CType(average, Double?), Nothing)
    End Function
End Module
