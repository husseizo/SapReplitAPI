namespace SapReplitAPI.Models.Warehouse;

// ── Response shapes ────────────────────────────────────────────────────────────

/// <summary>One bin with available quantity — returned to the picker for selection.</summary>
public sealed record BinCandidateDto(
    int     BinAbsEntry,
    string  BinCode,
    string  WhsCode,
    decimal AvailableQty);

/// <summary>State of one PKL1 line as presented to the warehouse app.</summary>
public sealed class PickLineStateDto
{
    public required int      PickEntry    { get; init; }
    public required string   ItemCode     { get; init; }
    public required string   Dscription   { get; init; }
    public required string   WhsCode      { get; init; }
    public required decimal  RelQtty      { get; init; }
    public required decimal  PickQtty     { get; init; }
    public required string   PickStatus   { get; init; }
    /// <summary>Live OIBQ candidates (only populated when UiState=AwaitingBinSelection or NoStock).</summary>
    public required List<BinCandidateDto> Candidates { get; init; }
    /// <summary>Already-allocated bins from PKL2 (only populated when UiState=ReadyAllocated/BinAllocationException).</summary>
    public required List<AllocatedBinDto> AllocatedBins { get; init; }
}

/// <summary>Bin already written to PKL2 by SAP.</summary>
public sealed record AllocatedBinDto(
    int     BinAbsEntry,
    string  BinCode,
    string  WhsCode,
    decimal RelQtty,
    decimal PickQtty);

/// <summary>Full UI state for a pick list — returned by GET /state endpoint.</summary>
public sealed class PickListStateResponseDto
{
    public required int              AbsEntry      { get; init; }
    public required string           SapStatus     { get; init; }   // raw "R","Y","P","D","C"
    public required string           UiState       { get; init; }   // PickListUiState.ToString()
    public required string           UiLabel       { get; init; }   // "Ready to Pick", "No Stock in Bins", etc.
    public required string           UiAction      { get; init; }   // "Select Bin & Pick", "Confirm Pick", etc.
    public required string           SupportingText{ get; init; }
    public required List<PickLineStateDto> Lines   { get; init; }
}

// ── Request shapes ─────────────────────────────────────────────────────────────

/// <summary>One bin allocation selected by the picker.</summary>
public sealed class BinAllocationRequestDto
{
    public required int     BinAbsEntry { get; init; }
    public required decimal Qty         { get; init; }
}

/// <summary>One pick line confirmation from the picker.</summary>
public sealed class ConfirmPickLineDto
{
    public required int                           PickEntry           { get; init; }
    /// <summary>
    /// Desired final SAP PickQtty — cumulative total, NOT an additive delta.
    /// SAP DI API pl.Lines.PickedQuantity is a desired-state assignment, so this must be
    /// the complete final quantity the picker wants SAP to record, not just the increment.
    /// Example: if SAP already has PickQtty=2 and picker adds 1 more, send 3 (not 1).
    /// Bin allocations must sum to this value.
    /// </summary>
    public required decimal                       DesiredFinalPickQty { get; init; }
    public required List<BinAllocationRequestDto> Bins                { get; init; }
}

/// <summary>POST body for /api/warehouse/pick-lists/{absEntry}/confirm-pick.</summary>
public sealed class ConfirmPickRequest
{
    public required List<ConfirmPickLineDto> Lines { get; init; }
}

// ── Confirm result shapes ──────────────────────────────────────────────────────

public sealed class LineConfirmResultDto
{
    public required int     PickEntry     { get; init; }
    public required bool    Success       { get; init; }
    public required decimal PickedQty     { get; init; }
    public required string? ItemCode      { get; init; }
    public required string? Error         { get; init; }
    /// <summary>"Validation" | "SapError" | "StaleConflict" — null on success.</summary>
    public required string? ErrorType     { get; init; }
    /// <summary>-2=stale conflict, -1=validation error, 0=success, >0=SAP rc.</summary>
    public required int     SapRc         { get; init; }
}

public sealed class ConfirmPickResponseDto
{
    public required int                       AbsEntry      { get; init; }
    public required bool                      Success       { get; init; }
    /// <summary>"Success" | "PartialSuccess" | "Failure".</summary>
    public required string                    OverallStatus { get; init; }
    public required List<LineConfirmResultDto> Lines        { get; init; }
    public required string?                   Error         { get; init; }
}

// ── Live SAP state (pre-confirm gate) ─────────────────────────────────────────

/// <summary>
/// Live SAP state for one PKL1 line, read immediately before ExecutePick.
/// Used by the pre-confirm stale-state gate (CHECK 2).
/// </summary>
public sealed record LiveSapPickState(
    /// <summary>OPKL.Status: "R"=Released, "Y"=Picked, "P"=PartiallyPicked, "D"=PartiallyDelivered, "C"=Closed.</summary>
    string  OpklStatus,
    /// <summary>OPKL.Canceled: "Y" or "N".</summary>
    string  OpklCanceled,
    /// <summary>PKL1.PickQtty — current picked quantity already recorded in SAP.</summary>
    decimal CurrentPickQtty,
    /// <summary>PKL1.RelQtty — authorized released quantity.</summary>
    decimal RelQtty,
    /// <summary>PKL1.PickStatus: "R"=Released, "P"=PartiallyPicked, "Y"=Picked.</summary>
    string  PickStatus);
