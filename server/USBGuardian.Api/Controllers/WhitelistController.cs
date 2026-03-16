// ============================================================
// WhitelistController.cs
// Distribuce whitelistu agentům + správa
// GET  /api/whitelist          – stáhnutí aktuálního whitelistu
// GET  /api/whitelist/version  – jen číslo verze (pro heartbeat)
// POST /api/whitelist          – přidání nového zařízení (IT admin)
// ============================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;
using USBGuardian.Api.Models;

namespace USBGuardian.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Microsoft.AspNetCore.Authorization.Authorize(Policy = "USBGuardianClients")]
public class WhitelistController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<WhitelistController> _logger;

    public WhitelistController(AppDbContext db, ILogger<WhitelistController> logger)
    {
        _db     = db;
        _logger = logger;
    }

    // --------------------------------------------------------
    // GET /api/whitelist
    // Agent stáhne aktuální whitelist (volá se při sync)
    // --------------------------------------------------------
    [HttpGet]
    public async Task<IActionResult> GetWhitelist()
    {
        // Aktuální aktivní verze
        var version = await _db.WhitelistVersions
            .Where(v => v.IsActive)
            .OrderByDescending(v => v.IssuedAt)
            .FirstOrDefaultAsync();

        if (version == null)
            return NotFound("Žádný aktivní whitelist nebyl nalezen.");

        // Aktivní zařízení
        var devices = await _db.WhitelistDevices
            .Where(d => d.IsActive)
            .Select(d => new WhitelistDeviceDto
            {
                VendorId     = d.VendorId,
                ProductId    = d.ProductId,
                SerialNumber = d.SerialNumber,
                Description  = d.Description,
                ApprovedAt   = d.ApprovedAt,
                ApprovedBy   = d.ApprovedBy
            })
            .ToListAsync();

        var response = new WhitelistResponse
        {
            Version    = version.Version,
            IssuedAt   = version.IssuedAt,
            ValidUntil = version.ValidUntil,
            Signature  = version.Signature,
            Devices    = devices
        };

        _logger.LogDebug("Whitelist {Version} stažen od {Ip}",
            version.Version, HttpContext.Connection.RemoteIpAddress);

        return Ok(response);
    }

    // --------------------------------------------------------
    // GET /api/whitelist/version
    // Rychlá kontrola verze bez stažení celého whitelistu
    // --------------------------------------------------------
    [HttpGet("version")]
    public async Task<IActionResult> GetCurrentVersion()
    {
        var version = await _db.WhitelistVersions
            .Where(v => v.IsActive)
            .OrderByDescending(v => v.IssuedAt)
            .Select(v => new { v.Version, v.IssuedAt, v.ValidUntil })
            .FirstOrDefaultAsync();

        return version != null ? Ok(version) : NotFound();
    }

    // --------------------------------------------------------
    // POST /api/whitelist/devices
    // Přidání nového zařízení do whitelistu (IT admin / L1)
    // --------------------------------------------------------
    [HttpPost("devices")]
    public async Task<IActionResult> AddDevice([FromBody] WhitelistDeviceDto dto)
    {
        // Kontrola duplicity
        var exists = await _db.WhitelistDevices.AnyAsync(d =>
            d.VendorId    == dto.VendorId &&
            d.ProductId   == dto.ProductId &&
            d.SerialNumber == dto.SerialNumber &&
            d.IsActive);

        if (exists)
            return Conflict("Zařízení je již na whitelistu.");

        var device = new WhitelistDevice
        {
            VendorId     = dto.VendorId,
            ProductId    = dto.ProductId,
            SerialNumber = dto.SerialNumber,
            Description  = dto.Description,
            ApprovedBy   = dto.ApprovedBy,
            ApprovedAt   = DateTime.UtcNow,
            IsActive     = true
        };

        _db.WhitelistDevices.Add(device);

        // Vytvoříme novou verzi whitelistu
        await BumpWhitelistVersion(dto.ApprovedBy);
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Přidáno zařízení {VendorId}:{ProductId}:{Serial} od {ApprovedBy}",
            dto.VendorId, dto.ProductId, dto.SerialNumber, dto.ApprovedBy);

        return Ok(device);
    }

    // --------------------------------------------------------
    // Interní: vytvoření nové verze whitelistu po změně
    // --------------------------------------------------------
    private async Task BumpWhitelistVersion(string issuedBy)
    {
        // Deaktivujeme starou verzi
        var old = await _db.WhitelistVersions
            .Where(v => v.IsActive).ToListAsync();
        old.ForEach(v => v.IsActive = false);

        // Nová verze
        var versionString = $"{DateTime.UtcNow:yyyy-MM-dd}-v{old.Count + 1}";
        _db.WhitelistVersions.Add(new WhitelistVersion
        {
            Version    = versionString,
            IssuedAt   = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.AddDays(30),
            IssuedBy   = issuedBy,
            IsActive   = true
        });
    }
}
