// ============================================================
// PolicyEnforcerExpiryTests.cs
// Regresní test pro audit-nález: vypršelý whitelist "je na (starém)
// seznamu" nesmí obejít politiku onExpired (viz PolicyEnforcer.HandleDevice
// a DeviceMonitor.ReEnforceConnectedDevices, oprava 56b4235).
//
// DetermineAction (internal, viz PolicyEnforcer.cs) testuje ZAMÝŠLENOU akci
// čistě z konfigurace/stavu whitelistu, bez enforcementu - přesně to, co
// tenhle test ověřuje. Jestli se SKUTEČNÁ zapsaná Action v incidentu liší
// od záměru (Blocked -> Warned při selhání enforcementu), to testuje
// samostatně Recorded_action_matches_actual_outcome_not_just_intent níž
// (oprava 11.09.2026 - dřív se do incidentu zapisoval záměr PŘED tím, než
// HandleBlock vůbec proběhl, takže neúspěšné zablokování se v auditu tvářilo
// jako úspěšné).
//
// Zavislosti (IncidentLogger/NotificationService/DeviceBlocker/PolicyState)
// nejsou mockovane - berou jen cesty k souborum, takze test pouziva SKUTECNE
// instance smerovane do docasneho adresare.
// ============================================================

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using USBGuardian;
using USBGuardian.Models;
using Xunit;

namespace USBGuardian.Agent.Tests;

public class PolicyEnforcerExpiryTests : IDisposable
{
    private readonly string _root;
    private readonly IncidentLogger _incidentLogger;
    private readonly NotificationService _notification;
    private readonly DeviceBlocker _deviceBlocker;
    private readonly PolicyState _policyState;

    public PolicyEnforcerExpiryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "usbguardian-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_root);

        _incidentLogger = new IncidentLogger(
            NullLogger<IncidentLogger>.Instance,
            queuePath: Path.Combine(_root, "queue"),
            sentPath: Path.Combine(_root, "sent"));

        // enabled:false = cisty no-op (viz NotificationService.ShowWarning*), test se
        // netyka toastu, jen zaznamu v incidentni fronte.
        _notification = new NotificationService(
            NullLogger<NotificationService>.Instance,
            enabled: false,
            contactMessage: "test",
            queuePath: Path.Combine(_root, "toast-queue"));

        _deviceBlocker = new DeviceBlocker(
            NullLogger<DeviceBlocker>.Instance,
            blockedPath: Path.Combine(_root, "blocked.json"));

        _policyState = new PolicyState(Path.Combine(_root, "override.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private PolicyEnforcer MakeEnforcer(string mode, string onExpired) =>
        new(NullLogger<PolicyEnforcer>.Instance,
            _notification, _incidentLogger, _deviceBlocker, _policyState,
            mode, onExpired, contactMessage: "test");

    // PnpDeviceId prázdné = enforcement nemá co dělat (fallback na warn) - používá se
    // v testu níž, který ověřuje přesně tenhle fallback a jeho dopad na zapsanou Action.
    private static DeviceInfo MakeDevice() => new()
    {
        VendorId     = "0951",
        ProductId    = "1666",
        SerialNumber = "TESTSN123",
        FriendlyName = "Test USB Disk",
        PnpDeviceId  = "",
    };

    private string ReadLoggedAction()
    {
        var queueDir = Path.Combine(_root, "queue");
        var file = Directory.GetFiles(queueDir, "*.json").Single();
        var daily = JsonSerializer.Deserialize<DailyLog>(File.ReadAllText(file),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return daily.Records.Single().Action;
    }

    [Theory]
    [InlineData("block", IncidentAction.Blocked)]
    [InlineData("warn",  IncidentAction.Warned)]
    [InlineData("allow", IncidentAction.Allowed)]
    public void Expired_whitelist_intent_respects_onExpired_even_for_a_listed_device(
        string onExpired, IncidentAction expectedIntent)
    {
        // Přesně bug z auditu: zařízení JE na (starém) whitelistu, ale whitelist je
        // EXPIROVANÝ - o ZAMÝŠLENÉ akci musí rozhodnout onExpired, ne holé "je na seznamu"
        // (o tom rozhoduje HandleDevice ještě před voláním DetermineAction).
        var enforcer = MakeEnforcer(mode: "warn", onExpired);

        Assert.Equal(expectedIntent, enforcer.DetermineAction(WhitelistStatus.Expired));
    }

    [Fact]
    public void Valid_whitelist_still_allows_a_listed_device_regardless_of_onExpired()
    {
        // Sanity: fix se netyká platného (needexpirovaného) whitelistu.
        var enforcer = MakeEnforcer(mode: "warn", onExpired: "block");

        enforcer.HandleDevice(MakeDevice(), whitelistVersion: "v1",
            isAllowed: true, whitelistStatus: WhitelistStatus.Valid);

        Assert.Equal("Allowed", ReadLoggedAction());
    }

    [Fact]
    public void Device_not_on_whitelist_still_uses_configured_mode()
    {
        // Sanity: druhá větev (isAllowed=false) fixem nezměněná.
        var enforcer = MakeEnforcer(mode: "warn", onExpired: "warn");

        enforcer.HandleDevice(MakeDevice(), whitelistVersion: "v1",
            isAllowed: false, whitelistStatus: WhitelistStatus.Valid);

        Assert.Equal("Warned", ReadLoggedAction());
    }

    [Fact]
    public void Recorded_action_matches_actual_outcome_not_just_intent()
    {
        // Nález z oponentury 11.09.2026: záměr je Blocked (mode=block), ale zařízení
        // nemá PNPDeviceId -> HandleBlock spadne na fallback (Warned). Incident MUSÍ
        // zaznamenat skutečný výsledek (Warned), ne původní záměr (Blocked) - jinak
        // audit tvrdí, že se médium zablokovalo, i když zůstalo přístupné.
        var enforcer = MakeEnforcer(mode: "block", onExpired: "warn");

        Assert.Equal(IncidentAction.Blocked, enforcer.DetermineAction(WhitelistStatus.Valid));

        enforcer.HandleDevice(MakeDevice(), whitelistVersion: "v1",
            isAllowed: false, whitelistStatus: WhitelistStatus.Valid);

        Assert.Equal("Warned", ReadLoggedAction());
    }
}
