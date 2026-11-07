Imports System.Drawing
Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Linq

Friend NotInheritable Class FactoryMapLayoutStore
    Private NotInheritable Class LayoutDocument
        Public Property Version As Integer = 1
        Public Property EquipmentLibraryRevision As Integer
        Public Property Positions As Dictionary(Of String, LayoutPosition)
        Public Property Assets As List(Of SavedAsset)
    End Class

    Private NotInheritable Class SavedAsset
        Public Property Id As String
        Public Property Name As String
        Public Property Category As String
        Public Property Location As String
        Public Property Source As String
        Public Property X As Single
        Public Property Y As Single
        Public Property IsPlaced As Boolean
        Public Property EquipmentId As String
        Public Property MeterSourceId As String = String.Empty
        Public Property Model As String
        Public Property ConveyorLength As Single
        Public Property Rotation As Single
        Public Property MirrorHorizontal As Boolean
        Public Property MirrorVertical As Boolean
        Public Property ScaleX As Single = 1.0F
        Public Property ScaleY As Single = 1.0F
        Public Property ScaleZ As Single = 1.0F
        Public Property BodyColor As String = String.Empty
        Public Property IsOpen As Boolean
        Public Property OpeningDirection As String = "Left inward"
        Public Property MonitoringDeviceId As String = String.Empty
        Public Property ParentUpsId As String = String.Empty
        Public Property CameraIpAddress As String = String.Empty
        Public Property RoomId As String = String.Empty
        Public Property BaseHeightMeters As Single
        Public Property Level As String = "Ground"
        Public Property CustomEquipment As FactoryMapCustomEquipment
        Public Property ConnectorKind As String = String.Empty
        Public Property ConnectorFromId As String = String.Empty
        Public Property ConnectorToId As String = String.Empty
        Public Property ConnectorFromX As Single
        Public Property ConnectorFromY As Single
        Public Property ConnectorToX As Single
        Public Property ConnectorToY As Single
        Public Property ConnectorThicknessMeters As Single = 0.12F
        Public Property ConnectorHeightMeters As Single = 0.75F
        Public Property ConnectorStyle As String = String.Empty
        Public Property ConnectorCount As Integer = 1
        Public Property ConnectorDepthMeters As Single = 0.06F
    End Class

    Private NotInheritable Class LayoutPosition
        Public Property X As Single
        Public Property Y As Single
    End Class

    Private Shared ReadOnly Options As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True,
        .WriteIndented = True
    }

    Private Shared Function LayoutPath(Optional path As String = Nothing) As String
        Return If(String.IsNullOrWhiteSpace(path),
            IO.Path.Combine(AppContext.BaseDirectory, "data", "factory-map-layout.json"),
            IO.Path.GetFullPath(path))
    End Function

    Private Sub New()
    End Sub

    Public Shared Function LoadAssets(defaults As IEnumerable(Of FactoryMapAsset), Optional path As String = Nothing) As List(Of FactoryMapAsset)
        Dim assets = defaults.Select(Function(asset) asset.Clone()).ToList()
        Dim settingsPath = LayoutPath(path)
        If Not File.Exists(settingsPath) Then Return assets
        Dim document = JsonSerializer.Deserialize(Of LayoutDocument)(File.ReadAllText(settingsPath), Options)
        If document Is Nothing Then Throw New InvalidDataException("The factory map document is empty.")
        If document.Version = 1 Then
            Dim positions = Load(assets.Select(Function(asset) asset.Id), path)
            For Each asset In assets
                Dim position As PointF
                If positions.TryGetValue(asset.Id, position) Then
                    asset.Position = position
                    asset.IsPlaced = True
                End If
            Next
            Return assets
        End If
        If document.Version <> 2 OrElse document.Assets Is Nothing Then Throw New InvalidDataException("Unsupported or incomplete factory map document.")
        If document.EquipmentLibraryRevision < 0 OrElse document.EquipmentLibraryRevision > 1 Then Throw New InvalidDataException("Unsupported factory equipment library revision.")
        assets = document.Assets.Select(Function(item)
                                            If item Is Nothing Then Throw New InvalidDataException("The map contains an empty item.")
                                            Return New FactoryMapAsset With {
                                                .Id = item.Id, .Name = item.Name, .Category = item.Category,
                                                .Location = item.Location, .Source = item.Source,
                                                .Position = New PointF(item.X, item.Y), .IsPlaced = item.IsPlaced,
                                                .EquipmentId = item.EquipmentId,
                                                .Model = If(document.EquipmentLibraryRevision = 0, LegacyModel(item.Model), item.Model),
                                                .MeterSourceId = item.MeterSourceId,
                                                .ConveyorLength = item.ConveyorLength, .Rotation = item.Rotation,
                                                .MirrorHorizontal = item.MirrorHorizontal, .MirrorVertical = item.MirrorVertical,
                                                .ScaleX = item.ScaleX, .ScaleY = item.ScaleY, .ScaleZ = item.ScaleZ, .BodyColor = item.BodyColor,
                                                .IsOpen = item.IsOpen, .OpeningDirection = item.OpeningDirection,
                                                .MonitoringDeviceId = item.MonitoringDeviceId, .ParentUpsId = item.ParentUpsId,
                                                .CameraIpAddress = item.CameraIpAddress, .RoomId = item.RoomId, .BaseHeightMeters = item.BaseHeightMeters,
                                                .Level = item.Level, .CustomEquipment = item.CustomEquipment,
                                                .ConnectorKind = If(item.ConnectorKind, ""), .ConnectorFromId = If(item.ConnectorFromId, ""),
                                                .ConnectorToId = If(item.ConnectorToId, ""),
                                                .ConnectorFromPoint = New PointF(item.ConnectorFromX, item.ConnectorFromY),
                                                .ConnectorToPoint = New PointF(item.ConnectorToX, item.ConnectorToY),
                                                .ConnectorThicknessMeters = item.ConnectorThicknessMeters, .ConnectorHeightMeters = item.ConnectorHeightMeters,
                                                .ConnectorStyle = If(item.ConnectorStyle, ""), .ConnectorCount = item.ConnectorCount,
                                                .ConnectorDepthMeters = item.ConnectorDepthMeters
                                            }
                                        End Function).ToList()
        ValidateAssets(assets)
        Return assets
    End Function

    Private Shared Function LegacyModel(model As String) As String
        Select Case model
            Case "Hansa chiller" : Return "Chiller"
            Case "Hansa chiller with control and power panels" : Return "Chiller with control and power panels"
            Case Else : Return model
        End Select
    End Function

    Public Shared Sub ValidateAssets(assets As IEnumerable(Of FactoryMapAsset))
        Dim ids As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        For Each asset In assets
            If asset Is Nothing OrElse String.IsNullOrWhiteSpace(asset.Id) OrElse Not ids.Add(asset.Id) Then Throw New InvalidDataException("Map items must have unique, nonempty IDs.")
            If Not FactoryMapBuildingStore.Levels.Contains(asset.Level) Then Throw New InvalidDataException("Select a supported equipment level.")
            If String.IsNullOrWhiteSpace(asset.Name) OrElse asset.Name.Length > 120 Then Throw New InvalidDataException("Enter an item name of 1-120 characters.")
            If asset.Model = FactoryMapCustomEquipmentStore.ModelName Then
                If asset.Category <> "Utilities" Then Throw New InvalidDataException("Custom image equipment must use Utilities.")
                FactoryMapCustomEquipmentStore.Validate(asset.CustomEquipment)
            Else
                If asset.CustomEquipment IsNot Nothing Then Throw New InvalidDataException("Only a custom image model can contain an imported symbol.")
                If asset.Category Is Nothing OrElse Not FactoryMapItemForm.Shapes.ContainsKey(asset.Category) OrElse
                    Not FactoryMapItemForm.Shapes(asset.Category).Contains(asset.Model) Then Throw New InvalidDataException("Select a supported equipment category and shape.")
            End If
            If asset.EquipmentId Is Nothing Then Throw New InvalidDataException("The equipment link must be an ID or an empty string.")
            For Each link In {asset.MonitoringDeviceId, asset.ParentUpsId, asset.CameraIpAddress, asset.RoomId}
                If link Is Nothing OrElse link.Length > 120 OrElse link.Any(Function(c) Char.IsControl(c)) Then Throw New InvalidDataException("Monitoring links must be empty or valid IDs (max 120 characters).")
            Next
            If asset.ConnectorKind <> "" AndAlso (asset.Model <> FactoryMapCustomEquipmentStore.ModelName OrElse
                Not {FactoryMapConnectors.PipeKind, FactoryMapConnectors.ConveyorKind, FactoryMapConnectors.CableTrayKind, FactoryMapConnectors.CableKind}.Contains(asset.ConnectorKind) OrElse
                Not Single.IsFinite(asset.ConnectorThicknessMeters) OrElse asset.ConnectorThicknessMeters < 0.002F OrElse asset.ConnectorThicknessMeters > 3 OrElse
                Not Single.IsFinite(asset.ConnectorHeightMeters) OrElse asset.ConnectorHeightMeters < 0 OrElse asset.ConnectorHeightMeters > 10 OrElse
                asset.ConnectorCount < 1 OrElse asset.ConnectorCount > 5 OrElse
                Not Single.IsFinite(asset.ConnectorDepthMeters) OrElse asset.ConnectorDepthMeters < 0.005F OrElse asset.ConnectorDepthMeters > 1 OrElse
                If(asset.ConnectorStyle, "").Length > 20) Then
                Throw New InvalidDataException("Connectors need a valid kind, size (2 mm-3 m), height (0-10 m), 1-5 cables and tray depth (5 mm-1 m).")
            End If
            If Not Single.IsFinite(asset.BaseHeightMeters) OrElse asset.BaseHeightMeters < 0 OrElse asset.BaseHeightMeters > 100 Then Throw New InvalidDataException("Mounting height must be 0-100 m.")
            If asset.ParentUpsId <> "" AndAlso (asset.Category <> "Battery Rooms" OrElse Not asset.ParentUpsId.StartsWith("UPS-", StringComparison.OrdinalIgnoreCase)) Then Throw New InvalidDataException("Only a battery rack can have a parent UPS link.")
            If asset.CameraIpAddress <> "" Then
                Dim address As Net.IPAddress = Nothing
                If asset.Category <> "CCTV" OrElse Not Net.IPAddress.TryParse(asset.CameraIpAddress, address) Then Throw New InvalidDataException("Camera IP must be a valid IPv4/IPv6 address, without credentials or URL.")
            End If
            If asset.MeterSourceId Is Nothing OrElse asset.MeterSourceId.Length > 120 OrElse
                (asset.MeterSourceId <> "" AndAlso (String.IsNullOrWhiteSpace(asset.MeterSourceId) OrElse Not FactoryMapMeterLink.Supports(asset.Model))) Then
                Throw New InvalidDataException("An independent meter link requires a metered panel or energy-meter symbol and a valid configured meter ID.")
            End If
            If Not Single.IsFinite(asset.Position.X) OrElse Not Single.IsFinite(asset.Position.Y) OrElse
               asset.Position.X < 0 OrElse asset.Position.X > 4 OrElse asset.Position.Y < 0 OrElse asset.Position.Y > 4 Then Throw New InvalidDataException("Item coordinates must be within 0-400% of the original site footprint.")
            If Not Single.IsFinite(asset.ConveyorLength) OrElse asset.ConveyorLength < 20 OrElse asset.ConveyorLength > 400 Then Throw New InvalidDataException("Conveyor length must be 20-400 plan units.")
            If Not Single.IsFinite(asset.Rotation) OrElse asset.Rotation < 0 OrElse asset.Rotation >= 360 Then Throw New InvalidDataException("Rotation must be between 0 and 359 degrees.")
            For Each scale In {asset.ScaleX, asset.ScaleY, asset.ScaleZ}
                If Not Single.IsFinite(scale) OrElse scale < 0.1F OrElse scale > 5.0F Then Throw New InvalidDataException("Item length, width and height must each be 10-500 percent.")
            Next
            If asset.BodyColor Is Nothing OrElse (asset.BodyColor <> String.Empty AndAlso
                Not System.Text.RegularExpressions.Regex.IsMatch(asset.BodyColor, "\A#[0-9A-Fa-f]{6}\z")) Then Throw New InvalidDataException("Choose a valid item color (#RRGGBB) or the original colors.")
            If Not FactoryMapBuildingStore.Directions.Contains(asset.OpeningDirection) Then Throw New InvalidDataException("Choose a supported gate opening direction.")
        Next
    End Sub

    Public Shared Sub SaveAssets(assets As IEnumerable(Of FactoryMapAsset), Optional path As String = Nothing)
        Dim items = assets.ToList()
        ValidateAssets(items)
        Dim document As New LayoutDocument With {
            .Version = 2,
            .EquipmentLibraryRevision = 1,
            .Assets = items.Select(Function(asset) New SavedAsset With {
                .Id = asset.Id, .Name = asset.Name, .Category = asset.Category, .Location = asset.Location,
                .Source = asset.Source, .X = asset.Position.X, .Y = asset.Position.Y, .IsPlaced = asset.IsPlaced,
                .EquipmentId = asset.EquipmentId, .Model = asset.Model, .ConveyorLength = asset.ConveyorLength,
                .MeterSourceId = asset.MeterSourceId,
                .Rotation = asset.Rotation, .MirrorHorizontal = asset.MirrorHorizontal, .MirrorVertical = asset.MirrorVertical,
                .ScaleX = asset.ScaleX, .ScaleY = asset.ScaleY, .ScaleZ = asset.ScaleZ, .BodyColor = asset.BodyColor,
                .IsOpen = asset.IsOpen, .OpeningDirection = asset.OpeningDirection,
                .MonitoringDeviceId = asset.MonitoringDeviceId, .ParentUpsId = asset.ParentUpsId,
                .CameraIpAddress = asset.CameraIpAddress, .RoomId = asset.RoomId, .BaseHeightMeters = asset.BaseHeightMeters,
                .Level = asset.Level, .CustomEquipment = asset.CustomEquipment,
                .ConnectorKind = asset.ConnectorKind, .ConnectorFromId = asset.ConnectorFromId, .ConnectorToId = asset.ConnectorToId,
                .ConnectorFromX = asset.ConnectorFromPoint.X, .ConnectorFromY = asset.ConnectorFromPoint.Y,
                .ConnectorToX = asset.ConnectorToPoint.X, .ConnectorToY = asset.ConnectorToPoint.Y,
                .ConnectorThicknessMeters = asset.ConnectorThicknessMeters, .ConnectorHeightMeters = asset.ConnectorHeightMeters,
                .ConnectorStyle = asset.ConnectorStyle, .ConnectorCount = asset.ConnectorCount, .ConnectorDepthMeters = asset.ConnectorDepthMeters
            }).ToList()
        }
        WriteDocument(document, path)
    End Sub

    Public Shared Function Load(knownIds As IEnumerable(Of String), Optional path As String = Nothing) As Dictionary(Of String, PointF)
        Dim settingsPath = LayoutPath(path)
        Dim result As New Dictionary(Of String, PointF)(StringComparer.OrdinalIgnoreCase)
        If Not File.Exists(settingsPath) Then Return result

        Dim document = JsonSerializer.Deserialize(Of LayoutDocument)(File.ReadAllText(settingsPath), Options)
        If document Is Nothing OrElse document.Version <> 1 OrElse document.Positions Is Nothing Then
            Throw New InvalidDataException("The saved factory map layout is empty or uses an unsupported version.")
        End If

        Dim allowed = New HashSet(Of String)(knownIds, StringComparer.OrdinalIgnoreCase)
        For Each entry In document.Positions
            If Not allowed.Contains(entry.Key) Then
                Throw New InvalidDataException("The saved factory map layout contains an unknown equipment ID: " & entry.Key)
            End If
            If entry.Value Is Nothing OrElse Not Single.IsFinite(entry.Value.X) OrElse Not Single.IsFinite(entry.Value.Y) OrElse
               entry.Value.X < 0 OrElse entry.Value.X > 1 OrElse entry.Value.Y < 0 OrElse entry.Value.Y > 1 Then
                Throw New InvalidDataException("The saved position for " & entry.Key & " is outside the map.")
            End If
            result.Add(entry.Key, New PointF(entry.Value.X, entry.Value.Y))
        Next
        Return result
    End Function

    Public Shared Sub Save(positions As IEnumerable(Of KeyValuePair(Of String, PointF)), knownIds As IEnumerable(Of String),
                           Optional path As String = Nothing)
        If positions Is Nothing Then Throw New ArgumentNullException(NameOf(positions))
        Dim allowed = New HashSet(Of String)(knownIds, StringComparer.OrdinalIgnoreCase)
        Dim saved As New Dictionary(Of String, LayoutPosition)(StringComparer.OrdinalIgnoreCase)
        For Each entry In positions
            If Not allowed.Contains(entry.Key) Then
                Throw New InvalidDataException("Cannot save an unknown factory map equipment ID: " & entry.Key)
            End If
            If Not Single.IsFinite(entry.Value.X) OrElse Not Single.IsFinite(entry.Value.Y) OrElse
               entry.Value.X < 0 OrElse entry.Value.X > 1 OrElse entry.Value.Y < 0 OrElse entry.Value.Y > 1 Then
                Throw New InvalidDataException("The position for " & entry.Key & " is outside the map.")
            End If
            saved.Add(entry.Key, New LayoutPosition With {.X = entry.Value.X, .Y = entry.Value.Y})
        Next

        WriteDocument(New LayoutDocument With {.Positions = saved}, path)
    End Sub

    Private Shared Sub WriteDocument(document As LayoutDocument, path As String)
        Dim settingsPath = LayoutPath(path)
        Dim directory = IO.Path.GetDirectoryName(settingsPath)
        IO.Directory.CreateDirectory(directory)
        Dim temporary = settingsPath & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Dim backup = settingsPath & ".bak"
        Try
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, Options), New UTF8Encoding(False))
            If File.Exists(settingsPath) Then File.Copy(settingsPath, backup, True)
            File.Move(temporary, settingsPath, True)
        Finally
            If File.Exists(temporary) Then File.Delete(temporary)
        End Try
    End Sub
End Class
