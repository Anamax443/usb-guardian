// ============================================================
// IncidentsController.cs
// Příjem incidentů z agentů (batch upload)
// POST /api/incidents
//
// v1.1 – DisconnectedAt podpora:
//   - IncidentDto má nullable DisconnectedAt
//   - Pokud záznam existuje a přišel DisconnectedAt → UPSERT (aktualizace)
//   - Nový záznam → INSERT
//
// v1.2 – fix N+1 deduplikace:
//   - Načteme existující klíče jedním bulk dotazem
//   - In-memory lookup místo N SQL dotazů
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
    // Batch upload incidentů z agenta.
    // Nové záznamy → INSERT
    // Existující záznamy s DisconnectedAt → UPDATE DisconnectedAt
    // --------------------------------------------------------
    [HttpPost]
    public async Task<IActionResult> SubmitBatch([FromBody] IncidentBatchRequest request)
    {
        if (request.Incidents.Count == 0)
            return Ok(new { accepted = 0, updated = 0, duplicates = 0 });

        var sourceIp = HttpContext.Connection.RemoteIpAddress?.ToString();

        // Upsert počítače
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

        // ── Fix N+1: načíst existující záznamy pro tuto stanici jedním dotazem ──
        // Rozsah: záznamy z posledních 24h (disconnect update přijde vždy v den připojení)
        var since      = DateTime.UtcNow.AddHours(-24);
        var timestamps = request.Incidents.Select(i => i.Timestamp).Distinct().ToList();

        // Načteme existující záznamy matchující hostname + timestamp (bulk)
        var existingMap = await _db.Incidents
            .Where(i => i.Hostname == request.Hostname && i.Timestamp >= since)
            .Select(i => new { i.Id, i.Timestamp, i.SerialNumber, i.VendorId, i.DisconnectedAt })
            .ToListAsync();

        // Rychlý lookup: klíč = "timestamp|serial|vendor"
        var existingLookup = existingMap
            .GroupBy(i => MakeKey(i.Timestamp, i.SerialNumber, i.VendorId))
            .ToDictionary(g => g.Key, g => g.First());

        var newIncidents     = new List<Incident>();
        var updatedCount     = 0;
        var duplicatesCount  = 0;

        foreach (var dto in request.Incidents)
        {
            var key = MakeKey(dto.Timestamp, dto.SerialNumber, dto.VendorId);

            if (existingLookup.TryGetValue(key, out var existing))
            {
                // Záznam existuje
                if (dto.DisconnectedAt.HasValue && existing.DisconnectedAt == null)
                {
                    // Aktualizovat DisconnectedAt – médium bylo odpojeno
                    await _db.Incidents
                        .Where(i => i.Id == existing.Id)
                        .ExecuteUpdateAsync(s =>
                            s.SetProperty(i => i.DisconnectedAt, dto.DisconnectedAt));
                    updatedCount++;
                }
                else
                {
                    duplicatesCount++;
                }
            }
            else
            {
                // Nový záznam
                newIncidents.Add(new Incident
                {
                    Timestamp        = dto.Timestamp,
                    DisconnectedAt   = dto.DisconnectedAt,
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
        }

        if (newIncidents.Count > 0)
        {
            _db.Incidents.AddRange(newIncidents);
            await _db.SaveChangesAsync();
        }

        _logger.LogInformation(
            "Přijato od {Hostname} ({Ip}): {New} nových, {Upd} disconnect aktualizací, {Dup} duplikátů",
            request.Hostname, sourceIp, newIncidents.Count, updatedCount, duplicatesCount);

        return Ok(new
        {
            accepted   = newIncidents.Count,
            updated    = updatedCount,
            duplicates = duplicatesCount
        });
    }

    // Klíč pro deduplikaci – timestamp na sekundy (bez ms) + serial + vendor
    private static string MakeKey(DateTime ts, string serial, string vendor) =>
        $"{ts:yyyy-MM-ddTHH:mm:ss}|{serial}|{vendor}";

    // --------------------------------------------------------
    // GET /api/incidents
    // --------------------------------------------------------
    [HttpGet]
    public async Task<IActionResult> GetIncidents(
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? hostname,
        [FromQuery] string? username,
        [FromQuery] int page     = 1,
        [FromQuery] int pageSize = 50)
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
}
