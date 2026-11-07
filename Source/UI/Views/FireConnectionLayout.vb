Imports System.Linq

Friend NotInheritable Class FireConnectionLayout
    Friend Shared Sub FillViewport(viewport As Panel, content As TableLayoutPanel)
        content.AutoSizeMode = AutoSizeMode.GrowAndShrink
        Dim sizing = False
        Dim fit As Action = Sub()
                                If sizing OrElse content.IsDisposed Then Return
                                sizing = True
                                Try
                                    content.MinimumSize = New Size(0, viewport.ClientSize.Height)
                                    content.Height = Math.Max(viewport.ClientSize.Height,
                                        content.GetPreferredSize(New Size(viewport.ClientSize.Width, 0)).Height)
                                Finally
                                    sizing = False
                                End Try
                            End Sub
        AddHandler viewport.Layout, Sub() fit()
        fit()
    End Sub

    Friend Shared Sub FitFields(table As TableLayoutPanel)
        Dim widths = table.GetColumnWidths()
        table.SuspendLayout()
        For row = 0 To table.RowCount - 1
            Dim height = 0
            For Each item As Control In table.Controls
                If table.GetRow(item) <> row OrElse Not item.Visible Then Continue For
                Dim column = table.GetColumn(item)
                Dim width = Math.Max(1, widths.Skip(column).Take(table.GetColumnSpan(item)).Sum() - item.Margin.Horizontal)
                Dim caption = TryCast(item, Label)
                Dim preferred As Integer
                If caption IsNot Nothing Then
                    preferred = TextRenderer.MeasureText(caption.Text, caption.Font,
                        New Size(Math.Max(1, width - caption.Padding.Horizontal), Integer.MaxValue),
                        TextFormatFlags.WordBreak Or TextFormatFlags.NoPrefix Or TextFormatFlags.TextBoxControl).Height + caption.Padding.Vertical
                    caption.AutoSize = False
                    caption.Dock = DockStyle.Fill
                    caption.MaximumSize = New Size(width, 0)
                Else
                    preferred = item.GetPreferredSize(New Size(width, 0)).Height
                End If
                height = Math.Max(height, preferred + item.Margin.Vertical)
            Next
            table.RowStyles(row).SizeType = SizeType.Absolute
            table.RowStyles(row).Height = height
        Next
        table.ResumeLayout(True)
    End Sub

    Friend Shared Function Status(state As Label, summary As Label) As TableLayoutPanel
        Dim panel = Card("SAVED CONNECTION STATUS", state, summary)
        panel.AutoSize = False
        panel.Height = 90
        panel.RowStyles.Clear()
        panel.RowStyles.Add(New RowStyle(SizeType.Absolute, 18))
        panel.RowStyles.Add(New RowStyle(SizeType.Absolute, 20))
        panel.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        summary.Font = New Font("Segoe UI", 8.25F)
        For Each statusLabel As Label In {state, summary}
            statusLabel.AutoSize = False
            statusLabel.AutoEllipsis = True
        Next
        Dim details As New ToolTip With {.AutoPopDelay = 30000}
        AddHandler summary.TextChanged, Sub() details.SetToolTip(summary, summary.Text)
        AddHandler state.TextChanged, Sub() details.SetToolTip(state, state.Text)
        AddHandler panel.Disposed, Sub() details.Dispose()
        Return panel
    End Function

    Friend Shared Function Card(title As String, ParamArray contents As Control()) As TableLayoutPanel
        Dim panel As New TableLayoutPanel With {
            .Dock = DockStyle.Fill, .AutoSize = True, .AutoSizeMode = AutoSizeMode.GrowAndShrink, .ColumnCount = 1,
            .BackColor = UiTheme.Surface, .Padding = New Padding(10, 6, 10, 6),
            .Margin = New Padding(3)}
        panel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        If title.Length > 0 Then
            AddRow(panel, New Label With {
                .Text = title, .AutoSize = True, .ForeColor = UiTheme.Blue,
                .Font = New Font("Segoe UI", 9, FontStyle.Bold), .Margin = New Padding(0, 0, 0, 6)})
        End If
        For Each content In contents
            AddRow(panel, content)
        Next
        Return panel
    End Function

    Private Shared Sub AddRow(table As TableLayoutPanel, content As Control)
        Dim row = table.RowCount
        table.RowCount += 1
        table.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        content.Dock = DockStyle.Fill
        content.Margin = New Padding(0)
        table.Controls.Add(content, 0, row)
    End Sub

    Friend Shared Function Footer(buttons As FlowLayoutPanel, message As Label) As Control
        Dim primary = {"Test Connection", "Save Settings", "Connect", "Disconnect", "Reconnect", "Clear Diagnostic Logs", "Copy Diagnostics"}
        Dim ordered = buttons.Controls.OfType(Of Button)().ToArray()
        buttons.SuspendLayout()
        For index = 0 To primary.Length - 1
            Dim text = primary(index)
            Dim button = ordered.Single(Function(item) item.Text = text)
            buttons.Controls.SetChildIndex(button, index)
        Next
        buttons.Dock = DockStyle.Fill
        buttons.AutoSize = False
        buttons.WrapContents = True
        buttons.Padding = Padding.Empty
        buttons.Margin = Padding.Empty
        buttons.SetFlowBreak(ordered.Single(Function(item) item.Text = "Copy Diagnostics"), True)
        For Each button As Button In ordered
            button.AutoSize = False
            button.Height = 32
            button.Margin = New Padding(0, 2, 6, 2)
            button.Font = New Font("Segoe UI", 9)
        Next
        AddHandler buttons.SizeChanged, Sub()
                                            Dim width = Math.Max(1, buttons.ClientSize.Width \ 7 - 6)
                                            For Each button As Button In ordered
                                                button.Width = width
                                            Next
                                        End Sub
        buttons.ResumeLayout(True)
        Dim panel As New TableLayoutPanel With {
            .Name = "connection-actions", .Dock = DockStyle.Bottom, .Height = 116,
            .ColumnCount = 1, .RowCount = 3, .BackColor = UiTheme.Surface,
            .Padding = New Padding(10, 4, 10, 4), .Margin = Padding.Empty}
        panel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100))
        panel.RowStyles.Add(New RowStyle(SizeType.Absolute, 72))
        panel.RowStyles.Add(New RowStyle(SizeType.Absolute, 18))
        panel.RowStyles.Add(New RowStyle(SizeType.Percent, 100))
        panel.Controls.Add(buttons, 0, 0)
        message.Dock = DockStyle.Fill
        message.AutoSize = False
        message.AutoEllipsis = True
        message.Margin = New Padding(0)
        message.TextAlign = ContentAlignment.MiddleLeft
        panel.Controls.Add(message, 0, 1)
        panel.Controls.Add(New Label With {
            .Dock = DockStyle.Fill, .AutoEllipsis = True, .ForeColor = UiTheme.MutedInk,
            .Margin = Padding.Empty,
            .Text = "Tests use the draft; Connect uses saved settings. Live fire status: UNKNOWN. Historical events / raw responses do not prove NORMAL."}, 0, 2)
        Dim details As New ToolTip With {.AutoPopDelay = 30000}
        AddHandler message.TextChanged, Sub() details.SetToolTip(message, message.Text)
        AddHandler panel.Disposed, Sub() details.Dispose()
        Return panel
    End Function
End Class
