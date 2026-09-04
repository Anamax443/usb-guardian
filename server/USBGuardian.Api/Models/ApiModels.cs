// ============================================================
// ApiModels.cs
// DTO modely pro REST API komunikaci agent ↔ server
// ============================================================

namespace USBGuardian.Api.Models;

// ── Agent → Server: odeslání incidentů ───────────────────────
public class IncidentBatchRequest
{
    /// <summary>Hostname odesílajícího agenta</summary>
    public string Hostname { get; set; } = string.Empty;

    /// <summary>Verze agenta</summary>
    public string AgentVersion { get; set; } = string.Empty;

    /// <summary>Název zdrojového souboru – pro audit trail (log_HOSTNAME_2026-03-15.json)</summary>
    public string SourceFile { get; set; } = string.Empty;

    /// <summary>Seznam incidentů (batch pro úsporu požadavků)</summary>
    public List<IncidentDto> Incidents { get; set; } = new();
}

public class IncidentDto
{
    public DateTime  Timestamp        { get; set; }
    /// <summary>Čas odpojení média. NULL = stále připojeno nebo neznámo.</summary>
    public DateTime? DisconnectedAt   { get; set; }
    public string   Username         { get; set; } = string.Empty;
    public string   VendorId         { get; set; } = string.Empty;
    public string   ProductId        { get; set; } = string.Empty;
    public string   SerialNumber     { get; set; } = string.Empty;
    public string   FriendlyName     { get; set; } = string.Empty;
    public string   DeviceType       { get; set; } = string.Empty;
    public long     SizeBytes        { get; set; }
    public string   FirmwareRevision { get; set; } = string.Empty;
    public string   PnpDeviceId      { get; set; } = string.Empty;
    public string   Action           { get; set; } = string.Empty;
    public string   WhitelistVersion { get; set; } = string.Empty;

    /// <summary>Název zdrojového souboru – pro audit trail</summary>
    public string   SourceFile       { get; set; } = string.Empty;
}

// ── Server → Agent: whitelist ─────────────────────────────────
public class WhitelistResponse
{
    public string Version { get; set; } = string.Empty;
    public DateTime IssuedAt { get; set; }
    public DateTime ValidUntil { get; set; }
    public string Signature { get; set; } = string.Empty;
    public List<WhitelistDeviceDto> Devices { get; set; } = new();
}

public class WhitelistDeviceDto
{
    public string VendorId { get; set; } = string.Empty;
    public string ProductId { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime ApprovedAt { get; set; }
    public string ApprovedBy { get; set; } = string.Empty;
}

// ── Server → Agent: heartbeat odpověď ────────────────────────
public class HeartbeatResponse
{
    /// <summary>Aktuální verze whitelistu na serveru</summary>
    public string CurrentWhitelistVersion { get; set; } = string.Empty;

    /// <summary>True pokud agent má zastaralý whitelist</summary>
    public bool WhitelistUpdateAvailable { get; set; }

    /// <summary>True pokud konzole vyžádala okamžité odevzdání dat (operátor klikl „Vyžádat data").
    /// Agent na to reaguje okamžitým flushem fronty incidentů. Jednorázové (viz HeartbeatController).</summary>
    public bool ReportNow { get; set; }

    /// <summary>Vynucování (centrální politika APP_SERVER, AppSettings policy.enforce): true = agent blokuje
    /// neschválená média, false = jen varuje. Agent ho aplikuje přes PolicyState (Fáze 2).</summary>
    public bool Enforce { get; set; }

    /// <summary>Serverový čas pro sync</summary>
    public DateTime ServerTime { get; set; } = DateTime.UtcNow;
}
