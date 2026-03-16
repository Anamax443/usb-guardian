// ============================================================
// Program.cs – USB Guardian REST API Server
// Běží jako Windows Service pod gMSA účtem (konfigurovatelný)
// Windows Authentication – agents se autentizují strojem
// Databáze je vytvořena SQL skripty (database/ složka)
// Žádné hardcoded hodnoty – vše v appsettings.json
// ============================================================

using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;

var builder = WebApplication.CreateBuilder(args);

// ── Načtení lokálního přepisu (NECOMMITUJE SE) ───────────────
// Explicitní cesta – funguje i jako Windows Service kde working dir je jiný
var exeDir = AppContext.BaseDirectory;
builder.Configuration
    .SetBasePath(exeDir)
    .AddJsonFile(Path.Combine(exeDir, "appsettings.json"), optional: false, reloadOnChange: true)
    .AddJsonFile(Path.Combine(exeDir, "appsettings.local.json"), optional: true, reloadOnChange: true);

// ── Windows Service hosting ───────────────────────────────────
builder.Services.AddWindowsService(o => o.ServiceName = "USB Guardian API");

// ── SQL Server – Windows Authentication ──────────────────────
// Heslo není součástí connection stringu – Integrated Security
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sql => sql.CommandTimeout(30)));

// ── Controllers + Swagger ─────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new()
    {
        Title       = "USB Guardian API",
        Version     = "v1",
        Description = "REST API pro synchronizaci whitelistu a sběr incidentů"
    });
});

// ── Windows Authentication ────────────────────────────────────
builder.Services.AddAuthentication(
    Microsoft.AspNetCore.Authentication.Negotiate.NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate();

// ── Authorization – AD skupiny z konfigurace ─────────────────
// Skupiny jsou definovány v appsettings.json → Authorization:AllowedGroups
// Žádné hardcoded názvy domén ani skupin
var allowedGroups = builder.Configuration
    .GetSection("Authorization:AllowedGroups")
    .Get<string[]>() ?? Array.Empty<string>();

if (allowedGroups.Length == 0)
{
    Console.WriteLine("VAROVÁNÍ: Authorization:AllowedGroups není nakonfigurováno!");
    Console.WriteLine("Nastavte skupiny v appsettings.local.json");
}

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("USBGuardianClients", policy =>
        policy.RequireAssertion(ctx =>
        {
            // Získáme všechny skupiny z claims
            var userGroups = ctx.User.Claims
                .Where(c => c.Type == System.Security.Claims.ClaimTypes.GroupSid
                         || c.Type == "http://schemas.microsoft.com/ws/2008/06/identity/claims/groupsid"
                         || c.Type == System.Security.Claims.ClaimTypes.Role)
                .Select(c => c.Value)
                .ToList();

            // Zkusíme i IsInRole pro každou skupinu
            return allowedGroups.Any(group =>
                ctx.User.IsInRole(group) ||
                ctx.User.HasClaim(System.Security.Claims.ClaimTypes.Role, group));
        }));
});

// ── Logging ───────────────────────────────────────────────────
builder.Logging.AddEventLog(settings =>
{
    settings.SourceName = "USB Guardian API";
});

var app = builder.Build();

// ── Middleware pipeline ───────────────────────────────────────
app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// ── Debug endpoint – zobrazí skupiny přihlášeného uživatele ──
// Použít pro diagnostiku autorizace, v produkci odstranit
app.MapGet("/api/debug/whoami", (System.Security.Claims.ClaimsPrincipal user) =>
{
    var identity = user.Identity;
    return new
    {
        Name            = identity?.Name,
        IsAuthenticated = identity?.IsAuthenticated,
        Claims          = user.Claims.Select(c => new { c.Type, c.Value }).ToList(),
        AllowedGroups   = allowedGroups,
        IsAuthorized    = allowedGroups.Any(g => user.IsInRole(g))
    };
}).RequireAuthorization();

// ── Info při startu ───────────────────────────────────────────
var connStr = builder.Configuration.GetConnectionString("DefaultConnection") ?? "";
var server  = connStr.Split(';')
    .FirstOrDefault(s => s.TrimStart().StartsWith("Server=", StringComparison.OrdinalIgnoreCase))
    ?.Split('=').LastOrDefault() ?? "neznámý";

Console.WriteLine($"USB Guardian API startuje");
Console.WriteLine($"  SQL Server:      {server}");
Console.WriteLine($"  Povolené skupiny: {string.Join(", ", allowedGroups)}");

await app.RunAsync();
