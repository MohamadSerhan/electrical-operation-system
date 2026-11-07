Option Strict On

Imports System.Globalization
Imports System.Linq
Imports System.Text

''' <summary>The license plans a key can be issued for. The expiry is computed from
''' <see cref="LicensePayload.ComputeExpiryUtc"/> at generation time and then baked into the
''' signed payload, so changing this enum later does not change already-issued keys.</summary>
Public Enum LicensePlan
    Trial
    SixMonths
    OneYear
    TwoYears
    FiveYears
    TenYears
    Lifetime
End Enum

''' <summary>The data a license key certifies. Signed as plain, versioned, pipe-delimited text
''' rather than JSON so both the app and the Keygen tool can parse it with no extra dependency.</summary>
Public NotInheritable Class LicensePayload
    Public Const CurrentProduct As String = "EOEMS-SCADA-V5"
    Private Const FormatVersion As Integer = 1
    Private Const PerpetualMarker As String = "PERPETUAL"
    Private Const FieldCount As Integer = 9

    Public Property LicenseId As Guid
    Public Property Product As String = CurrentProduct
    Public Property Licensee As String = String.Empty
    Public Property Email As String = String.Empty
    Public Property RequestCode As String = String.Empty
    Public Property IssuedUtc As DateTime
    Public Property ExpiresUtc As DateTime?
    Public Property Plan As LicensePlan

    ''' <summary>The trial/term length for each plan. Lifetime returns Nothing (never expires).</summary>
    Public Shared Function ComputeExpiryUtc(plan As LicensePlan, issuedUtc As DateTime) As DateTime?
        Select Case plan
            Case LicensePlan.Trial : Return issuedUtc.AddDays(14)
            Case LicensePlan.SixMonths : Return issuedUtc.AddMonths(6)
            Case LicensePlan.OneYear : Return issuedUtc.AddYears(1)
            Case LicensePlan.TwoYears : Return issuedUtc.AddYears(2)
            Case LicensePlan.FiveYears : Return issuedUtc.AddYears(5)
            Case LicensePlan.TenYears : Return issuedUtc.AddYears(10)
            Case LicensePlan.Lifetime : Return Nothing
            Case Else : Throw New ArgumentOutOfRangeException(NameOf(plan))
        End Select
    End Function

    Public Shared Function PlanDisplayName(plan As LicensePlan) As String
        Select Case plan
            Case LicensePlan.Trial : Return "Trial (14 days)"
            Case LicensePlan.SixMonths : Return "6 months"
            Case LicensePlan.OneYear : Return "1 year"
            Case LicensePlan.TwoYears : Return "2 years"
            Case LicensePlan.FiveYears : Return "5 years"
            Case LicensePlan.TenYears : Return "10 years"
            Case LicensePlan.Lifetime : Return "Lifetime (no expiry)"
            Case Else : Return plan.ToString()
        End Select
    End Function

    ''' <summary>The exact bytes that get signed / verified. Any field change invalidates the signature.</summary>
    Public Function ToMessageBytes() As Byte()
        Dim expiresText = If(ExpiresUtc.HasValue, ExpiresUtc.Value.ToString("O", CultureInfo.InvariantCulture), PerpetualMarker)
        Dim text = String.Join("|",
            FormatVersion.ToString(CultureInfo.InvariantCulture),
            LicenseId.ToString("N"),
            Product,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(If(Licensee, String.Empty))),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(If(Email, String.Empty))),
            RequestCode,
            IssuedUtc.ToString("O", CultureInfo.InvariantCulture),
            expiresText,
            Plan.ToString())
        Return Encoding.UTF8.GetBytes(text)
    End Function

    Public Shared Function Parse(messageBytes As Byte()) As LicensePayload
        Dim text = Encoding.UTF8.GetString(messageBytes)
        Dim parts = text.Split("|"c)
        If parts.Length <> FieldCount Then Throw New FormatException("Malformed license payload.")
        If Not String.Equals(parts(0), FormatVersion.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) Then
            Throw New FormatException("Unsupported license format version.")
        End If
        Dim payload As New LicensePayload With {
            .LicenseId = Guid.Parse(parts(1)),
            .Product = parts(2),
            .Licensee = Encoding.UTF8.GetString(Convert.FromBase64String(parts(3))),
            .Email = Encoding.UTF8.GetString(Convert.FromBase64String(parts(4))),
            .RequestCode = parts(5),
            .IssuedUtc = DateTime.Parse(parts(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            .Plan = CType([Enum].Parse(GetType(LicensePlan), parts(8)), LicensePlan)
        }
        payload.ExpiresUtc = If(String.Equals(parts(7), PerpetualMarker, StringComparison.Ordinal),
            CType(Nothing, DateTime?),
            DateTime.Parse(parts(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
        Return payload
    End Function
End Class

''' <summary>A license payload plus its RSA signature, packed into one copy/paste-able key string
''' ("base64(payload).base64(signature)"). Parsing never needs the private key.</summary>
Public NotInheritable Class SignedLicense
    Public Property Payload As LicensePayload
    Public Property MessageBytes As Byte()
    Public Property Signature As Byte()

    Public Function ToLicenseKeyText() As String
        Return Convert.ToBase64String(MessageBytes) & "." & Convert.ToBase64String(Signature)
    End Function

    Public Shared Function TryParse(licenseKeyText As String, ByRef result As SignedLicense) As Boolean
        result = Nothing
        If String.IsNullOrWhiteSpace(licenseKeyText) Then Return False
        ' Users may paste a wrapped block of text; whitespace carries no meaning in the key.
        Dim cleaned = New String(licenseKeyText.Where(Function(c) Not Char.IsWhiteSpace(c)).ToArray())
        Dim dot = cleaned.IndexOf("."c)
        If dot <= 0 OrElse dot >= cleaned.Length - 1 Then Return False
        Try
            Dim messageBytes = Convert.FromBase64String(cleaned.Substring(0, dot))
            Dim signature = Convert.FromBase64String(cleaned.Substring(dot + 1))
            Dim payload = LicensePayload.Parse(messageBytes)
            result = New SignedLicense With {.Payload = payload, .MessageBytes = messageBytes, .Signature = signature}
            Return True
        Catch
            Return False
        End Try
    End Function
End Class
