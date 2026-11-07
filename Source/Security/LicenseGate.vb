Option Strict On

''' <summary>The single entry point Program.vb calls before showing the login screen. Blocks
''' startup with <see cref="LicenseRegistrationForm"/> until a valid license is active.</summary>
Friend NotInheritable Class LicenseGate
    Private Sub New()
    End Sub

    ''' <summary>Returns True once a valid license is registered for this machine, or False if the
    ''' user chose to exit instead of registering one.</summary>
    Public Shared Function EnsureLicensed() As Boolean
        Dim keyText = LicenseStore.LoadRawKey()
        Dim status = If(String.IsNullOrEmpty(keyText),
            New LicenseStatus With {.IsValid = False, .Reason = "No license is registered on this computer."},
            LicenseStore.Evaluate(keyText))
        If status.IsValid Then Return True

        Using form As New LicenseRegistrationForm(status)
            Return form.ShowDialog() = DialogResult.OK
        End Using
    End Function
End Class
