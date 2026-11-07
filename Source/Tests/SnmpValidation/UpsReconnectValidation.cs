using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;

internal static class UpsReconnectValidation
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Profile = "qa-reconnect";
    private const string Community = "isolated-localhost-test";
    private const string Identity = "1.3.6.1.4.1.99999";
    private static readonly Assembly App = typeof(UpsAcquisitionService).Assembly;
    private static int assertions;

    internal static void RunCadenceAudit(string output, string? deviceId = null, bool minimal = false)
    {
        Directory.CreateDirectory(output);
        var probe = App.GetTypes().Single(type => type.Name == "UpsCapabilityProbe").GetMethod("Run", Static)!;
        var keys = new[] { "InputVoltage", "InputVoltageLN", "InputVoltageLL", "InputCurrent", "InputPower",
            "OutputFrequency", "OutputVoltage", "OutputVoltageLL", "OutputCurrent", "OutputPower", "@OutputKw",
            "@OutputKva", "@OutputPf", "OutputLoad", "BatteryVoltage", "BatteryCurrent",
            UpsInputEstimates.VoltageKey, UpsInputEstimates.KvaKey };
        if (minimal) keys = new[] { "OutputFrequency" };
        var devices = UpsSettingsStore.Current.Devices.Where(item => item.Enabled &&
            (deviceId is null || item.Id == deviceId)).ToArray();
        if (devices.Length == 0) throw new ArgumentException("No enabled devices match the requested cadence audit.");
        var records = new List<object>();
        foreach (var device in devices)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var profile = (UpsCapabilityProfile)probe.Invoke(null, new object[] { device, timeout.Token })!;
            var samples = new List<UpsSnapshot>();
            var wall = new List<double>();
            var elapsed = Stopwatch.StartNew();
            for (var index = 0; index < (profile.IdentityAnswered && profile.SupportsUpsMib ? 40 : 1); index++)
            {
                var clock = Stopwatch.StartNew();
                var sample = UpsAcquisitionService.PollAnalyzerDevice(device, keys, 200);
                samples.Add(sample);
                wall.Add(elapsed.Elapsed.TotalMilliseconds);
                var remaining = 100 - (int)clock.ElapsedMilliseconds;
                if (remaining > 0) Thread.Sleep(remaining);
            }
            var good = samples.Where(sample => sample.IsGood).ToArray();
            var gaps = good.Zip(good.Skip(1), (a, b) => (b.TimestampUtc - a.TimestampUtc).TotalMilliseconds).ToArray();
            Console.WriteLine($"{device.Id}: {(minimal ? "minimal" : "full-signal")} target=100 ms; Good={good.Length}/{samples.Count}; " +
                $"request p50={samples.Select(sample => sample.RoundTripMilliseconds ?? 0).Order().ElementAt(samples.Count / 2):0.0} ms; " +
                $"valid spacing p50={(gaps.Length == 0 ? 0 : gaps.Order().ElementAt(gaps.Length / 2)):0.0} ms; max={(gaps.Length == 0 ? 0 : gaps.Max()):0.0} ms; " +
                $"last={samples.Last().Quality} {string.Join("; ", samples.Where(sample => !sample.IsGood).Select(sample => sample.QualityDetail).Distinct())}");
            records.Add(new { device.Id, TargetMilliseconds = 100, Keys = keys, profile.Complete, profile.Aborted,
                Samples = samples.Select((sample, index) => new { WallMilliseconds = wall[index], sample.TimestampUtc,
                    sample.Quality, sample.QualityDetail, sample.RoundTripMilliseconds, sample.Phases, sample.Values, sample.RawMeasurements }) });
            File.WriteAllText(Path.Combine(output, "ups-full-signal-cadence.json"), JsonSerializer.Serialize(records,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        using var service = new UpsAcquisitionService();
        service.SetAnalyzerPolling(devices.
            ToDictionary(device => device.Id, _ => keys), 100);
        Thread.Sleep(12000);
        var acquired = service.StopAnalyzerPollingAndDrain();
        service.StopAsync().GetAwaiter().GetResult();
        foreach (var group in acquired.GroupBy(sample => sample.DeviceId).OrderBy(group => group.Key))
        {
            var good = group.Where(sample => sample.IsGood).ToArray();
            var gaps = good.Zip(good.Skip(1), (a, b) => (b.TimestampUtc - a.TimestampUtc).TotalMilliseconds).Order().ToArray();
            Console.WriteLine($"CONCURRENT {group.Key}: Good={good.Length}/{group.Count()}; actual median=" +
                $"{(gaps.Length == 0 ? 0 : gaps[gaps.Length / 2]):0.0} ms; max={(gaps.Length == 0 ? 0 : gaps.Max()):0.0} ms.");
        }
        if (service.AnalyzerDroppedSamples != 0) throw new InvalidOperationException("Live response buffer lost samples.");
        File.WriteAllText(Path.Combine(output, "ups-concurrent-100ms-cadence.json"), JsonSerializer.Serialize(acquired.Select(sample =>
            new { sample.DeviceId, sample.TimestampUtc, sample.Quality, sample.QualityDetail, sample.RoundTripMilliseconds,
                sample.Phases, sample.Values }), new JsonSerializerOptions { WriteIndented = true }));
    }

    internal static void RunLiveAnalyzer(string configuration, string deviceId, int samplesPerTarget = 5)
    {
        var path = SnmpCredentialStore.StorePath();
        var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var settingsField = typeof(UpsSettingsStore).GetField("_current", Static)!;
        var originalSettings = settingsField.GetValue(null);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(Path.Combine(configuration, "snmp-credentials.json"), path, true);
            var settings = JsonSerializer.Deserialize<UpsSettings>(File.ReadAllText(Path.Combine(configuration, "ups-settings.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            settingsField.SetValue(null, settings);
            var device = settings.Devices.Single(item => item.Id == deviceId);
            var probe = App.GetTypes().Single(type => type.Name == "UpsCapabilityProbe").GetMethod("Run", Static)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var profile = (UpsCapabilityProfile)probe.Invoke(null, new object[] { device, timeout.Token })!;
            Console.WriteLine($"{deviceId}: read-only capability verification: identity answered={profile.IdentityAnswered}, complete={profile.Complete}, elapsed={profile.ElapsedMilliseconds:0} ms.");
            var voltage = UpsAcquisitionService.PollAnalyzerDevice(device, new[] { "InputVoltage", "OutputVoltage", "OutputVoltageLL" });
            Console.WriteLine($"Voltage diagnostic: {voltage.Quality}; input reference={voltage.InputVoltageReference}; output reference={voltage.OutputVoltageReference}");
            foreach (var phase in voltage.Phases)
                Console.WriteLine($"{phase.Key}: {string.Join(", ", phase.Value.Select(value => value?.ToString("0.###") ?? "missing"))}");
            foreach (var binding in profile.MeasurementBindings.Where(item => item.Definition.Key.Contains("Voltage")))
                Console.WriteLine($"Verified voltage binding: {binding.Definition.Key}; phase={binding.Phase}; label={binding.Label}");
            var successes = 0;
            foreach (var target in new[] { 25, 100, 250, 500 })
            {
                var timings = new List<double>();
                var gaps = new List<double>();
                DateTime? previous = null;
                for (var index = 0; index < samplesPerTarget; index++)
                {
                    var watch = Stopwatch.StartNew();
                    var sample = UpsAcquisitionService.PollAnalyzerDevice(device, new[] { "@OutputKw" });
                    if (sample.IsGood && sample.OutputKw.HasValue)
                    {
                        successes++;
                        timings.Add(sample.RoundTripMilliseconds!.Value);
                        if (previous.HasValue) gaps.Add((sample.TimestampUtc - previous.Value).TotalMilliseconds);
                        previous = sample.TimestampUtc;
                    }
                    else Console.WriteLine($"{deviceId}: {sample.Quality}: {sample.QualityDetail}");
                    var remaining = target - (int)watch.ElapsedMilliseconds;
                    if (remaining > 0) Thread.Sleep(remaining);
                }
                if (timings.Count > 0)
                    Console.WriteLine($"{deviceId}: target={target} ms; valid reads={timings.Count}/{samplesPerTarget}; request mean={timings.Average():0.0} ms, p95={timings.Order().ElementAt((int)Math.Ceiling(timings.Count * 0.95) - 1):0.0} ms, max={timings.Max():0.0} ms; requests exceeding target={timings.Count(value => value > target)}; observed response spacing={(gaps.Count > 0 ? gaps.Average() : 0):0.0} ms.");
            }
            if (successes == 0) throw new InvalidOperationException("No valid live UPS kW responses; timing target cannot be confirmed.");
        }
        finally
        {
            settingsField.SetValue(null, originalSettings);
            if (before is null) File.Delete(path);
            else File.WriteAllBytes(path, before);
        }
    }

    internal static void Run(bool analyzerOnly = false)
    {
        var path = SnmpCredentialStore.StorePath();
        Check(Path.GetFullPath(path).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase),
            "credential fixture is confined to the validation executable directory");
        var before = File.Exists(path) ? File.ReadAllBytes(path) : null;
        var settingsField = typeof(UpsSettingsStore).GetField("_current", Static)!;
        var originalSettings = settingsField.GetValue(null);
        var alarmSettingsField = typeof(AlarmSettingsStore).GetField("_current", Static)!;
        var originalAlarmSettings = alarmSettingsField.GetValue(null);
        var cache = (IDictionary)App.GetTypes().Single(type => type.Name == "UpsCapabilityProbe").GetField("Cache", Static)!.GetValue(null)!;
        var ids = new[] { "qa-reconnect-a", "qa-reconnect-b", "qa-reconnect-disabled" };
        try
        {
            alarmSettingsField.SetValue(null, new AlarmSettings());
            var updated = DateTime.UtcNow;
            var encrypted = (string)typeof(SnmpCredentialStore).GetMethod("Protect", Static)!.Invoke(null, new object[] { Community })!;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new[]
            {
                new { Name = Profile, Version = "v2c", Community = encrypted, UpdatedUtc = updated,
                    AuthProtocol = "None", PrivProtocol = "None", Port = 161, TimeoutMilliseconds = 200, Retries = 0 }
            }));
            using var agent = new LocalAgent();
            var settings = new UpsSettings
            {
                AcquisitionEnabled = true, MaxConcurrentPolls = 2,
                Banks = new List<AtsBankDefinition> { new() { Number = 1, Name = "QA ATS" } },
                Devices = ids.Select((id, index) => new UpsDevice
                {
                    Id = id, Label = id, IpAddress = "127.0.0.1", Port = agent.Port, CredentialProfile = Profile,
                    PollIntervalSeconds = index == 0 ? 1 : 2, TimeoutMilliseconds = 200, Retries = 0, Enabled = index != 2, AtsBank = 1
                }).ToList()
            };
            Check(new UpsDevice().PollIntervalSeconds == 1, "new UPS devices default to a one-second poll interval");
            var minimumIntervalSettings = new UpsSettings
            {
                Banks = new List<AtsBankDefinition> { new() { Number = 1, Name = "QA ATS" } },
                Devices = new List<UpsDevice>
                {
                    new() { Id = "interval-minimum", Label = "interval-minimum", IpAddress = "127.0.0.1",
                        PollIntervalSeconds = 1, AtsBank = 1 }
                }
            };
            minimumIntervalSettings.Validate();
            Check(true, "one-second UPS poll interval passes settings validation");
            settingsField.SetValue(null, settings);
            foreach (var device in settings.Devices)
                cache[device.Id] = new UpsCapabilityProfile
                {
                    DeviceId = device.Id, IpAddress = device.IpAddress, Port = device.Port, CredentialProfile = Profile,
                    CredentialUpdatedUtc = updated, VerifiedUtc = updated, IdentityAnswered = true,
                    Aborted = device.Id == ids[0] ? "synthetic partial capability timeout" : "",
                    SysObjectId = Identity, Manufacturer = "QA", Model = "QA",
                    SupportedOids = new[] { UpsOidMap.Find("OutputFrequency").Oid, UpsOidMap.Find("AlarmsPresent").Oid },
                    Checks = new[]
                    {
                        new UpsOidCheck { Key = "OutputFrequency", Present = true, InRange = true },
                        new UpsOidCheck { Key = "AlarmsPresent", Present = true, InRange = true }
                    }
                };
            var getCached = App.GetTypes().Single(type => type.Name == "UpsCapabilityProbe").GetMethod("GetCached", Static)!;
            Check(ReferenceEquals(getCached.Invoke(null, new object[] { settings.Devices[0] }), cache[ids[0]]),
                "identity-verified partial capability data is reused instead of re-probed on each reading");
            if (analyzerOnly)
            {
                AnalyzerCapabilityFailures(settings.Devices[0], cache);
                AnalyzerIncompleteRecovery(settings.Devices[0], cache);
                AnalyzerRequests(agent, settings.Devices[0], (UpsCapabilityProfile)cache[ids[0]]!);
                AnalyzerVoltageChannels(settings.Devices[0]);
                var analyzer = AnalyzerRecording(agent, settings.Devices[0]);
                while (!analyzer.IsCompleted)
                {
                    Application.DoEvents();
                    Thread.Sleep(5);
                }
                analyzer.GetAwaiter().GetResult();
                AnalyzerConcurrency(settings, cache, updated).GetAwaiter().GetResult();
                Console.WriteLine($"UPS analyzer assertions passed: {assertions}.");
                return;
            }
            Scheduling(agent, settings.Devices[0]).GetAwaiter().GetResult();
            var recovery = BackgroundRecovery(agent, settings);
            while (!recovery.IsCompleted)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
            recovery.GetAwaiter().GetResult();
            Console.WriteLine($"UPS reconnect assertions passed: {assertions}.");
        }
        finally
        {
            foreach (var id in ids) cache.Remove(id);
            settingsField.SetValue(null, originalSettings);
            alarmSettingsField.SetValue(null, originalAlarmSettings);
            if (before == null) File.Delete(path);
            else File.WriteAllBytes(path, before);
        }
    }

    private static void AnalyzerIncompleteRecovery(UpsDevice device, IDictionary cache)
    {
        var original = (UpsCapabilityProfile)cache[device.Id]!;
        var verified = original.VerifiedUtc;
        try
        {
            UpsAcquisitionService.PollDevice(device, 0, null);
            Check(ReferenceEquals(cache[device.Id], original),
                "fresh partial capabilities remain usable without repeating slow discovery on every normal poll");
            original.VerifiedUtc = DateTime.UtcNow.AddMinutes(-2);
            var sample = UpsAcquisitionService.PollAnalyzerDevice(device, new[] { "OutputFrequency" });
            Check(sample.IsGood && ReferenceEquals(cache[device.Id], original),
                "selected analyzer requests never block on a retry of incomplete capability discovery");
            UpsAcquisitionService.PollDevice(device, 0, null);
            Check(!ReferenceEquals(cache[device.Id], original) &&
                ((UpsCapabilityProfile)cache[device.Id]!).VerifiedUtc > verified,
                "normal background polling retries incomplete capability discovery after one minute, not six hours");
        }
        finally
        {
            original.VerifiedUtc = verified;
            cache[device.Id] = original;
        }
    }

    private static void AnalyzerCapabilityFailures(UpsDevice device, IDictionary cache)
    {
        var original = (UpsCapabilityProfile)cache[device.Id]!;
        try
        {
            var failure = new UpsCapabilityProfile
            {
                DeviceId = original.DeviceId, IpAddress = original.IpAddress, Port = original.Port,
                CredentialProfile = original.CredentialProfile, CredentialUpdatedUtc = original.CredentialUpdatedUtc,
                VerifiedUtc = DateTime.UtcNow, Aborted = "Synthetic identity timeout", FailureQuality = UpsQuality.Timeout
            };
            cache[device.Id] = failure;
            var snapshot = UpsAcquisitionService.PollAnalyzerDevice(device, new[] { "InputCurrent" });
            Check(snapshot.Quality == UpsQuality.Timeout && snapshot.QualityDetail.Contains("Synthetic identity timeout") &&
                !snapshot.QualityDetail.Contains("Waiting"), "known capability failure is shown as timeout, never a permanent waiting status");
            failure.IdentityAnswered = true;
            failure.Aborted = "";
            failure.FailureQuality = UpsQuality.NoData;
            snapshot = UpsAcquisitionService.PollAnalyzerDevice(device, new[] { "InputCurrent" });
            Check(snapshot.Quality == UpsQuality.NotSupported && snapshot.QualityDetail.Contains("UPS-MIB"),
                "verified non-UPS agent is shown as unsupported, not waiting or input zero");
            cache.Remove(device.Id);
            snapshot = UpsAcquisitionService.PollAnalyzerDevice(device, new[] { "InputCurrent" });
            Check(snapshot.Quality == UpsQuality.NoData && snapshot.QualityDetail.Contains("Waiting"),
                "waiting remains correct only when no current capability evidence exists");
        }
        finally { cache[device.Id] = original; }
    }

    private static void AnalyzerVoltageChannels(UpsDevice device)
    {
        var snapshot = new UpsSnapshot
        {
            DeviceId = device.Id, TimestampUtc = DateTime.UtcNow, Quality = UpsQuality.Good,
            InputVoltageReference = "L-L (AB / BC / CA)", OutputVoltageReference = "",
            Phases = new Dictionary<string, double?[]>
            {
                ["InputVoltage"] = new double?[] { 400, 401, 402 },
                ["InputVoltageLL"] = new double?[] { 400, 401, 402 },
                ["OutputVoltage"] = new double?[] { 230, 231, 232 }
            }
        };
        var snapshots = new Dictionary<string, UpsSnapshot> { [device.Id] = snapshot };
        using var view = new OnlineAnalyzerView(() => Array.Empty<MeterSnapshot>(), () => snapshots, () => new RecordingSettings());
        var type = typeof(OnlineAnalyzerView);
        var reading = Activator.CreateInstance(type.GetNestedType("Reading", BindingFlags.NonPublic)!, true)!;
        reading.GetType().GetProperty("Ups")!.SetValue(reading, snapshot);
        reading.GetType().GetProperty("Quality")!.SetValue(reading, "Good");
        var channels = ((IEnumerable)type.GetField("_channels", Instance)!.GetValue(view)!).Cast<object>().ToArray();
        object Channel(string key, string mode) => channels.First(channel =>
            (string)channel.GetType().GetProperty("UpsKey")!.GetValue(channel)! == key &&
            (string)channel.GetType().GetProperty("VoltageMode")!.GetValue(channel)! == mode &&
            (int)channel.GetType().GetProperty("UpsPhase")!.GetValue(channel)! == 0);
        double? Value(object channel) => (double?)type.GetMethod("ValueOf", Static)!.Invoke(null, new[] { reading, channel });
        var ln = Channel("InputVoltage", "LN");
        var ll = Channel("InputVoltage", "LL");
        Check(Value(ln) is null && Value(ll) == 400, "LL-only card never fabricates LN readings and exposes its measured LL voltage");
        var quality = (string)type.GetMethod("ChannelQuality", Static)!.Invoke(null, new object?[] { reading, ln, null })!;
        Check(quality.StartsWith("Unavailable") && quality.Contains("L-N") && quality.Contains("L-L"),
            "missing voltage channel reports the selected and actual references instead of Good");
        Check(Value(Channel("OutputVoltage", "LN")) is null, "RFC voltage with unspecified reference is not mislabeled LN");
        var equipment = (CheckedListBox)type.GetField("_equipmentList", Instance)!.GetValue(view)!;
        for (var index = 0; index < equipment.Items.Count; index++)
            equipment.SetItemChecked(index, (string)equipment.Items[index].GetType().GetProperty("Id")!.GetValue(equipment.Items[index])! == device.Id);
        var offered = (CheckedListBox)type.GetField("_channelList", Instance)!.GetValue(view)!;
        Check(offered.Items.Contains(ln) && offered.Items.Contains(ll) && Value(ln) is null && Value(ll) == 400,
            "All voltage references remain selectable with exact missing reasons; LL-only source never relabels measured LL as LN");
        snapshot.Phases = new Dictionary<string, double?[]>(snapshot.Phases)
        {
            ["InputVoltageLN"] = new double?[] { 230, 231, 232 }
        };
        snapshot.InputVoltageReference = "L-N";
        Check(Value(ln) == 230 && Value(ll) == 400, "card providing both references returns each explicit voltage series independently");
    }

    private static void AnalyzerRequests(LocalAgent agent, UpsDevice device, UpsCapabilityProfile capability)
    {
        var before = Volatile.Read(ref agent.Requests);
        var sample = UpsAcquisitionService.PollAnalyzerDevice(device, new[] { "OutputFrequency" });
        Check(sample.IsGood && sample.Value("OutputFrequency") == 50,
            "selected-channel SNMP request obtains a fresh scaled reading");
        Check(Volatile.Read(ref agent.Requests) - before == 1 &&
            agent.LastRequestedOids.Contains(UpsOidMap.Find("OutputFrequency").Oid) &&
            !agent.LastRequestedOids.Contains(UpsOidMap.Find("AlarmsPresent").Oid),
            "analyzer batches channel and liveness into one GET without alarm-table traffic");
        Check(!sample.AlarmDetailsComplete && sample.AlarmDetailsStatus.Contains("separately"),
            "measurement-only response never pretends that alarm details were checked");
        var originalBindings = capability.MeasurementBindings;
        try
        {
            capability.MeasurementBindings = new[]
            {
                new UpsMeasurementBinding
                {
                    Definition = new UpsOidDefinition { Key = "OutputPower", Oid = LocalAgent.PowerOid,
                        Scale = 1000, MinValid = 0, MaxValid = 5000000 },
                    Phase = -1
                }
            };
            sample = UpsAcquisitionService.PollAnalyzerDevice(device, new[] { "@OutputKw" });
            Check(sample.IsGood && sample.OutputKw == 314 &&
                agent.LastRequestedOids.Contains(LocalAgent.PowerOid) &&
                !agent.LastRequestedOids.Contains(UpsOidMap.Find("OutputFrequency").Oid),
                "selected total kW uses the verified vendor binding and does not request unrelated measurements");
        }
        finally { capability.MeasurementBindings = originalBindings; }
        var originalIndices = capability.InputIndices;
        var originalSupported = capability.SupportedOids;
        var originalVerified = capability.VerifiedUtc;
        try
        {
            capability.InputIndices = new[] { 1, 2, 3 };
            capability.SupportedOids = originalSupported.Concat(new[] { "InputVoltage", "InputCurrent", "InputPower" }
                .SelectMany(key => capability.InputIndices.Select(index => UpsOidMap.Find(key).Oid + index))).ToArray();
            sample = UpsAcquisitionService.PollAnalyzerDevice(device, new[] { "InputVoltage", "InputCurrent", "InputPower" });
            Check(sample.IsGood && sample.InputKw == 0 &&
                new[] { "InputVoltage", "InputCurrent", "InputPower" }.All(key =>
                    Enumerable.Range(0, 3).All(index => sample.Phase(key, index) == 0)),
                "actual SNMP GET and production RFC acquisition preserve all nine measured-zero input channels");
            agent.MissingInput = true;
            sample = UpsAcquisitionService.PollAnalyzerDevice(device, new[] { "InputVoltage", "InputCurrent", "InputPower" });
            Check(sample.IsGood && sample.InputKw is null &&
                new[] { "InputVoltage", "InputCurrent", "InputPower" }.All(key =>
                    Enumerable.Range(0, 3).All(index => sample.Phase(key, index) is null)),
                "actual NoSuchInstance input responses remain missing rather than turning into nine zero readings");
        }
        finally
        {
            agent.MissingInput = false;
            capability.InputIndices = originalIndices;
            capability.SupportedOids = originalSupported;
            capability.VerifiedUtc = originalVerified;
        }
        try
        {
            capability.InputIndices = new[] { 1, 2, 3 };
            capability.SupportedOids = originalSupported.Concat(capability.InputIndices.Select(index => UpsOidMap.Find("InputCurrent").Oid + index)).ToArray();
            capability.MeasurementBindings = ((IEnumerable)App.GetTypes().Single(type => type.Name == "UpsMeasurementMap")
                .GetField("Candidates", Static)!.GetValue(null)!).Cast<UpsMeasurementBinding>()
                .Where(binding => binding.Definition.Key == "InputVoltageLN").ToArray();
            before = Volatile.Read(ref agent.Requests);
            sample = UpsAcquisitionService.PollAnalyzerDevice(device, new[] { UpsInputEstimates.KvaKey });
            Check(sample.IsGood && sample.InputVoltageReference == "L-N" &&
                UpsInputEstimates.Calculate(sample, UpsInputEstimates.KvaKey) == 0 &&
                Volatile.Read(ref agent.Requests) - before == 1,
                "C/D L-N-based kVA selection requests all actual phase voltages/currents in one real network GET; zero amps stay zero kVA");
        }
        finally
        {
            capability.InputIndices = originalIndices;
            capability.SupportedOids = originalSupported;
            capability.MeasurementBindings = originalBindings;
            capability.VerifiedUtc = originalVerified;
        }
        try
        {
            capability.InputIndices = new[] { 1, 2, 3 };
            capability.SupportedOids = originalSupported.Concat(capability.InputIndices.Select(index => UpsOidMap.Find("InputCurrent").Oid + index)).ToArray();
            capability.MeasurementBindings = ((IEnumerable)App.GetTypes().Single(type => type.Name == "UpsMeasurementMap")
                .GetField("Candidates", Static)!.GetValue(null)!).Cast<UpsMeasurementBinding>()
                .Where(binding => binding.Definition.Key == "InputVoltageLL").ToArray();
            before = Volatile.Read(ref agent.Requests);
            sample = UpsAcquisitionService.PollAnalyzerDevice(device, new[] { UpsInputEstimates.VoltageKey, UpsInputEstimates.KvaKey });
            Check(sample.IsGood && Math.Abs(UpsInputEstimates.Calculate(sample, UpsInputEstimates.VoltageKey)!.Value - 400 / Math.Sqrt(3)) < 1e-10 &&
                UpsInputEstimates.Calculate(sample, UpsInputEstimates.KvaKey) == 0,
                "Actual selected-estimate SNMP request obtains real L-L inputs and zero currents without creating measured L-N or input watts");
            Check(Volatile.Read(ref agent.Requests) - before == 1 &&
                capability.MeasurementBindings.All(binding => agent.LastRequestedOids.Contains(binding.Definition.Oid)) &&
                capability.InputIndices.All(index => agent.LastRequestedOids.Contains(UpsOidMap.Find("InputCurrent").Oid + index)) &&
                !agent.LastRequestedOids.Contains(LocalAgent.PowerOid) && sample.InputKw is null,
                "Both estimates share one bounded GET for all six measured prerequisites, never output PF or active input power");
            agent.MissingInput = true;
            sample = UpsAcquisitionService.PollAnalyzerDevice(device, new[] { UpsInputEstimates.VoltageKey, UpsInputEstimates.KvaKey });
            Check(UpsInputEstimates.Calculate(sample, UpsInputEstimates.VoltageKey).HasValue &&
                UpsInputEstimates.Calculate(sample, UpsInputEstimates.KvaKey) is null,
                "Missing input current withholds kVA while independently supported voltage estimate remains available");
        }
        finally
        {
            agent.MissingInput = false;
            capability.InputIndices = originalIndices;
            capability.SupportedOids = originalSupported;
            capability.MeasurementBindings = originalBindings;
            capability.VerifiedUtc = originalVerified;
        }
        agent.DelayMilliseconds = 60;
        try
        {
            var watch = Stopwatch.StartNew();
            sample = UpsAcquisitionService.PollAnalyzerDevice(device, new[] { "OutputFrequency" });
            Check(sample.IsGood && watch.ElapsedMilliseconds < 500 && sample.RoundTripMilliseconds >= 50,
                "actual delayed SNMP response is measured and stays below 500 ms in the local fixture");
        }
        finally { agent.DelayMilliseconds = 0; }
        try
        {
            capability.MeasurementBindings = ((IEnumerable)App.GetTypes().Single(type => type.Name == "UpsMeasurementMap")
                .GetField("Candidates", Static)!.GetValue(null)!).Cast<UpsMeasurementBinding>().Take(27).ToArray();
            before = Volatile.Read(ref agent.Requests);
            sample = UpsAcquisitionService.PollAnalyzerDevice(device,
                capability.MeasurementBindings.Select(binding => binding.Definition.Key).Distinct().ToArray());
            Check(sample.IsGood && Volatile.Read(ref agent.Requests) - before == 1 && agent.LastRequestedOids.Length >= 30,
                "A full 27-measurement plus liveness acquisition fits one bounded GET, not a second near-empty PDU.");
        }
        finally
        {
            capability.MeasurementBindings = originalBindings;
        }
        var slow = device.Clone();
        slow.TimeoutMilliseconds = 2000;
        slow.Retries = 3;
        agent.Online = false;
        try
        {
            before = Volatile.Read(ref agent.Requests);
            var watch = Stopwatch.StartNew();
            sample = UpsAcquisitionService.PollAnalyzerDevice(slow, new[] { "OutputFrequency" });
            Check(!sample.IsGood && sample.Quality == UpsQuality.Timeout && sample.Value("OutputFrequency") == null,
                "offline analyzer input is explicitly timed out rather than reusing a previous good value");
            Check(watch.ElapsedMilliseconds < 750 && Volatile.Read(ref agent.Requests) - before == 1,
                "analyzer enforces a 500 ms request budget without inheriting multi-second retries");
            watch.Restart();
            sample = UpsAcquisitionService.PollAnalyzerDevice(slow, new[] { "OutputFrequency" }, 200);
            Check(sample.Quality == UpsQuality.Timeout && watch.ElapsedMilliseconds < 350 &&
                sample.Value("OutputFrequency") is null && sample.LastGood is null,
                "A 100 ms worker's 200 ms failure budget reduces a missed poll without filling, retrying or blocking for 500 ms.");
        }
        finally { agent.Online = true; }
        Console.WriteLine("Selected-channel UPS network, mapping and bounded-timeout checks passed.");
    }

    private static async Task AnalyzerRecording(LocalAgent agent, UpsDevice device)
    {
        var previous = UpsAcquisitionService.Current;
        using var service = new UpsAcquisitionService();
        UpsAcquisitionService.Current = service;
        try
        {
            using var view = new OnlineAnalyzerView(() => Array.Empty<MeterSnapshot>(),
                () => new Dictionary<string, UpsSnapshot>(), () => new RecordingSettings());
            T Field<T>(string name) => (T)typeof(OnlineAnalyzerView).GetField(name, Instance)!.GetValue(view)!;
            var equipment = Field<CheckedListBox>("_equipmentList");
            for (var index = 0; index < equipment.Items.Count; index++)
                equipment.SetItemChecked(index, (string)equipment.Items[index].GetType().GetProperty("Id")!.GetValue(equipment.Items[index])! == device.Id);
            var channels = Field<CheckedListBox>("_channelList");
            for (var index = 0; index < channels.Items.Count; index++)
                channels.SetItemChecked(index, (string)channels.Items[index].GetType().GetProperty("UpsKey")!.GetValue(channels.Items[index])! == "OutputFrequency");
            var interval = Field<ComboBox>("_interval");
            Check(interval.Text == "0.1 s" && interval.Items.Contains("0.025 s") && interval.Items.Contains("0.25 s") &&
                interval.Items.Contains("0.5 s"), "analyzer defaults to 100 ms and exposes 25/250/500 ms sampling");
            Check(Field<System.Windows.Forms.Timer>("_timer").Interval <= 25 &&
                Field<AnalyticsTrendChart>("_chart").ShowMarkers,
                "analyzer timer supports sub-100 ms sampling and its first point is visible");
            Field<CheckBox>("_saveToFile").Checked = false;
            typeof(OnlineAnalyzerView).GetMethod("OnStartClicked", Instance)!.Invoke(view, new object?[] { null, EventArgs.Empty });
            await WaitUntil(() => Field<long>("_rows") >= 5, "analyzer records repeated fresh UPS acquisitions without waiting for a full normal poll");
            await WaitUntil(() => Field<Label>("_status").Text.Contains("UPS request:"),
                "analyzer publishes request timing on the independent status refresh");
            var status = Field<Label>("_status").Text;
            Check(status.Contains("target: 0.1 s") && status.Contains("UPS request:"),
                "analyzer shows requested cadence separately from actual UPS request latency");
            var snapshots = service.GetAnalyzerSnapshots();
            Check(snapshots[device.Id].IsGood && service.GetSnapshots().Count == 0,
                "analyzer measurements remain separate from normal UPS snapshots and historian input");
            Field<System.Windows.Forms.Timer>("_timer").Stop();
            var beforeStall = Field<long>("_rows");
            await Task.Delay(650);
            typeof(OnlineAnalyzerView).GetMethod("Sample", Instance)!.Invoke(view, null);
            Check(Field<long>("_rows") >= beforeStall + 4,
                "all worker responses during a UI sampling stall are drained, not replaced by the latest snapshot");
            Check(service.DrainAnalyzerSnapshots().Count == 0 && service.AnalyzerDroppedSamples == 0,
                "a completed analyzer drain is duplicate-free and reports no hidden buffer losses");
            var request = new Dictionary<string, string[]> { [device.Id] = new[] { "OutputFrequency" } };
            foreach (var cadence in new[] { 25, 100, 250, 500 })
            {
                service.SetAnalyzerPolling(request, cadence);
                var watch = Stopwatch.StartNew();
                var timestamps = new HashSet<DateTime>();
                while (watch.Elapsed < TimeSpan.FromMilliseconds(cadence * 5 + 150))
                {
                    var reading = service.GetAnalyzerSnapshots().GetValueOrDefault(device.Id);
                    if (reading?.IsGood == true) timestamps.Add(reading.TimestampUtc);
                    await Task.Delay(5);
                }
                Check(timestamps.Count >= 3, $"fresh selected-channel UPS responses continue at requested {cadence} ms cadence");
                var ordered = timestamps.OrderBy(timestamp => timestamp).ToArray();
                var gaps = ordered.Zip(ordered.Skip(1), (a, b) => (b - a).TotalMilliseconds).OrderBy(gap => gap).ToArray();
                Console.WriteLine($"UPS fixture target {cadence} ms: {timestamps.Count} distinct responses, median observed gap {gaps[gaps.Length / 2]:0.0} ms.");
            }
            await service.StopAsync();
            Check(service.GetAnalyzerSnapshots().Count == 0, "stopping analyzer polling clears its data and cancelled workers cannot republish it");
            Check(((Task)typeof(UpsAcquisitionService).GetField("_analyzerTasks", Instance)!.GetValue(service)!).IsCompleted,
                "service shutdown awaits analyzer workers even when normal polling was never started");
            var publish = typeof(UpsAcquisitionService).GetMethod("PublishAnalyzerSnapshot", Instance)!;
            var epoch = DateTime.UtcNow;
            for (var i = 0; i < 10_001; i++)
                publish.Invoke(service, new object[] { new UpsSnapshot { DeviceId = device.Id, TimestampUtc = epoch.AddTicks(i) } });
            var backlog = service.DrainAnalyzerSnapshots();
            Check(backlog.Count == 10_000 && service.AnalyzerDroppedSamples == 1 && backlog[0].TimestampUtc == epoch.AddTicks(1),
                "sustained consumer overload has a strict buffer limit and an exact visible loss count");
            var overflowRejected = false;
            try { typeof(OnlineAnalyzerView).GetMethod("Sample", Instance)!.Invoke(view, null); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException error && error.Message.Contains("buffer overflow"))
            { overflowRejected = true; }
            Check(overflowRejected, "analyzer refuses to continue silently after even one known dropped response");
        }
        finally { UpsAcquisitionService.Current = previous; }
    }

    private static async Task Scheduling(LocalAgent agent, UpsDevice template)
    {
        using var service = new UpsAcquisitionService();
        var stateType = typeof(UpsAcquisitionService).GetNestedType("DeviceState", BindingFlags.NonPublic)!;
        var states = (IDictionary)typeof(UpsAcquisitionService).GetField("_states", Instance)!.GetValue(service)!;
        var poll = typeof(UpsAcquisitionService).GetMethod("PollScheduledAsync", Instance)!;
        using var semaphore = new SemaphoreSlim(1);
        agent.Online = false;
        foreach (var interval in new[] { 2, 10, 60, 600 })
        foreach (var failures in new[] { 0, 2, 8, 40 })
        {
            var device = template.Clone();
            device.PollIntervalSeconds = interval;
            var state = Activator.CreateInstance(stateType, true)!;
            stateType.GetField("Failures", Instance)!.SetValue(state, failures);
            states[device.Id] = state;
            await ((Task)poll.Invoke(service, new object[] { device, state, semaphore, CancellationToken.None })!).ConfigureAwait(false);
            var remaining = (DateTime)stateType.GetField("NextDueUtc", Instance)!.GetValue(state)! - DateTime.UtcNow;
            Check(remaining.TotalSeconds <= Math.Min(5, interval) && remaining.TotalSeconds > Math.Min(5, interval) - 0.75,
                $"failure {failures + 1} with {interval}s configured interval schedules recovery within {Math.Min(5, interval)}s");
            Check(service.GetSnapshot(device.Id)!.ConsecutiveFailures == failures + 1,
                "failed reads continue publishing explicit failure counts");
        }
        agent.Online = true;
        var healthy = template.Clone();
        healthy.PollIntervalSeconds = 60;
        var goodState = Activator.CreateInstance(stateType, true)!;
        goodState.GetType().GetField("Failures", Instance)!.SetValue(goodState, 8);
        states[healthy.Id] = goodState;
        await ((Task)poll.Invoke(service, new object[] { healthy, goodState, semaphore, CancellationToken.None })!).ConfigureAwait(false);
        var recovered = service.GetSnapshot(healthy.Id)!;
        Check(recovered.IsGood, "real SNMP response replaces failed snapshot");
        Console.WriteLine($"Cached localhost UPS poll completed in {recovered.RoundTripMilliseconds:0.0} ms.");
        Check((int)stateType.GetField("Failures", Instance)!.GetValue(goodState)! == 0, "recovery resets consecutive failures");
        var next = (DateTime)stateType.GetField("NextDueUtc", Instance)!.GetValue(goodState)! - DateTime.UtcNow;
        Check(next.TotalSeconds > 59 && next.TotalSeconds <= 60, "healthy poll returns to configured interval");
        Check(service.GetDiagnostics().InFlight == 0 && semaphore.CurrentCount == 1, "success and failures release the concurrency slot");
    }

    private static async Task AnalyzerConcurrency(UpsSettings settings, IDictionary cache, DateTime updated)
    {
        var oldDevices = settings.Devices;
        var oldLimit = settings.MaxConcurrentPolls;
        using var a = new LocalAgent { DelayMilliseconds = 60 };
        using var b = new LocalAgent { DelayMilliseconds = 60 };
        using var c = new LocalAgent { DelayMilliseconds = 60 };
        using var offline = new LocalAgent { Online = false };
        using var service = new UpsAcquisitionService();
        var agents = new[] { a, b, c, offline };
        try
        {
            settings.MaxConcurrentPolls = 1;
            settings.Devices = agents.Select((agent, index) => new UpsDevice
            {
                Id = "cadence-independent-" + index, IpAddress = "127.0.0.1", Port = agent.Port,
                CredentialProfile = Profile, Enabled = true, TimeoutMilliseconds = 2000, Retries = 3
            }).ToList();
            foreach (var device in settings.Devices)
                cache[device.Id] = new UpsCapabilityProfile
                {
                    DeviceId = device.Id, IpAddress = device.IpAddress, Port = device.Port, CredentialProfile = Profile,
                    CredentialUpdatedUtc = updated, VerifiedUtc = updated, IdentityAnswered = true,
                    SysObjectId = Identity, Manufacturer = "QA", Model = "QA",
                    SupportedOids = new[] { UpsOidMap.Find("OutputFrequency").Oid },
                    Checks = new[] { new UpsOidCheck { Key = "OutputFrequency", Present = true, InRange = true } }
                };
            service.SetAnalyzerPolling(settings.Devices.ToDictionary(device => device.Id, _ => new[] { "OutputFrequency" }), 100);
            await Task.Delay(1800).ConfigureAwait(false);
            var samples = service.StopAnalyzerPollingAndDrain();
            foreach (var device in settings.Devices.Take(3))
            {
                var good = samples.Where(sample => sample.DeviceId == device.Id && sample.IsGood).ToArray();
                var spacing = good.Zip(good.Skip(1), (left, right) => (right.TimestampUtc - left.TimestampUtc).TotalMilliseconds).ToArray();
                Check(good.Length >= 14 && spacing.Average() >= 85 && spacing.Average() <= 120,
                    $"{device.Id}: independent 60 ms endpoints maintain measured ~100 ms cadence despite a background concurrency setting of 1 and another timed-out endpoint");
                Check(good.Select(sample => sample.TimestampUtc).Distinct().Count() == good.Length &&
                    good.All(sample => sample.RoundTripMilliseconds >= 50),
                    "Cadence assertions count actual unique delayed network responses, not timer frames or repeated cache values");
            }
            Check(samples.Any(sample => sample.DeviceId == settings.Devices[3].Id && sample.Quality == UpsQuality.Timeout) &&
                service.AnalyzerDroppedSamples == 0,
                "Unresponsive peer remains explicitly missing without starving other endpoints or losing buffered responses");
            await service.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            foreach (var device in settings.Devices) cache.Remove(device.Id);
            settings.Devices = oldDevices;
            settings.MaxConcurrentPolls = oldLimit;
        }
    }

    private static async Task BackgroundRecovery(LocalAgent agent, UpsSettings settings)
    {
        using var service = new UpsAcquisitionService();
        using var view = new UpsAtsView(service, null!, new UserSession { RoleName = "Viewer" }, null!);
        using var detail = new UpsDetailForm(settings.Devices[0], service, null!);
        view.CreateControl();
        detail.CreateControl();
        view.SetPageActive(true);
        var refresh = typeof(UpsAtsView).GetMethod("RefreshData", Instance)!;
        try
        {
            service.Start();
            await WaitUntil(() => settings.Devices.Where(device => device.Enabled).All(device => service.GetSnapshot(device.Id)?.IsGood == true),
                "initial background polls return good measurements");
            var initial = service.GetSnapshot(settings.Devices[0].Id)!;
            var version = service.Version;
            agent.Online = false;
            await WaitUntil(() => settings.Devices.Where(device => device.Enabled)
                    .All(device => service.GetSnapshot(device.Id)?.Quality is UpsQuality.Timeout or UpsQuality.Offline),
                "network loss is detected for enabled units");
            var failed = service.GetSnapshot(settings.Devices[0].Id)!;
            Check(!failed.IsGood && failed.Values.Count == 0 && failed.LastGoodUtc.HasValue,
                "network failure retains last-good timestamp without presenting old measurements as live");
            refresh.Invoke(view, new object[] { true });
            Check(CardSnapshot(view, settings.Devices[0].Id)?.IsGood == false, "monitoring card renders the lost connection");

            var states = (IDictionary)typeof(UpsAcquisitionService).GetField("_states", Instance)!.GetValue(service)!;
            var gate = typeof(UpsAcquisitionService).GetField("_stateGate", Instance)!.GetValue(service)!;
            lock (gate)
            {
                foreach (var device in settings.Devices.Where(device => device.Enabled))
                {
                    var state = states[device.Id]!;
                    state.GetType().GetField("Failures", Instance)!.SetValue(state, 20);
                }
            }
            var failuresBefore = service.GetDiagnostics().PollsFailed;
            await WaitUntil(() => service.GetDiagnostics().PollsFailed >= failuresBefore + 2,
                "long-standing outages keep retrying automatically");
            view.SetPageActive(false);
            agent.Frequency = 510;
            var restoredAt = Stopwatch.StartNew();
            agent.Online = true;
            await WaitUntil(() => settings.Devices.Where(device => device.Enabled)
                    .All(device => service.GetSnapshot(device.Id)?.Value("OutputFrequency") == 51),
                "restored SNMP resumes actual background measurement reads without PollNow or page activity");
            Check(restoredAt.Elapsed < TimeSpan.FromSeconds(7), "recovery occurs on the short retry schedule");
            Check(service.Version > version && service.GetSnapshot(settings.Devices[0].Id)!.TimestampUtc > initial.TimestampUtc,
                "recovery publishes a new version and current timestamp");
            Check(service.GetSnapshot(settings.Devices[0].Id)!.ConsecutiveFailures == 0 &&
                  service.GetSnapshot(settings.Devices[0].Id)!.LastFailureUtc.HasValue,
                "current state recovers while historical failure evidence is retained");
            Check(service.GetSnapshot(settings.Devices[2].Id)!.Quality == UpsQuality.Disabled,
                "disabled units remain disabled throughout network recovery");
            view.SetPageActive(true);
            Check(CardSnapshot(view, settings.Devices[0].Id)?.Value("OutputFrequency") == 51,
                "monitoring page shows fresh data on return without being recreated");

            agent.Online = false;
            await WaitUntil(() => service.GetSnapshot(settings.Devices[0].Id)?.IsGood == false,
                "second disconnect is detected on the same running service");
            var detailStatus = (Label)typeof(UpsDetailForm).GetField("_status", Instance)!.GetValue(detail)!;
            await WaitUntil(() => CardSnapshot(view, settings.Devices[0].Id)?.IsGood == false &&
                                  !detailStatus.Text.StartsWith("LIVE"),
                "page and already-open detail timers show the disconnect without manual refresh");
            agent.Frequency = 520;
            agent.Online = true;
            await WaitUntil(() => service.GetSnapshot(settings.Devices[0].Id)?.Value("OutputFrequency") == 52,
                "second automatic reconnect also reads fresh values");
            await WaitUntil(() => CardSnapshot(view, settings.Devices[0].Id)?.Value("OutputFrequency") == 52,
                "normal page timer updates the recovered card without manual refresh");
            await WaitUntil(() => detailStatus.Text.StartsWith("LIVE"),
                "already-open UPS detail timer resumes live display automatically");
            Check(service.IsRunning && agent.Requests > 10, "background scheduler stays alive across repeated network outages");
        }
        finally
        {
            view.SetPageActive(false);
            await service.StopAsync();
        }
    }

    private static UpsSnapshot? CardSnapshot(UpsAtsView view, string deviceId)
    {
        var banks = (IEnumerable)typeof(UpsAtsView).GetField("_banks", Instance)!.GetValue(view)!;
        foreach (var bank in banks)
        {
            var cards = (IEnumerable)bank!.GetType().GetField("_cards", Instance)!.GetValue(bank)!;
            foreach (var card in cards)
            {
                var device = (UpsDevice)card!.GetType().GetField("Device", Instance)!.GetValue(card)!;
                if (device.Id == deviceId) return (UpsSnapshot?)card.GetType().GetField("Snapshot", Instance)!.GetValue(card);
            }
        }
        throw new InvalidOperationException("Expected UPS monitoring card not found.");
    }

    private static async Task WaitUntil(Func<bool> condition, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(25).ConfigureAwait(true);
        Check(condition(), message);
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException("UPS reconnect test failed: " + message);
        assertions++;
    }

    private sealed class LocalAgent : IDisposable
    {
        internal const string PowerOid = "1.3.6.1.4.1.476.1.42.3.9.20.1.20.1.2.1.4208";
        private readonly UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task worker;
        internal volatile bool Online = true;
        internal volatile int Frequency = 500;
        internal volatile int DelayMilliseconds;
        internal volatile bool MissingInput;
        internal string[] LastRequestedOids = Array.Empty<string>();
        internal int Requests;
        internal int Port => ((IPEndPoint)socket.Client.LocalEndPoint!).Port;

        internal LocalAgent()
        {
            worker = Task.Run(async () =>
            {
                try
                {
                    while (!cancellation.IsCancellationRequested)
                    {
                        var packet = await socket.ReceiveAsync(cancellation.Token);
                        Interlocked.Increment(ref Requests);
                        if (!Online) continue;
                        var request = MessageFactory.ParseMessages(packet.Buffer, new UserRegistry()).Single();
                        LastRequestedOids = request.Variables().Select(variable => variable.Id.ToString()).ToArray();
                        if (DelayMilliseconds > 0) await Task.Delay(DelayMilliseconds, cancellation.Token);
                        var variables = request.Variables().Select(variable =>
                        {
                            var oid = variable.Id.ToString();
                            ISnmpData data = oid == UpsOidMap.SysObjectId ? new ObjectIdentifier(Identity) :
                                oid == UpsOidMap.SysName ? new OctetString("QA UPS") :
                                oid == UpsOidMap.SysUpTime ? new TimeTicks(10000) :
                                oid == UpsOidMap.Find("OutputFrequency").Oid ? new Integer32(Frequency) :
                                oid == UpsOidMap.Find("AlarmsPresent").Oid ? new Integer32(0) : new NoSuchInstance();
                            if (oid == PowerOid) data = new OctetString("314");
                            if (new[] { 4097, 4099, 4101 }.Any(point => oid ==
                                "1.3.6.1.4.1.476.1.42.3.9.20.1.20.1.2.1." + point))
                                data = new OctetString("400");
                            if (new[] { 4096, 4098, 4100 }.Any(point => oid ==
                                "1.3.6.1.4.1.476.1.42.3.9.20.1.20.1.2.1." + point))
                                data = new OctetString("230");
                            if (new[] { "InputVoltage", "InputCurrent", "InputPower" }.Any(key =>
                                oid.StartsWith(UpsOidMap.Find(key).Oid, StringComparison.Ordinal)))
                                data = MissingInput ? new NoSuchInstance() : new Integer32(0);
                            return new Variable(variable.Id, data);
                        }).ToList();
                        var response = new ResponseMessage(request.RequestId(), VersionCode.V2, new OctetString(Community),
                            ErrorCode.NoError, 0, variables).ToBytes();
                        await socket.SendAsync(response, packet.RemoteEndPoint, cancellation.Token);
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            });
        }

        public void Dispose()
        {
            cancellation.Cancel();
            worker.GetAwaiter().GetResult();
            socket.Dispose();
            cancellation.Dispose();
        }
    }
}
