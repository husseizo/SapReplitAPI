-- Gate 1: Tiered Zone Allocation — additive MolasIntegration schema
-- Run BEFORE deploying the new binary that references these columns.
-- Idempotent: all changes guarded by IF NOT EXISTS / IF NOT EXISTS column checks.
-- DO NOT modify existing table definitions or seed data for ZoneWarehousePriority.

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

-- ── 1. dbo.OriginWarehousePriority ───────────────────────────────────────────
IF OBJECT_ID('dbo.OriginWarehousePriority', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.OriginWarehousePriority (
        Id             INT IDENTITY(1,1) NOT NULL,
        ZoneName       NVARCHAR(50) NOT NULL,
        OriginWhsCode  NVARCHAR(8)  NOT NULL,  -- salesperson's effective origin warehouse
        WhsCode        NVARCHAR(8)  NOT NULL,  -- zone warehouse to assign this priority
        Priority       INT          NOT NULL,   -- 1=highest; lower = prefer first
        IsActive       BIT          NOT NULL DEFAULT 1,
        CONSTRAINT PK_OriginWarehousePriority PRIMARY KEY (Id),
        CONSTRAINT UQ_OWP_Zone_Origin_Whs UNIQUE (ZoneName, OriginWhsCode, WhsCode),
        CONSTRAINT UQ_OWP_Zone_Origin_Pri UNIQUE (ZoneName, OriginWhsCode, Priority)
    );
END;

-- Seed OriginWarehousePriority rows (exact Gate 1 approved values)
-- Cluster-side: four origin scenarios
MERGE dbo.OriginWarehousePriority AS tgt
USING (VALUES
    -- Cluster-side / origin 001 (zone default): 001→002→004→003
    (N'Cluster-side', N'001', N'001', 1),
    (N'Cluster-side', N'001', N'002', 2),
    (N'Cluster-side', N'001', N'004', 3),
    (N'Cluster-side', N'001', N'003', 4),
    -- Cluster-side / origin 002: 002→001→004→003
    (N'Cluster-side', N'002', N'002', 1),
    (N'Cluster-side', N'002', N'001', 2),
    (N'Cluster-side', N'002', N'004', 3),
    (N'Cluster-side', N'002', N'003', 4),
    -- Cluster-side / origin 004: 004→001→002→003
    (N'Cluster-side', N'004', N'004', 1),
    (N'Cluster-side', N'004', N'001', 2),
    (N'Cluster-side', N'004', N'002', 3),
    (N'Cluster-side', N'004', N'003', 4),
    -- Cluster-side / origin 003 (exception priority): 003→001→002→004
    (N'Cluster-side', N'003', N'003', 1),
    (N'Cluster-side', N'003', N'001', 2),
    (N'Cluster-side', N'003', N'002', 3),
    (N'Cluster-side', N'003', N'004', 4),
    -- Mikocheni-side: all origins normalize to effective 003 before lookup
    -- 003→001→002→004
    (N'Mikocheni-side', N'003', N'003', 1),
    (N'Mikocheni-side', N'003', N'001', 2),
    (N'Mikocheni-side', N'003', N'002', 3),
    (N'Mikocheni-side', N'003', N'004', 4)
) AS src (ZoneName, OriginWhsCode, WhsCode, Priority)
ON  tgt.ZoneName      = src.ZoneName
AND tgt.OriginWhsCode = src.OriginWhsCode
AND tgt.WhsCode       = src.WhsCode
WHEN NOT MATCHED THEN
    INSERT (ZoneName, OriginWhsCode, WhsCode, Priority, IsActive)
    VALUES (src.ZoneName, src.OriginWhsCode, src.WhsCode, src.Priority, 1);

-- ── 2. FulfillmentRequest — add OriginWhsCode ────────────────────────────────
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE  object_id = OBJECT_ID('dbo.FulfillmentRequest') AND name = 'OriginWhsCode'
)
    ALTER TABLE dbo.FulfillmentRequest
        ADD OriginWhsCode NVARCHAR(8) NULL;

-- ── 3. FulfillmentOrchestration — add tiered audit fields ────────────────────
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE  object_id = OBJECT_ID('dbo.FulfillmentOrchestration') AND name = 'OriginWhsCode'
)
    ALTER TABLE dbo.FulfillmentOrchestration
        ADD OriginWhsCode NVARCHAR(8) NULL;

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE  object_id = OBJECT_ID('dbo.FulfillmentOrchestration') AND name = 'EffectiveOrigin'
)
    ALTER TABLE dbo.FulfillmentOrchestration
        ADD EffectiveOrigin NVARCHAR(8) NULL;

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE  object_id = OBJECT_ID('dbo.FulfillmentOrchestration') AND name = 'AllocationTier'
)
    ALTER TABLE dbo.FulfillmentOrchestration
        ADD AllocationTier INT NULL;

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE  object_id = OBJECT_ID('dbo.FulfillmentOrchestration') AND name = 'AllocationReason'
)
    ALTER TABLE dbo.FulfillmentOrchestration
        ADD AllocationReason NVARCHAR(500) NULL;

-- ── 4. AllocationFragment — add SourceTier ───────────────────────────────────
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE  object_id = OBJECT_ID('dbo.AllocationFragment') AND name = 'SourceTier'
)
    ALTER TABLE dbo.AllocationFragment
        ADD SourceTier INT NULL;

-- ── 5. PickListRecord — add PickedAtUtc (write-once lifecycle timestamp) ─────
-- CreatedAtUtc already exists (DEFAULT SYSUTCDATETIME() on INSERT) — reuse it.
-- PickedAtUtc = first durable system-observed transition to fully Picked status.
-- Write-once: use COALESCE(PickedAtUtc, @pickedAtUtc) in UPDATE to never overwrite.
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE  object_id = OBJECT_ID('dbo.PickListRecord') AND name = 'PickedAtUtc'
)
    ALTER TABLE dbo.PickListRecord
        ADD PickedAtUtc DATETIME2 NULL;

-- ── 6. Permissions ───────────────────────────────────────────────────────────
IF OBJECT_ID('dbo.OriginWarehousePriority', 'U') IS NOT NULL
BEGIN
    GRANT SELECT, INSERT, UPDATE ON dbo.OriginWarehousePriority TO SapReplitOutboxApp;
END;
