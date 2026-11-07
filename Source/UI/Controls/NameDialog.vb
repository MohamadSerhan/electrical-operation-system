Imports System.Drawing
Imports System.Windows.Forms

Public NotInheritable Class NameDialog
    Private Sub New()
    End Sub

    ' Returns the entered text, or Nothing when the dialog is cancelled.
    Public Shared Function Prompt(owner As IWin32Window, title As String, caption As String, initial As String, maximumLength As Integer, font As Font) As String
        Using dialog As New Form With {.Text = title, .FormBorderStyle = FormBorderStyle.FixedDialog, .StartPosition = FormStartPosition.CenterParent,
            .MinimizeBox = False, .MaximizeBox = False, .ShowInTaskbar = False, .ClientSize = New Size(360, 110), .Font = font}
            Dim label As New Label With {.Text = caption, .Left = 14, .Top = 14, .AutoSize = True}
            Dim input As New TextBox With {.Left = 14, .Top = 38, .Width = 330, .MaxLength = maximumLength, .Text = initial}
            Dim ok As New Button With {.Text = "OK", .Left = 188, .Top = 72, .Width = 75, .DialogResult = DialogResult.OK}
            Dim cancel As New Button With {.Text = "Cancel", .Left = 269, .Top = 72, .Width = 75, .DialogResult = DialogResult.Cancel}
            dialog.Controls.AddRange({label, input, ok, cancel})
            dialog.AcceptButton = ok
            dialog.CancelButton = cancel
            If dialog.ShowDialog(owner) <> DialogResult.OK Then Return Nothing
            Return input.Text
        End Using
    End Function
End Class
