Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports Microsoft.Data.Sqlite

Public NotInheritable Class ZetaCloudEventHistory
    Private ReadOnly _path As String
    Private ReadOnly _gate As New Object

    Public Sub New(settingsPath As String)
        _path = settingsPath & ".cloud-events.db"
    End Sub

    Private Function Open() As SqliteConnection
        Directory.CreateDirectory(Path.GetDirectoryName(_path))
        Dim connection As New SqliteConnection(New SqliteConnectionStringBuilder With {.DataSource = _path}.ToString())
        Try
            connection.Open()
            Using command = connection.CreateCommand()
                command.CommandText = "CREATE TABLE IF NOT EXISTS cloud_events(event_key TEXT PRIMARY KEY, site_key TEXT NOT NULL, payload TEXT NOT NULL); CREATE INDEX IF NOT EXISTS ix_cloud_events_site ON cloud_events(site_key);"
                command.ExecuteNonQuery()
            End Using
            Return connection
        Catch
            connection.Dispose()
            Throw
        End Try
    End Function

    Private Shared Function Hash(value As String) As String
        Return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
    End Function

    Public Function Synchronize(events As IReadOnlyList(Of ZetaFireEvent), Optional cancellation As CancellationToken = Nothing) As Integer
        SyncLock _gate
            Using connection = Open(), transaction = connection.BeginTransaction(), exists = connection.CreateCommand()
                exists.Transaction = transaction
                exists.CommandText = "SELECT 1 FROM cloud_events WHERE event_key=$id"
                Dim keyParameter = exists.Parameters.Add("$id", SqliteType.Text)
                exists.Prepare()
                Dim added = 0
                For Each record In events
                    cancellation.ThrowIfCancellationRequested()
                    Dim serialized = JsonSerializer.Serialize(record)
                    Dim identity = If(record.EventId.Length > 0, "id:" & record.EventId, "fingerprint:" & serialized)
                    Dim key = Hash(record.SiteId & vbNullChar & identity)
                    keyParameter.Value = key
                    If exists.ExecuteScalar() IsNot Nothing Then Continue For
                    Using command = connection.CreateCommand()
                        command.Transaction = transaction
                        command.CommandText = "INSERT INTO cloud_events(event_key,site_key,payload) VALUES($id,$site,$payload) ON CONFLICT(event_key) DO NOTHING"
                        command.Parameters.AddWithValue("$id", key)
                        command.Parameters.AddWithValue("$site", Hash(record.SiteId))
                        command.Parameters.AddWithValue("$payload", FireConnectionStore.Protect(serialized))
                        added += command.ExecuteNonQuery()
                    End Using
                Next
                cancellation.ThrowIfCancellationRequested()
                transaction.Commit()
                Return added
            End Using
        End SyncLock
    End Function

    Public Function Count(site As String) As Long
        SyncLock _gate
            Using connection = Open(), command = connection.CreateCommand()
                command.CommandText = "SELECT COUNT(*) FROM cloud_events WHERE site_key=$site"
                command.Parameters.AddWithValue("$site", Hash(site))
                Return CLng(command.ExecuteScalar())
            End Using
        End SyncLock
    End Function

    Public Function Read(site As String, Optional limit As Integer = 200) As IReadOnlyList(Of ZetaFireEvent)
        If limit < 1 OrElse limit > 10000 Then Throw New ArgumentOutOfRangeException(NameOf(limit))
        SyncLock _gate
            Using connection = Open(), command = connection.CreateCommand()
                command.CommandText = "SELECT payload FROM cloud_events WHERE site_key=$site ORDER BY rowid DESC LIMIT $limit"
                command.Parameters.AddWithValue("$site", Hash(site))
                command.Parameters.AddWithValue("$limit", limit)
                Dim result As New List(Of ZetaFireEvent)
                Using reader = command.ExecuteReader()
                    While reader.Read()
                        Dim record = JsonSerializer.Deserialize(Of ZetaFireEvent)(FireConnectionStore.Unprotect(reader.GetString(0)))
                        If record Is Nothing Then Throw New InvalidDataException("Invalid stored cloud event.")
                        result.Add(record)
                    End While
                End Using
                Return result.OrderByDescending(Function(item) item.TimestampUtc).ToArray()
            End Using
        End SyncLock
    End Function
End Class
