// ============================================================
// WhitelistSync.cs
// Background service – synchronizace whitelistu ze serveru.
//
// v1.1 – RSA podpis sync:
//   Stahuje whitelist.json i whitelist.json.sig atomicky.
//   Pokud stažení .sig selže → stažený .json se neuloží
//   (agent pokračuje se starou verzí, která je platná).
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
    private readonly SyncSignals? _signals;
    private readonly PolicyState? _policyState;
    private readonly WhitelistChecker? _whitelistChecker;
    private readonly DeviceBlocker? _blocker;
    private readonly DeviceMonitor? _monitor;

    // Cesta k .sig souboru (vedle whitelistu)
    private string SignaturePath => _localWhitelistPath + ".sig";

    public WhitelistSync(
        ILogger<WhitelistSync> logger,
        string syncUrl,
        string localWhitelistPath,
        int syncIntervalMinutes,
        bool validateServerCertificate = true,
        string pinnedThumbprint = "",
        SyncSignals? signals = null,
        PolicyState? policyState = null,
        WhitelistChecker? whitelistChecker = null,
        DeviceBlocker? blocker = null,
        DeviceMonitor? monitor = null)
    {
        _logger              = logger;
        _syncUrl             = syncUrl;
        _localWhitelistPath  = localWhitelistPath;
        _syncIntervalMinutes = syncIntervalMinutes;
        _signals             = signals;
        _policyState         = policyState;
        _whitelistChecker    = whitelistChecker;
        _blocker             = blocker;
        _monitor             = monitor;

        // TLS: pinning (otisk API certu) / vývojové vypnutí / výchozí validace
        _httpClient = USBGuardian.Security.TlsClient.Create(validateServerCertificate, pinnedThumbprint, 30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "WhitelistSync spuštěn – interval: {Min} min, URL: {Url}",
            _syncIntervalMinutes, _syncUrl);

        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await TrySyncWhitelist();
            ReconcileBlocked();   // vrátit média schválená mezitím / když je blokování vypnuté
            await Task.Delay(TimeSpan.FromMinutes(_syncIntervalMinutes), stoppingToken);
        }
    }

    // --------------------------------------------------------
    // Reconciliace stavu vynucování po každém cyklu:
    //   1) blokování ZAPNUTÉ → znovu zablokovat připojená neschválená média, která ještě nejsou
    //      blokovaná (díra: po break-glassu zůstane médium připojené a samo se nezablokuje).
    //   2) blokování VYPNUTÉ (break-glass / enforce=false) → vrátit VŠE, co agent zakázal,
    //      blokování zapnuté → vrátit ta, která jsou MEZITÍM na whitelistu (schválená); jinak nechat.
    // (Pamatuje si jen to, co zakázal agent sám – disky zakázané jinde nevrací.)
    // --------------------------------------------------------
    private void ReconcileBlocked()
    {
        try
        {
            if (_blocker == null || _whitelistChecker == null || _policyState == null) return;

            var blocking = _policyState.EffectiveMode("warn") == "block";

            // 1) Re-blokace připojených neschválených médií (idempotentní – schválená i už-blokovaná přeskočí).
            if (blocking) _monitor?.ReEnforceConnectedDevices();

            // 2) Vracení dříve blokovaných.
            var blocked = _blocker.GetBlocked();
            if (blocked.Count == 0) return;
            foreach (var kv in blocked)
            {
                if (!blocking || _whitelistChecker.IsAllowedKey(kv.Value))
                    _blocker.UnblockDevice(kv.Key);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Reconciliace zablokovaných médií selhala"); }
    }

    private async Task TrySyncWhitelist()
    {
        try
        {
            var localVersion = GetLocalVersion();

            var heartbeatUrl =
                $"{_syncUrl}/api/heartbeat" +
                $"?hostname={Uri.EscapeDataString(Environment.MachineName)}" +
                $"&whitelistVersion={Uri.EscapeDataString(localVersion)}" +
                $"&agentVersion={Uri.EscapeDataString(AppInfo.Commit)}";

            var heartbeatResp = await _httpClient.GetAsync(heartbeatUrl);

            if (!heartbeatResp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Heartbeat selhal: {Status}", heartbeatResp.StatusCode);
                return;
            }

            var heartbeatJson = await heartbeatResp.Content.ReadAsStringAsync();
            var heartbeat     = JsonSerializer.Deserialize<HeartbeatDto>(heartbeatJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (heartbeat == null) return;

            // Fáze 2+3: spojení se serverem (APP_SERVER = zdroj pravdy) → aplikovat enforce A zrušit lokální
            // break-glass override (ten platí jen offline). Vrátí true, když override právě zrušil.
            var overrideCleared = _policyState?.OnServerHeartbeat(heartbeat.Enforce) ?? false;
            if (overrideCleared)
                _logger.LogWarning("Break-glass override ZRUŠEN spojením se serverem – platí opět serverová politika (enforce={Enforce}).", heartbeat.Enforce);

            _logger.LogInformation(
                "WhitelistSync: heartbeat OK – server: {Server}, lokální: {Local}, enforce: {Enforce}",
                heartbeat.CurrentWhitelistVersion, localVersion, heartbeat.Enforce);

            // Konzole vyžádala okamžité odevzdání dat → probudíme IncidentSync.
            // Heartbeat sám už potvrdil online + verzi (server posunul LastSeen).
            if (heartbeat.ReportNow)
            {
                _logger.LogInformation("WhitelistSync: konzole vyžádala data (ReportNow) → spouštím flush incidentů");
                _signals?.RequestFlush();
            }

            if (!heartbeat.WhitelistUpdateAvailable)
            {
                _logger.LogInformation(
                    "WhitelistSync: whitelist je aktuální ({Ver})", localVersion);
                return;
            }

            _logger.LogInformation(
                "Nová verze whitelistu: {New} (máme: {Old})",
                heartbeat.CurrentWhitelistVersion, localVersion);

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
    // Stáhne whitelist.json a whitelist.json.sig atomicky.
    // Pokud .sig endpoint selže → nový whitelist se NEULOŽÍ.
    // Agent pokračuje se starou (platnou) verzí.
    // --------------------------------------------------------
    private async Task DownloadAndSaveWhitelist()
    {
        // Krok 1: Stáhni whitelist JSON
        var jsonResponse = await _httpClient.GetAsync($"{_syncUrl}/api/whitelist");
        if (!jsonResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("Stažení whitelistu selhalo: {Status}", jsonResponse.StatusCode);
            return;
        }
        var json = await jsonResponse.Content.ReadAsStringAsync();

        // Validace JSON formátu
        JsonSerializer.Deserialize<object>(json);

        // Krok 2: Stáhni podpis (.sig)
        var sigResponse = await _httpClient.GetAsync($"{_syncUrl}/api/whitelist/signature");
        if (!sigResponse.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Stažení podpisu whitelistu selhalo: {Status} – " +
                "whitelist NEULOŽEN, pokračuji se starou verzí",
                sigResponse.StatusCode);
            return;
        }
        var signature = await sigResponse.Content.ReadAsStringAsync();

        // Krok 3: Atomický zápis OBOU souborů (JSON + SIG)
        // Nejdříve temp soubory, pak přejmenování → nelze přerušit
        var dir      = Path.GetDirectoryName(_localWhitelistPath)!;
        Directory.CreateDirectory(dir);

        var tempJson = _localWhitelistPath + ".tmp";
        var tempSig  = SignaturePath + ".tmp";

        await File.WriteAllTextAsync(tempJson, json);
        await File.WriteAllTextAsync(tempSig, signature.Trim());

        // Přejmenovat oba najednou (nejdříve .sig, pak .json)
        // Pořadí záměrné: pokud selže přejmenování .json, .sig je aktuální
        // → WhitelistChecker odmítne neplatnou kombinaci
        File.Move(tempSig,  SignaturePath,          overwrite: true);
        File.Move(tempJson, _localWhitelistPath,    overwrite: true);

        // Zahodit 5min cache WhitelistCheckeru → nová verze platí OKAMŽITĚ (důležité pro reconcile
        // hned po stažení: dříve blokované, mezitím schválené médium se vrátí v témž cyklu, ne až za ~5 min).
        _whitelistChecker?.Reload();

        _logger.LogInformation(
            "Whitelist + podpis synchronizovány: {Path}", _localWhitelistPath);
    }

    private string GetLocalVersion()
    {
        try
        {
            if (!File.Exists(_localWhitelistPath)) return string.Empty;
            var json = File.ReadAllText(_localWhitelistPath);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("version").GetString() ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }
}

internal class HeartbeatDto
{
    public string   CurrentWhitelistVersion  { get; set; } = string.Empty;
    public bool     WhitelistUpdateAvailable { get; set; }
    public bool     ReportNow                { get; set; }
    public bool     Enforce                  { get; set; }
    public DateTime ServerTime               { get; set; }
}
