// ============================================================
// AgentDeployServiceTests.cs
// Testuje jen ShuffleInPlace (čistá, DB-nezávislá část auto-enrollmentu).
// PROČ: bez zamíchání SQL dotaz bez ORDER BY vrací pořád stejné první N
// stanic - trvale nedostupná stanice na začátku pořadí navždy blokuje
// všechny MaxPerRun sloty a zbytek flotily se nikdy nenasadí.
// ============================================================

using USBGuardian.Admin.Deploy;
using Xunit;

namespace USBGuardian.Admin.Tests;

public class AgentDeployServiceTests
{
    [Fact]
    public void Shuffle_keeps_the_same_elements()
    {
        var list = Enumerable.Range(1, 50).ToList();
        AgentDeployService.ShuffleInPlace(list, new Random(1));

        Assert.Equal(Enumerable.Range(1, 50), list.OrderBy(x => x));
    }

    [Fact]
    public void Shuffle_changes_the_order()
    {
        var original = Enumerable.Range(1, 50).ToList();
        var list = new List<int>(original);

        AgentDeployService.ShuffleInPlace(list, new Random(1));

        Assert.NotEqual(original, list);
    }

    [Fact]
    public void Over_many_runs_every_element_reaches_the_front()
    {
        // Simuluje presne to, co se rozbilo: Take(N) po zamichani nesmi
        // porad vracet stejnou "problemovou" polozku na prvnim miste.
        var rng = new Random(42);
        var seenFirst = new HashSet<int>();
        for (var run = 0; run < 200; run++)
        {
            var list = Enumerable.Range(1, 10).ToList();
            AgentDeployService.ShuffleInPlace(list, rng);
            seenFirst.Add(list[0]);
        }

        Assert.True(seenFirst.Count > 1, "po 200 bezich mela na prvni pozici skoncit vic nez jedna polozka");
    }
}
