-- ============================================================
-- C_UPDATE_SP.sql
-- Target:  MOLAS_Live_2021 database (SAP company DB)
-- Run as:  sysadmin / db_owner on MOLAS_Live_2021
-- Purpose: Replace Phase 0 probe INSERT in
--          SBO_SP_PostTransactionNotice with the Phase 1
--          production EventOutbox INSERT.
--
-- Phase 1 filter (ONLY these ObjectType/TransactionType pairs):
--   13/A  — A/R Invoice Add
--   14/A  — A/R Credit Memo Add
--   24/A  — Incoming Payment Add
--   24/C  — Incoming Payment Cancel
--
-- NOT YET included (Phase 2):
--   15, 16, 59, 60, 67, 17 — physical inventory / commitment domain
--
-- PREREQUISITES:
--   1. A_CREATE_OUTBOX.sql must be run (SapEventOutbox must exist).
--   2. D_PERMISSIONS.sql must be run (B1 login must have INSERT on SapEventOutbox).
--   3. Application (SapReplitAPI) deployed and OutboxPoller started successfully.
--   4. B_ARCHIVE_FIX.sql must be run (SapEventProbe rows archived).
--
-- ROLLBACK: Re-run the Phase 0 probe SP definition saved in
--           MOLAS_Live_2021.dbo._Backup_SBO_SP_PostTransactionNotice.
-- ============================================================
USE MOLAS_Live_2021;
GO

-- ── Safety: Back up current SP definition before ALTER ───────
DECLARE @label NVARCHAR(200) = N'Phase1_PreAlter_' +
    REPLACE(REPLACE(CONVERT(NVARCHAR(30), SYSUTCDATETIME(), 126), ':', ''), '.', '');

DECLARE @spText NVARCHAR(MAX) =
    ISNULL(OBJECT_DEFINITION(OBJECT_ID('dbo.SBO_SP_PostTransactionNotice')),
           '-- definition not found');

INSERT INTO dbo._Backup_SBO_SP_PostTransactionNotice (BackupLabel, SpDefinition)
VALUES (@label, @spText);

PRINT 'OK: Pre-ALTER backup saved with label ' + @label;
GO

-- ── ALTER SBO_SP_PostTransactionNotice ───────────────────────
-- Parameter names, types, and order MUST match the original exactly —
-- SAP B1 calls the SP by parameter position.
-- Original signature confirmed from Phase 0 definition:
--   @object_type              NVARCHAR(30)
--   @transaction_type         NCHAR(1)
--   @num_of_cols_in_key       INT
--   @list_of_key_cols_tab_del NVARCHAR(255)
--   @list_of_cols_val_tab_del NVARCHAR(255)
ALTER PROCEDURE [dbo].[SBO_SP_PostTransactionNotice]
    @object_type              NVARCHAR(30),
    @transaction_type         NCHAR(1),
    @num_of_cols_in_key       INT,
    @list_of_key_cols_tab_del NVARCHAR(255),
    @list_of_cols_val_tab_del NVARCHAR(255)
AS
BEGIN
    SET NOCOUNT ON;

    -- SAP-required return values (preserved from original stub)
    DECLARE @error         INT           = 0;
    DECLARE @error_message NVARCHAR(200) = N'Ok';

    -- Phase 1 filter: Invoice + Credit Memo + Incoming Payment only.
    -- Phase 2 (physical inventory, commitment) added in C_UPDATE_SP_Phase2.sql.
    IF NOT (
        (@object_type = N'13' AND @transaction_type = N'A')   -- OINV Add
     OR (@object_type = N'14' AND @transaction_type = N'A')   -- ORIN Add
     OR (@object_type = N'24' AND @transaction_type = N'A')   -- ORCT Add
     OR (@object_type = N'24' AND @transaction_type = N'C')   -- ORCT Cancel
    )
    BEGIN
        SELECT @error, @error_message;
        RETURN 0;
    END

    -- Parse DocEntry from KeyValues for single-key documents.
    -- SAP passes key values as tab-delimited; single-key is a quoted integer: '1234'
    DECLARE @docEntry INT = NULL;
    IF @num_of_cols_in_key = 1
    BEGIN
        DECLARE @parsed INT = TRY_CAST(
            REPLACE(RTRIM(LTRIM(@list_of_cols_val_tab_del)), N'''', N'') AS INT
        );
        SET @docEntry = @parsed;
    END

    BEGIN TRY
        INSERT INTO MolasIntegration.dbo.SapEventOutbox (
            ObjectType,
            TransactionType,
            NumKeyColumns,
            KeyColumns,
            KeyValues,
            DocEntry,
            ApplicationName,
            HostName,
            SourceLogin
        )
        VALUES (
            @object_type,
            @transaction_type,
            @num_of_cols_in_key,
            @list_of_key_cols_tab_del,
            @list_of_cols_val_tab_del,
            @docEntry,
            APP_NAME(),
            HOST_NAME(),
            ORIGINAL_LOGIN()
        );
    END TRY
    BEGIN CATCH
        -- INSERT failure must NEVER surface to SAP — mirrors Phase 0 probe pattern.
        -- Errors are swallowed intentionally.
        -- Root causes: SapEventOutbox dropped, MolasIntegration offline, permission removed.
        -- Monitor SapEventOutbox row count externally to detect silent failures.
    END CATCH;

    SELECT @error, @error_message;
END;
GO

-- ── Verification ─────────────────────────────────────────────
PRINT '';
PRINT '=== SP Definition Verification ===';
EXEC sp_helptext 'dbo.SBO_SP_PostTransactionNotice';
GO

PRINT '';
PRINT '=== Backup history ===';
SELECT BackupId, BackupDate, BackupLabel
FROM dbo._Backup_SBO_SP_PostTransactionNotice
ORDER BY BackupId;
GO
