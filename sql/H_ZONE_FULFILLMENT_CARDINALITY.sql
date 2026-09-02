-- =============================================================================
-- H_ZONE_FULFILLMENT_CARDINALITY.sql
-- Target:  MolasIntegration (SQL Server)
-- Run as:  db_owner or sysadmin
-- Purpose: Three cardinality corrections for Zone Fulfillment multi-delivery support.
--   Part 1 — DeliveryRecord:       remove unique-per-orch, add filtered unique on SapDocEntry
--   Part 2 — DeliveryFragmentRecord: allow N rows per fragment (one per delivery), add DeliveredQty
--   Part 3 — PickListFragmentRecord: new table for normalized multi-line OPKL tracking
-- =============================================================================

USE MolasIntegration;
GO

PRINT '=== Part 0: PickListRecord constraint redesign ===';
GO

-- 0a: Drop UNIQUE(SoLineFragmentId, WhsCode).
--     BM10005 proves one fragment needs N PickListRecords over time (historical + repick).
--     This constraint incorrectly enforces lifetime uniqueness per fragment per warehouse.
IF EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.PickListRecord')
      AND name = 'UQ_PickListRecord_Fragment_Whs'
)
BEGIN
    ALTER TABLE dbo.PickListRecord DROP CONSTRAINT UQ_PickListRecord_Fragment_Whs;
    PRINT 'OK: UQ_PickListRecord_Fragment_Whs dropped.';
END
ELSE
    PRINT 'INFO: UQ_PickListRecord_Fragment_Whs already absent — skipped.';
GO

-- 0b: Add UNIQUE(OrchestrationId, SoLineFragmentId, PickListAbsEntry).
--     Contract:
--       • Same fragment, different AbsEntry → repick history, ALLOWED.
--       • Same AbsEntry, different fragment  → multi-line OPKL, ALLOWED.
--       • Same (orch, frag, AbsEntry)        → duplicate insert, BLOCKED.
--     SAP-level idempotency (U_ReplitId) prevents duplicate OPKL creation.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.PickListRecord')
      AND name = 'UX_PickListRecord_Orch_Frag_AbsEntry'
)
BEGIN
    CREATE UNIQUE INDEX UX_PickListRecord_Orch_Frag_AbsEntry
        ON dbo.PickListRecord (OrchestrationId, SoLineFragmentId, PickListAbsEntry);
    PRINT 'OK: UX_PickListRecord_Orch_Frag_AbsEntry created.';
END
ELSE
    PRINT 'INFO: UX_PickListRecord_Orch_Frag_AbsEntry already exists — skipped.';
GO

-- 0c: Non-unique indexes for common lookup patterns
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.PickListRecord')
      AND name = 'IX_PickListRecord_OrchId'
)
BEGIN
    CREATE INDEX IX_PickListRecord_OrchId ON dbo.PickListRecord (OrchestrationId);
    PRINT 'OK: IX_PickListRecord_OrchId created.';
END
ELSE
    PRINT 'INFO: IX_PickListRecord_OrchId already exists — skipped.';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.PickListRecord')
      AND name = 'IX_PickListRecord_WhsCode'
)
BEGIN
    CREATE INDEX IX_PickListRecord_WhsCode ON dbo.PickListRecord (WhsCode);
    PRINT 'OK: IX_PickListRecord_WhsCode created.';
END
ELSE
    PRINT 'INFO: IX_PickListRecord_WhsCode already exists — skipped.';
GO

PRINT '';
PRINT '=== Part 1: DeliveryRecord cardinality ===';
GO

-- 1a: Drop single-per-orchestration UNIQUE constraint
IF EXISTS (
    SELECT 1 FROM sys.objects
    WHERE name = 'UQ_DeliveryRecord_Orch'
      AND parent_object_id = OBJECT_ID('dbo.DeliveryRecord')
)
BEGIN
    ALTER TABLE dbo.DeliveryRecord DROP CONSTRAINT UQ_DeliveryRecord_Orch;
    PRINT 'OK: UQ_DeliveryRecord_Orch dropped.';
END
ELSE
    PRINT 'INFO: UQ_DeliveryRecord_Orch already absent — skipped.';
GO

-- 1b: Non-unique index — allows fast lookup of all delivery records per orchestration
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.DeliveryRecord')
      AND name = 'IX_DeliveryRecord_OrchId'
)
BEGIN
    CREATE INDEX IX_DeliveryRecord_OrchId ON dbo.DeliveryRecord (OrchestrationId);
    PRINT 'OK: IX_DeliveryRecord_OrchId created.';
END
ELSE
    PRINT 'INFO: IX_DeliveryRecord_OrchId already exists — skipped.';
GO

-- 1c: Filtered unique on SapDocEntry — one active ODLN cannot appear in two records
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.DeliveryRecord')
      AND name = 'UX_DeliveryRecord_SapDocEntry'
)
BEGIN
    CREATE UNIQUE INDEX UX_DeliveryRecord_SapDocEntry
        ON dbo.DeliveryRecord (SapDocEntry)
        WHERE SapDocEntry IS NOT NULL;
    PRINT 'OK: UX_DeliveryRecord_SapDocEntry created (filtered — NULLs excluded).';
END
ELSE
    PRINT 'INFO: UX_DeliveryRecord_SapDocEntry already exists — skipped.';
GO

PRINT '';
PRINT '=== Part 2: DeliveryFragmentRecord cardinality ===';
GO

-- 2a: Drop UNIQUE(FragmentId) — one fragment may appear across multiple delivery records
--     (e.g. BM10005: delivered in ODLN 30511 [Canceled], then re-delivered later)
IF EXISTS (
    SELECT 1 FROM sys.objects
    WHERE name = 'UQ_DFR_Fragment'
      AND parent_object_id = OBJECT_ID('dbo.DeliveryFragmentRecord')
)
BEGIN
    ALTER TABLE dbo.DeliveryFragmentRecord DROP CONSTRAINT UQ_DFR_Fragment;
    PRINT 'OK: UQ_DFR_Fragment dropped.';
END
ELSE
    PRINT 'INFO: UQ_DFR_Fragment already absent — skipped.';
GO

-- 2b: Add DeliveredQty — records quantity actually delivered in this specific ODLN
--     SAP active DLN1 quantity remains authoritative; this is a local trace field.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.DeliveryFragmentRecord')
      AND name = 'DeliveredQty'
)
BEGIN
    ALTER TABLE dbo.DeliveryFragmentRecord
        ADD DeliveredQty DECIMAL(19,6) NOT NULL CONSTRAINT DF_DFR_DeliveredQty DEFAULT 0;
    PRINT 'OK: DeliveredQty added to DeliveryFragmentRecord.';
END
ELSE
    PRINT 'INFO: DeliveredQty already present — skipped.';
GO

-- Backfill DeliveredQty from PickedQty for non-Canceled records
UPDATE dfr
SET    dfr.DeliveredQty = dfr.PickedQty
FROM   dbo.DeliveryFragmentRecord dfr
JOIN   dbo.DeliveryRecord         dr  ON dr.Id = dfr.DeliveryRecordId
WHERE  dr.Status != N'Canceled'
  AND  dfr.DeliveredQty = 0
  AND  dfr.PickedQty   > 0;
PRINT 'OK: DeliveredQty backfilled for non-Canceled records.';
GO

-- 2c: Add UNIQUE(DeliveryRecordId, FragmentId) — one DFR row per fragment per delivery attempt
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.DeliveryFragmentRecord')
      AND name = 'UX_DFR_DeliveryRecord_Fragment'
)
BEGIN
    CREATE UNIQUE INDEX UX_DFR_DeliveryRecord_Fragment
        ON dbo.DeliveryFragmentRecord (DeliveryRecordId, FragmentId);
    PRINT 'OK: UX_DFR_DeliveryRecord_Fragment created.';
END
ELSE
    PRINT 'INFO: UX_DFR_DeliveryRecord_Fragment already exists — skipped.';
GO

-- Grant UPDATE for the new column (SELECT/INSERT already granted)
GRANT UPDATE ON dbo.DeliveryFragmentRecord TO SapReplitOutboxApp;
GO

PRINT '';
PRINT '=== Part 3: PickListFragmentRecord (new normalized table) ===';
GO

-- 3a: New table — one row per SO fragment per PickListRecord (multi-line OPKL design)
IF OBJECT_ID('dbo.PickListFragmentRecord', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.PickListFragmentRecord
    (
        Id               BIGINT          NOT NULL IDENTITY(1,1) PRIMARY KEY,
        PickListRecordId BIGINT          NOT NULL,    -- FK → dbo.PickListRecord.Id
        SoLineFragmentId BIGINT          NOT NULL,    -- FK → dbo.SoLineFragment.Id
        SoDocEntry       INT             NOT NULL,
        SoLineNum        INT             NOT NULL,
        ItemCode         NVARCHAR(50)    NOT NULL,
        WhsCode          NVARCHAR(10)    NOT NULL,
        ReleasedQty      DECIMAL(19,6)   NOT NULL,
        PickedQty        DECIMAL(19,6)   NOT NULL CONSTRAINT DF_PLFR_PickedQty DEFAULT 0,
        SapPickEntry     INT             NULL,        -- PKL1.PickEntry — the SAP line identity
        PickStatus       NVARCHAR(10)    NOT NULL CONSTRAINT DF_PLFR_PickStatus DEFAULT N'Created',
        CreatedAtUtc     DATETIME2(7)    NOT NULL CONSTRAINT DF_PLFR_Created DEFAULT SYSUTCDATETIME(),
        UpdatedAtUtc     DATETIME2(7)    NOT NULL CONSTRAINT DF_PLFR_Updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT FK_PLFR_PickListRecord  FOREIGN KEY (PickListRecordId)
            REFERENCES dbo.PickListRecord (Id),
        CONSTRAINT FK_PLFR_SoLineFragment  FOREIGN KEY (SoLineFragmentId)
            REFERENCES dbo.SoLineFragment (Id),
        CONSTRAINT UX_PLFR_Record_Fragment UNIQUE (PickListRecordId, SoLineFragmentId)
    );
    CREATE INDEX IX_PLFR_PickListRecord  ON dbo.PickListFragmentRecord (PickListRecordId);
    CREATE INDEX IX_PLFR_SoLineFragment  ON dbo.PickListFragmentRecord (SoLineFragmentId);
    GRANT SELECT, INSERT, UPDATE ON dbo.PickListFragmentRecord TO SapReplitOutboxApp;
    PRINT 'OK: PickListFragmentRecord created.';
END
ELSE
    PRINT 'INFO: PickListFragmentRecord already exists — skipped.';
GO

PRINT '';
PRINT '=== Post-check ===';
GO

SELECT o.name AS object_name, o.type_desc
FROM   sys.objects o
WHERE  o.parent_object_id IN (
    OBJECT_ID('dbo.DeliveryRecord'),
    OBJECT_ID('dbo.DeliveryFragmentRecord')
)
  AND  o.type_desc IN ('UNIQUE_CONSTRAINT','CHECK_CONSTRAINT')
UNION ALL
SELECT i.name, 'INDEX'
FROM   sys.indexes i
WHERE  i.object_id IN (
    OBJECT_ID('dbo.DeliveryRecord'),
    OBJECT_ID('dbo.DeliveryFragmentRecord')
)
  AND  i.name IS NOT NULL
  AND  i.name NOT LIKE 'PK_%'
ORDER  BY 1;
GO

SELECT 'PickListFragmentRecord' AS tbl, COUNT(*) AS rows FROM dbo.PickListFragmentRecord;
SELECT 'DeliveryFragmentRecord' AS tbl, COUNT(*) AS rows FROM dbo.DeliveryFragmentRecord;
GO
