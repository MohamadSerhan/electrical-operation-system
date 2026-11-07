Imports Npgsql

' ECG outages are derived from the alarm log itself: each "ecg-off" row opens an outage and the next "ecg-back" row
' closes it, so the outage list, counts and durations always match the Alarm History.
Public NotInheritable Class EcgOutageLog
    Private Sub New()
    End Sub

    Public Shared Function Read() As IReadOnlyList(Of PowerEventRecord)
        Dim marks As New List(Of (Utc As DateTime, Off As Boolean))()
        Using connection = PostgresDatabase.Open(), command As New NpgsqlCommand(
            "SELECT observed_utc, alarm_key FROM equipment_alarm_history WHERE equipment_type='Transformer' AND equipment_id='TOTAL' " &
            "AND alarm_key IN ('ecg-off','ecg-back') AND (current_state->>'Active')='true' ORDER BY observed_utc, event_id", connection)
            Using reader = command.ExecuteReader()
                While reader.Read()
                    marks.Add((DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc), reader.GetString(1) = "ecg-off"))
                End While
            End Using
        End Using
        Return Pair(marks)
    End Function

    Public Shared Function Pair(marks As IEnumerable(Of (Utc As DateTime, Off As Boolean))) As IReadOnlyList(Of PowerEventRecord)
        Dim result As New List(Of PowerEventRecord)()
        Dim open As PowerEventRecord = Nothing
        For Each mark In marks.OrderBy(Function(item) item.Utc)
            If mark.Off Then
                If open Is Nothing Then
                    open = New PowerEventRecord With {.Id = Guid.NewGuid().ToString("N"), .Scope = "ECG", .StartUtc = mark.Utc,
                        .ConfirmedUtc = mark.Utc, .LastObservedUtc = mark.Utc, .EndReason = String.Empty, .Evidence = "All four transformers off"}
                End If
            ElseIf open IsNot Nothing Then
                open.EndUtc = mark.Utc
                open.LastObservedUtc = mark.Utc
                open.EndReason = "ECG back"
                result.Add(open)
                open = Nothing
            End If
        Next
        If open IsNot Nothing Then result.Add(open)
        Return result
    End Function
End Class
