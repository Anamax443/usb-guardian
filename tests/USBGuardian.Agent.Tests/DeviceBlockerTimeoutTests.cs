// ============================================================
// DeviceBlockerTimeoutTests.cs
// Nález z oponentury 11.09.2026: RunPowerShell četlo StandardOutput/StandardError
// synchronně (ReadToEnd()) PŘED voláním WaitForExit(10_000) - ReadToEnd() zůstane
// viset, dokud dítě nezavře handle (tj. dokud proces neskončí SÁM), takže deklarovaný
// 10s timeout se k WaitForExit vůbec nedostal a nic reálně neomezoval. Zaseknutý
// powershell.exe tak mohl zablokovat Block/UnblockDevice navěky.
//
// Testy spouští SKUTEČNÝ powershell.exe (stejně jako produkční kód) - jde o
// integrační test, timeoutMs je proto parametrizovaný, aby test nečekal reálných 10 s.
// ============================================================

using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using USBGuardian;
using Xunit;

namespace USBGuardian.Agent.Tests;

public class DeviceBlockerTimeoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "usbg-test-" + Guid.NewGuid());
    private readonly DeviceBlocker _blocker;

    public DeviceBlockerTimeoutTests()
    {
        Directory.CreateDirectory(_root);
        _blocker = new DeviceBlocker(
            NullLogger<DeviceBlocker>.Instance,
            blockedPath: Path.Combine(_root, "blocked.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Hung_powershell_is_killed_at_timeout_instead_of_blocking_forever()
    {
        // Skript spí 20 s, timeout je 500 ms - kdyby WaitForExit reálně nefungoval jako
        // strop (starý bug), test by čekal celých 20 s na dokončení Start-Sleep.
        var sw = Stopwatch.StartNew();
        var result = _blocker.RunPowerShell("Start-Sleep -Seconds 20", timeoutMs: 500);
        sw.Stop();

        Assert.Equal(string.Empty, result);
        Assert.True(sw.ElapsedMilliseconds < 15_000,
            $"RunPowerShell čekalo {sw.ElapsedMilliseconds} ms - proces po timeoutu nebyl zabit, čekalo se na dospání Start-Sleep");
    }

    [Fact]
    public void Fast_script_still_returns_full_output_within_timeout()
    {
        var result = _blocker.RunPowerShell("Write-Output 'BLOCKED:test'", timeoutMs: 5_000);

        Assert.Contains("BLOCKED:test", result);
    }
}
