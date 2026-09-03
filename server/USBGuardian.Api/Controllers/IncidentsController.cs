// ============================================================
// IncidentsController.cs
// Příjem incidentů z agentů (batch upload)
// POST /api/incidents → 202 Accepted (zařadí do fronty)
//
// Logika zpracování přesunuta do IncidentQueueWorker.
// Controller jen validuje a zařadí do Channel fronty.
// Díky tomu HTTP response time < 1ms bez ohledu na SQL zátěž.
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
    private readonly AppDbContext _db;
    private readonly ILogger<IncidentsController> _logger;
    private readonly ActivityLogger _dennik;

    public IncidentsController(
        IncidentQueue queue,
        AppDbContext db,
        ILogger<IncidentsController> logger,
        ActivityLogger dennik)
    {
        _queue  = queue;
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

        var sourceIp = HttpContext.Connection.RemoteIpAddress?.ToString();

        var item = new IncidentBatchItem(
            Request:    request,
            SourceIp:   sourceIp,
            ReceivedAt: DateTime.UtcNow);

        // Deník: kolik incidentů kdo poslal. Bez toho jde zpětně zjistit jen to,
        // co v databázi JE — ne to, že se něco poslat POKUSILO a fronta to odmítla.
        _dennik.Log("incidents",
            $"přijato {request.Incidents.Count} incidentů (soubor {request.SourceFile ?? "?"})",
            ActivityLevel.Info, request.Hostname);

        // TryWrite je non-blocking – okamžitě vrátí true/false
        if (!_queue.TryEnqueue(item))
        {
            // Fronta plná (> 1000 čekajících batchů) – agent zkusí příště
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
    // Monitoring – počet čekajících batchů ve frontě
    // --------------------------------------------------------
    [HttpGet("queue/status")]
    public IActionResult QueueStatus()
        => Ok(new { pending = _queue.PendingCount });
}
