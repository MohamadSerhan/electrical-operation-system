Imports System.Drawing
Imports System.Drawing.Imaging
Imports System.IO
Imports System.Text

Public Module AnalyticsChartPdfExporter
    Private Const PageWidth As Double = 842.0R
    Private Const PageHeight As Double = 595.0R
    Private Const PageMargin As Double = 24.0R

    Public Sub Export(bitmap As Bitmap, outputPath As String)
        If bitmap Is Nothing Then Throw New ArgumentNullException(NameOf(bitmap))
        If String.IsNullOrWhiteSpace(outputPath) OrElse
           Not String.Equals(Path.GetExtension(outputPath), ".pdf", StringComparison.OrdinalIgnoreCase) Then
            Throw New ArgumentException("Analytics chart PDF export requires a .pdf destination.", NameOf(outputPath))
        End If
        If bitmap.Width < 1 OrElse bitmap.Height < 1 Then
            Throw New ArgumentException("Analytics chart image must have non-zero dimensions.", NameOf(bitmap))
        End If

        Dim imageBytes As Byte()
        Using encoded As New MemoryStream()
            bitmap.Save(encoded, ImageFormat.Jpeg)
            imageBytes = encoded.ToArray()
        End Using
        Dim pageContent = BuildPageContent(bitmap.Width, bitmap.Height)
        Dim pageContentBytes = Encoding.ASCII.GetBytes(pageContent)

        Using document As New MemoryStream()
            WriteAscii(document, "%PDF-1.4" & vbLf & "%EOEMS chart export" & vbLf)
            Dim offsets As New List(Of Long) From {0}
            WriteObject(document, offsets, 1, "<< /Type /Catalog /Pages 2 0 R >>")
            WriteObject(document, offsets, 2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            WriteObject(document, offsets, 3,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " &
                NumberText(PageWidth) & " " & NumberText(PageHeight) &
                "] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>")
            offsets.Add(document.Position)
            WriteAscii(document,
                "4 0 obj" & vbLf &
                "<< /Type /XObject /Subtype /Image /Width " & bitmap.Width.ToString(Globalization.CultureInfo.InvariantCulture) &
                " /Height " & bitmap.Height.ToString(Globalization.CultureInfo.InvariantCulture) &
                " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " &
                imageBytes.Length.ToString(Globalization.CultureInfo.InvariantCulture) & " >>" & vbLf &
                "stream" & vbLf)
            document.Write(imageBytes, 0, imageBytes.Length)
            WriteAscii(document, vbLf & "endstream" & vbLf & "endobj" & vbLf)
            offsets.Add(document.Position)
            WriteAscii(document,
                "5 0 obj" & vbLf &
                "<< /Length " & pageContentBytes.Length.ToString(Globalization.CultureInfo.InvariantCulture) & " >>" & vbLf &
                "stream" & vbLf)
            document.Write(pageContentBytes, 0, pageContentBytes.Length)
            WriteAscii(document, "endstream" & vbLf & "endobj" & vbLf)

            Dim xrefOffset = document.Position
            WriteAscii(document, "xref" & vbLf & "0 6" & vbLf & "0000000000 65535 f " & vbLf)
            For objectIndex = 1 To 5
                WriteAscii(document, offsets(objectIndex).ToString("D10", Globalization.CultureInfo.InvariantCulture) & " 00000 n " & vbLf)
            Next
            WriteAscii(document,
                "trailer" & vbLf &
                "<< /Size 6 /Root 1 0 R >>" & vbLf &
                "startxref" & vbLf &
                xrefOffset.ToString(Globalization.CultureInfo.InvariantCulture) & vbLf &
                "%%EOF" & vbLf)

            Dim finalPath = Path.GetFullPath(outputPath)
            Dim temporaryPath = finalPath & "." & Guid.NewGuid().ToString("N") & ".partial"
            Try
                File.WriteAllBytes(temporaryPath, document.ToArray())
                File.Move(temporaryPath, finalPath, True)
            Finally
                If File.Exists(temporaryPath) Then File.Delete(temporaryPath)
            End Try
        End Using
    End Sub

    Private Function BuildPageContent(imageWidth As Integer, imageHeight As Integer) As String
        Dim scale = Math.Min((PageWidth - 2.0R * PageMargin) / imageWidth,
                             (PageHeight - 2.0R * PageMargin) / imageHeight)
        Dim drawWidth = imageWidth * scale
        Dim drawHeight = imageHeight * scale
        Dim x = (PageWidth - drawWidth) / 2.0R
        Dim y = (PageHeight - drawHeight) / 2.0R
        Return "q" & vbLf & NumberText(drawWidth) & " 0 0 " & NumberText(drawHeight) & " " &
            NumberText(x) & " " & NumberText(y) & " cm" & vbLf & "/Im0 Do" & vbLf & "Q" & vbLf
    End Function

    Private Sub WriteObject(stream As Stream, offsets As IList(Of Long), objectNumber As Integer, body As String)
        offsets.Add(stream.Position)
        WriteAscii(stream, objectNumber.ToString(Globalization.CultureInfo.InvariantCulture) & " 0 obj" & vbLf &
            body & vbLf & "endobj" & vbLf)
    End Sub

    Private Function NumberText(value As Double) As String
        Return value.ToString("0.###", Globalization.CultureInfo.InvariantCulture)
    End Function

    Private Sub WriteAscii(stream As Stream, value As String)
        Dim bytes = Encoding.ASCII.GetBytes(value)
        stream.Write(bytes, 0, bytes.Length)
    End Sub
End Module
