Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Globalization
Imports System.Linq
Imports System.Net
Imports System.Threading
Imports Lextm.SharpSnmpLib

' Result of one capability group request (e.g. "Battery", "Output table").
Public NotInheritable Class UpsCapabilityGroupResult
    Public Property Name As String = String.Empty
    Public Property Requested As Integer
    Public Property Supported As Integer
    Public Property Status As String = String.Empty
    Public Property Milliseconds As Double
End Class

' What one specific card was proven to support (never inferred from another card or from the registry model).
Public NotInheritable Class UpsCapabilityProfile
    Public Property DeviceId As String = String.Empty
    Public Property IpAddress As String = String.Empty
    Public Property Port As Integer
    Public Property CredentialProfile As String = String.Empty
    Public Property CredentialUpdatedUtc As DateTime?
    Public Property ProtocolVersion As String = String.Empty
    Public Property VerifiedUtc As DateTime
    Public Property SysName As String = String.Empty
    Public Property SysDescr As String = String.Empty
    Public Property SysObjectId As String = String.Empty
    Public Property SysUpTime As String = String.Empty
    Public Property Manufacturer As String = String.Empty
    Public Property Model As String = String.Empty
    Public Property UpsSoftware As String = String.Empty
    Public Property AgentSoftware As String = String.Empty
    Public Property IdentityLatencyMilliseconds As Double?
    Public Property ElapsedMilliseconds As Double
    ' Instance OIDs that returned a value (scalar ".0" or table ".index").
    Public Property SupportedOids As IReadOnlyCollection(Of String) = Array.Empty(Of String)()
    ' "Key oid – reason" for every requested object that the card did not provide.
    Public Property UnsupportedOids As IReadOnlyList(Of String) = Array.Empty(Of String)()
    Public Property InputIndices As Integer() = Array.Empty(Of Integer)()
    Public Property OutputIndices As Integer() = Array.Empty(Of Integer)()
    Public Property BypassIndices As Integer() = Array.Empty(Of Integer)()
    Public Property IndexEvidence As IReadOnlyList(Of String) = Array.Empty(Of String)()
    Public Property Groups As IReadOnlyList(Of UpsCapabilityGroupResult) = Array.Empty(Of UpsCapabilityGroupResult)()
    Public Property Checks As IReadOnlyList(Of UpsOidCheck) = Array.Empty(Of UpsOidCheck)()
    Public Property MeasurementBindings As IReadOnlyList(Of UpsMeasurementBinding) = Array.Empty(Of UpsMeasurementBinding)()
    ' Empty when every group was answered; otherwise why the probe stopped (timeout, authentication, cancel).
    Public Property Aborted As String = String.Empty
    Public Property FailureQuality As UpsQuality = UpsQuality.NoData
    ' True once the system-identity GET was answered with the configured credential.
    Public Property IdentityAnswered As Boolean

    Public ReadOnly Property Complete As Boolean
        Get
            Return Aborted.Length = 0
        End Get
    End Property

    Public ReadOnly Property SupportsUpsMib As Boolean
        Get
            Return Manufacturer.Length > 0 OrElse Model.Length > 0 OrElse SupportedOids.Any(Function(oid) oid.StartsWith(UpsOidMap.UpsMibRoot & ".", StringComparison.Ordinal))
        End Get
    End Property

    ' Identity used to detect a swapped/replaced card: a change forces a new capability probe.
    Public ReadOnly Property IdentityKey As String
        Get
            Return SysObjectId & "|" & Model & "|" & AgentSoftware
        End Get
    End Property

    ' A parameter is readable only when every requested phase/value is valid.
    Public ReadOnly Property SupportedParameterCount As Integer
        Get
            Return Checks.GroupBy(Function(item) item.Key, StringComparer.Ordinal).
                Count(Function(items) items.All(Function(item) item.Present AndAlso item.InRange))
        End Get
    End Property

    Public ReadOnly Property UnavailableParameters As IReadOnlyList(Of String)
        Get
            Return Checks.Where(Function(item) Not item.Present OrElse Not item.InRange).
                Select(Function(item) item.Key).Distinct(StringComparer.Ordinal).OrderBy(Function(key) key, StringComparer.Ordinal).ToArray()
        End Get
    End Property

    Public ReadOnly Property RequestedParameterCount As Integer
        Get
            Return Checks.Select(Function(item) item.Key).Distinct(StringComparer.Ordinal).Count()
        End Get
    End Property

    Public ReadOnly Property FullyMapped As Boolean
        Get
            Return Complete AndAlso UnsupportedOids.Count = 0 AndAlso CoreMapped AndAlso
                MeasurementBindings.Where(Function(item) item.Definition.Key = "OutputVoltage").Count() = 3 AndAlso
                Checks.All(Function(item) item.Present AndAlso item.InRange)
        End Get
    End Property

    Public ReadOnly Property CoreMapped As Boolean
        Get
            Return UpsOidAudit.CoreKeys.All(Function(key) Checks.Any(Function(item) item.Key = key) AndAlso
                Checks.Where(Function(item) item.Key = key).All(Function(item) item.Present AndAlso item.InRange))
        End Get
    End Property

    Public Function Supports(oid As String) As Boolean
        Return SupportedOids.Contains(oid)
    End Function

    Public Function IndicesFor(linesKey As String) As Integer()
        Select Case linesKey
            Case "InputLines" : Return InputIndices
            Case "BypassLines" : Return BypassIndices
            Case Else : Return OutputIndices
        End Select
    End Function
End Class

' Read-only capability discovery: system identity, RFC 1628 identity, then the UPS-MIB in small groups.
' An unsupported object is isolated (split per OID) instead of failing the whole read. GET/GETNEXT only.
Friend Module UpsCapabilityProbe
    Private Const B As String = "1.3.6.1.2.1.33.1."
    Private ReadOnly Cache As New ConcurrentDictionary(Of String, UpsCapabilityProfile)(StringComparer.OrdinalIgnoreCase)
    ' Capabilities are re-verified periodically even when nothing changed (firmware updates, card swaps).
    Public ReadOnly MaxAge As TimeSpan = TimeSpan.FromHours(6)
    Private Const MaxTableRows As Integer = 8

    Private ReadOnly ScalarGroups As (Name As String, Keys As String())() = {
        ("UPS-MIB identity", {"Manufacturer", "IdentModel", "UpsSoftware", "AgentSoftware"}),
        ("Battery", {"BatteryStatus", "SecondsOnBattery", "RuntimeMinutes", "ChargePercent", "BatteryVoltage", "BatteryCurrent", "BatteryTemperature"}),
        ("Input", {"InputLineBads", "InputLines"}),
        ("Output", {"OutputSource", "OutputFrequency", "OutputLines"}),
        ("Bypass", {"BypassFrequency", "BypassLines"}),
        ("Alarms", {"AlarmsPresent"}),
        ("Rating", {"RatedInputVoltage", "RatedOutputVa", "RatedOutputW"})}

    ' Table column used to walk the real row indices of each RFC 1628 line table.
    Private ReadOnly Tables As (Name As String, LinesKey As String, WalkColumn As String)() = {
        ("Input table", "InputLines", B & "3.3.1.3"),
        ("Output table", "OutputLines", B & "4.4.1.2"),
        ("Bypass table", "BypassLines", B & "5.3.1.2")}

    Public Function GetCached(device As UpsDevice) As UpsCapabilityProfile
        Dim profile = CurrentEvidence(device)
        Return If(profile IsNot Nothing AndAlso profile.IdentityAnswered AndAlso profile.SupportsUpsMib, profile, Nothing)
    End Function

    Public Function CurrentEvidence(device As UpsDevice) As UpsCapabilityProfile
        device = SnmpCredentialStore.EffectiveDevice(device)
        Dim profile As UpsCapabilityProfile = Nothing
        If Not Cache.TryGetValue(device.Id, profile) Then Return Nothing
        If DateTime.UtcNow - profile.VerifiedUtc > MaxAge OrElse
           Not String.Equals(profile.IpAddress, device.IpAddress.Trim(), StringComparison.Ordinal) OrElse profile.Port <> device.Port OrElse
           Not String.Equals(profile.CredentialProfile, device.CredentialProfile, StringComparison.OrdinalIgnoreCase) Then Return Nothing
        Dim credential = SnmpCredentialStore.ListProfiles().FirstOrDefault(Function(item) String.Equals(item.Name, device.CredentialProfile, StringComparison.OrdinalIgnoreCase))
        If credential Is Nothing OrElse Not Nullable.Equals(credential.UpdatedUtc, profile.CredentialUpdatedUtc) Then Return Nothing
        Return profile
    End Function

    ' Latest probe result even if incomplete or old (for diagnostics/report).
    Public Function Latest(deviceId As String) As UpsCapabilityProfile
        Dim profile As UpsCapabilityProfile = Nothing
        Cache.TryGetValue(deviceId, profile)
        Return profile
    End Function

    Public Sub Invalidate(deviceId As String)
        Dim profile As UpsCapabilityProfile = Nothing
        If Cache.TryGetValue(deviceId, profile) Then profile.VerifiedUtc = DateTime.MinValue
    End Sub

    Public Function Run(device As UpsDevice, token As CancellationToken) As UpsCapabilityProfile
        Dim watch = Stopwatch.StartNew()
        Dim profile As New UpsCapabilityProfile With {
            .DeviceId = device.Id, .IpAddress = device.IpAddress.Trim(), .Port = device.Port,
            .CredentialProfile = device.CredentialProfile, .VerifiedUtc = DateTime.UtcNow}
        Dim checks As New List(Of UpsOidCheck)()
        Dim groups As New List(Of UpsCapabilityGroupResult)()
        Dim supported As New HashSet(Of String)(StringComparer.Ordinal)
        Dim unsupported As New List(Of String)()
        Dim evidence As New List(Of String)()
        profile.Checks = checks
        profile.Groups = groups
        profile.SupportedOids = supported
        profile.UnsupportedOids = unsupported
        profile.IndexEvidence = evidence

        Dim credential As SnmpCredential
        Try
            device = SnmpCredentialStore.EffectiveDevice(device)
            profile.Port = device.Port
            credential = SnmpCredentialStore.Resolve(device.CredentialProfile)
            Dim summary = SnmpCredentialStore.ListProfiles().FirstOrDefault(Function(item) String.Equals(item.Name, device.CredentialProfile, StringComparison.OrdinalIgnoreCase))
            profile.CredentialUpdatedUtc = If(summary Is Nothing, Nothing, summary.UpdatedUtc)
        Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is IO.InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
            profile.Aborted = ex.Message
            profile.FailureQuality = UpsQuality.ConfigError
            Return Finish(profile, watch)
        End Try
        Dim address As IPAddress = Nothing
        If credential Is Nothing OrElse Not IPAddress.TryParse(profile.IpAddress, address) Then
            profile.Aborted = If(credential Is Nothing, "Credential profile '" & device.CredentialProfile & "' does not exist.", "Invalid IP address.")
            profile.FailureQuality = UpsQuality.ConfigError
            Return Finish(profile, watch)
        End If
        profile.ProtocolVersion = credential.Version
        Dim target As New IPEndPoint(address, device.Port)
        Dim timeout = Math.Max(500, device.TimeoutMilliseconds)
        Dim consecutiveTimeouts = 0

        ' One group GET with the device's retry count. Error responses (v1 noSuchName, genErr, tooBig) are split per OID.
        Dim readGroup = Function(name As String, oids As IList(Of String)) As Dictionary(Of String, ISnmpData)
                            Dim result As New Dictionary(Of String, ISnmpData)(StringComparer.Ordinal)
                            Dim groupWatch = Stopwatch.StartNew()
                            Dim group As New UpsCapabilityGroupResult With {.Name = name, .Requested = oids.Count}
                            groups.Add(group)
                            Try
                                For Each variable In GetWithRetries(target, credential, oids, timeout, device.Retries)
                                    result(variable.Id.ToString()) = variable.Data
                                Next
                                group.Status = "answered"
                                consecutiveTimeouts = 0
                            Catch ex As UpsSnmpFailure When ex.IsErrorResponse
                                group.Status = "error response (" & UpsSnmpClient.ErrorStatusName(ex.ErrorStatus) & ") – split per OID"
                                consecutiveTimeouts = 0
                                For Each oid In oids
                                    Try
                                        For Each variable In GetWithRetries(target, credential, {oid}, timeout, device.Retries)
                                            result(variable.Id.ToString()) = variable.Data
                                        Next
                                    Catch inner As UpsSnmpFailure When inner.IsErrorResponse
                                        result(oid) = New OctetString("#ERR " & UpsSnmpClient.ErrorStatusName(inner.ErrorStatus))
                                    End Try
                                Next
                            Catch ex As UpsSnmpFailure When ex.Quality = UpsQuality.Timeout
                                group.Status = "timeout"
                                consecutiveTimeouts += 1
                                Throw
                            Finally
                                group.Milliseconds = groupWatch.Elapsed.TotalMilliseconds
                                group.Supported = result.Values.Where(Function(item) Not UpsSnmpClient.IsMissing(item) AndAlso Not IsErrorMarker(item)).Count()
                            End Try
                            Return result
                        End Function

        Try
            ' 1. System identity alone: proves the credential and the agent before any UPS-MIB traffic.
            Dim identityWatch = Stopwatch.StartNew()
            Dim system = readGroup("System identity", {UpsOidMap.SysDescr, UpsOidMap.SysObjectId, UpsOidMap.SysUpTime, UpsOidMap.SysName})
            profile.IdentityAnswered = True
            profile.IdentityLatencyMilliseconds = identityWatch.Elapsed.TotalMilliseconds
            profile.SysDescr = TextOf(system, UpsOidMap.SysDescr)
            profile.SysObjectId = TextOf(system, UpsOidMap.SysObjectId)
            profile.SysUpTime = TextOf(system, UpsOidMap.SysUpTime)
            profile.SysName = TextOf(system, UpsOidMap.SysName)

            ' 2. RFC 1628 identity, then 3. capability groups. Each group is independent.
            Dim values As New Dictionary(Of String, Double)(StringComparer.Ordinal)
            For Each group In ScalarGroups
                token.ThrowIfCancellationRequested()
                Dim definitions = group.Keys.Select(Function(key) UpsOidMap.Find(key)).ToList()
                Dim data = TryReadGroup(readGroup, group.Name, definitions.Select(Function(item) item.Oid).ToList(), consecutiveTimeouts)
                For Each definition In definitions
                    Dim check = Evaluate(definition, definition.Oid, data, supported, unsupported)
                    checks.Add(check)
                    If check.Present AndAlso check.InRange AndAlso Not UpsOidMap.IdentityKeys.Contains(definition.Key) Then
                        values(definition.Key) = UpsSnmpClient.ToNumber(data(definition.Oid)).Value * definition.Scale
                    End If
                Next
                If group.Name = "UPS-MIB identity" Then
                    profile.Manufacturer = TextOf(data, UpsOidMap.Find("Manufacturer").Oid)
                    profile.Model = TextOf(data, UpsOidMap.Find("IdentModel").Oid)
                    profile.UpsSoftware = TextOf(data, UpsOidMap.Find("UpsSoftware").Oid)
                    profile.AgentSoftware = TextOf(data, UpsOidMap.Find("AgentSoftware").Oid)
                End If
            Next

            ' 4. Phase tables: walk the real row indices, compare with upsXxxNumLines, then read the columns for those rows.
            For Each table In Tables
                token.ThrowIfCancellationRequested()
                Dim declared As Double
                Dim hasDeclared = values.TryGetValue(table.LinesKey, declared)
                Dim indices As Integer()
                Try
                    indices = WalkIndices(target, credential, table.WalkColumn, timeout, device.Retries, evidence, table.Name)
                    consecutiveTimeouts = 0
                Catch ex As UpsSnmpFailure When ex.Quality = UpsQuality.Timeout
                    consecutiveTimeouts += 1
                    If consecutiveTimeouts >= 2 Then Throw New UpsSnmpFailure(UpsQuality.Timeout, "Device stopped answering during capability discovery (GETNEXT walk of '" & table.Name & "').")
                    evidence.Add(table.Name & ": GETNEXT walk timed out.")
                    indices = Nothing
                End Try
                If indices Is Nothing Then
                    ' Walk not possible on this agent: fall back to 1..NumLines and say so.
                    indices = If(hasDeclared, Enumerable.Range(1, CInt(Math.Max(0, Math.Min(3, declared)))).ToArray(), Array.Empty(Of Integer)())
                    evidence.Add(table.Name & ": indices " & IndexText(indices) & " taken from " & table.LinesKey & " (walk unavailable).")
                ElseIf hasDeclared AndAlso indices.Length <> CInt(declared) Then
                    evidence.Add(table.Name & ": MISMATCH – " & table.LinesKey & " = " & CInt(declared).ToString(CultureInfo.InvariantCulture) &
                                 " but the table contains " & If(indices.Length = 0, "no rows", "rows " & IndexText(indices)) & ". Rows actually present are used.")
                Else
                    evidence.Add(table.Name & ": rows " & IndexText(indices) & " verified by GETNEXT walk" &
                                 If(hasDeclared, " (= " & table.LinesKey & " " & CInt(declared).ToString(CultureInfo.InvariantCulture) & ")", "") & ".")
                End If
                If indices.Length > 3 Then
                    evidence.Add(table.Name & ": " & indices.Length.ToString(CultureInfo.InvariantCulture) & " rows; only the first three are mapped to L1–L3.")
                    indices = indices.Take(3).ToArray()
                End If
                Select Case table.LinesKey
                    Case "InputLines" : profile.InputIndices = indices
                    Case "BypassLines" : profile.BypassIndices = indices
                    Case Else : profile.OutputIndices = indices
                End Select
                Dim columns = UpsOidMap.Definitions.Where(Function(item) item.PerPhase AndAlso LinesKeyFor(item.Key) = table.LinesKey).ToList()
                If indices.Length = 0 Then
                    For Each definition In columns
                        checks.Add(New UpsOidCheck With {.Key = definition.Key, .Oid = definition.Oid & "*", .Description = definition.Description,
                                                         .Unit = definition.Unit, .Status = "no table rows"})
                        unsupported.Add(definition.Key & " " & definition.Oid & "* – no table rows")
                    Next
                    Continue For
                End If
                Dim oids As New List(Of String)()
                For Each definition In columns
                    For Each index In indices
                        oids.Add(definition.Oid & index.ToString(CultureInfo.InvariantCulture))
                    Next
                Next
                Dim data = TryReadGroup(readGroup, table.Name, oids, consecutiveTimeouts)
                For Each definition In columns
                    For Each index In indices
                        checks.Add(Evaluate(definition, definition.Oid & index.ToString(CultureInfo.InvariantCulture), data, supported, unsupported))
                    Next
                Next
            Next
            If profile.SysObjectId = "1.3.6.1.4.1.476.1.42" Then
                profile.MeasurementBindings = UpsMeasurementMap.Discover(readGroup, token)
                Dim keys = profile.MeasurementBindings.Select(Function(item) item.Definition.Key).ToHashSet(StringComparer.Ordinal)
                For Each prefix In {"Input", "Bypass"}
                    If keys.Contains(prefix & "VoltageLN") OrElse keys.Contains(prefix & "VoltageLL") Then keys.Add(prefix & "Voltage")
                Next
                If UpsMeasurementMap.IsNxl(profile.Model) Then keys.Add("OutputPower")
                checks.RemoveAll(Function(item) keys.Contains(item.Key))
                unsupported.RemoveAll(Function(item) keys.Any(Function(key) item.StartsWith(key & " ", StringComparison.Ordinal)))
                If UpsMeasurementMap.IsNxl(profile.Model) Then
                    evidence.Add("NXL RFC phase power repeats the system total: excluded. Vendor system power is required.")
                End If
                For offset = 0 To profile.MeasurementBindings.Count - 1 Step 24
                    token.ThrowIfCancellationRequested()
                    Dim bindings = profile.MeasurementBindings.Skip(offset).Take(24).ToList()
                    Dim data = readGroup("Vertiv verified measurements", bindings.Select(Function(item) item.Definition.Oid).ToList())
                    For Each binding In bindings
                        Dim check = Evaluate(binding.Definition, binding.Definition.Oid, data, supported, unsupported)
                        If Not UpsMeasurementMap.Scaled(binding, UpsMeasurementMap.GetData(data, binding.Definition.Oid)).HasValue Then
                            check.InRange = False
                            check.Status = "vendor value unavailable/invalid (including sentinel)"
                            unsupported.Add(binding.Definition.Key & " " & binding.Definition.Oid & " – " & check.Status)
                        End If
                        checks.Add(check)
                    Next
                Next
                If UpsMeasurementMap.IsNxl(profile.Model) AndAlso Not profile.MeasurementBindings.Any(Function(item) item.Definition.Key = "OutputPower") Then
                    unsupported.Add("OutputPower – NXL repeated RFC totals rejected; verified vendor total unavailable")
                End If
            End If
        Catch ex As OperationCanceledException
            profile.Aborted = "Cancelled by the operator."
            profile.FailureQuality = UpsQuality.NoData
        Catch ex As UpsSnmpFailure When token.IsCancellationRequested
            ' The request in flight when the operator cancelled ended as a timeout: that is a cancellation, not evidence.
            profile.Aborted = "Cancelled by the operator."
            profile.FailureQuality = UpsQuality.NoData
        Catch ex As UpsSnmpFailure
            profile.Aborted = ex.Message
            profile.FailureQuality = ex.Quality
        End Try
        If profile.Aborted.Length = 0 AndAlso token.IsCancellationRequested Then
            profile.Aborted = "Cancelled by the operator."
            profile.FailureQuality = UpsQuality.NoData
        End If
        If profile.Aborted.Length = 0 Then
            Dim timedOut = groups.Where(Function(item) item.Status = "timeout").Select(Function(item) item.Name).ToList()
            If timedOut.Count > 0 Then
                ' Support of those objects is unknown, so the profile must not be cached as complete.
                profile.Aborted = "Group(s) not answered: " & String.Join(", ", timedOut) & ". Capability incomplete; will be re-probed."
                profile.FailureQuality = UpsQuality.Timeout
            End If
        End If
        Return Finish(profile, watch)
    End Function

    Private Function Finish(profile As UpsCapabilityProfile, watch As Stopwatch) As UpsCapabilityProfile
        profile.ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds
        If profile.DeviceId.Length = 0 Then Return profile
        Dim existing As UpsCapabilityProfile = Nothing
        Dim keepExisting = Cache.TryGetValue(profile.DeviceId, existing) AndAlso existing.Complete AndAlso
                           profile.FailureQuality = UpsQuality.NoData AndAlso Not profile.Complete
        If Not keepExisting Then Cache(profile.DeviceId) = profile
        Return profile
    End Function

    ' A group timeout is tolerated once (the group is marked, the probe continues); two in a row abort the probe.
    Private Function TryReadGroup(readGroup As Func(Of String, IList(Of String), Dictionary(Of String, ISnmpData)), name As String,
                                  oids As IList(Of String), ByRef consecutiveTimeouts As Integer) As Dictionary(Of String, ISnmpData)
        Try
            Return readGroup(name, oids)
        Catch ex As UpsSnmpFailure When ex.Quality = UpsQuality.Timeout AndAlso consecutiveTimeouts < 2
            Return New Dictionary(Of String, ISnmpData)(StringComparer.Ordinal)
        Catch ex As UpsSnmpFailure When ex.Quality = UpsQuality.Timeout
            Throw New UpsSnmpFailure(UpsQuality.Timeout, "Device stopped answering during capability discovery (2 consecutive group timeouts at '" & name & "').")
        End Try
    End Function

    Private Function Evaluate(definition As UpsOidDefinition, oid As String, data As Dictionary(Of String, ISnmpData),
                              supported As HashSet(Of String), unsupported As List(Of String)) As UpsOidCheck
        Dim check As New UpsOidCheck With {.Key = definition.Key, .Oid = oid, .Description = definition.Description, .Unit = definition.Unit}
        Dim value As ISnmpData = Nothing
        If data Is Nothing OrElse Not data.TryGetValue(oid, value) Then
            check.Status = "not answered (group timeout)"
            unsupported.Add(definition.Key & " " & oid & " – group timeout (support unknown)")
            Return check
        End If
        If IsErrorMarker(value) Then
            check.Status = value.ToString().Substring(1)
            unsupported.Add(definition.Key & " " & oid & " – " & check.Status)
            Return check
        End If
        If UpsSnmpClient.IsMissing(value) Then
            check.Status = value.TypeCode.ToString()
            unsupported.Add(definition.Key & " " & oid & " – " & check.Status)
            Return check
        End If
        check.Present = True
        check.RawValue = UpsSnmpClient.ToText(value)
        check.Status = "present (" & value.TypeCode.ToString() & ")"
        If UpsOidMap.IdentityKeys.Contains(definition.Key) Then
            check.ScaledValue = check.RawValue
            check.InRange = True
        Else
            Dim raw = UpsSnmpClient.ToNumber(value)
            If raw.HasValue Then
                Dim scaled = raw.Value * definition.Scale
                check.ScaledValue = scaled.ToString("0.###", CultureInfo.InvariantCulture) & If(definition.Unit.Length > 0, " " & definition.Unit, "")
                check.InRange = scaled >= definition.MinValid AndAlso scaled <= definition.MaxValid
                If Not check.InRange Then check.Status &= " – OUT OF RANGE"
            Else
                check.Status = "non-numeric (" & value.TypeCode.ToString() & ")"
            End If
        End If
        supported.Add(oid)
        Return check
    End Function

    ' GETNEXT walk of one table column; returns the row indices or Nothing when the agent cannot be walked.
    Private Function WalkIndices(target As IPEndPoint, credential As SnmpCredential, column As String, timeout As Integer, retries As Integer,
                                 evidence As List(Of String), tableName As String) As Integer()
        Dim indices As New List(Of Integer)()
        Dim current = column
        Try
            For row = 1 To MaxTableRows + 1
                Dim reply = GetNextWithRetries(target, credential, current, timeout, retries)
                If reply Is Nothing OrElse UpsSnmpClient.IsMissing(reply.Data) Then Exit For
                Dim id = reply.Id.ToString()
                If Not id.StartsWith(column & ".", StringComparison.Ordinal) Then Exit For
                Dim suffix = id.Substring(column.Length + 1)
                Dim index As Integer
                If suffix.Contains("."c) OrElse Not Integer.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, index) Then Exit For
                If indices.Count >= MaxTableRows Then Exit For
                indices.Add(index)
                current = id
            Next
            Return indices.ToArray()
        Catch ex As UpsSnmpFailure When ex.IsErrorResponse
            ' SNMPv1 answers noSuchName at the end of the MIB view: the rows found so far are the table.
            If ex.ErrorStatus = 2 Then Return indices.ToArray()
            evidence.Add(tableName & ": GETNEXT walk refused (" & UpsSnmpClient.ErrorStatusName(ex.ErrorStatus) & ").")
            Return Nothing
        End Try
    End Function

    Private Function GetNextWithRetries(target As IPEndPoint, credential As SnmpCredential, oid As String, timeout As Integer, retries As Integer) As Variable
        Dim attempt = 0
        Do
            Try
                Dim reply = UpsSnmpClient.GetNextValues(target, credential, {oid}, timeout)
                Return If(reply.Count = 0, Nothing, reply(0))
            Catch ex As UpsSnmpFailure When ex.Quality = UpsQuality.Timeout AndAlso attempt < retries
                attempt += 1
            End Try
        Loop
    End Function

    Friend Function GetWithRetries(target As IPEndPoint, credential As SnmpCredential, oids As IList(Of String), timeout As Integer, retries As Integer) As IList(Of Variable)
        Dim attempt = 0
        Do
            Try
                Return UpsSnmpClient.GetValues(target, credential, oids, timeout)
            Catch ex As UpsSnmpFailure When ex.Quality = UpsQuality.Timeout AndAlso attempt < retries
                attempt += 1
            End Try
        Loop
    End Function

    Private Function IsErrorMarker(value As ISnmpData) As Boolean
        Return value IsNot Nothing AndAlso value.TypeCode = SnmpType.OctetString AndAlso value.ToString().StartsWith("#ERR ", StringComparison.Ordinal)
    End Function

    Private Function TextOf(data As Dictionary(Of String, ISnmpData), oid As String) As String
        Dim value As ISnmpData = Nothing
        If data Is Nothing OrElse Not data.TryGetValue(oid, value) OrElse IsErrorMarker(value) Then Return String.Empty
        Return UpsSnmpClient.ToText(value)
    End Function

    Friend Function LinesKeyFor(key As String) As String
        If key.StartsWith("Input", StringComparison.Ordinal) Then Return "InputLines"
        If key.StartsWith("Bypass", StringComparison.Ordinal) Then Return "BypassLines"
        Return "OutputLines"
    End Function

    Friend Function IndexText(indices As Integer()) As String
        If indices Is Nothing OrElse indices.Length = 0 Then Return "none"
        Return String.Join(",", indices.Select(Function(item) item.ToString(CultureInfo.InvariantCulture)))
    End Function

    ' Plain summary of what was proven (used in the grid and reports).
    Public Function MappingText(profile As UpsCapabilityProfile) As String
        If profile Is Nothing Then Return "Not probed"
        If profile.Checks.Count = 0 Then Return "Probe failed"
        Dim present = profile.SupportedParameterCount.ToString(CultureInfo.InvariantCulture) & "/" & profile.RequestedParameterCount.ToString(CultureInfo.InvariantCulture) & " parameters"
        If Not profile.Complete Then Return "Incomplete (" & present & ")"
        If profile.CoreMapped AndAlso profile.MeasurementBindings.Count = 0 Then Return "RFC objects readable; voltage reference unverified (" & present & ")"
        If profile.FullyMapped Then Return "Fully mapped (" & present & ")"
        If profile.CoreMapped Then Return "Partially mapped (" & present & ", " & profile.UnavailableParameters.Count.ToString(CultureInfo.InvariantCulture) & " parameters unavailable)"
        Return "Core objects missing (" & present & ")"
    End Function
End Module
