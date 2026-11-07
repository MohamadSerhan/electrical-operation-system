Imports System.Linq
Imports System.Text.RegularExpressions

Public NotInheritable Class UpsPowerBalanceStudy
    Public Property EquipmentId As String = String.Empty
    Public Property Model As String = String.Empty
    Public Property ModelBasis As String = String.Empty
    Public Property RatedKva As Double?
    Public Property RatingBasis As String = String.Empty
    Public Property CandidateAcquisitions As Integer
    Public Property PairedAcquisitions As Integer
    Public Property InputKw As Double?
    Public Property OutputKw As Double?
    Public Property ObservedInputKw As Double?
    Public Property ObservedOutputKw As Double?
    Public Property InputValidAcquisitions As Integer
    Public Property OutputValidAcquisitions As Integer
    Public Property DifferenceKw As Double?
    Public Property AcTransferPercent As Double?
    Public Property ReverseOrInconsistentPairs As Integer
    Public Property PowerBasis As String = String.Empty
    Public Property InputSelectedChannels As Integer
    Public Property OutputSelectedChannels As Integer
    Public Property OutputTotalSelected As Boolean
    Public ReadOnly Property Interpretation As String
        Get
            Dim text = EquipmentId & ": "
            If PairedAcquisitions > 0 Then
                text &= "On " & PairedAcquisitions & " exact acquisition pairs, mean active input = " & InputKw.Value.ToString("0.00", Globalization.CultureInfo.InvariantCulture) &
                    " kW, output = " & OutputKw.Value.ToString("0.00", Globalization.CultureInfo.InvariantCulture) & " kW; observed input-output difference = " &
                    EquipmentAnalysis.Num(DifferenceKw.Value) & " kW. "
                If AcTransferPercent.HasValue Then
                    text &= "Apparent AC transfer ratio = " & EquipmentAnalysis.Num(AcTransferPercent.Value) &
                        " %; observed difference / input = " & EquipmentAnalysis.Num(100.0R - AcTransferPercent.Value) &
                        " %. These are this device's observed AC power balance, NOT established conversion loss or verified operating efficiency. "
                Else
                    text &= ReverseOrInconsistentPairs & " pair(s) have nonpositive input, reverse output or output above input; efficiency-like ratios are withheld, not clamped. "
                End If
            Else
                text &= "No valid matched active-power pair; losses and efficiency cannot be calculated. "
            End If
            If InputValidAcquisitions = 0 Then
                text &= If(InputSelectedChannels = 0, "Measured active input power was not selected. ",
                    "Selected input active-power channels returned no complete valid total; unsupported/missing evidence is not zero input power. ")
            End If
            If OutputValidAcquisitions = 0 Then
                text &= If(OutputSelectedChannels = 0, "Measured active output power was not selected. ",
                    "Selected output active-power channels returned no complete valid total. ")
                If Not OutputTotalSelected Then text &= "Select Output Power (total) where supported; per-phase Output Power may be unavailable even when the system total exists. "
            ElseIf PairedAcquisitions = 0 Then
                text &= "Independent observed output mean = " & EquipmentAnalysis.Num(ObservedOutputKw.Value) &
                    " kW; this alone cannot establish loss or efficiency. "
            End If
            text &= "Verify UPS operating mode, battery charge/discharge balance and internal meter update timing. Current differences are not power loss, and no higher/lower loss or efficiency ranking against another equipment is supported."
            Return text
        End Get
    End Property
    Public ReadOnly Property CurrentRows As New List(Of String())()
    Public ReadOnly Property PfRows As New List(Of String())()

    Private Shared Function Key(metric As HistoryMetric) As String
        Dim id = If(metric.SourceMetricId.Length > 0, metric.SourceMetricId, metric.Id)
        Dim native = id.LastIndexOf("channel:", StringComparison.Ordinal)
        If native >= 0 Then id = id.Substring(native + 8)
        Return Regex.Replace(id, ":L[123]$", "").ToLowerInvariant()
    End Function

    Private Shared Function Phase(metric As HistoryMetric) As Integer
        If metric.SourcePhase > 0 Then Return metric.SourcePhase
        Dim match = Regex.Match(If(metric.SourceMetricId.Length > 0, metric.SourceMetricId, metric.Id), "(?:[_:]l|_l)([123])$", RegexOptions.IgnoreCase)
        If Not match.Success Then match = Regex.Match(metric.Name, "\bL([123])$", RegexOptions.IgnoreCase)
        Return If(match.Success, Integer.Parse(match.Groups(1).Value, Globalization.CultureInfo.InvariantCulture), 0)
    End Function

    Private Shared Function Equipment(metric As HistoryMetric, analysis As EquipmentAnalysis) As String
        If metric.EquipmentId.Length > 0 Then Return metric.EquipmentId
        If metric.Name.Contains("|"c) Then Return metric.Name.Substring(0, metric.Name.LastIndexOf("|"c)).Trim()
        Return analysis.Result.Request.Equipment.Id
    End Function

    Private Shared Function Eligible(metric As HistoryMetric) As Boolean
        Return Not metric.IsEstimate AndAlso Not metric.IsAlarm AndAlso Not metric.IsState AndAlso Not metric.IsIntervalPeak AndAlso Not ElectricalReportKnowledge.IsDc(metric)
    End Function

    Private Shared Function IsPower(metric As HistoryMetric, input As Boolean) As Boolean
        If Not Eligible(metric) OrElse Not {"W", "kW"}.Contains(metric.Unit) Then Return False
        Dim keyName = Key(metric)
        Return If(input, keyName = "inputpower" OrElse keyName = "input_kw",
            {"outputpower", "@outputkw", "output_kw", "reported_output_kw"}.Contains(keyName) OrElse Regex.IsMatch(keyName, "^out_w_l[123]$"))
    End Function

    Private Shared Function IsCurrent(metric As HistoryMetric, input As Boolean) As Boolean
        If Not Eligible(metric) OrElse metric.Unit <> "A" Then Return False
        Return If(input, Key(metric) = "inputcurrent" OrElse Regex.IsMatch(Key(metric), "^in_i_l[123]$"),
            Key(metric) = "outputcurrent" OrElse Regex.IsMatch(Key(metric), "^out_i_l[123]$"))
    End Function

    Private Shared Function IsPf(metric As HistoryMetric, input As Boolean) As Boolean
        If Not Eligible(metric) OrElse Not {"", "PF", "pf"}.Contains(metric.Unit) Then Return False
        Return If(input, {"inputpowerfactor", "input_pf"}.Contains(Key(metric)),
            {"outputpowerfactor", "@outputpf", "output_pf"}.Contains(Key(metric)))
    End Function

    Private Shared Function IsLnVoltage(metric As HistoryMetric, input As Boolean) As Boolean
        If Not Eligible(metric) OrElse metric.Unit <> "V" OrElse metric.VoltageReference <> "L-N" Then Return False
        Return Key(metric) = If(input, "inputvoltage", "outputvoltage")
    End Function

    Private Shared Function Samples(analysis As EquipmentAnalysis, metric As HistoryMetric, equipmentId As String) As Dictionary(Of Tuple(Of DateTime, String, DateTime?, Integer), Double?)
        Dim result As New Dictionary(Of Tuple(Of DateTime, String, DateTime?, Integer), Double?)()
        If analysis.Result.Rows.Count > 0 Then
            For Each acquisition In analysis.Result.Rows.Where(Function(row) row.MetricId = metric.Id AndAlso
                (row.EquipmentId.Length = 0 OrElse row.EquipmentId = equipmentId) AndAlso
                row.TimestampUtc >= analysis.Result.Request.FromUtc AndAlso row.TimestampUtc < analysis.Result.Request.ToUtc).
                GroupBy(Function(row) Tuple.Create(row.TimestampUtc, row.ObservationId, row.AcquiredUtc, row.Revision))
                Dim rows = acquisition.ToArray()
                result(acquisition.Key) = If(rows.Length = 1 AndAlso rows(0).Quality = "Good" AndAlso rows(0).Value.HasValue AndAlso
                    Double.IsFinite(rows(0).Value.GetValueOrDefault()), rows(0).Value, Nothing)
            Next
        Else
            Dim points As List(Of ReportPoint) = Nothing
            If Not analysis.RecordedSeries.TryGetValue(metric.Id, points) Then
                Dim channel = analysis.Channels.FirstOrDefault(Function(item) item.Metric.Id = metric.Id)
                If channel IsNot Nothing Then points = channel.Points
            End If
            If points IsNot Nothing Then
                For Each acquisition In points.Where(Function(point) point.T >= analysis.Result.Request.FromUtc AndAlso
                    point.T < analysis.Result.Request.ToUtc).GroupBy(Function(point) point.T)
                    Dim observed = acquisition.ToArray()
                    result(Tuple.Create(acquisition.Key, String.Empty, CType(Nothing, DateTime?), 0)) =
                        If(observed.Length = 1 AndAlso Double.IsFinite(observed(0).V), CType(observed(0).V, Double?), Nothing)
                Next
            End If
        End If
        Return result
    End Function

    Private Shared Function TotalPower(metrics As HistoryMetric(), tables As Dictionary(Of String, Dictionary(Of Tuple(Of DateTime, String, DateTime?, Integer), Double?)),
                                      ByRef basis As String) As Dictionary(Of Tuple(Of DateTime, String, DateTime?, Integer), Double?)
        Dim totals = metrics.Where(Function(metric) Phase(metric) = 0).ToArray()
        If totals.Any(Function(metric) Key(metric) <> "reported_output_kw") Then
            totals = totals.Where(Function(metric) Key(metric) <> "reported_output_kw").ToArray()
        End If
        If totals.Length > 1 Then
            basis = "Ambiguous selected total active-power channels; select one validated total"
            Return New Dictionary(Of Tuple(Of DateTime, String, DateTime?, Integer), Double?)()
        End If
        Dim total = totals.FirstOrDefault()
        If total IsNot Nothing Then
            basis = total.Name & " [" & total.Unit & "]"
            If Not tables(total.Id).Values.Any(Function(value) value.HasValue) Then basis &= " (selected, but no valid total readings)"
            Return tables(total.Id).ToDictionary(Function(item) item.Key,
                Function(item) If(item.Value.HasValue, CType(item.Value.Value / If(total.Unit = "W", 1000.0R, 1.0R), Double?), Nothing))
        End If
        Dim phases = Enumerable.Range(1, 3).Select(Function(number) metrics.Where(Function(metric) Phase(metric) = number).ToArray()).ToArray()
        If phases.Any(Function(items) items.Length <> 1) Then
            basis = "A measured total or exactly one measured active-power channel for each L1/L2/L3 is required"
            Return New Dictionary(Of Tuple(Of DateTime, String, DateTime?, Integer), Double?)()
        End If
        basis = "Sum of three same-acquisition measured phase active powers (W converted to kW)"
        Dim selected = phases.Select(Function(items) items(0)).ToArray()
        Dim result As New Dictionary(Of Tuple(Of DateTime, String, DateTime?, Integer), Double?)()
        For Each acquisition In selected.SelectMany(Function(metric) tables(metric.Id).Keys).Distinct()
            Dim values = selected.Select(Function(metric)
                                             Dim value As Double? = Nothing
                                             tables(metric.Id).TryGetValue(acquisition, value)
                                             Return If(value.HasValue, CType(value.Value / If(metric.Unit = "W", 1000.0R, 1.0R), Double?), Nothing)
                                         End Function).ToArray()
            result(acquisition) = If(values.All(Function(value) value.HasValue), CType(values.Sum(Function(value) value.GetValueOrDefault()), Double?), Nothing)
        Next
        If Not result.Values.Any(Function(value) value.HasValue) Then
            basis = "Selected L1/L2/L3 active-power channels have no complete valid same-acquisition phase total"
        End If
        Return result
    End Function

    Public Shared Function Build(analysis As EquipmentAnalysis) As List(Of UpsPowerBalanceStudy)
        Dim results As New List(Of UpsPowerBalanceStudy)()
        For Each asset In analysis.Result.Request.Metrics.Where(Function(metric) IsPower(metric, True) OrElse IsPower(metric, False) OrElse
            IsCurrent(metric, True) OrElse IsCurrent(metric, False) OrElse IsPf(metric, True) OrElse IsPf(metric, False) OrElse
            IsLnVoltage(metric, True) OrElse IsLnVoltage(metric, False)).
            GroupBy(Function(metric) Equipment(metric, analysis))
            Dim metrics = asset.ToArray()
            Dim first = metrics(0)
            Dim study As New UpsPowerBalanceStudy With {.EquipmentId = asset.Key, .Model = first.EquipmentModel,
                .ModelBasis = first.ModelBasis, .RatedKva = first.RatedCapacityKva, .RatingBasis = first.CapacityBasis,
                .InputSelectedChannels = metrics.Where(Function(metric) IsPower(metric, True)).Count(),
                .OutputSelectedChannels = metrics.Where(Function(metric) IsPower(metric, False)).Count(),
                .OutputTotalSelected = metrics.Any(Function(metric) IsPower(metric, False) AndAlso Phase(metric) = 0)}
            Dim tables = metrics.ToDictionary(Function(metric) metric.Id, Function(metric) Samples(analysis, metric, asset.Key))
            Dim inBasis = String.Empty
            Dim outBasis = String.Empty
            Dim inputs = TotalPower(metrics.Where(Function(metric) IsPower(metric, True)).ToArray(), tables, inBasis)
            Dim outputs = TotalPower(metrics.Where(Function(metric) IsPower(metric, False)).ToArray(), tables, outBasis)
            Dim validInput = inputs.Values.Where(Function(value) value.HasValue).Select(Function(value) value.Value).ToArray()
            Dim validOutput = outputs.Values.Where(Function(value) value.HasValue).Select(Function(value) value.Value).ToArray()
            study.InputValidAcquisitions = validInput.Length
            study.OutputValidAcquisitions = validOutput.Length
            If validInput.Length > 0 Then study.ObservedInputKw = validInput.Average()
            If validOutput.Length > 0 Then study.ObservedOutputKw = validOutput.Average()
            study.PowerBasis = "Input: " & inBasis & ". Output: " & outBasis & "."
            Dim pairs As New List(Of Tuple(Of Double, Double))()
            For Each acquisition In inputs.Keys.Union(outputs.Keys)
                study.CandidateAcquisitions += 1
                Dim pin As Double? = Nothing
                Dim pout As Double? = Nothing
                inputs.TryGetValue(acquisition, pin)
                outputs.TryGetValue(acquisition, pout)
                If Not pin.HasValue OrElse Not pout.HasValue Then Continue For
                If pin.Value <= 0 OrElse pout.Value < 0 OrElse pout.Value > pin.Value Then study.ReverseOrInconsistentPairs += 1
                pairs.Add(Tuple.Create(pin.Value, pout.Value))
            Next
            study.PairedAcquisitions = pairs.Count
            If pairs.Count > 0 Then
                study.InputKw = pairs.Average(Function(pair) pair.Item1)
                study.OutputKw = pairs.Average(Function(pair) pair.Item2)
                study.DifferenceKw = pairs.Average(Function(pair) pair.Item1 - pair.Item2)
                If study.ReverseOrInconsistentPairs = 0 AndAlso study.InputKw.Value > 0 Then
                    study.AcTransferPercent = 100.0R * study.OutputKw.Value / study.InputKw.Value
                End If
            End If
            For number = 1 To 3
                Dim phaseNumber = number
                Dim ins = metrics.Where(Function(metric) IsCurrent(metric, True) AndAlso Phase(metric) = phaseNumber).ToArray()
                Dim outs = metrics.Where(Function(metric) IsCurrent(metric, False) AndAlso Phase(metric) = phaseNumber).ToArray()
                If ins.Length = 0 AndAlso outs.Length = 0 Then Continue For
                AddPairRow(study.CurrentRows, "L" & number & " currents", ins, outs, tables, "A", False)
            Next
            For Each number In {0, 1, 2, 3}
                Dim phaseNumber = number
                Dim ins = metrics.Where(Function(metric) IsPf(metric, True) AndAlso Phase(metric) = phaseNumber).ToArray()
                Dim outs = metrics.Where(Function(metric) IsPf(metric, False) AndAlso Phase(metric) = phaseNumber).ToArray()
                If ins.Length = 0 AndAlso outs.Length = 0 Then Continue For
                AddPairRow(study.PfRows, If(number = 0, "System PF", "L" & number & " PF"), ins, outs, tables, "PF", True)
            Next
            If Not metrics.Any(Function(metric) IsPf(metric, True) AndAlso Phase(metric) = 0) Then
                Dim inputPf = DerivedPf(metrics, True, inputs, tables)
                Dim outputPf = DerivedPf(metrics, False, outputs, tables)
                Dim measuredOutput = metrics.Where(Function(metric) IsPf(metric, False) AndAlso Phase(metric) = 0).ToArray()
                If measuredOutput.Length = 1 Then outputPf = tables(measuredOutput(0).Id)
                Dim paired = inputPf.Keys.Intersect(outputPf.Keys).Where(Function(acquisition) inputPf(acquisition).HasValue AndAlso
                    outputPf(acquisition).HasValue AndAlso outputPf(acquisition).Value >= 0 AndAlso outputPf(acquisition).Value <= 1).ToArray()
                If paired.Length > 0 Then
                    study.PfRows.Add({"System PF (input P/S)", PfText(paired.Average(Function(acquisition) inputPf(acquisition).Value)),
                        PfText(paired.Average(Function(acquisition) outputPf(acquisition).Value)),
                        paired.Length & " exact pairs; input S = sum of three verified L-N rms V x phase rms I / 1000; input PF = active Pin / S. Output PF from selected validated system PF, or equivalent P/S."})
                End If
            End If
            results.Add(study)
        Next
        Return results
    End Function

    Private Shared Function DerivedPf(metrics As HistoryMetric(), input As Boolean,
                                     power As Dictionary(Of Tuple(Of DateTime, String, DateTime?, Integer), Double?),
                                     tables As Dictionary(Of String, Dictionary(Of Tuple(Of DateTime, String, DateTime?, Integer), Double?))) As Dictionary(Of Tuple(Of DateTime, String, DateTime?, Integer), Double?)
        Dim result As New Dictionary(Of Tuple(Of DateTime, String, DateTime?, Integer), Double?)()
        Dim voltages = Enumerable.Range(1, 3).Select(Function(number) metrics.Where(Function(metric) IsLnVoltage(metric, input) AndAlso Phase(metric) = number).ToArray()).ToArray()
        Dim currents = Enumerable.Range(1, 3).Select(Function(number) metrics.Where(Function(metric) IsCurrent(metric, input) AndAlso Phase(metric) = number).ToArray()).ToArray()
        If voltages.Concat(currents).Any(Function(items) items.Length <> 1) Then Return result
        For Each acquisition In power.Keys
            If Not power(acquisition).HasValue OrElse power(acquisition).Value < 0 Then Continue For
            Dim apparent = 0.0R
            Dim complete = True
            For index = 0 To 2
                Dim voltage As Double? = Nothing
                Dim current As Double? = Nothing
                tables(voltages(index)(0).Id).TryGetValue(acquisition, voltage)
                tables(currents(index)(0).Id).TryGetValue(acquisition, current)
                If Not voltage.HasValue OrElse Not current.HasValue OrElse voltage.Value <= 0 OrElse current.Value < 0 Then
                    complete = False
                    Exit For
                End If
                apparent += voltage.Value * current.Value / 1000.0R
            Next
            If complete AndAlso Double.IsFinite(apparent) AndAlso apparent > 0 Then
                Dim pf = power(acquisition).Value / apparent
                If Double.IsFinite(pf) AndAlso pf >= 0 AndAlso pf <= 1 Then result(acquisition) = pf
            End If
        Next
        Return result
    End Function

    Private Shared Sub AddPairRow(rows As List(Of String()), quantity As String, ins As HistoryMetric(), outs As HistoryMetric(),
                                 tables As Dictionary(Of String, Dictionary(Of Tuple(Of DateTime, String, DateTime?, Integer), Double?)),
                                 unit As String, pf As Boolean)
        If ins.Length <> 1 OrElse outs.Length <> 1 Then
            If ins.Length = 0 AndAlso outs.Length = 1 Then
                Dim values = tables(outs(0).Id).Values.Where(Function(value) value.HasValue AndAlso
                    value.Value >= 0 AndAlso (Not pf OrElse value.Value <= 1)).Select(Function(value) value.Value).ToArray()
                rows.Add({quantity & " (independent)", "Unavailable / unselected",
                    If(values.Length > 0, If(pf, PfText(values.Average()), EquipmentAnalysis.Num(values.Average()) & " " & unit), "No valid data"),
                    values.Length & " output-only acquisitions; NOT a paired comparison"})
            Else
                rows.Add({quantity, "Not calculable", "Not calculable", "No unique selected input/output pair"})
            End If
            Return
        End If
        Dim input = tables(ins(0).Id)
        Dim output = tables(outs(0).Id)
        Dim pairs As New List(Of Tuple(Of Double, Double))()
        For Each acquisition In input.Keys.Intersect(output.Keys)
            Dim a = input(acquisition)
            Dim b = output(acquisition)
            If a.HasValue AndAlso b.HasValue AndAlso If(pf, a.Value >= 0 AndAlso a.Value <= 1 AndAlso b.Value >= 0 AndAlso b.Value <= 1, a.Value >= 0 AndAlso b.Value >= 0) Then
                pairs.Add(Tuple.Create(a.Value, b.Value))
            End If
        Next
        If pairs.Count = 0 Then
            rows.Add({quantity, "Not calculable", "Not calculable", "No valid exact-acquisition pair"})
        Else
            rows.Add({quantity, If(pf, PfText(pairs.Average(Function(pair) pair.Item1)), EquipmentAnalysis.Num(pairs.Average(Function(pair) pair.Item1)) & " " & unit),
                If(pf, PfText(pairs.Average(Function(pair) pair.Item2)), EquipmentAnalysis.Num(pairs.Average(Function(pair) pair.Item2)) & " " & unit),
                pairs.Count & " paired samples; output - input = " & If(pf, PfText(pairs.Average(Function(pair) pair.Item2 - pair.Item1)), EquipmentAnalysis.Num(pairs.Average(Function(pair) pair.Item2 - pair.Item1)) & " " & unit)})
        End If
    End Sub

    Private Shared Function PfText(value As Double) As String
        Return value.ToString("0.000", Globalization.CultureInfo.InvariantCulture) & " PF"
    End Function
End Class
