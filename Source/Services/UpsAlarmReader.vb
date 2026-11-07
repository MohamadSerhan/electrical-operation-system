Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Globalization
Imports System.Linq
Imports Lextm.SharpSnmpLib

Public NotInheritable Class UpsCardAlarm
    Public Property Id As String = String.Empty
    Public Property Name As String = String.Empty
    Public Property Source As String = String.Empty
    Public Property DescriptionOid As String = String.Empty
    Public Property TableOid As String = String.Empty
    Public Property Active As Boolean?
    Public Property Acknowledged As Boolean?
    Public Property CardType As String = String.Empty
    Public Property CardSeverity As String = String.Empty
    Public Property OccurredAtUptime As String = String.Empty
    Public Property EvidenceConflict As Boolean

    Public ReadOnly Property StateText As String
        Get
            If EvidenceConflict Then Return "UNCONFIRMED (card data conflict)"
            Return If(Not Active.HasValue, "State not provided", If(Active.Value, "ACTIVE", "CLEARED (retained by card)"))
        End Get
    End Property
End Class

' RFC1628 alarm identities and Vertiv condition rows are separate views of the same events.
' Prefer the more specific vendor view; never append both and double-count the fault.
Friend Module UpsAlarmReader
    Friend Const VendorCount As String = "1.3.6.1.4.1.476.1.42.3.2.2.0"
    Friend Const VendorEntry As String = "1.3.6.1.4.1.476.1.42.3.2.3.1."
    Friend Const RfcEntry As String = "1.3.6.1.2.1.33.1.6.2.1."
    Private Const LabelColumn As String = "1.3.6.1.4.1.476.1.42.3.9.20.1.10."
    Private Const MaxRows As Integer = 32
    Private ReadOnly RfcNames As String() = {
        "Battery bad", "On battery", "Battery low", "Battery depleted", "Battery temperature high",
        "Input bad", "Output bad", "Output overload", "On bypass", "Bypass bad", "Output off as requested",
        "UPS off as requested", "Charger failed", "UPS output off", "UPS system off", "Fan failure",
        "Fuse failure", "General fault", "Diagnostic test failed", "Communications lost", "Awaiting power",
        "Shutdown pending", "Shutdown imminent", "Test in progress"}
    Private ReadOnly VendorNames As New Dictionary(Of String, String)(StringComparer.Ordinal) From {
        {"1.3.6.1.4.1.476.1.42.3.2.1.43", "Fan failure"},
        {"1.3.6.1.4.1.476.1.42.3.2.1.43.2", "Redundant Fan Failure"},
        {"1.3.6.1.4.1.476.1.42.3.2.1.43.3", "Multiple Fan Failure"},
        {"1.3.6.1.4.1.476.1.42.3.2.1.199", "Manual transfer to bypass inhibited"}}

    Friend Function KnownName(oid As String) As String
        Dim name As String = Nothing
        If VendorNames.TryGetValue(oid, name) Then Return name
        Const prefix As String = "1.3.6.1.2.1.33.1.6.3."
        Dim index As Integer
        If oid.StartsWith(prefix, StringComparison.Ordinal) AndAlso
           Integer.TryParse(oid.Substring(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, index) AndAlso
           index >= 1 AndAlso index <= RfcNames.Length Then Return RfcNames(index - 1)
        Return String.Empty
    End Function

    Public Sub Read(snapshot As UpsSnapshot,
                    getValues As Func(Of IList(Of String), Dictionary(Of String, ISnmpData)),
                    getNext As Func(Of String, Variable))
        Dim rows As New List(Of UpsCardAlarm)()
        Dim issues As New List(Of String)()
        Dim watch = Stopwatch.StartNew()
        Dim isVertiv = snapshot.SysObjectId = "1.3.6.1.4.1.476.1.42"
        Dim vendorAvailable = False
        Dim vendorEmpty = False
        Dim expected As Double? = snapshot.Value("AlarmsPresent")
        Dim entry = RfcEntry
        Dim source = "RFC1628"
        Dim completed = False
        Try
            If Not isVertiv AndAlso expected.HasValue AndAlso expected.Value = 0 Then
                snapshot.CardAlarms = rows
                snapshot.AlarmDetailsComplete = True
                snapshot.AlarmDetailsStatus = "RFC1628: 0 condition row(s) verified."
                Return
            End If
            If isVertiv Then
                Try
                    Dim counts = getValues({VendorCount})
                    Dim data = ValueAt(counts, VendorCount)
                    Dim count = UpsSnmpClient.ToNumber(data)
                    If count.HasValue AndAlso count >= 0 AndAlso count <= 1000 AndAlso count = Math.Truncate(count.Value) Then
                        If count = 0 AndAlso expected > 0 Then
                            issues.Add("Vertiv reports no conditions but RFC1628 reports alarms; reading RFC1628 details.")
                            EnsureBudget(watch)
                            Dim firstCondition As Variable = Nothing
                            Try
                                firstCondition = getNext(VendorEntry & "2")
                            Catch ex As UpsSnmpFailure When ex.ErrorStatus = 2
                                vendorEmpty = True
                            End Try
                            If firstCondition IsNot Nothing Then
                                vendorEmpty = UpsSnmpClient.IsMissing(firstCondition.Data) OrElse
                                    Not firstCondition.Id.ToString().StartsWith(VendorEntry & "2.", StringComparison.Ordinal)
                            End If
                        Else
                            expected = count
                            vendorAvailable = True
                            entry = VendorEntry
                            source = "Vertiv conditions"
                        End If
                    Else
                        issues.Add("Vertiv condition count unavailable; using RFC1628 alarm details.")
                    End If
                Catch ex As UpsSnmpFailure
                    issues.Add("Vertiv condition read failed: " & ex.Message & " Using RFC1628 alarm details.")
                End Try
            End If
            If vendorAvailable AndAlso expected.HasValue AndAlso expected.Value = 0 Then
                snapshot.CardAlarms = rows
                snapshot.AlarmDetailsComplete = True
                snapshot.AlarmDetailsStatus = "Vertiv conditions: 0 condition row(s) verified."
                Return
            End If
            Dim column = entry & "2."
            Dim cursor = column.TrimEnd("."c)
            Do
                EnsureBudget(watch)
                Dim variable As Variable
                Try
                    variable = getNext(cursor)
                Catch ex As UpsSnmpFailure When ex.ErrorStatus = 2
                    completed = True
                    Exit Do
                End Try
                If variable Is Nothing Then Throw New InvalidOperationException("Alarm table returned no GETNEXT result.")
                Dim oid = variable.Id.ToString()
                If UpsSnmpClient.IsMissing(variable.Data) OrElse Not oid.StartsWith(column, StringComparison.Ordinal) Then
                    completed = True
                    Exit Do
                End If
                If New ObjectIdentifier(oid).CompareTo(New ObjectIdentifier(cursor)) <= 0 Then
                    Throw New InvalidOperationException("Alarm table did not advance.")
                End If
                If rows.Count >= MaxRows Then
                    issues.Add("Alarm table truncated at " & MaxRows.ToString(CultureInfo.InvariantCulture) & " rows.")
                    Exit Do
                End If
                Dim id = oid.Substring(column.Length)
                Dim numericId As UInteger
                If Not UInteger.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, numericId) OrElse numericId = 0 Then
                    Throw New InvalidOperationException("Invalid alarm table row index: " & id)
                End If
                Dim description = If(TypeOf variable.Data Is ObjectIdentifier, variable.Data.ToString(), String.Empty)
                Dim name = KnownName(description)
                rows.Add(New UpsCardAlarm With {.Id = id, .Source = source, .TableOid = oid,
                    .DescriptionOid = description, .Name = If(name.Length > 0, name, "Unrecognized card condition (" & description & ")"),
                    .CardType = If(vendorAvailable, "Not provided", "Alarm"), .CardSeverity = "Not provided",
                    .Active = If(vendorAvailable, CType(Nothing, Boolean?), True)})
                cursor = oid
            Loop
            For Each row In rows
                EnsureBudget(watch)
                Dim columns = If(vendorAvailable, New String() {"2", "3", "6", "10", "12", "14", "18"}, New String() {"2", "3"})
                Dim metadata = getValues(columns.Select(Function(number) entry & number & "." & row.Id).ToArray())
                Dim currentDescription = ValueAt(metadata, entry & "2." & row.Id)
                If Not TypeOf currentDescription Is ObjectIdentifier OrElse currentDescription.ToString() <> row.DescriptionOid Then
                    row.Active = Nothing
                    issues.Add("Condition row " & row.Id & " changed during the read; state not verified.")
                    Continue For
                End If
                row.OccurredAtUptime = UpsSnmpClient.ToText(ValueAt(metadata, entry & "3." & row.Id))
                If vendorAvailable Then
                    row.Active = BooleanValue(ValueAt(metadata, entry & "12." & row.Id))
                    row.Acknowledged = AcknowledgedValue(ValueAt(metadata, entry & "18." & row.Id))
                    row.CardType = EnumText(ValueAt(metadata, entry & "10." & row.Id),
                        New Dictionary(Of Integer, String) From {{0, "Not specified"}, {2, "Message"}, {4, "Warning"}, {6, "Alarm"}, {8, "Fault"}})
                    row.CardSeverity = EnumText(ValueAt(metadata, entry & "14." & row.Id),
                        New Dictionary(Of Integer, String) From {{0, "Not specified"}, {3, "Minor"}, {6, "Major"}, {9, "Critical"}})
                    If Not row.Active.HasValue Then issues.Add("State unavailable for condition row " & row.Id & ".")
                    Dim reference = ValueAt(metadata, entry & "6." & row.Id)
                    ' Only dereference the card's read-only Flexible-MIB label column, never arbitrary returned OIDs.
                    If TypeOf reference Is ObjectIdentifier AndAlso reference.ToString().StartsWith(LabelColumn, StringComparison.Ordinal) Then
                        EnsureBudget(watch)
                        Dim label = ValueAt(getValues({reference.ToString()}), reference.ToString())
                        If Not UpsSnmpClient.IsMissing(label) AndAlso TypeOf label Is OctetString AndAlso label.ToString().Trim().Length > 0 Then
                            row.Name = label.ToString().Trim()
                        End If
                    End If
                End If
                If row.Name.StartsWith("Unrecognized", StringComparison.Ordinal) Then issues.Add("Condition name unresolved: " & row.DescriptionOid)
            Next
            EnsureBudget(watch)
            Dim countOid = If(vendorAvailable, VendorCount, UpsOidMap.Find("AlarmsPresent").Oid)
            Dim finalCount = UpsSnmpClient.ToNumber(ValueAt(getValues({countOid}), countOid))
            If Not expected.HasValue OrElse Not finalCount.HasValue OrElse finalCount <> expected OrElse rows.Count <> expected Then
                issues.Add("Alarm count/table changed or is incomplete; details will be read again next poll.")
            End If
            If vendorEmpty Then
                EnsureBudget(watch)
                Dim finalVendorCount = UpsSnmpClient.ToNumber(ValueAt(getValues({VendorCount}), VendorCount))
                If finalVendorCount = 0 AndAlso completed AndAlso expected > 0 AndAlso finalCount = expected AndAlso rows.Count = expected AndAlso
                   rows.All(Function(row) row.Active.HasValue AndAlso row.Active.Value) Then
                    For Each row In rows.Where(Function(item) item.DescriptionOid = "1.3.6.1.2.1.33.1.6.3.18")
                        row.Active = Nothing
                        row.EvidenceConflict = True
                    Next
                    issues.Add("Card data conflict: verified empty Vertiv condition table with zero count, but RFC1628 still reports alarms. " &
                               If(rows.Any(Function(row) row.EvidenceConflict), "RFC General fault is unconfirmed, not a verified active UPS fault. ", String.Empty) &
                               "Check the card's Active Events and SNMP agent.")
                Else
                    issues.Add("Vertiv clear-state verification changed or is incomplete; RFC1628 alarms remain reported.")
                End If
            End If
        Catch ex As UpsSnmpFailure
            issues.Add(source & " alarm details unavailable: " & ex.Message)
        Catch ex As InvalidOperationException
            issues.Add(source & " alarm details incomplete: " & ex.Message)
        End Try
        snapshot.CardAlarms = rows
        snapshot.AlarmDetailsComplete = completed AndAlso issues.Count = 0
        snapshot.AlarmDetailsStatus = If(issues.Count = 0, source & ": " & rows.Count.ToString(CultureInfo.InvariantCulture) &
            " condition row(s) verified.", String.Join(" ", issues))
    End Sub

    Private Sub EnsureBudget(watch As Stopwatch)
        If watch.Elapsed > TimeSpan.FromSeconds(4) Then Throw New InvalidOperationException("Four-second alarm detail budget exceeded.")
    End Sub

    Private Function ValueAt(data As IDictionary(Of String, ISnmpData), oid As String) As ISnmpData
        Dim value As ISnmpData = Nothing
        data.TryGetValue(oid, value)
        Return value
    End Function

    Private Function BooleanValue(data As ISnmpData) As Boolean?
        Dim value = UpsSnmpClient.ToNumber(data)
        Return If(value = 1, CType(True, Boolean?), If(value = 2, CType(False, Boolean?), Nothing))
    End Function

    Private Function AcknowledgedValue(data As ISnmpData) As Boolean?
        Dim value = UpsSnmpClient.ToNumber(data)
        Return If(value = 2, CType(True, Boolean?), If(value = 1, CType(False, Boolean?), Nothing))
    End Function

    Private Function EnumText(data As ISnmpData, names As Dictionary(Of Integer, String)) As String
        Dim number = UpsSnmpClient.ToNumber(data)
        Dim text As String = Nothing
        If Not number.HasValue Then Return "Not provided"
        If number >= Integer.MinValue AndAlso number <= Integer.MaxValue AndAlso number = Math.Truncate(number.Value) AndAlso
           names.TryGetValue(CInt(number.Value), text) Then Return text
        Return "Unknown (" & UpsSnmpClient.ToText(data) & ")"
    End Function
End Module
