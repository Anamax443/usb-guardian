# USB Guardian

*🇨🇿 Čeština · [🇬🇧 English](README.en.md)*

Bezpečnostní nástroj pro monitoring paměťových médií (USB flash, SD karty, USB disky)
na firemních počítačích. Každé médium musí být schváleno IT oddělením a zapsáno do
centrálního whitelistu. Nepovolená média jsou varována nebo zablokována. Navrženo jako
technické opatření pro **NIS2 / zákon 181/2014 Sb. / ISO 27001**.

> **Portabilita:** žádné firemně specifické hodnoty (server, doména, skupiny, účty) nejsou
> v kódu — vše je v `*.local.json` (gitignored). Nasazení do jiné firmy = úprava konfigurace,
> ne kódu.

## Stav projektu

| Fáze | Popis | Stav |
|------|-------|------|
| 1 | Agent – WMI detekce, warn mode, Toast | ✅ |
| 2 | Block mode – DeviceIoControl IOCTL | ✅ |
| 3 | API server – ASP.NET Core, SQL Server, gMSA, Kerberos | ✅ |
| 4 | RSA-4096 podpis whitelistu – fail-secure | ✅ |
| 5 | Incident queue – bounded Channel, jitter, retry 503 | ✅ |
| 6 | HTTPS – Kestrel TLS, validace certifikátu agentem | ✅ |
| 7 | Log role tagging `[KLIENT]`/`[SERVER]` | ✅ |
| 8 | **Lokální admin konzole agenta** (HttpListener, loopback, read-only) | ✅ |
| 9 | **Serverová admin konzole** (Blazor na .213): Přehled, Stanice, Nastavení, Dokumentace | ✅ |
| 10 | **AD sync** – inventář stanic z Active Directory + reconciliation (kdo nemá agenta) | ✅ |
| – | Vzdálená instalace agenta na stanice bez něj (WinRM) | 🔜 Plánováno |
| – | Webová správa whitelistu + podpisový workflow | 🔜 Plánováno |
| – | gMSA pro konzoli, dedikovaná skupina `USB-Guardian-Admins`, HTTPS konzole | 🔜 Hardening |
| – | Toast Privilege Separation, Email notifikace (Graph) | 🔜 Plánováno |

## Architektura

Tři komponenty, push model (agent → API), dvouvrstvý server (operativa na app serveru, DB = úložiště):

```
[Klientská stanice]                  [App server .213]            [DB server SQL-04]
┌────────────────────┐               ┌────────────────────┐       ┌──────────────────┐
│ Agent (.NET8 svc)  │               │ Admin konzole       │       │ SQL Server       │
│  WMI detekce       │  push  HTTPS  │ (Blazor :4200)      │ read/ │ DB USBGuardian   │
│  whitelist check   ├──────────────►│  Přehled / Stanice  │ write │  Incidents       │
│  warn / block      │   ┌───────────┤  AD sync ◄── AD     ├──────►│  Computers       │
│  lokální konzole   │   │  push     │  Nastavení / Docs   │       │  WhitelistDevices│
│  (loopback :5080)  │   │           └────────────────────┘       │  WhitelistVersions│
└────────────────────┘   │           ┌────────────────────┐       └──────────────────┘
                         └──────────►│ API (:5050/:5443)   ├──read/write──────▲
                                     │  příjem incidentů    │                  │
                                     │  whitelist distribuce│──────────────────┘
                                     └────────────────────┘
```

Detail viz [docs/architecture.md](docs/architecture.md). Předávka a živý stav: [HANDOFF.md](HANDOFF.md).

## Komponenty

| Komponenta | Technologie | Kde běží |
|-----------|-------------|----------|
| **Agent** | C# .NET 8, Windows Service | každá stanice (SYSTEM) |
| **API** | ASP.NET Core, :5050 / :5443 | `B-S-W-SQL-04` (Windows služba) |
| **Admin konzole** | Blazor Server, :4200 | `10.8.2.213` (`B-S-W-MIKOS`, Windows služba `USBGuardianConsole`) |
| **Databáze** | SQL Server | `B-S-W-SQL-04`, DB `USBGuardian` |
| **Autentizace** | Windows Auth (Kerberos / Negotiate) | API: AD skupina; konzole: AD skupina + whitelist účtů |

## Serverová admin konzole (Blazor)

Běží na app serveru (`10.8.2.213`), čte/píše SQL-04. Stránky:

- **Přehled** – incidenty za 30 dní (Blokováno / Varování) + poslední události vč. identifikátorů
  média **VID / PID / sériové číslo** (hodnoty pro whitelist).
- **Stanice** – inventář počítačů z AD; dlaždice filtrují (vše / hlásí agenta / chybí agent);
  cesta v AD (OU) vedle hostname; tlačítko **Aktualizovat z AD**.
- **Nastavení** – efektivní konfigurace (read-only; editace přes `appsettings.local.json`).
- **Dokumentace** – nápověda v prohlížeči.

Patička: živé hodiny + commit hash buildu.

**Autorizace:** Windows Auth, dovnitř jen členové skupin `Authorization:AdminGroups` nebo účty
v `Authorization:AllowedUsers`. Pro tiché SSO chodit přes hostname, ne IP.

### AD sync

Background služba (i na vyžádání tlačítkem) načte počítače z Active Directory a zapíše do tabulky
`Computers`. Klíčem je **hostname** (ne IP – stanice mají dynamické adresy). Doménu bere
automaticky podle serveru (`new DirectoryEntry()`, nic natvrdo). Reconciliation:
*v AD ⨯ hlásí agenta* → seznam stanic, kam agent chybí.

## Lokální admin konzole agenta

Volitelná (default vypnutá), `localConsole.enabled` v `agent.config.local.json`. `HttpListener`
na `127.0.0.1`, **admin-only, read-only** – živý stav agenta (whitelist, WMI, fronta, připojená
média) pro ověření funkčnosti a offline diagnostiku. Použit `HttpListener` (ne Kestrel), aby agent
nepotřeboval ASP.NET Core runtime.

## Konfigurace

Firemně specifické hodnoty jsou **jen** v `*.local.json` (gitignored). Šablony `*.example` / s
placeholdery jsou v repu.

| Komponenta | Šablona (v repu) | Reálné (gitignored) |
|-----------|------------------|---------------------|
| Agent | `agent/USBGuardian/Config/agent.config.json` | `agent.config.local.json` |
| API | `server/USBGuardian.Api/appsettings.json` | `appsettings.local.json` |
| Konzole | `server/USBGuardian.Admin/appsettings.local.json.example` | `appsettings.local.json` |

### Konzole – `appsettings.local.json`

```json
{
  "ConnectionStrings": { "DefaultConnection": "Server=tcp:SQL-SERVER,1433;Database=USBGuardian;Integrated Security=true;TrustServerCertificate=true;" },
  "Authorization": {
    "AdminGroups": [ "DOMENA\\USB-Guardian-Admins" ],
    "AllowedUsers": [ "DOMENA\\jmeno.admina" ],
    "DevAllowAll": false
  },
  "Kestrel": { "Endpoints": { "Http": { "Url": "http://0.0.0.0:4200" } } },
  "AdSync": { "Enabled": true, "IntervalMinutes": 60, "SearchBase": "", "IncludeDisabled": false }
}
```

## Databáze

SQL skripty v `database/` (spustit v pořadí):

| Skript | Obsah |
|--------|-------|
| `01_create_database.sql` | databáze |
| `02_create_tables.sql` | Computers, WhitelistDevices, WhitelistVersions, Incidents, view + sp |
| `03_add_sourcefile.sql` | SourceFile + DisconnectedAt |
| `04_adsync_columns.sql` | LastSeen nullable + OperatingSystem / InActiveDirectory / AdSyncedAt |
| `05_adpath.sql` | AdPath (cesta v AD) |

## Rychlý start (vývoj)

```powershell
# Agent (jako Administrator pro block mode)
cd agent\USBGuardian
dotnet run -- --console

# API
cd server\USBGuardian.Api
dotnet run

# Admin konzole
cd server\USBGuardian.Admin
dotnet run
```

## Nasazení konzole na app server (.213)

```powershell
# Build (self-contained – cílový server nepotřebuje .NET)
dotnet publish server\USBGuardian.Admin -c Release -r win-x64 --self-contained -o D:\deploy\USBGuardianConsole

# Kopie přes SMB + služba přes remote sc.exe (WinRM netřeba)
robocopy D:\deploy\USBGuardianConsole \\10.8.2.213\C$\Apps\USBGuardianConsole /E /XF appsettings.local.json
# vytvořit \\10.8.2.213\C$\Apps\USBGuardianConsole\appsettings.local.json (viz .example)
sc.exe \\10.8.2.213 create USBGuardianConsole binPath= "C:\Apps\USBGuardianConsole\USBGuardian.Admin.exe" start= auto
sc.exe \\10.8.2.213 start USBGuardianConsole
```

SQL grant (least-privilege) pro účet konzole na SQL-04:

```sql
CREATE LOGIN [DOMENA\B-S-W-MIKOS$] FROM WINDOWS;
USE USBGuardian;
CREATE USER [DOMENA\B-S-W-MIKOS$] FOR LOGIN [DOMENA\B-S-W-MIKOS$];
ALTER ROLE db_datareader ADD MEMBER [DOMENA\B-S-W-MIKOS$];
GRANT INSERT, UPDATE, DELETE ON dbo.Computers TO [DOMENA\B-S-W-MIKOS$];
GRANT INSERT, UPDATE ON dbo.WhitelistDevices TO [DOMENA\B-S-W-MIKOS$];
GRANT INSERT, UPDATE ON dbo.WhitelistVersions TO [DOMENA\B-S-W-MIKOS$];
```

## Bezpečnost

- Whitelist podepsaný RSA-4096 – agent odmítne podvrhnutý whitelist (privátní klíč **nikdy na serveru**).
- TLS validace certifikátu serveru (vypnutelné pro vývoj).
- Windows Auth (Kerberos) – agenti strojovým účtem; konzole admin skupina / whitelist.
- gMSA pro SQL – žádné heslo v konfiguraci.
- Least-privilege SQL grant pro konzoli (read vše, write jen Computers + whitelist).
- `*.local.json` gitignored.
- Lokální konzole agenta i serverová: loopback / admin-only / read-only dle role.

## Repo struktura

```
usb-guardian/
├── agent/USBGuardian/        # .NET 8 Windows Service agent
│   ├── LocalConsole/         # lokální admin konzole (HttpListener)
│   ├── Config/ Models/ Security/
├── server/
│   ├── USBGuardian.Api/      # ASP.NET Core API (příjem incidentů, whitelist)
│   └── USBGuardian.Admin/    # Blazor Server admin konzole (.213)
│       ├── Components/        # Pages (Home, Computers, Settings, Docs), Layout
│       ├── AdSync/            # AdSyncRunner + AdSyncService
│       └── appsettings.local.json.example
├── database/                 # 01–05 SQL skripty
├── scripts/                  # certifikáty, watchdog, ToastHelper
├── docs/architecture.md
├── README.md / README.en.md
└── HANDOFF.md / HANDOFF.en.md
```
