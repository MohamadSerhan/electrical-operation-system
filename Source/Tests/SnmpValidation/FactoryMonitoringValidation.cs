using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class FactoryMonitoringValidation
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static Type Type(string name) => typeof(FactoryMapView).Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5." + name)!;
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Instance)!.Invoke(target, args);
    private static object? Static(string type, string name, params object?[] args) => Type(type).GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(null, args);
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Instance)!.GetValue(target)!;
    private static void FieldSet(object target, string name, object value) => target.GetType().GetField(name, Instance)!.SetValue(target, value);
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    private static T Get<T>(object target, string name) => (T)target.GetType().GetProperty(name, Instance)!.GetValue(target)!;
    private static object New(string name) => Activator.CreateInstance(Type(name))!;
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
        if (!success) throw new InvalidOperationException("Factory monitoring: " + message);
    }
    private static void Rejected(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { Check(true, message); return; }
        catch (UnauthorizedAccessException) { Check(true, message); return; }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException or UnauthorizedAccessException) { Check(true, message); return; }
        throw new InvalidOperationException("Factory monitoring accepted invalid operation: " + message);
    }
    private static UserSession Admin => new() { RoleName = "Administrator", Username = "isolated-qa" };
    private static object Asset(string id, string category, string model)
    {
        var asset = New("FactoryMapAsset");
        Set(asset, "Id", id); Set(asset, "Name", id); Set(asset, "Category", category); Set(asset, "Model", model);
        Set(asset, "Position", new PointF(.5f, .5f)); Set(asset, "IsPlaced", true);
        return asset;
    }
    private static object Part(string id, string kind)
    {
        var part = New("FactoryBuildingPart");
        Set(part, "Id", id); Set(part, "Name", id); Set(part, "Kind", kind);
        Set(part, "Style", kind == "Floor" ? "Rectangle" : kind == "Door" ? "Single leaf" : "Solid");
        Set(part, "Material", kind == "Door" ? "Steel" : "Concrete");
        Set(part, "RoomId", "ROOM"); Set(part, "RoomName", "Test room");
        Set(part, "TemperatureSourceId", "PLC:T1"); Set(part, "TemperatureSourceConfirmed", true);
        Set(part, "FirePanelId", "1"); Set(part, "FireSiteId", "site"); Set(part, "FireZone", "3");
        return part;
    }
    private static UpsSnapshot Ups() => new()
    {
        DeviceId = "UPS-QA", TimestampUtc = DateTime.UtcNow, Quality = UpsQuality.Good, AlarmDetailsComplete = true,
        OutputVoltageReference = "L-N",
        Values = new Dictionary<string, double> { ["OutputSource"] = 5, ["BatteryStatus"] = 2,
            ["BatteryVoltage"] = 480, ["BatteryCurrent"] = 4, ["BatteryTemperature"] = 25,
            ["ChargePercent"] = 90, ["RuntimeMinutes"] = 20, ["AlarmsPresent"] = 0 },
        Phases = new Dictionary<string, double?[]> { ["OutputVoltage"] = new double?[] { 230, 230, 230 },
            ["OutputLoad"] = new double?[] { 30, 30, 30 } }
    };
    private static MeterSnapshot Meter(float temperature = 20) => new()
    {
        TransformerName = "T1", CapturedAtUtc = DateTime.UtcNow, Quality = MeterQuality.Good,
        PowerState = TransformerPowerState.On,
        DecodedValues = new float[] { 230, 230, 230, 230, 400, 400, 400, 400, 50, 20, 200, 20, 20, 20, 0, temperature }
    };
    private static Dictionary<string, IReadOnlyList<FireMapEventEvidence>> History(string panel = "1", string site = "site", string address = "L1/17") =>
        new() { [panel] = new[] { new FireMapEventEvidence(new ZetaFireEvent
        { SiteId = site, DeviceAddress = address, Zone = "3", EventType = "FIRE", Message = "historical fixture",
            TimestampUtc = DateTimeOffset.UtcNow.AddDays(-2) }) } };
    private static MapSignal Signal(object indication) => Get<MapSignal>(indication, "Signal");

    public static void Run(string evidence)
    {
        Directory.CreateDirectory(evidence);
        var alarmField = typeof(AlarmSettingsStore).GetField("_current", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previousAlarms = alarmField.GetValue(null);
        alarmField.SetValue(null, new AlarmSettings());
        var temporary = Path.Combine(Path.GetTempPath(), "eoems-monitoring-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            Registry(temporary);
            Persistence(temporary);
            AlarmAccuracy();
            Canvas(evidence);
            Editors();
            RegistryUi(temporary, evidence);
            Console.WriteLine($"Factory monitoring assertions passed: {assertions}.");
        }
        finally
        {
            alarmField.SetValue(null, previousAlarms);
            Directory.Delete(temporary, true);
        }
    }

    private static void Registry(string temporary)
    {
        var path = Path.Combine(temporary, "devices.json");
        var document = new MonitoringDeviceRegistry();
        document.Devices.Add(new MonitoringDevice { Id = "F1", Name = "Smoke", PanelId = "1", SiteId = "site", Loop = "1",
            Address = "17", EventAddress = "L1/17", Zone = "3", Location = "Test room", Model = "Zeta smoke detector" });
        document.Devices.Add(new MonitoringDevice { Id = "C1", Name = "Camera 1", Kind = "Camera", IpAddress = "192.0.2.10", ExternalId = "channel-1" });
        document.Devices.Add(new MonitoringDevice { Id = "D1", Name = "Door 1", Kind = "Access", ExternalId = "door-1" });
        MonitoringDeviceStore.Save(document, Admin, path);
        Check(JsonSerializer.Serialize(MonitoringDeviceStore.Load(path)) == JsonSerializer.Serialize(document), "full registry persists stable identities and addresses");
        Check(File.Exists(path + ".audit.jsonl"), "registry updates are audited");
        var saved = File.ReadAllText(path);
        Rejected(() => MonitoringDeviceStore.Save(document, UserSession.OfflinePreview(), path), "offline cannot save");
        var duplicate = document.Devices[0].Clone(); duplicate.Id = "F2";
        document.Devices.Add(duplicate);
        Rejected(() => MonitoringDeviceStore.Save(document, Admin, path), "duplicate panel/site/loop/address rejected");
        duplicate.Loop = "2";
        Rejected(() => MonitoringDeviceStore.Save(document, Admin, path), "ambiguous identical event address rejected across loops");
        duplicate.EventAddress = "L2/17";
        document.Validate();
        Check(true, "same address on a distinct loop accepted with complete event identity");
        document.Devices.Remove(duplicate);
        duplicate = document.Devices[0].Clone(); duplicate.Id = "F3"; duplicate.Loop = "3"; duplicate.Zone = "";
        document.Devices.Add(duplicate);
        Rejected(document.Validate, "wildcard zone cannot overlap another device's explicit event zone");
        document.Devices.Remove(duplicate);
        document.Devices[1].IpAddress = "http://user:password@192.0.2.10/";
        Rejected(() => MonitoringDeviceStore.Save(document, Admin, path), "camera metadata cannot accept credential URLs");
        Check(File.ReadAllText(path) == saved, "rejected settings never overwrite saved registry");
        File.WriteAllText(path, "{}");
        Check(MonitoringDeviceStore.Load(path).Devices.Count == 0, "empty valid legacy registry starts empty");
        File.WriteAllText(path, "null");
        Rejected(() => MonitoringDeviceStore.Load(path), "null registry is an explicit load failure");
        var limits = new RoomTemperatureLimits();
        Check(limits.Evaluate(10) == MapSignal.Fault && limits.Evaluate(15) == MapSignal.Warning &&
              limits.Evaluate(20) == MapSignal.Normal && limits.Evaluate(30) == MapSignal.Warning &&
              limits.Evaluate(35) == MapSignal.Fault && limits.Evaluate(null) == MapSignal.Unknown &&
              limits.Evaluate(double.NaN) == MapSignal.Unknown, "exact room limits and unknown values");
        limits.LowWarningC = 40;
        Rejected(limits.Validate, "unordered room limits rejected");
        var legacy = JsonSerializer.Deserialize<AlarmSettings>("{\"Transformers\":{}}")!;
        legacy.Validate();
        Check(legacy.Rooms.HighAlarmC == 35 && legacy.Clone().Rooms != legacy.Rooms, "old alarm settings gain independent room defaults and deep clone");
    }

    private static void Persistence(string temporary)
    {
        var battery = Asset("BAT", "Battery Rooms", "Battery rack - 4 levels");
        Set(battery, "ParentUpsId", "UPS-QA"); Set(battery, "RoomId", "ROOM");
        var camera = Asset("CAM", "CCTV", "Camera");
        Set(camera, "CameraIpAddress", "192.0.2.10"); Set(camera, "MonitoringDeviceId", "C1"); Set(camera, "BaseHeightMeters", 3f);
        var smoke = Asset("SMOKE", "Fire System", "Zeta smoke detector");
        Set(smoke, "RoomId", "ROOM"); Set(smoke, "MonitoringDeviceId", "F1");
        var list = List("FactoryMapAsset", battery, camera, smoke);
        var path = Path.Combine(temporary, "layout.json");
        Static("FactoryMapLayoutStore", "SaveAssets", list, path);
        var loaded = (IList)Static("FactoryMapLayoutStore", "LoadAssets", list, path)!;
        Check(JsonSerializer.Serialize(loaded) == JsonSerializer.Serialize(list), "camera, battery, room and mounting links round-trip");
        Set(camera, "CameraIpAddress", "invalid");
        Rejected(() => Static("FactoryMapLayoutStore", "SaveAssets", list, path), "invalid camera address cannot corrupt saved layout");
        var wall = Part("W", "Wall"); var floor = Part("F", "Floor");
        var parts = List("FactoryBuildingPart", wall, floor);
        var buildingPath = Path.Combine(temporary, "building.json");
        Static("FactoryMapBuildingStore", "Save", parts, buildingPath);
        var restored = (IList)Static("FactoryMapBuildingStore", "Load", buildingPath)!;
        Check(JsonSerializer.Serialize(restored) == JsonSerializer.Serialize(parts), "room temperature/fire links round-trip");
        Set(wall, "FireZone", "other");
        Rejected(() => Static("FactoryMapBuildingStore", "Validate", parts), "room members cannot disagree on monitoring links");
    }

    private static void Canvas(string evidence)
    {
        using var host = new Form { Size = new Size(1440, 900), Text = "Isolated map monitoring validation" };
        using var view = new FactoryMapView(Admin, Array.Empty<MeterSnapshot>(), null!);
        host.Controls.Add(view); host.Show(); view.SetPageActive(true); Application.DoEvents();
        var canvas = Field<Control>(view, "_canvas");
        var assets = Field<IList>(view, "_assets"); assets.Clear();
        var smoke = Asset("SMOKE", "Fire System", "Zeta smoke detector");
        Set(smoke, "MonitoringDeviceId", "F1"); Set(smoke, "RoomId", "ROOM"); assets.Add(smoke);
        var battery = Asset("BAT", "Battery Rooms", "Battery rack - 4 levels");
        Set(battery, "ParentUpsId", "UPS-QA"); Set(battery, "Position", new PointF(.4f, .5f)); assets.Add(battery);
        var transformer = Asset("TR", "Transformers", "Transformer");
        Set(transformer, "EquipmentId", "TRANSFORMER-T1"); Set(transformer, "Position", new PointF(.6f, .5f)); assets.Add(transformer);
        var wall = Part("W", "Wall"); Set(wall, "Y", .53f);
        var wall2 = Part("W2", "Wall"); Set(wall2, "X", .53f); Set(wall2, "Rotation", 90f);
        var floor = Part("F", "Floor"); Set(floor, "Depth", 80f);
        var door = Part("D", "Door"); Set(door, "MonitoringDeviceId", "D1"); Set(door, "Y", .6f); Set(door, "Length", 20f);
        var parts = Field<IList>(view, "_building"); parts.Clear();
        foreach (var part in new[] { wall, wall2, floor, door }) parts.Add(part);
        Call(canvas, "SetBuildingParts", parts);
        var registry = new MonitoringDeviceRegistry();
        registry.Devices.Add(new MonitoringDevice { Id = "F1", Name = "Smoke", Kind = "Fire", PanelId = "1", SiteId = "site", Loop = "1", Address = "17", EventAddress = "L1/17", Zone = "3" });
        registry.Devices.Add(new MonitoringDevice { Id = "D1", Name = "Gate", Kind = "Access" });
        FieldSet(view, "_monitoringRegistry", registry); FieldSet(view, "_monitoringRevision", MonitoringDeviceStore.Revision);
        var ups = Ups();
        var readings = new[] { Meter() };
        var upsReadings = new Dictionary<string, UpsSnapshot> { [ups.DeviceId] = ups };
        view.SetReadings(readings, upsReadings);
        var noHistory = new Dictionary<string, IReadOnlyList<FireMapEventEvidence>>();
        var live = new Dictionary<string, MonitoringSample>();
        void Update(Dictionary<string, IReadOnlyList<FireMapEventEvidence>>? history = null) => Call(canvas, "SetMonitoring", registry, history ?? noHistory, live);
        Update();
        Check(Signal(Call(canvas, "Indication", battery)!) == MapSignal.Warning, "battery discharging is orange");
        Check(!(bool)Call(canvas, "ShouldShowAsset", smoke)!, "quiet/unverified fire symbols are hidden in normal mode");
        Call(canvas, "SetPageActive", true);
        Check(Field<System.Windows.Forms.Timer>(canvas, "_blinkTimer").Enabled, "active visible alarm starts one blink timer");
        Check((float)Call(canvas, "MountHeight", smoke)! == 3f, "detector follows room wall height");
        Set(wall, "HeightMeters", 5f);
        Check((float)Call(canvas, "MountHeight", smoke)! == 5f, "room wall resize moves ceiling detector height");
        Set(canvas, "EditMode", true);
        Check((bool)Call(canvas, "ShouldShowAsset", smoke)!, "edit mode exposes quiet detectors");
        Set(canvas, "EditMode", false);
        Update(History());
        Check(Signal(Call(canvas, "Indication", smoke)!) == MapSignal.Unknown && !(bool)Call(canvas, "ShouldShowAsset", smoke)! &&
              !(bool)Call(canvas, "RoomHasFire", "ROOM")!,
            "old FIRE event remains history/unknown: no device blink, normal-view symbol or room fire");
        Update(History(site: "wrong-site"));
        Check(Signal(Call(canvas, "Indication", smoke)!) == MapSignal.Unknown, "same address on another site cannot match");
        Update(History(panel: "2"));
        Check(Signal(Call(canvas, "Indication", smoke)!) == MapSignal.Unknown, "same address on another panel cannot match");
        live["F1"] = new MonitoringSample { DeviceId = "F1", Verified = true, TimestampUtc = DateTime.UtcNow, Signal = MapSignal.Fault };
        Update();
        Check(Signal(Call(canvas, "Indication", smoke)!) == MapSignal.Warning && !(bool)Call(canvas, "RoomHasFire", "ROOM")!,
            "fire device fault is amber and does not escalate to room fire");
        live["F1"].Signal = MapSignal.Fire;
        Update();
        Check((bool)Call(canvas, "RoomHasFire", "ROOM")!, "verified current device fire maps to correct room");
        var buildingSignals = Field<IDictionary>(canvas, "_buildingSignals");
        Check((MapSignal)buildingSignals["W"]! == MapSignal.Fire && (MapSignal)buildingSignals["F"]! == MapSignal.Fire,
            "verified fire covers walls AND floor");
        using var image = new Bitmap(canvas.Width, canvas.Height);
        using var graphics = Graphics.FromImage(image);
        void Paint() => Call(canvas, "OnPaint", new PaintEventArgs(graphics, canvas.ClientRectangle));
        canvas.Invalidate(); Paint();
        image.Save(Path.Combine(evidence, "verified-fire-and-discharging.png"));
        var scenePaints = Field<int>(canvas, "_scenePaintCount");
        var cache = Field<object>(canvas, "_renderCache");
        var rasterBuilds = Get<int>(cache, "RasterBuildCount");
        using var previousFrame = (Bitmap)image.Clone();
        FieldSet(canvas, "_blinkOn", !Field<bool>(canvas, "_blinkOn"));
        Call(canvas, "InvalidateAlarmOverlay"); Paint();
        Check(Field<int>(canvas, "_scenePaintCount") == scenePaints && Get<int>(cache, "RasterBuildCount") == rasterBuilds,
            "blink reuses native scene, with no geometry or sprite rebuild");
        var changed = false;
        for (var y = 0; y < image.Height && !changed; y += 3)
            for (var x = 0; x < image.Width && !changed; x += 3)
                changed = image.GetPixel(x, y) != previousFrame.GetPixel(x, y);
        Check(changed, "alarm pulse actually changes rendered pixels");
        canvas.Invalidate(); Call(canvas, "InvalidateAlarmOverlay"); Paint();
        Check(Field<int>(canvas, "_scenePaintCount") == scenePaints + 1, "alarm overlay cannot swallow pending camera/edit redraw");
        live["F1"].TimestampUtc = DateTime.UtcNow.AddMinutes(-2);
        Update();
        Check(!(bool)Call(canvas, "RoomHasFire", "ROOM")! && Signal(Call(canvas, "Indication", smoke)!) == MapSignal.Unknown,
            "stale fire sample is unknown, not current fire");
        live.Clear();
        readings[0] = Meter(40);
        view.SetReadings(readings, upsReadings); Update();
        buildingSignals = Field<IDictionary>(canvas, "_buildingSignals");
        Check((MapSignal)buildingSignals["W"]! == MapSignal.Fault && !buildingSignals.Contains("F"), "temperature alarm affects walls, never floor");
        Set(wall, "TemperatureSourceConfirmed", false); Set(wall2, "TemperatureSourceConfirmed", false);
        Set(floor, "TemperatureSourceConfirmed", false); Set(door, "TemperatureSourceConfirmed", false);
        Update();
        Check(Field<IDictionary>(canvas, "_buildingSignals").Count == 0, "unverified temperature mapping cannot alarm a room");
        ((Dictionary<string, double>)ups.Values)["BatteryStatus"] = 3;
        Update();
        Check(Signal(Call(canvas, "Indication", battery)!) == MapSignal.Fault, "battery fault overrides discharge warning");
        ((Dictionary<string, double>)ups.Values)["BatteryStatus"] = 2;
        ((Dictionary<string, double>)ups.Values)["OutputSource"] = 3;
        ((Dictionary<string, double>)ups.Values)["BatteryCurrent"] = -4;
        Update();
        Check(Signal(Call(canvas, "Indication", battery)!) == MapSignal.Normal, "charging alone does not raise a discharge warning");
        Call(view, "UpdateSelection", battery);
        Check(Field<Label>(view, "_selectedDetails").Text.Contains("Battery mode: Charging"), "signed negative battery current displays charging");
        ((Dictionary<string, double>)ups.Values)["BatteryCurrent"] = 4;
        ups.CardAlarms = new[] { new UpsCardAlarm { Name = "Fan failure", Active = true, CardSeverity = "Critical" } };
        Update();
        Check(Signal(Call(canvas, "Indication", battery)!) == MapSignal.Fault, "a real UPS fault takes priority over discharge");
        Call(view, "UpdateSelection", battery);
        Check(Field<Label>(view, "_selectedStatus").Text.Contains("Parent UPS UPS-QA: Fan failure") &&
            Field<Label>(view, "_selectedDetails").Text.Contains("NOT a diagnosis"), "battery inspector identifies a parent fan fault, never a rack battery fault");
        var upsSymbol = Asset("UPS", "UPS", "Vertiv EXL 500 kVA");
        Set(upsSymbol, "EquipmentId", "UPS-QA"); assets.Add(upsSymbol); Update();
        Call(view, "UpdateSelection", upsSymbol);
        Check(Field<Label>(view, "_selectedStatus").Text.StartsWith("Fan failure") &&
            Field<Label>(view, "_selectedDetails").Text.Contains("Fan failure [ACTIVE]") &&
            Field<Label>(view, "_selectedDetails").Text.Contains("Battery discharging"), "UPS inspector shows exact active card alarm and concurrent discharge evidence");
        ups.CardAlarms = Array.Empty<UpsCardAlarm>();
        ups.Quality = UpsQuality.Timeout; Update();
        Check(Signal(Call(canvas, "Indication", battery)!) == MapSignal.Unknown, "failed acquisition does not reuse bank values as current");
        Call(view, "UpdateSelection", upsSymbol);
        Check(Field<Label>(view, "_selectedStatus").Text.Contains("TIMEOUT") &&
            Field<Label>(view, "_selectedDetails").Text.Contains("Battery voltage: --"), "UPS timeout is communication quality with no current readings");
        live["D1"] = new MonitoringSample { DeviceId = "D1", Verified = true, TimestampUtc = DateTime.UtcNow,
            Signal = MapSignal.Normal, DoorOpen = true };
        Update();
        Check((bool?)Call(canvas, "LiveDoorOpen", "D1") == true && !Get<bool>(door, "IsOpen"), "live door contact does not mutate saved drawing state");
        live["D1"].Signal = MapSignal.Fault; live["D1"].Detail = "Forced door"; Update();
        Check((MapSignal)Field<IDictionary>(canvas, "_buildingSignals")["D"]! == MapSignal.Fault &&
            !(bool)Call(canvas, "RoomHasFire", "ROOM")!, "building door fault blinks only the door, never implies room fire");
        live["D1"].Verified = false; Update();
        Check(Call(canvas, "LiveDoorOpen", "D1") is null, "unverified contact never claims open or closed");
        var generator = Asset("GEN", "Generators", "Caterpillar generator 810 kVA");
        Set(generator, "MonitoringDeviceId", "G1"); assets.Add(generator);
        registry.Devices.Add(new MonitoringDevice { Id = "G1", Name = "Generator", Kind = "Equipment" });
        live["G1"] = new MonitoringSample { DeviceId = "G1", Verified = true, TimestampUtc = DateTime.UtcNow, Signal = MapSignal.Fault };
        Update();
        Check(Signal(Call(canvas, "Indication", generator)!) == MapSignal.Fault, "generic commissioned equipment faults use the same red indication");
        readings[0] = Meter(); readings[0].DecodedValues[10] = 900;
        view.SetAlarmRatings(new PlcSettings { Blocks = new List<PlcBlock> { new() { TransformerName = "T1", MaxLoadKw = 1000 } } });
        view.SetReadings(readings, upsReadings); Update();
        Check(Signal(Call(canvas, "Indication", transformer)!) == MapSignal.Fault, "map transformer overload uses configured rating and existing alarm limits");
        readings[0].CapturedAtUtc = DateTime.UtcNow.AddMinutes(-1); Update();
        Check(Signal(Call(canvas, "Indication", transformer)!) == MapSignal.Unknown, "stale PLC values cannot drive present equipment alarms");
        Call(canvas, "SetPageActive", false);
        Check(!Field<System.Windows.Forms.Timer>(canvas, "_blinkTimer").Enabled, "hidden map stops blink timer");
        Call(view, "UpdateSelection", battery);
        Check(Field<Label>(view, "_selectedDetails").Text.Contains("NOT individual rack"), "rack selection explicitly distinguishes parent bank data");
        Set(canvas, "EditMode", true); Call(canvas, "SelectAsset", smoke);
        FieldSet(view, "_editing", true);
        Call(view, "CreateEquipmentTools");
        Call(view, "CopySelection"); Call(view, "PasteSelection");
        var copied = assets[assets.Count - 1]!;
        Check(Get<string>(copied, "MonitoringDeviceId") == "" && Get<string>(copied, "ParentUpsId") == "" &&
            Get<string>(copied, "CameraIpAddress") == "", "copy/paste does not duplicate live monitoring identities");
    }

    private static void AlarmAccuracy()
    {
        var symbol = Asset("UPS-SYMBOL", "UPS", "Vertiv EXL 500 kVA");
        Set(symbol, "EquipmentId", "UPS-QA");
        var rack = Asset("RACK", "Battery Rooms", "Battery rack - 4 levels");
        Set(rack, "ParentUpsId", "UPS-QA");
        var ups = Ups();
        var values = (Dictionary<string, double>)ups.Values;
        values["OutputSource"] = 3; values["BatteryCurrent"] = 0;
        var upsData = new Dictionary<string, UpsSnapshot> { ["UPS-QA"] = ups };
        var registry = new MonitoringDeviceRegistry();
        var readings = new[] { Meter() };
        var live = new Dictionary<string, MonitoringSample>();
        var ratings = new Dictionary<string, double> { ["T1"] = 1000 };
        object Evaluate(object item) => Static("FactoryMapMonitoring", "EvaluateAsset", item, readings, upsData,
            registry, new Dictionary<string, IReadOnlyList<FireMapEventEvidence>>(), live, ratings)!;
        string Text(object result) => Get<string>(result, "Text");
        string Detail(object result) => Get<string>(result, "Detail");
        string[] names = { "Battery bad", "On battery", "Battery low", "Battery depleted", "Battery temperature high",
            "Input bad", "Output bad", "Output overload", "On bypass", "Bypass bad", "Output off as requested",
            "UPS off as requested", "Charger failed", "UPS output off", "UPS system off", "Fan failure",
            "Fuse failure", "General fault", "Diagnostic test failed", "Communications lost", "Awaiting power",
            "Shutdown pending", "Shutdown imminent", "Test in progress" };
        for (var index = 0; index < names.Length; index++)
        {
            ups.CardAlarms = new[] { new UpsCardAlarm { Name = names[index], DescriptionOid = "1.3.6.1.2.1.33.1.6.3." + (index + 1), Active = true } };
            var actual = Evaluate(symbol);
            Check(Text(actual).Contains(names[index], StringComparison.Ordinal) &&
                Signal(actual) == (index == 1 ? MapSignal.Warning : MapSignal.Fault),
                "map retains exact RFC alarm identity and priority: " + names[index]);
            var bank = Evaluate(rack);
            Check(Text(bank).StartsWith("Parent UPS UPS-QA: ") && Text(bank).Contains(names[index]) &&
                Detail(bank).Contains("NOT a diagnosis"), "rack explicitly attributes parent condition: " + names[index]);
        }
        foreach (var name in new[] { "Fan failure", "Redundant Fan Failure", "Multiple Fan Failure", "Manual transfer to bypass inhibited" })
        {
            ups.CardAlarms = new[] { new UpsCardAlarm { Name = name, Active = true, CardType = name.Contains("inhibited") ? "Warning" : "Fault" } };
            var actual = Evaluate(symbol);
            Check(Text(actual).Contains(name) && !Text(actual).Contains("battery fault", StringComparison.OrdinalIgnoreCase),
                "vendor alarm name is never replaced with battery diagnosis: " + name);
        }
        ups.CardAlarms = new[] { new UpsCardAlarm { Name = "Fan failure", Active = true, Acknowledged = true },
            new UpsCardAlarm { Name = "Charger failed", Active = true } };
        var multiple = Evaluate(symbol);
        Check(Text(multiple).Contains("Fan failure") && Text(multiple).Contains("+1 more") && Detail(multiple).Contains("Charger failed"),
            "all simultaneous alarms remain visible; acknowledgement never clears an active fault");
        ups.CardAlarms = new[] { new UpsCardAlarm { Name = "Fan failure", Active = false, CardSeverity = "Critical" } };
        Check(Signal(Evaluate(symbol)) == MapSignal.Normal, "cleared retained card faults do not blink");
        ups.CardAlarms = new[] { new UpsCardAlarm { Name = "Fan failure", Active = null } };
        Check(Signal(Evaluate(symbol)) == MapSignal.Warning && Text(Evaluate(symbol)).Contains("Unconfirmed"),
            "unknown card state is labelled unconfirmed, not a confirmed fault");
        ups.CardAlarms = new[] { new UpsCardAlarm { Name = "General fault", Active = true, EvidenceConflict = true } };
        Check(Signal(Evaluate(symbol)) == MapSignal.Warning && Detail(Evaluate(symbol)).Contains("UNCONFIRMED"),
            "conflicting card evidence stays explicit amber");
        ups.CardAlarms = Array.Empty<UpsCardAlarm>(); ups.AlarmDetailsComplete = false; values["AlarmsPresent"] = 3;
        Check(Signal(Evaluate(symbol)) == MapSignal.Fault && Text(Evaluate(symbol)).Contains("details incomplete"),
            "alarm count without details never fabricates a fault type");
        ups.AlarmDetailsComplete = true; values["AlarmsPresent"] = 0;
        values["OutputSource"] = 5; values["BatteryCurrent"] = 4;
        Check(Signal(Evaluate(symbol)) == MapSignal.Warning && Text(Evaluate(symbol)).Contains("UPS ON BATTERY"),
            "explicit on-battery identity remains amber on UPS");
        ups.CardAlarms = new[] { new UpsCardAlarm { Name = "Fan failure", Active = true } };
        Check(Text(Evaluate(symbol)).StartsWith("Fan failure") && Signal(Evaluate(symbol)) == MapSignal.Fault &&
            Detail(Evaluate(symbol)).Contains("UPS ON BATTERY"), "fan fault outranks discharge without discarding either");
        ups.CardAlarms = Array.Empty<UpsCardAlarm>(); values["BatteryCurrent"] = -4;
        Check(((string)Static("FactoryMapMonitoring", "UpsBatteryMode", ups)!).Contains("conflicts"),
            "contradictory charging current and battery source are not presented as a verified mode");
        values["OutputSource"] = 3; values["BatteryCurrent"] = 0;
        foreach (var quality in Enum.GetValues<UpsQuality>().Where(value => value != UpsQuality.Good))
        {
            ups.Quality = quality;
            var actual = Evaluate(symbol);
            Check(Signal(actual) == MapSignal.Unknown && Text(actual).Contains(UpsQualityText.Describe(quality)) &&
                !Text(actual).Contains("battery fault"), "acquisition quality is shown exactly, not as hardware fault: " + quality);
        }
        ups.Quality = UpsQuality.Good; ups.TimestampUtc = DateTime.UtcNow.AddMinutes(-2);
        Check(Signal(Evaluate(symbol)) == MapSignal.Unknown &&
            (string)typeof(FactoryMapView).GetMethod("UpsReading", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, new object[] { ups, (Func<UpsSnapshot, double?>)(item => item.Value("BatteryVoltage")), "V" })! == "--",
            "old Good UPS snapshot neither alarms nor supplies current inspector readings");
        ups.TimestampUtc = DateTime.UtcNow.AddMinutes(2);
        Check(Signal(Evaluate(symbol)) == MapSignal.Unknown, "future UPS timestamps are invalid, not fresh");
        ups.TimestampUtc = DateTime.UtcNow; ups.DeviceId = "UPS-WRONG";
        Check(Text(Evaluate(symbol)).Contains("identity mismatch"), "dictionary key cannot substitute a different UPS identity");
        ups.DeviceId = "UPS-QA";
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, double.MaxValue, 0, 5.2 })
        {
            values["OutputSource"] = invalid;
            Check(Signal(Evaluate(symbol)) == MapSignal.Unknown && Text(Evaluate(symbol)).Contains("measurements invalid"),
                "invalid UPS status cannot throw or fabricate a battery condition: " + invalid);
        }
        values["OutputSource"] = 3;
        ups.TimestampUtc = default;
        Check(Signal(Evaluate(symbol)) == MapSignal.Unknown, "UPS timestamp is mandatory");
        ups.TimestampUtc = DateTime.UtcNow;
        var panel = Asset("PANEL", "Electrical Panels", "ACB panel with energy meter");
        Set(panel, "EquipmentId", "UPS-QA"); Set(panel, "MeterSourceId", "T1");
        ups.CardAlarms = new[] { new UpsCardAlarm { Name = "Fan failure", Active = true } };
        Check(!Text(Evaluate(panel)).Contains("Fan") && Detail(Evaluate(panel)).Contains("Source meter: T1"),
            "independent meter governs panel indication even when attached to a faulty UPS");
        readings[0].DecodedValues[0] = 260;
        Check(Text(Evaluate(panel)).Contains("OVER VOLTAGE") && !Text(Evaluate(panel)).Contains("Fan"),
            "meter voltage fault follows meter source, never attached equipment alarms");
        readings[0].CapturedAtUtc = DateTime.UtcNow.AddMinutes(-1);
        readings[0].PowerState = TransformerPowerState.Off;
        Check(Signal(Evaluate(panel)) == MapSignal.Unknown &&
            Static("FactoryMapMeterLink", "Find", panel, readings) is null, "stale meter link is unavailable in both evaluator and inspector");
        readings[0].CapturedAtUtc = DateTime.UtcNow.AddMinutes(1);
        Check(Signal(Evaluate(panel)) == MapSignal.Unknown, "future meter power OFF cannot trigger current power alarm");
        readings[0].CapturedAtUtc = null;
        Check(Signal(Evaluate(panel)) == MapSignal.Unknown, "meter power OFF without a timestamp cannot trigger current power alarm");
        readings[0] = Meter(); readings[0].DecodedValues[0] = float.NaN;
        Check(Signal(Evaluate(panel)) == MapSignal.Unknown, "non-finite voltage is unavailable, never normal zero voltage");
        readings[0] = Meter(); readings[0].DecodedValues[15] = (float)AlarmSettingsStore.Current.Transformers.TemperatureAlarmC + 1;
        readings[0].DecodedValues[0] = float.NaN; readings[0].DecodedValues[1] = float.NaN; readings[0].DecodedValues[2] = float.NaN;
        Check(Signal(Evaluate(panel)) == MapSignal.Fault && Text(Evaluate(panel)).Contains("TEMP"),
            "a valid temperature fault is not concealed by unavailable voltage channels");
        readings[0] = Meter(); readings[0].DecodedValues = new float[] { 230, 230, 230 };
        Check(Signal(Evaluate(panel)) == MapSignal.Unknown, "partial meter data never claims complete normal coverage");
        var door = Asset("DOOR", "Access Control", "Single swing gate");
        Set(door, "MonitoringDeviceId", "D1");
        registry.Devices.Add(new MonitoringDevice { Id = "D1", Kind = "Access", Name = "Door" });
        live["D1"] = new MonitoringSample { DeviceId = "D1", TimestampUtc = DateTime.UtcNow, Verified = true, Signal = MapSignal.Fault,
            DoorOpen = false, Detail = "Door forced / contact mismatch" };
        Check(Text(Evaluate(door)).Contains("Fault") && Text(Evaluate(door)).Contains("Door CLOSED") &&
            Detail(Evaluate(door)).Contains("forced"), "door position does not hide access fault");
        registry.Devices[0].Kind = "Fire";
        Check(Signal(Evaluate(door)) == MapSignal.Unknown && Text(Evaluate(door)).Contains("type mismatch"),
            "a mismatched registered device cannot drive another symbol's alarm");
    }

    private static void Editors()
    {
        var battery = Asset("B", "Battery Rooms", "Battery rack - 4 levels");
        Set(battery, "ParentUpsId", "UPS-QA");
        var ups = Asset("U", "UPS", "Vertiv EXL 500 kVA"); Set(ups, "EquipmentId", "UPS-QA");
        using var editor = (Form)Activator.CreateInstance(Type("FactoryMapItemForm"), battery, List("FactoryMapAsset", ups))!;
        Check(Field<ComboBox>(editor, "_parentUps").SelectedIndex == 1, "parent UPS editor resolves stable equipment ID");
        Call(editor, "ApplyItem", null, EventArgs.Empty);
        Check(Get<string>(Get<object>(editor, "Asset"), "Model") == "Battery rack - 4 levels" &&
            Get<string>(Get<object>(editor, "Asset"), "ParentUpsId") == "UPS-QA", "linking UPS preserves battery model");
        using var alarmView = new AlarmSettingsView(Admin);
        var inputs = Field<Dictionary<string, NumericUpDown>>(alarmView, "_inputs");
        Check(inputs["Rooms.LowAlarmC"].Minimum == -50 && inputs["Rooms.HighAlarmC"].Maximum == 150, "room limits UI exposes four dedicated bounded temperature thresholds");
        var cold = new AlarmSettings { Rooms = new RoomTemperatureLimits { LowAlarmC = -20, LowWarningC = -10, HighWarningC = 5, HighAlarmC = 10 } };
        Call(alarmView, "FillInputs", cold);
        Check(inputs["Rooms.LowAlarmC"].Value == -20, "cold-room negative temperature limits are retained");
        var shapes = (IDictionary)Type("FactoryMapItemForm").GetField("Shapes", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var fireShapes = (string[])shapes["Fire System"]!;
        Check(fireShapes.Contains("CO2 cylinder bank") && fireShapes.Contains("FM200 cylinder bank") &&
            fireShapes.Contains("Zeta heat detector") && fireShapes.Contains("Zeta manual call point"), "fire device catalog includes detection, annunciation and suppression assets");
        foreach (var shape in fireShapes)
        {
            var sample = Asset("PREVIEW", "Fire System", shape);
            using var preview = (Control)Activator.CreateInstance(Type("FactoryMapItemPreview"))!;
            preview.Size = new Size(250, 200);
            Call(preview, "SetAsset", sample);
            using var bitmap = new Bitmap(250, 200);
            preview.DrawToBitmap(bitmap, preview.ClientRectangle);
            Check(true, shape + " renders with physical dimensions");
        }

    }

    private static void RegistryUi(string temporary, string evidence)
    {
        using var service = new FireConnectionService(new FireConnectionStore(Path.Combine(temporary, "fire.json")), allowCommunication: false);
        using var panels = new FirePanelManager(service);
        using var host = new Form { Size = new Size(980, 600), Text = "Isolated monitoring registry" };
        using var view = new MonitoringDevicesView(Admin, panels) { Dock = DockStyle.Fill };
        host.Controls.Add(view); host.Show(); Application.DoEvents();
        var grids = Field<List<DataGridView>>(view, "_grids");
        Check(grids.Count == 4 && grids.All(grid => grid.AllowUserToAddRows && grid.AllowUserToDeleteRows), "configuration exposes four editable registries");
        var lists = Field<Dictionary<string, System.ComponentModel.BindingList<MonitoringDevice>>>(view, "_lists");
        var camera = lists["Camera"].AddNew();
        Check(camera.Kind == "Camera" && camera.Id.Length > 0, "new camera row gets correct kind and stable identity");
        lists["Camera"].CancelNew(lists["Camera"].Count - 1);
        using var image = new Bitmap(view.Width, view.Height);
        view.DrawToBitmap(image, view.ClientRectangle);
        image.Save(Path.Combine(evidence, "device-registry-980.png"));
        using var offline = new MonitoringDevicesView(UserSession.OfflinePreview(), panels);
        Check(Field<List<DataGridView>>(offline, "_grids").All(grid => grid.ReadOnly && !grid.AllowUserToAddRows && !grid.AllowUserToDeleteRows),
            "offline registry is read-only and cannot add/delete");
    }
}
