// ============================================================
// DeployResultIngestor.cs
// Čte, co Deploy-AgentFleet.ps1 zapsal LOKÁLNĚ na APP_SERVER (last.csv,
// last.log) - běží tam pod deploy gMSA přes scheduled task, na stejném
// stroji jako konzole. Žádný vzdálený přístup, jen čtení souboru na
// vlastním disku - proto to jde bezpečně z BackgroundService.
//
// PROČ: dřív bylo v Aktivitě vidět jen "spuštěna úloha", nikdy PROČ
// instalace na cíli neuspěla (offline, sc create FAIL, skript vůbec
// nenaběhl kvůli AllSigned...). last.csv existuje JEN když skript
// doběhl až k Export-Csv - když shodí PŘED tím (např. není podepsaný),
// zůstane jen last.log s transkriptem a "THROW: ...".
// ============================================================

using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;
using USBGuardian.Api.Models;

namespace USBGuardian.Admin.Deploy;

public sealed record DeployCsvRow(string Host, string Status, string Detail, string Ts);

public static class DeployResultIngestor
{
    public const string DefaultCsvPath = @"C:\ProgramData\USBGuardian\deploy\last.csv";
    public const string DefaultLogPath = @"C:\ProgramData\USBGuardian\deploy\last.log";
    public const string ManualCsvPath = @"C:\ProgramData\USBGuardian\deploy\manual-last.csv";
    public const string ManualLogPath = @"C:\ProgramData\USBGuardian\deploy\manual-last.log";

    private const string Source = "deploy-run";

    // "kind" odlišuje sledovací klíče (kam se doteklo naposled) mezi auto-enrollmentem
    // a ruční instalací - mají VLASTNÍ soubory (last.csv vs. manual-last.csv), takže
    // sdílet stejné "deploy.lastIngestedCsvUtc" by znamenalo, že druhý běh přeskočí
    // čtení, protože si myslí, že už ten soubor zpracoval.
    public static async Task RunAsync(AppDbContext db, ActivityLogger dennik,
                                       string csvPath = DefaultCsvPath, string logPath = DefaultLogPath,
                                       string kind = "auto")
    {
        var csvUtc = await IngestCsvAsync(db, dennik, csvPath, kind);
        // Log se hlásí, jen když je novější než poslední zpracovaný CSV o víc než pár
        // vteřin (a než sám sebe) - jinak by stejný "THROW" naskakoval do Aktivity znovu
        // po každém dalším cyklu, dokud ho někdo neopraví.
        await IngestLogFailureAsync(db, dennik, logPath, csvUtc, kind);
    }

    private static async Task<DateTime?> IngestCsvAsync(AppDbContext db, ActivityLogger dennik, string csvPath, string kind)
    {
        var csvIngestedKey = $"deploy.lastIngestedCsvUtc.{kind}";
        if (!File.Exists(csvPath)) return null;
        var writtenUtc = File.GetLastWriteTimeUtc(csvPath);
        var lastIngested = await GetTimestamp(db, csvIngestedKey);
        if (lastIngested is { } t && writtenUtc <= t) return writtenUtc;

        List<DeployCsvRow> rows;
        try { rows = ParseCsv(await File.ReadAllTextAsync(csvPath)); }
        catch (Exception ex)
        {
            dennik.Log(Source, $"Nelze přečíst {csvPath}: {ex.Message}", ActivityLevel.Error);
            await SetTimestamp(db, csvIngestedKey, writtenUtc);
            return writtenUtc;
        }

        foreach (var row in rows)
            dennik.Log(Source, $"{row.Status}: {row.Detail}", LevelForStatus(row.Status), row.Host);

        await SetTimestamp(db, csvIngestedKey, writtenUtc);
        return writtenUtc;
    }

    private static async Task IngestLogFailureAsync(AppDbContext db, ActivityLogger dennik, string logPath,
                                                     DateTime? justIngestedCsvUtc, string kind)
    {
        var logIngestedKey = $"deploy.lastIngestedLogUtc.{kind}";
        if (!File.Exists(logPath)) return;
        var writtenUtc = File.GetLastWriteTimeUtc(logPath);
        var lastIngested = await GetTimestamp(db, logIngestedKey);
        if (lastIngested is { } t && writtenUtc <= t) return;

        // last.csv a last.log ze STEJNÉHO úspěšného běhu vznikají v rychlém sledu (Export-Csv
        // hned po skončení runspace poolu, transkript se zavře pár řádků potom) - pár vteřin
        // od sebe. Když je log novější o víc, csv z tohohle běhu vůbec nevzniklo => skript
        // spadl dřív, než se dostal k instalaci (typicky AllSigned na neopodepsaný soubor).
        if (justIngestedCsvUtc is { } csvUtc && (writtenUtc - csvUtc).Duration() < TimeSpan.FromSeconds(5))
        {
            await SetTimestamp(db, logIngestedKey, writtenUtc);
            return;
        }

        var throwLine = ExtractThrowLine(await File.ReadAllTextAsync(logPath));
        if (throwLine is not null)
            dennik.Log(Source, $"Deploy skript selhal, žádný cíl z tohoto běhu se nenainstaloval: {throwLine}", ActivityLevel.Error);

        await SetTimestamp(db, logIngestedKey, writtenUtc);
    }

    // ── čisté, testovatelné ──────────────────────────────────────

    public static ActivityLevel LevelForStatus(string status) => status switch
    {
        "OK" or "WOULD-DEPLOY" => ActivityLevel.Info,
        "SKIP" or "STARTED?"   => ActivityLevel.Warn,
        _                      => ActivityLevel.Error,   // FAIL, OFFLINE, cokoliv neznámého
    };

    public static string? ExtractThrowLine(string transcript)
    {
        var line = transcript
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("THROW:", StringComparison.Ordinal));
        return line is null ? null : line["THROW:".Length..].Trim();
    }

    // Export-Csv (Windows PowerShell 5.1, výchozí) - RFC4180-ish: pole v uvozovkách při obsahu
    // čárky/uvozovky/nového řádku, zdvojená "" jako escaped uvozovka.
    public static List<DeployCsvRow> ParseCsv(string csvText)
    {
        var lines = SplitCsvLines(csvText);
        if (lines.Count == 0) return new List<DeployCsvRow>();

        var header = ParseCsvLine(lines[0]);
        int Idx(string name) => header.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
        int iHost = Idx("Host"), iStatus = Idx("Status"), iDetail = Idx("Detail"), iTs = Idx("Ts");

        string Get(List<string> f, int i) => i >= 0 && i < f.Count ? f[i] : "";

        var rows = new List<DeployCsvRow>();
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i].Length == 0) continue;
            var f = ParseCsvLine(lines[i]);
            rows.Add(new DeployCsvRow(Get(f, iHost), Get(f, iStatus), Get(f, iDetail), Get(f, iTs)));
        }
        return rows;
    }

    // Rozdělí na řádky, ale ne uprostřed uvozovkovaného pole (kde smí být vložený \n).
    private static List<string> SplitCsvLines(string text)
    {
        var lines = new List<string>();
        var cur = new StringBuilder();
        var inQuotes = false;
        foreach (var c in text)
        {
            if (c == '"') inQuotes = !inQuotes;
            if (c == '\n' && !inQuotes)
            {
                lines.Add(cur.ToString().TrimEnd('\r'));
                cur.Clear();
            }
            else cur.Append(c);
        }
        if (cur.Length > 0) lines.Add(cur.ToString().TrimEnd('\r'));
        return lines;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }

    private static async Task<DateTime?> GetTimestamp(AppDbContext db, string key)
    {
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        return DateTime.TryParse(row?.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;
    }

    private static async Task SetTimestamp(AppDbContext db, string key, DateTime utc)
    {
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        var value = utc.ToString("o", CultureInfo.InvariantCulture);
        if (row is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
        await db.SaveChangesAsync();
    }
}
