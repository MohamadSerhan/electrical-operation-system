Imports System.Security.Cryptography
Imports System.Text

Public NotInheritable Class PasswordHasher
    Public Shared Function HashPbkdf2(password As String, Optional iterations As Integer = 210000) As String
        If String.IsNullOrWhiteSpace(password) Then Throw New ArgumentException("A password is required.", NameOf(password))
        Dim salt = RandomNumberGenerator.GetBytes(16)
        Dim hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA512, 32)
        Return "PBKDF2$" & iterations.ToString() & "$" & Convert.ToBase64String(salt) & "$" & Convert.ToBase64String(hash)
    End Function

    Public Shared Function VerifyPbkdf2(password As String, storedValue As String) As Boolean
        If String.IsNullOrWhiteSpace(password) OrElse String.IsNullOrWhiteSpace(storedValue) Then Return False
        Dim parts = storedValue.Split("$"c)
        If parts.Length <> 4 OrElse Not String.Equals(parts(0), "PBKDF2", StringComparison.OrdinalIgnoreCase) Then Return False

        Dim iterations As Integer
        If Not Integer.TryParse(parts(1), iterations) OrElse iterations < 100000 Then Return False

        Try
            Dim salt = Convert.FromBase64String(parts(2))
            Dim expected = Convert.FromBase64String(parts(3))
            Dim actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA512, expected.Length)
            Return CryptographicOperations.FixedTimeEquals(actual, expected)
        Catch ex As FormatException
            Return False
        End Try
    End Function

End Class
