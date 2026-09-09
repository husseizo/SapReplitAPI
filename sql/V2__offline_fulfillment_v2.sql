-- ============================================================
-- Offline Fulfillment V2 — additive migration
-- Target: Neon PostgreSQL (same database as PendingOrders)
--
-- GATE: Does NOT touch existing tables:
--   PendingOrders, PendingOrderLines, or any other pre-existing table.
-- All statements are CREATE TABLE IF NOT EXISTS — safe to re-run.
-- ============================================================

-- ── 1. OfflineFulfillmentOrders ───────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS "OfflineFulfillmentOrders" (
    "Id"                     SERIAL          PRIMARY KEY,
    "OfflineId"              UUID            NOT NULL,
    "WorkflowVersion"        VARCHAR(64)     NOT NULL DEFAULT 'OfflineFulfillmentV2',
    "CardCode"               VARCHAR(64)     NOT NULL,
    "DocDate"                TIMESTAMPTZ     NOT NULL,
    "DeliveryDate"           TIMESTAMPTZ,
    "SlpCode"                INT,
    "DocCurrency"            VARCHAR(8)      NOT NULL DEFAULT 'TZS',
    "DeliveryLocation"       VARCHAR(256)    NOT NULL DEFAULT '',
    "State"                  VARCHAR(64)     NOT NULL DEFAULT 'Draft',
    "RecoveryStage"          VARCHAR(64)     NOT NULL DEFAULT 'None',
    "SapSalesOrderDocEntry"  INT,
    "SapSalesOrderDocNum"    INT,
    "SapDeliveryDocEntry"    INT,
    "SapDeliveryDocNum"      INT,
    "SapInvoiceDocEntry"     INT,
    "SapInvoiceDocNum"       INT,
    "RecoveryClaimId"        UUID,
    "RecoveryClaimedAt"      TIMESTAMPTZ,
    "ReconciliationReason"   VARCHAR(128),
    "ErrorMessage"           TEXT,
    "CreatedAtUtc"           TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    "UpdatedAtUtc"           TIMESTAMPTZ     NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_OfflineFulfillmentOrders_OfflineId"
    ON "OfflineFulfillmentOrders" ("OfflineId");

CREATE INDEX IF NOT EXISTS "IX_OfflineFulfillmentOrders_State_CardCode"
    ON "OfflineFulfillmentOrders" ("State", "CardCode");

-- ── 2. OfflineFulfillmentOrderLines ──────────────────────────────────────────

CREATE TABLE IF NOT EXISTS "OfflineFulfillmentOrderLines" (
    "Id"                        SERIAL      PRIMARY KEY,
    "OfflineFulfillmentOrderId" INT         NOT NULL
        REFERENCES "OfflineFulfillmentOrders"("Id") ON DELETE CASCADE,
    "LineSeq"                   INT         NOT NULL,
    "RequestedLineId"           UUID        NOT NULL,
    "ItemCode"                  VARCHAR(64) NOT NULL,
    "RequestedQty"              NUMERIC(18,4) NOT NULL,
    "UnitPrice"                 NUMERIC(18,4) NOT NULL,
    "Description"               TEXT,
    "U_ItemName"                VARCHAR(256),
    "U_Manufacturer"            VARCHAR(128)
);

CREATE UNIQUE INDEX IF NOT EXISTS "IX_OfflineFulfillmentOrderLines_OrderId_LineSeq"
    ON "OfflineFulfillmentOrderLines" ("OfflineFulfillmentOrderId", "LineSeq");

-- ── 3. OfflineFulfillmentPicks ────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS "OfflineFulfillmentPicks" (
    "Id"                        SERIAL          PRIMARY KEY,
    "OfflineFulfillmentOrderId" INT             NOT NULL
        REFERENCES "OfflineFulfillmentOrders"("Id") ON DELETE CASCADE,
    "RequestedLineId"           UUID            NOT NULL,
    "ItemCode"                  VARCHAR(64)     NOT NULL,
    "RequestedQty"              NUMERIC(18,4)   NOT NULL,
    "PickedQty"                 NUMERIC(18,4)   NOT NULL,
    "WhsCode"                   VARCHAR(32)     NOT NULL,
    "BinAbsEntry"               INT,
    "BinCode"                   VARCHAR(64),
    "PickerReference"           VARCHAR(128)    NOT NULL DEFAULT '',
    "PickedAtUtc"               TIMESTAMPTZ,
    "ConfirmedAtUtc"            TIMESTAMPTZ,
    "OfflineConfirmId"          UUID,
    "IsConfirmed"               BOOLEAN         NOT NULL DEFAULT FALSE
);

CREATE INDEX IF NOT EXISTS "IX_OfflineFulfillmentPicks_OrderId"
    ON "OfflineFulfillmentPicks" ("OfflineFulfillmentOrderId");

CREATE INDEX IF NOT EXISTS "IX_OfflineFulfillmentPicks_RequestedLineId"
    ON "OfflineFulfillmentPicks" ("RequestedLineId");

CREATE INDEX IF NOT EXISTS "IX_OfflineFulfillmentPicks_OfflineConfirmId"
    ON "OfflineFulfillmentPicks" ("OfflineConfirmId");

-- ── 4. OfflineReservations ────────────────────────────────────────────────────

CREATE TABLE IF NOT EXISTS "OfflineReservations" (
    "Id"                        SERIAL          PRIMARY KEY,
    "OfflineFulfillmentOrderId" INT             NOT NULL
        REFERENCES "OfflineFulfillmentOrders"("Id") ON DELETE CASCADE,
    "ItemCode"                  VARCHAR(64)     NOT NULL,
    "WhsCode"                   VARCHAR(32)     NOT NULL,
    "BinAbsEntry"               INT,
    "ReservedQty"               NUMERIC(18,4)   NOT NULL,
    "State"                     VARCHAR(32)     NOT NULL DEFAULT 'Reserved',
    "CreatedAtUtc"              TIMESTAMPTZ     NOT NULL DEFAULT NOW(),
    "UpdatedAtUtc"              TIMESTAMPTZ     NOT NULL DEFAULT NOW()
);

-- Prevent duplicate reservation entries per order/item/whs/bin
CREATE UNIQUE INDEX IF NOT EXISTS "IX_OfflineReservations_OrderId_ItemCode_WhsCode_Bin"
    ON "OfflineReservations" ("OfflineFulfillmentOrderId", "ItemCode", "WhsCode",
       COALESCE("BinAbsEntry", -1));

-- Supports the OfflineOperationalAvailable SUM query
CREATE INDEX IF NOT EXISTS "IX_OfflineReservations_ItemCode_WhsCode_Bin_State"
    ON "OfflineReservations" ("ItemCode", "WhsCode", "BinAbsEntry", "State");
