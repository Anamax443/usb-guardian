// ============================================================
// Incident.cs
// Model pro záznam bezpečnostního incidentu.
// Ukládá se do lokální SQLite DB a odesílá na server (Fáze 3).
// ============================================================

using USBGuardian.Security;

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

    /// <summary>Reálný přihlášený uživatel (WTS aktivní session) – agent běží jako SYSTEM,
    /// proto NE Environment.UserName (= strojový účet HOST$). Viz <see cref="SessionUser"/>.</summary>
    public string Username { get; set; } = SessionUser.GetActiveConsoleUser();

    /// <summary>Zařízení které incident způsobilo</summary>
    public DeviceInfo Device { get; set; } = new();

    /// <summary>Akce která byla provedena</summary>
    public IncidentAction Action { get; set; } = IncidentAction.Warned;

    /// <summary>Verze whitelistu platná v době incidentu</summary>
    public string WhitelistVersion { get; set; } = string.Empty;

    /// <summary>Zda byl incident odeslán na centrální server</summary>
    public bool SentToServer { get; set; } = false;
}

/// <summary>Typ akce provedené při připojení média</summary>
public enum IncidentAction
{
    /// <summary>Médium povoleno – je na whitelistu</summary>
    Allowed,

    /// <summary>Uživatel byl varován, médium funguje</summary>
    Warned,

    /// <summary>Přístup k médiu byl zablokován</summary>
    Blocked,

    /// <summary>Dočasně povoleno override kódem od IT (Fáze 3)</summary>
    TemporarilyAllowed,

    /// <summary>Lokální admin dočasně VYPNUL blokování (break-glass, offline). Auditní událost → server.</summary>
    OverrideDisabled
}
