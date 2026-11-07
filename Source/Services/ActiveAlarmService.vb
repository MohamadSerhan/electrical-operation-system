Imports System.IO
Imports System.Linq

Public NotInheritable Class ActiveAlarmAsset
    Public Property EquipmentType As String
    Public Property Id As String
    Public Property Name As String
    Public Property FreshnessSeconds As Double
    Public Property Enabled As Boolean = True
End Class

Public NotInheritable Class ActiveAlarmRow
    Public Property EquipmentType As String
    Public Property EquipmentId As String
    Public Property EquipmentName As String
    Public Property ConditionKey As String
    Public Property Condition As String
    Public Property Category As String
    Public Property State As String
    Public Property Severity As String
    Public Property ObservedUtc As DateTime?
    Public Property Detail As String
End Class

Public NotInheritable Class ActiveAlarmSnapshot
    Public Property CheckedUtc As DateTime
    Public Property Rows As IReadOnlyList(Of ActiveAlarmRow) = Array.Empty(Of ActiveAlarmRow)()
    Public Property FreshAssets As Integer
    Public Property ExpectedAssets As Integer
    Public Property Coverage As String
End Class

Public NotInheritable Class ActiveAlarmService
    Private ReadOnly _recorder As EquipmentAlarmRecorder
    Private ReadOnly _assets As Func(Of IReadOnlyList(Of ActiveAlarmAsset))

    Public Sub New(recorder As EquipmentAlarmRecorder, assets As Func(Of IReadOnlyList(Of ActiveAlarmAsset)))
        If recorder Is Nothing Then Throw New ArgumentNullException(NameOf(recorder))
        If assets Is Nothing Then Throw New ArgumentNullException(NameOf(assets))
        _recorder = recorder
        _assets = assets
    End Sub

    Public Shared Function Create(recorder As EquipmentAlarmRecorder, plc As Func(Of PlcSettings)) As ActiveAlarmService
        Return New ActiveAlarmService(recorder,
            Function()
                Dim settings = plc()
                Dim assets = {"T1", "T2", "T3", "T4", "TOTAL"}.Select(Function(id) New ActiveAlarmAsset With {
                    .EquipmentType = "Transformer", .Id = id, .Name = If(id = "TOTAL", "Transformer TOTAL / ECG", "Transformer " & id.Substring(1)),
                    .FreshnessSeconds = Math.Max(5, settings.EffectivePollingIntervalMilliseconds / 1000.0R * 3 + 3)}).ToList()
                Dim ups = UpsSettingsStore.Current
                assets.AddRange(ups.Devices.Select(Function(device) New ActiveAlarmAsset With {
                    .EquipmentType = "UPS", .Id = device.Id, .Name = device.Id,
                    .Enabled = ups.AcquisitionEnabled AndAlso device.Enabled,
                    .FreshnessSeconds = Math.Max(15, device.PollIntervalSeconds * 3 + device.TimeoutMilliseconds / 1000.0R * 3)}))
                Return assets
            End Function)
    End Function

    Public Function Read(utc As DateTime) As ActiveAlarmSnapshot
        If utc.Kind <> DateTimeKind.Utc Then Throw New InvalidDataException("Active alarm snapshot requires UTC.")
        Dim assets = _assets()
        If assets Is Nothing OrElse assets.Select(Function(asset) asset.EquipmentType & ":" & asset.Id).Distinct().Count() <> assets.Count Then
            Throw New InvalidDataException("Active alarm equipment catalog is invalid.")
        End If
        Dim result As New ActiveAlarmSnapshot With {.CheckedUtc = utc, .ExpectedAssets = assets.Where(Function(asset) asset.Enabled).Count()}
        Dim rows As New List(Of ActiveAlarmRow)
        For Each asset In assets
            If Not Double.IsFinite(asset.FreshnessSeconds) OrElse asset.FreshnessSeconds <= 0 Then Throw New InvalidDataException("Invalid alarm freshness limit: " & asset.Id)
            Dim current = _recorder.ReadCurrentState(asset.EquipmentType, asset.Id)
            If current.ReadUtc.Kind <> DateTimeKind.Utc Then Throw New InvalidDataException("Active alarm observation clock must be UTC.")
            Dim checkedUtc = If(current.ReadUtc > utc, current.ReadUtc, utc)
            If checkedUtc > result.CheckedUtc Then result.CheckedUtc = checkedUtc
            Dim saved = current.State
            If saved IsNot Nothing Then EquipmentAlarmRecorder.SetLabels(saved.Evidence)
            Dim fresh = asset.Enabled AndAlso saved IsNot Nothing AndAlso current.CurrentSession AndAlso
                saved.ObservedUtc <= checkedUtc AndAlso (checkedUtc - saved.ObservedUtc).TotalSeconds <= asset.FreshnessSeconds AndAlso
                saved.AcquiredUtc.HasValue AndAlso saved.AcquiredUtc.Value.Kind = DateTimeKind.Utc AndAlso
                saved.AcquiredUtc.Value <= checkedUtc AndAlso (checkedUtc - saved.AcquiredUtc.Value).TotalSeconds <= asset.FreshnessSeconds
            If fresh Then result.FreshAssets += 1
            If Not fresh Then
                rows.Add(New ActiveAlarmRow With {.EquipmentType = asset.EquipmentType, .EquipmentId = asset.Id,
                    .EquipmentName = asset.Name, .ConditionKey = "__monitoring", .Condition = "Monitoring unavailable",
                    .Category = "Monitoring", .State = "Unknown", .Severity = "Unknown",
                    .ObservedUtc = If(saved Is Nothing, CType(Nothing, DateTime?), saved.ObservedUtc),
                    .Detail = If(Not asset.Enabled, "Acquisition disabled; not proof of normal.",
                        If(saved Is Nothing OrElse Not current.CurrentSession, "Waiting for a fresh observation in this monitoring session.",
                            "No fresh observation within " & asset.FreshnessSeconds.ToString("0.#", Globalization.CultureInfo.InvariantCulture) & " seconds."))})
            End If
            If saved Is Nothing Then Continue For
            For Each entry In saved.Evidence.Conditions
                If entry.Key.StartsWith("__", StringComparison.Ordinal) OrElse entry.Key.StartsWith("voltage:", StringComparison.Ordinal) Then Continue For
                If entry.Key = "reported-alarms" AndAlso saved.Evidence.Complete AndAlso saved.Evidence.Conditions.Keys.Any(
                    Function(key) key.StartsWith("card:", StringComparison.Ordinal)) Then Continue For
                If entry.Key = "ecg-back" Then Continue For
                If entry.Key = "ecg-off" AndAlso asset.EquipmentType = "Transformer" AndAlso asset.Id <> "TOTAL" Then Continue For
                Dim state = entry.Value
                Dim active = state.Active.GetValueOrDefault()
                Dim unresolved = Not state.Active.HasValue AndAlso state.LastKnownActive.GetValueOrDefault()
                If Not active AndAlso Not unresolved Then Continue For
                Dim confirmed = fresh AndAlso state.Active.HasValue
                rows.Add(New ActiveAlarmRow With {.EquipmentType = asset.EquipmentType, .EquipmentId = asset.Id,
                    .EquipmentName = asset.Name, .ConditionKey = entry.Key, .Condition = state.Name,
                    .Category = state.Category, .State = If(confirmed, "Active", "Unknown"),
                    .Severity = If(confirmed, state.Severity, "Unknown"), .ObservedUtc = saved.ObservedUtc,
                    .Detail = If(confirmed, state.Detail, "Previously active; not confirmed cleared. " & state.Detail)})
            Next
        Next
        result.Rows = rows.OrderBy(Function(row) If(row.State = "Active", 0, 1)).
            ThenBy(Function(row) If(row.Category = "Fault", 0, 1)).ThenBy(Function(row) row.EquipmentType).
            ThenBy(Function(row) row.EquipmentId).ThenBy(Function(row) row.Condition).ToArray()
        result.Coverage = result.FreshAssets & "/" & result.ExpectedAssets & " enabled assets observed recently. " &
            "Fire alarm: NOT CONNECTED (alarm/fault acquisition; connection diagnostics alone are not alarm monitoring). Generators: NOT CONNECTED (no acquisition source). " &
            "Unknown is not cleared. Fresh observations do not prove every optional device signal is supported."
        Return result
    End Function
End Class
