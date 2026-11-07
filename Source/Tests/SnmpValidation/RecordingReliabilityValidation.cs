using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using EOEMS.PowerTransformersScadaUI.V5;
using Microsoft.Data.Sqlite;
using Npgsql;

internal static class RecordingReliabilityValidation
{
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static int assertions;

    internal static void Run(string connectionString, string directory)
    {
        var limits = new AlarmSettings();
        typeof(AlarmSettingsStore).GetField("_current", PrivateStatic)!.SetValue(null, limits);
        var settings = UpsSettings.CreateDefault();
        settings.Devices = settings.Devices.Take(1).ToList();
        settings.Devices[0].Id = "UPS-RELIABILITY-QA";
        settings.HistorianIntervalSeconds = 10;
        typeof(UpsSettingsStore).GetField("_current", PrivateStatic)!.SetValue(null, settings);
        var device = settings.Devices[0];
        var stamp = new DateTime(2026, 10, 30, 2, 0, 0, DateTimeKind.Utc);
        var snapshot = Snapshot(device.Id, stamp, 50, true, false);
        var row = UpsHistorian.CreateRecordedRow(device, snapshot, stamp, settings);
        var path = Path.Combine(directory, "recording-outbox.db");
        long buffered;
        using (var outbox = new DurableRecordingOutbox(path))
        {
            outbox.Enqueue(new[] { row });
            Check(outbox.PendingCount == 1, "UPS sample is durable before PostgreSQL access");
            typeof(PostgresDatabase).GetField("_connectionString", PrivateStatic)!.SetValue(null,
                "Host=127.0.0.1;Port=1;Database=missing;Username=isolated_qa;Timeout=1;Pooling=false");
            try
            {
                try { outbox.FlushAsync().GetAwaiter().GetResult(); throw new Exception("Expected unavailable PostgreSQL."); }
                catch (NpgsqlException) { }
                Check(outbox.PendingCount == 1, "database outage does not delete buffered observations");
            }
            finally { typeof(PostgresDatabase).GetField("_connectionString", PrivateStatic)!.SetValue(null, connectionString); }
            var recorder = new EquipmentAlarmRecorder(outbox) { CommunicationDebounce = TimeSpan.Zero, ValueDebounce = TimeSpan.Zero, CardClearDebounce = TimeSpan.Zero };
            recorder.Observe("UPS", device.Id, stamp, EquipmentAlarmRecorder.UpsEvidence(snapshot, limits.Ups));
            var count = outbox.PendingCount;
            recorder.Observe("UPS", device.Id, stamp, EquipmentAlarmRecorder.UpsEvidence(snapshot, limits.Ups));
            Check(outbox.PendingCount == count, "unchanged states are not duplicate incidents");
            var acknowledged = Snapshot(device.Id, stamp.AddSeconds(1), 50, true, true);
            recorder.Observe("UPS", device.Id, acknowledged.TimestampUtc, EquipmentAlarmRecorder.UpsEvidence(acknowledged, limits.Ups));
            var failed = new UpsSnapshot { DeviceId = device.Id, TimestampUtc = stamp.AddSeconds(2), Quality = UpsQuality.Timeout };
            recorder.Observe("UPS", device.Id, failed.TimestampUtc, EquipmentAlarmRecorder.UpsEvidence(failed, limits.Ups));
            var recovery = Snapshot(device.Id, stamp.AddSeconds(3), 50, true, true);
            recorder.Observe("UPS", device.Id, recovery.TimestampUtc, EquipmentAlarmRecorder.UpsEvidence(recovery, limits.Ups));
            var clear = Snapshot(device.Id, stamp.AddSeconds(4), 50, false, true);
            recorder.Observe("UPS", device.Id, clear.TimestampUtc, EquipmentAlarmRecorder.UpsEvidence(clear, limits.Ups));
            var active = Snapshot(device.Id, stamp.AddSeconds(5), 50, true, true);
            recorder.Observe("UPS", device.Id, active.TimestampUtc, EquipmentAlarmRecorder.UpsEvidence(active, limits.Ups));
            var changedPolicy = limits.Ups.Clone();
            changedPolicy.LoadPreAlarmPercent = 60;
            recorder.Observe("UPS", device.Id, active.TimestampUtc, EquipmentAlarmRecorder.UpsEvidence(active, changedPolicy));
            buffered = outbox.PendingCount;
            Check(buffered > count, "acknowledgements, unavailable evidence, clear, raise and policy changes are durable");
        }
        using (var outbox = new DurableRecordingOutbox(path))
        {
            Check(outbox.PendingCount == buffered, "buffer survives recorder restart");
            var recorder = new EquipmentAlarmRecorder(outbox) { CommunicationDebounce = TimeSpan.Zero, ValueDebounce = TimeSpan.Zero, CardClearDebounce = TimeSpan.Zero };
            recorder.Observe("UPS", device.Id, snapshot.TimestampUtc, EquipmentAlarmRecorder.UpsEvidence(snapshot, limits.Ups));
            outbox.FlushAsync().GetAwaiter().GetResult();
            Check(outbox.PendingCount == 0, "UPS and alarm observations drain after PostgreSQL recovers");
            Check(outbox.LastUpsRows == 1 && outbox.LastUpsWriteUtc.HasValue, "UPS diagnostics count verified per-device rows");
            outbox.Enqueue(new[] { row });
            outbox.FlushAsync().GetAwaiter().GetResult();
            using var connection = PostgresDatabase.Open();
            Check(Scalar(connection, "SELECT count(*) FROM ups_samples WHERE device_id='UPS-RELIABILITY-QA' AND sampled_utc='2026-10-30 02:00:00+00'") == 1,
                "ambiguous-commit replay cannot duplicate a UPS slot");
            Check(Scalar(connection, "SELECT count(*) FROM ups_samples WHERE device_id='UPS-RELIABILITY-QA' AND output_kw=50 AND alarm_evaluation IS NOT NULL") == 1,
                "durable typed parameters preserve numeric values and policy evaluation");
            Check(Scalar(connection, "SELECT count(*) FROM equipment_alarm_history WHERE equipment_id='UPS-RELIABILITY-QA' AND event_kind='ObservedAcknowledged'") == 0,
                "acknowledgement changes of an active alarm are not separate rows");
            Check(Scalar(connection, "SELECT count(*) FROM equipment_alarm_history WHERE equipment_id='UPS-RELIABILITY-QA' AND event_kind='ObservedRaised'") >= 1,
                "confirmed observed raises persist");
            Check(Scalar(connection, "SELECT count(*) FROM equipment_alarm_history WHERE equipment_id='UPS-RELIABILITY-QA' AND event_kind='ObservedCleared' AND alarm_key LIKE 'card:%'") == 1,
                "only a complete subsequent card table clears a previously active card condition");
            Check(Scalar(connection, "SELECT count(*) FROM equipment_alarm_history WHERE equipment_id='UPS-RELIABILITY-QA' AND event_kind='StateUnknown'") == 0,
                "unknown evidence is never written as a history row");
            Check(Scalar(connection, "SELECT count(*) FROM equipment_alarm_history WHERE equipment_id='UPS-RELIABILITY-QA' AND event_kind='PolicyChanged'") > 0,
                "alarm policy changes retain historical context");
            Check(Scalar(connection, "SELECT count(*) FROM equipment_alarm_history WHERE equipment_id='UPS-RELIABILITY-QA' AND event_kind='MonitoringResumed'") == 0,
                "restart writes no resume row");
            var source = new EquipmentAlarmHistorySource();
            var asset = source.GetEquipment().Single(item => item.SourceKey == "UPS:" + device.Id);
            var results = source.Read(new EquipmentHistoryRequest
            {
                Equipment = asset, Metrics = asset.Metrics, FromUtc = DateTime.UtcNow.AddMinutes(-5), ToUtc = DateTime.UtcNow.AddMinutes(1)
            }, default);
            Check(results.Count > 5 && results.Any(item => item.Evidence.Contains("ObservedRaised")),
                "history adapter exposes saved event identity, states, policy and card acknowledgements");
            Check(results.All(item => !item.EnergyEligible), "alarm observations never become energy measurements");
        }

        var values = Enumerable.Repeat(230f, 18).ToArray();
        values[10] = 900;
        values[15] = 80;
        var transformer = EquipmentAlarmRecorder.TransformerEvidence(values, "Good", limits.Transformers, 1000, null);
        Check(transformer.Conditions["load"].Severity == "ShedLoad" && transformer.Conditions["temperature"].Active == true,
            "transformer alarms use configured rating and actual recorded values");
        var unavailable = EquipmentAlarmRecorder.TransformerEvidence(values, "Timeout", limits.Transformers, 1000, null);
        Check(unavailable.Conditions["load"].Active is null && unavailable.Conditions["communication"].Active == true,
            "failed transformer acquisition never reuses stale values as a normal state");
        SparseAlarmTiming(directory);
        var off = EquipmentAlarmRecorder.TransformerEvidence(new float[16], "Good", limits.Transformers, 1000, null,
            TransformerPowerState.Off, true);
        Check(off.Conditions["transformer-off"].Active == true && off.Conditions["ecg-off"].Active == true &&
            off.Conditions["transformer-off"].Category == "Fault" && off.Conditions["ecg-off"].Name == "ECG off",
            "confirmed power detector states produce named transformer-off and ECG-off faults");
        Check(unavailable.Conditions["transformer-off"].Active == null && unavailable.Conditions["ecg-off"].Active == null,
            "missing power evidence is unknown, never a fabricated off or restored state");
        var mixedVoltages = Enumerable.Repeat(230f, 16).ToArray();
        mixedVoltages[0] = 210;
        mixedVoltages[1] = 250;
        var voltageEvidence = EquipmentAlarmRecorder.TransformerEvidence(mixedVoltages, "Good", limits.Transformers, 1000);
        Check(voltageEvidence.Conditions["under-voltage:L1"].Active == true &&
            voltageEvidence.Conditions["over-voltage:L1"].Active == false &&
            voltageEvidence.Conditions["over-voltage:L2"].Active == true &&
            voltageEvidence.Conditions["under-voltage:L2"].Active == false,
            "under-voltage and over-voltage are distinct phase-specific conditions, not a generic voltage bit");
        var fan = Snapshot(device.Id, stamp, 50, true, false);
        fan.CardAlarms[0].Name = "Fan failure";
        fan.CardAlarms[0].CardType = "Fault";
        fan.Values = new Dictionary<string, double>(fan.Values) { ["OutputSource"] = 5 };
        var batteryEvidence = EquipmentAlarmRecorder.UpsEvidence(fan, limits.Ups);
        Check(batteryEvidence.Conditions["on-battery"].Active == true &&
            batteryEvidence.Conditions["on-battery"].Category == "Alarm" &&
            batteryEvidence.Conditions.Values.Any(state => state.Name == "Fan failure" && state.Category == "Fault"),
            "UPS on-battery alarm and named card fan fault remain distinct, clear events");
        fan.Values = new Dictionary<string, double>(fan.Values) { ["OutputSource"] = 4 };
        Check(EquipmentAlarmRecorder.UpsEvidence(fan, limits.Ups).Conditions["on-bypass"].Active == true,
            "UPS bypass is a recorded operating alarm rather than a hidden card-only bit");

        var boundary = new DateTime(2026, 10, 30, 3, 0, 0, DateTimeKind.Utc);
        var cadence = new RecordingSettings { HistorianRecordingIntervalMilliseconds = 1000 };
        Check(!cadence.IsRawRecordingDue(boundary.AddMilliseconds(850), boundary) &&
            !cadence.IsRawRecordingDue(boundary.AddMilliseconds(999.9999), boundary) &&
            cadence.IsRawRecordingDue(boundary.AddMilliseconds(1000), boundary), "transformer cadence never admits an 85%-interval early sample");
        cadence.ContinuousRawRecordingEnabled = false;
        Check(!cadence.IsRawRecordingDue(boundary.AddSeconds(5), boundary), "disabled transformer recording stays disabled");
        Check(UpsHistorian.SlotAt(boundary.AddTicks(-1), 60) == boundary.AddMinutes(-1) &&
            UpsHistorian.SlotAt(boundary, 60) == boundary &&
            UpsHistorian.SlotAt(boundary.AddTicks(1), 60) == boundary,
            "slot arithmetic is exact on both sides of a clock boundary");

        using (var outbox = new DurableRecordingOutbox(Path.Combine(directory, "transformer-outbox.db")))
        {
            var recording = new RecordingSettings { HistorianRecordingIntervalMilliseconds = 1000 };
            recording.Normalize();
            using (var historian = new MeasurementHistorian(Path.Combine(directory, "transformer-qa.db"),
                recordingSettings: recording, migrateLegacyDatabase: false))
            {
                historian.AttachOutbox(outbox);
                var snapshots = Enumerable.Range(1, 4).Select(index => new MeterSnapshot
                {
                    TransformerName = "T" + index, CapturedAtUtc = DateTime.UtcNow.AddMilliseconds(index),
                    AcquisitionCycleId = 987654, Quality = MeterQuality.Good, DecodedValues = Enumerable.Repeat(230f, 16).ToArray()
                }).ToArray();
                var evidence = snapshots.ToDictionary(item => item.TransformerName, _ => JsonSerializer.Serialize(transformer));
                evidence["TOTAL"] = JsonSerializer.Serialize(transformer);
                Check(historian.TryEnqueue(987654, snapshots, evidence), "transformer cycle accepts alarm-policy evidence");
                historian.StopAsync().GetAwaiter().GetResult();
                Check(outbox.PendingCount == 10, "raw transformer rows and quality counters are buffered without synchronous PostgreSQL writes");
            }
            outbox.FlushAsync().GetAwaiter().GetResult();
            using var connection = PostgresDatabase.Open();
            Check(Scalar(connection, "SELECT count(*) FROM measurement_samples WHERE cycle_id=987654 AND alarm_evidence IS NOT NULL") == 5,
                "all transformer and TOTAL samples retain alarm evaluation after durable replay");
            Check(outbox.LastTransformerWriteUtc.HasValue, "transformer commit diagnostics advance after PostgreSQL replay");
            using (var query = new NpgsqlCommand("SELECT vals FROM measurement_samples WHERE cycle_id=987654 AND analyzer_id='TOTAL'", connection))
            {
                var channels = (float[])query.ExecuteScalar()!;
                Check(channels.Length == 18 && float.IsNaN(channels[4]), "non-applicable TOTAL channels remain NaN, never zero, through durable serialization");
            }

            RecordedCommand increment;
            using (var create = new NpgsqlCommand("CREATE TABLE qa_replay_counter(n integer); INSERT INTO qa_replay_counter VALUES(0)", connection))
                create.ExecuteNonQuery();
            using (var command = new NpgsqlCommand("UPDATE qa_replay_counter SET n=n+1"))
                increment = RecordedCommand.Capture(command);
            outbox.Enqueue(new[] { increment });
            using (var transaction = connection.BeginTransaction())
            {
                increment.Execute(connection, transaction);
                using var receipt = new NpgsqlCommand("INSERT INTO recording_replay_commits(command_id) VALUES(@id)", connection, transaction);
                receipt.Parameters.AddWithValue("id", increment.Id);
                receipt.ExecuteNonQuery();
                transaction.Commit();
            }
            outbox.FlushAsync().GetAwaiter().GetResult();
            using (var verify = new NpgsqlCommand("SELECT n FROM qa_replay_counter", connection))
                Check((int)verify.ExecuteScalar()! == 1, "crash after PostgreSQL commit but before local delete cannot double-add quality counters");
        }

        using (var acquisition = new UpsAcquisitionService())
        using (var outbox = new DurableRecordingOutbox(Path.Combine(directory, "scheduler-outbox.db")))
        using (var historian = new UpsHistorian(acquisition))
        {
            var started = DateTime.UtcNow;
            historian.Start(outbox, new EquipmentAlarmRecorder(outbox) { CommunicationDebounce = TimeSpan.Zero, ValueDebounce = TimeSpan.Zero, CardClearDebounce = TimeSpan.Zero });
            Thread.Sleep(22100);
            historian.StopAsync().GetAwaiter().GetResult();
            outbox.StopAsync().GetAwaiter().GetResult();
            while (outbox.PendingCount > 0) outbox.FlushAsync().GetAwaiter().GetResult();
            using var connection = PostgresDatabase.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sampled_utc,quality,output_kw FROM ups_samples WHERE device_id=@id AND sampled_utc>=@from ORDER BY sampled_utc";
            command.Parameters.AddWithValue("id", device.Id);
            command.Parameters.AddWithValue("from", UpsHistorian.SlotAt(started, 10));
            using var reader = command.ExecuteReader();
            var slots = new List<DateTime>();
            while (reader.Read())
            {
                slots.Add(reader.GetDateTime(0));
                Check(reader.GetString(1) != "GOOD" && reader.IsDBNull(2), "missing live snapshots remain failure-quality/null, not invented data");
            }
            Check(slots.Count >= 3, "real scheduler crosses at least two boundaries");
            Check(slots.Zip(slots.Skip(1), (a, b) => b - a).All(delta => delta == TimeSpan.FromSeconds(10)),
                "real scheduler does not skip slots when timers wake around boundaries");
        }
        Console.WriteLine($"Recording reliability assertions passed: {assertions}.");
    }

    private static void OutageRules(string directory)
    {
        var limits = new EquipmentAlarmLimits();
        var start = new DateTime(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc);
        var now = start;
        using var outbox = new DurableRecordingOutbox(Path.Combine(directory, "outage-rules.db"));
        var recorder = new EquipmentAlarmRecorder(outbox, () => now);
        void Observe(double seconds, TransformerPowerState state)
        {
            now = start.AddSeconds(seconds);
            var values = new float[18];
            for (var phase = 0; phase < 3; phase++) values[phase] = state == TransformerPowerState.On ? 230 : 0;
            var evidence = EquipmentAlarmRecorder.TransformerEvidence(values, "Good", limits, 1000, null, state,
                voltageEvaluable: state == TransformerPowerState.On);
            evidence.Conditions.Remove("ecg-off");
            recorder.Observe("Transformer", "T-OUT-QA", now, evidence);
        }
        Observe(0, TransformerPowerState.On);
        var count = outbox.PendingCount;
        Observe(1, TransformerPowerState.Off);
        Observe(2, TransformerPowerState.Off);
        Observe(3, TransformerPowerState.Unknown);
        Observe(4, TransformerPowerState.Off);
        Check(outbox.PendingCount == count + 1, "transformer off is recorded once and Unknown never repeats or clears it");
        Observe(10, TransformerPowerState.On);
        Observe(11, TransformerPowerState.On);
        Check(outbox.PendingCount == count + 2, "power restored is recorded once as transformer on");
    }

    private static void VoltageAlarmRules(string directory)
    {
        OutageRules(directory);
        var limits = new EquipmentAlarmLimits();
        var start = new DateTime(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc);
        var now = start;
        using var outbox = new DurableRecordingOutbox(Path.Combine(directory, "voltage-rules.db"));
        var recorder = new EquipmentAlarmRecorder(outbox, () => now);
        void Observe(double seconds, float volts, bool evaluable = true)
        {
            now = start.AddSeconds(seconds);
            var values = new float[18];
            for (var phase = 0; phase < 3; phase++) values[phase] = volts;
            recorder.Observe("Transformer", "T-VOLT-QA", now,
                EquipmentAlarmRecorder.TransformerEvidence(values, "Good", limits, 1000, null, voltageEvaluable: evaluable));
        }
        Observe(0, 234);
        var count = outbox.PendingCount;
        Observe(1, 234);
        Check(outbox.PendingCount == count, "in-range voltage never writes Normal rows");
        Observe(2, 250);
        Observe(3, 234);
        Observe(4, 250);
        Observe(12, 250);
        Check(outbox.PendingCount == count, "over-voltage shorter than ten seconds is ignored");
        Observe(13, 250);
        Observe(14, 250);
        Check(outbox.PendingCount == count + 3, "persistent over-voltage is recorded once per phase");
        Observe(15, 234);
        Observe(20, 234);
        Check(outbox.PendingCount == count + 3, "a brief return to range does not clear a recorded alarm");
        Observe(30, 234);
        Check(outbox.PendingCount == count + 6, "alarm clears once after ten stable seconds");
        Observe(40, 100, evaluable: false);
        Observe(60, 100, evaluable: false);
        Check(outbox.PendingCount == count + 6, "a de-energised transformer never raises under-voltage");
    }

    private static void CommunicationDebounce(string directory)
    {
        VoltageAlarmRules(directory);
        var start = new DateTime(2026, 10, 30, 6, 0, 0, DateTimeKind.Utc);
        var now = start;
        using var outbox = new DurableRecordingOutbox(Path.Combine(directory, "comm-debounce.db"));
        var recorder = new EquipmentAlarmRecorder(outbox, () => now);
        void Observe(double seconds, bool bad)
        {
            now = start.AddSeconds(seconds);
            var evidence = new EquipmentAlarmEvidence { Quality = bad ? "BadQuality" : "Good", Complete = !bad };
            evidence.Conditions["communication"] = new RecordedAlarmState { Active = bad, Severity = "Alarm" };
            recorder.Observe("Transformer", "TOTAL-DEBOUNCE-QA", now, evidence);
        }
        Observe(0, false);
        var count = outbox.PendingCount;
        Observe(5, true);
        Observe(10, false);
        Observe(15, true);
        Observe(20, false);
        Check(outbox.PendingCount == count, "single failed polls never record a communication failure");
        Observe(30, true);
        Observe(44, true);
        Check(outbox.PendingCount == count, "failure shorter than 15 seconds is not recorded");
        Observe(45, true);
        Check(outbox.PendingCount == count + 1, "failure lasting 15 seconds is recorded once");
    }

    private static void SparseAlarmTiming(string directory)
    {
        CommunicationDebounce(directory);
        var start = new DateTime(2026, 10, 30, 4, 0, 0, DateTimeKind.Utc);
        var now = start;
        var path = Path.Combine(directory, "sparse-alarm-timing.db");
        EquipmentAlarmEvidence Evidence(bool? fault, bool communication)
        {
            var evidence = new EquipmentAlarmEvidence { Quality = "Good", Complete = fault.HasValue };
            evidence.Conditions["battery-status"] = new RecordedAlarmState { Active = fault, Severity = "Alarm", Detail = "QA fault" };
            evidence.Conditions["communication"] = new RecordedAlarmState { Active = communication, Severity = "Alarm" };
            return evidence;
        }
        using (var outbox = new DurableRecordingOutbox(path))
        {
            var recorder = new EquipmentAlarmRecorder(outbox, () => now) { CommunicationDebounce = TimeSpan.Zero, ValueDebounce = TimeSpan.Zero, CardClearDebounce = TimeSpan.Zero };
            void Observe(double seconds, bool? fault, bool communication)
            {
                now = start.AddSeconds(seconds);
                recorder.Observe("UPS", "UPS-SPARSE-TIMING-QA", now, Evidence(fault, communication));
            }
            Observe(0, false, false);
            Observe(60, true, true);
            var count = outbox.PendingCount;
            for (var tick = 601; tick < 3000; tick++) Observe(tick / 10.0, true, true);
            Observe(300, true, true);
            Observe(660, true, true);
            Check(outbox.PendingCount == count, "an active alarm is never repeated or reminded while it stays active");
            Observe(661, null, true);
            Observe(662, null, true);
            Observe(663, true, true);
            Check(outbox.PendingCount == count, "an unknown gap writes nothing and recovery to the same state writes nothing");
            Observe(700, false, true);
            Check(outbox.PendingCount == count + 1, "the clear is recorded once");
            Observe(701, false, true);
            Observe(900, false, true);
            Check(outbox.PendingCount == count + 1, "a cleared alarm writes no Normal rows");
            outbox.FlushAsync().GetAwaiter().GetResult();
        }
        using var connection = PostgresDatabase.Open();
        Check(Scalar(connection, "SELECT count(*) FROM equipment_alarm_history WHERE equipment_id='UPS-SPARSE-TIMING-QA' AND alarm_key='battery-status'") == 2 &&
            Scalar(connection, "SELECT count(*) FROM equipment_alarm_history WHERE equipment_id='UPS-SPARSE-TIMING-QA' AND event_kind IN ('ActiveReminder','StateUnknown','ObservedInactive','StateAtResume')") == 0,
            "history holds exactly one raise and one clear, with no reminder/unknown/normal rows");
    }

    private static UpsSnapshot Snapshot(string id, DateTime stamp, double kw, bool alarm, bool acknowledged) => new()
    {
        DeviceId = id, TimestampUtc = stamp, Quality = UpsQuality.Good, OutputVoltageReference = "L-N",
        AlarmDetailsComplete = true, AlarmDetailsStatus = "isolated verified fixture",
        Values = new Dictionary<string, double> { ["OutputPower"] = kw * 1000, ["OutputApparentVa"] = kw * 1250,
            ["BatteryStatus"] = 2, ["OutputSource"] = 3, ["ChargePercent"] = 98 },
        Phases = new Dictionary<string, double?[]> { ["OutputLoad"] = new double?[] { 10, 20, 30 },
            ["OutputVoltage"] = new double?[] { 230, 230, 230 } },
        CardAlarms = alarm ? new[] { new UpsCardAlarm { Id = "1", Source = "QA", DescriptionOid = "1.2.3",
            Name = "synthetic fan condition", Active = true, Acknowledged = acknowledged, CardSeverity = "Major" } } : Array.Empty<UpsCardAlarm>()
    };

    private static long Scalar(NpgsqlConnection connection, string sql)
    {
        using var command = new NpgsqlCommand(sql, connection);
        return (long)command.ExecuteScalar()!;
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException("Recording reliability: " + message);
        assertions++;
    }
}
