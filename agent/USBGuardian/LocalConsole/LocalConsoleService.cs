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
        string policyMode,
        int port)
    {
        _logger     = logger;
        _monitor    = monitor;
        _whitelist  = whitelist;
        _incidents  = incidents;
        _policy     = policy;
        _blocker    = blocker;
        _policyMode = policyMode;
        _port       = port;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener = new HttpListener();
        // Loopback only – záměrně NE 0.0.0.0 / hostname (žádný síťový přístup).
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.AuthenticationSchemes = AuthenticationSchemes.IntegratedWindowsAuthentication;

        try
        {
            _listener.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Lokální konzole nelze spustit na portu {Port} – konzole vypnuta", _port);
            return;
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
            // ── Authorizace: pouze lokální admin ──────────────────
            if (!IsLocalAdmin(ctx.User))
            {
                _logger.LogWarning(
                    "Lokální konzole: odmítnut neadmin přístup ({User})",
                    ctx.User?.Identity?.Name ?? "anonymní");
                WriteText(ctx, 403, "403 – pristup pouze pro lokalni administratory");
                return;
            }

            var path   = ctx.Request.Url?.AbsolutePath ?? "/";
            var method = ctx.Request.HttpMethod;

            // Break-glass (jediná zapisující akce – admin-only, loopback): dočasné vypnutí blokování offline.
            if (method == "POST" && path.Equals("/api/override", StringComparison.OrdinalIgnoreCase))
            {
                HandleSetOverride(ctx);
            }
            else if (method == "POST" && path.Equals("/api/override/clear", StringComparison.OrdinalIgnoreCase))
            {
                _policy.ClearOverride();
                _logger.LogWarning("Break-glass: override ručně zrušen ({By}).", ctx.User?.Identity?.Name ?? "?");
                WriteJson(ctx, BuildStatus());
            }
            else if (method == "POST" && path.Equals("/api/restart", StringComparison.OrdinalIgnoreCase))
            {
                HandleRestart(ctx);
            }
            else if (path.Equals("/api/status", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(ctx, BuildStatus());
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

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = "cmd.exe",
                Arguments       = "/c sc stop \"USB Guardian\" & ping -n 4 127.0.0.1 >nul & sc start \"USB Guardian\"",
                UseShellExecute = false,
                CreateNoWindow  = true
            });
        }
        catch (Exception ex) { _logger.LogError(ex, "Restart služby selhal"); }
    }

    // --------------------------------------------------------
    // Je volající členem lokální skupiny Administrators?
    // --------------------------------------------------------
    private static bool IsLocalAdmin(IPrincipal? user)
    {
        if (user?.Identity is not WindowsIdentity { IsAuthenticated: true } identity)
            return false;

        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
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
                overrideBy     = _policy.OverrideBy
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
            recent
        };
    }

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
                  <div class="row"><span>Server (.213)</span><span>${enf.serverReceived ? (enf.serverEnforce ? 'vynucovat' : 'jen varovat') : 'nepřijato'}</span></div>
                  <div class="row"><span>Break-glass</span><span>${enf.overrideActive ? ('do ' + dt(enf.overrideUntil)) : '—'}</span></div>
                  ${enf.overrideActive
                    ? '<button class="btn ok" onclick="clearOv()">Zapnout blokování zpět</button>'
                    : '<button class="btn warn" onclick="setOv(4)">Vypnout blokování 4 h (offline)</button>'}
                </div>
                <div class="card">
                  <h2>Služba</h2>
                  <div class="row"><span>Agent</span><span>${esc(d.agentCommit||'?')}</span></div>
                  <div class="row"><span>Stav</span><span>běží</span></div>
                  <button class="btn" onclick="restartSvc()">↻ Restart služby</button>
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
