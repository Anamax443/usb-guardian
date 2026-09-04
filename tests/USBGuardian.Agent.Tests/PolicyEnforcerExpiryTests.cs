// ============================================================
// PolicyEnforcerExpiryTests.cs
// Regresní test pro audit-nález: vypršelý whitelist "je na (starém)
// seznamu" nesmí obejít politiku onExpired (viz PolicyEnforcer.HandleDevice
// a DeviceMonitor.ReEnforceConnectedDevices, oprava 56b4235).
//
// Zavislosti (IncidentLogger/NotificationService/DeviceBlocker/PolicyState)
// nejsou mockovane - berou jen cesty k souborum, takze test pouziva SKUTECNE
// instance smerovane do docasneho adresare. PnpDeviceId zarizeni je zamerne
// prazdne: test overuje ROZHODOVACI logiku (jaka Action se zaznamena do
// incidentu), ne mechanismus blokovani (DeviceBlocker/PowerShell) - prazdne
// PnpDeviceId drzi HandleBlock na fallbacku bez volani PowerShellu, aniz by
// to menilo zaznamenanou Action (ta se zapise driv, nez HandleBlock vubec bezi).
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
    [InlineData("block", "Blocked")]
    [InlineData("warn",  "Warned")]
    [InlineData("allow", "Allowed")]
    public void Expired_whitelist_respects_onExpired_even_for_a_listed_device(
        string onExpired, string expectedAction)
    {
        // Přesně bug z auditu: zařízení JE na (starém) whitelistu (isAllowed=true),
        // ale whitelist je EXPIROVANÝ - o výsledku musí rozhodnout onExpired,
        // ne holé "je na seznamu".
        var enforcer = MakeEnforcer(mode: "warn", onExpired);

        enforcer.HandleDevice(MakeDevice(), whitelistVersion: "v1",
            isAllowed: true, whitelistStatus: WhitelistStatus.Expired);

        Assert.Equal(expectedAction, ReadLoggedAction());
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
}
