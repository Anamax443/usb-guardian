// ============================================================
// IncidentSpoolTests.cs
// IncidentSpool je durabilita pod frontou incidentů (audit 04.09.2026 –
// "202 Accepted" se vracelo PŘED zápisem do DB, pád procesu API mezi
// přijetím a zápisem = ztráta batche navždy). Testy pokrývají jádro
// slibu, který spool dává: co se zapíše, jde přečíst zpátky; co se
// smaže, LoadPending už nevrátí; poškozený soubor se odloží stranou,
// místo aby zablokoval start služby navždy.
// ============================================================

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using USBGuardian.Api.Models;
using USBGuardian.Api.Queue;
using Xunit;

namespace USBGuardian.Api.Tests;

public class IncidentSpoolTests : IDisposable
{
    private readonly string _root;

    public IncidentSpoolTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "usbguardian-spool-tests-" + Guid.NewGuid());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private IncidentSpool MakeSpool()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["incidents:spoolPath"] = _root })
            .Build();
        return new IncidentSpool(config, NullLogger<IncidentSpool>.Instance);
    }

    private static IncidentBatchRequest SampleRequest(string hostname = "PC-01") => new()
    {
        Hostname     = hostname,
        AgentVersion = "test-agent",
        SourceFile   = "log_PC-01_2026-09-04.json",
        Incidents =
        {
            new IncidentDto { Timestamp = DateTime.UtcNow, SerialNumber = "SN1", VendorId = "0951" },
        },
    };

    [Fact]
    public void Write_then_LoadPending_roundtrips_the_batch()
    {
        var spool = MakeSpool();
        var path  = spool.Write(SampleRequest(), sourceIp: "10.0.0.5", receivedAt: DateTime.UtcNow);

        var pending = spool.LoadPending();

        var item = Assert.Single(pending);
        Assert.Equal(path, item.SpoolFile);
        Assert.Equal("PC-01", item.Request.Hostname);
        Assert.Equal("10.0.0.5", item.SourceIp);
        Assert.Single(item.Request.Incidents);
    }

    [Fact]
    public void Delete_removes_the_file_so_LoadPending_no_longer_returns_it()
    {
        var spool = MakeSpool();
        var path  = spool.Write(SampleRequest(), sourceIp: null, receivedAt: DateTime.UtcNow);

        spool.Delete(path);

        Assert.Empty(spool.LoadPending());
    }

    [Fact]
    public void LoadPending_survives_a_corrupt_file_by_quarantining_it()
    {
        var spool = MakeSpool();
        // Simuluje záznam, který se stihl zapsat, ale ne validně dopsat.
        Directory.CreateDirectory(_root);
        var badFile = Path.Combine(_root, "20260101-000000-000_bad.json");
        File.WriteAllText(badFile, "{ toto neni platny json");

        var pending = spool.LoadPending();

        Assert.Empty(pending);
        Assert.False(File.Exists(badFile));
        Assert.True(File.Exists(badFile + ".bad"));
    }

    [Fact]
    public void LoadPending_returns_batches_in_chronological_order()
    {
        var spool = MakeSpool();
        var older = spool.Write(SampleRequest("OLDER"), null, new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc));
        var newer = spool.Write(SampleRequest("NEWER"), null, new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc));

        var pending = spool.LoadPending();

        Assert.Equal(new[] { older, newer }, pending.Select(p => p.SpoolFile));
    }

    [Fact]
    public void GetStatus_reports_zero_and_no_age_when_empty()
    {
        var spool = MakeSpool();

        var status = spool.GetStatus();

        Assert.Equal(0, status.PendingCount);
        Assert.Null(status.OldestReceivedAtUtc);
    }

    [Fact]
    public void GetStatus_reports_count_and_the_oldest_receivedAt_from_multiple_batches()
    {
        var spool = MakeSpool();
        var oldest = new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        spool.Write(SampleRequest("NEWER"), null, oldest.AddMinutes(5));
        spool.Write(SampleRequest("OLDEST"), null, oldest);

        var status = spool.GetStatus();

        Assert.Equal(2, status.PendingCount);
        Assert.Equal(oldest, status.OldestReceivedAtUtc);
    }

    [Fact]
    public void GetStatus_ignores_the_batch_that_was_already_deleted()
    {
        var spool = MakeSpool();
        var path  = spool.Write(SampleRequest(), null, DateTime.UtcNow);
        spool.Delete(path);

        var status = spool.GetStatus();

        Assert.Equal(0, status.PendingCount);
    }
}
