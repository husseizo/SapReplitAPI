-- Zone Fulfillment Pick List traceability table
-- Run against MolasIntegration database.
-- Tracks one row per (SoLineFragment × Warehouse) created Pick List.
-- UNIQUE on (SoLineFragmentId, WhsCode) enforces one PL per SO-line per WHS.

USE MolasIntegration;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.TABLES
    WHERE TABLE_SCHEMA = 'dbo' AND TABLE_NAME = 'PickListRecord'
)
BEGIN
    CREATE TABLE dbo.PickListRecord (
        Id               BIGINT          IDENTITY(1,1) NOT NULL,
        OrchestrationId  BIGINT          NOT NULL,
        SoLineFragmentId BIGINT          NOT NULL,
        SoDocEntry       INT             NOT NULL,
        SoLineNum        INT             NOT NULL,
        WhsCode          NVARCHAR(10)    NOT NULL,
        PickListAbsEntry INT             NOT NULL,
        ReleasedQty      DECIMAL(19,6)   NOT NULL,
        Status           NVARCHAR(20)    NOT NULL CONSTRAINT DF_PickListRecord_Status DEFAULT N'Created',
        CreatedAtUtc     DATETIME2       NOT NULL CONSTRAINT DF_PickListRecord_Created DEFAULT SYSUTCDATETIME(),
        UpdatedAtUtc     DATETIME2       NOT NULL CONSTRAINT DF_PickListRecord_Updated DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_PickListRecord
            PRIMARY KEY CLUSTERED (Id),

        CONSTRAINT FK_PickListRecord_Orchestration
            FOREIGN KEY (OrchestrationId)
            REFERENCES dbo.FulfillmentOrchestration (Id),

        CONSTRAINT FK_PickListRecord_SoLineFragment
            FOREIGN KEY (SoLineFragmentId)
            REFERENCES dbo.SoLineFragment (Id),

        -- Idempotency / concurrency guard: one PL per SO-line per warehouse
        CONSTRAINT UQ_PickListRecord_Fragment_Whs
            UNIQUE (SoLineFragmentId, WhsCode)
    );

    GRANT SELECT, INSERT, UPDATE ON dbo.PickListRecord TO SapReplitOutboxApp;

    PRINT 'PickListRecord table created.';
END
ELSE
BEGIN
    PRINT 'PickListRecord table already exists — skipped.';
END
GO
