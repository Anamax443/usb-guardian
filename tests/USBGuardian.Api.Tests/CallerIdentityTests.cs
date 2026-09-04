// ============================================================
// CallerIdentityTests.cs
// ParseMachineHostname je čistá řetězcová funkce (bez závislosti na skutečné
// WindowsIdentity - tu nejde v testu snadno sestrojit) - "co by server viděl
// v autentizované identitě agenta" versus "co agent tvrdí v datech" (audit 04.09.2026).
// ============================================================

using USBGuardian.Api.Security;
using Xunit;

namespace USBGuardian.Api.Tests;

public class CallerIdentityTests
{
    [Fact]
    public void Extracts_bare_hostname_from_a_domain_machine_account()
    {
        Assert.Equal("PC-01", CallerIdentity.ParseMachineHostname(@"AXINETWORK\PC-01$"));
    }

    [Fact]
    public void Works_without_a_domain_prefix_too()
    {
        Assert.Equal("PC-01", CallerIdentity.ParseMachineHostname("PC-01$"));
    }

    [Fact]
    public void Is_case_preserving_not_normalized()
    {
        // Porovnání volající strany je case-insensitive (OrdinalIgnoreCase) - tahle
        // funkce sama velikost písmen nemění, jen odřízne doménu a koncové $.
        Assert.Equal("trnkamw11", CallerIdentity.ParseMachineHostname(@"AXINETWORK\trnkamw11$"));
    }

    [Fact]
    public void Returns_null_for_a_user_account_no_trailing_dollar()
    {
        // Uživatelský účet (interaktivní přihlášení admina, ruční test API) není stroj -
        // nedá se z něj tvrdit žádný hostname, takže se nemá s čím porovnávat.
        Assert.Null(CallerIdentity.ParseMachineHostname(@"AXINETWORK\trnkam"));
    }

    [Fact]
    public void Returns_null_for_null_or_empty_name()
    {
        Assert.Null(CallerIdentity.ParseMachineHostname(null));
        Assert.Null(CallerIdentity.ParseMachineHostname(""));
    }

    [Fact]
    public void A_lone_dollar_sign_does_not_produce_an_empty_hostname()
    {
        // Bez téhle pojistky by "DOMENA\$" vrátilo "" a to by se mohlo shodovat
        // s prázdným/chybějícím Hostname v datech - false "match".
        Assert.Null(CallerIdentity.ParseMachineHostname(@"AXINETWORK\$"));
    }
}
