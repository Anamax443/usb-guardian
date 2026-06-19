# USB Guardian

*🇨🇿 Čeština · [🇬🇧 English](README.en.md)*

Bezpečnostní nástroj pro monitoring paměťových médií (USB flash, SD karty, USB disky)
na firemních počítačích. Každé médium musí být schváleno IT oddělením a zapsáno do
centrálního whitelistu. Nepovolená média jsou varována nebo zablokována. Navrženo jako
technické opatření pro **NIS2 / zákon 181/2014 Sb. / ISO 27001**.

> **Konfigurace:** žádné firemně specifické hodnoty (server, doména, skupiny, účty) nejsou
> v kódu — vše je v `*.local.json` (gitignored).

## Stav projektu

| Fáze | Popis | Stav |
|------|-------|------|
| 1–7 | Agent (WMI/warn/block), API+SQL+gMSA+Kerberos, RSA-4096 podpis whitelistu, incident queue, log tagging | ✅ |
| 8 | **Lokální admin konzole agenta** (HttpListener, loopback, read-only) | ✅ |
| 9 | **Serverová admin konzole** (Blazor na .213) dle **AXIMA UI standardu** (dark/light, patička, /api/version) | ✅ |
| 10 | **AD sync** – inventář stanic z AD + reconciliation (kdo nemá agenta) + cesta v AD; ikona komunikace | ✅ |
| 11 | **Přehled** – dlaždicový souhrn napříč listy, filtr (období/akce/hledání), kumulace, sloupec „Schváleno" | ✅ |
| 12 | **Whitelist** – zadání jen sériovým číslem + autofill z incidentů + import + editace polí + aktivní checkbox | ✅ |
| 13 | **Centrální nastavení (DB)** – vynucování, whitelist přístupu do konzole, e-mail + alerty nad incidenty | ✅ |
| 14 | **Šifrovaná komunikace agent↔API** – self-signed cert (bez CA, MachineKeySet) + pinning otisku | ✅ |
| 15 | **Dohled komunikace** – dlaždice „Zmlklo agentů" + konfigurovatelný práh; **„Vyžádat data" na klik**; řaditelná tabulka Detailně | ✅ |
| 16 | **Startovní sken** už-připojených médií; whitelist poll 2 min; centrální `onExpired`/`enforce` | ✅ |
| 17 | **Auto-enrollment agenta** – konzole sama nasadí agenta na stanice bez něj (gMSA + scheduled task, dry-run/opt-in); **PILOT ÚSPĚŠNÝ na .181 (bez creds, přes gMSA)** | ✅ pilot OK |
| 18 | **DB/incidenty tečou** (agent→API→DB→konzole) | ✅ |
| 19 | **Verze/commit na všech komponentách** (`/api/version`, agent hlásí commit) | ✅ |
| – | Zavřít nešifrované HTTP 5050 (jen HTTPS) | 🔜 NIS2 |
| – | **Podpisový/publikační workflow** whitelistu → vynucování + blocklist „naostro" k agentům | 🔜 |
| – | Per-serial **blocklist** + blokace už-připojeného média | 🔜 |
| – | Monitoring expirace podpisového certu | 🔜 |

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
| **API** | ASP.NET Core, :5050 / :5443 | `B-S-W-SQL-04`, instalace `C:\USBGuardian.Api`, Windows služba „USB Guardian API" |
| **Admin konzole** | Blazor Server, :4200 | `10.8.2.213` (`B-S-W-MIKOS`, Windows služba `USBGuardianConsole`) |
| **Databáze** | SQL Server | `B-S-W-SQL-04`, DB `USBGuardian` |
| **Autentizace** | Windows Auth (Kerberos / Negotiate) | API: AD skupina; konzole: AD skupina + whitelist účtů |

## Serverová admin konzole (Blazor)

Běží na app serveru (`10.8.2.213`), čte/píše SQL-04, **AXIMA UI standard** (archetyp A – IT-ops:
dark/light přepínač `axima.theme` bez FOUC, tisk = light, semafor stavů). Stránky:

- **Přehled** – dlaždicový souhrn napříč listy (Stanic v AD / Chybí agent / Schválených médií /
  Deaktivovaných / Incidentů / Blokováno / Varování, prokliky na listy). **Filtr** (období
  30/90/rok/vše, akce, fulltext) + **kumulace** (seskupení médium+stanice+uživatel s počtem) +
  identifikátory **VID/PID/sériové číslo** + sloupec **„Schváleno"** (aktuálně dle whitelistu).
  Tabulka **„Detailně" má řaditelné hlavičky** (řazení v DB přes query-string).
- **Stanice** – inventář z AD; dlaždice filtrují (vše / hlásí / **zmlklo agentů** / chybí agent),
  **hledání**, **cesta v AD** (OU) vedle hostname, **ikona komunikace** (zelená ≤ práh / žlutá zmlkl
  / šedá žádný kontakt; práh `comm.silentAfterMinutes` v Nastavení), tlačítko **Aktualizovat z AD**
  a **„Vyžádat data"** (řádek/hromadně) – vynutí odevzdání dat při nejbližším heartbeatu.
- **Whitelist** – schválená média; **stačí zadat sériové číslo** (VID/PID/název se dotáhnou
  z incidentů, i zpětně), **hromadný import**, **editace polí** inline, **checkbox Aktivní**
  (dočasná deaktivace bez mazání).
- **Nastavení** (centrální, v DB) – **vynucování** (vyžadovat jen schválená média), **dohled
  komunikace** (práh „zmlklého agenta"), **whitelist přístupu** do konzole (uživatelé/skupiny;
  appsettings = lockout-safe bootstrap), **e-mail** (SMTP relay/Direct Send + test) a **alerty
  nad incidenty** (interval), **auto-enrollment agenta** (master vypínač + dry-run + cíle),
  AD sync / DB / build info.
- **Dokumentace** – rozcestník + **tisknutelné HTML** stránky (render `.md` přes Markdig, žádné
  externí odkazy).

Patička (servisní řádek dle standardu): **živé hodiny + klikací commit hash + DB health + © Milan Trnka**.
Kontrakt **`GET /api/version`**.

### Verzování (commit na všech komponentách)

Všechny komponenty hlásí svůj git commit, takže operátor ověří, co přesně běží:

- **Konzole** – commit v **patičce** + endpoint `:4200/api/version`.
- **API** – endpoint `:5050/api/version` (NOVĚ).
- **Agent** – hlásí commit (heartbeat) → konzole ho zobrazí jako **„Agent verze"**.

Commit se razítkuje při buildu přes MSBuild (`git rev-parse`).

**Autorizace:** Windows Auth, dovnitř jen členové `Authorization:AdminGroups` / účty
`Authorization:AllowedUsers` (appsettings) **nebo** DB seznam z Nastavení. Pro tiché SSO chodit přes hostname, ne IP.

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

## Šifrovaná komunikace agent ↔ API (self-contained TLS)

NIS2 vyžaduje šifrovaný přenos. Řešeno **bez závislosti na CA / externím certu**:

- **API** si při startu vygeneruje/persistne **vlastní self-signed cert** (`SelfCert.cs`,
  `C:\ProgramData\USBGuardian\api-tls.pfx`), Kestrel ho nabinduje na `:5443`. Klíč je
  **`MachineKeySet`** (běží i pod gMSA, Schannel ho použije). Otisk (PIN) zaloguje + vrací
  `GET /api/cert-info`.
- **Agent** server **nepinuje přes CA, ale přes otisk** (`tls.pinnedThumbprint` v configu,
  `TlsClient.cs`) → šifrované **i** ověřené, bez CA. Bez pinu lze `validateServerCertificate=false`
  (jen vývoj) nebo CA validace.

Agent prod config: `whitelist.syncUrl = https://SERVER:5443` + `tls.pinnedThumbprint = <otisk z /api/cert-info>`.

## Distribuce a auto-enrollment agenta

Stanice bez agenta jsou vidět na **Stanicích** (dlaždice „Chybí agent"). Nasazení:

- **Lokální instalace:** `scripts\Install-Agent.ps1 -SourcePath <publish>` (vytvoří službu „USB Guardian"
  + recovery + watchdog, nasadí per-machine `agent.config.local.json`), `scripts\Uninstall-Agent.ps1`.
- **Hromadně:** `scripts\Deploy-AgentFleet.ps1 -TargetsFile … -SourcePath …` – paralelní rollout přes
  `\\HOST\C$` + `sc.exe \\HOST create` + watchdog; přeskočí offline/už-nainstalované; audit CSV. (PS 5.1 i 7.)
- **Auto-enrollment (konzole nasazuje sama):** `AgentDeployService` v konzoli po AD syncu najde stanice bez
  agenta a (v ostrém režimu) zapíše seznam do `deploy.targetsFile`; instalaci provede **scheduled task na .213
  pod dedikovaným gMSA** (least-privilege – jen ten účet má admin na klientech). **Default VYPNUTO + dry-run.**
  Nastavení účtu: [docs/auto-deploy-setup.md](docs/auto-deploy-setup.md) (+ `scripts\New-DeployGmsa.ps1`).

> **Prostředí AXIMA:** PS skripty co běží na strojích **musí být podepsané** (execution policy AllSigned přes GPO)
> prod certem `CN=powershell.axinetwork.loc` a publisher musí být v `LocalMachine\TrustedPublisher`
> (na .213 i klientech, fleet přes GPO). Před podpisem **CRLF + UTF-8 BOM** (jinak HashMismatch).

## Konfigurace

Firemně specifické hodnoty jsou **jen** v `*.local.json` (gitignored). Centrální provozní
nastavení (vynucování, přístup, e-mail) je v **DB** (`AppSettings`), spravované z Nastavení. Šablony `*.example` / s
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
| `06_appsettings.sql` | AppSettings (centrální nastavení: vynucování, přístup, e-mail) + grant |

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

> **Build/deploy artefakty:** publikuje se lokálně do `D:\deploy`; API se stageuje na .213 do
> `C:\Apps\USBGuardianApiPublish` a odtud se instaluje na SQL-04 do `C:\USBGuardian.Api` (služba „USB Guardian API").

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
│       ├── Deploy/            # AgentDeployService (auto-enrollment orchestrátor)
│       ├── Notifications/     # IncidentAlertService + EmailSender
│       └── appsettings.local.json.example
├── database/                 # 01–06 SQL skripty
├── scripts/                  # certifikáty, watchdog, ToastHelper, Install/Uninstall-Agent,
│                             #   Deploy-AgentFleet, New-DeployGmsa
├── docs/architecture.md, docs/auto-deploy-setup.md
├── README.md / README.en.md
└── HANDOFF.md / HANDOFF.en.md
```
