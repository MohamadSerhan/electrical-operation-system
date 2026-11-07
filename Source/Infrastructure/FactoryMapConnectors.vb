Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Linq

''' <summary>Pipes and conveyors that stay attached to equipment and re-route automatically when it moves.</summary>
Friend NotInheritable Class FactoryMapConnectors
    Public Const PipeKind As String = "Pipe"
    Public Const ConveyorKind As String = "Conveyor"
    Public Const CableTrayKind As String = "Cable tray"
    Public Const CableKind As String = "Cable"
    Public Const MeshStyle As String = "Mesh"
    Public Const NormalStyle As String = "Normal"
    Private Const MapWidth As Single = 1365.0F
    Private Const MapHeight As Single = 766.0F

    Private Structure EndPoint
        Public Center As PointF
        Public Extent As RectangleF
    End Structure

    Private Sub New()
    End Sub

    Public Shared Function IsConnector(asset As FactoryMapAsset) As Boolean
        Return asset IsNot Nothing AndAlso asset.ConnectorKind <> ""
    End Function

    Public Shared Function Create(kind As String, name As String, fromAsset As FactoryMapAsset, fromPoint As PointF,
                                  toAsset As FactoryMapAsset, toPoint As PointF, color As String, thicknessMeters As Single,
                                  heightMeters As Single, assets As IEnumerable(Of FactoryMapAsset),
                                  Optional style As String = "", Optional count As Integer = 1, Optional depthMeters As Single = 0.06F) As FactoryMapAsset
        Dim connector As New FactoryMapAsset With {
            .ConnectorStyle = style, .ConnectorCount = count, .ConnectorDepthMeters = depthMeters,
            .Id = "MAP-" & Guid.NewGuid().ToString("N"), .Name = name, .Category = "Utilities", .Model = FactoryMapCustomEquipmentStore.ModelName,
            .ConnectorKind = kind, .ConnectorFromId = If(fromAsset?.Id, ""), .ConnectorToId = If(toAsset?.Id, ""),
            .ConnectorFromPoint = fromPoint, .ConnectorToPoint = toPoint, .BodyColor = color,
            .ConnectorThicknessMeters = thicknessMeters, .ConnectorHeightMeters = heightMeters,
            .Source = "Drawn connector - no linked acquisition source", .Location = "Attached connector", .IsPlaced = True
        }
        Rebuild(connector, assets)
        Return connector
    End Function

    ' Returns true when any connector was re-routed.
    Public Shared Function Sync(assets As IEnumerable(Of FactoryMapAsset)) As Boolean
        Dim list = assets.ToList()
        Dim changed = False
        For Each connector In list.Where(AddressOf IsConnector)
            If connector.ConnectorSignature <> Signature(connector, list) Then
                Rebuild(connector, list)
                changed = True
            End If
        Next
        Return changed
    End Function

    Private Shared Function Find(assets As IEnumerable(Of FactoryMapAsset), id As String) As FactoryMapAsset
        If String.IsNullOrEmpty(id) Then Return Nothing
        Return assets.FirstOrDefault(Function(item) item.Id = id AndAlso item.IsPlaced AndAlso Not IsConnector(item))
    End Function

    Private Shared Function Signature(connector As FactoryMapAsset, assets As IEnumerable(Of FactoryMapAsset)) As String
        Dim parts As New List(Of String) From {connector.ConnectorKind, connector.BodyColor,
            F(connector.ConnectorThicknessMeters), F(connector.ConnectorHeightMeters), F(connector.Position.X), F(connector.Position.Y),
            connector.ConnectorStyle, connector.ConnectorCount.ToString(CultureInfo.InvariantCulture), F(connector.ConnectorDepthMeters)}
        For Each end_ In {(connector.ConnectorFromId, connector.ConnectorFromPoint), (connector.ConnectorToId, connector.ConnectorToPoint)}
            Dim target = Find(assets, end_.Item1)
            If target Is Nothing Then
                parts.Add(F(end_.Item2.X) & "," & F(end_.Item2.Y))
            Else
                parts.Add(String.Join(",", {F(target.Position.X), F(target.Position.Y), F(target.Rotation), F(target.ScaleX), F(target.ScaleY), target.Model}))
            End If
        Next
        Return String.Join("|", parts)
    End Function

    Private Shared Function F(value As Single) As String
        Return value.ToString("R", CultureInfo.InvariantCulture)
    End Function

    Private Shared Function Resolve(assets As IEnumerable(Of FactoryMapAsset), id As String, point As PointF) As EndPoint
        Dim target = Find(assets, id)
        If target Is Nothing Then Return New EndPoint With {.Center = New PointF(point.X * MapWidth, point.Y * MapHeight)}
        Return New EndPoint With {
            .Center = New PointF(target.Position.X * MapWidth, target.Position.Y * MapHeight),
            .Extent = FactoryMapPlacement.AssetFootprint(target)
        }
    End Function

    Public Shared Function Route(a As PointF, aExtent As RectangleF, b As PointF, bExtent As RectangleF) As PointF()
        Dim dx = b.X - a.X, dy = b.Y - a.Y
        Dim aSideX = If(dx >= 0, aExtent.Right, aExtent.Left), bSideX = If(dx >= 0, bExtent.Left, bExtent.Right)
        Dim aSideY = If(dy >= 0, aExtent.Bottom, aExtent.Top), bSideY = If(dy >= 0, bExtent.Top, bExtent.Bottom)
        Dim gapX = Math.Abs(dx) - (If(dx >= 0, aExtent.Right, -aExtent.Left) + If(dx >= 0, -bExtent.Left, bExtent.Right))
        Dim gapY = Math.Abs(dy) - (If(dy >= 0, aExtent.Bottom, -aExtent.Top) + If(dy >= 0, -bExtent.Top, bExtent.Bottom))
        If gapX <= 0 AndAlso gapY <= 0 Then Return {a, New PointF(b.X, a.Y), b}
        If gapX >= gapY Then
            Dim sx = a.X + aSideX, ex = b.X + bSideX
            Dim mx = (sx + ex) / 2
            Return {New PointF(sx, a.Y), New PointF(mx, a.Y), New PointF(mx, b.Y), New PointF(ex, b.Y)}
        End If
        Dim sy = a.Y + aSideY, ey = b.Y + bSideY
        Dim my = (sy + ey) / 2
        Return {New PointF(a.X, sy), New PointF(a.X, my), New PointF(b.X, my), New PointF(b.X, ey)}
    End Function

    Public Shared Sub Rebuild(connector As FactoryMapAsset, assets As IEnumerable(Of FactoryMapAsset))
        Dim list = assets.ToList()
        Dim a = Resolve(list, connector.ConnectorFromId, connector.ConnectorFromPoint)
        Dim b = Resolve(list, connector.ConnectorToId, connector.ConnectorToPoint)
        If Find(list, connector.ConnectorFromId) IsNot Nothing Then connector.ConnectorFromPoint = New PointF(a.Center.X / MapWidth, a.Center.Y / MapHeight)
        If Find(list, connector.ConnectorToId) IsNot Nothing Then connector.ConnectorToPoint = New PointF(b.Center.X / MapWidth, b.Center.Y / MapHeight)
        Dim path = Route(a.Center, a.Extent, b.Center, b.Extent)
        Dim clean = path.Where(Function(p, i) i = 0 OrElse p <> path(i - 1)).ToArray()
        If clean.Length < 2 Then clean = {clean(0), New PointF(clean(0).X + 8, clean(0).Y)}

        Dim thickness = Math.Clamp(connector.ConnectorThicknessMeters, 0.002F, 3.0F) * 8.0F
        Dim z = Math.Clamp(connector.ConnectorHeightMeters, 0.0F, 10.0F) * 8.0F
        Dim isPipe = connector.ConnectorKind = PipeKind
        Dim bodyHeight = If(isPipe, thickness, Math.Max(0.6F, thickness * 0.18F))
        Dim leg = Math.Max(0.5F, If(isPipe, thickness * 0.5F, thickness * 0.15F))
        Dim geometry As New FactoryMapCadGeometry
        If connector.ConnectorKind = CableTrayKind Then
            BuildTray(geometry, clean, thickness, Math.Clamp(connector.ConnectorDepthMeters, 0.005F, 1.0F) * 8.0F, z,
                      String.Equals(connector.ConnectorStyle, MeshStyle, StringComparison.OrdinalIgnoreCase))
        ElseIf connector.ConnectorKind = CableKind Then
            BuildCables(geometry, clean, thickness, Math.Clamp(connector.ConnectorCount, 1, 5), z)
        Else
            For i = 1 To clean.Length - 1
                Dim p = clean(i - 1), q = clean(i)
                AddBox(geometry, Math.Min(p.X, q.X) - thickness / 2, Math.Min(p.Y, q.Y) - thickness / 2, z,
                       Math.Abs(q.X - p.X) + thickness, Math.Abs(q.Y - p.Y) + thickness, bodyHeight)
                If z > 0.01F Then
                    Dim distance = Math.Abs(q.X - p.X) + Math.Abs(q.Y - p.Y)
                    Dim spacing = If(isPipe, 40.0F, 24.0F)
                    For stepIndex = 0 To CInt(Math.Floor(distance / spacing))
                        Dim t = If(distance > 0, Math.Min(1.0F, stepIndex * spacing / distance), 0.0F)
                        AddBox(geometry, p.X + (q.X - p.X) * t - leg / 2, p.Y + (q.Y - p.Y) * t - leg / 2, 0, leg, leg, z)
                    Next
                End If
            Next
        End If
        Dim minX = geometry.Vertices.Min(Function(p) p.X), maxX = geometry.Vertices.Max(Function(p) p.X)
        Dim minY = geometry.Vertices.Min(Function(p) p.Y), maxY = geometry.Vertices.Max(Function(p) p.Y)
        Dim definition = FactoryMapCadImporter.FromGeometry(connector.Name, geometry)
        connector.CustomEquipment = FactoryMapCustomEquipmentStore.CreateAsset(definition).CustomEquipment
        connector.Position = New PointF(CSng((minX + maxX) / 2 / MapWidth), CSng((minY + maxY) / 2 / MapHeight))
        connector.BaseHeightMeters = 0
        connector.Rotation = 0
        connector.ScaleX = 1 : connector.ScaleY = 1 : connector.ScaleZ = 1
        connector.MirrorHorizontal = False : connector.MirrorVertical = False
        connector.ConnectorSignature = Signature(connector, list)
    End Sub

    Private Shared Sub BuildTray(mesh As FactoryMapCadGeometry, path As PointF(), widthUnits As Single, depthUnits As Single, z As Single, isMesh As Boolean)
        Dim w = Math.Max(0.8F, widthUnits)
        Dim rail = Math.Max(0.1F, Math.Min(0.25F, w * 0.06F))
        Const baseThickness As Single = 0.1F
        For i = 1 To path.Length - 1
            Dim p = path(i - 1), q = path(i)
            Dim x0 = Math.Min(p.X, q.X) - w / 2, x1 = Math.Max(p.X, q.X) + w / 2
            Dim y0 = Math.Min(p.Y, q.Y) - w / 2, y1 = Math.Max(p.Y, q.Y) + w / 2
            Dim horizontal = Math.Abs(q.X - p.X) >= Math.Abs(q.Y - p.Y)
            If horizontal Then
                AddBox(mesh, x0, y0, z, x1 - x0, rail, depthUnits)
                AddBox(mesh, x0, y1 - rail, z, x1 - x0, rail, depthUnits)
            Else
                AddBox(mesh, x0, y0, z, rail, y1 - y0, depthUnits)
                AddBox(mesh, x1 - rail, y0, z, rail, y1 - y0, depthUnits)
            End If
            If isMesh Then
                Dim length = If(horizontal, x1 - x0, y1 - y0)
                Dim rungStep = Math.Max(0.9F, length / 150.0F)
                For offset = 0.0F To length - 0.08F Step rungStep
                    If horizontal Then
                        AddBox(mesh, x0 + offset, y0, z, 0.08F, y1 - y0, baseThickness)
                    Else
                        AddBox(mesh, x0, y0 + offset, z, x1 - x0, 0.08F, baseThickness)
                    End If
                Next
            Else
                AddBox(mesh, x0, y0, z, x1 - x0, y1 - y0, baseThickness)
            End If
            If z > 0.01F Then
                Dim distance = Math.Abs(q.X - p.X) + Math.Abs(q.Y - p.Y)
                For stepIndex = 0 To CInt(Math.Floor(distance / 32.0F))
                    Dim t = If(distance > 0, Math.Min(1.0F, stepIndex * 32.0F / distance), 0.0F)
                    AddBox(mesh, p.X + (q.X - p.X) * t - 0.15F, p.Y + (q.Y - p.Y) * t - 0.15F, 0, 0.3F, 0.3F, z)
                Next
            End If
        Next
    End Sub

    Private Shared Sub BuildCables(mesh As FactoryMapCadGeometry, path As PointF(), diameterUnits As Single, count As Integer, z As Single)
        ' Very thin cables are drawn at a minimum visible size.
        Dim d = Math.Max(0.5F, diameterUnits)
        Dim pitch = d * 1.3F
        For lane = 0 To count - 1
            Dim o = (lane - (count - 1) / 2.0F) * pitch
            Dim shifted = OffsetPath(path, o)
            For i = 1 To shifted.Length - 1
                Dim p = shifted(i - 1), q = shifted(i)
                AddBox(mesh, Math.Min(p.X, q.X) - d / 2, Math.Min(p.Y, q.Y) - d / 2, z, Math.Abs(q.X - p.X) + d, Math.Abs(q.Y - p.Y) + d, d)
            Next
        Next
    End Sub

    Private Shared Function OffsetPath(path As PointF(), distance As Single) As PointF()
        Dim normals(path.Length - 2) As PointF
        For i = 1 To path.Length - 1
            Dim dx = path(i).X - path(i - 1).X, dy = path(i).Y - path(i - 1).Y
            Dim length = CSng(Math.Sqrt(dx * dx + dy * dy))
            normals(i - 1) = If(length > 0, New PointF(-dy / length, dx / length), New PointF(0, 0))
        Next
        Dim result(path.Length - 1) As PointF
        For i = 0 To path.Length - 1
            Dim shift As PointF
            If i = 0 Then
                shift = normals(0)
            ElseIf i = path.Length - 1 Then
                shift = normals(i - 1)
            Else
                Dim a = normals(i - 1), b = normals(i)
                Dim denominator = 1.0F + a.X * b.X + a.Y * b.Y
                shift = If(denominator > 0.2F, New PointF((a.X + b.X) / denominator, (a.Y + b.Y) / denominator), a)
            End If
            result(i) = New PointF(path(i).X + shift.X * distance, path(i).Y + shift.Y * distance)
        Next
        Return result
    End Function

    Private Shared Sub AddBox(mesh As FactoryMapCadGeometry, x As Single, y As Single, z As Single, w As Single, d As Single, h As Single)
        Dim start = mesh.Vertices.Count
        For Each p In {New FactoryMapCadPoint With {.X = x, .Y = y, .Z = z}, New FactoryMapCadPoint With {.X = x + w, .Y = y, .Z = z},
            New FactoryMapCadPoint With {.X = x + w, .Y = y + d, .Z = z}, New FactoryMapCadPoint With {.X = x, .Y = y + d, .Z = z},
            New FactoryMapCadPoint With {.X = x, .Y = y, .Z = z + h}, New FactoryMapCadPoint With {.X = x + w, .Y = y, .Z = z + h},
            New FactoryMapCadPoint With {.X = x + w, .Y = y + d, .Z = z + h}, New FactoryMapCadPoint With {.X = x, .Y = y + d, .Z = z + h}}
            mesh.Vertices.Add(p)
        Next
        For Each face In {New Integer() {0, 1, 5, 4}, New Integer() {1, 2, 6, 5}, New Integer() {2, 3, 7, 6},
                          New Integer() {3, 0, 4, 7}, New Integer() {4, 5, 6, 7}, New Integer() {0, 3, 2, 1}}
            mesh.Faces.Add(face.Select(Function(i) i + start).ToArray())
        Next
    End Sub
End Class
