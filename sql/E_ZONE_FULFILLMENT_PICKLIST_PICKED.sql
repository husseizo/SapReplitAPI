-- Phase C-SAP-03: Add PickedQty to PickListRecord
-- Run against MolasIntegration database.
-- Idempotent: guarded by sys.columns check.
-- Safe: ALTER TABLE ADD with DEFAULT 0 — existing rows get PickedQty=0 (not yet picked).

USE MolasIntegration;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.PickListRecord')
      AND name = 'PickedQty'
)
BEGIN
    ALTER TABLE dbo.PickListRecord
        ADD PickedQty DECIMAL(19,6) NOT NULL
            CONSTRAINT DF_PickListRecord_PickedQty DEFAULT 0;

    PRINT 'PickedQty column added to dbo.PickListRecord.';
END
ELSE
    PRINT 'PickedQty column already exists — skipped.';
GO
