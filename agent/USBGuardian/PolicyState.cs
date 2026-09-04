// ============================================================
// PolicyState.cs
// Sdílený běhový stav vynucování (singleton):
//   - SERVER enforce: doručeno přes heartbeat (APP_SERVER = zdroj pravdy). Agent dle něj blokuje/varuje.
//   - LOKÁLNÍ break-glass override: admin stanice dočasně vypne blokování (práce mimo síť),
//     vyprší po timeoutu; perzistované do souboru (přežije restart služby).
//
// Efektivní režim = override aktivní ? warn : (server přijat ? (enforce ? block : warn) : lokální default).
// ============================================================

using System.Text.Json;

namespace USBGuardian;

public class PolicyState
{
    private readonly string _overridePath;
    private readonly object _lock = new();

    private volatile bool _serverEnforce;
    private volatile bool _serverReceived;
    private DateTime? _overrideUntil;
    private string _overrideBy = string.Empty;

    public PolicyState(string overridePath)
    {
        _overridePath = overridePath;
        Load();
    }

    // ── Server (heartbeat = spojení se serverem) ─────────────
    // Server je ZDROJ PRAVDY: úspěšný heartbeat aplikuje serverové enforce A ZRUŠÍ lokální break-glass
    // override (ten platí jen offline). Vrací true, pokud byl aktivní override tímto zrušen (pro audit).
    public bool OnServerHeartbeat(bool enforce)
    {
        _serverEnforce = enforce;
        _serverReceived = true;
        lock (_lock)
        {
            if (_overrideUntil == null) return false;
            var wasActive = _overrideUntil > DateTime.UtcNow;
            _overrideUntil = null;
            _overrideBy = string.Empty;
            Save();
            return wasActive;
        }
    }

    public bool ServerEnforce  => _serverEnforce;
    public bool ServerReceived => _serverReceived;

    // ── Break-glass override ─────────────────────────────────
    public bool OverrideActive { get { lock (_lock) return _overrideUntil is { } u && u > DateTime.UtcNow; } }
    public DateTime? OverrideUntil { get { lock (_lock) return OverrideActive ? _overrideUntil : null; } }
    public string OverrideBy { get { lock (_lock) return OverrideActive ? _overrideBy : string.Empty; } }

    public void SetOverride(DateTime untilUtc, string by)
    {
        lock (_lock) { _overrideUntil = untilUtc; _overrideBy = by; Save(); }
    }

    public void ClearOverride()
    {
        lock (_lock) { _overrideUntil = null; _overrideBy = string.Empty; Save(); }
    }

    // ── Efektivní režim, který má PolicyEnforcer použít ──────
    public string EffectiveMode(string localMode)
    {
        if (OverrideActive) return "warn";                 // break-glass: nikdy neblokovat
        if (_serverReceived) return _serverEnforce ? "block" : "warn";
        return localMode;                                   // před prvním heartbeatem: lokální config
    }

    // ── Perzistence override (offline odolnost) ──────────────
    private sealed class Persisted { public DateTime? Until { get; set; } public string By { get; set; } = string.Empty; }

    private void Load()
    {
        try
        {
            if (!File.Exists(_overridePath)) return;
            var p = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_overridePath));
            if (p != null) { _overrideUntil = p.Until; _overrideBy = p.By; }
        }
        catch { /* poškozený soubor – ignorovat, override neaktivní */ }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_overridePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_overridePath,
                JsonSerializer.Serialize(new Persisted { Until = _overrideUntil, By = _overrideBy }));
        }
        catch { /* zápis nesmí shodit agenta */ }
    }
}
