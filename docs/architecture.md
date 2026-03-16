# USB Guardian – Technická dokumentace architektury

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
- **Metoda:** `FSCTL_DISMOUNT_VOLUME` + `FSCTL_LOCK_VOLUME`
- **Efekt:** Médium je vidět v systému ale nelze číst ani zapisovat
- **Reverzibilní:** Odblokování zavřením handle – bez nutnosti odpojit zařízení
- **Vyžaduje:** Admin práva (Windows Service musí běžet pod privilegovaným účtem)
- **Fallback:** Pokud lock selže (médium aktivně používáno) → přejde na warn

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
- Block mode – DeviceIoControl (FSCTL_DISMOUNT + FSCTL_LOCK)
- Drive letter detection (WMI asociace)
- Fallback warn při selhání blokování

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
│       ├── DeviceMonitor.cs        ← WMI listener, parsování, drive letters
│       ├── WhitelistChecker.cs     ← porovnání s whitelistem, cache, expirace
│       ├── PolicyEnforcer.cs       ← rozhodovací logika warn/block
│       ├── DeviceBlocker.cs        ← blokování přes DeviceIoControl (P/Invoke)
│       ├── NotificationService.cs  ← Windows Toast notifikace
│       ├── IncidentLogger.cs       ← SQLite log incidentů
│       ├── Program.cs              ← vstupní bod, DI konfigurace
│       ├── Models/
│       │   ├── DeviceInfo.cs       ← model zařízení (VID, PID, serial, kapacita, drive letters)
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
