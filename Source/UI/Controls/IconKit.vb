Imports System.ComponentModel
Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Drawing.Text
Imports System.Windows.Forms

' Vector icons come from the Windows icon font so they stay crisp at every DPI.
Public NotInheritable Class Icons
    Public Const Home As String = ChrW(&HE80F)
    Public Const Grid As String = ChrW(&HE80A)
    Public Const Chart As String = ChrW(&HE9D2)
    Public Const Monitor As String = ChrW(&HE7F4)
    Public Const Settings As String = ChrW(&HE713)
    Public Const Database As String = ChrW(&HE968)
    Public Const SaveLocal As String = ChrW(&HE78C)
    Public Const Refresh As String = ChrW(&HE72C)
    Public Const User As String = ChrW(&HE77B)
    Public Const ChevronDown As String = ChrW(&HE70D)
    Public Const Folder As String = ChrW(&HE8B7)
    Public Const Search As String = ChrW(&HE721)
    Public Const List As String = ChrW(&HE14C)
    Public Const Clock As String = ChrW(&HE121)
    Public Const Warning As String = ChrW(&HE7BA)
    Public Const ErrorBadge As String = ChrW(&HEA39)
    Public Const Info As String = ChrW(&HE946)
    Public Const Calendar As String = ChrW(&HE787)
    Public Const Save As String = ChrW(&HE74E)
    Public Const Document As String = ChrW(&HE8A5)
    Public Const CheckCircle As String = ChrW(&HEC61)
    Public Const Cancel As String = ChrW(&HE711)
    Public Const Equipment As String = ChrW(&HE9D9)
    Public Const Battery As String = ChrW(&HE83F)
    Public Const Ethernet As String = ChrW(&HE839)

    Public Shared ReadOnly AccentBlue As Color = UiTheme.Blue
    Public Shared ReadOnly AccentBlueDark As Color = UiTheme.BlueDark
    Public Shared ReadOnly SidebarNavy As Color = Color.FromArgb(14, 42, 77)
    Public Shared ReadOnly SidebarHover As Color = Color.FromArgb(26, 57, 98)
    Public Shared ReadOnly SidebarText As Color = Color.FromArgb(222, 230, 240)
    Public Shared ReadOnly SidebarMuted As Color = Color.FromArgb(150, 168, 190)
    Public Shared ReadOnly HairLine As Color = UiTheme.Border
    Public Shared ReadOnly PageCanvas As Color = UiTheme.Canvas

    Private Shared _family As String

    Public Shared Function FamilyName() As String
        If _family IsNot Nothing Then Return _family
        Using installed As New InstalledFontCollection()
            For Each candidate In {"Segoe Fluent Icons", "Segoe MDL2 Assets"}
                If installed.Families.Any(Function(f) String.Equals(f.Name, candidate, StringComparison.OrdinalIgnoreCase)) Then
                    _family = candidate
                    Return _family
                End If
            Next
        End Using
        _family = "Segoe UI Symbol"
        Return _family
    End Function

    Public Shared Function IconFont(size As Single) As Font
        Return New Font(FamilyName(), size, FontStyle.Regular, GraphicsUnit.Point)
    End Function

    Public Shared Function IconLabel(glyph As String, size As Single, color As Color) As Label
        Return New Label With {
            .Text = glyph,
            .Font = IconFont(size),
            .ForeColor = color,
            .BackColor = Color.Transparent,
            .AutoSize = False,
            .Size = New Size(CInt(size * 2.2F), CInt(size * 2.2F)),
            .TextAlign = ContentAlignment.MiddleCenter,
            .UseMnemonic = False,
            .Margin = Padding.Empty
        }
    End Function

    ' Section heading: blue icon + uppercase bold caption.
    Public Shared Function SectionHeading(glyph As String, text As String, Optional note As String = Nothing) As Control
        Dim row As New FlowLayoutPanel With {
            .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .FlowDirection = FlowDirection.LeftToRight,
            .WrapContents = False,
            .BackColor = Color.Transparent,
            .Margin = New Padding(0, 0, 0, 8)
        }
        Dim icon = IconLabel(glyph, 13.5F, AccentBlue)
        icon.Margin = New Padding(0, 0, 6, 0)
        row.Controls.Add(icon)
        Dim caption = UiTheme.Label(text.ToUpperInvariant(), New Font("Segoe UI", 9.5F, FontStyle.Bold), Color.FromArgb(24, 52, 94))
        caption.Margin = New Padding(0, 3, 0, 0)
        row.Controls.Add(caption)
        If Not String.IsNullOrEmpty(note) Then
            Dim extra = UiTheme.Label("•  " & note.ToUpperInvariant(), New Font("Segoe UI", 8.0F, FontStyle.Bold), AccentBlue)
            extra.Margin = New Padding(6, 5, 0, 0)
            row.Controls.Add(extra)
        End If
        Return row
    End Function
End Class

' Rounded flat button with an optional vector icon, painted manually so icon and text stay centred.
Public Class IconButton
    Inherits Button

    Private _glyph As String = String.Empty
    Private _glyphColor As Color = Color.Empty
    Private _hover As Boolean
    Private _pressed As Boolean
    Private _fill As Color = Color.White
    Private _border As Color = Icons.HairLine
    Private _text As Color = UiTheme.Ink
    Private _primary As Boolean

    Public Sub New()
        SetStyle(ControlStyles.UserPaint Or ControlStyles.OptimizedDoubleBuffer Or ControlStyles.ResizeRedraw, True)
        FlatStyle = FlatStyle.Flat
        Cursor = Cursors.Hand
        Height = 36
        Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        UseVisualStyleBackColor = False
        UseMnemonic = False
        FlatAppearance.BorderSize = 0
    End Sub

    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property Glyph As String
        Get
            Return _glyph
        End Get
        Set(value As String)
            _glyph = If(value, String.Empty)
            Invalidate()
        End Set
    End Property

    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property GlyphColor As Color
        Get
            Return _glyphColor
        End Get
        Set(value As Color)
            _glyphColor = value
            Invalidate()
        End Set
    End Property

    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property Primary As Boolean
        Get
            Return _primary
        End Get
        Set(value As Boolean)
            _primary = value
            Invalidate()
        End Set
    End Property

    Protected Overrides Sub OnMouseEnter(e As EventArgs)
        _hover = True
        Invalidate()
        MyBase.OnMouseEnter(e)
    End Sub

    Protected Overrides Sub OnMouseLeave(e As EventArgs)
        _hover = False
        _pressed = False
        Invalidate()
        MyBase.OnMouseLeave(e)
    End Sub

    Protected Overrides Sub OnMouseDown(mevent As MouseEventArgs)
        _pressed = True
        Invalidate()
        MyBase.OnMouseDown(mevent)
    End Sub

    Protected Overrides Sub OnMouseUp(mevent As MouseEventArgs)
        _pressed = False
        Invalidate()
        MyBase.OnMouseUp(mevent)
    End Sub

    Protected Overrides Sub OnEnabledChanged(e As EventArgs)
        Invalidate()
        MyBase.OnEnabledChanged(e)
    End Sub

    Protected Overrides Sub OnPaint(pevent As PaintEventArgs)
        Dim g = pevent.Graphics
        g.SmoothingMode = SmoothingMode.AntiAlias
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit
        Dim parentColor = If(Parent IsNot Nothing, Parent.BackColor, Icons.PageCanvas)
        If parentColor.A < 255 Then parentColor = Icons.PageCanvas
        g.Clear(parentColor)

        Dim fill As Color, border As Color, ink As Color
        If _primary Then
            fill = If(_pressed, Icons.AccentBlueDark, If(_hover, Color.FromArgb(31, 112, 224), Icons.AccentBlue))
            border = Icons.AccentBlueDark
            ink = Color.White
        Else
            fill = If(_pressed, Color.FromArgb(222, 232, 245), If(_hover, Color.FromArgb(238, 244, 252), Color.White))
            border = If(_hover, Icons.AccentBlue, Color.FromArgb(205, 214, 225))
            ink = UiTheme.Ink
        End If
        If Not Enabled Then
            fill = Color.FromArgb(243, 245, 248)
            border = Color.FromArgb(226, 231, 237)
            ink = Color.FromArgb(160, 170, 181)
        End If

        Dim rect = New Rectangle(0, 0, Width - 1, Height - 1)
        Using path = RoundPath(rect, 5)
            Using brush As New SolidBrush(fill)
                g.FillPath(brush, path)
            End Using
            Using pen As New Pen(border)
                g.DrawPath(pen, path)
            End Using
        End Using

        Dim glyphSize = Font.SizeInPoints + 3.0F
        Using iconFont = Icons.IconFont(glyphSize)
            Dim iconColor = If(Not Enabled, ink, If(_glyphColor.IsEmpty, If(_primary, Color.White, Icons.AccentBlue), _glyphColor))
            Dim flags = TextFormatFlags.NoPadding Or TextFormatFlags.NoPrefix Or TextFormatFlags.SingleLine Or TextFormatFlags.VerticalCenter
            Dim textSize = If(String.IsNullOrEmpty(Text), Size.Empty, TextRenderer.MeasureText(g, Text, Font, New Size(Integer.MaxValue, Height), flags))
            Dim glyphWidth = If(_glyph.Length = 0, 0, CInt(TextRenderer.MeasureText(g, _glyph, iconFont, New Size(Integer.MaxValue, Height), flags).Width))
            Dim gap = If(glyphWidth > 0 AndAlso textSize.Width > 0, 8, 0)
            Dim total = glyphWidth + gap + textSize.Width
            Dim x = Math.Max(4, (Width - total) \ 2)
            If glyphWidth > 0 Then
                TextRenderer.DrawText(g, _glyph, iconFont, New Rectangle(x, 0, glyphWidth, Height), iconColor, flags)
            End If
            If textSize.Width > 0 Then
                TextRenderer.DrawText(g, Text, Font, New Rectangle(x + glyphWidth + gap, 0, Math.Max(1, Width - x - glyphWidth - gap - 2), Height), ink, flags Or TextFormatFlags.EndEllipsis)
            End If
        End Using
    End Sub

    Private Shared Function RoundPath(rect As Rectangle, radius As Integer) As GraphicsPath
        Dim path As New GraphicsPath()
        Dim d = radius * 2
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90)
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90)
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90)
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90)
        path.CloseFigure()
        Return path
    End Function
End Class

' Shield emblem used in the application header.
Public Class BrandLogo
    Inherits Control

    Public Sub New()
        SetStyle(ControlStyles.UserPaint Or ControlStyles.OptimizedDoubleBuffer Or ControlStyles.SupportsTransparentBackColor Or ControlStyles.ResizeRedraw, True)
        BackColor = Color.Transparent
        Size = New Size(38, 42)
    End Sub

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        Dim g = e.Graphics
        g.SmoothingMode = SmoothingMode.AntiAlias
        Dim w = Width - 2.0F
        Dim h = Height - 2.0F
        Using shield As New GraphicsPath()
            shield.AddLine(1.0F, 4.0F, w * 0.5F, 1.0F)
            shield.AddLine(w * 0.5F, 1.0F, w, 4.0F)
            shield.AddLine(w, 4.0F, w, h * 0.55F)
            shield.AddBezier(w, h * 0.55F, w, h * 0.82F, w * 0.6F, h * 0.95F, w * 0.5F, h)
            shield.AddBezier(w * 0.5F, h, w * 0.4F, h * 0.95F, 1.0F, h * 0.82F, 1.0F, h * 0.55F)
            shield.CloseFigure()
            Using outer As New LinearGradientBrush(New PointF(0, 0), New PointF(0, h), Color.FromArgb(20, 55, 105), Color.FromArgb(12, 36, 72))
                g.FillPath(outer, shield)
            End Using
            Using rim As New Pen(Color.FromArgb(196, 160, 60), 1.6F)
                g.DrawPath(rim, shield)
            End Using
        End Using
        Using bolt As New GraphicsPath()
            Dim cx = w * 0.5F + 1.0F
            bolt.AddPolygon({
                New PointF(cx + 3.0F, h * 0.2F), New PointF(cx - 6.0F, h * 0.55F), New PointF(cx - 0.5F, h * 0.55F),
                New PointF(cx - 3.0F, h * 0.82F), New PointF(cx + 6.5F, h * 0.45F), New PointF(cx + 0.8F, h * 0.45F)})
            Using brush As New SolidBrush(Color.FromArgb(244, 196, 48))
                g.FillPath(brush, bolt)
            End Using
        End Using
    End Sub
End Class

' Rounded pill used for the PLC status and similar chips.
Public Class PillPanel
    Inherits Panel

    Private _border As Color = Color.FromArgb(166, 214, 182)

    Public Sub New()
        SetStyle(ControlStyles.UserPaint Or ControlStyles.OptimizedDoubleBuffer Or ControlStyles.ResizeRedraw, True)
        BackColor = Color.FromArgb(232, 246, 237)
    End Sub

    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property PillBorder As Color
        Get
            Return _border
        End Get
        Set(value As Color)
            _border = value
            Invalidate()
        End Set
    End Property

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        Dim g = e.Graphics
        g.SmoothingMode = SmoothingMode.AntiAlias
        Dim parentColor = If(Parent IsNot Nothing AndAlso Parent.BackColor.A = 255, Parent.BackColor, Color.White)
        g.Clear(parentColor)
        Dim rect = New Rectangle(0, 0, Width - 1, Height - 1)
        Using path As New GraphicsPath()
            Dim d = Math.Min(Height - 1, 12)
            path.AddArc(rect.Left, rect.Top, d, d, 180, 90)
            path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90)
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90)
            path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90)
            path.CloseFigure()
            Using brush As New SolidBrush(BackColor)
                g.FillPath(brush, path)
            End Using
            Using pen As New Pen(_border)
                g.DrawPath(pen, path)
            End Using
        End Using
    End Sub
End Class

' Sidebar navigation item: full-width, icon + label, solid accent fill when selected.
Public Class NavButton
    Inherits Button

    Private _glyph As String = String.Empty
    Private _selected As Boolean
    Private _hover As Boolean

    Public Sub New()
        SetStyle(ControlStyles.UserPaint Or ControlStyles.OptimizedDoubleBuffer Or ControlStyles.ResizeRedraw, True)
        FlatStyle = FlatStyle.Flat
        Cursor = Cursors.Hand
        TabStop = False
        UseMnemonic = False
        UseVisualStyleBackColor = False
        FlatAppearance.BorderSize = 0
        Font = New Font("Segoe UI", 9.5F, FontStyle.Bold)
    End Sub

    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property Glyph As String
        Get
            Return _glyph
        End Get
        Set(value As String)
            _glyph = If(value, String.Empty)
            Invalidate()
        End Set
    End Property

    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property Selected As Boolean
        Get
            Return _selected
        End Get
        Set(value As Boolean)
            _selected = value
            Invalidate()
        End Set
    End Property

    Protected Overrides Sub OnMouseEnter(e As EventArgs)
        _hover = True
        Invalidate()
        MyBase.OnMouseEnter(e)
    End Sub

    Protected Overrides Sub OnMouseLeave(e As EventArgs)
        _hover = False
        Invalidate()
        MyBase.OnMouseLeave(e)
    End Sub

    Protected Overrides Sub OnPaint(pevent As PaintEventArgs)
        Dim g = pevent.Graphics
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit
        Dim fill = If(_selected, Icons.AccentBlue, If(_hover, Icons.SidebarHover, Icons.SidebarNavy))
        Using brush As New SolidBrush(fill)
            g.FillRectangle(brush, 0, 0, Width, Height)
        End Using
        Dim ink = If(_selected, Color.White, Icons.SidebarText)
        Dim flags = TextFormatFlags.NoPadding Or TextFormatFlags.NoPrefix Or TextFormatFlags.SingleLine Or TextFormatFlags.VerticalCenter
        Using iconFont = Icons.IconFont(13.0F)
            TextRenderer.DrawText(g, _glyph, iconFont, New Rectangle(20, 0, 26, Height), ink, flags Or TextFormatFlags.HorizontalCenter)
        End Using
        TextRenderer.DrawText(g, Text, Font, New Rectangle(56, 0, Math.Max(1, Width - 60), Height), ink, flags Or TextFormatFlags.EndEllipsis)
    End Sub
End Class
