Imports System.Net
Imports System.Net.Http
Imports System.Threading
Imports System.Threading.Tasks

Public NotInheritable Class WebCardEndpointCheck
    Public Property Address As Uri
    Public Property Responds As Boolean
    Public Property RequiresBrowserReview As Boolean
    Public Property Detail As String = String.Empty
End Class

Public NotInheritable Class WebCardConnection
    Private Sub New()
    End Sub

    Public Shared Async Function CheckForOpeningAsync(deviceId As String, value As String, token As CancellationToken) As Task(Of WebCardEndpointCheck)
        Dim primary = Await CheckAsync(deviceId, value, token).ConfigureAwait(False)
        Dim alternative = HttpAlternative(primary.Address)
        If primary.Responds OrElse alternative Is Nothing Then Return primary
        Dim http = Await CheckAsync(deviceId, alternative.AbsoluteUri, token).ConfigureAwait(False)
        If http.Responds Then Return http
        primary.Detail &= " HTTP alternative: " & http.Detail
        Return primary
    End Function

    Public Shared Async Function CheckAsync(deviceId As String, value As String, token As CancellationToken) As Task(Of WebCardEndpointCheck)
        Dim address = WebCardCredentialStore.ManagementUri(deviceId, value)
        Using handler As New HttpClientHandler With {
            .AllowAutoRedirect = False, .UseProxy = False, .UseCookies = False, .UseDefaultCredentials = False}
            Using client As New HttpClient(handler) With {.Timeout = TimeSpan.FromSeconds(4)}
                Try
                    Using response = Await client.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(False)
                        Dim code = CInt(response.StatusCode)
                        Return New WebCardEndpointCheck With {
                            .Address = address,
                            .Responds = (code >= 200 AndAlso code < 400) OrElse
                                response.StatusCode = HttpStatusCode.Unauthorized OrElse response.StatusCode = HttpStatusCode.Forbidden,
                            .Detail = "HTTP " & code.ToString(Globalization.CultureInfo.InvariantCulture) &
                                ". The web server replied; browser authentication is separate from SNMP."}
                    End Using
                Catch ex As OperationCanceledException When Not token.IsCancellationRequested
                    Return New WebCardEndpointCheck With {.Address = address, .Detail = "No web response within 4 seconds."}
                Catch ex As HttpRequestException
                    Return New WebCardEndpointCheck With {
                        .Address = address, .RequiresBrowserReview = ex.HttpRequestError = HttpRequestError.SecureConnectionError,
                        .Detail = "Web connection failed: " & ex.Message}
                End Try
            End Using
        End Using
    End Function

    Public Shared Function HttpAlternative(address As Uri) As Uri
        If address.Scheme <> Uri.UriSchemeHttps OrElse Not address.IsDefaultPort Then Return Nothing
        Return New UriBuilder(address) With {.Scheme = Uri.UriSchemeHttp, .Port = -1}.Uri
    End Function
End Class
