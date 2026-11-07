Imports System.Globalization
Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Data.Sqlite

Public NotInheritable Class BackupPayloadManifest
    Public Property FormatVersion As Integer = 1
    Public Property BackupId As String = String.Empty
    Public Property CreatedAtUtc As DateTime
    Public Property ApplicationVersion As String = String.Empty
    Public Property DatabaseIntegrity As String = String.Empty
    Public Property PayloadHashes As Dictionary(Of String, String) = New Dictionary(Of String, String)(StringComparer.Ordinal)
    Public Property ExcludedItems As String() = {"data\eoems.db (authentication database)", "temporary files", "generated build outputs"}
End Class

Public NotInheritable Class BackupVerificationResult
    Public Property IsValid As Boolean
    Public Property FilePath As String = String.Empty
    Public Property Message As String = String.Empty
    Public Property CreatedAtUtc As DateTime?
    Public Property DatabaseIntegrity As String = String.Empty
    Public Property PayloadEntries As IReadOnlyList(Of String) = Array.Empty(Of String)()
    Public Property SizeBytes As Long
End Class

Public NotInheritable Class BackupArtifactInfo
    Public Property FilePath As String = String.Empty
    Public Property FileName As String = String.Empty
    Public Property LastWriteUtc As DateTime
    Public Property SizeBytes As Long
End Class

Public NotInheritable Class RestorePreparationResult
    Public Property RollbackBackupPath As String = String.Empty
    Public Property Message As String = String.Empty
End Class

Public NotInheritable Class BackupRecoveryService
    Implements IDisposable

    Private NotInheritable Class PendingRestore
        Public Property FormatVersion As Integer = 1
        Public Property TransactionId As String = String.Empty
        Public Property Phase As String = String.Empty
        Public Property SelectedBackupPath As String = String.Empty
        Public Property RollbackBackupPath As String = String.Empty
        Public Property FailureMessage As String = String.Empty
    End Class

    Private NotInheritable Class BackupState
        Public Property LastSuccessfulLocalDate As String = String.Empty
        Public Property LastSuccessfulUtc As DateTime?
        Public Property LastBackupPath As String = String.Empty
        Public Property LastSeenBuildId As String = String.Empty
    End Class

    Private Shared ReadOnly SerializerOptions As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True,
        .WriteIndented = True
    }

    Private ReadOnly _historian As MeasurementHistorian
    Private ReadOnly _baseDirectory As String
    Private ReadOnly _statePath As String
    Private ReadOnly _operationGate As New SemaphoreSlim(1, 1)
    Private ReadOnly _settingsGate As New Object()
    Private _settings As BackupSettings
    Private _recordingSettings As RecordingSettings
    Private _shutdown As CancellationTokenSource
    Private _worker As Task
    Private _lastError As String = String.Empty
    Private _lastBackupUtc As DateTime?
    Private _lastBackupPath As String = String.Empty

    Public Sub New(historian As MeasurementHistorian,
                   settings As BackupSettings,
                   recordingSettings As RecordingSettings,
                   Optional baseDirectory As String = Nothing)
        If historian Is Nothing Then Throw New ArgumentNullException(NameOf(historian))
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        If recordingSettings Is Nothing Then Throw New ArgumentNullException(NameOf(recordingSettings))
        settings.Validate()
        recordingSettings.Validate()
        _historian = historian
        _settings = settings
        _recordingSettings = recordingSettings
        _baseDirectory = Path.GetFullPath(If(String.IsNullOrWhiteSpace(baseDirectory), AppContext.BaseDirectory, baseDirectory))
        _statePath = Path.Combine(_baseDirectory, "EOEMSData", "Configuration", "backup-state.json")
        Dim state = LoadState()
        _lastBackupUtc = state.LastSuccessfulUtc
        _lastBackupPath = state.LastBackupPath
    End Sub

    Public ReadOnly Property LastError As String
        Get
            SyncLock _settingsGate
                Return _lastError
            End SyncLock
        End Get
    End Property

    Public ReadOnly Property LastBackupUtc As DateTime?
        Get
            SyncLock _settingsGate
                Return _lastBackupUtc
            End SyncLock
        End Get
    End Property

    Public ReadOnly Property LastBackupPath As String
        Get
            SyncLock _settingsGate
                Return _lastBackupPath
            End SyncLock
        End Get
    End Property

    Public Sub UpdateSettings(settings As BackupSettings, recordingSettings As RecordingSettings)
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        If recordingSettings Is Nothing Then Throw New ArgumentNullException(NameOf(recordingSettings))
        settings.Validate()
        recordingSettings.Validate()
        SyncLock _settingsGate
            _settings = settings
            _recordingSettings = recordingSettings
        End SyncLock
    End Sub

    Public Function GetRecentBackups(Optional limit As Integer = 20) As IReadOnlyList(Of BackupArtifactInfo)
        If limit < 1 OrElse limit > 500 Then Throw New ArgumentOutOfRangeException(NameOf(limit))
        Dim settings As BackupSettings
        SyncLock _settingsGate
            settings = _settings
        End SyncLock
        Dim folder = settings.ResolveBackupFolder(_baseDirectory)
        If Not Directory.Exists(folder) Then Return Array.Empty(Of BackupArtifactInfo)()
        Return Directory.EnumerateFiles(folder, "EOEMS_Backup_*.zip", SearchOption.TopDirectoryOnly).
            Select(Function(path)
                       Dim info As New FileInfo(path)
                       Return New BackupArtifactInfo With {
                           .FilePath = path,
                           .FileName = info.Name,
                           .LastWriteUtc = info.LastWriteTimeUtc,
                           .SizeBytes = info.Length
                       }
                   End Function).
            OrderByDescending(Function(item) item.LastWriteUtc).
            Take(limit).
            ToArray()
    End Function

    Public Sub DeleteBackup(backupPath As String)
        If String.IsNullOrWhiteSpace(backupPath) Then Throw New ArgumentException("Select a backup file to delete.", NameOf(backupPath))
        Dim settings As BackupSettings
        SyncLock _settingsGate
            settings = _settings
        End SyncLock
        Dim folder = Path.GetFullPath(settings.ResolveBackupFolder(_baseDirectory)).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
        Dim full = Path.GetFullPath(backupPath)
        If Not full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) OrElse
           Not Path.GetFileName(full).StartsWith("EOEMS_Backup_", StringComparison.OrdinalIgnoreCase) OrElse
           Not full.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) Then
            Throw New InvalidOperationException("Only backup archives in the configured backup folder can be deleted.")
        End If
        _operationGate.Wait()
        Try
            If Not File.Exists(full) Then Throw New FileNotFoundException("The backup no longer exists.", full)
            File.Delete(full)
        Finally
            _operationGate.Release()
        End Try
    End Sub

    Public Async Function CreateBackupAsync(Optional cancellationToken As CancellationToken = Nothing) As Task(Of String)
        Await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(False)
        Try
            Return Await CreateBackupCoreAsync(cancellationToken).ConfigureAwait(False)
        Finally
            _operationGate.Release()
        End Try
    End Function

    Public Async Function VerifyBackupAsync(backupPath As String,
                                            Optional cancellationToken As CancellationToken = Nothing) As Task(Of BackupVerificationResult)
        If String.IsNullOrWhiteSpace(backupPath) Then Throw New ArgumentException("Select a backup file to verify.", NameOf(backupPath))
        Return Await Task.Run(Function() VerifyBackupCore(Path.GetFullPath(backupPath), cancellationToken), cancellationToken).
            ConfigureAwait(False)
    End Function

    Public Async Function PrepareRestoreAsync(backupPath As String,
                                              Optional cancellationToken As CancellationToken = Nothing) As Task(Of RestorePreparationResult)
        If String.IsNullOrWhiteSpace(backupPath) Then Throw New ArgumentException("Select a backup file to restore.", NameOf(backupPath))
        Await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(False)
        Dim transactionId = Guid.NewGuid().ToString("N")
        Dim transactionRoot = Path.Combine(_baseDirectory, "EOEMSData", "RestoreStaging", transactionId)
        Try
            If File.Exists(PendingRestorePath(_baseDirectory)) Then
                Throw New IOException("Another restore is already pending startup recovery.")
            End If
            Dim selectedPath = Path.GetFullPath(backupPath)
            Dim verification = Await VerifyBackupAsync(selectedPath, cancellationToken).ConfigureAwait(False)
            If Not verification.IsValid Then Throw New InvalidDataException("Selected backup did not pass verification.")

            Dim rollbackPath = Await CreateBackupCoreAsync(cancellationToken, applyRetention:=False).ConfigureAwait(False)
            Dim selectedStage = Path.Combine(transactionRoot, "selected")
            ExtractRestorePayload(selectedPath, selectedStage, cancellationToken)

            Dim pending As New PendingRestore With {
                .TransactionId = transactionId,
                .Phase = "Ready",
                .SelectedBackupPath = selectedPath,
                .RollbackBackupPath = rollbackPath
            }
            SavePendingRestore(PendingRestorePath(_baseDirectory), pending)
            SetLastError(String.Empty)
            Return New RestorePreparationResult With {
                .RollbackBackupPath = rollbackPath,
                .Message = "Restore is staged. Historian and MeterLogs will be replaced on the next application start; current app and PLC settings and authentication data will remain unchanged."
            }
        Catch ex As Exception
            If Directory.Exists(transactionRoot) Then Directory.Delete(transactionRoot, True)
            SetLastError("Restore preparation failed: " & ex.Message)
            Throw
        Finally
            _operationGate.Release()
        End Try
    End Function

    Public Shared Function ApplyPendingRestoreAtStartup(Optional baseDirectory As String = Nothing) As String
        Dim root = Path.GetFullPath(If(String.IsNullOrWhiteSpace(baseDirectory), AppContext.BaseDirectory, baseDirectory))
        Dim pendingPath = PendingRestorePath(root)
        If Not File.Exists(pendingPath) Then Return String.Empty

        Dim pending As PendingRestore
        Try
            pending = JsonSerializer.Deserialize(Of PendingRestore)(File.ReadAllText(pendingPath), SerializerOptions)
        Catch ex As Exception
            Throw New InvalidDataException("A staged restore record could not be read; the application was not started.", ex)
        End Try
        Dim parsedTransactionId As Guid
        If pending Is Nothing OrElse pending.FormatVersion <> 1 OrElse
           Not Guid.TryParseExact(pending.TransactionId, "N", parsedTransactionId) OrElse
           String.IsNullOrWhiteSpace(pending.RollbackBackupPath) Then
            Throw New InvalidDataException("A staged restore record is invalid; the application was not started.")
        End If

        Dim transactionRoot = Path.Combine(root, "EOEMSData", "RestoreStaging", pending.TransactionId)
        Select Case pending.Phase
            Case "Applied"
                CompletePendingRestore(pendingPath, transactionRoot)
                Return "The staged historian and MeterLogs restore was applied successfully. App/PLC settings and authentication data were not changed."
            Case "RolledBack", "RollingBack"
                Try
                    ApplyArchiveToTargets(pending.RollbackBackupPath, root, Path.Combine(transactionRoot, "rollback"), pending.TransactionId)
                    pending.Phase = "RolledBack"
                    SavePendingRestore(pendingPath, pending)
                    CompletePendingRestore(pendingPath, transactionRoot)
                    Return "A previous restore attempt was interrupted or failed. The verified rollback archive was applied; the selected restore was not completed. " & pending.FailureMessage
                Catch ex As Exception
                    Throw New InvalidOperationException("Restore recovery could not apply the verified rollback archive. The application was not started. " & ex.Message, ex)
                End Try
            Case "Ready", "Applying"
                Try
                    pending.Phase = "Applying"
                    SavePendingRestore(pendingPath, pending)
                    ApplyStagedTargets(Path.Combine(transactionRoot, "selected"), root, pending.TransactionId)
                    pending.Phase = "Applied"
                    SavePendingRestore(pendingPath, pending)
                    CompletePendingRestore(pendingPath, transactionRoot)
                    Return "The staged historian and MeterLogs restore was applied successfully. App/PLC settings and authentication data were not changed. Rollback archive: " & pending.RollbackBackupPath
                Catch applyException As Exception
                    If String.Equals(pending.Phase, "Applied", StringComparison.Ordinal) Then Throw
                    pending.Phase = "RollingBack"
                    pending.FailureMessage = applyException.Message
                    SavePendingRestore(pendingPath, pending)
                    Try
                        ApplyArchiveToTargets(pending.RollbackBackupPath, root, Path.Combine(transactionRoot, "rollback"), pending.TransactionId)
                        pending.Phase = "RolledBack"
                        SavePendingRestore(pendingPath, pending)
                        CompletePendingRestore(pendingPath, transactionRoot)
                        Return "Restore was not applied. The verified rollback archive restored the pre-restore historian and MeterLogs. Reason: " & applyException.Message
                    Catch rollbackException As Exception
                        Throw New InvalidOperationException(
                            "Restore failed and rollback could not be completed. The application was not started; retry startup to resume recovery. Restore error: " &
                            applyException.Message & " Rollback error: " & rollbackException.Message,
                            New AggregateException(applyException, rollbackException))
                    End Try
                End Try
            Case Else
                Throw New InvalidDataException("A staged restore has an unknown recovery phase; the application was not started.")
        End Select
    End Function

    Public Async Function RunScheduledBackupIfDueAsync(nowUtc As DateTime,
                                                       Optional cancellationToken As CancellationToken = Nothing) As Task(Of String)
        If nowUtc.Kind <> DateTimeKind.Utc Then Throw New ArgumentException("Scheduled backup time must be UTC.", NameOf(nowUtc))
        Await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(False)
        Try
            Dim settings As BackupSettings
            Dim recordingSettings As RecordingSettings
            SyncLock _settingsGate
                settings = _settings
                recordingSettings = _recordingSettings
            End SyncLock
            If Not settings.AutomaticBackupEnabled Then Return Nothing

            ' No daily schedule: a backup is taken only when the application build changed (an update was installed).
            Dim buildId = GetType(BackupRecoveryService).Assembly.ManifestModule.ModuleVersionId.ToString("N")
            Dim state = LoadState()
            If String.Equals(state.LastSeenBuildId, buildId, StringComparison.Ordinal) Then Return Nothing

            Dim path = Await CreateBackupCoreAsync(cancellationToken).ConfigureAwait(False)
            state = LoadState()
            state.LastSeenBuildId = buildId
            state.LastBackupPath = path
            state.LastBackupPath = path
            SaveState(state)
            Return path
        Finally
            _operationGate.Release()
        End Try
    End Function

    Public Sub Start()
        SyncLock _settingsGate
            If _worker IsNot Nothing Then Return
            _shutdown = New CancellationTokenSource()
            _worker = Task.Run(Function() RunLoopAsync(_shutdown.Token))
        End SyncLock
    End Sub

    Public Async Function StopAsync() As Task
        Dim worker As Task
        SyncLock _settingsGate
            If _shutdown Is Nothing Then Return
            _shutdown.Cancel()
            worker = _worker
        End SyncLock
        If worker IsNot Nothing Then
            Try
                Await worker.ConfigureAwait(False)
            Catch ex As OperationCanceledException
            End Try
        End If
        SyncLock _settingsGate
            _shutdown.Dispose()
            _shutdown = Nothing
            _worker = Nothing
        End SyncLock
    End Function

    Private Async Function RunLoopAsync(cancellationToken As CancellationToken) As Task
        While Not cancellationToken.IsCancellationRequested
            Try
                Dim path = Await RunScheduledBackupIfDueAsync(DateTime.UtcNow, cancellationToken).ConfigureAwait(False)
                If Not String.IsNullOrWhiteSpace(path) Then SetLastError(String.Empty)
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Exit While
            Catch ex As Exception
                SetLastError("Scheduled backup failed: " & ex.Message)
            End Try
            Await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(False)
        End While
    End Function

    Private Async Function CreateBackupCoreAsync(cancellationToken As CancellationToken,
                                                 Optional applyRetention As Boolean = True) As Task(Of String)
        cancellationToken.ThrowIfCancellationRequested()
        Dim settings As BackupSettings
        SyncLock _settingsGate
            settings = _settings
        End SyncLock
        Dim backupFolder = settings.ResolveBackupFolder(_baseDirectory)
        Directory.CreateDirectory(backupFolder)

        Dim backupId = Guid.NewGuid().ToString("N")
        Dim createdAtUtc = DateTime.UtcNow
        Dim fileName = "EOEMS_Backup_" & createdAtUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) &
                       "_" & backupId.Substring(0, 8) & ".zip"
        Dim finalPath = Path.Combine(backupFolder, fileName)
        Dim stagedArchive = Path.Combine(backupFolder, "." & fileName & ".tmp")
        Dim workFolder = Path.Combine(Path.GetTempPath(), "eoems-backup-" & backupId)
        Directory.CreateDirectory(workFolder)
        Dim databaseSnapshot = Path.Combine(workFolder, "eoems_historian.db")

        Try
            Await _historian.CreateBackupDatabaseAsync(databaseSnapshot).ConfigureAwait(False)
            Dim integrity = CheckDatabaseIntegrity(databaseSnapshot)
            If Not String.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase) Then
                Throw New InvalidDataException("Historian backup failed SQLite integrity_check: " & integrity)
            End If

            Dim manifest As New BackupPayloadManifest With {
                .BackupId = backupId,
                .CreatedAtUtc = createdAtUtc,
                .ApplicationVersion = AppBuildInfo.VersionDisplay,
                .DatabaseIntegrity = integrity
            }
            Using output As New FileStream(stagedArchive, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
                Using archive As New ZipArchive(output, ZipArchiveMode.Create, True)
                    AddFilePayload(archive, databaseSnapshot, "historian/eoems_historian.db", manifest)
                    AddApplicationConfiguration(archive, manifest)
                    AddExistingConfiguration(archive, Path.Combine(_baseDirectory, "data", "plc-settings.json"),
                                             "configuration/plc-settings.json", manifest)
                    AddExistingConfiguration(archive, Path.Combine(_baseDirectory, "data", "ui-settings.json"),
                                             "configuration/ui-settings.json", manifest)
                    AddExistingConfiguration(archive,
                                             Path.Combine(_baseDirectory, "EOEMSData", "Configuration", "recording-settings.json"),
                                             "configuration/recording-settings.json", manifest)
                    AddExistingConfiguration(archive,
                                             Path.Combine(_baseDirectory, "EOEMSData", "Configuration", "backup-settings.json"),
                                             "configuration/backup-settings.json", manifest)
                    AddMeterLogs(archive, Path.Combine(_baseDirectory, "data", "MeterLogs"), manifest)
                    AddExistingConfiguration(archive,
                                             Path.Combine(_baseDirectory, "EOEMSData", "Configuration", "monitoring-devices.json"),
                                             "configuration/monitoring-devices.json", manifest)
                    AddManifest(archive, manifest)
                End Using
            End Using

            Dim verification = Await VerifyBackupAsync(stagedArchive, cancellationToken).ConfigureAwait(False)
            If Not verification.IsValid Then
                Throw New InvalidDataException("Staged backup verification failed: " & verification.Message)
            End If
            If File.Exists(finalPath) Then Throw New IOException("Backup filename collision; existing file was not overwritten.")
            File.Move(stagedArchive, finalPath)

            Dim state = LoadState()
            state.LastSuccessfulUtc = DateTime.UtcNow
            state.LastBackupPath = finalPath
            SaveState(state)
            SyncLock _settingsGate
                _lastBackupUtc = state.LastSuccessfulUtc
                _lastBackupPath = finalPath
                _lastError = String.Empty
            End SyncLock
            If applyRetention Then ApplyRetentionPolicy(settings, backupFolder, finalPath)
            Return finalPath
        Catch ex As Exception
            SetLastError("Backup failed: " & ex.Message)
            Throw
        Finally
            If File.Exists(stagedArchive) Then File.Delete(stagedArchive)
            If Directory.Exists(workFolder) Then Directory.Delete(workFolder, True)
        End Try
    End Function

    Private Sub AddApplicationConfiguration(archive As ZipArchive, manifest As BackupPayloadManifest)
        Dim appSettingsPath = Path.Combine(_baseDirectory, "appsettings.json")
        If Not File.Exists(appSettingsPath) Then Return
        Dim node = JsonNode.Parse(File.ReadAllText(appSettingsPath))
        If node Is Nothing Then Throw New InvalidDataException("Application settings are empty and cannot be backed up.")
        RedactSensitiveSettings(node)
        AddBytePayload(archive, JsonSerializer.SerializeToUtf8Bytes(node, SerializerOptions),
                       "configuration/appsettings.json", manifest)
    End Sub

    Private Shared Sub RedactSensitiveSettings(node As JsonNode)
        Dim settingsObject = TryCast(node, JsonObject)
        If settingsObject IsNot Nothing Then
            For Each propertyName In settingsObject.Select(Function(item) item.Key).ToArray()
                If propertyName.IndexOf("password", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                   propertyName.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                   propertyName.IndexOf("credential", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                   propertyName.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                   propertyName.IndexOf("connectionstring", StringComparison.OrdinalIgnoreCase) >= 0 Then
                    settingsObject(propertyName) = String.Empty
                Else
                    RedactSensitiveSettings(settingsObject(propertyName))
                End If
            Next
            Return
        End If
        Dim settingsArray = TryCast(node, JsonArray)
        If settingsArray IsNot Nothing Then
            For Each item In settingsArray
                If item IsNot Nothing Then RedactSensitiveSettings(item)
            Next
        End If
    End Sub

    Private Sub AddExistingConfiguration(archive As ZipArchive,
                                         sourcePath As String,
                                         entryName As String,
                                         manifest As BackupPayloadManifest)
        If File.Exists(sourcePath) Then AddFilePayload(archive, sourcePath, entryName, manifest)
    End Sub

    Private Sub AddMeterLogs(archive As ZipArchive, folder As String, manifest As BackupPayloadManifest)
        If Not Directory.Exists(folder) Then Return
        For Each filePath In Directory.EnumerateFiles(folder, "*.csv", SearchOption.AllDirectories)
            Dim relative = Path.GetRelativePath(folder, filePath).Replace(Path.DirectorySeparatorChar, "/"c)
            AddFilePayload(archive, filePath, "MeterLogs/" & relative, manifest)
        Next
    End Sub

    Private Shared Sub AddFilePayload(archive As ZipArchive,
                                      sourcePath As String,
                                      entryName As String,
                                      manifest As BackupPayloadManifest)
        Dim entry = archive.CreateEntry(entryName, CompressionLevel.Optimal)
        Using source As New FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
            Using destination = entry.Open()
                manifest.PayloadHashes(entryName) = CopyAndHash(source, destination)
            End Using
        End Using
    End Sub

    Private Shared Sub AddBytePayload(archive As ZipArchive,
                                      contents As Byte(),
                                      entryName As String,
                                      manifest As BackupPayloadManifest)
        Dim entry = archive.CreateEntry(entryName, CompressionLevel.Optimal)
        Using destination = entry.Open()
            destination.Write(contents, 0, contents.Length)
        End Using
        manifest.PayloadHashes(entryName) = Convert.ToHexString(SHA256.HashData(contents))
    End Sub

    Private Shared Function CopyAndHash(source As Stream, destination As Stream) As String
        Using hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            Dim buffer(65535) As Byte
            Dim count As Integer
            Do
                count = source.Read(buffer, 0, buffer.Length)
                If count <= 0 Then Exit Do
                destination.Write(buffer, 0, count)
                hash.AppendData(buffer, 0, count)
            Loop
            Return Convert.ToHexString(hash.GetHashAndReset())
        End Using
    End Function

    Private Shared Function HashStream(source As Stream) As String
        Using hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
            Dim buffer(65535) As Byte
            Dim count As Integer
            Do
                count = source.Read(buffer, 0, buffer.Length)
                If count <= 0 Then Exit Do
                hash.AppendData(buffer, 0, count)
            Loop
            Return Convert.ToHexString(hash.GetHashAndReset())
        End Using
    End Function

    Private Shared Sub AddManifest(archive As ZipArchive, manifest As BackupPayloadManifest)
        Dim entry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal)
        Using stream = entry.Open()
            JsonSerializer.Serialize(stream, manifest, SerializerOptions)
        End Using
    End Sub

    Private Shared Function VerifyBackupCore(backupPath As String,
                                             cancellationToken As CancellationToken) As BackupVerificationResult
        If Not File.Exists(backupPath) Then Throw New FileNotFoundException("Backup file was not found.", backupPath)
        Dim size = New FileInfo(backupPath).Length
        Dim workFolder = Path.Combine(Path.GetTempPath(), "eoems-verify-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(workFolder)
        Try
            Using input As New FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read)
                Using archive As New ZipArchive(input, ZipArchiveMode.Read, False)
                    Dim manifestEntry = archive.GetEntry("manifest.json")
                    If manifestEntry Is Nothing Then Throw New InvalidDataException("Backup manifest is missing.")
                    Dim manifest As BackupPayloadManifest
                    Using manifestStream = manifestEntry.Open()
                        manifest = JsonSerializer.Deserialize(Of BackupPayloadManifest)(manifestStream, SerializerOptions)
                    End Using
                    If manifest Is Nothing OrElse manifest.FormatVersion <> 1 OrElse
                       String.IsNullOrWhiteSpace(manifest.BackupId) OrElse manifest.PayloadHashes Is Nothing Then
                        Throw New InvalidDataException("Backup manifest is invalid or unsupported.")
                    End If
                    If Not manifest.PayloadHashes.ContainsKey("historian/eoems_historian.db") Then
                        Throw New InvalidDataException("Backup does not contain the required historian database.")
                    End If
                    For Each payload In manifest.PayloadHashes
                        cancellationToken.ThrowIfCancellationRequested()
                        Dim entry = archive.GetEntry(payload.Key)
                        If entry Is Nothing Then Throw New InvalidDataException("Backup payload is missing: " & payload.Key)
                        Dim digest As String
                        Using source = entry.Open()
                            digest = HashStream(source)
                        End Using
                        If Not String.Equals(digest, payload.Value, StringComparison.OrdinalIgnoreCase) Then
                            Throw New InvalidDataException("Backup payload checksum failed: " & payload.Key)
                        End If
                    Next

                    cancellationToken.ThrowIfCancellationRequested()
                    Dim databaseEntry = archive.GetEntry("historian/eoems_historian.db")
                    Dim databasePath = Path.Combine(workFolder, "historian.db")
                    Using source = databaseEntry.Open()
                        Using destination As New FileStream(databasePath, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                            source.CopyTo(destination)
                        End Using
                    End Using
                    Dim integrity = CheckDatabaseIntegrity(databasePath)
                    If Not String.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase) Then
                        Throw New InvalidDataException("Backup historian failed SQLite integrity_check: " & integrity)
                    End If
                    Return New BackupVerificationResult With {
                        .IsValid = True,
                        .FilePath = backupPath,
                        .Message = "Manifest, payload checksums, and SQLite integrity_check passed.",
                        .CreatedAtUtc = manifest.CreatedAtUtc,
                        .DatabaseIntegrity = integrity,
                        .PayloadEntries = manifest.PayloadHashes.Keys.OrderBy(Function(name) name, StringComparer.Ordinal).ToArray(),
                        .SizeBytes = size
                    }
                End Using
            End Using
        Finally
            If Directory.Exists(workFolder) Then Directory.Delete(workFolder, True)
        End Try
    End Function

    Private Shared Function CheckDatabaseIntegrity(databasePath As String) As String
        Dim builder As New SqliteConnectionStringBuilder With {
            .DataSource = databasePath,
            .Mode = SqliteOpenMode.ReadOnly,
            .Cache = SqliteCacheMode.Private,
            .Pooling = False
        }
        Using connection As New SqliteConnection(builder.ToString())
            connection.Open()
            Using command = connection.CreateCommand()
                command.CommandText = "PRAGMA integrity_check;"
                Dim result = CStr(command.ExecuteScalar())
                Return If(result, String.Empty)
            End Using
        End Using
    End Function

    Private Shared Function PendingRestorePath(baseDirectory As String) As String
        Return Path.Combine(Path.GetFullPath(baseDirectory), "EOEMSData", "Configuration", "pending-restore.json")
    End Function

    Private Shared Sub SavePendingRestore(path As String, pending As PendingRestore)
        Dim folder = System.IO.Path.GetDirectoryName(path)
        Directory.CreateDirectory(folder)
        Dim temporary = path & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Try
            File.WriteAllText(temporary, JsonSerializer.Serialize(pending, SerializerOptions), New UTF8Encoding(False))
            File.Move(temporary, path, True)
        Finally
            If File.Exists(temporary) Then File.Delete(temporary)
        End Try
    End Sub

    Private Shared Sub ExtractRestorePayload(backupPath As String,
                                             destinationFolder As String,
                                             cancellationToken As CancellationToken)
        Dim verification = VerifyBackupCore(Path.GetFullPath(backupPath), cancellationToken)
        If Not verification.IsValid Then Throw New InvalidDataException("Restore source did not pass verification.")
        If Directory.Exists(destinationFolder) Then Directory.Delete(destinationFolder, True)
        Dim historianFolder = Path.Combine(destinationFolder, "historian")
        Dim logsFolder = Path.Combine(destinationFolder, "MeterLogs")
        Directory.CreateDirectory(historianFolder)
        Directory.CreateDirectory(logsFolder)

        Using input As New FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read)
            Using archive As New ZipArchive(input, ZipArchiveMode.Read, False)
                For Each payloadName In verification.PayloadEntries
                    cancellationToken.ThrowIfCancellationRequested()
                    Dim destinationPath As String
                    If String.Equals(payloadName, "historian/eoems_historian.db", StringComparison.Ordinal) Then
                        destinationPath = Path.Combine(historianFolder, "eoems_historian.db")
                    ElseIf payloadName.StartsWith("MeterLogs/", StringComparison.Ordinal) Then
                        Dim relative = payloadName.Substring("MeterLogs/".Length)
                        Dim segments = relative.Split("/"c)
                        Dim unsafePath = segments.Length = 0
                        For Each segment In segments
                            If String.IsNullOrWhiteSpace(segment) OrElse segment = "." OrElse segment = ".." OrElse
                               segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 Then
                                unsafePath = True
                                Exit For
                            End If
                        Next
                        If unsafePath Then
                            Throw New InvalidDataException("Restore archive contains an unsafe MeterLogs path.")
                        End If
                        destinationPath = Path.GetFullPath(Path.Combine(logsFolder, relative.Replace("/"c, Path.DirectorySeparatorChar)))
                        Dim logsPrefix = Path.GetFullPath(logsFolder) & Path.DirectorySeparatorChar
                        If Not destinationPath.StartsWith(logsPrefix, StringComparison.OrdinalIgnoreCase) Then
                            Throw New InvalidDataException("Restore archive contains a path outside MeterLogs.")
                        End If
                    Else
                        Continue For
                    End If

                    Dim entry = archive.GetEntry(payloadName)
                    If entry Is Nothing Then Throw New InvalidDataException("Restore payload is missing: " & payloadName)
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath))
                    Using source = entry.Open()
                        Using destination As New FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                            source.CopyTo(destination)
                        End Using
                    End Using
                Next
            End Using
        End Using

        Dim databasePath = Path.Combine(historianFolder, "eoems_historian.db")
        If Not File.Exists(databasePath) OrElse
           Not String.Equals(CheckDatabaseIntegrity(databasePath), "ok", StringComparison.OrdinalIgnoreCase) Then
            Throw New InvalidDataException("Staged restore historian failed SQLite integrity_check.")
        End If
    End Sub

    Private Shared Sub ApplyStagedTargets(stagedFolder As String, baseDirectory As String, transactionId As String)
        Dim stagedDatabase = Path.Combine(stagedFolder, "historian", "eoems_historian.db")
        If Not File.Exists(stagedDatabase) OrElse
           Not String.Equals(CheckDatabaseIntegrity(stagedDatabase), "ok", StringComparison.OrdinalIgnoreCase) Then
            Throw New InvalidDataException("Staged historian is missing or failed SQLite integrity_check.")
        End If
        Dim stagedLogs = Path.Combine(stagedFolder, "MeterLogs")
        If Not Directory.Exists(stagedLogs) Then Throw New InvalidDataException("Staged MeterLogs directory is missing.")

        Dim targetDatabase = Path.Combine(baseDirectory, "EOEMSData", "Historian", "eoems_historian.db")
        Directory.CreateDirectory(Path.GetDirectoryName(targetDatabase))
        Dim temporaryDatabase = targetDatabase & "." & transactionId & ".restore"
        Try
            SqliteConnection.ClearAllPools()
            For Each suffix In {"-wal", "-shm", "-journal"}
                Dim sidecarPath = targetDatabase & suffix
                If File.Exists(sidecarPath) Then File.Delete(sidecarPath)
            Next
            File.Copy(stagedDatabase, temporaryDatabase, True)
            If Not String.Equals(CheckDatabaseIntegrity(temporaryDatabase), "ok", StringComparison.OrdinalIgnoreCase) Then
                Throw New InvalidDataException("Temporary historian failed SQLite integrity_check.")
            End If
            File.Move(temporaryDatabase, targetDatabase, True)
        Finally
            If File.Exists(temporaryDatabase) Then File.Delete(temporaryDatabase)
        End Try

        ReplaceMeterLogs(stagedLogs, Path.Combine(baseDirectory, "data", "MeterLogs"), transactionId)
    End Sub

    Private Shared Sub ReplaceMeterLogs(sourceFolder As String, targetFolder As String, transactionId As String)
        Dim parent = Path.GetDirectoryName(targetFolder)
        Directory.CreateDirectory(parent)
        Dim stagedFolder = Path.Combine(parent, ".MeterLogs.restore-" & transactionId)
        Dim previousFolder = Path.Combine(parent, ".MeterLogs.previous-" & transactionId)
        If Directory.Exists(stagedFolder) Then Directory.Delete(stagedFolder, True)
        If Directory.Exists(previousFolder) Then Directory.Delete(previousFolder, True)
        CopyDirectory(sourceFolder, stagedFolder)
        If Directory.Exists(targetFolder) Then Directory.Move(targetFolder, previousFolder)
        Try
            Directory.Move(stagedFolder, targetFolder)
        Catch
            If Not Directory.Exists(targetFolder) AndAlso Directory.Exists(previousFolder) Then
                Directory.Move(previousFolder, targetFolder)
            End If
            Throw
        End Try
        If Directory.Exists(previousFolder) Then Directory.Delete(previousFolder, True)
    End Sub

    Private Shared Sub CopyDirectory(sourceFolder As String, destinationFolder As String)
        Directory.CreateDirectory(destinationFolder)
        For Each directoryPath In Directory.EnumerateDirectories(sourceFolder, "*", SearchOption.AllDirectories)
            Directory.CreateDirectory(Path.Combine(destinationFolder, Path.GetRelativePath(sourceFolder, directoryPath)))
        Next
        For Each filePath In Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories)
            Dim destinationPath = Path.Combine(destinationFolder, Path.GetRelativePath(sourceFolder, filePath))
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath))
            File.Copy(filePath, destinationPath, False)
        Next
    End Sub

    Private Shared Sub ApplyArchiveToTargets(backupPath As String,
                                             baseDirectory As String,
                                             stagedFolder As String,
                                             transactionId As String)
        Dim verification = VerifyBackupCore(Path.GetFullPath(backupPath), CancellationToken.None)
        If Not verification.IsValid Then Throw New InvalidDataException("Rollback archive failed verification.")
        ExtractRestorePayload(backupPath, stagedFolder, CancellationToken.None)
        ApplyStagedTargets(stagedFolder, baseDirectory, transactionId)
    End Sub

    Private Shared Sub CompletePendingRestore(pendingPath As String, transactionRoot As String)
        If Directory.Exists(transactionRoot) Then Directory.Delete(transactionRoot, True)
        If File.Exists(pendingPath) Then File.Delete(pendingPath)
    End Sub

    Private Sub ApplyRetentionPolicy(settings As BackupSettings, backupFolder As String, newBackupPath As String)
        If settings.RetentionDays = 0 Then Return
        Dim cutoffUtc = DateTime.UtcNow.AddDays(-settings.RetentionDays)
        For Each artifact In GetRecentBackups(500)
            If String.Equals(artifact.FilePath, newBackupPath, StringComparison.OrdinalIgnoreCase) OrElse
               artifact.LastWriteUtc >= cutoffUtc Then Continue For
            Dim verification = VerifyBackupCore(artifact.FilePath, CancellationToken.None)
            If verification.IsValid Then File.Delete(artifact.FilePath)
        Next
    End Sub

    Private Function LoadState() As BackupState
        If Not File.Exists(_statePath) Then Return New BackupState()
        Try
            Dim state = JsonSerializer.Deserialize(Of BackupState)(File.ReadAllText(_statePath), SerializerOptions)
            If state Is Nothing Then Throw New InvalidDataException("Backup state file is empty.")
            Return state
        Catch ex As JsonException
            Throw New InvalidDataException("Backup state is malformed: " & ex.Message, ex)
        Catch ex As IOException
            Throw New InvalidDataException("Backup state could not be read: " & ex.Message, ex)
        End Try
    End Function

    Private Sub SaveState(state As BackupState)
        Dim folder = Path.GetDirectoryName(_statePath)
        Directory.CreateDirectory(folder)
        Dim temporary = _statePath & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Try
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, SerializerOptions), New UTF8Encoding(False))
            File.Move(temporary, _statePath, True)
        Finally
            If File.Exists(temporary) Then File.Delete(temporary)
        End Try
    End Sub

    Private Sub SetLastError(message As String)
        SyncLock _settingsGate
            _lastError = If(message, String.Empty)
        End SyncLock
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Task.Run(Function() StopAsync()).GetAwaiter().GetResult()
        _operationGate.Dispose()
    End Sub
End Class
