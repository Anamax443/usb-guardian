// ============================================================
// LocalConsoleService.cs
// Lokální admin konzole agenta – okno do živé funkčnosti.
//
// Účel:
//   - Verifikace běhu agenta přímo na stanici (vývoj + diagnostika).
//   - Offline-diagnostický pohled, když stanice nedosáhne na server.
//
// Bezpečnost (NIS2 – minimální attack surface, řízení přístupů §14):
//   - Naslouchá VÝHRADNĚ na loopbacku (http://127.0.0.1) – provoz
//     neopouští stroj, proto je plain HTTP akceptovatelný.
//   - IntegratedWindowsAuthentication + kontrola, že volající je
//     lokální administrator → běžný uživatel data nevidí.
//   - Read-only: žádné mutace stavu, žádný zápis.
//   - Ve výchozím configu VYPNUTO (localConsole.enabled=false);
//     zapíná se přes agent.config.local.json.
//
// Endpointy:
//   GET /            → HTML dashboard (auto-refresh)
//   GET /api/status  → JSON se živým stavem agenta
// ============================================================

using System.Net;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using USBGuardian.Models;

namespace USBGuardian.LocalConsole;

public class LocalConsoleService : BackgroundService
{
    private readonly ILogger<LocalConsoleService> _logger;
    private readonly DeviceMonitor _monitor;
    private readonly WhitelistChecker _whitelist;
    private readonly IncidentLogger _incidents;
    private readonly PolicyState _policy;
    private readonly DeviceBlocker _blocker;
    private readonly SelfRestartManager _selfRestart;
    private readonly string _policyMode;
    private readonly int _port;

    private HttpListener? _listener;

    public LocalConsoleService(
        ILogger<LocalConsoleService> logger,
        DeviceMonitor monitor,
        WhitelistChecker whitelist,
        IncidentLogger incidents,
        PolicyState policy,
        DeviceBlocker blocker,
        SelfRestartManager selfRestart,
        string policyMode,
        int port)
    {
        _logger     = logger;
        _monitor    = monitor;
        _whitelist  = whitelist;
        _incidents  = incidents;
        _policy     = policy;
        _blocker    = blocker;
        _selfRestart = selfRestart;
        _policyMode = policyMode;
        _port       = port;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener = new HttpListener();
        // Loopback only – záměrně NE 0.0.0.0 / hostname (žádný síťový přístup).
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.AuthenticationSchemes = AuthenticationSchemes.IntegratedWindowsAuthentication;

        // Po restartu služby drží registraci portu ještě dobíhající starý proces – http.sys ji
        // uvolní až s jeho koncem. Vzdát se napoprvé znamená, že konzole je mrtvá až do dalšího
        // restartu služby, přestože za pár vteřin je port volný. Proto to zkusit znovu — a nahlas,
        // ať je z logu poznat rozdíl mezi "chvíli obsazeno" a "nerozběhlo se vůbec".
        const int MaxPokusu = 6;
        var bezi = false;
        for (var pokus = 1; pokus <= MaxPokusu && !bezi; pokus++)
        {
            try
            {
                _listener.Start();
                bezi = true;
            }
            catch (Exception ex)
            {
                if (pokus == MaxPokusu)
                {
                    _logger.LogError(ex,
                        "Lokální konzole se na portu {Port} nerozběhla ani na {Pokusu}. pokus – konzole vypnuta. " +
                        "Port nejspíš drží starý proces agenta po nedokončeném restartu; break-glass ani uživatelská " +
                        "stránka na této stanici nepůjde.",
                        _port, MaxPokusu);
                    return;
                }

                _logger.LogWarning(
                    "Port {Port} je zatím obsazený ({Duvod}) – zkusím to znovu za 5 s ({Pokus}/{Pokusu})",
                    _port, ex.Message, pokus, MaxPokusu);

                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }

        _logger.LogInformation(
            "Lokální konzole běží na http://127.0.0.1:{Port}/ (loopback, admin-only, read-only)",
            _port);

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Chyba při čekání na požadavek lokální konzole");
                continue;
            }

            // Každý požadavek zpracovat samostatně – konzole se nesmí zaseknout
            _ = Task.Run(() => HandleRequest(ctx));
        }

        try { _listener.Stop(); } catch { }
        _logger.LogInformation("Lokální konzole zastavena");
    }

    // --------------------------------------------------------
    // Zpracování jednoho požadavku
    // --------------------------------------------------------
    private void HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            var path   = ctx.Request.Url?.AbsolutePath ?? "/";
            var method = ctx.Request.HttpMethod;

            // ── Autorizace: role rozhoduje, CO člověk uvidí ───────
            // Lokální admin → plná konzole (diagnostika, whitelist, break-glass, restart).
            // Kdokoli jiný → uživatelská stránka: jen jeho vlastní situace (co má připojené,
            // jestli se blokuje, čím se médium prokazuje). Žádný whitelist, žádná fronta,
            // žádná zapisující akce — blokování smí vypnout dál jen admin.
            if (!IsLocalAdmin(ctx.User))
            {
                HandleUzivatel(ctx, path, method);
                return;
            }

            // Break-glass (jediná zapisující akce – admin-only, loopback): dočasné vypnutí blokování offline.
            if (method == "POST" && path.Equals("/api/override", StringComparison.OrdinalIgnoreCase))
            {
                HandleSetOverride(ctx);
            }
            else if (method == "POST" && path.Equals("/api/override/clear", StringComparison.OrdinalIgnoreCase))
            {
                _policy.ClearOverride();
                _logger.LogWarning("Break-glass: override ručně zrušen ({By}).", ctx.User?.Identity?.Name ?? "?");
                // Zapnutí blokování zpět → OKAMŽITĚ znovu zablokovat připojená neschválená média
                // (jinak by médium vrácené break-glassem zůstalo viditelné až do dalšího reconcile cyklu).
                if (_policy.EffectiveMode(_policyMode) == "block")
                    _monitor.ReEnforceConnectedDevices();
                WriteJson(ctx, BuildStatus());
            }
            else if (method == "POST" && path.Equals("/api/unblock-all", StringComparison.OrdinalIgnoreCase))
            {
                // Ruční okamžité vrácení všech médií, která agent sám zakázal (admin-only, loopback).
                var restored = _blocker.UnblockAll();
                _logger.LogWarning("Lokální konzole: ruční vrácení blokovaných médií ({By}) → vráceno {N}",
                    ctx.User?.Identity?.Name ?? "?", restored);
                WriteJson(ctx, BuildStatus());
            }
            else if (method == "POST" && path.Equals("/api/restart", StringComparison.OrdinalIgnoreCase))
            {
                HandleRestart(ctx);
            }
            else if (method == "POST" && path.Equals("/api/selfrestart", StringComparison.OrdinalIgnoreCase))
            {
                HandleSelfRestartConfig(ctx);
            }
            else if (path.Equals("/api/status", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(ctx, BuildStatus());
            }
            // Admin si může zobrazit uživatelský pohled — aby při hovoru s uživatelem viděl
            // přesně to, co má ten člověk před sebou, a ne jen svůj dashboard.
            else if (path.Equals("/uzivatel", StringComparison.OrdinalIgnoreCase))
            {
                WriteHtml(ctx, UzivatelHtml);
            }
            else if (path.Equals("/api/muj-stav", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(ctx, BuildUserStatus());
            }
            else
            {
                WriteHtml(ctx, DashboardHtml);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chyba při zpracování požadavku lokální konzole");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    // --------------------------------------------------------
    // Uživatelská konzole (běžný účet, bez admin práv).
    //
    // Proč vůbec je: člověk, kterému nejde flashka, dosud viděl jen odmítnutí a jediná cesta
    // dál byla zavolat IT. Tahle stránka mu odpoví na to, na co se ptá — jestli se média
    // kontrolují, jestli je to jeho médium neschválené a čím se prokazuje, aby měl co poslat.
    //
    // Co NEuvidí: seznam schválených médií (znalost schválených sériových čísel oslabuje
    // whitelist), frontu incidentů, diagnostiku ani jedinou zapisující akci.
    // --------------------------------------------------------
    private void HandleUzivatel(HttpListenerContext ctx, string path, string method)
    {
        var kdo = PopisIdentity(ctx.User);

        if (method == "GET" && (path == "/"
                             || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase)
                             || path.Equals("/uzivatel", StringComparison.OrdinalIgnoreCase)))
        {
            // Debug, ne Warning: stránka se sama obnovuje, jinak by zaplavila Event Log.
            _logger.LogDebug("Uživatelská konzole zobrazena ({User})", kdo);
            WriteHtml(ctx, UzivatelHtml);
            return;
        }

        if (method == "GET" && path.Equals("/api/muj-stav", StringComparison.OrdinalIgnoreCase))
        {
            WriteJson(ctx, BuildUserStatus());
            return;
        }

        // Adminské endpointy a jakýkoli zápis: odmítnout a zalogovat — tohle už je pokus jít dál.
        // Skupiny v diagnostice: bez nich se "je to lokální admin, a přesto ho to nepustí"
        // nedalo vyšetřit - IsLocalAdmin řekne jen ANO/NE, ne PROČ.
        var skupiny = DiagnostikaSkupin(ctx.User);
        _logger.LogWarning("Lokální konzole: odmítnut přístup na {Method} {Path} ({User}) – skupiny v tokenu: {Skupiny}",
            method, path, kdo, skupiny);
        WriteBytes(ctx, 403, "text/html; charset=utf-8",
            Encoding.UTF8.GetBytes(OdmitnutoHtml(kdo, skupiny)));
    }

    // --------------------------------------------------------
    // Stav pro uživatelskou stránku – záměrně úzký výřez toho, co ví BuildStatus().
    // --------------------------------------------------------
    private object BuildUserStatus()
    {
        var blokuje = _policy.EffectiveMode(_policyMode) == "block";
        var blocked = _blocker.GetBlocked();

        var media = _monitor.GetActiveConnections()
            .OrderByDescending(c => c.ConnectedAt)
            .Select(c =>
            {
                var schvaleno   = !string.IsNullOrWhiteSpace(c.DeviceKey) && _whitelist.IsAllowedKey(c.DeviceKey);
                var zablokovano = blocked.ContainsKey(c.PnpDeviceId);
                return new
                {
                    nazev       = c.FriendlyName,
                    klic        = c.DeviceKey,
                    velikost    = c.Size,
                    pripojeno   = c.ConnectedAt,
                    schvaleno,
                    zablokovano,
                    stav = zablokovano ? "zablokováno"
                         : schvaleno   ? "schváleno"
                         : blokuje     ? "neschválené – bude zablokováno"
                                       : "neschválené – zatím jen hlášeno"
                };
            })
            .ToList();

        return new
        {
            hostname   = Environment.MachineName,
            generovano = DateTime.UtcNow,
            ochrana = new
            {
                blokuje,
                docasneVypnuto = _policy.OverrideActive,
                docasneDoKdy   = _policy.OverrideActive ? _policy.OverrideUntil : (DateTime?)null
            },
            agentVerze = AppInfo.Commit,
            media
        };
    }

    // --------------------------------------------------------
    // Break-glass: lokální admin dočasně vypne blokování (offline). Logováno + nahlášeno na server;
    // při příštím spojení se serverem se override zruší (server = zdroj pravdy).
    // --------------------------------------------------------
    private void HandleSetOverride(HttpListenerContext ctx)
    {
        var hours = 4;
        if (int.TryParse(ctx.Request.QueryString["hours"], out var h) && h > 0)
            hours = Math.Min(72, h);   // strop 72 h jako pojistka

        var until = DateTime.UtcNow.AddHours(hours);
        var by    = ctx.User?.Identity?.Name ?? "lokální admin";
        _policy.SetOverride(until, by);

        _logger.LogWarning(
            "Break-glass: {By} VYPNUL blokování na {H} h (do {Until} UTC) – offline výjimka, zruší se po spojení se serverem.",
            by, hours, until);

        // Hned vrátit média, která agent sám zakázal → admin může okamžitě připojit cokoli.
        var restored = _blocker.UnblockAll();
        if (restored > 0) _logger.LogWarning("Break-glass: vráceno {Count} dříve zablokovaných médií.", restored);

        // Auditní incident → fronta → server (NIS2 stopa kdo/kdy/jak dlouho).
        try
        {
            _incidents.LogConnection(new Incident
            {
                Username         = by,
                Device           = new DeviceInfo { FriendlyName = $"Break-glass: blokování vypnuto na {hours} h (offline)" },
                Action           = IncidentAction.OverrideDisabled,
                WhitelistVersion = _whitelist.GetVersion()
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Nelze zalogovat break-glass incident"); }

        WriteJson(ctx, BuildStatus());
    }

    // --------------------------------------------------------
    // Restart klientské služby na vyžádání lokálního admina. Agent běží jako SYSTEM → má lokální právo
    // restartovat vlastní službu. Spustí ODDĚLENÝ cmd (přežije zastavení služby): sc stop → pauza → sc start.
    // --------------------------------------------------------
    private void HandleRestart(HttpListenerContext ctx)
    {
        var by = ctx.User?.Identity?.Name ?? "lokální admin";
        _logger.LogWarning("Restart služby vyžádán z lokální konzole ({By}).", by);

        // odpovědět JEŠTĚ před restartem, ať klient dostane potvrzení
        WriteJson(ctx, new { restarting = true, by });

        // Vlastní provedení je v SelfRestartManageru – stejný kód použije i plánovaný noční restart.
        _selfRestart.Restart(by, scheduled: false);
    }

    // --------------------------------------------------------
    // Nastavení plánovaného (denního) restartu služby z lokální konzole.
    // Admin-only jako všechno ostatní; stav se perzistuje do ProgramData.
    // --------------------------------------------------------
    private void HandleSelfRestartConfig(HttpListenerContext ctx)
    {
        var by      = ctx.User?.Identity?.Name ?? "lokální admin";
        var enabled = string.Equals(ctx.Request.QueryString["enabled"], "true", StringComparison.OrdinalIgnoreCase);
        var at      = ctx.Request.QueryString["at"] ?? _selfRestart.At;

        if (!SelfRestartManager.TryParseTime(at, out _))
        {
            WriteText(ctx, 400, "400 – cas musi byt ve tvaru HH:mm");
            return;
        }

        _selfRestart.Configure(enabled, at, by);
        WriteJson(ctx, BuildStatus());
    }

    // --------------------------------------------------------
    // Je volající členem lokální skupiny Administrators?
    // --------------------------------------------------------
    private static bool IsLocalAdmin(IPrincipal? user)
    {
        if (user?.Identity is not WindowsIdentity { IsAuthenticated: true } identity)
            return false;

        // 1) Plný token – klasický případ.
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return true;

        // 2) UAC-FILTROVANÝ TOKEN. Přihlášení na 127.0.0.1 je z pohledu Windows
        //    SÍŤOVÉ, a u lokálního účtu se z takového tokenu skupina Administrators
        //    odebere (LocalAccountTokenFilterPolicy) – zůstane v něm jen jako
        //    "deny-only". IsInRole pak řekne NE, i když člověk lokální admin JE.
        //    Přesně tohle potkalo uživatele, který se chtěl dostat k break-glass:
        //    zadal správné přihlášení a konzole ho stejně odmítla.
        //
        //    Členství tu slouží jako AUTORIZACE, ne jako zdroj práv: samotnou akci
        //    provádí služba běžící pod SYSTEM, žádný elevovaný token k tomu není
        //    potřeba. Stačí tedy vědět, že ten člověk do skupiny patří.
        try
        {
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            if (identity.Groups is { } skupiny && skupiny.Any(g => g.Equals(admins)))
                return true;
        }
        catch { /* když token skupiny nenese, platí odpověď z bodu 1 */ }

        return false;
    }

    /// <summary>Popis identity do hlášky i do logu – bez toho se odmítnutí nedá diagnostikovat.</summary>
    private static string PopisIdentity(IPrincipal? user)
    {
        if (user?.Identity is not WindowsIdentity identity) return "neznámá identita";
        if (!identity.IsAuthenticated) return "nepřihlášeno (anonymní požadavek)";
        return identity.Name ?? "bez jména";
    }

    /// <summary>
    /// Syrový seznam skupin z tokenu – kdyz IsLocalAdmin řekne NE, tohle ukáže PROČ:
    /// jestli Administrators v tokenu vůbec je (třeba jen deny-only), nebo jestli čtení
    /// skupin rovnou spadlo (IsLocalAdmin takovou chybu dnes tiše polyká).
    /// Bez tohohle se rozpor "člověk JE lokální admin, konzole ho přesto odmítá" nedal vyšetřit.
    /// </summary>
    private static string DiagnostikaSkupin(IPrincipal? user)
    {
        if (user?.Identity is not WindowsIdentity identity) return "(žádná Windows identita)";
        if (!identity.IsAuthenticated) return "(neautentizováno)";

        try
        {
            var skupiny = identity.Groups;
            if (skupiny is null) return "(token nenese žádné skupiny – Groups je null)";
            if (skupiny.Count == 0) return "(token nenese žádné skupiny – prázdný seznam)";

            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var radky = new List<string>();
            foreach (var g in skupiny)
            {
                string popis;
                try { popis = g.Translate(typeof(NTAccount)).Value; }
                catch { popis = g.Value; }
                radky.Add(g.Equals(admins) ? popis + " ← ADMINISTRATORS" : popis);
            }
            return string.Join("; ", radky);
        }
        catch (Exception ex)
        {
            return $"(čtení skupin selhalo: {ex.GetType().Name}: {ex.Message})";
        }
    }

    // --------------------------------------------------------
    // Sestaví živý stav agenta (anonymní objekt → JSON)
    // --------------------------------------------------------
    private object BuildStatus()
    {
        var now = DateTime.UtcNow;
        var wl  = _whitelist.GetSnapshot();

        var (files, pending) = _incidents.GetQueueStats();

        var active = _monitor.GetActiveConnections()
            .Select(c => new
            {
                friendlyName    = c.FriendlyName,
                connectedAt     = c.ConnectedAt,
                durationSeconds = (int)(now - c.ConnectedAt).TotalSeconds
            })
            .OrderByDescending(c => c.connectedAt)
            .ToList();

        var recent = _incidents.GetRecentRecords(50)
            .Select(r => new
            {
                timestamp     = r.Timestamp,
                friendlyName  = r.FriendlyName,
                action        = r.Action,
                sizeFormatted = r.SizeFormatted,
                serialNumber  = r.SerialNumber,
                disconnected  = r.DisconnectedAt
            })
            .ToList();

        var devices = _whitelist.GetEntries()
            .Select(e => new
            {
                vendorId     = e.VendorId,
                productId    = e.ProductId,
                serialNumber = e.SerialNumber,
                description  = e.Description,
                approvedBy   = e.ApprovedBy,
                validUntil   = e.ValidUntil
            })
            .OrderBy(e => e.description)
            .ToList();

        return new
        {
            hostname    = Environment.MachineName,
            generatedAt = now,
            policyMode  = _policyMode,
            agentCommit = AppInfo.Commit,
            enforcement = new
            {
                effectiveMode  = _policy.EffectiveMode(_policyMode),   // block / warn
                serverEnforce  = _policy.ServerEnforce,
                serverReceived = _policy.ServerReceived,
                overrideActive = _policy.OverrideActive,
                overrideUntil  = _policy.OverrideUntil,
                overrideBy     = _policy.OverrideBy,
                blockedCount   = _blocker.BlockedCount,                // kolik médií agent právě drží zablokovaných
                blockedDevices = _blocker.GetBlocked()
                    .Select(kv => new { pnpId = kv.Key, key = kv.Value })
                    .ToList()
            },
            whitelist = new
            {
                version     = wl.Version,
                status      = wl.Status.ToString(),
                deviceCount = wl.DeviceCount,
                validUntil  = wl.ValidUntil == DateTime.MinValue ? (DateTime?)null : wl.ValidUntil,
                devices     = devices
            },
            monitor = new
            {
                lastWmiEventUtc       = _monitor.LastWmiEventAtUtc,
                secondsSinceLastEvent = (int)(now - _monitor.LastWmiEventAtUtc).TotalSeconds,
                activeConnections     = active
            },
            queue = new
            {
                files          = files,
                pendingRecords = pending
            },
            selfRestart = new
            {
                enabled    = _selfRestart.Enabled,
                at         = _selfRestart.At,
                lastResult = _selfRestart.LastResult,
                changedBy  = _selfRestart.ChangedBy,
                changedAt  = _selfRestart.ChangedAt,
                service    = _selfRestart.ServiceName
            },
            recent
        };
    }

    // --------------------------------------------------------
    // Odmítnutí musí říct KDO byl viděn a CO je potřeba. Holý řádek
    // "403 – pristup pouze pro lokalni administratory" nechal člověka
    // v terénu bez informace, co má dělat dál.
    // --------------------------------------------------------
    private static string OdmitnutoHtml(string kdo, string skupiny) => $$"""
        <!DOCTYPE html>
        <html lang="cs">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>USB Guardian – přístup odepřen</title>
          <style>
            body { margin:0; font-family:Segoe UI, system-ui, sans-serif; background:#0f1419; color:#e6e6e6;
                   display:flex; min-height:100vh; align-items:center; justify-content:center; }
            .k { background:#161b22; border:1px solid #2a2f37; border-radius:10px; padding:26px 30px; max-width:560px; }
            h1 { font-size:17px; margin:0 0 4px; }
            .p { color:#8b949e; font-size:13px; margin:0 0 16px; }
            dl { display:grid; grid-template-columns:130px 1fr; gap:6px 12px; font-size:13px; margin:0 0 16px; }
            dt { color:#8b949e; } dd { margin:0; }
            .mono { font-family:Consolas, monospace; }
            ul { font-size:13px; line-height:1.6; margin:0; padding-left:18px; color:#c9d1d9; }
          </style>
        </head>
        <body>
          <div class="k">
            <h1>Přístup odepřen</h1>
            <p class="p">Lokální konzole USB Guardian je jen pro správce tohoto počítače.</p>
            <dl>
              <dt>Přihlášen jako</dt><dd class="mono">{{kdo}}</dd>
              <dt>Potřeba</dt><dd>členství ve skupině <span class="mono">Administrators</span> na tomto počítači</dd>
              <dt>Skupiny v tokenu</dt><dd class="mono" style="word-break:break-all">{{skupiny}}</dd>
            </dl>
            <ul>
              <li>Přihlas se účtem, který je na tomhle počítači správcem.</li>
              <li>Konzole slouží k dočasnému vypnutí blokování médií, když jsi mimo firemní síť.</li>
              <li>Pokud správcem jsi a přesto tě to nepustí, nahlas to IT — chceme o tom vědět.</li>
            </ul>
          </div>
        </body>
        </html>
        """;

    // --------------------------------------------------------
    // HTTP odpovědi
    // --------------------------------------------------------
    private static void WriteJson(HttpListenerContext ctx, object payload)
    {
        var json = JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = false });
        WriteBytes(ctx, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json));
    }

    private static void WriteHtml(HttpListenerContext ctx, string html)
        => WriteBytes(ctx, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));

    private static void WriteText(HttpListenerContext ctx, int status, string text)
        => WriteBytes(ctx, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

    private static void WriteBytes(HttpListenerContext ctx, int status, string contentType, byte[] body)
    {
        try
        {
            ctx.Response.StatusCode  = status;
            ctx.Response.ContentType = contentType;
            // Bezpečnostní hlavičky – konzole nic neukládá ani neembeduje
            ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
            ctx.Response.Headers["Cache-Control"]          = "no-store";
            ctx.Response.ContentLength64 = body.Length;
            ctx.Response.OutputStream.Write(body, 0, body.Length);
        }
        finally
        {
            ctx.Response.Close();
        }
    }

    // --------------------------------------------------------
    // Uživatelská stránka – co vidí běžný účet. Self-contained, bez externích assetů.
    // Cíl: odpovědět na „proč mi nejde flashka" dřív, než člověk zvedne telefon na IT.
    // --------------------------------------------------------
    private const string UzivatelHtml = """
        <!DOCTYPE html>
        <html lang="cs">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>USB Guardian – moje média</title>
          <style>
            :root { color-scheme: dark; }
            body { margin:0; font-family:Segoe UI, system-ui, sans-serif; background:#0f1419; color:#e6e6e6;
                   display:flex; justify-content:center; padding:34px 16px; }
            .w { width:100%; max-width:640px; }
            h1 { font-size:19px; margin:0 0 2px; }
            .sub { color:#8b949e; font-size:13px; margin:0 0 20px; }
            .karta { background:#161b22; border:1px solid #2a2f37; border-radius:10px; padding:18px 20px; margin-bottom:14px; }
            .stav { display:flex; align-items:center; gap:11px; }
            .tecka { width:11px; height:11px; border-radius:50%; flex:0 0 auto; }
            .zelena { background:#2ea043; } .zluta { background:#d29922; } .seda { background:#6e7681; }
            .stav b { font-size:14.5px; font-weight:600; }
            .stav .d { color:#8b949e; font-size:12.5px; margin-top:3px; }
            h2 { font-size:12.5px; text-transform:uppercase; letter-spacing:.06em; color:#8b949e; margin:0 0 10px; font-weight:600; }
            .m { border-top:1px solid #21262d; padding:13px 0; }
            .m:first-of-type { border-top:none; padding-top:0; }
            .m .r { display:flex; justify-content:space-between; align-items:baseline; gap:12px; }
            .m .n { font-size:14px; font-weight:600; }
            .m .v { color:#8b949e; font-size:12px; margin-top:2px; }
            .znacka { font-size:11.5px; padding:2px 9px; border-radius:20px; white-space:nowrap; }
            .ok   { background:rgba(46,160,67,.16);  color:#5fd07a; border:1px solid rgba(46,160,67,.4); }
            .bad  { background:rgba(248,81,73,.14);  color:#ff7b72; border:1px solid rgba(248,81,73,.4); }
            .warn { background:rgba(210,153,34,.16); color:#e3b341; border:1px solid rgba(210,153,34,.4); }
            .id { margin-top:10px; background:#0d1117; border:1px solid #21262d; border-radius:8px; padding:10px 12px; }
            .id .l { color:#8b949e; font-size:11.5px; margin-bottom:5px; }
            .id .k { font-family:Consolas, monospace; font-size:12.5px; word-break:break-all; }
            button { margin-top:9px; font:inherit; font-size:12.5px; padding:6px 12px; border-radius:7px; cursor:pointer;
                     background:#21262d; color:#e6e6e6; border:1px solid #30363d; }
            button:hover { background:#2a3038; }
            .prazdno { color:#8b949e; font-size:13px; }
            .pomoc { font-size:13px; line-height:1.65; color:#c9d1d9; margin:0; padding-left:18px; }
            .pata { color:#6e7681; font-size:11.5px; text-align:center; margin-top:16px; font-family:Consolas, monospace; }
          </style>
        </head>
        <body>
        <div class="w">
          <h1>Moje média</h1>
          <p class="sub">Co má tenhle počítač právě připojené a jak se k tomu USB Guardian staví.</p>

          <div class="karta">
            <div class="stav">
              <span class="tecka seda" id="tecka"></span>
              <div><b id="ochrana">Načítám…</b><div class="d" id="ochranaD"></div></div>
            </div>
          </div>

          <div class="karta">
            <h2>Připojená média</h2>
            <div id="media"><div class="prazdno">Načítám…</div></div>
          </div>

          <div class="karta">
            <h2>Potřebuju schválit médium</h2>
            <ul class="pomoc">
              <li>U neschváleného média zkopíruj jeho identifikaci tlačítkem výš a pošli ji IT.</li>
              <li>Schvaluje se konkrétní kus média, ne typ — každý další kus se musí schválit zvlášť.</li>
              <li>Než je médium schválené, nekopíruj na něj pracovní data.</li>
            </ul>
          </div>

          <div class="pata" id="pata"></div>
        </div>
        <script>
        (function () {
          "use strict";
          var $ = function (id) { return document.getElementById(id); };

          function esc(s) {
            return String(s == null ? "" : s).replace(/[&<>"]/g, function (c) {
              return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c];
            });
          }

          function cas(iso) {
            try { return new Date(iso).toLocaleTimeString("cs-CZ", { hour: "2-digit", minute: "2-digit" }); }
            catch (e) { return "?"; }
          }

          function zaloha(text, hotovo) {
            var t = document.createElement("textarea");
            t.value = text; t.style.position = "fixed"; t.style.opacity = "0";
            document.body.appendChild(t); t.select();
            try { document.execCommand("copy"); hotovo(); } catch (e) { }
            document.body.removeChild(t);
          }

          function kopiruj(text, btn) {
            function hotovo() {
              btn.textContent = "Zkopírováno";
              setTimeout(function () { btn.textContent = "Zkopírovat pro IT"; }, 2000);
            }
            if (navigator.clipboard && navigator.clipboard.writeText) {
              navigator.clipboard.writeText(text).then(hotovo, function () { zaloha(text, hotovo); });
            } else { zaloha(text, hotovo); }
          }

          function vykresli(s) {
            var vypnuto = s.ochrana.docasneVypnuto;
            var blokuje = s.ochrana.blokuje;

            $("tecka").className = "tecka " + (vypnuto ? "zluta" : blokuje ? "zelena" : "zluta");
            $("ochrana").textContent = vypnuto ? "Blokování je dočasně vypnuté"
                                     : blokuje ? "Média se kontrolují a neschválená se blokují"
                                               : "Média se kontrolují, neschválená se zatím jen hlásí";
            $("ochranaD").textContent = vypnuto
              ? "Vypnul to správce tohoto počítače" + (s.ochrana.docasneDoKdy ? " do " + cas(s.ochrana.docasneDoKdy) : "") + "."
              : blokuje ? "Schválené firemní médium funguje bez omezení."
                        : "Neschválené médium teď funguje, ale zůstane o něm záznam.";

            var el = $("media");
            if (!s.media.length) {
              el.innerHTML = '<div class="prazdno">Teď není připojené žádné médium.</div>';
            } else {
              el.innerHTML = s.media.map(function (m) {
                var tr = m.zablokovano ? "bad" : m.schvaleno ? "ok" : "warn";
                var h = '<div class="m"><div class="r"><div>' +
                        '<div class="n">' + esc(m.nazev || "Neznámé médium") + '</div>' +
                        '<div class="v">připojeno v ' + cas(m.pripojeno) + (m.velikost ? " · " + esc(m.velikost) : "") + '</div>' +
                        '</div><span class="znacka ' + tr + '">' + esc(m.stav) + '</span></div>';
                if (!m.schvaleno && m.klic) {
                  h += '<div class="id"><div class="l">Identifikace média — tohle pošli IT:</div>' +
                       '<div class="k">' + esc(m.klic) + '</div>' +
                       '<button data-k="' + esc(m.klic) + '" data-n="' + esc(m.nazev || "") + '">Zkopírovat pro IT</button></div>';
                }
                return h + '</div>';
              }).join("");

              Array.prototype.forEach.call(el.querySelectorAll("button"), function (b) {
                b.onclick = function () {
                  kopiruj("USB Guardian – zadost o schvaleni media\n" +
                          "Pocitac: " + s.hostname + "\n" +
                          "Medium: " + b.getAttribute("data-n") + "\n" +
                          "Identifikace: " + b.getAttribute("data-k"), b);
                };
              });
            }

            $("pata").textContent = s.hostname + " · agent " + s.agentVerze;
          }

          function nacti() {
            fetch("/api/muj-stav", { cache: "no-store" })
              .then(function (r) { return r.json(); })
              .then(vykresli)
              .catch(function () { $("ochrana").textContent = "Stav se nepodařilo načíst"; });
          }

          nacti();
          setInterval(nacti, 5000);
        })();
        </script>
        </body>
        </html>
        """;

    // --------------------------------------------------------
    // Dashboard – self-contained (žádné externí assety, CSP-friendly)
    // Načítá /api/status a periodicky překresluje.
    // --------------------------------------------------------
    private const string DashboardHtml = """
        <!DOCTYPE html>
        <html lang="cs">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>USB Guardian – lokální konzole</title>
          <style>
            :root { color-scheme: dark; }
            * { box-sizing: border-box; }
            body { margin:0; font-family: Segoe UI, system-ui, sans-serif; background:#0f1419; color:#e6e6e6; }
            header { padding:14px 20px; background:#161b22; border-bottom:1px solid #2a2f37; display:flex; justify-content:space-between; align-items:center; flex-wrap:wrap; gap:8px; }
            header h1 { font-size:16px; margin:0; font-weight:600; }
            header .meta { font-size:12px; color:#8b949e; }
            main { padding:20px; display:grid; gap:16px; grid-template-columns:repeat(auto-fit,minmax(220px,1fr)); }
            .card { background:#161b22; border:1px solid #2a2f37; border-radius:8px; padding:14px 16px; }
            .card h2 { font-size:12px; text-transform:uppercase; letter-spacing:.05em; color:#8b949e; margin:0 0 10px; }
            .big { font-size:22px; font-weight:600; }
            .row { display:flex; justify-content:space-between; padding:3px 0; font-size:13px; }
            .row span:last-child { color:#c9d1d9; font-weight:500; }
            .full { grid-column:1/-1; }
            table { width:100%; border-collapse:collapse; font-size:13px; }
            th,td { text-align:left; padding:6px 8px; border-bottom:1px solid #21262d; }
            th { color:#8b949e; font-weight:500; font-size:11px; text-transform:uppercase; }
            .pill { display:inline-block; padding:1px 8px; border-radius:10px; font-size:11px; font-weight:600; }
            .ok { background:#16341f; color:#56d364; }
            .warn { background:#3a2d10; color:#e3b341; }
            .bad { background:#3a1518; color:#f85149; }
            .muted { color:#6e7681; }
            .muted-pill { background:#21262d; color:#8b949e; }
            .empty { color:#6e7681; font-size:13px; padding:8px 0; }
            .btn { font:inherit; font-size:12px; margin-top:10px; padding:7px 11px; border:1px solid #2a2f37;
                   background:#1c2333; color:#e6e6e6; border-radius:7px; cursor:pointer; }
            .btn.warn { border-color:#e3b341; } .btn.ok { border-color:#56d364; }
          </style>
        </head>
        <body>
          <header>
            <h1>USB Guardian — lokální konzole <span class="muted">(read-only)</span></h1>
            <div class="meta" id="meta">načítám…</div>
          </header>
          <main id="app"><div class="empty">načítám stav agenta…</div></main>
          <script>
            const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));
            const dt = s => s ? new Date(s).toLocaleString('cs-CZ') : '—';
            const dur = s => { s=Math.max(0,s|0); const m=(s/60|0), r=s%60; return m+':' + String(r).padStart(2,'0'); };

            function statusPill(st){
              const m = {Valid:'ok', Expired:'warn', Missing:'bad'};
              return `<span class="pill ${m[st]||'warn'}">${esc(st)}</span>`;
            }
            function actionPill(a){
              const m = {Allowed:'ok', Warned:'warn', Blocked:'bad'};
              return `<span class="pill ${m[a]||'warn'}">${esc(a)}</span>`;
            }

            async function setOv(h){
              if(!confirm('Dočasně VYPNOUT blokování USB na '+h+' h? Platí jen offline; po spojení se serverem se zruší. Akce se loguje a nahlásí na server.')) return;
              try{ await fetch('/api/override?hours='+h, {method:'POST'}); }catch(e){}
              refresh();
            }
            async function clearOv(){
              try{ await fetch('/api/override/clear', {method:'POST'}); }catch(e){}
              refresh();
            }
            async function unblockAll(){
              if(!confirm('Vrátit (povolit) všechna média, která agent zablokoval? Provede se okamžitě.')) return;
              try{ await fetch('/api/unblock-all', {method:'POST'}); }catch(e){}
              refresh();
            }
            async function saveSelfRestart(){
              const en = document.getElementById('srEnabled').checked;
              const at = document.getElementById('srAt').value;
              try{
                const r = await fetch('/api/selfrestart?enabled='+en+'&at='+encodeURIComponent(at), {method:'POST'});
                if(!r.ok){ alert('Nelze uložit: ' + await r.text()); }
              }catch(e){ alert('Nelze uložit: '+e.message); }
              refresh();
            }
            async function restartSvc(){
              if(!confirm('Restartovat klientskou službu USB Guardian? Konzole se na pár sekund odmlčí.')) return;
              try{ await fetch('/api/restart', {method:'POST'}); }catch(e){}
              document.getElementById('app').innerHTML = '<div class="empty">restartuji službu…</div>';
              setTimeout(refresh, 9000);
            }

            async function refresh(){
              try{
                const r = await fetch('/api/status', {cache:'no-store'});
                if(!r.ok){ document.getElementById('app').innerHTML =
                  `<div class="empty">chyba ${r.status} při čtení stavu</div>`; return; }
                render(await r.json());
              }catch(e){
                document.getElementById('app').innerHTML =
                  `<div class="empty">agent neodpovídá: ${esc(e.message)}</div>`;
              }
            }

            function render(d){
              document.getElementById('meta').textContent =
                `${d.hostname} · policy: ${d.policyMode} · agent ${d.agentCommit||'?'} · ${dt(d.generatedAt)}`;

              const wl = d.whitelist, mon = d.monitor, q = d.queue, enf = d.enforcement || {};
              const sr = d.selfRestart || {};
              const stale = mon.secondsSinceLastEvent > 600;
              const enfPill = enf.overrideActive
                ? '<span class="pill warn">BREAK-GLASS (neblokuje)</span>'
                : (enf.effectiveMode === 'block' ? '<span class="pill bad">BLOKUJE</span>' : '<span class="pill ok">jen varuje</span>');

              const wlDevices = (wl.devices||[]).map(e =>
                `<tr><td class="muted">${esc(e.vendorId)}</td><td class="muted">${esc(e.productId)}</td>
                 <td>${esc(e.serialNumber||'—')}</td><td>${esc(e.description||'—')}</td>
                 <td class="muted">${esc(e.approvedBy||'—')}</td>
                 <td>${e.validUntil ? dt(e.validUntil) : '<span class="muted">trvale</span>'}</td></tr>`).join('');

              const active = (mon.activeConnections||[]).map(c =>
                `<tr><td>${esc(c.friendlyName)}</td><td>${dt(c.connectedAt)}</td><td>${dur(c.durationSeconds)}</td></tr>`).join('');

              const recent = (d.recent||[]).map(r =>
                `<tr><td>${dt(r.timestamp)}</td><td>${esc(r.friendlyName)}</td>
                 <td>${actionPill(r.action)}</td><td>${esc(r.sizeFormatted)}</td>
                 <td class="muted">${esc(r.serialNumber)}</td>
                 <td>${r.disconnected ? dt(r.disconnected) : '<span class="muted">připojeno</span>'}</td></tr>`).join('');

              document.getElementById('app').innerHTML = `
                <div class="card">
                  <h2>Whitelist</h2>
                  <div class="big">${statusPill(wl.status)}</div>
                  <div class="row"><span>Verze</span><span>${esc(wl.version)}</span></div>
                  <div class="row"><span>Zařízení</span><span>${wl.deviceCount}</span></div>
                  <div class="row"><span>Platný do</span><span>${dt(wl.validUntil)}</span></div>
                </div>
                <div class="card">
                  <h2>WMI monitoring</h2>
                  <div class="big">${stale ? '<span class="pill bad">STALE</span>' : '<span class="pill ok">OK</span>'}</div>
                  <div class="row"><span>Poslední událost</span><span>${dt(mon.lastWmiEventUtc)}</span></div>
                  <div class="row"><span>Před</span><span>${dur(mon.secondsSinceLastEvent)}</span></div>
                </div>
                <div class="card">
                  <h2>Fronta (sync)</h2>
                  <div class="big">${q.pendingRecords}</div>
                  <div class="row"><span>Záznamů ve frontě</span><span>${q.pendingRecords}</span></div>
                  <div class="row"><span>Souborů</span><span>${q.files}</span></div>
                </div>
                <div class="card">
                  <h2>Vynucování</h2>
                  <div class="big">${enfPill}</div>
                  <div class="row"><span>Server (APP_SERVER)</span><span>${enf.serverReceived ? (enf.serverEnforce ? 'vynucovat' : 'jen varovat') : 'nepřijato'}</span></div>
                  <div class="row"><span>Break-glass</span><span>${enf.overrideActive ? ('do ' + dt(enf.overrideUntil)) : '—'}</span></div>
                  <div class="row"><span>Zablokováno teď</span><span>${enf.blockedCount ?? 0}</span></div>
                  ${enf.overrideActive
                    ? '<button class="btn ok" onclick="clearOv()">Zapnout blokování zpět</button>'
                    : '<button class="btn warn" onclick="setOv(4)">Vypnout blokování 4 h (offline)</button>'}
                  ${(enf.blockedCount > 0)
                    ? '<button class="btn ok" onclick="unblockAll()">Vrátit všechna média hned</button>'
                    : ''}
                </div>
                <div class="card">
                  <h2>Služba</h2>
                  <div class="row"><span>Agent</span><span>${esc(d.agentCommit||'?')}</span></div>
                  <div class="row"><span>Stav</span><span>běží</span></div>
                  <button class="btn" onclick="restartSvc()">↻ Restart služby</button>
                </div>
                <div class="card">
                  <h2>Plánovaný restart</h2>
                  <div class="big">${sr.enabled ? '<span class="pill ok">ZAPNUTO</span>' : '<span class="pill muted-pill">vypnuto</span>'}</div>
                  <div class="row"><span>Denně v</span>
                    <span><input id="srAt" type="time" value="${esc(sr.at||'03:30')}"></span></div>
                  <div class="row"><span>Zapnuto</span>
                    <span><input id="srEnabled" type="checkbox" ${sr.enabled ? 'checked' : ''}></span></div>
                  <div class="row"><span>Poslední běh</span><span>${sr.lastResult ? esc(sr.lastResult) : '—'}</span></div>
                  <button class="btn" onclick="saveSelfRestart()">Uložit plán</button>
                  <div class="muted" style="font-size:11px;margin-top:6px">
                    Služba se jednou denně sama restartuje. Když stanice v ten čas neběžela,
                    dohání se to nejvýš dvě hodiny — pak se počká na další den.</div>
                </div>
                <div class="card full">
                  <h2>Schválená zařízení – whitelist (${(wl.devices||[]).length})</h2>
                  ${wlDevices ? `<table><tr><th>VID</th><th>PID</th><th>Sériové číslo</th><th>Popis</th><th>Schválil</th><th>Platnost</th></tr>${wlDevices}</table>`
                              : '<div class="empty">whitelist je prázdný nebo nedostupný</div>'}
                </div>
                <div class="card full">
                  <h2>Právě připojená média (${(mon.activeConnections||[]).length})</h2>
                  ${active ? `<table><tr><th>Médium</th><th>Připojeno</th><th>Doba</th></tr>${active}</table>`
                           : '<div class="empty">žádné připojené médium</div>'}
                </div>
                <div class="card full">
                  <h2>Poslední události (dnes)</h2>
                  ${recent ? `<table><tr><th>Čas</th><th>Médium</th><th>Akce</th><th>Velikost</th><th>S/N</th><th>Odpojeno</th></tr>${recent}</table>`
                           : '<div class="empty">dnes žádné události</div>'}
                </div>`;
            }

            refresh();
            setInterval(refresh, 3000);
          </script>
        </body>
        </html>
        """;
}
