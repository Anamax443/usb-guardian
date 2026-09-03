// ============================================================
// ActivityLogger.cs
// Zápis do deníku aktivity. Sdílený zdroj – linkuje si ho API i konzole,
// aby obojí psalo do TÉŽE tabulky stejným způsobem a deník se dal číst
// jako jeden příběh, ne jako dva.
//
// ZÁSADA: DENÍK NESMÍ NIC ROZBÍT.
//   Zapisuje se mimo hlavní cestu požadavku (fire-and-forget) a každá
//   chyba se spolkne. Kdyby heartbeat agenta spadl kvůli tomu, že se
//   nepodařilo zapsat řádek do deníku, byl by pozorovatel důležitější
//   než to, co pozoruje.
//
// Proto se taky NEČEKÁ na dokončení zápisu: agentů jsou stovky a jejich
// tep nemá být svázaný s latencí databáze.
// ============================================================

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using USBGuardian.Api.Models;

namespace USBGuardian.Api.Data;

public enum ActivityLevel { Info, Warn, Error }

public sealed class ActivityLogger
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<ActivityLogger> _logger;

    public ActivityLogger(IDbContextFactory<AppDbContext> dbFactory, ILogger<ActivityLogger> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>Zapíše řádek deníku. Nikdy nevyhazuje a na dokončení se nečeká.</summary>
    public void Log(string source, string message,
                    ActivityLevel level = ActivityLevel.Info,
                    string? hostname = null, string? user = null)
    {
        var zaznam = new ActivityEntry
        {
            Timestamp = DateTime.UtcNow,
            Level = level switch
            {
                ActivityLevel.Warn => "warn",
                ActivityLevel.Error => "error",
                _ => "info",
            },
            Source = Zkrat(source, 32),
            Hostname = string.IsNullOrWhiteSpace(hostname) ? null : Zkrat(hostname, 128),
            User = string.IsNullOrWhiteSpace(user) ? null : Zkrat(user, 128),
            Message = Zkrat(message, 1000),
        };

        _ = ZapisAsync(zaznam);
    }

    private async Task ZapisAsync(ActivityEntry zaznam)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            db.ActivityLog.Add(zaznam);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Do vlastního logu jen na Debug: kdyby byla DB dlouho nedostupná,
            // neuděláme si z toho druhý zdroj šumu.
            _logger.LogDebug(ex, "Deník aktivity: řádek se nepodařilo zapsat ({Source})", zaznam.Source);
        }
    }

    private static string Zkrat(string? s, int max)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s[..max];
    }
}
