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
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetryDelay      = TimeSpan.FromMinutes(5);

    private readonly ILogger<IncidentQueueWorker> _logger;
    private readonly IncidentQueue _queue;
    private readonly IncidentSpool _spool;
    private readonly IServiceScopeFactory _scopeFactory;

    // Worker je záměrně sekvenční (viz komentář nahoře souboru - rovnoměrná zátěž na SQL
    // místo thundering herd). RetrySpoolLoopAsync běží souběžně s hlavním čtením z Channelu
    // po celou dobu života služby - bez zámku by mohl zkusit přehrát batch, který live smyčka
    // právě zpracovává (spool soubor se maže AŽ PO úspěšném zápisu do DB), a dvakrát souběžně
    // zapsat stejný incident dřív, než by ho dedup v ProcessBatch stihl uvidět z prvního zápisu.
    private readonly SemaphoreSlim _processingLock = new(1, 1);

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

        // Nález oponentury 11.09.2026: ReplaySpoolAsync výš pokrývá jen to, co zbylo PŘED
        // startem služby. Výpadek SQL UPROSTŘED běžícího provozu nechá batch ve spoolu úplně
        // stejně (catch níže), ale nic ho samo nezkusí znovu, dokud službu někdo ručně
        // nerestartuje - dřív to tak dokumentace i přiznávala. RetrySpoolLoopAsync běží
        // souběžně po celou dobu života služby a spool zkouší přehrát sám.
        _ = RetrySpoolLoopAsync(stoppingToken);

        // Čteme dokud služba běží
        await foreach (var item in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            await _processingLock.WaitAsync(stoppingToken);
            try
            {
                await ProcessBatch(item, stoppingToken);
                _spool.Delete(item.SpoolFile);
            }
            catch (Exception ex)
            {
                // Batch NEmažeme ze spoolu – zůstává na disku, RetrySpoolLoopAsync ho zkusí
                // znovu automaticky (žádný ruční restart potřeba).
                _logger.LogError(ex,
                    "Chyba při zpracování batche od {Hostname} – zůstává ve spoolu, zkusí se znovu automaticky",
                    item.Request.Hostname);
            }
            finally { _processingLock.Release(); }
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
            await TryProcessSpoolItemAsync(item, ct, "Přehrání spoolu při startu");
    }

    // --------------------------------------------------------
    // Nález oponentury 11.09.2026: bez tohohle cyklu se batch zaseklý ve spoolu kvůli výpadku
    // SQL uprostřed provozu (na rozdíl od pádu procesu, který pokryje ReplaySpoolAsync při
    // startu) sám od sebe nezpracoval - čekal, až službu někdo ručně restartuje.
    //
    // Ohraničený exponenciální odstup: GetStatus() je levný (jen počet souborů, bez
    // deserializace obsahu), takže časté "je něco ve spoolu?" kontroly nic nestojí - prodlužuje
    // se až SKUTEČNÝ neúspěšný pokus o zápis do SQL, ať se na spadlý server zbytečně netlačí.
    // Při prvním úspěchu se odstup vrátí na InitialRetryDelay, ať se zbytek spoolu (pokud SQL
    // spadl na déle a nastřádalo se víc batchů) zkusí brzy znovu, ne až za MaxRetryDelay.
    // --------------------------------------------------------
    private async Task RetrySpoolLoopAsync(CancellationToken ct)
    {
        var delay = InitialRetryDelay;

        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }

            if (_spool.GetStatus().PendingCount == 0)
            {
                delay = InitialRetryDelay;
                continue;
            }

            var pending = _spool.LoadPending();
            _logger.LogWarning(
                "IncidentQueueWorker retry: {Count} batchů čeká ve spoolu – zkouším znovu (odstup {Delay})",
                pending.Count, delay);

            var processedAny = false;
            foreach (var item in pending)
                if (await TryProcessSpoolItemAsync(item, ct, "Retry spoolu"))
                    processedAny = true;

            delay = NextRetryDelay(delay, processedAny);
        }
    }

    // Čistá rozhodovací funkce (bez I/O) - testovatelná odděleně od Task.Delay/SQL.
    internal static TimeSpan NextRetryDelay(TimeSpan currentDelay, bool processedAnyThisAttempt) =>
        processedAnyThisAttempt
            ? InitialRetryDelay
            : TimeSpan.FromSeconds(Math.Min(currentDelay.TotalSeconds * 2, MaxRetryDelay.TotalSeconds));

    private async Task<bool> TryProcessSpoolItemAsync(IncidentBatchItem item, CancellationToken ct, string context)
    {
        await _processingLock.WaitAsync(ct);
        try
        {
            await ProcessBatch(item, ct);
            _spool.Delete(item.SpoolFile);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Context} zatím neúspěšné pro {Hostname} ({File}) – zkusí se znovu",
                context, item.Request.Hostname, item.SpoolFile);
            return false;
        }
        finally { _processingLock.Release(); }
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
