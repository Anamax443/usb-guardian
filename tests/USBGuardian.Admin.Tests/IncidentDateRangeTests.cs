// ============================================================
// IncidentDateRangeTests.cs
// Casove pasmo bezicicho stroje se lisi mezi lokalem a CI runnerem - proto
// se tu neporovnavaji presne UTC hodnoty, ale round-trip pres ToLocalTime(),
// coz plati bez ohledu na to, v jakem pasmu test bezi.
// ============================================================

using USBGuardian.Admin;
using Xunit;

namespace USBGuardian.Admin.Tests;

public class IncidentDateRangeTests
{
    [Fact]
    public void No_range_given_falls_back_to_relative_days_window()
    {
        var (from, to, label) = IncidentDateRange.Resolve(30, null, null);

        Assert.Equal(DateTime.MaxValue, to);
        Assert.Equal("30 dní", label);
        Assert.True((DateTime.UtcNow - from) > TimeSpan.FromDays(29.9));
        Assert.True((DateTime.UtcNow - from) < TimeSpan.FromDays(30.1));
    }

    [Fact]
    public void Days_zero_means_everything()
    {
        var (from, to, label) = IncidentDateRange.Resolve(0, null, null);
        Assert.Equal(DateTime.MinValue, from);
        Assert.Equal(DateTime.MaxValue, to);
        Assert.Equal("vše", label);
    }

    [Fact]
    public void Explicit_range_covers_the_whole_end_day_inclusive()
    {
        var (from, to, label) = IncidentDateRange.Resolve(30, "2026-03-01", "2026-03-31");

        // Pulnoc 1.3. mistniho casu az pulnoc 1.4. (vyloucne) - cely 31.3. je jeste uvnitr.
        Assert.Equal(new DateTime(2026, 3, 1), from.ToLocalTime());
        Assert.Equal(new DateTime(2026, 4, 1), to.ToLocalTime());
        Assert.Equal("01.03.2026 – 31.03.2026", label);
    }

    [Fact]
    public void Swapped_dates_are_corrected()
    {
        var (from, to, _) = IncidentDateRange.Resolve(30, "2026-03-31", "2026-03-01");
        Assert.Equal(new DateTime(2026, 3, 1), from.ToLocalTime());
        Assert.Equal(new DateTime(2026, 4, 1), to.ToLocalTime());
    }

    [Fact]
    public void Only_od_given_means_od_until_today_inclusive()
    {
        var now = new DateTime(2026, 3, 15, 9, 0, 0);
        var (from, to, label) = IncidentDateRange.Resolve(30, "2026-03-01", null, now);

        Assert.Equal(new DateTime(2026, 3, 1), from.ToLocalTime());
        Assert.Equal(new DateTime(2026, 3, 16), to.ToLocalTime());
        Assert.Equal("01.03.2026 – dnes", label);
    }

    [Fact]
    public void Only_do_given_means_everything_up_to_do()
    {
        var (from, to, label) = IncidentDateRange.Resolve(30, null, "2026-03-31");
        Assert.Equal(DateTime.MinValue, from);
        Assert.Equal(new DateTime(2026, 4, 1), to.ToLocalTime());
        Assert.Equal("… – 31.03.2026", label);
    }

    [Fact]
    public void Garbage_input_is_ignored_like_no_range()
    {
        var (from, to, label) = IncidentDateRange.Resolve(90, "not-a-date", "also-not-a-date");
        Assert.Equal("90 dní", label);
        Assert.Equal(DateTime.MaxValue, to);
    }

    [Fact]
    public void One_valid_one_garbage_date_treats_the_garbage_one_as_not_given()
    {
        // Zdokumentovane chovani: negativni pripad k Only_od_given_* vyse - "do" prazdne
        // a "do" nesmyslne maji dopadnout stejne (obe znamenaji "az do dneska").
        var now = new DateTime(2026, 3, 15, 9, 0, 0);
        var (from, to, label) = IncidentDateRange.Resolve(30, "2026-03-01", "not-a-date", now);
        Assert.Equal(new DateTime(2026, 3, 1), from.ToLocalTime());
        Assert.Equal(new DateTime(2026, 3, 16), to.ToLocalTime());
        Assert.Equal("01.03.2026 – dnes", label);
    }

    [Fact]
    public void Maximum_end_date_does_not_throw_and_means_no_upper_bound()
    {
        // DateOnly.MaxValue (9999-12-31, napr. z <input type="date"> bez horniho limitu) driv
        // padalo na "+1 den" pri vypoctu vyloucene horni hranice (ArgumentOutOfRangeException).
        var (from, to, label) = IncidentDateRange.Resolve(30, null, "9999-12-31");
        Assert.Equal(DateTime.MinValue, from);
        Assert.Equal(DateTime.MaxValue, to);
        Assert.Equal("… – 31.12.9999", label);
    }
}
