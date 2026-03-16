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
      "description": "Popis – kdo, co, proč",
      "approvedAt": "2026-03-16T00:00:00Z",
      "approvedBy": "it-admin"
    }
  ]
}
```

- `signature` – zatím prázdné, Fáze 3 přidá RSA podpis (nelze podvrhnout offline)
- `validUntil` – expirace whitelistu, doporučeno 30 dní

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
