Imports System.Drawing
Imports System.IO
Imports System.Text
Imports System.Text.Json

Friend NotInheritable Class FactoryMapSiteSettings
    Public Property WidthPercent As Integer = 100
    Public Property DepthPercent As Integer = 100
    Public Property GroundColor As String = "#E5E8E3"

    Public Sub Validate()
        If WidthPercent < 100 OrElse WidthPercent > 400 OrElse DepthPercent < 100 OrElse DepthPercent > 400 OrElse
            Not FactoryMapBuildingStore.ValidColor(GroundColor) Then Throw New InvalidDataException("Site size must be 100-400 percent with a valid ground color.")
    End Sub
End Class

Friend NotInheritable Class FactoryMapSiteStore
    Private Sub New()
    End Sub

    Public Shared Function Load(Optional path As String = Nothing) As FactoryMapSiteSettings
        Dim resolved = StorePath(path)
        If Not File.Exists(resolved) Then Return New FactoryMapSiteSettings()
        Dim settings = JsonSerializer.Deserialize(Of FactoryMapSiteSettings)(File.ReadAllText(resolved))
        If settings Is Nothing Then Throw New InvalidDataException("Site settings are empty.")
        settings.Validate()
        Return settings
    End Function

    Public Shared Sub Save(settings As FactoryMapSiteSettings, Optional path As String = Nothing)
        settings.Validate()
        Dim resolved = StorePath(path)
        Directory.CreateDirectory(IO.Path.GetDirectoryName(resolved))
        Dim temporary = resolved & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Try
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings), New UTF8Encoding(False))
            If File.Exists(resolved) Then File.Copy(resolved, resolved & ".bak", True)
            File.Move(temporary, resolved, True)
        Finally
            If File.Exists(temporary) Then File.Delete(temporary)
        End Try
    End Sub

    Private Shared Function StorePath(path As String) As String
        Return If(String.IsNullOrWhiteSpace(path), IO.Path.Combine(AppContext.BaseDirectory, "data", "factory-map-site.json"), IO.Path.GetFullPath(path))
    End Function
End Class
