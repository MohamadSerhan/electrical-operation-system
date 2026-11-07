# Changelog

# V6 - Historian storage forecast accuracy

- Bases the planning estimate on observed PostgreSQL row sizes and recent persisted rates when each asset has enough recent data. Only transformer raw-history growth scales with the selected recording interval; full-resolution measurement and UPS rates remain independent. Sparse samples retain the configured-polling fallback and both paths keep 2x headroom.

# V6 - Idempotent daily report scheduling

- Validates and reuses an existing daily Excel report, marks its export job successful, and avoids repeated overwrite-collision errors without changing the report.

# V6 - SNMP status on the Overview dashboard

- Restores an Overview tile showing enabled UPS units with current Good SNMP readings, with explicit offline, waiting, and registry-load error states.

## V6 - Named UPS card alarms and missing-parameter explanations (2026-10-29)

- Replaces count-only card annunciation with bounded RFC1628/Vertiv condition-table reads: actual name, state, type/severity, acknowledgement, card occurrence uptime and exact identity OID. Resolves EXL flexible event names from the card-supplied label reference and known fan/standard alarm identities from their MIB definitions.
- Shows details in a readable UPS alarm grid, prioritized dashboard alarm text/tooltips, SNMP diagnostics and exported diagnostic matrices. Normal card messages such as Battery Charging do not trigger fault severity; cleared retained events are distinguished from active faults. No acknowledgement, reset or other SET is sent.
- Retains electrical reads if alarm details fail, but explicitly flags timeouts, unknown states/identities, table changes, truncation and conflicting RFC/vendor counts. Missing acquisition data never displays an all-clear alarm state.
- Rechecks alternate NXL battery/environment/AC-power objects without finding verified sources for the four unavailable parameter types. Keeps the honest 37/41 status; explains unavailable fields in measurements/Identity & Quality instead of inventing zeros or unrelated substitutions. Fixes RFC source captions being mislabeled as empty FLEX sources.
- Adds 24 isolated alarm/state/detail-UI assertions alongside existing measurement/credential regressions and actual fleet event capture. Preserves production configuration, active acquisition and historian schema/history; stages the combined update separately.

## V6 - Per-parameter SNMP verification (2026-10-29)

- Identifies the NXL 37/41 gap as unsupported battery current, battery temperature, input line-fault counter and phase input power, rather than a failure of the main electrical reads. Keeps unsupported readings unavailable.
- Counts a parameter as readable only when all requested values/phases are present and in range. Adds unavailable-parameter names and a data-quality explanation to the mapping tooltip.
- Retains RFC1628 raw values and OID/scale provenance alongside vendor readings. Adds isolated per-card audit modes, independent raw scaling/system kW/kVA/PF comparisons and production-file hash checks.
- Current read-only audit: 12 of 14 registered cards provide valid electrical reads, including D02-D04; A02/C02 remain unresponsive. Five NXL cards expose 37/41 mapped parameters and seven EXL cards expose 35/35. These counts do not claim every possible card MIB object is mapped or physically calibrated.

## V6 - Focused card workspace and local HTTP correction (2026-10-29)

- Replaces the long stacked Card Settings screen with Overview, Web Login, SNMP Profile and a dedicated full-height Profile Library. Shows saved-card summaries separately from editors, retains one UPS selector and moves status feedback outside the editing form.
- Adds explicit HTTP/HTTPS selection. Fixes HTTPS certificate/TLS failures skipping the HTTP check: a responding local HTTP endpoint is now offered before browser TLS review, with approval required and no automatic credential submission. Save Web Login retains the corrected address and existing encrypted password.
- Verifies the actual A01 Open Web Card workflow, local A01/D02 endpoint selection, protocol-edit isolation and compact layouts, including no overview/web-login scrolling at 900x700. Preserves production profiles, card settings, active acquisition and historian.

## V6 - SNMP Setting redesign and web-card opening (2026-10-29)

- Renames visible navigation, page heading and configuration/monitoring shortcuts to **SNMP Setting**; preserves the existing internal route and monitoring state.
- Reorganizes Card Settings into web access, SNMP connection and saved profiles. Selecting a UPS loads its saved values, assigned profile, secret-presence hints, shared-profile impact and actual monitoring transport. Removes the C04-only clone shortcut/API and redundant edit/manual-login buttons; generic duplication remains available.
- Checks the registered web endpoint asynchronously before opening the browser. Offers confirmed HTTP fallback when standard HTTPS cannot connect and HTTP replies, preserves custom ports, reports unreachable endpoints and permits explicit browser review for unverified TLS without bypassing certificate validation. No stored credentials are sent; web reachability is never authentication or SNMP acceptance.
- Simplifies registry/discovery/diagnostic action bars, moves advanced diagnostics to More Tools, provides optional detailed columns, and adds readable minimum column widths, horizontal scrolling and wrapping actions. Keeps all registration, assignment, discovery, OID and reporting capabilities.
- Validates saved-card loading and layouts at 1600x1000, 1100x768 and 900x700, plus credential-free HTTP status/redirect/cancellation/error handling. Confirms D2-D4 answer HTTP but not default-port HTTPS. No card settings, active installation, production credential stores or acquisition/historian services are changed by deployment preparation.

## V6 - Vertiv measurement interpretation correction (2026-10-28)

- Confirms the NXL RFC1628 phase-power rows repeat system power and its phase voltages are L-L. Replaces those interpretations with per-card, label/unit-verified Flexible-MIB points: actual L-N/L-L, system kW/kVA, phase PF, active/VA load percentages, frequency, battery/DC/environment readings. No guessed three-way allocation, voltage conversion or battery-current substitution.
- Maps EXL actual L-N input/output/bypass points, genuine phase kW/kVA (including sparse phase-C point 5959), supported input power and cabinet measurements. Rejects label-only **No Support** objects. If vendor verification is unavailable, preserves readable RFC values but flags the voltage reference and withholds unverified kVA/PF and L-N limits. Rejects impossible PF instead of clamping it to 1.00.
- Corrects detail/source display, dashboard totals, matched-population fleet PF and unavailable VA presentation. NXL vendor-read failures cannot fall back to the known-invalid RFC power sum.
- Adds additive historian mapping/reference columns and corrected system kVA/PF/output L-L storage, raw reported power and quality details. Card kW exceeding kVA raises an operator warning and is excluded from fleet/bank accounting and energy without concealing raw readings. Old samples are not rewritten; revision 1 power is excluded from corrected trends/energy and integration does not bridge mapping revisions.
- Adds 283 deterministic measurement/metadata/sentinel/alarm/energy/pacing assertions and 336 real-card same-response assertions alongside 141 existing regressions and four fresh-process checks. Serializes and paces same-endpoint requests, bounds queue waits, and caps metadata PDUs at 12 varbinds. Stages the tested x64 package separately; deployment, production historian migration/read-back and unresolved card mappings require controlled commissioning.
- Current evidence supersedes earlier connectivity snapshots: nine active-registry devices return real electrical reads, including C04; five NXL and three EXL cards have verified vendor maps in the fleet run. C01 remains readable through RFC but has vendor-probe timeouts. The active registry already had 14 entries at task start; no registration was removed or restored by this change. The historical provisional .6 target was separately tested without registration changes. See `Documentation/UPS/MeasurementCorrection_20261028/MeasurementReport.md`.

## V6 - Device-specific Web Card and SNMP access settings (2026-10-28)

- Separates the Credentials page into Vertiv Web Card Login, SNMPv1/v2c Access Community, and SNMPv3 Authentication, with a registered-device selector, stable IP/ATS/profile identity, source-IP information and current test results.
- Adds a separate DPAPI CurrentUser Web Card store, protected save/backup, registered-IP-only management URLs and manual browser login. No automated firmware authenticator is assumed, stored logins are not submitted to the card, and HTTP reachability is never treated as login success.
- Adds community confirmation, 15-second entered-text reveal, shared-profile impact lists, and explicit one-device assignment using the existing saved settings/permissions.
- Adds **Clone default for C04** -> **C04-SNMP** -> masked replacement -> save -> C04-only assignment -> real read-only identity/mapping/electrical test, without changing C01/C03 or default.
- Keeps the credential editor selection stable across profile-store refreshes, wraps action bars at compact sizes, and sizes the editor to relevant fields rather than leaving a large inactive panel.
- Reuses the existing diagnostics/client and electrical-read acceptance helper. Tests distinguish missing identity from a confirmed identity mismatch and never accept engine discovery or missing electrical fields as a connection.
- Validation: 141 isolated assertions, four separate-process persistence checks, all 15 independent assignment fixtures, clean x64 build/publish, actual all-15 Test All and actual credential-page test buttons. Current real acceptance is **C01/C03 only**; C04 still times out and awaits approved local community entry/retest. Live v3 commissioning, controlled production restart and new historian writes remain pending.
- No PLC acquisition or historian writer/schema changes. Protected production profile/registry/audit files remain unchanged; existing connection history was retained in full with append-only additions observed during the work. Details and screenshots: `Documentation/UPS/CardCredentials_20261028/SettingsReport.md`.

## V6 - C04 existing-credential recovery workflow (2026-10-28)

- Adds administrator-only **Restore UPS Connection - Use Existing Credential** actions in the UPS registry and diagnostics, with asynchronous verification, cancellation, and a local password-vault/card-settings checklist.
- Reuses the existing DPAPI decryption process internally. Does not display or copy the shared community, change `default`, reassign devices, send SNMP SET, or modify any IntelliSlot card setting.
- Requires real C01/C03 regression, C04 v2c identity matching the previously observed `UPS - 11` / `1.3.6.1.4.1.476.1.42`, verified mapping and electrical reads before acceptance. A name/object-ID match is not an independent serial-number check.
- Audited the original protected configuration against the previously successful backup. The original shared profile remains usable and unchanged; C01/C03 return real measurements. C04 currently times out on v2c while answering credential-free v3 engine discovery. The user-reported card-side change is consistent with this evidence, but timeout alone cannot confirm a wrong community.
- Stages a clean Windows x64 package separately from the active installation. See `Documentation/UPS/C04Recovery_20261028/RecoveryReport.md` for test evidence and remaining local card/retest/historian acceptance. Earlier C04 successes below describe historical tests, not current recovery.

## V6 - SNMP credential management hardening (2026-10-28)

- Extends the existing Credentials tab with explicit v3 security modes, conditional fields, SHA1/256/384/512 and supported legacy MD5, AES128/192/256 and legacy DES, profile port/timeout/retries, masked input, and administrator-only visibility of entered text (never stored secrets).
- Adds add/edit/test/duplicate-without-disclosure/assign/remove-unused actions. Windows DPAPI remains scoped to the current Windows user; corrupt or unreadable stores now produce explicit errors rather than silently becoming empty stores or secrets.
- Adds per-device profile transport opt-in, bulk assignments by selection/ATS/model, assignment saving, and a System Configuration entry point while preserving the original navigation shortcut. Existing registrations, transport values, and credential assignments are not migrated or overwritten.
- Protects C01/C03/C04 and other currently successful devices with administrator confirmation before shared-profile or connection changes. Assigned profiles cannot be deleted. Both stores retain protected pre-write backups; duplicate labels and duplicate IP registrations are rejected.
- Runs layered read-only tests with assigned credentials and bounded concurrency (at most four or the lower configured limit), validates mapping on responders, persists secret-free connection history, and exports assignment/configuration status. No automatic alternate-version requests or credential guessing occur.
- Makes timeout/authentication/access-error distinctions evidence-based. SNMPv3 discovery does not prove authentication. USM engine/time/security-level reports are not mislabeled as password rejection.
- Invalidates cached capability/test evidence after profile/endpoint changes and exposes old measurements only as historical while awaiting a verified poll.
- Includes isolated regression tests, clean x64 build/publish evidence, real 15-IP diagnostics, working-device before/after/restart results, and actual WinForms screenshots in `Documentation/UPS/CredentialValidation_20261028`.
- Live acceptance remains three responding, fully mapped devices (C01/C03/C04); twelve remain unresolved. Approved per-card SNMPv3 credentials and authenticated card-settings verification are still required. No card settings, PLC acquisition code, historian schema, or production history were changed.

## V6.1 — historian sampling and retention update (2026-10-26)

- Repairs UPS SNMP connectivity diagnostics, using the legacy `vertiv_ups_dashboard.zip` as an audited reference only (see `Documentation/UPS/SnmpConnectionReport_20261026.md`).
  - New grouped capability probe (identity, UPS-MIB identity, battery, input, output, bypass, alarms, rating, plus a GETNEXT table walk). It isolates unsupported OIDs per object and caches the verified profile per unit.
  - Polls read only verified OIDs and keep partial readings, flagged "partial poll" on the dashboard. Unsupported fields show "—".
  - Phase indices are verified against the table rows.
  - A sysObjectID change invalidates the data.
  - The connection test adds an alternate-version check, so a wrong SNMP version is confirmed only with evidence.
  - Diagnostics show the responding version, a classification and the required card-side action. The exported report is a 13-column per-unit matrix.
  - Discovery shows version, round trip, model, UPS-MIB support and sysObjectID.
  - Live result: C01, C03 and C04 are fully mapped (31/31) on v2c. The other 12 units remain disconnected with an evidence-based classification. No SNMP SET is ever sent.
- Adds the UPS & ATS monitoring module (read-only SNMP, RFC 1628 UPS-MIB; see `Documentation/UPS/UpsAtsModule_20261026.md`). A new MONITORING › UPS Monitoring page shows 4 ATS banks and 15 UPS units. Each unit shows quality, output kW, derived kVA/PF, load %, source, voltage, frequency and battery. All ATS panels and UPS cards share one size. Each bank and the fleet show totals of Good units only, marked PARTIAL when units are missing. Double-click opens a detail window with per-phase measurements and OID sources, identity and quality, alarms, and a 24 h trend with integrated kWh. A separate main page, DATA & CONTROL › UPS & SNMP (administrator editing, audited), has four sections: the UPS registry and ATS banks; credential profiles (v1/v2c/v3, DPAPI-encrypted, never displayed); read-only discovery; and diagnostics with the OID map. Samples are stored every 60 s in PostgreSQL `ups_samples`. UPS alarm limits now apply to each UPS. The application never sends SNMP SET, and UPS polling is independent of PLC acquisition.
- Cleanup and release hardening (see `Documentation/Cleanup/`):
  - The build now ships only the seed `data\eoems.seed.db`. At startup it is copied to `data\eoems.db` only when no live database exists, so a rebuild or republish can never overwrite live users or the authentication audit.
  - The launcher protects the live database during publish.
  - Late UI refreshes after a page or the shell is disposed are ignored (this fixes an intermittent "No row can be added to a DataGridView … without columns" crash).
  - PLC events are unhooked and the timers disposed on close.
  - Only English satellite resources are published, which removes 13 unused language folders.
  - Regenerable build outputs were removed, and the previous builds on the Desktop were archived.
- Vertically centers the login username and password text with their field icons, including the password visibility icon, using actual control heights during layout.
- Adds calculated measurements across the live view, historian and reports. Each transformer now records apparent power (kVA) and power factor. TOTAL records total current (per phase and overall), total kW, total kVA, total PF and average voltages. The "Peak, Average & Energy" items (peak voltage L-N/L-L, peak current, peak kW, time-weighted average kW and kWh) can be selected and appear on the Excel Statistics sheet and in the PDF. Existing selections receive the new items once (recording policy v3). New channels apply only to samples recorded after the update.
- Adds configurable operator alarms. Transformer gauges no longer show GREEN/YELLOW/RED; instead they show a NORMAL / HIGH LOAD / OVERLOAD / HIGH TEMP / OVER TEMP / UNDER VOLTAGE / OVER VOLTAGE / SHED LOAD status badge with the value that triggered it. Alarm and shed-load badges flash every 0.5 s. Load %, kW, temperature and L-N voltage values turn yellow (pre-alarm) or red (alarm), and the panel border follows the worst state. Gauge colour zones follow the configured limits. A new Configuration › Alarm Limits tab (administrator only, audited to `alarm-settings-audit.jsonl`) edits under/over voltage, load % and temperature limits for Transformers and Total Load. Defaults: 215–245 V, load 70/80/90 %, temperature 65/75/90 °C. Generators, UPS and Loads/Feeders sections are prepared for future equipment.
- Improves Transformers page readability. Measurement tables use a lighter background with clearer row striping, larger bold values (11 pt) and taller rows. Status colours are now high-contrast on dark panels, and a selected cell no longer turns dark. Gauge scale numbers are larger, white with a shadow and no longer clipped. The gauge status text, kW value, MAX label, Modbus mapping/Data Quality tables and footnote are also larger.
- Stores every good acquisition snapshot that meets the configured historian interval instead of suppressing unchanged values; PLC acquisition remains independent of PostgreSQL.
- Links raw measurement retention to recording interval: 100 ms/5 days, 250 ms/10 days, 500 ms/15 days, 1 s/30 days, 2 s/45 days, 5 s/60 days, and 10 s or slower/90 days.
- Rolls expired raw measurements into 15-minute min/max/average aggregates before deleting raw rows. Application settings and configuration audit records are stored separately and are not subject to measurement retention.
- Existing installation settings were backed up, audited, and set to 100 ms/5 days. Activation of the changed writer requires the running desktop application to be closed and relaunched through `run.exe`.
- PostgreSQL write/read checks confirmed current live records; the changed Windows x64 Release publish succeeded. Full-interval behavior still requires verification after restart against the live acquisition.
- Places System Configuration, System Diagnostics, and Backup / Recovery as three tabs within Configuration; Diagnostics and Backup / Recovery are no longer separate shell pages.
- Uses the Historian / Reports segmented-tab design for Configuration sections, with a clear Configuration shell heading and consistent section titles and more legible diagnostics and backup tables.
- Adds the Electrical Operation Center name and matching shield-and-lightning Windows application and launcher icons.
- Rebuilds the historian interval table around the active 0.1–10-second recording choices, their linked maximum raw-retention days, and the currently selected policy.
- Refreshes Manual Reports measurement selection and report-window cards with larger, clearer typography and explicit saved-history/source limitations; measurement categories are ordered by electrical function.
- Corrects report coverage estimates to use the configured historian interval, and adds saved-sample minimum, maximum (peak) with UTC time, and average statistics to PDF reports as well as Excel; peak values are explicitly distinguished from waveform peaks.

## V6.0 — validated Phase 1 baseline (2026-10-22)

- Preserves the fixed Delta AS228T-A / PM5110 FC03 read-only map.
- Adds source-uncertainty-aware historical temperature/voltage analytics and removes unsupported generic thermal alarm thresholds.
- Adds host-side acquisition/cycle diagnostics, coalesced UI refresh, bounded asynchronous CSV logging, and an initial SQLite measurement historian; later V6.1 updates store measurements in PostgreSQL.
- Adds protected display refresh cadence (Auto=100 ms, 10/25/50/100/250 ms).
- Self-contained Windows x64 folder package validated; no live PLC commissioning.

## V6.0 consolidated master — source verification (2026-10-22)

- Adds assembly-backed V6 display, About dialog, and build/source/test diagnostics.
- Adds the version registry, audit evidence, and approval-gated sequential release script.
- Final master and smoke-test Release builds passed with zero warnings/errors; 182 smoke assertions passed.
- No new package was published. The release script's syntax, read-only preview, and disposable candidate build/test flow passed; approved-package and promotion branches remain untested.

## V6.1 — historian and reporting implementation (unreleased)

- Reworks all three Configuration sections into compact responsive card/table layouts, preserving PLC setting authorization and auditing, live diagnostics, backup policy, verification, and staged restore workflows.
- Rebuilds Manual Reports as a no-page-scroll three-panel workflow, keeping analyzer equipment, saved measurement selection, report period, preview, and export controls visible together; parameter tables retain internal scrolling.
- Refreshes shared page colors, card borders, section-heading icons, and chart grid/series colors for a brighter, clearer visual hierarchy.
- Expands historian health into readable two-row KPI tiles, includes recording state, and increases report-table spacing, input, and label legibility.
- Presents historian equipment metadata as left-aligned, read-only fields with muted missing-value placeholders, consistent rounded inputs, responsive two-column layout, and factory-only metadata for TOTAL.
- Adds a System Configuration equipment editor backed by the shared recording equipment model, including transformer and meter identity, rated voltages, measurement side, installation location, verified temperature source, numeric validation, administrator checks, save/cancel, dirty-state indication, persistence and audit.
- Reuses visited pages instead of reconstructing their controls and charts on each navigation; refreshes cached live-page values and invalidates configuration-dependent pages after PLC settings change.
- Stops historian and backup-page refresh timers while those pages are hidden, and moves backup archive enumeration off the WinForms UI thread.
- Adds a 100-navigation shell regression check for page-instance reuse.
- Adds analyzer-specific selected recording parameters, equipment metadata, calculated TOTAL PTOT, quality summaries, and 100 ms/250 ms/1 s/10 s rollups without changing the PLC map or polling default.
- Adds streaming historian preview, manual Excel/PDF reports, persistent daily-export jobs with catch-up/idempotence, and an administrator-protected Recording & Historian page.
- Isolates temporary historian test databases from legacy production-history migration while preserving the production default.
- Adds administrator-protected daily/manual local SQLite backups, queued-write barriers, archive hashes, integrity verification, and a startup-applied history-only restore with a verified rollback archive; app/PLC settings and authentication DB remain untouched by restore.
- Routes scheduled daily Excel reports and manual exports to the configurable `Documents\EOEMS_SCADA\Records` default, resolving the Documents folder for the current Windows user instead of relying on the executable directory or a fixed user path.
- Keeps Excel retention warning-only; raw historian pruning is governed by the interval-linked retention policy above and preserves older measurements as 15-minute aggregates.
- Release packaging, target-network PLC validation, and production deployment approval remain outstanding.

## Historical

- V5.1 portable source and package are preserved in the discovered Desktop locations.
- Earlier Phase 1 and V6 Analytics packages remain unchanged; see `Documentation\\Audits\\PROJECT_VERSION_INVENTORY.md` for hashes and evidence.
