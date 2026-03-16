// ============================================================
// DeviceInfo.cs
// Model reprezentující USB/SD zařízení detekované systémem.
// VID + PID + SerialNumber = unikátní identifikace kusu.
// ============================================================

namespace USBGuardian.Models;

/// <summary>
/// Fyzické paměťové médium připojené k počítači.
/// </summary>
public class DeviceInfo
{
    /// <summary>Vendor ID – výrobce (např. "0951" = Kingston)</summary>
    public string VendorId { get; set; } = string.Empty;

    /// <summary>Product ID – model zařízení</summary>
    public string ProductId { get; set; } = string.Empty;

    /// <summary>Sériové číslo – konkrétní fyzický kus</summary>
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>Čitelný název z Windows (např. "Kingston DataTraveler")</summary>
    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>Typ média</summary>
    public DeviceType Type { get; set; } = DeviceType.Unknown;

    /// <summary>Kapacita média v bajtech (0 = nezjištěno)</summary>
    public long SizeBytes { get; set; } = 0;

    /// <summary>Kapacita v čitelném formátu (např. "15,5 GB")</summary>
    public string SizeFormatted => SizeBytes > 0
        ? $"{SizeBytes / 1_073_741_824.0:F1} GB"
        : "neznámá";

    /// <summary>Verze firmware zařízení</summary>
    public string FirmwareRevision { get; set; } = string.Empty;

    /// <summary>
    /// Písmena disků přiřazená tomuto médiu (např. ["F", "G"])
    /// Zjišťuje se přes WMI Win32_DiskDrive → Win32_DiskPartition → Win32_LogicalDisk
    /// </summary>
    public List<string> DriveLetters { get; set; } = new();

    /// <summary>Čas připojení</summary>
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Unikátní klíč pro porovnání s whitelistem.
    /// Formát: VID:PID:SERIAL (uppercase)
    /// </summary>
    public string UniqueKey =>
        $"{VendorId.ToUpper()}:{ProductId.ToUpper()}:{SerialNumber.ToUpper()}";

    public override string ToString() =>
        $"{FriendlyName} [{UniqueKey}] {SizeFormatted}";
}

public enum DeviceType
{
    Unknown,
    UsbFlashDrive,
    UsbHdd,
    SdCard,
    UsbCdRom
}
