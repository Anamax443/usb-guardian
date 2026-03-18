// ============================================================
// ToastMessage.cs
// Datový model zprávy ve frontě Toast notifikací.
//
// Agent (SYSTEM) zapíše JSON soubor do toast-queue\.
// ToastHelper přečte soubor, zobrazí Toast a smaže soubor.
// ============================================================

namespace USBGuardian.ToastHelper;

/// <summary>
/// Zpráva uložená agentem do toast fronty.
/// Serializována jako JSON soubor v C:\ProgramData\USBGuardian\toast-queue\
/// </summary>
public class ToastMessage
{
    /// <summary>Čas detekce zařízení (UTC).</summary>
    public DateTime DetectedAt { get; set; }

    /// <summary>Název zařízení, např. "Kingston DataTraveler 3.0 USB Device".</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>Kapacita zařízení, např. "28,9 GB".</summary>
    public string DeviceSize { get; set; } = string.Empty;

    /// <summary>Přihlášený uživatel v době detekce, např. "trnkam".</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Název počítače, např. "TRNKAMW11".</summary>
    public string MachineName { get; set; } = string.Empty;

    /// <summary>Výsledná akce: "Warned" nebo "Blocked".</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Kontaktní zpráva pro uživatele, např. "Kontaktujte IT oddělení".</summary>
    public string ContactMessage { get; set; } = "Kontaktujte IT oddeleni";
}
