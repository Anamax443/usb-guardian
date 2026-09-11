// ============================================================
// ReachabilityTests.cs
// DotClass/DotTitle jsou čisté mapovací funkce (ping výsledek -> vizuál na
// stránce Stanice), bez sítě/DB. PingAsync/PingManyAsync se netestují -
// reálný ICMP ping v jednotkovém testu by byl nespolehlivý (síť, firewall).
// ============================================================

using USBGuardian.Admin.Deploy;
using Xunit;

namespace USBGuardian.Admin.Tests;

public class ReachabilityTests
{
    [Fact]
    public void Reachable_host_gets_the_ok_dot()
    {
        Assert.Equal("ok", Reachability.DotClass(true));
    }

    [Fact]
    public void Unreachable_host_gets_the_bad_dot_not_warn()
    {
        Assert.Equal("bad", Reachability.DotClass(false));
    }

    [Fact]
    public void Never_checked_host_gets_the_off_dot_not_bad()
    {
        // Bez kliknutí na "Ověřit dostupnost" nesmí řádek vypadat, jako by
        // neodpovídal na ping - to by bylo zavádějící (nic se ještě nezjišťovalo).
        Assert.Equal("off", Reachability.DotClass(null));
    }

    [Fact]
    public void Title_for_unchecked_host_prompts_the_button_not_a_timestamp()
    {
        var title = Reachability.DotTitle(null, null);
        Assert.Contains("neověřena", title);
    }

    [Fact]
    public void Title_for_reachable_host_includes_the_check_time()
    {
        var title = Reachability.DotTitle(true, new DateTime(2026, 9, 9, 14, 47, 0));
        Assert.Contains("Odpovídá", title);
        Assert.Contains("14:47:00", title);
    }

    [Fact]
    public void Title_for_unreachable_host_includes_the_check_time()
    {
        var title = Reachability.DotTitle(false, new DateTime(2026, 9, 9, 14, 47, 0));
        Assert.Contains("Neodpovídá", title);
        Assert.Contains("14:47:00", title);
    }
}
