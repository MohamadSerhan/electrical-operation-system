Option Strict On

Imports System.Globalization
Imports EOEMS.Licensing

''' <summary>The outcome of evaluating a license key: whether it is currently usable, why not if
''' it isn't, and (when parseable) the certified details for display.</summary>
Friend NotInheritable Class LicenseStatus
    Public Property IsValid As Boolean
    Public Property Reason As String = String.Empty
    Public Property Payload As LicensePayload
    Public Property DaysRemaining As Integer?
End Class

''' <summary>Loads, saves and validates the local license key file, and guards against the clock
''' being wound back to extend a trial or an expired term.</summary>
Friend NotInheritable Class LicenseStore
    Private Shared ReadOnly DataDirectory As String = IO.Path.Combine(AppContext.BaseDirectory, "data")
    Private Shared ReadOnly LicenseFilePath As String = IO.Path.Combine(DataDirectory, "license.lic")
    Private Shared ReadOnly SeenFilePath As String = IO.Path.Combine(DataDirectory, ".license-seen")
    Private Const ClockRollbackTolerance As Double = 6.0 ' hours; absorbs normal timezone/DST shifts

    Public Shared Function LoadRawKey() As String
        Try
            If IO.File.Exists(LicenseFilePath) Then Return IO.File.ReadAllText(LicenseFilePath).Trim()
        Catch
        End Try
        Return String.Empty
    End Function

    Public Shared Sub SaveRawKey(keyText As String)
        IO.Directory.CreateDirectory(DataDirectory)
        IO.File.WriteAllText(LicenseFilePath, keyText.Trim())
    End Sub

    Public Shared Function Evaluate(keyText As String) As LicenseStatus
        Dim status As New LicenseStatus()
        Dim signed As SignedLicense = Nothing
        If Not SignedLicense.TryParse(keyText, signed) Then
            status.Reason = "The license key text is not in a recognized format. Paste the full key exactly as generated."
            Return status
        End If
        If Not LicenseVerifier.Verify(signed) Then
            status.Reason = "The license key signature is invalid. It may have been altered, or it was not issued for this software."
            Return status
        End If
        If Not String.Equals(signed.Payload.Product, LicensePayload.CurrentProduct, StringComparison.Ordinal) Then
            status.Reason = "This license key is not valid for this product."
            Return status
        End If
        Dim currentRequestCode = MachineFingerprint.Compute()
        If Not String.Equals(signed.Payload.RequestCode, currentRequestCode, StringComparison.OrdinalIgnoreCase) Then
            status.Reason = "This license key was issued for a different computer. Send this computer's Request Code to get a new key."
            status.Payload = signed.Payload
            Return status
        End If
        Dim nowUtc = DateTime.UtcNow
        Dim lastSeenUtc = ReadLastSeenUtc()
        If lastSeenUtc.HasValue AndAlso nowUtc < lastSeenUtc.Value.AddHours(-ClockRollbackTolerance) Then
            status.Reason = "The system clock appears to have been moved back. Correct the date and time and try again."
            status.Payload = signed.Payload
            Return status
        End If
        If signed.Payload.ExpiresUtc.HasValue AndAlso nowUtc > signed.Payload.ExpiresUtc.Value Then
            status.Reason = "This license expired on " & signed.Payload.ExpiresUtc.Value.ToLocalTime().ToString("d", CultureInfo.CurrentCulture) & "."
            status.Payload = signed.Payload
            Return status
        End If
        WriteLastSeenUtc(If(lastSeenUtc.HasValue AndAlso lastSeenUtc.Value > nowUtc, lastSeenUtc.Value, nowUtc))
        status.IsValid = True
        status.Payload = signed.Payload
        If signed.Payload.ExpiresUtc.HasValue Then
            status.DaysRemaining = CInt(Math.Max(0, Math.Ceiling((signed.Payload.ExpiresUtc.Value - nowUtc).TotalDays)))
        End If
        Return status
    End Function

    Private Shared Function ReadLastSeenUtc() As DateTime?
        Try
            If IO.File.Exists(SeenFilePath) Then
                Dim text = IO.File.ReadAllText(SeenFilePath).Trim()
                Dim value As DateTime
                If DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, value) Then Return value
            End If
        Catch
        End Try
        Return Nothing
    End Function

    Private Shared Sub WriteLastSeenUtc(value As DateTime)
        Try
            IO.Directory.CreateDirectory(DataDirectory)
            IO.File.WriteAllText(SeenFilePath, value.ToString("O", CultureInfo.InvariantCulture))
            IO.File.SetAttributes(SeenFilePath, IO.FileAttributes.Hidden)
        Catch
        End Try
    End Sub
End Class
