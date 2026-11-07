Option Strict On

''' <summary>The single entry point Program.vb calls before showing the login screen. Blocks
''' startup with <see cref="LicenseRegistrationForm"/> until a valid paid license, or an unexpired
''' self-service trial, is active.</summary>
Friend NotInheritable Class LicenseGate
    Private Sub New()
    End Sub

    ''' <summary>The most recently computed combined license/trial status. Populated by
    ''' <see cref="EnsureLicensed"/> at startup and refreshed by every later call to
    ''' <see cref="EvaluateCurrent"/>, so the shell can show a trial banner without re-evaluating.</summary>
    Public Shared Property CurrentStatus As LicenseStatus

    ''' <summary>Evaluates the registered paid license if any, falling back to the self-service
    ''' trial when no paid license is active. Shows no UI.</summary>
    Public Shared Function EvaluateCurrent() As LicenseStatus
        Dim keyText = LicenseStore.LoadRawKey()
        Dim licenseStatus = If(String.IsNullOrEmpty(keyText),
            New LicenseStatus With {.IsValid = False, .Reason = "No license is registered on this computer."},
            LicenseStore.Evaluate(keyText))
        If licenseStatus.IsValid Then
            CurrentStatus = licenseStatus
            Return licenseStatus
        End If

        Dim trialStatus = TrialStore.Evaluate()
        If trialStatus.IsValid Then
            CurrentStatus = trialStatus
            Return trialStatus
        End If

        ' Neither a valid paid license nor an active trial. Prefer the paid-license reason when a
        ' key is present on disk (it is the more actionable message - e.g. "wrong computer" or
        ' "expired on <date>") over the generic trial-expired reason.
        CurrentStatus = If(Not String.IsNullOrEmpty(keyText), licenseStatus, trialStatus)
        Return CurrentStatus
    End Function

    ''' <summary>Returns True once a valid license (or trial) is active for this machine, or False
    ''' if the user chose to exit instead of registering one.</summary>
    Public Shared Function EnsureLicensed() As Boolean
        Dim status = EvaluateCurrent()
        If status.IsValid Then Return True

        Using form As New LicenseRegistrationForm(status, isMandatory:=True)
            If form.ShowDialog() = DialogResult.OK Then
                CurrentStatus = EvaluateCurrent()
                Return True
            End If
            Return False
        End Using
    End Function
End Class
