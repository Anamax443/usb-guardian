// ============================================================
// IncidentSync.cs
// Odesílá denní log soubory na REST API server.
// Delta sync – odesílá jen nové záznamy od posledního odeslání.
//
// v1.1 – offset persist na disk (.offset soubor)
//   Restart agenta nezpůsobí opakované odesílání celého souboru.
//
// v1.2 – DisconnectedAt v payloadu
//
// v1.3 – Jitter při startu (thundering herd ochrana)
//   Náhodné zpoždění 0–60s před prvním sync.
//   Při hromadném deployi nebo restartu 500 PC najednou
//   nedostanou API server spike, ale rozložené requesty.
//
// v1.4 – Retry při 503 (fronta serveru plná)
//   Pokud server vrátí 503 (fronta plná), agent počká
//   a zkusí znovu místo zahození dat.
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

    // Jitter – náhodné zpoždění při startu (0–60 sekund)
    private static readonly Random _rng = new();
    private const int JitterMaxSeconds = 60;

    // Retry při 503 – počet pokusů a prodleva
    private const int MaxRetries       = 3;
    private const int RetryDelaySeconds = 30;

    public IncidentSync(
        ILogger<IncidentSync> logger,
        string syncUrl,
        IncidentLogger incidentLogger,
        int syncIntervalMinutes = 1,
        bool validateServerCertificate = true,
        string pinnedThumbprint = "")
    {
        _logger              = logger;
        _syncUrl             = syncUrl;
        _incidentLogger      = incidentLogger;
        _syncIntervalMinutes = syncIntervalMinutes;

        // TLS: pinning (otisk API certu) / vývojové vypnutí / výchozí validace
        _httpClient = USBGuardian.Security.TlsClient.Create(validateServerCertificate, pinnedThumbprint, 60);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var (files, records) = _incidentLogger.GetQueueStats();
        _logger.LogInformation(
            "IncidentSync spuštěn – interval: {Min} min, ve frontě: {Files} souborů, {Records} záznamů",
            _syncIntervalMinutes, files, records);

        // Jitter – rozloží nápor při hromadném deployi/restartu
        var jitter = _rng.Next(0, JitterMaxSeconds);
        _logger.LogInformation(
            "IncidentSync: jitter {Sec}s před prvním sync (thundering herd ochrana)", jitter);
        await Task.Delay(TimeSpan.FromSeconds(jitter), stoppingToken);

        // Počkáme ještě minutu (standardní prodleva při startu)
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
                _logger.LogInformation("IncidentSync: fronta prázdná");
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
    // Odeslání jednoho souboru s retry při 503
    // --------------------------------------------------------
    private async Task SendFile(string filePath)
    {
        var fileName   = Path.GetFileName(filePath);
        var offsetPath = filePath + ".offset";

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

            var alreadySent = ReadOffset(offsetPath);
            var newRecords  = daily.Records.Skip(alreadySent).ToList();

            var updatedDisconnects = daily.Records
                .Take(alreadySent)
                .Where(r => r.DisconnectedAt.HasValue)
                .ToList();

            if (newRecords.Count == 0 && updatedDisconnects.Count == 0)
            {
                _logger.LogInformation(
                    "IncidentSync: {File} – žádné nové záznamy ({Sent}/{Total})",
                    fileName, alreadySent, daily.Records.Count);
                return;
            }

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

            var body = JsonSerializer.Serialize(request);

            // Retry smyčka pro případ 503 (fronta serveru plná)
            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                var content  = new StringContent(body, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync($"{_syncUrl}/api/incidents", content);

                if (response.IsSuccessStatusCode)
                {
                    var newOffset = alreadySent + newRecords.Count;
                    WriteOffset(offsetPath, newOffset);

                    _logger.LogInformation(
                        "IncidentSync: {File} – OK, offset: {Offset}/{Total}",
                        fileName, newOffset, daily.Records.Count);

                    if (!_incidentLogger.IsTodaysFile(filePath))
                        _incidentLogger.MoveTeSent(filePath);

                    return; // Úspěch – konec retry smyčky
                }

                if ((int)response.StatusCode == 503 && attempt < MaxRetries)
                {
                    // Fronta serveru plná – počkáme a zkusíme znovu
                    _logger.LogWarning(
                        "IncidentSync: server fronta plná (503), pokus {Attempt}/{Max}, čekám {Delay}s",
                        attempt, MaxRetries, RetryDelaySeconds);
                    await Task.Delay(TimeSpan.FromSeconds(RetryDelaySeconds));
                }
                else
                {
                    _logger.LogWarning(
                        "IncidentSync: {File} – selhalo: {Status} (pokus {Attempt}/{Max})",
                        fileName, response.StatusCode, attempt, MaxRetries);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IncidentSync: chyba při odesílání {File}", fileName);
        }
    }

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
        try { File.WriteAllText(offsetPath, offset.ToString()); }
        catch { }
    }

    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }
}
