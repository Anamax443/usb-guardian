# HANDOFF – USB Guardian project

*🇬🇧 English · [🇨🇿 Čeština](HANDOFF.md)*

**Date:** 2026-06-18 · **Repo:** `Anamax443/usb-guardian` · **Author:** Milan Trnka (AXIMA)

Document for whoever takes over the project. Architecture: [docs/architecture.md](docs/architecture.md),
functional description: [README.en.md](README.en.md).

## 1. What it is

Monitoring of storage media on company stations (NIS2). The agent on a station detects connected
USB/SD/disk, compares it against a signed whitelist and warns / blocks; it pushes incidents to the
API. The server console aggregates the data, keeps a station inventory from AD and shows where the
agent is missing.

## 2. Current Live State

| | |
|---|---|
| **Domain** | `axinetwork.loc` |
| **DB** | SQL Server `B-S-W-SQL-04`, database `USBGuardian` (scripts `database/01–05` applied) |
| **API** | runs on `B-S-W-SQL-04`, `:5050` (HTTP) / `:5443` (HTTPS), Windows service, gMSA `AXINETWORK\gmsa-SQL$` |
| **Admin console** | **live** `http://10.8.2.213:4200/` (`B-S-W-MIKOS`), Windows service `USBGuardianConsole`, runtime `C:\Apps\USBGuardianConsole`, self-contained |
| **Console account** | currently **LocalSystem** = `AXINETWORK\B-S-W-MIKOS$` (least-priv SQL grant: read all + write Computers/Whitelist*) |
| **Console authorization** | AD group `AXINETWORK\SQL Admins2` + whitelist `AXINETWORK\trnkam` |
| **AD sync** | enabled, 60 min interval + on demand; live **211 stations in AD, 210 without agent** |
| **Live commit** | `a13f62d` (shown in the console footer) |
| **Agent (test)** | station `.181` (TRNKAMW11); first rollout target `TRNKAMW11N` (dynamic IP) |

## 3. Key decisions (why)

- **Push, not pull** – 500+ clients behind NAT/firewall; the agent only needs an outbound connection.
- **Two-tier** – logic (console, AD sync) on the app server `.213`, DB is storage only on SQL-04.
  (Note: the API still runs on SQL-04; moving it to .213 is planned hardening.)
- **Console = .NET/Blazor**, not Node – reuses the EF models from the API (linked `DbModels`/`AppDbContext`),
  one language, ASP.NET Core is already on the server.
- **Agent local console via `HttpListener`**, not Kestrel – the agent needs no ASP.NET Core runtime.
- **Keyed by hostname, not IP** – stations have dynamic IPs.
- **The whitelist RSA private key never on the server** – publishing a signed version is an offline step (NIS2).
- **Portability** – no company values in code; everything in `*.local.json`, domain from `new DirectoryEntry()`.

## 4. Console deploy (manual, from TRNKAMW11)

trnkam has admin on `.213`; WinRM was closed → deploy via **SMB + remote `sc.exe`** (ports 135/445):

```powershell
dotnet publish server\USBGuardian.Admin -c Release -r win-x64 --self-contained -o D:\deploy\USBGuardianConsole
sc.exe \\10.8.2.213 stop USBGuardianConsole
robocopy D:\deploy\USBGuardianConsole \\10.8.2.213\C$\Apps\USBGuardianConsole /E /XF appsettings.local.json
sc.exe \\10.8.2.213 start USBGuardianConsole
```

Firewall `:4200` created via DCOM/CIM. Server configuration:
`C:\Apps\USBGuardianConsole\appsettings.local.json` (see `*.example`).

## 5. Next steps / pending

- **Remote agent install** onto the 210 stations without it (WinRM – `Enable-PSRemoting` on clients;
  first prototype the channel against `TRNKAMW11N`). No stored admin creds, audited, just-in-time.
- **Web whitelist management** + signing workflow (staging → offline signing → publish).
- **Hardening:** gMSA instead of LocalSystem, dedicated `USB-Guardian-Admins` group instead of `SQL Admins2`,
  HTTPS for the console, move the API off SQL-04 onto .213.
- **Cleanup:** unused `Microsoft.Data.Sqlite` in the agent (persistence is JSON); stray folder
  `server/USBGuardianAPI/`; broken GUID format `:N[..8]` in `NotificationService.ShowWarning`.

## 6. Documentation map

| File | Content |
|--------|-------|
| `README.md` / `.en.md` | Functional overview, components, configuration, deployment |
| `HANDOFF.md` / `.en.md` | This document – handoff + live state |
| `docs/architecture.md` | Technical architecture, data flow, security layers |
