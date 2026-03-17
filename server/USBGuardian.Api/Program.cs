// ============================================================
// Program.cs – USB Guardian REST API Server
// Běží jako Windows Service pod gMSA účtem (konfigurovatelný)
// Windows Authentication – agents se autentizují strojem
// Databáze je vytvořena SQL skripty (database/ složka)
// Žádné hardcoded hodnoty – vše v appsettings.json
//
// v1.1 – přidána IncidentQueue + IncidentQueueWorker
// v1.2 – HTTPS podpora přes Kestrel + self-signed certifikát
// ============================================================

using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;
using USBGuardian.Api.Queue;

var builder = WebApplication.CreateBuilder(args);

// ── Načtení lokálního přepisu (NECOMMITUJE SE) ───────────────
var exeDir = AppContext.BaseDirectory;
builder.Configuration
    .SetBasePath(exeDir)
    .AddJsonFile(Path.Combine(exeDir, "appsettings.json"),       optional: false, reloadOnChange: true)
    .AddJsonFile(Path.Combine(exeDir, "appsettings.local.json"), optional: true,  reloadOnChange: true);

// ── Kestrel HTTPS konfigurace ─────────────────────────────────
// Certifikát se načte z Windows Certificate Store (LocalMachine\My)
// podle Subject z konfigurace. Generování: scripts/New-Certificate.ps1
builder.WebHost.ConfigureKestrel((context, options) =>
{
    var kestrelSection = context.Configuration.GetSection("Kestrel");
    if (kestrelSection.Exists())
    {
        // Načíst z appsettings – podporuje Certificate Store i PFX soubor
        options.Configure(kestrelSection);
    }
    else
    {
        // Fallback: HTTP pouze (vývoj bez certifikátu)
        options.ListenAnyIP(5050, listenOptions =>
        {
            listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
        });
    }
});

// ── Windows Service hosting ───────────────────────────────────
builder.Services.AddWindowsService(o => o.ServiceName = "USB Guardian API");

// ── SQL Server – Windows Authentication ──────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sql => sql.CommandTimeout(30)));

// ── Incident Queue + Worker ───────────────────────────────────
// Singleton fronta sdílená mezi controllery a workerem
builder.Services.AddSingleton<IncidentQueue>();
// Background worker čte z fronty a zapisuje do DB
builder.Services.AddHostedService<IncidentQueueWorker>();

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

// ── Debug endpoint ────────────────────────────────────────────
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
Console.WriteLine($"  SQL Server:       {server}");
Console.WriteLine($"  Povolené skupiny: {string.Join(", ", allowedGroups)}");
Console.WriteLine($"  Incident queue:   bounded Channel (max 1000 batchů)");
Console.WriteLine($"  HTTPS:            port 5443 (HTTP fallback: 5050)");

await app.RunAsync();
