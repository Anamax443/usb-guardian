// ============================================================
// AdSyncService.cs
// Background služba (běží v konzoli na .213): natáhne počítače
// z Active Directory do paměti a zapíše/aktualizuje je v SQL
// tabulce Computers. Reconciliation = co je v AD vs. co reportuje agent.
//
// Best practice:
//   - Enumeruje přímo objectCategory=computer (skupina USB-Guardian-Clients
//     obsahuje vnořeně "Domain Computers", takže group-filter = všechny stroje).
//   - Klíčování na Hostname (ne IP – stanice mají dynamické IP).
//   - Idempotentní upsert; nepřepisuje LastSeen/AgentVersion (to vlastní agent/API).
//   - Self-scheduling smyčka, čte AD pod účtem služby (stačí čtenář domény).
//   - Žádný hardcoded doménový název (SearchBase z configu, default = doména stroje).
// ============================================================

using System.DirectoryServices;
using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;
using USBGuardian.Api.Models;

namespace USBGuardian.Admin.AdSync;

public class AdSyncService : BackgroundService
{
    private readonly ILogger<AdSyncService> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly string _searchBase;
    private readonly int _intervalMinutes;
    private readonly bool _includeDisabled;

    public AdSyncService(
        ILogger<AdSyncService> logger,
        IDbContextFactory<AppDbContext> dbFactory,
        string searchBase,
        int intervalMinutes,
        bool includeDisabled)
    {
        _logger          = logger;
        _dbFactory       = dbFactory;
        _searchBase      = searchBase;
        _intervalMinutes = intervalMinutes < 1 ? 60 : intervalMinutes;
        _includeDisabled = includeDisabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "AD sync spuštěn – interval {Min} min, base '{Base}', disabled={Dis}",
            _intervalMinutes,
            string.IsNullOrWhiteSpace(_searchBase) ? "(doména stroje)" : _searchBase,
            _includeDisabled);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AD sync selhal");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(_intervalMinutes), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    // --------------------------------------------------------
    // Jeden běh: AD → paměť → upsert do Computers
    // --------------------------------------------------------
    private async Task SyncOnceAsync(CancellationToken ct)
    {
        var adComputers = QueryActiveDirectory();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Computers.ToListAsync(ct);
        var byHost   = existing.ToDictionary(c => c.Hostname.ToUpperInvariant());

        var now      = DateTime.UtcNow;
        var seen     = new HashSet<string>();
        int inserted = 0, updated = 0;

        foreach (var ad in adComputers)
        {
            var key = ad.Host.ToUpperInvariant();
            seen.Add(key);

            if (byHost.TryGetValue(key, out var c))
            {
                c.OperatingSystem   = ad.Os;
                if (string.IsNullOrEmpty(c.Domain)) c.Domain = ad.Domain;
                c.InActiveDirectory = true;
                c.AdSyncedAt        = now;
                updated++;
            }
            else
            {
                db.Computers.Add(new Computer
                {
                    Hostname          = ad.Host,
                    Domain            = ad.Domain,
                    OperatingSystem   = ad.Os,
                    InActiveDirectory = true,
                    AdSyncedAt        = now,
                    LastSeen          = null,            // agent zatím nereportoval
                    AgentVersion      = string.Empty,
                    IsActive          = true,
                    CreatedAt         = now
                });
                inserted++;
            }
        }

        // Reconciliation: záznamy dříve v AD, které tam už nejsou
        int gone = 0;
        foreach (var c in existing)
        {
            if (c.InActiveDirectory && !seen.Contains(c.Hostname.ToUpperInvariant()))
            {
                c.InActiveDirectory = false;
                gone++;
            }
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "AD sync hotov: {Total} v AD | +{Ins} nových, {Upd} aktualizováno, {Gone} už není v AD",
            adComputers.Count, inserted, updated, gone);
    }

    // --------------------------------------------------------
    // LDAP dotaz na počítače (ověřeno živě: name/dNSHostName/operatingSystem
    // jsou vyplněné; disabled přes userAccountControl bit 2)
    // --------------------------------------------------------
    private List<AdComputer> QueryActiveDirectory()
    {
        var list = new List<AdComputer>();

        using var root = string.IsNullOrWhiteSpace(_searchBase)
            ? new DirectoryEntry()
            : new DirectoryEntry($"LDAP://{_searchBase}");

        using var searcher = new DirectorySearcher(root)
        {
            Filter = _includeDisabled
                ? "(objectCategory=computer)"
                : "(&(objectCategory=computer)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))",
            PageSize = 1000
        };
        searcher.PropertiesToLoad.AddRange(new[] { "name", "dNSHostName", "operatingSystem" });

        using var results = searcher.FindAll();
        foreach (SearchResult r in results)
        {
            var name = Val(r, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var dns    = Val(r, "dNSHostName");
            var domain = dns.Contains('.') ? dns[(dns.IndexOf('.') + 1)..] : string.Empty;

            list.Add(new AdComputer(name, domain, Val(r, "operatingSystem")));
        }

        return list;
    }

    private static string Val(SearchResult r, string prop)
        => r.Properties.Contains(prop) && r.Properties[prop].Count > 0
            ? r.Properties[prop][0]?.ToString() ?? string.Empty
            : string.Empty;

    private record AdComputer(string Host, string Domain, string Os);
}
