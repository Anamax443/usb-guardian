-- ============================================================
-- 03_add_sourcefile.sql
-- Přidání sloupce SourceFile do tabulky Incidents
-- Umožňuje dohledat z jakého lokálního souboru záznam pochází
-- Audit trail: SQL Server ↔ lokální sent\ složka
-- ============================================================

USE USBGuardian;
GO

-- Přidat SourceFile sloupec
IF NOT EXISTS (
    SELECT 1 FROM sys.columns 
    WHERE object_id = OBJECT_ID('dbo.Incidents') 
    AND name = 'SourceFile'
)
BEGIN
    ALTER TABLE dbo.Incidents 
    ADD SourceFile NVARCHAR(255) NULL;
    
    PRINT 'Sloupec SourceFile přidán.';
END
ELSE
BEGIN
    PRINT 'Sloupec SourceFile již existuje.';
END
GO

PRINT 'Hotovo.';
