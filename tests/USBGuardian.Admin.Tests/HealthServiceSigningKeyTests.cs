// ============================================================
// HealthServiceSigningKeyTests.cs
// EvaluateSigningKey je čistá interpretace kontroly "Podpisový klíč whitelistu"
// (HealthService.cs, skupina "Whitelist a politika").
//
// Nález z 11.09.2026 (kontrola ACL na TLS/RSA klíčích na APP_SERVER): produkční
// appsettings.local.json konzole ztratil Whitelist:PrivateKeyPath - privátní klíč na
// disku ležel, ale nikde nebylo řečeno, že se má použít, takže auto-podpis whitelistu
// byl potichu vypnutý. Kontrola v HealthService to hlásila jako "Off/nenastaveno" -
// STEJNĚ jako záměrně vypnutý auto-podpis by vypadal, takže si toho nikdo nevšiml.
//
// Druhé kolo oponentury (11.09.2026): "nenastaveno" má dva různé významy (chyba vs.
// záměr) a nemělo by se to odvozovat jen z toho, že PrivateKeyPath chybí. Přidán
// Whitelist:SigningRequired (default true, fail-secure) - jen VÝSLOVNÝ opt-out
// (SigningRequired=false) dělá z chybějícího klíče legitimní Off místo Bad.
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
    public void Missing_path_with_signing_required_is_Bad_not_Off(string? path)
    {
        // Přesně nález z 11.09.2026: bez explicitního opt-outu je "nenastaveno" provozní
        // chyba, ne tichý legitimní stav - jinak zmizelý PrivateKeyPath v produkci vypadá
        // identicky jako záměrně vypnutý auto-podpis.
        var outcome = HealthService.EvaluateSigningKey(path, signingRequired: true);

        Assert.Equal(HealthState.Bad, outcome.State);
        Assert.Contains("Whitelist:PrivateKeyPath", outcome.Fix);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_path_with_explicit_opt_out_is_Off_not_Bad(string? path)
    {
        // Whitelist:SigningRequired=false = administrátor to vědomě vypnul - to je
        // legitimní stav (Off), ne chyba.
        var outcome = HealthService.EvaluateSigningKey(path, signingRequired: false);

        Assert.Equal(HealthState.Off, outcome.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Configured_but_missing_file_is_always_Bad_regardless_of_signing_required(bool signingRequired)
    {
        // Explicitně nakonfigurovaná cesta, která nikam nevede, je chyba vždy - SigningRequired
        // rozlišuje jen "nenastaveno vůbec", ne "nastaveno špatně".
        var path = Path.Combine(_root, "does-not-exist.pem");

        var outcome = HealthService.EvaluateSigningKey(path, signingRequired);

        Assert.Equal(HealthState.Bad, outcome.State);
        Assert.Contains(path, outcome.Value);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Existing_readable_file_is_always_Ok_regardless_of_signing_required(bool signingRequired)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "whitelist_private.pem");
        File.WriteAllText(path, "dummy key material");

        var outcome = HealthService.EvaluateSigningKey(path, signingRequired);

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

        var outcome = HealthService.EvaluateSigningKey(path, signingRequired: true);

        Assert.Equal(HealthState.Bad, outcome.State);
        Assert.Contains("nelze přečíst", outcome.Value);
    }
}
