# USB Guardian

Bezpečnostní nástroj pro monitoring paměťových médií na firemních počítačích.
Každé USB médium, SD karta nebo USB disk musí být schváleno IT oddělením a zapsáno do whitelistu.

## Rychlý start

### Požadavky
- Windows 10/11
- .NET 8.0 SDK
- Visual Studio Code nebo Visual Studio 2022

### Spuštění (vývojový režim)

```powershell
# 1. Klonovat repo
git clone https://github.com/vase-firma/usb-guardian.git
cd usb-guardian

# 2. Spustit agenta v konzoli (ne jako service)
cd agent/USBGuardian
dotnet run -- --console
```

### Přidat médium do whitelistu

Editujte `whitelist/whitelist.json` – přidejte záznam:

```json
{
  "vendorId": "0951",
  "productId": "1666",
  "serialNumber": "ABC123456",
  "description": "Kingston 32GB – Jan Novák – IT",
  "approvedAt": "2026-03-16T00:00:00Z",
  "approvedBy": "it-admin"
}
```

**Jak zjistit VID/PID/Serial?**
```powershell
# PowerShell – zobrazí připojená USB média
Get-WmiObject Win32_DiskDrive | Where-Object {$_.InterfaceType -eq "USB"} |
  Select-Object Caption, PNPDeviceID, SerialNumber | Format-List
```

## Struktura projektu

```
usb-guardian/
├── agent/USBGuardian/          ← C# .NET Windows Service
│   ├── DeviceMonitor.cs        ← WMI listener
│   ├── WhitelistChecker.cs     ← porovnání s whitelistem
│   ├── PolicyEnforcer.cs       ← warn / block logika
│   ├── NotificationService.cs  ← Windows Toast notifikace
│   ├── IncidentLogger.cs       ← SQLite log incidentů
│   ├── Program.cs              ← vstupní bod + DI konfigurace
│   ├── Models/                 ← datové modely
│   └── Config/
│       └── agent.config.json   ← hlavní konfigurace
├── whitelist/
│   └── whitelist.json          ← seznam povolených médií
└── docs/
    └── architecture.md         ← technická dokumentace
```

## Konfigurace

Editujte `agent/USBGuardian/Config/agent.config.json`:

| Klíč | Hodnoty | Popis |
|------|---------|-------|
| `policy.mode` | `warn` / `block` | Warn = varovat, Block = zablokovat (Fáze 2) |
| `policy.onExpiredWhitelist` | `warn` / `block_new` / `strict_block` | Chování po expiraci whitelistu |
| `notifications.toast.enabled` | `true` / `false` | Zobrazení Toast notifikace |

## Fáze vývoje

- ✅ **Fáze 1** – Detekce + warn + Toast + SQLite log
- 🔜 **Fáze 2** – Block mode + Email notifikace (Microsoft Graph API)
- 📋 **Fáze 3** – Centrální server + whitelist sync
- 📋 **Fáze 4** – Admin UI + statistiky + reporty

## Bezpečnostní poznámky

- `agent.config.local.json` se **nikdy necommituje** (obsahuje secrets)
- Whitelist by měl být v produkci **kryptograficky podepsán** (Fáze 3)
- Wildcard záznamy (bez SerialNumber) jsou bezpečnostní riziko

---
*USB Guardian – Fáze 1 | IT Security Tool*
