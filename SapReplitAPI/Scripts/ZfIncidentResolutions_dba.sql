-- DBA script: create dbo.ZfIncidentResolutions on MolasIntegration
-- Run with a privileged account (db_owner or CREATE TABLE permission on dbo).
-- Runtime login requires only SELECT, INSERT — NOT CREATE TABLE or UPDATE.
--
-- After running this script:
--   GRANT SELECT, INSERT ON dbo.ZfIncidentResolutions TO <runtime-login>;
-- where <runtime-login> is the SQL login used by the SapReplit API service.
--
-- This table is append-only. No UPDATE or DELETE is ever performed by the service.
-- Resolution history is a permanent, immutable audit trail of operator decisions
-- about ZF diagnostic incidents. Do not truncate or archive without separate sign-off.

CREATE TABLE dbo.ZfIncidentResolutions (
    Id              BIGINT          IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_ZfIncidentResolutions PRIMARY KEY,

    -- Deterministic incident identifier: "{SoDocNum}_{FragmentId}_{IncidentCode}"
    -- Example: "28879_20092_ZF_FRAGMENT_RDR1_MISSING"
    IncidentKey     NVARCHAR(200)   NOT NULL,

    -- User-visible SO number (SAP DocNum)
    SoDocNum        INT             NOT NULL,

    -- Internal SAP DocEntry (ORDR.DocEntry) — nullable in case lookup failed
    SoDocEntry      INT             NULL,

    -- ZF orchestration ID at time of resolution
    OrchestrationId BIGINT          NULL,

    -- ZF fragment ID where applicable (NULL for order-level incidents)
    FragmentId      BIGINT          NULL,

    -- Detection code that was resolved (e.g. ZF_FRAGMENT_RDR1_MISSING)
    IncidentCode    NVARCHAR(100)   NOT NULL,

    -- Operator's resolution choice (see ZfIncidentResolution constants)
    -- ACKNOWLEDGED_EXTERNAL_SAP_EDIT | RESTORE_REQUIRED | CANCEL_FRAGMENT_REQUIRED
    -- | FALSE_POSITIVE | DEFERRED
    Resolution      NVARCHAR(60)    NOT NULL,

    -- Resulting incident status after this resolution
    -- RESOLVED | ACKNOWLEDGED | DEFERRED
    Status          NVARCHAR(40)    NOT NULL,

    -- Operator name or ID
    Operator        NVARCHAR(200)   NOT NULL,

    -- Free-text reason for this resolution decision
    Reason          NVARCHAR(2000)  NOT NULL,

    -- UTC timestamp when this resolution was recorded
    ResolvedAtUtc   DATETIME2       NOT NULL,

    -- JSON snapshot of the incident's Evidence list at resolution time
    -- Preserves historical detection evidence even after status changes
    EvidenceJson    NVARCHAR(MAX)   NULL
);
GO

CREATE INDEX IX_ZfIncidentResolutions_IncidentKey
    ON dbo.ZfIncidentResolutions (IncidentKey, ResolvedAtUtc ASC);
GO

CREATE INDEX IX_ZfIncidentResolutions_SoDocNum
    ON dbo.ZfIncidentResolutions (SoDocNum);
GO

CREATE INDEX IX_ZfIncidentResolutions_ResolvedAtUtc
    ON dbo.ZfIncidentResolutions (ResolvedAtUtc DESC);
GO
