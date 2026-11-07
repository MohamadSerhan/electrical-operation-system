using System.Collections;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class FactoryBuildingValidation
{
    private static readonly Assembly Assembly = typeof(FactoryMapView).Assembly;
    private static Type Type(string name) => Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5." + name)!;
    private static object? Get(object item, string name) => item.GetType().GetProperty(name)!.GetValue(item);
    private static void Set(object item, string name, object value) => item.GetType().GetProperty(name)!.SetValue(item, value);
    private static T Field<T>(object item, string name) => (T)item.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(item)!;
    private static object? Call(object item, string name, params object?[] args) =>
        item.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(item, args);
    private static object? Store(string name, params object?[] args) => Type("FactoryMapBuildingStore").GetMethod(name)!.Invoke(null, args);
    private static object Clone(object item) => Call(item, "Clone")!;
    private static string Describe(object item) => JsonSerializer.Serialize(item, item.GetType());
    private static IList Parts(params object[] parts)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("FactoryBuildingPart")))!;
        foreach (var part in parts) list.Add(part);
        return list;
    }
    private static object Part(string kind = "Wall")
    {
        var part = Activator.CreateInstance(Type("FactoryBuildingPart"))!;
        Set(part, "Kind", kind);
        Set(part, "Name", "Test " + kind);
        Set(part, "Style", kind switch { "Floor" => "Rectangle", "Door" => "Single leaf", "Window" => "Casement", "Gate" => "Double swing", _ => "Solid" });
        Set(part, "Material", kind switch { "Door" => "Wood", "Window" => "Glass", "Gate" => "Steel", _ => "Concrete" });
        return part;
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid building data was accepted.");
    }

    public static void Run()
    {
        ValidatePersistence();
        ValidateCurvedRoads();
        ValidateGeometry();
        ValidateEditing();
        ValidateNavigation();
        FactoryMapBuildingToolsValidation.Run();
        FactoryMapCustomEquipmentValidation.Run();
        Console.WriteLine("Factory building persistence, openings, floors, editing and navigation checks passed.");
    }

    private static void ValidateCurvedRoads()
    {
        foreach (var style in new[] { "Curved dashed left", "Curved dashed right" })
        {
            var road = Part("Floor");
            Set(road, "Kind", "Road"); Set(road, "Style", style);
            Set(road, "Material", "Asphalt"); Set(road, "BodyColor", "#454B50");
            Set(road, "Length", 90F); Set(road, "Depth", 32F);
            Set(road, "X", 0F); Set(road, "Y", 0F); Set(road, "HeightMeters", .02F);
            foreach (var angle in new[] { 30F, 45F, 90F, 135F })
            {
                Set(road, "RoadBendDegrees", angle);
                Store("Validate", Parts(road));
                using var image = new Bitmap(500, 500);
                using var graphics = Graphics.FromImage(image);
                graphics.Clear(Color.White);
                Func<float, float, float, PointF> project = (x, y, z) => new(250 + x * 2, 250 + y * 2);
                using var hit = Draw(graphics, road, Parts(road), project);
                Require(hit.IsVisible(250, 250), "Curve midpoint must be selectable.");
                var half = angle * Math.PI / 360;
                var sign = style.EndsWith("left") ? -1 : 1;
                var end = new PointF((float)(90 * Math.Sin(half - .03)), (float)(sign * 90 * (1 - Math.Cos(half - .03))));
                Require(hit.IsVisible(250 + end.X * 2, 250 + end.Y * 2),
                    $"Curved road end must reach its tangent connector: {style}, {angle}, {end}, bounds {hit.GetBounds()}.");
                Require(!hit.IsVisible(250, 250 + sign * 100), "Inside bend void must not become a rectangular hit.");
                var bounds = (RectangleF)Type("FactoryMapPlacement").GetMethod("BuildingFootprint")!.Invoke(null, new[] { road })!;
                Require(bounds.Contains(PointF.Empty) && bounds.Width > 30,
                    "Snapping bounds must include actual curved-road geometry.");
            }
            Set(road, "Length", 10F);
            Reject(() => Store("Validate", Parts(road)));
            Set(road, "Length", 90F);
            Set(road, "RoadBendDegrees", float.NaN);
            Reject(() => Store("Validate", Parts(road)));
            Set(road, "RoadBendDegrees", 45F);
            var directory = Path.Combine(Path.GetTempPath(), "road-curve-" + Guid.NewGuid().ToString("N"));
            try
            {
                var path = Path.Combine(directory, "building.json");
                Store("Save", Parts(road), path);
                var restored = (IList)Store("Load", path)!;
                Require((string)Get(restored[0]!, "Style")! == style &&
                    (float)Get(restored[0]!, "RoadBendDegrees")! == 45F,
                    "Curved-road handedness and angle must persist.");
                using var dialog = (Form)Activator.CreateInstance(Type("FactoryMapBuildingForm"),
                    new object[] { road, Parts(road), true })!;
                Require(Field<ComboBox>(dialog, "_style").Items.Contains(style) &&
                    Field<NumericUpDown>(dialog, "_bend").Value == 45 &&
                    Field<NumericUpDown>(dialog, "_bend").Enabled,
                    "Build dialog must expose the actual curved-road style and angle.");
                var updated = Call(dialog, "ReadPart")!;
                Require((float)Get(updated, "RoadBendDegrees")! == 45F,
                    "Build dialog must retain the selected bend angle.");
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
        Console.WriteLine("Curved-road left/right styles, 30/45/90/135 degree geometry, hits, bounds and radius constraints passed.");
    }

    public static void Render(string directory)
    {
        var samples = new List<object>();
        foreach (var kind in new[] { "Door", "Gate", "Window", "Floor" })
        {
            var styles = (Dictionary<string, string[]>)Type("FactoryMapBuildingStore").GetField("Styles", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var materials = (Dictionary<string, string[]>)Type("FactoryMapBuildingStore").GetField("Materials", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var choices = kind == "Floor" ? materials[kind] : styles[kind];
            foreach (var choice in choices)
            {
                foreach (var open in kind == "Floor" ? new[] { false } : new[] { false, true })
                {
                    var part = Part(kind);
                    Set(part, "X", 0F); Set(part, "Y", 0F);
                    Set(part, "Length", kind == "Floor" ? 65F : 40F);
                    Set(part, "Depth", kind == "Floor" ? 48F : 2F);
                    Set(part, "HeightMeters", kind == "Floor" ? 0.02F : 3F);
                    Set(part, "Style", kind == "Floor" ? "Rectangle" : choice);
                    if (kind == "Floor") Set(part, "Material", choice);
                    Set(part, "BodyColor", Store("MaterialColor", Get(part, "Material"))!);
                    Set(part, "IsOpen", open);
                    Set(part, "Name", $"{kind} / {choice} / {(kind == "Floor" ? "finish" : open ? "open" : "closed")}");
                    samples.Add(part);
                }
            }
        }
        using var bitmap = new Bitmap(1600, ((samples.Count + 3) / 4) * 290);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var title = new Font("Segoe UI", 10, FontStyle.Bold))
        using (var brush = new SolidBrush(Color.FromArgb(24, 43, 61)))
        {
            graphics.Clear(Color.FromArgb(246, 249, 252));
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            for (var index = 0; index < samples.Count; index++)
            {
                var part = samples[index];
                var x = (index % 4) * 400;
                var y = (index / 4) * 290;
                graphics.DrawString((string)Get(part, "Name")!, title, brush, new RectangleF(x + 10, y + 8, 380, 40));
                Func<float, float, float, PointF> projection = (px, py, z) => new(x + 200 + (px - py) * 2.5F, y + 195 + ((px + py) * 0.5F - z) * 2.5F);
                using var hit = Draw(graphics, part, Parts(part), projection);
            }
        }
        bitmap.Save(Path.Combine(directory, "FactoryMap-BuildingLibrary.png"));
        using var view = new FactoryMapView(new UserSession { RoleName = "Administrator" }, Array.Empty<MeterSnapshot>(), null!);
        view.Size = new Size(1440, 900);
        view.CreateControl();
        Call(view, "BeginBuildingEdit", null, EventArgs.Empty);
        var partList = Field<IList>(view, "_building");
        Field<ComboBox>(view, "_buildingPicker").SelectedItem = partList[0];
        using var workspace = new Bitmap(view.Width, view.Height);
        view.DrawToBitmap(workspace, view.ClientRectangle);
        workspace.Save(Path.Combine(directory, "FactoryMap-BuildingEditor.png"));
        view.Size = new Size(1000, 700);
        using var compact = new Bitmap(view.Width, view.Height);
        view.DrawToBitmap(compact, view.ClientRectangle);
        compact.Save(Path.Combine(directory, "FactoryMap-BuildingCompact.png"));
        using var dialog = (Form)Activator.CreateInstance(Type("FactoryMapBuildingForm"), new object[] { Part("Door"), partList, true })!;
        dialog.Show();
        Application.DoEvents();
        using var dialogBitmap = new Bitmap(dialog.Width, dialog.Height);
        dialog.DrawToBitmap(dialogBitmap, new Rectangle(Point.Empty, dialogBitmap.Size));
        dialogBitmap.Save(Path.Combine(directory, "FactoryMap-BuildingProperties.png"));
        dialog.Close();
    }

    private static void ValidatePersistence()
    {
        var defaults = (IList)Store("Defaults", (object?)null)!;
        Store("Validate", defaults);
        Require(defaults.Cast<object>().Count(part => (string)Get(part, "Kind")! == "Wall") > 90, "Building migration must retain all traced walls.");
        Require(defaults.Cast<object>().Count(part => (string)Get(part, "Kind")! == "Floor") >= 25, "Room floor seeds must cover the factory's main rooms.");
        var wall = Part();
        Set(wall, "Length", 100F);
        Set(wall, "HeightMeters", 4F);
        var door = Part("Door");
        Set(door, "Length", 20F);
        Set(door, "HeightMeters", 2.5F);
        Set(door, "ParentWallId", Get(wall, "Id")!);
        Set(door, "Offset", 10F);
        Set(door, "IsOpen", true);
        Set(door, "OpeningDirection", "Right outward");
        var floor = Part("Floor");
        Set(floor, "Style", "L-shaped");
        Set(floor, "Depth", 60F);
        Set(floor, "HeightMeters", 0.02F);
        Set(floor, "Material", "Epoxy");
        var parts = Parts(wall, door, floor);
        var directory = Path.Combine(Path.GetTempPath(), "eoems-building-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "building.json");
        try
        {
            Store("Save", parts, path);
            var loaded = (IList)Store("Load", path)!;
            Require(Describe(parts) == Describe(loaded), "Every building field must survive Save/reload, including opening state and attachment.");
            Set(wall, "Rotation", 90F);
            var attached = Store("Resolve", door, parts)!;
            var position = (PointF)Get(attached, "Position")!;
            Require(Math.Abs(position.X - 0.5F) < 0.00001 && Math.Abs(position.Y - (0.5F + 10F / 766)) < 0.00001,
                "Wall rotation must rotate an attached opening's center and orientation.");
            Set(wall, "X", 0.7F);
            attached = Store("Resolve", door, parts)!;
            Require(Math.Abs((float)Get(attached, "X")! - 0.7F) < 0.00001 && (float)Get(attached, "Rotation")! == 90F,
                "Moving a wall must carry its attached opening.");
            Store("Save", parts, path);
            Require(File.Exists(path + ".bak"), "Building updates must retain a previous-version backup.");
            var saved = File.ReadAllText(path);
            foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, -1F, 101F })
            {
                var invalidPart = Clone(wall);
                Set(invalidPart, "HeightMeters", invalid);
                Reject(() => Store("Save", Parts(invalidPart), path));
                Require(File.ReadAllText(path) == saved, "Rejected building geometry must not overwrite saved data.");
            }
            Reject(() => Store("Validate", Parts(wall, wall)));
            var badDoor = Clone(door);
            Set(badDoor, "ParentWallId", "missing-wall");
            Reject(() => Store("Validate", Parts(wall, badDoor)));
            Set(badDoor, "ParentWallId", Get(wall, "Id")!);
            Set(badDoor, "Length", 120F);
            Reject(() => Store("Validate", Parts(wall, badDoor)));
            Set(badDoor, "Length", 20F);
            Set(badDoor, "BaseMeters", 3F);
            Reject(() => Store("Validate", Parts(wall, badDoor)));
            var overlapping = Clone(door);
            Set(overlapping, "Id", Guid.NewGuid().ToString("N"));
            Reject(() => Store("Validate", Parts(wall, door, overlapping)));
            Set(badDoor, "ParentWallId", "");
            Set(badDoor, "BodyColor", "red");
            Reject(() => Store("Validate", Parts(badDoor)));
            Store("Save", Parts(), path);
            Require(((IList)Store("Load", path)!).Count == 0, "Intentionally deleted buildings must remain empty after reload.");
            File.WriteAllText(path, "{\"Version\":3,\"Parts\":[]}");
            Reject(() => Store("Load", path));
            var assets = (IList)typeof(FactoryMapView).GetMethod("CreateAssets", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
            var gate = Clone(assets[0]!);
            assets.Clear();
            Set(gate, "Category", "Access Control"); Set(gate, "Model", "Rapid roll-up gate"); Set(gate, "EquipmentId", "");
            Set(gate, "IsOpen", true); Set(gate, "OpeningDirection", "Right outward");
            assets.Add(gate);
            var equipmentPath = Path.Combine(directory, "equipment.json");
            var equipmentStore = Type("FactoryMapLayoutStore");
            equipmentStore.GetMethod("SaveAssets")!.Invoke(null, new object[] { assets, equipmentPath });
            var gatesReloaded = (IList)equipmentStore.GetMethod("LoadAssets")!.Invoke(null, new object[] { assets, equipmentPath })!;
            Require(Describe(assets) == Describe(gatesReloaded), "Equipment gates must persist opening state/direction without changing existing layout compatibility.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static GraphicsPath Draw(Graphics graphics, object part, IList parts, Func<float, float, float, PointF> projection)
    {
        var hit = new GraphicsPath(FillMode.Winding);
        Type("FactoryMapBuildingRenderer").GetMethod("Draw")!.Invoke(null, new object[] { graphics, part, parts, 40, projection, hit });
        return hit;
    }

    private static void ValidateGeometry()
    {
        var wall = Part();
        Set(wall, "X", 0F); Set(wall, "Y", 0F);
        Set(wall, "Length", 100F); Set(wall, "HeightMeters", 4F);
        var door = Part("Door");
        Set(door, "Length", 20F); Set(door, "HeightMeters", 2.5F);
        Set(door, "ParentWallId", Get(wall, "Id")!);
        var parts = Parts(wall, door);
        using var bitmap = new Bitmap(400, 300);
        using var graphics = Graphics.FromImage(bitmap);
        Func<float, float, float, PointF> front = (x, y, z) => new(200 + x, 160 + y * 0.3F - z * 3);
        using (var solid = Draw(graphics, wall, Parts(wall), front))
            Require(solid.IsVisible(200, 140), "The uncut wall must fill the future doorway.");
        using (var cut = Draw(graphics, wall, parts, front))
        {
            Require(!cut.IsVisible(200, 140), "An attached door must cut a real wall opening, not be painted over a solid wall.");
            Require(cut.IsVisible(160, 140) && cut.IsVisible(200, 75), "Door openings must preserve adjacent wall and lintel.");
        }
        Set(door, "Kind", "Window"); Set(door, "Style", "Casement"); Set(door, "Material", "Glass");
        Set(door, "BaseMeters", 1F); Set(door, "HeightMeters", 1.5F);
        using (var cut = Draw(graphics, wall, parts, front))
            Require(cut.IsVisible(200, 150) && !cut.IsVisible(200, 118), "Window openings must preserve the sill wall below them.");

        var shapes = (Dictionary<string, string[]>)Type("FactoryMapBuildingStore").GetField("Styles", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var materials = (Dictionary<string, string[]>)Type("FactoryMapBuildingStore").GetField("Materials", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        foreach (var kind in new[] { "Door", "Gate", "Window" })
            foreach (var style in shapes[kind])
                foreach (var material in materials[kind])
                {
                    var part = Part(kind);
                    Set(part, "X", 0F); Set(part, "Y", 0F); Set(part, "Length", 40F);
                    Set(part, "Style", style); Set(part, "Material", material);
                    Func<float, float, float, PointF> iso = (x, y, z) => new(200 + (x - y) * 2, 160 + (x + y) - z * 3);
                    graphics.Clear(Color.White);
                    using var closed = Draw(graphics, part, Parts(part), iso);
                    Require(closed.PointCount > 20, $"Detailed selectable frames and leaves required: {kind}/{style}/{material}.");
                    Set(part, "IsOpen", true);
                    using var opened = Draw(graphics, part, Parts(part), iso);
                    if (style != "Fixed")
                        Require(!closed.PathPoints.SequenceEqual(opened.PathPoints), $"Open/closed state must change geometry: {kind}/{style}.");
                    if (style == "Rapid roll-up")
                    {
                        using var openFront = Draw(graphics, part, Parts(part), front);
                        Require(!openFront.IsVisible(200, 140) && openFront.IsVisible(200, 95),
                            "An open rapid door must roll UP, leaving its lower opening clear, not swing sideways.");
                    }
                }
        var floor = Part("Floor");
        Set(floor, "X", 0F); Set(floor, "Y", 0F);
        Set(floor, "Length", 100F); Set(floor, "Depth", 80F); Set(floor, "HeightMeters", 0.02F);
        Func<float, float, float, PointF> top = (x, y, z) => new(200 + x, 160 + y);
        var textures = new HashSet<long>();
        foreach (var material in materials["Floor"])
        {
            graphics.Clear(Color.White);
            Set(floor, "Material", material);
            Set(floor, "BodyColor", Store("MaterialColor", material)!);
            using var hit = Draw(graphics, floor, Parts(floor), top);
            Require(hit.IsVisible(200, 160), "Every floor material needs a selectable footprint.");
            long signature = 0;
            for (var y = 125; y < 195; y++)
                for (var x = 155; x < 245; x++) signature += bitmap.GetPixel(x, y).ToArgb();
            Require(textures.Add(signature), "Tile, concrete, asphalt, sand, gravel and epoxy must render distinct surface treatments.");
        }
        Set(floor, "Style", "L-shaped");
        using var lShape = Draw(graphics, floor, Parts(floor), top);
        Require(!lShape.IsVisible(220, 180) && lShape.IsVisible(180, 180), "L-shaped room floors must have a real missing quadrant.");
    }

    private static void ValidateEditing()
    {
        using var view = new FactoryMapView(new UserSession { RoleName = "Administrator" }, Array.Empty<MeterSnapshot>(), null!);
        view.Size = new Size(1440, 900);
        view.CreateControl();
        var parts = Field<IList>(view, "_building");
        var assets = Field<IList>(view, "_assets");
        var original = Describe(parts);
        var originalEquipment = Describe(assets);
        var canvas = Field<Control>(view, "_canvas");
        Call(view, "BeginBuildingEdit", null, EventArgs.Empty);
        Require(Field<bool>(view, "_buildingEditing") && !(bool)Get(canvas, "EditMode")!, "Building must be a separate editing transaction.");
        Require(!Field<Button>(view, "_editButton").Enabled, "Equipment editing must be disabled until Building Save/Cancel.");
        Call(view, "ToggleEditMode", null, EventArgs.Empty);
        Require(!Field<bool>(view, "_editing"), "The equipment entry point must reject a concurrent building transaction.");
        var selected = parts[0]!;
        Field<ComboBox>(view, "_buildingPicker").SelectedItem = selected;
        Require(ReferenceEquals(Get(canvas, "SelectedBuildingPart"), selected), "Building list selection must select exact wall segments, even where map geometry overlaps.");
        foreach (var size in new[] { new Size(1440, 900), new Size(1000, 700) })
        {
            view.Size = size;
            using var capture = new Bitmap(view.Width, view.Height);
            view.DrawToBitmap(capture, view.ClientRectangle);
            var tools = Field<FlowLayoutPanel>(view, "_buildingTools");
            foreach (var action in tools.Controls.Cast<Control>().Where(control => control is Button))
                Require(tools.ClientRectangle.Contains(action.Bounds), "Building Save/Cancel and editing actions must remain accessible at compact workspace sizes.");
        }
        using var dialog = (Form)Activator.CreateInstance(Type("FactoryMapBuildingForm"), new object[] { selected, parts, false })!;
        var unchanged = Describe(selected);
        Field<TextBox>(dialog, "_name").Text = "";
        Call(dialog, "ApplyPart", null, EventArgs.Empty);
        Require(dialog.DialogResult != DialogResult.OK && !string.IsNullOrWhiteSpace(Field<Label>(dialog, "_error").Text),
            "Invalid building edits must display an explicit error and keep the form open.");
        Field<TextBox>(dialog, "_name").Text = "Renamed building element";
        Field<NumericUpDown>(dialog, "_rotation").Value = 15;
        Call(dialog, "ApplyPart", null, EventArgs.Empty);
        Require(dialog.DialogResult == DialogResult.OK && Describe(selected) == unchanged, "Building properties must edit a validated copy until Apply.");
        Set(selected, "Name", "Unsaved wall");
        Set(selected, "BodyColor", "#FFAA66");
        Set(selected, "HeightMeters", 7F);
        parts.RemoveAt(1);
        parts.Add(Part("Gate"));
        Call(view, "CancelBuilding");
        Require(Describe(parts) == original && Describe(assets) == originalEquipment, "Building Cancel must restore every geometry/material/state and never touch equipment.");
        Require(!(bool)Get(canvas, "BuildingEditMode")!, "Building Cancel must leave editing mode.");
        Call(view, "ToggleEditMode", null, EventArgs.Empty);
        Require(!Field<Button>(view, "_buildingButton").Enabled, "Building must be disabled during equipment editing.");
        Call(view, "CancelLayout", null, EventArgs.Empty);
        foreach (var session in new[] { new UserSession { RoleName = "Operator" }, new UserSession { RoleName = "Administrator", IsOfflinePreview = true } })
        {
            using var denied = new FactoryMapView(session, Array.Empty<MeterSnapshot>(), null!);
            Call(denied, "BeginBuildingEdit", null, EventArgs.Empty);
            Require(!Field<bool>(denied, "_buildingEditing"), "Building editing must require an authenticated non-preview administrator.");
        }
        using var newDialog = (Form)Activator.CreateInstance(Type("FactoryMapBuildingForm"), new object[] { Part(), Parts(), true })!;
        foreach (var kind in new[] { "Wall", "Door", "Gate", "Window", "Floor" })
        {
            Field<ComboBox>(newDialog, "_kind").SelectedItem = kind;
            Call(newDialog, "ApplyPart", null, EventArgs.Empty);
            Require(newDialog.DialogResult == DialogResult.OK && (string)Get(Get(newDialog, "Part")!, "Kind")! == kind, "Every building type must be addable through the property form.");
        }
        Field<ComboBox>(newDialog, "_material").SelectedItem = "Asphalt";
        Call(newDialog, "ApplyPart", null, EventArgs.Empty);
        Require((string)Get(Get(newDialog, "Part")!, "BodyColor")! == "#454C53", "Changing a material must choose its recognizable default color before optional custom recoloring.");
        var attachmentWall = Part();
        Set(attachmentWall, "Rotation", 45F);
        var attachedDoor = Part("Door");
        Set(attachedDoor, "ParentWallId", Get(attachmentWall, "Id")!);
        Set(attachedDoor, "Length", 18F);
        Set(attachedDoor, "HeightMeters", 2F);
        Set(attachedDoor, "Offset", 10F);
        var effective = Store("Resolve", attachedDoor, Parts(attachmentWall, attachedDoor))!;
        using var detach = (Form)Activator.CreateInstance(Type("FactoryMapBuildingForm"), new object[] { attachedDoor, Parts(attachmentWall, attachedDoor), false })!;
        Field<ComboBox>(detach, "_wall").SelectedIndex = 0;
        Call(detach, "ApplyPart", null, EventArgs.Empty);
        var detached = Get(detach, "Part")!;
        Require((string)Get(detached, "ParentWallId")! == "" && Math.Abs((float)Get(detached, "X")! - (float)Get(effective, "X")!) < 0.0001 &&
            Math.Abs((float)Get(detached, "Y")! - (float)Get(effective, "Y")!) < 0.0001 && (float)Get(detached, "Rotation")! == 45,
            "Detaching an opening must retain its visible position and orientation, not jump to its stale serialized coordinates.");
        var formType = Type("FactoryMapItemForm");
        var catalog = typeof(FactoryMapView).GetMethod("EquipmentCatalog", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        using var gateDialog = (Form)Activator.CreateInstance(formType, new[] { assets[0]!, catalog })!;
        Field<ComboBox>(gateDialog, "_link").SelectedIndex = 0;
        Field<ComboBox>(gateDialog, "_category").SelectedItem = "Access Control";
        Field<ComboBox>(gateDialog, "_shape").SelectedItem = "Rapid roll-up gate";
        Field<CheckBox>(gateDialog, "_open").Checked = true;
        Field<ComboBox>(gateDialog, "_direction").SelectedItem = "Right outward";
        Call(gateDialog, "ApplyItem", null, EventArgs.Empty);
        var gate = Get(gateDialog, "Asset")!;
        Require((bool)Get(gate, "IsOpen")! && (string)Get(gate, "OpeningDirection")! == "Right outward", "Equipment-library gates need editable visual state and direction.");
    }

    private static Point VisiblePoint(GraphicsPath path)
    {
        var bounds = path.GetBounds();
        for (var y = (int)bounds.Top; y <= bounds.Bottom; y++)
            for (var x = (int)bounds.Left; x <= bounds.Right; x++)
                if (path.IsVisible(x, y)) return new Point(x, y);
        throw new InvalidOperationException("No visible selectable geometry.");
    }

    private static void ValidateNavigation()
    {
        using var view = new FactoryMapView(new UserSession { RoleName = "Administrator" }, Array.Empty<MeterSnapshot>(), null!);
        view.Size = new Size(1440, 900);
        view.CreateControl();
        var canvas = Field<Control>(view, "_canvas");
        canvas.CreateControl();
        Call(canvas, "SetTopView");
        var cursor = new Point(660, 350);
        var before = (PointF)Call(canvas, "ScreenToMap", cursor)!;
        Call(canvas, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, cursor.X, cursor.Y, 120));
        var after = (PointF)Call(canvas, "ScreenToMap", cursor)!;
        Require(Math.Abs(before.X - after.X) < 0.00001 && Math.Abs(before.Y - after.Y) < 0.00001, $"Zoom must retain the exact map point under the cursor: {before} -> {after}, canvas {canvas.ClientSize}.");
        var oldPan = Field<PointF>(canvas, "_pan");
        var right = new KeyEventArgs(Keys.Right);
        Call(canvas, "OnKeyDown", right);
        Require(right.Handled && Field<PointF>(canvas, "_pan").X == oldPan.X - 30, "Focused-canvas arrow keys must pan while zoomed.");
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Up));
        Require(Field<PointF>(canvas, "_pan").Y == oldPan.Y + 30, "Up/down keys must pan vertically, not alter equipment positions.");
        Call(canvas, "FitFactory");
        Set(canvas, "EditMode", true);
        using var bitmap = new Bitmap(canvas.Width, canvas.Height);
        canvas.DrawToBitmap(bitmap, canvas.ClientRectangle);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var frame = 0; frame < 10; frame++) canvas.DrawToBitmap(bitmap, canvas.ClientRectangle);
        clock.Stop();
        Console.WriteLine($"Factory map paint: {clock.Elapsed.TotalMilliseconds / 10:F1} ms/frame (10 full top-view renders).");
        var hits = Field<IDictionary>(canvas, "_hitAreas");
        var asset = hits.Keys.Cast<object>().First();
        var point = VisiblePoint((GraphicsPath)hits[asset]!);
        Call(canvas, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, point.X, point.Y, 0));
        var hovered = (object)Call(canvas, "FindAssetAt", point)!;
        Require(Field<ToolTip>(canvas, "_toolTip").GetToolTip(canvas)?.Contains((string)Get(hovered, "Name")!) == true, "Hover must show the item's name without persistent map labels.");
        var grab = (PointF)Get(hovered, "Position")!;
        Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
        Require(canvas.Capture, "Equipment dragging must capture the mouse.");
        Call(canvas, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, point.X + 20, point.Y + 15, 0));
        var moved = (PointF)Get(hovered, "Position")!;
        Require(moved != grab, "Press-and-drag must move equipment directly, not just edit coordinate fields.");
        Call(canvas, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, point.X + 20, point.Y + 15, 0));
        Require(!canvas.Capture && !Field<bool>(canvas, "_draggingAsset"), "Mouse-up must release capture and end equipment dragging.");
        var panBefore = Field<PointF>(canvas, "_pan");
        Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, 5, 550, 0));
        Call(canvas, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, 30, 570, 0));
        Call(canvas, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 30, 570, 0));
        Require(Field<PointF>(canvas, "_pan") != panBefore, "Blank-space left dragging without Ctrl must pan in equipment edit mode.");
        panBefore = Field<PointF>(canvas, "_pan");
        Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Middle, 1, 5, 550, 0));
        Call(canvas, "OnMouseMove", new MouseEventArgs(MouseButtons.Middle, 0, 30, 570, 0));
        Call(canvas, "OnMouseUp", new MouseEventArgs(MouseButtons.Middle, 1, 30, 570, 0));
        Require(Field<PointF>(canvas, "_pan") != panBefore, "Middle dragging must still pan in equipment edit mode.");
        Set(canvas, "EditMode", false);
        Set(canvas, "BuildingEditMode", true);
        var wall = Part();
        Call(canvas, "SetBuildingParts", Parts(wall));
        Call(canvas, "SelectBuildingPart", wall);
        canvas.DrawToBitmap(bitmap, canvas.ClientRectangle);
        var handles = Field<Dictionary<string, RectangleF>>(canvas, "_handles");
        Require(handles.ContainsKey("Rotate") && handles.ContainsKey("Length"), "Selected walls must expose mouse resize and rotate handles.");
        var lengthHandle = Point.Round(new PointF(handles["Length"].X + 8, handles["Length"].Y + 8));
        Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, lengthHandle.X, lengthHandle.Y, 0));
        Call(canvas, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, lengthHandle.X + 20, lengthHandle.Y, 0));
        Call(canvas, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, lengthHandle.X + 20, lengthHandle.Y, 0));
        Require((float)Get(wall, "Length")! > 80F, "Dragging the wall's length handle must resize its actual geometry.");
        canvas.DrawToBitmap(bitmap, canvas.ClientRectangle);
        var rotate = handles["Rotate"];
        var rotatePoint = Point.Round(new PointF(rotate.X + 8, rotate.Y + 8));
        Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, rotatePoint.X, rotatePoint.Y, 0));
        Call(canvas, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, rotatePoint.X + 30, rotatePoint.Y + 15, 0));
        Call(canvas, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, rotatePoint.X + 30, rotatePoint.Y + 15, 0));
        Require((float)Get(wall, "Rotation")! > 1, "The round handle must rotate the wall, independently of the camera.");
        var child = Part("Door");
        Set(child, "Length", 18F); Set(child, "HeightMeters", 2F);
        Set(child, "ParentWallId", Get(wall, "Id")!);
        var building = Parts(wall, child);
        Call(canvas, "SetBuildingParts", building);
        Call(canvas, "SelectBuildingPart", wall);
        canvas.DrawToBitmap(bitmap, canvas.ClientRectangle);
        var buildingHits = Field<IDictionary>(canvas, "_buildingHits");
        var wallPoint = VisiblePoint((GraphicsPath)buildingHits[wall]!);
        var wallBefore = (PointF)Get(wall, "Position")!;
        var childBefore = (PointF)Get(Store("Resolve", child, building)!, "Position")!;
        Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, wallPoint.X, wallPoint.Y, 0));
        Call(canvas, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, wallPoint.X + 20, wallPoint.Y + 12, 0));
        Call(canvas, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, wallPoint.X + 20, wallPoint.Y + 12, 0));
        var wallAfter = (PointF)Get(wall, "Position")!;
        var childAfter = (PointF)Get(Store("Resolve", child, building)!, "Position")!;
        Require(wallBefore != wallAfter && Math.Abs((childAfter.X - childBefore.X) - (wallAfter.X - wallBefore.X)) < 0.00001,
            "Direct wall dragging must move the wall and carry its attached opening by the same displacement.");
        Set(canvas, "ShowFloors", false);
        Set(canvas, "ShowOpenings", false);
        Require((bool)Get(canvas, "ShowWalls")!, "Room floors, openings and wall visibility must be independent.");
        Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Middle, 1, 20, 20, 0));
        canvas.Capture = false;
        Require(!Field<bool>(canvas, "_panning"), "Losing capture must cancel gestures instead of leaving navigation stuck.");
    }
}
