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
    private readonly string _blockedPath;
    private readonly object _lock = new();
    // Co agent SÁM zakázal (perzistované): PnpDeviceID → klíč VID:PID:SN (pro reconciliaci s whitelistem).
    // Jen tato média vrací reconcile, ne disky zakázané někým jiným.
    private readonly Dictionary<string, string> _blocked = new(StringComparer.OrdinalIgnoreCase);

    public DeviceBlocker(ILogger<DeviceBlocker> logger,
        string blockedPath = @"C:\ProgramData\USBGuardian\blocked.json")
    {
        _logger      = logger;
        _blockedPath = blockedPath;
        LoadBlocked();
    }

    // --------------------------------------------------------
    // Zablokuje zařízení přes PNPDeviceID
    // Příklad: USBSTOR\DISK&VEN_SANDISK&PROD_CRUZER_FORCE\4C530000...
    // --------------------------------------------------------
    public BlockResult BlockDevice(string pnpDeviceId, string deviceKey = "")
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
            TrackBlocked(pnpDeviceId, deviceKey);   // zapamatovat (+ klíč pro reconciliaci s whitelistem)
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
        {
            _logger.LogInformation("Zařízení POVOLENO: {PnpId}", pnpDeviceId);
            Untrack(pnpDeviceId);
        }
        else
            _logger.LogWarning("Nelze povolit zařízení: {PnpId}", pnpDeviceId);

        return success;
    }

    // --------------------------------------------------------
    // Vrátí VŠECHNA média, která agent sám zakázal (při vypnutí blokování / break-glass / enforce=false).
    // Idempotentní: po vrácení je seznam prázdný → další volání nic nedělají.
    // --------------------------------------------------------
    public int UnblockAll()
    {
        string[] ids;
        lock (_lock) { ids = _blocked.Keys.ToArray(); }
        if (ids.Length == 0) return 0;

        _logger.LogWarning("Vypnuté blokování → vracím {Count} dříve zablokovaných médií", ids.Length);
        var done = 0;
        foreach (var id in ids)
            if (UnblockDevice(id)) done++;   // UnblockDevice si sám odebere z _blocked
        return done;
    }

    /// <summary>Snapshot zablokovaných (PnpDeviceID → klíč VID:PID:SN) pro reconciliaci s whitelistem.</summary>
    public IReadOnlyDictionary<string, string> GetBlocked()
    {
        lock (_lock) { return new Dictionary<string, string>(_blocked, StringComparer.OrdinalIgnoreCase); }
    }

    private void TrackBlocked(string pnpId, string deviceKey)
    {
        lock (_lock) { _blocked[pnpId] = deviceKey ?? string.Empty; SaveBlocked(); }
    }

    private void Untrack(string pnpId)
    {
        lock (_lock) { if (_blocked.Remove(pnpId)) SaveBlocked(); }
    }

    private void LoadBlocked()
    {
        try
        {
            if (!File.Exists(_blockedPath)) return;
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_blockedPath));
            if (map != null) foreach (var kv in map) _blocked[kv.Key] = kv.Value;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Nelze načíst seznam zablokovaných médií"); }
    }

    private void SaveBlocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_blockedPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_blockedPath, System.Text.Json.JsonSerializer.Serialize(_blocked));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Nelze uložit seznam zablokovaných médií"); }
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
