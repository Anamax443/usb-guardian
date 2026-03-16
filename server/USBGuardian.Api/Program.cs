// ============================================================
// Program.cs – USB Guardian REST API Server
// Běží jako Windows Service pod gMSA účtem AXINETWORK\gmsa-SQL$
// Windows Authentication – agents se autentizují strojem
// Databáze je vytvořena SQL skripty (database/ složka)
// ============================================================

using Microsoft.EntityFrameworkCore;
using USBGuardian.Api.Data;

var builder = WebApplication.CreateBuilder(args);

// ── Windows Service hosting ───────────────────────────────────
builder.Services.AddWindowsService(o => o.ServiceName = "USB Guardian API");

// ── SQL Server – Windows Authentication přes gMSA ────────────
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

// ── Authorization – pouze počítače v AD skupině ───────────────
// AD skupina: AXINETWORK\USB-Guardian-Clients
// Členové: Domain Computers → všechny firemní stroje automaticky
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("USBGuardianClients", policy =>
        policy.RequireAssertion(ctx =>
            // Firemní stroje přes Domain Computers
            ctx.User.IsInRole(@"AXINETWORK\USB-Guardian-Clients")
            // IT admini přes browser/Swagger
            || ctx.User.IsInRole(@"AXINETWORK\SQL Admins2")));
});

// ── Logging ───────────────────────────────────────────────────
builder.Logging.AddEventLog(settings =>
{
    settings.SourceName = "USB Guardian API";
});

var app = builder.Build();

// ── Middleware pipeline ───────────────────────────────────────
// Swagger dostupný vždy (pro vývoj a testování)
app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

await app.RunAsync();
