// ============================================================
// Program.cs  –  SERVER (API)
// ASP.NET Core API server – konfigurace pipeline, DI, Kestrel.
// Spouští se jako Windows Service nebo konzolová aplikace.
//
// Log formát: HH:mm:ss [SERVER] info: USBGuardian.API.XyzController[0]
// ============================================================

using System.Security.Principal;
using Microsoft.AspNetCore.Authorization;
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

// Policy "USBGuardianClients" – přístup k API jen pro členy AD skupin z konfigurace
var allowedGroups = builder.Configuration.GetSection("Authorization:AllowedGroups").Get<string[]>()
                    ?? Array.Empty<string>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("USBGuardianClients", policy => policy.RequireAssertion(ctx =>
    {
        if (ctx.User.Identity is not WindowsIdentity { IsAuthenticated: true } wi) return false;
        if (allowedGroups.Length == 0) return true;            // bez konfigurace nepřekážet
        var principal = new WindowsPrincipal(wi);
        return allowedGroups.Any(principal.IsInRole);
    }));
});

// ── Self-contained TLS (vlastní self-signed cert, bez CA / cert store) ──
var tlsCertPath = builder.Configuration["tls:certPath"] ?? @"C:\ProgramData\USBGuardian\api-tls.pfx";
var tlsCert     = USBGuardian.Api.SelfCert.LoadOrCreate(tlsCertPath, Environment.MachineName);
builder.WebHost.ConfigureKestrel(o =>
{
    o.ListenAnyIP(5050);                                       // HTTP (přechodně; zvážit zavřít)
    o.ListenAnyIP(5443, listen => listen.UseHttps(tlsCert));  // HTTPS – self-cert
});

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

// Verze buildu (commit) – ať jde ověřit, co je nasazené (symetrické s konzolí)
var apiStartedAt = DateTime.UtcNow;
app.MapGet("/api/version", () => Results.Json(new
{
    commit    = USBGuardian.Api.AppInfo.Commit,
    startedAt = apiStartedAt
})).AllowAnonymous();

// Otisk TLS certu pro pinning na agentech (veřejná informace)
app.MapGet("/api/cert-info", () => Results.Json(new
{
    thumbprint = tlsCert.Thumbprint,
    subject    = tlsCert.Subject,
    notAfter   = tlsCert.NotAfter
})).AllowAnonymous();

app.Logger.LogWarning(
    "=== API TLS self-cert PIN (thumbprint pro agenty): {Tp} | platí do {Exp} ===",
    tlsCert.Thumbprint, tlsCert.NotAfter);

// ── Migrace DB při startu ─────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
}

await app.RunAsync();
