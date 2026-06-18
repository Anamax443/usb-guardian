# HANDOFF – předání projektu USB Guardian

*🇨🇿 Čeština · [🇬🇧 English](HANDOFF.en.md)*

**Datum:** 2026-06-18 · **Repo:** `Anamax443/usb-guardian` · **Autor:** Milan Trnka (AXIMA)

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
| **API** | `B-S-W-SQL-04`, Windows služba, gMSA `AXINETWORK\gmsa-SQL$`; **HTTPS `:5443`** (self-signed cert, **PIN `E6F6B4FCE0BB627F564E85D6509DE7C4B82CF2F0`**) + HTTP `:5050` (NIS2: zavřít) |
| **Admin konzole** | **živá** `http://10.8.2.213:4200/` (`B-S-W-MIKOS`), služba `USBGuardianConsole`, `C:\Apps\USBGuardianConsole`, self-contained |
| **Účet konzole** | **LocalSystem** = `AXINETWORK\B-S-W-MIKOS$` (SQL grant: read vše + write Computers/WhitelistDevices/WhitelistVersions/AppSettings) |
| **Autorizace konzole** | AD `AXINETWORK\SQL Admins2` + whitelist `AXINETWORK\trnkam` (+ DB seznam z Nastavení) |
| **Šifrování agent↔API** | HTTPS + **pinning otisku** (bez CA) — ověřeno end-to-end (heartbeat OK z .181) |
| **AD sync** | zapnutý 60 min + on-demand; **211 v AD, ~210 bez agenta** |
| **Live commit (konzole)** | viz patička konzole / `/api/version` (po posledním doc sweepu) |
| **Konzole – stránky** | Přehled (filtr+kumulace+řazení Detailně), Stanice (AD inventář + dlaždice „Zmlklo agentů" + „Vyžádat data"), Whitelist, Nastavení (vynucování/přístup/email/alerty/dohled komunikace/**auto-enrollment**), Dokumentace |
| **Deploy účet (auto-enroll)** | **gMSA `AXINETWORK\gmsa-USBGdep$`** – v `PC Admins` (admin na klientech), nainstalován na `.213`; scheduled task `\USBGuardian\USBGuardian-Watchdog`… deploy task `USBGuardian-AutoDeploy` na `.213` |
| **Agent (test)** | `.181` (TRNKAMW11) – `syncUrl=https://B-S-W-SQL-04:5443` + pin; **pilot auto-deploye běží** (soubory kopíruje, dolaďuje se sc.exe create) |

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

### 5.1 Hotovo a živé na konzoli (.213)
- **Dlaždice „Zmlklo agentů"** na Stanicích + práh `comm.silentAfterMinutes` (Nastavení → Dohled komunikace).
  Odhalí stanice, co dřív hlásily agenta, ale `LastSeen` je starší než práh (výpadek/tamper).
- **„Vyžádat data" na klik** (Stanice, řádek/hromadně) – příkaz `ReportNow` přes `AppSettings` `cmd.report.<HOST>`.
- **Přehled → tabulka „Detailně" s řaditelnými hlavičkami** (řazení v DB přes query-string).
- **Auto-enrollment orchestrátor** `AgentDeployService` + Nastavení „Auto-enrollment agenta" (default VYPNUTO + dry-run).

### 5.2 V repu, čeká na rollout / operátora
- **API (SQL-04, operátor):** `HeartbeatController` vrací `ReportNow` (jednorázově dle předchozího `LastSeen`).
  + fix `DateTimeStyles` (jinak heartbeat 500). Bez deploye API „Vyžádat data" jen zapíše příznak, agent ho nedostane.
- **Agent (rollout):** **whitelist poll 15 → 2 min**; **startovní sken už-připojených médií** (WMI watchers chytaly
  jen nová připojení); `ReportNow` handling (flush); fixy: `onExpiredWhitelist` (block/allow/warn), publicKeyPath
  vůči exe (jinak whitelist jako služba odmítnut), GUID `:N[..8]`, odebrán nepoužitý `Microsoft.Data.Sqlite`.

### 5.3 Auto-enrollment agenta (konzole .213 nasazuje sama) — ROZPRACOVÁNO, pilot
Cíl: konzole 24/7 po AD syncu sama nasadí agenta na stanice bez agenta. **Least-privilege:** konzole jen zapíše
seznam cílů (`deploy.targetsFile`), instalaci dělá **scheduled task na .213 pod gMSA** (jen ten účet má admin na PC).
- **Hotovo:** gMSA `gmsa-USBGdep$` (v `PC Admins`, na .213), task `USBGuardian-AutoDeploy`, .213 naprovisionována
  (publish agenta `C:\Apps\USBGuardianAgentPublish` + skripty), `Deploy-AgentFleet.ps1` (runspace pool = PS5.1 kompat),
  `scripts\New-DeployGmsa.ps1`, `Install-Agent.ps1`/`Uninstall-Agent.ps1`. Detail: [docs/auto-deploy-setup.md](docs/auto-deploy-setup.md).
- **Pilot .181:** robocopy souborů **funguje** (gMSA admin přes `PC Admins`); vytvoření služby přes CIM/DCOM selhalo
  → přepnuto na **`sc.exe \\HOST create`** (přes cmd kvůli quotingu). **Skript nutno znovu podepsat** (viz 5.4) a doběhnout.
- **Zbývá k „ostrému" auto-enrollmentu:** v Nastavení zapnout (dry-run → ověřit → vypnout); rozšířit z .181 na .180 → fleet.

### 5.4 Prostředí pro PS skripty (DŮLEŽITÉ – AXIMA gotchas)
- **AllSigned (GPO):** každý PS skript co tam běží **musí být podepsaný** prod certem `CN=powershell.axinetwork.loc`
  (`-ExecutionPolicy Bypass` to NEOBEJDE). Podpis přes službu `.213:4100` / share `\\herkules\ITC\UTIL\04-manualy-instalace\PS-scripty`.
  Týká se `Deploy-AgentFleet.ps1` (na .213) a `Watch-USBGuardian.ps1` (na klientech).
- **Před podpisem CRLF + UTF-8 BOM** (repo má LF → jinak `HashMismatch`).
- **Trusted Publisher:** pro neinteraktivní běh (gMSA/SYSTEM) musí být podpisový cert v `LocalMachine\TrustedPublisher`
  na .213 i klientech (přidáno na .181+.213; **fleet přes GPO** – cert export `_AXIMA-CodeSign-publisher.cer` na share).

### 5.5 Roadmapa (pending)
- **Monitoring expirace podpisového certu** (uživatel chce) – cert platí do 2028-06-17; alert přes e-mail z konzole.
- **Zavřít HTTP 5050** na SQL-04 (jen HTTPS) – NIS2.
- **Podpisový/publikační workflow whitelistu** → odemkne vynucování i **blocklist** „naostro".
- **Per-serial blocklist** + **blokace už-připojeného média** (startovní sken je půlka cesty).
- **Hardening:** dedikovaná `USB-Guardian-Admins` místo `SQL Admins2`, HTTPS konzole, přesun API z SQL-04 na .213.
- **Úklid:** zbývá stray (untracked) složka `server/USBGuardianAPI/` (duplikát – ke smazání).
  Hotovo: nepoužitý `Microsoft.Data.Sqlite`, GUID `:N[..8]`.

## 6. Mapa dokumentace

| Soubor | Obsah |
|--------|-------|
| `README.md` / `.en.md` | Funkční přehled, komponenty, konfigurace, nasazení |
| `HANDOFF.md` / `.en.md` | Tento dokument – předávka + živý stav |
| `docs/architecture.md` | Technická architektura, datový tok, bezpečnostní vrstvy |
| `docs/auto-deploy-setup.md` | Nastavení deploy gMSA + GPO + scheduled task pro auto-enrollment |
