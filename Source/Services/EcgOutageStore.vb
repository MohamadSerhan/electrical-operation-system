Imports Npgsql

' Permanent ECG outage record. Every outage found in the alarm log is copied to ecg_outage and the per day / week / month / year
' totals are kept in ecg_outage_period. Nothing here is ever deleted, so the outage hours survive any log or history limit.
Public NotInheritable Class EcgOutageStore
    Private Sub New()
    End Sub

    Private Shared ReadOnly Gate As New Object()

    Private Const UpsertOutageSql As String =
        "INSERT INTO ecg_outage(start_utc, end_utc, duration_seconds, updated_at) VALUES (@start, @end, @duration, now()) " &
        "ON CONFLICT (start_utc) DO UPDATE SET end_utc = COALESCE(EXCLUDED.end_utc, ecg_outage.end_utc), " &
        " duration_seconds = CASE WHEN EXCLUDED.end_utc IS NULL AND ecg_outage.end_utc IS NOT NULL THEN ecg_outage.duration_seconds ELSE EXCLUDED.duration_seconds END, " &
        " updated_at = now()"

    ' An outage is counted in the period it started in; its hours are split across every period it covers.
    Private Const PeriodsSql As String =
        "INSERT INTO ecg_outage_period(period_kind, period_start, outages, total_seconds, longest_seconds, updated_at) " &
        "SELECT k.kind, p::date, COUNT(*) FILTER (WHERE date_trunc(k.kind, o.s) = p), " &
        " ROUND(SUM(EXTRACT(EPOCH FROM LEAST(o.e, p + k.step) - GREATEST(o.s, p))))::bigint, " &
        " COALESCE(MAX(o.d) FILTER (WHERE date_trunc(k.kind, o.s) = p), 0), now() " &
        "FROM (SELECT start_utc AT TIME ZONE @tz AS s, COALESCE(end_utc, now()) AT TIME ZONE @tz AS e, " &
        "  GREATEST(0, EXTRACT(EPOCH FROM COALESCE(end_utc, now()) - start_utc))::int AS d FROM ecg_outage) o " &
        "CROSS JOIN (VALUES ('day', interval '1 day'), ('week', interval '1 week'), ('month', interval '1 month'), ('year', interval '1 year')) AS k(kind, step) " &
        "CROSS JOIN LATERAL generate_series(date_trunc(k.kind, o.s), date_trunc(k.kind, o.e), k.step) AS p " &
        "GROUP BY k.kind, p " &
        "ON CONFLICT (period_kind, period_start) DO UPDATE SET outages = EXCLUDED.outages, total_seconds = EXCLUDED.total_seconds, " &
        " longest_seconds = EXCLUDED.longest_seconds, updated_at = now()"

    Public Shared Sub Sync()
        SyncLock Gate
            Dim found = EcgOutageLog.Read()
            Using connection = PostgresDatabase.Open()
                Using transaction = connection.BeginTransaction()
                    For Each item In found
                        Using command As New NpgsqlCommand(UpsertOutageSql, connection, transaction)
                            command.Parameters.AddWithValue("start", item.StartUtc)
                            command.Parameters.AddWithValue("end", NpgsqlTypes.NpgsqlDbType.TimestampTz, If(item.EndUtc.HasValue, CObj(item.EndUtc.Value), DBNull.Value))
                            command.Parameters.AddWithValue("duration", CInt(Math.Max(0, ((If(item.EndUtc, DateTime.UtcNow)) - item.StartUtc).TotalSeconds)))
                            command.ExecuteNonQuery()
                        End Using
                    Next
                    Using command As New NpgsqlCommand(PeriodsSql, connection, transaction)
                        command.Parameters.AddWithValue("tz", EnergyAccounting.LocalZoneName(connection))
                        command.ExecuteNonQuery()
                    End Using
                    transaction.Commit()
                End Using
            End Using
        End SyncLock
    End Sub

    Public Shared Function ReadOutages() As IReadOnlyList(Of PowerEventRecord)
        Dim result As New List(Of PowerEventRecord)()
        Using connection = PostgresDatabase.Open(), command As New NpgsqlCommand(
            "SELECT start_utc, end_utc FROM ecg_outage ORDER BY start_utc DESC", connection)
            Using reader = command.ExecuteReader()
                While reader.Read()
                    Dim start = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc)
                    Dim finish As DateTime? = If(reader.IsDBNull(1), CType(Nothing, DateTime?), DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc))
                    result.Add(New PowerEventRecord With {.Id = start.Ticks.ToString(), .Scope = "ECG", .StartUtc = start, .ConfirmedUtc = start,
                        .LastObservedUtc = If(finish, start), .EndUtc = finish, .EndReason = If(finish.HasValue, "ECG back", String.Empty),
                        .Evidence = "All four transformers off"})
                End While
            End Using
        End Using
        Return result
    End Function

    Public Structure PeriodRow
        Public Start As Date
        Public Outages As Integer
        Public TotalSeconds As Long
        Public LongestSeconds As Integer
    End Structure

    Public Shared Function ReadPeriods(kind As String) As IReadOnlyList(Of PeriodRow)
        Dim result As New List(Of PeriodRow)()
        Using connection = PostgresDatabase.Open(), command As New NpgsqlCommand(
            "SELECT period_start, outages, total_seconds, longest_seconds FROM ecg_outage_period WHERE period_kind = @kind ORDER BY period_start DESC LIMIT 400", connection)
            command.Parameters.AddWithValue("kind", kind)
            Using reader = command.ExecuteReader()
                While reader.Read()
                    result.Add(New PeriodRow With {.Start = reader.GetFieldValue(Of DateOnly)(0).ToDateTime(TimeOnly.MinValue), .Outages = reader.GetInt32(1),
                        .TotalSeconds = reader.GetInt64(2), .LongestSeconds = reader.GetInt32(3)})
                End While
            End Using
        End Using
        Return result
    End Function
End Class
