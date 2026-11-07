Imports System.Drawing
Imports System.Drawing.Drawing2D

Friend NotInheritable Class FactoryMapHitGeometry
    Private ReadOnly _points As PointF()
    Private ReadOnly _types As Byte()
    Private ReadOnly _fillMode As FillMode
    Private ReadOnly _bounds As RectangleF

    Public Sub New(path As GraphicsPath)
        Using flattened = DirectCast(path.Clone(), GraphicsPath)
            flattened.Flatten(Nothing, 0.1F)
            _points = flattened.PathPoints
            _types = flattened.PathTypes
            _fillMode = flattened.FillMode
            _bounds = flattened.GetBounds()
            _bounds.Inflate(2.6F, 2.6F)
        End Using
    End Sub

    Public Function Contains(point As Point, Optional outline As Boolean = True) As Boolean
        If Not _bounds.Contains(point) Then Return False
        Dim winding = 0
        Dim start = 0
        For index = 0 To _points.Length - 1
            If (_types(index) And 7) = CInt(PathPointType.Start) Then start = index
            Dim closes = index = _points.Length - 1 OrElse (_types(index) And CInt(PathPointType.CloseSubpath)) <> 0 OrElse
                (_types(index + 1) And 7) = CInt(PathPointType.Start)
            Dim nextIndex = If(closes, start, index + 1)
            Dim a = _points(index), b = _points(nextIndex)
            Dim dx = b.X - a.X, dy = b.Y - a.Y
            Dim cross = dx * (point.Y - a.Y) - dy * (point.X - a.X)
            If a.Y <= point.Y AndAlso b.Y > point.Y AndAlso cross > 0 Then winding += 1
            If a.Y > point.Y AndAlso b.Y <= point.Y AndAlso cross < 0 Then winding -= 1
            If Not outline Then Continue For
            Dim lengthSquared = dx * dx + dy * dy
            Dim t = If(lengthSquared = 0, 0, Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / lengthSquared, 0, 1))
            Dim distanceX = point.X - a.X - t * dx, distanceY = point.Y - a.Y - t * dy
            If distanceX * distanceX + distanceY * distanceY <= 6.25F Then Return True
        Next
        Return If(_fillMode = FillMode.Alternate, Math.Abs(winding) Mod 2 <> 0, winding <> 0)
    End Function
End Class
