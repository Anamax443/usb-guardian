// ============================================================
// WhitelistCheckerTests.cs
// Nižší vrstva k PolicyEnforcerExpiryTests: WhitelistChecker sám o sobě
// SMÍ vrátit IsAllowed=true i pro expirovaný whitelist (degraded mode
// potřebuje vědět "byl by povolen, kdyby whitelist platil") - je to
// PolicyEnforcer.HandleDevice, kdo musí GetStatus() zohlednit (oprava
// 56b4235). Test tu dokumentuje/jistí tenhle záměr, ať ho někdo příště
// omylem "neopraví" přímo ve WhitelistChecker.
// ============================================================

using Microsoft.Extensions.Logging.Abstractions;
using USBGuardian;
using USBGuardian.Models;
using Xunit;

namespace USBGuardian.Agent.Tests;

public class WhitelistCheckerTests : IDisposable
{
    private readonly string _root;
    private readonly string _whitelistPath;

    public WhitelistCheckerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "usbguardian-wl-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_root);
        _whitelistPath = Path.Combine(_root, "whitelist.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void WriteWhitelist(DateTime validUntil)
    {
        var wl = new WhitelistFile
        {
            Version    = "test-v1",
            IssuedAt   = DateTime.UtcNow.AddDays(-60),
            ValidUntil = validUntil,
            Devices =
            {
                new WhitelistEntry { VendorId = "0951", ProductId = "1666", SerialNumber = "TESTSN123" },
            },
        };
        File.WriteAllText(_whitelistPath, System.Text.Json.JsonSerializer.Serialize(wl));
    }

    private WhitelistChecker MakeChecker() =>
        // signatureVerificationEnabled:false - podpis RSA je pokrytý jinde (SignatureVerifier),
        // tenhle test se týká jen expirace/lookupu.
        new(NullLogger<WhitelistChecker>.Instance, _whitelistPath,
            allowWildcards: false, signatureVerificationEnabled: false);

    private static DeviceInfo ListedDevice() => new()
    {
        VendorId = "0951", ProductId = "1666", SerialNumber = "TESTSN123",
    };

    [Fact]
    public void GetStatus_reports_Expired_after_ValidUntil()
    {
        WriteWhitelist(validUntil: DateTime.UtcNow.AddDays(-1));
        var checker = MakeChecker();

        Assert.Equal(WhitelistStatus.Expired, checker.GetStatus());
    }

    [Fact]
    public void GetStatus_reports_Valid_before_ValidUntil()
    {
        WriteWhitelist(validUntil: DateTime.UtcNow.AddDays(30));
        var checker = MakeChecker();

        Assert.Equal(WhitelistStatus.Valid, checker.GetStatus());
    }

    [Fact]
    public void IsAllowed_still_finds_a_listed_device_when_expired()
    {
        // Záměrné chování WhitelistChecker samotného - "je na seznamu" a "seznam je čerstvý"
        // jsou dvě oddělené otázky. Volající (PolicyEnforcer) musí zkombinovat obě - to je
        // přesně to, co oprava 56b4235 přidala.
        WriteWhitelist(validUntil: DateTime.UtcNow.AddDays(-1));
        var checker = MakeChecker();

        Assert.True(checker.IsAllowed(ListedDevice()));
        Assert.Equal(WhitelistStatus.Expired, checker.GetStatus());
    }
}
