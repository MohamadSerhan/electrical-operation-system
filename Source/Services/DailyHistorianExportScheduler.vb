Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Data.Sqlite

Public NotInheritable Class DailyExportJob
    Public Property AnalyzerId As String = String.Empty
    Public Property LocalDay As Date
    Public Property Status As String = String.Empty
    Public Property OutputPath As String = String.Empty
    Public Property StartedAtUtc As DateTime?
    Public Property CompletedAtUtc As DateTime?
    Public Property ErrorMessage As String = String.Empty
End Class

Public NotInheritable Class DailyHistorianExportScheduler
    Implements IDisposable

    Private ReadOnly _historian As MeasurementHistorian
    Private ReadOnly _jobs As New DailyExportJobStore()
    Private ReadOnly _gate As New Object()
    Private ReadOnly _outputRootDirectory As String
    Private _settings As RecordingSettings
    Private _lastRunUtc As DateTime?
    Private _lastError As String = String.Empty
    Private _shutdown As CancellationTokenSource
    Private _worker As Task
    Private _legacyCleaned As Boolean

    ' The old per-analyzer long-format workbooks are replaced by the grouped daily reports.
    Private Shared Sub DeleteLegacyAnalyzerExcel(exportRoot As String)
        For Each analyzerId In {"T1", "T2", "T3", "T4", "TOTAL"}
            Try
                Dim directory = Path.Combine(exportRoot, analyzerId, "DailyExcel")
                If Not IO.Directory.Exists(directory) Then Continue For
                For Each file In IO.Directory.GetFiles(directory, "EOEMS_" & analyzerId & "_*.xlsx*")
                    IO.File.Delete(file)
                Next
                If Not IO.Directory.EnumerateFileSystemEntries(directory).Any() Then IO.Directory.Delete(directory)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
        Next
    End Sub

    Public Sub New(historian As MeasurementHistorian,
                   settings As RecordingSettings,
                   Optional outputRootDirectory As String = Nothing)
        If historian Is Nothing Then Throw New ArgumentNullException(NameOf(historian))
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        settings.Validate()
        _historian = historian
        _settings = settings
        _outputRootDirectory = If(String.IsNullOrWhiteSpace(outputRootDirectory), Nothing, Path.GetFullPath(outputRootDirectory))
        _jobs.Initialize(historian.DatabasePath)
    End Sub

    Public ReadOnly Property LastRunUtc As DateTime?
        Get
            SyncLock _gate
                Return _lastRunUtc
            End SyncLock
        End Get
    End Property

    Public ReadOnly Property LastError As String
        Get
            SyncLock _gate
                Return _lastError
            End SyncLock
        End Get
    End Property

    Public Sub UpdateSettings(settings As RecordingSettings)
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        settings.Validate()
        SyncLock _gate
            _settings = settings
        End SyncLock
    End Sub

    Public Function GetRecentJobs(Optional limit As Integer = 20) As IReadOnlyList(Of DailyExportJob)
        Return _jobs.GetRecent(limit)
    End Function

    Public Sub Start()
        SyncLock _gate
            If _worker IsNot Nothing Then Return
            _shutdown = New CancellationTokenSource()
            _worker = Task.Run(Function() RunLoopAsync(_shutdown.Token))
        End SyncLock
    End Sub

    Public Async Function StopAsync() As Task
        Dim worker As Task
        SyncLock _gate
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
        SyncLock _gate
            _shutdown.Dispose()
            _shutdown = Nothing
            _worker = Nothing
        End SyncLock
    End Function

    Public Async Function RunDueExportsAsync(Optional cancellationToken As CancellationToken = Nothing) As Task
        Dim settings As RecordingSettings
        SyncLock _gate
            settings = _settings
        End SyncLock
        If Not settings.AutomaticDailyExcelEnabled Then Return

        Dim timezone = settings.ResolveFactoryTimeZone()
        If Not _legacyCleaned Then
            _legacyCleaned = True
            DeleteLegacyAnalyzerExcel(If(_outputRootDirectory, settings.ResolveExportRootDirectory()))
        End If
        Dim localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timezone)
        Dim latestClosedDay = localNow.Date.AddDays(-1)
        If localNow.TimeOfDay < TimeSpan.FromMinutes(settings.DailyExportStabilizationMinutes) Then
            latestClosedDay = latestClosedDay.AddDays(-1)
        End If

        Dim earliestUtc = _historian.GetEarliestSampleUtc()
        If Not earliestUtc.HasValue Then
            SetRunState(DateTime.UtcNow, String.Empty)
            Return
        End If

        Dim firstLocalDay = TimeZoneInfo.ConvertTimeFromUtc(earliestUtc.Value, timezone).Date
        Dim runError = String.Empty
        Dim dayCount = CInt((latestClosedDay - firstLocalDay).TotalDays)
        If dayCount < 0 Then
            SetRunState(DateTime.UtcNow, String.Empty)
            Return
        End If
        Dim resolvedJobs = _jobs.GetResolvedKeys(firstLocalDay, latestClosedDay)
        For dayOffset = 0 To dayCount
            Dim localDay = firstLocalDay.AddDays(dayOffset)
            cancellationToken.ThrowIfCancellationRequested()
            For Each analyzerId In DailyGroupReports.Groups
                cancellationToken.ThrowIfCancellationRequested()
                Dim jobKey = DailyExportJobStore.GetJobKey(analyzerId, localDay)
                If resolvedJobs.Contains(jobKey) Then Continue For
                Dim exportRoot = If(_outputRootDirectory, settings.ResolveExportRootDirectory())
                Dim existingPath = DailyGroupReports.GetPath(exportRoot, analyzerId, localDay)
                If File.Exists(existingPath) Then
                    Try
                        DailyGroupReports.Validate(existingPath)
                        _jobs.MarkExistingSucceeded(analyzerId, localDay, existingPath)
                        resolvedJobs.Add(jobKey)
                    Catch ex As Exception When TypeOf ex Is IOException OrElse
                                               TypeOf ex Is UnauthorizedAccessException OrElse
                                               TypeOf ex Is System.Security.SecurityException OrElse
                                               TypeOf ex Is System.Xml.XmlException
                        _jobs.MarkExistingFailed(analyzerId, localDay, existingPath, ex.Message)
                        runError = analyzerId & " " & localDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) & ": " & ex.Message
                    End Try
                    Continue For
                End If
                Dim dayStart = TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(localDay, DateTimeKind.Unspecified), timezone)
                Dim dayEnd = TimeZoneInfo.ConvertTimeToUtc(
                    DateTime.SpecifyKind(localDay.AddDays(1), DateTimeKind.Unspecified), timezone)
                If Not DailyGroupReports.HasData(analyzerId, dayStart, dayEnd) Then
                    _jobs.MarkSkippedEmpty(analyzerId, localDay, "No saved readings in this local day.")
                    resolvedJobs.Add(jobKey)
                    Continue For
                End If
                If Not _jobs.TryBegin(analyzerId, localDay) Then Continue For

                Try
                    Dim outputPath = Await Task.Run(
                        Function() DailyGroupReports.Export(analyzerId, localDay, timezone, exportRoot, cancellationToken), cancellationToken).ConfigureAwait(False)
                    _jobs.MarkSucceeded(analyzerId, localDay, outputPath)
                    resolvedJobs.Add(jobKey)
                Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                    _jobs.MarkFailed(analyzerId, localDay, "Daily export was cancelled before completion.")
                    Throw
                Catch ex As Exception
                    _jobs.MarkFailed(analyzerId, localDay, ex.Message)
                    runError = analyzerId & " " & localDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) & ": " & ex.Message
                End Try
            Next
        Next
        SetRunState(DateTime.UtcNow, runError)
    End Function

    Private Async Function RunLoopAsync(cancellationToken As CancellationToken) As Task
        While Not cancellationToken.IsCancellationRequested
            Try
                Await RunDueExportsAsync(cancellationToken).ConfigureAwait(False)
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Exit While
            Catch ex As Exception
                SetRunState(DateTime.UtcNow, ex.Message)
            End Try
            Await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(False)
        End While
    End Function

    Private Sub SetRunState(timestampUtc As DateTime, errorMessage As String)
        SyncLock _gate
            _lastRunUtc = timestampUtc
            _lastError = If(errorMessage, String.Empty)
        End SyncLock
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Task.Run(Function() StopAsync()).GetAwaiter().GetResult()
    End Sub
End Class

Public NotInheritable Class DailyExportJobStore
    Private ReadOnly _gate As New Object()
    Private _databasePath As String

    Public Sub Initialize(databasePath As String)
        If String.IsNullOrWhiteSpace(databasePath) Then Throw New ArgumentException("Historian database path is required.", NameOf(databasePath))
        _databasePath = Path.GetFullPath(databasePath)
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath))
        Using connection = OpenConnection()
            Using command = connection.CreateCommand()
                command.CommandText =
                    "CREATE TABLE IF NOT EXISTS DailyExportJobs (" &
                    "AnalyzerId TEXT NOT NULL, LocalDay TEXT NOT NULL, Status TEXT NOT NULL, OutputPath TEXT NOT NULL DEFAULT '', " &
                    "StartedAtUtc TEXT NULL, CompletedAtUtc TEXT NULL, ErrorMessage TEXT NOT NULL DEFAULT '', " &
                    "PRIMARY KEY(AnalyzerId, LocalDay));" &
                    "UPDATE DailyExportJobs SET Status='Failed', CompletedAtUtc=$now, " &
                    "ErrorMessage='Previous application session ended during export.' WHERE Status='Running';" &
                    "DELETE FROM DailyExportJobs WHERE AnalyzerId IN ('T1','T2','T3','T4','TOTAL');"
                command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                command.ExecuteNonQuery()
            End Using
        End Using
    End Sub

    Public Function GetRecent(Optional limit As Integer = 20) As IReadOnlyList(Of DailyExportJob)
        If limit < 1 OrElse limit > 1000 Then Throw New ArgumentOutOfRangeException(NameOf(limit))
        Dim result As New List(Of DailyExportJob)()
        SyncLock _gate
            Using connection = OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText =
                        "SELECT AnalyzerId, LocalDay, Status, OutputPath, StartedAtUtc, CompletedAtUtc, ErrorMessage " &
                        "FROM DailyExportJobs ORDER BY LocalDay DESC, AnalyzerId LIMIT $limit;"
                    command.Parameters.AddWithValue("$limit", limit)
                    Using reader = command.ExecuteReader()
                        While reader.Read()
                            result.Add(New DailyExportJob With {
                                .AnalyzerId = reader.GetString(0),
                                .LocalDay = Date.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                                .Status = reader.GetString(2),
                                .OutputPath = reader.GetString(3),
                                .StartedAtUtc = If(reader.IsDBNull(4), Nothing, CType(DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), DateTime?)),
                                .CompletedAtUtc = If(reader.IsDBNull(5), Nothing, CType(DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), DateTime?)),
                                .ErrorMessage = reader.GetString(6)
                            })
                        End While
                    End Using
                End Using
            End Using
        End SyncLock
        Return result.AsReadOnly()
    End Function

    Public Function GetResolvedKeys(startDay As Date, endDay As Date) As HashSet(Of String)
        Dim result As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        If endDay < startDay Then Return result
        SyncLock _gate
            Using connection = OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText =
                        "SELECT AnalyzerId, LocalDay FROM DailyExportJobs WHERE Status IN ('Succeeded', 'SkippedEmpty') " &
                        "AND LocalDay >= $start AND LocalDay <= $end;"
                    command.Parameters.AddWithValue("$start", startDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    command.Parameters.AddWithValue("$end", endDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                    Using reader = command.ExecuteReader()
                        While reader.Read()
                            result.Add(GetJobKey(reader.GetString(0),
                                Date.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture)))
                        End While
                    End Using
                End Using
            End Using
        End SyncLock
        Return result
    End Function

    Public Function TryBegin(analyzerId As String, localDay As Date) As Boolean
        SyncLock _gate
            Using connection = OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText =
                        "INSERT INTO DailyExportJobs(AnalyzerId, LocalDay, Status, OutputPath, StartedAtUtc, CompletedAtUtc, ErrorMessage) " &
                        "VALUES($analyzer, $day, 'Running', '', $now, NULL, '') " &
                        "ON CONFLICT(AnalyzerId, LocalDay) DO UPDATE SET Status='Running', OutputPath='', " &
                        "StartedAtUtc=excluded.StartedAtUtc, CompletedAtUtc=NULL, ErrorMessage='' " &
                        "WHERE DailyExportJobs.Status NOT IN ('Succeeded', 'Running') AND " &
                        "(DailyExportJobs.Status<>'Failed' OR DailyExportJobs.StartedAtUtc <= $retryBefore);"
                    AddJobParameters(command, analyzerId, localDay)
                    command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                    command.Parameters.AddWithValue("$retryBefore", DateTime.UtcNow.AddMinutes(-15).ToString("o", CultureInfo.InvariantCulture))
                    Return command.ExecuteNonQuery() = 1
                End Using
            End Using
        End SyncLock
    End Function

    Public Sub MarkSkippedEmpty(analyzerId As String,
                                localDay As Date,
                                Optional reason As String = "No recorded samples in this local day.")
        Update(analyzerId, localDay,
            "INSERT INTO DailyExportJobs(AnalyzerId, LocalDay, Status, CompletedAtUtc, ErrorMessage) " &
            "VALUES($analyzer, $day, 'SkippedEmpty', $now, $error) " &
            "ON CONFLICT(AnalyzerId, LocalDay) DO UPDATE SET Status='SkippedEmpty', CompletedAtUtc=$now, " &
            "ErrorMessage=$error WHERE DailyExportJobs.Status NOT IN ('Succeeded', 'Running');",
            errorMessage:=If(String.IsNullOrWhiteSpace(reason), "No recorded samples in this local day.", reason))
    End Sub

    Public Sub MarkSucceeded(analyzerId As String, localDay As Date, outputPath As String)
        Update(analyzerId, localDay,
            "UPDATE DailyExportJobs SET Status='Succeeded', OutputPath=$path, CompletedAtUtc=$now, ErrorMessage='' " &
            "WHERE AnalyzerId=$analyzer AND LocalDay=$day AND Status='Running';",
            value:=outputPath, requireMatch:=True)
    End Sub

    Public Sub MarkExistingSucceeded(analyzerId As String, localDay As Date, outputPath As String)
        Update(analyzerId, localDay,
            "INSERT INTO DailyExportJobs(AnalyzerId, LocalDay, Status, OutputPath, StartedAtUtc, CompletedAtUtc, ErrorMessage) " &
            "VALUES($analyzer, $day, 'Succeeded', $path, $now, $now, '') " &
            "ON CONFLICT(AnalyzerId, LocalDay) DO UPDATE SET Status='Succeeded', OutputPath=$path, " &
            "CompletedAtUtc=$now, ErrorMessage='' WHERE DailyExportJobs.Status<>'Running';",
            value:=outputPath)
    End Sub

    Public Sub MarkExistingFailed(analyzerId As String, localDay As Date, outputPath As String, errorMessage As String)
        Update(analyzerId, localDay,
            "INSERT INTO DailyExportJobs(AnalyzerId, LocalDay, Status, OutputPath, StartedAtUtc, CompletedAtUtc, ErrorMessage) " &
            "VALUES($analyzer, $day, 'Failed', $path, $now, $now, $error) " &
            "ON CONFLICT(AnalyzerId, LocalDay) DO UPDATE SET Status='Failed', OutputPath=$path, " &
            "CompletedAtUtc=$now, ErrorMessage=$error WHERE DailyExportJobs.Status<>'Running';",
            value:=outputPath, errorMessage:=errorMessage)
    End Sub

    Public Sub MarkFailed(analyzerId As String, localDay As Date, errorMessage As String)
        Update(analyzerId, localDay,
            "UPDATE DailyExportJobs SET Status='Failed', CompletedAtUtc=$now, ErrorMessage=$error " &
            "WHERE AnalyzerId=$analyzer AND LocalDay=$day AND Status='Running';",
            errorMessage:=If(errorMessage, String.Empty), requireMatch:=True)
    End Sub

    Private Sub Update(analyzerId As String,
                       localDay As Date,
                       sql As String,
                       Optional value As String = Nothing,
                       Optional errorMessage As String = Nothing,
                       Optional requireMatch As Boolean = False)
        SyncLock _gate
            Using connection = OpenConnection()
                Using command = connection.CreateCommand()
                    command.CommandText = sql
                    AddJobParameters(command, analyzerId, localDay)
                    command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                    If value IsNot Nothing Then command.Parameters.AddWithValue("$path", value)
                    If errorMessage IsNot Nothing Then command.Parameters.AddWithValue("$error", errorMessage)
                    Dim affected = command.ExecuteNonQuery()
                    If requireMatch AndAlso affected <> 1 Then
                        Throw New InvalidOperationException("The daily export job state changed before its completion could be recorded.")
                    End If
                End Using
            End Using
        End SyncLock
    End Sub

    Private Shared Sub AddJobParameters(command As SqliteCommand, analyzerId As String, localDay As Date)
        command.Parameters.AddWithValue("$analyzer", analyzerId)
        command.Parameters.AddWithValue("$day", localDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
    End Sub

    Public Shared Function GetJobKey(analyzerId As String, localDay As Date) As String
        Return analyzerId & "|" & localDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    End Function

    Private Function OpenConnection() As SqliteConnection
        If String.IsNullOrWhiteSpace(_databasePath) Then Throw New InvalidOperationException("Daily export job store is not initialized.")
        Dim connection As New SqliteConnection(New SqliteConnectionStringBuilder With {
            .DataSource = _databasePath,
            .Mode = SqliteOpenMode.ReadWriteCreate,
            .Cache = SqliteCacheMode.Shared
        }.ToString())
        connection.Open()
        Using command = connection.CreateCommand()
            command.CommandText = "PRAGMA busy_timeout=5000;"
            command.ExecuteNonQuery()
        End Using
        Return connection
    End Function
End Class
