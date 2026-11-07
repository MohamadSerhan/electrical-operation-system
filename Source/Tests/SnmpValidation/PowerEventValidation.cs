using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;
using Microsoft.Data.Sqlite;
using Microsoft.VisualBasic.FileIO;

internal static class PowerEventValidation
{
    private static int assertions;
    private static readonly DateTime Epoch = new(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(5);

    internal static void Run(string? evidenceDirectory = null)
    {
        var folder = Path.Combine(Path.GetTempPath(), "eoems-power-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Decode();
            GaugeNeedles(evidenceDirectory);
            Transitions();
            EcgBack();
            UnknownAndGaps();
            PersistenceAndExport(folder);
            ActiveEventsValidation.Run(folder, evidenceDirectory);
            Views(folder, evidenceDirectory);
            TransformerRowArrangement();
            Acquisition(folder).GetAwaiter().GetResult();
            Console.WriteLine($"Power-event assertions passed: {assertions}.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }

    private static void TransformerRowArrangement()
    {
        var snapshots = Enumerable.Range(1, 4).Select(index =>
        {
            var values = Values(230);
            for (var phase = 0; phase < 3; phase++)
            {
                values[phase] = 220 + index + phase * 10;
                values[11 + phase] = index * 10 + phase;
            }
            values[10] = 10;
            return new MeterSnapshot { TransformerName = "T" + index, Quality = MeterQuality.Good,
                CapturedAtUtc = DateTime.UtcNow, DecodedValues = values };
        }).ToArray();
        using var view = new TransformersView(new PlcSettings(), new PlcConnectionStatus(), snapshots, () => Task.CompletedTask, false);
        view.SetReadings(snapshots);
        var columns = (IList)typeof(TransformersView).GetField("_columns", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        var total = columns[4]!;
        var grid = (DataGridView)total.GetType().GetProperty("Grid")!.GetValue(total)!;
        for (var phase = 0; phase < 3; phase++)
        {
            Check(Convert.ToString(grid.Rows[2 + phase].Cells[0].Value) == $"Avg Voltage L{phase + 1}-N" &&
                Convert.ToString(grid.Rows[2 + phase].Cells[2].Value) == "V" &&
                Convert.ToDouble(grid.Rows[2 + phase].Cells[1].Value, CultureInfo.InvariantCulture) == 222.5 + phase * 10,
                "total voltage label, unit and averaged reading align with transformer voltage row");
            Check(Convert.ToString(grid.Rows[5 + phase].Cells[0].Value) == $"Total Current L{phase + 1}" &&
                Convert.ToString(grid.Rows[5 + phase].Cells[2].Value) == "A" &&
                Convert.ToDouble(grid.Rows[5 + phase].Cells[1].Value, CultureInfo.InvariantCulture) == 100 + phase * 4,
                "total current label, unit and summed reading align with transformer current row");
        }
        for (var index = 0; index < 4; index++)
        {
            var column = columns[index]!;
            var panel = (Control)column.GetType().GetProperty("Panel")!.GetValue(column)!;
            Check(panel.Controls.OfType<Label>().Any(label => label.Text == $"Transformer {index + 1}"),
                "transformer card uses full display name while acquisition IDs remain unchanged");
        }
        var compute = typeof(TransformersView).GetMethod("ComputeTotals", BindingFlags.Static | BindingFlags.NonPublic)!;
        snapshots[0].DecodedValues[0] = float.NaN;
        var totals = (double?[])compute.Invoke(null, new object[] { snapshots })!;
        Check(totals[2] is null && totals[5] == 100, "missing voltage does not shift or invalidate current values");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Power-event test failed: " + message);
        assertions++;
    }

    private static float[] Values(float voltage)
    {
        var values = new float[16];
        for (var index = 0; index < 8; index++) values[index] = voltage;
        values[8] = voltage < 20 ? float.NaN : 50;
        values[10] = voltage < 20 ? 0 : 100;
        values[15] = 45;
        return values;
    }

    private static ushort[] Encode(float[] values, bool swap = true)
    {
        var registers = new ushort[values.Length * 2];
        for (var index = 0; index < values.Length; index++)
        {
            var bits = BitConverter.SingleToUInt32Bits(values[index]);
            registers[index * 2] = (ushort)(swap ? bits & 0xffff : bits >> 16);
            registers[index * 2 + 1] = (ushort)(swap ? bits >> 16 : bits & 0xffff);
        }
        return registers;
    }

    private static MeterSnapshot[] Cycle(double seconds, params float[] voltage)
    {
        return Enumerable.Range(0, 4).Select(index => new MeterSnapshot
        {
            TransformerName = "T" + (index + 1),
            CapturedAtUtc = Epoch.AddSeconds(seconds),
            Quality = MeterQuality.Good,
            DecodedValues = Values(voltage[index]),
            AcquisitionCycleId = (long)(seconds * 1000) + 1
        }).ToArray();
    }

    private static IReadOnlyList<PowerEventRecord> Step(PowerEventTracker tracker, double seconds, params float[] voltage)
    {
        tracker.Update(Cycle(seconds, voltage), Epoch.AddSeconds(seconds), Gap);
        return tracker.PendingRecords();
    }

    private static PowerEventRecord Open(IReadOnlyList<PowerEventRecord> records, string scope) =>
        records.Single(record => record.Scope == scope && record.EndUtc == null);

    private static void Decode()
    {
        foreach (var swap in new[] { false, true })
        {
            var values = PlcModbus.DecodeRegisters(Encode(Values(0), swap), swap);
            Check(values[0] == 0 && values[10] == 0 && values[15] == 45 && float.IsNaN(values[8]),
                "off frame keeps zero voltage, kW, temperature and missing frequency for either word order");
            var snapshot = new MeterSnapshot { Quality = MeterQuality.Good, DecodedValues = values };
            Check(PlcModbus.FormatValue(snapshot, 8) == PlcModbus.MissingValue, "frequency is never formatted as valid NaN");
            Check(PlcModbus.FormatValue(snapshot, 0) == "0", "valid off voltage is visible as zero");
            var stored = typeof(MeasurementHistorian).GetMethod("CopySnapshot", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { snapshot })!;
            var measurements = ((Array)stored.GetType().GetProperty("Measurements")!.GetValue(stored)!).Cast<object>();
            var indexes = measurements.Select(item => (int)item.GetType().GetProperty("ParameterIndex")!.GetValue(item)!).ToArray();
            Check(indexes.Contains(0) && indexes.Contains(10) && indexes.Contains(15) && !indexes.Contains(8),
                "historian retains valid off channels and omits unavailable frequency");
        }
        foreach (var voltage in new[] { -1f, 20f, 100f, 230f })
        {
            var values = Values(voltage);
            values[8] = float.NaN;
            Throws<InvalidDataException>(() => PlcModbus.DecodeRegisters(Encode(values), true),
                "NaN frequency with unconfirmed off voltage is still rejected");
        }
        foreach (var index in new[] { 0, 1, 2, 10, 15 })
        {
            var values = Values(0);
            values[index] = float.NaN;
            Throws<InvalidDataException>(() => PlcModbus.DecodeRegisters(Encode(values), true),
                "non-frequency NaN remains invalid at index " + index);
        }
        var infinite = Values(0);
        infinite[8] = float.PositiveInfinity;
        Throws<InvalidDataException>(() => PlcModbus.DecodeRegisters(Encode(infinite), true), "infinite frequency is not outage evidence");
        Throws<InvalidDataException>(() => PlcModbus.DecodeRegisters(new ushort[31], true), "truncated blocks stay invalid");
        Check(PowerEventTracker.VoltageState(Values(19.99f)) == TransformerPowerState.Off, "off threshold strictly below 20 V");
        Check(PowerEventTracker.VoltageState(Values(20)) == TransformerPowerState.Unknown, "20 V is not off");
        Check(PowerEventTracker.VoltageState(Values(180)) == TransformerPowerState.Unknown, "180 V is not restored");
        Check(PowerEventTracker.VoltageState(Values(180.01f)) == TransformerPowerState.On, "restore threshold strictly above 180 V");
        var unloaded = Values(230);
        unloaded[10] = 0;
        Check(PowerEventTracker.VoltageState(unloaded) == TransformerPowerState.On, "zero kW does not imply power off");
    }

    private static double _last = -1;

    private static (bool Off, bool Back) Flags(PowerEventTracker tracker, double seconds, params float[] voltage)
    {
        // One-second cycles up to the target time so the 5 s monitoring-gap rule is never hit.
        for (var step = Math.Floor(_last) + 1; step < seconds; step++) tracker.Update(Cycle(step, voltage), Epoch.AddSeconds(step), Gap);
        var snapshots = Cycle(seconds, voltage);
        tracker.Update(snapshots, Epoch.AddSeconds(seconds), Gap);
        _last = seconds;
        return (snapshots[0].EcgOffConfirmed, snapshots[0].EcgBackConfirmed);
    }

    private static void EcgBackRecorded()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "ecg-chain-" + Guid.NewGuid().ToString("N") + ".db");
        var now = Epoch;
        using var outbox = new DurableRecordingOutbox(dbPath);
        var recorder = new EquipmentAlarmRecorder(outbox, () => now);
        var tracker = new PowerEventTracker();
        var limits = new EquipmentAlarmLimits();
        void Step(double seconds, float volts)
        {
            now = Epoch.AddSeconds(seconds);
            var snapshots = Cycle(seconds, volts, volts, volts, volts);
            tracker.Update(snapshots, now, Gap);
            var trusted = snapshots.All(item => item.Quality == MeterQuality.Good && item.PowerState != TransformerPowerState.Unknown);
            var total = EquipmentAlarmRecorder.TransformerEvidence(new float[18], "Good", limits, 1000, null,
                ecgOff: trusted ? snapshots.All(item => item.EcgOffConfirmed) : null,
                ecgBack: trusted ? snapshots.All(item => item.EcgBackConfirmed) : null,
                voltageEvaluable: false);
            recorder.Observe("Transformer", "TOTAL-CHAIN-QA", now, total);
        }
        for (var s = 0; s <= 2; s++) Step(s, 230);
        var count = outbox.PendingCount;
        for (var s = 3; s <= 20; s++) Step(s, 0);
        Check(outbox.PendingCount == count + 1, "full chain: ECG off recorded once on TOTAL");
        for (var s = 21; s <= 40; s++) Step(s, 230);
        Check(outbox.PendingCount == count + 2, "full chain: ECG back recorded once after all four transformers return");
        for (var s = 41; s <= 200; s++) Step(s, 230);
        Check(outbox.PendingCount == count + 2, "full chain: ECG back is not repeated or cleared into a row");
    }

    private static void EcgBack()
    {
        var pairs = EcgOutageLog.Pair(new (DateTime, bool)[] {
            (Epoch.AddMinutes(1), true), (Epoch.AddMinutes(5), true), (Epoch.AddMinutes(11), false),
            (Epoch.AddMinutes(30), true), (Epoch.AddMinutes(33), false),
            (Epoch.AddMinutes(50), true) });
        Check(pairs.Count == 3 && pairs[0].EndUtc == Epoch.AddMinutes(11) && pairs[1].EndUtc - pairs[1].StartUtc == TimeSpan.FromMinutes(3) &&
            pairs[2].EndUtc == null, "each ECG off pairs with the next ECG back; an unmatched off stays ongoing");
        EcgBackRecorded();
        var tracker = new PowerEventTracker();
        _last = -1;
        Flags(tracker, 0, 0, 0, 0, 0);
        Check(Flags(tracker, 3, 0, 0, 0, 0) == (true, false), "ECG off confirmed, no back flag");
        Flags(tracker, 10, 230, 230, 230, 230);
        Check(Flags(tracker, 13, 230, 230, 230, 230) == (false, true), "ECG back appears once all four are confirmed on");
        Check(Flags(tracker, 66, 230, 230, 230, 230).Back, "ECG back still shown before 60 s");
        Check(!Flags(tracker, 68, 230, 230, 230, 230).Back, "ECG back clears after 60 s");
        Flags(tracker, 100, 0, 0, 0, 0);
        Check(Flags(tracker, 103, 0, 0, 0, 0) == (true, false), "second outage shows ECG off");
        Flags(tracker, 110, 230, 230, 230, 230);
        Check(Flags(tracker, 113, 230, 230, 230, 230).Back, "ECG back again");
        Flags(tracker, 120, 0, 230, 230, 230);
        Check(!Flags(tracker, 124, 0, 230, 230, 230).Back, "another power alarm cancels ECG back");
        Flags(tracker, 130, 230, 230, 230, 230);
        Check(!Flags(tracker, 134, 230, 230, 230, 230).Back, "no ECG back without a preceding ECG outage");
    }

    private static void Transitions()
    {
        var tracker = new PowerEventTracker();
        Check(Step(tracker, 0, 0, 230, 230, 230).Count == 0, "no event before confirmation");
        Check(Step(tracker, 2.999, 0, 230, 230, 230).Count == 0, "2.999 seconds is not 3 seconds");
        var records = Step(tracker, 3, 0, 230, 230, 230);
        var first = Open(records, "T1");
        Check(first.StartUtc == Epoch.AddSeconds(3) && first.ConfirmedUtc == first.StartUtc, "start is confirmed detection time");
        Check(first.Evidence.Contains("T1 L-N=0/0/0 V"), "event includes voltage evidence");
        records = Step(tracker, 4, 0, 0, 0, 0);
        Check(records.Count(record => record.EndUtc == null) == 1, "other transformers wait for confirmation");
        records = Step(tracker, 7, 0, 0, 0, 0);
        Check(records.Count(record => record.EndUtc == null) == 1 && Open(records, "ECG") != null,
            "all four off creates only ECG");
        Check(first.EndUtc == Epoch.AddSeconds(7) && first.EndReason == "Scope changed", "individual segment closes at ECG boundary");
        var ecg = Open(records, "ECG");
        Step(tracker, 8, 0, 0, 0, 0);
        records = Step(tracker, 9, 0, 0, 0, 0);
        Check(records.Count == 2 && Open(records, "ECG").Id == ecg.Id, "polls update existing event, no duplicate rows");
        records = Step(tracker, 10, 230, 0, 0, 0);
        Check(Open(records, "ECG").Id == ecg.Id && ecg.LastObservedUtc == Epoch.AddSeconds(9), "restore debounce does not manufacture off observations");
        records = Step(tracker, 13, 230, 0, 0, 0);
        Check(ecg.EndUtc == Epoch.AddSeconds(13) && ecg.EndReason == "Scope changed", "ECG ends when first transformer restores");
        Check(records.Where(record => record.EndUtc == null).Select(record => record.Scope).Order().SequenceEqual(new[] { "T2", "T3", "T4" }),
            "remaining off transformers get individual segments");
        Check(records.Where(record => record.EndUtc == null).All(record => record.StartUtc == ecg.EndUtc), "scope boundaries do not overlap");
        Step(tracker, 14, 230, 230, 230, 230);
        records = Step(tracker, 17, 230, 230, 230, 230);
        Check(records.All(record => record.EndUtc.HasValue), "full restoration closes all outages");
        Check(records.Count(record => record.EndReason == "Power restored (confirmed)") == 3, "individual restoration reasons retained");
        tracker.AcceptPersisted();
        Check(tracker.PendingRecords().Count == 0, "acknowledged closed records leave pending buffer");

        tracker = new PowerEventTracker();
        Step(tracker, 0, 0, 230, 230, 230);
        Step(tracker, 2, 230, 230, 230, 230);
        Check(Step(tracker, 4, 0, 230, 230, 230).Count == 0, "brief voltage loss does not trigger an outage");
        Check(Step(tracker, 6, 0, 230, 230, 230).Count == 0, "off timer restarts after flicker");
        Check(Step(tracker, 7, 0, 230, 230, 230).Count == 1, "new uninterrupted candidate confirms");

        tracker = new PowerEventTracker();
        Step(tracker, 0, 0, 230, 230, 230);
        Step(tracker, 3, 0, 230, 230, 230);
        Step(tracker, 4, 0, 0, 0, 0);
        records = Step(tracker, 7, 230, 0, 0, 0);
        Check(records.All(record => record.Scope != "ECG"), "cannot start ECG while a previous off transformer is restoring");
        Check(tracker.Summary == "Transformer off", "global summary follows actual exclusive event scope");
        Step(tracker, 10, 230, 0, 0, 0);
        Check(tracker.PendingRecords().Single(record => record.Scope == "T1").EndReason == "Power restored (confirmed)",
            "individual restoration is not mislabeled scope change while others stay off");

        tracker = new PowerEventTracker();
        Step(tracker, 0, 0, 0, 0, 0);
        Step(tracker, 3, 0, 0, 0, 0);
        var transition = Cycle(4, 0, 230, 0, 0);
        transition[0].Quality = MeterQuality.Timeout;
        tracker.Update(transition, Epoch.AddSeconds(4), Gap);
        Check(tracker.PendingRecords().All(record => record.EndUtc.HasValue || record.Scope != "T2"),
            "scope split cannot start an off record from currently energized voltage during debounce");
    }

    private static void UnknownAndGaps()
    {
        foreach (var quality in new[] { MeterQuality.Timeout, MeterQuality.Offline, MeterQuality.BadQuality, MeterQuality.MappingError })
        {
            var tracker = new PowerEventTracker();
            Step(tracker, 0, 0, 0, 0, 0);
            var ecg = Open(Step(tracker, 3, 0, 0, 0, 0), "ECG");
            var snapshots = Cycle(4, 0, 0, 0, 0);
            snapshots[0].Quality = quality;
            tracker.Update(snapshots, Epoch.AddSeconds(4), Gap);
            Check(ecg.EndUtc == Epoch.AddSeconds(3) && ecg.EndReason.Contains("unknown"), quality + " closes ECG at last verified time, not restoration");
            Check(snapshots[0].PowerState == TransformerPowerState.Unknown, quality + " is not transformer off");
            Check(tracker.PendingRecords().Count(record => record.EndUtc == null && record.Scope != "ECG") == 3,
                "other valid off transformers continue as individual records");
        }
        var gaps = new PowerEventTracker();
        Step(gaps, 0, 0, 0, 0, 0);
        var old = Open(Step(gaps, 3, 0, 0, 0, 0), "ECG");
        var after = Step(gaps, 20, 0, 0, 0, 0);
        Check(old.EndUtc == Epoch.AddSeconds(3) && old.EndReason == "Monitoring gap", "long monitoring gap is not bridged");
        Check(after.All(record => record.EndUtc.HasValue), "gap requires fresh confirmation");
        Check(Open(Step(gaps, 23, 0, 0, 0, 0), "ECG").Id != old.Id, "post-gap outage gets new identity");

        var tracker2 = new PowerEventTracker();
        var partial = Cycle(0, 0, 0, 0, 0);
        partial[0].DecodedValues[1] = 230;
        tracker2.Update(partial, Epoch, Gap);
        partial = Cycle(3, 0, 0, 0, 0);
        partial[0].DecodedValues[1] = 230;
        tracker2.Update(partial, Epoch.AddSeconds(3), Gap);
        Check(partial[0].PowerState == TransformerPowerState.Unknown &&
            tracker2.PendingRecords().All(record => record.Scope != "ECG"), "single phase loss is unknown, not full transformer or ECG off");
        tracker2.Interrupt("Test stop");
        Check(tracker2.PendingRecords().All(record => record.EndUtc.HasValue), "explicit monitoring stop closes active records");
        var empty = new PowerEventTracker();
        empty.Update(Array.Empty<MeterSnapshot>(), Epoch, Gap);
        empty.Update(Array.Empty<MeterSnapshot>(), Epoch.AddSeconds(3), Gap);
        Check(empty.PendingRecords().Count == 0, "absent snapshots never imply all-off");
        var stale = Cycle(0, 0, 0, 0, 0);
        empty.Update(stale, Epoch.AddSeconds(10), Gap);
        Check(stale.All(snapshot => snapshot.PowerState == TransformerPowerState.Unknown), "stale snapshots are not outage evidence");
        var reversed = new PowerEventTracker();
        Step(reversed, 0, 0, 0, 0, 0);
        var beforeClockChange = Open(Step(reversed, 3, 0, 0, 0, 0), "ECG");
        Step(reversed, 2, 0, 0, 0, 0);
        Check(beforeClockChange.EndUtc == beforeClockChange.LastObservedUtc && beforeClockChange.EndReason == "Monitoring gap",
            "backwards system clock cannot produce negative or bridged durations");
        Throws<ArgumentNullException>(() => empty.Update(null!, Epoch, Gap), "null snapshots rejected explicitly");
        Throws<ArgumentException>(() => empty.Update(Array.Empty<MeterSnapshot>(), DateTime.Now, Gap), "local timestamps rejected explicitly");
    }

    private static void PersistenceAndExport(string folder)
    {
        var path = Path.Combine(folder, "power.db");
        var tracker = new PowerEventTracker();
        Step(tracker, 0, 0, 0, 0, 0);
        Step(tracker, 3, 0, 0, 0, 0);
        var store = new PowerEventStore(path);
        store.Save(tracker.PendingRecords());
        tracker.AcceptPersisted();
        Step(tracker, 4, 0, 0, 0, 0);
        store.Save(tracker.PendingRecords());
        var rows = PowerEventStore.ReadRange(path, Epoch, Epoch.AddDays(1));
        Check(rows.Count == 1 && rows[0].LastObservedUtc == Epoch.AddSeconds(4), "SQLite updates one durable event");
        Check(rows[0].StartUtc.Kind == DateTimeKind.Utc && rows[0].StartUtc == Epoch.AddSeconds(3), "UTC round-trip exact");
        Check(rows[0].SecondsInRange(Epoch.AddSeconds(3.5), Epoch.AddSeconds(3.75)) == 0.25, "duration is clipped at both report bounds");
        Check(rows[0].SecondsInRange(Epoch, Epoch.AddDays(1)) == 1, "open duration uses observation, not current time");
        var restarted = new PowerEventStore(path);
        restarted.Save(Array.Empty<PowerEventRecord>());
        rows = PowerEventStore.ReadRange(path, Epoch, Epoch.AddDays(1));
        Check(rows[0].EndUtc == Epoch.AddSeconds(4) && rows[0].EndReason.Contains("restart"), "restart seals old event at persisted observation");
        var csv = Path.Combine(folder, "events.csv");
        PowerEventStore.ExportCsv(csv, rows, Epoch, Epoch.AddDays(1));
        using (var parser = new TextFieldParser(csv))
        {
            parser.SetDelimiters(",");
            parser.HasFieldsEnclosedInQuotes = true;
            var header = parser.ReadFields()!;
            var fields = parser.ReadFields()!;
            Check(header.Length == 12 && fields.Length == 12, "CSV has 12 stable analysis fields");
            Check(fields[1] == "ECG" && fields[8] == "1" && fields[9].EndsWith("Z") && fields[11] == rows[0].Evidence,
                "CSV contains scope, invariant numeric duration, UTC window and intact evidence");
        }
        Check(PowerEventStore.ReadRange(path, Epoch.AddDays(1), Epoch.AddDays(2)).Count == 0, "out-of-range history excluded");
        Throws<ArgumentException>(() => PowerEventStore.ReadRange(path, Epoch, Epoch), "invalid report range is explicit");
        var blocker = Path.Combine(folder, "not-a-directory");
        File.WriteAllText(blocker, "block");
        Throws<IOException>(() => new PowerEventStore(Path.Combine(blocker, "events.db")).Save(rows), "write failure is not success-shaped");
        using var source = new SqliteConnection("Data Source=" + path);
        using var backup = new SqliteConnection("Data Source=" + Path.Combine(folder, "backup.db"));
        source.Open();
        backup.Open();
        source.BackupDatabase(backup);
        Check(PowerEventStore.ReadRange(Path.Combine(folder, "backup.db"), Epoch, Epoch.AddDays(1)).Count == 1,
            "existing SQLite database backup includes power events");
        using (var command = source.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER reject_test BEFORE INSERT ON power_events WHEN NEW.scope='FAIL' BEGIN SELECT RAISE(ABORT,'test failure'); END";
            command.ExecuteNonQuery();
        }
        var pending = new PowerEventRecord
        {
            Scope = "T1", StartUtc = Epoch.AddSeconds(10), ConfirmedUtc = Epoch.AddSeconds(10),
            LastObservedUtc = Epoch.AddSeconds(11), Evidence = "Quoted \"evidence\", with\nnew line"
        };
        var invalid = new PowerEventRecord
        {
            Scope = "FAIL", StartUtc = Epoch, ConfirmedUtc = Epoch, LastObservedUtc = Epoch
        };
        Throws<SqliteException>(() => restarted.Save(new[] { pending, invalid }), "database transaction failure surfaces explicitly");
        Check(PowerEventStore.ReadRange(path, Epoch, Epoch.AddDays(1)).Count == 1, "failed batch rolls back all inserts");
        restarted.Save(new[] { pending });
        rows = PowerEventStore.ReadRange(path, Epoch, Epoch.AddDays(1));
        Check(rows.Count == 2, "same pending records can persist after failure");
        PowerEventStore.ExportCsv(csv, rows, Epoch, Epoch.AddDays(1));
        using (var parser = new TextFieldParser(csv))
        {
            parser.SetDelimiters(",");
            parser.HasFieldsEnclosedInQuotes = true;
            parser.ReadFields();
            parser.ReadFields();
            Check(parser.ReadFields()![11] == pending.Evidence, "CSV quoting round-trips commas, quotes and embedded newline");
        }
    }

    private static void Views(string folder, string? evidenceDirectory)
    {
        var tracker = new PowerEventTracker();
        Step(tracker, 0, 0, 0, 0, 0);
        var snapshots = Cycle(3, 0, 0, 0, 0);
        tracker.Update(snapshots, Epoch.AddSeconds(3), Gap);
        var status = new PlcConnectionStatus { Health = PlcHealth.Connected, PowerSummary = "ECG off" };
        Check(UiTheme.StatusText(status) == "ECG off" && UiTheme.StatusColor(status) == UiTheme.Red, "global power status is red ECG off with healthy communication");
        Check(UiTheme.StatusText(PlcHealth.BadQuality) == "Bad quality", "actual data corruption retains original quality label");
        Check(UiTheme.StatusText(new PlcConnectionStatus { Health = PlcHealth.BadQuality, PowerSummary = "Power unknown" }) == "Bad quality",
            "unknown power cannot conceal actual frame/value validation failures");
        Check(UiTheme.StatusText(new PlcConnectionStatus { Health = PlcHealth.Timeout, PowerSummary = "Power unknown" }) == "PLC timeout",
            "unknown power cannot conceal actual PLC timeouts");
        Check(UiTheme.StatusColor(new PlcConnectionStatus { Health = PlcHealth.Timeout, PowerSummary = "Power unknown" }) == UiTheme.Red,
            "unknown power cannot downgrade communication error severity");
        using var view = new TransformersView(new PlcSettings(), status, snapshots, () => Task.CompletedTask, false);
        view.SetReadings(snapshots);
        var columns = (IList)typeof(TransformersView).GetField("_columns", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        for (var index = 0; index < 5; index++)
        {
            var column = columns[index]!;
            var gauge = (LoadGaugeControl)column.GetType().GetProperty("Gauge")!.GetValue(column)!;
            Check(gauge.IndicatorText == (index == 4 ? "ECG OFF" : "TRANSFORMER OFF"), "actual gauge uses outage title for column " + index);
            Check(gauge.IndicatorDetail.Length == 0, "power-off gauge hides explanatory text for column " + index);
        }
        if (evidenceDirectory != null) Capture(view, evidenceDirectory, "transformers-off-test.png");
        var settings = new RecordingSettings();
        settings.Normalize();
        using var historian = new MeasurementHistorian(Path.Combine(folder, "power.db"), recordingSettings: settings, migrateLegacyDatabase: false);
        using var scheduler = new DailyHistorianExportScheduler(historian, settings, folder);
        using var history = new HistorianView(settings, new UserSession { RoleName = "Viewer", IsOfflinePreview = true },
            historian, scheduler, _ => Task.CompletedTask);
        var tabs = (TabControl)typeof(HistorianView).GetField("_tabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(history)!;
        Check(tabs.TabPages.Count == 6 && tabs.TabPages[3].Text == "Active events" && tabs.TabPages[4].Text == "Equipment history" && tabs.TabPages[5].Text == "Fire Events",
            "Active events replaces outage-history UI while equipment history remains wired into the Historian strip");
        var backupPath = Path.Combine(folder, "historian-backup.db");
        historian.CreateBackupDatabaseAsync(backupPath).GetAwaiter().GetResult();
        Check(PowerEventStore.ReadRange(backupPath, Epoch, Epoch.AddDays(1)).Count == 2, "actual historian backup API preserves power events");
        if (evidenceDirectory != null)
        {
            tabs.SelectedIndex = 3;
            Capture(history, evidenceDirectory, "historian-power-events-test.png");
        }
    }

    private static void Capture(Control view, string directory, string fileName)
    {
        Directory.CreateDirectory(directory);
        using var form = new Form { ClientSize = new System.Drawing.Size(1600, 900), Text = "Isolated power-event validation (synthetic data)" };
        view.Dock = DockStyle.Fill;
        form.Controls.Add(view);
        form.Show();
        Application.DoEvents();
        using var bitmap = new System.Drawing.Bitmap(form.ClientSize.Width, form.ClientSize.Height);
        view.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(directory, fileName));
        form.Controls.Remove(view);
        form.Close();
    }

    private static void GaugeNeedles(string? evidenceDirectory)
    {
        foreach (var size in new[] { new System.Drawing.Size(180, 125), new System.Drawing.Size(260, 170) })
        foreach (var percent in new[] { 0d, 25d, 50d, 75d, 100d })
        {
            using var gauge = new LoadGaugeControl
            {
                Size = size, LoadPercent = percent, IndicatorText = "", BackColor = System.Drawing.Color.White
            };
            using var image = new System.Drawing.Bitmap(size.Width, size.Height);
            gauge.DrawToBitmap(image, new System.Drawing.Rectangle(System.Drawing.Point.Empty, size));
            var radius = Math.Min((size.Width - 28d) / 2, size.Height - 20d) - 8;
            var angle = (180 - percent * 1.8) * Math.PI / 180;
            var x = (int)Math.Round(size.Width / 2d + Math.Cos(angle) * radius * 0.86);
            var y = (int)Math.Round(size.Height - 10d - Math.Sin(angle) * radius * 0.86);
            bool NeedlePixel(int px, int py)
            {
                var pixel = image.GetPixel(px, py);
                return pixel.R > 130 && pixel.G < 85 && pixel.B < 85;
            }
            Check(Enumerable.Range(-1, 3).Any(dx => Enumerable.Range(-1, 3).Any(dy => NeedlePixel(x + dx, y + dy))),
                $"needle reaches scale ticks at {percent}% on {size.Width}px gauge");
            Check(Math.Abs(gauge.NeedleAngleDegrees - (180 - percent * 1.8)) < 0.001,
                "longer needle preserves exact load-to-scale angle");
            if (percent is 0 or 100)
            {
                var shaftX = (int)Math.Round(size.Width / 2d + Math.Cos(angle) * radius * 0.4);
                var baseline = size.Height - 10;
                var thickness = Enumerable.Range(-6, 13).Count(dy => NeedlePixel(shaftX, baseline + dy));
                Check(thickness >= 4, "slimmer needle shaft remains at least four visible pixels thick");
            }
            if (evidenceDirectory != null)
            {
                Directory.CreateDirectory(evidenceDirectory);
                image.Save(Path.Combine(evidenceDirectory, $"needle-{size.Width}-{percent}.png"));
            }
        }
    }

    private static async Task Acquisition(string folder)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var cancel = new CancellationTokenSource();
        var server = Task.Run(async () =>
        {
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(cancel.Token);
                    await using var stream = client.GetStream();
                    var request = new byte[12];
                    var received = await stream.ReadAtLeastAsync(request, request.Length, false, cancel.Token);
                    if (received == 0) continue;
                    if (received != request.Length) throw new InvalidDataException("Synthetic PLC received a truncated request.");
                    var registers = Encode(Values(0));
                    var response = new byte[73];
                    response[0] = request[0];
                    response[1] = request[1];
                    response[5] = 67;
                    response[6] = request[6];
                    response[7] = 3;
                    response[8] = 64;
                    for (var index = 0; index < registers.Length; index++)
                    {
                        response[9 + index * 2] = (byte)(registers[index] >> 8);
                        response[10 + index * 2] = (byte)registers[index];
                    }
                    await stream.WriteAsync(response, cancel.Token);
                }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        });
        var path = Path.Combine(folder, "acquisition.db");
        var settings = new PlcSettings
        {
            Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
            RequestedAcquisitionIntervalMilliseconds = 100, ReadTimeoutMilliseconds = 500, SwapFloatWords = true
        };
        using var plc = new PlcConnectionService(settings, path);
        try
        {
            await plc.StartAsync();
            var wait = Stopwatch.StartNew();
            while (plc.CurrentStatus.PowerSummary != "ECG off" && wait.Elapsed < TimeSpan.FromSeconds(8))
                await Task.Delay(50).ConfigureAwait(false);
            Check(plc.CurrentStatus.PowerSummary == "ECG off", "real FC03 poll pipeline confirms ECG off");
            Check(plc.CurrentStatus.Health == PlcHealth.Connected && plc.CurrentStatus.Diagnostics.FailedTransactions == 0,
                "off frame no longer increments bad-quality/failure counters");
            Check(plc.CurrentReadings.All(snapshot => snapshot.Quality == MeterQuality.Good && snapshot.DecodedValues[0] == 0 &&
                float.IsNaN(snapshot.DecodedValues[8]) && snapshot.PowerState == TransformerPowerState.Off),
                "real acquisition publishes preserved voltage, missing frequency and confirmed per-transformer state");
            var rows = PowerEventStore.ReadRange(path, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
            Check(rows.Count == 1 && rows[0].Scope == "ECG", "real acquisition persists exclusive ECG event");
            await plc.StopAsync().ConfigureAwait(false);
            rows = PowerEventStore.ReadRange(path, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
            Check(rows[0].EndReason == "Monitoring stopped" && rows[0].EndUtc.HasValue, "normal stop durably seals observation interval");
            var recoveryPath = Path.Combine(folder, "save-blocker");
            File.WriteAllText(recoveryPath, "block");
            using (var failingPlc = new PlcConnectionService(settings, Path.Combine(recoveryPath, "events.db")))
            {
                await failingPlc.StartAsync().ConfigureAwait(false);
                wait.Restart();
                while (failingPlc.CurrentStatus.PowerSummary != "ECG off" && wait.Elapsed < TimeSpan.FromSeconds(8))
                    await Task.Delay(50).ConfigureAwait(false);
                Check(failingPlc.CurrentStatus.PowerSummary == "ECG off" && failingPlc.CurrentStatus.PowerLogError.Length > 0,
                    "acquisition continues detecting outages while persistence failure is explicit");
                Check(UiTheme.StatusText(failingPlc.CurrentStatus) == "Power log error", "persistence failure is visible in the global status pill");
                File.Delete(recoveryPath);
                Directory.CreateDirectory(recoveryPath);
                wait.Restart();
                while (failingPlc.CurrentStatus.PowerLogError.Length > 0 && wait.Elapsed < TimeSpan.FromSeconds(5))
                    await Task.Delay(50).ConfigureAwait(false);
                Check(failingPlc.CurrentStatus.PowerLogError.Length == 0, "persistence retries and clears error only after successful commit");
                var recovered = PowerEventStore.ReadRange(Path.Combine(recoveryPath, "events.db"), DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
                Check(recovered.Count == 1 && recovered[0].Scope == "ECG", "outage first detected during storage failure survives retry");
                await failingPlc.StopAsync().ConfigureAwait(false);
            }
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.EnumerateFiles(recoveryPath)) File.Delete(file);
            Directory.Delete(recoveryPath);
        }
        finally
        {
            await plc.StopAsync().ConfigureAwait(false);
            cancel.Cancel();
            await server.ConfigureAwait(false);
        }
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { Check(true, message); return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + message);
    }
}
