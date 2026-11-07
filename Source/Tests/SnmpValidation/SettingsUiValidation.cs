using System.Collections;
using System.Drawing;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;
using Microsoft.Data.Sqlite;

internal static class SettingsUiValidation
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly UserSession Admin = new() { UserId = "qa-ui", Username = "qa-ui", RoleName = "Administrator" };
    private static int assertions;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongW", ExactSpelling = true)]
    private static extern int GetWindowLong(nint handle, int index);

    internal static void RunShellRefresh()
    {
        typeof(PostgresDatabase).GetField("_connectionString", Static)!.SetValue(null,
            "Host=127.0.0.1;Port=1;Username=isolated_qa;Database=isolated_qa;Timeout=1;Pooling=false");
        typeof(PostgresDatabase).GetField("_lastFailureUtc", Static)!.SetValue(null, DateTime.UtcNow);
        using var shell = new MainShellForm(new AppSettings(),
            new UserSession { RoleName = "Administrator", IsOfflinePreview = true })
            { SuppressBackgroundServices = true };
        shell.Show();
        Application.DoEvents();
        Call(shell, "ShowPage", "Factory Map");
        var map = (FactoryMapView)Field<Control>(shell, "_currentView");
        Check(Field<IReadOnlyDictionary<string, UpsSnapshot>>(map, "_upsSnapshots").Count == 0,
            "offline Factory Map opens without an absent UPS service crash");
        var plc = Field<PlcConnectionService>(shell, "_plc");
        IReadOnlyList<MeterSnapshot> readings = new[] { new MeterSnapshot { TransformerName = "T1",
            Quality = MeterQuality.Good, PowerState = TransformerPowerState.On } };
        plc.GetType().GetField("_snapshots", Instance)!.SetValue(plc, readings);
        void Tick()
        {
            typeof(MainShellForm).GetField("_lastMapRefreshTicks", Instance)!.SetValue(shell, 0L);
            Call(shell, "OnUiRefreshTick", null, EventArgs.Empty);
        }

        void WaitForMap(Func<bool> ready)
        {
            var timeout = Stopwatch.StartNew();
            do
            {
                Tick();
                if (ready()) return;
                Application.DoEvents();
                Thread.Sleep(2);
            } while (timeout.Elapsed < TimeSpan.FromSeconds(5));
            throw new TimeoutException("Map snapshot refresh did not complete.");
        }
        Tick();
        Check(ReferenceEquals(Field<IReadOnlyList<MeterSnapshot>>(map, "_snapshots"), readings),
            "periodic UI tick updates Factory Map without pending PLC event flags or reopening");
        var upsType = typeof(MainShellForm).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.UpsAcquisitionService")!;
        var ups = Activator.CreateInstance(upsType)!;
        typeof(MainShellForm).GetField("_ups", Instance)!.SetValue(shell, ups);
        var snapshots = Field<System.Collections.Concurrent.ConcurrentDictionary<string, UpsSnapshot>>(ups, "_snapshots");
        snapshots["UPS-D1"] = new UpsSnapshot { DeviceId = "UPS-D1", Quality = UpsQuality.Disabled };
        WaitForMap(() => Field<IReadOnlyDictionary<string, UpsSnapshot>>(map, "_upsSnapshots").ContainsKey("UPS-D1"));
        Check(Field<IReadOnlyDictionary<string, UpsSnapshot>>(map, "_upsSnapshots")["UPS-D1"].Quality == UpsQuality.Disabled,
            "UPS-only changes refresh the active map independently of PLC acquisition");
        snapshots["UPS-D1"] = new UpsSnapshot { DeviceId = "UPS-D1", Quality = UpsQuality.Timeout };
        WaitForMap(() => Field<IReadOnlyDictionary<string, UpsSnapshot>>(map, "_upsSnapshots")["UPS-D1"].Quality == UpsQuality.Timeout);
        Check(Field<IReadOnlyDictionary<string, UpsSnapshot>>(map, "_upsSnapshots")["UPS-D1"].Quality == UpsQuality.Timeout,
            "changed UPS acquisition replaces the prior state on the active map");
        var cache = Field<IDictionary>(shell, "_pageCache");
        for (var index = 0; index < 3; index++)
        {
            Call(shell, "ShowPage", "Overview");
            Call(shell, "ShowPage", "Factory Map");
            Check(ReferenceEquals(Field<Control>(shell, "_currentView"), map),
                "repeated navigation reuses the same Factory Map without duplicate pages");
        }
        Check(cache.Count == 2, "three navigation cycles retain exactly Overview and Factory Map");
        var savedMap = Environment.GetEnvironmentVariable("EOEMS_MAP_PERF_DATA");
        if (!string.IsNullOrEmpty(savedMap))
        {
            var mapAssets = Field<IList>(map, "_assets");
            var layout = typeof(FactoryMapView).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapLayoutStore")!;
            var loaded = (IList)layout.GetMethod("LoadAssets", Static)!.Invoke(null,
                new object[] { mapAssets, Path.Combine(savedMap, "factory-map-layout.json") })!;
            mapAssets.Clear();
            foreach (var item in loaded) mapAssets.Add(item);
            var buildings = typeof(FactoryMapView).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapBuildingStore")!;
            var parts = buildings.GetMethod("Load", Static)!.Invoke(null, new object[] { Path.Combine(savedMap, "factory-map-building.json") })!;
            Call(Field<Control>(map, "_canvas"), "SetBuildingParts", parts);
            Console.WriteLine($"Map UI contention workload: {mapAssets.Count} saved assets / {((IList)parts).Count} building parts.");
        }
        var device = UpsSettingsStore.Current.Devices.First();
        Field<Task>(shell, "_mapUpsReadTask").GetAwaiter().GetResult();
        typeof(MainShellForm).GetField("_mapUpsReadTask", Instance)!.SetValue(shell, null);
        snapshots.Clear();
        snapshots[device.Id] = new UpsSnapshot { DeviceId = device.Id, Quality = UpsQuality.Good, TimestampUtc = DateTime.UtcNow };
        var old = new UpsSnapshot { DeviceId = device.Id, Quality = UpsQuality.Good, TimestampUtc = DateTime.UtcNow.AddHours(-1),
            Values = new Dictionary<string, double> { ["BatteryVoltage"] = 480 } };
        typeof(MainShellForm).GetField("_mapUpsSnapshots", Instance)!.SetValue(shell,
            new Dictionary<string, UpsSnapshot> { [device.Id] = old });
        var upsAsset = Field<IList>(map, "_assets").Cast<object>().First(item => (string)item.GetType().GetProperty("Category")!.GetValue(item)! == "UPS");
        upsAsset.GetType().GetProperty("EquipmentId")!.SetValue(upsAsset, device.Id);
        var credentialGate = typeof(SnmpCredentialStore).GetField("Gate", Static)!.GetValue(null)!;
        using var locked = new ManualResetEventSlim();
        var contention = Task.Run(() =>
        {
            lock (credentialGate)
            {
                locked.Set();
                Thread.Sleep(1000);
            }
        });
        if (!locked.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Credential-lock fixture did not start.");
        var refreshClock = Stopwatch.StartNew();
        Tick();
        var blockedMilliseconds = refreshClock.Elapsed.TotalMilliseconds;
        var pendingRead = Field<Task>(shell, "_mapUpsReadTask");
        var canvas = Field<Control>(map, "_canvas");
        Call(map, "UpdateSelection", upsAsset);
        Check(Field<Label>(map, "_selectedStatus").Text.Contains("STALE") &&
            Field<Label>(map, "_selectedDetails").Text.Contains("Battery voltage: --"),
            "last published readings still expire while the next snapshot read is blocked");
        using var bitmap = new Bitmap(canvas.Width, canvas.Height);
        using var graphics = Graphics.FromImage(bitmap);
        var gaps = new List<double>();
        var heartbeat = Stopwatch.StartNew();
        var priorBeat = heartbeat.Elapsed.TotalMilliseconds;
        var beats = 0;
        while (!contention.IsCompleted)
        {
            Tick();
            if (!pendingRead.IsCompleted)
                Check(ReferenceEquals(pendingRead, Field<Task>(shell, "_mapUpsReadTask")), "blocked provider keeps exactly one snapshot read in flight");
            Call(canvas, "ZoomAt", beats % 2 == 0 ? 1.01f : 1 / 1.01f, new Point(canvas.Width / 2, canvas.Height / 2));
            var beforePaint = heartbeat.Elapsed.TotalMilliseconds;
            Call(canvas, "OnPaint", new PaintEventArgs(graphics, canvas.ClientRectangle));
            var afterPaint = heartbeat.Elapsed.TotalMilliseconds;
            Application.DoEvents();
            var beat = heartbeat.Elapsed.TotalMilliseconds;
            if (beat - priorBeat > 100)
                Console.WriteLine($"Provider heartbeat {beats}: scene {afterPaint - beforePaint:F1}ms, message pump {beat - afterPaint:F1}ms.");
            gaps.Add(beat - priorBeat);
            priorBeat = beat;
            beats++;
        }
        contention.GetAwaiter().GetResult();
        Console.WriteLine($"Map UI refresh with busy credential store: {blockedMilliseconds:F1}ms.");
        Console.WriteLine($"Blocked-provider map navigation: {beats} frames, max heartbeat gap {(gaps.Count == 0 ? 0 : gaps.Max()):F1}ms.");
        Check(blockedMilliseconds < 150, "a one-second credential-store wait must not block map movement or zoom");
        Check(beats >= 5 && gaps.Max() < 200, "zoom, paint and message-pump heartbeats continue during the entire one-second provider stall");
        pendingRead.GetAwaiter().GetResult();
        Tick();
        WaitForMap(() => Field<IReadOnlyDictionary<string, UpsSnapshot>>(map, "_upsSnapshots").ContainsKey(device.Id));
        Check(Field<IReadOnlyDictionary<string, UpsSnapshot>>(map, "_upsSnapshots")[device.Id].Quality != UpsQuality.Good,
            "background refresh still validates connection identity instead of displaying unverified Good data");
        ValidateNonBlockingMapDiagnostics(shell, plc);
        Field<Task>(shell, "_mapUpsReadTask").GetAwaiter().GetResult();
        typeof(MainShellForm).GetField("_mapUpsReadTask", Instance)!.SetValue(shell,
            Task.FromResult<(IReadOnlyDictionary<string, UpsSnapshot> Snapshots, string Error)>(
                (new Dictionary<string, UpsSnapshot>(), "Synthetic refresh failure")));
        Tick();
        Check(Field<IReadOnlyDictionary<string, UpsSnapshot>>(map, "_upsSnapshots").Count == 0 &&
            Field<Label>(shell, "_footerStatus").Text.Contains("MAP DATA ERROR: Synthetic refresh failure"),
            "a failed asynchronous refresh explicitly reports its error and clears current UPS data");
        WaitForMap(() => !Field<Label>(shell, "_footerStatus").Text.Contains("Synthetic refresh failure"));
        Check(Field<IReadOnlyDictionary<string, UpsSnapshot>>(map, "_upsSnapshots").ContainsKey(device.Id),
            "a recovered background refresh restores current source status and clears its prior error");
        Field<Task>(shell, "_mapUpsReadTask").GetAwaiter().GetResult();
        Call(shell, "StartPagePrewarm");
        var prewarm = Field<System.Windows.Forms.Timer>(shell, "_prewarmTimer");
        typeof(MainShellForm).GetField("_shutdownComplete", Instance)!.SetValue(shell, true);
        shell.Close();
        Check(!prewarm.Enabled, "closing the shell stops its deferred page-prewarm timer");
        Console.WriteLine($"Shell refresh/navigation assertions passed: {assertions}.");
    }

    internal static void RunMapInteraction()
    {
        typeof(PostgresDatabase).GetField("_connectionString", Static)!.SetValue(null,
            "Host=127.0.0.1;Port=1;Username=isolated_qa;Database=isolated_qa;Timeout=1;Pooling=false");
        typeof(PostgresDatabase).GetField("_lastFailureUtc", Static)!.SetValue(null, DateTime.UtcNow);
        RunMapQueuedInteraction(false);
        RunMapQueuedInteraction(true);
    }

    private static void RunMapQueuedInteraction(bool editing)
    {
        using var shell = new MainShellForm(new AppSettings(),
            new UserSession { RoleName = "Administrator", IsOfflinePreview = true })
            { SuppressBackgroundServices = true, WindowState = FormWindowState.Normal,
                Size = new Size(1920, 1080), TopMost = true };
        shell.Show(); Application.DoEvents();
        Call(shell, "ShowPage", "Factory Map");
        var map = (FactoryMapView)Field<Control>(shell, "_currentView");
        var canvas = Field<Control>(map, "_canvas");
        for (Control? ancestor = canvas; ancestor != null; ancestor = ancestor.Parent)
            Check((GetWindowLong(ancestor.Handle, -20) & 0x02000000) == 0,
                "the live map window and every native ancestor must be free of whole-tree compositing");
        var directory = Environment.GetEnvironmentVariable("EOEMS_MAP_PERF_DATA");
        if (!string.IsNullOrEmpty(directory))
        {
            var assets = Field<IList>(map, "_assets");
            var layout = typeof(FactoryMapView).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapLayoutStore")!;
            var loaded = (IList)layout.GetMethod("LoadAssets", Static)!.Invoke(null,
                new object[] { assets, Path.Combine(directory, "factory-map-layout.json") })!;
            assets.Clear(); foreach (var item in loaded) assets.Add(item);
            var store = typeof(FactoryMapView).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapBuildingStore")!;
            Call(canvas, "SetBuildingParts", store.GetMethod("Load", Static)!.Invoke(null,
                new object[] { Path.Combine(directory, "factory-map-building.json") }));
        }
        canvas.GetType().GetProperty("EditMode")!.SetValue(canvas, editing);
        if (editing) Call(canvas, "ZoomBy", 4f);
        Console.WriteLine($"Queued map workload: {(editing ? "edit, including quiet fire symbols" : "normal")}.");
        canvas.Invalidate(); Application.DoEvents();
        Call(shell, "StartPagePrewarm");
        shell.Activate();
        var clock = Stopwatch.StartNew();
        var paintGaps = new List<double>();
        var panZoomGaps = new List<double>();
        var orbitGaps = new List<double>();
        var gc = GC.CollectionCount(2);
        var bytes = GC.GetTotalAllocatedBytes();
        var priorPaint = 0d;
        var maximumInputGap = 0d;
        var priorInput = 0d;
        canvas.Paint += (_, _) =>
        {
            var now = clock.Elapsed.TotalMilliseconds;
            if (priorPaint > 0)
            {
                paintGaps.Add(now - priorPaint);
                (now < 15000 ? panZoomGaps : orbitGaps).Add(now - priorPaint);
            }
            if (priorPaint > 0 && now - priorPaint > 200)
                Console.WriteLine($"Delayed queued paint at {now:F0}ms: gap {now - priorPaint:F1}ms, last input {now - priorInput:F1}ms ago; state {shell.WindowState}, visible {canvas.Visible}, active {Form.ActiveForm == shell}.");
            priorPaint = now;
        };
        using var input = new System.Windows.Forms.Timer { Interval = 16 };
        var step = 0;
        var orbit = false;
        var hovering = false;
        var point = new Point(canvas.Width / 2, canvas.Height / 2);
        Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Middle, 1, point.X, point.Y, 0));
        input.Tick += (_, _) =>
        {
            var now = clock.Elapsed.TotalMilliseconds;
            if (priorInput > 0) maximumInputGap = Math.Max(maximumInputGap, now - priorInput);
            priorInput = now;
            if (clock.Elapsed.TotalSeconds >= 30) { input.Stop(); return; }
            var nextOrbit = clock.Elapsed.TotalSeconds >= 15;
            if (nextOrbit != orbit)
            {
                Call(canvas, "OnMouseUp", new MouseEventArgs(MouseButtons.Middle, 1, point.X, point.Y, 0));
                orbit = nextOrbit;
                hovering = false;
                Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Right, 1, point.X, point.Y, 0));
                Console.WriteLine($"Orbit started: rotating {Field<bool>(canvas, "_rotating")}, capture {canvas.Capture}, visible {canvas.Visible}.");
            }
            if (!orbit)
            {
                var nextHover = step % 80 >= 50;
                if (nextHover != hovering)
                {
                    if (nextHover) Call(canvas, "OnMouseUp", new MouseEventArgs(MouseButtons.Middle, 1, point.X, point.Y, 0));
                    else Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Middle, 1, point.X, point.Y, 0));
                    hovering = nextHover;
                }
            }
            point.X += step % 40 < 20 ? 2 : -2;
            Call(canvas, "OnMouseMove", new MouseEventArgs(orbit ? MouseButtons.Right : hovering ? MouseButtons.None : MouseButtons.Middle,
                0, hovering ? step * 167 % canvas.Width : point.X, hovering ? step * 137 % canvas.Height : point.Y, 0));
            if (!orbit && (step % 60 == 0 || (hovering && step % 4 == 0)))
                Call(canvas, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, point.X, point.Y,
                    hovering ? (step % 8 == 0 ? 24 : -24) : step % 120 == 0 ? 240 : -240));
            step++;
        };
        input.Start();
        while (clock.Elapsed.TotalSeconds < 30)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }
        input.Stop();
        Console.WriteLine($"Interaction ended: rotating {Field<bool>(canvas, "_rotating")}, capture {canvas.Capture}, visible {canvas.Visible}, active {Form.ActiveForm == shell}.");
        Call(canvas, "OnMouseUp", new MouseEventArgs(MouseButtons.Right, 1, point.X, point.Y, 0));
        Check(Field<System.Collections.Generic.Queue<string>>(shell, "_prewarmQueue").Count == 8,
            "hidden page constructors and handle preparation stay deferred throughout map interaction");
        Check(paintGaps.Count >= 300, "queued interaction must measure actual paints, not hidden-window input iterations");
        Console.WriteLine($"Queued map presentation: {paintGaps.Count} frames; p95 gap {paintGaps.Order().ElementAt((int)(paintGaps.Count * .95)):F1}ms; max gap {paintGaps.Max():F1}ms; inputs {step}, max input gap {maximumInputGap:F1}ms; allocated {(GC.GetTotalAllocatedBytes() - bytes) / 1048576d:F1}MB; gen2 {GC.CollectionCount(2) - gc}.");
        foreach (var (phase, gaps) in new[] { ("Pan/zoom", panZoomGaps), ("Orbit", orbitGaps) })
        {
            Check(gaps.Count > 0, phase + " must actually present queued frames");
            Console.WriteLine($"{phase}: {gaps.Count} paints, p95 {gaps.Order().ElementAt((int)(gaps.Count * .95)):F1}ms, max {gaps.Max():F1}ms.");
        }
        var host = Field<Control>(shell, "_pageHost");
        var createParams = typeof(Control).GetProperty("CreateParams", Instance)!;
        var mapHandle = canvas.Handle;
        Call(shell, "ShowPage", "Transformers");
        Check((((CreateParams)createParams.GetValue(host)!).ExStyle & 0x02000000) != 0,
            "leaving the map restores whole-page buffering for form/grid pages");
        Check((GetWindowLong(host.Handle, -20) & 0x02000000) != 0,
            "ordinary pages restore compositing on the actual native host window");
        Call(shell, "ShowPage", "Factory Map");
        Check(canvas.Handle == mapHandle &&
            (((CreateParams)createParams.GetValue(host)!).ExStyle & 0x02000000) == 0,
            "cached map navigation keeps the canvas handle and removes whole-page buffering again");
        Check((GetWindowLong(host.Handle, -20) & 0x02000000) == 0,
            "returning to the map removes compositing from the actual native host window");
        typeof(MainShellForm).GetField("_shutdownComplete", Instance)!.SetValue(shell, true);
        shell.Close();
        Check(paintGaps.Order().ElementAt((int)(paintGaps.Count * .95)) < 100 &&
            paintGaps.Max() < 200, "naturally queued mouse pan/zoom/orbit must keep presenting complete frames without long gaps");
    }

    private static void ValidateNonBlockingMapDiagnostics(MainShellForm shell, PlcConnectionService plc)
    {
        void Unblocked(object gate, Action action, string message)
        {
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var worker = Task.Run(() =>
            {
                lock (gate) { entered.Set(); release.Wait(TimeSpan.FromSeconds(1)); }
            });
            if (!entered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Diagnostic-lock fixture did not start.");
            var watch = Stopwatch.StartNew();
            try { action(); }
            finally { release.Set(); worker.GetAwaiter().GetResult(); }
            Check(watch.Elapsed.TotalMilliseconds < 150, message);
        }
        var folder = Path.Combine(Path.GetTempPath(), "eoems-map-contention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var fire = new FireConnectionService(new FireConnectionStore(Path.Combine(folder, "fire.json")), allowCommunication: false);
            Unblocked(Field<object>(fire, "_gate"), () =>
                Check(fire.ReadMapEventEvidence().Count == 0, "fire history snapshot preserves unknown/live-state boundary"),
                "map fire-history reads never wait for the service I/O lock");
            var outboxType = typeof(MainShellForm).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.DurableRecordingOutbox")!;
            using var outbox = (IDisposable)Activator.CreateInstance(outboxType, new object[] { Path.Combine(folder, "outbox.db") })!;
            typeof(MainShellForm).GetField("_recordingOutbox", Instance)!.SetValue(shell, outbox);
            try
            {
                Unblocked(Field<object>(outbox, "_gate"), () =>
                {
                    foreach (var name in new[] { "LastWriteUtc", "LastUpsWriteUtc", "LastTransformerWriteUtc", "LastError", "LastUpsRows" })
                        _ = outboxType.GetProperty(name)!.GetValue(outbox);
                    typeof(MainShellForm).GetField("_lastDiagnosticsTicks", Instance)!.SetValue(shell, 0L);
                    typeof(MainShellForm).GetField("_hasPendingStatus", Instance)!.SetValue(shell, true);
                    typeof(MainShellForm).GetField("_pendingStatus", Instance)!.SetValue(shell, plc.CurrentStatus);
                    Call(shell, "OnUiRefreshTick", null, EventArgs.Empty);
                }, "shell footer and recording diagnostics never wait for SQLite write/replay locks");
            }
            finally { typeof(MainShellForm).GetField("_recordingOutbox", Instance)!.SetValue(shell, null); }
        }
        finally { Directory.Delete(folder, true); }
    }

    internal static void RunSaveStability(string evidence)
    {
        Directory.CreateDirectory(evidence);
        // This fixture writes local stores, so require a deliberately staged test-only executable.
        if (!AppContext.BaseDirectory.Contains("settings-save-isolated-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Stage the test binaries in a settings-save-isolated- directory before running this fixture.");
        var temporary = Path.Combine(AppContext.BaseDirectory, "fixture");
        Directory.CreateDirectory(temporary);
        typeof(PostgresDatabase).GetField("_connectionString", Static)!.SetValue(null,
            "Host=127.0.0.1;Port=1;Username=isolated_qa;Database=isolated_qa;Timeout=1;Pooling=false");
        typeof(PostgresDatabase).GetField("_lastFailureUtc", Static)!.SetValue(null, DateTime.UtcNow);
        typeof(UpsSettingsStore).GetField("_current", Static)!.SetValue(null, UpsSettings.CreateDefault());
        typeof(AlarmSettingsStore).GetField("_current", Static)!.SetValue(null, new AlarmSettings());
        using var form = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-2400, -1800) };
        form.Show();
        using var context = new WindowsFormsSynchronizationContext();
        void Act(Control view, string handler)
        {
            SynchronizationContext.SetSynchronizationContext(context);
            Call(view, handler, null, EventArgs.Empty);
        }
        TaskCompletionSource? pendingPlc = null;
        using var plc = new PlcSettingsView(new PlcSettings(), 0, Admin, (_, _) =>
            pendingPlc?.Task ?? Task.CompletedTask);
        using var alarm = new AlarmSettingsView(Admin);
        using var snmp = new UpsSnmpSettingsView(Admin);
        var recording = new RecordingSettings { ExportFolderPath = temporary, AutomaticDailyExcelEnabled = false };
        recording.Normalize();
        using var historian = new MeasurementHistorian(Path.Combine(temporary, "history.db"), recordingSettings: recording, migrateLegacyDatabase: false);
        using var scheduler = new DailyHistorianExportScheduler(historian, recording, temporary);
        var backupSettings = new BackupSettings { BackupFolderPath = Path.Combine(temporary, "backups") };
        using var backupService = new BackupRecoveryService(historian, backupSettings, recording, temporary);
        var backupSaves = 0;
        var recordingSaves = 0;
        var acquisitionSaves = 0;
        using var backup = new BackupRecoveryView(backupSettings, Admin, backupService, _ => { backupSaves++; return Task.CompletedTask; },
            _ => Task.FromResult(new RestorePreparationResult()), () => { });
        using var reports = new HistorianView(recording, Admin, historian, scheduler, _ => { recordingSaves++; return Task.CompletedTask; },
            plcSettings: new PlcSettings(), savePlcSettings: (_, _) => { acquisitionSaves++; return Task.CompletedTask; });
        using var fire = new FireConnectionService(new FireConnectionStore(Path.Combine(temporary, "fire.json")), allowCommunication: false);
        using var versions = new ConfigVersionService(temporary);
        ConfigurationView? configurationReference = null;
        var configurationPlcSaves = 0;
        var configurationBackupSaves = 0;
        using var configuration = new ConfigurationView(new PlcSettings(), 0, new PlcConnectionStatus(),
            Array.Empty<MeterSnapshot>(), Admin, fire, backupSettings, backupService, versions,
            (settings, refresh) =>
            {
                configurationPlcSaves++;
                configurationReference!.UpdatePlcSettings(settings, refresh);
                return Task.CompletedTask;
            }, _ => { configurationBackupSaves++; return Task.CompletedTask; },
            _ => Task.FromResult(new RestorePreparationResult()), () => { }, () => { });
        configurationReference = configuration;
        foreach (var size in new[] { new Size(980, 600), new Size(1300, 650), new Size(1620, 830) })
        {
            form.ClientSize = size;
            Mount(plc);
            Stable(plc, () =>
            {
                var host = Field<TextBox>(plc, "_host");
                host.Text = host.Text == "127.0.0.2" ? "127.0.0.3" : "127.0.0.2";
                pendingPlc = new TaskCompletionSource();
                Act(plc, "OnSaveClicked");
                Check(Field<bool>(plc, "_saving"), "PLC exercises the asynchronous saving state");
                Application.DoEvents();
                pendingPlc.SetResult();
            }, () => !Field<bool>(plc, "_saving"), "plc");
            Mount(alarm);
            Stable(alarm, () => Act(alarm, "OnSaveClicked"), () => true, "alarm");
            Check(Field<Label>(alarm, "_message").Text.StartsWith("Alarm limits saved"), "alarm Save actually persisted fixture limits");
            Mount(backup);
            var beforeBackup = backupSaves;
            Stable(backup, () => Act(backup, "OnSaveClicked"), () => true, "backup-policy");
            Check(backupSaves == beforeBackup + 4, "every backup Save reaches the persistence callback");
            Mount(snmp);
            var snmpTabs = Field<TabControl>(snmp, "_tabs");
            snmpTabs.SelectedIndex = 0;
            Stable(snmp, () => Act(snmp, "OnSaveRegistry"), () => true, "snmp-registry");
            Check(Field<Label>(snmp, "_registryMessage").Text.StartsWith("Saved and audited"), "SNMP registry Save actually persisted fixture registry");
            snmpTabs.SelectedIndex = 1;
            var cardTabs = Field<TabControl>(snmp, "_cardTabs");
            var profileName = "save-layout-" + size.Width;
            cardTabs.SelectedIndex = 1;
            Call(snmp, "ClearProfileForm", false);
            Field<TextBox>(snmp, "_profileName").Text = profileName;
            Field<ComboBox>(snmp, "_profileVersion").SelectedItem = "v2c";
            Field<TextBox>(snmp, "_community").Text = "fixture-only-community";
            Field<TextBox>(snmp, "_confirmCommunity").Text = "fixture-only-community";
            Stable(snmp, () => Act(snmp, "OnSaveProfile"), () => true, "snmp-profile");
            Check(SnmpCredentialStore.ListProfiles().Any(profile => profile.Name == profileName), "credential Save actually persisted a fixture profile");
            cardTabs.SelectedIndex = 2;
            var credentialDevice = (UpsDevice)Field<ComboBox>(snmp, "_credentialDevice").SelectedItem!;
            Field<TextBox>(snmp, "_webUrl").Text = "https://" + credentialDevice.IpAddress;
            Field<TextBox>(snmp, "_webUser").Text = "fixture-only-user";
            Field<TextBox>(snmp, "_webPassword").Text = "fixture-only-password";
            Stable(snmp, () => Act(snmp, "OnSaveWebCredentials"), () => true, "web-card");
            Check(Field<bool>(snmp, "_webPasswordSaved"), "web-card Save persisted fixture credentials without contacting card");
            Mount(reports);
            var reportTabs = Field<TabControl>(reports, "_tabs");
            reportTabs.SelectedIndex = 0;
            var beforeAcquisition = acquisitionSaves;
            Stable(reports, () =>
            {
                var interval = Field<ComboBox>(reports, "_acquisitionInterval");
                interval.SelectedIndex = interval.SelectedIndex == 1 ? 2 : 1;
                Act(reports, "OnSaveAcquisitionClicked");
            }, () => !Field<bool>(reports, "_savingAcquisition"), "acquisition");
            Check(acquisitionSaves == beforeAcquisition + 4, "every acquisition Save reaches the persistence callback");
            reportTabs.SelectedIndex = 1;
            var beforeRecording = recordingSaves;
            Stable(reports, () => Act(reports, "OnSaveClicked"), () => !Field<bool>(reports, "_savingRecording"), "recording");
            Check(recordingSaves == beforeRecording + 4, "every recording Save reaches the persistence callback");
            Mount(configuration);
            configuration.SetPageActive(true);
            var tabs = Field<TabControl>(configuration, "_tabs");
            Wait(() => tabs.TabPages.Cast<TabPage>().All(page => page.Controls.Count == 1), "Configuration sections are loaded");
            var sections = tabs.TabPages.Cast<TabPage>().Select(page => page.Controls[0]).ToArray();
            foreach (var index in Enumerable.Range(0, sections.Length))
            {
                tabs.SelectedIndex = index;
                Pump();
                if (index == 0)
                {
                    var before = configurationPlcSaves;
                    Stable(configuration, () =>
                    {
                        var host = Field<TextBox>(sections[index], "_host");
                        host.Text = host.Text == "127.0.0.2" ? "127.0.0.3" : "127.0.0.2";
                        Act(sections[index], "OnSaveClicked");
                    }, () => !Field<bool>(sections[index], "_saving"), "configuration-plc");
                    Check(configurationPlcSaves == before + 4, "Configuration PLC saves use the shell's model-update path");
                }
                else if (index == 1)
                    Stable(configuration, () => Act(sections[index], "OnSaveClicked"), () => true, "configuration-alarm");
                else if (index == 4)
                {
                    var before = configurationBackupSaves;
                    Stable(configuration, () => Act(sections[index], "OnSaveClicked"), () => true, "configuration-backup");
                    Check(configurationBackupSaves == before + 4, "Configuration backup saves reach the persistence callback");
                }
                var fonts = Descendants(sections[index]).Where(control => control.Visible).ToDictionary(control => control,
                    control => (control.Font.Name, control.Font.SizeInPoints, control.Font.Style));
                Application.DoEvents();
                foreach (var pair in fonts)
                    Check(!pair.Key.IsDisposed && pair.Value == (pair.Key.Font.Name, pair.Key.Font.SizeInPoints, pair.Key.Font.Style),
                        $"Configuration section {index} typography remains stable at {size}");
                Check(ReferenceEquals(tabs.SelectedTab!.Controls[0], sections[index]), "Configuration retains cached section controls");
            }
            configuration.SetPageActive(false);
        }
        File.WriteAllText(Path.Combine(evidence, "settings-save-validation.json"),
            System.Text.Json.JsonSerializer.Serialize(new { assertions, productionSettingsChanged = false, realNetworkContacted = false }));
        Console.WriteLine($"Settings Save stability assertions passed: {assertions}.");
        form.Controls.Clear();

        void Mount(Control view)
        {
            form.Controls.Clear();
            view.Dock = DockStyle.Fill;
            form.Controls.Add(view);
            Pump();
        }

        void Stable(Control view, Action save, Func<bool> idle, string name)
        {
            Pump();
            var controls = Descendants(view).Where(control => control.Visible &&
                (control is Label || control is TextBox || control is ComboBox || control is NumericUpDown ||
                 control is CheckBox || control is Button || control is DataGridView)).ToArray();
            var baseline = controls.ToDictionary(control => control, control =>
                (control.Text, Bounds: new Rectangle(view.PointToClient(control.PointToScreen(Point.Empty)), control.Size),
                 Font: (control.Font.Name, control.Font.SizeInPoints, control.Font.Style), control.MaximumSize));
            for (var cycle = 0; cycle < 4; cycle++)
            {
                save();
                Wait(idle, name + " repeated Save completes");
                Compare();
                Pump();
                Compare();
            }
            using var image = new Bitmap(view.Width, view.Height);
            view.DrawToBitmap(image, view.ClientRectangle);
            image.Save(Path.Combine(evidence, $"{name}-after-save-{form.ClientSize.Width}.png"));

            void Compare()
            {
                foreach (var control in controls)
                {
                    Check(!control.IsDisposed && control.Visible, name + " Save preserves visible control identity");
                    var previous = baseline[control];
                    Check(previous.Font == (control.Font.Name, control.Font.SizeInPoints, control.Font.Style),
                        $"{name} Save preserves font of {control.GetType().Name}");
                    if (control is not Label || control.Text == previous.Text)
                        Check(previous.Bounds == new Rectangle(view.PointToClient(control.PointToScreen(Point.Empty)), control.Size) &&
                              previous.MaximumSize == control.MaximumSize,
                            $"{name} Save preserves bounds/wrap of {control.GetType().Name} at {form.ClientSize}: {previous.Bounds} -> {control.Bounds}");
                    if (control is DataGridView grid)
                        Check(grid.DefaultCellStyle.Font?.FontFamily.Name == "Segoe UI" && grid.ColumnHeadersDefaultCellStyle.Font?.FontFamily.Name == "Segoe UI",
                            name + " Save retains grid and header fonts");
                }
            }
        }
    }

    internal static void RunAlarmSteps()
    {
        using var editor = new AlarmSettingsView(Admin);
        ValidateAlarmSteps(editor);
        using var readOnly = new AlarmSettingsView(null!);
        Check(Field<Dictionary<string, NumericUpDown>>(readOnly, "_inputs").Values.All(input => !input.Enabled),
            "fractional arrow increments do not change read-only access");
        Console.WriteLine($"Alarm step assertions passed: {assertions}.");
    }

    private static void ValidateAlarmSteps(AlarmSettingsView editor)
    {
        var inputs = Field<Dictionary<string, NumericUpDown>>(editor, "_inputs");
        Check(inputs.Count == 40, "all five equipment groups expose eight alarm-limit parameters");
        foreach (var input in inputs.Values)
        {
            var originalValue = input.Value;
            Check(input.Increment == 0.1m && input.DecimalPlaces == 1, "every alarm-limit arrow uses a one-tenth increment");
            input.Value = 215m;
            input.UpButton();
            Check(input.Value == 215.1m, "one upward arrow changes 215 to 215.1");
            input.DownButton();
            Check(input.Value == 215m, "one downward arrow changes 215.1 back to 215");
            input.Value = input.Minimum;
            input.DownButton();
            Check(input.Value == input.Minimum, "downward arrows preserve the lower bound");
            input.Value = input.Maximum;
            input.UpButton();
            Check(input.Value == input.Maximum, "upward arrows preserve the upper bound");
            input.Value = originalValue;
        }
    }

    internal static void Run(string evidence)
    {
        evidence = Path.GetFullPath(evidence);
        Directory.CreateDirectory(evidence);
        PageHostTransitions();
        Check(UiTheme.ShellPageTitleFont.FontFamily.Name == "Segoe UI" &&
              UiTheme.ShellPageTitleFont.Size == 11.5F &&
              UiTheme.ShellPageTitleFont.Style == FontStyle.Bold,
            "all page titles use one consistent font in the shell header");
        var temporary = Path.Combine(Path.GetTempPath(), "eoems-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var credentialsPath = SnmpCredentialStore.StorePath();
        var credentials = File.Exists(credentialsPath) ? File.ReadAllBytes(credentialsPath) : null;
        var settings = new AppSettings();
        var recording = new RecordingSettings { ExportFolderPath = temporary };
        recording.Normalize();
        File.WriteAllText(Path.Combine(temporary, "appsettings.json"), "{}");
        typeof(PostgresDatabase).GetField("_connectionString", Static)!.SetValue(null,
            "Host=127.0.0.1;Port=1;Username=isolated_qa;Database=isolated_qa;Timeout=1;Pooling=false");
        typeof(PostgresDatabase).GetField("_lastFailureUtc", Static)!.SetValue(null, DateTime.UtcNow);
        typeof(UpsSettingsStore).GetField("_current", Static)!.SetValue(null, UpsSettings.CreateDefault());
        typeof(AlarmSettingsStore).GetField("_current", Static)!.SetValue(null, new AlarmSettings());
        Directory.CreateDirectory(Path.GetDirectoryName(credentialsPath)!);
        File.WriteAllText(credentialsPath, "[]");
        try
        {
            using var historian = new MeasurementHistorian(Path.Combine(temporary, "history.db"), recordingSettings: recording, migrateLegacyDatabase: false);
            using var scheduler = new DailyHistorianExportScheduler(historian, recording, temporary);
            using var fire = new FireConnectionService(
                new FireConnectionStore(Path.Combine(temporary, "fire-connection.json")), allowCommunication: false);
            var backupSettings = new BackupSettings();
            using var backup = new BackupRecoveryService(historian, backupSettings, recording, temporary);
            using var configVersions = new ConfigVersionService(temporary);
            configVersions.SaveVersion("settings UI repaint validation");
            using var configuration = new ConfigurationView(settings.Plc, settings.Ui.RefreshIntervalMilliseconds,
                new PlcConnectionStatus(), Array.Empty<MeterSnapshot>(), Admin, fire, backupSettings, backup,
                configVersions, (_, _) => Task.CompletedTask, _ => Task.CompletedTask,
                _ => Task.FromResult(new RestorePreparationResult()), () => { }, () => { });
            var configurationTabs = Field<TabControl>(configuration, "_tabs");
            Check(configurationTabs.TabPages.Cast<TabPage>().Select(page => page.Text).SequenceEqual(
                    new[] { "PLC system", "Alarm limits", "Fire system", "System diagnostics", "Backup & recovery", "Settings history", "Device registry" }) &&
                  configurationTabs.TabPages[0].Controls.OfType<PlcSettingsView>().Any() &&
                  configurationTabs.TabPages.Cast<TabPage>().Skip(1).All(page => page.Controls.Count == 0),
                "Configuration constructs its initial section quickly and defers warm-up until the workspace is visible");
            string? savedHost = null;
            var savedRefresh = -1;
            using (var plcEditor = new PlcSettingsView(settings.Plc, 0, Admin, (updated, refresh) =>
                   {
                       savedHost = updated.Host;
                       savedRefresh = refresh;
                       return Task.CompletedTask;
                   }))
            {
                Field<TextBox>(plcEditor, "_host").Text = "127.0.0.2";
                Field<ComboBox>(plcEditor, "_displayRefresh").SelectedIndex = 5;
                Field<Button>(plcEditor, "_saveButton").PerformClick();
                Check(savedHost == "127.0.0.2" && savedRefresh == 250,
                    "PLC/display editor stages validated settings through its save callback without direct I/O");
            }
            using (var configurationForm = new Form
            {
                ClientSize = new Size(1160, 680),
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-2400, -1800)
            })
            {
                configuration.Dock = DockStyle.Fill;
                configurationForm.Controls.Add(configuration);
                configurationForm.Show();
                Pump();
                configuration.SetPageActive(true);
                Wait(() => configurationTabs.TabPages.Cast<TabPage>().All(page => page.Controls.Count == 1),
                    "all Configuration sections preload on the visible idle workspace before section navigation");
                var preloadedSections = configurationTabs.TabPages.Cast<TabPage>()
                    .Select(page => page.Controls[0]).ToArray();
                Check(configurationTabs.SelectedIndex == 0 &&
                      !Field<System.Windows.Forms.Timer>(((FirePanelsView)preloadedSections[2]).CurrentView, "_timer").Enabled &&
                      !Field<System.Windows.Forms.Timer>(preloadedSections[4], "_refreshTimer").Enabled &&
                      !Field<System.Windows.Forms.Timer>(preloadedSections[5], "_refreshTimer").Enabled,
                    "preloading hidden sections leaves the active page selected and their refresh timers stopped");
                configurationTabs.SelectedIndex = 1;
                Check(configurationTabs.TabPages[1].Controls.OfType<AlarmSettingsView>().Any(),
                    "preloaded alarm section is available immediately when selected");
                ValidateAlarmSteps(configurationTabs.TabPages[1].Controls.OfType<AlarmSettingsView>().Single());
                configurationTabs.SelectedIndex = 2;
                Check(Field<System.Windows.Forms.Timer>(((FirePanelsView)configurationTabs.TabPages[2].Controls[0]).CurrentView, "_timer").Enabled,
                    "fire diagnostics timer starts only while its Configuration section is active");
                configurationTabs.SelectedIndex = 3;
                var diagnostics = configurationTabs.TabPages[3].Controls.OfType<SystemDataView>().Single();
                var blockGrid = Field<DataGridView>(diagnostics, "_blockGrid");
                blockGrid.Rows[0].Selected = true;
                diagnostics.SetReadings(Array.Empty<MeterSnapshot>());
                Check(blockGrid.Rows[0].Selected,
                    "repeated diagnostics updates preserve row selection instead of clearing and repainting the grid");
                Check(configurationTabs.TabPages[3].Controls.OfType<SystemDataView>().Any(),
                    "preloaded diagnostics section is available immediately when selected");
                configurationTabs.SelectedIndex = 4;
                Check(Field<System.Windows.Forms.Timer>(configurationTabs.TabPages[4].Controls[0], "_refreshTimer").Enabled,
                    "backup status refresh runs while its Configuration section is active");
                configurationTabs.SelectedIndex = 5;
                Check(Field<System.Windows.Forms.Timer>(configurationTabs.TabPages[5].Controls[0], "_refreshTimer").Enabled,
                    "settings history refresh runs while its Configuration section is active");
                Check(configurationTabs.TabPages[1].Controls.OfType<AlarmSettingsView>().Any() &&
                      configurationTabs.TabPages[2].Controls.OfType<FirePanelsView>().Any() &&
                      configurationTabs.TabPages[3].Controls.OfType<SystemDataView>().Any() &&
                      configurationTabs.TabPages[4].Controls.OfType<BackupRecoveryView>().Any() &&
                      configurationTabs.TabPages[5].Controls.OfType<ConfigVersionsView>().Any(),
                    "all Configuration sections are preloaded and remain cached");
                var versionsView = configurationTabs.TabPages[5].Controls.OfType<ConfigVersionsView>().Single();
                var versionGrid = Field<DataGridView>(versionsView, "_grid");
                Check(versionGrid.Rows.Count > 0, "isolated settings version is displayed");
                var versionRow = versionGrid.Rows[0];
                Call(versionsView, "RefreshList");
                Check(ReferenceEquals(versionRow, versionGrid.Rows[0]),
                    "unchanged settings history refresh keeps existing rows to avoid periodic blinking");
                configurationTabs.SelectedIndex = 0;
                configuration.SetPageActive(false);
                Check(!Field<System.Windows.Forms.Timer>(((FirePanelsView)configurationTabs.TabPages[2].Controls[0]).CurrentView, "_timer").Enabled &&
                      !Field<System.Windows.Forms.Timer>(configurationTabs.TabPages[4].Controls[0], "_refreshTimer").Enabled &&
                      !Field<System.Windows.Forms.Timer>(configurationTabs.TabPages[5].Controls[0], "_refreshTimer").Enabled,
                    "all Configuration section timers stop when the workspace is hidden");
                ConfigurationNavigationStability(configuration, configurationTabs, preloadedSections, evidence);
                configurationForm.Controls.Remove(configuration);
            }
            using var snmp = new UpsSnmpSettingsView(Admin);
            using var dashboard = new DashboardView(settings.Plc, new PlcConnectionStatus(), () => { }, historian, scheduler);
            var dashboardMetrics = Descendants(dashboard).OfType<TableLayoutPanel>().Single(panel => panel.Controls.Count == 5);
            var dashboardActions = Descendants(dashboard).OfType<TableLayoutPanel>()
                .Single(panel => panel.Controls.OfType<Button>().Count() == 4);
            Check(dashboardMetrics.ColumnCount == 5 &&
                Descendants(dashboard).OfType<Label>().Single(label => label.Name == "SNMP UPS UNITS").Text == "OFFLINE" &&
                dashboardActions.Top == 0 && dashboardMetrics.Top == 47 &&
                !Descendants(dashboard).OfType<Label>().Any(label => label.Text == "Overview"),
                "Overview includes the SNMP reporting tile and marks polling unavailable offline");
            using var transformers = new TransformersView(settings.Plc, new PlcConnectionStatus(),
                Array.Empty<MeterSnapshot>(), () => Task.CompletedTask, canRead: false);
            Check(!Descendants(transformers).OfType<Label>().Any(label => label.Text == "TRANSFORMERS") &&
                  Descendants(transformers).OfType<ScadaPanel>().Any(panel => panel.Height == 44),
                "Transformers omits its duplicate page title and keeps a compact status/action bar");
            using var reports = new HistorianView(recording, Admin, historian, scheduler, _ => Task.CompletedTask,
                plcSettings: settings.Plc, savePlcSettings: (_, _) => Task.CompletedTask);
            var historianStack = Field<TableLayoutPanel>(reports, "_stack");
            var historianTabs = Field<TabControl>(reports, "_tabs");
            var historianNavigation = historianStack.Controls.OfType<SegmentedTabStrip>().Single();
            Check(!Descendants(snmp).OfType<Label>().Any(label => label.Text == "SNMP Setting") &&
                  !Descendants(reports).OfType<Label>().Any(label => label.Text == "Historian / Reports"),
                "SNMP and Historian omit duplicate page titles");
            Check(historianStack.RowCount == 4 &&
                historianStack.RowStyles[0].Height == UiTheme.WorkspaceNavigationHeight &&
                historianStack.RowStyles[3].SizeType == SizeType.Absolute &&
                historianStack.RowStyles[3].Height == 62.0F &&
                historianStack.GetRow(historianNavigation) == 0 &&
                historianStack.GetRow(historianTabs) == 2,
                "Historian reserves a stable footer row so tab changes do not resize its content");
            Await(reports.RefreshHealthAsync());
            snmp.SetPageActive(true);
            foreach (var size in new[] { new Size(1160, 680), new Size(980, 600), new Size(1560, 900) })
            {
                var suffix = size.Width == 1160 ? "" : "-" + size.Width;
                CaptureTabs(configuration, configurationTabs, evidence, "configuration" + suffix, size);
                CaptureTabs(snmp, Field<TabControl>(snmp, "_tabs"), evidence, "snmp" + suffix, size);
                Field<TabControl>(snmp, "_tabs").SelectedIndex = 0;
                CaptureTabs(snmp, (TabControl)Field<DataGridView>(snmp, "_registry").Parent!.Parent!, evidence, "snmp-registry" + suffix, size);
                Field<TabControl>(snmp, "_tabs").SelectedIndex = 1;
                CaptureTabs(snmp, Field<TabControl>(snmp, "_cardTabs"), evidence, "snmp-card" + suffix, size);
                CaptureTabs(reports, Field<TabControl>(reports, "_tabs"), evidence, "historian" + suffix, size);
                Field<TabControl>(reports, "_tabs").SelectedIndex = 2;
                var dailySections = Descendants(Field<TabControl>(reports, "_tabs").TabPages[2]).OfType<TabControl>().Single();
                CaptureTabs(reports, dailySections, evidence, "daily-reports" + suffix, size);
            }

            Check(Field<TabControl>(reports, "_tabs").TabPages.Cast<TabPage>().Select(page => page.Text).SequenceEqual(
                new[] { "Acquisition", "Raw recording", "Daily reports", "Active events", "Equipment history", "Fire Events" }),
                "Transformer Reports page is removed, with equipment history, daily schedules and power events preserved");
            Check(typeof(HistorianView).GetMethod("BuildManualReportsTab", Instance) == null &&
                typeof(HistorianView).GetMethod("OnPreviewClicked", Instance) == null &&
                typeof(HistorianView).GetField("_reportAnalyzer", Instance) == null,
                "obsolete transformer report UI, filters and handlers are deleted rather than merely hidden");
            RegistryValues(snmp);
            RecordingSaves(historian, scheduler, temporary);
            ReportFailures(reports, historian, scheduler, recording, temporary);
            Permissions(settings, historian, scheduler, recording);
            Console.WriteLine($"Settings UI assertions passed: {assertions}.");
        }
        finally
        {
            if (credentials == null) File.Delete(credentialsPath);
            else File.WriteAllBytes(credentialsPath, credentials);
            SqliteConnection.ClearAllPools();
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static void PageHostTransitions()
    {
        var hostType = typeof(MainShellForm).Assembly.GetTypes().Single(type => type.Name == "PageHostPanel");
        using var host = (Control)Activator.CreateInstance(hostType, nonPublic: true)!;
        using var first = new Panel();
        using var second = new Panel();
        using var form = new Form
        {
            ClientSize = new Size(640, 480),
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-2400, -1800)
        };
        form.Controls.Add(host);
        host.Dock = DockStyle.Fill;
        var activatePage = hostType.GetMethod("ActivatePage", Instance)!;
        var hostPadding = new Padding(12, 10, 12, 8);
        Check(host.Padding == hostPadding,
            "page host uses fixed insets so cached views keep stable dimensions");
        Check(UiTheme.ShellPageTitleFont.Size == 11.5F,
            "page title font stays sized for the single shell header");

        activatePage.Invoke(host, new object[] { first });
        form.Show();
        Pump();
        activatePage.Invoke(host, new object[] { second });
        Pump();
        Check(host.Controls.Count == 2 && !first.Visible && second.Visible,
            "page transition keeps cached views hosted and shows only the selected page");
        Check(host.Padding == hostPadding,
            "switching cached pages does not resize the workspace host");

        activatePage.Invoke(host, new object[] { first });
        Pump();
        Check(host.Controls.Count == 2 && first.Visible && !second.Visible,
            "returning to a cached page preserves its host and hides the previous page");
        var firstPagePaints = 0;
        first.Paint += (_, _) => firstPagePaints++;
        Pump();
        var paintsBeforeReactivation = firstPagePaints;
        activatePage.Invoke(host, new object[] { first });
        Pump();
        Check(firstPagePaints == paintsBeforeReactivation,
            "reactivating the visible cached page does not force a full repaint");
        form.Close();
    }

    private static void ConfigurationNavigationStability(ConfigurationView configuration, TabControl tabs,
        Control[] preloadedSections, string evidence)
    {
        foreach (var size in new[] { new Size(1500, 768), new Size(1160, 680), new Size(980, 600) })
        {
            var form = configuration.FindForm()!;
            form.ClientSize = size;
            Pump();
            var tabBounds = tabs.Bounds;
            for (var index = 0; index < tabs.TabPages.Count; index++)
            {
                tabs.SelectedIndex = index;
                Pump();
                var page = tabs.TabPages[index];
                Check(tabs.Bounds == tabBounds, $"Configuration tab bounds remain fixed at {size.Width}: {page.Text}");
                Check(ReferenceEquals(page.Controls[0], preloadedSections[index]) &&
                      page.Controls[0].Visible &&
                      ReferenceEquals(tabs.SelectedTab, page),
                    $"Configuration {page.Text} reuses its preloaded view without rebuilding");
                using var bitmap = new Bitmap(configuration.Width, configuration.Height);
                configuration.DrawToBitmap(bitmap, configuration.ClientRectangle);
                bitmap.Save(Path.Combine(evidence, $"configuration-{size.Width}-{index}.png"));
            }

            for (var cycle = 0; cycle < 3; cycle++)
            {
                for (var index = tabs.TabPages.Count - 1; index >= 0; index--)
                {
                    tabs.SelectedIndex = index;
                    Pump();
                    Check(ReferenceEquals(tabs.TabPages[index].Controls[0], preloadedSections[index]) &&
                          tabs.TabPages[index].Controls[0].Visible,
                        $"repeated Configuration navigation remains steady: {tabs.TabPages[index].Text}, cycle {cycle + 1}");
                }
            }
        }
    }

    private static void CaptureControl(Control view, string directory, string prefix, Size size)
    {
        using var form = new Form
        {
            ClientSize = size,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-2400, -1800),
            Text = "Isolated UI audit"
        };
        view.Dock = DockStyle.Fill;
        form.Controls.Add(view);
        form.Show();
        Pump();
        using var bitmap = new Bitmap(view.Width, view.Height);
        view.DrawToBitmap(bitmap, view.ClientRectangle);
        bitmap.Save(Path.Combine(directory, $"{prefix}.png"));
        form.Controls.Remove(view);
        form.Close();
    }

    private static void CaptureTabs(Control view, TabControl tabs, string directory, string prefix, Size size)
    {
        using var form = new Form
        {
            ClientSize = size,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-2400, -1800),
            Text = "Isolated UI audit"
        };
        view.Dock = DockStyle.Fill;
        form.Controls.Add(view);
        form.Show();
        var tabsBounds = tabs.Bounds;
        for (var index = 0; index < tabs.TabPages.Count; index++)
        {
            tabs.SelectedIndex = index;
            Pump();
            var expectedBounds = tabsBounds;
            if (view is HistorianView && tabs == Field<TabControl>(view, "_tabs") && tabs.TabPages[index].Text == "Fire Events")
                expectedBounds = new Rectangle(tabsBounds.X, tabsBounds.Y - 46, tabsBounds.Width, tabsBounds.Height + 108);
            Check(tabs.Bounds == expectedBounds, prefix + " tab selection preserves its intended workspace bounds: " + tabs.TabPages[index].Text);
            using var bitmap = new Bitmap(view.Width, view.Height);
            view.DrawToBitmap(bitmap, view.ClientRectangle);
            bitmap.Save(Path.Combine(directory, $"{prefix}-{index}.png"));
            Check(tabs.SelectedIndex == index, prefix + " tab can be selected: " + tabs.TabPages[index].Text);
            if (view is HistorianView && tabs == Field<TabControl>(view, "_tabs") && tabs.SelectedIndex == 1)
            {
                Check(!Field<Panel>(view, "_resolutionPanel").Visible,
                    prefix + " raw recording stays collapsed until explicitly expanded");
                var interval = Field<ComboBox>(view, "_intervalCombo");
                var days = Field<NumericUpDown>(view, "_retentionDays");
                foreach (var input in new Control[] { interval, days })
                {
                    CheckVisible(input, view, prefix + " raw control " + input.GetType().Name);
                    Check(input.Parent!.ClientSize.Height - input.Bottom >= 8,
                        prefix + " raw control bottom border has at least 8 pixels of clear space: " +
                        $"{input.GetType().Name} bottom={input.Bottom}, parent={input.Parent.ClientSize.Height}, padding={input.Parent.Padding.Bottom}");
                }
                Check(!interval.RectangleToScreen(interval.ClientRectangle).IntersectsWith(
                    Field<Label>(view, "_retentionHint").RectangleToScreen(Field<Label>(view, "_retentionHint").ClientRectangle)),
                    prefix + " interval box and explanatory text do not overlap");
                Check(days.Enabled, prefix + " planning days remain editable");
                Check(!Descendants(tabs.TabPages[1]).OfType<Label>().Any(label => label.Text.Contains("CUSTOM INTERVAL")),
                    prefix + " no duplicate custom recording interval");
                Check(Field<Label>(view, "_storageEstimateHint").Text.Contains("unavailable", StringComparison.OrdinalIgnoreCase),
                    prefix + " unavailable database is disclosed instead of inventing a capacity estimate");
            }
            if (view is HistorianView && tabs == Field<TabControl>(view, "_tabs") && tabs.SelectedIndex == 4)
            {
                Check(!Descendants(view).OfType<Label>().Any(label => label.Text == "Historian / Reports"),
                    prefix + " no duplicate page title above equipment history");
                var history = Field<EquipmentHistoryView>(view, "_equipmentHistory");
                foreach (var name in new[] { "_equipment", "_channelSearch", "_channelKind", "_metric", "_channelCount", "_load" })
                    CheckVisible(Field<Control>(history, name), view, prefix + " raw workspace " + name);
                Check(Field<CheckedListBox>(history, "_metric").Height >= 72,
                    prefix + " full historian keeps usable channel list height");
                foreach (var button in Field<List<Button>>(history, "_exports"))
                    CheckVisible(button, view, prefix + " raw export action");
                var channels = Field<CheckedListBox>(history, "_metric");
                Check(!channels.Bounds.IntersectsWith(Field<Label>(history, "_channelCount").Bounds),
                    prefix + " channel count never overlaps channel checklist");
            }
            if (view is HistorianView && prefix.StartsWith("daily-reports") && tabs.SelectedIndex == 1)
            {
                CheckVisible(Field<Control>(view, "_saveButton"), view, prefix + " shared daily selection save");
                CheckVisible(Field<ListView>(view, "_parameters"), view, prefix + " daily measurement selection remains editable");
                foreach (var entry in Field<Dictionary<string, Button>>(view, "_analyzerButtons"))
                {
                    CheckVisible(entry.Value, view, prefix + " daily selection " + entry.Key);
                    entry.Value.PerformClick();
                    Check(Field<Label>(view, "_measurementHeading").Text.Contains(entry.Key),
                        prefix + " daily selection switches analyzer " + entry.Key);
                }
            }
            if (view is UpsSnmpSettingsView && prefix.StartsWith("snmp-card") && tabs.SelectedIndex == 2)
            {
                var button = Descendants(tabs.SelectedTab!).OfType<Button>().Single(control => control.Text == "Save Profile");
                CheckVisible(button, view, prefix + " profile save stays visible");
                Field<ComboBox>(view, "_profileVersion").SelectedItem = "v3";
                Pump();
                foreach (var name in new[] { "_v3User", "_securityMode", "_authProtocol", "_authSecret", "_privProtocol", "_privSecret" })
                {
                    var input = Field<Control>(view, name);
                    Ancestors(input).OfType<Panel>().First(panel => panel.AutoScroll).ScrollControlIntoView(input);
                    Pump();
                    CheckVisible(input, view, prefix + " reachable v3 field " + name);
                    CheckVisible(button, view, prefix + " v3 save stays visible");
                }
                using (var v3 = new Bitmap(view.Width, view.Height))
                {
                    view.DrawToBitmap(v3, view.ClientRectangle);
                    v3.Save(Path.Combine(directory, $"{prefix}-{index}-v3.png"));
                }
                Field<ComboBox>(view, "_profileVersion").SelectedItem = "v2c";
            }
        }
        form.Controls.Remove(view);
    }

    private static void RegistryValues(UpsSnmpSettingsView view)
    {
        var grid = Field<DataGridView>(view, "_registry");
        var banks = Field<DataGridView>(view, "_banks");
        var original = (UpsSettings)Call(view, "ReadRegistry")!;
        Check(original.Devices.Count == UpsSettingsStore.Current.Devices.Count && original.Banks.Count == UpsSettingsStore.Current.Banks.Count,
            "registry round-trips all devices and banks without a save");
        Check(original.Devices.Zip(UpsSettingsStore.Current.Devices.OrderBy(device => device.AtsBank).ThenBy(device => device.Label),
            (left, right) => left.Id == right.Id && left.Port == right.Port && left.TimeoutMilliseconds == right.TimeoutMilliseconds &&
                left.Retries == right.Retries && left.UseProfileTransport == right.UseProfileTransport && left.CredentialProfile == right.CredentialProfile).All(equal => equal),
            "opening and reading the registry retains transport settings and profile assignments");
        foreach (var column in new[] { "Port", "Interval", "Timeout", "Retries" })
        {
            var old = grid.Rows[0].Cells[column].Value;
            foreach (var invalid in new[] { "1.5", "NaN", "Infinity", "1e100", "2147483648", "-2147483649" })
            {
                grid.Rows[0].Cells[column].Value = invalid;
                Throws<InvalidDataException>(() => Call(view, "ReadRegistry"), column + " rejects " + invalid);
            }
            grid.Rows[0].Cells[column].Value = old;
        }
        foreach (var invalid in new[] { "NaN", "Infinity", "-1", "0" })
        {
            var old = banks.Rows[0].Cells["Nameplate"].Value;
            banks.Rows[0].Cells["Nameplate"].Value = invalid;
            Throws<InvalidDataException>(() => Call(view, "ReadRegistry"), "ATS nameplate rejects " + invalid);
            banks.Rows[0].Cells["Nameplate"].Value = old;
        }
        var rated = grid.Rows[0].Cells["Rated"].Value;
        grid.Rows[0].Cells["Rated"].Value = "800.5";
        Check(((UpsSettings)Call(view, "ReadRegistry")!).Devices[0].RatedKva == 800.5, "continuous nameplate rating can retain a valid fractional value");
        grid.Rows[0].Cells["Rated"].Value = rated;
        var verification = grid.Rows[0].Cells["Verification"].Value;
        grid.Rows[0].Cells["Verification"].Value = "999";
        Throws<InvalidDataException>(() => Call(view, "ReadRegistry"), "undefined verification enum is rejected rather than silently defaulted");
        grid.Rows[0].Cells["Verification"].Value = verification;
        var advanced = Descendants(view).OfType<CheckBox>().Single(control => control.Text == "Advanced columns");
        Check(!grid.Columns["Timeout"]!.Visible, "registry starts with a concise everyday column set");
        advanced.Checked = true;
        Check(grid.Columns["Timeout"]!.Visible && grid.Columns["ProfileTransport"]!.Visible, "advanced transport and notes remain accessible");
        advanced.Checked = false;
        var working = Field<UpsSettings>(view, "_working");
        var bankNumber = working.Banks[0].Number;
        working.Banks[0].Number = 7;
        Call(view, "RefreshComboItems");
        Check(Field<ComboBox>(view, "_assignmentGroup").Items.Contains("ATS 7"), "bulk assignment uses configured bank numbers, not hardcoded ATS 1-4");
        working.Banks[0].Number = bankNumber;
        Call(view, "RefreshComboItems");
        var storedSettings = UpsSettingsStore.Current;
        var fractional = storedSettings.Clone();
        fractional.Devices[0].RatedKva = 800.123456789;
        fractional.Banks[0].NameplateKva = 2400.123456789;
        typeof(UpsSettingsStore).GetField("_current", Static)!.SetValue(null, fractional);
        Call(view, "LoadWorkingCopy");
        var roundTrip = (UpsSettings)Call(view, "ReadRegistry")!;
        Check(roundTrip.Devices.Single(device => device.Id == fractional.Devices[0].Id).RatedKva == fractional.Devices[0].RatedKva &&
            roundTrip.Banks[0].NameplateKva == fractional.Banks[0].NameplateKva, "opening and saving registry does not round fractional nameplate values");
        typeof(UpsSettingsStore).GetField("_current", Static)!.SetValue(null, storedSettings);
        Call(view, "LoadWorkingCopy");
        using var form = new Form { ClientSize = new Size(1160, 680), Location = new Point(-2400, -1800), StartPosition = FormStartPosition.Manual };
        form.Controls.Add(view);
        Field<TabControl>(view, "_tabs").SelectedIndex = 0;
        ((TabControl)grid.Parent!.Parent!).SelectedIndex = 0;
        form.Show();
        Pump();
        grid.CurrentCell = grid.Rows[0].Cells["Interval"];
        var valueType = grid.CurrentCell.ValueType;
        grid.CurrentCell.ValueType = typeof(int);
        Check(grid.BeginEdit(false), "registry grid enters a real edit session");
        ((TextBox)grid.EditingControl!).Text = "not-an-integer";
        Throws<InvalidDataException>(() => Call(view, "ReadRegistry"), "failed grid edit cannot silently save the previous cell value");
        grid.CancelEdit();
        grid.CurrentCell.ValueType = valueType;
        form.Controls.Remove(view);
    }

    private static void RecordingSaves(MeasurementHistorian historian, DailyHistorianExportScheduler scheduler, string temporary)
    {
        var blocker = Path.Combine(temporary, "not-a-directory");
        File.WriteAllText(blocker, "isolated invalid export root");
        var calls = 0;
        RecordingSettings? submitted = null;
        var completion = new TaskCompletionSource();
        var settings = new RecordingSettings { ExportFolderPath = temporary };
        settings.Normalize();
        using var view = new HistorianView(settings, Admin, historian, scheduler, candidate =>
        {
            calls++;
            submitted = candidate;
            return completion.Task;
        });
        Field<TextBox>(view, "_exportFolder").Text = Path.Combine(blocker, "child");
        Field<CheckBox>(view, "_dailyExcel").Checked = false;
        Call(view, "OnSaveClicked", null, EventArgs.Empty);
        Check(calls == 1 && submitted != null && !submitted.AutomaticDailyExcelEnabled,
            "unavailable disabled export folder does not block unrelated recording settings");
        Check(!Directory.Exists(Path.Combine(blocker, "child")), "disabled daily export save does not probe/create its output path");
        var raw = Field<CheckBox>(view, "_continuousRawRecording");
        raw.Checked = !raw.Checked;
        Call(view, "OnSaveClicked", null, EventArgs.Empty);
        Check(calls == 1, "recording settings save is not reentrant");
        completion.SetResult();
        Wait(() => !Field<bool>(view, "_savingRecording"), "recording save completes");
        Check(Field<bool>(view, "_dirty"), "edits during asynchronous save remain unsaved");
        Check(Field<Label>(view, "_message").Text.Contains("New edits"), "save feedback distinguishes submitted changes from later edits");
        Field<CheckBox>(view, "_dailyExcel").Checked = true;
        Call(view, "OnSaveClicked", null, EventArgs.Empty);
        Check(calls == 1 && Field<Label>(view, "_message").Text.Contains("not writable"), "enabled daily export requires a writable output folder");
        Field<TextBox>(view, "_exportFolder").Text = temporary;
        Call(view, "OnSaveClicked", null, EventArgs.Empty);
        Check(calls == 2 && !Field<bool>(view, "_dirty"), "valid subsequent recording save clears only successfully submitted edits");
        Call(view, "SelectAnalyzer", "T1");
        Call(view, "SetAllParameters", false);
        Call(view, "OnSaveClicked", null, EventArgs.Empty);
        Wait(() => !Field<bool>(view, "_savingRecording"), "relocated daily measurement selection saves");
        Check(submitted!.GetAnalyzer("T1").SelectedParameters.Count == 0 &&
            submitted.GetAnalyzer("T2").SelectedParameters.SequenceEqual(settings.GetAnalyzer("T2").SelectedParameters) &&
            submitted.GetAnalyzer("T1").EquipmentName == settings.GetAnalyzer("T1").EquipmentName,
            "daily measurement settings remain editable after page removal without changing other equipment or metadata");

        var acqCompletion = new TaskCompletionSource();
        using var acquisition = new HistorianView(settings, Admin, historian, scheduler, _ => Task.CompletedTask,
            plcSettings: new PlcSettings(), savePlcSettings: (_, _) => acqCompletion.Task);
        Field<ComboBox>(acquisition, "_acquisitionInterval").SelectedIndex = 1;
        Call(acquisition, "OnSaveAcquisitionClicked", null, EventArgs.Empty);
        Check(!Field<ComboBox>(acquisition, "_acquisitionInterval").Enabled, "acquisition interval is locked during asynchronous apply");
        acqCompletion.SetResult();
        Wait(() => !Field<bool>(acquisition, "_savingAcquisition"), "acquisition save completes");
        Check(Field<ComboBox>(acquisition, "_acquisitionInterval").Enabled && !Field<bool>(acquisition, "_acquisitionDirty"),
            "acquisition apply completes and restores editor");

        IntervalAndRetentionPolicy(historian, scheduler, temporary);
    }

    private static void IntervalAndRetentionPolicy(MeasurementHistorian historian, DailyHistorianExportScheduler scheduler, string temporary)
    {
        var expected = new (int Ms, int Days)[] { (100, 8), (250, 15), (500, 23), (1000, 30), (2000, 45), (5000, 60), (10000, 90) };
        foreach (var (ms, days) in expected)
            Check(RecordingSettings.MaximumRetentionDaysFor(ms) == days, $"raw retention tier {ms} ms = {days} days");
        Check(expected.All(item => RecordingSettings.MaximumRetentionDaysFor(item.Ms) <= 90), "planning limits never exceed approved 90 days");
        var legacy = new RecordingSettings { HistorianRecordingIntervalMilliseconds = 60000, HistorianRetentionDays = 90 };
        legacy.Normalize();
        Check(legacy.HistorianRecordingIntervalMilliseconds == 10000 && legacy.HistorianRetentionDays == 90,
            "legacy slower-than-10 s recording moves to 10 s and keeps approved 90-day planning window");
        var minimum = new RecordingSettings { HistorianRecordingIntervalMilliseconds = 250, HistorianRetentionDays = 1 };
        minimum.Normalize();
        Check(minimum.HistorianRetentionDays == 3, "planning window normalizes to at least 3 days");
        Throws<InvalidDataException>(() => new PlcSettings { RequestedAcquisitionIntervalMilliseconds = 10001, PollingIntervalSeconds = 11 }.Validate(),
            "live acquisition slower than 10 s is rejected");
        Throws<InvalidDataException>(() => new PlcSettings { RequestedAcquisitionIntervalMilliseconds = 99 }.Validate(),
            "live acquisition faster than 0.1 s is rejected");
        var legacyPlc = new PlcSettings { RequestedAcquisitionIntervalMilliseconds = 60000, PollingIntervalSeconds = 60 };
        legacyPlc.Normalize();
        Check(legacyPlc.EffectivePollingIntervalMilliseconds == 10000 && legacyPlc.PollingIntervalSeconds == 10,
            "legacy live acquisition slower than 10 s moves to 10 s");

        var settings = new RecordingSettings { ExportFolderPath = temporary, HistorianRecordingIntervalMilliseconds = 1500, HistorianRetentionDays = 40 };
        settings.Normalize();
        RecordingSettings? submitted = null;
        using var view = new HistorianView(settings, Admin, historian, scheduler, candidate => { submitted = candidate; return Task.CompletedTask; },
            plcSettings: new PlcSettings { RequestedAcquisitionIntervalMilliseconds = 2000 });
        var tabs = Field<TabControl>(view, "_tabs");
        var combo = Field<ComboBox>(view, "_intervalCombo");
        var retention = Field<NumericUpDown>(view, "_retentionDays");
        Check(Descendants(tabs.TabPages[1]).Contains(combo) && Descendants(tabs.TabPages[1]).Contains(retention),
            "recording interval and retention are on the Raw Recording tab");
        Check(!Descendants(tabs.TabPages[2]).Contains(combo) && !Descendants(tabs.TabPages[2]).Contains(retention),
            "recording interval and retention are not on the Daily Excel Export tab");
        Check(combo.SelectedIndex == combo.Items.Count - 1 && combo.Text == "1.5 s" && retention.Value == 40 && retention.Maximum == 45,
            "previously saved 1.5 s interval remains selectable in the one interval box");
        Check(!combo.Items.Cast<string>().Any(item => item.Contains("Custom")) &&
            typeof(HistorianView).GetField("_customRecordingSeconds", Instance) == null,
            "recording has one selector and no separate custom editor");
        Check(Field<Label>(view, "_retentionHint").Text.Contains("cannot be closer than 2 s"),
            "hint warns when live acquisition is slower than recording");
        combo.SelectedIndex = 0;
        Check(retention.Minimum == 3 && retention.Maximum == 8 && retention.Value == 8 && retention.Enabled,
            "0.1 s synchronizes editable planning days to to the 8-day maximum");
        var storage = new RawRecordingStorageEstimate(5000, 10000, 3000, 20);
        typeof(HistorianView).GetField("_storageEstimate", Instance)!.SetValue(view, storage);
        Call(view, "RefreshResolutionTierStates");
        var grid = Field<DataGridView>(view, "_resolutionGrid");
        foreach (var (ms, days) in expected)
        {
            combo.SelectedIndex = Array.IndexOf(RecordingSettings.StandardRecordingIntervalsMilliseconds, ms);
            Check(retention.Maximum == days, $"changing interval immediately updates day limit for {ms} ms");
            retention.Value = days;
            Check(grid.Rows.Cast<DataGridViewRow>().Single(row => row.Cells["Selection"].Value!.ToString() == "Selected interval")
                .Cells["Interval"].Value!.ToString() == (ms / 1000d).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " s",
                "table highlights draft interval before saving");
            Check(grid.Rows.Cast<DataGridViewRow>().All(row => row.Cells["WorstCase"].Value!.ToString()!.StartsWith("~")),
                "every interval displays a numeric approximate storage estimate");
        }
        Check(storage.EstimateBytes(1000, 3, 100) == 18000d * 86400 * 3 * 2,
            "fallback storage keeps one row set per recording interval, UPS growth and 2x headroom");
        Check(storage.EstimateBytes(10000, 3, 100) < storage.EstimateBytes(1000, 3, 100),
            "a slower recording interval reduces transformer rows");
        Check(storage.EstimateBytes(1000, 6, 100) == storage.EstimateBytes(1000, 3, 100) * 2,
            "storage scales exactly with selected planning days");
        Check(storage.EstimateBytes(1000, 3, 100) > storage.EstimateBytes(1000, 3, 2000),
            "a slow PLC cycle limits rows even when the recording interval is shorter");
        var observed = new RawRecordingStorageEstimate(5000, 10000, 3000, 6, 100, 50, 25, 1000);
        Check(observed.UsesObservedRates &&
            observed.EstimateBytes(1000, 3, 100) == 175d * 86400 * 3 * 2,
            "observed forecast uses measured bytes per second and keeps 2x headroom");
        Check(observed.EstimateBytes(10000, 3, 100) == 40d * 86400 * 3 * 2,
            "transformer growth scales down with a slower interval while UPS growth stays fixed");
        var cycleLimited = new RawRecordingStorageEstimate(5000, 10000, 3000, 6, 100, 50, 25, 1000, 1270);
        Check(cycleLimited.EstimateBytes(100, 8, 100) == cycleLimited.EstimateBytes(1000, 8, 100),
            "recording faster than the measured PLC cycle does not inflate the estimate");
        Check(cycleLimited.EstimateBytes(5000, 3, 100) < cycleLimited.EstimateBytes(2000, 3, 100) &&
            cycleLimited.EstimateBytes(2000, 3, 100) < cycleLimited.EstimateBytes(1000, 3, 100),
            "slower intervals reduce the estimate once above the measured PLC cycle");
        Check(observed.EstimateBytes(1000, 3, 100) > observed.EstimateBytes(2000, 3, 100),
            "estimate decreases as the recording interval increases");
        Throws<ArgumentOutOfRangeException>(() => storage.EstimateBytes(100, 9, 100), "storage rejects days beyond interval limit");
        Throws<ArgumentOutOfRangeException>(() => storage.EstimateBytes(1000, 0, 100), "storage rejects zero-day plans");
        Throws<ArgumentOutOfRangeException>(() => new RawRecordingStorageEstimate(double.NaN, 1, 0, 5), "storage rejects invalid row-size samples");
        retention.Value = 45;
        var beforeSize = grid.Rows[6].Cells["SelectedSize"].Value!.ToString();
        retention.Value = 90;
        Check(grid.Rows[6].Cells["SelectedSize"].Value!.ToString() != beforeSize, "days edits immediately recalculate table sizes");
        combo.SelectedIndex = 1;
        Check(retention.Value == 15 && retention.Maximum == 15, "switching from 90-day plan to 250 ms clamps days to 15");
        combo.SelectedIndex = 6;
        Check(retention.Maximum == 90, "10 s preset allows up to 90 planning days");
        retention.Value = 90;
        Call(view, "OnSaveClicked", null, EventArgs.Empty);
        Wait(() => !Field<bool>(view, "_savingRecording"), "interval policy save completes");
        Check(submitted != null && submitted.HistorianRecordingIntervalMilliseconds == 10000 && submitted.HistorianRetentionDays == 90,
            "raw tab saves 10 s / 90 planning days");
    }

    private static void ReportFailures(HistorianView view, MeasurementHistorian historian,
        DailyHistorianExportScheduler scheduler, RecordingSettings settings, string temporary)
    {
        var end = DateTime.UtcNow;
        var start = end.AddHours(-1);
        Throws<IOException>(() => historian.GetRangeSummary("T1", start, end), "database failure is not an empty range summary");
        Throws<IOException>(() => historian.GetEarliestSampleUtc(), "database failure is not a missing earliest sample");
        Throws<IOException>(() => historian.GetLatestSampleUtc(), "database failure is not a missing latest sample");
        Throws<IOException>(() => historian.CountRawMeasurements("T1", new[] { "PTOT_KW" }, start, end), "database failure is not a zero measurement count");
        Throws<IOException>(() => HistorianReportExporter.Preview(historian, settings, "T1", start, end), "preview propagates unavailable storage instead of no samples");
        Throws<IOException>(() => Await(scheduler.RunDueExportsAsync()), "daily export aborts rather than permanently skips a day on database failure");
        Check(scheduler.GetRecentJobs().Count == 0, "database outage creates no skipped-empty or successful export jobs");
        foreach (var excel in new[] { true, false })
        {
            var path = Path.Combine(temporary, excel ? "test.xlsx" : "test.pdf");
            Throws<IOException>(() => Await(excel
                ? HistorianReportExporter.ExportExcelAsync(historian, settings, "T1", start, end, temporary, Path.GetFileName(path), default)
                : HistorianReportExporter.ExportPdfAsync(historian, settings, "T1", start, end, temporary, Path.GetFileName(path), default)),
                "offline " + (excel ? "Excel" : "PDF") + " export fails explicitly");
            Check(!File.Exists(path) && !Directory.EnumerateFiles(temporary, "*.partial").Any(), "failed export leaves no partial or success-shaped output");
        }
    }

    private static void Permissions(AppSettings settings, MeasurementHistorian historian,
        DailyHistorianExportScheduler scheduler, RecordingSettings recording)
    {
        foreach (var session in new[]
        {
            new UserSession { RoleName = "Viewer", UserId = "viewer", Username = "viewer" },
            new UserSession { RoleName = "Administrator", IsOfflinePreview = true }
        })
        {
            var calls = 0;
            using var snmp = new UpsSnmpSettingsView(session);
            Check(Field<DataGridView>(snmp, "_registry").ReadOnly, "viewer/preview registry is read only");
            Call(snmp, "OnSaveRegistry", null, EventArgs.Empty);
            Check(Field<Label>(snmp, "_registryMessage").Text.Contains("Read only"), "viewer/preview save explicitly refuses changes");
            using var reports = new HistorianView(recording, session, historian, scheduler, _ => { calls++; return Task.CompletedTask; });
            Call(reports, "OnSaveClicked", null, EventArgs.Empty);
            Check(calls == 0 && !Field<CheckBox>(reports, "_dailyExcel").Enabled, "viewer/preview cannot save recording policy");
            Check(Field<Control>(Field<EquipmentHistoryView>(reports, "_equipmentHistory"), "_load").Enabled,
                "viewer/preview retains equipment history access");
        }
    }

    private static IEnumerable<Control> Ancestors(Control control)
    {
        while (control.Parent != null) { control = control.Parent; yield return control; }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void CheckVisible(Control control, Control view, string message)
    {
        Check(control.Visible && control.Width > 0 && control.Height > 0, message + " is visible");
        var bounds = control.RectangleToScreen(control.ClientRectangle);
        foreach (var parent in Ancestors(control))
        {
            Check(parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds), message + " is not clipped by " + parent.GetType().Name);
            if (parent == view) break;
        }
    }

    private static object? Call(object instance, string method, params object?[] arguments)
    {
        try { return instance.GetType().GetMethod(method, Instance)!.Invoke(instance, arguments); }
        catch (TargetInvocationException error) when (error.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { Check(true, message); return; }
        throw new InvalidOperationException("Settings UI: expected " + typeof(T).Name + ": " + message);
    }

    private static void Await(Task task)
    {
        Wait(() => task.IsCompleted, "asynchronous operation completes");
        task.GetAwaiter().GetResult();
    }

    private static void Wait(Func<bool> ready, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!ready() && watch.Elapsed < TimeSpan.FromSeconds(15)) { Application.DoEvents(); Thread.Sleep(10); }
        Check(ready(), message);
    }

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, Instance)!.GetValue(instance)!;

    private static void Pump()
    {
        var until = DateTime.UtcNow.AddMilliseconds(200);
        do { Application.DoEvents(); Thread.Sleep(10); } while (DateTime.UtcNow < until);
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException("Settings UI: " + message);
        assertions++;
    }
}
