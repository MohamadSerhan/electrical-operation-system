Imports System.Globalization
Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Text
Imports System.Xml

Partial Public NotInheritable Class HistorianReportExporter
    Public Shared Sub ExportEquipmentHistory(result As EquipmentHistoryResult, outputPath As String, format As String,
                                            Optional comparison As ChannelComparisonStudy = Nothing)
        If result Is Nothing OrElse result.Request Is Nothing OrElse
            (result.Rows.Count = 0 AndAlso (Not result.Request.RawOnly OrElse result.AlarmEvents.Count = 0)) Then
            Throw New InvalidOperationException("Load nonempty history before exporting.")
        End If
        EquipmentHistoryService.CheckLimit(result.Rows.Count + result.AlarmEvents.Count)
        Dim extension = If(format = "Excel", ".xlsx", If(format = "PDF", ".pdf", If(format = "CSV", ".csv", "")))
        If extension.Length = 0 OrElse Not String.Equals(Path.GetExtension(outputPath), extension, StringComparison.OrdinalIgnoreCase) Then
            Throw New ArgumentException("Choose a matching CSV, Excel or PDF filename.")
        End If
        If File.Exists(outputPath) Then Throw New IOException("The report already exists. Choose a new filename; existing reports are never overwritten.")
        Dim folder = Path.GetDirectoryName(Path.GetFullPath(outputPath))
        Directory.CreateDirectory(folder)
        Dim temporary = outputPath & "." & Guid.NewGuid().ToString("N") & ".partial"
        Try
            Select Case format
                Case "CSV" : WriteEquipmentCsv(result, temporary)
                Case "Excel" : WriteEquipmentWorkbook(result, temporary)
                Case "PDF" : WriteEquipmentPdf(result, temporary, comparison)
            End Select
            If Not File.Exists(temporary) OrElse New FileInfo(temporary).Length = 0 Then Throw New IOException("Report output is empty.")
            File.Move(temporary, outputPath)
        Finally
            If File.Exists(temporary) Then File.Delete(temporary)
        End Try
    End Sub

    Private Shared Function HistorySummary(result As EquipmentHistoryResult) As IEnumerable(Of String())
        Dim request = result.Request
        If request.IsCombined Then Return New List(Of String()) From {
            New String() {"REPORT", "EOEMS combined equipment raw history"},
            New String() {"EQUIPMENT", String.Join("; ", request.IncludedEquipment.Select(Function(asset) asset.Name & " [" & asset.Id & "]"))},
            New String() {"PERIOD UTC [from, to)", request.FromUtc.ToString("o"), request.ToUtc.ToString("o")},
            New String() {"GENERATED UTC", result.GeneratedUtc.ToString("o")},
            New String() {"SOURCE", "Saved raw measurements and selected observed alarm/fault events. Every row identifies its source equipment and measurement; values with different units are not combined."},
            New String() {"CHANNELS", String.Join("; ", request.Metrics.Select(Function(metric) If(metric.SourceMetricName.Length > 0, metric.SourceMetricName, metric.Name) &
                If(metric.Unit.Length = 0, "", " [" & metric.Unit & "]") &
                " - " & request.IncludedEquipment.Single(Function(asset) asset.Id = metric.EquipmentId).Name))},
            New String() {"VALUES", "One chronological row per saved observation/channel. Blank values are unavailable, never zero. No missing measurements are filled."},
            New String() {"ALARMS", "Named observed alarm/fault changes and reminders; unknown is never cleared. Entries are observations, not exact onset times or incident counts."},
            New String() {"RETENTION", "All original acquisitions remain saved; this report does not delete or alter history."}
        }
        If request.RawOnly Then Return New List(Of String()) From {
            New String() {"REPORT", "EOEMS equipment raw history"},
            New String() {"EQUIPMENT", request.Equipment.Name, request.Equipment.Id},
            New String() {"PERIOD UTC [from, to)", request.FromUtc.ToString("o"), request.ToUtc.ToString("o")},
            New String() {"GENERATED UTC", result.GeneratedUtc.ToString("o")},
            New String() {"SOURCE", "Saved raw measurements and selected observed alarm/fault events; no live values, legacy slots or 15-minute archive."},
            New String() {"CHANNELS", String.Join("; ", request.Metrics.Select(Function(metric) metric.ToString()))},
            New String() {"VALUES", "Ordinary channels: Value = last valid reading; Min/Max = observed extrema within the saved interval. Partial intervals cover returned readings only."},
            New String() {"INTERVAL PEAKS", "Peak channel Value = observed interval maximum; its Min/Max cells stay blank. Trend/statistics use the same interval maxima. No extra storage, waveform peaks or energy from peak channels."},
            New String() {"PRECISION", "Measurements display/export with 1 decimal; new raw numeric values are rounded to 2 decimals. Original diagnostic/alarm evidence and existing stored history are unchanged."},
            New String() {"ALARMS", "Named Alarm / Fault / Unclassified rows: immediate observed changes and active 5-minute reminders. Unknown is never cleared. No events does not prove normal. Not exact device onset times or incident counts."},
            New String() {"TIME", "Exact host-acquisition UTC in individual mode; saved interval UTC otherwise. No filled gaps or invented measurements."},
            New String() {"STATISTICS", "Ordinary channels use selected raw observations; interval peak channels use one maximum per observed interval. State channels have no arithmetic mean or energy; peak channels have no energy."},
            New String() {"RETENTION", "All original acquisitions remain saved; this report does not delete or alter history."}
        }
        Return New List(Of String()) From {
            New String() {"REPORT", "EOEMS equipment historian / saved observations"},
            New String() {"EQUIPMENT", request.Equipment.Id, request.Equipment.Name, request.Equipment.Category},
            New String() {"SOURCE", request.Equipment.SourceId, request.Equipment.Description},
            New String() {"PERIOD UTC [from, to)", request.FromUtc.ToString("o"), request.ToUtc.ToString("o")},
            New String() {"GENERATED UTC", result.GeneratedUtc.ToString("o")},
            New String() {"ROWS", result.Rows.Count.ToString(CultureInfo.InvariantCulture), "Channel observations, not incident or acquisition-cycle count."},
            New String() {"QUALITY", "Blank values are unavailable, never zero. Excluded/partial/legacy rows are preserved."},
            New String() {"STATISTICS", "Records: Value is last valid acquired reading; Min/Max are observed extrema in the saved interval. Legacy Min/Max are unavailable. Statistics use individual saved readings; peak is not waveform peak."},
            New String() {"ENERGY", "Estimated trapezoid kW integration on recorded slots only, with advancing source timestamps within twice the recording interval (saved per UPS row; current interval for transformers). No extrapolation. UPS energy requires recorded acquisition time and interval; aggregates require every member to advance with unchanged membership."},
            New String() {"TIME", "Records use one interval UTC. Raw acquisitions retain exact host-poll UTC; legacy UPS rows can have a distinct slot time. No timestamp precision is discarded."},
            New String() {"TOTALS", "Transformer totals and downstream UPS totals overlap electrically. Never add them together. ATS bank totals mean UPS output aggregation only."},
            New String() {"ALARMS", "Interval summaries combine observed evidence. Alarm events are a separate observed-event ledger, not proof of device onset time or incident count."},
            New String() {"GAPS", "Missing intervals are not backfilled. Partial interval extrema cover returned acquisitions only. Full-resolution history is not automatically expired."}
        }
    End Function

    Private Shared Function HistoryRecordValues(result As EquipmentHistoryResult, row As EquipmentHistoryRow) As String()
        Dim metric = result.Request.Metrics.Single(Function(item) item.Id = row.MetricId)
        Return {row.EquipmentId, row.TimestampUtc.ToString("o"),
            If(row.AcquiredUtc.HasValue AndAlso row.AcquiredUtc.Value <> row.TimestampUtc, row.AcquiredUtc.Value.ToString("o"), ""), metric.Id,
            If(metric.SourceMetricName.Length > 0, metric.SourceMetricName, metric.Name),
            If(row.Value.HasValue, row.Value.Value.ToString("0.###", CultureInfo.InvariantCulture), ""), metric.Unit,
            row.Quality, row.Members.ToString(CultureInfo.InvariantCulture), row.ExpectedMembers.ToString(CultureInfo.InvariantCulture),
            row.Revision.ToString(CultureInfo.InvariantCulture), metric.Basis, row.Evidence,
            If(row.RecordedIntervalSeconds.HasValue, row.RecordedIntervalSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture), ""),
            If(Not metric.IsPower, "Not an integrated power channel", If(row.EnergyEligible, "Eligible if adjacent evidence qualifies", "Not eligible")),
            If(row.SourceTimes Is Nothing, "", String.Join("; ", row.SourceTimes.OrderBy(Function(item) item.Key).
                Select(Function(item) item.Key & "=" & item.Value.ToString("o"))))}
    End Function

    Private Shared ReadOnly HistoryHeaders As String() = {"Equipment ID", "Observation UTC", "Acquired UTC (when different)", "Channel ID", "Channel",
        "Value", "Unit", "Quality", "Available members", "Expected members", "Mapping revision", "Basis", "Evidence",
        "Recorded interval seconds", "Energy evidence", "Member acquisition UTC"}

    Private Shared Function HistoryStatisticValues(item As EquipmentHistoryStatistics) As String()
        Return {If(item.EquipmentName.Length = 0, "", item.EquipmentName & " | ") &
            If(item.Metric.SourceMetricName.Length > 0, item.Metric.SourceMetricName, item.Metric.Name),
            item.Metric.Unit, item.ValidCount.ToString(CultureInfo.InvariantCulture),
            item.MissingCount.ToString(CultureInfo.InvariantCulture), EquipmentHistoryTable.FormatMeasurement(item.Minimum, item.Metric.IsState),
            EquipmentHistoryTable.FormatMeasurement(item.Maximum, item.Metric.IsState), EquipmentHistoryTable.FormatMeasurement(item.Mean),
            EquipmentHistoryTable.FormatMeasurement(item.EnergyKwh), item.IntegratedSeconds.ToString("0.###", CultureInfo.InvariantCulture), item.GapCount.ToString(CultureInfo.InvariantCulture)}
    End Function

    Private Shared Sub WriteEquipmentCsv(result As EquipmentHistoryResult, path As String)
        Using writer As New StreamWriter(path, False, New UTF8Encoding(True))
            Dim csv = Function(values As IEnumerable(Of String)) String.Join(",", values.Select(
                Function(value)
                    Dim safe = If(value, "")
                    Dim numeric As Double
                    If safe.Length > 0 AndAlso "=+-@".Contains(safe.TrimStart().FirstOrDefault()) AndAlso
                       Not Double.TryParse(safe, NumberStyles.Float, CultureInfo.InvariantCulture, numeric) Then safe = "'" & safe
                    Return """" & safe.Replace("""", """""") & """"
                End Function))
            Dim table = EquipmentHistoryTable.Create(result)
            writer.WriteLine(csv(table.Headers))
            For Each row In table.Rows
                writer.WriteLine(csv(row.Cells))
            Next
        End Using
    End Sub

    Private Shared Sub WriteEquipmentWorkbook(result As EquipmentHistoryResult, path As String)
        If result.Request.RawOnly Then
            WriteSelectedRawWorkbook(result, path)
            Return
        End If
        Using archive = ZipFile.Open(path, ZipArchiveMode.Create)
            Using writer = StartWorksheet(archive, "xl/worksheets/sheet1.xml")
                Dim index = 1
                For Each summary In HistorySummary(result)
                    WriteWorksheetRow(writer, index, summary)
                    index += 1
                Next
                CloseWorksheet(writer)
            End Using
            Dim table = EquipmentHistoryTable.Create(result)
            Using writer = StartHistoryWorksheet(archive, "xl/worksheets/sheet2.xml", table.Headers.Length)
                WriteWorksheetRow(writer, 1, table.Headers)
                Dim index = 2
                For Each row In table.Rows
                    If row.Cells.Any(Function(value) value.Length > 32767) Then Throw New InvalidDataException("A table field exceeds Excel's cell limit. Export CSV to preserve it.")
                    Dim numeric = If(result.Request.IsCombined,
                        Enumerable.Range(0, table.Headers.Length).Where(Function(column) table.Headers(column) = "Value").ToArray(),
                        Enumerable.Range(3, result.Request.Metrics.Count + 1).Concat({table.Headers.Length - 1}).ToArray())
                    WriteHistoryNumericRow(writer, index, row.Cells, numeric)
                    index += 1
                Next
                FinishHistoryWorksheet(writer, index - 1, table.Headers.Length)
            End Using
            Using writer = StartHistoryWorksheet(archive, "xl/worksheets/sheet3.xml", HistoryHeaders.Length)
                WriteWorksheetRow(writer, 1, HistoryHeaders)
                Dim index = 2
                For Each row In result.Rows
                    Dim values = HistoryRecordValues(result, row)
                    If values.Any(Function(value) value.Length > 32767) Then
                        Throw New InvalidDataException("A source evidence field exceeds Excel's cell limit. Export CSV to preserve the full evidence.")
                    End If
                    WriteHistoryNumericRow(writer, index, values, {5, 8, 9, 10, 13})
                    index += 1
                Next
                FinishHistoryWorksheet(writer, index - 1, HistoryHeaders.Length)
            End Using
            Using writer = StartHistoryWorksheet(archive, "xl/worksheets/sheet4.xml", 5)
                WriteWorksheetRow(writer, 1, "Observed UTC", "Acquired UTC (when different)", "State", "Quality", "Event / fault evidence")
                Dim index = 2
                For Each item In result.AlarmEvents
                    Dim values = {item.TimestampUtc.ToString("o"),
                        If(item.AcquiredUtc.HasValue AndAlso item.AcquiredUtc.Value <> item.TimestampUtc, item.AcquiredUtc.Value.ToString("o"), ""),
                        If(item.Value.HasValue, item.Value.Value.ToString("0.###", CultureInfo.InvariantCulture), ""), item.Quality, item.Evidence}
                    If values.Any(Function(value) value.Length > 32767) Then Throw New InvalidDataException("Alarm evidence exceeds Excel's cell limit. Narrow the period or export the alarm-history source to CSV.")
                    WriteHistoryNumericRow(writer, index, values, {2})
                    index += 1
                Next
                FinishHistoryWorksheet(writer, index - 1, 5)
            End Using
            Using writer = StartHistoryWorksheet(archive, "xl/worksheets/sheet5.xml", 10)
                WriteWorksheetRow(writer, 1, "Channel", "Unit", "Valid", "Missing/excluded", "Minimum", "Sample peak", "Sample mean", "kWh calculated", "Integrated seconds", "Gap breaks")
                Dim index = 2
                For Each item In result.Statistics
                    WriteHistoryNumericRow(writer, index, HistoryStatisticValues(item), Enumerable.Range(2, 8).ToArray())
                    index += 1
                Next
                FinishHistoryWorksheet(writer, index - 1, 10)
            End Using
            WriteWorkbookParts(archive, {"Records", "Raw acquisitions", "Alarm events"})
        End Using
        ValidateWorkbook(path)
    End Sub

    Private Shared Sub WriteSelectedRawWorkbook(result As EquipmentHistoryResult, path As String)
        Using archive = ZipFile.Open(path, ZipArchiveMode.Create)
            Using writer = StartWorksheet(archive, "xl/worksheets/sheet1.xml")
                Dim index = 1
                For Each summary In HistorySummary(result)
                    WriteWorksheetRow(writer, index, summary)
                    index += 1
                Next
                CloseWorksheet(writer)
            End Using
            Dim table = EquipmentHistoryTable.Create(result)
            Using writer = StartHistoryWorksheet(archive, "xl/worksheets/sheet2.xml", table.Headers.Length)
                WriteWorksheetRow(writer, 1, table.Headers)
                Dim index = 2
                For Each row In table.Rows
                    If row.Cells.Any(Function(value) value.Length > 32767) Then Throw New InvalidDataException("A table field exceeds Excel's cell limit. Export CSV to preserve it.")
                    Dim numericHeaders = If(result.Request.IsCombined,
                        New HashSet(Of String)({"Value"}, StringComparer.Ordinal),
                        result.Request.Metrics.Where(Function(metric) Not metric.IsAlarm).
                            Select(Function(metric) metric.Name & If(metric.Unit.Length = 0, "", " [" & metric.Unit & "]")).
                            Concat({"Interval ms", "Acquisitions"}).ToHashSet(StringComparer.Ordinal))
                    Dim numeric = Enumerable.Range(0, table.Headers.Length).Where(Function(column) numericHeaders.Contains(table.Headers(column)))
                    WriteHistoryNumericRow(writer, index, row.Cells, numeric.ToArray())
                    index += 1
                Next
                FinishHistoryWorksheet(writer, index - 1, table.Headers.Length)
            End Using
            Using writer = StartHistoryWorksheet(archive, "xl/worksheets/sheet3.xml", 10)
                WriteWorksheetRow(writer, 1, "Channel", "Unit", "Valid", "Missing/excluded", "Minimum", "Maximum", "Mean", "kWh calculated", "Integrated seconds", "Gap breaks")
                Dim index = 2
                For Each item In result.Statistics
                    WriteHistoryNumericRow(writer, index, HistoryStatisticValues(item), Enumerable.Range(2, 8).ToArray())
                    index += 1
                Next
                FinishHistoryWorksheet(writer, index - 1, 10)
            End Using
            WriteWorkbookParts(archive, {"Records"})
        End Using
        ValidateWorkbook(path)
    End Sub

    Private Shared Function StartHistoryWorksheet(archive As ZipArchive, entryName As String, columns As Integer) As XmlWriter
        Const ns As String = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
        Dim writer = XmlWriter.Create(archive.CreateEntry(entryName, CompressionLevel.Optimal).Open(),
            New XmlWriterSettings With {.Encoding = New UTF8Encoding(False), .CloseOutput = True})
        writer.WriteStartDocument()
        writer.WriteStartElement("worksheet", ns)
        writer.WriteStartElement("sheetViews", ns)
        writer.WriteStartElement("sheetView", ns)
        writer.WriteAttributeString("workbookViewId", "0")
        writer.WriteStartElement("pane", ns)
        writer.WriteAttributeString("ySplit", "1")
        writer.WriteAttributeString("topLeftCell", "A2")
        writer.WriteAttributeString("state", "frozen")
        writer.WriteEndElement()
        writer.WriteEndElement()
        writer.WriteEndElement()
        writer.WriteStartElement("cols", ns)
        writer.WriteStartElement("col", ns)
        writer.WriteAttributeString("min", "1")
        writer.WriteAttributeString("max", columns.ToString(CultureInfo.InvariantCulture))
        writer.WriteAttributeString("width", "26")
        writer.WriteAttributeString("customWidth", "1")
        writer.WriteEndElement()
        writer.WriteEndElement()
        writer.WriteStartElement("sheetData", ns)
        Return writer
    End Function

    Private Shared Sub FinishHistoryWorksheet(writer As XmlWriter, lastRow As Integer, columns As Integer)
        writer.WriteEndElement()
        writer.WriteStartElement("autoFilter", "http://schemas.openxmlformats.org/spreadsheetml/2006/main")
        writer.WriteAttributeString("ref", "A1:" & CellReference(columns, lastRow))
        writer.WriteEndElement()
        writer.WriteEndElement()
        writer.WriteEndDocument()
    End Sub

    Private Shared Sub WriteHistoryNumericRow(writer As XmlWriter, row As Integer, values As String(), numericColumns As Integer())
        Const ns As String = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
        writer.WriteStartElement("row", ns)
        writer.WriteAttributeString("r", row.ToString(CultureInfo.InvariantCulture))
        For index = 0 To values.Length - 1
            writer.WriteStartElement("c", ns)
            writer.WriteAttributeString("r", CellReference(index + 1, row))
            Dim number As Double
            If numericColumns.Contains(index) AndAlso Double.TryParse(values(index), NumberStyles.Float, CultureInfo.InvariantCulture, number) AndAlso Double.IsFinite(number) Then
                writer.WriteElementString("v", ns, values(index))
            Else
                writer.WriteAttributeString("t", "inlineStr")
                writer.WriteStartElement("is", ns)
                writer.WriteStartElement("t", ns)
                writer.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve")
                writer.WriteString(values(index))
                writer.WriteEndElement()
                writer.WriteEndElement()
            End If
            writer.WriteEndElement()
        Next
        writer.WriteEndElement()
    End Sub

    Private Shared Sub WriteEquipmentPdf(result As EquipmentHistoryResult, path As String, comparison As ChannelComparisonStudy)
        Dim analysis = EquipmentAnalysis.Build(result)
        If comparison IsNot Nothing Then analysis.Comparison = ChannelComparisonStudy.Create(result, comparison.X, comparison.Y,
            comparison.Style, comparison.IncludeTimeTraces)
        EquipmentReportRenderer.WritePdf(analysis, path)
        ValidatePdf(path)
    End Sub
End Class