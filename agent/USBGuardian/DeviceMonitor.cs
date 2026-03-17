// ============================================================
// DeviceMonitor.cs
// Sleduje připojení paměťových médií přes dva WMI watchers:
//   1. Win32_DiskDrive  – fyzický disk (VID, PID, Serial, kapacita)
//   2. Win32_LogicalDisk – drive letter (F:, G: atd.)
// Korelace: diskIndex z DiskDrive → DiskIndex v LogicalDisk
//
// v1.1 – přidán WMI Watchdog:
//   - Každých 5 minut ověří živost WMI subscriptions testovacím dotazem
//   - Při selhání automaticky re-registruje oba watchers
//
// v1.2 – fix obousměrného timingu:
//   - WMI eventy přicházejí v nepředvídatelném pořadí
//   - DiskDrive může přijít před i po LogicalDisk eventu
//   - _pendingDevices     – DiskDrive přišel první, čeká na LogicalDisk
//   - _pendingDriveLetters – LogicalDisk přišel první, čeká na DiskDrive
//   - Timeout prodloužen z 10s na 30s
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

    // Dva WMI watchers
    private ManagementEventWatcher? _diskWatcher;
    private ManagementEventWatcher? _logicalWatcher;

    // Scénář A: DiskDrive přišel první → čeká na drive letter
    // Key = diskIndex, Value = (DeviceInfo, čas detekce)
    private readonly ConcurrentDictionary<int, (DeviceInfo Device, DateTime DetectedAt)>
        _pendingDevices = new();

    // Scénář B: LogicalDisk přišel první → čeká na DiskDrive
    // Key = diskIndex, Value = (driveLetter, čas detekce)
    private readonly ConcurrentDictionary<int, (string DriveLetter, DateTime DetectedAt)>
        _pendingDriveLetters = new();

    // Jak dlouho čekat na spárování (WMI eventy mohou mít velký rozestup)
    private const int PairingTimeoutSeconds = 30;

    // Watchdog – hlídá živost WMI subscriptions
    private Timer? _watchdogTimer;
    private DateTime _lastWmiEventAt = DateTime.UtcNow;
    private const int WatchdogIntervalSeconds = 300; // kontrola každých 5 minut

    public DeviceMonitor(
        ILogger<DeviceMonitor> logger,
        WhitelistChecker whitelistChecker,
        PolicyEnforcer policyEnforcer)
    {
        _logger           = logger;
        _whitelistChecker = whitelistChecker;
        _policyEnforcer   = policyEnforcer;
    }

    // --------------------------------------------------------
    // Spuštění monitoringu
    // --------------------------------------------------------
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("USB Guardian spuštěn – zahajuji monitoring zařízení");

        StartDiskWatcher();
        StartLogicalDiskWatcher();
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
    // Watcher 1: fyzický disk (Win32_DiskDrive)
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

            _logger.LogInformation("WMI watcher spuštěn (Win32_DiskDrive)");
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
            // Testovací WMI dotaz – pokud WMI pipe žije, projde rychle (~ms)
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
        try { _diskWatcher?.Stop();    _diskWatcher?.Dispose();    } catch { }
        try { _logicalWatcher?.Stop(); _logicalWatcher?.Dispose(); } catch { }

        try
        {
            StartDiskWatcher();
            StartLogicalDiskWatcher();
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
        try { _watchdogTimer?.Dispose(); } catch { }
        try { _diskWatcher?.Stop();    _diskWatcher?.Dispose();    } catch { }
        try { _logicalWatcher?.Stop(); _logicalWatcher?.Dispose(); } catch { }
    }

    // --------------------------------------------------------
    // Callback 1: fyzický disk připojen
    //
    // Možné scénáře:
    //   A) DiskDrive přišel první → uložit do _pendingDevices, čekat na LogicalDisk
    //   B) LogicalDisk přišel první → najdeme v _pendingDriveLetters, zpracovat rovnou
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
                // Nelze zjistit index → zpracujeme rovnou bez drive letter
                ProcessDevice(device);
                return;
            }

            // Scénář B: LogicalDisk přišel dřív → drive letter už čeká
            if (_pendingDriveLetters.TryRemove(diskIndex, out var pending))
            {
                device.DriveLetters.Add(pending.DriveLetter);
                _logger.LogInformation(
                    "Spárováno (LogicalDisk čekal): drive letter {Letter}: → {Device}",
                    pending.DriveLetter, device.FriendlyName);
                ProcessDevice(device);
                return;
            }

            // Scénář A: DiskDrive přišel první → čekáme na LogicalDisk
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
            _logger.LogError(ex, "Chyba při zpracování DiskDrive události");
        }
    }

    // --------------------------------------------------------
    // Callback 2: logický disk připojen
    //
    // Možné scénáře:
    //   A) DiskDrive přišel první → najdeme v _pendingDevices, zpracovat rovnou
    //   B) LogicalDisk přišel první → uložit do _pendingDriveLetters, čekat na DiskDrive
    // --------------------------------------------------------
    private void OnLogicalDiskConnected(object sender, EventArrivedEventArgs e)
    {
        _lastWmiEventAt = DateTime.UtcNow;

        try
        {
            var wmi = e.NewEvent["TargetInstance"] as ManagementBaseObject;
            if (wmi == null) return;

            // DriveType: 2 = Removable, 3 = Local (HDD/USB HDD)
            var driveType = int.Parse(wmi["DriveType"]?.ToString() ?? "0");
            if (driveType != 2 && driveType != 3) return;

            var driveLetter = wmi["DeviceID"]?.ToString()?.Replace(":", "").Trim() ?? string.Empty;
            var diskIndex   = GetDiskIndexForLogicalDisk(wmi["DeviceID"]?.ToString() ?? string.Empty);

            _logger.LogInformation("Nový logický disk: {Letter}: (DiskIndex={Index})",
                driveLetter, diskIndex);

            if (diskIndex < 0) return; // Nelze spárovat – ignorujeme

            // Scénář A: DiskDrive přišel první → drive letter přiřadíme a zpracujeme
            if (_pendingDevices.TryRemove(diskIndex, out var pending))
            {
                pending.Device.DriveLetters.Add(driveLetter);
                _logger.LogInformation(
                    "Spárováno (DiskDrive čekal): drive letter {Letter}: → {Device}",
                    driveLetter, pending.Device.FriendlyName);
                ProcessDevice(pending.Device);
                return;
            }

            // Scénář B: LogicalDisk přišel první → uložíme a čekáme na DiskDrive
            _logger.LogDebug(
                "LogicalDisk {Letter}: přišel před DiskDrive (DiskIndex={Index}) – čekám na DiskDrive",
                driveLetter, diskIndex);

            _pendingDriveLetters[diskIndex] = (driveLetter, DateTime.UtcNow);

            _ = Task.Run(async () =>
            {
                await Task.Delay(PairingTimeoutSeconds * 1000);
                if (_pendingDriveLetters.TryRemove(diskIndex, out _))
                {
                    // DiskDrive nepřišel – drive letter osiřel, nic nelogujeme
                    // (může jít o interní disk nebo nepodporované zařízení)
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
    // Zpracování zařízení – whitelist + policy + log vždy
    // --------------------------------------------------------
    private void ProcessDevice(DeviceInfo device)
    {
        var wlStatus  = _whitelistChecker.GetStatus();
        var isAllowed = _whitelistChecker.IsAllowed(device);

        // Logujeme VŠE – povolená i nepovolená (kompletní audit trail)
        _policyEnforcer.HandleDevice(
            device,
            _whitelistChecker.GetVersion(),
            isAllowed,
            wlStatus);

        if (isAllowed)
            _logger.LogInformation("Médium povoleno: {Device}", device);
    }

    // --------------------------------------------------------
    // Zjistí DiskIndex pro logický disk (F:) přes Win32_LogicalDiskToPartition
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
        var mediaType     = wmi["MediaType"]?.ToString() ?? string.Empty;
        device.Type       = DetermineDeviceType(interfaceType, mediaType);

        return device;
    }

    // --------------------------------------------------------
    // Filtr: pouze USB / SD zařízení
    // --------------------------------------------------------
    private bool IsRemovableMedia(ManagementBaseObject wmi)
    {
        var interfaceType = wmi["InterfaceType"]?.ToString() ?? string.Empty;
        return interfaceType.Equals("USB", StringComparison.OrdinalIgnoreCase)
            || interfaceType.Equals("SD",  StringComparison.OrdinalIgnoreCase);
    }

    // --------------------------------------------------------
    // Extrakce VID/PID nebo VEN/PROD z PNPDeviceID
    // --------------------------------------------------------
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

    // Extrakce čísla disku z DeviceID: \\.\PHYSICALDRIVE3 → 3
    private int ExtractDiskIndex(string deviceId)
    {
        var digits = new string(deviceId.Reverse()
            .TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, out var index) ? index : -1;
    }

    // Záložní extrakce sériového čísla z PNPDeviceID
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
