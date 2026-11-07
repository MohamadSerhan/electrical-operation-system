Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Text.Json

' Quality of one UPS acquisition. Anything other than Good means the values are NOT current and must never be shown as zero.
Public Enum UpsQuality
    NoData = 0
    Good = 1
    Stale = 2
    Timeout = 3
    AuthFailure = 4
    NotSupported = 5
    Invalid = 6
    Offline = 7
    ConfigError = 8
    Disabled = 9
End Enum

' How far the registry entry has been proven against the real device.
Public Enum UpsVerification
    Unverified = 0
    Verified = 1
    Provisional = 2
    Conflict = 3
End Enum

Public NotInheritable Class UpsDevice
    ' Immutable key used by the historian; never derived from the label or the IP address.
    Public Property Id As String = String.Empty
    Public Property Label As String = String.Empty
    Public Property AtsBank As Integer = 1
    Public Property IpAddress As String = String.Empty
    Public Property Port As Integer = 161
    Public Property CredentialProfile As String = "default"
    Public Property UseProfileTransport As Boolean
    Public Property Model As String = String.Empty
    Public Property RatedKva As Double = 500.0R
    Public Property Enabled As Boolean = True
    Public Property PollIntervalSeconds As Integer = 1
    Public Property TimeoutMilliseconds As Integer = 2000
    Public Property Retries As Integer = 1
    Public Property Verification As UpsVerification = UpsVerification.Unverified
    Public Property Notes As String = String.Empty

    Public Function Clone() As UpsDevice
        Return DirectCast(MemberwiseClone(), UpsDevice)
    End Function

    Public ReadOnly Property DisplayName As String
        Get
            Return If(String.IsNullOrWhiteSpace(Label), Id, Label)
        End Get
    End Property
End Class

Public NotInheritable Class AtsBankDefinition
    Public Property Number As Integer
    Public Property Name As String = String.Empty
    Public Property NameplateKva As Double
    Public Property SourceDescription As String = String.Empty

    Public Function Clone() As AtsBankDefinition
        Return DirectCast(MemberwiseClone(), AtsBankDefinition)
    End Function
End Class

Public NotInheritable Class UpsSettings
    Public Property SchemaVersion As Integer = 1
    Public Property AcquisitionEnabled As Boolean = True
    Public Property MaxConcurrentPolls As Integer = 4
    Public Property HistorianEnabled As Boolean = True
    Public Property HistorianIntervalSeconds As Integer = 60
    Public Property HistorianRetentionDays As Integer = 365
    Public Property Banks As List(Of AtsBankDefinition) = New List(Of AtsBankDefinition)()
    Public Property Devices As List(Of UpsDevice) = New List(Of UpsDevice)()

    Public Function Clone() As UpsSettings
        Dim copy = DirectCast(MemberwiseClone(), UpsSettings)
        copy.Banks = Banks.Select(Function(bank) bank.Clone()).ToList()
        copy.Devices = Devices.Select(Function(device) device.Clone()).ToList()
        Return copy
    End Function

    Public Function Bank(number As Integer) As AtsBankDefinition
        Return Banks.FirstOrDefault(Function(item) item.Number = number)
    End Function

    Public Function InstalledUpsCapacityKva(Optional atsBankNumber As Integer? = Nothing) As Double
        Return Devices.Where(Function(device) Not atsBankNumber.HasValue OrElse device.AtsBank = atsBankNumber.Value).
            Where(Function(device) Double.IsFinite(device.RatedKva) AndAlso device.RatedKva > 0).
            Sum(Function(device) device.RatedKva)
    End Function

    Public Sub Validate()
        If MaxConcurrentPolls < 1 OrElse MaxConcurrentPolls > 16 Then Throw New InvalidDataException("Concurrent polls must be 1–16.")
        If HistorianIntervalSeconds < 10 OrElse HistorianIntervalSeconds > 3600 Then Throw New InvalidDataException("UPS historian interval must be 10–3600 s.")
        If HistorianRetentionDays < 7 OrElse HistorianRetentionDays > 3650 Then Throw New InvalidDataException("UPS historian retention must be 7–3650 days.")
        If Banks.Count = 0 Then Throw New InvalidDataException("At least one ATS bank is required.")
        Dim ids As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim endpoints As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim labels As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each device In Devices
            If String.IsNullOrWhiteSpace(device.Id) Then Throw New InvalidDataException("Every UPS needs a unique ID.")
            If Not ids.Add(device.Id.Trim()) Then Throw New InvalidDataException("Duplicate UPS ID: " & device.Id)
            If String.IsNullOrWhiteSpace(device.Label) OrElse Not labels.Add(device.Label.Trim()) Then Throw New InvalidDataException("UPS labels must be nonempty and unique: " & device.Label)
            Dim address As IPAddress = Nothing
            If Not IPAddress.TryParse(If(device.IpAddress, String.Empty).Trim(), address) OrElse
               address.AddressFamily <> Net.Sockets.AddressFamily.InterNetwork Then
                Throw New InvalidDataException(device.Id & ": IP address '" & device.IpAddress & "' is not a valid IPv4 address.")
            End If
            If device.Port < 1 OrElse device.Port > 65535 Then Throw New InvalidDataException(device.Id & ": port must be 1–65535.")
            If Not endpoints.Add(address.ToString()) Then
                Throw New InvalidDataException(device.Id & ": IP " & address.ToString() & " is already assigned to another UPS.")
            End If
            If Bank(device.AtsBank) Is Nothing Then Throw New InvalidDataException(device.Id & ": ATS bank " & device.AtsBank & " does not exist.")
            If device.PollIntervalSeconds < 1 OrElse device.PollIntervalSeconds > 600 Then Throw New InvalidDataException(device.Id & ": poll interval must be 1–600 s.")
            If device.TimeoutMilliseconds < 200 OrElse device.TimeoutMilliseconds > 10000 Then Throw New InvalidDataException(device.Id & ": timeout must be 200–10000 ms.")
            If device.Retries < 0 OrElse device.Retries > 3 Then Throw New InvalidDataException(device.Id & ": retries must be 0–3.")
            If Not Double.IsFinite(device.RatedKva) OrElse device.RatedKva <= 0 OrElse device.RatedKva > 5000 Then Throw New InvalidDataException(device.Id & ": rated kVA must be 1–5000.")
            If String.IsNullOrWhiteSpace(device.CredentialProfile) Then Throw New InvalidDataException(device.Id & ": a credential profile is required.")
        Next
    End Sub

    ' Factory registry from the site specification. Verification reflects the read-only discovery of 26-Oct-2026:
    ' only .7, .16 and .17 answered SNMP v2c; their sysName does not match the supplied labels (conflict flagged).
    Public Shared Function CreateDefault() As UpsSettings
        Dim settings As New UpsSettings()
        settings.Banks.Add(New AtsBankDefinition With {.Number = 1, .Name = "ATS 1", .NameplateKva = 2400, .SourceDescription = "ECG Transformer 1 / Generator bus"})
        settings.Banks.Add(New AtsBankDefinition With {.Number = 2, .Name = "ATS 2", .NameplateKva = 2400, .SourceDescription = "ECG Transformer 2 / Generator bus"})
        settings.Banks.Add(New AtsBankDefinition With {.Number = 3, .Name = "ATS 3", .NameplateKva = 2500, .SourceDescription = "ECG Transformer 3 / Generator bus"})
        settings.Banks.Add(New AtsBankDefinition With {.Number = 4, .Name = "ATS 4", .NameplateKva = 2000, .SourceDescription = "ECG Transformer 4 / Generator bus"})
        ' Evidence from the 26 Oct 2026 layered test (ping, v2c/v1 GET, credential-free SNMPv3 engine discovery).
        Const agentAlive As String = "26 Oct 2026: ping OK; Liebert SNMP agent answers SNMPv3 discovery; v1/v2c GET with profile 'default' unanswered. Card-side SNMP access check required."
        Const hostUpNoSnmp As String = "26 Oct 2026: ping/HTTP OK; no answer on UDP 161 (v1, v2c or SNMPv3 discovery). Cause not yet determined."
        AddUnit(settings, "UPS-A01", "A01", 1, "192.168.107.11", "Liebert NXL 800 kVA", 800, UpsVerification.Unverified, agentAlive)
        AddUnit(settings, "UPS-A02", "A02", 1, "192.168.107.10", "Liebert NXL 800 kVA", 800, UpsVerification.Unverified, agentAlive)
        AddUnit(settings, "UPS-A03", "A03", 1, "192.168.107.9", "Liebert NXL 800 kVA", 800, UpsVerification.Unverified, agentAlive)
        AddUnit(settings, "UPS-B01", "B01", 2, "192.168.107.12", "Liebert NXL 800 kVA", 800, UpsVerification.Unverified, agentAlive)
        AddUnit(settings, "UPS-B02", "B02", 2, "192.168.107.13", "Liebert NXL 800 kVA", 800, UpsVerification.Unverified, agentAlive)
        AddUnit(settings, "UPS-B03", "B03", 2, "192.168.107.14", "Liebert NXL 800 kVA", 800, UpsVerification.Unverified, agentAlive)
        AddUnit(settings, "UPS-C01", "C01", 3, "192.168.107.7", "Chloride EXL-S1 500 kVA", 500, UpsVerification.Verified,
                "SNMP v2c verified (RFC 1628). Label C01 confirmed by site (device sysName 'UPS : 7').")
        AddUnit(settings, "UPS-C02", "C02", 3, "192.168.107.8", "EXL 500 kVA", 500, UpsVerification.Unverified, "26 Oct 2026: no reply to ping, HTTP or SNMP. Host/card down, cabling or wrong IP.")
        AddUnit(settings, "UPS-C03", "C03", 3, "192.168.107.17", "Chloride EXL-S1 500 kVA", 500, UpsVerification.Verified,
                "SNMP v2c verified (RFC 1628). Label C03 confirmed by site (device sysName 'UPS - 09').")
        AddUnit(settings, "UPS-C04", "C04", 3, "192.168.107.16", "Chloride EXL-S1 500 kVA", 500, UpsVerification.Verified,
                "SNMP v2c verified (RFC 1628). Label C04 confirmed by site (device sysName 'UPS - 11').")
        AddUnit(settings, "UPS-C05", "C05", 3, "192.168.107.15", "EXL 500 kVA", 500, UpsVerification.Unverified, agentAlive)
        AddUnit(settings, "UPS-D01", "D01 (unverified)", 4, "192.168.107.6", "EXL 500 kVA", 500, UpsVerification.Provisional,
                "Identity unconfirmed: listed as D01/S1 and also as D04. No reply to ping, HTTP or SNMP (26 Oct 2026).")
        AddUnit(settings, "UPS-D02", "D02", 4, "192.168.107.18", "EXL 500 kVA", 500, UpsVerification.Unverified, hostUpNoSnmp)
        AddUnit(settings, "UPS-D03", "D03", 4, "192.168.107.19", "EXL 500 kVA", 500, UpsVerification.Unverified, hostUpNoSnmp)
        AddUnit(settings, "UPS-D04", "D04", 4, "192.168.107.20", "EXL 500 kVA", 500, UpsVerification.Unverified, hostUpNoSnmp)
        Return settings
    End Function

    Private Shared Sub AddUnit(settings As UpsSettings, id As String, label As String, bank As Integer, ip As String,
                               model As String, ratedKva As Double, verification As UpsVerification, notes As String)
        settings.Devices.Add(New UpsDevice With {
            .Id = id, .Label = label, .AtsBank = bank, .IpAddress = ip, .Model = model, .RatedKva = ratedKva,
            .Verification = verification, .Notes = notes})
    End Sub
End Class

' RFC 1628 UPS-MIB objects verified on Chloride EXL-S1 (Liebert IntelliSlot, sysObjectID 1.3.6.1.4.1.476.1.42).
' Scale is the factor applied to the raw integer to obtain engineering units.
Public NotInheritable Class UpsOidDefinition
    Public Property Key As String = String.Empty
    Public Property Oid As String = String.Empty
    Public Property Description As String = String.Empty
    Public Property Unit As String = String.Empty
    Public Property Scale As Double = 1.0R
    Public Property PerPhase As Boolean
    Public Property MinValid As Double = Double.MinValue
    Public Property MaxValid As Double = Double.MaxValue
End Class

Public Module UpsOidMap
    Public Const SysDescr As String = "1.3.6.1.2.1.1.1.0"
    Public Const SysObjectId As String = "1.3.6.1.2.1.1.2.0"
    Public Const SysUpTime As String = "1.3.6.1.2.1.1.3.0"
    Public Const SysName As String = "1.3.6.1.2.1.1.5.0"
    Public Const UpsMibRoot As String = "1.3.6.1.2.1.33"

    Public ReadOnly Definitions As IReadOnlyList(Of UpsOidDefinition) = BuildDefinitions()
    ' Text identity objects; every other scalar is numeric.
    Public ReadOnly IdentityKeys As New HashSet(Of String)({"Manufacturer", "IdentModel", "UpsSoftware", "AgentSoftware"}, StringComparer.Ordinal)

    Private Function D(key As String, oid As String, description As String, unit As String, scale As Double,
                       perPhase As Boolean, minValid As Double, maxValid As Double) As UpsOidDefinition
        Return New UpsOidDefinition With {.Key = key, .Oid = oid, .Description = description, .Unit = unit, .Scale = scale,
                                          .PerPhase = perPhase, .MinValid = minValid, .MaxValid = maxValid}
    End Function

    Private Function BuildDefinitions() As IReadOnlyList(Of UpsOidDefinition)
        Const b As String = "1.3.6.1.2.1.33.1."
        Return New List(Of UpsOidDefinition) From {
            D("Manufacturer", b & "1.1.0", "upsIdentManufacturer", "", 1, False, Double.MinValue, Double.MaxValue),
            D("IdentModel", b & "1.2.0", "upsIdentModel", "", 1, False, Double.MinValue, Double.MaxValue),
            D("UpsSoftware", b & "1.3.0", "upsIdentUPSSoftwareVersion", "", 1, False, Double.MinValue, Double.MaxValue),
            D("AgentSoftware", b & "1.4.0", "upsIdentAgentSoftwareVersion", "", 1, False, Double.MinValue, Double.MaxValue),
            D("BatteryStatus", b & "2.1.0", "upsBatteryStatus (1 unknown, 2 normal, 3 low, 4 depleted)", "", 1, False, 1, 4),
            D("SecondsOnBattery", b & "2.2.0", "upsSecondsOnBattery", "s", 1, False, 0, 10000000),
            D("RuntimeMinutes", b & "2.3.0", "upsEstimatedMinutesRemaining", "min", 1, False, 0, 100000),
            D("ChargePercent", b & "2.4.0", "upsEstimatedChargeRemaining", "%", 1, False, 0, 100),
            D("BatteryVoltage", b & "2.5.0", "upsBatteryVoltage", "V", 0.1R, False, 0, 2000),
            D("BatteryCurrent", b & "2.6.0", "upsBatteryCurrent (signed)", "A", 0.1R, False, -10000, 10000),
            D("BatteryTemperature", b & "2.7.0", "upsBatteryTemperature", "°C", 1, False, -40, 150),
            D("InputLineBads", b & "3.1.0", "upsInputLineBads (counter)", "", 1, False, 0, Double.MaxValue),
            D("InputLines", b & "3.2.0", "upsInputNumLines", "", 1, False, 0, 3),
            D("InputFrequency", b & "3.3.1.2.", "upsInputFrequency", "Hz", 0.1R, True, 0, 100),
            D("InputVoltage", b & "3.3.1.3.", "upsInputVoltage", "V", 1, True, 0, 1000),
            D("InputCurrent", b & "3.3.1.4.", "upsInputCurrent", "A", 0.1R, True, 0, 10000),
            D("InputPower", b & "3.3.1.5.", "upsInputTruePower (card-reported)", "W", 1, True, 0, 5000000),
            D("OutputSource", b & "4.1.0", "upsOutputSource (2 none, 3 normal, 4 bypass, 5 battery, 6 booster, 7 reducer)", "", 1, False, 1, 7),
            D("OutputFrequency", b & "4.2.0", "upsOutputFrequency", "Hz", 0.1R, False, 0, 100),
            D("OutputLines", b & "4.3.0", "upsOutputNumLines", "", 1, False, 0, 3),
            D("OutputVoltage", b & "4.4.1.2.", "upsOutputVoltage", "V", 1, True, 0, 1000),
            D("OutputCurrent", b & "4.4.1.3.", "upsOutputCurrent", "A", 0.1R, True, 0, 10000),
            D("OutputPower", b & "4.4.1.4.", "upsOutputPower (true power, W)", "W", 1, True, 0, 5000000),
            D("OutputLoad", b & "4.4.1.5.", "upsOutputPercentLoad", "%", 1, True, 0, 200),
            D("BypassFrequency", b & "5.1.0", "upsBypassFrequency", "Hz", 0.1R, False, 0, 100),
            D("BypassLines", b & "5.2.0", "upsBypassNumLines", "", 1, False, 0, 3),
            D("BypassVoltage", b & "5.3.1.2.", "upsBypassVoltage", "V", 1, True, 0, 1000),
            D("AlarmsPresent", b & "6.1.0", "upsAlarmsPresent", "", 1, False, 0, 1000),
            D("RatedInputVoltage", b & "9.1.0", "upsConfigInputVoltage", "V", 1, False, 0, 1000),
            D("RatedOutputVa", b & "9.5.0", "upsConfigOutputVA", "VA", 1, False, 0, 10000000),
            D("RatedOutputW", b & "9.6.0", "upsConfigOutputPower", "W", 1, False, 0, 10000000)
        }
    End Function

    Public Function Find(key As String) As UpsOidDefinition
        Return If(Definitions.FirstOrDefault(Function(item) String.Equals(item.Key, key, StringComparison.Ordinal)),
                  UpsMeasurementMap.Candidates.First(Function(item) item.Definition.Key = key).Definition)
    End Function
End Module

' Immutable result of one poll. Measurement values are Nothing when not read or invalid – never zero-filled.
Public NotInheritable Class UpsSnapshot
    Public Property DeviceId As String = String.Empty
    Public Property TimestampUtc As DateTime
    Public Property Quality As UpsQuality
    Public Property QualityDetail As String = String.Empty
    Public Property RoundTripMilliseconds As Double?
    Public Property ConsecutiveFailures As Integer
    Public Property LastGoodUtc As DateTime?
    Public Property SysName As String = String.Empty
    Public Property SysDescr As String = String.Empty
    Public Property SysObjectId As String = String.Empty
    Public Property SysUpTime As String = String.Empty
    Public Property Manufacturer As String = String.Empty
    Public Property IdentModel As String = String.Empty
    Public Property UpsSoftware As String = String.Empty
    Public Property AgentSoftware As String = String.Empty
    ' Scalar values keyed by UpsOidDefinition.Key, already scaled to engineering units.
    Public Property Values As IReadOnlyDictionary(Of String, Double) = New Dictionary(Of String, Double)()
    ' Per-phase values keyed by UpsOidDefinition.Key; index 0 = L1. Missing phases are Nothing.
    Public Property Phases As IReadOnlyDictionary(Of String, Double?()) = New Dictionary(Of String, Double?())()
    Public Property MeasurementRevision As Integer = UpsMeasurementMap.Revision
    Public Property MeasurementMap As String = "RFC1628 (voltage reference unspecified)"
    Public Property MeasurementSources As IReadOnlyDictionary(Of String, String) = New Dictionary(Of String, String)()
    Public Property RawMeasurements As IReadOnlyDictionary(Of String, String) = New Dictionary(Of String, String)()
    Public Property OutputVoltageReference As String = "reference unverified"
    Public Property InputVoltageReference As String = "reference unverified"
    Public Property BypassVoltageReference As String = "reference unverified"
    Public Property InvalidFields As IReadOnlyList(Of String) = Array.Empty(Of String)()
    ' Most recent failed poll for this unit (kept after recovery so operators can see what happened).
    Public Property LastFailureReason As String = String.Empty
    Public Property LastFailureUtc As DateTime?
    ' Protocol and capability evidence for this unit (from the per-device capability probe).
    Public Property ProtocolVersion As String = String.Empty
    Public Property ConnectionStamp As String = String.Empty
    Public Property CapabilityVerifiedUtc As DateTime?
    Public Property SupportedParameters As Integer
    Public Property UnsupportedOids As IReadOnlyList(Of String) = Array.Empty(Of String)()
    ' Request groups that failed in this poll while others succeeded (partial reading kept, never zero-filled).
    Public Property PartialGroups As IReadOnlyList(Of String) = Array.Empty(Of String)()
    Public Property CardAlarms As IReadOnlyList(Of UpsCardAlarm) = Array.Empty(Of UpsCardAlarm)()
    Public Property AlarmDetailsComplete As Boolean
    Public Property AlarmDetailsStatus As String = "Alarm details not read."
    ' Last good snapshot kept for the detail view when the current poll failed (shown as historical only).
    Public Property LastGood As UpsSnapshot

    Public ReadOnly Property IsGood As Boolean
        Get
            Return Quality = UpsQuality.Good
        End Get
    End Property

    Public Function Value(key As String) As Double?
        Dim result As Double
        If Values IsNot Nothing AndAlso Values.TryGetValue(key, result) Then Return result
        Return Nothing
    End Function

    Public Function MeasurementSource(key As String) As String
        Dim source As String = Nothing
        If MeasurementSources.TryGetValue(key, source) Then Return source
        Dim definition = UpsOidMap.Definitions.FirstOrDefault(Function(item) item.Key = key)
        If definition Is Nothing Then Return "Unavailable (no verified source)"
        Return definition.Oid & If(definition.PerPhase, "n", "") & " ×" & definition.Scale.ToString("0.###", CultureInfo.InvariantCulture)
    End Function

    Public Function Phase(key As String, index As Integer) As Double?
        Dim items As Double?() = Nothing
        If Phases IsNot Nothing AndAlso Phases.TryGetValue(key, items) AndAlso index >= 0 AndAlso index < items.Length Then Return items(index)
        Return Nothing
    End Function

    Public Function MissingChannelReason(key As String, Optional phaseIndex As Integer = -1,
                                         Optional voltageReference As String = "") As String
        If Not IsGood Then Return Quality.ToString() & ": " & QualityDetail & " Missing measurements are unknown, not zero."
        Dim actualKey = key
        Dim reference = If(key.StartsWith("InputVoltage", StringComparison.Ordinal), InputVoltageReference,
            If(key.StartsWith("BypassVoltage", StringComparison.Ordinal), BypassVoltageReference, OutputVoltageReference))
        If voltageReference.Length > 0 Then
            actualKey = If(key = "InputVoltage" OrElse key = "BypassVoltage", key & voltageReference.Replace("-", ""),
                If(key = "OutputVoltage" AndAlso voltageReference = "L-L", "OutputVoltageLL", key))
        End If
        Dim value = If(phaseIndex >= 0, Phase(actualKey, phaseIndex), Me.Value(actualKey))
        If Not value.HasValue AndAlso actualKey <> key AndAlso voltageReference.Length > 0 Then
            If reference.StartsWith(voltageReference, StringComparison.Ordinal) Then value = Phase(key, phaseIndex)
        End If
        If value.HasValue AndAlso Double.IsFinite(value.Value) Then Return String.Empty
        Dim label = actualKey & If(phaseIndex < 0, "", " L" & (phaseIndex + 1).ToString(CultureInfo.InvariantCulture))
        If voltageReference.Length > 0 Then
            If Not reference.StartsWith(voltageReference, StringComparison.Ordinal) Then
                Return label & ": selected " & voltageReference & "; recorded card reference is " & reference &
                    ". Select the available reference; L-N and L-L must not be substituted or calculated from each other."
            End If
        End If
        If key = "OutputPower" AndAlso phaseIndex >= 0 AndAlso Me.Value("OutputPower").HasValue Then
            Return label & ": only system total active output power is available. Select Output total power; do not divide the total into invented phase powers."
        End If
        Dim invalid = InvalidFields.FirstOrDefault(Function(item) item = label OrElse item.StartsWith(label & " ", StringComparison.Ordinal))
        If invalid IsNot Nothing Then Return label & ": " & invalid & "; missing/invalid is not measured zero."
        Dim unsupported = UnsupportedOids.Where(Function(item) item.StartsWith(actualKey & " ", StringComparison.Ordinal)).ToArray()
        If unsupported.Length > 0 AndAlso Not MeasurementSources.ContainsKey(actualKey) Then
            Return label & ": card capability probe did not provide this measurement (" &
                String.Join("; ", unsupported) & "). Not a measured zero or proof of input supply loss."
        End If
        If PartialGroups.Count > 0 Then Return label & ": incomplete acquisition (" & String.Join("; ", PartialGroups) & "); not measured zero."
        Return label & ": no valid value in this acquisition. Check verified support, mapping and channel selection; missing is not zero."
    End Function

    Public Function ChannelAvailability() As IReadOnlyDictionary(Of String, String)
        Dim result As New Dictionary(Of String, String)(StringComparer.Ordinal)
        For Each group In UpsOidMap.Definitions.Concat(UpsMeasurementMap.Candidates.Select(Function(item) item.Definition)).
            Where(Function(item) Not UpsOidMap.IdentityKeys.Contains(item.Key)).GroupBy(Function(item) item.Key)
            For index = If(group.Any(Function(item) item.PerPhase), 0, -1) To If(group.Any(Function(item) item.PerPhase), 2, -1)
                Dim explicitReference = group.Key.EndsWith("VoltageLN", StringComparison.Ordinal) OrElse group.Key.EndsWith("VoltageLL", StringComparison.Ordinal)
                Dim reason = If(explicitReference, MissingChannelReason(group.Key.Substring(0, group.Key.Length - 2), index,
                    If(group.Key.EndsWith("LN", StringComparison.Ordinal), "L-N", "L-L")), MissingChannelReason(group.Key, index))
                If reason.Length > 0 Then result(group.Key & If(index < 0, "", ":L" & (index + 1).ToString(CultureInfo.InvariantCulture))) = reason
            Next
        Next
        Return result
    End Function

    ' Sum of all phases, only when every reported phase is present (partial sums are not shown as totals).
    Public Function PhaseSum(key As String) As Double?
        Dim items As Double?() = Nothing
        If Phases Is Nothing OrElse Not Phases.TryGetValue(key, items) OrElse items.Length = 0 Then Return Nothing
        If items.Any(Function(item) Not item.HasValue) Then Return Nothing
        Return items.Sum(Function(item) item.Value)
    End Function

    Public Function PhaseMax(key As String) As Double?
        Dim items As Double?() = Nothing
        If Phases Is Nothing OrElse Not Phases.TryGetValue(key, items) Then Return Nothing
        Dim present = items.Where(Function(item) item.HasValue).Select(Function(item) item.Value).ToList()
        If present.Count = 0 Then Return Nothing
        Return present.Max()
    End Function

    Public Function PhaseAverage(key As String) As Double?
        Dim items As Double?() = Nothing
        If Phases Is Nothing OrElse Not Phases.TryGetValue(key, items) Then Return Nothing
        Dim present = items.Where(Function(item) item.HasValue).Select(Function(item) item.Value).ToList()
        If present.Count = 0 Then Return Nothing
        Return present.Average()
    End Function

    Public ReadOnly Property OutputKw As Double?
        Get
            Dim total = Value("OutputPower")
            If Not total.HasValue Then total = PhaseSum("OutputPower")
            Return If(total.HasValue, total.Value / 1000.0R, CType(Nothing, Double?))
        End Get
    End Property

    ' Prefer the card's system VA; otherwise derive only from explicitly verified L-N phases.
    Public ReadOnly Property OutputKvaDerived As Double?
        Get
            Dim reported = Value("OutputApparentVa")
            If Not reported.HasValue Then reported = PhaseSum("OutputApparentVa")
            If reported.HasValue Then Return reported.Value / 1000.0R
            If MeasurementSources.ContainsKey("OutputApparentVa") Then Return Nothing
            If OutputVoltageReference <> "L-N" Then Return Nothing
            Dim voltages As Double?() = Nothing
            Dim currents As Double?() = Nothing
            If Phases Is Nothing OrElse Not Phases.TryGetValue("OutputVoltage", voltages) OrElse
               Not Phases.TryGetValue("OutputCurrent", currents) OrElse voltages.Length = 0 OrElse voltages.Length <> currents.Length Then Return Nothing
            If voltages.Concat(currents).Any(Function(item) Not item.HasValue) Then Return Nothing
            Return Enumerable.Range(0, voltages.Length).Sum(Function(index) voltages(index).Value * currents(index).Value) / 1000.0R
        End Get
    End Property

    ' System power factor = kW / kVA. An impossible result is missing, never clamped.
    Public ReadOnly Property PowerFactorDerived As Double?
        Get
            Dim kw = OutputKw
            Dim kva = OutputKvaDerived
            If Not kw.HasValue OrElse Not kva.HasValue OrElse kva.Value <= 0.5R Then Return Nothing
            Dim factor = kw.Value / kva.Value
            Return If(Double.IsFinite(factor) AndAlso factor >= 0 AndAlso factor <= 1, factor, CType(Nothing, Double?))
        End Get
    End Property

    Public ReadOnly Property PowerConsistencyIssue As String
        Get
            Dim kw = OutputKw
            Dim kva = OutputKvaDerived
            If kw.HasValue AndAlso kva.HasValue AndAlso kva.Value > 0.5R AndAlso kw.Value > kva.Value Then
                Return "Card-reported kW exceeds kVA; system PF withheld. Verify card measurement consistency."
            End If
            Return String.Empty
        End Get
    End Property

    Public ReadOnly Property AccountingOutputKw As Double?
        Get
            Return If(PowerConsistencyIssue.Length = 0, OutputKw, CType(Nothing, Double?))
        End Get
    End Property

    Public ReadOnly Property AccountingOutputKva As Double?
        Get
            Return If(PowerConsistencyIssue.Length = 0, OutputKvaDerived, CType(Nothing, Double?))
        End Get
    End Property

    Public ReadOnly Property InputKw As Double?
        Get
            Dim total = PhaseSum("InputPower")
            Return If(total.HasValue, total.Value / 1000.0R, CType(Nothing, Double?))
        End Get
    End Property

    Public ReadOnly Property OutputSourceText As String
        Get
            Dim source = Value("OutputSource")
            If Not source.HasValue Then Return "—"
            Select Case CInt(source.Value)
                Case 1 : Return "Other"
                Case 2 : Return "No output"
                Case 3 : Return "Normal (inverter)"
                Case 4 : Return "Bypass"
                Case 5 : Return "On battery"
                Case 6 : Return "Booster"
                Case 7 : Return "Reducer"
                Case Else : Return "Unknown (" & source.Value.ToString(CultureInfo.InvariantCulture) & ")"
            End Select
        End Get
    End Property

    Public ReadOnly Property BatteryStatusText As String
        Get
            Dim status = Value("BatteryStatus")
            If Not status.HasValue Then Return "—"
            Select Case CInt(status.Value)
                Case 2 : Return "Normal"
                Case 3 : Return "Low"
                Case 4 : Return "Depleted"
                Case Else : Return "Unknown"
            End Select
        End Get
    End Property
End Class

Public Module UpsQualityText
    Public Function Describe(quality As UpsQuality) As String
        Select Case quality
            Case UpsQuality.Good : Return "GOOD"
            Case UpsQuality.Stale : Return "STALE"
            Case UpsQuality.Timeout : Return "TIMEOUT"
            Case UpsQuality.AuthFailure : Return "AUTH FAILURE"
            Case UpsQuality.NotSupported : Return "NOT SUPPORTED"
            Case UpsQuality.Invalid : Return "INVALID DATA"
            Case UpsQuality.Offline : Return "DISCONNECTED"
            Case UpsQuality.ConfigError : Return "CONFIG ERROR"
            Case UpsQuality.Disabled : Return "DISABLED"
            Case Else : Return "NO DATA"
        End Select
    End Function

    Public Function DescribeVerification(verification As UpsVerification) As String
        Select Case verification
            Case UpsVerification.Verified : Return "Verified"
            Case UpsVerification.Provisional : Return "Provisional"
            Case UpsVerification.Conflict : Return "Identity conflict"
            Case Else : Return "Unverified"
        End Select
    End Function
End Module

' Annunciation for one UPS using the administrator's UPS alarm limits (Configuration › Alarm Limits › UPS).
Public NotInheritable Class UpsAlarmItem
    Public Property Severity As AlarmSeverity
    Public Property Text As String = String.Empty
    Public Property CardAlarm As UpsCardAlarm
    Public Property IsOnBattery As Boolean
End Class

Public Module UpsAlarmEvaluator
    Public Function Evaluate(snapshot As UpsSnapshot, limits As EquipmentAlarmLimits) As IReadOnlyList(Of UpsAlarmItem)
        Dim items As New List(Of UpsAlarmItem)()
        If snapshot Is Nothing OrElse snapshot.Quality = UpsQuality.Disabled Then Return items
        If Not snapshot.IsGood Then
            items.Add(New UpsAlarmItem With {.Severity = AlarmSeverity.Alarm,
                      .Text = "Communication: " & UpsQualityText.Describe(snapshot.Quality)})
            Return items
        End If
        Dim inv = CultureInfo.InvariantCulture
        If snapshot.PowerConsistencyIssue.Length > 0 Then
            items.Add(New UpsAlarmItem With {.Severity = AlarmSeverity.PreAlarm, .Text = snapshot.PowerConsistencyIssue})
        End If
        Select Case CInt(If(snapshot.Value("OutputSource"), 3.0R))
            Case 5 : items.Add(New UpsAlarmItem With {.Severity = AlarmSeverity.Alarm, .IsOnBattery = True, .Text = "UPS ON BATTERY"})
            Case 4 : items.Add(New UpsAlarmItem With {.Severity = AlarmSeverity.PreAlarm, .Text = "Load on bypass"})
            Case 2 : items.Add(New UpsAlarmItem With {.Severity = AlarmSeverity.Alarm, .Text = "UPS output OFF"})
        End Select
        Dim battery = snapshot.Value("BatteryStatus")
        If battery.HasValue AndAlso battery.Value >= 3 Then
            items.Add(New UpsAlarmItem With {.Severity = AlarmSeverity.Alarm, .Text = "Battery " & snapshot.BatteryStatusText.ToLowerInvariant()})
        End If
        Dim alarms = snapshot.Value("AlarmsPresent")
        For Each alarm In snapshot.CardAlarms
            Dim cleared = alarm.Active.HasValue AndAlso Not alarm.Active.Value
            Dim major = alarm.CardSeverity = "Major" OrElse alarm.CardSeverity = "Critical"
            Dim messageOnly = alarm.CardType = "Message" AndAlso Not major
            Dim warning = Not major AndAlso (alarm.CardType = "Warning" OrElse alarm.CardSeverity = "Minor")
            items.Add(New UpsAlarmItem With {.CardAlarm = alarm,
                .Severity = If(alarm.EvidenceConflict, AlarmSeverity.PreAlarm,
                    If(cleared OrElse messageOnly, AlarmSeverity.Normal, If(warning, AlarmSeverity.PreAlarm, AlarmSeverity.Alarm))),
                .Text = alarm.Name & " [" & alarm.StateText & "]"})
        Next
        If Not snapshot.AlarmDetailsComplete AndAlso alarms.HasValue AndAlso alarms.Value > 0 Then
            Dim onlyConflictingEvidence = snapshot.CardAlarms.Count > 0 AndAlso snapshot.CardAlarms.All(Function(alarm) alarm.EvidenceConflict)
            items.Add(New UpsAlarmItem With {.Severity = If(onlyConflictingEvidence, AlarmSeverity.PreAlarm, AlarmSeverity.Alarm),
                      .Text = alarms.Value.ToString("0", inv) & " UPS alarm(s) reported; details incomplete: " & snapshot.AlarmDetailsStatus})
        ElseIf Not snapshot.AlarmDetailsComplete Then
            items.Add(New UpsAlarmItem With {.Severity = AlarmSeverity.PreAlarm,
                      .Text = "Card alarm details not verified: " & snapshot.AlarmDetailsStatus})
        End If
        If limits IsNot Nothing Then
            Dim load = snapshot.PhaseMax("OutputLoad")
            If load.HasValue Then
                Dim severity = If(load.Value >= limits.LoadShedPercent, AlarmSeverity.ShedLoad,
                               If(load.Value >= limits.LoadAlarmPercent, AlarmSeverity.Alarm,
                               If(load.Value >= limits.LoadPreAlarmPercent, AlarmSeverity.PreAlarm, AlarmSeverity.Normal)))
                If severity <> AlarmSeverity.Normal Then
                    items.Add(New UpsAlarmItem With {.Severity = severity, .Text = "Load " & load.Value.ToString("0", inv) & " %"})
                End If
            End If
            If snapshot.OutputVoltageReference <> "L-N" Then
                items.Add(New UpsAlarmItem With {.Severity = AlarmSeverity.PreAlarm,
                          .Text = "Output voltage reference unverified; L-N voltage limits not evaluated"})
            End If
            For index = 0 To 2
                Dim voltage = snapshot.Phase("OutputVoltage", index)
                If snapshot.OutputVoltageReference = "L-N" AndAlso voltage.HasValue AndAlso (voltage.Value < limits.UnderVoltageV OrElse voltage.Value > limits.OverVoltageV) Then
                    items.Add(New UpsAlarmItem With {.Severity = AlarmSeverity.Alarm,
                              .Text = "Output L" & (index + 1) & " " & voltage.Value.ToString("0", inv) & " V out of range"})
                End If
            Next
            Dim temperature = snapshot.Value("BatteryTemperature")
            If temperature.HasValue Then
                Dim severity = If(temperature.Value >= limits.TemperatureShedC, AlarmSeverity.ShedLoad,
                               If(temperature.Value >= limits.TemperatureAlarmC, AlarmSeverity.Alarm,
                               If(temperature.Value >= limits.TemperaturePreAlarmC, AlarmSeverity.PreAlarm, AlarmSeverity.Normal)))
                If severity <> AlarmSeverity.Normal Then
                    items.Add(New UpsAlarmItem With {.Severity = severity, .Text = "Battery temperature " & temperature.Value.ToString("0", inv) & " °C"})
                End If
            End If
        End If
        Return items
    End Function

    Public Function Worst(items As IReadOnlyList(Of UpsAlarmItem)) As AlarmSeverity
        If items Is Nothing OrElse items.Count = 0 Then Return AlarmSeverity.Normal
        Return items.Max(Function(item) item.Severity)
    End Function
End Module

Public NotInheritable Class UpsSettingsStore
    Private Const DocumentKey As String = "ups-settings"
    Private Shared ReadOnly Gate As New Object()
    Private Shared ReadOnly SerializerOptions As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True,
        .WriteIndented = True
    }
    Private Shared _current As UpsSettings

    Private Sub New()
    End Sub

    Public Shared Event Changed As EventHandler

    Public Shared ReadOnly Property LoadError As String = String.Empty

    Public Shared ReadOnly Property Current As UpsSettings
        Get
            SyncLock Gate
                If _current Is Nothing Then _current = LoadOrDefault()
                Return _current
            End SyncLock
        End Get
    End Property

    Public Shared Function SettingsPath(Optional baseDirectory As String = Nothing) As String
        Return Path.Combine(If(baseDirectory, AppContext.BaseDirectory), "EOEMSData", "Configuration", "ups-settings.json")
    End Function

    Private Shared Function LoadOrDefault() As UpsSettings
        Try
            Dim json = PostgresDatabase.LoadDocument(DocumentKey, SettingsPath())
            If json Is Nothing Then Return UpsSettings.CreateDefault()
            Dim settings = JsonSerializer.Deserialize(Of UpsSettings)(json, SerializerOptions)
            If settings Is Nothing Then Throw New InvalidDataException("UPS settings document is empty.")
            settings.Validate()
            _LoadError = String.Empty
            Return settings
        Catch ex As Exception When TypeOf ex Is JsonException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is IOException
            _LoadError = "Saved UPS registry could not be read (" & ex.Message & "); the factory registry is active."
            Return UpsSettings.CreateDefault()
        End Try
    End Function

    Public Shared Sub Save(settings As UpsSettings, username As String, roleName As String, isOfflinePreview As Boolean,
                           Optional confirmWorkingDevices As Boolean = False)
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        If Not PlcConfigurationAccess.CanEdit(roleName, isOfflinePreview) Then
            Throw New UnauthorizedAccessException("Only an authenticated administrator can edit the UPS registry.")
        End If
        settings.Validate()
        If Not confirmWorkingDevices AndAlso ProtectedAssignmentChanges(settings).Count > 0 Then
            Throw New InvalidDataException("Explicit administrator confirmation is required to change working UPS connections.")
        End If
        Dim stored = settings.Clone()
        Dim path = SettingsPath()
        Dim folder = System.IO.Path.GetDirectoryName(path)
        Directory.CreateDirectory(folder)
        Dim serialized = JsonSerializer.Serialize(stored, SerializerOptions)
        Dim temporary = path & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Dim auditRecord = JsonSerializer.Serialize(New With {
            .TimestampUtc = DateTime.UtcNow,
            .Username = If(username, String.Empty),
            .Action = "UpsRegistryChanged",
            .Before = Current,
            .After = stored
        })
        SyncLock Gate
            Try
                File.WriteAllText(temporary, serialized, New System.Text.UTF8Encoding(False))
                If File.Exists(path) Then File.Copy(path, path & ".bak", True)
                File.Move(temporary, path, True)
                File.AppendAllText(System.IO.Path.Combine(folder, "ups-settings-audit.jsonl"),
                                   auditRecord & Environment.NewLine, New System.Text.UTF8Encoding(False))
            Finally
                If File.Exists(temporary) Then File.Delete(temporary)
            End Try
            PostgresDatabase.TryWriteDocument(DocumentKey, serialized, username)
            _current = stored
            _LoadError = String.Empty
        End SyncLock
        RaiseEvent Changed(Nothing, EventArgs.Empty)
    End Sub

    Public Shared Function ProtectedAssignmentChanges(settings As UpsSettings) As IReadOnlyList(Of String)
        Return Current.Devices.Where(Function(device)
                                         If Not SnmpCredentialStore.IsProtectedDevice(device) Then Return False
                                         Dim replacement = settings.Devices.FirstOrDefault(Function(item) item.Id = device.Id)
                                         Return replacement Is Nothing OrElse replacement.IpAddress <> device.IpAddress OrElse
                                             replacement.Port <> device.Port OrElse replacement.CredentialProfile <> device.CredentialProfile OrElse
                                             replacement.UseProfileTransport <> device.UseProfileTransport OrElse replacement.Enabled <> device.Enabled
                                     End Function).Select(Function(device) device.DisplayName).ToList()
    End Function
End Class
