// ============================================================
// ServiceRestartService.cs
// Plánovaný restart hlídaných Windows služeb (konzole, API, …).
//
// PROČ:
//   28.08.2026 stála API služba 6 týdnů zastavená (exit code 0 =
//   zůstala dole po deployi / po restartu serveru). Denní běh tohle
//   zvedne sám: služba, která NEBĚŽÍ, se prostě nastartuje.
//
// CO DĚLÁ:
//   - běží (RUNNING)  → Stop → počkat → Start   = restart
//   - stojí (STOPPED) → Start                   = záchrana výpadku
//   - jinak                                     = CHYBA s důvodem
//
// NASTAVENÍ (AppSettings, editovatelné v konzoli – nic natvrdo v kódu):
//   svc.restart.enabled  – "true"/"false" (default false)
//   svc.restart.at       – čas denního běhu "HH:mm" (default 03:30)
//   svc.restart.targets  – řádky "HOST|Název služby"; HOST prázdný = tento server
//   svc.restart.lastRun  – souhrn posledního běhu (píše se sem, čte UI i kontroly)
//   svc.restart.lastDate – "yyyy-MM-dd" posledního plánovaného běhu (ochrana proti opakování)
//
// PRÁVA:
//   Vzdálený stop/start dělá účet služby konzole. Na cizím serveru
//   na to musí mít právo – jinak běh skončí hláškou "přístup odepřen"
//   a je to vidět v Kontrolách stavu. Nic se nemaskuje.
// ============================================================

using System.ServiceProcess;
using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;
using USBGuardian.Api.Models;

namespace USBGuardian.Admin.Maintenance;

/// <summary>Jeden cíl restartu: služba na konkrétním stroji.</summary>
public sealed record RestartTarget(string Host, string ServiceName)
{
    public bool IsLocal => string.IsNullOrWhiteSpace(Host)
                        || Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                        || Host.Equals(".", StringComparison.Ordinal)
                        || Host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => IsLocal ? ServiceName : $"{Host}|{ServiceName}";
}

public sealed class ServiceRestartConfig
{
    public bool Enabled;
    public TimeSpan At = new(3, 30, 0);
    public List<RestartTarget> Targets = new();
    public string LastRun = "";
    public string LastDate = "";

    public static async Task<ServiceRestartConfig> LoadAsync(AppDbContext db, CancellationToken ct = default)
    {
        async Task<string> Get(string k) => (await db.AppSettings.FirstOrDefaultAsync(s => s.Key == k, ct))?.Value ?? "";

        var c = new ServiceRestartConfig
        {
            Enabled = string.Equals(await Get("svc.restart.enabled"), "true", StringComparison.OrdinalIgnoreCase),
            LastRun = await Get("svc.restart.lastRun"),
            LastDate = await Get("svc.restart.lastDate"),
        };

        if (TryParseTime(await Get("svc.restart.at"), out var at)) c.At = at;
        c.Targets = ParseTargets(await Get("svc.restart.targets"));
        return c;
    }

    /// <summary>"HH:mm" → TimeSpan. Prázdné nebo nesmysl = false (drží se default).</summary>
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

    /// <summary>Řádky "HOST|Služba" (host volitelný). Prázdné řádky a komentáře (#) se ignorují.</summary>
    public static List<RestartTarget> ParseTargets(string? raw)
    {
        var list = new List<RestartTarget>();
        if (string.IsNullOrWhiteSpace(raw)) return list;

        foreach (var line in raw.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var l = line.Trim();
            if (l.Length == 0 || l.StartsWith('#')) continue;

            var idx = l.IndexOf('|');
            var host = idx >= 0 ? l[..idx].Trim() : "";
            var svc = idx >= 0 ? l[(idx + 1)..].Trim() : l;
            if (svc.Length == 0) continue;

            list.Add(new RestartTarget(host, svc));
        }
        return list;
    }
}

/// <summary>
/// Vlastní provedení restartu. Singleton, ať jde spustit i ručně z UI
/// (tlačítko "Restartovat teď") stejným kódem jako plánovaný běh.
/// </summary>
public sealed class ServiceRestartRunner
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<ServiceRestartRunner> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Kolik čekat na dokončení stop/start jedné služby.</summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(60);

    public ServiceRestartRunner(IDbContextFactory<AppDbContext> dbFactory, ILogger<ServiceRestartRunner> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>Restartuje všechny nakonfigurované cíle a uloží souhrn. Vrací text souhrnu.</summary>
    public async Task<string> RunAsync(string triggeredBy, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(1), ct))
            return "Restart už právě běží, tenhle pokus se přeskočil.";

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var cfg = await ServiceRestartConfig.LoadAsync(db, ct);

            if (cfg.Targets.Count == 0)
            {
                const string none = "Není nastavená žádná služba k restartu.";
                await SaveAsync(db, $"{Stamp()} ({triggeredBy}) {none}", markDate: false, ct);
                return none;
            }

            var results = new List<string>();
            foreach (var t in cfg.Targets)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(RestartOne(t));
            }

            var summary = $"{Stamp()} ({triggeredBy}) " + string.Join(" · ", results);
            _logger.LogInformation("Plánovaný restart služeb: {Summary}", summary);
            await SaveAsync(db, summary, markDate: true, ct);
            return summary;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Jedna služba. Nikdy nevyhazuje – chyba je součást výsledku, ať je vidět v UI.</summary>
    private string RestartOne(RestartTarget t)
    {
        var label = t.IsLocal ? t.ServiceName : $"{t.Host}/{t.ServiceName}";
        try
        {
            using var sc = t.IsLocal
                ? new ServiceController(t.ServiceName)
                : new ServiceController(t.ServiceName, t.Host);

            var status = sc.Status; // první dotaz zároveň ověří dostupnost a práva

            if (status == ServiceControllerStatus.Running)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, WaitTimeout);
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, WaitTimeout);
                return $"{label}: restartována";
            }

            if (status == ServiceControllerStatus.Stopped)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, WaitTimeout);
                return $"{label}: NEBĚŽELA → nastartována";
            }

            return $"{label}: CHYBA – nečekaný stav {status}, nechávám být";
        }
        catch (Exception ex) when (IsAccessDenied(ex))
        {
            // ERROR_ACCESS_DENIED (5). Nejčastější reálný důvod: účet služby konzole
            // nemá na cílovém serveru právo službu ovládat. ServiceController ho balí
            // do InvalidOperationException, proto se hledá i ve vnitřní výjimce.
            var where = t.IsLocal ? Environment.MachineName : t.Host;
            return $"{label}: CHYBA – přístup odepřen (účet {WhoAmI()} nemá právo ovládat službu na {where})";
        }
        catch (InvalidOperationException ex)
        {
            return $"{label}: CHYBA – {Short(ex.InnerException?.Message ?? ex.Message)}";
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            return $"{label}: CHYBA – služba nedoběhla do {WaitTimeout.TotalSeconds:F0} s";
        }
        catch (Exception ex)
        {
            return $"{label}: CHYBA – {Short(ex.Message)}";
        }
    }

    private static async Task SaveAsync(AppDbContext db, string summary, bool markDate, CancellationToken ct)
    {
        await SetAsync(db, "svc.restart.lastRun", summary, ct);
        if (markDate) await SetAsync(db, "svc.restart.lastDate", DateTime.Now.ToString("yyyy-MM-dd"), ct);
        await db.SaveChangesAsync(ct);
    }

    private static async Task SetAsync(AppDbContext db, string key, string value, CancellationToken ct)
    {
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else row.Value = value;
    }

    /// <summary>Hledá ERROR_ACCESS_DENIED i ve zabalené výjimce (ServiceController balí Win32 chyby).</summary>
    private static bool IsAccessDenied(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is System.ComponentModel.Win32Exception w && w.NativeErrorCode == 5)
                return true;
        return false;
    }

    /// <summary>Účet, pod kterým konzole běží – u LocalSystem vyjde strojový účet DOMENA\HOST$.</summary>
    private static string WhoAmI()
    {
        try { return System.Security.Principal.WindowsIdentity.GetCurrent().Name; }
        catch { return Environment.UserName; }
    }

    private static string Stamp() => DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss");

    private static string Short(string s, int max = 160)
    {
        s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}

/// <summary>Časovač: jednou denně v nastavenou hodinu spustí runner.</summary>
public sealed class ServiceRestartService : BackgroundService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ServiceRestartRunner _runner;
    private readonly ILogger<ServiceRestartService> _logger;

    public ServiceRestartService(IDbContextFactory<AppDbContext> dbFactory, ServiceRestartRunner runner,
                                 ILogger<ServiceRestartService> logger)
    {
        _dbFactory = dbFactory;
        _runner = runner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Nechat konzoli naběhnout (DB, config) – stejný vzor jako auto-deploy.
        try { await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "Plánovaný restart služeb: běh selhal"); }

            try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        ServiceRestartConfig cfg;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            cfg = await ServiceRestartConfig.LoadAsync(db, ct);

        if (!cfg.Enabled || cfg.Targets.Count == 0) return;

        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (cfg.LastDate == today) return;                       // dnes už proběhlo
        if (DateTime.Now.TimeOfDay < cfg.At) return;             // ještě není čas

        // Ochrana proti "dohánění": když konzole naběhne až večer, nepouštět
        // restart z rána zpětně – počká se na zítřek.
        if (DateTime.Now.TimeOfDay - cfg.At > TimeSpan.FromHours(2))
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await MarkSkippedAsync(db, cfg, ct);
            return;
        }

        await _runner.RunAsync("plán", ct);
    }

    private static async Task MarkSkippedAsync(AppDbContext db, ServiceRestartConfig cfg, CancellationToken ct)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var msg = $"{DateTime.Now:dd.MM.yyyy HH:mm:ss} (plán) přeskočeno – okno {cfg.At:hh\\:mm} už dávno uplynulo, "
                + "restart proběhne až v dalším termínu.";

        var run = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "svc.restart.lastRun", ct);
        if (run is null) db.AppSettings.Add(new AppSetting { Key = "svc.restart.lastRun", Value = msg });
        else run.Value = msg;

        var date = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == "svc.restart.lastDate", ct);
        if (date is null) db.AppSettings.Add(new AppSetting { Key = "svc.restart.lastDate", Value = today });
        else date.Value = today;

        await db.SaveChangesAsync(ct);
    }
}
