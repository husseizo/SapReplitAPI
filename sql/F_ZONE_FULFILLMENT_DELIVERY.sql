-- Zone Fulfillment Delivery traceability tables
-- Run against MolasIntegration database.
-- FK-safe apply order: DeliveryRecord → DeliveryFragmentRecord → DeliveryFragmentBinRecord.
-- Idempotent: IF OBJECT_ID guards on every CREATE.

USE MolasIntegration;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

-- ── 1. DeliveryRecord (header — one row per ODLN creation attempt) ─────────────
IF OBJECT_ID('dbo.DeliveryRecord', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DeliveryRecord (
        Id               BIGINT        IDENTITY(1,1) NOT NULL,
        OrchestrationId  BIGINT        NOT NULL,           -- FK → dbo.FulfillmentOrchestration.Id
        SapDocEntry      INT           NULL,
        SapDocNum        INT           NULL,
        ZoneRef          NVARCHAR(50)  NOT NULL CONSTRAINT DF_DeliveryRecord_ZoneRef   DEFAULT N'ZoneFulfillment',
        DeliveryLocation NVARCHAR(50)  NOT NULL,
        CardCode         NVARCHAR(50)  NOT NULL,
        Status           NVARCHAR(20)  NOT NULL CONSTRAINT DF_DeliveryRecord_Status    DEFAULT N'Pending',
        SapErrorMessage  NVARCHAR(MAX) NULL,
        CreatedAtUtc     DATETIME2     NOT NULL CONSTRAINT DF_DeliveryRecord_Created   DEFAULT SYSUTCDATETIME(),
        UpdatedAtUtc     DATETIME2     NOT NULL CONSTRAINT DF_DeliveryRecord_Updated   DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_DeliveryRecord      PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UQ_DeliveryRecord_Orch UNIQUE (OrchestrationId),
        CONSTRAINT FK_DR_Orchestration    FOREIGN KEY (OrchestrationId)
            REFERENCES dbo.FulfillmentOrchestration (Id),
        CONSTRAINT CK_DeliveryRecord_Status CHECK (Status IN (N'Pending', N'Created', N'Failed', N'Canceled'))
    );

    CREATE INDEX IX_DeliveryRecord_Status ON dbo.DeliveryRecord (Status);

    GRANT SELECT, INSERT, UPDATE ON dbo.DeliveryRecord TO SapReplitOutboxApp;

    PRINT 'DeliveryRecord created.';
END
ELSE
    PRINT 'DeliveryRecord already exists — skipped.';
GO

-- ── 2. DeliveryFragmentRecord (child — one row per SO line in the delivery) ────
IF OBJECT_ID('dbo.DeliveryFragmentRecord', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DeliveryFragmentRecord (
        Id               BIGINT        IDENTITY(1,1) NOT NULL,
        DeliveryRecordId BIGINT        NOT NULL,
        FragmentId       BIGINT        NOT NULL,   -- FK → dbo.SoLineFragment.Id
        SoDocEntry       INT           NOT NULL,
        SoLineNum        INT           NOT NULL,
        ItemCode         NVARCHAR(50)  NOT NULL,
        WhsCode          NVARCHAR(10)  NOT NULL,
        PickedQty        DECIMAL(19,6) NOT NULL,
        PickListAbsEntry INT           NOT NULL,
        DlnLineNum       INT           NULL,       -- ODLN line index (0-based); set after Add()

        CONSTRAINT PK_DeliveryFragmentRecord PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UQ_DFR_Fragment           UNIQUE (FragmentId),
        CONSTRAINT FK_DFR_DeliveryRecord     FOREIGN KEY (DeliveryRecordId)
            REFERENCES dbo.DeliveryRecord (Id),
        CONSTRAINT FK_DFR_SoLineFragment     FOREIGN KEY (FragmentId)
            REFERENCES dbo.SoLineFragment (Id)
    );

    CREATE INDEX IX_DFR_DeliveryRecord ON dbo.DeliveryFragmentRecord (DeliveryRecordId);
    CREATE INDEX IX_DFR_Fragment       ON dbo.DeliveryFragmentRecord (FragmentId);

    GRANT SELECT, INSERT, UPDATE ON dbo.DeliveryFragmentRecord TO SapReplitOutboxApp;

    PRINT 'DeliveryFragmentRecord created.';
END
ELSE
    PRINT 'DeliveryFragmentRecord already exists — skipped.';
GO

-- ── 3. DeliveryFragmentBinRecord (child — one row per bin in a fragment pick) ──
-- Design: multi-bin explicit; never collapsed. If one fragment spans N bins,
--         there are N rows here. No bin columns on DeliveryFragmentRecord itself.
IF OBJECT_ID('dbo.DeliveryFragmentBinRecord', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DeliveryFragmentBinRecord (
        Id                       BIGINT        IDENTITY(1,1) NOT NULL,
        DeliveryFragmentRecordId BIGINT        NOT NULL,
        BinAbsEntry              INT           NOT NULL,   -- OBIN.AbsEntry / PKL2.BinAbs
        BinCode                  NVARCHAR(50)  NOT NULL,   -- OBIN.BinCode (denormalized)
        Quantity                 DECIMAL(19,6) NOT NULL,

        CONSTRAINT PK_DeliveryFragmentBinRecord PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UQ_DFBR_FragmentBin          UNIQUE (DeliveryFragmentRecordId, BinAbsEntry),
        CONSTRAINT FK_DFBR_Fragment             FOREIGN KEY (DeliveryFragmentRecordId)
            REFERENCES dbo.DeliveryFragmentRecord (Id)
    );

    CREATE INDEX IX_DFBR_Fragment ON dbo.DeliveryFragmentBinRecord (DeliveryFragmentRecordId);

    GRANT SELECT, INSERT, UPDATE ON dbo.DeliveryFragmentBinRecord TO SapReplitOutboxApp;

    PRINT 'DeliveryFragmentBinRecord created.';
END
ELSE
    PRINT 'DeliveryFragmentBinRecord already exists — skipped.';
GO

PRINT '';
PRINT '=== F_ZONE_FULFILLMENT_DELIVERY applied. Verify:';
SELECT name FROM sys.tables
WHERE name IN ('DeliveryRecord','DeliveryFragmentRecord','DeliveryFragmentBinRecord')
ORDER BY name;
GO
