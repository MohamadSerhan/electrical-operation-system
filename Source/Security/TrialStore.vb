Option Strict On

Imports System.Globalization
Imports EOEMS.Licensing

''' <summary>Grants every installation one self-service 14-day trial with no vendor contact
''' required. The trial start is recorded in two independent, hard-to-erase places - a hidden
''' file under the app's data folder and a per-user registry value - so reinstalling the app, or
''' deleting just one of the two markers, cannot restart or extend the trial. The earliest start
''' time found across both locations always wins.</summary>
Friend NotInheritable Class TrialStore
    Public Const TrialDays As Integer = 14
    Private Const ClockRollbackTolerance As Double = 6.0 ' hours; absorbs normal timezone/DST shifts

    Private Shared ReadOnly DataDirectory As String = IO.Path.Combine(AppContext.BaseDirectory, "data")
    Private Shared ReadOnly MarkerFilePath As String = IO.Path.Combine(DataDirectory, ".trial-marker")
    Private Const RegistryKeyPath As String = "SOFTWARE\EOEMS\PowerTransformersScadaUI\Trial"

    Private NotInheritable Class Marker
        Public Property StartUtc As DateTime
        Public Property SeenUtc As DateTime
    End Class

    Public Shared Function Evaluate() As LicenseStatus
        Dim currentRequestCode = MachineFingerprint.Compute()
        Dim nowUtc = DateTime.UtcNow

        Dim markers = {ReadFileMarker(currentRequestCode), ReadRegistryMarker(currentRequestCode)}
        Dim startUtc = nowUtc
        Dim seenUtc = nowUtc
        Dim haveMarker = False
        For Each candidate In markers
            If candidate Is Nothing Then Continue For
            If Not haveMarker Then
                startUtc = candidate.StartUtc
                seenUtc = candidate.SeenUtc
                haveMarker = True
            Else
                If candidate.StartUtc < startUtc Then startUtc = candidate.StartUtc
                If candidate.SeenUtc > seenUtc Then seenUtc = candidate.SeenUtc
            End If
        Next

        Dim expiryUtc = startUtc.AddDays(TrialDays)
        Dim status As New LicenseStatus With {
            .Payload = New LicensePayload With {
                .LicenseId = Guid.Empty,
                .Product = LicensePayload.CurrentProduct,
                .Licensee = "Trial user",
                .Email = String.Empty,
                .RequestCode = currentRequestCode,
                .IssuedUtc = startUtc,
                .ExpiresUtc = expiryUtc,
                .Plan = LicensePlan.Trial
            }
        }

        If nowUtc < seenUtc.AddHours(-ClockRollbackTolerance) Then
            ' Do not move SeenUtc backward - persist the markers unchanged so the rollback cannot "forget" time already used.
            WriteMarkers(startUtc, seenUtc, currentRequestCode)
            status.IsValid = False
            status.Reason = "The system clock appears to have been moved back. Correct the date and time, or register a license."
            Return status
        End If

        WriteMarkers(startUtc, If(nowUtc > seenUtc, nowUtc, seenUtc), currentRequestCode)

        If nowUtc >= expiryUtc Then
            status.IsValid = False
            status.DaysRemaining = 0
            status.Reason = "Your 14-day trial has expired. Register a license to keep using this software."
            Return status
        End If

        status.IsValid = True
        status.DaysRemaining = CInt(Math.Max(0, Math.Ceiling((expiryUtc - nowUtc).TotalDays)))
        status.Reason = String.Empty
        Return status
    End Function

    Private Shared Function ReadFileMarker(expectedRequestCode As String) As Marker
        Try
            If Not IO.File.Exists(MarkerFilePath) Then Return Nothing
            Dim parts = IO.File.ReadAllText(MarkerFilePath).Trim().Split("|"c)
            If parts.Length <> 3 Then Return Nothing
            If Not String.Equals(parts(2), expectedRequestCode, StringComparison.OrdinalIgnoreCase) Then Return Nothing
            Dim startUtc As DateTime, seenUtc As DateTime
            If Not DateTime.TryParse(parts(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, startUtc) Then Return Nothing
            If Not DateTime.TryParse(parts(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, seenUtc) Then Return Nothing
            Return New Marker With {.StartUtc = startUtc, .SeenUtc = seenUtc}
        Catch
            Return Nothing
        End Try
    End Function

    Private Shared Function ReadRegistryMarker(expectedRequestCode As String) As Marker
        Try
            Using key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryKeyPath)
                If key Is Nothing Then Return Nothing
                Dim requestCode = TryCast(key.GetValue("RequestCode"), String)
                If String.IsNullOrEmpty(requestCode) OrElse Not String.Equals(requestCode, expectedRequestCode, StringComparison.OrdinalIgnoreCase) Then Return Nothing
                Dim startText = TryCast(key.GetValue("StartUtc"), String)
                Dim seenText = TryCast(key.GetValue("SeenUtc"), String)
                Dim startUtc As DateTime, seenUtc As DateTime
                If String.IsNullOrEmpty(startText) OrElse Not DateTime.TryParse(startText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, startUtc) Then Return Nothing
                If String.IsNullOrEmpty(seenText) OrElse Not DateTime.TryParse(seenText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, seenUtc) Then Return Nothing
                Return New Marker With {.StartUtc = startUtc, .SeenUtc = seenUtc}
            End Using
        Catch
            Return Nothing
        End Try
    End Function

    Private Shared Sub WriteMarkers(startUtc As DateTime, seenUtc As DateTime, requestCode As String)
        Try
            IO.Directory.CreateDirectory(DataDirectory)
            Dim text = String.Join("|", startUtc.ToString("O", CultureInfo.InvariantCulture), seenUtc.ToString("O", CultureInfo.InvariantCulture), requestCode)
            IO.File.WriteAllText(MarkerFilePath, text)
            IO.File.SetAttributes(MarkerFilePath, IO.FileAttributes.Hidden)
        Catch
        End Try
        Try
            Using key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegistryKeyPath)
                If key IsNot Nothing Then
                    key.SetValue("StartUtc", startUtc.ToString("O", CultureInfo.InvariantCulture))
                    key.SetValue("SeenUtc", seenUtc.ToString("O", CultureInfo.InvariantCulture))
                    key.SetValue("RequestCode", requestCode)
                End If
            End Using
        Catch
        End Try
    End Sub
End Class
