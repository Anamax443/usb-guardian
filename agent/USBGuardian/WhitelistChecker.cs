// ============================================================
// WhitelistChecker.cs
// Načítá whitelist ze souboru a porovnává zařízení.
// Hlídá expiraci whitelistu a offline stav.
// ============================================================

using System.Text.Json;
using Microsoft.Extensions.Logging;
using USBGuardian.Models;

namespace USBGuardian;

public class WhitelistChecker
{
    private readonly ILogger<WhitelistChecker> _logger;
    private readonly string _whitelistPath;

    // Cachovaný whitelist – načte se při startu a po každé změně souboru
    private WhitelistFile? _cachedWhitelist;
    private DateTime _lastLoaded = DateTime.MinValue;

    // Jak dlouho platí cache v paměti (znovu načte ze souboru po X minutách)
    private const int CacheMinutes = 5;

    public WhitelistChecker(ILogger<WhitelistChecker> logger, string whitelistPath)
    {
        _logger = logger;
        _whitelistPath = whitelistPath;
    }

    // --------------------------------------------------------
    // Hlavní metoda – vrátí true pokud je zařízení na whitelistu
    // --------------------------------------------------------
    public bool IsAllowed(DeviceInfo device)
    {
        var whitelist = LoadWhitelist();

        if (whitelist == null)
        {
            // Whitelist soubor chybí nebo je poškozený → fail-safe = varovat
            _logger.LogWarning("Whitelist není dostupný, zařízení {Device} nelze ověřit", device);
            return false;
        }

        // Kontrola expirace whitelistu
        if (whitelist.ValidUntil != DateTime.MinValue && whitelist.ValidUntil < DateTime.UtcNow)
        {
            _logger.LogWarning("Whitelist vypršel {Expired}, pracuji v degraded módu", whitelist.ValidUntil);
            // Pozn: reakce na expiraci řeší PolicyEnforcer dle konfigurace
        }

        // Porovnání zařízení s whitelistem
        foreach (var entry in whitelist.Devices)
        {
            if (MatchesEntry(device, entry))
            {
                _logger.LogDebug("Zařízení {Device} nalezeno na whitelistu: {Description}",
                    device, entry.Description);
                return true;
            }
        }

        _logger.LogInformation("Zařízení {Device} NENÍ na whitelistu", device);
        return false;
    }

    // --------------------------------------------------------
    // Vrátí aktuální verzi whitelistu (pro log incidentu)
    // --------------------------------------------------------
    public string GetVersion()
    {
        return LoadWhitelist()?.Version ?? "unknown";
    }

    // --------------------------------------------------------
    // Vrátí stav whitelistu (platný / expirovaný / chybí)
    // --------------------------------------------------------
    public WhitelistStatus GetStatus()
    {
        var whitelist = LoadWhitelist();

        if (whitelist == null) return WhitelistStatus.Missing;
        if (whitelist.ValidUntil != DateTime.MinValue && whitelist.ValidUntil < DateTime.UtcNow)
            return WhitelistStatus.Expired;

        return WhitelistStatus.Valid;
    }

    // --------------------------------------------------------
    // Interní: porovnání jednoho zařízení s jedním záznamem
    // --------------------------------------------------------
    private bool MatchesEntry(DeviceInfo device, WhitelistEntry entry)
    {
        // VID musí vždy souhlasit
        if (!string.Equals(device.VendorId, entry.VendorId, StringComparison.OrdinalIgnoreCase))
            return false;

        // PID musí vždy souhlasit
        if (!string.Equals(device.ProductId, entry.ProductId, StringComparison.OrdinalIgnoreCase))
            return false;

        // Serial: pokud je v whitelistu prázdný → platí pro celou řadu (wildcard)
        // POZOR: wildcard je bezpečnostní riziko – používat jen pro sdílená zařízení
        if (!string.IsNullOrEmpty(entry.SerialNumber))
        {
            if (!string.Equals(device.SerialNumber, entry.SerialNumber, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    // --------------------------------------------------------
    // Interní: načtení whitelistu ze souboru s cachováním
    // --------------------------------------------------------
    private WhitelistFile? LoadWhitelist()
    {
        // Platná cache → vrátit bez čtení souboru
        if (_cachedWhitelist != null &&
            DateTime.UtcNow - _lastLoaded < TimeSpan.FromMinutes(CacheMinutes))
        {
            return _cachedWhitelist;
        }

        if (!File.Exists(_whitelistPath))
        {
            _logger.LogError("Whitelist soubor nenalezen: {Path}", _whitelistPath);
            return null;
        }

        try
        {
            var json = File.ReadAllText(_whitelistPath);
            _cachedWhitelist = JsonSerializer.Deserialize<WhitelistFile>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            _lastLoaded = DateTime.UtcNow;

            _logger.LogInformation("Whitelist načten: verze {Version}, zařízení: {Count}",
                _cachedWhitelist?.Version, _cachedWhitelist?.Devices.Count);

            return _cachedWhitelist;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chyba při čtení whitelistu: {Path}", _whitelistPath);
            return null;
        }
    }
}

public enum WhitelistStatus
{
    Valid,
    Expired,
    Missing
}
