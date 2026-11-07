Imports System.IO
Imports System.Text.Json

Public NotInheritable Class FirePanel
    Public Sub New(id As String, name As String, service As FireConnectionService, isPrimary As Boolean)
        Me.Id = id
        Me.Name = name
        Me.Service = service
        Me.IsPrimary = isPrimary
    End Sub

    Public ReadOnly Property Id As String
    Public Property Name As String
    Public ReadOnly Property Service As FireConnectionService
    Public ReadOnly Property IsPrimary As Boolean
End Class

Friend NotInheritable Class FirePanelRegistryEntry
    Public Property Id As String = String.Empty
    Public Property Name As String = String.Empty
End Class

Friend NotInheritable Class FirePanelRegistryDocument
    Public Property PrimaryName As String = FirePanelManager.DefaultPrimaryName
    Public Property Panels As List(Of FirePanelRegistryEntry) = New List(Of FirePanelRegistryEntry)
End Class

' Keeps the list of fire panels. The primary panel uses the existing service and settings file; every additional panel
' has its own settings file and service so credentials, connection state and monitoring stay independent.
Public NotInheritable Class FirePanelManager
    Implements IDisposable

    Public Const DefaultPrimaryName As String = "Fire Panel 1"
    Public Const MaximumNameLength As Integer = 60
    Private Shared ReadOnly Options As New JsonSerializerOptions With {.WriteIndented = True}
    Private ReadOnly _panels As New List(Of FirePanel)
    Private ReadOnly _primaryStorePath As String
    Private ReadOnly _registryPath As String
    Private _communication As Boolean
    Private _disposed As Boolean

    Public Event Changed As EventHandler

    Public Sub New(primary As FireConnectionService)
        If primary Is Nothing Then Throw New ArgumentNullException(NameOf(primary))
        _primaryStorePath = primary.StoreFilePath
        _registryPath = Path.Combine(Path.GetDirectoryName(_primaryStorePath), "fire-panels.json")
        Dim document = LoadDocument()
        _panels.Add(New FirePanel("1", If(String.IsNullOrWhiteSpace(document.PrimaryName), DefaultPrimaryName, document.PrimaryName), primary, True))
        For Each entry In document.Panels
            If String.IsNullOrWhiteSpace(entry.Id) OrElse entry.Id = "1" OrElse _panels.Any(Function(item) item.Id = entry.Id) Then Continue For
            _panels.Add(New FirePanel(entry.Id, entry.Name, New FireConnectionService(New FireConnectionStore(StorePath(entry.Id)), False), False))
        Next
    End Sub

    Public ReadOnly Property Panels As IReadOnlyList(Of FirePanel)
        Get
            Return _panels.ToArray()
        End Get
    End Property

    Public Async Function StartAsync() As Task
        _communication = True
        For Each panel In _panels.Where(Function(item) Not item.IsPrimary).ToArray()
            Await panel.Service.StartAsync(True).ConfigureAwait(False)
        Next
    End Function

    Public Async Function StopAsync() As Task
        _communication = False
        For Each panel In _panels.Where(Function(item) Not item.IsPrimary).ToArray()
            Await panel.Service.StopAsync().ConfigureAwait(False)
        Next
    End Function

    Public Function AddPanel(name As String, session As UserSession) As FirePanel
        FireConnectionStore.RequireAdministrator(session)
        Dim id = 2
        While _panels.Any(Function(item) item.Id = id.ToString(Globalization.CultureInfo.InvariantCulture)) OrElse File.Exists(StorePath(id.ToString(Globalization.CultureInfo.InvariantCulture))) OrElse
            File.Exists(StorePath(id.ToString(Globalization.CultureInfo.InvariantCulture)) & ".cloud-events.db")
            id += 1
        End While
        Dim key = id.ToString(Globalization.CultureInfo.InvariantCulture)
        Dim cleaned = CleanName(name, "Fire Panel " & key)
        Dim panel As New FirePanel(key, cleaned, New FireConnectionService(New FireConnectionStore(StorePath(key)), False), False)
        _panels.Add(panel)
        Save()
        If _communication Then Call panel.Service.StartAsync(True)
        RaiseEvent Changed(Me, EventArgs.Empty)
        Return panel
    End Function

    Public Sub Rename(panel As FirePanel, name As String, session As UserSession)
        FireConnectionStore.RequireAdministrator(session)
        If panel Is Nothing OrElse Not _panels.Contains(panel) Then Return
        panel.Name = CleanName(name, panel.Name)
        Save()
        RaiseEvent Changed(Me, EventArgs.Empty)
    End Sub

    Public Async Function RemoveAsync(panel As FirePanel, session As UserSession) As Task
        FireConnectionStore.RequireAdministrator(session)
        If panel Is Nothing OrElse panel.IsPrimary OrElse Not _panels.Contains(panel) Then
            Throw New InvalidOperationException("Fire Panel 1 cannot be removed.")
        End If
        Await panel.Service.StopAsync().ConfigureAwait(True)
        _panels.Remove(panel)
        Save()
        panel.Service.Dispose()
        For Each suffix In {String.Empty, ".events.jsonl", ".events.jsonl.previous"}
            Try
                File.Delete(StorePath(panel.Id) & suffix)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
        Next
        RaiseEvent Changed(Me, EventArgs.Empty)
    End Function

    Private Function StorePath(id As String) As String
        Return Path.Combine(Path.GetDirectoryName(_primaryStorePath), "fire-connection-" & id & ".json")
    End Function

    Private Shared Function CleanName(name As String, fallback As String) As String
        Dim value = If(name, String.Empty).Trim()
        If value.Length = 0 Then Return fallback
        Return If(value.Length > MaximumNameLength, value.Substring(0, MaximumNameLength), value)
    End Function

    Private Function LoadDocument() As FirePanelRegistryDocument
        Try
            If File.Exists(_registryPath) Then
                Dim document = JsonSerializer.Deserialize(Of FirePanelRegistryDocument)(File.ReadAllText(_registryPath), Options)
                If document IsNot Nothing AndAlso document.Panels IsNot Nothing Then Return document
            End If
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is UnauthorizedAccessException
        End Try
        Return New FirePanelRegistryDocument
    End Function

    Private Sub Save()
        Dim document As New FirePanelRegistryDocument With {.PrimaryName = _panels(0).Name}
        For Each panel In _panels.Skip(1)
            document.Panels.Add(New FirePanelRegistryEntry With {.Id = panel.Id, .Name = panel.Name})
        Next
        Directory.CreateDirectory(Path.GetDirectoryName(_registryPath))
        Dim temporary = _registryPath & ".tmp"
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, Options))
        File.Move(temporary, _registryPath, True)
    End Sub

    ' The primary service is owned by the caller and is not disposed here.
    Public Sub Dispose() Implements IDisposable.Dispose
        If _disposed Then Return
        _disposed = True
        For Each panel In _panels.Where(Function(item) Not item.IsPrimary)
            panel.Service.Dispose()
        Next
    End Sub
End Class
