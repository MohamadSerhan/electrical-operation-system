using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Forms;
using Lextm.SharpSnmpLib;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--factory-completion-unit")
            {
                FactoryCompletionValidation.Run(args[1]);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--factory-fire-events-unit")
            {
                FactoryFireEventValidation.Run(args[1]);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--factory-process-reference-unit")
            {
                FactoryMapRefinementsValidation.RunProcessReferences();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--factory-map-top-saved-unit")
            {
                FactoryMapMotionValidation.RunSavedWorkload(top: true);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--factory-map-frame-unit")
            {
                FactoryMapMotionValidation.ValidateFrameTimes();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--factory-map-motion-unit")
            {
                FactoryMapMotionValidation.Run();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--factory-map-appearance-unit")
            {
                FactoryMapRefinementsValidation.RunAppearance();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--factory-map-pointer-unit")
            {
                FactoryMapMotionValidation.RunPointerWorkload();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--factory-map-saved-unit")
            {
                FactoryMapMotionValidation.RunSavedWorkload();
                return 0;
            }
            if (args.Length == 2 && args[0] == "--factory-map-release-unit")
            {
                FactoryDrawingValidation.Run();
                FactoryMapValidation.Run();
                SettingsUiValidation.RunShellRefresh();
                FactoryMonitoringValidation.Run(args[1]);
                FactoryFireEventValidation.Run(args[1]);
                return 0;
            }
            if (args.Length == 3 && args[0] == "--factory-drawing-unit")
            {
                FactoryDrawingValidation.RunPrepared(args[1], args[2]);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--factory-map-unit")
            {
                FactoryDrawingValidation.Run();
                FactoryMapValidation.Run();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--factory-map-editor-unit")
            {
                FactoryMapRefinementsValidation.RunEditors();
                FactoryBuildingValidation.Run();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--factory-map-opening-unit")
            {
                FactoryMapRefinementsValidation.RunOpening();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--shell-refresh-unit")
            {
                SettingsUiValidation.RunShellRefresh();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--factory-map-interaction-unit")
            {
                SettingsUiValidation.RunMapInteraction();
                return 0;
            }
            if (args.Length == 2 && args[0] == "--factory-monitoring-unit")
            {
                FactoryMonitoringValidation.Run(args[1]);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--factory-map-render")
            {
                FactoryMapValidation.RenderViews(args[1]);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--ups-alarm-history-unit")
            {
                UpsAlarmHistoryValidation.Run(args[1]);
                return 0;
            }
            if (args.Length == 4 && args[0] == "--ups-cadence-minimal")
            {
                Validation.SeedByReflection(typeof(UpsSnapshot).Assembly, args[1]);
                UpsReconnectValidation.RunCadenceAudit(args[3], args[2], true);
                return 0;
            }
            if (args.Length == 3 && args[0] == "--ups-cadence-live")
            {
                Validation.SeedByReflection(typeof(UpsSnapshot).Assembly, args[1]);
                UpsReconnectValidation.RunCadenceAudit(args[2]);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--ups-estimate-unit")
            {
                UpsInputEstimateValidation.Run(args[1]);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--ups-channel-unit")
            {
                UpsChannelValidation.Run(args[1]);
                return 0;
            }
            if (args.Length == 3 && args[0] == "--ups-channel-live")
            {
                Validation.SeedByReflection(typeof(UpsSnapshot).Assembly, args[1]);
                UpsChannelValidation.RunLive(args[2]);
                return 0;
            }
            if (args.Length == 4 && args[0] == "--ups-power-balance-csv")
            {
                UpsPowerBalanceValidation.ReviewCsv(args[1], args[2], args[3]);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--analyzer-graph-unit")
            {
                AnalyzerGraphValidation.Run(args[1]);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--electrical-knowledge-unit")
            {
                ElectricalKnowledgeValidation.Run(args[1]);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--history-stress")
            {
                HistoryStressValidation.Run(args[1]);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--analyzer-recording-stress")
            {
                AnalyzerRecordingValidation.Run(args[1]);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--history-xy-unit")
            {
                HistoryComparisonValidation.Run(args[1]);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--history-trace-unit")
            {
                HistoryTraceValidation.Run();
                return 0;
            }
            if (args.Length == 2 && args[0] == "--analyzer-trace-unit")
            {
                AnalyzerTraceValidation.Run(args[1]);
                return 0;
            }
            if (args.Length == 2 && args[0] == "--report-review-unit")
            {
                ReportReviewValidation.Run(args[1]);
                return 0;
            }
            if (args.Length == 7 && args[0] == "--live-transformer-voltage-report")
            {
                LiveRecordingAudit.RunVoltageReport(args[1],
                    DateTime.Parse(args[2], null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
                    DateTime.Parse(args[3], null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
                    int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture), args[5], args[6]);
                return 0;
            }
            if (args.Length == 6 && args[0] == "--live-history-comparison-report")
            {
                LiveRecordingAudit.RunComparisonReport(args[1],
                    DateTime.Parse(args[2], null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
                    DateTime.Parse(args[3], null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
                    args[4], args[5]);
                return 0;
            }
            if (args.Length == 3 && args[0] == "--live-recording-audit")
            {
                LiveRecordingAudit.Run(args[1], args[2]);
                return 0;
            }
            if (args.Length == 3 && args[0] == "--live-ups-fleet-audit")
            {
                LiveRecordingAudit.RunFleet(args[1], args[2]);
                return 0;
            }
            if (args.Length == 3 && args[0] == "--published-recording")
            {
                var path = Path.GetFullPath(args[1]);
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                RunPublishedRecording(path, args[2]);
                return 0;
            }
            if (args.Length == 4 && args[0] == "--before")
            {
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
                Validation.SeedByReflection(assembly, args[2]);
                Validation.Screenshots(assembly, args[3], "before");
                return 0;
            }
            if (args.Length == 4 && args[0] == "--baseline-poll")
            {
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
                Validation.SeedByReflection(assembly, args[2]);
                Validation.Baseline(assembly, args[3]);
                return 0;
            }
            if (args.Length == 4 && args[0] == "--published-check")
            {
                var path = Path.GetFullPath(args[1]);
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                Validation.SeedByReflection(assembly, args[2]);
                Validation.Live(args[3]);
                Validation.Baseline(assembly, args[3], "published-known-good-polls.json");
                Validation.Run(new[] { "--profile-ui-live", args[2], Path.Combine(args[3], "CredentialsActions") });
                File.WriteAllText(Path.Combine(args[3], "validated-binary.json"), JsonSerializer.Serialize(new
                {
                    Path = path, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                    ValidatedUtc = DateTime.UtcNow, Method = "Actual WinForms Test All for all 15 registered IPs, real measurement polls for responders, and C01/C03/C04 regression using the staged Windows x64 assembly."
                }, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (args.Length == 4 && args[0] == "--published-alarms")
            {
                var path = Path.GetFullPath(args[1]);
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                Validation.SeedByReflection(assembly, args[2]);
                Validation.AlarmLive(args[2], args[3]);
                if (!string.Equals(path, Validation.ApplicationLocation, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The selected staged binary was not the tested assembly.");
                File.WriteAllText(Path.Combine(args[3], "tested-binary.json"), JsonSerializer.Serialize(new
                {
                    Path = path, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                    ValidatedUtc = DateTime.UtcNow, ActualAssembly = Validation.ApplicationLocation
                }, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (args.Length == 4 && args[0] == "--published-parameters")
            {
                var path = Path.GetFullPath(args[1]);
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                Validation.SeedByReflection(assembly, args[2]);
                Validation.ParameterAudit(args[2], args[3]);
                Validation.Screenshots(assembly, Path.Combine(args[3], "Screenshots"), "after");
                if (!string.Equals(path, Validation.ApplicationLocation, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The selected staged binary was not the tested assembly.");
                File.WriteAllText(Path.Combine(args[3], "tested-binary.json"), JsonSerializer.Serialize(new
                {
                    Path = path, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                    ValidatedUtc = DateTime.UtcNow, ActualAssembly = Validation.ApplicationLocation
                }, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (args.Length == 4 && args[0] == "--published-measurements")
            {
                var path = Path.GetFullPath(args[1]);
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                Validation.SeedByReflection(assembly, args[2]);
                Validation.Live(args[3]);
                Validation.MeasurementAcceptance(args[3]);
                if (!string.Equals(path, Validation.ApplicationLocation, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The selected staged binary was not the tested assembly.");
                File.WriteAllText(Path.Combine(args[3], "tested-binary.json"), JsonSerializer.Serialize(new
                {
                    Path = path, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                    ValidatedUtc = DateTime.UtcNow, ActualAssembly = Validation.ApplicationLocation
                }, new JsonSerializerOptions { WriteIndented = true }));
                return 0;
            }
            if (args.Length == 4 && args[0] == "--measurement-ui")
            {
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
                Validation.SeedByReflection(assembly, args[2]);
                Validation.MeasurementScreenshots(assembly, args[3]);
                return 0;
            }
            if (args.Length == 4 && args[0] == "--published-settings")
            {
                var path = Path.GetFullPath(args[1]);
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                Validation.SeedByReflection(assembly, args[2]);
                Validation.Screenshots(assembly, args[3], "after");
                if (!string.Equals(path, Validation.ApplicationLocation, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The selected staged binary was not the tested assembly.");
                Console.WriteLine("Verified staged assembly: " + path);
                return 0;
            }
            if (args.Length == 4 && args[0] == "--published-c04")
            {
                var path = Path.GetFullPath(args[1]);
                var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                var exit = Validation.Run(new[] { "--c04-verify", args[2], args[3] });
                if (!string.Equals(assembly.Location, Validation.ApplicationLocation, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The selected staged assembly was not the tested assembly.");
                File.WriteAllText(Path.Combine(args[3], "tested-binary.json"), JsonSerializer.Serialize(new
                {
                    Path = path, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                    ValidatedUtc = DateTime.UtcNow, Method = "Read-only C01/C03/C04 recovery service; actual result is recorded independently. This file is not a claim that C04 recovered."
                }, new JsonSerializerOptions { WriteIndented = true }));
                return exit;
            }
            return Validation.Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex is TargetInvocationException ? ex.InnerException : ex);
            return 1;
        }

    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void RunPublishedRecording(string path, string evidence)
    {
        var actual = typeof(MeasurementHistorian).Assembly.Location;
        if (!string.Equals(path, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected staged recording assembly was not the tested assembly: " + actual);
        EquipmentHistoryValidation.Run(evidence);
        Console.WriteLine("Validated staged recording assembly: " + actual);
    }
}

internal static class Validation
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string title);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static int assertions;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static Assembly App => typeof(UpsSettings).Assembly;
    internal static string ApplicationLocation => App.Location;
    private static UserSession Admin => new() { Username = "isolated-qa", UserId = "isolated-qa", RoleName = "Administrator" };

    internal static void SeedByReflection(Assembly assembly, string source)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "EOEMSData", "Configuration");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "snmp-credentials.json", "ups-settings.json" })
            File.Copy(Path.Combine(source, name), Path.Combine(folder, name), true);
        var sourceWeb = Path.Combine(source, "web-card-credentials.json");
        var isolatedWeb = Path.Combine(folder, "web-card-credentials.json");
        if (File.Exists(sourceWeb)) File.Copy(sourceWeb, isolatedWeb, true);
        else if (File.Exists(isolatedWeb)) File.Delete(isolatedWeb);
        var settings = assembly.GetTypes().Single(t => t.Name == "UpsSettings");
        var store = assembly.GetTypes().Single(t => t.Name == "UpsSettingsStore");
        store.GetField("_current", Static)!.SetValue(null, JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(folder, "ups-settings.json")), settings));
        var pg = assembly.GetTypes().Single(t => t.Name == "PostgresDatabase");
        pg.GetField("_connectionString", Static)!.SetValue(null, "Host=127.0.0.1;Port=1;Username=isolated_qa;Database=isolated_qa;Timeout=1;Pooling=false");
        pg.GetField("_lastFailureUtc", Static)!.SetValue(null, DateTime.UtcNow);
    }

    internal static void Screenshots(Assembly assembly, string output, string prefix)
    {
        Directory.CreateDirectory(output);
        Application.EnableVisualStyles();
        var sessionType = assembly.GetTypes().Single(t => t.Name == "UserSession");
        var session = Activator.CreateInstance(sessionType)!;
        foreach (var item in new[] { ("Username", "isolated-qa"), ("UserId", "isolated-qa"), ("RoleName", "Administrator") })
            sessionType.GetProperty(item.Item1)!.SetValue(session, item.Item2);
        var viewType = assembly.GetTypes().Single(t => t.Name == "UpsSnmpSettingsView");
        using var form = new Form { Size = new Size(1600, 1000), Text = "EOEMS isolated UI QA", StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) };
        var view = (Control)Activator.CreateInstance(viewType, session)!;
        view.Dock = DockStyle.Fill;
        form.Controls.Add(view);
        form.Show();
        var tabs = (TabControl)viewType.GetField("_tabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        foreach (var index in new[] { 0, 1, 2, 3 })
        {
            tabs.SelectedIndex = index;
            if (index == 3)
                viewType.GetMethod("RefreshDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
            form.PerformLayout();
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(output, $"{prefix}-{index}-{tabs.TabPages[index].Text.Replace(" & ", "-").Replace(" ", "-")}.png"));
        }
        if (prefix == "after")
        {
            tabs.SelectedIndex = 1;
            var cardTabs = viewType.GetField("_cardTabs", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(view) as TabControl;
            if (cardTabs is not null)
            {
                for (var index = 0; index < cardTabs.TabCount; index++)
                {
                    cardTabs.SelectedIndex = index;
                    Application.DoEvents();
                    using var image = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                    image.Save(Path.Combine(output, $"after-card-{index}-{cardTabs.TabPages[index].Text.Replace(" ", "-")}.png"));
                }
                cardTabs.SelectedIndex = 2;
            }
            var selector = viewType.GetField("_credentialDevice", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(view) as ComboBox;
            if (selector is not null)
            {
                selector.SelectedItem = selector.Items.Cast<object>().Single(item =>
                    (string)item.GetType().GetProperty("Id")!.GetValue(item)! == "UPS-C04");
                Application.DoEvents();
                using var c04Bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(c04Bitmap, new Rectangle(Point.Empty, form.Size));
                c04Bitmap.Save(Path.Combine(output, "after-C04-community.png"));
            }
            var version = (ComboBox)viewType.GetField("_profileVersion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            var mode = (ComboBox)viewType.GetField("_securityMode", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            version.SelectedItem = "v3";
            foreach (var value in new[] { "NoAuthNoPriv", "AuthNoPriv", "AuthPriv" })
            {
                mode.SelectedItem = value;
                Application.DoEvents();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(output, $"after-v3-{value}-editor.png"));
            }
            form.Size = new Size(1100, 768);
            version.SelectedItem = "v2c";
            Application.DoEvents();
            using var compactBitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(compactBitmap, new Rectangle(Point.Empty, form.Size));
            compactBitmap.Save(Path.Combine(output, "after-compact-1100x768.png"));
            if (cardTabs is not null)
            {
                foreach (var size in new[] { new Size(1100, 768), new Size(900, 700) })
                {
                    form.Size = size;
                    for (var index = 0; index < cardTabs.TabCount; index++)
                    {
                        cardTabs.SelectedIndex = index;
                        Application.DoEvents();
                        using var image = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                        image.Save(Path.Combine(output, $"after-card-{index}-{size.Width}x{size.Height}.png"));
                    }
                }
            }
        }

        form.Close();
        Console.WriteLine($"{prefix}: captured four actual WinForms views (isolated QA session; not an authenticated production session).");
    }

    internal static void Baseline(Assembly assembly, string output, string resultName = "before-known-good-polls.json")
        {
            var store = assembly.GetTypes().Single(t => t.Name == "UpsSettingsStore");
            var settings = store.GetProperty("Current", Static)!.GetValue(null)!;
            var devices = (IEnumerable)settings.GetType().GetProperty("Devices")!.GetValue(settings)!;
            var poll = assembly.GetTypes().Single(t => t.Name == "UpsAcquisitionService").GetMethod("PollDevice", Static)!;
            var results = new List<object>();
            foreach (var device in devices)
            {
                var id = (string)device.GetType().GetProperty("Id")!.GetValue(device)!;
                if (!new[] { "UPS-C01", "UPS-C03", "UPS-C04" }.Contains(id)) continue;
                var snapshot = poll.Invoke(null, new object?[] { device, 0, null })!;
                results.Add(snapshot);
                Console.WriteLine($"BASELINE {id}: {snapshot.GetType().GetProperty("Quality")!.GetValue(snapshot)}");
            }
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, resultName), JsonSerializer.Serialize(results, JsonOptions));
        }
    internal static int Run(string[] args)
    {
        if (args.Length == 4 && (args[0] == "--web-open-ui-live" || args[0] == "--web-decline-ui-live"))
        {
            var allowHttp = args[0] == "--web-open-ui-live";
            SeedByReflection(App, args[1]);
            Application.EnableVisualStyles();
            using var form = new Form { Size = new Size(1400, 900), Location = new Point(-2000, -2000), StartPosition = FormStartPosition.Manual };
            using var view = new UpsSnmpSettingsView(Admin) { Dock = DockStyle.Fill };
            form.Controls.Add(view);
            form.Show();
            T Field<T>(string name) => (T)typeof(UpsSnmpSettingsView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            Field<TabControl>("_tabs").SelectedIndex = 1;
            Field<ComboBox>("_credentialDevice").SelectedItem = UpsSettingsStore.Current.Devices.Single(d => d.Id == args[2]);
            Field<TextBox>("_webUrl").Text = "https://" + UpsSettingsStore.Current.Devices.Single(d => d.Id == args[2]).IpAddress + "/";
            var approved = false;
            using var approval = new System.Windows.Forms.Timer { Interval = 50 };
            approval.Tick += (_, _) =>
            {
                var popup = FindWindow("#32770", "Open local card over HTTP");
                GetWindowThreadProcessId(popup, out var processId);
                if (popup == IntPtr.Zero || processId != (uint)Environment.ProcessId) return;
                approved = true;
                approval.Stop();
                SendMessage(popup, 0x0111, new IntPtr(allowHttp ? 6 : 7), IntPtr.Zero);
            };
            approval.Start();
            var watch = Stopwatch.StartNew();
            typeof(UpsSnmpSettingsView).GetMethod("OnOpenWebCard", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view, new object?[] { null, EventArgs.Empty });
            while (Field<CancellationTokenSource?>("_webCancellation") is not null)
            {
                Application.DoEvents();
                Thread.Sleep(5);
                if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Web opening did not finish.");
            }
            if (!allowHttp)
            {
                Check(approved && Field<TextBox>("_webUrl").Text.StartsWith("https://") &&
                    Field<Label>("_webMessage").Text == "HTTP is available. Opening cancelled; saved address unchanged.",
                    args[2] + ": declining HTTP keeps HTTPS and does not launch a browser");
                return 0;
            }
            Check(approved && Field<TextBox>("_webUrl").Text.StartsWith("http://") &&
                Field<ComboBox>("_webProtocol").SelectedItem?.ToString() == "HTTP" &&
                Field<Label>("_webMessage").Text.StartsWith("Web server reached. Browser opened at http://"),
                args[2] + ": actual Open Web Card action checks HTTPS, confirms HTTP and launches the responding local URL");
            typeof(UpsSnmpSettingsView).GetMethod("OnSaveWebCredentials", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(view, new object?[] { null, EventArgs.Empty });
            Check(WebCardCredentialStore.Load(args[2], Admin).ManagementUrl == Field<TextBox>("_webUrl").Text &&
                WebCardCredentialStore.Load(args[2], Admin).HasPassword,
                args[2] + ": saving the corrected HTTP address preserves the existing encrypted password in the isolated store");
            Directory.CreateDirectory(args[3]);
            File.WriteAllText(Path.Combine(args[3], args[2] + "-web-button.json"), JsonSerializer.Serialize(new
            {
                DeviceId = args[2], ApprovedHttpFallback = approved, OpenedUrl = Field<TextBox>("_webUrl").Text,
                Status = Field<Label>("_webMessage").Text, ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
                CorrectedUrlSavedInIsolatedStore = true, ProductionStoreChanged = false
            }, JsonOptions));
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(args[3], args[2] + "-web-button.png"));
            return 0;
        }
        if (args.Length == 4 && args[0] == "--web-endpoint-evidence")
        {
            SeedByReflection(App, args[1]);
            var device = UpsSettingsStore.Current.Devices.Single(d => d.Id == args[2]);
            var https = WebCardConnection.CheckAsync(device.Id, $"https://{device.IpAddress}/", CancellationToken.None).GetAwaiter().GetResult();
            var http = WebCardConnection.CheckAsync(device.Id, $"http://{device.IpAddress}/", CancellationToken.None).GetAwaiter().GetResult();
            var resolved = WebCardConnection.CheckForOpeningAsync(device.Id, $"https://{device.IpAddress}/", CancellationToken.None).GetAwaiter().GetResult();
            Directory.CreateDirectory(args[3]);
            File.WriteAllText(Path.Combine(args[3], device.Id + "-web-endpoints.json"), JsonSerializer.Serialize(new { Https = https, Http = http, Resolved = resolved }, JsonOptions));
            Check(!https.Responds && http.Responds, device.Id + ": HTTP responds while saved/default HTTPS does not");
            Check(resolved.Responds && resolved.Address.Scheme == "http" && !resolved.RequiresBrowserReview,
                device.Id + ": opening resolves HTTPS failures including TLS errors to the responding local HTTP endpoint");
            return 0;
        }
        if (args.Length == 4 && (args[0] == "--capability-evidence" || args[0] == "--connection-evidence"))
        {
            SeedByReflection(App, args[1]);
            var device = UpsSettingsStore.Current.Devices.Single(d => d.Id == args[2]);
            var connection = args[0] == "--connection-evidence";
            var evidence = connection
                ? Call("UpsConnectionDiagnostics", "TestDevice", device, CancellationToken.None)
                : Call("UpsCapabilityProbe", "Run", device, CancellationToken.None);
            if (evidence is UpsConnectionTest test && !test.SnmpResponds &&
                (test.ProfileVersion == "v1" || test.ProfileVersion == "v2c"))
            {
                var credential = Resolve(device.CredentialProfile);
                var community = (string)credential.GetType().GetProperty("Community")!.GetValue(credential)!;
                test.AlternateVersion = test.ProfileVersion == "v1" ? "v2c" : "v1";
                try
                {
                    var response = Lextm.SharpSnmpLib.Messaging.Messenger.Get(
                        test.AlternateVersion == "v1" ? VersionCode.V1 : VersionCode.V2,
                        new System.Net.IPEndPoint(System.Net.IPAddress.Parse(test.IpAddress), test.Port),
                        new OctetString(community),
                        new List<Variable> { new(new ObjectIdentifier("1.3.6.1.2.1.1.2.0")) },
                        test.TimeoutMilliseconds);
                    test.AlternateVersionResult = $"answered ({response.Count} object(s)); configured profile unchanged";
                }
                catch (Lextm.SharpSnmpLib.Messaging.TimeoutException)
                {
                    test.AlternateVersionResult = "timeout with the same saved credential";
                }
                test.Summary += $" Alternate SNMP {test.AlternateVersion}: {test.AlternateVersionResult}.";
            }
            Directory.CreateDirectory(args[3]);
            File.WriteAllText(Path.Combine(args[3], device.Id + (connection ? "-connection.json" : "-capability.json")), JsonSerializer.Serialize(evidence, JsonOptions));
            Console.WriteLine(device.Id + ": " + evidence.GetType().GetProperty(connection ? "Summary" : "Aborted")!.GetValue(evidence));
            return 0;
        }
        if (args.Length == 4 && args[0] == "--historical-target")
        {
            SeedByReflection(App, args[1]);
            var old = JsonSerializer.Deserialize<UpsSettings>(File.ReadAllText(args[2]))!;
            var device = old.Devices.Single(d => d.Id == "UPS-D01" && d.IpAddress == "192.168.107.6");
            var result = Call("UpsConnectionDiagnostics", "TestDevice", device, CancellationToken.None);
            Directory.CreateDirectory(args[3]);
            File.WriteAllText(Path.Combine(args[3], "historical-D01-provisional-test.json"), JsonSerializer.Serialize(new
            {
                Origin = "Historical registered target; absent from active 14-entry registry before this task. Tested only, never re-registered or relabeled.",
                Device = device, Result = result, ActiveRegistryCountUnchanged = UpsSettingsStore.Current.Devices.Count
            }, JsonOptions));
            Console.WriteLine("Historical provisional .6 target tested with its assigned profile; active registry unchanged.");
            return 0;
        }
        if (args.Length == 2 && args[0] == "--measurement-unit")
        {
            Unit(args[1]);
            Console.WriteLine($"Measurement assertions passed: {assertions}.");
            return 0;
        }
        if (args.Length == 3 && args[0] == "--live")
        {
            SeedByReflection(App, args[1]);
            Live(args[2]);
            return 0;
        }
        if (args.Length == 3 && args[0] == "--after")
        {
            SeedByReflection(App, args[1]);
            Screenshots(App, args[2], "after");
            return 0;
        }
        if (args.Length == 3 && (args[0] == "--card-audit" || args[0] == "--card-redact"))
        {
            SeedByReflection(App, args[1]);
            Directory.CreateDirectory(args[2]);
            var profiles = SnmpCredentialStore.ListProfiles();
            var secrets = profiles.Select(p => Resolve(p.Name)).SelectMany(profile =>
                new[] { "Community", "AuthSecret", "PrivSecret" }.Select(name => (string)profile.GetType().GetProperty(name)!.GetValue(profile)!))
                .Where(secret => secret.Length > 0).ToArray();
            var files = Directory.EnumerateFiles(args[2], "*", SearchOption.AllDirectories)
                .Where(path => new[] { ".json", ".jsonl", ".csv", ".md", ".txt" }.Contains(Path.GetExtension(path))).ToArray();
            bool ContainsStoredSecret(string path)
            {
                var text = File.ReadAllText(path);
                return secrets.Any(secret => text.Contains(secret, StringComparison.Ordinal) ||
                    text.Contains(JsonSerializer.Serialize(secret)[1..^1], StringComparison.Ordinal));
            }
            if (args[0] == "--card-redact")
            {
                var redact = files.Where(ContainsStoredSecret).ToArray();
                if (redact.Any(path => Path.GetExtension(path) != ".md"))
                    throw new InvalidOperationException("Automatic redaction is limited to Markdown reports; structured evidence must not be changed.");
                foreach (var path in redact)
                {
                    var text = File.ReadAllText(path);
                    foreach (var secret in secrets.OrderByDescending(s => s.Length))
                    {
                        text = text.Replace(JsonSerializer.Serialize(secret)[1..^1], "[REDACTED]", StringComparison.Ordinal);
                        text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
                    }
                    File.WriteAllText(path, text);
                }
                File.WriteAllText(Path.Combine(args[2], "internal-report-redaction.json"), JsonSerializer.Serialize(new
                {
                    TimestampUtc = DateTime.UtcNow, Files = redact.Select(path => Path.GetRelativePath(args[2], path)),
                    StructuredMeasurementEvidenceChanged = false, StoredSecretsDisplayed = false,
                    Note = "Exact stored-secret matches were masked internally in generated Markdown; matching text was never returned."
                }, JsonOptions));
            }
            var requiringRedaction = files.Where(ContainsStoredSecret).Select(path => Path.GetRelativePath(args[2], path)).ToArray();
            var secretAbsent = requiringRedaction.Length == 0;
            File.WriteAllText(Path.Combine(args[2], "redacted-credential-audit.json"), JsonSerializer.Serialize(new
            {
                TimestampUtc = DateTime.UtcNow, ProfileCount = profiles.Count, SecretsDecryptSuccessfully = true,
                SecretAbsentFromTextReportsAndExports = secretAbsent, FilesChecked = files.Length, FilesRequiringInternalRedaction = requiringRedaction,
                Devices = UpsSettingsStore.Current.Devices.Select(device => new
                {
                    device.Id, device.Label, device.IpAddress, device.AtsBank, device.CredentialProfile,
                    Version = profiles.SingleOrDefault(p => p.Name == device.CredentialProfile)?.Version,
                    WebCredentialsConfigured = WebCardCredentialStore.Load(device.Id, Admin).HasPassword,
                    device.Verification
                }),
                Notes = "Only metadata and boolean results are recorded. No plaintext secrets or plaintext-derived hashes are included. Web credentials are not used in SNMP. Stored web password verification is manual in the browser."
            }, JsonOptions));
            if (!secretAbsent)
            {
                Console.WriteLine("Internal redaction required (file names only; no matching text): " + string.Join("; ", requiringRedaction));
                throw new InvalidOperationException("Secret redaction check failed; do not disclose the output.");
            }
            Console.WriteLine($"Redacted audit: {UpsSettingsStore.Current.Devices.Count} registrations, {profiles.Count} saved SNMP profiles, DPAPI usable; no stored SNMP secret found in {files.Length} text outputs.");
            return 0;
        }
        if (args.Length == 3 && args[0] == "--profile-ui-live")
        {
            SeedByReflection(App, args[1]);
            ProfileUiLive(args[2]);
            return 0;
        }
        if (args.Length == 1 && args[0] == "--diagnostics-unit")
        {
            DiagnosticsUnit();
            Console.WriteLine($"Diagnostics assertions passed: {assertions}.");
            return 0;
        }
        if ((args.Length == 1 || args.Length == 2) && args[0] == "--power-unit")
        {
            PowerEventValidation.Run(args.Length == 2 ? args[1] : null);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--config-open-time")
        {
            ConfigOpenTiming.Run(args[1]);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--fire-connection")
        {
            FireConnectionValidation.Run(args[1]);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--zeta-cloud-unit")
        {
            ZetaCloudValidation.Run(args[1]);
            return 0;
        }
        if (args.Length == 1 && args[0] == "--ups-reconnect-unit")
        {
            UpsReconnectValidation.Run();
            return 0;
        }
        if (args.Length == 1 && args[0] == "--ups-analyzer-unit")
        {
            UpsReconnectValidation.Run(analyzerOnly: true);
            return 0;
        }
        if (args.Length == 3 && args[0] == "--ups-analyzer-live")
        {
            UpsReconnectValidation.RunLiveAnalyzer(args[1], args[2]);
            return 0;
        }
        if (args.Length == 3 && args[0] == "--ups-analyzer-live-stress")
        {
            UpsReconnectValidation.RunLiveAnalyzer(args[1], args[2], 100);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--settings-ui")
        {
            SettingsUiValidation.Run(args[1]);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--settings-save-ui")
        {
            SettingsUiValidation.RunSaveStability(args[1]);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--equipment-history")
        {
            Console.WriteLine("Starting isolated equipment history validation.");
            EquipmentHistoryValidation.Run(args[1]);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--storage-integration")
        {
            EquipmentHistoryValidation.Run(args[1], storageOnly: true);
            return 0;
        }
        if (args.Length == 1 && args[0] == "--storage-cadence-unit")
        {
            EquipmentHistoryValidation.RunStorageCadence();
            return 0;
        }
        if (args.Length == 2 && args[0] == "--history-channel-layout")
        {
            EquipmentHistoryValidation.RunChannelLayout(args[1]);
            return 0;
        }
        if (args.Length == 1 && args[0] == "--history-trend-unit")
        {
            EquipmentHistoryValidation.RunTrendDrawing();
            return 0;
        }
        if (args.Length == 2 && args[0] == "--online-analyzer-capacity")
        {
            EquipmentHistoryValidation.RunOnlineAnalyzerCapacity(args[1]);
            return 0;
        }
        if (args.Length == 1 && args[0] == "--alarm-step-unit")
        {
            SettingsUiValidation.RunAlarmSteps();
            return 0;
        }
        if (args.Length == 1 && args[0] == "--alarm-unit")
        {
            AlarmUnit();
            Console.WriteLine($"Alarm assertions passed: {assertions}.");
            return 0;
        }
        if (args.Length == 3 && args[0] == "--alarm-live")
        {
            SeedByReflection(App, args[1]);
            AlarmLive(args[1], args[2]);
            return 0;
        }
        if (args.Length == 5 && args[0] == "--alarm-walk")
        {
            SeedByReflection(App, args[1]);
            var device = SnmpCredentialStore.EffectiveDevice(UpsSettingsStore.Current.Devices.Single(d => d.Id == args[2]));
            var root = args[3];
            if (root != "1.3.6.1.2.1.33.1.6" && root != "1.3.6.1.4.1.476.1.42.3.2" &&
                root != "1.3.6.1.4.1.476.1.42.3.3" && root != "1.3.6.1.4.1.476.1.42.3.5.1" &&
                root != "1.3.6.1.4.1.476.1.42.3.5.2")
                throw new ArgumentException("Only alarm and environmental measurement subtrees are allowed.");
            var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(device.IpAddress), device.Port);
            var credential = Resolve(device.CredentialProfile);
            var cursor = root;
            var rows = new List<object>();
            var watch = Stopwatch.StartNew();
            var complete = false;
            for (var index = 0; index < 800 && watch.Elapsed < TimeSpan.FromSeconds(60); index++)
            {
                var vars = ((IEnumerable)Call("UpsSnmpClient", "GetNextValues", endpoint, credential, new List<string> { cursor },
                    device.TimeoutMilliseconds)).Cast<Variable>().ToArray();
                if (vars.Length != 1) throw new InvalidDataException("Expected one GETNEXT result.");
                var variable = vars[0];
                var oid = variable.Id.ToString();
                if (!oid.StartsWith(root + ".", StringComparison.Ordinal) || variable.Data is EndOfMibView)
                { complete = true; break; }
                if (oid == cursor) throw new InvalidDataException("Non-advancing SNMP walk.");
                rows.Add(new { Oid = oid, Raw = variable.Data.ToString(), Type = variable.Data.TypeCode.ToString() });
                cursor = oid;
            }
            Directory.CreateDirectory(args[4]);
            File.WriteAllText(Path.Combine(args[4], device.Id + "-" + root + ".json"),
                JsonSerializer.Serialize(new { device.Id, Root = root, Complete = complete, Rows = rows }, JsonOptions));
            Console.WriteLine($"{device.Id}: {rows.Count} objects in {root}; complete={complete}");
            return 0;
        }
        if (args.Length == 3 && args[0] == "--parameter-audit")
        {
            SeedByReflection(App, args[1]);
            ParameterAudit(args[1], args[2]);
            return 0;
        }
        if (args.Length == 3 && args[0] == "--measurement-read")
        {
            SeedByReflection(App, args[1]);
            Directory.CreateDirectory(args[2]);
            var snapshots = new List<UpsSnapshot>();
            foreach (var id in new[] { "UPS-A01", "UPS-B01", "UPS-C01", "UPS-C03" })
            {
                var device = UpsSettingsStore.Current.Devices.Single(d => d.Id == id);
                var snapshot = UpsAcquisitionService.PollDevice(device, 0, null!);
                snapshots.Add(snapshot);
                Console.WriteLine($"{id}: {snapshot.Quality}; {snapshot.SysName}; output kW currently interpreted: {snapshot.OutputKw}");
                if (!snapshot.IsGood) continue;
                var effective = SnmpCredentialStore.EffectiveDevice(device);
                var credential = Resolve(device.CredentialProfile);
                var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(device.IpAddress), effective.Port);
                const string root = "1.3.6.1.4.1.476.1.42.3.5";
                var cursor = root + ".2";
                var rows = new List<object>();
                var watch = Stopwatch.StartNew();
                for (var index = 0; index < 450 && watch.Elapsed < TimeSpan.FromSeconds(60); index++)
                {
                    var vars = (IEnumerable)Call("UpsSnmpClient", "GetNextValues", endpoint, credential, new List<string> { cursor }, effective.TimeoutMilliseconds);
                    var variable = vars.Cast<object>().Single();
                    var oid = variable.GetType().GetProperty("Id")!.GetValue(variable)!.ToString()!;
                    var data = variable.GetType().GetProperty("Data")!.GetValue(variable)!;
                    if (!oid.StartsWith(root + ".2.", StringComparison.Ordinal) || oid == cursor) break;
                    rows.Add(new { Oid = oid, Raw = data.ToString(), Type = data.GetType().Name });
                    cursor = oid;
                    Thread.Sleep(20);
                }
                foreach (var suffix in new[] { ".1.18.0", ".1.19.0" })
                {
                    var vars = (IEnumerable)Call("UpsSnmpClient", "GetValues", endpoint, credential, new List<string> { root + suffix }, effective.TimeoutMilliseconds);
                    foreach (var variable in vars.Cast<object>())
                        rows.Add(new { Oid = variable.GetType().GetProperty("Id")!.GetValue(variable)!.ToString(), Raw = variable.GetType().GetProperty("Data")!.GetValue(variable)!.ToString() });
                }
                File.WriteAllText(Path.Combine(args[2], id + "-vendor-raw.json"), JsonSerializer.Serialize(rows, JsonOptions));
                Console.WriteLine($"{id}: {rows.Count} vendor measurement objects read (GET/GETNEXT only).");
            }
            File.WriteAllText(Path.Combine(args[2], "snapshots.json"), JsonSerializer.Serialize(snapshots, JsonOptions));
            return 0;
        }
        if ((args.Length == 3 && (args[0] == "--flex-read" || args[0] == "--flex-exl")) ||
            (args.Length == 4 && args[0] == "--flex-device"))
        {
            SeedByReflection(App, args[1]);
            Directory.CreateDirectory(args[2]);
            foreach (var id in args[0] == "--flex-device" ? new[] { args[3] } :
                args[0] == "--flex-exl" ? new[] { "UPS-C01", "UPS-C03", "UPS-C04", "UPS-C05" } : new[] { "UPS-B01", "UPS-A01" })
            {
                var device = SnmpCredentialStore.EffectiveDevice(UpsSettingsStore.Current.Devices.Single(d => d.Id == id));
                var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(device.IpAddress), device.Port);
                var credential = Resolve(device.CredentialProfile);
                const string root = "1.3.6.1.4.1.476.1.42.3.9.20.1.";
                var cursor = root + "10";
                var rows = new List<object>();
                string failure = "";
                for (var index = 0; index < 1500; index++)
                {
                    IEnumerable vars;
                    try
                    {
                        vars = (IEnumerable)Call("UpsSnmpClient", "GetNextValues", endpoint, credential, new List<string> { cursor }, device.TimeoutMilliseconds);
                    }
                    catch (Exception ex) when (ex.GetType().Name == "UpsSnmpFailure")
                    {
                        failure = ex.Message;
                        Console.WriteLine($"{id}: vendor flexible walk unavailable: {failure}");
                        break;
                    }
                    var variable = vars.Cast<object>().Single();
                    var oid = variable.GetType().GetProperty("Id")!.GetValue(variable)!.ToString()!;
                    var label = variable.GetType().GetProperty("Data")!.GetValue(variable)!.ToString()!;
                    if (!oid.StartsWith(root + "10.", StringComparison.Ordinal) || oid == cursor) break;
                    cursor = oid;
                    if (label.StartsWith("System Input", StringComparison.OrdinalIgnoreCase) ||
                        label.StartsWith("System Output", StringComparison.OrdinalIgnoreCase) ||
                        label.StartsWith("Bypass Input", StringComparison.OrdinalIgnoreCase) ||
                        label.StartsWith("Battery ", StringComparison.OrdinalIgnoreCase) ||
                        label.StartsWith("DC Bus ", StringComparison.OrdinalIgnoreCase) ||
                        label == "Inlet Air Temperature" || label == "UPS Battery Status")
                    {
                        var suffix = oid[(root + "10.").Length..];
                        object[] data;
                        try
                        {
                            data = ((IEnumerable)Call("UpsSnmpClient", "GetValues", endpoint, credential,
                                new List<string> { root + "20." + suffix, root + "30." + suffix }, device.TimeoutMilliseconds)).Cast<object>().ToArray();
                        }
                        catch (Exception ex) when (ex.GetType().Name == "UpsSnmpFailure")
                        {
                            failure = ex.Message;
                            rows.Add(new { Label = label, Index = suffix, Failure = failure });
                            Console.WriteLine($"{id}: {label} ({suffix}): {failure}");
                            break;
                        }
                        rows.Add(new { Label = label, Index = suffix, ValueOid = root + "20." + suffix,
                            Value = data[0].GetType().GetProperty("Data")!.GetValue(data[0])!.ToString(),
                            Unit = data[1].GetType().GetProperty("Data")!.GetValue(data[1])!.ToString() });
                    }
                    Thread.Sleep(10);
                }
                File.WriteAllText(Path.Combine(args[2], id + "-flexible-measurements.json"), JsonSerializer.Serialize(rows, JsonOptions));
                File.WriteAllText(Path.Combine(args[2], id + "-flexible-status.json"), JsonSerializer.Serialize(new { DeviceId = id,
                    TimestampUtc = DateTime.UtcNow, Rows = rows.Count, Failure = failure, Complete = failure.Length == 0 }, JsonOptions));
                Console.WriteLine($"{id}: {rows.Count} labeled vendor measurement rows; no credential/configuration values requested.");
            }
            return 0;
        }
        if (args.Length == 3 && args[0] == "--restart-live")
        {
            SeedByReflection(App, args[1]);
            Baseline(App, args[2], "after-restart-known-good-polls.json");
            return 0;
        }
        if (args.Length == 4 && args[0] == "--c04-audit")
        {
            C04Audit(args[1], args[2], args[3]);
            return 0;
        }
        if (args.Length == 3 && args[0] == "--c04-verify")
        {
            SeedByReflection(App, args[1]);
            Directory.CreateDirectory(args[2]);
            var result = UpsConnectionRecovery.VerifyAsync(Admin, CancellationToken.None).GetAwaiter().GetResult();
            var file = Path.Combine(args[2], "recovery-verification.json");
            File.WriteAllText(file, JsonSerializer.Serialize(result, JsonOptions));
            using var form = new UpsConnectionRecoveryForm(Admin);
            form.Show();
            var status = (TextBox)typeof(UpsConnectionRecoveryForm).GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            status.Text = (string)typeof(UpsConnectionRecoveryForm).GetMethod("Describe", Static)!.Invoke(null, new object[] { result })!;
            var retest = (Button)typeof(UpsConnectionRecoveryForm).GetField("_retest", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            retest.Enabled = result.ReferencesVerified && result.ProfilePreserved && result.CredentialAvailable && result.ProfileAssigned;
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(args[2], "recovery-workflow.png"));
            form.Close();
            var resolved = Resolve("default");
            var community = (string)resolved.GetType().GetProperty("Community")!.GetValue(resolved)!;
            var files = Directory.EnumerateFiles(args[2], "*.json").Concat(Directory.EnumerateFiles(Path.GetDirectoryName(SnmpCredentialStore.StorePath())!, "*.jsonl")).ToList();
            var secretAbsent = files.All(path => !File.ReadAllText(path).Contains(community, StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(args[2], "secret-output-check.json"), JsonSerializer.Serialize(new
            {
                TimestampUtc = DateTime.UtcNow, SecretAbsentFromRecoveryReportsAndGeneratedAuditLogs = secretAbsent,
                FilesChecked = files.Count, StoredCredentialDisplayed = false, ClipboardUsed = false,
                Note = "Exact plaintext compared internally; neither the community nor any plaintext-derived hash was written. Screenshot controls contain only redacted result fields."
            }, JsonOptions));
            if (!secretAbsent) throw new InvalidOperationException("Secret-output check failed; do not disclose the report.");
            Console.WriteLine($"Credential available: {(result.CredentialAvailable ? "Yes" : "No")}; DPAPI: {(result.DpapiDecryptionSuccessful ? "Yes" : "No")}; assigned: {(result.ProfileAssigned ? "Yes" : "No")}; profile preserved: {(result.ProfilePreserved ? "Yes" : "No")}.");
            foreach (var test in result.ReferenceTests)
                Console.WriteLine($"{test.DeviceId}: {test.Finding}; valid measurement poll: {result.ReferencePolls.Any(p => p.DeviceId == test.DeviceId && p.IsGood)}.");
            Console.WriteLine($"C04 v2c GET: {result.TargetTest?.Finding}; identity: {result.IdentityRead}; measurements: {result.MeasurementsRead}; recovery verified: {result.ConnectionVerified}.");
            Console.WriteLine("Reports and generated logs checked internally: stored community absent.");
            return 0;
        }
        if (args.Length == 1 && args[0] == "--restart-check")
        {
            var profile = Resolve("qa-v3");
            Check((string)profile.GetType().GetProperty("AuthSecret")!.GetValue(profile)! == "qa-auth-password", "DPAPI survives a fresh process");
            Check(SnmpCredentialStore.ListProfiles().Single(p => p.Name == "qa-v3").SecurityMode == "AuthPriv", "v3 mode persists");
            typeof(UpsSettingsStore).GetField("_current", Static)!.SetValue(null, JsonSerializer.Deserialize<UpsSettings>(File.ReadAllText(UpsSettingsStore.SettingsPath())));
            Check(WebCardCredentialStore.Load("UPS-C04", Admin).HasPassword && (bool)Call("WebCardCredentialStore", "VerifyProtection", "UPS-C04", Admin), "Web Card DPAPI credentials persist across a fresh process");
            Check(UpsSettingsStore.Current.Devices.Single(d => d.Id == "UPS-C04").CredentialProfile == "C04-SNMP" &&
                UpsSettingsStore.Current.Devices.Single(d => d.Id == "UPS-C01").CredentialProfile == "default" &&
                UpsSettingsStore.Current.Devices.Single(d => d.Id == "UPS-C03").CredentialProfile == "default", "C04-only assignment persists without changing C01/C03");
            return 0;
        }
        if (args.Length != 1 || args[0] != "--unit")
            throw new ArgumentException("Use --unit, --restart-check, --live <backup-configuration> <report-output>, --after <backup-configuration> <screenshots>, or --before <old-dll> <backup-configuration> <screenshots>.");
        Unit();
        return 0;
    }

    private static void C04Audit(string current, string previous, string output)
    {
        Directory.CreateDirectory(output);
        SeedByReflection(App, current);
        var savedProfiles = Path.Combine(current, "snmp-credentials.json");
        var oldProfiles = Path.Combine(previous, "snmp-credentials.json");
        var storeHash = SHA256.HashData(File.ReadAllBytes(savedProfiles));
        var registryHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(current, "ups-settings.json")));
        var oldRegistry = JsonSerializer.Deserialize<UpsSettings>(File.ReadAllText(Path.Combine(previous, "ups-settings.json")))!;
        var summary = SnmpCredentialStore.ListProfiles().SingleOrDefault(p => p.Name == "default");
        var resolved = Resolve("default");
        var available = resolved is not null && resolved.GetType().GetProperty("Community")!.GetValue(resolved) is string { Length: > 0 };
        var tests = new List<object>();
        var polls = new Dictionary<string, UpsSnapshot>();
        foreach (var id in new[] { "UPS-C01", "UPS-C03", "UPS-C04" })
        {
            var device = UpsSettingsStore.Current.Devices.Single(d => d.Id == id);
            var test = Call("UpsConnectionDiagnostics", "TestDevice", device, CancellationToken.None);
            tests.Add(test);
            if ((bool)test.GetType().GetProperty("IsConnected")!.GetValue(test)!)
                polls[id] = UpsAcquisitionService.PollDevice(device, 0, null!);
            Console.WriteLine($"{id}: {test.GetType().GetProperty("Finding")!.GetValue(test)}; measurement quality: {polls.GetValueOrDefault(id)?.Quality.ToString() ?? "not verified"}");
        }
        var target = UpsSettingsStore.Current.Devices.Single(d => d.Id == "UPS-C04");
        var historicalTests = File.Exists(Path.Combine(current, "ups-connection-history.jsonl"))
            ? File.ReadLines(Path.Combine(current, "ups-connection-history.jsonl")).Select(line => JsonDocument.Parse(line))
                .Select(document => { using (document) return document.RootElement.Clone(); }).ToList()
            : new List<JsonElement>();
        var lastSuccessful = new[] { "UPS-C01", "UPS-C03", "UPS-C04" }.Select(id =>
        {
            var last = historicalTests.Where(test => test.GetProperty("DeviceId").GetString() == id && test.GetProperty("SnmpResponds").GetBoolean())
                .OrderByDescending(test => test.GetProperty("TimestampUtc").GetDateTime()).FirstOrDefault();
            return new
            {
                DeviceId = id,
                TimestampUtc = last.ValueKind == JsonValueKind.Undefined ? (DateTime?)null : last.GetProperty("TimestampUtc").GetDateTime(),
                Profile = last.ValueKind == JsonValueKind.Undefined ? null : last.GetProperty("ProfileName").GetString(),
                Version = last.ValueKind == JsonValueKind.Undefined ? null : last.GetProperty("ProfileVersion").GetString()
            };
        }).ToArray();
        var audit = new
        {
            TimestampUtc = DateTime.UtcNow, ProfileExists = summary is not null, CredentialAvailable = available,
            DpapiDecryptionSuccessful = resolved is not null, Version = summary?.Version, ProfileAssigned = target.CredentialProfile == "default",
            PreviousC04Profile = oldRegistry.Devices.Single(d => d.Id == "UPS-C04").CredentialProfile,
            ProtectedStoreUnchangedFromPreviouslyTestedBackup = CryptographicOperations.FixedTimeEquals(storeHash, SHA256.HashData(File.ReadAllBytes(oldProfiles))),
            ProductionCredentialStorePreserved = CryptographicOperations.FixedTimeEquals(storeHash, SHA256.HashData(File.ReadAllBytes(savedProfiles))),
            ProductionRegistryPreserved = CryptographicOperations.FixedTimeEquals(registryHash, SHA256.HashData(File.ReadAllBytes(Path.Combine(current, "ups-settings.json")))),
            SuccessfulMeasurementDevices = polls.Where(p => p.Value.IsGood).Select(p => p.Key).ToArray(),
            ConnectionTests = tests, MeasurementPolls = polls, PreviousSuccessfulConnections = lastSuccessful,
            Interpretation = "Card-side community change is user-reported. A v2c timeout alone cannot confirm a credential mismatch. No secret values or hashes of plaintext secrets are included."
        };
        File.WriteAllText(Path.Combine(output, "credential-store-audit.json"), JsonSerializer.Serialize(audit, JsonOptions));
        Console.WriteLine($"Credential available: {(available ? "Yes" : "No")}; DPAPI decryption successful: {(resolved is not null ? "Yes" : "No")}; default profile assigned: {(target.CredentialProfile == "default" ? "Yes" : "No")}.");
    }

    private static object Call(string type, string method, params object?[] values)
    {
        try { return App.GetTypes().Single(t => t.Name == type).GetMethod(method, Static)!.Invoke(null, values)!; }
        catch (TargetInvocationException ex) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException!).Throw(); throw; }
    }

    private static object Resolve(string name) => Call("SnmpCredentialStore", "Resolve", name);

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException("FAIL: " + message);
        assertions++;
        Console.WriteLine("PASS: " + message);
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { Check(true, message); return; }
        throw new InvalidOperationException("FAIL: " + message);
    }

    private static void Save(string name, string version, string? community, string auth = "None", string? authSecret = null,
        string privacy = "None", string? privSecret = null, UserSession? session = null, bool confirm = false)
        => SnmpCredentialStore.Save(name, version, community!, version == "v3" ? "qa-user" : "", auth, authSecret!, privacy, privSecret!,
            session ?? Admin, 1161, 800, 0, confirm);

    private static void DiagnosticsUnit()
    {
        var reference = new UpsConnectionTest
        {
            DeviceId = "qa-reference", IpAddress = "127.0.0.1", Finding = UpsConnectionFinding.Connected,
            TimestampUtc = DateTime.UtcNow, SnmpResponds = true, SupportsUpsMib = true
        };
        var noSubject = (IReadOnlyList<string>)Call("UpsConnectionDiagnostics", "Compare", null, reference);
        Check(noSubject.Count > 0 && noSubject.Any(line => line.Contains("not run", StringComparison.OrdinalIgnoreCase)),
            "untested UPS with a known-good reference has an explicit not-tested comparison");
        var noEvidence = (IReadOnlyList<string>)Call("UpsConnectionDiagnostics", "Compare", null, null);
        Check(noEvidence.Count > 0, "missing subject and reference are handled without fabricating a comparison");
        var subject = new UpsConnectionTest { DeviceId = "qa-untested", IpAddress = "127.0.0.2" };
        var noReference = (IReadOnlyList<string>)Call("UpsConnectionDiagnostics", "Compare", subject, null);
        Check(noReference.Any(line => line.Contains("No known-good reference")),
            "tested subject without a reference retains the reference-test guidance");
        Check(((IReadOnlyList<string>)Call("UpsConnectionDiagnostics", "Compare", subject, reference))
            .Any(line => line.Contains("Compared with known-good")), "two connection results still produce the comparison");

        var settings = UpsSettings.CreateDefault();
        var known = settings.Devices[0];
        known.Verification = UpsVerification.Verified;
        var untested = settings.Devices[1];
        typeof(UpsSettingsStore).GetField("_current", Static)!.SetValue(null, settings);
        typeof(PostgresDatabase).GetField("_connectionString", Static)!.SetValue(null,
            "Host=127.0.0.1;Port=1;Username=isolated_qa;Database=isolated_qa;Timeout=1;Pooling=false");
        typeof(PostgresDatabase).GetField("_lastFailureUtc", Static)!.SetValue(null, DateTime.UtcNow);
        var diagnostics = App.GetTypes().Single(t => t.Name == "UpsConnectionDiagnostics");
        var tests = (System.Collections.Concurrent.ConcurrentDictionary<string, UpsConnectionTest>)
            diagnostics.GetField("Tests", Static)!.GetValue(null)!;
        tests.Clear();
        using var form = new Form { Size = new Size(1400, 900), Location = new Point(-2000, -2000), StartPosition = FormStartPosition.Manual };
        using var view = new UpsSnmpSettingsView(Admin) { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        form.Show();
        var viewType = typeof(UpsSnmpSettingsView);
        var tabs = (TabControl)viewType.GetField("_tabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        tabs.SelectedIndex = 3;
        viewType.GetField("_selectedDiagnosticId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, untested.Id);
        var refresh = viewType.GetMethod("RefreshDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic)!;
        refresh.Invoke(view, null);
        var detail = (TextBox)viewType.GetField("_detailBox", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        Check(detail.Text.Contains("CONNECTION TEST: not run"), "untested diagnostics selection renders before any reference has responded");
        reference.DeviceId = known.Id;
        reference.IpAddress = known.IpAddress;
        reference.ConnectionStamp = (string)typeof(SnmpCredentialStore).GetMethod("ConnectionStamp", Static)!.Invoke(null, new object[] { known })!;
        tests[known.Id] = reference;
        Check(Call("UpsConnectionDiagnostics", "KnownGoodReference", settings.Devices) is UpsConnectionTest,
            "fixture has a current known-good reference while the selected UPS is still untested");
        for (var tick = 0; tick < 5; tick++) refresh.Invoke(view, null);
        Check(detail.Text.Contains(untested.Id) && detail.Text.Contains("CONNECTION TEST: not run"),
            "repeated timer refresh of untested UPS after another test succeeds never crashes");
        viewType.GetField("_compareFirst", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, true);
        refresh.Invoke(view, null);
        Check(detail.Text.Contains("CONNECTION TEST: not run") && !detail.Text.Contains("KNOWN-GOOD COMPARISON"),
            "compare-first mode does not dereference an absent selected-device test");
        foreach (var (quality, label) in new[]
        {
            (UpsQuality.NoData, "Unknown (no poll yet)"), (UpsQuality.NotSupported, "SNMP Responding"),
            (UpsQuality.Timeout, "SNMP Timeout — Cause Unknown"), (UpsQuality.Offline, "SNMP Timeout — Cause Unknown"),
            (UpsQuality.Invalid, "Invalid response / data")
        })
        {
            var snapshot = new UpsSnapshot { DeviceId = untested.Id, Quality = quality, TimestampUtc = DateTime.UtcNow };
            var expected = quality is UpsQuality.Timeout or UpsQuality.Offline ? "SNMP timeout (not yet diagnosed)" : "Not tested";
            Check((string)Call("UpsConnectionDiagnostics", "Classification", untested, null, snapshot) == expected,
                "classification handles missing connection test with " + quality + " poll");
            Check(Equals(viewType.GetMethod("ConnectionText", Static)!.Invoke(null, new object[] { untested, snapshot }), label),
                "connection label preserves " + quality + " without a connection test");
        }
        var csv = (string)Call("UpsConnectionDiagnostics", "BuildCsvReport", settings.Devices, null);
        var markdown = (string)Call("UpsConnectionDiagnostics", "BuildMarkdownReport", settings.Devices, null);
        Check(csv.Contains(untested.Id) && markdown.Contains("Connection test: not run"),
            "diagnostic exports retain untested units when a known-good reference exists");
        subject.DeviceId = untested.Id;
        subject.TimestampUtc = DateTime.UtcNow;
        subject.Finding = UpsConnectionFinding.HostNotResponding;
        subject.ConnectionStamp = (string)typeof(SnmpCredentialStore).GetMethod("ConnectionStamp", Static)!.Invoke(null, new object[] { untested })!;
        tests[untested.Id] = subject;
        foreach (var compareFirst in new[] { false, true })
        {
            viewType.GetField("_compareFirst", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, compareFirst);
            refresh.Invoke(view, null);
            Check(detail.Text.Contains("KNOWN-GOOD COMPARISON") && detail.Text.Contains("Compared with known-good") &&
                !detail.Text.Contains("CONNECTION TEST: not run"),
                "completed failed test replaces not-tested state and renders comparison, compare-first=" + compareFirst);
        }
        subject.ConnectionStamp = "obsolete configuration";
        refresh.Invoke(view, null);
        Check(detail.Text.Contains("CONNECTION TEST: not run"),
            "invalidated selected-device test safely returns to not-tested even when a reference still exists");
        viewType.GetField("_selectedDiagnosticId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, string.Empty);
        refresh.Invoke(view, null);
        Check(detail.Text.Contains("Select a unit"), "cleared diagnostics selection returns to selection guidance");
        tests.Clear();
        form.Close();
    }

    private static void AlarmUnit()
    {
        const string vendor = "1.3.6.1.4.1.476.1.42.3.2.3.1.";
        const string count = "1.3.6.1.4.1.476.1.42.3.2.2.0";
        const string rfc = "1.3.6.1.2.1.33.1.6.2.1.";
        const string label = "1.3.6.1.4.1.476.1.42.3.9.20.1.10.1.2.100.6354";
        Dictionary<string, ISnmpData> Fixture(string description = "1.3.6.1.4.1.476.1.42.3.2.1.43.3") => new()
        {
            [count] = new Integer32(1),
            [vendor + "2.82"] = new ObjectIdentifier(description),
            [vendor + "3.82"] = new TimeTicks(16180),
            [vendor + "10.82"] = new Integer32(6),
            [vendor + "12.82"] = new Integer32(1),
            [vendor + "14.82"] = new Integer32(0),
            [vendor + "18.82"] = new Integer32(1),
            [UpsOidMap.Find("AlarmsPresent").Oid] = new Integer32(1)
        };
        UpsSnapshot Read(Dictionary<string, ISnmpData> data, bool vertiv = true, bool metadataTimeout = false, bool race = false,
            double reported = 1, bool rowChanged = false, bool nonAdvancing = false, bool v1End = false,
            bool vendorEndTimeout = false, Action<int, int>? requestCounts = null)
        {
            var snapshot = new UpsSnapshot { Quality = UpsQuality.Good, SysObjectId = vertiv ? "1.3.6.1.4.1.476.1.42" : "other",
                Values = new Dictionary<string, double> { ["AlarmsPresent"] = reported } };
            var reads = 0;
            var getCalls = 0;
            var nextCalls = 0;
            Dictionary<string, ISnmpData> Get(IList<string> oids)
            {
                getCalls++;
                if (metadataTimeout && oids.Any(o => o.StartsWith(vendor + "3.", StringComparison.Ordinal)))
                {
                    var type = App.GetTypes().Single(t => t.Name == "UpsSnmpFailure");
                    throw (Exception)Activator.CreateInstance(type, UpsQuality.Timeout, "synthetic alarm timeout", 0)!;
                }
                if (oids.Contains(count)) reads++;
                return oids.ToDictionary(oid => oid, oid => rowChanged && oid == vendor + "2.82" ? (ISnmpData)new NoSuchInstance() :
                    race && oid == count && reads > 1 ? (ISnmpData)new Integer32(2) :
                    data.GetValueOrDefault(oid) ?? new NoSuchInstance());
            }
            Variable Next(string cursor)
            {
                nextCalls++;
                var column = (cursor.StartsWith(vendor, StringComparison.Ordinal) ? vendor : rfc) + "2.";
                if (vendorEndTimeout && column == vendor + "2.")
                {
                    var type = App.GetTypes().Single(t => t.Name == "UpsSnmpFailure");
                    throw (Exception)Activator.CreateInstance(type, UpsQuality.Timeout, "synthetic vendor walk timeout", 0)!;
                }
                if (nonAdvancing) return new Variable(new ObjectIdentifier(column + "82"), new ObjectIdentifier("1.3.6.1.2.1.33.1.6.3.18"));
                var candidate = data.Where(p => p.Key.StartsWith(column, StringComparison.Ordinal) &&
                    new ObjectIdentifier(p.Key).CompareTo(new ObjectIdentifier(cursor)) > 0)
                    .OrderBy(p => new ObjectIdentifier(p.Key)).FirstOrDefault();
                if (v1End && candidate.Key is null)
                {
                    var type = App.GetTypes().Single(t => t.Name == "UpsSnmpFailure");
                    throw (Exception)Activator.CreateInstance(type, UpsQuality.Invalid, "synthetic v1 end", 2)!;
                }
                return candidate.Key is null ? new Variable(new ObjectIdentifier("1.3.6.1.9.0"), new EndOfMibView()) :
                    new Variable(new ObjectIdentifier(candidate.Key), candidate.Value);
            }
            Call("UpsAlarmReader", "Read", snapshot,
                (Func<IList<string>, Dictionary<string, ISnmpData>>)Get, (Func<string, Variable>)Next);
            requestCounts?.Invoke(getCalls, nextCalls);
            return snapshot;
        }
        var good = Read(Fixture());
        Check(good.AlarmDetailsComplete && good.CardAlarms.Single().Name == "Multiple Fan Failure", "vendor OID resolves to exact multiple-fan failure");
        var fan = good.CardAlarms.Single();
        Check(fan.Active == true && fan.Acknowledged == false && fan.CardType == "Alarm" && fan.CardSeverity == "Not specified",
            "vendor state/type/acknowledgement decoded without inventing unspecified severity");
        Check(fan.OccurredAtUptime.Length > 0 && fan.DescriptionOid.EndsWith(".43.3"), "card occurrence uptime and exact identity retained");
        var items = UpsAlarmEvaluator.Evaluate(good, null!);
        Check(items.Count(i => i.CardAlarm is not null) == 1 && !items.Any(i => i.Text.Contains("details incomplete")),
            "specific vendor alarm replaces generic count rather than duplicating RFC general fault");
        var cleared = Fixture();
        cleared[vendor + "12.82"] = new Integer32(2);
        var clearSnapshot = Read(cleared);
        Check(clearSnapshot.CardAlarms.Single().Active == false && UpsAlarmEvaluator.Worst(UpsAlarmEvaluator.Evaluate(clearSnapshot, null!)) < AlarmSeverity.Alarm,
            "cleared retained event is not presented as an active fault");
        var message = Fixture();
        message[vendor + "10.82"] = new Integer32(2);
        Check(UpsAlarmEvaluator.Worst(UpsAlarmEvaluator.Evaluate(Read(message), null!)) == AlarmSeverity.Normal,
            "normal card message such as Battery Charging is not promoted to an alarm");
        var flex = Fixture("1.3.6.1.4.1.476.1.42.3.2.7.1.6354");
        flex[vendor + "6.82"] = new ObjectIdentifier(label);
        flex[label] = new OctetString("Card supplied event name");
        Check(Read(flex).CardAlarms.Single().Name == "Card supplied event name", "flexible condition resolves using the exact card-supplied label reference");
        flex[vendor + "6.82"] = new ObjectIdentifier("1.3.6.1.4.1.999.1");
        Check(!Read(flex).AlarmDetailsComplete && Read(flex).CardAlarms.Single().Name.StartsWith("Unrecognized"),
            "arbitrary references are not dereferenced and unknown alarm names remain explicit");
        var badState = Fixture();
        badState[vendor + "12.82"] = new Integer32(77);
        Check(!Read(badState).AlarmDetailsComplete && Read(badState).CardAlarms.Single().Active is null,
            "unknown state is not silently treated as clear");
        var failed = Read(Fixture(), metadataTimeout: true);
        Check(!failed.AlarmDetailsComplete && failed.CardAlarms.Count == 1 && failed.AlarmDetailsStatus.Contains("synthetic alarm timeout"),
            "metadata timeout retains discovered alarm and an explicit failure status");
        Check(!Read(Fixture(), race: true).AlarmDetailsComplete, "changing count/table cannot be reported as a verified alarm list");
        var changed = Read(Fixture(), rowChanged: true);
        Check(!changed.AlarmDetailsComplete && changed.CardAlarms.Single().Active is null,
            "disappearing or replaced row is never mixed with stale active-state metadata");
        Check(!Read(Fixture(), nonAdvancing: true).AlarmDetailsComplete, "non-advancing table aborts explicitly rather than looping");
        Check(Read(Fixture(), v1End: true).AlarmDetailsComplete, "SNMPv1 noSuchName at end of table is handled as end, not failed poll");
        Check(Read(Fixture("1.3.6.1.4.1.476.1.42.3.2.1.43.2")).CardAlarms.Single().Name == "Redundant Fan Failure",
            "redundant fan alarm identity is distinct from multiple fan failure");
        var transferInhibited = Fixture("1.3.6.1.4.1.476.1.42.3.2.1.199");
        transferInhibited[vendor + "10.82"] = new Integer32(4);
        var transfer = Read(transferInhibited);
        Check(transfer.AlarmDetailsComplete && transfer.CardAlarms.Single().Name == "Manual transfer to bypass inhibited" &&
            UpsAlarmEvaluator.Worst(UpsAlarmEvaluator.Evaluate(transfer, null!)) == AlarmSeverity.PreAlarm,
            "A03 vendor condition 199 resolves to manual transfer inhibition with the card's Warning classification");
        var majorMessage = Fixture();
        majorMessage[vendor + "10.82"] = new Integer32(2);
        majorMessage[vendor + "14.82"] = new Integer32(6);
        Check(UpsAlarmEvaluator.Worst(UpsAlarmEvaluator.Evaluate(Read(majorMessage), null!)) == AlarmSeverity.Alarm,
            "major severity is not suppressed by message classification");
        foreach (var (type, name) in new[] { (0, "Not specified"), (2, "Message"), (4, "Warning"), (6, "Alarm"), (8, "Fault") })
        foreach (var (severity, severityName) in new[] { (0, "Not specified"), (3, "Minor"), (6, "Major"), (9, "Critical") })
        {
            var fixture = Fixture();
            fixture[vendor + "10.82"] = new Integer32(type);
            fixture[vendor + "14.82"] = new Integer32(severity);
            var decoded = Read(fixture);
            var expectedSeverity = severity is 6 or 9 ? AlarmSeverity.Alarm :
                type == 2 ? AlarmSeverity.Normal :
                type == 4 || severity == 3 ? AlarmSeverity.PreAlarm : AlarmSeverity.Alarm;
            Check(decoded.CardAlarms.Single().CardType == name && decoded.CardAlarms.Single().CardSeverity == severityName &&
                UpsAlarmEvaluator.Worst(UpsAlarmEvaluator.Evaluate(decoded, null!)) == expectedSeverity,
                $"card classification {name}/{severityName} respects explicit major/critical severity");
        }
        var acknowledged = Fixture();
        acknowledged[vendor + "18.82"] = new Integer32(2);
        Check(Read(acknowledged).CardAlarms.Single().Acknowledged == true &&
            UpsAlarmEvaluator.Worst(UpsAlarmEvaluator.Evaluate(Read(acknowledged), null!)) == AlarmSeverity.Alarm,
            "acknowledging a still-active condition does not clear or suppress its alarm");
        var standard = new Dictionary<string, ISnmpData>
        {
            [rfc + "2.18"] = new ObjectIdentifier("1.3.6.1.2.1.33.1.6.3.18"),
            [rfc + "3.18"] = new TimeTicks(100),
            [UpsOidMap.Find("AlarmsPresent").Oid] = new Integer32(1)
        };
        Check(Read(standard, vertiv: false).CardAlarms.Single().Name == "General fault" &&
            Read(standard, vertiv: false).AlarmDetailsComplete, "RFC1628 fallback decodes standard alarm identities");
        var conflicting = new Dictionary<string, ISnmpData>(standard) { [count] = new Integer32(0) };
        var conflict = Read(conflicting);
        Check(!conflict.AlarmDetailsComplete && conflict.CardAlarms.Single().Active is null &&
            conflict.CardAlarms.Single().EvidenceConflict && conflict.CardAlarms.Single().StateText.StartsWith("UNCONFIRMED"),
            "B01 generic RFC fault is unconfirmed only after verifying the vendor table is empty and its count remains zero");
        Check(UpsAlarmEvaluator.Worst(UpsAlarmEvaluator.Evaluate(conflict, null!)) == AlarmSeverity.PreAlarm &&
            conflict.AlarmDetailsStatus.Contains("Card data conflict"),
            "conflicting generic evidence stays visible as a warning, not a confirmed fault or an all-clear");
        var vendorChanged = Read(conflicting, race: true);
        Check(!vendorChanged.CardAlarms.Single().EvidenceConflict &&
            UpsAlarmEvaluator.Worst(UpsAlarmEvaluator.Evaluate(vendorChanged, null!)) == AlarmSeverity.Alarm,
            "a changed vendor count prevents downgrading the reported RFC fault");
        var vendorTimeout = Read(conflicting, vendorEndTimeout: true);
        Check(!vendorTimeout.AlarmDetailsComplete && !vendorTimeout.CardAlarms.Any(a => a.EvidenceConflict) &&
            UpsAlarmEvaluator.Worst(UpsAlarmEvaluator.Evaluate(vendorTimeout, null!)) == AlarmSeverity.Alarm,
            "vendor-table timeout never becomes proof that the UPS is clear");
        var inconsistentVendor = new Dictionary<string, ISnmpData>(conflicting)
        {
            [vendor + "2.82"] = new ObjectIdentifier("1.3.6.1.4.1.476.1.42.3.2.1.43.3")
        };
        Check(!Read(inconsistentVendor).CardAlarms.Single().EvidenceConflict,
            "zero vendor count with a nonempty vendor table cannot downgrade the RFC fault");
        var unknownVendor = new Dictionary<string, ISnmpData>(standard);
        Check(Read(unknownVendor).CardAlarms.Single().Active == true &&
            !Read(unknownVendor).CardAlarms.Single().EvidenceConflict,
            "unavailable vendor count preserves normal RFC fallback, not a fabricated clear-state conflict");
        var specificFault = new Dictionary<string, ISnmpData>(conflicting)
        {
            [rfc + "2.18"] = new ObjectIdentifier("1.3.6.1.2.1.33.1.6.3.3")
        };
        Check(Read(specificFault).CardAlarms.Single().Active == true &&
            UpsAlarmEvaluator.Worst(UpsAlarmEvaluator.Evaluate(Read(specificFault), null!)) == AlarmSeverity.Alarm,
            "specific RFC battery-low alarm is not suppressed by an empty vendor table");
        conflict.Values = new Dictionary<string, double> { ["AlarmsPresent"] = 1, ["OutputSource"] = 5 };
        Check(UpsAlarmEvaluator.Evaluate(conflict, null!).Any(a => a.Text == "UPS ON BATTERY" && a.Severity == AlarmSeverity.Alarm),
            "conflicting general fault never suppresses independent on-battery evaluation");
        conflict.Values = new Dictionary<string, double> { ["AlarmsPresent"] = 1 };
        var empty = Read(new Dictionary<string, ISnmpData> { [count] = new Integer32(0) });
        Check(!empty.AlarmDetailsComplete, "zero vendor count cannot conceal a positive RFC alarm count");
        var verifiedEmpty = Read(new Dictionary<string, ISnmpData> { [count] = new Integer32(0) }, reported: 0);
        Check(verifiedEmpty.AlarmDetailsComplete && UpsAlarmEvaluator.Evaluate(verifiedEmpty, null!).Count == 0,
            "verified empty condition list can show no active alarms");
        var emptyVendor = Fixture();
        emptyVendor[count] = new Integer32(0);
        var vendorCalls = (-1, -1);
        var emptyVendorSnapshot = Read(emptyVendor, reported: 0, requestCounts: (gets, nexts) => vendorCalls = (gets, nexts));
        Check(emptyVendorSnapshot.AlarmDetailsComplete && emptyVendorSnapshot.CardAlarms.Count == 0 &&
            vendorCalls == (1, 0), "zero-alarm Vertiv reading skips table walks and metadata requests");
        var standardCalls = (-1, -1);
        var emptyStandardSnapshot = Read(new Dictionary<string, ISnmpData>(), vertiv: false, reported: 0,
            requestCounts: (gets, nexts) => standardCalls = (gets, nexts));
        Check(emptyStandardSnapshot.AlarmDetailsComplete && standardCalls == (0, 0),
            "zero-alarm RFC1628 reading needs no additional SNMP requests");
        var rfcNames = new[]
        {
            "Battery bad", "On battery", "Battery low", "Battery depleted", "Battery temperature high",
            "Input bad", "Output bad", "Output overload", "On bypass", "Bypass bad", "Output off as requested",
            "UPS off as requested", "Charger failed", "UPS output off", "UPS system off", "Fan failure",
            "Fuse failure", "General fault", "Diagnostic test failed", "Communications lost", "Awaiting power",
            "Shutdown pending", "Shutdown imminent", "Test in progress"
        };
        for (var index = 0; index < rfcNames.Length; index++)
        {
            var fixture = new Dictionary<string, ISnmpData>(standard)
            {
                [rfc + "2.18"] = new ObjectIdentifier("1.3.6.1.2.1.33.1.6.3." + (index + 1))
            };
            var decoded = Read(fixture, vertiv: false);
            Check(decoded.AlarmDetailsComplete && decoded.CardAlarms.Single().Name == rfcNames[index] &&
                decoded.CardAlarms.Single().Active == true, "RFC1628 identity and active state: " + rfcNames[index]);
        }
        var limits = new EquipmentAlarmLimits();
        UpsSnapshot Local(Dictionary<string, double>? values = null, Dictionary<string, double?[]>? phases = null) => new()
        {
            Quality = UpsQuality.Good, AlarmDetailsComplete = true, OutputVoltageReference = "L-N",
            Values = values ?? new(), Phases = phases ?? new()
        };
        foreach (var (source, text, severity) in new[]
        {
            (2, "UPS output OFF", AlarmSeverity.Alarm), (3, "", AlarmSeverity.Normal),
            (4, "Load on bypass", AlarmSeverity.PreAlarm), (5, "UPS ON BATTERY", AlarmSeverity.Alarm)
        })
        {
            var evaluated = UpsAlarmEvaluator.Evaluate(Local(new() { ["OutputSource"] = source }), limits);
            Check(text.Length == 0 ? evaluated.Count == 0 : evaluated.Any(a => a.Text == text && a.Severity == severity),
                "local output-source evaluation: " + source);
        }
        foreach (var status in new[] { 2, 3, 4 })
        {
            var evaluated = UpsAlarmEvaluator.Evaluate(Local(new() { ["BatteryStatus"] = status }), limits);
            Check(status == 2 ? evaluated.Count == 0 : evaluated.Any(a => a.Text.StartsWith("Battery ") && a.Severity == AlarmSeverity.Alarm),
                "local battery-status evaluation: " + status);
        }
        foreach (var (value, severity) in new[]
        {
            (limits.LoadPreAlarmPercent - 0.1, AlarmSeverity.Normal), (limits.LoadPreAlarmPercent, AlarmSeverity.PreAlarm),
            (limits.LoadAlarmPercent, AlarmSeverity.Alarm), (limits.LoadShedPercent, AlarmSeverity.ShedLoad)
        })
        for (var phase = 0; phase < 3; phase++)
        {
            var loads = new double?[] { 0, 0, 0 };
            loads[phase] = value;
            Check(UpsAlarmEvaluator.Worst(UpsAlarmEvaluator.Evaluate(Local(phases: new() { ["OutputLoad"] = loads }), limits)) == severity,
                $"load threshold {value}% on phase {phase + 1}");
        }
        foreach (var (value, severity) in new[]
        {
            (limits.TemperaturePreAlarmC - 0.1, AlarmSeverity.Normal), (limits.TemperaturePreAlarmC, AlarmSeverity.PreAlarm),
            (limits.TemperatureAlarmC, AlarmSeverity.Alarm), (limits.TemperatureShedC, AlarmSeverity.ShedLoad)
        })
            Check(UpsAlarmEvaluator.Worst(UpsAlarmEvaluator.Evaluate(Local(new() { ["BatteryTemperature"] = value }), limits)) == severity,
                "battery-temperature threshold " + value);
        foreach (var voltage in new[] { limits.UnderVoltageV - 0.1, limits.UnderVoltageV, limits.OverVoltageV, limits.OverVoltageV + 0.1 })
        for (var phase = 0; phase < 3; phase++)
        {
            var voltages = new double?[] { 230, 230, 230 };
            voltages[phase] = voltage;
            var expectedSeverity = voltage < limits.UnderVoltageV || voltage > limits.OverVoltageV ? AlarmSeverity.Alarm : AlarmSeverity.Normal;
            Check(UpsAlarmEvaluator.Worst(UpsAlarmEvaluator.Evaluate(Local(phases: new() { ["OutputVoltage"] = voltages }), limits)) == expectedSeverity,
                $"output-voltage boundary {voltage} V on phase {phase + 1}");
        }
        var many = Fixture();
        many[count] = new Integer32(40);
        for (var n = 1; n <= 40; n++) many[vendor + "2." + n] = new ObjectIdentifier("1.3.6.1.4.1.476.1.42.3.2.1.43.3");
        var truncated = Read(many);
        Check(truncated.CardAlarms.Count == 32 && !truncated.AlarmDetailsComplete && truncated.AlarmDetailsStatus.Contains("truncated"),
            "large condition tables are bounded and explicitly incomplete");
        using var form = new UpsDetailForm(new UpsDevice { Id = "qa", Label = "QA" }, null!, null!);
        typeof(UpsDetailForm).GetMethod("FillAlarms", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { good });
        var grid = (DataGridView)typeof(UpsDetailForm).GetField("_alarms", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        Check(grid.Rows.Cast<DataGridViewRow>().Any(r => Equals(r.Cells[1].Value, "Multiple Fan Failure") && Equals(r.Cells[0].Value, "ACTIVE")),
            "actual alarm grid displays named active fault rather than only a count");
        typeof(UpsDetailForm).GetMethod("FillAlarms", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { conflict });
        Check(grid.Rows.Cast<DataGridViewRow>().Any(r => Equals(r.Cells[1].Value, "General fault") &&
            r.Cells[0].Value?.ToString()?.StartsWith("UNCONFIRMED") == true) &&
            !grid.Rows.Cast<DataGridViewRow>().Any(r => Equals(r.Cells[0].Value, "ACTIVE")),
            "actual alarm grid does not label B01 conflicting generic evidence ACTIVE");
        var dashboardDevice = new UpsDevice { Id = "qa", Label = "B01" };
        var dashboardSnapshots = new Dictionary<string, UpsSnapshot> { [dashboardDevice.Id] = conflict };
        var bankType = App.GetTypes().Single(t => t.Name == "AtsBankControl");
        using var bank = (Control)Activator.CreateInstance(bankType, new AtsBankDefinition { Number = 2, Name = "ATS 2" })!;
        bankType.GetMethod("SetData")!.Invoke(bank, new object[] { new[] { dashboardDevice }, dashboardSnapshots, limits });
        var card = ((IEnumerable)bankType.GetField("_cards", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(bank)!).Cast<object>().Single();
        Check((AlarmSeverity)card.GetType().GetField("Severity")!.GetValue(card)! == AlarmSeverity.PreAlarm &&
            card.GetType().GetField("AlarmText")!.GetValue(card)!.ToString()!.Contains("UNCONFIRMED"),
            "actual dashboard card is amber/unconfirmed, not a blinking red generic fault");
        var summaryType = App.GetTypes().Single(t => t.Name == "UpsSummaryStrip");
        using var summary = (Control)Activator.CreateInstance(summaryType)!;
        var dashboardSettings = new UpsSettings { Devices = new List<UpsDevice> { dashboardDevice } };
        summaryType.GetMethod("SetData")!.Invoke(summary, new object[] { dashboardSettings, dashboardSnapshots, limits });
        var alarmTile = ((IEnumerable)summaryType.GetField("_tiles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(summary)!)
            .Cast<object>().Single(tile => Equals(tile.GetType().GetField("Item1")!.GetValue(tile), "UNITS IN ALARM"));
        Check(Equals(alarmTile.GetType().GetField("Item2")!.GetValue(alarmTile), "0"),
            "unconfirmed generic evidence does not inflate the dashboard confirmed-alarm unit count");
        var installedTile = ((IEnumerable)summaryType.GetField("_tiles", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(summary)!)
            .Cast<object>().Single(tile => Equals(tile.GetType().GetField("Item1")!.GetValue(tile), "UPS NAMEPLATE TOTAL"));
        Check(Equals(installedTile.GetType().GetField("Item2")!.GetValue(installedTile), "500 kVA"),
            "fleet UPS capacity is derived from UPS unit ratings rather than ATS bus ratings");
        var defaultUpsSettings = UpsSettings.CreateDefault();
        foreach (var bankDefinition in defaultUpsSettings.Banks) bankDefinition.NameplateKva = 2500;
        Check(defaultUpsSettings.InstalledUpsCapacityKva() == 9300 &&
            defaultUpsSettings.InstalledUpsCapacityKva(1) == 2400 &&
            defaultUpsSettings.InstalledUpsCapacityKva(2) == 2400 &&
            defaultUpsSettings.InstalledUpsCapacityKva(3) == 2500 &&
            defaultUpsSettings.InstalledUpsCapacityKva(4) == 2000,
            "per-ATS and fleet UPS capacity sum the individual UPS nameplates, not four 2,500 kVA ATS ratings");
        var bankControlType = App.GetTypes().Single(t => t.Name == "AtsBankControl");
        var headerPower = (string)bankControlType.GetMethod("FormatHeaderPower", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object?[] { 490d, defaultUpsSettings.InstalledUpsCapacityKva(1) })!;
        Check(headerPower == "490.0 kW   |   2,400 kVA installed",
            "ATS header shows the running kW alongside that ATS's summed UPS nameplate capacity");
        typeof(UpsDetailForm).GetMethod("FillAlarms", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object?[] { null });
        Check(Equals(grid.Rows[0].Cells[0].Value, "NOT VERIFIED"), "no reading is never rendered as no active alarms");
        var rfcSnapshot = new UpsSnapshot { Quality = UpsQuality.Good,
            Values = new Dictionary<string, double> { ["BatteryTemperature"] = 32 },
            MeasurementSources = new Dictionary<string, string> { ["BatteryTemperature"] = "1.3.6.1.2.1.33.1.2.7.0 ×1" },
            UnsupportedOids = new[] { "BatteryCurrent 1.3.6.1.2.1.33.1.2.6.0 - NoSuchObject" } };
        typeof(UpsDetailForm).GetMethod("FillMeasurements", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { rfcSnapshot, true });
        var measurements = (DataGridView)typeof(UpsDetailForm).GetField("_measurements", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
        Check(measurements.Rows.Cast<DataGridViewRow>().Any(r => r.Cells[6].Value?.ToString()?.StartsWith("RFC1628:") == true),
            "RFC provenance is not mislabeled as an empty FLEX source");
        Check(measurements.Rows.Cast<DataGridViewRow>().Any(r => r.Cells[6].Value?.ToString()?.StartsWith("Not provided by card") == true),
            "unsupported measurements have an explicit card-unavailable explanation");
    }

    internal static void AlarmLive(string configuration, string output)
    {
        Directory.CreateDirectory(output);
        var limitsPath = Path.Combine(configuration, "alarm-settings.json");
        var alarmSettings = File.Exists(limitsPath)
            ? JsonSerializer.Deserialize<AlarmSettings>(File.ReadAllText(limitsPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Saved alarm settings document is empty.")
            : new AlarmSettings();
        alarmSettings.Validate();
        Console.WriteLine(File.Exists(limitsPath) ? "Evaluating saved UPS alarm limits (read-only local configuration)." :
            "No saved alarm limits in source configuration; evaluating application defaults.");
        var files = new[] { "ups-settings.json", "snmp-credentials.json", "web-card-credentials.json" }
            .Concat(File.Exists(limitsPath) ? new[] { "alarm-settings.json" } : Array.Empty<string>()).ToArray();
        string Hash(string name) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(configuration, name))));
        var before = files.ToDictionary(name => name, Hash);
        var snapshots = new List<UpsSnapshot>();
        foreach (var device in UpsSettingsStore.Current.Devices)
        {
            var snapshot = UpsAcquisitionService.PollDevice(device, 0, null!);
            snapshots.Add(snapshot);
            Console.WriteLine($"{device.Id}: {snapshot.Quality}; {snapshot.AlarmDetailsStatus}; " +
                string.Join("; ", snapshot.CardAlarms.Select(a => a.Name + " [" + a.StateText + "]")));
            if (!snapshot.IsGood) continue;
            Check(snapshot.AlarmDetailsComplete || snapshot.AlarmDetailsStatus.Length > 0 &&
                UpsAlarmEvaluator.Evaluate(snapshot, null!).Any(a => a.Text.Contains("incomplete") || a.Text.Contains("not verified")),
                device.Id + ": alarm details are verified or an explicit incomplete-details warning is shown");
            Check(snapshot.InvalidFields.Count == 0 && snapshot.PartialGroups.Count == 0, device.Id + ": electrical reads preserved");
            var evaluated = UpsAlarmEvaluator.Evaluate(snapshot, alarmSettings.Ups);
            Console.WriteLine(device.Id + ": evaluated severity " + UpsAlarmEvaluator.Worst(evaluated) + "; " +
                string.Join("; ", evaluated.Where(a => a.CardAlarm is null).Select(a => a.Text)));
            Check(evaluated.Where(a => a.CardAlarm?.EvidenceConflict == true).All(a => a.Severity == AlarmSeverity.PreAlarm &&
                a.CardAlarm!.Active is null && !a.CardAlarm.StateText.StartsWith("ACTIVE")),
                device.Id + ": contradictory generic evidence is never labeled an active fault");
            if (device.Id is "UPS-A01" or "UPS-A03" or "UPS-B01" or "UPS-B02" or "UPS-C01")
            {
                using var form = new UpsDetailForm(device, null!, null!);
                form.Text += " - isolated read-only SNMP capture";
                ((System.Windows.Forms.Timer)typeof(UpsDetailForm).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!).Stop();
                var tabs = form.Controls.OfType<TabControl>().Single();
                tabs.SelectedIndex = 2;
                form.Show();
                var status = (Label)typeof(UpsDetailForm).GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
                status.Text = "READ-ONLY SNMP CAPTURE " + snapshot.TimestampUtc.ToLocalTime().ToString("dd MMM HH:mm:ss") + " - no acquisition/historian instance started.";
                typeof(UpsDetailForm).GetMethod("FillAlarms", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { snapshot });
                typeof(UpsDetailForm).GetMethod("FillMeasurements", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new object[] { snapshot, true });
                Application.DoEvents();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.Combine(output, device.Id + "-alarms.png"));
            }
        }
        var after = files.ToDictionary(name => name, Hash);
        File.WriteAllText(Path.Combine(output, "alarm-live.json"), JsonSerializer.Serialize(new
        {
            TimestampUtc = DateTime.UtcNow, Before = before, After = after, SavedUpsLimits = alarmSettings.Ups, Snapshots = snapshots,
            Evaluations = snapshots.Select(snapshot => new
            {
                snapshot.DeviceId, Severity = UpsAlarmEvaluator.Worst(UpsAlarmEvaluator.Evaluate(snapshot, alarmSettings.Ups)),
                Alarms = UpsAlarmEvaluator.Evaluate(snapshot, alarmSettings.Ups)
            })
        }, JsonOptions));
        Check(files.All(name => before[name] == after[name]), "production registry and both credential stores unchanged");
        var report = (string)Call("UpsConnectionDiagnostics", "BuildCsvReport", UpsSettingsStore.Current.Devices,
            snapshots.ToDictionary(s => s.DeviceId));
        File.WriteAllText(Path.Combine(output, "alarm-summary.csv"), report);
        foreach (var snapshot in snapshots.Where(s => s.IsGood && s.CardAlarms.Count > 0))
            Check(snapshot.CardAlarms.All(a => report.Contains(a.Name)), snapshot.DeviceId + ": exported diagnostics includes actual condition names");
    }

    internal static void ParameterAudit(string configuration, string output)
    {
        Directory.CreateDirectory(output);
        var files = new[] { "ups-settings.json", "snmp-credentials.json", "web-card-credentials.json" };
        string Hash(string name) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(configuration, name))));
        var before = files.ToDictionary(name => name, Hash);
        var results = new List<object>();
        var failures = new List<string>();
        var verified = 0;
        var derived = 0;
        foreach (var device in UpsSettingsStore.Current.Devices)
        {
            var capability = (UpsCapabilityProfile)Call("UpsCapabilityProbe", "Run", device, CancellationToken.None);
            var snapshot = capability.IdentityAnswered ? UpsAcquisitionService.PollDevice(device, 0, null!) : null;
            var rows = new List<object>();
            var expectedValues = new Dictionary<string, double>();
            var expectedPhases = new Dictionary<string, double?[]>();
            var overridden = capability.MeasurementBindings.Select(b => b.Definition.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var prefix in new[] { "Input", "Bypass" })
                if (overridden.Contains(prefix + "VoltageLN") || overridden.Contains(prefix + "VoltageLL"))
                    overridden.Add(prefix + "Voltage");
            if ((bool)Call("UpsMeasurementMap", "IsNxl", capability.Model)) overridden.Add("OutputPower");
            void Verify(string key, int phase, string oid, string unit, double scale, double? expected, double? actual)
            {
                var match = expected.HasValue == actual.HasValue &&
                    (!expected.HasValue || Math.Abs(expected.Value - actual!.Value) <= 0.000001 * Math.Max(1, Math.Abs(expected.Value)));
                if (!match) failures.Add($"{device.Id} {key} phase {phase + 1}: raw/scaled value differs from snapshot.");
                if (expected.HasValue && match)
                {
                    if (oid.StartsWith("1.", StringComparison.Ordinal)) verified++;
                    else derived++;
                }
                if (expected.HasValue)
                {
                    if (phase < 0) expectedValues[key] = expected.Value;
                    else
                    {
                        if (!expectedPhases.ContainsKey(key)) expectedPhases[key] = new double?[3];
                        expectedPhases[key][phase] = expected;
                    }
                }
                rows.Add(new { Key = key, Phase = phase < 0 ? (int?)null : phase + 1, Oid = oid, Unit = unit,
                    Scale = scale, Raw = snapshot!.RawMeasurements.GetValueOrDefault(oid), Expected = expected, Actual = actual, Matches = match });
            }
            if (snapshot is not null)
            {
                if (!snapshot.IsGood || snapshot.InvalidFields.Count > 0 || snapshot.PartialGroups.Count > 0 ||
                    !capability.Complete || !capability.CoreMapped)
                    failures.Add(device.Id + ": incomplete/invalid core measurement reading: " + snapshot.QualityDetail);
                foreach (var definition in UpsOidMap.Definitions.Where(d => !UpsOidMap.IdentityKeys.Contains(d.Key) && !overridden.Contains(d.Key)))
                {
                    if (definition.Key is "InputLines" or "OutputLines" or "BypassLines")
                    {
                        var indices = capability.IndicesFor(definition.Key);
                        if (indices.Length > 0 && snapshot.Value(definition.Key) != indices.Length)
                            failures.Add(device.Id + ": verified table row count differs from snapshot: " + definition.Key);
                        continue;
                    }
                    var indicesToRead = definition.PerPhase ? capability.IndicesFor((string)Call("UpsCapabilityProbe", "LinesKeyFor", definition.Key)) : new[] { 0 };
                    for (var phase = 0; phase < indicesToRead.Length; phase++)
                    {
                        var oid = definition.Oid + (definition.PerPhase ? indicesToRead[phase].ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
                        double? expected = null;
                        if (snapshot.RawMeasurements.TryGetValue(oid, out var raw) &&
                            double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) &&
                            double.IsFinite(number) && number * definition.Scale >= definition.MinValid && number * definition.Scale <= definition.MaxValid)
                            expected = number * definition.Scale;
                        Verify(definition.Key, definition.PerPhase ? phase : -1, oid, definition.Unit, definition.Scale, expected,
                            definition.PerPhase ? snapshot.Phase(definition.Key, phase) : snapshot.Value(definition.Key));
                    }
                }
                foreach (var binding in capability.MeasurementBindings)
                {
                    var raw = snapshot.RawMeasurements.GetValueOrDefault(binding.Definition.Oid);
                    double? scaled = null;
                    if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number) &&
                        double.IsFinite(number) && number * binding.Definition.Scale >= binding.Definition.MinValid &&
                        number * binding.Definition.Scale <= binding.Definition.MaxValid &&
                        !(binding.Definition.Key == "RuntimeMinutes" && number == 65535))
                        scaled = number * binding.Definition.Scale;
                    Verify(binding.Definition.Key, binding.Phase, binding.Definition.Oid, binding.Definition.Unit,
                        binding.Definition.Scale, scaled, binding.Phase < 0 ? snapshot.Value(binding.Definition.Key) : snapshot.Phase(binding.Definition.Key, binding.Phase));
                }
                foreach (var prefix in new[] { "Input", "Bypass" })
                {
                    var key = overridden.Contains(prefix + "VoltageLN") ? prefix + "VoltageLN" :
                        overridden.Contains(prefix + "VoltageLL") ? prefix + "VoltageLL" : null;
                    if (key is not null && Enumerable.Range(0, 3).Any(p => snapshot.Phase(prefix + "Voltage", p) != snapshot.Phase(key, p)))
                        failures.Add(device.Id + ": voltage alias differs from independently mapped voltage: " + prefix);
                }
                double? Total(string key)
                {
                    if (expectedValues.TryGetValue(key, out var total)) return total / 1000;
                    if (expectedPhases.TryGetValue(key, out var phases) && phases.All(p => p.HasValue)) return phases.Sum(p => p!.Value) / 1000;
                    return null;
                }
                var kw = Total("OutputPower");
                var kva = Total("OutputApparentVa");
                Verify("SystemOutputKw", -1, "calculated from verified power objects", "kW", 1, kw, snapshot.OutputKw);
                Verify("SystemOutputKva", -1, "calculated from verified apparent-power objects", "kVA", 1, kva, snapshot.OutputKvaDerived);
                var pf = kw.HasValue && kva > 0 && kw / kva is >= 0 and <= 1 ? kw / kva : null;
                Verify("SystemOutputPf", -1, "verified system kW / kVA", "", 1, pf, snapshot.PowerFactorDerived);
                if (snapshot.PowerConsistencyIssue.Length > 0) failures.Add(device.Id + ": " + snapshot.PowerConsistencyIssue);
            }
            results.Add(new { DeviceId = device.Id, device.IpAddress, Capability = capability, Snapshot = snapshot, Readings = rows });
            Console.WriteLine($"{device.Id}: {capability.SupportedParameterCount}/{capability.RequestedParameterCount}; " +
                $"unavailable: {string.Join(", ", capability.UnavailableParameters)}; poll: {snapshot?.Quality.ToString() ?? capability.Aborted}; {rows.Count} audited objects");
        }
        var after = files.ToDictionary(name => name, Hash);
        foreach (var name in files)
            if (before[name] != after[name]) failures.Add("Production configuration changed during audit: " + name);
        File.WriteAllText(Path.Combine(output, "parameter-audit.json"), JsonSerializer.Serialize(new
        {
            TimestampUtc = DateTime.UtcNow, VerifiedCardReadings = verified, VerifiedDerivedReadings = derived, Failures = failures,
            ProductionHashesBefore = before, ProductionHashesAfter = after, Devices = results
        }, JsonOptions));
        Check(verified > 0 && failures.Count == 0, $"all {verified} returned numeric readings and {derived} derived totals/PF match raw OID values/scales; production files unchanged. " + string.Join("; ", failures));
        using var form = new Form { Size = new Size(1600, 1000), Location = new Point(-2000, -2000), StartPosition = FormStartPosition.Manual };
        using var view = new UpsSnmpSettingsView(Admin) { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        form.Show();
        var tabs = (TabControl)typeof(UpsSnmpSettingsView).GetField("_tabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        tabs.SelectedIndex = 3;
        typeof(UpsSnmpSettingsView).GetMethod("RefreshDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);
        Application.DoEvents();
        var grid = (DataGridView)typeof(UpsSnmpSettingsView).GetField("_diagnostics", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        foreach (var device in UpsSettingsStore.Current.Devices)
        {
            var capability = (UpsCapabilityProfile)Call("UpsCapabilityProbe", "CurrentEvidence", device);
            if (capability.UnavailableParameters.Count == 0) continue;
            var row = grid.Rows.Cast<DataGridViewRow>().Single(r => Equals(r.Cells[0].Value, device.Id));
            Check(capability.UnavailableParameters.All(key => row.Cells["Mapping"].ToolTipText.Contains(key)) &&
                row.Cells["Mapping"].ToolTipText.Contains("GOOD describes accepted readings"),
                device.Id + ": actual mapping tooltip names every unavailable parameter and explains GOOD");
        }
    }

    private static void Unit(string? measurementEvidence = null)
    {
        if (measurementEvidence is not null)
        {
            MeasurementUnit(measurementEvidence);
            return;
        }
        var folder = Path.GetDirectoryName(SnmpCredentialStore.StorePath())!;
        Directory.CreateDirectory(folder);
        File.WriteAllText(SnmpCredentialStore.StorePath(), "[]");
        File.WriteAllText(WebCardCredentialStore.StorePath(), "[]");
        typeof(UpsSettingsStore).GetField("_current", Static)!.SetValue(null, UpsSettings.CreateDefault());
        typeof(PostgresDatabase).GetField("_connectionString", Static)!.SetValue(null, "Host=127.0.0.1;Port=1;Username=isolated_qa;Database=isolated_qa;Timeout=1;Pooling=false");
        typeof(PostgresDatabase).GetField("_lastFailureUtc", Static)!.SetValue(null, DateTime.UtcNow);
        Save("qa-v2", "v2c", "qa-community-only");
        Check(SnmpCredentialStore.ListProfiles().Single().HasCommunity, "v2c profile created");
        Check(!File.ReadAllText(SnmpCredentialStore.StorePath()).Contains("qa-community-only"), "community is not plaintext");
        Check((string)Resolve("qa-v2").GetType().GetProperty("Community")!.GetValue(Resolve("qa-v2"))! == "qa-community-only", "DPAPI community round trip");
        Save("qa-v2", "v2c", null);
        Check(SnmpCredentialStore.ListProfiles().Single().HasCommunity, "blank update preserves community");
        Reject(() => Save("empty", "v2c", ""), "empty community rejected");
        Reject(() => Save("bad-version", "v4", "qa"), "unsupported SNMP version rejected");
        Reject(() => Save("bad-auth", "v3", null, "SHA999", "qa-auth-password"), "unsupported authentication rejected");
        Reject(() => Save("bad-combo", "v3", null, "None", null, "AES128", "qa-privacy-password"), "privacy without authentication rejected");
        Reject(() => Save("short", "v3", null, "SHA256", "short"), "short authentication password rejected");
        Reject(() => Save("offline", "v2c", "qa", session: UserSession.OfflinePreview()), "offline preview cannot edit credentials");
        Reject(() => Save("operator", "v2c", "qa", session: new UserSession { RoleName = "Operator" }), "operator cannot edit credentials");
        Save("qa-v3", "v3", null, "SHA256", "qa-auth-password", "AES128", "qa-privacy-password");
        Check(SnmpCredentialStore.ListProfiles().Single(p => p.Name == "qa-v3").SecurityMode == "AuthPriv", "AuthPriv persisted");
        Save("qa-noauth", "v3", null);
        Check(SnmpCredentialStore.ListProfiles().Single(p => p.Name == "qa-noauth").SecurityMode == "NoAuthNoPriv", "NoAuthNoPriv persisted");
        Save("qa-auth", "v3", null, "SHA1", "qa-auth-password");
        Check(SnmpCredentialStore.ListProfiles().Single(p => p.Name == "qa-auth").SecurityMode == "AuthNoPriv", "AuthNoPriv persisted");
        Save("qa-strong", "v3", null, "SHA512", "qa-auth-password", "AES192", "qa-privacy-password");
        Check(SnmpCredentialStore.ListProfiles().Single(p => p.Name == "qa-strong").IsConfigured, "supported SHA512/AES192 profile accepted");
        SnmpCredentialStore.Duplicate("qa-v3", "qa-v3-copy", Admin);
        Check((string)Resolve("qa-v3-copy").GetType().GetProperty("PrivSecret")!.GetValue(Resolve("qa-v3-copy"))! == "qa-privacy-password", "duplicate preserves secret without UI disclosure");
        Reject(() => SnmpCredentialStore.Duplicate("qa-v3", "qa-v3-copy", Admin), "duplicate cannot overwrite profile");
        Save("default", "v2c", "qa-default");
        Reject(() => Save("default", "v2c", "qa-change"), "known-good shared profile requires confirmation");
        Save("default", "v2c", "qa-approved", confirm: true);
        Check(SnmpCredentialStore.ProtectedUsers("default").Count >= 3, "three known-good devices protected");
        Reject(() => SnmpCredentialStore.Delete("default", Admin), "assigned profile deletion blocked");
        SnmpCredentialStore.Delete("qa-v3-copy", Admin);
        Check(!SnmpCredentialStore.ListProfiles().Any(p => p.Name == "qa-v3-copy"), "unused profile removable");
        var settings = UpsSettingsStore.Current.Clone();
        Check(settings.Devices.Count == 15, "all 15 registrations retained");
        settings.Validate();
        Check(settings.Devices.Single(d => d.IpAddress == "192.168.107.6").Verification != UpsVerification.Verified, ".6 remains unresolved");
        var duplicate = settings.Clone();
        duplicate.Devices[1].Label = duplicate.Devices[0].Label;
        Reject(duplicate.Validate, "duplicate labels rejected");
        duplicate = settings.Clone();
        duplicate.Devices[1].IpAddress = duplicate.Devices[0].IpAddress;
        duplicate.Devices[1].Port = 1161;
        Reject(duplicate.Validate, "same physical IP at another port rejected");
        var working = settings.Devices.Single(d => d.Id == "UPS-C01");
        working.CredentialProfile = "qa-v2";
        Reject(() => UpsSettingsStore.Save(settings, Admin.Username, Admin.RoleName, false), "known-good assignment change requires confirmation");
        settings = UpsSettingsStore.Current.Clone();
        settings.Devices[0].CredentialProfile = "qa-v2";
        settings.Devices[0].UseProfileTransport = true;
        UpsSettingsStore.Save(settings, Admin.Username, Admin.RoleName, false);
        var effective = SnmpCredentialStore.EffectiveDevice(settings.Devices[0]);
        Check(effective.Port == 1161 && effective.TimeoutMilliseconds == 800 && effective.Retries == 0, "profile timing and port reach effective device");
        Check(SnmpCredentialStore.EffectiveDevice(settings.Devices[6]).Port == 161, "legacy working transport unchanged");
        var restored = JsonSerializer.Deserialize<UpsSettings>(File.ReadAllText(UpsSettingsStore.SettingsPath()))!;
        Check(restored.Devices.Count == 15 && restored.Devices[0].UseProfileTransport, "registry assignment persisted independently of secrets");
        Check(!File.ReadAllText(UpsSettingsStore.SettingsPath()).Contains("qa-community-only"), "registry contains no community secret");
        Check(File.Exists(SnmpCredentialStore.StorePath() + ".bak"), "protected credential backup exists");
        var goodJson = File.ReadAllText(SnmpCredentialStore.StorePath());
        File.WriteAllText(SnmpCredentialStore.StorePath(), "{invalid");
        Reject(() => SnmpCredentialStore.ListProfiles(), "corrupt store fails explicitly");
        Reject(() => Save("must-not-overwrite", "v2c", "qa"), "corrupt store cannot be overwritten by save");
        File.WriteAllText(SnmpCredentialStore.StorePath(), goodJson);
        var summariesJson = JsonSerializer.Serialize(SnmpCredentialStore.ListProfiles());
        Check(!summariesJson.Contains("qa-auth-password") && !summariesJson.Contains("qa-community-only"), "UI summaries do not expose stored secrets");
        var report = (string)Call("UpsConnectionDiagnostics", "BuildCsvReport", restored.Devices, null);
        Check(report.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 16, "diagnostic matrix has 15 device rows");
        Check(report.Contains("Assigned Profile") && !report.Contains("qa-community-only"), "matrix includes assignments without secrets");
        var savedProfiles = File.ReadAllText(SnmpCredentialStore.StorePath());
        var documents = System.Text.Json.Nodes.JsonNode.Parse(savedProfiles)!.AsArray();
        documents.Single(p => p!["Name"]!.GetValue<string>() == "qa-v2")!["Community"] = "not-valid-dpapi";
        File.WriteAllText(SnmpCredentialStore.StorePath(), documents.ToJsonString());
        Reject(() => Resolve("qa-v2"), "unreadable DPAPI fails explicitly rather than becoming an empty secret");
        var badPoll = UpsAcquisitionService.PollDevice(settings.Devices[0], 0, null!);
        Check(badPoll.Quality == UpsQuality.ConfigError, "poll surfaces unreadable DPAPI as a configuration error");
        File.WriteAllText(SnmpCredentialStore.StorePath(), savedProfiles);
        var stamp = (string)Call("SnmpCredentialStore", "ConnectionStamp", SnmpCredentialStore.EffectiveDevice(settings.Devices[0]));
        var historical = new UpsSnapshot { DeviceId = settings.Devices[0].Id, TimestampUtc = DateTime.UtcNow, Quality = UpsQuality.Good, ConnectionStamp = stamp };
        Save("qa-v2", "v2c", "qa-rotated-community");
        var stale = (UpsSnapshot)typeof(UpsAcquisitionService).GetMethod("ApplyStaleness", Static)!.Invoke(null, new object[] { historical })!;
        Check(stale.Quality == UpsQuality.Stale && stale.Values.Count == 0 && stale.LastGood == historical, "password rotation retains old reading only as historical, not current");
        Check(JsonSerializer.Deserialize<UpsSettings>(File.ReadAllText(UpsSettingsStore.SettingsPath()))!.Devices.Count == 15, "password change does not corrupt registrations");
        Reject(() => Save("no-identity", "v2c", "qa", session: new UserSession { RoleName = "Administrator" }), "administrator role without session identity cannot save");
        var partial = new UpsCapabilityProfile
        {
            Checks = new[]
            {
                new UpsOidCheck { Key = "OutputCurrent", Present = true, InRange = true },
                new UpsOidCheck { Key = "OutputCurrent", Present = false },
                new UpsOidCheck { Key = "BatteryVoltage", Present = true, InRange = false },
                new UpsOidCheck { Key = "OutputFrequency", Present = true, InRange = true }
            }
        };
        Check(partial.SupportedParameterCount == 1 && partial.RequestedParameterCount == 3,
            "partial-phase and out-of-range parameters do not count as fully readable");
        Check(partial.UnavailableParameters.SequenceEqual(new[] { "BatteryVoltage", "OutputCurrent" }),
            "unavailable parameter list is distinct and includes invalid values");
        var provenance = new UpsSnapshot
        {
            RawMeasurements = new Dictionary<string, string> { ["rfc-oid"] = "500" },
            MeasurementSources = new Dictionary<string, string> { ["RfcFrequency"] = "rfc-oid ×0.1" }
        };
        Call("UpsMeasurementMap", "Apply", Array.Empty<UpsMeasurementBinding>(), new Dictionary<string, ISnmpData>(), provenance,
            new Dictionary<string, double>(), new Dictionary<string, double?[]>(), new List<string>());
        Check(provenance.RawMeasurements["rfc-oid"] == "500" && provenance.MeasurementSources.ContainsKey("RfcFrequency"),
            "vendor mapping preserves RFC raw readings and provenance");
        using var view = new UpsSnmpSettingsView(Admin);
        var tabs = (TabControl)typeof(UpsSnmpSettingsView).GetField("_tabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        Check(tabs.TabPages.Count == 4 && tabs.TabPages[1].Text == "Card Settings", "card settings integrated in existing tabs");
        using var form = new Form { Size = new Size(1400, 900), Location = new Point(-2000, -2000), StartPosition = FormStartPosition.Manual };
        form.Controls.Add(view);
        view.Dock = DockStyle.Fill;
        form.Show();
        tabs.SelectedIndex = 1;
        T Field<T>(string name) where T : Control => (T)typeof(UpsSnmpSettingsView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        Field<TabControl>("_cardTabs").SelectedIndex = 2;
        Field<ComboBox>("_profileVersion").SelectedItem = "v3";
        foreach (var mode in new[] { "NoAuthNoPriv", "AuthNoPriv", "AuthPriv" })
        {
            Field<ComboBox>("_securityMode").SelectedItem = mode;
            Application.DoEvents();
            Check(Field<TextBox>("_v3User").Visible && !Field<TextBox>("_community").Visible, mode + ": v3 username only, no community");
            Check(Field<TextBox>("_authSecret").Visible == (mode != "NoAuthNoPriv"), mode + ": authentication field visibility");
            Check(Field<TextBox>("_privSecret").Visible == (mode == "AuthPriv"), mode + ": privacy field visibility");
        }

        static void MeasurementUnit(string evidence)
        {
            var candidates = ((IEnumerable)App.GetTypes().Single(t => t.Name == "UpsMeasurementMap")
                .GetField("Candidates", Static)!.GetValue(null)!).Cast<UpsMeasurementBinding>().ToArray();
            foreach (var device in new[] { "UPS-A01", "UPS-B01", "UPS-C03", "UPS-C04", "UPS-C05" })
            {
                var folder = device.StartsWith("UPS-C") ? Path.Combine(Directory.GetParent(Path.GetFullPath(evidence))!.FullName, "ExlAudit") : evidence;
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, device + "-flexible-measurements.json")));
                var actual = document.RootElement.EnumerateArray().ToArray();
                var metadataBindings = new List<UpsMeasurementBinding>();
                var values = new Dictionary<string, ISnmpData>();
                foreach (var binding in candidates)
                {
                    var row = actual.SingleOrDefault(r => r.GetProperty("Index").GetString() == binding.Index);
                    if (row.ValueKind == JsonValueKind.Undefined || row.GetProperty("Value").GetString() == "No Support") continue;
                    var unit = row.GetProperty("Unit").GetString()!;
                    ISnmpData dataUnit = unit == "NoSuchInstance" ? new NoSuchInstance() : new OctetString(unit);
                    var matches = (bool)Call("UpsMeasurementMap", "MetadataMatches", binding,
                        new OctetString(row.GetProperty("Label").GetString()!), dataUnit);
                    Check(matches, $"{device} {binding.Label}: captured card label and unit match the mapping");
                    metadataBindings.Add(binding);
                    values[binding.Definition.Oid] = new OctetString(row.GetProperty("Value").GetString()!);
                }
                var snapshot = new UpsSnapshot { IdentModel = device.StartsWith("UPS-C") ? "EXL-S1" : "49SN800UUUFD", Quality = UpsQuality.Good };
                var scalars = new Dictionary<string, double>();
                var phases = new Dictionary<string, double?[]> { ["OutputPower"] = new double?[] { 250000, 250000, 250000 } };
                var invalid = new List<string>();
                Call("UpsMeasurementMap", "Apply", metadataBindings, values, snapshot, scalars, phases, invalid);
                snapshot.Values = scalars;
                snapshot.Phases = phases;
                Check(invalid.Count == 0, device + ": every captured mapped measurement parses and passes range validation");
                var hasPhasePower = metadataBindings.Any(b => b.Definition.Key == "OutputPower" && b.Phase >= 0);
                Check(snapshot.Phases.ContainsKey("OutputPower") == hasPhasePower, device + ": phase power is supplied only by actual supported vendor phase points");
                double RawTotal(string key) => metadataBindings.Where(b => b.Definition.Key == key)
                    .Sum(b => (double)Call("UpsMeasurementMap", "Scaled", b, values[b.Definition.Oid])) / 1000;
                Check(snapshot.OutputKw == RawTotal("OutputPower"), device + ": system kW equals card total or sum of genuine phase kW, not repeated total");
                Check(snapshot.OutputKvaDerived == RawTotal("OutputApparentVa"), device + ": system kVA equals card total or sum of genuine phase kVA");
                Check(snapshot.PowerFactorDerived is >= 0 and <= 1, device + ": system PF uses matching card totals");
                var inputReference = device.StartsWith("UPS-C") ? "L-N" : "L-L";
                Check(snapshot.OutputVoltageReference == "L-N" && snapshot.InputVoltageReference.StartsWith(inputReference) &&
                    snapshot.BypassVoltageReference.StartsWith(inputReference), device + ": voltage references are explicit and independent");
                Check(snapshot.Phase("OutputVoltage", 0) < 300 &&
                    (device.StartsWith("UPS-C") ? !snapshot.Phase("OutputVoltageLL", 0).HasValue : snapshot.Phase("OutputVoltageLL", 0) > 300),
                    device + ": separate actual L-N and L-L, unsupported voltages stay unavailable");
                Check(snapshot.Value("BatteryCurrent") is null && snapshot.Value("DcBusCurrent").HasValue, device + ": DC bus current is not mislabeled as battery current");
                Check(snapshot.Value("BatteryTemperature") is null, device + ": absent battery temperature is not fabricated from cabinet zero");
                Check(snapshot.Value("RuntimeMinutes") != snapshot.Value("BatteryDischargeHours"), device + ": estimated runtime is distinct from lifetime discharge hours");
                Check(snapshot.RawMeasurements.Count == metadataBindings.Count && snapshot.MeasurementSources.Count > 10, device + ": raw engineering values and exact OID/scale provenance retained");
                var power = metadataBindings.First(b => b.Definition.Key == "OutputPower");
                var badData = new Dictionary<string, ISnmpData>(values) { [power.Definition.Oid] = new NoSuchInstance() };
                Call("UpsMeasurementMap", "Apply", metadataBindings, badData, snapshot, scalars, phases, invalid);
                Check(!snapshot.OutputKw.HasValue, device + ": missing vendor power never falls back to invalid RFC total");
                var apparent = metadataBindings.First(b => b.Definition.Key == "OutputApparentVa");
                badData[apparent.Definition.Oid] = new OctetString("NaN");
                Call("UpsMeasurementMap", "Apply", metadataBindings, badData, snapshot, scalars, phases, invalid);
                Check(!snapshot.OutputKvaDerived.HasValue, device + ": invalid vendor VA is not silently replaced by derived VA");
            }
            var watts = candidates.Single(b => b.Definition.Key == "OutputPower" && b.Phase < 0);
            Check(!(bool)Call("UpsMeasurementMap", "MetadataMatches", watts, new OctetString("System Input Power"), new OctetString("kW")), "different measurement point is rejected");
            Check(!(bool)Call("UpsMeasurementMap", "MetadataMatches", watts, new OctetString(watts.Label), new OctetString("W")), "wrong unit cannot introduce 1000-fold scale error");
            foreach (var raw in new[] { "NaN", "Infinity", "-1", "No Support", "9999999999" })
                Check(Call("UpsMeasurementMap", "Scaled", watts, new OctetString(raw)) is null, "invalid power rejected: " + raw);
            var runtime = candidates.Single(b => b.Definition.Key == "RuntimeMinutes");
            Check(Call("UpsMeasurementMap", "Scaled", runtime, new OctetString("65535")) is null, "unavailable runtime sentinel rejected");
            Check((double)Call("UpsMeasurementMap", "Scaled", watts, new OctetString("0")) == 0, "valid zero power is retained");
            var balanced = new UpsSnapshot
            {
                OutputVoltageReference = "L-N",
                Phases = new Dictionary<string, double?[]>
                {
                    ["OutputPower"] = new double?[] { 100000, 100000, 100000 },
                    ["OutputVoltage"] = new double?[] { 230, 230, 230 },
                    ["OutputCurrent"] = new double?[] { 500, 500, 500 }
                }
            };
            Check(balanced.OutputKw == 300, "equal legitimate phase powers are still summed, never heuristically divided by three");
            Check(balanced.OutputKvaDerived == 345, "derived VA requires and uses actual L-N voltage");
            balanced.OutputVoltageReference = "reference unverified";
            Check(!balanced.OutputKvaDerived.HasValue && !balanced.PowerFactorDerived.HasValue, "unknown voltage reference cannot produce guessed VA or PF");
            balanced.Values = new Dictionary<string, double> { ["OutputPower"] = 400000, ["OutputApparentVa"] = 345000 };
            Check(balanced.OutputKw == 400 && balanced.OutputKvaDerived == 345, "authoritative system totals take precedence over phase sums");
            Check(!balanced.PowerFactorDerived.HasValue, "impossible PF above one is unavailable, not clamped to 1.00");
            Check(balanced.PowerConsistencyIssue.Contains("kW exceeds kVA"), "inconsistent card totals have an explicit diagnostic reason");
            Check(!balanced.AccountingOutputKw.HasValue && !balanced.AccountingOutputKva.HasValue && balanced.OutputKw == 400,
                "inconsistent raw totals remain inspectable but cannot enter fleet totals or energy");
            balanced.Quality = UpsQuality.Good;
            Check(UpsAlarmEvaluator.Evaluate(balanced, null!).Any(a => a.Text.Contains("kW exceeds kVA")),
                "card power inconsistency raises an operator warning without changing the UPS");
            balanced.Values = new Dictionary<string, double> { ["OutputPower"] = 300000, ["OutputApparentVa"] = 345000 };
            Check(balanced.PowerFactorDerived < 1, "physically valid system PF retained");
            Check(balanced.AccountingOutputKw == 300 && balanced.AccountingOutputKva == 345, "consistent power remains available for totals and historian accounting");
            var nxl = new UpsSnapshot { IdentModel = "49sn800uuufdprb" };
            var nxlPhases = new Dictionary<string, double?[]> { ["OutputPower"] = new double?[] { 100, 100, 100 } };
            Call("UpsMeasurementMap", "Apply", Array.Empty<UpsMeasurementBinding>(), new Dictionary<string, ISnmpData>(), nxl,
                new Dictionary<string, double>(), nxlPhases, new List<string>());
            Check(!nxlPhases.ContainsKey("OutputPower"), "NXL with no usable vendor mapping rejects repeated RFC power");
            var start = DateTime.UtcNow;
            var samples = new List<UpsTrendSample>
            {
                new() { TimestampUtc = start, OutputKw = 300, MeasurementRevision = 1 },
                new() { TimestampUtc = start.AddSeconds(60), OutputKw = 100, MeasurementRevision = 2 },
                new() { TimestampUtc = start.AddSeconds(120), OutputKw = 100, MeasurementRevision = 2 }
            };
            double covered = 0;
            var energy = UpsHistorian.IntegrateKwh(samples, 60, ref covered);
            Check(Math.Abs(energy!.Value - 100.0 / 60) < .000001 && Math.Abs(covered - 1.0 / 60) < .000001,
                "legacy power and mapping cutover are excluded without rewriting old samples");
            samples[2].TimestampUtc = start.AddMinutes(10);
            Check(!UpsHistorian.IntegrateKwh(samples, 60, ref covered).HasValue && covered == 0, "historian does not bridge missing coverage");
            var alarmSnapshot = new UpsSnapshot { Quality = UpsQuality.Good, OutputVoltageReference = "reference unverified",
                Phases = new Dictionary<string, double?[]> { ["OutputVoltage"] = new double?[] { 400, 400, 400 } } };
            var limits = new EquipmentAlarmLimits { UnderVoltageV = 200, OverVoltageV = 250 };
            var alarms = UpsAlarmEvaluator.Evaluate(alarmSnapshot, limits);
            Check(alarms.Any(a => a.Text.Contains("reference unverified")) && !alarms.Any(a => a.Text.Contains("out of range")),
                "unverified LL/LN cannot cause false L-N threshold alarms and produces explicit warning");
            alarmSnapshot.OutputVoltageReference = "L-N";
            Check(UpsAlarmEvaluator.Evaluate(alarmSnapshot, limits).Count(a => a.Text.Contains("out of range")) == 3,
                "verified L-N voltage violations still alarm on every phase");
            var credentialType = App.GetTypes().Single(t => t.Name == "SnmpCredential");
            var invalidCredential = Activator.CreateInstance(credentialType, true)!;
            credentialType.GetProperty("Version")!.SetValue(invalidCredential, "invalid-no-network-test");
            var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 1161);
            void NoPacketRequest()
            {
                try { Call("UpsSnmpClient", "GetValues", endpoint, invalidCredential, new List<string> { UpsOidMap.SysName }, 100); }
                catch (Exception ex) when (ex.GetType().Name == "UpsSnmpFailure" && ex.Message.StartsWith("Unsupported SNMP version")) { return; }
                throw new InvalidOperationException("The pacing fixture must reject the invalid version before any packet is sent.");
            }
            NoPacketRequest();
            var pacing = Stopwatch.StartNew();
            NoPacketRequest();
            Check(pacing.ElapsedMilliseconds >= 20, "successive requests to one endpoint respect the 25 ms minimum pacing (no packet sent)");
            var gates = App.GetTypes().Single(t => t.Name == "UpsSnmpClient").GetField("RequestGates", Static)!.GetValue(null)!;
            var gate = gates.GetType().GetProperty("Item")!.GetValue(gates, new object[] { endpoint.ToString() })!;
            Monitor.Enter(gate);
            try
            {
                var rejected = Task.Run(() =>
                {
                    try { Call("UpsSnmpClient", "GetValues", endpoint, invalidCredential, new List<string> { UpsOidMap.SysName }, 20); }
                    catch (Exception ex) when (ex.GetType().Name == "UpsSnmpFailure" && ex.Message.Contains("queue timeout")) { return true; }
                    return false;
                }).GetAwaiter().GetResult();
                Check(rejected, "same-endpoint overlapping request has a bounded explicit queue timeout, not an extra UDP request");
            }
            finally { Monitor.Exit(gate); }
        }
        Check(Field<TextBox>("_authSecret").UseSystemPasswordChar && Field<TextBox>("_privSecret").UseSystemPasswordChar, "passwords are masked by default");
        tabs.SelectedIndex = 0;
        var registry = Field<DataGridView>("_registry");
        registry.ClearSelection();
        registry.Rows[1].Selected = true;
        registry.Rows[2].Selected = true;
        Field<ComboBox>("_assignProfile").SelectedItem = "qa-v2";
        typeof(UpsSnmpSettingsView).GetMethod("OnAssignProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, new object?[] { null, EventArgs.Empty });
        Check(new[] { 1, 2 }.All(i => Equals(registry.Rows[i].Cells["Profile"].Value, "qa-v2") && registry.Rows[i].Cells["ProfileTransport"].Value is true), "bulk assignment applies profile and timing to selected rows");
        Check(Equals(registry.Rows[6].Cells["Profile"].Value, "default"), "bulk selection does not modify unselected working C01");
        Field<ComboBox>("_assignmentGroup").SelectedItem = "ATS 2";
        typeof(UpsSnmpSettingsView).GetMethod("OnAssignProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, new object?[] { null, EventArgs.Empty });
        Check(new[] { 3, 4, 5 }.All(i => Equals(registry.Rows[i].Cells["Profile"].Value, "qa-v2")), "ATS-group assignment updates all three bank members");
        Field<ComboBox>("_assignmentGroup").SelectedItem = "Model contains NXL";
        typeof(UpsSnmpSettingsView).GetMethod("OnAssignProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, new object?[] { null, EventArgs.Empty });
        Check(Enumerable.Range(0, 6).All(i => Equals(registry.Rows[i].Cells["Profile"].Value, "qa-v2")) && Equals(registry.Rows[6].Cells["Profile"].Value, "default"), "model grouping targets NXL without overwriting EXL assignments");
        form.Close();
        using var preview = new UpsSnmpSettingsView(UserSession.OfflinePreview());
        var show = (CheckBox)typeof(UpsSnmpSettingsView).GetField("_showSecrets", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview)!;
        Check(!show.Enabled, "offline preview cannot reveal entered passwords");
        var applicationSettings = new AppSettings();
        applicationSettings.Normalize();
        var configurationTemporary = Path.Combine(Path.GetTempPath(), "eoems-configuration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configurationTemporary);
        var configurationRecording = new RecordingSettings { ExportFolderPath = configurationTemporary };
        configurationRecording.Normalize();
        using var configurationHistorian = new MeasurementHistorian(
            Path.Combine(configurationTemporary, "history.db"),
            recordingSettings: configurationRecording, migrateLegacyDatabase: false);
        using var configurationFire = new FireConnectionService(
            new FireConnectionStore(Path.Combine(configurationTemporary, "fire-connection.json")), allowCommunication: false);
        var configurationBackupSettings = new BackupSettings();
        using var configurationBackup = new BackupRecoveryService(
            configurationHistorian, configurationBackupSettings, configurationRecording, configurationTemporary);
        using var configurationVersions = new ConfigVersionService(configurationTemporary);
        using var configuration = new ConfigurationView(applicationSettings.Plc, applicationSettings.Ui.RefreshIntervalMilliseconds,
            new PlcConnectionStatus(), Array.Empty<MeterSnapshot>(), Admin, configurationFire, configurationBackupSettings,
            configurationBackup, configurationVersions, (_, _) => Task.CompletedTask, _ => Task.CompletedTask,
            _ => Task.FromResult(new RestorePreparationResult()), () => { }, () => { });
        IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
        using var configurationForm = new Form { Size = new Size(1400, 900), Location = new Point(-2000, -2000), StartPosition = FormStartPosition.Manual };
        configurationForm.Controls.Add(configuration);
        configuration.Dock = DockStyle.Fill;
        configurationForm.Show();
        Check(!Descendants(configuration).Any(c => c is Button button && button.Text == "SNMP Setting"),
            "rebuilt Configuration does not retain the removed SNMP settings shortcut");
        configurationForm.Close();
        Reject(() => UpsConnectionRecovery.VerifyAsync(UserSession.OfflinePreview(), CancellationToken.None), "C04 recovery service rejects offline-preview sessions before network access");
        Reject(() => UpsConnectionRecovery.VerifyAsync(new UserSession { UserId = "operator", Username = "operator", RoleName = "Operator" }, CancellationToken.None), "C04 recovery service rejects operators before network access");
        Reject(() => new UpsConnectionRecoveryForm(UserSession.OfflinePreview()), "C04 recovery dialog cannot be opened by offline-preview users");
        var savedSettings = UpsSettingsStore.Current.Clone();
        typeof(UpsSettingsStore).GetField("_current", Static)!.SetValue(null, savedSettings.Clone());
        UpsSettingsStore.Current.Devices.Single(d => d.Id == "UPS-C04").IpAddress = "192.168.107.99";
        Reject(() => UpsConnectionRecovery.VerifyAsync(Admin, CancellationToken.None), "C04 recovery rejects a changed target IP before network access");
        typeof(UpsSettingsStore).GetField("_current", Static)!.SetValue(null, savedSettings.Clone());
        UpsSettingsStore.Current.Devices.Single(d => d.Id == "UPS-C03").CredentialProfile = "qa-v2";
        Reject(() => UpsConnectionRecovery.VerifyAsync(Admin, CancellationToken.None), "C04 recovery rejects changed reference assignments before network access");
        typeof(UpsSettingsStore).GetField("_current", Static)!.SetValue(null, savedSettings);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { UpsConnectionRecovery.VerifyAsync(Admin, cancelled.Token).GetAwaiter().GetResult(); throw new InvalidOperationException("Cancelled recovery ran."); }
            catch (OperationCanceledException) { Check(true, "cancelled C04 recovery makes no network requests"); }
        }
        var credentialBeforeRecovery = File.ReadAllBytes(SnmpCredentialStore.StorePath());
        typeof(UpsSettingsStore).GetField("_current", Static)!.SetValue(null, savedSettings.Clone());
        UpsSettingsStore.Current.Devices.Single(d => d.Id == "UPS-C04").CredentialProfile = "qa-v2";
        var wrongAssignment = UpsConnectionRecovery.VerifyAsync(Admin, CancellationToken.None).GetAwaiter().GetResult();
        Check(!wrongAssignment.ConnectionVerified && !wrongAssignment.ProfileAssigned && wrongAssignment.TargetTest is null, "C04 recovery never silently reassigns or tests an unexpected profile");
        Check(File.ReadAllBytes(SnmpCredentialStore.StorePath()).SequenceEqual(credentialBeforeRecovery), "blocked C04 recovery preserves the credential store byte-for-byte");
        typeof(UpsSettingsStore).GetField("_current", Static)!.SetValue(null, savedSettings);
        Save("default", "v1", "qa-approved", confirm: true);
        var wrongVersion = UpsConnectionRecovery.VerifyAsync(Admin, CancellationToken.None).GetAwaiter().GetResult();
        Check(!wrongVersion.ConnectionVerified && wrongVersion.ProfileVersion == "v1" && wrongVersion.TargetTest is null, "C04 recovery refuses to substitute a different SNMP version");
        Save("default", "v2c", "qa-approved", confirm: true);
        var credentialJson = File.ReadAllText(SnmpCredentialStore.StorePath());
        var missingDocuments = System.Text.Json.Nodes.JsonNode.Parse(credentialJson)!.AsArray();
        missingDocuments.Remove(missingDocuments.Single(p => p!["Name"]!.GetValue<string>() == "default"));
        File.WriteAllText(SnmpCredentialStore.StorePath(), missingDocuments.ToJsonString());
        var unavailable = UpsConnectionRecovery.VerifyAsync(Admin, CancellationToken.None).GetAwaiter().GetResult();
        Check(!unavailable.CredentialAvailable && !unavailable.ConnectionVerified && unavailable.Detail.Contains("protected backups"), "unavailable default produces a backup-recovery instruction without guessing");
        File.WriteAllText(SnmpCredentialStore.StorePath(), credentialJson);
        var unreadableDocuments = System.Text.Json.Nodes.JsonNode.Parse(credentialJson)!.AsArray();
        unreadableDocuments.Single(p => p!["Name"]!.GetValue<string>() == "default")!["Community"] = "AQ==";
        File.WriteAllText(SnmpCredentialStore.StorePath(), unreadableDocuments.ToJsonString());
        var unreadableStore = File.ReadAllBytes(SnmpCredentialStore.StorePath());
        var unreadableRecovery = UpsConnectionRecovery.VerifyAsync(Admin, CancellationToken.None).GetAwaiter().GetResult();
        Check(!unreadableRecovery.DpapiDecryptionSuccessful && !unreadableRecovery.ConnectionVerified && unreadableRecovery.TargetTest is null, "C04 recovery stops before network when DPAPI cannot decrypt the original profile");
        Check(File.ReadAllBytes(SnmpCredentialStore.StorePath()).SequenceEqual(unreadableStore), "C04 recovery never replaces an unreadable protected credential");
        File.WriteAllText(SnmpCredentialStore.StorePath(), credentialJson);
        var evidenceOnly = new UpsConnectionRecoveryResult
        {
            CredentialAvailable = true, DpapiDecryptionSuccessful = true, ProfileAssigned = true, ProfilePreserved = true,
            ProfileVersion = "v2c",
            TargetTest = new UpsConnectionTest { DeviceId = "UPS-C04", EngineProbe = "reply", Finding = UpsConnectionFinding.AgentAliveRequestUnanswered }
        };
        Check(!evidenceOnly.ConnectionVerified && !evidenceOnly.IdentityRead && !evidenceOnly.MeasurementsRead, "v3 engine discovery cannot be accepted as C04 recovery");
        Check(!JsonSerializer.Serialize(evidenceOnly).Contains("qa-approved"), "C04 recovery result has no community field or secret output");
        evidenceOnly.TargetTest = new UpsConnectionTest
        {
            DeviceId = "UPS-C04", Finding = UpsConnectionFinding.Connected, SnmpResponds = true, ProfileVersion = "v2c",
            SysName = "another-card", SysObjectId = "1.3.6.1.4.1.476.1.42", Manufacturer = "test", Model = "test", SupportsUpsMib = true
        };
        Check(evidenceOnly.IdentityRead && !evidenceOnly.PreviousIdentityMatches && !evidenceOnly.ConnectionVerified, "responding identity must match historical C04 sysName/sysObjectID before recovery acceptance");
        evidenceOnly.TargetTest.SysName = "UPS - 11";
        Check(evidenceOnly.PreviousIdentityMatches && !evidenceOnly.ConnectionVerified, "matching identity alone cannot substitute for real electrical reads and C01/C03 regression");
        CardCredentialUnit();
        WebConnectionUnit();
        Console.WriteLine($"PASS: {assertions} assertions. Test credentials are synthetic and were never sent to any device.");
    }

    private static void WebConnectionUnit()
    {
        var original = UpsSettingsStore.Current;
        var fixture = original.Clone();
        var device = fixture.Devices[0];
        device.IpAddress = "127.0.0.1";
        typeof(UpsSettingsStore).GetField("_current", Static)!.SetValue(null, fixture);
        try
        {
            foreach (var status in new[] { 200, 302, 401, 403, 500 })
            {
                using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
                listener.Start();
                var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                var server = Task.Run(async () =>
                {
                    using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, leaveOpen: true);
                    var lines = new List<string>();
                    string? line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))))
                        lines.Add(line);
                    var redirect = status == 302 ? "Location: http://192.0.2.1/never-follow\r\n" : "";
                    var response = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\n{redirect}Content-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response);
                    return lines;
                });
                var check = WebCardConnection.CheckAsync(device.Id, $"http://127.0.0.1:{port}/web/initialize.htm", CancellationToken.None).GetAwaiter().GetResult();
                var request = server.GetAwaiter().GetResult();
                Check(check.Responds == (status != 500) && check.Detail.Contains(status.ToString()), $"HTTP {status}: endpoint result is explicit, including login-required responses");
                Check(request[0].StartsWith("GET /web/initialize.htm ") &&
                    !request.Any(l => l.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase) || l.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)),
                    $"HTTP {status}: web check uses requested path without credentials or cookies");
            }
            var fallback = WebCardConnection.HttpAlternative(new Uri("https://127.0.0.1/web/initialize.htm"));
            Check(fallback?.AbsoluteUri == "http://127.0.0.1/web/initialize.htm", "HTTP alternative preserves registered host and path");
            Check(WebCardConnection.HttpAlternative(new Uri("https://127.0.0.1:8443/")) is null &&
                WebCardConnection.HttpAlternative(new Uri("http://127.0.0.1/")) is null, "custom ports and HTTP do not get a guessed fallback");
            Reject(() => WebCardConnection.CheckAsync(device.Id, "http://192.0.2.1/", CancellationToken.None).GetAwaiter().GetResult(),
                "web endpoint check rejects unregistered targets before network activity");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var cancellationObserved = false;
            try
            {
                WebCardConnection.CheckAsync(device.Id, "http://127.0.0.1:1/", cancelled.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                cancellationObserved = true;
            }
            Check(cancellationObserved, "web endpoint cancellation is not reported as a successful response");
            var failed = WebCardConnection.CheckAsync(device.Id, "http://127.0.0.1:1/", CancellationToken.None).GetAwaiter().GetResult();
            Check(!failed.Responds && failed.Detail.StartsWith("Web connection failed:"), "unreachable web service surfaces a connection error");
            using (var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
            {
                listener.Start();
                var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                var server = Task.Run(async () =>
                {
                    using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    await Task.Delay(TimeSpan.FromSeconds(5));
                });
                var watch = Stopwatch.StartNew();
                var timeout = WebCardConnection.CheckAsync(device.Id, $"http://127.0.0.1:{port}/", CancellationToken.None).GetAwaiter().GetResult();
                watch.Stop();
                Check(!timeout.Responds && timeout.Detail.Contains("4 seconds") && watch.Elapsed.TotalSeconds is >= 3.5 and < 6,
                    "unresponsive web service reports failure within the configured four-second timeout");
                server.GetAwaiter().GetResult();
            }
            using (var key = RSA.Create(2048))
            using (var certificate = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                "CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1)))
            using (var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
            {
                listener.Start();
                var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
                var server = Task.Run(async () =>
                {
                    using var client = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    using var tls = new System.Net.Security.SslStream(client.GetStream());
                    try
                    {
                        await tls.AuthenticateAsServerAsync(certificate).WaitAsync(TimeSpan.FromSeconds(5));
                    }
                    catch (System.Security.Authentication.AuthenticationException)
                    {
                        // The test client must reject the self-signed certificate.
                    }
                    catch (IOException)
                    {
                        // The client may close the transport after certificate rejection.
                    }
                });
                var tlsCheck = WebCardConnection.CheckAsync(device.Id, $"https://127.0.0.1:{port}/", CancellationToken.None).GetAwaiter().GetResult();
                Check(!tlsCheck.Responds && tlsCheck.RequiresBrowserReview, "untrusted HTTPS certificate is not bypassed or reported as a successful connection");
                server.GetAwaiter().GetResult();
            }
        }
        finally
        {
            typeof(UpsSettingsStore).GetField("_current", Static)!.SetValue(null, original);
        }
    }

    internal static void MeasurementAcceptance(string output)
    {
        var snapshots = JsonSerializer.Deserialize<Dictionary<string, UpsSnapshot>>(File.ReadAllText(Path.Combine(output, "measurement-polls.json")))!;
        var candidates = ((IEnumerable)App.GetTypes().Single(t => t.Name == "UpsMeasurementMap")
            .GetField("Candidates", Static)!.GetValue(null)!).Cast<UpsMeasurementBinding>().ToArray();
        var validated = new List<object>();
        foreach (var snapshot in snapshots.Values.Where(s => s.IsGood))
        {
            var checkedFields = 0;
            foreach (var binding in candidates.Where(b => snapshot.RawMeasurements.ContainsKey(b.Definition.Oid)))
            {
                var raw = snapshot.RawMeasurements[binding.Definition.Oid];
                var expected = (double?)Call("UpsMeasurementMap", "Scaled", binding, new OctetString(raw));
                var actual = binding.Phase < 0 ? snapshot.Value(binding.Definition.Key) : snapshot.Phase(binding.Definition.Key, binding.Phase);
                Check(actual == expected, $"{snapshot.DeviceId}: {binding.Label} exactly matches the card value read in this poll and its verified unit");
                checkedFields++;
            }
            if (checkedFields > 0)
            {
                var reportedBindings = candidates.Where(b => snapshot.RawMeasurements.ContainsKey(b.Definition.Oid)).ToList();
                Check(snapshot.Phases.ContainsKey("OutputPower") == reportedBindings.Any(b => b.Definition.Key == "OutputPower" && b.Phase >= 0),
                    snapshot.DeviceId + ": phase power comes only from genuine vendor phase points");
                double RawTotal(string key)
                {
                    var fields = reportedBindings.Where(b => b.Definition.Key == key).ToList();
                    var total = fields.FirstOrDefault(b => b.Phase < 0);
                    return (total is not null ? (double)Call("UpsMeasurementMap", "Scaled", total, new OctetString(snapshot.RawMeasurements[total.Definition.Oid])) :
                        fields.Sum(b => (double)Call("UpsMeasurementMap", "Scaled", b, new OctetString(snapshot.RawMeasurements[b.Definition.Oid])))) / 1000;
                }
                Check(snapshot.OutputKw == RawTotal("OutputPower") && snapshot.OutputKvaDerived == RawTotal("OutputApparentVa"),
                    snapshot.DeviceId + ": normalized system totals match card total or actual phase engineering values exactly");
            }
            else
            {
                Check(!snapshot.OutputKvaDerived.HasValue && !snapshot.PowerFactorDerived.HasValue,
                    snapshot.DeviceId + ": no assumed voltage reference or fabricated derived kVA/PF");
            }
            validated.Add(new { snapshot.DeviceId, snapshot.IdentModel, snapshot.MeasurementMap, snapshot.OutputKw,
                Kva = snapshot.OutputKvaDerived, PF = snapshot.PowerFactorDerived, snapshot.OutputVoltageReference,
                ExactRawFieldComparisons = checkedFields, PhasePowerReported = snapshot.Phases.ContainsKey("OutputPower") });
        }
        Check(new[] { "UPS-C01", "UPS-C03" }.All(id => snapshots.TryGetValue(id, out var s) && s.IsGood &&
            UpsCredentialConfiguration.HasElectricalRead(s)), "C01/C03 real identity and electrical regression passed");
        File.WriteAllText(Path.Combine(output, "measurement-acceptance.json"), JsonSerializer.Serialize(new
        {
            TimestampUtc = DateTime.UtcNow, AssertionsPassed = assertions, Devices = validated,
            C04ActualElectricalRead = snapshots.TryGetValue("UPS-C04", out var c04) && UpsCredentialConfiguration.HasElectricalRead(c04),
            Note = "Exact comparisons use the same GET response, not different devices/times or simulated successful connections. NXL uses supported system totals; EXL uses supported phase kW/kVA and explicit L-N points."
        }, JsonOptions));
        Console.WriteLine($"Real-device measurement assertions passed: {assertions}.");
    }

    internal static void MeasurementScreenshots(Assembly assembly, string output)
    {
        Directory.CreateDirectory(output);
        var store = assembly.GetTypes().Single(t => t.Name == "UpsSettingsStore");
        var settings = store.GetProperty("Current", Static)!.GetValue(null)!;
        var devices = ((IEnumerable)settings.GetType().GetProperty("Devices")!.GetValue(settings)!).Cast<object>();
        var poll = assembly.GetTypes().Single(t => t.Name == "UpsAcquisitionService").GetMethod("PollDevice", Static)!;
        var detail = assembly.GetTypes().Single(t => t.Name == "UpsDetailForm");
        var snapshots = new List<object>();
        foreach (var device in devices.Where(d => new[] { "UPS-A01", "UPS-B01", "UPS-C01", "UPS-C03", "UPS-C04" }
                     .Contains((string)d.GetType().GetProperty("Id")!.GetValue(d)!)))
        {
            var id = (string)device.GetType().GetProperty("Id")!.GetValue(device)!;
            var snapshot = poll.Invoke(null, new object?[] { device, 0, null })!;
            snapshots.Add(snapshot);
            using var form = (Form)Activator.CreateInstance(detail, device, null, null)!;
            form.Size = new Size(1400, 1000);
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-2000, -2000);
            form.Show();
            var isGood = (bool)snapshot.GetType().GetProperty("IsGood")!.GetValue(snapshot)!;
            detail.GetMethod("FillMeasurements", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new[] { snapshot, (object)isGood });
            detail.GetMethod("FillIdentity", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, new[] { snapshot, snapshot });
            var status = (Label)detail.GetField("_status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            status.Text = "CAPTURED READ-ONLY TEST • " + id + " • " + snapshot.GetType().GetProperty("TimestampUtc")!.GetValue(snapshot) +
                " UTC • " + snapshot.GetType().GetProperty("Quality")!.GetValue(snapshot) + " • No background poller/historian started";
            var grid = (DataGridView)detail.GetField("_measurements", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;
            foreach (var section in new[] { "OUTPUT", "INPUT", "BATTERY" })
            {
                var row = grid.Rows.Cast<DataGridViewRow>().First(r => (string?)r.Cells[0].Value == section);
                grid.FirstDisplayedScrollingRowIndex = row.Index;
                Application.DoEvents();
                using var image = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                image.Save(Path.Combine(output, id + "-" + section.ToLowerInvariant() + ".png"));
            }
            form.Close();
        }
        File.WriteAllText(Path.Combine(output, "captured-polls.json"), JsonSerializer.Serialize(snapshots, JsonOptions));
        Console.WriteLine("Captured real A01/B01/C01/C03/C04 measurement tables without starting polling or historian services.");
    }

    private static void CardCredentialUnit()
    {
        var registry = UpsSettingsStore.Current.Clone();
        foreach (var device in registry.Devices)
            Check(WebCardCredentialStore.Load(device.Id, Admin).ManagementUrl == $"https://{device.IpAddress}/", device.Id + ": independent registered-IP web settings available");
        var snmpBefore = File.ReadAllBytes(SnmpCredentialStore.StorePath());
        var registryBefore = File.ReadAllBytes(UpsSettingsStore.SettingsPath());
        WebCardCredentialStore.Save("UPS-C04", "https://192.168.107.16/", "qa-web-user", "qa-web-password", Admin);
        Check((bool)Call("WebCardCredentialStore", "VerifyProtection", "UPS-C04", Admin), "Web Card username/password DPAPI round trip succeeds internally");
        Check(!File.ReadAllText(WebCardCredentialStore.StorePath()).Contains("qa-web"), "Web Card username/password are encrypted at rest");
        Check(File.ReadAllBytes(SnmpCredentialStore.StorePath()).SequenceEqual(snmpBefore) &&
            File.ReadAllBytes(UpsSettingsStore.SettingsPath()).SequenceEqual(registryBefore), "saving web credentials never changes SNMP credentials or registrations");
        var login = WebCardCredentialStore.Load("UPS-C04", Admin);
        Check(login.Username == "qa-web-user" && login.HasPassword && login.GetType().GetProperty("Password") is null, "Web Card edit summary never returns the saved password");
        WebCardCredentialStore.Save("UPS-C04", login.ManagementUrl, login.Username, null!, Admin);
        Check((bool)Call("WebCardCredentialStore", "VerifyProtection", "UPS-C04", Admin), "blank web password preserves the protected saved value");
        WebCardCredentialStore.Save("UPS-C04", login.ManagementUrl, login.Username, "qa-web-replacement", Admin);
        Check(WebCardCredentialStore.Load("UPS-C04", Admin).HasPassword && File.Exists(WebCardCredentialStore.StorePath() + ".bak"), "web password replacement retains a protected backup");
        var storedWeb = JsonDocument.Parse(File.ReadAllText(WebCardCredentialStore.StorePath()));
        using (storedWeb)
            Check((string)Call("WebCardCredentialStore", "Unprotect", storedWeb.RootElement[0].GetProperty("Password").GetString()) == "qa-web-replacement", "Web Card password replacement saves the exact entered value internally");
        foreach (var url in new[] { "https://192.168.107.7/", "file:///C:/Windows/", "https://user:pass@192.168.107.16/", "https://192.168.107.16/?password=value", "https://192.168.107.16/#token" })
            Reject(() => WebCardCredentialStore.ManagementUri("UPS-C04", url), "management URL rejects other targets, non-web schemes and embedded credential/token fields");
        Reject(() => WebCardCredentialStore.Load("UPS-C04", UserSession.OfflinePreview()), "offline preview cannot read Web Card login metadata");
        Reject(() => WebCardCredentialStore.Save("UPS-C04", login.ManagementUrl, login.Username, "qa", new UserSession { Username = "operator", UserId = "operator", RoleName = "Operator" }), "operator cannot save Web Card credentials");
        Reject(() => WebCardCredentialStore.Save("unknown-device", login.ManagementUrl, login.Username, "qa", Admin), "Web Card credentials require a registered stable device ID");
        var webJson = File.ReadAllText(WebCardCredentialStore.StorePath());
        File.WriteAllText(WebCardCredentialStore.StorePath(), "{invalid");
        Reject(() => WebCardCredentialStore.Save("UPS-C04", login.ManagementUrl, login.Username, "qa", Admin), "corrupt Web Card store cannot be overwritten");
        File.WriteAllText(WebCardCredentialStore.StorePath(), webJson);
        var webEntries = System.Text.Json.Nodes.JsonNode.Parse(webJson)!.AsArray();
        webEntries[0]!["Password"] = "AQ==";
        File.WriteAllText(WebCardCredentialStore.StorePath(), webEntries.ToJsonString());
        Reject(() => WebCardCredentialStore.Load("UPS-C04", Admin), "unreadable Web Card DPAPI password fails explicitly");
        File.WriteAllText(WebCardCredentialStore.StorePath(), webJson);
        var sourceJson = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SnmpCredentialStore.StorePath()))!.AsArray()
            .Single(p => p!["Name"]!.GetValue<string>() == "default")!.ToJsonString();
        Reject(() => SnmpCredentialStore.Duplicate("default", "C04-SNMP", UserSession.OfflinePreview()), "preview cannot duplicate a credential");
        SnmpCredentialStore.Duplicate("default", "C04-SNMP", Admin);
        Check(SnmpCredentialStore.ListProfiles().Any(p => p.Name == "C04-SNMP" && p.Version == "v2c"), "generic duplication creates an independent profile without changing assignments");
        Check((string)Resolve("C04-SNMP").GetType().GetProperty("Community")!.GetValue(Resolve("C04-SNMP"))! ==
            (string)Resolve("default").GetType().GetProperty("Community")!.GetValue(Resolve("default"))!, "C04 clone retains the source community internally without revealing it");
        Reject(() => SnmpCredentialStore.Duplicate("default", "C04-SNMP", Admin), "duplication cannot overwrite an existing profile");
        Save("C04-SNMP", "v2c", "qa-c04-card-community");
        Reject(() => UpsCredentialConfiguration.AssignProfile("UPS-C04", "C04-SNMP", Admin, false), "C04 protected assignment requires explicit confirmation");
        Reject(() => UpsCredentialConfiguration.AssignProfile("UPS-C04", "C04-SNMP", UserSession.OfflinePreview(), true), "preview cannot assign credentials");
        UpsCredentialConfiguration.AssignProfile("UPS-C04", "C04-SNMP", Admin, true);
        Check(UpsSettingsStore.Current.Devices.Single(d => d.Id == "UPS-C04").CredentialProfile == "C04-SNMP", "C04-only profile assignment is saved");
        Check(registry.Devices.Where(d => d.Id != "UPS-C04").All(d =>
            JsonSerializer.Serialize(d) == JsonSerializer.Serialize(UpsSettingsStore.Current.Devices.Single(current => current.Id == d.Id))), "C04 assignment leaves all other fourteen device configurations unchanged");
        Check(sourceJson == System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SnmpCredentialStore.StorePath()))!.AsArray()
            .Single(p => p!["Name"]!.GetValue<string>() == "default")!.ToJsonString(), "C04 community replacement never changes default ciphertext or profile metadata");
        var c04Assigned = UpsSettingsStore.Current.Clone();
        foreach (var device in c04Assigned.Devices)
        {
            var beforeAssignment = UpsSettingsStore.Current.Clone();
            var profileName = "qa-" + device.Id;
            Save(profileName, "v2c", "qa-independent-community");
            UpsCredentialConfiguration.AssignProfile(device.Id, profileName, Admin, true);
            Check(UpsSettingsStore.Current.Devices.Single(d => d.Id == device.Id).CredentialProfile == profileName &&
                beforeAssignment.Devices.Where(d => d.Id != device.Id).All(d => JsonSerializer.Serialize(d) ==
                    JsonSerializer.Serialize(UpsSettingsStore.Current.Devices.Single(current => current.Id == d.Id))), device.Id + ": independent profile assignment preserves all other registrations");
        }
        UpsSettingsStore.Save(c04Assigned, Admin.Username, Admin.RoleName, false, true);
        var shared = c04Assigned.Clone();
        shared.Devices[0].CredentialProfile = "qa-auth";
        shared.Devices[1].CredentialProfile = "qa-auth";
        typeof(UpsSettingsStore).GetField("_current", Static)!.SetValue(null, shared);
        Check(SnmpCredentialStore.ConfirmationUsers("qa-auth").Count == 2, "shared-profile confirmation lists even non-working assigned devices");
        Reject(() => Save("qa-auth", "v3", null, "SHA1", "qa-auth-password"), "shared profile change cannot bypass explicit administrator confirmation");
        typeof(UpsSettingsStore).GetField("_current", Static)!.SetValue(null, c04Assigned);
        Check(!UpsCredentialConfiguration.HasElectricalRead(new UpsSnapshot { Quality = UpsQuality.Good }), "Good status without actual electrical values cannot satisfy connection acceptance");
        using var form = new Form { Size = new Size(1400, 900), Location = new Point(-2000, -2000), StartPosition = FormStartPosition.Manual };
        using var view = new UpsSnmpSettingsView(Admin) { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        form.Show();
        T Field<T>(string name) where T : Control => (T)typeof(UpsSnmpSettingsView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        var tabs = Field<TabControl>("_tabs");
        tabs.SelectedIndex = 1;
        Check(Field<ComboBox>("_credentialDevice").Items.Count == 15, "credential page selector includes all fifteen stable registrations");
        Field<ComboBox>("_credentialDevice").SelectedItem = UpsSettingsStore.Current.Devices.Single(d => d.Id == "UPS-C04");
        Check(Field<TextBox>("_profileName").Text == "C04-SNMP" &&
            Field<ComboBox>("_profileVersion").SelectedItem?.ToString() == "v2c", "selecting a card automatically loads its assigned saved profile");
        Check(Field<TextBox>("_webUser").Text == "qa-web-user" &&
            Field<TextBox>("_webPassword").PlaceholderText.Contains("Saved password") &&
            Field<TextBox>("_community").PlaceholderText.Contains("Saved community"), "saved web metadata and secret-presence hints are visible without revealing secrets");
        Check(Field<TextBox>("_webPassword").Text.Length == 0 && Field<TextBox>("_community").Text.Length == 0 &&
            Field<TextBox>("_confirmCommunity").Text.Length == 0, "selecting saved C04 credentials never populates password or community fields");
        var webStoreBeforeProtocol = File.ReadAllBytes(WebCardCredentialStore.StorePath());
        Field<ComboBox>("_webProtocol").SelectedItem = "HTTP";
        Check(Field<TextBox>("_webUrl").Text == "http://192.168.107.16/" &&
            File.ReadAllBytes(WebCardCredentialStore.StorePath()).SequenceEqual(webStoreBeforeProtocol),
            "explicit HTTP selection updates only the editor, not the saved web login");
        Field<ComboBox>("_webProtocol").SelectedItem = "HTTPS";
        Check(Field<TextBox>("_webUrl").Text == "https://192.168.107.16/", "HTTPS remains available without disabling certificate validation");
        var cardTabs = Field<TabControl>("_cardTabs");
        Check(cardTabs.TabPages.Cast<TabPage>().Select(p => p.Text).SequenceEqual(new[] { "Overview", "Web Login", "SNMP Profile", "Profile Library" }),
            "card workspace separates overview, web login, SNMP editing and profile management");
        cardTabs.SelectedIndex = 0;
        Check(!Field<TextBox>("_webUser").Visible && !Field<TextBox>("_community").Visible &&
            !Field<DataGridView>("_profiles").Visible && Field<Label>("_cardWebSummary").Visible,
            "overview shows saved-card summary without stacking editors or a profile table");
        cardTabs.SelectedIndex = 2;
        Field<CheckBox>("_showSecrets").Checked = true;
        Field<CheckBox>("_showWebPassword").Checked = true;
        Check(!Field<TextBox>("_community").UseSystemPasswordChar && !Field<TextBox>("_webPassword").UseSystemPasswordChar, "administrator reveal applies only to newly entered secret fields");
        var revealTimer = (System.Windows.Forms.Timer)typeof(UpsSnmpSettingsView).GetField("_revealTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        typeof(System.Windows.Forms.Timer).GetMethod("OnTick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(revealTimer, new object[] { EventArgs.Empty });
        Check(revealTimer.Interval == 15000 && Field<TextBox>("_community").UseSystemPasswordChar &&
            Field<TextBox>("_webPassword").UseSystemPasswordChar && Field<TextBox>("_confirmCommunity").UseSystemPasswordChar, "15-second reveal timeout remasks all newly entered secret controls");
        Field<TextBox>("_community").Text = "qa-ui-community";
        Field<TextBox>("_confirmCommunity").Text = "qa-mismatch";
        var beforeMismatch = File.ReadAllBytes(SnmpCredentialStore.StorePath());
        typeof(UpsSnmpSettingsView).GetMethod("OnSaveProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, new object?[] { null, EventArgs.Empty });
        Check(File.ReadAllBytes(SnmpCredentialStore.StorePath()).SequenceEqual(beforeMismatch), "mismatched community confirmation blocks save before any credential write");
        var unassignedEditorFixture = c04Assigned.Clone();
        unassignedEditorFixture.Devices.Single(d => d.Id == "UPS-C04").CredentialProfile = "default";
        typeof(UpsSettingsStore).GetField("_current", Static)!.SetValue(null, unassignedEditorFixture);
        Field<TextBox>("_confirmCommunity").Text = "qa-ui-community";
        typeof(UpsSnmpSettingsView).GetMethod("OnSaveProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, new object?[] { null, EventArgs.Empty });
        Application.DoEvents();
        Check(Field<TextBox>("_community").Text.Length == 0 && Field<TextBox>("_confirmCommunity").Text.Length == 0 &&
            Field<TextBox>("_profileName").Text == "C04-SNMP", "successful UI save clears both secret fields and preserves the selected profile after refresh");
        typeof(UpsSettingsStore).GetField("_current", Static)!.SetValue(null, c04Assigned);
        IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
        form.Size = new Size(1100, 768);
        Application.DoEvents();
        var actions = (FlowLayoutPanel)Descendants(view).OfType<Button>().Single(button => button.Text == "Save Profile").Parent!;
        Check(actions.WrapContents && actions.Controls.Cast<Control>().All(button => button.Right <= actions.ClientSize.Width && button.Bottom <= actions.ClientSize.Height), "compact-window credential action bar wraps without clipped buttons");
        Check(!Descendants(view).OfType<Button>().Any(b => b.Text.Contains("Clone default") || b.Text == "Edit Profile" || b.Text == "Web Login Test: Manual"),
            "redundant actions and the C04-only clone workflow are removed");
        var picker = Field<ComboBox>("_credentialDevice");
        foreach (var device in UpsSettingsStore.Current.Devices)
        {
            picker.SelectedItem = device;
            var profile = SnmpCredentialStore.ListProfiles().Single(p => p.Name == device.CredentialProfile);
            Check(Field<TextBox>("_profileName").Text == profile.Name &&
                Field<ComboBox>("_profileVersion").SelectedItem?.ToString() == profile.Version &&
                Field<NumericUpDown>("_profilePort").Value == profile.Port &&
                Field<NumericUpDown>("_profileTimeout").Value == profile.TimeoutMilliseconds &&
                Field<NumericUpDown>("_profileRetries").Value == profile.Retries,
                device.Id + ": selecting the card loads its own saved profile and transport");
            Check(Field<TextBox>("_webPassword").Text.Length == 0 && Field<TextBox>("_community").Text.Length == 0 &&
                Field<TextBox>("_authSecret").Text.Length == 0 && Field<TextBox>("_privSecret").Text.Length == 0,
                device.Id + ": switching cards clears entered secrets");
        }
        foreach (var size in new[] { new Size(1600, 1000), new Size(1100, 768), new Size(900, 700) })
        {
            form.Size = size;
            for (var index = 0; index < tabs.TabCount; index++)
            {
                tabs.SelectedIndex = index;
                Application.DoEvents();
                var bars = Descendants(tabs.SelectedTab!).OfType<FlowLayoutPanel>().Where(bar => bar.Visible);
                Check(bars.All(bar => bar.Controls.Cast<Control>().Where(c => c.Visible).All(c =>
                    c.Right <= bar.ClientSize.Width && c.Bottom <= bar.ClientSize.Height)),
                    $"{size.Width}x{size.Height} {tabs.SelectedTab!.Text}: toolbars wrap without clipped controls");
                Check(Descendants(tabs.SelectedTab!).OfType<DataGridView>().Where(g => g.Visible).All(g =>
                    g.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).All(c => c.Width >= c.MinimumWidth)),
                    $"{size.Width}x{size.Height} {tabs.SelectedTab!.Text}: table columns retain readable widths");
                if (index == 1)
                {
                    for (var cardIndex = 0; cardIndex < cardTabs.TabCount; cardIndex++)
                    {
                        cardTabs.SelectedIndex = cardIndex;
                        Application.DoEvents();
                        var workspace = cardTabs.SelectedTab!;
                        Check(Descendants(workspace).OfType<FlowLayoutPanel>().Where(bar => bar.Visible).All(bar =>
                            bar.Controls.Cast<Control>().Where(c => c.Visible).All(c =>
                                c.Right <= bar.ClientSize.Width && c.Bottom <= bar.ClientSize.Height)),
                            $"{size.Width}x{size.Height} {workspace.Text}: task actions are not clipped");
                        if (cardIndex is 0 or 1)
                            Check(!workspace.HorizontalScroll.Visible && !workspace.VerticalScroll.Visible,
                                $"{size.Width}x{size.Height} {workspace.Text}: primary card tasks fit without scrolling" +
                                (workspace.HorizontalScroll.Visible || workspace.VerticalScroll.Visible
                                    ? " " + string.Join("; ", Descendants(workspace).OfType<TableLayoutPanel>().Select(p =>
                                        $"{p.Bounds} preferred={p.GetPreferredSize(new Size(p.Width, 0))} rows={string.Join(",", p.GetRowHeights())}"))
                                    : ""));
                    }
                }
            }
        }
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(WebCardCredentialStore.StorePath())!, "*.jsonl")
            .Any(path => new[] { "qa-web-password", "qa-web-replacement", "qa-c04-card-community", "qa-ui-community" }.Any(secret => File.ReadAllText(path).Contains(secret))), "credential audit trails contain no synthetic web or community secrets");
        form.Close();
    }

    private static void ProfileUiLive(string output)
    {
        Directory.CreateDirectory(output);
        using var form = new Form { Size = new Size(1400, 1000), Text = "EOEMS isolated authorized profile-button QA",
            StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000) };
        using var view = new UpsSnmpSettingsView(Admin) { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        form.Show();
        T Field<T>(string name) => (T)typeof(UpsSnmpSettingsView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        Field<TabControl>("_tabs").SelectedIndex = 1;
        var records = new List<object>();
        foreach (var id in new[] { "UPS-C01", "UPS-C03", "UPS-C04" })
        {
            Field<ComboBox>("_credentialDevice").SelectedItem = UpsSettingsStore.Current.Devices.Single(d => d.Id == id);
            var authorized = false;
            using var approval = new System.Windows.Forms.Timer { Interval = 100 };
            approval.Tick += (_, _) =>
            {
                var popup = FindWindow("#32770", "Authorize profile test");
                GetWindowThreadProcessId(popup, out var processId);
                if (popup == IntPtr.Zero || processId != (uint)Environment.ProcessId) return;
                authorized = true;
                approval.Stop();
                SendMessage(popup, 0x0111, new IntPtr(1), IntPtr.Zero);
            };
            approval.Start();
            var watch = Stopwatch.StartNew();
            var ticks = 0;
            var lastTick = 0.0;
            var maximumGap = 0.0;
            using var responsiveness = new System.Windows.Forms.Timer { Interval = 50 };
            responsiveness.Tick += (_, _) =>
            {
                var elapsed = watch.Elapsed.TotalMilliseconds;
                maximumGap = Math.Max(maximumGap, elapsed - lastTick);
                lastTick = elapsed;
                ticks++;
            };
            responsiveness.Start();
            typeof(UpsSnmpSettingsView).GetMethod("OnTestProfile", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, new object?[] { null, EventArgs.Empty });
            while (Field<CancellationTokenSource?>("_testCancellation") is not null)
            {
                Application.DoEvents();
                Thread.Sleep(5);
                if (watch.Elapsed > TimeSpan.FromMinutes(1)) throw new TimeoutException("Profile button test did not finish.");
            }
            responsiveness.Stop();
            if (!authorized) throw new InvalidOperationException("The isolated profile test was not authorized.");
            var status = Field<Label>("_credentialMessage").Text;
            var accepted = status.Contains("Read-only connection verified.", StringComparison.Ordinal);
            if (id != "UPS-C04" && !accepted) throw new InvalidOperationException(id + " profile-button regression failed.");
            if (ticks == 0 || maximumGap > 500) throw new InvalidOperationException("Profile test UI exceeded its 500 ms responsiveness threshold.");
            var test = (UpsConnectionTest)Call("UpsConnectionDiagnostics", "LastTest", id);
            if (!test.IsConnected && status.Contains("identity does not match", StringComparison.Ordinal))
                throw new InvalidOperationException("A missing identity response was mislabeled as an identity mismatch.");
            records.Add(new { DeviceId = id, TimestampUtc = DateTime.UtcNow, ConnectionAccepted = accepted, Status = status,
                ElapsedSeconds = watch.Elapsed.TotalSeconds, UiTimerTicks = ticks, UiMaximumTimerGapMilliseconds = maximumGap });
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(output, id + "-profile-button.png"));
            Console.WriteLine($"{id}: actual Credentials Test SNMP / v3 action; read-only identity/mapping/electrical acceptance: {accepted}.");
        }
        File.WriteAllText(Path.Combine(output, "profile-button-results.json"), JsonSerializer.Serialize(records, JsonOptions));
        form.Close();
    }

    internal static void Live(string output)
    {
        Directory.CreateDirectory(output);
        var settings = UpsSettingsStore.Current;
        var snapshots = new Dictionary<string, UpsSnapshot>();
        var results = new List<object>();
        var watch = Stopwatch.StartNew();
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        using var form = new Form { Size = new Size(1600, 1000), Text = "EOEMS live read-only responsiveness test", Location = new Point(-2000, -2000), StartPosition = FormStartPosition.Manual };
        using var view = new UpsSnmpSettingsView(Admin) { Dock = DockStyle.Fill };
        form.Controls.Add(view);
        form.Show();
        var tabs = (TabControl)typeof(UpsSnmpSettingsView).GetField("_tabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        tabs.SelectedIndex = 3;
        var ticks = 0;
        var lastTick = watch.Elapsed.TotalMilliseconds;
        var maxGap = 0.0;
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) =>
        {
            var now = watch.Elapsed.TotalMilliseconds;
            maxGap = Math.Max(maxGap, now - lastTick);
            lastTick = now;
            ticks++;
        };
        timer.Start();
        typeof(UpsSnmpSettingsView).GetMethod("RunConnectionTests", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, new object[] { settings.Devices });
        var cancellation = typeof(UpsSnmpSettingsView).GetField("_testCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        while (cancellation.GetValue(view) is not null)
        {
            Application.DoEvents();
            Thread.Sleep(5);
            if (watch.Elapsed > TimeSpan.FromMinutes(10)) throw new TimeoutException("Live diagnostic UI did not finish within ten minutes.");
        }
        foreach (var device in settings.Devices)
        {
            var test = Call("UpsConnectionDiagnostics", "LastTest", device.Id);
            if (test is null) throw new InvalidOperationException("Missing actual test result for " + device.Id);
            results.Add(test);
            var responds = (bool)test.GetType().GetProperty("SnmpResponds")!.GetValue(test)!;
            if (responds)
            {
                var poll = Task.Run(() => UpsAcquisitionService.PollDevice(device, 0, null!));
                while (!poll.IsCompleted) { Application.DoEvents(); Thread.Sleep(5); }
                snapshots[device.Id] = poll.GetAwaiter().GetResult();
            }
            Console.WriteLine($"{device.DisplayName} {device.IpAddress} | {test.GetType().GetProperty("Finding")!.GetValue(test)} | valid measurement poll: {snapshots.GetValueOrDefault(device.Id)?.Quality.ToString() ?? "not run (no SNMP response)"}");
        }
        timer.Stop();
        form.Close();
        File.WriteAllText(Path.Combine(output, "device-tests.json"), JsonSerializer.Serialize(results, JsonOptions));
        File.WriteAllText(Path.Combine(output, "measurement-polls.json"), JsonSerializer.Serialize(snapshots, JsonOptions));
        File.WriteAllText(Path.Combine(output, "15-UPS-test-matrix.md"), (string)Call("UpsConnectionDiagnostics", "BuildMarkdownReport", settings.Devices, snapshots));
        File.WriteAllText(Path.Combine(output, "15-UPS-test-matrix.csv"), (string)Call("UpsConnectionDiagnostics", "BuildCsvReport", settings.Devices, snapshots));
        File.WriteAllText(Path.Combine(output, "run-metrics.json"), JsonSerializer.Serialize(new
        {
            StartedUtc = DateTime.UtcNow - watch.Elapsed, FinishedUtc = DateTime.UtcNow, ElapsedSeconds = watch.Elapsed.TotalSeconds,
            CpuSeconds = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalSeconds,
            PeakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            Requests = "Actual WinForms Test All action; at most min(4, configured limit) tests in parallel; registered IPs and assigned protected profiles only; GET/GETNEXT and engine discovery; no SET, no guessed credentials",
            PollsGood = snapshots.Count(p => p.Value.IsGood), DevicesTested = results.Count,
            UiTimerTicks = ticks, UiMaximumTimerGapMilliseconds = maxGap, UiResponsivenessThresholdMilliseconds = 500,
            UiResponsivenessPassed = ticks > 10 && maxGap < 500
        }, JsonOptions));
        Screenshots(App, Path.Combine(output, "Screenshots"), "after");
        if (ticks <= 10 || maxGap >= 500) throw new InvalidOperationException($"Live UI responsiveness threshold failed: {ticks} ticks, max gap {maxGap:0} ms.");
        Console.WriteLine($"Actual device tests completed: {results.Count}; valid measurement polls: {snapshots.Count(p => p.Value.IsGood)}.");
    }
}
