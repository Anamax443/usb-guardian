# HANDOFF – předání projektu USB Guardian

*🇨🇿 Čeština · [🇬🇧 English](HANDOFF.en.md)*

**Datum:** 2026-06-19 · **Repo:** `Anamax443/usb-guardian` · **Autor:** Milan Trnka (AXIMA)

Dokument pro toho, kdo projekt přebírá. Architektura: [docs/architecture.md](docs/architecture.md),
funkční popis: [README.md](README.md).

## 1. O co jde

Monitoring paměťových médií na firemních stanicích (NIS2). Agent na stanici detekuje připojené
USB/SD/disk, porovná s podepsaným whitelistem a varuje / blokuje; incidenty pushuje na API.
Serverová konzole agreguje data, drží inventář stanic z AD a ukazuje, kam chybí agent.

## 2. Živý stav (Current Live State)

| | |
|---|---|
| **Doména** | `domena.loc` |
| **DB** | SQL Server `SQL_SERVER` (= `SQL_SERVER_IP`), databáze `USBGuardian`, skripty `database/01–07` aplikované, **`08_deploy_ignored.sql` = trvalé vyřazení stanice (Ignorovat)**, **`09_activity_log.sql` = deník provozu (04.09.2026)**; **+ `GRANT DELETE ON dbo.WhitelistDevices` účtu konzole** (mazání z katalogu – aplikováno ručně) **+ granty na `ActivityLog`**: `SELECT,INSERT` pro `APP_SERVER$` i `gmsa-api$`, `EXECUTE ON sp_PurgeActivityLog` pro `gmsa-api$` |
| **API** | `SQL_SERVER`, Windows služba „USB Guardian API", install `C:\USBGuardian.Api`, gMSA `DOMENA\gmsa-api$`; **HTTPS `:5443`** (self-signed, **PIN `API_CERT_THUMBPRINT`**) – HTTP `:5050` zavřeno v produkci, jen `Development`. **Živá verze přes `GET /api/version`** |
| **Verze/commit (kontrola)** | konzole patička + `:4200/api/version`; API `:5443/api/version`; agent hlásí commit → konzole „Agent verze". Vše stampuje `git rev-parse` (MSBuild) |
| **Admin konzole** | **živá** `http://APP_SERVER_IP:4200/` (`APP_SERVER`), služba `USBGuardianConsole`, `C:\Apps\USBGuardianConsole`, self-contained |
| **Účet konzole** | **LocalSystem** = `DOMENA\APP_SERVER$` (SQL grant: read vše + write Computers/WhitelistDevices/WhitelistVersions/AppSettings; **+ DELETE na `WhitelistDevices`** – mazání z katalogu, jinak ✕ hodí „DELETE permission denied") |
| **Autorizace konzole** | AD `DOMENA\IT-Admins` + whitelist `DOMENA\it-admin` (+ DB seznam z Nastavení) |
| **Šifrování agent↔API** | HTTPS + **pinning otisku** (bez CA) — ověřeno end-to-end (heartbeat OK z PC-01) |
| **AD sync** | zapnutý 60 min + on-demand; **213 v AD, ~212 bez agenta** |
| **Live commit** (04.09.2026 15:21) | **konzole `3a6a2b2`** (redeploy: kontrola „Fronta incidentů (spool)" ověřena naživo přes `/api/health` – 16 kontrol, nová hlásí `ok`/`prázdná`) · **API `a6bcfaf`** (redeploy: durabilní spool fronta incidentů `8fbfa6d` + monitoring spoolu `2766344`, plus dřívější `297ac7a`) · **agent beta `924b9b8`** (BARTKOVAJW11, CERNYSW11, TRNKAMW11N) · **agent stable `cb8ef1d`** (PC-01/TRNKAMW11, zbytek fleetu — `56b4235`/expirace whitelistu je zatím jen v gitu, na fleet nenasazeno). Lokální konzole ověřena end-to-end na CERNYSW11 — viz 5.11. Bezpečnostní audit + náprava — viz 5.12 |
| **Rozvoz agenta – osvědčený postup** | balíček → archiv `…\USBGuardianAgentVersions\<commit>` → **beta na jednu stanici** (dočasně přepsaný `update-beta.txt`) → ověřit → beta na zbytek → teprve pak **stable**. Log `…\deploy\update-agent.log`; „Agent verze" v konzoli se projeví až dalším heartbeatem (≤2 min), takže hned po rozvozu tam ještě chvíli svítí stará verze |
| **Konzole – stránky** | Přehled (filtr+kumulace+řazení, kapacita, **export CSV + manažerský report s grafy**), Stanice (AD inventář + „Zmlklo agentů" + „Vyžádat data" + **Nasazení / hromadné vyřadit-zařadit**), Whitelist (**kapacita + filtr katalogu + auto-publish podepsané verze**), Nastavení (vynucování/přístup/email/alerty/dohled/auto-enrollment+default PC/retence/**Údržba: reload nastavení**), **Databáze**, **Kontroly** (health checks), Dokumentace (+HTML animace) |
| **Enforcement (F1-3)** | **whitelist 1:1** (auto-podpis serverem, interní RSA klíč na APP_SERVER) → **vynucování** server→agent (`policy.enforce` v heartbeatu) → **break-glass** (lokální konzole 5080, offline, logováno, zruší se při sync) + **auto-re-enable** + reconciliace s whitelistem. Lokální konzole: restart služby, break-glass, seznam whitelistu |
| **Deploy účty (oddělené vrstvy)** | **klienti:** gMSA `DOMENA\gmsa-deploy$` – v `Workstation-Admins`, admin **jen na stanicích**, task `USBGuardian-AutoDeploy` na `APP_SERVER`. **servery:** gMSA `DOMENA\gmsa-srvdeploy$` – lokální admin **jen na SQL_SERVER**, task `USBGuardian-ApiDeploy` na `APP_SERVER` — **úloha vznikla až 04.09.2026** (do té doby tam byl jen skript `Deploy-Api.cmd`, takže se API od června nenasazovalo; viz 5.10). **Konzole (`APP_SERVER$`) není admin nikde.** Od 03.09.2026 – jeden účet už nedrží fleet i server současně |
| **Agent (test) PC-01** | **PILOT ÚSPĚŠNÝ** – `PC-01` (vlastní workstation); služba „USB Guardian" RUNNING, heartbeat + **incidenty tečou do DB**. Agent live **`f2bb194`** – atribuce uživatele, klient 100% (watchdog+toast), **enforcement F1-3 + auto-re-enable + spolehlivý unblock + re-blokace připojených médií**. Update agenta chce elevaci (UAC) → spustí uživatel (build staged na APP_SERVER) |

## 3. Klíčová rozhodnutí (proč)

- **Push, ne pull** – 500+ klientů za NATem/firewallem; agentovi stačí odchozí spojení.
- **Dvouvrstvě** – operativa (konzole, AD sync) na app serveru `APP_SERVER`, DB jen úložiště na SQL_SERVER.
  (Pozn.: API zatím běží na SQL_SERVER; přesun na APP_SERVER je naplánovaný hardening.)
- **Konzole = .NET/Blazor**, ne Node – reuse EF modelů z API (slinkované `DbModels`/`AppDbContext`),
  jeden jazyk, na serveru už ASP.NET Core je.
- **Lokální konzole agenta přes `HttpListener`**, ne Kestrel – agent nepotřebuje ASP.NET Core runtime.
- **Klíčování na hostname, ne IP** – stanice mají dynamické IP.
- **Privátní RSA klíč whitelistu nikdy na serveru** – publikace podepsané verze = offline krok (NIS2).
- **Šifrování bez CA** – API si vyrobí vlastní self-signed cert (`MachineKeySet`, NE EphemeralKeySet!),
  agent ho ověří **pinningem otisku**. Nezávislé na firemní CA / externích certech.
- **Centrální nastavení v DB** (`AppSettings`) – vynucování, přístup, e-mail; agent zatím jede dle lokálního
  `policy.mode` (distribuce přes heartbeat je další krok).
- **Portabilita** – žádné firemní hodnoty v kódu; vše v `*.local.json`, doména z `new DirectoryEntry()`.

> Opravené latentní bugy v repu: chybějící authorization policy `USBGuardianClients` (controllery vracely 500);
> `EphemeralKeySet → MachineKeySet` (jinak Schannel neudělá server TLS handshake).

## 4. Deploy konzole (ručně, z PC-01)

it-admin má admin na `APP_SERVER`; WinRM byl zavřený → deploy přes **SMB + remote `sc.exe`** (port 135/445):

```powershell
dotnet publish server\USBGuardian.Admin -c Release -r win-x64 --self-contained -o D:\deploy\USBGuardianConsole
sc.exe \\APP_SERVER_IP stop USBGuardianConsole
robocopy D:\deploy\USBGuardianConsole \\APP_SERVER_IP\C$\Apps\USBGuardianConsole /E /XF appsettings.local.json
sc.exe \\APP_SERVER_IP start USBGuardianConsole
```

Firewall `:4200` byl vytvořen přes DCOM/CIM. Konfigurace na serveru:
`C:\Apps\USBGuardianConsole\appsettings.local.json` (viz `*.example`).

## 5. Stav a další kroky

### 5.1 Hotovo a živé
- **DB / incidenty = 100 %** — agent → API → DB → konzole celá cesta jede (Přehled ukazuje incidenty z PC-01).
  **Klíčový fix:** API mělo nedodělaný queue refactor — `IncidentsController` vyžadoval `IncidentQueue`, ale
  `Program.cs` ho **neregistroval v DI** → **500 na každý `/api/incidents`** (heartbeat bez té závislosti jel).
  Po `AddSingleton<IncidentQueue>` + `AddHostedService<IncidentQueueWorker>` controller vrací 202 + worker zapisuje.
- **Verze/commit na všech komponentách** — konzole + API mají `GET /api/version`, agent hlásí reálný commit
  (`AppInfo` + MSBuild `git rev-parse` stamp) → v konzoli „Agent verze" je vidět nasazený commit per stanice.
- **Konzole:** dlaždice „Zmlklo agentů" (práh `comm.silentAfterMinutes`), „Vyžádat data" (`ReportNow` přes
  `AppSettings cmd.report.<HOST>`), řaditelná tabulka „Detailně", auto-enrollment orchestrátor (default VYPNUTO+dry-run).
- **Fix trim sériáku** — WMI vrací serial s mezerami (`"WX92D622N4PE    "`) → nesedělo s whitelistem
  („Schváleno=ne" + agent nepoznal whitelisted). Agent trimuje při WMI parse, konzole v `Approved`.
- **Konzole – kapacita, export, retence, DB stránka (HOTOVO):**
  - **Kapacita** média v Přehledu (kumulovaný i detailní) a ve Whitelist katalogu (dotahuje se z incidentů).
  - **Export** z Přehledu (dědí filtr): `⬇ CSV` (Excel CZ) a `📊 Report` = manažerský souhrn (KPI + top uživatelé/
    stanice/média), tisknutelné HTML → PDF. Endpointy `/export/incidents.csv` a `/export/manager` (chráněné auth).
  - **Retence dat** (Nastavení → Retence dat): `retention.enabled/incidentDays/lastRun` v `AppSettings`; mazání dělá
    **API** (`RetentionService`, à 6 h, `db_datawriter`). Default vypnuto. **Vyžaduje redeploy API** (viz 5.2).
  - **Databáze** (nová stránka): počty v tabulkách, rozsah incidentů, výpis AppSettings, posledních 20 incidentů.
  - **Spolehlivý commit-stamp** ve všech komponentách (konzole/API/agent) — footer/`/api/version` teď ukazují přesně
    nasazený commit i u nesouvisejících změn (generovaný `GitCommit.g.cs`, vynutí recompile při změně commitu).
- **Lokální konzole agenta (rozšířeno):** `http://127.0.0.1:5080/` (loopback, admin-only, read-only; `localConsole.enabled`).
  Nově ukazuje **seznam schválených zařízení (whitelist)** + verzi agenta (commit), vedle stavu whitelistu, WMI,
  fronty, připojených médií a posledních událostí. Diagnostika přímo na stanici (i offline od serveru).
- **Atribuce uživatele (HOTOVO + ŽIVÉ)** — agent běží jako SYSTEM, dřív hlásil strojový účet (`HOST$`). Nový `SessionUser`
  (WTS API: `WTSGetActiveConsoleSessionId` + enumerace aktivních session, `WTSQuerySessionInformation`) zjistí
  reálného přihlášeného uživatele → `DOMAIN\user` v incidentu, logu i Toastu. Fail-safe: bez přihlášeného uživatele
  fallback na strojový účet (incident se zapíše vždy). **Ověřeno živě na PC-01:** nasazen agent commit `428a262`,
  nové incidenty zapisují `DOMENA\it-admin` (dřív `PC-01$`).

### 5.2 Nasazené komponenty
- **API na SQL_SERVER (živé `19e4018`):** `ReportNow` v heartbeatu, DI fix fronty, `/api/version`. Deploy přes
  **gMSA** (build staged na `APP_SERVER` `C:\Apps\USBGuardianApiPublish` → gMSA má lokální admin na SQL_SERVER). Pozor při
  redeployi: **počkat na `STOPPED`** (jinak je `USBGuardian.Api.exe` zamčený → robocopy `FAILED` → stará verze běží dál).
- **Agent na PC-01 (auto-nainstalovaný):** whitelist poll 2 min, startovní sken, `ReportNow`, trim sériáku,
  reálná verze. Fixy: `onExpiredWhitelist`, publicKeyPath vůči exe, GUID, odebrán Sqlite.

### 5.3 Auto-enrollment agenta — PILOT ÚSPĚŠNÝ (PC-01), rozšířit na fleet
Cíl: konzole 24/7 po AD syncu sama nasadí agenta na stanice bez agenta. **Least-privilege:** konzole zapíše seznam
cílů (`deploy.targetsFile`), instalaci dělá **scheduled task na APP_SERVER pod gMSA** (jen ten účet má admin na klientech).
- **Funguje end-to-end:** gMSA `gmsa-deploy$` (v `Workstation-Admins` = admin na klientech, bez hesla), task `USBGuardian-AutoDeploy`,
  `Deploy-AgentFleet.ps1` (runspace pool PS5.1, `sc.exe \\HOST create` přes cmd). **PC-01 se nainstaloval bez jakýchkoli creds**,
  služba běží, heartbeat + incidenty tečou. Skripty: `New-DeployGmsa.ps1`, `Install-Agent.ps1`/`Uninstall-Agent.ps1`,
  Detail: [docs/auto-deploy-setup.md](docs/auto-deploy-setup.md).
- **Kompletní klient (HOTOVO):** balíček nově nese i **ToastHelper** (notifikace uživateli) + scheduled tasky.
  Sestavení: `scripts\Build-AgentPackage.ps1` → agent (root) + `ToastHelper\` (self-contained) + `tasks\`.
  `Deploy-AgentFleet.ps1` registruje na klientovi **PS-free** dva tasky: **watchdog** (`schtasks … sc start`, à 3 min)
  a **ToastHelper** (`schtasks /XML`, trigger přihlášení+odemčení, běh v user session, least-privilege).
  Na **PC-01 aplikováno a ověřeno** (ToastHelper.exe v `…\ToastHelper\`, oba tasky Ready). Bez ToastHelpera by se
  incidenty zaznamenaly, ale uživatel by varování neviděl.
- **Rozšíření na fleet:** GPO trust publisheru na klienty (5.4), v Nastavení zapnout (dry-run → ostrý), `PC-01 → .180 → fleet`.

### 5.3b Publikační/podpisový workflow whitelistu — HOTOVO, AUTOMATICKÝ (klient = 1:1 kopie serveru)
Odemyká doručení katalogu k agentům (předtím se změny v konzoli nedostaly – verze se nebumpla + chyběl
`/api/whitelist/signature`). **Plně automatické (`WhitelistPublisher`):** po **každé změně katalogu**
(přidat/odebrat/aktivovat/edit; i ručně „Publikovat nyní") konzole sama snapshotne aktivní katalog → kanonický
`whitelist.json` blob (verze `yyyy-MM-dd-vN`, validita `whitelist.validityDays` default 365) → **PODEPÍŠE interním
RSA klíčem na serveru** (`Whitelist:PrivateKeyPath`) → uloží `Json`+`Signature`, aktivuje. API `GET /api/whitelist`
vrací blob **verbatim** + `GET /api/whitelist/signature` → agent stáhne (≤2 min), ověří (fail-secure), uloží JSON soubor.
**Byte-exact** (týž blob se podepisuje/servíruje/ověřuje, UTF-8 bez BOM, SHA-256/Pkcs1). Agent matchuje **Dictionary O(1)**
(scale-safe i 10k). Podpis = **interní klíč** USB Guardianu (public = `whitelist_public.pem` na agentech), ne CA/AXIMA cert.
**Trade-off (zvolený):** privátní klíč je na `APP_SERVER` (chránit ACL) výměnou za automatizaci. DB: `database/07_whitelist_publish.sql`
(`Json`+`Signature`→`NVARCHAR(MAX)`). **Nasazeno:** konzole + API + DB migrace. **Setup (uživatel):** umístit privátní klíč
na `APP_SERVER` a nastavit `Whitelist:PrivateKeyPath` v `appsettings.local.json`. Server = DB (blob), klient = JSON soubor.

### 5.4 Prostředí pro PS skripty (DŮLEŽITÉ – AXIMA gotchas)
- **AllSigned (GPO):** každý PS skript co tam běží **musí být podepsaný** prod certem `CN=powershell.domena.loc`
  (`-ExecutionPolicy Bypass` to NEOBEJDE). Podpis přes službu `APP_SERVER:4100` / share `\\herkules\ITC\UTIL\04-manualy-instalace\PS-scripty`.
  Týká se `Deploy-AgentFleet.ps1` (na APP_SERVER) a `Watch-USBGuardian.ps1` (na klientech).
- **Před podpisem CRLF + UTF-8 BOM** (repo má LF → jinak `HashMismatch`).
- **Trusted Publisher:** pro neinteraktivní běh (gMSA/SYSTEM) musí být podpisový cert v `LocalMachine\TrustedPublisher`
  na APP_SERVER i klientech (přidáno na PC-01+APP_SERVER; **fleet přes GPO** – cert export `_AXIMA-CodeSign-publisher.cer` na share).

### 5.3c Vynucování server→agent + break-glass — HOTOVO (Fáze 2+3)
**Fáze 2:** heartbeat vrací `Enforce` (z `AppSettings policy.enforce`, APP_SERVER = pravda); agent (`WhitelistSync`) ho předá
do `PolicyState`, `PolicyEnforcer` použije efektivní režim (enforce → block, jinak warn; před heartbeatem lokální default).
**Fáze 3 (break-glass):** lokální admin v lokální konzoli (`127.0.0.1:5080`, `POST /api/override?hours=N`, strop 72 h)
dočasně vypne blokování pro **offline** práci. Perzistované (`override.json`, přežije restart), **logované** jako incident
(`Action=OverrideDisabled`, kdo/délka) → nahlášeno na APP_SERVER. **Při příštím spojení se serverem se override ZRUŠÍ**
(`PolicyState.OnServerHeartbeat`). **Vyžaduje redeploy API (APP_SERVER→SQL_SERVER) + redeploy agenta.**

**Vypnout blokování = vrátit VŠE hned (oprava spolehlivosti, redeploy agenta):** lokální „Vypnout blokování"
(break-glass) volá `UnblockAll()` **synchronně → média se vrátí okamžitě** (žádné čekání na 2min cyklus; serverový
`enforce=false` se propíše až heartbeatem, to je OK). `UnblockDevice` zrobustněn: Enable přes **přesný `-InstanceId`**
(jako ruční `Enable-PnpDevice`), fallback `-like`; rozlišuje `ENABLED`/`GONE` (odpojené médium odebere ze seznamu, ať
nezůstane viset)/`FAILED` (zaloguje + ponechá na retry). Lokální konzole nově ukazuje **počet blokovaných** +
tlačítko **„Vrátit všechna média hned"** (`POST /api/unblock-all`).

**Symetrie – zapnout blokování = znovu zablokovat připojené (oprava, redeploy agenta):** agent blokuje jen na NOVÉ
připojení (WMI), takže médium vrácené break-glassem zůstalo po zapnutí blokování zpět připojené a viditelné
(„BLOKUJE, ale flashku vidím, Zablokováno teď: 0"). Nově `DeviceMonitor.ReEnforceConnectedDevices()` projde připojená
USB/SD média a **znovu zablokuje** neschválená (idempotentní – schválená i už-blokovaná přeskočí). Volá se **každý
reconcile cyklus když je blokování ON** (self-healing) + **okamžitě** při „Zapnout blokování zpět" v lokální konzoli.

**Dříve blokované médium zařazeno na whitelist → vrátí se (oprava cache):** `ReconcileBlocked` při `IsAllowedKey`=true
vrátí médium i při zapnutém blokování. **Bug:** `WhitelistChecker` cachoval whitelist 5 min a stažení nové verze cache
neinvalidovalo → schválení se projevilo až za ~5 min (a `ReEnforce` mezitím mohl médium znovu zablokovat). Fix:
`WhitelistSync` po stažení volá `WhitelistChecker.Reload()` → nová verze platí ihned, unblock v témž reconcile cyklu.

### 5.6 Kontroly stavu + plánovaný restart služeb — HOTOVO (28.08.2026)
**Co to řešilo:** 28.08.2026 se zjistilo, že služba „USB Guardian API" na SQL_SERVER byla **od poloviny července
zastavená** (`sc query` → STOPPED, exit code 0 = zůstala dole po deployi/restartu serveru). Agent na `PC-01` běžel,
incidenty si ukládal do fronty (7 souborů, nejstarší 02.07.), ale na server **6 týdnů nic nedoteklo**. Konzole to
nikde neřekla nahlas — dlaždice „Zmlklo agentů" ukazovala `1` a nikdo se nekoukl. Po ručním startu služby se fronta
sama dosypala.

**Kontroly stavu (nová stránka `/kontroly`, `Health/HealthService.cs`):** 16 read-only kontrol ve třech skupinách —
*Sběr dat* (databáze, **dostupnost API**, **fronta incidentů (spool)**, **stáří nejnovějšího incidentu**, zmlklí agenti, pokrytí stanic),
*Whitelist a politika* (aktivní verze / podpis / expirace, katalog vs. publikace, podpisový klíč, vynucování),
*Provoz a údržba* (e-mail, retence, AD sync, auto-enrollment, plánovaný restart, shoda verzí konzole/API/agentů).
Každá kontrola vrací **co naměřila + proč na tom záleží + co s tím**. Stavy jsou schválně čtyři, aby šlo poznat
rozdíl mezi rozbité a vědomě vypnuté: `v pořádku` / `varování` / `CHYBA` / `vypnuto` (+ `čeká na data`).
Strojově totéž na **`GET /api/health`** — JSON, **HTTP 200 = OK, 503 = aspoň jedna chyba** (kontrakt pro externí dohled).
Nastavení v Nastavení → Kontroly stavu: `health.apiUrl` (kam se ptát na `/api/version`), `health.maxIncidentAgeHours` (default 48).

**Plánovaný restart služeb (`Maintenance/ServiceRestartService.cs`, Nastavení → Plánovaný restart služeb):**
denně v nastavenou hodinu projde seznam cílů `HOST|Název služby` (host prázdný = tenhle server). Běžící službu
restartuje, **zastavenou nastartuje** — to je ta pojistka proti výpadku výše. Nastavení: `svc.restart.enabled`,
`svc.restart.at` (HH:mm), `svc.restart.targets`, výsledek do `svc.restart.lastRun` (čte ho i kontrola).
Tlačítko **„Restartovat teď"** dělá totéž hned = ověření, že sedí práva i názvy služeb.
Když stanice/server nebyl v okně dostupný, restart se dohání **nejvýš 2 h**, pak se čeká na další den.

> **Práva:** restart provádí **účet služby konzole** (`LocalSystem` = strojový účet `APP_SERVER`). Na cizím serveru na to
> musí mít právo, jinak běh vrátí `CHYBA – přístup odepřen` (a je to vidět v Kontrolách). Pro API na SQL_SERVER je
> potřeba účtu konzole povolit ovládání té jedné služby (`sc sdset`), nebo počkat na přesun API na `APP_SERVER` (5.5).

**Plánovaný restart i na klientovi (`agent/USBGuardian/SelfRestart.cs`):** stejná pojistka na stanici — agent se
jednou denně sám restartuje (`sc stop` → pauza → `sc start` z odděleného `cmd.exe`, protože služba se nemůže
restartovat zevnitř). Výchozí hodnoty z `agent.config.json` (`selfRestart.enabled/at`), přepínatelné z **lokální
konzole** (karta „Plánovaný restart", admin-only), stav perzistovaný v `C:\ProgramData\USBGuardian\selfrestart.json`.

**Konzole – provozní údaje nahoře:** čas, nasazený commit a tečka dostupnosti DB se přesunuly z patičky do horní lišty.

### 5.7 Vzhled konzole z banky UI — přepínatelný v Nastavení (28.08.2026)
Konzole přestala mít vlastní ručně psanou paletu a bere vzhled z **banky UI**
(repo `Anamax443/Interface-Par`, katalog `mockup/ui-styly-katalog.html`, rozbor `docs/styly.md`).

- **Co se zkopírovalo do `wwwroot/`:** `bank/ui.css`, `bank/fonts.css`, `bank/tokens/style/*.css` (23 stylů)
  a `vendor/fonts/*.woff2` (Cascadia Mono + Inter, latin i **latin-ext** — bez něj by chyběly ř/ě/š/č/ů).
  Banka se **needituje**, generuje ji katalog (`node scripts/build-bank.mjs`) — úpravy patří do katalogu.
- **Pořadí v `<head>`** (závazné): `fonts.css` → `tokens/style/<styl>.css` → `ui.css` → `app.css`.
- **Kostra** (`MainLayout.razor`): `.ui[data-style][data-layout]` + `p-title` / `p-nav` / `p-topnav` / `p-main` /
  `p-status`. Položky menu jsou v jednom poli a vykreslují se do bočního i vodorovného menu → **žádný druhý
  zdroj pravdy** o navigaci. Obal `.ui` dostává výšku z `app.css` (`100vh`), banka si ji sama nenastaví.
- **Přepínání:** Nastavení → **Vzhled konzole** (styl + rozvržení). Ukládá se do `AppSettings`
  (`ui.style`, `ui.layout`), čte `UiStyleCache` (singleton, přenačte se po uložení; DB dotaz na každý render by
  byl zbytečný). Hodnota jde do **cesty k souboru**, proto prochází **whitelistem** známých stylů/rozvržení —
  neznámá hodnota tiše spadne na výchozí `hmi-slate` / `side-nav`.
- **Výchozí = `hmi-slate` + `side-nav`** (Velín — průmyslový panel: ohraničení 2 px, rádius 0, hlavičky verzálkami).
- **`app.css` je teď jen vrstva komponent konzole** (dlaždice, pilulky, bannery, `dl.cfg`, dokumentace) a sahá
  **výhradně přes role** banky (`--pane`, `--dim`, `--accent`, `--ok`, `--crit`, `--row-h`, `--radius` …).
  Žádná barva natvrdo, žádný CSS framework vedle banky.
- **Ověřeno:** build OK; konzole spuštěná lokálně proti neexistující DB (bez zásahu do ostrých dat) —
  všech 7 souborů banky se servíruje (200), kostra i menu se vykreslí, aktivní položka svítí.
  Blazor značí aktivní `NavLink` třídou `.active` a `aria-current="page"`, banka čeká `aria-current="true"` →
  vzhled aktivní položky dodává `app.css`.

> **Před nasazením dalšího stylu:** v katalogu spustit **Zkontrolovat všechny styly**, a to v rozvržení
> `side-nav` — nálezy se liší podle kostry, ne jen podle stylu.

### 5.8 Oddělené deploy účty a nasazení API (03.09.2026)
Do 03.09.2026 držel **jeden** účet (`gmsa-deploy$`) admina na klientech i na SQL_SERVER — kompromitace deploy identity
by tedy sáhla na fleet i na databázový server současně. Rozděleno na tři role, každá bez práv té druhé:

| Role | Účet | Kde je admin |
|---|---|---|
| Klienti (auto-enrollment) | `gmsa-deploy$` | `Workstation-Admins` → jen stanice |
| Servery (deploy API) | `gmsa-srvdeploy$` | lokální admin jen na SQL_SERVER |
| Konzole (běžící aplikace) | `APP_SERVER$` (LocalSystem) | **nikde** |

`gmsa-srvdeploy$` je záměrně **mimo** skupinu `Server Admins` — ta by dala admina na všechny servery. Členství je
lokální, jen na tom jednom stroji.

**Nasazení API** dělal dřív operátor ručními PS bloky (opíralo se o to, že klientský účet je admin na SQL_SERVER).
Nově je to `scripts/Deploy-Api.cmd` + scheduled task `USBGuardian-ApiDeploy` na `APP_SERVER` pod serverovým gMSA:
zastaví službu, **počká na `STOPPED`** (bez toho zůstane `USBGuardian.Api.exe` zamčený, robocopy selže a na serveru
dál běží stará verze — a deploy přitom „proběhl"), zkopíruje bez `appsettings.local.json`, nastartuje a ověří `RUNNING`.
Log v `C:\ProgramData\USBGuardian\deploy\api-deploy.log`, návratový kód je vidět jako Last Result úlohy.

**Dávka, ne PowerShell:** `.cmd` nepodléhá `AllSigned` z GPO, takže se nasazovací krok nemusí při každé změně
znovu podepisovat.

> **Důsledek pro plánovaný restart služeb:** konzole (LocalSystem) na SQL_SERVER admin není a být nemá. Restart
> `USB Guardian API` odtud proto neprojde — buď jí povolit ovládání **té jedné služby** přes `sc sdset`
> (jedna ACE, ne účet s klíči od serveru), nebo restart nechat na serverovém gMSA stejným vzorem jako deploy.

### 5.9 Aktualizace už nasazeného agenta (03.09.2026)
Fleet skript uměl jen **čistou instalaci**. `-ReinstallExisting` šel rovnou na `robocopy` bez zastavení služby —
běžící `USBGuardian.exe` je zamčený, takže by se přepsala část DLL, kopie `.exe` selhala a na stanici by zůstala
**směs verzí**, zatímco deploy hlásí úspěch. Proto je aktualizace samostatná úloha:

`scripts/Update-Agent.cmd <ZDROJ> <HOST | SOUBOR_S_HOSTY> [SLUŽBA]` — zastaví službu, **počká na `STOPPED`**,
zkopíruje, nastartuje a **ověří `RUNNING`**. Stanici bez služby přeskočí (na čistou instalaci je AutoDeploy).
Návratový kód = počet neúspěšných stanic; log `C:\ProgramData\USBGuardian\deploy\update-agent.log`.

Spouští se úlohou **`USBGuardian-UpdateAgent`** na `APP_SERVER` pod `gmsa-deploy$`; seznam stanic v
`C:\ProgramData\USBGuardian\deploy\update.txt` (jeden host na řádek, `#` = komentář).

**Dávka, ne PowerShell** — `.cmd` nepodléhá `AllSigned`, takže změna aktualizačního kroku nevyžaduje podpis.

> **Vytvoření úlohy pod gMSA (gotcha):** `schtasks /Create /RU "…gmsa$"` bez hesla vyrobí úlohu s
> `LogonType=InteractiveToken` → nespustí se („uživatel nebyl přihlášen", event 332). S4U (`/NP`) nemá síťové
> credentials a nedosáhne na `\\HOST\C$`. Funguje jediné: vytáhnout XML fungující úlohy, vyměnit v něm
> `<Command>`/`<Arguments>`/`<URI>`, uložit jako **UTF-16** a založit přes `/XML` — to nese `LogonType=Password`,
> u kterého si heslo gMSA vyzvedne systém.

> **Když deploy úloha začne hlásit `ERROR_LOGON_FAILURE (0x8007052E)`**, není to o právech — je to zastaralá
> lokální kopie hesla gMSA. Spravit na `APP_SERVER`: `Install-ADServiceAccount gmsa-deploy`.

**Ověřeno 03.09.2026:** PC-01 přeskočena z `f2bb194` na `560722b`, kontrola Verze komponent hlásí jedinou
verzi agenta. Zároveň se tím uklidily dva soubory, které od července visely ve frontě.

### 5.10 Deník provozu — nasazení a co u něj nesedí (04.09.2026)
Tabulka `dbo.ActivityLog` + `sp_PurgeActivityLog` jsou v DB, granty vydané (viz Živý stav), konzole i API běží na
`5431dce` a deník se plní: v 8:16 se na stránce **Aktivita** objevily heartbeaty čtyř stanic
(`tep OK (whitelist 2026-06-19-v7, agent b0e1a0d)`). Řádky o komunikaci píše API, operátorské zásahy
(nasazení, aktualizace, vyřazení stanice) píše konzole — obojí do téže tabulky.

**Úloha `USBGuardian-ApiDeploy` na `APP_SERVER` chyběla** a založila se až 04.09.2026 (XML v UTF-16, `LogonType=Password`,
principál `gmsa-srvdeploy$` uvedený SIDem). První běh: Last Result `0`, robocopy rc 3, služba naběhla, `/api/version`
hlásí `5431dce`. Kanál pro nasazení API tedy existuje teprve teď — dřívější text v 5.8 popisoval záměr, ne stav.

**Úklid nikdo nevolá.** `sp_PurgeActivityLog` v DB je, ale v kódu na ni není jediný odkaz — v Nastavení je pouze
`retention.incidentDays`, deník tam vlastní hodnotu nemá. Při 213 stanicích a heartbeatu po 2 minutách to je
řádově **150 tisíc řádků denně**; než se retence zapojí, tabulka poroste bez omezení.

**Nekonzistence lokální konzole na fleetu.** Commit `3c8ba3f` uvádí, že balíček i archiv mají
`localConsole.enabled=false`, ale na `APP_SERVER` má **zdroj nasazení i všechny tři archivované verze
(`f2bb194`, `560722b`, `b0e1a0d`) hodnotu `true`** — poslední sestavení balíčku ji vrátilo zpět. Další běh
`AutoDeploy`/`UpdateAgent` tedy lokální konzoli na stanicích zase zapne. Komentář v `Build-AgentPackage.ps1`
přitom říká pravý opak commitu (konzole **má** být zapnutá, je to break-glass pro člověka v terénu).
**Je to rozhodnutí k udělání, ne překlep** — a od opravy `b0e1a0d` má proti sobě slabší argument: odmítnutí
už uživateli vysvětlí, na co kouká, místo holé 403.

**Opravena pojistka z `3c8ba3f`:** v `Build-AgentPackage.ps1` byl v cestě ke konfiguraci místo `\a` uložený
skutečný bajt BEL (`Config␇gent.config.local.json`), takže `Test-Path` byl vždy `false` a kontrola obsahu se
nikdy nespustila — skript jen hlásil „balíček nemá config". Po opravě kontrola na reálném balíčku projde.

### 5.11 Lokální konzole: zaseklý port po restartu → chybné rozpoznání admina (04.09.2026)
Kolega na CERNYSW11 hlásil, že se lokální konzole (`127.0.0.1:5080`) donekonečna načítá (bílá stránka,
točící se kolečko). Vyšetření odhalilo řetěz nezávislých problémů, opravených postupně:

1. **Agent nepsal do Event Logu vůbec** — `ClearProviders()` v `Program.cs` smazal i `EventLog` provider,
   takže služba pod SYSTEM po sobě nenechávala žádnou stopu. Přidán `AddEventLog` (`logging.eventLogLevel`,
   default `Warning`, ať minutový sync log nezaplaví) + hlášky lokální konzole vždy na Information.
2. **Instalace nechávala běžet starý proces.** `Install-Agent.ps1` čekal na `Stopped` v `try/catch`, takže se
   timeout tiše polkl a kopírovalo se i s běžícím procesem — nová instance pak nezabrala port a lokální
   konzole zůstala mrtvá do dalšího restartu. Oprava: skutečné ověření zastavení, v krajním případě tvrdé
   ukončení procesu, ověření `RUNNING` po startu + založení Event Log zdroje.
3. **Lokální konzole se po obsazeném portu vzdávala napoprvé** — přidán retry (6× po 5 s), ať přežije
   krátké okno, kdy port ještě drží dobíhající starý proces.
4. **`Archive-AgentVersion.cmd` / `Set-AgentVersion.cmd` psaly log do `C:\ProgramData\USBGuardian\deploy`**
   — adresář ACL zamčený na `SYSTEM`/`Administrators` (drží ho běžící agent). `mkdir` tiše selhal, přesměrování
   do neexistující cesty spadlo na obrazovku, ale skript i tak vypsal „HOTOVO", i když se nezkopírovalo nic.
   Log přesunut vedle archivu (`%ARCH%\_logs`), přidána tvrdá kontrola po kopii.
5. **Rozvoz bety byl jen ruční (RDP + `schtasks /Run`).** Přidáno tlačítko „⇪ Rozvézt betu na vzorek"
   v Nastavení (`DeployTrigger.SpustBetuAsync`) a pojistka `BetaRolloutService` — hlídá `VERSION.txt` v
   beta kanálu, a když se operátor nestihne, po intervalu (default 30 min, nastavitelné) rozveze sám.
6. **Provozní past (ne bug):** `schtasks /Run` spuštěné dřív, než `Set-AgentVersion.cmd` dopíše `VERSION.txt`,
   rozveze ještě PŘEDCHOZÍ commit — beze slova o chybě. Zjištěno porovnáním časů v logu (`Last Run Time` tasku
   vs. časové razítko `VERSION.txt`). Pořadí: archivovat → nastavit kanál → **teprve pak** spustit rozvoz.
7. **Skutečný nález u cernyse:** i po ověřeném restartu (interaktivní token správně nesl `Administrators`
   jako `Enabled`) konzole pořád odmítala. Nová diagnostika na stránce odmítnutí (syrový výpis skupin
   z tokenu) ukázala, že **síťový NTLM token přes loopback `Administrators` nenesl vůbec** — ne jen jako
   deny-only (to řešila starší oprava `b0e1a0d`), skupina tam chyběla úplně. `IsLocalAdmin` má proto třetí,
   spolehlivý krok: přímý dotaz na místní SAM (`System.DirectoryServices.AccountManagement`,
   `GroupPrincipal.GetMembers(recursive: true)`), nezávislý na tom, co si zrovna nese konkrétní typ přihlášení.

**Ověřeno živě:** CERNYSW11 na `924b9b8` ukazuje cernysovi plnou admin konzoli (WHITELIST, WMI MONITORING,
VYNUCOVÁNÍ, SLUŽBA, PLÁNOVANÝ RESTART) — dřív viděl jen věčné načítání, pak (po dřívější opravě `cb8ef1d`)
alespoň zjednodušenou uživatelskou stránku, teď správně plný přístup podle skutečného členství ve skupině.

### 5.12 Externí bezpečnostní audit + náprava (04.09.2026)
Nezávislý statický audit veřejného repa (architektura 9,0/10, security implementation 7,3/10,
testování 4,5/10, celkem 7,9/10) našel několik konkrétních chyb – ověřeny přímo v kódu, ne převzaty
bez kontroly. Opraveno dnes, každá věc samostatný commit (kvůli izolovatelnosti při rozbití):

- **`56b4235`** – vypršelý whitelist tiše autorizoval zařízení: `WhitelistChecker.IsAllowed()`
  jen zalogoval warning a pokračoval v lookupu; `PolicyEnforcer.HandleDevice()` se vrátil na
  `if (isAllowed) return` dřív, než se `DetermineAction()` podíval na `whitelistStatus` – `onExpired`
  tedy nikdy neplatilo pro zařízení, které v (starém) seznamu zůstalo. Stejná díra byla i v
  `DeviceMonitor.ReEnforceConnectedDevices()` pro už připojená média. **Zatím jen v gitu, na fleet
  nenasazeno** (dohodnuto s uživatelem – code review místo živého testu, protože ověření vyžaduje
  buď skutečně expirovaný produkční whitelist, nebo izolovaný testovací).
- **`033af8a`** – `WhitelistController` měl jednu authorization policy (`USBGuardianClients`) na
  celém controlleru včetně `POST /api/whitelist/devices` – účet stanice tak teoreticky mohl zapisovat
  whitelist, ne jen ho číst. Autorizace přesunuta na jednotlivé akce, nová policy `USBGuardianAdmins`
  (fail-closed) pro zápis. **Nasazeno, vyžaduje `Authorization:AdminGroups` v `appsettings.local.json`
  na `SQL_SERVER`.**
- **`57a0e15` + `a4f28bd`** – API poslouchalo bezpodmínečně na HTTP 5050 i HTTPS 5443. Port teď jen
  v `Development`. **Reálný `appsettings.local.json` na `SQL_SERVER` měl vlastní
  `Kestrel:Endpoints:Http:Url`** – Kestrel čte tuhle sekci nezávisle na kódu a oba zdroje se sčítají,
  ne nahrazují. Pokus vynulovat hodnotu v `IConfiguration` za běhu **nefungoval** (ověřeno izolovaným
  testem mimo repo) – Kestrel pořád najde endpoint „Http" a spadne na chybějící `Url`, což by v
  produkci znamenalo, že se API vůbec nerozjede. Řešení: fail-fast s jasnou hláškou, pokud produkce
  vůbec uvidí tuhle sekci existovat + ruční odstranění sekce z reálného configu na `SQL_SERVER`.
  **Nasazeno a ověřeno** (`/api/version` přes HTTPS OK, HTTP 5050 timeoutuje).
- **`297ac7a`** – `AllowedGroups.Length == 0 → true` (fail-open, komentář to přiznával). Teď fail-fast
  při startu, pokud je prázdné. **Ověřeno před nasazením**, že produkční `AllowedGroups` je vyplněné
  (`AXINETWORK\USB-Guardian-Clients`, `AXINETWORK\SQL Admins2`) – fail-closed tedy nic nerozbil.
  **Nasazeno a ověřeno** (`/api/version` po redeployi OK).
- **`ac73571`** – první C# testovací projekt (`tests/USBGuardian.Agent.Tests`, xUnit), 8 testů,
  regrese na opravu expirace whitelistu. Do té doby repo mělo jen jeden JS test (UI).
- **`8fbfa6d`** – `POST /api/incidents` vracelo `202 Accepted` hned po zařazení batche do
  paměťové fronty (`Channel`) – pád API procesu mezi tímto potvrzením a zápisem do DB batch
  nenávratně ztratil (agent po 2xx posune offset a znovu ho nepošle). Nový `IncidentSpool`
  zapíše batch atomicky na disk (`C:\ProgramData\USBGuardian\incident-spool`, konfigurovatelné
  přes `incidents:spoolPath`) **ještě před** 202; worker soubor smaže až po úspěšném zápisu do
  DB. Cokoliv zůstane na disku (pád, i běžný restart služby) se při příštím startu přehraje
  jako první – existující dedup v `ProcessBatch` dělá případné opakované přehrání neškodným.
  Nový testovací projekt `tests/USBGuardian.Api.Tests` (dosud API nemělo žádné testy), 4 testy.
  **Nasazeno a ověřeno 04.09.2026 15:08** (`/api/version` → `a6bcfaf`, `/api/incidents/queue/status`
  vrací prázdný stav na `SQL_SERVER`).
- **`2766344`** – spool žil jen na disku API serveru (`SQL_SERVER`), konzole běží na jiném stroji
  (`APP_SERVER`) a k tomu adresáři nemá přístup – uvíznutý batch by tak Kontroly stavu vůbec
  nezaznamenaly. Nový anonymní endpoint `GET /api/incidents/queue/status` (stejný vzor jako už
  veřejné `/api/version`) vrací počet čekajících batchů + stáří nejstaršího; nová kontrola
  „Fronta incidentů (spool)" ve skupině *Sběr dat* (viz Živý stav → Konzole – stránky) ho čte
  stejně jako ostatní API kontroly. 3 nové testy na `IncidentSpool.GetStatus()`.
  **Nasazeno a ověřeno 04.09.2026 15:21** – API i konzole (`/api/health` na `APP_SERVER` vrací
  16 kontrol, nová hlásí `ok`/`prázdná`).
- **`e8d702c`** – dedup klíč `timestamp-na-sekundu|serial|vendor` chyběl `ProductId`/`PnpDeviceId`:
  dvě různá zařízení stejného vendoru se stejným (u levných USB kusů často sdíleným, generickým)
  sériovým číslem, připojená ve stejné sekundě, by kolidovala – druhý incident by dedup tiše zahodil
  jako duplikát prvního. `MakeKey` teď bere navíc oba údaje; skutečný resend (retry po výpadku) i
  pozdější doplnění `DisconnectedAt` zůstávají správně spárované (bajtově stejný záznam, `PnpDeviceId`
  se nemění). Zpřístupněno testům přes `InternalsVisibleTo`, 3 nové testy. **Zatím jen v gitu, na
  fleet nenasazeno** (vyžaduje redeploy API).
- **`0bc6709`** – nalezeno při dnešní kontrole všech používaných API endpointů a jejich oprávnění
  (mimo původní seznam z auditu): API mělo na rozdíl od konzole zabezpečení jen po jednotlivých
  `[Authorize]` atributech, žádnou výchozí politiku – nový controller/akce bez atributu by byl podle
  výchozího chování ASP.NET Core tiše veřejný (přesně tenhle typ chyby už jednou našel audit,
  `033af8a`). Nová `FallbackPolicy = USBGuardianClients` dělá z API fail-closed jako konzoli:
  chráněno je defaultně vše, veřejné jen to, co má explicitní `[AllowAnonymous]`/`.AllowAnonymous()`
  (`/api/version`, `/api/cert-info`, `IncidentsController.QueueStatus`). Žádný existující endpoint
  dnes díru neměl – jde o pojistku pro budoucnost. **Zatím jen v gitu, na fleet nenasazeno**
  (vyžaduje redeploy API).

**Zatím neřešeno z auditu** (priorita pro příště): ACL na TLS PFX a RSA signing klíč,
ověření hostname z payloadu proti autentizované Windows identitě, víc testů,
CI (`.github/workflows` v repu chybí úplně).

### 5.5 Roadmapa (pending)
- **Monitoring expirace podpisového certu** – `CN=powershell.domena.loc` platí do 2028-06-17; alert e-mailem z konzole.
- **„Vše server na APP_SERVER":** přesun API runtime z SQL_SERVER na APP_SERVER (konzole+API na APP_SERVER, DB na SQL_SERVER, agent repoint na
  `https://APP_SERVER_IP:5443`) → PC-01 fakt netřeba. **Build/deploy artefakty jsou na D:\deploy (lokálně), ne na PC-01.**
- ~~Zavřít HTTP 5050~~ **HOTOVO (04.09.2026)** – naslouchá jen v `Development` (`Program.cs`, `builder.Environment.IsDevelopment()`), produkce jen HTTPS `:5443`.
- **Retence deníku** – `sp_PurgeActivityLog` nikdo nevolá; doplnit `activity.retentionDays` do Nastavení a volání do API.
- **`Microsoft.AspNetCore.Authentication.Negotiate` 8.0.0** – build hlásí NU1903 (známá vysoká zranitelnost), zvednout na aktuální 8.0.x.
- **Per-serial blocklist** + **blokace už-připojeného média** (startovní sken je půlka cesty).
- **Hardening:** dedikovaná `USB-Guardian-Admins` místo `IT-Admins`, HTTPS konzole.
- **Úklid:** stray (untracked) `server/USBGuardianAPI/` (ke smazání).

> **Pozn. k automatizaci (NEZ-obejitelné mnou):** bezpečnostní klasifikátor mi auto-deny-uje zásahy na prod
> SQL_SERVER i **změnu vlastních oprávnění** (update-config) → prod-deploye a permission-rules musí spustit/povolit
> uživatel (bypass režim nebo ruční rule). Proto API deploy na SQL_SERVER dělá uživatel hotovými PS bloky (build mu
> připravím na `APP_SERVER`).

## 6. Mapa dokumentace

| Soubor | Obsah |
|--------|-------|
| `README.md` / `.en.md` | Funkční přehled, komponenty, konfigurace, nasazení |
| `HANDOFF.md` / `.en.md` | Tento dokument – předávka + živý stav |
| `docs/architecture.md` / `.en.md` | Technická architektura, datový tok, bezpečnostní vrstvy, deník provozu |
| `docs/auto-deploy-setup.md` / `.en.md` | Nastavení deploy gMSA (klientské i serverové) + GPO + úlohy |
| `docs/how-it-works.html` | Animace toku informací (15 kroků), CS/EN přepínačem |
| `docs/mind-map.html` | Myšlenková mapa systému, CS/EN |
| `docs/flowchart.html` | Vývojový diagram cesty jednoho média (rozhodovací body), CS/EN |
| `docs/management-summary.html` | **Shrnutí pro vedení — 1× A4 na výšku**, k tisku, CS/EN |
| `docs/oponentura.md` / `.en.md` | Komplexní technický dokument k oponentuře (kontext, NIS2, obhajoba rozhodnutí, bezpečnost, omezení) — **kap. 34 = doplněk k 4. 9. 2026** |
| `docs/oponentura-komercni.md` / `.en.md` | Komerční oponentní posudek (business/product readiness) + reakce autora |
| `wwwroot/bank/README.md` | Banka UI – jak se zapojuje styl a rozvržení (kopie z Interface-Par) |
