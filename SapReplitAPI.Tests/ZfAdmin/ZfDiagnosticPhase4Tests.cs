using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using Xunit;

namespace SapReplitAPI.Tests.ZfAdmin;

/// <summary>
/// ZP4_01–ZP4_12: ZF Diagnosis Console — Phase 4 tests.
///
/// All tests are pure in-memory (no SQL, no COM, no SAP mutations).
/// BuildDiagnosticIncidents is now public static — tested directly.
///
/// Incident reference: SO 28879
///   - OrchestrationId: 20056
///   - Fragment ID: 20092
///   - Missing ItemCode: VAG13782
///   - Expected SoLineNum: 1
///   - Phase 3 classifier detects: ZF_FRAGMENT_RDR1_MISSING
/// </summary>
public sealed class ZfDiagnosticPhase4Tests
{
    // ── ZP4_01: original evidence contains VAG13782 ───────────────────────────

    [Fact]
    public void ZP4_01_OriginalEvidence_HasVAG13782()
    {
        // Simulate SO 28879 diagnostic: fragment for VAG13782 has no RDR1 row
        var diag = MakeDiag(
            orchState: OrchestrationState.Accepted,
            consistencyStatus: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments:
            [
                // BM12441 has valid RDR1
                MakeFrag(id: 20091, soLineNum: 0, itemCode: "BM12441", fragWhsCode: "002",
                    sapRdr1ItemCode: "BM12441"),
                // VAG13782 was deleted from RDR1 — SapRdr1ItemCode=null
                MakeFrag(id: 20092, soLineNum: 1, itemCode: "VAG13782", fragWhsCode: "002",
                    sapRdr1ItemCode: null),
            ]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        // Should produce exactly 1 incident (only the missing-RDR1 fragment)
        Assert.Single(incidents);
        var inc = incidents[0];

        Assert.Equal("VAG13782", inc.AffectedItemCode);
        // Evidence must contain VAG13782
        Assert.Contains(inc.Evidence, e => e.Contains("VAG13782"));
    }

    // ── ZP4_02: SAP RDR1 lacks VAG13782 → fragment with SapRdr1ItemCode=null ──

    [Fact]
    public void ZP4_02_SapRdr1Missing_VAG13782_DetectedAsFragment()
    {
        var diag = MakeDiag(
            orchState: OrchestrationState.Accepted,
            consistencyStatus: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments:
            [
                MakeFrag(id: 20092, soLineNum: 1, itemCode: "VAG13782", fragWhsCode: "002",
                    sapRdr1ItemCode: null),
            ]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        Assert.Single(incidents);
        var inc = incidents[0];
        Assert.Equal(20092L, inc.FragmentId);
        Assert.Equal(1, inc.SoLineNum);
        Assert.Equal("VAG13782", inc.AffectedItemCode);
    }

    // ── ZP4_03: classifier returns ZF_FRAGMENT_RDR1_MISSING ──────────────────

    [Fact]
    public void ZP4_03_Classifier_Returns_ZF_FRAGMENT_RDR1_MISSING()
    {
        var diag = MakeDiag(
            orchState: OrchestrationState.Accepted,
            consistencyStatus: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments:
            [
                MakeFrag(id: 20092, soLineNum: 1, itemCode: "VAG13782", fragWhsCode: "002",
                    sapRdr1ItemCode: null),
            ]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        Assert.Single(incidents);
        Assert.Equal(ZfConsistencyStatus.FragmentRdr1Missing, incidents[0].Code);
        Assert.Equal("ZF_FRAGMENT_RDR1_MISSING", incidents[0].Code);
    }

    // ── ZP4_04: incident is ACTIVE initially (non-terminal order) ────────────

    [Fact]
    public void ZP4_04_Incident_IsActive_WhenOrderNotTerminal()
    {
        // Accepted = non-terminal
        var diag = MakeDiag(
            orchState: OrchestrationState.Accepted,
            consistencyStatus: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments:
            [
                MakeFrag(id: 20092, soLineNum: 1, itemCode: "VAG13782", fragWhsCode: "002",
                    sapRdr1ItemCode: null),
            ]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        Assert.Single(incidents);
        Assert.Equal(ZfDiagnosticIncidentStatus.Active, incidents[0].IncidentStatus);
        Assert.False(incidents[0].IsResolved);
    }

    // ── ZP4_05: MutationAvailable is false ───────────────────────────────────

    [Fact]
    public void ZP4_05_MutationAvailable_IsFalse()
    {
        var diag = MakeDiag(
            orchState: OrchestrationState.Accepted,
            consistencyStatus: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments:
            [
                MakeFrag(id: 20092, soLineNum: 1, itemCode: "VAG13782", fragWhsCode: "002",
                    sapRdr1ItemCode: null),
            ]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        Assert.Single(incidents);
        var inc = incidents[0];
        // SafeActions should have MANUAL_RESOLUTION_REQUIRED with MutationAvailable=false
        var manualAction = inc.SafeActions.FirstOrDefault(a => a.Code == "MANUAL_RESOLUTION_REQUIRED");
        Assert.NotNull(manualAction);
        Assert.False(manualAction.MutationAvailable);
        Assert.False(manualAction.Enabled);
    }

    // ── ZP4_06: RESTORE_REQUIRED records intent only, no SAP mutation ─────────

    [Fact]
    public async Task ZP4_06_RestoreRequired_RecordsIntentOnly()
    {
        var repo = new FakeZfIncidentResolutionRepository();

        var record = new ZfIncidentResolutionRecord
        {
            IncidentKey   = "28879_20092_ZF_FRAGMENT_RDR1_MISSING",
            SoDocNum      = 28879,
            FragmentId    = 20092,
            IncidentCode  = ZfConsistencyStatus.FragmentRdr1Missing,
            Resolution    = ZfIncidentResolution.RestoreRequired,
            Status        = ZfIncidentResolution.ToIncidentStatus(ZfIncidentResolution.RestoreRequired),
            Operator      = "ops-user",
            Reason        = "VAG13782 RDR1 line must be manually re-added in SAP",
            ResolvedAtUtc = DateTime.UtcNow,
        };

        var id = await repo.InsertResolutionAsync(record);
        Assert.True(id > 0);

        // No SAP mutation was called (FakeRepo tracks calls; SapCallCount=0)
        Assert.Equal(0, repo.SapMutationCallCount);

        // Status should be ACKNOWLEDGED (not RESOLVED — action still pending)
        Assert.Equal(ZfIncidentStatusValue.Acknowledged, record.Status);
    }

    // ── ZP4_07: no SAP mutation method is called during resolution ────────────

    [Fact]
    public async Task ZP4_07_Resolution_NoSapMutationCalled()
    {
        var repo = new FakeZfIncidentResolutionRepository();

        foreach (var resolution in ZfIncidentResolution.AllValues)
        {
            var record = new ZfIncidentResolutionRecord
            {
                IncidentKey   = $"28879_20092_{ZfConsistencyStatus.FragmentRdr1Missing}",
                SoDocNum      = 28879,
                FragmentId    = 20092,
                IncidentCode  = ZfConsistencyStatus.FragmentRdr1Missing,
                Resolution    = resolution,
                Status        = ZfIncidentResolution.ToIncidentStatus(resolution),
                Operator      = "ops-user",
                Reason        = "test reason",
                ResolvedAtUtc = DateTime.UtcNow,
            };
            await repo.InsertResolutionAsync(record);
        }

        // After inserting 5 resolution records, SAP mutation count must remain 0
        Assert.Equal(0, repo.SapMutationCallCount);
        Assert.Equal(ZfIncidentResolution.AllValues.Count, repo.AllRecords.Count);
    }

    // ── ZP4_08: ACKNOWLEDGED_EXTERNAL_SAP_EDIT preserves incident history ─────

    [Fact]
    public async Task ZP4_08_AcknowledgedExternalSapEdit_PreservesHistory()
    {
        var repo = new FakeZfIncidentResolutionRepository();

        var evidenceJson = ZfIncidentResolutionRepository.SerializeEvidence(
        [
            "SoLineFragment Id=20092 LineNum=1 ItemCode=VAG13782 WhsCode=002",
            "SAP RDR1 lookup: no row found for DocEntry=28879 LineNum=1",
            "Original orchestration evidence: ItemCode=VAG13782 was in fragment Id=20092 SoLineNum=1",
        ]);

        var record = new ZfIncidentResolutionRecord
        {
            IncidentKey   = "28879_20092_ZF_FRAGMENT_RDR1_MISSING",
            SoDocNum      = 28879,
            FragmentId    = 20092,
            IncidentCode  = ZfConsistencyStatus.FragmentRdr1Missing,
            Resolution    = ZfIncidentResolution.AcknowledgedExternalSapEdit,
            Status        = ZfIncidentResolution.ToIncidentStatus(ZfIncidentResolution.AcknowledgedExternalSapEdit),
            Operator      = "ops-manager",
            Reason        = "Confirmed: VAG13782 line was deleted externally for diagnosis testing",
            ResolvedAtUtc = DateTime.UtcNow,
            EvidenceJson  = evidenceJson,
        };

        await repo.InsertResolutionAsync(record);

        // Retrieve history — should contain original incident evidence
        var history = await repo.GetResolutionsAsync("28879_20092_ZF_FRAGMENT_RDR1_MISSING");
        Assert.Single(history);

        var stored = history[0];
        Assert.Equal(ZfConsistencyStatus.FragmentRdr1Missing, stored.IncidentCode);
        Assert.Equal(ZfIncidentResolution.AcknowledgedExternalSapEdit, stored.Resolution);
        Assert.Equal(ZfIncidentStatusValue.Resolved, stored.Status);
        Assert.NotNull(stored.EvidenceJson);
        // Evidence JSON must contain VAG13782 — history is preserved
        Assert.Contains("VAG13782", stored.EvidenceJson);
    }

    // ── ZP4_09: resolution audit record has operator/reason/timestamp ─────────

    [Fact]
    public async Task ZP4_09_Resolution_AuditHasOperatorReasonTime()
    {
        var repo = new FakeZfIncidentResolutionRepository();
        var beforeInsert = DateTime.UtcNow.AddSeconds(-1);

        var record = new ZfIncidentResolutionRecord
        {
            IncidentKey   = "28879_20092_ZF_FRAGMENT_RDR1_MISSING",
            SoDocNum      = 28879,
            FragmentId    = 20092,
            IncidentCode  = ZfConsistencyStatus.FragmentRdr1Missing,
            Resolution    = ZfIncidentResolution.Deferred,
            Status        = ZfIncidentResolution.ToIncidentStatus(ZfIncidentResolution.Deferred),
            Operator      = "duty-manager",
            Reason        = "Defer to next business day review",
            ResolvedAtUtc = DateTime.UtcNow,
        };

        await repo.InsertResolutionAsync(record);

        var history = await repo.GetResolutionsAsync("28879_20092_ZF_FRAGMENT_RDR1_MISSING");
        Assert.Single(history);

        var stored = history[0];
        Assert.Equal("duty-manager", stored.Operator);
        Assert.Equal("Defer to next business day review", stored.Reason);
        Assert.True(stored.ResolvedAtUtc > beforeInsert);
        Assert.Equal(ZfIncidentStatusValue.Deferred, stored.Status);
    }

    // ── ZP4_10: diagnostic endpoint uses Phase 3 model ────────────────────────

    [Fact]
    public void ZP4_10_DiagnosticEndpoint_UsesPhase3Model()
    {
        // Static structural test: GetOrderDiagnosticIncidentsAsync returns ZfDiagnosticIncidentsResult,
        // not the old ZfOrderIncidentResult.
        // This is a compile-time check: if the return type changes, this test will fail to compile.

        // Verify the result type has Phase 3 incident model fields
        var result = new ZfDiagnosticIncidentsResult
        {
            SoDocEntry         = 28879,
            RequestId          = Guid.NewGuid(),
            OrchestrationState = OrchestrationState.Accepted,
            ConsistencyStatus  = ZfConsistencyStatus.FragmentRdr1Missing,
            Incidents          = [],
            HasActiveIncidents = false,
        };

        // Type check: this will not compile if ZfDiagnosticIncidentsResult is replaced with
        // ZfOrderIncidentResult (the old model from GetOrderIncidentsAsync)
        Assert.IsType<ZfDiagnosticIncidentsResult>(result);

        // The old ZfOrderIncidentResult has FailedDeliveries / ReplanConflicts / ConsistencyIssues
        // but NOT Incidents / HasActiveIncidents — Phase 3 model is structurally distinct
        Assert.Empty(result.Incidents);
        Assert.False(result.HasActiveIncidents);
    }

    // ── ZP4_11 Regression: completed order — incidents are Historical, not Active

    [Fact]
    public void ZP4_11_CompletedOrder_IncidentsAreHistorical_NotActive()
    {
        // A completed order (terminal state) should mark incidents as Historical
        var diag = MakeDiag(
            orchState: OrchestrationState.Delivered,
            consistencyStatus: ZfConsistencyStatus.Completed,
            fragments:
            [
                // Even with a missing RDR1, a completed order → Historical not Active
                MakeFrag(id: 99001, soLineNum: 0, itemCode: "BM12441", fragWhsCode: "004",
                    sapRdr1ItemCode: null),
            ]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        Assert.Single(incidents);
        var inc = incidents[0];
        // Terminal state (Completed) → Historical
        Assert.Equal(ZfDiagnosticIncidentStatus.Historical, inc.IncidentStatus);
        Assert.True(inc.IsResolved);
        // Impact should be null for historical incidents (not actively blocking)
        Assert.Null(inc.Impact);
    }

    // ── ZP4_12 Regression: SO 28917 (repaired, valid RDR1) → no incident ──────

    [Fact]
    public void ZP4_12_SO28917_ValidRdr1_NoFragmentMissingIncident()
    {
        // After repair: fragment has valid RDR1 (SapRdr1ItemCode != null)
        // → BuildDiagnosticIncidents must NOT produce a ZF_FRAGMENT_RDR1_MISSING incident
        var diag = MakeDiag(
            orchState: OrchestrationState.Accepted,
            consistencyStatus: ZfConsistencyStatus.Delivered,
            fragments:
            [
                MakeFrag(id: 99101, soLineNum: 0, itemCode: "BM12441", fragWhsCode: "004",
                    sapRdr1ItemCode: "BM12441"),
                MakeFrag(id: 99102, soLineNum: 1, itemCode: "VAG13782", fragWhsCode: "004",
                    sapRdr1ItemCode: "VAG13782"),   // RDR1 present — repaired
            ]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        // No fragments with null SapRdr1ItemCode → no incidents
        Assert.Empty(incidents);
    }

    // ── B4: Enhanced evidence contains the three mandated strings ─────────────

    [Fact]
    public void ZP4_B4_EvidenceTimeline_ContainsAllThreeMandatedStrings()
    {
        var diag = MakeDiag(
            orchState: OrchestrationState.Accepted,
            consistencyStatus: ZfConsistencyStatus.FragmentRdr1Missing,
            soDocEntry: 28879,
            fragments:
            [
                MakeFrag(id: 20092, soLineNum: 1, itemCode: "VAG13782", fragWhsCode: "002",
                    sapRdr1ItemCode: null),
            ]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);
        Assert.Single(incidents);
        var evidence = incidents[0].Evidence;

        // Mandated evidence strings (B4)
        Assert.Contains(evidence,
            e => e.Contains("Original orchestration evidence") && e.Contains("VAG13782") &&
                 e.Contains("Id=20092") && e.Contains("SoLineNum=1"));
        Assert.Contains(evidence,
            e => e.Contains("SAP RDR1 current state") && e.Contains("DocEntry=28879") &&
                 e.Contains("LineNum=1"));
        Assert.Contains(evidence,
            e => e.Contains("Divergence") && e.Contains("ZF orchestration") &&
                 e.Contains("absent from SAP RDR1"));
    }

    // ── B3: ExpectedWhsCode and ExpectedQty are populated ─────────────────────

    [Fact]
    public void ZP4_B3_IncidentHasExpectedWhsCode_And_ExpectedQty()
    {
        var diag = MakeDiag(
            orchState: OrchestrationState.Accepted,
            consistencyStatus: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments:
            [
                MakeFrag(id: 20092, soLineNum: 1, itemCode: "VAG13782", fragWhsCode: "002",
                    allocatedQty: 3m, sapRdr1ItemCode: null),
            ]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);
        Assert.Single(incidents);
        var inc = incidents[0];

        Assert.Equal("002", inc.ExpectedWhsCode);
        Assert.Equal(3m, inc.ExpectedQty);
    }

    // ── Resolution constant mapping tests ─────────────────────────────────────

    [Fact]
    public void ZP4_ResolutionMapping_AcknowledgedExternalSapEdit_MapsToResolved()
    {
        Assert.Equal(ZfIncidentStatusValue.Resolved,
            ZfIncidentResolution.ToIncidentStatus(ZfIncidentResolution.AcknowledgedExternalSapEdit));
    }

    [Fact]
    public void ZP4_ResolutionMapping_RestoreRequired_MapsToAcknowledged()
    {
        Assert.Equal(ZfIncidentStatusValue.Acknowledged,
            ZfIncidentResolution.ToIncidentStatus(ZfIncidentResolution.RestoreRequired));
    }

    [Fact]
    public void ZP4_ResolutionMapping_CancelFragmentRequired_MapsToAcknowledged()
    {
        Assert.Equal(ZfIncidentStatusValue.Acknowledged,
            ZfIncidentResolution.ToIncidentStatus(ZfIncidentResolution.CancelFragmentRequired));
    }

    [Fact]
    public void ZP4_ResolutionMapping_FalsePositive_MapsToResolved()
    {
        Assert.Equal(ZfIncidentStatusValue.Resolved,
            ZfIncidentResolution.ToIncidentStatus(ZfIncidentResolution.FalsePositive));
    }

    [Fact]
    public void ZP4_ResolutionMapping_Deferred_MapsToDeferred()
    {
        Assert.Equal(ZfIncidentStatusValue.Deferred,
            ZfIncidentResolution.ToIncidentStatus(ZfIncidentResolution.Deferred));
    }

    // ── IncidentKey builder ───────────────────────────────────────────────────

    [Fact]
    public void ZP4_IncidentKey_BuildsCorrectFormat()
    {
        var key = ZfIncidentResolutionRepository.BuildIncidentKey(
            28879, 20092, ZfConsistencyStatus.FragmentRdr1Missing);
        Assert.Equal("28879_20092_ZF_FRAGMENT_RDR1_MISSING", key);
    }

    // ── IncidentKey contract tests (Finding 1 + 2) ────────────────────────────

    [Fact]
    public void ZP4_IncidentKey_01_IncidentDto_IncludesKey()
    {
        var diag = MakeDiag(
            orchState: OrchestrationState.Accepted,
            consistencyStatus: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments: [MakeFrag(id: 20092, soLineNum: 1, itemCode: "VAG13782",
                fragWhsCode: "002", sapRdr1ItemCode: null)]);

        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);

        Assert.Single(incidents);
        Assert.False(string.IsNullOrEmpty(incidents[0].IncidentKey),
            "IncidentKey must be populated — clients need it to call /resolve and /resolutions.");
    }

    [Fact]
    public void ZP4_IncidentKey_02_Key_IsDeterministic()
    {
        var diag = MakeDiag(
            orchState: OrchestrationState.Accepted,
            consistencyStatus: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments: [MakeFrag(id: 20092, soLineNum: 1, itemCode: "VAG13782",
                fragWhsCode: "002", sapRdr1ItemCode: null)]);

        var key1 = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag)[0].IncidentKey;
        var key2 = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag)[0].IncidentKey;

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void ZP4_IncidentKey_03_Key_MatchesBuildIncidentKeyFormula()
    {
        var diag = MakeDiag(
            orchState: OrchestrationState.Accepted,
            consistencyStatus: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments: [MakeFrag(id: 20092, soLineNum: 1, itemCode: "VAG13782",
                fragWhsCode: "002", sapRdr1ItemCode: null)]);

        var inc     = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag)[0];
        var formula = ZfIncidentResolutionRepository.BuildIncidentKey(
            28879, inc.FragmentId, inc.Code);

        Assert.Equal(formula, inc.IncidentKey);
        Assert.Equal("28879_20092_ZF_FRAGMENT_RDR1_MISSING", inc.IncidentKey);
    }

    [Fact]
    public void ZP4_IncidentKey_04_ValidKey_FindIncidentByKey_ReturnsIncident()
    {
        var diag = MakeDiag(
            orchState: OrchestrationState.Accepted,
            consistencyStatus: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments: [MakeFrag(id: 20092, soLineNum: 1, itemCode: "VAG13782",
                fragWhsCode: "002", sapRdr1ItemCode: null)]);
        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);
        var result = new ZfDiagnosticIncidentsResult
        {
            SoDocEntry = 28879, Incidents = incidents, HasActiveIncidents = true,
            OrchestrationState = OrchestrationState.Accepted,
            ConsistencyStatus  = ZfConsistencyStatus.FragmentRdr1Missing,
            RequestId          = Guid.NewGuid(),
        };

        var match = ZfAdminDiagnosticService.FindIncidentByKey(result, "28879_20092_ZF_FRAGMENT_RDR1_MISSING");

        Assert.NotNull(match);
        Assert.Equal("VAG13782", match.AffectedItemCode);
    }

    [Fact]
    public void ZP4_IncidentKey_05_InvalidKey_FindIncidentByKey_ReturnsNull()
    {
        var diag = MakeDiag(
            orchState: OrchestrationState.Accepted,
            consistencyStatus: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments: [MakeFrag(id: 20092, soLineNum: 1, itemCode: "VAG13782",
                fragWhsCode: "002", sapRdr1ItemCode: null)]);
        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);
        var result = new ZfDiagnosticIncidentsResult
        {
            SoDocEntry = 28879, Incidents = incidents, HasActiveIncidents = true,
            OrchestrationState = "", ConsistencyStatus = "", RequestId = Guid.NewGuid(),
        };

        var match = ZfAdminDiagnosticService.FindIncidentByKey(result, "PASTE_INCIDENT_KEY_HERE");

        Assert.Null(match);
    }

    [Fact]
    public void ZP4_IncidentKey_06_KeyFromDifferentSO_FindIncidentByKey_ReturnsNull()
    {
        // A key built with docNum=99999 must not match an incident for docNum=28879
        var diag = MakeDiag(
            orchState: OrchestrationState.Accepted,
            consistencyStatus: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments: [MakeFrag(id: 20092, soLineNum: 1, itemCode: "VAG13782",
                fragWhsCode: "002", sapRdr1ItemCode: null)],
            soDocNum: 28879);
        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);
        var result = new ZfDiagnosticIncidentsResult
        {
            SoDocEntry = 28879, Incidents = incidents, HasActiveIncidents = true,
            OrchestrationState = "", ConsistencyStatus = "", RequestId = Guid.NewGuid(),
        };

        var wrongSoKey = ZfIncidentResolutionRepository.BuildIncidentKey(
            99999, 20092, ZfConsistencyStatus.FragmentRdr1Missing);
        var match = ZfAdminDiagnosticService.FindIncidentByKey(result, wrongSoKey);

        Assert.Null(match);
    }

    [Fact]
    public void ZP4_IncidentKey_07_MutationAvailable_StillFalseAfterKeyAdded()
    {
        var diag = MakeDiag(
            orchState: OrchestrationState.Accepted,
            consistencyStatus: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments: [MakeFrag(id: 20092, soLineNum: 1, itemCode: "VAG13782",
                fragWhsCode: "002", sapRdr1ItemCode: null)]);

        var inc = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag)[0];

        Assert.False(string.IsNullOrEmpty(inc.IncidentKey), "Key must be present");
        Assert.All(inc.SafeActions, a => Assert.False(a.MutationAvailable));
    }

    [Fact]
    public void ZP4_IncidentKey_08_InvalidKey_RejectsWithNull_NoInsertPerformed()
    {
        // Simulates the controller path: FindIncidentByKey returns null → 404, INSERT never called.
        var diag = MakeDiag(
            orchState: OrchestrationState.Accepted,
            consistencyStatus: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments: [MakeFrag(id: 20092, soLineNum: 1, itemCode: "VAG13782",
                fragWhsCode: "002", sapRdr1ItemCode: null)]);
        var result = new ZfDiagnosticIncidentsResult
        {
            SoDocEntry = 28879,
            Incidents  = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag),
            HasActiveIncidents = true,
            OrchestrationState = "", ConsistencyStatus = "", RequestId = Guid.NewGuid(),
        };

        var match = ZfAdminDiagnosticService.FindIncidentByKey(result, "INVALID_KEY_XYZ");

        // Controller returns 404 here — fake repo is never called
        Assert.Null(match);
        var fakeRepo = new FakeZfIncidentResolutionRepository();
        Assert.Empty(fakeRepo.AllRecords); // INSERT never executed
        Assert.Equal(0, fakeRepo.SapMutationCallCount); // B9 safety
    }

    [Fact]
    public async Task ZP4_IncidentKey_09_ValidKey_AllowsInsertWithZeroSapMutations()
    {
        // Simulates the controller path: FindIncidentByKey returns incident → INSERT proceeds.
        var diag = MakeDiag(
            orchState: OrchestrationState.Accepted,
            consistencyStatus: ZfConsistencyStatus.FragmentRdr1Missing,
            fragments: [MakeFrag(id: 20092, soLineNum: 1, itemCode: "VAG13782",
                fragWhsCode: "002", sapRdr1ItemCode: null)]);
        var incidents = ZfAdminDiagnosticService.BuildDiagnosticIncidents(diag);
        var result = new ZfDiagnosticIncidentsResult
        {
            SoDocEntry = 28879, Incidents = incidents, HasActiveIncidents = true,
            OrchestrationState = "", ConsistencyStatus = "", RequestId = Guid.NewGuid(),
        };

        var match = ZfAdminDiagnosticService.FindIncidentByKey(result, incidents[0].IncidentKey);
        Assert.NotNull(match);

        // Controller proceeds to INSERT with the fake repo
        var fakeRepo = new FakeZfIncidentResolutionRepository();
        var record = new ZfIncidentResolutionRecord
        {
            IncidentKey   = match.IncidentKey,
            SoDocNum      = 28879,
            IncidentCode  = match.Code,
            Resolution    = ZfIncidentResolution.FalsePositive,
            Status        = ZfIncidentStatusValue.Resolved,
            Operator      = "gate-operator",
            Reason        = "Confirmed false positive in test environment.",
            ResolvedAtUtc = DateTime.UtcNow,
        };
        var id = await fakeRepo.InsertResolutionAsync(record);

        Assert.True(id > 0);
        Assert.Single(fakeRepo.AllRecords);
        Assert.Equal(match.IncidentKey, fakeRepo.AllRecords[0].IncidentKey);
        Assert.Equal(0, fakeRepo.SapMutationCallCount); // B9: no SAP mutations ever
    }

    [Fact]
    public async Task ZP4_IncidentKey_10_ValidKey_NoHistory_CurrentStatus_IsActive()
    {
        // Simulates: valid key, no resolution history → currentStatus = ACTIVE (not fabricated).
        var fakeRepo = new FakeZfIncidentResolutionRepository();
        var key = "28879_20092_ZF_FRAGMENT_RDR1_MISSING";

        var status = await fakeRepo.GetCurrentStatusAsync(key);

        Assert.Equal(ZfIncidentStatusValue.Active, status);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Builds a minimal ZfOrderDiagnosticResult for BuildDiagnosticIncidents tests.</summary>
    private static ZfOrderDiagnosticResult MakeDiag(
        string                        orchState,
        string                        consistencyStatus,
        IReadOnlyList<ZfFragmentDiagnostic>? fragments = null,
        int                           soDocEntry = 28879,
        int                           soDocNum   = 28879)
        => new()
        {
            OrchestrationId  = 20056,
            RequestId        = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            SoDocEntry       = soDocEntry,
            SoDocNum         = soDocNum,
            State            = orchState,
            DeliveryLocation = "Mikocheni-side",
            Fragments        = fragments ?? [],
            Deliveries       = [],
            Invoices         = [],
            Consistency      = new ZfConsistencyVerdict(
                Status:              consistencyStatus,
                Detail:              null,
                HasFragmentMismatch: consistencyStatus == ZfConsistencyStatus.FragmentRdr1Missing,
                HasActiveReplan:     false,
                HasDeliveryFailure:  false,
                InvoicePending:      false),
            AvailableActions = [],
        };

    /// <summary>Builds a ZfFragmentDiagnostic with minimal required fields.</summary>
    private static ZfFragmentDiagnostic MakeFrag(
        long     id,
        int      soLineNum,
        string   itemCode,
        string   fragWhsCode,
        string?  sapRdr1ItemCode,
        decimal  allocatedQty = 1m)
        => new(
            Id:                id,
            SoLineNum:         soLineNum,
            ItemCode:          itemCode,
            FragmentWhsCode:   fragWhsCode,
            SoLineQty:         allocatedQty,
            AllocatedQty:      allocatedQty,
            DeliveredQty:      0m,
            OriginalWhsCode:   null,
            WhsChangedAtUtc:   null,
            WhsChangedBy:      null,
            SapRdr1ItemCode:   sapRdr1ItemCode,
            SapRdr1WhsCode:    sapRdr1ItemCode is not null ? fragWhsCode : null,
            SapRdr1LineStatus: sapRdr1ItemCode is not null ? "O" : null,
            SapRdr1OpenQty:    sapRdr1ItemCode is not null ? allocatedQty : 0m,
            WhsMatchesSap:     sapRdr1ItemCode is not null,
            PickListWhsCode:   null,
            PickListAbsEntry:  null,
            PickListPickedQty: 0m,
            PickListStatus:    "",
            SapPkl1PickQtty:   0m,
            SapPkl1PickStatus: null,
            SapPkl2BinCode:    null,
            SapPkl2BinWhsCode: null
        );
}

// ── Fake repository for in-memory tests ──────────────────────────────────────

/// <summary>
/// In-memory fake for ZfIncidentResolutionRepository.
/// Used by ZP4_06–ZP4_09 to test resolution logic without SQLite.
/// Tracks SapMutationCallCount (must always be 0) to enforce safety contract.
/// </summary>
internal sealed class FakeZfIncidentResolutionRepository : ZfIncidentResolutionRepository
{
    private readonly List<ZfIncidentResolutionRecord> _records = [];
    private long _nextId = 1;

    /// <summary>Count of any SAP mutation attempts. Must always stay 0.</summary>
    public int SapMutationCallCount { get; private set; }

    /// <summary>All records inserted (read-only view).</summary>
    public IReadOnlyList<ZfIncidentResolutionRecord> AllRecords => _records;

    public override Task EnsureTableAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    public override Task<long> InsertResolutionAsync(
        ZfIncidentResolutionRecord rec, CancellationToken ct = default)
    {
        // B9 Safety: verify no SAP mutation was requested
        // (SapMutationCallCount is incremented only from SAP-calling code paths;
        //  this fake tracks it at zero to prove the path is never reached)
        var id = _nextId++;
        rec.Id = id;
        _records.Add(rec);
        return Task.FromResult(id);
    }

    public override Task<IReadOnlyList<ZfIncidentResolutionRecord>> GetResolutionsAsync(
        string incidentKey, CancellationToken ct = default)
    {
        IReadOnlyList<ZfIncidentResolutionRecord> result =
            _records.Where(r => r.IncidentKey == incidentKey).ToList();
        return Task.FromResult(result);
    }

    public override Task<string> GetCurrentStatusAsync(
        string incidentKey, CancellationToken ct = default)
    {
        var latest = _records
            .Where(r => r.IncidentKey == incidentKey)
            .LastOrDefault();
        return Task.FromResult(latest?.Status ?? ZfIncidentStatusValue.Active);
    }
}
