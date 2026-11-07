using System.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EOEMS.PowerTransformersScadaUI.V5;
using Microsoft.VisualBasic.FileIO;

internal static class UpsPowerBalanceValidation
{
    internal static void ReviewCsv(string csvPath, string appDirectory, string output)
    {
        Directory.CreateDirectory(output);
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(appDirectory, "EOEMSData", "Configuration", "ups-settings.json")));
        var devices = settings.RootElement.GetProperty("Devices").EnumerateArray().ToDictionary(
            device => device.GetProperty("Id").GetString()!, device => device);
        using var parser = new TextFieldParser(csvPath) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true };
        parser.SetDelimiters(",");
        var header = parser.ReadFields() ?? throw new InvalidDataException("Missing analyzer CSV header.");
        var series = new Dictionary<int, Tuple<HistoryMetric, int, List<ReportPoint>>>();
        for (var i = 2; i < header.Length; i++)
        {
            if (!header[i].Contains(" | ") || header[i].EndsWith("reading quality") || header[i].EndsWith("acquisition UTC")) continue;
            var parts = header[i].Split(" | ", 2);
            var id = parts[0];
            var label = parts[1];
            var unit = Regex.Match(label, @" \[([^\]]+)\]$");
            if (unit.Success) label = label[..unit.Index];
            var phase = Regex.Match(label, @" L([123])(?:-N|-L[123])?$");
            var key = phase.Success ? label[..phase.Index] : label;
            var estimate = UpsInputEstimates.Metrics().SingleOrDefault(metric => metric.Name == label ||
                (label == "Estimated Input Apparent Power (balanced)" && metric.Id == UpsInputEstimates.KvaKey));
            key = key switch
            {
                "Output Power (total)" => "@OutputKw",
                "Output Apparent Power (total)" => "@OutputKva",
                "Output Power Factor (total)" => "@OutputPf",
                _ => key.Replace(" ", "")
            };
            if (estimate is not null) key = estimate.Id;
            var device = devices[id];
            var atsBank = device.GetProperty("AtsBank").GetInt32();
            var bank = settings.RootElement.GetProperty("Banks").EnumerateArray().FirstOrDefault(item => item.GetProperty("Number").GetInt32() == atsBank);
            var metric = new HistoryMetric
            {
                Id = "CSV" + i, EquipmentId = id, Name = id + " | " + label,
                SourceMetricId = key, SourceMetricName = label, SourcePhase = phase.Success ? int.Parse(phase.Groups[1].Value) : 0,
                VoltageReference = label.EndsWith("-N") ? "L-N" : Regex.IsMatch(label, @"L[123]-L[123]$") ? "L-L" : "",
                Unit = unit.Success ? unit.Groups[1].Value : "",
                IsEstimate = estimate is not null,
                EquipmentModel = device.GetProperty("Model").GetString()!,
                ModelBasis = "Current registry; not historical model verification or a datasheet",
                RatedCapacityKva = device.GetProperty("RatedKva").GetDouble(),
                CapacityBasis = "Current registry; historical rating unverified",
                AtsBankNumber = atsBank,
                ConfiguredSourceDescription = bank.ValueKind == JsonValueKind.Undefined ? "" : bank.GetProperty("SourceDescription").GetString()!,
                TopologyBasis = "Current configured ATS membership/source description only; approved time-valid wiring and active ATS source are not recorded",
                Basis = "Original analyzer CSV; actual per-equipment acquisition UTC. Voltage reference follows the analyzer's selected and guarded L-N/L-L channel, not a conversion."
            };
            if (estimate is not null)
            {
                metric.Basis = "Original analyzer CSV, separately recorded estimate. " + estimate.Basis;
                metric.VoltageReference = estimate.VoltageReference;
            }
            series.Add(i, Tuple.Create(metric, 0, new List<ReportPoint>()));
        }
        DateTime? first = null, last = null;
        while (!parser.EndOfData)
        {
            var row = parser.ReadFields() ?? throw new InvalidDataException("Missing CSV record.");
            if (row.Length != header.Length) throw new InvalidDataException("Analyzer CSV record width mismatch.");
            foreach (var item in series)
            {
                var id = item.Value.Item1.EquipmentId;
                var acquired = Array.IndexOf(header, id + " | acquisition UTC");
                var quality = Array.IndexOf(header, id + " | reading quality");
                if (acquired < 0 || quality < 0) throw new InvalidDataException("Per-equipment acquisition/quality evidence missing.");
                if (row[acquired].Length == 0) continue;
                var time = DateTime.Parse(row[acquired], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                var value = row[item.Key].Length > 0 && row[quality].StartsWith("Good", StringComparison.Ordinal)
                    ? double.Parse(row[item.Key], CultureInfo.InvariantCulture) : double.NaN;
                item.Value.Item3.Add(new ReportPoint(time, value));
                first = !first.HasValue || time < first ? time : first;
                last = !last.HasValue || time > last ? time : last;
            }
        }
        if (!first.HasValue || !last.HasValue) throw new InvalidDataException("No source acquisitions in analyzer CSV.");
        var equipmentIds = series.Values.Select(item => item.Item1.EquipmentId).Distinct().Order().ToArray();
        var reportName = string.Join("_", equipmentIds.Select(id => Regex.Replace(id, @"[^A-Za-z0-9_-]", "_")));
        var result = new EquipmentHistoryResult
        {
            Request = new EquipmentHistoryRequest
            {
                Equipment = new HistoryEquipment { Id = "ANALYZER", Name = string.Join("/", equipmentIds) + " recorded model and electrical evidence", IntervalSeconds = .1 },
                Metrics = series.Values.Select(item => item.Item1).ToArray(), FromUtc = first.Value, ToUtc = last.Value.AddTicks(1)
            }
        };
        var analysis = EquipmentAnalysis.BuildFromSeries(result, series.Values.Select(item => Tuple.Create(item.Item1, item.Item3.Count, item.Item3)));
        EquipmentReportRenderer.WritePdf(analysis, Path.Combine(output, reportName + "_Electrical_Evidence.pdf"));
        File.WriteAllText(Path.Combine(output, "power-balance-summary.json"), JsonSerializer.Serialize(analysis.UpsPowerBalances,
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: replayed {series.Count} original channels; {analysis.TotalSamples} source-channel observations; " +
            $"{analysis.UpsPowerBalances.Count} independently paired UPS studies.");
        foreach (var study in analysis.UpsPowerBalances)
            Console.WriteLine($"{study.EquipmentId}: model={study.Model}; Pin={study.InputKw}; Pout={study.OutputKw}; " +
                $"observed difference={study.DifferenceKw}; pairs={study.PairedAcquisitions}/{study.CandidateAcquisitions}; apparent AC transfer={study.AcTransferPercent}.");
    }

    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            count++;
        }
        var epoch = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        HistoryMetric Metric(string device, string key, string unit, int phase = 0) => new()
        {
            Id = device + ":" + key + ":" + phase, EquipmentId = device, SourceMetricId = key,
            SourcePhase = phase, Name = device + " | " + key + (phase > 0 ? " L" + phase : ""),
            Unit = unit, EquipmentModel = device == "UPS-A01" ? "Model A (test)" : "Model C (test)",
            ModelBasis = "Synthetic test model; not a manufacturer datasheet",
            RatedCapacityKva = device == "UPS-A01" ? 100 : 200, CapacityBasis = "Synthetic test rating"
        };
        Tuple<HistoryMetric, int, List<ReportPoint>> Series(HistoryMetric metric, params double[] values) =>
            Tuple.Create(metric, values.Length, values.Select((value, i) => new ReportPoint(epoch.AddSeconds(i), value)).ToList());
        EquipmentHistoryResult Result(HistoryMetric[] metrics, EquipmentHistoryRow[]? rows = null) => new()
        {
            Request = new EquipmentHistoryRequest
            {
                Equipment = new HistoryEquipment { Id = "ANALYZER", Name = "UPS power balance regression", IntervalSeconds = 1 },
                Metrics = metrics, FromUtc = epoch, ToUtc = epoch.AddSeconds(30)
            },
            Rows = rows ?? Array.Empty<EquipmentHistoryRow>()
        };
        EquipmentAnalysis Analyse(params Tuple<HistoryMetric, int, List<ReportPoint>>[] series) =>
            EquipmentAnalysis.BuildFromSeries(Result(series.Select(item => item.Item1).ToArray()), series);
        var input = Metric("UPS-A01", "InputPower", "W");
        var outputPower = Metric("UPS-A01", "@OutputKw", "kW");
        var matched = Analyse(Series(input, 10000, 20000), Series(outputPower, 9, 16));
        var study = matched.UpsPowerBalances.Single();
        Check(study.InputKw == 15 && study.OutputKw == 12.5 && study.DifferenceKw == 2.5,
            "W/kW conversion and same-acquisition paired active-power means are exact.");
        Check(Math.Abs(study.AcTransferPercent!.Value - 100 * 25.0 / 30) < 1e-10,
            "AC transfer ratio uses paired power means, not mean ratios or current ratios.");
        Check(study.PairedAcquisitions == 2 && study.CandidateAcquisitions == 2, "Coverage counts paired candidates.");
        Check(study.Interpretation.Contains("observed input-output difference = 2.5 kW") &&
              study.Interpretation.Contains("Apparent AC transfer ratio = 83.33 %") &&
              study.Interpretation.Contains("no higher/lower loss or efficiency ranking"),
            "Per-device conclusion states the numeric AC difference and transfer ratio without ranking unrelated equipment.");
        var unsupportedPhases = Enumerable.Range(1, 3).SelectMany(phase => new[]
        {
            Series(Metric("UPS-A01", "InputPower", "W", phase), double.NaN, double.NaN),
            Series(Metric("UPS-A01", "OutputPower", "W", phase), double.NaN, double.NaN)
        }).ToArray();
        var unsupportedAnalysis = Analyse(unsupportedPhases);
        study = unsupportedAnalysis.UpsPowerBalances.Single();
        Check(study.CandidateAcquisitions == 2 && study.PairedAcquisitions == 0 &&
              study.InputValidAcquisitions == 0 && study.OutputValidAcquisitions == 0,
            "All-invalid source acquisitions remain counted, not misleadingly reported as zero acquisition candidates.");
        Check(study.Interpretation.Contains("unsupported/missing evidence is not zero input power") &&
              study.Interpretation.Contains("Select Output Power (total)") &&
              study.Interpretation.Contains("losses and efficiency cannot be calculated") &&
              study.PowerBasis.Contains("no complete valid same-acquisition phase total"),
            "A1-like unsupported phase measurements produce an actionable missing-power conclusion rather than silence.");
        var withSystemTotal = Analyse(unsupportedPhases.Append(Series(outputPower, 9, 10)).ToArray()).UpsPowerBalances.Single();
        Check(withSystemTotal.ObservedOutputKw == 9.5 && withSystemTotal.InputKw is null &&
              withSystemTotal.Interpretation.Contains("Independent observed output mean = 9.5 kW") &&
              !withSystemTotal.Interpretation.Contains("Select Output Power (total)"),
            "A supported system output total is retained even when all phase watts are unsupported; missing input is not invented.");
        var shifted = Series(outputPower, 9, 16);
        shifted.Item3[0] = new ReportPoint(epoch.AddMilliseconds(1), 9);
        shifted.Item3[1] = new ReportPoint(epoch.AddSeconds(1).AddMilliseconds(1), 16);
        Check(Analyse(Series(input, 10000, 20000), shifted).UpsPowerBalances.Single().PairedAcquisitions == 0,
            "Asynchronous readings are not interpolated into an efficiency calculation.");
        var duplicate = Series(input, 10000, 20000);
        duplicate.Item3.Add(new ReportPoint(epoch, 10000));
        study = Analyse(duplicate, Series(outputPower, 9, 16)).UpsPowerBalances.Single();
        Check(study.PairedAcquisitions == 1 && study.InputKw == 20 && study.DifferenceKw == 4,
            "Duplicate acquisitions are excluded rather than averaged or silently deduplicated.");
        study = Analyse(Series(input, double.NaN, 20000), Series(outputPower, 9, 16)).UpsPowerBalances.Single();
        Check(study.PairedAcquisitions == 1 && study.InputKw == 20, "Invalid values do not enter paired means.");
        foreach (var values in new[] { new[] { 0.0, 2.0 }, new[] { -1.0, 2.0 }, new[] { 1.0, 2.0 }, new[] { 2.0, -1.0 } })
        {
            study = Analyse(Series(Metric("UPS-A01", "input_kw", "kW"), values[0]),
                Series(outputPower, values[1])).UpsPowerBalances.Single();
            Check(study.AcTransferPercent is null && study.ReverseOrInconsistentPairs == 1 &&
                  study.DifferenceKw == values[0] - values[1],
                "Zero/reverse input, reverse output and output-above-input retain signed evidence but withhold efficiency-like ratios.");
        }
        var phases = Enumerable.Range(1, 3).SelectMany(phase => new[]
        {
            Series(Metric("UPS-C01", "InputPower", "W", phase), 1000 * phase),
            Series(Metric("UPS-C01", "OutputPower", "W", phase), 900 * phase)
        }).ToArray();
        study = Analyse(phases).UpsPowerBalances.Single();
        Check(study.InputKw == 6 && study.OutputKw == 5.4 && Math.Abs(study.DifferenceKw!.Value - 0.6) < 1e-10,
            "All three native phase active-power readings are summed with correct units.");
        Check(study.AcTransferPercent == 90, "Complete three-phase AC transfer ratio is correct.");
        Check(Analyse(phases.Take(4).ToArray()).UpsPowerBalances.Single().PairedAcquisitions == 0,
            "Two phases never masquerade as a complete total.");
        Check(Analyse(phases.Append(withTupleReplacement()).ToArray()).UpsPowerBalances.Single().PairedAcquisitions == 0,
            "Ambiguous duplicate phase channels do not create a total.");
        var nativeHistory = phases.Select(item =>
        {
            var metric = item.Item1;
            metric.Id = "channel:" + metric.SourceMetricId + ":L" + metric.SourcePhase;
            metric.SourceMetricId = "";
            metric.SourcePhase = 0;
            return item;
        }).ToArray();
        Check(Analyse(nativeHistory).UpsPowerBalances.Single().AcTransferPercent == 90,
            "Native historian channel IDs retain phase identity without label inference.");
        var currentIn = Metric("UPS-A01", "InputCurrent", "A", 1);
        var currentOut = Metric("UPS-A01", "OutputCurrent", "A", 1);
        var pfIn = Metric("UPS-A01", "InputPowerFactor", "PF");
        var pfOut = Metric("UPS-A01", "@OutputPf", "");
        var electrical = Analyse(Series(currentIn, 20, 30), Series(currentOut, 18, 22),
            Series(pfIn, .9, .8), Series(pfOut, .8, .9));
        study = electrical.UpsPowerBalances.Single();
        Check(study.InputKw is null && study.AcTransferPercent is null,
            "Currents and PF never substitute for active input power.");
        Check(study.CurrentRows.Single()[1] == "25.0 A" && study.CurrentRows.Single()[2] == "20.0 A",
            "Input and output current means use exactly paired same-phase acquisitions.");
        Check(study.PfRows.Single()[1] == "0.850 PF" && study.PfRows.Single()[2] == "0.850 PF",
            "Measured/validated PF is compared dimensionlessly, not as efficiency.");
        Check(Analyse(Series(pfOut, .8)).UpsPowerBalances.Single().PfRows.Single()[2] == "0.800 PF",
            "Recorded output PF is preserved as explicitly independent evidence when input PF is unavailable.");
        Check(Analyse(Series(Metric("UPS-A01", "input_kw", "kW"), 8), Series(outputPower, 9))
            .Findings.Any(finding => finding.Title == "UPS power balance unsuitable for efficiency" && finding.Detail.Contains("1 of 1")),
            "Unsuitable power balance is prominently flagged in the analysis, not just hidden in the numerical table.");
        Check(Analyse(Series(pfIn, 1.2), Series(pfOut, .8)).UpsPowerBalances.Single().PfRows.Single()[1] == "Not calculable",
            "Impossible PF is excluded without clamping.");
        var voltagesAndCurrents = Enumerable.Range(1, 3).SelectMany(phase => new[]
        {
            Series(Metric("UPS-A01", "InputVoltage", "V", phase), 200),
            Series(Metric("UPS-A01", "InputCurrent", "A", phase), 10)
        }).ToArray();
        foreach (var item in voltagesAndCurrents.Where(item => item.Item1.Unit == "V")) item.Item1.VoltageReference = "L-N";
        var derived = voltagesAndCurrents.Concat(new[] { Series(Metric("UPS-A01", "input_kw", "kW"), 4.8),
            Series(outputPower, 4.5), Series(pfOut, .9) }).ToArray();
        Check(Analyse(derived).UpsPowerBalances.Single().PfRows.Any(row => row[0] == "System PF (input P/S)" && row[1] == "0.800 PF"),
            "Input PF derives from active Pin and three exact-acquisition verified L-N voltage/current products.");
        foreach (var item in voltagesAndCurrents.Where(item => item.Item1.Unit == "V")) item.Item1.VoltageReference = "L-L";
        Check(!Analyse(derived).UpsPowerBalances.Single().PfRows.Any(row => row[0] == "System PF (input P/S)"),
            "L-L voltage never masquerades as L-N in derived input apparent power/PF.");
        foreach (var item in voltagesAndCurrents.Where(item => item.Item1.Unit == "V")) item.Item1.VoltageReference = "";
        Check(!Analyse(derived).UpsPowerBalances.Single().PfRows.Any(row => row[0] == "System PF (input P/S)"),
            "An unverified/unspecified voltage reference cannot produce calculated PF.");
        var apparent = Metric("UPS-A01", "InputPower", "kVA");
        Check(Analyse(Series(apparent, 10), Series(outputPower, 9)).UpsPowerBalances.Single().PairedAcquisitions == 0,
            "Apparent kVA is not active kW.");
        Check(Analyse(Series(outputPower, 9)).UpsPowerBalances.Single().ObservedOutputKw == 9,
            "Real output power remains visible even when input power and efficiency cannot be calculated.");
        Check(Analyse(Series(Metric("UPS-A01", "BatteryPower", "kW"), 1), Series(outputPower, 9))
            .UpsPowerBalances.Single().InputKw is null, "Battery/DC power is not AC input power.");
        var pinRow = new EquipmentHistoryRow { EquipmentId = "UPS-A01", MetricId = input.Id, TimestampUtc = epoch,
            AcquiredUtc = epoch.AddMilliseconds(10), ObservationId = "one", Revision = 2, Quality = "Good", Value = 10000 };
        var poutRow = new EquipmentHistoryRow { EquipmentId = "UPS-A01", MetricId = outputPower.Id, TimestampUtc = epoch,
            AcquiredUtc = pinRow.AcquiredUtc, ObservationId = "one", Revision = 2, Quality = "Good", Value = 9 };
        EquipmentAnalysis Raw() => EquipmentAnalysis.Build(Result(new[] { input, outputPower }, new[] { pinRow, poutRow }));
        Check(Raw().UpsPowerBalances.Single().AcTransferPercent == 90, "Historian exact acquisition identity pairs correctly.");
        poutRow.ObservationId = "two";
        Check(Raw().UpsPowerBalances.Single().PairedAcquisitions == 0, "Different observation IDs cannot pair.");
        poutRow.ObservationId = "one"; poutRow.Revision = 3;
        Check(Raw().UpsPowerBalances.Single().PairedAcquisitions == 0, "Different mapping revisions cannot pair.");
        poutRow.Revision = 2; poutRow.AcquiredUtc = epoch.AddMilliseconds(11);
        Check(Raw().UpsPowerBalances.Single().PairedAcquisitions == 0, "Different source-acquired times cannot pair.");
        poutRow.AcquiredUtc = pinRow.AcquiredUtc; poutRow.Quality = "Stale";
        Check(Raw().UpsPowerBalances.Single().PairedAcquisitions == 0, "Stale historian values are explicitly excluded.");
        poutRow.Quality = "Good"; poutRow.EquipmentId = "UPS-C01";
        Check(Raw().UpsPowerBalances.Single().PairedAcquisitions == 0, "Different devices cannot supply one another's power.");
        var both = Analyse(Series(input, 10000, 20000), Series(outputPower, 9, 16),
            Series(Metric("UPS-C01", "input_kw", "kW"), 20, 30), Series(Metric("UPS-C01", "output_kw", "kW"), 18, 27),
            Series(Metric("UPS-C01", "BatteryCurrent", "A"), -5, 5),
            Series(currentIn, 20, 30), Series(currentOut, 18, 22), Series(pfIn, .9, .8), Series(pfOut, .8, .9));
        Check(both.UpsPowerBalances.Count == 2 && both.UpsPowerBalances.Select(item => item.Model).Distinct().Count() == 2 &&
              both.UpsPowerBalances.Select(item => item.RatedKva).Distinct().Count() == 2,
            "Different UPS models and capacities remain distinct in the analysis.");
        var pdfPath = Path.Combine(output, "ups-model-power-balance-evidence.pdf");
        EquipmentReportRenderer.WritePdf(both, pdfPath);
        var pdf = File.ReadAllText(pdfPath, Encoding.ASCII);
        Check(pdf.Contains("UPS input/output power, currents and efficiency evidence") && pdf.Contains("Model A") && pdf.Contains("Model C"),
            "PDF includes the dedicated model-aware engineering evidence table.");
        Check(pdf.Contains("Not established") && pdf.Contains("NOT certified UPS efficiency") && pdf.Contains("battery energy balance") &&
              pdf.Contains("Cross-equipment comparison is disabled"),
            "Unknown charging/mode cannot turn an AC power difference into a proven loss or efficiency ranking.");
        Check(pdf.Contains("2.5 kW") && pdf.Contains("25.0 A") && pdf.EndsWith("%%EOF"),
            "PDF contains the computed numbers, paired currents and finalized output.");
        Check(pdf.Contains("Observed AC difference") && pdf.Contains("observed input-output") && pdf.Contains("difference = 2.5 kW") &&
              both.UpsPowerBalances[0].Interpretation.Contains("observed input-output difference = 2.5 kW"),
            "Both numerical table and explicit conclusion contain per-device power-balance results.");
        Console.WriteLine($"PASS: {count} UPS model/power balance assertions.");

        Tuple<HistoryMetric, int, List<ReportPoint>> withTupleReplacement()
        {
            var metric = Metric("UPS-C01", "InputPower", "W", 1);
            metric.Id += "-duplicate";
            return Series(metric, 1000);
        }
    }
}
