// ============================================================
// WhitelistChecker.cs
// Načítá whitelist ze souboru a porovnává zařízení.
// Hlídá expiraci whitelistu a offline stav.
//
// v1.1 – RSA podpis verifikace:
//   Před načtením whitelistu ověří RSA-SHA256 podpis.
//   Pokud podpis chybí nebo nesouhlasí → whitelist ODMÍTNUT.
//   Konfigurace: SignatureVerificationEnabled v agent.config.json
// ============================================================

using System.Text.Json;
using Microsoft.Extensions.Logging;
using USBGuardian.Models;
using USBGuardian.Security;

namespace USBGuardian;

public class WhitelistChecker
{
    private readonly ILogger<WhitelistChecker> _logger;
    private readonly string _whitelistPath;
    private readonly bool _allowWildcards;
    private readonly bool _signatureVerificationEnabled;
    private readonly SignatureVerifier? _signatureVerifier;

    // Cesta k podpis souboru – vedle whitelistu se stejným názvem + .sig
    private string SignaturePath => _whitelistPath + ".sig";

    // Cachovaný whitelist
    private WhitelistFile? _cachedWhitelist;
    private DateTime _lastLoaded = DateTime.MinValue;
    private const int CacheMinutes = 5;

    public WhitelistChecker(
        ILogger<WhitelistChecker> logger,
        string whitelistPath,
        bool allowWildcards = false,
        bool signatureVerificationEnabled = true,
        SignatureVerifier? signatureVerifier = null)
    {
        _logger                       = logger;
        _whitelistPath                = whitelistPath;
        _allowWildcards               = allowWildcards;
        _signatureVerificationEnabled = signatureVerificationEnabled;
        _signatureVerifier            = signatureVerifier;

        if (_allowWildcards)
            _logger.LogWarning(
                "BEZPEČNOSTNÍ VAROVÁNÍ: AllowWildcards=true – " +
                "záznamy bez sériového čísla jsou povoleny.");

        if (!_signatureVerificationEnabled)
            _logger.LogWarning(
                "BEZPEČNOSTNÍ VAROVÁNÍ: SignatureVerification=false – " +
                "RSA ověření whitelistu je VYPNUTO. " +
                "Povolte v produkci: signing.enabled=true");
    }

    // --------------------------------------------------------
    // Hlavní metoda – vrátí true pokud je zařízení na whitelistu
    // --------------------------------------------------------
    public bool IsAllowed(DeviceInfo device)
    {
        var whitelist = LoadWhitelist();

        if (whitelist == null)
        {
            _logger.LogWarning("Whitelist není dostupný, zařízení {Device} nelze ověřit", device);
            return false;
        }

        if (whitelist.ValidUntil != DateTime.MinValue && whitelist.ValidUntil < DateTime.UtcNow)
            _logger.LogWarning("Whitelist vypršel {Expired}, pracuji v degraded módu", whitelist.ValidUntil);

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

    public string GetVersion()  => LoadWhitelist()?.Version ?? "unknown";

    public WhitelistStatus GetStatus()
    {
        var whitelist = LoadWhitelist();
        if (whitelist == null) return WhitelistStatus.Missing;
        if (whitelist.ValidUntil != DateTime.MinValue && whitelist.ValidUntil < DateTime.UtcNow)
            return WhitelistStatus.Expired;
        return WhitelistStatus.Valid;
    }

    // --------------------------------------------------------
    // Interní: načtení whitelistu se RSA verifikací
    // --------------------------------------------------------
    private WhitelistFile? LoadWhitelist()
    {
        // Platná cache → vrátit bez čtení souboru
        if (_cachedWhitelist != null &&
            DateTime.UtcNow - _lastLoaded < TimeSpan.FromMinutes(CacheMinutes))
            return _cachedWhitelist;

        if (!File.Exists(_whitelistPath))
        {
            _logger.LogError("Whitelist soubor nenalezen: {Path}", _whitelistPath);
            return null;
        }

        // ── RSA VERIFIKACE ────────────────────────────────────
        if (_signatureVerificationEnabled)
        {
            if (_signatureVerifier == null)
            {
                _logger.LogError(
                    "RSA verifikace povolena ale SignatureVerifier není nakonfigurován – " +
                    "whitelist ODMÍTNUT (fail-secure)");
                return null;
            }

            if (!_signatureVerifier.Verify(_whitelistPath, SignaturePath))
            {
                // Podpis neplatný – invalidujeme cache a odmítneme
                _cachedWhitelist = null;
                return null;
            }
        }
        // ─────────────────────────────────────────────────────

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

    private bool MatchesEntry(DeviceInfo device, WhitelistEntry entry)
    {
        if (!string.Equals(device.VendorId, entry.VendorId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(device.ProductId, entry.ProductId, StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.IsNullOrEmpty(entry.SerialNumber))
        {
            if (!_allowWildcards)
            {
                _logger.LogWarning(
                    "Whitelist záznam {VID}/{PID} nemá sériové číslo " +
                    "a AllowWildcards=false – médium ZAMÍTNUTO.",
                    entry.VendorId, entry.ProductId);
                return false;
            }
            _logger.LogDebug("Wildcard shoda: {VID}/{PID}", entry.VendorId, entry.ProductId);
        }
        else
        {
            if (!string.Equals(device.SerialNumber, entry.SerialNumber,
                StringComparison.OrdinalIgnoreCase))
                return false;
        }

        if (entry.ValidUntil.HasValue && entry.ValidUntil.Value < DateTime.UtcNow)
        {
            _logger.LogWarning(
                "Zařízení {Device} bylo na whitelistu ale platnost vypršela: {Expired}",
                device.FriendlyName, entry.ValidUntil.Value);
            return false;
        }

        return true;
    }
}

public enum WhitelistStatus { Valid, Expired, Missing }
