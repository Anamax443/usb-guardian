// ============================================================
// SessionUser.cs
// Zjištění REÁLNÉHO přihlášeného uživatele z aktivní interaktivní session.
//
// Agent běží jako SYSTEM → Environment.UserName vrací strojový účet (HOST$),
// ne člověka, který médium připojil. WTS API umí přečíst uživatele aktivní
// konzolové (a fallbackem libovolné aktivní) session → "DOMAIN\user".
//
// Fail-safe: když nikdo není přihlášen (zamčeno/odhlášeno/jen služby),
// vrátí Environment.UserName → incident se zaznamená VŽDY, jen se strojovým účtem.
// ============================================================

using System.Runtime.InteropServices;

namespace USBGuardian.Security;

/// <summary>
/// Resolver reálného přihlášeného uživatele přes WTS (Terminal Services) API.
/// Pod SYSTEM nahrazuje <see cref="Environment.UserName"/>, který vrací strojový účet.
/// </summary>
public static class SessionUser
{
    private const uint WTS_INVALID_SESSION = 0xFFFFFFFF;

    /// <summary>
    /// Vrátí "DOMAIN\user" aktivní interaktivní session (nejdřív fyzická konzole,
    /// pak libovolná aktivní – např. RDP). Když žádný interaktivní uživatel není
    /// přihlášen, vrátí <see cref="Environment.UserName"/> (fallback = strojový účet pod SYSTEM).
    /// Nikdy nevyhazuje výjimku.
    /// </summary>
    public static string GetActiveConsoleUser()
    {
        try
        {
            var user = QueryActiveSessionUser();
            return string.IsNullOrWhiteSpace(user) ? Environment.UserName : user!;
        }
        catch
        {
            // P/Invoke selhání nesmí shodit logování incidentu
            return Environment.UserName;
        }
    }

    // --------------------------------------------------------
    // Nejdřív fyzická konzole, pak fallback na enumeraci aktivních session.
    // --------------------------------------------------------
    private static string? QueryActiveSessionUser()
    {
        var consoleSession = WTSGetActiveConsoleSessionId();
        var fromConsole    = GetUserForSession(consoleSession);
        if (!string.IsNullOrEmpty(fromConsole))
            return fromConsole;

        return EnumerateActiveSessionUser();
    }

    // --------------------------------------------------------
    // Uživatel konkrétní session jako "DOMAIN\user" (prázdné = bez interaktivního uživatele).
    // --------------------------------------------------------
    private static string GetUserForSession(uint sessionId)
    {
        if (sessionId == WTS_INVALID_SESSION)
            return string.Empty;

        var user = QueryString(sessionId, WTS_INFO_CLASS.WTSUserName);
        if (string.IsNullOrEmpty(user))
            return string.Empty;   // session bez přihlášeného člověka (např. služby/session 0)

        var domain = QueryString(sessionId, WTS_INFO_CLASS.WTSDomainName);
        return string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}";
    }

    // --------------------------------------------------------
    // Projde všechny session, vrátí uživatele první AKTIVNÍ s přihlášeným člověkem.
    // --------------------------------------------------------
    private static string? EnumerateActiveSessionUser()
    {
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var pSessions, out var count))
            return null;

        try
        {
            var size    = Marshal.SizeOf<WTS_SESSION_INFO>();
            var current = pSessions;

            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(current);
                current  = IntPtr.Add(current, size);

                if (info.State != WTS_CONNECTSTATE_CLASS.WTSActive)
                    continue;

                var user = GetUserForSession((uint)info.SessionId);
                if (!string.IsNullOrEmpty(user))
                    return user;
            }
        }
        finally
        {
            WTSFreeMemory(pSessions);
        }

        return null;
    }

    // --------------------------------------------------------
    // Jeden řetězcový atribut session (Unicode), s uvolněním nativní paměti.
    // --------------------------------------------------------
    private static string QueryString(uint sessionId, WTS_INFO_CLASS infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass,
                out var buffer, out _))
            return string.Empty;

        try
        {
            return Marshal.PtrToStringUni(buffer)?.Trim() ?? string.Empty;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    // ── P/Invoke (Terminal Services) ─────────────────────────

    private enum WTS_INFO_CLASS
    {
        WTSUserName   = 5,
        WTSDomainName = 7
    }

    private enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive,        // 0 – přihlášený a aktivní uživatel
        WTSConnected,
        WTSConnectQuery,
        WTSShadow,
        WTSDisconnected,
        WTSIdle,
        WTSListen,
        WTSReset,
        WTSDown,
        WTSInit
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int    SessionId;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer, uint sessionId, WTS_INFO_CLASS infoClass,
        out IntPtr ppBuffer, out uint pBytesReturned);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true,
        EntryPoint = "WTSEnumerateSessionsW")]
    private static extern bool WTSEnumerateSessions(
        IntPtr hServer, uint reserved, uint version,
        out IntPtr ppSessionInfo, out uint pCount);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);
}
