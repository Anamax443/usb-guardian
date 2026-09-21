// ============================================================
// DeviceMonitorUsbDetectionTests.cs
// UASP (USB Attached SCSI) krabičky – USB-SATA redukce s UAS bridge čipem –
// se ve Win32_DiskDrive hlásí jako InterfaceType='SCSI', k nerozeznání od
// interního SATA/NVMe disku. Před opravou to znamenalo, že takový externí
// disk agent vůbec nezaevidoval: žádný log, žádné varování, žádná blokace.
// Živě ověřeno na TRNKAMW11 (WD Blue SA510 v UASP krabičce, 2026-09-21).
// ============================================================

using USBGuardian;
using Xunit;

namespace USBGuardian.Agent.Tests;

public class DeviceMonitorUsbDetectionTests
{
    [Theory]
    [InlineData("USB")]
    [InlineData("usb")]
    [InlineData("SD")]
    public void IsUsbAttached_KnownInterfaceType_TrueBezOhleduNaBusType(string interfaceType)
    {
        Assert.True(DeviceMonitor.IsUsbAttached(interfaceType, busType: null));
    }

    [Fact]
    public void IsUsbAttached_ScsiSUsbBusType_True()
    {
        // UASP disk: Win32_DiskDrive.InterfaceType='SCSI', ale MSFT_PhysicalDisk.BusType=7 (USB).
        Assert.True(DeviceMonitor.IsUsbAttached("SCSI", busType: 7));
    }

    [Fact]
    public void IsUsbAttached_ScsiBezBusTypeInfo_False()
    {
        // Interní disk (NVMe/SATA) i UASP disk, jehož BusType se nepodařilo zjistit –
        // bez potvrzeného USB busu nesmí projít jako removable médium.
        Assert.False(DeviceMonitor.IsUsbAttached("SCSI", busType: null));
    }

    [Theory]
    [InlineData(17)] // NVMe
    [InlineData(11)] // SATA
    [InlineData(1)]  // SCSI (klasický, ne USB)
    public void IsUsbAttached_ScsiSInterniBusType_False(ushort busType)
    {
        Assert.False(DeviceMonitor.IsUsbAttached("SCSI", busType));
    }
}
