Imports System.Globalization
Imports System.IO
Imports System.Text

Public NotInheritable Class AnalyticsTotalCsvExporter
    Private Sub New()
    End Sub

    Public Shared Sub Export(path As String,
                             cycles As IReadOnlyList(Of AnalyticsTotalCycle),
                             configuredMaximumKw As Double)
        If String.IsNullOrWhiteSpace(path) Then Throw New ArgumentException("A CSV output path is required.", NameOf(path))
        If cycles Is Nothing Then Throw New ArgumentNullException(NameOf(cycles))
        For Each cycle In cycles
            If cycle Is Nothing OrElse Not Double.IsFinite(cycle.TotalActivePowerKw) OrElse
               cycle.TransformerValuesKw Is Nothing Then
                Throw New InvalidDataException("TOTAL CSV export contains an invalid cycle.")
            End If
            For Each analyzer In {"T1", "T2", "T3", "T4"}
                Dim value As Double
                If Not cycle.TransformerValuesKw.TryGetValue(analyzer, value) OrElse Not Double.IsFinite(value) Then
                    Throw New InvalidDataException("TOTAL CSV export requires valid aligned PTOT for " & analyzer & ".")
                End If
            Next
        Next
        Using writer As New StreamWriter(path, False, New UTF8Encoding(True))
            writer.WriteLine("HostAcquisitionTimestampUtc,AcquisitionCycleId,DataQuality,TotalActivePowerKw,T1ActivePowerKw,T2ActivePowerKw,T3ActivePowerKw,T4ActivePowerKw,ConfiguredDisplayScalePercent")
            For Each cycle In cycles.OrderBy(Function(item) item.TimestampUtc)
                Dim transformerValues As New List(Of Double)(4)
                For Each analyzer In {"T1", "T2", "T3", "T4"}
                    Dim value As Double
                    cycle.TransformerValuesKw.TryGetValue(analyzer, value)
                    transformerValues.Add(value)
                Next
                Dim loadPercent = LoadGaugeMath.GetLoadPercent(cycle.TotalActivePowerKw, configuredMaximumKw)
                writer.WriteLine(String.Join(",", {
                    cycle.TimestampUtc.ToString("o", CultureInfo.InvariantCulture),
                    cycle.AcquisitionCycleId.ToString(CultureInfo.InvariantCulture),
                    "GoodAlignedCycle",
                    cycle.TotalActivePowerKw.ToString("0.###", CultureInfo.InvariantCulture),
                    transformerValues(0).ToString("0.###", CultureInfo.InvariantCulture),
                    transformerValues(1).ToString("0.###", CultureInfo.InvariantCulture),
                    transformerValues(2).ToString("0.###", CultureInfo.InvariantCulture),
                    transformerValues(3).ToString("0.###", CultureInfo.InvariantCulture),
                    If(loadPercent.HasValue, loadPercent.Value.ToString("0.###", CultureInfo.InvariantCulture), String.Empty)
                }))
            Next
        End Using
    End Sub
End Class
