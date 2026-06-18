# HANDOFF – USB Guardian project

*🇬🇧 English · [🇨🇿 Čeština](HANDOFF.md)*

**Date:** 2026-06-18 · **Repo:** `Anamax443/usb-guardian` · **Author:** Milan Trnka (AXIMA)

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
| **API** | `B-S-W-SQL-04`, Windows service, gMSA `AXINETWORK\gmsa-SQL$`; **HTTPS `:5443`** (self-signed cert, **PIN `E6F6B4FCE0BB627F564E85D6509DE7C4B82CF2F0`**) + HTTP `:5050` (NIS2: close) |
| **Admin console** | **live** `http://10.8.2.213:4200/` (`B-S-W-MIKOS`), service `USBGuardianConsole`, `C:\Apps\USBGuardianConsole`, self-contained |
| **Console account** | **LocalSystem** = `AXINETWORK\B-S-W-MIKOS$` (SQL grant: read all + write Computers/WhitelistDevices/WhitelistVersions/AppSettings) |
| **Console authorization** | AD `AXINETWORK\SQL Admins2` + whitelist `AXINETWORK\trnkam` (+ DB list from Settings) |
| **Agent↔API encryption** | HTTPS + **thumbprint pinning** (no CA) — verified end-to-end (heartbeat OK from .181) |
| **AD sync** | enabled 60 min + on-demand; **211 in AD, ~210 without agent** |
| **Live commit (console)** | see console footer / `/api/version` (after the last doc sweep) |
| **Console – pages** | Overview (filter+aggregation+sortable "Detailed"), Stations (AD inventory + "Agents gone silent" tile + "Request data"), Whitelist, Settings (enforcement/access/email/alerts/communication monitoring/**auto-enrollment**), Documentation |
| **Deploy account (auto-enroll)** | **gMSA `AXINETWORK\gmsa-USBGdep$`** – in `PC Admins` (admin on clients), installed on `.213`; scheduled task `\USBGuardian\USBGuardian-Watchdog`… deploy task `USBGuardian-AutoDeploy` on `.213` |
| **Agent (test)** | `.181` (TRNKAMW11) – `syncUrl=https://B-S-W-SQL-04:5443` + pin; **auto-deploy pilot is running** (copies files, sc.exe create being fine-tuned) |

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

### 5.1 Done and live on the console (.213)
- **"Agents gone silent" tile** on Stations + threshold `comm.silentAfterMinutes` (Settings → Communication monitoring).
  Reveals stations that previously reported an agent but whose `LastSeen` is older than the threshold (outage/tamper).
- **"Request data" on click** (Stations, per-row/bulk) – `ReportNow` command via `AppSettings` `cmd.report.<HOST>`.
- **Overview → "Detailed" table with sortable headers** (sorting in the DB via query-string).
- **Auto-enrollment orchestrator** `AgentDeployService` + Settings "Agent auto-enrollment" (default OFF + dry-run).

### 5.2 In repo, awaiting rollout / operator
- **API (SQL-04, operator):** `HeartbeatController` returns `ReportNow` (once, based on the previous `LastSeen`).
  + fix `DateTimeStyles` (otherwise heartbeat 500). Without an API deploy, "Request data" only writes a flag, the agent won't get it.
- **Agent (rollout):** **whitelist poll 15 → 2 min**; **startup scan of already-connected media** (WMI watchers caught
  only new connections); `ReportNow` handling (flush); fixes: `onExpiredWhitelist` (block/allow/warn), publicKeyPath
  relative to exe (otherwise the whitelist is rejected when running as a service), GUID `:N[..8]`, removed unused `Microsoft.Data.Sqlite`.

### 5.3 Agent auto-enrollment (console .213 deploys it itself) — IN PROGRESS, pilot
Goal: the console, running 24/7 after AD sync, deploys the agent itself onto stations without an agent. **Least-privilege:** the console only writes
the target list (`deploy.targetsFile`), the installation is done by a **scheduled task on .213 under gMSA** (only that account has admin on the PCs).
- **Done:** gMSA `gmsa-USBGdep$` (in `PC Admins`, on .213), task `USBGuardian-AutoDeploy`, .213 provisioned
  (agent publish `C:\Apps\USBGuardianAgentPublish` + scripts), `Deploy-AgentFleet.ps1` (runspace pool = PS5.1 compat),
  `scripts\New-DeployGmsa.ps1`, `Install-Agent.ps1`/`Uninstall-Agent.ps1`. Detail: [docs/auto-deploy-setup.md](docs/auto-deploy-setup.md).
- **Pilot .181:** file robocopy **works** (gMSA admin via `PC Admins`); service creation via CIM/DCOM failed
  → switched to **`sc.exe \\HOST create`** (via cmd because of quoting). **Script must be re-signed** (see 5.4) and finished.
- **Remaining for "live" auto-enrollment:** enable in Settings (dry-run → verify → turn off); expand from .181 to .180 → fleet.

### 5.4 Environment for PS scripts (IMPORTANT – AXIMA gotchas)
- **AllSigned (GPO):** every PS script that runs there **must be signed** with the prod cert `CN=powershell.axinetwork.loc`
  (`-ExecutionPolicy Bypass` does NOT bypass this). Signing via the `.213:4100` service / share `\\herkules\ITC\UTIL\04-manualy-instalace\PS-scripty`.
  Applies to `Deploy-AgentFleet.ps1` (on .213) and `Watch-USBGuardian.ps1` (on clients).
- **Before signing CRLF + UTF-8 BOM** (the repo has LF → otherwise `HashMismatch`).
- **Trusted Publisher:** for non-interactive runs (gMSA/SYSTEM) the signing cert must be in `LocalMachine\TrustedPublisher`
  on .213 and clients (added on .181+.213; **fleet via GPO** – cert export `_AXIMA-CodeSign-publisher.cer` on the share).

### 5.5 Roadmap (pending)
- **Monitoring of signing cert expiry** (user wants it) – cert valid until 2028-06-17; alert via e-mail from the console.
- **Close HTTP 5050** on SQL-04 (HTTPS only) – NIS2.
- **Whitelist signing/publishing workflow** → unlocks enforcement and the **blocklist** "for real".
- **Per-serial blocklist** + **blocking already-connected media** (the startup scan is half the way there).
- **Hardening:** dedicated `USB-Guardian-Admins` instead of `SQL Admins2`, HTTPS console, move the API off SQL-04 onto .213.
- **Cleanup:** stray (untracked) folder `server/USBGuardianAPI/` remains (duplicate – to be deleted).
  Done: unused `Microsoft.Data.Sqlite`, GUID `:N[..8]`.

## 6. Documentation map

| File | Content |
|--------|-------|
| `README.md` / `.en.md` | Functional overview, components, configuration, deployment |
| `HANDOFF.md` / `.en.md` | This document – handoff + live state |
| `docs/architecture.md` | Technical architecture, data flow, security layers |
| `docs/auto-deploy-setup.md` | Setup of the deploy gMSA + GPO + scheduled task for auto-enrollment |
