using System.Collections;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class FactoryMapValidation
{
    private sealed record Asset(string Id, string Category, PointF Position, bool IsPlaced);

    public static void Run()
    {
        var viewType = typeof(MainShellForm).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapView")
            ?? throw new InvalidOperationException("FactoryMapView type was not found.");
        var factory = viewType.GetMethod("CreateAssets", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Factory map equipment inventory was not found.");
        var rawAssets = (IEnumerable)(factory.Invoke(null, null)
            ?? throw new InvalidOperationException("Factory map equipment inventory is empty."));
        var assets = rawAssets.Cast<object>().Select(ReadAsset).ToArray();

        Require(assets.Select(asset => asset.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == assets.Length,
            "Factory map equipment IDs must be unique.");
        var generators = assets.Where(asset => asset.Category == "Generators").OrderBy(asset => asset.Position.Y).ToArray();
        Require(generators.Length == 12, "The generator bank must contain exactly 12 markers.");
        Require(generators.Select(asset => asset.Id).SequenceEqual(new[]
        {
            "GEN-G6", "GEN-G7", "GEN-G8", "GEN-G1", "GEN-G2", "GEN-G3",
            "GEN-G4", "GEN-G5", "GEN-G10", "GEN-G11", "GEN-G12", "GEN-G9"
        }), "Generator markers must follow the approved top-to-bottom order.");

        RequireIds(assets, "Compressors", Enumerable.Range(1, 6).Select(index => $"COMP-{index}"));
        RequireIds(assets, "UPS", new[]
        {
            "UPS-D1", "UPS-D2", "UPS-D3", "UPS-D4",
            "UPS-C1", "UPS-C2", "UPS-C3", "UPS-C4", "UPS-C5",
            "UPS-A3", "UPS-A2", "UPS-A1", "UPS-B3", "UPS-B2", "UPS-B1"
        });
        RequireIds(assets, "Battery Rooms", Enumerable.Range(1, 4).Select(index => $"BATTERY-ROOM-B{index}"));
        RequireIds(assets, "Transformers", new[] { "TRANSFORMER-T1", "TRANSFORMER-T2", "TRANSFORMER-T3", "TRANSFORMER-T4" });
        RequireIds(assets, "Chillers", new[] { "CHILLER-C1", "CHILLER-C2", "CHILLER-C3" });
        RequireIds(assets, "Production Lines", new[] { "LINE-PET1", "LINE-PET2", "LINE-PET3", "LINE-PET4", "LINE-PET5", "LINE-PET6", "CANLINE-1" });
        Require(assets.Where(asset => asset.Category == "Chillers").All(asset => !asset.IsPlaced),
            "Chiller markers must remain unplaced until their positions are confirmed.");

        var byId = assets.ToDictionary(asset => asset.Id, StringComparer.OrdinalIgnoreCase);
        var compressorPositions = new Dictionary<string, PointF>
        {
            ["COMP-1"] = new(133F / 1365F, 544F / 766F),
            ["COMP-2"] = new(133F / 1365F, 514F / 766F),
            ["COMP-3"] = new(133F / 1365F, 390F / 766F),
            ["COMP-4"] = new(183F / 1365F, 465F / 766F),
            ["COMP-5"] = new(133F / 1365F, 482F / 766F),
            ["COMP-6"] = new(133F / 1365F, 420F / 766F)
        };
        foreach (var (id, expected) in compressorPositions)
        {
            Require(Math.Abs(byId[id].Position.X - expected.X) < 0.0001F &&
                    Math.Abs(byId[id].Position.Y - expected.Y) < 0.0001F,
                $"{id} must remain at its matching approved blue compressor marker.");
        }
        Require(byId["UPS-D1"].Position.Y < byId["UPS-C1"].Position.Y, "UPS group D must be above group C.");
        Require(byId["UPS-A3"].Position.Y < byId["UPS-A2"].Position.Y &&
                byId["UPS-A2"].Position.Y < byId["UPS-A1"].Position.Y,
            "UPS group A must be ordered A3, A2, A1 from top to bottom.");
        Require(byId["UPS-B3"].Position.Y < byId["UPS-B2"].Position.Y &&
                byId["UPS-B2"].Position.Y < byId["UPS-B1"].Position.Y,
            "UPS group B must be ordered B3, B2, B1 from top to bottom.");
        Require(Math.Abs(byId["TRANSFORMER-T1"].Position.X - byId["TRANSFORMER-T2"].Position.X) < 0.0001F &&
                byId["TRANSFORMER-T2"].Position.Y > byId["TRANSFORMER-T1"].Position.Y,
            "Transformer T2 must be directly below T1.");
        Require(!assets.Any(asset => asset.Category is "CCTV" or "Access Control" or "Fire System"),
            "Unconfirmed security and fire-system devices must not be invented.");
        ValidateLayoutPersistence(typeof(MainShellForm).Assembly);
        ValidateProvisionalDimensions(typeof(MainShellForm).Assembly);
        ValidateCameraControls(typeof(MainShellForm).Assembly);
        ValidateEditableItems(typeof(MainShellForm).Assembly);
        ValidateEditorAndLinks();
        ValidateEquipmentGeometry(typeof(MainShellForm).Assembly);
        ValidateWallsAndWorkspace(typeof(MainShellForm).Assembly);
        FactoryBuildingValidation.Run();
        FactoryMapEnhancementsValidation.Run();
        Console.WriteLine("Factory Map inventory and placement checks passed.");
    }

    public static void RenderViews(string outputDirectory)
    {
        var viewType = typeof(MainShellForm).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapView")
            ?? throw new InvalidOperationException("FactoryMapView type was not found.");
        var factory = viewType.GetMethod("CreateAssets", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Factory map equipment inventory was not found.");
        var assets = factory.Invoke(null, null)
            ?? throw new InvalidOperationException("Factory map equipment inventory is empty.");
        var canvasType = typeof(MainShellForm).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapCanvas")
            ?? throw new InvalidOperationException("FactoryMapCanvas type was not found.");
        var constructor = canvasType.GetConstructor(BindingFlags.Public | BindingFlags.Instance, null, new[] { assets.GetType() }, null)
            ?? throw new InvalidOperationException("FactoryMapCanvas constructor was not found.");
        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);

        using var canvas = (Control)(constructor.Invoke(new[] { assets })
            ?? throw new InvalidOperationException("Factory map canvas could not be created."));
        canvas.Size = new Size(1365, 766);
        canvas.Visible = true;
        canvas.CreateControl();
        Application.DoEvents();

        using var bitmap = new Bitmap(canvas.Width, canvas.Height);
        canvasType.GetMethod("SetTopView")?.Invoke(canvas, null);
        Application.DoEvents();
        canvas.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        bitmap.Save(Path.Combine(directory, "FactoryMap-TopView.png"), System.Drawing.Imaging.ImageFormat.Png);

        canvasType.GetMethod("SetIsometricView")?.Invoke(canvas, null);
        Application.DoEvents();
        canvas.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        bitmap.Save(Path.Combine(directory, "FactoryMap-IsometricView.png"), System.Drawing.Imaging.ImageFormat.Png);
        canvasType.GetProperty("ShowWalls")!.SetValue(canvas, false);
        canvas.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        bitmap.Save(Path.Combine(directory, "FactoryMap-WallsHidden.png"), System.Drawing.Imaging.ImageFormat.Png);
        RenderEquipmentGallery(directory);
        using var editor = new FactoryMapView(new UserSession { RoleName = "Administrator" }, Array.Empty<MeterSnapshot>(), null!);
        editor.Size = new Size(1440, 900);
        editor.CreateControl();
        Invoke(editor, "ToggleEditMode", null, EventArgs.Empty);
        using var editorBitmap = new Bitmap(editor.Width, editor.Height);
        editor.DrawToBitmap(editorBitmap, editor.ClientRectangle);
        editorBitmap.Save(Path.Combine(directory, "FactoryMap-Editor.png"), System.Drawing.Imaging.ImageFormat.Png);
        editor.Size = new Size(1100, 780);
        using var compactBitmap = new Bitmap(editor.Width, editor.Height);
        editor.DrawToBitmap(compactBitmap, editor.ClientRectangle);
        compactBitmap.Save(Path.Combine(directory, "FactoryMap-Compact.png"), System.Drawing.Imaging.ImageFormat.Png);
        FactoryBuildingValidation.Render(directory);
        FactoryMapEnhancementsValidation.Render(directory);
        Console.WriteLine($"Factory Map previews saved to {directory}");
    }

    private static object NewAssets() => typeof(FactoryMapView).GetMethod("CreateAssets", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, null)!;

    private static object? Get(object item, string property) => item.GetType().GetProperty(property)!.GetValue(item);
    private static void Set(object item, string property, object value) => item.GetType().GetProperty(property)!.SetValue(item, value);
    private static object Clone(object item) => item.GetType().GetMethod("Clone")!.Invoke(item, null)!;
    private static T Field<T>(object item, string name) => (T)item.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(item)!;
    private static void Invoke(object item, string method, params object?[] args) =>
        item.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(item, args);

    private static string Describe(IEnumerable items) => string.Join("\n", items.Cast<object>().Select(item =>
        string.Join("|", new[] { "Id", "Name", "Category", "Location", "Source", "Position", "IsPlaced", "EquipmentId", "Model", "ConveyorLength", "Rotation", "ScaleX", "ScaleY", "ScaleZ", "BodyColor", "IsOpen", "OpeningDirection" }
            .Select(property => Get(item, property)?.ToString()))));

    private static void ValidateEditableItems(Assembly assembly)
    {
        var type = assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapLayoutStore")!;
        var save = type.GetMethod("SaveAssets")!;
        var load = type.GetMethod("LoadAssets")!;
        var legacySave = type.GetMethod("Save")!;
        var directory = Path.Combine(Path.GetTempPath(), "eoems-map-items-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "map.json");
        var defaults = (IList)NewAssets();
        var original = Describe(defaults);
        try
        {
            Require(Describe((IEnumerable)load.Invoke(null, new object?[] { defaults, path })!) == original,
                "A missing layout must preserve all default equipment and provisional placements.");
            legacySave.Invoke(null, new object?[] {
                new[] { new KeyValuePair<string, PointF>("GEN-G6", new PointF(0.25F, 0.75F)) },
                defaults.Cast<object>().Select(item => (string)Get(item, "Id")!), path });
            var migrated = (IList)load.Invoke(null, new object?[] { defaults, path })!;
            Require((PointF)Get(migrated[0]!, "Position")! == new PointF(0.25F, 0.75F), "Legacy positions must migrate.");
            Require(Describe(defaults) == original, "Loading a map must not mutate seed items.");
            migrated.RemoveAt(0);
            var transformer = migrated.Cast<object>().Single(item => (string)Get(item, "Id")! == "TRANSFORMER-T1");
            Set(transformer, "Name", "Renamed substation");
            Require((string)Get(transformer, "EquipmentId")! == "TRANSFORMER-T1", "Renaming must retain the stable equipment link.");
            var chiller = migrated.Cast<object>().First(item => (string)Get(item, "Category")! == "Chillers");
            Set(chiller, "IsPlaced", true);
            Set(chiller, "Position", new PointF(0.6F, 0.4F));
            var belt = Clone(migrated.Cast<object>().First(item => (string)Get(item, "Category")! == "Production Lines"));
            Set(belt, "Id", "MAP-TEST-BELT");
            Set(belt, "Name", "Extended conveyor");
            Set(belt, "Model", "Conveyor");
            Set(belt, "EquipmentId", "");
            Set(belt, "ConveyorLength", 240F);
            Set(belt, "Rotation", 90F);
            Set(belt, "ScaleX", 2F);
            Set(belt, "ScaleY", 0.5F);
            Set(belt, "ScaleZ", 1.5F);
            Set(belt, "BodyColor", "#CC2266");
            migrated.Add(belt);
            save.Invoke(null, new object?[] { migrated, path });
            var restored = (IList)load.Invoke(null, new object?[] { defaults, path })!;
            Require(Describe(restored) == Describe(migrated), "Every item property, addition, deletion and placement must survive reload.");
            Require(!restored.Cast<object>().Any(item => (string)Get(item, "Id")! == "GEN-G6"), "Deleted default items must not respawn.");
            var previous = (IList)load.Invoke(null, new object?[] { defaults, path + ".bak" })!;
            Require(previous.Count == defaults.Count, "Migration must retain a readable legacy backup.");

            var savedText = File.ReadAllText(path);
            var oldDocument = JsonNode.Parse(savedText)!;
            foreach (var item in oldDocument["Assets"]!.AsArray())
                foreach (var property in new[] { "ScaleX", "ScaleY", "ScaleZ", "BodyColor", "MeterSourceId" })
                    item!.AsObject().Remove(property);
            File.WriteAllText(path, oldDocument.ToJsonString());
            var oldItems = (IList)load.Invoke(null, new object?[] { defaults, path })!;
            Require(oldItems.Cast<object>().All(item => (float)Get(item, "ScaleX")! == 1 &&
                (float)Get(item, "ScaleY")! == 1 && (float)Get(item, "ScaleZ")! == 1 && (string)Get(item, "BodyColor")! == "" &&
                (string)Get(item, "MeterSourceId")! == ""),
                "Existing version-2 maps without appearance fields must load at original size and colors.");
            File.WriteAllText(path, savedText);
            foreach (var (property, value) in new (string, object)[] {
                ("Name", ""), ("Category", "Unknown"), ("Model", "Unknown"),
                ("Position", new PointF(float.NaN, 0.5F)), ("Position", new PointF(4.01F, 0)),
                ("ConveyorLength", 19F), ("ConveyorLength", 401F), ("ConveyorLength", float.PositiveInfinity),
                ("Rotation", -1F), ("Rotation", 360F), ("Rotation", float.NaN),
                ("ScaleX", 0.09F), ("ScaleX", 5.01F), ("ScaleY", float.NaN), ("ScaleZ", float.PositiveInfinity),
                ("BodyColor", "red"), ("BodyColor", "#12345Z"), ("BodyColor", "#11223344") })
            {
                var previousValue = Get(belt, property)!;
                Set(belt, property, value);
                RejectInvalid(() => save.Invoke(null, new object?[] { migrated, path }));
                Require(File.ReadAllText(path) == savedText, $"Rejected {property} must not overwrite the saved map.");
                Set(belt, property, previousValue);
            }
            migrated.Add(Clone(belt));
            RejectInvalid(() => save.Invoke(null, new object?[] { migrated, path }));
            migrated.RemoveAt(migrated.Count - 1);
            File.WriteAllText(path, savedText.Replace("\"ConveyorLength\": 240", "\"ConveyorLength\": 999"));
            RejectInvalid(() => load.Invoke(null, new object?[] { defaults, path }));
            File.WriteAllText(path, savedText);
            migrated.Clear();
            save.Invoke(null, new object?[] { migrated, path });
            Require(((IList)load.Invoke(null, new object?[] { defaults, path })!).Count == 0, "An intentionally empty map must stay empty after restart.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void RejectInvalid(Action action)
    {
        try
        {
            action();
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException("Invalid map data was accepted.");
    }

    private static void ValidateEditorAndLinks()
    {
        using var view = new FactoryMapView(new UserSession { RoleName = "Administrator" }, Array.Empty<MeterSnapshot>(), null!);
        view.Size = new Size(1440, 900);
        view.CreateControl();
        var items = Field<IList>(view, "_assets");
        var original = Describe(items);
        Invoke(view, "ToggleEditMode", null, EventArgs.Empty);
        Require(Field<bool>(view, "_editing"), "Authenticated administrators must be able to enter editing.");
        var canvas = Field<Control>(view, "_canvas");
        Require(!Descendants(view).OfType<CheckBox>().Any(toggle => toggle.Text == "Labels"), "Names must be hover-only, without a permanent-label toggle.");
        using (var bitmap = new Bitmap(view.Width, view.Height))
            view.DrawToBitmap(bitmap, view.ClientRectangle);
        var mapGrid = (TableLayoutPanel)canvas.Parent!;
        Require(mapGrid.GetRow(canvas) == 2, "The map must occupy a dedicated row, not cover the toolbars.");
        var editTools = Field<FlowLayoutPanel>(view, "_editTools");
        Require(editTools.Top >= canvas.Bottom, "Editing actions must remain visible below the map.");
        Require(editTools.ClientRectangle.Contains(Field<Button>(view, "_saveButton").Bounds), "Save must be accessible without scrolling away from editing actions.");
        var unplaced = items.Cast<object>().First(item => !(bool)Get(item, "IsPlaced")!);
        Field<ComboBox>(view, "_assetPicker").SelectedItem = unplaced;
        Require(ReferenceEquals(canvas.GetType().GetProperty("SelectedAsset")!.GetValue(canvas), unplaced), "The item list must select unplaced equipment.");
        var removed = items[0]!;
        items.RemoveAt(0);
        Set(items[0]!, "Name", "Unsaved name");
        Set(items[0]!, "EquipmentId", "");
        Set(unplaced, "IsPlaced", true);
        Set(unplaced, "Rotation", 90F);
        Set(unplaced, "ScaleX", 2F);
        Set(unplaced, "ScaleY", 3F);
        Set(unplaced, "ScaleZ", 0.5F);
        Set(unplaced, "BodyColor", "#33AA66");
        var added = Clone(removed);
        Set(added, "Id", "MAP-UNSAVED");
        items.Add(added);
        Invoke(view, "CancelLayout", null, EventArgs.Empty);
        Require(Describe(items) == original, "Cancel must restore deleted items and every property and remove new items.");
        Require(!Field<bool>(view, "_editing"), "Cancel must exit editing.");

        var transformer = items.Cast<object>().Single(item => (string)Get(item, "Id")! == "TRANSFORMER-T1");
        Set(transformer, "Name", "Renamed transformer");
        Set(transformer, "EquipmentId", "TRANSFORMER-T2");
        view.SetReadings(new[] { new MeterSnapshot { TransformerName = "T2", Quality = MeterQuality.Good,
            CapturedAtUtc = DateTime.UtcNow, PowerState = TransformerPowerState.On } });
        canvas.GetType().GetMethod("SelectAsset")!.Invoke(canvas, new[] { transformer });
        Require(Field<Label>(view, "_selectedStatus").Text.Contains("Verified On"), "Readings must follow the linked transformer, not the display name.");
        Set(transformer, "EquipmentId", "");
        view.SetReadings(Array.Empty<MeterSnapshot>());
        Require(Field<Label>(view, "_selectedStatus").Text == "Unknown / Not Connected", "Unlinking must remove live transformer readings.");

        var upsItem = items.Cast<object>().First(item => (string)Get(item, "Category")! == "UPS");
        Set(upsItem, "Name", "Renamed UPS");
        var upsId = (string)Get(upsItem, "EquipmentId")!;
        var ups = new UpsSnapshot { DeviceId = upsId, Quality = UpsQuality.Good, TimestampUtc = DateTime.UtcNow,
            Values = new Dictionary<string, double> { ["ChargePercent"] = 75 } };
        var upsData = new Dictionary<string, UpsSnapshot> { [upsId] = ups };
        view.SetReadings(Array.Empty<MeterSnapshot>(), upsData);
        canvas.GetType().GetMethod("SelectAsset")!.Invoke(canvas, new[] { upsItem });
        Require(Field<Label>(view, "_selectedDetails").Text.Contains("75.0 %"), "Linked UPS readings must use real registry IDs even after renaming.");
        ups.Quality = UpsQuality.Timeout;
        view.SetReadings(Array.Empty<MeterSnapshot>(), upsData);
        Require(Field<Label>(view, "_selectedDetails").Text.Contains("Battery charge: --"), "Failed UPS polls must never display previous values as live.");

        var formType = typeof(FactoryMapView).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapItemForm")!;
        var catalog = typeof(FactoryMapView).GetMethod("EquipmentCatalog", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        using var dialog = (Form)Activator.CreateInstance(formType, new[] { upsItem, catalog })!;
        var beforeEdit = Describe(new[] { upsItem });
        Field<TextBox>(dialog, "_name").Text = "Updated UPS label";
        Invoke(dialog, "ApplyItem", null, EventArgs.Empty);
        Require(dialog.DialogResult == DialogResult.OK, "Valid item edits must apply.");
        var edited = Get(dialog, "Asset")!;
        Require((string)Get(edited, "Name")! == "Updated UPS label" && (string)Get(edited, "EquipmentId")! == upsId, "Editing a label must retain the equipment link.");
        Require(Describe(new[] { upsItem }) == beforeEdit, "The dialog must edit a copy until Apply.");

        using var linkDialog = (Form)Activator.CreateInstance(formType, new[] { upsItem, catalog })!;
        var linkedIndex = ((IEnumerable)catalog).Cast<object>().ToList().FindIndex(item => (string)Get(item, "EquipmentId")! == "TRANSFORMER-T1");
        Field<ComboBox>(linkDialog, "_link").SelectedIndex = linkedIndex + 1;
        Invoke(linkDialog, "ApplyItem", null, EventArgs.Empty);
        var relinked = Get(linkDialog, "Asset")!;
        Require((string)Get(relinked, "EquipmentId")! == "TRANSFORMER-T1" && (string)Get(relinked, "Category")! == "Transformers",
            "Choosing system equipment must link the actual stable ID and update its category and shape.");

        using var beltDialog = (Form)Activator.CreateInstance(formType, new[] { upsItem, catalog })!;
        Field<ComboBox>(beltDialog, "_link").SelectedIndex = 0;
        Field<ComboBox>(beltDialog, "_category").SelectedItem = "Production Lines";
        Field<ComboBox>(beltDialog, "_shape").SelectedItem = "Conveyor";
        Field<NumericUpDown>(beltDialog, "_length").Value = 240;
        Field<NumericUpDown>(beltDialog, "_rotation").Value = 45;
        Field<NumericUpDown>(beltDialog, "_scaleX").Value = 200;
        Field<NumericUpDown>(beltDialog, "_scaleY").Value = 50;
        Field<NumericUpDown>(beltDialog, "_scaleZ").Value = 150;
        Field<CheckBox>(beltDialog, "_originalColor").Checked = false;
        Field<NumericUpDown>(beltDialog, "_x").Value = 30;
        Field<CheckBox>(beltDialog, "_placed").Checked = true;
        Invoke(beltDialog, "ApplyItem", null, EventArgs.Empty);
        var editedBelt = Get(beltDialog, "Asset")!;
        Require((string)Get(editedBelt, "EquipmentId")! == "" && (float)Get(editedBelt, "ConveyorLength")! == 240 &&
            (float)Get(editedBelt, "Rotation")! == 45 && Math.Abs(((PointF)Get(editedBelt, "Position")!).X - 0.3F) < 0.0001F,
            "The item dialog must apply conveyor extension, rotation, placement and unlinking.");
        Require((float)Get(editedBelt, "ScaleX")! == 2 && (float)Get(editedBelt, "ScaleY")! == 0.5F &&
            (float)Get(editedBelt, "ScaleZ")! == 1.5F && (string)Get(editedBelt, "BodyColor")! == "#4682B4",
            "The item dialog must apply independent dimensions and the selected body color.");
        var livePreview = Field<Control>(beltDialog, "_preview");
        Require((float)Get(Field<object>(livePreview, "_asset"), "ScaleX")! == 2,
            "The preview must update when size controls change, before Apply.");

        using var nxlDialog = (Form)Activator.CreateInstance(formType, new[] { upsItem, catalog })!;
        var nxlButton = Descendants(nxlDialog).OfType<Button>().Single(button => button.Text == "NXL 800 kVA");
        Invoke(nxlButton, "OnClick", EventArgs.Empty);
        Require((string)Field<ComboBox>(nxlDialog, "_shape").SelectedItem! == "Vertiv NXL 800 kVA",
            "The NXL shortcut must select the distinct 800 kVA UPS shape.");
        Require((string)Get(Field<object>(Field<Control>(nxlDialog, "_preview"), "_asset"), "Model")! == "Vertiv NXL 800 kVA",
            "The live preview must display the NXL 800 kVA model.");

        using var panelDialog = (Form)Activator.CreateInstance(formType, new[] { upsItem, catalog })!;
        Field<ComboBox>(panelDialog, "_link").SelectedIndex = 0;
        Field<ComboBox>(panelDialog, "_category").SelectedItem = "Electrical Panels";
        Require(new[] { "Wall panel", "Wall panel with energy meter", "Floor panel", "Floor panel with energy meter",
            "Switchboard lineup", "Switchboard lineup with energy meters", "ATS cabinet", "Motor control center",
            "ACB panel with energy meter", "ATS panel with energy meter", "MTS panel with energy meter", "Load panel with energy meter",
            "Eaton RMU switchgear", "SF6 circuit breaker" }.All(name => Field<ComboBox>(panelDialog, "_shape").Items.Contains(name)),
            "Panel choices must retain existing variants and include metered power panels and HV switchgear.");
        Field<ComboBox>(panelDialog, "_shape").SelectedItem = "Switchboard lineup with energy meters";
        Invoke(panelDialog, "ApplyItem", null, EventArgs.Empty);
        Require((string)Get(Get(panelDialog, "Asset")!, "Model")! == "Switchboard lineup with energy meters", "Metered panels must be addable as visual-only map items.");

        using var preview = new FactoryMapView(UserSession.OfflinePreview(), Array.Empty<MeterSnapshot>(), null!);
        Invoke(preview, "ToggleEditMode", null, EventArgs.Empty);
        Require(!Field<bool>(preview, "_editing"), "Offline previews must not edit the map.");
        using var viewer = new FactoryMapView(new UserSession { RoleName = "Operator" }, Array.Empty<MeterSnapshot>(), null!);
        Invoke(viewer, "ToggleEditMode", null, EventArgs.Empty);
        Require(!Field<bool>(viewer, "_editing"), "Non-administrators must not edit the map.");
    }

    private static IEnumerable<Control> Descendants(Control control) =>
        control.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));

    private static void ValidateWallsAndWorkspace(Assembly assembly)
    {
        var wallsType = assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapWalls")!;
        var walls = ((IEnumerable)wallsType.GetMethod("Create")!.Invoke(null, null)!).Cast<object>().ToArray();
        Require(walls.Length > 90, "Room walls must trace the factory rooms, not just a generic perimeter.");
        Require(walls.Select(wall => Get(wall, "Room")).Distinct().Count() >= 20, "Walls must include the main rooms shown on the floor plan.");
        foreach (var wall in walls)
        {
            var start = (PointF)Get(wall, "Start")!;
            var finish = (PointF)Get(wall, "Finish")!;
            Require(start != finish && new[] { start.X, start.Y, finish.X, finish.Y }.All(value => float.IsFinite(value) && value >= 0 && value <= 1),
                "Every wall must have valid normalized endpoints and nonzero length.");
        }
        Require(!walls.Where(wall => (string)Get(wall, "Room")! == "PET 1").Any(wall =>
        {
            var start = (PointF)Get(wall, "Start")!;
            var finish = (PointF)Get(wall, "Finish")!;
            return Math.Abs(start.X * 1365 - 733) < 0.1 && Math.Abs(finish.X * 1365 - 733) < 0.1 &&
                Math.Min(start.Y, finish.Y) * 766 < 508 && Math.Max(start.Y, finish.Y) * 766 > 508;
        }), "The traced PET 1 door gap must not be closed by a wall.");

        var topWall = walls.Single(wall => (string)Get(wall, "Room")! == "Warehouse" &&
            Math.Abs(((PointF)Get(wall, "Start")!).Y * 766 - 21) < 0.1 &&
            Math.Abs(((PointF)Get(wall, "Finish")!).Y * 766 - 21) < 0.1);
        using var wallBitmap = new Bitmap(500, 300);
        using var wallGraphics = Graphics.FromImage(wallBitmap);
        Func<float, float, float, PointF> projection = (x, y, z) => new PointF(100 + x * 0.5F, 100 + y * 0.25F - z);
        void DrawWall(int opacity, FactoryMapDimensions dimensions)
        {
            wallGraphics.Clear(Color.White);
            wallsType.GetMethod("Draw")!.Invoke(null, new object[] { wallGraphics, topWall, dimensions, opacity, projection, new Size(1365, 766) });
        }
        var dimensions = FactoryMapDimensions.Defaults();
        DrawWall(5, dimensions);
        var low = wallBitmap.GetPixel(200, 88);
        DrawWall(60, dimensions);
        var high = wallBitmap.GetPixel(200, 88);
        Require(high.R < low.R && high.R > 79 && low.R < 255, "Wall opacity must control actual alpha blending, remaining transparent.");
        DrawWall(22, dimensions);
        Require(wallBitmap.GetPixel(200, 60).ToArgb() == Color.White.ToArgb(), "Default walls must not extend past their provisional height.");
        dimensions.Components.Single(item => item.Name == "External Wall").HeightMeters = 8;
        DrawWall(22, dimensions);
        Require(wallBitmap.GetPixel(200, 60).ToArgb() != Color.White.ToArgb(), "Wall dimensions must change the extruded wall height.");

        using var view = new FactoryMapView(new UserSession { RoleName = "Administrator" }, Array.Empty<MeterSnapshot>(), null!);
        var canvas = Field<Control>(view, "_canvas");
        var originalItems = Describe(Field<IList>(view, "_assets"));
        foreach (var size in new[] { new Size(1440, 900), new Size(1100, 780), new Size(1000, 700) })
        {
            view.Size = size;
            view.CreateControl();
            Invoke(view, "ToggleEditMode", null, EventArgs.Empty);
            using var capture = new Bitmap(size.Width, size.Height);
            view.DrawToBitmap(capture, view.ClientRectangle);
            var mapGrid = (TableLayoutPanel)canvas.Parent!;
            Require(canvas.Height > 250, "The responsive toolbars must leave usable map space.");
            var toolbarRow = (TableLayoutPanel)mapGrid.GetControlFromPosition(0, 0)!;
            var toolbar = (FlowLayoutPanel)toolbarRow.GetControlFromPosition(0, 0)!;
            var buttons = toolbar.Controls.Cast<Control>().Where(control => control.Visible).ToArray();
            Require(!toolbar.WrapContents && toolbar.AutoScroll && buttons.Select(button => button.Top).Distinct().Count() == 1,
                "Map buttons must stay in one horizontally scrollable row at every supported width.");
            var floorPicker = (ComboBox)toolbarRow.GetControlFromPosition(1, 0)!;
            Require(toolbarRow.ClientRectangle.Contains(floorPicker.Bounds) && floorPicker.Items.Count == 4,
                "The floor selector must remain pinned and fully visible at narrow widths.");
            var fullscreen = (IconButton)toolbarRow.GetControlFromPosition(2, 0)!;
            Require(fullscreen.Text == "" && fullscreen.AccessibleName == "Full screen" && !string.IsNullOrEmpty(fullscreen.Glyph),
                "Fullscreen must be icon-only and the final button in the row.");
            Require(toolbarRow.ClientRectangle.Contains(fullscreen.Bounds), "The last fullscreen icon must remain pinned and fully visible at narrow widths.");
            Require(Descendants(view).OfType<IconButton>().Any(button => button.Text == "Edit Map" || button.Text.StartsWith("Editing")), "Map actions must use the shared SCADA action-button style.");
            var displayMenu = Field<ContextMenuStrip>(view, "_layerMenu");
            var display = displayMenu.Items.OfType<ToolStripControlHost>().Single().Control;
            var wallToggle = Descendants(display).OfType<CheckBox>().Single(toggle => toggle.Text == "3D walls");
            wallToggle.Checked = false;
            Require(!(bool)Get(canvas, "ShowWalls")!, "The wall checkbox must hide the 3D overlay.");
            wallToggle.Checked = true;
            Require((bool)Get(canvas, "ShowWalls")!, "The wall checkbox must restore the 3D overlay.");
            var floorToggle = Descendants(display).OfType<CheckBox>().Single(toggle => toggle.Text == "Site ground");
            floorToggle.Checked = false;
            Require((bool)Get(canvas, "ShowWalls")!, "Floor-plan and 3D-wall visibility must be independent.");
            floorToggle.Checked = true;
            Invoke(view, "CancelLayout", null, EventArgs.Empty);
        }
        canvas.GetType().GetMethod("SetIsometricView")!.Invoke(canvas, null);
        using var shown = new Bitmap(canvas.Width, canvas.Height);
        using var hidden = new Bitmap(canvas.Width, canvas.Height);
        canvas.DrawToBitmap(shown, canvas.ClientRectangle);
        canvas.GetType().GetProperty("ShowWalls")!.SetValue(canvas, false);
        canvas.DrawToBitmap(hidden, canvas.ClientRectangle);
        var changed = 0;
        for (var y = 0; y < canvas.Height; y += 2)
            for (var x = 0; x < canvas.Width; x += 2)
                if (shown.GetPixel(x, y) != hidden.GetPixel(x, y)) changed++;
        Require(changed > 1000, "The wall toggle must change rendered walls, not just a checkbox state.");
        Require(Describe(Field<IList>(view, "_assets")) == originalItems, "Wall/layer/camera controls must not alter equipment or saved placements.");
        var menu = Field<ContextMenuStrip>(view, "_layerMenu");
        Require(menu.Items.OfType<ToolStripMenuItem>().Any(item => item.Text == "Sensors & Controls"), "New SCADA library categories need visibility controls.");
        canvas.DrawToBitmap(shown, canvas.ClientRectangle);
        Require(canvas.GetType().GetMethod("DrawLabels", BindingFlags.NonPublic | BindingFlags.Instance) is null,
            "Neither selected nor unselected equipment may draw permanent item-name labels.");
        var symbol = LibraryAssets().Single(item => (string)Get(item, "Model")! == "PLC cabinet");
        var list = Field<IList>(view, "_assets");
        list.Add(symbol);
        Invoke(view, "UpdateSummary");
        Require(Field<Label>(view, "_mappedValue").Text == list.Count.ToString(), "Summary cards must count newly added SCADA symbols.");
        Require(Descendants(view).OfType<Label>().Single(label => label.Name == "count_Sensors_&_Controls").Text == "0 / 1",
            "Visual-only controls symbols must appear in their category summary without a fabricated connection.");
    }

    private static RectangleF DrawModel(Graphics graphics, object asset, float originX, float originY, float scale, bool topView = false)
    {
        var renderer = typeof(FactoryMapView).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapEquipmentRenderer")!;
        Func<float, float, float, PointF> projection = (x, y, z) => topView
            ? new PointF(originX + x * scale, originY + y * scale)
            : new PointF(originX + (x - y) * 0.707F * scale, originY + ((x + y) * 0.354F - z * 0.866F) * scale);
        using var hitArea = new GraphicsPath(FillMode.Winding);
        renderer.GetMethod("Draw")!.Invoke(null, new object[] { graphics, asset, projection, hitArea });
        Require(topView ? hitArea.PointCount >= 4 : hitArea.PointCount > 20,
            $"Equipment needs detailed selectable front geometry; hidden front details may reduce a top-view silhouette: {Get(asset, "Model")} ({hitArea.PointCount} points, top={topView}).");
        return hitArea.GetBounds();
    }

    private static List<object> LibraryAssets()
    {
        var type = typeof(FactoryMapView).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapItemForm")!;
        var shapes = (Dictionary<string, string[]>)type.GetField("Shapes", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var seed = ((IEnumerable)NewAssets()).Cast<object>().First();
        return shapes.SelectMany(category => category.Value.Select(model =>
        {
            var item = Clone(seed);
            Set(item, "Id", "LIBRARY-" + category + "-" + model);
            Set(item, "Name", model);
            Set(item, "Category", category.Key);
            Set(item, "Model", model);
            Set(item, "EquipmentId", "");
            return item;
        })).ToList();
    }

    private static void ValidateEquipmentGeometry(Assembly assembly)
    {
        var assets = ((IEnumerable)NewAssets()).Cast<object>().ToArray();
        using var bitmap = new Bitmap(1000, 1000);
        using var graphics = Graphics.FromImage(bitmap);
        var library = LibraryAssets();
        Require(library.Count >= 40, "The library must cover common electrical, process, controls and safety map items.");
        var store = assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapLayoutStore")!;
        var typedLibrary = (IList)NewAssets();
        typedLibrary.Clear();
        foreach (var item in library) typedLibrary.Add(item);
        store.GetMethod("ValidateAssets")!.Invoke(null, new object[] { typedLibrary });
        foreach (var model in library)
        {
            var bounds = DrawModel(graphics, model, 500, 500, 3);
            Require(bounds.Width > 0.1F && bounds.Height > 0.1F,
                $"Every library type needs nondegenerate selectable geometry, including metre-scale sensors: {Get(model, "Model")}.");
            DrawModel(graphics, model, 500, 500, 3, topView: true);
        }
        var conveyor = Clone(assets.First(item => (string)Get(item, "Category")! == "Production Lines"));
        Set(conveyor, "Model", "Conveyor");
        Set(conveyor, "ConveyorLength", 40F);
        var shortBounds = DrawModel(graphics, conveyor, 500, 500, 1, topView: true);
        Set(conveyor, "ConveyorLength", 200F);
        var longBounds = DrawModel(graphics, conveyor, 500, 500, 1, topView: true);
        Require(Math.Abs(longBounds.Width - shortBounds.Width - 160) < 0.01F, "Conveyor extension must alter rendered belt length by the exact requested amount.");

        var list = (IList)NewAssets();
        list.Clear();
        Set(conveyor, "Position", new PointF(0.5F, 0.5F));
        list.Add(conveyor);
        var canvasType = assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapCanvas")!;
        using var canvas = (Control)Activator.CreateInstance(canvasType, new[] { list })!;
        canvas.Size = new Size(1365, 766);
        canvasType.GetMethod("SetPlanView")!.Invoke(canvas, null);
        canvas.CreateControl();
        canvas.DrawToBitmap(bitmap, new Rectangle(0, 0, canvas.Width, canvas.Height));
        var hitAreas = Field<IDictionary>(canvas, "_hitAreas");
        var area = (GraphicsPath)hitAreas[conveyor]!;
        var rectangle = area.GetBounds();
        var point = new Point((int)(rectangle.Right - 8), (int)(rectangle.Top + rectangle.Height / 2));
        var hit = canvasType.GetMethod("FindAssetAt", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(canvas, new object[] { point });
        Require(ReferenceEquals(hit, conveyor), "The far end of an extended conveyor must be selectable, not only its center.");
        canvasType.GetProperty("EditMode")!.SetValue(canvas, true);
        var originalPosition = (PointF)Get(conveyor, "Position")!;
        Invoke(canvas, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0));
        Invoke(canvas, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, point.X, point.Y, 0));
        var unchanged = (PointF)Get(conveyor, "Position")!;
        Require(Math.Abs(unchanged.X - originalPosition.X) < 0.0001F && Math.Abs(unchanged.Y - originalPosition.Y) < 0.0001F,
            "Grabbing a conveyor's end must not jump its center to the cursor.");
        Invoke(canvas, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, point.X + 20, point.Y, 0));
        Require(((PointF)Get(conveyor, "Position")!).X > originalPosition.X, "Dragging the end must move the entire conveyor.");
        Invoke(canvas, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 0, point.X + 20, point.Y, 0));

        Set(conveyor, "Position", new PointF(0.5F, 0.5F));
        Set(conveyor, "Rotation", 0F);
        canvas.DrawToBitmap(bitmap, new Rectangle(0, 0, canvas.Width, canvas.Height));
        var initialBounds = ((GraphicsPath)Field<IDictionary>(canvas, "_hitAreas")[conveyor]!).GetBounds();
        Set(conveyor, "ScaleX", 2F);
        Set(conveyor, "ScaleY", 3F);
        canvas.DrawToBitmap(bitmap, new Rectangle(0, 0, canvas.Width, canvas.Height));
        var resized = ((GraphicsPath)Field<IDictionary>(canvas, "_hitAreas")[conveyor]!).GetBounds();
        Require(Math.Abs(resized.Width / initialBounds.Width - 2) < 0.001 && Math.Abs(resized.Height / initialBounds.Height - 3) < 0.001,
            "Per-item length and width must resize actual map geometry by the exact selected percentages.");
        Set(conveyor, "Rotation", 90F);
        canvas.DrawToBitmap(bitmap, new Rectangle(0, 0, canvas.Width, canvas.Height));
        var rotated = ((GraphicsPath)Field<IDictionary>(canvas, "_hitAreas")[conveyor]!).GetBounds();
        Require(Math.Abs(rotated.Width - resized.Height) < 0.01 && Math.Abs(rotated.Height - resized.Width) < 0.01,
            "A 90-degree rotation must rotate the resized map item and its hit area, not the camera.");
        canvasType.GetMethod("SetIsometricView")!.Invoke(canvas, null);
        Set(conveyor, "ScaleX", 1F);
        Set(conveyor, "ScaleY", 1F);
        Set(conveyor, "ScaleZ", 1F);
        canvas.DrawToBitmap(bitmap, new Rectangle(0, 0, canvas.Width, canvas.Height));
        var original3D = ((GraphicsPath)Field<IDictionary>(canvas, "_hitAreas")[conveyor]!).GetBounds();
        Set(conveyor, "ScaleZ", 3F);
        canvas.DrawToBitmap(bitmap, new Rectangle(0, 0, canvas.Width, canvas.Height));
        var taller3D = ((GraphicsPath)Field<IDictionary>(canvas, "_hitAreas")[conveyor]!).GetBounds();
        Require(taller3D.Height > original3D.Height && Math.Abs(taller3D.Width - original3D.Width) < 0.01,
            "Changing per-item height must extend actual vertical geometry without changing its horizontal footprint.");

        var panel = Clone(conveyor);
        Set(panel, "Category", "Electrical Panels");
        Set(panel, "EquipmentId", "");
        foreach (var style in new[] { "Wall panel", "Floor panel", "Switchboard lineup" })
        {
            Set(panel, "Model", style);
            using var plainBitmap = new Bitmap(400, 400);
            using var plainGraphics = Graphics.FromImage(plainBitmap);
            plainGraphics.Clear(Color.White);
            DrawModel(plainGraphics, panel, 200, 280, 5);
            Set(panel, "Model", style + (style == "Switchboard lineup" ? " with energy meters" : " with energy meter"));
            using var meteredBitmap = new Bitmap(400, 400);
            using var meteredGraphics = Graphics.FromImage(meteredBitmap);
            meteredGraphics.Clear(Color.White);
            DrawModel(meteredGraphics, panel, 200, 280, 5);
            Require(CountMeterPixels(meteredBitmap) > 5 && CountMeterPixels(plainBitmap) == 0,
                $"{style} must visibly include meter screens only in its metered variant.");
        }
        Set(panel, "Model", "Floor panel");
        Set(panel, "BodyColor", "#FF0000");
        graphics.Clear(Color.White);
        DrawModel(graphics, panel, 500, 500, 5);
        Require(CountPixels(bitmap, Color.Red) > 50, "Choosing a body color must recolor the actual enclosure geometry.");
        Set(panel, "BodyColor", "");
        graphics.Clear(Color.White);
        DrawModel(graphics, panel, 500, 500, 5);
        Require(CountPixels(bitmap, Color.Red) == 0, "Restoring original colors must remove the custom color.");
    }

    private static int CountPixels(Bitmap bitmap, Color color)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y).ToArgb() == color.ToArgb()) count++;
        return count;
    }

    private static int CountMeterPixels(Bitmap bitmap)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.G > pixel.R * 1.3 && pixel.G > pixel.B * 1.3) count++;
            }
        return count;
    }

    private static void RenderEquipmentGallery(string directory)
    {
        var assets = LibraryAssets();
        for (var start = 0; start < assets.Count; start += 12)
        {
        var page = assets.Skip(start).Take(12).ToList();
        using var bitmap = new Bitmap(1200, (int)Math.Ceiling(page.Count / 3D) * 280);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = new Font("Segoe UI", 12, FontStyle.Bold);
        for (var index = 0; index < page.Count; index++)
        {
            var x = index % 3 * 400;
            var y = index / 3 * 280;
            graphics.DrawString((string)Get(page[index], "Model")!, font, Brushes.Black, new RectangleF(x + 12, y + 8, 376, 48));
            using var scratch = new Bitmap(1, 1);
            using var measuring = Graphics.FromImage(scratch);
            var bounds = DrawModel(measuring, page[index], 0, 0, 1);
            var scale = Math.Min(4, Math.Min(360 / bounds.Width, 215 / bounds.Height));
            DrawModel(graphics, page[index], x + 200 - (bounds.Left + bounds.Width / 2) * scale,
                y + 160 - (bounds.Top + bounds.Height / 2) * scale, scale);
        }
        bitmap.Save(Path.Combine(directory, $"FactoryMap-Library-{start / 12 + 1:00}.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        var formType = typeof(FactoryMapView).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapItemForm")!;
        var catalog = typeof(FactoryMapView).GetMethod("EquipmentCatalog", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        var nxl = assets.Single(item => (string)Get(item, "Model")! == "Vertiv NXL 800 kVA");
        using var dialog = (Form)Activator.CreateInstance(formType, new[] { nxl, catalog })!;
        dialog.Show();
        Application.DoEvents();
        using var dialogBitmap = new Bitmap(dialog.Width, dialog.Height);
        dialog.DrawToBitmap(dialogBitmap, new Rectangle(0, 0, dialog.Width, dialog.Height));
        dialogBitmap.Save(Path.Combine(directory, "FactoryMap-ItemAppearance.png"), System.Drawing.Imaging.ImageFormat.Png);
        dialog.Close();
    }

    private static void ValidateCameraControls(Assembly assembly)
    {
        var viewType = assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapView")
            ?? throw new InvalidOperationException("FactoryMapView type was not found.");
        var factory = viewType.GetMethod("CreateAssets", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Factory map equipment inventory was not found.");
        var assets = factory.Invoke(null, null)
            ?? throw new InvalidOperationException("Factory map equipment inventory is empty.");
        var canvasType = assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapCanvas")
            ?? throw new InvalidOperationException("FactoryMapCanvas type was not found.");
        var constructor = canvasType.GetConstructor(BindingFlags.Public | BindingFlags.Instance, null, new[] { assets.GetType() }, null)
            ?? throw new InvalidOperationException("FactoryMapCanvas constructor was not found.");
        using var canvas = (Control)(constructor.Invoke(new[] { assets })
            ?? throw new InvalidOperationException("Factory map canvas could not be created."));
        canvas.Size = new Size(800, 600);
        canvas.Visible = true;
        canvas.CreateControl();
        canvasType.GetMethod("SetTopView")?.Invoke(canvas, null);
        Require(Math.Abs((float)(canvasType.GetField("_yaw", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(canvas) ?? -1.0F)) < 0.0001F,
            "Top View must restore a straight-down camera.");
        canvasType.GetMethod("SetIsometricView")?.Invoke(canvas, null);

        var mouseDown = canvasType.GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Factory map mouse-down handler was not found.");
        var mouseMove = canvasType.GetMethod("OnMouseMove", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Factory map mouse-move handler was not found.");
        var mouseUp = canvasType.GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Factory map mouse-up handler was not found.");
        var mouseWheel = canvasType.GetMethod("OnMouseWheel", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Factory map mouse-wheel handler was not found.");
        mouseDown.Invoke(canvas, new object[] { new MouseEventArgs(MouseButtons.Right, 1, 200, 200, 0) });
        mouseMove.Invoke(canvas, new object[] { new MouseEventArgs(MouseButtons.Right, 0, 240, 200, 0) });
        mouseUp.Invoke(canvas, new object[] { new MouseEventArgs(MouseButtons.Right, 1, 240, 200, 0) });
        var yaw = (float)(canvasType.GetField("_yaw", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(canvas) ?? 0.0F);
        Require(Math.Abs(yaw - (float)(Math.PI / 4.0)) > 0.01F, "Right-drag must rotate the camera.");

        var panBefore = (PointF)(canvasType.GetField("_pan", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(canvas) ?? PointF.Empty);
        mouseDown.Invoke(canvas, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 200, 200, 0) });
        mouseMove.Invoke(canvas, new object[] { new MouseEventArgs(MouseButtons.Left, 0, 225, 215, 0) });
        mouseUp.Invoke(canvas, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 225, 215, 0) });
        var panAfter = (PointF)(canvasType.GetField("_pan", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(canvas) ?? PointF.Empty);
        Require(Math.Abs(panAfter.X - panBefore.X) > 1 || Math.Abs(panAfter.Y - panBefore.Y) > 1,
            "Left-drag must pan the map.");

        var zoomBefore = (float)(canvasType.GetField("_zoom", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(canvas) ?? 1.0F);
        mouseWheel.Invoke(canvas, new object[] { new MouseEventArgs(MouseButtons.None, 0, 300, 300, 120) });
        Thread.Sleep(20);
        Invoke(canvas, "AdvanceZoom");
        var zoomAfter = (float)(canvasType.GetField("_zoom", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(canvas) ?? 1.0F);
        Require(zoomAfter > zoomBefore, "Mouse-wheel input must zoom in.");
        mouseWheel.Invoke(canvas, new object[] { new MouseEventArgs(MouseButtons.None, 0, 300, 300, -120) });
        Thread.Sleep(20);
        Invoke(canvas, "AdvanceZoom");
        var zoomOut = (float)(canvasType.GetField("_zoom", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(canvas) ?? 1.0F);
        Require(zoomOut < zoomAfter, "Mouse-wheel input must zoom out.");
    }

    private static void RequireIds(IEnumerable<Asset> assets, string category, IEnumerable<string> expected)
    {
        var actualIds = assets.Where(asset => asset.Category == category).Select(asset => asset.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedIds = expected.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Require(actualIds.SetEquals(expectedIds), $"{category} equipment IDs do not match the approved inventory.");
    }

    private static void ValidateLayoutPersistence(Assembly assembly)
    {
        var storeType = assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapLayoutStore")
            ?? throw new InvalidOperationException("Factory map layout store was not found.");
        var load = storeType.GetMethod("Load", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Factory map layout loader was not found.");
        var save = storeType.GetMethod("Save", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Factory map layout saver was not found.");
        var directory = Path.Combine(Path.GetTempPath(), "eoems-factory-map-validation-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "factory-map-layout.json");
        var knownIds = new[] { "GEN-G6", "UPS-C1" };
        try
        {
            var first = new[] { new KeyValuePair<string, PointF>("GEN-G6", new PointF(0.1F, 0.2F)) };
            save.Invoke(null, new object?[] { first, knownIds, path });
            var second = new[] { new KeyValuePair<string, PointF>("GEN-G6", new PointF(0.3F, 0.4F)) };
            save.Invoke(null, new object?[] { second, knownIds, path });

            var backup = (IDictionary)(load.Invoke(null, new object?[] { knownIds, path })
                ?? throw new InvalidOperationException("Saved factory map positions could not be loaded."));
            var restored = (PointF)(backup["GEN-G6"]
                ?? throw new InvalidOperationException("Saved generator position was not found."));
            Require(Math.Abs(restored.X - 0.3F) < 0.0001F && Math.Abs(restored.Y - 0.4F) < 0.0001F,
                "The latest saved normalized position must be restored.");
            Require(File.Exists(path + ".bak"), "Saving a layout update must retain a backup.");

            var invalid = new[] { new KeyValuePair<string, PointF>("GEN-G6", new PointF(1.1F, 0.4F)) };
            try
            {
                save.Invoke(null, new object?[] { invalid, knownIds, path });
                throw new InvalidOperationException("Out-of-range map coordinates were accepted.");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException)
            {
                var unchanged = (IDictionary)(load.Invoke(null, new object?[] { knownIds, path })
                    ?? throw new InvalidOperationException("The saved layout disappeared after rejected input."));
                var position = (PointF)(unchanged["GEN-G6"]
                    ?? throw new InvalidOperationException("The saved position disappeared after rejected input."));
                Require(Math.Abs(position.X - 0.3F) < 0.0001F, "Rejected coordinates must not replace the saved layout.");
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void ValidateProvisionalDimensions(Assembly assembly)
    {
        var dimensionsType = assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapDimensions")
            ?? throw new InvalidOperationException("Provisional factory dimensions model was not found.");
        var defaults = dimensionsType.GetMethod("Defaults", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Provisional factory dimensions were not found.");
        var dimensions = defaults.Invoke(null, null)
            ?? throw new InvalidOperationException("Default provisional dimensions are empty.");
        dimensionsType.GetMethod("Validate")?.Invoke(dimensions, null);
        var components = ((IEnumerable)(dimensionsType.GetProperty("Components")?.GetValue(dimensions)
            ?? throw new InvalidOperationException("Provisional component dimensions are empty."))).Cast<object>().ToArray();
        Require(components.Length == 8, "The provisional dimension editor must include walls and all equipment types.");
        var byName = components.ToDictionary(component => (string)(component.GetType().GetProperty("Name")?.GetValue(component)
            ?? throw new InvalidOperationException("A provisional dimension has no label.")), StringComparer.OrdinalIgnoreCase);
        CheckDimensions(byName, "External Wall", 0, 0.25, 4);
        CheckDimensions(byName, "Internal Wall", 0, 0.15, 3);
        CheckDimensions(byName, "Generator", 5, 2, 2.5);
        CheckDimensions(byName, "Compressor", 2.5, 1.5, 1.8);
        CheckDimensions(byName, "Chiller", 4, 2, 2.2);
        CheckDimensions(byName, "UPS Cabinet", 1.2, 1, 2);
        CheckDimensions(byName, "Transformer", 2.5, 2, 2.5);
        CheckDimensions(byName, "Battery Cabinet", 1, 0.8, 2);
        var storeType = assembly.GetType("EOEMS.PowerTransformersScadaUI.V5.FactoryMapDimensionStore")
            ?? throw new InvalidOperationException("Provisional dimension settings store was not found.");
        var save = storeType.GetMethod("Save", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Provisional dimension settings saver was not found.");
        var load = storeType.GetMethod("Load", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Provisional dimension settings loader was not found.");
        var directory = Path.Combine(Path.GetTempPath(), "eoems-factory-dimensions-validation-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "factory-map-dimensions.json");
        try
        {
            save.Invoke(null, new object?[] { dimensions, path });
            var restored = load.Invoke(null, new object?[] { path })
                ?? throw new InvalidOperationException("Saved provisional dimensions could not be reloaded.");
            dimensionsType.GetMethod("Validate")?.Invoke(restored, null);
            Require(File.Exists(path), "Provisional dimensions must persist in a configuration file.");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void CheckDimensions(Dictionary<string, object> components, string name, double length, double width, double height)
    {
        var component = components[name];
        var type = component.GetType();
        Require(Math.Abs(Convert.ToDouble(type.GetProperty("LengthMeters")?.GetValue(component)) - length) < 0.0001,
            $"{name} provisional length does not match the request.");
        Require(Math.Abs(Convert.ToDouble(type.GetProperty("WidthMeters")?.GetValue(component)) - width) < 0.0001,
            $"{name} provisional width does not match the request.");
        Require(Math.Abs(Convert.ToDouble(type.GetProperty("HeightMeters")?.GetValue(component)) - height) < 0.0001,
            $"{name} provisional height does not match the request.");
    }

    private static Asset ReadAsset(object asset)
    {
        var type = asset.GetType();
        var id = (string?)type.GetProperty("Id")?.GetValue(asset)
            ?? throw new InvalidOperationException("An asset is missing its ID.");
        var category = (string?)type.GetProperty("Category")?.GetValue(asset)
            ?? throw new InvalidOperationException($"Asset {id} is missing its category.");
        var position = (PointF)(type.GetProperty("Position")?.GetValue(asset)
            ?? throw new InvalidOperationException($"Asset {id} is missing its normalized position."));
        var isPlaced = (bool)(type.GetProperty("IsPlaced")?.GetValue(asset)
            ?? throw new InvalidOperationException($"Asset {id} is missing its placement state."));
        if (isPlaced && (position.X is < 0 or > 1 || position.Y is < 0 or > 1))
            throw new InvalidOperationException($"Asset {id} is outside normalized map coordinates.");
        return new Asset(id, category, position, isPlaced);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
