// ============================================================
// PolicyEnforcer.cs
// Rozhoduje co se stane když je detekováno nepovolené médium.
// Chování řídí agent.config.json → žádná reinstalace při změně.
// policy.mode = "warn"  → pouze varování, médium funguje
// policy.mode = "block" → médium uzamčeno přes DeviceIoControl
// ============================================================

using Microsoft.Extensions.Logging;
using USBGuardian.Models;

namespace USBGuardian;

public class PolicyEnforcer
{
    private readonly ILogger<PolicyEnforcer> _logger;
    private readonly NotificationService _notification;
    private readonly IncidentLogger _incidentLogger;
    private readonly DeviceBlocker _deviceBlocker;
    private readonly string _mode;       // "warn" nebo "block"
    private readonly string _onExpired;  // chování po expiraci whitelistu
    private readonly string _contactMessage;

    public PolicyEnforcer(
        ILogger<PolicyEnforcer> logger,
        NotificationService notification,
        IncidentLogger incidentLogger,
        DeviceBlocker deviceBlocker,
        string mode,
        string onExpired,
        string contactMessage)
    {
        _logger         = logger;
        _notification   = notification;
        _incidentLogger = incidentLogger;
        _deviceBlocker  = deviceBlocker;
        _mode           = mode.ToLower();
        _onExpired      = onExpired.ToLower();
        _contactMessage = contactMessage;
    }

    // --------------------------------------------------------
    // Hlavní vstupní bod – zavolá se při detekci jakéhokoli média
    // Logujeme VŠE – povolená i nepovolená
    // --------------------------------------------------------
    public void HandleDevice(DeviceInfo device, string whitelistVersion,
        bool isAllowed, WhitelistStatus whitelistStatus = WhitelistStatus.Valid)
    {
        if (isAllowed)
        {
            // Povolené médium – zalogujeme jako Allowed
            var allowedIncident = new Incident
            {
                Device           = device,
                Action           = IncidentAction.Allowed,
                WhitelistVersion = whitelistVersion
            };
            _incidentLogger.LogConnection(allowedIncident);
            return;
        }

        // Nepovolené médium
        _logger.LogWarning(
            "Neautorizované médium: {Device} | Uživatel: {User} | PC: {Host}",
            device, Environment.UserName, Environment.MachineName);

        var action = DetermineAction(whitelistStatus);

        var incident = new Incident
        {
            Device           = device,
            Action           = action,
            WhitelistVersion = whitelistVersion
        };
        _incidentLogger.LogConnection(incident);

        switch (action)
        {
            case IncidentAction.Warned:
                HandleWarn(device);
                break;
            case IncidentAction.Blocked:
                HandleBlock(device);
                break;
        }
    }

    // --------------------------------------------------------
    // Zpětná kompatibilita – pro nepovolená média
    // --------------------------------------------------------
    public void HandleUnauthorizedDevice(DeviceInfo device, string whitelistVersion,
        WhitelistStatus whitelistStatus = WhitelistStatus.Valid)
        => HandleDevice(device, whitelistVersion, false, whitelistStatus);

    // --------------------------------------------------------
    // Warn mode – uživatel dostane notifikaci, médium funguje
    // --------------------------------------------------------
    private void HandleWarn(DeviceInfo device)
    {
        _notification.ShowWarning(
            title: "Nepovolené paměťové médium",
            message: $"Médium \"{device.FriendlyName}\" nebylo schváleno IT oddělením.\n" +
                     "Může se jednat o bezpečnostní hrozbu.\n" +
                     _contactMessage);
    }

    // --------------------------------------------------------
    // Block mode – zařízení deaktivováno přes Disable-PnpDevice
    // Nevyžaduje drive letter – funguje vždy
    // --------------------------------------------------------
    private void HandleBlock(DeviceInfo device)
    {
        if (string.IsNullOrEmpty(device.PnpDeviceId))
        {
            _logger.LogWarning(
                "Block mode: PNPDeviceID není k dispozici pro {Device} – fallback na warn",
                device.FriendlyName);
            HandleWarn(device);
            return;
        }

        var result = _deviceBlocker.BlockDevice(device.PnpDeviceId);

        if (result.IsSuccess)
        {
            _logger.LogWarning("Zařízení {Device} ZABLOKOVÁNO", device.FriendlyName);
            _notification.ShowWarning(
                title: "Přístup k médiu byl zablokován",
                message: $"Médium \"{device.FriendlyName}\" nebylo schváleno IT oddělením.\n" +
                         "Zařízení bylo deaktivováno z bezpečnostních důvodů.\n" +
                         _contactMessage);
        }
        else
        {
            _logger.LogError("Blokování selhalo pro {Device}: {Error}",
                device.FriendlyName, result.ErrorMessage);
            HandleWarn(device);
        }
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
                _              => IncidentAction.Warned
            };
        }

        // Normální stav → dle policy.mode
        return _mode switch
        {
            "block" => IncidentAction.Blocked,
            _       => IncidentAction.Warned
        };
    }
}
