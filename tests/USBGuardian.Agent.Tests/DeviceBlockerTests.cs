// ============================================================
// DeviceBlockerTests.cs
// InterpretBlockOutput je čistá interpretace výstupu blokovacího PowerShell skriptu
// (bez skutečného volání PowerShellu/PnP - to v testu snadno nejde sestrojit).
// Regresní test pro nález z oponentury 10.09.2026: BlockDevice mohl chybně vyhodnotit
// blokování jako úspěšné, protože Disable-PnpDevice nebyl obalený v try/catch -
// nedokončující chyba se ztratila a skript vždy vypsal "BLOCKED", i když se zařízení
// deaktivovat nepodařilo. Tenhle test ověřuje už jen INTERPRETACI výstupu (script teď
// posílá FAILED:... místo BLOCKED při chybě) - tj. že "cokoliv jiného než čisté BLOCKED"
// se nikdy nevyhodnotí jako úspěch.
// ============================================================

using USBGuardian;
using Xunit;

namespace USBGuardian.Agent.Tests;

public class DeviceBlockerTests
{
    [Fact]
    public void Blocked_output_is_success()
    {
        var result = DeviceBlocker.InterpretBlockOutput(
            "BLOCKED:USBSTOR\\DISK&VEN_SANDISK\\4C530000", "PNP-1");

        Assert.True(result.IsSuccess);
        Assert.Equal("PNP-1", result.PnpDeviceId);
    }

    [Fact]
    public void Not_found_is_a_failure_not_a_success()
    {
        var result = DeviceBlocker.InterpretBlockOutput("NOT_FOUND", "PNP-1");

        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void A_failed_disable_pnp_device_call_is_a_failure_not_a_false_success()
    {
        // Přesně nález z oponentury: Disable-PnpDevice selže (např. zařízení nejde
        // deaktivovat), skript to teď hlásí jako FAILED:... - nesmí se to vyhodnotit
        // jako BLOCKED jen proto, že řetězec obsahuje jiná písmena.
        var result = DeviceBlocker.InterpretBlockOutput(
            "FAILED:Cannot disable device - restart required", "PNP-1");

        Assert.False(result.IsSuccess);
        Assert.Contains("Disable-PnpDevice selhalo", result.ErrorMessage);
    }

    [Fact]
    public void Unexpected_empty_or_garbage_output_is_a_failure()
    {
        Assert.False(DeviceBlocker.InterpretBlockOutput("", "PNP-1").IsSuccess);
        Assert.False(DeviceBlocker.InterpretBlockOutput("neco uplne jineho", "PNP-1").IsSuccess);
    }
}
