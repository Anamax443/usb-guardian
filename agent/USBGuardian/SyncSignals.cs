// ============================================================
// SyncSignals.cs
// Sdílený singleton mezi WhitelistSync (zdroj příkazu z heartbeatu)
// a IncidentSync (odesílatel dat). Když konzole vyžádá data
// (heartbeat vrátí ReportNow=true), WhitelistSync zavolá
// RequestFlush() a IncidentSync se probudí z čekání a hned
// odešle frontu místo aby čekal celý interval.
// ============================================================

namespace USBGuardian;

public sealed class SyncSignals
{
    // Kapacita 1: víc požadavků mezi dvěma cykly = jeden flush (stačí).
    private readonly SemaphoreSlim _flush = new(0, 1);

    /// <summary>Vyžádá okamžitý flush fronty incidentů (probudí IncidentSync).</summary>
    public void RequestFlush()
    {
        try { _flush.Release(); }
        catch (SemaphoreFullException) { /* už je naplánováno */ }
    }

    /// <summary>Počká buď na vypršení intervalu, nebo na vyžádaný flush (co nastane dřív).
    /// Vrací true = probuzeno flushem, false = uplynul interval.</summary>
    public Task<bool> WaitForFlushOrInterval(TimeSpan interval, CancellationToken ct)
        => _flush.WaitAsync(interval, ct);
}
