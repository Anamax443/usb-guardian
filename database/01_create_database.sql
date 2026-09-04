-- ============================================================
-- USB Guardian – vytvoření databáze a přihlášení
-- Spustit jako sysadmin na SQL_SERVER
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

IF NOT EXISTS (SELECT name FROM sys.database_principals WHERE name = 'DOMENA\gmsa-api')
BEGIN
    CREATE USER [DOMENA\gmsa-api] FOR LOGIN [DOMENA\gmsa-api];
    PRINT 'Uživatel gmsa-api vytvořen.';
END
GO

-- gMSA má právo číst a zapisovat, ale ne mazat schéma
ALTER ROLE db_datareader ADD MEMBER [DOMENA\gmsa-api];
ALTER ROLE db_datawriter ADD MEMBER [DOMENA\gmsa-api];
GO

PRINT 'Databáze připravena.';
GO
