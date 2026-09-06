using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SapReplitAPI.Models.Offline;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Services.Offline;

/// <summary>
/// Core V2 service: offline order capture, reservation management, and pick confirmation.
///
/// ISOLATION GUARANTEE:
///   This service NEVER touches PendingOrders or PendingOrderLines tables.
///   This service NEVER calls PendingOrderService methods.
///   V1 behavior is unchanged regardless of this service's activity.
/// </summary>
public sealed class OfflineFulfillmentService
{
    private readonly NeonDbContext _neon;
    private readonly OfflineFulfillmentOptions _opts;
    private readonly ILogger<OfflineFulfillmentService> _log;

    public OfflineFulfillmentService(
        NeonDbContext neon,
        IOptions<OfflineFulfillmentOptions> opts,
        ILogger<OfflineFulfillmentService> log)
    {
        _neon = neon;
        _opts = opts.Value;
        _log  = log;
    }

    // ── Idempotency ────────────────────────────────────────────────────────────

    public async Task<OfflineFulfillmentOrder?> FindByOfflineIdAsync(
        Guid offlineId, CancellationToken ct = default) =>
        await _neon.OfflineFulfillmentOrders
            .Include(o => o.Lines)
            .Include(o => o.Picks)
            .Include(o => o.Reservations)
            .FirstOrDefaultAsync(o => o.OfflineId == offlineId, ct);

    // ── Phase C: Capture ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new V2 offline fulfillment order.
    /// Returns existing record if OfflineId already known (idempotent).
    /// </summary>
    public async Task<OfflineFulfillmentOrder> CaptureAsync(
        CaptureOfflineFulfillmentRequest req, CancellationToken ct = default)
    {
        // Idempotency check — same OfflineId = return existing record
        var existing = await _neon.OfflineFulfillmentOrders
            .FirstOrDefaultAsync(o => o.OfflineId == req.OfflineId, ct);
        if (existing is not null)
        {
            _log.LogInformation("[OFFLINE-V2] Request captured (idempotent return) OfflineId={Id}", req.OfflineId);
            return existing;
        }

        var order = new OfflineFulfillmentOrder
        {
            OfflineId        = req.OfflineId,
            WorkflowVersion  = FulfillmentWorkflowVersion.OfflineFulfillmentV2,
            CardCode         = req.CardCode,
            DocDate          = DateTime.SpecifyKind(req.DocDate, DateTimeKind.Utc),
            DeliveryDate     = req.DeliveryDate.HasValue
                                 ? DateTime.SpecifyKind(req.DeliveryDate.Value, DateTimeKind.Utc)
                                 : null,
            SlpCode          = req.SlpCode,
            DocCurrency      = req.DocCurrency ?? "TZS",
            DeliveryLocation = req.DeliveryLocation,
            State            = OfflineFulfillmentState.PendingOffline,
            RecoveryStage    = RecoveryStage.None,
            CreatedAtUtc     = DateTime.UtcNow,
            UpdatedAtUtc     = DateTime.UtcNow,
            Lines            = req.Lines.Select((l, i) => new OfflineFulfillmentOrderLine
            {
                RequestedLineId = l.RequestedLineId ?? Guid.NewGuid(),
                LineSeq         = i,
                ItemCode        = l.ItemCode,
                RequestedQty    = l.RequestedQty,
                UnitPrice       = l.UnitPrice,
                Description     = l.Description,
                U_ItemName      = l.U_ItemName,
                U_Manufacturer  = l.U_Manufacturer
            }).ToList()
        };

        _neon.OfflineFulfillmentOrders.Add(order);
        await _neon.SaveChangesAsync(ct);

        _log.LogInformation("[OFFLINE-V2] Request captured OfflineId={Id} CardCode={Card} DeliveryLocation={Loc}",
            order.OfflineId, order.CardCode, order.DeliveryLocation);
        return order;
    }

    // ── Phase C: Reservations ──────────────────────────────────────────────────

    /// <summary>
    /// Creates or updates offline stock reservations for an order.
    /// Checks that OfflineOperationalAvailable = MirroredAvailable - ActiveReservations >= requested.
    /// If any item is under-available, the entire request is rejected (fail-safe).
    /// </summary>
    public async Task<ReserveOfflineStockResult> ReserveStockAsync(
        int orderId,
        IReadOnlyList<OfflineReservationRequest> requests,
        CancellationToken ct = default)
    {
        var order = await _neon.OfflineFulfillmentOrders
            .Include(o => o.Reservations)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw new InvalidOperationException($"OfflineFulfillmentOrder {orderId} not found.");

        if (order.State is OfflineFulfillmentState.Cancelled or
                           OfflineFulfillmentState.Failed or
                           OfflineFulfillmentState.Completed)
            return ReserveOfflineStockResult.Rejected($"Order is in terminal state {order.State}");

        var conflicts = new List<string>();

        foreach (var req in requests)
        {
            // Sum active reservations for this item+whs+bin — excluding THIS order's existing reservation
            var activeElsewhere = await _neon.OfflineReservations
                .Where(r => r.ItemCode   == req.ItemCode
                         && r.WhsCode   == req.WhsCode
                         && r.BinAbsEntry == req.BinAbsEntry
                         && r.OfflineFulfillmentOrderId != orderId
                         && (r.State == OfflineReservationState.Reserved
                          || r.State == OfflineReservationState.PickConfirmed
                          || r.State == OfflineReservationState.Recovering))
                .SumAsync(r => r.ReservedQty, ct);

            // Mirrored available from Neon BinInventory (if bin-managed) or WarehouseInventory
            decimal mirroredAvailable;
            if (req.BinAbsEntry.HasValue)
            {
                mirroredAvailable = await _neon.BinInventories
                    .Where(b => b.ItemCode == req.ItemCode
                             && b.WhsCode == req.WhsCode
                             && b.BinAbsEntry == req.BinAbsEntry.Value)
                    .Select(b => b.BinOnHand)
                    .FirstOrDefaultAsync(ct);
            }
            else
            {
                mirroredAvailable = await _neon.WarehouseInventories
                    .Where(w => w.ItemCode == req.ItemCode && w.WhsCode == req.WhsCode)
                    .Select(w => w.AvailableToSell)
                    .FirstOrDefaultAsync(ct);
            }

            var offlineOperationalAvailable = mirroredAvailable - activeElsewhere;

            if (offlineOperationalAvailable < req.RequestedQty)
            {
                conflicts.Add(
                    $"{req.ItemCode} WHS={req.WhsCode}" +
                    $" MirroredAvail={mirroredAvailable:F2}" +
                    $" ActiveReserved={activeElsewhere:F2}" +
                    $" OfflineOperationalAvail={offlineOperationalAvailable:F2}" +
                    $" Requested={req.RequestedQty:F2}");
            }
        }

        if (conflicts.Count > 0)
        {
            _log.LogWarning("[OFFLINE-V2] Reservation rejected — insufficient offline operational available. " +
                            "OrderId={Id} Conflicts={Conflicts}", orderId, string.Join("; ", conflicts));
            return ReserveOfflineStockResult.Rejected("Insufficient offline operational available. " +
                "LAST KNOWN stock is insufficient after accounting for active offline reservations. " +
                string.Join("; ", conflicts));
        }

        // Upsert reservations
        foreach (var req in requests)
        {
            var existing2 = order.Reservations.FirstOrDefault(r =>
                r.ItemCode    == req.ItemCode &&
                r.WhsCode     == req.WhsCode  &&
                r.BinAbsEntry == req.BinAbsEntry);

            if (existing2 is not null)
            {
                existing2.ReservedQty = req.RequestedQty;
                existing2.State       = OfflineReservationState.Reserved;
                existing2.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                _neon.OfflineReservations.Add(new OfflineReservation
                {
                    OfflineFulfillmentOrderId = orderId,
                    ItemCode    = req.ItemCode,
                    WhsCode     = req.WhsCode,
                    BinAbsEntry = req.BinAbsEntry,
                    ReservedQty = req.RequestedQty,
                    State       = OfflineReservationState.Reserved,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                });
            }
        }

        order.State       = OfflineFulfillmentState.OfflinePicking;
        order.UpdatedAtUtc = DateTime.UtcNow;
        await _neon.SaveChangesAsync(ct);

        _log.LogInformation("[OFFLINE-V2] Reservation created OrderId={Id} Count={Count}",
            orderId, requests.Count);
        return ReserveOfflineStockResult.Ok();
    }

    // ── Phase D: Pick recording ────────────────────────────────────────────────

    /// <summary>
    /// Records physical pick rows for an order.
    /// Can be called multiple times (each call appends or updates pick rows).
    /// Once the order is OfflinePickConfirmed, this method returns an error.
    /// </summary>
    public async Task<RecordPickResult> RecordPickAsync(
        int orderId,
        IReadOnlyList<OfflinePickRequest> picks,
        CancellationToken ct = default)
    {
        var order = await _neon.OfflineFulfillmentOrders
            .Include(o => o.Picks)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw new InvalidOperationException($"OfflineFulfillmentOrder {orderId} not found.");

        if (order.State == OfflineFulfillmentState.OfflinePickConfirmed)
            return RecordPickResult.Rejected("Pick already confirmed — no edits allowed after Offline Confirm Pick.");

        if (order.State is OfflineFulfillmentState.Cancelled or
                           OfflineFulfillmentState.Failed or
                           OfflineFulfillmentState.Completed)
            return RecordPickResult.Rejected($"Order is in terminal state {order.State}");

        foreach (var req in picks)
        {
            // Match by RequestedLineId + WhsCode + BinAbsEntry
            var existing = order.Picks.FirstOrDefault(p =>
                p.RequestedLineId == req.RequestedLineId &&
                p.WhsCode         == req.WhsCode         &&
                p.BinAbsEntry     == req.BinAbsEntry);

            if (existing is not null)
            {
                if (existing.IsConfirmed)
                    return RecordPickResult.Rejected(
                        $"Pick for line {req.RequestedLineId} is already confirmed — cannot edit.");
                existing.ItemCode        = req.ItemCode;
                existing.RequestedQty    = req.RequestedQty;
                existing.PickedQty       = req.PickedQty;
                existing.WhsCode         = req.WhsCode;
                existing.BinAbsEntry     = req.BinAbsEntry;
                existing.BinCode         = req.BinCode;
                existing.PickerReference = req.PickerReference;
                existing.PickedAtUtc     = DateTime.UtcNow;
            }
            else
            {
                _neon.OfflineFulfillmentPicks.Add(new OfflineFulfillmentPick
                {
                    OfflineFulfillmentOrderId = orderId,
                    RequestedLineId  = req.RequestedLineId,
                    ItemCode         = req.ItemCode,
                    RequestedQty     = req.RequestedQty,
                    PickedQty        = req.PickedQty,
                    WhsCode          = req.WhsCode,
                    BinAbsEntry      = req.BinAbsEntry,
                    BinCode          = req.BinCode,
                    PickerReference  = req.PickerReference,
                    PickedAtUtc      = DateTime.UtcNow,
                    IsConfirmed      = false
                });
            }
        }

        order.State       = OfflineFulfillmentState.OfflinePicking;
        order.UpdatedAtUtc = DateTime.UtcNow;
        await _neon.SaveChangesAsync(ct);

        _log.LogInformation("[OFFLINE-V2] Offline pick recorded OrderId={Id} PickCount={Count}",
            orderId, picks.Count);
        return RecordPickResult.Ok();
    }

    // ── Phase D: Offline Confirm Pick ─────────────────────────────────────────

    /// <summary>
    /// Offline Confirm Pick — the LAST human warehouse action for V2.
    ///
    /// After this:
    ///   - All pick records become immutable for normal users.
    ///   - Reservations transition to PickConfirmed.
    ///   - Order state transitions to OfflinePickConfirmed → WaitingForRecovery.
    ///   - Recovery job picks this up when SAP/connectivity returns.
    ///
    /// Idempotent: if already confirmed, returns existing confirmId.
    /// </summary>
    public async Task<ConfirmPickResult> ConfirmPickAsync(
        int orderId, CancellationToken ct = default)
    {
        var order = await _neon.OfflineFulfillmentOrders
            .Include(o => o.Picks)
            .Include(o => o.Reservations)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw new InvalidOperationException($"OfflineFulfillmentOrder {orderId} not found.");

        // Idempotent — already confirmed
        if (order.State == OfflineFulfillmentState.OfflinePickConfirmed
         || order.State == OfflineFulfillmentState.WaitingForRecovery)
        {
            var existingId = order.Picks.FirstOrDefault()?.OfflineConfirmId ?? Guid.Empty;
            _log.LogInformation("[OFFLINE-V2] Offline pick confirmed (idempotent) OrderId={Id}", orderId);
            return ConfirmPickResult.Ok(existingId);
        }

        if (order.State is OfflineFulfillmentState.Cancelled or
                           OfflineFulfillmentState.Failed or
                           OfflineFulfillmentState.Completed)
            return ConfirmPickResult.Rejected($"Order is in terminal state {order.State}");

        if (!order.Picks.Any())
            return ConfirmPickResult.Rejected("No pick records — cannot confirm pick with nothing picked.");

        var confirmId = Guid.NewGuid();
        var now       = DateTime.UtcNow;

        // Stamp all picks as confirmed — immutable from this point
        foreach (var pick in order.Picks)
        {
            pick.IsConfirmed      = true;
            pick.ConfirmedAtUtc   = now;
            pick.OfflineConfirmId = confirmId;
        }

        // Transition reservations to PickConfirmed
        foreach (var res in order.Reservations)
        {
            if (res.State == OfflineReservationState.Reserved)
            {
                res.State       = OfflineReservationState.PickConfirmed;
                res.UpdatedAtUtc = now;
            }
        }

        order.State       = OfflineFulfillmentState.WaitingForRecovery;
        order.UpdatedAtUtc = now;
        await _neon.SaveChangesAsync(ct);

        _log.LogInformation("[OFFLINE-V2] Offline pick confirmed OrderId={Id} ConfirmId={ConfirmId}",
            orderId, confirmId);
        return ConfirmPickResult.Ok(confirmId);
    }

    // ── Query helpers ──────────────────────────────────────────────────────────

    public async Task<OfflineFulfillmentOrder?> GetByIdAsync(int id, CancellationToken ct = default) =>
        await _neon.OfflineFulfillmentOrders
            .Include(o => o.Lines)
            .Include(o => o.Picks)
            .Include(o => o.Reservations)
            .FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task<List<OfflineFulfillmentOrder>> ListAsync(
        string? state = null, int limit = 50, CancellationToken ct = default)
    {
        var q = _neon.OfflineFulfillmentOrders.AsQueryable();
        if (!string.IsNullOrWhiteSpace(state))
            q = q.Where(o => o.State == state);
        return await q.OrderByDescending(o => o.CreatedAtUtc).Take(limit).ToListAsync(ct);
    }

    /// <summary>
    /// Returns the offline operational available for one item/whs/bin combination.
    /// = MirroredAvailable - SUM(active reservations for this slot, excluding this order).
    /// Labeled as LAST KNOWN / MIRRORED STOCK — not SAP real-time.
    /// </summary>
    public async Task<MirroredStockResult> GetMirroredStockAsync(
        string itemCode, string whsCode, int? binAbsEntry,
        int? excludeOrderId = null, CancellationToken ct = default)
    {
        decimal mirrored;
        if (binAbsEntry.HasValue)
        {
            mirrored = await _neon.BinInventories
                .Where(b => b.ItemCode == itemCode
                         && b.WhsCode  == whsCode
                         && b.BinAbsEntry == binAbsEntry.Value)
                .Select(b => b.BinOnHand)
                .FirstOrDefaultAsync(ct);
        }
        else
        {
            mirrored = await _neon.WarehouseInventories
                .Where(w => w.ItemCode == itemCode && w.WhsCode == whsCode)
                .Select(w => w.AvailableToSell)
                .FirstOrDefaultAsync(ct);
        }

        var activeReserved = await _neon.OfflineReservations
            .Where(r => r.ItemCode    == itemCode
                     && r.WhsCode    == whsCode
                     && r.BinAbsEntry == binAbsEntry
                     && (r.State == OfflineReservationState.Reserved
                      || r.State == OfflineReservationState.PickConfirmed
                      || r.State == OfflineReservationState.Recovering)
                     && (excludeOrderId == null || r.OfflineFulfillmentOrderId != excludeOrderId))
            .SumAsync(r => r.ReservedQty, ct);

        return new MirroredStockResult(itemCode, whsCode, binAbsEntry, mirrored,
            activeReserved, mirrored - activeReserved);
    }
}

// ── Request / result types ─────────────────────────────────────────────────────

public sealed class CaptureOfflineFulfillmentRequest
{
    public Guid     OfflineId        { get; init; } = Guid.NewGuid();
    public string   CardCode         { get; init; } = "";
    public DateTime DocDate          { get; init; } = DateTime.UtcNow.Date;
    public DateTime? DeliveryDate    { get; init; }
    public int?     SlpCode          { get; init; }
    public string?  DocCurrency      { get; init; }
    public string   DeliveryLocation { get; init; } = "";
    public List<CaptureOfflineLine> Lines { get; init; } = new();
}

public sealed class CaptureOfflineLine
{
    public Guid?   RequestedLineId  { get; init; }
    public string  ItemCode         { get; init; } = "";
    public decimal RequestedQty     { get; init; }
    public decimal UnitPrice        { get; init; }
    public string? Description      { get; init; }
    public string? U_ItemName       { get; init; }
    public string? U_Manufacturer   { get; init; }
}

public sealed class OfflineReservationRequest
{
    public string   ItemCode     { get; init; } = "";
    public string   WhsCode      { get; init; } = "";
    public int?     BinAbsEntry  { get; init; }
    public decimal  RequestedQty { get; init; }
}

public sealed class OfflinePickRequest
{
    public Guid     RequestedLineId  { get; init; }
    public string   ItemCode         { get; init; } = "";
    public decimal  RequestedQty     { get; init; }
    public decimal  PickedQty        { get; init; }
    public string   WhsCode          { get; init; } = "";
    public int?     BinAbsEntry      { get; init; }
    public string?  BinCode          { get; init; }
    public string   PickerReference  { get; init; } = "";
}

public sealed class ReserveOfflineStockResult
{
    public bool   Success { get; private init; }
    public string? Error  { get; private init; }
    public static ReserveOfflineStockResult Ok()              => new() { Success = true };
    public static ReserveOfflineStockResult Rejected(string e) => new() { Success = false, Error = e };
}

public sealed class RecordPickResult
{
    public bool   Success { get; private init; }
    public string? Error  { get; private init; }
    public static RecordPickResult Ok()              => new() { Success = true };
    public static RecordPickResult Rejected(string e) => new() { Success = false, Error = e };
}

public sealed class ConfirmPickResult
{
    public bool   Success   { get; private init; }
    public Guid   ConfirmId { get; private init; }
    public string? Error    { get; private init; }
    public static ConfirmPickResult Ok(Guid id)       => new() { Success = true, ConfirmId = id };
    public static ConfirmPickResult Rejected(string e) => new() { Success = false, Error = e };
}

public sealed class MirroredStockResult
{
    public string  ItemCode                   { get; init; }
    public string  WhsCode                    { get; init; }
    public int?    BinAbsEntry                { get; init; }
    public decimal MirroredAvailable          { get; init; }
    public decimal ActiveOfflineReservations  { get; init; }
    public decimal OfflineOperationalAvailable { get; init; }
    /// <summary>Always labeled LAST KNOWN — not guaranteed SAP real-time stock.</summary>
    public string  DataLabel                  => "LAST KNOWN / MIRRORED STOCK";

    public MirroredStockResult(string item, string whs, int? bin,
        decimal mirrored, decimal reserved, decimal operational)
    {
        ItemCode  = item; WhsCode = whs; BinAbsEntry = bin;
        MirroredAvailable = mirrored;
        ActiveOfflineReservations = reserved;
        OfflineOperationalAvailable = operational;
    }
}
