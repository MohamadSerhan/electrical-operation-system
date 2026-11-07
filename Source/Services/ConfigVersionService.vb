Imports System.Globalization
Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading

Public NotInheritable Class ConfigVersionInfo
    Public Property FilePath As String = String.Empty
    Public Property FileName As String = String.Empty
    Public Property CreatedUtc As DateTime
    Public Property SizeBytes As Long
    Public Property Reason As String = String.Empty
    Public Property FileCount As Integer
    Public Property IsDefault As Boolean
End Class

' Keeps every distinct state of the application settings and EOEMSData\Configuration files as a zip
' in EOEMSData\ConfigVersions so any earlier configuration can be restored. The first snapshot is
' stored as CFG_DEFAULT.zip and can never be deleted or pruned.
Public NotInheritable Class ConfigVersionService
    Implements IDisposable

    Public Const DefaultFileName As String = "CFG_DEFAULT.zip"
    Private Const ManifestEntry As String = "_manifest.txt"
    Private Const MaximumVersions As Integer = 500
    Private Const DebounceMilliseconds As Integer = 3000

    Private ReadOnly _baseDirectory As String
    Private ReadOnly _configDirectory As String
    Private ReadOnly _versionsDirectory As String
    Private ReadOnly _gate As New Object()
    Private ReadOnly _watchers As New List(Of FileSystemWatcher)()
    Private _debounce As Timer
    Private _lastFingerprint As String = String.Empty
    Private _suspended As Boolean
    Private _disposed As Boolean

    Public Sub New(Optional baseDirectory As String = Nothing)
        _baseDirectory = Path.GetFullPath(If(String.IsNullOrWhiteSpace(baseDirectory), AppContext.BaseDirectory, baseDirectory))
        _configDirectory = Path.Combine(_baseDirectory, "EOEMSData", "Configuration")
        _versionsDirectory = Path.Combine(_baseDirectory, "EOEMSData", "ConfigVersions")
    End Sub

    Public ReadOnly Property VersionsDirectory As String
        Get
            Return _versionsDirectory
        End Get
    End Property

    Public Sub Start()
        SyncLock _gate
            Directory.CreateDirectory(_versionsDirectory)
            Directory.CreateDirectory(_configDirectory)
            _lastFingerprint = ReadLatestFingerprint()
            SnapshotCore("Application start")
        End SyncLock
        Watch(_configDirectory, "*.json")
        Watch(_baseDirectory, "appsettings.json")
        If Directory.Exists(Path.Combine(_baseDirectory, "data")) Then Watch(Path.Combine(_baseDirectory, "data"), "*-settings.json")
    End Sub

    Private Sub Watch(directory As String, filter As String)
        Dim watcher As New FileSystemWatcher(directory, filter) With {
            .NotifyFilter = NotifyFilters.LastWrite Or NotifyFilters.FileName Or NotifyFilters.Size,
            .IncludeSubdirectories = False,
            .EnableRaisingEvents = True}
        AddHandler watcher.Changed, AddressOf OnConfigurationChanged
        AddHandler watcher.Created, AddressOf OnConfigurationChanged
        AddHandler watcher.Renamed, AddressOf OnConfigurationChanged
        _watchers.Add(watcher)
    End Sub

    Private Sub OnConfigurationChanged(sender As Object, e As FileSystemEventArgs)
        If _disposed OrElse _suspended Then Return
        If Not IsTrackedName(Path.GetFileName(e.FullPath)) Then Return
        SyncLock _gate
            If _debounce Is Nothing Then
                _debounce = New Timer(AddressOf OnDebounced, Nothing, DebounceMilliseconds, Timeout.Infinite)
            Else
                _debounce.Change(DebounceMilliseconds, Timeout.Infinite)
            End If
        End SyncLock
    End Sub

    Private Sub OnDebounced(state As Object)
        If _disposed OrElse _suspended Then Return
        Try
            SaveVersion("Settings changed")
        Catch ex As IOException
            ' A writer still holds the file; the next change event retries.
        Catch ex As UnauthorizedAccessException
        End Try
    End Sub

    Private Shared Function IsTrackedName(fileName As String) As Boolean
        If String.IsNullOrEmpty(fileName) Then Return False
        If Not fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) Then Return False
        If fileName.StartsWith("backup-state", StringComparison.OrdinalIgnoreCase) Then Return False
        If fileName.StartsWith("pending-restore", StringComparison.OrdinalIgnoreCase) Then Return False
        If fileName.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) OrElse
           fileName.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) Then Return False
        Return True
    End Function

    Private Function TrackedFiles() As List(Of KeyValuePair(Of String, String))
        Dim result As New List(Of KeyValuePair(Of String, String))()
        Dim appSettings = Path.Combine(_baseDirectory, "appsettings.json")
        If File.Exists(appSettings) Then result.Add(New KeyValuePair(Of String, String)("appsettings.json", appSettings))
        If Directory.Exists(_configDirectory) Then
            For Each path In Directory.EnumerateFiles(_configDirectory, "*.json", SearchOption.TopDirectoryOnly).
                Where(Function(item) IsTrackedName(System.IO.Path.GetFileName(item))).
                OrderBy(Function(item) item, StringComparer.OrdinalIgnoreCase)
                result.Add(New KeyValuePair(Of String, String)("Configuration/" & System.IO.Path.GetFileName(path), path))
            Next
        End If
        For Each name In {"plc-settings.json", "ui-settings.json"}
            Dim dataFile = Path.Combine(_baseDirectory, "data", name)
            If File.Exists(dataFile) Then result.Add(New KeyValuePair(Of String, String)("data/" & name, dataFile))
        Next
        Return result
    End Function

    Private Shared Function ReadShared(path As String) As Byte()
        Using stream As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete)
            Using memory As New MemoryStream()
                stream.CopyTo(memory)
                Return memory.ToArray()
            End Using
        End Using
    End Function

    Private Shared Function Fingerprint(entries As IEnumerable(Of KeyValuePair(Of String, Byte()))) As String
        Using sha = SHA256.Create()
            Dim builder As New StringBuilder()
            For Each entry In entries
                builder.Append(entry.Key).Append(":").Append(Convert.ToHexString(sha.ComputeHash(entry.Value))).Append(";"c)
            Next
            Return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())))
        End Using
    End Function

    Private Function ReadLatestFingerprint() As String
        Dim latest = ListVersions().FirstOrDefault()
        If latest Is Nothing Then Return String.Empty
        Try
            Using archive = ZipFile.OpenRead(latest.FilePath)
                Dim entry = archive.GetEntry(ManifestEntry)
                If entry Is Nothing Then Return String.Empty
                Using reader As New StreamReader(entry.Open(), Encoding.UTF8)
                    For Each line In reader.ReadToEnd().Split({ControlChars.Cr, ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries)
                        If line.StartsWith("fingerprint=", StringComparison.Ordinal) Then Return line.Substring(12)
                    Next
                End Using
            End Using
        Catch ex As InvalidDataException
        Catch ex As IOException
        End Try
        Return String.Empty
    End Function

    ' Returns the new version path, or Nothing when nothing changed since the latest version.
    Public Function SaveVersion(reason As String) As String
        SyncLock _gate
            Return SnapshotCore(reason)
        End SyncLock
    End Function

    Private Function SnapshotCore(reason As String) As String
        Dim files = TrackedFiles()
        If files.Count = 0 Then Return Nothing
        Dim contents As New List(Of KeyValuePair(Of String, Byte()))()
        For Each item In files
            contents.Add(New KeyValuePair(Of String, Byte())(item.Key, ReadShared(item.Value)))
        Next
        Dim print = Fingerprint(contents)
        If String.Equals(print, _lastFingerprint, StringComparison.Ordinal) Then Return Nothing

        Directory.CreateDirectory(_versionsDirectory)
        Dim defaultPath = Path.Combine(_versionsDirectory, DefaultFileName)
        Dim isFirst = Not File.Exists(defaultPath)
        Dim target = If(isFirst, defaultPath,
            Path.Combine(_versionsDirectory, "CFG_" & DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) & ".zip"))
        Dim temporary = target & ".tmp"
        If File.Exists(temporary) Then File.Delete(temporary)
        Using stream As New FileStream(temporary, FileMode.CreateNew, FileAccess.Write)
            Using archive As New ZipArchive(stream, ZipArchiveMode.Create)
                For Each item In contents
                    Dim entry = archive.CreateEntry(item.Key, CompressionLevel.Optimal)
                    Using output = entry.Open()
                        output.Write(item.Value, 0, item.Value.Length)
                    End Using
                Next
                Dim manifest = archive.CreateEntry(ManifestEntry, CompressionLevel.Optimal)
                Using writer As New StreamWriter(manifest.Open(), New UTF8Encoding(False))
                    writer.WriteLine("fingerprint=" & print)
                    writer.WriteLine("created=" & DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                    writer.WriteLine("reason=" & If(isFirst, "Default (original baseline)", If(reason, String.Empty)).Replace(ControlChars.Cr, " "c).Replace(ControlChars.Lf, " "c))
                    writer.WriteLine("files=" & contents.Count.ToString(CultureInfo.InvariantCulture))
                End Using
            End Using
        End Using
        File.Move(temporary, target)
        _lastFingerprint = print
        Prune()
        Return target
    End Function

    Private Sub Prune()
        Dim removable = ListVersions().Where(Function(item) Not item.IsDefault).Skip(MaximumVersions).ToList()
        For Each item In removable
            Try
                File.Delete(item.FilePath)
            Catch ex As IOException
            Catch ex As UnauthorizedAccessException
            End Try
        Next
    End Sub

    Public Function ListVersions() As IReadOnlyList(Of ConfigVersionInfo)
        If Not Directory.Exists(_versionsDirectory) Then Return Array.Empty(Of ConfigVersionInfo)()
        Dim result As New List(Of ConfigVersionInfo)()
        For Each path In Directory.EnumerateFiles(_versionsDirectory, "CFG_*.zip", SearchOption.TopDirectoryOnly)
            Dim info As New FileInfo(path)
            Dim version As New ConfigVersionInfo With {
                .FilePath = path,
                .FileName = info.Name,
                .CreatedUtc = info.LastWriteTimeUtc,
                .SizeBytes = info.Length,
                .IsDefault = String.Equals(info.Name, DefaultFileName, StringComparison.OrdinalIgnoreCase)}
            Try
                Using archive = ZipFile.OpenRead(path)
                    version.FileCount = archive.Entries.Where(Function(entry) entry.FullName <> ManifestEntry).Count()
                    Dim manifest = archive.GetEntry(ManifestEntry)
                    If manifest IsNot Nothing Then
                        Using reader As New StreamReader(manifest.Open(), Encoding.UTF8)
                            For Each line In reader.ReadToEnd().Split({ControlChars.Cr, ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries)
                                If line.StartsWith("reason=", StringComparison.Ordinal) Then version.Reason = line.Substring(7)
                                If line.StartsWith("created=", StringComparison.Ordinal) Then
                                    Dim created As DateTime
                                    If DateTime.TryParse(line.Substring(8), CultureInfo.InvariantCulture,
                                                         DateTimeStyles.RoundtripKind, created) Then version.CreatedUtc = created.ToUniversalTime()
                                End If
                            Next
                        End Using
                    End If
                End Using
            Catch ex As InvalidDataException
                version.Reason = "Unreadable archive"
            Catch ex As IOException
                version.Reason = "Unreadable archive"
            End Try
            result.Add(version)
        Next
        Return result.OrderByDescending(Function(item) item.CreatedUtc).ToList()
    End Function

    Private Function ResolveVersionPath(versionPath As String) As String
        If String.IsNullOrWhiteSpace(versionPath) Then Throw New ArgumentException("Select a settings version first.", NameOf(versionPath))
        Dim full = Path.GetFullPath(versionPath)
        Dim folder = Path.GetFullPath(_versionsDirectory).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
        If Not full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) OrElse Not File.Exists(full) Then
            Throw New FileNotFoundException("The selected settings version is not in the versions folder.")
        End If
        Return full
    End Function

    Public Sub DeleteVersion(versionPath As String)
        Dim full = ResolveVersionPath(versionPath)
        If String.Equals(Path.GetFileName(full), DefaultFileName, StringComparison.OrdinalIgnoreCase) Then
            Throw New InvalidOperationException("The default settings version is protected and cannot be deleted.")
        End If
        SyncLock _gate
            File.Delete(full)
            _lastFingerprint = ReadLatestFingerprint()
        End SyncLock
    End Sub

    ' Saves the current settings as a new version first, then overwrites the tracked files from the selected version.
    Public Function RestoreVersion(versionPath As String) As Integer
        Dim full = ResolveVersionPath(versionPath)
        Dim restored = 0
        SyncLock _gate
            _suspended = True
            Try
                SnapshotCore("Before restore of " & Path.GetFileName(full))
                Using archive = ZipFile.OpenRead(full)
                    Dim staged As New List(Of KeyValuePair(Of String, String))()
                    For Each entry In archive.Entries
                        If entry.FullName = ManifestEntry Then Continue For
                        Dim destination = ResolveDestination(entry.FullName)
                        Dim temporary = destination & ".restore.tmp"
                        Using input = entry.Open()
                            Using output As New FileStream(temporary, FileMode.Create, FileAccess.Write)
                                input.CopyTo(output)
                            End Using
                        End Using
                        staged.Add(New KeyValuePair(Of String, String)(temporary, destination))
                    Next
                    For Each item In staged
                        File.Move(item.Key, item.Value, True)
                        SyncDocument(item.Value)
                        restored += 1
                    Next
                End Using
                _lastFingerprint = String.Empty
                SnapshotCore("Restored " & Path.GetFileName(full))
            Finally
                _suspended = False
            End Try
        End SyncLock
        Return restored
    End Function

    ' PostgreSQL is the source of truth when the app loads settings, so a restored file must also replace the stored document.
    Private Shared Sub SyncDocument(filePath As String)
        Dim key As String
        Select Case Path.GetFileName(filePath).ToLowerInvariant()
            Case "recording-settings.json" : key = "recording-settings"
            Case "backup-settings.json" : key = "backup-settings"
            Case "alarm-settings.json" : key = "alarm-settings"
            Case "ups-settings.json" : key = "ups-settings"
            Case "plc-settings.json" : key = "plc-settings"
            Case "ui-settings.json" : key = "ui-settings"
            Case Else : Return
        End Select
        Try
            PostgresDatabase.TryWriteDocument(key, File.ReadAllText(filePath), "version-restore")
        Catch ex As IOException
        End Try
    End Sub

    Private Function ResolveDestination(entryName As String) As String
        If entryName.StartsWith("data/", StringComparison.Ordinal) Then
            Dim dataName = entryName.Substring(5)
            If dataName = "plc-settings.json" OrElse dataName = "ui-settings.json" Then
                Return Path.Combine(_baseDirectory, "data", dataName)
            End If
        End If
        If String.Equals(entryName, "appsettings.json", StringComparison.Ordinal) Then
            Return Path.Combine(_baseDirectory, "appsettings.json")
        End If
        Const prefix As String = "Configuration/"
        If entryName.StartsWith(prefix, StringComparison.Ordinal) Then
            Dim name = entryName.Substring(prefix.Length)
            If name.Length > 0 AndAlso name = Path.GetFileName(name) AndAlso IsTrackedName(name) Then
                Return Path.Combine(_configDirectory, name)
            End If
        End If
        Throw New InvalidDataException("The settings version contains an unexpected entry: " & entryName)
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        If _disposed Then Return
        _disposed = True
        For Each watcher In _watchers
            watcher.Dispose()
        Next
        _watchers.Clear()
        _debounce?.Dispose()
    End Sub
End Class
