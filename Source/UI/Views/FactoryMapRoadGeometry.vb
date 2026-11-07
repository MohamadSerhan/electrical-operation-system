Imports System.Drawing

Friend NotInheritable Class FactoryMapRoadGeometry
    Public Shared Function IsCurve(part As FactoryBuildingPart) As Boolean
        Return part.Kind = "Road" AndAlso {"Curved dashed left", "Curved dashed right"}.Contains(part.Style)
    End Function

    Public Shared Function PointAt(part As FactoryBuildingPart, angle As Double, offset As Single) As PointF
        Dim direction = If(part.Style = "Curved dashed left", -1, 1)
        Dim radius = part.Length - direction * offset
        Return New PointF(CSng(radius * Math.Sin(angle)),
                          CSng(direction * (part.Length - radius * Math.Cos(angle))))
    End Function

    Public Shared Function Strip(part As FactoryBuildingPart, startAngle As Double, endAngle As Double,
                                 firstOffset As Single, lastOffset As Single) As PointF()
        Dim count = Math.Max(2, CInt(Math.Ceiling(Math.Abs(endAngle - startAngle) * 180 / Math.PI)))
        Dim points As New List(Of PointF)
        For index = 0 To count
            points.Add(PointAt(part, startAngle + (endAngle - startAngle) * index / count, firstOffset))
        Next
        For index = count To 0 Step -1
            points.Add(PointAt(part, startAngle + (endAngle - startAngle) * index / count, lastOffset))
        Next
        Return points.ToArray()
    End Function
End Class
