// ============================================================
// PolicyEnforcer.cs
// Rozhoduje co se stane když je detekováno nepovolené médium.
// Chování řídí agent.config.json → žádná reinstalace při změně.
// policy.mode = "warn"  → pouze varování, médium funguje
// policy.mode = "block" → médium uzamčeno přes DeviceIoControl
// ============================================================

using Microsoft.Extensions.Logging;
using USBGuardian.Models;
using USBGuardian.Security;

namespace USBGuardian;

public class PolicyEnforcer
{
    private readonly ILogger<PolicyEnforcer> _logger;
    private readonly NotificationService _notification;
    private readonly IncidentLogger _incidentLogger;
    private readonly DeviceBlocker _deviceBlocker;
    private readonly PolicyState _policyState;
    private readonly string _mode;       // lokální default ("warn"/"block") – fallback před heartbeatem
    private readonly string _onExpired;  // chování po expiraci whitelistu
    private readonly string _contactMessage;

    public PolicyEnforcer(
        ILogger<PolicyEnforcer> logger,
        NotificationService notification,
        IncidentLogger incidentLogger,
        DeviceBlocker deviceBlocker,
        PolicyState policyState,
        string mode,
        string onExpired,
        string contactMessage)
    {
        _logger         = logger;
        _notification   = notification;
        _incidentLogger = incidentLogger;
        _deviceBlocker  = deviceBlocker;
        _policyState    = policyState;
        _mode           = mode.ToLower();
        _onExpired      = onExpired.ToLower();
        _contactMessage = contactMessage;
    }

    // --------------------------------------------------------
    // Hlavní vstupní bod – zavolá se při detekci jakéhokoli média
    // --------------------------------------------------------
    public void HandleDevice(DeviceInfo device, string whitelistVersion,
        bool isAllowed, WhitelistStatus whitelistStatus = WhitelistStatus.Valid)
    {
        if (isAllowed)
        {
            var allowedIncident = new Incident
            {
                Device           = device,
                Action           = IncidentAction.Allowed,
                WhitelistVersion = whitelistVersion
            };
            _incidentLogger.LogConnection(allowedIncident);
            return;
        }

        var user = SessionUser.GetActiveConsoleUser();

        _logger.LogWarning(
            "Neautorizované médium: {Device} | Uživatel: {User} | PC: {Host}",
            device, user, Environment.MachineName);

        var action = DetermineAction(whitelistStatus);

        var incident = new Incident
        {
            Device           = device,
            Action           = action,
            WhitelistVersion = whitelistVersion,
            Username         = user
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
    // Zpětná kompatibilita
    // --------------------------------------------------------
    public void HandleUnauthorizedDevice(DeviceInfo device, string whitelistVersion,
        WhitelistStatus whitelistStatus = WhitelistStatus.Valid)
        => HandleDevice(device, whitelistVersion, false, whitelistStatus);

    // --------------------------------------------------------
    // Warn mode – zapíše do Toast fronty, médium funguje
    // --------------------------------------------------------
    private void HandleWarn(DeviceInfo device)
    {
        _notification.ShowWarningForDevice(
            title:  "Nepovolené paměťové médium",
            device: device,
            action: "Warned");
    }

    // --------------------------------------------------------
    // Block mode – zařízení deaktivováno, zapíše do Toast fronty
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
            _notification.ShowWarningForDevice(
                title:  "Přístup k médiu byl zablokován",
                device: device,
                action: "Blocked");
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
        if (wlStatus == WhitelistStatus.Expired)
        {
            // Dokumentované hodnoty: warn | block | allow (+ zpětně staré strict_block/block_new).
            return _onExpired switch
            {
                "block" or "strict_block" or "block_new" => IncidentAction.Blocked,
                "allow"                                  => IncidentAction.Allowed,
                _                                        => IncidentAction.Warned
            };
        }

        // Efektivní režim řídí PolicyState: lokální break-glass override → warn; jinak server enforce
        // (.213 = zdroj pravdy); před prvním heartbeatem fallback na lokální _mode.
        var effective = _policyState.EffectiveMode(_mode);
        return effective switch
        {
            "block" => IncidentAction.Blocked,
            _       => IncidentAction.Warned
        };
    }
}
