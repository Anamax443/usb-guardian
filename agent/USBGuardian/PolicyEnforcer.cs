// ============================================================
// PolicyEnforcer.cs
// Rozhoduje co se stane když je detekováno nepovolené médium.
// Chování řídí agent.config.json → žádná reinstalace při změně.
// Fáze 1: pouze WARN
// Fáze 2: přidáme BLOCK (zakomentovaná sekce označena)
// ============================================================

using Microsoft.Extensions.Logging;
using USBGuardian.Models;

namespace USBGuardian;

public class PolicyEnforcer
{
    private readonly ILogger<PolicyEnforcer> _logger;
    private readonly NotificationService _notification;
    private readonly IncidentLogger _incidentLogger;
    private readonly string _mode;           // "warn" nebo "block"
    private readonly string _onExpired;      // chování po expiraci whitelistu

    public PolicyEnforcer(
        ILogger<PolicyEnforcer> logger,
        NotificationService notification,
        IncidentLogger incidentLogger,
        string mode,
        string onExpired)
    {
        _logger       = logger;
        _notification = notification;
        _incidentLogger = incidentLogger;
        _mode         = mode.ToLower();
        _onExpired    = onExpired.ToLower();
    }

    // --------------------------------------------------------
    // Hlavní vstupní bod – zavolá se při detekci neznámého média
    // --------------------------------------------------------
    public void HandleUnauthorizedDevice(DeviceInfo device, string whitelistVersion,
        WhitelistStatus whitelistStatus = WhitelistStatus.Valid)
    {
        _logger.LogWarning(
            "Neautorizované médium: {Device} | Uživatel: {User} | PC: {Host}",
            device, Environment.UserName, Environment.MachineName);

        var action = DetermineAction(whitelistStatus);

        // Zalogovat incident do SQLite
        var incident = new Incident
        {
            Device           = device,
            Action           = action,
            WhitelistVersion = whitelistVersion
        };
        _incidentLogger.LogIncident(incident);

        // Provést akci dle policy
        switch (action)
        {
            case IncidentAction.Warned:
                HandleWarn(device);
                break;

            case IncidentAction.Blocked:
                // ====================================================
                // FÁZE 2 – BLOCK MODE (zatím neaktivní)
                // HandleBlock(device);
                // ====================================================
                _logger.LogWarning("Block mode nakonfigurován ale ještě neimplementován – přechod na warn");
                HandleWarn(device);
                break;
        }
    }

    // --------------------------------------------------------
    // Warn mode – uživatel dostane notifikaci, médium funguje
    // --------------------------------------------------------
    private void HandleWarn(DeviceInfo device)
    {
        _notification.ShowWarning(
            title: "Nepovolené paměťové médium",
            message: $"Médium \"{device.FriendlyName}\" nebylo schváleno IT oddělením.\n" +
                     "Může se jednat o bezpečnostní hrozbu.\n" +
                     "Kontaktujte IT oddělení pro schválení.");
    }

    // --------------------------------------------------------
    // Určí akci dle konfigurace a stavu whitelistu
    // --------------------------------------------------------
    private IncidentAction DetermineAction(WhitelistStatus wlStatus)
    {
        // Expirovaný whitelist → dle konfigurace onExpiredWhitelist
        if (wlStatus == WhitelistStatus.Expired)
        {
            return _onExpired switch
            {
                "strict_block" => IncidentAction.Blocked,
                "block_new"    => IncidentAction.Blocked,
                _              => IncidentAction.Warned    // default = warn
            };
        }

        // Normální stav → dle policy.mode
        return _mode switch
        {
            "block" => IncidentAction.Blocked,
            _       => IncidentAction.Warned    // default = warn
        };
    }
}
