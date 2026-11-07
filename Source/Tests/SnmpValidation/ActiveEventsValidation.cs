using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;
using Microsoft.VisualBasic.FileIO;

internal static class ActiveEventsValidation
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int assertions;

    internal static void Run(string directory, string? evidence)
    {
        var now = new DateTime(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc);
        using var outbox = new DurableRecordingOutbox(Path.Combine(directory, "active-events-outbox.db"));
        var recorder = new EquipmentAlarmRecorder(outbox, () => now) { CommunicationDebounce = TimeSpan.Zero, ValueDebounce = TimeSpan.Zero, CardClearDebounce = TimeSpan.Zero };
        var assets = new[] { "T1", "T2", "T3", "T4", "TOTAL", "UPS-ACTIVE-QA" }.Select(id => new ActiveAlarmAsset {
            EquipmentType = id.StartsWith("UPS") ? "UPS" : "Transformer", Id = id, Name = id, FreshnessSeconds = 15
        }).ToArray();
        ActiveAlarmService Service(EquipmentAlarmRecorder source) => new(source, () => assets);
        var service = Service(recorder);
        Check(service.Read(now).Rows.Count == 6 && service.Read(now).Rows.All(row => row.State == "Unknown"),
            "initial missing observations are unavailable, never normal or fabricated alarms");
        Check(service.Read(now).Coverage.Contains("Fire alarm: NOT CONNECTED") && service.Read(now).Coverage.Contains("Generators: NOT CONNECTED"),
            "fire and generator coverage honestly reports absence of acquisition");
        var limits = new AlarmSettings();
        void Transformers(bool off, float voltage = 230)
        {
            var values = new float[16];
            for (var phase = 0; phase < 8; phase++) values[phase] = voltage;
            values[8] = 50;
            values[10] = 20;
            values[15] = 25;
            foreach (var asset in assets.Where(asset => asset.EquipmentType == "Transformer"))
            {
                var state = EquipmentAlarmRecorder.TransformerEvidence(values, "Good", limits.Transformers, 1000,
                    powerState: off ? TransformerPowerState.Off : TransformerPowerState.On, ecgOff: off);
                if (asset.Id == "TOTAL") state.Conditions.Remove("transformer-off");
                recorder.Observe(asset.EquipmentType, asset.Id, now, state);
            }
        }
        void Ups(int source = 3, bool fan = false, bool complete = true, UpsQuality quality = UpsQuality.Good, DateTime? acquired = null)
        {
            var snapshot = new UpsSnapshot {
                DeviceId = "UPS-ACTIVE-QA", TimestampUtc = acquired ?? now, Quality = quality,
                OutputVoltageReference = "L-N", AlarmDetailsComplete = complete, AlarmDetailsStatus = "Synthetic active-event QA",
                Values = new Dictionary<string, double> { ["OutputSource"] = source, ["BatteryStatus"] = 2, ["AlarmsPresent"] = fan ? 1 : 0 },
                Phases = new Dictionary<string, double?[]> { ["OutputLoad"] = new double?[] { 10, 20, 30 },
                    ["OutputVoltage"] = new double?[] { 230, 230, 230 } },
                CardAlarms = fan ? new[] { new UpsCardAlarm { Id = "fan1", Source = "QA", DescriptionOid = "1.2.3",
                    Name = "Multiple Fan Failure", CardType = "Fault", CardSeverity = "Major", Active = true } } : Array.Empty<UpsCardAlarm>()
            };
            recorder.Observe("UPS", snapshot.DeviceId, snapshot.TimestampUtc, EquipmentAlarmRecorder.UpsEvidence(snapshot, limits.Ups));
        }
        Transformers(false);
        Ups();
        Check(service.Read(now).Rows.Count == 0 && service.Read(now).FreshAssets == 6, "known healthy connected sources remove monitoring-unavailable rows");
        now = now.AddSeconds(1);
        Transformers(true, 0);
        var off = service.Read(now);
        Check(off.Rows.Count(row => row.ConditionKey == "ecg-off") == 1, "one global ECG off condition, not five repeated alarms");
        Check(off.Rows.Count(row => row.ConditionKey == "transformer-off") == 4, "each independently confirmed transformer off remains visible");
        Check(off.Rows.Count(row => row.ConditionKey.StartsWith("under-voltage:")) == 15 &&
            off.Rows.All(row => !row.ConditionKey.StartsWith("voltage:")), "actual under-voltage phase conditions retained without legacy duplicates");
        Check(off.Rows.Where(row => row.ConditionKey.EndsWith("-off")).All(row => row.Category == "Fault"), "power-off conditions are classified Fault");
        now = now.AddSeconds(1);
        Transformers(false, 300);
        Check(service.Read(now).Rows.Count(row => row.ConditionKey.StartsWith("over-voltage:")) == 15 &&
            service.Read(now).Rows.All(row => row.ConditionKey != "ecg-off" && row.ConditionKey != "transformer-off"),
            "confirmed power restoration removes off conditions and current over-voltage stays visible");
        Transformers(false);
        Ups(5, true);
        var raised = service.Read(now);
        Check(raised.Rows.Any(row => row.ConditionKey == "on-battery" && row.State == "Active" && row.Category == "Alarm"),
            "UPS on battery uses actual output source and clear Alarm classification");
        Check(raised.Rows.Any(row => row.Condition == "Multiple Fan Failure" && row.Category == "Fault"), "actual named fan fault appears");
        Check(raised.Rows.All(row => row.ConditionKey != "reported-alarms"), "complete named card condition suppresses redundant aggregate count");
        var queued = outbox.PendingCount;
        var savedJson = outbox.ReadAlarmState("UPS:UPS-ACTIVE-QA");
        for (var index = 0; index < 12; index++) { now = now.AddSeconds(1); Ups(5, true); }
        Check(outbox.PendingCount == queued && outbox.ReadAlarmState("UPS:UPS-ACTIVE-QA") == savedJson,
            "fresh live updates do not write per-poll durable state or per-poll history");
        Check(recorder.ReadCurrentState("UPS", "UPS-ACTIVE-QA").State.ObservedUtc == now &&
            service.Read(now).Rows.Count(row => row.EquipmentType == "UPS" && row.State == "Active") == 2,
            "unchanged samples update live freshness before five-minute reminders");
        var detached = recorder.ReadCurrentState("UPS", "UPS-ACTIVE-QA");
        detached.State.Evidence.Conditions.Clear();
        Check(recorder.ReadCurrentState("UPS", "UPS-ACTIVE-QA").State.Evidence.Conditions.Count > 0, "UI snapshot cannot mutate recorder state");
        now = now.AddSeconds(1);
        Ups(4, true);
        Check(service.Read(now).Rows.Any(row => row.ConditionKey == "on-bypass" && row.State == "Active") &&
            service.Read(now).Rows.All(row => row.ConditionKey != "on-battery"), "battery-to-bypass automatically clears battery and raises bypass");
        now = now.AddSeconds(1);
        Ups(4, false, false);
        Check(service.Read(now).Rows.Any(row => row.Condition == "Multiple Fan Failure" && row.State == "Unknown"),
            "missing fan entry in incomplete table becomes unknown, not cleared");
        now = now.AddSeconds(1);
        Ups(4, false, false);
        Check(service.Read(now).Rows.Any(row => row.Condition == "Multiple Fan Failure" && row.State == "Unknown"),
            "repeated incomplete observations retain last known active state");
        var resumed = new EquipmentAlarmRecorder(outbox, () => now) { CommunicationDebounce = TimeSpan.Zero, ValueDebounce = TimeSpan.Zero, CardClearDebounce = TimeSpan.Zero };
        var restart = Service(resumed).Read(now);
        Check(restart.Rows.All(row => row.State == "Unknown") && restart.Rows.Any(row => row.Condition == "Multiple Fan Failure"),
            "durable unresolved fan survives restart as unknown, never active or cleared");
        now = now.AddSeconds(1);
        Ups(4, false, true);
        Check(service.Read(now).Rows.All(row => row.Condition != "Multiple Fan Failure"), "only complete card absence clears the fan condition");
        now = now.AddSeconds(1);
        Ups(4, true);
        Check(service.Read(now).Rows.Any(row => row.Condition == "Multiple Fan Failure" && row.State == "Active"), "fan reappearance immediately returns to active");
        now = now.AddSeconds(1);
        Ups(quality: UpsQuality.Timeout, complete: false);
        var lost = service.Read(now);
        Check(lost.Rows.Any(row => row.ConditionKey == "communication" && row.State == "Active") &&
            lost.Rows.Any(row => row.ConditionKey == "on-bypass" && row.State == "Unknown") &&
            lost.Rows.Any(row => row.Condition == "Multiple Fan Failure" && row.State == "Unknown"),
            "communication loss raises communication fault and preserves previous active conditions as unknown");
        now = now.AddSeconds(1);
        Ups();
        Transformers(false);
        Check(service.Read(now).Rows.Count == 0, "fresh confirmed recovery automatically clears all corresponding conditions");
        now = now.AddSeconds(1);
        Ups(5, true, acquired: now.AddMinutes(-2));
        Check(service.Read(now).Rows.All(row => row.State == "Unknown") && service.Read(now).Rows.Any(row => row.ConditionKey == "__monitoring"),
            "delayed stale acquisition cannot appear fresh merely because callback was received now");
        Ups(5, true);
        var countBeforeRead = outbox.PendingCount;
        Check(service.Read(now.AddSeconds(16)).Rows.All(row => row.State == "Unknown") && outbox.PendingCount == countBeforeRead,
            "staleness marks previous active conditions unknown without fabricating persisted alarm events");
        assets[^1].Enabled = false;
        Check(service.Read(now).Rows.Where(row => row.EquipmentType == "UPS").All(row => row.State == "Unknown") &&
            service.Read(now).ExpectedAssets == 5, "disabled acquisition never presents retained conditions as active");
        assets[^1].Enabled = true;
        var reloaded = Service(new EquipmentAlarmRecorder(outbox, () => now) { CommunicationDebounce = TimeSpan.Zero, ValueDebounce = TimeSpan.Zero, CardClearDebounce = TimeSpan.Zero }).Read(now);
        Check(reloaded.Rows.All(row => row.State == "Unknown") && reloaded.Rows.Any(row => row.ConditionKey == "on-battery"),
            "restart before fresh poll marks previous durable active condition unknown");
        var snapshot = service.Read(now);
        UserInterface(snapshot, directory, evidence);
        Check(outbox.PendingCount == countBeforeRead, "reading/filtering/exporting live alarms cannot add history writes");
        var all = snapshot.Rows.ToArray();
        var csv = Path.Combine(directory, "active-snapshot.csv");
        ActiveEventsView.ExportCsv(csv, all);
        using (var parser = new TextFieldParser(csv))
        {
            parser.SetDelimiters(",");
            parser.HasFieldsEnclosedInQuotes = true;
            Check(parser.ReadFields()!.SequenceEqual(ActiveEventsView.Headers), "export header matches exact live table schema");
            foreach (var row in all) Check(parser.ReadFields()!.SequenceEqual(ActiveEventsView.Cells(row)), "export equals displayed snapshot cells including exact timestamps");
            Check(parser.EndOfData, "CSV contains only selected snapshot rows without old outage records");
        }
        try { ActiveEventsView.ExportCsv(csv, all); throw new Exception("Expected no-overwrite rejection."); }
        catch (IOException) { Check(true, "CSV refuses to overwrite an existing export"); }
        var unsafeCsv = Path.Combine(directory, "active-escaping.csv");
        var textRow = new ActiveAlarmRow { EquipmentName = " =FORMULA", EquipmentId = "QA", Condition = "fan,\"name\"\nline",
            Category = "Fault", State = "Active", Severity = "Alarm", Detail = "@formula" };
        ActiveEventsView.ExportCsv(unsafeCsv, new[] { textRow });
        using (var parser = new TextFieldParser(unsafeCsv))
        {
            parser.SetDelimiters(",");
            parser.HasFieldsEnclosedInQuotes = true;
            parser.ReadFields();
            var cells = parser.ReadFields()!;
            Check(cells[0].StartsWith("'") && cells[6] == "'@formula" && cells[2] == textRow.Condition,
                "CSV escapes multiline quoted evidence and prevents formula injection");
        }
        var afterRestart = new EquipmentAlarmRecorder(outbox, () => now) { CommunicationDebounce = TimeSpan.Zero, ValueDebounce = TimeSpan.Zero, CardClearDebounce = TimeSpan.Zero };
        var clear = new EquipmentAlarmEvidence { Complete = true };
        clear.Conditions["on-battery"] = new RecordedAlarmState { Active = false, Severity = "Alarm" };
        clear.Conditions["communication"] = new RecordedAlarmState { Active = false, Severity = "Alarm" };
        afterRestart.Observe("UPS", "UPS-ACTIVE-QA", now, clear);
        Check(Service(afterRestart).Read(now).Rows.All(row => row.EquipmentType != "UPS"), "fresh complete inactive evidence after restart clears retained active conditions");
        var active = new EquipmentAlarmEvidence { Complete = true };
        active.Conditions["on-battery"] = new RecordedAlarmState { Active = true, Severity = "Alarm" };
        afterRestart.Observe("UPS", "UPS-ACTIVE-QA", now, active);
        Check(Service(afterRestart).Read(now).Rows.Any(row => row.ConditionKey == "on-battery" && row.State == "Active"), "fresh positive evidence after restart confirms active again");
        now = now.AddSeconds(1);
        afterRestart.Observe("UPS", "UPS-ACTIVE-QA", now.AddMinutes(2), active);
        Check(Service(afterRestart).Read(now).Rows.Where(row => row.EquipmentType == "UPS").All(row => row.State == "Unknown"), "future acquisition timestamp cannot confirm a current active state");
        Console.WriteLine($"Active-event assertions passed: {assertions}.");
    }

    private static void UserInterface(ActiveAlarmSnapshot snapshot, string directory, string? evidence)
    {
        using var view = new ActiveEventsView(_ => snapshot);
        using var form = new Form { Text = "Synthetic active-event validation", ClientSize = new Size(1560, 820) };
        form.Controls.Add(view);
        form.Show();
        Wait(view.RefreshRecordsAsync());
        var grid = Field<DataGridView>(view, "_grid");
        var group = Field<ComboBox>(view, "_group");
        var state = Field<ComboBox>(view, "_state");
        var search = Field<TextBox>(view, "_search");
        Check(grid.ReadOnly && !grid.AllowUserToAddRows && grid.Rows.Count == snapshot.Rows.Count, "read-only active table shows current projection");
        Check(grid.Columns.Cast<DataGridViewColumn>().Select(column => column.HeaderText).SequenceEqual(ActiveEventsView.Headers),
            "professional table has no obsolete start/end/duration history columns");
        foreach (var (width, height) in new[] { (980, 600), (1160, 680), (1560, 900) })
        {
            form.ClientSize = new Size(width, height);
            Application.DoEvents();
            Check(grid.Height >= 240 && grid.Width >= width - 40, "active table retains usable viewport at width " + width);
            foreach (var name in new[] { "_group", "_state", "_search", "_refresh", "_export" })
            {
                var control = Field<Control>(view, name);
                Check(control.Bounds.Bottom <= control.Parent!.ClientSize.Height, "filter/control not clipped at " + width + ": " + name);
            }
            if (evidence != null)
            {
                Directory.CreateDirectory(evidence);
                using var image = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size));
                image.Save(Path.Combine(evidence, $"active-events-{width}.png"));
            }
        }
        group.SelectedIndex = 2;
        Check(grid.Rows.Count > 0 && grid.Rows.Cast<DataGridViewRow>().All(row => Convert.ToString(row.Cells[0].Value)!.Contains("UPS")),
            "equipment filter shows only selected UPS events");
        state.SelectedIndex = 1;
        Check(grid.Rows.Cast<DataGridViewRow>().All(row => Convert.ToString(row.Cells[3].Value) == "Active"), "active-only filter excludes unknowns");
        search.Text = "Multiple Fan Failure";
        Check(grid.Rows.Count == 1, "condition search selects one named fault");
        var filtered = Field<IReadOnlyList<ActiveAlarmRow>>(view, "_rows");
        ActiveEventsView.ExportCsv(Path.Combine(directory, "active-filtered.csv"), filtered);
        Check(File.ReadAllLines(Path.Combine(directory, "active-filtered.csv")).Length == 2, "filtered export contains exactly selected row and header");
        search.Text = "";
        group.SelectedIndex = 3;
        Check(grid.Rows.Count == 0 && Field<TextBox>(view, "_detail").Text.Contains("NOT CONNECTED") && !Field<Button>(view, "_export").Enabled,
            "fire filter has no invented data or false normal");
        group.SelectedIndex = 4;
        Check(grid.Rows.Count == 0 && Field<TextBox>(view, "_detail").Text.Contains("NOT CONNECTED"), "generator filter explicitly unavailable");
        group.SelectedIndex = 0;
        state.SelectedIndex = 2;
        Check(grid.Rows.Count == 0, "unknown-only filter contains no active rows");
        state.SelectedIndex = 0;
        view.SetPageActive(true);
        Check(Field<System.Windows.Forms.Timer>(view, "_timer").Enabled, "live updates run only when page active");
        view.SetPageActive(false);
        Check(!Field<System.Windows.Forms.Timer>(view, "_timer").Enabled, "hidden page stops live timer");
        Wait(view.RefreshRecordsAsync());
        using var unavailable = new ActiveEventsView(_ => throw new IOException("synthetic read failure"));
        form.Controls.Remove(view);
        form.Controls.Add(unavailable);
        Wait(unavailable.RefreshRecordsAsync());
        Check(Field<DataGridView>(unavailable, "_grid").Rows.Count == 0 && Field<Label>(unavailable, "_summary").Text.Contains("UNAVAILABLE") &&
            Field<Label>(unavailable, "_message").Text.Contains("synthetic read failure"), "read errors clear stale UI and surface explicit unavailable state");
        var good = true;
        using var transitions = new ActiveEventsView(_ => good ? snapshot : new ActiveAlarmSnapshot { CheckedUtc = snapshot.CheckedUtc, Coverage = snapshot.Coverage });
        Wait(transitions.RefreshRecordsAsync());
        Check(Field<DataGridView>(transitions, "_grid").Rows.Count == snapshot.Rows.Count, "active events initially rendered");
        good = false;
        Wait(transitions.RefreshRecordsAsync());
        Check(Field<DataGridView>(transitions, "_grid").Rows.Count == 0, "next confirmed-clear snapshot automatically removes actual UI rows");
        good = true;
        Wait(transitions.RefreshRecordsAsync());
        Check(Field<DataGridView>(transitions, "_grid").Rows.Count == snapshot.Rows.Count, "reappearance returns to actual UI without manual reset");
        form.Close();
    }

    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Instance)!.GetValue(instance)!;
    private static void Wait(Task task)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted && timeout.Elapsed < TimeSpan.FromSeconds(15)) { Application.DoEvents(); Thread.Sleep(5); }
        Check(task.IsCompletedSuccessfully, "live read completes successfully");
        task.GetAwaiter().GetResult();
    }
    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException("Active events: " + message);
        assertions++;
    }
}
