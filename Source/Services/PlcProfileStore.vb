Imports System.IO
Imports System.Text.Json

Public NotInheritable Class PlcProfile
    Public Property Id As String = String.Empty
    Public Property Name As String = String.Empty
    Public Property Settings As PlcSettings = New PlcSettings
    Public Property IsPrimary As Boolean
End Class

Friend NotInheritable Class PlcProfileDocument
    Public Property PrimaryName As String = PlcProfileStore.DefaultPrimaryName
    Public Property Profiles As List(Of PlcProfile) = New List(Of PlcProfile)
End Class

' Names the PLCs shown on the PLC system page. PLC 1 is the live PLC and its settings stay in the main application settings;
' additional PLCs are stored here as configuration only until multi-PLC acquisition is implemented.
Public NotInheritable Class PlcProfileStore
    Public Const DefaultPrimaryName As String = "PLC 1"
    Public Const MaximumNameLength As Integer = 60
    Private Shared ReadOnly Options As New JsonSerializerOptions With {.WriteIndented = True}
    Private ReadOnly _path As String
    Private ReadOnly _profiles As New List(Of PlcProfile)

    Public Sub New(Optional path As String = Nothing)
        _path = System.IO.Path.GetFullPath(If(path, System.IO.Path.Combine(AppContext.BaseDirectory, "EOEMSData", "Configuration", "plc-profiles.json")))
        Dim document As New PlcProfileDocument
        Try
            If File.Exists(_path) Then
                document = If(JsonSerializer.Deserialize(Of PlcProfileDocument)(File.ReadAllText(_path), Options), document)
            End If
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is UnauthorizedAccessException
            document = New PlcProfileDocument
        End Try
        _profiles.Add(New PlcProfile With {.Id = "1", .IsPrimary = True,
            .Name = If(String.IsNullOrWhiteSpace(document.PrimaryName), DefaultPrimaryName, document.PrimaryName)})
        For Each profile In If(document.Profiles, New List(Of PlcProfile))
            If profile Is Nothing OrElse String.IsNullOrWhiteSpace(profile.Id) OrElse profile.Id = "1" OrElse
               _profiles.Any(Function(item) item.Id = profile.Id) Then Continue For
            profile.IsPrimary = False
            profile.Settings = If(profile.Settings, New PlcSettings)
            profile.Settings.Normalize()
            _profiles.Add(profile)
        Next
    End Sub

    Public ReadOnly Property Profiles As IReadOnlyList(Of PlcProfile)
        Get
            Return _profiles.ToArray()
        End Get
    End Property

    Public Function Add(name As String, template As PlcSettings) As PlcProfile
        Dim number = 2
        While _profiles.Any(Function(item) item.Id = number.ToString(Globalization.CultureInfo.InvariantCulture))
            number += 1
        End While
        Dim key = number.ToString(Globalization.CultureInfo.InvariantCulture)
        Dim settings = If(template, New PlcSettings).Clone()
        settings.Host = String.Empty
        Dim profile As New PlcProfile With {.Id = key, .Name = CleanName(name, "PLC " & key), .Settings = settings}
        _profiles.Add(profile)
        Save()
        Return profile
    End Function

    Public Sub Rename(profile As PlcProfile, name As String)
        If profile Is Nothing OrElse Not _profiles.Contains(profile) Then Return
        profile.Name = CleanName(name, profile.Name)
        Save()
    End Sub

    Public Sub UpdateSettings(profile As PlcProfile, settings As PlcSettings)
        If profile Is Nothing OrElse profile.IsPrimary OrElse Not _profiles.Contains(profile) Then
            Throw New InvalidOperationException("Only additional PLC profiles are stored here.")
        End If
        profile.Settings = settings.Clone()
        Save()
    End Sub

    Public Sub Remove(profile As PlcProfile)
        If profile Is Nothing OrElse profile.IsPrimary Then Throw New InvalidOperationException("PLC 1 cannot be removed.")
        If _profiles.Remove(profile) Then Save()
    End Sub

    Private Shared Function CleanName(name As String, fallback As String) As String
        Dim value = If(name, String.Empty).Trim()
        If value.Length = 0 Then Return fallback
        Return If(value.Length > MaximumNameLength, value.Substring(0, MaximumNameLength), value)
    End Function

    Private Sub Save()
        Dim document As New PlcProfileDocument With {.PrimaryName = _profiles(0).Name}
        document.Profiles.AddRange(_profiles.Skip(1))
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path))
        Dim temporary = _path & ".tmp"
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, Options))
        File.Move(temporary, _path, True)
    End Sub
End Class
