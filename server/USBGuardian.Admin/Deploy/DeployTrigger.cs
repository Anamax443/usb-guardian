// ============================================================
// DeployTrigger.cs
// Ruční "nasadit teď" / "aktualizovat teď" na jednu stanici.
//
// PROČ TO EXISTUJE:
//   Auto-enrollment je vypnutý a úloha na APP_SERVER nemá časový spouštěč, takže
//   se sama nespustí nikdy. Pilulka "nasadí se" ve Stanicích říká jen "byla
//   by mezi cíli", ne "stane se". Bez tlačítka musel člověk zapisovat
//   hostname do souboru na serveru a spouštět úlohu ručně — což znamená,
//   že to nikdo neudělá.
//
// DĚLBA PRÁCE ZŮSTÁVÁ:
//   Konzole (LocalSystem na APP_SERVER) jen ZAPÍŠE cíl a ŠŤOUCHNE do úlohy.
//   Vlastní instalaci dělá úloha pod deploy účtem, který jediný má admina
//   na stanicích. Konzole nikam nekopíruje a nikde nespouští službu — kdyby
//   uměla obojí, byla by z webové aplikace cesta na 200 počítačů.
//
// Vše je konfigurovatelné (AppSettings), nic natvrdo:
//   deploy.targetsFile / deploy.taskName             – čistá instalace
//   deploy.updateTargetsFile / deploy.updateTaskName  – aktualizace (stable)
//   deploy.updateBetaTaskName                         – rozvoz bety na vzorek
//     (cílový soubor update-beta.txt píše Settings.razor při ukládání verze,
//     ne tenhle typ – vzorek je stálý seznam, ne volba za běhu jako u stable)
// ============================================================

using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;

namespace USBGuardian.Admin.Deploy;

public sealed class DeployTrigger
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<DeployTrigger> _logger;
    private readonly ActivityLogger _dennik;

    public DeployTrigger(IDbContextFactory<AppDbContext> dbFactory, ILogger<DeployTrigger> logger,
                         ActivityLogger dennik)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _dennik = dennik;
    }

    // POZOR: DefaultTargetsFile/DefaultTaskName níž jsou historické názvy, ale MÍŘÍ na
    // ruční instalaci (Akce.Instalace) - proto mají VLASTNÍ AppSettings klíče
    // (deploy.manualTargetsFile/deploy.manualTaskName), oddělené od auto-enrollmentu
    // (AgentDeployService čte deploy.targetsFile/deploy.taskName). Dřív sdílely STEJNÝ
    // klíč deploy.targetsFile/deploy.taskName jako auto-enrollment - ruční klik a
    // automatický cyklus si tak uměly navzájem přepsat cíle uprostřed běhu
    // (incident 10.09.2026: klik na PARLW11, výsledek přišel pro CERNYSW11 z auto-enrollmentu).
    public const string DefaultTargetsFile = @"C:\ProgramData\USBGuardian\deploy\manual-targets.txt";
    public const string DefaultTaskName = @"\USBGuardian\USBGuardian-ManualInstall";
    public const string DefaultUpdateTargetsFile = @"C:\ProgramData\USBGuardian\deploy\update.txt";
    public const string DefaultUpdateTaskName = @"\USBGuardian\USBGuardian-UpdateAgent";

    // Beta ma jiny tvar nez stable/instalace: cil neni JEDNA stanice zvolena v okamziku
    // kliknuti, ale STÁLÝ vzorek z Nastaveni (update-beta.txt), ktery uz drzi ZapisBetaHosts()
    // v Settings.razor pri ukladani verze. Sem se tedy nepredava hostname - jen se stouchne
    // do ulohy, ktera si soubor precte sama.
    public const string DefaultUpdateBetaTaskName = @"\USBGuardian\USBGuardian-UpdateAgentBeta";

    public enum Akce { Instalace, Aktualizace }

    /// <summary>
    /// Zapíše jednu stanici jako cíl a spustí příslušnou úlohu.
    /// Vrací hlášku pro uživatele – i při neúspěchu, ať je vidět, co se stalo.
    /// </summary>
    public async Task<(bool Ok, string Zprava)> SpustAsync(string hostname, Akce akce, string kdo,
                                                           CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return (false, "Chybí hostname.");

        string targetsFile, taskName;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            async Task<string> Get(string k, string vychozi)
            {
                var v = (await db.AppSettings.FirstOrDefaultAsync(s => s.Key == k, ct))?.Value;
                return string.IsNullOrWhiteSpace(v) ? vychozi : v.Trim();
            }

            targetsFile = akce == Akce.Instalace
                ? await Get("deploy.manualTargetsFile", DefaultTargetsFile)
                : await Get("deploy.updateTargetsFile", DefaultUpdateTargetsFile);
            taskName = akce == Akce.Instalace
                ? await Get("deploy.manualTaskName", DefaultTaskName)
                : await Get("deploy.updateTaskName", DefaultUpdateTaskName);
        }
        catch (Exception ex)
        {
            return (false, "Nelze načíst nastavení nasazení: " + Kratce(ex.Message));
        }

        // 1) cíl
        try
        {
            var dir = Path.GetDirectoryName(targetsFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(targetsFile, hostname.Trim() + Environment.NewLine, ct);
        }
        catch (Exception ex)
        {
            return (false, $"Nelze zapsat cíl do {targetsFile}: " + Kratce(ex.Message));
        }

        // 2) šťouchnutí do úlohy
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Run /TN \"{taskName}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (false, "Úlohu se nepodařilo spustit (proces nevznikl).");

            var vystup = (await p.StandardOutput.ReadToEndAsync(ct)
                        + await p.StandardError.ReadToEndAsync(ct)).Trim();
            await p.WaitForExitAsync(ct);

            if (p.ExitCode != 0)
            {
                _logger.LogWarning("Ruční nasazení {Host}: schtasks skončil {Code}: {Vystup}",
                    hostname, p.ExitCode, vystup);
                _dennik.Log("deploy", $"úlohu {taskName} se nepodařilo spustit (kód {p.ExitCode})",
                    ActivityLevel.Error, hostname, kdo);
                return (false, $"Úloha {taskName} nešla spustit (kód {p.ExitCode}): {Kratce(vystup)}");
            }

            _logger.LogWarning("Ruční {Akce} stanice {Host} vyžádána ({Kdo}) – spuštěna úloha {Task}",
                akce, hostname, kdo, taskName);
            _dennik.Log("deploy",
                (akce == Akce.Instalace ? "ruční instalace agenta" : "ruční aktualizace agenta")
                + $" – spuštěna úloha {taskName}",
                ActivityLevel.Warn, hostname, kdo);

            // Ruční kliknutí má dát rychlou zpětnou vazbu, ne čekat na dalsi tik
            // AgentDeployService (ten běží nezávisle, řádově v minutách) - proto
            // se na pozadí (nesvázané s životem tohoto HTTP/circuit volání) hlídá,
            // až úloha doběhne, a hned se natáhne manual-last.csv/log do Aktivity.
            // Jen pro Instalaci - Aktualizace/beta nejedou přes Deploy-AgentFleet.ps1,
            // takže nemají co v těchhle souborech hledat (signál je sloupec Agent verze).
            if (akce == Akce.Instalace)
                _ = SledujADoplnAktivituAsync(taskName, DeployResultIngestor.ManualCsvPath,
                    DeployResultIngestor.ManualLogPath, "manual");

            return (true, akce == Akce.Instalace
                ? $"Instalace na {hostname} spuštěna. Průběh: log úlohy na serveru, výsledek se projeví v Posledním kontaktu (do ~2 min po startu agenta)."
                : $"Aktualizace {hostname} spuštěna. Až doběhne, ukáže se nová verze ve sloupci Agent verze.");
        }
        catch (Exception ex)
        {
            return (false, "Spuštění úlohy selhalo: " + Kratce(ex.Message));
        }
    }

    /// <summary>
    /// Rozveze aktualni obsah beta kanalu na cely schvaleny vzorek stanic (Nastaveni →
    /// Beta stanice). Bez tlacitka to slo jen rucne přes RDP + schtasks - stable uz svuj
    /// spoustec ma (SpustAsync/Aktualizace), beta ne.
    /// </summary>
    public async Task<(bool Ok, string Zprava)> SpustBetuAsync(string kdo, CancellationToken ct = default)
    {
        string taskName;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var v = (await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "deploy.updateBetaTaskName", ct))?.Value;
            taskName = string.IsNullOrWhiteSpace(v) ? DefaultUpdateBetaTaskName : v.Trim();
        }
        catch (Exception ex)
        {
            return (false, "Nelze načíst nastavení nasazení: " + Kratce(ex.Message));
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Run /TN \"{taskName}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (false, "Úlohu se nepodařilo spustit (proces nevznikl).");

            var vystup = (await p.StandardOutput.ReadToEndAsync(ct)
                        + await p.StandardError.ReadToEndAsync(ct)).Trim();
            await p.WaitForExitAsync(ct);

            if (p.ExitCode != 0)
            {
                _logger.LogWarning("Rozvoz bety: schtasks skončil {Code}: {Vystup}", p.ExitCode, vystup);
                _dennik.Log("deploy", $"úlohu {taskName} se nepodařilo spustit (kód {p.ExitCode})",
                    ActivityLevel.Error, null, kdo);
                return (false, $"Úloha {taskName} nešla spustit (kód {p.ExitCode}): {Kratce(vystup)}");
            }

            _logger.LogWarning("Rozvoz bety na vzorek vyžádán ({Kdo}) – spuštěna úloha {Task}", kdo, taskName);
            _dennik.Log("deploy", $"ruční rozvoz beta kanálu na vzorek – spuštěna úloha {taskName}",
                ActivityLevel.Warn, null, kdo);

            // Beta jede přes Update-Agent.cmd, ne Deploy-AgentFleet.ps1 - nemá last.csv/log
            // ke čtení (signál je sloupec Agent verze u vzorkových stanic, viz zpráva níž).

            return (true,
                "Rozvoz na vzorek beta spuštěn. Výsledek se ukáže ve sloupci Agent verze u vzorkových stanic (do pár minut).");
        }
        catch (Exception ex)
        {
            return (false, "Spuštění úlohy selhalo: " + Kratce(ex.Message));
        }
    }

    /// <summary>
    /// Aktualizuje CELÝ fleet na aktuální stable verzi - všechny stanice s agentem, které na ní
    /// ještě nejsou, KROMĚ betatesterů (ti mají svůj vlastní kanál a mají na jiné verzi zůstat
    /// záměrně). Používá stejnou úlohu/soubor jako jednotlivá ruční "Aktualizace"
    /// (deploy.updateTaskName/deploy.updateTargetsFile) - jen napíše víc hostnames najednou.
    /// </summary>
    public async Task<(bool Ok, string Zprava)> SpustFleetUpdateAsync(string kdo, CancellationToken ct = default)
    {
        string taskName, targetsFile, stableVersion;
        List<string> cile;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            async Task<string> Get(string k, string vychozi = "")
            {
                var v = (await db.AppSettings.FirstOrDefaultAsync(s => s.Key == k, ct))?.Value;
                return string.IsNullOrWhiteSpace(v) ? vychozi : v.Trim();
            }

            taskName    = await Get("deploy.updateTaskName", DefaultUpdateTaskName);
            targetsFile = await Get("deploy.updateTargetsFile", DefaultUpdateTargetsFile);
            stableVersion = await Get("agent.version.stable");
            if (string.IsNullOrWhiteSpace(stableVersion))
                return (false, "Stable verze není nastavená (Nastavení → Verze agenta).");

            var betaHosts = (await Get("agent.betaHosts"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            cile = await db.Computers
                .Where(c => c.AgentVersion != "" && c.AgentVersion != stableVersion)
                .Select(c => c.Hostname)
                .ToListAsync(ct);
            cile = cile.Where(h => !betaHosts.Contains(h)).ToList();
        }
        catch (Exception ex)
        {
            return (false, "Nelze připravit seznam stanic: " + Kratce(ex.Message));
        }

        if (cile.Count == 0)
            return (true, $"Žádné stanice k aktualizaci — všechny (mimo betatestery) už mají stable {stableVersion}.");

        try
        {
            var dir = Path.GetDirectoryName(targetsFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllLinesAsync(targetsFile, cile, ct);
        }
        catch (Exception ex)
        {
            return (false, $"Nelze zapsat cíle do {targetsFile}: " + Kratce(ex.Message));
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Run /TN \"{taskName}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return (false, "Úlohu se nepodařilo spustit (proces nevznikl).");

            var vystup = (await p.StandardOutput.ReadToEndAsync(ct)
                        + await p.StandardError.ReadToEndAsync(ct)).Trim();
            await p.WaitForExitAsync(ct);

            if (p.ExitCode != 0)
            {
                _logger.LogWarning("Fleet update: schtasks skončil {Code}: {Vystup}", p.ExitCode, vystup);
                _dennik.Log("deploy", $"úlohu {taskName} se nepodařilo spustit (kód {p.ExitCode})",
                    ActivityLevel.Error, null, kdo);
                return (false, $"Úloha {taskName} nešla spustit (kód {p.ExitCode}): {Kratce(vystup)}");
            }

            _logger.LogWarning("Fleet update na stable {Verze} vyžádán ({Kdo}) – {Pocet} stanic, úloha {Task}",
                stableVersion, kdo, cile.Count, taskName);
            _dennik.Log("deploy",
                $"hromadná aktualizace na stable {stableVersion} – {cile.Count} stanic (mimo betatestery), spuštěna úloha {taskName}",
                ActivityLevel.Warn, null, kdo);

            return (true, $"Aktualizace na stable {stableVersion} spuštěna pro {cile.Count} stanic "
                        + "(mimo betatestery). Výsledek se ukáže postupně ve sloupci Agent verze.");
        }
        catch (Exception ex)
        {
            return (false, "Spuštění úlohy selhalo: " + Kratce(ex.Message));
        }
    }

    // Hlídá běžící úlohu (polling schtasks /Query po 3s, max 3 min) a jakmile doběhne
    // (nebo vyprší čas), natáhne last.csv/last.log do Aktivity přes DeployResultIngestor -
    // stejný soubor a stejná logika jako u automatického běhu, jen bez čekání na jeho tik.
    // Běží nesvázaně s HTTP/circuit voláním, které nasazení spustilo - proto CancellationToken.None
    // a vlastní try/catch (nic z tohohle nesmí shodit appku, viz ActivityLogger).
    private async Task SledujADoplnAktivituAsync(string taskName, string csvPath, string logPath, string kind)
    {
        try
        {
            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                if (!await UlohaBeziAsync(taskName)) break;
            }

            await using var db = await _dbFactory.CreateDbContextAsync();
            await DeployResultIngestor.RunAsync(db, _dennik, csvPath, logPath, kind);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sledování výsledku ručního nasazení ({Task}) selhalo", taskName);
        }
    }

    private static async Task<bool> UlohaBeziAsync(string taskName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN \"{taskName}\" /FO LIST",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;

            var vystup = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return vystup.Contains("Running", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string Kratce(string s, int max = 160)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
