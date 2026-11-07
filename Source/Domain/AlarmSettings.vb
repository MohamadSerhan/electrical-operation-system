Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text.Json

Public Enum AlarmSeverity
    Normal = 0
    PreAlarm = 1
    Alarm = 2
    ShedLoad = 3
End Enum

' Operator alarm limits for one equipment group. These are display/annunciation limits, not protection settings.
Public NotInheritable Class EquipmentAlarmLimits
    Public Property UnderVoltageV As Double = 215.0R
    Public Property OverVoltageV As Double = 245.0R
    Public Property LoadPreAlarmPercent As Double = 70.0R
    Public Property LoadAlarmPercent As Double = 80.0R
    Public Property LoadShedPercent As Double = 90.0R
    Public Property TemperaturePreAlarmC As Double = 65.0R
    Public Property TemperatureAlarmC As Double = 75.0R
    Public Property TemperatureShedC As Double = 90.0R

    Public Function Clone() As EquipmentAlarmLimits
        Return DirectCast(MemberwiseClone(), EquipmentAlarmLimits)
    End Function

    Public Sub Validate(groupName As String)
        Dim values = {UnderVoltageV, OverVoltageV, LoadPreAlarmPercent, LoadAlarmPercent, LoadShedPercent,
                      TemperaturePreAlarmC, TemperatureAlarmC, TemperatureShedC}
        If values.Any(Function(value) Not Double.IsFinite(value)) Then
            Throw New InvalidDataException(groupName & ": all alarm limits must be valid numbers.")
        End If
        If UnderVoltageV <= 0.0R OrElse OverVoltageV > 1000.0R OrElse UnderVoltageV >= OverVoltageV Then
            Throw New InvalidDataException(groupName & ": under-voltage must be above 0 V and below over-voltage (max 1000 V).")
        End If
        If LoadPreAlarmPercent <= 0.0R OrElse LoadShedPercent > 200.0R OrElse
           Not (LoadPreAlarmPercent < LoadAlarmPercent AndAlso LoadAlarmPercent < LoadShedPercent) Then
            Throw New InvalidDataException(groupName & ": load limits must rise in order pre-alarm < alarm < shed load (0–200 %).")
        End If
        If TemperaturePreAlarmC <= 0.0R OrElse TemperatureShedC > 250.0R OrElse
           Not (TemperaturePreAlarmC < TemperatureAlarmC AndAlso TemperatureAlarmC < TemperatureShedC) Then
            Throw New InvalidDataException(groupName & ": temperature limits must rise in order pre-alarm < alarm < shed load (0–250 °C).")
        End If
    End Sub
End Class

Public NotInheritable Class AlarmSettings
    Public Property SchemaVersion As Integer = 1
    Public Property Transformers As EquipmentAlarmLimits = New EquipmentAlarmLimits()
    Public Property TotalLoad As EquipmentAlarmLimits = New EquipmentAlarmLimits()
    ' Prepared for equipment that is not commissioned yet; limits are stored but not yet evaluated.
    Public Property Generators As EquipmentAlarmLimits = New EquipmentAlarmLimits()
    Public Property Ups As EquipmentAlarmLimits = New EquipmentAlarmLimits()
    Public Property Loads As EquipmentAlarmLimits = New EquipmentAlarmLimits()
    Public Property Rooms As RoomTemperatureLimits = New RoomTemperatureLimits()

    Public Function Clone() As AlarmSettings
        Return New AlarmSettings With {
            .SchemaVersion = SchemaVersion,
            .Transformers = Transformers.Clone(),
            .TotalLoad = TotalLoad.Clone(),
            .Generators = Generators.Clone(),
            .Ups = Ups.Clone(),
            .Loads = Loads.Clone(),
            .Rooms = Rooms.Clone()
        }
    End Function

    Public Sub Normalize()
        If Transformers Is Nothing Then Transformers = New EquipmentAlarmLimits()
        If TotalLoad Is Nothing Then TotalLoad = New EquipmentAlarmLimits()
        If Generators Is Nothing Then Generators = New EquipmentAlarmLimits()
        If Ups Is Nothing Then Ups = New EquipmentAlarmLimits()
        If Loads Is Nothing Then Loads = New EquipmentAlarmLimits()
        If Rooms Is Nothing Then Rooms = New RoomTemperatureLimits()
    End Sub

    Public Sub Validate()
        Normalize()
        Transformers.Validate("Transformers")
        TotalLoad.Validate("Total load")
        Generators.Validate("Generators")
        Ups.Validate("UPS")
        Loads.Validate("Loads")
        Rooms.Validate()
    End Sub
End Class

Public Structure AlarmCondition
    Public Severity As AlarmSeverity
    Public Title As String
    Public Detail As String
End Structure

Public Module AlarmEvaluator
    Public Function LoadSeverity(percent As Double?, limits As EquipmentAlarmLimits) As AlarmSeverity
        If Not percent.HasValue OrElse Not Double.IsFinite(percent.Value) OrElse limits Is Nothing Then Return AlarmSeverity.Normal
        If percent.Value >= limits.LoadShedPercent Then Return AlarmSeverity.ShedLoad
        If percent.Value >= limits.LoadAlarmPercent Then Return AlarmSeverity.Alarm
        If percent.Value >= limits.LoadPreAlarmPercent Then Return AlarmSeverity.PreAlarm
        Return AlarmSeverity.Normal
    End Function

    Public Function TemperatureSeverity(temperature As Double?, limits As EquipmentAlarmLimits) As AlarmSeverity
        If Not temperature.HasValue OrElse Not Double.IsFinite(temperature.Value) OrElse limits Is Nothing Then Return AlarmSeverity.Normal
        If temperature.Value >= limits.TemperatureShedC Then Return AlarmSeverity.ShedLoad
        If temperature.Value > limits.TemperatureAlarmC Then Return AlarmSeverity.Alarm
        If temperature.Value >= limits.TemperaturePreAlarmC Then Return AlarmSeverity.PreAlarm
        Return AlarmSeverity.Normal
    End Function

    Public Function VoltageSeverity(voltage As Double?, limits As EquipmentAlarmLimits) As AlarmSeverity
        If Not voltage.HasValue OrElse Not Double.IsFinite(voltage.Value) OrElse limits Is Nothing Then Return AlarmSeverity.Normal
        If voltage.Value < limits.UnderVoltageV OrElse voltage.Value > limits.OverVoltageV Then Return AlarmSeverity.Alarm
        Return AlarmSeverity.Normal
    End Function

    ' Returns the most severe active condition; when severities tie, load outranks temperature, which outranks voltage.
    Public Function Evaluate(loadPercent As Double?, temperature As Double?, voltages As IEnumerable(Of Double?),
                             limits As EquipmentAlarmLimits) As AlarmCondition
        Dim result As New AlarmCondition With {.Severity = AlarmSeverity.Normal, .Title = "NORMAL", .Detail = String.Empty}
        Dim active As New List(Of AlarmCondition)()
        Dim inv = CultureInfo.InvariantCulture

        Dim load = LoadSeverity(loadPercent, limits)
        If load <> AlarmSeverity.Normal Then
            active.Add(New AlarmCondition With {
                .Severity = load,
                .Title = If(load = AlarmSeverity.ShedLoad, "SHED LOAD", If(load = AlarmSeverity.Alarm, "OVERLOAD", "HIGH LOAD")),
                .Detail = "LOAD " & loadPercent.Value.ToString("0", inv) & " %"})
        End If
        Dim heat = TemperatureSeverity(temperature, limits)
        If heat <> AlarmSeverity.Normal Then
            active.Add(New AlarmCondition With {
                .Severity = heat,
                .Title = If(heat = AlarmSeverity.ShedLoad, "SHED LOAD", If(heat = AlarmSeverity.Alarm, "OVER TEMP", "HIGH TEMP")),
                .Detail = "TEMP " & temperature.Value.ToString("0", inv) & " °C"})
        End If
        If voltages IsNot Nothing Then
            Dim valid = voltages.Where(Function(v) v.HasValue AndAlso Double.IsFinite(v.Value)).Select(Function(v) v.Value).ToArray()
            If valid.Length > 0 AndAlso limits IsNot Nothing Then
                Dim lowest = valid.Min()
                Dim highest = valid.Max()
                If lowest < limits.UnderVoltageV Then
                    active.Add(New AlarmCondition With {.Severity = AlarmSeverity.Alarm, .Title = "UNDER VOLTAGE",
                        .Detail = lowest.ToString("0", inv) & " V"})
                ElseIf highest > limits.OverVoltageV Then
                    active.Add(New AlarmCondition With {.Severity = AlarmSeverity.Alarm, .Title = "OVER VOLTAGE",
                        .Detail = highest.ToString("0", inv) & " V"})
                End If
            End If
        End If
        If active.Count = 0 Then Return result

        Dim worst = active.OrderByDescending(Function(item) CInt(item.Severity)).First()
        Dim others = active.Count - 1
        If others > 0 Then worst.Detail &= "  (+" & others.ToString(inv) & ")"
        Return worst
    End Function
End Module

Public NotInheritable Class AlarmSettingsStore
    Private Const DocumentKey As String = "alarm-settings"
    Private Shared ReadOnly Gate As New Object()
    Private Shared ReadOnly SerializerOptions As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True,
        .WriteIndented = True
    }
    Private Shared _current As AlarmSettings

    Private Sub New()
    End Sub

    Public Shared Event Changed As EventHandler

    ' Set when the saved document could not be read; defaults are used until a valid document is saved.
    Public Shared ReadOnly Property LoadError As String = String.Empty

    Public Shared ReadOnly Property Current As AlarmSettings
        Get
            SyncLock Gate
                If _current Is Nothing Then _current = LoadOrDefault()
                Return _current
            End SyncLock
        End Get
    End Property

    Public Shared Function SettingsPath(Optional baseDirectory As String = Nothing) As String
        Return Path.Combine(If(baseDirectory, AppContext.BaseDirectory), "EOEMSData", "Configuration", "alarm-settings.json")
    End Function

    Private Shared Function LoadOrDefault() As AlarmSettings
        Try
            Dim json = PostgresDatabase.LoadDocument(DocumentKey, SettingsPath())
            If json Is Nothing Then Return New AlarmSettings()
            Dim settings = JsonSerializer.Deserialize(Of AlarmSettings)(json, SerializerOptions)
            If settings Is Nothing Then Throw New InvalidDataException("Alarm settings document is empty.")
            settings.Validate()
            _LoadError = String.Empty
            Return settings
        Catch ex As Exception When TypeOf ex Is JsonException OrElse TypeOf ex Is InvalidDataException OrElse TypeOf ex Is IOException
            _LoadError = "Saved alarm limits could not be read (" & ex.Message & "); default limits are active."
            Return New AlarmSettings()
        End Try
    End Function

    Public Shared Sub Save(settings As AlarmSettings, username As String, roleName As String, isOfflinePreview As Boolean)
        If settings Is Nothing Then Throw New ArgumentNullException(NameOf(settings))
        If Not PlcConfigurationAccess.CanEdit(roleName, isOfflinePreview) Then
            Throw New UnauthorizedAccessException("Only an authenticated administrator can edit alarm limits.")
        End If
        settings.Validate()
        Dim stored = settings.Clone()
        Dim path = SettingsPath()
        Dim folder = System.IO.Path.GetDirectoryName(path)
        Directory.CreateDirectory(folder)
        Dim serialized = JsonSerializer.Serialize(stored, SerializerOptions)
        Dim temporary = path & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Dim auditRecord = JsonSerializer.Serialize(New With {
            .TimestampUtc = DateTime.UtcNow,
            .Username = If(username, String.Empty),
            .Action = "AlarmLimitsChanged",
            .Before = Current,
            .After = stored
        })
        SyncLock Gate
            Try
                File.WriteAllText(temporary, serialized, New System.Text.UTF8Encoding(False))
                File.Move(temporary, path, True)
                File.AppendAllText(System.IO.Path.Combine(folder, "alarm-settings-audit.jsonl"),
                                   auditRecord & Environment.NewLine, New System.Text.UTF8Encoding(False))
            Finally
                If File.Exists(temporary) Then File.Delete(temporary)
            End Try
            PostgresDatabase.TryWriteDocument(DocumentKey, serialized, username)
            _current = stored
            _LoadError = String.Empty
        End SyncLock
        RaiseEvent Changed(Nothing, EventArgs.Empty)
    End Sub
End Class
