-- Zone Fulfillment Invoice traceability table (C4)
-- Run against MolasIntegration database.
-- UNIQUE(DeliveryDocEntry): one invoice record per ODLN — NOT unique per orchestration.
-- Idempotent: IF OBJECT_ID guard on CREATE.

USE MolasIntegration;
GO

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
GO

IF OBJECT_ID('dbo.InvoiceRecord', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.InvoiceRecord (
        Id               BIGINT        IDENTITY(1,1) NOT NULL,
        OrchestrationId  BIGINT        NOT NULL,           -- FK → dbo.FulfillmentOrchestration.Id
        DeliveryRecordId BIGINT        NOT NULL,           -- FK → dbo.DeliveryRecord.Id
        DeliveryDocEntry INT           NOT NULL,           -- SAP ODLN DocEntry (source document)
        SapDocEntry      INT           NULL,               -- SAP OINV DocEntry (set on Created)
        SapDocNum        INT           NULL,               -- SAP OINV DocNum (set on Created)
        Status           NVARCHAR(20)  NOT NULL CONSTRAINT DF_InvoiceRecord_Status  DEFAULT N'Pending',
        SapErrorMessage  NVARCHAR(MAX) NULL,
        CreatedAtUtc     DATETIME2     NOT NULL CONSTRAINT DF_InvoiceRecord_Created DEFAULT SYSUTCDATETIME(),
        UpdatedAtUtc     DATETIME2     NOT NULL CONSTRAINT DF_InvoiceRecord_Updated DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_InvoiceRecord           PRIMARY KEY CLUSTERED (Id),
        CONSTRAINT UQ_InvoiceRecord_Delivery  UNIQUE (DeliveryDocEntry),
        CONSTRAINT FK_IR_Orchestration        FOREIGN KEY (OrchestrationId)
            REFERENCES dbo.FulfillmentOrchestration (Id),
        CONSTRAINT FK_IR_DeliveryRecord       FOREIGN KEY (DeliveryRecordId)
            REFERENCES dbo.DeliveryRecord (Id),
        CONSTRAINT CK_InvoiceRecord_Status    CHECK (Status IN (N'Pending', N'Created', N'Failed'))
    );

    CREATE INDEX IX_InvoiceRecord_OrchestrationId  ON dbo.InvoiceRecord (OrchestrationId);
    CREATE INDEX IX_InvoiceRecord_DeliveryRecordId ON dbo.InvoiceRecord (DeliveryRecordId);
    CREATE INDEX IX_InvoiceRecord_Status           ON dbo.InvoiceRecord (Status);

    GRANT SELECT, INSERT, UPDATE ON dbo.InvoiceRecord TO SapReplitOutboxApp;

    PRINT 'InvoiceRecord created.';
END
ELSE
    PRINT 'InvoiceRecord already exists — skipped.';
GO

PRINT '';
PRINT '=== I_ZONE_FULFILLMENT_INVOICE applied. Verify:';
SELECT
    c.name AS ColumnName,
    t.name AS DataType,
    c.is_nullable
FROM sys.columns c
JOIN sys.types   t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID('dbo.InvoiceRecord')
ORDER BY c.column_id;
GO
