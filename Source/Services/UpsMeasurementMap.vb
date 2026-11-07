Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq
Imports System.Threading
Imports Lextm.SharpSnmpLib

Public NotInheritable Class UpsMeasurementBinding
    Public Property Definition As UpsOidDefinition
    Public Property Phase As Integer = -1
    Public Property Label As String = String.Empty
    Public Property CardUnit As String = String.Empty
    Public Property Index As String = String.Empty
End Class

' Flexible-MIB string values are already scaled by the card. Bind only when both the
' permanent point label and its engineering unit match; sysObjectID alone is insufficient.
Friend Module UpsMeasurementMap
    Public Const Revision As Integer = 2
    Private Const Root As String = "1.3.6.1.4.1.476.1.42.3.9.20.1."
    Public ReadOnly Candidates As IReadOnlyList(Of UpsMeasurementBinding) = Build()

    Private Function Build() As IReadOnlyList(Of UpsMeasurementBinding)
        Dim result As New List(Of UpsMeasurementBinding)()
        Dim add = Sub(key As String, point As Integer, label As String, cardUnit As String, unit As String,
                      scale As Double, phase As Integer, max As Double)
                      Dim index = "1.2.1." & point.ToString(CultureInfo.InvariantCulture)
                      result.Add(New UpsMeasurementBinding With {.Index = index, .Phase = phase, .Label = label, .CardUnit = cardUnit,
                          .Definition = New UpsOidDefinition With {.Key = key, .Oid = Root & "20." & index,
                              .Description = label, .Unit = unit, .Scale = scale, .PerPhase = phase >= 0, .MinValid = 0, .MaxValid = max}})
                  End Sub
        For phase = 0 To 2
            Dim letter = ChrW(AscW("A"c) + phase)
            Dim pair = {"A-B", "B-C", "C-A"}(phase)
            add("InputVoltageLL", 4097 + phase * 2, "System Input RMS " & pair, "VAC", "V", 1, phase, 1000)
            add("InputVoltageLN", 4096 + phase * 2, "System Input RMS " & letter & "-N", "VAC", "V", 1, phase, 1000)
            add("InputCurrent", 4113 + phase, "System Input RMS Current Phase " & letter, "A AC", "A", 1, phase, 10000)
            add("BypassVoltageLL", 4125 + phase, "Bypass Input Voltage RMS " & pair, "VAC", "V", 1, phase, 1000)
            add("BypassVoltageLN", 4128 + phase, "Bypass Input Voltage RMS " & letter & "-N", "VAC", "V", 1, phase, 1000)
            add("OutputVoltageLL", 4201 + phase, "System Output Voltage RMS " & pair, "VAC", "V", 1, phase, 1000)
            add("OutputVoltage", 4385 + phase, "System Output Voltage RMS " & letter & "-N", "VAC", "V", 1, phase, 1000)
            add("OutputCurrent", 4204 + phase, "System Output RMS Current Phs " & letter, "A AC", "A", 1, phase, 10000)
            add("OutputPhasePf", 4210 + phase, "System Output Power Factor Phs " & letter, "", "", 1, phase, 1)
            add("OutputLoad", 4223 + phase, "System Output Pct Power Phase " & letter, "%", "%", 1, phase, 200)
            add("OutputVaLoad", 4226 + phase, "System Output Pct Pwr (VA) Phs " & letter, "%", "%", 1, phase, 200)
            add("OutputPower", {5859, 5860, 5959}(phase), "System Output Power Phase " & letter, "kW", "W", 1000, phase, 5000000)
            add("OutputApparentVa", 5868 + phase, "System Output Apparent Power Phs " & letter, "kVA", "VA", 1000, phase, 10000000)
            add("InputPower", 6318 + phase, "System Input Power Phase " & letter, "kW", "W", 1000, phase, 5000000)
        Next
        add("InputFrequency", 4105, "System Input Frequency", "Hz", "Hz", 1, -1, 100)
        add("BypassFrequency", 4131, "Bypass Input Frequency", "Hz", "Hz", 1, -1, 100)
        add("OutputFrequency", 4207, "System Output Frequency", "Hz", "Hz", 1, -1, 100)
        add("OutputPower", 4208, "System Output Power", "kW", "W", 1000, -1, 5000000)
        add("OutputApparentVa", 4209, "System Output Apparent Power", "kVA", "VA", 1000, -1, 10000000)
        add("RuntimeMinutes", 4150, "Battery Time Remaining", "min", "min", 1, -1, 100000)
        add("ChargePercent", 4153, "Battery Percentage Charge", "", "%", 1, -1, 100)
        add("BatteryVoltage", 4154, "Battery Volts at Main Disconnect", "VDC", "V", 1, -1, 2000)
        add("BatteryCabinetVoltage", 4155, "Battery Volts for Cabinet", "VDC", "V", 1, -1, 2000)
        add("BatteryCabinetTemperature", 4156, "Battery Temperature for Cabinet", "deg C", "°C", 1, -1, 150)
        result.Last().Definition.MinValid = -40
        add("SecondsOnBattery", 4151, "Battery Discharge Time", "sec", "s", 1, -1, 10000000)
        add("BatteryDischargeHours", 4152, "Battery Total Discharge Time", "hr", "h", 1, -1, 10000000)
        add("BatteryDischargePower", 4159, "Battery Discharge Power", "W", "W", 1, -1, 5000000)
        add("BatteryAhConsumed", 4158, "Battery Amp-Hours Consumed", "AH", "Ah", 1, -1, 1000000000)
        add("DcBusVoltage", 4148, "DC Bus Voltage", "VDC", "V", 1, -1, 2000)
        add("DcBusCurrent", 4149, "DC Bus Current", "A DC", "A", 1, -1, 10000)
        result.Last().Definition.MinValid = -10000
        add("InletTemperature", 4291, "Inlet Air Temperature", "deg C", "°C", 1, -1, 150)
        result.Last().Definition.MinValid = -40
        Return result
    End Function

    Public Function IsNxl(model As String) As Boolean
        Return model IsNot Nothing AndAlso model.StartsWith("49SN", StringComparison.OrdinalIgnoreCase)
    End Function

    Public Function MetadataMatches(binding As UpsMeasurementBinding, label As ISnmpData, unit As ISnmpData) As Boolean
        If UpsSnmpClient.IsMissing(label) OrElse UpsSnmpClient.ToText(label).Trim() <> binding.Label Then Return False
        If binding.CardUnit.Length = 0 Then
            If binding.Definition.Key = "ChargePercent" AndAlso Not UpsSnmpClient.IsMissing(unit) AndAlso
               UpsSnmpClient.ToText(unit).Trim() = "%" Then Return True
            Return UpsSnmpClient.IsMissing(unit) OrElse UpsSnmpClient.ToText(unit).Trim().Length = 0
        End If
        Return Not UpsSnmpClient.IsMissing(unit) AndAlso UpsSnmpClient.ToText(unit).Trim() = binding.CardUnit
    End Function

    Public Function Scaled(binding As UpsMeasurementBinding, data As ISnmpData) As Double?
        If UpsSnmpClient.IsMissing(data) Then Return Nothing
        Dim raw As Double
        If Not Double.TryParse(UpsSnmpClient.ToText(data), NumberStyles.Float, CultureInfo.InvariantCulture, raw) OrElse
           Not Double.IsFinite(raw) OrElse (binding.Definition.Key = "RuntimeMinutes" AndAlso raw = 65535) Then Return Nothing
        Dim value = raw * binding.Definition.Scale
        Return If(value >= binding.Definition.MinValid AndAlso value <= binding.Definition.MaxValid, value, CType(Nothing, Double?))
    End Function

    Public Function Discover(read As Func(Of String, IList(Of String), Dictionary(Of String, ISnmpData)),
                             token As CancellationToken) As IReadOnlyList(Of UpsMeasurementBinding)
        Dim gate = Candidates.Single(Function(item) item.Definition.Key = "OutputVoltage" AndAlso item.Phase = 0)
        Dim probe = read("Vertiv measurement identity", {Root & "10." & gate.Index, Root & "30." & gate.Index})
        If Not MetadataMatches(gate, GetData(probe, Root & "10." & gate.Index), GetData(probe, Root & "30." & gate.Index)) Then
            Return Array.Empty(Of UpsMeasurementBinding)()
        End If
        Dim metadata As New Dictionary(Of String, ISnmpData)(StringComparer.Ordinal)
        Dim oids = Candidates.SelectMany(Function(item) {Root & "10." & item.Index, Root & "30." & item.Index}).ToList()
        For offset = 0 To oids.Count - 1 Step 12
            token.ThrowIfCancellationRequested()
            For Each item In read("Vertiv measurement labels / units", oids.Skip(offset).Take(12).ToList())
                metadata(item.Key) = item.Value
            Next
        Next
        Dim matched = Candidates.Where(Function(item) MetadataMatches(item, GetData(metadata, Root & "10." & item.Index),
                                                                     GetData(metadata, Root & "30." & item.Index))).ToList()
        Dim result As New List(Of UpsMeasurementBinding)()
        For offset = 0 To matched.Count - 1 Step 12
            token.ThrowIfCancellationRequested()
            Dim batch = matched.Skip(offset).Take(12).ToList()
            Dim data = read("Vertiv measurement availability", batch.Select(Function(item) item.Definition.Oid).ToList())
            For Each item In batch
                ' EXL exposes some labels/units even when the value explicitly says No Support.
                If Not String.Equals(UpsSnmpClient.ToText(GetData(data, item.Definition.Oid)), "No Support", StringComparison.OrdinalIgnoreCase) Then result.Add(item)
            Next
        Next
        Return result
    End Function

    Public Sub Apply(bindings As IReadOnlyList(Of UpsMeasurementBinding), data As IDictionary(Of String, ISnmpData),
                     snapshot As UpsSnapshot, values As Dictionary(Of String, Double), phases As Dictionary(Of String, Double?()),
                     invalid As List(Of String))
        Dim sources = snapshot.MeasurementSources.ToDictionary(Function(item) item.Key, Function(item) item.Value, StringComparer.Ordinal)
        Dim rawMeasurements = snapshot.RawMeasurements.ToDictionary(Function(item) item.Key, Function(item) item.Value, StringComparer.Ordinal)
        ' These cards publish the system total in every RFC phase-power row. Never use
        ' that sum, even when the authoritative vendor objects are temporarily absent.
        If IsNxl(snapshot.IdentModel) OrElse bindings.Any(Function(item) item.Definition.Key = "OutputPower") Then phases.Remove("OutputPower")
        For Each group In bindings.GroupBy(Function(item) item.Definition.Key)
            Dim key = group.Key
            values.Remove(key)
            phases.Remove(key)
            Dim items(2) As Double?
            For Each binding In group
                Dim value = Scaled(binding, GetData(data, binding.Definition.Oid))
                If Not value.HasValue Then
                    invalid.Add(key & If(binding.Phase < 0, "", " L" & (binding.Phase + 1).ToString(CultureInfo.InvariantCulture)) & " (vendor value unavailable/invalid)")
                ElseIf binding.Phase < 0 Then
                    values(key) = value.Value
                Else
                    items(binding.Phase) = value
                End If
            Next
            If group.Any(Function(item) item.Phase >= 0) Then phases(key) = items
            sources(key) = String.Join("; ", group.Select(Function(item) item.Definition.Oid & " ×" &
                                                        item.Definition.Scale.ToString("0.###", CultureInfo.InvariantCulture)))
        Next
        For Each prefix In {"Input", "Bypass"}
            Dim ln = prefix & "VoltageLN"
            Dim ll = prefix & "VoltageLL"
            Dim hasLn = phases.ContainsKey(ln) AndAlso phases(ln).Any(Function(value) value.HasValue AndAlso Double.IsFinite(value.Value))
            Dim hasLl = phases.ContainsKey(ll) AndAlso phases(ll).Any(Function(value) value.HasValue AndAlso Double.IsFinite(value.Value))
            Dim key = If(hasLn, ln, If(hasLl, ll, If(phases.ContainsKey(ln), ln, If(phases.ContainsKey(ll), ll, Nothing))))
            If key IsNot Nothing Then
                phases(prefix & "Voltage") = phases(key)
                sources(prefix & "Voltage") = sources(key)
                If prefix = "Input" Then
                    snapshot.InputVoltageReference = If(key = ln, "L-N", "L-L (AB / BC / CA)")
                Else
                    snapshot.BypassVoltageReference = If(key = ln, "L-N", "L-L (AB / BC / CA)")
                End If
            End If
        Next
        snapshot.MeasurementSources = sources
        For Each binding In bindings
            Dim value = GetData(data, binding.Definition.Oid)
            rawMeasurements(binding.Definition.Oid) = If(UpsSnmpClient.IsMissing(value), "unavailable", UpsSnmpClient.ToText(value))
        Next
        snapshot.RawMeasurements = rawMeasurements
        snapshot.MeasurementMap = If(bindings.Count > 0, "Vertiv Flexible-MIB (labels and units verified)", "RFC1628 (voltage reference unspecified)")
        If bindings.Any(Function(item) item.Definition.Key = "OutputVoltage") Then snapshot.OutputVoltageReference = "L-N"
        If IsNxl(snapshot.IdentModel) AndAlso Not values.ContainsKey("OutputPower") AndAlso Not phases.ContainsKey("OutputPower") Then
            invalid.Add("NXL system output power unavailable; repeated RFC phase totals rejected")
        End If
    End Sub

    Public Function GetData(data As IDictionary(Of String, ISnmpData), oid As String) As ISnmpData
        Dim value As ISnmpData = Nothing
        If data IsNot Nothing Then data.TryGetValue(oid, value)
        Return value
    End Function
End Module
