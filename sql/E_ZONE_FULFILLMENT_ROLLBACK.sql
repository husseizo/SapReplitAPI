-- Phase C Zone Fulfillment Rollback Script
-- Purpose: Remove all objects created by E_ZONE_FULFILLMENT_SCHEMA.sql
-- Safe: IF EXISTS guards on every drop
-- Order: FK-safe reverse dependency order
-- DO NOT execute unless E_ZONE_FULFILLMENT_SCHEMA.sql migration itself failed

USE MolasIntegration;
GO

PRINT '=== Phase C Rollback: removing SoLineFragment ==='
IF OBJECT_ID('dbo.SoLineFragment', 'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.SoLineFragment;
    PRINT 'OK: SoLineFragment dropped.';
END
ELSE PRINT 'INFO: SoLineFragment not found — skipping.';
GO

PRINT '=== Phase C Rollback: removing AllocationFragment ==='
IF OBJECT_ID('dbo.AllocationFragment', 'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.AllocationFragment;
    PRINT 'OK: AllocationFragment dropped.';
END
ELSE PRINT 'INFO: AllocationFragment not found — skipping.';
GO

PRINT '=== Phase C Rollback: removing AllocationPlan ==='
IF OBJECT_ID('dbo.AllocationPlan', 'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.AllocationPlan;
    PRINT 'OK: AllocationPlan dropped.';
END
ELSE PRINT 'INFO: AllocationPlan not found — skipping.';
GO

PRINT '=== Phase C Rollback: removing FulfillmentOrchestration ==='
IF OBJECT_ID('dbo.FulfillmentOrchestration', 'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.FulfillmentOrchestration;
    PRINT 'OK: FulfillmentOrchestration dropped.';
END
ELSE PRINT 'INFO: FulfillmentOrchestration not found — skipping.';
GO

PRINT '=== Phase C Rollback: removing FulfillmentRequestLine ==='
IF OBJECT_ID('dbo.FulfillmentRequestLine', 'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.FulfillmentRequestLine;
    PRINT 'OK: FulfillmentRequestLine dropped.';
END
ELSE PRINT 'INFO: FulfillmentRequestLine not found — skipping.';
GO

PRINT '=== Phase C Rollback: removing FulfillmentRequest ==='
IF OBJECT_ID('dbo.FulfillmentRequest', 'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.FulfillmentRequest;
    PRINT 'OK: FulfillmentRequest dropped.';
END
ELSE PRINT 'INFO: FulfillmentRequest not found — skipping.';
GO

PRINT '=== Phase C Rollback: removing PickerAssignment ==='
IF OBJECT_ID('dbo.PickerAssignment', 'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.PickerAssignment;
    PRINT 'OK: PickerAssignment dropped.';
END
ELSE PRINT 'INFO: PickerAssignment not found — skipping.';
GO

PRINT '=== Phase C Rollback: removing ZoneWarehousePriority ==='
IF OBJECT_ID('dbo.ZoneWarehousePriority', 'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.ZoneWarehousePriority;
    PRINT 'OK: ZoneWarehousePriority dropped.';
END
ELSE PRINT 'INFO: ZoneWarehousePriority not found — skipping.';
GO

PRINT ''
PRINT '=== Rollback complete. Verify with: SELECT name FROM sys.tables WHERE name IN (...) ==='
SELECT name FROM sys.tables
WHERE name IN (
  'ZoneWarehousePriority','PickerAssignment','FulfillmentRequest',
  'FulfillmentRequestLine','FulfillmentOrchestration','AllocationPlan',
  'AllocationFragment','SoLineFragment'
);
GO
