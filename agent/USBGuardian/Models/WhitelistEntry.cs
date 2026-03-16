// ============================================================
// WhitelistEntry.cs
// Jeden záznam v whitelistu = schválené paměťové médium.
// ============================================================

namespace USBGuardian.Models;

/// <summary>
/// Schválené paměťové médium evidované IT oddělením.
/// </summary>
public class WhitelistEntry
{
    /// <summary>Vendor ID zařízení</summary>
    public string VendorId { get; set; } = string.Empty;

    /// <summary>Product ID zařízení</summary>
    public string ProductId { get; set; } = string.Empty;

    /// <summary>
    /// Sériové číslo – pokud prázdné, platí pro VŠECHNA
    /// zařízení daného VID+PID (pozor na bezpečnostní riziko)
    /// </summary>
    public string SerialNumber { get; set; } = string.Empty;

    /// <summary>Popisek pro IT admina (kdo, co, proč)</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Datum přidání do whitelistu</summary>
    public DateTime ApprovedAt { get; set; }

    /// <summary>Kdo schválil</summary>
    public string ApprovedBy { get; set; } = string.Empty;

    /// <summary>
    /// Datum expirace záznamu.
    /// NULL = trvalé schválení.
    /// Datum = dočasné schválení (dodavatel, návštěva, zkušební provoz).
    /// Po expiraci médium přestane fungovat bez nutnosti ručního odebrání.
    /// ISO 27001 A.7.10 – řízení životního cyklu médií.
    /// </summary>
    public DateTime? ValidUntil { get; set; } = null;

    /// <summary>
    /// Vypočítaný klíč pro rychlé porovnání s DeviceInfo.UniqueKey
    /// </summary>
    public string UniqueKey =>
        $"{VendorId.ToUpper()}:{ProductId.ToUpper()}:{SerialNumber.ToUpper()}";
}

/// <summary>
/// Kořenový objekt whitelist souboru (whitelist.json)
/// </summary>
public class WhitelistFile
{
    /// <summary>Verze whitelistu – datum + iterace (např. "2026-03-16-v1")</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Datum vydání</summary>
    public DateTime IssuedAt { get; set; }

    /// <summary>Datum expirace – po tomto datu přejde agent do degraded módu</summary>
    public DateTime ValidUntil { get; set; }

    /// <summary>Digitální podpis (Fáze 3 – zatím prázdný)</summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>Seznam schválených zařízení</summary>
    public List<WhitelistEntry> Devices { get; set; } = new();
}
