// ============================================================
// AdSyncService.cs
// Tenký časovač nad AdSyncRunner – periodicky spouští AD sync, jen když
// je zapnuto v Nastavení (adsync.enabled). On-demand běh (tlačítko
// "Aktualizovat z AD") jde vždy přes stejný AdSyncRunner, nezávisle
// na tomhle přepínači.
//
// NASTAVENÍ (AppSettings, editovatelné v konzoli, nic natvrdo):
//   adsync.enabled          – "true"/"false" (default false)
//   adsync.intervalMinutes  – jak často synchronizovat (default 60, min 5)
//
// PROČ PŘEJITO Z appsettings.local.json (nález 11.09.2026):
//   AdSync:Enabled se dřív četl JEN při startu (Program.cs) – rozhodovalo
//   to, jestli se tahle služba vůbec zaregistruje. Zapnout/vypnout tak
//   šlo jen úpravou souboru na serveru + restart konzole, ne z Nastavení
//   jako všechny ostatní přepínače. Stejný vzor jako BetaRolloutService/
//   AgentDeployService: služba běží vždy, příznak z DB čte při každém tiku.
// ============================================================

using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;

namespace USBGuardian.Admin.AdSync;

public class AdSyncService : BackgroundService
{
    private readonly AdSyncRunner _runner;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<AdSyncService> _logger;

    private static readonly TimeSpan MinInterval     = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(60);

    public AdSyncService(AdSyncRunner runner, IDbContextFactory<AppDbContext> dbFactory,
                          ILogger<AdSyncService> logger)
    {
        _runner    = runner;
        _dbFactory = dbFactory;
        _logger    = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Nechat konzoli naběhnout (DB, config) – stejný vzor jako u ostatních hlídačů.
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = DefaultInterval;
            try { interval = await TickAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "AD sync (časovač): kontrola selhala"); }

            try { await Task.Delay(interval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task<TimeSpan> TickAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        async Task<string> Get(string k) =>
            (await db.AppSettings.FirstOrDefaultAsync(s => s.Key == k, ct))?.Value ?? "";

        var interval = int.TryParse(await Get("adsync.intervalMinutes"), out var m) && m >= MinInterval.TotalMinutes
            ? TimeSpan.FromMinutes(m) : DefaultInterval;

        if (!string.Equals(await Get("adsync.enabled"), "true", StringComparison.OrdinalIgnoreCase))
            return interval;

        await _runner.RunOnceAsync(ct);
        return interval;
    }
}
