// ============================================================
// IncidentLogger.cs
// Loguje VŠECHNA připojení médií (povolená i nepovolená).
// Ukládá do denních JSON souborů ve frontě.
// Formát: log_2026-03-16.json
// Soubory starší 3 měsíce se automaticky mažou.
// IncidentSync odesílá uzavřené soubory (ne aktuální den).
// ============================================================

using System.Text.Json;
using Microsoft.Extensions.Logging;
using USBGuardian.Models;

namespace USBGuardian;

public class IncidentLogger
{
    private readonly ILogger<IncidentLogger> _logger;
    private readonly string _queuePath;

    // Maximální stáří souborů – starší se smažou automaticky
    private const int MaxAgeDays = 90; // 3 měsíce

    // Zámek pro thread-safe zápis do denního souboru
    private readonly object _writeLock = new();

    public IncidentLogger(ILogger<IncidentLogger> logger, string queuePath)
    {
        _logger    = logger;
        _queuePath = queuePath;

        Directory.CreateDirectory(_queuePath);

        // Při startu uklidíme staré soubory
        CleanupOldFiles();
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
                WhitelistVersion = incident.WhitelistVersion
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
            // Načteme existující denní log nebo vytvoříme nový
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
                    Hostname = Environment.MachineName
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
    // = všechny soubory KROMĚ dnešního (ten se ještě zapisuje)
    // --------------------------------------------------------
    public List<string> GetFilesReadyToSync()
    {
        var today    = DateTime.UtcNow.Date;
        var hostname = Environment.MachineName;

        return Directory
            .GetFiles(_queuePath, $"log_{hostname}_*.json")
            .Where(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                // Formát: log_HOSTNAME_2026-03-16 → datum je poslední část
                var parts = name.Split('_');
                if (parts.Length >= 3 &&
                    DateTime.TryParse(parts[^1], out var fileDate))
                    return fileDate.Date < today;
                return false;
            })
            .OrderBy(f => f)
            .ToList();
    }

    // --------------------------------------------------------
    // Smazání souboru po úspěšném odeslání
    // --------------------------------------------------------
    public void DeleteFile(string filePath)
    {
        try
        {
            File.Delete(filePath);
            _logger.LogDebug("Soubor smazán po odeslání: {File}",
                Path.GetFileName(filePath));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nelze smazat soubor: {File}", filePath);
        }
    }

    // --------------------------------------------------------
    // Úklid souborů starších 3 měsíce
    // --------------------------------------------------------
    private void CleanupOldFiles()
    {
        try
        {
            var cutoff  = DateTime.UtcNow.Date.AddDays(-MaxAgeDays);
            var files   = Directory.GetFiles(_queuePath, "log_*.json");
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
                    "Uklidil {Count} souborů starších {Days} dní",
                    deleted, MaxAgeDays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chyba při úklidu starých souborů");
        }
    }

    // --------------------------------------------------------
    // Statistika fronty (pro log při startu)
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
    public string       Date        { get; set; } = string.Empty;
    public string       Hostname    { get; set; } = string.Empty;
    public int          RecordCount { get; set; }
    public List<DeviceRecord> Records { get; set; } = new();
}

public class DeviceRecord
{
    public DateTime Timestamp        { get; set; }
    public string   Username         { get; set; } = string.Empty;
    public string   VendorId         { get; set; } = string.Empty;
    public string   ProductId        { get; set; } = string.Empty;
    public string   SerialNumber     { get; set; } = string.Empty;
    public string   FriendlyName     { get; set; } = string.Empty;
    public string   DeviceType       { get; set; } = string.Empty;
    public long     SizeBytes        { get; set; }
    public string   SizeFormatted    { get; set; } = string.Empty;
    public string   FirmwareRevision { get; set; } = string.Empty;
    public string   PnpDeviceId      { get; set; } = string.Empty;
    public string   Action           { get; set; } = string.Empty;
    public string   WhitelistVersion { get; set; } = string.Empty;
}
