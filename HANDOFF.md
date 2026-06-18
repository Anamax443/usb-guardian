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
| **DB** | SQL Server `B-S-W-SQL-04`, databáze `USBGuardian` (skripty `database/01–05` aplikované) |
| **API** | běží na `B-S-W-SQL-04`, `:5050` (HTTP) / `:5443` (HTTPS), Windows služba, gMSA `AXINETWORK\gmsa-SQL$` |
| **Admin konzole** | **živá** `http://10.8.2.213:4200/` (`B-S-W-MIKOS`), Windows služba `USBGuardianConsole`, runtime `C:\Apps\USBGuardianConsole`, self-contained |
| **Účet konzole** | zatím **LocalSystem** = `AXINETWORK\B-S-W-MIKOS$` (least-priv SQL grant: read vše + write Computers/Whitelist*) |
| **Autorizace konzole** | AD skupina `AXINETWORK\SQL Admins2` + whitelist `AXINETWORK\trnkam` |
| **AD sync** | zapnutý, interval 60 min + na vyžádání; živě **211 stanic v AD, 210 bez agenta** |
| **Live commit** | `a13f62d` (zobrazený v patičce konzole) |
| **Agent (test)** | stanice `.181` (TRNKAMW11); první rollout target `TRNKAMW11N` (dynamická IP) |

## 3. Klíčová rozhodnutí (proč)

- **Push, ne pull** – 500+ klientů za NATem/firewallem; agentovi stačí odchozí spojení.
- **Dvouvrstvě** – operativa (konzole, AD sync) na app serveru `.213`, DB jen úložiště na SQL-04.
  (Pozn.: API zatím běží na SQL-04; přesun na .213 je naplánovaný hardening.)
- **Konzole = .NET/Blazor**, ne Node – reuse EF modelů z API (slinkované `DbModels`/`AppDbContext`),
  jeden jazyk, na serveru už ASP.NET Core je.
- **Lokální konzole agenta přes `HttpListener`**, ne Kestrel – agent nepotřebuje ASP.NET Core runtime.
- **Klíčování na hostname, ne IP** – stanice mají dynamické IP.
- **Privátní RSA klíč whitelistu nikdy na serveru** – publikace podepsané verze = offline krok (NIS2).
- **Portabilita** – žádné firemní hodnoty v kódu; vše v `*.local.json`, doména z `new DirectoryEntry()`.

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

- **Vzdálená instalace agenta** na 210 stanic bez agenta (WinRM – `Enable-PSRemoting` na klientech;
  nejdřív prototypovat kanál na `TRNKAMW11N`). Žádné uložené admin creds, audit, just-in-time.
- **Webová správa whitelistu** + podpisový workflow (staging → offline podpis → publikace).
- **Hardening:** gMSA místo LocalSystem, dedikovaná skupina `USB-Guardian-Admins` místo `SQL Admins2`,
  HTTPS pro konzoli, přesun API z SQL-04 na .213.
- **Úklid:** nepoužitý `Microsoft.Data.Sqlite` v agentu (persistence je JSON); stray složka
  `server/USBGuardianAPI/`; vadný GUID format `:N[..8]` v `NotificationService.ShowWarning`.

## 6. Mapa dokumentace

| Soubor | Obsah |
|--------|-------|
| `README.md` / `.en.md` | Funkční přehled, komponenty, konfigurace, nasazení |
| `HANDOFF.md` / `.en.md` | Tento dokument – předávka + živý stav |
| `docs/architecture.md` | Technická architektura, datový tok, bezpečnostní vrstvy |
