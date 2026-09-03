// ============================================================
// AgentDeployService.cs
// Auto-enrollment: konzole (.213) periodicky najde stanice z AD
// BEZ agenta. Sama je NEINSTALUJE (least-privilege – konzole nemá
// admin na klientech). Místo toho zapíše seznam do targets souboru;
// vlastní instalaci provede scheduled task na .213 pod dedikovaným
// deploy účtem (viz docs/auto-deploy-setup.md).
//
// BEZPEČNOST:
//   - deploy.enabled  – master vypínač (default FALSE).
//   - deploy.dryRun   – default TRUE: jen REPORTUJE koho by nasadil
//                       (ping reachability), targets soubor nepíše.
//   - deploy.allowHosts – volitelný allowlist (CSV hostname); prázdné = všechny bez agenta.
//   - deploy.maxPerRun  – throttle počtu stanic na jeden běh.
//   - deploy.targetsFile – kam zapsat cíle (čte je scheduled task).
//   - Souhrn posledního běhu → AppSettings deploy.lastRun (pro UI).
// ============================================================

using System.Net.NetworkInformation;
using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;
using USBGuardian.Api.Models;

namespace USBGuardian.Admin.Deploy;

public class AgentDeployService : BackgroundService
{
    private readonly ILogger<AgentDeployService> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public AgentDeployService(ILogger<AgentDeployService> logger, IDbContextFactory<AppDbContext> dbFactory)
    {
        _logger    = logger;
        _dbFactory = dbFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = 30;
            try { interval = await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Auto-deploy běh selhal"); }
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(5, interval)), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var cfg = await DeployConfig.LoadAsync(db);

        if (!cfg.Enabled)
            return cfg.IntervalMinutes;

        // Stanice z AD bez agenta (nikdy se neozvaly).
        // DeployIgnored se filtruje UŽ V DOTAZU: trvale vyřazená stanice se nesmí
        // objevit mezi cíli ani omylem, ani přes include seznam.
        var targets = await db.Computers
            .Where(c => c.InActiveDirectory && c.LastSeen == null && c.AgentVersion == ""
                        && !c.DeployIgnored)
            .Select(c => c.Hostname)
            .ToListAsync(ct);

        if (cfg.AllowHosts.Count > 0)
            targets = targets.Where(h => cfg.AllowHosts.Contains(h, StringComparer.OrdinalIgnoreCase)).ToList();

        // Default (deploy.defaultEnroll pro nové PC) + per-stanice výjimky (spravované v Stanicích):
        // include => nasadit, exclude => nenasadit, jinak default.
        targets = targets.Where(h =>
            cfg.IncludeHosts.Contains(h, StringComparer.OrdinalIgnoreCase) ? true :
            cfg.ExcludeHosts.Contains(h, StringComparer.OrdinalIgnoreCase) ? false :
            cfg.DefaultEnroll).ToList();

        targets = targets.Take(Math.Max(1, cfg.MaxPerRun)).ToList();

        if (targets.Count == 0)
        {
            await SaveSummaryAsync(db, $"{Stamp()} – žádné stanice k nasazení.", ct);
            return cfg.IntervalMinutes;
        }

        var summary = cfg.DryRun
            ? await DryRunAsync(targets, ct)
            : await WriteTargetsAsync(cfg, targets);

        await SaveSummaryAsync(db, summary, ct);
        return cfg.IntervalMinutes;
    }

    // Dry-run: jen reachability (ping), nic nepíše – bezpečné jako read-only.
    private async Task<string> DryRunAsync(List<string> targets, CancellationToken ct)
    {
        var reachable = 0;
        foreach (var h in targets)
        {
            ct.ThrowIfCancellationRequested();
            if (await PingAsync(h)) reachable++;
        }
        var msg = $"{Stamp()} DRY-RUN – {targets.Count} stanic bez agenta, {reachable} dostupných (nasadilo by se). " +
                  "Zapni deploy.dryRun=false pro ostrý běh.";
        _logger.LogInformation("Auto-deploy {Msg}", msg);
        return msg;
    }

    // Ostrý běh: zapíše cíle do souboru; instalaci provede scheduled task pod deploy účtem.
    private async Task<string> WriteTargetsAsync(DeployConfig cfg, List<string> targets)
    {
        if (string.IsNullOrWhiteSpace(cfg.TargetsFile))
            return $"{Stamp()} CHYBA: deploy.targetsFile nenastaven (kam zapsat cíle pro deploy task).";
        try
        {
            var dir = Path.GetDirectoryName(cfg.TargetsFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllLinesAsync(cfg.TargetsFile, targets);
            var msg = $"{Stamp()} zapsáno {targets.Count} cílů do {cfg.TargetsFile}; instalaci provede scheduled task (deploy účet).";
            _logger.LogInformation("Auto-deploy {Msg}", msg);
            return msg;
        }
        catch (Exception ex)
        {
            return $"{Stamp()} CHYBA zápisu targets souboru: {ex.Message}";
        }
    }

    private static async Task<bool> PingAsync(string host)
    {
        try { using var p = new Ping(); return (await p.SendPingAsync(host, 1500)).Status == IPStatus.Success; }
        catch { return false; }
    }

    private static string Stamp() => DateTime.Now.ToString("HH:mm:ss");

    private static async Task SaveSummaryAsync(AppDbContext db, string summary, CancellationToken ct)
    {
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "deploy.lastRun", ct);
        if (row is null) db.AppSettings.Add(new AppSetting { Key = "deploy.lastRun", Value = summary });
        else             row.Value = summary;
        await db.SaveChangesAsync(ct);
    }

    // ── konfigurace z AppSettings ────────────────────────────────
    private sealed class DeployConfig
    {
        public bool Enabled;
        public bool DryRun = true;
        public int  IntervalMinutes = 30;
        public int  MaxPerRun = 10;
        public string TargetsFile = "";
        public bool DefaultEnroll = true;        // deploy.defaultEnroll – výchozí pro nové PC
        public List<string> AllowHosts = new();
        public List<string> IncludeHosts = new();
        public List<string> ExcludeHosts = new();

        public static async Task<DeployConfig> LoadAsync(AppDbContext db)
        {
            async Task<string> Get(string k) =>
                (await db.AppSettings.FirstOrDefaultAsync(s => s.Key == k))?.Value ?? "";

            var c = new DeployConfig
            {
                Enabled       = string.Equals(await Get("deploy.enabled"), "true", StringComparison.OrdinalIgnoreCase),
                DryRun        = !string.Equals(await Get("deploy.dryRun"),  "false", StringComparison.OrdinalIgnoreCase),
                DefaultEnroll = !string.Equals(await Get("deploy.defaultEnroll"), "false", StringComparison.OrdinalIgnoreCase), // default ON
                TargetsFile   = await Get("deploy.targetsFile"),
            };
            if (int.TryParse(await Get("deploy.intervalMinutes"), out var iv) && iv > 0) c.IntervalMinutes = iv;
            if (int.TryParse(await Get("deploy.maxPerRun"), out var mp) && mp > 0)       c.MaxPerRun = mp;

            static List<string> Split(string v) => v.Split(new[] { ',', ';', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var allow = await Get("deploy.allowHosts");
            if (!string.IsNullOrWhiteSpace(allow)) c.AllowHosts = Split(allow);
            var incl = await Get("deploy.includeHosts");
            if (!string.IsNullOrWhiteSpace(incl)) c.IncludeHosts = Split(incl);
            var excl = await Get("deploy.excludeHosts");
            if (!string.IsNullOrWhiteSpace(excl)) c.ExcludeHosts = Split(excl);
            return c;
        }
    }
}
