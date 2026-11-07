Imports System.IO
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json

Friend NotInheritable Class FireMapAlertEntry
    Public Property Scope As String = ""
    Public Property Key As String = ""
    Public Property Record As ZetaFireEvent
End Class

Friend NotInheritable Class FireMapAlertDocument
    Public Property Version As Integer = 1
    Public Property Seen As New HashSet(Of String)(StringComparer.Ordinal)
    Public Property Pending As New List(Of FireMapAlertEntry)
End Class

Friend NotInheritable Class FireMapAlertStore
    Private ReadOnly _path As String
    Private ReadOnly _gate As New Object
    Private _document As New FireMapAlertDocument
    Private _snapshots As IReadOnlyDictionary(Of String, IReadOnlyList(Of FireMapEventEvidence)) =
        New Dictionary(Of String, IReadOnlyList(Of FireMapEventEvidence))(StringComparer.Ordinal)

    Public Sub New(settingsPath As String)
        _path = settingsPath & ".map-alerts"
        If File.Exists(_path) Then
            Dim document = JsonSerializer.Deserialize(Of FireMapAlertDocument)(FireConnectionStore.Unprotect(File.ReadAllText(_path)))
            If document Is Nothing OrElse document.Version <> 1 OrElse document.Seen Is Nothing OrElse document.Pending Is Nothing OrElse
                document.Pending.Any(Function(entry) entry Is Nothing OrElse entry.Record Is Nothing OrElse String.IsNullOrEmpty(entry.Scope) OrElse
                    entry.Key <> entry.Scope & ":" & FireEventIdentity.Key(entry.Record) OrElse Not document.Seen.Contains(entry.Key)) Then
                Throw New InvalidDataException("Invalid persisted map fire alerts. Restore this file before resetting alerts.")
            End If
            _document = document
        End If
        Publish(_document)
    End Sub

    Friend Shared Function Scope(settings As FireConnectionSettings) As String
        Return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            settings.CloudUrl & vbNullChar & settings.CloudApiEndpoint & vbNullChar & settings.CloudSiteIdentifier)))
    End Function

    Public Sub Observe(settings As FireConnectionSettings, events As IReadOnlyList(Of ZetaFireEvent))
        SyncLock _gate
            Dim source = Scope(settings)
            Dim nextDocument = CopyDocument()
            Dim changed = False
            For Each record In events
                If record.SiteId <> settings.CloudSiteIdentifier OrElse Not record.TimestampUtc.HasValue OrElse record.TimestampUtc.Value > DateTimeOffset.UtcNow Then Continue For
                Dim key = source & ":" & FireEventIdentity.Key(record)
                If Not nextDocument.Seen.Add(key) Then Continue For
                changed = True
                If FireEventIdentity.Signal(record) >= MapSignal.Warning Then
                    nextDocument.Pending.Add(New FireMapAlertEntry With {.Scope = source, .Key = key, .Record = record.Copy()})
                End If
            Next
            If changed Then Save(nextDocument)
        End SyncLock
    End Sub

    Public Function Read(settings As FireConnectionSettings) As IReadOnlyList(Of FireMapEventEvidence)
        Dim snapshots = Threading.Volatile.Read(_snapshots)
        Dim snapshot As IReadOnlyList(Of FireMapEventEvidence) = Nothing
        If snapshots.TryGetValue(Scope(settings), snapshot) Then Return snapshot
        Return Array.Empty(Of FireMapEventEvidence)()
    End Function

    Private Sub Publish(document As FireMapAlertDocument)
        Dim snapshots As IReadOnlyDictionary(Of String, IReadOnlyList(Of FireMapEventEvidence)) =
            document.Pending.GroupBy(Function(entry) entry.Scope).ToDictionary(Function(group) group.Key,
                Function(group) DirectCast(Array.AsReadOnly(group.Select(Function(entry) New FireMapEventEvidence(entry.Record)).ToArray()), IReadOnlyList(Of FireMapEventEvidence)),
                StringComparer.Ordinal)
        Threading.Volatile.Write(_snapshots, snapshots)
    End Sub

    Public Sub Reset(settings As FireConnectionSettings, session As UserSession, acknowledged As IReadOnlyList(Of FireMapEventEvidence))
        If session Is Nothing OrElse Not PlcConfigurationAccess.CanEdit(session.RoleName, session.IsOfflinePreview) Then Throw New UnauthorizedAccessException("Reset Map Alarms requires authorized live access.")
        If acknowledged Is Nothing Then Throw New ArgumentNullException(NameOf(acknowledged))
        SyncLock _gate
            Dim source = Scope(settings)
            Dim nextDocument = CopyDocument()
            Dim keys = acknowledged.Select(Function(item) source & ":" & item.EventKey).ToHashSet(StringComparer.Ordinal)
            Dim removed = nextDocument.Pending.Where(Function(entry) entry.Scope = source AndAlso keys.Contains(entry.Key)).Select(Function(entry) entry.Key).ToArray()
            Directory.CreateDirectory(Path.GetDirectoryName(_path))
            File.AppendAllText(_path & ".audit.jsonl", JsonSerializer.Serialize(New With {
                .TimestampUtc = DateTime.UtcNow, .Username = session.Username, .Action = "ResetMapAlarmsRequested",
                .Scope = source, .EventKeys = removed, .PanelCommandSent = False}) & Environment.NewLine, New UTF8Encoding(False))
            nextDocument.Pending.RemoveAll(Function(entry) entry.Scope = source AndAlso keys.Contains(entry.Key))
            Save(nextDocument)
        End SyncLock
    End Sub

    Private Function CopyDocument() As FireMapAlertDocument
        Return New FireMapAlertDocument With {
            .Seen = New HashSet(Of String)(_document.Seen, StringComparer.Ordinal),
            .Pending = New List(Of FireMapAlertEntry)(_document.Pending)}
    End Function

    Private Sub Save(document As FireMapAlertDocument)
        Directory.CreateDirectory(Path.GetDirectoryName(_path))
        Dim temporary = _path & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Try
            File.WriteAllText(temporary, FireConnectionStore.Protect(JsonSerializer.Serialize(document)), New UTF8Encoding(False))
            File.Move(temporary, _path, True)
            _document = document
            Publish(document)
        Finally
            If File.Exists(temporary) Then File.Delete(temporary)
        End Try
    End Sub
End Class
