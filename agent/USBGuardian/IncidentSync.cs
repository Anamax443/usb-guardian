// ============================================================
// IncidentSync.cs
// Odesílá uzavřené denní log soubory na REST API server.
// Aktuální denní soubor se neodesílá (ještě se zapisuje).
// Úspěch → soubor smazán. Chyba → soubor zůstane, zkusí příště.
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

    // Sledujeme kolik záznamů z dnešního souboru bylo odesláno
    // Klíč: název souboru, hodnota: počet odeslaných záznamů
    private readonly Dictionary<string, int> _sentRecordCount = new();

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

        // Windows Authentication – agent jako HOSTNAME$ účet
        var handler = new HttpClientHandler
        {
            UseDefaultCredentials = true
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Statistika fronty při startu
        var (files, records) = _incidentLogger.GetQueueStats();
        _logger.LogInformation(
            "IncidentSync spuštěn – interval: {Min} min, ve frontě: {Files} souborů, {Records} záznamů",
            _syncIntervalMinutes, files, records);

        // Počkáme minutu po startu
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await TrySyncFiles();
            await Task.Delay(
                TimeSpan.FromMinutes(_syncIntervalMinutes),
                stoppingToken);
        }
    }

    // --------------------------------------------------------
    // Projde frontu a odešle uzavřené soubory
    // --------------------------------------------------------
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

            _logger.LogInformation(
                "IncidentSync: odesílám {Count} souborů na server", files.Count);

            foreach (var filePath in files)
            {
                await SendFile(filePath);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("IncidentSync: API nedostupné – soubory čekají v queue: {Msg}", ex.Message);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("IncidentSync: timeout při spojení s API – zkusím příště");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IncidentSync: neočekávaná chyba");
        }
    }

    // --------------------------------------------------------
    // Odeslání jednoho denního souboru
    // Pro dnešní soubor odesílá pouze nové záznamy od posledního sync
    // --------------------------------------------------------
    private async Task SendFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);

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

            // Delta sync – pro dnešní soubor odeslat jen nové záznamy
            var alreadySent = _sentRecordCount.GetValueOrDefault(fileName, 0);
            var newRecords  = daily.Records.Skip(alreadySent).ToList();

            if (newRecords.Count == 0)
            {
                _logger.LogInformation(
                    "IncidentSync: {File} – žádné nové záznamy ({Sent}/{Total} již odesláno)",
                    fileName, alreadySent, daily.Records.Count);
                return;
            }

            _logger.LogInformation(
                "IncidentSync: {File} – odesílám {New} nových záznamů (celkem {Total})",
                fileName, newRecords.Count, daily.Records.Count);

            var request = new
            {
                hostname     = Environment.MachineName,
                agentVersion = "1.0.0",
                sourceFile   = fileName,
                incidents    = newRecords.Select(r => new
                {
                    timestamp        = r.Timestamp,
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
                JsonSerializer.Serialize(request),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.PostAsync(
                $"{_syncUrl}/api/incidents", content);

            if (response.IsSuccessStatusCode)
            {
                // Zapamatujeme si kolik záznamů bylo odesláno
                _sentRecordCount[fileName] = daily.Records.Count;

                _logger.LogInformation(
                    "IncidentSync: {File} – {New} nových záznamů odesláno, celkem {Total}",
                    fileName, newRecords.Count, daily.Records.Count);

                // Uzavřený den → přesunout do sent\
                // Dnešní den → zůstane v queue, čítač se resetuje po půlnoci
                if (!_incidentLogger.IsTodaysFile(filePath))
                {
                    _sentRecordCount.Remove(fileName);
                    _incidentLogger.MoveTeSent(filePath);
                }
            }
            else
            {
                _logger.LogWarning(
                    "IncidentSync: odeslání {File} selhalo: {Status} – zkusím příště",
                    fileName, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "IncidentSync: chyba při odesílání {File} – zkusím příště", fileName);
        }
    }

    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }
}
