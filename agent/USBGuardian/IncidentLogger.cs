// ============================================================
// IncidentLogger.cs
// Loguje VŠECHNA připojení médií (povolená i nepovolená).
// Ukládá do denních JSON souborů ve frontě.
// Formát: log_HOSTNAME_2026-03-16.json
// Po odeslání přesune do sent\ složky (ne smaže) – audit trail.
// Sent soubory starší sentRetentionDays se automaticky mažou.
//
// v1.1 – přidán DisconnectedAt tracking:
//   - DeviceRecord má nullable DisconnectedAt
//   - UpdateDisconnectedAt() doplní čas odpojení do existujícího záznamu
//   - Korelace přes PnpDeviceId + ConnectedAt timestamp
// ============================================================

using System.Text.Json;
using Microsoft.Extensions.Logging;
using USBGuardian.Models;

namespace USBGuardian;

public class IncidentLogger
{
    private readonly ILogger<IncidentLogger> _logger;
    private readonly string _queuePath;
    private readonly string _sentPath;
    private readonly int _sentRetentionDays;

    // Zámek pro thread-safe zápis do denního souboru
    private readonly object _writeLock = new();

    public IncidentLogger(
        ILogger<IncidentLogger> logger,
        string queuePath,
        string sentPath,
        int sentRetentionDays = 90)
    {
        _logger            = logger;
        _queuePath         = queuePath;
        _sentPath          = sentPath;
        _sentRetentionDays = sentRetentionDays;

        Directory.CreateDirectory(_queuePath);
        Directory.CreateDirectory(_sentPath);

        CleanupSentFiles();
    }

    // --------------------------------------------------------
    // Zaznamená připojení média – povolené i nepovolené
    // --------------------------------------------------------
    public void LogConnection(Incident incident)
    {
        try
        {
            var record = new DeviceRecord
            {
                Timestamp        = incident.Timestamp,
                Username         = incident.Username,
                VendorId         = incident.Device.VendorId,
                ProductId        = incident.Device.ProductId,
                SerialNumber     = incident.Device.SerialNumber,
                FriendlyName     = incident.Device.FriendlyName,
                DeviceType       = incident.Device.Type.ToString(),
                SizeBytes        = incident.Device.SizeBytes,
                SizeFormatted    = incident.Device.SizeFormatted,
                FirmwareRevision = incident.Device.FirmwareRevision,
                PnpDeviceId      = incident.Device.PnpDeviceId,
                Action           = incident.Action.ToString(),
                WhitelistVersion = incident.WhitelistVersion,
                DisconnectedAt   = null   // doplní se při odpojení
            };

            AppendToDaily(record);

            _logger.LogInformation(
                "Záznam uložen: {User}@{Host} → {Device} → {Action}",
                incident.Username, incident.Hostname,
                incident.Device.FriendlyName, incident.Action);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chyba při ukládání záznamu do fronty");
        }
    }

    // --------------------------------------------------------
    // Doplní čas odpojení do existujícího záznamu v dnešním souboru.
    // Korelace: PnpDeviceId + ConnectedAt timestamp (sekundy – bez ms).
    // Pokud záznam nenajde (restart agenta apod.), pouze loguje warning.
    // --------------------------------------------------------
    public void UpdateDisconnectedAt(string pnpDeviceId, DateTime connectedAt, DateTime disconnectedAt)
    {
        var today    = DateTime.UtcNow.Date;
        var hostname = Environment.MachineName;
        var fileName = $"log_{hostname}_{today:yyyy-MM-dd}.json";
        var filePath = Path.Combine(_queuePath, fileName);

        lock (_writeLock)
        {
            if (!File.Exists(filePath))
            {
                _logger.LogWarning(
                    "UpdateDisconnectedAt: soubor nenalezen pro {PnpId}", pnpDeviceId);
                return;
            }

            var existing = File.ReadAllText(filePath);
            var daily    = JsonSerializer.Deserialize<DailyLog>(existing,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (daily == null) return;

            // Hledáme záznam podle PnpDeviceId + timestamp (přesnost na sekundy)
            var record = daily.Records.LastOrDefault(r =>
                r.PnpDeviceId == pnpDeviceId &&
                Math.Abs((r.Timestamp - connectedAt).TotalSeconds) < 5);

            if (record == null)
            {
                // Záloha: hledáme jen podle PnpDeviceId (nejnovější bez DisconnectedAt)
                record = daily.Records.LastOrDefault(r =>
                    r.PnpDeviceId == pnpDeviceId &&
                    r.DisconnectedAt == null);
            }

            if (record == null)
            {
                _logger.LogWarning(
                    "UpdateDisconnectedAt: záznam nenalezen pro {PnpId} @ {Time}",
                    pnpDeviceId, connectedAt);
                return;
            }

            record.DisconnectedAt = disconnectedAt;

            var duration = disconnectedAt - record.Timestamp;
            _logger.LogInformation(
                "Odpojení zaznamenáno: {Device} – doba připojení: {Duration:mm\\:ss}",
                record.FriendlyName, duration);

            // Atomický zápis
            var json     = JsonSerializer.Serialize(daily,
                new JsonSerializerOptions { WriteIndented = true });
            var tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, filePath, overwrite: true);
        }
    }

    // --------------------------------------------------------
    // Přidá záznam do denního souboru (thread-safe)
    // --------------------------------------------------------
    private void AppendToDaily(DeviceRecord record)
    {
        var today    = DateTime.UtcNow.Date;
        var hostname = Environment.MachineName;
        var fileName = $"log_{hostname}_{today:yyyy-MM-dd}.json";
        var filePath = Path.Combine(_queuePath, fileName);

        lock (_writeLock)
        {
            DailyLog daily;

            if (File.Exists(filePath))
            {
                var existing = File.ReadAllText(filePath);
                daily = JsonSerializer.Deserialize<DailyLog>(existing,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new DailyLog();
            }
            else
            {
                daily = new DailyLog
                {
                    Date     = today.ToString("yyyy-MM-dd"),
                    Hostname = hostname
                };
            }

            daily.Records.Add(record);
            daily.RecordCount = daily.Records.Count;

            // Atomický zápis přes temp soubor
            var json     = JsonSerializer.Serialize(daily,
                new JsonSerializerOptions { WriteIndented = true });
            var tempPath = filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, filePath, overwrite: true);
        }
    }

    // --------------------------------------------------------
    // Vrátí seznam souborů připravených k odeslání
    // --------------------------------------------------------
    public List<string> GetFilesReadyToSync()
    {
        var hostname = Environment.MachineName;

        return Directory
            .GetFiles(_queuePath, $"log_{hostname}_*.json")
            .OrderBy(f => f)
            .ToList();
    }

    // --------------------------------------------------------
    // Vrátí true pokud je soubor aktuální den
    // --------------------------------------------------------
    public bool IsTodaysFile(string filePath)
    {
        var today = DateTime.UtcNow.Date;
        var name  = Path.GetFileNameWithoutExtension(filePath);
        var parts = name.Split('_');
        if (parts.Length >= 3 &&
            DateTime.TryParse(parts[^1], out var fileDate))
            return fileDate.Date == today;
        return false;
    }

    // --------------------------------------------------------
    // Přesune odeslaný soubor do sent\ složky (audit trail)
    // --------------------------------------------------------
    public void MoveTeSent(string filePath)
    {
        try
        {
            var fileName    = Path.GetFileName(filePath);
            var sentPath    = Path.Combine(_sentPath, fileName);
            var offsetPath  = filePath + ".offset"; // smazat i offset soubor

            if (File.Exists(sentPath))
                sentPath = Path.Combine(_sentPath,
                    $"{Path.GetFileNameWithoutExtension(fileName)}_{DateTime.UtcNow:HHmmss}.json");

            File.Move(filePath, sentPath);

            // Smazat offset soubor spolu s log souborem
            if (File.Exists(offsetPath))
                File.Delete(offsetPath);

            _logger.LogDebug("Soubor přesunut do sent: {File}", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nelze přesunout soubor do sent: {File}",
                Path.GetFileName(filePath));
        }
    }

    // --------------------------------------------------------
    // Úklid sent\ souborů starších sentRetentionDays
    // --------------------------------------------------------
    private void CleanupSentFiles()
    {
        try
        {
            var cutoff  = DateTime.UtcNow.Date.AddDays(-_sentRetentionDays);
            var files   = Directory.GetFiles(_sentPath, "log_*.json");
            var deleted = 0;

            foreach (var file in files)
            {
                var name  = Path.GetFileNameWithoutExtension(file);
                var parts = name.Split('_');
                if (parts.Length >= 3 &&
                    DateTime.TryParse(parts[^1], out var fileDate))
                {
                    if (fileDate.Date < cutoff)
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }
            }

            if (deleted > 0)
                _logger.LogInformation(
                    "Uklidil {Count} sent souborů starších {Days} dní",
                    deleted, _sentRetentionDays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chyba při úklidu sent složky");
        }
    }

    // --------------------------------------------------------
    // Statistika fronty
    // --------------------------------------------------------
    public (int files, int totalRecords) GetQueueStats()
    {
        try
        {
            var files = Directory.GetFiles(_queuePath, "log_*.json");
            var total = 0;

            foreach (var f in files)
            {
                try
                {
                    var content = File.ReadAllText(f);
                    var daily   = JsonSerializer.Deserialize<DailyLog>(content,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    total += daily?.RecordCount ?? 0;
                }
                catch { }
            }

            return (files.Length, total);
        }
        catch
        {
            return (0, 0);
        }
    }
}

// ── Datové modely pro JSON soubory ───────────────────────────

public class DailyLog
{
    public string             Date        { get; set; } = string.Empty;
    public string             Hostname    { get; set; } = string.Empty;
    public int                RecordCount { get; set; }
    public List<DeviceRecord> Records     { get; set; } = new();
}

public class DeviceRecord
{
    public DateTime  Timestamp        { get; set; }
    public DateTime? DisconnectedAt   { get; set; }   // null = stále připojeno / neznámo
    public string    Username         { get; set; } = string.Empty;
    public string    VendorId         { get; set; } = string.Empty;
    public string    ProductId        { get; set; } = string.Empty;
    public string    SerialNumber     { get; set; } = string.Empty;
    public string    FriendlyName     { get; set; } = string.Empty;
    public string    DeviceType       { get; set; } = string.Empty;
    public long      SizeBytes        { get; set; }
    public string    SizeFormatted    { get; set; } = string.Empty;
    public string    FirmwareRevision { get; set; } = string.Empty;
    public string    PnpDeviceId      { get; set; } = string.Empty;
    public string    Action           { get; set; } = string.Empty;
    public string    WhitelistVersion { get; set; } = string.Empty;
}
