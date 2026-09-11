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
        // Vypršelý whitelist není zdroj pravdy o povolení: "je na (starém) seznamu" nesmí
        // obejít politiku onExpired. Bez tohohle IsAllowed()==true zkratovalo rozhodnutí
        // dřív, než se DetermineAction() vůbec podíval na whitelistStatus - reálně tedy
        // onExpired nikdy neplatilo pro zařízení, které v expirovaném whitelistu zůstalo.
        if (isAllowed && whitelistStatus != WhitelistStatus.Expired)
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

        var intendedAction = DetermineAction(whitelistStatus);

        // Skutečná akce se může lišit od zamýšlené (Blocked -> Warned, když se enforcement
        // nepovede - chybějící PNPDeviceID nebo selhání Disable-PnpDevice). Audit smí zapsat
        // jen POTVRZENÝ výsledek, ne pouhý záměr - jinak incident tvrdí "Blocked", i když
        // médium zůstalo přístupné (nález z oponentury 11.09.2026).
        var actualAction = intendedAction switch
        {
            IncidentAction.Blocked => HandleBlock(device),
            IncidentAction.Warned  => HandleWarn(device),
            _                      => intendedAction
        };

        var incident = new Incident
        {
            Device           = device,
            Action           = actualAction,
            WhitelistVersion = whitelistVersion,
            Username         = user
        };
        _incidentLogger.LogConnection(incident);
    }

    // --------------------------------------------------------
    // Zpětná kompatibilita
    // --------------------------------------------------------
    public void HandleUnauthorizedDevice(DeviceInfo device, string whitelistVersion,
        WhitelistStatus whitelistStatus = WhitelistStatus.Valid)
        => HandleDevice(device, whitelistVersion, false, whitelistStatus);

    // --------------------------------------------------------
    // Warn mode – zapíše do Toast fronty, médium funguje. Vrací skutečnou akci (vždy Warned) -
    // volající s tím zapíše do incidentu, ať je to samo (HandleDevice), nebo fallback z HandleBlock.
    // --------------------------------------------------------
    private IncidentAction HandleWarn(DeviceInfo device)
    {
        _notification.ShowWarningForDevice(
            title:  "Nepovolené paměťové médium",
            device: device,
            action: "Warned");
        return IncidentAction.Warned;
    }

    // --------------------------------------------------------
    // Block mode – zařízení deaktivováno, zapíše do Toast fronty. Vrací SKUTEČNOU akci:
    // Blocked jen při potvrzeném úspěchu, jinak Warned (fallback) - volající to zapíše
    // do incidentu takové, jaké to doopravdy je.
    // --------------------------------------------------------
    private IncidentAction HandleBlock(DeviceInfo device)
    {
        if (string.IsNullOrEmpty(device.PnpDeviceId))
        {
            _logger.LogWarning(
                "Block mode: PNPDeviceID není k dispozici pro {Device} – fallback na warn",
                device.FriendlyName);
            return HandleWarn(device);
        }

        var result = _deviceBlocker.BlockDevice(device.PnpDeviceId,
            $"{device.VendorId}:{device.ProductId}:{device.SerialNumber}");

        if (result.IsSuccess)
        {
            _logger.LogWarning("Zařízení {Device} ZABLOKOVÁNO", device.FriendlyName);
            _notification.ShowWarningForDevice(
                title:  "Přístup k médiu byl zablokován",
                device: device,
                action: "Blocked");
            return IncidentAction.Blocked;
        }

        _logger.LogError("Blokování selhalo pro {Device}: {Error}",
            device.FriendlyName, result.ErrorMessage);
        return HandleWarn(device);
    }

    // --------------------------------------------------------
    // Určí ZAMÝŠLENOU akci dle konfigurace a stavu whitelistu (čistá rozhodovací logika,
    // bez enforcementu) - internal kvůli testům (viz PolicyEnforcerExpiryTests).
    // --------------------------------------------------------
    internal IncidentAction DetermineAction(WhitelistStatus wlStatus)
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
        // (APP_SERVER = zdroj pravdy); před prvním heartbeatem fallback na lokální _mode.
        var effective = _policyState.EffectiveMode(_mode);
        return effective switch
        {
            "block" => IncidentAction.Blocked,
            _       => IncidentAction.Warned
        };
    }
}
