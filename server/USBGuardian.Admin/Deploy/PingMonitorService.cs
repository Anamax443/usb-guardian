// ============================================================
// PingMonitorService.cs
// Pravidelně ověřuje síťovou dostupnost (ping) stanic, které hlásí agenta,
// ale dávno se neozvaly – nezávisle na tom, jestli s nimi někdo zrovna
// kouká do konzole. Bez tohohle byl ping jen ruční tlačítko ("Ověřit
// dostupnost") a "Zmlklo agentů" tak nemohlo rozlišit vypnuté PC od
// reálně spadlého agenta (viz Computers.razor Silent/ProbablyOff).
//
// Pinguje ZÁMĚRNĚ jen kandidáty (hlásí agenta, není čerstvý) – čerstvě
// komunikujícím stanicím ping nic nepřidá a je jich naprostá většina.
// Interval: ping.intervalMinutes (AppSettings), default 5 min.
// ============================================================

using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;
using USBGuardian.Api.Models;

namespace USBGuardian.Admin.Deploy;

public class PingMonitorService : BackgroundService
{
    private const int DefaultIntervalMinutes = 5;
    private const int DefaultSilentAfterMinutes = 180; // stejný default jako Computers.razor

    private readonly ILogger<PingMonitorService> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public PingMonitorService(ILogger<PingMonitorService> logger, IDbContextFactory<AppDbContext> dbFactory)
    {
        _logger    = logger;
        _dbFactory = dbFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var intervalMinutes = DefaultIntervalMinutes;
            try { intervalMinutes = await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Ping monitor: běh selhal"); }

            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, intervalMinutes)), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        async Task<string> Get(string k) => (await db.AppSettings.FirstOrDefaultAsync(s => s.Key == k, ct))?.Value ?? "";

        var intervalMinutes = int.TryParse(await Get("ping.intervalMinutes"), out var iv) && iv > 0
            ? iv : DefaultIntervalMinutes;
        var silentAfterMinutes = int.TryParse(await Get("comm.silentAfterMinutes"), out var sm) && sm > 0
            ? sm : DefaultSilentAfterMinutes;

        var cutoff = DateTime.UtcNow.AddMinutes(-silentAfterMinutes);

        // Kandidáti: hlásí agenta (AgentVersion nebo LastSeen), ale nejsou čerství –
        // stejná definice jako Silent()/CommClass() v Computers.razor.
        var candidates = await db.Computers
            .Where(c => (c.AgentVersion != "" || c.LastSeen != null)
                        && (c.LastSeen == null || c.LastSeen < cutoff))
            .ToListAsync(ct);

        if (candidates.Count == 0) return intervalMinutes;

        var results = await Reachability.PingManyAsync(candidates.Select(c => c.Hostname));
        var now = DateTime.UtcNow;
        foreach (var c in candidates)
        {
            if (!results.TryGetValue(c.Hostname, out var ok)) continue;
            c.LastPingOk = ok;
            c.LastPingAt = now;
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Ping monitor: ověřeno {Count} zmlklých stanic ({Reachable} odpovídá)",
            candidates.Count, results.Values.Count(ok => ok));

        return intervalMinutes;
    }
}
