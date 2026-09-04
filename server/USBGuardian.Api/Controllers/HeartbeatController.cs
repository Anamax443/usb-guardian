// ============================================================
// HeartbeatController.cs
// Agent se přihlásí, server odpoví jestli má nový whitelist
// GET /api/heartbeat?hostname=PC-01&whitelistVersion=2026-03-16-v2
// ============================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;
using USBGuardian.Api.Models;

namespace USBGuardian.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Microsoft.AspNetCore.Authorization.Authorize(Policy = "USBGuardianClients")]
public class HeartbeatController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ActivityLogger _dennik;

    public HeartbeatController(AppDbContext db, ActivityLogger dennik)
    {
        _db = db;
        _dennik = dennik;
    }

    [HttpGet]
    public async Task<IActionResult> Heartbeat(
        [FromQuery] string hostname,
        [FromQuery] string? whitelistVersion,
        [FromQuery] string? agentVersion)
    {
        // Aktualizujeme LastSeen
        var computer = await _db.Computers
            .FirstOrDefaultAsync(c => c.Hostname == hostname);

        // Vyžádání dat z konzole: klíč cmd.report.<HOST> v AppSettings nese čas požadavku (UTC ISO).
        // Jednorázovost bez zápisu z API: ReportNow=true jen když je požadavek novější než PŘEDCHOZÍ
        // kontakt. Jakmile tento heartbeat posune LastSeen na teď, příští heartbeaty už ReportNow nevrátí.
        var reportNow = false;
        if (computer != null)
        {
            var prevSeen = computer.LastSeen;

            var key = "cmd.report." + (hostname ?? string.Empty).ToUpperInvariant();
            var cmdRow = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
            if (cmdRow != null
                && DateTime.TryParse(cmdRow.Value, System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.RoundtripKind,
                       out var requestedAt))
            {
                reportNow = prevSeen is null || requestedAt > prevSeen.Value;
            }

            computer.LastSeen     = DateTime.UtcNow;
            computer.AgentVersion = agentVersion ?? computer.AgentVersion;
            await _db.SaveChangesAsync();
        }

        // Zjistíme aktuální verzi whitelistu
        var currentVersion = await _db.WhitelistVersions
            .Where(v => v.IsActive)
            .OrderByDescending(v => v.IssuedAt)
            .Select(v => v.Version)
            .FirstOrDefaultAsync() ?? string.Empty;

        // Centrální vynucování (APP_SERVER = zdroj pravdy) → agent dle něj blokuje/varuje (Fáze 2).
        var enforceRow = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "policy.enforce");
        var enforce = string.Equals(enforceRow?.Value, "true", StringComparison.OrdinalIgnoreCase);

        // Deník: tep agenta je nejčastější řádek, ale právě z něj je vidět,
        // kdo se ozývá a kdo mlčí. Zapisuje se, co server odpověděl — jinak
        // by se zpětně nedalo zjistit, proč se agent zachoval, jak se zachoval.
        var zmeny = new List<string>();
        if (currentVersion != whitelistVersion) zmeny.Add($"nový whitelist {currentVersion}");
        if (reportNow) zmeny.Add("vyžádána data");
        if (enforce) zmeny.Add("vynucování ZAP");
        if (computer == null) zmeny.Add("stanice není v evidenci");

        _dennik.Log("heartbeat",
            zmeny.Count == 0
                ? $"tep OK (whitelist {whitelistVersion}, agent {agentVersion})"
                : $"tep — {string.Join(", ", zmeny)} (agent {agentVersion})",
            computer == null ? ActivityLevel.Warn : ActivityLevel.Info,
            hostname);

        return Ok(new HeartbeatResponse
        {
            CurrentWhitelistVersion  = currentVersion,
            WhitelistUpdateAvailable = currentVersion != whitelistVersion,
            ReportNow                = reportNow,
            Enforce                  = enforce,
            ServerTime               = DateTime.UtcNow
        });
    }
}
