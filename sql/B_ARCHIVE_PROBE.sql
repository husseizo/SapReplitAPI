-- ============================================================
-- B_ARCHIVE_PROBE.sql
-- Target:  MOLAS_Live_2021 database (SAP company DB)
-- Run as:  sysadmin / db_owner on MOLAS_Live_2021
-- Purpose: Archive the Phase 0 probe log table and save the
--          current SBO_SP_PostTransactionNotice definition
--          to a backup table so Phase 0 rows are preserved
--          and the original SP is recoverable.
-- Safe:    IF NOT EXISTS / IF EXISTS guards — idempotent.
-- ============================================================
USE MOLAS_Live_2021;
GO

-- ── Step 1: Save current SP definition to a backup table ────
-- This captures the EXACT Phase 0 probe SP before any changes.

IF OBJECT_ID('dbo._Backup_SBO_SP_PostTransactionNotice', 'U') IS NULL
BEGIN
    CREATE TABLE dbo._Backup_SBO_SP_PostTransactionNotice (
        BackupId        INT             IDENTITY(1,1) NOT NULL PRIMARY KEY,
        BackupDate      DATETIME2(3)    NOT NULL DEFAULT SYSUTCDATETIME(),
        BackupLabel     NVARCHAR(200)   NOT NULL,
        SpDefinition    NVARCHAR(MAX)   NOT NULL
    );
    PRINT 'OK: _Backup_SBO_SP_PostTransactionNotice created.';
END
ELSE
    PRINT 'INFO: _Backup_SBO_SP_PostTransactionNotice already exists.';
GO

-- Insert current SP text as Phase 0 backup
DECLARE @spText NVARCHAR(MAX) = '';
SELECT @spText = @spText + ISNULL(OBJECT_DEFINITION(OBJECT_ID('dbo.SBO_SP_PostTransactionNotice')), '-- not found');

IF @spText = ''
    SET @spText = '-- SBO_SP_PostTransactionNotice was not found or has no definition at backup time.';

-- Only insert if no Phase0 backup exists yet
IF NOT EXISTS (
    SELECT 1 FROM dbo._Backup_SBO_SP_PostTransactionNotice
    WHERE BackupLabel = 'Phase0_Probe'
)
BEGIN
    INSERT INTO dbo._Backup_SBO_SP_PostTransactionNotice (BackupLabel, SpDefinition)
    VALUES (N'Phase0_Probe', @spText);
    PRINT 'OK: Phase 0 probe SP definition backed up.';
END
ELSE
    PRINT 'INFO: Phase0_Probe backup already exists — skipping duplicate.';
GO

-- ── Step 2: Check for probe log table ───────────────────────
-- Identify the probe log table name before archiving.
-- Common names: SapPostTransactionLog, SapEventProbeLog, PostTransactionLog, PostTransNoticeLog

PRINT '';
PRINT '=== Probe Table Discovery ===';
SELECT
    t.TABLE_SCHEMA,
    t.TABLE_NAME,
    t.TABLE_TYPE
FROM INFORMATION_SCHEMA.TABLES t
WHERE t.TABLE_NAME LIKE '%PostTransaction%'
   OR t.TABLE_NAME LIKE '%ProbeLog%'
   OR t.TABLE_NAME LIKE '%EventProbe%'
   OR t.TABLE_NAME LIKE '%SapEvent%'
   OR t.TABLE_NAME LIKE '%NoticeLog%'
ORDER BY t.TABLE_NAME;
GO

-- ── Step 3: Archive probe rows (UPDATE TABLE NAME IF NEEDED) ─
-- IMPORTANT: Review the discovery output above.
-- If your probe table has a different name, change 'SapPostTransactionLog' below.
-- The archive preserves all rows with an ArchiveDate stamp.

DECLARE @probeName NVARCHAR(200) = N'SapPostTransactionLog'; -- CHANGE IF NEEDED

IF OBJECT_ID('dbo.' + @probeName, 'U') IS NOT NULL
BEGIN
    DECLARE @archiveName NVARCHAR(200) = @probeName + N'_Phase0Archive';

    -- Create archive if it does not exist (SELECT INTO from empty set preserves structure)
    IF OBJECT_ID('dbo.' + @archiveName, 'U') IS NULL
    BEGIN
        DECLARE @createSql NVARCHAR(MAX) = N'
SELECT TOP 0 *
INTO   dbo.' + QUOTENAME(@archiveName) + N'
FROM   dbo.' + QUOTENAME(@probeName) + N';
ALTER  TABLE dbo.' + QUOTENAME(@archiveName) + N'
    ADD ArchiveDate DATETIME2(3) NOT NULL
        CONSTRAINT DF_' + REPLACE(@archiveName, ' ', '_') + N'_ArchiveDate
        DEFAULT SYSUTCDATETIME();';
        EXEC sp_executesql @createSql;
        PRINT 'OK: Archive table ' + @archiveName + ' created.';
    END
    ELSE
        PRINT 'INFO: Archive table already exists.';

    -- Copy probe rows to archive
    DECLARE @copySql NVARCHAR(MAX) = N'
INSERT INTO dbo.' + QUOTENAME(@archiveName) + N'
SELECT * FROM dbo.' + QUOTENAME(@probeName) + N';';
    EXEC sp_executesql @copySql;
    PRINT 'OK: Probe rows copied to archive.';

    -- Count verification
    DECLARE @cnt INT;
    SELECT @cnt = COUNT(*) FROM dbo.SapPostTransactionLog;   -- adjust if table name changed
    PRINT 'INFO: ' + CAST(@cnt, NVARCHAR(10)) + ' rows remain in probe table.';
    PRINT 'NOTE: Probe table itself is NOT dropped — SP update will stop new writes to it.';
END
ELSE
BEGIN
    PRINT 'WARN: Probe table ' + @probeName + ' not found.';
    PRINT '      Check the discovery output above and update the @probeName variable if needed.';
END
GO

-- ── Step 4: Verify backup ────────────────────────────────────
PRINT '';
PRINT '=== SP Backup Verification ===';
SELECT BackupId, BackupDate, BackupLabel,
       LEFT(SpDefinition, 200) AS SpDefinitionPreview
FROM dbo._Backup_SBO_SP_PostTransactionNotice
ORDER BY BackupId;
GO

-- ── Step 5: Print current SP definition for manual review ───
PRINT '';
PRINT '=== Current SBO_SP_PostTransactionNotice Definition ===';
EXEC sp_helptext 'dbo.SBO_SP_PostTransactionNotice';
GO
