-- ============================================================
-- 08_deploy_ignored.sql
-- Trvalé vyřazení stanice z nasazování agenta ("Ignorovat").
--
-- PROČ SLOUPEC A NE SEZNAM V NASTAVENÍ:
--   Vyřazení se dosud drželo v AppSettings jako seznam hostnamů
--   (deploy.excludeHosts). Hromadná tlačítka "Zařadit vše" / "Vyřadit vše"
--   ten seznam přepisují, takže ručně vyřazená stanice (typicky ředitel,
--   účetní, stroj ve výrobě) se po nejbližší hromadné akci zase vrátila
--   mezi cíle. Záměr operátora patří na řádek stanice, ne do seznamu,
--   který někdo jiný přepíše.
--
--   Stejný model jako monitor_enabled v ITDashboardu: příznak stanice
--   je "úmysl operátora" a hromadné operace se ho nesmí dotknout.
--
-- DeployIgnored má PŘEDNOST před vším ostatním – před include seznamem
-- i před výchozí volbou auto-enrollmentu.
--
-- Spustit na databázi USBGuardian. Idempotentní, jde pustit opakovaně.
-- ============================================================

USE USBGuardian;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('dbo.Computers') AND name = 'DeployIgnored')
BEGIN
    ALTER TABLE dbo.Computers
        ADD DeployIgnored BIT NOT NULL
            CONSTRAINT DF_Computers_DeployIgnored DEFAULT (0);
    PRINT 'Computers.DeployIgnored přidán';
END
ELSE
    PRINT 'Computers.DeployIgnored už existuje – přeskakuji';
GO

-- Kdo a kdy stanici vyřadil. Bez toho se za půl roku nikdo nedozví,
-- proč se na ten jeden stroj nenasazuje.
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('dbo.Computers') AND name = 'DeployIgnoredBy')
BEGIN
    ALTER TABLE dbo.Computers ADD DeployIgnoredBy NVARCHAR(128) NULL;
    PRINT 'Computers.DeployIgnoredBy přidán';
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('dbo.Computers') AND name = 'DeployIgnoredAt')
BEGIN
    ALTER TABLE dbo.Computers ADD DeployIgnoredAt DATETIME2 NULL;
    PRINT 'Computers.DeployIgnoredAt přidán';
END
GO

-- Účet konzole musí smět příznak měnit (na Computers už zápis má kvůli AD syncu,
-- tohle je jen pojistka, kdyby byla práva udělená po sloupcích).
PRINT 'Hotovo. Konzole vyžaduje UPDATE na dbo.Computers (má z AD syncu).';
GO
