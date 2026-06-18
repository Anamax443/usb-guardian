# HANDOFF – USB Guardian project

*🇬🇧 English · [🇨🇿 Čeština](HANDOFF.md)*

**Date:** 2026-06-18 · **Repo:** `Anamax443/usb-guardian` · **Author:** Milan Trnka (AXIMA)

Document for whoever takes over. Architecture: [docs/architecture.md](docs/architecture.md),
functional description: [README.en.md](README.en.md).

## 1. What it is

Monitoring of storage media on company stations (NIS2). The agent detects connected USB/SD/disk,
compares against a signed whitelist and warns / blocks; it pushes incidents to the API. The server
console aggregates data, keeps a station inventory from AD and shows where the agent is missing.

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
| **Live commit (console)** | `4e3ef32` (console footer; verify via `/api/version`) |
| **Agent (test)** | `.181` (TRNKAMW11) – `syncUrl=https://B-S-W-SQL-04:5443` + pin; 1st rollout target `TRNKAMW11N` (dyn. IP) |

## 3. Key decisions (why)

- **Push, not pull** – 500+ clients behind NAT/firewall; agent only needs outbound.
- **Two-tier** – logic (console, AD sync) on app server `.213`, DB is storage on SQL-04.
  (Note: the API still runs on SQL-04; moving it to .213 is planned hardening.)
- **Console = .NET/Blazor**, not Node – reuses EF models from the API (linked `DbModels`/`AppDbContext`).
- **Agent local console via `HttpListener`** – no ASP.NET Core runtime needed.
- **Keyed by hostname, not IP** – stations have dynamic IPs.
- **Whitelist RSA private key never on the server** – publishing a signed version is an offline step (NIS2).
- **Encryption without a CA** – API generates its own self-signed cert (`MachineKeySet`, NOT EphemeralKeySet!),
  agent verifies it via **thumbprint pinning**. Independent of the company CA / external certs.
- **Central settings in DB** (`AppSettings`) – enforcement, access, e-mail; the agent still uses its local
  `policy.mode` (distribution via heartbeat is a next step).
- **Portability** – no company values in code; everything in `*.local.json`, domain from `new DirectoryEntry()`.

> Fixed latent repo bugs: missing authorization policy `USBGuardianClients` (controllers returned 500);
> `EphemeralKeySet → MachineKeySet` (Schannel can't do the server TLS handshake otherwise).

## 4. Console deploy (manual, from TRNKAMW11)

trnkam has admin on `.213`; WinRM was closed → deploy via **SMB + remote `sc.exe`** (ports 135/445):

```powershell
dotnet publish server\USBGuardian.Admin -c Release -r win-x64 --self-contained -o D:\deploy\USBGuardianConsole
sc.exe \\10.8.2.213 stop USBGuardianConsole
robocopy D:\deploy\USBGuardianConsole \\10.8.2.213\C$\Apps\USBGuardianConsole /E /XF appsettings.local.json
sc.exe \\10.8.2.213 start USBGuardianConsole
```

The API deploy runs on SQL-04 (trnkam has no admin there → operator), self-contained build + restart.

## 5. Next steps / pending

- **Close HTTP 5050** on SQL-04 (HTTPS only) – NIS2.
- **Distribution + remote agent install** onto ~210 stations. Agent config = `syncUrl https://…:5443`
  + `tls.pinnedThumbprint`. Remote install via WinRM (`Enable-PSRemoting` on clients), no stored admin
  creds, audited, just-in-time; first prototype the channel against `TRNKAMW11N`.
- **Signing/publishing workflow** for the whitelist (staging → offline signing → publish) → unlocks the
  whitelist, **enforcement** and **blocklist** live to agents (need signed distribution + heartbeat propagation).
- **Per-serial blocklist** (ban a specific device, near-real-time to agents – takes precedence over whitelist).
- **E-mail alerts**: config + sending done (`IncidentAlertService`); fire once new incidents arrive.
- **Hardening:** gMSA instead of LocalSystem for the console, dedicated `USB-Guardian-Admins` instead of
  `SQL Admins2`, HTTPS for the console, move the API off SQL-04 onto .213.
- **Cleanup:** unused `Microsoft.Data.Sqlite` in the agent; stray `server/USBGuardianAPI/`;
  broken GUID format `:N[..8]` in `NotificationService.ShowWarning`.

## 6. Documentation map

| File | Content |
|--------|-------|
| `README.md` / `.en.md` | Functional overview, components, configuration, deployment |
| `HANDOFF.md` / `.en.md` | This document – handoff + live state |
| `docs/architecture.md` | Technical architecture, data flow, security layers |
