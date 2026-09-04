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
| 19 | **Verze/commit na všech komponentách** (`/api/version`, agent hlásí commit; **spolehlivý stamp** = footer/`/api/version` = git HEAD) | ✅ |
| 20 | **Atribuce uživatele** – reálný přihlášený uživatel přes **WTS API** (agent=SYSTEM → ne strojový účet); ověřeno živě | ✅ |
| 21 | **Kompletní klient** – ToastHelper (notifikace, logon+unlock) + **PS-free watchdog**, vše ve `Build-AgentPackage`; ověřeno na .181 | ✅ |
| 22 | **Kapacita média** v Přehledu i Whitelistu; **export CSV** + **manažerský report** s grafy (inline SVG, 1–2 A4) | ✅ |
| 23 | **Retence dat** – Nastavení (konzole) + `RetentionService` v API (maže staré incidenty); **stránka Databáze** (přehled obsahu DB) | ✅ |
| 24 | **Deploy targeting** – default pro nové PC (Nastavení) + per-stanice a hromadné zařazení/vyřazení v Stanicích | ✅ |
| 25 | **Lokální konzole agenta** ukazuje i seznam schválených zařízení (whitelist) + verzi agenta | ✅ |
| 26 | **HTML animace** fungování systému (`/how-it-works.html`, 13 kroků: datový tok + vynucování) | ✅ |
| 27 | **Publikační/podpisový workflow whitelistu (automatický)** – změna katalogu → konzole sama vydá a **interně podepíše** (server-side RSA, klíč na .213) → API servíruje podepsaný blob verbatim → **klient = 1:1 kopie serveru** do ~2 min; agent O(1) match (scale 10k) | ✅ |
| 28 | **Vynucování server→agent (Fáze 2)** – heartbeat nese `policy.enforce` (.213 = pravda) → agent reálně **blokuje/varuje** dle serveru | ✅ |
| 29 | **Lokální break-glass (Fáze 3)** – admin stanice dočasně vypne blokování offline (lokální konzole), perzistované, **logované** → server; při spojení se serverem se zruší | ✅ |
| 30 | **Auto-re-enable + reconciliace** – při vypnutí blokování / break-glass agent vrátí dříve zablokovaná média; mezitím schválené médium vrátí i při zapnutém blokování | ✅ |
| 31 | **Restart klientské služby** (lokální konzole, agent self-restart) + **reload nastavení** (serverová konzole, AccessCache) | ✅ |
| 32 | **Spolehlivé vynucování (symetrie)** – vypnout blokování = vrátit **vše hned** (přesný `Enable-PnpDevice`, ošetření odpojeného média); zapnout zpět = znovu zablokovat **už připojená** neschválená média; nově schválené médium platí **ihned po stažení** (invalidace whitelist cache); ✕ mazání z katalogu (DELETE grant); rozbalená chybová hláška konzole | ✅ |
| 33 | **Kontroly stavu** – odškrtávaný seznam kontrol (server i klient) s průběžnými výsledky a **exportem CSV / HTML / PDF / TXT**; **plánovaný restart** služeb (server i agent) | ✅ |
| 34 | **Vzhled z banky UI** – přepínatelný v Nastavení, dark/light bez FOUC, přežije překliknutí mezi stránkami | ✅ |
| 35 | **Oddělené deploy účty** – `gmsa-USBGdep$` (jen stanice) × `gmsa-USBGsrv$` (jen server API) × konzole (admin nikde); jedna identita už nedrží fleet i server současně | ✅ |
| 36 | **Aktualizace nasazeného agenta** – `Update-Agent.cmd` (stop → čekat `STOPPED` → kopie → ověřit `RUNNING`), **offline instalátor** v balíčku, **kanály stable/beta**, **archiv verzí** + návrat k předchozí | ✅ |
| 37 | **Deník provozu (Aktivita)** – `ActivityLog`: heartbeaty a odpovědi serveru, příjem dávek, publikace whitelistu, ruční zásahy operátora; API i konzole píšou do téže tabulky, stránka s filtry, živým režimem a exportem CSV | ✅ |
| 38 | **Lokální konzole: přihlášení lokálního admina** – loopback token je síťový a u lokálního účtu z něj Windows odebere Administrators (`LocalAccountTokenFilterPolicy`); kontrola nově uznává i filtrovaný token a odmítnutí ukáže, **jako kdo** byl člověk viděn | ✅ |
| – | Zavřít nešifrované HTTP 5050 (jen HTTPS) | 🔜 NIS2 |
| – | Per-serial **blocklist** + blokace už-připojeného média | 🔜 |
| – | Monitoring expirace podpisového certu | 🔜 |
| – | **Retence deníku** – `sp_PurgeActivityLog` existuje, ale nikdo ji nevolá | 🔜 |

## Architektura

Tři komponenty, push model (agent → API), dvouvrstvý server (operativa na app serveru, DB = úložiště):

```
[Klientská stanice]                  [App server .213]            [DB server SQL-04]
┌────────────────────┐               ┌─────────────────────┐      ┌───────────────────┐
│ Agent (.NET8 svc)  │               │ Admin konzole       │      │ SQL Server        │
│  WMI detekce       │  push  HTTPS  │ (Blazor :4200)      │ read/│ DB USBGuardian    │
│  whitelist check   ├──────────────►│  Přehled / Stanice  │ write│  Incidents        │
│  warn / block      │   ┌───────────┤  Aktivita (deník)   ├─────►│  Computers        │
│  lokální konzole   │   │  push     │  AD sync ◄── AD     │      │  WhitelistDevices │
│  (loopback :5080)  │   │           │  Nastavení / Docs   │      │  WhitelistVersions│
└─────────▲──────────┘   │           └─────────────────────┘      │  AppSettings      │
          │              │           ┌─────────────────────┐      │  ActivityLog      │
   instalace/update      └──────────►│ API (:5050/:5443)   ├─read/─└───────────────────┘
   (úlohy pod gMSA)                  │  příjem incidentů   │ write            ▲
          │                          │  heartbeat + politika│                 │
          └──────────────────────────┤  whitelist distribuce│─────────────────┘
                                     │  zápis do deníku     │
                                     └─────────────────────┘
```

Deník provozu (`ActivityLog`) píše **API i konzole do téže tabulky** — komunikace agentů z jedné strany,
zásahy operátora z druhé, aby se provoz četl jako jeden příběh.

Detail viz [docs/architecture.md](docs/architecture.md). Předávka a živý stav: [HANDOFF.md](HANDOFF.md).
Vizuálně: [animace toku dat](docs/how-it-works.html) · [myšlenková mapa](docs/mind-map.html) ·
[vývojový diagram](docs/flowchart.html) · [shrnutí pro vedení (A4)](docs/management-summary.html).

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
  identifikátory **VID/PID/sériové číslo** + **velikost média** + sloupec **„Schváleno"** (aktuálně dle whitelistu).
  Tabulka **„Detailně" má řaditelné hlavičky**. **Export:** `⬇ CSV` (Excel) a `📊 Report` =
  **manažerský souhrn** (KPI + grafy: vývoj incidentů, donut akcí, top uživatelé/stanice + sekce Databáze;
  inline SVG, tisknutelné na **1–2 A4**) – oba dědí aktivní filtr.
- **Stanice** – inventář z AD; dlaždice filtrují (vše / hlásí / **zmlklo agentů** / chybí agent),
  **hledání**, **cesta v AD** (OU) vedle hostname, **ikona komunikace** (zelená ≤ práh / žlutá zmlkl
  / šedá žádný kontakt; práh `comm.silentAfterMinutes` v Nastavení), tlačítko **Aktualizovat z AD**
  a **„Vyžádat data"** (řádek/hromadně). Sloupec **„Nasazení"** + hromadné **„Vyřadit / Zařadit vše"** =
  řízení auto-enrollmentu per stanice (výjimka proti výchozímu `deploy.defaultEnroll`).
- **Whitelist** – schválená média; **stačí zadat sériové číslo** (VID/PID/název se dotáhnou
  z incidentů, i zpětně), **kapacita** (z incidentů), **hromadný import**, **editace polí** inline,
  **checkbox Aktivní** (dočasná deaktivace bez mazání).
- **Nastavení** (centrální, v DB) – **vynucování**, **dohled komunikace** (práh „zmlklého agenta"),
  **whitelist přístupu** do konzole, **e-mail** + **alerty nad incidenty**, **auto-enrollment agenta**
  (master + dry-run + **výchozí pro nové PC** + cíle), **retence dat** (kolik dní uchovat incidenty),
  AD sync / DB / build info.
- **Kontroly** – health checks serveru i klientů: **seznam kontrol dopředu** a odškrtávání s průběžnými
  výsledky (aby bylo vidět, že běží), **plánovaný restart** služeb, **export** CSV / HTML / PDF (tisk) / TXT.
- **Aktivita** – **deník provozu**: heartbeaty (včetně toho, co server odpověděl), příjem dávek incidentů,
  publikace whitelistu, ruční nasazení a vyřazení stanice. Filtry (období, úroveň, zdroj, hledání),
  režim **živě** (obnova po 3 s) a export CSV. Nabídka zdrojů se bere z dat, ne z pevného seznamu.
- **Databáze** – read-only přehled obsahu DB (počty v tabulkách, rozsah incidentů pro kontrolu retence,
  výpis `AppSettings`, posledních 20 incidentů).
- **Dokumentace** – rozcestník + **tisknutelné HTML** stránky (render `.md` přes Markdig) + grafické výstupy:
  **animace** „Jak to funguje", **myšlenková mapa**, **vývojový diagram** a **shrnutí pro vedení (A4)** —
  všechny čtyři dvojjazyčně (CS/EN přepínačem).

Patička (servisní řádek dle standardu): **živé hodiny + klikací commit hash + DB health + © Milan Trnka**.
Kontrakt **`GET /api/version`**.

### Verzování (commit na všech komponentách)

Všechny komponenty hlásí svůj git commit, takže operátor ověří, co přesně běží:

- **Konzole** – commit v **patičce** + endpoint `:4200/api/version`.
- **API** – endpoint `:5050/api/version` (NOVĚ).
- **Agent** – hlásí commit (heartbeat) → konzole ho zobrazí jako **„Agent verze"**.

Commit se razítkuje při buildu přes MSBuild (`git rev-parse`) – **spolehlivě** (generovaný `GitCommit.g.cs`
přepsaný jen při změně commitu vynutí recompile), takže footer/`/api/version` přesně odpovídá nasazenému gitu
(= kontrola aktuálnosti řešení).

**Autorizace:** Windows Auth, dovnitř jen členové `Authorization:AdminGroups` / účty
`Authorization:AllowedUsers` (appsettings) **nebo** DB seznam z Nastavení. Pro tiché SSO chodit přes hostname, ne IP.

### AD sync

Background služba (i na vyžádání tlačítkem) načte počítače z Active Directory a zapíše do tabulky
`Computers`. Klíčem je **hostname** (ne IP – stanice mají dynamické adresy). Doménu bere
automaticky podle serveru (`new DirectoryEntry()`, nic natvrdo). Reconciliation:
*v AD ⨯ hlásí agenta* → seznam stanic, kam agent chybí.

## Lokální admin konzole agenta

Volitelná (`localConsole.enabled` v `agent.config.local.json`, v šabloně vypnutá). `HttpListener`
na `127.0.0.1:5080`, **jen lokální admin** – živý stav agenta: **seznam schválených zařízení (whitelist)**,
stav+verze whitelistu, **verze agenta (commit)**, WMI watchdog, fronta, připojená média a poslední události.
Kromě čtení umí tři akce: **break-glass** (dočasně vypnout blokování offline), **vrátit všechna média hned**
a **restart služby**. Použit `HttpListener` (ne Kestrel), aby agent nepotřeboval ASP.NET Core runtime.
Heslo netřeba (loopback + Windows auth + členství v Administrators).

> **Přihlášení lokálního admina (gotcha):** požadavek na `127.0.0.1` je z pohledu Windows **síťový** a
> u lokálního účtu z něj `LocalAccountTokenFilterPolicy` odebere skupinu Administrators (zůstane
> deny-only) → `IsInRole` řekne NE, i když člověk admin je. Kontrola proto uznává i **filtrovaný token**:
> členství tu slouží jako **autorizace**, ne jako zdroj práv — akci provádí služba pod SYSTEM.
> Odmítnutí není holá 403, ale stránka, která ukáže, **jako kdo** byl člověk viděn a co je potřeba.

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

- **Balíček klienta:** `scripts\Build-AgentPackage.ps1` složí kompletního klienta = self-contained agent +
  `ToastHelper\` (notifikace v user session) + `tasks\` (definice scheduled tasků). Klient nepotřebuje .NET runtime.
- **Lokální instalace:** `scripts\Install-Agent.ps1 -SourcePath <balíček>` (vytvoří službu „USB Guardian"
  + recovery, nasadí per-machine `agent.config.local.json`), `scripts\Uninstall-Agent.ps1`.
- **Hromadně:** `scripts\Deploy-AgentFleet.ps1 -TargetsFile … -SourcePath …` – paralelní rollout přes
  `\\HOST\C$` + `sc.exe \\HOST create`; registruje **PS-free** scheduled tasky (watchdog à 3 min `sc start`
  + **ToastHelper** logon/unlock přes `schtasks /XML`); přeskočí offline/už-nainstalované; audit CSV. (PS 5.1 i 7.)
- **Auto-enrollment (konzole nasazuje sama):** `AgentDeployService` po AD syncu najde stanice bez agenta,
  uplatní **výchozí `deploy.defaultEnroll` + výjimky** (`includeHosts`/`excludeHosts` spravované v Stanicích) a
  (v ostrém režimu) zapíše cíle do `deploy.targetsFile`; instalaci provede **scheduled task na .213 pod dedikovaným
  gMSA** (least-privilege). **Default VYPNUTO + dry-run.** Nastavení: [docs/auto-deploy-setup.md](docs/auto-deploy-setup.md).
- **Aktualizace už nasazeného agenta:** `scripts\Update-Agent.cmd <ZDROJ> <HOST|SOUBOR> [SLUŽBA]` – zastaví službu,
  **počká na `STOPPED`**, zkopíruje a **ověří `RUNNING`**. Bez toho je běžící `.exe` zamčený, přepíše se jen část
  souborů a na stanici zůstane **směs verzí**, zatímco deploy hlásí úspěch. Stanici bez služby přeskočí.
- **Nasazení API:** `scripts\Deploy-Api.cmd <ZDROJ> <HOST> <CÍL> [SLUŽBA]` – stejný vzor (stop → čekat → kopie →
  ověřit), běží jako úloha pod **serverovým** gMSA. Klientský deploy účet na server nesahá.
- **Kanály a návrat zpět:** balíček se archivuje po verzích (`stable` / `beta`), takže jde nasadit předchozí verzi.
  V balíčku je i **offline instalátor** (`Install-Agent.cmd` / `Uninstall-Agent.cmd`) pro stanici, kam deploy kanál
  nedosáhne — včetně úklidu po sobě.

> **Dávky (.cmd), ne PowerShell:** nasazovací kroky jsou `.cmd`, protože nepodléhají `AllSigned` z GPO —
> změna deploy skriptu tak nevyžaduje nový podpis.

**Oddělené deploy identity (od 09/2026):** jeden účet nesmí držet fleet i server současně.

| Role | Účet | Kde je admin |
|---|---|---|
| Klienti (auto-enrollment, update) | `gmsa-USBGdep$` | jen stanice |
| Server (nasazení API) | `gmsa-USBGsrv$` | jen server API |
| Konzole (běžící aplikace) | strojový účet app serveru | **nikde** |

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
| `06_appsettings.sql` | AppSettings (centrální nastavení: vynucování, přístup, e-mail, retence, deploy) + grant; `Value` = `NVARCHAR(MAX)` (dlouhé seznamy) |
| `07_whitelist_publish.sql` | WhitelistVersions: `Json` (podepsaný blob) + `Signature` → `NVARCHAR(MAX)` (publikační workflow) |
| `08_deploy_ignored.sql` | trvalé vyřazení stanice z nasazení (hromadné akce ho nepřepíšou) |
| `09_activity_log.sql` | `ActivityLog` (deník provozu) + indexy + `sp_PurgeActivityLog` (úklid po dávkách 5000) |

Granty se do skriptů **nepíšou** (portabilita – žádné firemní účty v repu). Pro deník je potřeba
`SELECT, INSERT ON dbo.ActivityLog` pro účet konzole i API a `EXECUTE ON dbo.sp_PurgeActivityLog` pro API.

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
GRANT INSERT, UPDATE, DELETE ON dbo.WhitelistDevices TO [DOMENA\B-S-W-MIKOS$];  -- DELETE = mazání z katalogu (✕)
GRANT INSERT, UPDATE ON dbo.WhitelistVersions TO [DOMENA\B-S-W-MIKOS$];          -- bez DELETE (verze = append-only audit)
```

## Bezpečnost

- Whitelist podepsaný RSA – agent odmítne podvrhnutý katalog (fail-secure: co neověří, nepoužije).
  **Vědomý kompromis:** podpisový klíč **je na app serveru**, protože publikace musí být automatická —
  ruční offline podpis po každé změně katalogu byl provozně neúnosný. Klíč je interní klíč nástroje,
  ne firemní CA, a agenti mají jen veřejnou část.
- TLS s **pinningem otisku** – šifrováno i ověřeno bez certifikační autority (vypnutelné jen pro vývoj).
- Windows Auth (Kerberos) – agenti strojovým účtem; konzole admin skupina / whitelist.
- gMSA pro SQL – žádné heslo v konfiguraci.
- Least-privilege SQL grant pro konzoli (read vše, write jen tam, kam opravdu píše).
- **Oddělené deploy identity** – kompromitace jedné nesáhne na obě vrstvy (fleet × server).
- `*.local.json` gitignored.
- Lokální konzole agenta: loopback, jen lokální admin, zápis omezený na break-glass a restart služby.

## Repo struktura

```
usb-guardian/
├── agent/USBGuardian/        # .NET 8 Windows Service agent
│   ├── LocalConsole/         # lokální admin konzole (HttpListener)
│   ├── Config/ Models/ Security/
│   ├── Security/ SessionUser.cs  # reálný uživatel přes WTS API
├── server/
│   ├── USBGuardian.Api/      # ASP.NET Core API (příjem incidentů, whitelist)
│   │   └── Retention/        # RetentionService (úklid starých incidentů)
│   └── USBGuardian.Admin/    # Blazor Server admin konzole (.213)
│       ├── Components/        # Pages (Home, Computers, Whitelist, Settings, Database, Docs), Layout
│       ├── AdSync/            # AdSyncRunner + AdSyncService
│       ├── Deploy/            # AgentDeployService (auto-enrollment orchestrátor)
│       ├── Export/            # ExportEndpoints (CSV + manažerský report)
│       ├── Notifications/     # IncidentAlertService + EmailSender
│       └── appsettings.local.json.example
├── tools/WhitelistSigner/    # offline RSA podpis whitelistu (generate/sign/verify)
├── database/                 # 01–09 SQL skripty
├── scripts/                  # certifikáty, Build-AgentPackage, watchdog, ToastHelper,
│                             #   Install/Uninstall-Agent, Deploy-AgentFleet, Update-Agent.cmd,
│                             #   Deploy-Api.cmd, Set/Archive-AgentVersion, New-DeployGmsa, tasks/
├── docs/                     # architecture(.en).md, auto-deploy-setup(.en).md, oponentura(.en).md,
│                             #   how-it-works.html (animace), mind-map.html (myšlenková mapa),
│                             #   flowchart.html (vývojový diagram), management-summary.html (A4)
├── README.md / README.en.md
└── HANDOFF.md / HANDOFF.en.md
```
