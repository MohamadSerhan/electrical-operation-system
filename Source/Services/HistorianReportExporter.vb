Imports System.Globalization
Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Xml

Public NotInheritable Class HistorianExportPreview
    Public Property AnalyzerId As String = String.Empty
    Public Property StartUtc As DateTime
    Public Property EndUtc As DateTime
    Public Property AvailableSamples As Long
    Public Property GoodSamples As Long
    Public Property BadSamples As Long
    Public Property EstimatedMeasurementRows As Long
    Public Property CoveragePercent As Double?
    Public Property SelectedParameters As IReadOnlyList(Of String) = Array.Empty(Of String)()
End Class

Partial Public NotInheritable Class HistorianReportExporter
    Private Const ExcelMaximumRows As Integer = 1048576
    Private Const MeasurementRowsPerSheet As Integer = ExcelMaximumRows - 1

    Private NotInheritable Class ValueStatistics
        Private ReadOnly _maximumGapSeconds As Double
        Public Property Count As Long
        Public Property Minimum As Double = Double.PositiveInfinity
        Public Property Maximum As Double = Double.NegativeInfinity
        Public Property Sum As Double
        Public Property MinimumAt As DateTime
        Public Property MaximumAt As DateTime
        Public Property IntegralHours As Double
        Public Property CoveredHours As Double
        Private _previousAt As DateTime?
        Private _previousValue As Double

        Public Sub New(maximumGapSeconds As Double)
            _maximumGapSeconds = maximumGapSeconds
        End Sub

        Public Sub Add(value As Double, timestampUtc As DateTime)
            Count += 1
            If value < Minimum Then
                Minimum = value
                MinimumAt = timestampUtc
            End If
            If value > Maximum Then
                Maximum = value
                MaximumAt = timestampUtc
            End If
            Sum += value
            ' Trapezoid integral; gaps longer than the allowed gap are not bridged so missing data is never invented.
            If _previousAt.HasValue Then
                Dim seconds = (timestampUtc - _previousAt.Value).TotalSeconds
                If seconds > 0 AndAlso seconds <= _maximumGapSeconds Then
                    IntegralHours += (value + _previousValue) / 2.0R * seconds / 3600.0R
                    CoveredHours += seconds / 3600.0R
                End If
            End If
            _previousAt = timestampUtc
            _previousValue = value
        End Sub
    End Class

    Private NotInheritable Class WindowStatisticResult
        Public Property Info As RecordingParameterInfo
        Public Property Value As Double
        Public Property AtUtc As DateTime?
        Public Property Basis As String = String.Empty
    End Class

    Private Sub New()
    End Sub

    Public Shared Function GetParameterCatalog(analyzerId As String) As IReadOnlyList(Of RecordingParameterInfo)
        Return MeasurementCatalog.GetCatalog(analyzerId)
    End Function

    ' Stored channels needed to produce a selection, including sources of peak/average/energy values.
    Public Shared Function GetSourceParameterIds(analyzerId As String, selected As IEnumerable(Of String)) As IReadOnlyList(Of String)
        Return MeasurementCatalog.GetSourceParameterIds(analyzerId, selected)
    End Function

    ' Energy integration bridges at most one missed recording interval (minimum 60 s).
    Private Shared Function MaximumIntegrationGapSeconds(settings As RecordingSettings) As Double
        Return Math.Max(60.0R, 2.0R * settings.HistorianRecordingIntervalMilliseconds / 1000.0R)
    End Function

    Private Shared Function SelectedStoredIds(analyzerId As String, selected As IEnumerable(Of String)) As HashSet(Of String)
        Return New HashSet(Of String)(selected.Where(
            Function(id)
                Dim info = MeasurementCatalog.Find(analyzerId, id)
                Return info IsNot Nothing AndAlso Not info.IsWindowStatistic
            End Function), StringComparer.Ordinal)
    End Function

    Private Shared Function CalculateWindowStatistics(analyzerId As String,
                                                      selected As IEnumerable(Of String),
                                                      stats As IReadOnlyDictionary(Of String, ValueStatistics)) As List(Of WindowStatisticResult)
        Dim results As New List(Of WindowStatisticResult)()
        For Each info In MeasurementCatalog.GetCatalog(analyzerId).Where(
                Function(parameter) parameter.IsWindowStatistic AndAlso selected.Contains(parameter.Id, StringComparer.Ordinal))
            Dim sources = MeasurementCatalog.GetStatisticSourceIds(analyzerId, info.Id).
                Where(Function(id) stats.ContainsKey(id) AndAlso stats(id).Count > 0).ToArray()
            If sources.Length = 0 Then Continue For
            Select Case info.Id
                Case "STAT_AVG_KW"
                    Dim power = stats(sources(0))
                    Dim timeWeighted = power.CoveredHours > 0
                    results.Add(New WindowStatisticResult With {
                        .Info = info,
                        .Value = If(timeWeighted, power.IntegralHours / power.CoveredHours, power.Sum / power.Count),
                        .Basis = If(timeWeighted,
                            "Time-weighted over " & power.CoveredHours.ToString("0.###", CultureInfo.InvariantCulture) & " h of recorded data",
                            "Mean of " & power.Count.ToString(CultureInfo.InvariantCulture) & " sample(s); no integrable interval")})
                Case "STAT_ENERGY_KWH"
                    Dim power = stats(sources(0))
                    results.Add(New WindowStatisticResult With {
                        .Info = info,
                        .Value = power.IntegralHours,
                        .Basis = "Trapezoidal integration of recorded kW over " &
                            power.CoveredHours.ToString("0.###", CultureInfo.InvariantCulture) & " h of recorded data"})
                Case Else
                    Dim peakSource = sources.OrderByDescending(Function(id) stats(id).Maximum).First()
                    Dim peak = stats(peakSource)
                    results.Add(New WindowStatisticResult With {
                        .Info = info,
                        .Value = peak.Maximum,
                        .AtUtc = peak.MaximumAt,
                        .Basis = "Highest saved sample, on " & ParameterLabel(analyzerId, peakSource)})
            End Select
        Next
        Return results
    End Function

    Public Shared Function Preview(historian As MeasurementHistorian,
                                   settings As RecordingSettings,
                                   analyzerId As String,
                                   startUtc As DateTime,
                                   endUtc As DateTime) As HistorianExportPreview
        ValidateRequest(historian, settings, analyzerId, startUtc, endUtc)
        Dim analyzer = settings.GetAnalyzer(analyzerId)
        Dim summary = historian.GetRangeSummary(analyzerId, startUtc, endUtc)
        Dim rows = If(analyzer.SelectedParameters.Count = 0, 0L,
            historian.CountRawMeasurements(analyzerId, GetSourceParameterIds(analyzerId, analyzer.SelectedParameters), startUtc, endUtc))
        Dim expected = GetExpectedSampleCount(startUtc, endUtc, settings.HistorianRecordingIntervalMilliseconds)
        Return New HistorianExportPreview With {
            .AnalyzerId = analyzerId,
            .StartUtc = startUtc,
            .EndUtc = endUtc,
            .AvailableSamples = summary.AvailableSampleCount,
            .GoodSamples = summary.GoodSampleCount,
            .BadSamples = summary.BadSampleCount,
            .EstimatedMeasurementRows = rows,
            .CoveragePercent = If(expected > 0, CType(Math.Min(100.0R, summary.GoodSampleCount * 100.0R / expected), Double?), Nothing),
            .SelectedParameters = analyzer.SelectedParameters.AsReadOnly()
        }
    End Function

    Public Shared Function ExportExcelAsync(historian As MeasurementHistorian,
                                           settings As RecordingSettings,
                                           analyzerId As String,
                                           startUtc As DateTime,
                                           endUtc As DateTime,
                                           outputDirectory As String,
                                           fileName As String,
                                           cancellationToken As CancellationToken) As Task(Of String)
        ValidateRequest(historian, settings, analyzerId, startUtc, endUtc)
        If String.IsNullOrWhiteSpace(outputDirectory) Then Throw New ArgumentException("Export output folder is required.", NameOf(outputDirectory))
        If String.IsNullOrWhiteSpace(fileName) OrElse Path.GetFileName(fileName) <> fileName OrElse
           Not fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) Then
            Throw New ArgumentException("Excel export must use a safe .xlsx file name.", NameOf(fileName))
        End If
        Return Task.Run(Function() WriteWorkbook(historian, settings, analyzerId, startUtc, endUtc,
                                                  outputDirectory, fileName, cancellationToken), cancellationToken)
    End Function

    Public Shared Function ExportPdfAsync(historian As MeasurementHistorian,
                                         settings As RecordingSettings,
                                         analyzerId As String,
                                         startUtc As DateTime,
                                         endUtc As DateTime,
                                         outputDirectory As String,
                                         fileName As String,
                                         cancellationToken As CancellationToken) As Task(Of String)
        ValidateRequest(historian, settings, analyzerId, startUtc, endUtc)
        If String.IsNullOrWhiteSpace(outputDirectory) Then Throw New ArgumentException("Export output folder is required.", NameOf(outputDirectory))
        If String.IsNullOrWhiteSpace(fileName) OrElse Path.GetFileName(fileName) <> fileName OrElse
           Not fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) Then
            Throw New ArgumentException("PDF export must use a safe .pdf file name.", NameOf(fileName))
        End If
        Return Task.Run(Function() WritePdf(historian, settings, analyzerId, startUtc, endUtc,
                                             outputDirectory, fileName, cancellationToken), cancellationToken)
    End Function

    Private Shared Function WriteWorkbook(historian As MeasurementHistorian,
                                          settings As RecordingSettings,
                                          analyzerId As String,
                                          startUtc As DateTime,
                                          endUtc As DateTime,
                                          outputDirectory As String,
                                          fileName As String,
                                          cancellationToken As CancellationToken) As String
        Directory.CreateDirectory(outputDirectory)
        Dim finalPath = Path.Combine(outputDirectory, fileName)
        If File.Exists(finalPath) Then Throw New IOException("Export filename already exists; refusing to overwrite: " & finalPath)
        Dim temporary = finalPath & "." & Guid.NewGuid().ToString("N") & ".partial"
        Try
            Using archive = ZipFile.Open(temporary, ZipArchiveMode.Create)
                WriteWorkbookSummary(archive, historian, settings, analyzerId, startUtc, endUtc)
                Dim stats As New Dictionary(Of String, ValueStatistics)(StringComparer.Ordinal)
                Dim maximumGapSeconds = MaximumIntegrationGapSeconds(settings)
                Dim sheetNames As New List(Of String)()
                Dim sheetNumber = 1
                Dim rowNumber = 0
                Dim selected = settings.GetAnalyzer(analyzerId).SelectedParameters
                Dim storedSelected = SelectedStoredIds(analyzerId, selected)
                Dim sources = GetSourceParameterIds(analyzerId, selected)
                ' Compact layout: one row per saved sample, one numeric column per selected parameter (no repeated timestamp, label, unit or constant columns).
                Dim columnIds = MeasurementCatalog.GetCatalog(analyzerId).Where(Function(item) storedSelected.Contains(item.Id)).Select(Function(item) item.Id).ToList()
                Dim columnOf = columnIds.Select(Function(id, position) (id, position)).ToDictionary(Function(pair) pair.id, Function(pair) pair.position, StringComparer.Ordinal)
                Dim headers = {"Timestamp UTC"}.Concat(columnIds.Select(Function(id) ParameterHeader(analyzerId, id))).ToArray()
                Dim sheetWriter = StartMeasurementSheet(archive, sheetNumber, sheetNames, headers)
                Dim pending(columnIds.Count - 1) As String
                Dim pendingStamp As DateTime? = Nothing
                Dim pendingCycle As Long = -1
                Dim FlushRow = Sub()
                                   If Not pendingStamp.HasValue Then Return
                                   If rowNumber >= MeasurementRowsPerSheet Then
                                       CloseWorksheet(sheetWriter)
                                       sheetNumber += 1
                                       rowNumber = 0
                                       sheetWriter = StartMeasurementSheet(archive, sheetNumber, sheetNames, headers)
                                   End If
                                   rowNumber += 1
                                   WriteMeasurementRow(sheetWriter, rowNumber + 1, pendingStamp.Value.ToString("yyyy-MM-dd HH:mm:ss.f", CultureInfo.InvariantCulture), pending)
                                   Array.Clear(pending, 0, pending.Length)
                                   pendingStamp = Nothing
                               End Sub
                If sources.Count > 0 Then
                    historian.StreamRawMeasurements(analyzerId, sources, startUtc, endUtc,
                        Sub(point)
                            cancellationToken.ThrowIfCancellationRequested()
                            If Not stats.ContainsKey(point.ParameterId) Then stats.Add(point.ParameterId, New ValueStatistics(maximumGapSeconds))
                            stats(point.ParameterId).Add(point.Value, point.TimestampUtc)
                            Dim column As Integer
                            If Not columnOf.TryGetValue(point.ParameterId, column) Then Return
                            If pendingStamp.HasValue AndAlso (pendingStamp.Value <> point.TimestampUtc OrElse pendingCycle <> point.AcquisitionCycleId) Then FlushRow()
                            pendingStamp = point.TimestampUtc
                            pendingCycle = point.AcquisitionCycleId
                            pending(column) = point.Value.ToString("0.###", CultureInfo.InvariantCulture)
                        End Sub, cancellationToken, includeAlarmEvidence:=False)
                    FlushRow()
                End If
                Dim windowStatistics = CalculateWindowStatistics(analyzerId, selected, stats)
                If rowNumber = 0 AndAlso windowStatistics.Count = 0 Then
                    Throw New InvalidOperationException(
                        "No good stored measurements match the selected report parameters in the requested time window.")
                End If
                CloseWorksheet(sheetWriter)
                WriteStatisticsSheet(archive, analyzerId, storedSelected, stats, windowStatistics, sheetNames.Count + 2)
                WriteWorkbookParts(archive, sheetNames)
            End Using
            ValidateWorkbook(temporary)
            If File.Exists(finalPath) Then Throw New IOException("Export filename collision occurred before promotion: " & finalPath)
            File.Move(temporary, finalPath)
            Return finalPath
        Finally
            If File.Exists(temporary) Then File.Delete(temporary)
        End Try
    End Function

    Private Shared Sub WriteWorkbookSummary(archive As ZipArchive,
                                            historian As MeasurementHistorian,
                                            settings As RecordingSettings,
                                            analyzerId As String,
                                            startUtc As DateTime,
                                            endUtc As DateTime)
        Dim equipment = settings.GetAnalyzer(analyzerId)
        Dim summary = historian.GetRangeSummary(analyzerId, startUtc, endUtc)
        Using writer = StartWorksheet(archive, "xl/worksheets/sheet1.xml")
            WriteWorksheetRow(writer, 1, "TWELLIUM INDUSTRIAL COMPANY", "ELECTRICAL DEPARTMENT - POWER ANALYSIS")
            WriteWorksheetRow(writer, 2, "ANALYZER", analyzerId & " - " & equipment.EquipmentName)
            WriteWorksheetRow(writer, 3, "SITE", If(String.IsNullOrWhiteSpace(equipment.SiteName), "Not configured", equipment.SiteName))
            Dim equipmentMetadata = If(String.Equals(analyzerId, "TOTAL", StringComparison.Ordinal),
                String.Join(" | ", {
                    "Factory: " & OptionalText(equipment.EquipmentName),
                    "Site / installation location: " & OptionalText(equipment.SiteName)
                }),
                String.Join(" | ", {
                    "Transformer manufacturer: " & OptionalText(equipment.Manufacturer),
                    "Transformer model: " & OptionalText(equipment.Model),
                    "Transformer serial: " & OptionalText(equipment.SerialNumber),
                    "Rated capacity (kVA): " & OptionalNumber(equipment.RatedKva),
                    "Primary rated voltage: " & OptionalNumber(equipment.PrimaryRatedVoltage),
                    "Secondary rated voltage: " & OptionalNumber(equipment.SecondaryRatedVoltage),
                    "Measurement side: " & OptionalText(equipment.ElectricalSide),
                    "Meter manufacturer: " & OptionalText(equipment.MeterManufacturer),
                    "Meter model: " & OptionalText(equipment.MeterModel),
                    "Meter serial / identity: " & OptionalText(equipment.MeterIdentity),
                    "Site / installation location: " & OptionalText(equipment.SiteName),
                    "Verified temperature sensor source: " & OptionalText(equipment.TemperatureSensorSource)
                }))
            WriteWorksheetRow(writer, 4, "EQUIPMENT", equipmentMetadata)
            WriteWorksheetRow(writer, 5, "WINDOW UTC [start, end)", startUtc.ToString("o", CultureInfo.InvariantCulture) & " / " & endUtc.ToString("o", CultureInfo.InvariantCulture))
            WriteWorksheetRow(writer, 6, "WINDOW FACTORY TIME", FormatFactoryWindow(settings, startUtc, endUtc))
            WriteWorksheetRow(writer, 7, "GENERATED UTC", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
            WriteWorksheetRow(writer, 8, "RECORDED QUALITY", "Observed samples " & summary.AvailableSampleCount.ToString(CultureInfo.InvariantCulture) &
                              "; good " & summary.GoodSampleCount.ToString(CultureInfo.InvariantCulture) &
                              "; bad " & summary.BadSampleCount.ToString(CultureInfo.InvariantCulture))
            WriteWorksheetRow(writer, 9, "SOURCE FRESHNESS", "Unknown - PLC exposes no source timestamp/sequence; host acquisition time is not physical meter freshness.")
            WriteWorksheetRow(writer, 10, "TOTAL SEMANTICS", "Calculated only from four good same-cycle T1-T4 readings aligned within 250 ms: total current and kW are sums, voltages are averages, total PF = total kW / total kVA. Temperature and frequency are not combined.")
            WriteWorksheetRow(writer, 11, "SELECTED PARAMETERS", String.Join(", ", equipment.SelectedParameters.Select(Function(id) ParameterLabel(analyzerId, id))))
            WriteWorksheetRow(writer, 12, "PARAMETER LIMITS", "Mapped float values are exported as decoded; engineering scaling not confirmed is marked unverified. PF and kVA are calculated from kW and phase V x I (no meter PF/kvar register is mapped). Peak, average power and kWh are calculated from saved samples on the Statistics sheet.")
            WriteWorksheetRow(writer, 13, "HISTORY RESOLUTION", "Raw decoded acquisition samples (one row per saved sample, one column per parameter); no empty intervals are zero-filled.")
            WriteWorksheetRow(writer, 14, "SOURCE REFRESH", "Not measured against physical PM5110 conversions. Display refresh is not acquisition rate.")
            WriteWorksheetRow(writer, 15, "DATA QUALITY", If(summary.GoodSampleCount = 0, "No good samples in the selected window.", "Only good saved samples are listed; an empty cell means no saved value (gaps are never zero-filled)."))
            WriteWorksheetRow(writer, 16, "PEAK INTERPRETATION", "Maximum / peak is the highest saved historian sample in the selected window, not an instantaneous waveform peak or a PM5110 peak register.")
            CloseWorksheet(writer)
        End Using
    End Sub

    Private Shared Function StartMeasurementSheet(archive As ZipArchive,
                                                  sheetNumber As Integer,
                                                  names As List(Of String),
                                                  headers As String()) As XmlWriter
        Dim name = If(sheetNumber = 1, "Measurements", "Measurements_" & sheetNumber.ToString(CultureInfo.InvariantCulture))
        names.Add(name)
        Dim writer = StartWorksheet(archive, "xl/worksheets/sheet" & (sheetNumber + 1).ToString(CultureInfo.InvariantCulture) & ".xml")
        WriteWorksheetRow(writer, 1, headers)
        Return writer
    End Function

    Private Shared Function ParameterHeader(analyzerId As String, parameterId As String) As String
        Dim item = MeasurementCatalog.Find(analyzerId, parameterId)
        If item Is Nothing Then Return parameterId
        Return If(String.IsNullOrWhiteSpace(item.Unit), item.Label, item.Label & " (" & item.Unit & ")")
    End Function

    ' Values are real numeric cells so Excel can sum, chart and filter them; a missing value is an empty cell, never zero.
    Private Shared Sub WriteMeasurementRow(writer As XmlWriter, rowNumber As Integer, timestamp As String, values As String())
        Const ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
        writer.WriteStartElement("row", ns)
        writer.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture))
        writer.WriteStartElement("c", ns)
        writer.WriteAttributeString("r", CellReference(1, rowNumber))
        writer.WriteAttributeString("t", "inlineStr")
        writer.WriteStartElement("is", ns)
        writer.WriteElementString("t", ns, timestamp)
        writer.WriteEndElement()
        writer.WriteEndElement()
        For index = 0 To values.Length - 1
            If values(index) Is Nothing Then Continue For
            writer.WriteStartElement("c", ns)
            writer.WriteAttributeString("r", CellReference(index + 2, rowNumber))
            writer.WriteElementString("v", ns, values(index))
            writer.WriteEndElement()
        Next
        writer.WriteEndElement()
    End Sub

    Private Shared Sub WriteStatisticsSheet(archive As ZipArchive,
                                            analyzerId As String,
                                            storedSelected As ISet(Of String),
                                            stats As IReadOnlyDictionary(Of String, ValueStatistics),
                                            windowStatistics As IReadOnlyList(Of WindowStatisticResult),
                                            sheetNumber As Integer)
        Using writer = StartWorksheet(archive, "xl/worksheets/sheet" & sheetNumber.ToString(CultureInfo.InvariantCulture) & ".xml")
            WriteWorksheetRow(writer, 1, "PARAMETER", "UNIT", "COUNT", "MIN", "MIN AT (UTC)", "MAX RECORDED (SAMPLE PEAK)", "MAX AT (UTC)", "AVERAGE", "ENGINEERING VALIDATION")
            Dim row = 2
            For Each parameter In MeasurementCatalog.GetCatalog(analyzerId).Where(Function(item) Not item.IsWindowStatistic)
                Dim item As ValueStatistics = Nothing
                If storedSelected.Contains(parameter.Id) AndAlso stats.TryGetValue(parameter.Id, item) Then
                    WriteWorksheetRow(writer, row, parameter.Label, parameter.Unit,
                        item.Count.ToString(CultureInfo.InvariantCulture),
                        item.Minimum.ToString("0.###", CultureInfo.InvariantCulture),
                        item.MinimumAt.ToString("yyyy-MM-dd HH:mm:ss.f", CultureInfo.InvariantCulture),
                        item.Maximum.ToString("0.###", CultureInfo.InvariantCulture),
                        item.MaximumAt.ToString("yyyy-MM-dd HH:mm:ss.f", CultureInfo.InvariantCulture),
                        (item.Sum / item.Count).ToString("0.###", CultureInfo.InvariantCulture),
                        parameter.Verification)
                    row += 1
                End If
            Next
            If windowStatistics.Count > 0 Then
                row += 1
                WriteWorksheetRow(writer, row, "PEAK, AVERAGE & ENERGY (REPORT WINDOW)", "UNIT", "VALUE", "AT (UTC)", "BASIS", "ENGINEERING VALIDATION")
                row += 1
                For Each result In windowStatistics
                    WriteWorksheetRow(writer, row, result.Info.Label, result.Info.Unit,
                        result.Value.ToString("0.###", CultureInfo.InvariantCulture),
                        If(result.AtUtc.HasValue, result.AtUtc.Value.ToString("yyyy-MM-dd HH:mm:ss.f", CultureInfo.InvariantCulture), String.Empty),
                        result.Basis,
                        result.Info.Verification)
                    row += 1
                Next
            End If
            CloseWorksheet(writer)
        End Using
    End Sub

    Private Shared Function StartWorksheet(archive As ZipArchive, entryName As String) As XmlWriter
        Dim entry = archive.CreateEntry(entryName, CompressionLevel.Optimal)
        Dim output = entry.Open()
        Dim writer = XmlWriter.Create(output, New XmlWriterSettings With {
            .Encoding = New UTF8Encoding(False),
            .CloseOutput = True,
            .Indent = False
        })
        writer.WriteStartDocument()
        writer.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main")
        writer.WriteStartElement("sheetData", "http://schemas.openxmlformats.org/spreadsheetml/2006/main")
        Return writer
    End Function

    Private Shared Sub WriteWorksheetRow(writer As XmlWriter, rowNumber As Integer, ParamArray values As String())
        writer.WriteStartElement("row", "http://schemas.openxmlformats.org/spreadsheetml/2006/main")
        writer.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture))
        For index = 0 To values.Length - 1
            writer.WriteStartElement("c", "http://schemas.openxmlformats.org/spreadsheetml/2006/main")
            writer.WriteAttributeString("r", CellReference(index + 1, rowNumber))
            writer.WriteAttributeString("t", "inlineStr")
            writer.WriteStartElement("is", "http://schemas.openxmlformats.org/spreadsheetml/2006/main")
            writer.WriteStartElement("t", "http://schemas.openxmlformats.org/spreadsheetml/2006/main")
            writer.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve")
            writer.WriteString(If(values(index), String.Empty))
            writer.WriteEndElement()
            writer.WriteEndElement()
            writer.WriteEndElement()
        Next
        writer.WriteEndElement()
    End Sub

    Private Shared Sub CloseWorksheet(writer As XmlWriter)
        writer.WriteEndElement()
        writer.WriteEndElement()
        writer.WriteEndDocument()
        writer.Dispose()
    End Sub

    Private Shared Sub WriteWorkbookParts(archive As ZipArchive, measurementSheetNames As IReadOnlyList(Of String))
        Dim allNames As New List(Of String) From {"Summary"}
        allNames.AddRange(measurementSheetNames)
        allNames.Add("Statistics")
        Using writer = StartXmlEntry(archive, "[Content_Types].xml")
            writer.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types")
            writer.WriteStartElement("Default", "http://schemas.openxmlformats.org/package/2006/content-types")
            writer.WriteAttributeString("Extension", "rels")
            writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-package.relationships+xml")
            writer.WriteEndElement()
            writer.WriteStartElement("Default", "http://schemas.openxmlformats.org/package/2006/content-types")
            writer.WriteAttributeString("Extension", "xml")
            writer.WriteAttributeString("ContentType", "application/xml")
            writer.WriteEndElement()
            writer.WriteStartElement("Override", "http://schemas.openxmlformats.org/package/2006/content-types")
            writer.WriteAttributeString("PartName", "/xl/workbook.xml")
            writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")
            writer.WriteEndElement()
            For index = 1 To allNames.Count
                writer.WriteStartElement("Override", "http://schemas.openxmlformats.org/package/2006/content-types")
                writer.WriteAttributeString("PartName", "/xl/worksheets/sheet" & index.ToString(CultureInfo.InvariantCulture) & ".xml")
                writer.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")
                writer.WriteEndElement()
            Next
            writer.WriteEndElement()
            CloseXmlWriter(writer)
        End Using
        Using writer = StartXmlEntry(archive, "_rels/.rels")
            writer.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships")
            WriteRelationship(writer, "rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "xl/workbook.xml")
            writer.WriteEndElement()
            CloseXmlWriter(writer)
        End Using
        Using writer = StartXmlEntry(archive, "xl/_rels/workbook.xml.rels")
            writer.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships")
            For index = 1 To allNames.Count
                WriteRelationship(writer, "rId" & index.ToString(CultureInfo.InvariantCulture),
                    "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet",
                    "worksheets/sheet" & index.ToString(CultureInfo.InvariantCulture) & ".xml")
            Next
            writer.WriteEndElement()
            CloseXmlWriter(writer)
        End Using
        Using writer = StartXmlEntry(archive, "xl/workbook.xml")
            writer.WriteStartElement("workbook", "http://schemas.openxmlformats.org/spreadsheetml/2006/main")
            writer.WriteAttributeString("xmlns", "r", Nothing, "http://schemas.openxmlformats.org/officeDocument/2006/relationships")
            writer.WriteStartElement("sheets", "http://schemas.openxmlformats.org/spreadsheetml/2006/main")
            For index = 0 To allNames.Count - 1
                writer.WriteStartElement("sheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main")
                writer.WriteAttributeString("name", allNames(index))
                writer.WriteAttributeString("sheetId", (index + 1).ToString(CultureInfo.InvariantCulture))
                writer.WriteAttributeString("r", "id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships",
                    "rId" & (index + 1).ToString(CultureInfo.InvariantCulture))
                writer.WriteEndElement()
            Next
            writer.WriteEndElement()
            writer.WriteEndElement()
            CloseXmlWriter(writer)
        End Using
    End Sub

    Private Shared Sub WriteRelationship(writer As XmlWriter, id As String, relationType As String, target As String)
        writer.WriteStartElement("Relationship", "http://schemas.openxmlformats.org/package/2006/relationships")
        writer.WriteAttributeString("Id", id)
        writer.WriteAttributeString("Type", relationType)
        writer.WriteAttributeString("Target", target)
        writer.WriteEndElement()
    End Sub

    Private Shared Function StartXmlEntry(archive As ZipArchive, entryName As String) As XmlWriter
        Dim writer = XmlWriter.Create(archive.CreateEntry(entryName, CompressionLevel.Optimal).Open(),
            New XmlWriterSettings With {.Encoding = New UTF8Encoding(False), .Indent = False, .CloseOutput = True})
        writer.WriteStartDocument()
        Return writer
    End Function

    Private Shared Sub CloseXmlWriter(writer As XmlWriter)
        writer.WriteEndDocument()
        writer.Dispose()
    End Sub

    Private Shared Function CellReference(column As Integer, row As Integer) As String
        Dim result = String.Empty
        While column > 0
            column -= 1
            result = ChrW(AscW("A"c) + (column Mod 26)) & result
            column \= 26
        End While
        Return result & row.ToString(CultureInfo.InvariantCulture)
    End Function

    Private Shared Sub ValidateWorkbook(path As String)
        Using archive = ZipFile.OpenRead(path)
            For Each required In {"[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/worksheets/sheet1.xml"}
                If archive.GetEntry(required) Is Nothing Then Throw New InvalidDataException("Generated XLSX is missing required OOXML part: " & required)
            Next
            For Each entry In archive.Entries.Where(Function(item) item.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                Using input = entry.Open()
                    Dim document As New XmlDocument()
                    document.Load(input)
                End Using
            Next
        End Using
    End Sub

    Public Shared Sub ValidateExistingExcelExport(path As String)
        If String.IsNullOrWhiteSpace(path) Then Throw New ArgumentException("Existing Excel report path is required.", NameOf(path))
        If Not File.Exists(path) Then Throw New FileNotFoundException("Existing Excel report was not found.", path)
        Using archive = ZipFile.OpenRead(path)
            For Each required In {"[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/worksheets/sheet1.xml"}
                If archive.GetEntry(required) Is Nothing Then Throw New InvalidDataException("Existing XLSX is missing required OOXML part: " & required)
            Next
            For Each entry In archive.Entries.Where(
                Function(item) item.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) AndAlso
                    Not (item.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal) AndAlso
                         Not String.Equals(item.FullName, "xl/worksheets/sheet1.xml", StringComparison.Ordinal)))
                Using input = entry.Open()
                    Dim document As New XmlDocument()
                    document.XmlResolver = Nothing
                    document.Load(input)
                End Using
            Next
            Dim worksheets = archive.Entries.Where(
                Function(item) item.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) AndAlso
                    item.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList()
            If worksheets.Count = 0 OrElse worksheets.Any(Function(item) item.Length = 0) Then
                Throw New InvalidDataException("Existing XLSX contains an empty or missing worksheet.")
            End If
        End Using
    End Sub

    Private Shared Function WritePdf(historian As MeasurementHistorian,
                                     settings As RecordingSettings,
                                     analyzerId As String,
                                     startUtc As DateTime,
                                     endUtc As DateTime,
                                     outputDirectory As String,
                                     fileName As String,
                                     cancellationToken As CancellationToken) As String
        Dim selected = settings.GetAnalyzer(analyzerId).SelectedParameters
        Dim storedSelected = SelectedStoredIds(analyzerId, selected)
        Dim sources = GetSourceParameterIds(analyzerId, selected)
        Dim maximumGapSeconds = MaximumIntegrationGapSeconds(settings)
        If sources.Count = 0 OrElse
           historian.CountRawMeasurements(analyzerId, sources, startUtc, endUtc) = 0 Then
            Throw New InvalidOperationException(
                "No good stored measurements match the selected report parameters in the requested time window.")
        End If
        Directory.CreateDirectory(outputDirectory)
        Dim finalPath = Path.Combine(outputDirectory, fileName)
        If File.Exists(finalPath) Then Throw New IOException("Export filename already exists; refusing to overwrite: " & finalPath)
        Dim temporaryFolder = Path.Combine(Path.GetTempPath(), "eoems-pdf-pages-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(temporaryFolder)
        Dim temporaryPdf = finalPath & "." & Guid.NewGuid().ToString("N") & ".partial"
        Try
            Dim pageLines As New List(Of String)()
            Dim statistics As New Dictionary(Of String, ValueStatistics)(StringComparer.Ordinal)
            pageLines.Add("Analyzer: " & analyzerId & " - " & settings.GetAnalyzer(analyzerId).EquipmentName)
            pageLines.Add("Window UTC [start, end): " & startUtc.ToString("o", CultureInfo.InvariantCulture) & " / " & endUtc.ToString("o", CultureInfo.InvariantCulture))
            pageLines.Add("Factory time: " & FormatFactoryWindow(settings, startUtc, endUtc))
            pageLines.Add("Source freshness unavailable; host timestamps do not establish PM5110 conversion time.")
            pageLines.Add("Raw decoded values; unverified scaling is labelled. No missing values are zero-filled.")
            pageLines.Add("")
            pageLines.Add("SAVED MEASUREMENTS - UTC | PARAMETER | VALUE | UNIT | QUALITY / TYPE")
            Dim pageFiles As New List(Of String)()
            Dim pageIndex = 0
            historian.StreamRawMeasurements(analyzerId, sources, startUtc, endUtc,
                Sub(point)
                    cancellationToken.ThrowIfCancellationRequested()
                    Dim item As ValueStatistics = Nothing
                    If Not statistics.TryGetValue(point.ParameterId, item) Then
                        item = New ValueStatistics(maximumGapSeconds)
                        statistics.Add(point.ParameterId, item)
                    End If
                    item.Add(point.Value, point.TimestampUtc)
                    If Not storedSelected.Contains(point.ParameterId) Then Return
                    If pageLines.Count = 0 Then pageLines.Add("SAVED MEASUREMENTS - UTC | PARAMETER | VALUE | UNIT | QUALITY / TYPE")
                    pageLines.Add(FormatPdfRow(point))
                    If pageLines.Count >= 42 Then SavePdfPage(temporaryFolder, pageFiles, pageLines, pageIndex)
                End Sub, cancellationToken)
            Dim windowStatistics = CalculateWindowStatistics(analyzerId, selected, statistics)
            If pageLines.Count > 0 OrElse pageFiles.Count = 0 Then SavePdfPage(temporaryFolder, pageFiles, pageLines, pageIndex)
            pageLines.Add("SAVED-SAMPLE STATISTICS")
            pageLines.Add("Peak is the maximum persisted sample in this report window, not an instantaneous waveform peak.")
            pageLines.Add("Extrema timestamps are UTC. Missing intervals are not interpolated.")
            pageLines.Add("")
            For Each parameter In MeasurementCatalog.GetCatalog(analyzerId).Where(Function(info) Not info.IsWindowStatistic)
                Dim item As ValueStatistics = Nothing
                If storedSelected.Contains(parameter.Id) AndAlso statistics.TryGetValue(parameter.Id, item) Then
                    pageLines.Add(parameter.Label & " [" & parameter.Unit & "] | samples " &
                        item.Count.ToString("N0", CultureInfo.InvariantCulture) & " | minimum " &
                        item.Minimum.ToString("0.###", CultureInfo.InvariantCulture) & " at " &
                        item.MinimumAt.ToString("yyyy-MM-dd HH:mm:ss.f", CultureInfo.InvariantCulture) & " UTC")
                    pageLines.Add("  maximum recorded peak " & item.Maximum.ToString("0.###", CultureInfo.InvariantCulture) &
                        " at " & item.MaximumAt.ToString("yyyy-MM-dd HH:mm:ss.f", CultureInfo.InvariantCulture) &
                        " UTC | average " & (item.Sum / item.Count).ToString("0.###", CultureInfo.InvariantCulture))
                End If
            Next
            If windowStatistics.Count > 0 Then
                pageLines.Add("")
                pageLines.Add("PEAK, AVERAGE & ENERGY (REPORT WINDOW)")
                For Each result In windowStatistics
                    pageLines.Add(result.Info.Label & ": " & result.Value.ToString("0.###", CultureInfo.InvariantCulture) & " " & result.Info.Unit &
                        If(result.AtUtc.HasValue, " at " & result.AtUtc.Value.ToString("yyyy-MM-dd HH:mm:ss.f", CultureInfo.InvariantCulture) & " UTC", String.Empty))
                    pageLines.Add("  " & result.Basis)
                    If pageLines.Count >= 42 Then SavePdfPage(temporaryFolder, pageFiles, pageLines, pageIndex)
                Next
                pageLines.Add("Gaps over " & maximumGapSeconds.ToString("0", CultureInfo.InvariantCulture) &
                    " s are not bridged. kWh is calculated energy, not a meter energy register.")
            End If
            SavePdfPage(temporaryFolder, pageFiles, pageLines, pageIndex)
            WritePdfDocument(temporaryPdf, pageFiles)
            ValidatePdf(temporaryPdf)
            If File.Exists(finalPath) Then Throw New IOException("PDF filename collision occurred before promotion: " & finalPath)
            File.Move(temporaryPdf, finalPath)
            Return finalPath
        Finally
            If File.Exists(temporaryPdf) Then File.Delete(temporaryPdf)
            If Directory.Exists(temporaryFolder) Then Directory.Delete(temporaryFolder, True)
        End Try
    End Function

    Private Shared Sub SavePdfPage(folder As String, pageFiles As List(Of String), lines As List(Of String), ByRef pageIndex As Integer)
        Dim pagePath = Path.Combine(folder, "page-" & pageIndex.ToString("D6", CultureInfo.InvariantCulture) & ".txt")
        File.WriteAllLines(pagePath, lines.Select(AddressOf ToPdfText), Encoding.ASCII)
        pageFiles.Add(pagePath)
        pageIndex += 1
        lines.Clear()
    End Sub

    Private Shared Function FormatPdfRow(point As HistorianMeasurementPoint) As String
        Return point.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss.f", CultureInfo.InvariantCulture) & " UTC | " &
            ParameterLabel(point.AnalyzerId, point.ParameterId) & " | " & point.Value.ToString("0.####", CultureInfo.InvariantCulture) &
            " | " & point.Unit & " | " & point.Quality & "/" & point.SampleKind
    End Function

    Private Shared Function ToPdfText(value As String) As String
        Return value.Replace("°", "deg").Replace("—", "-").Replace("–", "-").
            Select(Function(character) If(AscW(character) >= 32 AndAlso AscW(character) <= 126, character, "?"c)).
            Aggregate(New StringBuilder(), Function(builder, character) builder.Append(character)).ToString()
    End Function

    Private Shared Sub WritePdfDocument(outputPath As String, pageFiles As IReadOnlyList(Of String))
        Dim pageCount = pageFiles.Count
        Dim objectCount = 3 + pageCount * 2
        Dim offsets(objectCount) As Long
        Using stream As New FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            WriteAscii(stream, "%PDF-1.4" & vbLf)
            offsets(1) = stream.Position
            WriteAscii(stream, "1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj" & vbLf)
            offsets(2) = stream.Position
            Dim kids = String.Join(" ", Enumerable.Range(0, pageCount).Select(Function(index) (4 + index * 2).ToString(CultureInfo.InvariantCulture) & " 0 R"))
            WriteAscii(stream, "2 0 obj<</Type/Pages/Count " & pageCount.ToString(CultureInfo.InvariantCulture) & "/Kids[" & kids & "]>>endobj" & vbLf)
            offsets(3) = stream.Position
            WriteAscii(stream, "3 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>endobj" & vbLf)
            For index = 0 To pageCount - 1
                Dim pageObject = 4 + index * 2
                Dim contentObject = pageObject + 1
                offsets(pageObject) = stream.Position
                WriteAscii(stream, pageObject.ToString(CultureInfo.InvariantCulture) &
                    " 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Resources<</Font<</F1 3 0 R>>>>/Contents " &
                    contentObject.ToString(CultureInfo.InvariantCulture) & " 0 R>>endobj" & vbLf)
                Dim content = BuildPdfPageContent(pageFiles(index), index + 1, pageCount)
                offsets(contentObject) = stream.Position
                WriteAscii(stream, contentObject.ToString(CultureInfo.InvariantCulture) & " 0 obj<</Length " &
                    content.Length.ToString(CultureInfo.InvariantCulture) & ">>stream" & vbLf)
                stream.Write(content, 0, content.Length)
                WriteAscii(stream, vbLf & "endstream endobj" & vbLf)
            Next
            Dim xrefOffset = stream.Position
            WriteAscii(stream, "xref" & vbLf & "0 " & (objectCount + 1).ToString(CultureInfo.InvariantCulture) & vbLf)
            WriteAscii(stream, "0000000000 65535 f " & vbLf)
            For index = 1 To objectCount
                WriteAscii(stream, offsets(index).ToString("D10", CultureInfo.InvariantCulture) & " 00000 n " & vbLf)
            Next
            WriteAscii(stream, "trailer<</Size " & (objectCount + 1).ToString(CultureInfo.InvariantCulture) & "/Root 1 0 R>>" & vbLf &
                "startxref" & vbLf & xrefOffset.ToString(CultureInfo.InvariantCulture) & vbLf & "%%EOF")
        End Using
    End Sub

    Private Shared Sub ValidatePdf(path As String)
        Dim bytes = File.ReadAllBytes(path)
        If bytes.Length < 100 Then Throw New InvalidDataException("Generated PDF is unexpectedly small.")
        Dim content = Encoding.ASCII.GetString(bytes)
        If Not content.StartsWith("%PDF-1.4" & vbLf, StringComparison.Ordinal) OrElse
           Not content.TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal) Then
            Throw New InvalidDataException("Generated PDF header or EOF validation failed.")
        End If
        Dim startXref = content.LastIndexOf("startxref", StringComparison.Ordinal)
        If startXref < 0 Then Throw New InvalidDataException("Generated PDF is missing its cross-reference pointer.")
        Dim offsetText = content.Substring(startXref + "startxref".Length).Trim()
        Dim offsetLine = offsetText.Split({ControlChars.Cr, ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
        Dim xrefOffset As Long
        If Not Long.TryParse(offsetLine, NumberStyles.None, CultureInfo.InvariantCulture, xrefOffset) OrElse
           xrefOffset < 0 OrElse xrefOffset >= bytes.LongLength Then
            Throw New InvalidDataException("Generated PDF cross-reference pointer is invalid.")
        End If
        Using reader As New StringReader(content.Substring(CInt(xrefOffset)))
            If reader.ReadLine() <> "xref" Then Throw New InvalidDataException("Generated PDF cross-reference table is missing.")
            Dim subsection = reader.ReadLine().Split({" "c}, StringSplitOptions.RemoveEmptyEntries)
            Dim firstObject As Integer
            Dim objectCount As Integer
            If subsection.Length <> 2 OrElse Not Integer.TryParse(subsection(0), firstObject) OrElse firstObject <> 0 OrElse
               Not Integer.TryParse(subsection(1), objectCount) OrElse objectCount < 2 Then
                Throw New InvalidDataException("Generated PDF cross-reference table header is invalid.")
            End If
            reader.ReadLine()
            For objectNumber = 1 To objectCount - 1
                Dim entry = reader.ReadLine()
                Dim objectOffset As Long
                If String.IsNullOrEmpty(entry) OrElse entry.Length < 17 OrElse
                   Not Long.TryParse(entry.Substring(0, 10), NumberStyles.None, CultureInfo.InvariantCulture, objectOffset) OrElse
                   objectOffset < 0 OrElse objectOffset >= bytes.LongLength OrElse
                   Not content.Substring(CInt(objectOffset)).StartsWith(objectNumber.ToString(CultureInfo.InvariantCulture) & " 0 obj", StringComparison.Ordinal) Then
                    Throw New InvalidDataException("Generated PDF object offset is invalid for object " & objectNumber.ToString(CultureInfo.InvariantCulture) & ".")
                End If
            Next
        End Using
    End Sub

    Private Shared Function BuildPdfPageContent(pagePath As String, pageNumber As Integer, pageCount As Integer) As Byte()
        Dim builder As New StringBuilder()
        builder.Append("0.03 0.10 0.20 rg 40 755 532 3 re f 0 0 0 rg BT /F1 13 Tf 42 736 Td (")
        builder.Append(EscapePdfText("TWELLIUM INDUSTRIAL COMPANY"))
        builder.Append(") Tj ET BT /F1 9 Tf 42 718 Td (ELECTRICAL DEPARTMENT - POWER ANALYSIS) Tj ET ")
        Dim y = 697
        For Each line In File.ReadLines(pagePath, Encoding.ASCII)
            Dim sectionHeading = line.StartsWith("SAVED MEASUREMENTS -", StringComparison.Ordinal) OrElse
                                 line.StartsWith("SAVED-SAMPLE STATISTICS", StringComparison.Ordinal)
            builder.Append(If(sectionHeading, "0.05 0.20 0.42 rg ", "0.12 0.16 0.22 rg "))
            builder.Append("BT /F1 ")
            builder.Append(If(sectionHeading, "9", "8"))
            builder.Append(" Tf 42 ")
            builder.Append(y.ToString(CultureInfo.InvariantCulture))
            builder.Append(" Td (")
            builder.Append(EscapePdfText(line))
            builder.Append(") Tj ET ")
            y -= 15
        Next
        builder.Append("0.0 0.65 0.78 RG 42 30 m 570 30 l S BT /F1 7 Tf 42 18 Td (")
        builder.Append(pageNumber.ToString(CultureInfo.InvariantCulture))
        builder.Append(" / ")
        builder.Append(pageCount.ToString(CultureInfo.InvariantCulture))
        builder.Append(" - Generated from EOEMS historian snapshot; not PLC commissioning evidence.) Tj ET")
        Return Encoding.ASCII.GetBytes(builder.ToString())
    End Function

    Private Shared Function EscapePdfText(value As String) As String
        Return ToPdfText(value).Replace("\", "\\").Replace("(", "\(").Replace(")", "\)")
    End Function

    Private Shared Sub WriteAscii(stream As Stream, value As String)
        Dim bytes = Encoding.ASCII.GetBytes(value)
        stream.Write(bytes, 0, bytes.Length)
    End Sub

    Private Shared Sub ValidateRequest(historian As MeasurementHistorian,
                                       settings As RecordingSettings,
                                       analyzerId As String,
                                       startUtc As DateTime,
                                       endUtc As DateTime)
        If historian Is Nothing Then Throw New ArgumentNullException(NameOf(historian))
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        If startUtc.Kind <> DateTimeKind.Utc OrElse endUtc.Kind <> DateTimeKind.Utc OrElse endUtc <= startUtc Then
            Throw New ArgumentOutOfRangeException(NameOf(startUtc), "Export window must be a positive UTC interval [start, end).")
        End If
        settings.GetAnalyzer(analyzerId)
    End Sub

    Private Shared Function GetExpectedSampleCount(startUtc As DateTime,
                                                   endUtc As DateTime,
                                                   recordingIntervalMilliseconds As Integer) As Long
        If recordingIntervalMilliseconds < RecordingSettings.MinimumRecordingIntervalMilliseconds Then
            Throw New InvalidDataException("The configured historian recording interval is invalid for coverage estimation.")
        End If
        Return CLng(Math.Ceiling((endUtc - startUtc).TotalMilliseconds / recordingIntervalMilliseconds))
    End Function

    Private Shared Function FormatFactoryWindow(settings As RecordingSettings, startUtc As DateTime, endUtc As DateTime) As String
        Dim timezone = settings.ResolveFactoryTimeZone()
        Dim startLocal = TimeZoneInfo.ConvertTimeFromUtc(startUtc, timezone)
        Dim endLocal = TimeZoneInfo.ConvertTimeFromUtc(endUtc, timezone)
        Return startLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) & " to " &
               endLocal.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) & " (" & settings.FactoryTimeZoneId & ")"
    End Function

    Private Shared Function ParameterLabel(analyzerId As String, parameterId As String) As String
        Dim item = MeasurementCatalog.Find(analyzerId, parameterId)
        Return If(item Is Nothing, parameterId, item.Label)
    End Function

    Private Shared Function OptionalText(value As String) As String
        Return If(String.IsNullOrWhiteSpace(value), "Not configured", value.Trim())
    End Function

    Private Shared Function OptionalNumber(value As Double?) As String
        Return If(value.HasValue, value.Value.ToString("0.##", CultureInfo.InvariantCulture), "Not configured")
    End Function
End Class
