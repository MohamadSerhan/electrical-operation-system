Option Strict On

Imports System.Runtime.InteropServices
Imports System.Security.Cryptography
Imports System.Text

''' <summary>Builds a stable "Request Code" that identifies one Windows installation, so a
''' license key can be locked to the machine it was issued for. Based on the per-install
''' MachineGuid (set once by Windows setup) plus the system drive's volume serial number and the
''' computer name, hashed together so the raw identifiers are never exposed.</summary>
Public NotInheritable Class MachineFingerprint
    Private Sub New()
    End Sub

    Public Shared Function Compute() As String
        Dim combined = String.Join("|", ReadMachineGuid(), Environment.MachineName, GetSystemVolumeSerial().ToString("X8"))
        Dim hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined))
        Dim hex = Convert.ToHexString(hash).Substring(0, 20)
        Return FormatGroups(hex)
    End Function

    Private Shared Function FormatGroups(hex As String) As String
        Dim builder As New StringBuilder()
        Dim i = 0
        While i < hex.Length
            If builder.Length > 0 Then builder.Append("-"c)
            builder.Append(hex.Substring(i, Math.Min(4, hex.Length - i)))
            i += 4
        End While
        Return builder.ToString()
    End Function

    Private Shared Function ReadMachineGuid() As String
        Try
            Using key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey("SOFTWARE\Cryptography")
                If key IsNot Nothing Then
                    Dim value = TryCast(key.GetValue("MachineGuid"), String)
                    If Not String.IsNullOrEmpty(value) Then Return value
                End If
            End Using
        Catch
        End Try
        Return "NOGUID"
    End Function

    <DllImport("kernel32.dll", CharSet:=CharSet.Auto, SetLastError:=True)>
    Private Shared Function GetVolumeInformation(
        rootPathName As String,
        volumeNameBuffer As StringBuilder,
        volumeNameSize As Integer,
        ByRef volumeSerialNumber As UInteger,
        ByRef maximumComponentLength As UInteger,
        ByRef fileSystemFlags As UInteger,
        fileSystemNameBuffer As StringBuilder,
        fileSystemNameSize As Integer) As Boolean
    End Function

    Private Shared Function GetSystemVolumeSerial() As UInteger
        Try
            Dim root = IO.Path.GetPathRoot(Environment.SystemDirectory)
            Dim serial As UInteger = 0
            Dim maxLength As UInteger = 0
            Dim flags As UInteger = 0
            Dim volumeName As New StringBuilder(261)
            Dim fileSystemName As New StringBuilder(261)
            If GetVolumeInformation(root, volumeName, volumeName.Capacity, serial, maxLength, flags, fileSystemName, fileSystemName.Capacity) Then
                Return serial
            End If
        Catch
        End Try
        Return 0
    End Function
End Class
