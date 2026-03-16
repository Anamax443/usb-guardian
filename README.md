# USB Guardian

Bezpečnostní nástroj pro monitoring paměťových médií na firemních počítačích.
Každé USB médium, SD karta nebo USB disk musí být schváleno IT oddělením a zapsáno do whitelistu.

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

Soubor: `agent\USBGuardian\Config\agent.config.json`

| Klíč | Hodnoty | Popis |
|------|---------|-------|
| `policy.mode` | `warn` / `block` | Warn = varovat, Block = zablokovat (Fáze 2) |
| `policy.maxOfflineAgeDays` | číslo | Po kolika dnech offline přejít do degraded módu |
| `policy.onExpiredWhitelist` | `warn` / `block_new` / `strict_block` | Chování po expiraci whitelistu |
| `notifications.toast.enabled` | `true` / `false` | Windows Toast notifikace |
| `notifications.toast.contactMessage` | text | Zpráva zobrazená uživateli |
| `notifications.email.enabled` | `true` / `false` | Email notifikace (Fáze 2) |
| `logging.dbPath` | cesta | Cesta k SQLite databázi incidentů |
| `logging.logLevel` | `Debug` / `Information` / `Warning` | Úroveň logování |

## Datové úložiště

### Whitelist
```
C:\ProgramData\USBGuardian\whitelist\whitelist.json
```
- Pouze IT admin má write přístup (Administrators + SYSTEM)
- Uživatelé mají pouze read přístup
- Obsahuje seznam schválených médií s metadaty

### Log incidentů
```
C:\ProgramData\USBGuardian\logs\incidents.db
```
- SQLite databáze – jeden soubor, nulová instalace
- Funguje plně offline
- Ve Fázi 3 bude synchronizována na centrální SQL Server
- Uložená data: čas, PC, uživatel, zařízení (výrobce, model, serial, kapacita, firmware), akce

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
