Imports System.Drawing
Imports System.Drawing.Drawing2D

Friend NotInheritable Class FactoryMapCustomEquipmentRenderer
    Implements IDisposable

    Private Structure SilhouetteEdge
        Public X1 As Single
        Public Y1 As Single
        Public X2 As Single
        Public Y2 As Single
    End Structure

    Private NotInheritable Class CachedImage
        Implements IDisposable

        Public ReadOnly Image As Bitmap
        Public ReadOnly Edges As List(Of SilhouetteEdge)

        Public Sub New(image As Bitmap)
            Me.Image = image
            Edges = BuildSilhouetteEdges(image)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Image.Dispose()
        End Sub
    End Class

    Private ReadOnly _images As New Dictionary(Of String, CachedImage)

    Public Sub Draw(graphics As Graphics, asset As FactoryMapAsset, project As Func(Of Single, Single, Single, PointF), hit As GraphicsPath)
        Dim definition = asset.CustomEquipment
        If definition Is Nothing Then Throw New IO.InvalidDataException("Custom equipment image is missing.")
        If definition.GeometryModel = FactoryMapCustomEquipmentStore.CadModelName Then
            FactoryMapEquipmentRenderer.DrawImportedGeometry(graphics, asset, project, hit)
            Return
        End If
        If definition.GeometryModel = FactoryMapCustomEquipmentStore.SidelModelName Then
            Dim modeled = asset.Clone()
            modeled.Model = definition.GeometryModel
            FactoryMapEquipmentRenderer.Draw(graphics, modeled,
                Function(modelX, modelY, modelZ) project(modelX * definition.LengthMeters / 8,
                    modelY * definition.WidthMeters / 2.5F, modelZ * definition.HeightMeters / 2.5F), hit)
            Return
        End If
        project = FactoryMapEquipmentRenderer.MirrorProjection(asset, project)
        Dim cached As CachedImage = Nothing
        If Not _images.TryGetValue(definition.ImageHash, cached) Then
            cached = New CachedImage(FactoryMapCustomEquipmentStore.DecodeImage(definition))
            If _images.Count >= 8 Then
                Dim oldest = _images.First()
                oldest.Value.Dispose()
                _images.Remove(oldest.Key)
            End If
            _images.Add(definition.ImageHash, cached)
        End If

        Dim w = definition.LengthMeters * 4
        Dim d = definition.WidthMeters * 4
        Dim z = definition.HeightMeters * 8
        Dim bottom = {project(-w, -d, 0), project(w, -d, 0), project(w, d, 0), project(-w, d, 0)}
        hit.AddPolygon(bottom)
        Dim origin = project(0, 0, 0)
        Dim verticalPoint = project(0, 0, z)
        Dim verticalX = verticalPoint.X - origin.X
        Dim verticalY = verticalPoint.Y - origin.Y
        Dim state = graphics.Save()
        Try
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality
            If Math.Abs(verticalX) + Math.Abs(verticalY) < 1 Then
                Dim plan = {project(-w, -d, 0), project(w, -d, 0), project(w, d, 0), project(-w, d, 0)}
                graphics.DrawImage(cached.Image, New PointF() {plan(0), plan(1), plan(3)},
                                   New RectangleF(0, 0, cached.Image.Width, cached.Image.Height), GraphicsUnit.Pixel)
                Return
            End If

            Dim color As Color = If(String.IsNullOrEmpty(asset.BodyColor), Color.FromArgb(156, 176, 190), ColorTranslator.FromHtml(asset.BodyColor))
            DrawExtrudedSilhouette(graphics, cached.Edges, project, w, d, z, color, hit)

            Dim frontTopLeft = project(-w, -d / 2, z)
            Dim frontTopRight = project(w, -d / 2, z)
            Dim frontBottomLeft = project(-w, -d / 2, 0)
            Dim front = {frontTopLeft, frontTopRight, frontBottomLeft}
            graphics.DrawImage(cached.Image, front, New RectangleF(0, 0, cached.Image.Width, cached.Image.Height), GraphicsUnit.Pixel)
            hit.AddPolygon({frontTopLeft, frontTopRight, project(w, -d / 2, 0), frontBottomLeft})

            Dim backTopLeft = project(-w, d / 2, z)
            Dim backTopRight = project(w, d / 2, z)
            Dim backBottomLeft = project(-w, d / 2, 0)
            graphics.DrawImage(cached.Image, New PointF() {backTopRight, backTopLeft, project(w, d / 2, 0)},
                               New RectangleF(0, 0, cached.Image.Width, cached.Image.Height), GraphicsUnit.Pixel)
            hit.AddPolygon({backTopLeft, backTopRight, project(w, d / 2, 0), backBottomLeft})
        Finally
            graphics.Restore(state)
        End Try
    End Sub

    Private Shared Sub DrawExtrudedSilhouette(graphics As Graphics, edges As List(Of SilhouetteEdge),
                                               project As Func(Of Single, Single, Single, PointF),
                                               width As Single, depth As Single, height As Single, color As Color,
                                               hit As GraphicsPath)
        Using brush As New SolidBrush(ControlPaint.Dark(color, 0.22F)),
              pen As New Pen(ControlPaint.Dark(color, 0.4F), 0.7F)
            For Each edge In edges
                Dim x1 = -width + edge.X1 * width / 0.5F
                Dim x2 = -width + edge.X2 * width / 0.5F
                Dim z1 = height * (1 - edge.Y1)
                Dim z2 = height * (1 - edge.Y2)
                Dim side = {
                    project(x1, -depth / 2, z1), project(x2, -depth / 2, z2),
                    project(x2, depth / 2, z2), project(x1, depth / 2, z1)
                }
                graphics.FillPolygon(brush, side)
                graphics.DrawPolygon(pen, side)
                hit.AddPolygon(side)
            Next
        End Using
    End Sub

    Private Shared Function BuildSilhouetteEdges(image As Bitmap) As List(Of SilhouetteEdge)
        Dim stepSize = Math.Max(1, CInt(Math.Ceiling(Math.Max(image.Width, image.Height) / 128.0R)))
        Dim columns = CInt(Math.Ceiling(image.Width / CDbl(stepSize)))
        Dim rows = CInt(Math.Ceiling(image.Height / CDbl(stepSize)))
        Dim visible(columns - 1, rows - 1) As Boolean
        For y = 0 To rows - 1
            For x = 0 To columns - 1
                Dim pixelX = Math.Min(image.Width - 1, x * stepSize + stepSize \ 2)
                Dim pixelY = Math.Min(image.Height - 1, y * stepSize + stepSize \ 2)
                visible(x, y) = image.GetPixel(pixelX, pixelY).A > 24
            Next
        Next

        Dim edges As New List(Of SilhouetteEdge)
        For y = 0 To rows - 1
            For x = 0 To columns - 1
                If Not visible(x, y) Then Continue For
                Dim x1 = CSng(x * stepSize / CDbl(image.Width))
                Dim x2 = CSng(Math.Min(image.Width, (x + 1) * stepSize) / CDbl(image.Width))
                Dim y1 = CSng(y * stepSize / CDbl(image.Height))
                Dim y2 = CSng(Math.Min(image.Height, (y + 1) * stepSize) / CDbl(image.Height))
                If y = 0 OrElse Not visible(x, y - 1) Then edges.Add(New SilhouetteEdge With {.X1 = x1, .Y1 = y1, .X2 = x2, .Y2 = y1})
                If y = rows - 1 OrElse Not visible(x, y + 1) Then edges.Add(New SilhouetteEdge With {.X1 = x1, .Y1 = y2, .X2 = x2, .Y2 = y2})
                If x = 0 OrElse Not visible(x - 1, y) Then edges.Add(New SilhouetteEdge With {.X1 = x1, .Y1 = y1, .X2 = x1, .Y2 = y2})
                If x = columns - 1 OrElse Not visible(x + 1, y) Then edges.Add(New SilhouetteEdge With {.X1 = x2, .Y1 = y1, .X2 = x2, .Y2 = y2})
            Next
        Next
        Return edges
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        For Each cached In _images.Values
            cached.Dispose()
        Next
        _images.Clear()
    End Sub
End Class
