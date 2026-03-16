// ============================================================
// DeviceBlocker.cs
// Blokuje přístup k nepovolenému médiu přes PowerShell
// Disable-PnpDevice – deaktivuje zařízení na úrovni driveru.
//
// Výhody oproti IOCTL:
//   - Nevyžaduje drive letter
//   - Funguje okamžitě při detekci
//   - Používá PNPDeviceID které máme vždy k dispozici
//   - Reverzibilní přes Enable-PnpDevice
//
// Vyžaduje: admin práva (Windows Service běží jako SYSTEM)
// ============================================================

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using USBGuardian.Models;

namespace USBGuardian;

public class DeviceBlocker
{
    private readonly ILogger<DeviceBlocker> _logger;

    public DeviceBlocker(ILogger<DeviceBlocker> logger)
    {
        _logger = logger;
    }

    // --------------------------------------------------------
    // Zablokuje zařízení přes PNPDeviceID
    // Příklad: USBSTOR\DISK&VEN_SANDISK&PROD_CRUZER_FORCE\4C530000...
    // --------------------------------------------------------
    public BlockResult BlockDevice(string pnpDeviceId)
    {
        if (string.IsNullOrEmpty(pnpDeviceId))
        {
            _logger.LogWarning("PNPDeviceID je prázdné – nelze zablokovat");
            return BlockResult.Failed("PNPDeviceID není k dispozici");
        }

        _logger.LogInformation("Blokuji zařízení: {PnpId}", pnpDeviceId);

        // Escapujeme PNPDeviceID pro PowerShell
        var escapedId = pnpDeviceId.Replace("'", "''").Replace("&", "`&");

        var script = $@"
            $device = Get-PnpDevice | Where-Object {{ $_.InstanceId -like '*{escapedId}*' }}
            if ($device) {{
                Disable-PnpDevice -InstanceId $device.InstanceId -Confirm:$false
                Write-Output 'BLOCKED:' + $device.InstanceId
            }} else {{
                Write-Output 'NOT_FOUND'
            }}
        ";

        var result = RunPowerShell(script);

        if (result.Contains("BLOCKED"))
        {
            _logger.LogWarning("Zařízení DEAKTIVOVÁNO: {PnpId}", pnpDeviceId);
            return BlockResult.Success(pnpDeviceId);
        }
        else if (result.Contains("NOT_FOUND"))
        {
            _logger.LogWarning("Zařízení nenalezeno v PnpDevice: {PnpId}", pnpDeviceId);
            return BlockResult.Failed("Zařízení nenalezeno");
        }
        else
        {
            _logger.LogError("Neočekávaný výstup PowerShell: {Output}", result);
            return BlockResult.Failed($"PowerShell chyba: {result}");
        }
    }

    // --------------------------------------------------------
    // Odblokuje zařízení (pro budoucí override kód od IT)
    // --------------------------------------------------------
    public bool UnblockDevice(string pnpDeviceId)
    {
        var escapedId = pnpDeviceId.Replace("'", "''").Replace("&", "`&");

        var script = $@"
            $device = Get-PnpDevice | Where-Object {{ $_.InstanceId -like '*{escapedId}*' }}
            if ($device) {{
                Enable-PnpDevice -InstanceId $device.InstanceId -Confirm:$false
                Write-Output 'ENABLED'
            }}
        ";

        var result = RunPowerShell(script);
        var success = result.Contains("ENABLED");

        if (success)
            _logger.LogInformation("Zařízení POVOLENO: {PnpId}", pnpDeviceId);
        else
            _logger.LogWarning("Nelze povolit zařízení: {PnpId}", pnpDeviceId);

        return success;
    }

    // --------------------------------------------------------
    // Interní: spuštění PowerShell skriptu
    // --------------------------------------------------------
    private string RunPowerShell(string script)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = "powershell.exe",
                Arguments              = $"-NoProfile -NonInteractive -Command \"{script}\"",
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true
            };

            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            var error  = proc.StandardError.ReadToEnd();
            proc.WaitForExit(10_000);

            if (!string.IsNullOrEmpty(error))
                _logger.LogDebug("PowerShell stderr: {Error}", error);

            return output;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chyba při spuštění PowerShell");
            return string.Empty;
        }
    }
}

// --------------------------------------------------------
// Výsledek operace blokování
// --------------------------------------------------------
public class BlockResult
{
    public bool    IsSuccess    { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string  PnpDeviceId  { get; private set; } = string.Empty;

    public static BlockResult Success(string pnpId) =>
        new() { IsSuccess = true, PnpDeviceId = pnpId };

    public static BlockResult Failed(string error) =>
        new() { IsSuccess = false, ErrorMessage = error };
}
