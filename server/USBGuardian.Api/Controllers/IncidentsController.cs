// ============================================================
// IncidentsController.cs
// Příjem incidentů z agentů (batch upload)
// POST /api/incidents
// ============================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;
using USBGuardian.Api.Models;

namespace USBGuardian.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Microsoft.AspNetCore.Authorization.Authorize(Policy = "USBGuardianClients")]
public class IncidentsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<IncidentsController> _logger;

    public IncidentsController(AppDbContext db, ILogger<IncidentsController> logger)
    {
        _db     = db;
        _logger = logger;
    }

    // --------------------------------------------------------
    // POST /api/incidents
    // Agent odesílá batch incidentů (všechny kde SentToServer=0)
    // --------------------------------------------------------
    [HttpPost]
    public async Task<IActionResult> SubmitBatch([FromBody] IncidentBatchRequest request)
    {
        if (request.Incidents.Count == 0)
            return Ok(new { accepted = 0 });

        var sourceIp = HttpContext.Connection.RemoteIpAddress?.ToString();

        // Upsert počítače (aktualizujeme LastSeen)
        var computer = await _db.Computers
            .FirstOrDefaultAsync(c => c.Hostname == request.Hostname);

        if (computer == null)
        {
            computer = new Computer
            {
                Hostname     = request.Hostname,
                AgentVersion = request.AgentVersion,
                LastSeen     = DateTime.UtcNow
            };
            _db.Computers.Add(computer);
        }
        else
        {
            computer.LastSeen     = DateTime.UtcNow;
            computer.AgentVersion = request.AgentVersion;
        }

        await _db.SaveChangesAsync();

        // Uložení incidentů – s deduplikací
        // Dnešní soubor se odesílá opakovaně → kontrolujeme duplicity
        // Klíč: Hostname + Timestamp + SerialNumber
        var newIncidents = new List<Incident>();

        foreach (var dto in request.Incidents)
        {
            // Kontrola duplicity
            var exists = await _db.Incidents.AnyAsync(i =>
                i.Hostname    == request.Hostname &&
                i.Timestamp   == dto.Timestamp &&
                i.SerialNumber == dto.SerialNumber &&
                i.VendorId    == dto.VendorId);

            if (exists) continue;

            newIncidents.Add(new Incident
            {
                Timestamp        = dto.Timestamp,
                Hostname         = request.Hostname,
                Username         = dto.Username,
                ComputerId       = computer.Id,
                VendorId         = dto.VendorId,
                ProductId        = dto.ProductId,
                SerialNumber     = dto.SerialNumber,
                FriendlyName     = dto.FriendlyName,
                DeviceType       = dto.DeviceType,
                SizeBytes        = dto.SizeBytes,
                FirmwareRevision = dto.FirmwareRevision,
                PnpDeviceId      = dto.PnpDeviceId,
                Action           = dto.Action,
                WhitelistVersion = dto.WhitelistVersion,
                SourceFile       = !string.IsNullOrEmpty(dto.SourceFile)
                                   ? dto.SourceFile
                                   : request.SourceFile,
                ReceivedAt       = DateTime.UtcNow,
                SourceIp         = sourceIp
            });
        }

        if (newIncidents.Count > 0)
        {
            _db.Incidents.AddRange(newIncidents);
            await _db.SaveChangesAsync();
        }

        _logger.LogInformation(
            "Přijato {New} nových incidentů od {Hostname} ({Ip}), {Skip} duplicit přeskočeno",
            newIncidents.Count, request.Hostname, sourceIp,
            request.Incidents.Count - newIncidents.Count);

        return Ok(new { accepted = newIncidents.Count, duplicates = request.Incidents.Count - newIncidents.Count });
    }

    // --------------------------------------------------------
    // GET /api/incidents?from=2026-03-01&hostname=PC-01
    // Pro Admin UI – výpis incidentů s filtry
    // --------------------------------------------------------
    [HttpGet]
    public async Task<IActionResult> GetIncidents(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? hostname,
        [FromQuery] string? username,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var query = _db.Incidents.AsQueryable();

        if (from.HasValue)     query = query.Where(i => i.Timestamp >= from.Value);
        if (to.HasValue)       query = query.Where(i => i.Timestamp <= to.Value);
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
}
