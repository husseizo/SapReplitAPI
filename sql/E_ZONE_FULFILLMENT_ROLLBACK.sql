-- Phase C Zone Fulfillment Rollback Script
-- Purpose: Remove all objects created by E_ZONE_FULFILLMENT_SCHEMA.sql,
--          E_ZONE_FULFILLMENT_PICKLIST.sql, and F_ZONE_FULFILLMENT_DELIVERY.sql
-- Safe: IF EXISTS guards on every drop
-- Order: FK-safe reverse dependency order
-- DO NOT execute unless explicitly authorized.
--
-- IMPORTANT — GetOpenDeliveries filter:
--   SapService.GetOpenDeliveries() has filter:
--     AND ISNULL(T0.U_ZoneRef,'') <> 'ZoneFulfillment'
--   Do NOT remove this filter if any ODLN with U_ZoneRef='ZoneFulfillment' exists.
--   Removing the filter when ZF deliveries exist exposes them to InvoiceFromDeliveryJob.

USE MolasIntegration;
GO

-- ── F_ZONE_FULFILLMENT_DELIVERY rollback (FK-safe: bins → fragments → header) ──

PRINT '=== Rollback: removing DeliveryFragmentBinRecord ==='
IF OBJECT_ID('dbo.DeliveryFragmentBinRecord', 'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.DeliveryFragmentBinRecord;
    PRINT 'OK: DeliveryFragmentBinRecord dropped.';
END
ELSE PRINT 'INFO: DeliveryFragmentBinRecord not found — skipping.';
GO

PRINT '=== Rollback: removing DeliveryFragmentRecord ==='
IF OBJECT_ID('dbo.DeliveryFragmentRecord', 'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.DeliveryFragmentRecord;
    PRINT 'OK: DeliveryFragmentRecord dropped.';
END
ELSE PRINT 'INFO: DeliveryFragmentRecord not found — skipping.';
GO

PRINT '=== Rollback: removing DeliveryRecord ==='
IF OBJECT_ID('dbo.DeliveryRecord', 'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.DeliveryRecord;
    PRINT 'OK: DeliveryRecord dropped.';
END
ELSE PRINT 'INFO: DeliveryRecord not found — skipping.';
GO

-- ── E_ZONE_FULFILLMENT_PICKLIST rollback ──────────────────────────────────────

PRINT '=== Rollback: removing PickListRecord ==='
IF OBJECT_ID('dbo.PickListRecord', 'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.PickListRecord;
    PRINT 'OK: PickListRecord dropped.';
END
ELSE PRINT 'INFO: PickListRecord not found — skipping.';
GO

-- ── E_ZONE_FULFILLMENT_SCHEMA rollback ────────────────────────────────────────

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
  'AllocationFragment','SoLineFragment','PickListRecord',
  'DeliveryRecord','DeliveryFragmentRecord','DeliveryFragmentBinRecord'
);
GO
