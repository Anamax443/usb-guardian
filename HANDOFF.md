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
| **Doména** | `axinetwork.loc` |
| **DB** | SQL Server `B-S-W-SQL-04` (= `10.8.2.225`), databáze `USBGuardian`, skripty `database/01–06` aplikované |
| **API** | `B-S-W-SQL-04`, Windows služba „USB Guardian API", install `C:\USBGuardian.Api`, gMSA `AXINETWORK\gmsa-SQL$`; **HTTPS `:5443`** (self-signed, **PIN `E6F6B4FCE0BB627F564E85D6509DE7C4B82CF2F0`**) + HTTP `:5050`. **Živá verze přes `GET /api/version`** |
| **Verze/commit (kontrola)** | konzole patička + `:4200/api/version`; API `:5050/api/version`; agent hlásí commit → konzole „Agent verze". Vše stampuje `git rev-parse` (MSBuild) |
| **Admin konzole** | **živá** `http://10.8.2.213:4200/` (`B-S-W-MIKOS`), služba `USBGuardianConsole`, `C:\Apps\USBGuardianConsole`, self-contained |
| **Účet konzole** | **LocalSystem** = `AXINETWORK\B-S-W-MIKOS$` (SQL grant: read vše + write Computers/WhitelistDevices/WhitelistVersions/AppSettings) |
| **Autorizace konzole** | AD `AXINETWORK\SQL Admins2` + whitelist `AXINETWORK\trnkam` (+ DB seznam z Nastavení) |
| **Šifrování agent↔API** | HTTPS + **pinning otisku** (bez CA) — ověřeno end-to-end (heartbeat OK z .181) |
| **AD sync** | zapnutý 60 min + on-demand; **211 v AD, ~210 bez agenta** |
| **Live commit (konzole)** | `5940eb6` (patička / `/api/version`) · **API live `19e4018`** |
| **Konzole – stránky** | Přehled (filtr+kumulace+řazení Detailně), Stanice (AD inventář + dlaždice „Zmlklo agentů" + „Vyžádat data"), Whitelist, Nastavení (vynucování/přístup/email/alerty/dohled komunikace/**auto-enrollment**), Dokumentace |
| **Deploy účet (auto-enroll)** | **gMSA `AXINETWORK\gmsa-USBGdep$`** – v `PC Admins` (admin na klientech) **i lokální admin na SQL-04** (deploy API); nainstalován na `.213`; deploy task `USBGuardian-AutoDeploy` (pod gMSA, přes CIM) |
| **Agent (test) .181** | **PILOT ÚSPĚŠNÝ** – auto-nainstalován přes gMSA (bez creds), služba „USB Guardian" RUNNING, heartbeat + **incidenty tečou do DB** (37). Zbývá: watchdog task + atribuce uživatele (viz 5.5) |

## 3. Klíčová rozhodnutí (proč)

- **Push, ne pull** – 500+ klientů za NATem/firewallem; agentovi stačí odchozí spojení.
- **Dvouvrstvě** – operativa (konzole, AD sync) na app serveru `.213`, DB jen úložiště na SQL-04.
  (Pozn.: API zatím běží na SQL-04; přesun na .213 je naplánovaný hardening.)
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

## 4. Deploy konzole (ručně, z TRNKAMW11)

trnkam má admin na `.213`; WinRM byl zavřený → deploy přes **SMB + remote `sc.exe`** (port 135/445):

```powershell
dotnet publish server\USBGuardian.Admin -c Release -r win-x64 --self-contained -o D:\deploy\USBGuardianConsole
sc.exe \\10.8.2.213 stop USBGuardianConsole
robocopy D:\deploy\USBGuardianConsole \\10.8.2.213\C$\Apps\USBGuardianConsole /E /XF appsettings.local.json
sc.exe \\10.8.2.213 start USBGuardianConsole
```

Firewall `:4200` byl vytvořen přes DCOM/CIM. Konfigurace na serveru:
`C:\Apps\USBGuardianConsole\appsettings.local.json` (viz `*.example`).

## 5. Stav a další kroky

### 5.1 Hotovo a živé
- **DB / incidenty = 100 %** — agent → API → DB → konzole celá cesta jede (Přehled ukazuje incidenty z .181).
  **Klíčový fix:** API mělo nedodělaný queue refactor — `IncidentsController` vyžadoval `IncidentQueue`, ale
  `Program.cs` ho **neregistroval v DI** → **500 na každý `/api/incidents`** (heartbeat bez té závislosti jel).
  Po `AddSingleton<IncidentQueue>` + `AddHostedService<IncidentQueueWorker>` controller vrací 202 + worker zapisuje.
- **Verze/commit na všech komponentách** — konzole + API mají `GET /api/version`, agent hlásí reálný commit
  (`AppInfo` + MSBuild `git rev-parse` stamp) → v konzoli „Agent verze" je vidět nasazený commit per stanice.
- **Konzole:** dlaždice „Zmlklo agentů" (práh `comm.silentAfterMinutes`), „Vyžádat data" (`ReportNow` přes
  `AppSettings cmd.report.<HOST>`), řaditelná tabulka „Detailně", auto-enrollment orchestrátor (default VYPNUTO+dry-run).
- **Fix trim sériáku** — WMI vrací serial s mezerami (`"WX92D622N4PE    "`) → nesedělo s whitelistem
  („Schváleno=ne" + agent nepoznal whitelisted). Agent trimuje při WMI parse, konzole v `Approved`.

### 5.2 Nasazené komponenty
- **API na SQL-04 (živé `19e4018`):** `ReportNow` v heartbeatu, DI fix fronty, `/api/version`. Deploy přes
  **gMSA** (build staged na `.213` `C:\Apps\USBGuardianApiPublish` → gMSA má lokální admin na SQL-04). Pozor při
  redeployi: **počkat na `STOPPED`** (jinak je `USBGuardian.Api.exe` zamčený → robocopy `FAILED` → stará verze běží dál).
- **Agent na .181 (auto-nainstalovaný):** whitelist poll 2 min, startovní sken, `ReportNow`, trim sériáku,
  reálná verze. Fixy: `onExpiredWhitelist`, publicKeyPath vůči exe, GUID, odebrán Sqlite.

### 5.3 Auto-enrollment agenta — PILOT ÚSPĚŠNÝ (.181), rozšířit na fleet
Cíl: konzole 24/7 po AD syncu sama nasadí agenta na stanice bez agenta. **Least-privilege:** konzole zapíše seznam
cílů (`deploy.targetsFile`), instalaci dělá **scheduled task na .213 pod gMSA** (jen ten účet má admin na klientech).
- **Funguje end-to-end:** gMSA `gmsa-USBGdep$` (v `PC Admins` = admin na klientech, bez hesla), task `USBGuardian-AutoDeploy`,
  `Deploy-AgentFleet.ps1` (runspace pool PS5.1, `sc.exe \\HOST create` přes cmd). **.181 se nainstaloval bez jakýchkoli creds**,
  služba běží, heartbeat + incidenty tečou. Skripty: `New-DeployGmsa.ps1`, `Install-Agent.ps1`/`Uninstall-Agent.ps1`,
  Detail: [docs/auto-deploy-setup.md](docs/auto-deploy-setup.md).
- **Zbývá na .181:** **watchdog task** (PS-free `sc start` schtasks – jednořádkový příkaz pro klienta, viz git historie).
- **Rozšíření na fleet:** GPO trust publisheru na klienty (5.4), v Nastavení zapnout (dry-run → ostrý), `.181 → .180 → fleet`.

### 5.4 Prostředí pro PS skripty (DŮLEŽITÉ – AXIMA gotchas)
- **AllSigned (GPO):** každý PS skript co tam běží **musí být podepsaný** prod certem `CN=powershell.axinetwork.loc`
  (`-ExecutionPolicy Bypass` to NEOBEJDE). Podpis přes službu `.213:4100` / share `\\herkules\ITC\UTIL\04-manualy-instalace\PS-scripty`.
  Týká se `Deploy-AgentFleet.ps1` (na .213) a `Watch-USBGuardian.ps1` (na klientech).
- **Před podpisem CRLF + UTF-8 BOM** (repo má LF → jinak `HashMismatch`).
- **Trusted Publisher:** pro neinteraktivní běh (gMSA/SYSTEM) musí být podpisový cert v `LocalMachine\TrustedPublisher`
  na .213 i klientech (přidáno na .181+.213; **fleet přes GPO** – cert export `_AXIMA-CodeSign-publisher.cer` na share).

### 5.5 Roadmapa (pending)
- **Atribuce uživatele** — incidenty hlásí `TRNKAMW11$` (strojový účet), protože agent běží jako SYSTEM
  (`Environment.UserName`). Doplnit detekci aktivní konzolové session (WTS API: `WTSGetActiveConsoleSessionId`
  + `WTSQuerySessionInformation`) → reálný přihlášený uživatel. Zapadá do „Toast Privilege Separation".
- **Podpisový/publikační workflow whitelistu** — změny v katalogu se k agentům dostanou až **po vydání podepsané
  verze** (privátní klíč nikdy na serveru). Bez toho agent dál varuje i schválené médium (Stav podpisu = nepodepsáno).
  Odemkne i vynucování + **blocklist** „naostro".
- **Monitoring expirace podpisového certu** – `CN=powershell.axinetwork.loc` platí do 2028-06-17; alert e-mailem z konzole.
- **„Vše server na .213":** přesun API runtime z SQL-04 na .213 (konzole+API na .213, DB na SQL-04, agent repoint na
  `https://10.8.2.213:5443`) → .181 fakt netřeba. **Build/deploy artefakty jsou na D:\deploy (lokálně), ne na .181.**
- **Zavřít HTTP 5050** na SQL-04 (jen HTTPS) – NIS2.
- **Per-serial blocklist** + **blokace už-připojeného média** (startovní sken je půlka cesty).
- **Hardening:** dedikovaná `USB-Guardian-Admins` místo `SQL Admins2`, HTTPS konzole.
- **Úklid:** stray (untracked) `server/USBGuardianAPI/` (ke smazání).

> **Pozn. k automatizaci (NEZ-obejitelné mnou):** bezpečnostní klasifikátor mi auto-deny-uje zásahy na prod
> SQL-04 i **změnu vlastních oprávnění** (update-config) → prod-deploye a permission-rules musí spustit/povolit
> uživatel (bypass režim nebo ruční rule). Proto API deploy na SQL-04 dělá uživatel hotovými PS bloky (build mu
> připravím na `.213`).

## 6. Mapa dokumentace

| Soubor | Obsah |
|--------|-------|
| `README.md` / `.en.md` | Funkční přehled, komponenty, konfigurace, nasazení |
| `HANDOFF.md` / `.en.md` | Tento dokument – předávka + živý stav |
| `docs/architecture.md` | Technická architektura, datový tok, bezpečnostní vrstvy |
| `docs/auto-deploy-setup.md` | Nastavení deploy gMSA + GPO + scheduled task pro auto-enrollment |
