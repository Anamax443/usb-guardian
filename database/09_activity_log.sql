-- ============================================================
-- 09_activity_log.sql
-- Deník aktivity: co se v systému děje a kdo s kým mluví.
--
-- PROČ:
--   Dnes se dá zpětně zjistit jen to, co skončilo incidentem. Když agent
--   přestane komunikovat, když někdo změní whitelist nebo když se nasadí
--   verze, nezůstane po tom stopa nikde než v Event Logu toho jednoho
--   stroje — a tam se nikdo nedívá. Deník je jedno místo, kde je vidět
--   provoz celého systému: heartbeaty, příjem incidentů, stahování
--   whitelistu, operátorské zásahy, nasazení.
--
-- KDO PÍŠE:
--   API   – komunikace agentů (heartbeat, incidenty, whitelist)
--   Konzole – zásahy operátora (publikace, nastavení, nasazení, vyřazení)
--   Oba zapisují do TÉŽE tabulky, takže se to čte jako jeden příběh.
--
-- ZÁPIS NESMÍ NIC ZDRŽET:
--   Zapisuje se mimo hlavní cestu požadavku; když zápis selže, provoz jede
--   dál. Deník je pozorovatel, ne součást funkce.
--
-- RETENCE:
--   Řádků přibývá rychle (heartbeat každé 2 min × počet stanic). Úklid
--   dělá procedura níž podle nastavení activity.retentionDays (default 30).
--
-- Spustit na databázi USBGuardian. Idempotentní.
-- ============================================================

USE USBGuardian;
GO

IF OBJECT_ID('dbo.ActivityLog', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ActivityLog (
        Id        BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_ActivityLog PRIMARY KEY,
        Timestamp DATETIME2      NOT NULL CONSTRAINT DF_ActivityLog_Ts DEFAULT (SYSUTCDATETIME()),
        -- info / warn / error – ať jde odfiltrovat šum od toho, co hoří
        Level     NVARCHAR(16)   NOT NULL,
        -- heartbeat / incidents / whitelist / deploy / settings / adsync …
        Source    NVARCHAR(32)   NOT NULL,
        -- které stanice se to týká (prázdné = serverová akce)
        Hostname  NVARCHAR(128)  NULL,
        -- kdo to vyvolal (operátor u ruční akce, prázdné u automatiky)
        [User]    NVARCHAR(128)  NULL,
        Message   NVARCHAR(1000) NOT NULL
    );

    -- Deník se čte skoro vždy "od konce a za posledních N hodin".
    CREATE INDEX IX_ActivityLog_Timestamp ON dbo.ActivityLog (Timestamp DESC);
    CREATE INDEX IX_ActivityLog_Source    ON dbo.ActivityLog (Source, Timestamp DESC);
    CREATE INDEX IX_ActivityLog_Hostname  ON dbo.ActivityLog (Hostname, Timestamp DESC);

    PRINT 'dbo.ActivityLog vytvořena';
END
ELSE
    PRINT 'dbo.ActivityLog už existuje – přeskakuji';
GO

-- Úklid. Volá ho API (má práva mazat), stejně jako u retence incidentů.
CREATE OR ALTER PROCEDURE dbo.sp_PurgeActivityLog
    @Days INT = 30
AS
BEGIN
    SET NOCOUNT ON;
    IF @Days < 1 SET @Days = 1;

    -- Po dávkách, ať se z úklidu nestane dlouhý zámek nad tabulkou,
    -- do které zrovna píšou agenti.
    DECLARE @Smazano INT = 1;
    WHILE @Smazano > 0
    BEGIN
        DELETE TOP (5000) FROM dbo.ActivityLog
        WHERE Timestamp < DATEADD(DAY, -@Days, SYSUTCDATETIME());
        SET @Smazano = @@ROWCOUNT;
    END
END
GO

PRINT 'Hotovo. Konzole i API potřebují INSERT/SELECT na dbo.ActivityLog;';
PRINT 'API navíc EXECUTE na dbo.sp_PurgeActivityLog.';
GO
