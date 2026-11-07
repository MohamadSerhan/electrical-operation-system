using System.Collections;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class FactoryMapEnhancementsValidation
{
    private static Type Type(string name) => typeof(FactoryMapView).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5." + name)!;
    private static object? Call(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(target, args);
    private static object? Static(string type, string name, params object?[] args) => Type(type).GetMethod(name)!.Invoke(null, args);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object Get(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    private static string Describe(object target) => JsonSerializer.Serialize(target, target.GetType());
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static IList Parts(params object[] elements)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("FactoryBuildingPart")))!;
        foreach (var part in elements) list.Add(part);
        return list;
    }
    private static object Part(string kind = "Wall")
    {
        var part = Activator.CreateInstance(Type("FactoryBuildingPart"))!;
        Set(part, "Kind", kind);
        Set(part, "Style", kind == "Floor" ? "Rectangle" : kind == "Door" ? "Single leaf" : kind == "Stairs" ? "Straight flight" : kind == "Road" ? "Dashed yellow center" : "Solid");
        if (kind == "Door") Set(part, "Material", "Steel");
        Set(part, "Length", kind == "Door" ? 18F : 80F);
        Set(part, "HeightMeters", kind == "Door" ? 2F : 3F);
        return part;
    }

    public static void Run()
    {
        ValidateSnappingAndNudging();
        ValidateRooms();
        ValidateRotationAndLibrary();
        ValidateSiteAndRoads();
        FactoryMapRefinementsValidation.Run();
        Console.WriteLine("Factory edge snapping, precision nudging, named rooms, stairs and four-heading checks passed.");
    }

    private static void ValidateSiteAndRoads()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eoems-site-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "site.json");
        var settings = Activator.CreateInstance(Type("FactoryMapSiteSettings"))!;
        void Reject(Action action)
        {
            try { action(); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { return; }
            throw new InvalidOperationException("Invalid site settings were accepted.");
        }
        try
        {
            var defaults = Static("FactoryMapSiteStore", "Load", path)!;
            Require((int)Get(defaults, "WidthPercent") == 100 && (int)Get(defaults, "DepthPercent") == 100,
                "Absent site settings must retain the original coordinate footprint.");
            Static("FactoryMapSiteStore", "Save", settings, path);
            Set(settings, "WidthPercent", 250); Set(settings, "DepthPercent", 200); Set(settings, "GroundColor", "#A1B2C3");
            Static("FactoryMapSiteStore", "Save", settings, path);
            Require(Describe(settings) == Describe(Static("FactoryMapSiteStore", "Load", path)!) && File.Exists(path + ".bak"),
                "Site dimensions and ground color must survive reload and retain a backup.");
            var saved = File.ReadAllText(path);
            foreach (var (property, value) in new (string, object)[] {
                ("WidthPercent", 99), ("WidthPercent", 401), ("DepthPercent", 0), ("DepthPercent", 401), ("GroundColor", "red") })
            {
                var previous = Get(settings, property);
                Set(settings, property, value);
                Reject(() => Static("FactoryMapSiteStore", "Save", settings, path));
                Require(File.ReadAllText(path) == saved, "Rejected site settings must leave persisted settings unchanged.");
                Set(settings, property, previous);
            }
            var roadPath = Path.Combine(directory, "building.json");
            var wall = Part();
            File.WriteAllText(roadPath, "{\"Version\":1,\"Parts\":[" + Describe(wall) + "]}");
            var migrated = (IList)Static("FactoryMapBuildingStore", "Load", roadPath)!;
            Require(migrated.Cast<object>().Count(part => (string)Get(part, "Kind") == "Road") == 6 &&
                migrated.Cast<object>().Any(part => Describe(part) == Describe(wall)),
                "Nonempty legacy buildings must gain six editable traced road segments without moving existing parts.");
            Static("FactoryMapBuildingStore", "Save", migrated, roadPath);
            Require(Describe(Static("FactoryMapBuildingStore", "Load", roadPath)!) == Describe(migrated), "Roads must persist every style, color, dimension and rotation without duplicate segments.");
            var withoutRoads = Parts(wall);
            Static("FactoryMapBuildingStore", "Save", withoutRoads, roadPath);
            Require(((IList)Static("FactoryMapBuildingStore", "Load", roadPath)!).Count == 1, "Deleted roads must not return in version 2.");
            File.WriteAllText(roadPath, "{\"Version\":1,\"Parts\":[]}");
            Require(((IList)Static("FactoryMapBuildingStore", "Load", roadPath)!).Count == 0, "Intentionally empty legacy buildings must stay empty.");
            var styles = ((Dictionary<string, string[]>)Type("FactoryMapBuildingStore").GetField("Styles",
                BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)["Road"];
            using var image = new Bitmap(400, 300);
            using var graphics = Graphics.FromImage(image);
            foreach (var style in styles)
                foreach (var rotation in new[] { 0F, 90F, 180F, 270F })
                {
                    var road = Activator.CreateInstance(Type("FactoryBuildingPart"))!;
                    Set(road, "Kind", "Road"); Set(road, "Style", style); Set(road, "Material", "Asphalt");
                    Set(road, "BodyColor", "#454C53"); Set(road, "X", 0F); Set(road, "Y", 0F);
                    Set(road, "Length", 150F); Set(road, "Depth", 38F); Set(road, "HeightMeters", 0.02F); Set(road, "Rotation", rotation);
                    Static("FactoryMapBuildingStore", "Validate", Parts(road));
                    graphics.Clear(Color.White);
                    using var hit = new GraphicsPath(FillMode.Winding);
                    Func<float, float, float, PointF> projection = (x, y, z) => new(200 + x, 150 + y - z);
                    Static("FactoryMapBuildingRenderer", "Draw", graphics, road, Parts(road), 40, projection, hit);
                    Require(hit.PointCount >= 4 && hit.GetBounds().Width >= 37 && hit.GetBounds().Height >= 37,
                        "Every road style must be selectable and rotate in the horizontal plane.");
                    if (style == "Dashed yellow center")
                    {
                        var yellow = 0;
                        for (var x = 0; x < image.Width; x++)
                            for (var y = 0; y < image.Height; y++)
                            {
                                var pixel = image.GetPixel(x, y);
                                if (pixel.R > 220 && pixel.G > 160 && pixel.G < 230 && pixel.B < 100) yellow++;
                            }
                        Require(yellow > 50, "Asphalt roads must visibly render yellow center dashes at all four headings.");
                    }
                }
            using var view = new FactoryMapView(new UserSession { RoleName = "Administrator" }, Array.Empty<MeterSnapshot>(), null!);
            view.Size = new Size(1440, 900); view.CreateControl();
            var canvas = Field<Control>(view, "_canvas");
            var assets = Field<IList>(view, "_assets");
            Call(canvas, "SetBuildingParts", Parts());
            Call(canvas, "SetTopView");
            Call(canvas, "SetSite", defaults);
            var before = (PointF)Call(canvas, "ProjectMapPoint", 400F, 300F)!;
            var beforeAssets = Describe(assets);
            var originalBounds = (RectangleF)Call(canvas, "GetProjectedBounds")!;
            Call(canvas, "SetSite", settings);
            var afterBounds = (RectangleF)Call(canvas, "GetProjectedBounds")!;
            Require((PointF)Call(canvas, "ProjectMapPoint", 400F, 300F)! == before &&
                Describe(assets) == beforeAssets && Math.Abs(afterBounds.Width / originalBounds.Width - 2.5F) < 0.001,
                "Site expansion must enlarge the ground footprint without altering original world positions, equipment dimensions or rotations.");
            using var capture = new Bitmap(canvas.Width, canvas.Height);
            canvas.DrawToBitmap(capture, canvas.ClientRectangle);
            var corners = Field<PointF[]>(canvas, "_projectedCorners");
            var cursor = new Point((int)(corners[0].X + (corners[1].X - corners[0].X) * 0.6F),
                (int)(corners[0].Y + (corners[2].Y - corners[0].Y) * 0.6F));
            var position = (PointF)Call(canvas, "ScreenToMap", cursor)!;
            Require(Math.Abs(position.X - 1.5F) < 0.01 && Math.Abs(position.Y - 1.2F) < 0.01,
                "Expanded ground mouse coordinates must use the original footprint beyond 100 percent.");
            Call(canvas, "SetBuildingParts", Static("FactoryMapBuildingStore", "DefaultRoads"));
            canvas.DrawToBitmap(capture, canvas.ClientRectangle);
            Require(Field<IDictionary>(canvas, "_buildingHits").Count == 6, "All six road segments must be individually selectable.");
            Call(canvas, "SetLayerVisible", "Roads", false);
            canvas.DrawToBitmap(capture, canvas.ClientRectangle);
            Require(Field<IDictionary>(canvas, "_buildingHits").Count == 0, "Hidden roads must not leave selectable ghost surfaces.");
            Call(canvas, "SetLayerVisible", "Roads", true);
            var item = assets[0]!;
            Set(item, "Position", new PointF(3F, 2.5F)); Set(item, "IsPlaced", true);
            Call(canvas, "SetSite", defaults);
            var extent = (SizeF)Call(canvas, "GetSiteSize")!;
            Require(extent.Width >= 4095 && extent.Height >= 1915, "Outlying saved or edited items must remain included in the ground, not disappear outside its fit bounds.");
            var equipmentPath = Path.Combine(directory, "equipment.json");
            Static("FactoryMapLayoutStore", "SaveAssets", assets, equipmentPath);
            var restored = (IList)Static("FactoryMapLayoutStore", "LoadAssets", assets, equipmentPath)!;
            Require((PointF)Get(restored[0]!, "Position") == new PointF(3F, 2.5F), "Equipment beyond 100 percent must persist in complete layouts.");
            Require(Type("FactoryMapCanvas").GetField("_image", BindingFlags.NonPublic | BindingFlags.Instance) == null,
                "The map must no longer depend on a raster background.");
            var shapes = (Dictionary<string, string[]>)Type("FactoryMapItemForm").GetField("Shapes", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            Require(shapes["Generators"].Contains("Caterpillar generator 810 kVA") && shapes["Generators"].Contains("Caterpillar generator 1500 kVA") &&
                shapes["HVAC"].Length >= 13 && shapes["Compressors"].Contains("Atlas Copco compressor 7 bar") &&
                shapes["Production Lines"].Contains("PET bottling line") && shapes["Chillers"].Contains("HP compressor cooling tower 40 bar"),
                "The requested generator, HVAC, production and compressor variants must be explicit library choices.");
            Set(item, "Model", "Caterpillar generator 810 kVA"); Set(item, "Category", "Generators");
            var smaller = (RectangleF)Static("FactoryMapPlacement", "AssetFootprint", item)!;
            Set(item, "Model", "Caterpillar generator 1500 kVA");
            var larger = (RectangleF)Static("FactoryMapPlacement", "AssetFootprint", item)!;
            Require(larger.Width * larger.Height > smaller.Width * smaller.Height * 1.2F, "The 1500 kVA generator must have a visibly larger schematic footprint.");
            foreach (var model in new[] { "PET bottling line", "Can filling line", "Glass bottling line" })
            {
                Set(item, "Model", model); Set(item, "Category", "Production Lines"); Set(item, "Rotation", 0F);
                foreach (var run in new[] { 20F, 200F, 400F })
                {
                    Set(item, "ConveyorLength", run);
                    using var beltHit = new GraphicsPath(FillMode.Winding);
                    Func<float, float, float, PointF> topProjection = (x, y, z) => new(x, y);
                    Static("FactoryMapEquipmentRenderer", "Draw", graphics, item, topProjection, beltHit);
                    foreach (var end in new[] { -run / 2, run / 2 })
                        foreach (var edge in new[] { -3F, 3F })
                            Require(beltHit.PathPoints.Any(point => Math.Abs(point.X - end) < 0.001 && Math.Abs(point.Y - edge) < 0.001),
                                $"The actual integrated belt must have corners at the requested endpoints, independently of protruding machines: {model}, {run}.");
                }
                Set(item, "ConveyorLength", 20F);
                var compactRun = (RectangleF)Static("FactoryMapPlacement", "AssetFootprint", item)!;
                Require(compactRun.Width < 60, "Compact integrated lines must not silently impose a 140-unit conveyor minimum.");
            }
            Console.WriteLine("Factory site expansion, original-coordinate inverse, persistence, road migration/styles and rated equipment checks passed.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static void ValidateSnappingAndNudging()
    {
        var moving = new RectangleF(0, 0, 10, 10);
        var neighbor = new RectangleF(12, 0, 10, 10);
        var delta = (PointF)Static("FactoryMapPlacement", "SnapOffset", moving, new[] { neighbor }, 3F)!;
        Require(delta == new PointF(2, 0), "Nearby equipment edges must snap flush, not move the neighboring item.");
        delta = (PointF)Static("FactoryMapPlacement", "SnapOffset", new RectangleF(3, 0, 10, 10), new[] { neighbor }, 3F)!;
        Require(delta.X == -1, "A shallow overlap must snap back to the neighboring edge.");
        delta = (PointF)Static("FactoryMapPlacement", "SnapOffset", moving, new[] { new RectangleF(14, 0, 10, 10) }, 3F)!;
        Require(delta == PointF.Empty, "Snapping must not reach beyond its exact tolerance.");
        delta = (PointF)Static("FactoryMapPlacement", "SnapOffset", moving, new[] { new RectangleF(12, 20, 10, 10) }, 3F)!;
        Require(delta == PointF.Empty, "Unrelated distant rows must not magnetize each other.");
        using var view = new FactoryMapView(new UserSession { RoleName = "Administrator" }, Array.Empty<MeterSnapshot>(), null!);
        view.Size = new Size(1440, 900); view.CreateControl();
        var canvas = Field<Control>(view, "_canvas");
        var assets = Field<IList>(view, "_assets");
        Call(view, "ToggleEditMode", null, EventArgs.Empty);
        var item = assets[0]!;
        Call(canvas, "SelectAsset", item);
        var origin = (PointF)Get(item, "Position");
        var pan = Field<PointF>(canvas, "_pan");
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Right));
        var next = (PointF)Get(item, "Position");
        Require(Math.Abs((next.X - origin.X) * 1365 - 1) < 0.0001 && next.Y == origin.Y && Field<PointF>(canvas, "_pan") == pan,
            "An arrow must nudge selected equipment by exactly one plan unit without moving the camera.");
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Shift | Keys.Down));
        Require(Math.Abs((((PointF)Get(item, "Position")).Y - origin.Y) * 766 - 5) < 0.0001, "Shift-arrow must nudge five plan units.");
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.Left));
        Require(Field<PointF>(canvas, "_pan").X == pan.X + 30, "Ctrl-arrow must remain available for camera movement with a selected item.");
        Call(view, "DuplicateMapItem");
        var copy = assets[assets.Count - 1]!;
        Require((string)Get(copy, "Model") == (string)Get(item, "Model") && (string)Get(copy, "EquipmentId") == "" &&
            (string)Get(copy, "Id") != (string)Get(item, "Id"), "Duplicate must make an independent visual item, not invent another connected device.");
        Call(view, "CancelLayout", null, EventArgs.Empty);
        Call(view, "BeginBuildingEdit", null, EventArgs.Empty);
        foreach (var kind in new[] { "Wall", "Floor", "Stairs", "Door", "Road" })
        {
            var part = Part(kind);
            var others = Parts(part);
            Call(canvas, "SetBuildingParts", others);
            Call(canvas, "SelectBuildingPart", part);
            var before = (PointF)Get(part, "Position");
            var currentPan = Field<PointF>(canvas, "_pan");
            Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Left));
            Require(Math.Abs((((PointF)Get(part, "Position")).X - before.X) * 1365 + 1) < 0.0001 && Field<PointF>(canvas, "_pan") == currentPan,
                $"Keyboard precision movement must cover every building element: {kind}.");
        }
        var wall = Part();
        var door = Part("Door");
        Set(door, "ParentWallId", Get(wall, "Id"));
        Call(canvas, "SetBuildingParts", Parts(wall, door));
        Call(canvas, "SelectBuildingPart", door);
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Right));
        Require((float)Get(door, "Offset") == 1F, "Attached openings must nudge along their wall without detaching.");
        Set(door, "Offset", 31F);
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Right));
        Require((float)Get(door, "Offset") == 31F, "Invalid opening nudges must be rejected instead of escaping the wall.");

        var a = Part(); var b = Part();
        Set(a, "X", 0.4F); Set(b, "X", 0.4F + 82F / 1365);
        Set(a, "Y", 0.5F); Set(b, "Y", 0.5F);
        var unchanged = Describe(b);
        Call(canvas, "SetBuildingParts", Parts(a, b));
        Call(canvas, "SnapBuilding", a);
        var endA = (PointF)Static("FactoryMapBuildingStore", "WorldPoint", a, 40F, 0F)!;
        var startB = (PointF)Static("FactoryMapBuildingStore", "WorldPoint", b, -40F, 0F)!;
        Require(Math.Abs(endA.X - startB.X) < 0.001 && Math.Abs(endA.Y - startB.Y) < 0.001 && Describe(b) == unchanged,
            "Wall ends must snap exactly without rearranging other walls.");
        Set(a, "Kind", "Floor"); Set(a, "Style", "Rectangle"); Set(a, "Depth", 60F);
        Set(b, "Kind", "Floor"); Set(b, "Style", "Rectangle"); Set(b, "Depth", 60F);
        Set(a, "X", 0.4F);
        Call(canvas, "SnapBuilding", a);
        var boundsA = (RectangleF)Static("FactoryMapPlacement", "BuildingFootprint", a)!;
        var boundsB = (RectangleF)Static("FactoryMapPlacement", "BuildingFootprint", b)!;
        Require(Math.Abs(boundsA.Right - boundsB.Left) < 0.001, "Room floor edges must snap flush.");
        Set(canvas, "SnapEnabled", false);
        Set(a, "X", 0.4F);
        var unSnapped = (PointF)Get(a, "Position");
        Call(canvas, "SnapBuilding", a);
        Require((PointF)Get(a, "Position") == unSnapped, "The snapping switch must disable automatic edge placement.");
        Set(canvas, "SnapEnabled", true);
        var firstAsset = Call(assets[0]!, "Clone")!;
        var secondAsset = Call(assets[0]!, "Clone")!;
        Set(firstAsset, "Id", "SNAP-A"); Set(secondAsset, "Id", "SNAP-B");
        foreach (var equipment in new[] { firstAsset, secondAsset })
        {
            Set(equipment, "Model", "Conveyor"); Set(equipment, "Category", "Production Lines");
            Set(equipment, "Rotation", 90F); Set(equipment, "ScaleX", 2F);
            Set(equipment, "Position", new PointF(0.5F, 0.5F));
        }
        var localBounds = (RectangleF)Call(canvas, "AssetBounds", firstAsset, new PointF(0.5F, 0.5F))!;
        Set(secondAsset, "Position", new PointF(0.5F + (localBounds.Width + 2) / 1365, 0.5F));
        assets.Clear(); assets.Add(firstAsset); assets.Add(secondAsset);
        var neighborBefore = Describe(secondAsset);
        var snapped = (PointF)Call(canvas, "SnapAsset", firstAsset, new PointF(0.5F, 0.5F))!;
        var actualBounds = (RectangleF)Call(canvas, "AssetBounds", firstAsset, snapped)!;
        var neighborBounds = (RectangleF)Call(canvas, "AssetBounds", secondAsset, Get(secondAsset, "Position"))!;
        Require(Math.Abs(actualBounds.Right - neighborBounds.Left) < 0.001 && Describe(secondAsset) == neighborBefore,
            "Equipment snapping must use the actual rotated, resized rendered footprint and must never move its neighbor.");
    }

    private static void ValidateRooms()
    {
        var wall1 = Part(); var wall2 = Part(); var wall3 = Part(); var floor = Part("Floor");
        Set(wall1, "Name", "Wall 1"); Set(wall2, "Name", "Wall 2"); Set(wall3, "Name", "Wall 3"); Set(floor, "Name", "Floor");
        var parts = Parts(wall1, wall2, wall3, floor);
        using var create = (Form)Activator.CreateInstance(Type("FactoryMapRoomForm"), new object[] { parts, wall1 })!;
        var members = Field<CheckedListBox>(create, "_members");
        for (var index = 0; index < members.Items.Count; index++) members.SetItemChecked(index, members.Items[index] != wall3);
        Field<TextBox>(create, "_name").Text = "Water treatment room";
        Call(create, "ApplyRoom", null, EventArgs.Empty);
        Require(create.DialogResult == DialogResult.OK, "A floor plus two walls must be eligible to create a named room.");
        var result = (IList)Get(create, "Parts");
        var room = result.Cast<object>().First(part => (string)Get(part, "RoomId") != "");
        var roomId = (string)Get(room, "RoomId");
        Require(result.Cast<object>().Count(part => (string)Get(part, "RoomId") == roomId) == 3 && (string)Get(wall1, "RoomId") == "",
            "Room Apply must group only checked elements in a copy until the building transaction applies it.");
        using var rename = (Form)Activator.CreateInstance(Type("FactoryMapRoomForm"), new object[] { result, room })!;
        Field<TextBox>(rename, "_name").Text = "Renamed filtration room";
        Call(rename, "ApplyRoom", null, EventArgs.Empty);
        var renamed = (IList)Get(rename, "Parts");
        Require(renamed.Cast<object>().Where(part => (string)Get(part, "RoomId") == roomId).All(part => (string)Get(part, "RoomName") == "Renamed filtration room"),
            "Room renaming must update every room member without changing its stable group identity.");
        using var threeWalls = (Form)Activator.CreateInstance(Type("FactoryMapRoomForm"), new object[] { parts, wall1 })!;
        var wallMembers = Field<CheckedListBox>(threeWalls, "_members");
        for (var index = 0; index < wallMembers.Items.Count; index++) wallMembers.SetItemChecked(index, (string)Get(wallMembers.Items[index], "Kind") == "Wall");
        Call(threeWalls, "ApplyRoom", null, EventArgs.Empty);
        Require(threeWalls.DialogResult == DialogResult.OK, "Three walls must create a room even without a floor.");
        using var invalid = (Form)Activator.CreateInstance(Type("FactoryMapRoomForm"), new object[] { parts, wall1 })!;
        Call(invalid, "ApplyRoom", null, EventArgs.Empty);
        Require(invalid.DialogResult != DialogResult.OK && Field<Label>(invalid, "_message").Text != "", "Insufficient room structure must produce an explicit validation message.");
        var directory = Path.Combine(Path.GetTempPath(), "eoems-rooms-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "building.json");
            Static("FactoryMapBuildingStore", "Save", renamed, path);
            var loaded = Static("FactoryMapBuildingStore", "Load", path)!;
            Require(Describe(renamed) == Describe(loaded), "Named room groups must survive Save/restart.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        using var view = new FactoryMapView(new UserSession { RoleName = "Administrator" }, Array.Empty<MeterSnapshot>(), null!);
        var building = Field<IList>(view, "_building");
        var original = Describe(building);
        Call(view, "BeginBuildingEdit", null, EventArgs.Empty);
        foreach (var part in building.Cast<object>().Take(3))
        {
            Set(part, "RoomId", roomId);
            Set(part, "RoomName", "Unsaved room");
        }
        Call(view, "CancelBuilding");
        Require(Describe(building) == original, "Building Cancel must restore room membership and names, not only geometry.");
    }

    private static void ValidateRotationAndLibrary()
    {
        var expected = new[] { new PointF(8, 3), new PointF(-3, 8), new PointF(-8, -3), new PointF(3, -8) };
        for (var index = 0; index < 4; index++)
        {
            var point = (PointF)Static("FactoryMapPlacement", "RotatePoint", 8F, 3F, index * 90F)!;
            Require(Math.Abs(point.X - expected[index].X) < 0.00001 && Math.Abs(point.Y - expected[index].Y) < 0.00001,
                "Cardinal rotation must operate on X/Y only, with no negative height scale or vertical inversion.");
        }
        var shapes = (Dictionary<string, string[]>)Type("FactoryMapItemForm").GetField("Shapes", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Require(shapes["Water Systems"].Count(name => name.StartsWith("Ultrafiltration")) >= 2 &&
            shapes["Water Systems"].Contains("Stainless process vat") && shapes["Filler Room"].Contains("PET bottle blower"),
            "The library must cover multiple UF models, water systems, stainless vessels and filler-room equipment.");
        var assets = (IList)typeof(FactoryMapView).GetMethod("CreateAssets", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        var seed = assets[0]!;
        using var bitmap = new Bitmap(500, 500);
        using var graphics = Graphics.FromImage(bitmap);
        foreach (var category in shapes)
            foreach (var model in category.Value)
                foreach (var heading in new[] { 0, 90, 180, 270 })
                {
                    var item = Call(seed, "Clone")!;
                    Set(item, "Category", category.Key); Set(item, "Model", model); Set(item, "Rotation", (float)heading);
                    Func<float, float, float, PointF> projection = (x, y, z) =>
                    {
                        var plan = (PointF)Static("FactoryMapPlacement", "RotatePoint", x, y, (float)heading)!;
                        return new PointF(250 + (plan.X - plan.Y) * 1.4F, 300 + (plan.X + plan.Y) * 0.7F - z * 1.7F);
                    };
                    using var hit = new GraphicsPath(FillMode.Winding);
                    Static("FactoryMapEquipmentRenderer", "Draw", graphics, item, projection, hit);
                    Require(hit.PointCount >= 20 && hit.GetBounds().Height > 0.05F, $"Every model must remain visible and selectable at {heading} degrees, including rear views with hidden front panels and small sensors: {model}.");
                }
        var stairs = (Dictionary<string, string[]>)Type("FactoryMapBuildingStore").GetField("Styles", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        foreach (var style in stairs["Stairs"])
            foreach (var heading in new[] { 0F, 90F, 180F, 270F })
            {
                var part = Part("Stairs");
                Set(part, "Style", style); Set(part, "Depth", 50F); Set(part, "Rotation", heading);
                Set(part, "X", 0F); Set(part, "Y", 0F);
                using var hit = new GraphicsPath(FillMode.Winding);
                Func<float, float, float, PointF> projection = (x, y, z) => new(250 + x - y, 250 + (x + y) * 0.5F - z);
                Static("FactoryMapBuildingRenderer", "Draw", graphics, part, Parts(part), 40, projection, hit);
                Require(hit.PointCount > 150 && hit.GetBounds().Width > 20, "Every stair shape and direction must produce detailed upright selectable steps.");
            }
    }

    public static void Render(string directory)
    {
        var assets = (IList)typeof(FactoryMapView).GetMethod("CreateAssets", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        var catalog = typeof(FactoryMapView).GetMethod("EquipmentCatalog", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        var models = new[] { ("Generators", "Caterpillar generator 810 kVA"), ("Generators", "Caterpillar generator 1500 kVA"),
            ("HVAC", "Outdoor AC 20 HP"), ("Chillers", "Hansa chiller"), ("Chillers", "Hansa chiller cooling tower"),
            ("Water Systems", "Ultrafiltration twin rack"), ("Water Systems", "Stainless water tank"),
            ("Filler Room", "PET bottle blower"), ("UPS", "Vertiv EXL 500 kVA"), ("UPS", "Vertiv NXL 800 kVA"),
            ("Compressors", "HP compressor 40 bar with control and power panels"), ("Compressors", "Atlas Copco compressor 7 bar"),
            ("Electrical Panels", "Eaton RMU switchgear"), ("Production Lines", "PET bottling line") };
        using var bitmap = new Bitmap(1440, models.Length * 260);
        using var graphics = Graphics.FromImage(bitmap);
        using var font = new Font("Segoe UI", 10, FontStyle.Bold);
        graphics.Clear(Color.FromArgb(246, 249, 252));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        for (var row = 0; row < models.Length; row++)
            for (var col = 0; col < 4; col++)
            {
                var item = Call(assets[0]!, "Clone")!;
                Set(item, "Category", models[row].Item1); Set(item, "Model", models[row].Item2); Set(item, "Rotation", col * 90F);
                using var dialog = (Form)Activator.CreateInstance(Type("FactoryMapItemForm"), new[] { item, catalog })!;
                var preview = Field<Control>(dialog, "_preview");
                preview.Dock = DockStyle.None;
                preview.Size = new Size(350, 235);
                preview.CreateControl();
                using var capture = new Bitmap(350, 235);
                preview.DrawToBitmap(capture, new Rectangle(0, 0, 350, 235));
                graphics.DrawImage(capture, col * 360, row * 260 + 23);
                graphics.DrawString($"{models[row].Item2} / {col * 90} deg", font, Brushes.Black, col * 360 + 5, row * 260 + 3);
            }
        bitmap.Save(Path.Combine(directory, "FactoryMap-FourHeadings.png"));
        using var stairImage = new Bitmap(1440, 5 * 230);
        using var stairGraphics = Graphics.FromImage(stairImage);
        stairGraphics.Clear(Color.FromArgb(246, 249, 252));
        stairGraphics.SmoothingMode = SmoothingMode.AntiAlias;
        var styles = ((Dictionary<string, string[]>)Type("FactoryMapBuildingStore").GetField("Styles", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)["Stairs"];
        for (var row = 0; row < styles.Length; row++)
            for (var col = 0; col < 4; col++)
            {
                var part = Part("Stairs");
                Set(part, "X", 0F); Set(part, "Y", 0F); Set(part, "Depth", 50F); Set(part, "Rotation", col * 90F); Set(part, "Style", styles[row]);
                using var hit = new GraphicsPath(FillMode.Winding);
                Func<float, float, float, PointF> projection = (x, y, z) => new(col * 360 + 180 + (x - y) * 1.6F, row * 230 + 150 + (x + y) * 0.8F - z * 1.8F);
                Static("FactoryMapBuildingRenderer", "Draw", stairGraphics, part, Parts(part), 40, projection, hit);
                stairGraphics.DrawString($"{styles[row]} / {col * 90} deg", font, Brushes.Black, col * 360 + 5, row * 230 + 3);
            }
        stairImage.Save(Path.Combine(directory, "FactoryMap-StairHeadings.png"));
        FactoryMapRefinementsValidation.Render(directory);
    }
}
