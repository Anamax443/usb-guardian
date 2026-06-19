-- ============================================================
-- 07_whitelist_publish.sql
-- Publikační/podpisový workflow whitelistu (klient = 1:1 kopie serveru).
-- Server drží v DB PODEPSANÝ BLOB (přesné bajty whitelist.json) + RSA podpis;
-- API ho servíruje agentům verbatim, agent uloží jako JSON soubor (klient nemá DB).
--
-- Spustit po 06_appsettings.sql. Idempotentní.
-- ============================================================

USE USBGuardian;
GO

-- 1) Json: kanonický whitelist.json blob (podepisuje se a servíruje VERBATIM). NVARCHAR(MAX) = bez limitu (i 10k zařízení ~MB).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.WhitelistVersions') AND name = 'Json')
BEGIN
    ALTER TABLE dbo.WhitelistVersions ADD [Json] NVARCHAR(MAX) NOT NULL CONSTRAINT DF_WhitelistVersions_Json DEFAULT '';
    PRINT 'WhitelistVersions.Json pridan (NVARCHAR(MAX)).';
END
GO

-- 2) Signature: rozšířit na NVARCHAR(MAX) (RSA-4096 base64 ~684 znaků > původních 500 → truncation).
IF EXISTS (
    SELECT 1 FROM sys.columns c
    WHERE c.object_id = OBJECT_ID('dbo.WhitelistVersions') AND c.name = 'Signature' AND c.max_length <> -1   -- -1 = MAX
)
BEGIN
    DECLARE @dc sysname;
    SELECT @dc = dc.name
    FROM sys.default_constraints dc
    JOIN sys.columns col ON col.object_id = dc.parent_object_id AND col.column_id = dc.parent_column_id
    WHERE dc.parent_object_id = OBJECT_ID('dbo.WhitelistVersions') AND col.name = 'Signature';
    IF @dc IS NOT NULL EXEC('ALTER TABLE dbo.WhitelistVersions DROP CONSTRAINT [' + @dc + ']');

    ALTER TABLE dbo.WhitelistVersions ALTER COLUMN [Signature] NVARCHAR(MAX) NOT NULL;

    IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_WhitelistVersions_Signature')
        ALTER TABLE dbo.WhitelistVersions ADD CONSTRAINT DF_WhitelistVersions_Signature DEFAULT '' FOR [Signature];

    PRINT 'WhitelistVersions.Signature rozsiren na NVARCHAR(MAX).';
END
GO
