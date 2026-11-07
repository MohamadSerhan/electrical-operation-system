Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Net.Sockets
Imports System.Text.Json.Serialization

Public NotInheritable Class AppSettings
    Public Property Sql As New SqlSettings()
    Public Property Plc As New PlcSettings()
    Public Property Ui As New UiSettings()

    Public Sub Normalize()
        If Sql Is Nothing Then Sql = New SqlSettings()
        If Plc Is Nothing Then Plc = New PlcSettings()
        If Ui Is Nothing Then Ui = New UiSettings()
        Plc.Normalize()
    End Sub
End Class

Public NotInheritable Class SqlSettings
    Public Property StorageMode As String = "PortableSqlite"
    Public Property PortableDatabasePath As String = "data\eoems.db"
    Public Property ConnectionString As String = String.Empty
    Public Property EnableLegacyPlaintextAuthentication As Boolean = False

    Public Function ResolveDatabasePath(Optional baseDirectory As String = Nothing) As String
        If Not String.Equals(StorageMode, "PortableSqlite", StringComparison.OrdinalIgnoreCase) Then
            Throw New InvalidDataException("Authentication storage must use PortableSqlite.")
        End If
        If Not String.IsNullOrWhiteSpace(ConnectionString) Then
            Throw New InvalidDataException("Central SQL authentication is disabled for this portable application.")
        End If
        If EnableLegacyPlaintextAuthentication Then
            Throw New InvalidDataException("Legacy plaintext authentication is disabled.")
        End If
        If String.IsNullOrWhiteSpace(PortableDatabasePath) OrElse Path.IsPathRooted(PortableDatabasePath) Then
            Throw New InvalidDataException("The portable SQLite database path must be relative to the application folder.")
        End If

        Dim applicationDirectory = Path.GetFullPath(If(baseDirectory, AppContext.BaseDirectory))
        Dim databasePath = Path.GetFullPath(Path.Combine(applicationDirectory, PortableDatabasePath))
        Dim directoryPrefix = Path.TrimEndingDirectorySeparator(applicationDirectory) & Path.DirectorySeparatorChar
        If Not databasePath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase) Then
            Throw New InvalidDataException("The portable SQLite database path must remain inside the application folder.")
        End If
        Return databasePath
    End Function
End Class

Public NotInheritable Class UiSettings
    'This is deliberately false in the shipped configuration. It enables design-only navigation,
    'not a substitute for production authentication.
    Public Property EnableOfflinePreview As Boolean = False
    Public Property RefreshIntervalMilliseconds As Integer

    <JsonIgnore>
    Public Property ConfigurationError As String = String.Empty

    Public Sub Validate()
        If RefreshIntervalMilliseconds <> 0 AndAlso
           Not {10, 25, 50, 100, 250}.Contains(RefreshIntervalMilliseconds) Then
            Throw New InvalidDataException("UI refresh interval must be Auto (100 ms), 10, 25, 50, 100, or 250 milliseconds.")
        End If
    End Sub
End Class

Public NotInheritable Class PlcSettings
    Public Const MinimumAcquisitionIntervalMilliseconds As Integer = 100
    Public Const MaximumAcquisitionIntervalMilliseconds As Integer = 10000
    Public Property Host As String = "192.168.107.120"
    Public Property Port As Integer = 502
    Public Property UnitId As Integer = 1
    Public Property PollingIntervalSeconds As Integer = 10
    Public Property RequestedAcquisitionIntervalMilliseconds As Integer?
    Public Property ConnectTimeoutMilliseconds As Integer = 2000
    Public Property ReadTimeoutMilliseconds As Integer = 2000
    Public Property SwapFloatWords As Boolean = True
    Public Property RegisterAddressOffset As Integer = 0
    Public Property RegisterCount As Integer = 32
    Public Property TotalMaxLoadKw As Double = 10000
    Public Property Protocol As String = "Modbus TCP"
    Public Property Model As String = "Delta AS228T-A"
    Public Property Blocks As List(Of PlcBlock) = New List(Of PlcBlock) From {
        New PlcBlock With {.TransformerName = "T1", .MeterModel = "PM5110", .RegisterStart = 2000},
        New PlcBlock With {.TransformerName = "T2", .MeterModel = "PM5110", .RegisterStart = 2050},
        New PlcBlock With {.TransformerName = "T3", .MeterModel = "PM5110", .RegisterStart = 2100},
        New PlcBlock With {.TransformerName = "T4", .MeterModel = "PM5110", .RegisterStart = 2150}
    }

    <JsonIgnore>
    Public Property ConfigurationError As String = String.Empty

    <JsonIgnore>
    Public ReadOnly Property EffectivePollingIntervalMilliseconds As Integer
        Get
            Return If(RequestedAcquisitionIntervalMilliseconds.HasValue,
                      RequestedAcquisitionIntervalMilliseconds.Value,
                      PollingIntervalSeconds * 1000)
        End Get
    End Property

    Public Function Clone() As PlcSettings
        Return New PlcSettings With {
            .Host = Host,
            .Port = Port,
            .UnitId = UnitId,
            .PollingIntervalSeconds = PollingIntervalSeconds,
            .RequestedAcquisitionIntervalMilliseconds = RequestedAcquisitionIntervalMilliseconds,
            .ConnectTimeoutMilliseconds = ConnectTimeoutMilliseconds,
            .ReadTimeoutMilliseconds = ReadTimeoutMilliseconds,
            .SwapFloatWords = SwapFloatWords,
            .RegisterAddressOffset = RegisterAddressOffset,
            .RegisterCount = RegisterCount,
            .TotalMaxLoadKw = TotalMaxLoadKw,
            .Protocol = Protocol,
            .Model = Model,
            .Blocks = If(Blocks Is Nothing, New List(Of PlcBlock)(), Blocks.Select(
                Function(block) If(block Is Nothing, Nothing, New PlcBlock With {
                    .TransformerName = block.TransformerName,
                    .MeterModel = block.MeterModel,
                    .RegisterStart = block.RegisterStart,
                    .MaxLoadKw = block.MaxLoadKw
                })).ToList()),
            .ConfigurationError = ConfigurationError
        }
    End Function

    Public Sub Normalize()
        If String.IsNullOrWhiteSpace(Host) Then Host = "192.168.107.120"
        If Not RequestedAcquisitionIntervalMilliseconds.HasValue Then
            RequestedAcquisitionIntervalMilliseconds = Math.Min(MaximumAcquisitionIntervalMilliseconds, PollingIntervalSeconds * 1000)
        ElseIf RequestedAcquisitionIntervalMilliseconds.Value > MaximumAcquisitionIntervalMilliseconds AndAlso
               RequestedAcquisitionIntervalMilliseconds.Value <= 3600000 Then
            ' Live monitoring is limited to 0.1–10 s; slower legacy values move to 10 s.
            RequestedAcquisitionIntervalMilliseconds = MaximumAcquisitionIntervalMilliseconds
        End If
        If RequestedAcquisitionIntervalMilliseconds.Value >= MinimumAcquisitionIntervalMilliseconds AndAlso
           RequestedAcquisitionIntervalMilliseconds.Value <= MaximumAcquisitionIntervalMilliseconds Then
            PollingIntervalSeconds = CInt(Math.Ceiling(RequestedAcquisitionIntervalMilliseconds.Value / 1000.0R))
        End If
        If Port < 1 OrElse Port > 65535 Then Port = 502
        If ConnectTimeoutMilliseconds < 250 Then ConnectTimeoutMilliseconds = 1500
        If String.IsNullOrWhiteSpace(Protocol) Then Protocol = "Modbus TCP"
        If String.IsNullOrWhiteSpace(Model) Then Model = "Delta AS228T-A"
        If Blocks Is Nothing OrElse Blocks.Count = 0 Then
            Blocks = New List(Of PlcBlock) From {
                New PlcBlock With {.TransformerName = "T1", .MeterModel = "PM5110", .RegisterStart = 2000},
                New PlcBlock With {.TransformerName = "T2", .MeterModel = "PM5110", .RegisterStart = 2050},
                New PlcBlock With {.TransformerName = "T3", .MeterModel = "PM5110", .RegisterStart = 2100},
                New PlcBlock With {.TransformerName = "T4", .MeterModel = "PM5110", .RegisterStart = 2150}
            }
        ElseIf Blocks.Count <> 4 Then
            ConfigurationError = "Exactly four transformer blocks must be configured."
            Dim defaults = New PlcSettings().Blocks
            If Blocks.Count < 4 Then
                For index = Blocks.Count To 3
                    Blocks.Add(New PlcBlock With {
                        .TransformerName = defaults(index).TransformerName,
                        .MeterModel = defaults(index).MeterModel,
                        .RegisterStart = defaults(index).RegisterStart
                    })
                Next
            Else
                Blocks.RemoveRange(4, Blocks.Count - 4)
            End If
        End If
        Dim fallbackBlocks = New PlcSettings().Blocks
        For index = 0 To 3
            If Blocks(index) Is Nothing Then
                ConfigurationError = "A configured transformer block is missing."
                Blocks(index) = New PlcBlock With {
                    .TransformerName = fallbackBlocks(index).TransformerName,
                    .MeterModel = fallbackBlocks(index).MeterModel,
                    .RegisterStart = fallbackBlocks(index).RegisterStart
                }
            End If
        Next
    End Sub

    Public Sub Validate()
        Dim address As IPAddress = Nothing
        If String.IsNullOrWhiteSpace(Host) OrElse Not IPAddress.TryParse(Host, address) OrElse address.AddressFamily <> AddressFamily.InterNetwork Then
            Throw New InvalidDataException("PLC host must be a valid IPv4 address.")
        End If
        If Port < 1 OrElse Port > 65535 Then Throw New InvalidDataException("PLC port must be between 1 and 65535.")
        If UnitId < 0 OrElse UnitId > 247 Then Throw New InvalidDataException("Modbus Unit ID must be between 0 and 247.")
        If PollingIntervalSeconds < 1 OrElse PollingIntervalSeconds > 3600 Then Throw New InvalidDataException("Legacy polling interval must be between 1 and 3600 seconds.")
        If EffectivePollingIntervalMilliseconds < MinimumAcquisitionIntervalMilliseconds OrElse
           EffectivePollingIntervalMilliseconds > MaximumAcquisitionIntervalMilliseconds Then
            Throw New InvalidDataException("Live acquisition interval must be between 0.1 and 10 seconds.")
        End If
        If ReadTimeoutMilliseconds < 250 OrElse ReadTimeoutMilliseconds > 10000 Then Throw New InvalidDataException("Read timeout must be between 250 and 10000 milliseconds.")
        If RegisterAddressOffset <> 0 AndAlso RegisterAddressOffset <> 1 Then Throw New InvalidDataException("Register addressing offset must be either 0 or 1.")
        If RegisterCount <= 0 OrElse RegisterCount > 125 Then Throw New InvalidDataException("Register count must be between 1 and 125.")
        If RegisterCount <> 32 Then Throw New InvalidDataException("Register count must be 32 for the configured 16-float PM5110 map.")
        If Not Double.IsFinite(TotalMaxLoadKw) OrElse TotalMaxLoadKw <= 0 Then Throw New InvalidDataException("Total maximum load must be greater than zero.")
        If Blocks Is Nothing OrElse Blocks.Count <> 4 Then Throw New InvalidDataException("Exactly four PM5110 transformer blocks are required.")

        Dim expectedNames = {"T1", "T2", "T3", "T4"}
        Dim expectedStarts = {2000, 2050, 2100, 2150}
        For index = 0 To Blocks.Count - 1
            Dim block = Blocks(index)
            If block Is Nothing OrElse Not String.Equals(block.TransformerName, expectedNames(index), StringComparison.Ordinal) OrElse
               Not String.Equals(block.MeterModel, "PM5110", StringComparison.OrdinalIgnoreCase) OrElse block.RegisterStart <> expectedStarts(index) Then
                Throw New InvalidDataException("The fixed meter map must be T1–T4 PM5110 blocks starting at 2000, 2050, 2100, and 2150.")
            End If
            If Not Double.IsFinite(block.MaxLoadKw) OrElse block.MaxLoadKw <= 0 Then Throw New InvalidDataException("Each transformer maximum load must be greater than zero.")
            Dim effectiveStart = block.RegisterStart - RegisterAddressOffset
            If effectiveStart < 0 OrElse effectiveStart > 65536 - RegisterCount Then Throw New InvalidDataException($"Register block {block.TransformerName} is outside the configured Modbus address range.")
            For prior = 0 To index - 1
                Dim otherStart = Blocks(prior).RegisterStart - RegisterAddressOffset
                If effectiveStart < otherStart + 32 AndAlso otherStart < effectiveStart + 32 Then Throw New InvalidDataException("Configured 32-register transformer blocks overlap.")
            Next
        Next
    End Sub
End Class

Public NotInheritable Class PlcBlock
    Public Property TransformerName As String = String.Empty
    Public Property MeterModel As String = "PM5110"
    Public Property RegisterStart As Integer
    Public Property MaxLoadKw As Double = 2500
End Class

Public Module LoadGaugeMath
    Public Const TransformerGreenZoneEndKw As Double = 1400.0R
    Public Const TransformerRedZoneStartKw As Double = 2000.0R
    Public Const TotalGreenZoneEndKw As Double = 4000.0R
    Public Const TotalRedZoneStartKw As Double = 7000.0R
    Public Const MaximumTotalAlignmentSkewMilliseconds As Double = 250.0R

    Public Function GetLoadPercent(value As Double?, maximum As Double?) As Double?
        If Not value.HasValue OrElse Not maximum.HasValue OrElse
           Not Double.IsFinite(value.Value) OrElse Not Double.IsFinite(maximum.Value) OrElse maximum.Value <= 0 Then Return Nothing
        Dim ratio = value.Value / maximum.Value
        If Double.IsPositiveInfinity(ratio) Then Return Double.MaxValue
        If Double.IsNegativeInfinity(ratio) Then Return -Double.MaxValue
        Dim percent = ratio * 100.0R
        If Double.IsPositiveInfinity(percent) Then Return Double.MaxValue
        If Double.IsNegativeInfinity(percent) Then Return -Double.MaxValue
        Return If(Double.IsFinite(percent), CType(percent, Double?), Nothing)
    End Function

    Public Function GetLoadZone(percent As Double?, greenEndPercent As Double, redStartPercent As Double) As String
        If Not percent.HasValue OrElse Not Double.IsFinite(percent.Value) Then Return "Not polled"
        If percent.Value < 0.0R Then Return "UNDER RANGE"
        If percent.Value > 100.0R Then Return "OVERLOAD"
        If percent.Value >= redStartPercent Then Return "RED"
        If percent.Value >= greenEndPercent Then Return "YELLOW"
        Return "GREEN"
    End Function

    Public Function CalculateTotalLoad(snapshots As IReadOnlyList(Of MeterSnapshot)) As Double?
        If snapshots Is Nothing OrElse snapshots.Count <> 4 Then Return Nothing
        Dim requiredNames = {"T1", "T2", "T3", "T4"}
        If requiredNames.Any(Function(name) snapshots.Where(Function(snapshot) snapshot IsNot Nothing AndAlso
            String.Equals(snapshot.TransformerName, name, StringComparison.OrdinalIgnoreCase)).Count() <> 1) Then Return Nothing
        Dim total As Double = 0
        For Each snapshot In snapshots
            If snapshot Is Nothing OrElse snapshot.Quality <> MeterQuality.Good OrElse
               snapshot.DecodedValues Is Nothing OrElse snapshot.DecodedValues.Length <= 10 Then Return Nothing
            Dim value = CDbl(snapshot.DecodedValues(10))
            If Not Double.IsFinite(value) Then Return Nothing
            total += value
        Next
        If Not Double.IsFinite(total) Then Return Nothing
        Return total
    End Function
End Module

Public Module PlcConfigurationAccess
    Public Function CanEdit(roleName As String, isOfflinePreview As Boolean) As Boolean
        Return Not isOfflinePreview AndAlso String.Equals(If(roleName, String.Empty).Trim(), "Administrator", StringComparison.OrdinalIgnoreCase)
    End Function
End Module

Public Enum MeterQuality
    NotChecked
    Connecting
    Good
    Offline
    BadQuality
    MappingError
    Timeout
End Enum

Public NotInheritable Class MeterSnapshot
    Public Property TransformerName As String = String.Empty
    Public Property RegisterStart As Integer
    Public Property EffectiveRegisterStart As Integer
    Public Property RegisterCount As Integer = 32
    Public Property CapturedAtUtc As DateTime?
    Public Property AttemptStartedAtUtc As DateTime?
    Public Property AcquisitionCycleId As Long
    Public Property AttemptCount As Integer
    Public Property ResponseLatencyMilliseconds As Double?
    Public Property Quality As MeterQuality = MeterQuality.NotChecked
    Public Property CommunicationStatus As String = "Not checked"
    Public Property RawRegisters As UShort()
    Public Property DecodedValues As Single()
    Public Property ErrorMessage As String = String.Empty
    Public Property PowerState As TransformerPowerState = TransformerPowerState.Unknown
    Public Property PowerDetail As String = String.Empty
    Public Property EcgOffConfirmed As Boolean
    ' True for 60 s after all four transformers are confirmed back on following an ECG outage.
    Public Property EcgBackConfirmed As Boolean
End Class

Public Enum PlcHealth
    NotChecked
    Connecting
    Connected
    PartialCommunication
    Unreachable
    ErrorState
    BadQuality
    MappingError
    Timeout
End Enum

Public NotInheritable Class PlcConnectionStatus
    Public Property PowerSummary As String = String.Empty
    Public Property PowerLogError As String = String.Empty
    Public Property Health As PlcHealth = PlcHealth.NotChecked
    Public Property Detail As String = "No connection attempt has been made."
    Public Property CheckedAtUtc As DateTime? = Nothing
    Public Property LatencyMilliseconds As Long? = Nothing
    Public Property PollingIntervalSeconds As Integer
    Public Property RequestedIntervalMilliseconds As Integer
    Public Property LastPollUtc As DateTime?
    Public Property Diagnostics As AcquisitionDiagnosticsSnapshot = New AcquisitionDiagnosticsSnapshot()
End Class

Public NotInheritable Class AcquisitionDiagnosticsSnapshot
    Public Property CompletedCycles As Long
    Public Property SuccessfulCycles As Long
    Public Property FailedCycles As Long
    Public Property SuccessfulTransactions As Long
    Public Property FailedTransactions As Long
    Public Property RetryCount As Long
    Public Property TimeoutCount As Long
    Public Property LastCycleDurationMilliseconds As Double?
    Public Property AverageCycleDurationMilliseconds As Double?
    Public Property P95CycleDurationMilliseconds As Double?
    Public Property MaximumCycleDurationMilliseconds As Double?
    Public Property AverageCycleIntervalMilliseconds As Double?
    Public Property SuccessfulCycleRateHz As Double?
    Public Property LastCycleCompletedUtc As DateTime?
    Public Property LastSuccessfulCycleCompletedUtc As DateTime?
    Public Property SourceFreshnessAvailable As Boolean
End Class

Public NotInheritable Class OperationalLogDiagnosticsSnapshot
    Public Property QueueDepth As Integer
    Public Property DroppedAttempts As Long
    Public Property WriteFailures As Long
    Public Property LastWriteLatencyMilliseconds As Double?
    Public Property LastWriteUtc As DateTime?
    Public Property LastError As String = String.Empty
End Class

Public NotInheritable Class AuthResult
    Public Property Succeeded As Boolean
    Public Property Message As String = String.Empty
    Public Property Session As UserSession
End Class

Public NotInheritable Class UserSession
    Public Property UserId As String = String.Empty
    Public Property Username As String = String.Empty
    Public Property DisplayName As String = String.Empty
    Public Property RoleName As String = String.Empty
    Public Property IsOfflinePreview As Boolean
    Public Property IsSuperAdministrator As Boolean

    Public ReadOnly Property RoleDisplay As String
        Get
            Return If(IsSuperAdministrator, "Super Administrator", RoleName)
        End Get
    End Property

    Public Shared Function OfflinePreview() As UserSession
        Return New UserSession With {
            .UserId = "OFFLINE-PREVIEW",
            .Username = "OFFLINE-PREVIEW",
            .DisplayName = "Offline design preview",
            .RoleName = "No live access",
            .IsOfflinePreview = True
        }
    End Function
End Class
