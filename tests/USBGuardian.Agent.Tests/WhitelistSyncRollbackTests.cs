// ============================================================
// WhitelistSyncRollbackTests.cs
// TryGetIssuedAt/IsRollback jsou čisté funkce z WhitelistSync.DownloadAndSaveWhitelist
// (bez HTTP/souborového systému - to v testu snadno nejde sestrojit).
//
// Nález z oponentury 11.09.2026: platně podepsaný, ale STARŠÍ whitelist blob (stará záloha,
// DB rollback, replay útočníkem s přístupem ke starým podepsaným datům) se dřív uložil bez
// námitek - pinning brání zfalšování NOVÉHO obsahu, ale nebrání přehrání starého platného
// obsahu. `issuedAt` je součástí toho, co RSA podpis kryje (WhitelistPublisher.cs), takže
// libovolná změna vstupu (včetně smazání pole) podpis rozbije - "nejde přečíst issuedAt"
// proto vždy znamená podezřelý vstup, nikdy legitimní starší formát.
// ============================================================

using USBGuardian;
using Xunit;

namespace USBGuardian.Agent.Tests;

public class WhitelistSyncRollbackTests
{
    private const string ValidBlob =
        """{"version":"2026-09-11-v3","issuedAt":"2026-09-11T10:00:00Z","validUntil":"2027-09-11T10:00:00Z","devices":[]}""";

    [Fact]
    public void TryGetIssuedAt_parses_a_well_formed_blob()
    {
        var ok = WhitelistSync.TryGetIssuedAt(ValidBlob, out var issuedAt);

        Assert.True(ok);
        Assert.Equal(new DateTime(2026, 9, 11, 10, 0, 0, DateTimeKind.Utc), issuedAt);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"version":"2026-09-11-v3"}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void TryGetIssuedAt_fails_closed_on_anything_without_a_readable_issuedAt(string json)
    {
        var ok = WhitelistSync.TryGetIssuedAt(json, out _);

        Assert.False(ok);
    }

    [Fact]
    public void No_local_baseline_is_never_a_rollback()
    {
        // Prvni sync (agent zatim nema zadny whitelist na disku) - cokoli platne
        // podepsaneho je lepsi nez nic.
        var isRollback = WhitelistSync.IsRollback(currentIssuedAt: null, candidateIssuedAt: DateTime.UtcNow);

        Assert.False(isRollback);
    }

    [Fact]
    public void Older_candidate_than_local_is_a_rollback()
    {
        var local     = new DateTime(2026, 9, 11, 10, 0, 0, DateTimeKind.Utc);
        var candidate = local.AddDays(-1);

        Assert.True(WhitelistSync.IsRollback(local, candidate));
    }

    [Fact]
    public void Newer_candidate_than_local_is_not_a_rollback()
    {
        var local     = new DateTime(2026, 9, 11, 10, 0, 0, DateTimeKind.Utc);
        var candidate = local.AddDays(1);

        Assert.False(WhitelistSync.IsRollback(local, candidate));
    }

    [Fact]
    public void Identical_issuedAt_is_not_a_rollback()
    {
        // Idempotentni re-sync stejne verze (retry, druhy cyklus pred expiraci cache) neni utok.
        var same = new DateTime(2026, 9, 11, 10, 0, 0, DateTimeKind.Utc);

        Assert.False(WhitelistSync.IsRollback(same, same));
    }
}
