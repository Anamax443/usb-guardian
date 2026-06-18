-- ============================================================
-- 06_appsettings.sql
-- Centrální nastavení (key/value) spravované z konzole.
-- Spustit po 05_adpath.sql.
-- ============================================================

USE USBGuardian;
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'AppSettings')
BEGIN
    CREATE TABLE dbo.AppSettings (
        [Key]   NVARCHAR(100) NOT NULL PRIMARY KEY,
        [Value] NVARCHAR(500) NOT NULL DEFAULT ''
    );
    PRINT 'Tabulka AppSettings vytvořena.';
END
GO

-- Aktivně vyžadovat pouze schválená média (true = blokovat neschválená, false = jen varovat)
IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'policy.enforce')
    INSERT INTO dbo.AppSettings ([Key], [Value]) VALUES ('policy.enforce', 'false');
GO

-- Grant pro účet konzole (uprav účet dle nasazení):
-- GRANT SELECT, INSERT, UPDATE ON dbo.AppSettings TO [DOMENA\B-S-W-MIKOS$];
