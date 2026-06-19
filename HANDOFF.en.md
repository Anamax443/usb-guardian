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
| **Domain** | `axinetwork.loc` |
| **DB** | SQL Server `B-S-W-SQL-04` (= `10.8.2.225`), database `USBGuardian`, scripts `database/01–06` applied |
| **API** | `B-S-W-SQL-04`, Windows service "USB Guardian API", install `C:\USBGuardian.Api`, gMSA `AXINETWORK\gmsa-SQL$`; **HTTPS `:5443`** (self-signed, **PIN `E6F6B4FCE0BB627F564E85D6509DE7C4B82CF2F0`**) + HTTP `:5050`. **Live version via `GET /api/version`** |
| **Version/commit (check)** | console footer + `:4200/api/version`; API `:5050/api/version`; agent reports commit → console "Agent version". All stamped by `git rev-parse` (MSBuild) |
| **Admin console** | **live** `http://10.8.2.213:4200/` (`B-S-W-MIKOS`), service `USBGuardianConsole`, `C:\Apps\USBGuardianConsole`, self-contained |
| **Console account** | **LocalSystem** = `AXINETWORK\B-S-W-MIKOS$` (SQL grant: read all + write Computers/WhitelistDevices/WhitelistVersions/AppSettings) |
| **Console authorization** | AD `AXINETWORK\SQL Admins2` + whitelist `AXINETWORK\trnkam` (+ DB list from Settings) |
| **Agent↔API encryption** | HTTPS + **thumbprint pinning** (no CA) — verified end-to-end (heartbeat OK from .181) |
| **AD sync** | enabled 60 min + on-demand; **211 in AD, ~210 without agent** |
| **Live commit (console)** | `5940eb6` (footer / `/api/version`) · **API live `19e4018`** |
| **Console – pages** | Overview (filter+aggregation+sortable "Detailed"), Stations (AD inventory + "Agents gone silent" tile + "Request data"), Whitelist, Settings (enforcement/access/email/alerts/communication monitoring/**auto-enrollment**), Documentation |
| **Deploy account (auto-enroll)** | **gMSA `AXINETWORK\gmsa-USBGdep$`** – in `PC Admins` (admin on clients) **and local admin on SQL-04** (API deploy); installed on `.213`; deploy task `USBGuardian-AutoDeploy` (under gMSA, via CIM) |
| **Agent (test) .181** | **PILOT SUCCESSFUL** – auto-installed via gMSA (no creds), service "USB Guardian" RUNNING, heartbeat + **incidents flowing into DB** (37). Remaining: watchdog task + user attribution (see 5.5) |

## 3. Key decisions (why)

- **Push, not pull** – 500+ clients behind NAT/firewall; the agent only needs an outbound connection.
- **Two-tier** – operations (console, AD sync) on app server `.213`, DB is just storage on SQL-04.
  (Note: the API still runs on SQL-04; moving it to .213 is planned hardening.)
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

## 4. Console deploy (manual, from TRNKAMW11)

trnkam has admin on `.213`; WinRM was closed → deploy via **SMB + remote `sc.exe`** (ports 135/445):

```powershell
dotnet publish server\USBGuardian.Admin -c Release -r win-x64 --self-contained -o D:\deploy\USBGuardianConsole
sc.exe \\10.8.2.213 stop USBGuardianConsole
robocopy D:\deploy\USBGuardianConsole \\10.8.2.213\C$\Apps\USBGuardianConsole /E /XF appsettings.local.json
sc.exe \\10.8.2.213 start USBGuardianConsole
```

Firewall `:4200` was created via DCOM/CIM. Configuration on the server:
`C:\Apps\USBGuardianConsole\appsettings.local.json` (see `*.example`).

## 5. Status and next steps

### 5.1 Done and live
- **DB / incidents = 100 %** — agent → API → DB → console, the whole path runs (Overview shows incidents from .181).
  **Key fix:** the API had an unfinished queue refactor — `IncidentsController` required `IncidentQueue`, but
  `Program.cs` **did not register it in DI** → **500 on every `/api/incidents`** (heartbeat ran without that dependency).
  After `AddSingleton<IncidentQueue>` + `AddHostedService<IncidentQueueWorker>` the controller returns 202 + the worker writes.
- **Version/commit on all components** — the console + API have `GET /api/version`, the agent reports the real commit
  (`AppInfo` + MSBuild `git rev-parse` stamp) → in the console "Agent version" shows the deployed commit per station.
- **Console:** "Agents gone silent" tile (threshold `comm.silentAfterMinutes`), "Request data" (`ReportNow` via
  `AppSettings cmd.report.<HOST>`), sortable "Detailed" table, auto-enrollment orchestrator (default OFF + dry-run).
- **Serial trim fix** — WMI returns the serial with spaces (`"WX92D622N4PE    "`) → didn't match the whitelist
  ("Approved=no" + the agent didn't recognize it as whitelisted). The agent trims at WMI parse, the console in `Approved`.

### 5.2 Deployed components
- **API on SQL-04 (live `19e4018`):** `ReportNow` in the heartbeat, queue DI fix, `/api/version`. Deploy via
  **gMSA** (build staged on `.213` `C:\Apps\USBGuardianApiPublish` → gMSA has local admin on SQL-04). Caution on
  redeploy: **wait for `STOPPED`** (otherwise `USBGuardian.Api.exe` is locked → robocopy `FAILED` → the old version keeps running).
- **Agent on .181 (auto-installed):** whitelist poll 2 min, startup scan, `ReportNow`, serial trim,
  real version. Fixes: `onExpiredWhitelist`, publicKeyPath relative to exe, GUID, removed Sqlite.

### 5.3 Agent auto-enrollment — PILOT SUCCESSFUL (.181), expand to fleet
Goal: the console, running 24/7 after AD sync, deploys the agent itself onto stations without an agent. **Least-privilege:** the console writes the target list
(`deploy.targetsFile`), the installation is done by a **scheduled task on .213 under gMSA** (only that account has admin on the clients).
- **Works end-to-end:** gMSA `gmsa-USBGdep$` (in `PC Admins` = admin on clients, no password), task `USBGuardian-AutoDeploy`,
  `Deploy-AgentFleet.ps1` (runspace pool PS5.1, `sc.exe \\HOST create` via cmd). **.181 installed without any creds**,
  the service runs, heartbeat + incidents flow. Scripts: `New-DeployGmsa.ps1`, `Install-Agent.ps1`/`Uninstall-Agent.ps1`,
  Detail: [docs/auto-deploy-setup.md](docs/auto-deploy-setup.md).
- **Remaining on .181:** **watchdog task** (PS-free `sc start` schtasks – single-line command for the client, see git history).
- **Expand to fleet:** GPO publisher trust on clients (5.4), enable in Settings (dry-run → live), `.181 → .180 → fleet`.

### 5.4 Environment for PS scripts (IMPORTANT – AXIMA gotchas)
- **AllSigned (GPO):** every PS script that runs there **must be signed** with the prod cert `CN=powershell.axinetwork.loc`
  (`-ExecutionPolicy Bypass` does NOT bypass this). Signing via the `.213:4100` service / share `\\herkules\ITC\UTIL\04-manualy-instalace\PS-scripty`.
  Applies to `Deploy-AgentFleet.ps1` (on .213) and `Watch-USBGuardian.ps1` (on clients).
- **Before signing CRLF + UTF-8 BOM** (the repo has LF → otherwise `HashMismatch`).
- **Trusted Publisher:** for non-interactive runs (gMSA/SYSTEM) the signing cert must be in `LocalMachine\TrustedPublisher`
  on .213 and clients (added on .181+.213; **fleet via GPO** – cert export `_AXIMA-CodeSign-publisher.cer` on the share).

### 5.5 Roadmap (pending)
- **User attribution** — incidents report `TRNKAMW11$` (machine account), because the agent runs as SYSTEM
  (`Environment.UserName`). Add detection of the active console session (WTS API: `WTSGetActiveConsoleSessionId`
  + `WTSQuerySessionInformation`) → the real logged-in user. Fits into "Toast Privilege Separation".
- **Whitelist signing/publishing workflow** — changes in the catalog only reach the agents **after a signed
  version is released** (the private key is never on the server). Without it the agent keeps warning even on an approved medium (Signature status = unsigned).
  It also unlocks enforcement + the **blocklist** "for real".
- **Monitoring of signing cert expiry** – `CN=powershell.axinetwork.loc` valid until 2028-06-17; alert via e-mail from the console.
- **"Everything on the server .213":** move the API runtime from SQL-04 to .213 (console+API on .213, DB on SQL-04, agent repoint to
  `https://10.8.2.213:5443`) → .181 really not needed. **Build/deploy artifacts are on D:\deploy (locally), not on .181.**
- **Close HTTP 5050** on SQL-04 (HTTPS only) – NIS2.
- **Per-serial blocklist** + **blocking already-connected media** (the startup scan is half the way there).
- **Hardening:** dedicated `USB-Guardian-Admins` instead of `SQL Admins2`, HTTPS console.
- **Cleanup:** stray (untracked) `server/USBGuardianAPI/` (to be deleted).

> **Note on automation (NOT bypassable by me):** the security classifier auto-denies me actions on prod
> SQL-04 as well as **changes to my own permissions** (update-config) → prod deploys and permission rules must be run/allowed by the
> user (bypass mode or a manual rule). That's why the API deploy on SQL-04 is done by the user with ready-made PS blocks (I prepare
> the build on `.213`).

## 6. Documentation map

| File | Content |
|--------|-------|
| `README.md` / `.en.md` | Functional overview, components, configuration, deployment |
| `HANDOFF.md` / `.en.md` | This document – handoff + live state |
| `docs/architecture.md` | Technical architecture, data flow, security layers |
| `docs/auto-deploy-setup.md` | Setup of the deploy gMSA + GPO + scheduled task for auto-enrollment |
