// ============================================================
// IncidentQueueWorker.cs
// Background worker – čte batche z IncidentQueue a zapisuje
// je do SQL Serveru. Zpracovává sekvenčně → SQL Server
// dostane rovnoměrnou zátěž místo spike při thundering herd.
//
// Při chybě batch NEZTRATÍME – agent ho pošle znovu
// (offset persist na straně agenta zajistí retry).
// ============================================================

using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;
using USBGuardian.Api.Models;
using USBGuardian.Api.Queue;

namespace USBGuardian.Api.Queue;

public class IncidentQueueWorker : BackgroundService
{
    private readonly ILogger<IncidentQueueWorker> _logger;
    private readonly IncidentQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;

    public IncidentQueueWorker(
        ILogger<IncidentQueueWorker> logger,
        IncidentQueue queue,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _queue        = queue;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IncidentQueueWorker spuštěn");

        // Čteme dokud služba běží
        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessBatch(item, stoppingToken);
            }
            catch (Exception ex)
            {
                // Logujeme ale nepřerušujeme worker – zpracujeme další batch
                _logger.LogError(ex,
                    "Chyba při zpracování batche od {Hostname} – batch zahozen, agent zkusí znovu",
                    item.Request.Hostname);
            }
        }

        _logger.LogInformation("IncidentQueueWorker zastaven");
    }

    // --------------------------------------------------------
    // Zpracování jednoho batche – stejná logika jako původní
    // IncidentsController, přesunuta sem aby se neblokoval HTTP
    // --------------------------------------------------------
    private async Task ProcessBatch(IncidentBatchItem item, CancellationToken ct)
    {
        var request    = item.Request;
        var sourceIp   = item.SourceIp;

        // DbContext musí být scoped – worker je singleton
        using var scope = _scopeFactory.CreateScope();
        var db          = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        _logger.LogInformation(
            "Worker zpracovává batch od {Hostname} ({Count} incidentů, ve frontě: {Pending})",
            request.Hostname, request.Incidents.Count, _queue.PendingCount);

        // Upsert počítače
        var computer = await db.Computers
            .FirstOrDefaultAsync(c => c.Hostname == request.Hostname, ct);

        if (computer == null)
        {
            computer = new Computer
            {
                Hostname     = request.Hostname,
                AgentVersion = request.AgentVersion,
                LastSeen     = item.ReceivedAt
            };
            db.Computers.Add(computer);
        }
        else
        {
            computer.LastSeen     = item.ReceivedAt;
            computer.AgentVersion = request.AgentVersion;
        }
        await db.SaveChangesAsync(ct);

        // Bulk deduplikace – jeden dotaz na celý batch
        var since = DateTime.UtcNow.AddHours(-24);

        var existingMap = await db.Incidents
            .Where(i => i.Hostname == request.Hostname && i.Timestamp >= since)
            .Select(i => new { i.Id, i.Timestamp, i.SerialNumber, i.VendorId, i.DisconnectedAt })
            .ToListAsync(ct);

        var existingLookup = existingMap
            .GroupBy(i => MakeKey(i.Timestamp, i.SerialNumber, i.VendorId))
            .ToDictionary(g => g.Key, g => g.First());

        var newIncidents    = new List<Incident>();
        var updatedCount    = 0;
        var duplicatesCount = 0;

        foreach (var dto in request.Incidents)
        {
            var key = MakeKey(dto.Timestamp, dto.SerialNumber, dto.VendorId);

            if (existingLookup.TryGetValue(key, out var existing))
            {
                if (dto.DisconnectedAt.HasValue && existing.DisconnectedAt == null)
                {
                    await db.Incidents
                        .Where(i => i.Id == existing.Id)
                        .ExecuteUpdateAsync(s =>
                            s.SetProperty(i => i.DisconnectedAt, dto.DisconnectedAt), ct);
                    updatedCount++;
                }
                else
                {
                    duplicatesCount++;
                }
            }
            else
            {
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
                    ReceivedAt       = item.ReceivedAt,
                    SourceIp         = sourceIp
                });
            }
        }

        if (newIncidents.Count > 0)
        {
            db.Incidents.AddRange(newIncidents);
            await db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "Worker hotov: {Hostname} – {New} nových, {Upd} disconnect aktualizací, {Dup} duplikátů",
            request.Hostname, newIncidents.Count, updatedCount, duplicatesCount);
    }

    private static string MakeKey(DateTime ts, string serial, string vendor) =>
        $"{ts:yyyy-MM-ddTHH:mm:ss}|{serial}|{vendor}";
}
