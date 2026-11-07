Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Threading
Imports Microsoft.VisualBasic.FileIO

Public NotInheritable Class PlcHistoryStore
    Private Const MaximumRowsPerLoad As Integer = 2000000
    Private Shared ReadOnly RequiredColumns As String() = {
        "TimestampUtc", "Transformer", "DataQuality", "V A-N", "V B-N", "V C-N",
        "PTOT (kW per operator confirmation)", "Phase A current (unit unverified)",
        "Phase B current (unit unverified)", "Phase C current (unit unverified)",
        "TDEGC temperature (°C per operator confirmation)"
    }

    Private Sub New()
    End Sub

    Public Shared Function DefaultDirectory() As String
        Return Path.Combine(AppContext.BaseDirectory, "data", "MeterLogs")
    End Function

    Public Shared Function ReadRange(directoryPath As String,
                                    startUtc As DateTime,
                                    endUtc As DateTime,
                                    cancellationToken As CancellationToken) As List(Of HistoricalMeterSample)
        If String.IsNullOrWhiteSpace(directoryPath) Then Throw New ArgumentException("A history directory is required.", NameOf(directoryPath))
        If startUtc.Kind <> DateTimeKind.Utc OrElse endUtc.Kind <> DateTimeKind.Utc OrElse endUtc <= startUtc Then
            Throw New ArgumentOutOfRangeException(NameOf(startUtc), "The history window must be a positive UTC range.")
        End If
        If Not Directory.Exists(directoryPath) Then Return New List(Of HistoricalMeterSample)()

        Dim firstDay = startUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
        Dim lastDay = endUtc.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
        Dim paths = Directory.EnumerateFiles(directoryPath, "PM5110_*.csv", System.IO.SearchOption.TopDirectoryOnly).
            Where(Function(path)
                      Dim datePart = System.IO.Path.GetFileNameWithoutExtension(path).Substring("PM5110_".Length)
                      Return datePart.Length = 8 AndAlso String.CompareOrdinal(datePart, firstDay) >= 0 AndAlso
                             String.CompareOrdinal(datePart, lastDay) <= 0 AndAlso
                             DateTime.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture,
                                                    DateTimeStyles.None, Nothing)
                  End Function).
            OrderBy(Function(path) path, StringComparer.OrdinalIgnoreCase).
            ToArray()

        Dim samples As New List(Of HistoricalMeterSample)()
        For Each path In paths
            cancellationToken.ThrowIfCancellationRequested()
            ReadFile(path, startUtc, endUtc, cancellationToken, samples)
            If samples.Count > MaximumRowsPerLoad Then
                Throw New InvalidDataException("The selected interval contains more than 2,000,000 records. Select a shorter historical period.")
            End If
        Next
        Return samples.OrderBy(Function(sample) sample.TimestampUtc).ToList()
    End Function

    Private Shared Sub ReadFile(path As String,
                                startUtc As DateTime,
                                endUtc As DateTime,
                                cancellationToken As CancellationToken,
                                output As List(Of HistoricalMeterSample))
        Using parser As New TextFieldParser(path)
            parser.TextFieldType = FieldType.Delimited
            parser.SetDelimiters(",")
            parser.HasFieldsEnclosedInQuotes = True
            parser.TrimWhiteSpace = False
            Dim header = parser.ReadFields()
            If header Is Nothing Then Throw New InvalidDataException("History file has no header: " & System.IO.Path.GetFileName(path))
            Dim indexes = BuildIndexes(header, path)

            While Not parser.EndOfData
                cancellationToken.ThrowIfCancellationRequested()
                Dim fields As String()
                Try
                    fields = parser.ReadFields()
                Catch ex As MalformedLineException
                    Throw New InvalidDataException("Malformed CSV record in " & System.IO.Path.GetFileName(path) &
                                                    " at line " & parser.ErrorLineNumber.ToString(CultureInfo.InvariantCulture) & ".", ex)
                End Try
                If fields Is Nothing Then Continue While
                If fields.Length <> header.Length Then
                    Throw New InvalidDataException("Unexpected CSV column count in " & System.IO.Path.GetFileName(path) &
                                                    " at line " & parser.LineNumber.ToString(CultureInfo.InvariantCulture) & ".")
                End If

                Dim timestamp As DateTimeOffset
                If Not DateTimeOffset.TryParse(fields(indexes("TimestampUtc")), CultureInfo.InvariantCulture,
                                               DateTimeStyles.AssumeUniversal Or DateTimeStyles.AdjustToUniversal, timestamp) Then
                    Throw New InvalidDataException("Invalid UTC timestamp in " & System.IO.Path.GetFileName(path) &
                                                    " at line " & parser.LineNumber.ToString(CultureInfo.InvariantCulture) & ".")
                End If
                Dim utc = timestamp.UtcDateTime
                If utc < startUtc OrElse utc > endUtc Then Continue While

                output.Add(New HistoricalMeterSample With {
                    .TransformerName = fields(indexes("Transformer")),
                    .TimestampUtc = utc,
                    .Quality = fields(indexes("DataQuality")),
                    .VoltageL1N = ParseValue(fields(indexes("V A-N"))),
                    .VoltageL2N = ParseValue(fields(indexes("V B-N"))),
                    .VoltageL3N = ParseValue(fields(indexes("V C-N"))),
                    .ActivePowerKw = ParseValue(fields(indexes("PTOT (kW per operator confirmation)"))),
                    .CurrentL1 = ParseValue(fields(indexes("Phase A current (unit unverified)"))),
                    .CurrentL2 = ParseValue(fields(indexes("Phase B current (unit unverified)"))),
                    .CurrentL3 = ParseValue(fields(indexes("Phase C current (unit unverified)"))),
                    .TemperatureC = ParseValue(fields(indexes("TDEGC temperature (°C per operator confirmation)")))
                })
                If output.Count > MaximumRowsPerLoad Then
                    Throw New InvalidDataException("The selected interval contains more than 2,000,000 records. Select a shorter historical period.")
                End If
            End While
        End Using
    End Sub

    Private Shared Function BuildIndexes(header As String(), path As String) As Dictionary(Of String, Integer)
        Dim indexes As New Dictionary(Of String, Integer)(StringComparer.Ordinal)
        For index = 0 To header.Length - 1
            If Not indexes.ContainsKey(header(index)) Then indexes.Add(header(index), index)
        Next
        For Each column In RequiredColumns
            If Not indexes.ContainsKey(column) Then
                Throw New InvalidDataException("Required column '" & column & "' is missing from " & System.IO.Path.GetFileName(path) & ".")
            End If
        Next
        Return indexes
    End Function

    Private Shared Function ParseValue(value As String) As Double?
        Dim parsed As Double
        If String.IsNullOrWhiteSpace(value) OrElse
           Not Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, parsed) OrElse
           Not Double.IsFinite(parsed) Then Return Nothing
        Return parsed
    End Function
End Class
