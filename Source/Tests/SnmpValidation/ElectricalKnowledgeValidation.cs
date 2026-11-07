using System.Text;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class ElectricalKnowledgeValidation
{
    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var assertions = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
        var epoch = new DateTime(2026, 11, 3, 10, 0, 0, DateTimeKind.Utc);
        HistoryMetric Metric(string id, string name, string unit) => new() { Id = id, Name = name, Unit = unit };
        EquipmentAnalysis Analyse(params Tuple<HistoryMetric, int, List<ReportPoint>>[] series)
        {
            var metrics = series.Select(item => item.Item1).ToArray();
            var result = new EquipmentHistoryResult
            {
                Request = new EquipmentHistoryRequest
                {
                    Equipment = new HistoryEquipment { Id = "ANALYZER", Name = "Electrical evidence test", IntervalSeconds = 1 },
                    Metrics = metrics, FromUtc = epoch, ToUtc = epoch.AddSeconds(121)
                }
            };
            return EquipmentAnalysis.BuildFromSeries(result, series);
        }
        Tuple<HistoryMetric, int, List<ReportPoint>> Series(HistoryMetric metric, params double[] values) =>
            Tuple.Create(metric, values.Length, values.Select((value, index) => new ReportPoint(epoch.AddSeconds(index * 30), value)).ToList());
        var batteryV = Metric("BatteryVoltage", "UPS-B01 | Battery Voltage", "V");
        var batteryI = Metric("BatteryCurrent", "UPS-B01 | Battery Current", "A");
        var batteryT = Metric("BatteryTemperature", "UPS-B01 | Battery Temperature", "deg C");
        var bus = Metric("DcBusVoltage", "UPS-B01 | DC Bus Voltage", "V");
        Check(ElectricalReportKnowledge.IsDc(batteryV) && ElectricalReportKnowledge.IsDc(bus),
            "Battery and DC bus are explicitly classified as DC.");
        Check(ElectricalReportKnowledge.IsDc(Metric("battery_amps", "anonymous saved channel", "A")),
            "Historian DC IDs retain classification even with an abbreviated label.");
        Check(!ElectricalReportKnowledge.IsDc(Metric("UPS-BATTERY:InputVoltage", "UPS Battery room | Input Voltage L1-N", "V")),
            "An equipment label containing battery does not misclassify its AC input channel as DC.");
        var dc = Analyse(Series(batteryV, 480, 480, 420, 480), Series(bus, 700, 700, 620, 700),
            Series(batteryI, -20, -5, 0, 5), Series(batteryT, 90, 95, 99, 99));
        Check(!dc.Findings.Any(f => f.Title is "Voltage drop" or "Voltage rise" or "High current" or "High temperature"),
            "DC readings do not invoke AC voltage/current or transformer absolute-temperature guides.");
        var dcDrops = dc.Findings.Where(f => f.Title == "DC voltage decrease").ToArray();
        Check(dcDrops.Length == 2 && dcDrops.All(f => f.Severity == 0 && f.EvidenceValue.HasValue && f.ReferenceValue.HasValue),
            "DC voltage changes retain numeric evidence without inventing rated-voltage alarm limits.");
        Check(dc.Findings.Where(f => f.Metric.Contains("Battery") || f.Metric.Contains("DC Bus"))
                .All(f => !f.Advice.Contains("tap", StringComparison.OrdinalIgnoreCase) && !f.Cause.Contains("oil", StringComparison.OrdinalIgnoreCase)),
            "Battery/DC investigation does not recommend AC taps or transformer oil checks.");
        Check(dc.Channels.Single(c => c.Metric.Id == batteryI.Id).Summary.Contains("signed values") &&
              !dc.Channels.Single(c => c.Metric.Id == batteryI.Id).Summary.Contains("average/peak ratio"),
            "Signed battery current remains numeric evidence, not an invalid load ratio.");
        var signed = Metric("signed", "T1 | Active Power", "kW");
        var net = Analyse(Series(signed, -100, 100, -1, 1));
        Check(!net.Findings.Any(f => f.Title.StartsWith("High load") || f.Title.StartsWith("Sudden load")),
            "Zero-mean import/export readings do not produce astronomical above-average load flags.");
        var crossing = Analyse(Series(signed, -100, 101, -1, 1));
        Check(!crossing.Findings.Any(f => f.Title.StartsWith("High load")),
            "A small positive net mean with negative observations is not an AC overload denominator.");
        var acV = Metric("acV", "UPS-B01 | Input Voltage L1-N", "V");
        var ac = Analyse(Series(acV, 230, 230, 200, 230));
        Check(ac.Findings.Any(f => f.Title == "Voltage drop" && f.Severity >= 1),
            "Existing AC median-deviation screening remains available.");
        Check(ElectricalReportKnowledge.ComparisonQuantity(acV) != ElectricalReportKnowledge.ComparisonQuantity(batteryV),
            "AC voltage and battery DC never share the automatic native-unit comparison chart.");
        Check(ElectricalReportKnowledge.ComparisonQuantity(batteryV) != ElectricalReportKnowledge.ComparisonQuantity(bus),
            "Battery terminals and the DC bus are not treated as the same measurement point.");
        Check(ElectricalReportKnowledge.ComparisonQuantity(acV) !=
              ElectricalReportKnowledge.ComparisonQuantity(Metric("out", "UPS-B01 | Output Voltage L1-N", "V")),
            "Input and output voltage comparison families remain separate.");
        Check(ElectricalReportKnowledge.ComparisonQuantity(acV) !=
              ElectricalReportKnowledge.ComparisonQuantity(Metric("ll", "UPS-B01 | Input Voltage L1-L2", "V")),
            "Line-line and line-neutral comparisons never use one axis family.");
        var checklist = ElectricalReportKnowledge.Checklist(dc);
        Check(checklist.Any(row => row[0] == "Battery / DC interpretation") &&
              checklist.Any(row => row[0] == "DC limits, runtime & efficiency"),
            "Selected DC evidence brings in battery-specific prerequisites and cautious runtime/efficiency rules.");
        Check(!ElectricalReportKnowledge.Checklist(ac).Any(row => row[0] == "Battery / DC interpretation"),
            "DC-specific analysis steps are scoped to selected DC channels.");
        Check(checklist.SelectMany(row => row).Any(text => text.Contains("No manufacturer datasheets")),
            "Manufacturer references and missing chemistry/cell-count data are not fabricated.");
        var workflow = ElectricalReportKnowledge.Workflow();
        Check(workflow.Count == 7 && workflow.Select((row, i) => row[0].StartsWith((i + 1) + ". ")).All(value => value),
            "Engineering instructions have seven explicit ordered gates, not an unordered list of guesses.");
        Check(workflow.All(row => row.Length == 3 && row.All(text => text.Length > 0)) &&
              workflow[2][0].Contains("Electrical relationship") && workflow[3][0].Contains("Calculation"),
            "Every gate includes execution requirements and prohibitions; topology precedes calculation.");
        var metadataDc = Metric("channel:BatteryPower:L1", "anonymous saved channel", "kW");
        Check(ElectricalReportKnowledge.IsDc(metadataDc),
            "DC classification preserves a native channel key with a phase suffix, not only the final L1 token.");
        var dcPower = Analyse(Series(metadataDc, 1, 1, 1, 100));
        Check(!dcPower.Findings.Any(f => f.Title.StartsWith("High load") || f.Title.StartsWith("Sudden load")),
            "Battery/DC power does not invoke AC above-average load and switching rules.");
        Check(ElectricalReportKnowledge.Investigation("Sudden power step", metadataDc).StartsWith("Not established") &&
              ElectricalReportKnowledge.Investigation("Sudden power step", metadataDc).Contains("battery/DC measurement point"),
            "Even anonymous DC evidence uses metadata-aware cautious investigation instructions.");
        var pfMetric = Metric("@OutputPf", "UPS-B01 | Output Power Factor", "");
        var pf = Analyse(Series(pfMetric, .7, .75, .8, .8));
        Check(pf.Findings.Where(f => f.Title == "Low power factor").All(f =>
              f.Cause.Contains("true PF versus displacement PF") && f.Advice.Contains("harmonic evidence") &&
              !f.Advice.StartsWith("Check reactive-power compensation")),
            "UPS low PF does not infer failed compensation or prescribe capacitor actions.");
        var impossiblePf = Analyse(Series(pfMetric, 1.2, -.5, .8, .9));
        Check(impossiblePf.Findings.Any(f => f.Title == "PF outside physical range" && f.Category == "Data quality" && f.EvidenceValue == 1.2) &&
              impossiblePf.Channels.Single().Points.Count == 4,
            "Impossible PF is explicitly flagged without clamping or deleting signed/raw source evidence.");
        Check(ac.Findings.Where(f => f.Title == "Voltage drop").All(f =>
              f.Cause.StartsWith("Not established") && !f.Cause.Contains("tap-changer problem") && f.Advice.Contains("active feeder")),
            "AC voltage deviations require measured source evidence, not automatic tap/motor/generator diagnoses.");
        var unspecifiedVoltage = Metric("unspecified", "UPS-B01 | Input Voltage", "V");
        Check(ElectricalReportKnowledge.ComparisonQuantity(unspecifiedVoltage) != ElectricalReportKnowledge.ComparisonQuantity(acV),
            "An unspecified voltage reference is not silently grouped with explicit L-N phases.");
        var nativeInput = Metric("in_v_l1", "Anonymous source", "V");
        Check(ElectricalReportKnowledge.MeasurementSide(nativeInput) == "Input",
            "Historian native measurement ID preserves input node when the visible label is abbreviated.");
        var inputKva = Metric("input", "UPS-B01 | Input Apparent Power", "kVA");
        inputKva.RatedCapacityKva = 100;
        Check(!Analyse(Series(inputKva, 120, 130, 140)).Findings.Any(f => f.Category == "Capacity"),
            "Input apparent power never gets an output-nameplate capacity finding.");
        var frequency = Analyse(Series(Metric("freq", "UPS-B01 | Output Frequency", "Hz"), 50, 50, 48, 50));
        Check(frequency.Findings.Where(f => f.Title == "Frequency deviation").All(f =>
              f.Detail.Contains("inferred screening reference, not verified nominal") && f.Advice.Contains("approved nominal")),
            "50/60 Hz heuristics are disclosed as inferred screening, not verified source/nominal compliance.");
        Tuple<HistoryMetric, int, List<ReportPoint>> Gapped(string equipment) => Tuple.Create(
            Metric(equipment + "gap", equipment + " | Voltage L1-N", "V"), 6,
            new[] { 0, 1, 2, 100, 101, 102 }.Select(second => new ReportPoint(epoch.AddSeconds(second), 230)).ToList());
        var gaps = Analyse(Gapped("T1"), Gapped("UPS-B01"));
        Check(gaps.Findings.Count(f => f.Title == "Recording gaps") == 2 &&
              gaps.Findings.Where(f => f.Title == "Recording gaps").Select(f => f.EquipmentId).Distinct().Count() == 2,
            "Coincident gaps on unrelated feeds remain independent and preserve equipment identity.");
        var all = new List<Tuple<HistoryMetric, int, List<ReportPoint>>>();
        foreach (var equipment in new[] { "T1", "T2" })
        {
            for (var phase = 1; phase <= 3; phase++)
                all.Add(Series(Metric(equipment + "I" + phase, equipment + " | Current L" + phase, "A"), 100, 10, 1000));
            all.Add(Series(Metric(equipment + "N", equipment + " | Neutral Current", "A"),
                equipment == "T1" ? 60 : 90, 3, 200));
        }
        var neutral = Analyse(all.ToArray());
        var neutralFindings = neutral.Findings.Where(f => f.Title == "High neutral current").ToArray();
        Check(neutralFindings.Length == 2, "Every equipment neutral is checked, not just the first equipment.");
        foreach (var finding in neutralFindings)
            Check(finding.PeakUtc == epoch && finding.ReferenceValue == 20 && finding.EvidenceUnit == "A" &&
                  finding.EvidenceValue == (finding.Metric.StartsWith("T1") ? 60 : 90),
                "Neutral ratio uses the same-acquisition three-phase mean, not a different-time peak/period mean.");
        Check(neutralFindings.All(finding => finding.DeviationPercent > 0 && finding.Detail.Contains("average 100.0")),
            "Neutral numeric reference is the 20%-of-phase-mean guide, not an unrelated denominator.");
        var asynchronous = all.Take(4).Select(item => Tuple.Create(item.Item1, item.Item2,
            item.Item3.Select(point => new ReportPoint(point.T.AddMilliseconds(item.Item1.Name.Contains("Neutral") ? 1 : 0), point.V)).ToList())).ToArray();
        Check(!Analyse(asynchronous).Findings.Any(f => f.Title == "High neutral current"),
            "Unsynchronized neutral and phase observations are not compared.");
        var duplicate = all.Take(4).ToArray();
        duplicate[1] = Series(Metric("duplicate", "T1 | Current L1", "A"), 100, 10, 1000);
        Check(!Analyse(duplicate).Findings.Any(f => f.Title == "High neutral current" || f.Title.Contains("Current balance")),
            "A duplicate phase cannot stand in for the missing L2 in neutral or phase-balance screening.");
        var combined = Analyse(Series(batteryV, 480, 480, 420, 480), Series(batteryI, -20, -5, 0, 5),
            Series(batteryT, 90, 95, 99, 99), Series(acV, 230, 230, 200, 230),
            Series(Metric("kw", "UPS-B01 | Output Power", "kW"), 25, 28, 30, 29));
        var pdfPath = Path.Combine(output, "electrical-engineering-evidence.pdf");
        EquipmentReportRenderer.WritePdf(combined, pdfPath);
        var pdf = File.ReadAllText(pdfPath, Encoding.ASCII);
        Check(pdf.Contains("Electrical interpretation - evidence and calculation prerequisites") &&
              pdf.Contains("AC loading & calculations") && pdf.Contains("Battery / DC interpretation") &&
              pdf.Contains("same battery/bus") && pdf.Contains("not active kW"),
            "PDF renders the scoped electrical checks and calculation prerequisites.");
        Check(pdf.Contains("Battery sensors are excluded") && pdf.EndsWith("%%EOF"),
            "The report discloses battery-specific exclusions and finalizes completely.");
        Check(pdf.Contains("1. Scope and identity") && pdf.Contains("7. Conclusion and release checks") &&
              pdf.Contains("If a prerequisite fails") && !pdf.Contains("Possible cause:"),
            "PDF renders the ordered instructions, failed-prerequisite rule and unproven-hypothesis labels.");
        Console.WriteLine($"PASS: {assertions} electrical reasoning assertions.");
        UpsPowerBalanceValidation.Run(Path.Combine(output, "ups-power-balance"));
        TopologyComparisonValidation.Run(Path.Combine(output, "electrical-topology"));
    }
}
