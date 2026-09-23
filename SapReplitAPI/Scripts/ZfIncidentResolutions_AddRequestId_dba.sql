-- DBA script: add ResolutionRequestId to dbo.ZfIncidentResolutions on MolasIntegration
-- Run with a privileged account (db_owner or ALTER TABLE permission on dbo).
-- Runtime login (SapReplitOutboxApp) requires only SELECT, INSERT — NOT ALTER TABLE.
--
-- Purpose: client-generated idempotency key.
--   First submission with a given GUID → INSERT and return success.
--   Same GUID replay → return existing stored result without inserting again.
--   Different GUID (new operator decision) → INSERT new audit record.
--
-- The unique index is partial (WHERE ResolutionRequestId IS NOT NULL) so that
-- pre-existing rows and rows from older clients (no GUID supplied) remain valid.

IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = 'ZfIncidentResolutions' AND COLUMN_NAME = 'ResolutionRequestId'
)
BEGIN
    ALTER TABLE dbo.ZfIncidentResolutions ADD ResolutionRequestId UNIQUEIDENTIFIER NULL;
    PRINT 'Column ResolutionRequestId added.';
END
ELSE
    PRINT 'Column ResolutionRequestId already exists.';
GO

-- Filtered index requires QUOTED_IDENTIFIER ON (SQL Server requirement)
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.ZfIncidentResolutions')
      AND name = 'UX_ZfIncidentResolutions_RequestId'
)
BEGIN
    CREATE UNIQUE INDEX UX_ZfIncidentResolutions_RequestId
        ON dbo.ZfIncidentResolutions (ResolutionRequestId)
        WHERE ResolutionRequestId IS NOT NULL;
    PRINT 'Unique index UX_ZfIncidentResolutions_RequestId created.';
END
ELSE
    PRINT 'Index UX_ZfIncidentResolutions_RequestId already exists.';
GO

-- ── Performance index for GetAllLatestResolutionsAsync ────────────────────────
-- Supports: ROW_NUMBER() OVER (PARTITION BY IncidentKey ORDER BY Id DESC)
-- Used by every dashboard list and summary request (Phase 2 dashboard service).

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.ZfIncidentResolutions')
      AND name = 'IX_ZfIncidentResolutions_IncidentKey_Id'
)
BEGIN
    CREATE INDEX IX_ZfIncidentResolutions_IncidentKey_Id
        ON dbo.ZfIncidentResolutions (IncidentKey ASC, Id DESC);
    PRINT 'Index IX_ZfIncidentResolutions_IncidentKey_Id created.';
END
ELSE
    PRINT 'Index IX_ZfIncidentResolutions_IncidentKey_Id already exists.';
GO
