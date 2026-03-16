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
| A.8.15 – Logování | Audit trail bezpečnostních událostí | SQLite log: uživatel, PC, zařízení, čas, akce |
| A.5.26 – Reakce na incidenty | Evidence a hlášení incidentů | Strukturovaný log připravený pro SIEM/export |

### Praktický dopad na audit

Při bezpečnostním auditu (ISO 27001, NIS2, SOC2) USB Guardian poskytuje:

```
Důkaz 1: Existence whitelistu
  → whitelist.json s metadaty (kdo schválil, kdy, popis zařízení)

Důkaz 2: Log incidentů
  → incidents.db – každý pokus o připojení neznámého média
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
│  │(IOCTL) │  │(Toast/Email) │  │(SQLite)│  │                   │
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
- **Technologie:** SQLite (`Microsoft.Data.Sqlite`)
- **Soubor:** `C:\ProgramData\USBGuardian\logs\incidents.db`
- **Design:** Offline-first – data se ukládají lokálně, Fáze 3 je synchronizuje
- **Schéma:** viz sekce Datové schéma níže

---

## Datové schéma (SQLite)

### Tabulka Incidents

| Sloupec | Typ | Popis |
|---------|-----|-------|
| Id | INTEGER PK | Autoincrement |
| Timestamp | TEXT | ISO 8601 UTC |
| Hostname | TEXT | Název počítače |
| Username | TEXT | Přihlášený uživatel |
| VendorId | TEXT | Výrobce (KINGSTON, WD, ...) |
| ProductId | TEXT | Model (DATATRAVELER_2.0, ...) |
| SerialNumber | TEXT | Sériové číslo |
| FriendlyName | TEXT | Čitelný název z Windows |
| DeviceType | TEXT | UsbFlashDrive / UsbHdd / SdCard |
| SizeBytes | INTEGER | Kapacita v bajtech |
| FirmwareRevision | TEXT | Verze firmware |
| Action | TEXT | Warned / Blocked / TemporarilyAllowed |
| WhitelistVersion | TEXT | Verze whitelistu při incidentu |
| SentToServer | INTEGER | 0 = neodesláno, 1 = odesláno (Fáze 3) |

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
ONLINE  → sync whitelistu každých 15 min (Fáze 3)
          odesílání incidentů na server

OFFLINE → agent používá lokální cached whitelist
          incidenty se ukládají do SQLite (SentToServer=0)
          při obnovení připojení se odešlou (Fáze 3)

DEGRADED → whitelist expiroval
           chování dle policy.onExpiredWhitelist:
           "warn"         = stále varuje, medium funguje
           "block_new"    = blokuje neznámá media
           "strict_block" = blokuje vše
```

---

## Zabezpečení dat

### Adresář ProgramData
```
C:\ProgramData\USBGuardian\
  ├── whitelist\  → SYSTEM:F, Administrators:F, Users:R
  └── logs\       → SYSTEM:F, Administrators:F, Users:M
```

- Uživatelé **nemohou editovat whitelist** (pouze čtení)
- Uživatelé **mohou zapisovat do logs** (SQLite potřebuje write)
- IT admin a SYSTEM mají plný přístup

### Secrets v konfiguraci
- `agent.config.local.json` – lokální přepisy se secrets (NIKDY necommitovat)
- Fáze 2: Client Secret pro Graph API bude šifrován pomocí Windows DPAPI

---

## Fáze vývoje

### ✅ Fáze 1 – Dokončeno
- WMI monitoring USB/SD médií
- Whitelist (lokální JSON soubor, chráněný ACL)
- Windows Toast notifikace
- SQLite log incidentů (VendorId, ProductId, Serial, kapacita, firmware)
- Konfigurovatelný warn/block mód

### ✅ Fáze 2 – Dokončeno
- Block mode – FSCTL_LOCK_VOLUME přes DeviceIoControl
- Drive letter detection (dual WMI watcher – Win32_DiskDrive + Win32_LogicalDisk)
- Korelace přes DiskIndex + pending dictionary
- Fallback warn při selhání drive letter detekce
- PNPDeviceID uloženo pro každé zařízení
- Admin práva vyžadována pro block mode (Windows Service = SYSTEM, vývoj = elevated PS)

### 🔜 Fáze 3 – Plánováno
- Email notifikace přes Microsoft Graph API (Shared Mailbox, bez extra licence)
- Dočasný override kód od IT
- Instalační skript s UAC elevation

### 📋 Fáze 4 – Plánováno
- Centrální REST API server
- Synchronizace whitelistu z serveru
- Synchronizace incidentů na SQL Server
- RSA podpis whitelistu

### 📋 Fáze 5 – Plánováno
- Admin UI (React / Blazor)
- Dashboard se statistikami
- Správa whitelistu přes web
- Reporty a exporty

---

## Technologický stack

| Vrstva | Technologie | Verze |
|--------|------------|-------|
| Agent | C# / .NET | 8.0 |
| Hosting | Windows Service | – |
| Device detection | WMI / System.Management | – |
| Block mode | Win32 DeviceIoControl / P/Invoke | – |
| Local storage | SQLite / Microsoft.Data.Sqlite | 8.0 |
| Notifications | PowerShell Toast | – |
| Email (Fáze 3) | Microsoft Graph API / MSAL | – |
| Server (Fáze 4) | Python FastAPI nebo .NET | TBD |
| Database (Fáze 4) | SQL Server | TBD |
| Admin UI (Fáze 5) | React nebo Blazor | TBD |

---

## Struktura projektu

```
usb-guardian/
├── agent/
│   ├── USBGuardian.sln
│   └── USBGuardian/
│       ├── DeviceMonitor.cs        ← dual WMI watcher, parsování, DiskIndex korelace
│       ├── WhitelistChecker.cs     ← porovnání s whitelistem, cache, expirace
│       ├── PolicyEnforcer.cs       ← rozhodovací logika warn/block
│       ├── DeviceBlocker.cs        ← IOCTL lock + PnpDevice fallback
│       ├── NotificationService.cs  ← Windows Toast notifikace
│       ├── IncidentLogger.cs       ← SQLite log incidentů
│       ├── Program.cs              ← vstupní bod, DI konfigurace
│       ├── Models/
│       │   ├── DeviceInfo.cs       ← model zařízení (VID, PID, serial, kapacita, PnpDeviceId)
│       │   ├── Incident.cs         ← model incidentu
│       │   └── WhitelistEntry.cs   ← model záznamu whitelistu
│       └── Config/
│           └── agent.config.json   ← hlavní konfigurace
├── whitelist/
│   └── whitelist.json              ← ukázkový whitelist (kopírovat do ProgramData)
└── docs/
    └── architecture.md             ← tato dokumentace
```

---

## Obnova po havárii

Pro kompletní reinstalaci ze zdrojového kódu:

1. Klonovat repo: `git clone https://github.com/Anamax443/usb-guardian.git`
2. Vytvořit složky v ProgramData (viz README – Instalace)
3. Zkopírovat `whitelist\whitelist.json` do `C:\ProgramData\USBGuardian\whitelist\`
4. Editovat `agent\USBGuardian\Config\agent.config.json` – zkontrolovat cesty
5. Buildovat: `cd agent\USBGuardian && dotnet build`
6. Spustit: `dotnet run -- --console` (vývojový režim)
7. Nebo nainstalovat jako service (produkce) – viz README

Všechna konfigurace je v gitu. Jediné co není v gitu:
- `C:\ProgramData\USBGuardian\whitelist\whitelist.json` – záloha whitelistu
- `C:\ProgramData\USBGuardian\logs\incidents.db` – historická data incidentů

---

## Server – REST API (Fáze 3)

### Technologie
- **Framework:** ASP.NET Core 8.0 Web API
- **Hosting:** Windows Service
- **Databáze:** SQL Server 16 (`B-S-W-SQL-04`, databáze `USBGuardian`)
- **ORM:** Dapper (lightweight, rychlý)
- **Auth DB:** Windows Authentication přes gMSA (`AXINETWORK\gmsa-SQLS$`)
- **Auth API:** API klíč v hlavičce `X-Api-Key` (GUID per stanice)

### API Endpointy

| Method | Endpoint | Popis |
|--------|----------|-------|
| GET | `/health` | Health check – agent testuje dostupnost |
| GET | `/api/whitelist` | Agent stáhne aktuální whitelist |
| POST | `/api/whitelist/device` | IT admin přidá zařízení |
| POST | `/api/incidents/batch` | Agent odešle batch incidentů |
| GET | `/api/incidents/stats` | Statistiky pro dashboard |

### Databázové schéma

| Tabulka | Popis |
|---------|-------|
| `Whitelist` | Schválená média (VID, PID, Serial, metadata) |
| `WhitelistVersion` | Historie verzí – každá změna = nová verze |
| `Incidents` | Log incidentů ze všech stanic |
| `Agents` | Evidence stanic + API klíče |

### Offline provoz agenta
Agent testuje `/health` každých 15 minut.
- **Online:** sync whitelistu + odeslání čekajících incidentů
- **Offline:** lokální SQLite cache, `SentToServer = 0`
- **Reconnect:** automatický batch upload všech čekajících incidentů

---

## Tok dat systémem – detailní popis

### Přehled účastníků

```
[Firemní stanice]          [Síť]          [Server]              [Databáze]
  Agent                                    REST API              SQL Server
  (Windows Service)                        (Windows Service)     USBGuardian
  běží jako: SYSTEM        HTTPS:5050      běží jako: gmsa-SQL$  B-S-W-SQL-04
  lokální: SQLite                          Windows Auth
```

---

### Scénář 1 – Agent ONLINE, médium vloženo

```
1. Uživatel zasune USB médium
       ↓
2. Windows WMI event → DeviceMonitor detekuje zařízení
   → přečte: VendorId, ProductId, SerialNumber, kapacita, PnpDeviceId
       ↓
3. WhitelistChecker porovná s lokálním whitelist.json
   (lokální cache, platná 5 minut)
       ↓
   ┌─────────────────┐         ┌──────────────────────────┐
   │ NA WHITELISTU   │         │ NENÍ NA WHITELISTU        │
   │ → log "Allowed" │         │ → PolicyEnforcer          │
   │ → nic dalšího   │         │   warn: Toast notifikace  │
   └─────────────────┘         │   block: Disable-PnpDevice│
                               │ → IncidentLogger          │
                               │   uloží do SQLite         │
                               │   SentToServer = 0        │
                               └──────────────────────────┘
       ↓
4. WhitelistSync (background thread, každých 15 min)
   → GET http://api-server:5050/api/heartbeat
     ?hostname=PC-01&whitelistVersion=2026-03-16-v1
   ← { whitelistUpdateAvailable: true, currentVersion: "v2" }
       ↓
5. Pokud nová verze whitelistu:
   → GET http://api-server:5050/api/whitelist
   ← JSON s novým whitelistem
   → uloží do lokálního whitelist.json (přepíše)
       ↓
6. IncidentSync (background thread)
   → vezme všechny incidenty kde SentToServer = 0
   → POST http://api-server:5050/api/incidents
     { hostname, agentVersion, incidents: [...] }
   ← { accepted: 3 }
   → označí incidenty jako SentToServer = 1
```

---

### Scénář 2 – Agent OFFLINE (hotspot, domácí síť)

```
1. USB médium vloženo → stejná detekce jako online
       ↓
2. WhitelistChecker použije lokální cached whitelist.json
   → porovnání proběhne lokálně bez sítě
       ↓
3. Incident uložen do SQLite, SentToServer = 0
       ↓
4. WhitelistSync se pokouší každých 15 min:
   → ping na api-server selže (timeout)
   → agent zůstane v offline stavu
   → whitelist cache stárne
       ↓
5. Whitelist expiruje (ValidUntil):
   → dle policy.onExpiredWhitelist:
      "warn"         → stále funguje, jen loguje
      "block_new"    → nová neznámá média blokuje
      "strict_block" → blokuje vše
       ↓
6. Notebook se vrátí na firemní síť:
   → ConnectivityChecker detekuje dostupnost API
   → okamžitý sync whitelistu
   → batch upload všech pending incidentů
```

---

### Scénář 3 – IT admin přidá nové médium do whitelistu

```
IT admin (SSMS nebo budoucí Admin UI)
       ↓
POST /api/whitelist/devices
{ vendorId, productId, serialNumber, description, approvedBy }
       ↓
REST API (běží jako AXINETWORK\gmsa-SQL$)
   → ověří duplicitu
   → INSERT do WhitelistDevices
   → vytvoří novou WhitelistVersion (verze bump)
   → SQL Server zapíše přes gMSA účet
       ↓
Při příštím heartbeatu agentů:
   → všechny online agenty detekují novou verzi
   → stáhnou aktualizovaný whitelist
   → uloží lokálně
       ↓
Offline agenti:
   → dostanou novou verzi při příštím připojení
   → do té doby fungují se starou cached verzí
```

---

### Identita a oprávnění – kdo co smí

```
┌─────────────────────────────────────────────────────────────────┐
│ Komponenta          │ Účet                │ Oprávnění           │
├─────────────────────┼─────────────────────┼─────────────────────┤
│ Agent (Windows Svc) │ SYSTEM              │ lokální PC, WMI     │
│                     │ → na síti jako:     │ PnpDevice disable   │
│                     │ AXINETWORK\PC-01$   │ HTTP → REST API     │
├─────────────────────┼─────────────────────┼─────────────────────┤
│ REST API (Win Svc)  │ AXINETWORK\         │ db_datareader       │
│                     │ gmsa-SQL$           │ db_datawriter       │
│                     │                     │ POUZE USBGuardian   │
├─────────────────────┼─────────────────────┼─────────────────────┤
│ IT admin (Swagger)  │ AXINETWORK\         │ db_datareader       │
│                     │ SQL Admins2         │ db_datawriter       │
│                     │                     │ POUZE USBGuardian   │
├─────────────────────┼─────────────────────┼─────────────────────┤
│ SQL Server          │ –                   │ hostuje DB          │
│ B-S-W-SQL-04        │                     │ pouze Windows Auth  │
└─────────────────────┴─────────────────────┴─────────────────────┘
```

### AD skupina USB-Guardian-Clients

```
Skupina:  AXINETWORK\USB-Guardian-Clients
Typ:      Security, Global
Členové:  Domain Computers (všechny firemní stroje automaticky)

Vytvoření:
  New-ADGroup -Name "USB-Guardian-Clients" -GroupCategory Security -GroupScope Global
  Add-ADGroupMember -Identity "USB-Guardian-Clients" -Members (Get-ADGroup "Domain Computers")

Jak funguje:
  Nový stroj připojí do domény
    → automaticky v Domain Computers
    → automaticky dostane přístup na REST API
    → žádná ruční správa

  Vyřazení stroje:
    → odebrat z Domain Computers nebo přímo z USB-Guardian-Clients
    → stroj ztratí přístup na API
    → agent stále funguje offline s cached whitelistem

REST API ověřuje příslušnost ke skupině:
  → AXINETWORK\USB-Guardian-Clients (firemní stroje)
  → AXINETWORK\SQL Admins2 (IT admini přes Swagger)
  → ostatní → HTTP 401 Unauthorized
```

---

### Komunikační kanály

```
Agent → REST API:
  Protokol:    HTTP (HTTPS v produkci s certifikátem)
  Port:        5050
  Auth:        Windows Authentication (Negotiate/Kerberos)
  Směr:        jednosměrně agent → server
  Frekvence:   heartbeat každých 15 min
               incident batch při každém online eventu

REST API → SQL Server:
  Protokol:    TDS (SQL Server Native)
  Port:        1433
  Auth:        Windows Authentication (gMSA účet)
  Směr:        jednosměrně API → DB
  Poznámka:    gMSA heslo rotuje automaticky (AD spravuje)

Agent → lokální SQLite:
  Protokol:    přímý soubor
  Umístění:    C:\ProgramData\USBGuardian\logs\incidents.db
  Účel:        offline buffer, nikdy se neztratí data
```

---

### Co se stane při výpadku serveru

```
REST API server nedostupný:
  → Agent funguje normálně (offline-first design)
  → Incidenty se hromadí v SQLite (SentToServer = 0)
  → Whitelist funguje z cache (platný do ValidUntil)
  → Po obnovení serveru: automatický sync bez zásahu

SQL Server nedostupný:
  → REST API vrátí HTTP 500
  → Agent přejde do offline módu
  → Stejný efekt jako výpadek API serveru

Výpadek agenta (restart PC):
  → Windows Service se automaticky restartuje
  → SQLite data jsou persistentní (přežijí restart)
  → Při startu: okamžitý sync pokud je online
```
