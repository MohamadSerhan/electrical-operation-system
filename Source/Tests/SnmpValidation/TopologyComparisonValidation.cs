using System.Drawing;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class TopologyComparisonValidation
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
        var epoch = DateTime.UtcNow.AddDays(-1);
        var ids = new[] { "T1", "UPS-A01", "UPS-C01", "GENERATOR-1" };
        var metrics = ids.Select(id => new HistoryMetric
        {
            Id = id + ":power", EquipmentId = id, Name = id + " | Active Power", SourceMetricId = "power",
            SourceId = id.StartsWith("UPS") ? "ups" : id == "T1" ? "transformer" : "generator", Unit = "kW"
        }).ToArray();
        foreach (var a in metrics)
        foreach (var b in metrics)
            Check(ElectricalReportKnowledge.SameEquipment(a, b, "ANALYZER") == (a.EquipmentId == b.EquipmentId),
                "Same units, different equipment/type/feed never authorize direct comparisons.");
        var device = new UpsDevice { Id = "UPS-A01", AtsBank = 1, Model = "Test UPS", RatedKva = 800, Verification = UpsVerification.Verified };
        var bank = new AtsBankDefinition { Number = 1, SourceDescription = "Transformer 1 / Generator bus" };
        var tagged = metrics[1].WithUpsMetadata(device, bank);
        Check(tagged.AtsBankNumber == 1 && tagged.ConfiguredSourceDescription == bank.SourceDescription &&
              tagged.TopologyBasis.Contains("active ATS source are not recorded"),
            "Identity verification and configured membership are not promoted into verified active feeder topology.");
        metrics[1] = tagged;
        var assets = metrics.Select(metric => new HistoryEquipment
        {
            Id = metric.EquipmentId, Name = metric.EquipmentId, SourceId = metric.SourceId,
            IntervalSeconds = 1, Metrics = new[] { metric }
        }).ToArray();
        var rows = metrics.SelectMany((metric, i) => Enumerable.Range(0, 3).Select(n => new EquipmentHistoryRow
        {
            EquipmentId = metric.EquipmentId, MetricId = metric.Id, TimestampUtc = epoch.AddSeconds(n),
            Value = 10 + i + n, Quality = "Good", AcquiredUtc = epoch.AddSeconds(n), ObservationId = n.ToString(), Revision = 1
        })).ToArray();
        var result = new EquipmentHistoryResult
        {
            Request = new EquipmentHistoryRequest
            {
                Equipment = new HistoryEquipment { Id = "__ALL_EQUIPMENT__", Name = "Independent device evidence", SourceId = "combined" },
                IsCombined = true, IncludedEquipment = assets, Metrics = metrics, FromUtc = epoch, ToUtc = epoch.AddSeconds(3)
            }, Rows = rows
        };
        using var view = new EquipmentHistoryView(new EquipmentHistoryService(Array.Empty<IEquipmentHistorySource>()));
        using var form = new Form { ClientSize = new Size(1160, 800), Location = new Point(-2600, -1800), StartPosition = FormStartPosition.Manual };
        form.Controls.Add(view); form.Show();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(EquipmentHistoryView);
        type.GetField("_result", flags)!.SetValue(view, result);
        var optionType = type.GetNestedType("HistoryChannelSelection", BindingFlags.NonPublic)!;
        var option = Activator.CreateInstance(optionType, true)!;
        foreach (var (property, value) in new (string, object)[] {
            ("EquipmentId", "UPS-A01"), ("SourceId", "ups"), ("Metric", new HistoryMetric { Id = "power", Name = "Active Power", Unit = "kW" }) })
            optionType.GetProperty(property)!.SetValue(option, value);
        var combo = (ComboBox)type.GetField("_trendMetric", flags)!.GetValue(view)!;
        combo.Items.Add(option); combo.SelectedIndex = 0;
        type.GetMethod("RenderCombinedTrend", flags)!.Invoke(view, null);
        var chart = (AnalyticsTrendChart)type.GetField("_chart", flags)!.GetValue(view)!;
        Check(chart.Series.Count == 1 && chart.Series[0].Name == "UPS-A01" && chart.Series[0].Points.Count == 3,
            "Combined history does not overlay the same measurement from different UPS feeds.");
        optionType.GetProperty("EquipmentId")!.SetValue(option, "UPS-C01");
        type.GetMethod("RenderCombinedTrend", flags)!.Invoke(view, null);
        Check(chart.Series.Count == 1 && chart.Series[0].Name == "UPS-C01" && result.Rows.Count == 12,
            "Each equipment can be selected independently without dropping combined saved records.");
        var layout = (TableLayoutPanel)type.GetField("_analysisLayout", flags)!.GetValue(view)!;
        var statistics = (DataGridView)type.GetField("_statistics", flags)!.GetValue(view)!;
        Check(layout.RowStyles[4].Height == 0 && !ReferenceEquals(statistics.Parent, layout),
            "Combined equipment history reserves no obsolete bottom-table space after statistics move to their own page.");
        var analysis = EquipmentAnalysis.Build(result);
        analysis.Findings.AddRange(ids.Select(id => new ReportFinding { Metric = id + " | Voltage", Severity = 1, PeakUtc = epoch, Title = "Test extreme" }));
        Check(analysis.CoincidentGroups.All(group => group.Select(finding => finding.Metric.Split('|')[0]).Distinct().Count() == 1),
            "Simultaneous extremes on unrelated equipment do not create a cross-equipment incident group.");
        var path = Path.Combine(output, "independent-equipment-topology-evidence.pdf");
        EquipmentReportRenderer.WritePdf(analysis, path);
        var pdf = File.ReadAllText(path, Encoding.ASCII);
        Check(pdf.Contains("Electrical relationship - comparison permissions") && pdf.Contains("Configured ATS 1") &&
              pdf.Contains("Transformer 1 / Generator bus") && pdf.Contains("Not calculable: active supplying source"),
            "PDF discloses assigned ATS, ambiguous source and blocked upstream percentage.");
        Check(!pdf.Contains("VS PEER AVG") && !pdf.Contains("peer spread") &&
              !pdf.Contains("Peer comparison - selected equipment") && pdf.Contains("Peer ranking, cross-equipment overlay"),
            "Transformer/UPS/generator and different-feed peer rankings are removed from PDF output.");
        Check(ElectricalReportKnowledge.Checklist(analysis).Any(row => row[0] == "Electrical relationship before comparison"),
            "Electrical reasoning requires mapped topology before relating equipment.");
        Console.WriteLine($"PASS: {assertions} electrical topology/comparison gating assertions.");
    }
}
