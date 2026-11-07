Imports System.Drawing
Imports System.Drawing.Imaging
Imports System.IO
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json

Friend NotInheritable Class FactoryMapCustomEquipment
    Public Property Id As String = Guid.NewGuid().ToString("N")
    Public Property Name As String = "Custom equipment"
    Public Property LengthMeters As Single = 2
    Public Property WidthMeters As Single = 1
    Public Property HeightMeters As Single = 1
    Public Property PngBase64 As String = ""
    Public Property ImageHash As String = ""
    Public Property GeometryModel As String = ""
    Public Property CadGeometry As FactoryMapCadGeometry
    Public Property GeometryHash As String = ""
    Public Property ImportNotes As String = ""

    Public Function Clone() As FactoryMapCustomEquipment
        Dim copy = DirectCast(MemberwiseClone(), FactoryMapCustomEquipment)
        If CadGeometry IsNot Nothing Then copy.CadGeometry = CadGeometry.Clone()
        Return copy
    End Function
End Class

Friend NotInheritable Class FactoryMapCustomEquipmentStore
    Public Const ModelName As String = "Custom equipment (image)"
    Public Const SidelModelName As String = "Sidel-style blower and preform feed"
    Public Const CadModelName As String = "Imported OBJ/CAD geometry"
    Private Const MaxImageBytes As Integer = 2 * 1024 * 1024
    Private Shared ReadOnly Options As New JsonSerializerOptions With {.WriteIndented = True, .PropertyNameCaseInsensitive = True}

    Private Shared Function LibraryPath(directory As String) As String
        Return If(directory, Path.Combine(AppContext.BaseDirectory, "data", "equipment-library"))
    End Function

    Public Shared Function ImportImage(path As String) As FactoryMapCustomEquipment
        Dim extension = IO.Path.GetExtension(path).ToLowerInvariant()
        If Not {".png", ".jpg", ".jpeg"}.Contains(extension) Then Throw New InvalidDataException("Choose a PNG or JPG image. CAD, PDF and executable files are not image symbols.")
        Dim input As Byte()
        Using file = IO.File.OpenRead(path)
            If file.Length > 10 * 1024 * 1024 Then Throw New InvalidDataException("Equipment images must be at most 10 MB.")
            ReDim input(CInt(file.Length) - 1)
            file.ReadExactly(input)
        End Using
        CheckImageHeader(input)
        Try
            Using stream As New MemoryStream(input, False), source = Image.FromStream(stream, False, True)
                If Not source.RawFormat.Equals(ImageFormat.Png) AndAlso Not source.RawFormat.Equals(ImageFormat.Jpeg) Then
                    Throw New InvalidDataException("The selected file is not a PNG/JPG image.")
                End If
                If source.Width > 8192 OrElse source.Height > 8192 OrElse CLng(source.Width) * source.Height > 16000000 Then
                    Throw New InvalidDataException("Image dimensions must be at most 8192 per side and 16 million pixels.")
                End If
                Dim scale = Math.Min(1.0R, 1024.0R / Math.Max(source.Width, source.Height))
                Using normalized As New Bitmap(Math.Max(1, CInt(source.Width * scale)), Math.Max(1, CInt(source.Height * scale)), PixelFormat.Format32bppArgb)
                    Using graphics = Drawing.Graphics.FromImage(normalized)
                        graphics.Clear(Color.Transparent)
                        graphics.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBicubic
                        graphics.DrawImage(source, New Rectangle(0, 0, normalized.Width, normalized.Height))
                    End Using
                    Using cutout = RemoveLightBackgroundAndCrop(normalized),
                          output As New MemoryStream()
                        cutout.Save(output, ImageFormat.Png)
                        Dim bytes = output.ToArray()
                        If bytes.Length > MaxImageBytes Then Throw New InvalidDataException("The normalized image exceeds 2 MB. Use a smaller or simpler symbol.")
                        Dim name = IO.Path.GetFileNameWithoutExtension(path)
                        Dim result As New FactoryMapCustomEquipment With {
                            .Name = name.Substring(0, Math.Min(120, name.Length)),
                            .PngBase64 = Convert.ToBase64String(bytes), .ImageHash = Convert.ToHexString(SHA256.HashData(bytes))
                        }
                        Validate(result)
                        Return result
                    End Using
                End Using
            End Using
        Catch ex As ArgumentException
            Throw New InvalidDataException("The selected image is damaged or cannot be decoded.", ex)
        Catch ex As System.Runtime.InteropServices.ExternalException
            Throw New InvalidDataException("The image could not be decoded or normalized.", ex)
        End Try
    End Function

    Private Shared Function RemoveLightBackgroundAndCrop(image As Bitmap) As Bitmap
        Dim bounds As New Rectangle(0, 0, image.Width, image.Height)
        Dim data = image.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb)
        Dim rowBytes = image.Width * 4
        Dim pixels(rowBytes * image.Height - 1) As Byte
        Try
            Dim row(rowBytes - 1) As Byte
            For y = 0 To image.Height - 1
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, rowBytes)
                Buffer.BlockCopy(row, 0, pixels, y * rowBytes, rowBytes)
            Next

            Dim edgeCandidates As Integer
            For x = 0 To image.Width - 1
                If IsLightBackgroundPixel(pixels, x, 0, rowBytes) Then edgeCandidates += 1
                If IsLightBackgroundPixel(pixels, x, image.Height - 1, rowBytes) Then edgeCandidates += 1
            Next
            For y = 1 To image.Height - 2
                If IsLightBackgroundPixel(pixels, 0, y, rowBytes) Then edgeCandidates += 1
                If IsLightBackgroundPixel(pixels, image.Width - 1, y, rowBytes) Then edgeCandidates += 1
            Next

            If edgeCandidates >= (2 * image.Width + 2 * image.Height) / 5 Then
                Dim visited(image.Width * image.Height - 1) As Boolean
                Dim queue(image.Width * image.Height - 1) As Integer
                Dim head, tail As Integer
                Dim enqueue As Action(Of Integer, Integer) =
                    Sub(x, y)
                        Dim index = y * image.Width + x
                        If visited(index) OrElse Not IsLightBackgroundPixel(pixels, x, y, rowBytes) Then Return
                        visited(index) = True
                        queue(tail) = index
                        tail += 1
                    End Sub
                For x = 0 To image.Width - 1
                    enqueue(x, 0)
                    enqueue(x, image.Height - 1)
                Next
                For y = 1 To image.Height - 2
                    enqueue(0, y)
                    enqueue(image.Width - 1, y)
                Next
                While head < tail
                    Dim index = queue(head)
                    head += 1
                    Dim x = index Mod image.Width
                    Dim y = index \ image.Width
                    If x > 0 Then enqueue(x - 1, y)
                    If x + 1 < image.Width Then enqueue(x + 1, y)
                    If y > 0 Then enqueue(x, y - 1)
                    If y + 1 < image.Height Then enqueue(x, y + 1)
                End While

                For index = 0 To visited.Length - 1
                    If Not visited(index) Then Continue For
                    Dim pixelOffset = index * 4
                    Dim blue = CInt(pixels(pixelOffset))
                    Dim green = CInt(pixels(pixelOffset + 1))
                    Dim red = CInt(pixels(pixelOffset + 2))
                    Dim luminance = (red * 299 + green * 587 + blue * 114) \ 1000
                    Dim alpha = Math.Clamp((250 - luminance) * 255 \ 18, 0, 255)
                    pixels(pixelOffset + 3) = CByte(Math.Min(CInt(pixels(pixelOffset + 3)), alpha))
                Next
                For y = 0 To image.Height - 1
                    Buffer.BlockCopy(pixels, y * rowBytes, row, 0, rowBytes)
                    Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes)
                Next
            End If
        Finally
            image.UnlockBits(data)
        End Try
        Return CropTransparentMargins(image, pixels, rowBytes)
    End Function

    Private Shared Function IsLightBackgroundPixel(pixels As Byte(), x As Integer, y As Integer, rowBytes As Integer) As Boolean
        Dim offset = y * rowBytes + x * 4
        Dim blue = CInt(pixels(offset))
        Dim green = CInt(pixels(offset + 1))
        Dim red = CInt(pixels(offset + 2))
        Return pixels(offset + 3) > 8 AndAlso Math.Min(red, Math.Min(green, blue)) >= 232 AndAlso
            Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)) <= 28
    End Function

    Private Shared Function CropTransparentMargins(image As Bitmap, pixels As Byte(), rowBytes As Integer) As Bitmap
        Dim left = image.Width, top = image.Height, right = -1, bottom = -1
        For y = 0 To image.Height - 1
            For x = 0 To image.Width - 1
                If pixels(y * rowBytes + x * 4 + 3) <= 8 Then Continue For
                left = Math.Min(left, x)
                top = Math.Min(top, y)
                right = Math.Max(right, x)
                bottom = Math.Max(bottom, y)
            Next
        Next
        If right < left OrElse bottom < top Then Throw New InvalidDataException("No equipment remains after removing the image background.")
        Dim crop As New Rectangle(left, top, right - left + 1, bottom - top + 1)
        If crop.Width = image.Width AndAlso crop.Height = image.Height Then Return DirectCast(image.Clone(), Bitmap)
        Dim result As New Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb)
        Using graphics = Drawing.Graphics.FromImage(result)
            graphics.CompositingMode = Drawing2D.CompositingMode.SourceCopy
            graphics.DrawImage(image, New Rectangle(0, 0, result.Width, result.Height), crop, GraphicsUnit.Pixel)
        End Using
        Return result
    End Function

    Private Shared Sub CheckImageHeader(bytes As Byte())
        Dim width As UInteger
        Dim height As UInteger
        If bytes.Length >= 24 AndAlso bytes.Take(8).SequenceEqual(New Byte() {137, 80, 78, 71, 13, 10, 26, 10}) AndAlso
            Encoding.ASCII.GetString(bytes, 12, 4) = "IHDR" Then
            width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4))
            height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4))
        ElseIf bytes.Length >= 4 AndAlso bytes(0) = &HFF AndAlso bytes(1) = &HD8 Then
            Dim offset = 2
            While offset + 3 < bytes.Length
                If bytes(offset) <> &HFF Then Throw New InvalidDataException("Invalid JPEG image header.")
                While offset < bytes.Length AndAlso bytes(offset) = &HFF
                    offset += 1
                End While
                If offset >= bytes.Length Then Exit While
                Dim marker = bytes(offset)
                offset += 1
                If marker = &HD9 OrElse marker = &HDA Then Exit While
                If marker = 1 OrElse (marker >= &HD0 AndAlso marker <= &HD7) Then Continue While
                If offset + 1 >= bytes.Length Then Exit While
                Dim length = CInt(bytes(offset)) * 256 + bytes(offset + 1)
                If length < 2 OrElse offset + length > bytes.Length Then Throw New InvalidDataException("Truncated JPEG image header.")
                If {&HC0, &HC1, &HC2, &HC3, &HC5, &HC6, &HC7, &HC9, &HCA, &HCB, &HCD, &HCE, &HCF}.Contains(CInt(marker)) Then
                    If length < 8 Then Throw New InvalidDataException("Invalid JPEG image size.")
                    height = CUInt(CInt(bytes(offset + 3)) * 256 + bytes(offset + 4))
                    width = CUInt(CInt(bytes(offset + 5)) * 256 + bytes(offset + 6))
                    Exit While
                End If
                offset += length
            End While
        Else
            Throw New InvalidDataException("Choose a valid PNG or JPG image.")
        End If
        If width = 0 OrElse height = 0 OrElse width > 8192 OrElse height > 8192 OrElse CLng(width) * height > 16000000 Then
            Throw New InvalidDataException("Image dimensions must be at most 8192 per side and 16 million pixels.")
        End If
    End Sub

    Private Shared Function ImageBytes(definition As FactoryMapCustomEquipment) As Byte()
        If definition Is Nothing OrElse definition.PngBase64 Is Nothing OrElse definition.PngBase64.Length > 4 * ((MaxImageBytes + 2) \ 3) Then
            Throw New InvalidDataException("The custom equipment image is missing or exceeds 2 MB.")
        End If
        Dim bytes As Byte()
        Try
            bytes = Convert.FromBase64String(definition.PngBase64)
        Catch ex As FormatException
            Throw New InvalidDataException("The custom equipment image is not valid base64.", ex)
        End Try
        If bytes.Length < 24 OrElse bytes.Length > MaxImageBytes OrElse
            Not bytes.Take(8).SequenceEqual(New Byte() {137, 80, 78, 71, 13, 10, 26, 10}) OrElse
            Encoding.ASCII.GetString(bytes, 12, 4) <> "IHDR" Then Throw New InvalidDataException("The custom equipment needs a normalized PNG image.")
        Dim width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4))
        Dim height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4))
        If width = 0 OrElse height = 0 OrElse width > 1024 OrElse height > 1024 Then Throw New InvalidDataException("Stored custom images must be at most 1024 pixels per side.")
        If definition.ImageHash <> Convert.ToHexString(SHA256.HashData(bytes)) Then Throw New InvalidDataException("The custom equipment image checksum does not match.")
        Return bytes
    End Function

    Public Shared Function DecodeImage(definition As FactoryMapCustomEquipment) As Bitmap
        Dim bytes = ImageBytes(definition)
        Try
            Using stream As New MemoryStream(bytes, False), source = Image.FromStream(stream, False, True)
                Return New Bitmap(source)
            End Using
        Catch ex As ArgumentException
            Throw New InvalidDataException("The stored equipment image is damaged.", ex)
        Catch ex As System.Runtime.InteropServices.ExternalException
            Throw New InvalidDataException("The stored equipment image could not be decoded.", ex)
        End Try
    End Function

    Public Shared Sub Validate(definition As FactoryMapCustomEquipment)
        Dim id As Guid
        If definition Is Nothing OrElse Not Guid.TryParseExact(definition.Id, "N", id) Then Throw New InvalidDataException("Custom equipment needs a valid library ID.")
        If definition.GeometryModel <> "" AndAlso definition.GeometryModel <> SidelModelName AndAlso definition.GeometryModel <> CadModelName Then
            Throw New InvalidDataException("Unsupported equipment geometry model.")
        End If
        If String.IsNullOrWhiteSpace(definition.Name) OrElse definition.Name.Length > 120 OrElse definition.Name.Any(Function(c) Char.IsControl(c)) Then
            Throw New InvalidDataException("Enter a custom equipment name of 1-120 characters.")
        End If
        If {definition.LengthMeters, definition.WidthMeters}.Any(Function(value) Not Single.IsFinite(value) OrElse value < 0.05F OrElse value > 100) OrElse
            Not Single.IsFinite(definition.HeightMeters) OrElse definition.HeightMeters > 100 OrElse
            (definition.HeightMeters < 0.05F AndAlso Not (definition.GeometryModel = CadModelName AndAlso definition.CadGeometry IsNot Nothing AndAlso
                                                       definition.CadGeometry.IsPlan AndAlso definition.HeightMeters = 0)) Then
            Throw New InvalidDataException("Custom equipment length, width and height must each be 0.05-100 metres.")
        End If
        If definition.GeometryModel = CadModelName Then
            FactoryMapCadImporter.Validate(definition)
        Else
            Using image = DecodeImage(definition)
            End Using
        End If
    End Sub

    Public Shared Function Load(Optional directory As String = Nothing) As List(Of FactoryMapCustomEquipment)
        Dim folder = LibraryPath(directory)
        If Not IO.Directory.Exists(folder) Then Return New List(Of FactoryMapCustomEquipment)
        Dim paths = IO.Directory.EnumerateFiles(folder, "*.json").Take(101).ToArray()
        If paths.Length > 100 Then Throw New InvalidDataException("The custom equipment library supports up to 100 definitions.")
        Dim result As New List(Of FactoryMapCustomEquipment)
        For Each path In paths
            If New FileInfo(path).Length > 3 * 1024 * 1024 Then Throw New InvalidDataException("Equipment library file exceeds 3 MB: " & IO.Path.GetFileName(path))
            Dim item = JsonSerializer.Deserialize(Of FactoryMapCustomEquipment)(File.ReadAllText(path), Options)
            Validate(item)
            If IO.Path.GetFileNameWithoutExtension(path) <> item.Id OrElse result.Any(Function(existing) existing.Id = item.Id) Then
                Throw New InvalidDataException("Equipment library IDs must be unique and match their filenames.")
            End If
            result.Add(item)
        Next
        Return result.OrderBy(Function(item) item.Name).ToList()
    End Function

    Public Shared Sub Save(definition As FactoryMapCustomEquipment, Optional directory As String = Nothing)
        Validate(definition)
        Dim folder = LibraryPath(directory)
        IO.Directory.CreateDirectory(folder)
        Dim path = IO.Path.Combine(folder, definition.Id & ".json")
        If Not File.Exists(path) AndAlso IO.Directory.EnumerateFiles(folder, "*.json").Take(100).Count() >= 100 Then
            Throw New InvalidDataException("The library is full (100 definitions).")
        End If
        Dim temporary = path & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Dim json = JsonSerializer.Serialize(definition, Options)
        If Encoding.UTF8.GetByteCount(json) > 3 * 1024 * 1024 Then Throw New InvalidDataException("The equipment definition exceeds 3 MB. Simplify the model before importing.")
        Try
            File.WriteAllText(temporary, json, New UTF8Encoding(False))
            If File.Exists(path) Then
                File.Replace(temporary, path, path & ".bak", True)
            Else
                File.Move(temporary, path)
            End If
        Finally
            If File.Exists(temporary) Then File.Delete(temporary)
        End Try
    End Sub

    Public Shared Sub Delete(definition As FactoryMapCustomEquipment, Optional directory As String = Nothing)
        Dim id As Guid
        If definition Is Nothing OrElse Not Guid.TryParseExact(definition.Id, "N", id) Then
            Throw New InvalidDataException("Custom equipment needs a valid library ID.")
        End If
        Dim path = IO.Path.Combine(LibraryPath(directory), definition.Id & ".json")
        If Not File.Exists(path) Then Throw New FileNotFoundException("The uploaded equipment is no longer in the library.", path)
        Dim backup = path & ".bak"
        If File.Exists(backup) Then File.Delete(backup)
        File.Delete(path)
    End Sub

    Public Shared Function CreateAsset(definition As FactoryMapCustomEquipment) As FactoryMapAsset
        Validate(definition)
        Return New FactoryMapAsset With {
            .Name = definition.Name, .Model = ModelName, .Category = "Utilities", .CustomEquipment = definition.Clone(),
            .Source = If(definition.GeometryModel = CadModelName, "Imported model/CAD geometry",
                If(definition.GeometryModel = "", "Custom image symbol", "Hand-built equipment model")) & " - no linked acquisition source",
            .Location = "User placed"
        }
    End Function
End Class
