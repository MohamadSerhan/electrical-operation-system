using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using System.Xml.Linq;
using EOEMS.PowerTransformersScadaUI.V5;
using Microsoft.Data.Sqlite;
using Npgsql;

internal static class EquipmentHistoryValidation
{
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly DateTime Epoch = new(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
    private static int assertions;

    internal static void RunStorageCadence()
    {
        var method = typeof(EquipmentRawHistory).GetMethod("SustainedRowsPerSecond", PrivateStatic)!;
        double Rate(params double[] gaps) => (double)method.Invoke(null, new object[] { gaps })!;
        Check(Math.Abs(Rate(Enumerable.Repeat(60.0, 62).Append(3600).ToArray()) - 1.0 / 60) < 0.000001,
            "UPS storage planning preserves minute cadence despite application downtime");
        Check(Math.Abs(Rate(1.09, 1.1, 1.11, 3600, 1.08) - 1.0 / 1.1) < 0.000001,
            "transformer planning uses recent acquisition spacing, not uptime diluted by outages");
        Check(Rate(1, 1, 10, 10, 10) == 0.1, "sustained slower cadence remains slower, not replaced by fastest response");
        Check(Math.Abs(Rate(1, 2) - 1 / 1.5) < 0.000001, "even sample counts use the median pair");
        Check(Rate(0, -1, double.NaN, double.PositiveInfinity) == 0 && Rate() == 0,
            "invalid or absent cadence evidence cannot produce invented throughput");
        Console.WriteLine($"Storage cadence assertions passed: {assertions}.");
    }

    internal static void RunChannelLayout(string evidence)
    {
        Directory.CreateDirectory(evidence);
        using var view = new EquipmentHistoryView(new EquipmentHistoryService(new[] { new FixtureSource() }));
        using var form = new Form { Location = new Point(-2400, -1800), StartPosition = FormStartPosition.Manual };
        form.Controls.Add(view);
        form.Show();
        var choices = Field<CheckedListBox>(view, "_metric");
        foreach (var name in new[] { "Alarm / Fault", "Voltage L1-N [V]", "Voltage L2-N [V]", "Voltage L3-N [V]",
            "Voltage L1-L2 [V]", "Voltage L2-L3 [V]", "Voltage L3-L1 [V]", "Frequency [Hz]", "Active Power [kW]" })
            choices.Items.Add(name + " - Transformers");
        foreach (var size in new[] { new Size(980, 600), new Size(1160, 680), new Size(1560, 900) })
        foreach (var showTrend in new[] { false, true })
        {
            form.ClientSize = size;
            var trend = Field<Control>(view, "_trendField");
            trend.Visible = showTrend;
            Pump();
            var search = Field<TextBox>(view, "_channelSearch");
            var kind = Field<ComboBox>(view, "_channelKind");
            var metric = Field<CheckedListBox>(view, "_metric");
            var select = Field<CheckBox>(view, "_allChannels");
            var clear = Field<Button>(view, "_clearChannels");
            Rectangle Screen(Control control) => control.RectangleToScreen(control.ClientRectangle);
            foreach (var control in new Control[] { search, kind, select, clear, metric })
                Check(Screen(control.Parent!).Contains(Screen(control)), "channel controls are fully contained, not clipped by fixed-height rows");
            Check(!Screen(select).IntersectsWith(Screen(clear)) && Screen(clear).Bottom <= Screen(metric).Top,
                "Select visible, Clear and channel checkboxes have separate non-overlapping rows");
            Check(metric.RectangleToScreen(metric.GetItemRectangle(0)).Top >= Screen(clear).Bottom,
                "first channel checkbox begins below the fully visible Clear button");
            if (showTrend)
            {
                var combo = Field<ComboBox>(view, "_trendMetric");
                var label = trend.Controls.OfType<Label>().Single();
                Check(Screen(trend).Contains(Screen(combo)) && Screen(label).Bottom <= Screen(combo).Top &&
                    Screen(trend).Bottom <= Screen(clear.Parent!).Top,
                    "wrapped trend caption, dropdown and Clear row remain separated");
            }
            Check(metric.Height >= 120, "channel list retains usable space after sizing header rows");
            using var bitmap = new Bitmap(view.Width, view.Height);
            view.DrawToBitmap(bitmap, view.ClientRectangle);
            bitmap.Save(Path.Combine(evidence, $"channels-{size.Width}-{showTrend}.png"));
        }
        Console.WriteLine($"History channel layout assertions passed: {assertions}.");
    }

    internal static void RunTrendDrawing()
    {
        using var chart = new AnalyticsTrendChart
        {
            Size = new Size(1100, 420), ShowMarkers = true, PollingIntervalSeconds = 1,
            WindowStartUtc = Epoch, WindowEndUtc = Epoch.AddSeconds(60000)
        };
        chart.CreateControl();
        var plot = (RectangleF)typeof(AnalyticsTrendChart).GetMethod("PlotRect", Instance)!.Invoke(chart, null)!;
        int ColoredColumns(Bitmap image, double from, double to)
        {
            var count = 0;
            for (var x = (int)(plot.Left + plot.Width * from); x < plot.Left + plot.Width * to; x++)
            {
                for (var y = (int)plot.Top + 2; y < plot.Bottom - 2; y++)
                {
                    var pixel = image.GetPixel(x, y);
                    if (pixel.R > 180 && pixel.G < 120 && pixel.B > 180)
                    {
                        count++;
                        break;
                    }
                }
            }
            return count;
        }
        Bitmap Draw(IReadOnlyList<AnalyticsTrendPoint> points, double interval)
        {
            chart.Series = new[]
            {
                new AnalyticsTrendSeries { Name = "Voltage", Color = Color.Magenta, Points = points,
                    ExpectedIntervalSeconds = interval }
            };
            var bitmap = new Bitmap(chart.Width, chart.Height);
            chart.DrawToBitmap(bitmap, chart.ClientRectangle);
            return bitmap;
        }
        var dense = Enumerable.Range(0, 60000).Select(index => new AnalyticsTrendPoint
        {
            TimestampUtc = Epoch.AddSeconds(index), Value = 230, IsValid = true
        }).ToArray();
        foreach (var type in new[] { AnalyticsChartType.Line, AnalyticsChartType.StepLine, AnalyticsChartType.Area })
        {
            chart.ChartType = type;
            using var image = Draw(dense, 1);
            Check(ColoredColumns(image, 0.1, 0.9) > plot.Width * 0.75,
                $"{type}: reduced long-period readings remain a continuous visible trace");
        }
        chart.ChartType = AnalyticsChartType.Line;
        var gap = dense.Where(point => point.TimestampUtc < Epoch.AddSeconds(20000) ||
            point.TimestampUtc > Epoch.AddSeconds(40000)).ToArray();
        using (var image = Draw(gap, 1))
        {
            Check(ColoredColumns(image, 0.4, 0.6) == 0,
                "render reduction never draws a line across a real acquisition outage");
            Check(ColoredColumns(image, 0.05, 0.25) > plot.Width * 0.18,
                "valid recordings remain visible on either side of an outage");
        }
        var invalid = dense.Select((point, index) => index == 30000
            ? new AnalyticsTrendPoint { TimestampUtc = point.TimestampUtc, IsValid = false }
            : point).ToArray();
        using (var image = Draw(invalid, 1))
            Check(ColoredColumns(image, 0.1, 0.4) > plot.Width * 0.28,
                "one invalid reading does not hide the rest of a long-period chart");
        chart.WindowEndUtc = Epoch.AddSeconds(10459 * 23);
        var thinned = Enumerable.Range(0, 10459).Select(index => new AnalyticsTrendPoint
        {
            TimestampUtc = Epoch.AddSeconds(index * 23), Value = 230, IsValid = true
        }).ToArray();
        using (var image = Draw(thinned, 23))
            Check(ColoredColumns(image, 0.1, 0.9) > plot.Width * 0.75,
                "10,459 readings at Auto's 23-second cadence are visible instead of a blank chart");
        chart.WindowEndUtc = Epoch.AddSeconds(300 * 30);
        var isolated = Enumerable.Range(0, 300).Select(index => new AnalyticsTrendPoint
        {
            TimestampUtc = Epoch.AddSeconds(index * 30), Value = 230, IsValid = true
        }).ToArray();
        using (var image = Draw(isolated, 1))
            Check(ColoredColumns(image, 0.1, 0.9) > plot.Width * 0.4,
                "more than 120 isolated readings are drawn as visible markers without inventing connecting lines");
        Console.WriteLine($"History trend drawing assertions passed: {assertions}.");
    }

    internal static void Run(string evidence, bool storageOnly = false)
    {
        Console.WriteLine("Preparing disposable PostgreSQL history fixture.");
        Directory.CreateDirectory(evidence);
        var temporary = Path.Combine(Path.GetTempPath(), "eoems-equipment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var pg = @"C:\Program Files\PostgreSQL\18\bin";
        var data = Path.Combine(temporary, "postgres");
        var started = false;
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            ProcessRun(Path.Combine(pg, "initdb.exe"), "-D", data, "-U", "isolated_qa", "-A", "trust", "--no-locale", "--encoding=UTF8");
            ProcessRun(Path.Combine(pg, "pg_ctl.exe"), "-D", data, "-l", Path.Combine(temporary, "postgres.log"),
                "-o", $"-h 127.0.0.1 -p {port} -F", "-w", "start");
            started = true;
            var connectionString = $"Host=127.0.0.1;Port={port};Database=postgres;Username=isolated_qa;Pooling=false;Timeout=2";
            typeof(PostgresDatabase).GetField("_connectionString", PrivateStatic)!.SetValue(null, connectionString);
            typeof(PostgresDatabase).GetField("_initialized", PrivateStatic)!.SetValue(null, true);
            var settings = UpsSettings.CreateDefault();
            settings.Devices = settings.Devices.Take(2).Select(device => device.Clone()).ToList();
            settings.HistorianIntervalSeconds = 60;
            typeof(UpsSettingsStore).GetField("_current", PrivateStatic)!.SetValue(null, settings);
            using (var connection = PostgresDatabase.Open())
            {
                using var command = connection.CreateCommand();
                command.CommandText = (string)typeof(PostgresDatabase).GetField("SchemaSql", PrivateStatic)!.GetValue(null)!;
                command.ExecuteNonQuery();
                UpsHistorian.EnsureHistorySchema(connection);
                command.CommandText = "INSERT INTO ups_samples(device_id,sampled_utc,quality,output_kw) VALUES('UPS-LEGACY',@t,'GOOD',999); " +
                    "ALTER TABLE ups_samples DROP COLUMN acquired_utc, DROP COLUMN device_label, DROP COLUMN device_model, DROP COLUMN bank_members," +
                    " DROP COLUMN fleet_members, DROP COLUMN alarm_evidence, DROP COLUMN channel_evidence, DROP COLUMN recording_interval_seconds;";
                command.Parameters.AddWithValue("t", Epoch.AddDays(-1));
                command.ExecuteNonQuery();
                UpsHistorian.EnsureHistorySchema(connection);
                command.CommandText = "SELECT count(*) FROM ups_samples WHERE device_id='UPS-LEGACY' AND acquired_utc IS NULL AND measurement_revision=1";
                Check((long)command.ExecuteScalar()! == 1, "additive schema migration preserves older rows without fabricating source evidence");
                SeedUps(connection, settings);
                SeedTransformers(connection);
            }
            var recording = new RecordingSettings { ExportFolderPath = temporary };
            recording.Normalize();
            using var historian = new MeasurementHistorian(Path.Combine(temporary, "history.db"), recordingSettings: recording, migrateLegacyDatabase: false);
            if (storageOnly)
            {
                SingleSourceRecording(EquipmentHistoryService.Create(historian, recording), settings);
                using var connection = PostgresDatabase.Open();
                using var command = new NpgsqlCommand("INSERT INTO ups_samples(device_id,sampled_utc,quality) VALUES(@id,now(),'GOOD')", connection);
                command.Parameters.AddWithValue("id", settings.Devices[0].Id);
                command.ExecuteNonQuery();
                var sample = typeof(EquipmentRawHistory).GetMethod("ReadStorageSample", PrivateStatic)!.Invoke(null,
                    new object?[] { connection, "ups_samples", "device_id=@id", "sampled_utc", settings.Devices[0].Id, null, null, 1440 })!;
                var rate = (double)sample.GetType().GetProperty("RowsPerSecond")!.GetValue(sample)!;
                Check(Math.Abs(rate - 1) < 0.000001,
                    "actual PostgreSQL storage sample keeps sustained cadence after a multi-minute recording gap");
                Console.WriteLine($"Storage integration assertions passed: {assertions}.");
                return;
            }
            DailyExportReusesExistingReport(historian, recording, temporary);
            var service = EquipmentHistoryService.Create(historian, recording);
            var equipment = service.GetEquipment();
            Check(equipment.Count(asset => asset.Category == "Transformers") == 4, "all transformer assets appear");
            Check(equipment.Count(asset => asset.Category == "UPS") == 2, "UPS registry feeds equipment catalog");
            Check(equipment.Count(asset => asset.Category == "ATS banks (derived)") == settings.Banks.Count, "ATS bank assets are explicitly derived");
            Check(equipment.Single(asset => asset.Id == "GENERATORS").IsConnected == false, "generators have explicit no-source status");
            var ups = equipment.Single(asset => asset.Id == settings.Devices[0].Id);
            var metric = ups.Metrics.Single(metric => metric.Id == "output_kw");
            var request = Request(ups, metric);
            var result = service.Read(request, default);
            Check(result.Rows.Count == 5, "UPS SQL read loads all saved slots, including failure and legacy mapping");
            Check(result.Rows[0].Value == 100 && result.Rows[1].Value == 200, "UPS values round-trip via actual PostgreSQL write/read");
            Check(result.Rows[2].Quality == "TIMEOUT" && !result.Rows[2].Value.HasValue, "failed polls retain quality and null measurements");
            Check(result.Rows[3].Quality == "Legacy mapping excluded" && !result.Rows[3].Value.HasValue, "pre-correction UPS mapping excluded explicitly");
            Check(result.Rows[0].AcquiredUtc == Epoch.AddSeconds(-2), "actual poll timestamp retained separately from bucket");
            Check(result.Rows[0].Evidence.Contains("synthetic QA alarm") && result.Rows[0].Evidence.Contains("Output voltage"), "alarm and voltage-reference evidence survive persistence");
            var statistics = result.Statistics.Single();
            Check(statistics.ValidCount == 3 && statistics.MissingCount == 2, "analysis counts valid and excluded observations separately");
            Check(statistics.Minimum == 100 && statistics.Maximum == 500, "minimum and sampled peak are exact");
            Check(Math.Abs(statistics.EnergyKwh!.Value - 2.5) < 1e-8 && statistics.IntegratedSeconds == 60, "energy integrates only consecutive good source evidence");
            var all = service.Read(new EquipmentHistoryRequest { Equipment = ups, Metrics = ups.Metrics, FromUtc = Epoch, ToUtc = Epoch.AddMinutes(5) }, default);
            Check(all.Rows.Count == ups.Metrics.Count * 5, "all persisted UPS numeric columns are accessible through one bounded query");
            Check(all.Rows.Single(row => row.MetricId == "battery_status" && row.TimestampUtc == Epoch).Value == 2, "smallint status columns are projected safely");
            Check(all.Statistics.Single(stat => stat.Metric.Id == "battery_status").Mean == null, "state codes never receive arithmetic means");
            Check(all.Rows.Single(row => row.MetricId == "battery_charge_pct" && row.TimestampUtc == Epoch.AddMinutes(3)).Value == 95,
                "legacy battery records unaffected by the electrical mapping correction remain usable");
            Check(all.Rows.Single(row => row.MetricId == "channel:InputPower:L1" && row.TimestampUtc == Epoch).Value == 1000,
                "all vendor/standard phase channels have usable history, not only the old trend's three columns");
            Check(all.Rows.Single(row => row.MetricId == "channel:DcBusVoltage" && row.TimestampUtc == Epoch).Value == null,
                "nonfinite numeric evidence is explicitly null and does not stop historian writes");
            Check(all.Rows.Single(row => row.MetricId == "channel:InputPower:L3" && row.TimestampUtc == Epoch).Value == null,
                "nonfinite phase evidence is explicitly null");
            Check(all.Rows.Single(row => row.MetricId == "channel:InputPower:L2" && row.TimestampUtc == Epoch).Value == 0 &&
                all.Rows.Single(row => row.MetricId == "in_i_l1" && row.TimestampUtc == Epoch).Value == 0 &&
                all.Rows.Single(row => row.MetricId == "in_v_l1" && row.TimestampUtc == Epoch).Value == 0,
                "actual PostgreSQL round-trip preserves measured zero input W, A and V");
            Check(all.Rows.Single(row => row.MetricId == "channel:InputPower:L3" && row.TimestampUtc == Epoch).Quality.Contains("non-finite fixture"),
                "actual PostgreSQL round-trip retains per-channel invalid-value evidence, not zero or generic NoData");
            Check(all.Rows.Single(row => row.MetricId == "out_w_l1" && row.TimestampUtc == Epoch).Value == 50000 &&
                ups.Metrics.Single(metric => metric.Id == "out_w_l1").Unit == "W", "stored phase power uses its actual W unit");
            Check(all.Rows.Single(row => row.MetricId == UpsInputEstimates.VoltageKey && row.TimestampUtc == Epoch).Value == 0 &&
                all.Rows.Single(row => row.MetricId == UpsInputEstimates.KvaKey && row.TimestampUtc == Epoch).Value == 0,
                "Actual PostgreSQL estimate round-trip preserves zero values separately from native measurements");
            Check(all.Rows.Single(row => row.MetricId == UpsInputEstimates.KvaKey && row.TimestampUtc == Epoch).EnergyEligible == false &&
                all.Statistics.Single(stat => stat.Metric.Id == UpsInputEstimates.KvaKey).EnergyKwh == null,
                "Recorded estimates never enter energy accounting");
            Check(service.GetRawEquipment().Single(asset => asset.Id == ups.Id).Metrics.Count(metric => metric.IsEstimate) == 2,
                "Equipment History's actual channel picker exposes both separately labeled recorded estimates");
            using (var connection = PostgresDatabase.Open())
            using (var command = new NpgsqlCommand("UPDATE ups_samples SET channel_evidence=jsonb_set(channel_evidence, '{Estimates,Version}', '1'::jsonb) WHERE device_id=@id AND sampled_utc=@t", connection))
            {
                command.Parameters.AddWithValue("id", settings.Devices[0].Id);
                command.Parameters.AddWithValue("t", Epoch);
                command.ExecuteNonQuery();
            }
            Check(service.Read(Request(ups, ups.Metrics.Single(metric => metric.Id == UpsInputEstimates.KvaKey)), default).
                Rows.Single(row => row.TimestampUtc == Epoch).Value == 0,
                "Actual historian still reads separately stored version-1 estimates after the version-2 L-N method upgrade");
            using (var connection = PostgresDatabase.Open())
            using (var command = new NpgsqlCommand("UPDATE ups_samples SET channel_evidence=channel_evidence - 'Estimates' WHERE device_id=@id AND sampled_utc=@t", connection))
            {
                command.Parameters.AddWithValue("id", settings.Devices[0].Id);
                command.Parameters.AddWithValue("t", Epoch.AddMinutes(4));
                command.ExecuteNonQuery();
            }
            var estimatedHistory = service.Read(Request(ups, ups.Metrics.Single(metric => metric.Id == UpsInputEstimates.KvaKey)), default);
            Check(estimatedHistory.Rows.Single(row => row.TimestampUtc == Epoch.AddMinutes(4)).Value == null &&
                estimatedHistory.Rows.Single(row => row.TimestampUtc == Epoch.AddMinutes(4)).Quality.Contains("not recorded"),
                "Old acquisitions without estimate evidence remain unavailable instead of being silently backfilled");
            var bank = equipment.Single(asset => asset.Id == "ATS-BANK-1");
            var bankResult = service.Read(Request(bank, bank.Metrics[0]), default);
            Check(bankResult.Rows[0].Value == 400 && bankResult.Rows[0].ExpectedMembers == 2, "bank total sums complete saved membership");
            Check(bankResult.Rows[1].Quality == "Incomplete cohort" && bankResult.Rows[1].Value == null, "missing member cannot become a partial total");
            Check(bankResult.Rows[4].Quality == "Membership unknown", "legacy rows with no cohort evidence do not invent bank totals");
            var fleet = equipment.Single(asset => asset.Id == "UPS-FLEET-TOTAL");
            Check(service.Read(Request(fleet, fleet.Metrics[0]), default).Rows[0].Value == 400, "UPS fleet total is separate from transformer total");
            var transformer = equipment.Single(asset => asset.Id == "T1");
            var transformerResult = service.Read(Request(transformer, transformer.Metrics.Single(m => m.Id == "PTOT_KW")), default);
            Check(transformerResult.Rows.Count == 3 && transformerResult.Rows[0].Value == 10, "transformer history uses the shared workspace with its existing storage");
            Check(transformerResult.Rows.All(row => row.Evidence.Contains("cycle") && row.Evidence.Contains("session")), "transformer source/cycle identity retained");
            Check(transformerResult.Rows.All(row => !row.EnergyEligible) && transformerResult.Statistics.Single().EnergyKwh == null,
                "minute quality with known failed cycles conservatively blocks energy without discarding good measurements");
            var archive = equipment.Single(asset => asset.Id == "T1:15MIN");
            var archiveRequest = new EquipmentHistoryRequest
            {
                Equipment = archive, Metrics = archive.Metrics.Where(metric => metric.Id.StartsWith("PTOT_KW:")).ToArray(),
                FromUtc = Epoch, ToUtc = Epoch.AddMinutes(15)
            };
            var archivedRows = service.Read(archiveRequest, default);
            Check(archivedRows.Rows.Count == 4 && archivedRows.Rows.Single(row => row.MetricId == "PTOT_KW:mean").Value == 15,
                "retained 15-minute archive has exact weighted-within-bucket mean");
            Check(archivedRows.Rows.Single(row => row.MetricId == "PTOT_KW:count").Value == 3 &&
                archivedRows.Statistics.All(stat => stat.EnergyKwh == null), "archive exposes sample counts and never invents energy");
            archiveRequest.FromUtc = Epoch.AddSeconds(1);
            Check(service.Read(archiveRequest, default).Rows.Count == 0, "archive never clips whole-bucket aggregates into a misleading partial-window value");
            var discovered = service.DiscoverRecordedEquipment(default);
            var retired = discovered.Single(asset => asset.Id == "UPS-RETIRED");
            var retiredRequest = Request(retired, retired.Metrics.Single(metric => metric.Id == "output_kw"));
            retiredRequest.ToUtc = Epoch.AddMinutes(7);
            var retiredResult = service.Read(retiredRequest, default);
            Check(retired.Category == "Archived UPS" && retiredResult.Rows.Single().Value == 70, "removed UPS registrations remain discoverable and reportable by stable ID");
            var legacy = discovered.Single(asset => asset.Id == "UPS-LEGACY");
            var legacyRequest = Request(legacy, legacy.Metrics.Single(metric => metric.Id == "output_kw"));
            legacyRequest.FromUtc = Epoch.AddDays(-2);
            Check(service.Read(legacyRequest, default).Rows.Single().Quality == "Legacy mapping excluded", "legacy NULL metadata remains readable and excluded rather than crashing");
            var forged = Request(ups, new HistoryMetric { Id = "output_kw", Name = "wrong", Unit = "wrong", IsPower = false });
            Check(service.Read(forged, default).Statistics.Single().EnergyKwh == 2.5 &&
                service.Read(forged, default).Request.Metrics.Single().Unit == "kW", "requests use canonical source metric units and accounting semantics");
            var updatedRecording = recording;
            var accessorService = EquipmentHistoryService.Create(historian, recording, () => updatedRecording);
            updatedRecording = new RecordingSettings { HistorianRecordingIntervalMilliseconds = 5000 };
            updatedRecording.Normalize();
            Check(accessorService.GetEquipment().Single(asset => asset.Id == "T1").IntervalSeconds == 5,
                "saved recording-policy replacement is reflected in the equipment catalog");
            ProjectionRules(request);
            CohortRules(bank);
            CombinedHistory(temporary);
            InterfaceExtension();
            Views(service, result, evidence);
            Exports(result, temporary);
            Throws<InvalidDataException>(() => service.Read(new EquipmentHistoryRequest { Equipment = ups, Metrics = new[] { metric }, FromUtc = Epoch, ToUtc = Epoch }, default), "invalid report window rejected");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Throws<OperationCanceledException>(() => service.Read(request, cancellation.Token), "cancelled history reads never produce a result");
            var badMetric = new HistoryMetric { Id = "output_kw; DROP TABLE ups_samples" };
            Throws<InvalidDataException>(() => service.Read(Request(ups, badMetric), default), "metric SQL identifiers are allowlisted");
            Throws<InvalidDataException>(() => service.Read(new EquipmentHistoryRequest { Equipment = ups, Metrics = null!, FromUtc = Epoch, ToUtc = Epoch.AddMinutes(1) }, default), "null channel selection is explicit invalid input");
            LimitsAndCancellation(service, settings);
            typeof(PostgresDatabase).GetField("_connectionString", PrivateStatic)!.SetValue(null, "Host=127.0.0.1;Port=1;Database=isolated_missing;Username=isolated_qa;Timeout=1;Pooling=false");
            try { Throws<NpgsqlException>(() => service.Read(request, default), "database failure never appears as empty history"); }
            finally { typeof(PostgresDatabase).GetField("_connectionString", PrivateStatic)!.SetValue(null, connectionString); }
            RecordingReliabilityValidation.Run(connectionString, temporary);
            SingleSourceRecording(service, settings);
            OnlineAnalyzerRecording(temporary);
            PlanningSettingsUi(historian, temporary, evidence);
            EnergyAndOutageTables();
            Console.WriteLine($"Equipment history assertions passed: {assertions}.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            NpgsqlConnection.ClearAllPools();
            if (started) ProcessRun(Path.Combine(pg, "pg_ctl.exe"), "-D", data, "-m", "fast", "-w", "stop");
            Directory.Delete(temporary, recursive: true);
        }
    }

    internal static void RunOnlineAnalyzerCapacity(string temporary)
    {
        Directory.CreateDirectory(temporary);
        OnlineAnalyzerRecording(temporary);
        var metric = new HistoryMetric
        {
            Id = "capacity-test", Name = "UPS-A01 | Output Apparent Power", Unit = "kVA",
            RatedCapacityKva = 100, CapacityBasis = "test nameplate"
        };
        var start = DateTime.UtcNow;
        var result = new EquipmentHistoryResult
        {
            Request = new EquipmentHistoryRequest
            {
                Equipment = new HistoryEquipment { Id = "ANALYZER", Name = "Capacity test", IntervalSeconds = 1 },
                Metrics = new[] { metric }, FromUtc = start, ToUtc = start.AddSeconds(2)
            }
        };
        var points = new List<ReportPoint>
        {
            new(start, 80), new(start.AddSeconds(1), 90), new(start.AddSeconds(2), 100)
        };
        var analysis = EquipmentAnalysis.BuildFromSeries(result, new[] { Tuple.Create(metric, points.Count, points) });
        Check(analysis.Findings.Any(finding => finding.Title == "Rated capacity reached or exceeded" &&
            finding.Detail.Contains("100.0 %") && finding.Detail.Contains("test nameplate")),
            "capacity analysis compares the peak kVA with configured nameplate rating and reports its source");
        var upsLoadMetric = new HistoryMetric
        {
            Id = "ups-load-test", Name = "UPS-A01 | Output Load (highest phase)", Unit = "%",
            RatedCapacityKva = 100, CapacityBasis = "test UPS registry"
        };
        var upsLoadResult = new EquipmentHistoryResult
        {
            Request = new EquipmentHistoryRequest
            {
                Equipment = new HistoryEquipment { Id = "ANALYZER", Name = "UPS load test", IntervalSeconds = 1 },
                Metrics = new[] { upsLoadMetric }, FromUtc = start, ToUtc = start.AddSeconds(1)
            }
        };
        var upsLoad = EquipmentAnalysis.BuildFromSeries(upsLoadResult, new[]
        {
            Tuple.Create(upsLoadMetric, 2, new List<ReportPoint> { new(start, 80), new(start.AddSeconds(1), 95) })
        });
        Check(upsLoad.Findings.Any(finding => finding.Title == "Near rated capacity" && finding.Detail.Contains("95.0 % of rated output")),
            "UPS card load percentage is compared directly with its own full-load scale");
        var pdfPath = Path.Combine(temporary, "capacity-analysis.pdf");
        EquipmentReportRenderer.WritePdf(analysis, pdfPath);
        var pdf = File.ReadAllText(pdfPath, Encoding.ASCII);
        Check(pdf.Contains("Equipment rated capacity and recorded loading") && pdf.Contains("100.0 kVA") &&
            pdf.Contains("not factory demand") && pdf.Contains("Rated capacity reached or exceeded"),
            "analyzer PDF distinguishes nameplate utilization, factory demand, and capacity findings");
        Console.WriteLine($"Online analyzer capacity assertions passed: {assertions}.");
    }

    private static void OnlineAnalyzerRecording(string temporary)
    {
        using var analyzerPreference = new AnalyzerPreferenceScope();
        MeterSnapshot Meter(string name, DateTime at, float kw) => new()
        {
            TransformerName = name, Quality = MeterQuality.Good, CapturedAtUtc = at,
            DecodedValues = Enumerable.Range(0, 16).Select(i => i < 3 ? 230f : i == 10 ? kw : i >= 11 && i <= 13 ? 10f : 1f).ToArray()
        };
        var readings = new List<MeterSnapshot>();
        void Publish(DateTime at, float kw) { readings.Clear(); readings.AddRange(new[] { "T1", "T2", "T3", "T4" }.Select(n => Meter(n, at, kw))); }
        var folder = Path.Combine(temporary, "analyzer-out-" + Guid.NewGuid().ToString("N"));
        Publish(DateTime.UtcNow.AddSeconds(-5), 40);
        var recordingSettings = new RecordingSettings();
        foreach (var id in new[] { "T1", "T2", "T3", "T4" }) recordingSettings.GetAnalyzer(id).RatedKva = 100;
        recordingSettings.GetAnalyzer("TOTAL").RatedKva = 500;
        using var view = new OnlineAnalyzerView(() => readings.ToArray(), () => new Dictionary<string, UpsSnapshot>(), () => recordingSettings);
        var equipment = Field<CheckedListBox>(view, "_equipmentList");
        equipment.SetItemChecked(0, true);
        equipment.SetItemChecked(4, true);
        var channels = Field<CheckedListBox>(view, "_channelList");
        for (var index = 0; index < channels.Items.Count; index++)
        {
            var text = channels.Items[index]!.ToString()!;
            if (text.StartsWith("Transformer") && (text.Contains("Active Power") || text.Contains("Voltage L1-N"))) channels.SetItemChecked(index, true);
        }
        Field<TextBox>(view, "_folder").Text = folder;
        Field<TextBox>(view, "_fileName").Text = "qa";
        Field<ComboBox>(view, "_interval").SelectedIndex = 0;
        Field<ComboBox>(view, "_eventMode").SelectedIndex = 1;
        Field<TextBox>(view, "_eventLevel").Text = "5";
        var type = typeof(OnlineAnalyzerView);
        type.GetMethod("OnStartClicked", Instance)!.Invoke(view, new object?[] { null, EventArgs.Empty });
        Check((long)type.GetField("_rows", Instance)!.GetValue(view)! > 0,
            "online analyzer records the current fresh acquisition immediately when Start is pressed");
        var sample = type.GetMethod("Sample", Instance)!;
        sample.Invoke(view, null);
        Thread.Sleep(150);
        Publish(DateTime.UtcNow, 50);
        sample.Invoke(view, null);
        sample.Invoke(view, null);
        type.GetMethod("StopRun", Instance)!.Invoke(view, new object[] { "test" });
        var file = Directory.GetFiles(folder, "qa_*.csv").Single();
        var lines = File.ReadAllLines(file);
        Check(lines.Length == 3, "online analyzer writes one row per new reading and never duplicates a repeated reading");
        Check(lines[0].Contains("T1 | Active Power [kW]") && lines[0].Contains("TOTAL | Active Power [kW]") && lines[0].Contains("reading quality"),
            "online analyzer file names every selected equipment channel");
        Check(lines[2].Contains("200") && lines[2].Contains("50"), "online analyzer saves transformer and combined total values");
        var eventFolder = Directory.GetDirectories(folder, "qa_*_events").Single();
        var eventLines = File.ReadAllLines(Path.Combine(eventFolder, "events.csv"));
        Check(eventLines.Length >= 2 && eventLines[1].Contains("Sudden change"), "online analyzer logs a sudden load change as an event");
        var shots = Directory.GetFiles(eventFolder, "Event_*.png");
        Check(shots.Length >= 1 && shots.All(shot => new FileInfo(shot).Length > 5000), "online analyzer saves a screenshot of the signal for the event");
        var report = Field<object>(view, "_lastReport");
        var result = (EquipmentHistoryResult)report.GetType().GetProperty("Result")!.GetValue(report)!;
        var t1Metric = result.Request.Metrics.Single(metric => metric.Name == "T1 | Active Power");
        var totalMetric = result.Request.Metrics.Single(metric => metric.Name == "TOTAL | Active Power");
        Check(t1Metric.RatedCapacityKva == 100 && t1Metric.CapacityBasis.Contains("Current analyzer master data") &&
            t1Metric.CapacityBasis.Contains("historical"),
            "online analyzer report uses the configured transformer rating and discloses historical-rating uncertainty");
        Check(totalMetric.RatedCapacityKva == 400 && totalMetric.CapacityBasis.Contains("differs from this sum"),
            "factory TOTAL rating is derived from all four transformer ratings, not a sampled peak or conflicting master total");
        Check(result.Request.Equipment.Description.Contains("UPS output is reported per UPS") &&
            result.Request.Equipment.Description.Contains("must not be added together"),
            "online analyzer report explicitly separates per-UPS output from overlapping factory demand");

        using var waitingView = new OnlineAnalyzerView(() => Array.Empty<MeterSnapshot>(),
            () => new Dictionary<string, UpsSnapshot>(), () => recordingSettings);
        Field<CheckedListBox>(waitingView, "_equipmentList").SetItemChecked(0, true);
        Field<CheckedListBox>(waitingView, "_channelList").SetItemChecked(0, true);
        Field<CheckBox>(waitingView, "_saveToFile").Checked = false;
        type.GetMethod("OnStartClicked", Instance)!.Invoke(waitingView, new object?[] { null, EventArgs.Empty });
        Check((long)type.GetField("_rows", Instance)!.GetValue(waitingView)! == 0 &&
            Field<Label>(waitingView, "_status").Text.Contains("waiting for a fresh acquisition"),
            "analyzer clearly reports when the selected source has not provided a fresh reading yet");
        type.GetMethod("StopRun", Instance)!.Invoke(waitingView, new object[] { "test" });
    }

    private static void DailyExportReusesExistingReport(MeasurementHistorian historian, RecordingSettings settings, string temporary)
    {
        var timezone = settings.ResolveFactoryTimeZone();
        var localDay = TimeZoneInfo.ConvertTimeFromUtc(Epoch, timezone).Date;
        var dayStart = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDay, DateTimeKind.Unspecified), timezone);
        var dayEnd = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDay.AddDays(1), DateTimeKind.Unspecified), timezone);
        var directory = Path.Combine(temporary, "T1", "DailyExcel");
        var filename = "EOEMS_T1_" + localDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".xlsx";
        var path = HistorianReportExporter.ExportExcelAsync(historian, settings, "T1", dayStart, dayEnd,
            directory, filename, default).GetAwaiter().GetResult();
        var original = File.ReadAllBytes(path);
        using (var zip = System.IO.Compression.ZipFile.OpenRead(path))
        {
            var sheetXml = new StreamReader(zip.GetEntry("xl/worksheets/sheet2.xml")!.Open()).ReadToEnd();
            Check(sheetXml.Contains("Timestamp UTC") && !sheetXml.Contains("Acquisition cycle") && !sheetXml.Contains(">Quality<") && !sheetXml.Contains("Value type"),
                "daily Excel measurements use the compact layout without constant quality, type and cycle columns");
            Check(System.Text.RegularExpressions.Regex.Matches(sheetXml, "<row ").Count <= 4,
                "daily Excel has one row per saved sample, not one row per parameter value");
        }
        var jobs = new DailyExportJobStore();
        jobs.Initialize(historian.DatabasePath);
        using var scheduler = new DailyHistorianExportScheduler(historian, settings, temporary);
        scheduler.RunDueExportsAsync().GetAwaiter().GetResult();
        var grouped = DailyGroupReports.GetPath(temporary, "Transformers", localDay);
        Check(File.Exists(grouped), "daily scheduler writes the grouped transformers workbook: " + scheduler.LastError + " | " +
            string.Join(" ; ", scheduler.GetRecentJobs(50).Select(item => item.AnalyzerId + " " + item.Status + " " + item.ErrorMessage)));
        using (var zip = System.IO.Compression.ZipFile.OpenRead(grouped))
        {
            var t1 = new StreamReader(zip.GetEntry("xl/worksheets/sheet2.xml")!.Open()).ReadToEnd();
            var rows = System.Text.RegularExpressions.Regex.Matches(t1, "<row ").Count;
            Check(rows >= 1380 + 1 && rows <= 1500 + 1, "grouped workbook has one row per minute of the day (" + rows + ")");
            Check(t1.Contains("Avg kW") && !t1.Contains("Acquisition cycle"), "minute sheet has the compact averaged layout");
        }
        Check(File.Exists(DailyGroupReports.GetPath(temporary, "Generators", localDay)) && File.Exists(DailyGroupReports.GetPath(temporary, "Loads", localDay)),
            "generators and loads get a not-connected workbook without invented data");
        var job = scheduler.GetRecentJobs(1000).Single(item => item.AnalyzerId == "Transformers" && item.LocalDay == localDay);
        Check(job.Status == "Succeeded" && job.OutputPath == grouped, "grouped daily job is recorded as succeeded");
        var groupedBytes = File.ReadAllBytes(grouped);
        scheduler.RunDueExportsAsync().GetAwaiter().GetResult();
        Check(File.ReadAllBytes(grouped).SequenceEqual(groupedBytes), "a second run never rewrites an existing daily report");
    }

    private static void PlanningSettingsUi(MeasurementHistorian historian, string temporary, string evidence)
    {
        var recording = new RecordingSettings
        {
            ExportFolderPath = temporary, HistorianRecordingIntervalMilliseconds = 1000, HistorianRetentionDays = 10
        };
        recording.Normalize();
        using var scheduler = new DailyHistorianExportScheduler(historian, recording, temporary);
        using var view = new HistorianView(recording,
            new UserSession { UserId = "isolated_qa", Username = "isolated_qa", RoleName = "Administrator" },
            historian, scheduler, _ => Task.CompletedTask,
            plcSettings: new PlcSettings { RequestedAcquisitionIntervalMilliseconds = 100 });
        Await((Task)typeof(HistorianView).GetMethod("RefreshStorageEstimateAsync", Instance)!.Invoke(view, null)!);
        Check(Field<RawRecordingStorageEstimate>(view, "_storageEstimate").EquipmentCount == 7,
            "actual async settings estimator reads disposable PostgreSQL samples");
        using var form = new Form
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-2400, -1800),
            ClientSize = new Size(1160, 800), Text = "Isolated planning UI validation"
        };
        view.Dock = DockStyle.Fill;
        form.Controls.Add(view);
        form.Show();
        Field<TabControl>(view, "_tabs").SelectedIndex = 1;
        foreach (var size in new[] { new Size(1160, 800), new Size(980, 600) })
        {
            form.ClientSize = size;
            Pump();
            var combo = Field<ComboBox>(view, "_intervalCombo");
            Panel? scroll = null;
            for (var parent = combo.Parent; parent != null; parent = parent.Parent)
                if (parent is Panel { AutoScroll: true } panel) { scroll = panel; break; }
            Check(scroll != null && scroll.AutoScroll, "planning card lives inside a scrollable workspace");
            scroll!.AutoScrollPosition = Point.Empty;
            Pump();
            using (var bitmap = new Bitmap(view.Width, view.Height))
            {
                view.DrawToBitmap(bitmap, view.ClientRectangle);
                bitmap.Save(Path.Combine(evidence, $"planning-controls-{size.Width}.png"));
            }
            var grid = Field<DataGridView>(view, "_resolutionGrid");
            scroll.ScrollControlIntoView(grid);
            Pump();
            Check(scroll.RectangleToScreen(scroll.ClientRectangle).Contains(grid.RectangleToScreen(grid.ClientRectangle)),
                "entire storage table remains reachable at " + size.Width + " pixels");
            Check(grid.Rows.Count == 7 && grid.Rows.Cast<DataGridViewRow>().All(row =>
                row.Cells["SelectedSize"].Value!.ToString()!.StartsWith("~") &&
                row.Cells["WorstCase"].Value!.ToString()!.StartsWith("~")),
                "actual UI shows measured numeric estimates for every interval");
            using var tableBitmap = new Bitmap(view.Width, view.Height);
            view.DrawToBitmap(tableBitmap, view.ClientRectangle);
            tableBitmap.Save(Path.Combine(evidence, $"planning-storage-{size.Width}.png"));
        }
        form.Controls.Remove(view);
    }

    private static void SeedUps(NpgsqlConnection connection, UpsSettings settings)
    {
        for (var index = 0; index < 5; index++)
        {
            var stamp = Epoch.AddMinutes(index);
            var snapshot = Snapshot(settings.Devices[0].Id, stamp.AddSeconds(-2), 100 * (index + 1));
            if (index == 2) snapshot.Quality = UpsQuality.Timeout;
            if (index == 3) snapshot.MeasurementRevision = 1;
            Insert(connection, settings.Devices[0], snapshot, stamp, index == 4 ? null : settings);
            if (index == 0) Insert(connection, settings.Devices[1], Snapshot(settings.Devices[1].Id, stamp.AddSeconds(-1), 300), stamp, settings);
        }
        var retired = settings.Devices[0].Clone();
        retired.Id = "UPS-RETIRED";
        retired.Label = "QA, retired\nunit";
        retired.AtsBank = 2;
        Insert(connection, retired, Snapshot(retired.Id, Epoch.AddMinutes(6), 70), Epoch.AddMinutes(6), null);
        using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT device_label, acquired_utc, bank_members, alarm_evidence::text, channel_evidence::text FROM ups_samples WHERE bank_members IS NOT NULL ORDER BY sampled_utc,device_id LIMIT 1";
        using var reader = verify.ExecuteReader();
        Check(reader.Read() && reader.GetString(0) == settings.Devices[0].Label, "recorded device label preserved");
        Check(reader.GetFieldValue<string[]>(2).Length == 2 && reader.GetString(3).Contains("synthetic QA alarm"), "cohort and alarm evidence are durable database fields");
        Check(reader.GetString(4).Contains("ChargePercent"), "full numeric channel evidence is stored alongside legacy wide columns");
    }

    private static UpsSnapshot Snapshot(string id, DateTime timestamp, double kw) => new()
    {
        DeviceId = id, TimestampUtc = timestamp, Quality = UpsQuality.Good, MeasurementRevision = 2,
        OutputVoltageReference = "L-N verified", AlarmDetailsComplete = true, AlarmDetailsStatus = "synthetic QA alarm",
        Values = new Dictionary<string, double> { ["OutputPower"] = kw * 1000, ["OutputApparentVa"] = kw * 1250, ["ChargePercent"] = 95,
            ["BatteryStatus"] = 2, ["OutputSource"] = 3, ["AlarmsPresent"] = 0, ["DcBusVoltage"] = double.NaN },
        InvalidFields = new[] { "InputPower L3 (non-finite fixture)" },
        Phases = new Dictionary<string, double?[]> { ["InputPower"] = new double?[] { 1000, 0, double.PositiveInfinity },
            ["InputVoltage"] = new double?[] { 0, 0, 0 }, ["InputVoltageLL"] = new double?[] { 0, 0, 0 },
            ["InputCurrent"] = new double?[] { 0, 0, 0 },
            ["OutputPower"] = new double?[] { 50000, 30000, 20000 } }
    };

    private static void Insert(NpgsqlConnection connection, UpsDevice device, UpsSnapshot snapshot, DateTime stamp, UpsSettings? settings)
    {
        using var transaction = connection.BeginTransaction();
        var written = (int)typeof(UpsHistorian).GetMethod("InsertRow", PrivateStatic)!.Invoke(null, new object?[] { connection, transaction, device, snapshot, stamp, settings })!;
        transaction.Commit();
        Check(written == 1, "actual UPS historian writer persists synthetic sample");
    }

    private static void EnergyAndOutageTables()
    {
        EnergyAccounting.Recompute();
        EnergyAccounting.Recompute(1);
        var cells = EnergyAccounting.ReadCells("day", new[] { "Transformer", "Total" }, 400);
        Check(cells.Count > 0, "energy accounting writes shift rows from the recorded kW");
        Check(cells.All(cell => cell.Shift == "A" || cell.Shift == "B"), "every energy row belongs to shift A or B");
        Check(cells.All(cell => cell.Period.TimeOfDay == TimeSpan.Zero), "energy periods are whole dates");
        foreach (var unit in EnergyAccounting.Units) Check(EnergyAccounting.ReadCells(unit, new[] { "Total" }, 5).Count >= 0, $"energy {unit} roll-up is readable");
        EcgOutageStore.Sync();
        foreach (var kind in new[] { "day", "week", "month", "year" }) Check(EcgOutageStore.ReadPeriods(kind) != null, $"ECG outage {kind} periods are readable");
        Check(EcgOutageStore.ReadOutages() != null, "permanent ECG outages are readable");
    }

    private static void SeedTransformers(NpgsqlConnection connection)
    {
        for (var index = 0; index < 3; index++)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO measurement_samples(analyzer_id,captured_at,session_id,cycle_id,vals) VALUES('T1',@t,@s,@c,@v)";
            command.Parameters.AddWithValue("t", Epoch.AddSeconds(index));
            command.Parameters.AddWithValue("s", Guid.NewGuid());
            command.Parameters.AddWithValue("c", (long)index);
            var values = Enumerable.Repeat(230f, 18).ToArray();
            values[10] = 10 + index;
            command.Parameters.AddWithValue("v", values);
            command.ExecuteNonQuery();
        }
        using var archive = connection.CreateCommand();
        archive.CommandText = "INSERT INTO measurement_15min(analyzer_id,parameter_index,bucket_start,min_value,max_value,sum_value,sample_count) VALUES('T1',10,@t,10,20,45,3)";
        archive.Parameters.AddWithValue("t", Epoch);
        archive.ExecuteNonQuery();
        using var quality = connection.CreateCommand();
        quality.CommandText = "INSERT INTO sample_quality_minute(analyzer_id,bucket_start,total_samples,good_samples,bad_samples,first_at,last_at) VALUES('T1',@t,4,3,1,@t,@t)";
        quality.Parameters.AddWithValue("t", Epoch);
        quality.ExecuteNonQuery();
    }

    private static void SeedRecentStorageRateRows(NpgsqlConnection connection, UpsSettings settings)
    {
        var baseUtc = DateTime.UtcNow.AddMinutes(-5);
        foreach (var id in new[] { "T1", "T2", "T3", "T4", "TOTAL" })
        {
            var session = Guid.NewGuid();
            for (var index = 0; index < 64; index++)
            {
                using var legacy = connection.CreateCommand();
                legacy.CommandText = "INSERT INTO measurement_samples(analyzer_id,captured_at,session_id,cycle_id,vals) VALUES(@id,@time,@session,@cycle,@values)";
                legacy.Parameters.AddWithValue("id", id);
                legacy.Parameters.AddWithValue("time", baseUtc.AddSeconds(index));
                legacy.Parameters.AddWithValue("session", session);
                legacy.Parameters.AddWithValue("cycle", (long)index + 1000);
                legacy.Parameters.AddWithValue("values", Enumerable.Repeat(230f, 18).ToArray());
                legacy.ExecuteNonQuery();
            }
        }
        foreach (var device in settings.Devices.Where(item => item.Enabled))
            for (var index = 0; index < 64; index++)
            {
                using var legacy = connection.CreateCommand();
                legacy.CommandText = "INSERT INTO ups_samples(device_id,sampled_utc,quality) VALUES(@id,@time,'GOOD')";
                legacy.Parameters.AddWithValue("id", device.Id);
                legacy.Parameters.AddWithValue("time", baseUtc.AddSeconds(index));
                legacy.ExecuteNonQuery();
            }
    }

    private static void SingleSourceRecording(EquipmentHistoryService service, UpsSettings settings)
    {
        typeof(UpsSettingsStore).GetField("_current", PrivateStatic)!.SetValue(null, settings);
        using var connection = PostgresDatabase.Open();
        using (var exists = new NpgsqlCommand("SELECT to_regclass('equipment_raw_acquisitions') IS NULL", connection))
            Check((bool)exists.ExecuteScalar()!, "no separate raw acquisition table exists; one recording only");
        SeedRecentStorageRateRows(connection, settings);
        var storage = EquipmentRawHistory.ReadStorageEstimate(settings, 1000);
        Check(storage.UsesObservedRates && storage.ObservedRawTransformerBytesPerSecond == 0 && storage.ObservedLegacyBytesPerSecond > 0,
            "storage estimate is measured from the single recording only");
        var transformer = service.GetRawEquipment().Single(asset => asset.Id == "T1");
        Check(transformer.Metrics.All(metric => !metric.Id.StartsWith("peak:")), "no interval-peak channels derived from a second recording");
        var result = service.Read(new EquipmentHistoryRequest { Equipment = transformer, RawOnly = true,
            Metrics = transformer.Metrics.Where(metric => metric.Id == "PTOT_KW").ToArray(),
            FromUtc = DateTime.UtcNow.AddMinutes(-10), ToUtc = DateTime.UtcNow.AddMinutes(1) }, default);
        Check(result.Rows.Count >= 64, "Raw recording tab reads measurement_samples");
    }

    private static void CohortRules(HistoryEquipment bank)
    {
        var metric = bank.Metrics[0];
        var request = Request(bank, metric);
        UpsHistoryObservation Observation(string id, DateTime slot, DateTime acquired, string[] cohort) =>
            new() { DeviceId = id, TimestampUtc = slot, AcquiredUtc = acquired, Quality = "GOOD", Revision = 2,
                Members = cohort, RecordedIntervalSeconds = 60 };
        var first = Observation("a", Epoch, Epoch.AddSeconds(-10), new[] { "a", "b" });
        var second = Observation("b", Epoch, Epoch.AddSeconds(-2), new[] { "a", "b" });
        first.Values[metric.Id] = 100;
        second.Values[metric.Id] = 200;
        Check(UpsEquipmentHistorySource.Project(request, new[] { first, second }).Single().Value == 300, "complete uppercase GOOD cohort sums normally");
        second.Members = new[] { "a", "b", "c" };
        Check(UpsEquipmentHistorySource.Project(request, new[] { first, second }).Single().Quality == "Incomplete cohort", "conflicting saved membership cannot be summed");
        second.Members = first.Members;
        second.AcquiredUtc = Epoch.AddSeconds(-90);
        Check(UpsEquipmentHistorySource.Project(request, new[] { first, second }).Single().Quality == "Unaligned / stale sources", "unsynchronized member timestamps prevent an ATS-bank total");
        second.AcquiredUtc = Epoch.AddSeconds(-2);
        var third = Observation("a", Epoch.AddMinutes(1), Epoch.AddSeconds(55), first.Members);
        var fourth = Observation("b", Epoch.AddMinutes(1), second.AcquiredUtc!.Value, first.Members);
        third.Values[metric.Id] = 100;
        fourth.Values[metric.Id] = 200;
        var held = UpsEquipmentHistorySource.Project(request, new[] { first, second, third, fourth });
        Check(EquipmentHistoryStatistics.Calculate(metric, held, 60).EnergyKwh == null, "aggregate energy requires every member acquisition to advance, not only the minimum");
        fourth.AcquiredUtc = Epoch.AddSeconds(58);
        var fresh = UpsEquipmentHistorySource.Project(request, new[] { first, second, third, fourth });
        Check(EquipmentHistoryStatistics.Calculate(metric, fresh, 60).EnergyKwh == 5, "fresh aligned complete cohort integrates exactly");
        third.RecordedIntervalSeconds = 10;
        fourth.RecordedIntervalSeconds = 10;
        Check(EquipmentHistoryStatistics.Calculate(metric, UpsEquipmentHistorySource.Project(request, new[] { first, second, third, fourth }), 60).EnergyKwh == null,
            "saved interval changes cannot be bridged using today's more permissive interval");
        second.Revision = 1;
        Check(UpsEquipmentHistorySource.Project(request, new[] { first, second }).Single().Value == null, "legacy-mapped member makes aggregate incomplete");
        second.Revision = 2;
        second.Values[metric.Id] = null;
        Check(UpsEquipmentHistorySource.Project(request, new[] { first, second }).Single().Value == null, "missing member channel cannot be treated as zero");
    }

    private static void LimitsAndCancellation(EquipmentHistoryService service, UpsSettings settings)
    {
        var device = settings.Devices[0].Clone();
        device.Id = "UPS-LIMIT-QA";
        device.AtsBank = 99;
        settings.Devices.Add(device);
        try
        {
            using var connection = PostgresDatabase.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO ups_samples(device_id,sampled_utc,ats_bank,quality,measurement_revision,output_kw,battery_charge_pct) " +
                "SELECT @id,@t+g*interval '1 second',99,'GOOD',2,1,50 FROM generate_series(0,50000) g";
            insert.Parameters.AddWithValue("id", device.Id);
            insert.Parameters.AddWithValue("t", Epoch.AddDays(2));
            insert.ExecuteNonQuery();
            var asset = service.GetEquipment().Single(asset => asset.Id == device.Id);
            var request = new EquipmentHistoryRequest
            {
                Equipment = asset, Metrics = asset.Metrics.Where(metric => metric.Id is "output_kw" or "battery_charge_pct").ToArray(),
                FromUtc = Epoch.AddDays(2), ToUtc = Epoch.AddDays(2).AddSeconds(50000)
            };
            Check(service.Read(request, default).Rows.Count == 100000, "exact observation limit returns every row, not a smaller proxy limit");
            request.ToUtc = request.ToUtc.AddSeconds(1);
            Throws<InvalidDataException>(() => service.Read(request, default), "100,002 observations fail explicitly instead of producing a truncated report");
            using var transaction = connection.BeginTransaction();
            using var blocker = connection.CreateCommand();
            blocker.Transaction = transaction;
            blocker.CommandText = "LOCK TABLE ups_samples IN ACCESS EXCLUSIVE MODE";
            blocker.ExecuteNonQuery();
            using var cancel = new CancellationTokenSource(250);
            var watch = Stopwatch.StartNew();
            Throws<OperationCanceledException>(() => service.Read(Request(asset, asset.Metrics[0]), cancel.Token), "cancellation interrupts an actually blocked PostgreSQL query");
            Check(watch.Elapsed < TimeSpan.FromSeconds(10), "query cancellation is responsive");
            transaction.Rollback();
            using var transformerLock = connection.BeginTransaction();
            blocker.Transaction = transformerLock;
            blocker.CommandText = "LOCK TABLE measurement_samples IN ACCESS EXCLUSIVE MODE";
            blocker.ExecuteNonQuery();
            var transformer = service.GetEquipment().Single(asset => asset.Id == "T1");
            using var cancelTransformer = new CancellationTokenSource(250);
            Throws<OperationCanceledException>(() => service.Read(Request(transformer, transformer.Metrics[0]), cancelTransformer.Token),
                "cancellation also interrupts an actually blocked transformer raw query");
            transformerLock.Rollback();
        }
        finally { settings.Devices.Remove(device); }
    }

    private static void ProjectionRules(EquipmentHistoryRequest request)
    {
        var metric = request.Metrics[0];
        var repeated = new[]
        {
            new EquipmentHistoryRow { MetricId = metric.Id, TimestampUtc = Epoch, AcquiredUtc = Epoch, Value = 100, Quality = "Good" },
            new EquipmentHistoryRow { MetricId = metric.Id, TimestampUtc = Epoch.AddMinutes(1), AcquiredUtc = Epoch, Value = 100, Quality = "Good" }
        };
        Check(EquipmentHistoryStatistics.Calculate(metric, repeated, 60).EnergyKwh == null, "repeated held poll is not integrated as new energy evidence");
        repeated[1].AcquiredUtc = Epoch.AddMinutes(10);
        Check(EquipmentHistoryStatistics.Calculate(metric, repeated, 60).EnergyKwh == null, "excessive source gap is not bridged");
        repeated[1].AcquiredUtc = null;
        Check(EquipmentHistoryStatistics.Calculate(metric, repeated, 60).EnergyKwh == null, "legacy missing acquisition timestamp prevents invented energy");
        var invalid = new EquipmentHistoryRow { MetricId = metric.Id, TimestampUtc = Epoch.AddSeconds(30), Quality = "TIMEOUT" };
        repeated[1].AcquiredUtc = Epoch.AddMinutes(1);
        Check(EquipmentHistoryStatistics.Calculate(metric, new[] { repeated[0], invalid, repeated[1] }, 60).EnergyKwh == null, "invalid observation breaks energy continuity");
    }

    private static void Exports(EquipmentHistoryResult result, string temporary)
    {
        foreach (var format in new[] { "CSV", "Excel", "PDF" })
        {
            var path = Path.Combine(temporary, format == "Excel" ? "history.xlsx" : "history." + format.ToLowerInvariant());
            HistorianReportExporter.ExportEquipmentHistory(result, path, format);
            Check(new FileInfo(path).Length > 100, format + " report is persisted and nonempty");
            Throws<IOException>(() => HistorianReportExporter.ExportEquipmentHistory(result, path, format), format + " refuses overwriting prior report");
            if (format == "Excel")
            {
                using var zip = ZipFile.OpenRead(path);
                foreach (var entry in zip.Entries.Where(entry => entry.FullName.EndsWith(".xml")))
                {
                    using var stream = entry.Open();
                    XDocument.Load(stream);
                    Check(true, "XLSX XML part parses: " + entry.FullName);
                }
                using var records = new StreamReader(zip.GetEntry("xl/worksheets/sheet2.xml")!.Open());
                var xml = records.ReadToEnd();
                using var rawRecords = new StreamReader(zip.GetEntry("xl/worksheets/sheet3.xml")!.Open());
                Check(rawRecords.ReadToEnd().Contains("synthetic QA alarm") && !xml.Contains("TIMEOUT"), "Excel preserves raw evidence separately from selected interval table");
                var doc = XDocument.Parse(xml);
                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                var header = doc.Descendants(ns + "row").First().Elements(ns + "c").Single(element =>
                    element.Value == result.Request.Metrics.Single().Name + " [" + result.Request.Metrics.Single().Unit + "]");
                var measurementColumn = new string(header.Attribute("r")!.Value.TakeWhile(char.IsLetter).ToArray());
                var cell = doc.Descendants(ns + "c").Single(element => (string?)element.Attribute("r") == measurementColumn + "2");
                Check(cell.Attribute("t") == null && cell.Element(ns + "v")!.Value == "100.0", "Excel measurements retain one decimal as numeric cells usable in formulas and pivot tables");
                Check(doc.Descendants(ns + "pane").Single().Attribute("state")!.Value == "frozen" &&
                    doc.Descendants(ns + "autoFilter").Single().Attribute("ref")!.Value.StartsWith("A1:" +
                        new string(doc.Descendants(ns + "row").First().Elements(ns + "c").Last().Attribute("r")!.Value.TakeWhile(char.IsLetter).ToArray())),
                    "Excel records have frozen headers and a full-table filter matching the current date/time columns");
                using var stats = new StreamReader(zip.GetEntry("xl/worksheets/sheet5.xml")!.Open());
                Check(stats.ReadToEnd().Contains("2.5"), "Excel statistics use exact verified energy");
            }
            if (format == "CSV")
            {
                var csv = File.ReadAllText(path);
                Check(csv.Contains("Alarms / faults observed") && !csv.Contains("\"Min\""), "CSV matches interval table with alarms and quality");
            }
            if (format == "PDF")
            {
                var pdf = File.ReadAllText(path, Encoding.ASCII);
                Check(pdf.StartsWith("%PDF-1.4") && pdf.EndsWith("%%EOF") && pdf.Contains("EQUIPMENT ANALYSIS REPORT"), "PDF is valid structured company report with statistics");
            }
        }
        var originalEvidence = result.Rows[0].Evidence;
        var originalAlarm = result.Rows[0].AlarmSummary;
        var csvPath = Path.Combine(temporary, "quoted.csv");
        result.Rows[0].AlarmSummary = "=formula, with\nnewline";
        HistorianReportExporter.ExportEquipmentHistory(result, csvPath, "CSV");
        Check(File.ReadAllText(csvPath).Contains("\"'=formula, with\nnewline\""), "CSV quotes embedded commas/newlines and defuses formula-like evidence");
        result.Rows[0].Evidence = new string('x', 32768);
        var invalidExcel = Path.Combine(temporary, "oversized.xlsx");
        Throws<InvalidDataException>(() => HistorianReportExporter.ExportEquipmentHistory(result, invalidExcel, "Excel"), "Excel evidence cell limits are explicit rather than silently truncating");
        Check(!File.Exists(invalidExcel), "failed Excel export never promotes partial output");
        result.Rows[0].Evidence = originalEvidence;
        result.Rows[0].AlarmSummary = originalAlarm;
        Check(!Directory.EnumerateFiles(temporary, "*.partial").Any(), "successful exports leave no temporary files");
    }

    private static void InterfaceExtension()
    {
        var service = new EquipmentHistoryService(new[] { new FixtureSource() });
        var asset = service.GetEquipment().Single(asset => asset.Id == "FUTURE-QA");
        var result = service.Read(Request(asset, asset.Metrics[0]), default);
        Check(result.Rows.Single().Value == 42, "a future equipment provider shares query, tables, analysis and export contracts without transformer IDs");
        var generatorService = new EquipmentHistoryService(new[] { new FixtureSource("Generators") });
        Check(generatorService.GetEquipment().Count(asset => asset.Category == "Generators") == 1 &&
            generatorService.GetEquipment().All(asset => asset.Id != "GENERATORS"), "a connected future generator source replaces the unavailable placeholder");
    }

    private static void CombinedHistory(string temporary)
    {
        var assets = new[]
        {
            new HistoryEquipment { Id = "T1", Name = "Transformer 1", Category = "Transformers", SourceId = "transformer", IntervalSeconds = 1 },
            new HistoryEquipment { Id = "T2", Name = "Transformer 2", Category = "Transformers", SourceId = "transformer", IntervalSeconds = 2 }
        };
        var metrics = assets.Select(asset => new HistoryMetric
        {
            Id = asset.Id + "|PTOT_KW", Name = asset.Id + " | Active power", SourceMetricName = "Active power",
            Unit = "kW", IsPower = true, SourceId = "transformer", SourceMetricId = "PTOT_KW", EquipmentId = asset.Id
        }).ToArray();
        var rows = assets.Select((asset, index) => new EquipmentHistoryRow
        {
            EquipmentId = asset.Id, MetricId = metrics[index].Id, TimestampUtc = Epoch.AddSeconds(index),
            AcquiredUtc = Epoch.AddSeconds(index), Value = 100 + index, Quality = "Good", ObservationId = asset.Id
        }).ToArray();
        var result = new EquipmentHistoryResult
        {
            Request = new EquipmentHistoryRequest
            {
                Equipment = new HistoryEquipment { Id = "__ALL_EQUIPMENT__", Name = "All equipment", SourceId = "combined" },
                Metrics = metrics, IncludedEquipment = assets, IsCombined = true, RawOnly = true,
                FromUtc = Epoch, ToUtc = Epoch.AddMinutes(1)
            },
            Rows = rows, GeneratedUtc = Epoch.AddMinutes(1)
        };
        var table = EquipmentHistoryTable.Create(result);
        Check(table.Headers.Contains("Equipment") && table.Headers.Contains("Measurement") && table.Rows.Count == 2,
            "combined history uses a chronological long-form table with explicit equipment and measurement columns");
        Check(table.Rows.Select(row => row.Cells[2]).Order().SequenceEqual(new[] { "Transformer 1", "Transformer 2" }) &&
            table.Rows.All(row => row.Cells[4] == "kW" && row.Cells[6] == "Good"),
            "combined rows preserve equipment identity, units and quality");
        Check(result.Statistics.Count == 2 && result.Statistics.Select(item => item.EquipmentId).Order().SequenceEqual(new[] { "T1", "T2" }),
            "combined statistics stay scoped to each equipment rather than mixing intervals");
        var csvPath = Path.Combine(temporary, "combined-history.csv");
        HistorianReportExporter.ExportEquipmentHistory(result, csvPath, "CSV");
        var csv = File.ReadAllText(csvPath);
        Check(csv.Contains("Transformer 1") && csv.Contains("Transformer 2") && csv.Contains("Active power"),
            "combined CSV export preserves all equipment rows and channel labels");
        var excelPath = Path.Combine(temporary, "combined-history.xlsx");
        HistorianReportExporter.ExportEquipmentHistory(result, excelPath, "Excel");
        using (var zip = ZipFile.OpenRead(excelPath))
        using (var sheet = new StreamReader(zip.GetEntry("xl/worksheets/sheet2.xml")!.Open()))
        {
            var xml = sheet.ReadToEnd();
            Check(xml.Contains("Transformer 1") && xml.Contains("Transformer 2") && xml.Contains("100.0"),
                "combined Excel export retains equipment names and numeric measurement values");
            using var statistics = new StreamReader(zip.GetEntry("xl/worksheets/sheet3.xml")!.Open());
            var statsXml = statistics.ReadToEnd();
            Check(statsXml.Contains("Transformer 1 | Active power") && statsXml.Contains("Transformer 2 | Active power"),
                "combined statistics identify each equipment instead of merging channel names");
        }
        var pdfPath = Path.Combine(temporary, "combined-history.pdf");
        HistorianReportExporter.ExportEquipmentHistory(result, pdfPath, "PDF");
        Check(File.ReadAllText(pdfPath, Encoding.ASCII).Contains("EQUIPMENT ANALYSIS REPORT"),
            "combined PDF export generates an equipment analysis report");
    }

    private static void Views(EquipmentHistoryService service, EquipmentHistoryResult result, string evidence)
    {
        using var view = new EquipmentHistoryView(service);
        using var form = new Form { ClientSize = new Size(1160, 680), Location = new Point(-2400, -1800), StartPosition = FormStartPosition.Manual };
        form.Controls.Add(view);
        form.Show();
        var equipment = Field<ComboBox>(view, "_equipment");
        Check(equipment.Items.Count == 8 && equipment.Items.Cast<HistoryEquipment>().First().IsCombinedSelection,
            "All equipment offers a combined-history selection before the individual asset choices");
        equipment.SelectedIndex = 0;
        Field<DateTimePicker>(view, "_from").Value = Epoch;
        Field<DateTimePicker>(view, "_to").Value = Epoch.AddMinutes(5);
        Field<DateTimePicker>(view, "_fromTime").Value = Epoch;
        Field<DateTimePicker>(view, "_toTime").Value = Epoch.AddMinutes(5);
        Await(view.LoadRecordsAsync());
        var combined = Field<EquipmentHistoryResult>(view, "_result");
        Check(combined.Request.IsCombined && combined.Request.IncludedEquipment.Count > 1 &&
            Field<DataGridView>(view, "_records").Columns.Contains("Equipment") &&
            Field<DataGridView>(view, "_records").RowCount > 0,
            "combined selection loads saved data for multiple assets into the labeled chronological table");
        Check(Field<ComboBox>(view, "_trendMetric").Visible,
            "combined history exposes a single compatible-channel selector for its trend chart");
        var combinedChart = Field<AnalyticsTrendChart>(view, "_chart");
        Check(combinedChart.Series.Count == 1 && combinedChart.YAxisLabel.Length > 0 &&
            combinedChart.Series.All(series => combined.Request.IncludedEquipment.Any(asset => asset.Name == series.Name)),
            "combined history retains the combined table but charts only the selected equipment without unrelated overlays");
        Check(combinedChart.Series.All(series => series.ExpectedIntervalSeconds >= combined.Request.ThinSeconds),
            "combined trends respect the effective history sampling interval");
        equipment.SelectedItem = equipment.Items.Cast<HistoryEquipment>().Single(asset => asset.Id == result.Request.Equipment.Id);
        Field<DateTimePicker>(view, "_from").Value = Epoch;
        Field<DateTimePicker>(view, "_to").Value = Epoch.AddMinutes(5);
        Await(view.LoadRecordsAsync());
        Check(Field<DataGridView>(view, "_records").RowCount >= 0, "recorded workspace loads from the single saved recording");
        Check(Field<AnalyticsTrendChart>(view, "_chart").ShowMarkers, "isolated valid samples after gaps remain visible as chart markers");
        Check(Field<AnalyticsTrendChart>(view, "_chart").XAxisLabel.StartsWith("UTC  ", StringComparison.Ordinal) &&
            Field<AnalyticsTrendChart>(view, "_chart").WindowStartUtc == Epoch,
            "history chart labels the requested UTC window");
        var singleResult = Field<EquipmentHistoryResult>(view, "_result");
        singleResult.Request.ThinSeconds = 23;
        typeof(EquipmentHistoryView).GetMethod("RenderTrend", Instance)!.Invoke(view, null);
        Check(Field<AnalyticsTrendChart>(view, "_chart").Series.All(series => series.ExpectedIntervalSeconds >= 23),
            "single-equipment history passes Auto sampling cadence to the rendered series");
        singleResult.Request.ThinSeconds = 0;
        typeof(EquipmentHistoryView).GetMethod("RenderTrend", Instance)!.Invoke(view, null);
        Check(equipment.Items.Count == 8 && equipment.Items.Cast<HistoryEquipment>().Count(asset => !asset.IsCombinedSelection) == 7 &&
            equipment.Items.Cast<HistoryEquipment>().Where(asset => !asset.IsCombinedSelection).All(asset =>
                asset.SourceId == "transformer" || (asset.SourceId == "ups" && !asset.IsAggregate)),
            "clean equipment list contains one combined selection plus T1-T4/TOTAL and registered individual UPS only");
        Check(!equipment.Items.Cast<HistoryEquipment>().Any(asset => asset.Id.Contains(":15MIN") ||
            asset.Id == "GENERATORS" || asset.Id == "UPS-RETIRED" || asset.Id.StartsWith("ATS-")),
            "archives, placeholders, derived UPS groups and discovered historical IDs do not clutter raw equipment list");
        foreach (var size in new[] { new Size(980, 600), new Size(1160, 680), new Size(1560, 900) })
        {
            form.ClientSize = size;
            var tabs = Descendants(view).OfType<TabControl>().Single();
            for (var index = 0; index < tabs.TabPages.Count; index++)
            {
                tabs.SelectedIndex = index;
                Pump();
                using var bitmap = new Bitmap(view.Width, view.Height);
                view.DrawToBitmap(bitmap, view.ClientRectangle);
                bitmap.Save(Path.Combine(evidence, $"equipment-{size.Width}-{index}.png"));
                foreach (var button in Field<List<Button>>(view, "_exports"))
                    Check(view.RectangleToScreen(view.ClientRectangle).Contains(button.RectangleToScreen(button.ClientRectangle)), "export action remains inside responsive workspace");
                if (index == 1)
                    Check(Field<AnalyticsTrendChart>(view, "_chart").Visible && !Field<DataGridView>(view, "_statistics").Visible,
                        "statistics live on their own tab and never cover graph axes");
            }
        }
        Field<DateTimePicker>(view, "_from").Value = Epoch.AddSeconds(1);
        Check(Field<DataGridView>(view, "_records").RowCount == 0 && Field<List<Button>>(view, "_exports").All(button => !button.Enabled), "changed filters clear stale tables and disable exports");
        Check(Descendants(view).OfType<TabControl>().Single().TabPages.Cast<TabPage>().Select(page => page.Text)
            .SequenceEqual(new[] { "Records", "Graphs", "Statistics" }),
            "equipment workspace separates full-height Records, Graphs and Statistics");
        Check(!Descendants(view).OfType<Button>().Any(button => button.Text == "Recorded assets"),
            "recorded-assets discovery action is absent from clean workspace");
        form.Controls.Remove(view);
    }

    private sealed class FixtureSource : IEquipmentHistorySource
    {
        private readonly string category;
        public FixtureSource(string category = "Pumps") => this.category = category;
        public string Id => "future-qa";
        public IReadOnlyList<HistoryEquipment> GetEquipment() => new[]
        {
            new HistoryEquipment { Id = "FUTURE-QA", SourceId = Id, Name = "Future pump", Category = category, IntervalSeconds = 10,
                Metrics = new[] { new HistoryMetric { Id = "speed", Name = "Speed", Unit = "rpm" } } }
        };
        public IReadOnlyList<EquipmentHistoryRow> Read(EquipmentHistoryRequest request, CancellationToken token) =>
            new[] { new EquipmentHistoryRow { MetricId = "speed", TimestampUtc = Epoch, Value = 42, Quality = "Good" } };
    }

    private static EquipmentHistoryRequest Request(HistoryEquipment asset, HistoryMetric metric) =>
        new() { Equipment = asset, Metrics = new[] { metric }, FromUtc = Epoch, ToUtc = Epoch.AddMinutes(5) };
    private static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Instance)!.GetValue(instance)!;
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
    private static void Await(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(30)) { Application.DoEvents(); Thread.Sleep(10); }
        Check(task.IsCompleted, "asynchronous history operation completes");
        task.GetAwaiter().GetResult();
    }
    private static void Pump()
    {
        for (var index = 0; index < 10; index++) { Application.DoEvents(); Thread.Sleep(10); }
    }
    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException("Equipment history: " + message);
        assertions++;
    }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { Check(true, message); return; }
        throw new InvalidOperationException("Equipment history: expected " + typeof(T).Name + ": " + message);
    }
    private static void ProcessRun(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        if (!process.WaitForExit(60000)) throw new TimeoutException("Isolated PostgreSQL command exceeded 60 seconds: " + file);
        if (process.ExitCode != 0) throw new IOException("Isolated PostgreSQL command failed: " + file);
    }
}
