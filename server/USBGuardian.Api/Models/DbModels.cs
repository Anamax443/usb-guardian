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
    public DateTime LastSeen { get; set; }
    public string AgentVersion { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
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
    public string Signature { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

[Table("Incidents")]
public class Incident
{
    [Key] public int Id { get; set; }
    public DateTime Timestamp { get; set; }
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
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public string? SourceIp { get; set; }
}
