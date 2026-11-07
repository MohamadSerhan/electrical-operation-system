Imports System.Linq

Public NotInheritable Class RecordingParameterInfo
    Public Property Id As String = String.Empty
    Public Property Label As String = String.Empty
    Public Property Unit As String = String.Empty
    Public Property Verification As String = String.Empty
    ' Position in the stored sample array; -1 for values calculated over the report window.
    Public Property Index As Integer

    Public ReadOnly Property IsWindowStatistic As Boolean
        Get
            Return Index < 0
        End Get
    End Property
End Class

' Single source for recorded channels (the PM5110 map plus calculated channels) and the
' report-window peak/average/energy values offered for selection.
Public NotInheritable Class MeasurementCatalog
    Public Const PlcChannelCount As Integer = 16
    Public Const StoredChannelCount As Integer = 18
    Public Const ActivePowerIndex As Integer = 10
    Public Const ApparentPowerIndex As Integer = 16
    Public Const PowerFactorIndex As Integer = 17
    Public Const StatisticCategory As String = "Peak, Average & Energy"

    Public Shared ReadOnly StoredParameterIds As String() = {
        "V_L1_N", "V_L2_N", "V_L3_N", "AVG_V_L_N", "V_L1_L2", "V_L2_L3", "V_L3_L1", "AVG_V_L_L",
        "FREQUENCY", "AVG_CURRENT", "PTOT_KW", "CURRENT_L1", "CURRENT_L2", "CURRENT_L3", "CURRENT_NEUTRAL", "TDEGC_C",
        "KVA_CALC", "PF_CALC"}

    Public Shared ReadOnly WindowStatisticIds As String() = {
        "STAT_PEAK_V_L_N", "STAT_PEAK_V_L_L", "STAT_PEAK_CURRENT", "STAT_PEAK_KW", "STAT_AVG_KW", "STAT_ENERGY_KWH"}

    Private Const CalculatedVerification As String = "Calculated: kW / sum of phase V x I, clamped 0-1; V/I scaling unverified"
    Private Const WindowVerification As String = "Calculated from saved samples in the report window"

    Private Shared ReadOnly TransformerCatalog As RecordingParameterInfo() = {
        Item("V_L1_N", "Voltage L1-N", "V", "Mapped PLC value", 0),
        Item("V_L2_N", "Voltage L2-N", "V", "Mapped PLC value", 1),
        Item("V_L3_N", "Voltage L3-N", "V", "Mapped PLC value", 2),
        Item("AVG_V_L_N", "Average Voltage L-N", "V", "Mapped PLC value", 3),
        Item("V_L1_L2", "Voltage L1-L2", "V", "Mapped PLC value", 4),
        Item("V_L2_L3", "Voltage L2-L3", "V", "Mapped PLC value", 5),
        Item("V_L3_L1", "Voltage L3-L1", "V", "Mapped PLC value", 6),
        Item("AVG_V_L_L", "Average Voltage L-L", "V", "Mapped PLC value", 7),
        Item("FREQUENCY", "Frequency", "Hz", "Mapped PLC value", 8),
        Item("AVG_CURRENT", "Average Current", "A", "Mapped PLC value", 9),
        Item("PTOT_KW", "Active Power", "kW", "Unit confirmed; raw decoded value", 10),
        Item("CURRENT_L1", "Current L1", "A", "Mapped PLC value", 11),
        Item("CURRENT_L2", "Current L2", "A", "Mapped PLC value", 12),
        Item("CURRENT_L3", "Current L3", "A", "Mapped PLC value", 13),
        Item("CURRENT_NEUTRAL", "Neutral Current", "A", "Mapped PLC value", 14),
        Item("TDEGC_C", "TDEGC", "deg C", "Channel/source unverified", 15),
        Item("KVA_CALC", "Apparent Power (calc.)", "kVA", "Calculated: sum of phase V x I; V/I scaling unverified", 16),
        Item("PF_CALC", "Power Factor (calc.)", "PF", CalculatedVerification, 17),
        Item("STAT_PEAK_V_L_N", "Peak Voltage L-N", "V", WindowVerification & " (max of L1/L2/L3-N)", -1),
        Item("STAT_PEAK_V_L_L", "Peak Voltage L-L", "V", WindowVerification & " (max of L1-L2/L2-L3/L3-L1)", -1),
        Item("STAT_PEAK_CURRENT", "Peak Current", "A", WindowVerification & " (max of L1/L2/L3)", -1),
        Item("STAT_PEAK_KW", "Peak Active Power", "kW", WindowVerification & " (max kW)", -1),
        Item("STAT_AVG_KW", "Average Active Power", "kW", WindowVerification & " (time-weighted)", -1),
        Item("STAT_ENERGY_KWH", "Active Energy", "kWh", WindowVerification & " (integrated kW; not a meter register)", -1)
    }

    Private Const TotalVerification As String = "Calculated from four good same-cycle T1-T4 readings"

    Private Shared ReadOnly TotalCatalog As RecordingParameterInfo() = {
        Item("V_L1_N", "Average Voltage L1-N (T1-T4)", "V", TotalVerification & " (average)", 0),
        Item("V_L2_N", "Average Voltage L2-N (T1-T4)", "V", TotalVerification & " (average)", 1),
        Item("V_L3_N", "Average Voltage L3-N (T1-T4)", "V", TotalVerification & " (average)", 2),
        Item("AVG_V_L_N", "Average Voltage L-N (T1-T4)", "V", TotalVerification & " (average)", 3),
        Item("AVG_V_L_L", "Average Voltage L-L (T1-T4)", "V", TotalVerification & " (average)", 7),
        Item("AVG_CURRENT", "Total Current", "A", TotalVerification & " (sum of average currents)", 9),
        Item("PTOT_KW", "Total Active Power", "kW", TotalVerification & " (sum of kW)", 10),
        Item("CURRENT_L1", "Total Current L1", "A", TotalVerification & " (sum)", 11),
        Item("CURRENT_L2", "Total Current L2", "A", TotalVerification & " (sum)", 12),
        Item("CURRENT_L3", "Total Current L3", "A", TotalVerification & " (sum)", 13),
        Item("KVA_CALC", "Total Apparent Power (calc.)", "kVA", TotalVerification & " (sum of phase V x I)", 16),
        Item("PF_CALC", "Total Power Factor (calc.)", "PF", "Calculated: total kW / total kVA, clamped 0-1; V/I scaling unverified", 17),
        Item("STAT_PEAK_CURRENT", "Peak Total Current", "A", WindowVerification & " (max of total L1/L2/L3)", -1),
        Item("STAT_PEAK_KW", "Peak Total Active Power", "kW", WindowVerification & " (max total kW)", -1),
        Item("STAT_AVG_KW", "Average Total Active Power", "kW", WindowVerification & " (time-weighted)", -1),
        Item("STAT_ENERGY_KWH", "Total Active Energy", "kWh", WindowVerification & " (integrated total kW; not a meter register)", -1)
    }

    ' Measurements introduced with recording policy version 3; existing saved selections receive them once.
    ' Transformers only gain the new calculated/statistic items; TOTAL gains its newly supported combined channels.
    Public Shared Function AddedInPolicyVersion3(analyzerId As String) As String()
        Return GetCatalog(analyzerId).
            Where(Function(item) IsTotal(analyzerId) OrElse item.Index < 0 OrElse item.Index >= PlcChannelCount).
            Select(Function(item) item.Id).ToArray()
    End Function

    Private Sub New()
    End Sub

    Private Shared Function Item(id As String, label As String, unit As String, verification As String, index As Integer) As RecordingParameterInfo
        Return New RecordingParameterInfo With {.Id = id, .Label = label, .Unit = unit, .Verification = verification, .Index = index}
    End Function

    Public Shared Function IsTotal(analyzerId As String) As Boolean
        Return String.Equals(analyzerId, "TOTAL", StringComparison.OrdinalIgnoreCase)
    End Function

    Public Shared Function GetCatalog(analyzerId As String) As IReadOnlyList(Of RecordingParameterInfo)
        If IsTotal(analyzerId) Then Return Array.AsReadOnly(TotalCatalog)
        If Not {"T1", "T2", "T3", "T4"}.Contains(analyzerId, StringComparer.OrdinalIgnoreCase) Then
            Throw New ArgumentException("Analyzer must be T1, T2, T3, T4, or TOTAL.", NameOf(analyzerId))
        End If
        Return Array.AsReadOnly(TransformerCatalog)
    End Function

    ' Selection order used when normalising saved settings.
    Public Shared Function AllParameterIds() As String()
        Return StoredParameterIds.Concat(WindowStatisticIds).ToArray()
    End Function

    Public Shared Function Find(analyzerId As String, parameterId As String) As RecordingParameterInfo
        Return GetCatalog(analyzerId).FirstOrDefault(Function(parameter) String.Equals(parameter.Id, parameterId, StringComparison.Ordinal))
    End Function

    Public Shared Function GetStoredIndex(parameterId As String) As Integer
        Return Array.IndexOf(StoredParameterIds, parameterId)
    End Function

    Public Shared Function GetUnit(parameterIndex As Integer) As String
        Select Case parameterIndex
            Case 0 To 7
                Return "V"
            Case 8
                Return "Hz"
            Case ActivePowerIndex
                Return "kW"
            Case 15
                Return "°C"
            Case ApparentPowerIndex
                Return "kVA"
            Case PowerFactorIndex
                Return "PF"
            Case Else
                Return "A"
        End Select
    End Function

    ' Stored channels read to calculate a report-window statistic.
    Public Shared Function GetStatisticSourceIds(analyzerId As String, statisticId As String) As String()
        Select Case statisticId
            Case "STAT_PEAK_V_L_N"
                Return {"V_L1_N", "V_L2_N", "V_L3_N"}
            Case "STAT_PEAK_V_L_L"
                Return {"V_L1_L2", "V_L2_L3", "V_L3_L1"}
            Case "STAT_PEAK_CURRENT"
                Return {"CURRENT_L1", "CURRENT_L2", "CURRENT_L3"}
            Case "STAT_PEAK_KW", "STAT_AVG_KW", "STAT_ENERGY_KWH"
                Return {"PTOT_KW"}
            Case Else
                Return Array.Empty(Of String)()
        End Select
    End Function

    ' Stored channels that must be read for a selection (selected channels plus statistic sources).
    Public Shared Function GetSourceParameterIds(analyzerId As String, selected As IEnumerable(Of String)) As IReadOnlyList(Of String)
        If selected Is Nothing Then Return Array.Empty(Of String)()
        Dim result As New List(Of String)()
        For Each parameterId In selected
            Dim info = Find(analyzerId, parameterId)
            If info Is Nothing Then Continue For
            Dim sources = If(info.IsWindowStatistic, GetStatisticSourceIds(analyzerId, parameterId), {parameterId})
            For Each source In sources
                If Not result.Contains(source) Then result.Add(source)
            Next
        Next
        Return result.OrderBy(Function(id) GetStoredIndex(id)).ToList().AsReadOnly()
    End Function
End Class

' Shared engineering calculations so live display, historian and reports use the same formulas.
Public NotInheritable Class ElectricalCalculations
    Public Const MinimumApparentPowerKva As Double = 0.001R

    Private Sub New()
    End Sub

    ' Arithmetic apparent power: sum of phase-to-neutral voltage x phase current.
    Public Shared Function ApparentPowerKva(values As IReadOnlyList(Of Single)) As Double?
        If values Is Nothing OrElse values.Count < MeasurementCatalog.PlcChannelCount Then Return Nothing
        Dim sum = 0.0R
        For phase = 0 To 2
            Dim voltage = CDbl(values(phase))
            Dim current = CDbl(values(11 + phase))
            If Not Double.IsFinite(voltage) OrElse Not Double.IsFinite(current) Then Return Nothing
            sum += voltage * current
        Next
        Return sum / 1000.0R
    End Function

    Public Shared Function PowerFactor(kw As Double?, kva As Double?) As Double?
        If Not kw.HasValue OrElse Not kva.HasValue OrElse Not Double.IsFinite(kw.Value) OrElse
           Not Double.IsFinite(kva.Value) OrElse kva.Value <= MinimumApparentPowerKva Then Return Nothing
        Return Math.Clamp(kw.Value / kva.Value, 0.0R, 1.0R)
    End Function

    ' Stored channels for one good transformer reading; NaN marks a channel that cannot be calculated.
    Public Shared Function TransformerChannels(values As IReadOnlyList(Of Single)) As Double()
        Dim result = NewChannelArray()
        If values Is Nothing OrElse values.Count < MeasurementCatalog.PlcChannelCount Then Return result
        For index = 0 To MeasurementCatalog.PlcChannelCount - 1
            result(index) = CDbl(values(index))
        Next
        Dim kva = ApparentPowerKva(values)
        If kva.HasValue Then result(MeasurementCatalog.ApparentPowerIndex) = kva.Value
        Dim pf = PowerFactor(result(MeasurementCatalog.ActivePowerIndex), kva)
        If pf.HasValue Then result(MeasurementCatalog.PowerFactorIndex) = pf.Value
        Return result
    End Function

    ' TOTAL channels from four good transformer readings: summed currents/power, averaged voltages,
    ' total PF = total kW / total kVA. Channels without a combined meaning stay NaN.
    Public Shared Function TotalChannels(transformerValues As IReadOnlyList(Of IReadOnlyList(Of Single))) As Double()
        Dim result = NewChannelArray()
        If transformerValues Is Nothing OrElse transformerValues.Count <> 4 OrElse
           transformerValues.Any(Function(values) values Is Nothing OrElse values.Count < MeasurementCatalog.PlcChannelCount) Then Return result
        Dim channels = transformerValues.Select(Function(values) TransformerChannels(values)).ToArray()
        For Each index In {0, 1, 2, 3, 7}
            result(index) = Combine(channels, index, average:=True)
        Next
        For Each index In {9, MeasurementCatalog.ActivePowerIndex, 11, 12, 13, MeasurementCatalog.ApparentPowerIndex}
            result(index) = Combine(channels, index, average:=False)
        Next
        Dim pf = PowerFactor(result(MeasurementCatalog.ActivePowerIndex), result(MeasurementCatalog.ApparentPowerIndex))
        If pf.HasValue Then result(MeasurementCatalog.PowerFactorIndex) = pf.Value
        Return result
    End Function

    Private Shared Function NewChannelArray() As Double()
        Dim result(MeasurementCatalog.StoredChannelCount - 1) As Double
        For index = 0 To result.Length - 1
            result(index) = Double.NaN
        Next
        Return result
    End Function

    Private Shared Function Combine(channels As Double()(), index As Integer, average As Boolean) As Double
        Dim sum = 0.0R
        For Each values In channels
            If Not Double.IsFinite(values(index)) Then Return Double.NaN
            sum += values(index)
        Next
        Return If(average, sum / channels.Length, sum)
    End Function
End Class
