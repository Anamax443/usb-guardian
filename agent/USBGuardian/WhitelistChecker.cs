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

    // Indexy pro O(1) lookup (scale-safe i pro 10k+ zařízení) – rebuildují se při (re)načtení whitelistu.
    private Dictionary<string, WhitelistEntry> _exactIndex = new(StringComparer.OrdinalIgnoreCase);    // VID:PID:SERIAL
    private Dictionary<string, WhitelistEntry> _wildcardIndex = new(StringComparer.OrdinalIgnoreCase); // VID:PID (prázdný serial)

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

        // O(1) lookup přes index (scale-safe). Přesná shoda VID:PID:SERIAL.
        if (_exactIndex.TryGetValue($"{device.VendorId}:{device.ProductId}:{device.SerialNumber}", out var entry)
            && NotExpired(entry))
        {
            _logger.LogDebug("Zařízení {Device} na whitelistu: {Description}", device, entry.Description);
            return true;
        }

        // Wildcard (záznam bez sériáku) – jen když AllowWildcards.
        if (_allowWildcards
            && _wildcardIndex.TryGetValue($"{device.VendorId}:{device.ProductId}", out var wc)
            && NotExpired(wc))
        {
            _logger.LogDebug("Wildcard shoda: {VID}/{PID}", wc.VendorId, wc.ProductId);
            return true;
        }

        _logger.LogInformation("Zařízení {Device} NENÍ na whitelistu", device);
        return false;
    }

    // Per-záznam expirace (NULL = trvalé schválení).
    private bool NotExpired(WhitelistEntry e)
    {
        if (e.ValidUntil.HasValue && e.ValidUntil.Value < DateTime.UtcNow)
        {
            _logger.LogWarning("Záznam {VID}/{PID}/{SN} expiroval: {Exp}",
                e.VendorId, e.ProductId, e.SerialNumber, e.ValidUntil.Value);
            return false;
        }
        return true;
    }

    // Přestavění indexů z načteného whitelistu (volá LoadWhitelist po (re)načtení).
    private void RebuildIndex(WhitelistFile wl)
    {
        var exact    = new Dictionary<string, WhitelistEntry>(StringComparer.OrdinalIgnoreCase);
        var wildcard = new Dictionary<string, WhitelistEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in wl.Devices)
        {
            if (string.IsNullOrEmpty(e.SerialNumber))
                wildcard[$"{e.VendorId}:{e.ProductId}"] = e;
            else
                exact[$"{e.VendorId}:{e.ProductId}:{e.SerialNumber}"] = e;
        }
        _exactIndex    = exact;
        _wildcardIndex = wildcard;

        if (wildcard.Count > 0 && !_allowWildcards)
            _logger.LogWarning("Whitelist má {N} záznamů bez sériáku, ale AllowWildcards=false – ignorovány.", wildcard.Count);
    }

    public string GetVersion()  => LoadWhitelist()?.Version ?? "unknown";

    /// <summary>Read-only snapshot pro lokální konzoli.</summary>
    public record WhitelistSnapshot(string Version, WhitelistStatus Status, int DeviceCount, DateTime ValidUntil);

    public WhitelistSnapshot GetSnapshot()
    {
        var wl = LoadWhitelist();
        return new WhitelistSnapshot(
            wl?.Version ?? "unknown",
            GetStatus(),
            wl?.Devices.Count ?? 0,
            wl?.ValidUntil ?? DateTime.MinValue);
    }

    /// <summary>Read-only seznam schválených zařízení (pro lokální konzoli).</summary>
    public IReadOnlyList<WhitelistEntry> GetEntries() => LoadWhitelist()?.Devices ?? new List<WhitelistEntry>();

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

            if (_cachedWhitelist != null) RebuildIndex(_cachedWhitelist);

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

public enum WhitelistStatus { Valid, Expired, Missing }
