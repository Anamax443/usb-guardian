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
| `WhitelistSync` | Heartbeat + stahování whitelistu (interval: **2 min**, konfig `sync:whitelistSyncIntervalMinutes`). Heartbeat nese verzi/online; při změně whitelistu se stáhne v témž cyklu → nový whitelist na klientech do ~2 min |
| `IncidentSync` | Odesílá frontu incidentů na server (interval: 1 min, s jitter; probudí se dřív při `ReportNow`) |
| `SyncSignals` | Sdílený signál: heartbeat (`ReportNow`) → okamžitý flush fronty incidentů |
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

## Šifrovaná komunikace agent ↔ API (self-contained TLS)

API si při startu vygeneruje/persistne vlastní self-signed cert (`SelfCert.cs`, **`MachineKeySet`** –
běží i pod gMSA, NE EphemeralKeySet – s ní Schannel neudělá server handshake), Kestrel bind `:5443`.
Bez CA, bez cert store. Agent ho ověří **pinningem otisku** (`TlsClient.cs`, `tls.pinnedThumbprint`)
→ šifrované i ověřené. Otisk = `GET /api/cert-info` / log API. Přístup k API přes policy
`USBGuardianClients` (členství v `Authorization:AllowedGroups`).

## Vyžádání dat na klik (ReportNow)

Push model = server nemá zpětný kanál k agentovi. „Vyžádat data" proto jede přes **příkaz přibalený
do odpovědi na heartbeat** (stejný kanál jako `WhitelistUpdateAvailable`):

```
Konzole (Stanice) → AppSettings: cmd.report.<HOST> = čas požadavku (UTC)
Agent heartbeat (≤2 min) → HeartbeatController: ReportNow=true POKUD požadavek novější než PŘEDCHOZÍ LastSeen
        ↓ (jednorázové – příští heartbeat má LastSeen už za časem požadavku → ReportNow=false; API jen ČTE AppSettings)
Agent: heartbeat potvrdil online+verzi (LastSeen) + SyncSignals → IncidentSync hned flushne frontu
Konzole: „vyžádáno HH:mm" dokud se agent neozve (LastSeen ≥ čas požadavku)
```

Latence ≤ heartbeat interval (~2 min). Hromadně přes „Vyžádat data od všech" (jen stanice hlásící agenta).
Klíč `cmd.report.<HOST>` v `AppSettings` slouží i jako audit „naposledy vyžádáno".

## Centrální nastavení a alerty (konzole)

Tabulka `AppSettings` (key/value, migrace 06) spravovaná z Nastavení; `AccessCache` singleton:
- `policy.enforce` – vynucovat jen schválená média (agent začne respektovat po heartbeat distribuci – pending).
- `comm.silentAfterMinutes` – práh „zmlklého agenta" (default 180); hranice pro tečku komunikace i dlaždici na Stanicích.
- `access.users` / `access.groups` – whitelist přístupu do konzole (`appsettings` = lockout-safe bootstrap).
- `email.*` – SMTP relay (M365 Direct Send) + `IncidentAlertService` (background notifier: souhrn nových
  neschválených incidentů, baseline při 1. běhu, interval/throttle; `EmailSender`).

## Konzole – funkce stránek

- **Přehled** – dlaždicový souhrn napříč listy + filtr (období/akce/fulltext) + kumulace (GroupBy přes
  anonymní typ → in-memory map) + sloupec „Schváleno" dle aktivního whitelistu. Tabulka „Detailně" má
  **řaditelné hlavičky** (řazení v DB přes query-string, před `Take(200)`).
- **Stanice** – AD inventář, filtr, cesta v AD (OU), ikona komunikace (dle čerstvosti `LastSeen`),
  dlaždice „Zmlklo agentů" (hlásí agenta, ale `LastSeen` starší než práh `comm.silentAfterMinutes` – možný výpadek/tamper),
  tlačítko „Vyžádat data" (řádek/hromadně) → [ReportNow](#vyžádání-dat-na-klik-reportnow).
- **Whitelist** – serial-only zadání + backfill VID/PID z incidentů + import + inline edit + `IsActive` checkbox.
- **Dokumentace** – render `.md` (Markdig) jako tisknutelné HTML, rozcestník.

## Pending (roadmap)

| Položka | Popis |
|---------|-------|
| Zavřít HTTP 5050 | NIS2 – jen HTTPS (firewall block / přebindovat API na SQL-04) |
| Vzdálená instalace agenta | Na stanice bez agenta (seznam z AD sync); WinRM, just-in-time creds, audit, žádné uložené creds |
| Podpisový/publikační workflow | Whitelist staging → offline podpis → publikace; odemkne vynucování + blocklist „naostro" k agentům |
| Per-serial blocklist | Zákaz konkrétního média, near-real-time k agentům (přednost před whitelistem) |
| Hardening konzole | gMSA místo LocalSystem; dedikovaná `USB-Guardian-Admins`; HTTPS konzole; přesun API na .213 |
| Toast Privilege Separation | Helper process v user session – jednosměrné Pipes SYSTEM → user |

> Hotovo (dřív pending): Admin UI (Blazor konzole + AD sync), **šifrovaná komunikace agent↔API**
> (self-cert + pinning), centrální nastavení (vynucování/přístup/e-mail + alerty).

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
