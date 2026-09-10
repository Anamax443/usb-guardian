// ============================================================
// WhitelistController.cs
// Distribuce whitelistu agentům + správa
// GET  /api/whitelist          – stáhnutí aktuálního whitelistu (agent)
// GET  /api/whitelist/version  – jen číslo verze, pro heartbeat (agent)
// POST /api/whitelist/devices  – přidání nového zařízení (IT admin / L1)
//
// Autorizace je záměrně PO AKCI, ne na controlleru: GET* smí agent
// (policy USBGuardianClients), POST smí jen admin (policy USBGuardianAdmins).
// Dřív měl POST stejnou policy jako agent - účet stanice tak teoreticky mohl
// zapisovat security policy, ne jen ji číst.
// ============================================================

using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;
using USBGuardian.Api.Models;
using USBGuardian.Api.Security;

namespace USBGuardian.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WhitelistController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<WhitelistController> _logger;
    private readonly ActivityLogger _dennik;

    public WhitelistController(AppDbContext db, ILogger<WhitelistController> logger, ActivityLogger dennik)
    {
        _db     = db;
        _logger = logger;
        _dennik = dennik;
    }

    // --------------------------------------------------------
    // GET /api/whitelist
    // Agent stáhne aktuální whitelist (volá se při sync)
    // --------------------------------------------------------
    [HttpGet]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "USBGuardianClients")]
    public async Task<IActionResult> GetWhitelist()
    {
        var sw = Stopwatch.StartNew();
        var hostname = CallerIdentity.MachineHostnameOrNull(HttpContext.User.Identity);

        // Aktivní = PUBLIKOVANÁ + PODEPSANÁ verze. Servírujeme PŘESNÝ blob (`Json`), který byl offline
        // podepsán → agent ho uloží verbatim a ověří `.sig` bajt na bajt. NE re-serializovat!
        var version = await _db.WhitelistVersions
            .Where(v => v.IsActive)
            .OrderByDescending(v => v.IssuedAt)
            .FirstOrDefaultAsync();

        if (version == null || string.IsNullOrEmpty(version.Json))
        {
            _dennik.Log("whitelist", "stažení odmítnuto – žádná publikovaná (podepsaná) verze",
                ActivityLevel.Warn, hostname);
            return NotFound("Žádný publikovaný (podepsaný) whitelist – vydej verzi v konzoli.");
        }

        _logger.LogDebug("Whitelist {Version} stažen od {Ip}",
            version.Version, HttpContext.Connection.RemoteIpAddress);
        _dennik.Log("whitelist", $"whitelist {version.Version} stažen ({sw.Elapsed.TotalMilliseconds:0} ms)",
            ActivityLevel.Info, hostname);

        return Content(version.Json, "application/json; charset=utf-8");
    }

    // --------------------------------------------------------
    // GET /api/whitelist/signature
    // Detached RSA podpis aktivního blobu (base64) – agent ukládá jako whitelist.json.sig.
    // --------------------------------------------------------
    [HttpGet("signature")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "USBGuardianClients")]
    public async Task<IActionResult> GetSignature()
    {
        var version = await _db.WhitelistVersions
            .Where(v => v.IsActive)
            .OrderByDescending(v => v.IssuedAt)
            .FirstOrDefaultAsync();

        if (version == null || string.IsNullOrEmpty(version.Signature))
            return NotFound("Whitelist není podepsaný.");

        return Content(version.Signature, "text/plain; charset=utf-8");
    }

    // --------------------------------------------------------
    // GET /api/whitelist/version
    // Rychlá kontrola verze bez stažení celého whitelistu
    // --------------------------------------------------------
    [HttpGet("version")]
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "USBGuardianClients")]
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
    [Microsoft.AspNetCore.Authorization.Authorize(Policy = "USBGuardianAdmins")]
    public async Task<IActionResult> AddDevice([FromBody] WhitelistDeviceDto dto)
    {
        // Kontrola duplicity
        var exists = await _db.WhitelistDevices.AnyAsync(d =>
            d.VendorId    == dto.VendorId &&
            d.ProductId   == dto.ProductId &&
            d.SerialNumber == dto.SerialNumber &&
            d.IsActive);

        if (exists)
        {
            _dennik.Log("whitelist",
                $"přidání zařízení {dto.VendorId}:{dto.ProductId}:{dto.SerialNumber} odmítnuto – už je na whitelistu",
                ActivityLevel.Warn, user: dto.ApprovedBy);
            return Conflict("Zařízení je již na whitelistu.");
        }

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
        _dennik.Log("whitelist",
            $"přidáno zařízení {dto.VendorId}:{dto.ProductId}:{dto.SerialNumber} ({dto.Description}), nová verze",
            ActivityLevel.Info, user: dto.ApprovedBy);

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
