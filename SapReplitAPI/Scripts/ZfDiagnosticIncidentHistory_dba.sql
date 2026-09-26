-- DBA script: create dbo.ZfDiagnosticIncidentHistory on MolasIntegration
-- Run with a privileged account (db_owner or CREATE TABLE permission on dbo).
-- Runtime login requires SELECT, INSERT, UPDATE — NOT CREATE TABLE or DELETE.
--
-- After running this script:
--   GRANT SELECT, INSERT, UPDATE ON dbo.ZfDiagnosticIncidentHistory TO SapReplitOutboxApp;
-- SapReplitOutboxApp is the runtime SQL login used by the SapReplit API service.
-- Do not use LocalSystem or a Windows login for the runtime database principal.
--
-- This table is the durable TECHNICAL lifecycle history for ZF diagnostic incidents —
-- completely independent of dbo.ZfIncidentResolutions (the human-decision audit trail).
-- The only writer is ZfIncidentObservationService, on a scheduled Quartz job
-- (ZfIncidentObservationJob, every 5 minutes). No row is ever deleted; a RECOVERED row
-- is immutable, and a recurring IncidentKey gets a NEW row (next OccurrenceNumber)
-- rather than reusing the old one, so occurrence history is never lost.

CREATE TABLE dbo.ZfDiagnosticIncidentHistory (
    Id                  BIGINT          IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_ZfDiagnosticIncidentHistory PRIMARY KEY,

    -- Deterministic incident identifier: "{SoDocNum}_{FragmentId}_{IncidentCode}"
    -- Example: "28879_20092_ZF_FRAGMENT_RDR1_MISSING" — same formula as
    -- ZfIncidentResolutionRepository.BuildIncidentKey. Unchanged public contract.
    IncidentKey         NVARCHAR(200)   NOT NULL,

    -- 1 for a brand-new IncidentKey; incremented if the same key recovers and later
    -- reappears (recurrence). (IncidentKey, OccurrenceNumber) is unique.
    OccurrenceNumber    INT             NOT NULL,

    -- User-visible SO number (SAP DocNum)
    SoDocNum            INT             NOT NULL,

    -- Internal SAP DocEntry (ORDR.DocEntry)
    SoDocEntry          INT             NULL,

    -- ZF orchestration ID
    OrchestrationId     BIGINT          NULL,

    -- ZF fragment ID (NULL for order-level incidents)
    FragmentId          BIGINT          NULL,

    -- Detection code (currently always ZF_FRAGMENT_RDR1_MISSING — Phase 2 scope)
    IncidentCode        NVARCHAR(100)   NOT NULL,

    -- HIGH | MEDIUM | LOW | INFO (only HIGH is currently emitted)
    Severity            NVARCHAR(20)    NOT NULL,

    ItemCode            NVARCHAR(50)    NULL,
    ExpectedLineNum     INT             NULL,
    ExpectedWhsCode     NVARCHAR(20)    NULL,
    ExpectedQty         DECIMAL(18,4)   NULL,

    -- ACTIVE | RECOVERED. Set ONLY by ZfIncidentObservationService, based on a
    -- successful authoritative observation — never by the dashboard or any manual action.
    LifecycleStatus     NVARCHAR(20)    NOT NULL,

    -- First observation that detected this occurrence (immutable once set)
    FirstDetectedAtUtc  DATETIME2       NOT NULL,

    -- Most recent successful observation that still detected this occurrence.
    -- Updated on every observation cycle while LifecycleStatus = ACTIVE.
    LastObservedAtUtc   DATETIME2       NOT NULL,

    -- Set once, when LifecycleStatus transitions ACTIVE -> RECOVERED. NULL while ACTIVE.
    ClearedAtUtc        DATETIME2       NULL,

    -- Why this occurrence was marked RECOVERED (currently always
    -- NOT_DETECTED_IN_LIVE_SCAN — see ZfRecoveryReason)
    RecoveryReason      NVARCHAR(200)   NULL,

    -- Which subsystem produced this row (currently always ZfIncidentObservationJob)
    DetectionSource     NVARCHAR(60)    NOT NULL,

    -- JSON snapshot of the latest observation. Overwritten on every ACTIVE observation;
    -- frozen at whatever it held once RECOVERED (the last observation before recovery).
    EvidenceJson        NVARCHAR(MAX)   NULL,

    CreatedAtUtc        DATETIME2       NOT NULL,
    UpdatedAtUtc        DATETIME2       NOT NULL
);
GO

CREATE UNIQUE INDEX UX_ZfDiagnosticIncidentHistory_Key_Occurrence
    ON dbo.ZfDiagnosticIncidentHistory (IncidentKey, OccurrenceNumber);
GO

CREATE INDEX IX_ZfDiagnosticIncidentHistory_LifecycleStatus
    ON dbo.ZfDiagnosticIncidentHistory (LifecycleStatus);
GO

CREATE INDEX IX_ZfDiagnosticIncidentHistory_ClearedAtUtc
    ON dbo.ZfDiagnosticIncidentHistory (ClearedAtUtc);
GO

CREATE INDEX IX_ZfDiagnosticIncidentHistory_FirstDetectedAtUtc
    ON dbo.ZfDiagnosticIncidentHistory (FirstDetectedAtUtc);
GO

CREATE INDEX IX_ZfDiagnosticIncidentHistory_SoDocNum
    ON dbo.ZfDiagnosticIncidentHistory (SoDocNum);
GO

CREATE INDEX IX_ZfDiagnosticIncidentHistory_FragmentId
    ON dbo.ZfDiagnosticIncidentHistory (FragmentId);
GO

CREATE INDEX IX_ZfDiagnosticIncidentHistory_OrchestrationId
    ON dbo.ZfDiagnosticIncidentHistory (OrchestrationId);
GO
