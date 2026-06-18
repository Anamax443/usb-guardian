// ============================================================
// AgentDeployService.cs
// Auto-enrollment: konzole (.213) periodicky najde stanice z AD
// BEZ agenta a (volitelně) na ně agenta vzdáleně nasadí.
//
// BEZPEČNOST:
//   - deploy.enabled  – master vypínač (default FALSE).
//   - deploy.dryRun   – default TRUE: jen REPORTUJE koho by nasadil
//                       (ping reachability), nic nemění.
//   - deploy.allowHosts – volitelný allowlist (CSV hostname); prázdné = všechny bez agenta.
//   - deploy.maxPerRun  – throttle počtu stanic na jeden běh.
//   - Naostro (enabled && !dryRun) spustí scripts\Deploy-AgentFleet.ps1
//     POD IDENTITOU KONZOLE – ta musí mít lokální admin na klientech
//     (dedikovaný deploy účet, viz HANDOFF). Bez práv deploy selže (audit).
//   - Souhrn posledního běhu → AppSettings deploy.lastRun (pro UI).
// ============================================================

using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text;
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
        var targets = await db.Computers
            .Where(c => c.InActiveDirectory && c.LastSeen == null && c.AgentVersion == "")
            .Select(c => c.Hostname)
            .ToListAsync(ct);

        if (cfg.AllowHosts.Count > 0)
            targets = targets.Where(h => cfg.AllowHosts.Contains(h, StringComparer.OrdinalIgnoreCase)).ToList();

        targets = targets.Take(Math.Max(1, cfg.MaxPerRun)).ToList();

        if (targets.Count == 0)
        {
            await SaveSummaryAsync(db, $"{Stamp()} – žádné stanice k nasazení.", ct);
            return cfg.IntervalMinutes;
        }

        var summary = cfg.DryRun
            ? await DryRunAsync(targets, ct)
            : await DeployAsync(cfg, targets, ct);

        await SaveSummaryAsync(db, summary, ct);
        return cfg.IntervalMinutes;
    }

    // Dry-run: jen reachability (ping), nic nemění – bezpečné jako read-only.
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

    // Ostrý běh: spustí fleet skript pod identitou konzole.
    private async Task<string> DeployAsync(DeployConfig cfg, List<string> targets, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cfg.ScriptPath) || !File.Exists(cfg.ScriptPath))
            return $"{Stamp()} CHYBA: deploy.scriptPath nenastaven/nenalezen ({cfg.ScriptPath}).";
        if (string.IsNullOrWhiteSpace(cfg.SourcePath) || !Directory.Exists(cfg.SourcePath))
            return $"{Stamp()} CHYBA: deploy.sourcePath nenastaven/nenalezen ({cfg.SourcePath}).";

        var list = string.Join(",", targets);
        var psi = new ProcessStartInfo
        {
            FileName               = "powershell.exe",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy"); psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");     psi.ArgumentList.Add(cfg.ScriptPath);
        psi.ArgumentList.Add("-Targets");  psi.ArgumentList.Add(list);
        psi.ArgumentList.Add("-SourcePath"); psi.ArgumentList.Add(cfg.SourcePath);
        psi.ArgumentList.Add("-ThrottleLimit"); psi.ArgumentList.Add(cfg.MaxPerRun.ToString());

        _logger.LogInformation("Auto-deploy ostrý běh: {Count} stanic ({List})", targets.Count, list);

        var sb = new StringBuilder();
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            var stderr = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            sb.Append(stdout);
            if (!string.IsNullOrWhiteSpace(stderr)) sb.AppendLine("ERR: " + stderr);
            // Poslední řádky souhrnu skriptu
            var tail = string.Join(" | ", sb.ToString()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.Contains("OK") || l.Contains("FAIL") || l.Contains("OFFLINE") || l.Contains("SKIP"))
                .TakeLast(8));
            var msg = $"{Stamp()} deploy {targets.Count} stanic, exit={p.ExitCode}. {tail}";
            _logger.LogInformation("Auto-deploy {Msg}", msg);
            return msg;
        }
        catch (Exception ex)
        {
            return $"{Stamp()} CHYBA spuštění deploy skriptu: {ex.Message}";
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
        public string ScriptPath = "";
        public string SourcePath = "";
        public List<string> AllowHosts = new();

        public static async Task<DeployConfig> LoadAsync(AppDbContext db)
        {
            async Task<string> Get(string k) =>
                (await db.AppSettings.FirstOrDefaultAsync(s => s.Key == k))?.Value ?? "";

            var c = new DeployConfig
            {
                Enabled    = string.Equals(await Get("deploy.enabled"), "true", StringComparison.OrdinalIgnoreCase),
                DryRun     = !string.Equals(await Get("deploy.dryRun"),  "false", StringComparison.OrdinalIgnoreCase),
                ScriptPath = await Get("deploy.scriptPath"),
                SourcePath = await Get("deploy.sourcePath"),
            };
            if (int.TryParse(await Get("deploy.intervalMinutes"), out var iv) && iv > 0) c.IntervalMinutes = iv;
            if (int.TryParse(await Get("deploy.maxPerRun"), out var mp) && mp > 0)       c.MaxPerRun = mp;
            var allow = await Get("deploy.allowHosts");
            if (!string.IsNullOrWhiteSpace(allow))
                c.AllowHosts = allow.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            return c;
        }
    }
}
