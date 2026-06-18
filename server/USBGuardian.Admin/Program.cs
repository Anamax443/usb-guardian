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

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAssertion(ctx =>
        {
            if (devAllowAll) return true;
            if (ctx.User.Identity is not WindowsIdentity { IsAuthenticated: true } wi) return false;

            // 1) Whitelist uživatelů – "DOMENA\user" nebo holé "user" (case-insensitive)
            var name = wi.Name ?? string.Empty;
            var sam  = name.Contains('\\') ? name[(name.IndexOf('\\') + 1)..] : name;
            if (allowedUsers.Any(u =>
                    u.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    u.Equals(sam,  StringComparison.OrdinalIgnoreCase)))
                return true;

            // 2) Členství v admin AD skupině
            var principal = new WindowsPrincipal(wi);
            return adminGroups.Any(principal.IsInRole);
        })
        .Build();
});
builder.Services.AddCascadingAuthenticationState();

// ── AD sync (server natáhne počítače z AD a zapíše do Computers) ─
// Default vypnuto; zapnout v appsettings.local.json. Vyžaduje write
// na Computers (účet služby) – viz least-privilege grant.
if (bool.Parse(builder.Configuration["AdSync:Enabled"] ?? "false"))
{
    builder.Services.AddHostedService(sp => new AdSyncService(
        sp.GetRequiredService<ILogger<AdSyncService>>(),
        sp.GetRequiredService<IDbContextFactory<AppDbContext>>(),
        builder.Configuration["AdSync:SearchBase"] ?? string.Empty,
        int.Parse(builder.Configuration["AdSync:IntervalMinutes"] ?? "60"),
        bool.Parse(builder.Configuration["AdSync:IncludeDisabled"] ?? "false")));
}

// ── Blazor Server ─────────────────────────────────────────────
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();

await app.RunAsync();
