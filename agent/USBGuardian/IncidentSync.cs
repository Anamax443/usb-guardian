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

    private const int SyncIntervalMinutes = 15;

    public IncidentSync(
        ILogger<IncidentSync> logger,
        string syncUrl,
        IncidentLogger incidentLogger)
    {
        _logger         = logger;
        _syncUrl        = syncUrl;
        _incidentLogger = incidentLogger;

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
            "IncidentSync spuštěn – ve frontě: {Files} souborů, {Records} záznamů",
            files, records);

        // Počkáme minutu po startu
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await TrySyncFiles();
            await Task.Delay(
                TimeSpan.FromMinutes(SyncIntervalMinutes),
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
                _logger.LogDebug("Žádné soubory k odeslání");
                return;
            }

            _logger.LogInformation(
                "Odesílám {Count} souborů na server", files.Count);

            foreach (var filePath in files)
            {
                await SendFile(filePath);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug("API nedostupné – soubory čekají: {Msg}", ex.Message);
        }
        catch (TaskCanceledException)
        {
            _logger.LogDebug("IncidentSync timeout – zkusím příště");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chyba při sync souborů");
        }
    }

    // --------------------------------------------------------
    // Odeslání jednoho denního souboru
    // --------------------------------------------------------
    private async Task SendFile(string filePath)
    {
        var fileName = Path.GetFileName(filePath);

        try
        {
            var json    = await File.ReadAllTextAsync(filePath);
            var daily   = JsonSerializer.Deserialize<DailyLog>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (daily == null || daily.Records.Count == 0)
            {
                // Prázdný soubor – přesuneme do sent
                _incidentLogger.MoveTeSent(filePath);
                return;
            }

            // Sestavíme batch request – včetně názvu zdrojového souboru
            var request = new
            {
                hostname     = Environment.MachineName,
                agentVersion = "1.0.0",
                sourceFile   = fileName,   // ← pro audit trail v SQL
                incidents    = daily.Records.Select(r => new
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
                    sourceFile       = fileName  // ← na úrovni každého záznamu
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
                _logger.LogInformation(
                    "Soubor {File} odeslán ({Count} záznamů) – přesouvám do sent",
                    fileName, daily.Records.Count);

                // Úspěch → přesuneme do sent\ (ne smažeme)
                _incidentLogger.MoveTeSent(filePath);
            }
            else
            {
                _logger.LogWarning(
                    "Odeslání {File} selhalo: {Status} – zkusím příště",
                    fileName, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Chyba při odesílání {File} – zkusím příště", fileName);
        }
    }

    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }
}
