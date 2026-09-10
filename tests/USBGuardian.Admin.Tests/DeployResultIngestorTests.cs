// ============================================================
// DeployResultIngestorTests.cs
// Testuje jen čisté, souborem-nezávislé části: parsování last.csv
// (Export-Csv formát) a extrakci "THROW: ..." z last.log. Samotné
// RunAsync (I/O + AppSettings) se netestuje - psaní/čtení skutečné
// DB kontextem tady stejně nejde bez SQL Serveru po ruce.
// ============================================================

using USBGuardian.Admin.Deploy;
using USBGuardian.Api.Data;
using Xunit;

namespace USBGuardian.Admin.Tests;

public class DeployResultIngestorTests
{
    [Fact]
    public void Parses_a_simple_csv_row()
    {
        var csv = "\"Host\",\"Status\",\"Detail\",\"Ts\"\r\n\"PARLW11\",\"OK\",\"installed; wd ok; toast ok\",\"15:13:52\"\r\n";
        var rows = DeployResultIngestor.ParseCsv(csv);

        var row = Assert.Single(rows);
        Assert.Equal("PARLW11", row.Host);
        Assert.Equal("OK", row.Status);
        Assert.Equal("installed; wd ok; toast ok", row.Detail);
        Assert.Equal("15:13:52", row.Ts);
    }

    [Fact]
    public void Handles_a_comma_inside_a_quoted_detail_field()
    {
        // Skutečná exception message ze sc.exe/robocopy může obsahovat čárku - Export-Csv
        // pole obalí do uvozovek, naivní Split(',') by řádek rozsekal na víc sloupců.
        var csv = "\"Host\",\"Status\",\"Detail\",\"Ts\"\r\n\"PC1\",\"FAIL\",\"sc create selhal (5): access denied, retry later\",\"10:00:00\"\r\n";
        var rows = DeployResultIngestor.ParseCsv(csv);

        Assert.Equal("sc create selhal (5): access denied, retry later", rows[0].Detail);
    }

    [Fact]
    public void Handles_an_escaped_quote_inside_a_field()
    {
        var csv = "\"Host\",\"Status\",\"Detail\",\"Ts\"\r\n\"PC1\",\"FAIL\",\"cesta \"\"C:\\Apps\"\" nenalezena\",\"10:00:00\"\r\n";
        var rows = DeployResultIngestor.ParseCsv(csv);

        Assert.Equal("cesta \"C:\\Apps\" nenalezena", rows[0].Detail);
    }

    [Fact]
    public void Returns_empty_list_for_header_only_csv()
    {
        Assert.Empty(DeployResultIngestor.ParseCsv("\"Host\",\"Status\",\"Detail\",\"Ts\"\r\n"));
    }

    [Fact]
    public void Returns_empty_list_for_empty_input()
    {
        Assert.Empty(DeployResultIngestor.ParseCsv(""));
    }

    [Theory]
    [InlineData("OK", ActivityLevel.Info)]
    [InlineData("WOULD-DEPLOY", ActivityLevel.Info)]
    [InlineData("SKIP", ActivityLevel.Warn)]
    [InlineData("STARTED?", ActivityLevel.Warn)]
    [InlineData("FAIL", ActivityLevel.Error)]
    [InlineData("OFFLINE", ActivityLevel.Error)]
    public void Maps_each_deploy_status_to_the_right_activity_level(string status, ActivityLevel expected)
    {
        Assert.Equal(expected, DeployResultIngestor.LevelForStatus(status));
    }

    [Fact]
    public void Finds_the_throw_line_in_a_transcript()
    {
        var transcript = "**********\r\nTranscript started\r\n" +
                          "THROW: File ...Deploy-AgentFleet.ps1 cannot be loaded. The file ... is not digitally signed.\r\n" +
                          "WHOAMI: AXINETWORK\\gmsa-USBGdep$\r\n";
        var line = DeployResultIngestor.ExtractThrowLine(transcript);

        Assert.NotNull(line);
        Assert.Contains("not digitally signed", line);
    }

    [Fact]
    public void Returns_null_when_transcript_has_no_throw_line()
    {
        var transcript = "**********\r\nTranscript started\r\nWHOAMI: AXINETWORK\\gmsa-USBGdep$\r\n";
        Assert.Null(DeployResultIngestor.ExtractThrowLine(transcript));
    }
}
