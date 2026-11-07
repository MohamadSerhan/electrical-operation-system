Imports System.Globalization
Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Text
Imports System.Threading
Imports System.Xml
Imports Npgsql

' Daily report workbooks per equipment group. Everything is read from the saved raw recording
' (measurement_samples, ups_samples, equipment_alarm_history) and the permanent energy and outage tables.
' Each unit gets one row per minute: the minute's average, minimum and maximum, plus any alarm or peak inside it.
Public NotInheritable Class DailyGroupReports
    Public Shared ReadOnly Groups As String() = {"Transformers", "UPS", "Generators", "Loads"}
    Private Shared ReadOnly Transformers As String() = {"T1", "T2", "T3", "T4", "TOTAL"}

    Private Sub New()
    End Sub

    Public Shared Function GetPath(root As String, group As String, localDay As Date) As String
        Dim stamp = localDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        Return Path.Combine(root, "DailyReports", stamp, group & "_" & stamp & ".xlsx")
    End Function

    ' Transformers and UPS need saved samples in the day; generators and loads have no data source yet.
    Public Shared Function HasData(group As String, startUtc As DateTime, endUtc As DateTime) As Boolean
        If group = "Generators" OrElse group = "Loads" Then Return True
        Dim sql = If(group = "UPS",
            "SELECT EXISTS (SELECT 1 FROM ups_samples WHERE sampled_utc >= @s AND sampled_utc < @e)",
            "SELECT EXISTS (SELECT 1 FROM measurement_samples WHERE captured_at >= @s AND captured_at < @e)")
        Using connection = PostgresDatabase.Open(), command As New NpgsqlCommand(sql, connection)
            command.Parameters.AddWithValue("s", startUtc)
            command.Parameters.AddWithValue("e", endUtc)
            Return Convert.ToBoolean(command.ExecuteScalar(), CultureInfo.InvariantCulture)
        End Using
    End Function

    Public Shared Function Export(group As String, localDay As Date, timezone As TimeZoneInfo, root As String,
                                  cancellationToken As CancellationToken) As String
        Dim finalPath = GetPath(root, group, localDay)
        If File.Exists(finalPath) Then Throw New IOException("Daily report already exists; refusing to overwrite: " & finalPath)
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath))
        Dim startUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDay, DateTimeKind.Unspecified), timezone)
        Dim endUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localDay.AddDays(1), DateTimeKind.Unspecified), timezone)
        Dim context As New Context With {.Group = group, .Day = localDay, .Zone = timezone, .StartUtc = startUtc, .EndUtc = endUtc, .Token = cancellationToken}
        Dim book As New XlsxBook()
        Select Case group
            Case "Transformers" : BuildTransformers(book, context)
            Case "UPS" : BuildUps(book, context)
            Case Else : BuildNotConnected(book, context)
        End Select
        Dim temporary = finalPath & "." & Guid.NewGuid().ToString("N") & ".partial"
        Try
            book.Save(temporary)
            Validate(temporary)
            If File.Exists(finalPath) Then Throw New IOException("Daily report collision before promotion: " & finalPath)
            File.Move(temporary, finalPath)
        Catch
            If File.Exists(temporary) Then File.Delete(temporary)
            Throw
        End Try
        Return finalPath
    End Function

    Public Shared Sub Validate(path As String)
        Using archive = ZipFile.OpenRead(path)
            For Each part In {"[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels", "xl/styles.xml", "xl/worksheets/sheet1.xml"}
                Dim entry = archive.GetEntry(part)
                If entry Is Nothing Then Throw New InvalidDataException("Daily report is missing " & part & ".")
                Using stream = entry.Open()
                    Dim document As New XmlDocument()
                    document.Load(stream)
                End Using
            Next
        End Using
    End Sub

    Private NotInheritable Class Context
        Public Group As String
        Public Day As Date
        Public Zone As TimeZoneInfo
        Public StartUtc As DateTime
        Public EndUtc As DateTime
        Public Token As CancellationToken

        Public Function Local(utc As DateTime) As String
            Return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        End Function

        Public Function LocalSeconds(utc As DateTime) As String
            Return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        End Function

        Public Iterator Function Minutes() As IEnumerable(Of DateTime)
            Dim cursor = StartUtc
            While cursor < EndUtc
                Yield cursor
                cursor = cursor.AddMinutes(1)
            End While
        End Function
    End Class

    Private NotInheritable Class AlarmEvent
        Public TimeUtc As DateTime
        Public EquipmentId As String
        Public Kind As String
        Public Name As String
        Public Detail As String
        Public Severity As String
    End Class

    Private Shared Function Num(reader As NpgsqlDataReader, ordinal As Integer, Optional digits As Integer = 2) As Object
        If reader.IsDBNull(ordinal) Then Return Nothing
        Dim value = Convert.ToDouble(reader.GetValue(ordinal), CultureInfo.InvariantCulture)
        If Double.IsNaN(value) OrElse Double.IsInfinity(value) Then Return Nothing
        Return Math.Round(value, digits)
    End Function

    Private Shared Function MinuteKey(utc As DateTime) As Long
        Return utc.Ticks \ TimeSpan.TicksPerMinute
    End Function

    Private Shared Function ReadAlarms(context As Context, equipmentType As String) As List(Of AlarmEvent)
        Dim rows As New List(Of AlarmEvent)()
        Try
        Using connection = PostgresDatabase.Open(), command As New NpgsqlCommand(
            "SELECT observed_utc, equipment_id, event_kind, alarm_key, current_state->>'Name', current_state->>'Detail', current_state->>'Severity' " &
            "FROM equipment_alarm_history WHERE equipment_type = @t AND observed_utc >= @s AND observed_utc < @e " &
            "ORDER BY observed_utc, equipment_id, alarm_key", connection)
            command.Parameters.AddWithValue("t", equipmentType)
            command.Parameters.AddWithValue("s", context.StartUtc)
            command.Parameters.AddWithValue("e", context.EndUtc)
            Using reader = command.ExecuteReader()
                While reader.Read()
                    context.Token.ThrowIfCancellationRequested()
                    Dim kind = reader.GetString(2)
                    rows.Add(New AlarmEvent With {
                        .TimeUtc = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc),
                        .EquipmentId = reader.GetString(1),
                        .Kind = If(kind = "ObservedRaised", "RAISED", If(kind = "ObservedCleared", "CLEARED", "ACTIVE")),
                        .Name = If(reader.IsDBNull(4), reader.GetString(3), reader.GetString(4)),
                        .Detail = If(reader.IsDBNull(5), String.Empty, reader.GetString(5)),
                        .Severity = If(reader.IsDBNull(6), String.Empty, reader.GetString(6))
                    })
                End While
            End Using
        End Using
        Catch ex As PostgresException When ex.SqlState = PostgresErrorCodes.UndefinedTable
        End Try
        Return rows
    End Function

    Private Shared Function AlarmFlags(alarms As IEnumerable(Of AlarmEvent)) As Dictionary(Of String, List(Of String))
        Dim flags As New Dictionary(Of String, List(Of String))(StringComparer.Ordinal)
        For Each item In alarms
            Dim key = item.EquipmentId & "|" & MinuteKey(item.TimeUtc).ToString(CultureInfo.InvariantCulture)
            If Not flags.ContainsKey(key) Then flags(key) = New List(Of String)()
            flags(key).Add(item.Kind & " " & item.Name)
        Next
        Return flags
    End Function

    Private Shared Function JoinFlags(parts As IEnumerable(Of String)) As String
        Return String.Join("; ", parts.Distinct())
    End Function

    Private Shared Function Energy(context As Context, equipmentType As String) As Dictionary(Of String, Double())
        ' value: shift A, shift B, 1 when any shift of the day is still open.
        Dim result As New Dictionary(Of String, Double())(StringComparer.Ordinal)
        Try
        Using connection = PostgresDatabase.Open(), command As New NpgsqlCommand(
            "SELECT equipment_id, shift, kwh, complete FROM energy_shift WHERE equipment_type = @t AND shift_date = @d", connection)
            command.Parameters.AddWithValue("t", equipmentType)
            command.Parameters.Add(New NpgsqlParameter("d", NpgsqlTypes.NpgsqlDbType.Date) With {.Value = DateOnly.FromDateTime(context.Day)})
            Using reader = command.ExecuteReader()
                While reader.Read()
                    Dim id = reader.GetString(0)
                    If Not result.ContainsKey(id) Then result(id) = New Double(2) {}
                    result(id)(If(reader.GetString(1) = "A", 0, 1)) = reader.GetDouble(2)
                    If Not reader.GetBoolean(3) Then result(id)(2) = 1
                End While
            End Using
        End Using
        Catch ex As PostgresException When ex.SqlState = PostgresErrorCodes.UndefinedTable
        End Try
        Return result
    End Function

    Private Shared Sub AddTitle(sheet As XlsxSheet, title As String, context As Context)
        sheet.AddRow(True, title)
        sheet.AddRow(False, "Report day (factory local time)", context.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        sheet.AddRow(False, "Time zone", context.Zone.Id)
        sheet.AddRow(False, "Source", "Saved raw recording in PostgreSQL; one row per minute (average, min, max); empty cells mean no valid reading, never zero")
        sheet.AddRow(False, "Shift A / Shift B", "06:00-18:00 / 18:00-06:00")
        sheet.AddRow(False)
    End Sub

    Private Shared Sub BuildTransformers(book As XlsxBook, context As Context)
        Dim summary = book.AddSheet("Summary")
        AddTitle(summary, "Transformers daily report", context)
        Dim alarms = ReadAlarms(context, "Transformer")
        Dim flags = AlarmFlags(alarms)
        Dim energyByUnit = Energy(context, "Transformer")
        For Each total In Energy(context, "Total")
            energyByUnit(total.Key) = total.Value
        Next

        summary.AddRow(True, "Unit", "kWh Shift A", "kWh Shift B", "kWh total", "Energy final", "Peak kW", "Peak time", "Lowest phase V", "Highest phase V", "Minutes with data", "Minutes without data", "Alarm events")
        Dim sheets As New List(Of XlsxSheet)()
        For Each analyzerId In Transformers
            context.Token.ThrowIfCancellationRequested()
            Dim sheet = book.AddSheet(analyzerId)
            sheet.Freeze = 1
            sheet.AddRow(True, "Local time", "Samples", "Avg voltage L-N (V)", "Lowest phase V", "Highest phase V", "Frequency (Hz)", "Avg current (A)", "Highest phase current (A)",
                         "Avg kW", "Min kW", "Max kW", "kVA", "Power factor", "Status / events")
            Dim byMinute As New Dictionary(Of Long, Object())()
            Using connection = PostgresDatabase.Open(), command As New NpgsqlCommand(
                "SELECT date_trunc('minute', captured_at), count(*), avg(vals[4]), min(LEAST(vals[1],vals[2],vals[3])), max(GREATEST(vals[1],vals[2],vals[3]))," &
                " avg(vals[9]), avg(vals[10]), max(GREATEST(vals[12],vals[13],vals[14])), avg(vals[11]), min(vals[11]), max(vals[11]), avg(vals[17]), avg(vals[18]) " &
                "FROM measurement_samples WHERE analyzer_id = @a AND captured_at >= @s AND captured_at < @e GROUP BY 1", connection)
                command.Parameters.AddWithValue("a", analyzerId)
                command.Parameters.AddWithValue("s", context.StartUtc)
                command.Parameters.AddWithValue("e", context.EndUtc)
                Using reader = command.ExecuteReader()
                    While reader.Read()
                        byMinute(MinuteKey(DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc))) = New Object() {
                            Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture), Num(reader, 2, 1), Num(reader, 3, 1), Num(reader, 4, 1),
                            Num(reader, 5, 2), Num(reader, 6, 1), Num(reader, 7, 1), Num(reader, 8, 1), Num(reader, 9, 1), Num(reader, 10, 1), Num(reader, 11, 2), Num(reader, 12, 3)}
                    End While
                End Using
            End Using

            Dim peakKw = Double.MinValue
            Dim peakKey = -1L
            For Each values In byMinute
                If values.Value(9) IsNot Nothing AndAlso CDbl(values.Value(9)) > peakKw Then
                    peakKw = CDbl(values.Value(9))
                    peakKey = values.Key
                End If
            Next

            Dim withData = 0
            Dim lowest = Double.MaxValue
            Dim highest = Double.MinValue
            Dim alarmCount = alarms.Where(Function(item) item.EquipmentId = analyzerId AndAlso item.Kind <> "ACTIVE").Count()
            For Each slot In context.Minutes()
                Dim key = MinuteKey(slot)
                Dim events As New List(Of String)()
                Dim flagKey = analyzerId & "|" & key.ToString(CultureInfo.InvariantCulture)
                If flags.ContainsKey(flagKey) Then events.AddRange(flags(flagKey))
                Dim values As Object() = Nothing
                If byMinute.TryGetValue(key, values) Then
                    withData += 1
                    If values(2) IsNot Nothing Then lowest = Math.Min(lowest, CDbl(values(2)))
                    If values(3) IsNot Nothing Then highest = Math.Max(highest, CDbl(values(3)))
                    If key = peakKey Then events.Insert(0, "PEAK kW")
                    sheet.AddRow(False, context.Local(slot), values(0), values(1), values(2), values(3), values(4), values(5), values(6), values(7), values(8), values(9), values(10), values(11),
                                 If(events.Count = 0, "OK", JoinFlags(events)))
                Else
                    events.Insert(0, "NO DATA")
                    sheet.AddRow(False, context.Local(slot), Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, JoinFlags(events))
                End If
            Next

            Dim shiftEnergy As Double() = Nothing
            energyByUnit.TryGetValue(analyzerId, shiftEnergy)
            summary.AddRow(False, analyzerId,
                           If(shiftEnergy Is Nothing, Nothing, CObj(Math.Round(shiftEnergy(0), 1))), If(shiftEnergy Is Nothing, Nothing, CObj(Math.Round(shiftEnergy(1), 1))),
                           If(shiftEnergy Is Nothing, Nothing, CObj(Math.Round(shiftEnergy(0) + shiftEnergy(1), 1))),
                           If(shiftEnergy Is Nothing, "no energy rows", If(shiftEnergy(2) = 1, "open", "final")),
                           If(peakKey < 0, Nothing, CObj(peakKw)), If(peakKey < 0, Nothing, context.Local(New DateTime(peakKey * TimeSpan.TicksPerMinute, DateTimeKind.Utc))),
                           If(withData = 0, Nothing, CObj(lowest)), If(withData = 0, Nothing, CObj(highest)),
                           withData, context.Minutes().Count() - withData, alarmCount)
        Next

        Dim outages = book.AddSheet("ECG outages")
        outages.Freeze = 1
        outages.AddRow(True, "ECG off (local)", "ECG back (local)", "Duration (min)", "Note")
        Dim outageCount = 0
        Dim outageSeconds = 0.0
        Dim longest = 0.0
        Try
        Using connection = PostgresDatabase.Open(), command As New NpgsqlCommand(
            "SELECT start_utc, end_utc FROM ecg_outage WHERE start_utc < @e AND (end_utc IS NULL OR end_utc >= @s) ORDER BY start_utc", connection)
            command.Parameters.AddWithValue("s", context.StartUtc)
            command.Parameters.AddWithValue("e", context.EndUtc)
            Using reader = command.ExecuteReader()
                While reader.Read()
                    Dim startedUtc = DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc)
                    Dim endedUtc = If(reader.IsDBNull(1), CType(Nothing, DateTime?), DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc))
                    Dim clippedStart = If(startedUtc < context.StartUtc, context.StartUtc, startedUtc)
                    Dim clippedEnd = If(endedUtc.HasValue AndAlso endedUtc.Value < context.EndUtc, endedUtc.Value, context.EndUtc)
                    Dim seconds = Math.Max(0.0, (clippedEnd - clippedStart).TotalSeconds)
                    outageCount += 1
                    outageSeconds += seconds
                    longest = Math.Max(longest, seconds)
                    Dim note = If(startedUtc < context.StartUtc, "Started before this day; counted from 00:00", If(Not endedUtc.HasValue OrElse endedUtc.Value >= context.EndUtc, "Continues after this day; counted to 24:00", String.Empty))
                    outages.AddRow(False, context.LocalSeconds(startedUtc), If(endedUtc.HasValue, context.LocalSeconds(endedUtc.Value), "still off"), Math.Round(seconds / 60.0, 1), note)
                End While
            End Using
        End Using
        Catch ex As PostgresException When ex.SqlState = PostgresErrorCodes.UndefinedTable
        End Try
        summary.AddRow(False)
        summary.AddRow(True, "ECG outages this day", "Count", "Total (min)", "Longest (min)")
        summary.AddRow(False, "ECG", outageCount, Math.Round(outageSeconds / 60.0, 1), Math.Round(longest / 60.0, 1))
        If outageCount = 0 Then outages.AddRow(False, "No ECG outage in this day")

        WriteEvents(book, context, alarms)
    End Sub

    Private Shared Sub WriteEvents(book As XlsxBook, context As Context, alarms As List(Of AlarmEvent))
        Dim sheet = book.AddSheet("Events")
        sheet.Freeze = 1
        sheet.AddRow(True, "Local time", "Unit", "Event", "Alarm", "Detail", "Severity")
        For Each item In alarms
            sheet.AddRow(False, context.LocalSeconds(item.TimeUtc), item.EquipmentId, item.Kind, item.Name, item.Detail, item.Severity)
        Next
        If alarms.Count = 0 Then sheet.AddRow(False, "No alarm event in this day")
    End Sub

    Private Shared Sub BuildUps(book As XlsxBook, context As Context)
        Dim summary = book.AddSheet("Summary")
        AddTitle(summary, "UPS daily report", context)
        Dim alarms = ReadAlarms(context, "UPS")
        Dim flags = AlarmFlags(alarms)
        Dim upsEnergy = Energy(context, "UPS")
        Dim atsEnergy = Energy(context, "ATS")

        Dim devices As New List(Of String())()
        Using connection = PostgresDatabase.Open(), command As New NpgsqlCommand(
            "SELECT device_id, COALESCE(max(device_label), device_id), COALESCE(max(ats_bank)::text, ''), COALESCE(max(device_model), '') " &
            "FROM ups_samples WHERE sampled_utc >= @s AND sampled_utc < @e GROUP BY device_id ORDER BY device_id", connection)
            command.Parameters.AddWithValue("s", context.StartUtc)
            command.Parameters.AddWithValue("e", context.EndUtc)
            Using reader = command.ExecuteReader()
                While reader.Read()
                    devices.Add({reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)})
                End While
            End Using
        End Using

        summary.AddRow(True, "UPS", "ATS", "Model", "kWh Shift A", "kWh Shift B", "kWh total", "Energy final", "Peak kW", "Peak time", "Minutes good", "Minutes not good / no data", "Alarm events")
        Dim usedNames As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {"Summary", "Events"}
        For Each device In devices
            context.Token.ThrowIfCancellationRequested()
            Dim sheetName = device(1)
            If Not usedNames.Add(sheetName) Then sheetName = device(0)
            usedNames.Add(sheetName)
            Dim sheet = book.AddSheet(sheetName)
            sheet.Freeze = 1
            sheet.AddRow(True, "Local time", "Good samples", "Avg kW", "Max kW", "Max load %", "Output V lowest", "Output V highest", "Output Hz", "Input V lowest",
                         "Battery % lowest", "Battery runtime min (lowest)", "Seconds on battery (max)", "Output source (last)", "Battery status (last)", "Status / events")
            Dim byMinute As New Dictionary(Of Long, Object())()
            Using connection = PostgresDatabase.Open(), command As New NpgsqlCommand(
                "SELECT date_trunc('minute', sampled_utc), count(*) FILTER (WHERE quality = 'GOOD'), string_agg(DISTINCT quality, '/') FILTER (WHERE quality <> 'GOOD')," &
                " avg(output_kw) FILTER (WHERE quality = 'GOOD'), max(output_kw) FILTER (WHERE quality = 'GOOD'), max(load_max_pct) FILTER (WHERE quality = 'GOOD')," &
                " min(LEAST(out_v_l1, out_v_l2, out_v_l3)) FILTER (WHERE quality = 'GOOD'), max(GREATEST(out_v_l1, out_v_l2, out_v_l3)) FILTER (WHERE quality = 'GOOD')," &
                " avg(out_freq_hz) FILTER (WHERE quality = 'GOOD'), min(LEAST(in_v_l1, in_v_l2, in_v_l3)) FILTER (WHERE quality = 'GOOD')," &
                " min(battery_charge_pct) FILTER (WHERE quality = 'GOOD'), min(battery_runtime_min) FILTER (WHERE quality = 'GOOD'), max(seconds_on_battery) FILTER (WHERE quality = 'GOOD')," &
                " (array_agg(output_source ORDER BY sampled_utc DESC) FILTER (WHERE quality = 'GOOD'))[1], (array_agg(battery_status ORDER BY sampled_utc DESC) FILTER (WHERE quality = 'GOOD'))[1] " &
                "FROM ups_samples WHERE device_id = @d AND sampled_utc >= @s AND sampled_utc < @e GROUP BY 1", connection)
                command.Parameters.AddWithValue("d", device(0))
                command.Parameters.AddWithValue("s", context.StartUtc)
                command.Parameters.AddWithValue("e", context.EndUtc)
                Using reader = command.ExecuteReader()
                    While reader.Read()
                        byMinute(MinuteKey(DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc))) = New Object() {
                            Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture), If(reader.IsDBNull(2), String.Empty, reader.GetString(2)),
                            Num(reader, 3, 1), Num(reader, 4, 1), Num(reader, 5, 0), Num(reader, 6, 0), Num(reader, 7, 0), Num(reader, 8, 1), Num(reader, 9, 0),
                            Num(reader, 10, 0), Num(reader, 11, 0), Num(reader, 12, 0), Num(reader, 13, 0), Num(reader, 14, 0)}
                    End While
                End Using
            End Using

            Dim peakKw = Double.MinValue
            Dim peakKey = -1L
            For Each values In byMinute
                If values.Value(3) IsNot Nothing AndAlso CDbl(values.Value(3)) > peakKw Then
                    peakKw = CDbl(values.Value(3))
                    peakKey = values.Key
                End If
            Next

            Dim good = 0
            Dim total = 0
            For Each slot In context.Minutes()
                total += 1
                Dim key = MinuteKey(slot)
                Dim events As New List(Of String)()
                Dim flagKey = device(0) & "|" & key.ToString(CultureInfo.InvariantCulture)
                If flags.ContainsKey(flagKey) Then events.AddRange(flags(flagKey))
                Dim values As Object() = Nothing
                If byMinute.TryGetValue(key, values) AndAlso CInt(values(0)) > 0 Then
                    good += 1
                    If key = peakKey Then events.Insert(0, "PEAK kW")
                    sheet.AddRow(False, context.Local(slot), values(0), values(2), values(3), values(4), values(5), values(6), values(7), values(8), values(9), values(10), values(11), values(12), values(13),
                                 If(events.Count = 0, "OK", JoinFlags(events)))
                Else
                    Dim reason = If(values IsNot Nothing AndAlso CStr(values(1)).Length > 0, "NOT GOOD: " & CStr(values(1)), "NO DATA")
                    events.Insert(0, reason)
                    sheet.AddRow(False, context.Local(slot), 0, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, Nothing, JoinFlags(events))
                End If
            Next

            Dim shiftEnergy As Double() = Nothing
            upsEnergy.TryGetValue(device(0), shiftEnergy)
            summary.AddRow(False, device(1), device(2), device(3),
                           If(shiftEnergy Is Nothing, Nothing, CObj(Math.Round(shiftEnergy(0), 1))), If(shiftEnergy Is Nothing, Nothing, CObj(Math.Round(shiftEnergy(1), 1))),
                           If(shiftEnergy Is Nothing, Nothing, CObj(Math.Round(shiftEnergy(0) + shiftEnergy(1), 1))),
                           If(shiftEnergy Is Nothing, "no good readings", If(shiftEnergy(2) = 1, "open", "final")),
                           If(peakKey < 0, Nothing, CObj(peakKw)), If(peakKey < 0, Nothing, context.Local(New DateTime(peakKey * TimeSpan.TicksPerMinute, DateTimeKind.Utc))),
                           good, total - good, alarms.Where(Function(item) item.EquipmentId = device(0) AndAlso item.Kind <> "ACTIVE").Count())
        Next

        summary.AddRow(False)
        summary.AddRow(True, "ATS (sum of its UPS)", "kWh Shift A", "kWh Shift B", "kWh total", "Energy final")
        For Each ats In atsEnergy.OrderBy(Function(item) item.Key, StringComparer.Ordinal)
            summary.AddRow(False, ats.Key, Math.Round(ats.Value(0), 1), Math.Round(ats.Value(1), 1), Math.Round(ats.Value(0) + ats.Value(1), 1), If(ats.Value(2) = 1, "open", "final"))
        Next
        WriteEvents(book, context, alarms)
    End Sub

    Private Shared Sub BuildNotConnected(book As XlsxBook, context As Context)
        Dim sheet = book.AddSheet("Summary")
        AddTitle(sheet, context.Group & " daily report", context)
        sheet.AddRow(True, "Status")
        sheet.AddRow(False, context.Group & " have no data source connected to the system yet, so nothing was recorded or estimated.")
        sheet.AddRow(False, "When a meter or source is connected for them, this file will contain the same slot-by-slot layout as the transformers.")
    End Sub

    Private NotInheritable Class XlsxSheet
        Public ReadOnly Name As String
        Public Freeze As Integer
        Public ReadOnly Rows As New List(Of KeyValuePair(Of Boolean, Object()))()
        Public MaxColumns As Integer

        Public Sub New(name As String)
            Me.Name = name
        End Sub

        Public Sub AddRow(bold As Boolean, ParamArray cells As Object())
            Dim values = If(cells, New Object() {Nothing})
            MaxColumns = Math.Max(MaxColumns, values.Length)
            Rows.Add(New KeyValuePair(Of Boolean, Object())(bold, values))
        End Sub
    End Class

    Private NotInheritable Class XlsxBook
        Private ReadOnly _sheets As New List(Of XlsxSheet)()

        Public Function AddSheet(name As String) As XlsxSheet
            Dim clean As New StringBuilder()
            For Each character In name
                clean.Append(If("[]:*?/\".IndexOf(character) >= 0, "-"c, character))
            Next
            Dim safe = clean.ToString()
            If safe.Length > 31 Then safe = safe.Substring(0, 31)
            Dim sheet As New XlsxSheet(safe)
            _sheets.Add(sheet)
            Return sheet
        End Function

        Public Sub Save(path As String)
            Using archive = ZipFile.Open(path, ZipArchiveMode.Create)
                WriteXml(archive, "[Content_Types].xml",
                    Sub(w)
                        w.WriteStartElement("Types", "http://schemas.openxmlformats.org/package/2006/content-types")
                        w.WriteStartElement("Default") : w.WriteAttributeString("Extension", "rels") : w.WriteAttributeString("ContentType", "application/vnd.openxmlformats-package.relationships+xml") : w.WriteEndElement()
                        w.WriteStartElement("Default") : w.WriteAttributeString("Extension", "xml") : w.WriteAttributeString("ContentType", "application/xml") : w.WriteEndElement()
                        w.WriteStartElement("Override") : w.WriteAttributeString("PartName", "/xl/workbook.xml") : w.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml") : w.WriteEndElement()
                        w.WriteStartElement("Override") : w.WriteAttributeString("PartName", "/xl/styles.xml") : w.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml") : w.WriteEndElement()
                        For index = 1 To _sheets.Count
                            w.WriteStartElement("Override") : w.WriteAttributeString("PartName", "/xl/worksheets/sheet" & index & ".xml") : w.WriteAttributeString("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml") : w.WriteEndElement()
                        Next
                        w.WriteEndElement()
                    End Sub)
                WriteXml(archive, "_rels/.rels",
                    Sub(w)
                        w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships")
                        w.WriteStartElement("Relationship") : w.WriteAttributeString("Id", "rId1") : w.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument") : w.WriteAttributeString("Target", "xl/workbook.xml") : w.WriteEndElement()
                        w.WriteEndElement()
                    End Sub)
                WriteXml(archive, "xl/workbook.xml",
                    Sub(w)
                        w.WriteStartElement("workbook", "http://schemas.openxmlformats.org/spreadsheetml/2006/main")
                        w.WriteAttributeString("xmlns", "r", Nothing, "http://schemas.openxmlformats.org/officeDocument/2006/relationships")
                        w.WriteStartElement("sheets")
                        For index = 1 To _sheets.Count
                            w.WriteStartElement("sheet") : w.WriteAttributeString("name", _sheets(index - 1).Name) : w.WriteAttributeString("sheetId", index.ToString(CultureInfo.InvariantCulture))
                            w.WriteAttributeString("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships", "rId" & index) : w.WriteEndElement()
                        Next
                        w.WriteEndElement()
                        w.WriteEndElement()
                    End Sub)
                WriteXml(archive, "xl/_rels/workbook.xml.rels",
                    Sub(w)
                        w.WriteStartElement("Relationships", "http://schemas.openxmlformats.org/package/2006/relationships")
                        For index = 1 To _sheets.Count
                            w.WriteStartElement("Relationship") : w.WriteAttributeString("Id", "rId" & index) : w.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet") : w.WriteAttributeString("Target", "worksheets/sheet" & index & ".xml") : w.WriteEndElement()
                        Next
                        w.WriteStartElement("Relationship") : w.WriteAttributeString("Id", "rId" & (_sheets.Count + 1)) : w.WriteAttributeString("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles") : w.WriteAttributeString("Target", "styles.xml") : w.WriteEndElement()
                        w.WriteEndElement()
                    End Sub)
                WriteXml(archive, "xl/styles.xml",
                    Sub(w)
                        w.WriteStartElement("styleSheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main")
                        w.WriteStartElement("fonts") : w.WriteAttributeString("count", "2")
                        w.WriteStartElement("font") : w.WriteStartElement("sz") : w.WriteAttributeString("val", "11") : w.WriteEndElement() : w.WriteStartElement("name") : w.WriteAttributeString("val", "Calibri") : w.WriteEndElement() : w.WriteEndElement()
                        w.WriteStartElement("font") : w.WriteStartElement("b") : w.WriteEndElement() : w.WriteStartElement("sz") : w.WriteAttributeString("val", "11") : w.WriteEndElement() : w.WriteStartElement("name") : w.WriteAttributeString("val", "Calibri") : w.WriteEndElement() : w.WriteEndElement()
                        w.WriteEndElement()
                        w.WriteStartElement("fills") : w.WriteAttributeString("count", "2")
                        w.WriteStartElement("fill") : w.WriteStartElement("patternFill") : w.WriteAttributeString("patternType", "none") : w.WriteEndElement() : w.WriteEndElement()
                        w.WriteStartElement("fill") : w.WriteStartElement("patternFill") : w.WriteAttributeString("patternType", "gray125") : w.WriteEndElement() : w.WriteEndElement()
                        w.WriteEndElement()
                        w.WriteStartElement("borders") : w.WriteAttributeString("count", "1") : w.WriteStartElement("border") : w.WriteEndElement() : w.WriteEndElement()
                        w.WriteStartElement("cellStyleXfs") : w.WriteAttributeString("count", "1") : w.WriteStartElement("xf") : w.WriteEndElement() : w.WriteEndElement()
                        w.WriteStartElement("cellXfs") : w.WriteAttributeString("count", "2")
                        w.WriteStartElement("xf") : w.WriteAttributeString("fontId", "0") : w.WriteEndElement()
                        w.WriteStartElement("xf") : w.WriteAttributeString("fontId", "1") : w.WriteAttributeString("applyFont", "1") : w.WriteEndElement()
                        w.WriteEndElement()
                        w.WriteEndElement()
                    End Sub)
                For index = 1 To _sheets.Count
                    Dim sheet = _sheets(index - 1)
                    WriteXml(archive, "xl/worksheets/sheet" & index & ".xml", Sub(w) WriteSheet(w, sheet))
                Next
            End Using
        End Sub

        Private Shared Sub WriteSheet(w As XmlWriter, sheet As XlsxSheet)
            w.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main")
            If sheet.Freeze > 0 Then
                w.WriteStartElement("sheetViews") : w.WriteStartElement("sheetView") : w.WriteAttributeString("workbookViewId", "0")
                w.WriteStartElement("pane") : w.WriteAttributeString("ySplit", sheet.Freeze.ToString(CultureInfo.InvariantCulture)) : w.WriteAttributeString("topLeftCell", "A" & (sheet.Freeze + 1))
                w.WriteAttributeString("activePane", "bottomLeft") : w.WriteAttributeString("state", "frozen") : w.WriteEndElement()
                w.WriteEndElement() : w.WriteEndElement()
            End If
            w.WriteStartElement("cols")
            w.WriteStartElement("col") : w.WriteAttributeString("min", "1") : w.WriteAttributeString("max", Math.Max(2, sheet.MaxColumns).ToString(CultureInfo.InvariantCulture))
            w.WriteAttributeString("width", "20") : w.WriteAttributeString("customWidth", "1") : w.WriteEndElement()
            w.WriteEndElement()
            w.WriteStartElement("sheetData")
            Dim rowNumber = 0
            For Each row In sheet.Rows
                rowNumber += 1
                w.WriteStartElement("row") : w.WriteAttributeString("r", rowNumber.ToString(CultureInfo.InvariantCulture))
                For column = 0 To row.Value.Length - 1
                    Dim value = row.Value(column)
                    If value Is Nothing Then Continue For
                    w.WriteStartElement("c") : w.WriteAttributeString("r", CellName(column) & rowNumber)
                    If row.Key Then w.WriteAttributeString("s", "1")
                    If TypeOf value Is String Then
                        w.WriteAttributeString("t", "inlineStr")
                        w.WriteStartElement("is") : w.WriteStartElement("t") : w.WriteString(Sanitize(CStr(value))) : w.WriteEndElement() : w.WriteEndElement()
                    Else
                        w.WriteStartElement("v") : w.WriteString(Convert.ToString(value, CultureInfo.InvariantCulture)) : w.WriteEndElement()
                    End If
                    w.WriteEndElement()
                Next
                w.WriteEndElement()
            Next
            w.WriteEndElement()
            w.WriteEndElement()
        End Sub

        Private Shared Function Sanitize(text As String) As String
            Dim builder As New StringBuilder(text.Length)
            For Each character In text
                If XmlConvert.IsXmlChar(character) Then builder.Append(character)
            Next
            Return builder.ToString()
        End Function

        Private Shared Function CellName(zeroBased As Integer) As String
            Dim name = String.Empty
            Dim value = zeroBased + 1
            While value > 0
                Dim remainder = (value - 1) Mod 26
                name = Chr(65 + remainder) & name
                value = (value - 1) \ 26
            End While
            Return name
        End Function

        Private Shared Sub WriteXml(archive As ZipArchive, entryName As String, body As Action(Of XmlWriter))
            Dim entry = archive.CreateEntry(entryName, CompressionLevel.Optimal)
            Using stream = entry.Open(), writer = XmlWriter.Create(stream, New XmlWriterSettings With {.Encoding = New UTF8Encoding(False), .CloseOutput = False})
                writer.WriteStartDocument(True)
                body(writer)
                writer.WriteEndDocument()
            End Using
        End Sub
    End Class
End Class
