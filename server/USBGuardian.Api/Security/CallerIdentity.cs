// ============================================================
// CallerIdentity.cs
// Hostname, který volající NESE v autentizované Windows identitě (Negotiate) -
// na rozdíl od Hostname v datech požadavku, což je jen TVRZENÍ, ničím nepodložené.
// Agent běží jako SYSTEM, tzn. autentizuje se strojovým účtem (DOMENA\HOSTNAME$) -
// server tak může porovnat, čím se volající doopravdy prokázal, s tím, za koho se
// v datech vydává.
//
// Audit 04.09.2026: dnes se Hostname z payloadu bere jako fakt, ne jako tvrzení -
// libovolný účet ze skupiny USB-Guardian-Clients (jakákoliv stanice s agentem) může
// v datech napsat cizí hostname a incidenty/heartbeat se zapíšou pod cizí identitou.
// ============================================================

using System.Security.Principal;

namespace USBGuardian.Api.Security;

public static class CallerIdentity
{
    /// <summary>
    /// Bare hostname z autentizovaného Windows strojového účtu volajícího, nebo null,
    /// když identita není autentizovaný Windows účet nebo nejde o strojový účet
    /// (uživatelský účet nemá SAM zakončený `$`).
    /// </summary>
    public static string? MachineHostnameOrNull(IIdentity? identity)
        => identity is WindowsIdentity { IsAuthenticated: true } wi
            ? ParseMachineHostname(wi.Name)
            : null;

    /// <summary>
    /// Čistá funkce beze závislosti na skutečné WindowsIdentity (tu nejde v testu snadno
    /// sestrojit) - "DOMENA\PC-01$" -> "PC-01", "DOMENA\trnkam" (uživatel, ne stroj) -> null,
    /// funguje i bez domény v názvu.
    /// </summary>
    internal static string? ParseMachineHostname(string? windowsIdentityName)
    {
        var name = windowsIdentityName ?? string.Empty;
        var sam  = name.Contains('\\') ? name[(name.IndexOf('\\') + 1)..] : name;
        return sam.Length > 1 && sam.EndsWith('$') ? sam[..^1] : null;
    }
}
