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

        // Pro přesnou shodu escapujeme jen apostrof; pro -like fallback i ampersand (wildcard
        // engine). Nejdřív přesně (stejný vzor jako UnblockDevice) - substring -like sám o sobě
        // by mohl zachytit i JINÉ připojené zařízení, jehož InstanceId tenhle řetězec náhodou
        // obsahuje (VID/PID/sériové číslo si USB zařízení může nastavit libovolně), a zablokovat
        // tak něco jiného, než bylo zamýšleno. Wildcard zůstává jen jako fallback, kdyby se
        // InstanceId v PnP stromu mírně lišilo od WMI PNPDeviceID.
        var exactId = pnpDeviceId.Replace("'", "''");
        var likeId  = exactId.Replace("&", "`&");

        // Disable-PnpDevice obalen v try/catch (-ErrorAction Stop): bez toho je výchozí
        // $ErrorActionPreference 'Continue', takže nedokončující chyba (zařízení nejde
        // deaktivovat) nezastaví skript a "BLOCKED" se vypíše i po SELHÁNÍ Disable-PnpDevice.
        // Stejný nález/oprava jako dřív u UnblockDevice (§8.4 HANDOFF) - tam BLOCKED/FAILED,
        // tady stejný vzor.
        var script = $@"
            $device = Get-PnpDevice -InstanceId '{exactId}'
            if (-not $device) {{ $device = Get-PnpDevice | Where-Object {{ $_.InstanceId -like '*{likeId}*' }} }}
            if ($device) {{
                try {{
                    Disable-PnpDevice -InstanceId $device.InstanceId -Confirm:$false -ErrorAction Stop
                    Write-Output ('BLOCKED:' + $device.InstanceId)
                }} catch {{
                    Write-Output ('FAILED:' + $_.Exception.Message)
                }}
            }} else {{
                Write-Output 'NOT_FOUND'
            }}
        ";

        var result = RunPowerShell(script);
        var blockResult = InterpretBlockOutput(result, pnpDeviceId);

        if (blockResult.IsSuccess)
        {
            _logger.LogWarning("Zařízení DEAKTIVOVÁNO: {PnpId}", pnpDeviceId);
            TrackBlocked(pnpDeviceId, deviceKey);   // zapamatovat (+ klíč pro reconciliaci s whitelistem)
        }
        else if (result.Contains("NOT_FOUND"))
        {
            _logger.LogWarning("Zařízení nenalezeno v PnpDevice: {PnpId}", pnpDeviceId);
        }
        else
        {
            _logger.LogError("Blokování selhalo pro {PnpId}: {Error}", pnpDeviceId, blockResult.ErrorMessage);
        }

        return blockResult;
    }

    // --------------------------------------------------------
    // Čistá interpretace výstupu blokovacího skriptu (bez I/O, bez vedlejsich efektu) -
    // testovatelná odděleně od RunPowerShell/TrackBlocked.
    // --------------------------------------------------------
    internal static BlockResult InterpretBlockOutput(string output, string pnpDeviceId)
    {
        if (output.Contains("BLOCKED"))
            return BlockResult.Success(pnpDeviceId);
        if (output.Contains("NOT_FOUND"))
            return BlockResult.Failed("Zařízení nenalezeno");
        if (output.Contains("FAILED"))
            return BlockResult.Failed($"Disable-PnpDevice selhalo: {output.Trim()}");
        return BlockResult.Failed($"PowerShell chyba: {output}");
    }

    // --------------------------------------------------------
    // Odblokuje zařízení (pro budoucí override kód od IT)
    // --------------------------------------------------------
    public bool UnblockDevice(string pnpDeviceId)
    {
        // Pro přesnou shodu (jako ruční `Enable-PnpDevice -InstanceId '...'`) escapujeme jen apostrof;
        // pro -like fallback i ampersand (wildcard engine). Nejdřív přesně, pak fallback – ať Enable
        // proběhne, i kdyby se InstanceId v PnP stromu mírně lišil od WMI PNPDeviceID.
        var exactId = pnpDeviceId.Replace("'", "''");
        var likeId  = exactId.Replace("&", "`&");

        var script = $@"
            $ErrorActionPreference = 'SilentlyContinue'
            $device = Get-PnpDevice -InstanceId '{exactId}'
            if (-not $device) {{ $device = Get-PnpDevice | Where-Object {{ $_.InstanceId -like '*{likeId}*' }} }}
            if ($device) {{
                try {{
                    Enable-PnpDevice -InstanceId $device.InstanceId -Confirm:$false -ErrorAction Stop
                    Write-Output 'ENABLED'
                }} catch {{
                    Write-Output ('FAILED:' + $_.Exception.Message)
                }}
            }} else {{
                Write-Output 'GONE'
            }}
        ";

        var result = RunPowerShell(script);

        if (result.Contains("ENABLED"))
        {
            _logger.LogInformation("Zařízení POVOLENO: {PnpId}", pnpDeviceId);
            Untrack(pnpDeviceId);
            return true;
        }

        // Médium už není v systému (odpojené) → ber jako vyřešené a odeber ze seznamu blokovaných,
        // ať nezůstane viset napořád. Při příštím připojení se vyhodnotí znovu dle aktuální politiky.
        if (result.Contains("GONE"))
        {
            _logger.LogInformation("Zařízení {PnpId} už není připojené – odebírám ze seznamu blokovaných", pnpDeviceId);
            Untrack(pnpDeviceId);
            return true;
        }

        // Skutečné selhání Enable (necháváme v seznamu → příští reconcile zkusí znovu).
        _logger.LogWarning("Nelze povolit zařízení {PnpId}: {Out}", pnpDeviceId, result.Trim());
        return false;
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
        _logger.LogWarning("Odblokování dokončeno: vráceno {Done} z {Count} médií", done, ids.Length);
        return done;
    }

    /// <summary>Počet médií, která agent aktuálně drží zablokovaná (pro lokální konzoli).</summary>
    public int BlockedCount { get { lock (_lock) return _blocked.Count; } }

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

    private const int DefaultTimeoutMs = 10_000;

    // --------------------------------------------------------
    // Interní: spuštění PowerShell skriptu
    // --------------------------------------------------------
    private string RunPowerShell(string script) => RunPowerShell(script, DefaultTimeoutMs);

    // timeoutMs jako parametr kvůli testovatelnosti (test nemůže čekat 10 reálných sekund
    // na ověření, že zaseknutý proces skutečně zabijeme) - volající kód v tomhle souboru vždy
    // jede s výchozím DefaultTimeoutMs.
    internal string RunPowerShell(string script, int timeoutMs)
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

            // Nález z oponentury 11.09.2026: dřív se čekalo synchronně na ReadToEnd() PŘED
            // WaitForExit(10_000) - tím pádem timeout níže nikdy nic reálně neomezoval
            // (ReadToEnd zůstane viset, dokud dítě nezavře handle, tj. dokud neskončí samo)
            // a zaseknutý powershell.exe blokoval Block/UnblockDevice navěky. Čteme proto
            // asynchronně HNED po startu (obě roury zároveň, žádný sync blok na jedné z nich
            // dřív než na druhé - klasický pipe-buffer deadlock), takže WaitForExit(timeoutMs)
            // je konečně skutečný strop.
            var stdOutTask = proc.StandardOutput.ReadToEndAsync();
            var stdErrTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(timeoutMs))
            {
                _logger.LogError(
                    "PowerShell (PID {Pid}) překročil timeout {TimeoutMs} ms – zabíjím proces (i případné děti)",
                    proc.Id, timeoutMs);
                try { proc.Kill(entireProcessTree: true); }
                catch (Exception killEx)
                {
                    _logger.LogWarning(killEx, "Nelze zabít zaseknutý PowerShell proces {Pid}", proc.Id);
                }
                return string.Empty;
            }

            var output = stdOutTask.GetAwaiter().GetResult();
            var error  = stdErrTask.GetAwaiter().GetResult();

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
