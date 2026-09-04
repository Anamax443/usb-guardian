// ============================================================
// IncidentQueueWorker.cs
// Background worker – čte batche z IncidentQueue a zapisuje
// je do SQL Serveru. Zpracovává sekvenčně → SQL Server
// dostane rovnoměrnou zátěž místo spike při thundering herd.
//
// Při chybě batch NEZTRATÍME – zůstává na disku (IncidentSpool) a při
// dalším startu služby se přehraje (ReplaySpoolAsync), agent se o něj
// starat nemusí (2xx už dostal). Dedup v ProcessBatch dělá případné
// vícenásobné přehrání neškodným.
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
    private readonly IncidentSpool _spool;
    private readonly IServiceScopeFactory _scopeFactory;

    public IncidentQueueWorker(
        ILogger<IncidentQueueWorker> logger,
        IncidentQueue queue,
        IncidentSpool spool,
        IServiceScopeFactory scopeFactory)
    {
        _logger       = logger;
        _queue        = queue;
        _spool        = spool;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("IncidentQueueWorker spuštěn");

        // Co zbylo na disku z minula (pád procesu mezi 202 a zápisem do DB,
        // nebo prostě běžný restart služby dřív, než Channel stihl vyprázdnit) –
        // zpracuje se první, před čerstvým provozem, ať se nepřeskočí pořadí.
        await ReplaySpoolAsync(stoppingToken);

        // Čteme dokud služba běží
        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessBatch(item, stoppingToken);
                _spool.Delete(item.SpoolFile);
            }
            catch (Exception ex)
            {
                // Batch NEmažeme ze spoolu – zůstává na disku a přehraje se
                // při dalším startu služby (ReplaySpoolAsync výš).
                _logger.LogError(ex,
                    "Chyba při zpracování batche od {Hostname} – zůstává ve spoolu, přehraje se při dalším startu",
                    item.Request.Hostname);
            }
        }

        _logger.LogInformation("IncidentQueueWorker zastaven");
    }

    private async Task ReplaySpoolAsync(CancellationToken ct)
    {
        var pending = _spool.LoadPending();
        if (pending.Count == 0) return;

        _logger.LogWarning(
            "IncidentQueueWorker: {Count} nedokončených batchů ve spoolu z minula – přehrávám",
            pending.Count);

        foreach (var item in pending)
        {
            try
            {
                await ProcessBatch(item, ct);
                _spool.Delete(item.SpoolFile);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Přehrání spoolu selhalo pro {Hostname} ({File}) – zkusí se zas při dalším startu",
                    item.Request.Hostname, item.SpoolFile);
            }
        }
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
            .Select(i => new { i.Id, i.Timestamp, i.SerialNumber, i.VendorId, i.ProductId, i.PnpDeviceId, i.DisconnectedAt })
            .ToListAsync(ct);

        var existingLookup = existingMap
            .GroupBy(i => MakeKey(i.Timestamp, i.SerialNumber, i.VendorId, i.ProductId, i.PnpDeviceId))
            .ToDictionary(g => g.Key, g => g.First());

        var newIncidents    = new List<Incident>();
        var updatedCount    = 0;
        var duplicatesCount = 0;

        foreach (var dto in request.Incidents)
        {
            var key = MakeKey(dto.Timestamp, dto.SerialNumber, dto.VendorId, dto.ProductId, dto.PnpDeviceId);

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

    // Audit 04.09.2026: chyběly ProductId/PnpDeviceId - dvě různá zařízení stejného vendoru se
    // stejným (často generickým, u levných USB kusů sdíleným) sériovým číslem, připojená ve
    // stejné sekundě, by se dřív smíchala - druhý incident by dedup tiše zahodil jako duplikát
    // prvního, místo aby ho zapsal. ProductId a PnpDeviceId nejsou u retry resendu (offset persist
    // na agentovi, IncidentSync.cs) rizikové - jde o bajtově stejný záznam, takže se pořád spárují
    // se svým dřívějším zápisem stejně jako dřív.
    internal static string MakeKey(DateTime ts, string serial, string vendor, string productId, string pnpDeviceId) =>
        $"{ts:yyyy-MM-ddTHH:mm:ss}|{serial}|{vendor}|{productId}|{pnpDeviceId}";
}
