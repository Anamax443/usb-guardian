// ============================================================
// HealthServiceSigningKeyTests.cs
// EvaluateSigningKey je čistá interpretace kontroly "Podpisový klíč whitelistu"
// (HealthService.cs, skupina "Whitelist a politika").
//
// Nález z 11.09.2026 (kontrola ACL na TLS/RSA klíčích na APP_SERVER): produkční
// appsettings.local.json konzole ztratil Whitelist:PrivateKeyPath - privátní klíč na
// disku ležel, ale nikde nebylo řečeno, že se má použít, takže auto-podpis whitelistu
// byl potichu vypnutý. Kontrola v HealthService už tenhle stav správně hlásí jako
// "Off/nenastaveno", jen na ni chyběl test.
// ============================================================

using USBGuardian.Admin.Health;
using Xunit;

namespace USBGuardian.Admin.Tests;

public class HealthServiceSigningKeyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "usbg-signingkey-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_config_key_is_Off_not_Bad(string? path)
    {
        // Off, ne Bad: nenastavený klíč je legitimní stav (auto-podpis prostě vypnutý),
        // ne provozní chyba - stejné rozlišení jako u ostatních "Off vs Bad" kontrol v HealthService.
        var outcome = HealthService.EvaluateSigningKey(path);

        Assert.Equal(HealthState.Off, outcome.State);
        Assert.Equal("nenastaveno", outcome.Value);
        Assert.Contains("Whitelist:PrivateKeyPath", outcome.Fix);
    }

    [Fact]
    public void Configured_but_missing_file_is_Bad_with_the_path_in_the_message()
    {
        var path = Path.Combine(_root, "does-not-exist.pem");

        var outcome = HealthService.EvaluateSigningKey(path);

        Assert.Equal(HealthState.Bad, outcome.State);
        Assert.Contains(path, outcome.Value);
    }

    [Fact]
    public void Existing_readable_file_is_Ok()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "whitelist_private.pem");
        File.WriteAllText(path, "dummy key material");

        var outcome = HealthService.EvaluateSigningKey(path);

        Assert.Equal(HealthState.Ok, outcome.State);
        Assert.Contains(path, outcome.Value);
        Assert.Equal("", outcome.Fix);
    }

    [Fact]
    public void Existing_but_locked_file_is_Bad_not_a_crash()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "locked.pem");
        File.WriteAllText(path, "dummy key material");

        // Otevřít exkluzivně - simuluje soubor, na který má účet konzole nedostatečné právo
        // (přesně scénář, který Set-KeyFileAcl.ps1 může omylem způsobit chybně nastavenou ACL).
        using var lockHandle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var outcome = HealthService.EvaluateSigningKey(path);

        Assert.Equal(HealthState.Bad, outcome.State);
        Assert.Contains("nelze přečíst", outcome.Value);
    }
}
