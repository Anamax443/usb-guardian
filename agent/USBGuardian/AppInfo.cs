using System.Reflection;

namespace USBGuardian;

/// <summary>Informace o buildu agenta – commit hash zaražený MSBuildem z gitu.
/// Hlásí se na server (heartbeat/incidenty) → vidět v konzoli „Agent verze".</summary>
public static class AppInfo
{
    public static string Commit { get; } = Resolve();

    private static string Resolve()
    {
        var asm  = Assembly.GetExecutingAssembly();
        var meta = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
                      .FirstOrDefault(a => a.Key == "GitCommit");
        if (!string.IsNullOrWhiteSpace(meta?.Value))
            return meta!.Value!.Trim();

        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(info) && info.Contains('+'))
        {
            var sha = info[(info.IndexOf('+') + 1)..];
            return sha.Length > 7 ? sha[..7] : sha;
        }
        return "dev";
    }
}
