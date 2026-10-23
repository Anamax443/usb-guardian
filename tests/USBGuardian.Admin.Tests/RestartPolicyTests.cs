// ============================================================
// RestartPolicyTests.cs
//
// Zivy incident 23.10.2026: konzole mela sama sebe v svc.restart.targets,
// planovac ji v 03:30 zkusil restartovat (Stop+Start ve vlastnim procesu) -
// Stop() spustil SCM shutdown hostitele drive, nez stihl dobehnout Start(),
// konzole zustala STOPPED 8 hodin (viz RestartPolicy.Decide komentar).
// Tyhle testy pokryvaji jen tu rozhodovaci logiku (cista funkce, zadny
// skutecny ServiceController) - overuji, ze lokalni+Running uz nikdy
// nejde do vetve Stop()+Start().
// ============================================================

using System.ServiceProcess;
using USBGuardian.Admin.Maintenance;
using Xunit;

namespace USBGuardian.Admin.Tests;

public class RestartPolicyTests
{
    [Fact]
    public void LocalniSluzba_Bezi_PreskociSelfRestart()
    {
        var d = RestartPolicy.Decide(isLocal: true, ServiceControllerStatus.Running);
        Assert.Equal(RestartDecision.SkipSelfRunning, d);
    }

    [Fact]
    public void LocalniSluzba_Stoji_SeNastartuje()
    {
        var d = RestartPolicy.Decide(isLocal: true, ServiceControllerStatus.Stopped);
        Assert.Equal(RestartDecision.StartOnly, d);
    }

    [Fact]
    public void VzdalenaSluzba_Bezi_SePlneRestartuje()
    {
        var d = RestartPolicy.Decide(isLocal: false, ServiceControllerStatus.Running);
        Assert.Equal(RestartDecision.Restart, d);
    }

    [Fact]
    public void VzdalenaSluzba_Stoji_SeNastartuje()
    {
        var d = RestartPolicy.Decide(isLocal: false, ServiceControllerStatus.Stopped);
        Assert.Equal(RestartDecision.StartOnly, d);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NecekanyStav_JeChyba_BezOhleduNaLocal(bool isLocal)
    {
        var d = RestartPolicy.Decide(isLocal, ServiceControllerStatus.StartPending);
        Assert.Equal(RestartDecision.UnexpectedState, d);
    }
}
