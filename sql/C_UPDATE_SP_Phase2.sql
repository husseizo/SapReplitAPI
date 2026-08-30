-- ============================================================
-- C_UPDATE_SP_Phase2.sql
-- Target:  MOLAS_Live_2021 database (SAP company DB)
-- Run as:  sysadmin / db_owner on MOLAS_Live_2021
-- Purpose: Expand SBO_SP_PostTransactionNotice filter to emit
--          Phase 2 inventory / delivery / SO events.
--
-- Phase 1 filter (unchanged):
--   13/A  -- A/R Invoice Add
--   14/A  -- A/R Credit Memo Add
--   24/A  -- Incoming Payment Add
--   24/C  -- Incoming Payment Cancel
--
-- Phase 2 additions:
--   17/A, 17/U, 17/C  -- ORDR Sales Order (commitment)
--   15/A              -- ODLN Delivery Add (also fires for cancellation docs)
--   16/A              -- ORDN Return
--   20/A              -- OPDN Goods Receipt PO (CANDIDATE)
--   59/A              -- OIGN Goods Receipt
--   60/A              -- OIGE Goods Issue
--   67/A, 67/C        -- OWTR Stock Transfer
--
-- NOT included:  20/C, 59/C, 60/C (cancellation docs not confirmed)
--
-- PREREQUISITES (ALL must be satisfied before running):
--   1. Phase 2 binary deployed to C:\SAPAPI and confirmed healthy.
--   2. Startup log shows 7 new ISapEventHandler registrations.
--   3. Database.Migrate() applied Deliveries + DeliveryLines schema.
--   4. Phase 1 handlers (13/A, 14/A, 24/A/C) healthy for >= 1 hour.
--   5. Controlled tests T1-T15 verified.
--
-- ROLLBACK: Re-run C_UPDATE_SP.sql (Phase 1 definition) to revert.
--           That file is the safe rollback target.
-- ============================================================
USE MOLAS_Live_2021;
GO

-- ── Safety: Back up current SP definition before ALTER ───────
DECLARE @label NVARCHAR(200) = N'Phase2_PreAlter_' +
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
-- SAP B1 calls the SP by parameter position, not name.
-- Original 5-parameter signature confirmed from Phase 0 + Phase 1 definitions.
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

    -- ── Phase 1 + Phase 2 filter ────────────────────────────────────────────
    -- Only enqueue object types the integration actively handles.
    -- Unhandled events are silently discarded here — never reach the outbox.
    IF NOT (
        -- Phase 1: Invoice + Credit Memo + Incoming Payment
        (@object_type = N'13' AND @transaction_type = N'A')    -- OINV Add
     OR (@object_type = N'14' AND @transaction_type = N'A')    -- ORIN Add
     OR (@object_type = N'24' AND @transaction_type = N'A')    -- ORCT Add
     OR (@object_type = N'24' AND @transaction_type = N'C')    -- ORCT Cancel

        -- Phase 2: Sales Order commitment (IsCommitted / AvailableToSell)
     OR (@object_type = N'17' AND @transaction_type = N'A')    -- ORDR Add
     OR (@object_type = N'17' AND @transaction_type = N'U')    -- ORDR Update
     OR (@object_type = N'17' AND @transaction_type = N'C')    -- ORDR Cancel

        -- Phase 2: Delivery + Return (physical stock + delivery cache)
     OR (@object_type = N'15' AND @transaction_type = N'A')    -- ODLN (fires for cancellation docs too)
     OR (@object_type = N'16' AND @transaction_type = N'A')    -- ORDN Return

        -- Phase 2: Goods movements (physical stock)
     OR (@object_type = N'20' AND @transaction_type = N'A')    -- OPDN Goods Receipt PO (CANDIDATE)
     OR (@object_type = N'59' AND @transaction_type = N'A')    -- OIGN Goods Receipt
     OR (@object_type = N'60' AND @transaction_type = N'A')    -- OIGE Goods Issue
     OR (@object_type = N'67' AND @transaction_type = N'A')    -- OWTR Stock Transfer Add
     OR (@object_type = N'67' AND @transaction_type = N'C')    -- OWTR Stock Transfer Cancel
    )
    BEGIN
        SELECT @error, @error_message;
        RETURN 0;
    END

    -- ── Parse DocEntry from KeyValues ────────────────────────────────────────
    -- SAP passes key values as tab-delimited, single-key as a quoted integer: '1234'
    DECLARE @docEntry INT = NULL;
    IF @num_of_cols_in_key = 1
    BEGIN
        SET @docEntry = TRY_CAST(
            REPLACE(RTRIM(LTRIM(@list_of_cols_val_tab_del)), N'''', N'') AS INT
        );
    END

    -- ── Insert into outbox ───────────────────────────────────────────────────
    -- Error is intentionally swallowed — INSERT failure must NEVER surface to SAP.
    -- Monitor SapEventOutbox row count externally to detect silent failures.
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
        -- intentionally swallowed — see comment above
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
