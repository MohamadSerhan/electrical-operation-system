using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;
using Lextm.SharpSnmpLib;

internal static class UpsChannelValidation
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Assembly App = typeof(UpsSnapshot).Assembly;
    private static int assertions;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }

    private static object? Map(string name, params object[] args) =>
        App.GetTypes().Single(type => type.Name == "UpsMeasurementMap").GetMethod(name, Static)!.Invoke(null, args);

    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var candidates = ((IEnumerable)App.GetTypes().Single(type => type.Name == "UpsMeasurementMap")
            .GetField("Candidates", Static)!.GetValue(null)!).Cast<UpsMeasurementBinding>().ToArray();
        var bindings = candidates.Where(binding => new[] { "InputVoltageLN", "InputVoltageLL", "InputCurrent", "InputPower" }
            .Contains(binding.Definition.Key)).ToArray();
        var data = bindings.ToDictionary(binding => binding.Definition.Oid, _ => (ISnmpData)new OctetString("0"));
        var snapshot = new UpsSnapshot { DeviceId = "UPS-QA", Quality = UpsQuality.Good, TimestampUtc = DateTime.UtcNow };
        var values = new Dictionary<string, double>();
        var phases = new Dictionary<string, double?[]>();
        var invalid = new List<string>();
        void Apply()
        {
            invalid.Clear();
            Map("Apply", bindings, data, snapshot, values, phases, invalid);
            snapshot.Values = values;
            snapshot.Phases = phases;
            snapshot.InvalidFields = invalid.ToArray();
        }
        Apply();
        foreach (var binding in bindings)
        {
            Check(snapshot.Phase(binding.Definition.Key, binding.Phase) == 0, "Mapped numeric zero survives: " + binding.Label);
            Check(snapshot.MissingChannelReason(binding.Definition.Key, binding.Phase).Length == 0,
                "Measured zero is available, not NoData: " + binding.Label);
        }
        Check(snapshot.InputKw == 0 && snapshot.InputVoltageReference == "L-N",
            "Complete zero phase input power produces zero kW; real L-N zeros retain their reference.");
        var row = UpsHistorian.CreateRecordedRow(new UpsDevice { Id = snapshot.DeviceId }, snapshot, snapshot.TimestampUtc);
        Check(row.Parameters.Single(parameter => parameter.Name == "iv1").Value.GetDouble() == 0 &&
            row.Parameters.Single(parameter => parameter.Name == "ii1").Value.GetDouble() == 0, "Wide historian fields preserve measured zero V and A.");
        using (var json = JsonDocument.Parse(row.Parameters.Single(parameter => parameter.Name == "channels").Value.GetString()!))
        {
            Check(json.RootElement.GetProperty("Phases").GetProperty("InputPower")[0].GetDouble() == 0,
                "Raw historian JSON preserves zero input power.");
            Check(!json.RootElement.GetProperty("Availability").TryGetProperty("InputPower:L1", out _),
                "Zero input power is not recorded as unavailable.");
        }
        foreach (var binding in bindings.Where(binding => binding.Definition.Key == "InputVoltageLN"))
            data[binding.Definition.Oid] = new NoSuchInstance();
        foreach (var binding in bindings.Where(binding => binding.Definition.Key == "InputVoltageLL"))
            data[binding.Definition.Oid] = new OctetString("400");
        Apply();
        Check(snapshot.Phase("InputVoltage", 0) == 400 && snapshot.InputVoltageReference.StartsWith("L-L"),
            "Missing L-N does not mask available real L-L in the native input-voltage alias.");
        Check(snapshot.MissingChannelReason("InputVoltage", 0, "L-N").Contains("Select the available reference"),
            "Wrong selected voltage reference has actionable evidence, not an outage diagnosis.");
        VerifyAnalyzer(snapshot, "InputVoltage", "LL", 400);
        VerifyAnalyzer(snapshot, "InputVoltage", "LN", null);
        VerifyAnalyzer(snapshot, "InputCurrent", "", 0);
        VerifyAnalyzer(snapshot, "InputPower", "", 0);
        foreach (var binding in bindings.Where(binding => binding.Definition.Key == "InputVoltageLL"))
            data[binding.Definition.Oid] = new OctetString("0");
        Apply();
        Check(snapshot.InputVoltageReference.StartsWith("L-L") && snapshot.Phase("InputVoltage", 0) == 0,
            "Measured zero L-L remains a valid native voltage when L-N is absent.");
        data[bindings.Single(binding => binding.Definition.Key == "InputVoltageLN" && binding.Phase == 0).Definition.Oid] = new OctetString("0");
        Apply();
        Check(snapshot.InputVoltageReference == "L-N" && snapshot.Phase("InputVoltage", 0) == 0 &&
            snapshot.Phase("InputVoltage", 1) is null, "Partial L-N containing a real zero is retained without mixing in L-L phases.");
        foreach (var bad in new ISnmpData[] { new NoSuchInstance(), new NoSuchObject(), new OctetString("No Support"),
                     new OctetString("NaN"), new OctetString("Infinity") })
        {
            foreach (var binding in bindings.Where(binding => binding.Definition.Key == "InputPower"))
                data[binding.Definition.Oid] = bad;
            Apply();
            Check(snapshot.InputKw is null && snapshot.Phase("InputPower", 0) is null,
                "Invalid/missing SNMP input power never becomes zero: " + bad);
            VerifyAnalyzer(snapshot, "InputPower", "", null);
        }
        var missing = new UpsSnapshot
        {
            Quality = UpsQuality.Good,
            UnsupportedOids = new[] { "InputPower 1.3.6.1.2.1.33.1.3.3.1.5.1 - NoSuchInstance" },
            Values = new Dictionary<string, double> { ["OutputPower"] = 200000 }
        };
        Check(missing.MissingChannelReason("InputPower", 0).Contains("NoSuchInstance"),
            "Capability evidence distinguishes an unsupported input-power object from numeric zero.");
        Check(missing.MissingChannelReason("OutputPower", 0).Contains("system total"),
            "System output total does not imply supported per-phase output watts.");
        missing.Quality = UpsQuality.Timeout;
        missing.QualityDetail = "Read-only fixture timed out";
        Check(missing.MissingChannelReason("InputPower", 0).StartsWith("Timeout:"),
            "Communication timeout is not classified as unsupported or input supply loss.");
        var metric = new HistoryMetric { Id = "channel:InputPower:L1", SourceMetricId = "InputPower", SourcePhase = 1,
            EquipmentId = "UPS-QA", Name = "UPS-QA | Input power L1", Unit = "W", MinimumMappingRevision = 2 };
        var request = new EquipmentHistoryRequest
        {
            Equipment = new HistoryEquipment { Id = "UPS-QA", Name = "UPS-QA" },
            Metrics = new[] { metric }, FromUtc = snapshot.TimestampUtc, ToUtc = snapshot.TimestampUtc.AddSeconds(3)
        };
        var zero = new UpsHistoryObservation { DeviceId = "UPS-QA", TimestampUtc = snapshot.TimestampUtc, Quality = "Good", Revision = 2 };
        zero.Values[metric.Id] = 0;
        var absent = new UpsHistoryObservation { DeviceId = "UPS-QA", TimestampUtc = snapshot.TimestampUtc.AddSeconds(1), Quality = "Good", Revision = 2 };
        absent.Availability[metric.Id] = "InputPower L1: card returned NoSuchInstance; not measured zero.";
        var projected = UpsEquipmentHistorySource.Project(request, new[] { zero, absent });
        Check(projected[0].Value == 0 && projected[0].Quality == "Good", "History projection keeps real numeric zero as Good.");
        Check(projected[1].Value is null && projected[1].Quality.Contains("NoSuchInstance"),
            "History projection carries the recorded per-channel missing reason.");
        var result = new EquipmentHistoryResult { Request = request, Rows = projected };
        var analysis = EquipmentAnalysis.Build(result);
        Check(analysis.Scope.Single().Channel.Points.Single().V == 0,
            "Report analysis preserves measured zero.");
        result.Rows = projected.Skip(1).ToArray();
        analysis = EquipmentAnalysis.Build(result);
        Check(analysis.Findings.Any(finding => finding.Detail.Contains("NoSuchInstance") && !finding.Advice.Contains("PLC")),
            "Missing UPS reports use recorded card evidence, not misleading PLC advice.");
        var pdf = Path.Combine(output, "ups-input-channel-evidence.pdf");
        EquipmentReportRenderer.WritePdf(analysis, pdf);
        var text = File.ReadAllText(pdf, Encoding.ASCII);
        Check(text.Contains("NoSuchInstance") && text.Contains("UPS input-channel availability"),
            "PDF includes the specific missing reason and electrical availability knowledge.");
        RunRecording(output);
        Console.WriteLine($"UPS input-channel assertions passed: {assertions}.");
    }

    private static void RunRecording(string output)
    {
        using var preference = new AnalyzerPreferenceScope();
        var field = typeof(UpsSettingsStore).GetField("_current", Static)!;
        var previous = field.GetValue(null);
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        try
        {
            field.SetValue(null, new UpsSettings { Devices = new List<UpsDevice> { new() { Id = "UPS-QA", Model = "QA" } } });
            var phases = new Dictionary<string, double?[]>
            {
                ["InputVoltageLN"] = new double?[] { 0, 0, 0 }, ["InputCurrent"] = new double?[] { 0, 0, 0 },
                ["InputPower"] = new double?[] { 0, 0, 0 }
            };
            var current = new UpsSnapshot { DeviceId = "UPS-QA", Quality = UpsQuality.Good, TimestampUtc = DateTime.UtcNow.AddSeconds(-1),
                InputVoltageReference = "L-N", Phases = phases };
            using var view = new OnlineAnalyzerView(() => Array.Empty<MeterSnapshot>(),
                () => new Dictionary<string, UpsSnapshot> { [current.DeviceId] = current }, () => new RecordingSettings());
            T Field<T>(string name) => (T)typeof(OnlineAnalyzerView).GetField(name, instance)!.GetValue(view)!;
            void Call(string name, params object?[] args) => typeof(OnlineAnalyzerView).GetMethod(name, instance)!.Invoke(view, args);
            Field<System.Windows.Forms.Timer>("_timer").Stop();
            Field<CheckBox>("_includeInputEstimates").Checked = false;
            Field<CheckBox>("_eventEnable").Checked = false;
            Field<ComboBox>("_interval").SelectedIndex = 1;
            Field<TextBox>("_folder").Text = output;
            Field<TextBox>("_fileName").Text = "zero-and-missing-input";
            var equipment = Field<CheckedListBox>("_equipmentList");
            for (var index = 0; index < equipment.Items.Count; index++)
                equipment.SetItemChecked(index, (string)equipment.Items[index].GetType().GetProperty("Id")!.GetValue(equipment.Items[index])! == "UPS-QA");
            var channels = Field<CheckedListBox>("_channelList");
            for (var index = 0; index < channels.Items.Count; index++)
                channels.SetItemChecked(index, new[] { "InputVoltage", "InputCurrent", "InputPower" }.Contains(
                    (string)channels.Items[index].GetType().GetProperty("UpsKey")!.GetValue(channels.Items[index])!) &&
                    (string)channels.Items[index].GetType().GetProperty("VoltageMode")!.GetValue(channels.Items[index])! != "LL");
            Call("OnStartClicked", null, EventArgs.Empty);
            Call("Sample");
            Check(Field<IList>("_series").Count == 9, "Recording includes all nine zero input V/A/W channels.");
            phases.Remove("InputPower");
            current.UnsupportedOids = new[] { "InputPower 1.3.6.1.2.1.33.1.3.3.1.5.1 - NoSuchInstance" };
            Thread.Sleep(125);
            current.TimestampUtc = DateTime.UtcNow;
            Call("Sample");
            Call("StopRun", "Zero/missing input evidence verification");
            var reportTask = Field<Task>("_reportTask");
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (!reportTask.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(30))
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
            Check(reportTask.IsCompletedSuccessfully, "Zero/missing input recording finalizes its PDF successfully.");
            using var csv = new Microsoft.VisualBasic.FileIO.TextFieldParser(Directory.GetFiles(output, "zero-and-missing-input_*.csv").Single());
            csv.SetDelimiters(",");
            var headers = csv.ReadFields()!;
            var first = csv.ReadFields() ?? throw new InvalidDataException("Zero input acquisition was not saved.");
            var second = csv.ReadFields() ?? throw new InvalidDataException("Missing input acquisition was not saved.");
            var input = Array.FindIndex(headers, header => header.Contains("Input Power L1"));
            Check(input >= 0 && double.Parse(first[input], System.Globalization.CultureInfo.InvariantCulture) == 0 &&
                second[input].Length == 0, "CSV preserves measured zero in the first acquisition and blank missing power in the second.");
            Check(second.Any(cell => cell.Contains("NoSuchInstance")), "CSV records the specific unavailable-channel cause.");
            var pdf = File.ReadAllText(Directory.GetFiles(output, "zero-and-missing-input_*_report.pdf").Single(), Encoding.ASCII);
            Check(pdf.Contains("NoSuchInstance") && pdf.Contains("Last recorded unavailable-channel reason"),
                "Actual analyzer PDF includes the recorded missing cause while retaining all selected signals.");
        }
        finally { field.SetValue(null, previous); }
    }

    private static void VerifyAnalyzer(UpsSnapshot snapshot, string key, string mode, double? expected, int phase = 0)
    {
        var type = typeof(OnlineAnalyzerView);
        var reading = Activator.CreateInstance(type.GetNestedType("Reading", BindingFlags.NonPublic)!, true)!;
        reading.GetType().GetProperty("Ups")!.SetValue(reading, snapshot);
        reading.GetType().GetProperty("Quality")!.SetValue(reading, "Good");
        var channel = Activator.CreateInstance(type.GetNestedType("ChannelItem", BindingFlags.NonPublic)!, true)!;
        channel.GetType().GetProperty("IsUps")!.SetValue(channel, true);
        channel.GetType().GetProperty("UpsKey")!.SetValue(channel, key);
        channel.GetType().GetProperty("UpsPhase")!.SetValue(channel, phase);
        channel.GetType().GetProperty("VoltageMode")!.SetValue(channel, mode);
        var value = (double?)type.GetMethod("ValueOf", Static)!.Invoke(null, new[] { reading, channel });
        Check(value == expected, $"Analyzer {key}/{mode}: expected {expected?.ToString() ?? "missing"}, not a fabricated value.");
        var quality = (string)type.GetMethod("ChannelQuality", Static)!.Invoke(null, new object?[] { reading, channel, value })!;
        Check(expected.HasValue ? quality == "Good" : quality.StartsWith("Unavailable:") && quality.Length > 40,
            "Analyzer per-channel quality agrees with actual numeric availability.");
    }

    internal static void RunLive(string output)
    {
        Directory.CreateDirectory(output);
        var probe = App.GetTypes().Single(type => type.Name == "UpsCapabilityProbe").GetMethod("Run", Static)!;
        var records = new List<object>();
        foreach (var device in UpsSettingsStore.Current.Devices.Where(device => device.Enabled))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var capability = (UpsCapabilityProfile)probe.Invoke(null, new object[] { device, timeout.Token })!;
            var samples = new List<UpsSnapshot>();
            for (var index = 0; index < 5; index++)
            {
                var sample = UpsAcquisitionService.PollAnalyzerDevice(device,
                    new[] { "InputVoltage", "InputCurrent", "InputPower", "OutputVoltage", "OutputCurrent", "@OutputKw",
                        UpsInputEstimates.VoltageKey, UpsInputEstimates.KvaKey });
                samples.Add(sample);
                if (!sample.IsGood)
                {
                    if (!capability.IdentityAnswered || !capability.SupportsUpsMib) break;
                    continue;
                }
                foreach (var key in new[] { "InputCurrent", "InputPower" })
                    for (var phase = 0; phase < 3; phase++)
                        VerifyAnalyzer(sample, key, "", sample.Phase(key, phase), phase);
                foreach (var binding in capability.MeasurementBindings.Where(binding =>
                    new[] { "InputVoltageLN", "InputVoltageLL", "InputCurrent", "InputPower" }.Contains(binding.Definition.Key)))
                {
                    if (!sample.RawMeasurements.TryGetValue(binding.Definition.Oid, out var raw)) continue;
                    var scaled = (double?)Map("Scaled", binding, new OctetString(raw));
                    Check(sample.Phase(binding.Definition.Key, binding.Phase) == scaled,
                        $"{device.Id}: selected analyzer input equals its exact raw OID and scale.");
                }
                var volts = Enumerable.Range(0, 3).Select(index => sample.Phase("InputVoltageLL", index)).ToArray();
                var currents = Enumerable.Range(0, 3).Select(index => sample.Phase("InputCurrent", index)).ToArray();
                var neutral = Enumerable.Range(0, 3).Select(index => sample.Phase("InputVoltageLN", index)).ToArray();
                if (neutral.All(value => value.HasValue) && currents.All(value => value.HasValue))
                    Check(Math.Abs(UpsInputEstimates.Calculate(sample, UpsInputEstimates.KvaKey)!.Value -
                        Enumerable.Range(0, 3).Sum(index => neutral[index]!.Value * currents[index]!.Value) / 1000) < 1e-10,
                        device.Id + ": actual L-N input phases calculate apparent kVA with matched phase currents, not output PF.");
                if (volts.All(value => value.HasValue) && currents.All(value => value.HasValue))
                {
                    Check(Math.Abs(UpsInputEstimates.Calculate(sample, UpsInputEstimates.VoltageKey)!.Value -
                        volts.Average(value => value!.Value) / Math.Sqrt(3)) < 1e-10,
                        device.Id + ": live voltage estimate agrees with its own three raw measured line voltages.");
                    Check(Math.Abs(UpsInputEstimates.Calculate(sample, UpsInputEstimates.KvaKey)!.Value -
                        Math.Sqrt(3) * volts.Average(value => value!.Value) * currents.Average(value => value!.Value) / 1000) < 1e-10,
                        device.Id + ": live apparent-power estimate agrees with its own acquisition and never output PF.");
                }
                Thread.Sleep(100);
            }
            var last = samples[^1];
            Console.WriteLine($"{device.Id}: {last.Quality}; input reference={last.InputVoltageReference}; " +
                $"current={string.Join(",", Enumerable.Range(0, 3).Select(index => last.Phase("InputCurrent", index)?.ToString("0.###") ?? "missing"))}; " +
                $"Pin={last.InputKw?.ToString("0.###") ?? "missing"} kW; valid analyzer acquisitions={samples.Count(sample => sample.IsGood)}/{samples.Count}.");
            Console.WriteLine($"  ESTIMATED: equivalent L-N={UpsInputEstimates.Calculate(last, UpsInputEstimates.VoltageKey)?.ToString("0.###") ?? "missing"} V; " +
                $"balanced input={UpsInputEstimates.Calculate(last, UpsInputEstimates.KvaKey)?.ToString("0.###") ?? "missing"} kVA; not measured input kW.");
            records.Add(new { Device = device.Id, capability.Complete, capability.Aborted, capability.FailureQuality,
                capability.SupportsUpsMib, Samples = samples.Select(sample => new
            {
                sample.TimestampUtc, sample.Quality, sample.QualityDetail, sample.RoundTripMilliseconds,
                sample.InputVoltageReference, sample.Phases, sample.Values, sample.RawMeasurements,
                Estimates = UpsInputEstimates.RecordedEvidence(sample),
                Availability = sample.ChannelAvailability()
            }) });
        }
        File.WriteAllText(Path.Combine(output, "selected-input-channel-audit.json"), JsonSerializer.Serialize(records,
            new JsonSerializerOptions { WriteIndented = true }));
        Check(records.Count > 0, "Enabled devices were audited without modifying configuration or equipment state.");
        Console.WriteLine($"Live selected-input assertions passed: {assertions}. Unreachable/unsupported devices remain explicitly unavailable.");
    }
}
