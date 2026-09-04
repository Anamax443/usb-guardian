// ============================================================
// BetaRolloutService.cs
// Automaticky rozveze novou verzi z beta kanálu na schválený vzorek stanic,
// když to operátor neudělá ručně tlačítkem "Rozvézt betu na vzorek".
//
// PROČ:
//   Tlačítko v Nastavení je rychlá cesta, ale pořád vyžaduje, aby si na něj
//   po každém Set-AgentVersion.cmd na serveru někdo vzpomněl. Bez pojistky
//   beta klidně zůstane netknutá dny, i když už je v kanálu nový build –
//   přesně to riziko, co už pro zapadlé služby řeší ServiceRestartService.
//
// CO HLÍDÁ:
//   Fyzický obsah <beta kanál>\VERSION.txt – NE databázový záznam
//   "agent.version.beta" (ten je jen ZÁMĚR, viz Settings.razor/SaveVerze;
//   dokud někdo fyzicky nespustí Set-AgentVersion.cmd na serveru, soubor se
//   nezmění). Když se commit v souboru změní oproti naposledy rozvezenému,
//   spustí se DeployTrigger.SpustBetuAsync sám – přesně to samé, co dělá
//   ruční tlačítko.
//
// NASTAVENÍ (AppSettings, editovatelné v konzoli, nic natvrdo):
//   beta.autoRollout.enabled          – "true"/"false" (default false)
//   beta.autoRollout.intervalMinutes  – jak často kontrolovat (default 30, min 5)
//   beta.autoRollout.lastCommit       – poslední automaticky rozvezený commit
//   beta.autoRollout.lastRun          – souhrn posledního běhu (čte i UI)
//   agent.publishBetaDir              – kde je beta kanál (default C:\Apps\USBGuardianAgentPublishBeta)
// ============================================================

using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;
using USBGuardian.Api.Models;

namespace USBGuardian.Admin.Deploy;

public sealed class BetaRolloutService : BackgroundService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly DeployTrigger _deploy;
    private readonly ILogger<BetaRolloutService> _logger;

    public const string DefaultPublishBetaDir = @"C:\Apps\USBGuardianAgentPublishBeta";
    private static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);

    public BetaRolloutService(IDbContextFactory<AppDbContext> dbFactory, DeployTrigger deploy,
                              ILogger<BetaRolloutService> logger)
    {
        _dbFactory = dbFactory;
        _deploy = deploy;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Nechat konzoli naběhnout (DB, config) – stejný vzor jako u ostatních hlídačů.
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = DefaultInterval;
            try { interval = await TickAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Auto-rozvoz bety: kontrola selhala"); }

            try { await Task.Delay(interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task<TimeSpan> TickAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        async Task<string> Get(string k) => (await db.AppSettings.FirstOrDefaultAsync(s => s.Key == k, ct))?.Value ?? "";

        var interval = int.TryParse(await Get("beta.autoRollout.intervalMinutes"), out var m) && m >= MinInterval.TotalMinutes
            ? TimeSpan.FromMinutes(m) : DefaultInterval;

        if (!string.Equals(await Get("beta.autoRollout.enabled"), "true", StringComparison.OrdinalIgnoreCase))
            return interval;

        var pubBetaDir = await Get("agent.publishBetaDir");
        if (string.IsNullOrWhiteSpace(pubBetaDir)) pubBetaDir = DefaultPublishBetaDir;

        var verzeSoubor = Path.Combine(pubBetaDir, "VERSION.txt");
        if (!File.Exists(verzeSoubor)) return interval;

        string aktualni;
        try
        {
            var prvniRadek = (await File.ReadAllLinesAsync(verzeSoubor, ct)).FirstOrDefault() ?? "";
            aktualni = prvniRadek.Split(' ').FirstOrDefault()?.Trim() ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-rozvoz bety: VERSION.txt ({Soubor}) se nepodařilo přečíst", verzeSoubor);
            return interval;
        }
        if (string.IsNullOrWhiteSpace(aktualni)) return interval;

        var posledni = await Get("beta.autoRollout.lastCommit");
        if (string.Equals(aktualni, posledni, StringComparison.OrdinalIgnoreCase))
            return interval; // od posledniho rozvozu se kanal nezmenil

        var (ok, zprava) = await _deploy.SpustBetuAsync("systém (auto-rozvoz)", ct);

        var stamp = $"{DateTime.Now:dd.MM.yyyy HH:mm}  {aktualni}  " + (ok ? "OK" : "CHYBA: " + zprava);
        await Set(db, "beta.autoRollout.lastRun", stamp, ct);
        if (ok) await Set(db, "beta.autoRollout.lastCommit", aktualni, ct);
        await db.SaveChangesAsync(ct);

        _logger.LogWarning("Auto-rozvoz bety: nový commit {Commit} v kanálu → {Vysledek}",
            aktualni, ok ? "rozvoz spuštěn" : "selhalo: " + zprava);

        return interval;
    }

    private static async Task Set(AppDbContext db, string key, string value, CancellationToken ct)
    {
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
    }
}
