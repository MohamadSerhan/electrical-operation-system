# Electrical Operation Center — EOEMS SCADA V6

VB.NET WinForms application targeting Windows x64. The self-contained deployment does not require a separately installed .NET runtime, SQL Server or LocalDB. Authentication and the outage ledger use local SQLite stores; current measurement history and UPS samples require the configured PostgreSQL database.

## Portable deployment

1. Build and publish using the command below.
2. Extract or copy the entire published folder to a writable location. Do not run it from a ZIP or read-only folder.
3. Keep `data\` beside `EOEMS.PowerTransformersScadaUI.V5.exe`; copy the complete folder together when moving the application.
4. If `data\eoems.db` has no administrator, choose **CREATE PORTABLE ADMIN** on the login screen. First-admin setup closes after the first account is created.

The supplied secure SQLite database is preserved in the deployment; existing accounts are not replaced. Passwords use PBKDF2. Legacy plaintext authentication is disabled and plaintext legacy passwords are never imported or reused. Password change and authentication audit logging remain available.

`appsettings.json` configures `PortableSqlite` with the relative database path `data\eoems.db`, an empty central SQL connection string, and legacy plaintext authentication disabled. The build ships only the seed `data\eoems.seed.db`; on startup it is copied to `data\eoems.db` only when no live database exists, so rebuilding or republishing never overwrites live users or the authentication audit. PLC settings, remembered user ID, PLC reading logs, and settings audit logs are stored under `data\`. Settings are editable only through the protected Configuration page.
The folder publish also carries the supplied SQLite database and existing `data\MeterLogs` history; later operational logs remain alongside the executable.

The main workspaces are kept cached after first use to avoid rebuilding or reparenting their controls during navigation; inactive pages remain hosted but hidden, and a fixed page-host inset prevents cached workspaces from being resized on each navigation. Configuration preloads and caches its seven sections after the workspace first becomes visible, before section navigation, and only the active section's refresh timers run. Every workspace page title appears once in the shell header using the same font. Duplicate page-title banners are removed so content starts higher; Configuration and SNMP Setting open directly at their section navigation, while Historian / Reports uses fixed navigation, content and footer rows. Overview, Transformers and UPS Monitoring keep compact status/action rows without repeating their page titles. Configuration opens a fixed-layout section workspace for PLC/display settings, alarm limits, fire connection, system diagnostics, backup/recovery, settings history, and the monitoring device registry. Display-only refresh timers pause for hidden pages and while the window is minimized; PLC/UPS acquisition and historian recording continue independently. Restoring the window refreshes the visible page from the latest readings.

## Factory Map — Digital Twin

### Factory completion staging and drawing-position review

For missing equipment without confirmed installation locations, use
`Tools\prepare_factory_completion.py --complete-line-inventory --app <installed-App> --pdf <supplied-PDF> --output <new-staging-folder>`.
This mode adds unplaced planning references only, preserving every existing asset and the
building/monitoring documents. It refuses to generate the same plan twice. Retained
room-reference models count as coverage rather than being duplicated.
The user confirmed water duties for PET-4/5 and CSD duties for PET-1/2/3/6; Canline's
product remains unconfirmed. The plan includes process/cleaning, feed, quality, coding,
packing and control functions. Carbonation/blending provisions apply only to confirmed
CSD lines. Shrink/case packing are explicitly alternatives; CIP tank count, equipment
quantities, shared utilities and integrated OEM functions remain engineering decisions.
Juice thermal/aseptic treatment and application-specific inspection/disinfection equipment
are recorded as conditional, not automatically installed.
CTA means an air-handling unit: one unplaced HVAC planning reference uses the existing
`Air handling unit` model, not a chiller or a claim about installed quantity/capacity.
`Line-equipment-plan.html` and `line-equipment-plan.json` explain each reference.
Validate this mode with `--factory-completion-unit <staging-folder>`; it checks unchanged
originals, byte-identical buildings/registry, product/manufacturer coverage, no duplication,
no invented links/placement, conditional exclusions and unplaced save/load behavior.

`Tools\prepare_factory_completion.py --correct-room-layout --completion-baseline <previous-completion-snapshot> --app <installed-App> --pdf <supplied-PDF> --output <new-staging-folder>`
prepares a non-destructive equipment correction, original-file backup and
`Factory-completion-review.html`. The previous automatic full-line generator is disabled:
bulk repeated packing rows and a guessed bridge are not evidence of installed equipment.
The snapshot must contain the previously generated map/building documents; changed or
linked reference objects cannot be silently discarded. Correction aborts if such changes
conflict with removal. Retained reference objects keep their IDs when relocated.
The installed map and monitoring registry are never edited by this preparation command.
The existing PDF-review manifest supplies transcribed symbol centers; the audit recovers
each sheet's affine registration from unchanged symbols. Its 0.25 m tolerance is a drawing
comparison tolerance, not an installation tolerance or independent certification.
Existing fire positions, tags, links and commissioning status remain unchanged, including
unresolved reference-picture displacements and original legend/duplicate-tag discrepancies.

Manufacturer assignments were explicitly confirmed by the user: PET 1–4 and Canline are
Sidel; PET 5–6 are Krones. Retained machinery is generic reference equipment, not verified
installed OEM models or vendor CAD. Current room partitions and all fire assets remain
unchanged. Seven room-relative schematics retain the filling blocks, appropriate PET feed/cap
or can feed/seaming references and adjacent operator/PLC/MCC controls. Larger PET rooms
retain a local labelling reference; smaller Krones rooms do not borrow an adjacent hall
to force in additional equipment. Nominal size, doors and all existing user-added parts
constrain the correction. No identities, acquisition links or commissioned addresses are invented.

Unsupported off-room packing rows, auxiliary-bay overflow, redundant process/control skids
and the proposed bridge are removed, not silently reclassified as installed. Legacy redundant
parallel conveyors are retained as unplaced rather than deleted. The unidentified unlinked
`New item` that overlaps the west office is also preserved as unplaced; identified user
equipment is not deleted. Office furniture remains available in the equipment library.
After the user explicitly confirmed that the generator bank extends along the west exterior
strip, `--confirm-west-generator-strip` clears incorrect indoor-room membership only for
generators whose nominal footprints are outside the small generator-room floor.
Their positions, rotations, sizes and live links remain unchanged. Do not use this flag
without that location confirmation.
Inter-room conveyors, structural access, slab openings, hygienic zones, utilities and certified
clearances require measured drawings; keeping current partitions prevents an exact
reconstruction of every PDF route. Room-relative schematics must not be presented as
surveyed installed positions.
Stair schematics now derive their tread count from the configured rise rather than using
ten oversized risers for every height.

Validate a prepared folder with `--factory-completion-unit <staging-folder>` in the existing
`Tests\SnmpValidation` runner. This loads both documents through the application stores,
checks the exact removal/modification ledger, unchanged partitions, fire assets and all links,
manufacturer coverage, actual rendered equipment dimensions, room containment, doorway
access, overlaps, connected transfer belts and persistence, then renders all four floors
and an isometric view. The earlier completion snapshot retains its historical validation mode.
Deploy only after approval and after rechecking the recorded input SHA256 hashes to avoid
overwriting subsequent user edits; do not replace the live monitoring registry.

The EXL 500 kVA symbol follows the supplied two-door dark mesh-front reference, with an upper-left HMI and paired center handles. The NXL 800 kVA symbol has four dark doors, an HMI on the second door, grouped ventilation and a vented plinth. Neither symbol has rooftop fans. Existing physical envelopes, saved IDs/links, placements, rotations and user-selected colors remain supported; these are original schematic drawings, not certified manufacturer CAD.

The transformer schematic uses the supplied industrial top-view reference for twin radiator banks, a conservator, porcelain HV/LV bushings, terminals and lid hardware. It retains the original 22 x 17 x 21 raw envelope and 2.5 x 2 x 2.5 m nominal size, including saved color/scale overrides. Floors use soft color-based shading and restrained concrete/tile seams instead of concrete dot hatching; wall caps have a light edge for top-view depth. These are rendering-only changes in every view: camera angles, room geometry, equipment positions, inventory and monitoring links are not rewritten. `--factory-map-appearance-unit` checks transformer size/color, non-mutating rendering and L-floor clipping; `EOEMS_MAP_EVIDENCE` writes a detail preview.

The stronger top-view presentation uses opaque floor shading derived from each saved color (enhanced chroma, not replacement room colors), clipped two-stage floor-edge shading, and brighter, higher-contrast wall caps at steep viewing angles. Ground-mounted non-fire equipment receives a small capped screen-space contact shadow above 60 degrees elevation. Shadows follow actual model dimensions, saved scale, rotation and category dimensions; they do not enter hit geometry, apply to ceiling devices or change alarm overlays. No texture images, blur passes or extra retained bitmap caches are added. The original transparent wall sides, opacity control, equipment artwork and camera settings remain supported.
The subsequent rendered-plan reference finish uses a coordinated saved ground-floor palette: neutral concrete halls, rose production/syrup rooms, cool blue panel/compressor/water areas, green battery rooms, a coral transformer room and warm-neutral offices. The drawing-review `ReferencePalette` lists every changed floor color; geometry, upper floors, exterior asphalt, equipment and links are retained exactly. Floor shading is restrained rather than strongly oversaturated, with clipped contact shading and no square grids. Walls have crisp near-opaque ivory/graphite cutaway caps in Top View while retaining transparent sides, glass handling, hide/show and the opacity control. Equipment uses brighter stainless steel, continuous cylinder lighting without facet outlines, selectively shaded roof panels and clearer generator/UPS/filler top details. Filler-line schematics include two framed process enclosures, safety rails and a local HMI inside the existing model envelope; these are model details, not new inventory or monitoring devices. Subtle outlines preserve narrow status indicators, and roof shading retains the selected body color. Physical envelopes and custom body colors remain supported. `--factory-map-appearance-unit` also checks UPS details, wall cap/opacity/glass behavior and smooth grid-free floor pixels; set `EOEMS_MAP_APPEARANCE_BASELINE` to a previous application assembly to compare every non-opening catalog model's raw envelope exactly. These are original schematic reference-inspired materials, not photorealistic CAD, verified vendor geometry or relocated/replaced inventory.
The grey-production-floor/site-ground revision was cancelled and rolled back. The approved exterior-only revision instead adds unmarked asphalt patches (`#454B50`) outside a conservative ground-floor building perimeter, excluding every existing floor and apron footprint. All existing building parts, interior finishes and equipment remain unchanged. `ExteriorOnlyFinish` in the drawing-review manifest records the protected perimeter and patch count. Unmarked asphalt has no painted lane/edge stripes, so adjoining exterior patches do not create artificial white lines; marked driveway styles keep their existing markings.
The subsequent plain-surface request removes procedural square grids and epoxy stripes from room floors while retaining their saved colors and clipped edge shading. Unmarked asphalt is filled without polygon outlines or antialiased shared edges; adjacent patches must pass a pixel-level solid-color seam check. Marked driveways still retain intentional traffic markings.

The room-specific finish revision overrides the reference palette only for the existing UPS/panel room and battery rooms/service store (light grey epoxy, `#D9DDE0`) and the water-treatment/filtration rooms plus PET-1 through PET-6 and Canline filler rooms (white, `#FFFFFF`). These remain smooth, grid-free surfaces with restrained edge shading. The drawing-review palette records the requested color and epoxy material overrides; room dimensions, equipment, links, exterior surfaces and other rooms are unchanged.

Build > Road also offers **Curved dashed left/right** pieces. For curves, Length is the centerline radius in plan units, Depth is the road width, and Road bend angle accepts 5-170 degrees (90 by default); rotation follows the tangent at the arc midpoint. Radius must exceed half-width by more than one unit. Their asphalt, edge lines, dashed centerline, hit geometry, snapping bounds and preview follow the same circular arc. Saved straight roads remain backward compatible. The saved driveway revision trims existing straights and adds tangent-connected bends without changing other building parts or equipment.

Add Equipment > Water Systems includes **CIP station - 1 tank / 2 tanks / 3 tanks** with stainless vessels, lid/manway details, legs, ladder, headers, valves, pump and local panel. The same CIP variants are available under Filler Room, alongside **Krones-style mixing and carbonation skid**; Water Systems also offers **Water treatment process skid**. These are original schematic interpretations of the supplied reference photos, not confirmed vendor models or certified CAD. Their nominal footprints are provisional (CIP: 2.4/4.2/6 m long, 1.8 m wide, 3.2 m high; mixing: 3.2 x 2 x 2.8 m; treatment: 4.2 x 1.6 x 3.2 m) and remain adjustable using normal scale/rotation/color controls. They are selectable library entries only: no placed equipment, hardware registrations or acquisition links are automatically added. `--factory-process-reference-unit` checks catalog coverage, metric geometry and non-mutation; `EOEMS_MAP_EVIDENCE` writes the reference-library preview.
Use `--factory-map-top-saved-unit` with `EOEMS_MAP_PERF_DATA` for the same saved-map frame budgets at 1x/4x/12x/30x and both viewport sizes specifically in Top View, including the contact-shadow path.

The shell disables whole-child-tree `WS_EX_COMPOSITED` buffering only while Factory Map is selected. The map already paints a complete native-resolution scene through its own double-buffered canvas; it must not also wait for whole-page child-tree presentation. Leaving the map restores that style for form/grid pages without recreating the cached canvas. Invalidations coalesce behind a one-shot 16ms presentation timer, which calls `Update` so queued frames cannot wait indefinitely for idle `WM_PAINT`; this timer stops while inactive and is disposed with the canvas. The interaction test uses actual native window styles and timer-driven mouse pan/zoom/orbit without forcing painting from its input loop. It measures **actual Paint-event intervals**, reporting pan/zoom and orbit separately. These tests cannot certify the absence of physical display tearing; that requires checking the running app on the affected display.

Equipment geometry retains its full-detail vectors and high-resolution source rasters. At rest and during pan, bounded per-item native-resolution images reuse the exact fractional placement, avoiding repeated resampling. Tiny raster destinations (less than 12 pixels on either axis) use the original high-quality interpolation directly, avoiding extra subpixel rounding on physically small devices. Changing zoom uses the original high-quality source raster unless an exact native image already matches; oversized close-ups retain vector rendering. Source and native images share a 16-million-pixel budget, with at most 96 geometry entries and 256 native images. Pixel totals are updated when images change, not by repeatedly querying GDI bitmap dimensions during painting; tests compare the totals with actual allocated dimensions through replacement, eviction and camera changes.

Mouse selection/hover does not call GDI+ `IsOutlineVisible` on every equipment face. A local profile of the running map found 20.8 seconds out of 40 spent in that mouse-hit path, with individual pauses over 500 ms. Each painted hit path now retains a bounded flattened geometry (0.1-pixel curve tolerance), uses winding/alternate fill testing and a five-pixel round edge tolerance, and rejects distant pointers before testing edges. Floors keep fill-only selection; layer visibility, floor isolation, painter-order selection and invisible small-device hit padding remain intact. Geometry is refreshed with rendered paths and cleared on repaint/level/disposal. `--factory-map-pointer-unit` with `EOEMS_MAP_PERF_DATA` measures actual mouse-move handlers across the saved map in normal/edit mode at 1x/4x/12x/30x, requiring p95 under 8 ms and maximum under 25 ms. The queued interaction check also mixes hover with pan/wheel input and tests close-zoom edit orbit; rendering loops alone cannot validate mouse smoothness.

Deferred shell page construction/handle preparation pauses while Factory Map is active, so storage reads and hidden-page setup cannot interrupt map navigation. The queue resumes after leaving the map. Validate the real shell/message pump with `--factory-map-interaction-unit`; set `EOEMS_MAP_PERF_DATA` to a saved map directory for separate read-only 30-second normal and edit-visible pan/zoom/orbit workloads with normal map refresh and prewarming enabled. The test checks maximum frame/heartbeat gaps and verifies that hidden-page constructors remain queued. This does not simulate live acquisition or guarantee the absence of other machine-specific stalls. `EOEMS_MAP_EVIDENCE` optionally writes UPS front/isometric reference evidence during `--factory-map-unit`.

The Factory Map page draws editable equipment and building elements above a solid-color site ground, without loading the old raster background. The coordinate reference remains 1365 x 766 plan units, at 8 units/metre, with expandable site bounds. Zoom, fit/reset, pan, hover names, category visibility, and full-screen viewing are available. **Layers > Building level** selects Ground, Mezzanine, Upper mezzanine or Basement. Render/hit/edit lists, snapping, room choices and alarm pulses respect the selected level. New/copy-pasted items inherit it; cross-level equipment paste clears room membership. Legacy files without a level load as Ground. Openings must share their wall's level, and rooms cannot span levels.

The supplied four-sheet **TWELLIUM INDUSTRIES-BEVERAGE-FAS-MEDIE** drawing revision is a saved-map update, not an automatic overwrite of custom layouts. Its review manifest is `data\factory-map-drawing-review.json`. Room traces use the dimensioned warehouse as scale reference; equipment remains schematic. The user approved **provisional** placement of unnumbered generators/UPS/battery racks within matching rooms while retaining every existing equipment/meter/monitoring/parent-UPS link and color. The inspector discloses provisional locations; verify equipment identity, footprints, wall and mounting heights on site because the PDF provides no elevations. The three previously unplaced chillers remain unplaced. Camera and access-control sources are not invented.

The ground-floor wall-reference revision uses the supplied November 6 site picture for enclosure topology, production partitions, reception/HR placement and the two driveway runs, without rescaling its pixels as metres. Existing PDF-calibrated floor dimensions and all other levels are retained; affected items move with their rooms while keeping their IDs and hardware links. New partitions, shell steps and 6 m road widths are provisional, as are relocated drawing-only fire symbols. PET-3 has an explicitly unlinked schematic filler, not an invented hardware registration. The PDF's 90 m reference footprint remains as the south service apron; its fire devices remain drawing references requiring site verification because that enclosure is absent from the newer picture. `WallReferenceRevision` in the review manifest records these discrepancies, room translations and baseline hashes. The staged drawing validation writes both isometric and top-view ground-floor previews.

The drawing has **199 actual fire symbols**, including **130 smoke detectors**, rather than the legend's 197/128. Both distinct `SD-118-P4` locations are retained under different stable IDs; mezzanine `SD-104-PM` is separate from `SD-104-EO`. Symbol colors indicate 25 ordinary sounders, two sounder beacons and three sirens, versus the legend's 24/two/four. These discrepancies remain explicit in the review manifest and commissioning notes, not silently renumbered. Symbols include five heat detectors, six multisensors, 24 call points, two beam detectors, the control panel and repeater.

**Monitoring setup:** Configuration > **Device registry** contains Fire devices, Cameras, Doors & gates, and Other equipment. Add rows, enter the device name/model/location, then Save. New fire rows are **uncommissioned drafts**: enter their Drawing tag/reference, leaving **Addressing verified** unchecked. Drawing tags are not loop/device/event addresses. Draft entries cannot consume live samples or trigger verified room fire; a unique explicit drawing tag in a historical description can supply a separately labelled inferred event indication. Only after checking the authoritative panel export should addressing be verified: the stable panel ID, loop and device address are then mandatory; enter the exact cloud site ID, event zone and **complete event address** separately. Existing records default to verified addressing for backward compatibility; this does not assert that a live adapter is commissioned. Device addresses are unique per panel/site/loop, and overlapping commissioned event matches are rejected. Camera/access entries accept an IP address (not a credential-bearing URL) and the vendor's external camera/door ID. The role-controlled registry is saved atomically to `EOEMSData\Configuration\monitoring-devices.json`, audited, tracked by settings history and included in backup configuration payloads. Historian recovery continues to preserve current settings by design.

**Zeta mounting and shapes:** Edit Map > Add > Fire System includes Zeta smoke, heat and multisensor detectors, call points, sounders/beacons, sirens, beam detectors, panels and modules. Ceiling detectors hang **below** the selected room ceiling and follow changes to its wall height (including its base elevation). Without room walls they use the configured positive ceiling height, provisionally 3 m. Wall equipment aligns and faces along the nearest wall within its selected room and level; without a room it uses the nearest same-level wall. Original drawing coordinates/links are not rewritten. Missing compatible walls are reported in the inspector. Wall base heights are kept within the wall height; new call points default to 1.4 m, panels 1.2 m and sounders/modules 2.2 m, all provisional, not installation instructions. Actual elevations, exact product variants and wall faces require on-site verification. The original 3D schematics use Zeta-style white vented detector bodies, heat probes, red call-point/glass fronts, wall-facing sounder grilles, beacon lenses and beam optics; they are not manufacturer CAD. Fire items use the common **8 plan units/metre** baseline with a user-approved **25% visual enlargement** around their mounting anchor. Saved dimensions, individual size overrides, positions, mounting heights and links are unchanged; other categories are not enlarged. Quiet devices remain hidden outside Edit/Build. Active fire indications add a 20-pixel screen-space beacon with a 34-pixel halo over the original model, preserving visibility when zoomed out or in Top View without changing physical dimensions.

`--factory-map-release-unit <evidence-directory>` combines map/building/editor, shell-refresh and monitoring regression suites. `--factory-drawing-unit <prepared-directory> <previous-map-data-directory>` validates the staged drawing's counts, exact retained links/colors, measured scale, floor isolation, draft safety and persistence, and renders every floor in normal/edit views without writing application data.

**Map links:** Edit Map > Edit Item > **Registered monitoring device** links the symbol to a registry entry without changing its shape. Cameras can also store a direct IP override; no video/network request is made by rendering the map. A battery rack uses the separate **Battery rack belongs to UPS** selector, retaining its rack shape. Selecting it shows parent UPS ID, charge/discharge mode, voltage, signed current, temperature, charge and runtime from existing UPS acquisition. These are **parent UPS bank readings, not independent rack/BMS measurements**. Unsupported/bad-quality readings remain `--`. Copy/paste clears equipment, meter, device, parent UPS and camera-IP links; room/mount references remain positioning metadata.

**Rooms and ceilings:** Building > Rooms stores a room temperature source (`PLC:<configured meter name>`, existing TDEGC channel 15) and optional fire panel/site/zone. The PLC temperature channel is not verified as a room sensor by default: the administrator must explicitly confirm that the commissioned channel measures that room. Only good, finite, recent measurements are evaluated. Configuration > Alarm limits has four separate room thresholds: low alarm, low warning, high warning and high alarm, including negative cold-room limits. Temperature warnings/faults pulse **walls only**, never imply a fire. Smoke/heat/multisensor symbols follow the selected room's current wall height; without a room, a positive mounting height is required. Update the room reference when relocating a detector to another room. Fire devices are exposed in Edit/Build, otherwise hidden without a verified current warning/fault/fire signal. Small symbols get a selectable location marker rather than oversized physical geometry.

**Alarm meaning and performance:** Map amber indicates a warning (including battery discharging) or fire-system fault; red indicates a UPS/electrical fault or a fire indication. UPS fault severity overrides a discharge warning. Existing PLC voltage/load/temperature limits and configured maximum loads are reused; unconnected generators/other equipment have no invented alarms. A verified current **Fire** sample affects the addressed device and its linked room's walls **and floor** in steady red; an ordinary device fault/warning affects only its symbol. One 500ms pulse timer draws native-resolution overlays, without rebuilding equipment artwork or the full scene. It stops while the map is inactive/hidden. Alarm clearance, stale data and pending camera/edit redraws are covered by regression tests.

**Latched fire events:** Fire Events displays explicit addresses/loops/zones recovered from descriptions such as `(Address 24)` and vendor messages such as `Fault with 24, ...`; tooltips/details/CSV identify the source and preserve raw fields. `AREA 1`, kVA ratings, `Module failure at 5 (TCP/IP)` and `Zone Monitor Module` are not fabricated device addresses/zones. Conflicting identities block automatic matching. Successfully synchronized saved-site events latch locally: **Fire is steady red; Fault is blinking orange**, retained across restart until **Factory Map > Fire alerts > Reset Map Alarms**. This authorized, audited reset clears only the events displayed at confirmation, sends no hardware commands, does not clear verified live samples, and does not change event history. The same historical event cannot reappear after reset; a new event ID (or a new timestamp/content fingerprint without an ID) raises it again. Reset/service/restore/test messages do not create or silently clear latches. Per-panel protected `.map-alerts` files keep source/site-scoped event identities and pending events; storage errors are explicit, not treated as normal.

Matching prefers exact panel/site/complete address (or explicit loop plus leaf address), then a unique drawing tag/device name. Unscoped drawing/location inference is allowed only with a single configured panel/source; it does not commission registry addresses. A room name such as `PET2` can place a labelled **area-only** beacon, never illuminate an arbitrary smoke detector or claim current room fire. Ambiguous/missing matches remain visible in the Fire alerts list, with descriptions, event UTC, recovered address, matching reason and Show on map where available. Opening the map hydrates up to the latest 10,000 locally stored saved-site records on a background worker, so existing events can appear without a successful network poll; reset event identities are still suppressed. Rendering reads immutable published snapshots without waiting for history/alert file locks. The map always distinguishes latched history from verified current state; the cloud history connector still does not establish current panel condition. `--factory-fire-events-unit <evidence-directory>` covers extraction, ambiguity/site isolation, persistent reset/retrigger, steady/blinking pixels and zoom-independent marker size, plus saved-map examples when `EOEMS_MAP_PERF_DATA` is set.

**Inferred event locations:** Fire Events and the map's Fire alerts list include **Area / Location** and **Described Panel**. Shared parsing recognizes main/panel rooms, UPS room panels and kVA labels, underground/basement electrical panels (including the supplied `undeground` spelling), generator areas and named production/utility rooms. `Area 1` is an area label, never an invented fire zone. Described electrical panels are location labels, not replacements for the configured source fire-panel identity. An explicit UPS room takes precedence over another room merely containing UPS equipment; without such a room, a unique room containing mapped UPS equipment can receive an inferred area-only indication. Underground descriptions require a unique basement electrical room; repeated battery/utility rooms remain ambiguous.

Within one source fire panel, up to 10,000 locally stored events can supply a missing room description only when the same site, explicit loop identity and address have one consistent location. Conflicting rooms/areas/panel labels, contradictory identities, future records and cross-site/source evidence cannot teach a guessed location. Inference is labelled in tooltips, details and CSV, preserves the raw record, and does not create physical sensor addresses or commission devices. History hints are prepared in the background and published as immutable snapshots; resetting an indication does not erase historical location knowledge or relatch reset events. A unique physical sensor still requires its exact configured identity or unique drawing tag/name; room-level evidence alone never selects a random detector. Missing evidence remains **Unresolved**, not a guarantee of location.

UPS indications retain the **actual evaluated alarm name**, highest severity first, with all simultaneous active conditions in the inspector. Fan failure, communications lost, charger failure and battery conditions are not collapsed into a generic battery fault. Battery rack indications explicitly attribute inherited alarms to their **parent UPS**, not to an individually monitored rack. Retained cleared card rows do not blink; unspecified card state and conflicting evidence remain explicitly unconfirmed warnings. A positive card alarm count without complete details keeps its alarm severity without inventing a fault type. Acquisition timeout/offline/stale is shown separately as unknown data, not a battery diagnosis. UPS freshness follows the acquisition policy (three configured polling intervals plus five seconds); PLC readings require a good timestamp within 30 seconds, with at most five seconds of future clock tolerance. Old/missing/invalid readings are not displayed as current numbers or zero. Independent metered-panel links use their selected meter (and its applicable alarm limits), never the attached UPS/equipment readings; incomplete meter coverage is not labelled fully normal. Door contact position does not hide an access fault; building-door faults pulse the door only. Registry-type mismatches cannot drive a symbol or create room fire.

The shell collects UPS snapshots for the active map on a background worker with at most **one read in flight**. Protected-profile file reads and connection validation cannot block mouse, zoom or painting. The UI applies completed snapshots on its normal refresh tick; the last published readings continue to expire during a delayed read, and a failed refresh clears current UPS data with an explicit footer error. Fire-history snapshot reads and recording/footer diagnostics do not acquire locks held during file/SQLite writes. `--shell-refresh-unit` measures actual UI refresh duration and navigation/message-pump gaps while a simulated one-second profile-store stall is active, including single-flight and stale-data checks. Set `EOEMS_MAP_PERF_DATA` to the saved map's data directory to include its equipment/building workload.

**Integration boundary:** The existing Zeta connector supplies **event history, not verified live panel/device state**. Exact addressed records from successful saved-site synchronizations are exposed as immutable in-memory evidence (no SQLite/network work on map refresh). Matching history is available in the item's editor-mode inspector with an explicit **EVENT HISTORY - live state UNKNOWN** explanation, even if its category says FIRE; it never triggers blinking, exposes a quiet device in normal mode, turns a room red, guesses addresses from location/title, interprets reset semantics, or claims normal status after silence. Events lacking device addresses cannot identify a device. Cameras, live door contacts/personnel activity, per-rack BMS telemetry and current fire/suppression states require documented vendor adapters and site commissioning; no such hardware feed has been simulated. `FactoryMapView.SetMonitoringSamples` accepts copied, keyed, explicitly verified, timestamped read-only samples for future adapters; unverified/stale samples cannot create live states or fire indications. Live door contacts affect the drawing only and never overwrite saved visual states. No door, camera, fire reset/silence or suppression commands are sent. The SCADA display is not a replacement for the certified fire panel or life-safety controls.

The expanded Fire System library includes original schematic Zeta-style panels/repeaters, smoke/heat/multisensor detectors, call points, sounders/beacons, interface/isolator modules, a suppression control panel, and CO2/FM200 cylinder banks. These are representative symbols, not manufacturer CAD or a declaration of compatibility. Panel/loop/address modelling follows the [Zeta Premier Quatro product description](https://www.zetaalarmsystems.com/product/premier-quatro-4-loop-analogue-addressable-fire-alarm-panel/); actual device compatibility, event schemas and safety design must be verified for the installed model.

Run `--factory-monitoring-unit <evidence-directory>` for isolated registry validation, role checks, persistence, room limits, ceiling placement, UPS alarm priority, exact fire identity/history boundaries, live sample freshness, door-state isolation and native-frame pulse regressions. `--zeta-cloud-unit <evidence-directory>` additionally checks that map evidence comes only from successful saved-site event synchronization.

Administrators use **Site size** to reserve 100-400% length and depth independently and choose the ground color. Expansion adds blank ground to the right/bottom; it never stretches existing equipment or walls. Positions remain percentages of the ORIGINAL footprint, with coordinates up to 400%. Ground also includes placed equipment/building centers beyond the configured size so saved outlying items remain visible. Explicit shrinking cannot exclude existing item centers. Site settings save separately to `data\factory-map-site.json`, with a previous-version `.bak`; cancelling equipment/building edits does not undo a saved site change.

Administrators can choose **Edit Map**, then **Add Item**, **Edit Item**, or **Delete Item** in the editing bar. Select an item directly on the map or from the item list, which also includes unplaced chillers. Edit Item changes the display name, system-equipment link, category, 3D shape, placement, coordinates and rotation. To place a chiller, select it in the list, enable **Placed on map**, set its approximate X/Y percentages, apply, and drag it to the desired position. Existing system equipment can be selected in the link list, including actual stable IDs from the UPS registry; choose **None** for a visual-only item. Renaming never changes the linked identity. Deleting removes only the map item, never system equipment, acquisition settings or history. **Save** persists the complete map in `data\factory-map-layout.json` with a previous-version `.bak`; **Cancel** restores all additions, deletions, names, links, shapes, placements and conveyor edits. Version-1 position-only layouts migrate on the next save. Offline previews and non-administrators cannot edit.

**Move a region of equipment together:** in **Edit Map**, hold **Ctrl** and left-drag to draw a selection rectangle around the equipment (even when starting over an item). Release Ctrl after selecting, then drag any highlighted item to move the entire group. Ordinary empty-space left-drag pans the page and preserves the selection; middle-drag also pans. Items whose complete rendered bounds lie inside the rectangle are highlighted; only placed, visible items on the active floor are included. This works in **2D Plan**, **Top View** and **3D View**, including embedded CAD conveyors/pipes. Group movement preserves relative positions, dimensions, elevations and monitoring links. Group snapping excludes its own members; map-boundary clamping keeps the group spacing intact. Arrows nudge the group; one **Undo/Redo** reverses/reapplies a whole drag, retaining the selection. **Save** persists all moved positions; **Cancel** restores them. Click an unselected item or use the item list for single-item editing; **Esc** clears the selection. Region selection is for movement/nudging, not bulk properties/copy/delete; those actions still use the active single item. Buildings are not included.

**Mirroring:** **Edit Map > Edit Item** and **Building > Edit** include **Mirror horizontal (local X)** and **Mirror vertical (local Y)** checkboxes with live previews. These reflect the actual shape in plan/3D before its rotation; vertical here means the plan-depth axis, not upside-down height. Position, dimensions, elevation and monitoring IDs remain unchanged. Both flags can be combined or cleared; symmetric objects may look unchanged. Built-in equipment, imported OBJ/CAD, legacy image items, doors/gates, floors, stairs, roads and markings support reflection. Mirroring a wall also reflects its attached openings/cutouts along the wall; each opening can additionally be mirrored independently. Save/load, clipboard cloning, Undo/Redo and Cancel retain mirror state. Older layouts without the flags remain unmirrored. `--factory-map-editor-unit` checks both dialogs, reflected geometry vertices, mirrored wall coordinates/attachments, rendering-cache isolation, persistence and history; `EOEMS_MIRROR_EVIDENCE` optionally captures plan/3D comparisons.

The map uses original, schematic equipment geometry: yellow Caterpillar-style generator sets with radiator/grilles, service doors, lifting points and exhaust; 40-bar HP compressor tanks and finned piston heads; Vertiv-style EXL 500 kVA and wider NXL 800 kVA cabinets with vents, handles and displays; generic chillers with guarded roof fans and separate blue Hansa shell-and-tube chillers with twin pumps; battery racks containing four shelves of batteries; and PET/can beverage fillers with bottles and conveyors. These are not downloaded/licensed manufacturer CAD models or verified nameplate geometry. Production-line **Conveyor length** extends the belt; standalone **Conveyor** items can be added under Production Lines, rotated and positioned to join belts. Length is in schematic plan units (20-400), not surveyed metres. Larger equipment is selectable along its rendered shape, including extended belts.

Equipment now uses the same **8 plan units per metre** as building heights, rather than unrelated drawing sizes. Every fixed model has a representative nominal envelope; for example, the enclosed 810 kVA generator is approximately 5 x 2 x 2.8 m, the 1500 kVA is 6.5 x 2.4 x 3.2 m, and the bare 40-bar compressor skid is 3.2 x 1.6 x 1.9 m. These are visualization assumptions, **not universal standard sizes or verified manufacturer specifications**: compressor flow, chiller duty and exact enclosure/cabinet configurations change actual dimensions. Bundled panel variants retain the bare machine's scale and add cabinets outside its footprint instead of shrinking the machine to fit. Small meters, sensors, AC units and safety devices also use metre-scale envelopes. Doors and gates have separate sensible nominal widths/heights. Variable conveyors and integrated line layouts retain their exact requested plan-unit runs (divide by 8 for nominal metres); integrated lines remain schematic assemblies, not dimensioned manufacturer layouts.

**Edit Map > select item > Edit Item** includes **Approx. size L x W x H (m)**, independent **Length / Width / Height (%)** controls (10-500% of the per-model nominal envelope), a **100% size** reset, **Rotation (degrees)** (0-359), and **Choose color** for the item's enclosure/body. Existing saved percentages, rotations, positions, equipment links and colors are preserved; default-size items automatically adopt the corrected common scale. Existing category-level provisional dimension settings remain additional multipliers relative to their original category defaults; the dialog's approximate size is before these category overrides. Reset only changes the selected item's percentages, not category settings. **Original colors** restores the equipment's original palette; screens, battery terminals and other functional details retain their distinct colors. The live 3D preview updates before Apply. Sizes, rotation and colors are per item, survive Save/restart and are restored by Cancel; older saved maps default to 100% and original colors. Camera rotation remains separate from item rotation.

To find the wider UPS model, select **UPS > Vertiv NXL 800 kVA** or use the **NXL 800 kVA** shortcut in the item dialog; EXL 500 kVA remains a separate choice. Item names are no longer drawn permanently on the map, including selected-item names. Hover over the rendered item to see its name, model and stable ID; select it for acquisition status in the live inspector. Unplaced items are summarized by count rather than a stack of names. Removing the raster background also removes its old printed labels.

**Generators** includes separate **Caterpillar generator 810 kVA** and larger **1500 kVA** enclosures, with grilles, service doors, controls and exhaust. Legacy generic generators retain their saved model: select the correct rating for each installed set rather than inferring ratings from IDs. **Compressors** includes Atlas Copco-style 7-bar screw compressors, refrigerated dryers and filter banks alongside HP 40-bar units. **Chillers** includes generic four-fan and reference-based Hansa industrial units, plus separate chiller / 40-bar compressor cooling towers. **HVAC** includes indoor wall 1.5/2.5 HP, cassette 5 HP, ducted 10 HP, floor-standing 20 HP, outdoor 1.5/2.5/5/10/20 HP, VRV outdoor/indoor cassette and air-handling symbols. These capacity labels are selectable schematic variants, not verified manufacturer specifications.

**Production Lines** includes integrated PET, can and glass bottling lines with adjustable 20-400 plan-unit conveyor runs; machines scale down proportionately for compact runs below 140 units. **Filler Room** adds syrup mixing, carbonation, plate pasteurization, inspection, coding, case packing, palletizing, stretch wrapping and preform handling. **Utilities** adds a steam boiler, forklift, loaded pallet rack, pallet stack and loading dock. All are optional visual assets; no real acquisition devices or telemetry are created.

The HP 40-bar model now has a multistage compressor skid, motor, finned heads, intercoolers, guard, piping and gauge. Atlas-style 7-bar screw compressors have roof fans, vents and a local controller. Both offer separate **with control and power panels** model variants; bare models omit those external cabinets. **Chiller** retains the previous four-roof-fan unit, including its with-panels option. **Hansa chiller** is a separate blue industrial shell-and-tube schematic based on the supplied reference: stacked horizontal vessels, dark insulated upper vessel/return pipe, compressor modules and two circulation pumps. Its **with control and power panels** option adds a white combined control/power cabinet with screen, buttons, handles and cable drops; changing the machine body color leaves the cabinet white. Both Hansa options include two pumps. Their placement and the approximate 3.8 x 2.2 x 2.4 m envelope are visualization assumptions, not verified details from the reference photograph. Old saved Hansa-labelled fan units migrate to **Chiller** without changing their positions, links, names, size overrides or geometry; library revision 1 distinguishes newly selected Hansa units so they do not remigrate after Save/restart. The migration is in memory until the map is saved. Additional **Chillers** include air-cooled screw, water-cooled screw and centrifugal water-cooled models, pump/buffer skids, and standalone control/power panels for arranging a complete cooling plant. The Vertiv-style EXL 500/NXL 800 symbols have distinct two-/four-door footprints, ventilation, handles, displays and solid roofs. They remain original schematic approximations, not manufacturer CAD or verified dimensions.

**Electrical Panels** adds metered ACB, ATS, MTS and load-distribution panels, generic/Eaton-style RMUs, SF6 breaker cubicles, generic/Eaton-style HV voltage-regulator banks, HV utility poles, lattice transmission towers, pole fuse-cutout assemblies and insulators. These do not simulate electrical connectivity, safety clearances, switching states or actual HV protection. Capacity/brand labels are visual identifiers only; the map sends no switching commands.

For metered panel shapes or **Energy meter**, **Independent panel meter** lists existing configured PLC meter blocks by their acquisition ID (the block's transformer name), model and register. This link is saved separately from **Link to system equipment**, so a panel can be attached to a generator/UPS/other catalog equipment while reading its explicitly selected PLC meter. The inspector and connected counts use only Good data from that selected source; missing, removed or bad-quality sources show `--` without borrowing the attached equipment's readings. Unavailable saved IDs are shown and retained until explicitly cleared. Selecting an unmetered shape clears the independent meter link. No new acquisition meter, Modbus mapping, telemetry or energized state is created; embedded meter screens remain schematic. The current acquisition catalog is the existing PLC block registry, not a fictional list of additional meters.

With the map focused, **Ctrl+C / Ctrl+V** copies/pastes selected equipment in **Edit Map** or building elements in **Building**; **Ctrl+D** duplicates. Copies get new IDs, nearby offsets and no equipment/meter links. Copying a wall includes its attached openings with remapped parent IDs; copying only an opening detaches its resolved copy. Copied building elements do not inherit room membership. The clipboard is local to this map page, not the Windows clipboard, and cannot paste across equipment/building modes. **Delete** uses the existing confirmation; **Enter/F2**, double-click, or the right-click action menu opens properties. Right-drag still orbits; a stationary right-click opens the editing menu. Save/Cancel includes all pasted elements.

The fullscreen icon/button moves only the map into a borderless window; **Escape** (or its return strip) restores the page, camera and editing context. Wheel zoom eases toward its cursor-anchored target with a 0.2x-40x range. **WASD** pans independently from precision edit arrows; **Q/E** orbits, **+/-** zooms, **F / Focus** centers a selected element, and **Home** fits the site. Shift-wheel pans horizontally; Shift-arrow accelerates movement, while Ctrl+arrows always pans rather than nudging. This improves the existing orthographic/isometric navigation; it is not a first-person perspective walk mode.

The upper-right **3D navigation wheel** appears when the mouse moves and fades out after about 1.4 seconds of inactivity (including when parked over it). Drag its **ORBIT** sector or **3D** center to rotate, drag **PAN** to translate, click **N/E/S/W** to choose a cardinal orientation, **TOP** for a near-overhead 70-degree 3D view with visible equipment/wall height, or **FIT** to reset to the full isometric factory. **Layers > Exact plan view (2D)** retains the perpendicular overhead view for precise floor-plan work. The wheel remains visible during an active wheel drag. A completely hidden wheel does not intercept map selection, and these navigation gestures never modify equipment/building data. The same wheel works in fullscreen and both editing modes.

Map actions occupy one non-wrapping toolbar row. On narrow pages the action section scrolls horizontally; the **floor selector and icon-only fullscreen button stay pinned at the far right**, with accessible names. **2D Plan** opens the exact overhead editing view directly; switching floors retains the chosen camera mode. **Layers** contains the site/wall/floor/opening visibility controls, wall-opacity slider, category/snap controls and **Dimensions / Site size** actions. The contextual Building bar wraps at narrow widths so its Save/Cancel and drawing tools remain reachable.

The shell refreshes the active Factory Map at most every 250ms on its existing UI timer, even when no new PLC event is pending, so independent UPS changes do not require reopening the page. Unchanged indicator states still avoid scene repaints. Offline preview passes an empty UPS collection to the map/analyzer because acquisition is intentionally disabled; it does not dereference an absent service. Shell page-prewarm and restore timers are disposed on close. `--shell-refresh-unit` checks this actual shell refresh path, UPS-only changes, offline map opening, cached navigation and timer shutdown without starting acquisition.

**Edit Map / Building** tools are created only when entering the authorized editing mode, never as hidden controls during normal viewing. Only the active mode has an item picker and rollback copy; Save/Cancel removes and disposes its toolbar and releases that copy. Reopening creates fresh tools and captures the current layout for Cancel. Add/properties dialogs and their 3D previews already open on demand and are disposed on close; the shape catalog is metadata, not a set of running previews. Placed equipment and building geometry must still be rendered in normal viewing, so separating editing tools reduces retained controls/memory but does not eliminate scene-rendering costs.
The editing context menu is created on its first right-click and disposed when editing ends; snap-footprint entries are also cleared. Run `--factory-map-editor-unit` for targeted control-lifecycle, rollback, permissions, clipboard, meter-link and building-editor regressions without running the unrelated paint-time benchmark.

Unchanged acquisition states no longer invalidate the factory scene on every refresh; KPI/selected-item readings still update, and changing acquisition quality/state (including a mutated snapshot object) redraws its indicators. Navigation-wheel hover/fade repaints reuse a **native-size, unscaled** scene image only when no scene invalidation is pending. Pan, zoom, edits and resize still render at the current resolution; this is not the previous stretched whole-scene zoom cache. The image costs four bytes per viewport pixel and is disposed with the canvas. Leaving the page stops wheel/zoom timers. Equipment resolution thresholds retain the sharper artwork across small back-and-forth zooms to avoid rebuild oscillation. `--factory-map-opening-unit` reports cold construction, first activation and cached reopen timings and verifies repaint suppression, pending-invalidation priority and hidden-page timers.

Equipment rendering retains camera-specific vector commands and bounded high-resolution artwork. Identical shapes share artwork; appearance, dimensions, item rotation, opening state and camera changes invalidate it. Cached equipment is sampled from at least twice its displayed resolution; oversized close-ups replay the retained vector commands instead of magnifying capped artwork. Anti-aliasing and artwork quality stay enabled during movement, not only after releasing the mouse. Only visible image regions are sampled; offscreen equipment is skipped. Actual transformed paths still drive hit testing, while live status dots and selection outlines remain uncached. The cache is limited to 96 entries / approximately 64 MiB of image pixels and released with the map. Buildings retain native vector rendering and real opening cutouts. Saved maps and acquisition data are unaffected.

The map no longer scales an overscanned whole-scene bitmap or serializes the full layout on every paint. Floors, walls, editing handles and overlays are drawn at the current display resolution on each frame. Building culling includes open-door travel and stair railings. Wheel zoom uses a time-based 140ms easing curve rather than an absolute-error convergence loop, so high zoom does not create a long animation tail; the point beneath the mouse remains fixed.

For a read-only performance check against an installed saved layout, set `EOEMS_MAP_PERF_DATA` to its data directory before running the existing `--factory-map-unit` test mode. This measures pan/zoom at verified 1x, 4x, 12x and 30x at 1365x766 and 1920x1080 using those layout/building files; it does not save to them. It reports mean, p95 and maximum paint times, with a 25ms average budget per 1365x766 pixels (approximately 50ms at full HD), a 50ms p95 limit and a 100ms maximum-frame guard. Regression coverage also checks unchanged pixels when motion starts, vector replay against original equipment rendering, no capped-image magnification, transformed hit shapes, real message-pump wheel animation duration/anchor, narrow toolbar layout and the pinned fullscreen icon. The software renderer can remain slower at full HD with many overlapping translucent walls; these paint budgets do not promise 60fps or guarantee actual display pacing on every workstation.

**Building > Add > Marking** offers solid/dashed colored floor lines, walkways and keep-clear hatching. Adjust Length, Depth, rotation and color in Properties; markings follow the Room floors layer and have no collision or traffic-control meaning. **Utilities > Safety bollard** adds independently colored small posts. Rapid roll-up doors/gates now have two side-by-side transparent vision windows in their closed colored curtain; opening rolls the curtain upward, leaving the passage clear. Body color changes the curtain, not its frame or clear windows.

### Direct building and drawing tools

Enter **Building**, then choose a tool beside Save/Cancel. Drawing tools open **2D Plan** and edit the same geometry shown in 3D:

- **Wall:** drag a segment; repeat to continue. Nearby wall endpoints and centerlines snap together. Both wall-end handles resize with the opposite end anchored; dragging the wall moves it with its attached openings.
- **Room:** drag a rectangle to create four walls plus a floor with one shared room identity. **Floor** draws just the surface. The preview shows plan-unit dimensions, not surveyed metres.
- **Door / Window / Gate:** click or drag to place. Nearby compatible walls automatically align the opening's position, rotation and thickness and receive a real cutout. Attachment requires the same floor, enough length/height and no overlapping openings. Drag an attached opening along its wall, or away to detach; drag it near another compatible wall to reattach. A copied opening remains detached until dragged to a wall.
- **Line / Rectangle / Ellipse / Pen:** draw colored annotations; **Color** chooses the next drawing's color. Pen paths persist and can be moved, resized and rotated like other building parts. Outline shapes select only their strokes, not their empty centers.
- **Select:** drag an element to move it; square handles resize and the round handle rotates. Equipment in **Edit Map** has length/width/rotation handles too. Handles remain separated from small objects so they do not obscure the move target. Properties provides exact dimensions, height and base elevation. Ground-mounted items start at zero base; windows retain a sill, and wall/ceiling-mounted devices retain their appropriate mounting rules. Saved elevations are not globally reset.
- **Shift** constrains drawn lines/walls to 45-degree increments or rectangles to squares. **Alt** bypasses snapping; **Esc** cancels an unfinished drawing. Choose Select to return to moving existing geometry.

**Undo / Redo** buttons, the editing context menu, **Ctrl+Z**, **Ctrl+Y / Ctrl+Shift+Z**, and **Ctrl+X** (Cut) work in both editing transactions. Ctrl+C/V and Ctrl+D retain their existing copy/paste/duplicate behavior. Each drag or complete room is one undo step; keyboard nudges, property changes, room changes, adds and deletes are also recorded. History retains up to 100 undo steps, clears the redo branch after a new edit, and is released on Save/Cancel. Native text-field shortcuts remain native. Save persists the current transaction; Cancel still restores its entry state, independently of undo history.

The workflow follows established direct-manipulation floor-plan patterns (see the [Sweet Home 3D user guide](https://www.sweethome3d.com/users-guide/)): wall magnetism, selection handles, an overhead plan, levels and action-level undo. It is an original schematic editor, not structural CAD or a claim of architectural/safety compliance. Existing size limits remain: shape/floor depth up to 800 plan units, wall/line length up to 5460, and pen strokes up to 4096 points. `--factory-map-editor-unit` additionally exercises actual mouse/keyboard drawing, attachment/detachment, complete-room undo, cut, redo branches, equipment handles and drawing save/load.

### Searchable equipment library and OBJ/CAD imports

**Edit Map > Add Item** now opens a searchable library of every built-in equipment model plus uploaded equipment. Search by model/name/category, filter the category or **Uploaded equipment**, preview, then **Place selected** (or double-click). The item appears selected at the **current visible map center on the selected floor**, not a fixed factory coordinate. Its category is made visible. Drag to move, use square/round handles to resize/rotate, **Ctrl+D** for another copy, or **Edit Item** for exact scale, elevation, color and existing system/monitoring links. The placed-item picker also supports name-prefix autocomplete. Built-in ceiling/wall devices retain their mounting defaults; ground items start at zero base.

To add equipment not supplied with the application:

1. Choose **Add Item > Import OBJ / CAD** and select an **OBJ**, **DXF** or **DWG** file. JPG/PNG upload is no longer offered; existing image-based map/library entries remain supported.
2. Choose the source **up axis**: Z-up for CAD, or Y-up for many OBJ exports. The preview uses embedded polygon faces and curves, not a photograph on an envelope.
3. Enter a reusable name and actual **length, width and height in metres** (0.05-100 for each nonzero axis), then **Generate equipment** and **Place selected**. Source units are not assumed: the initial preview fits the longest dimension to 2 metres; very thin nonzero axes are clamped to the 0.05-metre editing minimum. Set the actual dimensions before placement. **2D CAD outlines remain flat with zero height and disabled height entry**; no hidden geometry or extrusion is invented.

Supported CAD geometry: lines, circles/arcs (sampled at 96 points), straight/curved 2D polylines, 3D polylines, 3DFACE, MESH, polyface meshes and exploded block inserts (up to 16 nesting levels). ACIS solids/surfaces, proxies and other unsupported geometric entities produce an explicit error rather than a partial model; export them as polygon meshes/3DFACE or OBJ first. Text/dimensions/attributes are excluded with an import count. Missing text-style references produce a visible warning; other reader warnings/errors reject the file. The ACadSharp reader supports DWG R14 through AC1032 (AutoCAD 2018-format) and DXF versions supported by that library, including ASCII/binary DXF.

OBJ supports XYZ vertices, polygon faces (including negative indices) and line paths. Groups/normals/UV/material directives are accepted but external MTL files and textures are never opened; the map uses its schematic material/color. Freeform OBJ surfaces are unsupported. Inputs are limited to 20 MB, 30,000 vertices, 15,000 faces/paths/entities, 2 MB embedded geometry and 3 MB per saved definition. Geometry is normalized, SHA-256 checked and deep-cloned for clipboard/history.

The built-in **Sidel-style blower and preform feed** under **Production Lines** remains available without importing a file. It uses reference-based hand-built machinery at **8 x 2.5 x 2.5 metres**; internal and unseen details are schematic, not manufacturer CAD. Placements already using this model receive its detailed rendering automatically; image-only placements do not.

The Sidel template uses antialiased edges, lighter outlines, beveled enclosure/roof edges, panel seams, hinges, roof handles and schematic HMI details. This finish is limited to the Sidel template; other equipment keeps its existing rendering. It changes appearance only, not stored dimensions, positions or system links.

Imports stay local: no file is uploaded to an external service. PDF, SVG, executable files, STL and GLB are not supported. Imported equipment is a **visual map item**, not a new commissioned acquisition source, equipment-register record or fabricated live reading. Existing device/system links can be assigned separately through Edit Item.

Legacy stored images retain their checked SHA-256 hashes and existing rendering. Invalid model files/dimensions display errors without adding items. Edit-history comparisons use content hashes instead of serializing embedded geometry/image bytes.

Reusable definitions are stored as individual atomic JSON documents in `data\equipment-library` (up to 100 definitions). Keep this folder when moving the portable application. Each placed item also embeds a copy of its definition/image in the normal map layout, so a saved map **does not depend on the original upload path or the library file**. Library saving is independent of map Save/Cancel: cancelling map edits removes unsaved placements but does not delete a newly saved library definition. **Delete uploaded** removes a selected uploaded definition and its backup after confirmation; built-in entries and existing map copies are not deleted. Existing placements are snapshots, not automatically changed by future library edits. Replace an older silhouette placement with the modeled template explicitly; existing map positions and device links are not migrated automatically.

`--factory-map-editor-unit` checks actual OBJ/DXF/DWG parser fixtures and import forms, flat CAD outlines, OBJ Y-up, portable persistence, cloning, deletion, corrupt-file guards, removal of the image-upload action and legacy image regression behavior. It also checks library filtering/layout, map placement, properties, clipboard and undo. The detailed Sidel model is checked at 24 camera views (plan, elevation and 3D), with measured geometry bounds, agreement between built-in/uploaded versions and image-independent rendering. Set `EOEMS_MAP_LIBRARY_EVIDENCE` to a directory to capture model views and library-dialog previews during that check.

### Proposed connected production-line layouts

The production catalog includes **PET Combi - still water**, **PET Combi - carbonated drinks**, **Can filler and seamer - carbonated drinks**, preform cage/tipper, roller sorter/bridge and can-end magazine geometry. PET Combi integrates blowing, direct neck transfer, filling and capping; its standalone foreground hopper is omitted when separate feed stages are placed. These are hand-built proposed equipment envelopes, not certified Sidel/Krones CAD.

`FactoryMapProductionLinePlan` prepares cloned map/building proposals, never saving live files itself:

- PET4/PET5: still drinking water. PET1/PET2/PET3/PET6 and CAN1: carbonated drinks, with blending/carbonation and a labelled food-grade CO2 supply interface.
- PET: cage/tipper -> hopper -> elevator -> roller orientation/bridge -> oven/blower -> direct transfer -> filler -> capper. Separate cap elevator/sorter feeds the capper.
- CAN1: empty-can depalletiser -> rinse/inversion -> can filler -> end magazine/seamer -> fill/seam inspection. No PET preforms, screw-cap handling or bottle labelling is added.
- Downstream: inspection -> reject -> drying -> PET labelling -> date code -> accumulation -> shrink packaging -> palletising -> pallet wrapping. Case packing remains an unplaced alternative. Shrink/tray forming and seam-inspection internals remain schematic.
- Product supply/pumping, CIP supply/valve manifold/return, treated-water/syrup, blow/plant air, cooling and electrical interfaces are labelled proposals. Interface symbols are not commissioned equipment, new utility plants or measured operating values.

Existing equipment IDs and monitoring links are preserved. Superseded standalone stages/conveyors retain their records but are unplaced. Existing fire devices and building/door records are unchanged; proposed conveyor/service openings are separately labelled and checked for wall bounds/non-overlap. PET5/PET6 retain Krones identification and full-size filling blocks; their feeds occupy explicitly **PROPOSED adjoining feed zones**, not claimed existing surveyed rooms. Proposed Combi envelopes are 12 x 4 x 3.5 m; can filler/seamer envelopes are 7 x 3 x 3 m. These dimensions are assumptions, not manufacturer general-arrangement measurements.

This is a **connected map schematic, not a buildable or commissioned process design**. OEM layouts, actual throughput/SKUs/cap finish, sanitary piping/CIP recipes, utility ratings, structural openings, enclosure/hygiene, maintenance and escape clearances, guarding and conveyor traffic require site engineering approval. Route and nominal-footprint checks do not certify those clearances or sanitary design.

Research basis: [Sidel complete CSD lines](https://www.sidel.com/en/about/media/press-releases/csd-complete-lines/) and [Sidel CSD line portfolio](https://www.sidel.com/en/complete-lines-repository/csd-lines/), including integrated Combi and Blendfill concepts. The proposed map does not assert a particular OEM configuration is installed.

`--factory-map-editor-unit` also validates seven line recipes, existing IDs/links/fire/door preservation, nominal equipment footprints, routed solid-wall crossings, building opening validation and embedded-geometry save/load. Set `EOEMS_PRODUCTION_PLAN_INPUT` to a directory containing both saved layout/building documents and `EOEMS_PRODUCTION_PLAN_OUTPUT` to a separate staging directory to export candidate documents, the connection graph, assumptions and per-line plan/3D previews. Never point the output at the live data directory. Without these variables the tests use a synthetic seven-room fixture and do not edit the application map. Deploying a reviewed proposal requires backing up both current documents and closing the application before replacing them.

The add-item library also includes wall/floor panels and switchboards with or without energy meters, ATS cabinets, motor control centers, motors, VFDs, single/twin pump skids, storage tanks, air receivers, PLC cabinets, HMI consoles, standalone energy meters, pressure/temperature sensors, stack beacons, cooling towers, ventilation fans, pipe sections, valves, cable trays, cameras/dome cameras, access readers, fire panels, extinguishers, hydrants and smoke detectors. Select the category and shape in **Add Item**, leaving the link as **None - visual item only** for equipment without an acquisition source. These are optional SCADA symbols, not newly discovered or automatically installed factory equipment. Screens, indicator colors and safety symbols are schematic, not invented readings or alarm states. New categories have their own visibility controls and map-summary counts.

**Water Systems** adds compact and twin-rack ultrafiltration units, reverse-osmosis skids, sand/carbon filter vessels, water softeners, UV sterilizers, chemical dosing, booster-pump skids, treatment control panels, stainless storage tanks/process vats, and CIP cleaning skids. **Filler Room** adds PET blowers, rinsers, rotary fillers, cappers, labelers, shrink wrappers, accumulation tables, control panels, overhead air conveyors and conveyor corners. Air conveyors/corners use the adjustable conveyor-length field. Add as many units as needed; **Duplicate** makes a new visual copy with a unique ID and no acquisition link. Link it explicitly only if that unit actually exists in the system. These additions never create real installed equipment or fabricate telemetry.

**Filler Room** also includes twenty optional ancillary/line choices:
- Feeding: **Preform elevator**, **Cap elevator**, **Cap sorter and chute**, **Bottle unscrambler**, and **Empty container depalletizer**.
- Filling/hygiene: **Rinser filler capper monoblock**, **Cap sterilizer**, **HEPA filler canopy**, **Can seamer**, and **Returnable bottle washer**.
- Product and cleaning services: **Product buffer tank**, **Sanitary product pump**, **Sanitary valve manifold**, **CIP return skid**, and **Foam cleaning station**.
- Conveying and quality: **Air conveyor blower**, **Bottle air knife dryer**, **Bottle reject station**, **Inline checkweigher**, and **Inline metal detector**.

A typical PET layout runs preform hopper/elevator -> bottle blower -> air conveyor/blower -> rinser/filler/capper (separate machines OR monoblock) -> inspection/reject -> dryer/labeler/coder -> packing/pallet handling. Cap elevator/sorter feeds the capper; product buffer/pump/valve manifold feeds the filler; the existing CIP skid under Water Systems and optional return skid support cleaning. Utilities already available elsewhere include treated water, 7/40-bar air plant, cooling/chillers and electrical/control panels. Glass/can, returnable-bottle, cap-treatment and hygienic-canopy equipment are process-specific alternatives, not requirements for every room; a HEPA canopy does not imply a validated aseptic installation. Select actual production-line requirements rather than adding all symbols. These fixed ancillary items have individual approximate metre-scale envelopes (not verified supplier dimensions); their depicted belt/chute connections are schematic and do not auto-connect or run equipment. The existing extendable Conveyor/Air conveyor remains available for joining them. New symbols do not automatically change saved room layouts, system inventory, acquisition links or existing integrated-line geometry. Small cylinders/bottles on machine drawings represent details, not additional selectable equipment.

Dragging equipment near another item's edge snaps it flush without moving the neighboring item. Building floors, stairs and free-standing openings snap to nearby edges of the same element type; walls snap endpoints and T-junctions, with nearby footprint-edge alignment as a fallback. Equipment snapping uses the rotated/resized schematic footprint; non-cardinal elements use conservative axis-aligned footprint bounds. Snapping is local proximity alignment, not an automatic global layout or a physical collision solver. **Layers > Snap nearby edges** disables it, and holding **Alt** bypasses it temporarily. Keyboard nudges bypass snapping for precision.

Item rotation turns only the horizontal X/Y plane: height remains upright at 0/90/180/270 degrees and intermediate headings. The item preview and map share the same horizontal transform, including the generator's original mounting orientation. Equipment faces and details are painted in camera-depth order so turning the model does not incorrectly expose rear/lower components over its roof. The item dialog includes four cardinal-heading shortcuts.

Only Good existing PLC meter and UPS SNMP snapshots are shown as live readings, resolved by linked equipment ID or the independent panel-meter acquisition ID rather than the map item's name. Failed/stale/unavailable readings remain `--`; a link or a model does not itself establish a working connection. Other equipment remains **Unknown / Not Connected**, and KPI values without an integrated verified source remain `--`. The map summary counts current map items (live / total), not a fixed inventory.

The **3D View** projects the solid site ground and adds transparent, extruded room walls. Initial walls and room-floor footprints are manually approximated from the reference plan, including the warehouse, syrup/storage/cold/lab rooms, generator/compressor/panel/battery areas, water treatment/filtration, production rooms, offices, reception and HR; clearly depicted door gaps are left open. This is a provisional trace, not a CAD reconstruction, surveyed building model or validated evacuation plan. **3D walls**, **Room floors**, **Openings / stairs**, **Site ground**, **Layers > Roads** and equipment-category layers have independent visibility. **Wall opacity** adjusts transparency from 5-60%. **Dimensions** stores provisional equipment sizes and seed-wall defaults in `data\factory-map-dimensions.json`; once using the building editor, edit each existing wall's height and thickness in **Building > Properties**, not the seed-default rows.

**Building > Add > Road** creates movable, rotatable, resizable asphalt/concrete road elements. Choose dashed yellow center markings, double yellow center, unmarked pavement, pedestrian crossings or parking bays; width, length and surface color remain editable. Six provisional connected segments approximate the original upper/lower driveway routes. Nonempty version-1 building files gain these roads once in memory; saving writes version 2. Once saved, deleting roads does not respawn them, and intentionally empty legacy buildings remain empty. Road visibility is independent of walls, floors and openings. Road placement is schematic, not a surveyed traffic or safety plan.

Administrators use **Building**, separately from **Edit Map**, to add, select, rename, delete, move, resize and rotate **Wall**, **Floor**, **Door**, **Window** and **Gate** elements. Select directly or use the building list to distinguish overlapping segments. Drag an element to move it; square handles change length (and floor depth), and the round handle rotates free-standing elements. **Properties** provides exact position, length, depth/thickness, height, sill/base height, rotation, color, material and style with a live preview. Walls support concrete, brick, glass, steel and wood appearances. Room floors support rectangular and L-shaped footprints with distinct tile, concrete, asphalt, sand, gravel and epoxy finishes. Changing material selects a matching default color; choose a custom color afterward if desired. Edit/rotate the footprint to fit the room; these are approximate visual surfaces, not inferred construction measurements.

**Building > Add > Stairs** provides straight, left/right L-turn and left/right U-turn flights, with steps, landings and schematic railing. Length, depth, rise, material, color and rotation are editable, including mouse depth/length handles and all four plan headings. The first flight rises along local positive X; left/right turn styles change the plan, never invert the rise. These are schematic stairs, not code-compliant construction or egress designs.

**Building > Rooms** opens a checked element list to create a room from selected walls/floors: at least three wall segments, or a floor with at least two walls. Choose a room name and Apply; the same dialog's existing-room selector lets you rename the room or adjust its members. Selecting a member highlights the room's other visible members and shows the room name in the inspector/hover text without adding permanent equipment labels. Attached openings inherit their wall's room. Room groups use stable IDs and persist with Building Save; Cancel restores previous membership and names. A member can belong to only one room. Room creation is explicitly user-selected, not automatic architectural detection or proof of enclosure.

Door materials include glass, steel and wood; styles include single leaf, double leaf, sliding and rapid roll-up. Windows include fixed, casement and sliding styles. Gates include single/double swing, sliding and rapid roll-up. Opening direction selects left/right hinge or sliding side and inward/outward swing; **Open** changes the visual geometry, and rapid doors raise their curtain vertically. Attach a door/window/gate to a wall and set its center offset along that wall: the opening cuts the wall, preserves window sills/lintels, and follows wall movement/rotation. Attached openings drag along the wall; detach in Properties for free movement/rotation. Openings must fit within wall length/height and must not overlap. Deleting a wall explicitly confirms deletion of attached openings. All open/closed states are visual only: no commands are sent to gates, access control or equipment.

**Building > Save** persists the full building independently in `data\factory-map-building.json`, with a previous-version `.bak`. **Cancel** restores all building geometry, additions/deletions, attachments, finishes, colors and opening states without touching equipment. Empty saved buildings remain empty. A missing file seeds provisional traces and floor footprints without changing existing equipment layouts; an invalid/unreadable file reports an error and disables building editing rather than overwriting it. Equipment and building transactions cannot run simultaneously. For optional free-standing symbols, **Edit Map > Add Item > Access Control** also offers swing/sliding/rapid gates and glass/wood entry doors with visual opening-state/direction controls; use Building for openings attached to walls.

The page uses the shared SCADA action buttons, bordered summary cards, compact camera/display toolbars, a **Layers** menu and a scrollable equipment summary/inspector. Summary cards show verified factory load, map-item count, placed/unplaced counts and Good linked acquisitions; unavailable energy/fuel/cost/alarm integrations are not shown as placeholder KPI cards. Top View returns to the untransformed image, where wall height collapses to its footprint. Middle-drag pans in any mode; ordinary blank-space left-drag also pans, including in Edit Map. **Ctrl+left-drag** selects equipment regions in Edit Map. Shift+left-drag or right-drag orbits with fine camera sensitivity. The mouse wheel zooms around the point under the cursor, up to 10x. With the canvas focused and an element selected in Edit Map/Building, arrow keys nudge it by one schematic plan unit on X/Y; Shift+arrow moves five units. Attached openings move along their wall, subject to fit/overlap validation. Without an editing selection, arrows pan the camera; Ctrl+arrows always pan, and Shift speeds up camera panning. Arrow keys in text boxes, lists or numeric editors retain their normal behavior; click the canvas element to give the canvas focus before nudging. Mouse capture keeps direct dragging reliable outside the canvas and releases on mouse-up or capture loss. Chillers initially remain unassigned until placed by an editor. Showing/hiding walls or layers does not change item positions, links or saved data.

## Configuration, SNMP settings and historian workflows

Alarm limits use one decimal place and a 0.1 increment/decrement per arrow press for every voltage, load and temperature threshold across equipment groups. Existing validation, role permissions and audited saving are unchanged.

**UPS input estimates (balanced assumption)** is an analyzer quick-selection preset. The visible **Add separate estimated input V / kVA** checkbox is enabled by default: starting with measured UPS input voltage/current/power selected also checks the two distinct estimate channels. Disable it for strictly measured-channel selection; existing quick presets themselves still select measurements only. No measured channel is replaced. Input L-N **equivalent** voltage is `mean(AB,BC,CA)/sqrt(3)`. Input apparent power is `sum(V1-N*I1,V2-N*I2,V3-N*I3)/1000` in kVA when three verified measured L-N phases exist; otherwise it is the balanced L-L estimate `sqrt(3)*mean(L-L)*mean(I1,I2,I3)/1000`. An invalid/incomplete authoritative voltage reference never silently falls back to another reference. All required phases must be finite, nonnegative, within mapped ranges and from one Good acquisition; valid zero remains zero. L-L estimates do not establish actual individual phase-neutral readings or apparent power under imbalance/distortion. No output PF is reused as input PF, and no input kW is fabricated. Estimates have distinct labels, **ESTIMATED** chart captions, CSV quality/assumptions, PDF basis and UPS detail rows. Historian JSON version 2 additionally records L-N source magnitudes and the apparent-power method; Equipment History accepts versions 1/2 and reads only recorded estimates, never backfills old records. Estimates are excluded from loss/efficiency, capacity/compliance and phase-balance conclusions, aggregation and energy accounting.

The analyzer retains all measurement choices for selected equipment instead of hiding them based on the latest snapshot. Unsupported channels remain explicitly unavailable in Readings/CSV/PDF, not zero or made-up values. Empty live graphs show their channel-specific acquisition/availability reasons directly, including the measured-reference alternative and the distinction between estimated input kVA and unavailable measured watts, rather than only a generic no-data message. A measured phase-neutral voltage, balanced-equivalent estimate and active input watts are different channels.

The graph and time-window filters sit beside Export in the top action row, wrapping on narrow windows rather than clipping controls; there is no separate graph-toolbar row or CSV/PDF path banner. Initial equipment/channel instructions and the idle prompt are omitted without reserving blank label space. Active recording status, validation errors and PDF completion/export failures remain visible. Recording destinations and Open folder remain in the save controls.

The analyzer graph stack uses the **explicit sum of row heights, per-graph margins and bottom padding** for both its height and scroll extent. This avoids an auto-size scroll range that lost six pixels per graph (240 pixels with 40 graphs). Large-stack tests cover 80 graphs at multiple viewport sizes, not just intersection of a small final graph. The final time axis and border have bottom clearance. Shared chart Y-axis labels adapt decimal precision to the tick interval, keeping narrow PF/voltage ranges distinguishable instead of repeating rounded labels.

Input-estimate validation: `dotnet run --project Tests\SnmpValidation\SnmpValidation.csproj -- --ups-estimate-unit "<output-directory>"` checks formulas, zero/missing inputs, separate CSV/PDF evidence, explicit selection and electrical-analysis exclusions. `--ups-analyzer-unit` also exercises estimate dependencies through real local SNMP GETs; `--equipment-history "<output-directory>"` checks the actual isolated PostgreSQL write/read and older records without estimates.

Normal background UPS polling retries incomplete capability discovery after one minute rather than keeping a partial map for the full six-hour capability cache lifetime. Selected-channel analyzer requests continue using verified fields without performing slow discovery. Known identity timeouts and verified non-UPS agents are reported as Timeout/NotSupported instead of a misleading perpetual verification wait. A timeout does not establish missing hardware support or a supply outage.

The online analyzer has five direct workspaces: Graphs, Readings, Events, Sampling & start, and Event capture. Sampling and event options are no longer nested under Setup; their controls, trigger behavior and recording-state locking are unchanged. Tables do not reduce graph height. Readings/events keep readable column widths with horizontal scrolling and the readings channel stays pinned. Expand workspace hides the equipment/channel selector while Start/Pause/Stop remain available; recording, selection and exports are unchanged. Equipment History separates Records, Graphs and Statistics; its Expand workspace also hides period filters and axis controls until Show controls is pressed, preserving the chosen comparison. Multi-series legends wrap above the plot; history comparisons keep a readable main plot and scrollable companion time charts.

Analyzer **UPS power balance evidence** quick selection includes system output kW/kVA/PF and phase input/output W, V, I and battery evidence. This is an explicit channel choice, not hidden auto-recording; unsupported fields remain missing. Each UPS gets a plain-language numerical power-balance conclusion in its evidence section and overall conclusion. Missing input/output measurements explain why loss/efficiency cannot be calculated and suggest selecting system output total where per-phase W are unsupported. Invalid analyzer source acquisitions remain in power-pair candidate counts. Observed AC difference/input percentage is not proven conversion loss; no cross-equipment efficiency/loss ranking is inferred.

UPS missing-channel explanations now carry recorded capability, invalid-value or voltage-reference evidence into analyzer quality/CSV, the PDF measurement-basis table, UPS detail and new historian records. Older records without this evidence remain explicitly unknown. Valid numeric zero input V/A/W stays zero; `NoSuchInstance`, `No Support` and timeout never imply zero or a supply outage. Some NXL cards provide input L-L and current but no verified L-N or active input watts; select actual L-L channels, never synthesize L-N or input power. Native input/bypass aliases prefer valid L-N phases (including zero), then real L-L if all L-N phases are absent; references are never mixed phase by phase. Validate with `dotnet run --project Tests\SnmpValidation\SnmpValidation.csproj -- --ups-channel-unit "<output-directory>"`. The read-only `--ups-channel-live "<configuration-directory>" "<output-directory>"` audits selected input acquisitions across enabled UPS devices without changing equipment state; unreachable/unsupported devices remain reported as such.

Electrical report knowledge follows seven ordered gates: scope/identity, quality/time, electrical topology, calculation prerequisites, observed values/reference provenance, hypotheses/missing proof, and bounded conclusions/output checks. These instructions appear in every PDF; shared investigation rules are also used by actual findings. Causes remain unestablished, not automatic motor/capacitor/tap/cooling-failure diagnoses. Generic thresholds and inferred 50/60 Hz are labeled screening references, not approved site/manufacturer limits. Impossible PF is reported explicitly and not clamped; DC power does not trigger AC above-average load rules. Input/bypass apparent power is not compared against an output nameplate rating. Coincident recording gaps are merged only within one equipment, preserving source identity.

Cross-equipment peer comparisons/rankings and unrelated overlays are disabled, even for matching units/models. Analyzer graph groups belong to one equipment; combined Equipment History keeps all selected rows but its trend selector identifies one equipment/channel. PDF comparisons and coincident-extreme groups stay within one equipment. Transformers, UPS units, generators and ATS equipment are not interchangeable peers. A future upstream load-contribution percentage requires an approved time-valid feeder mapping, actual ATS/source position and aligned measured downstream AC **input**/upstream power; different or unknown feeds prohibit calculation. Current ATS bank assignment/source description is shown as context only, never parsed into proof that a transformer or generator was actively supplying the load. Own-nameplate utilisation is not upstream load share.

UPS PDFs include a model-aware input/output evidence section. Active powers and phase-current/PF differences require exact same-device acquisition pairs (historian timestamp, observation ID, acquired time and revision; analyzer actual source timestamp). Complete measured phase W are summed into kW only when every phase is present; kVA and current ratios never substitute for active power. Duplicate, invalid, unpaired or ambiguous readings are excluded explicitly. The AC transfer ratio is a ratio of paired power means, not energy-weighted efficiency. `Pin - Pout` is an observed difference, not proven conversion loss: true efficiency and loss rankings remain unestablished without verified operating mode, battery energy balance/sign convention and comparable loading. Current model/rating metadata is labeled as current, not historical verification or a manufacturer datasheet.

Input system PF may be derived only from same-acquisition measured active Pin and all three verified L-N rms voltage/current products (`S = sum(Vphase * Iphase)/1000`, `PF = Pin/S`). Unknown/L-L references, incomplete/ambiguous phases and PF outside 0-1 are withheld, not guessed or clamped. The electrical-knowledge validation includes UPS power-balance regressions. To re-render an original analyzer CSV without changing it: `dotnet run --project Tests\SnmpValidation\SnmpValidation.csproj -- --ups-power-balance-csv "<csv>" "<installed-app-directory>" "<new-output-directory>"`; it requires per-equipment acquisition/quality columns and labels current registry model/rating provenance.

Configuration, SNMP Setting and Historian / Reports use the shell's single page title and start directly at their section navigation. Forms scroll without hiding their primary save actions:

- **Overview:** includes a read-only SNMP tile showing the number of enabled UPS units with a current Good reading; offline, waiting, and registry-load errors are identified explicitly.
- **Daily exports:** an existing daily Excel report is validated and reused rather than overwritten or repeatedly reported as a failed export.
- **Configuration:** includes **PLC & display** settings (admin-controlled, audited, read-only register map), **Alarm limits**, **Fire connection**, **System diagnostics**, **Backup & recovery**, and **Settings history**. Existing services, audit/version history, backups, and stored settings remain in place. Fire communication remains governed by its existing commissioning checks; opening the section does not initiate a connection or change panel settings.
- **UPS & SNMP settings:** **UPS Registry** separates **UPS units** from **ATS banks**, giving the unit list more room. **Advanced columns** reveals UDP port, rated kVA, timeout, retries, notes and profile-transport override; hiding them never discards their values. **Bulk profile assignment** stages changes until **Save registry** and uses the configured bank numbers. Card subpages use the same navigation style; profile save/assign/test actions stay outside the scrolling editor. Blank-secret preservation and protected-device confirmations are unchanged.
- **Historian & reports:** keeps **Acquisition**, **Raw recording**, **Daily reports**, **Active events**, **Equipment history**, and **Fire Events** in a fixed-size workspace. Raw recording's resolution/planning panel remains collapsed until explicitly opened; daily measurement selection uses fixed layout rows. Equipment history retains unified transformer/UPS tables, trends, statistics and CSV/Excel/PDF. Daily schedules, job history and per-transformer measurement selection remain available and do not restrict raw Equipment History.

Integer SNMP settings reject fractional, non-finite and out-of-range values rather than rounding or overflowing. Continuous kVA/display ratings retain their saved precision. Uncommitted grid edits and invalid identity-verification selections block saving/testing with an explicit error. Viewer/offline-preview settings remain read only; report preview/export remain available.

Recording-policy saves check folder writability only when automatic daily export is enabled. Disabling daily export does not require the disconnected destination to be writable. Edits made while an earlier asynchronous policy save is pending remain marked **unsaved**, rather than being incorrectly acknowledged.

PostgreSQL read failures now propagate as errors to previews, exports, coverage and daily scheduling, rather than masquerading as an empty historian. An unavailable database must not permanently mark an unexamined day as `SkippedEmpty`. Successful empty queries still mean no recorded data. Failed exports leave no completed or partial report file.

Equipment-history trends account for the selected Auto/Step sampling interval per equipment. Long-period readings remain visible, including isolated samples, while real acquisition gaps and invalid readings break the line. Chart-only point reduction preserves continuity from the original loaded readings and does not change saved records or statistics.

The history channel sidebar sizes its header rows from their contents, including the wrapped trend caption, dropdown and Select visible/Clear actions, so they do not clip or overlap the channel checkboxes.

Transformer cards and their detail windows display Transformer 1-4 while preserving the configured acquisition/history IDs. Total Load uses the same row order as each transformer: load, voltage L1-L3, current L1-L3, frequency, power factor, energy and temperature. Total voltages remain averages and phase currents remain sums; the detail table shares the card's labels, units and readings.

Online Analyzer defaults to **0.1 s** sampling and offers **0.025 / 0.1 / 0.25 / 0.5 s** targets as well as slower recording intervals. Selected UPS channels are read by independent, bounded background GET loops using the card's verified OID/unit mappings; normal UPS measurement/alarm polling and historian input are unchanged. Channel values and liveness are batched together (up to **32 OIDs per selected-channel request**, keeping typical complete input/output selections in one PDU; full background requests retain 24). Each selected endpoint has its own loop, rather than sharing the normal monitoring concurrency limit; the shared per-endpoint gate still prevents overlapping requests within the application. Monotonic scheduling corrects timer drift without issuing catch-up bursts. Analyzer requests omit alarm walks and retries; worker acquisition budgets, including endpoint queueing, are twice the selected interval, clamped to 100-500 ms (**200 ms at the 0.1 s target**). Initial capability discovery remains a separate background prerequisite and is shown explicitly while waiting. The status shows the requested interval, actual request duration and last successful response age; network/card speed and Windows scheduling can exceed the requested interval. Requests exceeding the acquisition budget produce missing/timeout readings, never repeated last-good samples; a valid response arriving later than the selected target retains its actual timestamp. Host request cadence is not proof that the UPS internally refreshes its measurements at the same speed.

Under each live graph, the axis caption identifies the target seconds, median actual valid-sample interval and maximum gap from the latest 100 valid observations per signal. UPS live traces join communication timeouts/late polls with a **dashed visual link between actual observations**, never an invented zero, held value, new recorded point or extrapolated tail. Unsupported/invalid-channel observations still break the trace. CSV, recording statistics, history and PDF evidence retain the real missing rows and timestamps; report metadata explains the display-only distinction. Other shared charts keep their existing non-bridging default. Run `--ups-cadence-live "<configuration-directory>" "<output-directory>"` for read-only full-signal fleet measurements and a simultaneous 0.1 s worker audit. Stop other high-rate recorders before this diagnostic so duplicate clients do not overload the same cards.

To isolate source communication from full-payload processing, `--ups-cadence-minimal "<configuration-directory>" "<UPS-ID>" "<output-directory>"` measures one frequency channel plus identity/liveness at the same target. This read-only diagnostic does not establish a hardware/network cause from timeouts alone and never reports an achieved 0.1 s interval merely because that target was configured.

Analyzer responses are queued independently of display refresh, so temporary UI stalls do not replace intermediate measurements with the latest snapshot. The 10,000-response buffer is bounded; any overflow stops recording explicitly and the report discloses the lost-response count. Pause pauses capture and analyzer-only workers; Resume starts fresh workers without replaying paused readings. Stop atomically cancels analyzer workers and drains already accepted responses. CSV appends per-equipment acquisition UTC columns after the existing values/quality columns; absent equipment stays blank. ISO timestamps and PDF evidence retain source fractional seconds. Each report channel counts only its own acquisitions, and invalid evidence breaks continuity. TOTAL requires exactly T1-T4, matching positive acquisition-cycle identities when provided, and source times within two seconds.

Selecting a UPS output line-to-line channel explicitly requests the verified `OutputVoltageLL` binding; selecting line-to-neutral alone keeps the smaller `OutputVoltage` request. Unsupported voltage references remain unavailable rather than being converted or guessed.

Long analyzer runs spill complete 200,000-point blocks to temporary binary evidence instead of discarding alternate samples. PDF analysis restores all blocks on a background task; generating very large reports still requires memory/time proportional to the retained evidence. Temporary blocks are cleaned after their report is replaced or the view closes. PDFs are promoted from partial files only after successful creation. Normal application shutdown waits for active analyzer CSV/PDF finalization before disposing views and services; abrupt process termination or power loss cannot guarantee finalization. Validation: `--analyzer-recording-stress "<new-evidence-directory>"`, `--history-stress "<new-evidence-directory>"`, and read-only `--ups-analyzer-live-stress "<configuration-directory>" "<UPS-ID>"` (100 real requests at each of 25/100/250/500 ms).

The Online Analyzer trace starts at the **left** of the selected fixed-width time window, fills toward the right, then scrolls at the same scale. Clear starts a new display sweep; Pause/Stop freeze the viewport. Visible waveform frames are requested approximately every 30 ms independently of the 500 ms statistics/status refresh; actual frame rates depend on UI load. A monotonic-clock reveal animates only the line between already acquired samples over 25-120 ms; it never extrapolates, creates recorded samples, changes acquired values, or bridges invalid/unsupported channel readings. The explicit dashed communication-link presentation above remains display-only during animation. Tooltips still identify real measurements only. Other charts remain unchanged unless their owner enables the reveal. Validate with `--analyzer-trace-unit "<evidence-directory>"` in the existing SnmpValidation harness.

The analyzer defaults to **All selected channels**, with one scrollable graph per native-unit/measurement family and at most six signals per graph. Input voltage/current, output voltage/current/power, L-N vs L-L, battery/DC and DC-bus readings are separated. A visible counter identifies all selected signals and graph count; each graph identifies how many signals have measured evidence, while every selected channel remains in the quality/statistics table and CSV/PDF. A unit filter changes only the graphs, never recording or table membership. In multi-graph mode the wheel scrolls graphs; Ctrl+wheel changes the shared time window. Empty event tables do not reserve chart space. Animation retains controls and point arrays between acquisitions. Validate layout, exact graph membership and recording/export using `--analyzer-graph-unit "<new-evidence-directory>"`.

Dense analyzer graph reduction retains bucket min/max, the first/last observation and both sides of invalid-run boundaries rather than taking every Nth reading. The normal 3,600-point visual target can be exceeded when required to preserve missing-evidence breaks; CSV, report evidence and statistics are never reduced.

Equipment History measurement trends use the same left-to-right reveal when loaded or when the combined-history trend selection changes. The fixed-axis sweep completes in approximately 1.2 seconds with frames requested every 30 ms, then leaves every recorded point visible for comparison. Zoom, mouse interaction or leaving the page immediately completes the reveal; returning does not replay it. Empty/single-time results are shown immediately. Alarm events remain observed markers, not continuous signals. Tables, statistics, exports and PDF reports always use the complete saved data, independently of this display animation. Validate with `--history-trace-unit`.

For a single equipment, Trend & statistics also offers **Y Channel** and **X Channel**, defaulting to all checked measurements versus **Time (UTC)**. Check both desired channels and load records, then select e.g. Power (Y) versus Temperature (X); **Swap X / Y** reverses them. A non-time X displays a numeric scatter plot, with wheel X-zoom, drag panning and double-click reset. Pairs require the same equipment, exact saved timestamp, observation identity, acquisition timestamp and mapping revision, with one finite Good value per channel; missing, ambiguous or nonmatching readings are excluded, never interpolated or nearest-time joined. The matched count is displayed. Interval peaks cannot be treated as simultaneous raw values, and alarms remain event evidence in Records. Combined equipment comparisons remain time-based. CSV/Excel and statistics still contain all checked loaded channels. PDF additionally captures the selected X/Y axes, comparison mode, exact-pair/exclusion counts, response table, generated comparison chart and optional aligned time traces over the **full loaded period**, not the interactive viewport. UI and export share the pairing/binning/plotting logic. Validate with `--history-xy-unit "<evidence-directory>"`.

**Comparison readability:** the default **Mean response + observed range** overlays sample-weighted Y means and observed Y min/max bars on transparent measured pairs. With up to 12 distinct X values, each recorded level is summarized separately; otherwise 12 equal-width X bins are used. Each populated level/bin shows its sample count; means with fewer than 3 samples remain grey and unconnected. Only adjacent supported means are joined; empty/sparse bins break the line. Bars are observed spread, **not confidence intervals**, and lines are descriptive guides, not fitted physical models or predictions. Aggregates have explicitly labelled tooltips, not invented acquisition times. Summaries use all loaded pairs and do not refit when zoomed.

**Chronological path** instead connects pairs in acquisition order, with blue-to-orange time colour and direction arrows. Missing/unmatched acquisitions, nonpositive time differences and gaps above 2.5 times the effective recorded/loaded interval break the path. **Raw measured pairs** remains available. Optional companion plots show both matched channels on vertically aligned UTC axes with their own native-unit Y scales; wheel zoom, drag and reset are synchronized between the time plots. No samples are time-shifted or thermally modelled. To investigate load/temperature response, use load/current as X and temperature as Y and inspect both time traces: temperature can lag loading, ambient conditions/cooling can affect it, and correlation is not causation. The TDEGC sensor source remains unverified, so it is not labelled winding or hot-spot temperature. Quantized temperature values may legitimately produce only a few X levels; the display never fabricates finer sensor resolution.

Design references: [NIST scatter plots and the limits of causal interpretation](https://www.itl.nist.gov/div898/handbook/eda/section3/scatterp.htm), [EU data visualisation guidance on overplotting/transparency/binning](https://data.europa.eu/apps/data-visualisation-guide/binned-scatter-plots), and [IEC 60076-7 loading-guide scope](https://webstore.iec.ch/publication/29367). These inform presentation and cautions, not a claim that this exploratory chart implements a certified IEC thermal model.

UPS voltage choices respect the card's verified reference. A card reporting only input L-L voltage does not offer input L-N channels once a good snapshot is available; no voltage conversion is guessed. When both references exist, each channel uses its own measured series. Channels selected before verification that remain unavailable show an explicit per-channel reason in the grid, a status warning and unavailable channel names in CSV quality, rather than blank values marked Good.

Raw-recording storage estimates use average PostgreSQL row sizes and median positive spacing from up to 64 recent saved rows per active asset/table. Rate samples require at least 64 rows within the last hour for transformers or 24 hours for UPS units. Median spacing prevents occasional app shutdowns or network gaps from understating sustained recording growth. The selected interval scales transformer history growth; UPS growth follows its independently recorded cadence. If the sample is too sparse, the estimate falls back to configured PLC/UPS polling. Both paths retain 2x headroom; forecasts exclude existing history, WAL, backups, exports and offline outbox, and are not worst-case guarantees or deletion policies.

Read-only production checks (results stay local; no settings, schemas or recording rows are changed):

```powershell
dotnet run --no-build --project .\Tests\SnmpValidation\SnmpValidation.csproj -- --live-recording-audit "<App directory>" "<evidence directory>"
dotnet run --no-build --project .\Tests\SnmpValidation\SnmpValidation.csproj -- --live-ups-fleet-audit "<App directory>" "<evidence directory>"
```

The database audit forces PostgreSQL read-only sessions, verifies recent history projections, checks 24-hour duplicate identities and measures row sizes. The fleet audit issues read-only SNMP requests to enabled configured UPS devices; it temporarily copies protected credentials only into the validation output directory and restores that file afterward. Missing/offline/unsupported channels are reported, not manufactured or repaired by these commands.

Isolated WinForms regression checks cover all main tabs at **980x600, 1160x680 and 1560x900 content sizes**, field/action visibility, SNMPv3 fields, metadata scrolling, numeric validation, precision, role restrictions, save races and database/export failure paths. They use temporary databases, callback spies and a deliberately unreachable loopback PostgreSQL endpoint; they do not scan production cards or save production settings. This does not substitute for an end-to-end export against the site's available PostgreSQL data or a live hardware/credential test.

```powershell
dotnet run --project .\Tests\SnmpValidation\SnmpValidation.csproj -c Release --no-restore -- --settings-ui "<screenshot-output-directory>"
```

## Fire alarm connection settings

The **Fire connection** Configuration section opens the cloud-first **Zeta Remote - Fire Alarm
Cloud Integration** editor. All three connection editors share a status card, two aligned settings
cards, the same full-height workspace, and a fixed bottom action dock. The seven primary actions
(Test Connection, Save Settings, Connect, Disconnect, Reconnect, Clear Diagnostic Logs and
Copy Diagnostics) retain identical order, size and position when switching protocols; mode-specific
actions occupy a reserved second row. Desktop layouts at 1300x600, 1300x650 and 1620x830 content
sizes fit without scrolling, including vendor BMS and independently verified BMS Modbus.
Status and command results remain explicit; ellipsized status/result text can be read by hovering.
**Connection type** directly selects **Zeta Remote Cloud API**,
**Local Modbus TCP**, or **Zeta BMS** settings on this page; there are no lower event, diagnostics or
Advanced tabs. **Copy Diagnostics** sits beside **Clear Diagnostic Logs** and copies the real
credential-free service snapshot; clipboard failures are reported. Smaller windows retain scrolling
for settings without moving the action dock; narrow windows reflow to one column. The panel-network
reference fields use one compact row; hover over Network mode for Local/Global reference details.
Opening this editor neither changes the saved primary protocol nor starts communication.
Site `78392d300758`, name `Twellium-Beverage`, MAC `78:39:2D:30:07:58` and panel/module metadata are
supplied references, not discovery or a verified live panel identity. Edit and verify the site ID/name
before authorized testing; changing the site ID removes the reference MAC association.

The existing service owns the single polling worker and shared request gate for both protocols.
The **Local Modbus TCP / Zeta BMS** editors are selected directly in Fire System; BMS retains its
vendor-protocol/independently verified Modbus choice. Switching the visible editor does not save
settings or start communication. Saved legacy hosts, profiles, credentials and diagnostic logs are retained. The new Cloud
Save merges the latest backend settings rather than overwriting cached alternative profiles.

- **Local Modbus TCP / BMS Modbus TCP:** a dedicated background service issues only
  read-only **FC04 input-register** requests. Configure the confirmed IP/hostname, port,
  Unit ID and **zero-based** contiguous start/count (1–125 registers). Identify the
  approved mapping document/version, tick **Vendor FC04 mapping reviewed**, then
  **Test Connection** and **Save Settings**. Reviewing the document alone is not protocol verification:
  a successful, structurally valid FC04 response is also required for that exact mode,
  host/port, Unit ID, register range and mapping reference. Windows-protected test evidence
  is retained with the reviewed read range and checked before raw polling. It proves FC04
  framing only, not the correctness of register definitions or fire-status interpretation. Changing those fields
  requires another review/test; old checkbox-only verification is invalidated on load.
  The confirmed Local panel IP default is `192.168.107.90`; the current known result is
  ping success (0–1 ms) and `TCP CONNECTION REFUSED` on port `502` (supplied prior evidence,
  not a new hardware test; no timestamp was supplied).
  Port `502` remains editable and unverified; register 0/count 1 are unverified placeholders,
  not an assumed panel-status mapping. Retry count defaults to 3, retry interval to 10 seconds,
  and polling to 5 seconds. Startup auto-connect is off by default.
  The panel's `Enabled Local`, address `1`, and name `PANEL NAME` are manually recorded
  references, explicitly not live-verified and never written to the panel. Monitoring is
  limited to this panel; no remote-panel mapping is assumed.
- **Zeta Remote Cloud:**
  The operator-supplied `AuthActions` and `APICall` source confirms two distinct credentials:
  login returns `auth.session`, which authorizes the claims POST; the returned `auth.claims`
  authorizes event GETs. The UI therefore calls the saved credential **login session token**
  (older documentation below calls it account token), not a manually issued API key.
  Existing protected storage is retained without migration. **Sign in to Zeta** is an explicit,
  administrator-only manual action: a separate modal dialog takes the operator's Zeta email
  and masked password, POSTs JSON `{email,password}` to the source-confirmed
  `/api//auth/login`, saves the email, password and returned login session token using Windows DPAPI CurrentUser,
  then exchanges claims and synchronizes event history. It also works when no token has
  previously been configured. Email/password are stored only as encrypted dedicated cloud fields, never plaintext or in diagnostics;
  the password field clears on submission/completion/cancellation and must be re-entered for
  retry. Ordinary managed request memory is used during the request; this is not a guarantee
  of zeroization of all runtime-managed string copies.
  Login stops existing monitoring first, uses the same serialized service/client request gates,
  timeout, no-redirect TLS policy and shared HTTP429 cooldown. The sign-in UI enables Auto Connect
  and Auto Reconnect; these remain changeable in settings. Successful login starts continuous
  polling when Auto Connect is enabled. Startup reuses saved tokens; an expired/rejected token
  can be renewed once per polling attempt using the encrypted saved login when Auto Reconnect is enabled.
  Failed login retains previous credentials and history; if cancellation
  occurs after successful session storage, the saved session is retained, not rolled back.
  Cancel/window close cancels in-flight work before the dialog can close.
  Login success alone is not ONLINE: claims plus a valid, persisted event response are required.
  No registration, password reset, browser-storage access, panel button or configuration-write
  endpoint is implemented. No supplied production credentials are used by automated tests.
  Server errors may use `err`; diagnostics accept that field only through the same safe category
  allowlist, never by copying its arbitrary text.
  The client restricts communication to exactly `https://www.zetaremote.com`
  and two routes: `POST /api//v2/claims/auth` with body `{}`, then
  `GET /api//v2/sites/{site_id}/events?start=yyyy-MM-dd&end=yyyy-MM-dd`;
  both double slashes are preserved. Following the operator-confirmed browser request,
  `start` is UTC today minus 30 days and `end` is UTC today, recomputed per request.
  Date-window rollover never deletes already synchronized local history.
  Redirects and cookies are disabled and ordinary HTTPS certificate validation stays enabled.
  An authorized **account token** is required; old Cloud username/password fields are never reused.
  This is the token used in the Authorization header of claims/auth, not its short-lived returned token.
  The account token remains protected with Windows DPAPI. The exchanged token is held only in
  memory, reused for event GETs, and renewed on the next request when `expires` has 30 seconds or
  less remaining. Malformed, expired or oversized authorization responses fail explicitly.
  Token replacement, settings Save and site changes invalidate the session. Diagnostics expose
  expiry only, never either token. This exchange cannot renew an expired account token: replace it
  through the protected UI, explicitly use Sign in to Zeta, or enable saved-login recovery.
  Recovery uses the same documented login route, never an undocumented refresh endpoint.
  Existing token-only installations need one successful Sign in to Zeta to save the encrypted login;
  previously unsaved passwords cannot be recovered. Dedicated cloud fields are separate from legacy/BMS credentials.
  Token entry is normalized before DPAPI protection and again after loading for requests:
  surrounding spaces and case-insensitive repeated `Bearer ` prefixes are removed; exactly one
  Authorization scheme is attached. Empty/scheme-only tokens, embedded whitespace and control
  characters are rejected locally without an HTTP request. No local JWT issuer, audience,
  scope, signature or account-expiry policy is invented: Zeta decides account-token validity.
  Only the exchanged session's documented numeric `expires` controls the local session cache.
  Blank token entry retains the saved token; reopening never reveals it. Replace/Show affect only
  the new draft, Save clears/remasks it, and Clear Token requires confirmation and removes the saved
  cloud login as well, preventing automatic token recreation. Explicit token replacement also removes
  the previous saved login so recovery cannot silently switch back to another account. Ordinary settings
  saves and temporary outages retain the login. DPAPI requires the original Windows account/profile.
  Polling defaults to 30 seconds (minimum 30), timeout to 10 seconds, and freshness to 90 seconds.
  Cloud polling permits 30-570 seconds; freshness must cover polling plus timeout, up to 600 seconds.
  Requests are sequential, cancelable, and independent of page visibility. HTTP 429 honors
  Retry-After, including manual retries; other failures use capped exponential backoff. Temporary DNS,
  HTTPS, timeout and server errors keep polling/recovery active when enabled; no uninterrupted network
  guarantee is made. HTTP401 or explicitly token-expiry-related403 may trigger one saved-login renewal
  and one new claims/event attempt. Rejected passwords and permission-denied403 stop polling with an
  explicit error requiring account/permission correction, avoiding account-lockout loops.
  **Test Connection** sends no token and reports transport response receipt separately from
  authentication; HTTP 401/403 is never logged as a successful connection. A transport test
  does not judge the saved token. Cloud diagnostics distinguish local credential/storage
  rejection (no HTTP status) from `ZETA HTTP` rejection, retain the exact claims/event failure
  stage and expose only allowlisted server error categories, never raw bodies or token details.
  Without a saved login, or with Recovery disabled, authenticated HTTP401/403 requires manual renewal.
  Failed automatic renewal with rejected credentials also requires manual review. Stopped transport is DISCONNECTED; historical
  HTTPS response evidence is a separate diagnostic flag, not an active connection.
  **Test Authentication** exchanges when required and validates the event response without saving events;
  claims HTTP 200 alone is insufficient. **Test Event Retrieval / Sync Cloud Events** validates and persists
  events. Historian **Refresh Events** reads stored events only. Save precedes authenticated tests and Connect/Reconnect.
  Only validated, successfully persisted event responses set **ONLINE**; stale/failed synchronization
  is DEGRADED/OFFLINE. The live fire panel condition always remains **UNKNOWN**.
  The operator supplied a browser HTTP 200 response for the event route. Its observed array
  fields map as `id` -> event ID, offset-qualified `date` -> UTC time, `category` -> event type,
  `title` -> message and `location` -> description. Returned `hwaddr`, when present, must match
  the requested site. Missing required observed fields or a site mismatch rejects the entire
  batch. Zone/device address stay blank unless explicitly returned; numbers in titles and
  `networkId` are not interpreted as device/zone addresses. The legacy array/`events` wrapper
  fixture format (`timestamp`, `event_type`, `zone`, `device_address`, `description`, `message`)
  remains readable for compatibility. Service events can be filtered alongside Fire/Fault/System;
  categories are retained exactly and never interpreted as the current panel condition.
  Application-token authorization and actual synchronization remain **not live-verified**;
  browser success under another session is not proof of SCADA authorization.
  Unsupported structures, HTML, oversized responses and invalid fields fail explicitly. No guessed
  endpoints, website login, browser-token extraction or undocumented pagination are implemented.
- **Zeta BMS Server / Vendor Protocol:** defaults to the displayed server
  `www.zetaremote.com:3333`, with separate authentication metadata/protected credentials,
  timeout/retries, an explicitly unverified Client/Server role, connection-direction
  metadata and protocol-document reference.
  The vendor mode is **PROTOCOL UNVERIFIED / UNSUPPORTED**
  pending the vendor protocol specification and implementation. The panel's BMS/CONNECTED
  screen does not prove SCADA access, nor establish whether a compatible panel-to-server
  receiving service is required. No port 3333 client or server listener is started.
  Independently verified BMS Modbus is a separate choice with a blank commissioning host
  and standard default port 502; it never inherits the vendor server/port 3333.
  Selecting a role is saved metadata only, not an implemented receiving service.
  Switching the SCADA
  primary interface never changes the panel/module's physical or cloud configuration.

In the **alternative Modbus editor**, **Test Connection** checks reachability and the configured TCP port separately, even when
the Modbus register map is missing. If TCP opens without an approved map, it reports
**TCP CONNECTED - MODBUS REGISTER MAPPING NOT CONFIGURED** and sends no Modbus request.
TCP refusal, timeout, network-unreachable and DNS errors are classified separately, with
the earliest failed communication stage and actual native socket code/detail (for example
`ConnectionRefused (10061)`) reported as the primary result. FC04 uses the
same established TCP connection and only an explicitly reviewed range/reference.
**MODBUS RESPONSE RECEIVED - PANEL STATUS INTERPRETATION NOT VERIFIED** means valid
framing only, not verified register meanings or fire status. Tests do not save settings,
activate monitoring, or change monitoring health.

Alternative Modbus diagnostics have two independent structured groups: **Last Connection Test** retains
its draft target, stage results, latency and UTC test timestamp; **Active Monitoring**
shows the saved target, protocol health, last raw response, freshness, failures and
actual communication error. Commissioning requirements are a separate field, not a
substitute for a transport failure. Save, Connect, Disconnect and monitoring recovery
do not overwrite the last test. Structured test evidence persists in the bounded event
log across restart. Each new test also records completion time, per-stage UTC timestamps,
transport/FC04 elapsed time (including failures), destination address, source address,
source interface and the precise failure stage. Established TCP sources are observed
from the socket. For refused/timed-out numeric-IP targets, a local UDP socket selects
the OS route without sending a datagram; this source is explicitly **route selection
only**, not proof of established TCP or a packet capture. Hostname failures and older
logs can have source/destination fields **NOT RECORDED**, rather than invented values.
IPv4-mapped socket addresses are normalized for interface identification.
Legacy structured refusals retain their native error details in the primary result,
without manufacturing missing timestamps or interface data. Legacy free-text events
remain historical events, not fabricated structured readings.
**Save Settings** validates and atomically persists the
configuration; an invalid candidate leaves the previous saved/applied settings unchanged.
When already monitoring, Save safely stops the old connection and applies the new primary.
Otherwise **Connect** is separate and uses saved settings only. **Disconnect** cancels
pending connect/read/retry operations and prevents automatic reconnection until Connect,
Reconnect or an enabled startup. **Reconnect** stops the previous worker before starting
one replacement. **Reset to Defaults** requires confirmation, disconnects monitoring,
restores connection defaults and removes saved cloud credentials; it never resets the
panel, deletes history or operates any alarm controls.

The service is owned by the application shell, not the page. For alternative Modbus it supports optional startup
connection, polling at 1/2/5/10/30 seconds, bounded connect/read timeouts and reconnection
spaced 5–300 seconds apart. With automatic reconnection disabled, a failed connection
stops after the initial attempt plus the configured retry count (0–5). With automatic
reconnection enabled, it continues at the configured retry interval, without a tight loop.
The displayed state requires matching transaction/protocol/unit/FC04/length/byte-count
validation; opening a TCP port is insufficient. **ONLINE** means fresh monitoring-interface
data, **not** fire NORMAL. A failed request is **OFFLINE / STATUS UNKNOWN**; stale data is
**DEGRADED**, never NORMAL. The page refreshes status/diagnostics every second while visible;
closing it does not stop communication.

Local settings are stored at `EOEMSData\Configuration\fire-connection.json` beside the
executable. The new `ProtectedCloudToken` is separately protected with Windows **DPAPI CurrentUser**;
format 3 retains format 1/2 migration and the old credential fields without using them for cloud authorization.
Legacy passwords are never redisplayed; blank legacy username/password fields retain saved credentials.
Legacy Cloud and vendor BMS have separate encrypted username and password fields;
switching modes does not move credentials between them. The explicit credential-removal
checkbox clears only that mode's credentials on Save. Reset to Defaults clears both.
Settings/communication
controls require an authenticated administrator; viewers and offline preview cannot use them.
Corrupted/undecryptable settings produce a visible error and are not overwritten by defaults.
For a machine/account migration, preserve the configuration and re-enter credentials under
the new Windows account; copying a DPAPI file alone is insufficient.
Preserve this protected configuration file separately when backing up/moving the application;
the existing historian Backup / Recovery archive does not currently include fire connection
settings, and its restore flow does not restore configuration files.

Communication commands and background reads are serialized to prevent overlapping
test/poll requests or duplicate Connect attempts. Retry counts still advance on every
failure, but identical errors are logged only once until the error changes or recovery.
Saving uncommissioned settings no longer appends a generic FC04 prerequisite event.
Repeated no-op disconnects do not append duplicate lifecycle messages. Each explicit
Test Connection still records its own timestamped result; identical test outcomes are
not silently discarded.

Cloud fire events are stored separately in `fire-connection.json.cloud-events.db`.
Each event payload is DPAPI CurrentUser-protected JSON; SQLite itself is not a fully encrypted database.
Only hashed site/identity keys are stored alongside payloads. A unique key combines site ID and vendor
event ID, or a stable normalized event fingerprint when no ID is returned. Transactions deduplicate
overlapping responses across restarts without deleting history. The grid/search/CSV operates on the
latest 200 stored events for the displayed site; full historical rows remain persisted. CSV exports
only displayed filtered events, neutralizes spreadsheet formulas, and never includes credentials.
**Historian / Reports -> Fire Events** now owns this history table, rather than Configuration.
It shares the shell's existing fire-panel manager/services; its panel selector follows additions,
renames and removals. The grid fills the Historian workspace with a pinned timestamp column and
horizontal scrolling. Unrelated acquisition health/save rows are hidden only on this tab.
Existing historian pages and the default Equipment history selection remain unchanged.
**Refresh Events** reloads persisted history without a network request. **Sync Cloud Events** is
a separate administrator-only authorized retrieval action with cancellation; it uses saved Cloud
settings and the existing shared request gate. Viewers/offline preview can browse/filter/export
stored events but cannot synchronize. Display timers stop when hidden; service polling does not.
Local Modbus response timestamps are never labeled as cloud event synchronization.
Diagnostic clearing never deletes this event database. Removing an optional panel retains its event
database as an archive and prevents its numeric ID from being reused for a different panel.
Back up this database with the fire settings separately; the ordinary historian archive does not
include these files. DPAPI data requires the original Windows account or authorized reconfiguration.

Credential-free connection diagnostics are kept beside the settings in
`fire-connection.json.events.jsonl`, with one rotated previous file (~256 KiB each).
The latest 100 events reload after restart; the page shows the latest 20 in chronological
order without nested scrolling. Optional event following scrolls only when already at
the end; it does not interrupt reading older content. **Copy Diagnostics** copies both
status groups and up to 100 events, excluding credentials, and remains available to
read-only users. **Export Diagnostics** writes a credential-free JSON troubleshooting
snapshot with separate last-test and active-monitoring groups, native error codes,
timing/source/destination fields, freshness, UNKNOWN fire status, event-log errors and
up to 100 events. It uses a user-selected file, confirms overwrite, writes asynchronously
and atomically, and cannot replace the fire configuration or event logs. Export works
for viewers/offline preview without initiating communication or appending an event.
Logging/access/clipboard/export errors are displayed explicitly.
This rotation applies only to connection diagnostics, not fire or equipment history.
Active Events continues to state that **fire alarm/fault acquisition is not implemented**;
an ONLINE connection does not invent alarm signals, alarm history or a cleared state.

No live Zeta panel has been contacted or commissioned by these changes. Subsequent
manufacturer-document verification found the
[SCM-LAN datasheet](https://www.zetaalarmsystems.com/wp-content/uploads/2024/04/SCM-LAN.pdf),
GLT-294-6-14, Issue 003, dated 28 April 2025: page 1 explicitly documents read-only Modbus
command **0x04** for general, expansion-module, zone, loop and local I/O status.
The [installation manual](https://www.zetaalarmsystems.com/wp-content/uploads/2024/04/SCM-LAN_Installation_Instructions_001.pdf),
GLT-294-7-15, Issue 001, does not supply a register map. Neither document establishes
the site's actual IP/port, Unit ID, register addresses or installed firmware compatibility.
Obtain these details and the approved SCM-LAN register/response map from Zeta or the
commissioning engineer before confirming the mapping or enabling real communication.
The manufacturer's generically named
[Modbus-Registers-Manual.pdf](https://www.zetaalarmsystems.com/wp-content/uploads/2025/02/Modbus-Registers-Manual.pdf)
is actually the **Quatro** RS485/RTU register manual, GLT-256-7-2, not an approved
SmartConnect SCM-LAN map; its addresses must not be copied as SmartConnect defaults.
Generic FC04 implementation tests do not establish compatibility with the installed panel.

Cloud fixture validation (no vendor/network requests):
`dotnet run --project Tests\SnmpValidation\SnmpValidation.csproj -- --zeta-cloud-unit "<output-directory>"`.
It covers the approved claims POST/payload and event GET paths/headers, exact expiry/renewal
thresholds using a fixture clock, account/session separation, errors, safe parsing, token migration/protection, persistent
deduplication/reload, serialized polling, cancellation, rate limits, actual UI actions and responsive
shell-sized viewports. The [implementation report](ZetaCloudEventConnector_20261104.md) records
results and remaining live-verification requirements.

Saving Fire connection settings preserves the existing editor controls; only saved state,
credential presence and mapping evidence are refreshed. Protocol changes still rebuild their
own fields, with label measurements performed after suspended layout resumes. Fixed-mode
editors also constrain captions on viewport layout, so the first Save does not trigger a late
wrap-width correction. The Cloud fixture checks first/repeated Save control identity, fonts,
bounds and label wrap limits immediately and after layout settles in all four connection modes.

The focused `--settings-save-ui "<output-directory>"` fixture audits PLC, alarm limits,
SNMP registry/profile, Web Card credentials, backup policy, acquisition and raw recording,
plus cached Configuration sections and its PLC model-update callback, at three viewport sizes.
Before running it, stage only built test executable/DLL/PDB/runtime JSON files and the
`runtimes` directory into a fresh directory containing `settings-save-isolated-` in its name.
Run `SnmpValidation.exe --settings-save-ui "<output-directory>"` from that copy.
The guard prevents accidental use against ordinary application stores: real Save handlers
write fixture settings and fake protected credentials only beneath the staged executable.
No card, PLC or vendor API communication is authorized by this fixture.

Legacy isolated verification uses a loopback TCP fixture, temporary settings and responsive
WinForms captures at 1366x768, 1600x900 and 1920x1080 window sizes, plus a 980x600
regression case. Checks measure field accessibility, table containment, non-overlap,
multiline label heights and absence of horizontal overflow. It covers valid/fragmented/invalid FC04
responses, exceptions, TCP-only responses, timeout/cancellation, genuine status transitions,
bounded retry/recovery, disconnect, configuration reload/startup, credential protection,
validation preservation, permissions, dynamic fields and actual UI save/reset actions.
The connection-mode correction also covers independent saved profiles/credentials, legacy
settings migration, invalidation of checkbox-only verification, bound successful FC04 test
evidence, pinned controls during scrolling and the actual review/test/save/connect UI workflow.
Run the fire-specific command below for these isolated checks. Simulator results validate
software behavior only; they do not establish live panel connectivity, register meanings,
or fire-alarm status.

```powershell
dotnet run --project .\Tests\SnmpValidation\SnmpValidation.csproj -c Release --no-restore -- --fire-connection "<evidence-output-directory>"
```

Implementation files:

| Area | Files |
|---|---|
| Validated settings / protected persistence | [FireConnectionSettings](Domain/FireConnectionSettings.vb), [FireConnectionStore](Infrastructure/FireConnectionStore.vb) |
| Read-only transport / independent service | [FireModbusClient](Services/FireModbusClient.vb), [FireConnectionService](Services/FireConnectionService.vb) |
| Configuration UI / application lifecycle | [FireConnectionSettingsView](UI/Views/FireConnectionSettingsView.vb), [ConfigurationView](UI/Views/ConfigurationView.vb), [MainShellForm](UI/MainShellForm.vb) |
| Honest alarm-acquisition coverage wording | [ActiveAlarmService](Services/ActiveAlarmService.vb), [ActiveEventsView](UI/Views/ActiveEventsView.vb) |
| Isolated verification / regression integration | [FireConnectionValidation](Tests/SnmpValidation/FireConnectionValidation.cs), [SettingsUiValidation](Tests/SnmpValidation/SettingsUiValidation.cs), [Program](Tests/SnmpValidation/Program.cs) |

## Unified equipment history and reporting

### Full-resolution acquisitions and interval Value / Min / Max

Equipment History reads saved PostgreSQL raw acquisitions, not live display values. The
`equipment_raw_acquisitions` store durably captures every observed transformer/TOTAL acquisition
and completed UPS poll, including bad-quality observations. It uses one acquisition UTC column,
session/sequence identity, the interval in force, and a versioned zlib payload containing
numeric values, quality and diagnostic source evidence. Alarm/fault changes and reminders are
durable events in `equipment_alarm_history`, not duplicated alarm payloads on every measurement.
Earlier raw payloads containing alarm evidence remain readable and are not rewritten.
New raw numeric channel values are rounded to **2 decimal places** (midpoint ties away from zero)
before compression; this intentionally reduces measurement precision, not acquisition count or time.
Existing PostgreSQL history is not rewritten. Live calculations, alarm evaluation and original
diagnostic/source evidence retain their precision. Compression is lossless relative to the rounded
payload; rounding does not reduce fixed-width PostgreSQL `double precision` columns in legacy tables.
History measurement cells and statistics display/export **1 decimal place**; state/alarm codes remain
integers and unavailable values remain blank. Extrema/statistics are computed before display rounding.
Size savings depend on the compressed payload and may be modest because diagnostic evidence is retained.
The existing selected-cadence transformer and UPS-slot tables remain compatible with older reports.
The redesigned equipment workspace is **raw-only**: it never mixes old selected-cadence slots,
15-minute rollups or derived UPS aggregates into measurement results. Selecting **Alarm / Fault**
adds saved observed event rows from the alarm ledger to the same table, without pretending they are
raw measurement samples.
Earlier records remain untouched in PostgreSQL and available to existing compatible reports/services.
An empty pre-cutover period is explicitly reported as having no raw acquisitions, not silently
filled from another source.

The Historian page uses a compact common heading/status/navigation layout. Equipment History has
an equipment/date toolbar, a searchable **CHANNEL / TREND** sidebar with measurement/alarm filters,
and only **Records** and **Trend & statistics** views. The equipment picker contains T1-T4,
Transformer TOTAL and registered individual UPS, not archive/event pseudo-equipment, derived
ATS/fleet totals, disconnected-source placeholders or a separate recorded-assets action.
Changing channel search/filter preserves selections; an unchanged catalog refresh preserves
loaded results. Select multiple channels to view voltage, current, power and named alarm/fault events together.
The Records table has selected channels as columns and **Value**, **Min**, **Max** rows for each
saved recording interval (including 100 ms, 250 ms, 1 s, 5 s and 10 s). Value is the last valid
acquisition; Min/Max include every valid acquisition returned within that interval. Peak means
observed maximum, not waveform peak. A single acquired reading gives equal Value/Min/Max; no
new polling rate or between-poll readings are invented.

Choose the **Interval peaks** filter or select **Interval peak - ...** in Channel / Trend to add observed maxima for voltage, current
and power channels alongside ordinary readings and Alarm / Fault. Peaks use every valid acquisition
inside each saved interval (0.1/0.25/0.5/1/2/5/10 s), not a new polling rate, waveform peak or
meter peak register. They are calculated on read without duplicate database records. Their Value
cell is the interval maximum; Min/Max cells stay blank for peak columns, and peak-only selections
have one row per interval. Trend/statistics use the same interval maxima, with no energy inferred
from peaks. Individual-readings mode is disabled while peaks are selected. Missing peaks remain
blank/unknown; partial periods are labelled partial. Phase voltage/current and calculated phase kVA
are available where the source supports them; unverified transformer phase active kW is not invented.
All stored transformer measurement channels remain in Equipment History. Previously saved legacy
slots/archives, daily reports and alarm events are preserved; the raw-only history cutoff is unchanged.

There is exactly one **Alarm / Fault** checkbox per equipment. It adds separate **Alarm**, **Fault**
or **Unclassified** rows with condition name, state, severity, observed event, acknowledgement and
details. Severity is separate from fault classification; ambiguous card events are not guessed.
Threshold excursions, battery/bypass operation and high load/temperature are alarms. Communication
failure, confirmed power-off and explicit card hardware failures are faults; vendor identity is preserved.
Transformer history includes distinct phase under/overvoltage and the existing confirmed
transformer-off / ECG-off detector (all phases below 20 V for at least 3 s; ECG requires all four).
UPS includes on-battery, bypass, output-off, battery state and individual named card conditions such as
fan failure, only when actually reported. No missing fan condition is fabricated as normal.

Alarm-only tables omit measurement interval/acquisition columns and empty acknowledgement columns;
they do not show "0 raw readings" when no measurement channel is checked. Checked channels, not
the highlighted list item, determine the table and exports. Unknown event states display **Unknown**
severity and **State unavailable** quality, never an old active alarm severity. Cleared/inactive
events display **Normal** severity; their original saved severity/policy remains in evidence.
Legacy condition-mapping changes remain visible as **Record note / Tracking replaced**, not a
physical alarm or a confirmed clear. Row tints distinguish active alarms/faults, inactive states
and unknown/tracking notes. Exact saved UTC timestamps and the shared selected-table projection
are preserved in CSV/Excel/PDF. Alarm-only trends have no empty measurement statistics pane.

Each condition records changes immediately (including clears, reappearance, severity and acknowledgement).
A continuously active condition records again on its first observation at or after **5 minutes**
since its last event. Every condition has an independent durable timer. Clear/reappearance restarts
that condition's timer; unrelated events and application restart do not reset it. Monitoring boundaries
remain explicit, but the old periodic one-minute observation heartbeat is removed. No reminders are
backfilled through an outage. Unknown/incomplete evidence is never a clear. Absence of events in a
selected period does not prove normal operation; these are observed events, not exact device transition
times or incident counts. Original history is untouched; new selected-cadence measurement rows also
omit repetitive alarm snapshots, while the event ledger retains saved policy and condition evidence.

Mixed measurement/event trends have synchronized engineering-unit and named 0/1 **event marker**
lanes; sparse events are not joined into an invented continuous state trace. Event observations never
receive measurement means or energy statistics. Unique UPS measurements stay available; only the 34
source-identical card-channel aliases are hidden and omitted from future raw numeric dictionaries.
Derived/scaled/validation-distinct readings are not merged. Diagnostic source evidence remains saved.
Generators remain explicitly unconnected until a real acquisition source is commissioned.

Use **Individual raw acquisitions** to inspect/export every received reading rather than its
interval projection. CSV, Excel Records and PDF use the same selected table projection as the UI;
Excel contains Summary, the identical selected Records table, and selected-channel Statistics only;
there are no extra unselected raw/evidence/alarm-event worksheets. Select individual readings before
export to obtain each original observation. The legacy report-service export format remains available
to its existing callers, unchanged.
The main table has one UTC time; a differing UPS slot/acquisition time remains available in source
details and the raw sheet. Equal timestamps are not duplicated in the new database schema.
Mixed-unit trends show native values with units in the legend; their numeric magnitudes are not
directly comparable. Selecting too many observations fails explicitly rather than truncating.

All three phase voltages/currents have independent extrema. Phase apparent power is explicitly
calculated kVA from L-N V x I; TOTAL sums the individual transformer phase kVA. Transformer phase
active kW is unavailable until a verified register exists, never estimated by dividing total power.
UPS phase power remains in its mapped W units and is available only where the card mapping supports it.

Old selected samples cannot establish within-interval extrema: their Min/Max remain blank, not
falsely reconstructed. Clipped/current intervals are marked partial. Original acquisitions remain
saved, so displaying Value/Min/Max is not lossy aggregation. Automatic transformer raw/quality and
UPS raw expiry is disabled; old 15-minute archives remain readable. The Raw Recording tab has one
recording interval selector (no separate custom interval box). Previously saved nonstandard intervals
remain selectable without silently changing them. Administrators can edit **Planning window (days;
no deletion)** from 3 days up to the interval's limit: **3 / 7 / 15 / 30 / 45 / 60 / 90 days** for
**0.1 / 0.25 / 0.5 / 1 / 2 / 5 / 10 seconds**. Changing the interval clamps the selected days to its
limit and updates the table immediately, before saving. The existing `HistorianRetentionDays` key
stores this planning choice; it never deletes records or restricts historical queries.

The table estimates incremental PostgreSQL storage for the chosen days and each interval's maximum
days. When every active asset/table has at least 64 rows in the trailing 24 hours, it uses those
observed row counts (including operating/offline gaps) and the average PostgreSQL row size of up to
64 recent rows. Only transformer raw-history growth scales with the selected recording interval;
full-resolution transformer measurement samples and UPS samples remain at their observed cadence.
If any active table lacks enough recent rows, the estimate falls back to configured PLC/UPS polling
and conservative sampled row sizes. Both paths add 2x headroom for indexes and growth. Estimates
cover T1-T4/TOTAL and enabled, acquiring UPS historians; database/query errors are reported
explicitly, never shown as zero. They refresh on opening the planning panel and at the 30-second
active-page refresh. These are **planning estimates, not guaranteed worst-case bounds**: future
evidence can grow. They exclude existing history, WAL, backups, exports and offline outbox.
Selecting more planning days increases the forecast, not actual retention; the planning window
never deletes or hides older records.
Full-resolution preservation grows disk usage beyond any selected window: provision storage,
monitor free space/outbox health, and back up PostgreSQL plus the pending outbox. Compression does
not make storage unlimited or fix the previously documented backup-coverage limitations.

### Recording reliability and observed alarm history

Only one SCADA application instance may run per Windows session. Transformer raw recording now waits the full configured interval (no 85% early-admission tolerance).

The running shell durably commits transformer samples/quality, UPS slots and alarm events to `EOEMSData\Historian\recording-outbox.db` before PostgreSQL replay. SQLite uses WAL and FULL synchronization. PostgreSQL transactions and replay receipts prevent duplicate samples/events and double-added quality counters after an ambiguous commit. The shared buffer holds at most **500,000 commands**, not unlimited days of data; a full/unwritable buffer reports an explicit recording error and never silently discards older buffered commands. Preserve this database and its WAL with the deployment; do not copy an open SQLite database without an online backup. Existing history-only backup/restore does not include or replace this outbox or PostgreSQL; separate database/outbox backups remain required.

UPS slot capture is independent of PostgreSQL writes and retention. Sub-millisecond early timer wakes no longer select the preceding slot and then skip the next one. Missed deadlines while running receive explicit NoData/null rows, not fabricated historical readings. Disabling acquisition/recording intentionally pauses slots; an application outage cannot be backfilled without actual source evidence.

**Equipment history > Alarm history** provides an individual transformer/TOTAL/UPS event stream using the existing records/evidence/CSV/Excel/PDF workflow. PostgreSQL `equipment_alarm_history` records observed active/inactive/raise/clear/acknowledgement changes, unknown evidence, alarm-limit/rating changes, minute heartbeats and monitoring boundaries. `equipment_alarm_latest` exposes the latest saved state per condition. Events include previous/current state, host observation/acquisition times, session and the evaluated policy. Transformer raw rows include `alarm_evidence`; UPS rows include `alarm_evaluation` alongside sampled card evidence. Old rows remain explicitly without this evidence.

Alarm recording observes acquired PLC cycles and completed UPS polls, independently of numeric raw-recording cadence. Missing/incomplete evidence cannot clear a previously active alarm. A card acknowledgement is an **observed card state**, not an acknowledgement action performed by EOEMS. Polling still cannot capture unobserved between-poll changes or establish exact hardware event times. Unsupported fields, disconnected UPS devices and uncommissioned ATS/generators remain unavailable, never estimated or zero-filled.

**UPS details > Alarm History** defaults to **Day (last 24 hours)**, with **Week (7 days)**,
**Month (30 days)** and **All**. Time/device predicates and a 201-row lookahead limit run in
PostgreSQL before JSON decoding. Only 200 records are displayed per page; **Older / Newer**
use the observation timestamp plus event ID, retaining a fixed upper-bound snapshot until
the range is changed or the tab reopened. All never loads the entire archive at once.
Changing ranges or closing the form cancels the previous read. Fixed-height rows avoid
measuring every diagnostic paragraph; hover over Detail to read the complete saved text.
An additive asset/time/event-ID index supports paging, and history reads no longer execute
schema/view DDL. Existing records are not deleted or rewritten.

UPS card-details availability, voltage-reference uncertainty and power-consistency diagnostics
remain in current device evidence, but no longer create their own alarm-history transitions.
Real electrical/card faults and debounced communication failures retain their raise/clear
records. New communication event details use the compact quality label instead of repeating
the entire SNMP capability/error report. Historical diagnostic records remain browsable.
Battery Charging (card type Message, without Major/Critical severity) is informational;
an unknown voltage reference still prevents L-N limit evaluation and is not a measured voltage fault.
Validate the card mapping before selecting a voltage reference; do not infer L-N solely from magnitude.
`--ups-alarm-history-unit <evidence-folder>` validates this behavior with an isolated PostgreSQL
instance, 50,000 synthetic history records and the actual WinForms paging/cancellation controls.

Select equipment type, stable equipment ID, channel (or **Load all channels**) and an explicit **UTC [from, to)** window, then **Load records**. **Records**, **Analysis / trend** and **Source / alarm evidence** share one loaded result; changing filters clears stale output. **Recorded assets** also discovers UPS IDs removed from the current registry.

Supported sources are T1-T4, the stored transformer TOTAL, retained transformer **15-minute archive** bucket min/max/mean/count, individual UPS standard/vendor numeric channels and battery/source states, **derived** ATS-bank UPS output kW/kVA, and a separate UPS-fleet total. Generators are clearly **not connected** until a real source exists. New equipment types can implement `IEquipmentHistorySource` and reuse the table/analysis/export pipeline; a label alone does not provide telemetry.

UPS recording adds acquisition UTC, recorded identity/model, historical bank/fleet membership, recording interval, full sampled alarm-table evidence and numeric/source/partial-field JSON. These additive fields are prospective, not backfilled. Missing values are blank, not zero; electrical/power measurements using UPS mapping before revision 2 are excluded, while unaffected legacy battery/status records remain usable. Incomplete/misaligned cohorts never become partial totals. ATS groups are **not ATS meters or switch-position telemetry**, and transformer/UPS totals must never be added together.

Statistics distinguish sample counts, sample mean and sampled peak from incident counts, availability or metered energy. Estimated kWh uses only eligible consecutive power observations with advancing acquisition evidence and bounded gaps; UPS calculations require the saved interval, and aggregate integration requires every member to advance with unchanged membership. Whole 15-minute archive buckets never produce invented raw measurements or energy.

CSV preserves normalized records and detailed evidence. Excel includes Summary/Records/Statistics, numeric cells, frozen headers and filters. PDF includes measurements/statistics with the existing company header; detailed JSON remains in CSV/Excel. Loads exceeding **100,000 channel observations** and PDFs exceeding **10,000** fail explicitly instead of truncating. Existing files are not overwritten; failed exports do not leave completed-looking output.

Equipment-history and Online Analyzer PDFs share the same local rule-based renderer; no external AI service is used. The chronological **event evidence register** includes every detected timed warning/critical finding with its observed start/end, measured extreme, extreme timestamp, reference, unit and percentage change where applicable. All detected threshold runs are retained, not only the three strongest. A single abnormal sample has unknown duration. Percentages describe valid samples, not time; the ordinary even/odd median, nearest-rank P95 and max absolute three-phase deviation from the phase mean are used. Phase balance requires one value per phase at an exactly matched timestamp; two phases or duplicate phase readings cannot establish three-phase balance.

The shared analyst now classifies battery/DC evidence separately from AC. DC median-voltage changes are informational, not AC compliance alarms; signed DC current and zero/negative or sign-crossing power means are not used as above-average AC load denominators. Battery sensors do not inherit generic transformer 80/100 deg C limits or oil/tap advice. Neutral screening checks every equipment against exactly coincident, uniquely identified L1/L2/L3 observations, with numeric neutral/phase-mean evidence, not different-time peaks or period averages. Automatic PDF comparisons separate input/output, L-N/L-L, battery terminals and DC bus.

Each PDF includes a scope-aware **Electrical interpretation** table: source identity/quality/scaling first, then equipment mode, verified ratings, aligned load/temperature/alarm evidence and calculation prerequisites. It explains apparent vs active power, matched three-phase kVA/PF, signed DC V x I, non-overlapping totals, battery runtime/chemistry/cell-count requirements and efficiency prerequisites. Current UPS registry model/verification is disclosed in analyzer channel provenance; it is not a historical nameplate or manufacturer datasheet. No datasheets or nominal DC/temperature/runtime limits are invented, and formulas are not applied when required measurements/topology/sign conventions are missing. Validate with `--electrical-knowledge-unit "<new-evidence-directory>"`.

The report distinguishes **saved-sample validity** from **average observed time coverage**, with coverage prominent on the cover and a per-channel assurance table showing valid/returned counts, first/last UTC and measurement provenance. Coverage sums adjacent valid spans no longer than `max(5 seconds, 3 x median positive cadence)`, excluding known invalid intermediate readings, clips to the requested period, divides by its duration and averages all selected numeric channels (empty channels contribute zero); unobserved window edges are excluded. This is a loaded-series continuity estimate, not certified equipment availability or proof of raw-data completeness. Reports do not issue an equipment health score. Coincident extreme-time groups (within 1 second of each group's first extreme) help navigate repeated phase/aggregate flags, but do not claim independent or common physical incidents.

Every analysed channel has a full requested-period overview plus up to two detail zooms; all findings remain in numeric tables regardless of zoom selection. Lines are visual guides, not interpolated measurements; gaps/invalid readings break them **before** chart reduction, preserving original segment endpoints and extrema. Dates accompany extreme/last timestamps, and trend statements explicitly compare last-third and first-third means. Alarm groups are equipment/condition-specific, all condition summaries are included, and cleared/inactive/unknown-only observations never become active-fault findings. Individual event details remain in CSV/Excel. Temperature sensor identity, positive/nonpositive PF interpretation, generic guides, missing evidence and thinning limits are explicit. Source verification precedes suggested physical checks; associations are hypotheses, not established causes.

History and Online Analyzer share current configured rating lookup. Transformer TOTAL uses the complete T1-T4 nameplate sum when available, with conflicting TOTAL settings disclosed; UPS registry verification and historical-rating uncertainty are retained. Current ratings do not prove historical ratings. A voltage-only study cannot calculate loading. A saved zero voltage alone does not establish an outage. Transformer, UPS and aggregate comparisons remain separate; tables wrap and repeat headers, and continuing sections identify their context.

Focused report regression and read-only regeneration of a T1-T4/TOTAL three-phase L-N study:

```powershell
dotnet run --project .\Tests\SnmpValidation\SnmpValidation.csproj --no-restore -- --report-review-unit "<evidence-directory>"
dotnet run --project .\Tests\SnmpValidation\SnmpValidation.csproj --no-restore -- --live-transformer-voltage-report "<app-directory>" "<from-UTC-ISO>" "<to-UTC-ISO>" <thinning-seconds> "<new-report.pdf>" "<evidence-directory>"
dotnet run --project .\Tests\SnmpValidation\SnmpValidation.csproj --no-restore -- --live-history-comparison-report "<app-directory>" "<from-UTC-ISO>" "<to-UTC-ISO>" "<new-report.pdf>" "<evidence-directory>"
```

The regeneration commands use enforced read-only PostgreSQL connections and never modify production history. They use the requested period, channels and resolution, not PDF images as data. The comparison command reproduces a T1 temperature(Y)/active-power(X) mean-response study with voltage/current context, paired counts, response evidence and aligned time traces.

See the [Equipment recording and reports study](Documentation/Historian/EquipmentRecordingAndReportsStudy.md) for the audited storage/telemetry coverage, company report catalogue, limitations and **proposed** cadence/retention/backup/event-ledger design. Current transformer-only automatic daily export policy remains unchanged pending approval. Live acquisition and raw recording intervals are limited to 0.1–10 s, with distinct acquisition and interval-grouping roles. The **Raw Recording** tab's editable 3–90 day planning window does not enable automatic raw expiry.

```powershell
# Requires the installed PostgreSQL 18 command-line tools for a disposable loopback-only test cluster.
dotnet run --project .\Tests\SnmpValidation\SnmpValidation.csproj -c Release --no-restore -- --equipment-history "<evidence-directory>"
```

## Active events and transformer power detection

**Historian & reports > Active events** replaces the old historical outage-duration page with a
current, read-only Alarm / Fault / Monitoring list. It shares the actual transformer/UPS alarm
recorder, including battery, bypass, named card/fan failures, confirmed transformer/ECG off and
separate phase under/over-voltage conditions. A confirmed inactive observation automatically removes
the condition; no manual acknowledgement, hardware reset or control command is sent. ECG appears
once, while genuinely independent transformer conditions remain visible.

Communication loss, incomplete card evidence, disabled acquisition, stale acquisitions and restart
are not clears. Previously active conditions remain **Unknown / unconfirmed** until fresh evidence
confirms active or inactive. An absent observation adds a clearly labelled Monitoring unavailable
row. Freshness uses actual completed acquisition timestamps and the configured polling/timeout
budget, not the five-minute alarm-reminder timestamp. Optional signals never observed are not
invented; the coverage note is not a claim that every optional signal is supported. Fire alarm and
generators explicitly show **NOT CONNECTED** because there is no acquisition source for them.

The page refreshes every two seconds only while selected and the Historian workspace is active.
Equipment/state filters, search and selected-condition source details apply to the live list.
CSV exports exactly the currently displayed filtered snapshot, retaining full timestamp precision,
and refuse to overwrite existing files. Source read errors clear stale rows and display Unavailable.
Raised/cleared/unknown events remain available in **Equipment History > Alarm / Fault**, with
immediate transitions and independent five-minute active reminders. Fresh live observations update
memory only when nothing changes: they add neither per-poll PostgreSQL alarm rows nor per-poll
durable-state writes. An unresolved last-known active flag survives recorder restart.

### Transformer detector and retained outage ledger

The observed power-off frames contain valid zero voltages and kW but an IEEE-754 NaN frequency. Previously that one unavailable frequency rejected the entire block. The decoder now accepts **only NaN frequency at index 8 when all three finite, nonnegative L-N voltages are below 20 V**; frequency remains unavailable, never zero-filled. Other non-finite fields, invalid frames and communication failures still fail validation. Valid off measurements continue into measurement history and energy calculations without inventing frequency or PF.

The voltage rule is independent of PLC communication health:

Transformer gauges use high-contrast red needles extending to the scale ticks. Power-off badges show only **TRANSFORMER OFF** / **ECG OFF**; voltage-rule explanations remain in diagnostics and event evidence, not inside the dial.

- All three L-N voltages **<20 V for at least 3 seconds of successive acquired evidence** confirm transformer off. All three **>180 V for at least 3 seconds** confirm restoration. Zero kW alone is not an outage. Detection is sampled: confirmation occurs on the first qualifying poll after the delay, not necessarily exactly three seconds later.
- All four transformers confirmed off produce **ECG off**; otherwise confirmed individual outages produce **Transformer off**. An existing off state is retained during restoration confirmation, with its pending-transition detail visible. A new ECG/individual segment cannot begin using an already energized, restoration-pending measurement.
- Intermediate/mixed phase voltages, missing/invalid readings, timeouts and stale data mean **power unknown**, not confirmed restoration or outage. Other transformers with valid off evidence may still be recorded individually.
- ECG replaces individual records while all four are off. Scope changes close the old segment and open the new scope(s), avoiding simultaneous ECG and transformer accounting. Segment counts are **not** physical failure counts; do not sum overlapping individual transformer durations as site downtime.

The former date-range/duration UI is removed; its SQLite outage ledger and compatible store/export/backup APIs are retained without deleting or rewriting any records. Start/end times in that ledger represent **confirmed detection boundaries**, not exact breaker operation times. Intermediate data ends a segment at its last verified off observation, not an assumed restoration. No historical outages are fabricated/backfilled from old bad-quality records.

Outage segments are transactionally stored in the `power_events` table in `EOEMSData\Historian\eoems_historian.db`, independently of PostgreSQL availability, raw-recording enablement and measurement retention. Transitions are saved immediately and the same open segment is checkpointed about once per second; this does not add a new alarm row every second. A crash may lose up to the latest unsaved observation interval. These records have no automatic purge and are included in existing historian SQLite backups/restores. Keep the database with its application data when moving the installation. The retained legacy CSV API includes stable event IDs, scope, UTC timestamps, status/end reason, numeric duration, selected window and voltage/cycle evidence; the Active events CSV instead matches its current live table.

Shutdown, reconfiguration, excessive acquisition gaps and backwards clock changes terminate observation coverage. A gap exceeds the greater of 5 seconds or twice the requested polling interval plus the eight-read timeout budget. On restart, previously open records close at their persisted last observation, marked monitoring interrupted; the new session must confirm afresh. Application downtime is never silently counted as continuous outage. The app cannot detect physical events while it is not running, nor prove source freshness without a PLC timestamp/sequence signal. File/database errors appear in the global status/footer, remain pending for retry, and are reported on shutdown if still unsaved.

Isolated decoder, threshold/debounce, scope, restart/gap, transaction/retry, actual UI/CSV/backup and localhost Modbus acquisition regressions:

```powershell
dotnet run --project .\Tests\SnmpValidation\SnmpValidation.csproj -c Release --no-restore -- --power-unit
```

## UPS measurement accuracy

UPS polling now distinguishes system totals from per-phase values and line-to-line (AB/BC/CA) from line-to-neutral voltage. On the tested NXL IntelliSlot cards, RFC1628 repeats the system kW total in every phase-power row and returns L-L voltage; treating those rows as phase kW/L-N produced tripled power, overstated kVA and a falsely clamped 1.00 power factor.

EOEMS prefers the card's self-described Vertiv Flexible-MIB measurements only after checking each permanent point's label and unit on that specific card. NXL supplies system kW/kVA; the tested EXL cards instead supply genuine phase kW/kVA, including a non-consecutive phase-C power index. These include explicit actual L-N/L-L voltages where supported, phase PF, active/VA load percentages, more precise frequency, battery main-disconnect/cabinet voltage, runtime, lifetime discharge hours, DC bus and temperature. System power is never invented as phase power. DC bus current is not battery current; uninstalled indexed cabinet zeros and unavailable battery temperature are not substituted.

Unsupported vendor objects remain unavailable, even when their labels exist but the card reports **No Support**. If vendor verification times out or is unavailable, RFC readings are retained but their voltage reference is marked unverified; derived kVA/PF and L-N voltage-limit evaluation are withheld until that reference is verified. This produces an explicit mapping warning. Physically impossible system PF is unavailable, never clamped. A card-reported kW > kVA raises a warning and is excluded from fleet/bank accounting and energy, while raw values remain inspectable. Fleet PF uses only matching kW/kVA populations and missing VA is never displayed as zero. Requests to the same endpoint are serialized, paced at least 25 ms apart, and have bounded queue waits; metadata probes use at most 12 varbinds per request.

The PostgreSQL UPS historian adds measurement revision, map/reference metadata, system kVA/PF, separate output L-L columns, raw `reported_output_kw` and `quality_detail`. Accounting `output_kw` is NULL for inconsistent power. Existing rows default to revision 1 and are retained unchanged under the existing retention policy. Corrected trends/energy use revision 2+ power only and never bridge the cutover or missing coverage. **Older inflated kWh is not automatically divided by three or rewritten.** On controlled deployment, verify database migration permissions and new-row read-back; no production historian writes were used as test evidence.

See the [measurement correction report](Documentation/UPS/MeasurementCorrection_20261028/MeasurementReport.md) for same-device raw-versus-normalized evidence, actual connection results, limitations, screenshots and staged build. The active installation is not overwritten automatically.

## UPS parameter verification

### Automatic connection recovery

Enabled UPS units are polled in the background even when UPS Monitoring is not open. After a failed read, the next attempt is scheduled after the smaller of **5 seconds** and the unit's normal polling interval; failures no longer escalate the delay to two minutes. Configured SNMP timeouts/retries, endpoint pacing and bounded fleet concurrency still apply, so queue time and read/probe duration are additional to the scheduled delay. Disabled units/acquisition remain disabled. No ping, browser session, credential change or manual reconnect is required.

On a successful SNMP measurement poll, the live snapshot and page version update automatically, failure counts reset, and normal configured polling resumes. UPS Monitoring refreshes every second when active; detail windows use their existing refresh timer. Historical last-good readings are never substituted as current during an outage, and the previous failure remains available for diagnostics. Recovery is confirmed by actual SNMP responses, not network reachability alone.

Local-only regressions exercise real UDP SNMP reads, sustained/repeated outages, background recovery with the page inactive, live monitoring-card refresh, disabled units, concurrency release and short retry timing across multiple failure counts:

```powershell
dotnet run --project .\Tests\SnmpValidation\SnmpValidation.csproj -c Release --no-restore -- --ups-reconnect-unit
```

### Named card alarms

UPS polling reads actual alarm rows, not just `upsAlarmsPresent`. On supported Vertiv cards it prefers the condition table, including the condition identity, active/cleared state, card type/severity, acknowledgement and occurrence **at card uptime**. Flexible events use the exact label reference returned by that card. Standard RFC1628 alarm identities are the fallback; unknown identities remain visible with their numeric OID and an explicit unresolved-details warning.

Open a UPS and select **Alarms** for the detailed table. The dashboard tooltip/priority alarm line, SNMP diagnostics detail and exported diagnostic matrix use the same named conditions. **Battery Charging** with card type Message is informational, not a fault; cleared retained events are not active faults. Reading never acknowledges, clears or resets a card alarm. Timeouts, changing tables, unknown states and count/table discrepancies are explicitly reported without discarding valid electrical readings or claiming that all alarms are clear.

The read-only A01 check on 2026-10-29 confirmed **Multiple Fan Failure** in both SNMP and Vertiv's Active Events page. B01 separately reported an RFC General fault while its vendor table and browser showed no active events. The reader now verifies that the vendor table is empty and its count remains zero before marking that generic RFC General fault **UNCONFIRMED (card data conflict)**. It remains an amber evidence warning in the dashboard, alarm table and diagnostics, not a confirmed active fault or an all-clear. A missing/failed vendor read, changing count, nonempty vendor table or specific RFC alarm never qualifies for this treatment. Independent operating-state and limit alarms remain evaluated. Explicit Major/Critical card severity also takes precedence over a Warning or Message type. Vendor condition `1.3.6.1.4.1.476.1.42.3.2.1.199` is named **Manual transfer to bypass inhibited** from `lgpConditionManTransferInhibited`; its card-provided Warning classification is retained. These are changing operating events, not permanent device classifications. Confirm physical equipment issues through qualified UPS maintenance.

Alarm detail reads are capped at 32 rows, with a four-second processing budget checked between requests and at most one second per alarm request, without network retries. A request already in flight can finish after the processing budget. Alarm completeness/status is separate from electrical quality. Named conditions are live snapshot data; the existing historian still stores its alarm count, not a new event-history schema.

**Measurement mapping is separate from electrical data quality.** A GOOD poll means the returned readings were accepted, not that every optional parameter exists. Hover over the Diagnostics mapping cell for the unavailable parameter names; select the UPS for exact OID responses. A parameter counts as readable only when all requested phases/values pass validation. Raw values and OID/scale provenance are retained for both RFC1628 and vendor readings.

The 2026-10-29 read-only fleet audit found five responding NXL cards at 37/41 parameters: battery current, battery temperature, input line-fault counter and input true power are not exposed by their requested RFC objects and have no verified equivalent vendor binding. They remain unavailable, never estimated from DC bus current, cabinet zeros or output power. Seven EXL cards, including D02-D04, returned 35/35 mapped parameters; A02/C02 did not respond. These are counts of the application's verified map, not all objects in the card's MIB. See the latest section of the [measurement report](Documentation/UPS/MeasurementCorrection_20261028/MeasurementReport.md).

For a reproducible same-response audit, run `dotnet run --project Tests\SnmpValidation\SnmpValidation.csproj -c Release -- --parameter-audit <configuration-folder> <evidence-folder>`. It uses isolated configuration copies, GET/GETNEXT only, records each parameter's raw OID, unit, scale and normalized value, independently checks system kW/kVA/PF, and compares production configuration hashes before/after. Unreachable cards are explicitly reported, not treated as successful measurements. Use `--published-parameters <staged-dll> <configuration-folder> <evidence-folder>` to verify the exact staged assembly too.

Use `--alarm-unit` for isolated parser/state/UI regressions, `--alarm-live <configuration-folder> <evidence-folder>` for current read-only card events and configuration-preservation checks, or `--published-alarms <staged-dll> <configuration-folder> <evidence-folder>` for the exact staged package. The live alarm audit also validates the saved local UPS limit settings, records each evaluated alarm/severity, and checks that the saved limits and credential/registry files remain unchanged. It explicitly reports use of application defaults if no saved limits exist; it does not read or modify the production database. The follow-up package is staged separately at `bin\SnmpAlarmDetailsV6\win-x64`; applying it to RunLatest requires a controlled update, not a second production instance.

## UPS SNMP credential configuration

The Diagnostics page can be refreshed or selected while fleet tests are still running. A selected UPS without a current connection-test result shows **Connection test: not run**; known-good comparison is deferred until that UPS has a result, even if another unit has already responded. Run `dotnet run --project Tests\SnmpValidation\SnmpValidation.csproj -c Release -- --diagnostics-unit` for isolated missing-result, repeated-refresh, comparison and report regressions (no network tests or production configuration changes).

Open **SNMP Setting > Card Settings**, either from the main navigation, **Configuration > System Configuration > SNMP Setting**, or the shortcut on UPS Monitoring. Selecting a UPS automatically loads its saved management URL, web username and assigned SNMP profile. Secret fields show saved/not-set hints, not decrypted passwords. The profile summary distinguishes profile transport from the device transport actually used for monitoring.

Authenticated administrators can create independent v1/v2c/v3 profiles, choose NoAuthNoPriv/AuthNoPriv/AuthPriv, duplicate profiles without displaying stored secrets, and assign profiles by selected UPS, ATS bank, or model. Profiles support port, timeout, and retry settings; existing devices retain their original transport settings until explicitly switched to profile transport.

Secrets are protected with Windows DPAPI for the current Windows account, independently of the registry. Stored secrets are never displayed; the administrator-only visibility checkbox affects newly entered text only. HTTP/web administrator passwords are not SNMP credentials. Changing the shared profile or assignments of C01, C03, C04 (and other currently verified responders) requires explicit administrator confirmation.

The Card Settings tab keeps one registered-UPS selector above four focused workspaces, rather than stacking every editor and table on one long page:

- **Overview:** saved web address/login availability, assigned SNMP profile, actual transport and current test status. Use **Open Web Card** or **Test Connection** directly, or open the appropriate editor. No password/community fields or profile table crowd the overview.
- **Web Login:** explicit HTTP/HTTPS selector, management URL, web username/password, **Save Web Login**, and **Open in Browser**. Opening checks the registered endpoint asynchronously with a four-second timeout per endpoint. If default-port HTTPS fails, including certificate/TLS errors, EOEMS checks HTTP before offering browser certificate review. A responding HTTP endpoint requires explicit approval; the URL field updates only after approval and **Save Web Login** persists it without replacing the saved password. Custom ports are not guessed. Browser TLS review remains available if HTTP does not respond; certificate validation is never disabled. Unreachable endpoints produce an error instead of opening a dead page. Local HTTP still does not encrypt browser login, and EOEMS does not automatically submit stored credentials.
- **SNMP Profile:** v1/v2c community replacement/confirmation or v3 user/security/auth/privacy settings, route-selected manager IP and UDP transport. Only fields relevant to the chosen security level appear. The summary shows saved-secret availability and shared-profile impact. Use **Save Profile**, **Assign to This UPS**, then **Test SNMP Connection**.
- **Profile Library:** dedicated full-height profile table. Select a row and **Edit Selected**, or double-click it to open SNMP Profile. **New Profile**, **Duplicate**, **Load Assigned** and **Delete Unused** remain available. The C04-only clone shortcut has been removed; generic duplication works for any UPS.

Reveal applies only to newly entered text and resets after 15 seconds or a tab/device change. Shared-profile edits list every affected registered device and require explicit confirmation. **Duplicate** plus **Assign to This UPS** allows a one-device change instead. Saving credentials in EOEMS never changes a card's community, manager ACL, SNMP version, IP or operating state.

Web usernames/passwords are stored separately in `EOEMSData\Configuration\web-card-credentials.json`, protected with Windows DPAPI CurrentUser; SNMP secrets remain in `snmp-credentials.json` and assignments in `ups-settings.json`. Saves keep protected `.bak` files. Retain protected snapshots of the complete configuration folder and the approved site vault. DPAPI files require the original Windows user/profile and protection keys: copying them to another account/computer is not a guaranteed recovery method. The history restore feature does not restore credential stores; the existing scheduled archive manifest has not been expanded by this change.

Read-only discovery and diagnostics use assigned profiles only, bounded concurrency, verified OIDs/phase indices, and explicit unknown-cause timeouts. No guessed credentials or SNMP SET operations are used. See the [credential-management and commissioning report](Documentation/UPS/SnmpCredentialManagement_20261028.md), [15-device test matrix](Documentation/UPS/CredentialValidation_20261028/15-UPS-test-matrix.md), and [before/after screenshots](Documentation/UPS/CredentialValidation_20261028/Screenshots).

The **UPS Registry** tab contains registration, batch profile assignment and acquisition/historian policy. Applying a profile stages changes; **Save registry** persists them with working-device confirmation. Tables retain readable minimum column widths and scroll horizontally rather than squeezing all columns. Action bars wrap at smaller sizes.

The **Diagnostics** tab prioritizes connection, mapping, data quality and last successful poll. **More Tools** contains OID discovery, known-good comparison, copy/export and a detailed-column toggle. The existing C04 recovery procedure is available there only when an administrator selects C04; its requirements are unchanged. Historical commissioning snapshots are not live connection status: see the [D-card follow-up](Documentation/UPS/SnmpCredentialManagement_20261028.md) and [earlier C04 report](Documentation/UPS/C04Recovery_20261028/RecoveryReport.md).

## Acquisition, display, and historian timing

PLC acquisition remains a sequential set of four FC03 reads, with a new TCP connection per block and up to two attempts per block. The configured PLC polling interval is start-to-start: elapsed cycle time is subtracted from the interval, and an overrun starts the next cycle immediately. It is not an additional delay after each cycle. The minimum remains one second; no faster PLC or physical meter sample rate is claimed.

Each acquisition records host-side transaction latency, attempt/retry/timeout and success/failure counts, cycle duration distribution, and fully successful cycle rate. These timestamps and rates describe PC-side communications only; the PLC supplies no source timestamp or sequence counter, so physical measurement freshness cannot be established. The protected UI refresh setting is independent: Auto currently uses a 100 ms WinForms timer, with optional 10, 25, 50, 100, or 250 ms intervals. UI updates are coalesced to the latest readings, and Windows timer scheduling is best-effort; these settings do not change PLC polling.

PLC attempt CSV logging and measurement history use separate bounded asynchronous queues so disk I/O does not run in the polling/UI path. The existing daily CSV logs remain the source for the Analytics page; an additional `EOEMSData\Historian\eoems_historian.db` stores raw quality and mapped measurement values independently of the authentication database. The historian also persists 100 ms, 250 ms, 1 s, and 10 s summary buckets and quality coverage. Each transformer sample also stores calculated apparent power (kVA = sum of phase V x I) and power factor (kW / kVA, clamped 0-1). TOTAL is calculated only when all four transformer readings are good in one acquisition cycle and their host-capture timestamps are within 250 ms; it stores total current (per phase and of the average currents), total kW, total kVA, total PF (total kW / total kVA) and average voltages. Reports can also include peak voltage, peak current, peak kW, time-weighted average kW and kWh, calculated from the saved samples in the report window. Queue depth, write errors, and dropped records/cycles are exposed on System Data. A full queue or repeated storage failure is reported as a drop rather than blocking acquisition. The historian does not infer PLC source freshness or unsupported measurements.

The Historian page provides protected acquisition/recording settings, Equipment History manual CSV/Excel/PDF exports, daily schedules/measurement selection, storage health and warning-only export retention. Equipment metadata is edited in Configuration, not a duplicate report page. Manual exports open a Save dialog; existing files are never overwritten. Daily Excel defaults on in the Africa/Accra factory timezone with a five-minute stabilization window. Daily reports default to `Documents\EOEMS_SCADA\Records`, resolved through the signed-in Windows account (including redirected/OneDrive Documents). Administrators can choose another output folder; a folder under Documents is stored as a relative path so it follows the current user's Documents folder. Export-job state is stored in the historian SQLite database for catch-up/idempotence. No automatic historian pruning or Excel deletion is enabled; pruning remains blocked pending verified backup/restore safeguards.

The administrator-protected Backup & Recovery page creates manual and scheduled local backups. The default schedule is daily at 00:01 Africa/Accra time, under `EOEMSData\Backups`; the configured retention is zero, so backups are not automatically deleted. The historian is flushed through a writer barrier and copied with SQLite's online backup API, then packaged with configuration and MeterLogs. A SHA-256 manifest and SQLite `integrity_check` are verified before the archive is promoted. The portable authentication database is excluded, and sensitive appsettings values are redacted.

An administrator can restore the historian and MeterLogs from a verified archive after explicit confirmation. Acquisition and CSV logging stop, a verified rollback archive is created, and the application closes. On the next launch, before settings, sign-in, or database access, the restore transaction applies the selected history and logs. If applying the selected restore fails, startup attempts to restore the rollback archive; if rollback also fails, the application refuses to open live files and reports the recovery error. Current app/PLC settings and the authentication database are not replaced. Restore and rollback were validated with synthetic fixtures; no production restore or plant-workstation drill has been performed.

## PLC and measurement limits

The application retains the Delta AS228T-A / Schneider PM5110 Modbus TCP map and FC03 read-only behavior:

| Transformer | Register start | Registers | Range |
|---|---:|---:|---|
| T1 | 2000 | 32 | 2000–2031 |
| T2 | 2050 | 32 | 2050–2081 |
| T3 | 2100 | 32 | 2100–2131 |
| T4 | 2150 | 32 | 2150–2181 |

Each block decodes 16 floats using the configured word order. Load gauge limits are 2500 kW per transformer (yellow at 1400, red at 2000) and 10000 kW total (yellow at 4000, red at 7000). PTOT is shown as kW and temperature as °C, with no added scaling.

Power factor, reactive power, apparent power, and energy are not present in the active 16-float map. PF and kVA are calculated from kW and phase V x I, and kWh is integrated from recorded kW (gaps longer than max(60 s, 2 x recording interval) are not bridged); these are labelled as calculated and depend on the unverified V/I scaling. Reactive power and meter energy registers remain unavailable. Voltage/current/frequency scaling, temperature source, and register-offset convention have not been verified on the target equipment. No live PLC read was performed during this build; unreachable equipment is reported offline/unavailable, never as simulated Online telemetry. A total reading requires four good transformer reads; last-good readings are labeled stale.

## Electrical correlation analytics

The Analytics page provides separate T1–T4 history views. It reads timestamped daily FC03 CSV logs from `data\MeterLogs` and never synthesizes missing observations. Available comparisons include same-record PTOT vs TDEGC, synchronized temperature/load panels, time-weighted preceding load windows, phase-matched L1/L2/L3 current vs L-N voltage, and selected-phase voltage vs PTOT. Scatter observations are not connected in acquisition order. Hover details show the host-side acquisition timestamp; CSV export retains the recorded values and quality.

TDEGC's physical source/channel is not verified even though the PLC project includes an AS06RTD-A module. It is labelled **Temperature — Source Unverified**; thermal-health, winding hot-spot, insulation-life, ambient-rise, and temperature alarm assessments are disabled. The prior generic 30/65/80 °C thresholds have been removed. Voltage deviation and excursion alarms are unavailable until an authorized, phase-to-neutral voltage reference and approved limits exist. Single-point comparisons are titled Voltage vs Load/Current; they do not claim transformer/cable voltage drop or impedance.

The current configuration has no PLC source timestamps, nominal voltage, ambient measurement, or upstream/downstream voltage pair. Logged timestamps represent PC acquisition attempts. Polling is sequential between transformers, so cross-transformer samples are not treated as simultaneous. Rolling averages use time weighting, require at least 80% window coverage, and reject intervals with gaps longer than 2.5 polling intervals. Descriptive regression/correlation is withheld unless there are at least 10 valid paired samples and non-negligible variation; it is explicitly non-causal and not equipment impedance.

The inspected CSV contained only 12 good block records total (3 per transformer across about 2.5 minutes), insufficient for the 5–60 minute lag windows or robust interpretation. Log provenance was not independently validated. History is append-only daily CSV; each selected load is bounded to 2,000,000 rows, but sustained multi-year operation should use a retained/indexed historian rather than repeatedly scanning large text archives. No PLC read or field calibration was performed for this analytics build.

## Build, test, and publish

Run from the project folder:

```powershell
dotnet build -c Release
dotnet run --project Tests\EOEMS.SmokeTests.vbproj -c Release -- <optional-transformers-screenshot> <optional-analytics-screenshot>
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o .\publish\win-x64
```

`run.exe` in the source project folder is a self-contained launcher. Each normal launch publishes the current source as a self-contained Windows x64 folder to `bin\RunLatest\win-x64`, validates the portable SQLite settings and required files, then starts that published application. It requires the .NET 10 SDK to build/publish; the resulting EOEMS application itself includes its runtime. Use `run.exe --publish-only` to refresh and validate the output folder without starting PLC polling. Keep the complete `bin\RunLatest\win-x64` folder together; its portable data is stored beside the published application.

The test screenshot uses isolated fixture values and is marked as simulated; it is not live PLC evidence. The folder publish includes the executable, embedded runtime dependencies, assets, `appsettings.json`, and the portable `data\` contents. Keep the complete folder when distributing or moving the application.

## Versioning and release approval

`VERSION.json` is the version registry. V6 is the validated current line, V5.1 is preserved as historical, and V6.1 is the next candidate. Version components are integers: V6.10 follows V6.9. The main title, footer/About dialog, assembly metadata, System Data diagnostics, release manifest, and ZIP name are populated from the release version. The current historian/reporting changes are not yet published or approved for deployment.

Preview the next candidate without reserving it:

```powershell
.\Scripts\Release\New-ReleaseCandidate.ps1 -Preview
```

Running without switches reserves a unique candidate, builds Release, builds/runs the smoke suite in a short temporary path, and records the result. This candidate-only path passed in a disposable project copy; it does not publish a ZIP:

```powershell
.\Scripts\Release\New-ReleaseCandidate.ps1
```

Only after explicit release approval should the portable package be created:

```powershell
.\Scripts\Release\New-ReleaseCandidate.ps1 -ApproveRelease
```

Promotion to `bin\Latest` requires the additional `-PromoteToLatest` switch and archives an existing Latest folder before replacement. Do not pass it without the separate production-deployment approval. Failed or reserved candidate numbers are never reused; failed candidates are retained in the registry. The explicit package-creation and promotion branches have not yet been exercised.

## Consolidated project structure

- `Source\EOEMS_SCADA_MASTER` — active source and tests.
- `bin\Latest` — reserved for an approved promoted folder deployment; currently empty.
- `Tested\Releases` — reserved for immutable approved ZIP packages.
- `Archive\Backups` — verified master baseline backup.
- `Documentation\Audits` — inventory, traceability, comparison, gap, cleanup, and build/test reports.

Existing historical source folders and packages remain in their original locations pending Gate B; no cleanup or deletion has occurred.
