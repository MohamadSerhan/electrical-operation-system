using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class UpsInputEstimateValidation
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static int assertions;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }

    private static UpsSnapshot Snapshot() => new()
    {
        DeviceId = "UPS-EST", Quality = UpsQuality.Good, TimestampUtc = DateTime.UtcNow.AddSeconds(-1),
        InputVoltageReference = "L-L (AB / BC / CA)", OutputVoltageReference = "L-N",
        Phases = new Dictionary<string, double?[]>
        {
            ["InputVoltageLL"] = new double?[] { 400, 402, 398 },
            ["InputVoltage"] = new double?[] { 400, 402, 398 },
            ["InputCurrent"] = new double?[] { 500, 510, 490 }
        },
        Values = new Dictionary<string, double> { ["OutputPower"] = 200000, ["OutputApparentVa"] = 250000 }
    };

    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var sample = Snapshot();
        var expectedV = 400 / Math.Sqrt(3);
        var expectedS = Math.Sqrt(3) * 400 * 500 / 1000;
        Check(Math.Abs(UpsInputEstimates.Calculate(sample, UpsInputEstimates.VoltageKey)!.Value - expectedV) < 1e-10,
            "Equivalent L-N uses mean of all three real line-line voltages, not invented individual phase magnitudes.");
        Check(Math.Abs(UpsInputEstimates.Calculate(sample, UpsInputEstimates.KvaKey)!.Value - expectedS) < 1e-10,
            "Balanced kVA uses mean line-line voltage and mean of all three measured input currents.");
        Check(sample.Phase("InputVoltageLN", 0) is null && sample.InputVoltageReference.StartsWith("L-L") && sample.InputKw is null,
            "Estimates do not replace measured L-N, input reference or missing input active watts.");
        ((Dictionary<string, double>)sample.Values)["OutputPower"] = 100000;
        Check(UpsInputEstimates.Calculate(sample, UpsInputEstimates.KvaKey) == expectedS,
            "Changing output power factor cannot change estimated input kVA or invent input kW.");
        foreach (var key in new[] { "InputVoltageLL", "InputCurrent" })
        foreach (var invalid in new double?[] { null, double.NaN, double.PositiveInfinity, -1, 20000 })
        {
            sample = Snapshot();
            sample.Phases[key][1] = invalid;
            Check(UpsInputEstimates.Calculate(sample, UpsInputEstimates.KvaKey) is null,
                $"Missing/invalid {key} prevents input kVA: {invalid}.");
            Check(!string.IsNullOrEmpty(UpsInputEstimates.MissingReason(sample, UpsInputEstimates.KvaKey)),
                "Unavailable estimate has explicit prerequisite evidence.");
            if (key == "InputVoltageLL")
                Check(UpsInputEstimates.Calculate(sample, UpsInputEstimates.VoltageKey) is null,
                    "Invalid authoritative L-L is not replaced by an older generic alias.");
        }
        sample = Snapshot();
        sample.Phases["InputVoltageLL"][0] = 0;
        Check(UpsInputEstimates.Calculate(sample, UpsInputEstimates.VoltageKey) > 0,
            "One numeric zero is retained in the assumption-based mean, not dropped as unavailable.");
        foreach (var key in new[] { "InputVoltageLL", "InputCurrent" })
            Array.Fill(sample.Phases[key], (double?)0);
        Check(UpsInputEstimates.Calculate(sample, UpsInputEstimates.VoltageKey) == 0 &&
            UpsInputEstimates.Calculate(sample, UpsInputEstimates.KvaKey) == 0, "Complete measured-zero inputs yield zero estimates.");
        sample = Snapshot();
        sample.Quality = UpsQuality.Timeout;
        sample.LastGood = Snapshot();
        Check(UpsInputEstimates.Calculate(sample, UpsInputEstimates.KvaKey) is null &&
            UpsInputEstimates.MissingReason(sample, UpsInputEstimates.KvaKey).Contains("last-good"),
            "Failed acquisition never borrows last-good measurements.");
        sample = Snapshot();
        ((Dictionary<string, double?[]>)sample.Phases).Remove("InputVoltageLL");
        sample.InputVoltageReference = "reference unverified";
        Check(UpsInputEstimates.Calculate(sample, UpsInputEstimates.VoltageKey) is null,
            "Unverified generic voltage is not assumed to be line-line.");
        sample.InputVoltageReference = "L-L";
        Check(UpsInputEstimates.Calculate(sample, UpsInputEstimates.VoltageKey) == expectedV,
            "Verified native L-L alias can support a separate estimate.");
        var metrics = UpsInputEstimates.Metrics();
        Check(metrics.All(metric => metric.IsEstimate && !metric.IsPower && !metric.CanSum &&
            metric.Basis.Contains("ESTIMATE ONLY") && metric.Name.StartsWith("Estimated")),
            "Every estimate has distinct identity, units, provenance and non-accounting metadata.");
        var plan = (HashSet<string>)typeof(UpsAcquisitionService).GetMethod("AnalyzerMeasurementKeys", Static)!.Invoke(null,
            new object[] { new[] { UpsInputEstimates.VoltageKey, UpsInputEstimates.KvaKey }, new UpsCapabilityProfile() })!;
        Check(new[] { "InputVoltageLL", "InputVoltage", "InputCurrent" }.All(plan.Contains) &&
            !plan.Contains("OutputPower") && !plan.Contains("OutputApparentVa") && !plan.Contains("InputPower"),
            "Selected estimates request measured inputs only; no output PF or fabricated active power dependency.");
        var row = UpsHistorian.CreateRecordedRow(new UpsDevice { Id = sample.DeviceId }, sample, sample.TimestampUtc);
        using (var json = JsonDocument.Parse(row.Parameters.Single(parameter => parameter.Name == "channels").Value.GetString()!))
        {
            var estimates = json.RootElement.GetProperty("Estimates");
            Check(estimates.GetProperty("Version").GetInt32() == UpsInputEstimates.EvidenceVersion &&
                estimates.GetProperty("Values").GetProperty(UpsInputEstimates.KvaKey).GetDouble() == expectedS,
                "Historian stores versioned estimates separately from measurements.");
            Check(!json.RootElement.GetProperty("Values").TryGetProperty(UpsInputEstimates.KvaKey, out _) &&
                estimates.GetProperty("Formulas").GetProperty(UpsInputEstimates.KvaKey).GetString()!.Contains("sqrt(3)") &&
                estimates.GetProperty("InputVoltageLl").GetArrayLength() == 3,
                "Formula, balance assumptions and source magnitudes are recorded without polluting measured values.");
        }
        sample = Snapshot();
        sample.Phases = new Dictionary<string, double?[]>
        {
            ["InputVoltageLN"] = new double?[] { 225, 230, 235 },
            ["InputCurrent"] = new double?[] { 10, 20, 30 }
        };
        sample.InputVoltageReference = "L-N";
        Check(UpsInputEstimates.Calculate(sample, UpsInputEstimates.KvaKey) == 13.9 &&
            UpsInputEstimates.Calculate(sample, UpsInputEstimates.VoltageKey) is null && sample.InputKw is null,
            "C/D verified input L-N uses matched phase V x I, not an LL assumption or invented input kW.");
        var evidence = JsonSerializer.SerializeToElement(UpsInputEstimates.RecordedEvidence(sample));
        Check(evidence.GetProperty("InputVoltageLn").GetArrayLength() == 3 &&
            evidence.GetProperty("ApparentPowerMethod").GetString()!.Contains("L-N") &&
            evidence.GetProperty("Values").GetProperty(UpsInputEstimates.KvaKey).GetDouble() == 13.9,
            "Versioned recording identifies the actual L-N method and its source phase magnitudes.");
        sample.Phases["InputVoltageLN"][0] = null;
        Check(UpsInputEstimates.Calculate(sample, UpsInputEstimates.KvaKey) is null,
            "Incomplete measured L-N cannot fall back to a synthetic line voltage or missing phase zero.");
        ValidateReports(output, metrics);
        ValidateRecording(output);
        ValidateCompanionSelection(output);
        Console.WriteLine($"PASS: {assertions} balanced input estimate assertions.");
    }

    private static void ValidateCompanionSelection(string output)
    {
        using var preference = new AnalyzerPreferenceScope();
        var settings = typeof(UpsSettingsStore).GetField("_current", Static)!;
        var original = settings.GetValue(null);
        try
        {
            var sample = Snapshot();
            settings.SetValue(null, new UpsSettings { Devices = new List<UpsDevice> { new() { Id = sample.DeviceId } } });
            using var view = new OnlineAnalyzerView(() => Array.Empty<MeterSnapshot>(),
                () => new Dictionary<string, UpsSnapshot> { [sample.DeviceId] = sample }, () => new RecordingSettings());
            T Field<T>(string name) => (T)typeof(OnlineAnalyzerView).GetField(name, Instance)!.GetValue(view)!;
            Field<System.Windows.Forms.Timer>("_timer").Stop();
            Field<CheckBox>("_saveToFile").Checked = false;
            Field<CheckBox>("_eventEnable").Checked = false;
            Field<TextBox>("_folder").Text = output;
            var equipment = Field<CheckedListBox>("_equipmentList");
            for (var index = 0; index < equipment.Items.Count; index++)
                equipment.SetItemChecked(index, (string)equipment.Items[index].GetType().GetProperty("Id")!.GetValue(equipment.Items[index])! == sample.DeviceId);
            var channels = Field<CheckedListBox>("_channelList");
            for (var index = 0; index < channels.Items.Count; index++)
                channels.SetItemChecked(index, (string)channels.Items[index].GetType().GetProperty("UpsKey")!.GetValue(channels.Items[index])! == "InputCurrent");
            Check(Field<CheckBox>("_includeInputEstimates").Checked,
                "Automatic input companions have a visible, explicitly checked control that can be disabled.");
            typeof(OnlineAnalyzerView).GetMethod("OnStartClicked", Instance)!.Invoke(view, new object?[] { null, EventArgs.Empty });
            typeof(OnlineAnalyzerView).GetMethod("Sample", Instance)!.Invoke(view, null);
            var selected = channels.CheckedItems.Cast<object>().Select(channel =>
                (string)channel.GetType().GetProperty("UpsKey")!.GetValue(channel)!).ToArray();
            Check(new[] { UpsInputEstimates.VoltageKey, UpsInputEstimates.KvaKey }.All(selected.Contains) && selected.Length == 5,
                "Selecting input currents adds two visibly checked separate estimate channels, without substituting any measured channels.");
            var charts = Field<IList>("_chartGroups").Cast<object>().Select(group =>
                (AnalyticsTrendChart)group.GetType().GetProperty("Item1")!.GetValue(group)!).ToArray();
            Check(charts.Where(chart => chart.ChartTitle.Contains("ESTIMATED")).All(chart => !chart.ChartTitle.Contains("measured")) &&
                Field<DataGridView>("_grid").Rows.Cast<DataGridViewRow>().Count(row =>
                    row.Cells[0].Value?.ToString()?.Contains("Estimated") == true) == 2,
                "Calculated companions are fully visible in charts/readings and never captioned as measured evidence.");
        }
        finally { settings.SetValue(null, original); }
    }

    private static void ValidateReports(string output, HistoryMetric[] metrics)
    {
        var epoch = DateTime.UtcNow;
        foreach (var metric in metrics) metric.EquipmentId = "UPS-EST";
        var request = new EquipmentHistoryRequest
        {
            Equipment = new HistoryEquipment { Id = "UPS-EST", Name = "UPS balanced estimates", IntervalSeconds = 1 },
            Metrics = metrics, FromUtc = epoch, ToUtc = epoch.AddSeconds(3)
        };
        var result = new EquipmentHistoryResult { Request = request };
        var series = metrics.Select(metric => Tuple.Create(metric, 3,
            Enumerable.Range(0, 3).Select(index => new ReportPoint(epoch.AddSeconds(index), 200 + index * 1000)).ToList()));
        var analysis = EquipmentAnalysis.BuildFromSeries(result, series);
        Check(analysis.Channels.Count == 2 && analysis.UpsPowerBalances.Count == 0 &&
            !analysis.Findings.Any(finding => new[] { "Capacity", "Voltage", "Load" }.Contains(finding.Category)),
            "Extreme estimate values do not produce measured voltage, overload, loss or efficiency conclusions.");
        Check(analysis.Channels.All(channel => channel.Summary.Contains("ESTIMATE ONLY")) &&
            analysis.Summary.Contains("the L-L method assumes balance") &&
            analysis.Summary.Contains("measured L-N kVA uses matched phase V x I"),
            "Report summaries distinguish the balanced L-L method from the measured L-N phase-product method.");
        var pdf = Path.Combine(output, "balanced-input-estimates-evidence.pdf");
        EquipmentReportRenderer.WritePdf(analysis, pdf);
        var text = File.ReadAllText(pdf, Encoding.ASCII);
        Check(text.Contains("Balanced input estimates") && text.Contains("ESTIMATE ONLY") && text.Contains(@"sqrt\(3\)"),
            "PDF exposes estimate formulas and restrictions, not an unexplained calculated number.");
        var clone = (HistoryMetric)typeof(EquipmentHistoryView).GetMethod("CloneCombinedMetric", Static)!.Invoke(null,
            new object[] { metrics[0], request.Equipment })!;
        Check(clone.IsEstimate && clone.Basis == metrics[0].Basis,
            "Combined history preserves estimated identity and provenance.");
        var measured = new HistoryMetric { Name = "Input voltage", Unit = "V", VoltageReference = "L-N" };
        Check(ElectricalReportKnowledge.ComparisonQuantity(clone) != ElectricalReportKnowledge.ComparisonQuantity(measured),
            "Estimated voltage is not treated as the same quantity as measured L-N.");
    }

    private static void ValidateRecording(string output)
    {
        using var preference = new AnalyzerPreferenceScope();
        var settings = typeof(UpsSettingsStore).GetField("_current", Static)!;
        var original = settings.GetValue(null);
        try
        {
            settings.SetValue(null, new UpsSettings { Devices = new List<UpsDevice> { new() { Id = "UPS-EST", Model = "NXL test" } } });
            var sample = Snapshot();
            using var view = new OnlineAnalyzerView(() => Array.Empty<MeterSnapshot>(),
                () => new Dictionary<string, UpsSnapshot> { [sample.DeviceId] = sample }, () => new RecordingSettings());
            T Field<T>(string name) => (T)typeof(OnlineAnalyzerView).GetField(name, Instance)!.GetValue(view)!;
            void Call(string name, params object?[] args) => typeof(OnlineAnalyzerView).GetMethod(name, Instance)!.Invoke(view, args);
            Field<System.Windows.Forms.Timer>("_timer").Stop();
            Field<CheckBox>("_eventEnable").Checked = false;
            Field<CheckBox>("_includeInputEstimates").Checked = false;
            Field<ComboBox>("_interval").SelectedIndex = 1;
            Field<TextBox>("_folder").Text = output;
            Field<TextBox>("_fileName").Text = "recorded-balanced-input-estimates";
            var equipment = Field<CheckedListBox>("_equipmentList");
            for (var index = 0; index < equipment.Items.Count; index++)
                equipment.SetItemChecked(index, (string)equipment.Items[index].GetType().GetProperty("Id")!.GetValue(equipment.Items[index])! == sample.DeviceId);
            var channels = Field<CheckedListBox>("_channelList");
            Field<ComboBox>("_quickSelect").SelectedItem = "Voltages";
            Check(!channels.CheckedItems.Cast<object>().Any(channel => UpsInputEstimates.IsEstimateKey(
                (string)channel.GetType().GetProperty("UpsKey")!.GetValue(channel)!)),
                "Existing measurement quick presets never silently select assumption-based estimates.");
            Field<ComboBox>("_quickSelect").SelectedItem = "UPS input estimates (balanced assumption)";
            Check(channels.CheckedItems.Cast<object>().Count(channel => UpsInputEstimates.IsEstimateKey(
                (string)channel.GetType().GetProperty("UpsKey")!.GetValue(channel)!)) == 2,
                "Explicit estimate preset selects both labeled calculated channels.");
            Check(channels.Items.Cast<object>().Any(channel =>
                (string)channel.GetType().GetProperty("UpsKey")!.GetValue(channel)! == "InputPower"),
                "Unsupported input watts remain selectable with quality explanations rather than disappearing from the channel list.");
            Call("OnStartClicked", null, EventArgs.Empty);
            Call("Sample");
            Check(Field<DataGridView>("_grid").Rows.Cast<DataGridViewRow>().Where(row =>
                    row.Cells[0].Value?.ToString()?.Contains("Estimated") == true)
                .All(row => row.Cells[8].Value?.ToString()?.Contains("ESTIMATED") == true),
                "Live readings identify numeric estimates as estimated rather than measured.");
            Array.Fill(sample.Phases["InputVoltageLL"], (double?)0);
            Array.Fill(sample.Phases["InputCurrent"], (double?)0);
            Thread.Sleep(125);
            sample.TimestampUtc = DateTime.UtcNow;
            Call("Sample");
            sample.Phases["InputVoltageLL"][1] = null;
            Thread.Sleep(125);
            sample.TimestampUtc = DateTime.UtcNow;
            Call("Sample");
            Call("StopRun", "Balanced estimate recording test");
            var task = Field<Task>("_reportTask");
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(30)) { Application.DoEvents(); Thread.Sleep(10); }
            Check(task.IsCompletedSuccessfully, "Actual analyzer estimate report completes successfully.");
            using var csv = new Microsoft.VisualBasic.FileIO.TextFieldParser(Directory.GetFiles(output, "recorded-balanced-input-estimates_*.csv").Single());
            csv.SetDelimiters(",");
            var headers = csv.ReadFields()!;
            var first = csv.ReadFields()!;
            var zero = csv.ReadFields()!;
            var missing = csv.ReadFields()!;
            foreach (var metric in UpsInputEstimates.Metrics())
            {
                var index = Array.FindIndex(headers, name => name.Contains(metric.Name));
                Check(index >= 0 && double.Parse(first[index], CultureInfo.InvariantCulture) > 0 &&
                    double.Parse(zero[index], CultureInfo.InvariantCulture) == 0 && missing[index] == "",
                    "Estimated CSV channel preserves finite/zero/missing distinctly: " + metric.Name);
            }
            Check(first.Any(value => value.Contains("ESTIMATE ONLY")) && missing.Any(value => value.Contains("same Good acquisition")),
                "CSV retains assumptions and unavailable-prerequisite evidence.");
            var pdf = File.ReadAllText(Directory.GetFiles(output, "recorded-balanced-input-estimates_*_report.pdf").Single(), Encoding.ASCII);
            Check(pdf.Contains("ESTIMATE ONLY") && pdf.Contains("Estimated Input") && pdf.Contains(@"sqrt\(3\)"),
                "Actual analyzer export retains calculated-channel labels, formulas and missing-input evidence.");
        }
        finally { settings.SetValue(null, original); }
    }
}
