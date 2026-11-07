using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class FactoryFireEventValidation
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type Type(string name) => typeof(FactoryMapView).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5." + name)!;
    private static object New(string name) => Activator.CreateInstance(Type(name))!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Instance)!.Invoke(target, args);
    private static object? Static(string type, string name, params object?[] args) => Type(type).GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, args);
    private static T Get<T>(object target, string name) => (T)target.GetType().GetProperty(name, Instance)!.GetValue(target)!;
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name, Instance)!.SetValue(target, value);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Instance)!.GetValue(target)!;
    private static void FieldSet(object target, string name, object value) => target.GetType().GetField(name, Instance)!.SetValue(target, value);
    private static IList List(string type, params object[] values)
    {
        var result = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(Type(type)))!;
        foreach (var value in values) result.Add(value);
        return result;
    }
    private static int assertions;
    private static void Check(bool success, string message)
    {
        assertions++;
        if (!success) throw new InvalidOperationException("Fire map events: " + message);
    }
    private static ZetaFireEvent Event(string id = "1", string category = "fault", string description = "SD-17-P2 (Address 17)", string message = "Fault with 17, Input/Output Module (Low Voltage)") =>
        new() { EventId = id, EventType = category, SiteId = "site", TimestampUtc = DateTimeOffset.UtcNow.AddMinutes(-1), Description = description, Message = message };
    private static object Asset(string id, string device, string room = "PET2")
    {
        var asset = New("FactoryMapAsset");
        Set(asset, "Id", id); Set(asset, "Name", id); Set(asset, "Category", "Fire System"); Set(asset, "Model", "Zeta smoke detector");
        Set(asset, "Position", new PointF(.5f, .5f)); Set(asset, "IsPlaced", true); Set(asset, "MonitoringDeviceId", device); Set(asset, "RoomId", room);
        return asset;
    }
    private static object Room(string id = "PET2", string name = "PET-2")
    {
        var room = New("FactoryBuildingPart");
        Set(room, "Id", "F-" + id); Set(room, "Kind", "Floor"); Set(room, "Style", "Rectangle"); Set(room, "RoomId", id); Set(room, "RoomName", name);
        Set(room, "X", .5f); Set(room, "Y", .5f); Set(room, "Length", 180f); Set(room, "Depth", 100f);
        return room;
    }
    private static void Rejected(Action action, string message)
    {
        try { action(); }
        catch (TargetInvocationException ex) when (ex.InnerException is UnauthorizedAccessException or InvalidDataException or IOException) { Check(true, message); return; }
        throw new InvalidOperationException("Fire map events accepted: " + message);
    }

    public static void Run(string evidence)
    {
        Directory.CreateDirectory(evidence);
        Parsing();
        Locations();
        Resolution();
        var temporary = Path.Combine(Path.GetTempPath(), "eoems-fire-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try { Latching(temporary); }
        finally { Directory.Delete(temporary, true); }
        Rendering(evidence);
        SavedMap(evidence);
        Console.WriteLine($"Fire map parsing, matching, persistent reset/recurrence and screen-space visibility assertions passed: {assertions}.");
    }

    private static void Parsing()
    {
        var item = new FireMapEventEvidence(Event());
        Check(item.Address == "17" && item.AddressSource == "description/message", "explicit description address recovered");
        Check(item.Zone == "" && item.Signal == MapSignal.Warning, "fault becomes amber, no zone invented");
        item = new FireMapEventEvidence(Event(description: "AREA 1 SND MODULE MAIN PANEL ROOM UPS 800KVA (Address 24)", message: "Fault with 24, Input/Output Module (Low Voltage)"));
        Check(item.Address == "24" && item.Zone == "", "AREA and kVA ratings cannot become zone/address");
        item = new FireMapEventEvidence(Event(description: "Loop 2 Zone 03 SD PET2 (Address 017)", message: "fault"));
        Check(item.Loop == "2" && item.Zone == "03" && item.Address == "017", "explicit loop/zone extracted");
        var record = Event(); record.DeviceAddress = "L1/17"; record.Description = "Loop 1 (Address 17)";
        item = new FireMapEventEvidence(record);
        Check(!item.IdentityConflict && item.Loop == "1", "structured composite and leaf address agree");
        record.Description = "Loop 2 (Address 17)";
        Check(new FireMapEventEvidence(record).IdentityConflict, "conflicting loops block inference");
        Check(new FireMapEventEvidence(Event(description: "Loop 2 Address L1/17", message: "fault")).IdentityConflict, "composite description loop conflict blocked");
        Check(new FireMapEventEvidence(Event(description: "Loop 1 Loop 2 Address 17", message: "fault")).IdentityConflict, "multiple described loops blocked");
        record.Description = "(Address 18)";
        Check(new FireMapEventEvidence(record).IdentityConflict, "structured and description conflict detected");
        record = Event(description: "Address 17 / Address 18", message: "fault");
        Check(new FireMapEventEvidence(record).IdentityConflict, "multiple explicit addresses block device selection");
        record = Event(description: "Address 17", message: "Fault with 24, Input/Output Module");
        Check(new FireMapEventEvidence(record).IdentityConflict, "vendor message and description conflict detected");
        item = new FireMapEventEvidence(Event(description: "No location", message: "Module failure at 5 (TCP/IP)"));
        Check(item.Address == "" && item.Zone == "", "module ID not guessed as detector address");
        item = new FireMapEventEvidence(Event(description: "LEFT SIDE BEAM (Address 2)", message: "Fault with 2, Zone Monitor Module"));
        Check(item.Zone == "", "Zone Monitor Module is device type, not a zone");
        Check(new FireMapEventEvidence(Event(category: "fire", message: "Fire detected")).Signal == MapSignal.Fire, "fire category is red");
        foreach (var message in new[] { "Fire restored", "Fault cleared", "Reset button pressed", "Fire test" })
            Check(new FireMapEventEvidence(Event(category: "fire", message: message)).Signal == MapSignal.Unknown, "restore/reset/test cannot latch " + message);
        Check(new FireMapEventEvidence(Event(category: "service", message: "Reset button pressed")).Signal == MapSignal.Unknown, "service event not an alarm");
    }

    private static void Resolution()
    {
        var assets = List("FactoryMapAsset", Asset("S1", "F1"), Asset("S2", "F2"));
        var rooms = List("FactoryBuildingPart", Room(), Room("PET20", "PET-20"));
        var registry = new MonitoringDeviceRegistry();
        registry.Devices.Add(new MonitoringDevice { Id = "F1", Name = "SD-17-P2", DrawingTag = "SD-17-P2", Commissioned = false });
        registry.Devices.Add(new MonitoringDevice { Id = "F2", Name = "SD-18-P2", DrawingTag = "SD-18-P2", Commissioned = false });
        object Resolve(ZetaFireEvent record, bool unscoped = true, string panel = "1") =>
            Static("FireMapEventResolver", "Resolve", panel, new FireMapEventEvidence(record), assets, rooms, registry, unscoped)!;
        var result = Resolve(Event());
        Check(Get<string>(result, "AssetId") == "S1" && Get<string>(result, "MatchReason").Contains("inferred"), "drawing tag maps uniquely without commissioning");
        Check(!registry.Devices[0].Commissioned && registry.Devices[0].Address == "", "inference never writes a physical panel address");
        Check(Get<string>(Resolve(Event(description: "SD alarm PET2", message: "fire")), "RoomId") == "PET2", "place-only event maps room, not random detector");
        Check(Get<string>(Resolve(Event(description: "SD alarm PET2", message: "fire")), "AssetId") == "", "room match cannot mark all detectors");
        Check(Get<string>(Resolve(Event(), false), "AssetId") == "", "unscoped drawing blocked with multiple sources");
        registry.Devices[1].DrawingTag = "SD-17-P2";
        Check(Get<string>(Resolve(Event()), "AssetId") == "", "duplicate drawing tags remain ambiguous");
        registry.Devices[1].DrawingTag = "SD-18-P2";
        var device = registry.Devices[0];
        device.Commissioned = true; device.PanelId = "1"; device.SiteId = "site"; device.Loop = "2"; device.Address = "17"; device.EventAddress = "L2/17";
        var addressed = Event(description: "No location", message: "fault"); addressed.DeviceAddress = "17";
        Check(Get<string>(Resolve(addressed), "AssetId") == "", "leaf address alone cannot select a loop");
        addressed.Description = "Loop 2";
        Check(Get<string>(Resolve(addressed), "AssetId") == "S1", "full loop/address plus panel/site identifies device");
        Check(Get<string>(Resolve(addressed, panel: "2"), "AssetId") == "", "panel boundary enforced");
        addressed.SiteId = "other";
        Check(Get<string>(Resolve(addressed), "AssetId") == "", "site boundary enforced");
        var before = JsonSerializer.Serialize(registry);
        Resolve(Event(description: "No location", message: "Earth status now Positive"));
        Check(JsonSerializer.Serialize(registry) == before, "resolution leaves registry intact");
    }

    private static void Locations()
    {
        object Parse(string description, string message = "fault") => Static("FireEventLocation", "Parse", description, message)!;
        var location = Parse("AREA 1 SND MODULE MAIN PANEL ROOM UPS 800KVA (Address 24)");
        Check(Get<string>(location, "Area") == "Area 1" && Get<string>(location, "Location") == "Panel room",
            "area number and specific main panel room recovered separately from zone");
        Check(Get<string>(location, "DescribedPanel") == "Main panel room panel / UPS 800 kVA" &&
            Get<string>(location, "DeviceType") == "Sounder module", "described electrical panel/rating and module type retained");
        location = Parse("AREA 1 SND MODULE UPS ROOM PANEL 500KVA (Address 26)");
        Check(Get<string>(location, "Location") == "UPS room" && Get<string>(location, "DescribedPanel") == "UPS room panel / UPS 500 kVA",
            "UPS room panel is not mistaken for generic panel room");
        foreach (var text in new[] { "underground panel", "undeground panel", "panel underground", "basement panel", "Basement LVDB" })
            Check(Get<string>(Parse(text), "Location") == "Basement electrical room", "basement electrical alias " + text);
        Check(Get<string>(Parse("AREA 1 GEN SET AREA SIREN"), "Location") == "Generator bank", "generator area recovered");
        Check(Get<string>(Parse("SD alarm PET2"), "Location") == "PET-2", "PET spelling normalized");
        Check(Get<string>(Parse("SD alarm PET20"), "Location") == "", "PET2 cannot consume PET20");
        Check(Get<string>(Parse("AREA 1 only"), "Location") == "", "area 1 alone cannot identify a room");
        Check(Get<string>(Parse("Module failure at 5 (TCP/IP)"), "Location") == "", "controller module numbers cannot identify physical locations");
        Check(Get<bool>(Parse("PANEL ROOM and UPS ROOM"), "Conflict"), "multiple room descriptions remain ambiguous");
        Check(Get<bool>(Parse("AREA 1 AREA 2 PANEL ROOM"), "Conflict"), "conflicting area descriptions remain visible");
        Check(Get<string>(Parse("Canline alarm"), "Location") == "Canline", "missing line number must not become line 1");

        var rooms = List("FactoryBuildingPart", Room("PR", "Panel room"), Room("UR", "UPS room"), Room("B", "Basement LVDB"));
        Set(rooms[2]!, "Level", "Basement");
        var assets = List("FactoryMapAsset");
        var ups = Asset("UPS", "", "PR"); Set(ups, "Category", "UPS"); assets.Add(ups);
        var registry = new MonitoringDeviceRegistry();
        object Resolve(string text) => Static("FireMapEventResolver", "Resolve", "1",
            new FireMapEventEvidence(Event(description: text, message: "fault")), assets, rooms, registry, true)!;
        Check(Get<string>(Resolve("UPS ROOM PANEL 500KVA"), "RoomId") == "UR", "explicit UPS room outranks other rooms containing UPS equipment");
        Check(Get<string>(Resolve("MAIN PANEL ROOM UPS 800KVA"), "RoomId") == "PR", "main panel room does not match every UPS room");
        Check(Get<string>(Resolve("UNDEGROUND PANEL"), "RoomId") == "B" && Get<string>(Resolve("UNDEGROUND PANEL"), "Level") == "Basement",
            "underground panel routes to the unique basement electrical room");
        Check(Get<string>(Resolve("PANEL ROOM and UPS ROOM"), "RoomId") == "", "competing named rooms cannot select a guessed room");

        var anchor = Event("anchor", description: "AREA 1 UPS ROOM PANEL 500KVA (Address 26)", message: "Fault with 26, Input/Output Module");
        var missing = Event("missing", description: "No location", message: "Fault with 26, Input/Output Module");
        object Related(params ZetaFireEvent[] records) => Static("FireEventLocation", "ResolveRelated", (object)records.Select(record => new FireMapEventEvidence(record)).ToArray())!;
        object Lookup(object index, ZetaFireEvent record) => index.GetType().GetProperty("Item")!.GetValue(index,
            new object[] { new FireMapEventEvidence(record).EventKey })!;
        var inferred = Lookup(Related(anchor, missing), missing);
        Check(Get<string>(inferred, "Location") == "UPS room" && Get<string>(inferred, "Source").Contains("related event"),
            "unlocated event inherits a unique location from same site/loop/address with explicit provenance");
        var target = Static("FireMapEventResolver", "ResolveLocated", "1", new FireMapEventEvidence(missing), assets, rooms, registry, true, inferred)!;
        Check(Get<string>(target, "RoomId") == "UR" && Get<string>(target, "AssetId") == "", "related location maps room, not arbitrary detector");
        ZetaFireEvent CopyAnchor() => (ZetaFireEvent)Call(anchor, "Copy")!;
        var other = CopyAnchor(); other.SiteId = "other";
        Check(Get<string>(Lookup(Related(other, missing), missing), "Location") == "", "correlation never crosses site");
        other = CopyAnchor(); other.Description = "Loop 2 UPS ROOM PANEL (Address 26)";
        Check(Get<string>(Lookup(Related(other, missing), missing), "Location") == "", "unknown loop does not inherit from a guessed loop");
        other = CopyAnchor(); other.Description = "AREA 1 UNDERGROUND PANEL (Address 26)"; other.EventId = "conflicting";
        Check(Get<bool>(Lookup(Related(anchor, other, missing), missing), "Conflict"), "same address with competing room descriptions blocks correlation");
        other = CopyAnchor(); other.Description = "AREA 2 UPS ROOM PANEL 500KVA (Address 26)"; other.EventId = "other-area";
        Check(Get<bool>(Lookup(Related(anchor, other, missing), missing), "Conflict"), "same address reused across areas remains ambiguous");
        other = CopyAnchor(); other.Description = "UPS ROOM PANEL (Address 26)"; other.DeviceAddress = "99";
        Check(Get<string>(Lookup(Related(other, missing), missing), "Location") == "", "identity-conflicting anchors cannot teach a location");
        other = CopyAnchor(); other.TimestampUtc = DateTimeOffset.UtcNow.AddDays(1);
        Check(Get<string>(Lookup(Related(other, missing), missing), "Location") == "", "future records cannot teach locations");

        using var view = new FireEventsView(null!, UserSession.OfflinePreview());
        var records = new[] { anchor, missing };
        FieldSet(view, "_events", records);
        FieldSet(view, "_locations", Related(records));
        Call(view, "FilterEvents");
        var grid = Field<DataGridView>(view, "_grid");
        Check(grid.Columns[4].HeaderText == "Area / Location" && grid.Columns[5].HeaderText == "Described Panel", "historian exposes the requested location and described-panel columns");
        Check(grid.Rows.Count == 2 && grid.Rows[1].Cells[4].Value?.ToString() == "Area 1 - UPS room", "actual event grid displays correlated location");
        Check(grid.Rows[1].Cells[4].ToolTipText.Contains("related event"), "grid distinguishes correlation from raw event fields");
        Field<TextBox>(view, "_search").Text = "UPS room panel";
        Check(grid.Rows.Count == 2, "search includes inferred panel labels even when raw description has no location");
        var csv = (string)Call(view, "ExportText", (object)records)!;
        Check(csv.Contains("Area / Location,Described Panel") && csv.Contains("\"Area 1 - UPS room\"") && csv.Contains("related event with same panel/site/loop/address"),
            "actual CSV contains location/panel and correlation provenance");
        Check(missing.Description == "No location" && missing.DeviceAddress == "" && registry.Devices.Count == 0,
            "inference leaves original events and monitoring registrations unchanged");
    }

    private static void Latching(string temporary)
    {
        var path = Path.Combine(temporary, "fire.json");
        object Store() => Activator.CreateInstance(Type("FireMapAlertStore"), path)!;
        var store = Store();
        var settings = new FireConnectionSettings { CloudSiteIdentifier = "site" };
        var admin = new UserSession { Username = "isolated-qa", RoleName = "Administrator" };
        var first = Event();
        var fire = Event("2", "fire", "SD-17-P2", "Fire detected");
        void Observe(params ZetaFireEvent[] events) => Call(store, "Observe", settings, events);
        IReadOnlyList<FireMapEventEvidence> Pending() => (IReadOnlyList<FireMapEventEvidence>)Call(store, "Read", settings)!;
        Observe(first, fire, Event("service", "service", "No location", "Reset button pressed"));
        Check(Pending().Count == 2, "fire and fault latch, service ignored");
        Check(ReferenceEquals(Pending(), Pending()), "UI reads immutable cached evidence without disk/parse work");
        var storageGate = Field<object>(store, "_gate");
        System.Threading.Monitor.Enter(storageGate);
        try
        {
            var read = Task.Run(() => Pending());
            Check(read.Wait(500) && read.Result.Count == 2, "map reads never wait behind the background persistence lock");
        }
        finally { System.Threading.Monitor.Exit(storageGate); }
        var shown = Pending();
        Observe(Event("3"));
        Call(store, "Reset", settings, admin, shown);
        Check(Pending().Count == 1 && Pending()[0].EventId == "3", "reset clears only shown events, not a concurrently received alarm");
        Call(store, "Reset", settings, admin, Pending());
        Observe(first, fire, Event("3"));
        Check(Pending().Count == 0, "repeated overlapping history does not relatch reset IDs");
        store = Store();
        Observe(first);
        Check(Pending().Count == 0, "reset survives restart");
        Observe(Event("4", "fire", "SD-17-P2", "Fire detected"));
        Check(Pending().Count == 1 && Pending()[0].Signal == MapSignal.Fire, "new occurrence of same device relatches");
        store = Store();
        Check(Pending().Count == 1, "active latch survives restart");
        settings.CloudSiteIdentifier = "other";
        Check(Pending().Count == 0, "site switch hides other site's latch");
        settings.CloudSiteIdentifier = "site";
        var future = Event("future"); future.TimestampUtc = DateTimeOffset.UtcNow.AddHours(1);
        var undated = Event("undated"); undated.TimestampUtc = null;
        Observe(future, undated);
        Check(Pending().Count == 1, "future and undated records cannot raise map alarms");
        Rejected(() => Call(store, "Reset", settings, UserSession.OfflinePreview(), Pending()), "offline reset");
        var protectedText = File.ReadAllText(path + ".map-alerts");
        Check(!protectedText.Contains("Fire detected") && !protectedText.Contains("SD-17"), "persisted descriptions remain DPAPI protected");
        Check(File.Exists(path + ".map-alerts.audit.jsonl"), "local reset audited");
        File.WriteAllText(path + ".map-alerts", "invalid protected document");
        try { Store(); throw new InvalidOperationException("corrupt store accepted"); }
        catch (TargetInvocationException ex) when (ex.InnerException is FormatException or System.Security.Cryptography.CryptographicException) { Check(true, "corrupt state fails explicitly"); }
    }

    private static void Rendering(string evidence)
    {
        using var form = new Form { Size = new Size(1100, 750) };
        using var view = new FactoryMapView(UserSession.OfflinePreview(), Array.Empty<MeterSnapshot>(), () => { });
        form.Controls.Add(view); form.Show(); Application.DoEvents();
        var canvas = Field<Control>(view, "_canvas");
        var assets = Field<IList>(view, "_assets"); assets.Clear();
        var smoke = Asset("S1", "F1"); assets.Add(smoke);
        var parts = List("FactoryBuildingPart", Room());
        Call(canvas, "SetBuildingParts", parts);
        var registry = new MonitoringDeviceRegistry();
        registry.Devices.Add(new MonitoringDevice { Id = "F1", Name = "SD-17-P2", DrawingTag = "SD-17-P2", Commissioned = false });
        var match = Static("FireMapEventResolver", "Resolve", "1", new FireMapEventEvidence(Event(category: "fire", message: "Fire detected")), assets, parts, registry, true)!;
        Call(canvas, "SetMonitoring", registry, new Dictionary<string, IReadOnlyList<FireMapEventEvidence>>(), new Dictionary<string, MonitoringSample>());
        Call(canvas, "SetEventAlerts", List("FireMapMatch", match));
        Check((bool)Call(canvas, "ShouldShowAsset", smoke)!, "latched event exposes concealed detector");
        Check(!(bool)Call(canvas, "RoomHasFire", "PET2")!, "historical latch never becomes verified room fire");
        Call(canvas, "SetPageActive", true);
        Check(!Field<System.Windows.Forms.Timer>(canvas, "_blinkTimer").Enabled, "steady fire alone needs no blink timer");
        using var a = new Bitmap(160, 100);
        using var b = new Bitmap(160, 100);
        void Beacon(Bitmap image, MapSignal signal, bool on)
        {
            FieldSet(canvas, "_blinkOn", on);
            using var g = Graphics.FromImage(image); g.Clear(Color.Black);
            Call(canvas, "DrawEventBeacon", g, new PointF(80, 50), signal, "");
        }
        Beacon(a, MapSignal.Fire, true); Beacon(b, MapSignal.Fire, false);
        Check(Same(a, b), "fire beacon is pixel-identical in both blink phases");
        Beacon(a, MapSignal.Warning, true); Beacon(b, MapSignal.Warning, false);
        Check(!Same(a, b), "fault beacon actually blinks");
        var colored = 0;
        for (var y = 40; y < 60; y++) for (var x = 70; x < 90; x++)
            if (a.GetPixel(x, y).R > 100) colored++;
        Check(colored > 200, "fault marker has at least 200 visible pixels, independent of physical device scale");
        using var screenshot = new Bitmap(canvas.Width, canvas.Height);
        using var graphics = Graphics.FromImage(screenshot);
        void Paint() => Call(canvas, "OnPaint", new PaintEventArgs(graphics, canvas.ClientRectangle));
        foreach (var mode in new[] { "SetTopView", "SetIsometricView" })
        foreach (var zoom in new[] { .35f, 1f, 4f })
        {
            Call(canvas, mode); FieldSet(canvas, "_zoom", zoom); FieldSet(canvas, "_pan", PointF.Empty);
            FieldSet(canvas, "_blinkOn", true); canvas.Invalidate(); Paint();
            var hits = Field<IDictionary>(canvas, "_hitAreas");
            Check(hits.Contains(smoke), $"active device renders in {mode} at {zoom}x");
            var bounds = ((System.Drawing.Drawing2D.GraphicsPath)hits[smoke]!).GetBounds();
            var center = new PointF(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            var visible = 0;
            for (var y = Math.Max(0, (int)center.Y - 12); y < Math.Min(screenshot.Height, center.Y + 12); y++)
            for (var x = Math.Max(0, (int)center.X - 12); x < Math.Min(screenshot.Width, center.X + 12); x++)
            {
                var pixel = screenshot.GetPixel(x, y);
                if (pixel.R > 130 && pixel.R > pixel.G * 1.4 && pixel.R > pixel.B * 1.2) visible++;
            }
            Check(visible >= 160, $"fire beacon retains >=160 red pixels in actual {mode} at {zoom}x; got {visible}");
            using var onFrame = (Bitmap)screenshot.Clone();
            FieldSet(canvas, "_blinkOn", false); Call(canvas, "InvalidateAlarmOverlay"); Paint();
            Check(Same(onFrame, screenshot, Rectangle.FromLTRB((int)center.X - 24, (int)center.Y - 24, (int)center.X + 24, (int)center.Y + 24)),
                $"actual fire device is steady in {mode} at {zoom}x");
            if (zoom == .35f) screenshot.Save(Path.Combine(evidence, mode + "-zoomed-out-fire.png"));
        }
        Call(canvas, "SetTopView"); FieldSet(canvas, "_zoom", 1f);
        var area = Static("FireMapEventResolver", "Resolve", "1",
            new FireMapEventEvidence(Event(description: "SD alarm PET2", message: "Fault")), assets, parts, registry, true)!;
        Call(canvas, "SetEventAlerts", List("FireMapMatch", area));
        FieldSet(canvas, "_blinkOn", true); canvas.Invalidate(); Paint();
        var roomHit = ((System.Drawing.Drawing2D.GraphicsPath)Field<IDictionary>(canvas, "_buildingHits")[parts[0]!]!).GetBounds();
        var markerPixel = screenshot.GetPixel((int)(roomHit.X + roomHit.Width / 2), (int)(roomHit.Y + roomHit.Height / 2) + 4);
        Check(markerPixel.R > markerPixel.B * 1.5, "room-only beacon is projected onto actual room screen coordinates");
        screenshot.Save(Path.Combine(evidence, "room-only-fault-top-view.png"));
        var paints = Field<int>(canvas, "_scenePaintCount");
        using var onArea = (Bitmap)screenshot.Clone();
        FieldSet(canvas, "_blinkOn", false); Call(canvas, "InvalidateAlarmOverlay"); Paint();
        Check(!Same(onArea, screenshot) && Field<int>(canvas, "_scenePaintCount") == paints,
            "actual room fault blinks without rebuilding native scene");
        Call(canvas, "SetEventAlerts", List("FireMapMatch"));
        Check(!(bool)Call(canvas, "ShouldShowAsset", smoke)!, "local reset hides quiet uncommissioned detector again");
        form.Close();
    }

    private static bool Same(Bitmap a, Bitmap b, Rectangle? region = null)
    {
        var bounds = Rectangle.Intersect(region ?? new Rectangle(0, 0, a.Width, a.Height), new Rectangle(0, 0, a.Width, a.Height));
        for (var y = bounds.Top; y < bounds.Bottom; y++) for (var x = bounds.Left; x < bounds.Right; x++)
            if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
        return true;
    }

    private static void SavedMap(string evidence)
    {
        var path = Environment.GetEnvironmentVariable("EOEMS_MAP_PERF_DATA");
        if (string.IsNullOrEmpty(path)) return;
        var mapPath = Path.Combine(path, "factory-map-layout.json");
        var buildingPath = Path.Combine(path, "factory-map-building.json");
        var registryPath = Path.Combine(path, "monitoring-devices.json");
        if (!File.Exists(registryPath)) registryPath = Path.Combine(path, "..", "EOEMSData", "Configuration", "monitoring-devices.json");
        var assets = (IList)Static("FactoryMapLayoutStore", "LoadAssets", List("FactoryMapAsset"), mapPath)!;
        var parts = (IList)Static("FactoryMapBuildingStore", "Load", buildingPath)!;
        var registry = JsonSerializer.Deserialize<MonitoringDeviceRegistry>(File.ReadAllText(registryPath))!;
        var matches = List("FireMapMatch");
        foreach (var (address, description, room) in new[] {
            ("24", "AREA 1 SND MODULE MAIN PANEL ROOM UPS 800KVA (Address 24)", "DRAW-ROOM-PR1"),
            ("26", "AREA 1 SND MODULE UPS ROOM PANEL 500KVA (Address 26)", "DRAW-ROOM-PR1"),
            ("55", "AREA 1 GEN SET AREA SIREN (Address 55)", "DRAW-ROOM-GEN") })
        {
            var record = Event(description: description, message: "Fault with " + address + ", Input/Output Module");
            var result = Static("FireMapEventResolver", "Resolve", "1", new FireMapEventEvidence(record), assets, parts, registry, true)!;
            Check(Get<string>(result, "RoomId") == room && Get<string>(result, "AssetId") == "", "saved map uses conservative room-only marker for " + address);
            matches.Add(result);
        }
        var draft = registry.Devices.First(device => device.DrawingTag == "SD-115-P3");
        var fire = Event("saved-fire", "fire", draft.DrawingTag, "Fire alarm");
        var matchedFire = Static("FireMapEventResolver", "Resolve", "1", new FireMapEventEvidence(fire), assets, parts, registry, true)!;
        Check(Get<string>(matchedFire, "AssetId") != "", "real saved drawing tag identifies its exact placed symbol");
        matches.Add(matchedFire);
        using var canvas = (Control)Activator.CreateInstance(Type("FactoryMapCanvas"), assets)!;
        canvas.Size = new Size(1365, 766);
        Call(canvas, "SetBuildingParts", parts);
        Call(canvas, "SetMonitoring", registry, new Dictionary<string, IReadOnlyList<FireMapEventEvidence>>(), new Dictionary<string, MonitoringSample>());
        Call(canvas, "SetEventAlerts", matches);
        using var image = new Bitmap(canvas.Width, canvas.Height);
        using var graphics = Graphics.FromImage(image);
        foreach (var mode in new[] { "SetTopView", "SetIsometricView" })
        {
            Call(canvas, mode); FieldSet(canvas, "_zoom", .35f);
            canvas.Invalidate(); Call(canvas, "OnPaint", new PaintEventArgs(graphics, canvas.ClientRectangle));
            Check(Field<IDictionary>(canvas, "_hitAreas").Keys.Cast<object>().Any(asset => Get<string>(asset, "Id") == Get<string>(matchedFire, "AssetId")),
                "saved-map alarm device remains rendered at 0.35x in " + mode);
            var symbol = assets.Cast<object>().First(asset => Get<string>(asset, "Id") == Get<string>(matchedFire, "AssetId"));
            var hit = ((System.Drawing.Drawing2D.GraphicsPath)Field<IDictionary>(canvas, "_hitAreas")[symbol]!).GetBounds();
            var cx = (int)(hit.X + hit.Width / 2); var cy = (int)(hit.Y + hit.Height / 2);
            var red = 0;
            for (var y = cy - 12; y < cy + 12; y++) for (var x = cx - 12; x < cx + 12; x++)
            {
                var pixel = image.GetPixel(x, y);
                if (pixel.R > 130 && pixel.R > pixel.G * 1.4 && pixel.R > pixel.B * 1.2) red++;
            }
            Check(red >= 160, $"saved-map fire beacon remains visible above area labels in {mode}: {red} red pixels");
            image.Save(Path.Combine(evidence, "saved-map-" + mode + "-alerts.png"));
        }
    }
}
