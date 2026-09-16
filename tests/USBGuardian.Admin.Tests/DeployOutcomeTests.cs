// ============================================================
// DeployOutcomeTests.cs
// Regresní test pro nález 16.09.2026: dlaždice "Chyby nasazení" a sloupec
// "Poslední nasazení" braly poslední deploy-run záznam bez ohledu na to,
// jestli stanice dneska funguje - CERNYSW11 hlásila (ozvala se), ale
// sloupec pořád ukazoval starý SKIP z dřívějšího pokusu a vypadalo to
// jako aktuální problém. IsRelevantError/ShouldShowHistory teď obojí
// řídí jen reports() - žádná chyba/historie se neukáže u stanice, co
// zrovna hlásí, ať je poslední deploy-run cokoliv.
// ============================================================

using USBGuardian.Admin.Deploy;
using Xunit;

namespace USBGuardian.Admin.Tests;

public class DeployOutcomeTests
{
    [Fact]
    public void Error_on_a_station_that_is_not_reporting_counts()
    {
        Assert.True(DeployOutcome.IsRelevantError(reports: false, level: "error"));
    }

    [Fact]
    public void Error_on_a_station_that_now_reports_is_stale_and_does_not_count()
    {
        // Přesně CERNYSW11: poslední deploy-run byl SKIP/FAIL, ale stanice dneska hlásí.
        Assert.False(DeployOutcome.IsRelevantError(reports: true, level: "error"));
    }

    [Fact]
    public void Warn_and_info_levels_never_count_as_error_regardless_of_reports()
    {
        Assert.False(DeployOutcome.IsRelevantError(reports: false, level: "warn"));
        Assert.False(DeployOutcome.IsRelevantError(reports: false, level: "info"));
    }

    [Fact]
    public void No_history_at_all_does_not_count_as_error()
    {
        Assert.False(DeployOutcome.IsRelevantError(reports: false, level: null));
    }

    [Fact]
    public void History_is_shown_only_while_the_station_does_not_report()
    {
        Assert.True(DeployOutcome.ShouldShowHistory(reports: false));
        Assert.False(DeployOutcome.ShouldShowHistory(reports: true));
    }

    [Theory]
    [InlineData("error", "bad")]
    [InlineData("warn", "warn")]
    [InlineData("info", "ok")]
    [InlineData("anything-else", "ok")]
    public void Pill_class_maps_level_to_the_right_color(string level, string expected)
    {
        Assert.Equal(expected, DeployOutcome.PillClass(level));
    }
}
