using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EOEMS.PowerTransformersScadaUI.V5;
using Microsoft.Data.Sqlite;
using Npgsql;

internal static class LiveRecordingAudit
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    internal static void RunFleet(string appDirectory, string output)
    {
        Directory.CreateDirectory(output);
        var configuration = Path.Combine(appDirectory, "EOEMSData", "Configuration");
        var credentialPath = SnmpCredentialStore.StorePath();
        var before = File.Exists(credentialPath) ? File.ReadAllBytes(credentialPath) : null;
        var settingsField = typeof(UpsSettingsStore).GetField("_current", Static)!;
        var original = settingsField.GetValue(null);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(credentialPath)!);
            File.Copy(Path.Combine(configuration, "snmp-credentials.json"), credentialPath, true);
            var settings = JsonSerializer.Deserialize<UpsSettings>(File.ReadAllText(Path.Combine(configuration, "ups-settings.json")), Json)!;
            settingsField.SetValue(null, settings);
            var probe = typeof(UpsSettings).Assembly.GetTypes().Single(type => type.Name == "UpsCapabilityProbe").GetMethod("Run",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
            foreach (var device in settings.Devices.Where(item => item.Enabled))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var watch = Stopwatch.StartNew();
                var capability = (UpsCapabilityProfile)probe.Invoke(null, new object[] { device, timeout.Token })!;
                UpsSnapshot? full = null;
                var fast = new List<object>();
                if (capability.IdentityAnswered && capability.SupportsUpsMib)
                {
                    full = UpsAcquisitionService.PollDevice(device, 0, null!);
                    foreach (var keys in new[] { new[] { "@OutputKw" }, new[] { "InputVoltage", "OutputVoltage", "OutputCurrent" } })
                    {
                        var sample = UpsAcquisitionService.PollAnalyzerDevice(device, keys);
                        fast.Add(new { keys, sample.Quality, sample.QualityDetail, sample.RoundTripMilliseconds,
                            sample.OutputKw, sample.Values, sample.Phases });
                    }
                }
                File.WriteAllText(Path.Combine(output, device.Id + "-live.json"), JsonSerializer.Serialize(new
                {
                    device.Id, device.PollIntervalSeconds, device.TimeoutMilliseconds,
                    capability.IdentityAnswered, capability.SupportsUpsMib, capability.Complete, capability.Aborted,
                    capability.ElapsedMilliseconds, capability.UnsupportedOids, Snapshot = full, Fast = fast
                }, Json));
                Console.WriteLine($"{device.Id}: identity={capability.IdentityAnswered}; UPS-MIB={capability.SupportsUpsMib}; " +
                    $"quality={full?.Quality.ToString() ?? capability.FailureQuality.ToString()}; " +
                    $"full request={full?.RoundTripMilliseconds:0} ms; values={full?.Values.Count ?? 0}; " +
                    $"phase groups={full?.Phases.Count ?? 0}; audit={watch.ElapsedMilliseconds} ms.");
            }
        }
        finally
        {
            settingsField.SetValue(null, original);
            if (before is null) File.Delete(credentialPath);
            else File.WriteAllBytes(credentialPath, before);
        }
    }

    internal static void Run(string appDirectory, string output)
    {
        Directory.CreateDirectory(output);
        var builder = Connection(appDirectory);
        builder.ApplicationName = "EOEMS_READ_ONLY_RECORDING_AUDIT";
        builder.Options = "-c default_transaction_read_only=on -c statement_timeout=30000";
        builder.CommandTimeout = 35;
        using var connection = new NpgsqlConnection(builder.ConnectionString);
        connection.Open();
        typeof(PostgresDatabase).GetField("_connectionString", Static)!.SetValue(null, builder.ConnectionString);
        typeof(PostgresDatabase).GetField("_initialized", Static)!.SetValue(null, true);
        object Query(string name, string sql)
        {
            using var command = new NpgsqlCommand(sql, connection);
            using var reader = command.ExecuteReader();
            var rows = new List<Dictionary<string, object?>>();
            while (reader.Read())
                rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(reader.GetName,
                    index => reader.IsDBNull(index) ? null : reader.GetValue(index)));
            File.WriteAllText(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(rows, Json));
            Console.WriteLine($"{name}: {JsonSerializer.Serialize(rows, Json)}");
            return rows;
        }
        Query("database", """
            SELECT now() AS server_utc, current_setting('default_transaction_read_only') AS read_only,
            pg_database_size(current_database()) AS database_bytes
            """);
        Query("tables", """
            SELECT relname, n_live_tup, n_dead_tup, pg_total_relation_size(relid) AS total_bytes,
            pg_relation_size(relid) AS heap_bytes, pg_indexes_size(relid) AS index_bytes
            FROM pg_stat_user_tables ORDER BY pg_total_relation_size(relid) DESC LIMIT 20
            """);
        Query("configuration", """
            SELECT key, value FROM app_config WHERE key IN ('recording-settings','plc-settings','ups-settings') ORDER BY key
            """);
        Query("transformer-coverage", """
            WITH recent AS (SELECT * FROM measurement_samples WHERE captured_at >= now()-interval '10 minutes'),
            spacing AS (SELECT *, extract(epoch FROM captured_at-lag(captured_at) OVER
              (PARTITION BY analyzer_id ORDER BY captured_at)) AS gap FROM recent)
            SELECT analyzer_id, count(*) AS rows, min(captured_at) AS first_utc, max(captured_at) AS last_utc,
            extract(epoch FROM now()-max(captured_at)) AS age_seconds,
            min(cardinality(vals)) AS min_channels, max(cardinality(vals)) AS max_channels,
            round(avg(gap)::numeric,3) AS mean_gap_seconds, max(gap) AS max_gap_seconds,
            count(DISTINCT (session_id,cycle_id)) AS distinct_acquisitions,
            count(*) FILTER (WHERE alarm_evidence IS NOT NULL) AS alarm_evidence_rows,
            avg(pg_column_size(spacing)) AS sampled_row_bytes
            FROM spacing GROUP BY analyzer_id ORDER BY analyzer_id
            """);
        Query("transformer-channel-coverage", """
            SELECT analyzer_id, channel, count(*) AS samples,
            count(*) FILTER (WHERE v IS NOT NULL AND v NOT IN ('NaN'::real,'Infinity'::real,'-Infinity'::real)) AS finite_samples,
            min(v) AS minimum, max(v) AS maximum
            FROM measurement_samples CROSS JOIN LATERAL unnest(vals) WITH ORDINALITY AS x(v,channel)
            WHERE captured_at >= now()-interval '5 minutes'
            GROUP BY analyzer_id,channel ORDER BY analyzer_id,channel
            """);
        Query("transformer-duplicates", """
            SELECT 'same timestamp' AS duplicate_kind, count(*) AS duplicate_groups, coalesce(sum(n-1),0) AS extra_rows FROM
            (SELECT count(*) n FROM measurement_samples WHERE captured_at>=now()-interval '24 hours'
             GROUP BY analyzer_id,captured_at HAVING count(*)>1) t
            UNION ALL
            SELECT 'same acquisition',count(*),coalesce(sum(n-1),0) FROM
            (SELECT count(*) n FROM measurement_samples WHERE captured_at>=now()-interval '24 hours'
             GROUP BY analyzer_id,session_id,cycle_id HAVING count(*)>1) t
            """);
        Query("ups-coverage", """
            WITH recent AS (SELECT * FROM ups_samples WHERE sampled_utc>=now()-interval '10 minutes'),
            gaps AS (SELECT *,extract(epoch FROM sampled_utc-lag(sampled_utc) OVER
              (PARTITION BY device_id ORDER BY sampled_utc)) AS gap FROM recent)
            SELECT device_id,quality,count(*) AS rows,min(sampled_utc) AS first_utc,max(sampled_utc) AS last_utc,
            max(acquired_utc) AS last_acquired_utc,extract(epoch FROM now()-max(acquired_utc)) AS source_age_seconds,
            count(DISTINCT acquired_utc) AS distinct_acquisitions,avg(gap) AS mean_gap_seconds,max(gap) AS max_gap_seconds,
            count(*) FILTER (WHERE channel_evidence IS NOT NULL) AS channel_evidence_rows,
            count(*) FILTER (WHERE alarm_evaluation IS NOT NULL) AS alarm_evaluation_rows,
            min(recording_interval_seconds) AS interval_seconds
            FROM gaps GROUP BY device_id,quality ORDER BY device_id,quality
            """);
        Query("ups-latest", """
            SELECT DISTINCT ON (device_id) device_id,sampled_utc,acquired_utc,quality,quality_detail,
            measurement_map,device_model,in_v_reference,out_v_reference,channel_evidence::text,
            output_kw,output_kva,output_pf,in_v_l1,in_v_l2,in_v_l3,out_v_l1,out_v_l2,out_v_l3,
            out_v_ll1,out_v_ll2,out_v_ll3
            FROM ups_samples WHERE sampled_utc>=now()-interval '24 hours' ORDER BY device_id,sampled_utc DESC
            """);
        Query("ups-duplicates", """
            SELECT 'same slot' AS duplicate_kind,count(*) AS duplicate_groups,coalesce(sum(n-1),0) AS extra_rows FROM
            (SELECT count(*) n FROM ups_samples WHERE sampled_utc>=now()-interval '24 hours'
             GROUP BY device_id,sampled_utc HAVING count(*)>1) t
            UNION ALL
            SELECT 'same source acquisition',count(*),coalesce(sum(n-1),0) FROM
            (SELECT count(*) n FROM ups_samples WHERE sampled_utc>=now()-interval '24 hours' AND acquired_utc IS NOT NULL
             GROUP BY device_id,acquired_utc HAVING count(*)>1) t
            """);
        Query("ups-channel-coverage", """
            SELECT device_id,group_name,key,count(*) AS rows,
            count(*) FILTER (WHERE value NOT IN ('null'::jsonb,'[]'::jsonb,'[null,null,null]'::jsonb)) AS present
            FROM ups_samples
            CROSS JOIN LATERAL (VALUES ('Values',channel_evidence->'Values'),('Phases',channel_evidence->'Phases')) g(group_name,channels)
            CROSS JOIN LATERAL jsonb_each(channels) AS x(key,value)
            WHERE sampled_utc>=now()-interval '5 minutes'
            GROUP BY device_id,group_name,key ORDER BY device_id,group_name,key
            """);
        Query("ups-stored-value-consistency", """
            SELECT device_id,count(*) AS checked_values,
            count(*) FILTER (WHERE actual IS DISTINCT FROM expected) AS mismatches
            FROM ups_samples
            CROSS JOIN LATERAL (VALUES
              (out_v_l1,(channel_evidence#>>'{Phases,OutputVoltage,0}')::double precision),
              (out_v_l2,(channel_evidence#>>'{Phases,OutputVoltage,1}')::double precision),
              (out_v_l3,(channel_evidence#>>'{Phases,OutputVoltage,2}')::double precision),
              (in_v_l1,(channel_evidence#>>'{Phases,InputVoltage,0}')::double precision),
              (out_i_l1,(channel_evidence#>>'{Phases,OutputCurrent,0}')::double precision),
              (battery_voltage_v,(channel_evidence#>>'{Values,BatteryVoltage}')::double precision),
              (out_freq_hz,(channel_evidence#>>'{Values,OutputFrequency}')::double precision)
            ) x(actual,expected)
            WHERE sampled_utc>=now()-interval '10 minutes' AND quality='GOOD'
            GROUP BY device_id ORDER BY device_id
            """);
        Query("constraints", """
            SELECT conrelid::regclass::text AS table_name,conname,pg_get_constraintdef(oid) AS definition
            FROM pg_constraint WHERE conrelid IN ('measurement_samples'::regclass,'ups_samples'::regclass,
            'equipment_alarm_history'::regclass) ORDER BY conrelid,conname
            """);
        Query("alarm-duplicates-and-coverage", """
            SELECT 'exact duplicate event' AS category,count(*) AS rows FROM
            (SELECT 1 FROM equipment_alarm_history WHERE observed_utc>=now()-interval '24 hours'
             GROUP BY equipment_type,equipment_id,observed_utc,acquired_utc,event_kind,alarm_key,previous_state,current_state
             HAVING count(*)>1) t
            UNION ALL
            SELECT event_kind,count(*) FROM equipment_alarm_history WHERE observed_utc>=now()-interval '24 hours'
            GROUP BY event_kind
            """);
        Query("storage-rate", """
            SELECT 'measurement_samples' AS table_name,count(*) AS rows, sum(pg_column_size(t)) AS tuple_bytes,
            extract(epoch FROM now()-min(captured_at)) AS elapsed_seconds
            FROM measurement_samples t WHERE captured_at>=now()-interval '10 minutes'
            UNION ALL
            SELECT 'ups_samples',count(*),sum(pg_column_size(t)),extract(epoch FROM now()-min(sampled_utc))
            FROM ups_samples t WHERE sampled_utc>=now()-interval '10 minutes'
            """);
        var configuration = Path.Combine(appDirectory, "EOEMSData", "Configuration");
        var ups = JsonSerializer.Deserialize<UpsSettings>(File.ReadAllText(Path.Combine(configuration, "ups-settings.json")), Json)!;
        typeof(UpsSettingsStore).GetField("_current", Static)!.SetValue(null, ups);
        var recording = RecordingSettingsStore.Load(appDirectory);
        ValidateHistory(connection, recording, output);
        var estimate = EquipmentRawHistory.ReadStorageEstimate(ups, recording.HistorianRecordingIntervalMilliseconds);
        File.WriteAllText(Path.Combine(output, "storage-estimate.json"), JsonSerializer.Serialize(new
        {
            recording.HistorianRecordingIntervalMilliseconds, estimate,
            BytesPerDayWithHeadroom = estimate.EstimateBytes(recording.HistorianRecordingIntervalMilliseconds,
                RecordingSettings.MinimumPlanningWindowDays, 100) / RecordingSettings.MinimumPlanningWindowDays
        }, Json));
        var queues = new List<object>();
        foreach (var item in new[] { ("recording-outbox.db", "recording_outbox"), ("eoems_historian.db", "spool_samples") })
        {
            var path = Path.Combine(appDirectory, "EOEMSData", "Historian", item.Item1);
            if (!File.Exists(path)) throw new FileNotFoundException("Expected recorder queue is missing.", path);
            using var sqlite = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
            sqlite.Open();
            using var count = sqlite.CreateCommand();
            count.CommandText = $"SELECT count(*) FROM {item.Item2}";
            queues.Add(new { File = item.Item1, PendingRows = (long)count.ExecuteScalar()! });
        }
        File.WriteAllText(Path.Combine(output, "recorder-queues.json"), JsonSerializer.Serialize(queues, Json));
        Console.WriteLine("Read-only database audit completed.");
    }

    private static void ValidateHistory(NpgsqlConnection connection, RecordingSettings recording, string output)
    {
        var summary = new List<object>();
        var end = DateTime.UtcNow.AddSeconds(-30);
        var start = end.AddMinutes(-2);
        using var historian = new MeasurementHistorian(Path.Combine(output, "audit-reader.db"),
            recordingSettings: recording, migrateLegacyDatabase: false);
        var transformer = new TransformerHistorySource(historian, () => recording);
        foreach (var asset in transformer.GetEquipment().Where(asset => new[] { "T1", "T2", "T3", "T4", "TOTAL" }.Contains(asset.Id)))
        {
            var metrics = asset.Metrics.Where(metric => MeasurementCatalog.GetStoredIndex(metric.Id) >= 0).ToArray();
            var rows = transformer.Read(new EquipmentHistoryRequest
                { Equipment = asset, Metrics = metrics, FromUtc = start, ToUtc = end, RawOnly = true }, default);
            var expected = new Dictionary<(DateTime, string), double?>();
            using (var command = new NpgsqlCommand("SELECT captured_at,vals FROM measurement_samples WHERE analyzer_id=@id AND captured_at>=@start AND captured_at<@end", connection))
            {
                command.Parameters.AddWithValue("id", asset.SourceKey);
                command.Parameters.AddWithValue("start", start);
                command.Parameters.AddWithValue("end", end);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var values = reader.GetFieldValue<float[]>(1);
                    foreach (var metric in metrics)
                    {
                        var value = values[MeasurementCatalog.GetStoredIndex(metric.Id)];
                        if (float.IsNaN(value)) continue;
                        expected[(reader.GetDateTime(0), metric.Id)] = float.IsFinite(value) ? value : null;
                    }
                }
            }
            var mismatches = rows.Count(row => !expected.TryGetValue((row.TimestampUtc, row.MetricId), out var value) ||
                value.HasValue != row.Value.HasValue || (value.HasValue && Math.Abs(value.Value - row.Value!.Value) > 0.00001));
            if (mismatches > 0 || expected.Count != rows.Count)
                throw new InvalidDataException($"{asset.Id}: history does not match stored measurement values/count " +
                    $"({mismatches} mismatches, {rows.Count} returned, {expected.Count} stored; {start:o} to {end:o}).");
            summary.Add(new { asset.Id, Measurements = metrics.Length, Rows = rows.Count, DirectDatabaseValues = expected.Count, Mismatches = mismatches });
        }
        var ups = new UpsEquipmentHistorySource();
        typeof(UpsEquipmentHistorySource).GetField("_schemaConnectionString", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(ups, connection.ConnectionString);
        foreach (var asset in ups.GetEquipment().Where(asset => !asset.IsAggregate))
        {
            var rows = ups.Read(new EquipmentHistoryRequest
                { Equipment = asset, Metrics = asset.Metrics, FromUtc = start, ToUtc = end, RawOnly = true }, default);
            var duplicates = rows.GroupBy(row => (row.TimestampUtc, row.MetricId)).Count(group => group.Count() > 1);
            if (duplicates > 0) throw new InvalidDataException($"{asset.Id}: history projection duplicated channel observations.");
            var stored = new Dictionary<DateTime, JsonDocument>();
            try
            {
                using (var command = new NpgsqlCommand("SELECT sampled_utc,to_jsonb(t)::text FROM ups_samples t WHERE device_id=@id AND sampled_utc>=@start AND sampled_utc<@end", connection))
                {
                    command.Parameters.AddWithValue("id", asset.SourceKey);
                    command.Parameters.AddWithValue("start", start);
                    command.Parameters.AddWithValue("end", end);
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) stored.Add(reader.GetDateTime(0), JsonDocument.Parse(reader.GetString(1)));
                }
                var checkedValues = 0;
                var mismatches = 0;
                foreach (var row in rows.Where(row => !row.MetricId.StartsWith("out_kva_l", StringComparison.Ordinal)))
                {
                    var root = stored[row.TimestampUtc].RootElement;
                    double? expected = null;
                    if (root.GetProperty("quality").GetString()!.Equals("GOOD", StringComparison.OrdinalIgnoreCase))
                    {
                        JsonElement value;
                        if (row.MetricId.StartsWith("channel:", StringComparison.Ordinal))
                        {
                            var parts = row.MetricId.Split(':');
                            var phase = parts.Length == 3;
                            if (root.GetProperty("channel_evidence").TryGetProperty(phase ? "Phases" : "Values", out var group) &&
                                group.TryGetProperty(parts[1], out value))
                            {
                                if (phase)
                                {
                                    var index = int.Parse(parts[2][1..]) - 1;
                                    value = value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > index ? value[index] : default;
                                }
                                if (value.ValueKind == JsonValueKind.Number) expected = value.GetDouble();
                            }
                        }
                        else if (root.TryGetProperty(row.MetricId, out value) && value.ValueKind == JsonValueKind.Number)
                            expected = value.GetDouble();
                    }
                    checkedValues++;
                    if (expected.HasValue != row.Value.HasValue ||
                        (expected.HasValue && Math.Abs(expected.Value - row.Value!.Value) > 0.00001)) mismatches++;
                }
                if (mismatches > 0 || rows.Count != stored.Count * asset.Metrics.Count)
                    throw new InvalidDataException($"{asset.Id}: UPS history differs from stored channel evidence/count ({mismatches} mismatches).");
                summary.Add(new { asset.Id, Measurements = asset.Metrics.Count, Rows = rows.Count,
                    NumericValues = rows.Count(row => row.Value.HasValue), CheckedValues = checkedValues,
                    Mismatches = mismatches, DuplicateChannelRows = duplicates });
            }
            finally
            {
                foreach (var document in stored.Values) document.Dispose();
            }
        }
        File.WriteAllText(Path.Combine(output, "history-adapter-verification.json"), JsonSerializer.Serialize(summary, Json));
        Console.WriteLine("Actual history adapters verified against PostgreSQL without schema writes.");
    }

    internal static void RunVoltageReport(string appDirectory, DateTime from, DateTime until, int thinSeconds, string pdf, string output)
    {
        if (from.Kind != DateTimeKind.Utc || until.Kind != DateTimeKind.Utc || until <= from || thinSeconds < 0)
            throw new ArgumentException("Choose a positive UTC period and nonnegative thinning interval.");
        Directory.CreateDirectory(output);
        var builder = Connection(appDirectory);
        builder.ApplicationName = "EOEMS_READ_ONLY_REPORT_REVIEW";
        builder.Options = "-c default_transaction_read_only=on -c statement_timeout=120000";
        builder.CommandTimeout = 125;
        using var connection = new NpgsqlConnection(builder.ConnectionString);
        connection.Open();
        typeof(PostgresDatabase).GetField("_connectionString", Static)!.SetValue(null, builder.ConnectionString);
        typeof(PostgresDatabase).GetField("_initialized", Static)!.SetValue(null, true);
        var recording = RecordingSettingsStore.Load(appDirectory);
        using var historian = new MeasurementHistorian(Path.Combine(output, "report-reader.db"),
            recordingSettings: recording, migrateLegacyDatabase: false);
        var source = new TransformerHistorySource(historian, () => recording);
        var assets = source.GetEquipment().Where(asset => new[] { "T1", "T2", "T3", "T4", "TOTAL" }.Contains(asset.Id)).ToArray();
        var metrics = new List<HistoryMetric>();
        var rows = new List<EquipmentHistoryRow>();
        foreach (var asset in assets)
        {
            var selected = asset.Metrics.Where(metric => new[] { "V_L1_N", "V_L2_N", "V_L3_N" }.Contains(metric.Id)).ToArray();
            if (selected.Length != 3) throw new InvalidDataException($"{asset.Id}: expected the three stored L-N voltage channels.");
            var found = source.Read(new EquipmentHistoryRequest
            {
                Equipment = asset, Metrics = selected, FromUtc = from, ToUtc = until, RawOnly = true, ThinSeconds = thinSeconds
            }, default);
            foreach (var metric in selected)
                metrics.Add(new HistoryMetric
                {
                    Id = asset.Id + "|" + metric.Id, Name = asset.Id + " | " + metric.Name, SourceMetricName = metric.Name,
                    EquipmentId = asset.Id, SourceId = asset.SourceId, SourceMetricId = metric.Id, Unit = metric.Unit,
                    Basis = metric.Basis, RatedCapacityKva = metric.RatedCapacityKva, CapacityBasis = metric.CapacityBasis
                });
            foreach (var row in found)
            {
                row.MetricId = asset.Id + "|" + row.MetricId;
                row.EquipmentId = asset.Id;
                rows.Add(row);
            }
            Console.WriteLine($"{asset.Id}: {found.Count} saved channel observations.");
        }
        if (rows.Count > EquipmentHistoryService.MaximumRows)
            throw new InvalidDataException("The selected report exceeds the existing history observation limit.");
        var result = new EquipmentHistoryResult
        {
            Request = new EquipmentHistoryRequest
            {
                Equipment = new HistoryEquipment { Id = "__ALL_EQUIPMENT__", Name = "All equipment", Category = "Combined history", SourceId = "combined" },
                IncludedEquipment = assets, IsCombined = true, RawOnly = true, Metrics = metrics,
                FromUtc = from, ToUtc = until, ThinSeconds = thinSeconds
            },
            Rows = rows
        };
        var analysis = EquipmentAnalysis.Build(result);
        File.WriteAllText(Path.Combine(output, "regenerated-study.json"), JsonSerializer.Serialize(new
        {
            FromUtc = from, ToUtc = until, ThinSeconds = thinSeconds, Channels = metrics.Count, Rows = rows.Count,
            analysis.Completeness, analysis.ObservedCoveragePercent,
            Scope = analysis.Scope.Select(row => new { row.Name, row.Samples }),
            Statistics = analysis.Channels.Select(channel => new { channel.Metric.Name, Count = channel.Points.Count,
                Minimum = channel.Points.Min(point => point.V), Mean = channel.Points.Average(point => point.V),
                Maximum = channel.Points.Max(point => point.V), channel.Median, channel.P95, channel.StdDev,
                FirstUtc = channel.Points.First().T, LastUtc = channel.Points.Last().T, channel.ObservedCoveragePercent }),
            TimedFindings = analysis.Findings.Where(finding => finding.Severity >= 1 && finding.StartUtc.HasValue)
        }, Json));
        HistorianReportExporter.ExportEquipmentHistory(result, pdf, "PDF");
        Console.WriteLine("Read-only saved-data PDF generated: " + pdf);
    }

    internal static void RunComparisonReport(string appDirectory, DateTime from, DateTime until, string pdf, string output)
    {
        if (from.Kind != DateTimeKind.Utc || until.Kind != DateTimeKind.Utc || until <= from)
            throw new ArgumentException("Choose a positive UTC period.");
        Directory.CreateDirectory(output);
        var builder = Connection(appDirectory);
        builder.ApplicationName = "EOEMS_READ_ONLY_COMPARISON_REPORT_REVIEW";
        builder.Options = "-c default_transaction_read_only=on -c statement_timeout=120000";
        builder.CommandTimeout = 125;
        using var connection = new NpgsqlConnection(builder.ConnectionString);
        connection.Open();
        typeof(PostgresDatabase).GetField("_connectionString", Static)!.SetValue(null, builder.ConnectionString);
        typeof(PostgresDatabase).GetField("_initialized", Static)!.SetValue(null, true);
        var recording = RecordingSettingsStore.Load(appDirectory);
        using var historian = new MeasurementHistorian(Path.Combine(output, "report-reader.db"),
            recordingSettings: recording, migrateLegacyDatabase: false);
        var source = new TransformerHistorySource(historian, () => recording);
        var equipment = source.GetEquipment().Single(asset => asset.Id == "T1");
        var selected = equipment.Metrics.Where(metric => new[] { "V_L1_N", "AVG_CURRENT", "PTOT_KW", "TDEGC_C" }.Contains(metric.Id)).ToArray();
        if (selected.Length != 4) throw new InvalidDataException("Expected voltage, average current, power and temperature channels.");
        var request = new EquipmentHistoryRequest { Equipment = equipment, Metrics = selected, FromUtc = from,
            ToUtc = until, RawOnly = true, ThinSeconds = 0 };
        var result = new EquipmentHistoryResult { Request = request, Rows = source.Read(request, default) };
        var study = ChannelComparisonStudy.Create(result, selected.Single(metric => metric.Id == "PTOT_KW"),
            selected.Single(metric => metric.Id == "TDEGC_C"), ChannelComparisonStyle.MeanResponse, true);
        var stats = result.Statistics;
        File.WriteAllText(Path.Combine(output, "comparison-source-evidence.json"), JsonSerializer.Serialize(new {
            FromUtc = from, ToUtc = until, Observations = result.Rows.Count, study.ModeName,
            X = study.X.ToString(), Y = study.Y.ToString(), MatchedPairs = study.Pairs.Length,
            study.CandidateAcquisitions, study.ExcludedAcquisitions,
            Statistics = stats.Select(stat => new { stat.Metric.Name, stat.ValidCount, stat.MissingCount, stat.Minimum,
                stat.Mean, stat.Maximum, stat.EnergyKwh, stat.IntegratedSeconds }),
            Response = ChannelComparisonBin.Build(study.Pairs)
        }, Json));
        HistorianReportExporter.ExportEquipmentHistory(result, pdf, "PDF", study);
        Console.WriteLine($"Read-only T1 comparison: {result.Rows.Count} channel observations, {study.Pairs.Length} pairs, {study.ExcludedAcquisitions} excluded groups.");
    }

    private static NpgsqlConnectionStringBuilder Connection(string appDirectory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(appDirectory, "appsettings.json")),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var builder = new NpgsqlConnectionStringBuilder(document.RootElement.GetProperty("Postgres").GetProperty("ConnectionString").GetString());
        var path = Path.Combine(appDirectory, "data", "postgres-connection.json");
        if (File.Exists(path))
        {
            using var overrides = JsonDocument.Parse(File.ReadAllText(path));
            var root = overrides.RootElement;
            if (root.TryGetProperty("host", out var host)) builder.Host = host.GetString();
            if (root.TryGetProperty("port", out var port)) builder.Port = port.GetInt32();
            if (root.TryGetProperty("database", out var database)) builder.Database = database.GetString();
            if (root.TryGetProperty("username", out var username)) builder.Username = username.GetString();
            if (root.TryGetProperty("password", out var password) && !string.IsNullOrEmpty(password.GetString()))
                builder.Password = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(password.GetString()!),
                    null, DataProtectionScope.CurrentUser));
        }
        var secretPath = Path.Combine(appDirectory, "data", "pg-service.secret");
        if (File.Exists(secretPath))
        {
            using var secret = JsonDocument.Parse(File.ReadAllText(secretPath));
            builder.Username = PostgresServiceAccount.RoleName;
            builder.Password = (string)typeof(PostgresServiceAccount).GetMethod("Unprotect", Static)!
                .Invoke(null, new object[] { secret.RootElement.GetProperty("current").GetString()! })!;
        }
        return builder;
    }
}
