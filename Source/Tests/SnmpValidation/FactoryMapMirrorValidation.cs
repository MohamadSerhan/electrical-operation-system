using System.Collections;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class FactoryMapMirrorValidation
{
    private static Type T(string name) => typeof(FactoryMapView).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5." + name)!;
    private static object Get(object o, string p) => o.GetType().GetProperty(p, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(o)!;
    private static void Set(object o, string p, object v) => o.GetType().GetProperty(p)!.SetValue(o, v);
    private static object Field(object o, string p) => o.GetType().GetField(p, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;
    private static object? Call(object o, string m, params object?[] args) => o.GetType().GetMethod(m, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(o, args);
    private static object? Static(string t, string m, params object?[] args) => T(t).GetMethod(m, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, args);
    private static IList List(string name, params object[] items)
    {
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(T(name)))!;
        foreach (var item in items) list.Add(item);
        return list;
    }
    private static void Require(bool c, string m) { if (!c) throw new InvalidOperationException(m); }
    private static object Asset(string model)
    {
        var a = Activator.CreateInstance(T("FactoryMapAsset"))!;
        Set(a, "Id", "MIRROR-" + model); Set(a, "Name", model); Set(a, "Model", model);
        Set(a, "Category", model == "Single swing gate" ? "Access Control" : "Production Lines");
        Set(a, "EquipmentId", "preserved-link");
        return a;
    }
    private static object Part(string kind, string style)
    {
        var p = Activator.CreateInstance(T("FactoryBuildingPart"))!;
        Set(p, "Kind", kind); Set(p, "Style", style); Set(p, "Material", kind == "Road" ? "Asphalt" : "Concrete");
        Set(p, "Length", 100F); Set(p, "Depth", 40F);
        return p;
    }

    public static void Run()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "mirror-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var equipment = new[] { Asset("Sidel-style blower and preform feed"), Asset("Single swing gate") };
            var obj = Path.Combine(scratch, "asymmetric.obj");
            File.WriteAllText(obj, "v -3 -1 0\nv 2 -1 0\nv -3 2 0\nv -3 -1 2\nf 1 2 3\nf 1 2 4\nf 1 3 4\nf 2 3 4\n");
            var definition = Static("FactoryMapCadImporter", "ImportFile", obj, false)!;
            var cad = Static("FactoryMapCustomEquipmentStore", "CreateAsset", definition)!;
            foreach (var a in equipment.Append(cad))
                CheckReflections(a, false);
            foreach (var p in new[] { Part("Floor", "L-shaped"), Part("Stairs", "L-turn left"), Part("Road", "Curved dashed left") })
                CheckReflections(p, true);

            var item = equipment[0];
            using (var form = (Form)Activator.CreateInstance(T("FactoryMapItemForm"), new object[] { item, List("FactoryMapAsset") })!)
            {
                ((CheckBox)Field(form, "_mirrorHorizontal")).Checked = true;
                ((CheckBox)Field(form, "_mirrorVertical")).Checked = true;
                var appearance = Call(item, "Clone")!;
                Call(form, "ApplyAppearance", appearance);
                Require((bool)Get(appearance, "MirrorHorizontal") && (bool)Get(appearance, "MirrorVertical"), "Edit Item checkboxes must apply both reflections.");
                Require(Get(appearance, "Position").Equals(Get(item, "Position")) && Get(appearance, "EquipmentId").Equals(Get(item, "EquipmentId")),
                    "Mirror must preserve position and linked identity.");
                Static("FactoryMapLayoutStore", "SaveAssets", List("FactoryMapAsset", appearance), Path.Combine(scratch, "map.json"));
                var loaded = (IList)Static("FactoryMapLayoutStore", "LoadAssets", List("FactoryMapAsset"), Path.Combine(scratch, "map.json"))!;
                Require((bool)Get(loaded[0]!, "MirrorHorizontal") && (bool)Get(loaded[0]!, "MirrorVertical"), "Both equipment flags must survive save/load.");
            }
            var floor = Part("Floor", "L-shaped");
            using (var form = (Form)Activator.CreateInstance(T("FactoryMapBuildingForm"), new object[] { floor, List("FactoryBuildingPart", floor), false })!)
            {
                ((CheckBox)Field(form, "_mirrorHorizontal")).Checked = true;
                ((CheckBox)Field(form, "_mirrorVertical")).Checked = true;
                var result = Call(form, "ReadPart")!;
                Require((bool)Get(result, "MirrorHorizontal") && (bool)Get(result, "MirrorVertical"), "Building checkboxes must apply both reflections.");
                Static("FactoryMapBuildingStore", "Save", List("FactoryBuildingPart", result), Path.Combine(scratch, "building.json"));
                var loaded = (IList)Static("FactoryMapBuildingStore", "Load", Path.Combine(scratch, "building.json"))!;
                Require((bool)Get(loaded[0]!, "MirrorHorizontal") && (bool)Get(loaded[0]!, "MirrorVertical"), "Both building flags must survive save/load.");
            }
            var wall = Part("Wall", "Solid");
            Set(wall, "Rotation", 35F); Set(wall, "MirrorHorizontal", true); Set(wall, "MirrorVertical", true);
            var point = (PointF)Static("FactoryMapBuildingStore", "WorldPoint", wall, 20F, 7F)!;
            var local = (PointF)Static("FactoryMapBuildingStore", "LocalPoint", wall, point)!;
            Require(Math.Abs(local.X - 20) < .001 && Math.Abs(local.Y - 7) < .001, "Mirrored rotated building coordinates must invert exactly.");
            var door = Part("Door", "Single leaf");
            Set(door, "Material", "Steel"); Set(door, "Length", 10F); Set(door, "HeightMeters", 2F);
            Set(door, "ParentWallId", Get(wall, "Id")); Set(door, "Offset", 20F);
            var resolved = Static("FactoryMapBuildingStore", "Resolve", door, List("FactoryBuildingPart", wall, door))!;
            var center = (PointF)Static("FactoryMapBuildingStore", "WorldPoint", wall, 20F, 0F)!;
            Require(Math.Abs((float)Get(resolved, "X") * 1365 - center.X) < .001 &&
                (bool)Get(resolved, "MirrorHorizontal") && (bool)Get(resolved, "MirrorVertical"),
                "Attached openings must follow their mirrored wall center, cutout and orientation.");
            ValidateCache(equipment[0]);
            ValidateUndo();
            CaptureViews(equipment[0], false);
            CaptureViews(Part("Stairs", "L-turn left"), true);
            Console.WriteLine("Mirroring: both property dialogs, reflected built-in/CAD/opening/floor/stair/road geometry, rotated coordinates, wall attachments, cache isolation, save/load and undo/cancel passed.");
        }
        finally { Directory.Delete(scratch, true); }
    }

    private static void CheckReflections(object item, bool building)
    {
        using var original = Draw(item, building);
        foreach (var flags in new[] { (true, false), (false, true), (true, true) })
        {
            Set(item, "MirrorHorizontal", flags.Item1); Set(item, "MirrorVertical", flags.Item2);
            using var mirrored = Draw(item, building);
            Require(original.PointCount > 0 && mirrored.PointCount == original.PointCount,
                "Mirroring must preserve the full selectable shape topology.");
            var expected = original.PathPoints.Select(p => new PointF(flags.Item1 ? 400 - p.X : p.X, flags.Item2 ? 400 - p.Y : p.Y)).ToArray();
            var actual = mirrored.PathPoints;
            Require(expected.All(p => actual.Any(q => Math.Abs(p.X - q.X) < .01 && Math.Abs(p.Y - q.Y) < .01)),
                $"Mirror must reflect every selectable geometry vertex: {Get(item, building ? "Kind" : "Model")} flags {flags}");
        }
        Set(item, "MirrorHorizontal", false); Set(item, "MirrorVertical", false);
    }

    private static GraphicsPath Draw(object item, bool building)
    {
        using var bitmap = new Bitmap(400, 400);
        using var g = Graphics.FromImage(bitmap);
        var hit = new GraphicsPath(FillMode.Winding);
        Func<float, float, float, PointF> project;
        if (building)
        {
            var wx = (float)Get(item, "X") * 1365;
            var wy = (float)Get(item, "Y") * 766;
            project = (x, y, z) => new(200 + (x - wx) * 2, 200 + (y - wy) * 2);
            Static("FactoryMapBuildingRenderer", "Draw", g, item, List("FactoryBuildingPart", item), 30, project, hit);
        }
        else
        {
            project = (x, y, z) => new(200 + x * 2, 200 + y * 2);
            Static("FactoryMapEquipmentRenderer", "Draw", g, item, project, hit);
        }
        return hit;
    }

    private static void ValidateCache(object asset)
    {
        using var cache = (IDisposable)Activator.CreateInstance(T("FactoryMapRenderCache"))!;
        using var bitmap = new Bitmap(400, 400);
        using var g = Graphics.FromImage(bitmap);
        Func<float, float, float, PointF> project = (x, y, z) => new(x * 2, y * 2);
        foreach (var flag in new[] { false, true, false })
        {
            Set(asset, "MirrorHorizontal", flag);
            using var hit = (GraphicsPath)Call(cache, "Draw", g, asset, project, 1F, 1F, 1F, new PointF(200, 200), 1F, new Rectangle(0, 0, 400, 400))!;
            Require(hit.PointCount > 0, "Reflected cached equipment needs selectable geometry.");
        }
        Require((int)Get(cache, "BuildCount") == 2, "Mirror flags must distinguish vector cache entries and reuse the original when cleared.");
    }

    private static void ValidateUndo()
    {
        using var host = new Form { Size = new Size(1440, 900) };
        using var view = new FactoryMapView(new UserSession { RoleName = "Administrator" }, Array.Empty<MeterSnapshot>(), null!);
        host.Controls.Add(view); host.Show();
        var assets = (IList)Field(view, "_assets");
        var canvas = (Control)Field(view, "_canvas");
        Call(view, "ToggleEditMode", null, EventArgs.Empty);
        Call(canvas, "SelectAsset", assets[0]);
        Set(assets[0]!, "MirrorHorizontal", true);
        Call(view, "MapItemsChanged");
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.Z));
        Require(!(bool)Get(assets[0]!, "MirrorHorizontal"), "Undo must restore equipment mirror flags.");
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.Y));
        Require((bool)Get(assets[0]!, "MirrorHorizontal"), "Redo must restore equipment reflection.");
        Call(view, "CancelLayout", null, EventArgs.Empty);
        Require(!(bool)Get(assets[0]!, "MirrorHorizontal"), "Cancel must restore saved mirror state.");
        var building = (IList)Field(view, "_building");
        building.Clear();
        building.Add(Part("Floor", "L-shaped"));
        Call(canvas, "SetBuildingParts", building);
        Call(view, "BeginBuildingEdit", null, EventArgs.Empty);
        Call(canvas, "SelectBuildingPart", building[0]);
        Set(building[0]!, "MirrorVertical", true);
        Call(view, "RecordEditHistory");
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.Z));
        Require(!(bool)Get(building[0]!, "MirrorVertical"), "Undo must restore building reflection.");
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.Y));
        Require((bool)Get(building[0]!, "MirrorVertical"), "Redo must restore building reflection.");
        Call(view, "CancelBuilding");
        Require(!(bool)Get(building[0]!, "MirrorVertical"), "Building Cancel must restore saved reflection.");
        host.Hide();
    }

    private static void CaptureViews(object item, bool building)
    {
        var directory = Environment.GetEnvironmentVariable("EOEMS_MIRROR_EVIDENCE");
        using var bitmap = new Bitmap(1600, 600);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.FromArgb(244, 247, 249));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var flags = new[] { (false, false), (true, false), (false, true), (true, true) };
        using var font = new Font("Segoe UI", 10);
        for (var row = 0; row < 2; row++)
            for (var col = 0; col < 4; col++)
            {
                Set(item, "MirrorHorizontal", flags[col].Item1); Set(item, "MirrorVertical", flags[col].Item2);
                var wx = building ? (float)Get(item, "X") * 1365 : 0;
                var wy = building ? (float)Get(item, "Y") * 766 : 0;
                Func<float, float, float, PointF> project = (x, y, z) =>
                {
                    x -= wx; y -= wy;
                    return row == 0 ? new PointF(col * 400 + 200 + x * 3, row * 300 + 160 + y * 3) :
                        new PointF(col * 400 + 200 + (x - y) * 2, row * 300 + 210 + (x + y) - z * 2);
                };
                using var hit = new GraphicsPath(FillMode.Winding);
                if (building) Static("FactoryMapBuildingRenderer", "Draw", g, item, List("FactoryBuildingPart", item), 30, project, hit);
                else Static("FactoryMapEquipmentRenderer", "Draw", g, item, project, hit);
                Require(hit.PointCount > 0, "Reflected plan/3D render must remain selectable.");
                g.DrawString((row == 0 ? "Plan" : "3D") + $" / H:{flags[col].Item1} V:{flags[col].Item2}", font, Brushes.Black, col * 400 + 10, row * 300 + 10);
            }
        Set(item, "MirrorHorizontal", false); Set(item, "MirrorVertical", false);
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        bitmap.Save(Path.Combine(directory, building ? "Building-mirrors.png" : "Equipment-mirrors.png"));
    }
}
