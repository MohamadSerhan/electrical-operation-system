Imports System.IO

Friend NotInheritable Class FactoryMapEquipmentSizes
    Public Const UnitsPerMeter As Single = 8

    ' Representative overall envelopes, not verified installation/nameplate dimensions.
    Private Shared ReadOnly Sizes As New Dictionary(Of String, (Length As Single, Width As Single, Height As Single)) From {
        {FactoryMapCustomEquipmentStore.SidelModelName, (8, 2.5F, 2.5F)},
        {"PET Combi - still water", (12, 4, 3.5F)},
        {"PET Combi - carbonated drinks", (12, 4, 3.5F)},
        {"Can filler and seamer - carbonated drinks", (7, 3, 3)},
        {"Preform cage and tipper", (3, 2.5F, 2.8F)},
        {"Preform roller sorter and bridge", (3, 1, 2.5F)},
        {"Can end magazine", (1.5F, 1, 2.5F)},
        {"Empty-can inverter and rinser", (2, 1.2F, 2.3F)},
        {"Caterpillar generator", (5, 2, 2.8F)},
        {"Caterpillar generator 810 kVA", (5, 2, 2.8F)},
        {"Caterpillar generator 1500 kVA", (6.5F, 2.4F, 3.2F)},
        {"Transformer", (2.5F, 2, 2.5F)},
        {"Vertiv EXL 500 kVA", (2.2F, 0.9F, 2)},
        {"Vertiv NXL 800 kVA", (3.6F, 1, 2)},
        {"Battery rack - 4 levels", (2, 0.8F, 1.8F)},
        {"HP compressor 40 bar", (3.2F, 1.6F, 1.9F)},
        {"Atlas Copco compressor 7 bar", (2.4F, 1.3F, 1.8F)},
        {"Refrigerated air dryer", (1.2F, 0.9F, 1.5F)},
        {"Compressed air filter bank", (1.4F, 0.6F, 1.6F)},
        {"Compressor control panel", (0.8F, 0.5F, 1.8F)},
        {"Compressor power panel", (0.9F, 0.7F, 2)},
        {"Chiller", (4.5F, 2, 2.2F)},
        {"Hansa chiller", (3.8F, 2.2F, 2.4F)},
        {"Air-cooled screw chiller", (5.5F, 2.2F, 2.4F)},
        {"Water-cooled screw chiller", (3.5F, 1.6F, 2)},
        {"Centrifugal water-cooled chiller", (4.2F, 2, 2.5F)},
        {"Chiller pump and buffer skid", (2.8F, 1.5F, 2.2F)},
        {"Chiller control panel", (0.8F, 0.5F, 1.8F)},
        {"Chiller power panel", (0.9F, 0.7F, 2)},
        {"Hansa chiller cooling tower", (3, 3, 3.5F)},
        {"HP compressor cooling tower 40 bar", (2.2F, 2.2F, 2.8F)},
        {"Indoor wall AC 1.5 HP", (0.85F, 0.23F, 0.3F)},
        {"Indoor wall AC 2.5 HP", (1.1F, 0.25F, 0.33F)},
        {"Indoor cassette AC 5 HP", (0.95F, 0.95F, 0.3F)},
        {"Indoor ducted AC 10 HP", (1.4F, 0.85F, 0.45F)},
        {"Indoor floor AC 20 HP", (1.2F, 0.6F, 1.9F)},
        {"Outdoor AC 1.5 HP", (0.8F, 0.35F, 0.6F)},
        {"Outdoor AC 2.5 HP", (0.95F, 0.4F, 0.8F)},
        {"Outdoor AC 5 HP", (1, 0.45F, 1.25F)},
        {"Outdoor AC 10 HP", (1.2F, 0.6F, 1.55F)},
        {"Outdoor AC 20 HP", (1.5F, 0.8F, 1.8F)},
        {"VRV outdoor system", (1.3F, 0.8F, 1.8F)},
        {"VRV indoor cassette", (0.9F, 0.9F, 0.3F)},
        {"Air handling unit", (3.5F, 1.5F, 1.8F)},
        {"Ultrafiltration skid", (2.5F, 1, 2.2F)},
        {"Ultrafiltration twin rack", (4, 1.2F, 2.2F)},
        {"Reverse osmosis skid", (3, 1.2F, 2)},
        {"Sand filter vessel", (1.2F, 1.2F, 2.4F)},
        {"Activated carbon filter", (1.2F, 1.2F, 2.4F)},
        {"Water softener skid", (2, 1, 2)},
        {"UV sterilizer skid", (1.5F, 0.6F, 1.1F)},
        {"Chemical dosing skid", (1.2F, 0.8F, 1.2F)},
        {"Water booster pump skid", (2, 1.2F, 1.3F)},
        {"Water treatment control panel", (0.8F, 0.5F, 1.8F)},
        {"Stainless water tank", (2.5F, 2.5F, 4)},
        {"Stainless process vat", (1.8F, 1.8F, 2.5F)},
        {"CIP cleaning skid", (3.5F, 1.6F, 2.4F)},
        {"CIP station - 1 tank", (2.4F, 1.8F, 3.2F)},
        {"CIP station - 2 tanks", (4.2F, 1.8F, 3.2F)},
        {"CIP station - 3 tanks", (6, 1.8F, 3.2F)},
        {"Krones-style mixing and carbonation skid", (3.2F, 2, 2.8F)},
        {"Water treatment process skid", (4.2F, 1.6F, 3.2F)},
        {"PET bottle blower", (4, 2, 2.5F)},
        {"Bottle rinser", (2, 2, 2.3F)},
        {"Rotary bottle filler", (3.5F, 3.5F, 2.8F)},
        {"Bottle capper", (1.5F, 1.5F, 2.4F)},
        {"Bottle labeler", (2.5F, 1.6F, 2)},
        {"Shrink wrapper", (3.5F, 1.5F, 2)},
        {"Bottle accumulation table", (2.5F, 1.4F, 1.1F)},
        {"Filler control panel", (0.8F, 0.5F, 1.8F)},
        {"Syrup mixing skid", (2.5F, 1.6F, 2.2F)},
        {"Beverage carbonator", (2, 1.2F, 2)},
        {"Plate pasteurizer", (3, 1.4F, 2)},
        {"Bottle inspection station", (1.2F, 0.8F, 1.8F)},
        {"Date coding station", (0.6F, 0.5F, 1.5F)},
        {"Case packer", (3, 2, 2.4F)},
        {"Palletizer", (4, 3, 3.2F)},
        {"Pallet stretch wrapper", (2.5F, 1.8F, 2.5F)},
        {"Preform hopper", (1.6F, 1.2F, 2.5F)},
        {"Preform elevator", (2.5F, 1.1F, 3)},
        {"Cap elevator", (1.8F, 0.8F, 2.8F)},
        {"Cap sorter and chute", (1.2F, 1, 2.5F)},
        {"Cap sterilizer", (1.5F, 0.8F, 1.8F)},
        {"Bottle unscrambler", (2.5F, 2.5F, 2.2F)},
        {"Rinser filler capper monoblock", (6, 3.2F, 2.8F)},
        {"Air conveyor blower", (0.9F, 0.7F, 0.9F)},
        {"HEPA filler canopy", (4.5F, 4, 3.6F)},
        {"Product buffer tank", (1.5F, 1.5F, 2.6F)},
        {"Sanitary product pump", (1, 0.5F, 0.65F)},
        {"Sanitary valve manifold", (1.8F, 0.7F, 1.6F)},
        {"CIP return skid", (1.6F, 0.9F, 1.2F)},
        {"Bottle air knife dryer", (1.8F, 0.9F, 1.8F)},
        {"Bottle reject station", (1.8F, 1.2F, 1.2F)},
        {"Inline checkweigher", (1.5F, 0.8F, 1.4F)},
        {"Inline metal detector", (1.5F, 0.8F, 1.5F)},
        {"Can seamer", (1.5F, 1.2F, 2.2F)},
        {"Empty container depalletizer", (4, 3, 3.5F)},
        {"Returnable bottle washer", (6, 2.5F, 2.8F)},
        {"Foam cleaning station", (0.7F, 0.5F, 1.2F)},
        {"Wall panel", (0.6F, 0.25F, 0.8F)},
        {"Wall panel with energy meter", (0.6F, 0.25F, 0.8F)},
        {"Floor panel", (0.8F, 0.6F, 2)},
        {"Floor panel with energy meter", (0.8F, 0.6F, 2)},
        {"Switchboard lineup", (3.2F, 0.8F, 2.2F)},
        {"Switchboard lineup with energy meters", (3.2F, 0.8F, 2.2F)},
        {"ATS cabinet", (1, 0.8F, 2)},
        {"Motor control center", (2.4F, 0.6F, 2.2F)},
        {"ACB panel with energy meter", (1, 0.8F, 2.2F)},
        {"ATS panel with energy meter", (1, 0.8F, 2.2F)},
        {"MTS panel with energy meter", (0.8F, 0.6F, 2)},
        {"Load panel with energy meter", (1, 0.6F, 2)},
        {"RMU switchgear", (1.6F, 0.9F, 1.8F)},
        {"Eaton RMU switchgear", (1.8F, 1, 1.8F)},
        {"SF6 circuit breaker", (0.8F, 1.2F, 2)},
        {"HV voltage regulator", (3.5F, 2, 3)},
        {"Eaton HV voltage regulator", (3.5F, 2, 3)},
        {"HV utility pole", (2, 1, 10)},
        {"HV lattice transmission tower", (8, 8, 25)},
        {"HV pole fuse cutouts", (1.2F, 0.4F, 0.8F)},
        {"HV insulator assembly", (0.25F, 0.25F, 1)},
        {"Electric motor", (0.8F, 0.5F, 0.6F)},
        {"Variable frequency drive", (0.35F, 0.3F, 0.6F)},
        {"Centrifugal pump", (1, 0.5F, 0.65F)},
        {"Twin pump skid", (1.8F, 1.1F, 1.2F)},
        {"Vertical storage tank", (2.5F, 2.5F, 4)},
        {"Horizontal air receiver", (3, 1.2F, 1.5F)},
        {"PLC cabinet", (0.8F, 0.5F, 1.8F)},
        {"HMI console", (0.6F, 0.5F, 1.5F)},
        {"Energy meter", (0.1F, 0.08F, 0.1F)},
        {"Pressure transmitter", (0.12F, 0.12F, 0.25F)},
        {"Temperature sensor", (0.08F, 0.08F, 0.3F)},
        {"Stack beacon", (0.08F, 0.08F, 0.4F)},
        {"Utility cabinet", (0.8F, 0.6F, 1.8F)},
        {"Cooling tower", (2.5F, 2.5F, 3)},
        {"Ventilation fan", (0.8F, 0.3F, 0.8F)},
        {"Pipe section", (3, 0.15F, 0.15F)},
        {"Isolation valve", (0.4F, 0.3F, 0.5F)},
        {"Cable tray", (3, 0.4F, 0.1F)},
        {"Steam boiler", (3, 1.8F, 2.5F)},
        {"Forklift", (3.5F, 1.2F, 2.2F)},
        {"Pallet rack", (2.7F, 1.1F, 4)},
        {"Pallet stack", (1.2F, 1, 1.2F)},
        {"Loading dock", (3, 2.5F, 1.2F)},
        {"Safety bollard", (0.25F, 0.25F, 1)},
        {"Office desk", (1.4F, 0.7F, 1.15F)},
        {"Office chair", (0.6F, 0.6F, 1.1F)},
        {"Office cabinet", (0.9F, 0.45F, 1.8F)},
        {"Camera", (0.35F, 0.15F, 0.25F)},
        {"Dome camera", (0.16F, 0.16F, 0.12F)},
        {"Access reader", (0.08F, 0.04F, 0.15F)},
        {"Single swing gate", (3, 0.12F, 2.2F)},
        {"Double swing gate", (4, 0.12F, 2.2F)},
        {"Sliding gate", (4, 0.12F, 2.2F)},
        {"Rapid roll-up gate", (3, 0.15F, 3)},
        {"Glass entry door", (1, 0.1F, 2.2F)},
        {"Wood entry door", (0.9F, 0.1F, 2.1F)},
        {"Fire panel", (0.5F, 0.2F, 0.6F)},
        {"Fire extinguisher", (0.18F, 0.18F, 0.6F)},
        {"Fire hydrant", (0.6F, 0.4F, 0.9F)},
        {"Smoke detector", (0.12F, 0.12F, 0.05F)},
        {"Zeta smoke detector", (0.12F, 0.12F, 0.05F)},
        {"Zeta heat detector", (0.12F, 0.12F, 0.06F)},
        {"Zeta multi-sensor detector", (0.12F, 0.12F, 0.06F)},
        {"Zeta fire panel", (0.5F, 0.2F, 0.6F)},
        {"Zeta repeater panel", (0.35F, 0.1F, 0.25F)},
        {"Zeta manual call point", (0.09F, 0.055F, 0.09F)},
        {"Zeta sounder", (0.11F, 0.1F, 0.11F)},
        {"Zeta sounder beacon", (0.11F, 0.1F, 0.15F)},
        {"Zeta siren", (0.25F, 0.3F, 0.25F)},
        {"Zeta beam detector", (0.15F, 0.12F, 0.18F)},
        {"Zeta interface module", (0.15F, 0.05F, 0.1F)},
        {"Zeta isolator module", (0.1F, 0.05F, 0.1F)},
        {"Fire suppression control panel", (0.4F, 0.15F, 0.5F)},
        {"CO2 cylinder bank", (1.2F, 0.5F, 1.8F)},
        {"FM200 cylinder bank", (1.4F, 0.6F, 1.7F)}
    }

    Private Sub New()
    End Sub

    Public Shared Function IsVariableLength(model As String) As Boolean
        Return {"Conveyor", "Air conveyor", "Conveyor corner", "Beverage line with filler",
                "PET bottling line", "Can filling line", "Glass bottling line"}.Contains(model)
    End Function

    Public Shared Function Nominal(model As String) As (Length As Single, Width As Single, Height As Single)
        Dim size As (Length As Single, Width As Single, Height As Single)
        If Not Sizes.TryGetValue(model, size) Then Throw New InvalidDataException("No nominal equipment size is defined for " & model & ".")
        Return size
    End Function

    Public Shared Function ScaleFor(asset As FactoryMapAsset) As (X As Single, Y As Single, Z As Single)
        If asset.Model = FactoryMapCustomEquipmentStore.ModelName Then Return (1, 1, 1)
        If IsVariableLength(asset.Model) OrElse FactoryMapEquipmentRenderer.IsOpeningModel(asset.Model) Then Return (1, 1, 1)
        Dim baseline = asset.Clone()
        baseline.Model = asset.Model.Replace(" with control and power panels", "")
        Dim target = Nominal(baseline.Model)
        Dim raw = FactoryMapEquipmentRenderer.RawSize(baseline)
        Return (target.Length * UnitsPerMeter / raw.Length, target.Width * UnitsPerMeter / raw.Width,
                target.Height * UnitsPerMeter / raw.Height)
    End Function

    Public Shared Function Dimensions(asset As FactoryMapAsset) As (Length As Single, Width As Single, Height As Single)
        If asset.Model = FactoryMapCustomEquipmentStore.ModelName Then
            If asset.CustomEquipment Is Nothing Then Throw New InvalidDataException("Custom equipment definition is missing.")
            Return (asset.CustomEquipment.LengthMeters, asset.CustomEquipment.WidthMeters, asset.CustomEquipment.HeightMeters)
        End If
        If FactoryMapEquipmentRenderer.IsOpeningModel(asset.Model) Then Return Nominal(asset.Model)
        Dim raw = FactoryMapEquipmentRenderer.RawSize(asset)
        Dim scale = ScaleFor(asset)
        Return (raw.Length * scale.X / UnitsPerMeter, raw.Width * scale.Y / UnitsPerMeter, raw.Height * scale.Z / UnitsPerMeter)
    End Function
End Class
