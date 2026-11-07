# Zeta Remote cloud integration implementation report

## Approved manual Zeta sign-in

After confirming that the operator did not know how to obtain a login session token,
explicit approval was obtained for a manual sign-in dialog. **Sign in to Zeta** now opens
a separate modal email/masked-password form. On explicit submission only, the connector
POSTs `{email,password}` to the source-confirmed `/api//auth/login` without any Bearer
header, validates the bounded JSON `token`, saves only that session using Windows DPAPI,
then executes the existing claims POST and event synchronization.

The password/email are not written to configuration, diagnostics or history; password entry
is cleared on submission and completion and must be re-entered for retry. Managed request
strings exist during execution; no guarantee of runtime-memory zeroization is made.
Cancel/window close requests cancellation and prevents closing until work completes.
Existing continuous monitoring is stopped before sign-in; one service command and shared
client/network gates serialize all requests. Login has no automatic retry and obeys shared
429 cooldown. Saved credentials/history survive rejected/malformed/timed-out login; cancellation
after a successful token save does not undo that save. Protected-storage failure cannot fall
through to claims/events. Continuous polling still needs explicit Connect after manual sign-in.
No browser-storage access, automatic password login, account edit or panel commands were added.
Login success alone never means ONLINE; only validated, persisted event data does.

Implementation reuses the existing Save core under the service command gate instead of
duplicating protected-storage and snapshot update behavior. Admin authentication and offline
preview restrictions remain enforced. The new action uses the reserved footer row, leaving
the primary seven action positions and all three connection editors unchanged.

Verification: **4,469 Cloud/UI**, **13,862 legacy Fire**, **24,246 isolated settings Save**
assertions; Debug/Release **0 warnings, 0 errors**. Fixtures cover exact login method/route,
JSON fields/password escaping, no Authorization on login, session/claims separation, no-token
initial sign-in, monitoring cancellation/serialization, protected persistence failures,
401/403/429/redirect/invalid JSON/oversized response, timeout/cancellation, permissions,
no automatic startup login and actual dialog masking/clearing/cancellation/re-entry.
Existing Cloud history, repeated Save geometry and other settings regressions pass.
No real credentials or vendor network requests were used by the assistant.

Evidence: session files `zeta-manual-login-verified`, `zeta-manual-login-legacy`,
and `settings-save-isolated-zeta-login\evidence`.

Validated Release DLL SHA-256:
`775F34964180E20A4839C845850AF72405D43C5F98475A64C1CDC0330916D6CC`

Validated Release PDB SHA-256:
`7108C1E16C3618370B0705D3F6086BC7600A49E9D5D931CA4C35BC53E6EDC310`

With explicit operator approval and normal application shutdown, DLL/PDB were installed
and verified against the Release hashes above. Previous binaries were backed up and verified:
`C:\Users\M.Serhan\.copilot\session-state\19a039d2-180c-4f6e-a611-77ca58a935b1\files\zeta-login-installed-backup-20261104-171556`

Settings, credentials and historical data were not replaced. The operator approved reopening
the application to enter their own login; no actual Zeta login is claimed here. Earlier statements
below that no login endpoint exists describe previous revisions, not this approved feature.

## Vendor frontend source clarification: login session versus claims token

The operator supplied `AuthActions`, `AuthFilters`, `AuthReducer`, `APICall` and event
loader source. This establishes that login returns `auth.session`; claims authorization
POST uses that credential and exact `{}`, while event GETs use returned `auth.claims`.
`APICall` concatenates `/api/` with the leading-slash route, preserving the observed
double slash, uses POST for a JSON body and GET without one, and attaches one Bearer
Authorization header. These request semantics already match the connector.

Earlier recommendations implying that a manually issued API account key is necessarily
required were not established by evidence. The UI now explicitly requests a **login session
token**, not a returned claims token. Existing DPAPI credentials are neither reclassified
nor replaced automatically; previously stored credentials cannot be proven to be the
correct type without a validated server response. No production credential was inspected.

The frontend reads server errors through `data.err`; the connector now also recognizes
`err` string/object categories through its existing strict allowlist. It never echoes
arbitrary vendor error text, personal data or token claims. The supplied Account component
only edits account profile information and adds no missing token-issuance contract.
No account-management, login, reset, browser-storage access or panel-write mechanism was added.
The precise cause of the earlier remote 401 remains unconfirmed.

Validation: **4,417 Cloud/UI assertions**, Debug/Release **0 warnings, 0 errors**.
Evidence: session files `zeta-session-contract-verified`; fake credentials and injected HTTP
handlers only. With explicit operator approval and normal application shutdown, this revision
was installed using DLL/PDB-only replacement and verified backups:
`C:\Users\M.Serhan\.copilot\session-state\19a039d2-180c-4f6e-a611-77ca58a935b1\files\zeta-session-installed-backup-20261104-165718`

Installed DLL SHA-256:
`035D57DB1824C71D60E3AD4265E5C6D275748602F9FB722F873436344F0FDCE8`

Installed PDB SHA-256:
`8377CCC4D305080145FCB402153085D849B8A043408D3915AB5D652ADD9D70A4`

Settings, credentials and historical data were not replaced. The operator requested reopening
the app to enter their own login; no credential fields are to be captured or read.

## HTTP 401 diagnostics and observed event contract correction

### Established findings, not an inferred credential diagnosis

The supplied diagnostic snapshot reports **HTTP 401 at CLAIMS AUTHORIZATION**, monitoring
false, no successful synchronization, and stale transport **CONNECTING**. This establishes
that Zeta rejected the claims POST; a local token-format/JWT rejection does not produce an
HTTP status. The previous text "token is invalid or expired" was a generic client message,
not a server-provided expiry diagnosis. The snapshot contains no safe server error category,
so the exact underlying reason (expired/wrong credential, revoked access, or another server
authorization rule) **cannot be established from the supplied evidence**.

The earlier client attached a single `AuthenticationHeaderValue("Bearer", token)`, rejected
prefixes/whitespace before Save, decrypted the current saved token for each authenticated
request, and did not impose local JWT issuer/audience/scope/account-expiry checks. Therefore
there is no evidence that a locally accepted duplicate Bearer header or undocumented JWT
policy caused the reported remote 401. Token validity was not tested using production secrets.

The operator additionally supplied a browser HTTP 200 response for:
`GET /api//v2/sites/78392d300758/events?start=2026-10-05&end=2026-11-04`.
The code had omitted both query parameters and read provisional field names instead of
`date/category/title/location`. These are confirmed event-request/parsing defects, but
**cannot explain an earlier rejection at the claims endpoint**. Browser HTTP 200 under its
own session does not prove that the separately saved SCADA account token is authorized.
The supplied `lastEventSeen` JavaScript exception and WebSocket closure originate in the
vendor web frontend, not this WinForms connector; no vendor frontend changes are claimed.

### Corrections

- Canonicalize entered and loaded tokens: trim surrounding spaces, remove repeated
  case-insensitive `Bearer ` wrappers, reject missing/scheme-only tokens and embedded
  whitespace/control characters, then attach exactly one Bearer scheme.
  Canonical account tokens remain Windows DPAPI protected; exchanged tokens remain memory-only.
  Blank replacement still retains the saved credential; invalid replacement cannot overwrite it.
- Keep account claims/signature/expiry decisions with Zeta. The only local expiry rule is the
  existing documented claims response `expires` and its 30-second session-renewal boundary.
  Local token/DPAPI failures have no HTTP status and explicit LOCAL origin; a server response
  retains `ZETA HTTP`, exact status and CLAIMS AUTHORIZATION or EVENT REQUEST stage.
- Add the exact observed `start`/`end` query. The operator approved a rolling window:
  UTC today minus 30 days through UTC today, without deleting local history.
  No alternate endpoint, undocumented pagination, automatic login or browser-token extraction.
- Parse observed numeric/string ID, UTC-qualified `date`, `category`, `title` and `location`.
  Verify returned `hwaddr` against the requested site; reject incomplete observed records
  before any batch is persisted. Keep zone/device fields blank unless explicitly provided:
  neither title text nor `networkId` is a documented zone/address mapping.
  Existing protected history and legacy fixture-field compatibility remain intact.
  Historian can filter the returned Service category without relabeling it.
- An unauthenticated HTTPS test explicitly reports transport receipt and HTTP status;
  401/403 never produces a "successful connection" log or judges the saved token.
  Authenticated 401/403 stops the worker even with AutoReconnect enabled, invalidates the
  cached session, asks for credential renewal/permission review and manual reconnect.
  Transport is DISCONNECTED when monitoring stops; last HTTP response receipt is separate evidence.
- Sanitized diagnostics include HTTP status, failure stage, origin, renewal-required flag and
  a fixed allowlist of server categories from `WWW-Authenticate` or bounded JSON error fields.
  Raw response/error descriptions, arbitrary categories, JWT claims and credentials are excluded.
  Malformed/oversized error bodies cannot hide the original HTTP failure.
  Only a parsed, successfully persisted event response can set ONLINE; live panel stays UNKNOWN.

### Verification and deployment

**4,414 Cloud/UI assertions**, **13,862 legacy Fire assertions**,
**24,243 isolated Settings/Configuration Save assertions**. Debug and Release:
**0 warnings, 0 errors**. Fixtures cover missing/expired/malformed-claim tokens, duplicated
Bearer wrappers before Save and in older protected storage, current-token reload/cache
invalidation, server 401/403 at both stages, allowlisted/malformed error responses, DPAPI
failure with zero requests, worker completion/no retries with AutoReconnect enabled,
preserved history, exact observed date query/UTC rollover and parsed authorized event records.
UI tests verify the renewal message and continued first/repeated Save layout stability.
All authorization/HTTP behavior used injected handlers and fake credentials, never the real API.

Evidence directories under session files:
`zeta-401-verified`, `zeta-401-legacy-verified`,
and `settings-save-isolated-zeta401\evidence`.

Changed: `Services\ZetaCloudEventClient.vb`, `Services\FireConnectionService.Cloud.vb`,
`Services\FireConnectionService.vb`, `Infrastructure\FireConnectionStore.vb`,
`UI\Views\FireConnectionSettingsView.vb`, `UI\Views\FireEventsView.vb`,
`Tests\SnmpValidation\ZetaCloudValidation.cs`, `README.md` and this report.

Validated Release DLL SHA-256:
`DC63A31A0CEFF770F4338910866DB27F1C7A23E85A7DF2FD06DD8B509D6078EA`

Validated Release PDB SHA-256:
`1AEB011BAC90EE8DE9F3524D52AE7EF961C2522919FB4241D26630C8F919BC88`

With explicit operator approval, only DLL/PDB were installed after confirming the app was
closed. Installed hashes match the validated Release above. Previous binaries were backed
up and verified against their preceding Save-stability revision hashes:
`C:\Users\M.Serhan\.copilot\session-state\19a039d2-180c-4f6e-a611-77ca58a935b1\files\zeta-401-installed-backup-20261104-162525`

The app remains closed as requested; configuration, protected tokens and historical data were
not replaced, and no production communication was started. Earlier deployment records below
are historical. Actual SCADA authorization/event synchronization remains unverified; a valid renewed account
credential must be entered only through protected settings, never pasted into a diagnostic report.

## Save typography and layout stability revision

The reported BMS caption fragmentation was reproduced with an isolated Save regression:
Local Save disposed and recreated visible labels. The editor rebuilt unchanged fields on
every Save and constrained captions while parent tables were still suspended.

Save now retains the existing controls and refreshes mapping/credential/status presentation
without rebuilding the editor. Protocol changes still rebuild their own fields, but the
viewport is suspended with the tables and caption measurements run only after layout resumes.
Fixed-mode viewport layout also resolves wrap constraints before the first Save rather than
leaving stale widths for a later layout timer. No polling, mapping approval, credential
protection, role validation or persistence behavior was relaxed.

Verified with:
- **24,243 focused Settings/Configuration assertions** at 980x600, 1300x650 and 1620x830.
  First and repeated real UI Save handlers preserve visible controls, fonts and field bounds
  for PLC, alarm limits, SNMP registry/profile, Web Card credentials, backup policy, acquisition
  and raw recording. Configuration PLC saves exercise the same model-update call as the shell;
  cached sections, alarm and backup saves, grid/header fonts and settled layout are checked.
  PLC/acquisition/recording/backup persistence callbacks are fixtures, not production services.
- **4,348 Cloud/UI assertions**, including first/repeated Save identity, complete font tuple,
  geometry and label wrap limits immediately and after layout settles in Cloud, Local,
  vendor BMS and BMS Modbus at 1300x650 and 1620x830.
- **13,862 legacy Fire assertions**; Debug and Release builds have **0 warnings, 0 errors**.

Evidence directories under the session files:
`settings-save-isolated-verified\evidence`, `settings-save-cloud-verified`,
and `settings-save-legacy-verified`. The dedicated settings fixture runs only from a deliberately
staged `settings-save-isolated-` executable directory; all settings and fake protected
credentials belong to that copy. No production settings, real credentials or vendor API
were accessed. This is a focused Save-stability audit, not a claim that the unrelated full
Settings/Historian UI suite passes.

Changed: `UI\Views\LegacyFireConnectionSettingsView.vb`,
`Tests\SnmpValidation\SettingsUiValidation.cs`, `ZetaCloudValidation.cs`, `Program.cs`,
`README.md` and this report.

Validated Release DLL SHA-256:
`E77CDF94D0E85DF8F964031FC0D9E5C4EFC041B744DE3B78CC4A2A935241863B`

Validated Release PDB SHA-256:
`48C66DA497454F2C786064B7590D88A80E8BA4ED9445A49D6D84B5811E7562F2`

With explicit operator approval, only the DLL/PDB were installed after confirming the app
was closed. Installed hashes match the validated Release hashes above; previous binary
backup hashes match the preceding authorization revision. Backup:
`C:\Users\M.Serhan\.copilot\session-state\19a039d2-180c-4f6e-a611-77ca58a935b1\files\settings-save-installed-backup-20261104-155149`

The app remains closed as requested; settings/data/credentials were not replaced and no
production communication was started. Earlier deployment records below remain historical.

## Claims authorization exchange revision

The operator supplied the observed `POST https://www.zetaremote.com/api//v2/claims/auth`
response shape (`token`, numeric Unix `expires`) and explicitly confirmed request body `{}`.
They approved implementing exchange and expiry-based renewal. No supplied credential was used,
copied into source/tests/reports, or sent to the vendor. Exposed tokens should be revoked/rotated.

The connector now sends the DPAPI-protected **account token** to this exact authorization route.
The returned short-lived token is memory-only and used for the existing event GET. It is reused
until the next request has at most 30 seconds remaining, when a new exchange is required.
Account expiry is not extended by this exchange: an invalid/expired account token still requires
operator replacement. Existing saved tokens are not silently reclassified or extracted from a browser;
if a short-lived panel token was previously saved, replace it with an authorized account token.

Host validation, no redirects/cookies, ordinary TLS checks, shared serialized service requests,
polling/backoff and cancellation remain intact. The exchange and event GET share one timeout budget.
Authorization JSON is limited to 64 KiB, requires a valid token and future Unix expiry, and never
falls through to events after validation/HTTP failure. Session identity is bound to the account-token
fingerprint and site; Save invalidates it. Event 401/403 invalidates the session without retry loops;
401/403 from either route stops monitoring, and 429 from either honors the shared cooldown.
No panel control commands, account login or account-refresh endpoint was added.

The Cloud UI now labels the input as **account token** and explains the POST-then-GET sequence.
Status/credential-free diagnostics expose the last accepted session expiry; hover over status
to read its full text. Auth-only HTTP 200 never establishes event access or live panel NORMAL.
The existing event route/schema remains provisional until a sanitized actual event response or
official vendor contract is verified.

Validation: **710 Cloud/UI assertions**, **13,862 legacy Fire assertions**; Debug/Release
**0 warnings, 0 errors**. Fixtures cover exact `{}` and auth/event headers, 31/30-second expiry
boundary, renewal, account/site changes, Save invalidation, concurrent exchange serialization,
malformed/expired/oversized auth responses, auth HTTP failures/rate limits, cancellation,
UI geometry and existing persistence/roles/history behavior. No actual API was contacted.

Changed: `Services\ZetaCloudEventClient.vb`, both `FireConnectionService` partials,
`UI\Views\FireConnectionSettingsView.vb`, shared `FireConnectionLayout.vb`,
`Tests\SnmpValidation\ZetaCloudValidation.cs`, this report and `README.md`.
No configuration, credential or event database was overwritten.

Release DLL SHA-256:
`3A534D5F4D4DD3BB5152BF7DCA5F333B481635781EF78FFE954BB18AAE66606D`

Evidence:
`C:\Users\M.Serhan\.copilot\session-state\19a039d2-180c-4f6e-a611-77ca58a935b1\files\fire-auth-validation-final`
and `fire-auth-legacy-regression` in the same session files directory.
With explicit operator approval, this revision was installed by copying DLL/PDB only after
confirming the app was closed. Installed files match their Release hashes; previous binaries
were backed up and hash-verified at:
`C:\Users\M.Serhan\.copilot\session-state\19a039d2-180c-4f6e-a611-77ca58a935b1\files\fire-auth-installed-backup-20261104-153320`

Installed PDB SHA-256:
`90AEC7C3A326FA526C779E8E4DFD3372D5F4A311BA8DBF5627C4A732EA7DFEF8`

The app was left closed; no production-token validation, automatic connection, real API test or
live commissioning was initiated. Settings, credentials and data were not changed.
Earlier deployment records below refer to their respective older binaries; GET-only descriptions
are historical and superseded here.

## Unified connection editor layout revision

Cloud, Local Modbus and Zeta BMS now use matching saved-status cards, a two-card settings
workspace and a fixed bottom action dock. Primary action rectangles are measured to remain
identical across modes. Mode-specific operations occupy a reserved second row, so switching
protocols does not move Test Connection, Save, Connect, Disconnect, Reconnect, Clear or Copy.
The settings surface fills the same available height in all three modes.

BMS reference fields are arranged horizontally; vendor BMS protocol/authentication fields
remain available without the former stacked Advanced sections. The independently verified
BMS Modbus option is retained. Field rows are measured at their actual available widths,
avoiding WinForms autosize inflation and overlapping verification text. This helper is shared
with Cloud so labels and inputs remain visible together. Smaller windows retain a settings-only
scroll fallback; the bottom actions do not scroll. Hidden-page timers and all existing
save/test/credential/protocol behavior remain unchanged.

Changed: new `UI\Views\FireConnectionLayout.vb`, both connection settings editors,
`Tests\SnmpValidation\ZetaCloudValidation.cs`, this report and `README.md`.
No configuration, token, history database or network/protocol implementation was changed.
This section supersedes only the connection-editor layout in the historical revisions below.

Verification: **665 Cloud/UI assertions** and **13,862 legacy Fire assertions** passed;
Debug and Release builds have **0 warnings and 0 errors**. Fixtures measure identical
primary button rectangles, visible input/caption areas, no scrollbars and full-height surfaces
at **1300x600, 1300x650 and 1620x830**, including both vendor BMS and BMS Modbus.
Existing smaller/narrow layouts, repeated saving, roles, cancellation and credential
preservation remain covered. HTTP fixtures do not contact the actual vendor API.

With operator approval, only DLL/PDB were installed with verified backups and hashes.
After operator sign-in, actual installed Cloud, Local and BMS pages were inspected at the
desktop resolution. Their settings cards and primary action positions match, and BMS has
no scrollbar. No Save, Connect or Test action was invoked during this inspection. The
displayed HTTP 401 / Auth NOT TESTED status is not proof of authorized API access.
The final small status-text correction was fixture-verified, installed after the operator
closed the app normally, and reopened responsive at Secure Sign In (PID 31588).

Final installed SHA-256:
- DLL: `3A108214B118C04B675F2257BE88B4A76FE947653183565EAEDB23C605FA0921`
- PDB: `B8AAFC9DA56B96255C7EA11F716BA1E387967865B5C872A00403B23D5B2BCED2`

Evidence and backups:
- `C:\Users\M.Serhan\.copilot\session-state\19a039d2-180c-4f6e-a611-77ca58a935b1\files\fire-uniform-validation-final`
- `C:\Users\M.Serhan\.copilot\session-state\19a039d2-180c-4f6e-a611-77ca58a935b1\files\fire-uniform-legacy-regression`
- `C:\Users\M.Serhan\.copilot\session-state\19a039d2-180c-4f6e-a611-77ca58a935b1\files\fire-uniform-installed`
- Pre-layout installation: `C:\Users\M.Serhan\.copilot\session-state\19a039d2-180c-4f6e-a611-77ca58a935b1\files\fire-uniform-installed-backup-20261104-151123`
- Before final status correction: `C:\Users\M.Serhan\.copilot\session-state\19a039d2-180c-4f6e-a611-77ca58a935b1\files\fire-uniform-final-installed-backup-20261104-151918`

## Navigation revision: Fire Events in Historian

The subsequent requested UI reorganization supersedes the original layout described
below:

- **Recent Fire Events** is now **Historian / Reports -> Fire Events**, a full-height
  page with panel selection, stored-event refresh, search/type filters, details, CSV,
  and separately labeled administrator-only **Sync Cloud Events** with cancellation.
- Configuration no longer contains Recent Fire Events, Communication Diagnostics or
  Advanced / Alternative Connections tabs.
- **Copy Diagnostics** is directly beside **Clear Diagnostic Logs**. It copies the
  existing service's credential-free snapshot and reports clipboard failures.
- Fire System has one direct **Connection type** selector: Cloud API, Local Modbus TCP
  or Zeta BMS. Cloud retains the API/token settings; Local/BMS show their existing
  settings inline without another nested primary-connection selector. BMS retains its
  vendor/independently verified Modbus choice.
- Switching editors does not connect or save. Drafts are cached until saved backend
  settings change; Cloud Save preserves the latest legacy settings and credentials.
  The Local editor supports repeated saving without losing its visible inputs.
- Historian and Configuration share the shell's same `FirePanelManager` and services.
  No duplicate worker or communication service was introduced. Panel additions/renames/
  removals update the historical selector. Hidden history timers stop without stopping
  polling. Local Modbus response timestamps are not cloud synchronization timestamps.
- The new Historian page hides acquisition health/save rows only while Fire Events is
  selected. Existing five pages and default Equipment history selection are unchanged.

Affected revision files: `FireConnectionSettingsView.vb`,
`LegacyFireConnectionSettingsView.vb`, new `FireEventsView.vb`, `HistorianView.vb`,
`MainShellForm.vb`, `ZetaCloudValidation.cs`, historian tab-shape assertions in
`SettingsUiValidation.cs` / `PowerEventValidation.cs`, and `README.md`.
No configuration, token, history database or protocol implementation was changed.

Revision verification: **197 Cloud/UI assertions**, **13,862 legacy Fire assertions**,
**156 power-event assertions**, and **75 active-event assertions** passed. Debug and
Release compile with **0 warnings and 0 errors**. Captured layouts at shell-sized
1080x520, 1300x650 and 1620x830 show the full-height historian event grid and direct
Cloud/Local/BMS editors. The tests exercise actual copied diagnostics, saved token
preservation, repeated Local Save, unchanged service ownership, read-only history
browsing, administrator synchronization and existing historian navigation.
No actual network/API access was performed for this revision.

Revision Release DLL SHA-256:
`E10E2DFF9B01123A5A550622575DEBE57822C83CEDBBE43C5F27D2B6CC29EC27`

Evidence:
`C:\Users\M.Serhan\.copilot\session-state\19a039d2-180c-4f6e-a611-77ca58a935b1\files\fire-navigation-validation-final`

With explicit operator approval, this revision was installed by copying DLL/PDB only.
Both installed hashes matched their Release files. The preceding installed files were
backed up and hash-verified at:

`C:\Users\M.Serhan\.copilot\session-state\19a039d2-180c-4f6e-a611-77ca58a935b1\files\fire-navigation-installed-backup-20261104-143455`

The previous app had closed normally. The revised app opened responsive (PID 9560);
the operator signed in through the normal UI. Actual installed-window inspection verified
the Cloud/Local/BMS selector, Copy Diagnostics beside Clear Diagnostic Logs, removal of
the lower settings tabs, and the full-height **Historian / Reports -> Fire Events** page.
Navigation did not send any authorized cloud request or change connection settings.
No token values were inspected.

Installed UI captures:
`C:\Users\M.Serhan\.copilot\session-state\19a039d2-180c-4f6e-a611-77ca58a935b1\files\fire-navigation-installed`

The older installation record and initial implementation details below refer to the
prior layout.

## Result and readiness

The Configuration fire editor is now cloud-first, with a read-only event connector,
protected token storage, persistent deduplication, working communication actions,
separate diagnostics, and optional legacy Modbus/BMS access.

**Isolated implementation validation passed. The integration is NOT live commissioned.**
No real token was supplied and no authenticated Zeta event request was tested. Neither
the proposed site ID nor the actual vendor response schema has been confirmed.
No live fire-panel condition is inferred. With explicit operator approval, the verified
Release DLL/PDB were installed and the application reopened successfully. The operator
signed in normally; the new Fire page was inspected in the actual installed shell.

## Initial inspection and authorized scope

The workspace contained the existing fire settings, service, store, Modbus driver,
legacy UI and validation suite. Contrary to the attached specification's assumption,
it did not contain `ZetaCloudEventClient.vb` or the previously described connector
report. The existing Cloud mode explicitly reported unsupported operation.

The discrepancy was raised before implementation. The user explicitly authorized
implementing the missing connector inside the current project. There is one new
GET client, not a second application or a duplicate polling service.

The six original core fire files were backed up, with matching SHA-256 hashes, under:

`C:\Users\M.Serhan\.copilot\session-state\19a039d2-180c-4f6e-a611-77ca58a935b1\files\fire-implementation-before-rebuild-20261104-132553`

## Files and responsibilities

| File | Change |
|---|---|
| `UI\Views\FireConnectionSettingsView.vb` | New primary cloud UI: status, two-column site/API form, token controls, communication actions, recent events/search/details/CSV, diagnostics and optional alternative connections. |
| `UI\Views\LegacyFireConnectionSettingsView.vb` | Original editor preserved under Advanced, rather than left as the primary page. It opens at Local Modbus when the new Cloud connector is active and retains legacy profile/diagnostic workflows. |
| `Domain\FireConnectionSettings.vb` | Site name, cloud defaults, exact route/host/site validation, cloud polling/freshness limits, compatible legacy profiles. |
| `Infrastructure\FireConnectionStore.vb` | Format 3 with independent DPAPI `ProtectedCloudToken`; format 1/2 migration and legacy encrypted credential fields retained. |
| `Services\ZetaCloudEventClient.vb` | Single approved-host GET client, bounded provisional JSON parser, cancellation/deadline and safe HTTP/DNS/TLS diagnostics. |
| `Infrastructure\ZetaCloudEventHistory.vb` | SQLite sidecar with DPAPI-protected event payloads, hashed keys, transactional deduplication, recent reads/counts and cancellation before commit. Existing duplicates are checked before unnecessary DPAPI encryption. |
| `Services\FireConnectionService.vb` | Reused lifecycle, command/request gates and diagnostic infrastructure; cloud routing, token presence, freshness and export protection. FC04 proof remains required only for Modbus. |
| `Services\FireConnectionService.Cloud.vb` | Cloud behavior within the same partial service: tests, sequential background polling, synchronization, cooldown/backoff, authorization failure stop and diagnostic-only clearing. |
| `Services\FirePanelManager.vb` | Retained cloud-history sidecars prevent reuse of a removed panel's numeric ID. Existing panel service ownership is unchanged. |
| `Tests\SnmpValidation\ZetaCloudValidation.cs` | New isolated client/storage/service/actual-control validation. |
| `Tests\SnmpValidation\FireConnectionValidation.cs` | Preserved original tests against the retained legacy editor; limitation text distinguishes the new separate cloud fixtures. |
| `Tests\SnmpValidation\Program.cs` | Added `--zeta-cloud-unit` entry. |
| `README.md` | Updated cloud setup, security, history, tests and commissioning limitations. |

`FireModbusClient.vb` is unchanged. Transformer/PLC acquisition, UPS polling, historian
recording, reporting and alarm settings implementations were not changed for this task.
The existing multi-panel host still constructs `FireConnectionSettingsView` normally.

## Connection and credential behavior

- Production requests are GET only to
  `https://www.zetaremote.com/api//v2/sites/{site_id}/events`.
  The observed double slash is preserved and checked by fixtures.
- Redirect following and cookies are disabled; normal certificate validation is enabled.
  Other hosts, embedded credentials, alternate schemes/routes and site path traversal fail
  before a request is sent.
- Test Connection sends no authorization header. An HTTPS response, including HTTP 401,
  proves transport response only and does not claim token authentication or ONLINE state.
- Authenticated actions use only the separately saved new Bearer token. Old Cloud
  username/password fields are never an authorization fallback.
- Token values are protected with Windows DPAPI CurrentUser. The previous token is never
  populated in the input. Blank retains it; replacement Save clears and remasks the draft;
  Clear Token requires confirmation and leaves history and legacy credentials intact.
- Save precedes authenticated tests and Connect/Reconnect. Administrator identity and
  role checks are applied in the service, not just by disabled UI controls.
- Status describes the saved site. A test for a different draft site cannot overwrite
  the saved site's monitoring/synchronization evidence.
- The UI displays supplied panel/module/site references, not discovered hardware identity.
  Editing the proposed site ID removes the association with the supplied reference MAC.

## Polling and state

The existing service owns one worker and the shared request semaphore. Tests and polling
cannot overlap. Disk/token work and synchronization run away from the UI thread.
Opening, hiding or closing Settings does not start/stop the background worker.

Cloud defaults are polling 30 seconds, request timeout 10 seconds, freshness 90 seconds.
The form accepts polling 30-570 seconds, timeout 1-30 seconds, and freshness through
600 seconds, with freshness covering polling plus request timeout. Polling waits after
completion, so actual start-to-start spacing can be longer than the selected interval.

Failures use exponential delays capped at 900 seconds, unless the poll interval or a
larger server Retry-After dominates. HTTP 429 also blocks immediate manual retries and
Reconnect requests from bypassing the cooldown. Long waits are cancelable. HTTP 401/403
stops automatic polling; disabled automatic reconnection stops after the first failed
Cloud request. Disconnect cancels pending requests and waits for the worker.

- ONLINE: supported authorized event response validated and history successfully persisted.
- DEGRADED: previous synchronization exists but requests fail or freshness expires.
- OFFLINE: failed/unavailable communication without successful current synchronization,
  or intentional connector disconnection.
- NOT CONFIGURED: required new token/settings are absent.
- AUTHENTICATION FAILED: authorized HTTP 401/403.
- CONNECTING: saved connector is starting.

The current fire-panel condition remains **UNKNOWN**, including when Cloud is ONLINE.
No reset, silence, disable, sounder, panel-setting or physical mode commands exist.
This historical-event integration is not a sole life-safety annunciation source.

## Event history and provisional schema

The parser currently accepts an event array, or an object whose `events` value is an
array. The provisional known fields are `id`, `timestamp`, `event_type`, `zone`,
`device_address`, `description` and `message`. Strings/numeric scalar fields are
validated; timestamps require an explicit UTC offset and are normalized to UTC.
Every event needs a vendor ID or message. Missing zone/address/description/type/time
remains missing, never fabricated. Source identifies the known connector origin.
Fire/Fault/System filters match exact returned type strings; they do not infer categories.

HTML/login responses, malformed JSON, unsupported structure/field types, invalid timestamps,
more than 10,000 events or more than 2 MiB fail explicitly. No guessed pagination,
alternate endpoint, website login or browser-token extraction is implemented.
These fixtures establish internal behavior only, not a vendor-supported public API contract.

`<settings-file>.cloud-events.db` stores DPAPI-protected JSON payloads alongside hashed
site/identity keys. **SQLite itself is not a fully encrypted database.** Unique keys
combine site plus vendor ID; ID-less records use a stable normalized event fingerprint.
Transactions prevent partial batches and deduplicate repeated/overlapping responses,
including after service reload. No automatic history purge is implemented.

The page reads the latest 200 inserted stored events for the displayed site and orders
that selection by available event timestamps. Search/filters and CSV apply to this
displayed selection, not the entire database. Full rows remain persisted. CSV escapes
quotes and neutralizes spreadsheet formulas, including leading whitespace.
Exports cannot overwrite the protected settings, diagnostic logs or cloud database.

Clear Diagnostic Logs clears communication diagnostic files only. It never removes
cloud fire events. Removing an optional panel leaves its sidecar as an archive and
prevents that panel ID being reused. No archive-management UI was added.
Back up settings and sidecar together; the existing historian archive does not include them.
DPAPI requires the original Windows account; moving files alone does not migrate secrets.

## Verification executed

Both Debug harness and Release application build with **0 warnings and 0 errors** using
the existing dependencies and `--no-restore`. No packages were added.

| Validation | Result |
|---|---|
| `--zeta-cloud-unit` | **162 assertions passed**, no real API contacted. |
| `--fire-connection` | **13,862 legacy assertions passed** after final backend changes. |
| `--alarm-step-unit` | **202 assertions passed**. |
| `--analyzer-graph-unit` | **167 assertions passed**, plus 192 signals x 3,600 points in 80 graph controls. |
| `--ups-estimate-unit` | **58 assertions passed**. |
| `--diagnostics-unit` | **23 assertions passed**. |

Cloud checks include:

- Exact GET URL/double slash and authorization header; no header on transport tests.
- HTTP 401/403/404/429/5xx/redirect handling, Retry-After, DNS/TLS classification,
  timeout, caller cancellation, response limits and unsupported JSON.
- Missing-field behavior, explicit-offset timestamps, no fabricated event metadata.
- Independent token migration/protection/clear; protected event data and reload.
- Duplicate vendor IDs, ID-less fingerprints, overlapping responses, site separation,
  retained history after diagnostic clearing and optional-panel removal.
- Actual shared-gate non-overlap; request cancellation; stale-state boundary; failed
  connection state; authorization-stop behavior; disabled auto-recovery.
- A complete real-time fixture polling cycle: two background GET starts were
  **at least 30 seconds apart**, with no Settings page present.
- Actual control handlers for Save, token replacement/show-hide, HTTPS/authentication/
  retrieval tests, cancellation, Connect/Disconnect/Reconnect, search/type filter,
  history expansion and diagnostic clearing.
- New primary Save preserves newer alternative backend settings; cached legacy editor
  refreshes when saved backend settings change.
- Blank/masked token on reopening, administrator restrictions, visible UNKNOWN condition.
- Responsive primary viewports 1080x520, 1300x650 and 1620x830, representing content space
  within 1366x768, 1600x900 and 1920x1080 desktop shells, plus narrow 720x600 reflow.
  Tests check readable inputs, card containment, top status visibility and complete
  scroll access to the final history-table border. Legacy tests cover full-window layouts.

Evidence:

- `C:\Users\M.Serhan\.copilot\session-state\19a039d2-180c-4f6e-a611-77ca58a935b1\files\zeta-cloud-validation-final`
- `C:\Users\M.Serhan\.copilot\session-state\19a039d2-180c-4f6e-a611-77ca58a935b1\files\fire-legacy-regression-final`
- `C:\Users\M.Serhan\.copilot\session-state\19a039d2-180c-4f6e-a611-77ca58a935b1\files\fire-change-analyzer-regression`
- `C:\Users\M.Serhan\.copilot\session-state\19a039d2-180c-4f6e-a611-77ca58a935b1\files\fire-change-estimate-regression`

The tests use temporary configuration/storage, not installed credentials or production
settings. Service/storage reload, new view construction and the installed application's
normal restart/sign-in were verified. The entire hardware/
PostgreSQL/full-system suite was not rerun. The previously observed unrelated historian
980-pixel layout failure is not claimed fixed by this task.

## Build artifact

Release application:
`bin\Release\net10.0-windows\EOEMS.PowerTransformersScadaUI.V5.dll`

SHA-256:
`7C932EA11EFB94E71E041428D6CFCE272F251B3B8FAF84C14A0ADB4FAEBC8895`

Approved deployment copied only DLL/PDB after verifying the installed binary backup:

`C:\Users\M.Serhan\.copilot\session-state\19a039d2-180c-4f6e-a611-77ca58a935b1\files\fire-cloud-installed-backup-20261104-140606`

Installed DLL SHA-256 matches the Release hash above. Installed PDB SHA-256:
`164058787851420EF93E604A3B8689CF9A568B4A7EA6B8403B4A550371DFBE4E`.

The previous app was already closed; no process was force-terminated. Data/settings/
credentials were not replaced by deployment. Startup exposed a responsive Secure Sign In
window; after the operator signed in, the installed Fire page rendered in the actual
1920-class desktop shell. The running process was PID 43744 at verification.

Installed UI evidence:
`C:\Users\M.Serhan\.copilot\session-state\19a039d2-180c-4f6e-a611-77ca58a935b1\files\zeta-cloud-installed\fire-page-installed.png`

The inspected page showed **HTTP 401, 668 ms**, **Auth: NOT TESTED**, no new configured
token and **Live fire panel: UNKNOWN**. This is observed unauthenticated HTTPS response
evidence from the installed UI, not an authorized event retrieval or a verified site/API
schema. The connector did not claim ONLINE or populate fabricated fire events.

## Remaining authorized commissioning

1. Deployment/restart and normal sign-in are complete. Continue in the installed Fire page.
2. Verify site ID and permissions with the vendor. Enter an independently authorized
   token directly in the masked application field, not in this report or source.
3. Save, Test Connection, Test Authentication and Test Event Retrieval.
4. Confirm the exact double-slash route and actual JSON schema. Unexpected formats must
   remain explicit failures until the vendor contract is verified and matching tests updated.
5. Confirm returned event timestamps/types/IDs against vendor evidence and assess whether
   documented pagination is needed for complete backfill.
6. Enable Connect/automatic startup only after successful authorized verification.
7. Retain the certified fire system and established annunciation procedures independently.

There is **no successful authenticated live API result to report**. The observed HTTP 401
does not establish real credentials, a supported public endpoint, authorized site access,
an event schema or panel condition.
