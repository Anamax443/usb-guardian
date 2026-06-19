// ============================================================
// RetentionService.cs
// Pravidelný úklid starých incidentů dle CENTRÁLNÍHO nastavení (AppSettings,
// spravuje konzole v Nastavení → Retence dat):
//   retention.enabled      = true | false
//   retention.incidentDays = N   (uchovat posledních N dní)
//
// Mazání dělá API – jako jediné má na DB delete práva (db_datawriter, gMSA).
// Po běhu zapíše retention.lastRun (vidět v konzoli). Interval: 6 h + 2 min po startu.
// ============================================================

using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;
using USBGuardian.Api.Models;

namespace USBGuardian.Api.Retention;

public class RetentionService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<RetentionService> _log;

    public RetentionService(IServiceScopeFactory scopes, ILogger<RetentionService> log)
    {
        _scopes = scopes;
        _log    = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // krátké zpoždění po startu (EnsureCreated/migrace, prohřátí)
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await RunOnceAsync(ct); }
            catch (Exception ex) { _log.LogError(ex, "Retence: chyba při úklidu incidentů"); }

            try { await Task.Delay(Interval, ct); } catch { break; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var enabled = string.Equals(await GetAsync(db, "retention.enabled", ct),
                                    "true", StringComparison.OrdinalIgnoreCase);
        if (!enabled) return;

        if (!int.TryParse(await GetAsync(db, "retention.incidentDays", ct), out var days) || days < 1)
        {
            _log.LogWarning("Retence: zapnuto, ale retention.incidentDays není platné číslo – přeskočeno.");
            return;
        }

        var cutoff  = DateTime.UtcNow.AddDays(-days);
        var deleted = await db.Incidents.Where(i => i.Timestamp < cutoff).ExecuteDeleteAsync(ct);

        await SetAsync(db, "retention.lastRun",
            $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC – smazáno {deleted} (limit {days} dní)", ct);

        if (deleted > 0)
            _log.LogInformation("Retence: smazáno {Count} incidentů starších {Days} dní", deleted, days);
    }

    private static async Task<string> GetAsync(AppDbContext db, string key, CancellationToken ct)
        => (await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct))?.Value ?? "";

    private static async Task SetAsync(AppDbContext db, string key, string value, CancellationToken ct)
    {
        var s = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (s is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else           s.Value = value;
        await db.SaveChangesAsync(ct);
    }
}
