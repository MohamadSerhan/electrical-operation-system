Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Globalization

Friend NotInheritable Class FactoryMapRenderCache
    Implements IDisposable

    Private NotInheritable Class NativeEntry
        Public Image As Bitmap
        Public Scale As Single
        Public Phase As PointF
        Public Pixels As Long
    End Class

    Private NotInheritable Class Entry
        Implements IDisposable
        Public Drawing As FactoryMapEquipmentRenderer.VectorDrawing
        Public Stamp As Long
        Public Image As Bitmap
        Public Resolution As Single
        Public ReadOnly NativeImages As New Dictionary(Of String, NativeEntry)
        Public RasterPixels As Long
        Public NativePixels As Long
        Public ReadOnly Property Pixels As Long
            Get
                Return RasterPixels + NativePixels
            End Get
        End Property
        Public Sub Dispose() Implements IDisposable.Dispose
            Drawing.Dispose()
            If Image IsNot Nothing Then Image.Dispose()
            ClearNative()
        End Sub
        Public Sub ClearNative()
            For Each item In NativeImages.Values
                item.Image.Dispose()
            Next
            NativeImages.Clear()
            NativePixels = 0
        End Sub
    End Class

    Private ReadOnly _drawings As New Dictionary(Of String, Entry)
    Private ReadOnly _customRenderer As New FactoryMapCustomEquipmentRenderer()
    Private _stamp As Long
    Private _camera As String = ""
    Public Property RetainNativeImages As Boolean = True
    Friend Property BuildCount As Integer
    Friend Property RasterBuildCount As Integer
    Friend Property VectorPaintCount As Integer
    Friend ReadOnly Property Pixels As Long
        Get
            Return _drawings.Values.Sum(Function(entry) entry.Pixels)
        End Get
    End Property

    Public Sub SetCamera(yaw As Single, elevation As Single)
        Dim key = yaw.ToString("R", CultureInfo.InvariantCulture) & "|" & elevation.ToString("R", CultureInfo.InvariantCulture)
        If key = _camera Then Return
        Dispose()
        _camera = key
    End Sub

    Public Function Draw(graphics As Graphics, asset As FactoryMapAsset, project As Func(Of Single, Single, Single, PointF),
                         sizeX As Single, sizeY As Single, sizeZ As Single, center As PointF, scale As Single,
                         viewport As Rectangle) As GraphicsPath
        If FactoryMapEquipmentRenderer.IsOpeningModel(asset.Model) OrElse asset.Model = FactoryMapCustomEquipmentStore.ModelName Then
            Dim openingHit As New GraphicsPath(FillMode.Winding)
            Dim screenProject As Func(Of Single, Single, Single, PointF) =
                Function(x, y, z)
                    Dim point = project(x, y, z)
                    Return New PointF(center.X + point.X * scale, center.Y + point.Y * scale)
                End Function
            If asset.Model = FactoryMapCustomEquipmentStore.ModelName Then
                _customRenderer.Draw(graphics, asset, screenProject, openingHit)
            Else
                FactoryMapEquipmentRenderer.Draw(graphics, asset, screenProject, openingHit)
            End If
            Return openingHit
        End If
        Dim key = String.Join("|", {asset.Model, asset.Category, asset.BodyColor, asset.IsOpen.ToString(), asset.OpeningDirection,
            asset.MirrorHorizontal.ToString(), asset.MirrorVertical.ToString(),
            asset.ConveyorLength.ToString("R", CultureInfo.InvariantCulture), asset.Rotation.ToString("R", CultureInfo.InvariantCulture),
            sizeX.ToString("R", CultureInfo.InvariantCulture), sizeY.ToString("R", CultureInfo.InvariantCulture), sizeZ.ToString("R", CultureInfo.InvariantCulture)})
        Dim entry As Entry = Nothing
        If Not _drawings.TryGetValue(key, entry) Then
            entry = New Entry With {.Drawing = New FactoryMapEquipmentRenderer.VectorDrawing(asset, project)}
            _drawings.Add(key, entry)
            BuildCount += 1
        End If
        _stamp += 1
        entry.Stamp = _stamp
        Dim bounds = entry.Drawing.Bounds
        bounds.Inflate(2, 2)
        Dim screenBounds As New RectangleF(center.X + bounds.X * scale, center.Y + bounds.Y * scale,
                                           bounds.Width * scale, bounds.Height * scale)
        screenBounds.Inflate(8, 8)
        Dim result As GraphicsPath = Nothing
        If screenBounds.IntersectsWith(viewport) Then
            Dim resolution = CSng(Math.Pow(2, Math.Ceiling(Math.Log(Math.Max(0.125F, scale * 2), 2))))
            If entry.Image IsNot Nothing AndAlso entry.Resolution >= resolution AndAlso entry.Resolution <= resolution * 2 Then
                resolution = entry.Resolution
            End If
            Dim width = Math.Max(1, CInt(Math.Ceiling(bounds.Width * resolution)))
            Dim height = Math.Max(1, CInt(Math.Ceiling(bounds.Height * resolution)))
            If width <= 3072 AndAlso height <= 3072 AndAlso CLng(width) * height <= 4 * 1024 * 1024 Then
                If entry.Image Is Nothing OrElse entry.Resolution <> resolution Then
                    If entry.Image IsNot Nothing Then entry.Image.Dispose()
                    entry.ClearNative()
                    entry.Image = New Bitmap(width, height, Imaging.PixelFormat.Format32bppPArgb)
                    entry.RasterPixels = CLng(width) * height
                    entry.Resolution = resolution
                    Using target = Drawing.Graphics.FromImage(entry.Image)
                        target.SmoothingMode = SmoothingMode.AntiAlias
                        entry.Drawing.Paint(target, New PointF(-bounds.X * resolution, -bounds.Y * resolution), resolution, resolution / 2)
                    End Using
                    RasterBuildCount += 1
                End If
                graphics.InterpolationMode = InterpolationMode.HighQualityBilinear
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality
                Dim destination As New RectangleF(center.X + bounds.X * scale, center.Y + bounds.Y * scale,
                    entry.Image.Width * scale / resolution, entry.Image.Height * scale / resolution)
                If Not DrawNative(graphics, entry, asset.Id, destination, scale, RetainNativeImages) Then
                    Dim visible = RectangleF.Intersect(destination, viewport)
                    Dim source As New RectangleF((visible.X - destination.X) * resolution / scale,
                        (visible.Y - destination.Y) * resolution / scale, visible.Width * resolution / scale, visible.Height * resolution / scale)
                    If visible.Width > 0 AndAlso visible.Height > 0 Then graphics.DrawImage(entry.Image, visible, source, GraphicsUnit.Pixel)
                End If
                graphics.PixelOffsetMode = PixelOffsetMode.Default
            Else
                entry.Drawing.Paint(graphics, center, scale)
                VectorPaintCount += 1
            End If
            result = DirectCast(entry.Drawing.Path.Clone(), GraphicsPath)
            Using transform As New Matrix(scale, 0, 0, scale, center.X, center.Y)
                result.Transform(transform)
            End Using
        End If
        While _drawings.Count > 96 OrElse Pixels > 16 * 1024 * 1024 OrElse
            _drawings.Values.Sum(Function(item) item.NativeImages.Count) > 256
            Dim oldest = _drawings.OrderBy(Function(item) item.Value.Stamp).First()
            oldest.Value.Dispose()
            _drawings.Remove(oldest.Key)
        End While
        Return result
    End Function

    Private Shared Function DrawNative(graphics As Graphics, entry As Entry, id As String, destination As RectangleF,
                                      scale As Single, retain As Boolean) As Boolean
        If destination.Width < 12 OrElse destination.Height < 12 Then Return False
        Dim left = CInt(Math.Floor(destination.Left)) - 2
        Dim top = CInt(Math.Floor(destination.Top)) - 2
        Dim phase As New PointF(destination.X - left, destination.Y - top)
        Dim native As NativeEntry = Nothing
        entry.NativeImages.TryGetValue(id, native)
        If native Is Nothing OrElse native.Image Is Nothing OrElse native.Scale <> scale OrElse native.Phase <> phase Then
            If Not retain Then Return False
            If native Is Nothing Then
                native = New NativeEntry
                entry.NativeImages.Add(id, native)
            End If
            If native.Image IsNot Nothing Then native.Image.Dispose()
            Dim previousPixels = native.Pixels
            native.Image = New Bitmap(CInt(Math.Ceiling(destination.Width)) + 5,
                CInt(Math.Ceiling(destination.Height)) + 5, Imaging.PixelFormat.Format32bppPArgb)
            native.Pixels = CLng(native.Image.Width) * native.Image.Height
            entry.NativePixels += native.Pixels - previousPixels
            native.Scale = scale
            native.Phase = phase
            Using target = Drawing.Graphics.FromImage(native.Image)
                target.CompositingMode = CompositingMode.SourceCopy
                target.InterpolationMode = InterpolationMode.HighQualityBilinear
                target.PixelOffsetMode = PixelOffsetMode.HighQuality
                target.DrawImage(entry.Image, New RectangleF(phase.X, phase.Y, destination.Width, destination.Height),
                    New RectangleF(0, 0, entry.Image.Width, entry.Image.Height), GraphicsUnit.Pixel)
            End Using
        End If
        graphics.DrawImageUnscaled(native.Image, left, top)
        Return True
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        _customRenderer.Dispose()
        For Each entry In _drawings.Values
            entry.Dispose()
        Next
        _drawings.Clear()
    End Sub
End Class
