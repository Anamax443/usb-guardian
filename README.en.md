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
| 9 | **Server admin console** (Blazor on .213) per **AXIMA UI standard** (dark/light, footer, /api/version) | ✅ |
| 10 | **AD sync** – station inventory from AD + reconciliation (who lacks the agent) + AD path; communication icon | ✅ |
| 11 | **Overview** – cross-page tile summary, filter (period/action/search), aggregation, "Approved" column | ✅ |
| 12 | **Whitelist** – entry by serial number only + autofill from incidents + import + inline field edit + active checkbox | ✅ |
| 13 | **Central settings (DB)** – enforcement, console access whitelist, e-mail + incident alerts | ✅ |
| 14 | **Encrypted agent↔API comms** – self-signed cert (no CA, MachineKeySet) + thumbprint pinning | ✅ |
| 15 | **Communication oversight** – "Silent agents" tile + configurable threshold; **"Request data" on click**; sortable Detailed table | ✅ |
| 16 | **Startup scan** of already-connected media; whitelist poll 2 min; central `onExpired`/`enforce` | ✅ |
| 17 | **Agent auto-enrollment** – the console deploys the agent itself onto stations without it (gMSA + scheduled task, dry-run/opt-in); **PILOT SUCCESSFUL on .181 (no creds, via gMSA)** | ✅ pilot OK |
| 18 | **DB/incidents flowing** (agent→API→DB→console) | ✅ |
| 19 | **Version/commit on all components** (`/api/version`, agent reports commit; **reliable stamp** = footer/`/api/version` = git HEAD) | ✅ |
| 20 | **User attribution** – the real logged-in user via the **WTS API** (agent=SYSTEM → not the machine account); verified live | ✅ |
| 21 | **Complete client** – ToastHelper (notifications, logon+unlock) + **PS-free watchdog**, all in `Build-AgentPackage`; verified on .181 | ✅ |
| 22 | **Media capacity** in Overview and Whitelist; **CSV export** + **manager report** with charts (inline SVG, 1–2 A4) | ✅ |
| 23 | **Data retention** – Settings (console) + `RetentionService` in the API (purges old incidents); **Database page** (DB content overview) | ✅ |
| 24 | **Deploy targeting** – default for new PCs (Settings) + per-station and bulk include/exclude in Stations | ✅ |
| 25 | **Agent local console** also shows the list of approved devices (whitelist) + agent version | ✅ |
| 26 | **HTML animation** of how the system works (`/how-it-works.html`, 10 steps of the data flow) | ✅ |
| 27 | **Whitelist signing/publishing workflow (automatic)** – catalog change → console publishes and **signs internally** (server-side RSA, key on .213) → API serves the signed blob verbatim → **client = a 1:1 copy of the server** within ~2 min; agent O(1) match (scales to 10k) | ✅ |
| 28 | **Enforcement server→agent (Phase 2)** – heartbeat carries `policy.enforce` (.213 = truth) → agent really **blocks/warns** per the server | ✅ |
| 29 | **Local break-glass (Phase 3)** – station admin temporarily disables blocking offline (local console), persisted, **logged** → server; cleared on reconnect | ✅ |
| 30 | **Auto-re-enable + reconciliation** – on blocking off / break-glass the agent restores previously blocked media; a now-approved medium is restored even while blocking is on | ✅ |
| 31 | **Client service restart** (local console, agent self-restart) + **settings reload** (server console, AccessCache) | ✅ |
| – | Close unencrypted HTTP 5050 (HTTPS only) | 🔜 NIS2 |
| – | Per-serial **blocklist** + blocking of an already-connected device | 🔜 |
| – | Signing certificate expiry monitoring | 🔜 |

## Architecture

Three components, push model (agent → API), two-tier server (logic on the app server, DB = storage):

```
[Client station]                     [App server .213]            [DB server SQL-04]
┌────────────────────┐               ┌────────────────────┐       ┌──────────────────┐
│ Agent (.NET8 svc)  │               │ Admin console       │       │ SQL Server       │
│  WMI detection     │  push  HTTPS  │ (Blazor :4200)      │ read/ │ DB USBGuardian   │
│  whitelist check   ├──────────────►│  Overview/Stations  │ write │  Incidents       │
│  warn / block      │   ┌───────────┤  AD sync ◄── AD     ├──────►│  Computers       │
│  local console     │   │  push     │  Settings / Docs    │       │  WhitelistDevices│
│  (loopback :5080)  │   │           └────────────────────┘       │  WhitelistVersions│
└────────────────────┘   │           ┌────────────────────┐       └──────────────────┘
                         └──────────►│ API (:5050/:5443)   ├──read/write──────▲
                                     │  incident ingestion  │                  │
                                     │  whitelist delivery   │──────────────────┘
                                     └────────────────────┘
```

Details: [docs/architecture.md](docs/architecture.md). Handoff & live state: [HANDOFF.en.md](HANDOFF.en.md).

## Components

| Component | Technology | Where it runs |
|-----------|-------------|----------|
| **Agent** | C# .NET 8, Windows Service | every station (SYSTEM) |
| **API** | ASP.NET Core, :5050 / :5443 | `B-S-W-SQL-04`, installed at `C:\USBGuardian.Api`, Windows service "USB Guardian API" |
| **Admin console** | Blazor Server, :4200 | `10.8.2.213` (`B-S-W-MIKOS`, Windows service `USBGuardianConsole`) |
| **Database** | SQL Server | `B-S-W-SQL-04`, DB `USBGuardian` |
| **Authentication** | Windows Auth (Kerberos / Negotiate) | API: AD group; console: AD group + account whitelist |

## Server admin console (Blazor)

Runs on the app server (`10.8.2.213`), reads/writes SQL-04, **AXIMA UI standard** (archetype A – IT-ops:
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
- **Database** – read-only overview of the DB content (table row counts, incident date range for checking
  retention, `AppSettings` dump, the last 20 incidents).
- **Documentation** – hub + **printable HTML** pages (render `.md` via Markdig) +
  **interactive animation** "How it works" (`/how-it-works.html`).

Footer (service line per standard): **live clock + clickable commit hash + DB health + © Milan Trnka**.
Contract **`GET /api/version`**.

### Versioning (commit on all components)

Every component reports its git commit so the operator can verify exactly what is running:

- **Console** – commit in the **footer** + endpoint `:4200/api/version`.
- **API** – endpoint `:5050/api/version` (NEW).
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

## Agent local admin console

Optional (off by default), `localConsole.enabled` in `agent.config.local.json`. `HttpListener`
on `127.0.0.1:5080`, **admin-only, read-only** – live agent state: **the list of approved devices (whitelist)**,
whitelist status+version, **agent version (commit)**, WMI watchdog, queue, connected media and recent events.
For functional verification and offline diagnostics. Uses `HttpListener` (not Kestrel) so the
agent needs no ASP.NET Core runtime. No password needed (loopback + Windows auth + local admin only + read-only).

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
  (in live mode) writes the targets into `deploy.targetsFile`; the install is performed by a **scheduled task on .213 under a
  dedicated gMSA** (least-privilege). **Default OFF + dry-run.** Account setup: [docs/auto-deploy-setup.md](docs/auto-deploy-setup.md).

> **AXIMA environment:** PS scripts running on machines **must be signed** (execution policy AllSigned via GPO)
> with the prod cert `CN=powershell.axinetwork.loc`, and the publisher must be in `LocalMachine\TrustedPublisher`
> (on .213 and the clients, fleet via GPO). Before signing **CRLF + UTF-8 BOM** (otherwise HashMismatch).

## Configuration

Company-specific values live **only** in `*.local.json` (gitignored). Central operational settings
(enforcement, access, e-mail) live in the **DB** (`AppSettings`), managed from Settings. The `*.example` /
placeholder templates are in the repo.

| Component | Template (in repo) | Real (gitignored) |
|-----------|------------------|---------------------|
| Agent | `agent/USBGuardian/Config/agent.config.json` | `agent.config.local.json` |
| API | `server/USBGuardian.Api/appsettings.json` | `appsettings.local.json` |
| Console | `server/USBGuardian.Admin/appsettings.local.json.example` | `appsettings.local.json` |

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

## Deploying the console to the app server (.213)

```powershell
# Build (self-contained – the target server needs no .NET)
dotnet publish server\USBGuardian.Admin -c Release -r win-x64 --self-contained -o D:\deploy\USBGuardianConsole

# Copy via SMB + service via remote sc.exe (no WinRM needed)
robocopy D:\deploy\USBGuardianConsole \\10.8.2.213\C$\Apps\USBGuardianConsole /E /XF appsettings.local.json
# create \\10.8.2.213\C$\Apps\USBGuardianConsole\appsettings.local.json (see .example)
sc.exe \\10.8.2.213 create USBGuardianConsole binPath= "C:\Apps\USBGuardianConsole\USBGuardian.Admin.exe" start= auto
sc.exe \\10.8.2.213 start USBGuardianConsole
```

> **Build/deploy artefacts:** published locally to `D:\deploy`; the API is staged on .213 at
> `C:\Apps\USBGuardianApiPublish` and installed from there onto SQL-04 at `C:\USBGuardian.Api` (service "USB Guardian API").

SQL grant (least-privilege) for the console account on SQL-04:

```sql
CREATE LOGIN [DOMENA\B-S-W-MIKOS$] FROM WINDOWS;
USE USBGuardian;
CREATE USER [DOMENA\B-S-W-MIKOS$] FOR LOGIN [DOMENA\B-S-W-MIKOS$];
ALTER ROLE db_datareader ADD MEMBER [DOMENA\B-S-W-MIKOS$];
GRANT INSERT, UPDATE, DELETE ON dbo.Computers TO [DOMENA\B-S-W-MIKOS$];
GRANT INSERT, UPDATE ON dbo.WhitelistDevices TO [DOMENA\B-S-W-MIKOS$];
GRANT INSERT, UPDATE ON dbo.WhitelistVersions TO [DOMENA\B-S-W-MIKOS$];
```

## Security

- RSA-4096 signed whitelist – the agent rejects a forged whitelist (private key **never on the server**).
- TLS validation of the server certificate (can be disabled for development).
- Windows Auth (Kerberos) – agents via machine account; console via admin group / whitelist.
- gMSA for SQL – no password in configuration.
- Least-privilege SQL grant for the console (read everything, write only Computers + whitelist).
- `*.local.json` gitignored.
- Agent local console and the server one: loopback / admin-only / read-only per role.

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
│   └── USBGuardian.Admin/    # Blazor Server admin console (.213)
│       ├── Components/        # Pages (Home, Computers, Whitelist, Settings, Database, Docs), Layout
│       ├── AdSync/            # AdSyncRunner + AdSyncService
│       ├── Deploy/            # AgentDeployService (auto-enrollment orchestrator)
│       ├── Export/            # ExportEndpoints (CSV + manager report)
│       ├── Notifications/     # IncidentAlertService + EmailSender
│       └── appsettings.local.json.example
├── tools/WhitelistSigner/    # offline RSA whitelist signing (generate/sign/verify)
├── database/                 # 01–06 SQL scripts
├── scripts/                  # certificates, Build-AgentPackage, watchdog, ToastHelper,
│                             #   Install/Uninstall-Agent, Deploy-AgentFleet, New-DeployGmsa, tasks/
├── docs/architecture.md, docs/auto-deploy-setup.md, docs/how-it-works.html (animation)
├── README.md / README.en.md
└── HANDOFF.md / HANDOFF.en.md
```
