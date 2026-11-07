Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Linq

Friend NotInheritable Class FactoryMapPlacement
    Private Sub New()
    End Sub

    Public Shared Function RotatePoint(x As Single, y As Single, degrees As Single) As PointF
        Dim angle = degrees * Math.PI / 180
        Return New PointF(CSng(x * Math.Cos(angle) - y * Math.Sin(angle)),
                          CSng(x * Math.Sin(angle) + y * Math.Cos(angle)))
    End Function

    Public Shared Function AssetFootprint(asset As FactoryMapAsset) As RectangleF
        If asset.Model = FactoryMapCustomEquipmentStore.ModelName Then
            Dim size = FactoryMapEquipmentSizes.Dimensions(asset)
            Dim w = size.Length * 4 * asset.ScaleX, d = size.Width * 4 * asset.ScaleY
            Dim corners = {RotatePoint(-w, -d, asset.Rotation), RotatePoint(w, -d, asset.Rotation),
                RotatePoint(w, d, asset.Rotation), RotatePoint(-w, d, asset.Rotation)}
            Return RectangleF.FromLTRB(corners.Min(Function(p) p.X), corners.Min(Function(p) p.Y),
                corners.Max(Function(p) p.X), corners.Max(Function(p) p.Y))
        End If
        Using bitmap As New Bitmap(1, 1), graphics = Drawing.Graphics.FromImage(bitmap), hit As New GraphicsPath(FillMode.Winding)
            Dim rotation = asset.Rotation + If(asset.Category = "Generators", 90, 0)
            FactoryMapEquipmentRenderer.Draw(graphics, asset,
                Function(x, y, z) RotatePoint(x * asset.ScaleX, y * asset.ScaleY, rotation), hit)
            Return hit.GetBounds()
        End Using
    End Function

    Public Shared Function BuildingFootprint(part As FactoryBuildingPart) As RectangleF
        Dim local = If(FactoryMapRoadGeometry.IsCurve(part),
            FactoryMapRoadGeometry.Strip(part, -part.RoadBendDegrees * Math.PI / 360, part.RoadBendDegrees * Math.PI / 360, -part.Depth / 2, part.Depth / 2),
            {New PointF(-part.Length / 2, -part.Depth / 2), New PointF(part.Length / 2, -part.Depth / 2),
             New PointF(part.Length / 2, part.Depth / 2), New PointF(-part.Length / 2, part.Depth / 2)})
        Dim corners = local.
            Select(Function(point) FactoryMapBuildingStore.WorldPoint(part, point.X, point.Y)).ToArray()
        Return RectangleF.FromLTRB(corners.Min(Function(point) point.X), corners.Min(Function(point) point.Y),
                                   corners.Max(Function(point) point.X), corners.Max(Function(point) point.Y))
    End Function

    Public Shared Function SnapOffset(moving As RectangleF, targets As IEnumerable(Of RectangleF), tolerance As Single) As PointF
        Dim bestX = tolerance + 1
        Dim bestY = tolerance + 1
        Dim offset As PointF
        For Each target In targets
            If moving.Bottom >= target.Top - tolerance AndAlso moving.Top <= target.Bottom + tolerance Then
                For Each delta In {target.Left - moving.Right, target.Right - moving.Left}
                    If Math.Abs(delta) <= tolerance AndAlso Math.Abs(delta) < bestX Then
                        bestX = Math.Abs(delta)
                        offset.X = delta
                    End If
                Next
            End If
            If moving.Right >= target.Left - tolerance AndAlso moving.Left <= target.Right + tolerance Then
                For Each delta In {target.Top - moving.Bottom, target.Bottom - moving.Top}
                    If Math.Abs(delta) <= tolerance AndAlso Math.Abs(delta) < bestY Then
                        bestY = Math.Abs(delta)
                        offset.Y = delta
                    End If
                Next
            End If
        Next
        Return offset
    End Function

    Public Shared Function AttachOpening(candidate As FactoryBuildingPart, parts As IEnumerable(Of FactoryBuildingPart), tolerance As Single) As Boolean
        If Not {"Door", "Window", "Gate"}.Contains(candidate.Kind) Then Return False
        Dim list = parts.ToList()
        Dim best As FactoryBuildingPart = Nothing
        Dim bestOffset As Single
        Dim bestBase As Single
        Dim nearest = tolerance
        For Each wall In list.Where(Function(part) part.Kind = "Wall" AndAlso part.Level = candidate.Level)
            Dim relativeBase = Math.Max(0, candidate.BaseMeters - wall.BaseMeters)
            If candidate.Length > wall.Length OrElse relativeBase + candidate.HeightMeters > wall.HeightMeters Then Continue For
            Dim local = FactoryMapBuildingStore.LocalPoint(wall, New PointF(candidate.X * 1365, candidate.Y * 766))
            Dim limit = (wall.Length - candidate.Length) / 2
            Dim offset = Math.Clamp(local.X, -limit, limit)
            Dim distance = CSng(Math.Sqrt((local.X - offset) ^ 2 + local.Y ^ 2))
            If distance > nearest + wall.Depth / 2 Then Continue For
            If list.Any(Function(part) part.Id <> candidate.Id AndAlso part.ParentWallId = wall.Id AndAlso
                Math.Abs(part.Offset - offset) < (part.Length + candidate.Length) / 2 + 0.1F) Then Continue For
            If best IsNot Nothing AndAlso distance >= nearest Then Continue For
            nearest = distance
            best = wall
            bestOffset = offset
            bestBase = relativeBase
        Next
        If best Is Nothing Then Return False
        If candidate.ParentWallId = "" Then
            candidate.MirrorHorizontal = candidate.MirrorHorizontal Xor best.MirrorHorizontal
            candidate.MirrorVertical = candidate.MirrorVertical Xor best.MirrorVertical
        End If
        candidate.ParentWallId = best.Id
        candidate.Offset = bestOffset
        candidate.Rotation = best.Rotation
        candidate.Depth = best.Depth
        candidate.BaseMeters = bestBase
        Dim center = FactoryMapBuildingStore.WorldPoint(best, bestOffset, 0)
        candidate.Position = New PointF(center.X / 1365, center.Y / 766)
        candidate.RoomId = best.RoomId
        candidate.RoomName = best.RoomName
        candidate.TemperatureSourceId = best.TemperatureSourceId
        candidate.TemperatureSourceConfirmed = best.TemperatureSourceConfirmed
        candidate.FirePanelId = best.FirePanelId
        candidate.FireSiteId = best.FireSiteId
        candidate.FireZone = best.FireZone
        Return True
    End Function

    Public Shared Function SnapWallPoint(point As PointF, parts As IEnumerable(Of FactoryBuildingPart), level As String,
                                         excludedId As String, tolerance As Single) As PointF
        Dim result = point
        Dim nearest = tolerance
        For Each wall In parts.Where(Function(part) part.Kind = "Wall" AndAlso part.Level = level AndAlso part.Id <> excludedId)
            Dim local = FactoryMapBuildingStore.LocalPoint(wall, point)
            Dim onWall = FactoryMapBuildingStore.WorldPoint(wall, Math.Clamp(local.X, -wall.Length / 2, wall.Length / 2), 0)
            For Each target In {FactoryMapBuildingStore.WorldPoint(wall, -wall.Length / 2, 0),
                                FactoryMapBuildingStore.WorldPoint(wall, wall.Length / 2, 0), onWall}
                Dim distance = CSng(Math.Sqrt((point.X - target.X) ^ 2 + (point.Y - target.Y) ^ 2))
                If distance <= nearest Then
                    result = target
                    nearest = distance
                End If
            Next
        Next
        Return result
    End Function
End Class
