// ============================================================
// IncidentDedupKeyTests.cs
// Audit 04.09.2026: dedup klíč v IncidentQueueWorker chyběl ProductId/PnpDeviceId -
// dvě různá zařízení stejného vendoru se stejným (u levných USB kusů často sdíleným,
// generickým) sériovým číslem, připojená ve stejné sekundě, by kolidovala a druhý
// incident by dedup tiše zahodil jako duplikát prvního. Testy pokrývají přesně tohle
// riziko a zároveň to, že skutečný resend (retry po výpadku) se pořád spáruje se
// svým dřívějším zápisem.
// ============================================================

using USBGuardian.Api.Queue;
using Xunit;

namespace USBGuardian.Api.Tests;

public class IncidentDedupKeyTests
{
    private static readonly DateTime Ts = new(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Different_products_from_the_same_vendor_and_serial_no_longer_collide()
    {
        // Přesně scénář z auditu: stejný vendor, stejné (generické) sériové číslo,
        // stejná sekunda - ale jde o dvě různá fyzická zařízení (jiný ProductId).
        var key1 = IncidentQueueWorker.MakeKey(Ts, serial: "0", vendor: "0951",
            productId: "1666", pnpDeviceId: "USB\\VID_0951&PID_1666\\AA111");
        var key2 = IncidentQueueWorker.MakeKey(Ts, serial: "0", vendor: "0951",
            productId: "16C0", pnpDeviceId: "USB\\VID_0951&PID_16C0\\BB222");

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void Different_physical_units_with_the_same_product_and_serial_no_longer_collide()
    {
        // I při shodném ProductId (stejný model) je PnpDeviceId to, co odliší dva
        // fyzicky odlišné kusy se shodně (chybně) naprogramovaným sériovým číslem.
        var key1 = IncidentQueueWorker.MakeKey(Ts, serial: "0", vendor: "0951",
            productId: "1666", pnpDeviceId: "USB\\VID_0951&PID_1666\\AA111");
        var key2 = IncidentQueueWorker.MakeKey(Ts, serial: "0", vendor: "0951",
            productId: "1666", pnpDeviceId: "USB\\VID_0951&PID_1666\\CC333");

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void The_same_incident_sent_twice_still_produces_the_same_key()
    {
        // Dvě situace, ve kterých agent pošle bajtově stejná pole znovu, a dedup
        // je MUSÍ spárovat s dřívějším zápisem, ne založit jako nový incident:
        //   1) retry po výpadku (offset persist, IncidentSync.cs)
        //   2) doplnění DisconnectedAt k už zapsanému připojení (klíč DisconnectedAt
        //      neobsahuje, takže se nezmění - koreluje se stejně jako u agenta
        //      v IncidentLogger.UpdateDisconnectedAt, přes PnpDeviceId + Timestamp)
        var original = IncidentQueueWorker.MakeKey(Ts, "SN123", "0951", "1666",
            "USB\\VID_0951&PID_1666\\AA111");
        var resend = IncidentQueueWorker.MakeKey(Ts, "SN123", "0951", "1666",
            "USB\\VID_0951&PID_1666\\AA111");

        Assert.Equal(original, resend);
    }
}
