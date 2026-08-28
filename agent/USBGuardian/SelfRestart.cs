// ============================================================
// SelfRestart.cs  –  plánovaný restart klientské služby (agent)
//
// PROČ:
//   Stejná pojistka, jakou má serverová konzole pro hlídané služby,
//   jen na straně stanice: jednou denně v nastavenou hodinu agent
//   restartuje sám sebe. Řeší dlouhoběžící potíže, které se navenek
//   projeví jako "agent běží, ale nic nedělá" (zaseknutý WMI watcher,
//   ukousnutý handle, zatuhlé spojení).
//
// JAK SE RESTARTUJE SLUŽBA SAMA:
//   Nejde zavolat stop/start uvnitř procesu, který se zastavuje.
//   Spustí se proto ODDĚLENÝ cmd.exe: sc stop → krátká pauza → sc start.
//   Ten přežije zastavení služby a nastartuje ji zpět (stejný postup
//   jako tlačítko "Restart služby" v lokální konzoli).
//
// STAV JE PERZISTENTNÍ:
//   C:\ProgramData\USBGuardian\selfrestart.json – přežije restart i update.
//   Výchozí hodnoty přicházejí z agent.config.json (selfRestart.*),
//   lokální admin je může přepnout v lokální konzoli.
// ============================================================

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace USBGuardian;

/// <summary>Perzistovaný stav plánovaného self-restartu (soubor selfrestart.json).</summary>
public sealed class SelfRestartState
{
    public bool Enabled { get; set; }
    /// <summary>Čas denního běhu ve tvaru "HH:mm".</summary>
    public string At { get; set; } = "03:30";
    /// <summary>Datum posledního plánovaného běhu ("yyyy-MM-dd") – ochrana proti opakování v týž den.</summary>
    public string LastDate { get; set; } = "";
    /// <summary>Popis posledního běhu pro konzoli (kdo/kdy/jak dopadl).</summary>
    public string LastResult { get; set; } = "";
    public string ChangedBy { get; set; } = "";
    public DateTime? ChangedAt { get; set; }
}

/// <summary>
/// Drží stav plánovaného restartu a umí restart provést.
/// Singleton – sdílí ho časovač i lokální konzole (ruční tlačítko).
/// </summary>
public sealed class SelfRestartManager
{
    private readonly ILogger<SelfRestartManager> _logger;
    private readonly string _statePath;
    private readonly string _serviceName;
    private readonly object _lock = new();
    private SelfRestartState _state = new();

    public SelfRestartManager(ILogger<SelfRestartManager> logger, string statePath, string serviceName,
                              bool defaultEnabled, string defaultAt)
    {
        _logger = logger;
        _statePath = statePath;
        _serviceName = serviceName;

        Load(defaultEnabled, defaultAt);
    }

    public bool Enabled { get { lock (_lock) return _state.Enabled; } }
    public string At { get { lock (_lock) return _state.At; } }
    public string LastResult { get { lock (_lock) return _state.LastResult; } }
    public string ChangedBy { get { lock (_lock) return _state.ChangedBy; } }
    public DateTime? ChangedAt { get { lock (_lock) return _state.ChangedAt; } }
    public string ServiceName => _serviceName;

    /// <summary>Dnešní termín už byl vyřízen (proběhl nebo se odepsal) – nedělat to znovu.</summary>
    public bool HandledToday
    {
        get { lock (_lock) return _state.LastDate == DateTime.Now.ToString("yyyy-MM-dd"); }
    }

    /// <summary>"HH:mm" → TimeSpan; nesmysl vrací false (volající si nechá původní hodnotu).</summary>
    public static bool TryParseTime(string? value, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var parts = value.Trim().Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out var h) || h is < 0 or > 23) return false;
        if (!int.TryParse(parts[1], out var m) || m is < 0 or > 59) return false;

        time = new TimeSpan(h, m, 0);
        return true;
    }

    /// <summary>Nastavení z lokální konzole (admin-only). Vrací platný uložený stav.</summary>
    public void Configure(bool enabled, string at, string by)
    {
        lock (_lock)
        {
            _state.Enabled = enabled;
            if (TryParseTime(at, out _)) _state.At = at.Trim();
            _state.ChangedBy = by;
            _state.ChangedAt = DateTime.UtcNow;
            Save();
        }

        _logger.LogWarning("Plánovaný restart služby: {State} v {At} (nastavil {By})",
            enabled ? "ZAPNUT" : "vypnut", At, by);
    }

    /// <summary>Je teď ten správný okamžik pro plánovaný běh?</summary>
    public bool ShouldRunNow(DateTime nowLocal)
    {
        lock (_lock)
        {
            if (!_state.Enabled) return false;
            if (!TryParseTime(_state.At, out var at)) return false;
            if (_state.LastDate == nowLocal.ToString("yyyy-MM-dd")) return false;
            if (nowLocal.TimeOfDay < at) return false;

            // Když stanice byla v naplánovaný čas vypnutá, restart se NEDOhání
            // celý den zpětně – jen do dvou hodin po termínu.
            return nowLocal.TimeOfDay - at <= TimeSpan.FromHours(2);
        }
    }

    /// <summary>Označí dnešek za vyřízený, aniž by se restartovalo (okno uplynulo).</summary>
    public void SkipToday(string reason)
    {
        lock (_lock)
        {
            _state.LastDate = DateTime.Now.ToString("yyyy-MM-dd");
            _state.LastResult = $"{DateTime.Now:dd.MM.yyyy HH:mm:ss} přeskočeno – {reason}";
            Save();
        }
    }

    /// <summary>
    /// Restartuje službu agenta. Vrací true, když se podařilo spustit restartovací proces
    /// (samotný restart pak proběhne mimo tenhle proces – ten se za chvíli zastaví).
    /// </summary>
    public bool Restart(string by, bool scheduled)
    {
        var stamp = DateTime.Now;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c sc stop \"{_serviceName}\" & ping -n 4 127.0.0.1 >nul & sc start \"{_serviceName}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            lock (_lock)
            {
                if (scheduled) _state.LastDate = stamp.ToString("yyyy-MM-dd");
                _state.LastResult = $"{stamp:dd.MM.yyyy HH:mm:ss} restart spuštěn ({by})";
                Save();
            }

            _logger.LogWarning("Restart služby {Service} spuštěn ({By}).", _serviceName, by);
            return true;
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                if (scheduled) _state.LastDate = stamp.ToString("yyyy-MM-dd");
                _state.LastResult = $"{stamp:dd.MM.yyyy HH:mm:ss} CHYBA – restart se nepodařilo spustit: {ex.Message}";
                Save();
            }

            _logger.LogError(ex, "Restart služby {Service} selhal ({By})", _serviceName, by);
            return false;
        }
    }

    // ── perzistence ──────────────────────────────────────────

    private void Load(bool defaultEnabled, string defaultAt)
    {
        try
        {
            if (File.Exists(_statePath))
            {
                var loaded = JsonSerializer.Deserialize<SelfRestartState>(File.ReadAllText(_statePath));
                if (loaded is not null)
                {
                    if (!TryParseTime(loaded.At, out _)) loaded.At = defaultAt;
                    _state = loaded;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nelze načíst {Path}, beru výchozí nastavení z configu", _statePath);
        }

        // První spuštění (nebo poškozený soubor) – výchozí hodnoty z agent.config.
        _state = new SelfRestartState
        {
            Enabled = defaultEnabled,
            At = TryParseTime(defaultAt, out _) ? defaultAt : "03:30",
        };
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_statePath,
                JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nelze uložit stav plánovaného restartu do {Path}", _statePath);
        }
    }
}

/// <summary>Časovač: každou minutu se ptá, jestli už nemá být plánovaný restart.</summary>
public sealed class SelfRestartService : BackgroundService
{
    private readonly SelfRestartManager _manager;
    private readonly ILogger<SelfRestartService> _logger;

    public SelfRestartService(SelfRestartManager manager, ILogger<SelfRestartService> logger)
    {
        _manager = manager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Chvíli po startu neřešit nic – ať se služba po restartu nezacyklí.
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                if (_manager.ShouldRunNow(now))
                    _manager.Restart("plán", scheduled: true);
                else if (_manager.Enabled && !_manager.HandledToday && MissedWindow(now))
                    _manager.SkipToday("stanice byla mimo naplánované okno");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plánovaný restart: kontrola selhala");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>Okno už uplynulo o víc než dvě hodiny – dnešek se odepíše, ať se nerestartuje v poledne.</summary>
    private bool MissedWindow(DateTime nowLocal)
    {
        if (!SelfRestartManager.TryParseTime(_manager.At, out var at)) return false;
        return nowLocal.TimeOfDay - at > TimeSpan.FromHours(2);
    }
}
