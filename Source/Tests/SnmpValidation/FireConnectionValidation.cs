using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class FireConnectionValidation
{
    private static readonly UserSession Admin = new() { UserId = "fire-qa", Username = "fire-qa", RoleName = "Administrator" };
    private static int assertions;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string evidence)
    {
        evidence = Path.GetFullPath(evidence);
        Directory.CreateDirectory(evidence);
        var temporary = Path.Combine(Path.GetTempPath(), "eoems-fire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            Protocol().GetAwaiter().GetResult();
            SettingsAndLifecycle(temporary).GetAwaiter().GetResult();
            ProfilesAndMigration(temporary).GetAwaiter().GetResult();
            CardCollapse.StartExpanded = true;
            Ui(temporary, evidence);
            CardCollapse.StartExpanded = false;
            Console.WriteLine($"Fire connection assertions passed: {assertions}.");
            File.WriteAllText(Path.Combine(evidence, "validation.json"), JsonSerializer.Serialize(new
            {
                assertions, validatedUtc = DateTime.UtcNow, realPanelContacted = false,
                productionSettingsChanged = false,
                limitations = "Legacy protocol regression fixtures only; cloud fixtures run separately. BMS vendor and real panel FC04 mapping remain unverified.",
                assembly = typeof(FireConnectionService).Assembly.Location
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            Directory.Delete(temporary, true);
        }
    }

    private static FireConnectionSettings Valid(int port) => new()
    {
        LocalHost = "127.0.0.1", LocalPort = port, MappingVerified = true,
        MappingReference = "Isolated simulator fixture; NOT a Zeta mapping", StartAddress = 37,
        RegisterCount = 2, UnitId = 7, PollSeconds = 1, FreshnessSeconds = 3,
        TimeoutMilliseconds = 350, RetryCount = 0, RetryIntervalSeconds = 5
    };

    private static async Task Protocol()
    {
        foreach (var mode in new[] { "valid", "fragmented", "transaction", "protocol", "unit", "function", "count", "length", "exception", "truncated", "timeout", "port-only" })
        {
            await using var panel = new PanelServer { Mode = mode };
            var settings = Valid(panel.Port);
            if (mode is "valid" or "fragmented")
            {
                var values = await FireModbusClient.ReadAsync(settings, CancellationToken.None);
                Check(values.SequenceEqual(new ushort[] { 0x1234, 0xabcd }), $"{mode}: valid FC04 values decoded exactly");
            }
            else
            {
                await ThrowsAsync<Exception>(() => FireModbusClient.ReadAsync(settings, CancellationToken.None),
                    $"{mode}: invalid/absent protocol data cannot succeed");
            }
            Check(panel.Requests.Count == 1, $"{mode}: only one read request");
            var request = panel.Requests.Single();
            Check(request.Length == 12 && request[7] == 4 && request[6] == 7 &&
                  request[8] == 0 && request[9] == 37 && request[10] == 0 && request[11] == 2,
                $"{mode}: wire is FC04 only with configured unit/zero-based mapping");
        }
        await using var cancelPanel = new PanelServer { Mode = "timeout" };
        var cancelSettings = Valid(cancelPanel.Port);
        cancelSettings.TimeoutMilliseconds = 5000;
        cancelSettings.FreshnessSeconds = 6;
        using var cancellation = new CancellationTokenSource(100);
        await ThrowsAsync<OperationCanceledException>(() => FireModbusClient.ReadAsync(cancelSettings, cancellation.Token),
            "caller cancellation interrupts a pending read independently of timeout");
        await using var maximumPanel = new PanelServer();
        var maximum = Valid(maximumPanel.Port);
        maximum.RegisterCount = 125;
        maximum.StartAddress = 65411;
        maximum.UnitId = 247;
        var maximumValues = await FireModbusClient.ReadAsync(maximum, CancellationToken.None);
        Check(maximumValues.Length == 125 && maximumValues[124] == 124,
            "maximum legal FC04 PDU and register address boundary are supported without truncation");
        Check(maximumPanel.Requests.Single()[6] == 247 && maximumPanel.Requests.Single()[11] == 125,
            "upper unit/count bounds are serialized exactly");
    }

    private static async Task SettingsAndLifecycle(string temporary)
    {
        var store = new FireConnectionStore(Path.Combine(temporary, "config", "fire.json"));
        using var service = new FireConnectionService(store);
        Check(service.ReadStatus().State == "PROTOCOL NOT VERIFIED", "configured default endpoint cannot claim protocol verification");
        Check(service.ReadSettings().LocalHost == "192.168.107.90" &&
              service.ReadSettings().BmsHost == "www.zetaremote.com" && service.ReadSettings().BmsPort == 3333 &&
              service.ReadSettings().PanelNetworkMode == "Enabled Local" &&
              service.ReadSettings().PanelNetworkAddress == "1" && service.ReadSettings().PanelNetworkName == "PANEL NAME",
            "confirmed Zeta endpoints and manually recorded panel-network references are defaults");
        Check(service.ReadStatus().LastTest is { IsReference: true, TimestampUtc: null } &&
              service.ReadStatus().LastTest.Reachability.State == "SUCCESS" &&
              service.ReadStatus().LastTest.Transport.State == "REFUSED" &&
              service.ReadStatus().LastTest.Protocol.State == "NOT TESTED",
            "supplied prior ping/refusal is structured, not a newly timestamped hardware test");
        foreach (var (error, expected) in new[]
        {
            (SocketError.ConnectionRefused, "REFUSED"), (SocketError.TimedOut, "TIMEOUT"),
            (SocketError.NetworkUnreachable, "NETWORK UNREACHABLE"), (SocketError.HostUnreachable, "NETWORK UNREACHABLE"),
            (SocketError.HostNotFound, "DNS FAILURE"), (SocketError.NoData, "DNS FAILURE"),
            (SocketError.TryAgain, "DNS FAILURE"), (SocketError.AccessDenied, "FAILED")
        })
            Check(FireConnectionService.ClassifySocketError(error) == expected, $"{error}: exact transport classification");
        await ThrowsAsync<InvalidOperationException>(() => service.ConnectAsync(Admin), "unverified map blocks connect");
        Check(!File.Exists(store.FilePath), "blocked connect does not create settings");
        var baseline = new FireConnectionSettings();
        await service.SaveAsync(baseline, "qa-secret-user", "qa-secret-password", false, Admin);
        var file = File.ReadAllText(store.FilePath);
        Check(!file.Contains("qa-secret-user") && !file.Contains("qa-secret-password"), "both username and password protected at rest");
        var saved = store.Load();
        Check(saved.ProtectedPassword.Length > 0 && saved.ProtectedUsername.Length > 0, "protected credentials load and decrypt under current account");
        Check(saved.Settings.PanelNetworkMode == "Enabled Local" && saved.Settings.PanelNetworkAddress == "1" &&
              saved.Settings.PanelNetworkName == "PANEL NAME" && saved.Settings.RetryCount == 3,
            "panel-network references and Modbus retry defaults persist without implying live verification");
        var networkReference = baseline.Copy();
        networkReference.PanelNetworkMode = "Enabled Global";
        networkReference.PanelNetworkAddress = "2";
        networkReference.PanelNetworkName = "Commissioning reference";
        await service.SaveAsync(networkReference, "", "", false, Admin);
        Check(store.Load().Settings.PanelNetworkMode == "Enabled Global" &&
              store.Load().Settings.PanelNetworkAddress == "2" &&
              store.Load().Settings.PanelNetworkName == "Commissioning reference",
            "authorized manual panel-network references persist without communicating with or changing the panel");
        await service.SaveAsync(baseline, "", "", false, Admin);
        Check(store.Load().ProtectedPassword == saved.ProtectedPassword && store.Load().ProtectedUsername == saved.ProtectedUsername,
            "blank username/password retain saved protected values");
        var copy = service.ReadSettings();
        copy.LocalHost = "uncommitted";
        Check(service.ReadSettings().LocalHost != "uncommitted", "settings reads are detached");
        var previous = File.ReadAllText(store.FilePath);
        foreach (var invalid in new Action<FireConnectionSettings>[]
        {
            s => s.LocalPort = 0, s => s.LocalPort = 65536, s => s.UnitId = 0, s => s.UnitId = 248,
            s => s.TimeoutMilliseconds = 249, s => s.TimeoutMilliseconds = 30001, s => s.RetryCount = 6,
            s => s.RetryIntervalSeconds = 4, s => s.PollSeconds = 3, s => s.FreshnessSeconds = 1,
            s => s.RegisterCount = 0, s => s.RegisterCount = 126, s => s.StartAddress = 65535,
            s => s.LocalHost = "999.1.1.1", s => s.LocalHost = "https://panel/",
            s => s.LocalHost = "panel/path", s => s.CloudUrl = "http://www.zetaremote.com",
            s => s.CloudUrl = "https://user:pass@example.com", s => s.CloudApiEndpoint = "https://example.com?token=secret",
            s => s.ConnectionType = (FireConnectionType)99, s => { s.MappingVerified = true; s.MappingReference = ""; }
        })
        {
            var bad = baseline.Copy();
            bad.RegisterCount = 2;
            invalid(bad);
            await ThrowsAsync<InvalidDataException>(() => service.SaveAsync(bad, "", "", false, Admin),
                "invalid settings rejected");
            Check(File.ReadAllText(store.FilePath) == previous && service.ReadSettings().LocalPort == baseline.LocalPort,
                "failed validation preserves last saved and applied configuration");
        }
        var viewer = new UserSession { UserId = "qa-view", Username = "qa-view", RoleName = "Viewer" };
        var preview = new UserSession { UserId = "qa-preview", Username = "qa-preview", RoleName = "Administrator", IsOfflinePreview = true };
        foreach (var actor in new[] { viewer, preview, new UserSession { RoleName = "Administrator" } })
        {
            await ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(baseline, "", "", false, actor), "unauthorized save blocked");
            await ThrowsAsync<UnauthorizedAccessException>(() => service.ConnectAsync(actor), "unauthorized connect blocked");
            await ThrowsAsync<UnauthorizedAccessException>(() => service.DisconnectAsync(actor), "unauthorized disconnect blocked");
            await ThrowsAsync<UnauthorizedAccessException>(() => service.TestAsync(baseline, actor), "unauthorized test blocked");
        }
        foreach (var type in new[] { FireConnectionType.ZetaCloud, FireConnectionType.BmsVendor })
        {
            var unsupported = baseline.Copy();
            unsupported.ConnectionType = type;
            await service.SaveAsync(unsupported, "", "", false, Admin);
            await ThrowsAsync<InvalidOperationException>(() => service.TestAsync(unsupported, Admin), "unsupported protocol test explicitly fails");
            await ThrowsAsync<InvalidOperationException>(() => service.ConnectAsync(Admin), "unsupported protocol connect explicitly fails");
            Check(service.ReadStatus().State == "PROTOCOL NOT VERIFIED" && !service.ReadStatus().LastDataUtc.HasValue,
                "website/vendor never fakes monitoring data");
        }
        await using (var tcpOnlyPanel = new PanelServer { Mode = "port-only" })
        {
            var tcpOnly = new FireConnectionSettings
            {
                LocalHost = "127.0.0.1", LocalPort = tcpOnlyPanel.Port, MappingVerified = false,
                TimeoutMilliseconds = 350
            };
            var result = await service.TestAsync(tcpOnly, Admin);
            Check(result.Contains("TCP CONNECTED - MODBUS REGISTER MAPPING NOT CONFIGURED") && tcpOnlyPanel.Requests.IsEmpty,
                "TCP-only test succeeds without a register map and never sends an FC04 request");
            Check(service.ReadStatus().LastTest is { IsReference: false, TimestampUtc: not null } &&
                  service.ReadStatus().LastTest.Transport.State == "CONNECTED" &&
                  service.ReadStatus().LastTest.Protocol.State == "NOT TESTED",
                "IP/TCP test stores separate timestamped stages, without fabricating Modbus verification");
            var networkTest = service.ReadStatus().LastTest!;
            Check(networkTest.SourceAddress == "127.0.0.1" && networkTest.SourceInterface != "NOT RECORDED" &&
                  networkTest.SourceEvidence == "Observed established TCP socket" && networkTest.DestinationAddress == "127.0.0.1",
                "connected loopback fixture records observed source address/interface and actual destination");
            Check(networkTest.CompletedUtc >= networkTest.TimestampUtc &&
                  networkTest.Reachability.TimestampUtc >= networkTest.TimestampUtc &&
                  networkTest.Transport.TimestampUtc >= networkTest.Reachability.TimestampUtc &&
                  networkTest.Transport.ResponseMilliseconds >= 0 && networkTest.FailureStage == "REGISTER CONFIGURATION",
                "test and individual network stages retain precise UTC timing and elapsed transport time");
            var badRegisters = tcpOnly.Copy();
            badRegisters.MappingVerified = true;
            badRegisters.MappingReference = "";
            badRegisters.RegisterCount = 0;
            await service.TestAsync(badRegisters, Admin);
            Check(tcpOnlyPanel.Requests.IsEmpty, "unconfigured register fields cannot block network tests or send guessed FC04 requests");
            badRegisters.MappingReference = "Isolated invalid-range fixture; not a Zeta mapping";
            await service.TestAsync(badRegisters, Admin);
            Check(service.ReadStatus().LastTest!.Transport.State == "CONNECTED" &&
                  service.ReadStatus().LastTest!.Protocol.State == "NOT TESTED" &&
                  service.ReadStatus().LastTest!.FailureStage == "REGISTER CONFIGURATION" &&
                  tcpOnlyPanel.Requests.IsEmpty,
                "invalid reviewed ranges are classified as configuration failures after TCP testing, without FC04 requests");
            Check(service.ReadSettings().ConnectionType == FireConnectionType.BmsVendor,
                "draft reachability/TCP test does not change the saved primary connection");
        }
        var closedListener = new TcpListener(IPAddress.Loopback, 0);
        closedListener.Start();
        var closedPort = ((IPEndPoint)closedListener.LocalEndpoint).Port;
        closedListener.Stop();
        var refused = new FireConnectionSettings { LocalHost = "127.0.0.1", LocalPort = closedPort, TimeoutMilliseconds = 350 };
        var refusedResult = await service.TestAsync(refused, Admin);
        Check((refusedResult.Contains("TCP: REFUSED") || refusedResult.Contains("TCP: TIMEOUT")) &&
              refusedResult.Contains("Modbus: NOT TESTED"),
            "TCP test reports the actual refused/timeout outcome and skips Modbus when transport is unavailable: " + refusedResult);
        Check(service.ReadStatus().LastTest!.PrimaryError.StartsWith("TCP CONNECTION") &&
              !service.ReadStatus().LastTest.PrimaryError.Contains("MAPPING") &&
              service.ReadStatus().LastTest.Protocol.Detail.Contains("TCP connection was unsuccessful"),
            "TCP error has priority over missing commissioning, with a specific reason for skipping protocol/mapping");
        var lastTest = service.ReadStatus().LastTest.Copy();
        Check(lastTest.FailureStage == "TCP TRANSPORT" && lastTest.Transport.ResponseMilliseconds >= 0 &&
              lastTest.DestinationAddress == "127.0.0.1" && lastTest.SourceAddress == "127.0.0.1" &&
              lastTest.SourceEvidence.Contains("route selection only"),
            "failed TCP retains measured duration and labels route-selected source separately from an established connection");
        if (lastTest.Transport.NativeErrorCode.HasValue)
            Check(lastTest.PrimaryError.Contains(lastTest.Transport.NativeErrorCode.Value.ToString()),
                "actual native socket error is retained in the primary failure");
        var eventsAfterRefusal = service.ReadStatus().Events.Count;
        await service.SaveAsync(baseline, "", "", false, Admin);
        await service.SaveAsync(baseline, "", "", false, Admin);
        Check(service.ReadStatus().LastTest!.PrimaryError == lastTest.PrimaryError &&
              service.ReadStatus().Events.Skip(eventsAfterRefusal).All(e => !e.Message.Contains("Confirm the panel/interface")),
            "saving uncommissioned settings does not repeat generic mapping errors or obscure the last TCP failure");
        await service.DisconnectAsync(Admin);
        Check(service.ReadStatus().LastTest!.TimestampUtc == lastTest.TimestampUtc &&
              service.ReadStatus().LastTest.PrimaryError == lastTest.PrimaryError &&
              !service.ReadStatus().Monitoring,
            "Disconnect never overwrites last-test stages with generic commissioning/disconnect errors");
        var disconnectedEvents = service.ReadStatus().Events.Count;
        await service.DisconnectAsync(Admin);
        Check(service.ReadStatus().Events.Count == disconnectedEvents, "duplicate Disconnect does not append identical lifecycle events");
        var exportPath = Path.Combine(temporary, "fire-diagnostics.json");
        await service.ExportDiagnosticsAsync(exportPath);
        var exportText = File.ReadAllText(exportPath);
        using (var exported = JsonDocument.Parse(exportText))
        {
            var root = exported.RootElement;
            Check(root.GetProperty("LastTestPrimaryResult").GetString() == lastTest.PrimaryError &&
                  root.GetProperty("LastConnectionTest").GetProperty("Transport").GetProperty("State").GetString() == lastTest.Transport.State &&
                  root.GetProperty("ActiveMonitoring").GetProperty("State").GetString() == "DISCONNECTED" &&
                  root.GetProperty("ActiveMonitoring").GetProperty("FireStatus").GetString() == "UNKNOWN",
                "persistent JSON export separates precise last test from disconnected monitoring and UNKNOWN fire status");
            Check(root.GetProperty("LastConnectionTest").GetProperty("SourceAddress").GetString() == "127.0.0.1" &&
                  root.GetProperty("LastConnectionTest").GetProperty("FailureStage").GetString() == "TCP TRANSPORT" &&
                  root.GetProperty("Events").GetArrayLength() <= 100 &&
                  root.GetProperty("ExportedUtc").GetDateTime().Kind == DateTimeKind.Utc,
                "diagnostic export includes source, destination, failure stage, UTC export timestamp and bounded history");
        }
        Check(!exportText.Contains("qa-secret-user") && !exportText.Contains("qa-secret-password") &&
              service.ReadStatus().Events.Count == disconnectedEvents,
            "diagnostic export excludes credentials and performs no connection or event logging");
        await ThrowsAsync<DirectoryNotFoundException>(() => service.ExportDiagnosticsAsync(Path.Combine(temporary, "missing", "diagnostics.json")),
            "export write failures are surfaced explicitly");
        await ThrowsAsync<InvalidOperationException>(() => service.ExportDiagnosticsAsync(store.FilePath),
            "diagnostic export cannot replace saved fire settings");
        Check(!Directory.EnumerateFiles(temporary, "*.tmp").Any(), "export leaves no temporary files after success or failure");
        var detached = service.ReadStatus();
        detached.LastTest!.Transport.State = "fake";
        Check(service.ReadStatus().LastTest!.Transport.State != "fake", "nested diagnostic reads are detached");
        using (var reloaded = new FireConnectionService(store))
            Check(reloaded.ReadStatus().LastTest!.TimestampUtc == lastTest.TimestampUtc &&
                  reloaded.ReadStatus().LastTest.PrimaryError == lastTest.PrimaryError &&
                  reloaded.ReadStatus().LastTest.SourceAddress == lastTest.SourceAddress &&
                  reloaded.ReadStatus().LastTest.SourceInterface == lastTest.SourceInterface &&
                  reloaded.ReadStatus().LastTest.Transport.ResponseMilliseconds == lastTest.Transport.ResponseMilliseconds &&
                  reloaded.ReadStatus().LastTest.CompletedUtc == lastTest.CompletedUtc,
                "structured last-test evidence reloads after restart from bounded events");
        var commandGate = Field<SemaphoreSlim>(service, "_commands");
        await commandGate.WaitAsync();
        try
        {
            await ThrowsAsync<InvalidOperationException>(() => service.TestAsync(refused, Admin), "duplicate test/command cannot queue a network attempt");
            await ThrowsAsync<InvalidOperationException>(() => service.ConnectAsync(Admin), "duplicate Connect cannot queue another worker");
        }
        finally { commandGate.Release(); }
        await using var panel = new PanelServer();
        var valid = Valid(panel.Port);
        await ThrowsAsync<InvalidDataException>(() => service.SaveAsync(valid, "", "", false, Admin),
            "checkbox alone cannot mark a mapping verified without an actual matching protocol response");
        await service.TestAsync(valid, Admin);
        await service.SaveAsync(valid, "", "", false, Admin);
        Check(FireConnectionStore.HasMappingProof(service.ReadSettings()), "successful FC04 test creates bound DPAPI verification evidence on save");
        var tampered = service.ReadSettings();
        tampered.StartAddress++;
        Check(!FireConnectionStore.HasMappingProof(tampered), "verification evidence cannot be reused for a different register range");
        tampered = service.ReadSettings();
        tampered.LocalHost = "127.0.0.2";
        Check(!FireConnectionStore.HasMappingProof(tampered), "verification evidence cannot be reused for a different monitoring host");
        Check(service.ReadStatus().State == "OFFLINE", "saved valid configuration is not ONLINE without connect");
        var message = await service.TestAsync(valid, Admin);
        Check(message.Contains("Valid FC04") && service.ReadStatus().LastTest!.RegisterMapping.Contains("INTERPRETATION NOT VERIFIED") &&
              service.ReadStatus().State == "OFFLINE" &&
              !service.ReadStatus().LastDataUtc.HasValue, "draft test reports genuine protocol result but does not mutate live status");
        await service.ConnectAsync(Admin);
        await Until(() => service.ReadStatus().State == "ONLINE");
        var online = service.ReadStatus();
        Check(online.LastDataUtc.HasValue && online.LastSuccessUtc == online.LastDataUtc && online.ResponseMilliseconds.HasValue &&
              online.ConnectedSinceUtc.HasValue && online.ConsecutiveFailures == 0, "ONLINE requires data and complete diagnostics");
        lock (Field<object>(service, "_gate"))
            Field<FireConnectionSnapshot>(service, "_snapshot").LastDataUtc = DateTime.UtcNow.AddSeconds(-30);
        var stale = service.ReadStatus();
        Check(stale.State == "DEGRADED" && stale.LastError.Contains("stale") && !stale.ConnectedSinceUtc.HasValue,
            "stale protocol data cannot remain ONLINE or retain a confirmed uptime");
        await Until(() => service.ReadStatus().State == "ONLINE");
        Check(service.ReadStatus().ConnectedSinceUtc > online.ConnectedSinceUtc,
            "receiving fresh data after a freshness gap starts a new uptime period");
        panel.Mode = "exception";
        await Until(() => service.ReadStatus().State == "OFFLINE");
        var offline = service.ReadStatus();
        Check(offline.LastError.Contains("UNKNOWN") && !offline.ConnectedSinceUtc.HasValue && offline.ConsecutiveFailures == 1,
            "protocol failure marks offline/unknown, resets uptime and counts failure");
        var failuresBefore = panel.Requests.Count;
        await Task.Delay(700);
        Check(panel.Requests.Count == failuresBefore, "failure never causes rapid retries");
        panel.Mode = "valid";
        await Until(() => service.ReadStatus().State == "ONLINE", 7500);
        Check(service.ReadStatus().ConsecutiveFailures == 0, "automatic reconnection recovers and resets consecutive failures");
        var requestTimes = panel.RequestTimes.ToArray();
        Check(requestTimes.Zip(requestTimes.Skip(1), (a, b) => (b - a).TotalSeconds).Any(t => t >= 4.8),
            "actual network retry respects configured five-second lower bound");
        await service.DisconnectAsync(Admin);
        var afterDisconnect = panel.Requests.Count;
        await Task.Delay(1200);
        Check(!service.ReadStatus().Monitoring && service.ReadStatus().State == "OFFLINE" &&
              panel.Requests.Count == afterDisconnect, "disconnect stops background polling and prevents autoreconnect");
        var bms = valid.Copy();
        bms.ConnectionType = FireConnectionType.BmsModbus;
        bms.BmsModbusHost = "127.0.0.1";
        bms.BmsModbusPort = panel.Port;
        await service.TestAsync(bms, Admin);
        await service.SaveAsync(bms, "", "", false, Admin);
        await service.ConnectAsync(Admin);
        await Until(() => service.ReadStatus().State == "ONLINE");
        Check(service.ReadStatus().ConnectionType == FireConnectionType.BmsModbus, "BMS Modbus uses same validated read-only transport");
        await service.SaveAsync(valid, "", "", false, Admin);
        await Until(() => service.ReadStatus().State == "ONLINE");
        Check(service.ReadStatus().ConnectionType == FireConnectionType.LocalModbus, "saving while monitoring safely stops old primary and applies only new primary");
        await service.DisconnectAsync(Admin);
        valid.AutoConnect = true;
        await service.SaveAsync(valid, "", "", false, Admin);
        using (var restarted = new FireConnectionService(store))
        {
            Check(restarted.ReadSettings().AutoConnect && restarted.ReadSettings().StartAddress == 37 && restarted.ReadStatus().HasCredentials,
                "new service instance reloads durable settings and protected credentials");
            Check(restarted.ReadStatus().Events.Count > 0, "previous connection events load after service/application restart");
            var requestsBeforeStartup = panel.Requests.Count;
            await restarted.StartAsync();
            await restarted.StartAsync();
            await Until(() => restarted.ReadStatus().State == "ONLINE");
            Check(restarted.ReadStatus().Monitoring, "startup connects without a Configuration page");
            await Task.Delay(2200);
            Check(panel.Requests.Count - requestsBeforeStartup <= 3, "repeated startup calls cannot create duplicate polling workers");
            await restarted.StopAsync();
        }
        valid.AutoConnect = false;
        valid.AutoReconnect = false;
        valid.RetryCount = 1;
        panel.Mode = "exception";
        await service.SaveAsync(valid, "", "", false, Admin);
        var eventsBeforeFailures = service.ReadStatus().Events.Count;
        await service.ConnectAsync(Admin);
        await Until(() => !service.ReadStatus().Monitoring, 7500);
        Check(service.ReadStatus().ConsecutiveFailures == 2, "with autoreconnect disabled retry count limits attempts to 1 + retries");
        Check(service.ReadStatus().Events.Skip(eventsBeforeFailures).Count(e => e.Message.StartsWith("OFFLINE:")) == 1,
            "identical recurring communication failures are logged once until recovery or error change");
        var stoppedRequests = panel.Requests.Count;
        await Task.Delay(600);
        Check(stoppedRequests == panel.Requests.Count, "retry exhaustion stays stopped");
        panel.Mode = "timeout";
        valid.TimeoutMilliseconds = 5000;
        valid.FreshnessSeconds = 6;
        await service.SaveAsync(valid, "", "", false, Admin);
        await service.ConnectAsync(Admin);
        await Task.Delay(100);
        Check(service.ReadStatus().State == "CONNECTING" && !service.ReadStatus().LastDataUtc.HasValue,
            "pending request is CONNECTING, never ONLINE based on open TCP alone");
        var stopWatch = Stopwatch.StartNew();
        await service.DisconnectAsync(Admin);
        Check(stopWatch.ElapsedMilliseconds < 1000, "disconnect cancels in-flight network operation promptly");
        await service.SaveAsync(new FireConnectionSettings(), "", "", true, Admin);
        Check(!service.ReadStatus().HasCredentials && !store.Load().Settings.MappingVerified, "defaults remove credentials and unverify mapping without touching history");
        var log = File.ReadAllText(store.FilePath + ".events.jsonl");
        Check(!log.Contains("qa-secret-password") && !log.Contains("qa-secret-user"), "events never contain stored credentials");
        using (var disabled = new FireConnectionService(store, false))
        {
            await ThrowsAsync<InvalidOperationException>(() => disabled.TestAsync(Valid(panel.Port), Admin), "preview/test suppression prevents actual communication");
        }
        File.WriteAllText(store.FilePath, "{broken");
        using (var corrupt = new FireConnectionService(store))
        {
            Check(corrupt.ReadStatus().LastError.Contains("Cannot load"), "corrupted settings are visible, never silently replaced");
            await ThrowsAsync<InvalidOperationException>(() => corrupt.SaveAsync(baseline, "", "", true, Admin), "corrupted store cannot be overwritten by fallback defaults");
            Check(File.ReadAllText(store.FilePath) == "{broken", "corrupted evidence remains intact for recovery");
        }
        var eventStore = new FireConnectionStore(Path.Combine(temporary, "events", "fire.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(eventStore.FilePath)!);
        var eventLine = JsonSerializer.Serialize(new FireConnectionEvent { TimestampUtc = DateTime.UtcNow, Message = new string('x', 180) });
        File.WriteAllText(eventStore.FilePath + ".events.jsonl", string.Concat(Enumerable.Repeat(eventLine + Environment.NewLine, 1500)));
        using (var rotating = new FireConnectionService(eventStore))
        {
            await rotating.SaveAsync(baseline, "", "", false, Admin);
            Check(File.Exists(eventStore.FilePath + ".events.jsonl.previous"), "connection-only diagnostic log rotates at bounded size");
        }
        using (var eventRestart = new FireConnectionService(eventStore))
            Check(eventRestart.ReadStatus().Events.Count == 100 && eventRestart.ReadStatus().Events.Any(e => e.Message.Contains("saved locally")),
                "last 100 events reload across current and rotated logs");
        File.WriteAllText(eventStore.FilePath + ".events.jsonl", "{broken-event");
        using (var malformedEvents = new FireConnectionService(eventStore))
        {
            Check(malformedEvents.ReadStatus().EventLogError.Contains("Cannot read"), "invalid previous event data is explicitly reported");
            await malformedEvents.SaveAsync(baseline, "", "", false, Admin);
            Check(malformedEvents.ReadStatus().EventLogError.Contains("Cannot read"), "new event append does not silently hide previous log corruption");
        }
        var malformedTest = new FireConnectionTestResult
        {
            TimestampUtc = DateTime.UtcNow,
            Reachability = null!
        };
        File.WriteAllText(eventStore.FilePath + ".events.jsonl", JsonSerializer.Serialize(new FireConnectionEvent
        {
            TimestampUtc = DateTime.UtcNow, Message = "Malformed structured test fixture", TestResult = malformedTest
        }));
        using (var malformedStages = new FireConnectionService(eventStore))
            Check(malformedStages.ReadStatus().EventLogError.Contains("Cannot read") && malformedStages.ReadStatus().LastTest?.IsReference == true,
                "missing structured stages are reported instead of causing runtime exceptions or manufacturing a new test");
        var legacyTime = new DateTime(2026, 10, 31, 11, 46, 35, DateTimeKind.Utc);
        File.WriteAllText(eventStore.FilePath + ".events.jsonl", JsonSerializer.Serialize(new FireConnectionEvent
        {
            TimestampUtc = legacyTime,
            Message = "Legacy structured refusal fixture",
            TestResult = new FireConnectionTestResult
            {
                Host = "192.168.107.90", Port = 502, TimestampUtc = legacyTime,
                Reachability = new FireDiagnosticStage { State = "SUCCESS", ResponseMilliseconds = 0 },
                Transport = new FireDiagnosticStage { State = "REFUSED", Detail = "ConnectionRefused (10061)" },
                PrimaryError = "TCP CONNECTION REFUSED - 192.168.107.90:502"
            }
        }));
        using (var legacyRefusal = new FireConnectionService(eventStore, false))
        {
            var restored = legacyRefusal.ReadStatus().LastTest!;
            Check(restored.PrimaryError.Contains("10061") && restored.TimestampUtc == legacyTime &&
                  restored.FailureStage == "TCP TRANSPORT" && restored.SourceAddress == "NOT RECORDED" &&
                  !restored.CompletedUtc.HasValue,
                "older structured logs retain actual refusal details and timestamps without invented interface or elapsed data");
        }
    }

    private static void Panels(string temporary)
    {
        var primaryStore = new FireConnectionStore(Path.Combine(temporary, "panels", "fire-connection.json"));
        using var primary = new FireConnectionService(primaryStore, false);
        string second;
        using (var manager = new FirePanelManager(primary))
        {
            Check(manager.Panels.Count == 1 && manager.Panels[0].Name == "Fire Panel 1" && manager.Panels[0].IsPrimary, "Fire Panel 1 exists by default");
            var added = manager.AddPanel("Boiler room", Admin);
            second = added.Id;
            manager.Rename(added, "Boiler room 2", Admin);
            Check(manager.Panels.Count == 2 && !ReferenceEquals(added.Service, primary), "added panel has its own service");
        }
        using (var reloaded = new FirePanelManager(primary))
        {
            Check(reloaded.Panels.Count == 2 && reloaded.Panels[1].Name == "Boiler room 2", "panels and names persist");
            reloaded.RemoveAsync(reloaded.Panels[1], Admin).GetAwaiter().GetResult();
            Check(reloaded.Panels.Count == 1 && !File.Exists(Path.Combine(temporary, "panels", "fire-connection-" + second + ".json")), "removed panel is deleted");
            var refused = false;
            try { reloaded.RemoveAsync(reloaded.Panels[0], Admin).GetAwaiter().GetResult(); } catch (InvalidOperationException) { refused = true; }
            Check(refused, "Fire Panel 1 cannot be removed");
        }
    }

    private static void Ui(string temporary, string evidence)
    {
        Panels(temporary);
        var store = new FireConnectionStore(Path.Combine(temporary, "ui", "fire.json"));
        using var service = new FireConnectionService(store, false);
        using var view = new LegacyFireConnectionSettingsView(service, Admin);
        var layoutPasses = 0;
        LayoutEventHandler countLayout = (_, _) => layoutPasses++;
        var statusLayouts = Descendants(view).OfType<TableLayoutPanel>().ToArray();
        foreach (var layout in statusLayouts) layout.Layout += countLayout;
        view.GetType().GetMethod("RenderStatus", Fields)!.Invoke(view, null);
        foreach (var layout in statusLayouts) layout.Layout -= countLayout;
        Check(layoutPasses == 0, "unchanged Fire status refresh does not relayout the diagnostics tables");
        using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-2400, -1800) };
        view.Dock = DockStyle.Fill;
        form.Controls.Add(view);
        Exception? failure = null;
        form.Shown += (_, _) => form.BeginInvoke(new Action(() =>
        {
            try { UiActions(view, form, service, store, temporary, evidence); }
            catch (Exception ex) { failure = ex; }
            finally { form.Close(); }
        }));
        Application.Run(form);
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task ProfilesAndMigration(string temporary)
    {
        var store = new FireConnectionStore(Path.Combine(temporary, "profiles", "fire.json"));
        using var service = new FireConnectionService(store, false);
        var settings = service.ReadSettings();
        foreach (var mode in new[] { FireConnectionType.ZetaCloud, FireConnectionType.BmsVendor })
        {
            var unsupportedTiming = settings.Copy();
            unsupportedTiming.ConnectionType = mode;
            unsupportedTiming.TimeoutMilliseconds = 30000;
            unsupportedTiming.PollSeconds = 30;
            unsupportedTiming.Validate();
            Check(true, $"{mode}: hidden Modbus freshness does not prevent saving its supported timeout/poll metadata");
        }
        Check(settings.BmsHost == "www.zetaremote.com" && settings.BmsPort == 3333 &&
              settings.BmsModbusPort == 502 && settings.BmsModbusHost == "",
            "vendor BMS endpoint is separate from independently commissioned BMS Modbus");
        settings.TimeoutMilliseconds = 4200;
        settings.UnitId = 9;
        settings.RetryCount = 4;
        settings.FreshnessSeconds = 30;
        settings.CaptureProfile(FireConnectionType.LocalModbus);
        settings.SelectProfile(FireConnectionType.ZetaCloud);
        settings.TimeoutMilliseconds = 8000;
        settings.PollSeconds = 30;
        settings.FreshnessSeconds = 45;
        settings.CloudAuthentication = "API token";
        settings.LocalProfile!.MappingVerified = true;
        settings.LocalProfile.MappingReference = "Untested inactive draft";
        settings.CaptureProfile(FireConnectionType.ZetaCloud);
        settings.SelectProfile(FireConnectionType.BmsVendor);
        settings.TimeoutMilliseconds = 9000;
        settings.FreshnessSeconds = 20;
        settings.RetryCount = 3;
        settings.BmsAuthentication = "Username / password";
        settings.BmsRole = "Server";
        await service.SaveAsync(settings, "bms-user-secret", "bms-token-secret", false, Admin);
        Check(store.Load().Settings.LocalProfile is { MappingVerified: false },
            "an untested inactive draft cannot persist a verified flag when another mode is saved");
        var bmsCipher = store.Load().ProtectedBmsPassword;
        settings = service.ReadSettings();
        settings.SelectProfile(FireConnectionType.ZetaCloud);
        Check(settings.TimeoutMilliseconds == 8000 && settings.PollSeconds == 30 && settings.CloudAuthentication == "API token",
            "Cloud profile retains its own request timeout, poll cadence and authentication type");
        await service.SaveAsync(settings, "cloud-user-secret", "cloud-token-secret", false, Admin);
        Check(service.ReadStatus().HasCloudCredentials && service.ReadStatus().HasBmsCredentials,
            "separate mode credential presence is reported without exposing secrets");
        var cloudCipher = store.Load().ProtectedPassword;
        settings = service.ReadSettings();
        settings.SelectProfile(FireConnectionType.LocalModbus);
        Check(settings.TimeoutMilliseconds == 4200 && settings.UnitId == 9 && settings.RetryCount == 4,
            "Local mode settings survive saving other modes");
        await service.SaveAsync(settings, "", "", false, Admin);
        using (var restarted = new FireConnectionService(store, false))
        {
            var reloaded = restarted.ReadSettings();
            reloaded.SelectProfile(FireConnectionType.BmsVendor);
            Check(reloaded.TimeoutMilliseconds == 9000 && reloaded.RetryCount == 3 && reloaded.BmsRole == "Server",
                "BMS metadata/profile survives application restart independently of Local/Cloud");
            Check(!restarted.ReadStatus().Monitoring, "saving a BMS server role never starts a listener/client");
        }
        settings.SelectProfile(FireConnectionType.BmsVendor);
        await service.SaveAsync(settings, "", "", true, Admin);
        Check(store.Load().ProtectedBmsPassword == "" && store.Load().ProtectedPassword == cloudCipher,
            "clear BMS credentials cannot clear or overwrite Cloud credentials");
        var json = File.ReadAllText(store.FilePath);
        Check(!json.Contains("bms-user-secret") && !json.Contains("bms-token-secret") &&
              !json.Contains("cloud-user-secret") && !json.Contains("cloud-token-secret") && bmsCipher.Length > 0,
            "both modes encrypt username and password/token at rest");
        var legacyPath = Path.Combine(temporary, "legacy.json");
        File.WriteAllText(legacyPath, JsonSerializer.Serialize(new
        {
            Settings = new FireConnectionSettings
            {
                ConnectionType = FireConnectionType.BmsModbus, BmsHost = "192.168.2.210", BmsPort = 1502,
                MappingVerified = true, MappingReference = "Old checkbox without successful protocol evidence"
            }
        }));
        var legacyStore = new FireConnectionStore(legacyPath);
        var migrated = legacyStore.Load().Settings;
        Check(migrated.BmsModbusHost == "192.168.2.210" && migrated.BmsModbusPort == 1502 &&
              migrated.BmsHost == "www.zetaremote.com" && migrated.BmsPort == 3333,
            "legacy BMS Modbus endpoint migrates without being mistaken for vendor port 3333");
        Check(!migrated.MappingVerified && migrated.BmsModbusProfile is { MappingVerified: false },
            "legacy checkbox-only verification is revoked, never treated as tested hardware evidence");
        Check(!File.ReadAllText(legacyPath).Contains("FormatVersion"), "loading/migration alone never rewrites live settings");
        var invalid = settings.Copy();
        invalid.CloudProfile!.TimeoutMilliseconds = 1;
        await ThrowsAsync<InvalidDataException>(() => service.SaveAsync(invalid, "", "", false, Admin),
            "invalid inactive mode settings cannot be saved and fail later during switching");
        await service.SaveAsync(new FireConnectionSettings(), "", "", true, Admin, clearAllCredentials: true);
        Check(!service.ReadStatus().HasCredentials, "explicit defaults reset clears all modes' credentials");
    }

    private static void UiActions(LegacyFireConnectionSettingsView view, Form form,
        FireConnectionService service, FireConnectionStore store, string temporary, string evidence)
    {
        view.SetPageActive(true);
        form.Size = new Size(1080, 640);
        form.Size = new Size(1366, 768);
        Check(Field<System.Windows.Forms.Timer>(view, "_layoutTimer").Enabled,
            "rapid window resizing defers wrapped-label relayout until the window settles");
        RestoreLayoutAfterMinimize(view, form);
        foreach (var size in new[] { new Size(1366, 768), new Size(1600, 900), new Size(1920, 1080), new Size(980, 600) })
        {
            form.Size = size;
            foreach (var type in new[] { 0, 1, 2 })
            {
                Field<ComboBox>(view, "_type").SelectedIndex = type;
                if (type == 2) Field<ComboBox>(view, "_bmsType").SelectedIndex = 1;
                var contentScroll = Field<Panel>(view, "_scroll");
                contentScroll.AutoScrollPosition = Point.Empty;
                Application.DoEvents();
                view.PerformLayout();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(evidence, $"fire-settings-{size.Width}-{type}.png"));
                foreach (var input in Descendants(view).Where(c => c.Visible && c is TextBox or NumericUpDown or ComboBox))
                    Check(input.Width >= 100, $"{size.Width} type {type}: editable fields remain usable ({input.Name}/{input.GetType().Name} w={input.Width} parent={input.Parent?.GetType().Name} pw={input.Parent?.Width})");
                Check(Field<Label>(view, "_state").Text.Contains("PROTOCOL NOT VERIFIED"), "UI never shows a mock ONLINE status");
                Check(Field<TextBox>(view, "_password").UseSystemPasswordChar, "cloud password always masked");
                var modeNotice = Field<Label>(view, "_modeNotice");
                contentScroll.ScrollControlIntoView(modeNotice);
                Application.DoEvents();
                Check(view.ClientRectangle.Contains(view.RectangleToClient(modeNotice.RectangleToScreen(modeNotice.ClientRectangle))),
                    $"{size.Width} type {type}: commissioning/protocol limitations remain accessible");
                Check(!contentScroll.HorizontalScroll.Visible, $"{size.Width} type {type}: no horizontal overflow");
                foreach (var table in Descendants(view).OfType<TableLayoutPanel>().Where(t => t.Visible))
                {
                    var controls = table.Controls.Cast<Control>().Where(c => c.Visible).ToArray();
                    foreach (var control in controls)
                    {
                        Check(control.Top >= table.Padding.Top && control.Bottom <= table.Height - table.Padding.Bottom &&
                              control.Left >= 0 && control.Right <= table.Width,
                            $"{size.Width} type {type}: {control.GetType().Name} {control.Text[..Math.Min(30, control.Text.Length)]} is not clipped by its table");
                        if (control is Label label)
                            Check(label.Height >= label.GetPreferredSize(new Size(label.Width, 0)).Height,
                                $"{size.Width} type {type}: multiline label has its measured height");
                    }
                    for (var i = 0; i < controls.Length; i++)
                        for (var j = i + 1; j < controls.Length; j++)
                            Check(!controls[i].Bounds.IntersectsWith(controls[j].Bounds),
                                $"{size.Width} type {type}: table controls never overlap");
                }
                foreach (var input in Descendants(view).Where(c => c.Visible && c is TextBox or NumericUpDown or ComboBox or CheckBox).ToArray())
                {
                    contentScroll.ScrollControlIntoView(input);
                    Application.DoEvents();
                    var bounds = contentScroll.RectangleToClient(input.RectangleToScreen(input.ClientRectangle));
                    Check(contentScroll.ClientRectangle.Contains(bounds), $"{size.Width} type {type}: {input.GetType().Name} can be fully scrolled into view");
                }
                foreach (var (name, anchor) in new[]
                {
                    ("parameters", (Control)Field<Panel>(view, "_dynamic")),
                    ("advanced", Field<Panel>(view, "_dynamicAdvanced")),
                    ("network", Field<ComboBox>(view, "_panelNetworkMode")),
                    ("diagnostics", Field<Label>(view, "_diagnostics"))
                })
                {
                    contentScroll.ScrollControlIntoView(anchor);
                    Application.DoEvents();
                    using var capture = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(capture, new Rectangle(Point.Empty, capture.Size));
                    capture.Save(Path.Combine(evidence, $"fire-settings-{size.Width}-{type}-{name}.png"));
                }
                Check(Descendants(view).OfType<Button>().Count(b => new[] { "test", "save", "connect", "disconnect", "reconnect", "defaults" }.Contains(b.Name)) == 6,
                    "all six actual communication/settings actions exist");
                foreach (var button in Field<FlowLayoutPanel>(view, "_buttons").Controls.OfType<Button>().Where(b => b.Visible))
                {
                    var bounds = view.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
                    Check(view.ClientRectangle.Contains(bounds), $"{size.Width}: {button.Name} remains visible outside scrolling fields");
                }
            }
            var scroll = view.Controls.OfType<Panel>().Single(p => p.AutoScroll);
            scroll.ScrollControlIntoView(Field<Label>(view, "_diagnostics"));
            Application.DoEvents();
            Check(Field<Label>(view, "_state").Parent is null, $"{size.Width}: overview status box is removed from the page");
            using (var bitmap = new Bitmap(form.Width, form.Height))
            {
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(evidence, $"fire-settings-{size.Width}-diagnostics.png"));
            }
            scroll.AutoScrollPosition = Point.Empty;
        }
        Field<ComboBox>(view, "_type").SelectedIndex = 2;
        Field<ComboBox>(view, "_bmsType").SelectedIndex = 1;
        Application.DoEvents();
        Check(!Field<NumericUpDown>(view, "_address").Visible && Field<TextBox>(view, "_endpoint").Visible,
            "vendor protocol hides irrelevant Modbus mapping");
        Field<ComboBox>(view, "_type").SelectedIndex = 0;
        Field<NumericUpDown>(view, "_timeout").Value = 4700;
        Field<NumericUpDown>(view, "_freshness").Value = 25;
        Field<ComboBox>(view, "_type").SelectedIndex = 1;
        Field<NumericUpDown>(view, "_timeout").Value = 8700;
        Field<ComboBox>(view, "_poll").SelectedItem = 10;
        Field<ComboBox>(view, "_authentication").SelectedItem = "API token";
        foreach (var action in new[] { "auth-test", "data-test" })
        {
            ClickAction(view, action);
            Check(Field<Label>(view, "_message").Text.Contains("failed"),
                $"{action}: unavailable authorized API does not fake authentication/data success");
        }
        Field<ComboBox>(view, "_type").SelectedIndex = 2;
        Field<ComboBox>(view, "_bmsType").SelectedIndex = 1;
        Check(Field<TextBox>(view, "_endpoint").Text == "www.zetaremote.com" &&
              Field<NumericUpDown>(view, "_port").Value == 3333, "BMS vendor UI shows known server metadata, not Local Modbus defaults");
        Field<ComboBox>(view, "_role").SelectedItem = "Client";
        Field<NumericUpDown>(view, "_timeout").Value = 9700;
        Field<ComboBox>(view, "_type").SelectedIndex = 0;
        Check(Field<NumericUpDown>(view, "_timeout").Value == 4700 && Field<NumericUpDown>(view, "_freshness").Value == 25,
            "switching UI types restores Local timeout/freshness, not BMS/Cloud values");
        Field<ComboBox>(view, "_type").SelectedIndex = 1;
        Check(Field<NumericUpDown>(view, "_timeout").Value == 8700 && (int)Field<ComboBox>(view, "_poll").SelectedItem! == 10 &&
              (string)Field<ComboBox>(view, "_authentication").SelectedItem! == "API token",
            "switching UI types restores Cloud-specific timeout/poll/authentication");
        Field<ComboBox>(view, "_type").SelectedIndex = 0;
        Field<TextBox>(view, "_endpoint").Text = "127.0.0.1";
        Field<ComboBox>(view, "_type").SelectedIndex = 2;
        Field<TextBox>(view, "_endpoint").Text = "bms.example.test";
        Field<ComboBox>(view, "_type").SelectedIndex = 0;
        Check(Field<TextBox>(view, "_endpoint").Text == "127.0.0.1", "switching type preserves separate draft panel/BMS endpoints");
        Check(!Field<CheckBox>(view, "_verified").Checked, "switching interface requires re-confirming the mapping");
        Field<CheckBox>(view, "_verified").Checked = true;
        Field<NumericUpDown>(view, "_address").Value = 15;
        Check(!Field<CheckBox>(view, "_verified").Checked, "changing a register mapping invalidates prior commissioning confirmation");
        Field<NumericUpDown>(view, "_port").Value = 1502;
        ClickAction(view, "save");
        Check(store.Load().Settings.LocalPort == 1502 && Field<Label>(view, "_message").Text.Contains("saved"),
            "Save button really persists/applies settings asynchronously");
        Field<TextBox>(view, "_endpoint").Text = "999.1.1.1";
        var beforeInvalid = File.ReadAllText(store.FilePath);
        ClickAction(view, "save");
        Check(Field<Label>(view, "_message").Text.Contains("failed") && File.ReadAllText(store.FilePath) == beforeInvalid,
            "invalid UI save shows failure and preserves last configuration");
        Field<TextBox>(view, "_endpoint").Text = "127.0.0.1";
        foreach (var action in new[] { "test", "connect", "reconnect" })
        {
            ClickAction(view, action);
            Check(Field<Label>(view, "_message").Text.Contains("failed"), $"{action} button reports real unavailable/unsaved/suppressed communication, not success");
        }
        ClickAction(view, "disconnect");
        Check(!service.ReadStatus().Monitoring && Field<Label>(view, "_message").Text.Contains("disconnected"),
            "Disconnect button actually stops communication");
        var history = Path.Combine(temporary, "ui", "fire-history-preservation.txt");
        File.WriteAllText(history, "history must remain unchanged");
        ConfirmDefaults(view, false);
        Check(store.Load().Settings.LocalPort == 1502 && Field<Label>(view, "_message").Text.Contains("cancelled"),
            "declining Reset to Defaults confirmation changes nothing");
        ConfirmDefaults(view, true);
        Check(store.Load().Settings.LocalPort == 502 && !store.Load().Settings.MappingVerified &&
              File.ReadAllText(history) == "history must remain unchanged",
            "confirmed defaults save/disconnect settings only and preserve separate history");
        Task.Run(() => service.StartAsync()).GetAwaiter().GetResult();
        var livePanel = new PanelServer();
        try
        {
            Field<ComboBox>(view, "_type").SelectedIndex = 0;
            Field<TextBox>(view, "_endpoint").Text = "127.0.0.1";
            Field<NumericUpDown>(view, "_port").Value = livePanel.Port;
            Field<NumericUpDown>(view, "_unit").Value = 7;
            Field<NumericUpDown>(view, "_address").Value = 37;
            Field<NumericUpDown>(view, "_count").Value = 2;
            Field<NumericUpDown>(view, "_timeout").Value = 350;
            Field<ComboBox>(view, "_poll").SelectedItem = 1;
            Field<NumericUpDown>(view, "_freshness").Value = 3;
            Field<TextBox>(view, "_reference").Text = "Isolated simulator fixture; NOT a Zeta mapping";
            ClickAction(view, "test");
            Check(livePanel.Requests.Count == 0, "UI Test never reads placeholder/unreviewed register map");
            Field<CheckBox>(view, "_verified").Checked = true;
            ClickAction(view, "save");
            Check(Field<Label>(view, "_message").Text.Contains("failed") && !service.ReadSettings().MappingVerified,
                "UI checkbox without successful test cannot save a verified map");
            ClickAction(view, "test");
            Check(Field<Label>(view, "_message").Text.Contains("MODBUS RESPONSE RECEIVED") && !service.ReadSettings().MappingVerified,
                "real UI Test obtains valid response but does not silently alter saved map/status");
            ClickAction(view, "save");
            Check(FireConnectionStore.HasMappingProof(service.ReadSettings()) &&
                  Field<Label>(view, "_mappingStatus").Text.StartsWith("FC04 RESPONSE VERIFIED"),
                "UI Save marks verified only after documentation attestation and exact FC04 test");
            ClickAction(view, "connect");
            var watch = Stopwatch.StartNew();
            while (service.ReadStatus().State != "ONLINE" && watch.ElapsedMilliseconds < 3000)
            {
                Application.DoEvents();
                Thread.Sleep(10);
            }
            Check(service.ReadStatus().State == "ONLINE", "actual UI Connect runs validated background monitoring");
            view.SetPageActive(false);
            Check(!Field<System.Windows.Forms.Timer>(view, "_timer").Enabled && service.ReadStatus().Monitoring,
                "hiding Configuration stops rendering only, not the communication service");
            ClickAction(view, "disconnect");
            Field<NumericUpDown>(view, "_address").Value = 38;
            Check(!Field<CheckBox>(view, "_verified").Checked && Field<Label>(view, "_mappingStatus").Text.StartsWith("NOT VERIFIED"),
                "editing tested mapping revokes UI verification and requires another exact test");
        }
        finally
        {
            livePanel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        view.SetPageActive(false);
        Check(!Field<System.Windows.Forms.Timer>(view, "_timer").Enabled, "hidden page stops UI timer, not service");
        var testValues = Field<Dictionary<string, Label>>(view, "_testValues");
        var monitorValues = Field<Dictionary<string, Label>>(view, "_monitorValues");
        Check(testValues["IP Reachability"].Text.StartsWith("SUCCESS") &&
              monitorValues["Monitoring State"].Text == "DISCONNECTED" &&
              monitorValues["TCP Transport"].Text == "DISCONNECTED" &&
              monitorValues["Data Freshness"].Text.Contains("disconnected"),
            "last network success remains visible independently of disconnected live monitoring and unavailable freshness");
        var beforeCopy = service.ReadStatus().LastTest!.Summary();
        ClickAction(view, "copy");
        Check(Clipboard.GetText().Contains(beforeCopy) && Clipboard.GetText().Contains("ACTIVE MONITORING") &&
              !Clipboard.GetText().Contains("qa-secret-password"),
            "Copy Diagnostics copies structured test and live status without credentials");
        foreach (var actor in new[] {
            new UserSession { UserId = "view", Username = "view", RoleName = "Viewer" },
            new UserSession { UserId = "preview", Username = "preview", RoleName = "Administrator", IsOfflinePreview = true }
        })
        {
            using var restricted = new LegacyFireConnectionSettingsView(service, actor);
            Check(Field<FlowLayoutPanel>(restricted, "_buttons").Controls.OfType<Button>().Where(b => b.Name != "copy" && b.Name != "export").All(b => !b.Enabled) &&
                  Field<FlowLayoutPanel>(restricted, "_buttons").Controls.OfType<Button>().Single(b => b.Name == "copy").Enabled &&
                  Field<FlowLayoutPanel>(restricted, "_buttons").Controls.OfType<Button>().Single(b => b.Name == "export").Enabled &&
                  !Field<ComboBox>(restricted, "_type").Enabled, "viewer/offline preview has read-only settings; credential-free copy and export are enabled");
        }
    }

    private static void RestoreLayoutAfterMinimize(LegacyFireConnectionSettingsView view, Form form)
    {
        view.SetPageActive(false);
        form.WindowState = FormWindowState.Minimized;
        Application.DoEvents();
        form.WindowState = FormWindowState.Normal;
        Application.DoEvents();
        view.SetPageActive(true);

        var layoutTimer = Field<System.Windows.Forms.Timer>(view, "_layoutTimer");
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < 250 || layoutTimer.Enabled)
        {
            Application.DoEvents();
            Thread.Sleep(10);
            if (watch.ElapsedMilliseconds > 2000)
                throw new InvalidOperationException("Fire layout did not settle after minimizing and restoring.");
        }

        var constrainedLabels = Descendants(view).OfType<Label>()
            .Where(label => label.Parent is TableLayoutPanel && label.MaximumSize.Width > 0);
        Check(constrainedLabels.All(label => label.MaximumSize.Width >= 120),
            "minimize/restore never stores transient narrow widths for wrapped labels");
        Check(Field<TableLayoutPanel>(view, "_content").Width >= 200,
            "minimize/restore keeps Fire settings content expanded instead of collapsing into a narrow column");
    }

    private static void ClickAction(LegacyFireConnectionSettingsView view, string name)
    {
        Field<FlowLayoutPanel>(view, "_buttons").Controls.OfType<Button>().Single(b => b.Name == name).PerformClick();
        var watch = Stopwatch.StartNew();
        while (Field<bool>(view, "_busy"))
        {
            Application.DoEvents();
            Thread.Sleep(5);
            if (watch.ElapsedMilliseconds > 10000)
            {
                var service = Field<FireConnectionService>(view, "_service");
                throw new InvalidOperationException($"UI action timed out: {name}; settings port={service.ReadSettings().LocalPort}; command gate={Field<SemaphoreSlim>(service, "_commands").CurrentCount}; UI message={Field<Label>(view, "_message").Text}; sync context={SynchronizationContext.Current?.GetType().Name ?? "none"}");
            }
        }
        Application.DoEvents();
    }

    private static void ConfirmDefaults(LegacyFireConnectionSettingsView view, bool accept)
    {
        var answered = false;
        using var timer = new System.Windows.Forms.Timer { Interval = 30 };
        timer.Tick += (_, _) =>
        {
            var dialog = FindWindow("#32770", "Reset connection settings only");
            if (dialog == IntPtr.Zero) return;
            GetWindowThreadProcessId(dialog, out var process);
            if (process != Environment.ProcessId) return;
            answered = true;
            timer.Stop();
            PostMessage(dialog, 0x111, new IntPtr(accept ? 6 : 7), IntPtr.Zero);
        };
        timer.Start();
        ClickAction(view, "defaults");
        Check(answered, "Reset to Defaults presents a real confirmation dialog owned by this test process");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string title);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out int process);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wparam, IntPtr lparam);

    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Fields)!.GetValue(instance)!;
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static async Task Until(Func<bool> condition, int milliseconds = 4000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > milliseconds) throw new InvalidOperationException("Timed out waiting for expected connection lifecycle state.");
            await Task.Delay(20);
        }
        assertions++;
    }

    private static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { Check(true, message); return; }
        throw new InvalidOperationException("Expected exception: " + message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }

    private sealed class PanelServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task worker;
        public readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> Requests = new();
        public readonly System.Collections.Concurrent.ConcurrentQueue<DateTime> RequestTimes = new();
        public string Mode = "valid";
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public PanelServer()
        {
            listener.Start();
            worker = Task.Run(Serve);
        }

        private async Task Serve()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                    var mode = Mode;
                    await using var stream = client.GetStream();
                    var request = new byte[12];
                    try { await stream.ReadExactlyAsync(request, cancellation.Token); }
                    catch (EndOfStreamException) { continue; }
                    Requests.Enqueue(request);
                    RequestTimes.Enqueue(DateTime.UtcNow);
                    if (mode == "port-only") continue;
                    if (mode == "timeout")
                    {
                        // Wait for client cancellation/timeout close without blocking later fixture accepts.
                        var end = new byte[1];
                        var received = await stream.ReadAsync(end, cancellation.Token);
                        if (received != 0) throw new InvalidDataException("Unexpected second request on one test connection.");
                        continue;
                    }
                    var count = request[10] * 256 + request[11];
                    var response = new byte[9 + count * 2];
                    new byte[] { request[0], request[1], 0, 0, 0, (byte)(3 + count * 2), request[6], 4, (byte)(count * 2) }.CopyTo(response, 0);
                    for (var index = 0; index < count; index++)
                    {
                        var value = index == 0 ? 0x1234 : index == 1 ? 0xabcd : index;
                        response[9 + index * 2] = (byte)(value >> 8);
                        response[10 + index * 2] = (byte)value;
                    }
                    switch (mode)
                    {
                        case "transaction": response[1] ^= 1; break;
                        case "protocol": response[3] = 1; break;
                        case "unit": response[6]++; break;
                        case "function": response[7] = 3; break;
                        case "count": response[8] = 2; break;
                        case "length": response[5] = 255; break;
                        case "exception": response = new byte[] { request[0], request[1], 0, 0, 0, 3, request[6], 0x84, 2 }; break;
                        case "truncated": response = response[..10]; break;
                    }
                    if (mode == "fragmented")
                    {
                        foreach (var b in response)
                        {
                            await stream.WriteAsync(new[] { b }, cancellation.Token);
                            await Task.Delay(2, cancellation.Token);
                        }
                    }
                    else await stream.WriteAsync(response, cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (SocketException) when (cancellation.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (cancellation.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            listener.Stop();
            await worker.ConfigureAwait(false);
            cancellation.Dispose();
        }
    }
}
