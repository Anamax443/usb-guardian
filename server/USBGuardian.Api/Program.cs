// ============================================================
// Program.cs  –  SERVER (API)
// ASP.NET Core API server – konfigurace pipeline, DI, Kestrel.
// Spouští se jako Windows Service nebo konzolová aplikace.
//
// Log formát: HH:mm:ss [SERVER] info: USBGuardian.API.XyzController[0]
// ============================================================

using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using USBGuardian.Api.Data;

var builder = WebApplication.CreateBuilder(args);

// ── Konfigurace ──────────────────────────────────────────────
// appsettings.json        – šablona s placeholdery (v repo)
// appsettings.local.json  – reálné hodnoty (gitignored, na každém serveru zvlášť)
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

// ── Logování ─────────────────────────────────────────────────
// Přidává [SERVER] za timestamp – symetrické s agentem ([KLIENT])
//   16:01:33 [SERVER] info: USBGuardian.API.IncidentController[0]
builder.Logging
    .ClearProviders()
    .AddConsole(o => o.FormatterName = "role-tag")
    .AddConsoleFormatter<RoleTagFormatter, RoleTagFormatterOptions>(o =>
    {
        o.RoleTag         = "[SERVER]";
        o.TimestampFormat = "HH:mm:ss ";
    })
    .AddEventLog(settings =>
    {
        settings.SourceName = "USB Guardian API";  // Windows Event Log (produkce)
    })
    .SetMinimumLevel(LogLevel.Information);

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

// ── Windows Authentication (Kerberos/NTLM) ───────────────────
builder.Services.AddAuthentication(
    Microsoft.AspNetCore.Authentication.Negotiate.NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate();

builder.Services.AddAuthorization();

var app = builder.Build();

// ── Middleware pipeline ───────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// ── Migrace DB při startu ─────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
}

await app.RunAsync();
