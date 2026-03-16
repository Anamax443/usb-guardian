# USB Guardian

Bezpečnostní nástroj pro monitoring paměťových médií na firemních počítačích.
Každé USB médium, SD karta nebo USB disk musí být schváleno IT oddělením a zapsáno do whitelistu.

## Regulatorní soulad

USB Guardian byl navržen jako technické opatření splňující požadavky:

- **NIS2** (Směrnice EU 2022/2555) – čl. 21 odst. 2: bezpečnost dodavatelského řetězce, základní kybernetická hygiena, hlášení incidentů
- **Zákon č. 181/2014 Sb.** o kybernetické bezpečnosti + Vyhláška č. 82/2018 Sb. – § 14 řízení přístupů, § 16 ochrana před škodlivým kódem
- **ISO/IEC 27001:2022** – kontroly A.8.12 (prevence úniku dat), A.7.10 (paměťová média), A.8.15 (logování), A.5.26 (reakce na incidenty)

Podrobný popis compliance viz [`docs/architecture.md`](docs/architecture.md) – sekce *Regulatorní kontext*.

---

## Stav projektu

| Fáze | Popis | Stav |
|------|-------|------|
| 1 | Windows agent – detekce + warn + Toast + SQLite log | ✅ Hotovo |
| 2 | Block mode – IOCTL lock, drive letter detection, admin práva | ✅ Hotovo |
| 3 | Email notifikace (Microsoft Graph API) + instalační skript | 🔜 Plánováno |
| 4 | Centrální server + whitelist sync + SQL Server | 📋 Plánováno |
| 5 | Admin UI – dashboard + statistiky + reporty | 📋 Plánováno |

## Jak to funguje

```
[USB/SD médium připojeno]
         ↓
[WMI event – Windows detekuje zařízení]
         ↓
[Agent přečte VendorId, ProductId, SerialNumber, kapacitu]
         ↓
[Porovnání s whitelistem v C:\ProgramData\USBGuardian\whitelist\whitelist.json]
         ↓                          ↓
[Médium na whitelistu]     [Médium NENÍ na whitelistu]
[Logováno jako povoleno]   [Windows Toast notifikace]
                           [Incident uložen do SQLite]
                           [Email alert – Fáze 2]
```

## Požadavky

- Windows 10/11 (64-bit)
- .NET 8.0 SDK
- Visual Studio Code
- Admin práva pro instalaci složek v C:\ProgramData

## Instalace

### 1. Vytvoření složek a nastavení práv (jako Administrator)

```powershell
New-Item -ItemType Directory -Force -Path "C:\ProgramData\USBGuardian\whitelist"
New-Item -ItemType Directory -Force -Path "C:\ProgramData\USBGuardian\logs"

icacls "C:\ProgramData\USBGuardian" /inheritance:r
icacls "C:\ProgramData\USBGuardian" /grant "SYSTEM:(OI)(CI)F"
icacls "C:\ProgramData\USBGuardian" /grant "Administrators:(OI)(CI)F"
icacls "C:\ProgramData\USBGuardian" /grant "Users:(OI)(CI)R"
icacls "C:\ProgramData\USBGuardian\logs" /grant "Users:(OI)(CI)M"
```

### 2. Zkopírovat whitelist

```powershell
Copy-Item whitelist\whitelist.json "C:\ProgramData\USBGuardian\whitelist\"
```

### 3. Spuštění (vývojový režim)

```powershell
cd agent\USBGuardian
dotnet run -- --console
```

> **Block mode vyžaduje admin práva.** Pro testování blokování spusťte v elevated PowerShell:
> ```powershell
> Start-Process powershell -Verb RunAs -ArgumentList "-NoExit -Command `"cd 'D:\git\usb-guardian\agent\USBGuardian'; dotnet run -- --console`""
> ```
> V produkci (Windows Service) agent běží jako SYSTEM – admin práva jsou automatická.

### 4. Instalace jako Windows Service (produkce)

```powershell
dotnet publish -c Release -r win-x64 --self-contained
sc create "USB Guardian" binPath="C:\USBGuardian\USBGuardian.exe"
sc start "USB Guardian"
```

## Správa whitelistu

### Přidání nového média

1. Zjistit identifikátory zařízení:

```powershell
Get-WmiObject Win32_DiskDrive |
  Where-Object { $_.InterfaceType -eq "USB" } |
  Select-Object Caption, PNPDeviceID, SerialNumber |
  Format-List
```

2. Editovat `C:\ProgramData\USBGuardian\whitelist\whitelist.json` (vyžaduje admin práva):

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
      "description": "Kingston DataTraveler 14GB – IT oddělení",
      "approvedAt": "2026-03-16T00:00:00Z",
      "approvedBy": "it-admin"
    }
  ]
}
```

### Identifikace zařízení

Agent podporuje dva formáty Windows PNPDeviceID:

| Formát | Příklad | Použití |
|--------|---------|---------|
| USB (hex) | `VID_0951 / PID_1666` | Některé USB huby a adaptéry |
| USBSTOR (text) | `VEN_KINGSTON / PROD_DATATRAVELER_2.0` | Standardní USB flash disky a HDD |

Klíč pro whitelist: `VendorId:ProductId:SerialNumber` (case-insensitive)

### Wildcard záznam (celá řada bez sériového čísla)

```json
{
  "vendorId": "KINGSTON",
  "productId": "DATATRAVELER_2.0",
  "serialNumber": "",
  "description": "POZOR: Wildcard – platí pro VŠECHNY kusy tohoto modelu"
}
```

⚠️ Wildcard je bezpečnostní riziko – používat pouze výjimečně.

## Konfigurace

### Přístup bez hardcoded hodnot

Projekt neobsahuje žádné hardcoded názvy domén, serverů ani skupin. Vše je konfigurovatelné – projekt lze bezpečně publikovat jako open source.

**Agent** – `agent\USBGuardian\Config\agent.config.json`:

| Klíč | Výchozí | Popis |
|------|---------|-------|
| `policy.mode` | `warn` | `warn` = varovat, `block` = zablokovat |
| `policy.maxOfflineAgeDays` | `30` | Max stáří whitelistu offline |
| `policy.onExpiredWhitelist` | `warn` | `warn` / `block_new` / `strict_block` |
| `whitelist.syncUrl` | `http://YOUR_API_SERVER:5050` | URL API serveru |
| `whitelist.syncIntervalMinutes` | `15` | Interval synchronizace |
| `notifications.toast.enabled` | `true` | Windows Toast notifikace |
| `notifications.toast.contactMessage` | text | Zpráva uživateli |
| `logging.queuePath` | `C:\ProgramData\USBGuardian\queue` | Složka pro denní log soubory |

**API Server** – `server\USBGuardian.Api\appsettings.json`:

| Klíč | Popis |
|------|-------|
| `ConnectionStrings.DefaultConnection` | SQL Server connection string (Integrated Security) |
| `Authorization.AllowedGroups` | AD skupiny s přístupem k API |
| `Urls` | Port API serveru |

### Lokální přepisy (necommitují se)

```
agent/USBGuardian/Config/agent.config.local.json      ← přepis pro agenta
server/USBGuardian.Api/appsettings.local.json          ← přepis pro API server
```

Viz `*.example` soubory jako šablonu.

## Datové úložiště

### Whitelist
```
C:\ProgramData\USBGuardian\whitelist\whitelist.json
```
- Pouze IT admin má write přístup (Administrators + SYSTEM)
- Automaticky synchronizován ze serveru každých 15 minut

### Log fronty (denní soubory)
```
C:\ProgramData\USBGuardian\queue\log_HOSTNAME_2026-03-16.json
```
- Denní JSON soubory – jedno připojení = jeden záznam
- Loguje VŠE: povolená i nepovolená média
- Po úspěšném odeslání na server soubor smazán
- Při výpadku sítě soubor zůstane a sync zkusí příště
- Soubory starší 3 měsíce automaticky smazány

### Centrální databáze
```
SQL Server → databáze USBGuardian
  → tabulka Incidents  (všechna připojení)
  → tabulka WhitelistDevices (schválená média)
  → tabulka Computers  (evidence stanic)
```

## Struktura projektu

```
usb-guardian/
├── agent/
│   ├── USBGuardian.sln
│   └── USBGuardian/
│       ├── DeviceMonitor.cs        ← WMI listener, parsování zařízení
│       ├── WhitelistChecker.cs     ← porovnání s whitelistem, cache, expirace
│       ├── PolicyEnforcer.cs       ← rozhodovací logika warn/block
│       ├── NotificationService.cs  ← Windows Toast notifikace
│       ├── IncidentLogger.cs       ← SQLite log incidentů
│       ├── Program.cs              ← vstupní bod, DI konfigurace
│       ├── Models/
│       │   ├── DeviceInfo.cs       ← model zařízení (VID, PID, serial, kapacita)
│       │   ├── Incident.cs         ← model incidentu
│       │   └── WhitelistEntry.cs   ← model záznamu whitelistu
│       └── Config/
│           └── agent.config.json   ← hlavní konfigurace
├── whitelist/
│   └── whitelist.json              ← ukázkový whitelist (kopírovat do ProgramData)
└── docs/
    └── architecture.md             ← technická dokumentace
```

## Bezpečnostní doporučení

- `agent.config.local.json` se **nikdy necommituje** – obsahuje secrets (email, API klíče)
- Whitelist v `C:\ProgramData` je chráněn ACL – uživatelé nemohou editovat
- Ve Fázi 3 bude whitelist kryptograficky podepsán (RSA) – nelze podvrhnout na offline stroji
- Doporučeno spouštět Windows Service pod účtem `NETWORK SERVICE` nebo dedikovaným service accountem

---
*USB Guardian – Fáze 1 dokončena | IT Security Tool*
