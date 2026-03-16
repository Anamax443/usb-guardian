// ============================================================
// DeviceMonitor.cs
// Sleduje připojení paměťových médií přes WMI události.
// WMI = Windows Management Instrumentation – vestavěno v každém Windows.
// Spouští se jako BackgroundService (část Windows Service).
// ============================================================

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

    // WMI watchers – jeden pro připojení, pro budoucí potřebu i odpojení
    private ManagementEventWatcher? _connectWatcher;

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
    // Spuštění monitoringu (volá se při startu Windows Service)
    // --------------------------------------------------------
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("USB Guardian spuštěn – zahajuji monitoring zařízení");

        StartWmiWatcher();

        // Čekáme dokud není service zastaven
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            // Normální ukončení při StopAsync
        }
        finally
        {
            StopWmiWatcher();
            _logger.LogInformation("USB Guardian zastaven");
        }
    }

    // --------------------------------------------------------
    // Inicializace WMI event watcheru
    // Sleduje Win32_USBHub a Win32_DiskDrive pro detekci médií
    // --------------------------------------------------------
    private void StartWmiWatcher()
    {
        try
        {
            // WQL dotaz: sleduj připojení jakéhokoli USB zařízení
            // __InstanceCreationEvent = nová instance WMI objektu = nové zařízení
            var query = new WqlEventQuery(
                "__InstanceCreationEvent",
                TimeSpan.FromSeconds(2),     // polling interval
                "TargetInstance ISA 'Win32_DiskDrive'");

            _connectWatcher = new ManagementEventWatcher(query);
            _connectWatcher.EventArrived += OnDeviceConnected;
            _connectWatcher.Start();

            _logger.LogInformation("WMI watcher spuštěn (Win32_DiskDrive)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chyba při spuštění WMI watcheru – monitoring nebude fungovat");
        }
    }

    private void StopWmiWatcher()
    {
        try
        {
            _connectWatcher?.Stop();
            _connectWatcher?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chyba při zastavování WMI watcheru");
        }
    }

    // --------------------------------------------------------
    // Callback – zavolá se při každém připojení zařízení
    // --------------------------------------------------------
    private void OnDeviceConnected(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var targetInstance = e.NewEvent["TargetInstance"] as ManagementBaseObject;
            if (targetInstance == null) return;

            // Přečteme vlastnosti WMI objektu Win32_DiskDrive
            var device = ParseDeviceFromWmi(targetInstance);

            // Přeskočíme interní disky (interface != USB)
            if (!IsRemovableMedia(targetInstance))
            {
                _logger.LogDebug("Přeskočeno interní zařízení: {Device}", device.FriendlyName);
                return;
            }

            _logger.LogInformation("Detekováno médium: {Device}", device);

            // Ověření proti whitelistu
            var wlStatus  = _whitelistChecker.GetStatus();
            var isAllowed = _whitelistChecker.IsAllowed(device);

            if (!isAllowed)
            {
                // Nepovolené médium → PolicyEnforcer rozhodne co dál
                _policyEnforcer.HandleUnauthorizedDevice(
                    device,
                    _whitelistChecker.GetVersion(),
                    wlStatus);
            }
            else
            {
                _logger.LogInformation("Médium povoleno: {Device}", device);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chyba při zpracování události připojení zařízení");
        }
    }

    // --------------------------------------------------------
    // Parsování WMI objektu na náš model DeviceInfo
    // --------------------------------------------------------
    private DeviceInfo ParseDeviceFromWmi(ManagementBaseObject wmi)
    {
        // PNPDeviceID může mít dva formáty:
        //   USB\VID_0951&PID_1666\...                          → klasický USB
        //   USBSTOR\DISK&VEN_KINGSTON&PROD_DATATRAVELER_2.0\... → storage formát
        var pnpId = wmi["PNPDeviceID"]?.ToString() ?? string.Empty;

        var device = new DeviceInfo
        {
            FriendlyName     = wmi["Caption"]?.ToString() ?? "Neznámé zařízení",
            SerialNumber     = wmi["SerialNumber"]?.ToString() ?? ExtractSerialFromPnp(pnpId),
            FirmwareRevision = wmi["FirmwareRevision"]?.ToString() ?? string.Empty,
            SizeBytes        = long.TryParse(wmi["Size"]?.ToString(), out var size) ? size : 0,
        };

        // Extrakce VID/PID nebo VEN/PROD z PNPDeviceID
        ExtractVidPid(pnpId, device);

        // Určení typu média dle InterfaceType + MediaType
        var interfaceType = wmi["InterfaceType"]?.ToString() ?? string.Empty;
        var mediaType     = wmi["MediaType"]?.ToString() ?? string.Empty;
        device.Type       = DetermineDeviceType(interfaceType, mediaType);

        // Zjistíme drive letters přiřazené tomuto médiu (F:, G: atd.)
        var deviceId = wmi["DeviceID"]?.ToString() ?? string.Empty;
        device.DriveLetters = GetDriveLetters(deviceId);

        return device;
    }

    // --------------------------------------------------------
    // Zjistí drive letters pro dané fyzické zařízení
    // Cesta WMI: Win32_DiskDrive → Win32_DiskPartition → Win32_LogicalDisk
    // --------------------------------------------------------
    private List<string> GetDriveLetters(string deviceId)
    {
        var letters = new List<string>();

        try
        {
            // Escapování zpětných lomítek pro WMI dotaz
            var escapedId = deviceId.Replace(@"\", @"\\");

            using var diskQuery = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{escapedId}'}} " +
                "WHERE AssocClass=Win32_DiskDriveToDiskPartition");

            foreach (ManagementObject partition in diskQuery.Get())
            {
                using var logicalQuery = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} " +
                    "WHERE AssocClass=Win32_LogicalDiskToPartition");

                foreach (ManagementObject logical in logicalQuery.Get())
                {
                    var name = logical["Name"]?.ToString();
                    if (!string.IsNullOrEmpty(name))
                    {
                        // Uložíme jen písmeno bez dvojtečky (např. "F" ne "F:")
                        letters.Add(name.Replace(":", "").Trim());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nelze zjistit drive letters pro {DeviceId}", deviceId);
        }

        return letters;
    }

    // --------------------------------------------------------
    // Filtr: přeskočit interní disky (SATA, SCSI, NVMe)
    // --------------------------------------------------------
    private bool IsRemovableMedia(ManagementBaseObject wmi)
    {
        var interfaceType = wmi["InterfaceType"]?.ToString() ?? string.Empty;

        // Zajímají nás pouze USB zařízení
        // SD karty přes vestavěnou čtečku se mohou zobrazit jako "SD" nebo "USB"
        return interfaceType.Equals("USB", StringComparison.OrdinalIgnoreCase)
            || interfaceType.Equals("SD",  StringComparison.OrdinalIgnoreCase);
    }

    // --------------------------------------------------------
    // Extrakce VID/PID nebo VEN/PROD z PNPDeviceID řetězce
    // Podporuje dva formáty Windows:
    //   USB\VID_0951&PID_1666\...                           → klasický USB (hex ID)
    //   USBSTOR\DISK&VEN_KINGSTON&PROD_DATATRAVELER_2.0\... → storage (textový název)
    // --------------------------------------------------------
    private void ExtractVidPid(string pnpId, DeviceInfo device)
    {
        var parts = pnpId.Split('\\', '&');

        foreach (var part in parts)
        {
            // Klasický USB formát (hex)
            if (part.StartsWith("VID_", StringComparison.OrdinalIgnoreCase))
                device.VendorId = part.Substring(4);

            if (part.StartsWith("PID_", StringComparison.OrdinalIgnoreCase))
                device.ProductId = part.Substring(4);

            // USBSTOR formát (textový název výrobce/produktu)
            if (part.StartsWith("VEN_", StringComparison.OrdinalIgnoreCase))
                device.VendorId = part.Substring(4);

            if (part.StartsWith("PROD_", StringComparison.OrdinalIgnoreCase))
                device.ProductId = part.Substring(5);

            // REV = firmware revision záloha (pokud WMI nevrátí FirmwareRevision)
            if (part.StartsWith("REV_", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(device.FirmwareRevision))
                device.FirmwareRevision = part.Substring(4);
        }
    }

    // --------------------------------------------------------
    // Záložní extrakce sériového čísla z PNPDeviceID
    // --------------------------------------------------------
    private string ExtractSerialFromPnp(string pnpId)
    {
        // Sériové číslo je typicky poslední segment po posledním '\'
        var lastSegment = pnpId.Split('\\').LastOrDefault() ?? string.Empty;

        // Odstraníme "&0" nebo podobné sufixy
        var ampIdx = lastSegment.IndexOf('&');
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
