using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class AnalyzerGraphValidation
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(string output)
    {
        Directory.CreateDirectory(output);
        using var preference = new AnalyzerPreferenceScope();
        var settingsField = typeof(UpsSettingsStore).GetField("_current", BindingFlags.Static | BindingFlags.NonPublic)!;
        var original = settingsField.GetValue(null);
        try
        {
            settingsField.SetValue(null, new UpsSettings
            {
                Devices = Enumerable.Range(1, 8).Select(index => new UpsDevice
                {
                    Id = index == 1 ? "UPS-TEST" : "UPS-STRESS-" + index,
                    Label = "Graph test " + index, Model = "Test model", RatedKva = 100
                }).ToList()
            });
            RunFixture(output);
            RunDenseGraphs(output);
            RunCommunicationGaps(output);
            RunUnavailableGraphs(output);
        }
        finally { settingsField.SetValue(null, original); }
    }

    private static void RunUnavailableGraphs(string output)
    {
        var snapshot = new UpsSnapshot
        {
            DeviceId = "UPS-TEST", Quality = UpsQuality.Good, TimestampUtc = DateTime.UtcNow,
            InputVoltageReference = "L-L",
            Phases = new Dictionary<string, double?[]>
            {
                ["InputVoltageLL"] = new double?[] { 400, 401, 399 },
                ["InputCurrent"] = new double?[] { 10, 11, 12 }
            }
        };
        using var view = new OnlineAnalyzerView(() => Array.Empty<MeterSnapshot>(),
            () => new Dictionary<string, UpsSnapshot> { [snapshot.DeviceId] = snapshot }, () => new RecordingSettings());
        var type = typeof(OnlineAnalyzerView);
        T Field<T>(string name) => (T)type.GetField(name, Instance)!.GetValue(view)!;
        void Call(string name, params object?[] args) => type.GetMethod(name, Instance)!.Invoke(view, args);
        Field<System.Windows.Forms.Timer>("_timer").Stop();
        Field<CheckBox>("_saveToFile").Checked = false;
        Field<CheckBox>("_eventEnable").Checked = false;
        Field<TextBox>("_folder").Text = output;
        using (var host = new Form { ClientSize = new Size(1500, 850) })
        {
            host.Controls.Add(view);
            view.Dock = DockStyle.Fill;
            host.Show();
            Application.DoEvents();
            Call("OnStartClicked", null, EventArgs.Empty);
            var status = Field<Label>("_status");
            if (!status.Visible || status.Height != 28 || !status.Text.Contains("Choose at least one"))
                throw new InvalidOperationException("Removing idle hints must not hide invalid-selection errors.");
            status.Text = string.Empty;
            if (status.Visible || status.Height != 0)
                throw new InvalidOperationException("Cleared status must release its reserved row.");
            host.Controls.Remove(view);
        }
        var equipment = Field<CheckedListBox>("_equipmentList");
        for (var index = 0; index < equipment.Items.Count; index++)
            equipment.SetItemChecked(index, (string)equipment.Items[index].GetType().GetProperty("Id")!.GetValue(equipment.Items[index])! == snapshot.DeviceId);
        var channels = Field<CheckedListBox>("_channelList");
        for (var index = 0; index < channels.Items.Count; index++)
            channels.SetItemChecked(index, new[] { "InputVoltage", "InputCurrent", "InputPower" }.Contains(
                (string)channels.Items[index].GetType().GetProperty("UpsKey")!.GetValue(channels.Items[index])!));
        Call("OnStartClicked", null, EventArgs.Empty);
        Call("RefreshDisplay", true);
        AnalyticsTrendChart[] Charts() => Field<IList>("_chartGroups").Cast<object>().Select(group =>
            (AnalyticsTrendChart)group.GetType().GetProperty("Item1")!.GetValue(group)!).ToArray();
        AnalyticsTrendChart Chart(string title) => Charts().Single(chart => chart.ChartTitle.Contains(title));
        var watts = Chart("input Input Power");
        if (!watts.EmptyDataMessage.Contains("Input kW cannot be inferred from V x I alone or output power factor") ||
            watts.Series.Any(series => series.Points.Any(point => point.Value.HasValue)) ||
            watts.EmptyDataMessage.Split("Input kW cannot").Length != 2)
            throw new InvalidOperationException("Missing A/B input watts must explain the kVA alternative without inventing measured watts.");
        if (!Chart("input voltage L-N").EmptyDataMessage.Contains("recorded card reference is L-L") ||
            Chart("input ESTIMATED (L-L equivalent)").EmptyDataMessage.Length != 0)
            throw new InvalidOperationException("A/B missing measured L-N and valid separate calculated voltage must not be confused.");
        watts.Dock = DockStyle.None;
        watts.Size = new Size(600, 360);
        using (var bitmap = new Bitmap(watts.Width, watts.Height))
        {
            watts.DrawToBitmap(bitmap, watts.ClientRectangle);
            bitmap.Save(Path.Combine(output, "unavailable-input-watts-explained.png"));
        }
        snapshot.TimestampUtc = snapshot.TimestampUtc.AddMilliseconds(100);
        snapshot.InputVoltageReference = "L-N";
        snapshot.Phases = new Dictionary<string, double?[]>
        {
            ["InputVoltageLN"] = new double?[] { 229, 230, 231 },
            ["InputCurrent"] = new double?[] { 0, 0, 0 }
        };
        foreach (var state in Field<IList>("_series"))
            ((List<AnalyticsTrendPoint>)state.GetType().GetField("Points")!.GetValue(state)!).Clear();
        type.GetField("_lastSampleUtc", Instance)!.SetValue(view, DateTime.MinValue);
        Call("Sample");
        Call("RefreshDisplay", true);
        var equivalent = Chart("input ESTIMATED (L-L equivalent)");
        if (!equivalent.EmptyDataMessage.Contains("use measured Input Voltage L1-N/L2-N/L3-N") ||
            !Chart("input voltage L-L").EmptyDataMessage.Contains("recorded card reference is L-N"))
            throw new InvalidOperationException("C/D unavailable voltage graphs must identify measured L-N as the available alternative.");
        var kva = Chart("input ESTIMATED (V x I)");
        if (kva.EmptyDataMessage.Length != 0 || !kva.Series.Single().Points.Any(point => point.IsValid && point.Value == 0))
            throw new InvalidOperationException("Real zero-current kVA must draw zero, not an unavailable message.");
        equivalent.Dock = DockStyle.None;
        equivalent.Size = new Size(600, 360);
        using (var bitmap = new Bitmap(equivalent.Width, equivalent.Height))
        {
            equivalent.DrawToBitmap(bitmap, equivalent.ClientRectangle);
            bitmap.Save(Path.Combine(output, "unavailable-equivalent-use-measured-ln.png"));
        }
        snapshot.TimestampUtc = snapshot.TimestampUtc.AddMilliseconds(100);
        snapshot.Quality = UpsQuality.Timeout;
        snapshot.QualityDetail = "Test SNMP timeout; cause unverified.";
        type.GetField("_lastSampleUtc", Instance)!.SetValue(view, DateTime.MinValue);
        Call("Sample");
        Call("RefreshDisplay", true);
        if (!Chart("input Input Power").EmptyDataMessage.Contains("Timeout") ||
            Chart("input Input Power").EmptyDataMessage.Contains("Input kW cannot be inferred"))
            throw new InvalidOperationException("Communication failure must replace stale unsupported-channel advice with actual acquisition quality.");
        using var ordinary = new AnalyticsTrendChart();
        if (ordinary.EmptyDataMessage.Length != 0)
            throw new InvalidOperationException("Shared history/PDF charts must retain their standard no-data message by default.");
        Console.WriteLine("PASS: missing A/B watts, C/D reference alternatives, zero kVA and changing communication reasons are explicit in actual graph controls.");
    }

    private static void RunCommunicationGaps(string output)
    {
        var epoch = DateTime.UtcNow.AddSeconds(-4);
        var points = new[]
        {
            new AnalyticsTrendPoint { TimestampUtc = epoch, Value = 230, IsValid = true },
            new AnalyticsTrendPoint { TimestampUtc = epoch.AddSeconds(.1), Value = 230, IsValid = true },
            new AnalyticsTrendPoint { TimestampUtc = epoch.AddSeconds(.2), IsCommunicationGap = true },
            new AnalyticsTrendPoint { TimestampUtc = epoch.AddSeconds(2), Value = 230, IsValid = true },
            new AnalyticsTrendPoint { TimestampUtc = epoch.AddSeconds(3), Value = 230, IsValid = true },
            new AnalyticsTrendPoint { TimestampUtc = epoch.AddSeconds(3.2) },
            new AnalyticsTrendPoint { TimestampUtc = epoch.AddSeconds(3.8), Value = 230, IsValid = true }
        };
        var series = new AnalyticsTrendSeries { Name = "Actual voltage", Color = Color.Fuchsia,
            Points = points, ExpectedIntervalSeconds = .1 };
        using var chart = new AnalyticsTrendChart { Size = new Size(1000, 360), ShowMarkers = true,
            WindowStartUtc = epoch, WindowEndUtc = epoch.AddSeconds(4), Series = new[] { series } };
        var plot = (RectangleF)typeof(AnalyticsTrendChart).GetMethod("PlotRect", Instance)!.Invoke(chart, null)!;
        int Ink(Bitmap bitmap, double from, double until)
        {
            var n = 0;
            for (var x = (int)(plot.Left + plot.Width * from / 4); x < plot.Left + plot.Width * until / 4; x++)
            for (var y = (int)plot.Top + 1; y < plot.Bottom - 1; y++)
            {
                var c = bitmap.GetPixel(x, y);
                if (c.R > 170 && c.B > 170 && c.G < 100) n++;
            }
            return n;
        }
        using var baseline = new Bitmap(chart.Width, chart.Height);
        chart.DrawToBitmap(baseline, chart.ClientRectangle);
        if (Ink(baseline, .4, 1.8) != 0) throw new InvalidOperationException("Ordinary history/PDF charts unexpectedly bridge missing data.");
        series.ConnectCommunicationGaps = true;
        using var connected = new Bitmap(chart.Width, chart.Height);
        chart.DrawToBitmap(connected, chart.ClientRectangle);
        if (Ink(connected, .4, 1.8) < 80 || Ink(connected, 3.3, 3.6) != 0)
            throw new InvalidOperationException("Communication-only gaps must connect, but unsupported/invalid channel evidence must remain broken.");
        if (points.Length != 7 || points[2].Value.HasValue || points[5].Value.HasValue)
            throw new InvalidOperationException("Visual bridging must not add or replace recorded measurements.");
        connected.Save(Path.Combine(output, "communication-gaps-dashed-not-measured.png"));
        chart.TraceRevealUtc = epoch.AddSeconds(4);
        using var revealed = new Bitmap(chart.Width, chart.Height);
        chart.DrawToBitmap(revealed, chart.ClientRectangle);
        if (Ink(revealed, .4, 1.8) < 80 || Ink(revealed, 3.3, 3.6) != 0)
            throw new InvalidOperationException("Animated traces must preserve dashed communication links and actual invalid-channel breaks.");
        Console.WriteLine("PASS: actual rendered pixels join only communication gaps; history/PDF defaults and missing evidence remain unchanged.");
    }

    private static void RunDenseGraphs(string output)
    {
        var readings = UpsSettingsStore.Current.Devices.ToDictionary(device => device.Id, device => new UpsSnapshot
        {
            DeviceId = device.Id, Quality = UpsQuality.Good, TimestampUtc = DateTime.UtcNow,
            InputVoltageReference = "L-N", OutputVoltageReference = "L-N",
            Values = new Dictionary<string, double> { ["BatteryVoltage"] = 480, ["BatteryCurrent"] = -10 },
            Phases = new Dictionary<string, double?[]>
            {
                ["InputVoltageLN"] = new double?[] { 229, 230, 231 }, ["InputVoltageLL"] = new double?[] { 399, 400, 401 },
                ["OutputVoltage"] = new double?[] { 228, 229, 230 }, ["OutputVoltageLL"] = new double?[] { 398, 399, 400 },
                ["InputCurrent"] = new double?[] { 10, 11, 12 }, ["OutputCurrent"] = new double?[] { 8, 9, 10 },
                ["OutputPower"] = new double?[] { 1000, 1100, 1200 }
            }
        });
        using var view = new OnlineAnalyzerView(() => Array.Empty<MeterSnapshot>(), () => readings, () => new RecordingSettings());
        var type = typeof(OnlineAnalyzerView);
        T Field<T>(string name) => (T)type.GetField(name, Instance)!.GetValue(view)!;
        void Call(string name, params object?[] args) => type.GetMethod(name, Instance)!.Invoke(view, args);
        Field<System.Windows.Forms.Timer>("_timer").Stop();
        Field<CheckBox>("_includeInputEstimates").Checked = false;
        if (Field<Label>("_status").Text.Length != 0 || Field<Label>("_status").Height != 0 ||
            Field<Label>("_graphInfo").Text.Length != 0 || Field<Label>("_graphInfo").Height != 0)
            throw new InvalidOperationException("Initial idle/instruction messages and their reserved vertical space must be removed.");
        Field<CheckBox>("_saveToFile").Checked = false;
        Field<CheckBox>("_eventEnable").Checked = false;
        Field<TextBox>("_folder").Text = output;
        var equipment = Field<CheckedListBox>("_equipmentList");
        for (var index = 0; index < equipment.Items.Count; index++)
            equipment.SetItemChecked(index, (bool)equipment.Items[index].GetType().GetProperty("IsUps")!.GetValue(equipment.Items[index])!);
        var channels = Field<CheckedListBox>("_channelList");
        for (var index = 0; index < channels.Items.Count; index++)
        {
            var key = (string)channels.Items[index].GetType().GetProperty("UpsKey")!.GetValue(channels.Items[index])!;
            channels.SetItemChecked(index, new[] { "InputVoltage", "InputCurrent", "OutputVoltage", "OutputCurrent", "OutputPower", "@OutputKw", "BatteryVoltage", "BatteryCurrent" }.Contains(key));
        }
        Call("OnStartClicked", null, EventArgs.Empty);
        var states = Field<IList>("_series").Cast<object>().ToArray();
        if (states.Length != 192) throw new InvalidOperationException("Dense graph fixture did not select exactly 192 signals.");
        var epoch = DateTime.UtcNow.AddSeconds(-360);
        foreach (var state in states)
        {
            var points = (List<AnalyticsTrendPoint>)state.GetType().GetField("Points")!.GetValue(state)!;
            points.Clear();
            for (var index = 0; index < 3600; index++)
                points.Add(new AnalyticsTrendPoint { TimestampUtc = epoch.AddMilliseconds(index * 100), Value = 230 + Math.Sin(index / 10.0), IsValid = true });
        }
        type.GetField("_traceStartUtc", Instance)!.SetValue(view, epoch);
        Field<ComboBox>("_windowChoice").SelectedIndex = 8;
        var measurements = new List<double>();
        for (var iteration = 0; iteration < 6; iteration++)
        {
            var clock = Stopwatch.StartNew();
            Call("RefreshChart", true);
            clock.Stop();
            measurements.Add(clock.Elapsed.TotalMilliseconds);
        }
        var charts = Field<IList>("_chartGroups").Cast<object>()
            .Select(group => (AnalyticsTrendChart)group.GetType().GetProperty("Item1")!.GetValue(group)!).ToArray();
        foreach (var group in Field<IList>("_chartGroups").Cast<object>())
        {
            var members = ((IEnumerable)group.GetType().GetProperty("Item2")!.GetValue(group)!).Cast<object>();
            var owners = members.Select(state =>
            {
                var asset = state.GetType().GetProperty("Equipment")!.GetValue(state)!;
                return (string)asset.GetType().GetProperty("Id")!.GetValue(asset)!;
            });
            if (owners.Distinct().Count() != 1) throw new InvalidOperationException("Analyzer overlays different equipment without verified topology.");
        }
        if (charts.Sum(chart => chart.Series.Count) != 192 || charts.Any(chart => chart.Series.Any(series => series.Points.Count != 3600)))
            throw new InvalidOperationException("Dense grouped charts omitted a selected signal or loaded point.");
        var median = measurements.Order().ElementAt(measurements.Count / 2);
        if (median >= 150) throw new InvalidOperationException($"Dense 192-signal graph refresh median {median:0.0} ms exceeds 150 ms validation budget.");
        using (var host = new Form { ClientSize = new Size(1100, 700), StartPosition = FormStartPosition.Manual,
            Location = new Point(-2600, -1800) })
        {
            host.Controls.Add(view);
            view.Dock = DockStyle.Fill;
            host.Show();
            Application.DoEvents();
            var scroll = Field<Panel>("_chartScroll");
            var stack = Field<TableLayoutPanel>("_chartStack");
            foreach (var size in new[] { new Size(1100, 700), new Size(1900, 1000), new Size(860, 540) })
            {
                host.ClientSize = size;
                Application.DoEvents();
                scroll.AutoScrollPosition = new Point(0, int.MaxValue);
                Application.DoEvents();
                var bounds = scroll.RectangleToClient(charts[^1].RectangleToScreen(charts[^1].ClientRectangle));
                if (bounds.Bottom > scroll.ClientSize.Height - 20 || scroll.AutoScrollMinSize.Height != stack.Height ||
                    stack.Height < charts.Length * 366 + 32)
                    throw new InvalidOperationException($"Actual {charts.Length}-graph stack cannot reach its full final graph at {size}: last={bounds}, viewport={scroll.ClientRectangle}, stack={stack.Height}, min={scroll.AutoScrollMinSize}.");
            }
            host.Controls.Remove(view);
        }
        Call("StopRun", "dense graph verification");
        Console.WriteLine($"PASS: 192 signals x 3,600 measured points in {charts.Length} graphs; refresh median {median:0.0} ms.");
    }

    private static void RunFixture(string output)
    {
        var assertions = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
        }
        var epoch = DateTime.UtcNow.AddSeconds(-3);
        UpsSnapshot Reading(DateTime time) => new()
        {
            DeviceId = "UPS-TEST", Quality = UpsQuality.Good, TimestampUtc = time,
            InputVoltageReference = "L-N", OutputVoltageReference = "L-N",
            Values = new Dictionary<string, double> { ["BatteryVoltage"] = 480, ["BatteryCurrent"] = -10 },
            Phases = new Dictionary<string, double?[]>
            {
                ["InputVoltageLN"] = new double?[] { 229, 230, 231 },
                ["InputVoltageLL"] = new double?[] { 399, 400, 401 },
                ["OutputVoltage"] = new double?[] { 228, 229, 230 },
                ["OutputVoltageLL"] = new double?[] { 398, 399, 400 },
                ["InputCurrent"] = new double?[] { 10, 11, 12 },
                ["InputPower"] = new double?[] { 1300, 1400, 1500 },
                ["OutputCurrent"] = new double?[] { 8, 9, 10 },
                ["OutputPower"] = new double?[] { 1000, 1100, 1200 }
            }
        };
        var snapshot = Reading(epoch);
        using var view = new OnlineAnalyzerView(() => Array.Empty<MeterSnapshot>(),
            () => new Dictionary<string, UpsSnapshot> { ["UPS-TEST"] = snapshot }, () => new RecordingSettings());
        var type = typeof(OnlineAnalyzerView);
        T Field<T>(string name) => (T)type.GetField(name, Instance)!.GetValue(view)!;
        void Call(string name, params object?[] args) => type.GetMethod(name, Instance)!.Invoke(view, args);
        Field<System.Windows.Forms.Timer>("_timer").Stop();
        Field<CheckBox>("_includeInputEstimates").Checked = false;
        var equipment = Field<CheckedListBox>("_equipmentList");
        for (var index = 0; index < equipment.Items.Count; index++)
            equipment.SetItemChecked(index, (string)equipment.Items[index].GetType().GetProperty("Id")!.GetValue(equipment.Items[index])! == "UPS-TEST");
        var channels = Field<CheckedListBox>("_channelList");
        for (var index = 0; index < channels.Items.Count; index++)
        {
            var channel = channels.Items[index];
            var key = (string)channel.GetType().GetProperty("UpsKey")!.GetValue(channel)!;
            channels.SetItemChecked(index, new[] { "InputVoltage", "InputCurrent", "OutputVoltage", "OutputCurrent", "OutputPower", "@OutputKw", "BatteryVoltage", "BatteryCurrent" }.Contains(key));
        }
        Field<TextBox>("_folder").Text = output;
        Field<TextBox>("_fileName").Text = "all-selected-graphs";
        Call("OnStartClicked", null, EventArgs.Empty);
        Call("RefreshDisplay", true);
        var states = Field<IList>("_series");
        var groups = Field<IList>("_chartGroups");
        AnalyticsTrendChart Chart(object group) => (AnalyticsTrendChart)group.GetType().GetProperty("Item1")!.GetValue(group)!;
        object[] Members(object group) => ((IEnumerable)group.GetType().GetProperty("Item2")!.GetValue(group)!).Cast<object>().ToArray();
        Check(states.Count == 24, "All 24 selected AC input/output, LN/LL, power and battery signals enter the analyzer.");
        Check(Field<DataGridView>("_grid").Rows.Count == states.Count, "Every selected signal has its own quality/statistics table row.");
        Check((string)Field<ComboBox>("_unitChoice").SelectedItem! == "All selected channels", "The default is all signals, not the first native unit.");
        Check(Field<ComboBox>("_unitChoice").Parent!.Controls.Count == 3,
            "The filter group contains only the label and two choices, without a recording/report path banner.");
        Check(Field<ComboBox>("_unitChoice").Parent!.Parent == Field<Button>("_exportPdf").Parent!.Parent,
            "Graph and time-window filters share the top action row with Export, not the Graphs page.");
        Check(groups.Count == 10, "Native-unit and input/output/DC/reference families receive separate graphs.");
        var members = groups.Cast<object>().SelectMany(Members).ToArray();
        Check(members.Length == states.Count && members.Distinct().Count() == states.Count,
            "Every selected state occurs in exactly one graph without omission or duplication.");
        foreach (var group in groups.Cast<object>())
        {
            var chart = Chart(group);
            Check(chart.Series.Count == Members(group).Length && chart.Series.All(series => series.Points.Count == 1 && series.Points[0].IsValid),
                "Every selected signal is rendered from its actual acquired numeric point.");
            Check(chart.Height >= 280 && chart.Series.Count <= 6, "Graphs keep a readable height and bounded legend population.");
        }
        var graphObjects = groups.Cast<object>().Select(Chart).ToArray();
        var pointLists = graphObjects.Select(chart => chart.Series).ToArray();
        var clock = Stopwatch.StartNew();
        for (var frame = 0; frame < 100; frame++) Call("RefreshChart", false);
        clock.Stop();
        Check(graphObjects.SequenceEqual(Field<IList>("_chartGroups").Cast<object>().Select(Chart)) &&
              graphObjects.Select((chart, index) => ReferenceEquals(chart.Series, pointLists[index])).All(same => same),
            "Animation frames do not rebuild chart controls or recopy acquisition points.");
        using (var host = new Form { ClientSize = new Size(1100, 700), StartPosition = FormStartPosition.Manual, Location = new Point(25, 25) })
        {
            view.Dock = DockStyle.Fill;
            host.Controls.Add(view);
            host.Show();
            Application.DoEvents();
            var scroll = Field<Panel>("_chartScroll");
            Check(scroll.ClientSize.Height >= 220 && scroll.VerticalScroll.Visible,
                "At 1100x700 the graph viewport is usable and other graphs are explicitly scrollable.");
            Check(graphObjects.All(chart => chart.Width >= 600 && chart.Height >= 360),
                "Every graph retains its native-unit plot area at the minimum test window: " +
                string.Join(", ", graphObjects.Select(chart => $"{chart.Width}x{chart.Height}")));
            Check(new[] { "_start", "_pause", "_stop", "_clear", "_exportPdf" }.Select(name => Field<Button>(name))
                    .All(button => button.Parent!.ClientRectangle.Contains(button.Bounds)),
                "Start/Pause/Stop/Clear and Export PDF all fit inside the action bar.");
            using (var bitmap = new Bitmap(view.Width, view.Height))
            {
                view.DrawToBitmap(bitmap, view.ClientRectangle);
                bitmap.Save(Path.Combine(output, "all-graphs-top.png"));
            }
            scroll.ScrollControlIntoView(graphObjects[^1]);
            scroll.AutoScrollPosition = new Point(0, int.MaxValue);
            Application.DoEvents();
            var lastBounds = scroll.RectangleToClient(graphObjects[^1].RectangleToScreen(graphObjects[^1].ClientRectangle));
            Check(lastBounds.Bottom <= scroll.ClientRectangle.Bottom - 20 &&
                (lastBounds.Height + 20 > scroll.ClientSize.Height || scroll.ClientRectangle.Contains(lastBounds)),
                "The last graph's full lower axis and border are reachable with bottom clearance, not just partly intersecting the viewport.");
            using (var bitmap = new Bitmap(view.Width, view.Height))
            {
                view.DrawToBitmap(bitmap, view.ClientRectangle);
                bitmap.Save(Path.Combine(output, "all-graphs-last.png"));
            }
            var tabs = Field<TabControl>("_workspaceTabs");
            Check(tabs.TabPages.Cast<TabPage>().Select(page => page.Text).SequenceEqual(new[] { "Graphs", "Readings", "Events", "Sampling & start", "Event capture" }),
                "Sampling/start and event capture are direct workspaces without a nested Setup page.");
            foreach (var size in new[] { new Size(860, 540), new Size(1100, 700), new Size(1500, 850) })
            {
                host.ClientSize = size;
                scroll.AutoScrollPosition = Point.Empty;
                tabs.SelectedIndex = 0;
                Application.DoEvents();
                var toolbar = Field<ComboBox>("_unitChoice").Parent!.Parent!;
                if (size.Width >= 1500)
                    Check(Math.Abs(Field<ComboBox>("_unitChoice").PointToScreen(Point.Empty).Y -
                        Field<Button>("_exportPdf").PointToScreen(Point.Empty).Y) <= 8,
                        "On a wide window the graph filters sit immediately beside Export on the same row.");
                foreach (var control in new Control[] { Field<CheckBox>("_hideSelection"), Field<Button>("_exportPdf"),
                    Field<ComboBox>("_unitChoice"), Field<ComboBox>("_windowChoice") })
                {
                    var bounds = toolbar.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                    Check(toolbar.ClientRectangle.Contains(bounds),
                        $"Action/filter controls remain fully reachable without clipping at {size}: {control.Name} {bounds} in {toolbar.ClientRectangle}.");
                }
                foreach (var options in new[] { (Page: 3, Controls: new[] { "_interval", "_duration", "_triggerMode", "_triggerSeries", "_triggerLevel" }),
                    (Page: 4, Controls: new[] { "_eventEnable", "_eventMode", "_eventWatch", "_eventLevel", "_eventGap", "_eventSpan", "_eventShots" }) })
                {
                    tabs.SelectedIndex = options.Page;
                    Application.DoEvents();
                    foreach (var name in options.Controls)
                    {
                        var control = Field<Control>(name);
                        var page = tabs.TabPages[options.Page];
                        Check(control.Visible && page.Contains(control) &&
                            page.ClientRectangle.Contains(page.RectangleToClient(control.RectangleToScreen(control.ClientRectangle))),
                            $"Direct options page shows {name} fully at {size}.");
                    }
                    Check(!Field<ComboBox>("_interval").Enabled,
                        "Recording-state locking remains intact while navigating direct options pages.");
                    if (size.Width == 1500)
                    {
                        using var bitmap = new Bitmap(view.Width, view.Height);
                        view.DrawToBitmap(bitmap, view.ClientRectangle);
                        bitmap.Save(Path.Combine(output, $"direct-options-page-{options.Page}.png"));
                    }
                }
                tabs.SelectedIndex = 0;
                Field<CheckBox>("_eventShots").Checked = false;
                var eventGrid = Field<DataGridView>("_eventGrid");
                var previousEvents = eventGrid.Rows.Count;
                _ = eventGrid.Handle;
                eventGrid.Height = 0;
                Call("RegisterEvent", states[0], "Hidden event workspace", 10.0, 20.0, epoch);
                Check(eventGrid.Rows.Count == previousEvents + 1 && !eventGrid.Visible &&
                    Field<object>("_state").ToString() == "Recording",
                    "A live event is saved while the event grid is hidden/zero-height without stopping the recording.");
                tabs.SelectedIndex = 2;
                Application.DoEvents();
                Call("RegisterEvent", states[0], "Visible event workspace", 20.0, 30.0, epoch.AddMilliseconds(10));
                Check(eventGrid.Visible && eventGrid.Rows.Count == previousEvents + 2 && eventGrid.Height >= 280,
                    $"Previously hidden event rows remain available and new visible events scroll normally at {size}; grid height {eventGrid.Height}.");
                tabs.SelectedIndex = 0;
                Application.DoEvents();
                Check(scroll.ClientSize.Height >= 240 && !Field<DataGridView>("_grid").Visible,
                    "Graphs retain at least 240 pixels of viewport without tables stealing height at " + size);
                scroll.AutoScrollPosition = new Point(0, int.MaxValue);
                Application.DoEvents();
                var lastGraph = graphObjects[^1].RectangleToScreen(graphObjects[^1].ClientRectangle);
                var viewport = scroll.RectangleToScreen(scroll.ClientRectangle);
                Check(lastGraph.Bottom <= viewport.Bottom - 20,
                    $"The final graph's axis and lower border have at least 20 pixels of scroll clearance at {size}: graph={lastGraph}, viewport={viewport}, stack={Field<TableLayoutPanel>("_chartStack").Bounds}, scroll={scroll.AutoScrollPosition}.");
                if (lastGraph.Height + 20 <= viewport.Height)
                    Check(viewport.Contains(lastGraph), "The complete last graph fits inside the viewport at maximum scroll.");
                for (Control? parent = scroll.Parent; parent != null; parent = parent.Parent)
                    Check(parent.RectangleToScreen(parent.ClientRectangle).Contains(viewport),
                        "The graph viewport remains inside every ancestor rather than extending below the window.");
                var previousWidth = scroll.Width;
                Field<CheckBox>("_hideSelection").Checked = true;
                Application.DoEvents();
                Check(scroll.Width >= previousWidth + 340, "Expand workspace recovers the selector's width without changing any selected signals.");
                Check(Field<Button>("_stop").Visible && Field<Button>("_start").Visible,
                    "Recording controls remain available while equipment/channel selectors are hidden.");
                Field<CheckBox>("_hideSelection").Checked = false;
                tabs.SelectedIndex = 1;
                Application.DoEvents();
                var grid = Field<DataGridView>("_grid");
                Check(grid.Height >= 350 && grid.Columns[0].Width >= 300 &&
                    grid.Columns.Cast<DataGridViewColumn>().All(column => column.AutoSizeMode == DataGridViewAutoSizeColumnMode.None) &&
                    grid.Rows.Count == 24, "All readings retain full-height rows and readable horizontally scrollable columns at " + size);
                using var layout = new Bitmap(view.Width, view.Height);
                view.DrawToBitmap(layout, view.ClientRectangle);
                layout.Save(Path.Combine(output, $"readings-layout-{size.Width}.png"));
            }
            tabs.SelectedIndex = 0;
            using (var legend = new AnalyticsTrendChart { Width = 500,
                Series = Enumerable.Range(0, 24).Select(index => new AnalyticsTrendSeries { Name = "Selected channel " + index }).ToArray() })
            {
                legend.Height = legend.MinimumReadableHeight;
                var plot = (RectangleF)typeof(AnalyticsTrendChart).GetMethod("PlotRect", Instance)!.Invoke(legend, null)!;
                Check(plot.Height >= 248 && plot.Top >= 290 && plot.Right <= legend.Width,
                    "24 selected legend labels wrap into reserved rows without covering or shrinking the 248-pixel plot.");
            }
            host.Controls.Remove(view);
        }
        var filter = Field<ComboBox>("_unitChoice");
        filter.SelectedItem = "A";
        var filtered = Field<IList>("_chartGroups").Cast<object>().ToArray();
        Check(filtered.Sum(group => Members(group).Length) == 7 &&
              filtered.All(group => Chart(group).YAxisLabel == "A") &&
              Field<Label>("_graphInfo").Text.Contains("Filter active"),
            "Optional unit focus identifies its subset explicitly without changing selection or recording.");
        Check(states.Count == 24 && Field<DataGridView>("_grid").Rows.Count == 24, "Graph filters do not remove saved channels or table rows.");
        Check(Field<IList>("_chartGroups").Cast<object>().Select(Chart).All(chart =>
            chart.XAxisLabel.Contains("target 0.1 s") && chart.XAxisLabel.Contains("actual") &&
            chart.XAxisLabel.Contains("dashed = missed reads") &&
            chart.Series.All(series => series.ConnectCommunicationGaps && series.ExpectedIntervalSeconds == .1)),
            "Each live UPS graph declares requested versus observed seconds and opt-in communication-only display bridging.");
        filter.SelectedItem = "All selected channels";
        Check(Field<IList>("_chartGroups").Cast<object>().Sum(group => Members(group).Length) == 24,
            "All graphs return when the filter is cleared.");
        snapshot = Reading(epoch.AddSeconds(1));
        snapshot.Values = snapshot.Values.Where(item => item.Key != "BatteryCurrent").ToDictionary(item => item.Key, item => item.Value);
        type.GetField("_lastSampleUtc", Instance)!.SetValue(view, DateTime.MinValue);
        Call("Sample");
        Call("RefreshDisplay", true);
        Check(Field<DataGridView>("_grid").Rows.Cast<DataGridViewRow>().Any(row =>
            row.Cells[0].Value?.ToString()?.Contains("Battery Current") == true && row.Cells[8].Value?.ToString()?.StartsWith("Unavailable") == true),
            "Unavailable selected signals remain in the table with an explicit quality explanation.");
        snapshot = new UpsSnapshot { DeviceId = "UPS-TEST", Quality = UpsQuality.Timeout,
            QualityDetail = "Synthetic missed SNMP response", TimestampUtc = epoch.AddSeconds(1.1) };
        type.GetField("_lastSampleUtc", Instance)!.SetValue(view, DateTime.MinValue);
        Call("Sample");
        Check(states.Cast<object>().All(state =>
        {
            var points = (List<AnalyticsTrendPoint>)state.GetType().GetField("Points")!.GetValue(state)!;
            return points[^1].IsCommunicationGap && !points[^1].IsValid && points[^1].Value is null;
        }), "Timeouts stay explicit missing observations and are classified separately for display-only connection.");
        snapshot = Reading(epoch.AddSeconds(1.2));
        Array.Fill(snapshot.Phases["InputCurrent"], (double?)0);
        type.GetField("_lastSampleUtc", Instance)!.SetValue(view, DateTime.MinValue);
        Call("Sample");
        Call("RefreshDisplay", true);
        Check(Field<IList>("_chartGroups").Cast<object>().Select(Chart).All(chart =>
            chart.XAxisLabel.Contains("actual") && chart.XAxisLabel.Contains("max gap")),
            "Observed cadence and largest valid-acquisition interval are displayed separately from the requested rate.");
        Call("StopRun", "graph verification");
        var csv = File.ReadAllLines(Directory.GetFiles(output, "all-selected-graphs_*.csv").Single());
        Check(csv.Length == 5 && csv[0].Contains("Input Voltage") && csv[0].Contains("Input Current") &&
              csv[0].Contains("Output Voltage") && csv[0].Contains("Output Current") && csv[0].Contains("Output Power"),
            "The same selected inputs, outputs and power are saved once per new acquisition.");
        using (var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(Directory.GetFiles(output, "all-selected-graphs_*.csv").Single()))
        {
            parser.SetDelimiters(",");
            var header = parser.ReadFields()!;
            var rows = new List<string[]>();
            while (!parser.EndOfData) rows.Add(parser.ReadFields()!);
            Check(rows[2].Skip(2).Take(24).All(value => value == "") && rows[2].Any(value => value.Contains("Timeout")),
                "Display bridging never writes interpolated values or artificial zeros over the actual timeout CSV row.");
            var current = Array.FindIndex(header, value => value.Contains("Input Current L1"));
            Check(current >= 0 && rows[3][current] == "0",
                "A genuine zero after recovery is recorded as numeric zero and not hidden as a timing gap.");
        }
        var reportTask = Field<System.Threading.Tasks.Task>("_reportTask");
        var wait = Stopwatch.StartNew();
        while (!reportTask.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(30))
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Check(reportTask.IsCompletedSuccessfully, "The all-signal recording completes its asynchronous PDF.");
        var pdf = File.ReadAllText(Directory.GetFiles(output, "all-selected-graphs_*_report.pdf").Single(), Encoding.ASCII);
        foreach (var label in new[] { "Input Voltage L1-N", "Input Current L1", "Output Voltage L1-N", "Output Current L1", "Output Power L1", "Battery Current" })
            Check(pdf.Contains(label), "Every selected measurement family is present in the exported report.");
        Check(pdf.EndsWith("%%EOF") && !Directory.GetFiles(output, "*.partial").Any(), "All-signal report and CSV finalize cleanly.");
        var dense = Enumerable.Range(0, 12000).Select(index => new AnalyticsTrendPoint
        {
            TimestampUtc = epoch.AddMilliseconds(index * 25), Value = 230, IsValid = true
        }).ToList();
        dense[1].Value = 999;
        dense[7].Value = -50;
        dense[5].IsValid = false;
        var reduced = (AnalyticsTrendPoint[])type.GetMethod("Downsample", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { dense })!;
        Check(reduced.Contains(dense[1]) && reduced.Contains(dense[7]) &&
              reduced.Contains(dense[0]) && reduced.Contains(dense[^1]),
            "Dense visual reduction preserves observed extrema and boundary measurements, not a blind stride.");
        Check(reduced.Contains(dense[4]) && reduced.Contains(dense[5]) && reduced.Contains(dense[6]) &&
              reduced.SequenceEqual(reduced.OrderBy(point => point.TimestampUtc)),
            "Reduced graphs keep invalid-run boundaries and chronological order instead of bridging missing evidence.");
        for (var index = 100; index <= 300; index++)
        {
            dense[index].IsValid = false;
            dense[index].IsCommunicationGap = true;
        }
        dense[201].IsCommunicationGap = false;
        reduced = (AnalyticsTrendPoint[])type.GetMethod("Downsample", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { dense })!;
        Check(new[] { dense[200], dense[201], dense[202] }.All(reduced.Contains),
            "Visual reduction must not erase a genuinely invalid channel within a longer communication-only gap.");
        Field<ComboBox>("_quickSelect").SelectedItem = "UPS power balance evidence";
        var preset = channels.CheckedItems.Cast<object>().Select(channel =>
            (string)channel.GetType().GetProperty("UpsKey")!.GetValue(channel)!).ToArray();
        Check(new[] { "@OutputKw", "@OutputKva", "@OutputPf", "InputPower", "OutputPower", "InputVoltage",
            "OutputVoltage", "InputCurrent", "OutputCurrent", "BatteryCurrent" }.All(preset.Contains),
            "Power balance preset selects system totals/PF and input/output phases so unsupported phase watts do not hide a supported output total. Actual keys: " + string.Join(", ", preset));
        Check(preset.All(key => key.Length > 0) && Field<ComboBox>("_quickSelect").SelectedIndex == 0,
            "UPS power balance preset remains an explicit pre-recording channel choice and resets normally.");
        var formatTick = typeof(AnalyticsTrendChart).GetMethod("FormatAxisTick", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var step in new[] { 10.0, 1.0, .1, .01, .001, .0001, .00001 })
        {
            var ticks = Enumerable.Range(0, 12).Select(index => .875 + step * index).ToArray();
            var labels = ticks.Select(value => (string)formatTick.Invoke(null, new object[] { value, step })!).ToArray();
            Check(labels.Distinct().Count() == labels.Length,
                $"Adjacent Y ticks remain numerically distinct at step {step}, including narrow power-factor ranges.");
            Check(labels.Select((label, index) => Math.Abs(double.Parse(label, System.Globalization.CultureInfo.InvariantCulture) -
                ticks[index]) <= step / 2 + 1e-12).All(valid => valid), "Axis labels retain precision appropriate to the actual tick interval.");
        }
        Console.WriteLine($"PASS: {assertions} all-signal graph assertions; 100 retained-control frames {clock.Elapsed.TotalMilliseconds:0.0} ms.");
    }
}
