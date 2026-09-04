# USB Guardian

*🇬🇧 English · [🇨🇿 Čeština](README.md)*

Security tool for monitoring storage media (USB flash, SD cards, USB disks) on company
computers. Every device must be approved by IT and recorded in a central whitelist.
Unapproved media are warned or blocked. Designed as a technical control for
**NIS2 / Act 181/2014 Coll. / ISO 27001**.

> **Configuration:** no company-specific values (server, domain, groups, accounts) live in the
> code — everything is in `*.local.json` (gitignored).

## Project status

| Phase | Description | Status |
|------|-------|------|
| 1–7 | Agent (WMI/warn/block), API+SQL+gMSA+Kerberos, RSA-4096 whitelist signature, incident queue, log tagging | ✅ |
| 8 | **Agent local admin console** (HttpListener, loopback, read-only) | ✅ |
| 9 | **Server admin console** (Blazor on APP_SERVER) per **AXIMA UI standard** (dark/light, footer, /api/version) | ✅ |
| 10 | **AD sync** – station inventory from AD + reconciliation (who lacks the agent) + AD path; communication icon | ✅ |
| 11 | **Overview** – cross-page tile summary, filter (period/action/search), aggregation, "Approved" column | ✅ |
| 12 | **Whitelist** – entry by serial number only + autofill from incidents + import + inline field edit + active checkbox | ✅ |
| 13 | **Central settings (DB)** – enforcement, console access whitelist, e-mail + incident alerts | ✅ |
| 14 | **Encrypted agent↔API comms** – self-signed cert (no CA, MachineKeySet) + thumbprint pinning | ✅ |
| 15 | **Communication oversight** – "Silent agents" tile + configurable threshold; **"Request data" on click**; sortable Detailed table | ✅ |
| 16 | **Startup scan** of already-connected media; whitelist poll 2 min; central `onExpired`/`enforce` | ✅ |
| 17 | **Agent auto-enrollment** – the console deploys the agent itself onto stations without it (gMSA + scheduled task, dry-run/opt-in); **PILOT SUCCESSFUL on PC-01 (no creds, via gMSA)** | ✅ pilot OK |
| 18 | **DB/incidents flowing** (agent→API→DB→console) | ✅ |
| 19 | **Version/commit on all components** (`/api/version`, agent reports commit; **reliable stamp** = footer/`/api/version` = git HEAD) | ✅ |
| 20 | **User attribution** – the real logged-in user via the **WTS API** (agent=SYSTEM → not the machine account); verified live | ✅ |
| 21 | **Complete client** – ToastHelper (notifications, logon+unlock) + **PS-free watchdog**, all in `Build-AgentPackage`; verified on PC-01 | ✅ |
| 22 | **Media capacity** in Overview and Whitelist; **CSV export** + **manager report** with charts (inline SVG, 1–2 A4) | ✅ |
| 23 | **Data retention** – Settings (console) + `RetentionService` in the API (purges old incidents); **Database page** (DB content overview) | ✅ |
| 24 | **Deploy targeting** – default for new PCs (Settings) + per-station and bulk include/exclude in Stations | ✅ |
| 25 | **Agent local console** also shows the list of approved devices (whitelist) + agent version | ✅ |
| 26 | **HTML animation** of how the system works (`/how-it-works.html`, 16 steps: data flow + enforcement + spool) | ✅ |
| 27 | **Whitelist signing/publishing workflow (automatic)** – catalog change → console publishes and **signs internally** (server-side RSA, key on APP_SERVER) → API serves the signed blob verbatim → **client = a 1:1 copy of the server** within ~2 min; agent O(1) match (scales to 10k) | ✅ |
| 28 | **Enforcement server→agent (Phase 2)** – heartbeat carries `policy.enforce` (APP_SERVER = truth) → agent really **blocks/warns** per the server | ✅ |
| 29 | **Local break-glass (Phase 3)** – station admin temporarily disables blocking offline (local console), persisted, **logged** → server; cleared on reconnect | ✅ |
| 30 | **Auto-re-enable + reconciliation** – on blocking off / break-glass the agent restores previously blocked media; a now-approved medium is restored even while blocking is on | ✅ |
| 31 | **Client service restart** (local console, agent self-restart) + **settings reload** (server console, AccessCache) | ✅ |
| 32 | **Reliable enforcement (symmetry)** – disable blocking = return **everything at once** (exact `Enable-PnpDevice`, unplugged-media cleanup); re-enable = re-block **already-connected** unauthorized media; a newly approved medium applies **immediately after download** (whitelist cache invalidation); ✕ delete from catalog (DELETE grant); console error message unwrapped | ✅ |
| 33 | **Health checks** – a checklist of checks (server and client) ticked off with running results and **CSV / HTML / PDF / TXT export**; **scheduled restart** of services (server and agent) | ✅ |
| 34 | **Bank UI look** – switchable in Settings, dark/light without FOUC, survives navigation between pages | ✅ |
| 35 | **Separate deploy accounts** – `gmsa-deploy$` (stations only) × `gmsa-srvdeploy$` (API server only) × console (admin nowhere); one identity no longer holds both the fleet and the server | ✅ |
| 36 | **Updating a deployed agent** – `Update-Agent.cmd` (stop → wait for `STOPPED` → copy → verify `RUNNING`), **offline installer** in the package, **stable/beta channels**, **version archive** + rollback | ✅ |
| 37 | **Activity log** – `ActivityLog`: heartbeats and the server's answers, incident batches received, whitelist publications, manual operator actions; API and console write into the same table, page with filters, live mode and CSV export | ✅ |
| 38 | **Local console: local admin login** – a loopback token is a *network* token and for a local account Windows strips Administrators from it (`LocalAccountTokenFilterPolicy`) → the check now accepts a filtered token as well, and a refusal shows **who** the person was seen as | ✅ |
| 39 | **Closed unencrypted HTTP 5050** – only listens in `Development`, production (Windows service) is HTTPS `:5443` only | ✅ |
| 40 | **External security audit + remediation** (2026-09-04) – 6 findings, 5 fixed and deployed, 1 deliberately in observation mode only (see Security) | ✅ |
| 41 | **Durable incident queue** – `IncidentSpool` writes the batch to disk BEFORE the API acknowledges it, survives a process crash, replayed on restart; the "Incident queue (spool)" check on `/kontroly` | ✅ |
| 42 | **First tests and CI** – 24 C# tests (0 before 2026-09-04), `.github/workflows/build-and-test.yml` on every push/PR | ✅ |
| – | Per-serial **blocklist** + blocking of an already-connected device | 🔜 |
| – | Signing certificate expiry monitoring | 🔜 |
| – | **Activity-log retention** – `sp_PurgeActivityLog` exists but nothing calls it | 🔜 |
| – | ACLs on the server's TLS/RSA keys (last open item from the audit) | 🔜 |
| – | Tighten hostname verification (warn-only today) to a hard rejection | 🔜 |

## Architecture

Three components, push model (agent → API), two-tier server (logic on the app server, DB = storage):

```
[Client station]                     [App server APP_SERVER]            [DB server SQL_SERVER]
┌────────────────────┐               ┌─────────────────────┐      ┌───────────────────┐
│ Agent (.NET8 svc)  │               │ Admin console       │      │ SQL Server        │
│  WMI detection     │  push  HTTPS  │ (Blazor :4200)      │ read/│ DB USBGuardian    │
│  whitelist check   ├──────────────►│  Overview/Stations  │ write│  Incidents        │
│  warn / block      │   ┌───────────┤  Activity (log)     ├─────►│  Computers        │
│  local console     │   │  push     │  AD sync ◄── AD     │      │  WhitelistDevices │
│  (loopback :5080)  │   │           │  Settings / Docs    │      │  WhitelistVersions│
└─────────▲──────────┘   │           └─────────────────────┘      │  AppSettings      │
          │              │           ┌─────────────────────┐      │  ActivityLog      │
   install / update      └──────────►│ API (:5443, HTTPS only) ├─read/─└───────────────────┘
   (tasks under gMSA)                │  incident ingestion │ write            ▲
          │                          │  heartbeat + policy │                  │
          └──────────────────────────┤  whitelist delivery │──────────────────┘
                                     │  activity logging   │
                                     └─────────────────────┘
```

The activity log (`ActivityLog`) is written by **both the API and the console into the same table** —
agent traffic from one side, operator actions from the other, so the operation reads as one story.

Details: [docs/architecture.en.md](docs/architecture.en.md). Handoff & live state: [HANDOFF.en.md](HANDOFF.en.md).
Visually: [data-flow animation](docs/how-it-works.html) · [mind map](docs/mind-map.html) ·
[flowchart](docs/flowchart.html) · [management summary (A4)](docs/management-summary.html).

## Components

| Component | Technology | Where it runs |
|-----------|-------------|----------|
| **Agent** | C# .NET 8, Windows Service | every station (SYSTEM) |
| **API** | ASP.NET Core, :5443 (HTTPS only) | `SQL_SERVER`, installed at `C:\USBGuardian.Api`, Windows service "USB Guardian API" |
| **Admin console** | Blazor Server, :4200 | `APP_SERVER_IP` (`APP_SERVER`, Windows service `USBGuardianConsole`) |
| **Database** | SQL Server | `SQL_SERVER`, DB `USBGuardian` |
| **Authentication** | Windows Auth (Kerberos / Negotiate) | API: AD group; console: AD group + account whitelist |

## Server admin console (Blazor)

Runs on the app server (`APP_SERVER_IP`), reads/writes SQL_SERVER, **AXIMA UI standard** (archetype A – IT-ops:
dark/light toggle `axima.theme` without FOUC, print = light, status traffic-lights). Pages:

- **Overview** – cross-page tile summary (Stations in AD / Missing agent / Approved media /
  Deactivated / Incidents / Blocked / Warned, click-through to lists). **Filter** (period
  30/90/year/all, action, full-text) + **aggregation** (group by media+station+user with count) +
  device identifiers **VID/PID/serial** + **media capacity** + **"Approved"** column (currently per whitelist).
  The **"Detailed" table has sortable headers**. **Export:** `⬇ CSV` (Excel) and `📊 Report` =
  **manager summary** (KPIs + charts: incident trend, action donut, top users/stations + Database section;
  inline SVG, printable on **1–2 A4**) – both inherit the active filter.
- **Stations** – inventory from AD; tiles filter (all / reporting / **silent agents** / missing agent),
  **search**, **AD path** (OU) next to hostname, **communication icon** (green ≤ threshold / amber silent /
  grey no contact; threshold `comm.silentAfterMinutes` in Settings), **Refresh from AD** button
  and **"Request data"** (per row / bulk). A **"Deployment"** column + bulk **"Exclude / Include all"** =
  per-station auto-enrollment control (an exception to the default `deploy.defaultEnroll`).
- **Whitelist** – approved media; **enter just the serial number** (VID/PID/name autofill from
  incidents, retroactively too), **capacity** (from incidents), **bulk import**, **inline field edit**,
  **Active checkbox** (temporary deactivation without deletion).
- **Settings** (central, in DB) – **enforcement** (require only approved media), **communication
  oversight** ("silent agent" threshold), **console access whitelist** (users/groups; appsettings
  = lockout-safe bootstrap), **e-mail** (SMTP relay/Direct Send + test) and **incident alerts**
  (interval), **agent auto-enrollment** (master switch + dry-run + **default for new PCs** + targets),
  **data retention** (how many days to keep incidents), AD sync / DB / build info.
- **Health checks** – checks of the server and the clients: **the list of checks up front** and ticking them
  off with running results (so it is visible that it works), **scheduled restart** of services,
  **export** to CSV / HTML / PDF (print) / TXT.
- **Activity** – the **operations log**: heartbeats (including what the server answered), incident batches
  received, whitelist publications, manual deployments and station exclusions. Filters (period, level,
  source, search), a **live** mode (3 s refresh) and CSV export. The source list comes from the data,
  not from a fixed list.
- **Database** – read-only overview of the DB content (table row counts, incident date range for checking
  retention, `AppSettings` dump, the last 20 incidents).
- **Documentation** – hub + **printable HTML** pages (render `.md` via Markdig) + graphical outputs:
  the **animation** "How it works", a **mind map**, a **flowchart** and a **management summary (A4)** —
  all four bilingual (CS/EN toggle).

Footer (service line per standard): **live clock + clickable commit hash + DB health + © Milan Trnka**.
Contract **`GET /api/version`**.

### Versioning (commit on all components)

Every component reports its git commit so the operator can verify exactly what is running:

- **Console** – commit in the **footer** + endpoint `:4200/api/version`.
- **API** – endpoint `:5443/api/version`.
- **Agent** – reports the commit (heartbeat) → the console shows it as **"Agent version"**.

The commit is stamped at build time via MSBuild (`git rev-parse`) – **reliably** (the generated `GitCommit.g.cs`,
rewritten only when the commit changes, forces a recompile), so the footer/`/api/version` exactly matches the deployed git
(= a currency check for the solution).

**Authorization:** Windows Auth; access only for members of `Authorization:AdminGroups` / accounts
`Authorization:AllowedUsers` (appsettings) **or** the DB list from Settings. For silent SSO use the hostname, not the IP.

### AD sync

A background service (also on demand via a button) reads computers from Active Directory and writes
them into `Computers`. Keyed by **hostname** (not IP – stations have dynamic addresses). Domain taken
automatically from the server (`new DirectoryEntry()`, nothing hardcoded). Reconciliation:
*in AD ⨯ reporting an agent* → list of stations missing the agent.

## Agent local console (two views by role)

One port, two views — **the role decides what a person sees**:

| | An ordinary account (user page) | A local admin (full console) |
|---|---|---|
| Protection state (blocking / warning only / temporarily off) | ✅ | ✅ |
| Attached media + whether they are approved | ✅ | ✅ |
| The identification of an unapproved medium + "copy for IT" | ✅ | ✅ |
| The list of approved media (whitelist) | ❌ | ✅ |
| Incident queue, WMI watchdog, diagnostics | ❌ | ✅ |
| Break-glass, return media, restart the service | ❌ | ✅ |

The **user page** (`GET /`, data from `GET /api/muj-stav`) answers the question people call IT about:
*"why doesn't my flash drive work."* It shows whether media are being checked, which of the attached ones is
unapproved and **what identifies it** (`VID:PID:serial`) — with a button that copies it into a message for IT.
The whitelist is deliberately **not** shown there: knowing approved serial numbers weakens the protection.
An admin can open the same view at `/uzivatel`, to see exactly what the user is looking at during a call.
Render check: `node tests/user-page.test.mjs`.

The **full console** is unchanged (`localConsole.enabled` in `agent.config.local.json`, off in the template).
`HttpListener` on `127.0.0.1:5080`, **local admins only** – live agent state: **the list of approved devices (whitelist)**,
whitelist status+version, **agent version (commit)**, WMI watchdog, queue, connected media and recent events.
Besides reading it offers three actions: **break-glass** (switch blocking off temporarily while offline),
**return all media now** and **restart the service**. Uses `HttpListener` (not Kestrel) so the agent needs no
ASP.NET Core runtime. No password needed (loopback + Windows auth + Administrators membership).

> **Local admin login (gotcha):** a request to `127.0.0.1` is a **network** logon as far as Windows is
> concerned, and for a local account `LocalAccountTokenFilterPolicy` strips the Administrators group from
> that token (it stays deny-only) → `IsInRole` says NO even though the person *is* an admin. The check
> therefore accepts a **filtered token** as well: membership serves as **authorization**, not as the source
> of rights — the action itself is performed by the service running as SYSTEM. A refusal is not a bare 403
> but a page showing **who** the person was seen as and what is required.

## Encrypted agent ↔ API comms (self-contained TLS)

NIS2 requires encrypted transport. Solved **without any CA / external cert dependency**:

- **API** generates/persists its **own self-signed cert** at startup (`SelfCert.cs`,
  `C:\ProgramData\USBGuardian\api-tls.pfx`), Kestrel binds it on `:5443`. The key is
  **`MachineKeySet`** (works under gMSA; usable by Schannel). It logs the thumbprint (PIN) and returns
  `GET /api/cert-info`.
- **Agent** does not pin via a CA but via the **thumbprint** (`tls.pinnedThumbprint` in config,
  `TlsClient.cs`) → encrypted **and** authenticated, no CA. Without a pin you can use
  `validateServerCertificate=false` (dev only) or CA validation.

Agent prod config: `whitelist.syncUrl = https://SERVER:5443` + `tls.pinnedThumbprint = <thumbprint from /api/cert-info>`.

## Agent distribution and auto-enrollment

Stations without an agent are visible under **Stations** (the "Missing agent" tile). Deployment:

- **Client package:** `scripts\Build-AgentPackage.ps1` assembles the complete client = self-contained agent +
  `ToastHelper\` (notifications in the user session) + `tasks\` (scheduled task definitions). The client needs no .NET runtime.
- **Local install:** `scripts\Install-Agent.ps1 -SourcePath <package>` (creates the "USB Guardian" service
  + recovery, deploys the per-machine `agent.config.local.json`), `scripts\Uninstall-Agent.ps1`.
- **Bulk:** `scripts\Deploy-AgentFleet.ps1 -TargetsFile … -SourcePath …` – parallel rollout via
  `\\HOST\C$` + `sc.exe \\HOST create`; registers **PS-free** scheduled tasks (watchdog every 3 min `sc start`
  + **ToastHelper** logon/unlock via `schtasks /XML`); skips offline/already-installed; audit CSV. (PS 5.1 and 7.)
- **Auto-enrollment (the console deploys on its own):** `AgentDeployService`, after AD sync, finds stations without an agent,
  applies the **default `deploy.defaultEnroll` + exceptions** (`includeHosts`/`excludeHosts` managed in Stations) and
  (in live mode) writes the targets into `deploy.targetsFile`; the install is performed by a **scheduled task on APP_SERVER under a
  dedicated gMSA** (least-privilege). **Default OFF + dry-run.** Account setup: [docs/auto-deploy-setup.en.md](docs/auto-deploy-setup.en.md).
- **Updating a deployed agent:** `scripts\Update-Agent.cmd <SOURCE> <HOST|FILE> [SERVICE]` – stops the service,
  **waits for `STOPPED`**, copies, and **verifies `RUNNING`**. Without that the running `.exe` is locked, only part
  of the files is overwritten and the station is left with a **mix of versions** while the deploy reports success.
  A station without the service is skipped.
- **API deployment:** `scripts\Deploy-Api.cmd <SOURCE> <HOST> <TARGET> [SERVICE]` – the same pattern (stop → wait →
  copy → verify), run as a task under the **server** gMSA. The client deploy account never touches the server.
- **Channels and rollback:** the package is archived per version (`stable` / `beta`), so a previous version can be
  deployed. The package also carries an **offline installer** (`Install-Agent.cmd` / `Uninstall-Agent.cmd`) for a
  station the deploy channel cannot reach — including cleaning up after itself.

> **Batch files (.cmd), not PowerShell:** the deployment steps are `.cmd` because they are not subject to
> `AllSigned` from GPO — changing a deploy step therefore does not require re-signing.

**Separate deploy identities (since 09/2026):** one account must not hold both the fleet and the server.

| Role | Account | Where it is an admin |
|---|---|---|
| Clients (auto-enrollment, update) | `gmsa-deploy$` | stations only |
| Server (API deployment) | `gmsa-srvdeploy$` | the API server only |
| Console (the running app) | app server machine account | **nowhere** |

> **AXIMA environment:** PS scripts running on machines **must be signed** (execution policy AllSigned via GPO)
> with the prod cert `CN=powershell.domena.loc`, and the publisher must be in `LocalMachine\TrustedPublisher`
> (on APP_SERVER and the clients, fleet via GPO). Before signing **CRLF + UTF-8 BOM** (otherwise HashMismatch).

## Configuration

Company-specific values live **only** in `*.local.json` (gitignored). Central operational settings
(enforcement, access, e-mail) live in the **DB** (`AppSettings`), managed from Settings. The `*.example` /
placeholder templates are in the repo.

| Component | Template (in repo) | Real (gitignored) |
|-----------|------------------|---------------------|
| Agent | `agent/USBGuardian/Config/agent.config.json` | `agent.config.local.json` |
| API | `server/USBGuardian.Api/appsettings.json` | `appsettings.local.json` |
| Console | `server/USBGuardian.Admin/appsettings.local.json.example` | `appsettings.local.json` |

### Placeholders in the documentation

The documentation describes the real pilot deployment, but server, workstation and account
names are replaced with placeholders — substitute your own values:

| Placeholder | Meaning |
|---|---|
| `APP_SERVER` / `APP_SERVER_IP` | application server — hosts the admin console and the deploy tasks |
| `SQL_SERVER` / `SQL_SERVER_IP` | SQL Server with the `USBGuardian` database and the API service |
| `DOMENA`, `domena.loc` | Active Directory domain |
| `PC-01` … `PC-04` | workstations running the agent |
| `gmsa-api`, `gmsa-deploy`, `gmsa-srvdeploy` | gMSA accounts (API, client deploy, API deploy) |
| `IT-Admins`, `Workstation-Admins` | AD groups (console access, local admin on workstations) |
| `API_CERT_THUMBPRINT` | thumbprint of the self-signed API certificate (pinning) |

### Console – `appsettings.local.json`

```json
{
  "ConnectionStrings": { "DefaultConnection": "Server=tcp:SQL-SERVER,1433;Database=USBGuardian;Integrated Security=true;TrustServerCertificate=true;" },
  "Authorization": {
    "AdminGroups": [ "DOMENA\\USB-Guardian-Admins" ],
    "AllowedUsers": [ "DOMENA\\jmeno.admina" ],
    "DevAllowAll": false
  },
  "Kestrel": { "Endpoints": { "Http": { "Url": "http://0.0.0.0:4200" } } },
  "AdSync": { "Enabled": true, "IntervalMinutes": 60, "SearchBase": "", "IncludeDisabled": false }
}
```

## Database

SQL scripts in `database/` (run in order):

| Script | Content |
|--------|-------|
| `01_create_database.sql` | database |
| `02_create_tables.sql` | Computers, WhitelistDevices, WhitelistVersions, Incidents, view + sp |
| `03_add_sourcefile.sql` | SourceFile + DisconnectedAt |
| `04_adsync_columns.sql` | LastSeen nullable + OperatingSystem / InActiveDirectory / AdSyncedAt |
| `05_adpath.sql` | AdPath (AD path) |
| `06_appsettings.sql` | AppSettings (central settings: enforcement, access, e-mail, retention, deploy) + grant; `Value` = `NVARCHAR(MAX)` (long lists) |
| `07_whitelist_publish.sql` | WhitelistVersions: `Json` (signed blob) + `Signature` → `NVARCHAR(MAX)` (publishing workflow) |
| `08_deploy_ignored.sql` | permanent exclusion of a station from deployment (bulk actions do not override it) |
| `09_activity_log.sql` | `ActivityLog` (operations log) + indexes + `sp_PurgeActivityLog` (cleanup in batches of 5000) |

Grants are deliberately **not** in the scripts (portability – no company accounts in the repo). The activity
log needs `SELECT, INSERT ON dbo.ActivityLog` for both the console and the API accounts, and
`EXECUTE ON dbo.sp_PurgeActivityLog` for the API.

## Quick start (development)

```powershell
# Agent (as Administrator for block mode)
cd agent\USBGuardian
dotnet run -- --console

# API
cd server\USBGuardian.Api
dotnet run

# Admin console
cd server\USBGuardian.Admin
dotnet run
```

## Deploying the console to the app server (APP_SERVER)

```powershell
# Build (self-contained – the target server needs no .NET)
dotnet publish server\USBGuardian.Admin -c Release -r win-x64 --self-contained -o D:\deploy\USBGuardianConsole

# Copy via SMB + service via remote sc.exe (no WinRM needed)
robocopy D:\deploy\USBGuardianConsole \\APP_SERVER_IP\C$\Apps\USBGuardianConsole /E /XF appsettings.local.json
# create \\APP_SERVER_IP\C$\Apps\USBGuardianConsole\appsettings.local.json (see .example)
sc.exe \\APP_SERVER_IP create USBGuardianConsole binPath= "C:\Apps\USBGuardianConsole\USBGuardian.Admin.exe" start= auto
sc.exe \\APP_SERVER_IP start USBGuardianConsole
```

> **Build/deploy artefacts:** published locally to `D:\deploy`; the API is staged on APP_SERVER at
> `C:\Apps\USBGuardianApiPublish` and installed from there onto SQL_SERVER at `C:\USBGuardian.Api` (service "USB Guardian API").

SQL grant (least-privilege) for the console account on SQL_SERVER:

```sql
CREATE LOGIN [DOMENA\APP_SERVER$] FROM WINDOWS;
USE USBGuardian;
CREATE USER [DOMENA\APP_SERVER$] FOR LOGIN [DOMENA\APP_SERVER$];
ALTER ROLE db_datareader ADD MEMBER [DOMENA\APP_SERVER$];
GRANT INSERT, UPDATE, DELETE ON dbo.Computers TO [DOMENA\APP_SERVER$];
GRANT INSERT, UPDATE, DELETE ON dbo.WhitelistDevices TO [DOMENA\APP_SERVER$];  -- DELETE = remove from catalog (✕)
GRANT INSERT, UPDATE ON dbo.WhitelistVersions TO [DOMENA\APP_SERVER$];          -- no DELETE (versions = append-only audit)
```

## Security

- RSA-signed whitelist – the agent rejects a forged catalog (fail-secure: what it cannot verify, it does not use).
  **A deliberate trade-off:** the signing key **is on the app server**, because publishing has to be automatic —
  signing offline by hand after every catalog change proved operationally unworkable. It is the tool's own
  internal key, not a company CA, and agents hold only the public part.
- TLS with **thumbprint pinning** – encrypted and authenticated without a certificate authority
  (can be disabled for development only).
- Windows Auth (Kerberos) – agents via machine account; console via admin group / whitelist.
- gMSA for SQL – no password in configuration.
- Least-privilege SQL grant for the console (read everything, write only where it actually writes).
- **Separate deploy identities** – compromising one does not reach both tiers (fleet × server).
- `*.local.json` gitignored.
- Agent local console: loopback, local admins only, writes limited to break-glass and a service restart.
- **API `FallbackPolicy`** (since 2026-09-04) – everything is protected by default, public only where
  explicitly marked `[AllowAnonymous]`. A new endpoint with no attribute can no longer end up silently public
  by accident.
- **Hostname verification** (since 2026-09-04, warn-only for now) – the server compares the hostname in the
  data against the caller's authenticated machine-account identity; for now it only logs a mismatch to
  Activity, and will be tightened to a hard rejection after a few days with no false positives.
- **Independent security audit** (2026-09-04) reviewed the repo after it went public – 6 findings, 5 fixed
  and deployed the same day (see `docs/oponentura.en.md` §34.7 for the detail on each).

## Repo structure

```
usb-guardian/
├── agent/USBGuardian/        # .NET 8 Windows Service agent
│   ├── LocalConsole/         # local admin console (HttpListener)
│   ├── Config/ Models/ Security/
│   ├── Security/ SessionUser.cs  # real user via the WTS API
├── server/
│   ├── USBGuardian.Api/      # ASP.NET Core API (incident ingestion, whitelist)
│   │   └── Retention/        # RetentionService (cleanup of old incidents)
│   └── USBGuardian.Admin/    # Blazor Server admin console (APP_SERVER)
│       ├── Components/        # Pages (Home, Computers, Whitelist, Settings, Database, Docs), Layout
│       ├── AdSync/            # AdSyncRunner + AdSyncService
│       ├── Deploy/            # AgentDeployService (auto-enrollment orchestrator)
│       ├── Export/            # ExportEndpoints (CSV + manager report)
│       ├── Notifications/     # IncidentAlertService + EmailSender
│       └── appsettings.local.json.example
├── tools/WhitelistSigner/    # offline RSA whitelist signing (generate/sign/verify)
├── database/                 # 01–09 SQL scripts
├── scripts/                  # certificates, Build-AgentPackage, watchdog, ToastHelper,
│                             #   Install/Uninstall-Agent, Deploy-AgentFleet, Update-Agent.cmd,
│                             #   Deploy-Api.cmd, Set/Archive-AgentVersion, New-DeployGmsa, tasks/
├── docs/                     # architecture(.en).md, auto-deploy-setup(.en).md, oponentura(.en).md,
│                             #   how-it-works.html (animation), mind-map.html, flowchart.html,
│                             #   management-summary.html (A4 one-pager)
├── README.md / README.en.md
└── HANDOFF.md / HANDOFF.en.md
```
