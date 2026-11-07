using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;
using Microsoft.Data.Sqlite;
using Npgsql;

internal static class UpsAlarmHistoryValidation
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private static int assertions;

    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var temporary = Path.Combine(Path.GetTempPath(), "eoems-ups-alarm-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(temporary, "postgres");
        Directory.CreateDirectory(temporary);
        var pg = @"C:\Program Files\PostgreSQL\18\bin";
        var started = false;
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            RunProcess(Path.Combine(pg, "initdb.exe"), "-D", data, "-U", "isolated_qa", "-A", "trust", "--no-locale", "--encoding=UTF8");
            RunProcess(Path.Combine(pg, "pg_ctl.exe"), "-D", data, "-l", Path.Combine(temporary, "postgres.log"),
                "-o", $"-h 127.0.0.1 -p {port} -F", "-w", "start");
            started = true;
            typeof(PostgresDatabase).GetField("_connectionString", Static)!.SetValue(null,
                $"Host=127.0.0.1;Port={port};Database=postgres;Username=isolated_qa;Pooling=false;Timeout=2");
            typeof(PostgresDatabase).GetField("_initialized", Static)!.SetValue(null, true);
            typeof(AlarmSettingsStore).GetField("_current", Static)!.SetValue(null, new AlarmSettings());
            typeof(UpsSettingsStore).GetField("_current", Static)!.SetValue(null, UpsSettings.CreateDefault());
            using var connection = PostgresDatabase.Open();
            EquipmentAlarmRecorder.EnsureSchema(connection);
            Recording(temporary, connection);
            var end = new DateTime(2026, 11, 4, 18, 0, 0, DateTimeKind.Utc);
            using (var seed = connection.CreateCommand())
            {
                seed.CommandText = """
                    INSERT INTO equipment_alarm_history(event_id,equipment_type,equipment_id,observed_utc,session_id,event_kind,alarm_key,current_state,evidence)
                    SELECT md5(g::text)::uuid,'UPS','QA-HISTORY',@end - ((g/3)::text || ' minutes')::interval,
                        @session,'ObservedRaised','load',
                        jsonb_build_object('Active',true,'Severity','Alarm','Name','Synthetic high load','Category','Alarm','Detail',repeat('Synthetic diagnostic context. ',30)),
                        '{}'::jsonb FROM generate_series(1,50000) g;
                    ANALYZE equipment_alarm_history;
                    """;
                seed.Parameters.AddWithValue("end", end);
                seed.Parameters.AddWithValue("session", Guid.NewGuid());
                seed.ExecuteNonQuery();
            }
            var watch = Stopwatch.StartNew();
            var first = UpsHistorian.ReadAlarmEvents("QA-HISTORY", end.AddDays(-1), end, 201);
            watch.Stop();
            var firstMilliseconds = watch.Elapsed.TotalMilliseconds;
            Check(first.Count == 201 && first.All(e => e.TimestampUtc >= end.AddDays(-1) && e.TimestampUtc < end),
                "first read is bounded and filters UTC [from,to) in PostgreSQL");
            Check(firstMilliseconds < 2000, "50,000-row indexed first page completes within two seconds");
            var second = UpsHistorian.ReadAlarmEvents("QA-HISTORY", end.AddDays(-1), end, 201, before: first[199]);
            Check(!first.Take(200).Select(e => e.EventId).Intersect(second.Select(e => e.EventId)).Any() &&
                first[200].EventId == second[0].EventId, "timestamp ties page without duplicate or skipped rows");
            foreach (var days in new[] { 1, 7, 30 })
            {
                var from = end.AddDays(-days);
                var rows = UpsHistorian.ReadAlarmEvents("QA-HISTORY", from, end, 1000);
                Check(rows.Count <= 1000 && rows.All(e => e.TimestampUtc >= from && e.TimestampUtc < end),
                    $"rolling {days}-day range is enforced before materialization");
            }
            var boundary = first[0].TimestampUtc;
            Check(UpsHistorian.ReadAlarmEvents("QA-HISTORY", boundary, boundary.AddMinutes(1), 201).Count == 3 &&
                UpsHistorian.ReadAlarmEvents("QA-HISTORY", boundary.AddMinutes(-1), boundary, 201).All(e => e.TimestampUtc < boundary),
                "lower boundary is inclusive and upper boundary exclusive");
            Check(UpsHistorian.ReadAlarmEvents("another-device", end.AddDays(-1), end, 201).Count == 0,
                "device filtering never mixes UPS records");
            var allIds = new HashSet<Guid>();
            UpsAlarmEvent? cursor = null;
            do
            {
                var page = UpsHistorian.ReadAlarmEvents("QA-HISTORY", DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
                    end, 200, before: cursor!);
                Check(page.Count <= 200 && page.All(e => allIds.Add(e.EventId)),
                    "All keyset pages remain bounded and contain no repeated event IDs");
                cursor = page.Count == 200 ? page[^1] : null;
            } while (cursor is not null);
            Check(allIds.Count == 49998, "All reaches every historical row before the exclusive end, including timestamp ties");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try
                {
                    UpsHistorian.ReadAlarmEvents("QA-HISTORY", end.AddDays(-1), end, 201, cancellation: cancellation.Token);
                    throw new InvalidOperationException("Cancelled history request returned success.");
                }
                catch (OperationCanceledException) { assertions++; }
            }
            Ui(connection);
            File.WriteAllText(Path.Combine(output, "ups-alarm-history-validation.json"),
                JsonSerializer.Serialize(new { Synthetic = true, SeedRows = 50000, FirstPageMilliseconds = firstMilliseconds, Assertions = assertions },
                    new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS: {assertions} UPS alarm-history assertions; 50,000 synthetic rows; first page {firstMilliseconds:0.0} ms.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            NpgsqlConnection.ClearAllPools();
            if (started) RunProcess(Path.Combine(pg, "pg_ctl.exe"), "-D", data, "-m", "fast", "-w", "stop");
            Directory.Delete(temporary, true);
        }
    }

    private static void Recording(string temporary, NpgsqlConnection connection)
    {
        var now = DateTime.UtcNow;
        using var outbox = new DurableRecordingOutbox(Path.Combine(temporary, "outbox.db"));
        var recorder = new EquipmentAlarmRecorder(outbox, () => now)
        {
            ValueDebounce = TimeSpan.Zero, CardClearDebounce = TimeSpan.Zero, CommunicationDebounce = TimeSpan.Zero
        };
        EquipmentAlarmEvidence Evidence(bool diagnostic, bool fault) => new()
        {
            Complete = !diagnostic, Quality = "Good", Policy = new EquipmentAlarmLimits(),
            Conditions = new(StringComparer.Ordinal)
            {
                ["card-evidence"] = new() { Active = diagnostic, Severity = "PreAlarm", Detail = "Synthetic timeout context" },
                ["voltage-reference"] = new() { Active = diagnostic, Severity = "PreAlarm", Detail = "Unknown reference" },
                ["power-consistency"] = new() { Active = diagnostic, Severity = "PreAlarm", Detail = "Synthetic diagnostic" },
                ["load"] = new() { Active = fault, Severity = fault ? "Alarm" : "Normal", Detail = "Synthetic high load" },
                ["communication"] = new() { Active = fault, Severity = fault ? "Alarm" : "Normal", Detail = "Timeout" }
            }
        };
        recorder.Observe("UPS", "QA-RECORD", now, Evidence(false, false));
        var baseline = outbox.PendingCount;
        for (var i = 0; i < 20; i++)
        {
            now = now.AddSeconds(1);
            recorder.Observe("UPS", "QA-RECORD", now, Evidence(i % 2 == 0, false));
        }
        Check(outbox.PendingCount == baseline, "diagnostic fluctuations append no alarm-history rows");
        now = now.AddSeconds(1);
        recorder.Observe("UPS", "QA-RECORD", now, Evidence(true, true));
        Check(recorder.ReadCurrentState("UPS", "QA-RECORD").State.Evidence.Conditions["card-evidence"].Active == true,
            "diagnostic evidence remains available in live state");
        now = now.AddSeconds(1);
        recorder.Observe("UPS", "QA-RECORD", now, Evidence(false, false));
        outbox.FlushAsync().GetAwaiter().GetResult();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT alarm_key,event_kind FROM equipment_alarm_history WHERE equipment_id='QA-RECORD' AND left(alarm_key,2)<>'__'";
        using var reader = command.ExecuteReader();
        var rows = new List<(string Key, string Kind)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        Check(rows.Count == 4 && rows.All(r => r.Key is "load" or "communication") &&
            rows.Count(r => r.Kind == "ObservedRaised") == 2 && rows.Count(r => r.Kind == "ObservedCleared") == 2,
            "actual faults and communication failures retain both raises and clears");
        var snapshot = new UpsSnapshot { Quality = UpsQuality.Timeout, QualityDetail = new string('x', 10000) };
        Check(EquipmentAlarmRecorder.UpsEvidence(snapshot, new EquipmentAlarmLimits()).Conditions["communication"].Detail.Length < 100,
            "communication history stores compact quality instead of verbose poll diagnostics");
    }

    private static void Ui(NpgsqlConnection connection)
    {
        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = "UPDATE equipment_alarm_history SET observed_utc=observed_utc+@shift WHERE equipment_id='QA-HISTORY'";
            seed.Parameters.AddWithValue("shift", DateTime.UtcNow - new DateTime(2026, 11, 4, 18, 0, 0, DateTimeKind.Utc));
            seed.ExecuteNonQuery();
        }
        using var historian = new UpsHistorian(null!);
        using var form = new UpsDetailForm(new UpsDevice { Id = "QA-HISTORY", Label = "SYNTHETIC QA - NOT LIVE" }, null!, historian);
        T Field<T>(string name) => (T)typeof(UpsDetailForm).GetField(name, Instance)!.GetValue(form)!;
        var tabs = form.Controls.OfType<FlatTabControl>().Single();
        var grid = Field<DataGridView>("_alarmHistory");
        var range = Field<ComboBox>("_alarmRange");
        var older = Field<Button>("_alarmOlder");
        var newer = Field<Button>("_alarmNewer");
        form.Show();
        Application.DoEvents();
        using var context = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(context);
        Check(grid.Rows.Count == 0 && range.SelectedIndex == 0, "opening UPS details does not eagerly read alarm history; default is Day");
        var watch = Stopwatch.StartNew();
        tabs.SelectedTab = tabs.TabPages.Cast<TabPage>().Single(p => p.Text == "Alarm History");
        Wait(() => typeof(UpsDetailForm).GetField("_alarmCancellation", Instance)!.GetValue(form) is null);
        watch.Stop();
        Check(grid.Rows.Count == 200 && older.Enabled && !newer.Enabled && watch.ElapsedMilliseconds < 2000,
            "actual WinForms first page binds only 200 rows within two seconds");
        Check(grid.AutoSizeRowsMode == DataGridViewAutoSizeRowsMode.None &&
            grid.Rows[0].Cells[5].ToolTipText.Length > 500, "fixed row heights avoid full text measurement; complete detail remains available");
        var firstTime = grid.Rows[0].Cells[0].Value;
        SynchronizationContext.SetSynchronizationContext(context);
        older.PerformClick();
        Wait(() => typeof(UpsDetailForm).GetField("_alarmCancellation", Instance)!.GetValue(form) is null);
        Check(grid.Rows.Count == 200 && newer.Enabled, "Older reads a bounded next page");
        SynchronizationContext.SetSynchronizationContext(context);
        newer.PerformClick();
        Wait(() => typeof(UpsDetailForm).GetField("_alarmCancellation", Instance)!.GetValue(form) is null);
        Check(Equals(firstTime, grid.Rows[0].Cells[0].Value) && !newer.Enabled, "Newer returns to the first snapshot page");
        using (var transaction = connection.BeginTransaction())
        {
            using var command = new NpgsqlCommand("LOCK TABLE equipment_alarm_history IN ACCESS EXCLUSIVE MODE", connection, transaction);
            command.ExecuteNonQuery();
            SynchronizationContext.SetSynchronizationContext(context);
            range.SelectedIndex = 1;
            Application.DoEvents();
            var oldRequest = Field<CancellationTokenSource>("_alarmCancellation");
            SynchronizationContext.SetSynchronizationContext(context);
            range.SelectedIndex = 2;
            Check(oldRequest.IsCancellationRequested, "changing range cancels the in-flight database read");
            SynchronizationContext.SetSynchronizationContext(context);
            range.SelectedIndex = 3;
            var responsive = false;
            form.BeginInvoke(() => responsive = true);
            Application.DoEvents();
            Check(responsive, "UI message queue remains responsive while database history is blocked");
            transaction.Rollback();
        }
        Wait(() => typeof(UpsDetailForm).GetField("_alarmCancellation", Instance)!.GetValue(form) is null);
        Check(range.Items.Count == 4 && grid.Rows.Count == 200 && !newer.Enabled && older.Enabled,
            "Day/Week/Month/All selection resets paging; All remains capped at 200 displayed records");
        using (var transaction = connection.BeginTransaction())
        {
            using var command = new NpgsqlCommand("LOCK TABLE equipment_alarm_history IN ACCESS EXCLUSIVE MODE", connection, transaction);
            command.ExecuteNonQuery();
            SynchronizationContext.SetSynchronizationContext(context);
            range.SelectedIndex = 0;
            var closingRequest = Field<CancellationTokenSource>("_alarmCancellation");
            form.Close();
            Check(closingRequest.IsCancellationRequested, "closing UPS details cancels pending alarm reads");
            transaction.Rollback();
        }
        Wait(() => typeof(UpsDetailForm).GetField("_alarmCancellation", Instance)!.GetValue(form) is null);
    }

    private static void Wait(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            Application.DoEvents();
            if (ready()) return;
            Thread.Sleep(10);
        } while (watch.Elapsed < TimeSpan.FromSeconds(10));
        throw new TimeoutException("UPS history UI did not settle.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }

    private static void RunProcess(string file, params string[] args)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(30000) || process.ExitCode != 0) throw new InvalidOperationException("Disposable PostgreSQL fixture failed: " + Path.GetFileName(file));
    }
}
