// ============================================================
// StationStatusTests.cs
// Regresní test pro nález 11.09.2026: "Zmlklo agentů" se dřív počítalo čistě
// z LastSeen, bez ohledu na to, jestli je PC vůbec zapnuté - vypnutá stanice
// přes noc vypadala stejně jako reálně spadlý agent na běžícím stroji.
// Silent() teď smí platit JEN s potvrzeným pingem (PC běží, agent mlčí).
// ============================================================

using USBGuardian.Admin.Deploy;
using Xunit;

namespace USBGuardian.Admin.Tests;

public class StationStatusTests
{
    [Fact]
    public void Stale_but_unreachable_station_is_not_silent()
    {
        // Přesně případ z 11.09.2026: agent dávno neodpověděl, ping taky ne -
        // PC je nejspíš vypnuté, ne "zmlklý agent" vyžadující pozornost.
        Assert.False(StationStatus.Silent(reports: true, fresh: false, lastPingOk: false));
        Assert.True(StationStatus.ProbablyOff(reports: true, fresh: false, lastPingOk: false));
    }

    [Fact]
    public void Stale_and_reachable_station_is_silent()
    {
        // PC běží (ping OK), ale agent se dlouho neozval - tohle je ten
        // skutečně podezřelý případ (spadlá/zaseklá služba).
        Assert.True(StationStatus.Silent(reports: true, fresh: false, lastPingOk: true));
        Assert.False(StationStatus.ProbablyOff(reports: true, fresh: false, lastPingOk: true));
    }

    [Fact]
    public void Stale_station_not_yet_pinged_is_neither_silent_nor_probably_off()
    {
        // Dostupnost se ještě neověřila (PingMonitorService na ni ještě nedošel) -
        // nesmí se to tvářit jako potvrzený stav ani jedním směrem.
        Assert.False(StationStatus.Silent(reports: true, fresh: false, lastPingOk: null));
        Assert.False(StationStatus.ProbablyOff(reports: true, fresh: false, lastPingOk: null));
    }

    [Fact]
    public void Fresh_station_is_never_silent_regardless_of_ping()
    {
        Assert.False(StationStatus.Silent(reports: true, fresh: true, lastPingOk: false));
        Assert.False(StationStatus.ProbablyOff(reports: true, fresh: true, lastPingOk: false));
    }

    [Fact]
    public void Station_that_never_reported_is_never_silent_regardless_of_ping()
    {
        // "Chybí agent" je jiná kategorie - Silent/ProbablyOff se jí netýká.
        Assert.False(StationStatus.Silent(reports: false, fresh: false, lastPingOk: true));
        Assert.False(StationStatus.ProbablyOff(reports: false, fresh: false, lastPingOk: true));
    }

    [Theory]
    [InlineData(false, true, false, null, "off")]   // žádný kontakt vůbec
    [InlineData(true,  true, true,  null, "ok")]    // čerstvý
    [InlineData(true,  true, false, true, "bad")]   // zmlklý, PC běží - stojí za pozornost
    [InlineData(true,  true, false, false, "off")]  // zmlklý, PC nejspíš vypnuté
    [InlineData(true,  true, false, null, "warn")]  // zmlklý, ping se ještě ověřuje
    public void CommDotClass_matches_expected_visual(
        bool hasContact, bool reports, bool fresh, bool? lastPingOk, string expected)
    {
        Assert.Equal(expected, StationStatus.CommDotClass(hasContact, reports, fresh, lastPingOk));
    }
}
