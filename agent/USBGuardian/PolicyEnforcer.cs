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
                HandleBlock(device);
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
                     _contactMessage);
    }

    // --------------------------------------------------------
    // Block mode – médium uzamčeno přes DeviceIoControl
    // Pokud nelze zjistit drive letter → fallback na warn
    // --------------------------------------------------------
    private void HandleBlock(DeviceInfo device)
    {
        if (device.DriveLetters.Count == 0)
        {
            _logger.LogWarning(
                "Block mode: nelze zjistit drive letter pro {Device} – fallback na warn",
                device.FriendlyName);
            HandleWarn(device);
            return;
        }

        var blockedLetters = new List<string>();
        var failedLetters  = new List<string>();

        // Zablokujeme každý drive letter přiřazený k médiu
        foreach (var letter in device.DriveLetters)
        {
            var result = _deviceBlocker.BlockDrive(letter);

            if (result.IsSuccess)
            {
                blockedLetters.Add(letter);
                _logger.LogWarning("Drive {Letter}: zablokován", letter);
            }
            else
            {
                failedLetters.Add(letter);
                _logger.LogError("Drive {Letter}: blokování selhalo – {Error}",
                    letter, result.ErrorMessage);
            }
        }

        // Notifikace uživateli
        if (blockedLetters.Count > 0)
        {
            var drives = string.Join(", ", blockedLetters.Select(l => $"{l}:"));
            _notification.ShowWarning(
                title: "Přístup k médiu byl zablokován",
                message: $"Médium \"{device.FriendlyName}\" ({drives}) nebylo schváleno IT oddělením.\n" +
                         "Přístup byl zablokován z bezpečnostních důvodů.\n" +
                         _contactMessage);
        }

        // Pokud blokování selhalo pro část disků → warn jako fallback
        if (failedLetters.Count > 0)
        {
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
