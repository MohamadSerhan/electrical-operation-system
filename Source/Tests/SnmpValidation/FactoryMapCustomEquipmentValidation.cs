using System.Collections;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class FactoryMapCustomEquipmentValidation
{
    private static Type Type(string name) => typeof(FactoryMapView).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5." + name)!;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target)!;
    private static object? Call(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!.Invoke(target, args);
    private static object? Static(string type, string name, params object?[] args) =>
        Type(type).GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
    private static object Get(object item, string name) => item.GetType().GetProperty(name)!.GetValue(item)!;
    private static void Set(object item, string name, object value) => item.GetType().GetProperty(name)!.SetValue(item, value);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid equipment import was accepted.");
    }
    private static string Describe(object item) => JsonSerializer.Serialize(item, item.GetType());
    private static IEnumerable<Control> Descendants(Control control) =>
        control.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    private static IList Assets(params object[] assets)
    {
        var result = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("FactoryMapAsset")))!;
        foreach (var asset in assets) result.Add(asset);
        return result;
    }

    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "factory-custom-equipment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var png = Path.Combine(directory, "Test pump.png");
            var jpg = Path.Combine(directory, "Test motor.jpg");
            var whiteBackgroundJpg = Path.Combine(directory, "White background.jpg");
            using (var image = new Bitmap(1600, 800))
            {
                using var graphics = Graphics.FromImage(image);
                graphics.Clear(Color.Transparent);
                graphics.FillRectangle(Brushes.Red, 100, 100, 1200, 500);
                graphics.FillEllipse(Brushes.Blue, 300, 200, 200, 200);
                image.Save(png, ImageFormat.Png);
                image.Save(jpg, ImageFormat.Jpeg);
            }
            using (var image = new Bitmap(320, 240))
            {
                using var graphics = Graphics.FromImage(image);
                graphics.Clear(Color.White);
                graphics.FillEllipse(Brushes.DarkRed, 52, 30, 218, 180);
                image.Save(whiteBackgroundJpg, ImageFormat.Jpeg);
            }
            var definition = Static("FactoryMapCustomEquipmentStore", "ImportImage", png)!;
            Set(definition, "Name", "My custom pump");
            Set(definition, "LengthMeters", 4F);
            Set(definition, "WidthMeters", 2F);
            Set(definition, "HeightMeters", 3F);
            var jpeg = Static("FactoryMapCustomEquipmentStore", "ImportImage", jpg)!;
            var whiteBackground = Static("FactoryMapCustomEquipmentStore", "ImportImage", whiteBackgroundJpg)!;
            using (var decoded = (Bitmap)Static("FactoryMapCustomEquipmentStore", "DecodeImage", definition)!)
                Require(decoded.Width < 1024 && decoded.Height < 512 && decoded.GetPixel(0, 0).A > 100,
                    "Imports must crop transparent margins and preserve the foreground silhouette.");
            using (var decoded = (Bitmap)Static("FactoryMapCustomEquipmentStore", "DecodeImage", whiteBackground)!)
                Require(decoded.GetPixel(0, 0).A < 32 && decoded.GetPixel(decoded.Width / 2, decoded.Height / 2).A > 220,
                    "JPG imports must remove a connected white background without erasing the foreground item.");
            Static("FactoryMapCustomEquipmentStore", "Validate", jpeg);
            var library = Path.Combine(directory, "library");
            Static("FactoryMapCustomEquipmentStore", "Save", definition, library);
            var restored = (IList)Static("FactoryMapCustomEquipmentStore", "Load", library)!;
            Require(restored.Count == 1 && Describe(restored[0]!) == Describe(definition),
                "Reusable library must preserve exact name, metre dimensions, checksum and image.");
            var asset = Static("FactoryMapCustomEquipmentStore", "CreateAsset", definition)!;
            Set(asset, "Id", "MAP-CUSTOM");
            var other = Call(asset, "Clone")!;
            Set(Get(other, "CustomEquipment"), "Name", "Changed clone");
            Require((string)Get(Get(asset, "CustomEquipment"), "Name") == "My custom pump", "Clones must isolate editable definition metadata.");
            var layout = Path.Combine(directory, "layout.json");
            Static("FactoryMapLayoutStore", "SaveAssets", Assets(asset), layout);
            File.Delete(png); File.Delete(jpg); File.Delete(whiteBackgroundJpg);
            Directory.Delete(library, true);
            var loaded = (IList)Static("FactoryMapLayoutStore", "LoadAssets", Assets(), layout)!;
            Require(loaded.Count == 1 && Describe(loaded[0]!) == Describe(asset),
                "Saved maps must remain portable without the uploaded source or library files.");
            ValidateRendering(asset);
            ValidateModeledEquipment(asset, directory);
            ValidateCadImports(directory);
            FactoryMapProductionLineValidation.Run();
            ValidateLibraryAndEditing(asset, definition, library);
            ValidateUploadedSample(Environment.GetEnvironmentVariable("EOEMS_MAP_UPLOAD_SAMPLE"),
                Environment.GetEnvironmentVariable("EOEMS_MAP_LIBRARY_EVIDENCE"));
            Set(definition, "Name", "My custom pump updated");
            Static("FactoryMapCustomEquipmentStore", "Save", definition, library);
            var invalid = Call(definition, "Clone")!;
            Set(invalid, "LengthMeters", float.NaN);
            Reject(() => Static("FactoryMapCustomEquipmentStore", "Validate", invalid));
            invalid = Call(definition, "Clone")!;
            Set(invalid, "PngBase64", "not-base64");
            Reject(() => Static("FactoryMapCustomEquipmentStore", "Validate", invalid));
            invalid = Call(definition, "Clone")!;
            Set(invalid, "ImageHash", "bad checksum");
            Reject(() => Static("FactoryMapCustomEquipmentStore", "Validate", invalid));
            invalid = Call(definition, "Clone")!;
            Set(invalid, "Id", "..\\outside");
            Reject(() => Static("FactoryMapCustomEquipmentStore", "Save", invalid, library));
            Reject(() => Static("FactoryMapCustomEquipmentStore", "Delete", invalid, library));
            Static("FactoryMapCustomEquipmentStore", "Delete", definition, library);
            Require(((IList)Static("FactoryMapCustomEquipmentStore", "Load", library)!).Count == 0 &&
                    !File.Exists(Path.Combine(library, (string)Get(definition, "Id") + ".json")) &&
                    !File.Exists(Path.Combine(library, (string)Get(definition, "Id") + ".json.bak")),
                "Deleting uploaded equipment must remove it and its update backup from the reusable library.");
            Require(Get(asset, "CustomEquipment") != null,
                "Deleting a reusable definition must not erase the image embedded in existing map items.");
            var corrupt = Path.Combine(directory, "broken.png");
            File.WriteAllText(corrupt, "not an image");
            Reject(() => Static("FactoryMapCustomEquipmentStore", "ImportImage", corrupt));
            var unsupported = Path.Combine(directory, "model.obj");
            File.WriteAllText(unsupported, "not supported");
            Reject(() => Static("FactoryMapCustomEquipmentStore", "ImportImage", unsupported));
            var oversized = Path.Combine(directory, "large.jpg");
            using (var stream = File.Create(oversized)) stream.SetLength(10 * 1024 * 1024 + 1);
            Reject(() => Static("FactoryMapCustomEquipmentStore", "ImportImage", oversized));
            var bytes = Convert.FromBase64String((string)Get(definition, "PngBase64"));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 50000);
            File.WriteAllBytes(corrupt, bytes);
            Reject(() => Static("FactoryMapCustomEquipmentStore", "ImportImage", corrupt));
            Console.WriteLine("Custom equipment: background removal, silhouette cropping/extrusion, 2D/3D rendering, delete, corrupt/oversize/path guards, portable save/load, library, placement, clipboard and undo passed.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void ValidateUploadedSample(string? sample, string? evidence)
    {
        if (string.IsNullOrWhiteSpace(sample) || string.IsNullOrWhiteSpace(evidence) || !File.Exists(sample)) return;
        using var upload = (Form)Activator.CreateInstance(Type("FactoryMapCustomEquipmentForm"))!;
        Call(upload, "LoadImage", sample);
        Field<NumericUpDown>(upload, "_length").Value = 2;
        Field<NumericUpDown>(upload, "_width").Value = 1;
        Field<NumericUpDown>(upload, "_height").Value = 1;
        Field<ComboBox>(upload, "_geometry").SelectedIndex = 1;
        upload.Show();
        Application.DoEvents();
        using (var dialogCapture = new Bitmap(upload.Width, upload.Height))
        {
            upload.DrawToBitmap(dialogCapture, upload.ClientRectangle);
            Directory.CreateDirectory(evidence);
            dialogCapture.Save(Path.Combine(evidence, "UploadedEquipment-Dialog.png"), ImageFormat.Png);
        }
        upload.Hide();
        Call(upload, "ApplyDefinition");
        Require(upload.DialogResult == DialogResult.OK,
            "The supplied JPG must pass through the real upload and equipment-generation form.");
        Require((float)Get(Get(upload, "Definition"), "LengthMeters") == 8 &&
                (float)Get(Get(upload, "Definition"), "WidthMeters") == 2.5F &&
                (float)Get(Get(upload, "Definition"), "HeightMeters") == 2.5F,
            "The Sidel equipment generator must use the requested overall dimensions.");
        var preview = Field<Control>(upload, "_generatedPreview");
        using var capture = new Bitmap(preview.Width, preview.Height);
        preview.DrawToBitmap(capture, preview.ClientRectangle);
        Directory.CreateDirectory(evidence);
        capture.Save(Path.Combine(evidence, "UploadedEquipment-GeneratedPreview.png"), ImageFormat.Png);
    }

    private static void ValidateModeledEquipment(object asset, string directory)
    {
        var modeled = Call(asset, "Clone")!;
        var definition = Get(modeled, "CustomEquipment");
        Set(definition, "GeometryModel", "Sidel-style blower and preform feed");
        Set(definition, "LengthMeters", 8F);
        Set(definition, "WidthMeters", 2.5F);
        Set(definition, "HeightMeters", 2.5F);
        Static("FactoryMapCustomEquipmentStore", "Validate", definition);
        var footprint = (RectangleF)Static("FactoryMapPlacement", "AssetFootprint", modeled)!;
        Require(footprint.Width == 64 && footprint.Height == 20,
            "Modeled Sidel equipment must retain its 8 x 2.5 metre footprint.");
        var library = Path.Combine(directory, "modeled-library");
        Static("FactoryMapCustomEquipmentStore", "Save", definition, library);
        var restored = (IList)Static("FactoryMapCustomEquipmentStore", "Load", library)!;
        Require(restored.Count == 1 && Describe(restored[0]!) == Describe(definition),
            "Library persistence must retain the selected geometry model.");
        var layout = Path.Combine(directory, "modeled-layout.json");
        Static("FactoryMapLayoutStore", "SaveAssets", Assets(modeled), layout);
        var loaded = (IList)Static("FactoryMapLayoutStore", "LoadAssets", Assets(), layout)!;
        Require(loaded.Count == 1 && Describe(loaded[0]!) == Describe(modeled),
            "Map persistence must retain the selected geometry and reference image.");
        var invalid = Call(definition, "Clone")!;
        Set(invalid, "GeometryModel", "unsupported");
        Reject(() => Static("FactoryMapCustomEquipmentStore", "Validate", invalid));
        var builtIn = Activator.CreateInstance(Type("FactoryMapAsset"))!;
        Set(builtIn, "Id", "MAP-SIDEL-BUILTIN");
        Set(builtIn, "Model", "Sidel-style blower and preform feed");
        var dimensions = ((float Length, float Width, float Height))Static("FactoryMapEquipmentSizes", "Dimensions", builtIn)!;
        Require(dimensions == (8F, 2.5F, 2.5F), "The built-in Sidel model must use the same exact metre dimensions.");
        var referenceImage = (string)Get(definition, "PngBase64");
        var renderTimer = new System.Diagnostics.Stopwatch();
        foreach (var elevation in new[] { 0, 30, 90 })
            foreach (var degrees in new[] { 0, 45, 90, 135, 180, 225, 270, 315 })
            {
                Set(definition, "PngBase64", referenceImage);
                using var bitmap = new Bitmap(1200, 800);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.White);
                using var hit = new GraphicsPath(FillMode.Winding);
                var radians = degrees * Math.PI / 180;
                var tilt = elevation * Math.PI / 180;
                Func<float, float, float, PointF> project = (x, y, z) =>
                {
                    var rx = x * Math.Cos(radians) - y * Math.Sin(radians);
                    var ry = x * Math.Sin(radians) + y * Math.Cos(radians);
                    return new(600 + (float)rx * 8, 460 + (float)(ry * Math.Sin(tilt) - z * Math.Cos(tilt)) * 8);
                };
                renderTimer.Start();
                var originalSmoothing = graphics.SmoothingMode;
                Static("FactoryMapEquipmentRenderer", "Draw", graphics, modeled, project, hit);
                renderTimer.Stop();
                Require(graphics.SmoothingMode == originalSmoothing,
                    "Refined model rendering must restore graphics state for subsequent map items.");
                Require(hit.PointCount > 6000, "The detailed Sidel model must retain its component faces, including volumetric rails and open-frame machinery.");
                var bounds = hit.GetBounds();
                Require(bounds.Left > 0 && bounds.Right < bitmap.Width && bounds.Top > 0 && bounds.Bottom < bitmap.Height,
                    "Detailed geometry must remain completely visible in all validation views.");
                if (elevation == 90 && degrees == 0)
                    Require(Math.Abs(bounds.Width - 512) < .01F && Math.Abs(bounds.Height - 160) < .01F &&
                            Math.Abs(bounds.Left - 344) < .01F && Math.Abs(bounds.Top - 380) < .01F,
                        "Actual modeled geometry must occupy the centered 8 x 2.5 metre plan, not just report a nominal footprint.");
                if (elevation == 0 && degrees == 0)
                    Require(Math.Abs(bounds.Height - 160) < .01F,
                        "Actual modeled geometry must be exactly 2.5 metres high.");
                using var before = new MemoryStream();
                bitmap.Save(before, ImageFormat.Png);
                graphics.Clear(Color.White);
                using var builtInHit = new GraphicsPath(FillMode.Winding);
                Static("FactoryMapEquipmentRenderer", "Draw", graphics, builtIn, project, builtInHit);
                using var builtInImage = new MemoryStream();
                bitmap.Save(builtInImage, ImageFormat.Png);
                Require(before.ToArray().SequenceEqual(builtInImage.ToArray()),
                    "Built-in and uploaded Sidel selections must render the same detailed model.");
                var evidence = Environment.GetEnvironmentVariable("EOEMS_MAP_LIBRARY_EVIDENCE");
                if (!string.IsNullOrWhiteSpace(evidence))
                {
                    Directory.CreateDirectory(evidence);
                    bitmap.Save(Path.Combine(evidence, $"Sidel-Elevation{elevation}-Yaw{degrees}.png"), ImageFormat.Png);
                }
                Set(definition, "PngBase64", "reference-not-decoded-by-geometry-renderer");
                graphics.Clear(Color.White);
                using var nextHit = new GraphicsPath(FillMode.Winding);
                Static("FactoryMapEquipmentRenderer", "Draw", graphics, modeled, project, nextHit);
                using var after = new MemoryStream();
                bitmap.Save(after, ImageFormat.Png);
                Require(before.ToArray().SequenceEqual(after.ToArray()),
                    "Modeled equipment geometry must not depend on rendering the uploaded image.");
            }
        Set(definition, "PngBase64", referenceImage);
        using (var bitmap = new Bitmap(1000, 700))
        using (var graphics = Graphics.FromImage(bitmap))
        using (var cache = (IDisposable)Activator.CreateInstance(Type("FactoryMapRenderCache"))!)
        {
            graphics.Clear(Color.White);
            var smoothing = graphics.SmoothingMode;
            Func<float, float, float, PointF> projection = (x, y, z) =>
                new((x - y) * .7F, (x + y) * .35F - z * .86F);
            using var hit = (GraphicsPath)Call(cache, "Draw", graphics, builtIn, projection, 1F, 1F, 1F,
                new PointF(500, 400), 8F, new Rectangle(Point.Empty, bitmap.Size))!;
            Require(hit.PointCount > 6000 && graphics.SmoothingMode == smoothing,
                "Cached built-in rendering must preserve detailed geometry and restore the refined drawing state.");
        }
        Console.WriteLine("Detailed Sidel model: measured 8 x 2.5 x 2.5 m geometry, volumetric components, plan/elevation/3D and 24 camera views passed.");
        Console.WriteLine($"Detailed Sidel direct rendering: {renderTimer.Elapsed.TotalMilliseconds / 24:F1} ms/view at 1200 x 800.");
    }

    private static void ValidateCadImports(string directory)
    {
        var obj = Path.Combine(directory, "Equipment.obj");
        File.WriteAllText(obj, """
            mtllib ignored-external-material.mtl
            v 0 0 0
            v 4 0 0
            v 4 2 0
            v 0 2 0
            v 0 0 3
            v 4 0 3
            v 4 2 3
            v 0 2 3
            f 1 2 3 4
            f -4 -3 -2 -1
            f 1 2 6 5
            f 2 3 7 6
            f 3 4 8 7
            f 4 1 5 8
            """);
        var document = new ACadSharp.CadDocument(ACadSharp.ACadVersion.AC1032);
        document.Entities.Add(new ACadSharp.Entities.Line(new CSMath.XYZ(0, 0, 0), new CSMath.XYZ(4, 0, 0)));
        document.Entities.Add(new ACadSharp.Entities.Line(new CSMath.XYZ(4, 0, 0), new CSMath.XYZ(4, 2, 0)));
        document.Entities.Add(new ACadSharp.Entities.Line(new CSMath.XYZ(4, 2, 0), new CSMath.XYZ(0, 2, 0)));
        document.Entities.Add(new ACadSharp.Entities.Line(new CSMath.XYZ(0, 2, 0), new CSMath.XYZ(0, 0, 0)));
        document.Entities.Add(new ACadSharp.Entities.Circle { Center = new CSMath.XYZ(2, 1, 0), Radius = .5 });
        var dxf = Path.Combine(directory, "Equipment.dxf");
        var dwg = Path.Combine(directory, "Equipment.dwg");
        using (var writer = new ACadSharp.IO.DxfWriter(dxf, document, false)) writer.Write();
        using (var writer = new ACadSharp.IO.DwgWriter(dwg, document)) writer.Write();
        foreach (var file in new[] { obj, dxf, dwg })
        {
            var definition = Static("FactoryMapCadImporter", "ImportFile", file, false)!;
            var mesh = Get(definition, "CadGeometry");
            var plan = (bool)Get(mesh, "IsPlan");
            Require(plan == (file != obj), "OBJ mesh must retain height, while 2D DWG/DXF must not acquire invented 3D geometry.");
            Require(((IList)Get(mesh, plan ? "Paths" : "Faces")).Count == (plan ? 5 : 6),
                "Import must preserve the fixture's face/curve count.");
            Set(definition, "LengthMeters", 4F); Set(definition, "WidthMeters", 2F);
            Set(definition, "HeightMeters", plan ? 0F : 3F);
            var clone = Call(definition, "Clone")!;
            Set(((IList)Get(Get(clone, "CadGeometry"), "Vertices"))[0]!, "X", .25D);
            Require((double)Get(((IList)Get(mesh, "Vertices"))[0]!, "X") != .25D,
                "Geometry clones must isolate vertices for map history and copying.");
            var library = Path.Combine(directory, "cad-library-" + Path.GetExtension(file)[1..]);
            Static("FactoryMapCustomEquipmentStore", "Save", definition, library);
            var restored = (IList)Static("FactoryMapCustomEquipmentStore", "Load", library)!;
            Require(Describe(restored[0]!) == Describe(definition), "CAD geometry must survive library persistence without an image.");
            var asset = Static("FactoryMapCustomEquipmentStore", "CreateAsset", definition)!;
            Set(asset, "Id", "CAD-" + Path.GetExtension(file));
            var layout = Path.Combine(directory, Path.GetFileName(file) + ".json");
            Static("FactoryMapLayoutStore", "SaveAssets", Assets(asset), layout);
            var loaded = (IList)Static("FactoryMapLayoutStore", "LoadAssets", Assets(), layout)!;
            Require(Describe(loaded[0]!) == Describe(asset), "CAD geometry must remain portable in saved maps.");
            using var bitmap = new Bitmap(600, 400);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.White);
            using var hit = new GraphicsPath(FillMode.Winding);
            Func<float, float, float, PointF> project = (x, y, z) => new(300 + (x - y) * 5, 250 + (x + y) * 2.5F - z * 5);
            Static("FactoryMapEquipmentRenderer", "Draw", graphics, asset, project, hit);
            Require(hit.PointCount > 20 && hit.GetBounds().Width > 100, "Imported equipment must render with selectable geometry.");
            var evidence = Environment.GetEnvironmentVariable("EOEMS_MAP_LIBRARY_EVIDENCE");
            if (!string.IsNullOrWhiteSpace(evidence)) bitmap.Save(Path.Combine(evidence, "Imported-" + Path.GetExtension(file)[1..] + ".png"));
            using var dialog = (Form)Activator.CreateInstance(Type("FactoryMapCustomEquipmentForm"), new object[] { true })!;
            Call(dialog, "LoadGeometry", file);
            Require(Field<NumericUpDown>(dialog, "_height").Enabled != plan, "2D imports must disable height, not offer extrusion.");
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                dialog.Show();
                Application.DoEvents();
                using var capture = new Bitmap(dialog.Width, dialog.Height);
                dialog.DrawToBitmap(capture, dialog.ClientRectangle);
                capture.Save(Path.Combine(evidence, "ImportDialog-" + Path.GetExtension(file)[1..] + ".png"));
                dialog.Hide();
            }
            Call(dialog, "ApplyDefinition");
            Require(dialog.DialogResult == DialogResult.OK, "OBJ/DXF/DWG must pass the actual import form.");
            Static("FactoryMapCustomEquipmentStore", "Delete", definition, library);
        }
        var yUp = Static("FactoryMapCadImporter", "ImportFile", obj, true)!;
        Require(Math.Abs((float)Get(yUp, "WidthMeters") - 1.5F) < .001F &&
                Math.Abs((float)Get(yUp, "HeightMeters") - 1F) < .001F, "OBJ Y-up selection must exchange height and depth correctly.");
        var spatial = new ACadSharp.CadDocument(ACadSharp.ACadVersion.AC1032);
        var block = new ACadSharp.Tables.BlockRecord("EquipmentBlock");
        spatial.BlockRecords.Add(block);
        block.Entities.Add(new ACadSharp.Entities.Face3D {
            FirstCorner = new CSMath.XYZ(0, 0, 0), SecondCorner = new CSMath.XYZ(4, 0, 0),
            ThirdCorner = new CSMath.XYZ(4, 2, 3), FourthCorner = new CSMath.XYZ(0, 2, 3)
        });
        spatial.Entities.Add(new ACadSharp.Entities.Insert(block) { InsertPoint = new CSMath.XYZ(10, 20, 30), Rotation = .2 });
        var curved = new ACadSharp.Entities.LwPolyline();
        curved.Vertices.Add(new ACadSharp.Entities.LwPolyline.Vertex(10, 20) { Bulge = .5 });
        curved.Vertices.Add(new ACadSharp.Entities.LwPolyline.Vertex(14, 22));
        spatial.Entities.Add(curved);
        foreach (var extension in new[] { "dxf", "dwg" })
        {
            var file = Path.Combine(directory, "Spatial." + extension);
            if (extension == "dwg") { using var writer = new ACadSharp.IO.DwgWriter(file, spatial); writer.Write(); }
            else { using var writer = new ACadSharp.IO.DxfWriter(file, spatial, false); writer.Write(); }
            var imported = Static("FactoryMapCadImporter", "ImportFile", file, false)!;
            var geometry = Get(imported, "CadGeometry");
            Require(!(bool)Get(geometry, "IsPlan") && ((IList)Get(geometry, "Faces")).Count == 1 &&
                    ((IList)Get(geometry, "Paths")).Count > 0,
                "Real 3D DWG/DXF faces, transformed blocks and curved polylines must import without losing height.");
        }
        var invalid = Path.Combine(directory, "bad.obj");
        File.WriteAllText(invalid, "v 0 0 0\nv 1 1 1\nf 0 2 3");
        Reject(() => Static("FactoryMapCadImporter", "ImportFile", invalid, false));
        File.WriteAllText(invalid, "v NaN 0 0");
        Reject(() => Static("FactoryMapCadImporter", "ImportFile", invalid, false));
        var tooMany = Path.Combine(directory, "oversize.obj");
        File.WriteAllText(tooMany, string.Concat(Enumerable.Repeat("v 0 0 0\n", 30001)));
        Reject(() => Static("FactoryMapCadImporter", "ImportFile", tooMany, false));
        var altered = Call(yUp, "Clone")!;
        Set(((IList)Get(Get(altered, "CadGeometry"), "Vertices"))[0]!, "X", .2D);
        Reject(() => Static("FactoryMapCustomEquipmentStore", "Validate", altered));
        using (var failedDialog = (Form)Activator.CreateInstance(Type("FactoryMapCustomEquipmentForm"), new object[] { true })!)
        {
            Call(failedDialog, "LoadGeometry", obj);
            Reject(() => Call(failedDialog, "LoadGeometry", invalid));
            Call(failedDialog, "ApplyDefinition");
            Require(failedDialog.DialogResult != DialogResult.OK,
                "A failed CAD import must not save a previous model as though the new import succeeded.");
        }
        var badDwg = Path.Combine(directory, "bad.dwg");
        File.WriteAllText(badDwg, "not an AutoCAD drawing");
        Reject(() => Static("FactoryMapCadImporter", "ImportFile", badDwg, false));
        using var libraryDialog = (Form)Activator.CreateInstance(Type("FactoryMapEquipmentLibraryForm"), new object?[] { Path.Combine(directory, "empty-cad-library") })!;
        var buttons = Descendants(libraryDialog).OfType<Button>().Select(b => b.Text).ToArray();
        Require(buttons.Contains("Import OBJ / CAD") && !buttons.Contains("Upload PNG/JPG"),
            "Add Item must offer real OBJ/CAD import instead of image upload.");
        Console.WriteLine("OBJ/DXF/DWG: actual parsers, 3D faces, flat CAD outlines, Y-up, UI import, persistence, cloning, deletion and invalid-file guards passed.");
    }

    private static void ValidateRendering(object asset)
    {
        using var bitmap = new Bitmap(400, 300);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        using var hit = new GraphicsPath(FillMode.Winding);
        Func<float, float, float, PointF> top = (x, y, z) => new(200 + x * 3, 150 + y * 3);
        Static("FactoryMapEquipmentRenderer", "Draw", graphics, asset, top, hit);
        Require(hit.IsVisible(200, 150) && hit.GetBounds().Width == 96 && hit.GetBounds().Height == 48,
            "Custom symbols must render/select at their true metre envelope.");
        var planPixels = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.R > 180 && pixel.G < 100 && pixel.B < 100) planPixels++;
            }
        Require(planPixels > 1000, "The 2D plan symbol must show the uploaded item pixels.");
        Set(asset, "Rotation", 90F); Set(asset, "ScaleX", 2F);
        var bounds = (RectangleF)Static("FactoryMapPlacement", "AssetFootprint", asset)!;
        Require(Math.Abs(bounds.Width - 16) < .001 && Math.Abs(bounds.Height - 64) < .001,
            "Custom footprints must follow declared size, scale and rotation.");
        Set(asset, "Rotation", 0F); Set(asset, "ScaleX", 1F);
        using var cache = (IDisposable)Activator.CreateInstance(Type("FactoryMapRenderCache"))!;
        Func<float, float, float, PointF> project = (x, y, z) => new((x - y) * .7F, (x + y) * .35F - z * .86F);
        using var cachedHit = (GraphicsPath)Call(cache, "Draw", graphics, asset, project, 1F, 1F, 1F, new PointF(200, 150), 2F, new Rectangle(0, 0, 400, 300))!;
        Require(cachedHit.PointCount >= 8, "Actual map rendering must include image and selectable 3D envelope.");
        using var preview = (Control)Activator.CreateInstance(Type("FactoryMapItemPreview"))!;
        preview.Size = new Size(400, 460);
        Call(preview, "SetAsset", asset);
        using var previewImage = new Bitmap(preview.Width, preview.Height);
        preview.DrawToBitmap(previewImage, preview.ClientRectangle);
        var redPixels = 0;
        for (var y = 0; y < previewImage.Height; y++)
            for (var x = 0; x < previewImage.Width; x++)
            {
                var pixel = previewImage.GetPixel(x, y);
                if (pixel.R > 180 && pixel.G < 100 && pixel.B < 100) redPixels++;
            }
        Require(redPixels > 2000, "Uploaded equipment previews must zoom to fill the available space.");
        using var isoBitmap = new Bitmap(400, 300);
        using var isoGraphics = Graphics.FromImage(isoBitmap);
        isoGraphics.Clear(Color.White);
        using var isoHit = new GraphicsPath(FillMode.Winding);
        Func<float, float, float, PointF> isometric = (x, y, z) => new(200 + (x - y) * 3, 150 + (x + y) * 1.5F - z * 2);
        Static("FactoryMapEquipmentRenderer", "Draw", isoGraphics, asset, isometric, isoHit);
        var facadePixels = 0;
        for (var y = 0; y < isoBitmap.Height; y++)
            for (var x = 0; x < isoBitmap.Width; x++)
            {
                var pixel = isoBitmap.GetPixel(x, y);
                if (pixel.R > 180 && pixel.G < 100 && pixel.B < 100) facadePixels++;
            }
        Require(facadePixels > 1000, "Isometric equipment must display the uploaded image on a generated 3D face.");
    }

    private static void ValidateLibraryAndEditing(object asset, object definition, string library)
    {
        Static("FactoryMapCustomEquipmentStore", "Save", definition, library);
        var imagePath = Path.Combine(library, "upload-form-test.png");
        using (var image = (Bitmap)Static("FactoryMapCustomEquipmentStore", "DecodeImage", definition)!)
            image.Save(imagePath, ImageFormat.Png);
        using (var upload = (Form)Activator.CreateInstance(Type("FactoryMapCustomEquipmentForm"))!)
        {
            Call(upload, "ApplyDefinition");
            Require(upload.DialogResult != DialogResult.OK && Field<Label>(upload, "_error").Text.Contains("Choose"),
                "The actual upload form must explicitly reject saving without an image.");
            Call(upload, "LoadImage", imagePath);
            Field<TextBox>(upload, "_name").Text = "User uploaded compressor";
            Field<NumericUpDown>(upload, "_length").Value = 5;
            Field<NumericUpDown>(upload, "_width").Value = 2;
            Field<NumericUpDown>(upload, "_height").Value = 3;
            using var generatedPreview = new Bitmap(Field<Control>(upload, "_generatedPreview").Width,
                Field<Control>(upload, "_generatedPreview").Height);
            Field<Control>(upload, "_generatedPreview").DrawToBitmap(generatedPreview,
                Field<Control>(upload, "_generatedPreview").ClientRectangle);
            var generatedPixels = 0;
            for (var y = 0; y < generatedPreview.Height; y++)
                for (var x = 0; x < generatedPreview.Width; x++)
                {
                    var pixel = generatedPreview.GetPixel(x, y);
                    if (pixel.R > 180 && pixel.G < 100 && pixel.B < 100) generatedPixels++;
                }
            Require(generatedPixels > 2000, "Uploading an image must immediately generate and preview the 3D equipment item.");
            Call(upload, "ApplyDefinition");
            var imported = Get(upload, "Definition");
            Require(upload.DialogResult == DialogResult.OK && (string)Get(imported, "Name") == "User uploaded compressor" &&
                    (float)Get(imported, "LengthMeters") == 5 && (float)Get(imported, "WidthMeters") == 2 &&
                    (float)Get(imported, "HeightMeters") == 3,
                "The actual upload form must retain the user's name and metre dimensions.");
        }
        File.Delete(imagePath);
        using (var chooser = (Form)Activator.CreateInstance(Type("FactoryMapEquipmentLibraryForm"), new object[] { library })!)
        {
            chooser.Show();
            Application.DoEvents();
            var search = Field<TextBox>(chooser, "_search");
            var list = Field<ListBox>(chooser, "_list");
            var category = Field<ComboBox>(chooser, "_category");
            var delete = Field<Button>(chooser, "_delete");
            search.Text = "MY CUSTOM PUMP";
            Require(list.Items.Count == 1, "Library must case-insensitively search uploaded names.");
            Require(delete.Enabled, "Uploaded library items must expose the delete action.");
            Call(chooser, "ChooseSelection");
            Require((string)Get(Get(chooser, "SelectedTemplate"), "Model") == "Custom equipment (image)", "Library must return the real custom definition.");
            search.Text = "no such equipment";
            Require(list.Items.Count == 0 && !Field<Button>(chooser, "_place").Enabled, "No-match results must disable placement.");
            search.Text = "chiller"; category.SelectedItem = "Chillers";
            Require(list.Items.Count > 3 && !delete.Enabled, "Built-in equipment must remain protected from the uploaded-item delete action.");
            category.SelectedItem = "Uploaded equipment"; search.Clear();
            Require(list.Items.Count == 1, "Uploaded equipment filter must retain reusable custom definitions.");
            foreach (var size in new[] { new Size(1000, 720), new Size(800, 560) })
            {
                chooser.Size = size;
                Application.DoEvents();
                using var capture = new Bitmap(chooser.Width, chooser.Height);
                chooser.DrawToBitmap(capture, chooser.ClientRectangle);
                var button = Field<Button>(chooser, "_place");
                Require(button.Parent!.ClientRectangle.Contains(button.Bounds) && list.Width > 250 &&
                        Field<Control>(chooser, "_preview").Height > 200 &&
                        chooser.ClientRectangle.Contains(chooser.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))),
                    $"Library controls must remain usable at {size}: Place {button.Bounds} in {button.Parent.ClientRectangle}, screen bounds {chooser.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))} in form {chooser.ClientRectangle}, list {list.Size}, preview {Field<Control>(chooser, "_preview").Size}.");
                var evidence = Environment.GetEnvironmentVariable("EOEMS_MAP_LIBRARY_EVIDENCE");
                if (!string.IsNullOrWhiteSpace(evidence))
                {
                    Directory.CreateDirectory(evidence);
                    capture.Save(Path.Combine(evidence, $"EquipmentLibrary-{size.Width}x{size.Height}.png"), ImageFormat.Png);
                }
            }
            chooser.Hide();
        }
        using var host = new Form { Size = new Size(1440, 900) };
        using var view = new FactoryMapView(new UserSession { RoleName = "Administrator" }, Array.Empty<MeterSnapshot>(), null!);
        host.Controls.Add(view); host.Show();
        var canvas = Field<Control>(view, "_canvas");
        var assets = Field<IList>(view, "_assets");
        var originalCount = assets.Count;
        Call(view, "ToggleEditMode", null, EventArgs.Empty);
        Call(canvas, "SetPlanView");
        Call(canvas, "OnKeyDown", new KeyEventArgs(Keys.Right));
        Call(view, "PlaceLibraryItem", asset);
        var placed = assets[assets.Count - 1]!;
        Require(assets.Count == originalCount + 1 && (string)Get(placed, "Level") == (string)Get(canvas, "ActiveLevel") &&
                (PointF)Get(placed, "Position") == (PointF)Call(canvas, "PlacementCenter")! &&
                (float)Get(placed, "BaseHeightMeters") == 0 && ReferenceEquals(Get(canvas, "SelectedAsset"), placed),
            "Library placement must use the visible view center, active floor and ground base, with selection ready to drag.");
        using (var image = new Bitmap(canvas.Width, canvas.Height)) canvas.DrawToBitmap(image, canvas.ClientRectangle);
        Require(Field<Dictionary<string, RectangleF>>(canvas, "_handles").Count == 3, "Custom symbols must expose resize/rotate mouse handles.");
        Call(view, "HandleEditCommand", "Duplicate");
        Require(assets.Count == originalCount + 2 && Get(assets[assets.Count - 1]!, "CustomEquipment") != null,
            "Duplicating custom equipment must keep a usable portable image.");
        Call(view, "HandleEditCommand", "Undo");
        Require(assets.Count == originalCount + 1, "Custom duplication must be undoable.");
        Call(view, "HandleEditCommand", "Undo");
        Require(assets.Count == originalCount, "Custom placement must be undoable.");
        Call(view, "HandleEditCommand", "Redo");
        Require(assets.Count == originalCount + 1, "Redo must restore custom equipment and image.");
        placed = assets[assets.Count - 1]!;
        var catalog = typeof(FactoryMapView).GetMethod("EquipmentCatalog", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        using (var properties = (Form)Activator.CreateInstance(Type("FactoryMapItemForm"), new[] { placed, catalog })!)
        {
            Require((string)Field<ComboBox>(properties, "_shape").SelectedItem! == "Custom equipment (image)" &&
                    !Field<ComboBox>(properties, "_shape").Enabled, "Properties must preserve the custom model instead of substituting a built-in.");
            Field<NumericUpDown>(properties, "_scaleX").Value = 200;
            Field<ComboBox>(properties, "_link").SelectedIndex = 1;
            Call(properties, "ApplyItem", null, EventArgs.Empty);
            Require(properties.DialogResult == DialogResult.OK && (float)Get(Get(properties, "Asset"), "ScaleX") == 2 &&
                    Get(Get(properties, "Asset"), "CustomEquipment") != null && (string)Get(Get(properties, "Asset"), "EquipmentId") != "",
                "Custom properties must resize and link existing equipment without substituting a built-in model or losing the uploaded image.");
        }
        Call(view, "CancelLayout", null, EventArgs.Empty);
        Require(assets.Count == originalCount, "Cancel must restore equipment without removing the separately saved library.");
        Require(((IList)Static("FactoryMapCustomEquipmentStore", "Load", library)!).Count == 1, "Library must persist independently of map Cancel.");
        Call(view, "PlaceLibraryItem", asset);
        Require(assets.Count == originalCount, "Library placement must not bypass authorized editing mode.");
        host.Hide();
        using var deleteChooser = (Form)Activator.CreateInstance(Type("FactoryMapEquipmentLibraryForm"), new object[] { library })!;
        deleteChooser.Show();
        Application.DoEvents();
        Field<ComboBox>(deleteChooser, "_category").SelectedItem = "Uploaded equipment";
        Call(deleteChooser, "RemoveCustomDefinition", definition);
        Require(Field<ListBox>(deleteChooser, "_list").Items.Count == 0 &&
                !Field<Button>(deleteChooser, "_delete").Enabled &&
                ((IList)Static("FactoryMapCustomEquipmentStore", "Load", library)!).Count == 0 &&
                Get(asset, "CustomEquipment") != null,
            "Deleting an uploaded item must remove it from the UI and library while preserving existing map copies.");
    }
}
