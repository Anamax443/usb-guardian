// ============================================================
// AccessCache.cs
// Cache DB-spravovaného seznamu přístupu do konzole (uživatelé/skupiny).
// Konfigurace v appsettings (AdminGroups/AllowedUsers) zůstává BOOTSTRAP,
// který nelze vyřadit (ochrana proti lockoutu) – tento seznam ho jen rozšiřuje.
// Klíče v AppSettings: access.users, access.groups (CSV / řádky).
// ============================================================

using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;

namespace USBGuardian.Admin.Security;

public class AccessCache
{
    private readonly IDbContextFactory<AppDbContext> _factory;

    public AccessCache(IDbContextFactory<AppDbContext> factory) => _factory = factory;

    public string[] Users  { get; private set; } = Array.Empty<string>();
    public string[] Groups { get; private set; } = Array.Empty<string>();

    public async Task ReloadAsync()
    {
        try
        {
            await using var db = await _factory.CreateDbContextAsync();
            var all = await db.AppSettings.ToListAsync();
            Users  = Split(all.FirstOrDefault(s => s.Key == "access.users")?.Value);
            Groups = Split(all.FirstOrDefault(s => s.Key == "access.groups")?.Value);
        }
        catch
        {
            // tabulka ještě neexistuje / DB nedostupná → ponech poslední (config bootstrap stačí)
        }
    }

    private static string[] Split(string? s) =>
        string.IsNullOrWhiteSpace(s)
            ? Array.Empty<string>()
            : s.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
