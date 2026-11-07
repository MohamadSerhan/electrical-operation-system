Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text
Imports Microsoft.Data.Sqlite

Public Enum TransformerPowerState
    Unknown
    [On]
    Off
End Enum

Public NotInheritable Class PowerEventRecord
    Public Property Id As String = Guid.NewGuid().ToString("N")
    Public Property Scope As String = String.Empty
    Public Property StartUtc As DateTime
    Public Property ConfirmedUtc As DateTime
    Public Property LastObservedUtc As DateTime
    Public Property EndUtc As DateTime?
    Public Property EndReason As String = String.Empty
    Public Property Evidence As String = String.Empty

    Public Function SecondsInRange(fromUtc As DateTime, toUtc As DateTime) As Double
        Dim first = If(StartUtc > fromUtc, StartUtc, fromUtc)
        Dim last = If(EndUtc, LastObservedUtc)
        If last > toUtc Then last = toUtc
        Return Math.Max(0, (last - first).TotalSeconds)
    End Function
End Class

Public NotInheritable Class PowerEventTracker
    Private NotInheritable Class State
        Public Property Stable As TransformerPowerState
        Public Property Candidate As TransformerPowerState
        Public Property SinceUtc As DateTime
    End Class

    Public Shared ReadOnly TransformerNames As String() = {"T1", "T2", "T3", "T4"}
    Public Const OffVoltage As Double = 20
    Public Const OnVoltage As Double = 180
    Public Const ConfirmationSeconds As Double = 3
    Public Const EcgBackSeconds As Double = 60
    Private Const EcgBackPendingMinutes As Double = 10
    Private _backPendingSinceUtc As DateTime?
    Private _backSinceUtc As DateTime?
    Private ReadOnly _states As New Dictionary(Of String, State)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly _active As New Dictionary(Of String, PowerEventRecord)(StringComparer.Ordinal)
    Private ReadOnly _completed As New List(Of PowerEventRecord)()
    Private _lastCycleUtc As DateTime?

    Public Sub New()
        For Each name In TransformerNames
            _states.Add(name, New State())
        Next
    End Sub

    Public Shared Function VoltageState(values As Single()) As TransformerPowerState
        If values Is Nothing OrElse values.Length <> 16 OrElse
           values.Take(3).Any(Function(value) Not Single.IsFinite(value) OrElse value < 0) Then Return TransformerPowerState.Unknown
        If values.Take(3).All(Function(value) value < OffVoltage) Then Return TransformerPowerState.Off
        If values.Take(3).All(Function(value) value > OnVoltage) Then Return TransformerPowerState.On
        Return TransformerPowerState.Unknown
    End Function

    Public Sub Update(snapshots As IReadOnlyList(Of MeterSnapshot), nowUtc As DateTime, maximumGap As TimeSpan)
        If snapshots Is Nothing Then Throw New ArgumentNullException(NameOf(snapshots))
        If nowUtc.Kind <> DateTimeKind.Utc Then Throw New ArgumentException("Power events require UTC timestamps.", NameOf(nowUtc))
        If maximumGap <= TimeSpan.Zero Then Throw New ArgumentOutOfRangeException(NameOf(maximumGap))
        If _lastCycleUtc.HasValue AndAlso
           (nowUtc <= _lastCycleUtc.Value OrElse nowUtc - _lastCycleUtc.Value > maximumGap) Then Interrupt("Monitoring gap")
        _lastCycleUtc = nowUtc
        Dim rawStates As New Dictionary(Of String, TransformerPowerState)(StringComparer.OrdinalIgnoreCase)
        For Each name In TransformerNames
            Dim matching = snapshots.Where(Function(item) item IsNot Nothing AndAlso
                String.Equals(item.TransformerName, name, StringComparison.OrdinalIgnoreCase)).ToArray()
            Dim snapshot = If(matching.Length = 1, matching(0), Nothing)
            Dim raw = TransformerPowerState.Unknown
            If snapshot IsNot Nothing AndAlso snapshot.Quality = MeterQuality.Good AndAlso snapshot.CapturedAtUtc.HasValue AndAlso
               snapshot.CapturedAtUtc.Value.Kind = DateTimeKind.Utc AndAlso snapshot.CapturedAtUtc.Value <= nowUtc AndAlso
               nowUtc - snapshot.CapturedAtUtc.Value <= maximumGap Then raw = VoltageState(snapshot.DecodedValues)
            rawStates(name) = raw
            Dim state = _states(name)
            If raw = TransformerPowerState.Unknown Then
                state.Stable = TransformerPowerState.Unknown
                state.Candidate = TransformerPowerState.Unknown
            Else
                If raw <> state.Candidate Then
                    state.Candidate = raw
                    state.SinceUtc = nowUtc
                End If
                If (nowUtc - state.SinceUtc).TotalSeconds >= ConfirmationSeconds Then state.Stable = raw
            End If
            If snapshot IsNot Nothing Then
                snapshot.PowerState = state.Stable
                snapshot.PowerDetail = If(raw = TransformerPowerState.Unknown, "Power unknown: invalid, missing or intermediate phase voltages.",
                    If(state.Stable <> raw, "Voltage transition pending 3-second confirmation.",
                       If(state.Stable = TransformerPowerState.Off, "All three L-N voltages < 20 V.", "All three L-N voltages > 180 V.")))
            End If
        Next

        Dim desired As New HashSet(Of String)(StringComparer.Ordinal)
        If _states.Values.All(Function(item) item.Stable = TransformerPowerState.Off) AndAlso
           (_active.ContainsKey("ECG") OrElse rawStates.Values.All(Function(item) item = TransformerPowerState.Off)) Then
            desired.Add("ECG")
        Else
            For Each name In TransformerNames
                If _states(name).Stable = TransformerPowerState.Off AndAlso
                   (rawStates(name) = TransformerPowerState.Off OrElse _active.ContainsKey(name)) Then desired.Add(name)
            Next
        End If
        For Each scope In _active.Keys.ToArray()
            If desired.Contains(scope) Then Continue For
            Dim record = _active(scope)
            Dim names = If(scope = "ECG", TransformerNames, {scope})
            Dim unknown = names.Any(Function(name) _states(name).Stable = TransformerPowerState.Unknown)
            record.EndUtc = If(unknown, record.LastObservedUtc, nowUtc)
            Dim scopeChanged = If(scope = "ECG", desired.Count > 0, desired.Contains("ECG"))
            record.EndReason = If(unknown, "Power unknown / monitoring interrupted",
                If(scopeChanged, "Scope changed", "Power restored (confirmed)"))
            _completed.Add(record)
            _active.Remove(scope)
            If scope = "ECG" AndAlso Not unknown Then _backPendingSinceUtc = nowUtc
        Next
        For Each scope In desired
            Dim names = If(scope = "ECG", TransformerNames, {scope})
            If Not _active.ContainsKey(scope) Then
                _active.Add(scope, New PowerEventRecord With {
                    .Scope = scope, .StartUtc = nowUtc, .ConfirmedUtc = nowUtc, .LastObservedUtc = nowUtc,
                    .Evidence = Evidence(snapshots, names)
                })
            End If
            If names.All(Function(name) rawStates(name) = TransformerPowerState.Off) Then
                _active(scope).LastObservedUtc = nowUtc
            End If
        Next
        UpdateEcgBack(nowUtc)
        For Each snapshot In snapshots
            If snapshot IsNot Nothing Then
                snapshot.EcgOffConfirmed = _active.ContainsKey("ECG")
                snapshot.EcgBackConfirmed = _backSinceUtc.HasValue
            End If
        Next
    End Sub

    ' ECG BACK is shown for one minute once all four transformers are confirmed on after an outage. Any new power
    ' alarm (a transformer off again, unknown state) ends it immediately so that alarm takes over.
    Private Sub UpdateEcgBack(nowUtc As DateTime)
        Dim allOn = _states.Values.All(Function(item) item.Stable = TransformerPowerState.On)
        If _active.ContainsKey("ECG") Then
            _backPendingSinceUtc = Nothing
            _backSinceUtc = Nothing
            Return
        End If
        If _active.Count > 0 Then
            _backSinceUtc = Nothing
            Return
        End If
        If _backPendingSinceUtc.HasValue AndAlso (nowUtc - _backPendingSinceUtc.Value).TotalMinutes > EcgBackPendingMinutes Then _backPendingSinceUtc = Nothing
        If _backPendingSinceUtc.HasValue AndAlso allOn Then
            _backPendingSinceUtc = Nothing
            _backSinceUtc = nowUtc
        End If
        If _backSinceUtc.HasValue AndAlso (Not allOn OrElse (nowUtc - _backSinceUtc.Value).TotalSeconds >= EcgBackSeconds) Then _backSinceUtc = Nothing
    End Sub

    Public ReadOnly Property Summary As String
        Get
            If _active.ContainsKey("ECG") Then Return "ECG off"
            If _active.Count > 0 Then Return "Transformer off"
            Return If(_states.Values.All(Function(item) item.Stable = TransformerPowerState.On), String.Empty, "Power unknown")
        End Get
    End Property

    Private Shared Function Evidence(snapshots As IReadOnlyList(Of MeterSnapshot), names As String()) As String
        Return "All phases <20 V for >=3 s; " & String.Join("; ", names.Select(
            Function(name)
                Dim snapshot = snapshots.Single(Function(item) item IsNot Nothing AndAlso String.Equals(item.TransformerName, name, StringComparison.OrdinalIgnoreCase))
                Return name & " L-N=" & String.Join("/", snapshot.DecodedValues.Take(3).Select(
                    Function(value) value.ToString("R", CultureInfo.InvariantCulture))) & " V; cycle=" &
                    snapshot.AcquisitionCycleId.ToString(CultureInfo.InvariantCulture)
            End Function))
    End Function

    Public Sub Interrupt(reason As String)
        For Each record In _active.Values
            record.EndUtc = record.LastObservedUtc
            record.EndReason = reason
            _completed.Add(record)
        Next
        _active.Clear()
        _backPendingSinceUtc = Nothing
        _backSinceUtc = Nothing
        For Each state In _states.Values
            state.Stable = TransformerPowerState.Unknown
            state.Candidate = TransformerPowerState.Unknown
        Next
        _lastCycleUtc = Nothing
    End Sub

    Public Function PendingRecords() As IReadOnlyList(Of PowerEventRecord)
        Return _completed.Concat(_active.Values).ToArray()
    End Function

    Public Sub AcceptPersisted()
        _completed.Clear()
    End Sub
End Class

Public NotInheritable Class PowerEventStore
    Private ReadOnly _databasePath As String
    Private ReadOnly _sessionId As String = Guid.NewGuid().ToString("N")
    Private _initialized As Boolean

    Public Sub New(Optional databasePath As String = Nothing)
        _databasePath = If(databasePath, DefaultDatabasePath())
    End Sub

    Public Shared Function DefaultDatabasePath() As String
        Return Path.Combine(AppContext.BaseDirectory, "EOEMSData", "Historian", "eoems_historian.db")
    End Function

    Private Shared Function OpenDatabase(path As String) As SqliteConnection
        Directory.CreateDirectory(IO.Path.GetDirectoryName(IO.Path.GetFullPath(path)))
        Dim connection As New SqliteConnection(New SqliteConnectionStringBuilder With {
            .DataSource = path, .DefaultTimeout = 5
        }.ToString())
        Try
            connection.Open()
            Using command = connection.CreateCommand()
                command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;" &
                    "CREATE TABLE IF NOT EXISTS power_events(" &
                    "id TEXT PRIMARY KEY, session_id TEXT NOT NULL, scope TEXT NOT NULL, start_ticks INTEGER NOT NULL," &
                    "confirmed_ticks INTEGER NOT NULL, last_ticks INTEGER NOT NULL, end_ticks INTEGER NULL," &
                    "end_reason TEXT NOT NULL, evidence TEXT NOT NULL);" &
                    "CREATE INDEX IF NOT EXISTS ix_power_events_start ON power_events(start_ticks);"
                command.ExecuteNonQuery()
            End Using
            Return connection
        Catch
            connection.Dispose()
            Throw
        End Try
    End Function

    Public Sub Save(records As IReadOnlyList(Of PowerEventRecord))
        Using connection = OpenDatabase(_databasePath), transaction = connection.BeginTransaction()
            If Not _initialized Then
                Using command = connection.CreateCommand()
                    command.Transaction = transaction
                    command.CommandText = "UPDATE power_events SET end_ticks=last_ticks, end_reason='Application restart / monitoring interrupted' " &
                        "WHERE end_ticks IS NULL AND session_id<>$session"
                    command.Parameters.AddWithValue("$session", _sessionId)
                    command.ExecuteNonQuery()
                End Using
            End If
            For Each record In records
                Using command = connection.CreateCommand()
                    command.Transaction = transaction
                    command.CommandText = "INSERT INTO power_events VALUES($id,$session,$scope,$start,$confirmed,$last,$end,$reason,$evidence) " &
                        "ON CONFLICT(id) DO UPDATE SET last_ticks=excluded.last_ticks,end_ticks=excluded.end_ticks,end_reason=excluded.end_reason"
                    command.Parameters.AddWithValue("$id", record.Id)
                    command.Parameters.AddWithValue("$session", _sessionId)
                    command.Parameters.AddWithValue("$scope", record.Scope)
                    command.Parameters.AddWithValue("$start", record.StartUtc.Ticks)
                    command.Parameters.AddWithValue("$confirmed", record.ConfirmedUtc.Ticks)
                    command.Parameters.AddWithValue("$last", record.LastObservedUtc.Ticks)
                    command.Parameters.AddWithValue("$end", If(record.EndUtc.HasValue, CType(record.EndUtc.Value.Ticks, Object), DBNull.Value))
                    command.Parameters.AddWithValue("$reason", record.EndReason)
                    command.Parameters.AddWithValue("$evidence", record.Evidence)
                    command.ExecuteNonQuery()
                End Using
            Next
            transaction.Commit()
            _initialized = True
        End Using
    End Sub

    Public Shared Function ReadRange(path As String, fromUtc As DateTime, toUtc As DateTime) As IReadOnlyList(Of PowerEventRecord)
        If fromUtc.Kind <> DateTimeKind.Utc OrElse toUtc.Kind <> DateTimeKind.Utc OrElse toUtc <= fromUtc Then
            Throw New ArgumentException("Choose a positive UTC date range.")
        End If
        Dim result As New List(Of PowerEventRecord)()
        Using connection = OpenDatabase(path), command = connection.CreateCommand()
            command.CommandText = "SELECT id,scope,start_ticks,confirmed_ticks,last_ticks,end_ticks,end_reason,evidence " &
                "FROM power_events WHERE start_ticks<$to AND COALESCE(end_ticks,last_ticks)>=$from ORDER BY start_ticks,id"
            command.Parameters.AddWithValue("$from", fromUtc.Ticks)
            command.Parameters.AddWithValue("$to", toUtc.Ticks)
            Using reader = command.ExecuteReader()
                While reader.Read()
                    If result.Count >= 100000 Then Throw New InvalidDataException("More than 100,000 power-event segments match. Select a shorter date range.")
                    result.Add(New PowerEventRecord With {
                        .Id = reader.GetString(0), .Scope = reader.GetString(1),
                        .StartUtc = New DateTime(reader.GetInt64(2), DateTimeKind.Utc),
                        .ConfirmedUtc = New DateTime(reader.GetInt64(3), DateTimeKind.Utc),
                        .LastObservedUtc = New DateTime(reader.GetInt64(4), DateTimeKind.Utc),
                        .EndUtc = If(reader.IsDBNull(5), CType(Nothing, DateTime?), New DateTime(reader.GetInt64(5), DateTimeKind.Utc)),
                        .EndReason = reader.GetString(6), .Evidence = reader.GetString(7)
                    })
                End While
            End Using
        End Using
        Return result
    End Function

    Public Shared Sub ExportCsv(path As String, records As IReadOnlyList(Of PowerEventRecord), fromUtc As DateTime, toUtc As DateTime)
        Using writer As New StreamWriter(path, False, New UTF8Encoding(True))
            writer.WriteLine("EventId,Scope,StartUtc,ConfirmedUtc,LastObservedUtc,EndUtc,Status,EndReason,DurationSecondsInWindow,WindowFromUtc,WindowToUtc,Evidence")
            For Each record In records
                Dim fields = {record.Id, record.Scope, record.StartUtc.ToString("O"), record.ConfirmedUtc.ToString("O"),
                    record.LastObservedUtc.ToString("O"), If(record.EndUtc.HasValue, record.EndUtc.Value.ToString("O"), String.Empty),
                    If(record.EndUtc.HasValue, "Closed", "Open (observed only)"), record.EndReason,
                    record.SecondsInRange(fromUtc, toUtc).ToString("0.###", CultureInfo.InvariantCulture),
                    fromUtc.ToString("O"), toUtc.ToString("O"), record.Evidence}
                writer.WriteLine(String.Join(",", fields.Select(Function(field) """" & field.Replace("""", """""") & """")))
            Next
        End Using
    End Sub
End Class
