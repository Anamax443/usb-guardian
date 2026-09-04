# USB Guardian — technický dokument k oponentuře

**Monitoring a vynucování politiky výměnných paměťových médií na firemních stanicích jako technické opatření podle NIS2**

---

| | |
|---|---|
| **Projekt** | USB Guardian |
| **Repozitář** | `Anamax443/usb-guardian` |
| **Autor** | Milan Trnka (AXIMA) |
| **Verze dokumentu** | 1.1 — kapitoly 1–33 ve znění 1.0, kapitola 34 = doplněk k 4. 9. 2026 |
| **Datum** | 2026-06-19, doplněno 2026-09-04 |
| **Klasifikace** | Interní — podklad pro oponenturu |
| **Doménové prostředí** | `domena.loc` (AXIMA) |
| **Jazyk** | 🇨🇿 Čeština · [🇬🇧 English](oponentura.en.md) |
| **Související dokumenty** | [README.md](../README.md), [HANDOFF.md](../HANDOFF.md), [architecture.md](architecture.md), [auto-deploy-setup.md](auto-deploy-setup.md), [oponentura-komercni.md](oponentura-komercni.md) · grafické výstupy: [how-it-works.html](how-it-works.html), [mind-map.html](mind-map.html), [flowchart.html](flowchart.html), [management-summary.html](management-summary.html) |

---

## Abstrakt

USB Guardian je systém pro **kontrolu výměnných paměťových médií** (USB flash disky, SD karty, externí
USB disky) na koncových stanicích firemní sítě. Každé médium musí být schváleno IT a zapsáno do
centrálního, kryptograficky podepsaného whitelistu; neschválená média jsou v závislosti na politice
**varována nebo reálně zablokována** na úrovni ovladače. Veškeré události (připojení, blokace, výjimky)
jsou logovány a centrálně agregovány jako auditní stopa.

Systém je navržen jako **technické opatření** podporující soulad se směrnicí **NIS2** (2022/2555),
zákonem **č. 181/2014 Sb.** o kybernetické bezpečnosti a normou **ISO/IEC 27001/27002** (zejména
řízení výměnných médií a přenosných zařízení). Klade důraz na **least-privilege**, **fail-secure**
chování, **auditovatelnost** a **portabilitu** (žádné firemně specifické hodnoty v kódu).

Architektura je **třívrstvá** (agent na stanici → ingestní API → centrální databáze) s oddělenou
**administrátorskou konzolí** a využívá **push model** (agent iniciuje odchozí spojení), což je
vhodné pro flotilu 500+ stanic za NAT/firewallem. Komunikace je šifrovaná self-signed certifikátem
s **pinningem otisku** (bez závislosti na PKI), integrita whitelistu je zajištěna podpisem **RSA-4096**.

Tento dokument popisuje problém, normativní rámec, požadavky, architekturu a — pro účely oponentury
zásadní — **obhajobu jednotlivých návrhových rozhodnutí**, **bezpečnostní a hrozbový model**,
implementaci klíčových komponent, způsob nasazení a provozu, výsledky živého ověření na pilotní
stanici a **poctivý rozbor omezení, rizik a otevřených bodů**.

---

## Obsah

**ČÁST I — Kontext a požadavky**
1. Úvod
2. Problém: rizika výměnných médií
3. Legislativní a normativní rámec
4. Analýza požadavků

**ČÁST II — Návrh**
5. Přehled architektury
6. Obhajoba návrhových rozhodnutí (decision log)
7. Datový model a datové toky

**ČÁST III — Implementace**
8. Agent (klientská stanice)
9. Serverové API
10. Administrátorská konzole
11. Kryptografie a podepisování whitelistu

**ČÁST IV — Bezpečnost a vynucování**
12. Bezpečnostní a hrozbový model
13. Model vynucování politiky
14. Auditovatelnost a soulad s NIS2

**ČÁST V — Provoz**
15. Sestavení, nasazení a aktualizace
16. Verzování a ověřitelnost nasazení
17. Provoz, monitoring, retence

**ČÁST VI — Ověření a hodnocení**
18. Testování a živé ověření
19. Omezení, rizika a známé slabiny
20. Roadmapa
21. Závěr

**ČÁST VII — Rozšiřující analýzy a obhajoba**
22. Anticipované otázky oponenta a odpovědi
23. Srovnání s alternativními přístupy a produkty
24. Detailní testovací katalog
25. Výkon a škálování (kvantitativní analýza)
26. Provozní runbooky
27. Detailní diagramy
28. Detailní legislativní a normativní analýza
29. Referenční přehled tříd a odpovědností
30. Detailní rozbor klíčových algoritmů a kódu
31. Útočné scénáře (attack trees)
32. Kompletní příklady konfigurace
33. Chování v hraničních situacích (edge cases)

**ČÁST VIII — Doplněk**
34. Co se změnilo od verze 1.0 (stav k 4. 9. 2026)

**Přílohy**
- A. Glosář pojmů
- B. Přehled konfiguračních klíčů
- C. Databázové schéma a SQL granty
- D. Přehled API endpointů
- E. Mapování NIS2 / ISO 27001 → funkce
- F. Seznam návrhových rozhodnutí (souhrn)

---

# ČÁST I — Kontext a požadavky

## 1. Úvod

### 1.1 Účel dokumentu

Dokument slouží jako **podklad pro oponenturu** projektu USB Guardian. Jeho cílem není pouze popsat,
*co* systém dělá, ale především **obhájit, proč byl navržen tak, jak byl** — tj. doložit, že jednotlivá
rozhodnutí jsou racionální, že byly zváženy alternativy, a že známá omezení jsou vědomá a řízená.
Dokument je proto psán s předpokladem **kritického čtenáře (oponenta)**, který bude hledat slabiny,
nepodložená tvrzení a opomenuté alternativy.

### 1.2 Cílová skupina

- **Oponent / posuzovatel** — technicky zdatný čtenář hodnotící úplnost, korektnost a obhajitelnost řešení.
- **Bezpečnostní manažer / manažer kybernetické bezpečnosti** — posuzuje soulad s NIS2 / ISO 27001.
- **Provozní IT (správci)** — přebírá systém do provozu (viz též [HANDOFF.md](../HANDOFF.md)).
- **Vývojář přebírající projekt** — potřebuje pochopit rozhodnutí a jejich důsledky.

### 1.3 Rozsah a hranice

Dokument pokrývá **celý systém**: agenta na stanici, ingestní API, databázi, administrátorskou konzoli,
kryptografický model, nasazení a provoz. **Nepokrývá** detailní zdrojový kód řádek po řádku (k tomu
slouží repozitář), ani konkrétní firemně citlivé hodnoty (ty jsou mimo repozitář v `*.local.json`).

### 1.4 Konvence

- Názvy komponent, tříd a souborů jsou psány `kódovým písmem`.
- IP adresy a hostnames odpovídají reálnému pilotnímu nasazení v doméně `domena.loc`.
- Označení „**APP_SERVER**" = aplikační server `APP_SERVER` (`APP_SERVER_IP`), „**SQL_SERVER**" = databázový
  server `SQL_SERVER` (`SQL_SERVER_IP`), „**PC-01**" = pilotní stanice `PC-01`.

---

## 2. Problém: rizika výměnných paměťových médií

### 2.1 Proč právě výměnná média

Výměnná paměťová média (USB flash disky, SD karty, externí disky) představují jeden z nejstarších a
zároveň stále aktuálních vektorů kybernetických incidentů. Jejich riziko spočívá v kombinaci tří
vlastností:

1. **Obousměrný nekontrolovaný přenos dat** — médium může z chráněné sítě **vynést** citlivá data
   (exfiltrace, únik) a zároveň do ní **vnést** škodlivý kód (malware, ransomware) zcela mimo
   perimetrovou ochranu (firewall, e-mailové brány, web proxy).
2. **Fyzická povaha** — útok nevyžaduje síťové spojení; postačí fyzický přístup ke stanici. Tím obchází
   většinu síťových kontrol. Klasickým příkladem je nasazení malwaru přes „ztracený" USB disk na
   parkovišti (baiting) nebo přes podvržené nabíjecí kabely.
3. **Důvěryhodnost koncového bodu** — operační systém médiu standardně důvěřuje (automaticky připojí
   souborový systém, v některých konfiguracích spustí autorun), takže uživatel ani nemusí provést
   vědomou akci.

### 2.2 Konkrétní scénáře hrozeb

| Scénář | Popis | Dopad |
|--------|-------|-------|
| **Exfiltrace dat** | Zaměstnanec (úmyslně nebo z nedbalosti) zkopíruje citlivá data na soukromý USB disk | Únik dat, porušení GDPR/obchodního tajemství |
| **Vnesení malwaru** | Připojení infikovaného média (BadUSB, autorun, infikované dokumenty) | Kompromitace stanice, laterální pohyb |
| **HID spoofing (BadUSB)** | Médium se tváří jako klávesnice a vkládá příkazy | Spuštění kódu (mimo rozsah tohoto systému — viz §19) |
| **Ztráta / krádež média** | Nešifrovaný firemní disk s daty se ztratí | Únik dat |
| **Stínové IT** | Neevidovaná média mimo dohled IT | Ztráta přehledu, nemožnost auditu |

USB Guardian primárně cílí na **scénáře 1, 2, 4 a 5** (storage-class zařízení). HID spoofing (BadUSB
jako klávesnice) je mimo rozsah a je explicitně uveden v omezeních (§19).

### 2.3 Proč nestačí stávající kontroly

- **GPO / Removable Storage Access** (Windows) blokuje globálně nebo dle tříd, ale **nemá centrální
  whitelist konkrétních médií**, neposkytuje **auditní stopu s atribucí uživatele** a její správa
  napříč flotilou je nepružná.
- **DLP řešení** jsou nákladná, často cloudová a vyžadují klasifikaci dat.
- **Perimetrová ochrana** (firewall, proxy) je vůči fyzickému médiu **slepá**.
- **EDR/antivir** detekuje až *následek* (malware), ne *připojení neschváleného média*.

USB Guardian vyplňuje mezeru: **evidence + selektivní whitelist + vynucování + audit**, centrálně
spravované, s atribucí uživatele a kryptograficky zajištěnou integritou pravidel.

---

## 3. Legislativní a normativní rámec

### 3.1 Směrnice NIS2 (EU 2022/2555)

NIS2 rozšiřuje okruh povinných subjektů a zpřísňuje požadavky na **řízení kybernetických rizik**.
Relevantní jsou zejména články o **opatřeních k řízení rizik** (čl. 21), která zahrnují mj.:

- politiky a postupy pro **řízení aktiv** a **kontrolu přístupu**,
- **bezpečnost provozu** včetně ochrany před škodlivým kódem,
- **logování a monitorování** událostí,
- opatření pro **bezpečné zacházení s médii**.

USB Guardian je **technickým opatřením**, které přímo podporuje:

| Požadavek NIS2 (oblast) | Příspěvek USB Guardian |
|--------------------------|------------------------|
| Řízení aktiv | Centrální evidence všech připojených médií (i neschválených) s identifikací VID/PID/sériové číslo |
| Kontrola přístupu k médiím | Whitelist + vynucování (block/warn) na úrovni stanice |
| Ochrana před škodlivým kódem | Blokace neschválených médií jako prevence vnesení malwaru |
| Logování a monitorování | Auditní stopa všech událostí, centrálně agregovaná, s atribucí uživatele |
| Reakce na incidenty | Near-real-time hlášení incidentů na server, alerty e-mailem |
| Řízení změn / kontrola integrity | Kryptografický podpis whitelistu (RSA-4096), verzování |

### 3.2 Zákon č. 181/2014 Sb. o kybernetické bezpečnosti

Zákon o KB a navazující vyhláška o bezpečnostních opatřeních (VKB) operacionalizují požadavky pro
povinné osoby. USB Guardian přispívá k opatřením v oblastech **řízení aktiv**, **řízení přístupu**,
**ochrana před škodlivým kódem**, **zaznamenávání událostí** a **fyzická bezpečnost** (kontrola
přenosných zařízení). Konkrétní mapování viz Příloha E.

### 3.3 ISO/IEC 27001 a 27002

Z pohledu kontrol ISO/IEC 27002:2022 USB Guardian podporuje zejména:

- **8.7 Ochrana před malwarem** — prevence vnesení přes neschválené médium.
- **7.10 Paměťová média** — řízení životního cyklu a používání výměnných médií.
- **8.15 Protokolování** — záznam událostí připojení/blokace.
- **8.16 Monitorovací činnosti** — centrální dohled, detekce „zmlklých" agentů.
- **5.9 Inventura aktiv** — evidence médií objevených v síti.

### 3.4 Princip „technického opatření", nikoli samostatného souladu

Je třeba zdůraznit (a oponent na to bude citlivý): **žádný jednotlivý nástroj nezajišťuje soulad s
NIS2 nebo ISO 27001 sám o sobě.** USB Guardian je **dílčím technickým opatřením**, které musí být
zasazeno do širšího systému řízení bezpečnosti informací (ISMS), doprovázeno organizačními opatřeními
(směrnice o používání médií, školení, klasifikace dat) a procesy (schvalování médií, reakce na
incidenty). Dokument tento nárok nikde nepřekračuje.

---

## 4. Analýza požadavků

### 4.1 Funkční požadavky (FR)

| ID | Požadavek | Realizace |
|----|-----------|-----------|
| FR-1 | Detekovat připojení libovolného výměnného paměťového média (USB, SD) | `DeviceMonitor` (WMI watchery + startovní sken) |
| FR-2 | Identifikovat médium jednoznačně (VID, PID, sériové číslo) | Parsování `PNPDeviceID` z WMI |
| FR-3 | Porovnat médium s centrálně spravovaným whitelistem | `WhitelistChecker` (index O(1)) |
| FR-4 | V režimu „warn" médium ponechat funkční, jen varovat | `PolicyEnforcer` + Toast |
| FR-5 | V režimu „block" médium reálně znepřístupnit | `DeviceBlocker` (`Disable-PnpDevice`) |
| FR-6 | Zaznamenat každou událost jako incident s atribucí uživatele | `IncidentLogger` + `SessionUser` (WTS) |
| FR-7 | Doručit incidenty centrálně na server | `IncidentSync` → API → DB |
| FR-8 | Centrálně spravovat whitelist (přidat/odebrat/aktivovat) | Konzole, stránka Whitelist |
| FR-9 | Zajistit integritu whitelistu proti podvržení | RSA-4096 podpis, ověření na agentovi (fail-secure) |
| FR-10 | Centrálně přepínat režim vynucování (block/warn) | `policy.enforce` v heartbeatu |
| FR-11 | Umožnit dočasnou lokální výjimku (break-glass) pro práci offline | Lokální konzole, `PolicyState` override |
| FR-12 | Vrátit dříve zablokovaná média při vypnutí blokování / schválení | Auto-re-enable + reconciliace |
| FR-13 | Evidovat stanice z AD a identifikovat, kde chybí agent | `AdSyncRunner` + reconciliation |
| FR-14 | Nasadit agenta na stanice bez něj (hromadně) | Auto-enrollment (gMSA task) |
| FR-15 | Poskytnout přehledy, export a manažerský report | Konzole (Přehled, Export) |
| FR-16 | Mazat stará data dle retenční politiky | `RetentionService` (API) |
| FR-17 | Upozornit na nové neschválené incidenty e-mailem | `IncidentAlertService` |

### 4.2 Nefunkční požadavky (NFR)

| ID | Požadavek | Cíl / realizace |
|----|-----------|-----------------|
| NFR-1 **Škálovatelnost** | Flotila 500+ stanic | Push model (agent iniciuje spojení), O(1) match whitelistu (Dictionary), oddělené ingestní API od konzole, in-memory fronta incidentů |
| NFR-2 **Bezpečnost** | Šifrování, integrita, autentizace, autorizace, least-privilege | TLS+pinning, RSA-4096, Kerberos, AD skupiny, gMSA, granulární SQL granty |
| NFR-3 **Fail-secure** | Při selhání ověření nepustit | Neplatný/chybějící podpis whitelistu → médium se neověří → dle politiky |
| NFR-4 **Dostupnost / odolnost** | Příjem incidentů nesmí padnout pod náporem; offline provoz | 202 Accepted + fronta + worker; agent funguje offline (lokální whitelist) |
| NFR-5 **Auditovatelnost** | Kompletní stopa pro NIS2 | Každá událost = incident; break-glass logován; centrální agregace |
| NFR-6 **Portabilita** | Žádné firemní hodnoty v kódu | Vše v `*.local.json` (gitignored), doména z `new DirectoryEntry()` |
| NFR-7 **Ověřitelnost nasazení** | Operátor ověří, co běží | Commit stamp ve footeru / `/api/version` / heartbeatu |
| NFR-8 **Provozovatelnost** | Snadné nasazení i ve ztíženém prostředí | Self-contained buildy, SMB+sc.exe deploy, PS-free scheduled tasky |
| NFR-9 **Tamper-resistance** | Útočník nesmí triviálně agenta vyřadit | Služba + watchdog (dva nezávislé mechanismy), běh pod SYSTEM |

### 4.3 Omezení prostředí (AXIMA)

Návrh musel respektovat reálná omezení produkčního prostředí, která zásadně ovlivnila rozhodnutí:

- **AllSigned GPO** — všechny PowerShell skripty spouštěné na strojích musí být podepsané prod certem
  `CN=powershell.domena.loc`; `-ExecutionPolicy Bypass` to neobejde. Důsledek: provozní skripty
  (deploy, watchdog) musí být buď podepsané, nebo **PS-free** (scheduled tasky přes `schtasks`).
- **Bezpečnostní klasifikátor** — automaticky blokuje některé operace na produkčním SQL_SERVER a změny
  vlastních oprávnění. Důsledek: prod-deploy API a SQL granty spouští **lidský operátor**, build se
  jen připraví.
- **NAT / firewall** — stanice za NATem, dynamické IP. Důsledek: **push model** a **klíčování na
  hostname**, ne IP.
- **WinRM zavřený** — vzdálená správa přes WinRM nedostupná. Důsledek: deploy přes **SMB + remote
  `sc.exe`** (porty 135/445).
- **gMSA** — preferovaný způsob běhu služeb bez hesel v konfiguraci.

### 4.4 Rozsah a hranice systému (out of scope)

Explicitně **mimo rozsah** (a tedy předmět omezení, ne opomenutí — viz §19):

- Blokace **HID spoofing** (BadUSB jako klávesnice/síťová karta) — řešení cílí na storage-class.
- **Garantované pre-mount blokování** (médium se vůbec neobjeví) — user-mode agent je reaktivní;
  garance vyžaduje GPO Device Installation Restrictions nebo kernel filter driver.
- **Šifrování dat na médiu** (to řeší BitLocker To Go / DLP).
- **Klasifikace obsahu dat** (DLP).

---

# ČÁST II — Návrh

## 5. Přehled architektury

### 5.1 Komponenty

Systém tvoří čtyři logické komponenty:

```
┌──────────────────────────┐     push (HTTPS :5443)    ┌───────────────────────────┐
│  AGENT (klientská stanice)│ ─────────────────────────►│  API (ingest)             │
│  .NET 8 Windows Service   │   heartbeat / incidenty   │  ASP.NET Core, SQL_SERVER     │
│  běží jako SYSTEM         │ ◄───────────────────────── │  :5443 (HTTPS) / :5050    │
│  - DeviceMonitor (WMI)    │   whitelist + policy       │  - příjem incidentů (202) │
│  - WhitelistChecker (RSA) │                            │  - distribuce whitelistu  │
│  - PolicyEnforcer         │                            │  - heartbeat (enforce)    │
│  - DeviceBlocker          │                            └────────────┬──────────────┘
│  - lokální konzole :5080  │                                         │ read/write
└──────────────────────────┘                                         ▼
                                                          ┌───────────────────────────┐
┌──────────────────────────┐     read/write (SQL)        │  DATABÁZE (SQL Server)    │
│  KONZOLE (administrace)   │ ───────────────────────────►│  SQL_SERVER, DB USBGuardian   │
│  Blazor Server, APP_SERVER :4200│                             │  Incidents / Computers /  │
│  - Přehled / Stanice      │ ◄── AD sync ── Active Dir.  │  WhitelistDevices /       │
│  - Whitelist (podpis)     │                             │  WhitelistVersions /      │
│  - Nastavení / Databáze   │                             │  AppSettings              │
│  - auto-enrollment        │                             └───────────────────────────┘
└──────────────────────────┘
```

| Komponenta | Technologie | Umístění | Identita |
|-----------|-------------|----------|----------|
| Agent | C# .NET 8, Windows Service | každá stanice | LocalSystem (SYSTEM) |
| API | ASP.NET Core (Kestrel) | SQL_SERVER, `C:\USBGuardian.Api` | gMSA `gmsa-api$` |
| Konzole | Blazor Server | APP_SERVER, `C:\Apps\USBGuardianConsole` | LocalSystem (= `APP_SERVER$`) |
| Databáze | SQL Server | SQL_SERVER, DB `USBGuardian` | — |

### 5.2 Dvě klíčové architektonické osy

**(a) Push model (agent → server).** Agent iniciuje veškerou komunikaci: periodický *heartbeat*
(hlásí online stav, verzi whitelistu a agenta; přijímá zpět příznak `enforce`, dostupnost nové verze
whitelistu a případné příkazy) a *incident sync* (odesílá frontu událostí). Server nemá zpětný kanál
k agentovi — vše „od serveru" se doručuje **přibalením do odpovědi na heartbeat**.

**(b) Dvouvrstvý server.** Operativa (konzole, AD sync, publikace whitelistu) běží na aplikačním
serveru **APP_SERVER**; databáze je čisté úložiště na **SQL_SERVER**. Ingestní API zatím běží na SQL_SERVER
(plánovaný přesun na APP_SERVER — viz roadmapa). Příjem incidentů (API) je **oddělen** od administrace
(konzole), aby nápor 500+ agentů neovlivnil použitelnost administrace.

### 5.3 Zásadní vlastnosti návrhu

- **Klient = 1:1 kopie serveru.** Agent nedrží vlastní „pravdu" — whitelist i politiku vynucování
  přebírá ze serveru a konverguje k němu. Lokálně drží jen podepsanou kopii (JSON soubor).
- **Server = zdroj pravdy.** Jakákoli lokální výjimka (break-glass) je dočasná a při příštím spojení
  se serverem se ruší.
- **Fail-secure.** Selhání ověření podpisu whitelistu nevede k „povolit vše", ale k bezpečné variantě.
- **Bez závislosti na PKI.** Šifrování i integrita stojí na vlastních mechanismech (self-signed +
  pinning, interní RSA klíč), nezávisle na firemní CA.
- **Least-privilege všude.** gMSA pro služby, granulární SQL granty, oddělené identity pro deploy.

---

## 6. Obhajoba návrhových rozhodnutí (decision log)

Tato kapitola je jádrem dokumentu pro oponenturu. Každé rozhodnutí je uvedeno spolu s **kontextem**,
**zváženými alternativami**, **zvolenou variantou** a **vědomým trade-offem**.

### 6.1 Push model vs. pull model

- **Kontext:** 500+ stanic za NATem/firewallem, s dynamickými IP, bez příchozí dostupnosti.
- **Alternativy:** (a) Pull — server se připojuje k agentům a vyžaduje od nich data; (b) Push — agent
  iniciuje odchozí spojení.
- **Zvoleno:** **Push.** Agentovi stačí odchozí spojení (HTTPS ven), což funguje za NATem bez
  port-forwardingu a bez evidence dynamických IP. Server nemusí znát adresu stanice.
- **Trade-off:** Server nemá okamžitý zpětný kanál → příkazy „od serveru" (vyžádání dat, změna
  `enforce`) se doručují s latencí ≤ heartbeat interval (~2 min). To je **vědomě přijatá** vlastnost;
  pro daný účel (politika média) je latence ≤2 min plně dostačující. (Diskuse latence viz §13.4.)

### 6.2 Blazor Server (.NET) vs. Node.js pro konzoli

- **Kontext:** Konzole sdílí datový model s API (EF Core entity, `AppDbContext`).
- **Alternativy:** (a) Node.js/React SPA; (b) Blazor Server (.NET).
- **Zvoleno:** **Blazor Server.** Umožňuje **přímý reuse EF modelů** z API (slinkované `DbModels.cs`,
  `AppDbContext.cs` — žádná duplikace schématu), jeden jazyk a runtime, a na serveru už ASP.NET Core
  běží. Odpadá samostatná API vrstva pro konzoli (čte SQL přímo).
- **Trade-off:** Blazor Server drží stav na serveru (SignalR spojení) — pro administrátorskou konzoli
  s malým počtem souběžných uživatelů (IT tým) je to bez problému; nebylo by vhodné pro veřejnou
  vysokokoncurrenční aplikaci, což ale není tento případ.

### 6.3 HttpListener vs. Kestrel pro lokální konzoli agenta

- **Kontext:** Agent potřebuje lokální diagnostické UI (loopback) a několik admin akcí.
- **Alternativy:** (a) Kestrel (ASP.NET Core); (b) `System.Net.HttpListener` (http.sys).
- **Zvoleno:** **HttpListener.** Agent je `Worker` bez ASP.NET Core runtime — Kestrel by přitáhl
  celý web stack. HttpListener postačí pro loopback dashboard a pár endpointů, bez další závislosti.
- **Trade-off:** Méně komfortu (ruční routing, žádný DI middleware), ale výrazně menší footprint a
  attack surface. Pro loopback-only, admin-only, převážně read-only rozhraní je to přiměřené.

### 6.4 Klíčování na hostname, ne IP

- **Kontext:** Stanice mají dynamické IP (DHCP).
- **Zvoleno:** **Hostname** jako primární klíč v tabulce `Computers` a v korelaci heartbeatů.
- **Trade-off:** Předpokládá rozumně unikátní hostnames v doméně (splněno přes AD). IP by byla
  nestabilní a nepoužitelná jako identita.

### 6.5 Self-signed cert + pinning otisku vs. firemní CA

- **Kontext:** NIS2 vyžaduje šifrovaný přenos; nasazení nemá záviset na externí PKI.
- **Alternativy:** (a) Certifikát z firemní CA; (b) Let's Encrypt (nedostupné interně); (c) self-signed
  + pinning otisku na agentovi.
- **Zvoleno:** **Self-signed cert generovaný API při startu + pinning otisku** (`tls.pinnedThumbprint`)
  na agentovi. Šifrované **i** ověřené, bez jakékoli závislosti na CA, bez expirací řízených externě.
- **Trade-off:** Otisk se musí jednorázově distribuovat do konfigurace agentů (součást nasazení).
  Výměna certu vyžaduje aktualizaci pinu. Pro uzavřený systém agent↔API je to přijatelné a naopak
  odolnější (nezávislost na stavu firemní CA). Alternativně lze zapnout CA validaci.

### 6.6 `MachineKeySet` vs. `EphemeralKeySet` u self-signed certu

- **Kontext:** API běží pod **gMSA**; Kestrel musí udělat TLS handshake přes Schannel.
- **Problém (nalezený latentní bug):** S `EphemeralKeySet` Schannel **neudělá** server-side handshake
  (privátní klíč není trvale dostupný pro službu) → spojení selže.
- **Zvoleno:** **`MachineKeySet`** — klíč uložený do strojového úložiště, dostupný i pod gMSA.
- **Poučení:** Toto je typický příklad rozhodnutí, které není zřejmé „od stolu" a vyplynulo z reálného
  testu; dokumentováno, aby se neopakovalo.

### 6.7 Server-side automatický podpis whitelistu vs. offline ruční podpis

- **Kontext:** Whitelist musí být podepsaný (integrita), ale správa musí být provozně únosná.
- **Alternativy:** (a) **Offline podpis** — privátní klíč mimo server, každá změna = ruční offline krok
  (maximální bezpečnost klíče, principiálně „klíč nikdy na serveru"); (b) **Server-side auto-podpis** —
  privátní klíč na serveru, konzole podepisuje automaticky po každé změně katalogu.
- **Zvoleno:** **Server-side auto-podpis** (`WhitelistPublisher`). Po každé změně katalogu (i ručním
  „Publikovat nyní") konzole vydá novou verzi, podepíše interním RSA klíčem (`Whitelist:PrivateKeyPath`
  na APP_SERVER), uloží `Json`+`Signature` do DB a aktivuje.
- **Trade-off (vědomě zvolený a klíčový pro oponenturu):** Privátní klíč **je** na serveru APP_SERVER
  (chráněn ACL/DPAPI) výměnou za **plnou automatizaci**. Původní princip „privátní klíč nikdy na
  serveru" byl **vědomě opuštěn**, protože ruční offline krok po každé změně katalogu byl provozně
  neúnosný a vedl by k tomu, že se whitelist nebude udržovat aktuální (větší reálné riziko než
  kompromitace ACL-chráněného klíče na app serveru). Klíč je **interní klíč USB Guardianu**, ne
  firemní code-signing cert ani CA — jeho kompromitace ohrožuje pouze integritu whitelistu, nic víc.
  Offline `WhitelistSigner` zůstává jako nástroj pro generování klíčů a ruční ověření.

### 6.8 Klient = 1:1 bajtová kopie serveru

- **Kontext:** Podpis musí sedět bajt na bajt; agent nemá databázi.
- **Zvoleno:** Server drží **přesný podepsaný blob** (`WhitelistVersions.Json`, `NVARCHAR(MAX)`), API
  ho servíruje **verbatim**, agent uloží jako JSON soubor a ověří. Kanonizace: UTF-8 bez BOM,
  SHA-256/PKCS#1. Týž blob string se podepisuje, servíruje i ověřuje.
- **Trade-off:** Server musí blob uchovat přesně (ne re-serializovat) — proto `NVARCHAR(MAX)` a
  verbatim servírování. Výhodou je triviální a robustní ověření na agentovi (žádná re-serializace,
  žádné rozdíly v pořadí klíčů či escapování).

### 6.9 Vynucování blokace přes `Disable-PnpDevice` (ovladač) vs. IOCTL eject

- **Kontext:** Blokace musí být spolehlivá a reverzibilní, ideálně bez závislosti na drive-letteru.
- **Alternativy:** (a) `IOCTL_STORAGE_EJECT_MEDIA` (vyžaduje drive-letter, médium lze znovu připojit);
  (b) `Disable-PnpDevice` dle `PNPDeviceID` (deaktivace na úrovni PnP uzlu).
- **Zvoleno:** **`Disable-PnpDevice`.** Nevyžaduje drive-letter (lze blokovat hned na `Win32_DiskDrive`
  connect), funguje okamžitě, je **reverzibilní** (`Enable-PnpDevice`) a používá `PNPDeviceID`, který
  je vždy k dispozici.
- **Trade-off:** Volá se přes PowerShell (proces `powershell.exe`) — drobná režie na spuštění procesu,
  přijatelná pro řídkou událost (připojení média). Reverzibilita je naopak nutná pro break-glass a
  reconciliaci.

### 6.10 Atribuce uživatele přes WTS API (ne `Environment.UserName`)

- **Kontext:** Agent běží jako **SYSTEM**, takže `Environment.UserName` vrací strojový účet (`HOST$`),
  ne reálného uživatele — to by znehodnotilo auditní stopu.
- **Zvoleno:** `SessionUser` přes **WTS API** (`WTSGetActiveConsoleSessionId`, enumerace session,
  `WTSQuerySessionInformation`) → reálný `DOMÉNA\uživatel`. Fail-safe: bez přihlášeného uživatele
  fallback na strojový účet (incident se zapíše vždy).
- **Trade-off:** Závislost na WTS API (Windows-specifické) — což je v pořádku, agent je Windows-only.

### 6.11 Soft-delete (deaktivace) vs. hard-delete whitelistu

- **Kontext:** Odebrání média z whitelistu; NIS2 audit preferuje uchování historie schválení.
- **Zvoleno:** **Obojí** — checkbox „Aktivní" = soft-deaktivace (UPDATE, zachová auditní záznam),
  tlačítko ✕ = hard-delete (DELETE, čistý katalog). Publikace snapshotuje jen aktivní záznamy, takže
  obě varianty funkčně odeberou médium z vynucovaného whitelistu.
- **Trade-off:** Hard-delete vyžaduje DELETE oprávnění na `WhitelistDevices` (granulární grant — konzole
  tu tabulku celá vlastní). DELETE se **nedává** na `WhitelistVersions` (verze = append-only audit).
  Pro NIS2 lze preferovat soft-delete; systém umožňuje obojí.

### 6.12 Least-privilege deploy přes gMSA scheduled task

- **Kontext:** Auto-nasazení agenta vyžaduje admin práva na klientech; konzole je nesmí mít.
- **Zvoleno:** Konzole (identita `APP_SERVER$`) jen **zapíše seznam cílů**; instalaci provede
  **oddělený scheduled task na APP_SERVER pod dedikovaným gMSA** (`gmsa-deploy$`), který má admin jen na
  klientech. Konzole tak nemění svou identitu ani SQL granty.
- **Trade-off:** Více pohyblivých částí (task, gMSA, soubor cílů), ale striktní oddělení rolí —
  kompromitace konzole nedává admin na klientech.

### 6.13 Souhrn rozhodnutí

Detailní tabulkový souhrn všech rozhodnutí viz **Příloha F**.

---

## 7. Datový model a datové toky

### 7.1 Datový model (tabulky)

| Tabulka | Účel | Klíčové sloupce |
|---------|------|-----------------|
| `Computers` | Inventář stanic z AD + stav agenta | `Hostname` (klíč), `Domain`, `OperatingSystem`, `AdPath`, `InActiveDirectory`, `LastSeen`, `AgentVersion` |
| `Incidents` | Auditní záznamy událostí | `Timestamp`, `Hostname`, `Username`, `VendorId`/`ProductId`/`SerialNumber`, `FriendlyName`, `SizeBytes`, `Action`, `WhitelistVersion`, `DisconnectedAt` |
| `WhitelistDevices` | Katalog schválených médií | `VendorId`, `ProductId`, `SerialNumber`, `Description`, `ApprovedBy`, `ApprovedAt`, `IsActive` |
| `WhitelistVersions` | Podepsané verze whitelistu (snapshoty) | `Version`, `IssuedAt`, `ValidUntil`, `IssuedBy`, `Json` (NVARCHAR(MAX)), `Signature` (NVARCHAR(MAX)), `IsActive` |
| `AppSettings` | Centrální provozní nastavení (key/value) | `Key`, `Value` (NVARCHAR(MAX)) |

Poznámka k návrhu: `WhitelistVersions` **neodkazuje** na `WhitelistDevices` cizím klíčem — drží
**nezávislý snapshot** (JSON blob) v okamžiku publikace. Tím je verze imunní vůči pozdějším změnám
katalogu a smazání řádku v katalogu nerozbije historické verze (a neselže na FK).

### 7.2 Datový tok — incident (připojení neschváleného média)

```
1. USB připojeno → WMI __InstanceCreationEvent (Win32_DiskDrive)
2. DeviceMonitor: parsuje VID:PID:Serial z PNPDeviceID (serial trim)
3. WhitelistChecker: klíč VID:PID:SERIAL → index O(1) → NENÍ na whitelistu
4. PolicyEnforcer: efektivní režim (PolicyState) → block / warn
5a. block: DeviceBlocker.BlockDevice → Disable-PnpDevice + zápis do blocked.json
5b. NotificationService → Toast frontu → ToastHelper (user session) zobrazí
6. IncidentLogger: zápis do denní JSON fronty (queue/), atribuce přes SessionUser (WTS)
7. IncidentSync (≤1 min, nebo hned na ReportNow): POST /api/incidents (HTTPS+pinning)
8. API IncidentsController: 202 Accepted → IncidentQueue (do DB nepíše)
9. IncidentQueueWorker (async): zápis do SQL tabulky Incidents
10. Konzole (Přehled): agregace, filtr, export; alerty e-mailem (IncidentAlertService)
```

### 7.3 Datový tok — distribuce whitelistu (1:1 kopie)

```
Admin změní katalog (konzole) → WhitelistPublisher:
   snapshot aktivních zařízení → kanonický whitelist.json blob (verze yyyy-MM-dd-vN)
   → podpis interním RSA klíčem (APP_SERVER) → uložit Json+Signature, aktivovat
API: GET /api/whitelist (blob verbatim) · GET /api/whitelist/signature (base64)
Agent (heartbeat ≤2 min hlásí WhitelistUpdateAvailable):
   stáhne blob+podpis → SignatureVerifier ověří (fail-secure) → uloží whitelist.json (+.sig)
   → WhitelistChecker.Reload() (zahodí cache) → RebuildIndex (Dictionary O(1))
```

### 7.4 Datový tok — vynucování a reconciliace

```
Heartbeat → HeartbeatController vrací Enforce (z AppSettings policy.enforce, APP_SERVER = pravda)
Agent: PolicyState.OnServerHeartbeat(enforce) (+ zruší lokální break-glass override)
WhitelistSync.ReconcileBlocked (každý cyklus):
   - blokování ON  → ReEnforceConnectedDevices() (znovu zablokovat připojená neschválená)
   - blokování OFF → UnblockAll() (vrátit vše, co agent zakázal)
   - blokované, mezitím schválené (IsAllowedKey) → vrátit i při zapnutém blokování
Break-glass (lokální konzole): SetOverride + UnblockAll() okamžitě
```

### 7.5 Datový tok — AD sync a auto-enrollment

```
AdSyncRunner (60 min + on-demand): AD (objectCategory=computer, ne disabled)
   → upsert Computers (klíč hostname; NEpřepisuje LastSeen/AgentVersion)
   → reconciliation: InActiveDirectory && LastSeen==null && AgentVersion=="" = chybí agent
AgentDeployService (po syncu, default OFF + dry-run):
   aplikuje defaultEnroll + include/exclude → zapíše deploy.targetsFile
   → scheduled task na APP_SERVER (gMSA) → Deploy-AgentFleet.ps1 → instalace na klienty
```

---

# ČÁST III — Implementace

## 8. Agent (klientská stanice)

Agent je .NET 8 Windows Service běžící pod **LocalSystem (SYSTEM)**. Skládá se z hostovaných služeb
(`BackgroundService`) a sdílených singletonů registrovaných v DI (`Program.cs`).

### 8.1 `DeviceMonitor` — detekce médií

Sleduje připojení/odpojení médií třemi **WMI watchery**:

1. `__InstanceCreationEvent` na `Win32_DiskDrive` — fyzický disk připojen.
2. `__InstanceCreationEvent` na `Win32_LogicalDisk` — přiřazen drive-letter.
3. `__InstanceDeletionEvent` na `Win32_DiskDrive` — disk odpojen (doplní `DisconnectedAt`).

**Párování (timing fix).** Pořadí WMI událostí (disk vs. drive-letter) není garantované, proto monitor
drží dvě „pending" mapy (`_pendingDevices`, `_pendingDriveLetters`) s timeoutem 30 s a koreluje přes
`DiskIndex`. **Klíčové rozhodnutí:** enforcement se spouští **hned na `Win32_DiskDrive` connect**, bez
čekání na drive-letter (minimalizace okna, kdy se médium stihne namountovat). Drive-letter, pokud
dorazí, se jen doplní do logu.

**Startovní sken (`ScanConnectedDevices`).** Watchery chytají jen *nová* připojení; média připojená
před startem služby by zůstala neviděna. Při startu se proto jednorázově projdou všechna připojená
USB/SD média a vyhodnotí (i pro blokaci „naostro" po restartu agenta).

**Re-enforcement (`ReEnforceConnectedDevices`).** Symetrický protějšek k auto-re-enable: když je
blokování zapnuté, projde připojená média a **znovu zablokuje** ta neschválená, která ještě nejsou
blokovaná. Řeší díru, kdy se médium vrátí break-glassem a po zapnutí blokování zpět by zůstalo
připojené a nezablokované. Idempotentní (schválená i už-blokovaná přeskakuje).

**WMI watchdog.** Každých 5 min ověří živost subscriptions (dotaz na `Win32_DiskDrive`); při selhání
re-registruje watchery. Čas poslední WMI události je vystaven do lokální konzole (indikátor „STALE").

### 8.2 `WhitelistChecker` — ověření vůči whitelistu

- Načítá lokální `whitelist.json`, před použitím ověří **RSA-4096 podpis** (fail-secure: neplatný/chybějící
  podpis → whitelist odmítnut → `null`).
- **Indexy O(1).** Po načtení staví `Dictionary` (`VID:PID:SERIAL` → záznam) — match je O(1), škáluje
  i na 10k+ zařízení. Volitelně wildcard index (`VID:PID` bez sériáku) jen při `AllowWildcards=true`
  (default vypnuto, s bezpečnostním varováním).
- **Cache 5 min** + **`Reload()`**. Načtený whitelist se cachuje 5 minut (úspora I/O při častých
  dotazech na connect). `WhitelistSync` po stažení nové verze volá `Reload()` → cache se zahodí → nová
  verze platí **ihned** (jinak by se nově schválené/odebrané médium projevilo až po vypršení cache —
  tento latentní problém byl nalezen a opraven, viz §18).
- **Per-záznam expirace** (`ValidUntil`, NULL = trvalé) i expirace celé verze whitelistu (degraded mód
  s varováním).

### 8.3 `PolicyEnforcer` — rozhodnutí o akci

Pro každé médium rozhodne dle **efektivního režimu** z `PolicyState`:
- schválené → tichý audit záznam `Allowed`;
- neschválené → `Warned` (médium funguje) nebo `Blocked` (deaktivace) dle efektivního režimu;
- expirovaný whitelist → dle `onExpiredWhitelist` (warn/block/allow).

Efektivní režim **neřídí** fixní lokální `policy.mode`, ale `PolicyState.EffectiveMode()`:
`override aktivní ? warn : (server přijat ? (enforce ? block : warn) : lokální default)`.

### 8.4 `DeviceBlocker` — blokace a vracení

- `BlockDevice(pnpId, key)` → `Disable-PnpDevice` (přes PowerShell), při úspěchu zapíše do
  `blocked.json` (mapa `PNPDeviceID → klíč VID:PID:SN`) pro pozdější reconciliaci.
- `UnblockDevice(pnpId)` → **spolehlivé vracení**: nejdřív přesná shoda `Get-PnpDevice -InstanceId`
  (jako ruční `Enable-PnpDevice`), pak fallback `-like`; `Enable-PnpDevice` s `-ErrorAction Stop` v
  `try/catch`. Výsledky: `ENABLED` (povoleno → odebrat ze seznamu), `GONE` (médium odpojeno → bereme
  jako vyřešené a odebíráme, ať nezůstane viset), `FAILED` (skutečné selhání → zalogovat a ponechat na
  retry). Tato robustnost vznikla po zjištění, že naivní varianta hlásila *falešný úspěch* při
  ne-terminující chybě `Enable-PnpDevice` (viz §18).
- `UnblockAll()` → vrátí vše, co agent zakázal (break-glass / vypnuté vynucování).
- Stav blokovaných je **perzistovaný** (`blocked.json`), takže přežije restart služby.

### 8.5 `SessionUser` — atribuce reálného uživatele

Přes WTS API zjistí přihlášeného uživatele aktivní interaktivní session (`DOMÉNA\uživatel`), protože
agent jako SYSTEM by jinak hlásil strojový účet. Fail-safe fallback na strojový účet (incident se
zapíše vždy). Použito v `Incident.Username`, logu i Toast notifikaci.

### 8.6 `IncidentLogger` a synchronizace

- `IncidentLogger` ukládá incidenty do denních JSON front (`queue/`), po odeslání přesouvá do `sent/`
  s vlastní retencí.
- `IncidentSync` (interval ~1 min, s jitterem) odesílá frontu na API; probudí se dřív na signál
  `ReportNow` (vyžádání dat z konzole).
- `WhitelistSync` (interval ~2 min) posílá heartbeat (verze, online, agent commit), přijímá `enforce`,
  dostupnost nové verze whitelistu a příkazy; stahuje a ověřuje whitelist; spouští `ReconcileBlocked`.
- `SyncSignals` — sdílený signál heartbeat → okamžitý flush incidentů.

### 8.7 `PolicyState` — sdílený stav vynucování

Singleton držící: server `enforce` (z heartbeatu), `serverReceived` (zda už dorazil), a lokální
break-glass override (`_overrideUntil`, perzistováno do `override.json`). Klíčová logika:
- `OnServerHeartbeat(enforce)` — aplikuje serverové enforce a **ruší** lokální override (server = pravda);
- `EffectiveMode(localMode)` — viz §8.3;
- `SetOverride/ClearOverride` — break-glass se stropem 72 h.

### 8.8 Lokální admin konzole agenta

`LocalConsoleService` — `HttpListener` na `127.0.0.1:5080` (volitelné, default vypnuto). **Admin-only**
(`WindowsPrincipal.IsInRole(Administrator)`), převážně **read-only**. Vystavuje živý stav: whitelist
(verze, stav, seznam zařízení), verze agenta (commit), WMI watchdog, fronta, připojená média, poslední
události, **počet blokovaných** médií. Zapisující akce (admin-only, loopback): `POST /api/override[/clear]`
(break-glass), `POST /api/unblock-all` (ruční okamžité vrácení), `POST /api/restart` (self-restart
služby). Loopback + Windows auth + admin-only + převážně read-only ⇒ heslo netřeba.

### 8.9 Odolnost agenta

- **Watchdog (scheduled task, à 3 min, PS-free)** — nahodí službu, pokud spadne. Útočník musí vyřadit
  *službu i task* (dva nezávislé mechanismy).
- **Recovery actions služby** (`sc failure`) — automatický restart při pádu.
- **Offline provoz** — agent funguje bez serveru (lokální whitelist), heartbeat jen reportuje a přebírá
  politiku; break-glass umožní práci offline.

---

## 9. Serverové API (ingest)

ASP.NET Core aplikace na SQL_SERVER, Kestrel bind **HTTPS :5443** (+ HTTP :5050, plánováno k uzavření).
Běží pod gMSA `gmsa-api$`. Autentizace agentů Windows Auth (Kerberos/Negotiate), autorizace přes
policy `USBGuardianClients` (členství v `Authorization:AllowedGroups`).

### 9.1 Controllery

| Controller | Endpoint | Funkce |
|------------|----------|--------|
| `IncidentsController` | `POST /api/incidents` | Příjem incidentů od agentů → **202 Accepted** + vložení do `IncidentQueue` (do DB **nepíše**). `GET` pro konzoli. |
| `WhitelistController` | `GET /api/whitelist` | Aktivní podepsaný blob **verbatim**. |
| | `GET /api/whitelist/signature` | Base64 podpis. |
| `HeartbeatController` | `GET /api/heartbeat` | Vrací `CurrentWhitelistVersion`, `WhitelistUpdateAvailable`, `ReportNow`, `Enforce`, `ServerTime`. Posune `LastSeen`/`AgentVersion`. |
| (cert info) | `GET /api/cert-info` | Otisk self-signed certu (pro pinning). |
| (version) | `GET /api/version` | Commit běžícího API. |

### 9.2 Oddělení příjmu od zápisu (odolnost)

Klíčové rozhodnutí pro NFR-4 (odolnost pod náporem): `IncidentsController` **nepíše do DB přímo**.
Přijatý incident vloží do **`IncidentQueue`** (in-memory) a vrátí 202. Asynchronně **`IncidentQueueWorker`**
(hosted service) odebírá z fronty a zapisuje do SQL. Tím se příjem (rychlý, nezávislý na DB latenci)
odděluje od zápisu — nápor 500 agentů neblokuje na DB.

> **Latentní bug (nalezený a opravený):** `IncidentsController` vyžadoval `IncidentQueue`, ale
> `Program.cs` ji neregistroval v DI → **500 na každý `/api/incidents`** (heartbeat bez té závislosti
> jel). Po `AddSingleton<IncidentQueue>` + `AddHostedService<IncidentQueueWorker>` controller vrací 202
> a worker zapisuje. Uvedeno jako příklad důležitosti integračního ověření celé cesty.

### 9.3 `SelfCert` — self-signed TLS

Při startu vygeneruje/persistne vlastní certifikát (`C:\ProgramData\USBGuardian\api-tls.pfx`,
`MachineKeySet`), Kestrel ho nabinduje na :5443. Otisk zaloguje a vrací přes `/api/cert-info`. Bez CA,
bez cert store (viz §6.5, §6.6).

### 9.4 `RetentionService`

`BackgroundService` (à 6 h). Jako jediná komponenta s DELETE právy na `Incidents` (`db_datawriter`)
maže incidenty starší limitu (`retention.incidentDays`, `ExecuteDeleteAsync`) a zapíše `retention.lastRun`.
Konzole má na `Incidents` jen čtení/zápis bez delete — proto je enforcement retence v API
(least-privilege: mazací právo jen tam, kde je potřeba).

---

## 10. Administrátorská konzole

Blazor Server na APP_SERVER (:4200), AXIMA UI standard (dark/light, patička se servisním řádkem). Čte/píše
SQL_SERVER přes EF Core (modely slinkované z API). Autorizace: Windows Auth, dovnitř jen členové
`Authorization:AdminGroups` / účty `AllowedUsers` (appsettings = lockout-safe bootstrap) **nebo** DB
seznam z Nastavení.

### 10.1 Stránky

- **Přehled** — dlaždicový souhrn napříč listy, filtr (období/akce/fulltext), kumulace, sloupec
  „Schváleno" dle aktivního whitelistu, kapacita média. **Export:** CSV (Excel) + **manažerský report**
  (KPI + inline-SVG grafy, tisknutelné na 1–2 A4).
- **Stanice** — AD inventář, dlaždice (vše / hlásí / **zmlklo agentů** / chybí agent), cesta v AD,
  ikona komunikace, „Vyžádat data" (ReportNow), sloupec „Nasazení" (řízení auto-enrollmentu).
- **Whitelist** — zadání sériovým číslem + autofill z incidentů, import, inline editace, checkbox
  Aktivní (soft-deaktivace) i ✕ mazání, **auto-publikace podepsané verze** po každé změně.
- **Nastavení** — vynucování, dohled komunikace, whitelist přístupu, e-mail + alerty, auto-enrollment
  (+ default pro nové PC), retence, AD sync, Údržba (reload `AccessCache`).
- **Databáze** — read-only přehled obsahu DB (počty, rozsah incidentů, výpis `AppSettings`).
- **Dokumentace** — render `.md` (Markdig) + interaktivní animace „Jak to funguje".

### 10.2 `AdSyncRunner` / `AdSyncService`

Načte počítače z AD (`new DirectoryEntry()` — ambient doména, nic natvrdo), upsert do `Computers`
(klíč hostname, nepřepisuje `LastSeen`/`AgentVersion`). Reconciliation „v AD ⨯ hlásí agenta".

### 10.3 `WhitelistPublisher`

Po každé změně katalogu: snapshot aktivních zařízení → kanonický blob → podpis interním RSA klíčem →
uložení `Json`+`Signature`, aktivace. Viz §6.7, §11.

### 10.4 `AgentDeployService` (auto-enrollment)

24/7 orchestrátor (default OFF + dry-run). Po AD syncu najde stanice bez agenta
(`InActiveDirectory && LastSeen==null && AgentVersion==""`), uplatní `defaultEnroll` + include/exclude
výjimky a (v ostrém režimu) zapíše cíle do `deploy.targetsFile`. Instalaci provede scheduled task na
APP_SERVER pod gMSA (viz §6.12, §15).

### 10.5 `IncidentAlertService` + `EmailSender`

Background notifier: souhrn nových neschválených incidentů e-mailem (SMTP relay / M365 Direct Send),
baseline při prvním běhu, interval/throttle.

### 10.6 `AccessCache` a robustní chybové hlášky

`AccessCache` cachuje seznam povolených uživatelů/skupin (reload přes Nastavení → Údržba). Chybové
hlášky DB operací rozbalují **celý řetězec InnerException** (`Detail(ex)`) — EF `DbUpdateException` má
v `.Message` jen „See the inner exception", takže bez rozbalení by se reálná příčina (např. „DELETE
permission denied on WhitelistDevices") v UI nezobrazila.

---

## 11. Kryptografie a podepisování whitelistu

### 11.1 Integrita whitelistu — RSA-4096

Whitelist je podepsán **RSA-4096** (SHA-256, PKCS#1). Agent ověřuje podpis před každým použitím
(`SignatureVerifier`), **fail-secure** (neplatný/chybějící podpis → whitelist se nepoužije). Veřejný
klíč je na agentech (`whitelist_public.pem`), privátní na serveru APP_SERVER (`Whitelist:PrivateKeyPath`,
gitignored, chráněn ACL).

### 11.2 Bajtová přesnost (kanonizace)

Týž blob string se **podepisuje, servíruje i ověřuje** — UTF-8 bez BOM, žádná re-serializace. Server
uchovává přesný blob (`NVARCHAR(MAX)`), API servíruje verbatim, agent ho uloží a ověří 1:1. Tím se
vylučují rozdíly v pořadí klíčů, escapování či kódování, které by jinak rozbily podpis.

### 11.3 Oddělení od firemní PKI

Podpisový klíč whitelistu je **interní klíč USB Guardianu**, **ne** firemní code-signing cert ani CA.
Jeho účel je výhradně integrita whitelistu. To je odlišné od:
- **TLS** (self-signed cert API + pinning — §6.5),
- **podpisu PowerShell skriptů** (firemní cert `CN=powershell.domena.loc`, AllSigned GPO — §15).

Tři nezávislé „kryptografické světy" jsou záměrně odděleny, aby kompromitace jednoho neohrozila ostatní.

### 11.4 Životní cyklus a rizika klíče

- **Generování:** offline `WhitelistSigner` (`tools/WhitelistSigner`).
- **Uložení privátního klíče:** server APP_SERVER, gitignored, ACL/DPAPI.
- **Riziko:** kompromitace privátního klíče by umožnila podvrhnout whitelist → mitigace ACL + omezený
  dopad (jen integrita whitelistu). Diskuse trade-offu „klíč na serveru" viz §6.7 a §19.

---

# ČÁST IV — Bezpečnost a vynucování

## 12. Bezpečnostní a hrozbový model

### 12.1 Aktiva a důvěryhodné hranice

- **Aktiva:** integrita whitelistu (pravidla), auditní stopa (incidenty), dostupnost vynucování,
  důvěrnost firemních dat (nepřímo — prevence exfiltrace).
- **Důvěryhodné hranice:** server APP_SERVER (= zdroj pravdy, drží privátní klíč), API/DB (gMSA), agent
  (běží jako SYSTEM, drží jen veřejný klíč a podepsanou kopii).

### 12.2 Hrozby a protiopatření (STRIDE)

| Kategorie | Hrozba | Protiopatření |
|-----------|--------|---------------|
| **Spoofing** | Podvržení serveru agentovi (MITM) | TLS + pinning otisku certu (agent ověří přesný server) |
| | Podvržení agenta serveru | Windows Auth (Kerberos), členství v AD skupině `USBGuardianClients` |
| **Tampering** | Podvržení whitelistu (přidání útočníkova média) | RSA-4096 podpis, ověření na agentovi (fail-secure) |
| | Modifikace lokálního `whitelist.json` na stanici | Podpis nesedí → whitelist odmítnut; útočník nemá privátní klíč |
| | Modifikace `blocked.json` / `override.json` | Vyžaduje lokální admin; override se ruší při heartbeatu (server = pravda) |
| **Repudiation** | Uživatel popře připojení média | Incident s atribucí přes WTS API (`DOMÉNA\uživatel`), centrální audit |
| **Information disclosure** | Odposlech komunikace agent↔API | TLS šifrování |
| | Citlivé hodnoty v repozitáři | `*.local.json` a privátní klíč gitignored |
| **Denial of service** | Nápor incidentů shodí příjem | 202 + in-memory fronta + worker (oddělení příjmu od zápisu) |
| | Útočník zastaví službu agenta | Watchdog (scheduled task) + recovery actions — dva nezávislé mechanismy |
| **Elevation of privilege** | Kompromitace konzole → admin na klientech | Konzole nemá admin na klientech; deploy dělá oddělený gMSA task |
| | Zneužití lokální konzole agenta | Loopback-only, admin-only, převážně read-only |

### 12.3 Bezpečnostní vrstvy (defense in depth)

| Vrstva | Mechanismus |
|--------|-------------|
| Transport | TLS 1.2+ (Kestrel), pinning otisku |
| Integrita pravidel | RSA-4096 podpis whitelistu, fail-secure |
| Autentizace | Windows Auth (Kerberos / Negotiate) |
| Autorizace | AD skupiny (`USBGuardianClients`, admin skupiny konzole) + whitelist účtů |
| Identity služeb | gMSA (bez hesel v konfiguraci) |
| Least-privilege DB | granulární granty (read vše; write jen potřebné tabulky; DELETE jen kde nutné) |
| Least-privilege deploy | oddělený gMSA jen s admin na klientech |
| Tamper-resistance | služba + watchdog, běh pod SYSTEM |
| Konfigurace | citlivé hodnoty mimo repozitář (`*.local.json`) |

### 12.4 Předpoklady a hranice modelu

Model **předpokládá**, že:
- útočník **nemá** trvalý lokální admin/SYSTEM na stanici (jinak může agenta vyřadit — to platí pro
  jakýkoli host-based agent a je mimo dosažitelnou garanci);
- doménová infrastruktura (AD, Kerberos, gMSA) je důvěryhodná;
- server APP_SERVER a jeho ACL na privátní klíč jsou chráněné.

Tyto předpoklady jsou explicitně uvedeny, protože oponent na ně oprávněně cílí. Mitigace (watchdog,
audit, server = pravda) **zvyšují náklady útoku**, ale negarantují odolnost proti lokálnímu adminovi —
což je principiální omezení host-based přístupu (viz §19).

---

## 13. Model vynucování politiky

### 13.1 Fáze 1–3

- **Fáze 1 — distribuce whitelistu (1:1).** Automatický server-side podpis, agent = bajtová kopie
  (§6.7, §6.8, §7.3).
- **Fáze 2 — distribuce politiky.** Heartbeat nese `enforce` (`AppSettings policy.enforce`, APP_SERVER =
  pravda); agent použije efektivní režim (enforce → block, jinak warn).
- **Fáze 3 — lokální break-glass.** Lokální admin může dočasně (strop 72 h) vypnout blokování pro
  práci offline; perzistováno, logováno jako incident, **zrušeno při příštím heartbeatu** (server = pravda).

### 13.2 Reconciliace (symetrie)

Klíčová vlastnost — vynucování je **obousměrně samohojící**:

| Přechod | Akce agenta |
|---------|-------------|
| Blokování **vypnuto** (break-glass / `enforce=false`) | Vrátit **vše**, co agent zakázal (`UnblockAll`). Lokálně **okamžitě**; serverově do ≤ heartbeat. |
| Blokování **zapnuto** | **Znovu zablokovat** připojená neschválená média, která nejsou blokovaná (`ReEnforceConnectedDevices`). |
| Médium **schváleno** (přidáno na whitelist) za běhu | Vrátit i při zapnutém blokování (reconcile `IsAllowedKey`); platí ihned po stažení (cache invalidace). |
| Médium **odebráno** z whitelistu | Zablokuje se (na connect, re-enforce, nebo po restartu); po stažení nové verze ihned. |

### 13.3 Spolehlivost a idempotence

- **Vracení** (`UnblockDevice`) je robustní (přesný `-InstanceId` + fallback, ošetření `GONE`/`FAILED`)
  — neopakuje falešný úspěch, odpojené médium uklidí, skutečné selhání ponechá na retry.
- **Re-blokace** je idempotentní (přeskakuje schválená i už-blokovaná) → lze bezpečně volat každý cyklus.
- **Stav** (`blocked.json`, `override.json`) je perzistovaný → přežije restart.

### 13.4 Latence a její limity

- **Lokální akce** (break-glass zapnout/vypnout) → **okamžité** (synchronní z konzole 5080).
- **Serverové změny** (`enforce`, whitelist) → ≤ heartbeat interval (~2 min). Vědomě přijaté (push
  model, §6.1); pro politiku média plně dostačující.
- **Okno před blokací na connect** — agent blokuje hned na `Win32_DiskDrive` connect, ale Windows
  removable storage mountuje velmi rychle; krátký okamžik před `Disable-PnpDevice` nelze v user-mode
  plně eliminovat. **Garantované pre-mount blokování** vyžaduje GPO Device Installation Restrictions
  nebo kernel filter driver (viz §19). Toto je nejvýznamnější otevřené omezení a je uvedeno poctivě.

---

## 14. Auditovatelnost a soulad s NIS2

### 14.1 Auditní stopa

Každá relevantní událost je **incident** s časem, hostname, **reálným uživatelem**, identifikací média
(VID/PID/sériák), velikostí, akcí (`Allowed`/`Warned`/`Blocked`/`OverrideDisabled`) a verzí whitelistu.
Incidenty jsou centrálně agregované (konzole, export, manažerský report). Break-glass je logován jako
plnohodnotná auditní událost (kdo, kdy, na jak dlouho) a hlášen na server.

### 14.2 Evidence pro audit

- **Co se připojilo** (i neschválené, i schválené) — kompletní stopa.
- **Kdo** — atribuce přes WTS API.
- **Jak systém reagoval** — akce v incidentu.
- **Jaká pravidla platila** — verze whitelistu u každého incidentu + verzování `WhitelistVersions`.
- **Výjimky** — break-glass logován a auditovatelný.
- **Stav nasazení** — které stanice mají agenta, které „zmlkly" (možný výpadek/tamper).

### 14.3 Retence

Centrálně řízená (`retention.incidentDays`), enforcement v API (`RetentionService`). Umožňuje doložit
politiku uchovávání i kontrolu rozsahu dat (stránka Databáze ukazuje rozsah incidentů).

### 14.4 Mapování na požadavky

Detailní mapování NIS2 / ISO 27001 → konkrétní funkce viz **Příloha E**.

---

# ČÁST V — Provoz

## 15. Sestavení, nasazení a aktualizace

### 15.1 Sestavení

- **Agent (kompletní balíček):** `scripts\Build-AgentPackage.ps1` → self-contained agent (root) +
  `ToastHelper\` (notifikace v user session) + `tasks\` (definice scheduled tasků). Klient nepotřebuje
  .NET runtime.
- **Konzole / API:** `dotnet publish -c Release -r win-x64 --self-contained`.
- Buildy jsou **self-contained** — cílové stroje nepotřebují .NET SDK ani runtime.

### 15.2 Nasazení (mechanismus pro ztížené prostředí)

WinRM je zavřený, proto deploy probíhá přes **SMB + remote `sc.exe`** (porty 135/445), tj. síťový token
účtu bez UAC na cíli:

- **Konzole (APP_SERVER):** `robocopy` → `\\APP_SERVER\C$\Apps\USBGuardianConsole` (s `/XF appsettings.local.json`)
  + `sc.exe \\APP_SERVER stop/start`. Pozor: počkat na `STOPPED` (jinak je exe zamčený).
- **API (SQL_SERVER):** build staged na APP_SERVER, instalace na SQL_SERVER; spouští **operátor** (klasifikátor
  blokuje prod SQL_SERVER ops asistentovi). Počkat na `STOPPED` před `robocopy`.
- **Agent (fleet):** `Deploy-AgentFleet.ps1` (runspace pool, PS 5.1 i 7) — `robocopy` balíčku +
  `sc.exe \\HOST create` + recovery + **PS-free** watchdog a ToastHelper tasky (`schtasks`).

### 15.3 Auto-enrollment

Konzole (po opt-in) sama nasadí agenta na stanice bez něj: zapíše cíle, gMSA scheduled task na APP_SERVER
spustí `Deploy-AgentFleet.ps1`. Least-privilege (§6.12). Default OFF + dry-run; doporučený postup
`PC-01 → pilotní skupina → flotila`. Detail: [auto-deploy-setup.md](auto-deploy-setup.md).

### 15.4 Aktualizace klientů (návrh)

Aktuálně je čistě automatizována jen **čerstvá instalace**; **update** běžícího agenta je předmětem
roadmapy. Navržený postup (reuse stávající pipeline):

1. **Update-safe `Deploy-AgentFleet.ps1 -ReinstallExisting`:** `sc stop` → počkat na `STOPPED` →
   `robocopy` → `sc start`, s **dočasným vypnutím watchdog tasku** během kopie (jinak watchdog do 3 min
   nahodí starou službu a zamkne exe). (Stávající `-ReinstallExisting` zatím nezastavuje službu před
   kopií — to je známá mezera, viz §19.)
2. **Verzové cílení v konzoli:** porovnat `Computers.AgentVersion` (z heartbeatu) s cílovým commitem;
   zastaralé stanice → update-targets → gMSA task spustí reinstall. Stejný least-privilege model.
3. **Řízený rollout:** dry-run/opt-in, ring deployment, audit CSV; commit stamp slouží jako potvrzení
   úspěchu (konzole ukáže, kdo je aktuální).

**Alternativa „self-update" agentem** (stažení a přepsání vlastní exe) byla zvážena a **zamítnuta** jako
rizikovější (služba přepisující vlastní binárku, nutnost hostovaného a podepsaného buildu); push z APP_SERVER
je jednodušší a z velké části hotový.

### 15.5 Prostředí AXIMA — podpis PowerShell

Skripty běžící na strojích (Deploy-AgentFleet na APP_SERVER) musí být **podepsané** prod certem
`CN=powershell.domena.loc` (AllSigned GPO), publisher v `LocalMachine\TrustedPublisher`; před
podpisem CRLF + UTF-8 BOM. Watchdog a ToastHelper jsou **PS-free** (`schtasks`), takže na klientech
nevyžadují podpis.

---

## 16. Verzování a ověřitelnost nasazení

Každá komponenta hlásí svůj **git commit** (razítkováno při buildu přes MSBuild `git rev-parse`), aby
operátor ověřil, co přesně běží:

- **Konzole** — patička + `:4200/api/version`.
- **API** — `:5050/api/version`.
- **Agent** — hlásí commit v heartbeatu → konzole „Agent verze" per stanice.

Stamp je **spolehlivý**: generuje se `GitCommit.g.cs` přepsaný jen při změně commitu
(`WriteOnlyWhenDifferent`), což vynutí recompile i při jinak nezměněném kódu. Tím footer/`/api/version`
**přesně** odpovídá nasazenému gitu — slouží jako kontrola aktuálnosti řešení a jako potvrzení úspěšného
deploye/updatu.

> **Provozní zásada:** deploy se dělá **vždy jako poslední krok po commitu**, a po každém nasazení se
> reportuje živý commit hash k ověření operátorem.

---

## 17. Provoz, monitoring, retence

- **Dohled komunikace:** dlaždice „Zmlklo agentů" (hlásí agenta, ale `LastSeen` starší než práh
  `comm.silentAfterMinutes`) — indikátor výpadku nebo tamperu. Ikona komunikace per stanice.
- **Vyžádání dat (ReportNow):** konzole zapíše požadavek do `AppSettings`; agent při heartbeatu (≤2 min)
  flushne frontu. Slouží i jako audit „naposledy vyžádáno".
- **Alerty:** e-mail na nové neschválené incidenty (`IncidentAlertService`).
- **Retence:** centrálně řízená, enforcement v API.
- **Lokální diagnostika:** konzole agenta (5080) — stav whitelistu, WMI, fronty, blokovaných, poslední
  události, self-restart.
- **Logování:** agent loguje do **Windows Event Logu** (`ProviderName=USBGuardian`, Application);
  úroveň Warning+ (Information se do Event Logu nepromítá). Server loguje do Event Logu i konzole
  (`RoleTagFormatter`: `[KLIENT]` / `[SERVER]`).

> **Provozní poznámka:** při ladění/ověřování chování agenta je Event Log primárním zdrojem pravdy o
> tom, co služba reálně dělá (statická analýza nestačí — viz §18).

---

# ČÁST VI — Ověření a hodnocení

## 18. Testování a živé ověření

### 18.1 Metodika

Ověření probíhalo **end-to-end na pilotní stanici PC-01 (PC-01)** v reálné doméně, s důrazem na
**runtime evidenci** z Windows Event Logu (ne pouze statickou analýzu kódu). To se ukázalo jako
zásadní: některé chyby (falešný úspěch `Enable-PnpDevice` při ne-terminující chybě) nebyly viditelné
ze statického pohledu a projevily se až v běhu.

### 18.2 Ověřené scénáře (živě, z Event Logu)

| Scénář | Očekávání | Výsledek (Event Log) |
|--------|-----------|----------------------|
| Připojení neschváleného média (enforce ON) | Zablokovat + incident | `Neautorizované médium … → DEAKTIVOVÁNO → ZABLOKOVÁNO` ✅ |
| Vypnout blokování (break-glass) | Vrátit vše hned | `vracím 1 → Odblokování dokončeno: vráceno 1 z 1` ✅ |
| Zapnout blokování zpět | Znovu zablokovat připojené | `Re-enforcement … blokuji → DEAKTIVOVÁNO → znovu zablokováno 1` ✅ |
| Odebrat médium z whitelistu (server) | Zablokovat | Po stažení v7 + restartu: `znovu zablokováno 1` (Kingston 3.0) ✅ |
| Přidat médium na whitelist (server) | Povolit / vrátit | Reconcile `IsAllowedKey` → vráceno ✅ |
| Vypnout vynucování na serveru | Vrátit zablokovaná média | Po heartbeatu: Kingston `Status=OK`, `blocked.json` prázdný ✅ |
| Atribuce uživatele | `DOMÉNA\uživatel`, ne `HOST$` | Incidenty `DOMENA\it-admin` ✅ |
| Doručení incidentů | agent→API→DB→konzole | Přehled ukazuje incidenty z PC-01 ✅ |
| Commit stamp | footer = nasazený git | Patička `agent f2bb194` po redeployi ✅ |

### 18.3 Nalezené a opravené chyby (regrese/latentní)

Pro oponenturu je relevantní transparentnost o chybách nalezených během vývoje a jejich příčinách:

1. **DI fronty incidentů** — `IncidentsController` vyžadoval `IncidentQueue` neregistrovanou v DI →
   500 na `/api/incidents`. Oprava: registrace `IncidentQueue` + `IncidentQueueWorker`.
2. **`EphemeralKeySet` → `MachineKeySet`** — bez toho Schannel neudělal server TLS handshake pod gMSA.
3. **Trim sériového čísla** — WMI vrací sériák s koncovými mezerami → nesedělo s whitelistem. Trim při
   parsování i v konzoli.
4. **Falešný úspěch `Enable-PnpDevice`** — bez `-ErrorAction Stop` byla chyba ne-terminující, skript
   přesto hlásil `ENABLED` → médium zůstalo zablokované, ale odebráno ze seznamu. Oprava: přesný
   `-InstanceId` + `try/catch` + rozlišení `ENABLED`/`GONE`/`FAILED`.
5. **5min cache whitelistu neinvalidovaná po stažení** — nově schválené/odebrané médium se projevilo až
   po vypršení cache (až ~5 min, nebo po restartu). Oprava: `WhitelistChecker.Reload()` ve
   `WhitelistSync` po stažení.
6. **Re-blokace připojených médií** — agent blokoval jen na nové připojení; médium vrácené break-glassem
   po zapnutí blokování zpět zůstalo viditelné. Oprava: `ReEnforceConnectedDevices` (každý cyklus + na
   clear-override).
7. **Chybějící DELETE grant** — mazání z whitelistu padalo na „DELETE permission denied"; UI navíc
   skrývalo inner exception. Oprava: `GRANT DELETE ON WhitelistDevices` + rozbalení inner exception.

### 18.4 Limity ověření

Ověření proběhlo na **jedné pilotní stanici**, ne na flotile 500+. Škálovatelnost (O(1) match,
oddělené API, fronta) je **navržena**, ale **plně ověřena pod zátěží 500 agentů zatím nebyla** — to je
otevřený bod (viz §19). Automatizované testy (unit/integ) jsou omezené; těžiště ověření je na živém
end-to-end testu — což je vědomé a v dokumentu uvedené.

---

## 19. Omezení, rizika a známé slabiny

Tato kapitola je pro oponenturu klíčová — uvádí **vědomá** omezení, nikoli opomenutí.

### 19.1 Principiální omezení host-based přístupu

- **Lokální admin / SYSTEM útočník** může agenta vyřadit (zastavit službu i task). Watchdog a audit
  **zvyšují náklady** a zajistí viditelnost (zmlklý agent), ale **negarantují** odolnost proti lokálnímu
  adminovi. Platí pro jakýkoli host-based agent. Mitigace na úrovni organizace (omezit lokální admin).

### 19.2 Garantované pre-mount blokování (nejvýznamnější technické omezení)

- User-mode agent je **reaktivní**: blokuje hned na connect, ale Windows mountuje removable storage
  velmi rychle → existuje **krátké okno**, kdy se médium může objevit v Exploreru, než `Disable-PnpDevice`
  zabere. **Garantované** zabránění (médium se vůbec neobjeví) vyžaduje **GPO Device Installation
  Restrictions** nebo **kernel storage filter driver** — to je na roadmapě jako doplněk, ne náhrada.

### 19.3 Privátní podpisový klíč na serveru

- Vědomý trade-off (§6.7): klíč je na APP_SERVER (ACL) výměnou za automatizaci. Riziko kompromitace klíče =
  podvržení whitelistu; dopad omezen jen na integritu whitelistu (ne CA, ne code-signing). Mitigace:
  ACL/DPAPI, monitoring, případně budoucí HSM/odebrání práv.

### 19.4 Nešifrované HTTP :5050

- API zatím naslouchá i na HTTP :5050 (vedle HTTPS :5443). Pro NIS2 by mělo zůstat **jen HTTPS** —
  uzavření :5050 je na roadmapě.

### 19.5 Single points / topologie

- API zatím běží na SQL_SERVER (plánovaný přesun na APP_SERVER). DB je single instance (zálohování/HA mimo rozsah
  tohoto nástroje, řeší se infrastrukturně). Konzole a API jsou jednoinstanční (pro daný rozsah dostačující).

### 19.6 Aktualizace klientů

- Čistá automatizace updatu běžícího agenta zatím **chybí** (jen fresh install). `-ReinstallExisting`
  nezastavuje službu před kopií (zamčený exe). Návrh řešení viz §15.4 — je to **známá mezera**, ne
  opomenutí.

### 19.7 Per-serial blocklist

- Chybí explicitní **blocklist** konkrétního média s předností před whitelistem (např. zákaz známého
  škodlivého média i kdyby VID/PID odpovídalo schválenému). Na roadmapě.

### 19.8 HID / BadUSB

- Systém cílí na **storage-class**; nechrání proti médiu, které se tváří jako klávesnice/síťová karta.
  Mimo rozsah (řeší jiná opatření — např. blokace HID na GPO/EDR).

### 19.9 Škálování — neověřeno pod plnou zátěží

- Návrh počítá s 500+ (O(1) match, fronta, oddělené API), ale **zátěžový test na plné flotile zatím
  neproběhl**. Doporučeno před plošným nasazením.

### 19.10 Závislost na PowerShell pro block/unblock

- `Disable/Enable-PnpDevice` se volá přes `powershell.exe` (režie spuštění procesu). Pro řídké události
  (připojení média) přijatelné; při masivním re-enforce by se dalo optimalizovat (CIM přímo). Sledováno.

### 19.11 Souhrn rizik

| Riziko | Závažnost | Stav |
|--------|-----------|------|
| Okno před blokací (pre-mount) | Střední | Známé, mitigace GPO/driver na roadmapě |
| Lokální admin vyřadí agenta | Střední | Principiální, mitigace organizační |
| Klíč na serveru | Nízká–střední | Vědomý trade-off, ACL |
| HTTP :5050 otevřený | Nízká | Roadmapa (uzavřít) |
| Update flotily chybí | Střední (provozní) | Návrh hotov, implementace čeká |
| Škálování neověřeno | Střední | Zátěžový test doporučen |

---

## 20. Roadmapa

| Priorita | Položka | Stav |
|----------|---------|------|
| Vysoká | Per-serial blocklist (přednost před whitelistem) | 🔜 |
| Vysoká | Aktualizace klientů (update-safe fleet + verzové cílení) | návrh hotov |
| Vysoká | Garantované pre-mount blokování (GPO Device Installation Restrictions / kernel driver) | 🔜 |
| Střední | Uzavřít HTTP :5050 (jen HTTPS) | 🔜 |
| Střední | Přesun API na APP_SERVER („vše na serveru") | 🔜 |
| Střední | Monitoring expirace podpisového certu | 🔜 |
| Střední | Zátěžový test na plné flotile | 🔜 |
| Nízká | Hardening: dedikovaná `USB-Guardian-Admins`, HTTPS konzole | 🔜 |
| Nízká | Toast privilege separation (pipes SYSTEM→user) | 🔜 |

---

## 21. Závěr

USB Guardian je funkční, end-to-end ověřené technické opatření pro kontrolu výměnných médií, navržené
s ohledem na NIS2 / ISO 27001 a na reálná omezení produkčního prostředí. Jeho silné stránky jsou:

- **Centrální, kryptograficky zajištěný whitelist** (RSA-4096, fail-secure, 1:1 distribuce).
- **Reálné vynucování** s obousměrnou samohojící reconciliací (blokace, vracení, re-blokace,
  break-glass) — živě ověřené.
- **Auditovatelnost** s atribucí uživatele (NIS2).
- **Least-privilege a portabilita** (gMSA, granulární granty, žádné firemní hodnoty v kódu).
- **Ověřitelnost nasazení** (commit stamp napříč komponentami).

Známá omezení (pre-mount okno, klíč na serveru, chybějící fleet-update, neověřené škálování) jsou
**vědomá, dokumentovaná a opatřená plánem řešení**. Systém nepřekračuje nárok „dílčího technického
opatření" v rámci širšího ISMS.

Z pohledu oponentury je podstatné, že **každé zásadní rozhodnutí má doloženou alternativu a trade-off**
(§6, Příloha F) a že **chyby nalezené během vývoje jsou transparentně uvedeny** spolu s příčinou a
opravou (§18.3).

Následuje **ČÁST VII** s rozšiřujícími analýzami (anticipované otázky oponenta, srovnání s alternativami,
testovací katalog, kvantitativní škálování, provozní runbooky, detailní diagramy), které doplňují hlavní
argument a slouží jako rezerva pro hloubkovou diskusi při obhajobě.

---

# ČÁST VII — Rozšiřující analýzy a obhajoba

## 22. Anticipované otázky oponenta a odpovědi

Tato kapitola předjímá pravděpodobné otázky kritického oponenta a odpovídá na ně přímo. Je strukturována
tematicky.

### 22.1 Architektura a model

**Q1: Proč push model, když pull by dal serveru okamžitou kontrolu nad agentem?**
Pull předpokládá příchozí dostupnost stanic — ta v praxi (NAT, dynamické IP, firewally, notebooky mimo
síť) neexistuje pro 500+ strojů. Push vyžaduje jen odchozí HTTPS, funguje univerzálně. Cenou je latence
příkazů ≤ heartbeat (~2 min), což je pro politiku média irelevantní (médium není real-time hrozba ve
smyslu milisekund). Pro lokální okamžité akce (break-glass) má agent vlastní synchronní cestu.

**Q2: 2 minuty latence — není to bezpečnostní díra? Mezi vypnutím whitelist položky a blokací uplyne čas.**
Ano, serverové změny se propíší do ≤2 min. To je vědomé. Mitigace: (a) agent blokuje neschválené médium
**okamžitě na connect** bez ohledu na čerstvost serverové změny (whitelist je lokálně k dispozici);
(b) odebrání položky se na *nově připojené* médium projeví ihned (lokální whitelist), na *již připojené*
do jednoho reconcile cyklu; (c) pro skutečně okamžité globální vypnutí existuje lokální break-glass a
re-enforcement. Latence se týká jen *distribuce serverové změny*, ne reakce na médium jako takové.

**Q3: Proč agent jako SYSTEM a ne méně privilegovaně?**
Blokace zařízení (`Disable-PnpDevice`) i čtení napříč session vyžadují vysoká oprávnění; služba SYSTEM
je standardní model pro endpoint agenty. Riziko (kompromitace agenta = SYSTEM) je mitigováno tím, že
agent nepřijímá libovolné příkazy ze sítě — jen definovaný heartbeat protokol s ověřeným serverem.

**Q4: Blazor Server drží stav na serveru — co když spadne SignalR spojení / co škálování konzole?**
Konzole je administrátorský nástroj pro IT tým (jednotky souběžných uživatelů), ne veřejná aplikace.
Ztráta SignalR spojení znamená jen reload stránky. Ingestní zátěž (500 agentů) jde na **oddělené API**,
ne na konzoli — proto oddělení (NFR-4).

### 22.2 Kryptografie a integrita

**Q5: Privátní klíč na serveru je porušení „klíč nikdy na serveru". Jak to obhájíte?**
Je to vědomý trade-off (§6.7). Původní princip vede k ručnímu offline podpisu po každé změně katalogu,
což je provozně neúnosné → reálným důsledkem by byl neaktuální whitelist (větší riziko než ACL-chráněný
klíč). Klíč je **interní** (jen integrita whitelistu, ne CA/code-signing), takže dopad kompromitace je
ohraničený. Mitigace: ACL/DPAPI, monitoring, budoucí HSM. Je to klasická volba mezi teoretickou
bezpečností a provozní realitou — zvolili jsme provozně udržitelnou variantu s ohraničeným dopadem.

**Q6: Self-signed cert + pinning — co výměna certu, co rotace?**
Výměna certu = aktualizace `pinnedThumbprint` v konfiguraci agentů (součást nasazení). Pro uzavřený
systém agent↔API je to přijatelné; alternativou je CA validace (podporováno). Pinning naopak chrání před
MITM lépe než slepá důvěra v CA řetězec.

**Q7: RSA-4096 — proč ne ECC / podpis novějším schématem?**
RSA-4096/SHA-256 je konzervativní, široce podporované v .NET bez externích závislostí, s dostatečnou
bezpečnostní rezervou pro daný účel (podpis malého JSON blobu jednou za změnu). ECC by ušetřilo velikost
podpisu, ale to není úzké místo. Volba upřednostnila kompatibilitu a jednoduchost ověření.

**Q8: Co když agent dostane starší (validně podepsanou) verzi whitelistu — replay/rollback?**
Verze nese `version` (yyyy-MM-dd-vN) a `ValidUntil`. Agent stahuje na základě heartbeatu hlásícího
*aktuální* verzi serveru; server servíruje vždy aktivní verzi. Útok rollbackem by vyžadoval MITM
(eliminováno pinningem) nebo kompromitaci serveru. Tvrdší ochrana (monotónní verze vynucená agentem) je
možné vylepšení; aktuálně se spoléhá na to, že kanál je autentizovaný a pinovaný.

### 22.3 Vynucování a spolehlivost

**Q9: User-mode agent nezabrání připojení média dřív, než se objeví. To je zásadní slabina.**
Souhlas — je to nejvýznamnější technické omezení (§19.2) a uvádíme ho poctivě. Agent okno minimalizuje
(blok na `Win32_DiskDrive` connect, ne až na drive-letter), ale negarantuje pre-mount. **Garantované**
řešení je GPO Device Installation Restrictions nebo kernel filter driver — na roadmapě jako doplněk.
USB Guardian přidává *centrální whitelist, audit a vynucování* nad rámec toho, co GPO samo umí; obě
opatření se doplňují, ne nahrazují.

**Q10: Co se stane, když `Disable-PnpDevice` selže (např. médium se odpojí během blokace)?**
`BlockDevice` reportuje úspěch/neúspěch; při selhání se médium nezařadí jako blokované a událost se
loguje. Pro vracení (`UnblockDevice`) jsme zavedli rozlišení `ENABLED`/`GONE`/`FAILED` (§8.4) — odpojené
médium je `GONE` (uklidí se), skutečné selhání `FAILED` (retry). Tato robustnost vznikla po nálezu
falešného úspěchu (§18.3, bod 4).

**Q11: Break-glass = lokální admin vypne ochranu. Není to backdoor?**
Break-glass je **dočasný** (strop 72 h), **logovaný** (auditní incident kdo/kdy/délka, hlášeno na server),
a **automaticky zrušený** při příštím spojení se serverem (server = pravda). Je určen pro legitimní práci
offline. Je to řízená výjimka s plnou auditní stopou, ne tichý bypass. Lokální admin by ostatně mohl
agenta i zastavit (§19.1) — break-glass je *kontrolovanější* a auditovaná varianta.

**Q12: Co konzistence, když agent restartuje uprostřed blokace?**
Stav blokovaných (`blocked.json`) i override (`override.json`) jsou perzistované. Po startu proběhne
startovní sken + reconcile, takže se stav dorovná k serverové pravdě. Ověřeno (restart → re-blokace).

### 22.4 Provoz a nasazení

**Q13: Jak se aktualizují klienti na nové verze agenta?**
Aktuálně automatizovaná jen čerstvá instalace; update běžícího agenta je navržen (§15.4), ale ještě
neimplementován — je to **známá mezera**, ne opomenutí. Návrh reusuje gMSA pipeline (update-safe
reinstall + verzové cílení dle `AgentVersion`).

**Q14: Deploy přes SMB + sc.exe — není to křehké / bezpečné?**
Je to důsledek zavřeného WinRM v prostředí. Používá standardní Windows mechanismy (SCM přes named-pipes,
admin share) pod účtem s příslušnými právy (gMSA jen pro deploy). Auditováno (CSV), idempotentní
(přeskakuje offline/už-nainstalované). Alternativy (SCCM/Intune) jsou validní, pokud je prostředí má.

**Q15: Jak poznáte, že to, co běží, je opravdu poslední verze?**
Commit stamp (footer/`/api/version`/heartbeat) = git HEAD buildu, spolehlivě (regenerace `GitCommit.g.cs`).
Konzole ukazuje verzi agenta per stanice. Deploy se dělá jako poslední krok po commitu, živý hash se
reportuje k ověření.

### 22.5 Soulad a rozsah

**Q16: Tvrdíte soulad s NIS2?**
Ne — tvrdíme, že jsme **technické opatření podporující** soulad. Soulad je vlastnost celého ISMS, ne
jednoho nástroje (§3.4). Příloha E je *indikativní* mapování.

**Q17: Co GDPR — logujete uživatele a média?**
Logy obsahují hostname, uživatele a identifikaci média — provozní data nutná pro bezpečnostní účel
(oprávněný zájem / plnění povinnosti). Retence je řízená (mazání po `incidentDays`). Nasazení musí být
doprovázeno informováním zaměstnanců a záznamem o zpracování (organizační rovina).

### 22.6 Kvalita a ověření

**Q18: Kde jsou automatizované testy?**
Těžiště ověření je na **živém end-to-end testu** s runtime evidencí z Event Logu (§18). Automatizované
unit/integ testy jsou omezené — uvádíme to otevřeně jako prostor ke zlepšení. Pro daný typ chyb (WMI
timing, PnP chování, TLS pod gMSA) má živý test vyšší výpovědní hodnotu než mock.

**Q19: Jak víte, že to vydrží 500 agentů?**
Navrženo pro to (O(1) match, oddělené API, in-memory fronta, push). **Zátěžový test na plné flotile
zatím neproběhl** (§19.9) — doporučen před plošným nasazením. Kvantitativní odhad viz §25.

**Q20: Nejhorší scénář, který jste nepokryli?**
Kombinace lokálního admin útočníka + fyzický přístup + pre-mount okno. To je principiální hranice
host-based přístupu; mitigace jsou organizační (omezit lokální admin) a doplňková technická (GPO/driver).

**Q21: Proč vlastní řešení a ne komerční produkt?**
Viz §23. Stručně: kontrola nad chováním, žádné licenční náklady na 500+ stanic, plná integrace do
prostředí (AD, gMSA, doména), žádná závislost na cloudu/dodavateli, a přizpůsobení specifikům AXIMA
(AllSigned, klasifikátor). Komerční produkty jsou validní alternativa; volba byla vědomá.

**Q22: Co když konzole (APP_SERVER) nebo API (SQL_SERVER) vypadne?**
Agenti fungují **offline** — drží lokální podepsaný whitelist a poslední politiku, blokují/varují dál.
Heartbeat jen reportuje a přebírá změny; jeho výpadek znamená pouze, že se nové změny nedistribuují a
nesbírají incidenty (fronta na agentovi je perzistentní, dožene se po obnovení). Žádný výpadek serveru
neotevře ochranu — to je důsledek modelu „klient = kopie, funguje samostatně".

---

### 22.7 Hlubší technické otázky

**Q23: Co WMI jako zdroj událostí — není polling `WITHIN 1` neefektivní / nespolehlivý?**
`__InstanceCreationEvent ... WITHIN 1` je dotazovací interval 1 s — pro připojení média (řídká, lidská
událost) je to dostatečné a nezatěžující. Spolehlivost řeší **watchdog** (à 5 min ověří subscriptions a
re-registruje při selhání) a **startovní sken** (média připojená před startem). Alternativou by byl
`RegisterDeviceNotification` (Win32) — výkonnější, ale složitější; WMI bylo zvoleno pro jednoduchost a
dostatečnost.

**Q24: Sériové číslo z WMI není spolehlivé u všech zařízení (některá vrací prázdné / VID-založené).**
Pravda. Proto: (a) sériák se **trimuje** (WMI vrací koncové mezery); (b) při prázdném `SerialNumber` se
fallbackuje na extrakci z `PNPDeviceID`; (c) volitelný **wildcard** režim (`VID:PID` bez sériáku) je
default **vypnutý** s bezpečnostním varováním (méně specifické). Médium bez stabilního identifikátoru je
inherentně obtížné whitelistovat — to je vlastnost HW, ne nástroje.

**Q25: Dvě zařízení se stejným VID:PID:SN (klon/kolize)?**
Match je dle klíče; kolize sériáků jsou u kvalitních zařízení vzácné, ale teoreticky možné (levné
klony). Whitelist by je nerozlišil. Mitigace: per-serial blocklist (roadmapa) a fyzická kontrola; pro
většinu firemního parku (značková média) je riziko nízké.

**Q26: Proč incidenty přes JSON soubory na disku, ne přímo do paměti/streamu?**
Perzistence fronty (`queue/`) zajišťuje, že incident **nezmizí** při výpadku sítě/restartu — agent ho
doručí po obnovení. Soubor je jednoduchý, odolný a auditovatelný i lokálně. Po odeslání se přesune do
`sent/` s vlastní retencí.

**Q27: In-memory `IncidentQueue` na API — co když API spadne s plnou frontou?**
Riziko ztráty nezapsaných incidentů v okamžiku pádu API. Mitigace: agent dostane 202 až po zařazení;
pokud by se vyžadovala tvrdší garance, šlo by frontu perzistovat (trade-off latence/throughput).
Pro daný účel (řídké incidenty, agent má vlastní perzistentní frontu a retry) je in-memory přijatelné —
agent při nepotvrzení znovu odešle. **Pozn.:** agent maže z `queue/` až po úspěšném odeslání, takže
duplicitní doručení je možné, ztráta nikoli (na straně agenta).

**Q28: Idempotence příjmu incidentů — duplikáty?**
Agent může incident odeslat znovu (po timeoutu), takže duplikáty jsou možné. Pro audit je „raději dvakrát
než nikdy" přijatelné; deduplikace dle (hostname, timestamp, sériák, akce) je možné vylepšení.

**Q29: Proč PowerShell pro `Disable-PnpDevice` a ne přímé Win32/CIM volání z .NET?**
PowerShell cmdlet je nejjednodušší stabilní cesta k PnP operacím; přímé CIM `Win32_PnPEntity.Disable` /
SetupAPI je možné, ale složitější a chybovější. Pro řídkou událost je režie `powershell.exe` (~stovky ms)
zanedbatelná. Při masivním re-enforce by se dalo přejít na CIM (sledováno, §19.10).

**Q30: `Get-PnpDevice | Where -like '*...*'` — nemůže matchnout víc zařízení / špatné zařízení?**
Používáme nejdřív **přesný** `-InstanceId` (jedno zařízení); `-like` je jen fallback. Wildcard `*id*`
by teoreticky matchnul podřetězec, ale `InstanceId` médií jsou dostatečně specifické (VID/PID/sériák).
Riziko je nízké a fallback se uplatní jen když přesná shoda selže.

**Q31: Co se stane při změně písmene jednotky / reconnect téhož média?**
Identita je `PNPDeviceID` / VID:PID:SN, ne drive-letter — reconnect téhož média = stejný klíč, stejné
rozhodnutí. Drive-letter je jen doplňková informace do logu.

**Q32: Blazor Server + Windows Auth — jak řešíte autorizaci granularně?**
`WindowsPrincipal.IsInRole` (řeší doménové skupiny) proti `AdminGroups`, plus whitelist účtů
(`AllowedUsers` v appsettings = lockout-safe) **nebo** DB seznam z Nastavení. `DevAllowAll` je bypass jen
pro vývoj (v prod false). Pro SSO chodit přes hostname (ne IP).

**Q33: AccessCache — co když změním přístup a cache drží staré?**
Reload přes Nastavení → Údržba (a při restartu). Trade-off: cache šetří DB dotazy na každý request;
explicitní reload je přijatelný kompromis pro řídkou změnu přístupových práv.

**Q34: Jak zabráníte „lockoutu" z konzole (smažu si vlastní přístup)?**
`AllowedUsers`/`AdminGroups` v **appsettings** (mimo DB) fungují jako **bootstrap** — i kdyby se DB
seznam přístupů vyprázdnil, appsettings účet se dostane dovnitř. Záměrně.

**Q35: Heartbeat nese `enforce` — co když útočník odposlechne a podvrhne enforce=false?**
Kanál je TLS + pinning (MITM eliminován). Bez kompromitace serveru/klíče nelze podvrhnout odpověď.
Navíc agent při nedostupnosti serveru drží **poslední** politiku (nezmění se na „nechráněno" jen proto,
že server mlčí).

**Q36: `ReportNow` přes AppSettings — jak je to jednorázové?**
Konzole zapíše `cmd.report.<HOST>` = čas požadavku. Agent při heartbeatu dostane `ReportNow=true`, jen
pokud je požadavek novější než předchozí `LastSeen`; příští heartbeat má `LastSeen` už za časem požadavku
→ `ReportNow=false`. API jen **čte** AppSettings, nezapisuje stav agenta jinam.

**Q37: Proč konzole nemá DELETE na Incidents, ale API ano?**
Least-privilege. Mazání incidentů (retence) je citlivá operace; provádí ji **jediná** komponenta (API,
`RetentionService`) s úzce vymezeným právem. Konzole incidenty jen čte/agreguje — nemůže je mazat (ani
omylem, ani při kompromitaci).

**Q38: Co lokalizace / více jazyků?**
UI a dokumentace jsou CS + EN (README/HANDOFF dvojjazyčně). Hlášky agenta jsou CS (firemní prostředí).
Rozšíření je možné, není to bezpečnostní téma.

**Q39: Jak se chová systém při změně času / časových zónách?**
Časy se drží v **UTC** (override `until`, timestamps), zobrazení v lokálním čase. Tím se vyhneme chybám
při DST/zónách. Heartbeat nese `ServerTime` pro referenci.

**Q40: Co aktualizace .NET runtime / závislostí (zranitelnosti)?**
Buildy jsou **self-contained** — runtime je součástí balíčku, takže update runtime = redeploy nové verze
(viz §15.4). To je trade-off (větší balíček, vlastní odpovědnost za patchování runtime) za nezávislost na
přítomnosti .NET na stanici. Správa zranitelností runtime je součástí update procesu.

**Q41: Proč ne kontejnery / jiná distribuce serveru?**
Cílové prostředí je Windows Server + AD + gMSA; služby běží nativně jako Windows Services. Kontejnerizace
by přidala složitost bez zjevného přínosu pro daný rozsah. Self-contained publish + `sc.exe` je dostačující.

**Q42: Jak testujete regrese po změnách?**
Aktuálně živým end-to-end ověřením na pilotu + Event Log evidencí (§18). Slabší stránka je absence
rozsáhlých automatizovaných testů — uvedeno otevřeně (§19); doporučení: doplnit unit testy pro čistou
logiku (`PolicyState.EffectiveMode`, klíčování, reconcile rozhodování) a integrační test ingest cesty.

**Q43: Co když se whitelist přiblíží expiraci (`ValidUntil`)?**
Per-záznam i celá verze mají expiraci. Při expiraci celé verze jede agent v **degraded** módu dle
`onExpiredWhitelist` (warn/block/allow) s varováním. Roadmapa: aktivní monitoring blížící se expirace +
alert (analogicky k monitoringu podpisového certu).

**Q44: Jak se liší chování na notebooku mimo síť?**
Agent funguje offline (lokální whitelist + poslední politika). Break-glass umožní legitimní výjimku.
Po návratu do sítě heartbeat dorovná politiku a zruší override. Incidenty se doručí ze fronty.

**Q45: Jaký je dopad na uživatele / výkon stanice?**
Agent je lehký (WMI subscriber, řídké události). Žádné průběžné skenování souborů. Toast jen při události.
Blokace je událostní (na connect). Dopad na výkon stanice je zanedbatelný.

---

## 23. Srovnání s alternativními přístupy a produkty

### 23.1 Přístupové možnosti

| Přístup | Výhody | Nevýhody | Vztah k USB Guardian |
|---------|--------|----------|----------------------|
| **GPO Removable Storage Access** | Nativní, pre-mount (zabrání instalaci zařízení), zdarma | Bez centrálního whitelistu konkrétních médií, slabý audit s atribucí, nepružná správa per-médium | **Doplněk** — GPO pro tvrdou pre-mount vrstvu, USB Guardian pro whitelist+audit+vynucování |
| **Device Installation Restrictions (GPO)** | Pre-mount blok dle tříd/ID | Správa ID napříč flotilou nepružná, bez auditní stopy událostí | Doplněk (roadmapa §19.2) |
| **Komerční device control** (Endpoint Protector, Ivanti, ...) | Hotové, pre-mount, bohaté funkce | Licenční náklady (500+), cloud/dodavatel, integrace | Validní alternativa; vlastní řešení zvoleno pro kontrolu/náklady/integraci |
| **Plné DLP** | Klasifikace obsahu, ne jen médium | Drahé, komplexní nasazení | Jiná vrstva (obsah vs. médium) |
| **EDR/antivir** | Detekce malwaru | Reaguje až na následek, ne na připojení neschváleného média | Komplementární |
| **USB Guardian** | Centrální podepsaný whitelist, audit s atribucí, vynucování, AD integrace, bez licencí, portabilní | User-mode (pre-mount okno), vlastní údržba, host-based limity | — |

### 23.2 Pozice USB Guardian

USB Guardian **nenahrazuje** GPO/EDR/DLP — vyplňuje konkrétní mezeru: *centrálně spravovaný, kryptograficky
zajištěný whitelist konkrétních médií s plnou auditní stopou a atribucí uživatele, vynucováním a integrací
do AD/gMSA, bez licenčních nákladů a bez závislosti na cloudu*. Pro tvrdou pre-mount garanci se má
kombinovat s GPO Device Installation Restrictions (defense in depth).

### 23.3 Proč „build" a ne „buy" — rozhodovací kritéria

| Kritérium | Vlastní řešení | Komerční |
|-----------|----------------|----------|
| Náklady na 500+ stanic | Bez licencí | Roční licence/stanice |
| Kontrola chování | Plná | Omezená |
| Integrace (AD, gMSA, AllSigned) | Na míru | Závisí na produktu |
| Závislost na dodavateli/cloudu | Žádná | Často ano |
| Pre-mount garance | Ne (roadmapa) | Často ano |
| Údržba/odpovědnost | Interní | Dodavatel |
| Auditní stopa na míru NIS2 | Plně přizpůsobeno | Závisí |

Závěr: pro AXIMA převážily kontrola, náklady a integrace; pre-mount mezera se uzavře kombinací s GPO.

---

## 24. Detailní testovací katalog

Strukturovaný přehled testovacích případů. Stav „✅ ověřeno živě" = potvrzeno na PC-01 z Event Logu;
„⏳" = navržené/doporučené, dosud neprovedené systematicky.

### 24.1 Detekce a identifikace

| TC | Scénář | Očekávaný výsledek | Stav |
|----|--------|--------------------|------|
| TC-01 | Připojení USB flash při běžícím agentu | Incident s VID/PID/sériák, akce dle politiky | ✅ |
| TC-02 | Médium připojené před startem agenta | Startovní sken ho vyhodnotí | ✅ |
| TC-03 | Sériák s koncovými mezerami | Trim → match s whitelistem | ✅ |
| TC-04 | Odpojení média | `DisconnectedAt` doplněn | ✅ |
| TC-05 | SD karta | Detekce (InterfaceType SD) | ⏳ |
| TC-06 | Rychlé připojení/odpojení (race) | Žádný crash, korektní párování/timeout | ⏳ |

### 24.2 Whitelist a podpis

| TC | Scénář | Očekávaný výsledek | Stav |
|----|--------|--------------------|------|
| TC-10 | Schválené médium | `Allowed`, médium funguje | ✅ |
| TC-11 | Neschválené médium (enforce) | `Blocked` | ✅ |
| TC-12 | Podvržený `whitelist.json` (změna bez podpisu) | Odmítnut (fail-secure) | ⏳ |
| TC-13 | Chybějící `.sig` | Whitelist neuložen, jede stará verze | ✅ (návrhem) |
| TC-14 | Nová verze whitelistu | Stažení ≤2 min, Reload, platí ihned | ✅ |
| TC-15 | Expirovaná verze | Degraded mód dle `onExpired` | ⏳ |
| TC-16 | 10k záznamů | O(1) match, bez degradace | ⏳ (zátěž) |

### 24.3 Vynucování a reconciliace

| TC | Scénář | Očekávaný výsledek | Stav |
|----|--------|--------------------|------|
| TC-20 | Vypnout blokování (break-glass) | Vrátit vše hned | ✅ |
| TC-21 | Zapnout blokování zpět | Re-blokace připojených | ✅ |
| TC-22 | Vypnout enforce na serveru | Vrátit do ≤ heartbeat | ✅ |
| TC-23 | Přidat médium na whitelist za běhu | Vráceno (i při enforce) | ✅ |
| TC-24 | Odebrat médium z whitelistu | Zablokováno | ✅ |
| TC-25 | Restart agenta s aktivní blokací | Stav dorovnán (perzistence + reconcile) | ✅ |
| TC-26 | Break-glass expirace (timeout) | Override zrušen, blokace obnovena | ⏳ |
| TC-27 | `UnblockDevice` na odpojené médium | `GONE`, úklid ze seznamu | ✅ (návrhem/logem) |

### 24.4 Komunikace a odolnost

| TC | Scénář | Očekávaný výsledek | Stav |
|----|--------|--------------------|------|
| TC-30 | TLS handshake agent↔API (gMSA) | OK (MachineKeySet) | ✅ |
| TC-31 | MITM / špatný otisk | Spojení odmítnuto (pinning) | ⏳ |
| TC-32 | API nedostupné | Agent jede offline, fronta se hromadí | ✅ (fronta 21 obs.) |
| TC-33 | Obnova API | Fronta se doručí | ⏳ |
| TC-34 | Nápor incidentů | 202 + fronta, bez pádu | ⏳ (zátěž) |
| TC-35 | ReportNow | Flush fronty ≤ heartbeat | ✅ |

### 24.5 Nasazení a verze

| TC | Scénář | Očekávaný výsledek | Stav |
|----|--------|--------------------|------|
| TC-40 | Fresh install (fleet) | Služba běží, heartbeat+incidenty | ✅ (PC-01) |
| TC-41 | Reinstall/update běžícího agenta | Update-safe (stop→copy→start) | ⏳ (mezera §19.6) |
| TC-42 | Commit stamp | Footer = git HEAD | ✅ |
| TC-43 | Watchdog nahodí zastavenou službu | Služba restartována | ⏳ |
| TC-44 | Auto-enrollment (dry-run → ostrý) | Cíle zapsány, instalace přes gMSA | ✅ (PC-01) |

### 24.6 Konzole a DB

| TC | Scénář | Očekávaný výsledek | Stav |
|----|--------|--------------------|------|
| TC-50 | Přidat médium do whitelistu | INSERT + auto-publish | ✅ |
| TC-51 | Smazat médium (✕) | DELETE (s grantem) + auto-publish | ✅ |
| TC-52 | Smazat bez DELETE grantu | Chyba s rozbalenou inner exception | ✅ |
| TC-53 | Toggle Aktivní | UPDATE + auto-publish | ✅ |
| TC-54 | Export CSV / manažerský report | Soubor dědí filtr | ⏳ |
| TC-55 | AD sync | Upsert Computers, reconciliation | ✅ |
| TC-56 | Retence | Mazání starých incidentů (API) | ⏳ |

---

## 25. Výkon a škálování (kvantitativní analýza)

### 25.1 Zátěž heartbeatu

- **Předpoklad:** 500 agentů, heartbeat à 2 min.
- **Frekvence:** 500 / 120 s ≈ **4,2 req/s** v průměru. Heartbeat je lehký GET (čte `AppSettings`,
  porovná verzi) → milisekundy. I s nárazem (synchronizace startů) jde o desítky req/s — pro Kestrel
  triviální.
- **Závěr:** heartbeat není úzké místo ani při 2000 agentech (~17 req/s).

### 25.2 Zátěž incidentů

- **Předpoklad:** incidenty vznikají řídce (připojení média) — řádově jednotky/stanici/den. I při
  „špatném dni" (1000 incidentů napříč flotilou za hodinu) je to ~0,3 req/s.
- **Odolnost vůči náporu:** příjem je oddělen od zápisu (202 + in-memory fronta + worker), takže ani
  krátký špičkový nápor neblokuje na DB latenci. Fronta tlumí špičky; worker zapisuje vlastním tempem.
- **Závěr:** ingestní cesta je dimenzována s velkou rezervou.

### 25.3 Match whitelistu na agentovi

- **Algoritmus:** `Dictionary<string, WhitelistEntry>` (VID:PID:SERIAL), lookup **O(1)**.
- **10 000 záznamů:** paměť ~jednotky MB, lookup konstantní. Načtení/index se děje jen při změně verze
  (ne na každé připojení — cache + Reload).
- **Závěr:** match škáluje na velké whitelisty bez degradace; není to úzké místo.

### 25.4 Distribuce whitelistu

- **Blob:** ~stovky bajtů na záznam; 10k záznamů ≈ jednotky MB JSON. Stahuje se jen při změně verze
  (heartbeat hlásí `WhitelistUpdateAvailable`), ne periodicky.
- **Síť:** i při hromadné změně se 500 agentů stáhne blob rozprostřeně přes ~2min okno → zanedbatelné.

### 25.5 Databáze — růst

- **Incidenty:** dominantní tabulka. Při ~5 incidentech/stanici/den × 500 = 2 500/den ≈ ~900k/rok.
  Při ~1 KB/řádek ≈ stovky MB/rok — pro SQL Server triviální. **Retence** (default 365 dní) drží objem
  ohraničený.
- **Computers/Whitelist:** stovky–tisíce řádků, zanedbatelné.

### 25.6 Konzole

- **Souběh:** jednotky uživatelů (IT). Dotazy nad incidenty používají filtr + `Take(200/50000)` →
  ohraničené. Kumulace je in-memory nad omezeným výběrem.
- **Závěr:** konzole není výkonnostně kritická při daném počtu administrátorů.

### 25.7 Limity analýzy

Výše uvedené jsou **odhady z návrhu**, ne výsledky zátěžového testu. Doporučení: před plošným nasazením
provést syntetický zátěžový test (500 simulovaných agentů, heartbeat + nárazové incidenty) a změřit
latenci API, hloubku fronty a zápisový throughput workeru (§19.9).

---

## 26. Provozní runbooky

### 26.1 Nasazení nové verze konzole (APP_SERVER)

1. `git commit` (deploy = poslední krok po commitu).
2. `dotnet publish ... -o D:\deploy\USBGuardianConsole`.
3. `sc.exe \\APP_SERVER_IP stop USBGuardianConsole`; počkat na `STOPPED`.
4. `robocopy ... \\APP_SERVER_IP\C$\Apps\USBGuardianConsole /E /XF appsettings.local.json`.
5. `sc.exe \\APP_SERVER_IP start USBGuardianConsole`.
6. Ověřit footer = živý commit.

### 26.2 Nasazení nové verze API (SQL_SERVER) — spouští operátor

1. Build staged na APP_SERVER (`C:\Apps\USBGuardianApiPublish`).
2. `sc stop "USB Guardian API"`; **počkat na STOPPED** (jinak je exe zamčený → robocopy FAILED).
3. `robocopy` na SQL_SERVER `C:\USBGuardian.Api` (s `/XF appsettings.local.json`).
4. `sc start`; ověřit `:5050/api/version`.

### 26.3 Redeploy agenta na stanici (ruční, UAC)

```powershell
$src='D:\deploy\USBGuardianAgent'; $dst='C:\Program Files\USBGuardian'
Stop-Service 'USB Guardian' -Force
while ((Get-Service 'USB Guardian').Status -ne 'Stopped'){ Start-Sleep -Milliseconds 500 }
robocopy $src $dst /E /XF agent.config.local.json /NFL /NDL /NJH /NJS
Start-Service 'USB Guardian'
```
Ověřit patičku lokální konzole (`agent <commit>`) a Event Log.

### 26.4 Diagnostika „médium se nezablokovalo"

1. Lokální konzole `127.0.0.1:5080` → karta Vynucování (BLOKUJE? Zablokováno teď?).
2. Event Log (`ProviderName=USBGuardian`): hledat `DEAKTIVOVÁNO` / `Re-enforcement` / `Nelze povolit`.
3. `whitelist.json` na disku — verze a počet zařízení (= co agent reálně má).
4. `blocked.json` — co agent drží zablokované.
5. `Get-PnpDevice` — Status `Error` = disabled.
6. Pokud agent má starou verzi whitelistu (cache) → ověřit, že běží build s `Reload()` (cache invalidace).

### 26.5 Diagnostika „incidenty netečou"

1. Lokální konzole → fronta (počet záznamů).
2. Event Log → chyby `IncidentSync` (HTTPS/pinning/auth).
3. Ověřit dostupnost API (`:5443`), platnost pinu (`/api/cert-info`).
4. Heartbeat OK? (LastSeen v konzoli). „Vyžádat data" (ReportNow) → flush.

### 26.6 Incident response — podezření na tamper

1. Konzole → dlaždice „Zmlklo agentů" (LastSeen > práh) = možný výpadek/tamper.
2. Ověřit běh služby + watchdog tasku na stanici.
3. Zkontrolovat auditní incidenty `OverrideDisabled` (neoprávněný break-glass?).
4. Případně vzdálený restart služby / redeploy.

### 26.7 Obnova privátního klíče / rotace

1. Vygenerovat nový pár (`tools/WhitelistSigner`).
2. Distribuovat nový `whitelist_public.pem` na agenty (součást balíčku/configu).
3. Nastavit `Whitelist:PrivateKeyPath` na APP_SERVER, re-publikovat whitelist (podepíše novým klíčem).
4. Ověřit, že agenti přijmou novou podepsanou verzi.

---

## 27. Detailní diagramy

### 27.1 Stavový diagram efektivního režimu (`PolicyState`)

```
                 ┌─────────────────────────── lokální default (před 1. heartbeatem)
                 │
   start ───────►│  EffectiveMode = localMode (warn/block)
                 │
  heartbeat ─────┼──► serverReceived = true
                 │        │
                 │        ├─ enforce=true  → block
                 │        └─ enforce=false → warn
                 │
  break-glass ───┼──► override aktivní → warn  (bez ohledu na server)
   (5080)        │        │ (strop 72 h, perzistováno)
                 │        ▼
  heartbeat ─────┴──► OnServerHeartbeat: override ZRUŠEN → zpět na serverové enforce
```

### 27.2 Sekvence — připojení neschváleného média (enforce ON)

```
USB        DeviceMonitor   WhitelistChecker  PolicyEnforcer  DeviceBlocker  IncidentLogger  API
 │  connect    │                 │                │              │              │            │
 ├────────────►│ parse VID:PID:SN│                │              │              │            │
 │             ├────────────────►│ index O(1)     │              │              │            │
 │             │   not allowed   │                │              │              │            │
 │             ├─────────────────┴───────────────►│ effective=block            │            │
 │             │                                  ├─────────────►│ Disable-PnpDevice         │
 │             │                                  │              │ track blocked.json        │
 │             │                                  ├──── Toast frontu (ToastHelper) ──────────│
 │             │                                  ├─────────────────────────────►│ queue     │
 │             │                                  │              │              │ IncidentSync─►│ 202→fronta→DB
```

### 27.3 Sekvence — distribuce whitelistu (1:1)

```
Admin    Konzole(WhitelistPublisher)   DB         API        Agent(WhitelistSync)  WhitelistChecker
 │ změna   │                            │          │              │                     │
 ├────────►│ snapshot+podpis(RSA)       │          │              │                     │
 │         ├───────────────────────────►│ Json+Sig (aktivovat)    │                     │
 │         │                            │          │              │ heartbeat           │
 │         │                            │          │◄─────────────┤ (verze)             │
 │         │                            │          ├─ UpdateAvailable────────►│          │
 │         │                            │          │ GET /whitelist(+sig)     │          │
 │         │                            │          ├─────────────►│ ověř(fail-secure)    │
 │         │                            │          │              ├ uložit + Reload ────►│ RebuildIndex O(1)
```

### 27.4 Komponentový diagram nasazení

```
        Active Directory  ◄── LDAP ── Konzole(APP_SERVER, Blazor :4200) ── SQL ──►  SQL_SERVER
                                          │  (APP_SERVER$)                    DB USBGuardian
                                          │  WhitelistPublisher (priv. klíč)        ▲
                                          │  AgentDeployService                     │ SQL (gMSA gmsa-api$)
                                          ▼                                          │
                          gMSA task (gmsa-deploy$) ── SMB+sc ──► Klienti     API(.SQL_SERVER :5443) ── do DB
                                                                     │  ▲
                                                  push HTTPS :5443   │  │ heartbeat/whitelist/enforce
                                                                     ▼  │
                                                            Agent (SYSTEM) ── lokální konzole :5080
```

---




## 28. Detailní legislativní a normativní analýza

### 28.1 NIS2 (směrnice EU 2022/2555) — rozbor relevantních povinností

Směrnice NIS2 v **čl. 21 odst. 2** vyjmenovává minimální opatření k řízení kybernetických rizik.
Následující tabulka rozebírá, jak USB Guardian přispívá k jednotlivým bodům (výklad je indikativní;
plný soulad je vlastností ISMS, §3.4):

| Bod čl. 21(2) (oblast) | Co požaduje | Příspěvek USB Guardian | Doplňující opatření (mimo nástroj) |
|------------------------|-------------|------------------------|-------------------------------------|
| a) analýza rizik a politiky | Posuzovat rizika, mít politiky | Data pro analýzu rizik médií (evidence, incidenty) | Směrnice o médiích, metodika |
| b) zvládání incidentů | Detekce, hlášení, reakce | Detekce neschválených médií, near-real-time hlášení, alerty, audit | Proces IR, eskalace |
| c) kontinuita / zálohování | BCM | Nepřímo (agenti fungují offline; výpadek serveru neotevře ochranu) | Zálohování DB, HA |
| d) dodavatelský řetězec | — | Mimo rozsah | — |
| e) bezpečný vývoj, zranitelnosti | Bezpečný vývoj/údržba | Verzování, commit stamp, transparentní rozbor chyb (§18.3) | Správa zranitelností |
| f) hodnocení účinnosti opatření | Měřit účinnost | Audit + dohled „zmlklých" agentů = měřitelnost pokrytí | Metriky, audit |
| g) kybernetická hygiena, školení | — | Vynucování whitelistu jako technická podpora hygieny | Školení uživatelů |
| h) kryptografie a šifrování | Použití kryptografie | TLS přenos, RSA-4096 integrita whitelistu | Politika kryptografie |
| i) řízení přístupu a aktiv | Kontrola přístupu, evidence | Whitelist (přístup k médiím), evidence médií i stanic | Politika přístupu, klasifikace |
| j) MFA / zabezpečená komunikace | Zabezpečená komunikace | TLS+pinning, Kerberos agent↔API | MFA pro konzoli (organizačně) |

**Závěr k NIS2:** USB Guardian přímo přispívá zejména k bodům **b, f, h, i** a podpůrně k **a, e, g, j**.

### 28.2 Zákon č. 181/2014 Sb. a vyhláška o bezpečnostních opatřeních (VKB)

USB Guardian je **technické opatření** přispívající zejména k:

| Oblast (VKB, indikativně) | Příspěvek |
|---------------------------|-----------|
| Řízení aktiv | Evidence médií a stanic (inventář z AD) |
| Řízení přístupu | Whitelist + vynucování — jen schválená média mají přístup |
| Ochrana před škodlivým kódem | Prevence vnesení malwaru přes neschválené médium |
| Detekce událostí | Detekce připojení, incidenty |
| Zaznamenávání událostí | Auditní stopa s atribucí, centrální agregace, retence |
| Řízení změn | Verzování whitelistu + commit stamp komponent |
| Fyzická bezpečnost | Kontrola přenosných paměťových zařízení |
| Kryptografické prostředky | TLS, RSA-4096 podpis |

### 28.3 ISO/IEC 27002:2022 — detail kontrol

| Kontrola | Název | Příspěvek |
|----------|-------|-----------|
| 5.9 | Inventura aktiv | Evidence médií + inventář stanic |
| 5.10 | Přijatelné používání aktiv | Vynucování whitelistu |
| 7.10 | Paměťová média | Jádro řešení |
| 8.7 | Ochrana před malwarem | Prevence vnesení přes médium |
| 8.15 | Protokolování | Incidenty s atribucí |
| 8.16 | Monitorovací činnosti | Centrální dohled, anomálie (zmlklí agenti) |
| 8.20 | Bezpečnost sítí | Zabezpečená komunikace agent↔API |
| 8.24 | Použití kryptografie | TLS + RSA-4096 |

### 28.4 GDPR / ochrana osobních údajů

Systém zpracovává **provozní osobní údaje** (uživatel, hostname, čas) za účelem bezpečnosti informací.
Doporučené organizační kroky: právní základ (oprávněný zájem / plnění právní povinnosti dle NIS2),
informování zaměstnanců, záznam o činnostech zpracování, řízená retence (`retention.incidentDays`),
minimalizace (logujeme jen nezbytné). Retenci systém technicky vynucuje (API) — podporuje zásadu
omezení uložení.

---

## 29. Referenční přehled tříd a odpovědností

### 29.1 Agent

| Třída | Odpovědnost | Klíčové metody / artefakty |
|-------|-------------|----------------------------|
| `DeviceMonitor` | Detekce médií (WMI), párování, startovní sken, re-enforcement | `OnDiskConnected`, `ScanConnectedDevices`, `ReEnforceConnectedDevices` |
| `WhitelistChecker` | Ověření vůči whitelistu, podpis, index, cache | `IsAllowed`, `IsAllowedKey`, `Reload`, `RebuildIndex` |
| `SignatureVerifier` | Ověření RSA-4096 podpisu (fail-secure) | `Verify` |
| `PolicyEnforcer` | Rozhodnutí o akci (warn/block/allowed) | `HandleDevice`, `DetermineAction` |
| `DeviceBlocker` | Blokace/vracení, perzistence | `BlockDevice`, `UnblockDevice`, `UnblockAll`, `blocked.json` |
| `PolicyState` | Stav vynucování (server enforce + break-glass) | `OnServerHeartbeat`, `EffectiveMode`, `SetOverride` |
| `SessionUser` | Atribuce reálného uživatele (WTS) | `GetActiveConsoleUser` |
| `IncidentLogger` | Fronta incidentů, retence sent | `LogConnection`, `UpdateDisconnectedAt` |
| `WhitelistSync` | Heartbeat, stahování whitelistu, reconcile | `TrySyncWhitelist`, `DownloadAndSaveWhitelist`, `ReconcileBlocked` |
| `IncidentSync` | Odesílání fronty na API | `ExecuteAsync` (jitter, ReportNow) |
| `NotificationService` | Toast fronta (user session via ToastHelper) | `ShowWarningForDevice` |
| `LocalConsoleService` | Lokální admin konzole (loopback) | `/api/status`, `/api/override`, `/api/unblock-all`, `/api/restart` |
| `TlsClient` | HTTP klient s pinningem | `Create` |

### 29.2 API

| Třída | Odpovědnost |
|-------|-------------|
| `IncidentsController` | Příjem incidentů (202 → fronta), výpis pro konzoli |
| `WhitelistController` | Servírování podepsaného blobu + podpisu (verbatim) |
| `HeartbeatController` | Stav, verze, `Enforce`, `ReportNow` |
| `IncidentQueue` / `IncidentQueueWorker` | Fronta + asynchronní zápis do DB |
| `SelfCert` | Self-signed TLS cert (MachineKeySet) |
| `RetentionService` | Mazání starých incidentů (jediný s DELETE na Incidents) |
| `AppDbContext` | EF Core kontext (sdílený s konzolí) |

### 29.3 Konzole

| Třída / stránka | Odpovědnost |
|-----------------|-------------|
| `Home` (Přehled) | Agregace, filtr, kumulace, „Schváleno", export |
| `Computers` (Stanice) | AD inventář, dohled, ReportNow, řízení nasazení |
| `Whitelist` | Správa katalogu, auto-publish, soft/hard delete, `Detail(ex)` |
| `Settings` / `Database` | Centrální nastavení / read-only přehled DB |
| `AdSyncRunner` / `AdSyncService` | AD sync + reconciliation |
| `WhitelistPublisher` | Snapshot + podpis + aktivace verze |
| `AgentDeployService` | Auto-enrollment orchestrátor |
| `ExportEndpoints` | CSV + manažerský report |
| `IncidentAlertService` / `EmailSender` | Alerty e-mailem |
| `AccessCache` | Cache přístupových práv (reload z Údržby) |

---

## 30. Detailní rozbor klíčových algoritmů a kódu

Tato kapitola rozebírá netriviální algoritmy do hloubky — pro oponenta, který chce ověřit korektnost
implementace, ne jen popis.

### 30.1 Párování WMI událostí (timing fix)

**Problém:** Při připojení média přijdou dvě nezávislé WMI události — `Win32_DiskDrive` (fyzický disk)
a `Win32_LogicalDisk` (drive-letter) — v **nedeterministickém pořadí** a s prodlevou. Naivní řešení
(čekat na obě) by zdrželo blokaci.

**Řešení:** dvě „pending" mapy klíčované `DiskIndex` + okamžité vyhodnocení na disk-connect:

```
OnDiskConnected(wmi):
    if not IsRemovableMedia(wmi): return
    device = ParseDeviceFromWmi(wmi)          # VID:PID:SN (serial TRIM), PnpDeviceId
    diskIndex = ExtractDiskIndex(DeviceID)
    if _pendingDriveLetters.TryRemove(diskIndex, out drive):   # scénář B: letter přišel dřív
        device.DriveLetters.Add(drive)
    ProcessDevice(device)                      # ENFORCEMENT HNED, nečeká na letter

OnLogicalDiskConnected(wmi):
    diskIndex = GetDiskIndexForLogicalDisk(DeviceID)
    if _pendingDevices.TryRemove(diskIndex, out pending):      # scénář A: disk čekal
        pending.Device.DriveLetters.Add(letter); ProcessDevice(pending.Device)
    else:
        _pendingDriveLetters[diskIndex] = (letter, now)        # počkej na disk (timeout 30 s)
```

**Klíčové rozhodnutí:** enforcement se spouští v `OnDiskConnected` **bez čekání** na drive-letter →
minimalizace okna namountování. Drive-letter se jen doplní do logu, pokud dorazí. Timeout 30 s brání
hromadění „osiřelých" pending záznamů.

**Hraniční případy:** velmi rychlé připojení/odpojení (race) — pending mapy jsou `ConcurrentDictionary`,
`TryRemove` je atomické; osiřelý záznam vyprší. Médium bez drive-letteru (nenamountovatelné) se přesto
vyhodnotí (blokace funguje na úrovni PnP, ne FS).

### 30.2 Reconciliace stavu vynucování (`ReconcileBlocked`)

Volá se po každém sync cyklu. Logika (zjednodušeně):

```
blocking = PolicyState.EffectiveMode("warn") == "block"

# 1) Re-blokace připojených (jen když blokujeme) – idempotentní
if blocking:
    DeviceMonitor.ReEnforceConnectedDevices()

# 2) Vracení dříve blokovaných
blocked = DeviceBlocker.GetBlocked()        # PnpId -> klíč VID:PID:SN
if blocked.Count == 0: return
for (pnpId, key) in blocked:
    if (not blocking) or WhitelistChecker.IsAllowedKey(key):
        DeviceBlocker.UnblockDevice(pnpId)
```

**Invarianty:**
- *Vypnuté blokování* → vrátí **vše**, co agent zakázal.
- *Zapnuté blokování* → vrátí jen ta, co jsou **mezitím schválená** (`IsAllowedKey`).
- Idempotence: opakované volání v ustáleném stavu nic nemění (re-enforce přeskakuje
  schválená i už-blokovaná; unblock se volá jen na splněnou podmínku).

**Pořadí (subtilní, ale korektní):** re-enforce běží **před** unblock smyčkou. Pro médium mezitím
schválené a stále připojené: re-enforce zkontroluje `IsAllowed(device)` = true → **přeskočí** (nezablokuje),
následná unblock smyčka ho `IsAllowedKey` = true → **vrátí**. Tím nedojde ke konfliktu „zablokuj a hned
odblokuj".

### 30.3 Spolehlivé vracení (`UnblockDevice`)

**Problém (nalezený bug):** naivní `Enable-PnpDevice` bez `-ErrorAction Stop` → ne-terminující chyba →
skript přesto vypíše `ENABLED` → **falešný úspěch** → médium zůstane zakázané, ale agent ho odebere ze
seznamu (a už nezkusí).

**Řešení:** přesný `-InstanceId` (jako ruční příkaz) + fallback `-like`, `try/catch` s `-ErrorAction Stop`,
tři výsledky:

```
$dev = Get-PnpDevice -InstanceId '<exact>'              # přesná shoda
if (-not $dev) { $dev = Get-PnpDevice | ? InstanceId -like '*<escaped>*' }   # fallback
if ($dev) {
    try { Enable-PnpDevice -InstanceId $dev.InstanceId -Confirm:$false -ErrorAction Stop; 'ENABLED' }
    catch { 'FAILED:' + $_.Exception.Message }
} else { 'GONE' }
```

| Výsledek | Význam | Akce agenta |
|----------|--------|-------------|
| `ENABLED` | Povoleno | Untrack (odebrat z `blocked.json`) |
| `GONE` | Médium už není v systému (odpojeno) | Untrack (vyřešeno; další plug se vyhodnotí znovu) |
| `FAILED:<chyba>` | Skutečné selhání Enable | **Ponechat** v seznamu → příští reconcile retry; zalogovat příčinu |

**Escapování:** pro `-InstanceId` přesnou shodu escapujeme jen apostrof; pro `-like` i `&` (`` `& ``).
Ověřeno, že `-like` matchne reálné `InstanceId` s `&`.

### 30.4 Cache whitelistu a invalidace (`Reload`)

**Problém (nalezený bug):** 5min cache + stažení nové verze bez invalidace → nově schválené/odebrané
médium se projeví až po vypršení cache (a `ReEnforce` mezitím čte stará data).

**Řešení:** `WhitelistSync.DownloadAndSaveWhitelist` po atomickém zápisu souborů volá
`WhitelistChecker.Reload()` (zahodí cache; `_lastLoaded = MinValue`). Pořadí ve smyčce:

```
TrySyncWhitelist()         # heartbeat → pokud nová verze: stáhnout, ověřit, uložit, Reload()
ReconcileBlocked()         # IsAllowedKey → LoadWhitelist (cache=MinValue) → čerstvý index
```

→ reconcile v **témž cyklu** vidí novou verzi. Cache zůstává jako optimalizace pro časté dotazy na
connect (mezi stahováními se nemění), ale po stažení je vždy čerstvá.

### 30.5 Efektivní režim (`PolicyState.EffectiveMode`)

```
EffectiveMode(localMode):
    if OverrideActive:        return "warn"            # break-glass má přednost (offline práce)
    if serverReceived:        return enforce ? "block" : "warn"   # server = pravda
    return localMode                                    # před 1. heartbeatem: lokální config
```

**OnServerHeartbeat(enforce):** nastaví `serverEnforce`/`serverReceived` a **zruší** případný override
(server reasertuje politiku). Override je perzistovaný (`override.json`) se stropem 72 h. Tím je
zajištěno: lokální výjimka je dočasná a vždy ustoupí serveru při spojení.

### 30.6 Bajtová přesnost podpisu

Kritická invarianta: **týž string** se podepisuje, servíruje i ověřuje.

```
publish:  blob = CanonicalJson(activeDevices)          # UTF-8, bez BOM, stabilní pořadí
          sig  = RSA_SHA256_Sign(blob, privateKey)
          DB: Json = blob (NVARCHAR(MAX)), Signature = base64(sig)
serve:    GET /api/whitelist  → vrátí Json VERBATIM (žádná re-serializace)
          GET /api/whitelist/signature → base64(sig)
verify:   ok = RSA_SHA256_Verify(downloadedBlob, decode(sig), publicKey)   # fail-secure
```

Jakákoli re-serializace (jiné pořadí klíčů, mezery, BOM) by podpis rozbila — proto **verbatim** přenos
a uložení `NVARCHAR(MAX)` (ne strukturovaně).

---

## 31. Útočné scénáře (attack trees)

Detailní rozbor vybraných útoků krok za krokem, s vyznačením, kde a jak je systém přeruší.

### 31.1 Cíl útočníka: vynést data na neschválené médium

```
Vynést data na USB
├── Připojit neschválené USB
│   ├── Agent enforce=block → Disable-PnpDevice → médium nepoužitelné            [BLOK]
│   │     └── (zbytkové okno před mountem — §19.2; mitigace GPO/driver)         [ČÁST. RIZIKO]
│   ├── Agent enforce=warn → médium funguje, ale incident s atribucí            [AUDIT/DETEKCE]
│   └── Agent nainstalován? → konzole „chybí agent" / „zmlklo"                   [VIDITELNOST]
├── Podvrhnout médium na whitelist (přidat svoje VID:PID:SN)
│   ├── Bez přístupu do konzole (AD skupina/whitelist) → nelze                   [AUTORIZACE]
│   └── Přímo do DB → nemá podpis priv. klíčem → agent odmítne (fail-secure)     [INTEGRITA]
├── Podvrhnout lokální whitelist.json na stanici
│   └── Podpis nesedí (nemá priv. klíč) → odmítnuto                              [INTEGRITA]
├── Vypnout agenta (lokální admin)
│   ├── Stop služby → watchdog (3 min) nahodí                                    [ODOLNOST]
│   ├── Stop služby + task → „zmlklo agentů" v konzoli                           [DETEKCE]
│   └── (principiální limit host-based — §19.1; mitigace organizační)            [RIZIKO]
└── Break-glass zneužití
    └── Logováno (kdo/kdy/délka) + zrušeno při heartbeatu                        [AUDIT]
```

### 31.2 Cíl útočníka: vnést malware přes médium

```
Vnést malware
├── Infikované neschválené USB → block/warn + incident                          [BLOK/AUDIT]
├── Infikované SCHVÁLENÉ USB (legitimní médium, infikovaný obsah)
│   └── Mimo rozsah USB Guardian (řeší EDR/antivir)                              [HRANICE]
│         → doporučení: blocklist konkrétního média (roadmapa §19.7)
└── BadUSB (médium se tváří jako klávesnice/HID)
    └── Mimo rozsah (storage-class) — §19.8; mitigace GPO/EDR                    [HRANICE]
```

### 31.3 Cíl útočníka: MITM mezi agentem a serverem

```
MITM agent↔API
├── Odposlech → TLS šifrování                                                    [DŮVĚRNOST]
├── Podvržení serveru → pinning otisku (agent ověří přesný cert)                 [SPOOFING BLOK]
│     └── útočník nemá privátní klíč certu → handshake selže
└── Rollback whitelistu (podstrčit starší validní verzi)
    ├── Vyžaduje MITM → eliminováno pinningem                                    [BLOK]
    └── tvrdší ochrana (monotónní verze vynucená agentem) = vylepšení (§22 Q8)
```

### 31.4 Cíl útočníka: kompromitace serveru / klíče

```
Kompromitace serveru APP_SERVER
├── Získat privátní klíč whitelistu → podvrhnout whitelist
│   ├── Mitigace: ACL/DPAPI na klíči, omezený přístup na APP_SERVER                    [MITIGACE]
│   └── Dopad ohraničen: jen integrita whitelistu (ne CA, ne code-signing)       [OMEZENÝ DOPAD]
├── Změnit politiku (enforce=false) → agenti přestanou blokovat
│   └── Detekovatelné (audit změn nastavení); vyžaduje přístup do konzole        [DETEKCE/AUTORIZACE]
└── Doporučení: monitoring přístupu k APP_SERVER, budoucí HSM, rotace klíče (§26.7)
```

### 31.5 Shrnutí pokrytí

| Útok | Přeruší | Zbytkové riziko |
|------|---------|-----------------|
| Neschválené médium | Blok/warn + audit | Pre-mount okno |
| Podvržení whitelistu | Podpis (fail-secure) | Kompromitace klíče na serveru |
| MITM | TLS + pinning | — |
| Vyřazení agenta | Watchdog + detekce | Lokální admin |
| BadUSB / obsah | — (mimo rozsah) | Doplnit GPO/EDR/blocklist |

---

## 32. Kompletní příklady konfigurace (komentované)

> Hodnoty jsou ilustrativní; reálné firemní hodnoty jsou v `*.local.json` (gitignored).

### 32.1 Agent — `agent.config.json` (+ `.local.json`)

```json
{
  "policy": {
    "mode": "block",                 // lokální default před 1. heartbeatem (server pak přebije)
    "onExpiredWhitelist": "warn",    // warn|block|allow při prošlé verzi whitelistu
    "overridePath": "C:\\ProgramData\\USBGuardian\\override.json"
  },
  "whitelist": {
    "syncUrl": "https://SQL_SERVER_IP:5443",   // API (HTTPS)
    "localPath": "C:\\ProgramData\\USBGuardian\\whitelist\\whitelist.json",
    "allowWildcards": false          // true = povolit záznamy bez sériáku (bezpečnostní varování)
  },
  "sync": {
    "whitelistSyncIntervalMinutes": 2,   // heartbeat + kontrola verze
    "incidentSyncIntervalMinutes": 1
  },
  "tls": {
    "validateServerCertificate": true,
    "pinnedThumbprint": "API_CERT_THUMBPRINT"   // otisk certu API
  },
  "signing": {
    "enabled": true,                 // prod: VŽDY true (ověřovat podpis whitelistu)
    "publicKeyPath": "Config\\whitelist_public.pem"
  },
  "localConsole": { "enabled": true, "port": 5080 },
  "notifications": { "toast": { "enabled": true, "contactMessage": "Kontaktujte IT" } }
}
```

### 32.2 API — `appsettings.local.json`

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=tcp:SQL_SERVER_IP,1433;Database=USBGuardian;Integrated Security=true;TrustServerCertificate=true;"
  },
  "Authorization": { "AllowedGroups": [ "DOMENA\\USBGuardianClients" ] },
  "Kestrel": { "Endpoints": {
    "Https": { "Url": "https://0.0.0.0:5443" },
    "Http":  { "Url": "http://0.0.0.0:5050" }     // roadmapa: uzavřít (jen HTTPS)
  }}
}
```

### 32.3 Konzole — `appsettings.local.json`

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=tcp:SQL_SERVER_IP,1433;Database=USBGuardian;Integrated Security=true;TrustServerCertificate=true;"
  },
  "Authorization": {
    "AdminGroups": [ "DOMENA\\USB-Guardian-Admins" ],
    "AllowedUsers": [ "DOMENA\\it-admin" ],     // lockout-safe bootstrap
    "DevAllowAll": false
  },
  "Whitelist": { "PrivateKeyPath": "C:\\Apps\\USBGuardianConsole\\whitelist_private.pem" },
  "Kestrel": { "Endpoints": { "Http": { "Url": "http://0.0.0.0:4200" } } },
  "AdSync": { "Enabled": true, "IntervalMinutes": 60, "SearchBase": "", "IncludeDisabled": false }
}
```

### 32.4 Centrální nastavení (`AppSettings` v DB) — typické hodnoty

| Klíč | Hodnota | Poznámka |
|------|---------|----------|
| `policy.enforce` | `true` | globální vynucování |
| `comm.silentAfterMinutes` | `180` | práh „zmlklého agenta" |
| `whitelist.validityDays` | `365` | platnost vydané verze |
| `retention.enabled` / `.incidentDays` | `true` / `365` | retence |
| `deploy.enabled` / `.dryRun` / `.defaultEnroll` | `false` / `true` / `false` | auto-enroll (bezpečný default) |
| `email.enabled` / `.smtpHost` | `true` / `axima-cz.mail.protection.outlook.com` | M365 Direct Send |

---

## 33. Chování v hraničních situacích (edge cases)

Systematický přehled, jak se systém chová v netriviálních situacích — pro oponenta hledajícího
nedefinované stavy.

| Situace | Chování systému | Návrhový princip |
|---------|------------------|------------------|
| Server (APP_SERVER/API) nedostupný | Agent jede offline: lokální whitelist + poslední politika; fronta incidentů se hromadí (perzistentní) | Klient = samostatný; výpadek neotevře ochranu |
| Whitelist soubor chybí | `WhitelistChecker` vrátí `null` → médium se neověří → dle `onExpired`/politiky (fail-secure) | Fail-secure |
| Podpis `.sig` chybí/nesedí | Whitelist odmítnut, jede poslední platná verze; nová se neuloží | Fail-secure, atomický zápis |
| Stažení whitelistu přeruší uprostřed | Atomický zápis (temp → rename, nejdřív .sig pak .json); nekonzistentní kombinace se odmítne | Atomicita |
| Agent restartuje s aktivní blokací | `blocked.json` + startovní sken + reconcile → stav dorovnán | Perzistence + reconcile |
| Médium odpojeno během blokace | `BlockDevice` reportuje stav; při vracení `GONE` → úklid ze seznamu | Robustní vracení |
| Médium odpojeno, pak znovu připojeno | Stejný klíč → stejné rozhodnutí; reconcile na připojeném vyhodnotí dle aktuální politiky | Identita = VID:PID:SN |
| Break-glass vyprší (timeout) | `OverrideActive` = false → efektivní režim zpět na server enforce; reconcile re-blokuje | Dočasnost override |
| Break-glass + ztráta spojení dlouhodobě | Override platí do timeoutu (max 72 h), pak vyprší i bez serveru | Strop jako pojistka |
| Server enforce=false → true (zapnutí) | Reconcile re-blokuje připojená neschválená (ReEnforce) | Symetrie |
| Médium schváleno za běhu | Po stažení (Reload) reconcile vrátí i při enforce | Cache invalidace |
| Médium odebráno z whitelistu | Nově připojené blokováno ihned; připojené po reconcile/restartu | Lokální whitelist |
| Dvě média současně | Každé vyhodnoceno samostatně (per PNPDeviceID) | Nezávislé zpracování |
| Uživatel odhlášen (jen služby) | Atribuce fallback na strojový účet (incident se zapíše vždy) | Fail-safe atribuce |
| Více session (RDP + konzole) | WTS API bere aktivní konzolovou session, fallback enumerace | Best-effort atribuce |
| WMI subsystém selže | Watchdog (5 min) re-registruje watchery; loguje | Sebeozdravení |
| Disk bez drive-letteru | Vyhodnocen na úrovni PnP (blokace funguje i bez FS mountu) | Blok na connect |
| Cizí (jiným nástrojem) zakázané médium | Agent vrací jen to, co **sám** zakázal (`blocked.json`) | Neplete se do cizího |
| Hodiny stanice posunuté | Časy v UTC; override `until` v UTC → timeout robustní | UTC všude |
| Velmi velký whitelist (10k) | O(1) match, index v paměti; načtení jen při změně verze | Škálovatelný index |
| Souběh reconcile a connect události | Stavy thread-safe (`ConcurrentDictionary`, zámky v `DeviceBlocker`/`PolicyState`) | Thread-safety |

### 33.1 Definované „bezpečné" výchozí stavy

- Před prvním heartbeatem: lokální `policy.mode` (lze nastavit `block` pro „secure by default").
- Chybějící/neplatný whitelist: nepustí (fail-secure), dle `onExpired`.
- Auto-enrollment: **vypnuto + dry-run** (žádné nečekané hromadné nasazení).
- Lokální konzole: **vypnuta** (default), admin-only, loopback.

### 33.2 Nedoporučené konfigurace (a proč)

| Konfigurace | Riziko |
|-------------|--------|
| `signing.enabled=false` | Vypne ověření podpisu → whitelist lze podvrhnout (jen pro vývoj) |
| `tls.validateServerCertificate=false` bez pinu | MITM (jen pro vývoj) |
| `allowWildcards=true` | Méně specifické (VID:PID bez sériáku) → širší povolení |
| `policy.onExpiredWhitelist=allow` | Po expiraci pustí vše → ztráta ochrany |

---

# ČÁST VIII — Doplněk

## 34. Co se změnilo od verze 1.0 (stav k 4. 9. 2026)

Kapitoly 1–33 zůstávají ve znění z 19. 6. 2026. Tato kapitola shrnuje, co od té doby přibylo, co se
v provozu ukázalo jinak, než dokument předpokládal, a co zůstává otevřené. Píšu ji proto, že dokument,
který popisuje záměr a tváří se jako popis stavu, je horší než žádný — oponent podle něj nemůže nic ověřit.

### 34.1 Deník provozu (`ActivityLog`)

Verze 1.0 měla auditní stopu postavenou výhradně na incidentech. To je málo: **do incidentů se dostane jen
to, co skončilo incidentem**. Když agent přestal komunikovat, když někdo změnil whitelist nebo když se
nasadila nová verze, nezůstala po tom stopa nikde než v Event Logu jednoho stroje.

Přibyla tabulka `dbo.ActivityLog` (čas, úroveň, zdroj, stanice, uživatel, zpráva) a stránka **Aktivita**
v konzoli. Píše do ní **API** (heartbeat včetně toho, *co* server odpověděl, příjem dávek incidentů) i
**konzole** (ruční nasazení a aktualizace, trvalé vyřazení stanice, publikace whitelistu) — obojí přes
sdílený `ActivityLogger`, aby se provoz četl jako jeden příběh.

Zápis je **fire-and-forget a každá chyba se spolkne**. Kdyby heartbeat agenta spadl kvůli tomu, že nešlo
zapsat řádek deníku, byl by pozorovatel důležitější než to, co pozoruje. Ze stejného důvodu se na dokončení
zápisu nečeká — tep stovek agentů nemá být svázaný s latencí databáze.

**Otevřený bod (poctivě):** `sp_PurgeActivityLog` v databázi je, ale **nikdo ji nevolá**. Při 227 stanicích
a heartbeatu po 2 minutách jde řádově o **150 tisíc řádků denně**; retence deníku je tedy dluh, ne funkce.
V Nastavení je zatím jen `retention.incidentDays`.

### 34.2 Nasazovací kanál: instalace ≠ aktualizace

Verze 1.0 popisovala nasazení jako jednu úlohu. Provoz ukázal dvě chyby v tom předpokladu.

**(a) Aktualizace není kopie navíc.** Fleet skript uměl jen čistou instalaci; „prostě robocopy" by na
běžícím agentovi přepsal část DLL, kopie zamčeného `.exe` by selhala a na stanici by zůstala **směs verzí**
— zatímco deploy hlásí úspěch. Vznikl proto `Update-Agent.cmd` (a `Deploy-Api.cmd` pro server) se vzorem
**zastav → počkej na `STOPPED` → zkopíruj → ověř `RUNNING`**.

**(b) Jedna identita držela obě vrstvy.** Klientský deploy účet byl zároveň admin na databázovém serveru,
takže jeho kompromitace by sáhla na fleet i na server současně. Rozděleno na tři role: `gmsa-deploy$`
(jen stanice), `gmsa-srvdeploy$` (jen server API, záměrně mimo skupinu serverových adminů) a účet běžící
konzole, který **není admin nikde**.

**Nález při ověřování (4. 9. 2026):** úloha `USBGuardian-ApiDeploy`, kterou dokumentace popisovala jako
existující, na app serveru **nikdy nevznikla** — byl tam jen skript. API proto od června běželo ve staré
verzi, ačkoli „deploy proběhl". Úloha byla založena a první běh ověřen (návratový kód 0, služba `RUNNING`,
`/api/version` hlásí aktuální commit). Poučení do oponentury: *tvrzení „kanál existuje" má cenu jen tehdy,
když je doložené jeho posledním během.*

> **Past při zakládání úlohy pod gMSA:** `schtasks /Create /RU "…gmsa$"` bez hesla vyrobí úlohu s
> `LogonType=InteractiveToken` → nespustí se (event 332). S4U (`/NP`) nemá síťové credentials a nedosáhne
> na `\\HOST\C$`. Funguje jedině XML s `LogonType=Password` uložené v **UTF-16** a založené přes `/XML`.

**Kanály a návrat zpět:** balíček se archivuje po verzích (`stable` / `beta`), takže jde nasadit předchozí
verzi. V balíčku je i offline instalátor pro stanici, kam deploy kanál nedosáhne.

### 34.3 Lokální konzole: autorizace lokálního admina

Fáze 3 (break-glass) předpokládala, že lokální admin se do konzole na `127.0.0.1:5080` dostane. V praxi
se nedostal. Požadavek na loopback je z pohledu Windows **síťové přihlášení** a u **lokálního** účtu z
takového tokenu `LocalAccountTokenFilterPolicy` odebere skupinu `Administrators` (zůstane jen jako
*deny-only*), takže `IsInRole` vrátí false, i když člověk admin **je**. Break-glass byl tedy nedostupný
přesně v situaci, na kterou je určený.

Kontrola nyní **uznává i filtrovaný token**. Je to obhajitelné: členství tu slouží jako **autorizace**,
ne jako zdroj práv — samotnou akci provádí služba pod SYSTEM, žádný elevovaný token volajícího není
potřeba. Odmítnutí navíc vrací stránku, která ukáže, **jako kdo** byl požadavek viděn a co je potřeba;
bez toho se to nedalo diagnostikovat na dálku.

**Rozpor v konfiguraci a jeho rozhodnutí:** šablona měla `localConsole.enabled=false` (minimální attack
surface), ale **rozvezený balíček i archivované verze mají `true`**. Rozhodnuto 4. 9. 2026: konzole je na
fleetu **zapnutá** a je **výhradně pro lokálního administrátora té stanice** — koncový uživatel do ní
nepatří. Šablona v repu zůstává `false` (bezpečný default pro jiné prostředí, portabilita), balíček pro
fleet se staví s `true` a build na opačný stav upozorní.

**Důsledek pro čtení dokumentu:** v prostředí, kde jsou admin práva na oddělených účtech (`pcadmin.*` ve
skupině `Workstation-Admins`), není break-glass nástroj *uživatele v terénu*, ale **technika u stanice, která
nedosáhne na server**. Běžný účet dostane vysvětlující odmítnutí — ověřeno v provozu 4. 9. 2026, kdy se do
konzole zkusil dostat kolega pod svým denním účtem. Chování bylo správné; formulace na několika místech
dokumentace, které to popisovaly jako funkci pro uživatele, byly opraveny.

### 34.4 Provozní funkce přidané po verzi 1.0

| Funkce | Co řeší |
|--------|---------|
| **Kontroly stavu** | Seznam kontrol serveru i klientů se ukáže dopředu a odškrtává se s průběžnými výsledky; export CSV / HTML / PDF / TXT. Bez toho nebylo poznat, jestli kontrola běží, nebo se zasekla. |
| **Plánovaný restart** | Služeb na serveru i agenta na stanici (agent výchozím nastavením 04:15) — zaseknutý WMI watcher přežije restart služby, ne den provozu. |
| **Denní self-restart agenta** | Totéž z druhé strany: agent si restart řídí sám, i když na server nedosáhne. |
| **Filtry a vyřazení v Stanicích** | Filtry po sloupcích + trvalé „Ignorovat", které hromadné akce nepřepíšou. |
| **Vzhled z banky UI** | Přepínatelný v Nastavení, přežije překliknutí mezi stránkami. |
| **Uživatelská stránka lokální konzole** | Běžný účet místo odmítnutí vidí svou situaci: jestli se média kontrolují, které z připojených je neschválené a čím se prokazuje (`VID:PID:SN` + „zkopírovat pro IT"). Whitelist, diagnostika ani break-glass tam nejsou — ty zůstávají lokálnímu adminovi. Řeší nejčastější dotaz na helpdesk, aniž by komukoli rozšířila práva. |

### 34.5 Aktuální provozní čísla (4. 9. 2026)

| Ukazatel | Hodnota |
|----------|---------|
| Stanic v evidenci (z AD) | 227 |
| Stanic hlásících agenta | 4 (pilot) |
| Stanic bez agenta | 200 |
| Incidentů za 30 dní | 29 (z toho 20 varování, 0 blokováno) |
| Schválených médií ve whitelistu | 3 |
| Režim vynucování | varování (blokování zatím nezapnuto) |

Jinými slovy: **systém je hotový a ověřený, plošné rozvezení a zapnutí blokování jsou rozhodnutí, ne
technický dluh.** To je poctivější formulace než „nasazeno", kterou by tabulka bez čtvrtého řádku svedla
napsat.

### 34.6 Dopad na kapitoly 1–33

| Kapitola | Co se mění |
|----------|------------|
| 14 (Auditovatelnost, NIS2) | Auditní stopa už není jen incidentní — deník pokrývá i komunikaci a zásahy operátora. Chybí retence deníku. |
| 15 (Sestavení, nasazení) | Instalace a aktualizace jsou dvě úlohy; tři oddělené deploy identity; kanály stable/beta. |
| 17 (Provoz, monitoring, retence) | Přibyly kontroly stavu a plánované restarty; retence deníku je otevřená. |
| 13 / 19 (Vynucování, omezení) | Break-glass byl fakticky nedostupný kvůli filtrovanému tokenu — opraveno; rozpor kolem `localConsole.enabled` na fleetu trvá. |
| 20 (Roadmapa) | Nově: retence deníku, sjednocení lokální konzole na fleetu, upgrade `Microsoft.AspNetCore.Authentication.Negotiate` (NU1903). |

---

# Přílohy

## Příloha A — Glosář pojmů

| Pojem | Význam |
|-------|--------|
| **Agent** | .NET 8 Windows služba na stanici (SYSTEM), provádí detekci, vyhodnocení a vynucování. |
| **Whitelist** | Centrální seznam schválených médií (VID:PID:sériák), podepsaný RSA-4096. |
| **Blocklist** | (roadmapa) seznam explicitně zakázaných médií s předností před whitelistem. |
| **Enforce / vynucování** | Režim, kdy agent neschválené médium reálně blokuje (`Disable-PnpDevice`). |
| **Break-glass** | Dočasná lokální výjimka (admin stanice vypne blokování offline), zruší se při heartbeatu. |
| **Reconciliace** | Sladění stavu agenta se serverovou pravdou (vrátit/zablokovat dle politiky a whitelistu). |
| **Re-enforcement** | Znovuzablokování již připojených neschválených médií po zapnutí blokování. |
| **Heartbeat** | Periodické odchozí spojení agenta na API (verze, online, příjem `enforce` a příkazů). |
| **Pinning** | Ověření serveru agentem přes otisk certifikátu (bez CA). |
| **gMSA** | Group Managed Service Account — služební účet bez hesla v konfiguraci. |
| **Fail-secure** | Při selhání ověření systém volí bezpečnou variantu (nepustí). |
| **1:1 kopie** | Agent drží bajtově shodnou kopii serverem podepsaného whitelistu. |
| **PNPDeviceID** | Identifikátor PnP uzlu zařízení (`USBSTOR\DISK&VEN_…&PROD_…\…`). |
| **WTS API** | Windows Terminal Services API pro zjištění uživatele aktivní session. |
| **AllSigned** | GPO politika vyžadující podpis všech PS skriptů spouštěných na stroji. |
| **ToastHelper** | Pomocný proces v user session zobrazující Windows notifikace (agent = SYSTEM neumí přímo). |
| **Watchdog** | Scheduled task hlídající běh služby agenta (à 3 min). |

## Příloha B — Přehled konfiguračních klíčů

### B.1 Agent (`agent.config.json` / `.local.json`)

| Klíč | Význam |
|------|--------|
| `policy.mode` | Lokální default režim (`warn`/`block`) před prvním heartbeatem. |
| `policy.onExpiredWhitelist` | Chování při expiraci whitelistu (`warn`/`block`/`allow`). |
| `policy.overridePath` | Cesta k `override.json` (break-glass). |
| `whitelist.syncUrl` | URL API (`https://SERVER:5443`). |
| `whitelist.localPath` | Cesta k lokálnímu `whitelist.json`. |
| `whitelist.allowWildcards` | Povolit záznamy bez sériáku (default false). |
| `sync.whitelistSyncIntervalMinutes` | Interval heartbeatu/whitelist syncu (~2). |
| `sync.incidentSyncIntervalMinutes` | Interval odesílání incidentů (~1). |
| `tls.validateServerCertificate` | Validace certu serveru. |
| `tls.pinnedThumbprint` | Otisk certu API (pinning). |
| `signing.enabled` | Ověřovat podpis whitelistu (prod: true). |
| `signing.publicKeyPath` | Veřejný klíč pro ověření (`whitelist_public.pem`). |
| `localConsole.enabled` / `localConsole.port` | Lokální konzole (default vypnuto / 5080). |
| `notifications.toast.enabled` / `.contactMessage` | Toast notifikace. |

### B.2 Server — centrální `AppSettings` (DB)

| Klíč | Význam |
|------|--------|
| `policy.enforce` | Globální vynucování (APP_SERVER = pravda) → heartbeat. |
| `comm.silentAfterMinutes` | Práh „zmlklého agenta". |
| `deploy.*` | Auto-enrollment (`enabled`/`dryRun`/`defaultEnroll`/`intervalMinutes`/`maxPerRun`/`allowHosts`/`includeHosts`/`excludeHosts`/`targetsFile`/`lastRun`). |
| `access.users` / `access.groups` | Whitelist přístupu do konzole. |
| `email.*` | SMTP relay (M365 Direct Send) + alerty. |
| `retention.enabled` / `retention.incidentDays` / `retention.lastRun` | Retence incidentů. |
| `whitelist.validityDays` | Platnost vydané verze whitelistu (default 365). |
| `cmd.report.<HOST>` | Vyžádání dat (ReportNow) per stanice. |

### B.3 Server — `appsettings.local.json` (konzole/API)

| Klíč | Význam |
|------|--------|
| `ConnectionStrings.DefaultConnection` | Připojení k SQL (Integrated Security). |
| `Authorization.AdminGroups` / `AllowedUsers` | Přístup do konzole (lockout-safe bootstrap). |
| `Authorization.AllowedGroups` (API) | AD skupina agentů (`USBGuardianClients`). |
| `Whitelist.PrivateKeyPath` | Privátní RSA klíč pro podpis whitelistu (APP_SERVER, gitignored). |
| `Kestrel.Endpoints` | Bind adresy/porty. |
| `AdSync.*` | AD sync (interval, SearchBase, IncludeDisabled). |

## Příloha C — Databázové schéma a SQL granty

### C.1 Skripty (spustit v pořadí)

| Skript | Obsah |
|--------|-------|
| `01_create_database.sql` | databáze |
| `02_create_tables.sql` | Computers, WhitelistDevices, WhitelistVersions, Incidents, view + sp |
| `03_add_sourcefile.sql` | SourceFile + DisconnectedAt |
| `04_adsync_columns.sql` | LastSeen nullable + OperatingSystem / InActiveDirectory / AdSyncedAt |
| `05_adpath.sql` | AdPath (cesta v AD) |
| `06_appsettings.sql` | AppSettings (`Value` = NVARCHAR(MAX)) + grant |
| `07_whitelist_publish.sql` | WhitelistVersions: `Json` + `Signature` → NVARCHAR(MAX) |

### C.2 SQL granty (least-privilege, účet konzole)

```sql
CREATE LOGIN [DOMENA\APP_SERVER$] FROM WINDOWS;
USE USBGuardian;
CREATE USER  [DOMENA\APP_SERVER$] FOR LOGIN [DOMENA\APP_SERVER$];
ALTER ROLE db_datareader ADD MEMBER [DOMENA\APP_SERVER$];           -- čte vše
GRANT INSERT, UPDATE, DELETE ON dbo.Computers          TO [DOMENA\APP_SERVER$];
GRANT INSERT, UPDATE, DELETE ON dbo.WhitelistDevices   TO [DOMENA\APP_SERVER$];  -- DELETE = mazání z katalogu
GRANT INSERT, UPDATE         ON dbo.WhitelistVersions  TO [DOMENA\APP_SERVER$];  -- bez DELETE (append-only audit)
-- AppSettings grant viz 06_appsettings.sql
-- Pozn.: DELETE na Incidents NEMÁ konzole (retenci dělá API pod gMSA).
```

## Příloha D — Přehled API endpointů

| Metoda | Endpoint | Auth | Účel |
|--------|----------|------|------|
| GET | `/api/heartbeat` | Kerberos (skupina) | Stav, verze, `Enforce`, `ReportNow`, dostupnost nové verze |
| POST | `/api/incidents` | Kerberos (skupina) | Příjem incidentů → 202 → fronta |
| GET | `/api/incidents` | (konzole) | Výpis pro UI |
| GET | `/api/whitelist` | Kerberos (skupina) | Podepsaný blob verbatim |
| GET | `/api/whitelist/signature` | Kerberos (skupina) | Base64 podpis |
| GET | `/api/cert-info` | — | Otisk certu (pinning) |
| GET | `/api/version` | — | Commit běžícího API |
| GET | (konzole) `/api/version` | — | Commit konzole |
| GET | (konzole) `/export/incidents.csv` | konzole auth | CSV export (dědí filtr) |
| GET | (konzole) `/export/manager` | konzole auth | Manažerský report |
| — lokální konzole agenta (loopback :5080, admin-only) — | | | |
| GET | `/` , `/api/status` | lokální admin | Dashboard / stav |
| POST | `/api/override` , `/api/override/clear` | lokální admin | Break-glass |
| POST | `/api/unblock-all` | lokální admin | Okamžité vrácení blokovaných |
| POST | `/api/restart` | lokální admin | Self-restart služby |

## Příloha E — Mapování NIS2 / ISO 27001 → funkce

| Požadavek | Funkce USB Guardian |
|-----------|---------------------|
| NIS2 — řízení aktiv | Evidence připojených médií (i neschválených), inventář stanic z AD |
| NIS2 — kontrola přístupu | Whitelist + vynucování (block/warn) |
| NIS2 — ochrana před malwarem | Blokace neschválených médií (prevence vnesení) |
| NIS2 — logování/monitoring | Auditní stopa incidentů, dohled „zmlklých" agentů |
| NIS2 — reakce na incidenty | Near-real-time hlášení + e-mail alerty |
| NIS2 — kontrola integrity | RSA-4096 podpis whitelistu, verzování |
| ISO 27002 8.7 (malware) | Prevence vnesení přes médium |
| ISO 27002 7.10 (média) | Řízení používání výměnných médií (whitelist) |
| ISO 27002 8.15 (protokolování) | Incidenty s atribucí |
| ISO 27002 8.16 (monitoring) | Centrální dohled, detekce zmlklých agentů |
| ISO 27002 5.9 (inventura aktiv) | Evidence médií a stanic |
| zák. 181/2014 + VKB | Řízení aktiv/přístupu, ochrana před škodlivým kódem, záznam událostí, fyzická bezpečnost přenosných zařízení |

> Mapování je **indikativní** — konkrétní soulad závisí na zařazení do ISMS a doprovodných organizačních
> opatřeních (§3.4).

## Příloha F — Souhrn návrhových rozhodnutí

| # | Rozhodnutí | Zvoleno | Klíčový trade-off |
|---|-----------|---------|-------------------|
| 6.1 | Push vs. pull | Push | Latence příkazů ≤ heartbeat (přijato) |
| 6.2 | Blazor vs. Node | Blazor Server | Server-side stav (OK pro malý tým) |
| 6.3 | HttpListener vs. Kestrel (lokální konzole) | HttpListener | Méně komfortu, menší footprint |
| 6.4 | Klíč hostname vs. IP | Hostname | Vyžaduje unikátní hostnames (AD) |
| 6.5 | Self-signed + pinning vs. CA | Self-signed + pinning | Distribuce otisku, výměna = update pinu |
| 6.6 | MachineKeySet vs. Ephemeral | MachineKeySet | Nutné pro gMSA TLS handshake |
| 6.7 | Auto-podpis vs. offline klíč | Server-side auto-podpis | Privátní klíč na serveru (ACL) za automatizaci |
| 6.8 | 1:1 bajtová kopie | Ano | Server musí uchovat blob verbatim |
| 6.9 | Disable-PnpDevice vs. IOCTL | Disable-PnpDevice | Režie PowerShellu (řídká událost) |
| 6.10 | WTS API vs. Environment.UserName | WTS API | Windows-specifické (OK) |
| 6.11 | Soft vs. hard delete whitelistu | Obojí | Hard-delete vyžaduje DELETE grant |
| 6.12 | Deploy identita | Oddělený gMSA task | Více částí, ale striktní oddělení rolí |

---

*Konec dokumentu. Verze 1.0, 2026-06-19. Autor: Milan Trnka (AXIMA). Podklad pro oponenturu projektu USB Guardian.*








