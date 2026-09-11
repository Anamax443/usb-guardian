// ============================================================
// LocalConsoleCsrfTests.cs
// IsSameOrigin je čistá verze CSRF kontroly zapisujících endpointů lokální konzole
// (bez HttpListenerContext, který se v testu nedá snadno sestrojit).
// Nález z oponentury 11.09.2026: zapisující endpointy (/api/override, /api/unblock-all,
// /api/restart, /api/selfrestart) neměly žádnou kontrolu Origin/Referer - loopback +
// Integrated Windows Auth samy o sobě nebrání cizí stránce v prohlížeči přihlášeného
// admina poslat POST na 127.0.0.1:<port> (Windows auth handshake proběhne automaticky
// za útočnou stránku). Test ověřuje, že chybějící/cizí Origin (i bez Refereru) je
// vždy odmítnut - fail-closed.
// ============================================================

using USBGuardian.LocalConsole;
using Xunit;

namespace USBGuardian.Agent.Tests;

public class LocalConsoleCsrfTests
{
    private const string ExpectedOrigin = "http://127.0.0.1:5080";

    [Fact]
    public void Matching_origin_header_is_allowed()
    {
        Assert.True(LocalConsoleService.IsSameOrigin(ExpectedOrigin, null, ExpectedOrigin));
    }

    [Fact]
    public void Foreign_origin_header_is_rejected_even_with_no_referer()
    {
        Assert.False(LocalConsoleService.IsSameOrigin("http://evil.example", null, ExpectedOrigin));
    }

    [Fact]
    public void Missing_origin_falls_back_to_referer()
    {
        Assert.True(LocalConsoleService.IsSameOrigin(null, "http://127.0.0.1:5080/", ExpectedOrigin));
        Assert.False(LocalConsoleService.IsSameOrigin(null, "http://evil.example/", ExpectedOrigin));
    }

    [Fact]
    public void Missing_origin_and_referer_is_rejected_fail_closed()
    {
        // Legitimní volání z vlastního dashboardu (fetch POST) má vždy aspoň Origin.
        // Chybí-li oboje, jde o něco jiného než browser fetch z konzole - odmítnout.
        Assert.False(LocalConsoleService.IsSameOrigin(null, null, ExpectedOrigin));
    }

    [Fact]
    public void Origin_check_is_case_insensitive_but_exact_on_port()
    {
        Assert.True(LocalConsoleService.IsSameOrigin("HTTP://127.0.0.1:5080", null, ExpectedOrigin));
        Assert.False(LocalConsoleService.IsSameOrigin("http://127.0.0.1:5081", null, ExpectedOrigin));
    }
}
