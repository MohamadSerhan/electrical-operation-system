Imports System.Diagnostics
Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Net.Sockets
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks

Public NotInheritable Class ZetaFireEvent
    Public Property TimestampUtc As DateTimeOffset?
    Public Property EventType As String = String.Empty
    Public Property Zone As String = String.Empty
    Public Property DeviceAddress As String = String.Empty
    Public Property Description As String = String.Empty
    Public Property Message As String = String.Empty
    Public Property Source As String = "Zeta Remote Cloud event history"
    Public Property EventId As String = String.Empty
    Public Property SiteId As String = String.Empty

    Friend Function Copy() As ZetaFireEvent
        Return DirectCast(MemberwiseClone(), ZetaFireEvent)
    End Function
End Class

Public NotInheritable Class ZetaCloudResult
    Public Property Success As Boolean
    Public Property Authenticated As Boolean
    Public Property TransportSucceeded As Boolean
    Public Property CredentialRenewalRequired As Boolean
    Public Property ErrorOrigin As String = String.Empty
    Public Property ServerErrorCategory As String = String.Empty
    Public Property HttpStatus As Integer?
    Public Property ResponseMilliseconds As Double
    Public Property ErrorMessage As String = String.Empty
    Public Property Stage As String = "NOT TESTED"
    Public Property RetryAfterSeconds As Double
    Public Property SessionExpiresUtc As DateTimeOffset?
    Public Property Events As IReadOnlyList(Of ZetaFireEvent) = Array.Empty(Of ZetaFireEvent)()
End Class

Public NotInheritable Class ZetaCloudEventClient
    Implements IDisposable

    Public Const EventRoute As String = "/api//v2/sites/{site_id}/events"
    Public Const AuthorizationRoute As String = "/api//v2/claims/auth"
    Public Const LoginRoute As String = "/api//auth/login"
    Private Const MaximumBodyBytes As Integer = 2 * 1024 * 1024
    Private ReadOnly _http As HttpClient
    Private ReadOnly _requests As New SemaphoreSlim(1, 1)
    Private ReadOnly _clock As TimeProvider
    Private _sessionToken As String = String.Empty
    Private _sessionIdentity As String = String.Empty
    Private _sessionExpires As DateTimeOffset
    Private _authorizationVersion As Integer
    Private _sessionVersion As Integer

    Public Sub New(Optional handler As HttpMessageHandler = Nothing, Optional clock As TimeProvider = Nothing)
        _http = New HttpClient(If(handler, New HttpClientHandler With {.AllowAutoRedirect = False, .UseCookies = False})) With {.Timeout = Threading.Timeout.InfiniteTimeSpan}
        _clock = If(clock, TimeProvider.System)
    End Sub

    Public Sub InvalidateAuthorization()
        Interlocked.Increment(_authorizationVersion)
    End Sub

    Friend Async Function LoginAsync(settings As FireConnectionSettings, email As String, password As String,
                                     saveToken As Func(Of String, Task), cancellation As CancellationToken) As Task(Of ZetaCloudResult)
        ValidateEndpoint(settings)
        If String.IsNullOrWhiteSpace(email) OrElse email.Length > 320 OrElse email.Any(Function(c) Char.IsControl(c)) OrElse
            String.IsNullOrEmpty(password) OrElse password.Length > 4096 Then
            Return New ZetaCloudResult With {.Stage = "LOCAL LOGIN INPUT", .ErrorOrigin = "LOCAL",
                .ErrorMessage = "Enter your Zeta email and password. No login request sent."}
        End If
        Await _requests.WaitAsync(cancellation).ConfigureAwait(False)
        Dim result As New ZetaCloudResult With {.Stage = "LOGIN"}
        Dim watch = Stopwatch.StartNew()
        Try
            Using deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation),
                  request As New HttpRequestMessage(HttpMethod.Post, New Uri(settings.CloudUrl & LoginRoute))
                deadline.CancelAfter(settings.TimeoutMilliseconds)
                request.Headers.Accept.Add(New MediaTypeWithQualityHeaderValue("application/json"))
                request.Content = New StringContent(JsonSerializer.Serialize(New With {.email = email.Trim(), .password = password}),
                    Encoding.UTF8, "application/json")
                email = Nothing
                password = Nothing
                Using response = Await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(False)
                    result.HttpStatus = CInt(response.StatusCode)
                    result.TransportSucceeded = True
                    If response.StatusCode <> HttpStatusCode.OK Then
                        Await SetHttpFailureAsync(result, response, deadline.Token).ConfigureAwait(False)
                        Return result
                    End If
                    Using json = Await ReadJsonAsync(response, 65536, deadline.Token).ConfigureAwait(False)
                        Dim value As JsonElement
                        If json.RootElement.ValueKind <> JsonValueKind.Object OrElse
                            Not json.RootElement.TryGetProperty("token", value) OrElse value.ValueKind <> JsonValueKind.String Then
                            Throw New InvalidDataException("Login response has no session token.")
                        End If
                        Dim token = NormalizeToken(value.GetString())
                        Try
                            result.Stage = "LOGIN TOKEN STORAGE"
                            Await saveToken(token).ConfigureAwait(False)
                        Finally
                            token = Nothing
                        End Try
                    End Using
                End Using
                result.Success = True
                result.Stage = "LOGIN SESSION SAVED"
            End Using
        Catch ex As OperationCanceledException When Not cancellation.IsCancellationRequested
            result.Stage = "LOGIN TIMEOUT"
            result.ErrorOrigin = "TRANSPORT"
            result.ErrorMessage = "Zeta login timed out. No automatic login retry is attempted."
        Catch ex As HttpRequestException
            result.ErrorOrigin = "TRANSPORT"
            result.ErrorMessage = "Zeta login transport failed. Check HTTPS connectivity; no credentials or server body are logged."
        Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is JsonException
            result.ErrorOrigin = "RESPONSE VALIDATION"
            result.ErrorMessage = "Zeta login response was rejected. No valid login session was accepted."
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is CryptographicException
            result.ErrorOrigin = If(result.Stage = "LOGIN TOKEN STORAGE", "LOCAL STORAGE", "TRANSPORT")
            result.ErrorMessage = "Zeta login could not complete protected token storage or response reading. No connection success is claimed."
        Finally
            email = Nothing
            password = Nothing
            result.ResponseMilliseconds = watch.Elapsed.TotalMilliseconds
            _requests.Release()
        End Try
        Return result
    End Function

    Public Shared Sub ValidateToken(token As String)
        NormalizeToken(token)
    End Sub

    Public Shared Function NormalizeToken(token As String) As String
        If token Is Nothing OrElse token.Length > 8192 OrElse token.Any(Function(c) Char.IsControl(c)) Then
            Throw New InvalidDataException("Local credential rejected: token is missing, too long or contains control characters. No HTTP request sent.")
        End If
        token = token.Trim()
        While token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            token = token.Substring(7).Trim()
        End While
        If String.IsNullOrWhiteSpace(token) OrElse token.Equals("Bearer", StringComparison.OrdinalIgnoreCase) OrElse token.Length > 8192 OrElse
            Not Regex.IsMatch(token, "\A[A-Za-z0-9\-._~+/]+=*\z", RegexOptions.CultureInvariant) Then
            Throw New InvalidDataException("Enter an authorized Bearer token without whitespace or control characters.")
        End If
        Return token
    End Function

    Public Shared Function ValidateEndpoint(settings As FireConnectionSettings) As Uri
        If settings.CloudUrl <> "https://www.zetaremote.com" Then Throw New InvalidDataException("Cloud requests are restricted to the approved https://www.zetaremote.com host.")
        If settings.CloudApiEndpoint <> EventRoute Then Throw New InvalidDataException("The observed event route must remain /api//v2/sites/{site_id}/events.")
        If String.IsNullOrWhiteSpace(settings.CloudSiteIdentifier) OrElse
            Not Regex.IsMatch(settings.CloudSiteIdentifier, "\A[A-Za-z0-9_-]{1,128}\z", RegexOptions.CultureInvariant) Then Throw New InvalidDataException("Site ID must contain only letters, digits, underscore or hyphen; verify it with the vendor.")
        Return New Uri(settings.CloudUrl & EventRoute.Replace("{site_id}", settings.CloudSiteIdentifier), UriKind.Absolute)
    End Function

    Public Async Function GetAsync(settings As FireConnectionSettings, token As String, connectivityOnly As Boolean,
                                   cancellation As CancellationToken) As Task(Of ZetaCloudResult)
        Dim uri = ValidateEndpoint(settings)
        If Not connectivityOnly Then
            Try
                token = NormalizeToken(token)
            Catch ex As InvalidDataException
                Return New ZetaCloudResult With {.Stage = "LOCAL CREDENTIAL", .ErrorOrigin = "LOCAL",
                    .CredentialRenewalRequired = True, .ErrorMessage = "Local token missing or malformed. No HTTP request sent; replace the saved login session token."}
            End Try
        End If
        Dim today = _clock.GetUtcNow().UtcDateTime.Date
        uri = New Uri(uri.AbsoluteUri & "?start=" & today.AddDays(-30).ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture) &
            "&end=" & today.ToString("yyyy-MM-dd", Globalization.CultureInfo.InvariantCulture))
        Await _requests.WaitAsync(cancellation).ConfigureAwait(False)
        Try
            Return Await GetCoreAsync(settings, uri, token, connectivityOnly, cancellation).ConfigureAwait(False)
        Finally
            _requests.Release()
        End Try
    End Function

    Private Async Function GetCoreAsync(settings As FireConnectionSettings, uri As Uri, accountToken As String,
                                        connectivityOnly As Boolean, cancellation As CancellationToken) As Task(Of ZetaCloudResult)
        Dim result As New ZetaCloudResult
        Dim watch = Stopwatch.StartNew()
        Using deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation),
              request As New HttpRequestMessage(HttpMethod.Get, uri)
            deadline.CancelAfter(settings.TimeoutMilliseconds)
            request.Headers.Accept.Add(New MediaTypeWithQualityHeaderValue("application/json"))
            Try
                If Not connectivityOnly Then
                    Dim identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountToken))) & ":" & settings.CloudSiteIdentifier
                    Dim version = Volatile.Read(_authorizationVersion)
                    If _sessionIdentity <> identity OrElse _sessionVersion <> version OrElse _sessionExpires <= _clock.GetUtcNow().AddSeconds(30) Then
                        ClearSession()
                        result.Stage = "CLAIMS AUTHORIZATION"
                        Using auth As New HttpRequestMessage(HttpMethod.Post, New Uri(settings.CloudUrl & AuthorizationRoute))
                            auth.Headers.Accept.Add(New MediaTypeWithQualityHeaderValue("application/json"))
                            auth.Headers.Authorization = New AuthenticationHeaderValue("Bearer", accountToken)
                            auth.Content = New StringContent("{}", Encoding.UTF8, "application/json")
                            Using response = Await _http.SendAsync(auth, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(False)
                                result.HttpStatus = CInt(response.StatusCode)
                                result.TransportSucceeded = True
                                If response.StatusCode <> HttpStatusCode.OK Then
                                    Await SetHttpFailureAsync(result, response, deadline.Token).ConfigureAwait(False)
                                    Return result
                                End If
                                Using json = Await ReadJsonAsync(response, 65536, deadline.Token).ConfigureAwait(False)
                                    Dim value As JsonElement
                                    Dim expiryValue As JsonElement
                                    Dim seconds As Long
                                    Dim root = json.RootElement
                                    If root.ValueKind <> JsonValueKind.Object OrElse
                                        Not root.TryGetProperty("token", value) OrElse value.ValueKind <> JsonValueKind.String OrElse
                                        Not root.TryGetProperty("expires", expiryValue) OrElse expiryValue.ValueKind <> JsonValueKind.Number OrElse
                                        Not expiryValue.TryGetInt64(seconds) OrElse seconds < 0 OrElse seconds > 253402300799L Then
                                        Throw New InvalidDataException("Invalid authorization response: expected token and Unix expires. No session accepted.")
                                    End If
                                    Dim sessionToken = NormalizeToken(value.GetString())
                                    Dim expiry = DateTimeOffset.FromUnixTimeSeconds(seconds)
                                    If expiry <= _clock.GetUtcNow().AddSeconds(30) Then Throw New InvalidDataException("Authorization response is expired or has less than 30 seconds remaining. Replace the login session token or verify the system clock.")
                                    _sessionToken = sessionToken
                                    _sessionExpires = expiry
                                    _sessionIdentity = identity
                                    _sessionVersion = version
                                End Using
                            End Using
                        End Using
                    End If
                    result.SessionExpiresUtc = _sessionExpires
                    result.HttpStatus = Nothing
                    result.TransportSucceeded = False
                    result.Stage = "EVENT REQUEST"
                    request.Headers.Authorization = New AuthenticationHeaderValue("Bearer", _sessionToken)
                End If
                Using response = Await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(False)
                    result.HttpStatus = CInt(response.StatusCode)
                    result.TransportSucceeded = True
                    If connectivityOnly Then
                        result.Stage = "HTTPS TRANSPORT ONLY"
                        result.Success = response.StatusCode = HttpStatusCode.OK
                        If Not result.Success Then Await SetHttpFailureAsync(result, response, deadline.Token).ConfigureAwait(False)
                        result.CredentialRenewalRequired = False
                        result.ErrorMessage = "HTTPS response received (HTTP " & CInt(response.StatusCode).ToString(Globalization.CultureInfo.InvariantCulture) &
                            "); transport reached Zeta, but this test sent no token. Authentication and event data NOT verified."
                        Return result
                    End If
                    If response.StatusCode <> HttpStatusCode.OK Then
                        Await SetHttpFailureAsync(result, response, deadline.Token).ConfigureAwait(False)
                        If response.StatusCode = HttpStatusCode.Unauthorized OrElse response.StatusCode = HttpStatusCode.Forbidden Then
                            ClearSession()
                            result.SessionExpiresUtc = Nothing
                        End If
                        Return result
                    End If
                    result.Stage = "JSON VALIDATION"
                    Using json = Await ReadJsonAsync(response, MaximumBodyBytes, deadline.Token).ConfigureAwait(False)
                        result.Events = ParseEvents(json.RootElement, settings.CloudSiteIdentifier)
                    End Using
                    result.Success = True
                    result.Authenticated = True
                    result.Stage = "VALID EVENTS"
                End Using
            Catch ex As OperationCanceledException When Not cancellation.IsCancellationRequested
                result.ErrorMessage = "Cloud request timed out."
                result.Stage = "TIMEOUT"
                result.ErrorOrigin = "TRANSPORT"
            Catch ex As HttpRequestException
                Dim socket = TryCast(ex.InnerException, SocketException)
                result.Stage = If(ex.HttpRequestError = HttpRequestError.NameResolutionError OrElse (socket IsNot Nothing AndAlso
                    (socket.SocketErrorCode = SocketError.HostNotFound OrElse socket.SocketErrorCode = SocketError.NoData OrElse socket.SocketErrorCode = SocketError.TryAgain)),
                    "DNS FAILURE", If(ex.HttpRequestError = HttpRequestError.SecureConnectionError, "TLS FAILURE", "HTTPS FAILURE"))
                result.ErrorMessage = result.Stage & ": request failed; no server response body or credentials are logged."
                result.ErrorOrigin = "TRANSPORT"
            Catch ex As JsonException
                result.ErrorMessage = "Malformed JSON response; no authorization or event success is claimed."
                result.ErrorOrigin = "RESPONSE VALIDATION"
            Catch ex As InvalidDataException
                result.ErrorMessage = If(result.Stage = "CLAIMS AUTHORIZATION",
                    "Authorization response rejected locally: invalid claims token format or expiry. No event request sent; renew the login session token or verify the system clock.",
                    ex.Message)
                result.ErrorOrigin = "RESPONSE VALIDATION"
                result.CredentialRenewalRequired = result.Stage = "CLAIMS AUTHORIZATION"
            Catch ex As IOException
                result.ErrorMessage = "Cloud response stream failed; no events synchronized."
                result.ErrorOrigin = "TRANSPORT"
            Finally
                result.ResponseMilliseconds = watch.Elapsed.TotalMilliseconds
            End Try
        End Using
        Return result
    End Function

    Private Sub ClearSession()
        _sessionToken = String.Empty
        _sessionIdentity = String.Empty
        _sessionExpires = DateTimeOffset.MinValue
    End Sub

    Private Async Function SetHttpFailureAsync(result As ZetaCloudResult, response As HttpResponseMessage,
                                               cancellation As CancellationToken) As Task
        result.ErrorMessage = StatusError(response.StatusCode)
        If result.Stage = "LOGIN" AndAlso (response.StatusCode = HttpStatusCode.Unauthorized OrElse response.StatusCode = HttpStatusCode.Forbidden) Then
            result.ErrorMessage = "Zeta rejected sign-in (HTTP " & CInt(response.StatusCode).ToString(Globalization.CultureInfo.InvariantCulture) &
                "). Check your login and account access. No automatic retry is attempted; previous saved credentials are retained."
        End If
        result.ErrorOrigin = "ZETA HTTP"
        result.CredentialRenewalRequired = response.StatusCode = HttpStatusCode.Unauthorized OrElse response.StatusCode = HttpStatusCode.Forbidden
        If response.Headers.RetryAfter IsNot Nothing Then
            result.RetryAfterSeconds = Math.Max(0, If(response.Headers.RetryAfter.Delta.HasValue,
                response.Headers.RetryAfter.Delta.GetValueOrDefault().TotalSeconds,
                (response.Headers.RetryAfter.Date.GetValueOrDefault() - _clock.GetUtcNow()).TotalSeconds))
        End If
        Dim categories As String() = {"invalid_token", "expired_token", "token_expired", "jwt_expired", "invalid_signature",
            "insufficient_scope", "access_denied", "unauthorized", "forbidden", "invalid_audience", "invalid_issuer",
            "tokenexpirederror", "jsonwebtokenerror", "notbeforeerror"}
        For Each challenge In response.Headers.WwwAuthenticate
            If Not String.Equals(challenge.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) OrElse challenge.Parameter Is Nothing Then Continue For
            Dim match = Regex.Match(challenge.Parameter, "(?:^|,\s*)error=""([a-zA-Z_]+)""", RegexOptions.CultureInvariant)
            If match.Success AndAlso categories.Contains(match.Groups(1).Value.ToLowerInvariant()) Then
                result.ServerErrorCategory = match.Groups(1).Value.ToLowerInvariant()
                Exit For
            End If
        Next
        If result.ServerErrorCategory.Length = 0 Then
            Try
                Using json = Await ReadJsonAsync(response, 65536, cancellation).ConfigureAwait(False)
                    Dim root = json.RootElement
                    If root.ValueKind = JsonValueKind.Object Then
                        Dim nested As JsonElement
                        If (root.TryGetProperty("err", nested) OrElse root.TryGetProperty("error", nested)) AndAlso nested.ValueKind = JsonValueKind.Object Then root = nested
                        For Each key In {"err", "error", "code", "category", "name"}
                            Dim value As JsonElement
                            If root.TryGetProperty(key, value) AndAlso value.ValueKind = JsonValueKind.String Then
                                Dim category = value.GetString().ToLowerInvariant()
                                If categories.Contains(category) Then
                                    result.ServerErrorCategory = category
                                    Exit For
                                End If
                            End If
                        Next
                    End If
                End Using
                If result.ServerErrorCategory.Length = 0 Then result.ServerErrorCategory = "NOT PROVIDED / NOT ALLOWLISTED"
            Catch ex As Exception When TypeOf ex Is JsonException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is IOException OrElse
                TypeOf ex Is HttpRequestException OrElse TypeOf ex Is OperationCanceledException
                result.ServerErrorCategory = "UNAVAILABLE (ERROR BODY NOT ACCEPTED)"
            End Try
        End If
        result.ErrorMessage &= " Stage: " & result.Stage & "; server category: " & result.ServerErrorCategory & "."
    End Function

    Private Shared Async Function ReadJsonAsync(response As HttpResponseMessage, maximum As Integer,
                                                cancellation As CancellationToken) As Task(Of JsonDocument)
        Dim mediaType = response.Content.Headers.ContentType?.MediaType
        If mediaType Is Nothing OrElse Not (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) OrElse
            mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)) Then
            Throw New InvalidDataException("Unexpected response format: expected JSON, not a website login page.")
        End If
        Using stream = Await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(False),
              buffer As New MemoryStream()
            Dim bytes(8191) As Byte
            While True
                Dim count = Await stream.ReadAsync(bytes.AsMemory(), cancellation).ConfigureAwait(False)
                If count = 0 Then Exit While
                If buffer.Length + count > maximum Then Throw New InvalidDataException("Cloud response exceeds the permitted body limit; no response accepted.")
                buffer.Write(bytes, 0, count)
            End While
            Return JsonDocument.Parse(buffer.ToArray(), New JsonDocumentOptions With {.MaxDepth = 32})
        End Using
    End Function

    Private Shared Function StatusError(status As HttpStatusCode) As String
        Select Case CInt(status)
            Case 401 : Return "Zeta returned HTTP 401: authorization rejected. Exact cause is unconfirmed unless a server category is supplied. Renew the login session token and reconnect manually; polling is stopped."
            Case 403 : Return "Zeta returned HTTP 403: access denied. Verify account/site permission or renew credentials before reconnecting; polling is stopped."
            Case 404 : Return "Endpoint or site not found (HTTP 404); verify the observed route and proposed site ID."
            Case 429 : Return "Rate limit exceeded (HTTP 429); retry is delayed."
            Case 300 To 399 : Return "Redirect rejected; credentials are never forwarded to another location."
            Case 500 To 599 : Return "Cloud server error (HTTP " & CInt(status).ToString(Globalization.CultureInfo.InvariantCulture) & ")."
            Case Else : Return "Unexpected HTTP status " & CInt(status).ToString(Globalization.CultureInfo.InvariantCulture) & "."
        End Select
    End Function

    Private Shared Function ParseEvents(root As JsonElement, site As String) As IReadOnlyList(Of ZetaFireEvent)
        Dim array = root
        If root.ValueKind = JsonValueKind.Object Then
            If Not root.TryGetProperty("events", array) Then Throw New InvalidDataException("Unexpected JSON structure: an events array is required; vendor schema is not yet verified.")
        End If
        If array.ValueKind <> JsonValueKind.Array Then Throw New InvalidDataException("Unexpected JSON structure: expected an event array.")
        Dim events As New List(Of ZetaFireEvent)
        For Each item In array.EnumerateArray()
            If item.ValueKind <> JsonValueKind.Object Then Throw New InvalidDataException("Unexpected event structure: each event must be an object.")
            Dim marker As JsonElement
            Dim observed = item.TryGetProperty("date", marker) OrElse item.TryGetProperty("category", marker) OrElse item.TryGetProperty("hwaddr", marker)
            Dim hardware = Text(item, "hwaddr")
            If hardware.Length > 0 AndAlso Not String.Equals(hardware, site, StringComparison.OrdinalIgnoreCase) Then Throw New InvalidDataException("Returned event belongs to a different site; no events accepted.")
            Dim record As New ZetaFireEvent With {.SiteId = site, .EventId = Text(item, "id"), .EventType = If(observed, Text(item, "category"), Text(item, "event_type")),
                .Zone = Text(item, "zone"), .DeviceAddress = Text(item, "device_address"), .Description = Text(item, "description"),
                .Message = Text(item, "message")}
            If observed Then
                record.Message = Text(item, "title")
                record.Description = Text(item, "location")
            End If
            Dim timestamp = Text(item, If(observed, "date", "timestamp"))
            If observed AndAlso (timestamp.Length = 0 OrElse record.EventId.Length = 0 OrElse record.EventType.Length = 0 OrElse record.Message.Length = 0) Then
                Throw New InvalidDataException("Observed event response requires id, date, category and title; no events accepted.")
            End If
            If timestamp.Length > 0 Then
                Dim parsed As DateTimeOffset
                If Not Regex.IsMatch(timestamp, "(?:Z|[+-]\d{2}:\d{2})\z") OrElse
                    Not DateTimeOffset.TryParse(timestamp, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.None, parsed) Then Throw New InvalidDataException("Event timestamp must be valid ISO-8601 with an explicit UTC offset.")
                record.TimestampUtc = parsed.ToUniversalTime()
            End If
            If record.EventId.Length = 0 AndAlso record.Message.Length = 0 Then Throw New InvalidDataException("Event has neither a vendor ID nor a message; response schema needs verification.")
            events.Add(record)
            If events.Count > 10000 Then Throw New InvalidDataException("Too many events in one response; verified pagination is required.")
        Next
        Return events
    End Function

    Private Shared Function Text(item As JsonElement, key As String) As String
        Dim value As JsonElement
        If Not item.TryGetProperty(key, value) OrElse value.ValueKind = JsonValueKind.Null Then Return String.Empty
        Dim textValue As String
        If value.ValueKind = JsonValueKind.String Then
            textValue = value.GetString()
        ElseIf value.ValueKind = JsonValueKind.Number Then
            textValue = value.GetRawText()
        Else
            Throw New InvalidDataException("Unexpected type for event field " & key & ".")
        End If
        If textValue.Length > 4096 OrElse textValue.Any(Function(c) Char.IsControl(c) AndAlso c <> ControlChars.Lf AndAlso c <> ControlChars.Cr AndAlso c <> ControlChars.Tab) Then Throw New InvalidDataException("Invalid event field " & key & ".")
        Return textValue
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        ClearSession()
        _http.Dispose()
        _requests.Dispose()
    End Sub
End Class
