Imports System.Drawing
Imports System.Linq
Imports System.Text.Json
Imports System.Windows.Forms

Partial Public NotInheritable Class FactoryMapView
    Private NotInheritable Class EditSnapshot
        Public Property Assets As List(Of FactoryMapAsset)
        Public Property Building As List(Of FactoryBuildingPart)
        Public Property Signature As String
    End Class

    Private ReadOnly _editHistory As New List(Of EditSnapshot)
    Private _historyIndex As Integer = -1
    Private _restoringHistory As Boolean
    Private _undoButton As Button
    Private _redoButton As Button

    Private Function CaptureEditSnapshot() As EditSnapshot
        Dim snapshot As New EditSnapshot With {
            .Assets = If(_editing, _assets.Select(Function(asset) asset.Clone()).ToList(), Nothing),
            .Building = If(_buildingEditing, _building.Select(Function(part) part.Clone()).ToList(), Nothing)
        }
        Dim metadata = snapshot.Assets?.Select(
            Function(asset)
                Dim copy = asset.Clone()
                ' Verified content hashes identify geometry/images without serializing payloads for every drag.
                If copy.CustomEquipment IsNot Nothing Then
                    copy.CustomEquipment.PngBase64 = ""
                    copy.CustomEquipment.CadGeometry = Nothing
                End If
                Return copy
            End Function).ToList()
        snapshot.Signature = JsonSerializer.Serialize(New With {.Assets = metadata, .Building = snapshot.Building})
        Return snapshot
    End Function

    Private Sub BeginEditHistory()
        _editHistory.Clear()
        _historyIndex = -1
        RecordEditHistory()
    End Sub

    Private Sub EndEditHistory()
        _editHistory.Clear()
        _historyIndex = -1
        UpdateHistoryButtons()
    End Sub

    Private Sub RecordEditHistory()
        If _restoringHistory OrElse (Not _editing AndAlso Not _buildingEditing) Then Return
        Dim snapshot = CaptureEditSnapshot()
        If _historyIndex >= 0 AndAlso snapshot.Signature = _editHistory(_historyIndex).Signature Then Return
        If _historyIndex + 1 < _editHistory.Count Then _editHistory.RemoveRange(_historyIndex + 1, _editHistory.Count - _historyIndex - 1)
        _editHistory.Add(snapshot)
        If _editHistory.Count > 101 Then _editHistory.RemoveAt(0)
        _historyIndex = _editHistory.Count - 1
        If _buildingEditing AndAlso _buildingPicker IsNot Nothing Then RefreshBuildingPicker(_canvas.SelectedBuildingPart)
        UpdateHistoryButtons()
    End Sub

    Private Sub UpdateHistoryButtons()
        If _undoButton IsNot Nothing Then _undoButton.Enabled = (_editing OrElse _buildingEditing) AndAlso _historyIndex > 0
        If _redoButton IsNot Nothing Then _redoButton.Enabled = (_editing OrElse _buildingEditing) AndAlso _historyIndex + 1 < _editHistory.Count
    End Sub

    Private Sub RestoreEditHistory(delta As Integer)
        _canvas.CompleteEditGesture()
        Dim index = _historyIndex + delta
        If index < 0 OrElse index >= _editHistory.Count Then
            _layoutStatus.Text = If(delta < 0, "Nothing to undo.", "Nothing to redo.")
            Return
        End If
        Dim assetId = If(_canvas.SelectedAsset Is Nothing, "", _canvas.SelectedAsset.Id)
        Dim assetIds = _canvas.SelectedAssets.Select(Function(asset) asset.Id).ToHashSet()
        Dim partId = If(_canvas.SelectedBuildingPart Is Nothing, "", _canvas.SelectedBuildingPart.Id)
        _restoringHistory = True
        Try
            _historyIndex = index
            Dim snapshot = _editHistory(index)
            If _editing Then
                _assets.Clear()
                _assets.AddRange(snapshot.Assets.Select(Function(asset) asset.Clone()))
                Dim selected = _assets.FirstOrDefault(Function(asset) asset.Id = assetId)
                RefreshAssetPicker(selected)
                _canvas.SelectAssetGroup(_assets.Where(Function(asset) assetIds.Contains(asset.Id)))
                UpdateSummary()
            ElseIf _buildingEditing Then
                _building.Clear()
                _building.AddRange(snapshot.Building.Select(Function(part) part.Clone()))
                Dim selected = _building.FirstOrDefault(Function(part) part.Id = partId)
                RefreshBuildingPicker(selected)
                _canvas.SelectBuildingPart(selected)
            End If
            _canvas.Invalidate()
            _layoutStatus.Text = If(delta < 0, "Undone.", "Redone.") & " Save to persist; Cancel restores the saved layout."
        Finally
            _restoringHistory = False
        End Try
        UpdateHistoryButtons()
    End Sub

    Private Sub CutSelection()
        If Not CopySelection() Then Return
        If _editing Then
            _assets.Remove(_canvas.SelectedAsset)
            RefreshAssetPicker(Nothing)
            _canvas.SelectAsset(Nothing)
            MapItemsChanged()
        ElseIf _buildingEditing Then
            Dim id = _canvas.SelectedBuildingPart.Id
            _building.RemoveAll(Function(part) part.Id = id OrElse part.ParentWallId = id)
            RefreshBuildingPicker(Nothing)
            _canvas.SelectBuildingPart(Nothing)
            RecordEditHistory()
        End If
        _layoutStatus.Text = "Cut. Ctrl+V pastes; Ctrl+Z restores."
        _canvas.Invalidate()
    End Sub

    Protected Overrides Function ProcessCmdKey(ByRef msg As Message, keyData As Keys) As Boolean
        If _editing OrElse _buildingEditing Then
            ' Leave native text-box cut/undo behavior intact, including numeric property inputs.
            Dim focused As Control = FindForm().ActiveControl
            While TypeOf focused Is ContainerControl AndAlso DirectCast(focused, ContainerControl).ActiveControl IsNot Nothing
                focused = DirectCast(focused, ContainerControl).ActiveControl
            End While
            If Not TypeOf focused Is TextBoxBase AndAlso Not TypeOf focused Is UpDownBase Then
                Dim command = FactoryMapCanvas.EditShortcut(keyData)
                If command <> "" Then
                    HandleEditCommand(command)
                    Return True
                End If
            End If
        End If
        Return MyBase.ProcessCmdKey(msg, keyData)
    End Function

    Private Sub CreateDrawingTools()
        Dim tool As New ComboBox With {.DropDownStyle = ComboBoxStyle.DropDownList, .Width = 122, .AccessibleName = "Drawing tool"}
        tool.Items.AddRange(FactoryMapCanvas.DrawingTools)
        tool.SelectedIndex = 0
        AddHandler tool.SelectedIndexChanged,
            Sub()
                _canvas.SetDrawingTool(CStr(tool.SelectedItem))
                _layoutStatus.Text = "Drag to draw. Wall: repeat segments; Room: four walls + floor. Shift: straight/square. Esc: cancel. Alt: bypass snapping."
                _canvas.Focus()
            End Sub
        _buildingTools.Controls.Add(tool)
        AddToolbarButton(_buildingTools, "Color",
            Sub()
                Using dialog As New ColorDialog With {.Color = _canvas.DrawingColor, .FullOpen = True}
                    If dialog.ShowDialog(Me) = DialogResult.OK Then _canvas.DrawingColor = dialog.Color
                End Using
                _canvas.Focus()
            End Sub)
    End Sub
End Class
