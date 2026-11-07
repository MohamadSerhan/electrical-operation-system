Imports System.Drawing
Imports System.Runtime.CompilerServices
Imports System.Windows.Forms

' Shared open/close behaviour for settings cards. Cards start closed unless StartExpanded is set (used by automated UI tests).
Public NotInheritable Class CardCollapse
    Public Const CollapsedHeight As Integer = 46
    Private Shared ReadOnly Registry As New ConditionalWeakTable(Of Control, Action(Of Boolean))

    Public Shared Property StartExpanded As Boolean

    Public Shared Event Toggled(card As Control)

    Private Sub New()
    End Sub

    Public Shared Sub Attach(card As Control, header As Control, ParamArray body As Control())
        Attach(card, header, StartExpanded, body)
    End Sub

    Public Shared Sub Attach(card As Control, header As Control, startOpen As Boolean, ParamArray body As Control())
        Dim expandedHeight = card.Height
        Dim originalDock = card.Dock
        Dim chevron = UiTheme.Label(ChrW(&H25BE), New Font("Segoe UI Symbol", 9.0F, FontStyle.Bold), UiTheme.Blue, False)
        If TypeOf header Is FlowLayoutPanel Then
            chevron.AutoSize = True
            chevron.Margin = New Padding(8, 3, 0, 0)
        Else
            chevron.Dock = DockStyle.Right
            chevron.Width = 30
            chevron.TextAlign = ContentAlignment.MiddleCenter
        End If
        header.Controls.Add(chevron)
        Dim expanded = startOpen
        Dim rowStyle As RowMemo = Nothing
        Dim apply As Action(Of Boolean) = Sub(expand)
            expanded = expand
            card.SuspendLayout()
            For Each item In body
                item.Visible = expand
            Next
            If expand Then
                card.Dock = originalDock
                card.Height = expandedHeight
            Else
                If originalDock = DockStyle.Fill Then card.Dock = DockStyle.Top
                card.Height = CollapsedHeight
            End If
            chevron.Text = ChrW(If(expand, &H25B4, &H25BE))
            card.ResumeLayout(True)
            ApplyRow(card, expand, rowStyle)
            RaiseEvent Toggled(card)
        End Sub
        Dim toggle As EventHandler = Sub() apply(Not expanded)
        header.Cursor = Cursors.Hand
        AddHandler header.Click, toggle
        For Each child As Control In header.Controls
            child.Cursor = Cursors.Hand
            AddHandler child.Click, toggle
        Next
        AddHandler card.ParentChanged, Sub() If Not expanded Then ApplyRow(card, False, rowStyle)
        ' The table cell is assigned after the card is parented, so apply again once the card is fully placed.
        AddHandler card.HandleCreated, Sub() If Not expanded Then ApplyRow(card, False, rowStyle)
        Registry.Remove(card)
        Registry.Add(card, apply)
        apply(startOpen)
    End Sub

    Private NotInheritable Class RowMemo
        Public Table As TableLayoutPanel
        Public Row As Integer
        Public Style As RowStyle
    End Class

    ' A card that alone occupies a fixed or percentage table row gives the row back when closed so the cards below move up.
    Private Shared Sub ApplyRow(card As Control, expand As Boolean, ByRef saved As RowMemo)
        Dim table = TryCast(card.Parent, TableLayoutPanel)
        If table Is Nothing Then Return
        Dim row = table.GetRow(card)
        If row < 0 OrElse row >= table.RowStyles.Count Then Return
        ' Undo an earlier change made before the card reached its final cell.
        If saved IsNot Nothing AndAlso (Not ReferenceEquals(saved.Table, table) OrElse saved.Row <> row OrElse expand) Then
            If saved.Row < saved.Table.RowStyles.Count Then
                saved.Table.RowStyles(saved.Row).SizeType = saved.Style.SizeType
                saved.Table.RowStyles(saved.Row).Height = saved.Style.Height
            End If
            saved = Nothing
        End If
        If expand Then Return
        If table.Controls.Cast(Of Control)().Count(Function(item) table.GetRow(item) = row) <> 1 Then Return
        Dim current = table.RowStyles(row)
        If current.SizeType = SizeType.AutoSize OrElse saved IsNot Nothing Then Return
        saved = New RowMemo With {.Table = table, .Row = row, .Style = New RowStyle(current.SizeType, current.Height)}
        current.SizeType = SizeType.Absolute
        current.Height = CollapsedHeight + card.Margin.Vertical
    End Sub
    Public Shared Sub Register(card As Control, setExpanded As Action(Of Boolean))
        Registry.Remove(card)
        Registry.Add(card, setExpanded)
    End Sub

    Public Shared Sub SetAll(root As Control, expanded As Boolean)
        Dim action As Action(Of Boolean) = Nothing
        If Registry.TryGetValue(root, action) Then action(expanded)
        For Each child As Control In root.Controls
            SetAll(child, expanded)
        Next
    End Sub
End Class
