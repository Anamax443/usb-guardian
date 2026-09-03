// ============================================================
// DeployTrigger.cs
// Ruční "nasadit teď" / "aktualizovat teď" na jednu stanici.
//
// PROČ TO EXISTUJE:
//   Auto-enrollment je vypnutý a úloha na .213 nemá časový spouštěč, takže
//   se sama nespustí nikdy. Pilulka "nasadí se" ve Stanicích říká jen "byla
//   by mezi cíli", ne "stane se". Bez tlačítka musel člověk zapisovat
//   hostname do souboru na serveru a spouštět úlohu ručně — což znamená,
//   že to nikdo neudělá.
//
// DĚLBA PRÁCE ZŮSTÁVÁ:
//   Konzole (LocalSystem na .213) jen ZAPÍŠE cíl a ŠŤOUCHNE do úlohy.
//   Vlastní instalaci dělá úloha pod deploy účtem, který jediný má admina
//   na stanicích. Konzole nikam nekopíruje a nikde nespouští službu — kdyby
//   uměla obojí, byla by z webové aplikace cesta na 200 počítačů.
//
// Vše je konfigurovatelné (AppSettings), nic natvrdo:
//   deploy.targetsFile / deploy.taskName             – čistá instalace
//   deploy.updateTargetsFile / deploy.updateTaskName  – aktualizace (stable)
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

    public const string DefaultTargetsFile = @"C:\ProgramData\USBGuardian\deploy\targets.txt";
    public const string DefaultTaskName = @"\USBGuardian\USBGuardian-AutoDeploy";
    public const string DefaultUpdateTargetsFile = @"C:\ProgramData\USBGuardian\deploy\update.txt";
    public const string DefaultUpdateTaskName = @"\USBGuardian\USBGuardian-UpdateAgent";

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
                ? await Get("deploy.targetsFile", DefaultTargetsFile)
                : await Get("deploy.updateTargetsFile", DefaultUpdateTargetsFile);
            taskName = akce == Akce.Instalace
                ? await Get("deploy.taskName", DefaultTaskName)
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

            // Úloha běží na pozadí – výsledek přijde do jejího logu, ne sem.
            return (true, akce == Akce.Instalace
                ? $"Instalace na {hostname} spuštěna. Průběh: log úlohy na serveru, výsledek se projeví v Posledním kontaktu (do ~2 min po startu agenta)."
                : $"Aktualizace {hostname} spuštěna. Až doběhne, ukáže se nová verze ve sloupci Agent verze.");
        }
        catch (Exception ex)
        {
            return (false, "Spuštění úlohy selhalo: " + Kratce(ex.Message));
        }
    }

    private static string Kratce(string s, int max = 160)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
