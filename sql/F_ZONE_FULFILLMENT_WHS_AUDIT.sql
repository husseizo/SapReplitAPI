-- F_ZONE_FULFILLMENT_WHS_AUDIT.sql
-- Post-allocation warehouse change audit columns for dbo.SoLineFragment.
--
-- OriginalWhsCode : write-once. Records the WhsCode that was current at the time
--                  of the FIRST reassignment. Stays NULL for orders that were never
--                  reassigned after allocation. The application uses COALESCE to
--                  ensure this is never overwritten once set.
-- WhsChangedAtUtc : UTC timestamp of the most-recent reassignment.
-- WhsChangedBy    : Identity (user or system) that performed the most-recent change.
--
-- All columns are nullable. No existing row is affected until a warehouse change occurs.
-- AllocationFragment.WhsCode is NOT modified — it preserves the original Tiered decision.
-- SoLineFragment.WhsCode becomes the current operational fulfillment warehouse.

ALTER TABLE dbo.SoLineFragment
  ADD OriginalWhsCode  NVARCHAR(8)  NULL,
      WhsChangedAtUtc  DATETIME2    NULL,
      WhsChangedBy     NVARCHAR(50) NULL;
GO
