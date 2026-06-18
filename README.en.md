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
| – | Close unencrypted HTTP 5050 (HTTPS only) | 🔜 NIS2 |
| – | Distribution + **remote agent install** (WinRM) onto stations without it | 🔜 |
| – | **Signing/publishing workflow** for the whitelist → enforcement + blocklist live to agents | 🔜 |
| – | Per-serial **blocklist** (ban a specific device, near-real-time to agents) | 🔜 |

## Architecture

Three components, push model (agent → API), two-tier server (logic on the app server, DB = storage):

```
[Client station]                     [App server .213]            [DB server SQL-04]
┌────────────────────┐               ┌────────────────────┐       ┌──────────────────┐
│ Agent (.NET8 svc)  │               │ Admin console       │       │ SQL Server       │
│  WMI detection     │  push  HTTPS  │ (Blazor :4200)      │ read/ │ DB USBGuardian   │
│  whitelist check   ├──────────────►│  Overview/Stations  │ write │  Incidents       │
│  warn / block      │   ┌───────────┤  AD sync ◄── AD     ├──────►│  Computers       │
│  local console     │   │  push     │  Whitelist/Settings │       │  WhitelistDevices│
│  (loopback :5080)  │   │           └────────────────────┘       │  AppSettings ... │
└────────────────────┘   │           ┌────────────────────┐       └──────────────────┘
                         └──────────►│ API (:5443 HTTPS)   ├──read/write──────▲
                                     │  incident ingestion  │                  │
                                     │  whitelist delivery   │──────────────────┘
                                     └────────────────────┘
```

Details: [docs/architecture.md](docs/architecture.md). Handoff & live state: [HANDOFF.en.md](HANDOFF.en.md).

## Components

| Component | Technology | Where it runs |
|-----------|-------------|----------|
| **Agent** | C# .NET 8, Windows Service | every station (SYSTEM) |
| **API** | ASP.NET Core, :5050 / :5443 | `B-S-W-SQL-04` (Windows service) |
| **Admin console** | Blazor Server, :4200 | `10.8.2.213` (`B-S-W-MIKOS`, Windows service `USBGuardianConsole`) |
| **Database** | SQL Server | `B-S-W-SQL-04`, DB `USBGuardian` |
| **Authentication** | Windows Auth (Kerberos / Negotiate) | API: AD group; console: AD group + account whitelist |

## Server admin console (Blazor)

Runs on the app server (`10.8.2.213`), reads/writes SQL-04, **AXIMA UI standard** (archetype A – IT-ops:
dark/light toggle `axima.theme` without FOUC, print = light, status traffic-lights). Pages:

- **Overview** – cross-page tile summary (Stations in AD / Missing agent / Approved media / Deactivated /
  Incidents / Blocked / Warned, click-through). **Filter** (period 30/90/year/all, action, full-text) +
  **aggregation** (group by media+station+user with count) + device identifiers **VID/PID/serial** +
  **"Approved"** column (currently per whitelist).
- **Stations** – inventory from AD; tiles filter (all / reporting / missing agent), **search**,
  **AD path** (OU) next to hostname, **communication icon** (green ≤60 min / amber silent / grey no contact),
  **Refresh from AD** button.
- **Whitelist** – approved media; **enter just the serial number** (VID/PID/name autofill from incidents,
  retroactively too), **bulk import**, **inline field edit**, **Active checkbox** (temporary deactivation).
- **Settings** (central, in DB) – **enforcement** (require only approved media), **console access whitelist**
  (users/groups; appsettings = lockout-safe bootstrap), **e-mail** (SMTP relay/Direct Send + test) and
  **incident alerts** (interval), AD sync / DB / build info.
- **Documentation** – hub + **printable HTML** pages (render `.md` via Markdig, no external links).

Footer (service line per standard): **live clock + clickable commit hash + DB health + © Milan Trnka**.
Contract **`GET /api/version`**.

**Authorization:** Windows Auth; access only for `Authorization:AdminGroups` / `Authorization:AllowedUsers`
(appsettings) **or** the DB list from Settings. For silent SSO use the hostname, not the IP.

### AD sync

A background service (also on demand via a button) reads computers from Active Directory and writes them
into `Computers`. Keyed by **hostname** (not IP – stations have dynamic addresses). Domain taken
automatically from the server (`new DirectoryEntry()`, nothing hardcoded). Reconciliation: *in AD ⨯
reporting an agent* → list of stations missing the agent.

## Agent local admin console

Optional (off by default), `localConsole.enabled` in `agent.config.local.json`. `HttpListener` on
`127.0.0.1`, **admin-only, read-only** – live agent state. Uses `HttpListener` (not Kestrel) so the agent
needs no ASP.NET Core runtime.

## Encrypted agent ↔ API comms (self-contained TLS)

NIS2 requires encrypted transport. Solved **without any CA / external cert dependency**:

- **API** generates/persists its **own self-signed cert** at startup (`SelfCert.cs`,
  `C:\ProgramData\USBGuardian\api-tls.pfx`), Kestrel binds it on `:5443`. The key is **`MachineKeySet`**
  (works under gMSA; usable by Schannel). It logs the thumbprint (PIN) and `GET /api/cert-info` returns it.
- **Agent** does not pin via a CA but via the **thumbprint** (`tls.pinnedThumbprint` in config,
  `TlsClient.cs`) → encrypted **and** authenticated, no CA. Without a pin you can use
  `validateServerCertificate=false` (dev only) or CA validation.

Agent prod config: `whitelist.syncUrl = https://SERVER:5443` + `tls.pinnedThumbprint = <thumbprint from /api/cert-info>`.

## Configuration

Company-specific values live **only** in `*.local.json` (gitignored). Central operational settings
(enforcement, access, e-mail) live in the **DB** (`AppSettings`), managed from Settings.

| Component | Template (in repo) | Real (gitignored) |
|-----------|------------------|---------------------|
| Agent | `agent/USBGuardian/Config/agent.config.json` | `agent.config.local.json` |
| API | `server/USBGuardian.Api/appsettings.json` | `appsettings.local.json` |
| Console | `server/USBGuardian.Admin/appsettings.local.json.example` | `appsettings.local.json` |

## Database

SQL scripts in `database/` (run in order):

| Script | Content |
|--------|-------|
| `01_create_database.sql` | database |
| `02_create_tables.sql` | Computers, WhitelistDevices, WhitelistVersions, Incidents, view + sp |
| `03_add_sourcefile.sql` | SourceFile + DisconnectedAt |
| `04_adsync_columns.sql` | LastSeen nullable + OperatingSystem / InActiveDirectory / AdSyncedAt |
| `05_adpath.sql` | AdPath (AD path) |
| `06_appsettings.sql` | AppSettings (central settings: enforcement, access, e-mail) + grant |

## Deploying the console to the app server (.213)

```powershell
dotnet publish server\USBGuardian.Admin -c Release -r win-x64 --self-contained -o D:\deploy\USBGuardianConsole
robocopy D:\deploy\USBGuardianConsole \\10.8.2.213\C$\Apps\USBGuardianConsole /E /XF appsettings.local.json
# create appsettings.local.json (see .example)
sc.exe \\10.8.2.213 create USBGuardianConsole binPath= "C:\Apps\USBGuardianConsole\USBGuardian.Admin.exe" start= auto
sc.exe \\10.8.2.213 start USBGuardianConsole
```

## Security

- RSA-4096 signed whitelist – the agent rejects a forged whitelist (private key **never on the server**).
- Encrypted agent↔API via self-signed cert + **thumbprint pinning** (no CA).
- Windows Auth (Kerberos) – agents via machine account; console via admin group / whitelist (DB-managed too).
- gMSA for SQL – no password in configuration.
- Least-privilege SQL grant for the console.
- `*.local.json` gitignored.

## Repo structure

```
usb-guardian/
├── agent/USBGuardian/        # .NET 8 Windows Service agent
│   ├── LocalConsole/  Security/ (TlsClient) Config/ Models/
├── server/
│   ├── USBGuardian.Api/      # ASP.NET Core API (ingestion, whitelist, SelfCert TLS)
│   └── USBGuardian.Admin/    # Blazor Server admin console (.213)
│       ├── Components/ (Pages, Layout)  AdSync/  Security/  Notifications/
│       └── appsettings.local.json.example
├── database/                 # 01–06 SQL scripts
├── scripts/                  # certificates, watchdog, ToastHelper
├── docs/architecture.md
├── README.md / README.en.md
└── HANDOFF.md / HANDOFF.en.md
```
