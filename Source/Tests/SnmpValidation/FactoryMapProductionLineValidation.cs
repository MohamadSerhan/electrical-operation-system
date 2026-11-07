using System.Collections;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Text.Json;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class FactoryMapProductionLineValidation
{
    private static Type T(string name) => typeof(FactoryMapView).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5." + name)!;
    private static object Get(object o, string p) => o.GetType().GetProperty(p)!.GetValue(o)!;
    private static void Set(object o, string p, object v) => o.GetType().GetProperty(p)!.SetValue(o, v);
    private static object Static(string t, string m, params object?[] args) => T(t).GetMethod(m, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, args)!;
    private static object Call(object o, string m, params object?[] args) => o.GetType().GetMethod(m, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(o, args)!;
    private static object Field(object o, string p) => o.GetType().GetField(p)!.GetValue(o)!;
    private static IList List(string name) => (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(T(name)))!;
    private static string Json(object o) => JsonSerializer.Serialize(o, o.GetType());
    private static void Require(bool c, string m) { if (!c) throw new InvalidOperationException(m); }

    public static void Run()
    {
        var input = Environment.GetEnvironmentVariable("EOEMS_PRODUCTION_PLAN_INPUT");
        var output = Environment.GetEnvironmentVariable("EOEMS_PRODUCTION_PLAN_OUTPUT");
        if (!string.IsNullOrWhiteSpace(input) && !string.IsNullOrWhiteSpace(output) &&
            string.Equals(Path.GetFullPath(input).TrimEnd('\\'), Path.GetFullPath(output).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Production proposal output must be separate from the source map directory.");
        var originalAssets = List("FactoryMapAsset");
        var originalBuilding = List("FactoryBuildingPart");
        if (!string.IsNullOrWhiteSpace(input))
        {
            originalAssets = (IList)Static("FactoryMapLayoutStore", "LoadAssets", originalAssets, Path.Combine(input, "factory-map-layout.json"));
            originalBuilding = (IList)Static("FactoryMapBuildingStore", "Load", Path.Combine(input, "factory-map-building.json"));
        }
        else
        {
            for (var i = 0; i < 7; i++)
            {
                var line = i == 6 ? "CAN1" : "PET" + (i + 1);
                var room = i == 4 ? "DRAW-ROOM-P5" : i == 5 ? "DRAW-ROOM-P6" : "REF-ROOM-" + line;
                var floor = Activator.CreateInstance(T("FactoryBuildingPart"))!;
                Set(floor, "Id", "FLOOR-" + line); Set(floor, "RoomId", room); Set(floor, "RoomName", line); Set(floor, "Kind", "Floor");
                Set(floor, "Style", "Rectangle"); Set(floor, "Material", "Concrete");
                var center = i is 4 or 5 ? 1270F / 1365 : 616F / 1365;
                var length = i is 4 or 5 ? 150F : 300F;
                Set(floor, "X", center); Set(floor, "Y", (250F + i * 100) / 766);
                Set(floor, "Length", length); Set(floor, "Depth", 100F); Set(floor, "Level", "Ground");
                originalBuilding.Add(floor);
                foreach (var side in new[] { -1, 1 })
                {
                    var wall = Call(floor, "Clone");
                    Set(wall, "Id", "WALL-" + line + side); Set(wall, "Kind", "Wall"); Set(wall, "Style", "Solid");
                    Set(wall, "Length", 100F); Set(wall, "Depth", 1.6F); Set(wall, "HeightMeters", 4F);
                    Set(wall, "X", center + side * length / 2 / 1365); Set(wall, "Rotation", side > 0 ? 90F : 270F);
                    originalBuilding.Add(wall);
                }
            }
        }
        var before = originalAssets.Cast<object>().ToDictionary(a => (string)Get(a, "Id"), Json);
        var beforeBuilding = originalBuilding.Cast<object>().ToDictionary(a => (string)Get(a, "Id"), Json);
        var proposal = Activator.CreateInstance(T("FactoryMapProductionLinePlan"), new object[] { originalAssets, originalBuilding })!;
        Call(proposal, "Build");
        var assets = (IList)Field(proposal, "Assets");
        var building = (IList)Field(proposal, "Building");
        var links = ((IList)Field(proposal, "Connections")).Cast<object>().ToArray();
        Require(assets.Cast<object>().Select(a => Get(a, "Id")).Distinct().Count() == assets.Count, "Proposed lines must not duplicate asset IDs.");
        foreach (var asset in assets.Cast<object>())
        {
            var single = List("FactoryMapAsset"); single.Add(asset);
            try { Static("FactoryMapLayoutStore", "ValidateAssets", single); }
            catch (TargetInvocationException ex) { throw new InvalidOperationException($"Invalid proposal asset {Get(asset, "Id")}: {Get(asset, "Category")} / {Get(asset, "Model")}", ex.InnerException); }
        }
        foreach (var old in originalAssets.Cast<object>())
        {
            var id = (string)Get(old, "Id");
            Require(Json(old) == before[id], "Building a proposal must not mutate the source map.");
            var current = assets.Cast<object>().Single(a => (string)Get(a, "Id") == id);
            foreach (var property in new[] { "EquipmentId", "MeterSourceId", "MonitoringDeviceId", "ParentUpsId", "CameraIpAddress" })
                Require(Equals(Get(old, property), Get(current, property)), "Production rebuild must preserve " + id + " " + property);
            if ((string)Get(old, "Category") == "Fire System")
                Require(Json(current) == before[id], "Fire devices must remain completely unchanged.");
        }
        foreach (var old in originalBuilding.Cast<object>())
            Require(Json(building.Cast<object>().Single(p => (string)Get(p, "Id") == (string)Get(old, "Id"))) == beforeBuilding[(string)Get(old, "Id")],
                "Existing walls, floors and personnel doors must not be modified.");
        Static("FactoryMapBuildingStore", "Validate", building);
        ValidateWallCrossings(links, building);
        ValidateEquipmentFootprints(assets, building);
        RenderWholeSite(assets, building, output);
        foreach (var line in new[] { "PET1", "PET2", "PET3", "PET4", "PET5", "PET6", "CAN1" })
        {
            var stages = assets.Cast<object>().Where(a => (bool)Get(a, "IsPlaced") && ((string)Get(a, "Name")).StartsWith(line + " ")).ToArray();
            var can = line == "CAN1";
            var water = line is "PET4" or "PET5";
            Require(stages.Any(a => (string)Get(a, "Model") == (can ? "Can filler and seamer - carbonated drinks" :
                water ? "PET Combi - still water" : "PET Combi - carbonated drinks")), "Correct filling architecture required for " + line);
            Require(!can || !stages.Any(a => ((string)Get(a, "Model")).Contains("Preform") || ((string)Get(a, "Model")).Contains("Cap elevator")),
                "Can production cannot contain preform feeds or screw-cap elevators.");
            foreach (var model in new[] { "Bottle inspection station", "Bottle reject station", "Date coding station",
                "Bottle accumulation table", "Shrink wrapper", "Palletizer", "Pallet stretch wrapper", "CIP station - 1 tank", "CIP return skid" })
                Require(stages.Any(a => (string)Get(a, "Model") == model), "Missing downstream/service stage " + line + ": " + model);
            Require(stages.Any(a => (string)Get(a, "Model") == "Beverage carbonator") != water, "Carbonation must follow the confirmed recipe.");
            var lineLinks = links.Where(c => (string)Get(c, "Line") == line).ToArray();
            Require(lineLinks.Length >= 15, "Line requires connected feed, closure, product, CIP and packing paths.");
            foreach (var link in lineLinks)
            {
                Require(assets.Cast<object>().Any(a => Get(a, "Id").Equals(Get(link, "FromId"))) &&
                        assets.Cast<object>().Any(a => Get(a, "Id").Equals(Get(link, "ToId"))), "All connections require valid equipment IDs.");
                var route = (PointF[])Get(link, "Route");
                Require(route.Length >= 2 && route.Zip(route.Skip(1)).All(p => p.First.X == p.Second.X || p.First.Y == p.Second.Y),
                    "Route segments must be physically representable orthogonal runs.");
            }
            RenderLine(line, stages, output);
        }
        var scratch = Path.Combine(Path.GetTempPath(), "production-proposal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var map = Path.Combine(scratch, "factory-map-layout.json");
            var floorPath = Path.Combine(scratch, "factory-map-building.json");
            Static("FactoryMapLayoutStore", "SaveAssets", assets, map);
            Static("FactoryMapBuildingStore", "Save", building, floorPath);
            var restored = (IList)Static("FactoryMapLayoutStore", "LoadAssets", List("FactoryMapAsset"), map);
            Require(restored.Count == assets.Count, "Entire proposed map must survive persistence with embedded connectors.");
            foreach (var item in assets.Cast<object>())
                Require(Json(restored.Cast<object>().Single(a => Get(a, "Id").Equals(Get(item, "Id")))) == Json(item),
                    "Saved proposal must preserve equipment geometry, positions and links exactly.");
            if (!string.IsNullOrWhiteSpace(output))
            {
                Directory.CreateDirectory(output);
                File.Copy(map, Path.Combine(output, "factory-map-layout.json"), true);
                File.Copy(floorPath, Path.Combine(output, "factory-map-building.json"), true);
                File.WriteAllText(Path.Combine(output, "production-connections.json"), Json(Field(proposal, "Connections")));
                File.WriteAllText(Path.Combine(output, "production-assumptions.txt"), string.Join(Environment.NewLine, ((IList)Field(proposal, "Notes")).Cast<string>()));
            }
        }
        finally { Directory.Delete(scratch, true); }
        Console.WriteLine($"Seven proposed lines: {assets.Count} retained/new assets, {links.Length} material/service connections, PET water/CSD and can recipes, preserved IDs/fire/doors and portable persistence passed.");
    }

    private static void RenderWholeSite(IList assets, IList building, string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        using var view = new FactoryMapView(new UserSession { RoleName = "Administrator" }, Array.Empty<MeterSnapshot>(), null!);
        view.Size = new Size(1600, 1000);
        view.CreateControl();
        var field = typeof(FactoryMapView).GetField("_assets", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var displayed = (IList)field.GetValue(view)!;
        displayed.Clear();
        foreach (var asset in assets) displayed.Add(asset);
        var canvas = (Control)typeof(FactoryMapView).GetField("_canvas", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;
        typeof(FactoryMapView).GetField("_building", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(view, building);
        Call(canvas, "SetBuildingParts", building);
        view.SetReadings(Array.Empty<MeterSnapshot>());
        foreach (var plan in new[] { true, false })
        {
            Call(canvas, plan ? "SetPlanView" : "SetIsometricView");
            Call(view, "FitFactory");
            using var bitmap = new Bitmap(view.Width, view.Height);
            view.DrawToBitmap(bitmap, view.ClientRectangle);
            bitmap.Save(Path.Combine(output, plan ? "Whole-site-Plan.png" : "Whole-site-3D.png"));
        }
    }

    private static void RenderLine(string line, object[] assets, string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var minX = assets.Min(a => ((PointF)Get(a, "Position")).X * 1365) - 70;
        var maxX = assets.Max(a => ((PointF)Get(a, "Position")).X * 1365) + 70;
        var minY = assets.Min(a => ((PointF)Get(a, "Position")).Y * 766) - 40;
        var maxY = assets.Max(a => ((PointF)Get(a, "Position")).Y * 766) + 40;
        var cy = (minY + maxY) / 2;
        foreach (var plan in new[] { true, false })
        {
            using var bitmap = new Bitmap(1600, 700);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.FromArgb(243, 247, 249));
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var scale = Math.Min(1400F / (maxX - minX), 500F / (maxY - minY + 40));
            foreach (var a in assets.OrderBy(a => ((PointF)Get(a, "Position")).X + ((PointF)Get(a, "Position")).Y))
            {
                var position = (PointF)Get(a, "Position");
                var angle = (float)Get(a, "Rotation") * Math.PI / 180;
                var elevation = (float)Get(a, "BaseHeightMeters") * 8;
                Func<float, float, float, PointF> project = (x, y, z) =>
                {
                    var rx = position.X * 1365 + x * Math.Cos(angle) - y * Math.Sin(angle);
                    var ry = position.Y * 766 + x * Math.Sin(angle) + y * Math.Cos(angle);
                    return new(100 + (float)(rx - minX) * scale, 350 + (float)(ry - cy) * scale * (plan ? 1 : .5F) - (z + elevation) * scale * (plan ? 0 : .86F));
                };
                using var hit = new GraphicsPath(FillMode.Winding);
                Static("FactoryMapEquipmentRenderer", "Draw", graphics, a, project, hit);
            }
            using var font = new Font("Segoe UI", 16);
            graphics.DrawString(line + " - PROPOSED process layout / site and OEM verification required", font, Brushes.Black, 25, 15);
            bitmap.Save(Path.Combine(output, line + (plan ? "-Plan.png" : "-3D.png")));
        }
    }

        private static void ValidateEquipmentFootprints(IList assets, IList building)
        {
            var items = assets.Cast<object>().Where(a => (bool)Get(a, "IsPlaced") &&
                ((string)Get(a, "Name")).Contains("PROPOSED") && Get(a, "CustomEquipment") == null).ToArray();
            RectangleF Bounds(object a)
            {
                var dimensions = Static("FactoryMapEquipmentSizes", "Dimensions", a);
                var l = (float)Field(dimensions, "Item1") * 8;
                var w = (float)Field(dimensions, "Item2") * 8;
                var p = (PointF)Get(a, "Position");
                return new(p.X * 1365 - l / 2, p.Y * 766 - w / 2, l, w);
            }
            var errors = new List<string>();
            foreach (var item in items)
                foreach (var wall in building.Cast<object>().Where(p => (string)Get(p, "Kind") == "Wall" && (string)Get(p, "Level") == "Ground"))
                {
                    var angle = (float)Get(wall, "Rotation") * Math.PI / 180;
                    if (Math.Abs(Math.Sin(angle)) > .01 && Math.Abs(Math.Cos(angle)) > .01) continue;
                    var l = (float)Get(wall, "Length");
                    var d = (float)Get(wall, "Depth");
                    var w = Math.Abs(Math.Cos(angle)) * l + Math.Abs(Math.Sin(angle)) * d;
                    var h = Math.Abs(Math.Sin(angle)) * l + Math.Abs(Math.Cos(angle)) * d;
                    var rectangle = new RectangleF((float)Get(wall, "X") * 1365 - (float)w / 2,
                        (float)Get(wall, "Y") * 766 - (float)h / 2, (float)w, (float)h);
                    var overlap = RectangleF.Intersect(Bounds(item), rectangle);
                    if (overlap.Width > .5 && overlap.Height > .5)
                        errors.Add($"{Get(item, "Id")} / solid wall {Get(wall, "Id")}");
                }
            for (var i = 0; i < items.Length; i++)
                for (var j = i + 1; j < items.Length; j++)
                {
                    var overlap = RectangleF.Intersect(Bounds(items[i]), Bounds(items[j]));
                    if (overlap.Width > .5 && overlap.Height > .5)
                        errors.Add($"{Get(items[i], "Id")} / {Get(items[j], "Id")} ({overlap.Width:F1}x{overlap.Height:F1} units)");
                }
            Require(errors.Count == 0, "Proposed nominal equipment footprints must not overlap:\n" + string.Join("\n", errors));
        }

        private static void ValidateWallCrossings(object[] links, IList building)
        {
            var errors = new List<string>();
            foreach (var link in links)
            {
                var route = (PointF[])Get(link, "Route");
                var z = (float)Get(link, "ElevationUnits") / 8;
                foreach (var wall in building.Cast<object>().Where(p => (string)Get(p, "Kind") == "Wall" && (string)Get(p, "Level") == "Ground"))
                {
                    var angle = (float)Get(wall, "Rotation") * Math.PI / 180;
                    PointF Local(PointF p)
                    {
                        var x = p.X - (float)Get(wall, "X") * 1365;
                        var y = p.Y - (float)Get(wall, "Y") * 766;
                        return new((float)(x * Math.Cos(angle) + y * Math.Sin(angle)), (float)(-x * Math.Sin(angle) + y * Math.Cos(angle)));
                    }
                    for (var i = 1; i < route.Length; i++)
                    {
                        var a = Local(route[i - 1]); var b = Local(route[i]);
                        if (Math.Abs(b.Y - a.Y) < .01 || a.Y * b.Y > 0) continue;
                        var along = a.X + (b.X - a.X) * -a.Y / (b.Y - a.Y);
                        if (Math.Abs(along) > (float)Get(wall, "Length") / 2 ||
                            z >= (float)Get(wall, "BaseMeters") + (float)Get(wall, "HeightMeters")) continue;
                        var opening = building.Cast<object>().Any(p => (string)Get(p, "ParentWallId") == (string)Get(wall, "Id") &&
                            Math.Abs(along - (float)Get(p, "Offset")) < (float)Get(p, "Length") / 2 &&
                            z >= (float)Get(p, "BaseMeters") && z + .08 <= (float)Get(p, "BaseMeters") + (float)Get(p, "HeightMeters"));
                        if (!opening) errors.Add($"{Get(link, "Line")} {Get(link, "Medium")}: wall {Get(wall, "Id")} at offset {along:F1}, elevation {z:F2}m");
                }
            }
            Require(errors.Count == 0, "Proposed routes cannot cross solid walls:\n" + string.Join("\n", errors.Distinct()));
        }
    }
}
