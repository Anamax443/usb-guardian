# USB Guardian

*🇬🇧 English · [🇨🇿 Čeština](README.md)*

Security tool for monitoring storage media (USB flash, SD cards, USB disks) on company
computers. Every device must be approved by IT and recorded in a central whitelist.
Unapproved media are warned or blocked. Designed as a technical control for
**NIS2 / Act 181/2014 Coll. / ISO 27001**.

> **Portability:** no company-specific values (server, domain, groups, accounts) live in the
> code — everything is in `*.local.json` (gitignored). Deploying to another company = editing
> configuration, not code.

## Project status

| Phase | Description | Status |
|------|-------|------|
| 1 | Agent – WMI detection, warn mode, Toast | ✅ |
| 2 | Block mode – DeviceIoControl IOCTL | ✅ |
| 3 | API server – ASP.NET Core, SQL Server, gMSA, Kerberos | ✅ |
| 4 | RSA-4096 whitelist signature – fail-secure | ✅ |
| 5 | Incident queue – bounded Channel, jitter, retry 503 | ✅ |
| 6 | HTTPS – Kestrel TLS, agent certificate validation | ✅ |
| 7 | Log role tagging `[KLIENT]`/`[SERVER]` | ✅ |
| 8 | **Agent local admin console** (HttpListener, loopback, read-only) | ✅ |
| 9 | **Server admin console** (Blazor on .213): Overview, Stations, Settings, Docs | ✅ |
| 10 | **AD sync** – station inventory from Active Directory + reconciliation (who lacks the agent) | ✅ |
| – | Remote agent install onto stations without it (WinRM) | 🔜 Planned |
| – | Web whitelist management + signing workflow | 🔜 Planned |
| – | gMSA for console, dedicated `USB-Guardian-Admins` group, console HTTPS | 🔜 Hardening |
| – | Toast Privilege Separation, email notifications (Graph) | 🔜 Planned |

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
| **API** | ASP.NET Core, :5050 / :5443 | `B-S-W-SQL-04` (Windows service) |
| **Admin console** | Blazor Server, :4200 | `10.8.2.213` (`B-S-W-MIKOS`, Windows service `USBGuardianConsole`) |
| **Database** | SQL Server | `B-S-W-SQL-04`, DB `USBGuardian` |
| **Authentication** | Windows Auth (Kerberos / Negotiate) | API: AD group; console: AD group + account whitelist |

## Server admin console (Blazor)

Runs on the app server (`10.8.2.213`), reads/writes SQL-04. Pages:

- **Overview** – 30-day incidents (Blocked / Warned) + recent events incl. device identifiers
  **VID / PID / serial number** (the whitelist values).
- **Stations** – computer inventory from AD; tiles filter (all / reporting / missing agent);
  AD path (OU) next to hostname; **Refresh from AD** button.
- **Settings** – effective configuration (read-only; edited via `appsettings.local.json`).
- **Documentation** – in-browser help.

Footer: live clock + build commit hash.

**Authorization:** Windows Auth; access only for members of `Authorization:AdminGroups` or accounts
in `Authorization:AllowedUsers`. For silent SSO use the hostname, not the IP.

### AD sync

A background service (also on demand via a button) reads computers from Active Directory and writes
them into the `Computers` table. The key is the **hostname** (not the IP – stations have dynamic
addresses). The domain is taken automatically from the server (`new DirectoryEntry()`, nothing
hardcoded). Reconciliation: *in AD ⨯ reporting an agent* → the list of stations missing the agent.

## Agent local admin console

Optional (off by default), `localConsole.enabled` in `agent.config.local.json`. `HttpListener`
on `127.0.0.1`, **admin-only, read-only** – live agent state (whitelist, WMI, queue, connected
media) for functional verification and offline diagnostics. Uses `HttpListener` (not Kestrel) so
the agent does not need the ASP.NET Core runtime.

## Configuration

Company-specific values live **only** in `*.local.json` (gitignored). Templates with placeholders
are in the repo.

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
    "AdminGroups": [ "DOMAIN\\USB-Guardian-Admins" ],
    "AllowedUsers": [ "DOMAIN\\admin.name" ],
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

## Quick start (dev)

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

# Copy over SMB + service via remote sc.exe (no WinRM needed)
robocopy D:\deploy\USBGuardianConsole \\10.8.2.213\C$\Apps\USBGuardianConsole /E /XF appsettings.local.json
# create \\10.8.2.213\C$\Apps\USBGuardianConsole\appsettings.local.json (see .example)
sc.exe \\10.8.2.213 create USBGuardianConsole binPath= "C:\Apps\USBGuardianConsole\USBGuardian.Admin.exe" start= auto
sc.exe \\10.8.2.213 start USBGuardianConsole
```

Least-privilege SQL grant for the console account on SQL-04:

```sql
CREATE LOGIN [DOMAIN\B-S-W-MIKOS$] FROM WINDOWS;
USE USBGuardian;
CREATE USER [DOMAIN\B-S-W-MIKOS$] FOR LOGIN [DOMAIN\B-S-W-MIKOS$];
ALTER ROLE db_datareader ADD MEMBER [DOMAIN\B-S-W-MIKOS$];
GRANT INSERT, UPDATE, DELETE ON dbo.Computers TO [DOMAIN\B-S-W-MIKOS$];
GRANT INSERT, UPDATE ON dbo.WhitelistDevices TO [DOMAIN\B-S-W-MIKOS$];
GRANT INSERT, UPDATE ON dbo.WhitelistVersions TO [DOMAIN\B-S-W-MIKOS$];
```

## Security

- RSA-4096 signed whitelist – the agent rejects a forged whitelist (private key **never on the server**).
- TLS validation of the server certificate (can be disabled for dev).
- Windows Auth (Kerberos) – agents via machine account; console via admin group / whitelist.
- gMSA for SQL – no password in configuration.
- Least-privilege SQL grant for the console (read everything, write only Computers + whitelist).
- `*.local.json` gitignored.
- Both the agent and server consoles: loopback / admin-only / read-only per their role.

## Repo structure

```
usb-guardian/
├── agent/USBGuardian/        # .NET 8 Windows Service agent
│   ├── LocalConsole/         # local admin console (HttpListener)
│   ├── Config/ Models/ Security/
├── server/
│   ├── USBGuardian.Api/      # ASP.NET Core API (incident ingestion, whitelist)
│   └── USBGuardian.Admin/    # Blazor Server admin console (.213)
│       ├── Components/        # Pages (Home, Computers, Settings, Docs), Layout
│       ├── AdSync/            # AdSyncRunner + AdSyncService
│       └── appsettings.local.json.example
├── database/                 # 01–05 SQL scripts
├── scripts/                  # certificates, watchdog, ToastHelper
├── docs/architecture.md
├── README.md / README.en.md
└── HANDOFF.md / HANDOFF.en.md
```
