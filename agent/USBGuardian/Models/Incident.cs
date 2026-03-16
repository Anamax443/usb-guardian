// ============================================================
// Incident.cs
// Model pro záznam bezpečnostního incidentu.
// Ukládá se do lokální SQLite DB a odesílá na server (Fáze 3).
// ============================================================

namespace USBGuardian.Models;

/// <summary>
/// Jeden bezpečnostní incident = připojení nepovolené ho média.
/// </summary>
public class Incident
{
    /// <summary>Interní ID záznamu v SQLite</summary>
    public int Id { get; set; }

    /// <summary>Čas vzniku incidentu (UTC)</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Název počítače kde k incidentu došlo</summary>
    public string Hostname { get; set; } = Environment.MachineName;

    /// <summary>Přihlášený uživatel Windows</summary>
    public string Username { get; set; } = Environment.UserName;

    /// <summary>Zařízení které incident způsobilo</summary>
    public DeviceInfo Device { get; set; } = new();

    /// <summary>Akce která byla provedena</summary>
    public IncidentAction Action { get; set; } = IncidentAction.Warned;

    /// <summary>Verze whitelistu platná v době incidentu</summary>
    public string WhitelistVersion { get; set; } = string.Empty;

    /// <summary>Zda byl incident odeslán na centrální server</summary>
    public bool SentToServer { get; set; } = false;
}

/// <summary>Typ akce provedené při incidentu</summary>
public enum IncidentAction
{
    /// <summary>Uživatel byl varován, médium funguje</summary>
    Warned,

    /// <summary>Přístup k médiu byl zablokován (Fáze 2)</summary>
    Blocked,

    /// <summary>Dočasně povoleno override kódem od IT (Fáze 2)</summary>
    TemporarilyAllowed
}
