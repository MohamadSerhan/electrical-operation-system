Imports System.Globalization
Imports System.IO

Public Enum RecordingHealthState
    NoData
    Recording
    Degraded
    Stalled
    Faulted
End Enum

Public NotInheritable Class RecordingHealth
    Public Property State As RecordingHealthState
    Public Property Text As String = String.Empty
    Public Property Detail As String = String.Empty
End Class

Public NotInheritable Class ReportWindow
    Public Property StartUtc As DateTime
    Public Property EndUtcExclusive As DateTime
    Public Property Description As String = String.Empty
End Class

Public NotInheritable Class HistorianHealthEvaluator
    Private Sub New()
    End Sub

    ' Health is derived from the last successful write and recorded failures, never from queue depth or file existence.
    Public Shared Function Evaluate(diagnostics As HistorianDiagnosticsSnapshot,
                                    utcNow As DateTime,
                                    expectedIntervalSeconds As Integer) As RecordingHealth
        If diagnostics Is Nothing Then Throw New ArgumentNullException(NameOf(diagnostics))
        Dim thresholdSeconds = Math.Max(60, Math.Max(1, expectedIntervalSeconds) * 6)
        Dim hasError = diagnostics.WriteFailures > 0 AndAlso Not String.IsNullOrWhiteSpace(diagnostics.LastError)
        If Not diagnostics.LastWriteUtc.HasValue Then
            If hasError Then
                Return New RecordingHealth With {
                    .State = RecordingHealthState.Faulted, .Text = "Fault",
                    .Detail = "No successful write; " & diagnostics.LastError}
            End If
            Return New RecordingHealth With {
                .State = RecordingHealthState.NoData, .Text = "No writes yet",
                .Detail = "The historian has not recorded a cycle in this session."}
        End If
        Dim age = utcNow - diagnostics.LastWriteUtc.Value
        If age.TotalSeconds > thresholdSeconds Then
            Return New RecordingHealth With {
                .State = If(hasError, RecordingHealthState.Faulted, RecordingHealthState.Stalled),
                .Text = If(hasError, "Fault", "Stalled"),
                .Detail = "Last successful write " & FormatAge(age) & " ago (limit " &
                    thresholdSeconds.ToString(CultureInfo.InvariantCulture) & " s)."}
        End If
        If hasError Then
            Return New RecordingHealth With {
                .State = RecordingHealthState.Degraded, .Text = "Recording (errors)",
                .Detail = diagnostics.WriteFailures.ToString(CultureInfo.InvariantCulture) &
                    " write failure(s); latest: " & diagnostics.LastError}
        End If
        Return New RecordingHealth With {
            .State = RecordingHealthState.Recording, .Text = "Recording",
            .Detail = "Last successful write " & FormatAge(age) & " ago."}
    End Function

    ' Start is inclusive; the end minute is inclusive, so the exclusive UTC upper bound is the following minute.
    Public Shared Function ResolveReportWindow(startLocal As DateTime,
                                               endLocalInclusive As DateTime,
                                               zone As TimeZoneInfo) As ReportWindow
        If zone Is Nothing Then Throw New ArgumentNullException(NameOf(zone))
        Dim start = DateTime.SpecifyKind(New DateTime(startLocal.Year, startLocal.Month, startLocal.Day,
                                                       startLocal.Hour, startLocal.Minute, 0), DateTimeKind.Unspecified)
        Dim finish = DateTime.SpecifyKind(New DateTime(endLocalInclusive.Year, endLocalInclusive.Month, endLocalInclusive.Day,
                                                        endLocalInclusive.Hour, endLocalInclusive.Minute, 0).AddMinutes(1),
                                          DateTimeKind.Unspecified)
        If finish <= start Then
            Throw New InvalidDataException("The report end must be on or after its start.")
        End If
        If zone.IsInvalidTime(start) OrElse zone.IsInvalidTime(finish) Then
            Throw New InvalidDataException("The selected local time does not exist in the factory timezone (daylight-saving gap). Choose a different time.")
        End If
        Dim startUtc = TimeZoneInfo.ConvertTimeToUtc(start, zone)
        Dim endUtc = TimeZoneInfo.ConvertTimeToUtc(finish, zone)
        Return New ReportWindow With {
            .StartUtc = startUtc,
            .EndUtcExclusive = endUtc,
            .Description = start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) & " to " &
                finish.AddMinutes(-1).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) & " " & zone.Id &
                " (UTC " & startUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) & " to " &
                endUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) & ", end exclusive)"}
    End Function

    Public Shared Function FormatAge(age As TimeSpan) As String
        If age.TotalSeconds < 0 Then Return "0 s"
        If age.TotalMinutes < 2 Then Return CInt(age.TotalSeconds).ToString(CultureInfo.InvariantCulture) & " s"
        If age.TotalHours < 2 Then Return CInt(age.TotalMinutes).ToString(CultureInfo.InvariantCulture) & " min"
        If age.TotalDays < 2 Then Return CInt(age.TotalHours).ToString(CultureInfo.InvariantCulture) & " h"
        Return CInt(age.TotalDays).ToString(CultureInfo.InvariantCulture) & " d"
    End Function

    Public Shared Function FormatBytes(bytes As Long) As String
        If bytes < 1024 Then Return bytes.ToString(CultureInfo.InvariantCulture) & " B"
        If bytes < 1024L * 1024L Then Return (bytes / 1024.0R).ToString("0.0", CultureInfo.InvariantCulture) & " KB"
        If bytes < 1024L * 1024L * 1024L Then Return (bytes / 1048576.0R).ToString("0.0", CultureInfo.InvariantCulture) & " MB"
        Return (bytes / 1073741824.0R).ToString("0.00", CultureInfo.InvariantCulture) & " GB"
    End Function

    Public Shared Function GetParameterCategory(parameterId As String) As String
        If parameterId Is Nothing Then Return "Other"
        If parameterId.StartsWith("V_", StringComparison.Ordinal) OrElse parameterId.StartsWith("AVG_V_", StringComparison.Ordinal) Then Return "Voltage"
        If parameterId.StartsWith("CURRENT_", StringComparison.Ordinal) OrElse parameterId = "AVG_CURRENT" Then Return "Current"
        If parameterId = "PTOT_KW" OrElse parameterId = "KVA_CALC" OrElse parameterId = "PF_CALC" Then Return "Power"
        If parameterId.StartsWith("STAT_", StringComparison.Ordinal) Then Return MeasurementCatalog.StatisticCategory
        If parameterId = "FREQUENCY" Then Return "Frequency"
        If parameterId = "TDEGC_C" Then Return "Temperature"
        Return "Other"
    End Function
End Class
