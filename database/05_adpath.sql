-- ============================================================
-- 05_adpath.sql
-- Cesta v AD (OU) u stanic. Spustit po 04_adsync_columns.sql.
-- ============================================================

USE USBGuardian;
GO

IF COL_LENGTH('dbo.Computers', 'AdPath') IS NULL
    ALTER TABLE dbo.Computers ADD AdPath NVARCHAR(500) NULL;
GO

PRINT 'Computers.AdPath přidáno.';
GO
