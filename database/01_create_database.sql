-- ============================================================
-- USB Guardian – vytvoření databáze a přihlášení
-- Spustit jako sysadmin na B-S-W-SQL-04
-- ============================================================

USE master;
GO

-- Vytvoření databáze
IF NOT EXISTS (SELECT name FROM sys.databases WHERE name = 'USBGuardian')
BEGIN
    CREATE DATABASE USBGuardian
        COLLATE Czech_CI_AS;
    PRINT 'Databáze USBGuardian vytvořena.';
END
GO

-- Práva pro gMSA účet
USE USBGuardian;
GO

IF NOT EXISTS (SELECT name FROM sys.database_principals WHERE name = 'AXINETWORK\gmsa-SQLS')
BEGIN
    CREATE USER [AXINETWORK\gmsa-SQLS] FOR LOGIN [AXINETWORK\gmsa-SQLS];
    PRINT 'Uživatel gmsa-SQLS vytvořen.';
END
GO

-- gMSA má právo číst a zapisovat, ale ne mazat schéma
ALTER ROLE db_datareader ADD MEMBER [AXINETWORK\gmsa-SQLS];
ALTER ROLE db_datawriter ADD MEMBER [AXINETWORK\gmsa-SQLS];
GO

PRINT 'Databáze připravena.';
GO
