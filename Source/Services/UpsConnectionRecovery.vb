Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Threading
Imports System.Threading.Tasks

Public NotInheritable Class UpsConnectionRecoveryResult
    Public Property TimestampUtc As DateTime = DateTime.UtcNow
    Public Property ProfileName As String = "default"
    Public Property ProfileVersion As String = String.Empty
    Public Property CredentialAvailable As Boolean
    Public Property DpapiDecryptionSuccessful As Boolean
    Public Property ProfileAssigned As Boolean
    Public Property ProfilePreserved As Boolean
    Public Property ReferenceTests As New List(Of UpsConnectionTest)()
    Public Property ReferencePolls As New List(Of UpsSnapshot)()
    Public Property TargetTest As UpsConnectionTest
    Public Property TargetPoll As UpsSnapshot
    Public Property MappingVerified As Boolean
    Public Property LivePollRequested As Boolean
    Public Property Detail As String = String.Empty

    Public ReadOnly Property ReferencesVerified As Boolean
        Get
            Return {"UPS-C01", "UPS-C03"}.All(Function(id)
                Return ReferenceTests.Any(Function(test) test.DeviceId = id AndAlso test.IsConnected AndAlso test.ProfileVersion = "v2c") AndAlso
                    ReferencePolls.Any(Function(poll) poll.DeviceId = id AndAlso poll.IsGood)
            End Function)
        End Get
    End Property

    Public ReadOnly Property IdentityRead As Boolean
        Get
            Return TargetTest IsNot Nothing AndAlso TargetTest.IsConnected AndAlso TargetTest.ProfileVersion = "v2c" AndAlso
                TargetTest.SysName.Length > 0 AndAlso TargetTest.SysObjectId.Length > 0 AndAlso TargetTest.SupportsUpsMib AndAlso
                TargetTest.Manufacturer.Length > 0 AndAlso TargetTest.Model.Length > 0
        End Get
    End Property

    Public ReadOnly Property MeasurementsRead As Boolean
        Get
            Return UpsCredentialConfiguration.HasElectricalRead(TargetPoll)
        End Get
    End Property

    Public ReadOnly Property PreviousIdentityMatches As Boolean
        Get
            Return IdentityRead AndAlso String.Equals(TargetTest.SysName, "UPS - 11", StringComparison.Ordinal) AndAlso
                TargetTest.SysObjectId = "1.3.6.1.4.1.476.1.42"
        End Get
    End Property

    Public ReadOnly Property ConnectionVerified As Boolean
        Get
            Return CredentialAvailable AndAlso DpapiDecryptionSuccessful AndAlso ProfileAssigned AndAlso ProfilePreserved AndAlso
                ReferencesVerified AndAlso IdentityRead AndAlso PreviousIdentityMatches AndAlso MappingVerified AndAlso MeasurementsRead
        End Get
    End Property
End Class

Public NotInheritable Class UpsConnectionRecovery
    Private Sub New()
    End Sub

    Public Const LocalProcedure As String =
        "C04 192.168.107.16 must use the approved read-only SNMPv1/v2c community matching EOEMS profile 'default'." & vbCrLf &
        "1. Verify the existing profile and C01/C03 first. Do not edit, reset, rotate or duplicate default during this repair." & vbCrLf &
        "2. On the local workstation, open the approved C04 IntelliSlot management page and authenticate as the card administrator." & vbCrLf &
        "3. Retrieve the original approved SNMP community from the site's password vault. Do not send it to chat, screenshots, logs or exports." & vbCrLf &
        "4. In SNMPv1/v2c Access (NOT SNMPv1 Trap), restore only C04's matching read-only community. Keep the entry and v2c enabled, " &
        "and verify the allowed manager/source IP, UDP port and UPS-MIB read view." & vbCrLf &
        "5. Save the card-side change locally under your site's procedure, then retest here. EOEMS never changes the card or sends SET." & vbCrLf &
        "6. A real v2c identity GET and electrical measurement poll must pass before recovery is confirmed. Engine discovery or ping is not authentication." & vbCrLf &
        "If the original credential cannot be recovered, stop: do not overwrite default. Use an independently named C04-only profile " &
        "with an approved new read-only community, configure only C04 to match, then explicitly assign/test it through the registry."

    Public Shared Function VerifyAsync(session As UserSession, token As CancellationToken) As Task(Of UpsConnectionRecoveryResult)
        If session Is Nothing OrElse String.IsNullOrWhiteSpace(session.UserId) OrElse String.IsNullOrWhiteSpace(session.Username) OrElse
           Not PlcConfigurationAccess.CanEdit(session.RoleName, session.IsOfflinePreview) Then
            Throw New UnauthorizedAccessException("Only an authenticated administrator can restore a UPS connection.")
        End If
        Dim settings = UpsSettingsStore.Current.Clone()
        Dim target = settings.Devices.SingleOrDefault(Function(device) device.Id = "UPS-C04")
        If target Is Nothing OrElse target.IpAddress <> "192.168.107.16" Then
            Throw New InvalidDataException("C04's registered identity/IP does not match the recovery target. No settings have been changed.")
        End If
        Dim peers = settings.Devices.Where(Function(device) device.Id = "UPS-C01" OrElse device.Id = "UPS-C03").ToList()
        If peers.Count <> 2 OrElse peers.Any(Function(device) Not String.Equals(device.CredentialProfile, "default", StringComparison.OrdinalIgnoreCase)) OrElse
           settings.Devices.Single(Function(device) device.Id = "UPS-C01").IpAddress <> "192.168.107.7" OrElse
           settings.Devices.Single(Function(device) device.Id = "UPS-C03").IpAddress <> "192.168.107.17" Then
            Throw New InvalidDataException("The C01/C03 reference registrations no longer match their known-good default assignments. Stop and review the registry.")
        End If
        Return Task.Run(Function() Verify(settings, target, peers, token), token)
    End Function

    Private Shared Function Verify(settings As UpsSettings, target As UpsDevice, peers As IReadOnlyList(Of UpsDevice),
                                   token As CancellationToken) As UpsConnectionRecoveryResult
        Dim result As New UpsConnectionRecoveryResult With {
            .ProfileAssigned = String.Equals(target.CredentialProfile, "default", StringComparison.OrdinalIgnoreCase)}
        Dim path = SnmpCredentialStore.StorePath()
        If Not File.Exists(path) Then
            result.Detail = "Original credential unavailable in this store. Investigate authorized protected backups under the original Windows account. Do not overwrite default."
            Return result
        End If
        Dim before = SHA256.HashData(File.ReadAllBytes(path))
        Dim initialStamps = settings.Devices.Where(Function(device) device.Id = target.Id OrElse peers.Any(Function(peer) peer.Id = device.Id)).
            ToDictionary(Function(device) device.Id, Function(device) SnmpCredentialStore.ConnectionStamp(SnmpCredentialStore.EffectiveDevice(device)))
        Try
            Dim summary = SnmpCredentialStore.ListProfiles().SingleOrDefault(Function(profile) String.Equals(profile.Name, "default", StringComparison.OrdinalIgnoreCase))
            If summary Is Nothing Then
                result.Detail = "The original default profile is unavailable. Investigate protected backups; do not guess credentials or overwrite any shared profile."
                Return result
            End If
            result.ProfileVersion = summary.Version
            Dim credential = SnmpCredentialStore.Resolve(summary.Name)
            result.DpapiDecryptionSuccessful = credential IsNot Nothing
            result.CredentialAvailable = credential IsNot Nothing AndAlso credential.Community.Length > 0
            If Not result.ProfileAssigned OrElse summary.Version <> "v2c" OrElse Not result.CredentialAvailable Then
                result.Detail = "Stop: C04 must already be assigned to the original usable default v2c profile. Review the saved assignment; this action never changes it."
                Return result
            End If
            For Each peer In peers
                token.ThrowIfCancellationRequested()
                Dim test = UpsConnectionDiagnostics.TestDevice(peer, token)
                result.ReferenceTests.Add(test)
                token.ThrowIfCancellationRequested()
                If test.IsConnected Then result.ReferencePolls.Add(UpsAcquisitionService.PollDevice(peer, 0, Nothing))
            Next
            token.ThrowIfCancellationRequested()
            result.TargetTest = UpsConnectionDiagnostics.TestDevice(target, token)
            token.ThrowIfCancellationRequested()
            If result.TargetTest.IsConnected Then
                result.TargetPoll = UpsAcquisitionService.PollDevice(target, 0, Nothing)
                Dim audit = UpsConnectionDiagnostics.LastAudit(target.Id)
                result.MappingVerified = audit IsNot Nothing AndAlso audit.MappingVerified
            End If
        Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is UnauthorizedAccessException
            result.Detail = "Recovery could not be verified: " & ex.Message & " No profile or card setting was changed."
        Finally
            result.ProfilePreserved = File.Exists(path) AndAlso CryptographicOperations.FixedTimeEquals(before, SHA256.HashData(File.ReadAllBytes(path)))
        End Try
        Dim current = UpsSettingsStore.Current
        If initialStamps.Any(Function(pair)
                                 Dim device = current.Devices.SingleOrDefault(Function(item) item.Id = pair.Key)
                                 Return device Is Nothing OrElse pair.Value <> SnmpCredentialStore.ConnectionStamp(SnmpCredentialStore.EffectiveDevice(device))
                             End Function) Then
            result.ProfilePreserved = False
            result.Detail = "Connection settings changed during verification. Stop and rerun against the saved configuration; recovery is not confirmed."
        End If
        If result.ConnectionVerified Then
            result.Detail = "C04 v2c identity, UPS-MIB mapping and electrical reads verified. C01/C03 still pass with the unchanged default profile."
            If settings.AcquisitionEnabled AndAlso target.Enabled AndAlso UpsAcquisitionService.Current IsNot Nothing AndAlso UpsAcquisitionService.Current.IsRunning Then
                UpsAcquisitionService.Current.PollNow(target.Id)
                result.LivePollRequested = True
                result.Detail &= " A normal acquisition poll was requested. Verify its live snapshot and historian separately."
            Else
                result.Detail &= " Continuous acquisition is not running or is disabled; no monitoring setting has been changed."
            End If
        ElseIf result.Detail.Length = 0 Then
            result.Detail = If(Not result.ReferencesVerified, "C01/C03 regression did not pass; stop and investigate before changing any card.",
                If(Not result.IdentityRead, "C04 has not returned a valid v2c identity. Restore C04's matching approved community locally, then retest.",
                   If(Not result.PreviousIdentityMatches, "C04 replied, but sysName/sysObjectID differ from the previously verified C04 identity. Stop and confirm the physical device; recovery is not confirmed.",
                      "C04 replied, but complete identity/mapping/electrical verification did not pass. Recovery is not confirmed.")))
        End If
        Return result
    End Function
End Class
