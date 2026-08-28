// ============================================================
// HealthService.cs
// Sada kontrol "funguje všechno, jak má?" pro serverovou konzoli.
//
// PROČ TO EXISTUJE:
//   28.08.2026 se ukázalo, že API služba na SQL boxu byla 6 týdnů
//   zastavená. Agent běžel, incidenty si ukládal do fronty, ale na
//   server nedotekly. Konzole to nikde neřekla nahlas — dlaždice
//   "Zmlklo agentů" ukazovala 1 a nikdo se nekoukl. Tahle třída dělá
//   z tichého selhání hlasité: každá vrstva má vlastní kontrolu
//   s vlastním verdiktem.
//
// SÉMANTIKA STAVŮ (schválně rozlišené, ať je z výstupu poznat,
// jestli něco NEJDE nebo je to jen VYPNUTÉ):
//   Ok      – funguje
//   Warn    – funguje, ale něco si zaslouží pozornost
//   Bad     – nefunguje, je potřeba zásah (má vyplněné Fix)
//   Off     – vypnuto/nenastaveno ZÁMĚRNĚ, není to chyba
//   Unknown – zatím není z čeho soudit (čeká na data)
//
// Kontroly jsou READ-ONLY (SQL select + HTTP GET), nic nemění.
// Každá kontrola je izolovaná – když spadne, spadne jen ona.
// ============================================================

using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;

namespace USBGuardian.Admin.Health;

public enum HealthState { Ok, Warn, Bad, Off, Unknown }

public sealed class HealthCheck
{
    public string Group { get; init; } = "";
    public string Name { get; init; } = "";
    public HealthState State { get; init; }
    /// <summary>Krátká naměřená hodnota (co kontrola viděla).</summary>
    public string Value { get; init; } = "";
    /// <summary>Vysvětlení pro člověka, který projekt nezná.</summary>
    public string Detail { get; init; } = "";
    /// <summary>Co s tím dělat, když stav není Ok. Prázdné = není co řešit.</summary>
    public string Fix { get; init; } = "";
}

public sealed class HealthReport
{
    public DateTime RanAt { get; init; } = DateTime.Now;
    public List<HealthCheck> Checks { get; init; } = new();
    public TimeSpan Duration { get; init; }

    public int Bad => Checks.Count(c => c.State == HealthState.Bad);
    public int Warn => Checks.Count(c => c.State == HealthState.Warn);
    public int Ok => Checks.Count(c => c.State == HealthState.Ok);
    public int Off => Checks.Count(c => c.State is HealthState.Off or HealthState.Unknown);

    /// <summary>Celkový verdikt: nejhorší stav vyhrává.</summary>
    public HealthState Overall => Bad > 0 ? HealthState.Bad
                                : Warn > 0 ? HealthState.Warn
                                : Ok > 0 ? HealthState.Ok
                                : HealthState.Unknown;
}

public sealed class HealthService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IConfiguration _config;

    public HealthService(IDbContextFactory<AppDbContext> dbFactory, IConfiguration config)
    {
        _dbFactory = dbFactory;
        _config = config;
    }

    public async Task<HealthReport> RunAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var checks = new List<HealthCheck>();

        AppDbContext? db = null;
        try
        {
            db = await _dbFactory.CreateDbContextAsync(ct);
            checks.Add(await CheckDatabaseAsync(db, ct));
        }
        catch (Exception ex)
        {
            db?.Dispose();
            db = null;
            checks.Add(new HealthCheck
            {
                Group = "Sběr dat",
                Name = "Databáze",
                State = HealthState.Bad,
                Value = "nedostupná",
                Detail = "Konzole se nepřipojí k SQL Serveru: " + Short(ex.Message),
                Fix = "Ověř běh SQL Serveru a connection string v appsettings.local.json; "
                    + "účet konzole potřebuje práva na databázi USBGuardian.",
            });
        }

        if (db is not null)
        {
            await using (db)
            {
                var cfg = await HealthConfig.LoadAsync(db, ct);

                checks.Add(await CheckApiAsync(cfg, ct));
                checks.Add(await CheckIncidentFlowAsync(db, cfg, ct));
                checks.Add(await CheckAgentsSilentAsync(db, cfg, ct));
                checks.Add(await CheckCoverageAsync(db, ct));

                checks.Add(await CheckWhitelistVersionAsync(db, ct));
                checks.Add(await CheckWhitelistCatalogFreshAsync(db, ct));
                checks.Add(CheckSigningKey());
                checks.Add(await CheckEnforceAsync(db, ct));

                checks.Add(await CheckEmailAsync(db, ct));
                checks.Add(await CheckRetentionAsync(db, ct));
                checks.Add(CheckAdSync());
                checks.Add(await CheckAutoDeployAsync(db, ct));
                checks.Add(await CheckServiceRestartAsync(db, ct));
                checks.Add(await CheckVersionsAsync(db, cfg, ct));
            }
        }

        sw.Stop();
        return new HealthReport { Checks = checks, Duration = sw.Elapsed };
    }

    // ── Sběr dat ─────────────────────────────────────────────────

    private static async Task<HealthCheck> CheckDatabaseAsync(AppDbContext db, CancellationToken ct)
    {
        var incidents = await db.Incidents.CountAsync(ct);
        var computers = await db.Computers.CountAsync(ct);
        return new HealthCheck
        {
            Group = "Sběr dat",
            Name = "Databáze",
            State = HealthState.Ok,
            Value = $"{incidents} incidentů · {computers} stanic",
            Detail = "Konzole čte databázi USBGuardian. Bez ní není vidět nic.",
        };
    }

    /// <summary>Přesně ten výpadek z 28.08.2026: API služba stojí → agenti nemají kam reportovat.</summary>
    private static async Task<HealthCheck> CheckApiAsync(HealthConfig cfg, CancellationToken ct)
    {
        const string name = "API pro agenty";
        const string why = "Agenti sem posílají incidenty a odsud si berou whitelist a politiku. "
                         + "Když API stojí, agent si data ukládá do fronty na disku a server je slepý.";

        if (string.IsNullOrWhiteSpace(cfg.ApiUrl))
        {
            return new HealthCheck
            {
                Group = "Sběr dat",
                Name = name,
                State = HealthState.Off,
                Value = "nenastaveno",
                Detail = why,
                Fix = "Doplň adresu API v Nastavení → Kontroly stavu, např. https://SQL-SERVER:5443.",
            };
        }

        var url = cfg.ApiUrl.TrimEnd('/') + "/api/version";
        var sw = Stopwatch.StartNew();
        try
        {
            // Self-signed cert API je záměr (agent ho ověřuje pinningem otisku);
            // tahle kontrola řeší JEN dostupnost, proto validaci certu nevyžaduje.
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(cfg.ApiTimeoutSeconds) };
            var resp = await http.GetAsync(url, ct);
            sw.Stop();

            if (!resp.IsSuccessStatusCode)
            {
                return new HealthCheck
                {
                    Group = "Sběr dat",
                    Name = name,
                    State = HealthState.Bad,
                    Value = $"HTTP {(int)resp.StatusCode}",
                    Detail = why + $" Odpověď z {url} nebyla úspěšná.",
                    Fix = "Zkontroluj log API služby na jejím serveru (Event Log → Application).",
                };
            }

            var body = (await resp.Content.ReadAsStringAsync(ct)).Trim();
            return new HealthCheck
            {
                Group = "Sběr dat",
                Name = name,
                State = HealthState.Ok,
                Value = $"odpovídá ({sw.ElapsedMilliseconds} ms) · {Short(body, 120)}",
                Detail = why,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new HealthCheck
            {
                Group = "Sběr dat",
                Name = name,
                State = HealthState.Bad,
                Value = "NEDOSTUPNÉ",
                Detail = why + $" Spojení na {url} selhalo: " + Short(ex.Message),
                Fix = "Na serveru s API nastartuj službu API a ověř, že má START_TYPE = AUTO_START "
                    + "(jinak po restartu serveru nenaběhne). Pojistkou je Nastavení → Plánovaný restart služeb.",
            };
        }
    }

    /// <summary>Přitékají vůbec nová data? Tichý výpadek pozná jen tahle kontrola.</summary>
    private static async Task<HealthCheck> CheckIncidentFlowAsync(AppDbContext db, HealthConfig cfg, CancellationToken ct)
    {
        const string name = "Přítok incidentů";
        const string why = "Stáří nejnovějšího incidentu v databázi. Když roste, agenti sice mohou běžet, "
                         + "ale jejich hlášení nedotečou (výpadek API, sítě nebo služby agenta).";

        var reporting = await db.Computers.CountAsync(c => c.LastSeen != null, ct);
        if (reporting == 0)
        {
            return new HealthCheck
            {
                Group = "Sběr dat",
                Name = name,
                State = HealthState.Unknown,
                Value = "žádný agent zatím nereportoval",
                Detail = why,
                Fix = "Nasaď agenta aspoň na jednu stanici (Stanice → Nasazení).",
            };
        }

        var newest = await db.Incidents.OrderByDescending(i => i.Timestamp)
                                       .Select(i => (DateTime?)i.Timestamp)
                                       .FirstOrDefaultAsync(ct);
        if (newest is null)
        {
            return new HealthCheck
            {
                Group = "Sběr dat",
                Name = name,
                State = HealthState.Unknown,
                Value = "zatím žádný incident",
                Detail = why,
            };
        }

        var age = DateTime.UtcNow - newest.Value;
        var state = age.TotalHours > cfg.MaxIncidentAgeHours ? HealthState.Bad
                  : age.TotalHours > cfg.MaxIncidentAgeHours / 2.0 ? HealthState.Warn
                  : HealthState.Ok;

        return new HealthCheck
        {
            Group = "Sběr dat",
            Name = name,
            State = state,
            Value = $"poslední před {Age(age)} ({newest.Value.ToLocalTime():dd.MM.yyyy HH:mm})",
            Detail = why + $" Práh je {cfg.MaxIncidentAgeHours} h.",
            Fix = state == HealthState.Ok ? ""
                : "Projdi kontroly API pro agenty a Zmlklí agenti. Pozor: klidný provoz "
                + "(nikdo nepřipojil médium) vypadá stejně — práh nastav podle reality v Nastavení → Kontroly stavu.",
        };
    }

    private static async Task<HealthCheck> CheckAgentsSilentAsync(AppDbContext db, HealthConfig cfg, CancellationToken ct)
    {
        const string name = "Zmlklí agenti";
        const string why = "Stanice, které už agenta hlásily, ale déle než je práh se neozvaly. "
                         + "Může jít o vypnuté PC, ale i o zastavenou službu nebo zásah uživatele.";

        var reporting = await db.Computers.CountAsync(c => c.LastSeen != null, ct);
        if (reporting == 0)
        {
            return new HealthCheck
            {
                Group = "Sběr dat",
                Name = name,
                State = HealthState.Unknown,
                Value = "žádný agent zatím nereportoval",
                Detail = why,
            };
        }

        var limit = DateTime.UtcNow.AddMinutes(-cfg.SilentAfterMinutes);
        var silent = await db.Computers.CountAsync(c => c.LastSeen != null && c.LastSeen < limit, ct);

        return new HealthCheck
        {
            Group = "Sběr dat",
            Name = name,
            State = silent == 0 ? HealthState.Ok
                  : silent == reporting ? HealthState.Bad
                  : HealthState.Warn,
            Value = $"{silent} z {reporting} (práh {cfg.SilentAfterMinutes} min)",
            Detail = why,
            Fix = silent == 0 ? ""
                : "Seznam je na stránce Stanice (tečka komunikace). "
                + "Když mlčí VŠECHNY, je problém na serveru, ne na stanicích.",
        };
    }

    private static async Task<HealthCheck> CheckCoverageAsync(AppDbContext db, CancellationToken ct)
    {
        const string name = "Pokrytí stanic";
        const string why = "Kolik stanic z Active Directory má nainstalovaného agenta. "
                         + "Nepokrytá stanice není monitorovaná — pro NIS2 je to díra v evidenci.";

        var inAd = await db.Computers.CountAsync(c => c.InActiveDirectory, ct);
        var withAgent = await db.Computers.CountAsync(c => c.InActiveDirectory && c.LastSeen != null, ct);
        var missing = inAd - withAgent;

        if (inAd == 0)
        {
            return new HealthCheck
            {
                Group = "Sběr dat",
                Name = name,
                State = HealthState.Unknown,
                Value = "AD sync zatím neproběhl",
                Detail = why,
                Fix = "Spusť Stanice → Aktualizovat z AD.",
            };
        }

        var pct = withAgent * 100.0 / inAd;
        return new HealthCheck
        {
            Group = "Sběr dat",
            Name = name,
            State = missing == 0 ? HealthState.Ok : HealthState.Warn,
            Value = $"{withAgent} z {inAd} ({pct:F0} %) · chybí {missing}",
            Detail = why,
            Fix = missing == 0 ? "" : "Stanice → Nasazení, případně zapni auto-enrollment v Nastavení.",
        };
    }

    // ── Whitelist a politika ─────────────────────────────────────

    private static async Task<HealthCheck> CheckWhitelistVersionAsync(AppDbContext db, CancellationToken ct)
    {
        const string name = "Publikovaný whitelist";
        const string why = "Agent bere jako platnou jen PODEPSANOU a NEPROŠLOU verzi katalogu. "
                         + "Nepodepsaná nebo prošlá verze znamená, že se schválená média k agentům nedostanou.";

        var active = await db.WhitelistVersions.Where(v => v.IsActive)
                             .OrderByDescending(v => v.IssuedAt)
                             .FirstOrDefaultAsync(ct);
        if (active is null)
        {
            return new HealthCheck
            {
                Group = "Whitelist a politika",
                Name = name,
                State = HealthState.Bad,
                Value = "žádná aktivní verze",
                Detail = why,
                Fix = "Whitelist → Publikovat nyní.",
            };
        }

        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(active.Signature)) problems.Add("NENÍ PODEPSANÁ");
        if (active.ValidUntil <= DateTime.UtcNow)
            problems.Add("PROŠLÁ " + active.ValidUntil.ToLocalTime().ToString("dd.MM.yyyy"));

        var daysLeft = (active.ValidUntil - DateTime.UtcNow).TotalDays;
        var state = problems.Count > 0 ? HealthState.Bad
                  : daysLeft < 30 ? HealthState.Warn
                  : HealthState.Ok;

        return new HealthCheck
        {
            Group = "Whitelist a politika",
            Name = name,
            State = state,
            Value = problems.Count > 0
                ? $"{active.Version} — {string.Join(", ", problems)}"
                : $"{active.Version} · platí do {active.ValidUntil.ToLocalTime():dd.MM.yyyy} ({daysLeft:F0} dní)",
            Detail = why,
            Fix = state == HealthState.Ok ? ""
                : "Whitelist → Publikovat nyní (vydá a podepíše novou verzi). "
                + "Když podpis chybí i po publikaci, zkontroluj kontrolu Podpisový klíč whitelistu.",
        };
    }

    private static async Task<HealthCheck> CheckWhitelistCatalogFreshAsync(AppDbContext db, CancellationToken ct)
    {
        const string name = "Katalog vs. publikace";
        const string why = "Jestli se od poslední publikace nezměnil katalog schválených médií. "
                         + "Nepublikovaná změna se k agentům nedostane.";

        var active = await db.WhitelistVersions.Where(v => v.IsActive)
                             .OrderByDescending(v => v.IssuedAt)
                             .FirstOrDefaultAsync(ct);
        var activeDevices = await db.WhitelistDevices.CountAsync(d => d.IsActive, ct);

        if (active is null)
        {
            return new HealthCheck
            {
                Group = "Whitelist a politika",
                Name = name,
                State = HealthState.Unknown,
                Value = $"{activeDevices} aktivních médií, nic nepublikováno",
                Detail = why,
                Fix = "Whitelist → Publikovat nyní.",
            };
        }

        var changedAfter = await db.WhitelistDevices
            .CountAsync(d => d.IsActive && d.ApprovedAt > active.IssuedAt, ct);

        return new HealthCheck
        {
            Group = "Whitelist a politika",
            Name = name,
            State = changedAfter == 0 ? HealthState.Ok : HealthState.Warn,
            Value = changedAfter == 0
                ? $"{activeDevices} médií, publikováno {active.IssuedAt.ToLocalTime():dd.MM.yyyy HH:mm}"
                : $"{changedAfter} médií schváleno až PO poslední publikaci",
            Detail = why,
            Fix = changedAfter == 0 ? "" : "Whitelist → Publikovat nyní.",
        };
    }

    private HealthCheck CheckSigningKey()
    {
        const string name = "Podpisový klíč whitelistu";
        const string why = "Privátní RSA klíč, kterým konzole podepisuje vydané verze whitelistu. "
                         + "Bez něj vznikne nepodepsaná verze, kterou agent odmítne.";

        var path = _config["Whitelist:PrivateKeyPath"];
        if (string.IsNullOrWhiteSpace(path))
        {
            return new HealthCheck
            {
                Group = "Whitelist a politika",
                Name = name,
                State = HealthState.Off,
                Value = "nenastaveno",
                Detail = why,
                Fix = "Doplň Whitelist:PrivateKeyPath do appsettings.local.json na serveru konzole.",
            };
        }

        try
        {
            if (!File.Exists(path))
            {
                return new HealthCheck
                {
                    Group = "Whitelist a politika",
                    Name = name,
                    State = HealthState.Bad,
                    Value = "soubor neexistuje",
                    Detail = why + $" Cesta: {path}",
                    Fix = "Ulož privátní klíč na uvedenou cestu (chraň ho ACL – čte ho jen účet konzole).",
                };
            }

            using var _ = File.OpenRead(path);
            return new HealthCheck
            {
                Group = "Whitelist a politika",
                Name = name,
                State = HealthState.Ok,
                Value = "k dispozici",
                Detail = why + $" Cesta: {path}",
            };
        }
        catch (Exception ex)
        {
            return new HealthCheck
            {
                Group = "Whitelist a politika",
                Name = name,
                State = HealthState.Bad,
                Value = "nelze přečíst",
                Detail = why + " " + Short(ex.Message),
                Fix = "Uprav ACL souboru tak, aby na něj měl účet služby konzole čtecí právo.",
            };
        }
    }

    private static async Task<HealthCheck> CheckEnforceAsync(AppDbContext db, CancellationToken ct)
    {
        var enforce = string.Equals(await Get(db, "policy.enforce", ct), "true", StringComparison.OrdinalIgnoreCase);
        return new HealthCheck
        {
            Group = "Whitelist a politika",
            Name = "Vynucování (blokování)",
            // Vypnuté vynucování je legitimní režim (jen varovat), ne chyba – proto Off, ne Bad.
            State = enforce ? HealthState.Ok : HealthState.Off,
            Value = enforce ? "ZAPNUTO — neschválená média se blokují" : "vypnuto — jen se varuje a loguje",
            Detail = "Centrální politika, agenti ji přebírají heartbeatem (do 2 min).",
            Fix = enforce ? "" : "Zapnout jde v Nastavení → Vynucování.",
        };
    }

    // ── Provoz a údržba ──────────────────────────────────────────

    private static async Task<HealthCheck> CheckEmailAsync(AppDbContext db, CancellationToken ct)
    {
        const string name = "E-mailové alerty";
        const string why = "Jediná cesta, jak se o problému dozvíš, aniž bys otevřel konzoli.";

        var enabled = string.Equals(await Get(db, "email.enabled", ct), "true", StringComparison.OrdinalIgnoreCase);
        var host = await Get(db, "email.host", ct);
        var to = await Get(db, "email.recipients", ct);

        if (!enabled)
        {
            return new HealthCheck
            {
                Group = "Provoz a údržba",
                Name = name,
                State = HealthState.Off,
                Value = "vypnuto",
                Detail = why,
                Fix = "Zapni v Nastavení → E-mailové notifikace (jinak se výpadek nikde neohlásí).",
            };
        }

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(to))
        {
            return new HealthCheck
            {
                Group = "Provoz a údržba",
                Name = name,
                State = HealthState.Bad,
                Value = "zapnuto, ale nedokonfigurováno",
                Detail = why,
                Fix = "Doplň SMTP host a příjemce v Nastavení → E-mailové notifikace a pošli test.",
            };
        }

        return new HealthCheck
        {
            Group = "Provoz a údržba",
            Name = name,
            State = HealthState.Ok,
            Value = $"zapnuto → {to}",
            Detail = why,
        };
    }

    private static async Task<HealthCheck> CheckRetentionAsync(AppDbContext db, CancellationToken ct)
    {
        var enabled = string.Equals(await Get(db, "retention.enabled", ct), "true", StringComparison.OrdinalIgnoreCase);
        var last = await Get(db, "retention.lastRun", ct);
        var days = await Get(db, "retention.incidentDays", ct);

        return new HealthCheck
        {
            Group = "Provoz a údržba",
            Name = "Retence dat",
            State = !enabled ? HealthState.Off
                  : string.IsNullOrEmpty(last) ? HealthState.Warn
                  : HealthState.Ok,
            Value = !enabled ? "vypnuto — nic se nemaže"
                  : string.IsNullOrEmpty(last) ? $"zapnuto ({days} dní), ale zatím neproběhla"
                  : $"zapnuto ({days} dní) · {last}",
            Detail = "Mazání starých incidentů (NIS2 – minimalizace dat). Úklid provádí API, ne konzole.",
            Fix = enabled && string.IsNullOrEmpty(last)
                ? "Úklid dělá API — ověř, že běží aktuální verze API (kontrola Verze komponent)."
                : "",
        };
    }

    private HealthCheck CheckAdSync()
    {
        var enabled = _config.GetValue<bool>("AdSync:Enabled");
        return new HealthCheck
        {
            Group = "Provoz a údržba",
            Name = "AD sync",
            State = enabled ? HealthState.Ok : HealthState.Off,
            Value = enabled ? $"zapnuto, každých {_config["AdSync:IntervalMinutes"] ?? "60"} min" : "vypnuto",
            Detail = "Inventář stanic z Active Directory — z něj se počítá, kde chybí agent.",
            Fix = enabled ? "" : "Zapni AdSync:Enabled v appsettings.local.json (vyžaduje restart konzole).",
        };
    }

    private static async Task<HealthCheck> CheckAutoDeployAsync(AppDbContext db, CancellationToken ct)
    {
        var enabled = string.Equals(await Get(db, "deploy.enabled", ct), "true", StringComparison.OrdinalIgnoreCase);
        var dryRun = !string.Equals(await Get(db, "deploy.dryRun", ct), "false", StringComparison.OrdinalIgnoreCase);
        var last = await Get(db, "deploy.lastRun", ct);

        return new HealthCheck
        {
            Group = "Provoz a údržba",
            Name = "Auto-enrollment agenta",
            State = !enabled ? HealthState.Off : dryRun ? HealthState.Warn : HealthState.Ok,
            Value = !enabled ? "vypnuto"
                  : dryRun ? "zapnuto, ale jen DRY-RUN (nic neinstaluje)"
                  : "zapnuto — ostrý režim",
            Detail = string.IsNullOrEmpty(last)
                ? "Automatické nasazování agenta na stanice bez agenta."
                : "Poslední běh: " + last,
            Fix = enabled && dryRun ? "Vypni deploy.dryRun v Nastavení → Auto-enrollment pro ostrý běh." : "",
        };
    }

    private static async Task<HealthCheck> CheckServiceRestartAsync(AppDbContext db, CancellationToken ct)
    {
        var enabled = string.Equals(await Get(db, "svc.restart.enabled", ct), "true", StringComparison.OrdinalIgnoreCase);
        var at = await Get(db, "svc.restart.at", ct);
        var targets = await Get(db, "svc.restart.targets", ct);
        var last = await Get(db, "svc.restart.lastRun", ct);

        var failed = last.Contains("CHYBA", StringComparison.OrdinalIgnoreCase);
        var count = targets.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

        return new HealthCheck
        {
            Group = "Provoz a údržba",
            Name = "Plánovaný restart služeb",
            State = !enabled ? HealthState.Off
                  : failed ? HealthState.Bad
                  : string.IsNullOrEmpty(last) ? HealthState.Warn
                  : HealthState.Ok,
            Value = !enabled ? "vypnuto"
                  : $"zapnuto v {(string.IsNullOrEmpty(at) ? "?" : at)} · {count} služeb",
            Detail = string.IsNullOrEmpty(last)
                ? "Denní restart hlídaných služeb. Zastavenou službu i nastartuje — pojistka proti výpadku typu "
                  + "služba spadla a nikdo si nevšiml."
                : "Poslední běh: " + last,
            Fix = failed
                ? "Poslední běh hlásí chybu — viz text výše (typicky práva účtu konzole na cílovém serveru)."
                : enabled && string.IsNullOrEmpty(last)
                    ? "Zatím neproběhl. Otestuj tlačítkem Restartovat teď v Nastavení."
                    : "",
        };
    }

    private static async Task<HealthCheck> CheckVersionsAsync(AppDbContext db, HealthConfig cfg, CancellationToken ct)
    {
        var agentVersions = await db.Computers
            .Where(c => c.LastSeen != null && c.AgentVersion != "")
            .Select(c => c.AgentVersion)
            .Distinct()
            .ToListAsync(ct);

        var apiCommit = "nenastaveno";
        if (!string.IsNullOrWhiteSpace(cfg.ApiUrl))
        {
            apiCommit = "nedostupné";
            try
            {
                using var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(cfg.ApiTimeoutSeconds) };
                var body = await http.GetStringAsync(cfg.ApiUrl.TrimEnd('/') + "/api/version", ct);
                var parsed = ExtractJsonString(body, "commit");
                if (!string.IsNullOrEmpty(parsed)) apiCommit = parsed;
            }
            catch
            {
                // dostupnost API řeší vlastní kontrola výše – tady jen nevyplníme commit
            }
        }

        var agents = agentVersions.Count == 0 ? "—" : string.Join(", ", agentVersions.OrderBy(v => v));
        return new HealthCheck
        {
            Group = "Provoz a údržba",
            Name = "Verze komponent",
            State = agentVersions.Count > 1 ? HealthState.Warn : HealthState.Ok,
            Value = $"konzole {AppInfo.Commit} · API {apiCommit} · agenti {agents}",
            Detail = "Nasazený commit každé vrstvy. Rozjeté verze agentů znamenají, že někde neproběhl update.",
            Fix = agentVersions.Count > 1 ? "Sjednoť agenty (Stanice → Nasazení)." : "",
        };
    }

    // ── pomocné ──────────────────────────────────────────────────

    private static async Task<string> Get(AppDbContext db, string key, CancellationToken ct)
        => (await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct))?.Value ?? "";

    /// <summary>Vytáhne hodnotu textového pole z ploché JSON odpovědi (bez tahání serializeru kvůli jednomu poli).</summary>
    private static string ExtractJsonString(string json, string field)
    {
        var key = "\"" + field + "\"";
        var idx = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return "";
        var colon = json.IndexOf(':', idx + key.Length);
        if (colon < 0) return "";
        var start = json.IndexOf('"', colon + 1);
        if (start < 0) return "";
        var end = json.IndexOf('"', start + 1);
        return end > start ? json[(start + 1)..end] : "";
    }

    private static string Short(string s, int max = 200)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    private static string Age(TimeSpan t) =>
        t.TotalMinutes < 60 ? $"{t.TotalMinutes:F0} min"
      : t.TotalHours < 48 ? $"{t.TotalHours:F0} h"
      : $"{t.TotalDays:F0} dní";

    private sealed class HealthConfig
    {
        public string ApiUrl = "";
        public int MaxIncidentAgeHours = 48;
        public int SilentAfterMinutes = 180;
        public int ApiTimeoutSeconds = 8;

        public static async Task<HealthConfig> LoadAsync(AppDbContext db, CancellationToken ct)
        {
            var c = new HealthConfig { ApiUrl = await Get(db, "health.apiUrl", ct) };
            if (int.TryParse(await Get(db, "health.maxIncidentAgeHours", ct), out var h) && h > 0)
                c.MaxIncidentAgeHours = h;
            if (int.TryParse(await Get(db, "comm.silentAfterMinutes", ct), out var m) && m > 0)
                c.SilentAfterMinutes = m;
            return c;
        }
    }
}
