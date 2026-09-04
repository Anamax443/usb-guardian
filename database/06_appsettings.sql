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
        -- bez limitu: drží i dlouhé seznamy (deploy.includeHosts/excludeHosts); pojmenovaný default constraint
        [Value] NVARCHAR(MAX)  NOT NULL CONSTRAINT DF_AppSettings_Value DEFAULT ''
    );
    PRINT 'Tabulka AppSettings vytvořena.';
END
GO

-- Migrace existující tabulky: rozšířit Value na NVARCHAR(MAX) (dříve NVARCHAR(500) → truncation u dlouhých seznamů).
-- Pozn.: na sloupci je DEFAULT '' constraint (auto-pojmenovaný DF__AppSettin__Value__...), který blokuje ALTER COLUMN
--        → musí se dropnout, změnit typ a znovu přidat.
IF EXISTS (
    SELECT 1 FROM sys.columns c
    JOIN sys.tables t ON t.object_id = c.object_id
    WHERE t.name = 'AppSettings' AND c.name = 'Value' AND c.max_length <> -1   -- -1 = MAX
)
BEGIN
    DECLARE @dc sysname;
    SELECT @dc = dc.name
    FROM sys.default_constraints dc
    JOIN sys.columns col ON col.object_id = dc.parent_object_id AND col.column_id = dc.parent_column_id
    WHERE dc.parent_object_id = OBJECT_ID('dbo.AppSettings') AND col.name = 'Value';
    IF @dc IS NOT NULL EXEC('ALTER TABLE dbo.AppSettings DROP CONSTRAINT [' + @dc + ']');

    ALTER TABLE dbo.AppSettings ALTER COLUMN [Value] NVARCHAR(MAX) NOT NULL;

    IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_AppSettings_Value')
        ALTER TABLE dbo.AppSettings ADD CONSTRAINT DF_AppSettings_Value DEFAULT '' FOR [Value];

    PRINT 'AppSettings.Value rozšířen na NVARCHAR(MAX).';
END
GO

-- Aktivně vyžadovat pouze schválená média (true = blokovat neschválená, false = jen varovat)
IF NOT EXISTS (SELECT 1 FROM dbo.AppSettings WHERE [Key] = 'policy.enforce')
    INSERT INTO dbo.AppSettings ([Key], [Value]) VALUES ('policy.enforce', 'false');
GO

-- Grant pro účet konzole (uprav účet dle nasazení):
-- GRANT SELECT, INSERT, UPDATE ON dbo.AppSettings TO [DOMENA\APP_SERVER$];
