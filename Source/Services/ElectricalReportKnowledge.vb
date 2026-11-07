Imports System.Linq

Public NotInheritable Class ElectricalReportKnowledge
    Private Sub New()
    End Sub

    Public Shared Function EquipmentIdentity(metric As HistoryMetric, fallback As String) As String
        If metric.EquipmentId.Length > 0 Then Return metric.EquipmentId
        If metric.Name.Contains("|"c) Then Return metric.Name.Substring(0, metric.Name.LastIndexOf("|"c)).Trim()
        Return fallback
    End Function

    Public Shared Function SameEquipment(a As HistoryMetric, b As HistoryMetric, fallback As String) As Boolean
        Dim owner = EquipmentIdentity(a, fallback)
        Return owner.Length > 0 AndAlso String.Equals(owner, EquipmentIdentity(b, fallback), StringComparison.Ordinal)
    End Function

    Public Shared Function IsDc(metric As HistoryMetric) As Boolean
        Return {QuantityName(metric.Name), QuantityName(metric.SourceMetricName), MetricKey(metric.Id), MetricKey(metric.SourceMetricId)}.
            Any(Function(name) name.Contains("battery", StringComparison.OrdinalIgnoreCase) OrElse
                name.Contains("DC bus", StringComparison.OrdinalIgnoreCase) OrElse name.Contains("DcBus", StringComparison.OrdinalIgnoreCase))
    End Function

    Public Shared Function MeasurementSide(metric As HistoryMetric) As String
        Dim names = {QuantityName(metric.Name), QuantityName(metric.SourceMetricName)}
        Dim keys = {MetricKey(metric.Id), MetricKey(metric.SourceMetricId)}
        If names.Any(Function(name) name.Contains("Input", StringComparison.OrdinalIgnoreCase)) OrElse
            keys.Any(Function(key) key.StartsWith("Input", StringComparison.OrdinalIgnoreCase) OrElse key.StartsWith("in_", StringComparison.OrdinalIgnoreCase)) Then Return "Input"
        If names.Any(Function(name) name.Contains("Output", StringComparison.OrdinalIgnoreCase)) OrElse
            keys.Any(Function(key) key.StartsWith("Output", StringComparison.OrdinalIgnoreCase) OrElse key.StartsWith("@Output", StringComparison.OrdinalIgnoreCase) OrElse
                key.StartsWith("out_", StringComparison.OrdinalIgnoreCase) OrElse key.StartsWith("output_", StringComparison.OrdinalIgnoreCase)) Then Return "Output"
        If names.Any(Function(name) name.Contains("Bypass", StringComparison.OrdinalIgnoreCase)) OrElse
            keys.Any(Function(key) key.StartsWith("Bypass", StringComparison.OrdinalIgnoreCase) OrElse key.StartsWith("bypass_", StringComparison.OrdinalIgnoreCase)) Then Return "Bypass"
        Return "Meter"
    End Function

    Private Shared Function QuantityName(name As String) As String
        If String.IsNullOrWhiteSpace(name) Then Return String.Empty
        Return name.Substring(name.LastIndexOf("|"c) + 1).Trim()
    End Function

    Private Shared Function MetricKey(id As String) As String
        Dim key = QuantityName(id)
        key = System.Text.RegularExpressions.Regex.Replace(key, ":L[123]$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        Return key.Split(":"c).Last()
    End Function

    Public Shared Function Investigation(title As String, metric As HistoryMetric) As String
        If metric.IsEstimate Then Return "Assumption-based estimate, not a measured channel. " & metric.Basis & " Verify actual input measurements before any electrical diagnosis."
        If IsDc(metric) Then
            Return "Not established. Verify the battery/DC measurement point, mapping, sign convention and sensor; then corroborate operating/charging state, ambient conditions and manufacturer chemistry/cell-count limits. AC tap, capacitor and motor diagnoses are not applicable."
        End If
        If title.Contains("power factor", StringComparison.OrdinalIgnoreCase) Then
            Return "Not established. Verify input/output measurement point, total versus phase PF, true PF versus displacement PF, signed/no-load behaviour and same-acquisition P/S consistency. Load, UPS mode and waveform distortion are alternatives; no capacitor failure or compensation requirement is proven without topology, reactive power and harmonic evidence."
        End If
        If title.Contains("temperature", StringComparison.OrdinalIgnoreCase) Then
            Return "Not established. Verify sensor type/location, units and internal update cadence, approved limits and ambient conditions; examine the same equipment's earlier load and cooling evidence with thermal lag. Do not infer cooling failure, overheating connection or winding temperature from this trend alone."
        End If
        If title.Contains("Voltage", StringComparison.OrdinalIgnoreCase) OrElse title.Contains("Frequency", StringComparison.OrdinalIgnoreCase) Then
            Return "Not established. Verify phase/reference, mapping/scaling, actual source/mode and internal meter update timing; then corroborate same-equipment demand and alarms. UPS regulation/bypass and source changes require observed state evidence; upstream causes require a verified active feeder path."
        End If
        If title.Contains("neutral", StringComparison.OrdinalIgnoreCase) OrElse title.Contains("balance", StringComparison.OrdinalIgnoreCase) Then
            Return "Not established. Verify three distinct simultaneous phase channels, neutral identity, reference and scaling. Uneven demand or waveform distortion are hypotheses only; magnitude deviation is not sequence unbalance, THD, conductor ampacity or proof of a failed phase."
        End If
        If title.Contains("load", StringComparison.OrdinalIgnoreCase) OrElse title.Contains("current", StringComparison.OrdinalIgnoreCase) OrElse
            title.Contains("capacity", StringComparison.OrdinalIgnoreCase) OrElse title.StartsWith("Sudden", StringComparison.OrdinalIgnoreCase) Then
            Return "Not established. Verify measurement point, sign, baseline versus approved rating/derating and same-equipment mode/alarms. Charging, demand changes, switching and instrument updates are alternatives, not proof of overload, motor starting, breaker operation or an electrical fault."
        End If
        If title.Contains("gap", StringComparison.OrdinalIgnoreCase) OrElse title.Contains("data", StringComparison.OrdinalIgnoreCase) Then
            Return "Not established. Verify acquisition quality, unsupported/unselected channels, recording cadence, mapping revision and source timestamps. Missing records do not prove equipment outage or recorder/application shutdown."
        End If
        If title = "Alarm / fault observed" Then Return "Recorded device condition only; verify code mapping, active/cleared state and observed times. Repeated alarm observations are not separate incidents and do not establish a physical root cause."
        Return "No physical cause established from the selected evidence. Resolve measurement quality and missing prerequisites before diagnosis."
    End Function

    Public Shared Function Workflow() As List(Of String())
        Return New List(Of String()) From {
            New String() {"1. Scope and identity", "List selected equipment, measurement nodes, model and rating provenance. Separate observed, derived, configured and unavailable evidence.",
                "Current identity verification is not historical model/rating, electrical mapping or manufacturer-limit verification."},
            New String() {"2. Evidence quality and time", "Check finite values, Good quality, units/scaling, source UTC, observation ID/revision, duplicates, gaps and loaded resolution. Keep missing channels visible.",
                "A valid measured numeric zero remains zero. NoSuchInstance, No Support, timeout and missing/unselected channels remain unknown, never zero-filled. No last-good filling, interpolation or duplicate averaging. One shared request timestamp does not prove internally simultaneous meters; constant values alone do not prove stale data."},
            New String() {"3. Electrical relationship", "Keep every equipment independent. For any downstream contribution, first require approved period-valid wiring and actual ATS/source state.",
                "Same type/unit/model/time, ATS membership or free-text source labels never authorize unrelated comparisons. Unknown or different source paths prohibit contribution percentages."},
            New String() {"4. Calculation prerequisites", "Distinguish AC/DC, input/output/bypass, L-N/L-L, phase/system, W/kW/kVA/kVAr, true/displacement PF and signed import/export. Use complete eligible same-device acquisitions.",
                "Never use current/kVA/PF ratios as efficiency, sum overlapping upstream/downstream demand, average phase PF as system PF, or treat kW as kWh. Zero/negative denominators and impossible P/S withhold ratios; retain signed evidence."},
            New String() {"5. Observations and references", "Present measured values, exact time, sample/paired coverage, formula, denominator and reference provenance before a flag. Separate own-baseline deviation from nameplate/site compliance.",
                "Generic screening thresholds are not equipment trips, approved safety limits or manufacturer acceptance criteria. Quantization, sparse samples, thermal lag and internal update skew limit interpretation."},
            New String() {"6. Hypotheses and missing proof", "Separate observations from possible explanations. Check mapping/measurement issues first, then same-equipment state, loading, temperature and alarms.",
                "Correlation is not causation; zeros do not prove outage; no alarm is not proof of health. Pin - Pout is not proven loss without operating mode and battery energy balance. Withhold conclusions whose prerequisites are missing."},
            New String() {"7. Conclusion and release checks", "State supported conclusions, unassessed channels/time spans, unresolved contradictions and required measurements. Verify every selected signal/table, numeric statistics, units and page arrangement against original evidence.",
                "No healthy/perfect/no-loss claims from incomplete recordings, no fabricated datasheets or fault causes. Recommend evidence collection and qualified site review, not switching instructions."}}
    End Function

    Public Shared Function ComparisonQuantity(metric As HistoryMetric) As String
        If metric.IsEstimate Then Return "Estimated (balanced assumption) input " & metric.Unit
        Dim name = QuantityName(metric.Name)
        If IsDc(metric) Then Return If(name.Contains("DC bus", StringComparison.OrdinalIgnoreCase) OrElse
            MetricKey(metric.Id).Contains("DcBus", StringComparison.OrdinalIgnoreCase) OrElse
            MetricKey(metric.SourceMetricId).Contains("DcBus", StringComparison.OrdinalIgnoreCase), "DC bus ", "Battery ") & metric.Unit
        Dim direction = MeasurementSide(metric)
        If metric.Unit = "V" Then
            Dim reference = If(metric.VoltageReference.Length > 0, metric.VoltageReference,
                If(name.Contains("L-L", StringComparison.OrdinalIgnoreCase) OrElse name.Contains("line-line", StringComparison.OrdinalIgnoreCase) OrElse
                    System.Text.RegularExpressions.Regex.IsMatch(name, "L[123]-L[123]", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "L-L",
                    If(name.Contains("L-N", StringComparison.OrdinalIgnoreCase) OrElse
                        System.Text.RegularExpressions.Regex.IsMatch(name, "L[123]-N", System.Text.RegularExpressions.RegexOptions.IgnoreCase), "L-N", "unspecified")))
            Return direction & " voltage " & reference
        End If
        If metric.Unit = "A" Then Return direction & If(name.Contains("Neutral", StringComparison.OrdinalIgnoreCase), " neutral current", " phase current")
        Return direction & " " & metric.Unit
    End Function

    Public Shared Function Checklist(analysis As EquipmentAnalysis) As List(Of String())
        Dim metrics = analysis.Scope.Where(Function(row) row.Metric IsNot Nothing).Select(Function(row) row.Metric).ToArray()
        Dim rows = Workflow()
        If metrics.Any(Function(metric) metric.IsEstimate) Then rows.Add(
            {"Balanced input estimates", "Estimated L-N = mean(measured AB, BC, CA)/sqrt(3). Input kVA = sum(V_L-N*I_phase)/1000 when all three measured L-N phases are verified; otherwise estimated input kVA = sqrt(3)*mean(L-L)*mean(input current)/1000. Complete same-device, same-acquisition measurements required.",
                UpsInputEstimates.Assumptions & " Estimated channels never replace measured channels or establish actual per-phase L-N, input kW/PF, losses or efficiency."})
        rows.AddRange(New String()() {
            New String() {"Electrical relationship before comparison", "Analyse each device independently. Never compare transformer, UPS, generator or unrelated feeds as peers merely because units, timestamps or equipment types match.",
                "Only a verified time-valid feeder relationship can support a load contribution percentage. Use measured downstream AC input power and actual supplying upstream power, like units/reference, aligned evidence and positive denominator. Different ATS/source paths prohibit comparison. Configured ATS membership and free-text Transformer / Generator bus descriptions do not prove active supply; no upstream percentage is inferred."},
            New String() {"Evidence before diagnosis", "Verify equipment identity, channel mapping/revision, units, AC/DC reference, acquired timestamp, quality and gaps. Align corroborating channels from the same acquisition.",
                "Missing, unsupported, stale or unselected channels are unknown. A smooth chart or repeated numeric value is not proof that the instrument updated internally."},
            New String() {"UPS input-channel availability", "Inspect recorded per-card capability and unavailable-channel evidence, not the equipment family alone. Some NXL cards expose input L-L voltage and current but no verified input active power or L-N voltage.",
                "Select verified L-L channels when L-N is absent; never divide by sqrt(3) to invent measured L-N. Separately labeled balanced-equivalent estimates are assumptions only. Input current is not input active power. Missing Pin prohibits input/output loss or efficiency calculation even when output total kW exists. Numeric input zero is evidence only when actually returned and valid; corroborate supply state and alarms before diagnosing an outage."},
            New String() {"Equipment documents", "Current configured kVA and model/registry metadata may guide checks; historical ratings and exact manufacturer operating limits require approved device documentation.",
                "No manufacturer datasheets are bundled with this report. Do not invent rated kW, nominal DC voltage, battery chemistry/cell count, runtime curve, efficiency, permissible overload duration or protection settings."}})
        If metrics.Any(Function(metric) Not IsDc(metric) AndAlso {"V", "A", "kW", "kVA", "PF"}.Contains(metric.Unit)) Then
            rows.Add({"AC loading & calculations", "S(kVA) = sum(V_L-N,rms x I_phase,rms)/1000 for three matched phases. PF = measured P(kW)/S(kVA) when S > 0. Balanced sinusoidal approximation: S = sqrt(3) x V_L-L x I/1000.",
                "V x I is apparent power, not active kW. Do not mix L-L with L-N, average phase values with totals, inputs with outputs, or different devices/times. The balanced formula is not valid proof for unbalanced/distorted loads; THD/phasors are needed for harmonic or sequence analysis."})
            rows.Add({"Overload vs demand change", "Compare apparent output power with approved rated kVA; compare measured active kW with approved rated kW separately. The UPS card's load percentage is its own reported basis.",
                "Above-average current/power alone is not overload. Verify measurement scaling, configuration verification and temperature/operating derating. TOTAL overlaps downstream UPS demand; do not add both. kW is instantaneous power; kWh needs eligible chronological power integration across observed spans only."})
            rows.Add({"UPS efficiency / losses", "Establish each device's own model, rating and operating load independently. Use simultaneous same-device measured active input/output power, complete phase totals, operating mode and battery charge/discharge balance.",
                "Current ratio, kVA ratio and PF are not efficiency. Pin - Pout is only an observed AC power difference until charging, auxiliaries and operating mode are verified. Higher absolute difference is not proof of worse efficiency across different models or loads."})
        End If
        If metrics.Any(Function(metric) Not IsDc(metric) AndAlso metric.Unit = "V") Then
            rows.Add({"Input / bypass / output voltage", "Inspect like-for-like phase/reference voltage, input and output frequency, output-source/bypass state, load, communication quality and timestamp-matched alarms.",
                "An online double-conversion UPS may regulate its output independently of input. Do not assume topology or bypass mode from voltage similarity alone. A median-relative deviation is not nominal-voltage compliance; zeros do not prove a physical outage."})
        End If
        If metrics.Any(Function(metric) IsDc(metric)) Then
            rows.Add({"Battery / DC interpretation", "DC terminal power = V_DC x signed I_DC /1000 in kW only for the same battery/bus, acquisition and verified current sign convention. Corroborate battery status, seconds on battery, charge estimate, DC bus, output-source state and observed alarms.",
                "Charging vs discharging cannot be inferred from current sign until the device convention is verified. Battery voltage and DC-bus voltage are different measurement points. AC current is not DC current; no PF or sqrt(3) in DC V x I. A zero battery current can mean float/no discharge, not necessarily failure."})
            rows.Add({"DC limits, runtime & efficiency", "Obtain battery chemistry, series cell count, nominal/float/end-of-discharge voltage, temperature compensation, capacity, age, test state and the manufacturer's load/runtime curve before assessing autonomy.",
                "Charge percent alone cannot prove remaining runtime or battery health. Do not apply AC voltage limits to DC. Efficiency requires simultaneous active input/output power at a verified operating mode with battery charge/discharge accounted for; unrelated readings or kVA ratios are not efficiency."})
        End If
        If metrics.Any(Function(metric) metric.Name.Contains("temp", StringComparison.OrdinalIgnoreCase) OrElse metric.Name.Contains("TDEGC", StringComparison.OrdinalIgnoreCase)) Then
            rows.Add({"Temperature & load response", "Identify sensor location/type (battery, ambient, enclosure, oil or winding), manufacturer/site limits, ambient temperature, cooling state and the aligned loading history.",
                "Battery temperature is not transformer winding temperature. Generic 80/100 deg C guides must not be applied to battery sensors. Thermal lag is expected; a same-time power/temperature association does not establish causation or a thermal model."})
        End If
        rows.Add({"Starting point for corrective checks", "First resolve evidence/communication and mapping problems. Then check source/mode, matched electrical demand, approved ratings, temperature/cooling and corroborating alarms. List the evidence still needed before naming a cause.",
            "Recommendations are verification steps, not switching instructions or permission to work on energized equipment. Physical checks require qualified personnel and approved site isolation procedures."})
        Return rows
    End Function
End Class
