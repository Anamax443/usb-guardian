// ============================================================
// HealthService.cs
// Kontroly "funguje všechno, jak má?" pro serverovou konzoli.
//
// PROČ TO EXISTUJE:
//   28.08.2026 se ukázalo, že API služba na SQL boxu byla 6 týdnů
//   zastavená. Agent běžel, incidenty si ukládal do fronty, ale na
//   server nedotekly. Konzole to nikde neřekla nahlas — dlaždice
//   "Zmlklo agentů" ukazovala 1 a nikdo se nekoukl. Tahle třída dělá
//   z tichého selhání hlasité: každá vrstva má vlastní kontrolu
//   s vlastním verdiktem.
//
// SEZNAM JE ZDROJ PRAVDY:
//   Kontroly jsou v jednom poli `Defs` — a to samé pole slouží stránce
//   k vypsání seznamu DŘÍV, než se cokoli spustí. Díky tomu je vidět,
//   co se bude kontrolovat, a jednotlivé položky se pak odškrtávají.
//   Kdyby byl seznam pro UI opsaný zvlášť, rozešel by se s tím, co se
//   doopravdy počítá.
//
// KE KAŽDÉ KONTROLE PATŘÍ VĚTA "PROČ":
//   Kontrola, u které není vidět, jaké rozhodnutí hlídá, se při první
//   nepohodlné změně smaže.
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
// Výjimka uvnitř jedné kontroly je JEJÍ výsledek, ne konec běhu —
// jinak by jedna rozbitá kontrola schovala všechny ostatní.
// ============================================================

using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;

namespace USBGuardian.Admin.Health;

public enum HealthState { Ok, Warn, Bad, Off, Unknown }

/// <summary>Položka seznamu kontrol – zná se PŘED během, aby šla vypsat a odškrtávat.</summary>
public sealed class CheckPlanItem
{
    public string Group { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>Proč tahle kontrola existuje – jaké rozhodnutí hlídá.</summary>
    public string Why { get; init; } = "";
}

/// <summary>Co kontrola naměřila.</summary>
public sealed record CheckOutcome(HealthState State, string Value, string Fix = "");

public sealed class HealthCheck
{
    public string Group { get; init; } = "";
    public string Name { get; init; } = "";
    public string Why { get; init; } = "";
    public HealthState State { get; init; }
    /// <summary>Krátká naměřená hodnota (co kontrola viděla).</summary>
    public string Value { get; init; } = "";
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
    private const string GData = "Sběr dat";
    private const string GWhitelist = "Whitelist a politika";
    private const string GOps = "Provoz a údržba";

    /// <summary>Kontext jednoho běhu – co všechny kontroly sdílejí.</summary>
    private sealed class Ctx
    {
        public AppDbContext? Db;
        public HealthConfig Cfg = new();
        public IConfiguration Config = null!;
    }

    private sealed record Def(
        string Group,
        string Name,
        string Why,
        bool NeedsDb,
        Func<Ctx, CancellationToken, Task<CheckOutcome>> Run);

    // ── SEZNAM KONTROL – jediný zdroj pravdy pro běh i pro výpis ──────────
    private static readonly Def[] Defs =
    {
        new(GData, "Databáze",
            "Konzole čte databázi USBGuardian. Bez ní není vidět nic.",
            true, CheckDatabaseAsync),

        new(GData, "API pro agenty",
            "Agenti sem posílají incidenty a odsud si berou whitelist a politiku. "
          + "Když API stojí, agent si data ukládá do fronty na disku a server je slepý.",
            false, CheckApiAsync),

        new(GData, "Přítok incidentů",
            "Stáří nejnovějšího incidentu v databázi. Když roste, agenti sice mohou běžet, "
          + "ale jejich hlášení nedotečou (výpadek API, sítě nebo služby agenta).",
            true, CheckIncidentFlowAsync),

        new(GData, "Zmlklí agenti",
            "Stanice, které už agenta hlásily, ale déle než je práh se neozvaly. "
          + "Může jít o vypnuté PC, ale i o zastavenou službu nebo zásah uživatele.",
            true, CheckAgentsSilentAsync),

        new(GData, "Pokrytí stanic",
            "Kolik stanic z Active Directory má nainstalovaného agenta. "
          + "Nepokrytá stanice není monitorovaná — pro NIS2 je to díra v evidenci.",
            true, CheckCoverageAsync),

        new(GWhitelist, "Publikovaný whitelist",
            "Agent bere jako platnou jen PODEPSANOU a NEPROŠLOU verzi katalogu. "
          + "Nepodepsaná nebo prošlá verze znamená, že se schválená média k agentům nedostanou.",
            true, CheckWhitelistVersionAsync),

        new(GWhitelist, "Katalog vs. publikace",
            "Jestli se od poslední publikace nezměnil katalog schválených médií. "
          + "Nepublikovaná změna se k agentům nedostane.",
            true, CheckWhitelistCatalogFreshAsync),

        new(GWhitelist, "Podpisový klíč whitelistu",
            "Privátní RSA klíč, kterým konzole podepisuje vydané verze whitelistu. "
          + "Bez něj vznikne nepodepsaná verze, kterou agent odmítne.",
            false, CheckSigningKeyAsync),

        new(GWhitelist, "Vynucování (blokování)",
            "Centrální politika, agenti ji přebírají heartbeatem (do 2 min). "
          + "Vypnuté vynucování je legitimní režim, ale musí být vidět, že se jen varuje.",
            true, CheckEnforceAsync),

        new(GOps, "E-mailové alerty",
            "Jediná cesta, jak se o problému dozvíš, aniž bys otevřel konzoli.",
            true, CheckEmailAsync),

        new(GOps, "Retence dat",
            "Mazání starých incidentů (NIS2 – minimalizace dat). Úklid provádí API, ne konzole.",
            true, CheckRetentionAsync),

        new(GOps, "AD sync",
            "Inventář stanic z Active Directory — z něj se počítá, kde chybí agent.",
            false, CheckAdSyncAsync),

        new(GOps, "Auto-enrollment agenta",
            "Automatické nasazování agenta na stanice bez agenta.",
            true, CheckAutoDeployAsync),

        new(GOps, "Plánovaný restart služeb",
            "Denní restart hlídaných služeb. Zastavenou službu i nastartuje — pojistka proti "
          + "výpadku typu služba spadla a nikdo si nevšiml.",
            true, CheckServiceRestartAsync),

        new(GOps, "Verze komponent",
            "Nasazený commit každé vrstvy. Rozjeté verze agentů znamenají, že někde neproběhl update.",
            true, CheckVersionsAsync),
    };

    /// <summary>Seznam kontrol pro stránku – zná se před během, aby šly odškrtávat.</summary>
    public static IReadOnlyList<CheckPlanItem> Plan { get; } =
        Defs.Select(d => new CheckPlanItem { Group = d.Group, Name = d.Name, Why = d.Why }).ToArray();

    public static int TotalChecks => Defs.Length;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IConfiguration _config;

    public HealthService(IDbContextFactory<AppDbContext> dbFactory, IConfiguration config)
    {
        _dbFactory = dbFactory;
        _config = config;
    }

    /// <param name="progress">
    /// Hlásí každou hotovou kontrolu hned, jak doběhne. Stránka je díky tomu
    /// odškrtává jednu po druhé místo toho, aby několik vteřin mlčela — dotaz
    /// na API má timeout až 8 s.
    /// </param>
    public async Task<HealthReport> RunAsync(IProgress<HealthCheck>? progress = null,
                                             CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var checks = new List<HealthCheck>();
        var ctx = new Ctx { Config = _config };

        string? dbError = null;
        try
        {
            ctx.Db = await _dbFactory.CreateDbContextAsync(ct);
            ctx.Cfg = await HealthConfig.LoadAsync(ctx.Db, ct);
        }
        catch (Exception ex)
        {
            ctx.Db?.Dispose();
            ctx.Db = null;
            dbError = Short(ex.Message);
        }

        try
        {
            foreach (var def in Defs)
            {
                ct.ThrowIfCancellationRequested();

                CheckOutcome outcome;
                if (def.NeedsDb && ctx.Db is null)
                {
                    // Databáze nejede – kontroly nad ní se nemají o co opřít.
                    // Kontrola "Databáze" si vlastní hlášku vyrobí sama níž.
                    outcome = def.Name == "Databáze"
                        ? new CheckOutcome(HealthState.Bad, "nedostupná",
                            "Ověř běh SQL Serveru a connection string v appsettings.local.json; "
                          + "účet konzole potřebuje práva na databázi USBGuardian. Chyba: " + dbError)
                        : new CheckOutcome(HealthState.Unknown, "nelze ověřit – databáze nedostupná");
                }
                else
                {
                    try { outcome = await def.Run(ctx, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // Spadlá kontrola je JEJÍ výsledek, ne konec běhu.
                        outcome = new CheckOutcome(HealthState.Bad, "kontrola spadla",
                            "Výjimka: " + Short(ex.Message));
                    }
                }

                var check = new HealthCheck
                {
                    Group = def.Group,
                    Name = def.Name,
                    Why = def.Why,
                    State = outcome.State,
                    Value = outcome.Value,
                    Fix = outcome.Fix,
                };
                checks.Add(check);
                progress?.Report(check);

                // Kontroly samotné jsou většinou hotové do pár milisekund a celý běh
                // by problikl — a co problikne, to není vidět. Prodleva je tedy kvůli
                // ČITELNOSTI: každý krok se stihne odškrtnout před očima.
                // Platí JEN pro běh ve stránce (je připojený progress). Strojové
                // /api/health, na které se ptá dohled, běží naplno bez zdržování.
                if (progress is not null && ctx.Cfg.StepDelayMs > 0 && def != Defs[^1])
                    await Task.Delay(ctx.Cfg.StepDelayMs, ct);
            }
        }
        finally
        {
            if (ctx.Db is not null) await ctx.Db.DisposeAsync();
        }

        sw.Stop();
        return new HealthReport { Checks = checks, Duration = sw.Elapsed };
    }

    // ── Sběr dat ─────────────────────────────────────────────────

    private static async Task<CheckOutcome> CheckDatabaseAsync(Ctx c, CancellationToken ct)
    {
        var incidents = await c.Db!.Incidents.CountAsync(ct);
        var computers = await c.Db.Computers.CountAsync(ct);
        return new CheckOutcome(HealthState.Ok, $"{incidents} incidentů · {computers} stanic");
    }

    /// <summary>Přesně ten výpadek z 28.08.2026: API služba stojí → agenti nemají kam reportovat.</summary>
    private static async Task<CheckOutcome> CheckApiAsync(Ctx c, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(c.Cfg.ApiUrl))
        {
            return new CheckOutcome(HealthState.Off, "nenastaveno",
                "Doplň adresu API v Nastavení → Kontroly stavu, např. https://SQL-SERVER:5443.");
        }

        var url = c.Cfg.ApiUrl.TrimEnd('/') + "/api/version";
        var sw = Stopwatch.StartNew();
        try
        {
            using var http = NewHttpClient(c.Cfg);
            var resp = await http.GetAsync(url, ct);
            sw.Stop();

            if (!resp.IsSuccessStatusCode)
            {
                return new CheckOutcome(HealthState.Bad, $"HTTP {(int)resp.StatusCode} z {url}",
                    "Zkontroluj log API služby na jejím serveru (Event Log → Application).");
            }

            var body = (await resp.Content.ReadAsStringAsync(ct)).Trim();
            return new CheckOutcome(HealthState.Ok,
                $"odpovídá ({sw.ElapsedMilliseconds} ms) · {Short(body, 120)}");
        }
        catch (Exception ex)
        {
            return new CheckOutcome(HealthState.Bad, $"NEDOSTUPNÉ – {url}: {Short(ex.Message, 120)}",
                "Na serveru s API nastartuj službu API a ověř, že má START_TYPE = AUTO_START "
              + "(jinak po restartu serveru nenaběhne). Pojistkou je Nastavení → Plánovaný restart služeb.");
        }
    }

    /// <summary>Přitékají vůbec nová data? Tichý výpadek pozná jen tahle kontrola.</summary>
    private static async Task<CheckOutcome> CheckIncidentFlowAsync(Ctx c, CancellationToken ct)
    {
        var reporting = await c.Db!.Computers.CountAsync(x => x.LastSeen != null, ct);
        if (reporting == 0)
        {
            return new CheckOutcome(HealthState.Unknown, "žádný agent zatím nereportoval",
                "Nasaď agenta aspoň na jednu stanici (Stanice → Nasazení).");
        }

        var newest = await c.Db.Incidents.OrderByDescending(i => i.Timestamp)
                                         .Select(i => (DateTime?)i.Timestamp)
                                         .FirstOrDefaultAsync(ct);
        if (newest is null)
            return new CheckOutcome(HealthState.Unknown, "zatím žádný incident");

        var age = DateTime.UtcNow - newest.Value;
        var state = age.TotalHours > c.Cfg.MaxIncidentAgeHours ? HealthState.Bad
                  : age.TotalHours > c.Cfg.MaxIncidentAgeHours / 2.0 ? HealthState.Warn
                  : HealthState.Ok;

        return new CheckOutcome(state,
            $"poslední před {Age(age)} ({newest.Value.ToLocalTime():dd.MM.yyyy HH:mm}), práh {c.Cfg.MaxIncidentAgeHours} h",
            state == HealthState.Ok ? ""
                : "Projdi kontroly API pro agenty a Zmlklí agenti. Pozor: klidný provoz "
                + "(nikdo nepřipojil médium) vypadá stejně — práh nastav podle reality v Nastavení → Kontroly stavu.");
    }

    private static async Task<CheckOutcome> CheckAgentsSilentAsync(Ctx c, CancellationToken ct)
    {
        var reporting = await c.Db!.Computers.CountAsync(x => x.LastSeen != null, ct);
        if (reporting == 0)
            return new CheckOutcome(HealthState.Unknown, "žádný agent zatím nereportoval");

        var limit = DateTime.UtcNow.AddMinutes(-c.Cfg.SilentAfterMinutes);
        var silent = await c.Db.Computers.CountAsync(x => x.LastSeen != null && x.LastSeen < limit, ct);

        return new CheckOutcome(
            silent == 0 ? HealthState.Ok : silent == reporting ? HealthState.Bad : HealthState.Warn,
            $"{silent} z {reporting} (práh {c.Cfg.SilentAfterMinutes} min)",
            silent == 0 ? ""
                : "Seznam je na stránce Stanice (tečka komunikace). "
                + "Když mlčí VŠECHNY, je problém na serveru, ne na stanicích.");
    }

    private static async Task<CheckOutcome> CheckCoverageAsync(Ctx c, CancellationToken ct)
    {
        var inAd = await c.Db!.Computers.CountAsync(x => x.InActiveDirectory, ct);
        var withAgent = await c.Db.Computers.CountAsync(x => x.InActiveDirectory && x.LastSeen != null, ct);
        var missing = inAd - withAgent;

        if (inAd == 0)
            return new CheckOutcome(HealthState.Unknown, "AD sync zatím neproběhl",
                "Spusť Stanice → Aktualizovat z AD.");

        var pct = withAgent * 100.0 / inAd;
        return new CheckOutcome(missing == 0 ? HealthState.Ok : HealthState.Warn,
            $"{withAgent} z {inAd} ({pct:F0} %) · chybí {missing}",
            missing == 0 ? "" : "Stanice → Nasazení, případně zapni auto-enrollment v Nastavení.");
    }

    // ── Whitelist a politika ─────────────────────────────────────

    private static async Task<CheckOutcome> CheckWhitelistVersionAsync(Ctx c, CancellationToken ct)
    {
        var active = await c.Db!.WhitelistVersions.Where(v => v.IsActive)
                                .OrderByDescending(v => v.IssuedAt)
                                .FirstOrDefaultAsync(ct);
        if (active is null)
            return new CheckOutcome(HealthState.Bad, "žádná aktivní verze", "Whitelist → Publikovat nyní.");

        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(active.Signature)) problems.Add("NENÍ PODEPSANÁ");
        if (active.ValidUntil <= DateTime.UtcNow)
            problems.Add("PROŠLÁ " + active.ValidUntil.ToLocalTime().ToString("dd.MM.yyyy"));

        var daysLeft = (active.ValidUntil - DateTime.UtcNow).TotalDays;
        var state = problems.Count > 0 ? HealthState.Bad
                  : daysLeft < 30 ? HealthState.Warn
                  : HealthState.Ok;

        return new CheckOutcome(state,
            problems.Count > 0
                ? $"{active.Version} — {string.Join(", ", problems)}"
                : $"{active.Version} · platí do {active.ValidUntil.ToLocalTime():dd.MM.yyyy} ({daysLeft:F0} dní)",
            state == HealthState.Ok ? ""
                : "Whitelist → Publikovat nyní (vydá a podepíše novou verzi). "
                + "Když podpis chybí i po publikaci, zkontroluj kontrolu Podpisový klíč whitelistu.");
    }

    private static async Task<CheckOutcome> CheckWhitelistCatalogFreshAsync(Ctx c, CancellationToken ct)
    {
        var active = await c.Db!.WhitelistVersions.Where(v => v.IsActive)
                                .OrderByDescending(v => v.IssuedAt)
                                .FirstOrDefaultAsync(ct);
        var activeDevices = await c.Db.WhitelistDevices.CountAsync(d => d.IsActive, ct);

        if (active is null)
            return new CheckOutcome(HealthState.Unknown,
                $"{activeDevices} aktivních médií, nic nepublikováno", "Whitelist → Publikovat nyní.");

        var changedAfter = await c.Db.WhitelistDevices
            .CountAsync(d => d.IsActive && d.ApprovedAt > active.IssuedAt, ct);

        return new CheckOutcome(changedAfter == 0 ? HealthState.Ok : HealthState.Warn,
            changedAfter == 0
                ? $"{activeDevices} médií, publikováno {active.IssuedAt.ToLocalTime():dd.MM.yyyy HH:mm}"
                : $"{changedAfter} médií schváleno až PO poslední publikaci",
            changedAfter == 0 ? "" : "Whitelist → Publikovat nyní.");
    }

    private static Task<CheckOutcome> CheckSigningKeyAsync(Ctx c, CancellationToken ct)
    {
        var path = c.Config["Whitelist:PrivateKeyPath"];
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(new CheckOutcome(HealthState.Off, "nenastaveno",
                "Doplň Whitelist:PrivateKeyPath do appsettings.local.json na serveru konzole."));
        }

        if (!File.Exists(path))
        {
            return Task.FromResult(new CheckOutcome(HealthState.Bad, $"soubor neexistuje: {path}",
                "Ulož privátní klíč na uvedenou cestu (chraň ho ACL – čte ho jen účet konzole)."));
        }

        try
        {
            using var _ = File.OpenRead(path);
            return Task.FromResult(new CheckOutcome(HealthState.Ok, $"k dispozici ({path})"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new CheckOutcome(HealthState.Bad, "nelze přečíst: " + Short(ex.Message),
                "Uprav ACL souboru tak, aby na něj měl účet služby konzole čtecí právo."));
        }
    }

    private static async Task<CheckOutcome> CheckEnforceAsync(Ctx c, CancellationToken ct)
    {
        var enforce = string.Equals(await Get(c.Db!, "policy.enforce", ct), "true", StringComparison.OrdinalIgnoreCase);
        // Vypnuté vynucování je legitimní režim (jen varovat), ne chyba – proto Off, ne Bad.
        return new CheckOutcome(enforce ? HealthState.Ok : HealthState.Off,
            enforce ? "ZAPNUTO — neschválená média se blokují" : "vypnuto — jen se varuje a loguje",
            enforce ? "" : "Zapnout jde v Nastavení → Vynucování.");
    }

    // ── Provoz a údržba ──────────────────────────────────────────

    private static async Task<CheckOutcome> CheckEmailAsync(Ctx c, CancellationToken ct)
    {
        var enabled = string.Equals(await Get(c.Db!, "email.enabled", ct), "true", StringComparison.OrdinalIgnoreCase);
        var host = await Get(c.Db!, "email.host", ct);
        var to = await Get(c.Db!, "email.recipients", ct);

        if (!enabled)
            return new CheckOutcome(HealthState.Off, "vypnuto",
                "Zapni v Nastavení → E-mailové notifikace (jinak se výpadek nikde neohlásí).");

        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(to))
            return new CheckOutcome(HealthState.Bad, "zapnuto, ale nedokonfigurováno",
                "Doplň SMTP host a příjemce v Nastavení → E-mailové notifikace a pošli test.");

        return new CheckOutcome(HealthState.Ok, $"zapnuto → {to}");
    }

    private static async Task<CheckOutcome> CheckRetentionAsync(Ctx c, CancellationToken ct)
    {
        var enabled = string.Equals(await Get(c.Db!, "retention.enabled", ct), "true", StringComparison.OrdinalIgnoreCase);
        var last = await Get(c.Db!, "retention.lastRun", ct);
        var days = await Get(c.Db!, "retention.incidentDays", ct);

        return new CheckOutcome(
            !enabled ? HealthState.Off : string.IsNullOrEmpty(last) ? HealthState.Warn : HealthState.Ok,
            !enabled ? "vypnuto — nic se nemaže"
                : string.IsNullOrEmpty(last) ? $"zapnuto ({days} dní), ale zatím neproběhla"
                : $"zapnuto ({days} dní) · {last}",
            enabled && string.IsNullOrEmpty(last)
                ? "Úklid dělá API — ověř, že běží aktuální verze API (kontrola Verze komponent)." : "");
    }

    private static Task<CheckOutcome> CheckAdSyncAsync(Ctx c, CancellationToken ct)
    {
        var enabled = c.Config.GetValue<bool>("AdSync:Enabled");
        return Task.FromResult(new CheckOutcome(
            enabled ? HealthState.Ok : HealthState.Off,
            enabled ? $"zapnuto, každých {c.Config["AdSync:IntervalMinutes"] ?? "60"} min" : "vypnuto",
            enabled ? "" : "Zapni AdSync:Enabled v appsettings.local.json (vyžaduje restart konzole)."));
    }

    private static async Task<CheckOutcome> CheckAutoDeployAsync(Ctx c, CancellationToken ct)
    {
        var enabled = string.Equals(await Get(c.Db!, "deploy.enabled", ct), "true", StringComparison.OrdinalIgnoreCase);
        var dryRun = !string.Equals(await Get(c.Db!, "deploy.dryRun", ct), "false", StringComparison.OrdinalIgnoreCase);
        var last = await Get(c.Db!, "deploy.lastRun", ct);

        return new CheckOutcome(
            !enabled ? HealthState.Off : dryRun ? HealthState.Warn : HealthState.Ok,
            (!enabled ? "vypnuto"
                : dryRun ? "zapnuto, ale jen DRY-RUN (nic neinstaluje)"
                : "zapnuto — ostrý režim")
            + (string.IsNullOrEmpty(last) ? "" : " · " + last),
            enabled && dryRun ? "Vypni deploy.dryRun v Nastavení → Auto-enrollment pro ostrý běh." : "");
    }

    private static async Task<CheckOutcome> CheckServiceRestartAsync(Ctx c, CancellationToken ct)
    {
        var enabled = string.Equals(await Get(c.Db!, "svc.restart.enabled", ct), "true", StringComparison.OrdinalIgnoreCase);
        var at = await Get(c.Db!, "svc.restart.at", ct);
        var targets = await Get(c.Db!, "svc.restart.targets", ct);
        var last = await Get(c.Db!, "svc.restart.lastRun", ct);

        var failed = last.Contains("CHYBA", StringComparison.OrdinalIgnoreCase);
        var count = targets.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;

        return new CheckOutcome(
            !enabled ? HealthState.Off
                : failed ? HealthState.Bad
                : string.IsNullOrEmpty(last) ? HealthState.Warn
                : HealthState.Ok,
            (!enabled ? "vypnuto" : $"zapnuto v {(string.IsNullOrEmpty(at) ? "?" : at)} · {count} služeb")
            + (string.IsNullOrEmpty(last) ? "" : " · " + last),
            failed ? "Poslední běh hlásí chybu — viz text výše (typicky práva účtu konzole na cílovém serveru)."
                : enabled && string.IsNullOrEmpty(last)
                    ? "Zatím neproběhl. Otestuj tlačítkem Restartovat teď v Nastavení." : "");
    }

    private static async Task<CheckOutcome> CheckVersionsAsync(Ctx c, CancellationToken ct)
    {
        var agentVersions = await c.Db!.Computers
            .Where(x => x.LastSeen != null && x.AgentVersion != "")
            .Select(x => x.AgentVersion)
            .Distinct()
            .ToListAsync(ct);

        var apiCommit = "nenastaveno";
        if (!string.IsNullOrWhiteSpace(c.Cfg.ApiUrl))
        {
            apiCommit = "nedostupné";
            try
            {
                using var http = NewHttpClient(c.Cfg);
                var body = await http.GetStringAsync(c.Cfg.ApiUrl.TrimEnd('/') + "/api/version", ct);
                var parsed = ExtractJsonString(body, "commit");
                if (!string.IsNullOrEmpty(parsed)) apiCommit = parsed;
            }
            catch
            {
                // dostupnost API řeší vlastní kontrola výše – tady jen nevyplníme commit
            }
        }

        var agents = agentVersions.Count == 0 ? "—" : string.Join(", ", agentVersions.OrderBy(v => v));
        return new CheckOutcome(agentVersions.Count > 1 ? HealthState.Warn : HealthState.Ok,
            $"konzole {AppInfo.Commit} · API {apiCommit} · agenti {agents}",
            agentVersions.Count > 1 ? "Sjednoť agenty (Stanice → Nasazení)." : "");
    }

    // ── pomocné ──────────────────────────────────────────────────

    /// <summary>
    /// Self-signed cert API je záměr (agent ho ověřuje pinningem otisku);
    /// kontrola řeší JEN dostupnost, proto validaci certu nevyžaduje.
    /// </summary>
    private static HttpClient NewHttpClient(HealthConfig cfg)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(cfg.ApiTimeoutSeconds),
        };
    }

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
        /// <summary>Prodleva mezi kontrolami při běhu ve stránce (ms) – viz komentář v RunAsync.</summary>
        public int StepDelayMs = 300;

        public static async Task<HealthConfig> LoadAsync(AppDbContext db, CancellationToken ct)
        {
            var c = new HealthConfig { ApiUrl = await Get(db, "health.apiUrl", ct) };
            if (int.TryParse(await Get(db, "health.maxIncidentAgeHours", ct), out var h) && h > 0)
                c.MaxIncidentAgeHours = h;
            if (int.TryParse(await Get(db, "comm.silentAfterMinutes", ct), out var m) && m > 0)
                c.SilentAfterMinutes = m;
            if (int.TryParse(await Get(db, "health.stepDelayMs", ct), out var d) && d >= 0)
                c.StepDelayMs = Math.Min(2000, d);   // strop, ať se z kontrol nestane čekání
            return c;
        }
    }
}
