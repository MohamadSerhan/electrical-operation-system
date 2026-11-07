Imports System.Globalization
Imports Npgsql

' Permanent energy accounting. Active energy is integrated from the recorded kW (transformers: PostgreSQL measurement_samples,
' UPS: ups_samples) and stored per equipment and per shift in energy_shift (A = 06:00-18:00, B = 18:00-06:00 local time).
' Daily, weekly, monthly and yearly figures are sums of those shift rows (views energy_daily/weekly/monthly/yearly).
' The job is idempotent: it recomputes whole shifts from the raw samples, and a finished shift is never replaced by a smaller one.
Public NotInheritable Class EnergyAccounting
    Private Sub New()
    End Sub

    Public Const ShiftSeconds As Integer = 12 * 3600
    Private Const TransformerGapSeconds As Integer = 60
    Private Const UpsGapSeconds As Integer = 150
    Private Shared ReadOnly Gate As New Object()

    Private Const RecomputeSql As String =
        "WITH bounds AS (SELECT ((@from::timestamp + interval '6 hours') AT TIME ZONE @tz) AS from_utc), " &
        "tr AS (SELECT id, t, CASE WHEN g <= @tgap THEN (kw + CASE WHEN nkw < 1e9 THEN nkw ELSE kw END) / 2.0 * g / 3600.0 ELSE 0 END AS kwh, " &
        "  CASE WHEN g <= @tgap THEN g ELSE 0 END AS secs FROM (" &
        "  SELECT analyzer_id AS id, captured_at AS t, vals[11] AS kw, LEAD(vals[11]) OVER w AS nkw, " &
        "  EXTRACT(EPOCH FROM LEAD(captured_at) OVER w - captured_at) AS g FROM measurement_samples " &
        "  WHERE analyzer_id IN ('T1','T2','T3','T4') AND captured_at >= (SELECT from_utc FROM bounds) " &
        "  WINDOW w AS (PARTITION BY analyzer_id ORDER BY captured_at)) m WHERE g IS NOT NULL AND kw IS NOT NULL AND kw >= 0 AND kw < 1e9), " &
        "bank_n AS (SELECT ats_bank, COUNT(DISTINCT device_id) AS n FROM ups_samples GROUP BY ats_bank), " &
        "up AS (SELECT id, bank, t, CASE WHEN g <= @ugap THEN kw * g / 3600.0 ELSE 0 END AS kwh, CASE WHEN g <= @ugap THEN g ELSE 0 END AS secs FROM (" &
        "  SELECT device_id AS id, ats_bank AS bank, sampled_utc AS t, output_kw AS kw, quality, " &
        "  EXTRACT(EPOCH FROM LEAD(sampled_utc) OVER w - sampled_utc) AS g FROM ups_samples WHERE sampled_utc >= (SELECT from_utc FROM bounds) " &
        "  WINDOW w AS (PARTITION BY device_id ORDER BY sampled_utc)) u WHERE g IS NOT NULL AND quality = 'GOOD' AND kw IS NOT NULL AND kw >= 0 AND kw < 1e9), " &
        "src AS (" &
        "  SELECT 'Transformer' AS ty, id, t, kwh, secs, 1.0 AS w FROM tr " &
        "  UNION ALL SELECT 'Total', 'TOTAL', t, kwh, secs / 4.0, 0.25 FROM tr " &
        "  UNION ALL SELECT 'UPS', id, t, kwh, secs, 1.0 FROM up " &
        "  UNION ALL SELECT 'ATS', 'ATS ' || up.bank, t, kwh, secs / COALESCE(NULLIF(bank_n.n, 0), 1), 1.0 / COALESCE(NULLIF(bank_n.n, 0), 1) " &
        "    FROM up LEFT JOIN bank_n ON bank_n.ats_bank = up.bank), " &
        "b AS (SELECT ty, id, t, kwh, secs, w, ((t AT TIME ZONE @tz) - interval '6 hours')::date AS sd, " &
        "  CASE WHEN (t AT TIME ZONE @tz)::time >= time '06:00' AND (t AT TIME ZONE @tz)::time < time '18:00' THEN 'A' ELSE 'B' END AS sh FROM src) " &
        "INSERT INTO energy_shift (equipment_type, equipment_id, shift_date, shift, kwh, covered_seconds, samples, first_utc, last_utc, complete, updated_at) " &
        "SELECT ty, id, sd, sh, SUM(kwh), ROUND(SUM(secs))::int, ROUND(SUM(w))::int, MIN(t), MAX(t), " &
        "  ((sd::timestamp + CASE WHEN sh = 'A' THEN interval '18 hours' ELSE interval '30 hours' END) AT TIME ZONE @tz) <= now(), now() " &
        "FROM b GROUP BY ty, id, sd, sh " &
        "ON CONFLICT (equipment_type, equipment_id, shift_date, shift) DO UPDATE SET kwh = EXCLUDED.kwh, covered_seconds = EXCLUDED.covered_seconds, " &
        "  samples = EXCLUDED.samples, first_utc = EXCLUDED.first_utc, last_utc = EXCLUDED.last_utc, complete = EXCLUDED.complete, updated_at = now() " &
        "WHERE NOT energy_shift.complete OR EXCLUDED.samples >= energy_shift.samples"

    Private Const StartDateSql As String =
        "SELECT CASE WHEN EXISTS (SELECT 1 FROM energy_shift) THEN ((now() AT TIME ZONE @tz) - interval '6 hours')::date - @back " &
        "ELSE (SELECT ((MIN(captured_at) AT TIME ZONE @tz) - interval '6 hours')::date FROM measurement_samples) END"

    ' IANA name of the machine zone so PostgreSQL applies the same local shift boundaries (including DST) as the operators see.
    Public Shared Function LocalZoneName(connection As NpgsqlConnection) As String
        Dim candidate As String = Nothing
        Try
            If Not TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, candidate) Then candidate = Nothing
        Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is PlatformNotSupportedException OrElse TypeOf ex Is TimeZoneNotFoundException
            candidate = Nothing
        End Try
        If candidate Is Nothing Then Return "UTC"
        Using command As New NpgsqlCommand("SELECT 1 FROM pg_timezone_names WHERE name = @name", connection)
            command.Parameters.AddWithValue("name", candidate)
            Return If(command.ExecuteScalar() Is Nothing, "UTC", candidate)
        End Using
    End Function

    ' Recomputes the last few days (everything on the first run). Safe to call repeatedly.
    Public Shared Sub Recompute(Optional lookbackDays As Integer = 3)
        SyncLock Gate
            Using connection = PostgresDatabase.Open()
                Dim zone = LocalZoneName(connection)
                Dim startDate As Date
                Using command As New NpgsqlCommand(StartDateSql, connection)
                    command.Parameters.AddWithValue("tz", zone)
                    command.Parameters.AddWithValue("back", lookbackDays)
                    Dim value = command.ExecuteScalar()
                    If value Is Nothing OrElse value Is DBNull.Value Then Return
                    startDate = DirectCast(value, DateOnly).ToDateTime(TimeOnly.MinValue)
                End Using
                Using command As New NpgsqlCommand(RecomputeSql, connection) With {.CommandTimeout = 300}
                    command.Parameters.AddWithValue("from", NpgsqlTypes.NpgsqlDbType.Date, startDate)
                    command.Parameters.AddWithValue("tz", zone)
                    command.Parameters.AddWithValue("tgap", CDbl(TransformerGapSeconds))
                    command.Parameters.AddWithValue("ugap", CDbl(UpsGapSeconds))
                    command.ExecuteNonQuery()
                End Using
            End Using
        End SyncLock
    End Sub

    Public Structure EnergyCell
        Public Period As Date
        Public EquipmentType As String
        Public EquipmentId As String
        Public Shift As String
        Public Kwh As Double
        Public CoveredSeconds As Long
        Public Complete As Boolean
    End Structure

    Public Shared ReadOnly Units As String() = {"day", "week", "month", "year"}

    ' One row per period, equipment and shift for the chosen unit, newest first.
    Public Shared Function ReadCells(unit As String, equipmentTypes As String(), periods As Integer) As IReadOnlyList(Of EnergyCell)
        If Array.IndexOf(Units, unit) < 0 Then Throw New ArgumentException("Unknown period unit.", NameOf(unit))
        Dim result As New List(Of EnergyCell)()
        Using connection = PostgresDatabase.Open(), command As New NpgsqlCommand(
            "SELECT date_trunc('" & unit & "', shift_date)::date AS p, equipment_type, equipment_id, shift, SUM(kwh), SUM(covered_seconds), bool_and(complete) " &
            "FROM energy_shift WHERE equipment_type = ANY(@types) AND date_trunc('" & unit & "', shift_date)::date >= " &
            "(date_trunc('" & unit & "', (SELECT MAX(shift_date) FROM energy_shift)) - (@periods || ' " & unit & "')::interval)::date " &
            "GROUP BY 1, 2, 3, 4 ORDER BY 1 DESC", connection)
            command.Parameters.AddWithValue("types", equipmentTypes)
            command.Parameters.AddWithValue("periods", (periods - 1).ToString(CultureInfo.InvariantCulture))
            Using reader = command.ExecuteReader()
                While reader.Read()
                    result.Add(New EnergyCell With {.Period = reader.GetFieldValue(Of DateOnly)(0).ToDateTime(TimeOnly.MinValue), .EquipmentType = reader.GetString(1), .EquipmentId = reader.GetString(2),
                        .Shift = reader.GetString(3).Trim(), .Kwh = reader.GetDouble(4), .CoveredSeconds = reader.GetInt64(5), .Complete = reader.GetBoolean(6)})
                End While
            End Using
        End Using
        Return result
    End Function

    Public Shared Function PeriodLabel(unit As String, start As Date) As String
        Select Case unit
            Case "day" : Return start.ToString("ddd dd MMM yyyy", CultureInfo.InvariantCulture)
            Case "week" : Return "Week of " & start.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)
            Case "month" : Return start.ToString("MMMM yyyy", CultureInfo.InvariantCulture)
            Case Else : Return start.ToString("yyyy", CultureInfo.InvariantCulture)
        End Select
    End Function

    ' Share of the finished shifts of a period that actually have recorded samples (holes in the recording are not energy).
    Public Shared Function CoverageText(cells As IEnumerable(Of EnergyCell)) As String
        Dim finished = cells.Where(Function(cell) cell.Complete).ToArray()
        If finished.Length = 0 Then Return "in progress"
        Dim covered = finished.Sum(Function(cell) CDbl(cell.CoveredSeconds))
        Return Math.Min(100.0R, covered / (finished.Length * CDbl(ShiftSeconds)) * 100.0R).ToString("0.0", CultureInfo.InvariantCulture) & " %"
    End Function
End Class
