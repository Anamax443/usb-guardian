// ============================================================
// DeviceMonitor.cs
// Sleduje připojení paměťových médií přes tři WMI watchers:
//   1. Win32_DiskDrive (creation)  – fyzický disk připojen
//   2. Win32_LogicalDisk (creation) – drive letter přiřazen
//   3. Win32_DiskDrive (deletion)  – fyzický disk odpojen
// Korelace: diskIndex z DiskDrive → DiskIndex v LogicalDisk
//
// v1.1 – WMI Watchdog:
//   - Každých 5 minut ověří živost WMI subscriptions
//   - Při selhání automaticky re-registruje watchers
//
// v1.2 – Obousměrný timing fix:
//   - _pendingDevices     – DiskDrive přišel první, čeká na LogicalDisk
//   - _pendingDriveLetters – LogicalDisk přišel první, čeká na DiskDrive
//   - Timeout 30 sekund
//
// v1.3 – Disconnect tracking:
//   - Třetí WMI watcher: __InstanceDeletionEvent na Win32_DiskDrive
//   - _activeConnections: mapa PnpDeviceId → (SerialNumber, ConnectedAt)
//   - Při odpojení volá IncidentLogger.UpdateDisconnectedAt()
// ============================================================

using System.Collections.Concurrent;
using System.Management;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using USBGuardian.Models;

namespace USBGuardian;

public class DeviceMonitor : BackgroundService
{
    private readonly ILogger<DeviceMonitor> _logger;
    private readonly WhitelistChecker _whitelistChecker;
    private readonly PolicyEnforcer _policyEnforcer;
    private readonly IncidentLogger _incidentLogger;

    // Tři WMI watchers
    private ManagementEventWatcher? _diskWatcher;
    private ManagementEventWatcher? _logicalWatcher;
    private ManagementEventWatcher? _disconnectWatcher;

    // Scénář A: DiskDrive přišel první → čeká na drive letter
    private readonly ConcurrentDictionary<int, (DeviceInfo Device, DateTime DetectedAt)>
        _pendingDevices = new();

    // Scénář B: LogicalDisk přišel první → čeká na DiskDrive
    private readonly ConcurrentDictionary<int, (string DriveLetter, DateTime DetectedAt)>
        _pendingDriveLetters = new();

    // Aktivní spojení – pro korelaci s disconnect eventem
    // Key: PnpDeviceId, Value: (FriendlyName, ConnectedAt)
    private readonly ConcurrentDictionary<string, (string FriendlyName, DateTime ConnectedAt)>
        _activeConnections = new();

    private const int PairingTimeoutSeconds  = 30;
    private const int WatchdogIntervalSeconds = 300;

    // Watchdog
    private Timer?   _watchdogTimer;
    private DateTime _lastWmiEventAt = DateTime.UtcNow;

    public DeviceMonitor(
        ILogger<DeviceMonitor> logger,
        WhitelistChecker whitelistChecker,
        PolicyEnforcer policyEnforcer,
        IncidentLogger incidentLogger)
    {
        _logger           = logger;
        _whitelistChecker = whitelistChecker;
        _policyEnforcer   = policyEnforcer;
        _incidentLogger   = incidentLogger;
    }

    // --------------------------------------------------------
    // Read-only snapshot pro lokální konzoli (živý stav)
    // --------------------------------------------------------
    public record ActiveConnectionInfo(string PnpDeviceId, string FriendlyName, DateTime ConnectedAt);

    /// <summary>Aktuálně připojená sledovaná média (kopie, thread-safe).</summary>
    public IReadOnlyList<ActiveConnectionInfo> GetActiveConnections() =>
        _activeConnections
            .Select(kv => new ActiveConnectionInfo(kv.Key, kv.Value.FriendlyName, kv.Value.ConnectedAt))
            .ToList();

    /// <summary>Čas poslední WMI události (UTC) – indikátor živosti monitoringu.</summary>
    public DateTime LastWmiEventAtUtc => _lastWmiEventAt;

    // --------------------------------------------------------
    // Spuštění monitoringu
    // --------------------------------------------------------
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("USB Guardian spuštěn – zahajuji monitoring zařízení");

        StartDiskWatcher();
        StartLogicalDiskWatcher();
        StartDisconnectWatcher();
        StartWatchdog();

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException) { }
        finally
        {
            StopWatchers();
            _logger.LogInformation("USB Guardian zastaven");
        }
    }

    // --------------------------------------------------------
    // Watcher 1: fyzický disk připojen (Win32_DiskDrive creation)
    // --------------------------------------------------------
    private void StartDiskWatcher()
    {
        try
        {
            var query = new WqlEventQuery(
                "__InstanceCreationEvent",
                TimeSpan.FromSeconds(2),
                "TargetInstance ISA 'Win32_DiskDrive'");

            _diskWatcher = new ManagementEventWatcher(query);
            _diskWatcher.EventArrived += OnDiskConnected;
            _diskWatcher.Start();

            _logger.LogInformation("WMI watcher spuštěn (Win32_DiskDrive connect)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chyba při spuštění DiskDrive watcheru");
        }
    }

    // --------------------------------------------------------
    // Watcher 2: logický disk / drive letter (Win32_LogicalDisk)
    // --------------------------------------------------------
    private void StartLogicalDiskWatcher()
    {
        try
        {
            var query = new WqlEventQuery(
                "__InstanceCreationEvent",
                TimeSpan.FromSeconds(2),
                "TargetInstance ISA 'Win32_LogicalDisk'");

            _logicalWatcher = new ManagementEventWatcher(query);
            _logicalWatcher.EventArrived += OnLogicalDiskConnected;
            _logicalWatcher.Start();

            _logger.LogInformation("WMI watcher spuštěn (Win32_LogicalDisk)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chyba při spuštění LogicalDisk watcheru");
        }
    }

    // --------------------------------------------------------
    // Watcher 3: fyzický disk odpojen (Win32_DiskDrive deletion)
    // --------------------------------------------------------
    private void StartDisconnectWatcher()
    {
        try
        {
            var query = new WqlEventQuery(
                "__InstanceDeletionEvent",
                TimeSpan.FromSeconds(2),
                "TargetInstance ISA 'Win32_DiskDrive'");

            _disconnectWatcher = new ManagementEventWatcher(query);
            _disconnectWatcher.EventArrived += OnDiskDisconnected;
            _disconnectWatcher.Start();

            _logger.LogInformation("WMI watcher spuštěn (Win32_DiskDrive disconnect)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chyba při spuštění Disconnect watcheru");
        }
    }

    // --------------------------------------------------------
    // Watchdog – periodicky ověřuje živost WMI subscriptions
    // --------------------------------------------------------
    private void StartWatchdog()
    {
        _watchdogTimer = new Timer(
            callback: _ => CheckWatchdog(),
            state: null,
            dueTime:  TimeSpan.FromSeconds(WatchdogIntervalSeconds),
            period:   TimeSpan.FromSeconds(WatchdogIntervalSeconds));

        _logger.LogInformation("WMI watchdog spuštěn (interval {Sec}s)", WatchdogIntervalSeconds);
    }

    private void CheckWatchdog()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\cimv2",
                "SELECT DeviceID FROM Win32_DiskDrive WHERE Size > 0");
            searcher.Get().Dispose();

            _logger.LogDebug("WMI watchdog OK (poslední událost: {Age:F1} min zpět)",
                (DateTime.UtcNow - _lastWmiEventAt).TotalMinutes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WMI watchdog detekoval selhání – re-registruji watchers");
            ReRegisterWatchers();
        }
    }

    private void ReRegisterWatchers()
    {
        try { _diskWatcher?.Stop();       _diskWatcher?.Dispose();       } catch { }
        try { _logicalWatcher?.Stop();    _logicalWatcher?.Dispose();    } catch { }
        try { _disconnectWatcher?.Stop(); _disconnectWatcher?.Dispose(); } catch { }

        try
        {
            StartDiskWatcher();
            StartLogicalDiskWatcher();
            StartDisconnectWatcher();
            _logger.LogInformation("WMI watchers úspěšně re-registrovány");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Re-registrace WMI watcherů selhala – agent bez ochrany!");
        }
    }

    // --------------------------------------------------------
    // Zastavení všech watcherů + watchdog timeru
    // --------------------------------------------------------
    private void StopWatchers()
    {
        try { _watchdogTimer?.Dispose();                                  } catch { }
        try { _diskWatcher?.Stop();       _diskWatcher?.Dispose();        } catch { }
        try { _logicalWatcher?.Stop();    _logicalWatcher?.Dispose();     } catch { }
        try { _disconnectWatcher?.Stop(); _disconnectWatcher?.Dispose();  } catch { }
    }

    // --------------------------------------------------------
    // Callback 1: fyzický disk připojen
    // Scénář A: DiskDrive přišel první → čekat na LogicalDisk
    // Scénář B: LogicalDisk přišel první → spárovat rovnou
    // --------------------------------------------------------
    private void OnDiskConnected(object sender, EventArrivedEventArgs e)
    {
        _lastWmiEventAt = DateTime.UtcNow;

        try
        {
            var wmi = e.NewEvent["TargetInstance"] as ManagementBaseObject;
            if (wmi == null) return;

            if (!IsRemovableMedia(wmi)) return;

            var device    = ParseDeviceFromWmi(wmi);
            var diskIndex = ExtractDiskIndex(wmi["DeviceID"]?.ToString() ?? string.Empty);

            _logger.LogInformation("Detekováno médium: {Device} (DiskIndex={Index})",
                device, diskIndex);

            if (diskIndex < 0)
            {
                ProcessDevice(device);
                return;
            }

            // Scénář B: LogicalDisk čekal
            if (_pendingDriveLetters.TryRemove(diskIndex, out var pendingDrive))
            {
                device.DriveLetters.Add(pendingDrive.DriveLetter);
                _logger.LogInformation(
                    "Spárováno (LogicalDisk čekal): drive letter {Letter}: → {Device}",
                    pendingDrive.DriveLetter, device.FriendlyName);
                ProcessDevice(device);
                return;
            }

            // Scénář A: DiskDrive přišel první
            _pendingDevices[diskIndex] = (device, DateTime.UtcNow);

            _ = Task.Run(async () =>
            {
                await Task.Delay(PairingTimeoutSeconds * 1000);
                if (_pendingDevices.TryRemove(diskIndex, out var timedOut))
                {
                    _logger.LogWarning(
                        "Drive letter nepřišel do {Sec}s pro {Device} – zpracovávám bez něj",
                        PairingTimeoutSeconds, timedOut.Device.FriendlyName);
                    ProcessDevice(timedOut.Device);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chyba při zpracování DiskDrive connect události");
        }
    }

    // --------------------------------------------------------
    // Callback 2: logický disk připojen
    // --------------------------------------------------------
    private void OnLogicalDiskConnected(object sender, EventArrivedEventArgs e)
    {
        _lastWmiEventAt = DateTime.UtcNow;

        try
        {
            var wmi = e.NewEvent["TargetInstance"] as ManagementBaseObject;
            if (wmi == null) return;

            var driveType = int.Parse(wmi["DriveType"]?.ToString() ?? "0");
            if (driveType != 2 && driveType != 3) return;

            var driveLetter = wmi["DeviceID"]?.ToString()?.Replace(":", "").Trim() ?? string.Empty;
            var diskIndex   = GetDiskIndexForLogicalDisk(wmi["DeviceID"]?.ToString() ?? string.Empty);

            _logger.LogInformation("Nový logický disk: {Letter}: (DiskIndex={Index})",
                driveLetter, diskIndex);

            if (diskIndex < 0) return;

            // Scénář A: DiskDrive čekal
            if (_pendingDevices.TryRemove(diskIndex, out var pending))
            {
                pending.Device.DriveLetters.Add(driveLetter);
                _logger.LogInformation(
                    "Spárováno (DiskDrive čekal): drive letter {Letter}: → {Device}",
                    driveLetter, pending.Device.FriendlyName);
                ProcessDevice(pending.Device);
                return;
            }

            // Scénář B: LogicalDisk přišel první
            _logger.LogDebug(
                "LogicalDisk {Letter}: přišel před DiskDrive (DiskIndex={Index}) – čekám na DiskDrive",
                driveLetter, diskIndex);

            _pendingDriveLetters[diskIndex] = (driveLetter, DateTime.UtcNow);

            _ = Task.Run(async () =>
            {
                await Task.Delay(PairingTimeoutSeconds * 1000);
                if (_pendingDriveLetters.TryRemove(diskIndex, out _))
                {
                    _logger.LogDebug(
                        "DiskDrive nepřišel do {Sec}s pro drive letter {Letter}: – ignoruji",
                        PairingTimeoutSeconds, driveLetter);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chyba při zpracování LogicalDisk události");
        }
    }

    // --------------------------------------------------------
    // Callback 3: fyzický disk odpojen
    // Najde aktivní spojení a doplní DisconnectedAt
    // --------------------------------------------------------
    private void OnDiskDisconnected(object sender, EventArrivedEventArgs e)
    {
        _lastWmiEventAt = DateTime.UtcNow;

        try
        {
            var wmi = e.NewEvent["TargetInstance"] as ManagementBaseObject;
            if (wmi == null) return;

            if (!IsRemovableMedia(wmi)) return;

            var pnpId        = wmi["PNPDeviceID"]?.ToString() ?? string.Empty;
            var friendlyName = wmi["Caption"]?.ToString() ?? "Neznámé zařízení";
            var disconnectAt = DateTime.UtcNow;

            _logger.LogInformation("Médium odpojeno: {Name} ({PnpId})", friendlyName, pnpId);

            // Najít aktivní spojení podle PnpDeviceId
            if (_activeConnections.TryRemove(pnpId, out var active))
            {
                var duration = disconnectAt - active.ConnectedAt;
                _logger.LogInformation(
                    "Doba připojení {Name}: {Duration:mm\\:ss} (min:sec)",
                    active.FriendlyName, duration);

                // Zapsat DisconnectedAt do lokálního JSON
                _incidentLogger.UpdateDisconnectedAt(pnpId, active.ConnectedAt, disconnectAt);
            }
            else
            {
                // Médium nebylo v aktivních (restart agenta, nepovolené médium bez záznamu)
                _logger.LogDebug(
                    "Odpojeno médium bez aktivního záznamu: {PnpId}", pnpId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chyba při zpracování DiskDrive disconnect události");
        }
    }

    // --------------------------------------------------------
    // Zpracování zařízení – whitelist + policy + log + registrace aktivního spojení
    // --------------------------------------------------------
    private void ProcessDevice(DeviceInfo device)
    {
        var wlStatus  = _whitelistChecker.GetStatus();
        var isAllowed = _whitelistChecker.IsAllowed(device);
        var connectedAt = DateTime.UtcNow;

        // Logujeme VŠE – povolená i nepovolená (kompletní audit trail)
        _policyEnforcer.HandleDevice(
            device,
            _whitelistChecker.GetVersion(),
            isAllowed,
            wlStatus);

        if (isAllowed)
            _logger.LogInformation("Médium povoleno: {Device}", device);

        // Zaregistrovat do aktivních spojení pro budoucí disconnect tracking
        if (!string.IsNullOrEmpty(device.PnpDeviceId))
        {
            _activeConnections[device.PnpDeviceId] = (device.FriendlyName, connectedAt);
            _logger.LogDebug(
                "Aktivní spojení registrováno: {PnpId}", device.PnpDeviceId);
        }
    }

    // --------------------------------------------------------
    // Zjistí DiskIndex pro logický disk přes Win32_LogicalDiskToPartition
    // --------------------------------------------------------
    private int GetDiskIndexForLogicalDisk(string deviceId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\cimv2",
                $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{deviceId}'}} " +
                "WHERE AssocClass=Win32_LogicalDiskToPartition");

            foreach (ManagementBaseObject partition in searcher.Get())
            {
                var diskIndexVal = partition["DiskIndex"];
                if (diskIndexVal != null)
                    return Convert.ToInt32(diskIndexVal);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nelze zjistit DiskIndex pro {DeviceId}", deviceId);
        }

        return -1;
    }

    // --------------------------------------------------------
    // Parsování WMI objektu na model DeviceInfo
    // --------------------------------------------------------
    private DeviceInfo ParseDeviceFromWmi(ManagementBaseObject wmi)
    {
        var pnpId = wmi["PNPDeviceID"]?.ToString() ?? string.Empty;

        var device = new DeviceInfo
        {
            FriendlyName     = wmi["Caption"]?.ToString() ?? "Neznámé zařízení",
            SerialNumber     = wmi["SerialNumber"]?.ToString() ?? ExtractSerialFromPnp(pnpId),
            FirmwareRevision = wmi["FirmwareRevision"]?.ToString() ?? string.Empty,
            SizeBytes        = long.TryParse(wmi["Size"]?.ToString(), out var size) ? size : 0,
            PnpDeviceId      = pnpId,
        };

        ExtractVidPid(pnpId, device);

        var interfaceType = wmi["InterfaceType"]?.ToString() ?? string.Empty;
        var mediaType     = wmi["MediaType"]?.ToString()    ?? string.Empty;
        device.Type       = DetermineDeviceType(interfaceType, mediaType);

        return device;
    }

    private bool IsRemovableMedia(ManagementBaseObject wmi)
    {
        var interfaceType = wmi["InterfaceType"]?.ToString() ?? string.Empty;
        return interfaceType.Equals("USB", StringComparison.OrdinalIgnoreCase)
            || interfaceType.Equals("SD",  StringComparison.OrdinalIgnoreCase);
    }

    private void ExtractVidPid(string pnpId, DeviceInfo device)
    {
        var parts = pnpId.Split('\\', '&');

        foreach (var part in parts)
        {
            if (part.StartsWith("VID_",  StringComparison.OrdinalIgnoreCase))
                device.VendorId = part.Substring(4);
            if (part.StartsWith("PID_",  StringComparison.OrdinalIgnoreCase))
                device.ProductId = part.Substring(4);
            if (part.StartsWith("VEN_",  StringComparison.OrdinalIgnoreCase))
                device.VendorId = part.Substring(4);
            if (part.StartsWith("PROD_", StringComparison.OrdinalIgnoreCase))
                device.ProductId = part.Substring(5);
            if (part.StartsWith("REV_",  StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(device.FirmwareRevision))
                device.FirmwareRevision = part.Substring(4);
        }
    }

    private int ExtractDiskIndex(string deviceId)
    {
        var digits = new string(deviceId.Reverse()
            .TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out var index) ? index : -1;
    }

    private string ExtractSerialFromPnp(string pnpId)
    {
        var lastSegment = pnpId.Split('\\').LastOrDefault() ?? string.Empty;
        var ampIdx      = lastSegment.IndexOf('&');
        return ampIdx > 0 ? lastSegment[..ampIdx] : lastSegment;
    }

    private DeviceType DetermineDeviceType(string interfaceType, string mediaType) =>
        interfaceType.ToUpper() switch
        {
            "USB" when mediaType.Contains("Removable") => DeviceType.UsbFlashDrive,
            "USB" when mediaType.Contains("Fixed")     => DeviceType.UsbHdd,
            "SD"                                       => DeviceType.SdCard,
            _                                          => DeviceType.Unknown
        };
}
