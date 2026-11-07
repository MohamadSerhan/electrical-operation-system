using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Forms;
using EOEMS.PowerTransformersScadaUI.V5;

internal static class FactoryCompletionValidation
{
    private static readonly Assembly Assembly = typeof(MainShellForm).Assembly;
    private static Type Type(string name) => Assembly.GetType("EOEMS.PowerTransformersScadaUI.V5." + name, true)!;
    private static object Call(string type, string method, params object?[] args) =>
        Type(type).GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;
    private static T Get<T>(object value, string name) => (T)value.GetType().GetProperty(name)!.GetValue(value)!;
    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }

    public static void Run(string prepared)
    {
        var empty = Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("FactoryMapAsset")))!;
        var assets = (IList)Call("FactoryMapLayoutStore", "LoadAssets", empty, Path.Combine(prepared, "factory-map-layout.json"));
        var parts = (IList)Call("FactoryMapBuildingStore", "Load", Path.Combine(prepared, "factory-map-building.json"));
        Call("FactoryMapLayoutStore", "ValidateAssets", assets);
        Call("FactoryMapBuildingStore", "Validate", parts);
        var layout = JsonNode.Parse(File.ReadAllText(Path.Combine(prepared, "factory-map-layout.json")))!;
        var building = JsonNode.Parse(File.ReadAllText(Path.Combine(prepared, "factory-map-building.json")))!;
        var review = JsonNode.Parse(File.ReadAllText(Path.Combine(prepared, "factory-map-drawing-review.json")))!;
        var baseline = Path.Combine(prepared, "baseline");
        var oldLayout = JsonNode.Parse(File.ReadAllText(Path.Combine(baseline, "factory-map-layout.json")))!;
        var oldBuilding = JsonNode.Parse(File.ReadAllText(Path.Combine(baseline, "factory-map-building.json")))!;
        if (review["LineInventoryRevision"] is not null)
        {
            ValidateInventory(prepared, assets, layout, review, oldLayout);
            return;
        }
        if (review["EquipmentCorrectionRevision"] is not null)
        {
            ValidateCorrection(prepared, assets, parts, layout, building, review, oldLayout, oldBuilding);
            return;
        }
        foreach (var old in oldLayout["Assets"]!.AsArray())
            Check(JsonNode.DeepEquals(old, layout["Assets"]!.AsArray().Single(a => a!["Id"]!.GetValue<string>() == old!["Id"]!.GetValue<string>())),
                "Existing equipment changed: " + old!["Id"]);
        foreach (var old in oldBuilding["Parts"]!.AsArray())
            Check(JsonNode.DeepEquals(old, building["Parts"]!.AsArray().Single(a => a!["Id"]!.GetValue<string>() == old!["Id"]!.GetValue<string>())),
                "Existing building geometry changed: " + old!["Id"]);
        Check(File.ReadAllBytes(Path.Combine(prepared, "monitoring-devices.json")).SequenceEqual(
            File.ReadAllBytes(Path.Combine(baseline, "monitoring-devices.json"))), "Fire registry must be byte-identical.");

        var byId = assets.Cast<object>().ToDictionary(a => Get<string>(a, "Id"));
        var additions = assets.Cast<object>().Where(a => Get<string>(a, "Id").StartsWith("COMPLETE-")).ToArray();
        var revision = review["FactoryCompletionRevision"]!;
        Check(additions.Length == revision["AddedAssets"]!.GetValue<int>() && additions.Length >= 250,
            "The complete reference equipment inventory must not be silently reduced.");
        Check(parts.Cast<object>().Count(p => Get<string>(p, "Id").StartsWith("COMPLETE-")) ==
            revision["AddedBuildingParts"]!.GetValue<int>(), "Bridge part count.");
        Check(revision["Kits"]!.AsArray().Count == 7, "Exactly seven production-room kits.");
        foreach (var kit in revision["Kits"]!.AsArray())
        {
            var line = kit!["Line"]!.GetValue<string>();
            var expectedBrand = line is "PET5" or "PET6" ? "Krones" : "Sidel";
            Check(kit["Manufacturer"]!.GetValue<string>() == expectedBrand &&
                kit["ManufacturerEvidence"]!.GetValue<string>() == "User confirmed", "Manufacturer assignment: " + line);
            var items = kit["AddedIds"]!.AsArray().Select(id => byId[id!.GetValue<string>()]).ToArray();
            var models = items.Select(a => Get<string>(a, "Model")).ToHashSet();
            foreach (var required in new[] { "Product buffer tank", "Sanitary product pump", "Sanitary valve manifold",
                         "CIP station - 1 tank", "CIP return skid", "Foam cleaning station", "Filler control panel",
                         "PLC cabinet", "HMI console", "Motor control center", "Variable frequency drive" })
                Check(models.Contains(required), line + " missing " + required);
            if (line == "CAN1")
                Check(models.Contains("Can seamer") && models.Contains("Empty container depalletizer") &&
                    !models.Contains("PET bottle blower") && !models.Contains("Cap elevator"), "Canline must not use a PET/cap kit.");
            else
                foreach (var required in new[] { "Preform hopper", "Preform elevator", "PET bottle blower", "Cap elevator",
                             "Cap sorter and chute", "Cap sterilizer" })
                    Check(models.Contains(required), line + " missing " + required);
            var packing = kit["PackingIds"]!.AsArray().Select(id => byId[id!.GetValue<string>()]).ToArray();
            Check(packing.Count(a => Get<string>(a, "Model") == "Conveyor") == 10, line + " transfer belt count.");
            foreach (var required in new[] { "Bottle inspection station", "Bottle reject station", "Inline checkweigher",
                         "Date coding station", "Bottle labeler", "Bottle accumulation table", "Shrink wrapper",
                         "Case packer", "Palletizer", "Pallet stretch wrapper", "Bottle air knife dryer" })
                Check(packing.Any(a => Get<string>(a, "Model") == required), line + " missing downstream " + required);
            var stations = packing.Where(a => Get<string>(a, "Model") != "Conveyor").OrderBy(a => Get<PointF>(a, "Position").X).ToArray();
            var belts = packing.Where(a => Get<string>(a, "Model") == "Conveyor").OrderBy(a => Get<PointF>(a, "Position").X).ToArray();
            for (var i = 0; i < belts.Length; i++)
            {
                var previous = Bounds(stations[i]);
                var next = Bounds(stations[i + 1]);
                var belt = Bounds(belts[i]);
                Check(Math.Abs(previous.Right - belt.Left) < .15f && Math.Abs(next.Left - belt.Right) < .15f &&
                    Math.Abs(previous.Y + previous.Height / 2 - belt.Y - belt.Height / 2) < .15f &&
                    Math.Abs(next.Y + next.Height / 2 - belt.Y - belt.Height / 2) < .15f,
                    line + " transfer conveyor must connect adjacent stages at actual rendered dimensions.");
            }
        }

        foreach (var asset in additions)
        {
            Check(Get<float>(asset, "BaseHeightMeters") == 0, "New reference items start at floor level.");
            Check(new[] { "EquipmentId", "MeterSourceId", "MonitoringDeviceId", "ParentUpsId", "CameraIpAddress" }
                .All(link => Get<string>(asset, link) == ""), "No invented live equipment links.");
            Check(Get<string>(asset, "Location").Contains("PROVISIONAL"), "Visible provisional label.");
            var box = Bounds(asset);
            var floors = parts.Cast<object>().Where(p => Get<string>(p, "Kind") == "Floor" &&
                Get<string>(p, "RoomId") == Get<string>(asset, "RoomId") &&
                Get<string>(p, "Level") == Get<string>(asset, "Level"));
            Check(floors.Any(f => Corners(box).All(c =>
            {
                var local = (PointF)Call("FactoryMapBuildingStore", "LocalPoint", f, c);
                return Math.Abs(local.X) <= Get<float>(f, "Length") / 2 + .1 &&
                       Math.Abs(local.Y) <= Get<float>(f, "Depth") / 2 + .1;
            })), "Item does not fit its actual room floor at nominal size: " + Get<string>(asset, "Id"));
            if (Get<string>(asset, "Model") == "Conveyor") continue;
            foreach (var other in assets.Cast<object>().Where(a => Get<string>(a, "Id") != Get<string>(asset, "Id") &&
                         Get<bool>(a, "IsPlaced") && Get<string>(a, "Level") == Get<string>(asset, "Level") &&
                         Get<string>(a, "Category") != "Fire System" && Get<string>(a, "Model") != "Conveyor"))
            {
                var intersection = RectangleF.Intersect(box, Bounds(other));
                Check(intersection.Width < .15f || intersection.Height < .15f,
                    $"Reference equipment overlaps {Get<string>(asset, "Id")} / {Get<string>(other, "Id")}.");
            }
        }
        var registry = MonitoringDeviceStore.Load(Path.Combine(prepared, "monitoring-devices.json"));
        var drafts = registry.Devices.Where(d => d.Id.StartsWith("DRAW-")).ToArray();
        Check(drafts.Length == 199 && drafts.All(d => !d.Commissioned && d.PanelId == "" && d.SiteId == "" &&
            d.Loop == "" && d.Address == "" && d.EventAddress == ""), "199 fire drafts must remain uncommissioned.");
        using var audit = JsonDocument.Parse(File.ReadAllText(Path.Combine(prepared, "factory-fire-position-audit.json")));
        var report = audit.RootElement;
        Check(report.GetProperty("Count").GetInt32() == 199 && !report.GetProperty("FirePositionsChanged").GetBoolean(),
            "All fire symbols must be audited without moving them.");
        var mismatches = 0;
        foreach (var row in report.GetProperty("Symbols").EnumerateArray())
        {
            var id = row.GetProperty("Id").GetString()!;
            var asset = byId[id];
            var x = Get<PointF>(asset, "Position").X * 1365;
            var y = Get<PointF>(asset, "Position").Y * 766;
            var dx = x - row.GetProperty("ExpectedX").GetDouble();
            var dy = y - row.GetProperty("ExpectedY").GetDouble();
            var distance = Math.Sqrt(dx * dx + dy * dy) / 8;
            Check(Math.Abs(distance - row.GetProperty("DifferenceMeters").GetDouble()) < .0001, "Fire difference calculation: " + id);
            var matches = distance <= report.GetProperty("PositionToleranceMeters").GetDouble();
            Check(matches == row.GetProperty("PositionMatches").GetBoolean(), "Fire tolerance result: " + id);
            if (!matches) mismatches++;
            foreach (var field in new[] { "ModelMatches", "TagMatches", "LevelMatches", "MonitoringLinkMatches", "Uncommissioned" })
                Check(row.GetProperty(field).GetBoolean(), "Fire identity verification failed: " + id + " / " + field);
        }
        Check(mismatches == report.GetProperty("PositionMismatches").GetInt32(), "Fire mismatch summary must equal the itemized report.");

        var roundtrip = Path.Combine(prepared, "validation-roundtrip.json");
        try
        {
            Call("FactoryMapLayoutStore", "SaveAssets", assets, roundtrip);
            var reloaded = (IList)Call("FactoryMapLayoutStore", "LoadAssets", empty, roundtrip);
            Check(reloaded.Count == assets.Count, "Completed layout round-trip.");
        }
        finally
        {
            if (File.Exists(roundtrip)) File.Delete(roundtrip);
            if (File.Exists(roundtrip + ".bak")) File.Delete(roundtrip + ".bak");
        }
        Render(prepared, assets, parts);
        Console.WriteLine($"Factory completion: {additions.Length} new items, seven manufacturer-assigned kits, preserved originals/registry, " +
                          $"real-size fit, connected local belts, four floor renders, {mismatches} retained fire displacements verified.");
    }

    private static RectangleF Bounds(object asset)
    {
        var dimensions = Call("FactoryMapEquipmentSizes", "Dimensions", asset);
        var tupleType = dimensions.GetType();
        var length = (float)tupleType.GetField("Item1")!.GetValue(dimensions)! * 8 * Get<float>(asset, "ScaleX");
        var width = (float)tupleType.GetField("Item2")!.GetValue(dimensions)! * 8 * Get<float>(asset, "ScaleY");
        var angle = Get<float>(asset, "Rotation") * Math.PI / 180;
        var w = (float)(Math.Abs(length * Math.Cos(angle)) + Math.Abs(width * Math.Sin(angle)));
        var d = (float)(Math.Abs(length * Math.Sin(angle)) + Math.Abs(width * Math.Cos(angle)));
        var position = Get<PointF>(asset, "Position");
        return new RectangleF(position.X * 1365 - w / 2, position.Y * 766 - d / 2, w, d);
    }

    private static PointF[] Corners(RectangleF r) =>
        [new(r.Left, r.Top), new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom)];

    private static void ValidateCorrection(string prepared, IList assets, IList parts, JsonNode layout,
        JsonNode building, JsonNode review, JsonNode oldLayout, JsonNode oldBuilding)
    {
        var revision = review["EquipmentCorrectionRevision"]!;
        var removed = revision["RemovedIds"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet();
        var modified = revision["ModifiedIds"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet();
        var added = revision["AddedIds"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet();
        var current = layout["Assets"]!.AsArray().ToDictionary(n => n!["Id"]!.GetValue<string>(), n => n!);
        var objects = assets.Cast<object>().ToDictionary(a => Get<string>(a, "Id"));
        var previous = oldLayout["Assets"]!.AsArray().ToDictionary(n => n!["Id"]!.GetValue<string>(), n => n!);
        foreach (var node in revision["GeneratorExteriorAssignmentIds"]!.AsArray())
        {
            var id = node!.GetValue<string>();
            var old = previous[id];
            var value = current[id];
            Check(old["Category"]!.GetValue<string>() == "Generators" && old["RoomId"]!.GetValue<string>() == "DRAW-ROOM-GEN" &&
                value["RoomId"]!.GetValue<string>() == "" && value["Location"]!.GetValue<string>().Contains("confirmed by user"),
                "Only user-confirmed exterior generators may have indoor membership cleared.");
            foreach (var field in new[] { "X", "Y", "Rotation", "Model", "ScaleX", "ScaleY", "ScaleZ", "IsPlaced",
                         "BaseHeightMeters", "EquipmentId", "MeterSourceId", "MonitoringDeviceId" })
                Check(JsonNode.DeepEquals(old[field], value[field]), "Exterior-generator placement/link changed: " + id + " / " + field);
        }
        foreach (var (id, original) in previous)
        {
            if (removed.Contains(id))
            {
                Check(id.StartsWith("COMPLETE-") && !current.ContainsKey(id), "Only known generated equipment can be removed.");
                Check(new[] { "EquipmentId", "MeterSourceId", "MonitoringDeviceId", "ParentUpsId", "CameraIpAddress" }
                    .All(k => original[k]!.GetValue<string>() == ""), "Removed reference must be unlinked.");
                continue;
            }
            Check(current.ContainsKey(id), "Unreported missing equipment: " + id);
            if (!modified.Contains(id))
                Check(JsonNode.DeepEquals(original, current[id]), "Unexpected change: " + id);
            else
            {
                var normalized = current[id].DeepClone();
                foreach (var field in new[] { "Name", "Location", "X", "Y", "Rotation", "IsPlaced", "RoomId", "Level",
                             "BaseHeightMeters", "ConveyorLength", "ScaleX", "ScaleY", "ScaleZ" })
                    normalized[field] = original[field]?.DeepClone();
                Check(JsonNode.DeepEquals(original, normalized), "Non-placement identity, model or link changed: " + id);
            }
            if (original["Category"]!.GetValue<string>() == "Fire System")
                Check(JsonNode.DeepEquals(original, current[id]), "Every fire asset must remain unchanged, not only its center.");
        }
        Check(current.Keys.Where(id => !previous.ContainsKey(id)).ToHashSet().SetEquals(added), "Added-ID ledger.");
        foreach (var original in oldBuilding["Parts"]!.AsArray())
        {
            var id = original!["Id"]!.GetValue<string>();
            var matches = building["Parts"]!.AsArray().Where(p => p!["Id"]!.GetValue<string>() == id).ToArray();
            if (removed.Contains(id))
                Check(id.StartsWith("COMPLETE-BRIDGE-") && matches.Length == 0, "Only the unsupported generated bridge may be removed.");
            else
                Check(matches.Length == 1 && JsonNode.DeepEquals(original, matches[0]), "Existing room partition/part changed: " + id);
        }
        Check(!parts.Cast<object>().Any(p => Get<string>(p, "Id").StartsWith("COMPLETE-BRIDGE-")), "No invented bridge remains.");
        var selectedReferences = revision["Kits"]!.AsArray()
            .SelectMany(k => k!["ReferenceIds"]!.AsArray()).Select(n => n!.GetValue<string>()).ToHashSet();
        Check(!assets.Cast<object>().Any(a => Get<string>(a, "Id").Contains("-BELT-") ||
            ((Get<string>(a, "Id").Contains("-PACK-") || Get<string>(a, "Id").Contains("-AUX-")) &&
             !selectedReferences.Contains(Get<string>(a, "Id")))),
            "Repeated off-room packing rows and auxiliary overflow must be removed; a retained relocated reference keeps its ID.");
        Check(File.ReadAllBytes(Path.Combine(prepared, "monitoring-devices.json")).SequenceEqual(
            File.ReadAllBytes(Path.Combine(prepared, "baseline", "monitoring-devices.json"))), "Registry unchanged.");
        Check(revision["Kits"]!.AsArray().Count == 7, "Seven retained room assignments.");
        var checkedIds = new HashSet<string>();
        foreach (var kit in revision["Kits"]!.AsArray())
        {
            var line = kit!["Line"]!.GetValue<string>();
            Check(kit["Manufacturer"]!.GetValue<string>() == (line is "PET5" or "PET6" ? "Krones" : "Sidel"),
                "Correct manufacturer assignment.");
            var ids = kit["ReferenceIds"]!.AsArray().Concat(kit["ConveyorIds"]!.AsArray())
                .Select(n => n!.GetValue<string>()).Append(kit["CoreId"]!.GetValue<string>()).ToArray();
            var floor = parts.Cast<object>().Single(p => Get<string>(p, "Kind") == "Floor" &&
                Get<string>(p, "RoomId") == kit["RoomId"]!.GetValue<string>());
            foreach (var id in ids)
            {
                Check(checkedIds.Add(id), "A machine cannot serve multiple room schematics: " + id);
                var a = objects[id];
                Check(Get<bool>(a, "IsPlaced") && Get<string>(a, "RoomId") == kit["RoomId"]!.GetValue<string>(),
                    "No room equipment placed in a different hall or auxiliary bay.");
                Check(Get<float>(a, "BaseHeightMeters") == 0, "Floor-mounted reference item elevation.");
                var box = Bounds(a);
                foreach (var corner in Corners(box))
                {
                    var point = (PointF)Call("FactoryMapBuildingStore", "LocalPoint", floor, corner);
                    Check(Math.Abs(point.X) <= Get<float>(floor, "Length") / 2 - 2 &&
                        Math.Abs(point.Y) <= Get<float>(floor, "Depth") / 2 - 2,
                        "Machine/belt crosses its actual room wall: " + id);
                }
                foreach (var door in parts.Cast<object>().Where(p => Get<string>(p, "Kind") is "Door" or "Gate" &&
                             Get<string>(p, "Level") == Get<string>(a, "Level")))
                {
                    var resolved = Call("FactoryMapBuildingStore", "Resolve", door, parts);
                    var width = Math.Max(24, Get<float>(door, "Length") + 4);
                    var access = new RectangleF(Get<float>(resolved, "X") * 1365 - width / 2,
                        Get<float>(resolved, "Y") * 766 - width / 2, width, width);
                    var intersection = RectangleF.Intersect(box, access);
                    Check(intersection.Width < .15 || intersection.Height < .15, "Doorway clearance blocked: " + id);
                }
                foreach (var other in ids.Where(other => other != id && Get<string>(objects[other], "Model") != "Conveyor"))
                {
                    var intersection = RectangleF.Intersect(box, Bounds(objects[other]));
                    Check(intersection.Width < .15 || intersection.Height < .15,
                        "Unintended room equipment overlap: " + id + " / " + other);
                }
            }
            foreach (var id in kit["ConveyorIds"]!.AsArray().Select(n => n!.GetValue<string>()))
            {
                var belt = Bounds(objects[id]);
                var y = belt.Top + belt.Height / 2;
                var start = new PointF(belt.Left, y);
                var finish = new PointF(belt.Right, y);
                bool Touches(PointF p) => ids.Where(other => other != id).Any(other =>
                {
                    var box = Bounds(objects[other]);
                    return p.X >= box.Left - .2 && p.X <= box.Right + .2 && p.Y >= box.Top - .2 && p.Y <= box.Bottom + .2;
                });
                Check(Touches(start) && (Touches(finish) || Math.Abs(finish.X -
                    (Get<float>(floor, "X") * 1365 + Get<float>(floor, "Length") / 2 - 20)) < .2),
                    "Conveyor must join an actual room station or its documented local output: " + id);
            }
        }
        Check(assets.Count < 400 && removed.Count >= 200, "Unsupported bulk inventory must actually be corrected.");
        using var audit = JsonDocument.Parse(File.ReadAllText(Path.Combine(prepared, "factory-fire-position-audit.json")));
        Check(audit.RootElement.GetProperty("Count").GetInt32() == 199 &&
            !audit.RootElement.GetProperty("FirePositionsChanged").GetBoolean(), "Retained fire audit.");
        var roundtrip = Path.Combine(prepared, "validation-roundtrip.json");
        try
        {
            Call("FactoryMapLayoutStore", "SaveAssets", assets, roundtrip);
            var empty = Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("FactoryMapAsset")))!;
            var loaded = (IList)Call("FactoryMapLayoutStore", "LoadAssets", empty, roundtrip);
            Check(loaded.Count == assets.Count, "Corrected layout persists.");
        }
        finally
        {
            if (File.Exists(roundtrip)) File.Delete(roundtrip);
            if (File.Exists(roundtrip + ".bak")) File.Delete(roundtrip + ".bak");
        }
        Render(prepared, assets, parts);
        Console.WriteLine($"Room correction passed: {removed.Count} unsupported references removed, all partitions/fire assets/links preserved, " +
                          "seven room-local manufacturer assignments, actual model fit, no blocked doors or overlaps, and connected local conveyors.");
    }

    private static void ValidateInventory(string prepared, IList assets, JsonNode layout, JsonNode review, JsonNode oldLayout)
    {
        var revision = review["LineInventoryRevision"]!;
        var added = revision["AddedIds"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet();
        var originals = oldLayout["Assets"]!.AsArray().ToDictionary(n => n!["Id"]!.GetValue<string>(), n => n!);
        var current = layout["Assets"]!.AsArray().ToDictionary(n => n!["Id"]!.GetValue<string>(), n => n!);
        foreach (var (id, value) in originals)
            Check(current.TryGetValue(id, out var saved) && JsonNode.DeepEquals(value, saved),
                "Inventory planning must not alter any existing equipment: " + id);
        Check(current.Keys.Where(id => !originals.ContainsKey(id)).ToHashSet().SetEquals(added), "Exact inventory addition ledger.");
        foreach (var name in new[] { "factory-map-building.json", "monitoring-devices.json" })
            Check(File.ReadAllBytes(Path.Combine(prepared, name)).SequenceEqual(File.ReadAllBytes(Path.Combine(prepared, "baseline", name))),
                "Inventory planning must byte-preserve " + name);
        Check(added.Count > 0 && !revision["PlacedItemsChanged"]!.GetValue<bool>(), "Unplaced inventory only.");
        foreach (var id in added)
        {
            var value = current[id];
            Check(id.StartsWith("COMPLETE-PLAN-") && !value["IsPlaced"]!.GetValue<bool>() &&
                value["RoomId"]!.GetValue<string>() == "" && value["BaseHeightMeters"]!.GetValue<float>() == 0,
                "Unconfirmed equipment must not appear on the map or acquire an assumed room/elevation.");
            Check(new[] { "EquipmentId", "MeterSourceId", "MonitoringDeviceId", "ParentUpsId", "CameraIpAddress" }
                .All(k => value[k]!.GetValue<string>() == ""), "No invented live links.");
            Check(value["Source"]!.GetValue<string>().Contains("NOT confirmed") ||
                id == revision["CTAId"]!.GetValue<string>(), "Explicit planning status.");
        }
        var referencedAdditions = new HashSet<string>();
        var lines = revision["Lines"]!.AsArray();
        Check(lines.Count == 7 && lines.Select(l => l!["Line"]!.GetValue<string>()).Distinct().Count() == 7, "Seven unique line plans.");
        var common = new[] { "Product buffer tank", "Sanitary product pump", "Sanitary valve manifold", "CIP station - 1 tank",
            "CIP return skid", "Foam cleaning station", "Bottle inspection station", "Bottle reject station", "Date coding station",
            "Bottle accumulation table", "Shrink wrapper", "Case packer", "Palletizer", "Pallet stretch wrapper", "Variable frequency drive" };
        foreach (var line in lines)
        {
            var id = line!["Line"]!.GetValue<string>();
            var water = id is "PET4" or "PET5";
            var can = id == "CAN1";
            Check(line["Manufacturer"]!.GetValue<string>() == (id is "PET5" or "PET6" ? "Krones" : "Sidel"),
                "Confirmed manufacturer assignment: " + id);
            Check(line["Product"]!.GetValue<string>() == (water ? "Water" : can ? "Canned beverage - product unconfirmed" : "CSD"),
                "Confirmed product assignment: " + id);
            var existing = line["ExistingIds"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet();
            Check(existing.All(originals.ContainsKey), "Only known existing line assets count as coverage.");
            var coverage = line["Coverage"]!.AsArray();
            var covered = coverage.Select(e => e!["Model"]!.GetValue<string>()).ToHashSet();
            Check(covered.Count == coverage.Count, "No duplicate model requirements per line.");
            Check(common.All(covered.Contains), "Cleaning, product, quality, coding, packing and drive functions covered.");
            foreach (var model in new[] { "Preform hopper", "Preform elevator", "Cap sorter and chute", "Bottle labeler" })
                Check(covered.Contains(model) == !can, "PET-only feed/label references must not be added as PET functions to Canline.");
            foreach (var model in new[] { "Syrup mixing skid", "Beverage carbonator" })
                Check(covered.Contains(model) == (!water && !can), "Only confirmed CSD lines receive carbonation/blending provisions.");
            foreach (var entry in coverage)
            {
                var model = entry!["Model"]!.GetValue<string>();
                var addedId = entry["AddedId"]!.GetValue<string>();
                var existingIds = entry["ExistingIds"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
                if (addedId != "")
                {
                    Check(added.Contains(addedId) && referencedAdditions.Add(addedId) &&
                        current[addedId]["Model"]!.GetValue<string>() == model, "New inventory belongs to exactly one line/function.");
                    Check(!existing.Any(i => originals[i]["Model"]!.GetValue<string>() == model),
                        "Do not duplicate a retained line model: " + id + " / " + model);
                    Check(existingIds.Length == 0, "Added function is not simultaneously claimed existing.");
                }
                else
                    Check(existingIds.Length > 0 && existingIds.All(i => existing.Contains(i) &&
                        originals[i]["Model"]!.GetValue<string>() == model), "Existing coverage must refer to actual line equipment.");
                if (model is "Case packer" or "Shrink wrapper")
                    Check(entry["Requirement"]!.GetValue<string>().StartsWith("OPTION:"), "Alternative packaging must not be presented as mandatory.");
            }
        }
        var ctaId = revision["CTAId"]!.GetValue<string>();
        var cta = current[ctaId];
        Check(added.Contains(ctaId) && referencedAdditions.Add(ctaId) &&
            cta["Model"]!.GetValue<string>() == "Air handling unit" && cta["Category"]!.GetValue<string>() == "HVAC" &&
            cta["Name"]!.GetValue<string>().Contains("CTA") && cta["Source"]!.GetValue<string>().Contains("not a chiller"),
            "CTA is an unplaced HVAC air-handling reference, not a refrigeration chiller.");
        Check(referencedAdditions.SetEquals(added), "Every added planning item has documented purpose.");
        Check(!added.Any(i => new[] { "Plate pasteurizer", "Cap sterilizer", "HEPA filler canopy", "Inline metal detector",
                    "Inline checkweigher", "Rotary bottle filler", "Bottle capper", "Bottle rinser" }
                .Contains(current[i]["Model"]!.GetValue<string>())),
            "Conditional treatment/inspection and integrated filling-block machines must not be invented.");
        var path = Path.Combine(prepared, "validation-roundtrip.json");
        try
        {
            Call("FactoryMapLayoutStore", "SaveAssets", assets, path);
            var empty = Activator.CreateInstance(typeof(List<>).MakeGenericType(Type("FactoryMapAsset")))!;
            var loaded = (IList)Call("FactoryMapLayoutStore", "LoadAssets", empty, path);
            Check(loaded.Count == assets.Count && loaded.Cast<object>().Count(a => !Get<bool>(a, "IsPlaced")) ==
                assets.Cast<object>().Count(a => !Get<bool>(a, "IsPlaced")), "Unplaced inventory survives application persistence.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        }
        Console.WriteLine($"Line inventory passed: {added.Count} unplaced references, seven correct manufacturer/product plans, CTA HVAC, " +
                          "no duplicate retained functions, no conditional-treatment assumptions, originals/buildings/registry unchanged, persistence verified.");
    }

    private static void Render(string prepared, IList assets, IList parts)
    {
        var canvasType = Type("FactoryMapCanvas");
        using var canvas = (Control)Activator.CreateInstance(canvasType, assets)!;
        canvasType.GetMethod("SetBuildingParts")!.Invoke(canvas, [parts]);
        canvas.Size = new Size(1800, 1100);
        canvas.CreateControl();
        canvasType.GetProperty("EditMode")!.SetValue(canvas, true);
        foreach (var level in new[] { "Ground", "Mezzanine", "Upper mezzanine", "Basement" })
        {
            canvasType.GetMethod("SetLevel")!.Invoke(canvas, [level]);
            canvasType.GetMethod("SetTopView")!.Invoke(canvas, null);
            Application.DoEvents();
            using var image = new Bitmap(canvas.Width, canvas.Height);
            canvas.DrawToBitmap(image, canvas.ClientRectangle);
            image.Save(Path.Combine(prepared, $"Completed-{level.Replace(' ', '-')}.png"));
        }
        canvasType.GetMethod("SetLevel")!.Invoke(canvas, ["Ground"]);
        canvasType.GetMethod("SetIsometricView")!.Invoke(canvas, null);
        Application.DoEvents();
        using var iso = new Bitmap(canvas.Width, canvas.Height);
        canvas.DrawToBitmap(iso, canvas.ClientRectangle);
        iso.Save(Path.Combine(prepared, "Completed-Isometric.png"));
    }
}
