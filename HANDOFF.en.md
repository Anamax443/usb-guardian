# HANDOFF – USB Guardian project

*🇬🇧 English · [🇨🇿 Čeština](HANDOFF.md)*

**Date:** 2026-06-19 · **Repo:** `Anamax443/usb-guardian` · **Author:** Milan Trnka (AXIMA)

Document for whoever takes over the project. Architecture: [docs/architecture.md](docs/architecture.md),
functional description: [README.en.md](README.en.md).

## 1. What it is

Monitoring of storage media on company stations (NIS2). The agent on a station detects connected
USB/SD/disk, compares against a signed whitelist and warns / blocks; it pushes incidents to the API.
The server console aggregates data, keeps a station inventory from AD and shows where the agent is missing.

## 2. Current Live State

| | |
|---|---|
| **Domain** | `domena.loc` |
| **DB** | SQL Server `SQL_SERVER` (= `SQL_SERVER_IP`), database `USBGuardian`, scripts `database/01–07` applied, **`08_deploy_ignored.sql`**, **`09_activity_log.sql` = activity log (2026-09-04)**; **+ `GRANT DELETE ON dbo.WhitelistDevices` to the console account** (catalog deletion – applied manually) **+ `ActivityLog` grants**: `SELECT,INSERT` for `APP_SERVER$` and `gmsa-api$`, `EXECUTE ON sp_PurgeActivityLog` for `gmsa-api$` |
| **API** | `SQL_SERVER`, Windows service "USB Guardian API", install `C:\USBGuardian.Api`, gMSA `DOMENA\gmsa-api$`; **HTTPS `:5443`** (self-signed, **PIN `API_CERT_THUMBPRINT`**) – HTTP `:5050` closed in production, `Development` only. **Live version via `GET /api/version`** |
| **Version/commit (check)** | console footer + `:4200/api/version`; API `:5443/api/version`; agent reports commit → console "Agent version". All stamped by `git rev-parse` (MSBuild) |
| **Admin console** | **live** `http://APP_SERVER_IP:4200/` (`APP_SERVER`), service `USBGuardianConsole`, `C:\Apps\USBGuardianConsole`, self-contained |
| **Console account** | **LocalSystem** = `DOMENA\APP_SERVER$` (SQL grant: read all + write Computers/WhitelistDevices/WhitelistVersions/AppSettings) |
| **Console authorization** | AD `DOMENA\IT-Admins` + whitelist `DOMENA\it-admin` (+ DB list from Settings) |
| **Agent↔API encryption** | HTTPS + **thumbprint pinning** (no CA) — verified end-to-end (heartbeat OK from PC-01) |
| **AD sync** | enabled 60 min + on-demand; **213 in AD, ~212 without agent** |
| **Live commit** (2026-09-04 15:21) | **console `3a6a2b2`** (redeployed: the "Incident queue (spool)" check verified live via `/api/health` – 16 checks, the new one reports `ok`/`empty`) · **API `a6bcfaf`** (redeployed: durable incident spool `8fbfa6d` + spool monitoring `2766344`, plus the earlier `297ac7a`) · **agent beta `924b9b8`** (BARTKOVAJW11, CERNYSW11, TRNKAMW11N) · **agent stable `cb8ef1d`** (PC-01/TRNKAMW11, rest of the fleet — `56b4235`/the whitelist-expiry fix is git-only so far, not rolled out to the fleet). Local console verified end-to-end on CERNYSW11 — see 5.11. External security audit + remediation — see 5.12 |
| **Agent rollout – the routine that works** | package → archive `…\USBGuardianAgentVersions\<commit>` → **beta to a single station** (temporarily overwritten `update-beta.txt`) → verify → beta to the rest → only then **stable**. Log `…\deploy\update-agent.log`; the console's "Agent version" only catches up on the next heartbeat (≤2 min), so right after a rollout it still shows the old one |
| **Console – pages** | Overview (filter+aggregation+sort, capacity, **CSV export + manager report with charts**), Stations (AD inventory + "Agents gone silent" + "Request data" + **Deployment / bulk exclude-include**), Whitelist (**capacity + catalog filter + auto-published signed version**), Settings (enforcement/access/email/alerts/monitoring/auto-enrollment+default PC/retention/**Maintenance: reload settings**), **Database**, **Health checks**, Documentation (+HTML animation) |
| **Enforcement (P1-3)** | **whitelist 1:1** (server-side auto-sign, internal RSA key on APP_SERVER) → **enforcement** server→agent (`policy.enforce` in heartbeat) → **break-glass** (local console 5080, offline, logged, cleared on sync) + **auto-re-enable** + whitelist reconciliation. Local console: service restart, break-glass, whitelist list |
| **Deploy account (auto-enroll)** | **gMSA `DOMENA\gmsa-deploy$`** – in `Workstation-Admins` (admin on clients) **and local admin on SQL_SERVER** (API deploy); installed on `APP_SERVER`; deploy task `USBGuardian-AutoDeploy` (under gMSA, via CIM) |
| **Agent (test) PC-01** | **PILOT SUCCESSFUL** – `PC-01` (own workstation); service "USB Guardian" RUNNING, heartbeat + **incidents flowing into DB**. Agent live **`f2bb194`** – user attribution, client 100% (watchdog+toast), **enforcement P1-3 + auto-re-enable + reliable unblock + re-block of connected media**. Updating the agent needs elevation (UAC) → run by the user (build staged on APP_SERVER) |

## 3. Key decisions (why)

- **Push, not pull** – 500+ clients behind NAT/firewall; the agent only needs an outbound connection.
- **Two-tier** – operations (console, AD sync) on app server `APP_SERVER`, DB is just storage on SQL_SERVER.
  (Note: the API still runs on SQL_SERVER; moving it to APP_SERVER is planned hardening.)
- **Console = .NET/Blazor**, not Node – reuses EF models from the API (linked `DbModels`/`AppDbContext`),
  one language, ASP.NET Core is already on the server.
- **Agent local console via `HttpListener`**, not Kestrel – the agent does not need the ASP.NET Core runtime.
- **Keyed by hostname, not IP** – stations have dynamic IPs.
- **Whitelist RSA private key never on the server** – publishing a signed version is an offline step (NIS2).
- **Encryption without a CA** – the API generates its own self-signed cert (`MachineKeySet`, NOT EphemeralKeySet!),
  the agent verifies it via **thumbprint pinning**. Independent of the company CA / external certs.
- **Central settings in DB** (`AppSettings`) – enforcement, access, e-mail; the agent still runs by its local
  `policy.mode` (distribution via heartbeat is the next step).
- **Portability** – no company values in code; everything in `*.local.json`, domain from `new DirectoryEntry()`.

> Fixed latent repo bugs: missing authorization policy `USBGuardianClients` (controllers returned 500);
> `EphemeralKeySet → MachineKeySet` (otherwise Schannel won't do the server TLS handshake).

## 4. Console deploy (manual, from PC-01)

it-admin has admin on `APP_SERVER`; WinRM was closed → deploy via **SMB + remote `sc.exe`** (ports 135/445):

```powershell
dotnet publish server\USBGuardian.Admin -c Release -r win-x64 --self-contained -o D:\deploy\USBGuardianConsole
sc.exe \\APP_SERVER_IP stop USBGuardianConsole
robocopy D:\deploy\USBGuardianConsole \\APP_SERVER_IP\C$\Apps\USBGuardianConsole /E /XF appsettings.local.json
sc.exe \\APP_SERVER_IP start USBGuardianConsole
```

Firewall `:4200` was created via DCOM/CIM. Configuration on the server:
`C:\Apps\USBGuardianConsole\appsettings.local.json` (see `*.example`).

## 5. Status and next steps

### 5.1 Done and live
- **DB / incidents = 100 %** — agent → API → DB → console, the whole path runs (Overview shows incidents from PC-01).
  **Key fix:** the API had an unfinished queue refactor — `IncidentsController` required `IncidentQueue`, but
  `Program.cs` **did not register it in DI** → **500 on every `/api/incidents`** (heartbeat ran without that dependency).
  After `AddSingleton<IncidentQueue>` + `AddHostedService<IncidentQueueWorker>` the controller returns 202 + the worker writes.
- **Version/commit on all components** — the console + API have `GET /api/version`, the agent reports the real commit
  (`AppInfo` + MSBuild `git rev-parse` stamp) → in the console "Agent version" shows the deployed commit per station.
- **Console:** "Agents gone silent" tile (threshold `comm.silentAfterMinutes`), "Request data" (`ReportNow` via
  `AppSettings cmd.report.<HOST>`), sortable "Detailed" table, auto-enrollment orchestrator (default OFF + dry-run).
- **Serial trim fix** — WMI returns the serial with spaces (`"WX92D622N4PE    "`) → didn't match the whitelist
  ("Approved=no" + the agent didn't recognize it as whitelisted). The agent trims at WMI parse, the console in `Approved`.
- **Console – capacity, export, retention, DB page (DONE):**
  - **Capacity** of the medium in the Overview (cumulated + detailed) and in the Whitelist catalog (pulled from incidents).
  - **Export** from the Overview (inherits the filter): `⬇ CSV` (Excel) and `📊 Report` = manager summary (KPIs + top
    users/stations/media), printable HTML → PDF. Endpoints `/export/incidents.csv` and `/export/manager` (auth-protected).
  - **Data retention** (Settings → Retention): `retention.enabled/incidentDays/lastRun` in `AppSettings`; the deletion is
    done by the **API** (`RetentionService`, every 6 h, `db_datawriter`). Default off. **Requires API redeploy** (see 5.2).
  - **Database** (new page): table row counts, incident range, AppSettings dump, last 20 incidents.
  - **Reliable commit stamp** across all components (console/API/agent) — footer/`/api/version` now show exactly the
    deployed commit even for unrelated changes (generated `GitCommit.g.cs`, forces recompile when the commit changes).
- **Agent local console (extended):** `http://127.0.0.1:5080/` (loopback, admin-only, read-only; `localConsole.enabled`).
  Now shows the **list of approved devices (whitelist)** + agent version (commit), alongside whitelist status, WMI,
  queue, connected media and recent events. On-station diagnostics (even offline from the server).
- **User attribution (DONE + LIVE)** — the agent runs as SYSTEM and previously reported the machine account (`HOST$`). The new
  `SessionUser` (WTS API: `WTSGetActiveConsoleSessionId` + active-session enumeration, `WTSQuerySessionInformation`)
  resolves the real logged-in user → `DOMAIN\user` in the incident, the log and the Toast. Fail-safe: with nobody
  logged in it falls back to the machine account (the incident is always recorded). **Verified live on PC-01:** agent
  commit `428a262` deployed, new incidents record `DOMENA\it-admin` (previously `PC-01$`).

### 5.2 Deployed components
- **API on SQL_SERVER (live `19e4018`):** `ReportNow` in the heartbeat, queue DI fix, `/api/version`. Deploy via
  **gMSA** (build staged on `APP_SERVER` `C:\Apps\USBGuardianApiPublish` → gMSA has local admin on SQL_SERVER). Caution on
  redeploy: **wait for `STOPPED`** (otherwise `USBGuardian.Api.exe` is locked → robocopy `FAILED` → the old version keeps running).
- **Agent on PC-01 (auto-installed):** whitelist poll 2 min, startup scan, `ReportNow`, serial trim,
  real version. Fixes: `onExpiredWhitelist`, publicKeyPath relative to exe, GUID, removed Sqlite.

### 5.3 Agent auto-enrollment — PILOT SUCCESSFUL (PC-01), expand to fleet
Goal: the console, running 24/7 after AD sync, deploys the agent itself onto stations without an agent. **Least-privilege:** the console writes the target list
(`deploy.targetsFile`), the installation is done by a **scheduled task on APP_SERVER under gMSA** (only that account has admin on the clients).
- **Works end-to-end:** gMSA `gmsa-deploy$` (in `Workstation-Admins` = admin on clients, no password), task `USBGuardian-AutoDeploy`,
  `Deploy-AgentFleet.ps1` (runspace pool PS5.1, `sc.exe \\HOST create` via cmd). **PC-01 installed without any creds**,
  the service runs, heartbeat + incidents flow. Scripts: `New-DeployGmsa.ps1`, `Install-Agent.ps1`/`Uninstall-Agent.ps1`,
  Detail: [docs/auto-deploy-setup.md](docs/auto-deploy-setup.md).
- **Complete client (DONE):** the package now also carries **ToastHelper** (user notifications) + scheduled tasks.
  Build: `scripts\Build-AgentPackage.ps1` → agent (root) + `ToastHelper\` (self-contained) + `tasks\`.
  `Deploy-AgentFleet.ps1` registers two **PS-free** tasks on the client: **watchdog** (`schtasks … sc start`, every 3 min)
  and **ToastHelper** (`schtasks /XML`, logon+unlock trigger, runs in the user session, least-privilege).
  **Applied and verified on PC-01** (ToastHelper.exe in `…\ToastHelper\`, both tasks Ready). Without ToastHelper the
  incidents are still recorded, but the user would not see the warning.
- **Expand to fleet:** GPO publisher trust on clients (5.4), enable in Settings (dry-run → live), `PC-01 → .180 → fleet`.

### 5.3b Whitelist signing/publishing workflow — DONE, AUTOMATIC (client = a 1:1 copy of the server)
Unlocks catalog delivery to agents (previously console changes never reached agents – version wasn't bumped +
`/api/whitelist/signature` was missing). **Fully automatic (`WhitelistPublisher`):** on **every catalog change**
(add/remove/activate/edit; also manual "Publish now") the console snapshots the active catalog → canonical
`whitelist.json` blob (version `yyyy-MM-dd-vN`, validity `whitelist.validityDays` default 365) → **signs it with the
internal RSA key on the server** (`Whitelist:PrivateKeyPath`) → stores `Json`+`Signature`, activates. API
`GET /api/whitelist` returns the blob **verbatim** + `GET /api/whitelist/signature` → the agent downloads (≤2 min),
verifies (fail-secure), stores as a JSON file. **Byte-exact** (same blob signed/served/verified, UTF-8 without BOM,
SHA-256/Pkcs1). Agent matches via **Dictionary O(1)** (scales to 10k). Signing uses the **internal** USB Guardian key
(public = `whitelist_public.pem` on agents), not a CA/AXIMA cert. **Trade-off (chosen):** the private key sits on `APP_SERVER`
(protect via ACL) in exchange for automation. DB: `database/07_whitelist_publish.sql` (`Json`+`Signature`→`NVARCHAR(MAX)`).
**Deployed:** console + API + DB migration. **Setup (user):** place the private key on `APP_SERVER` and set
`Whitelist:PrivateKeyPath` in `appsettings.local.json`. Server = DB (blob), client = JSON file.

### 5.4 Environment for PS scripts (IMPORTANT – AXIMA gotchas)
- **AllSigned (GPO):** every PS script that runs there **must be signed** with the prod cert `CN=powershell.domena.loc`
  (`-ExecutionPolicy Bypass` does NOT bypass this). Signing via the `APP_SERVER:4100` service / share `\\herkules\ITC\UTIL\04-manualy-instalace\PS-scripty`.
  Applies to `Deploy-AgentFleet.ps1` (on APP_SERVER) and `Watch-USBGuardian.ps1` (on clients).
- **Before signing CRLF + UTF-8 BOM** (the repo has LF → otherwise `HashMismatch`).
- **Trusted Publisher:** for non-interactive runs (gMSA/SYSTEM) the signing cert must be in `LocalMachine\TrustedPublisher`
  on APP_SERVER and clients (added on PC-01+APP_SERVER; **fleet via GPO** – cert export `_AXIMA-CodeSign-publisher.cer` on the share).

### 5.3c Enforcement server→agent + break-glass — DONE (Phase 2+3)
**Phase 2:** heartbeat returns `Enforce` (from `AppSettings policy.enforce`, APP_SERVER = truth); the agent (`WhitelistSync`)
passes it to `PolicyState`, `PolicyEnforcer` uses the effective mode (enforce → block, else warn; local default before
the first heartbeat). **Phase 3 (break-glass):** a local admin in the local console (`127.0.0.1:5080`,
`POST /api/override?hours=N`, cap 72 h) temporarily disables blocking for **offline** work. Persisted (`override.json`,
survives restart), **logged** as an incident (`Action=OverrideDisabled`, who/duration) → reported to APP_SERVER. **On the next
connection to the server the override is CLEARED** (`PolicyState.OnServerHeartbeat`). **Requires API redeploy
(APP_SERVER→SQL_SERVER) + agent redeploy.**

**Disable blocking = return EVERYTHING immediately (reliability fix, agent redeploy):** the local "Disable blocking"
(break-glass) calls `UnblockAll()` **synchronously → media are returned at once** (no waiting for the 2-min cycle;
a server-side `enforce=false` still propagates via heartbeat, which is fine). `UnblockDevice` hardened: Enable via an
**exact `-InstanceId`** (like a manual `Enable-PnpDevice`), with a `-like` fallback; it distinguishes
`ENABLED`/`GONE` (an unplugged device is dropped from the list so it doesn't linger)/`FAILED` (logged + kept for retry).
The local console now shows the **blocked count** + a **"Return all media now"** button (`POST /api/unblock-all`).

**Symmetry – re-enable blocking = re-block connected media (fix, agent redeploy):** the agent only blocks on a NEW
connect (WMI), so media returned via break-glass stayed connected and visible after blocking was turned back on
("BLOCKING, yet I can see the flash drive, Blocked now: 0"). `DeviceMonitor.ReEnforceConnectedDevices()` now scans
connected USB/SD media and **re-blocks** the unauthorized ones (idempotent – skips authorized and already-blocked).
Called **every reconcile cycle while blocking is ON** (self-healing) + **immediately** on "Re-enable blocking" in the
local console.

**Previously blocked device added to the whitelist → it gets returned (cache fix):** `ReconcileBlocked` returns the
device when `IsAllowedKey`=true even while blocking is on. **Bug:** `WhitelistChecker` cached the whitelist for 5 min and
a new-version download didn't invalidate that cache → approval took effect only after ~5 min (and `ReEnforce` could
re-block the device meanwhile). Fix: `WhitelistSync` calls `WhitelistChecker.Reload()` after download → the new version
applies immediately, unblock in the same reconcile cycle.

### 5.6 Health checks + scheduled service restart — DONE (2026-08-28)
**What it fixed:** on 2026-08-28 the "USB Guardian API" service on SQL_SERVER turned out to have been **stopped since
mid-July** (`sc query` → STOPPED, exit code 0 = left down after a deploy/server restart). The agent on `PC-01` kept
running and queued incidents locally (7 files, oldest 2026-07-02), but **nothing reached the server for six weeks**.
The console never said so out loud — the "Agents gone silent" tile showed `1` and nobody looked. After starting the
service manually the queue drained on its own.

**Health checks (new page `/kontroly`, `Health/HealthService.cs`):** 16 read-only checks in three groups —
*Data collection* (database, **API reachability**, **incident queue (spool)**, **age of the newest incident**, silent agents, station coverage),
*Whitelist and policy* (active version / signature / expiry, catalog vs. publication, signing key, enforcement),
*Operations* (email, retention, AD sync, auto-enrollment, scheduled restart, console/API/agent version match).
Every check reports **what it measured + why it matters + what to do**. Four deliberate states so "broken" is never
confused with "deliberately off": `ok` / `warning` / `ERROR` / `off` (+ `waiting for data`).
Machine-readable equivalent at **`GET /api/health`** — JSON, **HTTP 200 = fine, 503 = at least one error**
(a contract external monitoring can act on). Configured in Settings: `health.apiUrl`, `health.maxIncidentAgeHours` (default 48).

**Scheduled service restart (`Maintenance/ServiceRestartService.cs`, Settings):** once a day at the configured time it
walks a list of `HOST|Service name` targets (empty host = this server). A running service is restarted, **a stopped one
is started** — that is the safety net for the outage above. Settings: `svc.restart.enabled`, `svc.restart.at` (HH:mm),
`svc.restart.targets`; the outcome goes to `svc.restart.lastRun` (the health check reads it too). The **"Restart now"**
button does the same immediately = a check that permissions and service names are right. A missed window is caught up
for **at most 2 hours**, then it waits for the next day.

> **Permissions:** the restart runs under the **console service account** (`LocalSystem` = the `APP_SERVER` machine account).
> On a remote server it needs the right to control that service, otherwise the run returns `CHYBA – přístup odepřen`
> (visible in the health checks). For the API on SQL_SERVER, grant the console account control of that single service
> (`sc sdset`), or wait for the API move to `APP_SERVER` (5.5).

**Same safety net on the client (`agent/USBGuardian/SelfRestart.cs`):** the agent restarts its own service once a day
(`sc stop` → pause → `sc start` from a detached `cmd.exe`, since a service cannot restart itself from the inside).
Defaults come from `agent.config.json` (`selfRestart.enabled/at`), switchable from the **local console** (admin-only
card), state persisted in `C:\ProgramData\USBGuardian\selfrestart.json`.

**Console – runtime info moved up:** clock, deployed commit and the DB availability dot moved from the footer to the top bar.

### 5.7 Console look from the UI bank — switchable in Settings (2026-08-28)
The console no longer carries its own hand-written palette; the look comes from the **UI bank**
(repo `Anamax443/Interface-Par`, catalog `mockup/ui-styly-katalog.html`).

- **Copied into `wwwroot/`:** `bank/ui.css`, `bank/fonts.css`, `bank/tokens/style/*.css` (23 styles) and
  `vendor/fonts/*.woff2` (Cascadia Mono + Inter, latin and **latin-ext** — without it Czech diacritics fall back
  to another face mid-word). The bank is **never edited**; the catalog generates it (`node scripts/build-bank.mjs`).
- **`<head>` order** (binding): `fonts.css` → `tokens/style/<style>.css` → `ui.css` → `app.css`.
- **Skeleton** (`MainLayout.razor`): `.ui[data-style][data-layout]` + `p-title` / `p-nav` / `p-topnav` / `p-main` /
  `p-status`. Menu items live in one array rendered into both the side and the top nav → **no second source of
  truth** about navigation. The `.ui` wrapper gets its height from `app.css` (`100vh`).
- **Switching:** Settings → **Console look** (style + layout), stored in `AppSettings` (`ui.style`, `ui.layout`),
  read through `UiStyleCache` (singleton, reloaded on save). The value ends up in a **file path**, so it passes a
  **whitelist** of known styles/layouts — anything else silently falls back to `hmi-slate` / `side-nav`.
- **Default = `hmi-slate` + `side-nav`** (industrial panel: 2px borders, zero radius, uppercase headers).
- **`app.css` is now only this app's component layer** and reaches for the bank **roles only**
  (`--pane`, `--dim`, `--accent`, `--ok`, `--crit`, `--row-h`, `--radius` …). No hard-coded colours, no CSS
  framework beside the bank.
- **Verified:** build clean; console run locally against a non-existent DB (no touching of live data) — all seven
  bank files serve (200), skeleton and menu render, active item highlights. Blazor marks the active `NavLink` with
  `.active` and `aria-current="page"` while the bank expects `aria-current="true"`, so `app.css` supplies the
  active-item look.

> **Before adopting another style:** run **Check all styles** in the catalog, in the `side-nav` layout — findings
> depend on the skeleton, not only on the style.

### 5.8 Separated deploy accounts and API deployment (2026-09-03)
Until 2026-09-03 a **single** account (`gmsa-deploy$`) was admin both on the client fleet and on SQL_SERVER — one
compromised deploy identity would have reached both. Split into three roles, none holding the other's rights:

| Role | Account | Admin where |
|---|---|---|
| Clients (auto-enrollment) | `gmsa-deploy$` | `Workstation-Admins` → workstations only |
| Servers (API deploy) | `gmsa-srvdeploy$` | local admin on SQL_SERVER only |
| Console (the running app) | `APP_SERVER$` (LocalSystem) | **nowhere** |

`gmsa-srvdeploy$` is deliberately **not** in `Server Admins` — that would grant admin on every server. Membership is
local, on that one machine.

**API deployment** used to be manual PS blocks relying on the client account being admin on SQL_SERVER. It is now
`scripts/Deploy-Api.cmd` plus the `USBGuardian-ApiDeploy` scheduled task on `APP_SERVER` under the server gMSA: stop the
service, **wait for `STOPPED`** (otherwise `USBGuardian.Api.exe` stays locked, robocopy fails and the old version
keeps running while the deploy "succeeded"), copy without `appsettings.local.json`, start, verify `RUNNING`.
Log in `C:\ProgramData\USBGuardian\deploy\api-deploy.log`; the exit code shows as the task's Last Result.

**A batch file, not PowerShell:** `.cmd` is not subject to the `AllSigned` GPO, so the deployment step needs no
re-signing on every change.

> **Consequence for the scheduled service restart:** the console (LocalSystem) is not admin on SQL_SERVER and must not
> be. Restarting `USB Guardian API` from there will fail — either grant it control of **that single service** via
> `sc sdset` (one ACE, not an account holding the keys to the server), or let the server gMSA do the restart the
> same way it does the deploy.

### 5.10 Activity log — deployment and what does not add up (2026-09-04)
`dbo.ActivityLog` + `sp_PurgeActivityLog` are in the DB, grants issued (see Live State), console and API both run
`5431dce`, and the log is filling up: at 8:16 the **Activity** page showed heartbeats from four stations
(`tep OK (whitelist 2026-06-19-v7, agent b0e1a0d)`). Communication rows come from the API, operator actions
(deploy, update, excluding a station) from the console — both into the same table.

**The `USBGuardian-ApiDeploy` task on `APP_SERVER` was missing** and was created only on 2026-09-04 (UTF-16 XML,
`LogonType=Password`, principal `gmsa-srvdeploy$` given as a SID). First run: Last Result `0`, robocopy rc 3, service
came up, `/api/version` reports `5431dce`. The API deploy channel therefore exists only as of now — the earlier
text in 5.8 described the intent, not the state.

**Nothing calls the purge.** `sp_PurgeActivityLog` exists in the DB but there is not a single reference to it in
the code — Settings only carries `retention.incidentDays`. With 213 stations and a 2-minute heartbeat that is
roughly **150k rows per day**; until retention is wired up the table grows unbounded.

**Local console inconsistency on the fleet.** Commit `3c8ba3f` states the package and archive have
`localConsole.enabled=false`, but on `APP_SERVER` the **deploy source and all three archived versions
(`f2bb194`, `560722b`, `b0e1a0d`) say `true`** — the last package build put it back. The next `AutoDeploy` /
`UpdateAgent` run will therefore re-enable the local console on workstations. The comment in
`Build-AgentPackage.ps1` says the opposite of the commit (the console **should** be on — it is the break-glass for
someone in the field). **This is a decision to make, not a typo** — and since fix `b0e1a0d` the argument against it
is weaker: a rejected login now explains what the person is looking at instead of a bare 403.

**Guard from `3c8ba3f` fixed:** `Build-AgentPackage.ps1` held a real BEL byte instead of `\a` in the config path
(`Config␇gent.config.local.json`), so `Test-Path` was always `false` and the content check never ran — the script
only ever reported "package has no config". After the fix the check passes on a real package.

### 5.11 Local console: port stuck after restart → wrong admin detection (2026-09-04)
A colleague on CERNYSW11 reported the local console (`127.0.0.1:5080`) loading forever (white page, spinner).
Investigation uncovered a chain of independent problems, fixed one by one:

1. **The agent wrote nothing to the Event Log at all** — `ClearProviders()` in `Program.cs` also removed the
   `EventLog` provider, so the SYSTEM-run service left no trace of itself anywhere. Added `AddEventLog`
   (`logging.eventLogLevel`, default `Warning` so the per-minute sync doesn't flood it) + local-console
   messages always at Information.
2. **The install left the old process running.** `Install-Agent.ps1` waited for `Stopped` inside a
   `try/catch`, so the timeout was silently swallowed and the copy proceeded with the old process still
   alive — the new instance then couldn't bind the port and the local console stayed dead until the next
   restart. Fix: a real stop-status check, a hard process kill as a last resort, a `RUNNING` check after
   start, and registering the Event Log source.
3. **The local console gave up on the first busy-port attempt** — added a retry (6× at 5 s) so it survives
   the brief window where a dying old process still holds the port.
4. **`Archive-AgentVersion.cmd` / `Set-AgentVersion.cmd` wrote their log into `C:\ProgramData\USBGuardian\deploy`**
   — a folder ACL-locked to `SYSTEM`/`Administrators` (owned by the running agent). `mkdir` silently failed,
   the redirect into the missing path spilled an error onto the screen, yet the script still printed "DONE"
   even though nothing was actually copied. Log moved next to the archive (`%ARCH%\_logs`), added a hard
   post-copy check.
5. **Rolling out a beta build was manual only (RDP + `schtasks /Run`).** Added a "⇪ Roll out beta to the
   sample" button in Settings (`DeployTrigger.SpustBetuAsync`) plus a `BetaRolloutService` safety net —
   watches the beta channel's `VERSION.txt` and, if the operator doesn't get to it, rolls it out itself
   after an interval (default 30 min, configurable).
6. **An operational trap (not a bug):** running `schtasks /Run` before `Set-AgentVersion.cmd` finishes
   writing `VERSION.txt` rolls out the PREVIOUS commit — silently, no error. Found by comparing the task's
   `Last Run Time` against the `VERSION.txt` timestamp in the log. Correct order: archive → set the channel
   → **only then** trigger the rollout.
7. **The real finding for cernys:** even after a confirmed restart (his interactive token correctly carried
   `Administrators` as `Enabled`), the console still refused him. New diagnostics on the refusal page (a raw
   dump of the token's groups) showed the **network NTLM token over loopback didn't carry `Administrators`
   at all** — not just deny-only (which the earlier `b0e1a0d` fix already covered), the group was completely
   absent. `IsLocalAdmin` now has a third, reliable step: a direct query against the local SAM
   (`System.DirectoryServices.AccountManagement`, `GroupPrincipal.GetMembers(recursive: true)`), independent
   of whatever a particular logon type's token happens to carry.

**Verified live:** CERNYSW11 on `924b9b8` now shows cernys the full admin console (WHITELIST, WMI MONITORING,
ENFORCEMENT, SERVICE, PLANNED RESTART) — previously he saw only an endless load, then (after the earlier
`cb8ef1d` fix) at best the simplified user page; now correct full access matching his real group membership.

### 5.12 External security audit + remediation (2026-09-04)
An independent static audit of the public repo (architecture 9.0/10, security implementation 7.3/10,
testing 4.5/10, overall 7.9/10) found several concrete bugs – verified directly in the code, not taken
on faith. Fixed today, each item its own commit (for isolation if something breaks):

- **`56b4235`** – an expired whitelist silently authorized devices: `WhitelistChecker.IsAllowed()`
  only logged a warning and kept going with the lookup; `PolicyEnforcer.HandleDevice()` returned on
  `if (isAllowed) return` before `DetermineAction()` ever looked at `whitelistStatus` – `onExpired`
  therefore never applied to a device that stayed in the (stale) list. The same gap existed in
  `DeviceMonitor.ReEnforceConnectedDevices()` for already-connected media. **Git-only so far, not
  rolled out to the fleet** (agreed with the user – code review instead of a live test, since verifying
  it needs either a genuinely expired production whitelist or an isolated test one).
- **`033af8a`** – `WhitelistController` had one authorization policy (`USBGuardianClients`) on the
  whole controller, including `POST /api/whitelist/devices` – a station account could in theory write
  to the whitelist, not just read it. Authorization moved to individual actions, a new fail-closed
  policy `USBGuardianAdmins` added for the write path. **Deployed, needs `Authorization:AdminGroups`
  in `appsettings.local.json` on `SQL_SERVER`.**
- **`57a0e15` + `a4f28bd`** – the API unconditionally listened on both HTTP 5050 and HTTPS 5443. The
  port now only opens in `Development`. **The real `appsettings.local.json` on `SQL_SERVER` had its own
  `Kestrel:Endpoints:Http:Url`** – Kestrel reads that section independently of the code and the two
  sources add up rather than one replacing the other. Trying to null the value out of `IConfiguration`
  at runtime **didn't work** (verified with an isolated test outside the repo) – Kestrel still finds the
  "Http" endpoint and crashes on the missing `Url`, which in production would mean the API never starts
  at all. Fix: fail fast with a clear message if production ever sees that section exist, plus manually
  removing the section from the real config on `SQL_SERVER`. **Deployed and verified**
  (`/api/version` over HTTPS OK, HTTP 5050 times out).
- **`297ac7a`** – `AllowedGroups.Length == 0 → true` (fail-open, the comment admitted as much). Now
  fails fast at startup if empty. **Verified before deploying** that the production `AllowedGroups` is
  populated (`AXINETWORK\USB-Guardian-Clients`, `AXINETWORK\SQL Admins2`) – so fail-closed broke nothing.
  **Deployed and verified** (`/api/version` OK after the redeploy).
- **`ac73571`** – the first C# test project (`tests/USBGuardian.Agent.Tests`, xUnit), 8 tests, a
  regression test for the whitelist-expiry fix. Before this the repo had exactly one JS test (UI).
- **`8fbfa6d`** – `POST /api/incidents` returned `202 Accepted` as soon as the batch was queued in the
  in-memory `Channel` – a process crash between that acknowledgement and the DB write lost the batch
  for good (the agent advances its offset on 2xx and never resends). A new `IncidentSpool` writes the
  batch to disk atomically (`C:\ProgramData\USBGuardian\incident-spool`, overridable via
  `incidents:spoolPath`) **before** the 202; the worker deletes the file only after a successful DB
  write. Anything left on disk (a crash, or just a routine service restart) is replayed first on the
  next start – the existing dedup in `ProcessBatch` makes a repeated replay harmless. New test project
  `tests/USBGuardian.Api.Tests` (the API had no tests before), 4 tests. **Deployed and verified
  2026-09-04 15:08** (`/api/version` → `a6bcfaf`, `/api/incidents/queue/status` returns an empty
  state on `SQL_SERVER`).
- **`2766344`** – the spool only lived on the API server's disk (`SQL_SERVER`); the console runs on a
  different box (`APP_SERVER`) and has no access to that directory, so a stuck batch would never show up
  in the health checks. A new anonymous endpoint `GET /api/incidents/queue/status` (same pattern as the
  already-public `/api/version`) returns the pending count plus the age of the oldest one; a new check
  "Incident queue (spool)" in the *Data collection* group reads it the same way the other API checks do.
  3 new tests for `IncidentSpool.GetStatus()`. **Deployed and verified 2026-09-04 15:21** – both API
  and console (`/api/health` on `APP_SERVER` returns 16 checks, the new one reports `ok`/`empty`).
- **`e8d702c`** – the dedup key `timestamp-to-the-second|serial|vendor` was missing `ProductId`/
  `PnpDeviceId`: two different devices from the same vendor sharing a (often generic, shared across
  a batch of cheap USB sticks) serial number, connected in the same second, would collide – the
  second incident would get silently dropped as a duplicate of the first instead of being written.
  `MakeKey` now takes both fields too; a genuine resend (retry after an outage) and a later
  `DisconnectedAt` update still pair up correctly (byte-identical record, `PnpDeviceId` doesn't
  change). Exposed to tests via `InternalsVisibleTo`, 3 new tests. **Git-only so far, not deployed
  to the fleet** (needs an API redeploy).
- **`0bc6709`** – found while auditing every API endpoint and its authorization today (outside the
  original audit list): unlike the console, the API's security relied entirely on individual
  `[Authorize]` attributes with no default policy – a new controller/action with no attribute would
  be silently public under ASP.NET Core's default behavior (exactly the kind of mistake the audit
  already caught once, `033af8a`). A new `FallbackPolicy = USBGuardianClients` makes the API
  fail-closed like the console: everything is protected by default, public only where explicitly
  marked `[AllowAnonymous]`/`.AllowAnonymous()` (`/api/version`, `/api/cert-info`,
  `IncidentsController.QueueStatus` – those still take precedence over the fallback, so they stay
  public unchanged). No existing endpoint had a gap today – this is a safety net for the future.
  **Git-only so far, not deployed to the fleet** (needs an API redeploy).

**Still open from the audit** (priority for next time): ACL on the TLS PFX and the RSA signing key,
verifying the payload's hostname against the authenticated Windows identity, more tests, CI
(`.github/workflows` is entirely absent from the repo).

### 5.5 Roadmap (pending)
- **Monitoring of signing cert expiry** – `CN=powershell.domena.loc` valid until 2028-06-17; alert via e-mail from the console.
- **"Everything on the server APP_SERVER":** move the API runtime from SQL_SERVER to APP_SERVER (console+API on APP_SERVER, DB on SQL_SERVER, agent repoint to
  `https://APP_SERVER_IP:5443`) → PC-01 really not needed. **Build/deploy artifacts are on D:\deploy (locally), not on PC-01.**
- ~~Close HTTP 5050~~ **DONE (2026-09-04)** – only listens in `Development` (`Program.cs`, `builder.Environment.IsDevelopment()`), production is HTTPS `:5443` only.
- **Activity log retention** – nothing calls `sp_PurgeActivityLog`; add `activity.retentionDays` to Settings and the call to the API.
- **`Microsoft.AspNetCore.Authentication.Negotiate` 8.0.0** – the build reports NU1903 (known high-severity advisory); bump to current 8.0.x.
- **Per-serial blocklist** + **blocking already-connected media** (the startup scan is half the way there).
- **Hardening:** dedicated `USB-Guardian-Admins` instead of `IT-Admins`, HTTPS console.
- **Cleanup:** stray (untracked) `server/USBGuardianAPI/` (to be deleted).

> **Note on automation (NOT bypassable by me):** the security classifier auto-denies me actions on prod
> SQL_SERVER as well as **changes to my own permissions** (update-config) → prod deploys and permission rules must be run/allowed by the
> user (bypass mode or a manual rule). That's why the API deploy on SQL_SERVER is done by the user with ready-made PS blocks (I prepare
> the build on `APP_SERVER`).

## 6. Documentation map

| File | Content |
|--------|-------|
| `README.md` / `.en.md` | Functional overview, components, configuration, deployment |
| `HANDOFF.md` / `.en.md` | This document – handoff + live state |
| `docs/architecture.md` / `.en.md` | Technical architecture, data flow, security layers, activity log |
| `docs/auto-deploy-setup.md` / `.en.md` | Setup of the deploy gMSAs (client and server) + GPO + tasks |
| `docs/how-it-works.html` | Animation of the information flow (15 steps), CS/EN toggle |
| `docs/mind-map.html` | Mind map of the system, CS/EN |
| `docs/flowchart.html` | Flowchart of one medium's path (decision points), CS/EN |
| `docs/management-summary.html` | **Management summary — one A4 portrait page**, print-ready, CS/EN |
| `docs/oponentura.md` / `.en.md` | The full technical review document (context, NIS2, defence of decisions, security, limitations) — **ch. 34 = the 2026-09-04 addendum** |
| `docs/oponentura-komercni.md` / `.en.md` | The commercial review (business/product readiness) + the author's response |
