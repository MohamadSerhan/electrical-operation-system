Imports System.Linq

Friend NotInheritable Class FactoryMapMeterLink
    Private Sub New()
    End Sub

    Public Shared Function Supports(model As String) As Boolean
        Return model = "Energy meter" OrElse model.Contains("with energy meter", StringComparison.Ordinal)
    End Function

    Public Shared Function Find(asset As FactoryMapAsset, snapshots As IReadOnlyList(Of MeterSnapshot)) As MeterSnapshot
        If asset Is Nothing Then Return Nothing
        Dim id = asset.MeterSourceId
        If id = "" Then
            If Supports(asset.Model) Then Return Nothing
            If Not asset.EquipmentId.StartsWith("TRANSFORMER-", StringComparison.OrdinalIgnoreCase) Then Return Nothing
            id = asset.EquipmentId.Substring("TRANSFORMER-".Length)
        ElseIf Not Supports(asset.Model) Then
            Return Nothing
        End If
        Return snapshots.FirstOrDefault(Function(snapshot) FactoryMapMonitoring.IsCurrentMeter(snapshot) AndAlso
            String.Equals(snapshot.TransformerName, id, StringComparison.OrdinalIgnoreCase))
    End Function
End Class
