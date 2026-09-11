// ============================================================
// StationStatus.cs
// Čisté odvození stavu stanice ze tří nezávislých signálů (hlásí agenta / je
// čerstvý / potvrzený ping) - bez DB/UI, testovatelné odděleně od Computers.razor.
//
// Zmlklý (Silent) smí platit JEN když ping potvrdí, že PC běží - jinak by vypnutý
// notebook přes noc vypadal stejně jako spadlá služba na běžícím stroji (viz
// database/10_ping_status.sql, PingMonitorService).
// ============================================================

namespace USBGuardian.Admin.Deploy;

public static class StationStatus
{
    public static bool Silent(bool reports, bool fresh, bool? lastPingOk) =>
        reports && !fresh && lastPingOk == true;

    public static bool ProbablyOff(bool reports, bool fresh, bool? lastPingOk) =>
        reports && !fresh && lastPingOk == false;

    /// <summary>Vizuál sloupce "Kom." - `hasContact` = agent se OD ŽIVOTA aspoň jednou ozval (LastSeen not null).</summary>
    public static string CommDotClass(bool hasContact, bool reports, bool fresh, bool? lastPingOk)
    {
        if (!hasContact) return "off";
        if (fresh) return "ok";
        if (Silent(reports, fresh, lastPingOk)) return "bad";
        if (ProbablyOff(reports, fresh, lastPingOk)) return "off";
        return "warn"; // zmlkl, ping se ještě neověřil (PingMonitorService doběhne brzy)
    }
}
