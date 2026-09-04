// ============================================================
// IncidentSpool.cs
// Durabilní vrstva pod IncidentQueue – zapisuje přijatý batch na disk
// JEŠTĚ PŘED tím, než controller vrátí 202 Accepted. Agent po 2xx odpovědi
// posune offset a batch už nepošle znovu (IncidentSync.cs na klientovi) –
// pokud by mezi 202 a zápisem do DB spadl proces API, batch žijící jen
// v paměťovém Channelu (IncidentQueue) by byl nenávratně ztracen.
//
// Worker soubor smaže až PO úspěšném zápisu do DB. Cokoliv na disku zbyde
// (nedoběhlo do DB, nebo doběhlo a proces spadl těsně před smazáním) se při
// dalším startu služby – i běžném denním restartu, ne jen pádu – přehraje
// jako první, před čerstvým provozem (IncidentQueueWorker.ReplaySpoolAsync).
// Případné vícenásobné přehrání řeší existující dedup v ProcessBatch (klíč
// timestamp|serial|vendor za posledních 24 h), takže at-least-once je bezpečné.
// ============================================================

using System.Text.Json;
using USBGuardian.Api.Models;

namespace USBGuardian.Api.Queue;

public class IncidentSpool
{
    private readonly string _path;
    private readonly ILogger<IncidentSpool> _logger;

    public IncidentSpool(IConfiguration config, ILogger<IncidentSpool> logger)
    {
        _logger = logger;
        _path   = config["incidents:spoolPath"] ?? @"C:\ProgramData\USBGuardian\incident-spool";
        Directory.CreateDirectory(_path);
    }

    /// <summary>
    /// Zapíše batch na disk dřív, než ho controller potvrdí agentovi.
    /// Atomický zápis (temp + move) – pád uprostřed zápisu nenechá napůl
    /// dopsaný soubor, který by LoadPending nešel přečíst.
    /// Vyhodí výjimku při chybě – volající (controller) to musí propsat jako
    /// neúspěch (žádné 202), ať agent pošle batch znovu.
    /// </summary>
    public string Write(IncidentBatchRequest request, string? sourceIp, DateTime receivedAt)
    {
        var fileName  = $"{receivedAt:yyyyMMdd-HHmmss-fff}_{Guid.NewGuid():N}.json";
        var finalPath = Path.Combine(_path, fileName);
        var tempPath  = finalPath + ".tmp";

        var record = new SpoolRecord(request, sourceIp, receivedAt);
        File.WriteAllText(tempPath, JsonSerializer.Serialize(record));
        File.Move(tempPath, finalPath, overwrite: false);

        return finalPath;
    }

    /// <summary>Smaže spool soubor po úspěšném zápisu do DB. Neúspěch jen zaloguje –
    /// nesmazaný soubor znamená pouze to, že se batch přehraje i příště (neškodné).</summary>
    public void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nelze smazat spool soubor {Path} – přehraje se příště", path);
        }
    }

    /// <summary>
    /// Co zbylo na disku z minula – chronologicky (jméno souboru = čas přijetí).
    /// Soubor, který se nepodaří přečíst (poškozený zápis), se odloží stranou
    /// (přípona .bad), ať nezablokuje start služby navždy.
    /// </summary>
    public List<IncidentBatchItem> LoadPending()
    {
        var result = new List<IncidentBatchItem>();

        foreach (var path in Directory.GetFiles(_path, "*.json").OrderBy(p => p))
        {
            try
            {
                var record = JsonSerializer.Deserialize<SpoolRecord>(File.ReadAllText(path))
                             ?? throw new InvalidDataException("prázdný záznam");

                result.Add(new IncidentBatchItem(record.Request, record.SourceIp, record.ReceivedAt, path));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Spool soubor {Path} se nepodařilo přečíst – odkládám jako .bad, dál se nezkouší",
                    path);
                TryQuarantine(path);
            }
        }

        return result;
    }

    private static void TryQuarantine(string path)
    {
        try { File.Move(path, path + ".bad", overwrite: true); }
        catch { /* i tohle je jen diagnostika, nesmí shodit start služby */ }
    }

    private record SpoolRecord(IncidentBatchRequest Request, string? SourceIp, DateTime ReceivedAt);
}
