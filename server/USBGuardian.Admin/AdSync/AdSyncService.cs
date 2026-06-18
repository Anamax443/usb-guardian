// ============================================================
// AdSyncService.cs
// Tenký časovač nad AdSyncRunner – periodicky spouští AD sync.
// On-demand běh (tlačítko v UI) jde přes stejný AdSyncRunner.
// ============================================================

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace USBGuardian.Admin.AdSync;

public class AdSyncService : BackgroundService
{
    private readonly AdSyncRunner _runner;
    private readonly ILogger<AdSyncService> _logger;
    private readonly int _intervalMinutes;

    public AdSyncService(AdSyncRunner runner, ILogger<AdSyncService> logger, int intervalMinutes)
    {
        _runner          = runner;
        _logger          = logger;
        _intervalMinutes = intervalMinutes < 1 ? 60 : intervalMinutes;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AD sync (časovač) spuštěn – interval {Min} min", _intervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            await _runner.RunOnceAsync(stoppingToken);
            try { await Task.Delay(TimeSpan.FromMinutes(_intervalMinutes), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }
}
