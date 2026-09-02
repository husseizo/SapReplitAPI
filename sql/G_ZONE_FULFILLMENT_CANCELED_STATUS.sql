-- =============================================================================
-- G_ZONE_FULFILLMENT_CANCELED_STATUS.sql
-- Target:  MolasIntegration (SQL Server)
-- Run as:  db_owner or sysadmin  (SapReplitOutboxApp does NOT have ALTER TABLE)
-- Purpose: Extend CK_DeliveryRecord_Status to include 'Canceled' state.
--          Required for the Zone Fulfillment cancelled-delivery reconciliation gate.
-- =============================================================================

USE MolasIntegration;
GO

-- Verify current constraint exists
IF OBJECT_ID('dbo.CK_DeliveryRecord_Status') IS NULL
BEGIN
    PRINT 'WARNING: CK_DeliveryRecord_Status not found. Verify table name and run context.';
    RETURN;
END
GO

-- Drop old constraint (Status IN Pending, Created, Failed)
ALTER TABLE dbo.DeliveryRecord DROP CONSTRAINT CK_DeliveryRecord_Status;
GO

-- Recreate with Canceled added
ALTER TABLE dbo.DeliveryRecord
    ADD CONSTRAINT CK_DeliveryRecord_Status
    CHECK (Status IN (N'Pending', N'Created', N'Failed', N'Canceled'));
GO

PRINT 'OK: CK_DeliveryRecord_Status updated — Canceled is now a valid status.';

-- Verify
SELECT name, definition
FROM   sys.check_constraints
WHERE  parent_object_id = OBJECT_ID('dbo.DeliveryRecord')
  AND  name = 'CK_DeliveryRecord_Status';
GO
