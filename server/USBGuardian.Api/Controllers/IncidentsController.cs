// ============================================================
// IncidentsController.cs
// Příjem incidentů z agentů (batch upload)
// POST /api/incidents → 202 Accepted (zapsáno na disk + zařazeno do fronty)
//
// Logika zpracování přesunuta do IncidentQueueWorker.
// Controller batch nejdřív zapíše přes IncidentSpool (přežije pád procesu),
// pak ho zařadí do Channel fronty. Díky tomu HTTP response time zůstává
// nezávislý na SQL zátěži (lokální diskový zápis, ne vzdálený SQL round-trip),
// ale 202 už neznamená jen "je v RAM" – agent po 2xx batch víc nepošle.
// ============================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;
using USBGuardian.Api.Models;
using USBGuardian.Api.Queue;

namespace USBGuardian.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Microsoft.AspNetCore.Authorization.Authorize(Policy = "USBGuardianClients")]
public class IncidentsController : ControllerBase
{
    private readonly IncidentQueue _queue;
    private readonly IncidentSpool _spool;
    private readonly AppDbContext _db;
    private readonly ILogger<IncidentsController> _logger;
    private readonly ActivityLogger _dennik;

    public IncidentsController(
        IncidentQueue queue,
        IncidentSpool spool,
        AppDbContext db,
        ILogger<IncidentsController> logger,
        ActivityLogger dennik)
    {
        _queue  = queue;
        _spool  = spool;
        _db     = db;
        _logger = logger;
        _dennik = dennik;
    }

    // --------------------------------------------------------
    // POST /api/incidents
    // Zařadí batch do fronty a vrátí 202 Accepted okamžitě.
    // Worker zpracuje batch asynchronně vlastním tempem.
    // --------------------------------------------------------
    [HttpPost]
    public IActionResult SubmitBatch([FromBody] IncidentBatchRequest request)
    {
        if (request.Incidents.Count == 0)
            return Ok(new { queued = 0 });

        var sourceIp   = HttpContext.Connection.RemoteIpAddress?.ToString();
        var receivedAt = DateTime.UtcNow;

        string spoolFile;
        try
        {
            // Zapsat na disk DŘÍV, než agentovi cokoliv potvrdíme – agent po 2xx
            // odpovědi batch víc nepošle (offset persist na klientovi), takže
            // "přijato" musí od teď znamenat "přežije i pád procesu API",
            // ne jen "je v paměťovém Channelu" (viz IncidentSpool.cs).
            spoolFile = _spool.Write(request, sourceIp, receivedAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Spool zápis selhal pro {Hostname} – batch NEpotvrzen, agent zkusí znovu",
                request.Hostname);
            return StatusCode(500, new { error = "Spool write failed – retry later" });
        }

        var item = new IncidentBatchItem(
            Request:    request,
            SourceIp:   sourceIp,
            ReceivedAt: receivedAt,
            SpoolFile:  spoolFile);

        // Deník: kolik incidentů kdo poslal. Bez toho jde zpětně zjistit jen to,
        // co v databázi JE — ne to, že se něco poslat POKUSILO a fronta to odmítla.
        _dennik.Log("incidents",
            $"přijato {request.Incidents.Count} incidentů (soubor {request.SourceFile ?? "?"})",
            ActivityLevel.Info, request.Hostname);

        // TryWrite je non-blocking – okamžitě vrátí true/false
        if (!_queue.TryEnqueue(item))
        {
            // Fronta plná (> 1000 čekajících batchů) – batch NEpotvrzujeme
            // (agent ho pošle znovu), takže rozepsaný spool soubor by tu jen
            // ležel navíc; smazat.
            _spool.Delete(spoolFile);

            _logger.LogWarning(
                "Fronta plná – batch od {Hostname} odmítnut (pending: {Count})",
                request.Hostname, _queue.PendingCount);

            return StatusCode(503, new
            {
                error   = "Queue full – retry later",
                pending = _queue.PendingCount
            });
        }

        _logger.LogDebug(
            "Batch od {Hostname} zařazen do fronty ({Count} incidentů, pending: {Pending})",
            request.Hostname, request.Incidents.Count, _queue.PendingCount);

        // 202 Accepted = přijato, bude zpracováno
        return Accepted(new
        {
            queued  = request.Incidents.Count,
            pending = _queue.PendingCount
        });
    }

    // --------------------------------------------------------
    // GET /api/incidents
    // --------------------------------------------------------
    [HttpGet]
    public async Task<IActionResult> GetIncidents(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string?   hostname,
        [FromQuery] string?   username,
        [FromQuery] int       page     = 1,
        [FromQuery] int       pageSize = 50)
    {
        var query = _db.Incidents.AsQueryable();

        if (from.HasValue)                   query = query.Where(i => i.Timestamp >= from.Value);
        if (to.HasValue)                     query = query.Where(i => i.Timestamp <= to.Value);
        if (!string.IsNullOrEmpty(hostname)) query = query.Where(i => i.Hostname == hostname);
        if (!string.IsNullOrEmpty(username)) query = query.Where(i => i.Username == username);

        var total = await query.CountAsync();
        var items = await query
            .OrderByDescending(i => i.Timestamp)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new { total, page, pageSize, items });
    }

    // --------------------------------------------------------
    // GET /api/incidents/queue/status
    // Monitoring – počet čekajících batchů (paměťová fronta i disková spool
    // vrstva). Anonymní záměrně (stejně jako /api/version) – nese jen počty
    // a stáří, žádný obsah incidentů, a čte ho konzole běžící na JINÉM
    // serveru pro Kontroly stavu (HealthService.cs) – ta se přihlásit
    // Windows identitou API serveru neumí.
    // --------------------------------------------------------
    [HttpGet("queue/status")]
    [Microsoft.AspNetCore.Authorization.AllowAnonymous]
    public IActionResult QueueStatus()
    {
        var spool = _spool.GetStatus();
        return Ok(new
        {
            pending               = _queue.PendingCount,
            spoolPending          = spool.PendingCount,
            spoolOldestAgeSeconds = spool.OldestReceivedAtUtc is { } oldest
                ? (int)(DateTime.UtcNow - oldest).TotalSeconds
                : (int?)null
        });
    }
}
