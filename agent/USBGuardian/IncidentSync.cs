// ============================================================
// IncidentSync.cs
// Odesílá denní log soubory na REST API server.
// Delta sync – odesílá jen nové záznamy od posledního odeslání.
//
// v1.1 – fix duplikátů: offset persistuje na disk (.offset soubor)
//   Restart agenta už nezpůsobí opakované odesílání celého souboru.
//   Offset soubor se smaže spolu s log souborem při přesunu do sent\.
//
// v1.2 – DisconnectedAt v payloadu:
//   Záznamy s vyplněným DisconnectedAt se odesílají i opakovaně,
//   pokud se DisconnectedAt změnil od posledního odeslání.
// ============================================================

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace USBGuardian;

public class IncidentSync : BackgroundService
{
    private readonly ILogger<IncidentSync> _logger;
    private readonly string _syncUrl;
    private readonly IncidentLogger _incidentLogger;
    private readonly HttpClient _httpClient;
    private readonly int _syncIntervalMinutes;

    public IncidentSync(
        ILogger<IncidentSync> logger,
        string syncUrl,
        IncidentLogger incidentLogger,
        int syncIntervalMinutes = 1)
    {
        _logger              = logger;
        _syncUrl             = syncUrl;
        _incidentLogger      = incidentLogger;
        _syncIntervalMinutes = syncIntervalMinutes;

        var handler = new HttpClientHandler { UseDefaultCredentials = true };
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var (files, records) = _incidentLogger.GetQueueStats();
        _logger.LogInformation(
            "IncidentSync spuštěn – interval: {Min} min, ve frontě: {Files} souborů, {Records} záznamů",
            _syncIntervalMinutes, files, records);

        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await TrySyncFiles();
            await Task.Delay(TimeSpan.FromMinutes(_syncIntervalMinutes), stoppingToken);
        }
    }

    private async Task TrySyncFiles()
    {
        try
        {
            var files = _incidentLogger.GetFilesReadyToSync();

            if (files.Count == 0)
            {
                _logger.LogInformation("IncidentSync: fronta prázdná – žádné soubory k odeslání");
                return;
            }

            foreach (var filePath in files)
                await SendFile(filePath);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("IncidentSync: API nedostupné – soubory čekají: {Msg}", ex.Message);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("IncidentSync: timeout – zkusím příště");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IncidentSync: neočekávaná chyba");
        }
    }

    // --------------------------------------------------------
    // Odeslání jednoho denního souboru s persistovaným offsetem
    // --------------------------------------------------------
    private async Task SendFile(string filePath)
    {
        var fileName   = Path.GetFileName(filePath);
        var offsetPath = filePath + ".offset"; // soubor s číslem posledního odeslaného záznamu

        try
        {
            var json  = await File.ReadAllTextAsync(filePath);
            var daily = JsonSerializer.Deserialize<DailyLog>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (daily == null || daily.Records.Count == 0)
            {
                if (!_incidentLogger.IsTodaysFile(filePath))
                    _incidentLogger.MoveTeSent(filePath);
                return;
            }

            // Načíst offset z disku (přežije restart agenta)
            var alreadySent = ReadOffset(offsetPath);

            // Nové záznamy od posledního odeslání
            var newRecords = daily.Records.Skip(alreadySent).ToList();

            // Záznamy kde se změnil DisconnectedAt (byl null, teď má hodnotu)
            // Tyto záznamy potřebujeme aktualizovat na serveru i když jsou "staré"
            var updatedDisconnects = daily.Records
                .Take(alreadySent)
                .Where(r => r.DisconnectedAt.HasValue)
                .ToList();

            if (newRecords.Count == 0 && updatedDisconnects.Count == 0)
            {
                _logger.LogInformation(
                    "IncidentSync: {File} – žádné nové záznamy ({Sent}/{Total} odesláno)",
                    fileName, alreadySent, daily.Records.Count);
                return;
            }

            _logger.LogInformation(
                "IncidentSync: {File} – {New} nových, {Upd} disconnect aktualizací",
                fileName, newRecords.Count, updatedDisconnects.Count);

            // Kombinujeme nové záznamy + disconnect aktualizace
            var toSend = newRecords.Concat(updatedDisconnects).ToList();

            var request = new
            {
                hostname     = Environment.MachineName,
                agentVersion = "1.0.0",
                sourceFile   = fileName,
                incidents    = toSend.Select(r => new
                {
                    timestamp        = r.Timestamp,
                    disconnectedAt   = r.DisconnectedAt,
                    username         = r.Username,
                    vendorId         = r.VendorId,
                    productId        = r.ProductId,
                    serialNumber     = r.SerialNumber,
                    friendlyName     = r.FriendlyName,
                    deviceType       = r.DeviceType,
                    sizeBytes        = r.SizeBytes,
                    firmwareRevision = r.FirmwareRevision,
                    pnpDeviceId      = r.PnpDeviceId,
                    action           = r.Action,
                    whitelistVersion = r.WhitelistVersion,
                    sourceFile       = fileName
                }).ToList()
            };

            var content  = new StringContent(
                JsonSerializer.Serialize(request), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"{_syncUrl}/api/incidents", content);

            if (response.IsSuccessStatusCode)
            {
                // Persistovat nový offset na disk
                var newOffset = alreadySent + newRecords.Count;
                WriteOffset(offsetPath, newOffset);

                _logger.LogInformation(
                    "IncidentSync: {File} – odesláno OK, offset: {Offset}/{Total}",
                    fileName, newOffset, daily.Records.Count);

                if (!_incidentLogger.IsTodaysFile(filePath))
                {
                    // MoveTeSent smaže i .offset soubor
                    _incidentLogger.MoveTeSent(filePath);
                }
            }
            else
            {
                _logger.LogWarning(
                    "IncidentSync: {File} – selhalo: {Status}", fileName, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IncidentSync: chyba při odesílání {File}", fileName);
        }
    }

    // --------------------------------------------------------
    // Offset persistance – čtení a zápis .offset souboru
    // Formát: jediné číslo (int) jako text
    // --------------------------------------------------------
    private static int ReadOffset(string offsetPath)
    {
        try
        {
            if (File.Exists(offsetPath))
                return int.TryParse(File.ReadAllText(offsetPath).Trim(), out var n) ? n : 0;
        }
        catch { }
        return 0;
    }

    private static void WriteOffset(string offsetPath, int offset)
    {
        try
        {
            File.WriteAllText(offsetPath, offset.ToString());
        }
        catch { }
    }

    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }
}
