Imports System.Globalization
Imports System.Linq
Imports System.Text.RegularExpressions

Public Structure ReportPoint
    Public T As DateTime
    Public V As Double

    Public Sub New(time As DateTime, value As Double)
        T = time
        V = value
    End Sub
End Structure

Public NotInheritable Class ReportFinding
    Public Property EquipmentId As String = String.Empty
    Public Property Severity As Integer
    Public Property Category As String = String.Empty
    Public Property Title As String = String.Empty
    Public Property Detail As String = String.Empty
    Public Property Metric As String = String.Empty
    Public Property StartUtc As DateTime?
    Public Property EndUtc As DateTime?
    Public Property Advice As String = String.Empty
    Public Property Cause As String = String.Empty
    Public Property EvidenceValue As Double?
    Public Property EvidenceUnit As String = String.Empty
    Public Property ReferenceValue As Double?
    Public Property DeviationPercent As Double?
    Public Property PeakUtc As DateTime?
    Public Property EvidenceLabel As String = "Observed extreme"

    Public ReadOnly Property SeverityName As String
        Get
            Return If(Severity >= 2, "CRITICAL", If(Severity = 1, "WARNING", "INFO"))
        End Get
    End Property
End Class

Public NotInheritable Class ChannelAnalysis
    Public Property Metric As HistoryMetric
    Public ReadOnly Property Points As New List(Of ReportPoint)()
    Public ReadOnly Property Findings As New List(Of ReportFinding)()
    Public ReadOnly Property Gaps As New List(Of ReportPoint)()
    Public Property BreakBefore As New HashSet(Of DateTime)()
    Public Property Stats As EquipmentHistoryStatistics
    Public Property PeakAt As DateTime
    Public Property LowAt As DateTime
    Public Property Median As Double
    Public Property StdDev As Double
    Public Property LastValue As Double
    Public Property TrendDelta As Double
    Public Property MedianStepSeconds As Double
    Public Property Summary As String = String.Empty
    Public Property P95 As Double
    Public Property ObservedCoveragePercent As Double
    Public ReadOnly Property Severity As Integer
        Get
            Return If(Findings.Count = 0, 0, Findings.Max(Function(item) item.Severity))
        End Get
    End Property
End Class

Public NotInheritable Class ScopeRow
    Public Property Name As String = String.Empty
    Public Property Unit As String = String.Empty
    Public Property Kind As String = String.Empty
    Public Property Samples As Integer
    Public Property Note As String = String.Empty
    Public Property Channel As ChannelAnalysis
    Public Property Metric As HistoryMetric
End Class

Public NotInheritable Class EventGroup
    Public Property EquipmentId As String = String.Empty
    Public Property Name As String = String.Empty
    Public Property Severity As Integer
    Public Property Count As Integer
    Public Property FirstUtc As DateTime
    Public Property LastUtc As DateTime
    Public Property LastState As String = String.Empty
    Public Property ActiveCount As Integer
    Public ReadOnly Property Times As New List(Of DateTime)()
    Public ReadOnly Property ActiveTimes As New List(Of DateTime)()
End Class

' Rule-based statistical analysis of a loaded history period. It never invents readings:
' every statement is derived from the saved good-quality observations.
Public NotInheritable Class EquipmentAnalysis
    Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture

    Public Property Result As EquipmentHistoryResult
    Public Property Comparison As ChannelComparisonStudy
    Public ReadOnly Property Channels As New List(Of ChannelAnalysis)()
    Public ReadOnly Property Findings As New List(Of ReportFinding)()
    Public ReadOnly Property Events As New List(Of EventGroup)()
    Public Property TotalSamples As Integer
    Public Property MissingSamples As Integer
    Public Property Completeness As Double = 100.0R
    Public ReadOnly Property ObservedCoveragePercent As Double
        Get
            Dim numeric = Scope.Where(Function(row) row.Kind = "Analog").ToArray()
            Return If(numeric.Length = 0, 0.0R, numeric.Average(Function(row) If(row.Channel Is Nothing, 0, row.Channel.ObservedCoveragePercent)))
        End Get
    End Property
    Public Property Summary As String = String.Empty
    Public ReadOnly Property Scope As New List(Of ScopeRow)()
    Public Property UpsPowerBalances As IReadOnlyList(Of UpsPowerBalanceStudy) = Array.Empty(Of UpsPowerBalanceStudy)()
    Friend ReadOnly RecordedSeries As New Dictionary(Of String, List(Of ReportPoint))(StringComparer.Ordinal)
    Public Property HealthScore As Integer = 100
    Public ReadOnly Property StudyName As String
        Get
            Return If(Result.Request.IsCombined OrElse Result.Request.Equipment.Id = "__ALL_EQUIPMENT__",
                "Selected equipment / selected channels", Result.Request.Equipment.Name)
        End Get
    End Property

    Public ReadOnly Property CoincidentGroups As IReadOnlyList(Of ReportFinding())
        Get
            Dim groups As New List(Of List(Of ReportFinding))()
            For Each equipment In Findings.Where(Function(item) item.Severity >= 1 AndAlso item.PeakUtc.HasValue AndAlso
                item.Category <> "Data quality" AndAlso item.Category <> "Alarm events" AndAlso item.Category <> "Correlation").
                GroupBy(Function(item) If(item.EquipmentId.Length > 0, item.EquipmentId,
                    ElectricalReportKnowledge.EquipmentIdentity(New HistoryMetric With {.Name = item.Metric}, Result.Request.Equipment.Id)))
                Dim current As List(Of ReportFinding) = Nothing
                For Each finding In equipment.OrderBy(Function(item) item.PeakUtc.Value)
                    If current Is Nothing OrElse (finding.PeakUtc.Value - current(0).PeakUtc.Value).TotalSeconds > 1 Then
                        current = New List(Of ReportFinding)()
                        groups.Add(current)
                    End If
                    current.Add(finding)
                Next
            Next
            Return groups.Select(Function(group) group.ToArray()).ToArray()
        End Get
    End Property

    Public ReadOnly Property Severity As Integer
        Get
            Return If(Findings.Count = 0, 0, Findings.Max(Function(item) item.Severity))
        End Get
    End Property

    Public ReadOnly Property Verdict As String
        Get
            Select Case Severity
                Case 2 : Return "ACTION RECOMMENDED"
                Case 1 : Return "ATTENTION"
                Case Else : Return "NO FLAGS IN SELECTED DATA"
            End Select
        End Get
    End Property

    Public Shared Function Stamp(value As DateTime) As String
        Return value.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", Inv) & " UTC"
    End Function

    Public Shared Function Num(value As Double) As String
        Return value.ToString(If(Math.Abs(value) >= 100.0R, "0.0", "0.0#"), Inv)
    End Function

    Public Shared Function Span(value As TimeSpan) As String
        If value.TotalSeconds < 1.0R Then Return "under 1 s"
        If value.TotalMinutes < 1.0R Then Return CInt(Math.Round(value.TotalSeconds)).ToString(Inv) & " s"
        If value.TotalHours < 1.0R Then Return CInt(Math.Floor(value.TotalMinutes)).ToString(Inv) & " min " & value.Seconds.ToString(Inv) & " s"
        If value.TotalDays < 1.0R Then Return CInt(Math.Floor(value.TotalHours)).ToString(Inv) & " h " & value.Minutes.ToString(Inv) & " min"
        Return CInt(Math.Floor(value.TotalDays)).ToString(Inv) & " d " & value.Hours.ToString(Inv) & " h"
    End Function

    Private Structure Run
        Public StartIndex As Integer
        Public EndIndex As Integer
        Public PeakIndex As Integer
        Public PeakScore As Double
    End Structure

    Public Shared Function Build(result As EquipmentHistoryResult) As EquipmentAnalysis
        Dim analysis As New EquipmentAnalysis With {.Result = result}
        Dim statistics = result.Statistics.ToDictionary(Function(item) item.Metric.Id)
        For Each metric In result.Request.Metrics
            If metric.Id = "alarm:events" Then Continue For
            If metric.IsAlarm OrElse metric.IsState Then
                analysis.Scope.Add(New ScopeRow With {.Name = metric.Name, .Unit = metric.Unit, .Kind = "State / alarm", .Samples = result.GetObservations(metric).Count,
                    .Note = "Listed in the alarm timeline"})
                Continue For
            End If
            Dim channel As New ChannelAnalysis With {.Metric = metric}
            Dim observations = result.GetObservations(metric)
            Dim good = observations.Where(Function(row) row.Quality = "Good" AndAlso row.Value.HasValue AndAlso Double.IsFinite(row.Value.Value)).
                OrderBy(Function(row) row.TimestampUtc).ToArray()
            Dim stats As EquipmentHistoryStatistics = Nothing
            statistics.TryGetValue(metric.Id, stats)
            Dim breaks As New HashSet(Of DateTime)()
            Dim interrupted = False
            For Each row In observations.OrderBy(Function(item) item.TimestampUtc)
                If row.Quality <> "Good" OrElse Not row.Value.HasValue OrElse Not Double.IsFinite(row.Value.GetValueOrDefault()) Then
                    interrupted = True
                Else
                    If interrupted Then breaks.Add(row.TimestampUtc)
                    interrupted = False
                End If
            Next
            Dim unavailable = observations.Where(Function(row) row.Quality <> "Good").Select(Function(row) row.Quality).Distinct().Take(3)
            analysis.AddAnalog(metric, observations.Count, good.Select(Function(row) New ReportPoint(row.TimestampUtc, row.Value.Value)).ToList(),
                stats, breaks, String.Join("; ", unavailable))
        Next
        analysis.Complete()
        Return analysis
    End Function

    ' Builds a report from series that are already in memory (for example an online analyzer recording).
    Public Shared Function BuildFromSeries(result As EquipmentHistoryResult, series As IEnumerable(Of Tuple(Of HistoryMetric, Integer, List(Of ReportPoint)))) As EquipmentAnalysis
        Dim analysis As New EquipmentAnalysis With {.Result = result}
        For Each item In series
            analysis.RecordedSeries.Add(item.Item1.Id, item.Item3)
            Dim valid As New List(Of ReportPoint)()
            Dim breaks As New HashSet(Of DateTime)()
            Dim interrupted = False
            For Each point In item.Item3.OrderBy(Function(value) value.T)
                If Not Double.IsFinite(point.V) Then
                    interrupted = True
                    Continue For
                End If
                If interrupted Then breaks.Add(point.T)
                valid.Add(point)
                interrupted = False
            Next
            analysis.AddAnalog(item.Item1, item.Item2, valid, Nothing, breaks)
        Next
        analysis.Complete()
        Return analysis
    End Function

    Private Sub AddAnalog(metric As HistoryMetric, total As Integer, good As List(Of ReportPoint), stats As EquipmentHistoryStatistics,
                          Optional breaks As HashSet(Of DateTime) = Nothing, Optional unavailableEvidence As String = "")
        TotalSamples += total
        MissingSamples += Math.Max(0, total - good.Count)
        Dim scopeRow As New ScopeRow With {.Name = metric.Name, .Unit = metric.Unit, .Kind = "Analog", .Samples = total, .Metric = metric}
        Scope.Add(scopeRow)
        If good.Count = 0 Then
            scopeRow.Note = "No valid data" & If(unavailableEvidence.Length > 0, ": " & unavailableEvidence, "")
            Findings.Add(New ReportFinding With {.Severity = 1, .Category = "Data quality", .Metric = metric.Name, .Title = "No valid data",
                .Detail = metric.Name & " has " & total.ToString(Inv) & " saved sample(s) in this period but none with good quality, so it could not be analysed." &
                    If(unavailableEvidence.Length > 0, " Recorded channel evidence: " & unavailableEvidence, "") &
                    If(metric.Basis.Length > 0, " Measurement basis: " & metric.Basis, ""),
                .Advice = "Verify recorded device capability, measurement reference, mapping and communication quality. An unsupported or unselected channel is unknown, not zero; it does not prove a supply outage."})
            Return
        End If
        Dim channel As New ChannelAnalysis With {.Metric = metric, .Stats = stats}
        If breaks IsNot Nothing Then channel.BreakBefore = breaks
        channel.Points.AddRange(good)
        Channels.Add(channel)
        scopeRow.Channel = channel
        Describe(channel)
    End Sub

    Private Sub Complete()
        UpsPowerBalances = UpsPowerBalanceStudy.Build(Me)
        For Each study In UpsPowerBalances.Where(Function(item) item.ReverseOrInconsistentPairs > 0)
            Findings.Add(New ReportFinding With {.Severity = 1, .Category = "Electrical evidence", .Metric = study.EquipmentId,
                .Title = "UPS power balance unsuitable for efficiency",
                .Detail = study.EquipmentId & " has " & study.ReverseOrInconsistentPairs & " of " & study.PairedAcquisitions &
                    " paired acquisitions with nonpositive input, reverse output or output above input. Signed power differences are retained, but an efficiency-like ratio is withheld.",
                .Cause = "Battery/source contribution, operating mode, card internal update timing, mapping and meter uncertainty are unverified alternatives, not established causes.",
                .Advice = "Verify active-power mappings/units, internal meter update times, UPS mode and battery energy balance before interpreting the AC difference as loss. A shared acquisition timestamp does not prove internal meters updated simultaneously."})
        Next
        CrossChannelChecks()
        ReadEvents()
        Correlate()
        Finish()
    End Sub


    Private Sub Describe(channel As ChannelAnalysis)
        Dim points = channel.Points
        Dim values = points.Select(Function(point) point.V).OrderBy(Function(value) value).ToArray()
        channel.Median = If(values.Length Mod 2 = 0, values(values.Length \ 2 - 1) / 2.0R + values(values.Length \ 2) / 2.0R, values(values.Length \ 2))
        channel.P95 = values(Math.Max(0, CInt(Math.Ceiling(values.Length * 0.95R)) - 1))
        Dim mean = values.Average()
        channel.StdDev = Math.Sqrt(values.Sum(Function(value) (value - mean) * (value - mean)) / values.Length)
        channel.LastValue = points(points.Count - 1).V
        Dim peak = points(0)
        Dim low = points(0)
        For Each point In points
            If point.V > peak.V Then peak = point
            If point.V < low.V Then low = point
        Next
        channel.PeakAt = peak.T
        channel.LowAt = low.T
        Dim third = Math.Max(1, points.Count \ 3)
        channel.TrendDelta = points.Skip(points.Count - third).Average(Function(point) point.V) - points.Take(third).Average(Function(point) point.V)
        Dim steps = New List(Of Double)()
        For index = 1 To points.Count - 1
            Dim spacing = (points(index).T - points(index - 1).T).TotalSeconds
            If spacing > 0 Then steps.Add(spacing)
        Next
        steps.Sort()
        channel.MedianStepSeconds = If(steps.Count = 0, Math.Max(1.0R, Result.Request.Equipment.IntervalSeconds),
            Math.Max(0.5R, If(steps.Count Mod 2 = 0, steps(steps.Count \ 2 - 1) / 2.0R + steps(steps.Count \ 2) / 2.0R, steps(steps.Count \ 2))))

        Dim metric = channel.Metric
        Dim name = metric.Name.ToLowerInvariant()
        Dim unit = metric.Unit
        Dim unitText = If(unit.Length > 0, " " & unit, "")
        Dim isVoltage = unit = "V" AndAlso Not metric.IsEstimate
        Dim isDc = ElectricalReportKnowledge.IsDc(metric)
        Dim isCurrent = unit = "A" AndAlso Not isDc
        Dim isLoad = Not metric.IsEstimate AndAlso Not isDc AndAlso (unit = "kW" OrElse unit = "kVA" OrElse unit = "kVAr" OrElse unit = "kvar")
        Dim isTemperature = name.Contains("temp") OrElse name.Contains("tdegc") OrElse unit.Contains("C") AndAlso unit.Contains("deg")
        Dim isFrequency = unit = "Hz"
        Dim isPowerFactor = unit = "PF" OrElse name.Contains("power factor")
        Dim stepGap = TimeSpan.FromSeconds(channel.MedianStepSeconds * 3.0R)

        If isVoltage AndAlso channel.Median > 0 Then
            Dim baseline = channel.Median
            AddRunFindings(channel, FindRuns(points, Function(v) (baseline - v) / baseline * 100.0R, 5.0R, stepGap, channel.BreakBefore), If(isDc, "DC voltage", "Voltage"),
                If(isDc, "DC voltage decrease", "Voltage drop"), Function(score) If(isDc, 0, If(score >= 10.0R, 2, 1)),
                Function(run) channel.Metric.Name & " fell to " & Num(points(run.PeakIndex).V) & unitText & " (-" & Num(run.PeakScore) &
                    " % from the typical " & Num(baseline) & unitText & ") at " & Stamp(points(run.PeakIndex).T) & "; below-typical samples span " &
                    RunSpan(points, run, channel) & ".",
                If(isDc, "Compare with charging/discharging state, current sign convention and approved chemistry/cell-count-specific DC limits; no battery fault is established by a median-relative change.",
                    "Verify raw registers, scaling, phase/reference and same-equipment source/mode/alarms. Only investigate an upstream source when the active feeder relationship is verified; no tap or switching cause is established."), baseline)
            AddRunFindings(channel, FindRuns(points, Function(v) (v - baseline) / baseline * 100.0R, 5.0R, stepGap, channel.BreakBefore), If(isDc, "DC voltage", "Voltage"),
                If(isDc, "DC voltage increase", "Voltage rise"), Function(score) If(isDc, 0, If(score >= 10.0R, 2, 1)),
                Function(run) channel.Metric.Name & " rose to " & Num(points(run.PeakIndex).V) & unitText & " (+" & Num(run.PeakScore) &
                    " % above the typical " & Num(baseline) & unitText & ") at " & Stamp(points(run.PeakIndex).T) & "; above-typical samples span " &
                    RunSpan(points, run, channel) & ".",
                If(isDc, "Verify charge state, battery/DC-bus measurement point, scaling and manufacturer DC limits before interpreting the change.",
                    "Verify raw registers, scaling, phase/reference and same-equipment source/mode/alarms. Do not infer capacitor, tap or generation effects without applicable topology and observed state evidence."), baseline)
        ElseIf (isLoad OrElse isCurrent) AndAlso mean > 1.0E-9 AndAlso low.V >= 0 Then
            Dim average = Math.Max(1.0E-9, mean)
            Dim kind = If(isCurrent, "Current", "Load")
            AddRunFindings(channel, FindRuns(points, Function(v) (v - average) / average * 100.0R, 15.0R, stepGap, channel.BreakBefore), kind,
                "High " & kind.ToLowerInvariant() & " episode", Function(score) If(score >= 50.0R, 2, If(score >= 25.0R, 1, 0)),
                Function(run) channel.Metric.Name & " reached " & Num(points(run.PeakIndex).V) & unitText & " (+" & Num(run.PeakScore) &
                    " % above this channel's recording average of " & Num(average) & unitText & ") at " & Stamp(points(run.PeakIndex).T) & "; above-average samples span " &
                    RunSpan(points, run, channel) & ".",
                "This is a deviation from this channel's own recording baseline, not an overload finding. Compare kVA or the UPS-reported load percentage with the equipment rating; use TOTAL (T1-T4) for factory demand.", average)
            Dim stepsFound As New List(Of Integer)()
            For index = 1 To points.Count - 1
                If Not channel.BreakBefore.Contains(points(index).T) AndAlso (points(index).T - points(index - 1).T) > TimeSpan.Zero AndAlso
                    (points(index).T - points(index - 1).T) <= stepGap AndAlso Math.Abs(points(index).V - points(index - 1).V) > 0.2R * average Then stepsFound.Add(index)
            Next
            For Each index In stepsFound.OrderByDescending(Function(item) Math.Abs(points(item).V - points(item - 1).V))
                Dim change = points(index).V - points(index - 1).V
                Dim percent = Math.Abs(change) / average * 100.0R
                channel.Findings.Add(New ReportFinding With {.Severity = If(percent >= 35.0R, 1, 0), .Category = kind, .Metric = channel.Metric.Name,
                    .Title = "Sudden " & kind.ToLowerInvariant() & If(change < 0, " drop", " step"), .StartUtc = points(index - 1).T, .EndUtc = points(index).T,
                    .EvidenceValue = points(index).V, .EvidenceUnit = unit, .ReferenceValue = points(index - 1).V,
                    .DeviationPercent = If(Math.Abs(points(index - 1).V) > 1.0E-9, CType(change / Math.Abs(points(index - 1).V) * 100.0R, Double?), Nothing), .PeakUtc = points(index).T,
                    .Detail = channel.Metric.Name & " changed by " & If(change >= 0, "+", "") & Num(change) & unitText & " (" & Num(percent) &
                        " % of average) between " & Stamp(points(index - 1).T) & " and " & Stamp(points(index).T) & ".",
                    .Advice = "Verify source quality and aligned load/mode/alarms; investigate a switching or demand change only when supported by corroborating evidence."})
            Next
        ElseIf isTemperature Then
            Dim peakValue = peak.V
            If peakValue >= 80.0R AndAlso Not isDc Then
                channel.Findings.Add(New ReportFinding With {.Severity = If(peakValue >= 100.0R, 2, 1), .Category = "Temperature", .Metric = channel.Metric.Name,
                    .Title = "High temperature", .StartUtc = peak.T, .EndUtc = peak.T,
                    .EvidenceValue = peakValue, .EvidenceUnit = unit, .ReferenceValue = 80.0R, .PeakUtc = peak.T, .EvidenceLabel = "Peak / guide",
                    .Detail = channel.Metric.Name & " peaked at " & Num(peakValue) & unitText & " at " & Stamp(peak.T) &
                        " (guide values: 80 attention / 100 critical; no site limit is configured).",
                    .Advice = "Verify sensor location/type and approved limits; then assess the applicable cooling system and aligned load. Do not assume an oil-filled transformer."})
            End If
            Dim best = 0.0R
            Dim bestIndex = -1
            Dim bestOrigin = -1
            Dim origin = 0
            For index = 0 To points.Count - 1
                If index > 0 AndAlso ((points(index).T - points(index - 1).T) > stepGap OrElse
                    channel.BreakBefore.Contains(points(index).T)) Then origin = index
                While origin < index AndAlso (points(index).T - points(origin).T).TotalSeconds > 120.0R
                    origin += 1
                End While
                Dim minutes = (points(index).T - points(origin).T).TotalMinutes
                If minutes >= 0.5R AndAlso (points(index).V - points(origin).V) / minutes > best Then
                    best = (points(index).V - points(origin).V) / minutes
                    bestIndex = index
                    bestOrigin = origin
                End If
            Next
            If best >= 3.0R AndAlso bestIndex >= 0 Then
                channel.Findings.Add(New ReportFinding With {.Severity = 1, .Category = "Temperature", .Metric = channel.Metric.Name,
                    .Title = "Fast temperature rise", .StartUtc = points(bestOrigin).T, .EndUtc = points(bestIndex).T,
                    .EvidenceValue = best, .EvidenceUnit = "deg C/min", .ReferenceValue = 3.0R, .PeakUtc = points(bestIndex).T,
                    .Detail = channel.Metric.Name & " rose at up to " & Num(best) & " deg C per minute between saved readings at " &
                        Stamp(points(bestOrigin).T) & " and " & Stamp(points(bestIndex).T) & ", reaching " & Num(points(bestIndex).V) & unitText & ".",
                    .Advice = "Verify sensor identity and scaling before evaluating loading or cooling; no physical cause is established."})
            End If
            If channel.TrendDelta >= 5.0R Then
                channel.Findings.Add(New ReportFinding With {.Severity = 1, .Category = "Temperature", .Metric = channel.Metric.Name,
                    .Title = "Temperature trending up", .StartUtc = points(0).T, .EndUtc = points(points.Count - 1).T,
                    .EvidenceValue = channel.TrendDelta, .EvidenceUnit = unit, .ReferenceValue = 5.0R, .PeakUtc = points(points.Count - 1).T, .EvidenceLabel = "Last-third vs first-third mean",
                    .Detail = channel.Metric.Name & " last-third sample mean was " & Num(channel.TrendDelta) & unitText & " above its first-third sample mean.",
                    .Advice = "Watch the trend; a steady rise at constant load points to reduced cooling."})
            End If
        ElseIf isFrequency Then
            Dim nominal = If(channel.Median >= 55.0R, 60.0R, 50.0R)
            AddRunFindings(channel, FindRuns(points, Function(v) Math.Abs(v - nominal), 0.5R, stepGap, channel.BreakBefore), "Frequency",
                "Frequency deviation", Function(score) If(score >= 1.0R, 2, 1),
                Function(run) channel.Metric.Name & " reached " & Num(points(run.PeakIndex).V) & unitText & " (" & Num(run.PeakScore) & " Hz away from " &
                    Num(nominal) & " Hz inferred screening reference, not verified nominal) at " & Stamp(points(run.PeakIndex).T) & "; deviating samples span " & RunSpan(points, run, channel) & ".",
                "Verify the approved nominal frequency and tolerance, measurement mapping and actual source/mode. A frequency change alone does not prove generator operation or a weak grid.", nominal)
        ElseIf isPowerFactor Then
            Dim invalidPf = points.Where(Function(point) Math.Abs(point.V) > 1).ToArray()
            If invalidPf.Length > 0 Then
                channel.Findings.Add(New ReportFinding With {.Severity = 1, .Category = "Data quality", .Metric = metric.Name,
                    .Title = "PF outside physical range", .StartUtc = invalidPf.First().T, .EndUtc = invalidPf.Last().T,
                    .EvidenceValue = invalidPf(0).V, .EvidenceUnit = "PF", .PeakUtc = invalidPf(0).T,
                    .Detail = invalidPf.Length & " saved PF value(s) have magnitude above 1. They remain visible as source evidence but cannot support PF, efficiency or power-consistency conclusions.",
                    .Advice = "Verify raw scaling, PF units, sign convention and measured versus derived basis. Do not clamp impossible PF into a valid-looking value."})
            End If
            AddRunFindings(channel, FindRuns(points, Function(v) If(v <= 0.0R, -1.0R, 0.85R - v), 0.0R, stepGap, channel.BreakBefore), "Power factor",
                "Low power factor", Function(score) If(0.85R - score <= 0.80R, 2, 1),
                Function(run) channel.Metric.Name & " dropped to " & points(run.PeakIndex).V.ToString("0.00", Inv) & " at " & Stamp(points(run.PeakIndex).T) &
                    "; low-PF samples span " & RunSpan(points, run, channel) & ".",
                "Verify true versus displacement PF, input/output node, phase/system basis, load/mode and harmonic evidence. Do not infer capacitor failure or prescribe compensation from UPS PF alone.", 0.85R)
        End If

        If Not metric.IsEstimate Then AddCapacityFinding(channel)

        Dim gapLimit = Math.Max(5.0R, channel.MedianStepSeconds * 3.0R)
        For index = 1 To points.Count - 1
            If (points(index).T - points(index - 1).T).TotalSeconds > gapLimit Then channel.Gaps.Add(New ReportPoint(points(index - 1).T, (points(index).T - points(index - 1).T).TotalSeconds))
        Next
        If channel.Gaps.Count > 0 Then
            Dim longest = channel.Gaps.OrderByDescending(Function(gap) gap.V).First()
            Dim longestEnd = longest.T.AddSeconds(longest.V)
            channel.Findings.Add(New ReportFinding With {.Severity = 1, .Category = "Data quality", .Metric = channel.Metric.Name,
                .Title = "Recording gaps", .StartUtc = longest.T, .EndUtc = longestEnd,
                .EvidenceValue = longest.V, .EvidenceUnit = "s", .ReferenceValue = gapLimit, .EvidenceLabel = "Longest sample gap",
                .Detail = channel.Metric.Name & " has " & channel.Gaps.Count.ToString(Inv) & " gap(s); the longest is " & Span(TimeSpan.FromSeconds(longest.V)) &
                    " (" & Stamp(longest.T) & " to " & Stamp(longestEnd) & "). These are gaps in the loaded series; requested thinning may hide additional evidence. No interpolation is performed.",
                .Advice = "Review the loaded resolution and raw history, then check whether the recorder and PLC link were running during the gap."})
        End If
        Dim observedSeconds = 0.0R
        For index = 1 To points.Count - 1
            Dim spacing = (points(index).T - points(index - 1).T).TotalSeconds
            If spacing > 0 AndAlso spacing <= gapLimit AndAlso Not channel.BreakBefore.Contains(points(index).T) Then
                observedSeconds += Math.Max(0.0R, (If(points(index).T < Result.Request.ToUtc, points(index).T, Result.Request.ToUtc) -
                    If(points(index - 1).T > Result.Request.FromUtc, points(index - 1).T, Result.Request.FromUtc)).TotalSeconds)
            End If
        Next
        channel.ObservedCoveragePercent = Math.Min(100.0R, observedSeconds / Math.Max(0.001R, (Result.Request.ToUtc - Result.Request.FromUtc).TotalSeconds) * 100.0R)

        Dim stats = channel.Stats
        channel.Summary = metric.Name & " averaged " & Num(mean) & unitText & " (lowest " & Num(low.V) & " at " & Stamp(low.T) &
            ", highest " & Num(peak.V) & " at " & Stamp(peak.T) & ")"
        If (isLoad OrElse isCurrent) AndAlso mean > 0 AndAlso low.V >= 0 AndAlso peak.V > 0 Then channel.Summary &= "; recording average/peak ratio " & Num(mean / peak.V * 100.0R) & " %"
        If (isLoad OrElse unit = "A") AndAlso low.V < 0 Then channel.Summary &= "; signed values are retained; verify import/export or DC current direction before interpreting loading"
        If isLoad OrElse isCurrent OrElse isTemperature Then channel.Summary &= "; 95 % of valid samples were at or below " & Num(channel.P95) & unitText
        If isVoltage AndAlso channel.Median > 0 Then
            Dim outside = points.Where(Function(point) Math.Abs(point.V - channel.Median) / channel.Median > 0.05R).Count() * 100.0R / points.Count
            channel.Summary &= If(outside > 0, "; " & Num(outside) & " % of valid samples outside +/-5 % of typical", "; all valid samples within +/-5 % of typical")
        End If
        channel.Summary &= "; " & If(Math.Abs(channel.TrendDelta) < Math.Max(1.0E-9, 0.01R * Math.Abs(mean)), "first/last-third means are similar",
            If(channel.TrendDelta > 0, "last-third mean higher by ", "last-third mean lower by ") & Num(Math.Abs(channel.TrendDelta)) & unitText & " than first-third mean") & "."
        If stats IsNot Nothing AndAlso stats.EnergyKwh.HasValue AndAlso metric.IsPower Then channel.Summary &= " Estimated energy " & Num(stats.EnergyKwh.Value) & " kWh."
        If isTemperature Then channel.Summary &= " Sensor location/type and site limits are not verified here; do not assume winding/hot-spot temperature. Generic temperature guides are screening only."
        If isDc Then channel.Summary &= " Battery/DC evidence: verify chemistry, cell count, operating state and current sign convention. AC limits, tap-position diagnosis and AC overload rules are not applicable."
        If isPowerFactor AndAlso points.Any(Function(point) point.V <= 0) Then
            channel.Summary &= " Nonpositive PF values remain in statistics but are not classified as low positive PF; verify no-load state, sign convention and calculation basis."
        End If
        If metric.IsEstimate Then
            channel.Summary = metric.Name & " estimate averaged " & Num(mean) & unitText & " (minimum " & Num(low.V) &
                ", maximum " & Num(peak.V) & unitText & "). " & metric.Basis &
                " Descriptive statistics only; no measured phase-voltage, overload, loss or efficiency conclusion is drawn from this estimate."
        End If
        For Each finding In channel.Findings
            finding.EquipmentId = ElectricalReportKnowledge.EquipmentIdentity(metric, Result.Request.Equipment.Id)
        Next
        Findings.AddRange(channel.Findings)
    End Sub

    Private Sub AddCapacityFinding(channel As ChannelAnalysis)
        Dim metric = channel.Metric
        If ElectricalReportKnowledge.IsDc(metric) OrElse {"Input", "Bypass"}.Contains(ElectricalReportKnowledge.MeasurementSide(metric)) Then Return
        Dim isUpsPercent = metric.Unit = "%" AndAlso metric.Name.IndexOf("Output Load", StringComparison.OrdinalIgnoreCase) >= 0
        If metric.Unit <> "kVA" AndAlso Not isUpsPercent Then Return

        Dim peak = channel.Points.OrderByDescending(Function(point) point.V).First()
        Dim percentage As Double
        Dim ratingText As String
        If isUpsPercent Then
            percentage = peak.V
            ratingText = "the UPS card's reported load percentage"
        Else
            If Not metric.RatedCapacityKva.HasValue OrElse Not Double.IsFinite(metric.RatedCapacityKva.Value) OrElse metric.RatedCapacityKva.Value <= 0 Then Return
            If metric.Basis.IndexOf("scaling must be verified", StringComparison.OrdinalIgnoreCase) >= 0 Then Return
            percentage = peak.V / metric.RatedCapacityKva.Value * 100.0R
            ratingText = "the configured " & Num(metric.RatedCapacityKva.Value) & " kVA rating"
        End If
        If Not Double.IsFinite(percentage) OrElse percentage < 90.0R Then Return

        Dim severity = If(percentage >= 100.0R, 2, 1)
        Dim detail = If(isUpsPercent,
            metric.Name & " peaked at " & Num(peak.V) & " % of rated output at " & Stamp(peak.T) & " (" & ratingText & ").",
            metric.Name & " peaked at " & Num(peak.V) & " kVA at " & Stamp(peak.T) & ", equal to " & Num(percentage) & " % of " & ratingText & ".")
        If metric.CapacityBasis.Length > 0 Then detail &= " Rating source: " & metric.CapacityBasis & "."
        channel.Findings.Add(New ReportFinding With {
            .Severity = severity,
            .Category = "Capacity",
            .Metric = metric.Name,
            .Title = If(severity >= 2, "Rated capacity reached or exceeded", "Near rated capacity"),
            .StartUtc = peak.T,
            .EndUtc = peak.T,
            .EvidenceValue = peak.V, .EvidenceUnit = metric.Unit, .ReferenceValue = If(isUpsPercent, CType(100.0R, Double?), metric.RatedCapacityKva),
            .DeviationPercent = percentage - 100.0R, .PeakUtc = peak.T, .EvidenceLabel = "Peak / rated capacity",
            .Detail = detail,
            .Advice = "Verify the configured nameplate rating and measurement basis. A UPS output is not factory demand; use the recorded TOTAL (T1-T4) series for the factory aggregate."
        })
    End Sub

    Private Shared Function FindRuns(points As List(Of ReportPoint), score As Func(Of Double, Double), threshold As Double, maxGap As TimeSpan,
                                    breaks As HashSet(Of DateTime)) As List(Of Run)
        Dim runs As New List(Of Run)()
        Dim open = False
        Dim current As Run
        For index = 0 To points.Count - 1
            Dim value = score(points(index).V)
            Dim breach = value > threshold
            If open AndAlso index > 0 AndAlso ((points(index).T - points(index - 1).T) > maxGap OrElse
                points(index).T <= points(index - 1).T OrElse breaks.Contains(points(index).T)) Then
                runs.Add(current)
                open = False
            End If
            If breach Then
                If Not open Then
                    current = New Run With {.StartIndex = index, .EndIndex = index, .PeakIndex = index, .PeakScore = value}
                    open = True
                Else
                    current.EndIndex = index
                    If value > current.PeakScore Then
                        current.PeakScore = value
                        current.PeakIndex = index
                    End If
                End If
            ElseIf open Then
                runs.Add(current)
                open = False
            End If
        Next
        If open Then runs.Add(current)
        Return runs
    End Function

    Private Shared Function RunSpan(points As List(Of ReportPoint), run As Run, channel As ChannelAnalysis) As String
        Dim length = points(run.EndIndex).T - points(run.StartIndex).T
        If run.StartIndex = run.EndIndex Then Return "one saved sample; duration unknown"
        Return Span(length)
    End Function

    Private Sub AddRunFindings(channel As ChannelAnalysis, runs As List(Of Run), category As String, title As String,
                               severityOf As Func(Of Double, Integer), describe As Func(Of Run, String), advice As String, Optional reference As Double? = Nothing)
        Dim ranked = runs.OrderByDescending(Function(run) run.PeakScore).ToList()
        For Each run In ranked
            Dim severity = severityOf(run.PeakScore)
            channel.Findings.Add(New ReportFinding With {.Severity = severity, .Category = category, .Metric = channel.Metric.Name, .Title = title,
                .StartUtc = channel.Points(run.StartIndex).T, .EndUtc = channel.Points(run.EndIndex).T,
                .EvidenceValue = channel.Points(run.PeakIndex).V, .EvidenceUnit = channel.Metric.Unit, .ReferenceValue = reference, .PeakUtc = channel.Points(run.PeakIndex).T,
                .EvidenceLabel = If(category = "Voltage", "Period median", "Rule baseline / guide"),
                .DeviationPercent = If(reference.HasValue AndAlso Math.Abs(reference.GetValueOrDefault()) > 1.0E-9,
                    CType((channel.Points(run.PeakIndex).V - reference.GetValueOrDefault()) / Math.Abs(reference.GetValueOrDefault()) * 100.0R, Double?), Nothing),
                .Detail = describe(run), .Advice = advice})
        Next
    End Sub

    Private Sub CrossChannelChecks()
        Balance("Voltage L[123]-N$", "Voltage", "Voltage balance", 2.0R, 5.0R, "V", "Check single-phase loading and the supply on the phase with the lowest voltage.")
        Balance("Current L[123]$", "Current", "Current balance", 10.0R, 20.0R, "A", "Redistribute single-phase loads and check for a failing phase.")
        For Each neutral In Channels.Where(Function(item) item.Metric.Unit = "A" AndAlso item.Metric.Name.Contains("neutral", StringComparison.OrdinalIgnoreCase))
            Dim equipment = If(neutral.Metric.EquipmentId.Length > 0, neutral.Metric.EquipmentId, Prefix(neutral.Metric.Name))
            If equipment.Length = 0 Then Continue For
            Dim phases = Channels.Where(Function(item) If(item.Metric.EquipmentId.Length > 0, item.Metric.EquipmentId, Prefix(item.Metric.Name)) = equipment AndAlso
                item.Metric.Unit = "A" AndAlso Regex.IsMatch(item.Metric.Name, "\|\s*Current L[123]$")).ToArray()
            If phases.Length <> 3 OrElse phases.Select(Function(item) Regex.Match(item.Metric.Name, "L[123]$").Value).Distinct().Count() <> 3 Then Continue For
            Dim phaseTimes = phases.Select(Function(item) item.Points.GroupBy(Function(point) point.T).
                Where(Function(group) group.Count() = 1).ToDictionary(Function(group) group.Key, Function(group) group.Single().V)).ToArray()
            Dim highestRatio = 0.0R
            Dim worstPoint As ReportPoint
            Dim worstAverage = 0.0R
            Dim compared = 0
            For Each acquisition In neutral.Points.GroupBy(Function(point) point.T).Where(Function(part) part.Count() = 1)
                Dim point = acquisition.Single()
                If point.V < 0 OrElse Not phaseTimes.All(Function(times) times.ContainsKey(point.T) AndAlso times(point.T) >= 0) Then Continue For
                Dim average = phaseTimes.Average(Function(times) times(point.T))
                If average <= 1.0R Then Continue For
                compared += 1
                Dim ratio = point.V / average
                If ratio <= highestRatio Then Continue For
                highestRatio = ratio
                worstPoint = point
                worstAverage = average
            Next
            If highestRatio > 0.2R Then
                AddFinding(New ReportFinding With {.Severity = 1, .Category = "Current", .Metric = neutral.Metric.Name,
                    .Title = "High neutral current", .StartUtc = worstPoint.T, .EndUtc = worstPoint.T, .PeakUtc = worstPoint.T,
                    .EvidenceValue = worstPoint.V, .EvidenceUnit = "A", .ReferenceValue = 0.2R * worstAverage,
                    .DeviationPercent = (highestRatio / 0.2R - 1) * 100.0R, .EvidenceLabel = "Neutral / guide (20% of simultaneous phase mean)",
                    .Detail = equipment & ": neutral current " & Num(worstPoint.V) & " A at " & Stamp(worstPoint.T) & " was " &
                        Num(highestRatio * 100.0R) & " % of the simultaneously measured three-phase average " & Num(worstAverage) &
                        " A; " & compared.ToString(Inv) & " exact matched acquisitions checked. The 20 % ratio is a screening guide, not neutral ampacity or harmonic proof.",
                    .Advice = "Verify the neutral sensor and conductor rating, then check phase loading and measured THD/harmonic evidence. Different-time peaks and period averages are not compared."}, neutral)
            End If
        Next
    End Sub

    Private Sub AddFinding(finding As ReportFinding, owner As ChannelAnalysis)
        finding.EquipmentId = ElectricalReportKnowledge.EquipmentIdentity(owner.Metric, Result.Request.Equipment.Id)
        owner.Findings.Add(finding)
        Findings.Add(finding)
    End Sub

    Private Sub Balance(pattern As String, quantity As String, title As String, warnPercent As Double, criticalPercent As Double, unit As String, advice As String)
        Dim groups = Channels.Where(Function(item) Not item.Metric.IsEstimate).
            Select(Function(item) New With {.Item = item, .Match = Regex.Match(item.Metric.Name, pattern)}).Where(Function(entry) entry.Match.Success).
            GroupBy(Function(entry) entry.Item.Metric.Name.Substring(0, entry.Match.Index)).ToList()
        For Each part In groups
            Dim prefix = part.Key.Trim().TrimEnd("-"c, " "c, ChrW(183), "|"c).Trim()
            BalanceGroup(part.Select(Function(entry) entry.Item).ToList(), quantity, If(prefix.Length > 0, prefix & " - ", "") & title, warnPercent, criticalPercent, unit, advice)
        Next
    End Sub

    Private Sub BalanceGroup(group As List(Of ChannelAnalysis), quantity As String, title As String, warnPercent As Double, criticalPercent As Double, unit As String, advice As String)
        If group.Count <> 3 Then Return
        If group.Any(Function(item) Not ElectricalReportKnowledge.SameEquipment(item.Metric, group(0).Metric, Result.Request.Equipment.Id)) Then Return
        If group.Select(Function(item) Regex.Match(item.Metric.Name, "L[123](?:-N)?$").Value).Distinct().Count() <> 3 Then Return
        Dim byTime = New Dictionary(Of Long, List(Of Double))()
        For Each item In group
            For Each acquisition In item.Points.GroupBy(Function(point) point.T.Ticks).Where(Function(part) part.Count() = 1)
                Dim point = acquisition.Single()
                Dim list As List(Of Double) = Nothing
                If Not byTime.TryGetValue(point.T.Ticks, list) Then
                    list = New List(Of Double)()
                    byTime(point.T.Ticks) = list
                End If
                list.Add(point.V)
            Next
        Next
        Dim worst = 0.0R
        Dim worstTicks As Long? = Nothing
        Dim compared = 0
        Dim above = 0
        For Each pair In byTime
            If pair.Value.Count <> group.Count Then Continue For
            Dim average = pair.Value.Average()
            If average < If(unit = "A", 1.0R, 10.0R) Then Continue For
            compared += 1
            Dim unbalance = pair.Value.Max(Function(value) Math.Abs(value - average)) / average * 100.0R
            If unbalance > warnPercent Then above += 1
            If Not worstTicks.HasValue OrElse unbalance > worst Then
                worst = unbalance
                worstTicks = pair.Key
            End If
        Next
        If compared = 0 Then Return
        Dim at = New DateTime(worstTicks.Value, DateTimeKind.Utc)
        Dim finding As New ReportFinding With {.EquipmentId = ElectricalReportKnowledge.EquipmentIdentity(group(0).Metric, Result.Request.Equipment.Id),
            .Category = quantity, .Metric = String.Join(" / ", group.Select(Function(item) item.Metric.Name)), .Title = title,
            .StartUtc = at, .EndUtc = at, .Advice = advice, .EvidenceValue = worst, .EvidenceUnit = "%", .ReferenceValue = warnPercent,
            .PeakUtc = at, .EvidenceLabel = "Max phase deviation / warning guide"}
        If worst > warnPercent Then
            finding.Severity = If(worst > criticalPercent, 2, 1)
            finding.Detail = "Phase unbalance reached " & Num(worst) & " % at " & Stamp(at) & "; it was above " & Num(warnPercent) & " % in " &
                Num(above * 100.0R / compared) & " % of the compared samples."
        Else
            finding.Severity = 0
            finding.Title = title & " is good"
            finding.Detail = "Maximum phase deviation / phase mean was within " & Num(warnPercent) & " % in all " & compared.ToString(Inv) & " matched three-phase samples (worst " & Num(worst) & " %)."
            finding.StartUtc = Nothing
            finding.EndUtc = Nothing
        End If
        Findings.Add(finding)
    End Sub

    Private Sub ReadEvents()
        Dim source = Result.AlarmEvents.Concat(Result.Rows.Where(Function(row) row.EventKind.Length > 0)).
            GroupBy(Function(row) New With {Key row.EquipmentId, Key row.ObservationId, Key row.TimestampUtc, Key row.AcquiredUtc,
                Key row.ConditionKey, Key row.ConditionName, Key row.EventKind, Key row.EventState, Key row.EventSeverity,
                Key row.EventCategory, Key row.EventAcknowledged, Key row.EventDetails}).Select(Function(group) group.First()).ToList()
        For Each group In source.GroupBy(Function(row) New With {Key .Equipment = If(row.EquipmentId.Length > 0, row.EquipmentId, Result.Request.Equipment.Id),
            Key .Condition = If(row.ConditionKey.Length > 0, row.ConditionKey, row.ConditionName)})
            Dim ordered = group.OrderBy(Function(row) row.TimestampUtc).ToList()
            Dim active = ordered.Where(Function(row) row.EventState.Equals("Active", StringComparison.OrdinalIgnoreCase)).ToArray()
            Dim text = (String.Join(" ", active.Select(Function(row) row.EventSeverity & " " & row.EventCategory))).ToLowerInvariant()
            Dim item As New EventGroup With {.EquipmentId = group.Key.Equipment,
                .Name = group.Key.Equipment & " | " & If(ordered(0).ConditionName.Length > 0, ordered(0).ConditionName, group.Key.Condition),
                .Count = ordered.Count, .ActiveCount = active.Length, .FirstUtc = ordered(0).TimestampUtc, .LastUtc = ordered(ordered.Count - 1).TimestampUtc,
                .LastState = ordered(ordered.Count - 1).EventState,
                .Severity = If(active.Length = 0, 0, If(text.Contains("fault") OrElse text.Contains("critical") OrElse text.Contains("trip"), 2, 1))}
            item.Times.AddRange(ordered.Select(Function(row) row.TimestampUtc))
            item.ActiveTimes.AddRange(active.Select(Function(row) row.TimestampUtc))
            Events.Add(item)
        Next
        For Each item In Events.OrderByDescending(Function(entry) entry.Severity).ThenByDescending(Function(entry) entry.Count)
            Findings.Add(New ReportFinding With {.Severity = item.Severity, .Category = "Alarm events", .Metric = item.Name, .Title = "Alarm / fault observed",
                .StartUtc = item.FirstUtc, .EndUtc = item.LastUtc,
                .Detail = item.Name & " was observed " & item.Count.ToString(Inv) & " time(s) between " & Stamp(item.FirstUtc) & " and " & Stamp(item.LastUtc) &
                    "; " & item.ActiveCount.ToString(Inv) & " active observation(s); last observed state: " & If(item.LastState.Length > 0, item.LastState, "unknown") &
                    ". Cleared/inactive/unknown-only observations do not prove an active alarm. Reminders are not incidents; state after the last observation is unknown.",
                .Advice = "Open the alarm history for the condition and confirm the cause on site."})
        Next
    End Sub

    Private Shared Function Prefix(name As String) As String
        Dim index = name.LastIndexOf("|"c)
        Return If(index < 0, String.Empty, name.Substring(0, index).Trim())
    End Function

    Private Shared Function AverageBetween(channel As ChannelAnalysis, from As DateTime, until As DateTime) As Double?
        Dim total = 0.0R
        Dim count = 0
        For Each point In channel.Points
            If point.T >= from AndAlso point.T <= until Then
                total += point.V
                count += 1
            End If
        Next
        Return If(count = 0, CType(Nothing, Double?), total / count)
    End Function

    ' Links findings that happen at the same time on different channels, so the report can say what probably caused what.
    Private Sub Correlate()
        Dim loads = Channels.Where(Function(item) Not item.Metric.IsEstimate AndAlso Not ElectricalReportKnowledge.IsDc(item.Metric) AndAlso
            (item.Metric.Unit = "kW" OrElse item.Metric.Unit = "kVA" OrElse item.Metric.Unit = "A")).
            OrderBy(Function(item) If(item.Metric.Unit = "kW", 0, If(item.Metric.Unit = "kVA", 1, 2))).ToList()
        If loads.Count = 0 Then Return
        Dim hints As New List(Of ReportFinding)()
        For Each channel In Channels
            For Each finding In channel.Findings
                If Not finding.StartUtc.HasValue OrElse Not finding.EndUtc.HasValue Then Continue For
                Dim start = finding.StartUtc.Value
                Dim finish = finding.EndUtc.Value
                If finding.Title = "Voltage drop" Then
                    Dim best As ChannelAnalysis = Nothing
                    Dim bestRise = Double.MinValue
                    Dim bestBefore = 0.0R
                    Dim bestDuring = 0.0R
                    For Each load In loads.Where(Function(item) ElectricalReportKnowledge.SameEquipment(item.Metric, channel.Metric, Result.Request.Equipment.Id))
                        Dim before = AverageBetween(load, start.AddSeconds(-120), start.AddSeconds(-10))
                        Dim during = AverageBetween(load, start, finish)
                        If before.HasValue AndAlso during.HasValue AndAlso Math.Abs(before.Value) > 1.0E-9 Then
                            Dim rise = (during.Value - before.Value) / Math.Abs(before.Value) * 100.0R
                            If rise > bestRise Then
                                bestRise = rise
                                best = load
                                bestBefore = before.Value
                                bestDuring = during.Value
                            End If
                        End If
                    Next
                    If best Is Nothing Then Continue For
                    Dim unit = If(best.Metric.Unit.Length > 0, " " & best.Metric.Unit, "")
                    Dim hint As New ReportFinding With {.Severity = 0, .Category = "Correlation", .Metric = channel.Metric.Name, .Title = "Observed association: " & channel.Metric.Name & " drop",
                        .StartUtc = start, .EndUtc = finish}
                    If bestRise >= 10.0R Then
                        hint.Detail = "For the saved deviation of " & channel.Metric.Name & " (" & Stamp(start) & "), " & best.Metric.Name &
                            " had a " & Num(bestRise) & " % increase in sample mean (" & Num(bestBefore) & unit &
                            " from 120 to 10 seconds before, " & Num(bestDuring) & unit &
                            " within the observed abnormal span). This is the largest eligible load-channel change, not proof of load-induced voltage drop or an exact simultaneous acquisition."
                        hint.Advice = "Verify timestamps, sample density, raw scaling and alarms before investigating switching or supply impedance."
                    Else
                        hint.Detail = "The drop of " & channel.Metric.Name & " (" & Stamp(start) & ") happened without any matching rise in " & best.Metric.Name &
                            " (" & Num(bestBefore) & unit & " mean from 120 to 10 seconds before, " & Num(bestDuring) & unit &
                            " within the observed span). This does not distinguish supply-side causes from sensor, timing or unmeasured load effects."
                        hint.Advice = "Verify raw voltage and load evidence, then compare upstream equipment and recorded alarms; no cause is established."
                    End If
                    hints.Add(hint)
                ElseIf finding.Title = "High temperature" OrElse finding.Title = "Fast temperature rise" Then
                    Dim sameSource = loads.Where(Function(item) ElectricalReportKnowledge.SameEquipment(item.Metric, channel.Metric, Result.Request.Equipment.Id)).ToList()
                    If sameSource.Count = 0 Then Continue For
                    Dim load = sameSource(0)
                    Dim recent = AverageBetween(load, finish.AddMinutes(-10), finish)
                    Dim overall = load.Points.Average(Function(point) point.V)
                    If Not recent.HasValue OrElse Math.Abs(overall) < 1.0E-9 Then Continue For
                    Dim ratio = recent.Value / overall
                    Dim unit = If(load.Metric.Unit.Length > 0, " " & load.Metric.Unit, "")
                    Dim hint As New ReportFinding With {.Severity = 0, .Category = "Correlation", .Metric = channel.Metric.Name, .Title = "Observed association: " & channel.Metric.Name & " temperature",
                        .StartUtc = start, .EndUtc = finish}
                    Dim facts = "In the 10 minutes before " & Stamp(finish) & " " & load.Metric.Name & " averaged " & Num(recent.Value) & unit & " (period average " & Num(overall) & unit & "). "
                    If ratio >= 1.1R Then
                        hint.Detail = facts & "The selected load mean was above its period mean. This association does not establish a thermal cause, temperature lag or overload."
                        hint.Advice = "Verify temperature sensor location and time alignment, then compare with verified ratings and site thermal limits."
                    ElseIf ratio <= 0.95R Then
                        hint.Detail = facts & "The selected load mean was not elevated. Earlier loading, ambient conditions, unmeasured channels and sensor effects remain unknown; a cooling fault cannot be inferred."
                        hint.Advice = "Verify sensor identity, ambient conditions and a longer loading history before evaluating cooling."
                    Else
                        hint.Detail = facts & "The selected load mean was close to its period mean. These readings do not establish cooling margin or a physical fault."
                        hint.Advice = "Check verified temperature limits and sensor provenance; use aligned trends to investigate timing."
                    End If
                    hints.Add(hint)
                End If
            Next
        Next
        Findings.AddRange(hints)
    End Sub

    Private Sub Finish()
        Completeness = If(TotalSamples = 0, 0.0R, (TotalSamples - MissingSamples) * 100.0R / TotalSamples)
        If Channels.Count > 0 AndAlso ObservedCoveragePercent < 90.0R Then
            Findings.Add(New ReportFinding With {.Severity = 1, .Category = "Data quality", .Metric = "Selected channels", .Title = "Limited time coverage",
                .Detail = "Average observed continuity covers " & Num(ObservedCoveragePercent) &
                    " % of the requested period. Saved-sample validity is a separate measure; missing intervals and unobserved window edges cannot establish normal operation.",
                .Advice = "Review the requested date range, recording cadence and missing intervals before drawing conclusions about the whole period."})
        End If
        Dim gapGroups = Findings.Where(Function(item) item.Title = "Recording gaps" AndAlso item.StartUtc.HasValue).
            GroupBy(Function(item) New With {Key .Equipment = item.EquipmentId, Key .Start = item.StartUtc, Key .Finish = item.EndUtc}).ToList()
        For Each group In gapGroups
            If group.Count() < 2 Then Continue For
            Dim keep = group.First()
            keep.Metric = keep.EquipmentId & " | Several channels"
            keep.Detail &= " The same gap affects " & (group.Count() - 1).ToString(Inv) & " other channel(s)."
            For Each extra In group.Skip(1).ToList()
                Findings.Remove(extra)
            Next
        Next
        Dim request = Result.Request
        Dim critical = Findings.Where(Function(item) item.Severity >= 2).Count()
        Dim warnings = Findings.Where(Function(item) item.Severity = 1).Count()
        Dim text = New System.Text.StringBuilder()
        text.Append("This report analyses ").Append(Channels.Count.ToString(Inv)).Append(" numeric channel(s) of ").Append(StudyName).Append(" from ").
            Append(Stamp(request.FromUtc)).Append(" to ").Append(Stamp(request.ToUtc)).Append(" (").Append(Span(request.ToUtc - request.FromUtc)).Append("). ")
        If Channels.Count > 0 AndAlso Channels.All(Function(channel) channel.Metric.IsEstimate) Then
            text.Append("Only assumption-based estimates were selected; no measured electrical-condition assessment is established. ")
        ElseIf critical + warnings = 0 Then
            text.Append("No abnormal condition was detected in the saved data. ")
        Else
            text.Append(critical.ToString(Inv)).Append(" critical and ").Append(warnings.ToString(Inv)).Append(" warning finding(s) need attention. ")
        End If
        text.Append("Saved-sample validity is ").Append(Num(Completeness)).Append(" %; average observed time coverage is ").
            Append(Num(ObservedCoveragePercent)).Append(" %. Findings apply only to recorded observations, not the entire requested period. ").
            Append("Numerical extremes, references and timestamps are listed in the event evidence register; each channel has its own charts and statistics.")
        text.Append(" Unselected channels and alarms are not assessed. Finding counts are channel/rule flags, not independent incidents or confirmed equipment faults.")
        If Channels.Any(Function(channel) channel.Metric.IsEstimate) Then text.Append(" Estimated channels are calculated values, not direct measurements: the L-L method assumes balance, while measured L-N kVA uses matched phase V x I. They are excluded from efficiency, losses and compliance conclusions.")
        If UpsPowerBalances.Count > 0 Then text.Append(" Per-device measured power balance and missing calculation prerequisites are explained in the UPS evidence and conclusion sections; no cross-equipment loss/efficiency ranking is inferred.")
        If request.ThinSeconds > 0 Then text.Append(" Loaded thinning: ").Append(request.ThinSeconds).Append(" s; events and extrema between retained readings may be absent.")
        Summary = text.ToString().Trim()
        For Each finding In Findings
            If finding.Cause.Length = 0 Then
                Dim metric = Result.Request.Metrics.FirstOrDefault(Function(item) item.Name = finding.Metric)
                If metric Is Nothing Then metric = New HistoryMetric With {.Name = finding.Metric}
                finding.Cause = ElectricalReportKnowledge.Investigation(finding.Title, metric)
            End If
            If finding.Severity >= 1 AndAlso finding.StartUtc.HasValue AndAlso finding.EndUtc.HasValue AndAlso finding.Category <> "Alarm events" Then
                Dim from = finding.StartUtc.Value.AddMinutes(-5)
                Dim until = finding.EndUtc.Value.AddMinutes(5)
                Dim owner = If(finding.EquipmentId.Length > 0, finding.EquipmentId, Prefix(finding.Metric))
                If owner.Length = 0 AndAlso Not Result.Request.IsCombined Then owner = Result.Request.Equipment.Id
                Dim near = Events.Where(Function(item) item.EquipmentId = owner AndAlso
                    item.ActiveTimes.Any(Function(time) time >= from AndAlso time <= until)).Select(Function(item) item.Name).ToList()
                If near.Count > 0 Then finding.Detail &= " Same-equipment active alarm observation within 5 minutes: " & String.Join(", ", near) & ". Timing is not proof of cause."
            End If
        Next
        Dim criticalCount = Findings.Where(Function(item) item.Severity >= 2).Count()
        Dim warningCount = Findings.Where(Function(item) item.Severity = 1 AndAlso item.Category <> "Data quality").Count()
        Dim qualityCount = Findings.Where(Function(item) item.Severity = 1 AndAlso item.Category = "Data quality").Count()
        HealthScore = CInt(Math.Max(0.0R, Math.Min(100.0R, 100.0R - 15.0R * Math.Min(criticalCount, 4) - 4.0R * Math.Min(warningCount, 10) - 2.0R * Math.Min(qualityCount, 3) - (100.0R - Completeness) * 0.5R)))
        Dim ranked = Findings.OrderByDescending(Function(item) item.Severity).ThenBy(Function(item) If(item.StartUtc, DateTime.MaxValue)).ToList()
        Findings.Clear()
        Findings.AddRange(ranked)
    End Sub
End Class
