Imports System.Reflection
Imports System.Linq
Imports System.Text.RegularExpressions

Public NotInheritable Class AppBuildInfo
    Private Shared ReadOnly EntryAssembly As Assembly = GetType(AppBuildInfo).Assembly

    Private Sub New()
    End Sub

    Public Shared ReadOnly Property VersionDisplay As String
        Get
            Dim information = EntryAssembly.GetCustomAttribute(Of AssemblyInformationalVersionAttribute)()
            If information Is Nothing Then Return "Version unavailable"

            Dim match = Regex.Match(information.InformationalVersion, "^(?:V)?(?<major>\d+)(?:\.(?<minor>\d+))?")
            If Not match.Success Then Return "Version unavailable"

            Dim major = match.Groups("major").Value
            Dim minor = match.Groups("minor").Value
            Return If(String.IsNullOrEmpty(minor) OrElse minor = "0", "V" & major, "V" & major & "." & minor)
        End Get
    End Property

    Public Shared Function MetadataValue(name As String) As String
        Dim attribute = EntryAssembly.GetCustomAttributes(Of AssemblyMetadataAttribute)().
            FirstOrDefault(Function(item) String.Equals(item.Key, name, StringComparison.Ordinal))
        Return If(attribute Is Nothing OrElse String.IsNullOrWhiteSpace(attribute.Value),
                  "Not recorded", attribute.Value)
    End Function

    Public Shared ReadOnly Property BuildIdentifier As String
        Get
            Return MetadataValue("BuildIdentifier")
        End Get
    End Property

    Public Shared ReadOnly Property SourceHash As String
        Get
            Return MetadataValue("SourceHash")
        End Get
    End Property

    Public Shared ReadOnly Property BuildStatus As String
        Get
            Return MetadataValue("BuildStatus")
        End Get
    End Property

    Public Shared ReadOnly Property TestStatus As String
        Get
            Return MetadataValue("TestStatus")
        End Get
    End Property
End Class
