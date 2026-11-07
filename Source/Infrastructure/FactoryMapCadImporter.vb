Imports System.Globalization
Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports ACadSharp
Imports ACadSharp.Entities
Imports ACadSharp.IO
Imports CSMath

Friend NotInheritable Class FactoryMapCadPoint
    Public Property X As Double
    Public Property Y As Double
    Public Property Z As Double
End Class

Friend NotInheritable Class FactoryMapCadGeometry
    Public Property Vertices As New List(Of FactoryMapCadPoint)
    Public Property Faces As New List(Of Integer())
    Public Property Paths As New List(Of Integer())
    Public Property IsPlan As Boolean

    Public Function Clone() As FactoryMapCadGeometry
        Return New FactoryMapCadGeometry With {
            .Vertices = Vertices.Select(Function(p) New FactoryMapCadPoint With {.X = p.X, .Y = p.Y, .Z = p.Z}).ToList(),
            .Faces = Faces.Select(Function(f) f.ToArray()).ToList(), .Paths = Paths.Select(Function(f) f.ToArray()).ToList(), .IsPlan = IsPlan
        }
    End Function
End Class

Friend NotInheritable Class FactoryMapCadImporter
    Private Const MaxVertices As Integer = 30000
    Private Const MaxElements As Integer = 15000
    Private ReadOnly _mesh As New FactoryMapCadGeometry
    Private _entities As Integer
    Private _annotations As Integer
    Private ReadOnly _styleWarnings As New HashSet(Of String)

    Public Shared Function ImportFile(path As String, yUp As Boolean) As FactoryMapCustomEquipment
        Dim importer As New FactoryMapCadImporter
        Dim extension = System.IO.Path.GetExtension(path).ToLowerInvariant()
        If Not {".obj", ".dxf", ".dwg"}.Contains(extension) Then Throw New InvalidDataException("Choose an OBJ, DXF or DWG file, not an image.")
        Using stream = File.OpenRead(path)
            If stream.Length = 0 OrElse stream.Length > 20 * 1024 * 1024 Then Throw New InvalidDataException("Model files must be nonempty and at most 20 MB.")
            Try
                If extension = ".obj" Then
                    importer.ReadObj(stream)
                Else
                    Dim notices As New List(Of String)
                    Dim notification As NotificationEventHandler =
                        Sub(sender, args)
                            If args.NotificationType = NotificationType.Warning AndAlso args.Message.Contains("ACadSharp.Tables.TextStyle table reference", StringComparison.Ordinal) Then
                                importer._styleWarnings.Add(args.Message)
                            ElseIf args.NotificationType <> NotificationType.None Then
                                notices.Add(args.Message)
                            End If
                        End Sub
                    Dim document = If(extension = ".dwg", DwgReader.Read(stream, notification), DxfReader.Read(stream, notification))
                    If notices.Count > 0 Then Throw New InvalidDataException("CAD reader reported an incomplete or unsupported drawing: " & String.Join("; ", notices.Take(3)))
                    For Each entity In document.Entities
                        importer.ReadEntity(entity, 0)
                    Next
                End If
            Catch ex As Exception When TypeOf ex Is FormatException OrElse TypeOf ex Is ArgumentException OrElse
                                       TypeOf ex Is NotSupportedException OrElse TypeOf ex Is EndOfStreamException OrElse TypeOf ex Is OverflowException
                Throw New InvalidDataException("The model file is damaged or uses unsupported geometry: " & ex.Message, ex)
            Catch ex As ACadSharp.Exceptions.DwgException
                Throw New InvalidDataException("DWG could not be read: " & ex.Message, ex)
            Catch ex As ACadSharp.Exceptions.DxfException
                Throw New InvalidDataException("DXF could not be read: " & ex.Message, ex)
            End Try
        End Using
        Return importer.Normalize(path, yUp)
    End Function

    Private Sub CheckLimits()
        If _mesh.Vertices.Count > MaxVertices OrElse _mesh.Faces.Count + _mesh.Paths.Count > MaxElements OrElse _entities > MaxElements Then
            Throw New InvalidDataException("Model exceeds 30,000 vertices or 15,000 faces/paths/entities. Export a simplified equipment model.")
        End If
    End Sub

    Friend Shared Function FromGeometry(name As String, geometry As FactoryMapCadGeometry) As FactoryMapCustomEquipment
        Dim importer As New FactoryMapCadImporter
        importer._mesh.Vertices.AddRange(geometry.Vertices)
        importer._mesh.Faces.AddRange(geometry.Faces)
        importer._mesh.Paths.AddRange(geometry.Paths)
        Dim length = geometry.Vertices.Max(Function(p) p.X) - geometry.Vertices.Min(Function(p) p.X)
        Dim width = geometry.Vertices.Max(Function(p) p.Y) - geometry.Vertices.Min(Function(p) p.Y)
        Dim height = geometry.Vertices.Max(Function(p) p.Z) - geometry.Vertices.Min(Function(p) p.Z)
        Dim result = importer.Normalize(name, False)
        result.LengthMeters = CSng(length / 8)
        result.WidthMeters = CSng(width / 8)
        result.HeightMeters = CSng(height / 8)
        FactoryMapCustomEquipmentStore.Validate(result)
        Return result
    End Function

    Private Function AddPoint(point As XYZ) As Integer
        If Not Double.IsFinite(point.X) OrElse Not Double.IsFinite(point.Y) OrElse Not Double.IsFinite(point.Z) OrElse
            Math.Max(Math.Abs(point.X), Math.Max(Math.Abs(point.Y), Math.Abs(point.Z))) > 100000000 Then
            Throw New InvalidDataException("Model coordinates must be finite and within 100,000,000 source units.")
        End If
        _mesh.Vertices.Add(New FactoryMapCadPoint With {.X = point.X, .Y = point.Y, .Z = point.Z})
        CheckLimits()
        Return _mesh.Vertices.Count - 1
    End Function

    Private Sub AddPath(points As IEnumerable(Of XYZ), closed As Boolean)
        Dim indices = points.Select(Function(p) AddPoint(p)).ToList()
        If indices.Count < 2 Then Throw New InvalidDataException("CAD curve has fewer than two points.")
        If closed Then indices.Add(indices(0))
        _mesh.Paths.Add(indices.ToArray())
        CheckLimits()
    End Sub

    Private Sub ReadObj(stream As Stream)
        Using reader As New StreamReader(stream, Encoding.UTF8, True, 4096, True)
            Dim lineNumber As Integer
            While Not reader.EndOfStream
                lineNumber += 1
                Dim line = reader.ReadLine().Split("#"c)(0).Trim()
                If line = "" Then Continue While
                Dim parts = line.Split(CType(Nothing, Char()), StringSplitOptions.RemoveEmptyEntries)
                Select Case parts(0)
                    Case "v"
                        If parts.Length <> 4 Then Throw New InvalidDataException($"OBJ line {lineNumber}: only XYZ vertices are supported.")
                        AddPoint(New XYZ(Double.Parse(parts(1), NumberStyles.Float, CultureInfo.InvariantCulture),
                                         Double.Parse(parts(2), NumberStyles.Float, CultureInfo.InvariantCulture),
                                         Double.Parse(parts(3), NumberStyles.Float, CultureInfo.InvariantCulture)))
                    Case "f", "l"
                        Dim indices As New List(Of Integer)
                        For Each token In parts.Skip(1)
                            Dim index = Integer.Parse(token.Split("/"c)(0), CultureInfo.InvariantCulture)
                            index = If(index < 0, _mesh.Vertices.Count + index, index - 1)
                            If index < 0 OrElse index >= _mesh.Vertices.Count Then Throw New InvalidDataException($"OBJ line {lineNumber}: invalid vertex index.")
                            indices.Add(index)
                        Next
                        If indices.Count < If(parts(0) = "f", 3, 2) OrElse indices.Count > 256 Then
                            Throw New InvalidDataException($"OBJ line {lineNumber}: invalid or excessive face/path size.")
                        End If
                        If parts(0) = "f" Then
                            _mesh.Faces.Add(indices.ToArray())
                        Else
                            _mesh.Paths.Add(indices.ToArray())
                        End If
                    Case "o", "g", "s", "vn", "vt", "mtllib", "usemtl"
                        ' Geometry is embedded locally; material/texture files are never opened.
                    Case Else
                        Throw New InvalidDataException($"OBJ line {lineNumber}: unsupported statement '{parts(0)}'. Export polygon faces.")
                End Select
                CheckLimits()
            End While
        End Using
    End Sub

    Private Sub ReadEntity(entity As Entity, depth As Integer)
        _entities += 1
        CheckLimits()
        If depth > 16 Then Throw New InvalidDataException("CAD block nesting exceeds 16 levels.")
        If entity.IsInvisible Then Return
        Select Case True
            Case TypeOf entity Is Insert
                For Each child In DirectCast(entity, Insert).Explode()
                    ReadEntity(child, depth + 1)
                Next
            Case TypeOf entity Is Face3D
                Dim face = DirectCast(entity, Face3D)
                Dim points = {face.FirstCorner, face.SecondCorner, face.ThirdCorner, face.FourthCorner}.Distinct().ToArray()
                If points.Length < 3 Then Throw New InvalidDataException("CAD contains a degenerate 3D face.")
                _mesh.Faces.Add(points.Select(Function(p) AddPoint(p)).ToArray())
            Case TypeOf entity Is Mesh
                Dim mesh = DirectCast(entity, Mesh)
                Dim offset = _mesh.Vertices.Count
                For Each point In mesh.Vertices
                    AddPoint(point)
                Next
                For Each face In mesh.Faces
                    If face.Any(Function(i) i < 0 OrElse i >= mesh.Vertices.Count) Then Throw New InvalidDataException("CAD mesh contains an invalid vertex index.")
                    _mesh.Faces.Add(face.Select(Function(i) i + offset).ToArray())
                Next
            Case TypeOf entity Is PolyfaceMesh
                Dim mesh = DirectCast(entity, PolyfaceMesh)
                Dim vertices = mesh.Vertices.Select(Function(v) v.Location).ToArray()
                For Each face In mesh.Faces
                    Dim indices = {CInt(face.Index1), CInt(face.Index2), CInt(face.Index3), CInt(face.Index4)}.Where(Function(i) i <> 0).Select(Function(i) Math.Abs(i) - 1).ToArray()
                    If indices.Any(Function(i) i >= vertices.Length) Then Throw New InvalidDataException("CAD polyface contains an invalid vertex index.")
                    _mesh.Faces.Add(indices.Select(Function(i) AddPoint(vertices(i))).ToArray())
                Next
            Case TypeOf entity Is ACadSharp.Entities.Line
                Dim line = DirectCast(entity, ACadSharp.Entities.Line)
                AddPath({line.StartPoint, line.EndPoint}, False)
            Case TypeOf entity Is Arc
                AddPath(DirectCast(entity, Arc).PolygonalVertexes(96), False)
            Case TypeOf entity Is Circle
                AddPath(DirectCast(entity, Circle).PolygonalVertexes(96), True)
            Case TypeOf entity Is LwPolyline
                For Each segment In ACadSharp.Extensions.PolylineExtensions.Explode(DirectCast(entity, LwPolyline))
                    ReadEntity(segment, depth)
                Next
            Case TypeOf entity Is Polyline3D
                Dim poly = DirectCast(entity, Polyline3D)
                AddPath(poly.Vertices.Select(Function(v) v.Location), poly.IsClosed)
            Case TypeOf entity Is Polyline2D
                For Each segment In ACadSharp.Extensions.PolylineExtensions.Explode(DirectCast(entity, Polyline2D))
                    ReadEntity(segment, depth)
                Next
            Case Else
                If {"TEXT", "MTEXT", "DIMENSION", "ATTDEF", "ATTRIB"}.Contains(entity.ObjectName) Then
                    _annotations += 1
                Else
                    Throw New InvalidDataException("Unsupported CAD entity: " & entity.ObjectName & ". Export solids/surfaces as 3DFACE, polygon meshes or OBJ. No partial model was imported.")
                End If
        End Select
        CheckLimits()
    End Sub

    Private Function Normalize(path As String, yUp As Boolean) As FactoryMapCustomEquipment
        If _mesh.Vertices.Count < 2 OrElse _mesh.Faces.Count + _mesh.Paths.Count = 0 Then Throw New InvalidDataException("The file has no supported equipment geometry.")
        If yUp Then
            For Each point In _mesh.Vertices
                Dim oldY = point.Y
                point.Y = -point.Z
                point.Z = oldY
            Next
        End If
        Dim minX = _mesh.Vertices.Min(Function(p) p.X), maxX = _mesh.Vertices.Max(Function(p) p.X)
        Dim minY = _mesh.Vertices.Min(Function(p) p.Y), maxY = _mesh.Vertices.Max(Function(p) p.Y)
        Dim minZ = _mesh.Vertices.Min(Function(p) p.Z), maxZ = _mesh.Vertices.Max(Function(p) p.Z)
        Dim dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ
        If dx <= 0 OrElse dy <= 0 Then Throw New InvalidDataException("Equipment must have nonzero length and width. Choose the correct up axis.")
        _mesh.IsPlan = dz <= Math.Max(dx, dy) * 0.000001F
        For Each point In _mesh.Vertices
            point.X = (point.X - minX) / dx - 0.5F
            point.Y = (point.Y - minY) / dy - 0.5F
            point.Z = If(_mesh.IsPlan, 0, (point.Z - minZ) / dz)
        Next
        Dim size = Math.Max(dx, Math.Max(dy, dz))
        Dim result As New FactoryMapCustomEquipment With {
            .Name = System.IO.Path.GetFileNameWithoutExtension(path), .GeometryModel = FactoryMapCustomEquipmentStore.CadModelName,
            .CadGeometry = _mesh, .LengthMeters = Math.Max(0.05F, 2 * dx / size), .WidthMeters = Math.Max(0.05F, 2 * dy / size),
            .HeightMeters = If(_mesh.IsPlan, 0, Math.Max(0.05F, 2 * dz / size)),
            .ImportNotes = $"Imported {_mesh.Faces.Count} faces and {_mesh.Paths.Count} paths. Set actual metre dimensions; file units are not assumed. Materials/textures are not imported. {_annotations} CAD annotation entities excluded." &
                If(_styleWarnings.Count > 0, $" Warning: {_styleWarnings.Count} missing CAD text-style references; text is not rendered.", "")
        }
        result.GeometryHash = Hash(_mesh)
        If JsonSerializer.SerializeToUtf8Bytes(_mesh, New JsonSerializerOptions With {.WriteIndented = True}).Length > 2 * 1024 * 1024 Then
            Throw New InvalidDataException("Embedded geometry exceeds 2 MB. Export a simplified model.")
        End If
        FactoryMapCustomEquipmentStore.Validate(result)
        Return result
    End Function

    Private Shared Function Hash(mesh As FactoryMapCadGeometry) As String
        Return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(mesh)))
    End Function

    Public Shared Sub Validate(definition As FactoryMapCustomEquipment)
        Dim mesh = definition.CadGeometry
        If mesh Is Nothing OrElse mesh.Vertices Is Nothing OrElse mesh.Faces Is Nothing OrElse mesh.Paths Is Nothing OrElse
            mesh.Vertices.Count < 2 OrElse mesh.Vertices.Count > MaxVertices OrElse mesh.Faces.Count + mesh.Paths.Count = 0 OrElse
            mesh.Faces.Count + mesh.Paths.Count > MaxElements Then Throw New InvalidDataException("Imported geometry is missing or exceeds supported limits.")
        If mesh.Vertices.Any(Function(p) p Is Nothing OrElse Not Double.IsFinite(p.X) OrElse Not Double.IsFinite(p.Y) OrElse Not Double.IsFinite(p.Z) OrElse
                               Math.Abs(p.X) > 0.501F OrElse Math.Abs(p.Y) > 0.501F OrElse p.Z < 0 OrElse p.Z > 1 OrElse (mesh.IsPlan AndAlso p.Z <> 0)) Then
            Throw New InvalidDataException("Imported geometry has invalid normalized coordinates.")
        End If
        For Each face In mesh.Faces
            ValidateIndices(face, mesh.Vertices.Count, 3)
        Next
        For Each path In mesh.Paths
            ValidateIndices(path, mesh.Vertices.Count, 2)
        Next
        If mesh.IsPlan AndAlso definition.HeightMeters <> 0 Then Throw New InvalidDataException("2D CAD outlines must retain zero height; no 3D extrusion is inferred.")
        If definition.GeometryHash <> Hash(mesh) Then Throw New InvalidDataException("Imported geometry checksum does not match.")
    End Sub

    Private Shared Sub ValidateIndices(indices As Integer(), count As Integer, minimum As Integer)
        If indices Is Nothing OrElse indices.Length < minimum OrElse indices.Length > 30000 OrElse
            indices.Any(Function(i) i < 0 OrElse i >= count) Then Throw New InvalidDataException("Imported geometry has invalid face/path indices.")
    End Sub
End Class
