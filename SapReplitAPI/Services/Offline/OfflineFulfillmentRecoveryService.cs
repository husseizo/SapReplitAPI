using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SapReplitAPI.Models.Offline;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Services.Offline;

/// <summary>
/// V2 Recovery service: idempotent SAP reconstruction after connectivity is restored.
///
/// STAGE CHECKPOINTING:
///   Each SAP mutation persists RecoveryStage BEFORE advancing.
///   On restart, completed stages are skipped. Never duplicate a SAP document.
///
/// CROSS-PROCESS SAFETY:
///   Claim = atomic UPDATE ... WHERE RecoveryClaimId IS NULL.
///   Only the process holding RecoveryClaimId drives this order.
///
/// IMMUTABILITY CONTRACT:
///   Physical fulfillment truth (WhsCode/BinAbsEntry/PickedQty on IsConfirmed picks)
///   is NEVER altered. If SAP disagrees → ReconciliationRequired. No silent reallocation.
///
/// ZERO SAP MUTATIONS GUARD:
///   All SAP mutation methods are grouped and clearly labeled.
///   During testing/engineering phase: Enabled=false prevents any calls reaching this service.
/// </summary>
public sealed class OfflineFulfillmentRecoveryService
{
    private readonly NeonDbContext _neon;
    private readonly OfflineFulfillmentOptions _opts;
    private readonly OfflineSapAdapter _sap;
    private readonly ILogger<OfflineFulfillmentRecoveryService> _log;

    public OfflineFulfillmentRecoveryService(
        NeonDbContext neon,
        IOptions<OfflineFulfillmentOptions> opts,
        OfflineSapAdapter sap,
        ILogger<OfflineFulfillmentRecoveryService> log)
    {
        _neon = neon;
        _opts = opts.Value;
        _sap  = sap;
        _log  = log;
    }

    // ── Public entry: batch recovery ──────────────────────────────────────────

    /// <summary>
    /// Recovers up to RecoveryBatchSize orders in WaitingForRecovery state.
    /// Called by OfflineFulfillmentRecoveryJob. Each order recovered independently.
    /// </summary>
    public async Task RecoverBatchAsync(CancellationToken ct = default)
    {
        var candidates = await _neon.OfflineFulfillmentOrders
            .Where(o => o.State == OfflineFulfillmentState.WaitingForRecovery
                     && o.RecoveryClaimId == null
                     && o.WorkflowVersion == FulfillmentWorkflowVersion.OfflineFulfillmentV2)
            .OrderBy(o => o.UpdatedAtUtc)
            .Take(_opts.RecoveryBatchSize)
            .Select(o => o.Id)
            .ToListAsync(ct);

        foreach (var id in candidates)
        {
            if (ct.IsCancellationRequested) break;
            await TryRecoverOneAsync(id, ct);
        }
    }

    // ── Single-order recovery ─────────────────────────────────────────────────

    private async Task TryRecoverOneAsync(int orderId, CancellationToken ct)
    {
        // Atomically claim this order — skip if already claimed by another worker
        var claimId = Guid.NewGuid();
        var claimed = await _neon.Database.ExecuteSqlRawAsync(
            @"UPDATE ""OfflineFulfillmentOrders""
              SET ""RecoveryClaimId"" = {0},
                  ""RecoveryClaimedAt"" = {1},
                  ""State"" = {2},
                  ""UpdatedAtUtc"" = {1}
              WHERE ""Id"" = {3}
                AND ""RecoveryClaimId"" IS NULL
                AND ""State"" = {4}",
            claimId, DateTime.UtcNow,
            OfflineFulfillmentState.Recovering,
            orderId,
            OfflineFulfillmentState.WaitingForRecovery,
            ct);

        if (claimed == 0)
        {
            _log.LogDebug("[OFFLINE-V2-RECOVERY] OrderId={Id} already claimed or state changed, skipping.", orderId);
            return;
        }

        _log.LogInformation("[OFFLINE-V2-RECOVERY] Claimed OrderId={Id} ClaimId={Claim}", orderId, claimId);

        try
        {
            await RecoverAsync(orderId, claimId, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[OFFLINE-V2-RECOVERY] OrderId={Id} recovery threw — marking Failed", orderId);
            await MarkFailedAsync(orderId, ex.Message, ct);
        }
    }

    private async Task RecoverAsync(int orderId, Guid claimId, CancellationToken ct)
    {
        var order = await _neon.OfflineFulfillmentOrders
            .Include(o => o.Lines)
            .Include(o => o.Picks.Where(p => p.IsConfirmed))
            .Include(o => o.Reservations)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw new InvalidOperationException($"OfflineFulfillmentOrder {orderId} vanished after claim.");

        var confirmedPicks = order.Picks.Where(p => p.IsConfirmed).ToList();
        if (confirmedPicks.Count == 0)
        {
            _log.LogWarning("[OFFLINE-V2-RECOVERY] OrderId={Id} has no confirmed picks — marking Reconciliation", orderId);
            await MarkReconciliationAsync(orderId, ReconciliationReasonCode.SapPreflightFailed,
                "No confirmed pick records found during recovery.", ct);
            return;
        }

        // ── Stage 1: Create SAP Sales Order ───────────────────────────────────
        if (order.RecoveryStage == RecoveryStage.None)
        {
            _log.LogInformation("[OFFLINE-V2-RECOVERY] OrderId={Id} Stage=SalesOrderCreated (creating SAP ORDR)", orderId);

            var (docEntry, docNum, sapError) = await _sap.CreateOfflineRecoveryOrderAsync(order, ct);
            if (sapError is not null)
            {
                _log.LogWarning("[OFFLINE-V2-RECOVERY] OrderId={Id} SAP ORDR failed: {Err}", orderId, sapError);
                await MarkReconciliationAsync(orderId, ReconciliationReasonCode.SapPreflightFailed, sapError, ct);
                return;
            }

            order.SapSalesOrderDocEntry = docEntry;
            order.SapSalesOrderDocNum   = docNum;
            order.RecoveryStage         = RecoveryStage.SalesOrderCreated;
            order.UpdatedAtUtc          = DateTime.UtcNow;
            await _neon.SaveChangesAsync(ct);
        }

        // ── Stage 2: Create SAP Pick Lists (OPKL) ─────────────────────────────
        if (order.RecoveryStage == RecoveryStage.SalesOrderCreated)
        {
            _log.LogInformation("[OFFLINE-V2-RECOVERY] OrderId={Id} Stage=PickListsCreated (creating SAP OPKL)", orderId);

            var pkError = await _sap.CreateOfflineRecoveryPickListsAsync(
                order.SapSalesOrderDocEntry!.Value, confirmedPicks, ct);
            if (pkError is not null)
            {
                _log.LogWarning("[OFFLINE-V2-RECOVERY] OrderId={Id} SAP OPKL failed: {Err}", orderId, pkError);
                await MarkReconciliationAsync(orderId, ReconciliationReasonCode.SapPreflightFailed, pkError, ct);
                return;
            }

            order.RecoveryStage = RecoveryStage.PickListsCreated;
            order.UpdatedAtUtc  = DateTime.UtcNow;
            await _neon.SaveChangesAsync(ct);
        }

        // ── Stage 3: Replay physical picks into SAP ────────────────────────────
        if (order.RecoveryStage == RecoveryStage.PickListsCreated)
        {
            _log.LogInformation("[OFFLINE-V2-RECOVERY] OrderId={Id} Stage=PickReplayDone (replaying picks into SAP)", orderId);

            var (replayError, reconciliationCode) = await _sap.ReplayOfflinePicksAsync(
                order.SapSalesOrderDocEntry!.Value, confirmedPicks, ct);
            if (replayError is not null)
            {
                _log.LogWarning("[OFFLINE-V2-RECOVERY] OrderId={Id} SAP pick replay failed [{Code}]: {Err}",
                    orderId, reconciliationCode, replayError);
                await MarkReconciliationAsync(orderId, reconciliationCode ?? ReconciliationReasonCode.SapPreflightFailed,
                    replayError, ct);
                return;
            }

            // Transition reservations to Recovering
            foreach (var res in order.Reservations.Where(r => r.State == OfflineReservationState.PickConfirmed))
            {
                res.State       = OfflineReservationState.Recovering;
                res.UpdatedAtUtc = DateTime.UtcNow;
            }

            order.RecoveryStage = RecoveryStage.PickReplayDone;
            order.State         = OfflineFulfillmentState.PickReplayCompleted;
            order.UpdatedAtUtc  = DateTime.UtcNow;
            await _neon.SaveChangesAsync(ct);
        }

        // ── Stage 4: Create Delivery (ODLN) ───────────────────────────────────
        if (order.RecoveryStage == RecoveryStage.PickReplayDone)
        {
            _log.LogInformation("[OFFLINE-V2-RECOVERY] OrderId={Id} Stage=DeliveryCreated (creating SAP ODLN)", orderId);

            var (delDocEntry, delDocNum, delError) = await _sap.CreateOfflineRecoveryDeliveryAsync(
                order.SapSalesOrderDocEntry!.Value, order, ct);
            if (delError is not null)
            {
                _log.LogWarning("[OFFLINE-V2-RECOVERY] OrderId={Id} SAP ODLN failed: {Err}", orderId, delError);
                await MarkReconciliationAsync(orderId, ReconciliationReasonCode.SapPreflightFailed, delError, ct);
                return;
            }

            order.SapDeliveryDocEntry = delDocEntry;
            order.SapDeliveryDocNum   = delDocNum;
            order.RecoveryStage       = RecoveryStage.DeliveryCreated;
            order.State               = OfflineFulfillmentState.DeliveryCreated;
            order.UpdatedAtUtc        = DateTime.UtcNow;
            await _neon.SaveChangesAsync(ct);
        }

        // ── Stage 5: Create Invoice (OINV) ────────────────────────────────────
        if (order.RecoveryStage == RecoveryStage.DeliveryCreated)
        {
            _log.LogInformation("[OFFLINE-V2-RECOVERY] OrderId={Id} Stage=InvoiceCreated (creating SAP OINV)", orderId);

            var (invDocEntry, invDocNum, invError) = await _sap.CreateOfflineRecoveryInvoiceAsync(
                order.SapDeliveryDocEntry!.Value, order, ct);
            if (invError is not null)
            {
                _log.LogWarning("[OFFLINE-V2-RECOVERY] OrderId={Id} SAP OINV failed: {Err}", orderId, invError);
                await MarkReconciliationAsync(orderId, ReconciliationReasonCode.SapPreflightFailed, invError, ct);
                return;
            }

            order.SapInvoiceDocEntry = invDocEntry;
            order.SapInvoiceDocNum   = invDocNum;
            order.RecoveryStage      = RecoveryStage.InvoiceCreated;
            order.State              = OfflineFulfillmentState.Invoiced;
            order.UpdatedAtUtc       = DateTime.UtcNow;
            await _neon.SaveChangesAsync(ct);
        }

        // ── Final: mark completed, release reservations ────────────────────────
        if (order.RecoveryStage == RecoveryStage.InvoiceCreated)
        {
            foreach (var res in order.Reservations.Where(r => r.State == OfflineReservationState.Recovering))
            {
                res.State       = OfflineReservationState.AppliedToSAP;
                res.UpdatedAtUtc = DateTime.UtcNow;
            }

            order.State            = OfflineFulfillmentState.Completed;
            order.RecoveryClaimId  = null;
            order.RecoveryClaimedAt = null;
            order.UpdatedAtUtc     = DateTime.UtcNow;
            await _neon.SaveChangesAsync(ct);

            _log.LogInformation("[OFFLINE-V2-RECOVERY] OrderId={Id} COMPLETED. ORDR={Ordr} ODLN={Del} OINV={Inv}",
                orderId,
                order.SapSalesOrderDocEntry,
                order.SapDeliveryDocEntry,
                order.SapInvoiceDocEntry);
        }
    }

    // ── State transition helpers ──────────────────────────────────────────────

    private async Task MarkReconciliationAsync(
        int orderId, string reason, string message, CancellationToken ct)
    {
        await _neon.Database.ExecuteSqlRawAsync(
            @"UPDATE ""OfflineFulfillmentOrders""
              SET ""State"" = {0},
                  ""ReconciliationReason"" = {1},
                  ""ErrorMessage"" = {2},
                  ""RecoveryClaimId"" = NULL,
                  ""RecoveryClaimedAt"" = NULL,
                  ""UpdatedAtUtc"" = {3}
              WHERE ""Id"" = {4}",
            OfflineFulfillmentState.ReconciliationRequired,
            reason, message, DateTime.UtcNow, orderId, ct);
        _log.LogWarning("[OFFLINE-V2-RECOVERY] OrderId={Id} → ReconciliationRequired [{Reason}]", orderId, reason);
    }

    private async Task MarkFailedAsync(int orderId, string message, CancellationToken ct)
    {
        await _neon.Database.ExecuteSqlRawAsync(
            @"UPDATE ""OfflineFulfillmentOrders""
              SET ""State"" = {0},
                  ""ErrorMessage"" = {1},
                  ""RecoveryClaimId"" = NULL,
                  ""RecoveryClaimedAt"" = NULL,
                  ""UpdatedAtUtc"" = {2}
              WHERE ""Id"" = {3}",
            OfflineFulfillmentState.Failed,
            message, DateTime.UtcNow, orderId, ct);
    }

    // ── Stale claim cleanup ────────────────────────────────────────────────────

    /// <summary>
    /// Releases stale claims held past the lease duration.
    /// Called by recovery job before each batch to return stuck orders to WaitingForRecovery.
    /// </summary>
    public async Task ReleaseStaleClaimsAsync(CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-_opts.RecoveryClaimLeaseSeconds);
        var released = await _neon.Database.ExecuteSqlRawAsync(
            @"UPDATE ""OfflineFulfillmentOrders""
              SET ""State"" = {0},
                  ""RecoveryClaimId"" = NULL,
                  ""RecoveryClaimedAt"" = NULL,
                  ""UpdatedAtUtc"" = {1}
              WHERE ""State"" = {2}
                AND ""RecoveryClaimedAt"" < {3}
                AND ""WorkflowVersion"" = {4}",
            OfflineFulfillmentState.WaitingForRecovery,
            DateTime.UtcNow,
            OfflineFulfillmentState.Recovering,
            cutoff,
            FulfillmentWorkflowVersion.OfflineFulfillmentV2,
            ct);

        if (released > 0)
            _log.LogWarning("[OFFLINE-V2-RECOVERY] Released {Count} stale claims (lease > {Lease}s)",
                released, _opts.RecoveryClaimLeaseSeconds);
    }
}
