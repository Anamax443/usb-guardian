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

// ── Fronta incidentů (controller zařadí batch → worker zapisuje do DB async) ──
// BEZ TÉTO REGISTRACE: IncidentsController nejde postavit (DI) → 500 na /api/incidents.
// Deník aktivity. Vlastní továrna na kontext schválně: zápis do deníku běží
// MIMO požadavek (fire-and-forget), takže si nesmí půjčovat scoped kontext,
// který mu pod rukama zmizí, až požadavek skončí.
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")),
    lifetime: ServiceLifetime.Singleton);
builder.Services.AddSingleton<ActivityLogger>();

builder.Services.AddSingleton<USBGuardian.Api.Queue.IncidentQueue>();
builder.Services.AddHostedService<USBGuardian.Api.Queue.IncidentQueueWorker>();

// ── Retence dat: úklid starých incidentů dle AppSettings (retention.*) ──
// API maže (db_datawriter); nastavení spravuje konzole. Default vypnuto.
builder.Services.AddHostedService<USBGuardian.Api.Retention.RetentionService>();

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
// Policy "USBGuardianAdmins" – zápisové/administrativní endpointy (např. přidání zařízení
// na whitelist přes API). Záměrně SAMOSTATNÁ od USBGuardianClients: agent smí číst
// (heartbeat, stažení whitelistu), ale nemá důvod umět zapisovat security policy – účet
// stanice v USB-Guardian-Clients dřív procházel i na POST /api/whitelist/devices.
var adminGroups = builder.Configuration.GetSection("Authorization:AdminGroups").Get<string[]>()
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

    // Fail-closed záměrně (na rozdíl od USBGuardianClients výše): tohle je nová, admin-only
    // cesta bez existujícího nasazení, které by se prázdnou konfigurací mohlo rozbít. Prázdné
    // AdminGroups tedy znamená "nikdo", ne "kdokoli" – dokud si to operátor nenastaví sám.
    options.AddPolicy("USBGuardianAdmins", policy => policy.RequireAssertion(ctx =>
    {
        if (ctx.User.Identity is not WindowsIdentity { IsAuthenticated: true } wi) return false;
        if (adminGroups.Length == 0) return false;
        var principal = new WindowsPrincipal(wi);
        return adminGroups.Any(principal.IsInRole);
    }));
});

// appsettings.local.json na serveru může nést vlastní "Kestrel:Endpoints:*" - Kestrel tuhle
// sekci čte NEZÁVISLE na ConfigureKestrel níž a oba zdroje se SČÍTAJÍ (config otevře port
// navíc, ne nahradí kód). Reálný appsettings.local.json na SQL_SERVER měl "Http": "http://0.0.0.0:5050" -
// bez týhle pojistky by port 5050 v produkci zůstal otevřený i po fixu níž.
//
// Vynulovat tu hodnotu v kódu (`builder.Configuration["...:Url"] = null`) NEFUNGUJE -
// ověřeno izolovaným testem: klíč zůstane v konfiguraci "existovat" (jen s null hodnotou),
// Kestrel proto pořád najde endpoint "Http" a spadne na "missing required Url parameter" -
// to by v produkci znamenalo, že se API vůbec nerozjede. Fail-fast s jasnou hláškou je proto
// spolehlivější než tichý pokus config obejít.
if (!builder.Environment.IsDevelopment()
    && builder.Configuration.GetSection("Kestrel:Endpoints:Http").Exists())
{
    throw new InvalidOperationException(
        "appsettings.local.json definuje Kestrel:Endpoints:Http (nešifrované HTTP) - " +
        "v produkci NENÍ povoleno (NIS2). Odeber tuhle sekci z appsettings.local.json na serveru.");
}

// ── Self-contained TLS (vlastní self-signed cert, bez CA / cert store) ──
var tlsCertPath = builder.Configuration["tls:certPath"] ?? @"C:\ProgramData\USBGuardian\api-tls.pfx";
var tlsCert     = USBGuardian.Api.SelfCert.LoadOrCreate(tlsCertPath, Environment.MachineName);
builder.WebHost.ConfigureKestrel(o =>
{
    // HTTP jen ve vývoji (Windows služba v produkci ASPNETCORE_ENVIRONMENT nenastavuje,
    // takže defaultně Production) – incidenty, hostname, sériová čísla i policy metadata
    // by jinak šla po síti nešifrovaně. NIS2. Stejný vzor jako Swagger níž.
    if (builder.Environment.IsDevelopment())
        o.ListenAnyIP(5050);                                   // HTTP – jen dev
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
