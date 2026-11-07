Imports System.Linq

Public Enum ChannelComparisonStyle
    RawPoints
    MeanResponse
    TimePath
End Enum

Public NotInheritable Class ChannelComparisonStudy
    Public Property X As HistoryMetric
    Public Property Y As HistoryMetric
    Public Property Style As ChannelComparisonStyle
    Public Property IncludeTimeTraces As Boolean
    Public Property Pairs As AnalyticsTrendPoint() = Array.Empty(Of AnalyticsTrendPoint)()
    Public Property CandidateAcquisitions As Integer
    Public ReadOnly Property ExcludedAcquisitions As Integer
        Get
            Return CandidateAcquisitions - Pairs.Length
        End Get
    End Property
    Public ReadOnly Property ModeName As String
        Get
            Return If(Style = ChannelComparisonStyle.MeanResponse, "Mean response + observed range",
                If(Style = ChannelComparisonStyle.TimePath, "Chronological path",
                    If((X IsNot Nothing AndAlso X.IsEstimate) OrElse (Y IsNot Nothing AndAlso Y.IsEstimate),
                        "Raw acquisition pairs (includes estimates)", "Raw measured pairs")))
        End Get
    End Property

    Public Shared Function Create(result As EquipmentHistoryResult, x As HistoryMetric, y As HistoryMetric,
                                  style As ChannelComparisonStyle, includeTimeTraces As Boolean) As ChannelComparisonStudy
        If result Is Nothing OrElse result.Request Is Nothing OrElse result.Request.Equipment Is Nothing OrElse
            result.Request.Metrics Is Nothing OrElse result.Request.IsCombined OrElse x Is Nothing OrElse y Is Nothing OrElse
            x.IsIntervalPeak OrElse y.IsIntervalPeak OrElse x.IsState OrElse y.IsState OrElse x.IsAlarm OrElse y.IsAlarm OrElse
            Not result.Request.Metrics.Any(Function(metric) metric.Id = x.Id) OrElse
            Not result.Request.Metrics.Any(Function(metric) metric.Id = y.Id) OrElse Not [Enum].IsDefined(style) OrElse
            Not ElectricalReportKnowledge.SameEquipment(x, y, result.Request.Equipment.Id) OrElse
            ElectricalReportKnowledge.EquipmentIdentity(x, result.Request.Equipment.Id) <> result.Request.Equipment.Id Then
            Throw New ArgumentException("Comparison requires two selected numeric raw channels from the same equipment.")
        End If
        Dim study As New ChannelComparisonStudy With {.X = x, .Y = y, .Style = style, .IncludeTimeTraces = includeTimeTraces}
        Dim pairs As New List(Of AnalyticsTrendPoint)()
        Dim connect = False
        Dim previous As DateTime? = Nothing
        Dim gapSeconds = Math.Max(0.001R, Math.Max(result.Request.Equipment.IntervalSeconds, result.Request.ThinSeconds)) * 2.5R
        For Each group In result.Rows.Where(Function(row) (row.EquipmentId.Length = 0 OrElse row.EquipmentId = result.Request.Equipment.Id) AndAlso
            (row.MetricId = x.Id OrElse row.MetricId = y.Id) AndAlso row.TimestampUtc >= result.Request.FromUtc AndAlso
            row.TimestampUtc < result.Request.ToUtc).
            GroupBy(Function(row) New With {Key .Utc = row.TimestampUtc, Key .Observation = row.ObservationId,
                Key .AcquiredUtc = row.AcquiredUtc, Key .Revision = row.Revision}).
            OrderBy(Function(acquisition) acquisition.Key.Utc)
            study.CandidateAcquisitions += 1
            Dim xs = group.Where(Function(row) row.MetricId = x.Id).ToArray()
            Dim ys = group.Where(Function(row) row.MetricId = y.Id).ToArray()
            If xs.Length <> 1 OrElse ys.Length <> 1 OrElse
                xs(0).Quality <> "Good" OrElse ys(0).Quality <> "Good" OrElse
                Not xs(0).Value.HasValue OrElse Not ys(0).Value.HasValue OrElse
                Not Double.IsFinite(xs(0).Value.GetValueOrDefault()) OrElse Not Double.IsFinite(ys(0).Value.GetValueOrDefault()) Then
                connect = False
                Continue For
            End If
            Dim spacing = If(previous.HasValue, (group.Key.Utc - previous.Value).TotalSeconds, 0)
            pairs.Add(New AnalyticsTrendPoint With {.TimestampUtc = group.Key.Utc, .XValue = xs(0).Value, .Value = ys(0).Value,
                .IsValid = True, .ConnectFromPrevious = connect AndAlso spacing > 0 AndAlso spacing <= gapSeconds})
            previous = group.Key.Utc
            connect = True
        Next
        study.Pairs = pairs.ToArray()
        Return study
    End Function
End Class

Public NotInheritable Class ChannelComparisonBin
    Public Property Index As Integer
    Public Property MinimumX As Double
    Public Property MaximumX As Double
    Public Property MeanX As Double
    Public Property MeanY As Double
    Public Property MinimumY As Double
    Public Property MaximumY As Double
    Public Property Count As Integer

    Public Shared Function Build(points As IReadOnlyList(Of AnalyticsTrendPoint)) As ChannelComparisonBin()
        Dim valid = points.Where(Function(point) point IsNot Nothing AndAlso point.IsValid AndAlso
            point.XValue.HasValue AndAlso Double.IsFinite(point.XValue.Value) AndAlso
            point.Value.HasValue AndAlso Double.IsFinite(point.Value.Value)).ToArray()
        If valid.Length = 0 Then Return Array.Empty(Of ChannelComparisonBin)()
        Dim levels = valid.Select(Function(point) point.XValue.Value).Distinct().OrderBy(Function(value) value).ToArray()
        Dim exactLevels = levels.Length <= 12
        Dim width = If(exactLevels, 0.0R, (levels.Last() - levels.First()) / 12.0R)
        Return valid.GroupBy(Function(point) If(exactLevels, Array.BinarySearch(levels, point.XValue.Value),
                Math.Min(11, CInt(Math.Floor((point.XValue.Value - levels.First()) / width))))).
            OrderBy(Function(group) group.Key).Select(Function(group) New ChannelComparisonBin With {
                .Index = group.Key, .MinimumX = If(exactLevels, levels(group.Key), levels.First() + group.Key * width),
                .MaximumX = If(exactLevels, levels(group.Key), levels.First() + (group.Key + 1) * width),
                .MeanX = group.Average(Function(point) point.XValue.Value),
                .MeanY = group.Average(Function(point) point.Value.Value),
                .MinimumY = group.Min(Function(point) point.Value.Value),
                .MaximumY = group.Max(Function(point) point.Value.Value), .Count = group.Count()}).ToArray()
    End Function
End Class
