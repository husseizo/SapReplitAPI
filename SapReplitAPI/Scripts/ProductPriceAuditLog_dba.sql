-- DBA script: create dbo.ProductPriceAuditLog
-- Run with a privileged account (db_owner or CREATE TABLE permission on dbo).
-- Runtime login requires only SELECT, INSERT, UPDATE — NOT CREATE TABLE.
--
-- After running this script:
--   GRANT SELECT, INSERT, UPDATE ON dbo.ProductPriceAuditLog TO <runtime-login>;
-- where <runtime-login> is the SQL login used by the SapReplit API service.

CREATE TABLE dbo.ProductPriceAuditLog (
    Id                   BIGINT           IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_ProductPriceAuditLog PRIMARY KEY,
    ActionId             UNIQUEIDENTIFIER NOT NULL
        CONSTRAINT DF_ProductPriceAuditLog_ActionId DEFAULT NEWID(),
    BatchRequestId       UNIQUEIDENTIFIER NULL,
    ItemCode             NVARCHAR(50)     NOT NULL,
    PriceListNum         INT              NOT NULL,
    OldPrice             DECIMAL(18,2)    NULL,
    ExpectedCurrentPrice DECIMAL(18,2)    NULL,
    RequestedPrice       DECIMAL(18,2)    NOT NULL,
    ActualPriceAfter     DECIMAL(18,2)    NULL,
    Currency             NVARCHAR(10)     NULL,
    RequestedBy          NVARCHAR(200)    NOT NULL,
    Reason               NVARCHAR(500)    NULL,
    RequestedAtUtc       DATETIME2        NOT NULL,
    ExecutedAtUtc        DATETIME2        NULL,
    Result               NVARCHAR(40)     NOT NULL
        CONSTRAINT DF_ProductPriceAuditLog_Result DEFAULT 'Pending',
    SapErrorCode         INT              NULL,
    SapErrorMessage      NVARCHAR(1000)   NULL,
    SqliteSyncResult     NVARCHAR(20)     NULL,
    NeonSyncResult       NVARCHAR(20)     NULL,
    EvidenceJson         NVARCHAR(MAX)    NULL
);
GO

CREATE INDEX IX_ProductPriceAuditLog_ItemCode
    ON dbo.ProductPriceAuditLog (ItemCode, RequestedAtUtc DESC);
GO

CREATE INDEX IX_ProductPriceAuditLog_BatchRequestId
    ON dbo.ProductPriceAuditLog (BatchRequestId)
    WHERE BatchRequestId IS NOT NULL;
GO

CREATE INDEX IX_ProductPriceAuditLog_ActionId
    ON dbo.ProductPriceAuditLog (ActionId);
GO
