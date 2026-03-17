// ============================================================
// WhitelistSync.cs
// Background service – synchronizace whitelistu ze serveru.
// Běží každých 15 minut (konfigurovatelné).
// Online: stáhne nový whitelist → přepíše lokální soubor.
// Offline: nic, agent pokračuje s cached verzí.
// ============================================================

using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace USBGuardian;

public class WhitelistSync : BackgroundService
{
    private readonly ILogger<WhitelistSync> _logger;
    private readonly string _syncUrl;
    private readonly string _localWhitelistPath;
    private readonly int _syncIntervalMinutes;
    private readonly HttpClient _httpClient;

    public WhitelistSync(
        ILogger<WhitelistSync> logger,
        string syncUrl,
        string localWhitelistPath,
        int syncIntervalMinutes)
    {
        _logger              = logger;
        _syncUrl             = syncUrl;
        _localWhitelistPath  = localWhitelistPath;
        _syncIntervalMinutes = syncIntervalMinutes;

        // Windows Authentication – agent jako HOSTNAME$ účet
        var handler = new HttpClientHandler
        {
            UseDefaultCredentials = true
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "WhitelistSync spuštěn – interval: {Min} min, URL: {Url}",
            _syncIntervalMinutes, _syncUrl);

        // 30 sekund po startu – dáme čas ostatním službám
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await TrySyncWhitelist();
            await Task.Delay(
                TimeSpan.FromMinutes(_syncIntervalMinutes),
                stoppingToken);
        }
    }

    // --------------------------------------------------------
    // Hlavní sync logika
    // --------------------------------------------------------
    private async Task TrySyncWhitelist()
    {
        try
        {
            // Krok 1: Zjistíme lokální verzi
            var localVersion = GetLocalVersion();

            // Krok 2: Heartbeat – je nová verze?
            var heartbeatUrl =
                $"{_syncUrl}/api/heartbeat" +
                $"?hostname={Uri.EscapeDataString(Environment.MachineName)}" +
                $"&whitelistVersion={Uri.EscapeDataString(localVersion)}" +
                $"&agentVersion=1.0.0";

            var heartbeatResp = await _httpClient.GetAsync(heartbeatUrl);

            if (!heartbeatResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Heartbeat selhal: {Status}", heartbeatResp.StatusCode);
                return;
            }

            var heartbeatJson = await heartbeatResp.Content.ReadAsStringAsync();
            var heartbeat = JsonSerializer.Deserialize<HeartbeatDto>(heartbeatJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (heartbeat == null) return;

            _logger.LogInformation(
                "WhitelistSync: heartbeat OK – server: {Server}, lokální: {Local}",
                heartbeat.CurrentWhitelistVersion, localVersion);

            // Krok 3: Pokud není nová verze, nic neděláme
            if (!heartbeat.WhitelistUpdateAvailable)
            {
                _logger.LogInformation(
                    "WhitelistSync: whitelist je aktuální ({Ver})", localVersion);
                return;
            }

            _logger.LogInformation(
                "Nová verze whitelistu: {New} (máme: {Old})",
                heartbeat.CurrentWhitelistVersion, localVersion);

            // Krok 4: Stáhneme a uložíme nový whitelist
            await DownloadAndSaveWhitelist();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("WhitelistSync: API nedostupné (offline provoz): {Msg}", ex.Message);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("WhitelistSync: heartbeat timeout – API nedostupné");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Neočekávaná chyba při sync whitelistu");
        }
    }

    // --------------------------------------------------------
    // Stažení a atomický zápis whitelistu
    // --------------------------------------------------------
    private async Task DownloadAndSaveWhitelist()
    {
        var response = await _httpClient.GetAsync($"{_syncUrl}/api/whitelist");

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Stažení whitelistu selhalo: {Status}",
                response.StatusCode);
            return;
        }

        var json = await response.Content.ReadAsStringAsync();

        // Validace – je to platný JSON?
        JsonSerializer.Deserialize<object>(json);

        // Atomický zápis přes temp soubor
        // Zabraňuje poškození při výpadku uprostřed zápisu
        var tempPath = _localWhitelistPath + ".tmp";
        var dir      = Path.GetDirectoryName(_localWhitelistPath);

        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(tempPath, json);
        File.Move(tempPath, _localWhitelistPath, overwrite: true);

        _logger.LogInformation(
            "Whitelist synchronizován a uložen: {Path}", _localWhitelistPath);
    }

    // --------------------------------------------------------
    // Přečtení verze z lokálního whitelist.json
    // --------------------------------------------------------
    private string GetLocalVersion()
    {
        try
        {
            if (!File.Exists(_localWhitelistPath))
                return string.Empty;

            var json = File.ReadAllText(_localWhitelistPath);
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement
                .GetProperty("version")
                .GetString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }
}

// DTO pro heartbeat odpověď
internal class HeartbeatDto
{
    public string   CurrentWhitelistVersion  { get; set; } = string.Empty;
    public bool     WhitelistUpdateAvailable { get; set; }
    public DateTime ServerTime               { get; set; }
}
