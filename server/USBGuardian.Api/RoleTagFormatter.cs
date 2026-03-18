// ============================================================
// RoleTagFormatter.cs
// Vlastní konzolový formatter pro .NET ILogger.
// Přidává roli ([KLIENT] / [SERVER]) za timestamp, aby bylo
// na první pohled jasné, z které části systému log pochází.
//
// Výstup:
//   16:01:33 [KLIENT] info: USBGuardian.DeviceMonitor[0]
//             Spárováno (DiskDrive čekal): drive letter F: → ...
//
// Použití v Program.cs:
//   builder.Logging
//       .AddConsole(o => o.FormatterName = "role-tag")
//       .AddConsoleFormatter<RoleTagFormatter, RoleTagFormatterOptions>(o =>
//       {
//           o.RoleTag        = "[KLIENT]";   // nebo "[SERVER]"
//           o.TimestampFormat = "HH:mm:ss ";
//           o.SingleLine      = false;
//       });
// ============================================================

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

/// <summary>
/// Nastavení pro RoleTagFormatter.
/// </summary>
public class RoleTagFormatterOptions : ConsoleFormatterOptions
{
    /// <summary>
    /// Štítek role zobrazený za timestampem. Např. "[KLIENT]" nebo "[SERVER]".
    /// </summary>
    public string RoleTag { get; set; } = "[APP]";
}

/// <summary>
/// Konzolový formatter přidávající tag role do každého řádku logu.
/// Zaregistrovat jako: AddConsoleFormatter&lt;RoleTagFormatter, RoleTagFormatterOptions&gt;
/// s FormatterName = "role-tag".
/// </summary>
public sealed class RoleTagFormatter : ConsoleFormatter, IDisposable
{
    // Barvy úrovní – konzistentní s výchozím .NET chováním
    private static readonly Dictionary<LogLevel, ConsoleColor> LevelColors = new()
    {
        [LogLevel.Trace]       = ConsoleColor.Gray,
        [LogLevel.Debug]       = ConsoleColor.Gray,
        [LogLevel.Information] = ConsoleColor.DarkGreen,
        [LogLevel.Warning]     = ConsoleColor.Yellow,
        [LogLevel.Error]       = ConsoleColor.Red,
        [LogLevel.Critical]    = ConsoleColor.DarkRed,
    };

    private readonly IDisposable?              _optionsReloadToken;
    private          RoleTagFormatterOptions   _opts;

    public RoleTagFormatter(IOptionsMonitor<RoleTagFormatterOptions> options)
        : base("role-tag")
    {
        _opts               = options.CurrentValue;
        _optionsReloadToken = options.OnChange(o => _opts = o);
    }

    /// <inheritdoc/>
    public override void Write<TState>(
        in LogEntry<TState>   logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter            textWriter)
    {
        // ── Timestamp ────────────────────────────────────────
        var now = _opts.UseUtcTimestamp
            ? DateTimeOffset.UtcNow
            : DateTimeOffset.Now;

        if (!string.IsNullOrEmpty(_opts.TimestampFormat))
            textWriter.Write(now.ToString(_opts.TimestampFormat));

        // ── Role tag ─────────────────────────────────────────
        textWriter.Write(_opts.RoleTag);
        textWriter.Write(' ');

        // ── Úroveň logu ──────────────────────────────────────
        var levelLabel = logEntry.LogLevel switch
        {
            LogLevel.Trace       => "trce",
            LogLevel.Debug       => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning     => "warn",
            LogLevel.Error       => "fail",
            LogLevel.Critical    => "crit",
            _                    => "    "
        };
        textWriter.Write($"{levelLabel}: ");

        // ── Kategorie a EventId ───────────────────────────────
        textWriter.Write(logEntry.Category);
        textWriter.Write('[');
        textWriter.Write(logEntry.EventId.Id);
        textWriter.WriteLine(']');

        // ── Zpráva ────────────────────────────────────────────
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (!string.IsNullOrEmpty(message))
        {
            textWriter.Write("      ");   // odsazení pod kategorii
            textWriter.WriteLine(message);
        }

        // ── Exception ─────────────────────────────────────────
        if (logEntry.Exception is not null)
        {
            textWriter.Write("      ");
            textWriter.WriteLine(logEntry.Exception.ToString());
        }
    }

    public void Dispose() => _optionsReloadToken?.Dispose();
}
