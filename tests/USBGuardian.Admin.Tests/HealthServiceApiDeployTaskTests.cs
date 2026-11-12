// ============================================================
// HealthServiceApiDeployTaskTests.cs
// EvaluateApiDeployTask je cista interpretace vystupu "schtasks /Query ... /FO LIST /V"
// (HealthService.cs, skupina "Provoz a udrzba").
//
// Zivy incident 12.11.2026: naplanovana uloha USBGuardian-ApiDeploy mela vadnou Action
// v XML (zdvojene "cmd" pred /c) a padala s Last Result != 0 (ERROR_INVALID_FUNCTION)
// cele dva mesice, anis by si toho kdokoli vsiml - deploy-log zustal beze zmeny, protoze
// selhani nastalo driv, nez skript stihl zapsat prvni radek. Tahle kontrola se pta primo
// Scheduleru (Last Result), ne logu.
// ============================================================

using USBGuardian.Admin.Health;
using Xunit;

namespace USBGuardian.Admin.Tests;

public class HealthServiceApiDeployTaskTests
{
    private const string TaskName = @"\USBGuardian\USBGuardian-ApiDeploy";

    private static string Schtasks(string lastResult, string lastRun) =>
        "Folder: \\USBGuardian\n"
      + "HostName:                            APP_SERVER\n"
      + "TaskName:                             USBGuardian-ApiDeploy\n"
      + "Status:                               Ready\n"
      + $"Last Run Time:                        {lastRun}\n"
      + $"Last Result:                          {lastResult}\n";

    [Fact]
    public void UspesnyBeh_JeOk()
    {
        var o = HealthService.EvaluateApiDeployTask(0, Schtasks("0", "11/12/2026 5:44:04 PM"), TaskName);
        Assert.Equal(HealthState.Ok, o.State);
        Assert.Contains("OK", o.Value);
    }

    [Fact]
    public void NenulovyLastResult_JeBad_ZivyIncidentKod1()
    {
        // Presne tenhle kod (1 = ERROR_INVALID_FUNCTION) zpusobila vadna Action v XML.
        var o = HealthService.EvaluateApiDeployTask(0, Schtasks("1", "11/12/2026 5:44:04 PM"), TaskName);
        Assert.Equal(HealthState.Bad, o.State);
        Assert.Contains("1", o.Value);
        Assert.NotEmpty(o.Fix);
    }

    [Fact]
    public void UlohaNikdyNebezela_JeWarn_NeBad()
    {
        var o = HealthService.EvaluateApiDeployTask(0, Schtasks("0", "N/A"), TaskName);
        Assert.Equal(HealthState.Warn, o.State);
    }

    [Fact]
    public void UlohaNeexistuje_JeOff_NeBad()
    {
        // schtasks vraci nenulovy exit kod, kdyz uloha vubec neni definovana.
        var o = HealthService.EvaluateApiDeployTask(1, "ERROR: The system cannot find the file specified.", TaskName);
        Assert.Equal(HealthState.Off, o.State);
    }
}
