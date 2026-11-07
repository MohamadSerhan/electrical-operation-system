using System.Diagnostics;
using System.Text.Json;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class HistoryStressValidation
{
    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var assertions = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
        var power = new HistoryMetric { Id = "kw", Name = "Active Power", Unit = "kW", IsPower = true };
        var temperature = new HistoryMetric { Id = "temp", Name = "Temperature", Unit = "deg C" };
        var start = DateTime.UtcNow.AddDays(-1);
        var equipment = new HistoryEquipment { Id = "T1", Name = "SYNTHETIC COMPARISON STRESS TEST - NOT LIVE DATA",
            IntervalSeconds = 0.1, Metrics = new[] { power, temperature } };
        var rows = new List<EquipmentHistoryRow>(100_000);
        var expected = new List<(DateTime Time, double X, double Y)>();
        for (var i = 0; i < 50_000; i++)
        {
            var time = start.AddMilliseconds(i * 100);
            var temp = 40 + 2 * Math.Sin(i / 1000.0);
            var kw = 300 + 3 * (temp - 40) + 0.01 * (i % 7);
            var bad = i % 997 == 0;
            rows.Add(new EquipmentHistoryRow { EquipmentId = "T1", MetricId = power.Id, TimestampUtc = time,
                AcquiredUtc = time, ObservationId = i.ToString(), Value = kw, Quality = "Good", Revision = 1 });
            rows.Add(new EquipmentHistoryRow { EquipmentId = "T1", MetricId = temperature.Id, TimestampUtc = time,
                AcquiredUtc = time, ObservationId = i.ToString(), Value = bad ? null : temp,
                Quality = bad ? "Timeout" : "Good", Revision = 1 });
            if (!bad) expected.Add((time, temp, kw));
        }
        var result = new EquipmentHistoryResult { Request = new EquipmentHistoryRequest { Equipment = equipment,
            Metrics = equipment.Metrics, RawOnly = true, ThinSeconds = 0, FromUtc = start,
            ToUtc = start.AddSeconds(5000) }, Rows = rows };
        var watch = Stopwatch.StartNew();
        foreach (var style in Enum.GetValues<ChannelComparisonStyle>())
        foreach (var reverse in new[] { false, true })
        {
            var study = ChannelComparisonStudy.Create(result, reverse ? power : temperature,
                reverse ? temperature : power, style, true);
            Check(study.CandidateAcquisitions == 50_000 && study.Pairs.Length == expected.Count &&
                study.ExcludedAcquisitions == 50_000 - expected.Count, "All 100,000 rows are accounted for without inventing pairs.");
            Check(study.Pairs.Select(p => p.TimestampUtc).SequenceEqual(expected.Select(p => p.Time)),
                "Exact chronological source identity survives both axis directions and all presentation modes.");
            Check(study.Pairs.Zip(expected).All(p => p.First.XValue == (reverse ? p.Second.Y : p.Second.X) &&
                p.First.Value == (reverse ? p.Second.X : p.Second.Y)),
                "No interpolation, swapped units, aggregation or point reduction changes the comparison evidence.");
            Check(study.Pairs.Zip(study.Pairs.Skip(1)).All(p =>
                p.Second.ConnectFromPrevious == ((p.Second.TimestampUtc - p.First.TimestampUtc).TotalSeconds <= 0.15)),
                "Every failed acquisition breaks the chronological path.");
            var bins = ChannelComparisonBin.Build(study.Pairs);
            Check(bins.Length == 12 && bins.Sum(b => b.Count) == expected.Count,
                "The 12 response bins retain all exact pairs including boundary values.");
            var yValues = expected.Select(p => reverse ? p.X : p.Y).ToArray();
            Check(Math.Abs(bins.Sum(b => b.MeanY * b.Count) / expected.Count - yValues.Average()) < 1e-8 &&
                bins.Min(b => b.MinimumY) == yValues.Min() && bins.Max(b => b.MaximumY) == yValues.Max(),
                "Binned weighted mean and observed extrema agree with independently calculated raw values.");
            if (!reverse)
            {
                var path = Path.Combine(output, $"SYNTHETIC_100000_rows_{style}.pdf");
                HistorianReportExporter.ExportEquipmentHistory(result, path, "PDF", study);
                Check(new FileInfo(path).Length > 10_000, "The complete stress study exports in each comparison mode.");
            }
        }
        watch.Stop();
        File.WriteAllText(Path.Combine(output, "stress-evidence.json"), JsonSerializer.Serialize(new
        {
            Synthetic = true, Rows = rows.Count, Candidates = 50_000, Pairs = expected.Count,
            Excluded = 50_000 - expected.Count, ElapsedSeconds = watch.Elapsed.TotalSeconds, Assertions = assertions
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS: {assertions} history stress assertions, 100,000 rows, six axis/mode combinations, three PDFs; {watch.Elapsed.TotalSeconds:0.0}s.");
    }
}
