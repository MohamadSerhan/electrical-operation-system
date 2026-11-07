Imports System.Drawing
Imports System.Runtime.InteropServices
Imports System.Windows.Forms

Public NotInheritable Class AboutForm
    Inherits Form

    Public Sub New()
        Text = "About Electrical Operation Center"
        StartPosition = FormStartPosition.CenterParent
        FormBorderStyle = FormBorderStyle.FixedDialog
        MaximizeBox = False
        MinimizeBox = False
        ShowInTaskbar = False
        ClientSize = New Size(560, 330)
        BackColor = UiTheme.ShellDeep
        Font = New Font("Segoe UI", 9.0F)
        Dim applicationIcon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath)
        If applicationIcon IsNot Nothing Then Icon = applicationIcon

        Dim title = UiTheme.Label("ELECTRICAL OPERATION CENTER", New Font("Segoe UI", 15.0F, FontStyle.Bold), Color.White, False)
        title.Location = New Point(20, 18)
        title.Size = New Size(510, 30)
        Controls.Add(title)

        Dim version = UiTheme.Label(AppBuildInfo.VersionDisplay, New Font("Segoe UI", 12.0F, FontStyle.Bold), UiTheme.Cyan, False)
        version.Location = New Point(22, 56)
        version.Size = New Size(500, 24)
        Controls.Add(version)

        Dim details = UiTheme.Label(
            "Build ID: " & AppBuildInfo.BuildIdentifier & Environment.NewLine &
            "Source SHA-256: " & AppBuildInfo.SourceHash & Environment.NewLine &
            "Build status: " & AppBuildInfo.BuildStatus & Environment.NewLine &
            "Test status: " & AppBuildInfo.TestStatus & Environment.NewLine &
            "Runtime: " & RuntimeInformation.FrameworkDescription & Environment.NewLine &
            "Process architecture: " & If(Environment.Is64BitProcess, "64-bit", "32-bit"),
            New Font("Segoe UI", 9.0F), Color.FromArgb(205, 224, 242), False)
        details.Location = New Point(22, 98)
        details.Size = New Size(516, 152)
        Controls.Add(details)

        Dim note = UiTheme.Label("Build and test metadata is populated by the release script. It does not indicate PLC commissioning.", New Font("Segoe UI", 8.0F), UiTheme.Amber, False)
        note.Location = New Point(22, 259)
        note.Size = New Size(516, 30)
        Controls.Add(note)

        Dim closeButton = UiTheme.Button("CLOSE", True)
        closeButton.Size = New Size(100, 32)
        closeButton.Location = New Point(ClientSize.Width - closeButton.Width - 20, ClientSize.Height - closeButton.Height - 14)
        closeButton.DialogResult = DialogResult.OK
        Controls.Add(closeButton)
        AcceptButton = closeButton
    End Sub
End Class
