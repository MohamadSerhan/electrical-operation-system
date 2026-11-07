using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class FactoryMapMotionValidation
{
    private static Type Type(string name) => typeof(FactoryMapView).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5." + name)!;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Put(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Call(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(target, args);
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    private static int Builds(object cache) => (int)cache.GetType().GetProperty("BuildCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static object Assets() => typeof(FactoryMapView).GetMethod("CreateAssets", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;

    public static void Run()
    {
        ValidateHitGeometry();
        ValidateCache();
        ValidateNativePresentation();
        ValidateCacheAccounting();
        ValidateVectorReplay();
        ValidateWheel();
        ValidateFrameTimes();
        ValidateSharpScene();
        RunSavedWorkload();
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("EOEMS_MAP_PERF_DATA"))) RunPointerWorkload();
    }

    internal static void RunPointerWorkload()
    {
        ValidateHitGeometry();
        var assets = Assets();
        var directory = Environment.GetEnvironmentVariable("EOEMS_MAP_PERF_DATA");
        Require(!string.IsNullOrEmpty(directory), "Pointer regression requires the actual saved factory map.");
        assets = Type("FactoryMapLayoutStore").GetMethod("LoadAssets")!.Invoke(null,
            new[] { assets, Path.Combine(directory!, "factory-map-layout.json") })!;
        using var canvas = (Control)Activator.CreateInstance(Type("FactoryMapCanvas"), new[] { assets })!;
        canvas.Size = new Size(1920, 1080);
        Call(canvas, "SetBuildingParts", Type("FactoryMapBuildingStore").GetMethod("Load")!.Invoke(null,
            new object[] { Path.Combine(directory!, "factory-map-building.json") }));
        using var image = new Bitmap(canvas.Width, canvas.Height);
        using var graphics = Graphics.FromImage(image);
        foreach (var edit in new[] { false, true })
        foreach (var zoom in new[] { 1f, 4f, 12f, 30f })
        {
            Set(canvas, "EditMode", edit);
            Call(canvas, "ResetView"); Call(canvas, "ZoomBy", zoom);
            Call(canvas, "OnPaint", new PaintEventArgs(graphics, canvas.ClientRectangle));
            var timings = new List<double>();
            for (var y = 40; y < canvas.Height; y += 137)
            for (var x = 30; x < canvas.Width; x += 167)
            {
                var clock = Stopwatch.StartNew();
                Call(canvas, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, x, y, 0));
                timings.Add(clock.Elapsed.TotalMilliseconds);
            }
            Console.WriteLine($"Real pointer hit tests, edit={edit}, {zoom}x: {timings.Count} moves, " +
                $"mean {timings.Average():F2}ms, p95 {timings.Order().ElementAt((int)(timings.Count * .95)):F2}ms, max {timings.Max():F2}ms.");
            Require(timings.Max() < 25 && timings.Order().ElementAt((int)(timings.Count * .95)) < 8,
                "Actual mouse hover/selection cannot stall camera input even on complex close-up equipment.");
        }
    }

    private static void ValidateHitGeometry()
    {
        foreach (var mode in new[] { FillMode.Winding, FillMode.Alternate })
        {
            using var path = new GraphicsPath(mode);
            path.AddPolygon(new[] { new PointF(10, 10), new PointF(90, 10), new PointF(90, 45),
                new PointF(45, 45), new PointF(45, 90), new PointF(10, 90) });
            path.AddRectangle(new Rectangle(20, 20, 15, 15));
            path.AddEllipse(110, 10, 30, 50);
            var geometry = Activator.CreateInstance(Type("FactoryMapHitGeometry"), path)!;
            using var pen = new Pen(Color.Black, 5) { LineJoin = LineJoin.Round };
            foreach (var point in new[] { new Point(15, 15), new Point(25, 25), new Point(75, 75),
                new Point(8, 30), new Point(6, 30), new Point(125, 35), new Point(150, 35), new Point(95, 95) })
            {
                Require((bool)Call(geometry, "Contains", point, false)! == path.IsVisible(point),
                    "Cached hit geometry preserves filled polygons, holes, curves and winding/alternate fill.");
                Require((bool)Call(geometry, "Contains", point, true)! == (path.IsVisible(point) || path.IsOutlineVisible(point, pen)),
                    "Cached hit geometry preserves five-pixel edge selection with round corner tolerance.");
            }
        }
    }

    private static void ValidateNativePresentation()
    {
        var asset = ((IList)Assets())[0]!;
        foreach (var model in new[] { "Caterpillar generator", "Vertiv EXL 500 kVA", "Beverage line with filler",
            "Zeta smoke detector", "Zeta heat detector", "Zeta multi-sensor detector", "Zeta sounder", "Zeta manual call point" })
        foreach (var scale in new[] { .75f, 1.25f, 4f })
        {
            Set(asset, "Model", model);
            Set(asset, "Category", model.StartsWith("Zeta", StringComparison.Ordinal) ? "Fire System" : "Generators");
            using var native = (IDisposable)Activator.CreateInstance(Type("FactoryMapRenderCache"))!;
            using var direct = (IDisposable)Activator.CreateInstance(Type("FactoryMapRenderCache"))!;
            Set(direct, "RetainNativeImages", false);
            using var expected = new Bitmap(400, 300);
            using var actual = new Bitmap(400, 300);
            using var reference = Graphics.FromImage(expected);
            using var presentation = Graphics.FromImage(actual);
            reference.Clear(Color.White); presentation.Clear(Color.White);
            reference.SmoothingMode = presentation.SmoothingMode = SmoothingMode.AntiAlias;
            Func<float, float, float, PointF> project = (x, y, z) => new(x - .6f * y, .5f * y - z);
            using var expectedHit = (GraphicsPath)Call(direct, "Draw", reference, asset, project, 1f, 1f, 1f,
                new PointF(200.375f, 180.625f), scale, new Rectangle(0, 0, 400, 300))!;
            using var actualHit = (GraphicsPath)Call(native, "Draw", presentation, asset, project, 1f, 1f, 1f,
                new PointF(200.375f, 180.625f), scale, new Rectangle(0, 0, 400, 300))!;
            long difference = 0;
            var maximum = 0;
            var covered = 0;
            for (var y = 0; y < actual.Height; y++)
            for (var x = 0; x < actual.Width; x++)
            {
                var a = actual.GetPixel(x, y); var b = expected.GetPixel(x, y);
                if (a.ToArgb() == Color.White.ToArgb() && b.ToArgb() == Color.White.ToArgb()) continue;
                covered++;
                var red = Math.Abs(a.R - b.R); var green = Math.Abs(a.G - b.G); var blue = Math.Abs(a.B - b.B);
                difference += red + green + blue;
                maximum = Math.Max(maximum, Math.Max(red, Math.Max(green, blue)));
            }
            var mean = difference / (Math.Max(1, covered) * 3d * 255);
            Console.WriteLine($"Native/direct artwork {model} at {scale}x: mean {mean:P4}, max channel {maximum}.");
            Require(mean < .001 && maximum <= 4 && expectedHit.GetBounds() == actualHit.GetBounds(),
                "Native presentation must retain full-resolution interpolation, subpixel placement, alpha edges and exact hits.");
        }
    }

    private static void ValidateCacheAccounting()
    {
        using var cache = (IDisposable)Activator.CreateInstance(Type("FactoryMapRenderCache"))!;
        var asset = ((IList)Assets())[0]!;
        Set(asset, "Model", "Zeta smoke detector");
        using var image = new Bitmap(400, 300);
        using var graphics = Graphics.FromImage(image);
        Func<float, float, float, PointF> project = (x, y, z) => new(x - .6f * y, .5f * y - z);
        void CheckAccounting()
        {
            long actual = 0;
            var count = 0;
            foreach (var entry in Field<IDictionary>(cache, "_drawings").Values)
            {
                var raster = (Bitmap?)entry!.GetType().GetField("Image")!.GetValue(entry);
                if (raster != null) actual += (long)raster.Width * raster.Height;
                var natives = (IDictionary)entry.GetType().GetField("NativeImages")!.GetValue(entry)!;
                count += natives.Count;
                foreach (var native in natives.Values)
                {
                    var bitmap = (Bitmap)native!.GetType().GetField("Image")!.GetValue(native)!;
                    actual += (long)bitmap.Width * bitmap.Height;
                }
            }
            var reported = (long)cache.GetType().GetProperty("Pixels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
            Require(actual == reported && actual <= 16 * 1024 * 1024 && count <= 256,
                "Cached pixel accounting must match allocated source/native images through replacement and eviction.");
        }
        for (var i = 0; i < 300; i++)
        {
            Set(asset, "Id", i < 20 ? "replace-native" : "native-" + i);
            using var hit = (GraphicsPath?)Call(cache, "Draw", graphics, asset, project, 1f, 1f, 1f,
                new PointF(200.375f, 180.625f), i < 20 ? 4f + i % 3 * .5f : 4.25f, new Rectangle(0, 0, 400, 300));
            CheckAccounting();
        }
        Require(Builds(cache) > 1, "The workload must actually exercise native image count eviction.");
        Call(cache, "SetCamera", 20f, 40f);
        CheckAccounting();
        Require(Field<IDictionary>(cache, "_drawings").Count == 0, "Camera changes must dispose all cached images.");
    }

    private static void ValidateVectorReplay()
    {
        var renderer = Type("FactoryMapEquipmentRenderer");
        var drawingType = renderer.GetNestedType("VectorDrawing", BindingFlags.NonPublic)!;
        var asset = ((IList)Assets())[0]!;
        foreach (var model in new[] { "Hansa chiller with control and power panels", "Chiller",
            "Caterpillar generator 810 kVA", "Vertiv EXL 500 kVA", "Vertiv NXL 800 kVA", "PET bottling line" })
        {
            Set(asset, "Model", model);
            using var expected = new Bitmap(600, 500);
            using var actual = new Bitmap(600, 500);
            using var reference = Graphics.FromImage(expected);
            using var replay = Graphics.FromImage(actual);
            reference.Clear(Color.White); replay.Clear(Color.White);
            reference.SmoothingMode = replay.SmoothingMode = SmoothingMode.AntiAlias;
            Func<float, float, float, PointF> local = (x, y, z) => new(x - y * 0.6F, y * 0.5F - z);
            Func<float, float, float, PointF> screen = (x, y, z) =>
            {
                var p = local(x, y, z);
                return new(300 + p.X * 5, 350 + p.Y * 5);
            };
            using var hit = new GraphicsPath();
            renderer.GetMethod("Draw")!.Invoke(null, new object[] { reference, asset, screen, hit });
            using var drawing = (IDisposable)Activator.CreateInstance(drawingType, new object[] { asset, local })!;
            Call(drawing, "Paint", replay, new PointF(300, 350), 5F, 0F);
            long difference = 0;
            for (var y = 0; y < actual.Height; y++)
                for (var x = 0; x < actual.Width; x++)
                {
                    var a = actual.GetPixel(x, y); var b = expected.GetPixel(x, y);
                    difference += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                }
            Require(difference / (600.0 * 500 * 3 * 255) < 0.01,
                $"Retained vector replay must preserve original faces, detail strokes, child components and captions: {model}.");
        }
        Console.WriteLine("Retained vector pixels match original equipment geometry/detail rendering within 1% mean color difference.");
    }

    private static void ValidateSharpScene()
    {
        using var canvas = (Control)Activator.CreateInstance(Type("FactoryMapCanvas"), new[] { Assets() })!;
        canvas.Size = new Size(1000, 700);
        using var bitmap = new Bitmap(1000, 700);
        using var graphics = Graphics.FromImage(bitmap);
        void Paint() => Call(canvas, "OnPaint", new PaintEventArgs(graphics, canvas.ClientRectangle));
        Paint();
        var assets = Field<IList>(canvas, "_assets");
        var selected = assets[0]!;
        var originalHit = Field<IDictionary>(canvas, "_hitAreas")[selected] as GraphicsPath;
        var bounds = originalHit!.GetBounds();
        var buildingHits = Field<IDictionary>(canvas, "_buildingHits");
        var buildingKey = buildingHits.Keys.Cast<object>().First();
        var originalBuildingBounds = ((GraphicsPath)buildingHits[buildingKey]!).GetBounds();
        var cache = Field<object>(canvas, "_renderCache");
        var count = Builds(cache);
        byte[] Pixels()
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }
        var restingPixels = Pixels();
        Put(canvas, "_panning", true);
        Paint();
        Require(graphics.SmoothingMode == SmoothingMode.AntiAlias, "Anti-aliasing must not be disabled during pan.");
        Require(Pixels().SequenceEqual(restingPixels), "Starting a pan must not downgrade a single rendered pixel.");
        Field<System.Windows.Forms.Timer>(canvas, "_zoomTimer").Start();
        Paint();
        Field<System.Windows.Forms.Timer>(canvas, "_zoomTimer").Stop();
        Require(Pixels().SequenceEqual(restingPixels), "Active wheel zoom must use the same artwork quality as the resting view.");
        Put(canvas, "_pan", new PointF(10, 15));
        Paint();
        Require(Builds(cache) == count, "Pan must reuse vector geometry without rebuilding or stretching scene images.");
        var moved = ((GraphicsPath)Field<IDictionary>(canvas, "_hitAreas")[selected]!).GetBounds();
        Require(Math.Abs(moved.X - bounds.X - 10) < 0.001 && Math.Abs(moved.Y - bounds.Y - 15) < 0.001,
            "Buffered scene hits must follow the exact screen translation.");
        var movedBuildingBounds = ((GraphicsPath)Field<IDictionary>(canvas, "_buildingHits")[buildingKey]!).GetBounds();
        Require(Math.Abs(movedBuildingBounds.X - originalBuildingBounds.X - 10) < 0.001 &&
            Math.Abs(movedBuildingBounds.Y - originalBuildingBounds.Y - 15) < 0.001,
            "Buffered building hits must follow the same exact translation as equipment.");
        Call(canvas, "ZoomAt", 1.05F, new Point(400, 300));
        Paint();
        Require(Builds(cache) == count, "Zoom must reuse vector commands without raster resolution changes.");
        var zoomedHit = (GraphicsPath)Field<IDictionary>(canvas, "_hitAreas")[selected]!;
        var zoomed = zoomedHit.GetBounds();
        Require(Math.Abs(zoomed.Width / moved.Width - 1.05F) < 0.001 &&
            Math.Abs(zoomed.Height / moved.Height - 1.05F) < 0.001,
            "Buffered equipment hit polygons must follow exact zoom scale.");
        Set(selected, "BodyColor", "#BB3344");
        Paint();
        Require(Builds(cache) > count, "Changing an item must invalidate its vector drawing.");
        var part = Field<IList>(canvas, "_building")[0]!;
        Set(part, "BodyColor", "#557788");
        Paint();
        Call(canvas, "SetLayerVisible", "Generators", false);
        Paint();
        Require(!Field<IDictionary>(canvas, "_hitAreas").Contains(selected),
            "Hidden layers must disappear from cached geometry and hit testing.");
        Put(canvas, "_panning", false);
        Paint();
        Require(graphics.SmoothingMode == SmoothingMode.AntiAlias, "Vector edge quality must stay high during and after gestures.");
        Set(selected, "Category", "Electrical Panels");
        Set(selected, "Model", "Floor panel with energy meter"); Set(selected, "MeterSourceId", "scene-meter");
        Paint();
        IReadOnlyList<MeterSnapshot> readings = new[] { new MeterSnapshot { TransformerName = "scene-meter",
            Quality = MeterQuality.Good, CapturedAtUtc = DateTime.UtcNow, DecodedValues = new float[32] } };
        Call(canvas, "SetSnapshots", readings);
        Paint();
        Require((string)Call(canvas, "StatusText", selected)! == "Good acquisition", "Live status must remain current without raster overlays.");
        Call(canvas, "SelectAsset", selected);
        Paint();
        Require(ReferenceEquals(canvas.GetType().GetProperty("SelectedAsset")!.GetValue(canvas), selected), "Selection must update immediately.");
        Console.WriteLine("Factory vector pan/zoom reuse, transformed hits, appearance/building/layer updates and continuous quality passed.");
    }

    internal static void RunSavedWorkload(bool top = false)
    {
        var directory = Environment.GetEnvironmentVariable("EOEMS_MAP_PERF_DATA");
        if (string.IsNullOrEmpty(directory)) return;
        var defaults = Assets();
        var assets = Type("FactoryMapLayoutStore").GetMethod("LoadAssets")!.Invoke(null,
            new[] { defaults, Path.Combine(directory, "factory-map-layout.json") })!;
        var parts = Type("FactoryMapBuildingStore").GetMethod("Load")!.Invoke(null,
            new object[] { Path.Combine(directory, "factory-map-building.json") })!;
        foreach (var size in new[] { new Size(1365, 766), new Size(1920, 1080) })
            ValidateSavedViewport(assets, parts, size, top);
    }

    private static void ValidateSavedViewport(object assets, object parts, Size size, bool top)
    {
        using var canvas = (Control)Activator.CreateInstance(Type("FactoryMapCanvas"), new[] { assets })!;
        canvas.Size = size;
        Call(canvas, "SetBuildingParts", parts);
        using var bitmap = new Bitmap(canvas.Width, canvas.Height);
        using var graphics = Graphics.FromImage(bitmap);
        void Paint() => Call(canvas, "OnPaint", new PaintEventArgs(graphics, canvas.ClientRectangle));
        var selected = ((IList)assets).Cast<object>().First(asset => (bool)asset.GetType().GetProperty("IsPlaced")!.GetValue(asset)!);
        Call(canvas, "SelectAsset", selected);
        foreach (var level in new[] { 1F, 4F, 12F, 30F })
        {
            Call(canvas, "ResetView");
            if (top) Call(canvas, "SetTopView");
            Call(canvas, "ZoomBy", level);
            Require(Math.Abs(Field<float>(canvas, "_zoom") - level) < 0.0001, "The benchmark must measure the requested zoom, not a focus-reset scale.");
            Paint();
            var cache = Field<object>(canvas, "_renderCache");
            var rasterProperty = cache.GetType().GetProperty("RasterBuildCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var rasterBefore = (int)rasterProperty.GetValue(cache)!;
            var pan = Field<PointF>(canvas, "_pan");
            Put(canvas, "_panning", true);
            var timings = new List<double>();
            for (var index = 0; index < 60; index++)
            {
                Put(canvas, "_pan", new PointF(pan.X + index, pan.Y + index / 2));
                var clock = Stopwatch.StartNew();
                Paint();
                timings.Add(clock.Elapsed.TotalMilliseconds);
            }
            Put(canvas, "_panning", false);
            Console.WriteLine($"Pan artwork rebuilds: geometry {Builds(cache)}, rasters {(int)rasterProperty.GetValue(cache)! - rasterBefore}, cached pixels {cache.GetType().GetProperty("Pixels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)}.");
            var zoomTimings = new List<double>();
            Field<System.Windows.Forms.Timer>(canvas, "_zoomTimer").Start();
            for (var index = 0; index < 40; index++)
            {
                Call(canvas, "ZoomAt", index < 20 ? 1.015F : 1 / 1.015F, new Point(680, 380));
                var clock = Stopwatch.StartNew();
                Paint();
                zoomTimings.Add(clock.Elapsed.TotalMilliseconds);
            }
            Field<System.Windows.Forms.Timer>(canvas, "_zoomTimer").Stop();
            Paint();
            var pan95 = timings.Order().ElementAt(56);
            var zoom95 = zoomTimings.Order().ElementAt(37);
            Console.WriteLine($"Saved-map {(top ? "top" : "isometric")} {((IList)assets).Count} assets/{((IList)parts).Count} building parts, {size}, {level}x: pan {timings.Average():F1}ms (p95 {pan95:F1}, max {timings.Max():F1}), zoom {zoomTimings.Average():F1}ms (p95 {zoom95:F1}, max {zoomTimings.Max():F1}).");
            var averageBudget = 25.0 * size.Width * size.Height / (1365 * 766);
            Require(timings.Average() < averageBudget && zoomTimings.Average() < averageBudget && pan95 < 50 && zoom95 < 50 &&
                timings.Max() < 100 && zoomTimings.Max() < 100,
                "The saved-map workload must remain responsive at overview and close zoom, not just the default demo.");
        }
    }

    public static void Render(string directory)
    {
        var assets = Assets();
        var savedDirectory = Environment.GetEnvironmentVariable("EOEMS_MAP_PERF_DATA");
        if (!string.IsNullOrEmpty(savedDirectory))
            assets = Type("FactoryMapLayoutStore").GetMethod("LoadAssets")!.Invoke(null,
                new[] { assets, Path.Combine(savedDirectory, "factory-map-layout.json") })!;
        using var canvas = (Control)Activator.CreateInstance(Type("FactoryMapCanvas"), new[] { assets })!;
        if (!string.IsNullOrEmpty(savedDirectory))
            Call(canvas, "SetBuildingParts", Type("FactoryMapBuildingStore").GetMethod("Load")!.Invoke(null,
                new object[] { Path.Combine(savedDirectory, "factory-map-building.json") }));
        canvas.Size = new Size(1365, 766);
        Call(canvas, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, canvas.Width - 84, 76, 0));
        using var bitmap = new Bitmap(canvas.Width, canvas.Height);
        canvas.DrawToBitmap(bitmap, canvas.ClientRectangle);
        bitmap.Save(Path.Combine(directory, "FactoryMap-NavigationWheel.png"));
        Call(canvas, "SetTopView");
        canvas.DrawToBitmap(bitmap, canvas.ClientRectangle);
        bitmap.Save(Path.Combine(directory, "FactoryMap-Top3D.png"));
        var asset = Field<IList>(canvas, "_assets")[0]!;
        Call(canvas, "SetIsometricView");
        canvas.DrawToBitmap(bitmap, canvas.ClientRectangle);
        Call(canvas, "SelectAsset", asset);
        Call(canvas, "FocusSelection");
        canvas.DrawToBitmap(bitmap, canvas.ClientRectangle);
        bitmap.Save(Path.Combine(directory, "FactoryMap-SharpCloseUp.png"));
    }

    private static void ValidateCache()
    {
        using var bitmap = new Bitmap(600, 500);
        using var graphics = Graphics.FromImage(bitmap);
        using var cache = (IDisposable)Activator.CreateInstance(Type("FactoryMapRenderCache"))!;
        var asset = ((IList)Assets())[0]!;
        Set(asset, "Model", "Hansa chiller with control and power panels"); Set(asset, "Category", "Chillers");
        Func<float, float, float, PointF> project = (x, y, z) => new(x - y * 0.3F, y * 0.5F - z);
        GraphicsPath Draw(float scale = 2, float x = 250) => (GraphicsPath)Call(cache, "Draw", graphics, asset,
            project, 1F, 1F, 1F, new PointF(x, 300), scale, new Rectangle(0, 0, 600, 500))!;
        Call(cache, "SetCamera", 0F, 0.5F);
        using var first = Draw();
        var builds = Builds(cache);
        using var pan = Draw(x: 270);
        Require(Builds(cache) == builds, "Panning must reuse equipment sprites instead of rebuilding model geometry.");
        using var zoom = Draw(2.1F);
        Require(Builds(cache) == builds, "Animated zoom steps must reuse vector geometry and refresh artwork resolution when necessary.");
        var drawings = Field<IDictionary>(cache, "_drawings");
        var entry = drawings.Values.Cast<object>().Single();
        var resolution = (float)entry.GetType().GetField("Resolution")!.GetValue(entry)!;
        Require(resolution >= 2.1F * 2, "Equipment artwork must have at least twice the current displayed resolution.");
        var rasterProperty = cache.GetType().GetProperty("RasterBuildCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var rasterCount = (int)rasterProperty.GetValue(cache)!;
        for (var index = 0; index < 8; index++)
        {
            using var crossing = Draw(index % 2 == 0 ? 1.99F : 2.01F);
        }
        Require((int)rasterProperty.GetValue(cache)! == rasterCount,
            "Zooming back and forth across a resolution boundary must reuse the sharper image instead of repeatedly rebuilding it.");
        using var expected = new GraphicsPath(FillMode.Winding);
        Func<float, float, float, PointF> screen = (x, y, z) =>
        {
            var point = project(x, y, z);
            return new(250 + point.X * 2, 300 + point.Y * 2);
        };
        Type("FactoryMapEquipmentRenderer").GetMethod("Draw")!.Invoke(null, new object[] { graphics, asset, screen, expected });
        Require(first.PathPoints.Length == expected.PathPoints.Length &&
            first.PathPoints.OrderBy(point => Math.Round(point.X, 3)).ThenBy(point => Math.Round(point.Y, 3))
                .Zip(expected.PathPoints.OrderBy(point => Math.Round(point.X, 3)).ThenBy(point => Math.Round(point.Y, 3)))
                .All(pair => Math.Abs(pair.First.X - pair.Second.X) < 0.001 &&
                Math.Abs(pair.First.Y - pair.Second.Y) < 0.001),
            "Cached hits must be exact transformed geometry, not rectangular sprite bounds.");
        Set(asset, "BodyColor", "#BB2244");
        using var recolor = Draw();
        Require(Builds(cache) > builds, "Recoloring must invalidate the cached artwork.");
        builds = Builds(cache);
        Set(asset, "Model", "Chiller");
        using var model = Draw();
        Require(Builds(cache) > builds, "Changing an equipment model must not retain old artwork.");
        builds = Builds(cache);
        using var resized = (GraphicsPath)Call(cache, "Draw", graphics, asset, project, 1.5F, 1F, 1F,
            new PointF(250, 300), 2F, new Rectangle(0, 0, 600, 500))!;
        Require(Builds(cache) > builds, "Dimension overrides must invalidate the sprite.");
        Call(cache, "SetCamera", 0.1F, 0.5F);
        builds = Builds(cache);
        using var orbit = Draw();
        Require(Builds(cache) > builds, "Changing the camera must rebuild the correct equipment faces.");
        Require(Call(cache, "Draw", graphics, asset, project, 1F, 1F, 1F, new PointF(-10000, -10000),
            2F, new Rectangle(0, 0, 600, 500)) == null, "Offscreen equipment must be culled.");
        builds = Builds(cache);
        var rasters = (int)cache.GetType().GetProperty("RasterBuildCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        var vectors = (int)cache.GetType().GetProperty("VectorPaintCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        using var closeUp = Draw(100);
        Require(Builds(cache) == builds && closeUp.PointCount > 20,
            "Extreme close-ups must retain vector detail without bitmap allocation or cache rebuild.");
        Require((int)cache.GetType().GetProperty("VectorPaintCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)! == vectors + 1 &&
            (int)cache.GetType().GetProperty("RasterBuildCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)! == rasters,
            "Oversized close-ups must paint vectors, never magnify a capped image, even during movement.");
        for (var index = 0; index < 110; index++)
        {
            Set(asset, "BodyColor", "#" + index.ToString("X6"));
            Call(cache, "Draw", graphics, asset, project, 1F, 1F, 1F, new PointF(-10000, -10000),
                2F, new Rectangle(0, 0, 600, 500));
        }
        Require(Field<IDictionary>(cache, "_drawings").Count <= 96 &&
            (long)cache.GetType().GetProperty("Pixels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)! <= 16 * 1024 * 1024,
            "The artwork cache must respect both entry and pixel memory limits.");
        Console.WriteLine("Factory sprite reuse, exact transformed hits, appearance/size/camera invalidation and viewport culling passed.");
    }

    private static void ValidateWheel()
    {
        var assets = Assets();
        using var canvas = (Control)Activator.CreateInstance(Type("FactoryMapCanvas"), new[] { assets })!;
        canvas.Size = new Size(1000, 700);
        canvas.CreateControl();
        var wheel = Field<object>(canvas, "_navigation");
        var bounds = (Rectangle)wheel.GetType().GetProperty("Bounds")!.GetValue(wheel)!;
        var cx = bounds.X + 72;
        var cy = bounds.Y + 68;
        Call(canvas, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, 20, 200, 0));
        Require(Field<int>(wheel, "_alpha") > 0, "Mouse movement must reveal the navigation wheel.");
        var now = Field<long>(wheel, "_lastMotion");
        Call(wheel, "AdvanceFade", now + 1550);
        Require(Field<int>(wheel, "_alpha") > 0 && Field<int>(wheel, "_alpha") < 205, "The idle wheel must fade rather than disappear abruptly.");
        Call(wheel, "AdvanceFade", now + 1800);
        Require(Field<int>(wheel, "_alpha") == 0 && (string)Call(wheel, "Hit", new Point(cx, cy))! == "",
            "A hidden wheel must not block selecting equipment beneath it.");
        Call(canvas, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, cx, cy, 0));
        Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, cx, cy, 0));
        var yaw = Field<float>(canvas, "_yaw");
        Call(canvas, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, cx + 20, cy + 10, 0));
        Require(Field<float>(canvas, "_yaw") > yaw, "Dragging the wheel center/Orbit must rotate the scene.");
        Call(canvas, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, cx + 20, cy + 10, 0));
        var pan = Field<PointF>(canvas, "_pan");
        Call(canvas, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, cx, cy + 28, 0));
        Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, cx, cy + 28, 0));
        Call(canvas, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, cx + 15, cy + 35, 0));
        Require(Field<PointF>(canvas, "_pan") == new PointF(pan.X + 15, pan.Y + 7), "The Pan sector must translate without rotating or moving equipment.");
        Call(canvas, "OnMouseCaptureChanged", EventArgs.Empty);
        Call(canvas, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, cx + 15, cy + 35, 0));
        Require(!(bool)wheel.GetType().GetProperty("Active")!.GetValue(wheel)!, "Capture loss must end navigation-wheel drags.");
        void Click(int x, int y)
        {
            Call(canvas, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, x, y, 0));
            Call(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
            Call(canvas, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, x, y, 0));
        }
        Click(cx + 55, cy);
        Require(Math.Abs(Field<float>(canvas, "_yaw") - Math.PI / 2) < 0.00001, "East must choose the exact cardinal heading.");
        Click(cx + 28, cy);
        Require(Math.Abs(Field<float>(canvas, "_elevation") - 70 * Math.PI / 180) < 0.00001, "Top must retain a visible 3D height component at 70 degrees.");
        Call(canvas, "SetPlanView");
        Require(Math.Abs(Field<float>(canvas, "_elevation") - Math.PI / 2) < 0.00001, "Exact plan view must remain available separately.");
        Click(cx - 28, cy);
        Require(Field<float>(canvas, "_zoom") == 1F && Field<PointF>(canvas, "_pan") == PointF.Empty, "Fit must restore the whole factory.");
        Console.WriteLine("Factory navigation wheel fade, hidden hit-through, orbit/pan gestures, capture recovery and cardinal/top/fit views passed.");
    }

    internal static void ValidateFrameTimes()
    {
        using var canvas = (Control)Activator.CreateInstance(Type("FactoryMapCanvas"), new[] { Assets() })!;
        canvas.Size = new Size(1365, 766);
        using var bitmap = new Bitmap(1365, 766);
        using var graphics = Graphics.FromImage(bitmap);
        void Paint() => Call(canvas, "OnPaint", new PaintEventArgs(graphics, canvas.ClientRectangle));
        Paint();
        var cache = Field<object>(canvas, "_renderCache");
        var count = Builds(cache);
        Put(canvas, "_panning", true);
        var clock = Stopwatch.StartNew();
        for (var index = 0; index < 30; index++)
        {
            Put(canvas, "_pan", new PointF(index * 2, index));
            Paint();
        }
        var panTime = clock.Elapsed.TotalMilliseconds / 30;
        Put(canvas, "_panning", false);
        Require(Builds(cache) == count, "Thirty pan frames must not rebuild any equipment sprites.");
        clock.Restart();
        Field<System.Windows.Forms.Timer>(canvas, "_zoomTimer").Start();
        for (var index = 0; index < 20; index++)
        {
            Call(canvas, "ZoomAt", 1.01F, new Point(650, 380));
            Paint();
        }
        var zoomTime = clock.Elapsed.TotalMilliseconds / 20;
        Field<System.Windows.Forms.Timer>(canvas, "_zoomTimer").Stop();
        clock.Restart();
        Put(canvas, "_rotating", true);
        for (var index = 0; index < 10; index++)
        {
            Call(canvas, "NavigateDrag", "Orbit", 2F, 1F);
            Paint();
        }
        var orbitTime = clock.Elapsed.TotalMilliseconds / 10;
        Put(canvas, "_rotating", false);
        Paint();
        Require(graphics.SmoothingMode == SmoothingMode.AntiAlias,
            "Full vector edge quality must be restored after interaction ends.");
        Console.WriteLine($"Factory interaction paint: pan {panTime:F1}, zoom {zoomTime:F1}, orbit {orbitTime:F1} ms/frame (1365x766).");
        Require(panTime < 25 && zoomTime < 25 && orbitTime < 50,
            "Default-map pan/zoom must average under 25 ms and changing-angle orbit under 50 ms per frame.");
    }
}
