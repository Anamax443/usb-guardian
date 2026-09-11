// ============================================================
// IncidentQueueWorkerRetryTests.cs
// NextRetryDelay je čistá rozhodovací funkce pro ohraničený exponenciální odstup
// RetrySpoolLoopAsync (bez skutečného Task.Delay/SQL - to se v testu snadno nesestrojí,
// stejný důvod, proč RunPowerShell/ProcessBatch samotné testované nejsou).
// Nález z oponentury 11.09.2026: ReplaySpoolAsync běžel jen JEDNOU při startu služby -
// výpadek SQL uprostřed provozu vyžadoval ruční restart, než se spool zkusil přehrát znovu.
// ============================================================

using USBGuardian.Api.Queue;
using Xunit;

namespace USBGuardian.Api.Tests;

public class IncidentQueueWorkerRetryTests
{
    private static readonly TimeSpan Initial = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Max     = TimeSpan.FromMinutes(5);

    [Fact]
    public void Successful_attempt_resets_delay_to_initial()
    {
        var next = IncidentQueueWorker.NextRetryDelay(TimeSpan.FromMinutes(2), processedAnyThisAttempt: true);

        Assert.Equal(Initial, next);
    }

    [Fact]
    public void Failed_attempt_doubles_the_delay()
    {
        var next = IncidentQueueWorker.NextRetryDelay(TimeSpan.FromSeconds(5), processedAnyThisAttempt: false);

        Assert.Equal(TimeSpan.FromSeconds(10), next);
    }

    [Fact]
    public void Repeated_failures_grow_exponentially()
    {
        var delay = Initial;
        Span<int> expectedSeconds = stackalloc int[] { 10, 20, 40, 80, 160 };

        foreach (var expected in expectedSeconds)
        {
            delay = IncidentQueueWorker.NextRetryDelay(delay, processedAnyThisAttempt: false);
            Assert.Equal(TimeSpan.FromSeconds(expected), delay);
        }
    }

    [Fact]
    public void Delay_never_exceeds_the_bounded_maximum_even_after_many_failures()
    {
        var delay = Initial;
        for (var i = 0; i < 20; i++)
            delay = IncidentQueueWorker.NextRetryDelay(delay, processedAnyThisAttempt: false);

        Assert.Equal(Max, delay);
    }

    [Fact]
    public void A_success_after_a_long_outage_drops_straight_back_to_initial_not_gradually()
    {
        // Ať SQL spadne na 3 minuty nebo na 3 dny, jakmile se jeden batch zapíše, zbytek
        // spoolu (může jich být hodně nastřádaných) se má zkusit brzy, ne postupně odbourávat
        // z několikaminutového odstupu.
        var next = IncidentQueueWorker.NextRetryDelay(Max, processedAnyThisAttempt: true);

        Assert.Equal(Initial, next);
    }
}
