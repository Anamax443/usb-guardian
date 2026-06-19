// ============================================================
// Program.cs  –  AGENT (klientský stroj)
// Vstupní bod aplikace – konfigurace a DI kontejner.
// Spouští se jako Windows Service nebo konzolová aplikace
// (přepínání dle přítomnosti --console argumentu).
//
// Log formát: HH:mm:ss [KLIENT] info: USBGuardian.DeviceMonitor[0]
// ============================================================

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using USBGuardian;
using USBGuardian.LocalConsole;
using USBGuardian.Security;

var builder = Host.CreateApplicationBuilder(args);

// ── Konfigurace ──────────────────────────────────────────────
var exeDir     = AppContext.BaseDirectory;
var configPath = Path.Combine(exeDir, "Config", "agent.config.json");

builder.Configuration
    .SetBasePath(exeDir)
    .AddJsonFile(configPath, optional: false, reloadOnChange: true)
    .AddJsonFile(Path.Combine(exeDir, "Config", "agent.config.local.json"),
        optional: true, reloadOnChange: true);

// ── Logování ─────────────────────────────────────────────────
// Vlastní formatter přidává [KLIENT] za timestamp:
//   16:01:33 [KLIENT] info: USBGuardian.DeviceMonitor[0]
builder.Logging
    .ClearProviders()
    .AddConsole(o => o.FormatterName = "role-tag")
    .AddConsoleFormatter<RoleTagFormatter, RoleTagFormatterOptions>(o =>
    {
        o.RoleTag         = "[KLIENT]";
        o.TimestampFormat = "HH:mm:ss ";
    })
    .SetMinimumLevel(LogLevel.Information);

// ── Závislosti (DI) ──────────────────────────────────────────
builder.Services.AddSingleton(sp =>
{
    var config  = builder.Configuration;
    var logger  = sp.GetRequiredService<ILogger<SignatureVerifier>>();
    var keyCfg  = config["signing:publicKeyPath"] ?? Path.Combine("Config", "whitelist_public.pem");
    // Relativní cestu bereme vůči složce exe (ne vůči CWD – služba má CWD System32!).
    var keyPath = Path.IsPathRooted(keyCfg) ? keyCfg : Path.Combine(exeDir, keyCfg);
    return new SignatureVerifier(logger, keyPath);
});

builder.Services.AddSingleton(sp =>
{
    var config         = builder.Configuration;
    var logger         = sp.GetRequiredService<ILogger<WhitelistChecker>>();
    var wlPath         = config["whitelist:localPath"]
                         ?? @"C:\ProgramData\USBGuardian\whitelist\whitelist.json";
    var allowWildcards = bool.Parse(config["whitelist:allowWildcards"] ?? "false");
    var sigEnabled     = bool.Parse(config["signing:enabled"] ?? "true");
    var sigVerifier    = sigEnabled ? sp.GetRequiredService<SignatureVerifier>() : null;
    return new WhitelistChecker(logger, wlPath, allowWildcards, sigEnabled, sigVerifier);
});

builder.Services.AddSingleton(sp =>
{
    var config            = builder.Configuration;
    var logger            = sp.GetRequiredService<ILogger<IncidentLogger>>();
    var queuePath         = config["logging:queuePath"]
                            ?? @"C:\ProgramData\USBGuardian\queue";
    var sentPath          = config["logging:sentPath"]
                            ?? @"C:\ProgramData\USBGuardian\sent";
    var sentRetentionDays = int.Parse(config["logging:sentRetentionDays"] ?? "90");
    return new IncidentLogger(logger, queuePath, sentPath, sentRetentionDays);
});

builder.Services.AddSingleton(sp =>
{
    var config  = builder.Configuration;
    var logger  = sp.GetRequiredService<ILogger<NotificationService>>();
    var enabled = bool.Parse(config["notifications:toast:enabled"] ?? "true");
    var contact = config["notifications:toast:contactMessage"] ?? "Kontaktujte IT oddeleni";
    return new NotificationService(logger, enabled, contact);
});

builder.Services.AddSingleton(sp =>
{
    var logger = sp.GetRequiredService<ILogger<DeviceBlocker>>();
    return new DeviceBlocker(logger);
});

// Sdílený stav vynucování: server enforce (heartbeat) + lokální break-glass override (perzistovaný).
builder.Services.AddSingleton(sp => new PolicyState(
    builder.Configuration["policy:overridePath"] ?? @"C:\ProgramData\USBGuardian\override.json"));

builder.Services.AddSingleton(sp =>
{
    var config  = builder.Configuration;
    var logger  = sp.GetRequiredService<ILogger<PolicyEnforcer>>();
    var notif   = sp.GetRequiredService<NotificationService>();
    var iLogger = sp.GetRequiredService<IncidentLogger>();
    var blocker = sp.GetRequiredService<DeviceBlocker>();
    var policy  = sp.GetRequiredService<PolicyState>();
    var mode    = config["policy:mode"] ?? "warn";
    var expired = config["policy:onExpiredWhitelist"] ?? "warn";
    var contact = config["notifications:toast:contactMessage"] ?? "Kontaktujte IT oddeleni";
    return new PolicyEnforcer(logger, notif, iLogger, blocker, policy, mode, expired, contact);
});

// DeviceMonitor jako singleton + hosted service (sdílený stav čte lokální konzole)
builder.Services.AddSingleton<DeviceMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DeviceMonitor>());

// Sdílený flush signál: heartbeat (WhitelistSync) → okamžitý sync incidentů (IncidentSync)
builder.Services.AddSingleton<SyncSignals>();

// ── Lokální admin konzole (loopback, read-only) ──────────────
// Výchozí VYPNUTO – minimální attack surface (NIS2). Zapnout přes
// agent.config.local.json: { "localConsole": { "enabled": true } }
if (bool.Parse(builder.Configuration["localConsole:enabled"] ?? "false"))
{
    builder.Services.AddHostedService(sp => new LocalConsoleService(
        sp.GetRequiredService<ILogger<LocalConsoleService>>(),
        sp.GetRequiredService<DeviceMonitor>(),
        sp.GetRequiredService<WhitelistChecker>(),
        sp.GetRequiredService<IncidentLogger>(),
        sp.GetRequiredService<PolicyState>(),
        sp.GetRequiredService<DeviceBlocker>(),
        builder.Configuration["policy:mode"] ?? "warn",
        int.Parse(builder.Configuration["localConsole:port"] ?? "5080")));

    Console.WriteLine(
        $"Lokální konzole aktivní → http://127.0.0.1:{builder.Configuration["localConsole:port"] ?? "5080"}/ (admin-only)");
}

// ── Sync services ────────────────────────────────────────────
var syncUrl = builder.Configuration["whitelist:syncUrl"] ?? string.Empty;

if (!string.IsNullOrEmpty(syncUrl))
{
    var validateTls = bool.Parse(
        builder.Configuration["tls:validateServerCertificate"] ?? "true");
    var pinnedThumbprint = builder.Configuration["tls:pinnedThumbprint"] ?? string.Empty;

    if (!string.IsNullOrWhiteSpace(pinnedThumbprint))
        Console.WriteLine($"TLS pinning aktivní – ověřuji otisk API certu ({pinnedThumbprint})");
    else if (!validateTls)
        Console.WriteLine("VAROVÁNÍ: TLS validace certifikátu je VYPNUTA (pouze pro vývoj!)");

    builder.Services.AddHostedService(sp =>
    {
        var config   = builder.Configuration;
        var logger   = sp.GetRequiredService<ILogger<WhitelistSync>>();
        var wlPath   = config["whitelist:localPath"]
                       ?? @"C:\ProgramData\USBGuardian\whitelist\whitelist.json";
        var interval = int.Parse(config["sync:whitelistSyncIntervalMinutes"] ?? "2");
        var signals  = sp.GetRequiredService<SyncSignals>();
        return new WhitelistSync(logger, syncUrl, wlPath, interval, validateTls, pinnedThumbprint, signals,
            sp.GetRequiredService<PolicyState>(),
            sp.GetRequiredService<WhitelistChecker>(),
            sp.GetRequiredService<DeviceBlocker>());
    });

    builder.Services.AddHostedService(sp =>
    {
        var config   = builder.Configuration;
        var logger   = sp.GetRequiredService<ILogger<IncidentSync>>();
        var iLogger  = sp.GetRequiredService<IncidentLogger>();
        var interval = int.Parse(config["sync:incidentSyncIntervalMinutes"] ?? "1");
        var signals  = sp.GetRequiredService<SyncSignals>();
        return new IncidentSync(logger, syncUrl, iLogger, interval, validateTls, pinnedThumbprint, signals);
    });

    Console.WriteLine($"Sync aktivní → {syncUrl}");
}
else
{
    Console.WriteLine("Sync vypnut (whitelist:syncUrl není nastaven)");
}

// ── Spuštění ─────────────────────────────────────────────────
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
