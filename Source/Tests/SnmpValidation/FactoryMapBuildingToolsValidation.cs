using System.Collections;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class FactoryMapBuildingToolsValidation
{
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetKeyboardState(byte[] state);
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetKeyboardState(byte[] state);

    private static void ControlDrag(Control canvas, Point first, Point last)
    {
        var original = new byte[256];
        if (!GetKeyboardState(original)) throw new System.ComponentModel.Win32Exception();
        var pressed = (byte[])original.Clone();
        pressed[(int)Keys.ControlKey] = 128;
        pressed[(int)Keys.LControlKey] = 128;
        try
        {
            if (!SetKeyboardState(pressed)) throw new System.ComponentModel.Win32Exception();
            Require((Control.ModifierKeys & Keys.Control) != 0, "The region gesture must actually receive the Ctrl modifier.");
            Drag(canvas, first, last);
        }
        finally
        {
            if (!SetKeyboardState(original)) throw new System.ComponentModel.Win32Exception();
        }
    }
    private static Type Type(string name) => typeof(FactoryMapView).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5." + name)!;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(target, args);
    private static object? Static(string type, string name, params object?[] args) =>
        Type(type).GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, args);
    private static object Get(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static IList Parts(params object[] parts)
    {
        var result = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("FactoryBuildingPart")))!;
        foreach (var part in parts) result.Add(part);
        return result;
    }
    private static object Part(string kind = "Wall", float rotation = 0)
    {
        var part = Activator.CreateInstance(Type("FactoryBuildingPart"))!;
        Set(part, "Kind", kind);
        Set(part, "Rotation", rotation);
        if (kind != "Wall")
        {
            Set(part, "Style", kind == "Window" ? "Casement" : kind == "Gate" ? "Double swing" : "Single leaf");
            Set(part, "Material", kind == "Window" ? "Glass" : "Steel");
            Set(part, "Length", 18F);
            Set(part, "HeightMeters", kind == "Window" ? 1.2F : 2.1F);
            Set(part, "BaseMeters", kind == "Window" ? 1F : 0F);
        }
        return part;
    }
    private static Point Screen(Control canvas, float x, float y)
    {
        var origin = (PointF)Call(canvas, "ScreenToMap", Point.Empty)!;
        var unit = (PointF)Call(canvas, "ScreenToMap", new Point(100, 100))!;
        return new Point((int)Math.Round((x / 1365 - origin.X) * 100 / (unit.X - origin.X)),
                         (int)Math.Round((y / 766 - origin.Y) * 100 / (unit.Y - origin.Y)));
    }
    private static void Paint(Control canvas)
    {
        using var bitmap = new Bitmap(canvas.Width, canvas.Height);
        canvas.DrawToBitmap(bitmap, canvas.ClientRectangle);
    }
    private static void Mouse(Control canvas, string method, Point point, MouseButtons buttons = MouseButtons.Left) =>
        Call(canvas, method, new MouseEventArgs(buttons, 1, point.X, point.Y, 0));
    private static void Drag(Control canvas, Point first, Point last)
    {
        Mouse(canvas, "OnMouseDown", first);
        Mouse(canvas, "OnMouseMove", last);
        Mouse(canvas, "OnMouseUp", last);
    }
    private static void Key(Control canvas, Keys key) => Call(canvas, "OnKeyDown", new KeyEventArgs(key));
    private static string Describe(object target) => JsonSerializer.Serialize(target, target.GetType());

    public static void Run()
    {
        ValidateOpeningSnap();
        ValidateDrawingHistory();
        ValidateEquipmentTransforms();
        ValidateEquipmentRegionSelection();
        FactoryMapMirrorValidation.Run();
        Console.WriteLine("Building tools: rotated opening snaps, overlap/level guards, drag attachment, room/shape/pen drawing, persistence, gesture undo/redo/cut and equipment transforms passed.");
    }

    private static void ValidateOpeningSnap()
    {
        foreach (var rotation in new[] { 0F, 45F, 90F, 135F })
        foreach (var kind in new[] { "Door", "Window", "Gate" })
        {
            var wall = Part(rotation: rotation);
            Set(wall, "Length", 120F);
            var opening = Part(kind);
            var point = (PointF)Static("FactoryMapBuildingStore", "WorldPoint", wall, 20F, 3F)!;
            Set(opening, "Position", new PointF(point.X / 1365, point.Y / 766));
            Require((bool)Static("FactoryMapPlacement", "AttachOpening", opening, Parts(wall), 7F)!,
                $"{kind} must attach to rotated wall {rotation}.");
            Static("FactoryMapBuildingStore", "Validate", Parts(wall, opening));
            Require((string)Get(opening, "ParentWallId") == (string)Get(wall, "Id") &&
                    Math.Abs((float)Get(opening, "Offset") - 20) < .001 &&
                    (float)Get(opening, "Rotation") == rotation && (float)Get(opening, "Depth") == (float)Get(wall, "Depth"),
                "Attachment must align centerline, thickness, rotation and offset.");
            var overlap = Part(kind);
            Set(overlap, "Position", Get(opening, "Position"));
            Require(!(bool)Static("FactoryMapPlacement", "AttachOpening", overlap, Parts(wall, opening), 7F)!,
                "Automatic attachment must never overlap an existing opening.");
            Set(overlap, "Level", "Mezzanine");
            Require(!(bool)Static("FactoryMapPlacement", "AttachOpening", overlap, Parts(wall), 7F)!,
                "Automatic attachment must not cross floors.");
            Set(overlap, "Level", "Ground"); Set(overlap, "Length", 130F);
            Require(!(bool)Static("FactoryMapPlacement", "AttachOpening", overlap, Parts(wall), 7F)!,
                "An opening wider than a wall must not attach.");
            var endpoint = (PointF)Static("FactoryMapBuildingStore", "WorldPoint", wall, 60F, 0F)!;
            var snapped = (PointF)Static("FactoryMapPlacement", "SnapWallPoint",
                new PointF(endpoint.X + 2, endpoint.Y + 2), Parts(wall), "Ground", "", 7F)!;
            Require(Math.Abs(snapped.X - endpoint.X) < .01 && Math.Abs(snapped.Y - endpoint.Y) < .01,
                "Wall drawing endpoints must connect to nearby rotated walls.");
        }
        var elevated = Part();
        Set(elevated, "BaseMeters", 4F);
        var window = Part("Window");
        Set(window, "BaseMeters", 5F);
        Set(window, "Position", Get(elevated, "Position"));
        Require((bool)Static("FactoryMapPlacement", "AttachOpening", window, Parts(elevated), 7F)! &&
                (float)Get(window, "BaseMeters") == 1F &&
                (float)Get(Static("FactoryMapBuildingStore", "Resolve", window, Parts(elevated))!, "BaseMeters") == 5F,
            "An attached opening's sill must be relative to its wall, with the correct world elevation.");
    }

    private static void ValidateDrawingHistory()
    {
        using var host = new Form { Size = new Size(1440, 900) };
        using var view = new FactoryMapView(new UserSession { RoleName = "Administrator" }, Array.Empty<MeterSnapshot>(), null!);
        host.Controls.Add(view);
        host.Show();
        var canvas = Field<Control>(view, "_canvas");
        var parts = Field<IList>(view, "_building");
        parts.Clear();
        Call(view, "BeginBuildingEdit", null, EventArgs.Empty);
        Call(canvas, "SetDrawingTool", "Room");
        Paint(canvas);
        Drag(canvas, Screen(canvas, 250, 250), Screen(canvas, 400, 360));
        Require(parts.Count == 5 && parts.Cast<object>().Count(p => (string)Get(p, "Kind") == "Wall") == 4,
            "One rectangle drag must create a floor and exactly four room walls.");
        Require(parts.Cast<object>().Select(p => (string)Get(p, "RoomId")).Distinct().Count() == 1 &&
                parts.Cast<object>().All(p => (float)Get(p, "BaseMeters") == 0),
            "Room members must share identity and start at ground zero.");
        Static("FactoryMapBuildingStore", "Validate", parts);
        var room = Describe(parts);
        Key(canvas, Keys.Control | Keys.Z);
        Require(parts.Count == 0, "One Ctrl+Z must undo the entire room, not one wall.");
        Key(canvas, Keys.Control | Keys.Shift | Keys.Z);
        Require(Describe(parts) == room, "Ctrl+Shift+Z must exactly restore room IDs, dimensions, colors and membership.");
        Field<Button>(view, "_undoButton").Focus();
        Require((bool)Call(view, "ProcessCmdKey", new Message(), Keys.Control | Keys.Z)! && parts.Count == 0,
            "Undo must work while a toolbar button, rather than the canvas, has focus.");
        Require((bool)Call(view, "ProcessCmdKey", new Message(), Keys.Control | Keys.Y)! && Describe(parts) == room,
            "Redo must work while a toolbar button has focus.");

        Call(canvas, "SetDrawingTool", "Door");
        Paint(canvas);
        Drag(canvas, Screen(canvas, 310, 252), Screen(canvas, 315, 252));
        var door = parts.Cast<object>().Single(p => (string)Get(p, "Kind") == "Door");
        Require((string)Get(door, "ParentWallId") != "", "Quick-placement doors must automatically cut/attach to the room wall.");
        Key(canvas, Keys.Control | Keys.Z); Key(canvas, Keys.Control | Keys.Y);
        Require(parts.Count == 6, "Ctrl+Y must restore the attached opening.");
        var current = parts.Cast<object>().Single(p => (string)Get(p, "Kind") == "Door");
        Call(canvas, "SelectBuildingPart", current);
        Key(canvas, Keys.Control | Keys.X);
        Require(parts.Count == 5, "Ctrl+X must cut the selected opening.");
        Key(canvas, Keys.Control | Keys.Z);
        Require(parts.Count == 6, "Cut must be fully undoable.");
        Call(canvas, "SetDrawingTool", "Select");
        current = parts.Cast<object>().Single(p => (string)Get(p, "Kind") == "Door");
        Call(canvas, "SelectBuildingPart", current);
        Paint(canvas);
        var attachedPosition = (PointF)Get(Static("FactoryMapBuildingStore", "Resolve", current, parts)!, "Position");
        Drag(canvas, Screen(canvas, attachedPosition.X * 1365, attachedPosition.Y * 766), Screen(canvas, 310, 205));
        Require((string)Get(current, "ParentWallId") == "", "Dragging away must detach an opening without a properties dialog.");
        Paint(canvas);
        Drag(canvas, Screen(canvas, 310, 205), Screen(canvas, 320, 251));
        Require((string)Get(current, "ParentWallId") != "", "Dragging a free opening near a wall must reattach it.");

        foreach (var tool in new[] { "Wall", "Floor", "Line", "Rectangle", "Ellipse", "Pen" })
        {
            Call(canvas, "SetDrawingTool", tool);
            Set(canvas, "DrawingColor", Color.Red);
            Paint(canvas);
            var count = parts.Count;
            var first = Screen(canvas, 500, 300);
            var last = Screen(canvas, 600, 370);
            Mouse(canvas, "OnMouseDown", first);
            Mouse(canvas, "OnMouseMove", Screen(canvas, 550, 320));
            Mouse(canvas, "OnMouseMove", last);
            Mouse(canvas, "OnMouseUp", last);
            Require(parts.Count == count + 1, $"{tool} must create one persisted building/drawing element.");
            var created = parts[parts.Count - 1]!;
            Require((string)Get(created, "BodyColor") == "#FF0000", "Named drawing colors must serialize as valid #RRGGBB.");
            var after = Describe(parts);
            Key(canvas, Keys.Control | Keys.Z);
            Require(parts.Count == count, $"{tool} gesture must undo in one step.");
            Key(canvas, Keys.Control | Keys.Y);
            Require(Describe(parts) == after, $"{tool} redo must restore exact geometry.");
        }
        Static("FactoryMapBuildingStore", "Validate", parts);
        var directory = Path.Combine(Path.GetTempPath(), "factory-building-tools-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "building.json");
            Static("FactoryMapBuildingStore", "Save", parts, path);
            Require(Describe(Static("FactoryMapBuildingStore", "Load", path)!) == Describe(parts),
                "All drawing tools, normalized pen points and colors must survive save/load exactly.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Call(canvas, "SetDrawingTool", "Room");
        var beforeEscape = parts.Count;
        Mouse(canvas, "OnMouseDown", Screen(canvas, 700, 300));
        Mouse(canvas, "OnMouseMove", Screen(canvas, 750, 350));
        Key(canvas, Keys.Escape);
        Mouse(canvas, "OnMouseUp", Screen(canvas, 750, 350));
        Require(parts.Count == beforeEscape && !canvas.Capture, "Escape must cancel preview without creating geometry.");
        Key(canvas, Keys.Control | Keys.Z);
        Call(canvas, "SetDrawingTool", "Line");
        Paint(canvas);
        Drag(canvas, Screen(canvas, 700, 300), Screen(canvas, 750, 350));
        var branch = Describe(parts);
        Key(canvas, Keys.Control | Keys.Y);
        Require(Describe(parts) == branch, "A new edit after Undo must discard the previous redo branch.");
        var picker = (ComboBox)((TableLayoutPanel)((TableLayoutPanel)canvas.Parent!).GetControlFromPosition(0, 0)!).GetControlFromPosition(1, 0)!;
        picker.SelectedItem = "Mezzanine";
        Require((string)Get(canvas, "ActiveLevel") == "Mezzanine" && Field<string>(canvas, "_viewMode") == "Plan",
            "The pinned floor selector must work during Building without leaving 2D plan mode.");
        picker.SelectedItem = "Ground";
        Call(view, "CancelBuilding");
        Require(parts.Count == 0 && !Field<Button>(view, "_undoButton").Enabled && Field<IList>(view, "_editHistory").Count == 0,
            "Cancel must restore the pre-edit building, release history and disable Undo.");
        host.Hide();
    }

    private static void ValidateEquipmentRegionSelection()
    {
        using var host = new Form { Size = new Size(1440, 900) };
        using var view = new FactoryMapView(new UserSession { RoleName = "Administrator" }, Array.Empty<MeterSnapshot>(), null!);
        host.Controls.Add(view);
        host.Show();
        var canvas = Field<Control>(view, "_canvas");
        var assets = Field<IList>(view, "_assets");
        assets.Clear();
        object Add(string id, float x, float y, string category = "Filler Room", string model = "Product buffer tank")
        {
            var asset = Activator.CreateInstance(Type("FactoryMapAsset"))!;
            Set(asset, "Id", id); Set(asset, "Name", id); Set(asset, "Category", category); Set(asset, "Model", model);
            Set(asset, "Position", new PointF(x / 1365, y / 766)); Set(asset, "EquipmentId", "existing-" + id);
            assets.Add(asset);
            return asset;
        }
        Add("GROUP-A", 450, 350);
        Add("GROUP-B", 520, 350);
        Add("GROUP-PIPE", 485, 350, "Utilities", "Pipe section");
        var outside = Add("OUTSIDE", 800, 550);
        var hidden = Add("HIDDEN", 485, 350, "CCTV", "Camera");
        var upper = Add("UPPER", 485, 350);
        Set(upper, "Level", "Mezzanine");
        var unplaced = Add("UNPLACED", 485, 350);
        Set(unplaced, "IsPlaced", false);
        Call(canvas, "SetBuildingParts", Parts());
        Call(canvas, "SetLayerVisible", "CCTV", false);
        Call(view, "ToggleEditMode", null, EventArgs.Empty);
        Set(canvas, "SnapEnabled", false);
        var initial = assets.Cast<object>().ToDictionary(a => (string)Get(a, "Id"), a => (PointF)Get(a, "Position"));
        foreach (var mode in new[] { "SetPlanView", "SetIsometricView" })
        {
            Call(canvas, mode);
            Call(canvas, "SelectAsset", (object?)null);
            Paint(canvas);
            var hits = Field<IDictionary>(canvas, "_hitAreas");
            var group = assets.Cast<object>().Where(a => ((string)Get(a, "Id")).StartsWith("GROUP-")).ToArray();
            var bounds = group.Select(a => ((GraphicsPath)hits[a]!).GetBounds()).Aggregate(RectangleF.Union);
            bounds.Inflate(12, 12);
            var start = Point.Round(new PointF(bounds.Left, bounds.Top));
            var finish = Point.Round(new PointF(bounds.Right, bounds.Bottom));
            var beforePan = Field<PointF>(canvas, "_pan");
            var history = Field<IList>(view, "_editHistory").Count;
            var historyIndex = Field<int>(view, "_historyIndex");
            ControlDrag(canvas, finish, start);
            Require(((IEnumerable)Get(canvas, "SelectedAssets")).Cast<object>().Count() == 3,
                "Reverse-direction marquee must select all three enclosed visible ground items, not hidden/upper/unplaced items.");
            Require(Field<PointF>(canvas, "_pan") == beforePan && Field<IList>(view, "_editHistory").Count == history,
                "Region selection must not pan the camera or create an undo action.");
            Require(Field<Label>(view, "_selectedTitle").Text.StartsWith("3 "),
                "The real Edit Map picker callback must not collapse the region selection.");
            Drag(canvas, new Point(5, canvas.Height - 80), new Point(25, canvas.Height - 65));
            Require(Field<PointF>(canvas, "_pan") != beforePan &&
                ((IEnumerable)Get(canvas, "SelectedAssets")).Cast<object>().Count() == 3,
                "Plain empty-space dragging must pan without losing the selected group.");
            Paint(canvas);
            Require(Field<Dictionary<string, RectangleF>>(canvas, "_handles").Count == 0,
                "Multi-selection must not resize only one equipment item.");
            var selected = assets.Cast<object>().Single(a => (string)Get(a, "Id") == "GROUP-B");
            var hit = ((GraphicsPath)Field<IDictionary>(canvas, "_hitAreas")[selected]!).GetBounds();
            var center = Point.Round(new PointF(hit.Left + hit.Width / 2, hit.Top + hit.Height / 2));
            Drag(canvas, center, new Point(center.X + 45, center.Y + 22));
            var moved = assets.Cast<object>().Where(a => ((string)Get(a, "Id")).StartsWith("GROUP-")).ToArray();
            var delta = new PointF(((PointF)Get(moved[0], "Position")).X - initial[(string)Get(moved[0], "Id")].X,
                ((PointF)Get(moved[0], "Position")).Y - initial[(string)Get(moved[0], "Id")].Y);
            Require(Math.Abs(delta.X) + Math.Abs(delta.Y) > .001, "Dragging a selected member must actually move the group.");
            foreach (var a in moved)
            {
                var position = (PointF)Get(a, "Position"); var original = initial[(string)Get(a, "Id")];
                Require(Math.Abs(position.X - original.X - delta.X) < .000001 && Math.Abs(position.Y - original.Y - delta.Y) < .000001,
                    "Group movement must preserve relative positions, including its pipe.");
                Require((string)Get(a, "EquipmentId") == "existing-" + (string)Get(a, "Id"), "Group movement must preserve equipment links.");
            }
            Require((PointF)Get(outside, "Position") == initial["OUTSIDE"] &&
                (PointF)Get(hidden, "Position") == initial["HIDDEN"] && (PointF)Get(upper, "Position") == initial["UPPER"],
                "Unselected items must not move.");
            Require(Field<int>(view, "_historyIndex") == historyIndex + 1 &&
                Field<IList>(view, "_editHistory").Count == historyIndex + 2, "An entire group drag must create one undo action.");
            Key(canvas, Keys.Control | Keys.Z);
            foreach (var a in assets.Cast<object>()) Require((PointF)Get(a, "Position") == initial[(string)Get(a, "Id")], "One Undo must restore every group member.");
            Require(((IEnumerable)Get(canvas, "SelectedAssets")).Cast<object>().Count() == 3, "Undo must retain the group selection on restored items.");
            Key(canvas, Keys.Control | Keys.Y);
            Require(assets.Cast<object>().Where(a => ((string)Get(a, "Id")).StartsWith("GROUP-")).All(a =>
                (PointF)Get(a, "Position") != initial[(string)Get(a, "Id")]), "One Redo must restore the whole group move.");
            Key(canvas, Keys.Control | Keys.Z);
            Key(canvas, Keys.Right);
            foreach (var a in assets.Cast<object>().Where(a => ((string)Get(a, "Id")).StartsWith("GROUP-")))
                Require(Math.Abs(((PointF)Get(a, "Position")).X - initial[(string)Get(a, "Id")].X - 1F / 1365) < .000001,
                    "Arrow nudging must move every selected item by the same amount.");
            Key(canvas, Keys.Control | Keys.Z);
            Paint(canvas);
            selected = assets.Cast<object>().Single(a => (string)Get(a, "Id") == "GROUP-A");
            hit = ((GraphicsPath)Field<IDictionary>(canvas, "_hitAreas")[selected]!).GetBounds();
            center = Point.Round(new PointF(hit.Left + hit.Width / 2, hit.Top + hit.Height / 2));
            Drag(canvas, center, new Point(-10000, -10000));
            moved = assets.Cast<object>().Where(a => ((string)Get(a, "Id")).StartsWith("GROUP-")).ToArray();
            var firstPosition = (PointF)Get(moved[0], "Position");
            foreach (var a in moved)
            {
                var position = (PointF)Get(a, "Position");
                Require(position.X >= 0 && position.X <= 4 && position.Y >= 0 && position.Y <= 4, "Group drag must respect map coordinate bounds.");
                Require(Math.Abs((position.X - firstPosition.X) -
                    (initial[(string)Get(a, "Id")].X - initial[(string)Get(moved[0], "Id")].X)) < .000001,
                    "Clamping a group at map boundaries must not collapse its spacing.");
            }
            Key(canvas, Keys.Control | Keys.Z);
            Call(canvas, "SelectAsset", assets[0]);
            Require(((IEnumerable)Get(canvas, "SelectedAssets")).Cast<object>().Count() == 1, "Picker/single selection must replace a region.");
        }
        Call(view, "CancelLayout", null, EventArgs.Empty);
        foreach (var a in assets.Cast<object>()) Require((PointF)Get(a, "Position") == initial[(string)Get(a, "Id")], "Cancel must restore the saved positions.");
        host.Hide();
        Console.WriteLine("Edit Map region selection: actual mouse rectangles in 2D/3D, visible-floor filtering, rigid group dragging, picker integration, single-action undo/redo and cancel passed.");
    }

    private static void ValidateEquipmentTransforms()
    {
        using var host = new Form { Size = new Size(1440, 900) };
        using var view = new FactoryMapView(new UserSession { RoleName = "Administrator" }, Array.Empty<MeterSnapshot>(), null!);
        host.Controls.Add(view);
        host.Show();
        var canvas = Field<Control>(view, "_canvas");
        var assets = Field<IList>(view, "_assets");
        var asset = assets[0]!;
        Call(view, "ToggleEditMode", null, EventArgs.Empty);
        Call(canvas, "SetPlanView");
        Call(canvas, "SelectAsset", asset);
        Set(canvas, "SnapEnabled", false);
        foreach (var handle in new[] { "Length", "Depth", "Rotate" })
        {
            Paint(canvas);
            var handles = Field<Dictionary<string, RectangleF>>(canvas, "_handles");
            Require(handles.ContainsKey(handle), $"Selected equipment must expose a {handle} handle in 2D.");
            var bounds = handles[handle];
            var first = Point.Round(new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
            var property = handle == "Length" ? "ScaleX" : handle == "Depth" ? "ScaleY" : "Rotation";
            var before = (float)Get(asset, property);
            Drag(canvas, first, new Point(first.X + 25, first.Y + 18));
            Require(Math.Abs((float)Get(asset, property) - before) > .001, $"{handle} must change actual equipment geometry.");
            var id = (string)Get(asset, "Id");
            Key(canvas, Keys.Control | Keys.Z);
            asset = assets.Cast<object>().Single(p => (string)Get(p, "Id") == id);
            Require(Math.Abs((float)Get(asset, property) - before) < .001, "Equipment transform must undo in one step.");
        }
        var beforeNudge = (PointF)Get(asset, "Position");
        Key(canvas, Keys.Right);
        Key(canvas, Keys.Control | Keys.Z);
        asset = assets.Cast<object>().Single(p => (string)Get(p, "Id") == (string)Get(asset, "Id"));
        Require((PointF)Get(asset, "Position") == beforeNudge, "Keyboard nudges must be undoable.");
        Require((float)Get(asset, "BaseHeightMeters") == 0 && (float)Call(canvas, "MountHeight", asset)! == 0,
            "Ground-mounted equipment must remain at zero elevation during transforms.");
        Call(view, "CancelLayout", null, EventArgs.Empty);
        host.Hide();
    }
}
