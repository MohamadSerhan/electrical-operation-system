Imports System.Drawing
Imports System.Windows.Forms

Public Module UiTheme
    Public ReadOnly Canvas As Color = Color.FromArgb(239, 245, 248)
    Public ReadOnly Shell As Color = Color.FromArgb(32, 59, 80)
    Public ReadOnly ShellDeep As Color = Color.FromArgb(23, 45, 64)
    Public ReadOnly Surface As Color = Color.White
    Public ReadOnly SurfaceMuted As Color = Color.FromArgb(246, 249, 252)
    Public ReadOnly Ink As Color = Color.FromArgb(24, 43, 61)
    Public ReadOnly MutedInk As Color = Color.FromArgb(83, 104, 123)
    Public ReadOnly Blue As Color = Color.FromArgb(24, 101, 204)
    Public ReadOnly BlueDark As Color = Color.FromArgb(17, 77, 163)
    Public ReadOnly Cyan As Color = Color.FromArgb(13, 148, 160)
    Public ReadOnly Green As Color = Color.FromArgb(24, 132, 91)
    Public ReadOnly Amber As Color = Color.FromArgb(183, 119, 31)
    Public ReadOnly Red As Color = Color.FromArgb(185, 64, 64)
    Public ReadOnly Border As Color = Color.FromArgb(213, 225, 234)
    Public ReadOnly ShellPageTitleFont As New Font("Segoe UI", 11.5F, FontStyle.Bold)
    Public Const WorkspaceNavigationHeight As Integer = 44

    Public Function Label(text As String, font As Font, foreColor As Color, Optional autoSize As Boolean = True) As Label
        Return New Label With {
            .Text = text,
            .Font = font,
            .ForeColor = foreColor,
            .AutoSize = autoSize,
            .BackColor = Color.Transparent,
            .UseMnemonic = False,
            .Margin = Padding.Empty
        }
    End Function

    Public Function Button(text As String, Optional emphasized As Boolean = True) As Button
        Dim result = New Button With {
            .Text = text,
            .FlatStyle = FlatStyle.Flat,
            .Cursor = Cursors.Hand,
            .Font = New Font("Segoe UI", 9.5F, FontStyle.Bold),
            .UseVisualStyleBackColor = False,
            .BackColor = If(emphasized, Blue, SurfaceMuted),
            .ForeColor = If(emphasized, Color.White, Ink),
            .Height = 40,
            .AutoSize = False
        }
        result.FlatAppearance.BorderSize = 0
        Return result
    End Function

    ' Shared action button used by the Configuration, SNMP and Historian pages (rounded, bordered, optional icon).
    Public Function ActionButton(text As String, Optional primary As Boolean = False, Optional glyph As String = Nothing) As IconButton
        Return New IconButton With {.Text = text, .Primary = primary, .Glyph = glyph, .Height = 36}
    End Function

    ' Shared read-only table look: muted header that never turns selection-blue, light row selection, hairline rows.
    Public Sub StyleReadOnlyGrid(grid As DataGridView)
        grid.BackgroundColor = Surface
        grid.BorderStyle = BorderStyle.None
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal
        grid.GridColor = Border
        grid.EnableHeadersVisualStyles = False
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single
        grid.ColumnHeadersDefaultCellStyle.BackColor = SurfaceMuted
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Ink
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = SurfaceMuted
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Ink
        grid.DefaultCellStyle.BackColor = Surface
        grid.DefaultCellStyle.ForeColor = Ink
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(214, 230, 250)
        grid.DefaultCellStyle.SelectionForeColor = Ink
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 253)
    End Sub

    Public Function StatusColor(health As PlcHealth) As Color
        Select Case health
            Case PlcHealth.Connected
                Return Green
            Case PlcHealth.PartialCommunication
                Return Amber
            Case PlcHealth.Connecting
                Return Blue
            Case PlcHealth.Unreachable, PlcHealth.ErrorState, PlcHealth.BadQuality, PlcHealth.MappingError, PlcHealth.Timeout
                Return Red
            Case Else
                Return Amber
        End Select
    End Function

    Public Function StatusText(status As PlcConnectionStatus) As String
        If Not String.IsNullOrEmpty(status.PowerLogError) Then Return "Power log error"
        If status.PowerSummary = "Power unknown" AndAlso status.Health <> PlcHealth.Connected Then Return StatusText(status.Health)
        Return If(String.IsNullOrEmpty(status.PowerSummary), StatusText(status.Health), status.PowerSummary)
    End Function

    Public Function StatusColor(status As PlcConnectionStatus) As Color
        If Not String.IsNullOrEmpty(status.PowerLogError) Then Return Red
        If status.PowerSummary = "ECG off" OrElse status.PowerSummary = "Transformer off" Then Return Red
        If status.Health <> PlcHealth.Connected Then Return StatusColor(status.Health)
        If status.PowerSummary = "Power unknown" Then Return Amber
        Return StatusColor(status.Health)
    End Function

    Public Function StatusText(health As PlcHealth) As String
        Select Case health
            Case PlcHealth.Connected
                Return "PLC online (FC03)"
            Case PlcHealth.PartialCommunication
                Return "PLC partial (FC03)"
            Case PlcHealth.Connecting
                Return "PLC connecting"
            Case PlcHealth.Unreachable
                Return "PLC offline"
            Case PlcHealth.ErrorState
                Return "PLC error"
            Case PlcHealth.BadQuality
                Return "Bad quality"
            Case PlcHealth.MappingError
                Return "Mapping error"
            Case PlcHealth.Timeout
                Return "PLC timeout"
            Case Else
                Return "PLC not checked"
        End Select
    End Function
End Module
