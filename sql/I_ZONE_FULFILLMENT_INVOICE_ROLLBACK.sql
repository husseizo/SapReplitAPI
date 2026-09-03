-- Rollback: removes dbo.InvoiceRecord created by I_ZONE_FULFILLMENT_INVOICE.sql
-- Run against MolasIntegration database.
-- IRREVERSIBLE — drops all invoice records. Only run in dev/test or on explicit authorization.

USE MolasIntegration;
GO

IF OBJECT_ID('dbo.InvoiceRecord', 'U') IS NOT NULL
BEGIN
    DROP TABLE dbo.InvoiceRecord;
    PRINT 'InvoiceRecord dropped.';
END
ELSE
    PRINT 'InvoiceRecord does not exist — nothing to drop.';
GO
