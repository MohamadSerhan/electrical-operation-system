Imports System.IO
Imports System.Net.Sockets
Imports System.Threading
Imports System.Threading.Tasks

Public NotInheritable Class FireModbusClient
    Private Shared _transaction As Integer

    Private Sub New()
    End Sub

    Public Shared Async Function ReadAsync(settings As FireConnectionSettings, cancellationToken As CancellationToken) As Task(Of UShort())
        settings.Validate()
        Dim reason = settings.UnavailableReason()
        If reason.Length > 0 Then Throw New InvalidOperationException(reason)
        Using deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            deadline.CancelAfter(settings.TimeoutMilliseconds)
            Using client As New TcpClient
                Try
                    Await client.ConnectAsync(settings.Host(), settings.Port(), deadline.Token).ConfigureAwait(False)
                    Using stream = client.GetStream()
                        Return Await ReadConnectedAsync(settings, stream, deadline.Token).ConfigureAwait(False)
                    End Using
                Catch ex As OperationCanceledException When Not cancellationToken.IsCancellationRequested
                    Throw New TimeoutException("Fire monitoring FC04 connect/read timed out.")
                End Try
            End Using
        End Using
    End Function

    Friend Shared Async Function ReadConnectedAsync(settings As FireConnectionSettings, stream As NetworkStream, token As CancellationToken) As Task(Of UShort())
        settings.Validate()
        Dim reason = settings.UnavailableReason()
        If reason.Length > 0 Then Throw New InvalidOperationException(reason)
        Dim transaction = CUShort(Interlocked.Increment(_transaction) And &HFFFF)
        Dim request As Byte() = {
            CByte(transaction >> 8), CByte(transaction And 255), 0, 0, 0, 6, CByte(settings.UnitId), 4,
            CByte(settings.StartAddress >> 8), CByte(settings.StartAddress And 255),
            CByte(settings.RegisterCount >> 8), CByte(settings.RegisterCount And 255)}
        Await stream.WriteAsync(request, token).ConfigureAwait(False)
        Dim header(6) As Byte
        Await stream.ReadExactlyAsync(header.AsMemory(), token).ConfigureAwait(False)
        Dim length = CInt(header(4)) * 256 + header(5)
        If CInt(header(0)) * 256 + header(1) <> transaction OrElse header(2) <> 0 OrElse header(3) <> 0 OrElse
            header(6) <> settings.UnitId OrElse (length <> 3 AndAlso length <> 3 + settings.RegisterCount * 2) Then
            Throw New InvalidDataException("Invalid FC04 Modbus TCP header, transaction, unit or length.")
        End If
        Dim pdu(length - 2) As Byte
        Await stream.ReadExactlyAsync(pdu.AsMemory(), token).ConfigureAwait(False)
        If pdu(0) = &H84 AndAlso pdu.Length = 2 Then Throw New InvalidDataException("FC04 Modbus exception code " & pdu(1).ToString(Globalization.CultureInfo.InvariantCulture) & ".")
        If pdu(0) <> 4 OrElse pdu.Length <> 2 + settings.RegisterCount * 2 OrElse pdu(1) <> settings.RegisterCount * 2 Then
            Throw New InvalidDataException("FC04 response function or register byte count does not match the configured mapping.")
        End If
        Dim values(settings.RegisterCount - 1) As UShort
        For index = 0 To values.Length - 1
            values(index) = CUShort(CInt(pdu(2 + index * 2)) * 256 + pdu(3 + index * 2))
        Next
        Return values
    End Function
End Class
