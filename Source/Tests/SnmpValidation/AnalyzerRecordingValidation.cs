using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

internal sealed class AnalyzerPreferenceScope : IDisposable
{
    private readonly string path = (string)typeof(OnlineAnalyzerView)
        .GetField("SettingsFile", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    private readonly byte[]? original;

    internal AnalyzerPreferenceScope() => original = File.Exists(path) ? File.ReadAllBytes(path) : null;

    public void Dispose()
    {
        if (original is null) File.Delete(path);
        else File.WriteAllBytes(path, original);
    }
}

internal static class AnalyzerRecordingValidation
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        using var preference = new AnalyzerPreferenceScope();
        var assertions = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
        var epoch = DateTime.UtcNow.AddSeconds(-30);
        MeterSnapshot Meter(string id, DateTime time, float voltage, long cycle = 1) => new()
        {
            TransformerName = id, CapturedAtUtc = time, AcquisitionCycleId = cycle,
            Quality = MeterQuality.Good,
            DecodedValues = Enumerable.Range(0, 16).Select(i => i < 3 ? voltage : 1f).ToArray()
        };
        var readings = new[] { Meter("T1", epoch, 230), Meter("T2", epoch.AddMilliseconds(19), 240) };
        using var view = new OnlineAnalyzerView(() => readings, () => new Dictionary<string, UpsSnapshot>(),
            () => new RecordingSettings());
        T Field<T>(string name) => (T)typeof(OnlineAnalyzerView).GetField(name, Instance)!.GetValue(view)!;
        void Set(string name, object value) => typeof(OnlineAnalyzerView).GetField(name, Instance)!.SetValue(view, value);
        void Call(string name, params object?[] args) => typeof(OnlineAnalyzerView).GetMethod(name, Instance)!.Invoke(view, args);
        Field<System.Windows.Forms.Timer>("_timer").Stop();
        var equipment = Field<CheckedListBox>("_equipmentList");
        for (var i = 0; i < equipment.Items.Count; i++) equipment.SetItemChecked(i, i < 2);
        var channels = Field<CheckedListBox>("_channelList");
        for (var i = 0; i < channels.Items.Count; i++)
            channels.SetItemChecked(i, (int)channels.Items[i].GetType().GetProperty("TransformerIndex")!.GetValue(channels.Items[i])! == 0);
        Field<TextBox>("_folder").Text = output;
        Field<TextBox>("_fileName").Text = "timestamp-stress";
        Call("OnStartClicked", null, EventArgs.Empty);
        var states = Field<IList>("_series");
        object Get(object state, string property) => state.GetType().GetProperty(property)?.GetValue(state)
            ?? state.GetType().GetField(property)!.GetValue(state)!;
        List<ReportPoint> Points(int i) => (List<ReportPoint>)Get(states[i]!, "Recorded");
        Check(states.Count == 2 && Points(0).Single().T == epoch &&
            Points(1).Single().T == epoch.AddMilliseconds(19),
            "Each equipment report point retains its own exact source timestamp.");
        void Sample()
        {
            Set("_lastSampleUtc", DateTime.MinValue);
            Call("Sample");
        }
        Sample();
        Check(Field<long>("_rows") == 1, "Repeated cached snapshots never create duplicate rows.");
        readings = new[] { Meter("T1", epoch.AddSeconds(1), 231), readings[1] };
        Sample();
        Check((int)Get(states[0]!, "RecordedTotal") == 2 && (int)Get(states[1]!, "RecordedTotal") == 1,
            "An unchanged second device is not falsely counted as a missing acquisition.");
        readings = new[] { Meter("T1", epoch.AddMilliseconds(500), 999), readings[1] };
        Sample();
        Check(Points(0).Count == 2 && Points(0).All(p => p.V != 999),
            "Out-of-order source clocks are never replayed into the recording.");
        readings = new[] { Meter("T1", epoch.AddSeconds(2), float.NaN), Meter("T2", epoch.AddSeconds(2.019), 241) };
        Sample();
        readings = new[] { Meter("T1", epoch.AddSeconds(3), 232), readings[1] };
        Sample();
        Check(Points(0).Count == 4 && double.IsNaN(Points(0)[2].V),
            "Invalid acquisitions survive as explicit evidence breaks, not numerical samples.");
        Call("CloseFile");
        var csv = File.ReadAllLines(Field<string>("_filePath"));
        var header = csv[0].Split(',');
        var t1Column = Array.IndexOf(header, "T1 | acquisition UTC");
        var t2Column = Array.IndexOf(header, "T2 | acquisition UTC");
        var firstRow = csv[1].Split(',');
        Check(t1Column >= 0 && t2Column >= 0 &&
            DateTime.Parse(firstRow[t1Column], null, DateTimeStyles.RoundtripKind) == epoch &&
            DateTime.Parse(firstRow[t2Column], null, DateTimeStyles.RoundtripKind) == epoch.AddMilliseconds(19),
            "CSV includes lossless per-equipment UTC acquisition times.");
        Check(csv[2].Split(',')[t2Column] == "", "CSV leaves absent equipment timestamps blank rather than repeating old data.");
        Call("CaptureReport");
        var snapshot = Field<object>("_lastReport");
        var result = (EquipmentHistoryResult)Get(snapshot, "Result");
        var tuples = (List<Tuple<HistoryMetric, int, List<ReportPoint>>>)Get(snapshot, "Series");
        var analysis = EquipmentAnalysis.BuildFromSeries(result, tuples);
        Check(analysis.TotalSamples == 6 && analysis.MissingSamples == 1 && analysis.Channels.Sum(c => c.Points.Count) == 5,
            "Report validity denominator counts real per-equipment acquisitions, not asynchronous row slots.");
        var t1 = analysis.Channels.Single(s => s.Metric.Name.StartsWith("T1 |"));
        Check(t1.Points.Count == 3 && t1.BreakBefore.Contains(t1.Points[^1].T),
            "An invalid reading breaks report continuity even when neighboring readings are close.");
        Check(result.Request.FromUtc == epoch, "Immediate cached evidence is included in the honest report period.");

        var state = states[0]!;
        var longEpoch = DateTime.UtcNow.AddSeconds(-5100);
        var longRun = Enumerable.Range(0, 200_001).Select(i => new ReportPoint(longEpoch.AddMilliseconds(i * 25),
            i == 177_777 ? 9999 : i == 133_333 ? -999 : 230)).ToList();
        state.GetType().GetField("Recorded")!.SetValue(state, longRun);
        state.GetType().GetField("RecordedTotal")!.SetValue(state, longRun.Count);
        var spill = typeof(OnlineAnalyzerView).GetMethod("ThinRecorded", BindingFlags.Static | BindingFlags.NonPublic)!;
        var clock = Stopwatch.StartNew();
        spill.Invoke(null, new[] { state });
        clock.Stop();
        var chunks = (List<string>)Get(state, "RecordedChunks");
        var chunk = chunks.Single();
        Check(Points(0).Count == 0 && new FileInfo(chunk).Length == 200_001L * 16,
            "Crossing the old thinning threshold spills every measurement, without dropping alternate values or extrema.");
        Call("CaptureReport");
        snapshot = Field<object>("_lastReport");
        var reader = typeof(OnlineAnalyzerView).GetMethod("ReadReportSeries", BindingFlags.Static | BindingFlags.NonPublic)!;
        var loaded = ((IEnumerable<Tuple<HistoryMetric, int, List<ReportPoint>>>)reader.Invoke(null, new[] { snapshot })!).ToList();
        var evidence = loaded[0].Item3;
        Check(evidence.SequenceEqual(longRun), "All 200,001 UTC timestamps and exact values round-trip through disk evidence in order.");
        Check(evidence.Min(p => p.V) == -999 && evidence.Max(p => p.V) == 9999,
            "Long-recording report evidence retains short-lived minima and maxima.");
        var longMetric = loaded[0].Item1;
        var longResult = new EquipmentHistoryResult { Request = new EquipmentHistoryRequest
        {
            Equipment = new HistoryEquipment { Id = "ANALYZER", Name = "SYNTHETIC STRESS TEST - NOT LIVE DATA",
                IntervalSeconds = 0.025, Metrics = new[] { longMetric } },
            Metrics = new[] { longMetric }, FromUtc = longRun[0].T, ToUtc = longRun[^1].T.AddMilliseconds(25)
        }};
        var longAnalysis = EquipmentAnalysis.BuildFromSeries(longResult, loaded.Take(1));
        Check(longAnalysis.TotalSamples == 200_001 && longAnalysis.MissingSamples == 0 &&
            longAnalysis.Channels[0].Median == 230 && longAnalysis.Channels[0].P95 == 230,
            "Long-recording analysis keeps exact count, completeness, median and P95.");
        Check(longAnalysis.Channels[0].LowAt == longRun[133_333].T &&
            longAnalysis.Channels[0].PeakAt == longRun[177_777].T,
            "Long-recording report extremes retain their original sub-second acquisition timestamps.");
        Check(EquipmentAnalysis.Stamp(longRun[177_777].T).Contains(
            longRun[177_777].T.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)),
            "PDF evidence timestamps retain source sub-second precision, not just integer seconds.");
        EquipmentReportRenderer.WritePdf(longAnalysis, Path.Combine(output, "SYNTHETIC_200001_readings.pdf"));
        Check(new FileInfo(Path.Combine(output, "SYNTHETIC_200001_readings.pdf")).Length > 10_000,
            "A real PDF is generated from every preserved long-recording observation.");
        using (var stream = new FileStream(chunk, FileMode.Append, FileAccess.Write)) stream.WriteByte(0);
        var rejected = false;
        try { _ = ((IEnumerable<Tuple<HistoryMetric, int, List<ReportPoint>>>)reader.Invoke(null, new[] { snapshot })!).ToList(); }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected, "Incomplete evidence blocks fail explicitly instead of producing success-shaped PDFs.");
        view.Dispose();
        Check(!File.Exists(chunk), "Temporary report evidence is cleaned after the owning analyzer is disposed.");

        var type = typeof(OnlineAnalyzerView);
        object Nested(string name) => Activator.CreateInstance(type.GetNestedType(name, BindingFlags.NonPublic)!, true)!;
        void Property(object item, string name, object value) => item.GetType().GetProperty(name)!.SetValue(item, value);
        var total = Nested("EquipmentItem");
        Property(total, "Id", "TOTAL");
        var acquire = type.GetMethod("Acquire", BindingFlags.Static | BindingFlags.NonPublic)!;
        var four = new[] { "T1", "T2", "T3", "T4" }.Select(id => Meter(id, epoch, 230)).ToArray();
        object? Acquire() => acquire.Invoke(null, new object[] { total, four, new Dictionary<string, UpsSnapshot>() });
        Check(Acquire() is not null, "A complete synchronized transformer cycle produces TOTAL evidence.");
        four[3].AcquisitionCycleId = 2;
        Check(((string)Get(Acquire()!, "Quality")).StartsWith("Unavailable"),
            "TOTAL never combines transformers belonging to different acquisition cycles.");
        four[3].AcquisitionCycleId = 1;
        four[3].CapturedAtUtc = epoch.AddSeconds(3);
        Check(((string)Get(Acquire()!, "Quality")).StartsWith("Unavailable"),
            "TOTAL never treats widely separated transformer acquisition times as simultaneous.");
        four[3] = Meter("T3", epoch, 230);
        Check(Acquire() is null, "Duplicate transformer identity cannot stand in for a missing fourth transformer.");
        var channel = Nested("ChannelItem");
        Property(channel, "IsUps", true);
        Property(channel, "UpsKey", "OutputVoltage");
        Property(channel, "VoltageMode", "LL");
        var key = type.GetMethod("AnalyzerKey", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check((string)key.Invoke(null, new[] { channel })! == "OutputVoltageLL",
            "Selecting UPS output L-L requests the verified L-L binding, not only the L-N output channel.");
        Property(channel, "VoltageMode", "LN");
        Check((string)key.Invoke(null, new[] { channel })! == "OutputVoltage",
            "Selecting UPS L-N alone keeps the fast L-N-only request.");
        Property(channel, "UpsKey", "InputVoltage");
        Check((string)key.Invoke(null, new[] { channel })! == "InputVoltage",
            "Input voltage retains the service's reference-aware LN/LL expansion.");
        var failedReading = Nested("Reading");
        var failedUps = new UpsSnapshot { DeviceId = "test", Quality = UpsQuality.Timeout,
            Values = new Dictionary<string, double> { ["OutputFrequency"] = 50 } };
        Property(failedReading, "Ups", failedUps);
        Property(channel, "UpsKey", "OutputFrequency");
        Property(channel, "VoltageMode", "");
        var valueOf = type.GetMethod("ValueOf", BindingFlags.Static | BindingFlags.NonPublic)!;
        Check(valueOf.Invoke(null, new[] { failedReading, channel }) is null,
            "A failed UPS response cannot replay a populated cached numeric field as good evidence.");

        using var shutdownView = new OnlineAnalyzerView(() => new[] { Meter("T1", DateTime.UtcNow, 230) },
            () => new Dictionary<string, UpsSnapshot>(), () => new RecordingSettings());
        T ShutdownField<T>(string name) => (T)type.GetField(name, Instance)!.GetValue(shutdownView)!;
        ShutdownField<System.Windows.Forms.Timer>("_timer").Stop();
        ShutdownField<CheckedListBox>("_equipmentList").SetItemChecked(0, true);
        ShutdownField<CheckedListBox>("_channelList").SetItemChecked(0, true);
        ShutdownField<TextBox>("_folder").Text = output;
        ShutdownField<TextBox>("_fileName").Text = "shutdown-evidence";
        type.GetMethod("OnStartClicked", Instance)!.Invoke(shutdownView, new object?[] { null, EventArgs.Empty });
        var finish = shutdownView.FinishRecordingAsync();
        var deadline = Stopwatch.StartNew();
        while (!finish.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(30))
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Check(finish.IsCompletedSuccessfully, "Graceful application shutdown waits for analyzer evidence and PDF finalization.");
        finish.GetAwaiter().GetResult();
        var shutdownCsv = Directory.GetFiles(output, "shutdown-evidence_*.csv").Single();
        var shutdownPdf = Directory.GetFiles(output, "shutdown-evidence_*_report.pdf").Single();
        Check(File.ReadAllLines(shutdownCsv).Length == 2 &&
            File.ReadAllText(shutdownPdf).EndsWith("%%EOF") &&
            Directory.GetFiles(output, "*.partial").Length == 0,
            "Closing an active analyzer produces a finalized one-acquisition CSV and complete PDF, with no partial output.");
        Console.WriteLine($"PASS: {assertions} analyzer recording stress assertions; lossless 200,001-point spill {clock.Elapsed.TotalMilliseconds:0.0} ms.");
    }
}
