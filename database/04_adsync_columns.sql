-- ============================================================
-- 04_adsync_columns.sql
-- Rozšíření tabulky Computers pro AD sync (serverová konzole).
-- Spustit po 02_create_tables.sql (idempotentní).
-- ============================================================

USE USBGuardian;
GO

-- LastSeen nullable: NULL = agent zatím nereportoval
-- (řádek může pocházet jen z AD syncu = stanice bez agenta)
ALTER TABLE dbo.Computers ALTER COLUMN LastSeen DATETIME2 NULL;
GO

IF COL_LENGTH('dbo.Computers', 'OperatingSystem') IS NULL
    ALTER TABLE dbo.Computers ADD OperatingSystem NVARCHAR(200) NULL;
GO

IF COL_LENGTH('dbo.Computers', 'InActiveDirectory') IS NULL
    ALTER TABLE dbo.Computers ADD InActiveDirectory BIT NOT NULL CONSTRAINT DF_Computers_InAd DEFAULT 0;
GO

IF COL_LENGTH('dbo.Computers', 'AdSyncedAt') IS NULL
    ALTER TABLE dbo.Computers ADD AdSyncedAt DATETIME2 NULL;
GO

PRINT 'Computers rozšířeno o AD sync sloupce.';
GO
