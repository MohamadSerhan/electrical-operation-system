using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class HistoryTraceValidation
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run()
    {
        var assertions = 0;
        void Check(bool success, string message)
        {
            if (!success) throw new InvalidOperationException(message);
            assertions++;
        }
        using var view = new EquipmentHistoryView(new EquipmentHistoryService(Array.Empty<IEquipmentHistorySource>()));
        using var form = new Form { StartPosition = FormStartPosition.Manual,
            Location = new Point(-2600, -1800), ClientSize = new Size(1100, 700) };
        form.Controls.Add(view);
        form.Show();
        T Field<T>(string name) => (T)typeof(EquipmentHistoryView).GetField(name, Instance)!.GetValue(view)!;
        void Set(string name, object value) => typeof(EquipmentHistoryView).GetField(name, Instance)!.SetValue(view, value);
        void Call(string name, params object[] args) => typeof(EquipmentHistoryView).GetMethod(name, Instance)!.Invoke(view, args);
        var epoch = DateTime.UtcNow.AddDays(-2);
        var voltage = new HistoryMetric { Id = "voltage", Name = "Voltage", Unit = "V" };
        var alarm = new HistoryMetric { Id = "alarm:events", Name = "Alarm", IsAlarm = true };
        var equipment = new HistoryEquipment { Id = "T1", Name = "Transformer 1", IntervalSeconds = 60,
            Metrics = new[] { voltage, alarm } };
        var result = new EquipmentHistoryResult
        {
            Request = new EquipmentHistoryRequest { Equipment = equipment, Metrics = equipment.Metrics,
                FromUtc = epoch, ToUtc = epoch.AddHours(1), RawOnly = true },
            Rows = new[]
            {
                new EquipmentHistoryRow { MetricId = voltage.Id, TimestampUtc = epoch.AddMinutes(2), Value = 230, Quality = "Good" },
                new EquipmentHistoryRow { MetricId = voltage.Id, TimestampUtc = epoch.AddMinutes(3), Value = 240, Quality = "Good" },
                new EquipmentHistoryRow { MetricId = voltage.Id, TimestampUtc = epoch.AddMinutes(4), Quality = "Timeout" },
                new EquipmentHistoryRow { MetricId = voltage.Id, TimestampUtc = epoch.AddMinutes(5), Value = 225, Quality = "Good" }
            },
            AlarmEvents = new[] { new EquipmentHistoryRow { ConditionKey = "alarm", ConditionName = "Fault",
                TimestampUtc = epoch.AddMinutes(3), EventState = "Active" } }
        };
        Set("_result", result);
        Call("RenderTrend");
        var chart = Field<AnalyticsTrendChart>("_chart");
        var timer = Field<System.Windows.Forms.Timer>("_trendTimer");
        var rows = result.Rows.ToArray();
        var series = chart.Series;
        Check(timer.Enabled && timer.Interval == 30 && chart.TraceRevealUtc == epoch.AddMinutes(2),
            "History begins at the first saved point and uses an independent 30 ms frame timer.");
        Check(chart.WindowStartUtc == epoch && chart.WindowEndUtc == epoch.AddHours(1),
            "History keeps the complete requested time scale, not a moving live window.");
        Call("AdvanceTrendReveal", 600.0);
        Check(chart.TraceRevealUtc == epoch.AddMinutes(3.5), "Halfway frame is an interpolated timestamp, not a sample jump.");
        Check(ReferenceEquals(series, chart.Series) && chart.Series[0].Points.Count == 4 &&
            !chart.Series[0].Points[2].IsValid && result.Rows.SequenceEqual(rows),
            "Frames reuse the full measured series and preserve missing samples and source rows.");
        Check(result.Statistics[0].ValidCount == 3 && result.Statistics[0].Maximum == 240,
            "Statistics always include all saved data, even ahead of the reveal.");
        var alarmChart = Field<AnalyticsTrendChart>("_alarmChart");
        Check(alarmChart.ChartType == AnalyticsChartType.Scatter && alarmChart.TraceRevealUtc is null &&
            alarmChart.Series[0].Points.Count == 1, "Alarm evidence remains complete observed markers.");
        Call("AdvanceTrendReveal", 1200.0);
        Check(!timer.Enabled && chart.TraceRevealUtc is null && chart.Series[0].Points.Count == 4,
            "The 1.2 second reveal ends with every saved point visible and the timer stopped.");
        Call("RenderTrend");
        typeof(AnalyticsTrendChart).GetMethod("OnMouseWheel", Instance)!.Invoke(chart,
            new object[] { new MouseEventArgs(MouseButtons.None, 0, 500, 200, 120) });
        Check(!timer.Enabled && chart.TraceRevealUtc is null, "Wheel zoom immediately completes the reveal.");
        Call("RenderTrend");
        typeof(AnalyticsTrendChart).GetMethod("OnMouseDown", Instance)!.Invoke(chart,
            new object[] { new MouseEventArgs(MouseButtons.Left, 1, 500, 200, 0) });
        Check(!timer.Enabled && chart.TraceRevealUtc is null, "Mouse interaction never waits for animation.");
        Call("RenderTrend");
        view.Visible = false;
        Check(!timer.Enabled && chart.TraceRevealUtc is null && !Field<Stopwatch>("_trendClock").IsRunning,
            "Leaving the page stops animation work.");
        view.Visible = true;
        Check(!timer.Enabled && chart.TraceRevealUtc is null, "Returning does not replay old results.");
        Call("RenderTrend");
        Call("ClearResult");
        Check(!timer.Enabled && chart.TraceRevealUtc is null && chart.Series.Count == 0,
            "Clearing or changing the query cannot leave an old reveal running.");
        Set("_result", result);
        result.Rows = rows.Take(1).ToArray();
        Call("RenderTrend");
        Check(!timer.Enabled && chart.TraceRevealUtc is null, "Single-time results are immediately visible.");
        result.Rows = Array.Empty<EquipmentHistoryRow>();
        Call("RenderTrend");
        Check(!timer.Enabled && chart.TraceRevealUtc is null, "Empty results do not animate.");
        result.Rows = rows;
        result.Request.Metrics = new[] { alarm };
        Call("RenderTrend");
        Check(chart.ChartType == AnalyticsChartType.Scatter && !timer.Enabled,
            "Alarm-only history never becomes an invented continuous waveform.");
        result.Request.Metrics = equipment.Metrics;
        result.Request.IsCombined = true;
        result.Request.IncludedEquipment = new[] { equipment };
        voltage.SourceId = equipment.SourceId = "fixture";
        voltage.SourceMetricId = voltage.Id;
        voltage.EquipmentId = equipment.Id;
        foreach (var row in rows) row.EquipmentId = equipment.Id;
        foreach (var row in result.AlarmEvents) row.EquipmentId = equipment.Id;
        var optionType = typeof(EquipmentHistoryView).GetNestedType("HistoryChannelSelection", BindingFlags.NonPublic)!;
        var option = Activator.CreateInstance(optionType, true)!;
        optionType.GetProperty("Metric")!.SetValue(option, voltage);
        optionType.GetProperty("SourceId")!.SetValue(option, "fixture");
        optionType.GetProperty("EquipmentId")!.SetValue(option, equipment.Id);
        var choice = Field<ComboBox>("_trendMetric");
        choice.Items.Add(option);
        choice.SelectedItem = option;
        Check(timer.Enabled && chart.TraceRevealUtc == epoch.AddMinutes(2) &&
            chart.Series.Count == 1 && chart.Series[0].Name == "Transformer 1",
            "Combined equipment history uses the same reveal for the selected compatible channel.");
        Call("AdvanceTrendReveal", 600.0);
        choice.SelectedIndex = -1;
        Check(!timer.Enabled && chart.Series.Count == 0 && chart.TraceRevealUtc is null,
            "Changing combined trend selection cancels the previous reveal.");
        choice.SelectedItem = option;
        Check(timer.Enabled, "Selecting a new combined trend starts its own reveal.");
        view.Dispose();
        Check(!timer.Enabled, "Disposing history stops and disposes its frame timer.");
        Console.WriteLine($"History trace assertions passed: {assertions}.");
    }
}
