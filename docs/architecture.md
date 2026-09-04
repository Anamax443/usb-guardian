# USB Guardian – Architektura

*🇨🇿 Čeština · [🇬🇧 English](architecture.en.md)*

## Přehled systému

```
┌─────────────────────────────────────────────────────────────────────┐
│  Klientský PC (Windows 10/11)                                       │
│                                                                     │
│  ┌─────────────────────────────────────────────────────────────┐   │
│  │  USB Guardian Agent (Windows Service – SYSTEM)              │   │
│  │                                                             │   │
│  │  DeviceMonitor ──► WhitelistChecker ──► PolicyEnforcer      │   │
│  │    (WMI)              (RSA verify)      (warn / block)      │   │
│  │                                             │               │   │
│  │                          ┌──────────────────┤              │   │
│  │                          ▼                  ▼               │   │
│  │                   NotificationService   IncidentLogger      │   │
│  │                   (Toast – user session) (JSON queue)       │   │
│  │                                          DeviceBlocker      │   │
│  │                                          (IOCTL)            │   │
│  │                                                             │   │
│  │  WhitelistSync ──────────────────────────────────────────┐  │   │
│  │  IncidentSync  ──────────────────────────────────────────┤  │   │
│  └──────────────────────────────────────────────────────────┼──┘   │
│                                                             │       │
└─────────────────────────────────────────────────────────────┼───────┘
                              HTTPS (TLS) │ port 5443
                              ┌───────────┘
                              ▼
┌─────────────────────────────────────────────────────────────────────┐
│  Server (B-S-W-SQL-04 nebo dedikovaný Windows Server)               │
│                                                                     │
│  ┌──────────────────────────────────┐   ┌────────────────────────┐ │
│  │  USB Guardian API                │   │  SQL Server            │ │
│  │  ASP.NET Core – port 5443/5050   │   │  Database: USBGuardian │ │
│  │                                  │   │                        │ │
│  │  /api/whitelist  (GET)           │◄─►│  Incidents             │ │
│  │  /api/incidents  (POST/GET)      │   │  WhitelistVersions     │ │
│  │  /api/heartbeat  (GET)           │   │  Computers             │ │
│  │  ActivityLogger (deník)          │   │  AppSettings           │ │
│  │  Windows Auth (Kerberos)         │   │  ActivityLog  ◄──────┐ │ │
│  │  AD skupiny: USB-Guardian-Clients│   │  gMSA: gmsa-SQL$     │ │ │
│  └──────────────────────────────────┘   └──────────────────────┼─┘ │
│                                                                │   │
│  ┌──────────────────────────────────┐                          │   │
│  │  Admin konzole (Blazor :4200)    │  zásahy operátora ───────┘   │
│  │  Přehled · Stanice · Whitelist   │  (nasazení, vyřazení,        │
│  │  Nastavení · Kontroly · Aktivita │   publikace whitelistu)      │
│  │  AD sync ◄── Active Directory    │                              │
│  └──────────────────────────────────┘                              │
└─────────────────────────────────────────────────────────────────────┘
```

> Deník (`ActivityLog`) je jediné místo, kam píšou **obě** serverové strany — API komunikaci agentů,
> konzole zásahy operátora. Proto se dá provoz číst jako jeden příběh.

## Komponenty agenta

| Komponenta | Popis |
|-----------|-------|
| `DeviceMonitor` | WMI subscriber – Win32_DiskDrive connect/disconnect eventy + **startovní sken** už-připojených médií (watchers chytají jen nová připojení) + **`ReEnforceConnectedDevices()`** (znovu zablokuje připojená neschválená média při zapnutí blokování) |
| `WhitelistChecker` | Čte lokální `whitelist.json`, ověřuje RSA-4096 podpis |
| `PolicyEnforcer` | Rozhoduje o akci dle `policy.mode` (warn / block) |
| `NotificationService` | Windows Toast notifikace pro přihlášeného uživatele |
| `IncidentLogger` | Ukládá incidenty do JSON front (`queue/`) |
| `DeviceBlocker` | Blokuje médium přes DeviceIoControl (IOCTL_STORAGE_EJECT_MEDIA) |
| `WhitelistSync` | Heartbeat + stahování whitelistu (interval: **2 min**, konfig `sync:whitelistSyncIntervalMinutes`). Heartbeat nese verzi/online; při změně whitelistu se stáhne v témž cyklu → nový whitelist na klientech do ~2 min |
| `IncidentSync` | Odesílá frontu incidentů na server (interval: 1 min, s jitter; probudí se dřív při `ReportNow`) |
| `SyncSignals` | Sdílený signál: heartbeat (`ReportNow`) → okamžitý flush fronty incidentů |
| `SignatureVerifier` | Ověřuje RSA-4096 podpis whitelistu – fail-secure |
| `SessionUser` | Reálný přihlášený uživatel přes WTS API (agent=SYSTEM → ne `Environment.UserName`=`HOST$`); fail-safe fallback na strojový účet |

## Komponenty serveru

| Komponenta | Popis |
|-----------|-------|
| `IncidentsController` | POST příjem incidentů od agentů (vrací **202 Accepted** – zařadí do `IncidentQueue`, sám do DB NEpíše), GET pro Admin UI |
| `WhitelistController` | GET aktuální whitelist + verze + podpis |
| `HeartbeatController` | GET zdravotní stav serveru |
| `IncidentQueue` | In-memory fronta přijatých incidentů (mezi controllerem a workerem) |
| `IncidentQueueWorker` | Background worker – odebírá z `IncidentQueue` a **až on zapisuje** incidenty do DB (async) |
| `ActivityLogger` | Zápis do **deníku provozu** (`ActivityLog`) – sdílený zdroj slinkovaný do API i konzole, fire-and-forget (viz níže) |
| `AppDbContext` | EF Core kontext – SQL Server přes gMSA Windows Auth |

> **DI:** příjem→zápis je rozdělený, proto je nutné v `Program.cs` zaregistrovat **`IncidentQueue`** (singleton)
> **i** hosted **`IncidentQueueWorker`** – bez obojího se incidenty přijmou (202), ale do DB se nezapíšou.

## Serverová admin konzole (USBGuardian.Admin)

Samostatná **Blazor Server** aplikace na app serveru (`10.8.2.213`), Windows služba
`USBGuardianConsole`, port `:4200`. Oddělená od ingestion API (odolnost – příjem incidentů
od 500+ agentů nesmí ovlivnit adminní použití). Čte/píše SQL-04, modely reusnuté z API
(slinkované `DbModels.cs` + `AppDbContext.cs` – žádná duplikace).

| Komponenta | Popis |
|-----------|-------|
| `Home` (Přehled) | Incidenty za 30 dní + poslední události vč. VID/PID/sériové číslo |
| `Computers` (Stanice) | Inventář z AD; dlaždice = filtr; cesta v AD (OU); tlačítko Aktualizovat z AD |
| `Settings` / `Docs` | Efektivní konfigurace (read-only) / nápověda v prohlížeči |
| `AdSyncRunner` | Logika AD syncu – volatelná z časovače i z UI (semafor proti souběhu) |
| `AdSyncService` | Časovač nad `AdSyncRunner` (interval z configu) |
| `AppInfo` | Commit hash buildu (MSBuild `git rev-parse` stamp z gitu) → patička + `:4200/api/version` |

**Autorizace:** Windows Auth (Negotiate). Přístup jen členům `Authorization:AdminGroups`
(AD skupina) **nebo** účtům v `Authorization:AllowedUsers` (whitelist). Kontrola přes
`WindowsPrincipal.IsInRole` (řeší doménové skupiny). `DevAllowAll` = bypass jen pro vývoj.

### AD sync

```
Active Directory (objectCategory=computer, ne disabled)
        ↓  (new DirectoryEntry() – ambient doména, nic natvrdo)
AdSyncRunner: name → Hostname, dNSHostName → Domain, operatingSystem, distinguishedName → AdPath (OU)
        ↓  upsert (klíč = hostname), NEpřepisuje LastSeen/AgentVersion (vlastní agent/API)
SQL Computers + reconciliation: InActiveDirectory; "v AD ⨯ hlásí agenta" = kam chybí agent
```

## Lokální admin konzole agenta

`LocalConsoleService` – `HttpListener` na `127.0.0.1:5080` (volitelné, `localConsole.enabled`, default vypnuto;
port `localConsole.port`). Admin-only (`WindowsPrincipal.IsInRole(Administrator)`), read-only. Živý in-memory stav
agenta: **seznam schválených zařízení (whitelist)** vč. VID/PID/sériák/popis/schválil/platnost, stav+verze
whitelistu, **verze agenta (commit)**, WMI watchdog, fronta incidentů, právě připojená média a poslední události.
`HttpListener` schválně místo Kestrelu – agent (`Sdk.Worker`) nepotřebuje ASP.NET Core runtime; loopback → plain
HTTP akceptovatelné. Endpointy: `GET /` (HTML dashboard, auto-refresh 3 s) · `GET /api/status` (JSON, vč. počtu
a seznamu médií, která agent právě drží zablokovaná) · zapisující (admin-only): `POST /api/override[/clear]`
(break-glass) · `POST /api/unblock-all` (**okamžitě vrátí všechna média, která agent sám zakázal** – ruční pojistka
vedle automatického vrácení) · `POST /api/restart` (**restart klientské služby** – agent jako SYSTEM si lokálně
restartne vlastní službu odděleným `cmd: sc stop → pauza → sc start`; lokální admin to spustí z dashboardu, žádný
server/admin na klientech netřeba).

### Autorizace lokální konzole – filtrovaný token

Požadavek na `127.0.0.1` je z pohledu Windows **síťové přihlášení**. U **lokálního** účtu z takového tokenu
`LocalAccountTokenFilterPolicy` odebere skupinu `Administrators` (zůstane v něm jen jako *deny-only*), takže
`WindowsPrincipal.IsInRole(Administrator)` vrátí **false**, i když člověk lokální admin **je**. Tím byl
break-glass nedostupný přesně v situaci, na kterou je určený (technik u stanice, která nedosáhne na server).

> **Kdo se do konzole dostane:** **jen lokální administrátor té stanice** — konzole není pro koncového
> uživatele. V prostředí, kde jsou admin práva na oddělených účtech (`pcadmin.*` ve skupině `PC Admins`),
> je break-glass fakticky nástroj IT, ne uživatele; běžný účet dostane vysvětlující odmítnutí. Je to
> záměr: vypnutí blokování je zásah do vynucované politiky, i když dočasný a logovaný.

Kontrola proto **uznává i filtrovaný token** (deny-only SID členství). Je to bezpečné, protože členství tu slouží
jako **autorizace**, ne jako zdroj práv: samotnou akci provádí služba běžící pod SYSTEM, žádný elevovaný token
volajícího k tomu není potřeba. Odmítnutí vrací stránku, která ukáže **jako kdo** byl požadavek viděn a co je
potřeba — bez toho se to nedalo diagnostikovat na dálku ani na místě.

### Denní restart agenta

`SelfRestart` (výchozí **zapnuto, 04:15**, konfigurovatelné, vypnutelné z lokální konzole) drží agenta svěží —
zaseknutý WMI watcher nebo ukousnutý handle přežije restart služby, ne den provozu.

## Identifikace zařízení

```
VID:PID:SERIAL  →  klíč pro porovnání (uppercase)
Např: KINGSTON:DATATRAVELER_3.0:4E0788D05AC9
```

Whitelist záznam obsahuje: `vendorId`, `productId`, `serialNumber`, `description`, `approvedAt`, `approvedBy`

> **Pozn. – trim sériového čísla:** WMI vrací sériák často s **koncovými mezerami** (trailing spaces);
> před porovnáním i zápisem se musí **trimovat**, jinak whitelist match selže nebo se uloží „špinavý" sériák.

> **Pozn. – atribuce uživatele (HOTOVO):** agent běží jako **SYSTEM**, takže `Environment.UserName` by vrátil
> **strojový účet** (`HOST$`), ne reálného uživatele. `SessionUser` proto čte uživatele aktivní interaktivní
> session přes **WTS API** (`WTSGetActiveConsoleSessionId` → fallback enumerace aktivních session přes
> `WTSEnumerateSessions`; `WTSQuerySessionInformation` na `WTSUserName`+`WTSDomainName`) → `DOMAIN\user`.
> **Fail-safe:** když nikdo není přihlášen (zamčeno/jen služby), padá zpět na `Environment.UserName` (strojový účet) –
> incident se zaznamená vždy. Použito v `Incident.Username`, `PolicyEnforcer` (log) i Toast notifikaci.

## Bezpečnostní vrstvy

| Vrstva | Mechanismus |
|--------|-------------|
| Transport | TLS 1.2+ (Kestrel), agent ověřuje server **pinningem otisku** (bez CA) |
| Autentizace | Windows Auth – Kerberos Negotiate |
| Autorizace | AD skupiny – `USB-Guardian-Clients` (API), admin skupina + whitelist účtů (konzole) |
| Integrita dat | RSA podpis whitelistu, fail-secure (co agent neověří, nepoužije) |
| Service účet | gMSA `AXINETWORK\gmsa-SQL$` – bez hesla v konfiguraci |
| Oddělení vrstev | tři deploy identity: fleet × server × běžící konzole (ta není admin nikde) |
| Auditní stopa | incidenty + **deník provozu** (kdo s kým mluvil, kdo co změnil) |
| Konfigurace | `appsettings.local.json` gitignored – citlivé hodnoty mimo repo |

> **Kde je podpisový klíč:** privátní klíč whitelistu **je na app serveru** (`Whitelist:PrivateKeyPath`).
> Je to vědomý kompromis za plně automatickou publikaci — ruční offline podpis po každé změně katalogu byl
> provozně neúnosný. Klíč je interní klíč nástroje (agenti mají jen veřejnou část), ne firemní CA; chrání ho
> ACL na serveru. Offline `WhitelistSigner` zůstává pro generování klíčů a ruční ověření.

## Konfigurace – klíčové hodnoty

### Agent (`agent.config.json`)

```json
{
  "policy": {
    "mode": "warn",               // warn | block
    "onExpiredWhitelist": "warn"  // warn | block | allow
  },
  "whitelist": {
    "syncUrl": "https://SERVER:5443",
    "localPath": "C:\\ProgramData\\USBGuardian\\whitelist\\whitelist.json"
  },
  "tls": {
    "validateServerCertificate": true   // false pouze pro vývoj
  },
  "signing": {
    "enabled": true   // false pouze pro vývoj
  }
}
```

### Server (`appsettings.json` + `appsettings.local.json`)

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=tcp:SQL_SERVER,1433;Database=USBGuardian;Integrated Security=true;"
  },
  "Authorization": {
    "AllowedGroups": ["DOMAIN\\USB-Guardian-Clients"]
  }
}
```

## Logování

Oba procesy používají vlastní `RoleTagFormatter` (konzolový formatter):

```
HH:mm:ss [KLIENT] info: USBGuardian.DeviceMonitor[0]
HH:mm:ss [SERVER] info: USBGuardian.Api.IncidentController[0]
```

- **Agent** → `[KLIENT]`
- **Server** → `[SERVER]`
- Produkce: agent loguje do Windows Event Log, server do Event Log i konzole

## Verzování (commit na všech komponentách)

Každá komponenta hlásí svůj git commit (razítkuje MSBuild `git rev-parse` při buildu), aby operátor ověřil, co běží
(= kontrola aktuálnosti nasazení: co je na gitu, musí být na stránce). **Stamp je spolehlivý** – generuje se zdrojový
soubor `GitCommit.g.cs` přepsaný jen při změně commitu (`WriteOnlyWhenDifferent`), což vynutí recompile i když se
jinak nezměnil žádný `.cs`. Dřív (`BeforeTargets=CoreGenerateAssemblyInfo`) mohl incremental build držet starý commit.

| Komponenta | Kde |
|-----------|-----|
| Konzole | patička + `:4200/api/version` |
| API | `:5050/api/version` (**NOVĚ**) |
| Agent | hlásí commit v heartbeatu → konzole „Agent verze" |

## Datový tok – incident

```
1. USB připojeno → WMI event
2. Agent identifikuje VID:PID:Serial
3. WhitelistChecker: médium NENÍ na whitelistu
4. PolicyEnforcer: mode=warn
5. NotificationService: Toast uživateli
6. IncidentLogger: uložit do queue/log_MACHINE_DATE.json
7. IncidentSync (1 min): odeslat na server /api/incidents
8. IncidentsController: zařadit do IncidentQueue → vrátit 202 Accepted (NEpíše do DB)
9. IncidentQueueWorker (async): odebrat z fronty a zapsat do SQL tabulky Incidents
10. ActivityLogger: řádek do deníku „přijata dávka N incidentů ze stanice X" (fire-and-forget)
```

## Deník provozu (ActivityLog)

Do `Incidents` se dostane jen to, co **skončilo incidentem**. Když agent přestal komunikovat, když někdo změnil
whitelist nebo když se nasadila verze, nezůstala po tom stopa nikde než v Event Logu jednoho stroje — a tam se
nikdo nedívá. Deník je jedno místo, kde je vidět provoz celého systému.

```
API      → heartbeat (vč. toho, CO server odpověděl), příjem dávek incidentů
Konzole  → ruční nasazení/aktualizace, trvalé vyřazení stanice, publikace whitelistu
              ↓ oba přes sdílený ActivityLogger
        dbo.ActivityLog (Timestamp · Level · Source · Hostname · User · Message)
              ↓
        stránka Aktivita: filtry (období/úroveň/zdroj/hledání), režim „živě" (3 s), export CSV
```

**Proč fire-and-forget:** zápis běží mimo hlavní cestu požadavku a každá chyba se spolkne. Kdyby heartbeat agenta
spadl kvůli tomu, že nešlo zapsat řádek deníku, byl by **pozorovatel důležitější než to, co pozoruje**. Ze stejného
důvodu se na dokončení zápisu nečeká — tep stovek agentů nemá být svázaný s latencí databáze.

**Obě strany píšou do TÉŽE tabulky**, takže se provoz čte jako jeden příběh, ne jako dva. Nabídka zdrojů ve filtru
se bere z dat, ne z pevného seznamu — jinak by se rozešla s tím, co se do deníku doopravdy píše.

**Retence:** řádků přibývá rychle (heartbeat à 2 min × počet stanic ≈ 150 tis./den při 213 stanicích). Úklid má
dělat `sp_PurgeActivityLog` (maže po dávkách po 5000, aby z něj nebyl dlouhý zámek nad tabulkou, do které zrovna
píšou agenti). **Pozor – procedura zatím není odnikud volaná**, viz roadmap.

## Deployment

### Vývojové prostředí

```
dotnet run -- --console    (agent)
dotnet run                 (server)
```

### Produkce

- **Kompletní balíček klienta:** `scripts\Build-AgentPackage.ps1` → self-contained agent (root) +
  `ToastHelper\` (notifikace v user session) + `tasks\` (definice scheduled tasků). Klient nepotřebuje .NET runtime.
- Agent: Windows Service, spouštěn pod SYSTEM
- **ToastHelper:** scheduled task `\USBGuardian\USBGuardian-ToastHelper` (trigger přihlášení + odemčení, běh v user
  session, least-privilege) – registrace **PS-free** přes `schtasks /XML` (`tasks\USBGuardian-ToastHelper.xml`).
- **Watchdog:** scheduled task `\USBGuardian\USBGuardian-Watchdog` (à 3 min, PS-free `sc start`).
- Fleet rozvoz obojího: `Deploy-AgentFleet.ps1` (robocopy balíček + sc.exe create + oba tasky), pod gMSA z `.213`.
- Server: Windows Service, spouštěn pod gMSA
- HTTPS certifikát: `scripts\New-Certificate.ps1` na produkčním serveru
- AD skupiny: `USB-Guardian-Clients` – stroje s nasazeným agentem

### Aktualizace už nasazeného agenta a nasazení API

Instalace a aktualizace jsou **dvě různé úlohy**. Fleet skript uměl jen čistou instalaci; aktualizace „rovnou
robocopy" by na běžícím agentovi přepsala část DLL, kopie zamčeného `.exe` by selhala a na stanici by zůstala
**směs verzí**, zatímco deploy hlásí úspěch.

| Krok | Skript | Úloha na `.213` | Účet |
|------|--------|-----------------|------|
| Čistá instalace na stanice bez agenta | `Deploy-AgentFleet.ps1` | `USBGuardian-AutoDeploy` | `gmsa-USBGdep$` |
| Aktualizace nasazeného agenta | `Update-Agent.cmd` | `USBGuardian-UpdateAgent` (+ `-UpdateAgentBeta`) | `gmsa-USBGdep$` |
| Nasazení API na jeho server | `Deploy-Api.cmd` | `USBGuardian-ApiDeploy` | `gmsa-USBGsrv$` |

Oba `.cmd` drží stejný vzor: **zastav službu → počkej na `STOPPED` → zkopíruj (bez `*.local.json`) → nastartuj →
ověř `RUNNING`**; návratový kód = počet neúspěšných stanic, log v `C:\ProgramData\USBGuardian\deploy\`.

**Dávka (.cmd), ne PowerShell:** prostředí vynucuje `AllSigned` přes GPO; `.cmd` mu nepodléhá, takže změna
nasazovacího kroku nevyžaduje nový podpis.

**Kanály a návrat zpět:** balíček se archivuje po verzích (`stable` / `beta`, `Set-AgentVersion.cmd` /
`Archive-AgentVersion.cmd`), takže jde nasadit předchozí verzi. V balíčku je i **offline instalátor**
(`Install-Agent.cmd` / `Uninstall-Agent.cmd`) pro stanici, kam deploy kanál nedosáhne — včetně úklidu po sobě.

> **Gotcha – úloha pod gMSA:** `schtasks /Create /RU "…gmsa$"` bez hesla vyrobí úlohu s `LogonType=InteractiveToken`
> → nespustí se („uživatel nebyl přihlášen", event 332). S4U (`/NP`) nemá síťové credentials a nedosáhne na
> `\\HOST\C$`. Funguje jediné: vzít XML fungující úlohy, vyměnit `<Command>`/`<Arguments>`/`<URI>`, uložit jako
> **UTF-16** a založit přes `/XML` — to nese `LogonType=Password`, u kterého si heslo gMSA vyzvedne systém.

### Oddělené deploy identity

Jeden účet nesmí držet fleet i server současně — kompromitace deploy identity by jinak sáhla na obojí.

| Role | Účet | Kde je admin |
|---|---|---|
| Klienti (auto-enrollment, update) | `gmsa-USBGdep$` | skupina `PC Admins` → jen stanice |
| Server (nasazení API) | `gmsa-USBGsrv$` | lokální admin jen na serveru API |
| Konzole (běžící aplikace) | strojový účet app serveru | **nikde** |

`gmsa-USBGsrv$` je záměrně **mimo** skupinu serverových adminů; členství je lokální, jen na tom jednom stroji.
Když deploy úloha začne hlásit `ERROR_LOGON_FAILURE (0x8007052E)`, není to o právech — je to zastaralá lokální
kopie hesla gMSA (`Install-ADServiceAccount`).

## Šifrovaná komunikace agent ↔ API (self-contained TLS)

API si při startu vygeneruje/persistne vlastní self-signed cert (`SelfCert.cs`, **`MachineKeySet`** –
běží i pod gMSA, NE EphemeralKeySet – s ní Schannel neudělá server handshake), Kestrel bind `:5443`.
Bez CA, bez cert store. Agent ho ověří **pinningem otisku** (`TlsClient.cs`, `tls.pinnedThumbprint`)
→ šifrované i ověřené. Otisk = `GET /api/cert-info` / log API. Přístup k API přes policy
`USBGuardianClients` (členství v `Authorization:AllowedGroups`).

## Vyžádání dat na klik (ReportNow)

Push model = server nemá zpětný kanál k agentovi. „Vyžádat data" proto jede přes **příkaz přibalený
do odpovědi na heartbeat** (stejný kanál jako `WhitelistUpdateAvailable`):

```
Konzole (Stanice) → AppSettings: cmd.report.<HOST> = čas požadavku (UTC)
Agent heartbeat (≤2 min) → HeartbeatController: ReportNow=true POKUD požadavek novější než PŘEDCHOZÍ LastSeen
        ↓ (jednorázové – příští heartbeat má LastSeen už za časem požadavku → ReportNow=false; API jen ČTE AppSettings)
Agent: heartbeat potvrdil online+verzi (LastSeen) + SyncSignals → IncidentSync hned flushne frontu
Konzole: „vyžádáno HH:mm" dokud se agent neozve (LastSeen ≥ čas požadavku)
```

Latence ≤ heartbeat interval (~2 min). Hromadně přes „Vyžádat data od všech" (jen stanice hlásící agenta).
Klíč `cmd.report.<HOST>` v `AppSettings` slouží i jako audit „naposledy vyžádáno".

## Centrální nastavení a alerty (konzole)

Tabulka `AppSettings` (key/value, migrace 06) spravovaná z Nastavení; `AccessCache` singleton:
- `policy.enforce` – vynucovat jen schválená média (agent začne respektovat po heartbeat distribuci – pending).
- `comm.silentAfterMinutes` – práh „zmlklého agenta" (default 180); hranice pro tečku komunikace i dlaždici na Stanicích.
- `deploy.*` – auto-enrollment (viz níže): `enabled`/`dryRun`/`defaultEnroll`/`intervalMinutes`/`maxPerRun`/`allowHosts`/`includeHosts`/`excludeHosts`/`targetsFile`/`lastRun`.
  **Model default + výjimky:** `defaultEnroll` (Nastavení) = výchozí pro nově objevené PC (nasazovat/ne). Per-stanice výjimky
  se dělají **přímo v Stanicích** (sloupec „Nasazení", + hromadně „Vyřadit/Zařadit vše"); ukládají se jako `includeHosts`
  (vynutit ON) / `excludeHosts` (vynutit OFF). Efektivní stav = include ? ON : exclude ? OFF : `defaultEnroll`.
- `access.users` / `access.groups` – whitelist přístupu do konzole (`appsettings` = lockout-safe bootstrap).
- `email.*` – SMTP relay (M365 Direct Send) + `IncidentAlertService` (background notifier: souhrn nových
  neschválených incidentů, baseline při 1. běhu, interval/throttle; `EmailSender`).

## Auto-enrollment agenta (konzole nasazuje sama)

```
AdSync → Computers (kdo nemá agenta)
AgentDeployService (24/7, default VYPNUTO + dry-run)
   ↓ ostrý režim
deploy.targetsFile (seznam stanic bez agenta)         [konzole = B-S-W-MIKOS$, jen zápis]
   ↓ čte
Scheduled task na .213 pod gMSA gmsa-USBGdep$         [least-privilege: admin jen na klientech]
   ↓ Deploy-AgentFleet.ps1
\\HOST\C$ robocopy + sc.exe \\HOST create + watchdog + start  → agent na klientovi (LocalSystem)
```

Least-privilege: konzole **nemění identitu** (zůstává `B-S-W-MIKOS$`, SQL granty beze změny), instalaci dělá
oddělený task pod deploy účtem. **Prostředí (AXIMA): PS skripty musí být podepsané** (AllSigned GPO) prod certem
`CN=powershell.axinetwork.loc` + publisher v `LocalMachine\TrustedPublisher`; před podpisem CRLF+UTF-8 BOM.
Nastavení: [auto-deploy-setup.md](auto-deploy-setup.md).

## Konzole – funkce stránek

- **Přehled** – dlaždicový souhrn napříč listy + filtr (období/akce/fulltext) + kumulace (GroupBy přes
  anonymní typ → in-memory map) + sloupec „Schváleno" dle aktivního whitelistu. Tabulka „Detailně" má
  **řaditelné hlavičky** (řazení v DB přes query-string, před `Take(200)`).
- **Stanice** – AD inventář, filtr, cesta v AD (OU), ikona komunikace (dle čerstvosti `LastSeen`),
  dlaždice „Zmlklo agentů" (hlásí agenta, ale `LastSeen` starší než práh `comm.silentAfterMinutes` – možný výpadek/tamper),
  tlačítko „Vyžádat data" (řádek/hromadně) → [ReportNow](#vyžádání-dat-na-klik-reportnow). Sloupec **„Nasazení"** u stanic
  bez agenta = přepínač zařadit/vyřadit z auto-enrollmentu (výjimka proti `deploy.defaultEnroll`); hromadně „Vyřadit/Zařadit vše".
- **Whitelist** – serial-only zadání + backfill VID/PID z incidentů + import + inline edit + `IsActive` checkbox.
  **Kapacita** média se dotahuje z incidentů (max `SizeBytes` dle sériáku, display-only – na whitelistu se nedrží).
- **Kontroly** – health checks serveru i klientů. Seznam kontrol se ukáže **dopředu** a odškrtává se s průběžnými
  výsledky (aby bylo vidět, že to běží, ne jen že se něco točí); prodleva mezi kroky je záměrná. Export výsledků
  do CSV / HTML / PDF (tisk) / TXT. Součástí je i **plánovaný restart** služeb (server i klient).
- **Aktivita** – deník provozu (viz [Deník provozu](#deník-provozu-activitylog)): filtry (období, úroveň, zdroj,
  hledání), režim **živě** s obnovou po 3 s, export CSV.
- **Databáze** – read-only přehled obsahu DB: počty záznamů v tabulkách, rozsah incidentů (kontrola retence),
  výpis `AppSettings` a posledních 20 incidentů.
- **Dokumentace** – render `.md` (Markdig) jako tisknutelné HTML, rozcestník + grafické výstupy:
  animace „Jak to funguje", **myšlenková mapa**, **vývojový diagram** a **shrnutí pro vedení (A4)**.
  Všechny čtyři jsou dvojjazyčné (přepínač CS/EN v hlavičce stránky).

**Přehled – kapacita & export:** kumulovaný i detailní výpis ukazují velikost média. Dvě tlačítka exportu (dědí
aktivní filtr období/akce/hledání):
- `GET /export/incidents.csv` – surová data (CSV, UTF-8 BOM + `;` → Excel CZ), max 50 000 řádků.
- `GET /export/manager` – **manažerský report** (tisknutelné HTML → PDF, cíleně na **1–2 A4**): KPI + **grafy
  (inline SVG, bez knihoven):** vývoj incidentů v čase (stacked bar po dnech/týdnech), donut rozpadu podle akce,
  horizontální pruhy top uživatelé/stanice; tabulka neschválených médií; sekce **Databáze incidentů** (celkový počet,
  unikátní média/stanice, rozsah dat pro kontrolu retence). Endpointy dědí FallbackPolicy (auth).

## Retence dat (NIS2)

Centrální nastavení v `AppSettings` (konzole → Nastavení → Retence dat): `retention.enabled`, `retention.incidentDays`
(default 365), `retention.lastRun`. Samotné mazání dělá **API** (`RetentionService`, BackgroundService à 6 h) – jako
jediné má na DB delete práva (`db_datawriter`). Smaže incidenty starší limitu (`ExecuteDeleteAsync`) a zapíše `lastRun`.
Konzole má na `AppSettings` jen write (ne delete na `Incidents`), proto je enforcement v API.

## Pending (roadmap)

| Položka | Popis |
|---------|-------|
| Zavřít HTTP 5050 | NIS2 – jen HTTPS (firewall block / přebindovat API na SQL-04) |
| Per-serial blocklist | Zákaz konkrétního média, near-real-time k agentům (přednost před whitelistem) |
| Hardening konzole | gMSA místo LocalSystem; dedikovaná `USB-Guardian-Admins`; HTTPS konzole; přesun API na .213 |
| **Retence deníku** | `sp_PurgeActivityLog` existuje, ale **nikdo ji nevolá** – doplnit `activity.retentionDays` do Nastavení a volání do API (vzor: `RetentionService`) |
| ~~Lokální konzole na fleetu~~ | **Rozhodnuto 04.09.2026: na fleetu ZAPNUTÁ, výhradně pro lokálního admina stanice.** Šablona v repu zůstává `false` (bezpečný default pro jiné prostředí), balíček pro fleet se staví s `true`; build na opačný stav upozorní |
| Toast Privilege Separation | Helper process v user session – jednosměrné Pipes SYSTEM → user |

> Hotovo (dřív pending): Admin UI (Blazor konzole + AD sync), **šifrovaná komunikace agent↔API**
> (self-cert + pinning), centrální nastavení (vynucování/přístup/e-mail + alerty), **publikační/podpisový
> workflow whitelistu** (klient = 1:1 kopie serveru, viz níže).

## Publikační/podpisový workflow whitelistu (automatický, klient = 1:1 kopie serveru)

Agent dostává **jen podepsanou verzi**. Podpis je **interní RSA-4096 klíč** USB Guardianu (jeho public =
`whitelist_public.pem` na agentech), NE AXIMA code-signing cert ani CA. Server drží v DB **přesný podepsaný blob**
(`WhitelistVersions.Json`, `NVARCHAR(MAX)`) + podpis (`Signature`, `NVARCHAR(MAX)`), API ho servíruje **verbatim**.
Klient nemá DB → ukládá jako **JSON soubor** (`C:\ProgramData\USBGuardian\whitelist\whitelist.json` + `.sig`).

**Automatický server-side podpis (`WhitelistPublisher`):** po **každé změně katalogu** (přidat/odebrat/aktivovat/edit;
i ručně „Publikovat nyní") konzole sama:

```
změna katalogu → snapshot aktivního katalogu → kanonický whitelist.json blob (nová verze yyyy-MM-dd-vN, platnost
        whitelist.validityDays default 365) → PODEPÍŠE interním klíčem (Whitelist:PrivateKeyPath na .213)
        → uloží Json+Signature, aktivuje (deaktivuje staré)
API: GET /api/whitelist = blob verbatim · GET /api/whitelist/signature = base64 podpis
   ↓ heartbeat hlásí novou verzi (≤2 min)
Agent: stáhne blob+podpis → SignatureVerifier ověří (fail-secure) → uloží whitelist.json (+.sig)
        → WhitelistChecker indexuje (Dictionary VID:PID:SERIAL, O(1) – scale-safe i pro 10k zařízení)
```

Bajt-exact: stejný blob string se **podepisuje** i **servíruje** (`/api/whitelist`) a **ověřuje** (agent) — vše UTF-8
bez BOM (SHA-256 / Pkcs1), takže RSA podpis sedí. **Trade-off (vědomě zvolený):** privátní klíč je na serveru `.213`
(chránit ACL/DPAPI) výměnou za **plnou automatizaci** (žádný ruční offline krok). Offline `WhitelistSigner` zůstává
jako nástroj pro generování klíčů / ruční ověření.

## Vynucování: server → agent + lokální break-glass (Fáze 2+3)

**Fáze 2 – distribuce politiky:** `HeartbeatController` vrací `Enforce` (z `AppSettings policy.enforce`, .213 = zdroj
pravdy). Agent (`WhitelistSync`) ho při každém heartbeatu předá do `PolicyState`; `PolicyEnforcer` pak místo fixního
lokálního `policy.mode` použije **efektivní režim** (`PolicyState.EffectiveMode`): server enforce=true → `block`,
false → `warn`. Před prvním heartbeatem fallback na lokální config.

**Auto-re-enable / reconciliace (vypnuté blokování = připoj cokoli):** agent si pamatuje, co **sám** zakázal
(`DeviceBlocker`, perzistně `blocked.json`: PnpDeviceID → klíč VID:PID:SN). `WhitelistSync` po každém cyklu
**reconciliuje**: blokování vypnuté (break-glass/`enforce=false`) → vrátí (`Enable-PnpDevice`) **vše**, co zakázal;
blokování zapnuté → vrátí média, která jsou **mezitím na whitelistu** (schválená); jinak nechá blokované.
**Lokální break-glass vrací média OKAMŽITĚ** (synchronně z konzole 5080, nečeká na cyklus); serverový `enforce=false`
se propíše dalším heartbeatem (≤ interval). Vrací jen disky zakázané agentem (ne ručně zakázané jinde).

**Dříve blokované médium se objeví na whitelistu:** schválení proběhne v konzoli → nová podepsaná verze → agent ji
stáhne (≤ heartbeat) a **invaliduje 5min cache** (`WhitelistChecker.Reload()` z `WhitelistSync` hned po stažení – jinak
by se nově schválené médium rozpoznalo až po vypršení cache). `ReconcileBlocked` v témž cyklu zjistí `IsAllowedKey` =
true a médium **vrátí i při zapnutém blokování**. (Klíč `VID:PID:SN` blokace = klíč indexu whitelistu, `OrdinalIgnoreCase`,
sériák trimovaný na obou stranách.)

**Re-blokace připojených médií (symetrie k auto-re-enable):** agent blokuje jen na **nové** připojení (WMI event).
Když se médium vrátí break-glassem a pak se blokování **zapne zpět** (override zrušen / `enforce=true`), médium zůstane
připojené a samo se znovu nezablokuje. `DeviceMonitor.ReEnforceConnectedDevices()` to dožene: projde připojená USB/SD
média a **znovu zablokuje** ta neschválená, která ještě nejsou blokovaná (idempotentní – schválená i už-blokovaná
přeskočí). Volá se **každý reconcile cyklus, když je blokování ON** (self-healing) a **okamžitě** při „Zapnout blokování
zpět" v lokální konzoli.

**Spolehlivost vracení (`UnblockDevice`):** Enable se hledá nejdřív **přesnou shodou** `Get-PnpDevice -InstanceId`
(jako ruční `Enable-PnpDevice -InstanceId '…'`), pak fallbackem `-like`. Výsledek: `ENABLED` (povoleno → odebrat ze
seznamu), `GONE` (médium už není připojené → bereme jako vyřešené a odebíráme, ať nezůstane viset; příští připojení
se vyhodnotí znovu), `FAILED` (skutečné selhání → zalogovat a ponechat, příští reconcile zkusí znovu). V lokální
konzoli je vidět **počet právě blokovaných** a tlačítko **„Vrátit všechna média hned"** (`POST /api/unblock-all`).

**Fáze 3 – lokální break-glass (offline):** lokální **admin** stanice může v lokální konzoli (`127.0.0.1:5080`,
admin-only, loopback) **dočasně vypnout blokování** (`POST /api/override?hours=N`, strop 72 h) — pro práci, když
stanice nedosáhne na .213. Override je **perzistovaný** (`C:\ProgramData\USBGuardian\override.json` → přežije restart),
**logovaný** jako auditní incident (`Action=OverrideDisabled`, kdo/jak dlouho) a nahlášený na .213. **Při příštím
spojení se serverem se override ZRUŠÍ** (úspěšný heartbeat → `PolicyState.OnServerHeartbeat` → server reasertuje politiku).
Efektivní režim: `override aktivní ? warn : (server přijat ? enforce : lokální default)`.

**Latence blokace + notifikace:** agent vynucuje **hned na `Win32_DiskDrive` connect** (nečeká na spárování drive-letteru
→ minimalizuje okno, kdy se médium stihne namountovat) a po zápisu toastu **okamžitě spustí ToastHelper task**
(`schtasks /Run`), takže hláška „médium nebylo schváleno" vyskočí do pár sekund. **Limit (user-mode agent je reaktivní):**
Windows removable storage mountuje velmi rychle, takže krátký okamžik před `Disable-PnpDevice` nelze plně eliminovat.
Pro **garantované zabránění připojení** (médium se vůbec neobjeví v Exploreru) je třeba doplnit Windows **Device
Installation Restrictions / Removable Storage Access GPO** nebo kernel storage filter driver – roadmap.

## Watchdog – Task Scheduler

```
Task Scheduler (\USBGuardian\USBGuardian-Watchdog)
    ↓  každé 3 minuty + při startu systému
Kontrola: běží "USB Guardian" service?
    ↓ NE
Start-Service + Event Log ID 200 (Warning)
    ↓ selhání
Event Log ID 500 (Error) – nutný zásah IT
```

- Běží pod **SYSTEM** – nezávisle na přihlášeném uživateli
- Útočník musí zastavit **service i scheduled task** – více kroků, více stop
- Registrace: `scripts\Register-Watchdog.ps1` (auto-elevace UAC)
