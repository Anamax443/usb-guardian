// ============================================================
// Program.cs  –  USB Guardian Admin Console (Blazor Server)
//
// Serverová admin konzole. Běží na app serveru (.213),
// čte SQL Server (B-S-W-SQL-04) READ-ONLY přes reusnutý
// AppDbContext z USBGuardian.Api (žádná duplikace modelů).
//
// Bezpečnost (NIS2):
//   - Windows Authentication (Kerberos/Negotiate).
//   - Přístup jen pro členy AD skupiny USB-Guardian-Admins
//     (FallbackPolicy → každá stránka chráněná).
//   - Oddělený proces/služba od ingestion API (odolnost).
// ============================================================

using System.Security.Principal;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using USBGuardian.Admin.AdSync;
using USBGuardian.Admin.Components;
using USBGuardian.Admin.Export;
using USBGuardian.Admin.Security;
using USBGuardian.Api.Data;

var builder = WebApplication.CreateBuilder(args);

// ── Konfigurace (stejný vzor jako API) ───────────────────────
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);

// ── Windows Service hosting ───────────────────────────────────
builder.Services.AddWindowsService(o => o.ServiceName = "USB Guardian Console");

// ── SQL Server (read-only pohled), DbContextFactory pro Blazor ─
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sql => sql.CommandTimeout(30)));

// ── Windows Authentication ────────────────────────────────────
builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme)
    .AddNegotiate();

// ── Autorizace – pouze AD skupina USB-Guardian-Admins ────────
// Pozn.: u Windows auth se členství spolehlivě ověřuje přes
// WindowsPrincipal.IsInRole (řeší "DOMENA\Skupina"), ne přes
// RequireRole (claim = SID). DevAllowAll = únik pro lokální test.
var adminGroups  = builder.Configuration.GetSection("Authorization:AdminGroups").Get<string[]>()
                   ?? new[] { "USB-Guardian-Admins" };
var allowedUsers = builder.Configuration.GetSection("Authorization:AllowedUsers").Get<string[]>()
                   ?? Array.Empty<string>();
var devAllowAll  = builder.Configuration.GetValue<bool>("Authorization:DevAllowAll");

// DB-spravovaný seznam přístupu (rozšiřuje config bootstrap, viz AccessCache)
builder.Services.AddSingleton<AccessCache>();

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAssertion(ctx =>
        {
            if (devAllowAll) return true;
            if (ctx.User.Identity is not WindowsIdentity { IsAuthenticated: true } wi) return false;

            var name = wi.Name ?? string.Empty;
            var sam  = name.Contains('\\') ? name[(name.IndexOf('\\') + 1)..] : name;
            var principal = new WindowsPrincipal(wi);

            bool MatchUser(string u) => u.Equals(name, StringComparison.OrdinalIgnoreCase)
                                     || u.Equals(sam, StringComparison.OrdinalIgnoreCase);

            // 1) BOOTSTRAP z appsettings – nelze vyřadit (ochrana proti lockoutu)
            if (allowedUsers.Any(MatchUser)) return true;
            if (adminGroups.Any(principal.IsInRole)) return true;

            // 2) DB-spravovaný seznam (přidává se z Nastavení)
            if (ctx.Resource is HttpContext http)
            {
                var cache = http.RequestServices.GetService<AccessCache>();
                if (cache is not null)
                {
                    if (cache.Users.Any(MatchUser)) return true;
                    if (cache.Groups.Any(principal.IsInRole)) return true;
                }
            }
            return false;
        })
        .Build();
});
builder.Services.AddCascadingAuthenticationState();

// ── AD sync ──────────────────────────────────────────────────
// Runner je volatelný i z UI ("Aktualizovat z AD"); časovač běží jen
// když AdSync:Enabled=true. Vyžaduje write na Computers (účet služby).
builder.Services.AddSingleton<AdSyncRunner>();
if (bool.Parse(builder.Configuration["AdSync:Enabled"] ?? "false"))
{
    builder.Services.AddHostedService(sp => new AdSyncService(
        sp.GetRequiredService<AdSyncRunner>(),
        sp.GetRequiredService<ILogger<AdSyncService>>(),
        int.Parse(builder.Configuration["AdSync:IntervalMinutes"] ?? "60")));
}

// ── E-mailové alerty nad incidenty (běží, jen když je e-mail zapnutý) ──
builder.Services.AddHostedService<USBGuardian.Admin.Notifications.IncidentAlertService>();

// ── Auto-enrollment agenta (běží, jen když deploy.enabled=true; default dry-run) ──
builder.Services.AddHostedService<USBGuardian.Admin.Deploy.AgentDeployService>();

// ── Blazor Server ─────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Načíst DB-spravovaný seznam přístupu (bez pádu, když tabulka ještě není)
await app.Services.GetRequiredService<AccessCache>().ReloadAsync();

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ── Kontrakt /api/version (AXIMA UI standard 2.4) ────────────
var startedAt = DateTime.UtcNow;
app.MapGet("/api/version", () => Results.Json(new
{
    commit    = USBGuardian.Admin.AppInfo.Commit,
    startedAt
})).AllowAnonymous();

// ── Export incidentů (CSV + manažerský report) – dědí FallbackPolicy ──
app.MapExportEndpoints();

app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();

await app.RunAsync();
