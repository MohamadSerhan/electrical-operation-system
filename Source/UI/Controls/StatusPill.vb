Imports System.Drawing
Imports System.Windows.Forms

Public Class StatusPill
    Inherits PillPanel

    Private ReadOnly _dot As New Panel()
    Private ReadOnly _caption As New Label()
    Private _dotColor As Color = UiTheme.Amber

    Public Sub New()
        Height = 30
        Width = 150
        Padding = New Padding(10, 0, 8, 0)

        _dot.Size = New Size(10, 10)
        _dot.Location = New Point(11, 10)
        _dot.Anchor = AnchorStyles.Left
        AddHandler _dot.Paint, Sub(sender, e)
                                   e.Graphics.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias
                                   e.Graphics.Clear(BackColor)
                                   Using brush As New SolidBrush(_dotColor)
                                       e.Graphics.FillEllipse(brush, 0, 0, _dot.Width - 1, _dot.Height - 1)
                                   End Using
                               End Sub
        Controls.Add(_dot)

        _caption.AutoSize = False
        _caption.Location = New Point(27, 4)
        _caption.Size = New Size(118, 22)
        _caption.Font = New Font("Segoe UI", 8.5F, FontStyle.Bold)
        _caption.ForeColor = UiTheme.Ink
        _caption.TextAlign = ContentAlignment.MiddleLeft
        _caption.BackColor = Color.Transparent
        _caption.UseMnemonic = False
        Controls.Add(_caption)
        SetStatus(New PlcConnectionStatus())
    End Sub

    Private _lastCaption As String
    Private _lastColor As Color

    Public Sub SetStatus(status As PlcConnectionStatus)
        Dim caption = UiTheme.StatusText(status)
        Dim color = UiTheme.StatusColor(status)
        If _lastCaption = caption AndAlso _lastColor = color Then Return
        _lastCaption = caption
        _lastColor = color
        _dotColor = color
        _caption.Text = caption
        If _dotColor = UiTheme.Green Then
            BackColor = Color.FromArgb(232, 246, 237)
            PillBorder = Color.FromArgb(166, 214, 182)
        ElseIf _dotColor = UiTheme.Red Then
            BackColor = Color.FromArgb(251, 236, 236)
            PillBorder = Color.FromArgb(232, 178, 178)
        Else
            BackColor = Color.FromArgb(252, 244, 228)
            PillBorder = Color.FromArgb(232, 204, 150)
        End If
        _dot.Invalidate()
    End Sub
End Class
