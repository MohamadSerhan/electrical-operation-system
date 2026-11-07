using System.Collections;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class FactoryMapRefinementsValidation
{
    private static Type Type(string name) => typeof(FactoryMapView).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5." + name)!;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(target, args);
    private static object? Static(string type, string name, params object?[] args) => Type(type).GetMethod(name)!.Invoke(null, args);
    private static object Get(object target, string name) => target.GetType().GetProperty(name)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Describe(object value) => JsonSerializer.Serialize(value, value.GetType());
    private static IList Parts(params object[] parts)
    {
        var result = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("FactoryBuildingPart")))!;
        foreach (var part in parts) result.Add(part);
        return result;
    }

    public static void Run()
    {
        ValidateEditorLifecycle();
        ValidateEquipmentSizes();
        ValidateHansaChiller();
        RunAppearance();
        ValidateFillerAncillaries();
        RunProcessReferences();
        FactoryMapMotionValidation.Run();
        ValidateMeterLinks();
        ValidateClipboard();
        ValidateNavigation();
        ValidateMarkingsAndCurtain();
        Console.WriteLine("Factory independent meter links, clipboard, smooth zoom, fullscreen Escape, markings and curtain checks passed.");
    }

    public static void RunEditors()
    {
        ValidateEditorLifecycle();
        ValidateMeterLinks();
        ValidateClipboard();
        Console.WriteLine("Factory on-demand editor lifecycle, meter links and clipboard checks passed.");
    }

    public static void RunOpening()
    {
        using var host = new Form { Size = new Size(1440, 900) };
        host.Show();
        Application.DoEvents();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var view = new FactoryMapView(new UserSession { RoleName = "Administrator" }, Array.Empty<MeterSnapshot>(), null!);
        Console.WriteLine($"Map constructor: {clock.Elapsed.TotalMilliseconds:F1}ms");
        clock.Restart();
        host.Controls.Add(view);
        view.SetPageActive(true);
        Application.DoEvents();
        Console.WriteLine($"Map first activation/paint: {clock.Elapsed.TotalMilliseconds:F1}ms");
        var canvas = Field<Control>(view, "_canvas");
        var cache = Field<object>(canvas, "_renderCache");
        Console.WriteLine($"Equipment geometry builds: {cache.GetType().GetProperty("BuildCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)}");
        clock.Restart();
        for (var index = 0; index < 5; index++)
        {
            view.Visible = false;
            view.SetPageActive(false);
            view.Visible = true;
            view.SetPageActive(true);
            Application.DoEvents();
        }
        Console.WriteLine($"Cached map navigation: {clock.Elapsed.TotalMilliseconds / 5:F1}ms/visit");
        ValidateIdleRendering();
    }

    private static void ValidateIdleRendering()
    {
        using var view = new FactoryMapView(new UserSession { RoleName = "Administrator" }, Array.Empty<MeterSnapshot>(), null!);
        view.Size = new Size(1440, 900);
        view.CreateControl();
        var canvas = Field<Control>(view, "_canvas");
        canvas.CreateControl();
        using var image = new Bitmap(canvas.Width, canvas.Height);
        using var graphics = Graphics.FromImage(image);
        void Paint() => Call(canvas, "OnPaint", new PaintEventArgs(graphics, canvas.ClientRectangle));
        Paint();
        var original = Field<int>(canvas, "_scenePaintCount");
        var wheel = Field<object>(canvas, "_navigation");
        var bounds = (Rectangle)Get(wheel, "Bounds");
        canvas.Invalidate(bounds);
        Paint();
        Require(Field<int>(canvas, "_scenePaintCount") == original,
            "Navigation-overlay fade must reuse exact native-resolution scene pixels instead of rebuilding factory geometry.");
        canvas.Invalidate();
        canvas.Invalidate(bounds);
        Paint();
        Require(Field<int>(canvas, "_scenePaintCount") == original + 1,
            "An overlay invalidation must never hide an already-pending map edit or camera change.");
        var invalidations = 0;
        canvas.Invalidated += (_, _) => invalidations++;
        view.SetReadings(Array.Empty<MeterSnapshot>());
        invalidations = 0;
        for (var index = 0; index < 20; index++) view.SetReadings(Array.Empty<MeterSnapshot>());
        Require(invalidations == 0, "Unchanged acquisition state must not invalidate the map on every shell refresh.");
        var transformer = Field<IList>(view, "_assets").Cast<object>().First(asset => ((string)Get(asset, "EquipmentId")).StartsWith("TRANSFORMER-"));
        var id = ((string)Get(transformer, "EquipmentId")).Replace("TRANSFORMER-", "");
        var reading = new MeterSnapshot { TransformerName = id, Quality = MeterQuality.Good,
            CapturedAtUtc = DateTime.UtcNow, PowerState = TransformerPowerState.On };
        view.SetReadings(new[] { reading });
        Require(invalidations > 0, "A new acquisition state must request a fresh map scene.");
        invalidations = 0;
        reading.Quality = MeterQuality.Timeout;
        view.SetReadings(new[] { reading });
        Require(invalidations > 0, "Mutating an existing acquisition object must still refresh its map status.");
        Call(canvas, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, 300, 300, 120));
        Call(wheel, "PointerMove", new MouseEventArgs(MouseButtons.None, 0, 50, 50, 0));
        view.SetPageActive(true);
        view.SetPageActive(false);
        Require(!Field<System.Windows.Forms.Timer>(canvas, "_zoomTimer").Enabled &&
            !Field<System.Windows.Forms.Timer>(wheel, "_timer").Enabled,
            "Leaving the page must stop map-only zoom and navigation timers.");
        Console.WriteLine("Factory idle rendering: unchanged readings cause no map invalidations; overlay fade reuses native pixels; edits/state changes invalidate; hidden navigation timers stop.");
    }

    private static void ValidateEditorLifecycle()
    {
        using var view = new FactoryMapView(new UserSession { RoleName = "Administrator" }, Array.Empty<MeterSnapshot>(), null!);
        view.Size = new Size(1440, 900);
        view.CreateControl();
        var canvas = Field<Control>(view, "_canvas");
        int Count(Control parent) => 1 + parent.Controls.Cast<Control>().Sum(Count);
        void Released()
        {
            foreach (var name in new[] { "_editTools", "_assetPicker", "_saveButton", "_cancelButton",
                "_buildingTools", "_buildingPicker", "_savedAssets", "_savedBuilding" })
                Require(Field<object?>(view, name) == null, $"Normal map view must not retain editor controls or rollback copies: {name}.");
            Require(Field<object?>(canvas, "_editMenu") == null && Field<IDictionary>(canvas, "_footprints").Count == 0,
                "Normal map view must release editing context menus and snapping geometry.");
        }
        Released();
        var restingControls = Count(view);
        var assets = Field<IList>(view, "_assets");
        var parts = Field<IList>(view, "_building");
        var originalAssets = Describe(assets);
        var originalParts = Describe(parts);
        var equipmentControls = 0;
        var buildingControls = 0;
        for (var cycle = 0; cycle < 5; cycle++)
        {
            Call(view, "ToggleEditMode", null, EventArgs.Empty);
            var editTools = Field<FlowLayoutPanel>(view, "_editTools");
            var picker = Field<ComboBox>(view, "_assetPicker");
            var save = Field<Button>(view, "_saveButton");
            equipmentControls = Count(view) - restingControls;
            Require(equipmentControls > 0 && picker.Items.Count == assets.Count &&
                Field<object?>(view, "_buildingTools") == null && Field<object?>(view, "_savedBuilding") == null,
                "Equipment editing must construct only its own tools and rollback state.");
            Call(view, "ToggleEditMode", null, EventArgs.Empty);
            Call(view, "BeginBuildingEdit", null, EventArgs.Empty);
            Require(ReferenceEquals(editTools, Field<FlowLayoutPanel>(view, "_editTools")) &&
                Field<object?>(view, "_buildingTools") == null, "Duplicate/concurrent entry must not allocate another editor.");
            Set(assets[0]!, "Name", "Unsaved lifecycle change");
            Call(canvas, "ShowEditMenu", new Point(20, 20));
            var menu = Field<ContextMenuStrip>(canvas, "_editMenu");
            menu.Hide();
            Field<IDictionary>(canvas, "_footprints")["lifecycle"] = new RectangleF(0, 0, 10, 10);
            Call(view, "CancelLayout", null, EventArgs.Empty);
            Released();
            Require(editTools.IsDisposed && picker.IsDisposed && save.IsDisposed && menu.IsDisposed && Count(view) == restingControls &&
                Describe(assets) == originalAssets, "Leaving equipment editing must dispose controls and restore the transaction.");

            Call(view, "BeginBuildingEdit", null, EventArgs.Empty);
            var buildingTools = Field<FlowLayoutPanel>(view, "_buildingTools");
            var buildingPicker = Field<ComboBox>(view, "_buildingPicker");
            buildingControls = Count(view) - restingControls;
            Require(buildingControls > 0 && buildingPicker.Items.Count == parts.Count &&
                Field<object?>(view, "_editTools") == null && Field<object?>(view, "_savedAssets") == null,
                "Building editing must construct only its own tools and rollback state.");
            Call(view, "BeginBuildingEdit", null, EventArgs.Empty);
            Require(ReferenceEquals(buildingTools, Field<FlowLayoutPanel>(view, "_buildingTools")),
                "Repeated Building entry must not duplicate tools or reset the rollback copy.");
            Set(parts[0]!, "Name", "Unsaved building lifecycle change");
            Call(view, "CancelBuilding");
            Released();
            Require(buildingTools.IsDisposed && buildingPicker.IsDisposed && Count(view) == restingControls &&
                Describe(parts) == originalParts, "Leaving Building must dispose controls and restore the transaction.");
        }
        foreach (var session in new[] { new UserSession { RoleName = "Operator" },
            new UserSession { RoleName = "Administrator", IsOfflinePreview = true } })
        {
            using var denied = new FactoryMapView(session, Array.Empty<MeterSnapshot>(), null!);
            Call(denied, "ToggleEditMode", null, EventArgs.Empty);
            Call(denied, "BeginBuildingEdit", null, EventArgs.Empty);
            Require(Field<object?>(denied, "_editTools") == null && Field<object?>(denied, "_buildingTools") == null &&
                Field<object?>(denied, "_savedAssets") == null && Field<object?>(denied, "_savedBuilding") == null,
                "Denied editor entry must not allocate editing controls or transaction copies.");
        }
        Console.WriteLine($"Factory editor lifecycle: normal view excludes {equipmentControls} equipment / {buildingControls} building controls; five enter/cancel cycles dispose all tools and rollback copies without control growth.");
    }

    private static void ValidateFillerAncillaries()
    {
        var models = new[] { "Preform elevator", "Cap elevator", "Cap sorter and chute", "Cap sterilizer", "Bottle unscrambler",
            "Rinser filler capper monoblock", "Air conveyor blower", "HEPA filler canopy", "Product buffer tank",
            "Sanitary product pump", "Sanitary valve manifold", "CIP return skid", "Bottle air knife dryer",
            "Bottle reject station", "Inline checkweigher", "Inline metal detector", "Can seamer",
            "Empty container depalletizer", "Returnable bottle washer", "Foam cleaning station" };
        var shapes = (Dictionary<string, string[]>)Type("FactoryMapItemForm")
            .GetField("Shapes", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var defaults = (IList)StaticView("CreateAssets");
        var originalDefaults = Describe(defaults);
        var placed = (IList)StaticView("CreateAssets");
        placed.Clear();
        foreach (var model in models)
        {
            Require(shapes["Filler Room"].Contains(model), $"Filler ancillary must be selectable in its room category: {model}.");
            var asset = Call(defaults[0]!, "Clone")!;
            Set(asset, "Id", "FILLER-" + placed.Count);
            Set(asset, "Category", "Filler Room"); Set(asset, "Model", model);
            Set(asset, "EquipmentId", ""); Set(asset, "MeterSourceId", "");
            Set(asset, "Source", "No linked acquisition source");
            Set(asset, "Position", new PointF(0.5F, 0.5F));
            Set(asset, "ScaleX", 1.2F); Set(asset, "ScaleY", 0.8F); Set(asset, "ScaleZ", 1.1F);
            Set(asset, "Rotation", 270F); Set(asset, "BodyColor", "#6A99BC");
            placed.Add(asset);
            using var form = (Form)Activator.CreateInstance(Type("FactoryMapItemForm"), new[] { asset, defaults })!;
            Require((string)Field<ComboBox>(form, "_shape").SelectedItem! == model,
                "The actual item editor must restore every new filler shape.");
            Call(form, "ApplyItem", null, EventArgs.Empty);
            Require(Describe(Get(form, "Asset")) == Describe(asset),
                "Editing ancillary properties must retain dimensions, rotation, color and visual-only links.");
        }
        var directory = Path.Combine(Path.GetTempPath(), "eoems-filler-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "layout.json");
            Static("FactoryMapLayoutStore", "SaveAssets", placed, path);
            var loaded = (IList)Static("FactoryMapLayoutStore", "LoadAssets", defaults, path)!;
            Require(Describe(loaded) == Describe(placed), "All filler ancillary shapes and transforms must round-trip in saved maps.");
            Require(Describe(defaults) == originalDefaults, "Adding optional filler equipment must not alter existing system inventory.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Console.WriteLine("Twenty filler-room ancillary models, actual editor, visual-only identities and persistence checks passed.");
    }

    private static void ValidateEquipmentSizes()
    {
        var shapes = (Dictionary<string, string[]>)Type("FactoryMapItemForm")
            .GetField("Shapes", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var asset = ((IList)StaticView("CreateAssets"))[0]!;
        Set(asset, "ScaleX", 1F); Set(asset, "ScaleY", 1F); Set(asset, "ScaleZ", 1F); Set(asset, "Rotation", 0F);
        Set(asset, "IsOpen", false);
        using var bitmap = new Bitmap(1, 1);
        using var graphics = Graphics.FromImage(bitmap);
        var count = 0;
        foreach (var category in shapes)
            foreach (var model in category.Value)
            {
                Set(asset, "Category", category.Key); Set(asset, "Model", model);
                var size = ((float Length, float Width, float Height))Static("FactoryMapEquipmentSizes", "Dimensions", asset)!;
                Require(size.Length > 0 && size.Width > 0 && size.Height > 0 &&
                    float.IsFinite(size.Length + size.Width + size.Height), $"Every library model needs finite metre dimensions: {model}.");
                if (!(bool)Static("FactoryMapEquipmentRenderer", "IsOpeningModel", model)!)
                {
                    using var top = new GraphicsPath(FillMode.Winding);
                    using var front = new GraphicsPath(FillMode.Winding);
                    Func<float, float, float, PointF> topProjection = (x, y, z) => new(x, y);
                    Func<float, float, float, PointF> frontProjection = (x, y, z) => new(x, -z);
                    Static("FactoryMapEquipmentRenderer", "Draw", graphics, asset, topProjection, top);
                    Static("FactoryMapEquipmentRenderer", "Draw", graphics, asset, frontProjection, front);
                    var plan = top.GetBounds();
                    var elevation = front.GetBounds();
                    Require(plan.Width <= size.Length * 8 + 0.01F && plan.Height <= size.Width * 8 + 0.01F &&
                        elevation.Height <= size.Height * 8 + 0.01F,
                        $"Rendered geometry must not exceed its metre-scale envelope: {model}, {plan}, {elevation}, {size}.");
                }
                count++;
            }
        float Area(string model)
        {
            Set(asset, "Model", model);
            var size = ((float Length, float Width, float Height))Static("FactoryMapEquipmentSizes", "Dimensions", asset)!;
            return size.Length * size.Width;
        }
        var generator = Area("Caterpillar generator 810 kVA");
        Require(Area("HP compressor 40 bar") < generator * 0.6F &&
            Area("Atlas Copco compressor 7 bar") < generator * 0.4F &&
            Area("Caterpillar generator 1500 kVA") > generator * 1.4F,
            "The 40-bar and Atlas skids must be smaller than the 810 kVA set; the 1500 kVA set must be larger.");
        Require(Area("Outdoor AC 1.5 HP") < Area("Outdoor AC 2.5 HP") &&
            Area("Outdoor AC 5 HP") < Area("Outdoor AC 10 HP") && Area("Outdoor AC 10 HP") < Area("Outdoor AC 20 HP"),
            "Outdoor AC rated variants must have increasing representative footprints.");
        foreach (var (bare, bundled) in new[] {
            ("HP compressor 40 bar", "HP compressor 40 bar with control and power panels"),
            ("Atlas Copco compressor 7 bar", "Atlas Copco compressor 7 bar with control and power panels"),
            ("Hansa chiller", "Hansa chiller with control and power panels") })
        {
            Set(asset, "Model", bare);
            var bareScale = Static("FactoryMapEquipmentSizes", "ScaleFor", asset);
            Set(asset, "Model", bundled);
            Require(Equals(bareScale, Static("FactoryMapEquipmentSizes", "ScaleFor", asset)),
                "Bundled cabinets must never shrink or enlarge the underlying machine.");
        }
        Set(asset, "Category", "Compressors"); Set(asset, "Model", "HP compressor 40 bar");
        var baseline = (RectangleF)Static("FactoryMapPlacement", "AssetFootprint", asset)!;
        Set(asset, "ScaleX", 1.5F); Set(asset, "ScaleY", 0.75F); Set(asset, "ScaleZ", 1.25F);
        var resized = (RectangleF)Static("FactoryMapPlacement", "AssetFootprint", asset)!;
        Require(Math.Abs(resized.Width / baseline.Width - 1.5F) < 0.001F &&
            Math.Abs(resized.Height / baseline.Height - 0.75F) < 0.001F,
            "Saved independent size overrides must remain effective over the corrected baseline and snapping footprint.");
        var before = Describe(asset);
        using var dialog = (Form)Activator.CreateInstance(Type("FactoryMapItemForm"), new[] { asset, StaticView("EquipmentCatalog") })!;
        Require(Field<Label>(dialog, "_nominalSize").Text == "4.8 x 1.2 x 2.38",
            "The editor must show metre dimensions including each item's saved percentage overrides.");
        Field<NumericUpDown>(dialog, "_scaleX").Value = 100;
        Require(Field<Label>(dialog, "_nominalSize").Text.StartsWith("3.2 x"),
            "Live size text must refresh when percentages change.");
        Require(Describe(asset) == before, "Previewing/resetting sizes must never mutate the original item before Apply.");
        Console.WriteLine($"Factory common metre scale verified for all {count} models, relative sizes, bundled cabinets and saved overrides.");
    }

    public static void RunProcessReferences()
    {
        var defaults = (IList)StaticView("CreateAssets");
        var before = Describe(defaults);
        var shapes = (Dictionary<string, string[]>)Type("FactoryMapItemForm")
            .GetField("Shapes", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var models = new[] { "CIP station - 1 tank", "CIP station - 2 tanks", "CIP station - 3 tanks",
            "Krones-style mixing and carbonation skid", "Water treatment process skid" };
        using var sheet = new Bitmap(1500, 850);
        using var graphics = Graphics.FromImage(sheet);
        graphics.Clear(Color.WhiteSmoke);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = new Font("Segoe UI", 10);
        var previousLength = 0F;
        for (var index = 0; index < models.Length; index++)
        {
            var model = models[index];
            var asset = Call(defaults[0]!, "Clone")!;
            var category = index < 3 || index == 4 ? "Water Systems" : "Filler Room";
            Require(shapes[category].Contains(model), "Reference process equipment must be selectable: " + model);
            Set(asset, "Model", model); Set(asset, "Category", category); Set(asset, "BodyColor", "");
            Set(asset, "EquipmentId", ""); Set(asset, "MeterSourceId", "");
            var dimensions = ((float Length, float Depth, float Height))Static("FactoryMapEquipmentSizes", "Dimensions", asset)!;
            Require(dimensions.Length > 0 && dimensions.Depth > 0 && dimensions.Height > 0,
                "Reference models must have valid metre-scaled geometry.");
            if (index < 3)
            {
                Require(dimensions.Length > previousLength, "CIP one/two/three-tank variants must have increasing physical lengths.");
                previousLength = dimensions.Length;
                var raw = ((float Length, float Depth, float Height))Static("FactoryMapEquipmentRenderer", "RawSize", asset)!;
                Require(raw.Height >= 36, "CIP station must retain elevated tank lids and stainless upper pipework.");
                var tankCount = index + 1;
                Require(raw.Length >= tankCount * 20, "CIP raw envelope must accommodate the selected number of tanks.");
            }
            var original = Describe(asset);
            var column = index % 3; var row = index / 3;
            var scale = Math.Min(400 / (dimensions.Length * 8), 290 / (dimensions.Height * 8));
            Func<float, float, float, PointF> project = (x, y, z) =>
                new(column * 500 + 250 + (x - y * .45F) * scale,
                    row * 420 + 350 + (y * .45F - z) * scale);
            using var hit = new GraphicsPath(FillMode.Winding);
            Static("FactoryMapEquipmentRenderer", "Draw", graphics, asset, project, hit);
            Require(hit.PointCount > 300 && hit.GetBounds().Width > 100, "Reference models need actual detailed 3D geometry.");
            Require(Describe(asset) == original, "Rendering reference models must not mutate equipment.");
            graphics.DrawString(model, font, Brushes.DimGray, column * 500 + 15, row * 420 + 380);
        }
        Require(Describe(defaults) == before, "Adding selectable process shapes must not insert any equipment into the map.");
        var directory = Environment.GetEnvironmentVariable("EOEMS_MAP_EVIDENCE");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            sheet.Save(Path.Combine(directory, "CIP-and-process-reference-library.png"));
        }
        Console.WriteLine("CIP one/two/three tanks and two reference process skids: catalog, metric sizes, detailed render and non-insertion checks passed.");
    }

    public static void RunAppearance()
    {
        ValidateUpsReferences();
        ValidateReferenceFinish();
        var assets = (IList)StaticView("CreateAssets");
        var asset = Call(assets[0]!, "Clone")!;
        Set(asset, "Model", "Transformer"); Set(asset, "Category", "Transformers");
        Set(asset, "BodyColor", "");
        var before = Describe(asset);
        var raw = ((float Length, float Depth, float Height))Static("FactoryMapEquipmentRenderer", "RawSize", asset)!;
        Require(raw == (22F, 17F, 21F), "Transformer detail must keep its original raw envelope and scaling anchor.");
        var nominal = ((float Length, float Depth, float Height))Static("FactoryMapEquipmentSizes", "Nominal", "Transformer")!;
        Require(nominal == (2.5F, 2F, 2.5F), "Transformer physical dimensions must remain unchanged.");
        using var sheet = new Bitmap(1200, 800);
        using var sheetGraphics = Graphics.FromImage(sheet);
        sheetGraphics.Clear(Color.WhiteSmoke);
        for (var index = 0; index < 3; index++)
        {
            using var image = new Bitmap(400, 400);
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.WhiteSmoke);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Set(asset, "BodyColor", index == 2 ? "#BB3344" : "");
            Func<float, float, float, PointF> project = index == 1
                ? (x, y, z) => new(200 + x * 10 - y * 5, 300 + y * 5 - z * 10)
                : (x, y, z) => new(200 + x * 13, 200 + y * 12 - z * 2);
            using var hit = new GraphicsPath(FillMode.Winding);
            Static("FactoryMapEquipmentRenderer", "Draw", graphics, asset, project, hit);
            var brown = 0; var red = 0; var dark = 0;
            for (var y = 0; y < image.Height; y++)
                for (var x = 0; x < image.Width; x++)
                {
                    var c = image.GetPixel(x, y);
                    if (c.R > 65 && c.R > c.G * 1.3 && c.G > c.B * 1.15) brown++;
                    if (c.R > 90 && c.R > c.G * 1.7 && c.R > c.B * 1.3) red++;
                    if (c.R < 85 && c.G < 95 && c.B < 100) dark++;
                }
            Require(brown > 40 && dark > 150, "Transformer bushings, radiators and base must remain legible from above and isometric.");
            if (index == 2) Require(red > 500, "Transformer tank/radiators must respect saved custom body colors.");
            sheetGraphics.DrawImageUnscaled(image, index * 400, 0);
        }
        Set(asset, "BodyColor", "");
        Require(Describe(asset) == before, "Rendering detailed transformers must not mutate saved equipment.");
        var floor = Activator.CreateInstance(Type("FactoryBuildingPart"))!;
        Set(floor, "Kind", "Floor"); Set(floor, "Material", "Concrete");
        Set(floor, "BodyColor", "#99BED5"); Set(floor, "Length", 120F); Set(floor, "Depth", 96F);
        Set(floor, "X", 0F); Set(floor, "Y", 0F); Set(floor, "HeightMeters", .02F);
        using var floorImage = new Bitmap(500, 350);
        using var floorGraphics = Graphics.FromImage(floorImage);
        floorGraphics.Clear(Color.White);
        floorGraphics.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var style in new[] { "Rectangle", "L-shaped" })
        {
            Set(floor, "Style", style);
            var original = Describe(floor);
            using var hit = new GraphicsPath(FillMode.Winding);
            Func<float, float, float, PointF> project = (x, y, z) => new(250 + x * 3, 175 + y * 3);
            Static("FactoryMapBuildingRenderer", "Draw", floorGraphics, floor, Parts(floor), 45, project, hit);
            Require(hit.GetBounds() == new RectangleF(70, 31, 360, 288),
                "Floor styling must not change its footprint/hit extent.");
            Require(Describe(floor) == original, "Floor styling must not mutate room dimensions, positions or colors.");
            if (style == "L-shaped")
            {
                floorGraphics.Clear(Color.White);
                hit.Reset();
                Static("FactoryMapBuildingRenderer", "Draw", floorGraphics, floor, Parts(floor), 45, project, hit);
                Require(floorImage.GetPixel(360, 260).ToArgb() == Color.White.ToArgb(),
                    "Floor gradients and seams must remain clipped out of the L-shaped cutout.");
            }
        }
        sheetGraphics.DrawImageUnscaled(floorImage, 100, 430);
        floorGraphics.Clear(Color.White);
        Set(floor, "Kind", "Road"); Set(floor, "Style", "Unmarked asphalt");
        Set(floor, "Material", "Asphalt"); Set(floor, "BodyColor", "#454B50");
        Set(floor, "Length", 40F); Set(floor, "Depth", 60F);
        Func<float, float, float, PointF> roadProject = (x, y, z) => new(100 + x * 2, 100 + y * 2);
        foreach (var x in new[] { 0F, 40F })
        {
            Set(floor, "X", x / 1365);
            using var hit = new GraphicsPath();
            Static("FactoryMapBuildingRenderer", "Draw", floorGraphics, floor, Parts(floor), 45, roadProject, hit);
        }
        for (var y = 50; y < 150; y++)
            for (var x = 70; x < 210; x++)
                Require(floorImage.GetPixel(x, y).ToArgb() == Color.FromArgb(69, 75, 80).ToArgb(),
                    "Adjacent unmarked asphalt patches must form one solid surface without outlines, stripes or anti-alias seams.");
        var directory = Environment.GetEnvironmentVariable("EOEMS_MAP_EVIDENCE");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            sheet.Save(Path.Combine(directory, "Transformer-and-floor-appearance.png"));
        }
        Console.WriteLine("Transformer reference detail, fixed raw/physical size, custom colors and clipped floor styling passed.");
    }

    private static void ValidateReferenceFinish()
    {
        using var bitmap = new Bitmap(500, 300);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var wall = Activator.CreateInstance(Type("FactoryBuildingPart"))!;
        Set(wall, "Kind", "Wall"); Set(wall, "Material", "Concrete"); Set(wall, "BodyColor", "#8DAABD");
        Set(wall, "X", 0F); Set(wall, "Y", 0F); Set(wall, "Length", 100F); Set(wall, "Depth", 6F); Set(wall, "HeightMeters", 3F);
        Func<float, float, float, PointF> top = (x, y, z) => new(250 + x * 3, 150 + y * 3);
        graphics.Clear(Color.FromArgb(80, 110, 135));
        using (var hit = new GraphicsPath())
            Static("FactoryMapBuildingRenderer", "Draw", graphics, wall, Parts(wall), 22, top, hit);
        var cap = bitmap.GetPixel(250, 150);
        Require(cap.R > 190 && cap.G > 190 && cap.B > 190 && Math.Abs(cap.R - cap.B) < 25,
            "Top-view wall caps must be bright neutral cutaway material, not translucent blue strips.");
        var original = Describe(wall);
        graphics.Clear(Color.White);
        using (var hit = new GraphicsPath())
            Static("FactoryMapBuildingRenderer", "Draw", graphics, wall, Parts(wall), 0, top, hit);
        Require(bitmap.GetPixel(250, 150).ToArgb() == Color.White.ToArgb(),
            "Zero wall opacity must remain invisible, including the new cap.");
        Set(wall, "Material", "Glass");
        graphics.Clear(Color.FromArgb(80, 110, 135));
        using (var hit = new GraphicsPath())
            Static("FactoryMapBuildingRenderer", "Draw", graphics, wall, Parts(wall), 22, top, hit);
        Require(bitmap.GetPixel(250, 150).R < cap.R - 50, "Glass must not become an opaque cutaway wall.");
        Set(wall, "Material", "Concrete");
        Require(Describe(wall) == original, "Material rendering must not modify wall geometry or persisted body colors.");
        var floor = Activator.CreateInstance(Type("FactoryBuildingPart"))!;
        Set(floor, "Kind", "Floor"); Set(floor, "Material", "Concrete"); Set(floor, "BodyColor", "#EDB4D3");
        Set(floor, "X", 0F); Set(floor, "Y", 0F); Set(floor, "Length", 120F); Set(floor, "Depth", 80F);
        graphics.Clear(Color.White);
        using (var hit = new GraphicsPath())
            Static("FactoryMapBuildingRenderer", "Draw", graphics, floor, Parts(floor), 22, top, hit);
        var center = bitmap.GetPixel(250, 150);
        Require(center.R > center.G + 30 && center.B > center.G + 15,
            "Reference production floor must retain a rose palette rather than grey/purple oversaturation.");
        for (var x = 170; x < 330; x++)
        {
            var a = bitmap.GetPixel(x, 150); var b = bitmap.GetPixel(x + 1, 150);
            Require(Math.Abs(a.R - b.R) <= 2 && Math.Abs(a.G - b.G) <= 2 && Math.Abs(a.B - b.B) <= 2,
                "Plain floors must remain smooth and grid-free across their interior.");
        }
        var baseline = Environment.GetEnvironmentVariable("EOEMS_MAP_APPEARANCE_BASELINE");
        if (!string.IsNullOrEmpty(baseline))
        {
            var context = new System.Runtime.Loader.AssemblyLoadContext("appearance-envelope-baseline", isCollectible: true);
            try
            {
                var previous = context.LoadFromAssemblyPath(Path.GetFullPath(baseline));
                var previousAssetType = previous.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapAsset")!;
                var previousRenderer = previous.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapEquipmentRenderer")!;
                var shapes = (Dictionary<string, string[]>)Type("FactoryMapItemForm").GetField("Shapes", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
                var checkedModels = 0;
                foreach (var category in shapes)
                foreach (var model in category.Value.Distinct())
                {
                    if ((bool)Static("FactoryMapEquipmentRenderer", "IsOpeningModel", model)!) continue;
                    var asset = Activator.CreateInstance(Type("FactoryMapAsset"))!;
                    var oldAsset = Activator.CreateInstance(previousAssetType)!;
                    foreach (var item in new[] { asset, oldAsset })
                    {
                        Set(item, "Model", model); Set(item, "Category", category.Key); Set(item, "ConveyorLength", 60F);
                    }
                    var size = Static("FactoryMapEquipmentRenderer", "RawSize", asset)!;
                    var oldSize = previousRenderer.GetMethod("RawSize", BindingFlags.Static | BindingFlags.Public)!.Invoke(null, new[] { oldAsset })!;
                    Require(size.Equals(oldSize), model + ": reference detailing must preserve the installed raw equipment envelope exactly.");
                    checkedModels++;
                }
                Console.WriteLine($"Reference finish: exact installed raw envelopes preserved for {checkedModels} category/model choices.");
            }
            finally { context.Unload(); }
        }
        Console.WriteLine("Reference wall caps, opacity/glass, plain rose floor and rendering non-mutation checks passed.");
    }

    private static void ValidateUpsReferences()
    {
        var assets = (IList)StaticView("CreateAssets");
        var asset = Call(assets[0]!, "Clone")!;
        Set(asset, "Category", "UPS"); Set(asset, "Rotation", 0F);
        Set(asset, "ScaleX", 1F); Set(asset, "ScaleY", 1F); Set(asset, "ScaleZ", 1F);
        using var sheet = new Bitmap(1000, 900);
        using var sheetGraphics = Graphics.FromImage(sheet);
        sheetGraphics.Clear(Color.WhiteSmoke);
        var index = 0;
        foreach (var model in new[] { "Vertiv EXL 500 kVA", "Vertiv NXL 800 kVA" })
        {
            Set(asset, "Model", model); Set(asset, "BodyColor", "");
            var raw = Static("FactoryMapEquipmentRenderer", "RawSize", asset)!;
            var rawHeight = (float)raw.GetType().GetField("Item3")!.GetValue(raw)!;
            Require(Math.Abs(rawHeight - (index == 0 ? 31.6F : 31F)) < .001,
                $"{model} has a solid cabinet roof, without the previous rooftop fans.");
            using var front = new Bitmap(500, 450);
            using var graphics = Graphics.FromImage(front);
            graphics.Clear(Color.WhiteSmoke);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Func<float, float, float, PointF> projection = (x, y, z) => new(250 + x * 14, 380 - z * 14);
            using var hit = new GraphicsPath(FillMode.Winding);
            Static("FactoryMapEquipmentRenderer", "Draw", graphics, asset, projection, hit);
            var dark = 0;
            var display = new List<Point>();
            for (var y = 0; y < front.Height; y++)
                for (var x = 0; x < front.Width; x++)
                {
                    var pixel = front.GetPixel(x, y);
                    if (pixel.R < 80 && pixel.G < 80 && pixel.B < 80) dark++;
                    if (index == 0 ? pixel.G > 130 && pixel.G > pixel.R * 1.2 && pixel.G > pixel.B * 1.2
                        : pixel.B > pixel.R * 1.5 && pixel.B > pixel.G * 1.3) display.Add(new Point(x, y));
                }
            Require(dark > 10000, $"{model} must have the dark cabinet/ventilation front shown in the reference.");
            var bounds = hit.GetBounds();
            Require(display.Count > 10 && display.Average(point => point.Y) < bounds.Top + bounds.Height * .4,
                $"{model} must retain its visible upper-door HMI/status strip (pixels={display.Count}, meanY={(display.Count == 0 ? 0 : display.Average(point => point.Y)):F1}, top={bounds.Top:F1}, height={bounds.Height:F1}).");
            var displayPosition = (display.Average(point => point.X) - bounds.Left) / bounds.Width;
            Require(index == 0 ? displayPosition < .3 : displayPosition > .25 && displayPosition < .5,
                $"{model} HMI belongs on the left EXL door / second of four NXL doors.");
            sheetGraphics.DrawImageUnscaled(front, index * 500, 0);
            Func<float, float, float, PointF> iso = (x, y, z) =>
                new(250 + index * 500 + x * 12 - y * 6, 825 + y * 4 - z * 12);
            Static("FactoryMapEquipmentRenderer", "Draw", sheetGraphics, asset, iso, hit);
            Set(asset, "BodyColor", "#BB3344");
            graphics.Clear(Color.WhiteSmoke);
            Static("FactoryMapEquipmentRenderer", "Draw", graphics, asset, projection, hit);
            var red = 0;
            for (var y = 0; y < front.Height; y++)
                for (var x = 0; x < front.Width; x++)
                {
                    var pixel = front.GetPixel(x, y);
                    if (pixel.R > pixel.G * 1.8 && pixel.R > pixel.B * 1.4) red++;
                }
            Require(red > 1000, $"{model} still respects the user's cabinet color without recoloring its display.");
            index++;
        }
        var directory = Environment.GetEnvironmentVariable("EOEMS_MAP_EVIDENCE");
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            sheet.Save(Path.Combine(directory, "UPS-reference-front-and-isometric.png"));
        }
        Console.WriteLine("EXL two-door / NXL four-door reference silhouettes, HMI placement, solid roofs and custom colors passed.");
    }

    private static void ValidateHansaChiller()
    {
        var assets = (IList)StaticView("CreateAssets");
        var asset = Call(assets[0]!, "Clone")!;
        Set(asset, "Category", "Chillers"); Set(asset, "Model", "Hansa chiller");
        Set(asset, "EquipmentId", "CHILLER-1");
        Set(asset, "Rotation", 90F); Set(asset, "ScaleX", 1.2F);
        assets.Clear(); assets.Add(asset);
        var directory = Path.Combine(Path.GetTempPath(), "eoems-hansa-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "layout.json");
            Static("FactoryMapLayoutStore", "SaveAssets", assets, path);
            var current = File.ReadAllText(path);
            var loaded = (IList)Static("FactoryMapLayoutStore", "LoadAssets", assets, path)!;
            Require(Describe(loaded) == Describe(assets), "New Hansa selections must round-trip without being changed to generic chillers.");
            var legacy = System.Text.Json.Nodes.JsonNode.Parse(current)!;
            legacy.AsObject().Remove("EquipmentLibraryRevision");
            File.WriteAllText(path, legacy.ToJsonString());
            loaded = (IList)Static("FactoryMapLayoutStore", "LoadAssets", assets, path)!;
            var expected = Call(asset, "Clone")!;
            Set(expected, "Model", "Chiller");
            Require(Describe(loaded[0]!) == Describe(expected),
                "Old fan units must migrate to Chiller without altering user names, links, size, rotation or positions.");
            legacy["Assets"]![0]!["Model"] = "Hansa chiller with control and power panels";
            File.WriteAllText(path, legacy.ToJsonString());
            loaded = (IList)Static("FactoryMapLayoutStore", "LoadAssets", assets, path)!;
            Set(expected, "Model", "Chiller with control and power panels");
            Require(Describe(loaded[0]!) == Describe(expected), "Legacy with-panels units must retain their generic fan unit and external cabinets.");
            Require(!File.ReadAllText(path).Contains("EquipmentLibraryRevision"),
                "Loading a legacy chiller must not rewrite the user's saved map.");
            Static("FactoryMapLayoutStore", "SaveAssets", loaded, path);
            Require(Describe(Static("FactoryMapLayoutStore", "LoadAssets", assets, path)!) == Describe(loaded),
                "Generic chiller migration must persist after Save.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        using var bitmap = new Bitmap(500, 400);
        using var graphics = Graphics.FromImage(bitmap);
        Set(asset, "Rotation", 0F); Set(asset, "ScaleX", 1F);
        Set(asset, "Model", "Hansa chiller with control and power panels");
        var renderer = Type("FactoryMapEquipmentRenderer");
        using (var pumps = new GraphicsPath(FillMode.Winding))
        {
            Func<float, float, float, PointF> top = (x, y, z) => new(x, y);
            var pumpRenderer = Activator.CreateInstance(renderer, BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object?[] { graphics, top, pumps, "", null, false, null }, null)!;
            Call(pumpRenderer, "HansaPumps");
            foreach (var center in new[] { -14F, 12F })
                Require(pumps.PathPoints.Any(point => Math.Abs(point.X - (center - 4 * 0.48F)) < 0.001F &&
                    Math.Abs(point.Y - (15 - 5 * 0.48F)) < 0.001F),
                    "Hansa must contain both complete pump/motor assemblies on the front service skid.");
        }
        Func<float, float, float, PointF> projection = (x, y, z) => new(220 + x * 7, 330 + y * 1.5F - z * 7);
        using var hit = new GraphicsPath(FillMode.Winding);
        Static("FactoryMapEquipmentRenderer", "Draw", graphics, asset, projection, hit);
        var white = 0;
        var blue = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A > 0 && pixel.R > 165 && pixel.G > 165 && pixel.B > 165 && Math.Abs(pixel.R - pixel.B) < 15) white++;
                if (pixel.A > 0 && pixel.B > pixel.R * 1.5 && pixel.B > pixel.G * 1.2) blue++;
            }
        Require(white > 300 && blue > 500, "Reference Hansa must render a blue machine and a visibly white panel.");
        Set(asset, "BodyColor", "#AA2255");
        graphics.Clear(Color.Transparent);
        Static("FactoryMapEquipmentRenderer", "Draw", graphics, asset, projection, hit);
        var recoloredWhite = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A > 0 && pixel.R > 165 && pixel.G > 165 && pixel.B > 165 && Math.Abs(pixel.R - pixel.B) < 15) recoloredWhite++;
            }
        Require(recoloredWhite >= white * 0.9, "Changing the Hansa body color must preserve its white control/power cabinet.");
        Console.WriteLine("Hansa reference shape, blue body/white cabinet and non-destructive legacy fan-unit migration checks passed.");
    }

    private static void ValidateMeterLinks()
    {
        using var view = new FactoryMapView(new UserSession { RoleName = "Administrator" }, Array.Empty<MeterSnapshot>(), null!);
        var assets = Field<IList>(view, "_assets");
        var panel = Call(assets[0]!, "Clone")!;
        Set(panel, "Category", "Electrical Panels"); Set(panel, "Model", "ACB panel with energy meter");
        Set(panel, "EquipmentId", "TRANSFORMER-T1"); Set(panel, "MeterSourceId", "T2");
        var first = new MeterSnapshot { TransformerName = "T1", Quality = MeterQuality.Good, CapturedAtUtc = DateTime.UtcNow, DecodedValues = new float[32] };
        var second = new MeterSnapshot { TransformerName = "T2", Quality = MeterQuality.Good, CapturedAtUtc = DateTime.UtcNow, DecodedValues = new float[32] };
        first.DecodedValues[10] = 999;
        second.DecodedValues[10] = 123;
        IReadOnlyList<MeterSnapshot> snapshots = new[] { first, second };
        Require(ReferenceEquals(Static("FactoryMapMeterLink", "Find", panel, snapshots), second),
            "Panel meter selection must resolve independently from attached equipment.");
        second.Quality = MeterQuality.Timeout;
        Require(Static("FactoryMapMeterLink", "Find", panel, snapshots) == null, "Bad meter quality must not fall back to the equipment's good meter.");
        second.Quality = MeterQuality.Good;
        Set(panel, "MeterSourceId", "missing");
        Require(Static("FactoryMapMeterLink", "Find", panel, snapshots) == null, "Missing meter IDs must not fall back to a different source.");
        Set(panel, "MeterSourceId", "");
        Require(Static("FactoryMapMeterLink", "Find", panel, snapshots) == null, "A panel with no independent meter must not borrow equipment readings.");
        Set(panel, "MeterSourceId", "T2");
        Call(view, "SetReadings", snapshots, null);
        Call(view, "UpdateSelection", panel);
        Require(Field<Label>(view, "_selectedDetails").Text.Contains("123.0 kW") &&
            !Field<Label>(view, "_selectedDetails").Text.Contains("999.0"), "Panel inspector must display only its selected meter reading.");
        second.Quality = MeterQuality.Timeout;
        Call(view, "UpdateSelection", panel);
        Require(Field<Label>(view, "_selectedDetails").Text.Contains("Load: --") &&
            !Field<Label>(view, "_selectedDetails").Text.Contains("123.0") && !Field<Label>(view, "_selectedDetails").Text.Contains("999.0"),
            "The live inspector must clear unavailable/bad independent readings rather than retain or substitute values.");
        second.Quality = MeterQuality.Good;
        var catalog = StaticView("EquipmentCatalog");
        using var dialog = (Form)Activator.CreateInstance(Type("FactoryMapItemForm"), new[] { panel, catalog })!;
        var choices = new List<KeyValuePair<string, string>> { new("T1", "Configured T1"), new("T2", "Configured T2") };
        Call(dialog, "SetMeterChoices", choices);
        Require(Field<ComboBox>(dialog, "_meter").SelectedIndex == 2, "Existing independent meter selection must be restored in the editor.");
        var generatorIndex = ((IList)catalog).Cast<object>().Select((item, index) => (item, index))
            .First(entry => (string)Get(entry.item, "Category") == "Generators").index;
        Field<ComboBox>(dialog, "_link").SelectedIndex = generatorIndex + 1;
        Require((string)Field<ComboBox>(dialog, "_shape").SelectedItem! == "ACB panel with energy meter",
            "Attaching a metered panel to equipment must not replace the panel model.");
        Call(dialog, "ApplyItem", null, EventArgs.Empty);
        var edited = Get(dialog, "Asset");
        Require((string)Get(edited, "MeterSourceId") == "T2" && ((string)Get(edited, "EquipmentId")).StartsWith("GEN-"),
            "The form must persist separate equipment and configured meter identities.");
        using var unavailable = (Form)Activator.CreateInstance(Type("FactoryMapItemForm"), new[] { edited, catalog })!;
        Call(unavailable, "SetMeterChoices", new List<KeyValuePair<string, string>>());
        Require(Field<ComboBox>(unavailable, "_meter").Text.Contains("Unavailable meter"), "Unavailable saved meter IDs must be explicit, not silently cleared.");
        Call(unavailable, "ApplyItem", null, EventArgs.Empty);
        Require((string)Get(Get(unavailable, "Asset"), "MeterSourceId") == "T2", "Editing another property must preserve an unavailable meter ID.");
        var directory = Path.Combine(Path.GetTempPath(), "eoems-meter-link-" + Guid.NewGuid().ToString("N"));
        try
        {
            var list = (IList)StaticView("CreateAssets");
            list.Clear(); list.Add(edited);
            var path = Path.Combine(directory, "layout.json");
            Static("FactoryMapLayoutStore", "SaveAssets", list, path);
            var loaded = (IList)Static("FactoryMapLayoutStore", "LoadAssets", list, path)!;
            Require(Describe(loaded) == Describe(list), "Independent meter IDs must survive complete layout persistence.");
            var saved = File.ReadAllText(path);
            Set(edited, "Model", "Floor panel");
            try
            {
                Static("FactoryMapLayoutStore", "SaveAssets", list, path);
                throw new InvalidOperationException("An unmetered symbol accepted an independent meter source.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { }
            Require(File.ReadAllText(path) == saved, "Rejected meter links must not overwrite saved layouts.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static object StaticView(string method) =>
        typeof(FactoryMapView).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;

    private static void ValidateClipboard()
    {
        using var view = new FactoryMapView(new UserSession { RoleName = "Administrator" }, Array.Empty<MeterSnapshot>(), null!);
        var canvas = Field<Control>(view, "_canvas");
        var assets = Field<IList>(view, "_assets");
        var original = Describe(assets);
        Call(view, "ToggleEditMode", null, EventArgs.Empty);
        var selected = assets[0]!;
        Set(selected, "Category", "Electrical Panels"); Set(selected, "Model", "ACB panel with energy meter");
        Set(selected, "MeterSourceId", "T2");
        Call(canvas, "SelectAsset", selected);
        var count = assets.Count;
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.C));
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.V));
        var pasted = assets[assets.Count - 1]!;
        Require(assets.Count == count + 1 && (string)Get(pasted, "Id") != (string)Get(selected, "Id") &&
            (string)Get(pasted, "EquipmentId") == "" && (string)Get(pasted, "MeterSourceId") == "" &&
            (string)Get(pasted, "Model") == (string)Get(selected, "Model"), "Ctrl+C/V must create an independent unlinked visual copy.");
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.V));
        Require((PointF)Get(assets[assets.Count - 1]!, "Position") != (PointF)Get(pasted, "Position"), "Repeated paste must offset each copy.");
        Call(canvas, "SelectAsset", (object?)null);
        count = assets.Count;
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.D));
        Require(assets.Count == count, "Duplicate with no selection must not paste an old clipboard entry.");
        Call(view, "CancelLayout", null, EventArgs.Empty);
        Require(Describe(assets) == original, "Cancel must roll back clipboard additions completely.");
        Call(view, "BeginBuildingEdit", null, EventArgs.Empty);
        var parts = Field<IList>(view, "_building");
        var wall = Activator.CreateInstance(Type("FactoryBuildingPart"))!;
        var door = Activator.CreateInstance(Type("FactoryBuildingPart"))!;
        Set(door, "Kind", "Door"); Set(door, "Style", "Rapid roll-up"); Set(door, "Material", "Steel");
        Set(door, "ParentWallId", Get(wall, "Id")); Set(door, "Length", 18F); Set(door, "HeightMeters", 2F);
        parts.Add(wall); parts.Add(door);
        Call(canvas, "SelectBuildingPart", wall);
        count = parts.Count;
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.C));
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.V));
        var copiedWall = parts[count]!;
        var copiedDoor = parts[count + 1]!;
        Require(parts.Count == count + 2 && (string)Get(copiedDoor, "ParentWallId") == (string)Get(copiedWall, "Id") &&
            (string)Get(copiedWall, "Id") != (string)Get(wall, "Id") && (string)Get(copiedDoor, "RoomId") == "",
            "Copying walls must remap attached openings to new IDs without joining the old room.");
        Static("FactoryMapBuildingStore", "Validate", parts);
        Call(canvas, "SelectBuildingPart", door);
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.C));
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.V));
        Require((string)Get(parts[parts.Count - 1]!, "ParentWallId") == "", "Copying just an attached opening must detach its resolved visual copy.");
        Call(view, "CancelBuilding");
        using var denied = new FactoryMapView(new UserSession { RoleName = "Operator" }, Array.Empty<MeterSnapshot>(), null!);
        var deniedBefore = Describe(Field<IList>(denied, "_assets"));
        Call(denied, "HandleEditCommand", "Paste");
        Require(Describe(Field<IList>(denied, "_assets")) == deniedBefore, "Clipboard commands must respect editing permissions.");
    }

    private static void ValidateNavigation()
    {
        using var host = new Form { Size = new Size(1440, 900) };
        using var view = new FactoryMapView(new UserSession { RoleName = "Administrator" }, Array.Empty<MeterSnapshot>(), null!);
        host.Controls.Add(view); host.Show(); Application.DoEvents();
        var canvas = Field<Control>(view, "_canvas");
        Call(canvas, "SetTopView");
        var cursor = new Point(canvas.Width / 3, canvas.Height / 2);
        var before = (PointF)Call(canvas, "ScreenToMap", cursor)!;
        var oldZoom = Field<float>(canvas, "_zoom");
        Call(canvas, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, cursor.X, cursor.Y, 120));
        Thread.Sleep(20);
        Call(canvas, "AdvanceZoom");
        Require(Field<float>(canvas, "_zoom") > oldZoom && Field<float>(canvas, "_zoom") < oldZoom * 1.16F,
            "Smooth wheel zoom must begin on the first timer frame and ease toward its target.");
        Thread.Sleep(150);
        Call(canvas, "AdvanceZoom");
        var after = (PointF)Call(canvas, "ScreenToMap", cursor)!;
        Require(Math.Abs(after.X - before.X) < 0.00001 && Math.Abs(after.Y - before.Y) < 0.00001 &&
            Math.Abs(Field<float>(canvas, "_zoom") - oldZoom * 1.16F) < 0.00001,
            "Every animation step must retain the exact cursor map anchor and finish at the intended zoom.");
        foreach (var level in new[] { 1F, 12F, 30F })
        {
            Call(canvas, "ResetView");
            Call(canvas, "ZoomBy", level);
            Application.DoEvents();
            var anchor = (PointF)Call(canvas, "ScreenToMap", cursor)!;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Call(canvas, "OnMouseWheel", new MouseEventArgs(MouseButtons.None, 0, cursor.X, cursor.Y, 120));
            var zoomTimer = Field<System.Windows.Forms.Timer>(canvas, "_zoomTimer");
            while (zoomTimer.Enabled && watch.ElapsedMilliseconds < 500)
            {
                Application.DoEvents();
                Thread.Sleep(1);
            }
            var elapsed = watch.Elapsed.TotalMilliseconds;
            var zoomAnchor = (PointF)Call(canvas, "ScreenToMap", cursor)!;
            Require(!zoomTimer.Enabled && elapsed < 300 && Math.Abs(Field<float>(canvas, "_zoom") - level * 1.16F) < 0.0001 &&
                Math.Abs(zoomAnchor.X - anchor.X) < 0.00001 && Math.Abs(zoomAnchor.Y - anchor.Y) < 0.00001,
                "Real message-pump wheel animation must settle within 300ms with the exact anchor at every zoom level.");
            Console.WriteLine($"Message-pump wheel animation at {level}x completed in {elapsed:F1}ms.");
        }
        Call(canvas, "ZoomBy", 1000F);
        Require(Field<float>(canvas, "_zoom") == 40F, "Close inspection zoom must reach exactly 40x.");
        Call(canvas, "ZoomBy", 0.00001F);
        Require(Field<float>(canvas, "_zoom") == 0.2F, "Overview zoom must stop at exactly 0.2x.");
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Home));
        Require(Field<float>(canvas, "_zoom") == 1F, "Home must restore fit.");
        var pan = Field<PointF>(canvas, "_pan");
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.W));
        Require(Field<PointF>(canvas, "_pan").Y == pan.Y + 30, "WASD navigation must pan independently of precision arrow editing.");
        var selected = Field<IList>(view, "_assets")[0]!;
        Call(canvas, "SelectAsset", selected);
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.F));
        var centered = (PointF)Call(canvas, "ScreenToMap", new Point(canvas.Width / 2, canvas.Height / 2))!;
        Require(Math.Abs(centered.X - ((PointF)Get(selected, "Position")).X) < 0.002, "Focus must center the selected equipment.");
        var parent = canvas.Parent;
        var closed = false;
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var full = Application.OpenForms.Cast<Form>().Single(form => form.Text.StartsWith("Factory Map"));
            Require(canvas.Parent == full && full.FormBorderStyle == FormBorderStyle.None, "Fullscreen must host only the map in a borderless window.");
            Call(full, "OnKeyDown", new KeyEventArgs(Keys.Escape));
            closed = true;
        };
        timer.Start();
        Call(view, "ShowFullScreen");
        Require(closed && canvas.Parent == parent && canvas.Dock == DockStyle.Fill, "Escape must leave fullscreen and restore the original page layout.");
        host.Close();
    }

    public static void Render(string directory)
    {
        FactoryMapMotionValidation.Render(directory);
        RenderEquipmentScale(directory);
        using var bitmap = new Bitmap(1000, 620);
        using var graphics = Graphics.FromImage(bitmap);
        using var font = new Font("Segoe UI", 11, FontStyle.Bold);
        graphics.Clear(Color.FromArgb(242, 246, 249));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var index = 0;
        foreach (var open in new[] { false, true })
        {
            var door = Activator.CreateInstance(Type("FactoryBuildingPart"))!;
            Set(door, "Kind", "Door"); Set(door, "Style", "Rapid roll-up"); Set(door, "Material", "Steel");
            Set(door, "Length", 40F); Set(door, "Depth", 2F); Set(door, "HeightMeters", 4F);
            Set(door, "X", 0F); Set(door, "Y", 0F); Set(door, "BodyColor", "#2469AA"); Set(door, "IsOpen", open);
            var origin = 250 + index * 500;
            Func<float, float, float, PointF> projection = (x, y, z) => new(origin + x * 7 - y, 275 + y * 0.6F - z * 6);
            using var hit = new GraphicsPath(FillMode.Winding);
            Static("FactoryMapBuildingRenderer", "Draw", graphics, door, Parts(door), 40, projection, hit);
            graphics.DrawString("Rapid curtain / " + (open ? "rolled up" : "closed, twin clear windows"), font, Brushes.Black, origin - 225, 15);
            index++;
        }

        index = 0;
        foreach (var style in new[] { "Solid line", "Dashed line", "Walkway", "Keep clear hatching" })
        {
            var part = Activator.CreateInstance(Type("FactoryBuildingPart"))!;
            Set(part, "Kind", "Marking"); Set(part, "Style", style); Set(part, "Material", "Epoxy");
            Set(part, "Length", 90F); Set(part, "Depth", style.Contains("line") ? 2F : 20F);
            Set(part, "X", 0F); Set(part, "Y", 0F); Set(part, "HeightMeters", 0.03F); Set(part, "BodyColor", "#D5A620");
            var originY = 350 + index * 65;
            Func<float, float, float, PointF> projection = (x, y, z) => new(350 + x * 5, originY + y * 1.8F);
            using var hit = new GraphicsPath(FillMode.Winding);
            Static("FactoryMapBuildingRenderer", "Draw", graphics, part, Parts(part), 40, projection, hit);
            graphics.DrawString(style, font, Brushes.Black, 650, originY - 12);
            index++;
        }
        bitmap.Save(Path.Combine(directory, "FactoryMap-CurtainAndMarkings.png"));
    }

    private static void RenderEquipmentScale(string directory)
    {
        using var bitmap = new Bitmap(1400, 1200);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(242, 246, 249));
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = new Font("Segoe UI", 11);
        graphics.DrawString("COMMON SCALE: 1 metre = 40 pixels; representative dimensions, before custom overrides",
            font, Brushes.Black, 20, 15);
        var asset = ((IList)StaticView("CreateAssets"))[0]!;
        Set(asset, "ScaleX", 1F); Set(asset, "ScaleY", 1F); Set(asset, "ScaleZ", 1F);
        var models = new[] {
            ("Generators", "Caterpillar generator 810 kVA"), ("Generators", "Caterpillar generator 1500 kVA"),
            ("Compressors", "HP compressor 40 bar"), ("Compressors", "Atlas Copco compressor 7 bar"),
            ("UPS", "Vertiv EXL 500 kVA"), ("UPS", "Vertiv NXL 800 kVA"),
            ("Chillers", "Hansa chiller"), ("Electrical Panels", "ACB panel with energy meter"),
            ("HVAC", "Outdoor AC 1.5 HP"), ("HVAC", "Outdoor AC 20 HP"),
            ("Water Systems", "Ultrafiltration skid"), ("Battery Rooms", "Battery rack - 4 levels"),
            ("Chillers", "Chiller"), ("Chillers", "Chiller with control and power panels"),
            ("Chillers", "Hansa chiller"), ("Chillers", "Hansa chiller with control and power panels")
        };
        for (var index = 0; index < models.Length; index++)
        {
            var (category, model) = models[index];
            Set(asset, "Category", category); Set(asset, "Model", model);
            var cx = 180 + index % 4 * 345;
            var cy = 215 + index / 4 * 275;
            using var hit = new GraphicsPath(FillMode.Winding);
            Func<float, float, float, PointF> project = (x, y, z) =>
                new(cx + (x - y) * 0.707F * 5, cy + ((x + y) * 0.354F - z * 0.866F) * 5);
            Static("FactoryMapEquipmentRenderer", "Draw", graphics, asset, project, hit);
            var size = ((float Length, float Width, float Height))Static("FactoryMapEquipmentSizes", "Dimensions", asset)!;
            graphics.DrawString($"{model}\n{size.Length:0.##} x {size.Width:0.##} x {size.Height:0.##} m",
                font, Brushes.Black, new RectangleF(cx - 160, cy + 40, 325, 65));
        }
        bitmap.Save(Path.Combine(directory, "FactoryMap-CommonScale.png"));
    }

    private static void ValidateMarkingsAndCurtain()
    {
        var assets = (IList)StaticView("CreateAssets");
        var sample = assets[0]!;
        foreach (var (category, bare, bundled) in new[] {
            ("Compressors", "HP compressor 40 bar", "HP compressor 40 bar with control and power panels"),
            ("Compressors", "Atlas Copco compressor 7 bar", "Atlas Copco compressor 7 bar with control and power panels"),
            ("Chillers", "Hansa chiller", "Hansa chiller with control and power panels") })
        {
            Set(sample, "Category", category); Set(sample, "Model", bare); Set(sample, "Rotation", 0F);
            var bareBounds = (RectangleF)Static("FactoryMapPlacement", "AssetFootprint", sample)!;
            Set(sample, "Model", bundled);
            var fullBounds = (RectangleF)Static("FactoryMapPlacement", "AssetFootprint", sample)!;
            Require(fullBounds.Width * fullBounds.Height > bareBounds.Width * bareBounds.Height * 1.15,
                "With-panel options must add actual control and power cabinets to the selectable geometry.");
        }
        var styles = ((Dictionary<string, string[]>)Type("FactoryMapBuildingStore").GetField("Styles",
            BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)["Marking"];
        using var bitmap = new Bitmap(400, 300);
        using var graphics = Graphics.FromImage(bitmap);
        foreach (var style in styles)
        {
            var part = Activator.CreateInstance(Type("FactoryBuildingPart"))!;
            Set(part, "Kind", "Marking"); Set(part, "Style", style); Set(part, "Material", "Epoxy");
            Set(part, "Length", 100F); Set(part, "Depth", style.Contains("line") ? 2F : 20F);
            Set(part, "X", 0F); Set(part, "Y", 0F); Set(part, "HeightMeters", 0.03F); Set(part, "BodyColor", "#FF2266");
            if (style == "Freehand")
                Set(part, "DrawingPoints", new List<PointF> { new(-.5F, 0), new(.5F, 0) });
            Static("FactoryMapBuildingStore", "Validate", Parts(part));
            using var hit = new GraphicsPath(FillMode.Winding);
            Func<float, float, float, PointF> top = (x, y, z) => new(200 + x, 150 + y);
            Static("FactoryMapBuildingRenderer", "Draw", graphics, part, Parts(part), 40, top, hit);
            var outline = style is "Rectangle" or "Ellipse";
            Require(hit.IsVisible(200, outline ? 140 : 150), "Every marking style needs usable selectable geometry.");
            if (outline) Require(!hit.IsVisible(200, 150), "Outline shapes must not intercept clicks in their empty center.");
        }
        using (var form = (Form)Activator.CreateInstance(Type("FactoryMapBuildingForm"),
            new object[] { Activator.CreateInstance(Type("FactoryBuildingPart"))!, Parts(), true })!)
        {
            Field<ComboBox>(form, "_kind").SelectedItem = "Marking";
            Field<ComboBox>(form, "_style").SelectedItem = "Walkway";
            Require(Field<NumericUpDown>(form, "_depth").Value >= 20, "Choosing a walkway should give it a useful default width, not a thin line.");
            Call(form, "ApplyPart", null, EventArgs.Empty);
            Require(form.DialogResult == DialogResult.OK && (string)Get(Get(form, "Part"), "Kind") == "Marking",
                "Floor markings must be addable through the actual Building form.");
        }
        var door = Activator.CreateInstance(Type("FactoryBuildingPart"))!;
        Set(door, "Kind", "Door"); Set(door, "Style", "Rapid roll-up"); Set(door, "Material", "Steel");
        Set(door, "Length", 40F); Set(door, "Depth", 2F); Set(door, "HeightMeters", 4F);
        Set(door, "X", 0F); Set(door, "Y", 0F); Set(door, "BodyColor", "#AA2255");
        Func<float, float, float, PointF> front = (x, y, z) => new(200 + x * 5, 250 - z * 5);
        graphics.Clear(Color.White);
        using var closedHit = new GraphicsPath(FillMode.Winding);
        Static("FactoryMapBuildingRenderer", "Draw", graphics, door, Parts(door), 40, front, closedHit);
        // Two separate transparent windows must interrupt the colored curtain at the middle.
        var row = 250 - (int)((0.1F + (32 - 1.5F - 0.1F) * 0.55F) * 5);
        var transitions = 0;
        var inside = false;
        for (var x = 110; x < 290; x++)
        {
            var pixel = bitmap.GetPixel(x, row);
            var glass = pixel.G > pixel.R * 0.9 && pixel.B > pixel.R * 0.9 && pixel.G > 150;
            if (glass && !inside) transitions++;
            inside = glass;
        }
        Require(transitions == 2, "Closed rapid curtains must have exactly two side-by-side transparent windows.");
        Set(door, "IsOpen", true);
        graphics.Clear(Color.White);
        using var openHit = new GraphicsPath(FillMode.Winding);
        Static("FactoryMapBuildingRenderer", "Draw", graphics, door, Parts(door), 40, front, openHit);
        Require(bitmap.GetPixel(200, 200).ToArgb() == Color.White.ToArgb(), "Open rapid curtains must roll upward and leave the lower passage clear.");
    }
}
