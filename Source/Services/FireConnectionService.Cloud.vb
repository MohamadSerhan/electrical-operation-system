Imports System.IO
Imports System.Security.Cryptography
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Microsoft.Data.Sqlite

Partial Public NotInheritable Class FireConnectionService
    Private ReadOnly _cloudClient As ZetaCloudEventClient
    Private ReadOnly _cloudHistory As ZetaCloudEventHistory
    Private _cloudNotBeforeUtc As DateTimeOffset = DateTimeOffset.MinValue
    Private _mapEventEvidence As IReadOnlyList(Of FireMapEventEvidence) = Array.Empty(Of FireMapEventEvidence)()
    Private _mapAlerts As FireMapAlertStore
    Private _mapAlertsError As String = ""
    Private _mapLocationEvidence As IReadOnlyDictionary(Of String, IReadOnlyList(Of FireMapEventEvidence)) =
        New Dictionary(Of String, IReadOnlyList(Of FireMapEventEvidence))

    Friend Function ReadMapLocationEvidence() As IReadOnlyList(Of FireMapEventEvidence)
        Dim snapshot = Threading.Volatile.Read(_mapLocationEvidence)
        Dim result As IReadOnlyList(Of FireMapEventEvidence) = Nothing
        Return If(snapshot.TryGetValue(FireMapAlertStore.Scope(ReadSettings()), result), result, Array.Empty(Of FireMapEventEvidence)())
    End Function

    Private Sub PublishMapLocationEvidence(settings As FireConnectionSettings, records As IReadOnlyList(Of ZetaFireEvent))
        Dim evidence As IReadOnlyList(Of FireMapEventEvidence) = Array.AsReadOnly(records.Where(Function(record) record.SiteId = settings.CloudSiteIdentifier AndAlso
            record.TimestampUtc.HasValue AndAlso record.TimestampUtc.Value <= DateTimeOffset.UtcNow).
            DistinctBy(Function(record) FireEventIdentity.Key(record)).Select(Function(record) New FireMapEventEvidence(record)).ToArray())
        Dim snapshot As IReadOnlyDictionary(Of String, IReadOnlyList(Of FireMapEventEvidence)) =
            New Dictionary(Of String, IReadOnlyList(Of FireMapEventEvidence)) From {{FireMapAlertStore.Scope(settings), evidence}}
        Threading.Volatile.Write(_mapLocationEvidence, snapshot)
    End Sub

    Public Function ReadLatchedMapEvents() As IReadOnlyList(Of FireMapEventEvidence)
        If _mapAlerts Is Nothing Then Throw New InvalidDataException("Map alert storage unavailable: " & _mapAlertsError)
        Dim settings = ReadSettings()
        If settings.ConnectionType <> FireConnectionType.ZetaCloud Then Return Array.Empty(Of FireMapEventEvidence)()
        Return _mapAlerts.Read(settings)
    End Function

    Public Sub ResetMapAlerts(session As UserSession, acknowledged As IReadOnlyList(Of FireMapEventEvidence))
        If _mapAlerts Is Nothing Then Throw New InvalidDataException("Map alert storage unavailable: " & _mapAlertsError)
        _mapAlerts.Reset(ReadSettings(), session, acknowledged)
    End Sub

    Public Async Function PrepareMapEventHistoryAsync() As Task
        Dim settings = ReadSettings()
        If settings.ConnectionType <> FireConnectionType.ZetaCloud Then Return
        If _mapAlerts Is Nothing Then Throw New InvalidDataException("Map alert storage unavailable: " & _mapAlertsError)
        If Not File.Exists(_store.FilePath & ".cloud-events.db") Then Return
        Await Task.Run(Sub()
                           _lifetime.Token.ThrowIfCancellationRequested()
                           Dim stored = _cloudHistory.Read(settings.CloudSiteIdentifier, 10000)
                           _lifetime.Token.ThrowIfCancellationRequested()
                           _mapAlerts.Observe(settings, stored)
                           PublishMapLocationEvidence(settings, stored)
                       End Sub, _lifetime.Token).ConfigureAwait(False)
    End Function

    Public Function ReadMapEventEvidence() As IReadOnlyList(Of FireMapEventEvidence)
        Return Threading.Volatile.Read(_mapEventEvidence)
    End Function

    Public Function ReadCloudEvents(site As String, Optional limit As Integer = 200) As IReadOnlyList(Of ZetaFireEvent)
        Return _cloudHistory.Read(site, limit)
    End Function

    Public Function CloudEventCount(site As String) As Long
        Return _cloudHistory.Count(site)
    End Function

    Public Async Function SignInCloudAsync(settings As FireConnectionSettings, email As String, password As String,
                                          session As UserSession, Optional cancellation As CancellationToken = Nothing) As Task(Of ZetaCloudResult)
        FireConnectionStore.RequireAdministrator(session)
        RequireCommunication()
        Dim candidate = settings.Copy()
        candidate.Validate()
        If candidate.ConnectionType <> FireConnectionType.ZetaCloud Then Throw New InvalidOperationException("Select Cloud before signing in to Zeta.")
        ZetaCloudEventClient.ValidateEndpoint(candidate)
        If Not Await _commands.WaitAsync(0, cancellation).ConfigureAwait(False) Then Throw New InvalidOperationException("Another fire connection command is running.")
        Try
            If DateTimeOffset.UtcNow < _cloudNotBeforeUtc Then Throw New InvalidOperationException("Cloud rate limiting is active; wait before signing in.")
            Using linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellation)
                Await StopWorkerAsync().ConfigureAwait(False)
                SetStopped("OFFLINE", "Manual Zeta sign-in requested. Monitoring stopped; live fire panel condition UNKNOWN.")
                Dim result As ZetaCloudResult
                Await _communicationGate.WaitAsync(linked.Token).ConfigureAwait(False)
                Try
                    result = Await _cloudClient.LoginAsync(candidate, email, password,
                        Function(token) SaveSettingsCoreAsync(candidate, "", "", False, session, False, token, False, email, password), linked.Token).ConfigureAwait(False)
                Finally
                    email = Nothing
                    password = Nothing
                    _communicationGate.Release()
                End Try
                If Not result.Success Then
                    If result.HttpStatus = 429 Then
                        Dim now = DateTimeOffset.UtcNow
                        _cloudNotBeforeUtc = now.AddSeconds(Math.Min((DateTimeOffset.MaxValue - now).TotalSeconds - 1, Math.Max(30, result.RetryAfterSeconds)))
                    End If
                    SyncLock _gate
                        _snapshot.HttpStatus = result.HttpStatus
                        _snapshot.CloudStage = result.Stage
                        _snapshot.CloudErrorOrigin = result.ErrorOrigin
                        _snapshot.CloudServerErrorCategory = result.ServerErrorCategory
                        _snapshot.CloudTransportSucceeded = result.TransportSucceeded
                        _snapshot.CloudCredentialRenewalRequired = True
                        _snapshot.CloudAuthentication = "LOGIN NOT ACCEPTED"
                        _snapshot.LastError = result.ErrorMessage
                        _snapshot.TransportHealth = "DISCONNECTED"
                    End SyncLock
                    Record("Manual Zeta login: " & result.Stage & "; " & result.ErrorMessage)
                    Return result
                End If
                Record("Zeta login and session saved with Windows DPAPI for automatic recovery. Claims/event access not yet verified.")
                Dim synchronized = Await RequestCloudAsync(candidate, False, True, linked.Token).ConfigureAwait(False)
                If candidate.AutoConnect AndAlso Not synchronized.CredentialRenewalRequired Then StartWorker()
                Return synchronized
            End Using
        Finally
            email = Nothing
            password = Nothing
            _commands.Release()
        End Try
    End Function

    Public Async Function TestCloudAsync(settings As FireConnectionSettings, connectivityOnly As Boolean, retrieveEvents As Boolean,
                                        session As UserSession, Optional cancellation As CancellationToken = Nothing) As Task(Of ZetaCloudResult)
        FireConnectionStore.RequireAdministrator(session)
        RequireCommunication()
        Dim candidate = settings.Copy()
        candidate.Validate()
        If candidate.ConnectionType <> FireConnectionType.ZetaCloud Then Throw New InvalidOperationException("Select the cloud connection for this test.")
        If Not Await _commands.WaitAsync(0, cancellation).ConfigureAwait(False) Then Throw New InvalidOperationException("Another fire connection command is running.")
        Try
            Using linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellation)
                Return Await RequestCloudAsync(candidate, connectivityOnly, retrieveEvents, linked.Token).ConfigureAwait(False)
            End Using
        Finally
            _commands.Release()
        End Try
    End Function

    Private Async Function RequestCloudAsync(settings As FireConnectionSettings, connectivityOnly As Boolean, retrieveEvents As Boolean,
                                            cancellation As CancellationToken) As Task(Of ZetaCloudResult)
        Await _communicationGate.WaitAsync(cancellation).ConfigureAwait(False)
        Try
            If DateTimeOffset.UtcNow < _cloudNotBeforeUtc Then Throw New InvalidOperationException("Cloud rate limiting is active until " & _cloudNotBeforeUtc.ToString("u") & ". No request was sent.")
            Dim token As String = String.Empty
            Dim result As ZetaCloudResult = Nothing
            Dim document As FireConnectionDocument = Nothing
            If Not connectivityOnly Then
                Try
                    document = Await Task.Run(Function() _store.Load(), cancellation).ConfigureAwait(False)
                    token = FireConnectionStore.Unprotect(document.ProtectedCloudToken)
                Catch ex As Exception When TypeOf ex Is CryptographicException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse
                    TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is FormatException
                    result = New ZetaCloudResult With {.Stage = "LOCAL CREDENTIAL STORAGE", .ErrorOrigin = "LOCAL", .CredentialRenewalRequired = True,
                        .ErrorMessage = "Saved login session token could not be loaded with this Windows account. No HTTP request sent; replace the token or correct protected storage access."}
                End Try
            End If
            If result Is Nothing Then result = Await _cloudClient.GetAsync(settings, token, connectivityOnly, cancellation).ConfigureAwait(False)
            Dim canRenew = Not connectivityOnly AndAlso settings.AutoReconnect AndAlso document IsNot Nothing AndAlso
                document.ProtectedCloudEmail.Length > 0 AndAlso document.ProtectedCloudPassword.Length > 0
            Dim expired = result.CredentialRenewalRequired AndAlso
                (result.HttpStatus = 401 OrElse (result.HttpStatus = 403 AndAlso
                 {"expired_token", "token_expired", "jwt_expired", "invalid_token", "tokenexpirederror"}.Contains(result.ServerErrorCategory)) OrElse
                 (token.Length = 0 AndAlso result.ErrorOrigin = "LOCAL"))
            If canRenew AndAlso expired Then
                Dim email = FireConnectionStore.Unprotect(document.ProtectedCloudEmail)
                Dim password = FireConnectionStore.Unprotect(document.ProtectedCloudPassword)
                Try
                    result = Await _cloudClient.LoginAsync(settings, email, password,
                        Function(renewed) Task.Run(Sub() _store.RenewCloudToken(settings, renewed), cancellation), cancellation).ConfigureAwait(False)
                Finally
                    email = Nothing
                    password = Nothing
                End Try
                If result.Success Then
                    _cloudClient.InvalidateAuthorization()
                    token = Await Task.Run(Function() FireConnectionStore.Unprotect(_store.Load().ProtectedCloudToken), cancellation).ConfigureAwait(False)
                    SyncLock _gate
                        _snapshot.HasCloudToken = True
                    End SyncLock
                    result = Await _cloudClient.GetAsync(settings, token, False, cancellation).ConfigureAwait(False)
                End If
                Record("Automatic Zeta login renewal: " & result.Stage & If(result.Success, "; event response validated.", "; " & result.ErrorMessage))
            End If
            If result.HttpStatus = 429 Then
                Dim now = DateTimeOffset.UtcNow
                _cloudNotBeforeUtc = now.AddSeconds(Math.Min((DateTimeOffset.MaxValue - now).TotalSeconds - 1, Math.Max(30, result.RetryAfterSeconds)))
            End If
            If Not connectivityOnly AndAlso result.Success AndAlso retrieveEvents Then
                Try
                    Await Task.Run(Sub() _cloudHistory.Synchronize(result.Events, cancellation), cancellation).ConfigureAwait(False)
                Catch ex As Exception When TypeOf ex Is SqliteException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is CryptographicException
                    result.Success = False
                    result.Stage = "EVENT SYNCHRONIZATION"
                    result.ErrorOrigin = "LOCAL STORAGE"
                    result.ErrorMessage = "Could not persist protected fire event history; check storage access. No successful synchronization is claimed."
                End Try
            End If
            Dim saved = ReadSettings()
            Dim matchesSaved = saved.ConnectionType = FireConnectionType.ZetaCloud AndAlso saved.CloudUrl = settings.CloudUrl AndAlso
                saved.CloudApiEndpoint = settings.CloudApiEndpoint AndAlso saved.CloudSiteIdentifier = settings.CloudSiteIdentifier
            If matchesSaved AndAlso result.Success AndAlso retrieveEvents AndAlso Not connectivityOnly Then
                Try
                    If _mapAlerts Is Nothing Then Throw New InvalidDataException(_mapAlertsError)
                    _mapAlerts.Observe(saved, result.Events)
                    PublishMapLocationEvidence(saved, _cloudHistory.Read(saved.CloudSiteIdentifier, 10000))
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
                    TypeOf ex Is CryptographicException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is SqliteException
                    result.Success = False
                    result.Stage = "MAP ALERT STORAGE"
                    result.ErrorOrigin = "LOCAL STORAGE"
                    result.ErrorMessage = "Fire history was stored, but map alert latching failed: " & ex.Message
                End Try
            End If
            If matchesSaved Then
                SyncLock _gate
                    _snapshot.HttpStatus = result.HttpStatus
                    _snapshot.ResponseMilliseconds = result.ResponseMilliseconds
                    _snapshot.CloudStage = result.Stage
                    _snapshot.CloudErrorOrigin = result.ErrorOrigin
                    _snapshot.CloudServerErrorCategory = result.ServerErrorCategory
                    _snapshot.CloudTransportSucceeded = result.TransportSucceeded
                    If Not connectivityOnly Then _snapshot.CloudSessionExpiresUtc = result.SessionExpiresUtc
                    If Not connectivityOnly AndAlso result.CredentialRenewalRequired Then
                        _snapshot.CloudCredentialRenewalRequired = True
                        _snapshot.CloudAuthentication = If(result.ErrorOrigin = "ZETA HTTP", "AUTHENTICATION FAILED",
                            If(result.ErrorOrigin = "LOCAL", "LOCAL CREDENTIAL REJECTED", "AUTHORIZATION RESPONSE REJECTED"))
                        _snapshot.Monitoring = False
                        _cloudClient.InvalidateAuthorization()
                        _cancellation?.Cancel()
                    ElseIf Not connectivityOnly Then
                        _snapshot.CloudCredentialRenewalRequired = False
                        _snapshot.CloudAuthentication = If(result.Authenticated, "AUTHORIZED RESPONSE VALIDATED", "NOT VERIFIED")
                    End If
                    _snapshot.TransportHealth = If(Not _snapshot.Monitoring, "DISCONNECTED",
                        If(result.TransportSucceeded, "HTTPS RESPONSE", "UNAVAILABLE"))
                    If Not connectivityOnly Then
                        If result.Success Then
                            _snapshot.LastSuccessUtc = DateTime.UtcNow
                            If retrieveEvents Then
                                _snapshot.LastDataUtc = DateTime.UtcNow
                                Threading.Volatile.Write(Of IReadOnlyList(Of FireMapEventEvidence))(_mapEventEvidence, Array.AsReadOnly(result.Events.Where(Function(item)
                                    Return item.TimestampUtc.HasValue AndAlso item.TimestampUtc.Value <= DateTimeOffset.UtcNow
                                End Function).DistinctBy(Function(item) FireEventIdentity.Key(item)).
                                    Select(Function(item) New FireMapEventEvidence(item)).ToArray()))
                                _snapshot.LastEventUtc = result.Events.Where(Function(item) item.TimestampUtc.HasValue).
                                    Select(Function(item) item.TimestampUtc).OrderByDescending(Function(value) value).FirstOrDefault()
                            End If
                            _snapshot.ConsecutiveFailures = 0
                            _snapshot.LastError = String.Empty
                            _snapshot.State = If(retrieveEvents, "ONLINE", If(_snapshot.Monitoring, _snapshot.State, "OFFLINE"))
                            _snapshot.ProtocolHealth = If(retrieveEvents, "VALIDATED EVENT HISTORY - LIVE PANEL UNKNOWN", "AUTHENTICATION TEST ONLY")
                        Else
                            _snapshot.ConsecutiveFailures += 1
                            _snapshot.LastError = result.ErrorMessage
                            _snapshot.State = If(result.CredentialRenewalRequired,
                                _snapshot.CloudAuthentication,
                                If(_snapshot.LastDataUtc.HasValue, "DEGRADED", "OFFLINE"))
                            _snapshot.ProtocolHealth = result.Stage
                        End If
                    End If
                End SyncLock
            End If
            Record("Cloud " & If(connectivityOnly, "HTTPS test", If(retrieveEvents, "event synchronization", "authentication test")) &
                ": " & result.Stage & If(result.HttpStatus.HasValue, " HTTP " & result.HttpStatus.Value.ToString(Globalization.CultureInfo.InvariantCulture), "") &
                If(result.Success AndAlso Not connectivityOnly, "; successful. Live fire panel condition remains UNKNOWN.", "; " & result.ErrorMessage))
            Return result
        Finally
            _communicationGate.Release()
        End Try
    End Function

    Friend Shared Function CloudDelaySeconds(settings As FireConnectionSettings, failures As Integer, retryAfter As Double) As Double
        Return Math.Max(Math.Max(30, settings.PollSeconds), Math.Max(retryAfter,
            If(failures = 0, 0, Math.Min(900, 30 * Math.Pow(2, Math.Min(5, failures - 1))))))
    End Function

    Private Async Function MonitorCloudAsync(settings As FireConnectionSettings, cancellation As CancellationToken) As Task
        Try
            While True
                Dim result As ZetaCloudResult
                Try
                    result = Await RequestCloudAsync(settings, False, True, cancellation).ConfigureAwait(False)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is CryptographicException OrElse
                    TypeOf ex Is FormatException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is SqliteException OrElse TypeOf ex Is InvalidOperationException
                    result = New ZetaCloudResult With {.Stage = "LOCAL CONFIGURATION", .ErrorMessage = "Cloud configuration or protected storage is unavailable; reconnect after correcting local settings."}
                    SyncLock _gate
                        _snapshot.State = If(_snapshot.LastDataUtc.HasValue, "DEGRADED", "OFFLINE")
                        _snapshot.LastError = result.ErrorMessage
                        _snapshot.ConsecutiveFailures += 1
                        _snapshot.CloudStage = result.Stage
                        _snapshot.CloudErrorOrigin = "LOCAL"
                        _snapshot.TransportHealth = "DISCONNECTED"
                    End SyncLock
                    Record(result.ErrorMessage)
                End Try
                If Not result.Success AndAlso (Not settings.AutoReconnect OrElse result.CredentialRenewalRequired) Then
                    SyncLock _gate
                        _snapshot.Monitoring = False
                        _snapshot.TransportHealth = "DISCONNECTED"
                    End SyncLock
                    Record("Cloud polling stopped. Correct authorization/settings and use Reconnect. Live fire panel condition is UNKNOWN.")
                    Return
                End If
                Dim delay = CloudDelaySeconds(settings, ReadStatus().ConsecutiveFailures, result.RetryAfterSeconds)
                While delay > 0
                    Dim segment = Math.Min(86400, delay)
                    Await Task.Delay(TimeSpan.FromSeconds(segment), cancellation).ConfigureAwait(False)
                    delay -= segment
                End While
            End While
        Catch ex As OperationCanceledException When cancellation.IsCancellationRequested
            Return
        End Try
    End Function

    Public Async Function ClearDiagnosticLogsAsync(session As UserSession) As Task
        FireConnectionStore.RequireAdministrator(session)
        Await _commands.WaitAsync().ConfigureAwait(False)
        Try
            Await _communicationGate.WaitAsync(_lifetime.Token).ConfigureAwait(False)
            Try
                Await Task.Run(Sub()
                                   SyncLock _gate
                                       File.WriteAllText(_store.FilePath & ".events.jsonl", String.Empty)
                                       If File.Exists(_store.FilePath & ".events.jsonl.previous") Then File.Delete(_store.FilePath & ".events.jsonl.previous")
                                       _snapshot.Events = Array.Empty(Of FireConnectionEvent)()
                                       _snapshot.LastTest = Nothing
                                       _snapshot.LastTestResult = String.Empty
                                       _snapshot.EventLogError = String.Empty
                                       _eventReadError = String.Empty
                                   End SyncLock
                               End Sub).ConfigureAwait(False)
            Finally
                _communicationGate.Release()
            End Try
            Record("Diagnostic records cleared by operator. Persistent fire event history was not changed.")
        Finally
            _commands.Release()
        End Try
    End Function
End Class
