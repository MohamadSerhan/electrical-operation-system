Imports System.Globalization
Imports System.IO
Imports System.Text.Json

' Calculates active energy for the local day by integrating recorded kW (index 10) over time.
' Gaps longer than MaxGapSeconds are not bridged. The running value is persisted so a restart keeps today's total.
Public NotInheritable Class EnergyTodayTracker
    Private Const MaxGapSeconds As Double = 5.0R
    Private Shared ReadOnly SaveInterval As TimeSpan = TimeSpan.FromSeconds(30)

    Private NotInheritable Class State
        Public Property Day As String = String.Empty
        Public Property Kwh As Double() = New Double(3) {}
    End Class

    Private ReadOnly _gate As New Object()
    Private ReadOnly _path As String
    Private ReadOnly _lastUtc(3) As DateTime?
    Private _state As New State()
    Private _lastSaveUtc As DateTime = DateTime.MinValue

    Public Sub New(Optional path As String = Nothing)
        _path = If(path, System.IO.Path.Combine(AppContext.BaseDirectory, "data", "energy-today.json"))
        Try
            If File.Exists(_path) Then
                Dim loaded = JsonSerializer.Deserialize(Of State)(File.ReadAllText(_path))
                If loaded IsNot Nothing AndAlso loaded.Kwh IsNot Nothing AndAlso loaded.Kwh.Length = 4 AndAlso
                    String.Equals(loaded.Day, Today(), StringComparison.Ordinal) Then _state = loaded
            End If
        Catch
            _state = New State()
        End Try
        _state.Day = Today()
    End Sub

    Private Shared Function Today() As String
        Return DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    End Function

    Public Sub Update(snapshots As IReadOnlyList(Of MeterSnapshot))
        If snapshots Is Nothing Then Return
        SyncLock _gate
            If Not String.Equals(_state.Day, Today(), StringComparison.Ordinal) Then
                _state = New State() With {.Day = Today()}
            End If
            For index = 0 To Math.Min(3, snapshots.Count - 1)
                Dim snapshot = snapshots(index)
                If snapshot Is Nothing OrElse snapshot.Quality <> MeterQuality.Good OrElse
                    Not snapshot.CapturedAtUtc.HasValue OrElse snapshot.DecodedValues Is Nothing OrElse
                    snapshot.DecodedValues.Length <= 10 Then Continue For
                Dim kw = CDbl(snapshot.DecodedValues(10))
                Dim at = snapshot.CapturedAtUtc.Value
                If Double.IsFinite(kw) AndAlso kw >= 0.0R AndAlso _lastUtc(index).HasValue Then
                    Dim seconds = (at - _lastUtc(index).Value).TotalSeconds
                    If seconds > 0.0R AndAlso seconds <= MaxGapSeconds Then _state.Kwh(index) += kw * seconds / 3600.0R
                End If
                If Not _lastUtc(index).HasValue OrElse at > _lastUtc(index).Value Then _lastUtc(index) = at
            Next
            If (DateTime.UtcNow - _lastSaveUtc) >= SaveInterval Then Save()
        End SyncLock
    End Sub

    Public Function GetKwh(index As Integer) As Double
        SyncLock _gate
            Return _state.Kwh(index)
        End SyncLock
    End Function

    Public Function GetTotalKwh() As Double
        SyncLock _gate
            Return _state.Kwh.Sum()
        End SyncLock
    End Function

    Public Sub Flush()
        SyncLock _gate
            Save()
        End SyncLock
    End Sub

    Private Sub Save()
        _lastSaveUtc = DateTime.UtcNow
        Try
            Dim directory = System.IO.Path.GetDirectoryName(_path)
            If Not String.IsNullOrEmpty(directory) Then System.IO.Directory.CreateDirectory(directory)
            Dim temporary = _path & ".tmp"
            File.WriteAllText(temporary, JsonSerializer.Serialize(_state))
            File.Move(temporary, _path, True)
        Catch
            ' Energy persistence is best effort; live calculation continues.
        End Try
    End Sub
End Class
