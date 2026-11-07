using System.Collections;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class FactoryDrawingValidation
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type Type(string name) => typeof(FactoryMapView).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5." + name)!;
    private static object New(string name) => Activator.CreateInstance(Type(name))!;
    private static T Get<T>(object target, string name) => (T)target.GetType().GetProperty(name, Instance)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Instance)!.Invoke(target, args);
    private static object? Static(string type, string name, params object?[] args) => Type(type).GetMethod(name)!.Invoke(null, args);
    private static void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); }
    private static IList List(string type, params object[] values)
    {
        var result = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type(type)))!;
        foreach (var value in values) result.Add(value);
        return result;
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid drawing/floor configuration was accepted.");
    }

    public static void Run()
    {
        ValidateMounting();
        var draft = new MonitoringDevice { Id = "draft", Name = "Drawing detector", DrawingTag = "SD-01",
            Kind = "Fire", Commissioned = false };
        var registry = new MonitoringDeviceRegistry { Devices = new() { draft } };
        registry.Validate();
        draft.Commissioned = true;
        Reject(registry.Validate);
        draft.Commissioned = false;
        draft.DrawingTag = "";
        Reject(registry.Validate);
        draft.DrawingTag = "SD-01";
        draft.PanelId = "1"; draft.Loop = "1"; draft.Address = "1"; draft.EventAddress = "L1/1";
        var asset = New("FactoryMapAsset");
        Set(asset, "Id", "a"); Set(asset, "Name", "Detector"); Set(asset, "Category", "Fire System");
        Set(asset, "Model", "Zeta smoke detector"); Set(asset, "MonitoringDeviceId", "draft");
        Set(asset, "Position", new PointF(.4f, .4f)); Set(asset, "IsPlaced", true); Set(asset, "RoomId", "r");
        var live = new Dictionary<string, MonitoringSample> { ["draft"] = new() { DeviceId = "draft",
            Verified = true, TimestampUtc = DateTime.UtcNow, Signal = MapSignal.Fire } };
        var history = new Dictionary<string, IReadOnlyList<FireMapEventEvidence>> {
            ["1"] = new[] { new FireMapEventEvidence(new ZetaFireEvent { DeviceAddress = "L1/1", EventType = "FIRE" }) } };
        using var canvas = (Control)Activator.CreateInstance(Type("FactoryMapCanvas"), List("FactoryMapAsset", asset))!;
        Call(canvas, "SetMonitoring", registry, history, live);
        Check(Get<MapSignal>(Call(canvas, "Indication", asset)!, "Signal") == MapSignal.Unknown,
            "Draft fire records must ignore injected live and historical matches.");
        var wall = New("FactoryBuildingPart");
        Set(wall, "Id", "wall"); Set(wall, "RoomId", "r"); Set(wall, "RoomName", "Room");
        var parts = List("FactoryBuildingPart", wall);
        Call(canvas, "SetBuildingParts", parts);
        Call(canvas, "SetMonitoring", registry, history, live);
        Check(!(bool)Call(canvas, "RoomHasFire", "r")!, "Draft fire sources cannot ignite a room.");
        Set(canvas, "EditMode", true);
        Check((bool)Call(canvas, "ShouldShowAsset", asset)!, "Active-floor draft is visible for editing.");
        Call(canvas, "SetLevel", "Basement");
        Check(!(bool)Call(canvas, "ShouldShowAsset", asset)!, "Other floors remain hidden even while editing.");
        var door = New("FactoryBuildingPart");
        Set(door, "Id", "door"); Set(door, "Kind", "Door"); Set(door, "Style", "Single leaf");
        Set(door, "Material", "Steel"); Set(door, "Length", 5f); Set(door, "ParentWallId", "wall");
        Set(door, "Level", "Basement");
        Reject(() => Static("FactoryMapBuildingStore", "Validate", List("FactoryBuildingPart", wall, door)));
        Set(door, "ParentWallId", ""); Set(door, "RoomId", "r"); Set(door, "RoomName", "Room");
        Reject(() => Static("FactoryMapBuildingStore", "Validate", List("FactoryBuildingPart", wall, door)));
        var temp = Path.Combine(Path.GetTempPath(), "factory-levels-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var path = Path.Combine(temp, "assets.json");
            Set(asset, "Level", "Upper mezzanine");
            Static("FactoryMapLayoutStore", "SaveAssets", List("FactoryMapAsset", asset), path);
            var loaded = (IList)Static("FactoryMapLayoutStore", "LoadAssets", List("FactoryMapAsset"), path)!;
            Check(Get<string>(loaded[0]!, "Level") == "Upper mezzanine", "Asset level survives roundtrip.");
            var legacy = JsonSerializer.SerializeToNode(JsonDocument.Parse(File.ReadAllText(path)).RootElement)!;
            legacy["Assets"]![0]!.AsObject().Remove("Level");
            File.WriteAllText(path, legacy.ToJsonString());
            loaded = (IList)Static("FactoryMapLayoutStore", "LoadAssets", List("FactoryMapAsset"), path)!;
            Check(Get<string>(loaded[0]!, "Level") == "Ground", "Existing maps default to Ground.");
            path = Path.Combine(temp, "building.json");
            Set(wall, "Level", "Mezzanine");
            Static("FactoryMapBuildingStore", "Save", parts, path);
            loaded = (IList)Static("FactoryMapBuildingStore", "Load", path)!;
            Check(Get<string>(loaded[0]!, "Level") == "Mezzanine", "Building level survives roundtrip.");
            legacy = JsonSerializer.SerializeToNode(JsonDocument.Parse(File.ReadAllText(path)).RootElement)!;
            legacy["Parts"]![0]!.AsObject().Remove("Level");
            File.WriteAllText(path, legacy.ToJsonString());
            loaded = (IList)Static("FactoryMapBuildingStore", "Load", path)!;
            Check(Get<string>(loaded[0]!, "Level") == "Ground", "Existing building files default to Ground.");
        }
        finally { Directory.Delete(temp, true); }
        canvas.CreateControl();
        Call(canvas, "SetPageActive", true);
        canvas.Invalidate();
        var timer = (System.Windows.Forms.Timer)canvas.GetType().GetField("_presentationTimer", Instance)!.GetValue(canvas)!;
        Check(timer.Enabled, "An active canvas invalidation schedules bounded presentation.");
        Call(canvas, "SetPageActive", false);
        Check(!timer.Enabled, "Inactive maps stop pending presentation.");
        Console.WriteLine("Drawing drafts, floor isolation, cross-floor constraints and legacy/roundtrip checks passed.");
    }

    private static void ValidateMounting()
    {
        var detector = New("FactoryMapAsset");
        Set(detector, "Id", "mount"); Set(detector, "Name", "Mount test");
        Set(detector, "Category", "Fire System"); Set(detector, "Model", "Zeta smoke detector");
        Set(detector, "RoomId", "room"); Set(detector, "Position", new PointF(.48f, .52f));
        using var canvas = (Control)Activator.CreateInstance(Type("FactoryMapCanvas"), List("FactoryMapAsset", detector))!;
        var wall = New("FactoryBuildingPart");
        Set(wall, "Id", "mount-wall"); Set(wall, "RoomId", "room"); Set(wall, "RoomName", "Test room");
        Set(wall, "Length", 100f); Set(wall, "Depth", 2f); Set(wall, "HeightMeters", 4f);
        Set(wall, "BaseMeters", .25f); Set(wall, "Rotation", 90f);
        var wrongFloor = Call(wall, "Clone")!;
        Set(wrongFloor, "Id", "other-floor"); Set(wrongFloor, "Level", "Basement");
        Set(wrongFloor, "RoomId", "basement-room");
        var wrongRoom = Call(wall, "Clone")!;
        Set(wrongRoom, "Id", "other-room"); Set(wrongRoom, "RoomId", "other");
        Set(wrongRoom, "X", .48f);
        Call(canvas, "SetBuildingParts", List("FactoryBuildingPart", wall, wrongFloor, wrongRoom));
        foreach (var model in new[] { "Smoke detector", "Zeta smoke detector", "Zeta heat detector", "Zeta multi-sensor detector" })
        {
            Set(detector, "Model", model);
            Check((float)Call(canvas, "MountHeight", detector)! == 4.25f, "Every ceiling detector follows its own room ceiling including wall base.");
            Set(wall, "HeightMeters", 6f);
            Check((float)Call(canvas, "MountHeight", detector)! == 6.25f, "Ceiling devices follow edited wall heights.");
            Set(wall, "HeightMeters", 4f);
            using var image = new Bitmap(500, 300);
            using var graphics = Graphics.FromImage(image);
            using var hit = new GraphicsPath();
            float minZ = 0, maxZ = 0;
            Func<float, float, float, PointF> project = (x, y, z) =>
                new(250 + (x - .6f * y) * 10, 100 + (.5f * y - z) * 10);
            Action<float, float, float> measure = (x, y, z) => { minZ = Math.Min(minZ, z); maxZ = Math.Max(maxZ, z); };
            Type("FactoryMapEquipmentRenderer").GetMethod("DrawCore", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { graphics, detector, project, hit, measure, true });
            Check(minZ < 0 && maxZ <= .0001f, "The complete detector hangs below, not above, the ceiling mounting plane.");
            Check((float)Static("FactoryMapMonitoring", "DefaultMountHeight", model)! > 0, "New ceiling devices cannot default to floor height.");
            Set(detector, "RoomId", "");
            Check((float)Call(canvas, "MountHeight", detector)! == 3f, "Unassigned detectors use an explicit positive provisional ceiling height.");
            Set(detector, "RoomId", "room");
        }
        foreach (var model in new[] { "Zeta manual call point", "Zeta sounder", "Zeta sounder beacon", "Zeta siren",
            "Zeta beam detector", "Zeta fire panel", "Zeta repeater panel", "Zeta interface module", "Zeta isolator module" })
        {
            Set(detector, "Model", model);
            Check(ReferenceEquals(Call(canvas, "MountWall", detector), wall), "Wall devices must use their own room and level, not a closer other-room/floor wall.");
            var mounted = Call(canvas, "MountedAsset", detector)!;
            var position = Get<PointF>(mounted, "Position");
            var local = (PointF)Static("FactoryMapBuildingStore", "LocalPoint", wall, new PointF(position.X * 1365, position.Y * 766))!;
            Check(Math.Abs(local.X) <= 50.01f && local.Y > 1, "Wall enclosure must align with the wall's length and sit against its room-facing surface.");
            Check(Get<float>(mounted, "Rotation") == 90 && Get<PointF>(detector, "Position") == new PointF(.48f, .52f),
                "Wall rendering follows wall orientation without changing drawing coordinates.");
            var height = (float)Call(canvas, "MountHeight", detector)!;
            Check(height > .25f && height < 4.25f, "Wall devices are elevated above floor and remain below ceiling.");
            Set(wall, "Rotation", 45f);
            Check(Get<float>(Call(canvas, "MountedAsset", detector)!, "Rotation") % 180 == 45,
                "Wall devices follow a wall rotation without editing the saved drawing anchor.");
            Set(wall, "Rotation", 90f);
            Set(detector, "BaseHeightMeters", 99f);
            Check((float)Call(canvas, "MountHeight", detector)! < 4.25f, "Oversized configured mounting height cannot put wall equipment above the room.");
            Set(detector, "BaseHeightMeters", 0f);
        }
        Set(detector, "RoomId", "missing");
        Check(Call(canvas, "MountWall", detector) == null &&
            ((string)Call(canvas, "MountDescription", detector)!).Contains("NO COMPATIBLE WALL"),
            "Missing room walls must be disclosed rather than silently borrowing another room wall.");
        Set(detector, "Model", "Zeta smoke detector"); Set(detector, "RoomId", "");
        Call(canvas, "SetBuildingParts", List("FactoryBuildingPart"));
        canvas.Size = new Size(1000, 700); Set(canvas, "EditMode", true);
        var assets = (IList)canvas.GetType().GetField("_assets", Instance)!.GetValue(canvas)!;
        var overviewWidth = 0;
        foreach (var zoom in new[] { 1f, 30f })
        {
            canvas.GetType().GetField("_zoom", Instance)!.SetValue(canvas, zoom);
            var view = (RectangleF)Call(canvas, "GetProjectedBounds")!;
            var point = Get<PointF>(detector, "Position");
            var projected = (PointF)Call(canvas, "ProjectMapPoint", point.X * 1365, point.Y * 766)!;
            var viewScale = Math.Min((canvas.Width - 32) / view.Width, (canvas.Height - 32) / view.Height) * zoom;
            var elevation = (float)canvas.GetType().GetField("_elevation", Instance)!.GetValue(canvas)!;
            var mount = (float)Call(canvas, "MountHeight", detector)!;
            canvas.GetType().GetField("_pan", Instance)!.SetValue(canvas,
                new PointF(((view.Left + view.Right) / 2 - projected.X) * viewScale,
                    ((view.Top + view.Bottom) / 2 - projected.Y + mount * 8 * MathF.Cos(elevation)) * viewScale));
            using var actual = new Bitmap(canvas.Width, canvas.Height);
            using var reference = new Bitmap(canvas.Width, canvas.Height);
            using var actualGraphics = Graphics.FromImage(actual);
            using var referenceGraphics = Graphics.FromImage(reference);
            Call(canvas, "OnPaint", new PaintEventArgs(actualGraphics, canvas.ClientRectangle));
            var hits = (IDictionary)canvas.GetType().GetField("_hitAreas", Instance)!.GetValue(canvas)!;
            Check(((GraphicsPath)hits[detector]!).GetBounds().Width >= 12, "Small device retains an invisible accessible hit target.");
            assets.Clear();
            Call(canvas, "OnPaint", new PaintEventArgs(referenceGraphics, canvas.ClientRectangle));
            assets.Add(detector);
            int left = actual.Width, right = -1;
            for (var y = 0; y < actual.Height; y++)
            for (var x = 0; x < actual.Width; x++)
            {
                if (actual.GetPixel(x, y).ToArgb() == reference.GetPixel(x, y).ToArgb()) continue;
                left = Math.Min(left, x); right = Math.Max(right, x);
            }
            var width = right - left + 1;
            var fit = (float)canvas.GetType().GetField("_fitScale", Instance)!.GetValue(canvas)!;
            var physical = ((float Length, float Width, float Height))Static("FactoryMapEquipmentSizes", "Nominal", "Zeta smoke detector")!;
            var factor = (float)Type("FactoryMapCanvas").GetMethod("FirePresentationFactor",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new[] { detector })!;
            Check(factor == 1.25f, "Fire equipment must receive exactly the approved 25% visual enlargement.");
            var maximumWidth = (physical.Length + physical.Width) * 8 * fit * zoom * factor + 4;
            Check(width > 0 && width <= maximumWidth,
                $"Rendered detector must use metre scale plus 25%, not a minimum-size icon: {zoom}x, {width}px vs bound {maximumWidth:F1}px.");
            if (zoom == 1) { overviewWidth = width; Check(width < 8, "Overview detector must not be enlarged into a 16-pixel marker/model."); }
            else Check(width > overviewWidth, "Zoom enlarges the physical model naturally.");
        }
        Set(detector, "Category", "Sensors & Controls");
        Check((float)Type("FactoryMapCanvas").GetMethod("FirePresentationFactor",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new[] { detector })! == 1f,
            "The fire visibility adjustment must not enlarge other equipment.");
        Set(detector, "Category", "Fire System");
        Console.WriteLine("Ceiling underside, elevated wall alignment/orientation, floor isolation and actual metre-scale fire-model checks passed.");
    }

    public static void RunPrepared(string prepared, string previous)
    {
        Run();
        var assets = (IList)Static("FactoryMapLayoutStore", "LoadAssets", List("FactoryMapAsset"), Path.Combine(prepared, "factory-map-layout.json"))!;
        var parts = (IList)Static("FactoryMapBuildingStore", "Load", Path.Combine(prepared, "factory-map-building.json"))!;
        var registry = MonitoringDeviceStore.Load(Path.Combine(prepared, "monitoring-devices.json"));
        var drawing = registry.Devices.Where(d => d.Id.StartsWith("DRAW-")).ToArray();
        Check(drawing.Length == 199, "Every one of the 199 actual drawing symbols must be registered.");
        Check(drawing.All(d => !d.Commissioned && d.PanelId == "" && d.Loop == "" && d.Address == "" && d.EventAddress == ""),
            "No drawing tags may be invented as commissioned addresses.");
        Check(drawing.Count(d => d.Model == "Zeta smoke detector") == 130, "Retain the actual 130 smoke symbols, not the inconsistent legend total.");
        Check(drawing.Count(d => d.DrawingTag == "SD-118-P4") == 2, "Keep duplicate drawing tags as two separately identified locations.");
        Check(assets.Cast<object>().Select(a => Get<string>(a, "Level")).Distinct().Count() == 4, "All four drawing levels are represented.");
        var warehouse = parts.Cast<object>().Single(p => Get<string>(p, "Id") == "DRAW-PW-F1");
        Check(Math.Abs(Get<float>(warehouse, "Length") / 8 - 90) < .1 &&
              Math.Abs(Get<float>(warehouse, "Depth") / 8 - 18) < .5,
            "Ground warehouse geometry must reproduce the drawing's 90 m by 18 m dimensional reference.");
        var originals = (IList)Static("FactoryMapLayoutStore", "LoadAssets", List("FactoryMapAsset"), Path.Combine(previous, "factory-map-layout.json"))!;
        foreach (var old in originals.Cast<object>())
        {
            var current = assets.Cast<object>().Single(a => Get<string>(a, "Id") == Get<string>(old, "Id"));
            foreach (var link in new[] { "EquipmentId", "MeterSourceId", "MonitoringDeviceId", "ParentUpsId", "CameraIpAddress", "BodyColor" })
                Check(Get<string>(old, link) == Get<string>(current, link), "Original link/color changed: " + link);
        }
        var reviewPath = Path.Combine(prepared, "factory-map-drawing-review.json");
        if (File.Exists(reviewPath))
        {
            using var reviewDocument = JsonDocument.Parse(File.ReadAllText(reviewPath));
            if (reviewDocument.RootElement.TryGetProperty("WallReferenceRevision", out _))
                ValidateWallReference(prepared, previous, assets, parts);
            if (reviewDocument.RootElement.TryGetProperty("SurfaceFinishRevision", out _))
            {
                var roomNames = new[] { "PET-1", "PET-2", "PET-3", "PET-4", "PET-5", "PET-6", "Canline-1" };
                var productionFloors = parts.Cast<object>().Where(p => Get<string>(p, "Level") == "Ground" &&
                    Get<string>(p, "Kind") == "Floor" && roomNames.Contains(Get<string>(p, "RoomName"))).ToArray();
                Check(productionFloors.Length == 7 && productionFloors.All(p => Get<string>(p, "BodyColor") == "#B8BDC2"),
                    "All six PET rooms and Canline must retain the requested grey finish.");
                var apron = parts.Cast<object>().Single(p => Get<string>(p, "Id") == "DRAW-PW-F1");
                Check(Get<string>(apron, "Material") == "Asphalt" && Get<string>(apron, "BodyColor") == "#454B50",
                    "The exterior service apron must use the asphalt finish.");
                var site = Static("FactoryMapSiteStore", "Load", Path.Combine(prepared, "factory-map-site.json"))!;
                Check(Get<string>(site, "GroundColor") == "#454B50", "Exterior site ground must use the asphalt color.");
                Check(File.ReadAllBytes(Path.Combine(prepared, "factory-map-layout.json")).SequenceEqual(
                    File.ReadAllBytes(Path.Combine(previous, "factory-map-layout.json"))),
                    "Surface finish updates must not rewrite equipment or their links/positions.");
                Console.WriteLine("Seven grey production floors, asphalt exterior/apron and byte-identical equipment layout verified.");
            }
            if (reviewDocument.RootElement.TryGetProperty("ExteriorOnlyFinish", out _))
            {
                var previousBuilding = System.Text.Json.Nodes.JsonNode.Parse(
                    File.ReadAllText(Path.Combine(previous, "factory-map-building.json")))!;
                var currentBuilding = System.Text.Json.Nodes.JsonNode.Parse(
                    File.ReadAllText(Path.Combine(prepared, "factory-map-building.json")))!;
                foreach (var old in previousBuilding["Parts"]!.AsArray())
                {
                    var current = currentBuilding["Parts"]!.AsArray().Single(p =>
                        p!["Id"]!.GetValue<string>() == old!["Id"]!.GetValue<string>());
                    var roadRounding = reviewDocument.RootElement.TryGetProperty("RoundedDriveways", out _) &&
                        old!["Id"]!.GetValue<string>().StartsWith("REF-ROAD-");
                    var comparison = current!.DeepClone();
                    var palette = reviewDocument.RootElement.TryGetProperty("ReferencePalette", out var referencePalette);
                    if (palette && old!["Kind"]!.GetValue<string>() == "Floor" && old["Level"]!.GetValue<string>() == "Ground")
                    {
                        var declared = referencePalette.GetProperty("Floors").EnumerateArray().Where(entry =>
                            entry.GetProperty("Id").GetString() == old["Id"]!.GetValue<string>()).ToArray();
                        if (declared.Length == 1)
                        {
                            Check(current["BodyColor"]!.GetValue<string>() == declared[0].GetProperty("After").GetString(),
                                "Ground-floor reference palette must match its declared color.");
                            comparison["BodyColor"] = old["BodyColor"]!.DeepClone();
                            if (declared[0].TryGetProperty("MaterialAfter", out var material))
                            {
                                Check(current["Material"]!.GetValue<string>() == material.GetString(),
                                    "Declared room floor finish must retain its requested material.");
                                comparison["Material"] = old["Material"]!.DeepClone();
                            }
                        }
                    }
                    Check((roadRounding && !palette) || System.Text.Json.Nodes.JsonNode.DeepEquals(old, comparison),
                        "Exterior-only asphalt must not alter any existing interior/building part.");
                }
                Check(File.ReadAllBytes(Path.Combine(prepared, "factory-map-layout.json")).SequenceEqual(
                    File.ReadAllBytes(Path.Combine(previous, "factory-map-layout.json"))),
                    "Exterior-only asphalt must preserve all equipment bytes.");
                Check(!File.Exists(Path.Combine(prepared, "factory-map-site.json")),
                    "Exterior-only patches must not recolor the site ground inside the building.");
                Console.WriteLine("Exterior-only finish: equipment and geometry unchanged; only manifest-declared ground-floor palette changes allowed; no global ground-color override.");
            }
        }
        foreach (var device in drawing)
            Check(assets.Cast<object>().Count(a => Get<string>(a, "MonitoringDeviceId") == device.Id) == 1, "Each drawing registration has exactly one symbol.");
        using var canvas = (Control)Activator.CreateInstance(Type("FactoryMapCanvas"), assets)!;
        canvas.Size = new Size(1600, 1000);
        canvas.CreateControl();
        var sitePath = Path.Combine(prepared, "factory-map-site.json");
        if (File.Exists(sitePath))
            Call(canvas, "SetSite", Static("FactoryMapSiteStore", "Load", sitePath));
        Call(canvas, "SetBuildingParts", parts);
        Call(canvas, "SetMonitoring", registry, new Dictionary<string, IReadOnlyList<FireMapEventEvidence>>(), new Dictionary<string, MonitoringSample>());
        Directory.CreateDirectory(Path.Combine(prepared, "review"));
        RenderFireModels(Path.Combine(prepared, "review", "Zeta-3D-equipment.png"));
        foreach (var level in new[] { "Ground", "Mezzanine", "Upper mezzanine", "Basement" })
        {
            Call(canvas, "SetLevel", level);
            foreach (var edit in new[] { false, true })
            {
                Set(canvas, "EditMode", edit);
                using var image = new Bitmap(canvas.Width, canvas.Height);
                using var graphics = Graphics.FromImage(image);
                Call(canvas, "OnPaint", new PaintEventArgs(graphics, canvas.ClientRectangle));
                image.Save(Path.Combine(prepared, "review", level.Replace(' ', '-') + (edit ? "-fire-review" : "-normal") + ".png"));
            }
        }
        Call(canvas, "SetLevel", "Ground");
        Call(canvas, "SetTopView");
        foreach (var edit in new[] { false, true })
        {
            Set(canvas, "EditMode", edit);
            using var image = new Bitmap(canvas.Width, canvas.Height);
            using var graphics = Graphics.FromImage(image);
            Call(canvas, "OnPaint", new PaintEventArgs(graphics, canvas.ClientRectangle));
            image.Save(Path.Combine(prepared, "review", edit ? "Ground-top-fire-review.png" : "Ground-top-normal.png"));
        }
        Console.WriteLine($"Prepared drawing validated: {assets.Count} assets, {parts.Count} building parts, {drawing.Length} draft fire devices; all original links preserved.");
    }

    private static void ValidateWallReference(string prepared, string previous, IList assets, IList parts)
    {
        var oldParts = (IList)Static("FactoryMapBuildingStore", "Load",
            Path.Combine(previous, "factory-map-building.json"))!;
        foreach (var old in oldParts.Cast<object>().Where(p => Get<string>(p, "Kind") == "Floor"))
        {
            var current = parts.Cast<object>().Single(p => Get<string>(p, "Id") == Get<string>(old, "Id"));
            foreach (var dimension in new[] { "Length", "Depth", "Rotation", "HeightMeters" })
                Check(Get<float>(old, dimension) == Get<float>(current, dimension),
                    "Reference picture must not rescale a PDF floor: " + Get<string>(old, "Id") + " / " + dimension);
        }
        foreach (var (file, collection) in new[] {
            ("factory-map-building.json", "Parts"), ("factory-map-layout.json", "Assets") })
        {
            var old = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(previous, file)))!;
            var current = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(prepared, file)))!;
            var oldUpper = old[collection]!.AsArray().Where(n => (n!["Level"]?.GetValue<string>() ?? "Ground") != "Ground").ToArray();
            var currentUpper = current[collection]!.AsArray().Where(n => (n!["Level"]?.GetValue<string>() ?? "Ground") != "Ground").ToArray();
            Check(oldUpper.Length == currentUpper.Length &&
                oldUpper.Zip(currentUpper).All(pair => System.Text.Json.Nodes.JsonNode.DeepEquals(pair.First, pair.Second)),
                "Ground wall revision must leave other floors exactly unchanged: " + file);
        }
        Check(File.ReadAllBytes(Path.Combine(previous, "monitoring-devices.json")).SequenceEqual(
            File.ReadAllBytes(Path.Combine(prepared, "monitoring-devices.json"))),
            "Wall reference must not change registrations, tags, addresses or commissioning.");
        Check(parts.Cast<object>().Count(p => Get<string>(p, "Kind") == "Road" &&
            Get<string>(p, "Id").StartsWith("REF-ROAD-") && !Get<string>(p, "Id").StartsWith("REF-ROAD-BEND-")) == 8,
            "Both reference driveway runs must be represented by their eight segments.");
        Check(!parts.Cast<object>().Any(p => Get<string>(p, "Kind") == "Wall" &&
            Get<string>(p, "RoomId") == "DRAW-ROOM-PW"),
            "The south apron must not retain the old warehouse enclosure absent from the reference.");
        var pet3 = assets.Cast<object>().Single(a => Get<string>(a, "Id") == "REF-PET3-FILLER");
        foreach (var link in new[] { "EquipmentId", "MeterSourceId", "MonitoringDeviceId", "ParentUpsId" })
            Check(Get<string>(pet3, link) == "", "PET-3 schematic must not invent a hardware link.");
        Console.WriteLine("Wall reference: exact PDF floor sizes, untouched upper floors/registry, both streets and unlinked PET-3 verified.");
    }

    private static void RenderFireModels(string path)
    {
        var models = new[] { "Zeta smoke detector", "Zeta heat detector", "Zeta multi-sensor detector",
            "Zeta manual call point", "Zeta sounder", "Zeta sounder beacon", "Zeta siren", "Zeta beam detector",
            "Zeta fire panel", "Zeta repeater panel", "Zeta interface module", "Zeta isolator module" };
        using var image = new Bitmap(1200, 900);
        using var graphics = Graphics.FromImage(image);
        using var font = new Font("Segoe UI", 11);
        using var title = new Font("Segoe UI", 14, FontStyle.Bold);
        graphics.Clear(Color.FromArgb(245, 248, 251)); graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.DrawString("ZETA-STYLE 3D EQUIPMENT / schematic variants, verify installed product", title, Brushes.DimGray, 18, 12);
        var draw = Type("FactoryMapEquipmentRenderer").GetMethod("Draw")!;
        for (var index = 0; index < models.Length; index++)
        {
            var asset = New("FactoryMapAsset");
            Set(asset, "Model", models[index]); Set(asset, "Category", "Fire System");
            var column = index % 4; var row = index / 4;
            var center = new PointF(column * 300 + 150, row * 270 + 155);
            var size = ((float Length, float Width, float Height))Static("FactoryMapEquipmentSizes", "Nominal", models[index])!;
            var scale = 100 / (8 * Math.Max(size.Height, Math.Max(size.Length, size.Width)));
            Func<float, float, float, PointF> project = (x, y, z) =>
                new(center.X + (x - .6f * y) * scale, center.Y + (.35f * y - z) * scale);
            using var hit = new GraphicsPath();
            draw.Invoke(null, new object[] { graphics, asset, project, hit });
            graphics.DrawString(models[index], font, Brushes.DimGray, column * 300 + 12, row * 270 + 255);
        }
        image.Save(path);
    }
}
