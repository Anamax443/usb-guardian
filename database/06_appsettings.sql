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
        [Key]   NVARCHAR(100)  NOT NULL PRIMARY KEY,
        [Value] NVARCHAR(MAX)  NOT NULL DEFAULT ''   -- bez limitu: drží i dlouhé seznamy (deploy.includeHosts/excludeHosts)
    );
    PRINT 'Tabulka AppSettings vytvořena.';
END
GO

-- Migrace existující tabulky: rozšířit Value na NVARCHAR(MAX) (dříve NVARCHAR(500) → truncation u dlouhých seznamů).
IF EXISTS (
    SELECT 1 FROM sys.columns c
    JOIN sys.tables t ON t.object_id = c.object_id
    WHERE t.name = 'AppSettings' AND c.name = 'Value' AND c.max_length <> -1   -- -1 = MAX
)
BEGIN
    ALTER TABLE dbo.AppSettings ALTER COLUMN [Value] NVARCHAR(MAX) NOT NULL;
    PRINT 'AppSettings.Value rozšířen na NVARCHAR(MAX).';
END
GO

-- Aktivně vyžadovat pouze schválená média (true = blokovat neschválená, false = jen varovat)
IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'policy.enforce')
    INSERT INTO dbo.AppSettings ([Key], [Value]) VALUES ('policy.enforce', 'false');
GO

-- Grant pro účet konzole (uprav účet dle nasazení):
-- GRANT SELECT, INSERT, UPDATE ON dbo.AppSettings TO [DOMENA\B-S-W-MIKOS$];
