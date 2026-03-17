# USB Guardian – Technická dokumentace architektury

## Regulatorní kontext a compliance

USB Guardian byl navržen jako technické opatření splňující požadavky níže uvedených předpisů.
Tato sekce slouží jako podklad pro bezpečnostní audity a dokumentaci ISMS.

### NIS2 – Směrnice EU 2022/2555

Směrnice NIS2 (Network and Information Security) ukládá povinným subjektům zavést technická
a organizační opatření pro řízení kybernetických rizik. USB Guardian adresuje konkrétně:

| Požadavek NIS2 | Jak USB Guardian plní |
|---|---|
| Čl. 21 odst. 2 písm. e) – bezpečnost dodavatelského řetězce | Kontrola médií přinášených do firmy (flash disky, SD karty) |
| Čl. 21 odst. 2 písm. h) – základní hygiena v oblasti kyb. bezpečnosti | Whitelist povolených zařízení, automatické blokování neznámých |
| Čl. 23 – hlášení incidentů | Strukturovaný log incidentů (kdo, kdy, co, jaká akce) připravený pro hlášení |

### Zákon č. 181/2014 Sb. o kybernetické bezpečnosti

Zákon a jeho prováděcí předpisy ukládají povinnost chránit systémy před neoprávněným přístupem.

**Vyhláška č. 82/2018 Sb.** (o bezpečnostních opatřeních) – USB Guardian implementuje:
- § 14 – Řízení přístupů: pouze schválená média mohou být použita na firemních stanicích
- § 16 – Ochrana před škodlivým kódem: zamezení zavlečení malware přes neznámá USB média
- § 22 – Fyzická bezpečnost: kontrola fyzických nosičů dat

> **Poznámka:** V souvislosti s transpozicí NIS2 do českého práva (zákon č. 240/2022 Sb.)
> jsou připravovány nové prováděcí vyhlášky. Čísla vyhlášek ověřte na aktuálním znění
> ve Sbírce zákonů (epravo.cz / zakonyprolidi.cz) – čísla se mohla změnit.

### ISO/IEC 27001:2022 – ISMS

USB Guardian podporuje implementaci následujících kontrol dle Přílohy A normy ISO 27001:

| Kontrola ISO 27001 | Popis | Jak USB Guardian plní |
|---|---|---|
| A.8.12 – Prevence úniku dat | Zabránění neoprávněnému přenosu dat | Blokování neschválených médií |
| A.8.20 – Bezpečnost sítí | Ochrana před zanesením hrozeb | Whitelist zabraňuje použití neznámých médií |
| A.7.10 – Paměťová média | Řízení životního cyklu médií | Evidence schválených médií s metadaty (kdo, kdy schválil) |
| A.8.15 – Logování | Audit trail bezpečnostních událostí | Denní JSON soubory: uživatel, PC, zařízení, čas, akce. Po odeslání archiv v sent\ složce (90 dní). |
| A.5.26 – Reakce na incidenty | Evidence a hlášení incidentů | Strukturovaný log připravený pro SIEM/export |

### Praktický dopad na audit

Při bezpečnostním auditu (ISO 27001, NIS2, SOC2) USB Guardian poskytuje:

```
Důkaz 1: Existence whitelistu
  → whitelist.json s metadaty (kdo schválil, kdy, popis zařízení)

Důkaz 2: Log incidentů
  → queue\*.json – každé připojení média (povolené i nepovolené)
  → obsahuje: timestamp, hostname, username, VID/PID/serial, akce

Důkaz 3: Technické opatření
  → warn mode = detekce + upozornění + log
  → block mode = aktivní blokování přístupu k médiu

Důkaz 4: Pokrytí offline stanic
  → agent funguje bez síťového připojení
  → whitelist má datum expirace (max. stáří konfigurovatelné)
```

---

## Přehled systému

USB Guardian je Windows agent (Background Service) který monitoruje připojení paměťových médií
a porovnává je proti centrálně spravovanému whitelistu schválených zařízení.

Klíčový design princip: **offline-first** – agent funguje plně bez síťového připojení,
což je nezbytné pro terénní pracovníky na hotspotu nebo mimo doménu.

---

## Architektura komponent

```
┌──────────────────────────────────────────────────────────────────┐
│                        Windows Service                           │
│                                                                  │
│  ┌─────────────────────────────────┐                            │
│  │         DeviceMonitor           │                            │
│  │                                 │                            │
│  │  [WMI: Win32_DiskDrive]        │                            │
│  │    ↓ fyzický disk               │                            │
│  │  [pendingDevices dict]          │                            │
│  │    ↓ korelace DiskIndex         │                            │
│  │  [WMI: Win32_LogicalDisk]      │                            │
│  │    ↓ drive letter (F:)          │                            │
│  └──────────────┬──────────────────┘                            │
│                 ↓                                                │
│  ┌──────────────────┐   ┌──────────────────┐                   │
│  │ WhitelistChecker  │   │                  │                   │
│  │  (JSON cache)     │   │                  │                   │
│  └────────┬─────────┘   │                  │                   │
│           ↓             │                  │                   │
│  ┌──────────────────┐   │                  │                   │
│  │  PolicyEnforcer  │   │                  │                   │
│  │  (warn / block)  │   │                  │                   │
│  └───┬──────────────┘   │                  │                   │
│      │                  │                  │                   │
│      ↓          ↓               ↓          │                   │
│  ┌────────┐  ┌──────────────┐  ┌────────┐  │                   │
│  │Device  │  │Notification  │  │Incident│  │                   │
│  │Blocker │  │Service       │  │Logger  │  │                   │
│  │(IOCTL) │  │(Toast/Email) │  │(JSON)  │  │                   │
│  └────────┘  └──────────────┘  └────────┘  │                   │
└──────────────────────────────────────────────────────────────────┘
```

---

## Komponenty

### DeviceMonitor
- **Technologie:** Dva WMI watchers běžící paralelně
  - `Win32_DiskDrive` – fyzický disk (VID, PID, Serial, kapacita, firmware)
  - `Win32_LogicalDisk` – drive letter (F:, G: atd.)
- **Korelace:** `DiskIndex` spojuje fyzický disk s logickým diskem
- **Pending mechanismus:** Fyzický disk se uloží do `ConcurrentDictionary`, čeká na drive letter event
- **Fallback:** Pokud drive letter nepřijde do 10 sekund, zpracuje se médium bez něj
- **Parser:** Podporuje dva formáty PNPDeviceID:
  - `USB\VID_xxxx&PID_xxxx` – klasický USB (hex identifikátory)
  - `USBSTOR\DISK&VEN_xxx&PROD_xxx` – storage zařízení (textové názvy)
- **Filtr:** Přeskakuje interní disky (SATA, NVMe, SCSI)
- **Data ze zařízení:** VendorId, ProductId, SerialNumber, FriendlyName, kapacita, firmware, drive letters

### WhitelistChecker
- **Zdroj:** JSON soubor `C:\ProgramData\USBGuardian\whitelist\whitelist.json`
- **Cache:** In-memory cache platná 5 minut (snižuje I/O)
- **Porovnání:** VendorId + ProductId + SerialNumber (case-insensitive)
- **Wildcard:** Prázdný SerialNumber = platí pro celou řadu (bezpečnostní riziko)
- **Expirace:** Whitelist má `validUntil` datum – po expiraci degraded mód

### PolicyEnforcer
- **Řídí se:** `policy.mode` v `agent.config.json`
- **warn:** uživatel dostane Toast, médium funguje
- **block:** médium uzamčeno přes DeviceBlocker (DeviceIoControl)
- **Fallback:** pokud nelze zjistit drive letter → automaticky přejde na warn
- **Degraded mód:** při expiraci whitelistu dle `onExpiredWhitelist`

### DeviceBlocker
- **Technologie:** Win32 API – `DeviceIoControl` přes P/Invoke (`kernel32.dll`)
- **Primární metoda:** `FSCTL_DISMOUNT_VOLUME` + `FSCTL_LOCK_VOLUME` na drive letter (`\\.\F:`)
- **Fallback:** `Disable-PnpDevice` přes PowerShell na PNPDeviceID (pokud drive letter není k dispozici)
- **Efekt:** Médium je nepřístupné – Windows vrátí "Přístup odepřen"
- **Reverzibilní:** Odblokování zavřením handle nebo `Enable-PnpDevice`
- **Vyžaduje:** Admin práva
  - Produkce (Windows Service): běží jako SYSTEM – automaticky
  - Vývoj: spustit přes elevated PowerShell (`Start-Process powershell -Verb RunAs`)

### NotificationService
- **Technologie:** Windows Toast Notification přes PowerShell
- **Nevyžaduje:** žádné extra knihovny ani NuGet balíčky
- **Fáze 2:** Microsoft Graph API pro email (Shared Mailbox, bez extra licence)

### IncidentLogger
- **Technologie:** Denní JSON soubory – žádná DB, žádná instalace
- **Queue:** `C:\ProgramData\USBGuardian\queue\log_HOSTNAME_2026-03-16.json`
- **Design:** Offline-first – aktuální den se neodesílá (zapisuje se), uzavřené dny se odesílají
- **Loguje vše:** Povolená i nepovolená média (kompletní audit trail)
- **Retence archívu:** Konfigurovatelná, výchozí 90 dní (`sentRetentionDays`)

### IncidentSync
- **Delta sync:** Odesílají se pouze nové záznamy od posledního odeslaní (agent sleduje offset)
- **Dnešní soubor:** Zůstává v `queue\`, odesílá se každých N minut (jen nové záznamy)
- **Uzavřený den:** Po půlnoci přesun do `sent\` (audit trail)
- **Offline:** Soubory čekají v `queue\` – při obnovení spojení se odešlou
- **Deduplikace na API:** Server odmítne záznamy se stejným Hostname+Timestamp+SerialNumber

### WhitelistSync
- **Heartbeat:** Každých N minut dotaz na `/api/heartbeat` (verze whitelistu)
- **Stažení:** Jen při změně verze – úspora bandwidth
- **Atomický zápis:** Přes `.tmp` soubor – nelze přerušit při výpadku napájení

---

## Datové schéma – lokální fronta (JSON)

### Denní log soubor (`log_HOSTNAME_YYYY-MM-DD.json`)

```json
{
  "Date": "2026-03-17",
  "Hostname": "PC-NOVAK-01",
  "RecordCount": 3,
  "Records": [
    {
      "Timestamp": "2026-03-17T07:42:44Z",
      "Username": "jan.novak",
      "VendorId": "KINGSTON",
      "ProductId": "DATATRAVELER_2.0",
      "SerialNumber": "4B018CD154C9",
      "FriendlyName": "Kingston DataTraveler 2.0 USB Device",
      "DeviceType": "UsbFlashDrive",
      "SizeBytes": 15496427520,
      "SizeFormatted": "14,4 GB",
      "FirmwareRevision": "PMAP",
      "PnpDeviceId": "USBSTOR\\DISK&VEN_KINGSTON&PROD_DATATRAVELER_2.0...",
      "Action": "Allowed",
      "WhitelistVersion": "2026-03-16-v3"
    }
  ]
}
```

**IncidentAction hodnoty:**
- `Allowed` – médium je na whitelistu, povoleno
- `Warned` – médium není na whitelistu, uživatel varován (warn mode)
- `Blocked` – médium zablokováno (block mode)
- `TemporarilyAllowed` – dočasné povolení (budoucí funkce)

---

## Whitelist soubor (JSON)

```json
{
  "version": "2026-03-16-v2",
  "issuedAt": "2026-03-16T00:00:00Z",
  "validUntil": "2026-04-16T00:00:00Z",
  "signature": "",
  "devices": [
    {
      "vendorId": "KINGSTON",
      "productId": "DATATRAVELER_2.0",
      "serialNumber": "4B018CD154C9",
      "description": "Trvalé schválení – IT oddělení",
      "approvedAt": "2026-03-16T00:00:00Z",
      "approvedBy": "it-admin",
      "validUntil": null
    },
    {
      "vendorId": "SANDISK",
      "productId": "CRUZER_FORCE",
      "serialNumber": "4C530000030923112121",
      "description": "Dočasné schválení – dodavatel XYZ do 31.3.2026",
      "approvedAt": "2026-03-16T00:00:00Z",
      "approvedBy": "it-admin",
      "validUntil": "2026-03-31T23:59:59Z"
    }
  ]
}
```

- `signature` – zatím prázdné, budoucí verze přidá RSA podpis (nelze podvrhnout offline)
- `validUntil` (whitelist) – expirace celého whitelistu, doporučeno 30 dní

### Expirace záznamů (ISO 27001 A.7.10 – řízení životního cyklu médií)

Každý záznam v whitelistu může mít vlastní datum platnosti (`validUntil`):

| validUntil | Význam | Použití |
|------------|--------|---------|
| `null` | Trvalé schválení | Firemní média IT oddělení |
| datum | Dočasné schválení | Dodavatel, návštěva, zkušební provoz |

Po expiraci médium přestane fungovat bez nutnosti ručního odebrání. Záznam zůstane v databázi jako audit trail. IT musí aktivně prodloužit platnost – **pravidelná recertifikace médií**.

SQL pro dočasné schválení:
```sql
INSERT INTO dbo.WhitelistDevices
    (VendorId, ProductId, SerialNumber, Description, ApprovedBy, ValidUntil)
VALUES
    ('SANDISK', 'CRUZER_FORCE', '4C530000030923112121',
     'Dodavatel XYZ – dočasný přístup', 'it-admin',
     DATEADD(DAY, 7, GETUTCDATE()));
```

---

## Offline provoz

```
ONLINE  → sync whitelistu každých N minut (konfigurovatelné)
          odesílání incidentů na server (delta sync – jen nové záznamy)

OFFLINE → agent používá lokální cached whitelist
          incidenty se ukládají do queue\log_HOSTNAME_DATE.json
          při obnovení připojení se odešlou automaticky

DEGRADED → whitelist expiroval
           chování dle policy.onExpiredWhitelist:
           "warn"         = stále varuje, médium funguje
           "block_new"    = blokuje neznámá média
           "strict_block" = blokuje vše
```

---

## Konfigurace bez hardcoded hodnot (Open Source ready)

Projekt neobsahuje žádné hardcoded názvy domén, serverů, skupin ani hesla.
Lze bezpečně publikovat jako open source nebo sdílet mezi organizacemi.

### Princip

```
Repozitář (veřejný)          Lokální přepis (NECOMMITUJE SE)
─────────────────────        ──────────────────────────────
agent.config.json            agent.config.local.json
  syncUrl: YOUR_API_SERVER     syncUrl: http://B-S-W-SQL-04:5050

appsettings.json             appsettings.local.json
  Server: YOUR_SQL_SERVER      Server: B-S-W-SQL-04
  AllowedGroups: YOUR_DOMAIN   AllowedGroups: AXINETWORK\...
```

### Co patří do repozitáře

```
✅ agent.config.json              – šablona s YOUR_* placeholdery
✅ appsettings.json               – šablona s YOUR_* placeholdery
✅ *.example soubory              – příklady lokálních přepisů
✅ .gitignore                     – chrání *.local.json soubory
✅ Kód                            – žádné hardcoded hodnoty
```

### Co NEPATŘÍ do repozitáře

```
❌ agent.config.local.json        – obsahuje skutečný hostname serveru
❌ appsettings.local.json         – obsahuje název domény a SQL Serveru
❌ whitelist.json v ProgramData   – firemní data
❌ queue/*.json                   – záznamy připojení
```

### Autentizace – proč žádná hesla

```
Agent → API Server:
  Windows Authentication (Kerberos)
  Agent se prezentuje jako HOSTNAME$ (účet počítače v AD)
  Žádné heslo – AD ověřuje automaticky

API Server → SQL Server:
  Integrated Security (gMSA účet)
  gMSA heslo rotuje automaticky – AD spravuje
  Connection string neobsahuje heslo

→ Lze bezpečně commitovat connection stringy do repozitáře
```

## Zabezpečení dat

### Adresář ProgramData
```
C:\ProgramData\USBGuardian\
  ├── whitelist\  → SYSTEM:F, Administrators:F, Users:R  (jen čtení)
  ├── queue\      → SYSTEM:F, Administrators:F, Users:M  (zápis pro agenta)
  └── sent\       → SYSTEM:F, Administrators:F, Users:M  (archiv)
```

- Uživatelé **nemohou editovat whitelist** (pouze čtení)
- `queue\` a `sent\` – Users mohou zapisovat (agent v konzolním módu běží jako user; v produkci jako SYSTEM)
- IT admin a SYSTEM mají plný přístup

### Secrets v konfiguraci
- `agent.config.local.json` – lokální přepisy (NIKDY necommitovat)
- `appsettings.local.json` – lokální přepisy pro API (NIKDY necommitovat)
- gMSA účet – heslo rotuje automaticky, žádné heslo v konfiguraci

---

## Fáze vývoje

### ✅ Fáze 1 – Dokončeno
- WMI monitoring USB/SD médií (Win32_DiskDrive + Win32_LogicalDisk dual watcher)
- Whitelist (lokální JSON soubor, chráněný ACL)
- Windows Toast notifikace
- Konfigurovatelný warn/block mód

### ✅ Fáze 2 – Dokončeno
- Block mode – FSCTL_LOCK_VOLUME přes DeviceIoControl
- Drive letter detection (dual WMI watcher, DiskIndex korelace)
- Fallback warn při selhání drive letter detekce
- PNPDeviceID uloženo pro každé zařízení
- Admin práva vyžadována pro block mode

### ✅ Fáze 3 – Dokončeno
- REST API server (.NET 8, Windows Service, port 5050)
- SQL Server databáze (Incidents, WhitelistDevices, WhitelistVersions, Computers)
- Windows Authentication (Kerberos) – agent jako HOSTNAME$, API pod gMSA
- File-based logging (denní JSON soubory) – žádná lokální DB
- WhitelistSync – heartbeat, stažení nové verze při změně
- IncidentSync – delta sync (jen nové záznamy), archiv do sent\
- AllowWildcards: false (NIS2 – záznamy bez sériového čísla zakázány)
- Konfigurovatelné sync intervaly (incidentSyncIntervalMinutes, whitelistSyncIntervalMinutes)
- Verbose logging s timestamps v konzoli
- SourceFile audit trail v DB (odkaz na zdrojový soubor)
- Deduplikace incidentů na API (Hostname + Timestamp + SerialNumber)

### 🔜 Fáze 4 – Plánováno
- Service recovery akce pro agenta (instalační skript)
- WMI watchdog – detekce zaseknutí Win32_DiskDrive watcheru
- WM_DEVICECHANGE jako záloha za WMI
- HTTPS pro API
- API verzování `/api/v1/`
- Email notifikace (Microsoft Graph API – Shared Mailbox)
- GPO šablona pro distribuci agent.config.local.json

### 📋 Fáze 5 – Plánováno
- Admin UI (React nebo Blazor)
- Dashboard se statistikami
- Správa whitelistu přes web
- Enrollment tool pro L1 support
- TenantId (multi-tenant příprava)

---

## Technologický stack

| Vrstva | Technologie | Verze |
|--------|------------|-------|
| Agent | C# / .NET | 8.0 |
| Hosting | Windows Service | – |
| Device detection | WMI / System.Management | – |
| Block mode | Win32 DeviceIoControl / P/Invoke | – |
| Local storage | Denní JSON soubory (queue/sent) | – |
| Notifications | PowerShell Toast | – |
| API Server | ASP.NET Core | 8.0 |
| Databáze | SQL Server + Entity Framework Core | – |
| Auth | Windows Authentication (Kerberos/NTLM) | – |
| Email (Fáze 4) | Microsoft Graph API / MSAL | – |
| Admin UI (Fáze 5) | React nebo Blazor | TBD |

---

## Struktura projektu

```
usb-guardian/
├── agent/
│   └── USBGuardian/
│       ├── DeviceMonitor.cs        ← dual WMI watcher, parsování, DiskIndex korelace
│       ├── WhitelistChecker.cs     ← porovnání, cache, AllowWildcards, expirace
│       ├── PolicyEnforcer.cs       ← warn/block logika
│       ├── DeviceBlocker.cs        ← IOCTL lock + PnpDevice fallback
│       ├── NotificationService.cs  ← Windows Toast notifikace
│       ├── IncidentLogger.cs       ← denní JSON soubory, queue/sent, retence 90 dní
│       ├── IncidentSync.cs         ← delta sync, přesun do sent\
│       ├── WhitelistSync.cs        ← heartbeat, stažení nové verze whitelistu
│       ├── Program.cs              ← DI, Windows Service hosting, konfigurace
│       ├── Models/
│       │   ├── DeviceInfo.cs
│       │   ├── Incident.cs         ← IncidentAction: Allowed/Warned/Blocked
│       │   └── WhitelistEntry.cs   ← ValidUntil (expirace záznamu)
│       └── Config/
│           ├── agent.config.json              ← šablona
│           └── agent.config.local.json.example
    └── architecture.md             ← tato dokumentace
```

---

## Obnova po havárii

Pro kompletní reinstalaci ze zdrojového kódu:

1. Klonovat repo: `git clone https://github.com/Anamax443/usb-guardian.git`
2. Vytvořit složky v ProgramData (viz README – Instalace)
3. Zkopírovat `whitelist\whitelist.json` do `C:\ProgramData\USBGuardian\whitelist\`
4. Editovat `agent\USBGuardian\Config\agent.config.local.json` – nastavit syncUrl
5. Buildovat: `cd agent\USBGuardian && dotnet build`
6. Spustit: `dotnet run -- --console` (vývojový režim)
7. Nebo nainstalovat jako service (produkce) – viz README

Všechna konfigurace je v gitu. Jediné co není v gitu:
- `C:\ProgramData\USBGuardian\whitelist\whitelist.json` – záloha whitelistu
- `C:\ProgramData\USBGuardian\queue\*.json` – záznamy čekající na sync

---

## Nasazení API serveru

### Proč SMB nefunguje (C$)

SQL server `B-S-W-SQL-04` má záměrně **vypnuté SMB/File and Printer Sharing** ve firewallu:

```
Důvod: Bezpečnostní hardening DB serverů
  → SMB na DB serveru = bezpečnostní riziko
  → Útočník přes SMB může číst/zapisovat soubory
  → Standardní praxe: SMB na DB serverech vypnout
  → NEMĚŇTE toto nastavení
```

### Správný způsob nasazení – SCP přes SSH

Server má otevřený **port 22 (SSH/SCP)** – použijte tento způsob vždy:

```powershell
# 1. Publishnout API na dev stroji
cd D:\git\usb-guardian\server\USBGuardian.Api
dotnet publish -c Release -r win-x64 --self-contained -o "D:\deploy\USBGuardian.Api"

# 2. Zkopírovat na server přes SCP
scp -r "D:\deploy\USBGuardian.Api" admintrnka@B-S-W-SQL-04:/C:/USBGuardian.Api
```

### Instalace Windows Service na serveru (přes SSH)

```powershell
# Připojit se na server
ssh admintrnka@B-S-W-SQL-04

# Na serveru – nainstalovat Windows Service pod gMSA účtem
sc.exe create "USB Guardian API" `
    binPath="C:\USBGuardian.Api\USBGuardian.Api.exe" `
    obj="AXINETWORK\gmsa-SQL$" `
    start=auto

# Otevřít port 5050 ve firewallu
New-NetFirewallRule -DisplayName "USB Guardian API" `
    -Direction Inbound -Protocol TCP -LocalPort 5050 -Action Allow

# Nakonfigurovat appsettings.local.json
# (zkopírovat ze šablony a upravit)

# Spustit service
Start-Service "USB Guardian API"
Get-Service "USB Guardian API"
```

### Ověření po nasazení

```powershell
# Test SSH konektivity
ssh admintrnka@B-S-W-SQL-04 "hostname && whoami"

# Test API dostupnosti
Test-NetConnection -ComputerName B-S-W-SQL-04 -Port 5050

# Test API endpointu
Invoke-WebRequest -Uri "http://B-S-W-SQL-04:5050/api/whitelist/version" `
    -UseDefaultCredentials -AllowUnencryptedAuthentication
```

---

## Server – REST API

### Technologie
- **Framework:** ASP.NET Core 8.0 Web API
- **Hosting:** Windows Service (port 5050)
- **Databáze:** SQL Server (`B-S-W-SQL-04`, databáze `USBGuardian`)
- **ORM:** Entity Framework Core
- **Auth:** Windows Authentication (Kerberos) – gMSA účet
- **Autorizace:** AD skupina `USB-Guardian-Clients`

### API Endpointy

| Method | Endpoint | Popis |
|--------|----------|-------|
| GET | `/api/heartbeat` | Heartbeat – verze whitelistu, LastSeen stanice |
| GET | `/api/whitelist` | Stažení celého whitelistu |
| GET | `/api/whitelist/version` | Jen verze (pro rychlou kontrolu) |
| POST | `/api/whitelist/devices` | Přidání zařízení do whitelistu |
| POST | `/api/incidents` | Batch upload incidentů (s deduplikací) |
| GET | `/api/incidents` | Výpis incidentů s filtry |
| GET | `/swagger` | Swagger UI (pro adminy) |
| GET | `/api/debug/whoami` | Debug – ověření autentizace |

### Databázové schéma SQL Server

| Tabulka | Popis |
|---------|-------|
| `dbo.WhitelistDevices` | Schválená média (VID, PID, Serial, ValidUntil) |
| `dbo.WhitelistVersions` | Verze whitelistu – každá změna = nová verze |
| `dbo.Incidents` | Log incidentů ze všech stanic (+ SourceFile audit trail) |
| `dbo.Computers` | Evidence stanic (hostname, IP, agent verze, LastSeen) |

---

## Tok dat systémem

### Účastníci

```
[Firemní stanice]               [Síť]          [Server B-S-W-SQL-04]
  USB Guardian Agent                             REST API + SQL Server
  (Windows Service / --console)  HTTP:5050      Windows Service pod gMSA
  autentizace: HOSTNAME$                        Kerberos / AD skupina
```

---

### Scénář 1 – Agent ONLINE, médium vloženo

```
1. Uživatel zasune USB médium
       ↓
2. WMI event → DeviceMonitor přečte VID, PID, Serial, kapacita, PnpDeviceId
       ↓
3. WhitelistChecker porovná s lokálním whitelist.json (cache 5 min)
   AllowWildcards=false: záznamy bez sériového čísla jsou zamítnuty
       ↓
   ┌─────────────────────────┐    ┌──────────────────────────────┐
   │ NA WHITELISTU           │    │ NENÍ NA WHITELISTU           │
   │ → záznam "Allowed"      │    │ → PolicyEnforcer             │
   │ → médium funguje        │    │   warn: Toast notifikace     │
   └─────────────────────────┘    │   block: FSCTL_LOCK_VOLUME   │
                                  │ → záznam "Warned" / "Blocked"│
                                  └──────────────────────────────┘
       ↓
4. IncidentLogger → queue\log_HOSTNAME_2026-03-17.json
       ↓
5. WhitelistSync (každých N min) → GET /api/heartbeat
   ← { whitelistUpdateAvailable: false } → nic se nestáhne
   ← { whitelistUpdateAvailable: true  } → GET /api/whitelist → uloží lokálně
       ↓
6. IncidentSync (každých N min) → delta sync:
   → odešle pouze nové záznamy (od posledního offsetu)
   → POST /api/incidents { hostname, incidents: [nové záznamy] }
   ← { accepted: N, duplicates: 0 }
   → aktualizuje offset
```

---

### Scénář 2 – Agent OFFLINE

```
1. USB médium vloženo → stejná detekce jako online
       ↓
2. WhitelistChecker použije lokální whitelist.json (bez sítě)
       ↓
3. Incident uložen do queue\log_HOSTNAME_DATE.json
       ↓
4. WhitelistSync: timeout → agent v offline stavu, whitelist stárne
       ↓
5. Whitelist expiruje → chování dle policy.onExpiredWhitelist
       ↓
6. Po obnovení sítě: IncidentSync odešle všechny čekající soubory
   → dle policy.onExpiredWhitelist:
      "warn"         → stále funguje, jen loguje
      "block_new"    → nová neznámá média blokuje
      "strict_block" → blokuje vše
       ↓
6. Po obnovení sítě: IncidentSync odešle všechny čekající soubory z queue\
```

---

### Scénář 3 – IT admin přidá nové médium

```
IT admin (SSMS nebo budoucí Admin UI)
       ↓
INSERT INTO dbo.WhitelistDevices (VendorId, ProductId, SerialNumber, ...)
UPDATE dbo.WhitelistVersions SET Version = 'YYYY-MM-DD-vN' WHERE IsActive = 1
       ↓
Při příštím heartbeatu agentů (každých N min):
   → agent detekuje novou verzi
   → GET /api/whitelist → stáhne a uloží lokálně
       ↓
Offline agenti:
   → dostanou novou verzi při příštím připojení
```

---

### Identita a oprávnění

```
┌─────────────────────────────────────────────────────────────────┐
│ Komponenta          │ Účet                │ Oprávnění           │
├─────────────────────┼─────────────────────┼─────────────────────┤
│ Agent (Windows Svc) │ SYSTEM              │ lokální PC, WMI     │
│                     │ → na síti jako:     │ PnpDevice disable   │
│                     │ DOMENA\HOSTNAME$    │ HTTP → REST API     │
├─────────────────────┼─────────────────────┼─────────────────────┤
│ REST API (Win Svc)  │ DOMENA\gmsa-ucet$   │ db_datareader       │
│                     │                     │ db_datawriter       │
│                     │                     │ POUZE USBGuardian   │
├─────────────────────┼─────────────────────┼─────────────────────┤
│ IT admin (Swagger)  │ DOMENA\IT-admin     │ db_datareader       │
│                     │ (v USB-Guardian-    │ db_datawriter       │
│                     │  Clients skupině)   │ POUZE USBGuardian   │
├─────────────────────┼─────────────────────┼─────────────────────┤
│ SQL Server          │ –                   │ hostuje DB          │
│                     │                     │ pouze Windows Auth  │
└─────────────────────┴─────────────────────┴─────────────────────┘
```

### AD skupina USB-Guardian-Clients

```
Typ:      Security, Global
Členové:  Domain Computers (všechny firemní stroje automaticky)
          + IT admini pro Swagger přístup

Jak funguje:
  Nový stroj → připojí do domény → automaticky v Domain Computers
             → automaticky dostane přístup na REST API
             → žádná ruční správa

  Vyřazení stroje → ztratí přístup na API
                  → agent stále funguje offline s cached whitelistem

REST API ověřuje:
  → USB-Guardian-Clients (firemní stroje)
  → ostatní → HTTP 401 Unauthorized
```

---

### Komunikační kanály

```
Agent → REST API:
  Protokol:    HTTP (HTTPS plánováno)
  Port:        5050
  Auth:        Windows Authentication (Kerberos)
  Frekvence:   heartbeat každých N min (konfigurovatelné)
               incident sync každých N min (delta – jen nové záznamy)

REST API → SQL Server:
  Protokol:    TDS (SQL Server Native)
  Port:        1433 (localhost)
  Auth:        Windows Authentication (gMSA účet)
  Poznámka:    gMSA heslo rotuje automaticky

Agent → lokální soubory:
  queue\log_HOSTNAME_DATE.json  ← živý soubor dne
  sent\log_HOSTNAME_DATE.json   ← archiv po odeslání (90 dní)
  whitelist\whitelist.json      ← lokální cache whitelistu
```

---

### Co se stane při výpadku serveru

```
REST API server nedostupný:
  → Agent funguje normálně (offline-first design)
  → Incidenty se hromadí v queue\ (JSON soubory)
  → Whitelist funguje z cache (platný do ValidUntil)
  → Po obnovení serveru: automatický delta sync bez zásahu

SQL Server nedostupný:
  → REST API vrátí HTTP 500
  → Agent přejde do offline módu
  → Stejný efekt jako výpadek API serveru

Výpadek agenta (restart PC):
  → Windows Service se automaticky restartuje
  → JSON soubory v queue\ jsou persistentní (přežijí restart)
  → Při startu: delta sync odešle čekající záznamy
```

---

## Kompletní průvodce nasazením

Tento průvodce popisuje vše co bylo potřeba nastavit pro funkční nasazení USB Guardian v prostředí AXINETWORK. Slouží jako checklist pro nasazení v jiné organizaci.

### 1. Active Directory

#### Vytvoření skupiny USB-Guardian-Clients
```powershell
# Spustit na Domain Controlleru nebo stroji s RSAT
New-ADGroup `
    -Name "USB-Guardian-Clients" `
    -SamAccountName "USB-Guardian-Clients" `
    -GroupCategory Security `
    -GroupScope Global `
    -Description "Pocitace a uzivatele s pristupem na USB Guardian REST API"

# Přidat Domain Computers (všechny firemní PC automaticky)
Add-ADGroupMember `
    -Identity "USB-Guardian-Clients" `
    -Members (Get-ADGroup "Domain Computers")

# Přidat IT adminy pro přístup přes Swagger/browser
Add-ADGroupMember `
    -Identity "USB-Guardian-Clients" `
    -Members "jmeno.admina"
```

#### Registrace SPN pro gMSA účet
```powershell
# POVINNÉ – bez SPN Kerberos autentizace nefunguje
setspn -S HTTP/B-S-W-SQL-04 "AXINETWORK\gmsa-SQL$"
setspn -S HTTP/B-S-W-SQL-04.axinetwork.loc "AXINETWORK\gmsa-SQL$"

# Ověření
setspn -L "AXINETWORK\gmsa-SQL$"
# Výstup musí obsahovat:
#   HTTP/B-S-W-SQL-04
#   HTTP/B-S-W-SQL-04.axinetwork.loc
```

> **Pozor:** Bez SPN registrace API poběží ale Kerberos autentizace nebude fungovat – klienti se nepřihlásí správně (isAuthenticated: false).

---

### 2. SQL Server (B-S-W-SQL-04)

#### Databáze a uživatelé
```sql
-- Spustit v SSMS jako sysadmin

-- 1. Vytvoření databáze
-- Spustit: database/01_create_database.sql

-- 2. Oprava gMSA účtu (pozor na $ na konci!)
USE USBGuardian;
IF NOT EXISTS (SELECT name FROM sys.database_principals WHERE name = 'AXINETWORK\gmsa-SQL$')
BEGIN
    CREATE USER [AXINETWORK\gmsa-SQL$] FOR LOGIN [AXINETWORK\gmsa-SQL$];
END
ALTER ROLE db_datareader ADD MEMBER [AXINETWORK\gmsa-SQL$];
ALTER ROLE db_datawriter ADD MEMBER [AXINETWORK\gmsa-SQL$];

-- 3. Přidat IT admina pro vývoj/testování
CREATE USER [AXINETWORK\jmeno.admina] FOR LOGIN [AXINETWORK\jmeno.admina];
ALTER ROLE db_datareader ADD MEMBER [AXINETWORK\jmeno.admina];
ALTER ROLE db_datawriter ADD MEMBER [AXINETWORK\jmeno.admina];

-- 4. Vytvoření tabulek
-- Spustit: database/02_create_tables.sql

-- 5. Přidání SourceFile sloupce
-- Spustit: database/03_add_sourcefile.sql

-- 6. Seed dat – první whitelist verze a zařízení
INSERT INTO dbo.WhitelistVersions (Version, IssuedAt, ValidUntil, IssuedBy, IsActive)
VALUES ('2026-03-16-v1', GETUTCDATE(), DATEADD(DAY, 30, GETUTCDATE()), 'it-admin', 1);

INSERT INTO dbo.WhitelistDevices (VendorId, ProductId, SerialNumber, Description, ApprovedBy, IsActive)
VALUES ('KINGSTON', 'DATATRAVELER_2.0', '4B018CD154C9',
        'Kingston DataTraveler 14GB – IT oddeleni', 'it-admin', 1);
```

---

### 3. API Server (B-S-W-SQL-04)

#### Přenos souborů přes SCP (SMB je vypnuto – správně!)
```powershell
# Publishnout na dev stroji
cd D:\git\usb-guardian\server\USBGuardian.Api
dotnet publish -c Release -r win-x64 --self-contained -o "D:\deploy\USBGuardian.Api"

# Zkopírovat na server přes SCP (SSH port 22)
scp -r "D:\deploy\USBGuardian.Api" admintrnka@B-S-W-SQL-04:/C:/USBGuardian.Api
```

> **Proč ne SMB?** SQL Server má záměrně vypnuté File and Printer Sharing (SMB) ve firewallu. Je to správný bezpečnostní hardening – neměňte to. Vždy používejte SCP.

#### Konfigurace
```powershell
# Na serveru přes RDP nebo SSH
@'
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=USBGuardian;Integrated Security=true;TrustServerCertificate=true;"
  },
  "Authorization": {
    "AllowedGroups": [
      "AXINETWORK\\USB-Guardian-Clients",
      "AXINETWORK\\SQL Admins2"
    ]
  }
}
'@ | Set-Content -Path "C:\USBGuardian.Api\appsettings.local.json" -Encoding UTF8
```

#### Instalace Windows Service
```powershell
# Na serveru (RDP nebo SSH)
sc.exe create "USB Guardian API" `
    binPath="C:\USBGuardian.Api\USBGuardian.Api.exe" `
    obj="AXINETWORK\gmsa-SQL$" `
    start=auto

# Firewall – otevřít port 5050
New-NetFirewallRule `
    -DisplayName "USB Guardian API" `
    -Direction Inbound `
    -Protocol TCP `
    -LocalPort 5050 `
    -Action Allow

# Spustit
Start-Service "USB Guardian API"
Get-Service "USB Guardian API"
```

#### Update API (při nové verzi)
```powershell
# Na serveru – zastavit service
Stop-Service "USB Guardian API"

# Na dev stroji – zkopírovat nové soubory
scp "D:\deploy\USBGuardian.Api\USBGuardian.Api.exe" admintrnka@B-S-W-SQL-04:/C:/USBGuardian.Api/USBGuardian.Api.exe
scp "D:\deploy\USBGuardian.Api\USBGuardian.Api.dll" admintrnka@B-S-W-SQL-04:/C:/USBGuardian.Api/USBGuardian.Api.dll

# Na serveru – spustit
Start-Service "USB Guardian API"
```

---

### 4. Klientský PC (agent)

#### Složky a práva
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

# queue a sent – Users mohou zapisovat (agent běží jako SYSTEM v produkci)
# Pro vývoj (--console) potřebuje uživatel práva:
icacls "C:\ProgramData\USBGuardian\queue" /grant "Users:(OI)(CI)M"
icacls "C:\ProgramData\USBGuardian\sent"  /grant "Users:(OI)(CI)M"

# whitelist – Users jen čtou
icacls "C:\ProgramData\USBGuardian\whitelist" /grant "Users:(OI)(CI)R"
```

#### Lokální konfigurace
```powershell
# Vytvořit lokální přepis (necommituje se do gitu)
@'
{
  "whitelist": {
    "syncUrl": "http://B-S-W-SQL-04:5050"
  }
}
'@ | Set-Content `
    -Path "D:\git\usb-guardian\agent\USBGuardian\Config\agent.config.local.json" `
    -Encoding UTF8
```

#### Spuštění (vývojový režim)
```powershell
cd D:\git\usb-guardian\agent\USBGuardian
dotnet run -- --console
```

---

### 5. Ověření funkčnosti

#### Test konektivity
```powershell
# Test portu
Test-NetConnection -ComputerName B-S-W-SQL-04 -Port 5050

# Test API
Invoke-WebRequest `
    -Uri "http://B-S-W-SQL-04:5050/api/whitelist/version" `
    -UseDefaultCredentials `
    -AllowUnencryptedAuthentication | Select-Object -ExpandProperty Content
```

#### Test end-to-end sync
```powershell
# Vytvořit testovací soubor ve frontě
@'
{
  "Date": "2026-03-15",
  "Hostname": "NAZEVPC",
  "RecordCount": 1,
  "Records": [{
    "Timestamp": "2026-03-15T10:00:00Z",
    "Username": "testuser",
    "VendorId": "KINGSTON",
    "ProductId": "DATATRAVELER_2.0",
    "SerialNumber": "TEST123",
    "FriendlyName": "Test Device",
    "DeviceType": "UsbFlashDrive",
    "SizeBytes": 0,
    "SizeFormatted": "0 B",
    "FirmwareRevision": "",
    "PnpDeviceId": "TEST",
    "Action": "Allowed",
    "WhitelistVersion": "2026-03-16-v1"
  }]
}
'@ | Set-Content "C:\ProgramData\USBGuardian\queue\log_NAZEVPC_2026-03-15.json" -Encoding UTF8

# Spustit agenta – po minutě se soubor odešle a přesune do sent\
```

#### Ověření v SSMS
```sql
SELECT TOP 10 * FROM USBGuardian.dbo.Incidents ORDER BY ReceivedAt DESC;
-- Zkontrolovat: SourceFile sloupec obsahuje název souboru
```

---

### 6. Známé problémy a řešení

| Problém | Příčina | Řešení |
|---------|---------|--------|
| `Cannot find path C:\ProgramData\USBGuardian\queue` | Složka neexistuje | Spustit setup script (krok 4) |
| `Login failed for user AXINETWORK\trnkam` | Uživatel nemá přístup k DB | Přidat uživatele do db_datareader/db_datawriter |
| `isAuthenticated: false` | Chybí SPN registrace pro gMSA | Spustit setspn příkazy (krok 1) |
| `SMB Síťový název nelze nalézt` | SMB záměrně vypnuto na SQL serveru | Používat SCP místo UNC cest |
| `Access to path is denied` při přesunu do sent\ | Uživatel nemá práva na složku | Přidat `Users:(OI)(CI)M` práva |
| `YOUR_SQL_SERVER` ve startup logu | appsettings.local.json nenačten | Zkontrolovat cestu a obsah souboru |
| `no such column: PnpDeviceId` | Stará SQLite DB | Smazat incidents.db (přepnuto na file-based logging) |
