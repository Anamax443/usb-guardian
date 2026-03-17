# USB Guardian

Bezpečnostní nástroj pro monitoring paměťových médií (USB flash disky, SD karty, USB HDD)
na firemních počítačích. Každé médium musí být schváleno IT oddělením a zapsáno do
centrálního whitelistu. Nepovolená média jsou varována nebo zablokována.

## Regulatorní soulad

USB Guardian byl navržen jako technické opatření splňující požadavky:

- **NIS2** (Směrnice EU 2022/2555) – čl. 21 odst. 2: bezpečnost dodavatelského řetězce,
  základní kybernetická hygiena, hlášení incidentů
- **Zákon č. 181/2014 Sb.** o kybernetické bezpečnosti + Vyhláška č. 82/2018 Sb.
  (§ 14 řízení přístupů, § 16 ochrana před škodlivým kódem)
- **ISO/IEC 27001:2022** – kontroly A.8.12 (prevence úniku dat), A.7.10 (paměťová média),
  A.8.15 (logování), A.5.26 (reakce na incidenty)

Podrobný popis compliance viz [`docs/architecture.md`](docs/architecture.md).

---

## Stav projektu

| Fáze | Popis | Stav |
|------|-------|------|
| 1 | Windows agent – WMI detekce, warn mode, Toast notifikace | ✅ Hotovo |
| 2 | Block mode – IOCTL lock, PnpDevice fallback | ✅ Hotovo |
| 3 | REST API server, SQL Server, file-based logging, sync | ✅ Hotovo |
| 4 | ACL queue\, service recovery, WMI watchdog, timing fix | ✅ Hotovo |
| 4b | Disconnect tracking, fix duplikátů (offset persist), N+1 fix | ✅ Hotovo |
| 4c | Incident queue (bounded Channel), jitter, retry 503 | ✅ Hotovo |
| 5 | Toast z SYSTEM kontextu (Privilege Separation) | 🔜 Plánováno |
| 6 | Email notifikace (Microsoft Graph API) | 🔜 Plánováno |
| 7 | Admin UI – dashboard, správa whitelistu, reporty | 📋 Plánováno |

---

## Architektura

```
[Klientský PC]                          [Server B-S-W-SQL-04]
┌─────────────────────────────┐         ┌──────────────────────────┐
│  USB Guardian Agent         │         │  USB Guardian API        │
│                             │         │  (Windows Service :5050) │
│  DeviceMonitor (WMI)        │         │                          │
│    ↓                        │  HTTP   │  IncidentsController     │
│  WhitelistChecker ◄─────────┼─────────┤  WhitelistController     │
│    ↓                        │         │  HeartbeatController     │
│  PolicyEnforcer             │         └──────────┬───────────────┘
│    ↓              ↓         │    Windows Auth (Kerberos / AD skupina)
│  IncidentLogger  Blocker    │         ┌──────────▼───────────────┐
│    ↓                        │         │  SQL Server              │
│  queue\log_HOST_DATE.json   │         │  databáze USBGuardian    │
│    ↓                        │         │  Incidents               │
│  IncidentSync ──────────────┼────────►│  WhitelistDevices        │
│  WhitelistSync ◄────────────┼─────────│  WhitelistVersions       │
└─────────────────────────────┘         │  Computers               │
                                        └──────────────────────────┘
```

### Offline-first design
Agent funguje plně bez připojení k serveru:
- Whitelist je uložen lokálně (synchronizován každých N minut)
- Incidenty se ukládají do denních JSON souborů (`queue\`)
- Po obnovení spojení se soubory automaticky odešlou (delta sync – jen nové záznamy)
- Uzavřené dny se přesouvají do `sent\` (audit trail, 90 dní retence)

---

## Jak to funguje

```
[USB/SD médium připojeno]
         ↓
[WMI event – Windows detekuje zařízení]
         ↓
[Agent přečte VendorId, ProductId, SerialNumber, kapacitu, firmware]
         ↓
[Porovnání s lokálním whitelistem]
         ↓                              ↓
[Médium NA whitelistu]         [Médium NENÍ na whitelistu]
[Záznam: Allowed]              [Toast notifikace uživateli]
                               [Záznam: Warned / Blocked]
                               [Block mode: FSCTL_LOCK_VOLUME]
         ↓
[Záznam do queue\log_HOSTNAME_YYYY-MM-DD.json]
         ↓
[IncidentSync → POST /api/incidents (delta – jen nové záznamy)]
         ↓
[Uzavřený den → přesun do sent\ (archiv)]
```

---

## Požadavky

### Klientský PC (agent)
- Windows 10/11 64-bit
- .NET 8.0 SDK (vývoj) nebo Runtime (produkce)
- Přístup na síť k API serveru (port 5050)
- Počítačový účet v AD skupině `USB-Guardian-Clients`

### API Server
- Windows Server
- .NET 8.0 Runtime
- SQL Server (doporučeno 2019+)
- gMSA účet s SPN registrací pro Kerberos autentizaci

---

## Instalace

### 1. Active Directory (Domain Controller)

```powershell
# Vytvořit skupinu
New-ADGroup -Name "USB-Guardian-Clients" -GroupCategory Security `
    -GroupScope Global -Description "Pristup na USB Guardian API"

# Přidat Domain Computers (všechny firemní PC)
Add-ADGroupMember -Identity "USB-Guardian-Clients" `
    -Members (Get-ADGroup "Domain Computers")

# Přidat IT adminy pro testování přes Swagger
Add-ADGroupMember -Identity "USB-Guardian-Clients" -Members "jmeno.admina"

# Registrovat SPN pro gMSA účet (POVINNÉ pro Kerberos)
setspn -S HTTP/NAZEV-SERVERU "DOMENA\gmsa-ucet$"
setspn -S HTTP/NAZEV-SERVERU.domena.local "DOMENA\gmsa-ucet$"
```

### 2. SQL Server

```sql
-- Spustit skripty v pořadí:
-- database/01_create_database.sql
-- database/02_create_tables.sql
-- database/03_add_sourcefile.sql

-- Přidat gMSA účet
USE USBGuardian;
CREATE USER [DOMENA\gmsa-ucet$] FOR LOGIN [DOMENA\gmsa-ucet$];
ALTER ROLE db_datareader ADD MEMBER [DOMENA\gmsa-ucet$];
ALTER ROLE db_datawriter ADD MEMBER [DOMENA\gmsa-ucet$];
```

### 3. API Server

```powershell
# Build na dev stroji
cd server\USBGuardian.Api
dotnet publish -c Release -r win-x64 --self-contained -o "D:\deploy\USBGuardian.Api"

# Kopírovat na server přes SCP (SMB záměrně vypnuto – security hardening)
scp -r "D:\deploy\USBGuardian.Api" admin@SERVER:/C:/USBGuardian.Api

# Konfigurace na serveru: C:\USBGuardian.Api\appsettings.local.json
# (viz server\USBGuardian.Api\appsettings.local.json.example)

# Instalace jako Windows Service
sc.exe create "USB Guardian API" binPath="C:\USBGuardian.Api\USBGuardian.Api.exe" `
    obj="DOMENA\gmsa-ucet$" start=auto

# Service recovery – auto-restart při pádu
sc.exe failure "USB Guardian API" reset=86400 `
    actions=restart/5000/restart/10000/restart/30000

# Firewall
New-NetFirewallRule -DisplayName "USB Guardian API" -Direction Inbound `
    -Protocol TCP -LocalPort 5050 -Action Allow

Start-Service "USB Guardian API"
```

### 4. Klientský PC – příprava složek

```powershell
# Spustit jako Administrator
$folders = @(
    "C:\ProgramData\USBGuardian\whitelist",
    "C:\ProgramData\USBGuardian\queue",
    "C:\ProgramData\USBGuardian\sent"
)
foreach ($folder in $folders) {
    New-Item -ItemType Directory -Force -Path $folder
    icacls $folder /grant "SYSTEM:(OI)(CI)F"
    icacls $folder /grant "Administrators:(OI)(CI)F"
}
icacls "C:\ProgramData\USBGuardian\queue" /grant "Users:(OI)(CI)M"
icacls "C:\ProgramData\USBGuardian\sent"  /grant "Users:(OI)(CI)M"
icacls "C:\ProgramData\USBGuardian\whitelist" /grant "Users:(OI)(CI)R"

# Lokální konfigurace
@'
{
  "whitelist": {
    "syncUrl": "http://NAZEV-SERVERU:5050"
  }
}
'@ | Set-Content "agent\USBGuardian\Config\agent.config.local.json" -Encoding UTF8
```

### 5. Spuštění agenta (vývojový režim)

```powershell
cd agent\USBGuardian
dotnet run -- --console
```

> **Block mode vyžaduje admin práva.** V produkci agent běží jako SYSTEM.

---

## Konfigurace agenta

Soubor `agent\USBGuardian\Config\agent.config.json`:

| Klíč | Výchozí | Popis |
|------|---------|-------|
| `policy.mode` | `warn` | `warn` = varovat, `block` = zablokovat |
| `policy.maxOfflineAgeDays` | `30` | Max stáří whitelistu v offline režimu |
| `policy.onExpiredWhitelist` | `warn` | Chování při expiraci whitelistu |
| `whitelist.localPath` | `C:\ProgramData\...\whitelist.json` | Lokální cesta k whitelistu |
| `whitelist.syncUrl` | `http://YOUR_API_SERVER:5050` | URL API serveru |
| `whitelist.allowWildcards` | `false` | Záznamy bez sér. čísla – výchozí zakázáno (NIS2) |
| `sync.incidentSyncIntervalMinutes` | `1` | Interval odesílání incidentů (min) |
| `sync.whitelistSyncIntervalMinutes` | `15` | Interval sync whitelistu (min) |
| `logging.queuePath` | `C:\ProgramData\USBGuardian\queue` | Složka fronty |
| `logging.sentPath` | `C:\ProgramData\USBGuardian\sent` | Archivní složka |
| `logging.sentRetentionDays` | `90` | Retence archivních souborů (dny) |

---

## Správa whitelistu

### Zjistit identifikátory zařízení

```powershell
Get-WmiObject Win32_DiskDrive |
  Where-Object { $_.InterfaceType -eq "USB" } |
  Select-Object Caption, PNPDeviceID, SerialNumber |
  Format-List
```

### Přidat zařízení

```sql
USE USBGuardian;
INSERT INTO dbo.WhitelistDevices (VendorId, ProductId, SerialNumber, Description, ApprovedBy, IsActive)
VALUES ('KINGSTON', 'DATATRAVELER_2.0', '4B018CD154C9', 'Kingston DT 14GB – IT', 'it-admin', 1);

-- Zvýšit verzi (agent stáhne aktualizaci do 15 minut)
UPDATE dbo.WhitelistVersions SET Version = '2026-03-16-v2' WHERE IsActive = 1;
```

### AllowWildcards (výchozí: false)

Záznamy bez sériového čísla jsou ve výchozím stavu **zakázány** (NIS2 compliance).
Bez sériového čísla by útočník mohl použít stejný model média.

---

## Datové úložiště

### Lokální (agent)

```
C:\ProgramData\USBGuardian\
├── whitelist\whitelist.json              ← sync ze serveru, read-only pro Users
├── queue\log_HOSTNAME_2026-03-17.json    ← aktuální den, průběžně odesílán
└── sent\log_HOSTNAME_2026-03-15.json     ← archiv uzavřených dní (90 dní)
```

**Delta sync**: Odesílají se jen nové záznamy od posledního odeslaní. Po půlnoci
se soubor přesune do `sent\`.

### Centrální databáze (SQL Server)

```
USBGuardian
├── dbo.Incidents         ← všechna připojení (Allowed / Warned / Blocked)
│   ├── Timestamp         ← čas připojení média
│   ├── DisconnectedAt    ← čas odpojení (NULL = neznámo / stále připojeno)
│   └── ...
├── dbo.WhitelistDevices  ← schválená média
├── dbo.WhitelistVersions ← verze whitelistu
└── dbo.Computers         ← evidence stanic (hostname, IP, LastSeen)
```

---

## Struktura projektu

```
usb-guardian/
├── agent/USBGuardian/
│   ├── DeviceMonitor.cs        ← WMI dual-watcher, parsování PNPDeviceID
│   ├── WhitelistChecker.cs     ← porovnání, cache, AllowWildcards, expirace
│   ├── PolicyEnforcer.cs       ← warn/block logika
│   ├── DeviceBlocker.cs        ← FSCTL_LOCK_VOLUME + PnpDevice fallback
│   ├── NotificationService.cs  ← Windows Toast
│   ├── IncidentLogger.cs       ← denní JSON, queue/sent, retence
│   ├── IncidentSync.cs         ← delta sync, přesun do sent\
│   ├── WhitelistSync.cs        ← heartbeat, stažení nové verze
│   ├── Program.cs              ← DI, Windows Service, konfigurace
│   ├── Models/                 ← DeviceInfo, Incident, WhitelistEntry
│   └── Config/                 ← agent.config.json + local.json.example
├── server/USBGuardian.Api/
│   ├── Controllers/            ← Incidents, Whitelist, Heartbeat
│   ├── Queue/                  ← IncidentQueue (Channel), IncidentQueueWorker
│   ├── Data/AppDbContext.cs
│   ├── Models/                 ← DbModels, ApiModels
│   ├── Program.cs
│   └── appsettings*.json
├── database/
│   ├── 01_create_database.sql
│   ├── 02_create_tables.sql
│   └── 03_add_sourcefile.sql
└── docs/architecture.md        ← technická dokumentace + deployment guide
```

---

## Ověření funkčnosti

```powershell
# Test konektivity
Test-NetConnection -ComputerName B-S-W-SQL-04 -Port 5050

# Test API
Invoke-WebRequest -Uri "http://B-S-W-SQL-04:5050/api/whitelist/version" `
    -UseDefaultCredentials -AllowUnencryptedAuthentication |
    Select-Object -ExpandProperty Content

# Swagger UI
Start-Process "http://B-S-W-SQL-04:5050/swagger"
```

---

## Bezpečnostní doporučení

- `*.local.json` soubory se nikdy necommitují (jsou v `.gitignore`)
- `AllowWildcards: false` – bez sériového čísla = zamítnuto (NIS2)
- SMB záměrně vypnuto na SQL serveru – deploy výhradně přes SCP
- Windows Auth (Kerberos) – agent jako `HOSTNAME$`, API pod gMSA
- Service recovery: auto-restart při pádu (3× s rostoucí prodlevou: 5s/30s/60s)
- ACL `queue\`: pouze SYSTEM + Administrators – uživatelé nemají přístup k datům incidentů
- WMI watchdog: automatická re-registrace subscriptions při selhání WMI

---

## Pending (plánované funkce)

- [x] ACL `queue\` – pouze SYSTEM + Administrators (Users bez přístupu)
- [x] Service recovery – automatický restart při selhání (3× s rostoucí prodlevou)
- [x] WMI watchdog – detekce zaseknutí watcheru, automatická re-registrace
- [x] Timing fix – obousměrné párování DiskDrive/LogicalDisk (timeout 30s)
- [x] Disconnect tracking – čas připojení a odpojení média, doba připojení
- [x] Fix duplikátů – offset persistuje na disk (.offset soubor, přežije restart)
- [x] Fix N+1 – deduplikace jedním bulk SQL dotazem místo N dotazů
- [x] UNIQUE constraint v DB – pojistka proti duplikátům na úrovni databáze
- [x] Incident queue – bounded Channel (max 1000 batchů), 202 Accepted, sekvenční zpracování
- [x] Jitter při startu – náhodné zpoždění 0–60s (thundering herd ochrana pro 500+ PC)
- [x] Retry při 503 – agent opakuje při plné frontě serveru (3× po 30s)
- [ ] Toast z SYSTEM kontextu (Privilege Separation – helper process v user session)
- [ ] RSA podpis whitelistu (Fáze 4 – bezpečný rollout na terénní stroje)
- [ ] WM_DEVICECHANGE jako záloha za WMI
- [ ] HTTPS pro API
- [ ] API verzování `/api/v1/`
- [ ] Email notifikace (Microsoft Graph API)
- [ ] Admin UI – dashboard, správa whitelistu, reporty
- [ ] Enrollment tool pro L1 support
- [ ] GPO šablona pro `agent.config.local.json`
- [ ] TenantId (multi-tenant příprava)

---

*USB Guardian – Fáze 4c dokončena | IT Security Tool | NIS2 + ISO 27001 compliant*
