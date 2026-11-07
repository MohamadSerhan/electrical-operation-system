"""Stage a room-equipment correction and PDF-manifest comparison, never live edits.

Run with Python 3, --correct-room-layout and --completion-baseline as well as
--app, --pdf and a new --output directory. The old bulk generator is disabled.
The supplied PDF review manifest is the transcription baseline, not an as-built survey.
"""

import argparse
import copy
import hashlib
import html
import json
import math
import pathlib
import random
import re
import shutil
from collections import Counter


ROOT = pathlib.Path(__file__).resolve().parents[1]
SCALE = (1365, 766)
LEVELS = ("Ground", "Mezzanine", "Upper mezzanine", "Basement")
CLEARANCE = 2.4  # 0.3 m each side; not a certified maintenance/egress clearance.
LINES = (
    ("PET1", "Sidel", "REF-PET1-F1", "LINE-PET1", 760),
    ("PET2", "Sidel", "REF-PET2-F1", "LINE-PET2", 700),
    ("PET3", "Sidel", "REF-PET3-F1", "REF-PET3-FILLER", 505),
    ("PET4", "Sidel", "REF-PET4-F1", "LINE-PET4", 455),
    ("CAN1", "Sidel", "REF-CAN1-F1", "CANLINE-1", 554),
    ("PET5", "Krones", "DRAW-P5-F1", "LINE-PET5", 660),
    ("PET6", "Krones", "DRAW-P6-F1", "LINE-PET6", 800),
)


def load(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=True) + "\n", encoding="utf-8")


def world(part, x, y):
    angle = math.radians(part["Rotation"])
    return (part["X"] * SCALE[0] + x * math.cos(angle) - y * math.sin(angle),
            part["Y"] * SCALE[1] + x * math.sin(angle) + y * math.cos(angle))


def local(part, x, y):
    dx, dy = x - part["X"] * SCALE[0], y - part["Y"] * SCALE[1]
    angle = math.radians(part["Rotation"])
    return dx * math.cos(angle) + dy * math.sin(angle), -dx * math.sin(angle) + dy * math.cos(angle)


def overlap(a, b, gap=0):
    return abs(a[0] - b[0]) < (a[2] + b[2]) / 2 + gap and abs(a[1] - b[1]) < (a[3] + b[3]) / 2 + gap


def contains(part, box, inset=0):
    for dx in (-box[2] / 2, box[2] / 2):
        for dy in (-box[3] / 2, box[3] / 2):
            x, y = local(part, box[0] + dx, box[1] + dy)
            if abs(x) > part["Length"] / 2 - inset or abs(y) > part["Depth"] / 2 - inset:
                return False
    return True


def catalog():
    source = (ROOT / "UI" / "Views" / "FactoryMapItemForm.vb").read_text(encoding="utf-8-sig")
    categories = {}
    for category, models in re.findall(r'\{"([^"]+)", \{([^}]+)\}\}', source):
        for model in re.findall(r'"([^"]+)"', models):
            categories.setdefault(model, category)
    sizes = {}
    source = (ROOT / "UI" / "Views" / "FactoryMapEquipmentSizes.vb").read_text(encoding="utf-8-sig")
    for model, numbers in re.findall(r'\{"([^"]+)", \(([^)]+)\)\}', source):
        sizes[model] = tuple(float(n.strip().rstrip("Ff")) * 8 for n in numbers.split(","))
    return categories, sizes


class Preparation:
    def __init__(self, app, pdf, output):
        self.app, self.pdf, self.output = app, pdf, output
        self.layout = load(app / "data" / "factory-map-layout.json")
        self.building = load(app / "data" / "factory-map-building.json")
        self.review = load(app / "data" / "factory-map-drawing-review.json")
        self.registry_path = app / "EOEMSData" / "Configuration" / "monitoring-devices.json"
        self.registry = load(self.registry_path)
        self.assets, self.parts = self.layout["Assets"], self.building["Parts"]
        if any(a["Id"].startswith("COMPLETE-") for a in self.assets):
            raise ValueError("This map already contains a completion revision. Do not duplicate its equipment.")
        if pathlib.Path(self.review["Source"]).name != pdf.name:
            raise ValueError("PDF filename does not match the reviewed drawing.")
        self.categories, self.sizes = catalog()
        self.additions, self.structures, self.kits, self.conflicts = [], [], [], []
        self.previous_assets = copy.deepcopy(self.assets)
        self.previous_parts = copy.deepcopy(self.parts)
        self.reserved = [(1040, 730, 200, 21)]
        self.door_boxes = self.door_clearances()

    def box(self, asset):
        if asset["Model"] in ("Conveyor", "Air conveyor", "Beverage line with filler",
                              "PET bottling line", "Can filling line", "Glass bottling line", "Conveyor corner"):
            length = asset["ConveyorLength"]
            width = 24 if asset["Model"] != "Conveyor" else 10
        elif asset["Model"] == "Custom equipment (image)":
            definition = asset["CustomEquipment"]
            length, width = definition["LengthMeters"] * 8, definition["WidthMeters"] * 8
        else:
            length, width, _ = self.sizes[asset["Model"].replace(" with control and power panels", "")]
            if asset["Model"].endswith(" with control and power panels"):
                length += 32
        length *= asset["ScaleX"]
        width *= asset["ScaleY"]
        a = math.radians(asset["Rotation"])
        return (asset["X"] * 1365, asset["Y"] * 766,
                abs(length * math.cos(a)) + abs(width * math.sin(a)),
                abs(length * math.sin(a)) + abs(width * math.cos(a)))

    def door_clearances(self):
        walls = {p["Id"]: p for p in self.parts if p["Kind"] == "Wall"}
        boxes = []
        for door in self.parts:
            if door["Kind"] not in ("Door", "Gate"):
                continue
            wall = walls.get(door["ParentWallId"])
            x, y = world(wall, door["Offset"], 0) if wall else (door["X"] * 1365, door["Y"] * 766)
            boxes.append((door["Level"], (x, y, max(door["Length"] + 4, 24), max(door["Length"] + 4, 24))))
        return boxes

    def add_asset(self, code, name, model, x, y, room, rotation=0, height=0, length=60):
        asset = copy.deepcopy(self.previous_assets[0])
        asset.update(Id="COMPLETE-" + code, Name=name, Category=self.categories[model], Model=model,
                     Location=f'{room["Level"]} / {room["RoomName"]} / PROVISIONAL reference layout; verify on site',
                     Source="Reference equipment only; no verified acquisition source or installed model",
                     X=x / 1365, Y=y / 766, IsPlaced=True, EquipmentId="", MeterSourceId="",
                     ConveyorLength=length, Rotation=rotation % 360, ScaleX=1, ScaleY=1, ScaleZ=1,
                     BodyColor="", IsOpen=False, OpeningDirection="Left inward", MonitoringDeviceId="",
                     ParentUpsId="", CameraIpAddress="", RoomId=room["RoomId"], BaseHeightMeters=height,
                     Level=room["Level"])
        asset.pop("CustomEquipment", None)
        if len(name) > 120:
            raise ValueError("Reference item name exceeds map limit.")
        self.assets.append(asset)
        self.additions.append(asset)
        return asset

    def free(self, box, level, floor):
        if not contains(floor, box, inset=3):
            return False
        obstacles = [self.box(a) for a in self.assets if a["IsPlaced"] and a["Level"] == level
                     and a["Category"] != "Fire System"]
        obstacles += [b for lev, b in self.door_boxes if lev == level]
        if level == "Ground":
            obstacles += self.reserved
            obstacles += [(p["X"] * 1365, p["Y"] * 766, p["Length"] + 6, p["Depth"] + 6)
                          for p in self.parts if p["Kind"] == "Floor" and p["Level"] == level
                          and p["RoomId"] != floor["RoomId"] and p["Length"] < 200
                          and not p["RoomId"].startswith("REF-ROOM-")]
        return not any(overlap(box, b, CLEARANCE * 2) for b in obstacles)

    def place_in_room(self, line, brand, floor, items, fallback=None):
        placed = []
        for number, (role, model) in enumerate(sorted(items, key=lambda item: -math.prod(self.sizes[item[1]][:2])), 1):
            length, width, _ = self.sizes[model]
            found = None
            for rotation in (0, 90):
                w, d = (length, width) if rotation == 0 else (width, length)
                corners = [world(floor, x, y) for x in (-floor["Length"] / 2, floor["Length"] / 2)
                           for y in (-floor["Depth"] / 2, floor["Depth"] / 2)]
                xmin, xmax = min(p[0] for p in corners), max(p[0] for p in corners)
                ymin, ymax = min(p[1] for p in corners), max(p[1] for p in corners)
                y = ymin + d / 2 + 3
                while y <= ymax - d / 2 - 3 and found is None:
                    x = xmin + w / 2 + 3
                    while x <= xmax - w / 2 - 3:
                        if self.free((x, y, w, d), floor["Level"], floor):
                            found = x, y, rotation
                            break
                        x += 2
                    y += 2
                if found:
                    break
            if found is None:
                if fallback is not None:
                    placed.extend(self.place_in_room(f"{line}-AUX-{number:02}", brand, fallback, [(role, model)]))
                    continue
                raise ValueError(f"{line}: cannot fit {role} at nominal size with clearances. No shrinking or silent omission.")
            x, y, rotation = found
            asset = self.add_asset(f"{line}-{number:02}", f"{line} {brand} REF / {role}", model, x, y, floor, rotation)
            placed.append(asset["Id"])
        return placed

    def filler_kits(self):
        common = [
            ("Product buffer", "Product buffer tank"), ("Product pump", "Sanitary product pump"),
            ("Valve manifold", "Sanitary valve manifold"), ("CIP supply", "CIP station - 1 tank"),
            ("CIP return", "CIP return skid"), ("Foam cleaning", "Foam cleaning station"),
            ("Filler operator panel", "Filler control panel"), ("PLC and I/O cabinet", "PLC cabinet"),
            ("Line HMI", "HMI console"), ("MCC power and starters", "Motor control center"),
            ("Drive control", "Variable frequency drive"),
        ]
        pet = [
            ("Preform feed hopper", "Preform hopper"), ("Preform elevator", "Preform elevator"),
            ("Blow moulding", "PET bottle blower"), ("Cap elevator", "Cap elevator"),
            ("Cap sorter", "Cap sorter and chute"), ("Cap treatment", "Cap sterilizer"),
        ]
        can = [("Empty-can depalletizer", "Empty container depalletizer"), ("Can seamer", "Can seamer")]
        for line, brand, floor_id, core_id, packing_y in LINES:
            floor = next(p for p in self.parts if p["Id"] == floor_id)
            core = next(a for a in self.assets if a["Id"] == core_id)
            if core["RoomId"] != floor["RoomId"]:
                raise ValueError(f"{line}: existing filler is not assigned to its expected room.")
            auxiliary = None
            if line == "PET6":
                auxiliary = copy.deepcopy(next(p for p in self.parts if p["Id"] == "DRAW-EXT-F1"))
                auxiliary.update(X=1165 / 1365, Y=357 / 766, Length=100, Depth=90, Rotation=0)
            placed = self.place_in_room(line, brand, floor, common + (can if line == "CAN1" else pet), auxiliary)
            auxiliary_ids = [a["Id"] for a in self.additions if a["Id"] in placed and a["RoomId"] != floor["RoomId"]]
            self.kits.append(dict(Line=line, Manufacturer=brand, ManufacturerEvidence="User confirmed",
                                  RoomId=floor["RoomId"], CoreId=core_id, AddedIds=placed,
                                  CoreScope="Existing schematic filler retained, including its identity and links. "
                                            "Rinser/filler/capper or can filler are represented by this retained assembly, "
                                            "not claimed as verified installed submachines.",
                                  AuxiliaryIds=auxiliary_ids,
                                  AuxiliaryStatus="Adjacent north-hall bay used where retained conveyors leave insufficient room. "
                                                  "No equipment shrunk or existing item moved." if auxiliary_ids else "",
                                  PackingY=packing_y))

    def packing_lanes(self):
        floor = next(p for p in self.parts if p["Id"] == "DRAW-PROD-F1")
        stages = [
            ("Dryer", "Bottle air knife dryer"), ("Inspection", "Bottle inspection station"),
            ("Reject", "Bottle reject station"), ("Checkweigher", "Inline checkweigher"),
            ("Coding", "Date coding station"), ("Labelling", "Bottle labeler"),
            ("Accumulation", "Bottle accumulation table"), ("Shrink packing", "Shrink wrapper"),
            ("Case packing", "Case packer"), ("Palletizing", "Palletizer"),
            ("Pallet wrapping", "Pallet stretch wrapper"),
        ]
        for kit in self.kits:
            x, y = 800, kit["PackingY"]
            previous_edge = None
            ids = []
            for index, (role, model) in enumerate(stages, 1):
                width, depth, _ = self.sizes[model]
                center = x + width / 2
                box = center, y, width, depth
                if not self.free(box, "Ground", floor):
                    raise ValueError(f'{kit["Line"]}: blocked downstream {role} at {center}, {y}.')
                ids.append(self.add_asset(f'{kit["Line"]}-PACK-{index:02}',
                           f'{kit["Line"]} {kit["Manufacturer"]} REF / {role}',
                           model, center, y, floor)["Id"])
                if previous_edge is not None:
                    gap = x - previous_edge
                    # The stock conveyor has a 20-unit minimum. Scale only its length for a shorter gap.
                    conveyor = self.add_asset(f'{kit["Line"]}-BELT-{index:02}',
                                              f'{kit["Line"]} REF / transfer belt {index - 1}', "Conveyor",
                                              (previous_edge + x) / 2, y, floor, length=20)
                    conveyor["ScaleX"] = gap / 20
                    ids.append(conveyor["Id"])
                previous_edge = x + width
                x += width + 10
            kit["PackingIds"] = ids
            kit["TransportStatus"] = ("Local downstream transfer belts connect adjacent reference stages. "
                                      "Filler-to-packing routes, wall penetrations, guarding, "
                                      "utilities and emergency circuits require measured design; not fabricated.")

    def add_part(self, code, name, kind, x, y, length, depth, height, base=0, rotation=0):
        part = copy.deepcopy(self.previous_parts[0])
        part.update(Id="COMPLETE-" + code, Name=name, Kind=kind,
                    Style={"Floor": "Rectangle", "Wall": "Solid", "Stairs": "Straight flight"}[kind],
                    Material="Steel" if kind != "Floor" else "Concrete", BodyColor="#8FA6B3",
                    X=x / 1365, Y=y / 766, Length=length, Depth=depth, HeightMeters=height,
                    BaseMeters=base, Level="Ground", Rotation=rotation, RoomId="", RoomName="",
                    ParentWallId="", Offset=0, MonitoringDeviceId="", TemperatureSourceId="",
                    TemperatureSourceConfirmed=False, FirePanelId="", FireSiteId="", FireZone="")
        self.parts.append(part)
        self.structures.append(part)
        return part

    def bridge(self):
        self.add_part("BRIDGE-DECK", "PROVISIONAL service bridge / location and elevation to confirm",
                      "Floor", 1040, 730, 64, 12.8, .15, base=4.1)
        for side in (-1, 1):
            self.add_part(f"BRIDGE-RAIL-{side}", "PROVISIONAL service bridge / guardrail envelope",
                          "Wall", 1040, 730 + side * 6.4, 64, .5, 1.1, base=4.25)
        self.add_part("BRIDGE-WEST", "PROVISIONAL service bridge / west stair",
                      "Stairs", 984, 730, 48, 12.8, 4.25)
        self.add_part("BRIDGE-EAST", "PROVISIONAL service bridge / east stair",
                      "Stairs", 1096, 730, 48, 12.8, 4.25, rotation=180)

    def offices(self):
        floor_ids = ["DRAW-OF3-F1", "DRAW-OF4-F1", "DRAW-PR-F1", "DRAW-REST-F1"] + [
            f"DRAW-MO{i}-F1" for i in range(1, 5)]
        for floor_id in floor_ids:
            floor = next(p for p in self.parts if p["Id"] == floor_id)
            obstructions = [a["Id"] for a in self.previous_assets if a["IsPlaced"] and a["Level"] == floor["Level"]
                            and a["Category"] != "Fire System" and
                            overlap(self.box(a), (floor["X"] * 1365, floor["Y"] * 766, floor["Length"], floor["Depth"]))]
            if obstructions:
                self.conflicts.append(f'{floor["RoomName"]}: existing equipment {", ".join(obstructions)} overlaps '
                                      'the office footprint. Furniture deferred rather than moving the user item.')
                continue
            self.place_in_room(floor_id.removesuffix("-F1"), "Office", floor,
                               [("Workstation", "Office desk"), ("Chair", "Office chair"),
                                ("Storage", "Office cabinet")])

    def audit(self):
        assets = {a["Id"]: a for a in self.assets}
        devices = {d["Id"]: d for d in self.registry["Devices"]}
        symbols = self.review["Symbols"]
        if len(symbols) != 199 or len({s["id"] for s in symbols}) != 199:
            raise ValueError("The PDF transcription must contain 199 unique symbol identities.")
        transforms = {}
        rows = []
        for page in range(1, 5):
            selected = [s for s in symbols if s["page"] == page]
            if any(s["id"] not in assets or s["id"] not in devices for s in selected):
                raise ValueError(f"Page {page} has missing map/registry symbols.")
            transform, anchors = fit_registration(selected, assets)
            transforms[str(page)] = dict(Coefficients=transform, UnchangedAnchors=len(anchors),
                                         Evidence="Affine registration recovered from unchanged saved PDF symbols; not an independent survey.")
            for s in selected:
                a, d = assets[s["id"]], devices[s["id"]]
                ex, ey = project(transform, s["x"], s["y"])
                distance = math.hypot(a["X"] * 1365 - ex, a["Y"] * 766 - ey) / 8
                same_model = a["Model"] == s["model"] == d["Model"]
                same_tag = a["Name"] == s["tag"] == d["DrawingTag"]
                same_level = a["Level"] == LEVELS[page - 1]
                uncommissioned = not d.get("Commissioned", False) and not any(
                    d.get(k, "") for k in ("PanelId", "SiteId", "Loop", "Address", "EventAddress"))
                rows.append(dict(Id=s["id"], Tag=s["tag"], Page=page, Level=a["Level"],
                                 Model=s["model"], PDFx=s["x"], PDFy=s["y"],
                                 ExpectedX=ex, ExpectedY=ey, CurrentX=a["X"] * 1365, CurrentY=a["Y"] * 766,
                                 DifferenceMeters=round(distance, 4), PositionMatches=distance <= .25,
                                 ModelMatches=same_model, TagMatches=same_tag, LevelMatches=same_level,
                                 MonitoringLinkMatches=a["MonitoringDeviceId"] == d["Id"],
                                 Uncommissioned=uncommissioned,
                                 Status="Within drawing-registration tolerance" if distance <= .25 else
                                        "UNRESOLVED: retained reference-picture displacement"))
        fire_ids = {a["Id"] for a in self.assets if a["Category"] == "Fire System" and a["Id"].startswith("DRAW-")}
        if fire_ids != {s["id"] for s in symbols}:
            raise ValueError("Extra or missing drawing fire identities.")
        if not all(r[k] for r in rows for k in ("ModelMatches", "TagMatches", "LevelMatches",
                                               "MonitoringLinkMatches", "Uncommissioned")):
            raise ValueError("A fire identity, level, tag, model, link or commissioning discrepancy requires explicit resolution.")
        return dict(SourcePDF=self.pdf.name, SourcePDFSHA256=digest(self.pdf), Scope="PDF-review-manifest comparison only",
                    PositionToleranceMeters=.25, FirePositionsChanged=False, CommissioningChanged=False,
                    Decision="User requested keeping current fire positions and a mismatch report only.",
                    Registration=transforms, Count=len(rows), CountsByModel=dict(Counter(r["Model"] for r in rows)),
                    PositionMatches=sum(r["PositionMatches"] for r in rows),
                    PositionMismatches=sum(not r["PositionMatches"] for r in rows),
                    Discrepancies=self.review["Discrepancies"], Symbols=rows)

    def run(self):
        self.output.mkdir(parents=True, exist_ok=False)
        baseline = self.output / "baseline"
        baseline.mkdir()
        inputs = {}
        for name in ("factory-map-layout.json", "factory-map-building.json", "factory-map-drawing-review.json"):
            path = self.app / "data" / name
            shutil.copy2(path, baseline / name)
            inputs[str(path.relative_to(self.app))] = digest(path)
        shutil.copy2(self.registry_path, baseline / "monitoring-devices.json")
        inputs[str(self.registry_path.relative_to(self.app))] = digest(self.registry_path)
        self.filler_kits()
        self.packing_lanes()
        self.offices()
        self.bridge()
        audit = self.audit()
        revision = dict(Provisional=True, Authority="User-confirmed manufacturer assignments; generic reference equipment, not OEM CAD.",
                        InputsSHA256=inputs, AddedAssets=len(self.additions), AddedBuildingParts=len(self.structures),
                        Kits=self.kits, AddedBuildingIds=[p["Id"] for p in self.structures],
                        UnresolvedConflicts=self.conflicts,
                        FloorCoverage={lev: [p["Id"] for p in self.parts if p["Kind"] == "Floor" and p["Level"] == lev]
                                       for lev in LEVELS},
                        BridgeStatus="Proposed location only, NOT located or certified by the fire PDF. "
                                     "4.1 m is a drawing conveyor-height annotation, NOT a verified bridge elevation. "
                                     "No upper-floor stair routes or slab openings inferred.",
                        Limitations=["Office floors already represented; added furniture, not invented office buildings.",
                                     "No certified egress, access, hygienic zoning, utility routing or OEM service clearances.",
                                     "CSD carbonation, pasteurization and aseptic treatments depend on actual products; "
                                     "not universally installed in every filler room.",
                                     "All original equipment, building parts and fire positions retained unchanged."],
                        ManufacturerReferences=[
                            "https://www.krones.com/en/products/filling-and-packing-technology/block-synchronised-systems/ergobloc-l-wet-section-block/",
                            "https://www.sidel.com/en/equipment/blowing/"])
        self.review["FactoryCompletionRevision"] = revision
        for name, value in (("factory-map-layout.json", self.layout), ("factory-map-building.json", self.building),
                            ("factory-map-drawing-review.json", self.review), ("factory-fire-position-audit.json", audit)):
            write(self.output / name, value)
        shutil.copy2(self.registry_path, self.output / "monitoring-devices.json")
        self.report(audit, revision)
        print(f"Staged {len(self.additions)} equipment items, {len(self.structures)} bridge parts.")
        print(f'Fire comparison: {audit["PositionMatches"]} within tolerance; {audit["PositionMismatches"]} displaced; no fire items moved.')
        print(self.output)

    def report(self, audit, revision):
        escape = html.escape
        contents = ["""<!doctype html><html lang="en"><meta charset="utf-8">
<title>Factory completion and fire-position review</title>
<style>body{font:15px system-ui;margin:28px;background:#101b25;color:#e4edf4}
h1,h2{color:#8fdbff}table{border-collapse:collapse;width:100%;margin:16px 0}
td,th{border:1px solid #52616e;padding:7px;text-align:left}th{background:#243a49}
.warning{background:#624817;padding:16px;border-left:5px solid #ffc45b}
svg{background:#182733;width:100%;border:1px solid #52616e}a{color:#8fdbff}
summary{cursor:pointer;padding:10px}p{line-height:1.55}.bad{color:#ffc45b}.good{color:#93e5b7}
@media print{body{background:white;color:black}svg{background:#ddd}a{color:navy}}</style>
<h1>Factory completion and fire-position review</h1>
<p class="warning">PROVISIONAL REFERENCE LAYOUT — not an OEM installation drawing, as-built survey,
commissioning certificate, or fire-safety approval. Existing equipment, links, walls and all fire positions are unchanged.
Hover symbols in the plans for item names. Orange equipment is new reference equipment; blue is existing.
Red fire circles are retained displaced positions; green are within comparison tolerance.
White rings show the PDF-registered comparison positions.</p>"""]
        contents += [f'<p>Drawing: {escape(audit["SourcePDF"])}<br>SHA256: {audit["SourcePDFSHA256"]}</p>',
                     f'<p>Added <b>{len(self.additions)}</b> equipment/furniture/belt items and '
                     f'<b>{len(self.structures)}</b> proposed bridge parts. All four existing levels retained.</p>',
                     '<h2>Filler-room coverage</h2><table><tr><th>Line</th><th>Manufacturer</th><th>Room additions</th><th>Downstream</th></tr>']
        for kit in self.kits:
            contents.append(f'<tr><td>{kit["Line"]}</td><td>{kit["Manufacturer"]} — user confirmed</td>'
                            f'<td>{len(kit["AddedIds"])} total, {len(kit["AuxiliaryIds"])} in adjacent bay; retained core {kit["CoreId"]}</td>'
                            f'<td>{len(kit["PackingIds"])} stations/belts</td></tr>')
        contents.append('</table><p>PET: preform feed/elevation, blowing, cap handling/treatment; all rooms: buffer, '
                        'sanitary product handling, CIP supply/return, cleaning, filler panel, PLC/I/O, HMI, MCC and drive. '
                        'Canline uses can depalletizing and seaming rather than PET preforms and caps. The retained '
                        'existing filler assembly represents the filling core. Machine variants, sterile treatment and '
                        'carbonation requirements still require the installed equipment/product list.</p>'
                        '<p>Downstream: drying, inspection, rejection, weighing, coding, labelling, accumulation, '
                        'shrink/case packing, palletizing and wrapping. Transfer belts connect these stations locally. '
                        '<b>Room-to-packing transport routes are unresolved</b>; no conveyors through walls or offices were invented.</p>'
                        '<h2>Buildings and floors</h2><p>All existing room floors and office enclosures preserved. '
                        'Furniture added where unobstructed; conflicts are explicitly listed below. '
                        'Upper mezzanine and basement floors remain unchanged, not filled in beyond the evidence.</p>'
                        f'<p class="warning">{escape(revision["BridgeStatus"])}</p>')
        if self.conflicts:
            contents.append('<h3>Unresolved existing-layout conflicts</h3><ul>' +
                            ''.join(f'<li>{escape(text)}</li>' for text in self.conflicts) + '</ul>')
        for level in LEVELS:
            contents.append(f'<h2>{escape(level)}</h2><svg viewBox="0 0 1570 980" role="img" aria-label="{escape(level)} layout">')
            for p in self.parts:
                if p["Level"] != level or p["Kind"] not in ("Floor", "Wall", "Door", "Stairs"):
                    continue
                color = "#556570" if p["Kind"] == "Wall" else "#293d4b"
                if p["Id"].startswith("COMPLETE-"):
                    color = "#ad7026"
                contents.append(f'<g transform="translate({p["X"] * 1365:.2f} {p["Y"] * 766:.2f}) rotate({p["Rotation"]:.2f})">'
                                f'<title>{escape(p["Name"])} / {p["Id"]}</title>'
                                f'<rect x="{-p["Length"]/2:.2f}" y="{-p["Depth"]/2:.2f}" width="{p["Length"]:.2f}" '
                                f'height="{p["Depth"]:.2f}" fill="{color}" stroke="#72818c" stroke-width=".6"/></g>')
            for a in self.assets:
                if a["Level"] != level or not a["IsPlaced"] or a["Category"] == "Fire System":
                    continue
                x, y, w, d = self.box(a)
                color = "#efab49" if a["Id"].startswith("COMPLETE-") else "#6dc9e9"
                contents.append(f'<rect x="{x-w/2:.2f}" y="{y-d/2:.2f}" width="{w:.2f}" height="{d:.2f}" '
                                f'fill="{color}" fill-opacity=".72" stroke="{color}" stroke-width=".5">'
                                f'<title>{escape(a["Name"])} / {escape(a["Model"])} / {a["Id"]}</title></rect>')
            for row in audit["Symbols"]:
                if row["Level"] != level:
                    continue
                color = "#8ae3af" if row["PositionMatches"] else "#ff6d68"
                contents.append(f'<circle cx="{row["ExpectedX"]:.2f}" cy="{row["ExpectedY"]:.2f}" r="3.5" '
                                'fill="none" stroke="white" stroke-width="1"/>'
                                f'<circle cx="{row["CurrentX"]:.2f}" cy="{row["CurrentY"]:.2f}" r="2.5" fill="{color}">'
                                f'<title>{escape(row["Tag"])} / {row["Id"]} / delta {row["DifferenceMeters"]:.2f} m</title></circle>')
            contents.append('</svg>')
        contents.append(f'<h2>Fire-position comparison</h2><p>All {audit["Count"]} manifest symbols are present with matching tags, '
                        f'models, floors and registry links. <b>{audit["PositionMatches"]}</b> centers are within the '
                        f'{audit["PositionToleranceMeters"]} m drawing-registration tolerance; '
                        f'<b class="bad">{audit["PositionMismatches"]} are displaced</b>. All remain uncommissioned. '
                        'This comparison checks the existing PDF transcription and recovered registration, not an independent '
                        'recount of every vector symbol or a physical-site inspection. Duplicate tags are not collapsed.</p>'
                        '<details><summary>Original drawing discrepancies (unresolved)</summary><pre>'
                        + escape(json.dumps(audit["Discrepancies"], indent=2)) + '</pre></details>'
                        '<details open><summary>All 199 fire-symbol results</summary><table>'
                        '<tr><th>ID / tag</th><th>Sheet</th><th>Model</th><th>Difference (m)</th><th>Result</th></tr>')
        for row in audit["Symbols"]:
            contents.append(f'<tr><td>{row["Id"]}<br>{escape(row["Tag"])}</td><td>{row["Page"]}</td>'
                            f'<td>{escape(row["Model"])}</td><td>{row["DifferenceMeters"]:.3f}</td>'
                            f'<td class="{"good" if row["PositionMatches"] else "bad"}">{row["Status"]}</td></tr>')
        contents.append('</table></details><h2>Next verification required</h2><ul>' +
                        ''.join(f'<li>{escape(text)}</li>' for text in revision["Limitations"]) +
                        '<li>Confirm bridge position, loads, guarding, stair geometry, slab openings and all inter-floor routes '
                        'from measured architectural/structural drawings.</li>'
                        '<li>Verify actual installed fire devices, mounting heights, wall/door access, loops and addresses on site '
                        'with the responsible fire-system engineer before commissioning.</li></ul>'
                        '<h2>Manufacturer background</h2><p>These references establish process families only, not site equipment:</p><ul>' +
                        ''.join(f'<li><a href="{escape(url)}">{escape(url)}</a></li>' for url in revision["ManufacturerReferences"]) +
                        '</ul></html>')
        (self.output / "Factory-completion-review.html").write_text("\n".join(contents), encoding="utf-8")


def solve(matrix, values):
    rows = [list(row) + [value] for row, value in zip(matrix, values)]
    for i in range(3):
        pivot = max(range(i, 3), key=lambda k: abs(rows[k][i]))
        rows[i], rows[pivot] = rows[pivot], rows[i]
        divisor = rows[i][i]
        if abs(divisor) < 1e-10:
            raise ValueError("Collinear registration anchors.")
        rows[i] = [v / divisor for v in rows[i]]
        for k in range(3):
            if k != i:
                factor = rows[k][i]
                rows[k] = [a - factor * b for a, b in zip(rows[k], rows[i])]
    return [r[3] for r in rows]


def project(transform, x, y):
    return tuple(c[0] * x + c[1] * y + c[2] for c in transform)


def fit_registration(symbols, assets):
    points = [[s["x"], s["y"], 1] for s in symbols]
    targets = [(assets[s["id"]]["X"] * 1365, assets[s["id"]]["Y"] * 766) for s in symbols]
    rng, best = random.Random(42), []
    for _ in range(1000):
        sample = rng.sample(range(len(points)), 3)
        try:
            transform = [solve([points[i] for i in sample], [targets[i][axis] for i in sample]) for axis in (0, 1)]
        except ValueError:
            continue
        good = [i for i, p in enumerate(points) if math.dist(project(transform, p[0], p[1]), targets[i]) < .15]
        if len(good) > len(best):
            best = good
    if len(best) < 6:
        raise ValueError("Insufficient unchanged anchors for reliable sheet registration.")
    normal = [[sum(points[i][j] * points[i][k] for i in best) for k in range(3)] for j in range(3)]
    transform = [solve(normal, [sum(points[i][j] * targets[i][axis] for i in best) for j in range(3)])
                 for axis in (0, 1)]
    a, b = transform
    if abs(a[0] - b[1]) > .001 or abs(a[1] + b[0]) > .001 or a[0] >= 0:
        raise ValueError("Recovered registration is not the expected deskewed 180-degree similarity.")
    return transform, best


class LineInventory(Preparation):
    """Add missing planning items without placing or duplicating existing equipment."""

    def __init__(self, app, pdf, output):
        self.app, self.pdf, self.output = app, pdf, output
        self.layout = load(app / "data" / "factory-map-layout.json")
        self.building = load(app / "data" / "factory-map-building.json")
        self.review = load(app / "data" / "factory-map-drawing-review.json")
        self.registry_path = app / "EOEMSData" / "Configuration" / "monitoring-devices.json"
        self.registry = load(self.registry_path)
        self.assets, self.parts = self.layout["Assets"], self.building["Parts"]
        self.categories, self.sizes = catalog()
        self.previous_assets = copy.deepcopy(self.assets)
        self.additions = []
        self.plan = []

    def run(self):
        if self.review.get("LineInventoryRevision"):
            raise ValueError("A line inventory plan already exists; reconcile it instead of duplicating equipment.")
        if pathlib.Path(self.review["Source"]).name != self.pdf.name:
            raise ValueError("Wrong source drawing.")
        correction = self.review["EquipmentCorrectionRevision"]
        baseline_ids = {a["Id"] for a in self.assets}
        requirements = [
            ("Product buffer tank", "Process", "Reference provision; final capacity and shared/dedicated arrangement to confirm"),
            ("Sanitary product pump", "Process", "Reference provision; product duty and hygienic specification to confirm"),
            ("Sanitary valve manifold", "Process", "Reference provision; hygienic routing and valve matrix to confirm"),
            ("CIP station - 1 tank", "Cleaning", "Planning envelope only; tank count, chemistry, capacity and shared supply not confirmed"),
            ("CIP return skid", "Cleaning", "Return duty and connection to shared or dedicated CIP to confirm"),
            ("Foam cleaning station", "Cleaning", "Cleaning-method compatibility and hygienic zoning to confirm"),
            ("Bottle inspection station", "Quality", "Reference inspection function; sensor/reject criteria and installed station to confirm"),
            ("Bottle reject station", "Quality", "Reference reject function; line controls and reject verification to confirm"),
            ("Date coding station", "Coding", "Code content, method and substrate to confirm"),
            ("Bottle accumulation table", "Conveying", "Accumulation function; type and capacity depend on line rate"),
            ("Shrink wrapper", "Packing option", "OPTION: select pack format; not universally required together with a case packer"),
            ("Case packer", "Packing option", "OPTION: select pack format; not universally required together with a shrink wrapper"),
            ("Palletizer", "End of line", "Palletizing function; manual, shared or automatic solution to confirm"),
            ("Pallet stretch wrapper", "End of line", "Load stabilization function; shared/automatic arrangement to confirm"),
            ("Variable frequency drive", "Controls", "Drive-control reference; quantity and integration in MCC/OEM cabinet not confirmed"),
        ]
        pet = [
            ("Preform hopper", "PET feed", "Preform feed reference; reuse retained hopper where present"),
            ("Preform elevator", "PET feed", "Preform transport reference; integrated feed may already provide this function"),
            ("Cap sorter and chute", "Cap feed", "Cap sorting/feed reference; integrated system and cap format to confirm"),
            ("Bottle labeler", "Labelling", "Labelling function; retained when already present"),
        ]
        # These are process functions, not a purported installed OEM bill of materials.
        for line, brand, floor_id, core_id, _ in LINES:
            kit = next(k for k in correction["Kits"] if k["Line"] == line)
            floor = next(p for p in self.parts if p["Id"] == floor_id)
            scoped = {core_id, *kit["ReferenceIds"], *kit["ConveyorIds"]}
            existing = [a for a in self.assets if a["Id"] in scoped]
            models = {a["Model"] for a in existing}
            product = "Water" if line in ("PET4", "PET5") else "CSD" if line != "CAN1" else "Canned beverage - product unconfirmed"
            items = list(requirements) + ([] if line == "CAN1" else pet)
            if product == "CSD":
                items += [
                    ("Syrup mixing skid", "CSD process", "CSD blending reference; may be supplied by the existing shared syrup room"),
                    ("Beverage carbonator", "CSD process", "CSD carbonation reference; CO2, chilling, pressure and product specification to confirm"),
                ]
            coverage = []
            for index, (model, stage, note) in enumerate(items, 1):
                matched = [a["Id"] for a in existing if a["Model"] == model]
                if matched:
                    coverage.append(dict(Model=model, Stage=stage, ExistingIds=matched, AddedId="", Requirement=note))
                    continue
                a = self.add_asset(f"PLAN-{line}-{index:02}", f"{line} {brand} PLAN / {model}", model, 0, 0, floor)
                a.update(IsPlaced=False, RoomId="", Location=f"UNPLACED / {line} {brand} / {product} / {stage}",
                         Source="Planning reference, NOT confirmed installed equipment. " + note)
                coverage.append(dict(Model=model, Stage=stage, ExistingIds=[], AddedId=a["Id"], Requirement=note))
                models.add(model)
            self.plan.append(dict(Line=line, Manufacturer=brand, Product=product, CoreId=core_id,
                                  ExistingIds=sorted(scoped), Coverage=coverage,
                                  CoreFunctions="Retained schematic filling block represents rinsing/filling/capping for PET; "
                                                "retained can filler and seamer represent can filling/closing. No duplicate core added.",
                                  Unconfirmed="Actual OEM machine models, quantities, capacities, integrated blocks and installed coordinates."))
        room = next(p for p in self.parts if p["Id"] == "DRAW-PROD-F1")
        cta = self.add_asset("PLAN-CTA-AHU", "CTA air-handling unit / UNPLACED planning reference",
                             "Air handling unit", 0, 0, room)
        cta.update(IsPlaced=False, RoomId="", Location="UNPLACED / HVAC / CTA air-handling unit",
                   Source="CTA means air-handling unit, not a chiller. One reusable planning reference; installed quantity, "
                          "airflow, cooling coil, filters, hygiene class, dimensions and duty are NOT confirmed.")
        if len({a["Id"] for a in self.assets}) != len(self.assets) or baseline_ids.intersection(a["Id"] for a in self.additions):
            raise ValueError("Inventory identities would duplicate existing equipment.")
        self.output.mkdir(parents=True, exist_ok=False)
        baseline = self.output / "baseline"
        baseline.mkdir()
        inputs = {}
        for name in ("factory-map-layout.json", "factory-map-building.json", "factory-map-drawing-review.json"):
            source = self.app / "data" / name
            shutil.copy2(source, baseline / name)
            inputs[str(source.relative_to(self.app))] = digest(source)
        shutil.copy2(self.registry_path, baseline / "monitoring-devices.json")
        inputs[str(self.registry_path.relative_to(self.app))] = digest(self.registry_path)
        revision = dict(InputsSHA256=inputs, AddedIds=[a["Id"] for a in self.additions], PlacedItemsChanged=False,
                        Lines=self.plan, CTAId=cta["Id"],
                        CTAQuantityStatus="One planning reference, not an installed quantity claim.",
                        ConditionalNotAdded=[
                            "Juice/hot-fill/aseptic treatment: product recipe, shelf-life and thermal process must be specified. CSD does not establish a pasteurization requirement.",
                            "Canline carbonation, syrup blending and pasteurization depend on the canned product, which is not yet confirmed.",
                            "Water disinfection (UV/ozone etc.) depends on the water process and validated hygiene design; no treatment technology assumed.",
                            "Cap sterilization, HEPA/aseptic canopy, metal detection and checkweighing are application-specific, not universal line requirements.",
                            "Rinser/filler/capper may be integrated in the retained block. Do not duplicate its constituent machines without OEM confirmation.",
                        ],
                        SharedServices=[
                            "Treated product water and hygienic distribution",
                            "PET high-pressure blowing air, low-pressure instrument air, dryers/filtration and suitable air quality",
                            "Chilled/process cooling water and heat rejection, sized from OEM loads",
                            "Food-grade CO2 supply/regulation for confirmed CSD duties",
                            "Electrical distribution, earthing, MCC/PLC/OEM interfaces and safety-rated emergency/guard circuits",
                            "CIP chemicals, heating, return, drainage and wastewater interfaces",
                            "CTA air handling, ventilation and hygienic pressure zoning where specified",
                        ])
        self.review["LineInventoryRevision"] = revision
        write(self.output / "factory-map-layout.json", self.layout)
        write(self.output / "factory-map-drawing-review.json", self.review)
        # Byte-preserve the building document and registry used by validation.
        shutil.copy2(self.app / "data" / "factory-map-building.json", self.output / "factory-map-building.json")
        shutil.copy2(self.registry_path, self.output / "monitoring-devices.json")
        write(self.output / "line-equipment-plan.json", revision)
        rows = []
        for line in self.plan:
            rows.append(f'<h2>{line["Line"]} / {line["Manufacturer"]} / {html.escape(line["Product"])}</h2>'
                        f'<p>{html.escape(line["CoreFunctions"])}</p><table><tr><th>Function / model</th>'
                        '<th>Stage</th><th>Status</th><th>Qualification</th></tr>')
            for entry in line["Coverage"]:
                status = "Retained existing reference" if entry["ExistingIds"] else "Added UNPLACED planning item"
                rows.append(f'<tr><td>{html.escape(entry["Model"])}</td><td>{entry["Stage"]}</td>'
                            f'<td>{status}</td><td>{html.escape(entry["Requirement"])}</td></tr>')
            rows.append('</table>')
        content = """<!doctype html><html lang="en"><meta charset="utf-8"><title>Line equipment planning inventory</title>
<style>body{font:15px system-ui;max-width:1250px;margin:30px auto;background:#17232d;color:#eef6fa}
td,th{border:1px solid #71818c;padding:8px}table{border-collapse:collapse;width:100%}
.warning{background:#624817;padding:16px}li{margin:8px}</style>
<h1>Sidel / Krones line-equipment planning inventory</h1>
<p class="warning">All additions are UNPLACED planning references, not verified installed machines or an OEM bill of materials.
No existing map position, equipment link, room geometry or fire asset is changed.
Generic model sizes are representative, not selected vendor dimensions. A planned function may be integrated,
shared or provided manually; each reference is not a mandatory additional physical machine.</p>"""
        content += f'<p>Added {len(self.additions)} unplaced references. Existing line functions were checked to avoid duplicating retained models.</p>'
        content += ''.join(rows) + '<h2>CTA air-handling unit</h2><p>' + html.escape(cta["Source"]) + '</p>'
        for title, key in (("Conditional equipment NOT automatically added", "ConditionalNotAdded"),
                           ("Shared utility / safety interfaces requiring engineering", "SharedServices")):
            content += f'<h2>{title}</h2><ul>' + ''.join(f'<li>{html.escape(v)}</li>' for v in revision[key]) + '</ul>'
        content += '<h2>How to place</h2><p>Open Edit Map, select the unplaced PLAN item from the item list, then confirm its dimensions, location and actual equipment identity before placement. Do not create live acquisition links until the equipment is identified and commissioned.</p></html>'
        (self.output / "Line-equipment-plan.html").write_text(content, encoding="utf-8")
        print(f"Added {len(self.additions)} unplaced planning references for seven lines and CTA. Existing assets and buildings unchanged.")


class RoomCorrection(Preparation):
    """Correct the previous generated revision without changing room/fire geometry."""

    def __init__(self, app, pdf, output, completion_baseline, confirm_west_generator_strip=False):
        self.app, self.pdf, self.output = app, pdf, output
        self.layout = load(app / "data" / "factory-map-layout.json")
        self.building = load(app / "data" / "factory-map-building.json")
        self.review = load(app / "data" / "factory-map-drawing-review.json")
        self.registry_path = app / "EOEMSData" / "Configuration" / "monitoring-devices.json"
        self.registry = load(self.registry_path)
        self.assets, self.parts = self.layout["Assets"], self.building["Parts"]
        self.previous_assets, self.previous_parts = copy.deepcopy(self.assets), copy.deepcopy(self.parts)
        self.categories, self.sizes = catalog()
        self.additions, self.structures, self.kits, self.conflicts = [], [], [], []
        self.reserved = []
        self.door_boxes = self.door_clearances()
        self.baseline_assets = {a["Id"]: a for a in load(completion_baseline / "factory-map-layout.json")["Assets"]}
        self.baseline_parts = {p["Id"]: p for p in load(completion_baseline / "factory-map-building.json")["Parts"]}
        self.modified, self.removed = [], []
        self.old_revision = self.review["FactoryCompletionRevision"]
        self.active_by_id = {a["Id"]: a for a in self.assets}
        self.confirm_west_generator_strip = confirm_west_generator_strip
        self.generator_assignments = []

    def owned_unchanged(self, item, originals):
        original = originals.get(item["Id"])
        if original != item:
            raise ValueError(f'{item["Id"]}: user-edited or unrecognized generated item; refusing to discard it.')
        if any(item.get(k) for k in ("EquipmentId", "MeterSourceId", "MonitoringDeviceId", "ParentUpsId", "CameraIpAddress")):
            raise ValueError(f'{item["Id"]}: linked item cannot be removed as an unused reference.')

    def move(self, asset, floor, x, y, rotation=0):
        if asset["Id"] not in self.modified:
            self.modified.append(asset["Id"])
        asset.update(X=x / 1365, Y=y / 766, Rotation=rotation % 360, RoomId=floor["RoomId"],
                     Level=floor["Level"], BaseHeightMeters=0, IsPlaced=True)

    def belt(self, code, floor, start, end, existing=None):
        length = math.dist(start, end)
        if length < 2:
            raise ValueError("A transfer belt must have a real nonzero run.")
        angle = math.degrees(math.atan2(end[1] - start[1], end[0] - start[0])) % 360
        x, y = (start[0] + end[0]) / 2, (start[1] + end[1]) / 2
        if existing:
            asset = existing
            self.move(asset, floor, x, y, angle)
        else:
            asset = self.add_asset(code, code + " / room transfer", "Conveyor", x, y, floor, angle)
        asset.update(ConveyorLength=max(20, length), ScaleX=min(1, length / 20), ScaleY=1, ScaleZ=1)
        return asset

    def select_reference(self, kit, model):
        candidates = [self.active_by_id[i] for i in kit["AddedIds"] + kit["PackingIds"]
                      if i in self.active_by_id and self.active_by_id[i]["Model"] == model]
        if len(candidates) != 1:
            raise ValueError(f'{kit["Line"]}: expected one known reference for {model}.')
        asset = candidates[0]
        self.owned_unchanged(asset, self.baseline_assets)
        return asset

    def correct_rooms(self):
        retained = set()
        for line, brand, floor_id, core_id, _ in LINES:
            kit = next(k for k in self.old_revision["Kits"] if k["Line"] == line)
            floor = next(p for p in self.parts if p["Id"] == floor_id)
            left = floor["X"] * 1365 - floor["Length"] / 2
            cy = floor["Y"] * 766
            top, bottom = cy - floor["Depth"] / 2, cy + floor["Depth"] / 2
            right = left + floor["Length"]
            core = self.active_by_id[core_id]
            compact = line in ("PET5", "PET6")
            core_x, core_y = (left + 45, cy + 3) if compact else (left + 100, cy)
            feed_shift = 24 if line == "PET3" else 0
            core_x += feed_shift
            if line == "PET3":
                core_y -= 30
            self.move(core, floor, core_x, core_y)
            core["Name"] = f'{line} {brand} / retained filling block (schematic)'
            core["Location"] = f'Ground / {floor["RoomName"]} / room-relative schematic; installed coordinates unverified'
            selected = []
            connections = []
            # Keep controls beside the block, not a row of unrelated process skids at the wall.
            for model, x in (("Filler control panel", left + 14), ("PLC cabinet", left + 28),
                             ("Motor control center", left + 49)):
                a = self.select_reference(kit, model)
                self.move(a, floor, x, bottom - 8)
                selected.append(a)
            conveyor_index = {"PET1": 1, "PET2": 2, "CAN1": 3, "PET4": 4, "PET5": 5, "PET6": 6}.get(line)
            cv1 = self.active_by_id.get(f"DRAW-L{conveyor_index}-CV1") if conveyor_index else None
            cv2 = self.active_by_id.get(f"DRAW-L{conveyor_index}-CV2") if conveyor_index else None
            if line == "CAN1":
                feed = self.select_reference(kit, "Empty container depalletizer")
                self.move(feed, floor, left + 25, cy)
                seamer = self.select_reference(kit, "Can seamer")
                self.move(seamer, floor, core_x + 42, cy)
                selected += [feed, seamer]
                a = self.belt(line + "-INFEED", floor, (left + 41, cy), (core_x - 30, cy), cv2)
                b = self.belt(line + "-CLOSURE", floor, (core_x + 30, cy), (core_x + 36, cy))
                c = self.belt(line + "-OUTFEED", floor, (core_x + 48, cy), (right - 20, cy), cv1)
                connections += [a, b, c]
            elif compact:
                # The smaller rooms cannot contain a separately invented full packing line.
                blower = self.select_reference(kit, "PET bottle blower")
                self.move(blower, floor, left + 32, top + 13)
                caps = self.select_reference(kit, "Cap elevator")
                self.move(caps, floor, left + 65, top + 13)
                selected += [blower, caps]
                connections.append(self.belt(line + "-OUTFEED", floor, (core_x + 30, core_y),
                                             (right - 20, core_y), cv1))
                if cv2:
                    self.modified.append(cv2["Id"])
                    cv2["IsPlaced"] = False
                    cv2["Location"] += " / redundant parallel schematic archived; not a verified second route"
            else:
                blower = self.select_reference(kit, "PET bottle blower")
                self.move(blower, floor, left + 31 + feed_shift, core_y)
                hopper = self.select_reference(kit, "Preform hopper")
                self.move(hopper, floor, left + 25, top + 12)
                caps = self.select_reference(kit, "Cap elevator")
                self.move(caps, floor, core_x, top + 12)
                labeler = self.select_reference(kit, "Bottle labeler")
                self.move(labeler, floor, left + 165 + feed_shift, core_y)
                selected += [blower, hopper, caps, labeler]
                connections.append(self.belt(line + "-INFEED", floor, (left + 47 + feed_shift, core_y), (core_x - 30, core_y), cv2))
                connections.append(self.belt(line + "-LABEL-TRANSFER", floor, (core_x + 30, core_y), (left + 155 + feed_shift, core_y), cv1))
                connections.append(self.belt(line + "-OUTFEED", floor, (left + 175 + feed_shift, core_y), (right - 20, core_y)))
            for a in selected:
                retained.add(a["Id"])
                a["Location"] = f'Ground / {floor["RoomName"]} / PROVISIONAL room-relative reference; verify installed position'
            self.kits.append(dict(Line=line, Manufacturer=brand, RoomId=floor["RoomId"], CoreId=core_id,
                                  ReferenceIds=[a["Id"] for a in selected], ConveyorIds=[a["Id"] for a in connections],
                                  Scope="Room-relative schematic with current partitions preserved; not PDF-coordinate or OEM certification."))
        for a in list(self.assets):
            if not a["Id"].startswith("COMPLETE-") or a in self.additions or a["Id"] in retained:
                continue
            # Furniture is unrelated to the defective production-row generator.
            if a["Model"] in ("Office desk", "Office chair", "Office cabinet"):
                continue
            self.owned_unchanged(a, self.baseline_assets)
            self.removed.append(a["Id"])
            self.assets.remove(a)
        for p in list(self.parts):
            if p["Id"].startswith("COMPLETE-BRIDGE-"):
                self.owned_unchanged(p, self.baseline_parts)
                self.removed.append(p["Id"])
                self.parts.remove(p)
        unknown = self.active_by_id.get("MAP-f4c6106346e74555a7216325b5cb4297")
        if unknown and unknown["Name"] == "New item" and not any(
                unknown.get(k) for k in ("EquipmentId", "MeterSourceId", "MonitoringDeviceId")):
            self.modified.append(unknown["Id"])
            unknown["IsPlaced"] = False
            unknown["Location"] += " / archived from office overlap; identify equipment before placing"
            self.conflicts.append("Unidentified user 'New item' preserved in the item list, unplaced to clear its overlap with the west office.")
        if self.confirm_west_generator_strip:
            floors = [p for p in self.parts if p["Kind"] == "Floor" and p["RoomId"] == "DRAW-ROOM-GEN"]
            for asset in self.assets:
                if asset["Category"] != "Generators" or asset["RoomId"] != "DRAW-ROOM-GEN" or not asset["IsPlaced"]:
                    continue
                if not any(contains(f, self.box(asset)) for f in floors):
                    self.modified.append(asset["Id"])
                    self.generator_assignments.append(asset["Id"])
                    asset["RoomId"] = ""
                    asset["Location"] = ("Ground / West generator bank / exterior strip confirmed by user; "
                                         "position retained, not inside the small generator room")
            self.conflicts.append(f"{len(self.generator_assignments)} generators outside the small room reassigned "
                                  "to the user-confirmed west exterior bank; every position and live link retained.")

    def run(self):
        if pathlib.Path(self.review["Source"]).name != self.pdf.name:
            raise ValueError("Wrong source drawing.")
        self.output.mkdir(parents=True, exist_ok=False)
        baseline = self.output / "baseline"
        baseline.mkdir()
        inputs = {}
        for name in ("factory-map-layout.json", "factory-map-building.json", "factory-map-drawing-review.json"):
            source = self.app / "data" / name
            shutil.copy2(source, baseline / name)
            inputs[str(source.relative_to(self.app))] = digest(source)
        shutil.copy2(self.registry_path, baseline / "monitoring-devices.json")
        inputs[str(self.registry_path.relative_to(self.app))] = digest(self.registry_path)
        self.correct_rooms()
        audit = self.audit()
        self.review["EquipmentCorrectionRevision"] = dict(
            InputsSHA256=inputs, PreservedPartitions=True, PreservedFirePositions=True,
            AssetCount=len(self.assets), BuildingPartCount=len(self.parts),
            RemovedIds=self.removed, ModifiedIds=self.modified, AddedIds=[a["Id"] for a in self.additions],
            Kits=self.kits, Notes=self.conflicts,
            GeneratorExteriorAssignmentIds=self.generator_assignments,
            GeneratorExteriorEvidence="User confirmed the west exterior strip; no generator moved." if self.confirm_west_generator_strip else "",
            Reason="Previous repeated packing rows, auxiliary-bay overflow and proposed bridge were not site-evidenced. "
                   "Removed unsupported references and confined the remaining process/controls to current rooms.",
            Limitations=["Current room geometry differs from the PDF; this is a room-relative schematic, not an exact PDF reconstruction.",
                         "Only a measured equipment/as-built survey can establish actual machine models, duties and installed coordinates.",
                         "Unresolved fire displacements intentionally retained by user instruction."])
        # Retain historical provenance, but do not expose obsolete counts as the active revision.
        self.review["FactoryCompletionRevision"]["SupersededBy"] = "EquipmentCorrectionRevision"
        for name, value in (("factory-map-layout.json", self.layout), ("factory-map-building.json", self.building),
                            ("factory-map-drawing-review.json", self.review), ("factory-fire-position-audit.json", audit)):
            write(self.output / name, value)
        shutil.copy2(self.registry_path, self.output / "monitoring-devices.json")
        rows = []
        by_id = {a["Id"]: a for a in self.assets}
        for kit in self.kits:
            models = [by_id[i]["Model"] for i in kit["ReferenceIds"]]
            rows.append(f'<tr><td>{kit["Line"]}</td><td>{kit["Manufacturer"]}</td><td>{html.escape(", ".join(models))}</td>'
                        f'<td>{len(kit["ConveyorIds"])}</td></tr>')
        content = f"""<!doctype html><html lang="en"><meta charset="utf-8"><title>Corrected room equipment</title>
<style>body{{font:16px system-ui;max-width:1250px;margin:32px auto;background:#17232d;color:#eef6fa}}
table{{border-collapse:collapse;width:100%}}td,th{{border:1px solid #72818c;padding:9px}}a{{color:#8fdbff}}
.warning{{background:#624817;padding:16px}}img{{width:100%}}</style>
<h1>Corrected room equipment layout</h1>
<p class="warning">Current room partitions and every fire-symbol position are preserved.
Remaining equipment is a room-relative schematic, NOT confirmed installed coordinates or OEM CAD.
No off-room process rows or invented bridge remain.</p>
<p>Removed {len(self.removed)} unsupported equipment/building references. Seven filling-block identities and links retained.
The anonymous user item overlapping the office is preserved as unplaced, not deleted.</p>
<p>{html.escape(" ".join(self.conflicts))}</p>
<table><tr><th>Line</th><th>Manufacturer (user confirmed)</th><th>Room reference equipment, besides existing block</th><th>Local belts</th></tr>
{''.join(rows)}</table>
<p>Standalone CIP, sterile cap treatment, duplicate HMIs/drives, buffer/process skids and seven repeated packing/pallet lines
were not established by this drawing and have been removed from the active map rather than claimed installed.</p>
<h2>Fire positions unchanged</h2><p>{audit["Count"]} drawing identities retained; {audit["PositionMatches"]} within
drawing-registration tolerance and {audit["PositionMismatches"]} unresolved differences. All remain uncommissioned.
The previous instruction to leave these positions unchanged still applies.</p>
<h2>Preview</h2><p><a href="Completed-Ground.png">Ground-floor application render</a> |
<a href="Completed-Isometric.png">Isometric application render</a></p>
<img src="Completed-Ground.png" alt="Corrected ground-floor map">
<h2>Limits</h2><p>Keeping current room partitions prevents a literal reconstruction of all PDF conveyor routes.
Filler-to-packing transport outside these rooms, actual bridge position, OEM equipment identities and as-built positions
need measured confirmation. The PDF fire plan does not establish an exact equipment bill of materials.</p></html>"""
        (self.output / "Factory-completion-review.html").write_text(content, encoding="utf-8")
        print(f"Corrected: {len(self.assets)} map assets, {len(self.removed)} removed references, "
              f"{len(self.additions)} local belts added; {len(self.parts)} building parts.")
        print(f'Fire unchanged: {audit["PositionMatches"]} within tolerance / {audit["PositionMismatches"]} unresolved.')


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=pathlib.Path, required=True)
    parser.add_argument("--pdf", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--correct-room-layout", action="store_true")
    parser.add_argument("--complete-line-inventory", action="store_true")
    parser.add_argument("--completion-baseline", type=pathlib.Path)
    parser.add_argument("--confirm-west-generator-strip", action="store_true",
                        help="Use only after site/user confirmation: retain west generator positions but clear incorrect indoor-room membership.")
    arguments = parser.parse_args()
    if not arguments.pdf.is_file():
        parser.error("The supplied source PDF must exist.")
    if arguments.correct_room_layout and arguments.complete_line_inventory:
        parser.error("Choose either room correction or unplaced inventory planning, not both.")
    if arguments.complete_line_inventory:
        LineInventory(arguments.app.resolve(), arguments.pdf.resolve(), arguments.output.resolve()).run()
    elif arguments.correct_room_layout:
        if arguments.completion_baseline is None:
            parser.error("--correct-room-layout requires --completion-baseline to protect user-edited references.")
        RoomCorrection(arguments.app.resolve(), arguments.pdf.resolve(), arguments.output.resolve(),
                       arguments.completion_baseline.resolve(), arguments.confirm_west_generator_strip).run()
    else:
        parser.error("The former automatic full-line generator was superseded. Use --correct-room-layout "
                     "with the previous completion snapshot; do not generate unsupported packing rows.")
