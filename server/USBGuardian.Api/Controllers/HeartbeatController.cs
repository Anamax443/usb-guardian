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

    public HeartbeatController(AppDbContext db) => _db = db;

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

        // Centrální vynucování (.213 = zdroj pravdy) → agent dle něj blokuje/varuje (Fáze 2).
        var enforceRow = await _db.AppSettings.FirstOrDefaultAsync(s => s.Key == "policy.enforce");
        var enforce = string.Equals(enforceRow?.Value, "true", StringComparison.OrdinalIgnoreCase);

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
