-- ============================================================
-- USB Guardian – tabulky
-- Spustit po 01_create_database.sql
-- ============================================================

USE USBGuardian;
GO

-- ── Tabulka: Počítače ─────────────────────────────────────────
-- Evidence firemních stanic kde běží agent
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Computers')
BEGIN
    CREATE TABLE dbo.Computers (
        Id            INT           NOT NULL IDENTITY(1,1) PRIMARY KEY,
        Hostname      NVARCHAR(100) NOT NULL,               -- název PC
        Domain        NVARCHAR(100) NULL,                   -- doména
        LastSeen      DATETIME2     NOT NULL,               -- poslední kontakt
        AgentVersion  NVARCHAR(20)  NOT NULL DEFAULT '',    -- verze agenta
        IsActive      BIT           NOT NULL DEFAULT 1,
        CreatedAt     DATETIME2     NOT NULL DEFAULT GETUTCDATE(),

        CONSTRAINT UQ_Computers_Hostname UNIQUE (Hostname)
    );
    PRINT 'Tabulka Computers vytvořena.';
END
GO

-- ── Tabulka: Whitelist ────────────────────────────────────────
-- Centrální evidence schválených paměťových médií
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'WhitelistDevices')
BEGIN
    CREATE TABLE dbo.WhitelistDevices (
        Id            INT           NOT NULL IDENTITY(1,1) PRIMARY KEY,
        VendorId      NVARCHAR(50)  NOT NULL,               -- výrobce (KINGSTON, WD...)
        ProductId     NVARCHAR(100) NOT NULL,               -- model
        SerialNumber  NVARCHAR(100) NOT NULL DEFAULT '',    -- prázdné = wildcard
        Description   NVARCHAR(500) NOT NULL DEFAULT '',    -- popis pro IT
        ApprovedBy    NVARCHAR(100) NOT NULL,               -- kdo schválil
        ApprovedAt    DATETIME2     NOT NULL DEFAULT GETUTCDATE(),
        IsActive      BIT           NOT NULL DEFAULT 1,     -- lze deaktivovat bez mazání
        Notes         NVARCHAR(1000) NULL,                  -- poznámky

        CONSTRAINT UQ_Whitelist_Device UNIQUE (VendorId, ProductId, SerialNumber)
    );
    PRINT 'Tabulka WhitelistDevices vytvořena.';
END
GO

-- ── Tabulka: Verze whitelistu ─────────────────────────────────
-- Každá změna whitelistu vytvoří novou verzi pro audit
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'WhitelistVersions')
BEGIN
    CREATE TABLE dbo.WhitelistVersions (
        Id            INT           NOT NULL IDENTITY(1,1) PRIMARY KEY,
        Version       NVARCHAR(50)  NOT NULL,               -- např. 2026-03-16-v3
        IssuedAt      DATETIME2     NOT NULL DEFAULT GETUTCDATE(),
        ValidUntil    DATETIME2     NOT NULL,               -- expirace
        IssuedBy      NVARCHAR(100) NOT NULL,               -- kdo vydal
        Signature     NVARCHAR(500) NOT NULL DEFAULT '',    -- RSA podpis (Fáze 4)
        IsActive      BIT           NOT NULL DEFAULT 1,     -- aktuální verze

        CONSTRAINT UQ_WhitelistVersions_Version UNIQUE (Version)
    );
    PRINT 'Tabulka WhitelistVersions vytvořena.';
END
GO

-- ── Tabulka: Incidenty ────────────────────────────────────────
-- Log všech pokusů o připojení nepovoleného média
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Incidents')
BEGIN
    CREATE TABLE dbo.Incidents (
        Id               INT           NOT NULL IDENTITY(1,1) PRIMARY KEY,
        -- Kdy a kde
        Timestamp        DATETIME2     NOT NULL,
        Hostname         NVARCHAR(100) NOT NULL,
        Username         NVARCHAR(100) NOT NULL,
        ComputerId       INT           NULL REFERENCES dbo.Computers(Id),
        -- Zařízení
        VendorId         NVARCHAR(50)  NOT NULL,
        ProductId        NVARCHAR(100) NOT NULL,
        SerialNumber     NVARCHAR(100) NOT NULL,
        FriendlyName     NVARCHAR(200) NOT NULL,
        DeviceType       NVARCHAR(50)  NOT NULL,
        SizeBytes        BIGINT        NOT NULL DEFAULT 0,
        FirmwareRevision NVARCHAR(50)  NOT NULL DEFAULT '',
        PnpDeviceId      NVARCHAR(500) NOT NULL DEFAULT '',
        -- Akce
        Action           NVARCHAR(50)  NOT NULL,            -- Warned / Blocked
        WhitelistVersion NVARCHAR(50)  NOT NULL,
        -- Metadata
        ReceivedAt       DATETIME2     NOT NULL DEFAULT GETUTCDATE(),
        SourceIp         NVARCHAR(50)  NULL,                -- IP agenta

        INDEX IX_Incidents_Timestamp (Timestamp DESC),
        INDEX IX_Incidents_Hostname (Hostname),
        INDEX IX_Incidents_Username (Username),
        INDEX IX_Incidents_SerialNumber (SerialNumber)
    );
    PRINT 'Tabulka Incidents vytvořena.';
END
GO

-- ── View: Přehled incidentů ───────────────────────────────────
-- Denormalizovaný pohled pro reporting a Admin UI
IF EXISTS (SELECT * FROM sys.views WHERE name = 'vw_IncidentSummary')
    DROP VIEW dbo.vw_IncidentSummary;
GO

CREATE VIEW dbo.vw_IncidentSummary AS
SELECT
    i.Id,
    i.Timestamp,
    i.Hostname,
    i.Username,
    i.VendorId + ':' + i.ProductId + ':' + i.SerialNumber AS DeviceKey,
    i.FriendlyName,
    i.DeviceType,
    CAST(i.SizeBytes / 1073741824.0 AS DECIMAL(10,1)) AS SizeGB,
    i.Action,
    i.WhitelistVersion,
    i.ReceivedAt,
    -- Je zařízení nyní na whitelistu?
    CASE WHEN EXISTS (
        SELECT 1 FROM dbo.WhitelistDevices w
        WHERE w.VendorId = i.VendorId
          AND w.ProductId = i.ProductId
          AND (w.SerialNumber = '' OR w.SerialNumber = i.SerialNumber)
          AND w.IsActive = 1
    ) THEN 1 ELSE 0 END AS IsNowWhitelisted
FROM dbo.Incidents i;
GO

PRINT 'View vw_IncidentSummary vytvořen.';
GO

-- ── Stored Procedure: Statistiky ─────────────────────────────
IF EXISTS (SELECT * FROM sys.procedures WHERE name = 'sp_GetDashboardStats')
    DROP PROCEDURE dbo.sp_GetDashboardStats;
GO

CREATE PROCEDURE dbo.sp_GetDashboardStats
    @DaysBack INT = 30
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @From DATETIME2 = DATEADD(DAY, -@DaysBack, GETUTCDATE());

    -- Celkové počty
    SELECT
        COUNT(*)                                            AS TotalIncidents,
        COUNT(DISTINCT Hostname)                            AS UniqueComputers,
        COUNT(DISTINCT Username)                            AS UniqueUsers,
        COUNT(DISTINCT VendorId + ':' + ProductId + ':' + SerialNumber) AS UniqueDevices,
        SUM(CASE WHEN Action = 'Blocked' THEN 1 ELSE 0 END) AS TotalBlocked,
        SUM(CASE WHEN Action = 'Warned'  THEN 1 ELSE 0 END) AS TotalWarned
    FROM dbo.Incidents
    WHERE Timestamp >= @From;

    -- Top 10 uživatelů s incidenty
    SELECT TOP 10
        Username,
        COUNT(*) AS IncidentCount
    FROM dbo.Incidents
    WHERE Timestamp >= @From
    GROUP BY Username
    ORDER BY IncidentCount DESC;

    -- Top 10 strojů s incidenty
    SELECT TOP 10
        Hostname,
        COUNT(*) AS IncidentCount
    FROM dbo.Incidents
    WHERE Timestamp >= @From
    GROUP BY Hostname
    ORDER BY IncidentCount DESC;
END
GO

PRINT 'Stored procedure sp_GetDashboardStats vytvořena.';
PRINT '';
PRINT 'Databáze USB Guardian je připravena.';
GO
