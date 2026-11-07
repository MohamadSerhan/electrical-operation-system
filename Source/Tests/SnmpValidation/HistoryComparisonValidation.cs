using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class HistoryComparisonValidation
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var assertions = 0;
        void Check(bool success, string message)
        {
            if (!success) throw new InvalidOperationException(message);
            assertions++;
        }
        using var view = new EquipmentHistoryView(new EquipmentHistoryService(Array.Empty<IEquipmentHistorySource>()));
        using var form = new Form { StartPosition = FormStartPosition.Manual,
            Location = new Point(-2600, -1800), ClientSize = new Size(1160, 800) };
        form.Controls.Add(view);
        form.Show();
        T Field<T>(string name) => (T)typeof(EquipmentHistoryView).GetField(name, Instance)!.GetValue(view)!;
        void Call(string name) => typeof(EquipmentHistoryView).GetMethod(name, Instance)!.Invoke(view, null);
        var power = new HistoryMetric { Id = "power", Name = "Active Power", Unit = "kW", IsPower = true };
        var temperature = new HistoryMetric { Id = "temperature", Name = "Temperature", Unit = "deg C" };
        var peak = new HistoryMetric { Id = "peak", Name = "Power peak", Unit = "kW", PeakSourceMetricId = "power" };
        var alarm = new HistoryMetric { Id = "alarm:events", Name = "Alarm", IsAlarm = true };
        var equipment = new HistoryEquipment { Id = "T1", Name = "Transformer 1", IntervalSeconds = 60,
            Metrics = new[] { power, temperature, alarm } };
        var epoch = DateTime.UtcNow.AddDays(-1);
        var otherDevice = new HistoryMetric { Id = "other-device", EquipmentId = "UPS-A01", Name = "UPS input", Unit = "kW" };
        var invalidSelection = new EquipmentHistoryResult { Request = new EquipmentHistoryRequest {
            Equipment = equipment, Metrics = new[] { power, otherDevice }, FromUtc = epoch, ToUtc = epoch.AddMinutes(1) } };
        var rejectedDifferentDevice = false;
        try { ChannelComparisonStudy.Create(invalidSelection, power, otherDevice, ChannelComparisonStyle.RawPoints, true); }
        catch (ArgumentException) { rejectedDifferentDevice = true; }
        Check(rejectedDifferentDevice, "A malformed single-equipment request cannot compare a transformer with UPS channels.");
        EquipmentHistoryRow Row(string metric, int minute, double? value, string observation = "") =>
            new() { MetricId = metric, TimestampUtc = epoch.AddMinutes(minute), AcquiredUtc = epoch.AddMinutes(minute),
                Value = value, ObservationId = observation, Quality = "Good", EquipmentId = "T1" };
        var rows = new List<EquipmentHistoryRow>();
        foreach (var (minute, kw, temp) in new[] { (0, 100.0, 40.0), (1, 200.0, 35.0), (2, 150.0, 45.0) })
        {
            rows.Add(Row("power", minute, kw));
            rows.Add(Row("temperature", minute, temp));
        }
        rows.AddRange(new[] { Row("power", 3, 300), Row("temperature", 3, null),
            Row("power", 4, 400), Row("temperature", 4, double.NaN),
            Row("power", 5, 500, "one"), Row("temperature", 5, 55, "two"),
            Row("power", 6, 600), Row("temperature", 6, 60), Row("temperature", 6, 61),
            Row("power", 7, 700), Row("temperature", 7, 70),
            Row("power", 8, 800), Row("temperature", 8, 80),
            Row("power", 9, 900), Row("temperature", 9, 90),
            Row("power", 10, 1000), Row("temperature", 10, 100),
            Row("power", 11, 1100), Row("temperature", 11, 110),
            Row("power", 12, 1200), Row("temperature", 12, 120) });
        rows.Single(row => row.MetricId == "temperature" && row.TimestampUtc == epoch.AddMinutes(7)).Quality = "Timeout";
        rows.Single(row => row.MetricId == "temperature" && row.TimestampUtc == epoch.AddMinutes(8)).EquipmentId = "T2";
        rows.Single(row => row.MetricId == "temperature" && row.TimestampUtc == epoch.AddMinutes(9)).AcquiredUtc = epoch.AddMinutes(9).AddSeconds(1);
        rows.Single(row => row.MetricId == "temperature" && row.TimestampUtc == epoch.AddMinutes(10)).Revision = 2;
        rows.Single(row => row.MetricId == "temperature" && row.TimestampUtc == epoch.AddMinutes(11)).TimestampUtc = epoch.AddMinutes(11).AddMilliseconds(1);
        var result = new EquipmentHistoryResult { Request = new EquipmentHistoryRequest { Equipment = equipment,
            Metrics = equipment.Metrics, RawOnly = true, FromUtc = epoch, ToUtc = epoch.AddMinutes(12) }, Rows = rows };
        typeof(EquipmentHistoryView).GetField("_result", Instance)!.SetValue(view, result);
        Call("RenderTrend");
        var chart = Field<AnalyticsTrendChart>("_chart");
        var x = Field<ComboBox>("_xChannel");
        var y = Field<ComboBox>("_yChannel");
        var page = chart.Parent;
        while (page is not TabPage) page = page!.Parent;
        ((TabControl)page.Parent!).SelectedTab = (TabPage)page;
        Check(x.SelectedIndex == 0 && x.Text == "Time (UTC)" && !chart.NumericXAxis && chart.Series.Count == 2,
            "The default is the existing complete multi-channel time plot.");
        Check(x.Items.Count == 3 && !x.Items.Contains(alarm), "Axis choices contain loaded measurement channels, not alarms.");
        x.SelectedItem = temperature;
        Check(ReferenceEquals(y.SelectedItem, power) && chart.NumericXAxis && chart.ChartType == AnalyticsChartType.Scatter,
            "Selecting temperature X chooses power Y and enables a real numeric scatter plot.");
        Check(chart.Series.Single().Points.Count == 3, "Only exact, unambiguous, valid same-equipment acquisition pairs survive.");
        var points = chart.Series.Single().Points;
        Check(points.Select(p => p.XValue).SequenceEqual(new double?[] { 40, 35, 45 }) &&
            points.Select(p => p.Value).SequenceEqual(new double?[] { 100, 200, 150 }),
            "X is temperature and Y is power; timestamps are evidence, not the X coordinate.");
        Check(chart.XAxisLabel == "Temperature (deg C)" && chart.YAxisLabel == "Active Power (kW)" &&
            chart.TraceRevealUtc is null, "Numeric axes use channel labels/units and do not animate a false time sweep.");
        var mode = Field<ComboBox>("_comparisonMode");
        Check(chart.ComparisonStyle == ChannelComparisonStyle.MeanResponse && chart.Series[0].ComparisonBins.Count == 3,
            "Channel comparison defaults to an explicitly labelled mean-and-observed-range response, not bare dots.");
        var yTime = Field<AnalyticsTrendChart>("_comparisonYTime");
        var xTime = Field<AnalyticsTrendChart>("_comparisonXTime");
        Check(yTime.Visible && xTime.Visible && yTime.Series[0].Points.Select(p => p.Value).SequenceEqual(points.Select(p => p.Value)) &&
            xTime.Series[0].Points.Select(p => p.Value).SequenceEqual(points.Select(p => p.XValue)) &&
            yTime.WindowStartUtc == xTime.WindowStartUtc && yTime.WindowEndUtc == xTime.WindowEndUtc,
            "Both native-unit time traces show exactly the matched measurements on aligned time axes.");
        typeof(AnalyticsTrendChart).GetMethod("OnMouseWheel", Instance)!.Invoke(yTime,
            new object[] { new MouseEventArgs(MouseButtons.None, 0, 200, 80, 120) });
        Check(yTime.ViewStartUtc == xTime.ViewStartUtc && yTime.ViewEndUtc == xTime.ViewEndUtc &&
            yTime.ViewEndUtc - yTime.ViewStartUtc < result.Request.ToUtc - result.Request.FromUtc,
            "Zooming either companion time trace synchronizes the other without shifting the data in time.");
        yTime.ResetZoom();
        Check(yTime.ViewStartUtc == xTime.ViewStartUtc && yTime.ViewEndUtc == xTime.ViewEndUtc &&
            yTime.ViewEndUtc == result.Request.ToUtc, "Companion time zoom resets together.");
        Check(Field<Label>("_axisInfo").Text.Contains("3 matched") && !Field<AnalyticsTrendChart>("_alarmChart").Visible,
            "UI explicitly states paired count and comparison limitations.");
        Check(result.Rows.Count == rows.Count && result.Statistics[0].ValidCount > 3,
            "Comparison filtering does not change saved rows, statistics or export inputs.");
        foreach (var size in new[] { new Size(980, 600), new Size(1160, 800), new Size(1560, 900) })
        {
            form.ClientSize = size;
            Application.DoEvents();
            Rectangle Screen(Control control) => control.RectangleToScreen(control.ClientRectangle);
            Check(Screen(x.Parent!).Contains(Screen(x)) && Screen(y.Parent!).Contains(Screen(y)) &&
                !Screen(x).IntersectsWith(Screen(y)) && Screen(x).Bottom <= Screen(chart).Top &&
                Screen(Field<Button>("_swapAxes")).Bottom <= Screen(chart).Top &&
                chart.Height >= 360 && yTime.Height >= 280 && xTime.Height >= 280 &&
                !Field<DataGridView>("_statistics").Visible,
                $"Axis controls fit without clipping or overlapping the plot at {size}: X={Screen(x)}; Y={Screen(y)}; chart={Screen(chart)}; Y-time={yTime.Height}; X-time={xTime.Height}; statistics visible={Field<DataGridView>("_statistics").Visible}.");
            using var image = new Bitmap(view.Width, view.Height);
            view.DrawToBitmap(image, view.ClientRectangle);
            image.Save(Path.Combine(output, $"comparison-layout-{size.Width}.png"));
            var widthBefore = chart.Width;
            Field<CheckBox>("_hideSelection").Checked = true;
            Application.DoEvents();
            Check(chart.Width >= widthBefore + 240 && !x.Visible && Field<CheckBox>("_hideSelection").Visible &&
                ((TabPage)page).ClientSize.Height >= 300,
                "Focus mode reclaims filter, axis-control and channel space while keeping Show controls accessible.");
            using var focused = new Bitmap(view.Width, view.Height);
            view.DrawToBitmap(focused, view.ClientRectangle);
            focused.Save(Path.Combine(output, $"comparison-focused-{size.Width}.png"));
            Field<CheckBox>("_hideSelection").Checked = false;
            Application.DoEvents();
            Check(x.Visible && y.Visible && ReferenceEquals(x.SelectedItem, temperature),
                "Restoring controls keeps the selected comparison channels and data unchanged.");
        }
        var tabs = (TabControl)page.Parent!;
        tabs.SelectedIndex = 2;
        Application.DoEvents();
        Check(Field<DataGridView>("_statistics").Visible && Field<DataGridView>("_statistics").Height >= 350 &&
            !chart.Visible, "History statistics have their own full-height page and cannot cover the graphs.");
        tabs.SelectedTab = (TabPage)page;
        mode.SelectedIndex = 2;
        Check(chart.ComparisonStyle == ChannelComparisonStyle.RawPoints && chart.Series[0].Points.Count == 3,
            "Raw measured pairs remain available without a summary overlay.");
        chart.Size = new Size(1100, 420);
        using (var bitmap = new Bitmap(chart.Width, chart.Height))
        {
            chart.DrawToBitmap(bitmap, chart.ClientRectangle);
            bitmap.Save(Path.Combine(output, "power-vs-temperature.png"));
        }
        var targets = (IList)typeof(AnalyticsTrendChart).GetField("_hitTargets", Instance)!.GetValue(chart)!;
        PointF Location(int index) => (PointF)targets[index]!.GetType().GetProperty("Location")!.GetValue(targets[index])!;
        Check(targets.Count == 3 && Location(1).X < Location(0).X && Location(2).X > Location(0).X &&
            Location(1).Y < Location(0).Y,
            "Actual painted hit targets follow numeric X/Y values, not chronological order.");
        typeof(AnalyticsTrendChart).GetMethod("OnMouseWheel", Instance)!.Invoke(chart,
            new object[] { new MouseEventArgs(MouseButtons.None, 0, 500, 200, 120) });
        Check((bool)typeof(AnalyticsTrendChart).GetField("_numericZoomed", Instance)!.GetValue(chart)!,
            "Numeric X supports wheel zoom.");
        var initialMin = (double)typeof(AnalyticsTrendChart).GetField("_minimumX", Instance)!.GetValue(chart)!;
        typeof(AnalyticsTrendChart).GetMethod("OnMouseDown", Instance)!.Invoke(chart,
            new object[] { new MouseEventArgs(MouseButtons.Left, 1, 500, 200, 0) });
        typeof(AnalyticsTrendChart).GetMethod("OnMouseMove", Instance)!.Invoke(chart,
            new object[] { new MouseEventArgs(MouseButtons.Left, 0, 520, 200, 0) });
        typeof(AnalyticsTrendChart).GetMethod("OnMouseUp", Instance)!.Invoke(chart,
            new object[] { new MouseEventArgs(MouseButtons.Left, 0, 520, 200, 0) });
        Check((double)typeof(AnalyticsTrendChart).GetField("_minimumX", Instance)!.GetValue(chart)! < initialMin,
            "Numeric X supports drag panning without altering samples.");
        typeof(AnalyticsTrendChart).GetMethod("OnMouseDoubleClick", Instance)!.Invoke(chart,
            new object[] { new MouseEventArgs(MouseButtons.Left, 2, 500, 200, 0) });
        Check(!(bool)typeof(AnalyticsTrendChart).GetField("_numericZoomed", Instance)!.GetValue(chart)!,
            "Double-click resets numeric zoom.");
        Field<Button>("_swapAxes").PerformClick();
        Check(ReferenceEquals(x.SelectedItem, power) && ReferenceEquals(y.SelectedItem, temperature) &&
            chart.Series[0].Points[0].XValue == 100 && chart.Series[0].Points[0].Value == 40,
            "Swap reverses both channel selections and actual point coordinates.");
        mode.SelectedIndex = 1;
        using (var bitmap = new Bitmap(chart.Width, chart.Height))
        {
            chart.DrawToBitmap(bitmap, chart.ClientRectangle);
            bitmap.Save(Path.Combine(output, "chronological-path.png"));
        }
        Check(chart.ComparisonStyle == ChannelComparisonStyle.TimePath && chart.Series[0].Points.Count == 3,
            "Time-path mode connects chronological measurements without reordering by X.");
        Field<CheckBox>("_showComparisonTime").Checked = false;
        Check(!xTime.Visible && !yTime.Visible, "Companion time traces can be hidden without changing compared data.");
        Field<CheckBox>("_showComparisonTime").Checked = true;
        y.SelectedItem = power;
        Check(chart.Series[0].Points.Count == 12 && chart.Series[0].Points.All(p => p.XValue == p.Value),
            "The same channel on both axes is a valid identity comparison with no Cartesian duplication.");
        x.SelectedIndex = 0;
        Check(!chart.NumericXAxis && chart.Series.Count == 1 && chart.YAxisLabel == "kW",
            "Returning to Time restores the selected Y time-series and its native units.");
        y.SelectedIndex = 0;
        Check(chart.Series.Count == 2, "All checked measurements restores existing multi-channel time behavior.");
        result.Rows = Array.Empty<EquipmentHistoryRow>();
        x.SelectedItem = temperature;
        Check(chart.Series[0].Points.Count == 0 && Field<Label>("_axisInfo").Text.StartsWith("0 matched"),
            "No matched records is explicit, never a made-up value.");
        result.Rows = new[] { Row("power", 0, 100), Row("temperature", 0, 40) };
        Call("RenderTrend");
        using (var bitmap = new Bitmap(chart.Width, chart.Height))
        {
            chart.DrawToBitmap(bitmap, chart.ClientRectangle);
            Check(((IList)typeof(AnalyticsTrendChart).GetField("_hitTargets", Instance)!.GetValue(chart)!).Count == 1,
                "A single pair or constant X/Y still has a visible numeric range.");
        }
        result.Request.Metrics = new[] { power, temperature, peak };
        x.SelectedIndex = 0;
        Call("RenderTrend");
        Check(!x.Items.Contains(peak) && y.Items.Contains(peak), "Interval peaks remain available only for time-based Y studies.");
        y.SelectedItem = peak;
        x.SelectedItem = temperature;
        Check(chart.Series[0].Points.Count == 0 && Field<Label>("_axisInfo").Text.Contains("Interval peaks") &&
            !Field<Button>("_swapAxes").Enabled, "Derived interval peaks are never represented as simultaneous raw channel pairs.");
        Call("ClearResult");
        Check(!x.Enabled && !y.Enabled && x.SelectedIndex == 0 && !chart.NumericXAxis,
            "Query/equipment changes clear the comparison and restore Time.");
        result.Request.IsCombined = true;
        result.Request.IncludedEquipment = new[] { equipment };
        typeof(EquipmentHistoryView).GetField("_result", Instance)!.SetValue(view, result);
        Call("RenderTrend");
        Check(!x.Enabled && !y.Enabled && !chart.NumericXAxis && Field<Label>("_axisInfo").Text.Contains("one equipment"),
            "Combined equipment history cannot accidentally pair readings across different assets.");
        ValidateResponse(output, Check);
        Console.WriteLine($"History comparison assertions passed: {assertions}.");
    }

    private static void ValidateResponse(string output, Action<bool, string> check)
    {
        var epoch = DateTime.UtcNow.AddDays(-1);
        AnalyticsTrendPoint Point(double x, double y, int second) =>
            new() { TimestampUtc = epoch.AddSeconds(second), XValue = x, Value = y, IsValid = true };
        var points = new[] { Point(1, 2, 0), Point(1, 4, 1), Point(1, 6, 2),
            Point(2, 8, 3), Point(2, 10, 4), Point(2, 12, 5), Point(3, 20, 6) };
        var bins = ChannelComparisonBin.Build(points);
        check(bins.Length == 3 && bins[0].MeanX == 1 && bins[0].MeanY == 4 &&
            bins[0].MinimumY == 2 && bins[0].MaximumY == 6 && bins[0].Count == 3 &&
            bins[1].MeanY == 10 && bins[2].Count == 1,
            "Repeated quantized X levels produce exact sample means, observed ranges and counts, not fictitious intermediate temperatures.");
        var dense = Enumerable.Range(0, 120).Select(i => Point(i, i * 2, i)).ToArray();
        var wide = ChannelComparisonBin.Build(dense);
        check(wide.Length == 12 && wide.Sum(b => b.Count) == 120 &&
            wide.All(b => b.Count == 10) && wide[0].MeanY == 9 && wide[11].MaximumY == 238,
            "Many distinct X values use 12 equal-width bins and preserve every sample and extreme.");
        check(ChannelComparisonBin.Build(new[] { Point(1, double.NaN, 0), Point(double.PositiveInfinity, 2, 1) }).Length == 0,
            "Invalid/nonfinite data cannot influence response statistics.");
        check(ChannelComparisonBin.Build(new[] { Point(7, 3, 0), Point(7, 9, 1) }).Single().MeanY == 6,
            "Constant X gives a single observed response, not an invented slope.");
        using var chart = new AnalyticsTrendChart { Size = new Size(1100, 460),
            NumericXAxis = true, ComparisonStyle = ChannelComparisonStyle.MeanResponse,
            WindowStartUtc = epoch, WindowEndUtc = epoch.AddSeconds(200), ChartTitle = "Observed response - sample means and measured range",
            XAxisLabel = "Temperature (deg C)", YAxisLabel = "Power (kW)",
            Series = new[] { new AnalyticsTrendSeries { Name = "Power vs temperature", Color = Color.Blue,
                Points = points, ComparisonBins = bins, ExpectedIntervalSeconds = 1 } } };
        using (var bitmap = new Bitmap(chart.Width, chart.Height))
        {
            chart.DrawToBitmap(bitmap, chart.ClientRectangle);
            bitmap.Save(Path.Combine(output, "response-summary.png"));
            bool GreenNear(double x, double y)
            {
                var px = (int)Math.Round(72 + (x - 0.84) / 2.32 * 1010);
                var py = (int)Math.Round(408 - (y - 0.56) / 20.88 * 350);
                for (var dx = -4; dx <= 4; dx++)
                for (var dy = -4; dy <= 4; dy++)
                {
                    var c = bitmap.GetPixel(px + dx, py + dy);
                    if (c.G > c.R + 25 && c.G > c.B + 10) return true;
                }
                return false;
            }
            check(GreenNear(1.5, 7) && !GreenNear(2.5, 15),
                "The actual response line joins supported means and does not invent a trend toward the sparse single-sample level.");
        }
        var targets = (IList)typeof(AnalyticsTrendChart).GetField("_hitTargets", Instance)!.GetValue(chart)!;
        check(targets.Count == 10 && targets.Cast<object>().Count(t =>
            ((string)t.GetType().GetProperty("Description")!.GetValue(t)!).Contains("Summary, not an individual")) == 3,
            "Summary hover targets are explicitly labelled aggregates, separate from all real measurements.");
        var gapped = new[] { Point(0, 0, 0), Point(10, 10, 1) };
        gapped[1].ConnectFromPrevious = false;
        chart.ComparisonStyle = ChannelComparisonStyle.TimePath;
        chart.Series = new[] { new AnalyticsTrendSeries { Color = Color.Blue, Points = gapped, ExpectedIntervalSeconds = 1 } };
        using (var bitmap = new Bitmap(chart.Width, chart.Height))
        {
            chart.DrawToBitmap(bitmap, chart.ClientRectangle);
            var middle = bitmap.GetPixel(577, 233);
            check(Math.Abs(middle.R - middle.G) < 25 && middle.B >= middle.R,
                "A missing pair cannot create a coloured chronological path across the gap.");
        }
        gapped[1].ConnectFromPrevious = true;
        using (var bitmap = new Bitmap(chart.Width, chart.Height))
        {
            chart.DrawToBitmap(bitmap, chart.ClientRectangle);
            var colour = bitmap.GetPixel(577, 233);
            check(colour.R > colour.B + 40, "A valid chronological segment is visibly connected in time colour.");
        }
        gapped[1].TimestampUtc = epoch.AddSeconds(20);
        using (var bitmap = new Bitmap(chart.Width, chart.Height))
        {
            chart.DrawToBitmap(bitmap, chart.ClientRectangle);
            var colour = bitmap.GetPixel(577, 233);
            check(Math.Abs(colour.R - colour.G) < 25 && colour.B >= colour.R,
                "Long recording gaps also break chronological path segments.");
        }
        var metricX = new HistoryMetric { Id = "x" };
        var metricY = new HistoryMetric { Id = "y" };
        var result = new EquipmentHistoryResult { Request = new EquipmentHistoryRequest {
            Equipment = new HistoryEquipment { Id = "T1", IntervalSeconds = 1 }, Metrics = new[] { metricX, metricY },
            FromUtc = epoch, ToUtc = epoch.AddSeconds(10) },
            Rows = new[]
            {
                new EquipmentHistoryRow { MetricId = "x", TimestampUtc = epoch, Value = 1, Quality = "Good" },
                new EquipmentHistoryRow { MetricId = "y", TimestampUtc = epoch, Value = 2, Quality = "Good" },
                new EquipmentHistoryRow { MetricId = "x", TimestampUtc = epoch.AddSeconds(1), Value = 3, Quality = "Good" },
                new EquipmentHistoryRow { MetricId = "y", TimestampUtc = epoch.AddSeconds(1), Quality = "Timeout" },
                new EquipmentHistoryRow { MetricId = "x", TimestampUtc = epoch.AddSeconds(2), Value = 4, Quality = "Good" },
                new EquipmentHistoryRow { MetricId = "y", TimestampUtc = epoch.AddSeconds(2), Value = 5, Quality = "Good" }
            } };
        var pairs = (AnalyticsTrendPoint[])typeof(EquipmentHistoryView).GetMethod("PairChannels", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { result, metricX, metricY })!;
        check(pairs.Length == 2 && !pairs[1].ConnectFromPrevious,
            "Pairing preserves evidence of unavailable intermediate acquisitions so both path and time trends break there.");
    }
}
