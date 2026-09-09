// ============================================================
// Reachability.cs
// Ping dostupnost stanic – sdílené mezi AgentDeployService (dry-run
// report) a stránkou Stanice (tlačítko "Ověřit dostupnost"). Odděleno
// od DB/komunikace agenta: "Komunikace" ve Stanice.razor je heartbeat
// agenta, tohle je čistě síťová dostupnost (ping), bez ohledu na to,
// jestli tam agent vůbec je.
// ============================================================

using System.Net.NetworkInformation;

namespace USBGuardian.Admin.Deploy;

public static class Reachability
{
    public static async Task<bool> PingAsync(string host, int timeoutMs = 1500)
    {
        try { using var p = new Ping(); return (await p.SendPingAsync(host, timeoutMs)).Status == IPStatus.Success; }
        catch { return false; }
    }

    // Paralelně, ať 228 stanic neznamená 228× 1.5s čekání za sebou.
    public static async Task<Dictionary<string, bool>> PingManyAsync(IEnumerable<string> hosts, int timeoutMs = 1500)
    {
        var list = hosts.Where(h => !string.IsNullOrWhiteSpace(h))
                         .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var results = await Task.WhenAll(list.Select(async h => (Host: h, Ok: await PingAsync(h, timeoutMs))));
        return results.ToDictionary(r => r.Host, r => r.Ok, StringComparer.OrdinalIgnoreCase);
    }

    // Čisté mapování výsledku na vizuál – testovatelné bez sítě/DB.
    public static string DotClass(bool? ok) => ok switch
    {
        true  => "ok",
        false => "bad",
        null  => "off",
    };

    public static string DotTitle(bool? ok, DateTime? checkedAtLocal) => ok switch
    {
        true  => $"Odpovídá na ping – ověřeno {FormatTime(checkedAtLocal)}",
        false => $"Neodpovídá na ping – ověřeno {FormatTime(checkedAtLocal)}",
        null  => "Dostupnost zatím neověřena – klikni „Ověřit dostupnost“",
    };

    private static string FormatTime(DateTime? t) => t is { } v ? v.ToString("HH:mm:ss") : "?";
}
