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

        if (computer != null)
        {
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

        return Ok(new HeartbeatResponse
        {
            CurrentWhitelistVersion  = currentVersion,
            WhitelistUpdateAvailable = currentVersion != whitelistVersion,
            ServerTime               = DateTime.UtcNow
        });
    }
}
