# USB Guardian – Architecture

*[🇨🇿 Čeština](architecture.md) · 🇬🇧 English*

## System overview

```
┌─────────────────────────────────────────────────────────────────────┐
│  Client PC (Windows 10/11)                                          │
│                                                                     │
│  ┌─────────────────────────────────────────────────────────────┐   │
│  │  USB Guardian Agent (Windows Service – SYSTEM)              │   │
│  │                                                             │   │
│  │  DeviceMonitor ──► WhitelistChecker ──► PolicyEnforcer      │   │
│  │    (WMI)              (RSA verify)      (warn / block)      │   │
│  │                                             │               │   │
│  │                          ┌──────────────────┤              │   │
│  │                          ▼                  ▼               │   │
│  │                   NotificationService   IncidentLogger      │   │
│  │                   (Toast – user session) (JSON queue)       │   │
│  │                                          DeviceBlocker      │   │
│  │                                          (IOCTL)            │   │
│  │                                                             │   │
│  │  WhitelistSync ──────────────────────────────────────────┐  │   │
│  │  IncidentSync  ──────────────────────────────────────────┤  │   │
│  └──────────────────────────────────────────────────────────┼──┘   │
│                                                             │       │
└─────────────────────────────────────────────────────────────┼───────┘
                              HTTPS (TLS) │ port 5443
                              ┌───────────┘
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│  Server (SQL_SERVER or a dedicated Windows Server)                │
│                                                                     │
│  ┌──────────────────────────────────┐   ┌────────────────────────┐ │
│  │  USB Guardian API                │   │  SQL Server            │ │
│  │  ASP.NET Core – port 5443 (HTTPS only)│   │  Database: USBGuardian │ │
│  │                                  │   │                        │ │
│  │  /api/whitelist  (GET)           │◄─►│  Incidents             │ │
│  │  /api/incidents  (POST/GET)      │   │  WhitelistVersions     │ │
│  │  /api/heartbeat  (GET)           │   │  Computers             │ │
│  │  ActivityLogger (activity log)   │   │  AppSettings           │ │
│  │  Windows Auth (Kerberos)         │   │  ActivityLog  ◄──────┐ │ │
│  │  AD groups: USB-Guardian-Clients │   │  gMSA: gmsa-api$     │ │ │
│  └──────────────────────────────────┘   └──────────────────────┼─┘ │
│                                                                │   │
│  ┌──────────────────────────────────┐                          │   │
│  │  Admin console (Blazor :4200)    │  operator actions ───────┘   │
│  │  Overview · Stations · Whitelist │  (deployment, exclusion,     │
│  │  Settings · Health · Activity    │   whitelist publication)     │
│  │  AD sync ◄── Active Directory    │                              │
│  └──────────────────────────────────┘                              │
└─────────────────────────────────────────────────────────────────────┘
```

> The activity log (`ActivityLog`) is the single place **both** server-side parts write to — the API records
> agent traffic, the console records operator actions. That is what makes the operation readable as one story.

## Agent components

| Component | Description |
|-----------|-------------|
| `DeviceMonitor` | WMI subscriber – Win32_DiskDrive connect/disconnect events + a **startup scan** of already-attached media (watchers only catch new plug-ins) + **`ReEnforceConnectedDevices()`** (re-blocks attached unapproved media when blocking is switched back on) |
| `WhitelistChecker` | Reads the local `whitelist.json`, verifies the RSA signature |
| `PolicyEnforcer` | Decides the action per `policy.mode` (warn / block) |
| `NotificationService` | Windows toast notification for the logged-on user |
| `IncidentLogger` | Stores incidents in JSON queues (`queue/`) |
| `DeviceBlocker` | Blocks a medium via DeviceIoControl (IOCTL_STORAGE_EJECT_MEDIA) / `Disable-PnpDevice` |
| `WhitelistSync` | Heartbeat + whitelist download (interval: **2 min**, config `sync:whitelistSyncIntervalMinutes`). The heartbeat carries version/online state; when the whitelist changes it is downloaded in the same cycle → a new whitelist reaches clients within ~2 min |
| `IncidentSync` | Pushes the incident queue to the server (interval: 1 min with jitter; wakes up earlier on `ReportNow`) |
| `SyncSignals` | Shared signal: heartbeat (`ReportNow`) → immediate flush of the incident queue |
| `SignatureVerifier` | Verifies the RSA signature of the whitelist – fail-secure |
| `SessionUser` | The real logged-on user via the WTS API (agent = SYSTEM → not `Environment.UserName` = `HOST$`); fail-safe fallback to the machine account |
| `SelfRestart` | Daily restart of the service (default **on, 04:15**) – keeps the agent fresh (a stuck WMI watcher, a leaked handle) |

## Server components

| Component | Description |
|-----------|-------------|
| `IncidentsController` | POST intake of incidents from agents (returns **202 Accepted** – enqueues into `IncidentQueue`, does **not** write to the DB itself), GET for the Admin UI |
| `WhitelistController` | GET the current whitelist + version + signature |
| `HeartbeatController` | GET server health; returns the enforcement policy and pending commands |
| `IncidentQueue` | In-memory queue of received incidents (between the controller and the worker) |
| `IncidentQueueWorker` | Background worker – takes from `IncidentQueue` and **it alone** writes incidents to the DB (async) |
| `ActivityLogger` | Writes to the **activity log** (`ActivityLog`) – a shared source linked into both the API and the console, fire-and-forget (see below) |
| `AppDbContext` | EF Core context – SQL Server via gMSA Windows Auth |

> **DI:** intake and writing are separated, so `Program.cs` must register **`IncidentQueue`** (singleton)
> **and** the hosted **`IncidentQueueWorker`** – without both, incidents are accepted (202) but never stored.

## Server admin console (USBGuardian.Admin)

A separate **Blazor Server** application on the app server (`APP_SERVER_IP`), Windows service
`USBGuardianConsole`, port `:4200`. Kept apart from the ingestion API (resilience – intake from 500+ agents
must not affect admin use). Reads/writes SQL_SERVER, models reused from the API (linked `DbModels.cs` +
`AppDbContext.cs` – no duplication).

| Component | Description |
|-----------|-------------|
| `Home` (Overview) | Incidents over 30 days + recent events incl. VID/PID/serial |
| `Computers` (Stations) | Inventory from AD; tiles = filter; AD path (OU); Refresh from AD button |
| `Settings` / `Docs` | Central settings / documentation hub in the browser |
| `Activity` | The activity log with filters, live mode and CSV export |
| `Health` | Health checks of the server and clients + scheduled service restart |
| `AdSyncRunner` | AD sync logic – callable from the timer and from the UI (a semaphore prevents overlap) |
| `AdSyncService` | Timer over `AdSyncRunner` (interval from config) |
| `AppInfo` | Build commit hash (MSBuild `git rev-parse` stamp) → footer + `:4200/api/version` |

**Authorization:** Windows Auth (Negotiate). Access only for members of `Authorization:AdminGroups`
(AD group) **or** accounts in `Authorization:AllowedUsers` (whitelist). Checked via
`WindowsPrincipal.IsInRole` (handles domain groups). `DevAllowAll` = a bypass for development only.

### AD sync

```
Active Directory (objectCategory=computer, not disabled)
        ↓  (new DirectoryEntry() – ambient domain, nothing hardcoded)
AdSyncRunner: name → Hostname, dNSHostName → Domain, operatingSystem, distinguishedName → AdPath (OU)
        ↓  upsert (key = hostname), does NOT overwrite LastSeen/AgentVersion (owned by the agent/API)
SQL Computers + reconciliation: InActiveDirectory; "in AD ⨯ reporting an agent" = where the agent is missing
```

## Agent local admin console

`LocalConsoleService` – `HttpListener` on `127.0.0.1:5080` (optional, `localConsole.enabled`, off in the
template; port `localConsole.port`). Local administrators only. Live in-memory agent state: **the list of
approved devices (whitelist)** incl. VID/PID/serial/description/approver/validity, whitelist status+version,
**agent version (commit)**, WMI watchdog, incident queue, currently attached media and recent events.
`HttpListener` deliberately instead of Kestrel – the agent (`Sdk.Worker`) needs no ASP.NET Core runtime;
loopback → plain HTTP is acceptable. Endpoints: `GET /` (HTML dashboard, 3 s auto-refresh) ·
`GET /api/status` (JSON, incl. the count and list of media the agent currently holds blocked) · writing
(admin-only): `POST /api/override[/clear]` (break-glass) · `POST /api/unblock-all` (**returns every medium the
agent itself disabled, at once** – a manual safety net next to the automatic return) · `POST /api/restart`
(**restarts the client service** – the agent, running as SYSTEM, restarts its own service through a detached
`cmd: sc stop → pause → sc start`; a local admin triggers it from the dashboard, no server or domain admin
on the clients is needed).

### Two views on one port (the role decides what a person sees)

The console does not turn an ordinary account away — it **shows that person their own situation**. The fork is
a single place in `HandleRequest`: a local admin goes to the full dashboard, anyone else to the user page.

| Endpoint | Who | What it returns |
|----------|-----|-----------------|
| `GET /`, `GET /uzivatel` | anyone logged on locally | the user page (HTML) |
| `GET /api/muj-stav` | anyone logged on locally | a narrow slice of the state: the protection mode, attached media and their `VID:PID:SN` key |
| `GET /api/status`, every `POST` | a local admin only | the full state and the writing actions; otherwise 403 with an explanation |

The user page does **not** see the whitelist (knowing approved serial numbers weakens the protection), the
incident queue or the diagnostics, and it has no writing action at all — break-glass stays with the admin.
What it does show for an unapproved medium is its identification, with a button that copies it into a message
for IT; that is the whole point of the page: to answer "why doesn't my flash drive work" before the person
picks up the phone.

So that there is something to show, `DeviceMonitor` now keeps the **medium's key and size** alongside an
active connection (previously only the name and the connect time). The rendering is guarded by
`node tests/user-page.test.mjs`: it reads the HTML straight out of the agent's source and checks that the
states and badges match the data (blocking / warning only / break-glass, approved / blocked, the
identification only for an unapproved medium).

### Local console authorization – the filtered token

A request to `127.0.0.1` is, as far as Windows is concerned, a **network logon**. For a **local** account,
`LocalAccountTokenFilterPolicy` strips the `Administrators` group from such a token (it remains in it only as
*deny-only*), so `WindowsPrincipal.IsInRole(Administrator)` returns **false** even though the person **is** a
local admin. That made break-glass unavailable in exactly the situation it exists for (a technician at a
station that cannot reach the server).

> **Who gets into the console:** **only a local administrator of that station** — the console is not for the
> end user. In an environment where admin rights live on separate accounts (`pcadmin.*` in the `Workstation-Admins`
> group), break-glass is in effect a tool for IT, not for the user; an ordinary account gets the explanatory
> refusal. This is intentional: switching blocking off is an intervention into the enforced policy, even if
> a temporary and logged one.

The check therefore **accepts a filtered token** (deny-only SID membership) as well. This is safe because
membership serves as **authorization**, not as the source of rights: the action itself is carried out by the
service running as SYSTEM, no elevated caller token is needed. A refusal returns a page showing **who** the
request was seen as and what is required — without that it could be diagnosed neither remotely nor on site.

### Daily agent restart

`SelfRestart` (default **on, 04:15**, configurable, switchable from the local console) keeps the agent fresh —
a stuck WMI watcher or a leaked handle survives a service restart, but not a day of operation.

## Device identification

```
VID:PID:SERIAL  →  the comparison key (uppercase)
e.g.: KINGSTON:DATATRAVELER_3.0:4E0788D05AC9
```

A whitelist entry contains: `vendorId`, `productId`, `serialNumber`, `description`, `approvedAt`, `approvedBy`

> **Note – trimming the serial:** WMI often returns the serial with **trailing spaces**; it must be
> **trimmed** before comparison and before storing, otherwise the whitelist match fails or a "dirty" serial
> gets saved.

> **Note – user attribution:** the agent runs as **SYSTEM**, so `Environment.UserName` would return the
> **machine account** (`HOST$`), not the real user. `SessionUser` therefore reads the user of the active
> interactive session through the **WTS API** (`WTSGetActiveConsoleSessionId` → fallback to enumerating active
> sessions via `WTSEnumerateSessions`; `WTSQuerySessionInformation` for `WTSUserName`+`WTSDomainName`) →
> `DOMAIN\user`. **Fail-safe:** when nobody is logged on (locked / services only) it falls back to
> `Environment.UserName` (the machine account) – an incident is always recorded. Used in `Incident.Username`,
> in `PolicyEnforcer` (log) and in the toast notification.

## Security layers

| Layer | Mechanism |
|-------|-----------|
| Transport | TLS 1.2+ (Kestrel), the agent verifies the server by **thumbprint pinning** (no CA) |
| Authentication | Windows Auth – Kerberos Negotiate |
| Authorization | AD groups – `USB-Guardian-Clients` (API), admin group + account whitelist (console) |
| Data integrity | RSA signature of the whitelist, fail-secure (what the agent cannot verify, it does not use) |
| Service account | gMSA `DOMENA\gmsa-api$` – no password in configuration |
| Tier separation | three deploy identities: fleet × server × the running console (which is an admin nowhere) |
| Audit trail | incidents + the **activity log** (who talked to whom, who changed what) |
| Configuration | `appsettings.local.json` gitignored – sensitive values outside the repo |

> **Where the signing key lives:** the whitelist private key **is on the app server**
> (`Whitelist:PrivateKeyPath`). This is a deliberate trade-off for fully automatic publishing — signing
> offline by hand after every catalog change proved operationally unworkable. It is the tool's own internal
> key (agents hold only the public part), not a company CA; it is protected by ACLs on the server. The offline
> `WhitelistSigner` remains for key generation and manual verification.

## Configuration – key values

### Agent (`agent.config.json`)

```json
{
  "policy": {
    "mode": "warn",               // warn | block
    "onExpiredWhitelist": "warn"  // warn | block | allow
  },
  "whitelist": {
    "syncUrl": "https://SERVER:5443",
    "localPath": "C:\\ProgramData\\USBGuardian\\whitelist\\whitelist.json"
  },
  "tls": {
    "validateServerCertificate": true   // false for development only
  },
  "signing": {
    "enabled": true   // false for development only
  },
  "localConsole": {
    "enabled": false, // break-glass console on 127.0.0.1 – see the roadmap note
    "port": 5080
  }
}
```

### Server (`appsettings.json` + `appsettings.local.json`)

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=tcp:SQL_SERVER,1433;Database=USBGuardian;Integrated Security=true;"
  },
  "Authorization": {
    "AllowedGroups": ["DOMAIN\\USB-Guardian-Clients"]
  }
}
```

## Logging

Both processes use their own `RoleTagFormatter` (a console formatter):

```
HH:mm:ss [KLIENT] info: USBGuardian.DeviceMonitor[0]
HH:mm:ss [SERVER] info: USBGuardian.Api.IncidentController[0]
```

- **Agent** → `[KLIENT]`
- **Server** → `[SERVER]`
- Production: the agent logs to the Windows Event Log, the server to the Event Log and the console

## Versioning (commit on every component)

Every component reports its git commit (stamped by MSBuild `git rev-parse` at build time) so an operator can
verify what is actually running (= a currency check: what is in git must be what the page shows). **The stamp
is reliable** – a generated source file `GitCommit.g.cs` is rewritten only when the commit changes
(`WriteOnlyWhenDifferent`), which forces a recompile even when no other `.cs` changed. Previously
(`BeforeTargets=CoreGenerateAssemblyInfo`) an incremental build could keep an old commit.

| Component | Where |
|-----------|-------|
| Console | footer + `:4200/api/version` |
| API | `:5443/api/version` |
| Agent | reports the commit in the heartbeat → console "Agent version" |

## Data flow – an incident

```
1. USB attached → WMI event
2. The agent identifies VID:PID:Serial
3. WhitelistChecker: the medium is NOT on the whitelist
4. PolicyEnforcer: mode=warn
5. NotificationService: toast to the user
6. IncidentLogger: store into queue/log_MACHINE_DATE.json
7. IncidentSync (1 min): send to the server /api/incidents
8. IncidentsController: enqueue into IncidentQueue → return 202 Accepted (does NOT write to the DB)
9. IncidentQueueWorker (async): dequeue and write into the SQL table Incidents
10. ActivityLogger: a log line "batch of N incidents received from station X" (fire-and-forget)
```

## Activity log (ActivityLog)

Only what **ended as an incident** lands in `Incidents`. When an agent stopped communicating, when somebody
changed the whitelist, or when a version was deployed, no trace remained anywhere except in the Event Log of a
single machine — and nobody looks there. The activity log is the one place where the operation of the whole
system is visible.

```
API      → heartbeats (including WHAT the server answered), incident batches received
Console  → manual deployment/update, permanent exclusion of a station, whitelist publication
              ↓ both through the shared ActivityLogger
        dbo.ActivityLog (Timestamp · Level · Source · Hostname · User · Message)
              ↓
        Activity page: filters (period/level/source/search), "live" mode (3 s), CSV export
```

**Why fire-and-forget:** the write runs off the main request path and every error is swallowed. If an agent's
heartbeat failed because a log row could not be written, the **observer would matter more than the thing it
observes**. For the same reason the write is not awaited — the pulse of hundreds of agents must not be tied to
database latency.

**Both sides write into the SAME table**, so the operation reads as one story rather than two. The source list
in the filter is derived from the data, not from a fixed list — otherwise it would drift away from what is
really written into the log.

**Retention:** rows accumulate fast (a heartbeat every 2 min × the number of stations ≈ 150k/day at 213
stations). Cleanup is the job of `sp_PurgeActivityLog` (deleting in batches of 5000 so it does not become a
long lock on a table agents are writing into). **Careful – nothing calls the procedure yet**, see the roadmap.

## Deployment

### Development

```
dotnet run -- --console    (agent)
dotnet run                 (server)
```

### Production

- **Complete client package:** `scripts\Build-AgentPackage.ps1` → self-contained agent (root) +
  `ToastHelper\` (notifications in the user session) + `tasks\` (scheduled task definitions). The client needs
  no .NET runtime.
- Agent: Windows Service, running as SYSTEM
- **ToastHelper:** scheduled task `\USBGuardian\USBGuardian-ToastHelper` (triggers on logon + unlock, runs in
  the user session, least-privilege) – registered **PS-free** via `schtasks /XML`
  (`tasks\USBGuardian-ToastHelper.xml`).
- **Watchdog:** scheduled task `\USBGuardian\USBGuardian-Watchdog` (every 3 min, PS-free `sc start`).
- Fleet rollout of both: `Deploy-AgentFleet.ps1` (robocopy the package + sc.exe create + both tasks), under a
  gMSA from `APP_SERVER`.
- Server: Windows Service, running under a gMSA
- HTTPS certificate: `scripts\New-Certificate.ps1` on the production server
- AD groups: `USB-Guardian-Clients` – machines with the agent deployed

### Updating a deployed agent and deploying the API

Installation and update are **two different jobs**. The fleet script could only do a clean install; updating by
going "straight to robocopy" would overwrite part of the DLLs on a running agent, the copy of the locked `.exe`
would fail, and the station would be left with a **mix of versions** while the deploy reports success.

| Step | Script | Task on `APP_SERVER` | Account |
|------|--------|----------------|---------|
| Clean install on stations without an agent | `Deploy-AgentFleet.ps1` | `USBGuardian-AutoDeploy` | `gmsa-deploy$` |
| Update of a deployed agent | `Update-Agent.cmd` | `USBGuardian-UpdateAgent` (+ `-UpdateAgentBeta`) | `gmsa-deploy$` |
| Deployment of the API to its server | `Deploy-Api.cmd` | `USBGuardian-ApiDeploy` | `gmsa-srvdeploy$` |

Both `.cmd` files follow the same pattern: **stop the service → wait for `STOPPED` → copy (without
`*.local.json`) → start → verify `RUNNING`**; the return code is the number of failed stations, the log lives
in `C:\ProgramData\USBGuardian\deploy\`.

**Batch (.cmd), not PowerShell:** the environment enforces `AllSigned` through GPO; a `.cmd` is not subject to
it, so changing a deployment step does not require re-signing.

**Channels and rollback:** the package is archived per version (`stable` / `beta`, `Set-AgentVersion.cmd` /
`Archive-AgentVersion.cmd`), so a previous version can be deployed. The package also carries an **offline
installer** (`Install-Agent.cmd` / `Uninstall-Agent.cmd`) for a station the deploy channel cannot reach —
including cleaning up after itself.

> **Gotcha – a task under a gMSA:** `schtasks /Create /RU "…gmsa$"` without a password produces a task with
> `LogonType=InteractiveToken` → it never runs ("the user was not logged on", event 332). S4U (`/NP`) has no
> network credentials and cannot reach `\\HOST\C$`. Only one thing works: take the XML of a working task,
> replace `<Command>`/`<Arguments>`/`<URI>`, save it as **UTF-16** and create it via `/XML` — that carries
> `LogonType=Password`, for which the system fetches the gMSA password itself.

### Separate deploy identities

One account must not hold both the fleet and the server — compromising the deploy identity would otherwise
reach both.

| Role | Account | Where it is an admin |
|---|---|---|
| Clients (auto-enrollment, update) | `gmsa-deploy$` | group `Workstation-Admins` → stations only |
| Server (API deployment) | `gmsa-srvdeploy$` | local admin on the API server only |
| Console (the running app) | app server machine account | **nowhere** |

`gmsa-srvdeploy$` is deliberately **outside** the server-admins group; the membership is local, on that one
machine only. When a deploy task starts reporting `ERROR_LOGON_FAILURE (0x8007052E)`, it is not about rights —
it is a stale local copy of the gMSA password (`Install-ADServiceAccount`).

## Encrypted agent ↔ API comms (self-contained TLS)

At startup the API generates/persists its own self-signed cert (`SelfCert.cs`, **`MachineKeySet`** – works
under a gMSA; NOT EphemeralKeySet, with which Schannel will not perform a server handshake), Kestrel binds
`:5443`. No CA, no cert store. The agent verifies it by **thumbprint pinning** (`TlsClient.cs`,
`tls.pinnedThumbprint`) → encrypted and authenticated. The thumbprint comes from `GET /api/cert-info` / the API
log. Access to the API goes through the `USBGuardianClients` policy (membership in
`Authorization:AllowedGroups`).

## Requesting data on click (ReportNow)

A push model means the server has no back-channel to the agent. "Request data" therefore travels as a
**command piggy-backed on the heartbeat response** (the same channel as `WhitelistUpdateAvailable`):

```
Console (Stations) → AppSettings: cmd.report.<HOST> = time of the request (UTC)
Agent heartbeat (≤2 min) → HeartbeatController: ReportNow=true IF the request is newer than the PREVIOUS LastSeen
        ↓ (one-shot – the next heartbeat has LastSeen past the request time → ReportNow=false; the API only READS AppSettings)
Agent: the heartbeat confirmed online+version (LastSeen) + SyncSignals → IncidentSync flushes the queue at once
Console: "requested HH:mm" until the agent reports back (LastSeen ≥ request time)
```

Latency ≤ the heartbeat interval (~2 min). In bulk via "Request data from all" (only stations reporting an
agent). The key `cmd.report.<HOST>` in `AppSettings` doubles as the "last requested" audit record.

## Central settings and alerts (console)

The `AppSettings` table (key/value, migration 06) managed from Settings; `AccessCache` singleton:
- `policy.enforce` – enforce approved media only (distributed to the agent through the heartbeat).
- `comm.silentAfterMinutes` – the "silent agent" threshold (default 180); the boundary for both the
  communication dot and the tile on Stations.
- `deploy.*` – auto-enrollment (see below):
  `enabled`/`dryRun`/`defaultEnroll`/`intervalMinutes`/`maxPerRun`/`allowHosts`/`includeHosts`/`excludeHosts`/`targetsFile`/`lastRun`.
  **Default + exceptions model:** `defaultEnroll` (Settings) = the default for newly discovered PCs (deploy or
  not). Per-station exceptions are made **directly in Stations** (the "Deployment" column, plus bulk
  "Exclude/Include all"); they are stored as `includeHosts` (force ON) / `excludeHosts` (force OFF). Effective
  state = include ? ON : exclude ? OFF : `defaultEnroll`.
- `access.users` / `access.groups` – the console access whitelist (`appsettings` = a lockout-safe bootstrap).
- `email.*` – SMTP relay (M365 Direct Send) + `IncidentAlertService` (background notifier: a digest of new
  unapproved incidents, a baseline on the first run, interval/throttle; `EmailSender`).
- `retention.*` – how long incidents are kept; the deletion itself is done by the API.

## Agent auto-enrollment (the console deploys on its own)

```
AdSync → Computers (who has no agent)
AgentDeployService (24/7, default OFF + dry-run)
   ↓ live mode
deploy.targetsFile (the list of stations without an agent)   [console = machine account, write only]
   ↓ read by
Scheduled task on APP_SERVER under gMSA gmsa-deploy$              [least-privilege: admin on clients only]
   ↓ Deploy-AgentFleet.ps1
\\HOST\C$ robocopy + sc.exe \\HOST create + watchdog + start  → agent on the client (LocalSystem)
```

Least-privilege: the console **does not change identity** (SQL grants unchanged), the installation is performed
by a separate task under the deploy account. **Environment (AXIMA): PS scripts must be signed** (AllSigned GPO)
with the prod cert `CN=powershell.domena.loc` + the publisher in `LocalMachine\TrustedPublisher`; before
signing, CRLF + UTF-8 BOM. Setup: [auto-deploy-setup.en.md](auto-deploy-setup.en.md).

## Console – what the pages do

- **Overview** – a cross-page tile summary + filter (period/action/full-text) + aggregation (GroupBy over an
  anonymous type → in-memory map) + an "Approved" column per the active whitelist. The "Detailed" table has
  **sortable headers** (sorting in the DB via a query string, before `Take(200)`).
- **Stations** – AD inventory, filter, AD path (OU), communication icon (by the freshness of `LastSeen`),
  the "Silent agents" tile (reports an agent but `LastSeen` is older than the `comm.silentAfterMinutes`
  threshold – a possible outage or tampering), the "Request data" button (row/bulk) → ReportNow. The
  **"Deployment"** column on stations without an agent = include/exclude from auto-enrollment (an exception to
  `deploy.defaultEnroll`); in bulk via "Exclude/Include all".
- **Whitelist** – serial-only entry + VID/PID backfill from incidents + import + inline edit + the `IsActive`
  checkbox. Media **capacity** is pulled from incidents (max `SizeBytes` per serial, display-only – it is not
  kept on the whitelist).
- **Health checks** – checks of the server and the clients. The list of checks is shown **up front** and ticked
  off with running results (so it is visible that it works, not just that something spins); the delay between
  steps is deliberate. Results export to CSV / HTML / PDF (print) / TXT. It also covers the **scheduled
  restart** of services (server and client).
- **Activity** – the activity log (see above): filters (period, level, source, search), a **live** mode with a
  3 s refresh, CSV export.
- **Database** – a read-only overview of the DB content: row counts per table, the incident date range
  (a retention check), a dump of `AppSettings` and the last 20 incidents.
- **Documentation** – `.md` rendered (Markdig) as printable HTML, a hub, plus the graphical outputs: the
  "How it works" animation, a **mind map**, a **flowchart** and a **management summary (A4)**. All four are
  bilingual (a CS/EN toggle in the page header).

**Overview – capacity & export:** both the aggregated and the detailed listing show the media size. Two export
buttons (inheriting the active period/action/search filter):
- `GET /export/incidents.csv` – raw data (CSV, UTF-8 BOM + `;` → Excel), up to 50,000 rows.
- `GET /export/manager` – the **management report** (printable HTML → PDF, deliberately **1–2 A4 pages**):
  KPIs + **charts (inline SVG, no libraries):** incidents over time (a stacked bar per day/week), a donut of
  the action breakdown, horizontal bars of top users/stations; a table of unapproved media; a **Incident
  database** section (total count, unique media/stations, the data range for a retention check). The endpoints
  inherit the FallbackPolicy (auth).

## Data retention (NIS2)

Central settings in `AppSettings` (console → Settings → Data retention): `retention.enabled`,
`retention.incidentDays` (default 365), `retention.lastRun`. The deletion itself is done by the **API**
(`RetentionService`, a BackgroundService every 6 h) – it is the only component with delete rights on the DB
(`db_datawriter`). It deletes incidents older than the limit (`ExecuteDeleteAsync`) and writes `lastRun`.
The console only has write on `AppSettings` (no delete on `Incidents`), which is why enforcement lives in the API.

## Pending (roadmap)

| Item | Description |
|------|-------------|
| Per-serial blocklist | Banning a specific medium, near-real-time to the agents (takes precedence over the whitelist) |
| Console hardening | gMSA instead of LocalSystem; a dedicated `USB-Guardian-Admins`; HTTPS console; move the API to the app server |
| **Activity-log retention** | `sp_PurgeActivityLog` exists but **nothing calls it** – add `activity.retentionDays` to Settings and the call to the API (pattern: `RetentionService`) |
| ~~Local console on the fleet~~ | **Decided 2026-09-04: ON across the fleet, exclusively for a local admin of the station.** The template in the repo stays `false` (a safe default for other environments), the fleet package is built with `true`; the build warns about the opposite state |
| Toast privilege separation | A helper process in the user session – one-way pipes SYSTEM → user |

## Whitelist publishing/signing workflow (automatic, the client is a 1:1 copy of the server)

The agent only ever receives a **signed version**. The signature uses USB Guardian's **internal RSA key**
(its public part = `whitelist_public.pem` on the agents), not a company code-signing cert or CA. The server
keeps the **exact signed blob** in the DB (`WhitelistVersions.Json`, `NVARCHAR(MAX)`) plus the signature
(`Signature`, `NVARCHAR(MAX)`), and the API serves it **verbatim**. The client has no DB → it stores it as a
**JSON file** (`C:\ProgramData\USBGuardian\whitelist\whitelist.json` + `.sig`).

**Automatic server-side signing (`WhitelistPublisher`):** after **every catalog change** (add/remove/activate/
edit; and on a manual "Publish now") the console itself:

```
catalog change → snapshot of the active catalog → canonical whitelist.json blob (new version yyyy-MM-dd-vN,
        validity whitelist.validityDays default 365) → SIGNS it with the internal key (Whitelist:PrivateKeyPath)
        → stores Json+Signature, activates it (deactivating the old one)
API: GET /api/whitelist = the blob verbatim · GET /api/whitelist/signature = base64 signature
   ↓ the heartbeat announces the new version (≤2 min)
Agent: downloads blob+signature → SignatureVerifier verifies (fail-secure) → stores whitelist.json (+.sig)
        → WhitelistChecker indexes it (Dictionary VID:PID:SERIAL, O(1) – scale-safe even for 10k devices)
```

Byte-exact: the same blob string is **signed**, **served** (`/api/whitelist`) and **verified** (the agent) —
all UTF-8 without BOM (SHA-256 / Pkcs1), so the RSA signature matches. **Trade-off (deliberately chosen):**
the private key is on the app server (protected by ACL/DPAPI) in exchange for **full automation** (no manual
offline step). The offline `WhitelistSigner` remains as a tool for key generation / manual verification.

## Enforcement: server → agent + local break-glass (phases 2+3)

**Phase 2 – policy distribution:** `HeartbeatController` returns `Enforce` (from `AppSettings policy.enforce`,
the app server = source of truth). The agent (`WhitelistSync`) passes it into `PolicyState` on every heartbeat;
`PolicyEnforcer` then uses the **effective mode** (`PolicyState.EffectiveMode`) instead of a fixed local
`policy.mode`: server enforce=true → `block`, false → `warn`. Before the first heartbeat it falls back to the
local config.

**Auto-re-enable / reconciliation (blocking off = attach anything):** the agent remembers what **it itself**
disabled (`DeviceBlocker`, persisted in `blocked.json`: PnpDeviceID → key VID:PID:SN). After each cycle
`WhitelistSync` **reconciles**: blocking off (break-glass / `enforce=false`) → it returns
(`Enable-PnpDevice`) **everything** it disabled; blocking on → it returns media that are **now on the
whitelist** (approved); otherwise it leaves them blocked. **Local break-glass returns media IMMEDIATELY**
(synchronously from the console on 5080, it does not wait for a cycle); a server-side `enforce=false`
propagates with the next heartbeat (≤ the interval). Only disks disabled by the agent are returned (not ones
disabled manually elsewhere).

**A previously blocked medium appears on the whitelist:** approval happens in the console → a new signed
version → the agent downloads it (≤ heartbeat) and **invalidates the 5-minute cache**
(`WhitelistChecker.Reload()` from `WhitelistSync` right after the download – otherwise a newly approved medium
would only be recognised once the cache expired). `ReconcileBlocked` in the same cycle finds `IsAllowedKey` =
true and **returns the medium even while blocking is on**. (The blocking key `VID:PID:SN` is the whitelist
index key, `OrdinalIgnoreCase`, with the serial trimmed on both sides.)

**Re-blocking attached media (symmetry with auto-re-enable):** the agent only blocks on a **new** connection
(a WMI event). If a medium came back through break-glass and blocking is then **switched back on** (the
override is cleared / `enforce=true`), the medium stays attached and would not re-block itself.
`DeviceMonitor.ReEnforceConnectedDevices()` catches up: it walks the attached USB/SD media and **re-blocks**
the unapproved ones that are not blocked yet (idempotent – approved and already-blocked ones are skipped). It
is called on **every reconcile cycle while blocking is ON** (self-healing) and **immediately** on "Switch
blocking back on" in the local console.

**Reliability of returning (`UnblockDevice`):** the Enable is first attempted as an **exact match** on
`Get-PnpDevice -InstanceId` (like a manual `Enable-PnpDevice -InstanceId '…'`), then via a `-like` fallback.
Outcomes: `ENABLED` (allowed → remove from the list), `GONE` (the medium is no longer attached → treated as
resolved and removed so it does not hang around; the next connection is evaluated afresh), `FAILED` (a real
failure → log it and keep it, the next reconcile tries again). The local console shows the **number of
currently blocked media** and a **"Return all media now"** button (`POST /api/unblock-all`).

**Phase 3 – local break-glass (offline):** a local **admin** of the station can, in the local console
(`127.0.0.1:5080`, admins only, loopback), **switch blocking off temporarily** (`POST /api/override?hours=N`,
capped at 72 h) — to be able to work when the station cannot reach the server. The override is **persisted**
(`C:\ProgramData\USBGuardian\override.json` → it survives a restart), **logged** as an audit incident
(`Action=OverrideDisabled`, who / for how long) and reported to the server. **On the next successful contact
with the server the override is CLEARED** (a successful heartbeat → `PolicyState.OnServerHeartbeat` → the
server re-asserts the policy). Effective mode:
`override active ? warn : (server answer received ? enforce : local default)`.

**Blocking latency + notification:** the agent enforces **immediately on the `Win32_DiskDrive` connect** (it
does not wait for a drive letter to be paired → minimising the window in which the medium can be mounted) and
right after writing the toast it **triggers the ToastHelper task** (`schtasks /Run`), so the "this medium was
not approved" message appears within a few seconds. **A limit (a user-mode agent is reactive):** Windows mounts
removable storage very quickly, so the brief moment before `Disable-PnpDevice` cannot be fully eliminated. For
**guaranteed prevention** (the medium never even appears in Explorer) one has to add Windows **Device
Installation Restrictions / Removable Storage Access GPO** or a kernel storage filter driver – roadmap.

## Watchdog – Task Scheduler

```
Task Scheduler (\USBGuardian\USBGuardian-Watchdog)
    ↓  every 3 minutes + at system start
Check: is the "USB Guardian" service running?
    ↓ NO
Start-Service + Event Log ID 200 (Warning)
    ↓ failure
Event Log ID 500 (Error) – IT intervention required
```

- Runs as **SYSTEM** – independent of the logged-on user
- An attacker has to stop **both the service and the scheduled task** – more steps, more traces
- Registration: `scripts\Register-Watchdog.ps1` (auto-elevates UAC)
