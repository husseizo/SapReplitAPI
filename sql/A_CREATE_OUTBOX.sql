-- ============================================================
-- A_CREATE_OUTBOX.sql
-- Target:  MolasIntegration database on SQL Server
-- Run as:  sysadmin / db_owner on MolasIntegration
-- Purpose: Create dbo.SapEventOutbox table for the Phase 1
--          event-driven Invoice + Payment pipeline.
-- Safe:    IF NOT EXISTS guard — idempotent re-run.
-- ============================================================
USE MolasIntegration;
GO

-- ── Pre-flight check ────────────────────────────────────────
IF OBJECT_ID('dbo.SapEventOutbox', 'U') IS NOT NULL
BEGIN
    PRINT 'WARN: dbo.SapEventOutbox already exists. Skipping CREATE TABLE.';
    PRINT 'Verify the schema matches expectations before proceeding.';
    GOTO SchemaVerify;
END
GO

-- ── Create table ─────────────────────────────────────────────
CREATE TABLE dbo.SapEventOutbox (
    -- Identity / correlation
    Id                  BIGINT          IDENTITY(1,1) NOT NULL,
    EventId             UNIQUEIDENTIFIER NOT NULL
                            CONSTRAINT DF_SapEventOutbox_EventId    DEFAULT (NEWSEQUENTIALID()),

    -- SAP document identity
    ObjectType          NVARCHAR(20)    NOT NULL,
    TransactionType     NVARCHAR(5)     NOT NULL,
    NumKeyColumns       INT             NOT NULL
                            CONSTRAINT DF_SapEventOutbox_NumKey     DEFAULT (1),
    KeyColumns          NVARCHAR(500)   NULL,
    KeyValues           NVARCHAR(500)   NULL,
    DocEntry            INT             NULL,           -- parsed from KeyValues for known single-key types

    -- Source metadata (populated by SBO_SP_PostTransactionNotice)
    CreatedAtUtc        DATETIME2(3)    NOT NULL
                            CONSTRAINT DF_SapEventOutbox_Created    DEFAULT (SYSUTCDATETIME()),
    ApplicationName     NVARCHAR(200)   NULL,           -- APP_NAME()
    HostName            NVARCHAR(200)   NULL,           -- HOST_NAME()
    SourceLogin         NVARCHAR(200)   NULL,           -- ORIGINAL_LOGIN()

    -- Processing state machine
    -- Status values: Pending | Processing | Done | Failed
    Status              NVARCHAR(20)    NOT NULL
                            CONSTRAINT DF_SapEventOutbox_Status     DEFAULT (N'Pending'),
    AttemptCount        INT             NOT NULL
                            CONSTRAINT DF_SapEventOutbox_Attempts   DEFAULT (0),
    ClaimedAtUtc        DATETIME2(3)    NULL,
    ClaimedBy           NVARCHAR(300)   NULL,           -- "<ProcessName>@<MachineName>"
    ProcessedAtUtc      DATETIME2(3)    NULL,
    NextAttemptAtUtc    DATETIME2(3)    NULL,           -- NULL = no backoff; set by retry logic
    LastError           NVARCHAR(MAX)   NULL,

    -- Constraints
    CONSTRAINT PK_SapEventOutbox PRIMARY KEY CLUSTERED (Id ASC),
    CONSTRAINT UQ_SapEventOutbox_EventId UNIQUE NONCLUSTERED (EventId)
);
GO

PRINT 'OK: dbo.SapEventOutbox created.';

-- ── Indexes ───────────────────────────────────────────────────
-- Poller reads Pending rows ordered by Id; READPAST skips Processing rows.
CREATE INDEX IX_SapEventOutbox_Status_NextAttempt
    ON dbo.SapEventOutbox (Status, NextAttemptAtUtc, Id)
    INCLUDE (ObjectType, TransactionType, DocEntry);

-- Orphan recovery queries Processing by ClaimedAtUtc age.
CREATE INDEX IX_SapEventOutbox_Status_ClaimedAt
    ON dbo.SapEventOutbox (Status, ClaimedAtUtc)
    WHERE Status = N'Processing';

PRINT 'OK: Indexes created.';
GO

-- ── Schema verification (also jumped to on re-run) ──────────
:setvar label SchemaVerify
PRINT '';
PRINT '=== Schema Verification ===';

SELECT
    c.COLUMN_NAME,
    c.DATA_TYPE,
    c.CHARACTER_MAXIMUM_LENGTH,
    c.IS_NULLABLE,
    c.COLUMN_DEFAULT
FROM INFORMATION_SCHEMA.COLUMNS c
WHERE c.TABLE_SCHEMA = 'dbo'
  AND c.TABLE_NAME   = 'SapEventOutbox'
ORDER BY c.ORDINAL_POSITION;

PRINT '';
PRINT '=== Indexes ===';
SELECT
    i.name          AS IndexName,
    i.type_desc     AS IndexType,
    i.is_unique     AS IsUnique,
    c.name          AS ColumnName,
    ic.index_column_id AS KeyPosition,
    ic.is_included_column AS IsIncluded
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns       c  ON c.object_id  = i.object_id AND c.column_id  = ic.column_id
WHERE i.object_id = OBJECT_ID('dbo.SapEventOutbox')
ORDER BY i.index_id, ic.index_column_id;

PRINT '';
PRINT '=== Constraints (PK + UQ + DF) ===';
SELECT
    tc.CONSTRAINT_NAME,
    tc.CONSTRAINT_TYPE,
    kcu.COLUMN_NAME
FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
LEFT JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
       ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
      AND kcu.TABLE_NAME      = tc.TABLE_NAME
WHERE tc.TABLE_SCHEMA = 'dbo'
  AND tc.TABLE_NAME   = 'SapEventOutbox'
ORDER BY tc.CONSTRAINT_TYPE, kcu.ORDINAL_POSITION;
GO
