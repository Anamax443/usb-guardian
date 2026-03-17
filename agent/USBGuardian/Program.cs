// ============================================================
// Program.cs
// Vstupní bod aplikace – konfigurace a DI kontejner.
// Spouští se jako Windows Service nebo konzolová aplikace
// (přepínání dle přítomnosti --console argumentu).
// ============================================================

using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using USBGuardian;

var builder = Host.CreateApplicationBuilder(args);

// ── Konfigurace ──────────────────────────────────────────────
// Načteme agent.config.json ze složky vedle exe souboru
var exeDir = AppContext.BaseDirectory;
var configPath = Path.Combine(exeDir, "Config", "agent.config.json");

builder.Configuration
    .SetBasePath(exeDir)
    .AddJsonFile(configPath, optional: false, reloadOnChange: true)
    .AddJsonFile(Path.Combine(exeDir, "Config", "agent.config.local.json"),
        optional: true, reloadOnChange: true);    // lokální přepisy (necommitovat)

// ── Logování ─────────────────────────────────────────────────
builder.Logging
    .ClearProviders()
    .AddSimpleConsole(options =>
    {
        options.TimestampFormat = "HH:mm:ss ";  // timestamp v konzoli
        options.SingleLine      = false;
    })
    .SetMinimumLevel(LogLevel.Information);

// ── Závislosti (DI) ──────────────────────────────────────────
builder.Services.AddSingleton(sp =>
{
    var config         = builder.Configuration;
    var logger         = sp.GetRequiredService<ILogger<WhitelistChecker>>();
    var wlPath         = config["whitelist:localPath"]
                         ?? @"C:\ProgramData\USBGuardian\whitelist\whitelist.json";
    var allowWildcards = bool.Parse(
                         config["whitelist:allowWildcards"] ?? "false");
    return new WhitelistChecker(logger, wlPath, allowWildcards);
});

builder.Services.AddSingleton(sp =>
{
    var config            = builder.Configuration;
    var logger            = sp.GetRequiredService<ILogger<IncidentLogger>>();
    var queuePath         = config["logging:queuePath"]
                            ?? @"C:\ProgramData\USBGuardian\queue";
    var sentPath          = config["logging:sentPath"]
                            ?? @"C:\ProgramData\USBGuardian\sent";
    var sentRetentionDays = int.Parse(
                            config["logging:sentRetentionDays"] ?? "90");
    return new IncidentLogger(logger, queuePath, sentPath, sentRetentionDays);
});

builder.Services.AddSingleton(sp =>
{
    var config   = builder.Configuration;
    var logger   = sp.GetRequiredService<ILogger<NotificationService>>();
    var enabled  = bool.Parse(config["notifications:toast:enabled"] ?? "true");
    var contact  = config["notifications:toast:contactMessage"] ?? "Kontaktujte IT oddeleni";
    return new NotificationService(logger, enabled, contact);
});

builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<DeviceBlocker>>();
    return new DeviceBlocker(logger);
});

builder.Services.AddSingleton(sp =>
{
    var config   = builder.Configuration;
    var logger   = sp.GetRequiredService<ILogger<PolicyEnforcer>>();
    var notif    = sp.GetRequiredService<NotificationService>();
    var iLogger  = sp.GetRequiredService<IncidentLogger>();
    var blocker  = sp.GetRequiredService<DeviceBlocker>();
    var mode     = config["policy:mode"] ?? "warn";
    var expired  = config["policy:onExpiredWhitelist"] ?? "warn";
    var contact  = config["notifications:toast:contactMessage"] ?? "Kontaktujte IT oddeleni";
    return new PolicyEnforcer(logger, notif, iLogger, blocker, mode, expired, contact);
});

// Hlavní background service – WMI monitoring
builder.Services.AddHostedService<DeviceMonitor>();

// ── Sync services – pouze pokud je syncUrl nakonfigurováno ───
var syncUrl = builder.Configuration["whitelist:syncUrl"] ?? string.Empty;

if (!string.IsNullOrEmpty(syncUrl))
{
    // Synchronizace whitelistu ze serveru
    builder.Services.AddHostedService(sp =>
    {
        var config   = builder.Configuration;
        var logger   = sp.GetRequiredService<ILogger<WhitelistSync>>();
        var wlPath   = config["whitelist:localPath"]
                       ?? @"C:\ProgramData\USBGuardian\whitelist\whitelist.json";
        var interval = int.Parse(
                       config["sync:whitelistSyncIntervalMinutes"] ?? "15");
        return new WhitelistSync(logger, syncUrl, wlPath, interval);
    });

    // Odesílání incidentů na server
    builder.Services.AddHostedService(sp =>
    {
        var config        = builder.Configuration;
        var logger        = sp.GetRequiredService<ILogger<IncidentSync>>();
        var iLogger       = sp.GetRequiredService<IncidentLogger>();
        var interval      = int.Parse(
                            config["sync:incidentSyncIntervalMinutes"] ?? "1");
        return new IncidentSync(logger, syncUrl, iLogger, interval);
    });

    Console.WriteLine($"Sync aktivní → {syncUrl}");
}
else
{
    Console.WriteLine("Sync vypnut (whitelist:syncUrl není nastaven)");
}

// ── Spuštění ─────────────────────────────────────────────────
// Pokud běží jako Windows Service → UseWindowsService()
// Pokud má argument --console → spustí se jako konzolová app (pro vývoj)
if (args.Contains("--console"))
{
    Console.WriteLine("USB Guardian – konzolový režim (Ctrl+C pro ukončení)");
}
else
{
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "USB Guardian";
    });
}

var host = builder.Build();
await host.RunAsync();
