# USB Guardian

Bezpečnostní nástroj pro monitoring paměťových médií na firemních počítačích.
Každé USB médium, SD karta nebo USB disk musí být schváleno IT oddělením a zapsáno do whitelistu.

## Stav projektu

| Fáze | Popis | Stav |
|------|-------|------|
| 1 | Windows agent – detekce + warn + Toast + log | ✅ Hotovo |
| 2 | Block mode – DeviceIoControl IOCTL | ✅ Hotovo |
| 3 | Server – ASP.NET Core API, SQL Server, gMSA, Kerberos | ✅ Hotovo |
| 4 | RSA-4096 podpis whitelistu – fail-secure | ✅ Hotovo |
| 5 | Incident queue – bounded Channel, jitter, retry 503 | ✅ Hotovo |
| 6 | HTTPS – Kestrel TLS, self-signed cert, TLS validace agenta | ✅ Hotovo |
| 7 | Log role tagging – [KLIENT]/[SERVER] prefix + timestamp | ✅ Hotovo |
| – | Toast Privilege Separation (SYSTEM → user pipe) | 🔜 Pending |
| – | Task Scheduler watchdog | 🔜 Pending |
| – | Admin UI – dashboard, whitelist správa | 🔜 Pending |
| – | Email notifikace (Microsoft Graph API) | 🔜 Pending |

## Architektura

Viz [docs/architecture.md](docs/architecture.md)

## Stack

| Komponenta | Technologie |
|-----------|-------------|
| Agent | C# .NET 8, Windows Service |
| API server | ASP.NET Core, port 5050 (HTTP) / 5443 (HTTPS) |
| Databáze | SQL Server – `B-S-W-SQL-04` |
| Autentizace | Windows Auth – Kerberos / Negotiate |
| Service účet | gMSA `AXINETWORK\gmsa-SQL$` |
| AD skupiny | `USB-Guardian-Clients`, `SQL Admins2` |

## Konfigurace

### Agent (`agent/USBGuardian/Config/`)

| Soubor | Popis |
|--------|-------|
| `agent.config.json` | Šablona s placeholdery (v repo) |
| `agent.config.local.json` | Reálné hodnoty – **gitignored**, nutno vytvořit ručně |

### Server (`server/USBGuardian.Api/`)

| Soubor | Popis |
|--------|-------|
| `appsettings.json` | Šablona s placeholdery (v repo) |
| `appsettings.local.json` | Reálné hodnoty – **gitignored**, nutno vytvořit ručně |

### appsettings.local.json (příklad)

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=tcp:B-S-W-SQL-04,1433;Database=USBGuardian;Integrated Security=true;TrustServerCertificate=true;"
  },
  "Authorization": {
    "AllowedGroups": [
      "AXINETWORK\\USB-Guardian-Clients",
      "AXINETWORK\\SQL Admins2"
    ]
  }
}
```

## Logování

Každý výstup v konzoli obsahuje:
- **Timestamp** – `HH:mm:ss`
- **Role tag** – `[KLIENT]` pro agenta, `[SERVER]` pro API
- **Úroveň** – `info`, `warn`, `fail`, `crit`

```
11:39:33 [KLIENT] info: USBGuardian.DeviceMonitor[0]
      USB Guardian zahájen monitoring zařízení
11:39:33 [SERVER] info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
```

Implementováno přes vlastní `RoleTagFormatter` – viz `RoleTagFormatter.cs` v obou projektech.

## Rychlý start

### Požadavky

- Windows 10/11 (64-bit)
- .NET 8.0 SDK
- Přístup na SQL Server `B-S-W-SQL-04` (port 1433)
- Členství v AD skupině `USB-Guardian-Clients`

### Spuštění agenta (vývojový režim)

```powershell
cd agent\USBGuardian
dotnet run -- --console
```

### Spuštění serveru (vývojový režim)

```powershell
# 1. Připravit lokální konfiguraci
copy server\USBGuardian.Api\appsettings.json server\USBGuardian.Api\appsettings.local.json
# Editovat appsettings.local.json – doplnit reálné hodnoty

# 2. Spustit
cd server\USBGuardian.Api
dotnet run
```

### Instalace jako Windows Service (produkce)

```powershell
# Agent
dotnet publish agent\USBGuardian -c Release -r win-x64 --self-contained
sc create "USB Guardian" binPath="C:\USBGuardian\USBGuardian.exe"
sc start "USB Guardian"

# Server
dotnet publish server\USBGuardian.Api -c Release -r win-x64 --self-contained
sc create "USB Guardian API" binPath="C:\USBGuardianAPI\USBGuardian.Api.exe"
sc start "USB Guardian API"
```

## Bezpečnost

- Whitelist podepisován RSA-4096 – agent odmítne podvrhnutý whitelist
- TLS validace certifikátu serveru (vypnutelné pro vývoj: `tls:validateServerCertificate=false`)
- Windows Auth (Kerberos) – agenti se autentizují strojovým účtem
- gMSA účet pro přístup k SQL – žádné heslo v konfiguraci
- `appsettings.local.json` a `agent.config.local.json` jsou gitignored

## Repo struktura

```
usb-guardian/
├── agent/
│   └── USBGuardian/           # .NET 8 Windows Service agent
│       ├── Config/            # agent.config.json (šablona)
│       ├── RoleTagFormatter.cs # konzolový formatter [KLIENT]
│       └── Program.cs
├── server/
│   └── USBGuardian.Api/       # ASP.NET Core API
│       ├── Controllers/       # Incidents, Whitelist, Heartbeat
│       ├── Data/              # EF Core, AppDbContext
│       ├── Models/            # API + DB modely
│       ├── Queue/             # IncidentQueueWorker
│       ├── RoleTagFormatter.cs # konzolový formatter [SERVER]
│       ├── appsettings.json   # šablona
│       └── Program.cs
├── scripts/
│   ├── New-Certificate.ps1    # generování self-signed cert
│   └── Install-Certificate.ps1
├── docs/
│   └── architecture.md
└── README.md
```
