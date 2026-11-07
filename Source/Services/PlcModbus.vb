Imports System.IO

Public NotInheritable Class PlcModbus
    Public Const MissingValue As String = "—"
    Public Shared ReadOnly FieldNames As String() = {
        "V A-N", "V B-N", "V C-N", "Average V L-N",
        "V A-B", "V B-C", "V C-A", "Average V L-L",
        "Frequency (unit unverified)", "Average current (unit unverified)", "PTOT (kW per operator confirmation)",
        "Phase A current (unit unverified)", "Phase B current (unit unverified)",
        "Phase C current (unit unverified)", "Neutral current (unit unverified)",
        "TDEGC temperature (°C per operator confirmation)"
    }

    Private Sub New()
    End Sub

    Public Shared Function DecodeRegisters(registers As UShort(), swapWords As Boolean) As Single()
        If registers Is Nothing OrElse registers.Length <> 32 Then
            Throw New InvalidDataException("A PM5110 block must contain exactly 32 registers (16 float values).")
        End If

        Dim values(15) As Single
        For valueIndex = 0 To values.Length - 1
            Dim firstWord As UInteger = registers(valueIndex * 2)
            Dim secondWord As UInteger = registers(valueIndex * 2 + 1)
            Dim bits = If(swapWords, (secondWord << 16) Or firstWord, (firstWord << 16) Or secondWord)
            Dim number = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0)
            values(valueIndex) = number
        Next
        For valueIndex = 0 To values.Length - 1
            If Not Single.IsFinite(values(valueIndex)) AndAlso Not IsUnavailableOffFrequency(values, valueIndex) Then
                Throw New InvalidDataException("Non-finite IEEE-754 value at measurement index " & valueIndex.ToString(Globalization.CultureInfo.InvariantCulture) & ".")
            End If
        Next
        Return values
    End Function

    Public Shared Function IsUnavailableOffFrequency(values As Single(), index As Integer) As Boolean
        Return index = 8 AndAlso values IsNot Nothing AndAlso values.Length = 16 AndAlso
            Single.IsNaN(values(8)) AndAlso PowerEventTracker.VoltageState(values) = TransformerPowerState.Off
    End Function

    Public Shared Function FormatValue(snapshot As MeterSnapshot, index As Integer) As String
        If snapshot Is Nothing OrElse snapshot.Quality <> MeterQuality.Good OrElse
           snapshot.DecodedValues Is Nothing OrElse index < 0 OrElse index >= snapshot.DecodedValues.Length Then
            Return MissingValue
        End If
        If Not Single.IsFinite(snapshot.DecodedValues(index)) Then Return MissingValue
        Return snapshot.DecodedValues(index).ToString("R", Globalization.CultureInfo.InvariantCulture)
    End Function
End Class
