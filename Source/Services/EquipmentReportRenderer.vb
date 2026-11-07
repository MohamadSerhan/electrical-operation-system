Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Drawing.Imaging
Imports System.Drawing.Text
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text

' Draws the analysis as colour A4 pages (cards, charts, findings) and stores them in a PDF
' together with an invisible text layer so the report stays searchable.
Public NotInheritable Class EquipmentReportRenderer
    Implements IDisposable

    Private Const PageW As Integer = 1240
    Private Const PageH As Integer = 1754
    Private Const Margin As Integer = 60
    Private Const ContentW As Integer = PageW - 2 * Margin
    Private Const BottomLimit As Integer = PageH - 100
    Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture

    Private Shared ReadOnly Navy As Color = Color.FromArgb(15, 38, 77)
    Private Shared ReadOnly Blue As Color = Color.FromArgb(37, 99, 235)
    Private Shared ReadOnly Green As Color = Color.FromArgb(22, 163, 74)
    Private Shared ReadOnly Amber As Color = Color.FromArgb(217, 119, 6)
    Private Shared ReadOnly Red As Color = Color.FromArgb(220, 38, 38)
    Private Shared ReadOnly Ink As Color = Color.FromArgb(30, 41, 59)
    Private Shared ReadOnly Muted As Color = Color.FromArgb(100, 116, 139)
    Private Shared ReadOnly Line As Color = Color.FromArgb(226, 232, 240)
    Private Shared ReadOnly Soft As Color = Color.FromArgb(241, 245, 249)
    Private Shared ReadOnly SeriesColors As Color() = {Color.FromArgb(37, 99, 235), Color.FromArgb(220, 38, 38), Color.FromArgb(22, 163, 74),
        Color.FromArgb(147, 51, 234), Color.FromArgb(217, 119, 6), Color.FromArgb(8, 145, 178)}

    Private ReadOnly _analysis As EquipmentAnalysis
    Private ReadOnly _pages As New List(Of Byte())()
    Private ReadOnly _texts As New List(Of String)()
    Private ReadOnly _pageText As New StringBuilder()
    Private ReadOnly _body As New Font("Segoe UI", 20.0F, FontStyle.Regular, GraphicsUnit.Pixel)
    Private ReadOnly _bold As New Font("Segoe UI", 20.0F, FontStyle.Bold, GraphicsUnit.Pixel)
    Private ReadOnly _small As New Font("Segoe UI", 16.0F, FontStyle.Regular, GraphicsUnit.Pixel)
    Private ReadOnly _label As New Font("Segoe UI", 15.0F, FontStyle.Bold, GraphicsUnit.Pixel)
    Private ReadOnly _h2 As New Font("Segoe UI", 28.0F, FontStyle.Bold, GraphicsUnit.Pixel)
    Private ReadOnly _kpi As New Font("Segoe UI", 38.0F, FontStyle.Bold, GraphicsUnit.Pixel)
    Private ReadOnly _title As New Font("Segoe UI", 46.0F, FontStyle.Bold, GraphicsUnit.Pixel)
    Private _bitmap As Bitmap
    Private _g As Graphics
    Private _y As Integer
    Private _pageNumber As Integer
    Private ReadOnly _totalPages As Integer
    Private ReadOnly _dryRun As Boolean
    Private _sectionContext As String = String.Empty

    Private Sub New(analysis As EquipmentAnalysis, totalPages As Integer, dryRun As Boolean)
        _analysis = analysis
        _totalPages = totalPages
        _dryRun = dryRun
        Try
            Dim logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "TwelliumLogo.png")
            If Not dryRun AndAlso File.Exists(logoPath) Then
                Using source = Image.FromFile(logoPath)
                    _logo = New Bitmap(source, 396, 224)
                End Using
            End If
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is OutOfMemoryException OrElse TypeOf ex Is ArgumentException
            _logo = Nothing
        End Try
    End Sub

    Private ReadOnly _logo As Bitmap

    ' The first pass only counts pages (no image encoding) so the second pass can print "Page n of N".
    Public Shared Sub WritePdf(analysis As EquipmentAnalysis, path As String)
        Dim total As Integer
        Using measure As New EquipmentReportRenderer(analysis, 0, True)
            measure.RenderAll()
            total = measure._pageNumber
        End Using
        Using renderer As New EquipmentReportRenderer(analysis, total, False)
            renderer.RenderAll()
            renderer.SavePdf(path)
        End Using
    End Sub

    Private Shared Function SeverityColor(severity As Integer) As Color
        Return If(severity >= 2, Red, If(severity = 1, Amber, Green))
    End Function

    Private Shared Function Tint(color As Color, alpha As Integer) As Color
        Return Color.FromArgb(alpha, color)
    End Function

    Private Sub RenderAll()
        NewPage(True)
        DrawSummaryPage()
        DrawScope()
        DrawCapacityComparison()
        DrawDataAssurance()
        DrawElectricalKnowledge()
        DrawUpsPowerBalance()
        If _analysis.Comparison IsNot Nothing Then DrawSelectedComparison()
        DrawChronology()
        DrawComparisonPermissions()
        DrawRootCause()
        Dim comparison = _analysis.Channels.GroupBy(Function(item) New With {
            Key .Unit = item.Metric.Unit, Key .Equipment = ElectricalReportKnowledge.EquipmentIdentity(item.Metric, _analysis.Result.Request.Equipment.Id),
            Key .Quantity = ElectricalReportKnowledge.ComparisonQuantity(item.Metric)}).
            Where(Function(group) group.Count() >= 2 AndAlso group.Key.Unit.Length > 0).ToList()
        For Each group In comparison
            Dim members = group.ToList()
            Dim parts = CInt(Math.Ceiling(members.Count / 6.0R))
            Dim size = CInt(Math.Ceiling(members.Count / CDbl(parts)))
            For part = 0 To parts - 1
                DrawComparison(members.Skip(part * size).Take(size).ToList(), group.Key.Unit, group.Key.Quantity)
            Next
        Next
        For Each channel In _analysis.Channels
            DrawChannel(channel)
        Next
        If _analysis.Events.Count > 0 Then DrawEventTimeline()
        DrawAdviceAndMethod()
        Flush()
    End Sub

    Private Sub NewPage(first As Boolean)
        If _bitmap IsNot Nothing Then Flush()
        _bitmap = New Bitmap(PageW, PageH, PixelFormat.Format24bppRgb)
        _bitmap.SetResolution(150.0F, 150.0F)
        _g = Graphics.FromImage(_bitmap)
        _g.SmoothingMode = SmoothingMode.AntiAlias
        _g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit
        _g.Clear(Color.White)
        _pageNumber += 1
        _pageText.Clear()
        Dim request = _analysis.Result.Request
        If first Then
            Using brush As New SolidBrush(Navy)
                _g.FillRectangle(brush, 0, 0, PageW, 310)
            End Using
            Using brush As New SolidBrush(Blue)
                _g.FillRectangle(brush, 0, 310, PageW, 8)
            End Using
            Using brush As New SolidBrush(Color.FromArgb(30, 64, 120))
                _g.FillEllipse(brush, PageW - 330, -120, 420, 420)
            End Using
            Dim textX = Margin + 84
            If _logo IsNot Nothing Then
                _g.InterpolationMode = InterpolationMode.HighQualityBicubic
                _g.DrawImage(_logo, New Rectangle(Margin, 30, 132, 75))
                textX = Margin + 156
            Else
                Dim logo As New Rectangle(Margin, 38, 64, 64)
                Using path = Rounded(logo, 14)
                    Using brush As New SolidBrush(Blue)
                        _g.FillPath(brush, path)
                    End Using
                End Using
            End If
            Text("TWELLIUM INDUSTRIAL COMPANY", _h2, Color.White, textX, 38, ContentW - 100)
            Text("ELECTRICAL DEPARTMENT", _label, Color.FromArgb(147, 197, 253), textX + 2, 78, ContentW - 100)
            Text("Equipment Analysis Report", _title, Color.White, Margin, 130, ContentW)
            Text(_analysis.StudyName & "   |   " & request.Equipment.Category, _bold, Color.FromArgb(203, 213, 225), Margin, 200, ContentW)
            Text("Selected study: " & _analysis.Scope.Count.ToString(Inv) & " channels  |  UTC timestamps  |  detailed scope and evidence inside",
                _small, Color.FromArgb(186, 200, 220), Margin, 240, ContentW)
            Text("Generated " & EquipmentAnalysis.Stamp(_analysis.Result.GeneratedUtc) & "   |   EOEMS SCADA", _small, Color.FromArgb(148, 163, 184), Margin, 270, ContentW)
            _y = 350
            _pageText.AppendLine("EQUIPMENT ANALYSIS REPORT")
            _pageText.AppendLine("TWELLIUM INDUSTRIAL COMPANY - ELECTRICAL DEPARTMENT")
        Else
            Using brush As New SolidBrush(Navy)
                _g.FillRectangle(brush, 0, 0, PageW, 64)
            End Using
            Dim innerX = Margin
            If _logo IsNot Nothing Then
                _g.InterpolationMode = InterpolationMode.HighQualityBicubic
                _g.DrawImage(_logo, New Rectangle(Margin, 10, 70, 44))
                innerX = Margin + 86
            End If
            Text("TWELLIUM INDUSTRIAL COMPANY  |  Electrical Department  |  " & _analysis.StudyName, _label, Color.White, innerX, 22, ContentW - 86)
            Text(_sectionContext, _label, Muted, Margin, 80, ContentW)
            _y = 116
        End If
    End Sub

    Private Sub Flush()
        If _bitmap Is Nothing Then Return
        Using brush As New SolidBrush(Line)
            _g.FillRectangle(brush, Margin, PageH - 70, ContentW, 2)
        End Using
        Text("Twellium Industrial Company  -  Electrical Department  -  rule-based analysis of recorded readings", _small, Muted, Margin, PageH - 56, ContentW - 160)
        Text("Page " & _pageNumber.ToString(Inv) & " of " & _totalPages.ToString(Inv), _small, Muted, PageW - Margin - 130, PageH - 56, 130)
        If _dryRun Then
            _g.Dispose()
            _bitmap.Dispose()
            _g = Nothing
            _bitmap = Nothing
            Return
        End If
        Using stream As New MemoryStream()
            Dim encoder = ImageCodecInfo.GetImageEncoders().First(Function(item) item.MimeType = "image/jpeg")
            Using parameters As New EncoderParameters(1)
                parameters.Param(0) = New EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L)
                _bitmap.Save(stream, encoder, parameters)
            End Using
            _pages.Add(stream.ToArray())
        End Using
        _texts.Add(_pageText.ToString())
        _g.Dispose()
        _bitmap.Dispose()
        _g = Nothing
        _bitmap = Nothing
    End Sub

    Private Sub Ensure(height As Integer)
        If _y + height > BottomLimit Then NewPage(False)
    End Sub

    Private Sub Text(value As String, font As Font, color As Color, x As Integer, y As Integer, width As Integer)
        Using brush As New SolidBrush(color)
            Dim format As New StringFormat(StringFormatFlags.NoWrap) With {.Trimming = StringTrimming.EllipsisCharacter}
            _g.DrawString(value, font, brush, New RectangleF(x, y, width, font.Height + 6), format)
            format.Dispose()
        End Using
        _pageText.AppendLine(value)
    End Sub

    Private Function MeasureHeight(value As String, font As Font, width As Integer) As Integer
        Return CInt(Math.Ceiling(_g.MeasureString(value, font, width).Height))
    End Function

    Private Function DrawParagraph(value As String, font As Font, color As Color, x As Integer, width As Integer) As Integer
        Dim height = MeasureHeight(value, font, width)
        Ensure(height + 6)
        Using brush As New SolidBrush(color)
            _g.DrawString(value, font, brush, New RectangleF(x, _y, width, height + 4))
        End Using
        _pageText.AppendLine(value)
        _y += height + 6
        Return height
    End Function

    Private Shared Function Rounded(rectangle As Rectangle, radius As Integer) As GraphicsPath
        Dim path As New GraphicsPath()
        Dim d = radius * 2
        path.AddArc(rectangle.X, rectangle.Y, d, d, 180, 90)
        path.AddArc(rectangle.Right - d, rectangle.Y, d, d, 270, 90)
        path.AddArc(rectangle.Right - d, rectangle.Bottom - d, d, d, 0, 90)
        path.AddArc(rectangle.X, rectangle.Bottom - d, d, d, 90, 90)
        path.CloseFigure()
        Return path
    End Function

    Private Sub Card(rectangle As Rectangle, fill As Color, border As Color)
        Using path = Rounded(rectangle, 10)
            Using brush As New SolidBrush(fill)
                _g.FillPath(brush, path)
            End Using
            Using pen As New Pen(border, 2.0F)
                _g.DrawPath(pen, path)
            End Using
        End Using
    End Sub

    Private Function Chip(label As String, color As Color, x As Integer, y As Integer) As Integer
        Dim width = CInt(_g.MeasureString(label, _label).Width) + 22
        Dim rectangle As New Rectangle(x, y, width, 28)
        Using path = Rounded(rectangle, 14)
            Using brush As New SolidBrush(color)
                _g.FillPath(brush, path)
            End Using
        End Using
        Using brush As New SolidBrush(Color.White)
            _g.DrawString(label, _label, brush, x + 11, y + 4)
        End Using
        _pageText.Append(label).Append(" "c)
        Return width
    End Function

    Private Sub SectionTitle(value As String, Optional accent As Color = Nothing)
        _sectionContext = value
        Ensure(90)
        Dim stripe = If(accent.IsEmpty, Blue, accent)
        Using brush As New SolidBrush(stripe)
            _g.FillRectangle(brush, Margin, _y, 8, 40)
        End Using
        Text(value, _h2, Ink, Margin + 22, _y, ContentW - 22)
        _y += 58
    End Sub

    Private Sub DrawTable(headers As String(), widths As Integer(), rows As IEnumerable(Of String()))
        If widths.Sum() <> ContentW OrElse widths.Length <> headers.Length Then Throw New ArgumentException("Report table columns must fit the content width.")
        Dim records = rows.ToArray()
        If records.Length = 0 Then Return
        Dim rowHeight As Func(Of String(), Integer) =
            Function(row)
                If row.Length <> widths.Length Then Throw New ArgumentException("Report table row does not match its columns.")
                Return Math.Max(50, row.Select(Function(value, index) MeasureHeight(value, _small, widths(index) - 24) + 24).Max())
            End Function
        Dim header = Sub()
                         Using brush As New SolidBrush(Navy)
                             _g.FillRectangle(brush, Margin, _y, ContentW, 52)
                         End Using
                         Dim x = Margin
                         For index = 0 To headers.Length - 1
                             Using brush As New SolidBrush(Color.White)
                                 _g.DrawString(headers(index), _label, brush, New RectangleF(x + 12, _y + 8, widths(index) - 24, 42))
                             End Using
                             _pageText.AppendLine(headers(index))
                             x += widths(index)
                         Next
                         _y += 52
                     End Sub
        Ensure(52 + rowHeight(records(0)))
        header()
        Dim alternate = False
        For Each row In records
            Dim height = rowHeight(row)
            If height > BottomLimit - 168 Then Throw New InvalidDataException("A report table row is too tall to fit a page.")
            If _y + height > BottomLimit Then
                NewPage(False)
                header()
            End If
            Using brush As New SolidBrush(If(alternate, Soft, Color.White))
                _g.FillRectangle(brush, Margin, _y, ContentW, height)
            End Using
            Dim x = Margin
            For index = 0 To row.Length - 1
                Using brush As New SolidBrush(If(index = 0, Navy, Ink))
                    _g.DrawString(row(index), _small, brush, New RectangleF(x + 12, _y + 12, widths(index) - 24, height - 20))
                End Using
                _pageText.AppendLine(row(index))
                x += widths(index)
            Next
            Using pen As New Pen(Line)
                _g.DrawLine(pen, Margin, _y + height, Margin + ContentW, _y + height)
            End Using
            _y += height
            alternate = Not alternate
        Next
        _y += 24
    End Sub

    Private Sub StatCard(x As Integer, y As Integer, width As Integer, height As Integer, label As String, value As String, subtitle As String, color As Color)
        Card(New Rectangle(x, y, width, height), Color.White, Line)
        Using brush As New SolidBrush(color)
            _g.FillRectangle(brush, x + 1, y + 14, 6, height - 28)
        End Using
        Text(label, _label, Muted, x + 20, y + 12, width - 28)
        Text(value, If(height > 110, _kpi, _h2), Ink, x + 20, y + 36, width - 28)
        If subtitle.Length > 0 Then Text(subtitle, _small, Muted, x + 20, y + height - 32, width - 28)
    End Sub

    Private Sub DrawSummaryPage()
        Dim request = _analysis.Result.Request
        Dim verdictColor = SeverityColor(_analysis.Severity)
        Dim banner As New Rectangle(Margin, _y, ContentW, 140)
        Card(banner, Color.FromArgb(18, verdictColor), verdictColor)
        Using brush As New SolidBrush(verdictColor)
            _g.FillRectangle(brush, banner.X, banner.Y + 14, 10, banner.Height - 28)
        End Using
        Text("OVERALL STATUS", _label, Muted, banner.X + 30, banner.Y + 14, 400)
        Text(_analysis.Verdict, _h2, verdictColor, banner.X + 30, banner.Y + 38, 600)
        Dim critical = _analysis.Findings.Where(Function(item) item.Severity >= 2).Count()
        Dim warnings = _analysis.Findings.Where(Function(item) item.Severity = 1).Count()
        Text(critical.ToString(Inv) & " critical   |   " & warnings.ToString(Inv) & " warning   |   " & (_analysis.Findings.Count - critical - warnings).ToString(Inv) & " information",
            _body, Ink, banner.X + 30, banner.Y + 80, 700)
        Text("SCREENING ONLY", _label, verdictColor, banner.Right - 205, banner.Y + 24, 190)
        Text("No health rating", _small, Muted, banner.Right - 205, banner.Y + 56, 180)
        _y = banner.Bottom + 24

        Dim cardW = (ContentW - 3 * 20) \ 4
        StatCard(Margin, _y, cardW, 128, "PERIOD", EquipmentAnalysis.Span(request.ToUtc - request.FromUtc), request.FromUtc.ToString("MM-dd HH:mm", Inv) & " to " & request.ToUtc.ToString("MM-dd HH:mm", Inv) & " UTC", Blue)
        StatCard(Margin + (cardW + 20), _y, cardW, 128, "SELECTED CHANNELS", _analysis.Scope.Count.ToString(Inv), _analysis.Channels.Count.ToString(Inv) & " numeric with valid data", Blue)
        StatCard(Margin + 2 * (cardW + 20), _y, cardW, 128, "SAMPLES", _analysis.TotalSamples.ToString("N0", Inv), _analysis.MissingSamples.ToString("N0", Inv) & " missing / unknown", Blue)
        StatCard(Margin + 3 * (cardW + 20), _y, cardW, 128, "EST. TIME COVERAGE", EquipmentAnalysis.Num(_analysis.ObservedCoveragePercent) & " %", "saved validity " & EquipmentAnalysis.Num(_analysis.Completeness) & " %", If(_analysis.ObservedCoveragePercent >= 99.0R, Blue, Amber))
        _y += 152

        SectionTitle("Executive summary")
        Dim height = MeasureHeight(_analysis.Summary, _body, ContentW - 44)
        Card(New Rectangle(Margin, _y, ContentW, height + 36), Soft, Line)
        Using brush As New SolidBrush(Ink)
            _g.DrawString(_analysis.Summary, _body, brush, New RectangleF(Margin + 22, _y + 18, ContentW - 44, height + 4))
        End Using
        _pageText.AppendLine(_analysis.Summary)
        _y += height + 62

        SectionTitle("Key findings")
        Dim shown = 0
        For Each finding In _analysis.Findings.Where(Function(item) item.Severity >= 1).
            OrderByDescending(Function(item) item.Severity).
            ThenByDescending(Function(item) Math.Abs(item.DeviationPercent.GetValueOrDefault())).
            GroupBy(Function(item) item.Category & "|" & item.Title).Select(Function(group) group.First()).Take(3)
            Ensure(80)
            DrawFindingRow(finding, False)
            shown += 1
        Next
        If shown = 0 Then
            Ensure(80)
            Card(New Rectangle(Margin, _y, ContentW, 70), Color.FromArgb(18, Green), Green)
            Text("No abnormal condition detected in the valid saved readings; unobserved intervals remain unknown.", _small, Ink, Margin + 24, _y + 22, ContentW - 48)
            _y += 90
        End If
        DrawParagraph("Observed time coverage: " & EquipmentAnalysis.Num(_analysis.ObservedCoveragePercent) &
            " % average across analysed channels. A complete set of saved values does not prove uninterrupted recording.", _small, Muted, Margin, ContentW)
        DrawParagraph(_analysis.CoincidentGroups.Count.ToString(Inv) &
            " coincident extreme-time group(s) (within 1 second); these are navigation groups, not confirmed incidents. " &
            "A shared event may produce flags on several phases and on derived TOTAL. Do not count those as independent failures.",
            _small, Muted, Margin, ContentW)
    End Sub

    Private Sub DrawFindingRow(finding As ReportFinding, includeAdvice As Boolean)
        Dim color = SeverityColor(finding.Severity)
        Dim detail = finding.Detail
        If includeAdvice AndAlso finding.Cause.Length > 0 Then detail &= "  Investigation / unproven hypotheses: " & finding.Cause
        If includeAdvice AndAlso finding.Advice.Length > 0 Then detail &= "  Suggested check: " & finding.Advice
        Dim titleHeight = MeasureHeight(finding.Title, _bold, ContentW - 210)
        Dim textHeight = MeasureHeight(detail, _small, ContentW - 210)
        Dim height = Math.Max(82, textHeight + titleHeight + 34)
        Ensure(height + 12)
        Card(New Rectangle(Margin, _y, ContentW, height), Color.White, Line)
        Using brush As New SolidBrush(color)
            _g.FillRectangle(brush, Margin + 1, _y + 12, 6, height - 24)
        End Using
        Chip(finding.SeverityName, color, Margin + 22, _y + 14)
        Text(finding.Category, _small, Muted, Margin + 22, _y + 48, 150)
        Using brush As New SolidBrush(Ink)
            _g.DrawString(finding.Title, _bold, brush, New RectangleF(Margin + 190, _y + 10, ContentW - 210, titleHeight + 4))
        End Using
        _pageText.AppendLine(finding.Title)
        Using brush As New SolidBrush(Ink)
            _g.DrawString(detail, _small, brush, New RectangleF(Margin + 190, _y + titleHeight + 18, ContentW - 210, textHeight + 4))
        End Using
        _pageText.AppendLine(detail)
        _y += height + 12
    End Sub

    Private Sub DrawScope()
        _sectionContext = "Study scope & equipment capacity"
        NewPage(False)
        SectionTitle("Study scope - channels covered by this report")
        DrawParagraph("Only the following selected channels are assessed. Min/mean/max use finite good-quality returned values; " &
            "unselected parameters are not missing measurements and are not inferred.", _small, Muted, Margin, ContentW)
        Dim rows As New List(Of String())()
        For Each row In _analysis.Scope
            Dim channel = row.Channel
            If channel IsNot Nothing Then
                Dim substantive = channel.Findings.Where(Function(item) item.Category <> "Data quality").Select(Function(item) item.Severity).DefaultIfEmpty(0).Max()
                rows.Add({row.Name, row.Unit, row.Samples.ToString("N0", Inv),
                    EquipmentAnalysis.Num(channel.Points.Min(Function(point) point.V)),
                    EquipmentAnalysis.Num(channel.Points.Average(Function(point) point.V)),
                    EquipmentAnalysis.Num(channel.Points.Max(Function(point) point.V)),
                    If(substantive >= 2, "Critical flags", If(substantive = 1, "Warning flags",
                        If(channel.Severity >= 1, "Recording gaps", "No flags in selected values")))})
            Else
                rows.Add({row.Name, row.Unit, row.Samples.ToString("N0", Inv), "-", "-", "-", row.Note})
            End If
        Next
        DrawTable({"CHANNEL", "UNIT", "RETURNED", "MIN", "MEAN", "MAX", "SCREENING STATUS"},
            {340, 70, 120, 110, 110, 110, 260}, rows)
    End Sub

    Private Sub DrawUpsPowerBalance()
        If _analysis.UpsPowerBalances.Count = 0 Then Return
        _sectionContext = "UPS input/output power, currents and efficiency evidence"
        NewPage(False)
        SectionTitle("UPS input/output power, currents and efficiency evidence")
        DrawParagraph("Each UPS is evaluated separately using exact same-device acquisition pairs, never an input/output current ratio. Pin and Pout are measured active power in kW. Means below use only common valid pairs; exclusions and measurement sources are disclosed. No interpolation or independent-period mean subtraction is used.",
            _small, Muted, Margin, ContentW)
        DrawParagraph("Different models, capacities, voltage references, topology and operating loads are not interchangeable. Higher input current may reflect voltage, PF, load, charging or conversion overhead; it does not by itself prove higher kW consumption or loss. PF is dimensionless, not efficiency; no PF is invented from V x I or a current ratio.",
            _small, Muted, Margin, ContentW)
        Dim rows As New List(Of String())()
        For Each study In _analysis.UpsPowerBalances
            rows.Add({study.EquipmentId,
                If(study.Model.Length > 0, study.Model, "Model not supplied") & vbLf & study.ModelBasis,
                If(study.RatedKva.HasValue, EquipmentAnalysis.Num(study.RatedKva.Value) & " kVA", "Rating unavailable") & vbLf & study.RatingBasis})
        Next
        DrawTable({"UPS ID", "MODEL / EVIDENCE", "RATING / EVIDENCE"}, {180, 470, 470}, rows)
        If _analysis.UpsPowerBalances.Select(Function(study) study.Model).Where(Function(model) model.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1 Then
            DrawParagraph("Selected UPS model metadata differs. These observations must not be treated as identical-model load sharing or a manufacturer efficiency comparison.",
                _bold, Ink, Margin, ContentW)
        End If
        For Each study In _analysis.UpsPowerBalances
            SectionTitle(study.EquipmentId & " - matched electrical evidence")
            DrawParagraph(study.Interpretation, _bold, Ink, Margin, ContentW)
            Dim powerRows As New List(Of String()) From {
                New String() {"Recorded input mean (independent)", PowerText(study.ObservedInputKw), study.InputValidAcquisitions & " valid input acquisitions; NOT used for unpaired subtraction"},
                New String() {"Recorded output mean (independent)", PowerText(study.ObservedOutputKw), study.OutputValidAcquisitions & " valid output acquisitions; retained even when input is missing"},
                New String() {"Mean active input Pin", PowerText(study.InputKw), "Measured input, same-acquisition paired subset only"},
                New String() {"Mean active output Pout", PowerText(study.OutputKw), "Measured output, same-acquisition paired subset only"},
                New String() {"Observed Pin - Pout", PowerText(study.DifferenceKw), "Not proven conversion loss: includes unknown charging, auxiliaries, bypass/mode and instrument uncertainty"},
                New String() {"Apparent AC transfer ratio", If(study.AcTransferPercent.HasValue, EquipmentAnalysis.Num(study.AcTransferPercent.Value) & " %", "Not calculable / unsuitable"),
                    "100 x mean paired Pout / mean paired Pin; NOT certified UPS efficiency or an energy-weighted efficiency"},
                New String() {"Observed AC difference / input", If(study.AcTransferPercent.HasValue, EquipmentAnalysis.Num(100.0R - study.AcTransferPercent.Value) & " %", "Not calculable / unsuitable"),
                    "100 x (mean paired Pin - mean paired Pout) / mean paired Pin; observed AC difference only, not proven loss percentage"},
                New String() {"True efficiency / conversion loss", "Not established", "Needs verified operating mode/topology and battery energy balance/current sign; manufacturer test conditions and meter uncertainty also apply"},
                New String() {"Power pairing coverage", study.PairedAcquisitions & " / " & study.CandidateAcquisitions & " candidates",
                    (study.CandidateAcquisitions - study.PairedAcquisitions) & " excluded; " & study.ReverseOrInconsistentPairs & " nonpositive-input, reverse-power or Pout > Pin pair(s). No clamping to 100 %."}}
            DrawTable({"MEASUREMENT / CALCULATION", "OBSERVED VALUE", "BASIS / LIMITATION"}, {310, 240, 570}, powerRows)
            DrawParagraph(study.PowerBasis, _small, Muted, Margin, ContentW)
            If study.CurrentRows.Count > 0 Then
                DrawTable({"CURRENT QUANTITY", "INPUT MEAN", "OUTPUT MEAN", "PAIRING / DIFFERENCE"},
                    {220, 200, 200, 500}, study.CurrentRows)
            End If
            If study.PfRows.Count > 0 Then
                DrawTable({"PF QUANTITY", "INPUT MEAN", "OUTPUT MEAN", "PAIRING / DIFFERENCE"},
                    {220, 200, 200, 500}, study.PfRows)
            Else
                DrawParagraph("Input/output PF pair unavailable or unselected. Obtain measured system PF or validated same-acquisition active/apparent power at each side; per-phase PF must not be averaged into system PF.",
                    _small, Muted, Margin, ContentW)
            End If
        Next
        DrawParagraph("Cross-equipment comparison is disabled. The values above analyse each UPS's own input/output only; they are not a ranking against other UPS units, transformers, ATS equipment or generators. A downstream/upstream load contribution is not inferred without approved time-valid wiring, actual ATS source and eligible measured powers.",
            _bold, Ink, Margin, ContentW)
    End Sub

    Private Shared Function PowerText(value As Double?) As String
        Return If(value.HasValue, EquipmentAnalysis.Num(value.Value) & " kW", "Not calculable")
    End Function

    Private Sub DrawCapacityComparison()
        Dim groups = _analysis.Scope.Where(Function(row) row.Metric IsNot Nothing AndAlso Not row.Metric.IsEstimate).
            GroupBy(Function(row) If(row.Name.Contains("|"c), row.Name.Substring(0, row.Name.LastIndexOf("|"c)).Trim(),
                _analysis.Result.Request.Equipment.Id)).ToList()
        If groups.Count = 0 Then Return

        SectionTitle("Equipment rated capacity and recorded loading")
        DrawParagraph("Capacity is compared with configured nameplate kVA, or with the UPS card's own load percentage. A recording peak or peer average is not factory demand. TOTAL (T1-T4) is the factory aggregate when it was selected; UPS outputs are reported separately and overlap with that aggregate.",
            _small, Muted, Margin, ContentW)

        Dim rows As New List(Of String())()
        For Each group In groups
            Dim members = group.ToList()
            Dim metric = members.Select(Function(row) row.Metric).First()
            Dim role = If(group.Key.Equals("TOTAL", StringComparison.OrdinalIgnoreCase), "Factory total",
                If(group.Key.StartsWith("UPS", StringComparison.OrdinalIgnoreCase), "UPS output", "Transformer"))
            Dim apparent = members.Where(Function(row) row.Unit = "kVA" AndAlso row.Channel IsNot Nothing AndAlso
                Not ElectricalReportKnowledge.IsDc(row.Metric) AndAlso Not {"Input", "Bypass"}.Contains(ElectricalReportKnowledge.MeasurementSide(row.Metric))).
                OrderByDescending(Function(row) row.Channel.Points.Max(Function(point) point.V)).FirstOrDefault()
            Dim upsLoad = members.Where(Function(row) row.Unit = "%" AndAlso row.Name.IndexOf("Output Load", StringComparison.OrdinalIgnoreCase) >= 0 AndAlso row.Channel IsNot Nothing).
                OrderByDescending(Function(row) row.Channel.Points.Max(Function(point) point.V)).FirstOrDefault()
            Dim basisMetric = If(apparent IsNot Nothing, apparent.Metric, If(upsLoad IsNot Nothing, upsLoad.Metric, metric))

            Dim utilization = "Not calculable"
            If apparent IsNot Nothing AndAlso metric.RatedCapacityKva.HasValue AndAlso metric.RatedCapacityKva.Value > 0 Then
                Dim percent = apparent.Channel.Points.Max(Function(point) point.V) / metric.RatedCapacityKva.Value * 100.0R
                Dim scalingUnverified = apparent.Metric.Basis.IndexOf("scaling must be verified", StringComparison.OrdinalIgnoreCase) >= 0
                utilization = EquipmentAnalysis.Num(percent) & " %" & If(scalingUnverified, " (indicative)", "")
            ElseIf upsLoad IsNot Nothing Then
                utilization = EquipmentAnalysis.Num(upsLoad.Channel.Points.Max(Function(point) point.V)) & " % (UPS card)"
            ElseIf metric.RatedCapacityKva.HasValue Then
                utilization = "No kVA/load % data"
            End If
            Dim basis = If(metric.CapacityBasis.Length > 0, metric.CapacityBasis, "Rating metadata unavailable in the loaded study; this does not prove no rating is configured")
            If basisMetric.Basis.Length > 0 Then basis &= "; " & basisMetric.Basis
            rows.Add({group.Key & vbLf & role,
                If(metric.RatedCapacityKva.HasValue AndAlso Double.IsFinite(metric.RatedCapacityKva.Value),
                    EquipmentAnalysis.Num(metric.RatedCapacityKva.Value) & " kVA", "Not available"),
                If(apparent Is Nothing, "Not selected / no valid kVA", EquipmentAnalysis.Num(apparent.Channel.Points.Max(Function(point) point.V)) & " kVA"),
                utilization, basis})
        Next
        DrawTable({"EQUIPMENT / ROLE", "RATED CAPACITY", "RECORDED PEAK", "PEAK / RATING", "SOURCE & LIMITATIONS"},
            {170, 155, 175, 190, 430}, rows)
    End Sub

    Private Sub DrawDataAssurance()
        _sectionContext = "Data assurance - what was and was not observed"
        NewPage(False)
        SectionTitle("Data assurance - what was and was not observed")
        DrawParagraph("This report covers only the channels listed in its scope. It is not a full equipment audit. " &
            "Counts refer to returned channel observations, not acquisition cycles. No absent values are replaced by zero. " &
            "Coverage is a cadence-based estimate, not verified uptime or proof that every original sample was returned.",
            _body, Ink, Margin, ContentW)
        DrawParagraph("Loaded resolution: " & If(_analysis.Result.Request.ThinSeconds > 0,
            _analysis.Result.Request.ThinSeconds.ToString(Inv) & " s retained-sample selection. True extrema and events between retained samples may be missing.",
            "no requested time thinning; selected saved observations only.") &
            " Missing edges and long gaps remain unknown. Alarm evidence: " &
            If(_analysis.Events.Count = 0, "none returned; no alarm-based fault attribution is possible.", _analysis.Events.Count.ToString(Inv) & " condition group(s), reported separately."),
            _small, Muted, Margin, ContentW)
        Dim rows As New List(Of String())()
        For Each row In _analysis.Scope.Where(Function(item) item.Kind = "Analog")
            Dim channel = row.Channel
            rows.Add({row.Name, If(channel Is Nothing, "0", channel.Points.Count.ToString("N0", Inv)) & " / " & row.Samples.ToString("N0", Inv),
                If(channel Is Nothing, "No valid saved reading",
                    EquipmentAnalysis.Stamp(channel.Points(0).T) & vbLf & EquipmentAnalysis.Stamp(channel.Points.Last().T)),
                EquipmentAnalysis.Num(If(channel Is Nothing, 0, channel.ObservedCoveragePercent)) & " %",
                If(row.Metric.Basis.Length > 0, row.Metric.Basis, "Measurement provenance not supplied; verify sensor/register and scaling") &
                    If(channel Is Nothing AndAlso row.Note.Length > 0, " Recorded availability: " & row.Note, "")})
        Next
        DrawTable({"SELECTED CHANNEL", "VALID / RETURNED", "FIRST / LAST VALID UTC", "EST. COVERAGE", "MEASUREMENT BASIS / LIMITS"},
            {260, 130, 235, 120, 375}, rows)
        DrawParagraph("Interpretation order: verify source identity, register scaling, quality and recorder continuity; " &
            "then compare corroborating channels and alarms. Statistical voltage deviations are relative to the period median, not a nominal-voltage compliance test. " &
            "Zero readings are retained evidence, not independently confirmed outages. Generic temperature guides do not identify sensor location or equipment limits.",
            _small, Muted, Margin, ContentW)
    End Sub

    Private Sub DrawSelectedComparison()
        Dim study = _analysis.Comparison
        _sectionContext = "Selected X/Y comparison"
        NewPage(False)
        SectionTitle("Selected X/Y comparison")
        DrawTable({"COMPARISON SETTING", "EXPORTED EVIDENCE"}, {260, 860}, New String()() {
            New String() {"Equipment / axes", _analysis.StudyName & vbLf & "Y: " & study.Y.ToString() & vbLf & "X: " & study.X.ToString()},
            New String() {"View", study.ModeName & "; full loaded period (interactive zoom is not exported)"},
            New String() {"Exact acquisition pairing", study.Pairs.Length.ToString("N0", Inv) & " matched saved acquisitions; " &
                study.ExcludedAcquisitions.ToString("N0", Inv) & " excluded candidate acquisition groups (missing, invalid or ambiguous pair). " &
                "Equipment, saved timestamp, observation identity, acquired timestamp and mapping revision must match. No nearest-time matching."}})
        DrawParagraph("Association is not causation. Temperature may lag load; no lag shift or physical model is fitted. " &
            "TDEGC sensor source is unverified: do not assume winding/hot-spot temperature. Mean/range statistics are sample-weighted, not confidence limits.",
            _small, Muted, Margin, ContentW)
        If study.Pairs.Length = 0 Then
            DrawParagraph("No valid exact acquisition pairs exist in the loaded study. No comparison curve, correlation or trend conclusion can be supported.",
                _bold, Amber, Margin, ContentW)
            Return
        End If
        Ensure(570)
        Using chart As New AnalyticsTrendChart With {
            .Size = New Size(700, 350), .NumericXAxis = True, .ComparisonStyle = study.Style, .ShowInteractionHints = False,
            .ChartType = AnalyticsChartType.Scatter, .ChartTitle = study.Y.Name & " vs " & study.X.Name,
            .XAxisLabel = study.X.ToString(), .YAxisLabel = study.Y.ToString(),
            .WindowStartUtc = _analysis.Result.Request.FromUtc, .WindowEndUtc = _analysis.Result.Request.ToUtc}
            chart.Series = {New AnalyticsTrendSeries With {.Name = study.Y.Name & " vs " & study.X.Name,
                .Color = Blue, .Points = study.Pairs, .ComparisonBins = ChannelComparisonBin.Build(study.Pairs),
                .ExpectedIntervalSeconds = Math.Max(_analysis.Result.Request.Equipment.IntervalSeconds, _analysis.Result.Request.ThinSeconds)}}
            If Not _dryRun Then
                Using bitmap As New Bitmap(chart.Width, chart.Height)
                    chart.DrawToBitmap(bitmap, chart.ClientRectangle)
                    _g.DrawImage(bitmap, New Rectangle(Margin, _y, ContentW, 560))
                End Using
            End If
        End Using
        _y += 580
        DrawParagraph(If(study.Style = ChannelComparisonStyle.MeanResponse,
            "Green means join adjacent supported X groups only. Bars are observed Y min-max; n is the sample count. Groups with n < 3 are grey and unconnected.",
            If(study.Style = ChannelComparisonStyle.TimePath,
                "The path follows acquisition time, blue to orange, not increasing X. Missing/invalid pairs, nonpositive time differences and gaps over 2.5 effective intervals break connections.",
                "Each point represents one matched saved acquisition. Coincident points may overlap; this is not a fitted response.")),
            _small, Muted, Margin, ContentW)
        Dim bins = ChannelComparisonBin.Build(study.Pairs)
        Ensure(250 + bins.Length * 70)
        SectionTitle("Comparison response - numeric evidence")
        DrawParagraph("Up to 12 distinct X levels are grouped exactly; otherwise 12 equal-width X bins are used. " &
            "All matched pairs contribute. No bin is silently filled and no extrapolation is performed.", _small, Muted, Margin, ContentW)
        DrawTable({"X RANGE (" & study.X.Unit & ")", "MEAN X", "MEAN Y (" & study.Y.Unit & ")", "OBSERVED Y MIN / MAX", "n / SUPPORT"},
            {250, 160, 220, 290, 200}, bins.Select(Function(bin) New String() {
                EquipmentAnalysis.Num(bin.MinimumX) & " to " & EquipmentAnalysis.Num(bin.MaximumX),
                EquipmentAnalysis.Num(bin.MeanX), EquipmentAnalysis.Num(bin.MeanY),
                EquipmentAnalysis.Num(bin.MinimumY) & " / " & EquipmentAnalysis.Num(bin.MaximumY),
                bin.Count.ToString("N0", Inv) & If(bin.Count < 3, " / sparse", " / supported")}))
        Dim first = study.Pairs.First().TimestampUtc
        Dim last = study.Pairs.Last().TimestampUtc
        DrawParagraph("Paired evidence: " & EquipmentAnalysis.Stamp(first) & " to " & EquipmentAnalysis.Stamp(last) & ". " &
            study.Pairs.Select(Function(point) point.XValue.Value).Distinct().Count().ToString(Inv) & " distinct X levels; " &
            bins.Count(Function(bin) bin.Count < 3).ToString(Inv) & " sparse response group(s). " &
            "These descriptive means do not establish linearity, causation or a thermal rating.", _small, Muted, Margin, ContentW)
        Dim supported = bins.Where(Function(bin) bin.Count >= 3).ToArray()
        If supported.Length >= 2 Then
            Dim low = supported.OrderBy(Function(bin) bin.MeanY).First()
            Dim high = supported.OrderByDescending(Function(bin) bin.MeanY).First()
            DrawParagraph("Descriptive response: among supported groups, the lowest mean " & study.Y.Name & " was " &
                EquipmentAnalysis.Num(low.MeanY) & " " & study.Y.Unit & " at mean X " & EquipmentAnalysis.Num(low.MeanX) & " " & study.X.Unit &
                " (n=" & low.Count.ToString(Inv) & "); the highest was " & EquipmentAnalysis.Num(high.MeanY) & " " & study.Y.Unit &
                " at mean X " & EquipmentAnalysis.Num(high.MeanX) & " " & study.X.Unit & " (n=" & high.Count.ToString(Inv) &
                "). Unequal group counts, quantized sensors and delayed response can affect this association. This is not a monotonic-trend or causal claim.",
                _body, Ink, Margin, ContentW)
        Else
            DrawParagraph("Insufficient supported X groups for a response-shape conclusion. Keep the raw pairs and time traces; do not infer a smooth relationship from sparse levels.",
                _small, Amber, Margin, ContentW)
        End If
        If Not study.IncludeTimeTraces Then Return
        _sectionContext = "Aligned time traces - exact paired readings"
        NewPage(False)
        SectionTitle("Aligned time traces - exact paired readings")
        DrawParagraph("Same acquisition pairs as the X/Y plot, full shared UTC range and independent native Y scales. " &
            "Inspect timing and possible lag visually; no lag correction or interpolation is applied.", _small, Muted, Margin, ContentW)
        For Each metric In {study.Y, study.X}
            SectionTitle(metric.ToString())
            Dim useX = metric Is study.X
            Dim points = study.Pairs.Select(Function(point) New ReportPoint(point.TimestampUtc,
                If(useX, point.XValue.Value, point.Value.Value))).ToList()
            Dim breaks = study.Pairs.Skip(1).Where(Function(point) Not point.ConnectFromPrevious).
                Select(Function(point) point.TimestampUtc).ToHashSet()
            DrawChart(Margin, _y, ContentW, 420, New List(Of ChartSeries) From {
                New ChartSeries With {.Name = metric.Name, .Color = If(useX, Amber, Blue), .Points = points,
                    .GapSeconds = Math.Max(_analysis.Result.Request.Equipment.IntervalSeconds, _analysis.Result.Request.ThinSeconds) * 2.5R,
                    .BreakBefore = breaks}}, metric.Unit, New List(Of ReportFinding)(), Nothing, first, If(last > first, last, first.AddSeconds(1)))
            _y += 450
        Next
    End Sub

    Private Sub DrawComparisonPermissions()
        Dim groups = _analysis.Result.Request.Metrics.GroupBy(Function(metric) ElectricalReportKnowledge.EquipmentIdentity(metric, _analysis.Result.Request.Equipment.Id)).ToList()
        If groups.Count = 0 Then Return
        SectionTitle("Electrical relationship - comparison permissions")
        DrawParagraph("Every equipment is analysed independently. Peer ranking, cross-equipment overlay, current/voltage comparison and causal grouping between unrelated devices are disabled. Matching units, model or timestamps do not establish a feeder relationship. Transformer / UPS / generator / ATS quantities are not interchangeable.",
            _small, Muted, Margin, ContentW)
        Dim rows As New List(Of String())()
        For Each group In groups
            Dim metric = group.First()
            Dim source = If(metric.AtsBankNumber.HasValue, "Configured ATS " & metric.AtsBankNumber.Value, "ATS/feed mapping unavailable")
            If metric.ConfiguredSourceDescription.Length > 0 Then source &= vbLf & metric.ConfiguredSourceDescription
            rows.Add({group.Key, source,
                If(metric.TopologyBasis.Length > 0, metric.TopologyBasis, "No approved time-valid feeder relationship or active source evidence supplied"),
                "Not calculable: active supplying source and eligible downstream INPUT/upstream power pairs are required"})
        Next
        DrawTable({"EQUIPMENT", "CONFIGURED FEED (NOT ACTIVE SOURCE)", "TOPOLOGY EVIDENCE", "UPSTREAM LOAD CONTRIBUTION"}, {180, 280, 310, 350}, rows)
        DrawParagraph("If an approved mapping establishes UPS -> ATS -> actual transformer/generator for the measured period, only that verified load contribution may be assessed using like-for-like measured powers and aligned timestamps. UPS output omits its charging/loss demand, so it cannot substitute for UPS input at the feeder. Different feeds, unknown ATS position or missing input power prohibit a percentage. Nameplate utilisation of each device is separate, not a share of upstream demand.",
            _small, Muted, Margin, ContentW)
    End Sub

    Private Shared Function EquipmentPrefix(metricName As String) As String
        Dim separator = metricName.LastIndexOf("|"c)
        Return If(separator < 0, metricName.Trim(), metricName.Substring(0, separator).Trim())
    End Function

    Private Sub DrawConclusion()
        Dim findings = _analysis.Findings.Where(Function(item) item.Severity >= 1 AndAlso item.Category <> "Correlation").
            OrderByDescending(Function(item) item.Severity).ThenBy(Function(item) If(item.StartUtc.HasValue, item.StartUtc.Value, DateTime.MaxValue)).ToList()
        Dim critical = _analysis.Findings.Where(Function(item) item.Severity >= 2).Count()
        Dim warnings = _analysis.Findings.Where(Function(item) item.Severity = 1).Count()
        Dim request = _analysis.Result.Request
        Dim color = SeverityColor(_analysis.Severity)
        Ensure(300)
        SectionTitle("Conclusion", color)
        Dim banner As New Rectangle(Margin, _y, ContentW, 86)
        Card(banner, Color.FromArgb(18, color), color)
        Using brush As New SolidBrush(color)
            _g.FillRectangle(brush, banner.X, banner.Y + 14, 10, banner.Height - 28)
        End Using
        Text(_analysis.Verdict, _h2, color, banner.X + 30, banner.Y + 10, ContentW - 60)
        Text(critical.ToString(Inv) & " critical flags   |   " & warnings.ToString(Inv) & " warning flags   |   " &
            EquipmentAnalysis.Num(_analysis.ObservedCoveragePercent) & " % estimated time coverage; not a health rating",
            _small, Ink, banner.X + 30, banner.Y + 48, ContentW - 60)
        _y += banner.Height + 22

        Dim points As New List(Of String)()
        points.Add("Scope: " & _analysis.Channels.Count.ToString(Inv) & " selected numeric channel(s) of " & _analysis.StudyName & " were studied over " & EquipmentAnalysis.Span(request.ToUtc - request.FromUtc) &
            " (" & EquipmentAnalysis.Stamp(request.FromUtc) & " to " & EquipmentAnalysis.Stamp(request.ToUtc) & ").")
        If findings.Count = 0 Then
            points.Add("Result: no abnormality was found in the selected valid observations. Unselected parameters, unrecorded periods and physical equipment condition were not assessed.")
        Else
            Dim affected = _analysis.Channels.Where(Function(item) item.Findings.Any(Function(finding) finding.Severity >= 1 AndAlso finding.Category <> "Data quality")).
                Select(Function(item) item.Metric.Name).ToList()
            If affected.Count > 0 Then
                points.Add("Channels with abnormal behaviour (" & affected.Count.ToString(Inv) & "): " & String.Join(", ", affected.Take(8)) & If(affected.Count > 8, " and " & (affected.Count - 8).ToString(Inv) & " more", "") & ".")
            End If
            Dim worst = findings(0)
            points.Add("Most important event: " & worst.Title & " on " & worst.Metric & If(worst.StartUtc.HasValue, " at " & EquipmentAnalysis.Stamp(worst.StartUtc.Value), "") & ". " & worst.Detail)
        End If
        For Each hint In _analysis.Findings.Where(Function(item) item.Category = "Correlation").Take(2)
            points.Add("Correlation to investigate, not a confirmed cause: " & hint.Detail)
        Next
        For Each study In _analysis.UpsPowerBalances
            points.Add(study.Interpretation)
        Next
        If _analysis.Events.Count > 0 Then
            points.Add("Alarm and fault entries: " & _analysis.Events.Sum(Function(item) item.Count).ToString(Inv) & " saved observation(s) of " & _analysis.Events.Count.ToString(Inv) & " different condition(s); reminders are not separate incidents.")
        End If
        If _analysis.Completeness < 99.0R Then
            points.Add("Data quality: only " & EquipmentAnalysis.Num(_analysis.Completeness) & " % of the saved samples were valid, so conclusions about the missing intervals are not possible.")
        End If
        points.Add("Study limitation: observed time coverage averages " & EquipmentAnalysis.Num(_analysis.ObservedCoveragePercent) &
            " %. Validity counts saved samples only; no conclusion is made about gaps or the unobserved edges of the requested period. A zero voltage is a saved reading, not proof by itself of a physical outage.")
        For Each item In points
            Dim height = MeasureHeight(item, _body, ContentW - 40)
            Ensure(height + 18)
            Using brush As New SolidBrush(color)
                _g.FillEllipse(brush, Margin + 6, _y + 9, 10, 10)
            End Using
            Using brush As New SolidBrush(Ink)
                _g.DrawString(item, _body, brush, New RectangleF(Margin + 30, _y, ContentW - 40, height + 4))
            End Using
            _pageText.AppendLine(item)
            _y += height + 12
        Next
        Dim actions = findings.Where(Function(item) item.Advice.Length > 0).Select(Function(item) item.Advice).Distinct().Take(3).ToList()
        If actions.Count > 0 Then
            Ensure(80)
            Text("PRIORITY ACTIONS", _label, Muted, Margin + 6, _y + 4, 400)
            _y += 34
            For index = 0 To actions.Count - 1
                Dim height = MeasureHeight(actions(index), _body, ContentW - 60)
                Ensure(height + 18)
                Text((index + 1).ToString(Inv) & ".", _bold, color, Margin + 6, _y, 40)
                Using brush As New SolidBrush(Ink)
                    _g.DrawString(actions(index), _body, brush, New RectangleF(Margin + 50, _y, ContentW - 60, height + 4))
                End Using
                _pageText.AppendLine(actions(index))
                _y += height + 12
            Next
        End If
        _y += 16
    End Sub

    Private Sub DrawRootCause()
        Dim hints = _analysis.Findings.Where(Function(item) item.Category = "Correlation").ToList()
        If hints.Count = 0 Then Return
        SectionTitle("Observed associations - hypotheses to verify", Blue)
        For Each hint In hints
            DrawFindingRow(hint, True)
        Next
    End Sub

    Private Sub DrawChronology()
        Dim items = _analysis.Findings.Where(Function(item) item.Severity >= 1 AndAlso item.StartUtc.HasValue).OrderBy(Function(item) item.StartUtc.Value).ToList()
        If items.Count = 0 Then Return
        _sectionContext = "Event evidence register"
        NewPage(False)
        SectionTitle("Event evidence register - chronology & measurements")
        DrawParagraph("All " & items.Count.ToString(Inv) & " timed warning/critical findings are listed below. Times are UTC. " &
            "Start/end are the first/last observed abnormal samples, not verified device onset or continuous duration. " &
            "Reference means the stated statistical baseline or guide, not necessarily a configured equipment limit.", _small, Muted, Margin, ContentW)
        Dim groups = _analysis.CoincidentGroups.Where(Function(group) group.Length > 1).ToArray()
        If groups.Length > 0 Then
            DrawTable({"COINCIDENT EXTREMES UTC", "FLAGS", "RELATED CHANNEL / RULE EVIDENCE"},
                {230, 90, 800}, groups.Select(Function(group) New String() {
                    EquipmentAnalysis.Stamp(group(0).PeakUtc.Value), group.Length.ToString(Inv),
                    String.Join("; ", group.Select(Function(item) item.Metric & ": " & item.Title))}))
            DrawParagraph("Grouped by extreme timestamp within 1 second of each group's first extreme, within the same equipment only. " &
                "Timing alone does not establish a common physical incident. The complete individual evidence follows.",
                _small, Muted, Margin, ContentW)
        End If
        Dim rows As New List(Of String())()
        For Each item In items
            Dim unit = If(item.EvidenceUnit.Length > 0, " " & item.EvidenceUnit, "")
            Dim measured = If(item.EvidenceValue.HasValue, EquipmentAnalysis.Num(item.EvidenceValue.GetValueOrDefault()) & unit, "See finding detail")
            If item.PeakUtc.HasValue Then measured &= vbLf & item.PeakUtc.Value.ToString("MM-dd HH:mm:ss.FFFFFFF", Inv)
            Dim interval = item.StartUtc.Value.ToString("yyyy-MM-dd", Inv) & vbLf & item.StartUtc.Value.ToString("HH:mm:ss.FFFFFFF", Inv)
            If item.EndUtc.HasValue AndAlso item.EndUtc.Value > item.StartUtc.Value Then interval &= vbLf & "to " & item.EndUtc.Value.ToString("MM-dd HH:mm:ss.FFFFFFF", Inv)
            rows.Add({interval, item.Metric, item.SeverityName & vbLf & item.Title, measured,
                If(item.ReferenceValue.HasValue, EquipmentAnalysis.Num(item.ReferenceValue.GetValueOrDefault()) & unit & vbLf & item.EvidenceLabel, "Not applicable"),
                If(item.DeviationPercent.HasValue, If(item.DeviationPercent.GetValueOrDefault() >= 0, "+", "") &
                    EquipmentAnalysis.Num(item.DeviationPercent.GetValueOrDefault()) & " %", "Not applicable")})
        Next
        DrawTable({"OBSERVED UTC", "EQUIPMENT / CHANNEL", "EVENT / PRIORITY", "MEASURED / PEAK UTC", "REFERENCE / BASIS", "CHANGE VS REF."},
            {190, 305, 155, 165, 155, 150}, rows)
    End Sub

    Private Function BuildSeries(channels As List(Of ChannelAnalysis)) As List(Of ChartSeries)
        Return channels.Select(Function(channel, index) New ChartSeries With {.Name = channel.Metric.Name, .Color = SeriesColors(index Mod SeriesColors.Length),
            .Points = channel.Points, .BreakBefore = channel.BreakBefore, .GapSeconds = Math.Max(5.0R, channel.MedianStepSeconds * 3.0R)}).ToList()
    End Function

    Private Sub DrawComparison(channels As List(Of ChannelAnalysis), unit As String, quantity As String)
        Ensure(690)
        SectionTitle(ElectricalReportKnowledge.EquipmentIdentity(channels(0).Metric, _analysis.Result.Request.Equipment.Id) & " channels - " & quantity & " (" & unit & ") - " & channels.Count.ToString(Inv) & " selected channels")
        DrawParagraph("Observed range: " & EquipmentAnalysis.Stamp(channels.Min(Function(channel) channel.Points(0).T)) & " to " &
            EquipmentAnalysis.Stamp(channels.Max(Function(channel) channel.Points(channel.Points.Count - 1).T)) &
            ". Shading marks findings; blank intervals are not interpolated.", _small, Muted, Margin, ContentW)
        Dim bands = channels.SelectMany(Function(channel) channel.Findings).Where(Function(item) item.Severity >= 1 AndAlso item.StartUtc.HasValue AndAlso item.Category <> "Data quality").ToList()
        DrawChart(Margin, _y, ContentW, 430, BuildSeries(channels), unit, bands, Nothing)
        _y += 450
        Dim finding = _analysis.Findings.FirstOrDefault(Function(item) item.Category <> "Alarm events" AndAlso item.Title.Contains("Voltage balance") AndAlso
            channels.Any(Function(channel) item.Metric.Contains(channel.Metric.Name)))
        If finding Is Nothing Then finding = _analysis.Findings.FirstOrDefault(Function(item) item.Title.Contains("Current balance") AndAlso channels.Any(Function(channel) item.Metric.Contains(channel.Metric.Name)))
        If finding IsNot Nothing Then DrawFindingRow(finding, True)
    End Sub

    Private NotInheritable Class TimeWindow
        Public Property FromUtc As DateTime
        Public Property ToUtc As DateTime
        Public Property Severity As Integer
        Public ReadOnly Titles As New List(Of String)()
    End Class

    ' Zooms supplement the full-period overview; every finding remains in the evidence tables.
    Private Shared Function ProblemWindows(channel As ChannelAnalysis, first As DateTime, last As DateTime, ByRef omitted As Integer) As List(Of TimeWindow)
        Dim total = Math.Max(1.0R, (last - first).TotalSeconds)
        Dim raw As New List(Of TimeWindow)()
        For Each finding In channel.Findings.Where(Function(item) item.StartUtc.HasValue AndAlso item.Severity >= 1).OrderBy(Function(item) item.StartUtc.Value)
            Dim s = finding.StartUtc.Value
            Dim e = If(finding.EndUtc.HasValue AndAlso finding.EndUtc.Value >= s, finding.EndUtc.Value, s)
            Dim pad = Math.Max(15.0R, Math.Min(600.0R, Math.Max((e - s).TotalSeconds * 0.2R, channel.MedianStepSeconds * 3.0R)))
            Dim window As New TimeWindow With {.FromUtc = If(s.AddSeconds(-pad) < first, first, s.AddSeconds(-pad)), .ToUtc = If(e.AddSeconds(pad) > last, last, e.AddSeconds(pad)),
                .Severity = finding.Severity}
            window.Titles.Add(finding.Title)
            If raw.Count > 0 AndAlso window.FromUtc <= raw(raw.Count - 1).ToUtc Then
                Dim current = raw(raw.Count - 1)
                If window.ToUtc > current.ToUtc Then current.ToUtc = window.ToUtc
                current.Severity = Math.Max(current.Severity, window.Severity)
                current.Titles.AddRange(window.Titles)
            Else
                raw.Add(window)
            End If
        Next
        omitted = Math.Max(0, raw.Count - 2)
        If raw.Count <= 2 Then Return raw
        Dim ranked = raw.OrderByDescending(Function(item) item.Severity).ThenByDescending(Function(item) (item.ToUtc - item.FromUtc).TotalSeconds).ToList()
        Dim selected As New List(Of TimeWindow)()
        Dim represented As New HashSet(Of String)(StringComparer.Ordinal)
        For Each window In ranked
            If Not window.Titles.Any(Function(title) Not represented.Contains(title)) Then Continue For
            selected.Add(window)
            represented.UnionWith(window.Titles)
            If selected.Count = 2 Then Exit For
        Next
        For Each window In ranked
            If selected.Count = 2 Then Exit For
            If Not selected.Contains(window) Then selected.Add(window)
        Next
        Return selected.OrderBy(Function(item) item.FromUtc).ToList()
    End Function

    Private Sub DrawChannelCharts(channel As ChannelAnalysis, bands As List(Of ReportFinding))
        Dim first = channel.Points(0).T
        Dim last = channel.Points(channel.Points.Count - 1).T
        Dim total = (last - first).TotalSeconds
        Dim unit = channel.Metric.Unit
        Dim series = BuildSeries(New List(Of ChannelAnalysis) From {channel})
        DrawChartBlock("Full selected period - " & EquipmentAnalysis.Stamp(_analysis.Result.Request.FromUtc) &
            " to " & EquipmentAnalysis.Stamp(_analysis.Result.Request.ToUtc),
            _analysis.Result.Request.FromUtc, _analysis.Result.Request.ToUtc, series, unit, bands, channel, Blue, 280)
        DrawParagraph("Points are retained measurements; connecting lines are visual guides, not measured behaviour between samples. " &
            "Blank spans/edges are unknown. Shading marks observed abnormal sample bounds, not a confirmed continuous incident.",
            _small, Muted, Margin, ContentW)
        Dim omitted = 0
        Dim windows = If(total >= 1.0R, ProblemWindows(channel, first, last, omitted), New List(Of TimeWindow)())
        If windows.Count > 0 Then
            For index = 0 To windows.Count - 1
                Dim window = windows(index)
                Dim caption = "Problem window " & (index + 1).ToString(Inv) & " of " & windows.Count.ToString(Inv) & " - " &
                    String.Join(", ", window.Titles.Distinct().Take(3)) & vbLf &
                    EquipmentAnalysis.Stamp(window.FromUtc) & " to " & EquipmentAnalysis.Stamp(window.ToUtc)
                DrawChartBlock(caption, window.FromUtc, window.ToUtc, series, unit, bands, channel, SeverityColor(window.Severity), 240)
            Next
            If omitted > 0 Then
                DrawParagraph(omitted.ToString(Inv) & " additional zoom window(s) not drawn; every retained finding is included in the numeric evidence tables.",
                    _small, Muted, Margin, ContentW)
            End If
            Return
        End If
    End Sub

    Private Sub DrawChartBlock(caption As String, winStart As DateTime, winEnd As DateTime, series As List(Of ChartSeries), unit As String, bands As List(Of ReportFinding),
                               channel As ChannelAnalysis, accent As Color, Optional chartHeight As Integer = 320)
        Dim captionHeight = MeasureHeight(caption, _bold, ContentW - 30)
        Ensure(captionHeight + chartHeight + 30)
        Using brush As New SolidBrush(accent)
            _g.FillEllipse(brush, Margin + 2, _y + 8, 14, 14)
        End Using
        Using brush As New SolidBrush(Ink)
            _g.DrawString(caption, _bold, brush, New RectangleF(Margin + 26, _y, ContentW - 30, captionHeight + 4))
        End Using
        _pageText.AppendLine(caption)
        _y += captionHeight + 10
        DrawChart(Margin, _y, ContentW, chartHeight, series, unit, bands, channel, winStart, winEnd)
        _y += chartHeight + 20
    End Sub

    Private Sub DrawChannel(channel As ChannelAnalysis)
        _sectionContext = channel.Metric.Name & " - recorded evidence"
        Ensure(650)
        Dim unit = channel.Metric.Unit
        Dim stats = channel.Stats
        SectionTitle(channel.Metric.Name & If(unit.Length > 0, "  (" & unit & ")", ""))
        Dim cardW = (ContentW - 4 * 16) \ 5
        Dim low = channel.Points.Min(Function(point) point.V)
        Dim high = channel.Points.Max(Function(point) point.V)
        Dim mean = channel.Points.Average(Function(point) point.V)
        StatCard(Margin, _y, cardW, 104, "MINIMUM", EquipmentAnalysis.Num(low), channel.LowAt.ToString("MM-dd HH:mm:ss.FFFFFFF", Inv) & " UTC", Blue)
        StatCard(Margin + (cardW + 16), _y, cardW, 104, "AVERAGE", EquipmentAnalysis.Num(mean), "std dev " & EquipmentAnalysis.Num(channel.StdDev), Blue)
        StatCard(Margin + 2 * (cardW + 16), _y, cardW, 104, "PEAK", EquipmentAnalysis.Num(high), channel.PeakAt.ToString("MM-dd HH:mm:ss.FFFFFFF", Inv) & " UTC", Red)
        StatCard(Margin + 3 * (cardW + 16), _y, cardW, 104, "LAST VALUE", EquipmentAnalysis.Num(channel.LastValue), channel.Points.Last().T.ToString("MM-dd HH:mm:ss.FFFFFFF", Inv) & " UTC", Blue)
        StatCard(Margin + 4 * (cardW + 16), _y, cardW, 104,
            If(channel.Metric.IsPower AndAlso stats IsNot Nothing AndAlso stats.EnergyKwh.HasValue, "ENERGY (CALC.)", "VALID SAMPLES"),
            If(channel.Metric.IsPower AndAlso stats IsNot Nothing AndAlso stats.EnergyKwh.HasValue, EquipmentAnalysis.Num(stats.EnergyKwh.Value) & " kWh", channel.Points.Count.ToString("N0", Inv)),
            If(stats IsNot Nothing, stats.MissingCount.ToString(Inv) & " missing", ""), If(channel.Gaps.Count > 0, Amber, Green))
        _y += 124
        Dim bands = channel.Findings.Where(Function(item) item.StartUtc.HasValue AndAlso item.Severity >= 1).ToList()
        DrawChannelCharts(channel, bands)
        Dim sentence = channel.Summary
        _y += 0
        Dim height = DrawParagraph(sentence, _body, Ink, Margin, ContentW)
        If channel.Findings.Count = 0 Then
            Dim color = Green
            Ensure(60)
            Card(New Rectangle(Margin, _y, ContentW, 52), Color.FromArgb(18, color), color)
            Text("No abnormal behaviour detected in this channel's valid saved readings.", _body, Ink, Margin + 22, _y + 12, ContentW - 44)
            _y += 66
        Else
            DrawTable({"OBSERVED UTC", "RULE / PRIORITY", "MEASURED", "EVIDENCE / INTERPRETATION"}, {190, 220, 160, 550},
                channel.Findings.Select(Function(finding) New String() {
                    If(finding.StartUtc.HasValue, EquipmentAnalysis.Stamp(finding.StartUtc.GetValueOrDefault()), "Not timed"),
                    finding.Title & vbLf & finding.SeverityName,
                    If(finding.EvidenceValue.HasValue, EquipmentAnalysis.Num(finding.EvidenceValue.GetValueOrDefault()) & " " & finding.EvidenceUnit, "-"),
                    finding.Detail}))
        End If
        _y += 18
    End Sub

    Private Sub DrawEventTimeline()
        SectionTitle("Alarm and fault evidence - all selected conditions", Red)
        DrawParagraph("Grouped by equipment and stable condition key. Identical event projections are counted once. " &
            "Active, cleared, inactive and unknown observations remain distinct; reminders are not incidents. " &
            "This is a condition summary, not the complete event ledger; export CSV/Excel for individual saved event details.",
            _small, Muted, Margin, ContentW)
        DrawTable({"EQUIPMENT / CONDITION", "OBSERVATIONS / ACTIVE", "FIRST / LAST UTC", "LAST OBSERVED STATE", "PRIORITY"},
            {340, 170, 260, 200, 150}, _analysis.Events.Select(Function(item) New String() {
                item.Name, item.Count.ToString("N0", Inv) & " / " & item.ActiveCount.ToString("N0", Inv),
                EquipmentAnalysis.Stamp(item.FirstUtc) & vbLf & EquipmentAnalysis.Stamp(item.LastUtc),
                If(item.LastState.Length > 0, item.LastState, "Unknown"), If(item.Severity >= 2, "Critical active evidence",
                    If(item.Severity = 1, "Warning active evidence", "No active state observed"))}))
    End Sub

    Private Sub DrawAdviceAndMethod()
        _sectionContext = "Conclusions & priority actions"
        NewPage(False)
        Dim advice = _analysis.Findings.Where(Function(item) item.Severity >= 1 AndAlso item.Advice.Length > 0).
            GroupBy(Function(item) item.Advice).Select(Function(group) New With {.Advice = group.Key, .Severity = group.Max(Function(item) item.Severity)}).
            OrderByDescending(Function(item) item.Severity).Take(8).ToList()
        If advice.Count > 0 Then
            Ensure(160)
            SectionTitle("Suggested checks")
            For Each item In advice
                Dim height = MeasureHeight(item.Advice, _body, ContentW - 80)
                Ensure(height + 30)
                Using brush As New SolidBrush(SeverityColor(item.Severity))
                    _g.FillEllipse(brush, Margin + 6, _y + 8, 14, 14)
                End Using
                Using brush As New SolidBrush(Ink)
                    _g.DrawString(item.Advice, _body, brush, New RectangleF(Margin + 36, _y, ContentW - 80, height + 4))
                End Using
                _pageText.AppendLine(item.Advice)
                _y += height + 14
            Next
            _y += 10
        End If
        DrawConclusion()
        DrawMethod()
    End Sub

    Private Sub DrawMethod()
        _sectionContext = "Analysis method & limitations"
        NewPage(False)
        SectionTitle("How this report was made - method & limitations")
        DrawParagraph("LOCAL, RULE-BASED ANALYSIS  |  No external AI service  |  No invented or backfilled readings", _bold, Navy, Margin, ContentW)
        Dim request = _analysis.Result.Request
        DrawTable({"STUDY RECORD", "VALUE / TRACEABILITY"}, {240, 880}, New List(Of String()) From {
            New String() {"Requested period", EquipmentAnalysis.Stamp(request.FromUtc) & " to " & EquipmentAnalysis.Stamp(request.ToUtc) & " (end exclusive)"},
            New String() {"Selected evidence", _analysis.Scope.Count.ToString(Inv) & " channels; " & _analysis.TotalSamples.ToString("N0", Inv) &
                " saved numeric-channel observations. State/alarm observations are handled separately."},
            New String() {"Loaded resolution", If(request.ThinSeconds > 0, "One retained reading per " & request.ThinSeconds.ToString(Inv) &
                " s. This is a sampled study: short events and true extrema between retained readings may be missed.",
                "No requested time thinning. Statistics use the returned valid saved readings; visual chart reduction preserves their extrema.")},
            New String() {"Data assurance", EquipmentAnalysis.Num(_analysis.Completeness) & " % saved-sample validity; " &
                EquipmentAnalysis.Num(_analysis.ObservedCoveragePercent) & " % average observed time coverage. These are different measures."}})
        Dim rows As New List(Of String()) From {
            New String() {"Statistics & continuity", "Arithmetic mean, median, observed min/max, population standard deviation and nearest-rank P95 on finite good-quality samples.",
                "Statistics are sample-weighted, not time-weighted. Coverage sums adjacent valid spans no longer than max(5 s, 3 x median cadence), divided by the requested duration, then averages channels. Window edges count as unobserved."}}
        If _analysis.Channels.Any(Function(channel) channel.Metric.Unit = "V") Then
            rows.Add({"Voltage", "AC: deviation from each channel's period median: >5 % warning; >=10 % critical. Battery/DC median changes >5 % are informational, not AC warning/critical limits.",
                "Statistical screening, not a nominal-voltage compliance test. DC requires chemistry, cell count and approved operating limits. Saved zero readings require checking supply state, raw registers and meter scaling before declaring an outage."})
        End If
        If _analysis.Channels.Any(Function(channel) {"kW", "kVA", "kVAr", "kvar", "A"}.Contains(channel.Metric.Unit)) Then
            rows.Add({"Load & rating", "AC current/power with positive period mean: episodes >15 % above own mean; >=25 % warning, >=50 % critical. DC current is excluded. Verified capacity: >=90 % warning, >=100 % critical.",
                "Above-average load is not overload. Nonpositive power means do not become tiny positive denominators. Capacity requires selected valid kVA plus a configured rating, or the UPS card's own load %. Unverified transformer scaling is not used to declare overload."})
        End If
        If _analysis.Channels.Any(Function(channel) channel.Metric.Unit = "V" OrElse channel.Metric.Unit = "A") Then
            rows.Add({"Phase balance", "Max absolute phase deviation / phase mean at exactly matching timestamps. Voltage >2 % warning, >5 % critical; current >10 % / >20 %.",
                "Exactly three selected phases must have one value each at matching timestamps; this is magnitude screening, not negative-sequence unbalance or IEC compliance."})
        End If
        If _analysis.Channels.Any(Function(channel) channel.Metric.Unit.Contains("C") OrElse channel.Metric.Name.IndexOf("temp", StringComparison.OrdinalIgnoreCase) >= 0) Then
            rows.Add({"Temperature", "Non-battery sensor generic guide: >=80 deg C warning, >=100 deg C critical; rise >=3 deg C/min or last-third mean >=5 deg C above first-third.",
                "Not site-approved limits. Battery sensors are excluded from generic 80/100 deg C rules. Confirm sensor mapping, ambient conditions, cooling and equipment manufacturer limits."})
        End If
        If _analysis.Channels.Any(Function(channel) channel.Metric.Unit = "Hz" OrElse channel.Metric.Unit = "PF") Then
            rows.Add({"Frequency & PF", "Frequency baseline 50/60 Hz from median; deviation >0.5 Hz warning, >=1 Hz critical. Positive PF <0.85 warning, <=0.80 critical.",
                "Review configured supply frequency and PF sign convention. These rules do not replace equipment protection settings."})
        End If
        rows.Add({"Chart interpretation", "Charts preserve loaded extrema when visually reduced. Lines break at recording gaps; event windows retain context and full UTC ranges.",
            "Every detected threshold run is retained in analysis; every timed warning/critical finding is listed. Full-period channel overviews accompany at most two detail zooms. Omitted zooms do not omit findings."})
        rows.Add({"Totals & attribution", "Compare like-for-like channels within their equipment family. TOTAL (T1-T4) is the transformer aggregate, not a sum including downstream UPS outputs.",
            "Never add overlapping transformer and UPS demand. Correlations suggest checks only; energy, when shown, is calculated from eligible saved power samples and is not a revenue-meter reading."})
        rows.Add({"Confidence & attribution", "Priority is a screening-rule flag, not proof of failure. Coincident extreme-time groups use a 1-second tolerance, not a physical incident model.",
            "No equipment health score is issued. Verify sensor/source quality, raw records and matching alarms before physical fault attribution. No events does not prove normal operation; gaps and unselected channels remain unknown."})
        DrawTable({"ANALYSIS STEP", "CALCULATION / GUIDE", "INTERPRETATION & LIMITS"}, {195, 420, 505}, rows)
    End Sub

    Private Sub DrawElectricalKnowledge()
        SectionTitle("Electrical interpretation - evidence and calculation prerequisites")
        DrawParagraph("Scope-aware engineering checks, not a manufacturer specification or an automatic physical diagnosis. Only selected recorded evidence is available.",
            _small, Muted, Margin, ContentW)
        DrawParagraph("Analysis order: scope/identity -> quality/time -> electrical relationship -> eligible calculations -> numeric observations/references -> unproven hypotheses -> bounded conclusion. If a prerequisite fails, state what is missing and withhold that calculation or diagnosis; do not fill the gap with a plausible explanation.",
            _bold, Ink, Margin, ContentW)
        DrawTable({"CHECK", "WHAT TO VERIFY / CALCULATION BASIS", "LIMITS / EVIDENCE STILL NEEDED"},
            {195, 460, 465}, ElectricalReportKnowledge.Checklist(_analysis))
    End Sub

    Private NotInheritable Class ChartSeries
        Public Property Name As String = String.Empty
        Public Property Color As Color
        Public Property Points As List(Of ReportPoint)
        Public Property GapSeconds As Double
        Public Property BreakBefore As HashSet(Of DateTime)
    End Class

    Private Shared Function Reduce(points As List(Of ReportPoint), buckets As Integer) As List(Of ReportPoint)
        If points.Count <= buckets * 2 Then Return points
        Dim result As New List(Of ReportPoint)(buckets * 2 + 2) From {points(0)}
        Dim size = points.Count / CDbl(buckets)
        For bucket = 0 To buckets - 1
            Dim first = CInt(Math.Floor(bucket * size))
            Dim last = Math.Min(points.Count - 1, CInt(Math.Floor((bucket + 1) * size)) - 1)
            If last < first Then Continue For
            Dim low = first
            Dim high = first
            For index = first To last
                If points(index).V < points(low).V Then low = index
                If points(index).V > points(high).V Then high = index
            Next
            If low = high Then
                result.Add(points(low))
            ElseIf low < high Then
                result.Add(points(low))
                result.Add(points(high))
            Else
                result.Add(points(high))
                result.Add(points(low))
            End If
        Next
        If result.Last().T <> points.Last().T Then result.Add(points.Last())
        Return result
    End Function

    Private Sub DrawChart(x As Integer, y As Integer, width As Integer, height As Integer, series As List(Of ChartSeries), unit As String,
                          bands As List(Of ReportFinding), solo As ChannelAnalysis, Optional rangeFrom As DateTime? = Nothing, Optional rangeTo As DateTime? = Nothing)
        If rangeFrom.HasValue AndAlso rangeTo.HasValue Then
            Dim winStart = rangeFrom.Value
            Dim winEnd = rangeTo.Value
            series = series.Select(Function(item) New ChartSeries With {.Name = item.Name, .Color = item.Color, .GapSeconds = item.GapSeconds,
                .BreakBefore = item.BreakBefore, .Points = item.Points.Where(Function(point) point.T >= winStart AndAlso point.T <= winEnd).ToList()}).Where(Function(item) item.Points.Count >= 1).ToList()
            If series.Count = 0 Then Return
        End If
        Card(New Rectangle(x, y, width, height), Color.White, Line)
        Dim legendRows = CInt(Math.Ceiling(series.Count / 2.0R))
        Dim legendH = If(series.Count > 1, legendRows * 34 + 14, 8)
        Dim plot As New Rectangle(x + 96, y + 16 + legendH, width - 96 - 24, height - 16 - legendH - 56)
        Dim tMin = If(rangeFrom.HasValue, rangeFrom.Value, series.Min(Function(item) item.Points(0).T))
        Dim tMax = If(rangeTo.HasValue, rangeTo.Value, series.Max(Function(item) item.Points(item.Points.Count - 1).T))
        If tMax <= tMin Then tMax = tMin.AddSeconds(1)
        Dim vMin = series.Min(Function(item) item.Points.Min(Function(point) point.V))
        Dim vMax = series.Max(Function(item) item.Points.Max(Function(point) point.V))
        If vMax - vMin < 1.0E-9 Then
            vMin -= 1.0R
            vMax += 1.0R
        End If
        Dim pad = (vMax - vMin) * 0.1R
        vMin -= pad
        vMax += pad
        Dim totalSeconds = (tMax - tMin).TotalSeconds
        Dim toX = Function(t As DateTime) CSng(plot.Left + (t - tMin).TotalSeconds / totalSeconds * plot.Width)
        Dim toY = Function(v As Double) CSng(plot.Bottom - (v - vMin) / (vMax - vMin) * plot.Height)

        For tick = 0 To 5
            Dim value = vMin + (vMax - vMin) * tick / 5.0R
            Dim py = toY(value)
            Using pen As New Pen(Line, 1.0F)
                _g.DrawLine(pen, plot.Left, py, plot.Right, py)
            End Using
            Text(EquipmentAnalysis.Num(value), _small, Muted, x + 8, CInt(py) - 10, 84)
        Next
        Dim spanHours = totalSeconds / 3600.0R
        For tick = 0 To 5
            Dim time = tMin.AddSeconds(totalSeconds * tick / 5.0R)
            Dim px = CInt(toX(time))
            Using pen As New Pen(Color.FromArgb(120, Line), 1.0F)
                _g.DrawLine(pen, px, plot.Top, px, plot.Bottom)
            End Using
            Dim format = If(totalSeconds <= 10, "HH:mm:ss.fff",
                If(spanHours <= 2.0R, "HH:mm:ss", If(spanHours <= 48.0R, "dd HH:mm", "MM-dd")))
            Text(time.ToString(format, Inv), _small, Muted, Math.Max(x + 4, Math.Min(px - 40, x + width - 100)), plot.Bottom + 8, 100)
        Next
        Text("UTC   " & unit, _label, Muted, plot.Left, plot.Bottom + 30, 400)

        For Each band In bands
            If band.Category = "Data quality" Then Continue For
            Dim startX = toX(If(band.StartUtc.Value < tMin, tMin, band.StartUtc.Value))
            Dim endX = toX(If(band.EndUtc.HasValue AndAlso band.EndUtc.Value <= tMax, band.EndUtc.Value, If(band.StartUtc.Value > tMax, tMax, tMax)))
            If band.StartUtc.Value > tMax OrElse (band.EndUtc.HasValue AndAlso band.EndUtc.Value < tMin) Then Continue For
            Dim widthPx = Math.Max(6.0F, endX - startX)
            Using brush As New SolidBrush(Tint(SeverityColor(band.Severity), 46))
                _g.FillRectangle(brush, startX, plot.Top, widthPx, plot.Height)
            End Using
        Next
        _g.SetClip(plot)
        For Each item In series
            For Each gap In If(solo Is Nothing, New List(Of ReportPoint)(), solo.Gaps)
                Using brush As New SolidBrush(Color.FromArgb(40, 148, 163, 184))
                    _g.FillRectangle(brush, toX(gap.T), plot.Top, Math.Max(3.0F, toX(gap.T.AddSeconds(gap.V)) - toX(gap.T)), plot.Height)
                End Using
            Next
            Dim sourceSegment As New List(Of ReportPoint)()
            Dim previous As ReportPoint? = Nothing
            Dim flushSegment = Sub()
                                   Dim segment = Reduce(sourceSegment, plot.Width \ 2).
                                       Select(Function(point) New PointF(toX(point.T), toY(point.V))).ToList()
                                   If segment.Count >= 2 Then
                                       Using pen As New Pen(item.Color, 2.6F) With {.LineJoin = LineJoin.Round}
                                           _g.DrawLines(pen, segment.ToArray())
                                       End Using
                                   ElseIf segment.Count = 1 Then
                                       Using brush As New SolidBrush(item.Color)
                                           _g.FillEllipse(brush, segment(0).X - 3, segment(0).Y - 3, 6, 6)
                                       End Using
                                   End If
                                   Using brush As New SolidBrush(item.Color)
                                       For Each point In segment
                                           _g.FillEllipse(brush, point.X - 2, point.Y - 2, 4, 4)
                                       Next
                                   End Using
                                   sourceSegment.Clear()
                               End Sub
            For Each point In item.Points
                If previous.HasValue AndAlso ((point.T - previous.Value.T).TotalSeconds > item.GapSeconds OrElse point.T <= previous.Value.T OrElse
                    (item.BreakBefore IsNot Nothing AndAlso item.BreakBefore.Contains(point.T))) Then flushSegment()
                sourceSegment.Add(point)
                previous = point
            Next
            flushSegment()
        Next
        _g.ResetClip()

        If solo IsNot Nothing Then
            Dim pts = series(0).Points
            Dim peakPoint = pts(0)
            Dim lowPoint = pts(0)
            For Each point In pts
                If point.V > peakPoint.V Then peakPoint = point
                If point.V < lowPoint.V Then lowPoint = point
            Next
            Dim mean = pts.Average(Function(point) point.V)
            Using pen As New Pen(Muted, 1.5F) With {.DashStyle = DashStyle.Dash}
                _g.DrawLine(pen, plot.Left, toY(mean), plot.Right, toY(mean))
            End Using
            Using brush As New SolidBrush(Color.FromArgb(215, 255, 255, 255))
                _g.FillRectangle(brush, plot.Left + 4, CInt(toY(mean)) + 3, 150, 24)
            End Using
            Text("average " & EquipmentAnalysis.Num(mean), _small, Muted, plot.Left + 8, CInt(toY(mean)) + 4, 186)
            Marker(toX(peakPoint.T), toY(peakPoint.V), "Peak " & EquipmentAnalysis.Num(peakPoint.V) & "  " & peakPoint.T.ToString("MM-dd HH:mm:ss.FFFFFFF", Inv), Red, plot, True)
            Marker(toX(lowPoint.T), toY(lowPoint.V), "Low " & EquipmentAnalysis.Num(lowPoint.V) & "  " & lowPoint.T.ToString("MM-dd HH:mm:ss.FFFFFFF", Inv), Blue, plot, False)
        End If
        Using pen As New Pen(Color.FromArgb(203, 213, 225), 1.5F)
            _g.DrawRectangle(pen, plot)
        End Using
        If series.Count > 1 Then
            For index = 0 To series.Count - 1
                Dim item = series(index)
                Dim legendX = x + 24 + (index Mod 2) * ((width - 48) \ 2)
                Dim legendY = y + 18 + (index \ 2) * 34
                Using brush As New SolidBrush(item.Color)
                    _g.FillRectangle(brush, legendX, legendY + 10, 26, 6)
                End Using
                Text(item.Name, _small, Ink, legendX + 34, legendY, (width - 48) \ 2 - 42)
            Next
        End If
    End Sub

    Private Sub Marker(px As Single, py As Single, label As String, color As Color, plot As Rectangle, above As Boolean)
        Using brush As New SolidBrush(color)
            _g.FillEllipse(brush, px - 7, py - 7, 14, 14)
        End Using
        Using pen As New Pen(Color.White, 2.0F)
            _g.DrawEllipse(pen, px - 7, py - 7, 14, 14)
        End Using
        Dim size = _g.MeasureString(label, _small)
        Dim lx = Math.Max(plot.Left + 4, Math.Min(px - size.Width / 2, plot.Right - size.Width - 8))
        Dim ly = If(above, py - size.Height - 12, py + 12)
        ly = Math.Max(plot.Top + 2, Math.Min(ly, plot.Bottom - size.Height - 2))
        Using brush As New SolidBrush(Color.FromArgb(235, 255, 255, 255))
            _g.FillRectangle(brush, lx - 4, ly - 1, size.Width + 8, size.Height + 2)
        End Using
        Using brush As New SolidBrush(color)
            _g.DrawString(label, _small, brush, lx, ly)
        End Using
    End Sub

    Private Sub SavePdf(path As String)
        Dim count = _pages.Count
        Using stream As New FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            Dim offsets As New List(Of Long) From {0}
            Dim ascii = Sub(value As String)
                            Dim bytes = Encoding.ASCII.GetBytes(value)
                            stream.Write(bytes, 0, bytes.Length)
                        End Sub
            ascii("%PDF-1.4" & vbLf)
            Dim objectCount = 3 + count * 3
            offsets.Add(stream.Position)
            ascii("1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj" & vbLf)
            offsets.Add(stream.Position)
            ascii("2 0 obj<</Type/Pages/Count " & count.ToString(Inv) & "/Kids[" &
                String.Join(" ", Enumerable.Range(0, count).Select(Function(index) (4 + index * 3).ToString(Inv) & " 0 R")) & "]>>endobj" & vbLf)
            offsets.Add(stream.Position)
            ascii("3 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica/Encoding/WinAnsiEncoding>>endobj" & vbLf)
            For index = 0 To count - 1
                Dim pageObject = 4 + index * 3
                Dim imageObject = pageObject + 1
                Dim contentObject = pageObject + 2
                offsets.Add(stream.Position)
                ascii(pageObject.ToString(Inv) & " 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 595.28 841.89]/Resources<</XObject<</Im0 " &
                    imageObject.ToString(Inv) & " 0 R>>/Font<</F1 3 0 R>>>>/Contents " & contentObject.ToString(Inv) & " 0 R>>endobj" & vbLf)
                offsets.Add(stream.Position)
                ascii(imageObject.ToString(Inv) & " 0 obj<</Type/XObject/Subtype/Image/Width " & PageW.ToString(Inv) & "/Height " & PageH.ToString(Inv) &
                    "/ColorSpace/DeviceRGB/BitsPerComponent 8/Filter/DCTDecode/Length " & _pages(index).Length.ToString(Inv) & ">>stream" & vbLf)
                stream.Write(_pages(index), 0, _pages(index).Length)
                ascii(vbLf & "endstream endobj" & vbLf)
                Dim textLines As New List(Of String)()
                For Each sourceLine In _texts(index).Replace(vbCr, "").Split(ControlChars.Lf)
                    Dim remaining = sourceLine
                    While remaining.Length > 110
                        Dim cut = remaining.LastIndexOf(" "c, 110)
                        If cut <= 0 Then cut = 110
                        textLines.Add(remaining.Substring(0, cut))
                        remaining = remaining.Substring(cut).TrimStart()
                    End While
                    If remaining.Length > 0 Then textLines.Add(remaining)
                Next
                Dim fontSize = Math.Min(8.0R, 740.0R / Math.Max(1, textLines.Count) / 1.25R)
                Dim content As New StringBuilder()
                content.Append("q 595.28 0 0 841.89 0 0 cm /Im0 Do Q" & vbLf & "BT /F1 " & fontSize.ToString("0.####", Inv) &
                    " Tf 3 Tr 20 800 Td " & (fontSize * 1.25R).ToString("0.####", Inv) & " TL" & vbLf)
                For Each textLine In textLines
                    content.Append("(").Append(EscapePdf(textLine)).Append(") Tj T*" & vbLf)
                Next
                content.Append("ET")
                Dim contentBytes = Encoding.ASCII.GetBytes(content.ToString())
                offsets.Add(stream.Position)
                ascii(contentObject.ToString(Inv) & " 0 obj<</Length " & contentBytes.Length.ToString(Inv) & ">>stream" & vbLf)
                stream.Write(contentBytes, 0, contentBytes.Length)
                ascii(vbLf & "endstream endobj" & vbLf)
            Next
            Dim xref = stream.Position
            ascii("xref" & vbLf & "0 " & (objectCount + 1).ToString(Inv) & vbLf & "0000000000 65535 f " & vbLf)
            For index = 1 To objectCount
                ascii(offsets(index).ToString("D10", Inv) & " 00000 n " & vbLf)
            Next
            ascii("trailer<</Size " & (objectCount + 1).ToString(Inv) & "/Root 1 0 R>>" & vbLf & "startxref" & vbLf & xref.ToString(Inv) & vbLf & "%%EOF")
        End Using
    End Sub

    Private Shared Function EscapePdf(value As String) As String
        Dim builder As New StringBuilder()
        For Each character In value
            Select Case character
                Case "\"c, "("c, ")"c : builder.Append("\"c).Append(character)
                Case Else : builder.Append(If(AscW(character) >= 32 AndAlso AscW(character) < 127, character, "?"c))
            End Select
        Next
        Return builder.ToString()
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        For Each font In {_body, _bold, _small, _label, _h2, _kpi, _title}
            font.Dispose()
        Next
        If _g IsNot Nothing Then _g.Dispose()
        If _bitmap IsNot Nothing Then _bitmap.Dispose()
        If _logo IsNot Nothing Then _logo.Dispose()
    End Sub
End Class
