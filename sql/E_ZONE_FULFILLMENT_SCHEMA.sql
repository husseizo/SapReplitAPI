-- Phase C: Zone-Based Sales Order Fulfillment — MolasIntegration schema
-- Run as: sysadmin or db_owner on MolasIntegration
-- Idempotent: wrapped in IF NOT EXISTS guards

-- ── 1. Zone warehouse priority ────────────────────────────────────────────────
IF OBJECT_ID('dbo.ZoneWarehousePriority', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.ZoneWarehousePriority (
        Id       INT IDENTITY(1,1) NOT NULL,
        ZoneName NVARCHAR(50)  NOT NULL,
        WhsCode  NVARCHAR(8)   NOT NULL,
        Priority INT           NOT NULL,   -- 1=highest; lower = prefer first
        IsActive BIT           NOT NULL DEFAULT 1,
        CONSTRAINT PK_ZoneWarehousePriority PRIMARY KEY (Id),
        CONSTRAINT UQ_Zone_Whs      UNIQUE (ZoneName, WhsCode),
        CONSTRAINT UQ_Zone_Priority UNIQUE (ZoneName, Priority)
    );
END;

-- ── 2. Picker assignment ──────────────────────────────────────────────────────
IF OBJECT_ID('dbo.PickerAssignment', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.PickerAssignment (
        Id        INT IDENTITY(1,1) NOT NULL,
        WhsCode   NVARCHAR(8)   NOT NULL,
        UserId    NVARCHAR(50)  NOT NULL,
        UserName  NVARCHAR(100) NOT NULL,
        IsDefault BIT           NOT NULL DEFAULT 0,
        IsActive  BIT           NOT NULL DEFAULT 1,
        CONSTRAINT PK_PickerAssignment PRIMARY KEY (Id),
        CONSTRAINT UQ_Picker_Whs_User UNIQUE (WhsCode, UserId)
    );
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'UIX_PickerAssignment_DefaultActive'
      AND object_id = OBJECT_ID('dbo.PickerAssignment')
)
BEGIN
    CREATE UNIQUE INDEX UIX_PickerAssignment_DefaultActive
        ON dbo.PickerAssignment (WhsCode)
        WHERE IsDefault = 1 AND IsActive = 1;
END;

-- ── 3. Fulfillment request header ─────────────────────────────────────────────
IF OBJECT_ID('dbo.FulfillmentRequest', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.FulfillmentRequest (
        Id               BIGINT IDENTITY(1,1) NOT NULL,
        RequestId        UNIQUEIDENTIFIER NOT NULL,
        PayloadHash      NVARCHAR(64)     NOT NULL,  -- SHA-256 hex
        CardCode         NVARCHAR(50)     NOT NULL,
        DocDate          DATE             NOT NULL,
        DeliveryDate     DATE             NOT NULL,
        DeliveryLocation NVARCHAR(50)     NOT NULL,
        SlpCode          INT              NULL,
        CreatedAtUtc     DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_FulfillmentRequest    PRIMARY KEY (Id),
        CONSTRAINT UQ_FulfillmentRequest_Id UNIQUE (RequestId)
    );
END;

-- ── 4. Fulfillment request lines ──────────────────────────────────────────────
IF OBJECT_ID('dbo.FulfillmentRequestLine', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.FulfillmentRequestLine (
        Id             BIGINT IDENTITY(1,1) NOT NULL,
        RequestId      UNIQUEIDENTIFIER NOT NULL,
        RequestLineId  UNIQUEIDENTIFIER NOT NULL,
        LineSeq        INT              NOT NULL,
        ItemCode       NVARCHAR(50)     NOT NULL,
        RequestedQty   DECIMAL(19,6)    NOT NULL,
        UnitPrice      DECIMAL(19,6)    NOT NULL,
        Description    NVARCHAR(200)    NULL,
        U_ItemName     NVARCHAR(200)    NULL,
        U_Manufacturer NVARCHAR(50)     NULL,
        CONSTRAINT PK_FulfillmentRequestLine    PRIMARY KEY (Id),
        CONSTRAINT UQ_FulfillmentRequestLine_Id UNIQUE (RequestLineId),
        CONSTRAINT FK_FRL_Request FOREIGN KEY (RequestId)
            REFERENCES dbo.FulfillmentRequest (RequestId)
    );
END;

-- ── 5. Fulfillment orchestration ──────────────────────────────────────────────
IF OBJECT_ID('dbo.FulfillmentOrchestration', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.FulfillmentOrchestration (
        Id               BIGINT IDENTITY(1,1) NOT NULL,
        RequestId        UNIQUEIDENTIFIER NOT NULL,
        State            NVARCHAR(50)     NOT NULL,  -- Received/Allocating/CreatingSalesOrder/etc.
        U_ReplitId       NVARCHAR(50)     NULL,
        SoDocEntry       INT              NULL,
        SoDocNum         INT              NULL,
        DeliveryLocation NVARCHAR(50)     NOT NULL,
        AllocationVersion INT             NOT NULL DEFAULT 1,
        FailureKind      NVARCHAR(50)     NULL,
        ErrorMessage     NVARCHAR(MAX)    NULL,
        CreatedAtUtc     DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAtUtc     DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_FulfillmentOrchestration    PRIMARY KEY (Id),
        CONSTRAINT UQ_FulfillmentOrchestration_Id UNIQUE (RequestId),
        CONSTRAINT FK_FO_Request FOREIGN KEY (RequestId)
            REFERENCES dbo.FulfillmentRequest (RequestId)
    );
END;

-- ── 6. Allocation plan ────────────────────────────────────────────────────────
IF OBJECT_ID('dbo.AllocationPlan', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AllocationPlan (
        Id              BIGINT IDENTITY(1,1) NOT NULL,
        OrchestrationId BIGINT        NOT NULL,
        Version         INT           NOT NULL DEFAULT 1,
        Reason          NVARCHAR(200) NOT NULL DEFAULT 'Initial',
        CreatedAtUtc    DATETIME2     NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_AllocationPlan PRIMARY KEY (Id),
        CONSTRAINT FK_AP_Orchestration FOREIGN KEY (OrchestrationId)
            REFERENCES dbo.FulfillmentOrchestration (Id)
    );
END;

-- ── 7. Allocation fragments ───────────────────────────────────────────────────
IF OBJECT_ID('dbo.AllocationFragment', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AllocationFragment (
        Id             BIGINT IDENTITY(1,1) NOT NULL,
        PlanId         BIGINT           NOT NULL,
        RequestLineId  UNIQUEIDENTIFIER NOT NULL,
        WhsCode        NVARCHAR(8)      NOT NULL,
        AllocatedQty   DECIMAL(19,6)    NOT NULL,
        UnallocatedQty DECIMAL(19,6)    NOT NULL DEFAULT 0,
        CONSTRAINT PK_AllocationFragment PRIMARY KEY (Id),
        CONSTRAINT UQ_AF_Plan_Line_Whs UNIQUE (PlanId, RequestLineId, WhsCode),
        CONSTRAINT FK_AF_Plan FOREIGN KEY (PlanId)
            REFERENCES dbo.AllocationPlan (Id)
    );
END;

-- ── 8. SO line fragments ──────────────────────────────────────────────────────
IF OBJECT_ID('dbo.SoLineFragment', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.SoLineFragment (
        Id               BIGINT IDENTITY(1,1) NOT NULL,
        OrchestrationId  BIGINT           NOT NULL,
        RequestLineId    UNIQUEIDENTIFIER NOT NULL,
        AllocationPlanId BIGINT           NOT NULL,
        SoDocEntry       INT              NOT NULL,
        SoLineNum        INT              NOT NULL,
        ItemCode         NVARCHAR(50)     NOT NULL,
        WhsCode          NVARCHAR(8)      NOT NULL,
        SoLineQty        DECIMAL(19,6)    NOT NULL,
        AllocatedQty     DECIMAL(19,6)    NOT NULL,
        UnallocatedQty   DECIMAL(19,6)    NOT NULL DEFAULT 0,
        ReleasedQty      DECIMAL(19,6)    NOT NULL DEFAULT 0,
        DeliveredQty     DECIMAL(19,6)    NOT NULL DEFAULT 0,
        CreatedAtUtc     DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        UpdatedAtUtc     DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_SoLineFragment           PRIMARY KEY (Id),
        CONSTRAINT UQ_SLF_DocEntry_LineNum     UNIQUE (SoDocEntry, SoLineNum),
        CONSTRAINT FK_SLF_Orchestration FOREIGN KEY (OrchestrationId)
            REFERENCES dbo.FulfillmentOrchestration (Id),
        CONSTRAINT FK_SLF_Plan FOREIGN KEY (AllocationPlanId)
            REFERENCES dbo.AllocationPlan (Id)
    );
    CREATE INDEX IX_SoLineFragment_Orchestration ON dbo.SoLineFragment (OrchestrationId);
    CREATE INDEX IX_SoLineFragment_RequestLine   ON dbo.SoLineFragment (RequestLineId);
END;

-- ── Seed: ZoneWarehousePriority ───────────────────────────────────────────────
MERGE dbo.ZoneWarehousePriority AS tgt
USING (VALUES
    (N'Mikocheni-side', N'003', 1),
    (N'Mikocheni-side', N'002', 2),
    (N'Mikocheni-side', N'001', 3),
    (N'Mikocheni-side', N'004', 4),
    (N'Cluster-side',   N'002', 1),
    (N'Cluster-side',   N'001', 2),
    (N'Cluster-side',   N'004', 3),
    (N'Cluster-side',   N'003', 4)
) AS src (ZoneName, WhsCode, Priority)
ON tgt.ZoneName = src.ZoneName AND tgt.WhsCode = src.WhsCode
WHEN NOT MATCHED THEN
    INSERT (ZoneName, WhsCode, Priority, IsActive)
    VALUES (src.ZoneName, src.WhsCode, src.Priority, 1);

-- ── Seed: PickerAssignment ────────────────────────────────────────────────────
MERGE dbo.PickerAssignment AS tgt
USING (VALUES
    (N'001', N'USERID21', N'Bonny',  1),
    (N'002', N'USERID24', N'Ngenge', 1),
    (N'003', N'USERID26', N'Etta',   1),
    (N'004', N'USERID23', N'Maingi', 1)
) AS src (WhsCode, UserId, UserName, IsDefault)
ON tgt.WhsCode = src.WhsCode AND tgt.UserId = src.UserId
WHEN NOT MATCHED THEN
    INSERT (WhsCode, UserId, UserName, IsDefault, IsActive)
    VALUES (src.WhsCode, src.UserId, src.UserName, src.IsDefault, 1);

-- ── Permissions: grant SapReplitOutboxApp access to new tables ─────────────
GRANT SELECT, INSERT, UPDATE ON dbo.ZoneWarehousePriority   TO SapReplitOutboxApp;
GRANT SELECT, INSERT, UPDATE ON dbo.PickerAssignment        TO SapReplitOutboxApp;
GRANT SELECT, INSERT, UPDATE ON dbo.FulfillmentRequest      TO SapReplitOutboxApp;
GRANT SELECT, INSERT, UPDATE ON dbo.FulfillmentRequestLine  TO SapReplitOutboxApp;
GRANT SELECT, INSERT, UPDATE ON dbo.FulfillmentOrchestration TO SapReplitOutboxApp;
GRANT SELECT, INSERT, UPDATE ON dbo.AllocationPlan          TO SapReplitOutboxApp;
GRANT SELECT, INSERT, UPDATE ON dbo.AllocationFragment      TO SapReplitOutboxApp;
GRANT SELECT, INSERT, UPDATE ON dbo.SoLineFragment          TO SapReplitOutboxApp;
