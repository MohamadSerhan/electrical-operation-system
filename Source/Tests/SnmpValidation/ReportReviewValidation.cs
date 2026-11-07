using System.Text;
using System.Text.Json;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class ReportReviewValidation
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
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        HistoryMetric Metric(string id, string name, string unit) => new() { Id = id, Name = name, Unit = unit };
        EquipmentHistoryResult Result(params HistoryMetric[] metrics) => new()
        {
            Request = new EquipmentHistoryRequest
            {
                Equipment = new HistoryEquipment { Id = "__ALL_EQUIPMENT__", Name = "All equipment", Category = "Report validation", IntervalSeconds = 1 },
                Metrics = metrics, FromUtc = start, ToUtc = start.AddDays(33), ThinSeconds = 60
            },
            GeneratedUtc = start.AddDays(33)
        };
        var voltage = Metric("voltage", "T1 | Voltage L1-N", "V");
        var sparse = EquipmentAnalysis.BuildFromSeries(Result(voltage), new[]
        {
            Tuple.Create(voltage, 4, new List<ReportPoint>
            {
                new(start.AddDays(26), 230), new(start.AddDays(26).AddMinutes(1), 0),
                new(start.AddDays(26).AddMinutes(2), 230), new(start.AddDays(32), 230)
            })
        });
        var drop = sparse.Findings.Single(finding => finding.Title == "Voltage drop");
        Check(drop.EvidenceValue == 0 && drop.ReferenceValue == 230 && drop.DeviationPercent == -100,
            "Voltage event retains measured zero, median reference and exact percentage change.");
        Check(drop.PeakUtc == start.AddDays(26).AddMinutes(1), "Event evidence retains the extreme timestamp.");
        Check(drop.Detail.Contains("duration unknown") && !drop.Detail.Contains("stayed"), "One observation never becomes a fabricated event duration.");
        Check(sparse.Completeness == 100 && sparse.ObservedCoveragePercent < 1, "Saved validity does not hide unobserved days or requested window edges.");
        Check(sparse.Findings.Any(finding => finding.Title == "Limited time coverage"), "Sparse study emits a coverage limitation.");
        Check(!sparse.Channels[0].Summary.Contains("% of the time"), "Sample percentages are not claimed as time percentages.");
        var gap = sparse.Findings.Single(finding => finding.Title == "Recording gaps");
        Check(gap.EvidenceValue > 0 && gap.EvidenceUnit == "s" && gap.ReferenceValue > 0, "Gap duration and cadence threshold are numeric evidence.");

        var phases = Enumerable.Range(1, 3).Select(i => Metric("phase" + i, "T2 | Voltage L" + i + "-N", "V")).ToArray();
        var balanced = EquipmentAnalysis.BuildFromSeries(Result(phases), phases.Select((metric, index) =>
            Tuple.Create(metric, 3, new List<ReportPoint>
            {
                new(start, 230), new(start.AddSeconds(1), index == 0 ? 0 : 230), new(start.AddSeconds(2), 230)
            })));
        var balance = balanced.Findings.Single(finding => finding.Title == "T2 - Voltage balance");
        Check(Math.Abs(balance.EvidenceValue!.Value - 100) < 0.0001, "Phase balance uses the largest absolute deviation, including the lowest phase.");
        Check(balance.EvidenceUnit == "%" && balance.ReferenceValue == 2, "Balance report preserves percent and warning guide.");

        var power = Metric("power", "UPS-A01 | Output Apparent Power", "kVA");
        power.RatedCapacityKva = 100;
        power.CapacityBasis = "Verified test rating; this deliberately long basis must remain fully visible without ellipsis.";
        var capacity = EquipmentAnalysis.BuildFromSeries(Result(power), new[]
        {
            Tuple.Create(power, 3, new List<ReportPoint> { new(start, 80), new(start.AddSeconds(1), 90), new(start.AddSeconds(2), 110) })
        });
        var rating = capacity.Findings.Single(finding => finding.Title == "Rated capacity reached or exceeded");
        Check(rating.EvidenceValue == 110 && rating.ReferenceValue == 100 && Math.Abs(rating.DeviationPercent!.Value - 10) < 0.0001,
            "Capacity evidence compares against nameplate, not the recording peak.");
        Check(capacity.Channels[0].P95 == 110, "Nearest-rank P95 is correct for a three-sample series.");

        var even = EquipmentAnalysis.BuildFromSeries(Result(voltage), new[] {
            Tuple.Create(voltage, 4, new List<ReportPoint> { new(start, 220), new(start.AddSeconds(1), 230),
                new(start.AddSeconds(2), 240), new(start.AddSeconds(3), 250) }) });
        Check(even.Channels[0].Median == 235, "Even-sized samples use the ordinary mean of the two central values.");
        var empty = Metric("empty", "Unavailable channel", "A");
        var missing = EquipmentAnalysis.BuildFromSeries(Result(voltage, empty), new[] {
            Tuple.Create(voltage, 4, even.Channels[0].Points), Tuple.Create(empty, 0, new List<ReportPoint>()) });
        Check(Math.Abs(missing.ObservedCoveragePercent - even.ObservedCoveragePercent / 2) < 1e-10,
            "A selected channel with no data contributes zero coverage instead of disappearing from assurance.");
        Check(missing.Scope.Count == 2 && missing.Findings.Any(f => f.Title == "No valid data"),
            "Unavailable selected channels remain in scope and emit an explicit finding.");
        Check(sparse.StudyName == "Selected equipment / selected channels" && !sparse.Summary.Contains("of All equipment"),
            "Combined study wording cannot imply that every parameter or equipment was assessed.");
        Check(balanced.CoincidentGroups.Any(group => group.Length >= 2),
            "Simultaneous phase deviation and balance flags are visibly related without declaring separate incidents.");
        var twoPhase = EquipmentAnalysis.BuildFromSeries(Result(phases.Take(2).ToArray()), phases.Take(2).Select((metric, i) =>
            Tuple.Create(metric, 2, new List<ReportPoint> { new(start, i == 0 ? 0 : 230), new(start.AddSeconds(1), 230) })));
        Check(!twoPhase.Findings.Any(f => f.Title.Contains("Voltage balance")), "Two selected phases cannot establish three-phase balance.");
        var duplicates = EquipmentAnalysis.BuildFromSeries(Result(phases), phases.Select((metric, i) =>
            Tuple.Create(metric, i == 0 ? 2 : 1, i == 0
                ? new List<ReportPoint> { new(start, 0), new(start, 230) }
                : new List<ReportPoint> { new(start, 230) })));
        Check(!duplicates.Findings.Any(f => f.Title.Contains("Voltage balance")),
            "Duplicate phase observations cannot substitute for a complete unambiguous three-phase acquisition.");
        var temp = Metric("temperature", "TDEGC", "deg C");
        var rate = EquipmentAnalysis.BuildFromSeries(Result(temp), new[] {
            Tuple.Create(temp, 3, new List<ReportPoint> { new(start, 40), new(start.AddSeconds(30), 42),
                new(start.AddSeconds(60), 43) }) });
        var fast = rate.Findings.Single(f => f.Title == "Fast temperature rise");
        Check(fast.StartUtc == start && fast.EndUtc == start.AddSeconds(30) && fast.EvidenceValue == 4,
            "Rate evidence uses its real source samples, not a fabricated two-minute start.");
        Check(rate.Channels[0].Summary.Contains("do not assume winding/hot-spot"), "Unverified temperature provenance remains explicit in reports.");
        var pf = Metric("pf", "Power Factor", "PF");
        var pfBoundary = EquipmentAnalysis.BuildFromSeries(Result(pf), new[] {
            Tuple.Create(pf, 3, new List<ReportPoint> { new(start, 1), new(start.AddSeconds(1), .80), new(start.AddSeconds(2), 1) }) });
        Check(pfBoundary.Findings.Single(f => f.Title == "Low power factor").Severity == 2,
            "The exact 0.80 PF critical boundary is not lost to floating-point subtraction.");
        var associationLoad = Metric("assocload", "T1 | Active Power", "kW");
        var associationVoltage = Metric("assocvoltage", "T1 | Voltage L1-N", "V");
        var association = EquipmentAnalysis.BuildFromSeries(Result(associationVoltage, associationLoad), new[] {
            Tuple.Create(associationVoltage, 5, Enumerable.Range(0,5).Select(i =>
                new ReportPoint(start.AddSeconds(i * 30), i == 4 ? 0 : 230)).ToList()),
            Tuple.Create(associationLoad, 5, Enumerable.Range(0,5).Select(i =>
                new ReportPoint(start.AddSeconds(i * 30), i == 4 ? 200 : 100)).ToList()) });
        Check(association.Findings.Any(f => f.Category == "Correlation" && f.Detail.Contains("not proof")) &&
            association.Findings.All(f => !f.Detail.Contains("most likely") && !f.Detail.Contains("cause is therefore")),
            "Co-occurring sample means generate descriptive associations, not an asserted physical root cause.");
        var interrupted = Result(voltage);
        interrupted.Request.ThinSeconds = 0;
        interrupted.Request.ToUtc = start.AddSeconds(3);
        interrupted.Rows = new[] {
            new EquipmentHistoryRow { MetricId = voltage.Id, TimestampUtc = start, Value = 230, Quality = "Good" },
            new EquipmentHistoryRow { MetricId = voltage.Id, TimestampUtc = start.AddSeconds(1), Quality = "Timeout" },
            new EquipmentHistoryRow { MetricId = voltage.Id, TimestampUtc = start.AddSeconds(2), Value = 230, Quality = "Good" } };
        var interruptedAnalysis = EquipmentAnalysis.Build(interrupted);
        Check(interruptedAnalysis.Channels[0].BreakBefore.Contains(start.AddSeconds(2)) &&
            interruptedAnalysis.ObservedCoveragePercent == 0, "Known invalid intermediate samples break lines and continuity coverage.");
        Check(Math.Abs(interruptedAnalysis.Completeness - 200.0 / 3) < 1e-10,
            "Saved-sample validity accounts for explicit invalid rows.");
        EquipmentHistoryRow Event(string equipment, string condition, string state, int second = 0) => new() {
            EquipmentId = equipment, ConditionKey = condition, ConditionName = "Common fault label", ObservationId = equipment + condition + second,
            TimestampUtc = start.AddSeconds(second), EventKind = "ObservedChange", EventState = state,
            EventSeverity = state == "Active" ? "Critical" : "Normal", EventCategory = "Fault" };
        var events = Result(voltage);
        events.AlarmEvents = new[] { Event("T1", "fault", "Cleared"), Event("T2", "fault", "Active"),
            Event("T1", "fault", "Cleared"), Event("T1", "other", "Unknown") };
        var eventAnalysis = EquipmentAnalysis.Build(events);
        Check(eventAnalysis.Events.Count == 3 && eventAnalysis.Events.Sum(e => e.Count) == 3,
            "Repeated projections are deduplicated but equipment IDs and stable conditions stay separate.");
        Check(eventAnalysis.Events.Single(e => e.EquipmentId == "T2").Severity == 2 &&
            eventAnalysis.Events.Where(e => e.EquipmentId == "T1").All(e => e.Severity == 0),
            "Cleared/unknown fault-category observations do not become active critical faults.");
        var settings = new RecordingSettings();
        foreach (var id in new[] { "T1", "T2", "T3", "T4" }) settings.GetAnalyzer(id).RatedKva = 100;
        settings.GetAnalyzer("TOTAL").RatedKva = 999;
        var totalRating = HistoryCapacity.Transformer(settings, "TOTAL");
        Check(totalRating.Item1 == 400 && totalRating.Item2.Contains("differs"),
            "Factory rating uses a complete transformer sum and discloses a conflicting TOTAL setting.");
        var copy = power.WithCapacity(Tuple.Create((double?)200, "Current test source"));
        Check(copy.RatedCapacityKva == 200 && power.RatedCapacityKva == 100 && copy.IsPower == power.IsPower,
            "Per-equipment capacity cloning preserves metric semantics and never mutates shared catalog entries.");

        var pairX = Metric("px", "Active Power", "kW");
        var pairY = Metric("py", "TDEGC", "deg C");
        var comparisonResult = new EquipmentHistoryResult { Request = new EquipmentHistoryRequest {
            Equipment = new HistoryEquipment { Id = "T1", Name = "Transformer 1", IntervalSeconds = 1 },
            Metrics = new[] { pairX, pairY }, FromUtc = start, ToUtc = start.AddSeconds(20), RawOnly = true } };
        var pairRows = new List<EquipmentHistoryRow>();
        for (var i = 0; i < 12; i++)
        {
            pairRows.Add(new() { MetricId = pairX.Id, EquipmentId = "T1", TimestampUtc = start.AddSeconds(i),
                Value = i / 4 * 100 + 100, Quality = "Good", ObservationId = i.ToString() });
            pairRows.Add(new() { MetricId = pairY.Id, EquipmentId = "T1", TimestampUtc = start.AddSeconds(i),
                Value = 40 + i / 4, Quality = i == 5 ? "Timeout" : "Good", ObservationId = i.ToString() });
        }
        comparisonResult.Rows = pairRows;
        foreach (var mode in Enum.GetValues<ChannelComparisonStyle>())
        {
            var study = ChannelComparisonStudy.Create(comparisonResult, pairX, pairY, mode, true);
            Check(study.Pairs.Length == 11 && study.CandidateAcquisitions == 12 && study.ExcludedAcquisitions == 1,
                "Comparison explicitly reports matched/excluded acquisition groups.");
            Check(ChannelComparisonBin.Build(study.Pairs).Sum(bin => bin.Count) == study.Pairs.Length,
                "Response bins account for every valid paired acquisition exactly once.");
            Check(!study.Pairs.Single(p => p.TimestampUtc == start.AddSeconds(6)).ConnectFromPrevious,
                "Comparison path cannot bridge a failed pair.");
            var comparisonPath = Path.Combine(output, "comparison-" + mode + ".pdf");
            Task.Run(() => HistorianReportExporter.ExportEquipmentHistory(comparisonResult, comparisonPath, "PDF", study)).GetAwaiter().GetResult();
            var comparisonText = File.ReadAllText(comparisonPath, Encoding.ASCII);
            Check(comparisonText.Contains("Selected X/Y comparison") && comparisonText.Contains(study.ModeName) &&
                comparisonText.Contains("11 matched saved acquisitions") && comparisonText.Contains("1 excluded"),
                "Real PDF export includes the selected axes/mode, pairing count and exclusions.");
            Check(comparisonText.Contains("Comparison response - numeric evidence") &&
                comparisonText.Contains("Aligned time traces") && comparisonText.Contains("Descriptive response"),
                "Comparison PDF includes numeric response, cautious interpretation and requested aligned traces.");
            Check(!comparisonText.Contains("HEALTH SCORE") && !comparisonText.Contains("vs start") &&
                comparisonText.Contains("Full selected period") && comparisonText.Contains("Data assurance"),
                "PDF has complete-period context and assurance without misleading health scores or deltas.");
        }
        comparisonResult.Request.IsCombined = true;
        var rejectedCombined = false;
        try { ChannelComparisonStudy.Create(comparisonResult, pairX, pairY, ChannelComparisonStyle.RawPoints, false); }
        catch (ArgumentException) { rejectedCombined = true; }
        Check(rejectedCombined, "Cross-equipment numeric comparisons fail explicitly instead of exporting a misleading plot.");
        comparisonResult.Request.IsCombined = false;
        pairRows.RemoveAll(row => row.MetricId == pairY.Id);
        var noPairs = ChannelComparisonStudy.Create(comparisonResult, pairX, pairY, ChannelComparisonStyle.MeanResponse, true);
        var emptyComparison = EquipmentAnalysis.Build(comparisonResult);
        emptyComparison.Comparison = noPairs;
        var noPairsPath = Path.Combine(output, "comparison-no-valid-pairs.pdf");
        EquipmentReportRenderer.WritePdf(emptyComparison, noPairsPath);
        Check(File.ReadAllText(noPairsPath, Encoding.ASCII).Contains("No valid exact acquisition pairs"),
            "No paired evidence produces an explicit limitation, not an invented response curve.");

        var many = Enumerable.Range(1, 15).Select(i =>
            Metric("many" + i, (i <= 12 ? "T" + ((i - 1) / 3 + 1) : "TOTAL") + " | Voltage L" + ((i - 1) % 3 + 1) + "-N", "V")).ToArray();
        var crowded = EquipmentAnalysis.BuildFromSeries(Result(many), many.Select((metric, i) =>
            Tuple.Create(metric, 120, Enumerable.Range(0, 120).Select(j =>
                new ReportPoint(start.AddDays(26).AddMinutes(j), j % 20 == 5 ? 0 : j % 20 == 10 ? 280 + i : 230 + i * 0.1)).ToList())));
        Check(crowded.Findings.Count(finding => finding.Severity >= 1 && finding.StartUtc.HasValue) > 24,
            "Crowded fixture exceeds the previous silently truncated event register.");
        var report = Path.Combine(output, "report-review-regression.pdf");
        EquipmentReportRenderer.WritePdf(crowded, report);
        var text = File.ReadAllText(report, Encoding.ASCII);
        Check(text.Contains("Event evidence register") && text.Contains("MEASURED / PEAK UTC") &&
            text.Contains("REFERENCE / BASIS") && text.Contains("-100.0 %"), "PDF includes the numeric event table, not event names alone.");
        Check(text.Contains("How this report was made - method & limitations") && text.Contains("Loaded resolution") &&
            text.Contains("short events and true extrema"), "Methodology exposes sampling and analysis limitations.");
        Check(crowded.Channels[0].Findings.Count(f => f.Title == "Voltage drop") == 6 &&
            !crowded.Findings.Any(f => f.Title.Contains("further episodes")),
            "All six detected threshold runs are retained rather than silently limiting evidence to three.");
        Check(!text.Contains("Peer comparison - selected equipment in the same family") && !text.Contains("VS PEER AVG") &&
            text.Contains("Electrical relationship - comparison permissions") && text.Contains("T1 channels"),
            "Unrelated equipment are not ranked or overlaid as peers; same-equipment phase charts and topology limits remain explicit.");
        Check(!text.Contains("DATA COMPLETENESS") && !text.Contains("No corrective action is required"),
            "PDF no longer makes unsupported completeness or equipment-condition claims.");
        foreach (var finding in crowded.Findings.Where(finding => finding.Severity >= 1 && finding.StartUtc.HasValue))
            Check(text.Contains(finding.StartUtc!.Value.ToString("yyyy-MM-dd")), "Every timed event date is retained in the searchable PDF.");
        Check(text.EndsWith("%%EOF"), "Regression PDF is complete.");
        File.WriteAllText(Path.Combine(output, "report-review-assertions.json"), JsonSerializer.Serialize(new
        {
            Assertions = assertions,
            TimedFindings = crowded.Findings.Count(finding => finding.Severity >= 1 && finding.StartUtc.HasValue),
            sparse.Completeness,
            sparse.ObservedCoveragePercent
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Report review assertions passed: {assertions}.");
    }
}
