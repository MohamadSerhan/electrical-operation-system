Imports System.IO
Imports System.Text
Imports System.Text.Json

Public NotInheritable Class FactoryMapComponentDimension
    Public Property Name As String = String.Empty
    Public Property LengthMeters As Double
    Public Property WidthMeters As Double
    Public Property HeightMeters As Double
End Class

Public NotInheritable Class FactoryMapDimensions
    Public Property Units As String = "meters"
    Public Property Status As String = "UNVERIFIED visualization assumptions"
    Public Property Components As List(Of FactoryMapComponentDimension) = New List(Of FactoryMapComponentDimension)()

    Public Shared Function Defaults() As FactoryMapDimensions
        Return New FactoryMapDimensions With {
            .Components = New List(Of FactoryMapComponentDimension) From {
                New FactoryMapComponentDimension With {.Name = "External Wall", .LengthMeters = 0, .WidthMeters = 0.25R, .HeightMeters = 4.0R},
                New FactoryMapComponentDimension With {.Name = "Internal Wall", .LengthMeters = 0, .WidthMeters = 0.15R, .HeightMeters = 3.0R},
                New FactoryMapComponentDimension With {.Name = "Generator", .LengthMeters = 5.0R, .WidthMeters = 2.0R, .HeightMeters = 2.5R},
                New FactoryMapComponentDimension With {.Name = "Compressor", .LengthMeters = 2.5R, .WidthMeters = 1.5R, .HeightMeters = 1.8R},
                New FactoryMapComponentDimension With {.Name = "Chiller", .LengthMeters = 4.0R, .WidthMeters = 2.0R, .HeightMeters = 2.2R},
                New FactoryMapComponentDimension With {.Name = "UPS Cabinet", .LengthMeters = 1.2R, .WidthMeters = 1.0R, .HeightMeters = 2.0R},
                New FactoryMapComponentDimension With {.Name = "Transformer", .LengthMeters = 2.5R, .WidthMeters = 2.0R, .HeightMeters = 2.5R},
                New FactoryMapComponentDimension With {.Name = "Battery Cabinet", .LengthMeters = 1.0R, .WidthMeters = 0.8R, .HeightMeters = 2.0R}
            }
        }
    End Function

    Public Sub Validate()
        If Not String.Equals(Units, "meters", StringComparison.Ordinal) OrElse
           Not String.Equals(Status, "UNVERIFIED visualization assumptions", StringComparison.Ordinal) Then
            Throw New InvalidDataException("Factory map dimensions must remain explicitly unverified and use meters.")
        End If
        If Components Is Nothing OrElse Components.Count <> 8 Then
            Throw New InvalidDataException("Factory map dimensions must contain the eight supported component types.")
        End If
        Dim defaults = FactoryMapDimensions.Defaults().Components
        For Each expected In defaults
            Dim actual = Components.Find(Function(item) item IsNot Nothing AndAlso
                String.Equals(item.Name, expected.Name, StringComparison.OrdinalIgnoreCase))
            If actual Is Nothing OrElse Not Double.IsFinite(actual.LengthMeters) OrElse
               Not Double.IsFinite(actual.WidthMeters) OrElse Not Double.IsFinite(actual.HeightMeters) OrElse
               actual.LengthMeters < 0 OrElse actual.LengthMeters > 100 OrElse
               actual.WidthMeters <= 0 OrElse actual.WidthMeters > 100 OrElse
               actual.HeightMeters <= 0 OrElse actual.HeightMeters > 100 OrElse
               (expected.LengthMeters > 0 AndAlso actual.LengthMeters = 0) Then
                Throw New InvalidDataException("Invalid provisional dimensions for " & expected.Name & ".")
            End If
        Next
    End Sub
End Class

Friend NotInheritable Class FactoryMapDimensionStore
    Private Shared ReadOnly Options As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True,
        .WriteIndented = True
    }

    Private Sub New()
    End Sub

    Private Shared Function SettingsPath(Optional path As String = Nothing) As String
        Return If(String.IsNullOrWhiteSpace(path),
            IO.Path.Combine(AppContext.BaseDirectory, "data", "factory-map-dimensions.json"),
            IO.Path.GetFullPath(path))
    End Function

    Public Shared Function Load(Optional path As String = Nothing) As FactoryMapDimensions
        Dim resolvedPath = SettingsPath(path)
        If Not File.Exists(resolvedPath) Then Return FactoryMapDimensions.Defaults()
        Dim dimensions = JsonSerializer.Deserialize(Of FactoryMapDimensions)(File.ReadAllText(resolvedPath), Options)
        If dimensions Is Nothing Then Throw New InvalidDataException("Saved provisional factory map dimensions are empty.")
        dimensions.Validate()
        Return dimensions
    End Function

    Public Shared Sub Save(dimensions As FactoryMapDimensions, Optional path As String = Nothing)
        If dimensions Is Nothing Then Throw New ArgumentNullException(NameOf(dimensions))
        dimensions.Validate()
        Dim resolvedPath = SettingsPath(path)
        IO.Directory.CreateDirectory(IO.Path.GetDirectoryName(resolvedPath))
        Dim temporary = resolvedPath & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Try
            File.WriteAllText(temporary, JsonSerializer.Serialize(dimensions, Options), New UTF8Encoding(False))
            File.Move(temporary, resolvedPath, True)
        Finally
            If File.Exists(temporary) Then File.Delete(temporary)
        End Try
    End Sub
End Class
