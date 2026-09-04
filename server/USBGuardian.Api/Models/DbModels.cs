// ============================================================
// DbModels.cs
// Entity Framework modely mapující SQL Server tabulky
// ============================================================

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace USBGuardian.Api.Models;

[Table("Computers")]
public class Computer
{
    [Key] public int Id { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string? Domain { get; set; }

    /// <summary>Poslední kontakt agenta. NULL = agent zatím nereportoval (řádek může pocházet jen z AD syncu).</summary>
    public DateTime? LastSeen { get; set; }
    public string AgentVersion { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ── AD sync (doplňuje serverová konzole na APP_SERVER) ──
    /// <summary>OS z Active Directory.</summary>
    public string? OperatingSystem { get; set; }
    /// <summary>Nalezen v posledním AD syncu (false = už v AD není).</summary>
    public bool InActiveDirectory { get; set; }
    /// <summary>Čas posledního AD syncu tohoto záznamu.</summary>
    public DateTime? AdSyncedAt { get; set; }
    /// <summary>Cesta (OU) v Active Directory, např. "AXIMA / Computers".</summary>
    public string? AdPath { get; set; }

    // ── Nasazování agenta (úmysl operátora) ──
    /// <summary>
    /// Trvale vyřazeno z nasazování agenta ("Ignorovat"). Má PŘEDNOST před
    /// include seznamem i před výchozí volbou. Hromadná tlačítka "Zařadit vše" /
    /// "Vyřadit vše" se tohohle příznaku ZÁMĚRNĚ nedotýkají – jinak by se ručně
    /// vyřazená stanice po první hromadné akci vrátila mezi cíle.
    /// </summary>
    public bool DeployIgnored { get; set; }
    /// <summary>Kdo stanici vyřadil – bez toho se za půl roku nikdo nedozví proč.</summary>
    public string? DeployIgnoredBy { get; set; }
    public DateTime? DeployIgnoredAt { get; set; }
}

[Table("AppSettings")]
public class AppSetting
{
    [Key] public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

[Table("WhitelistDevices")]
public class WhitelistDevice
{
    [Key] public int Id { get; set; }
    public string VendorId { get; set; } = string.Empty;
    public string ProductId { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ApprovedBy { get; set; } = string.Empty;
    public DateTime ApprovedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
}

[Table("WhitelistVersions")]
public class WhitelistVersion
{
    [Key] public int Id { get; set; }
    public string Version { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public DateTime ValidUntil { get; set; }
    public string IssuedBy { get; set; } = string.Empty;
    /// <summary>RSA-4096 podpis blobu (base64, ~684 znaků) – NVARCHAR(MAX). Prázdné = čeká na podpis.</summary>
    public string Signature { get; set; } = string.Empty;
    /// <summary>Kanonický whitelist.json BLOB, který se podepisuje a servíruje agentům VERBATIM
    /// (podpis musí sedět bajt na bajt). NVARCHAR(MAX) – pojme i 10k zařízení (~MB).</summary>
    public string Json { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

[Table("ActivityLog")]
public class ActivityEntry
{
    [Key] public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    /// <summary>info / warn / error – aby šel odfiltrovat šum od toho, co hoří.</summary>
    public string Level { get; set; } = "info";
    /// <summary>heartbeat / incidents / whitelist / deploy / settings / adsync …</summary>
    public string Source { get; set; } = string.Empty;
    /// <summary>Které stanice se to týká. NULL = serverová akce.</summary>
    public string? Hostname { get; set; }
    /// <summary>Kdo to vyvolal. NULL = automatika.</summary>
    [Column("User")] public string? User { get; set; }
    public string Message { get; set; } = string.Empty;
}

[Table("Incidents")]
public class Incident
{
    [Key] public int Id { get; set; }
    public DateTime  Timestamp      { get; set; }
    /// <summary>Čas odpojení média. NULL = stále připojeno nebo neznámo.</summary>
    public DateTime? DisconnectedAt { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public int? ComputerId { get; set; }
    public string VendorId { get; set; } = string.Empty;
    public string ProductId { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string FirmwareRevision { get; set; } = string.Empty;
    public string PnpDeviceId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string WhitelistVersion { get; set; } = string.Empty;

    /// <summary>Název zdrojového souboru – audit trail (log_HOSTNAME_2026-03-15.json)</summary>
    public string? SourceFile { get; set; }

    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public string? SourceIp { get; set; }
}
