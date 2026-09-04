// ============================================================
// AdSyncRunner.cs
// Vlastní logika AD syncu – volatelná z časovače (AdSyncService)
// i na vyžádání z UI (tlačítko "Aktualizovat z AD").
// Singleton; běh chráněn semaforem (manuál nepřekryje časovač).
// ============================================================

using System.DirectoryServices;
using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;
using USBGuardian.Api.Models;

namespace USBGuardian.Admin.AdSync;

public class AdSyncRunner
{
    private readonly ILogger<AdSyncRunner> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly string _searchBase;
    private readonly bool _includeDisabled;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DateTime? LastRunUtc { get; private set; }
    public string LastSummary { get; private set; } = "zatím neproběhl";

    public AdSyncRunner(
        ILogger<AdSyncRunner> logger,
        IDbContextFactory<AppDbContext> dbFactory,
        IConfiguration config)
    {
        _logger          = logger;
        _dbFactory       = dbFactory;
        _searchBase      = config["AdSync:SearchBase"] ?? string.Empty;
        _includeDisabled = bool.Parse(config["AdSync:IncludeDisabled"] ?? "false");
    }

    /// <summary>Jeden běh: AD → paměť → upsert do Computers. Vrací shrnutí.</summary>
    public async Task<string> RunOnceAsync(CancellationToken ct = default)
    {
        // Neběžet dvakrát naráz (časovač × manuál)
        if (!await _gate.WaitAsync(0, ct))
            return "AD sync právě běží…";

        try
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
                    c.AdPath            = ad.Path;
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
                        AdPath            = ad.Path,
                        InActiveDirectory = true,
                        AdSyncedAt        = now,
                        LastSeen          = null,
                        AgentVersion      = string.Empty,
                        IsActive          = true,
                        CreatedAt         = now
                    });
                    inserted++;
                }
            }

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

            LastRunUtc  = now;
            LastSummary = $"{adComputers.Count} v AD · +{inserted} nových · {updated} aktualizováno · {gone} už není v AD";
            _logger.LogInformation("AD sync hotov: {Summary}", LastSummary);
            return LastSummary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AD sync selhal");
            LastSummary = "chyba: " + ex.Message;
            return LastSummary;
        }
        finally
        {
            _gate.Release();
        }
    }

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
        searcher.PropertiesToLoad.AddRange(new[] { "name", "dNSHostName", "operatingSystem", "distinguishedName" });

        using var results = searcher.FindAll();
        foreach (SearchResult r in results)
        {
            var name = Val(r, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var dns    = Val(r, "dNSHostName");
            var domain = dns.Contains('.') ? dns[(dns.IndexOf('.') + 1)..] : string.Empty;
            var path   = FriendlyOu(Val(r, "distinguishedName"));

            list.Add(new AdComputer(name, domain, Val(r, "operatingSystem"), path));
        }

        return list;
    }

    // "CN=PC,OU=Computers,OU=AXIMA,DC=domena,DC=loc" → "AXIMA / Computers"
    private static string FriendlyOu(string dn)
    {
        if (string.IsNullOrEmpty(dn)) return string.Empty;
        var ous = dn.Split(',')
            .Select(p => p.Trim())
            .Where(p => p.StartsWith("OU=", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[3..])
            .Reverse();
        return string.Join(" / ", ous);
    }

    private static string Val(SearchResult r, string prop)
        => r.Properties.Contains(prop) && r.Properties[prop].Count > 0
            ? r.Properties[prop][0]?.ToString() ?? string.Empty
            : string.Empty;

    private record AdComputer(string Host, string Domain, string Os, string Path);
}
