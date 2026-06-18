# USB Guardian – Architektura

## Přehled systému

```
┌─────────────────────────────────────────────────────────────────────┐
│  Klientský PC (Windows 10/11)                                       │
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
│  Server (B-S-W-SQL-04 nebo dedikovaný Windows Server)               │
│                                                                     │
│  ┌──────────────────────────────────┐   ┌────────────────────────┐ │
│  │  USB Guardian API                │   │  SQL Server            │ │
│  │  ASP.NET Core – port 5443/5050   │   │  Database: USBGuardian │ │
│  │                                  │   │                        │ │
│  │  /api/whitelist  (GET)           │◄─►│  Incidents             │ │
│  │  /api/incidents  (POST/GET)      │   │  WhitelistVersions     │ │
│  │  /api/heartbeat  (GET)           │   │                        │ │
│  │                                  │   │  gMSA: gmsa-SQL$       │ │
│  │  Windows Auth (Kerberos)         │   │  AD: USB-Guardian-     │ │
│  │  AD skupiny: USB-Guardian-Clients│   │       Clients          │ │
│  └──────────────────────────────────┘   └────────────────────────┘ │
└─────────────────────────────────────────────────────────────────────┘
```

## Komponenty agenta

| Komponenta | Popis |
|-----------|-------|
| `DeviceMonitor` | WMI subscriber – Win32_DiskDrive connect/disconnect eventy |
| `WhitelistChecker` | Čte lokální `whitelist.json`, ověřuje RSA-4096 podpis |
| `PolicyEnforcer` | Rozhoduje o akci dle `policy.mode` (warn / block) |
| `NotificationService` | Windows Toast notifikace pro přihlášeného uživatele |
| `IncidentLogger` | Ukládá incidenty do JSON front (`queue/`) |
| `DeviceBlocker` | Blokuje médium přes DeviceIoControl (IOCTL_STORAGE_EJECT_MEDIA) |
| `WhitelistSync` | Pravidelně stahuje whitelist ze serveru (interval: 15 min) |
| `IncidentSync` | Odesílá frontu incidentů na server (interval: 1 min, s jitter) |
| `SignatureVerifier` | Ověřuje RSA-4096 podpis whitelistu – fail-secure |

## Komponenty serveru

| Komponenta | Popis |
|-----------|-------|
| `IncidentsController` | POST příjem incidentů od agentů, GET pro Admin UI |
| `WhitelistController` | GET aktuální whitelist + verze + podpis |
| `HeartbeatController` | GET zdravotní stav serveru |
| `IncidentQueueWorker` | Background worker – zpracovává příchozí incidenty do DB |
| `AppDbContext` | EF Core kontext – SQL Server přes gMSA Windows Auth |

## Serverová admin konzole (USBGuardian.Admin)

Samostatná **Blazor Server** aplikace na app serveru (`10.8.2.213`), Windows služba
`USBGuardianConsole`, port `:4200`. Oddělená od ingestion API (odolnost – příjem incidentů
od 500+ agentů nesmí ovlivnit adminní použití). Čte/píše SQL-04, modely reusnuté z API
(slinkované `DbModels.cs` + `AppDbContext.cs` – žádná duplikace).

| Komponenta | Popis |
|-----------|-------|
| `Home` (Přehled) | Incidenty za 30 dní + poslední události vč. VID/PID/sériové číslo |
| `Computers` (Stanice) | Inventář z AD; dlaždice = filtr; cesta v AD (OU); tlačítko Aktualizovat z AD |
| `Settings` / `Docs` | Efektivní konfigurace (read-only) / nápověda v prohlížeči |
| `AdSyncRunner` | Logika AD syncu – volatelná z časovače i z UI (semafor proti souběhu) |
| `AdSyncService` | Časovač nad `AdSyncRunner` (interval z configu) |
| `AppInfo` | Commit hash buildu (MSBuild stamp z gitu) → patička |

**Autorizace:** Windows Auth (Negotiate). Přístup jen členům `Authorization:AdminGroups`
(AD skupina) **nebo** účtům v `Authorization:AllowedUsers` (whitelist). Kontrola přes
`WindowsPrincipal.IsInRole` (řeší doménové skupiny). `DevAllowAll` = bypass jen pro vývoj.

### AD sync

```
Active Directory (objectCategory=computer, ne disabled)
        ↓  (new DirectoryEntry() – ambient doména, nic natvrdo)
AdSyncRunner: name → Hostname, dNSHostName → Domain, operatingSystem, distinguishedName → AdPath (OU)
        ↓  upsert (klíč = hostname), NEpřepisuje LastSeen/AgentVersion (vlastní agent/API)
SQL Computers + reconciliation: InActiveDirectory; "v AD ⨯ hlásí agenta" = kam chybí agent
```

## Lokální admin konzole agenta

`LocalConsoleService` – `HttpListener` na `127.0.0.1` (volitelné, default vypnuto). Admin-only
(`WindowsPrincipal.IsInRole(Administrator)`), read-only. Živý in-memory stav agenta (whitelist,
WMI watchdog, fronta, připojená média). `HttpListener` schválně místo Kestrelu – agent
(`Sdk.Worker`) nepotřebuje ASP.NET Core runtime; loopback → plain HTTP akceptovatelné.

## Identifikace zařízení

```
VID:PID:SERIAL  →  klíč pro porovnání (uppercase)
Např: KINGSTON:DATATRAVELER_3.0:4E0788D05AC9
```

Whitelist záznam obsahuje: `vendorId`, `productId`, `serialNumber`, `description`, `approvedAt`, `approvedBy`

## Bezpečnostní vrstvy

| Vrstva | Mechanismus |
|--------|-------------|
| Transport | TLS 1.2+ (Kestrel), agent validuje certifikát serveru |
| Autentizace | Windows Auth – Kerberos Negotiate |
| Autorizace | AD skupiny – `USB-Guardian-Clients` |
| Integrita dat | RSA-4096 podpis whitelistu |
| Service účet | gMSA `AXINETWORK\gmsa-SQL$` – bez hesla v konfiguraci |
| Konfigurace | `appsettings.local.json` gitignored – citlivé hodnoty mimo repo |

## Konfigurace – klíčové hodnoty

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
    "validateServerCertificate": true   // false pouze pro vývoj
  },
  "signing": {
    "enabled": true   // false pouze pro vývoj
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

## Logování

Oba procesy používají vlastní `RoleTagFormatter` (konzolový formatter):

```
HH:mm:ss [KLIENT] info: USBGuardian.DeviceMonitor[0]
HH:mm:ss [SERVER] info: USBGuardian.Api.IncidentController[0]
```

- **Agent** → `[KLIENT]`
- **Server** → `[SERVER]`
- Produkce: agent loguje do Windows Event Log, server do Event Log i konzole

## Datový tok – incident

```
1. USB připojeno → WMI event
2. Agent identifikuje VID:PID:Serial
3. WhitelistChecker: médium NENÍ na whitelistu
4. PolicyEnforcer: mode=warn
5. NotificationService: Toast uživateli
6. IncidentLogger: uložit do queue/log_MACHINE_DATE.json
7. IncidentSync (1 min): odeslat na server /api/incidents
8. Server: uložit do SQL tabulky Incidents
```

## Deployment

### Vývojové prostředí

```
dotnet run -- --console    (agent)
dotnet run                 (server)
```

### Produkce

- Agent: Windows Service, spouštěn pod SYSTEM
- Server: Windows Service, spouštěn pod gMSA
- HTTPS certifikát: `scripts\New-Certificate.ps1` na produkčním serveru
- AD skupiny: `USB-Guardian-Clients` – stroje s nasazeným agentem

## Pending (roadmap)

| Položka | Popis |
|---------|-------|
| Vzdálená instalace agenta | Na stanice bez agenta (seznam z AD sync); WinRM kanál, just-in-time creds, audit, žádné uložené admin creds |
| Webová správa whitelistu | Přidání schváleného média z konzole → DB; **staging + offline podpis** (privátní klíč nikdy na serveru) |
| Hardening konzole | gMSA místo LocalSystem; dedikovaná skupina `USB-Guardian-Admins`; HTTPS; přesun API z SQL-04 na .213 (plný dvouvrstvý model) |
| Toast Privilege Separation | Helper process v user session – jednosměrné Pipes SYSTEM → user |
| Email notifikace | Microsoft Graph API – alerting bez SMTP závislosti |

> Hotovo (dřív pending): **Admin UI** – serverová Blazor konzole (Přehled, Stanice, Nastavení,
> Dokumentace) + AD sync inventář stanic. Viz „Serverová admin konzole".
| HTTPS produkce | Spustit `scripts\New-Certificate.ps1` na `B-S-W-SQL-04` |

## Watchdog – Task Scheduler

```
Task Scheduler (\USBGuardian\USBGuardian-Watchdog)
    ↓  každé 3 minuty + při startu systému
Kontrola: běží "USB Guardian" service?
    ↓ NE
Start-Service + Event Log ID 200 (Warning)
    ↓ selhání
Event Log ID 500 (Error) – nutný zásah IT
```

- Běží pod **SYSTEM** – nezávisle na přihlášeném uživateli
- Útočník musí zastavit **service i scheduled task** – více kroků, více stop
- Registrace: `scripts\Register-Watchdog.ps1` (auto-elevace UAC)
