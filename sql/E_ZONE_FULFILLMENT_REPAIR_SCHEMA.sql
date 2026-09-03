-- =============================================================================
-- Zone Fulfillment Repair Gate — Schema Migration
-- Applies to: MolasIntegration (SQL Server)
-- Required before: PICK_LIST_MUTATION_ENABLED = true / second delivery authorization
-- DO NOT RUN without separate explicit authorization.
-- =============================================================================

-- -----------------------------------------------------------------------------
-- PART 1: DeliveryRecord constraint change
-- Bug #8: drop UNIQUE(OrchestrationId) to allow N deliveries per orchestration
--         add UNIQUE(SapDocEntry) to prevent duplicate ODLN entries
-- -----------------------------------------------------------------------------

-- Step 1a: Find and drop the UNIQUE constraint on OrchestrationId
DECLARE @ConstraintName NVARCHAR(256);
SELECT @ConstraintName = name
FROM   sys.indexes
WHERE  object_id = OBJECT_ID('dbo.DeliveryRecord')
  AND  is_unique = 1
  AND  name LIKE '%OrchestrationId%';

IF @ConstraintName IS NOT NULL
    EXEC('ALTER TABLE dbo.DeliveryRecord DROP CONSTRAINT ' + @ConstraintName);
GO

-- Step 1b: Add UNIQUE constraint on SapDocEntry (NULLs excluded — partial index)
-- Allows multiple Pending rows per orchestration; once SapDocEntry is set, it must be unique.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.DeliveryRecord')
      AND name = 'UX_DeliveryRecord_SapDocEntry'
)
    CREATE UNIQUE INDEX UX_DeliveryRecord_SapDocEntry
        ON dbo.DeliveryRecord (SapDocEntry)
        WHERE SapDocEntry IS NOT NULL;
GO

-- -----------------------------------------------------------------------------
-- PART 2: PickListRecord constraint redesign
-- Bug #2: move from per-fragment to per-WHS records
--
-- New PickListRecord: one row per (OrchestrationId × WhsCode)
-- New PickListFragmentRecord: one row per SoLineFragment per pick list
-- -----------------------------------------------------------------------------

-- Step 2a: Drop old UNIQUE constraint on (SoLineFragmentId, WhsCode)
DECLARE @PlConstraint NVARCHAR(256);
SELECT @PlConstraint = name
FROM   sys.indexes
WHERE  object_id = OBJECT_ID('dbo.PickListRecord')
  AND  is_unique = 1
  AND  name LIKE '%SoLineFragment%';

IF @PlConstraint IS NOT NULL
    EXEC('ALTER TABLE dbo.PickListRecord DROP CONSTRAINT ' + @PlConstraint);
GO

-- Step 2b: Add new UNIQUE constraint on (OrchestrationId, WhsCode)
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.PickListRecord')
      AND name = 'UX_PickListRecord_OrchestraionId_WhsCode'
)
    ALTER TABLE dbo.PickListRecord
        ADD CONSTRAINT UX_PickListRecord_OrchestraionId_WhsCode
        UNIQUE (OrchestrationId, WhsCode);
GO

-- Step 2c: Add SoLineFragmentId as nullable (will be moved to child table)
-- Skip if column already removed in a prior migration
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.PickListRecord') AND name = 'SoLineFragmentId_Deprecated'
)
    EXEC sp_rename 'dbo.PickListRecord.SoLineFragmentId', 'SoLineFragmentId_Deprecated', 'COLUMN';
GO

-- Step 2d: Create new PickListFragmentRecord table
IF OBJECT_ID('dbo.PickListFragmentRecord', 'U') IS NULL
CREATE TABLE dbo.PickListFragmentRecord
(
    Id               BIGINT          NOT NULL IDENTITY(1,1) PRIMARY KEY,
    PickListRecordId BIGINT          NOT NULL REFERENCES dbo.PickListRecord(Id),
    SoLineFragmentId BIGINT          NOT NULL,    -- FK to dbo.SoLineFragment.Id
    SoDocEntry       INT             NOT NULL,
    SoLineNum        INT             NOT NULL,
    ReleasedQty      DECIMAL(19,6)   NOT NULL,
    PickedQty        DECIMAL(19,6)   NOT NULL DEFAULT 0,
    CreatedAtUtc     DATETIME2(7)    NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAtUtc     DATETIME2(7)    NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UX_PickListFragmentRecord_SoLineFragmentId UNIQUE (SoLineFragmentId)
);
GO

-- Step 2e: Seed PickListFragmentRecord from existing PickListRecord rows
-- (Migrates legacy per-fragment pick list records into the new child table structure)
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.PickListRecord') AND name = 'SoLineFragmentId_Deprecated'
)
BEGIN
    INSERT INTO dbo.PickListFragmentRecord
        (PickListRecordId, SoLineFragmentId, SoDocEntry, SoLineNum, ReleasedQty, PickedQty)
    SELECT
        Id,
        SoLineFragmentId_Deprecated,
        SoDocEntry,
        SoLineNum,
        ReleasedQty,
        PickedQty
    FROM dbo.PickListRecord
    WHERE SoLineFragmentId_Deprecated IS NOT NULL;
END
GO
