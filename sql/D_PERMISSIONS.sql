-- ============================================================
-- D_PERMISSIONS.sql
-- Target:  MolasIntegration database on SQL Server
-- Run as:  sysadmin on master + db_owner on MolasIntegration
-- Purpose: Create two SQL logins / DB users with least-privilege
--          access to dbo.SapEventOutbox.
--
-- B1_<company>_RW (SAP DI-API system login):
--   - INSERT on SapEventOutbox (written by SBO_SP_PostTransactionNotice)
--   - No SELECT, no UPDATE, no DELETE
--
-- SapReplitOutboxApp (application reader/processor login):
--   - SELECT on SapEventOutbox  (read Pending rows)
--   - UPDATE on SapEventOutbox  (claim, mark Done/Failed/Pending-retry)
--   - No INSERT, no DELETE, no ALTER, no db_owner, no db_datareader
--
-- PREREQUISITES:
--   1. MolasIntegration database must exist (run A_CREATE_OUTBOX.sql first).
--   2. Replace <B1_LOGIN_NAME> with the actual SAP DI-API SQL Server login.
--      In MOLAS SAP config this is typically the login SAP uses to connect
--      to the company DB (e.g., B1_MOLASSOLUTIONS or similar).
--      Find it via: SELECT name FROM sys.sql_logins WHERE name LIKE 'B1%'
--   3. Replace <STRONG_PASSWORD> with a strong password for SapReplitOutboxApp.
--      Store the password only in the ConnectionStrings__MolasIntegration env var
--      on the host machine — NEVER commit it.
-- ============================================================

-- ── Step 1: Verify correct database context ──────────────────
USE master;
GO

-- ── Step 2: Discover SAP DI-API SQL login name ───────────────
PRINT '=== SAP DI-API login discovery ===';
SELECT name, create_date, is_disabled
FROM sys.sql_logins
WHERE name LIKE 'B1%'
   OR name LIKE 'SAP%'
ORDER BY name;
GO

-- ── Step 3: Create SapReplitOutboxApp server login ──────────
-- Replace <STRONG_PASSWORD> before running.
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'SapReplitOutboxApp')
BEGIN
    -- IMPORTANT: Replace <STRONG_PASSWORD> with a real strong password.
    -- This script will fail intentionally until the placeholder is replaced.
    CREATE LOGIN [SapReplitOutboxApp]
        WITH PASSWORD    = N'<STRONG_PASSWORD>',
             CHECK_POLICY = ON,
             CHECK_EXPIRATION = OFF;
    PRINT 'OK: Login SapReplitOutboxApp created.';
END
ELSE
    PRINT 'INFO: Login SapReplitOutboxApp already exists — skipping CREATE LOGIN.';
GO

-- ── Step 4: Grant B1 login INSERT on SapEventOutbox ─────────
USE MolasIntegration;
GO

-- Create DB user for the SAP DI-API login (INSERT-only writer)
-- IMPORTANT: Replace <B1_LOGIN_NAME> with the actual SAP SQL login name
-- found in Step 2 output (e.g., B1_MOLASSOLUTIONS).
DECLARE @b1Login NVARCHAR(200) = N'<B1_LOGIN_NAME>';   -- REPLACE THIS

-- Create DB user if it doesn't exist
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @b1Login)
BEGIN
    DECLARE @createUser NVARCHAR(MAX) =
        N'CREATE USER ' + QUOTENAME(@b1Login) + N' FOR LOGIN ' + QUOTENAME(@b1Login);
    EXEC sp_executesql @createUser;
    PRINT 'OK: DB user for B1 login created in MolasIntegration.';
END
ELSE
    PRINT 'INFO: DB user for B1 login already exists.';

-- Grant INSERT only
GRANT INSERT ON dbo.SapEventOutbox TO [<B1_LOGIN_NAME>];   -- REPLACE B1_LOGIN_NAME

PRINT 'OK: INSERT granted to B1 login on SapEventOutbox.';
GO

-- ── Step 5: Grant SapReplitOutboxApp SELECT + UPDATE ────────
-- Create DB user for SapReplitOutboxApp
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'SapReplitOutboxApp')
BEGIN
    CREATE USER [SapReplitOutboxApp] FOR LOGIN [SapReplitOutboxApp];
    PRINT 'OK: DB user SapReplitOutboxApp created in MolasIntegration.';
END
ELSE
    PRINT 'INFO: DB user SapReplitOutboxApp already exists.';

-- SELECT (ClaimBatch + ResetOrphaned queries)
GRANT SELECT ON dbo.SapEventOutbox TO [SapReplitOutboxApp];

-- UPDATE (ClaimBatch OUTPUT, MarkDone, MarkFailed, ResetOrphaned)
GRANT UPDATE ON dbo.SapEventOutbox TO [SapReplitOutboxApp];

-- Explicitly DENY anything not granted
DENY INSERT ON dbo.SapEventOutbox TO [SapReplitOutboxApp];
DENY DELETE ON dbo.SapEventOutbox TO [SapReplitOutboxApp];
DENY ALTER  ON dbo.SapEventOutbox TO [SapReplitOutboxApp];

PRINT 'OK: SELECT + UPDATE granted to SapReplitOutboxApp. INSERT + DELETE + ALTER denied.';
GO

-- ── Step 6: Verify permissions ───────────────────────────────
PRINT '';
PRINT '=== Permission Verification ===';

SELECT
    dp.name         AS Principal,
    perm.permission_name AS Permission,
    perm.state_desc AS State,       -- GRANT or DENY
    obj.name        AS ObjectName,
    obj.type_desc   AS ObjectType
FROM sys.database_permissions perm
JOIN sys.database_principals  dp   ON dp.principal_id  = perm.grantee_principal_id
JOIN sys.objects               obj ON obj.object_id     = perm.major_id
WHERE obj.name = N'SapEventOutbox'
ORDER BY dp.name, perm.permission_name;
GO

PRINT '';
PRINT '=== Role Membership Check (no db_owner / db_datawriter / db_datareader expected) ===';
SELECT
    dp.name        AS Member,
    dr.name        AS Role
FROM sys.database_role_members rm
JOIN sys.database_principals dp ON dp.principal_id = rm.member_principal_id
JOIN sys.database_principals dr ON dr.principal_id = rm.role_principal_id
WHERE dp.name IN (N'SapReplitOutboxApp')
ORDER BY dp.name, dr.name;
GO
