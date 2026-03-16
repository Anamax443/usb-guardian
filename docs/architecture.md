# USB Guardian – Architektura systému

## Přehled

USB Guardian je bezpečnostní nástroj pro monitoring paměťových médií (USB flashky, SD karty, USB disky) na firemních počítačích. Každé médium musí být schváleno IT oddělením a zapsáno do whitelistu.

## Fáze vývoje

| Fáze | Popis | Stav |
|------|-------|------|
| 1 | Windows agent – detekce + warn + toast + SQLite log | ✅ Aktuální |
| 2 | Block mode + email notifikace (Graph API) | 🔜 Připraveno v configu |
| 3 | Centrální server + whitelist sync | 📋 Plánováno |
| 4 | Admin UI – dashboard + statistiky | 📋 Plánováno |

## Komponenty (Fáze 1)

```
USBGuardian (Windows Service)
├── DeviceMonitor       – WMI listener, detekuje připojení médií
├── WhitelistChecker    – čte whitelist.json, porovnává VID/PID/Serial
├── PolicyEnforcer      – rozhoduje o akci (warn/block) dle konfigurace
├── NotificationService – Windows Toast notifikace pro uživatele
└── IncidentLogger      – ukládá incidenty do SQLite databáze
```

## Identifikace zařízení

Každé médium je identifikováno třemi parametry:

- **VID** (Vendor ID) – výrobce, např. `0951` = Kingston
- **PID** (Product ID) – model zařízení
- **Serial Number** – konkrétní fyzický kus (unikátní)

Klíč pro porovnání: `VID:PID:SERIAL` (uppercase)

## Offline provoz

Agent funguje plně offline s lokální kopií whitelistu. Whitelist má datum expirace – po expiraci agent přejde do degraded módu dle konfigurace `onExpiredWhitelist`.

## Konfigurace

Veškeré chování řídí `Config/agent.config.json`:
- `policy.mode` – `warn` nebo `block`
- `policy.onExpiredWhitelist` – chování po expiraci whitelistu
- `notifications.toast.enabled` – zapnutí/vypnutí toast notifikací
- `logging.dbPath` – cesta k SQLite databázi

## Spuštění pro vývoj

```powershell
cd agent/USBGuardian
dotnet run -- --console
```

## Instalace jako Windows Service

```powershell
dotnet publish -c Release -r win-x64 --self-contained
sc create "USB Guardian" binPath="C:\USBGuardian\USBGuardian.exe"
sc start "USB Guardian"
```
