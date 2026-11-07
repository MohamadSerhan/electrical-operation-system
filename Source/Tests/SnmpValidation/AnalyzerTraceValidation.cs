using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class AnalyzerTraceValidation
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var assertions = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
        var captured = DateTime.UtcNow;
        var voltage = 230f;
        using var view = new OnlineAnalyzerView(() => new[]
        {
            new MeterSnapshot { TransformerName = "T1", CapturedAtUtc = captured, Quality = MeterQuality.Good,
                DecodedValues = Enumerable.Range(0, 16).Select(i => i < 3 ? voltage : 1f).ToArray() }
        }, () => new Dictionary<string, UpsSnapshot>(), () => new RecordingSettings());
        T Field<T>(string name) => (T)typeof(OnlineAnalyzerView).GetField(name, Instance)!.GetValue(view)!;
        void Set(string name, object value) => typeof(OnlineAnalyzerView).GetField(name, Instance)!.SetValue(view, value);
        void Call(string name, params object?[] args) => typeof(OnlineAnalyzerView).GetMethod(name, Instance)!.Invoke(view, args);
        Field<System.Windows.Forms.Timer>("_timer").Stop();
        var equipment = Field<CheckedListBox>("_equipmentList");
        for (var i = 0; i < equipment.Items.Count; i++)
            equipment.SetItemChecked(i, (string)equipment.Items[i].GetType().GetProperty("Id")!.GetValue(equipment.Items[i])! == "T1");
        var channels = Field<CheckedListBox>("_channelList");
        for (var i = 0; i < channels.Items.Count; i++)
            channels.SetItemChecked(i, (int)channels.Items[i].GetType().GetProperty("TransformerIndex")!.GetValue(channels.Items[i])! == 0);
        Field<CheckBox>("_saveToFile").Checked = false;
        Field<ComboBox>("_windowChoice").SelectedIndex = 3;
        view.SetPageActive(true);
        Call("OnStartClicked", null, EventArgs.Empty);
        var chart = Field<AnalyticsTrendChart>("_chart");
        Check(Field<long>("_rows") == 1, "Start records one real acquisition.");
        Check(chart.WindowStartUtc == captured && chart.WindowEndUtc == captured.AddSeconds(10),
            "The first sample starts at the left with the chosen fixed time scale, not at the right.");
        Check(chart.TraceRevealUtc == captured, "The first sample is visible immediately.");
        var start = chart.WindowStartUtc;
        Thread.Sleep(110);
        captured = DateTime.UtcNow;
        voltage = 240;
        Call("Sample");
        Call("RefreshChart", false);
        var firstReveal = chart.TraceRevealUtc!.Value;
        var series = chart.Series;
        var rows = Field<long>("_rows");
        Thread.Sleep(35);
        Call("RefreshChart", false);
        var secondReveal = chart.TraceRevealUtc!.Value;
        Check(firstReveal < secondReveal && secondReveal < captured,
            "Intermediate animation frames progressively reveal the measured segment.");
        Check(ReferenceEquals(series, chart.Series), "Animation reuses measured series instead of copying them every frame.");
        Check(chart.WindowStartUtc == start && chart.WindowEndUtc == start.AddSeconds(10),
            "The time axis does not expand or jump while the initial sweep fills.");
        Check(Field<long>("_rows") == rows && chart.Series[0].Points.Count == 2,
            "Animation creates no samples and does not change saved statistics.");
        Thread.Sleep(125);
        Call("RefreshChart", false);
        Check(chart.TraceRevealUtc == captured, "The animation reaches, but never extrapolates beyond, the newest real sample.");
        Set("_lastRefreshUtc", DateTime.UtcNow);
        Set("_lastFrameMilliseconds", -100L);
        var lastFrame = Field<long>("_lastFrameMilliseconds");
        Call("OnTick", null, EventArgs.Empty);
        Check(Field<long>("_lastFrameMilliseconds") > lastFrame,
            "The waveform frame updates independently of the 500 ms statistics refresh.");
        Set("_traceStartUtc", DateTime.UtcNow.AddSeconds(-20));
        Call("RefreshChart", false);
        Check(Math.Abs((chart.WindowEndUtc - chart.WindowStartUtc).TotalSeconds - 10) < 0.00001 &&
            chart.WindowStartUtc > Field<DateTime>("_traceStartUtc") &&
            Math.Abs((chart.WindowEndUtc - DateTime.UtcNow).TotalSeconds) < 0.2,
            "After the screen fills the chart scrolls at the same fixed scale.");
        Set("_paused", true);
        Call("RefreshDisplay", true);
        var pausedStart = chart.WindowStartUtc;
        var pausedEnd = chart.WindowEndUtc;
        Thread.Sleep(40);
        Call("RefreshDisplay", true);
        Check(chart.WindowStartUtc == pausedStart && chart.WindowEndUtc == pausedEnd && chart.TraceRevealUtc is null,
            "Pause freezes the view and shows all confirmed samples.");
        Set("_paused", false);
        Call("ClearDisplay");
        captured = DateTime.UtcNow.AddMilliseconds(10);
        Thread.Sleep(110);
        Call("Sample");
        Call("RefreshDisplay", true);
        Check(chart.WindowStartUtc == captured && chart.Series[0].Points.Count == 1,
            "Clear restarts the display sweep from the left without clearing the recording.");

        var epoch = DateTime.UtcNow;
        using var trace = new AnalyticsTrendChart
        {
            Size = new Size(1000, 420), ChartType = AnalyticsChartType.Line, ShowMarkers = true,
            WindowStartUtc = epoch, WindowEndUtc = epoch.AddSeconds(10),
            ChartTitle = "Confirmed measurements - progressive sweep", PollingIntervalSeconds = 1,
            Series = new[] { new AnalyticsTrendSeries { Name = "Measured voltage", Color = Color.Red,
                Points = new[]
                {
                    new AnalyticsTrendPoint { TimestampUtc = epoch, Value = 230, IsValid = true },
                    new AnalyticsTrendPoint { TimestampUtc = epoch.AddSeconds(1), Value = 240, IsValid = true },
                    new AnalyticsTrendPoint { TimestampUtc = epoch.AddSeconds(2), Value = 225, IsValid = true }
                } } }
        };
        var x = new List<float>();
        foreach (var time in new[] { 0.25, 0.5, 0.75, 1.25, 1.75, 2.0 })
        {
            trace.TraceRevealUtc = epoch.AddSeconds(time);
            using var bitmap = new Bitmap(trace.Width, trace.Height);
            trace.DrawToBitmap(bitmap, trace.ClientRectangle);
            bitmap.Save(Path.Combine(output, $"trace-{time:0.00}.png"));
            var targets = (IList)typeof(AnalyticsTrendChart).GetField("_hitTargets", Instance)!.GetValue(trace)!;
            Check(targets.Count == (int)Math.Floor(time) + 1,
                "Only real, already revealed measurements are available to tooltips.");
            var rightmostRed = 0;
            for (var px = 72; px < 982; px++)
            for (var py = 60; py < 368; py++)
            {
                var color = bitmap.GetPixel(px, py);
                if (color.R > 170 && color.G < 100 && color.B < 100) rightmostRed = Math.Max(rightmostRed, px);
            }
            var expectedX = 72 + 910 * time / 10;
            Check(Math.Abs(rightmostRed - expectedX) <= 5,
                "The painted trace head follows the fractional frame position, not whole-sample jumps.");
            x.Add(rightmostRed);
        }
        Check(x.Zip(x.Skip(1), (a, b) => b > a).All(value => value), "The sweep head moves monotonically left to right.");
        trace.TraceRevealUtc = null;
        using (var bitmap = new Bitmap(trace.Width, trace.Height))
        {
            trace.DrawToBitmap(bitmap, trace.ClientRectangle);
            Check(((IList)typeof(AnalyticsTrendChart).GetField("_hitTargets", Instance)!.GetValue(trace)!).Count == 3,
                "Ordinary charts remain fully rendered with animation disabled.");
        }
        trace.Series[0].Points[1].IsValid = false;
        trace.TraceRevealUtc = epoch.AddSeconds(1.5);
        using (var bitmap = new Bitmap(trace.Width, trace.Height))
        {
            trace.DrawToBitmap(bitmap, trace.ClientRectangle);
            var colored = 0;
            for (var px = 110; px < 200; px++)
            for (var py = 60; py < 368; py++)
            {
                var color = bitmap.GetPixel(px, py);
                if (color.R > 170 && color.G < 100 && color.B < 100) colored++;
            }
            Check(colored == 0, "Smooth reveal never draws across unavailable measurements.");
        }
        trace.Series = Enumerable.Range(0, 6).Select(seriesIndex => new AnalyticsTrendSeries
        {
            Name = "Signal " + seriesIndex, Color = Color.FromArgb(30 + seriesIndex * 30, 80, 190),
            Points = Enumerable.Range(0, 3600).Select(index => new AnalyticsTrendPoint
            {
                TimestampUtc = epoch.AddSeconds(index / 360.0), Value = 230 + seriesIndex + Math.Sin(index * 0.02), IsValid = true
            }).ToArray()
        }).ToArray();
        using (var bitmap = new Bitmap(trace.Width, trace.Height))
        {
            var frames = new List<double>();
            for (var index = 0; index < 12; index++)
            {
                trace.TraceRevealUtc = epoch.AddSeconds(5 + index * 0.03);
                var elapsed = Stopwatch.StartNew();
                trace.DrawToBitmap(bitmap, trace.ClientRectangle);
                frames.Add(elapsed.Elapsed.TotalMilliseconds);
            }
            frames.Sort();
            Console.WriteLine($"Six signals x 3,600 points: median frame {frames[frames.Count / 2]:0.0} ms.");
        }
        Console.WriteLine($"Analyzer trace assertions passed: {assertions}.");
    }
}
