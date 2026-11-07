Option Strict On

Imports System.Security.Cryptography
Imports EOEMS.Licensing

''' <summary>Checks a license key's signature using only the public key, so the app itself can
''' never mint a valid license — only the separate Keygen tool (which holds the private key) can.</summary>
Friend NotInheritable Class LicenseVerifier
    ' Public half of the RSA-3072 keypair whose private half lives only in the Keygen tool
    ' (Source\Tools\Keygen). Keep this value in sync with Keygen's embedded private key; if you
    ' ever regenerate the keypair, every previously issued license key stops working.
    Private Const PublicKeyBase64 As String =
        "MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAnH68NbMC7iOMjwb+nIkFcblrgaceE5WiCbboEWPsZwJiDRbV/EirBVwNPFm/GKDqNbd73iVQ5ezn4czIzWmX2gWKRhUKth0dVffhORPj6B7zegS/27s5Cn57NRx3ArE1ICNGpgq/3oDd9d0zBXvuhaSGHwu2vJeZvs8ie9KmX+xfoDnZqNEIe5I37W3Rg+yVPM4STFSLXS176aA5Jj0ZAzdItuOTL0qY7aMtjuCe8eERexLG69sM6QRJiTzHLiFM+Rihu7cbbR1FjzZ2yGvxQXJtJAC/G12txw6LEli8ihEGaCEjWJsImSRs5hmMyb8T4PxJMXFUy/03vb6d31QfyKdTU2tgWYzaGID5iGlM0/jCQJRYtgsEm1EY+HtdR5vO9UyVDssFRh2RXzqSinZpSTo+PGRyMvQnOBhwd2dH2jKOD4fcfmRuk6Y7dAmY6tVmFirUpxzAqpeAKG6LmKWD/Hq+ljSl6gw8xQI731n6sRvqeQ6yeU4ILhk/3YAqfrMNAgMBAAE="

    Public Shared Function Verify(signed As SignedLicense) As Boolean
        Try
            Using rsa As RSA = RSA.Create()
                rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKeyBase64), Nothing)
                Return rsa.VerifyData(signed.MessageBytes, signed.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            End Using
        Catch
            Return False
        End Try
    End Function
End Class
