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
| **Live commit (konzole)** | `4e3ef32` (v patičce konzole; ověř přes `/api/version`) |
| **Agent (test)** | `.181` (TRNKAMW11) – `syncUrl=https://B-S-W-SQL-04:5443` + pin; 1. rollout target `TRNKAMW11N` (dyn. IP) |

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

## 5. Další kroky / pending

> **Nově implementováno (čeká na nasazení API operátorem + rollout agenta):**
> - **Whitelist na klienty rychleji**: `WhitelistSync` interval **15 → 2 min** (konfig `sync:whitelistSyncIntervalMinutes`).
>   Nový schválený whitelist je na klientech do ~2 min (heartbeat sám stáhne). Stačí redeploy agenta.
> - **„Vyžádat data" na klik** (Stanice): příkaz `ReportNow` přibalený do heartbeatu (klíč `cmd.report.<HOST>`
>   v `AppSettings`; API jen čte, jednorázovost přes porovnání s předchozím `LastSeen`). Agent při něm hned
>   flushne incidenty. **Vyžaduje deploy API na SQL-04 (operátor)** + redeploy agenta; konzole funguje hned.
> - **Dlaždice „Zmlklo agentů"** + práh `comm.silentAfterMinutes` (Nastavení); **řaditelné** sloupce „Detailně" (Přehled).


- **Zavřít HTTP 5050** na SQL-04 (jen HTTPS) – NIS2. (Potřebuje SQL-04: firewall block, nebo přebindovat API.)
- **Distribuce + vzdálená instalace agenta** na ~210 stanic bez agenta. Agent config = `syncUrl https://…:5443`
  + `tls.pinnedThumbprint`. Vzdálená instalace přes WinRM (`Enable-PSRemoting` na klientech), bez uložených
  admin creds, audit, just-in-time; nejdřív prototypovat kanál na `TRNKAMW11N`.
- **Podpisový/publikační workflow** whitelistu (staging → offline podpis → publikace) → odemkne whitelist,
  **vynucování** i **blocklist** „naostro" k agentům (potřebují podepsanou distribuci + propagaci přes heartbeat).
- **Per-serial blocklist** (zákaz konkrétního média, near-real-time k agentům – přednost před whitelistem).
- **E-mailové alerty**: konfigurace + odesílání hotové (`IncidentAlertService`); fungují, jakmile dorazí nové incidenty.
- **Hardening:** gMSA místo LocalSystem pro konzoli, dedikovaná `USB-Guardian-Admins` místo `SQL Admins2`,
  HTTPS pro konzoli, přesun API z SQL-04 na .213 (dvouvrstvý princip).
- **Úklid:** ~~nepoužitý `Microsoft.Data.Sqlite` v agentu~~ (hotovo); ~~vadný GUID format `:N[..8]` v
  `NotificationService.ShowWarning`~~ (hotovo); zbývá stray (untracked) složka `server/USBGuardianAPI/`
  (duplikát vedle `USBGuardian.Api/` – ke smazání).

## 6. Mapa dokumentace

| Soubor | Obsah |
|--------|-------|
| `README.md` / `.en.md` | Funkční přehled, komponenty, konfigurace, nasazení |
| `HANDOFF.md` / `.en.md` | Tento dokument – předávka + živý stav |
| `docs/architecture.md` | Technická architektura, datový tok, bezpečnostní vrstvy |
