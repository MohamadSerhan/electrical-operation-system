Imports System.Linq

Public Module UpsInputEstimates
    Public Const VoltageKey As String = "@EstimatedInputVoltageLN"
    Public Const KvaKey As String = "@EstimatedInputKva"
    Public Const EvidenceVersion As Integer = 2
    Public Const Assumptions As String = "ESTIMATE ONLY: L-L based voltage and kVA assume a balanced three-phase supply and currents; balance, waveform and neutral reference are not verified. Equivalent voltage, not measured L1-N/L2-N/L3-N. When all three measured L-N voltages are verified, kVA uses their matched phase V x I sum instead. Not active input kW, input PF, efficiency, losses, energy or compliance evidence. Output PF must never substitute for input PF."

    Public Function IsEstimateKey(key As String) As Boolean
        Return key = VoltageKey OrElse key = KvaKey
    End Function

    Public Function Metrics() As HistoryMetric()
        Return {
            New HistoryMetric With {.Id = VoltageKey, .SourceMetricId = VoltageKey, .Name = "Estimated Input L-N Voltage (balanced equivalent)",
                .Unit = "V", .IsEstimate = True, .MinimumMappingRevision = 2,
                .VoltageReference = "Estimated L-N (balanced equivalent)", .Basis = "mean(measured AB, BC, CA) / sqrt(3). " & Assumptions},
            New HistoryMetric With {.Id = KvaKey, .SourceMetricId = KvaKey, .Name = "Estimated Input Apparent Power (V x I)",
                .Unit = "kVA", .IsEstimate = True, .MinimumMappingRevision = 2,
                .Basis = "Verified measured L-N: sum(V1-N*I1, V2-N*I2, V3-N*I3)/1000. Otherwise balanced L-L: sqrt(3) * mean(measured AB, BC, CA) * mean(measured I1, I2, I3) / 1000. " & Assumptions}}
    End Function

    Private Function CompletePhases(snapshot As UpsSnapshot, key As String, maximum As Double) As Double()
        If snapshot Is Nothing OrElse Not snapshot.IsGood Then Return Nothing
        Dim phases As Double?() = Nothing
        If Not snapshot.Phases.TryGetValue(key, phases) OrElse phases Is Nothing OrElse phases.Length <> 3 OrElse
           phases.Any(Function(value) Not value.HasValue OrElse Not Double.IsFinite(value.Value) OrElse value.Value < 0 OrElse value.Value > maximum) Then Return Nothing
        Return phases.Select(Function(value) value.Value).ToArray()
    End Function

    Private Function LineVoltages(snapshot As UpsSnapshot) As Double()
        If snapshot Is Nothing OrElse Not snapshot.IsGood Then Return Nothing
        Dim explicit = CompletePhases(snapshot, "InputVoltageLL", 1000)
        If snapshot.Phases.ContainsKey("InputVoltageLL") Then Return explicit
        If Not snapshot.InputVoltageReference.StartsWith("L-L", StringComparison.Ordinal) Then Return Nothing
        Return CompletePhases(snapshot, "InputVoltage", 1000)
    End Function

    Private Function NeutralVoltages(snapshot As UpsSnapshot) As Double()
        If snapshot Is Nothing OrElse Not snapshot.IsGood Then Return Nothing
        If snapshot.Phases.ContainsKey("InputVoltageLN") Then Return CompletePhases(snapshot, "InputVoltageLN", 1000)
        If snapshot.InputVoltageReference <> "L-N" Then Return Nothing
        Return CompletePhases(snapshot, "InputVoltage", 1000)
    End Function

    Public Function Calculate(snapshot As UpsSnapshot, key As String) As Double?
        If Not IsEstimateKey(key) Then Throw New ArgumentException("Unknown UPS input estimate.", NameOf(key))
        If key = KvaKey Then
            Dim neutral = NeutralVoltages(snapshot)
            Dim phaseCurrents = CompletePhases(snapshot, "InputCurrent", 10000)
            If phaseCurrents Is Nothing Then Return Nothing
            If neutral IsNot Nothing Then Return Enumerable.Range(0, 3).Sum(Function(index) neutral(index) * phaseCurrents(index)) / 1000
            If snapshot IsNot Nothing AndAlso (snapshot.Phases.ContainsKey("InputVoltageLN") OrElse snapshot.InputVoltageReference = "L-N") Then Return Nothing
        End If
        Dim voltages = LineVoltages(snapshot)
        If voltages Is Nothing Then Return Nothing
        If key = VoltageKey Then Return voltages.Average() / Math.Sqrt(3)
        Dim currents = CompletePhases(snapshot, "InputCurrent", 10000)
        If currents Is Nothing Then Return Nothing
        Return Math.Sqrt(3) * voltages.Average() * currents.Average() / 1000
    End Function

    Public Function MissingReason(snapshot As UpsSnapshot, key As String) As String
        If Calculate(snapshot, key).HasValue Then Return String.Empty
        If snapshot Is Nothing Then Return "No acquisition available; estimate not calculated."
        If Not snapshot.IsGood Then Return snapshot.Quality.ToString() & ": " & snapshot.QualityDetail & "; estimate not calculated from failed or last-good data."
        If key = VoltageKey AndAlso NeutralVoltages(snapshot) IsNot Nothing Then Return "Actual input L-N phase voltages are available: use measured Input Voltage L1-N/L2-N/L3-N. No verified L-L source for this separate balanced-equivalent channel."
        Return "Estimate requires three finite, nonnegative measured input " &
            If(key = KvaKey, "L-N or L-L voltages and all three measured input currents", "L-L voltages") &
            " from this same Good acquisition. Missing or unverified inputs remain unknown, not zero."
    End Function

    Public Function RecordedEvidence(snapshot As UpsSnapshot) As Object
        Return New With {
            .Version = EvidenceVersion, .Assumptions = Assumptions,
            .Values = Metrics().ToDictionary(Function(metric) metric.Id, Function(metric) Calculate(snapshot, metric.Id)),
            .Formulas = Metrics().ToDictionary(Function(metric) metric.Id, Function(metric) metric.Basis),
            .Availability = Metrics().ToDictionary(Function(metric) metric.Id, Function(metric) MissingReason(snapshot, metric.Id)),
            .InputVoltageLl = LineVoltages(snapshot), .InputVoltageLn = NeutralVoltages(snapshot),
            .InputCurrent = CompletePhases(snapshot, "InputCurrent", 10000),
            .ApparentPowerMethod = If(NeutralVoltages(snapshot) IsNot Nothing, "Sum of measured L-N phase V x I", "Balanced L-L approximation")}
    End Function
End Module
