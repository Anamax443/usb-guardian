-- ============================================================
-- 10_ping_status.sql
-- Přetrvalý stav síťové dostupnosti (ping) stanice – nezávisle na agentovi.
--
-- PROČ:
--   "Zmlklo agentů" se dosud počítalo čistě z LastSeen (agent se dlouho
--   neozval) - bez ohledu na to, jestli je PC vůbec zapnuté. Vypnutý
--   notebook přes noc tak vypadal stejně "zmlkle" jako spadlá služba na
--   běžícím stroji, přestože jde o naprosto různé situace (jedna nechce
--   žádnou akci, druhá ano). Tlačítko "Ověřit dostupnost" na stránce
--   Stanice sice ping uměl, ale jen ručně a jen v paměti (nepřežilo reload
--   ani druhého uživatele) - "zmlklý" tedy tuhle informaci nikdy nepoužil.
--
--   PingMonitorService teď ping dělá sám na pozadí (jen pro stanice, co
--   agenta hlásí, ale nejsou čerstvé - viz Computers.razor Silent()) a
--   výsledek ukládá sem. "Zmlklý" pak znamená: hlásí agenta, není čerstvý,
--   A potvrzeně odpovídá na ping (PC běží, agent na něm mlčí - to je ten
--   případ, který stojí za pozornost).
--
-- Spustit na databázi USBGuardian. Idempotentní, jde pustit opakovaně.
-- ============================================================

USE USBGuardian;
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('dbo.Computers') AND name = 'LastPingOk')
BEGIN
    ALTER TABLE dbo.Computers ADD LastPingOk BIT NULL;
    PRINT 'Computers.LastPingOk přidán';
END
ELSE
    PRINT 'Computers.LastPingOk už existuje – přeskakuji';
GO

IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID('dbo.Computers') AND name = 'LastPingAt')
BEGIN
    ALTER TABLE dbo.Computers ADD LastPingAt DATETIME2 NULL;
    PRINT 'Computers.LastPingAt přidán';
END
ELSE
    PRINT 'Computers.LastPingAt už existuje – přeskakuji';
GO

PRINT 'Hotovo. Konzole vyžaduje UPDATE na dbo.Computers (má z AD syncu).';
GO
