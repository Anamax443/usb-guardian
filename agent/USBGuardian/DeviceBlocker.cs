// ============================================================
// DeviceBlocker.cs
// Blokuje přístup k nepovolenému médiu pomocí DeviceIoControl.
// Uzamkne svazek na úrovni OS – médium je vidět ale nelze
// číst ani zapisovat. Reverzibilní bez odpojení zařízení.
//
// Vyžaduje: Windows Service běžící pod účtem s admin právy
// API:       kernel32.dll – DeviceIoControl, CreateFile
// ============================================================

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using USBGuardian.Models;

namespace USBGuardian;

public class DeviceBlocker
{
    private readonly ILogger<DeviceBlocker> _logger;

    // ── Win32 API konstanty ───────────────────────────────────
    private const uint GENERIC_READ          = 0x80000000;
    private const uint GENERIC_WRITE         = 0x40000000;
    private const uint FILE_SHARE_READ       = 0x00000001;
    private const uint FILE_SHARE_WRITE      = 0x00000002;
    private const uint OPEN_EXISTING         = 3;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    // IOCTL kódy pro práci se svazkem
    private const uint FSCTL_LOCK_VOLUME   = 0x00090018;  // uzamknout svazek
    private const uint FSCTL_DISMOUNT_VOLUME = 0x00090020; // odpojit svazek (flush + lock)
    private const uint IOCTL_STORAGE_EJECT_MEDIA = 0x2D4808; // vysunout médium

    private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

    // ── P/Invoke deklarace ────────────────────────────────────
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public DeviceBlocker(ILogger<DeviceBlocker> logger)
    {
        _logger = logger;
    }

    // --------------------------------------------------------
    // Hlavní metoda – zablokuje médium na zadaném drive letteru
    // Příklad: BlockDrive("F")
    // --------------------------------------------------------
    public BlockResult BlockDrive(string driveLetter)
    {
        var drivePath = $@"\\.\{driveLetter}:";
        _logger.LogInformation("Blokuji médium: {Drive}", drivePath);

        IntPtr handle = INVALID_HANDLE_VALUE;

        try
        {
            // Otevřeme handle na svazek (vyžaduje admin práva)
            handle = CreateFile(
                drivePath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);

            if (handle == INVALID_HANDLE_VALUE)
            {
                var err = Marshal.GetLastWin32Error();
                _logger.LogError("Nelze otevřít handle na {Drive}, Win32 error: {Error}", drivePath, err);
                return BlockResult.Failed($"Nelze otevřít drive handle (error {err})");
            }

            // Krok 1: Dismount – flush bufferů a odpojení souborového systému
            if (!SendIoctl(handle, FSCTL_DISMOUNT_VOLUME, "DISMOUNT"))
            {
                // Dismount selhal – pokusíme se alespoň lock
                _logger.LogWarning("Dismount selhal, pokouším se přímý lock");
            }

            // Krok 2: Lock – uzamkne svazek, žádný proces nemůže číst/zapisovat
            if (!SendIoctl(handle, FSCTL_LOCK_VOLUME, "LOCK"))
            {
                return BlockResult.Failed("Lock svazku selhal – médium je pravděpodobně používáno");
            }

            _logger.LogWarning("Médium {Drive} UZAMČENO – přístup zablokován", driveLetter);
            return BlockResult.Success(handle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chyba při blokování média {Drive}", driveLetter);
            if (handle != INVALID_HANDLE_VALUE) CloseHandle(handle);
            return BlockResult.Failed(ex.Message);
        }
    }

    // --------------------------------------------------------
    // Odblokování média (pro budoucí override kód od IT)
    // --------------------------------------------------------
    public bool UnblockDrive(IntPtr handle, string driveLetter)
    {
        try
        {
            CloseHandle(handle);
            _logger.LogInformation("Médium {Drive} odblokováno", driveLetter);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chyba při odblokování média {Drive}", driveLetter);
            return false;
        }
    }

    // --------------------------------------------------------
    // Interní: odeslání IOCTL příkazu
    // --------------------------------------------------------
    private bool SendIoctl(IntPtr handle, uint ioctl, string name)
    {
        var result = DeviceIoControl(
            handle, ioctl,
            IntPtr.Zero, 0,
            IntPtr.Zero, 0,
            out _, IntPtr.Zero);

        if (!result)
        {
            var err = Marshal.GetLastWin32Error();
            _logger.LogWarning("IOCTL {Name} selhal, Win32 error: {Error}", name, err);
        }

        return result;
    }
}

// --------------------------------------------------------
// Výsledek operace blokování
// --------------------------------------------------------
public class BlockResult
{
    public bool IsSuccess { get; private set; }
    public string? ErrorMessage { get; private set; }

    // Handle zůstane otevřený – uzavření = odblokování
    public IntPtr VolumeHandle { get; private set; }

    public static BlockResult Success(IntPtr handle) =>
        new() { IsSuccess = true, VolumeHandle = handle };

    public static BlockResult Failed(string error) =>
        new() { IsSuccess = false, ErrorMessage = error,
                VolumeHandle = new IntPtr(-1) };
}
