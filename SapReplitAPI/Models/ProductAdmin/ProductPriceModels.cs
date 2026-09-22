namespace SapReplitAPI.Models.ProductAdmin;

// ── Request / Response ────────────────────────────────────────────────────────

public sealed class UpdateProductPriceRequest
{
    public decimal Price { get; set; }
    public decimal? ExpectedCurrentPrice { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class UpdateProductPriceResponse
{
    public string ItemCode { get; set; } = string.Empty;
    public int PriceListNum { get; set; }
    public decimal OldPrice { get; set; }
    public decimal RequestedPrice { get; set; }
    public decimal ActualSapPrice { get; set; }
    public string Currency { get; set; } = string.Empty;
    public bool SapUpdated { get; set; }
    public bool SqliteUpdated { get; set; }
    public bool NeonUpdated { get; set; }
    public long AuditId { get; set; }
    public string ResultCode { get; set; } = string.Empty;
}

public sealed class BulkUpdateProductPricesRequest
{
    public Guid RequestId { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public List<BulkPriceUpdateItem> Updates { get; set; } = new();
}

/// <summary>
/// One row in a bulk price update or CSV import.
/// Canonical CSV import columns: ItemCode, PriceListNum, NewPrice, ExpectedCurrentPrice, Reason.
/// The API field remains <c>Price</c> for backward compatibility; in CSV export/import "NewPrice" maps to this field.
/// </summary>
public sealed class BulkPriceUpdateItem
{
    public string ItemCode { get; set; } = string.Empty;
    public int PriceListNum { get; set; }
    public decimal Price { get; set; }
    public decimal? ExpectedCurrentPrice { get; set; }
    /// <summary>
    /// Optional per-row reason override. If not set, the batch-level Reason from
    /// <see cref="BulkUpdateProductPricesRequest.Reason"/> is used.
    /// </summary>
    public string? Reason { get; set; }
}

public sealed class BulkUpdateProductPricesResponse
{
    public Guid RequestId { get; set; }
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    // Detailed counters
    public int AlreadyCompleted { get; set; }
    public int NoChange { get; set; }
    public int Conflict { get; set; }
    public int ValidationFailed { get; set; }
    public int SapFailed { get; set; }
    public int CacheSyncWarning { get; set; }
    public List<BulkPriceItemResult> Results { get; set; } = new();
}

public sealed class BulkPriceItemResult
{
    public string ItemCode { get; set; } = string.Empty;
    public int PriceListNum { get; set; }
    public string ResultCode { get; set; } = string.Empty;
    public decimal? OldPrice { get; set; }
    public decimal? ActualSapPrice { get; set; }
    public string? Currency { get; set; }
    public long? AuditId { get; set; }
    public string? ErrorDetail { get; set; }
    public string? Message { get; set; }
}

// ── Audit ─────────────────────────────────────────────────────────────────────

public sealed class ProductPriceAuditEntry
{
    public long Id { get; set; }
    public Guid ActionId { get; set; } = Guid.NewGuid();
    public Guid? BatchRequestId { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public int PriceListNum { get; set; }
    public decimal? OldPrice { get; set; }
    public decimal? ExpectedCurrentPrice { get; set; }
    public decimal RequestedPrice { get; set; }
    public decimal? ActualPriceAfter { get; set; }
    public string? Currency { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public DateTime? ExecutedAtUtc { get; set; }
    public string Result { get; set; } = "Pending";
    public int? SapErrorCode { get; set; }
    public string? SapErrorMessage { get; set; }
    public string? SqliteSyncResult { get; set; }
    public string? NeonSyncResult { get; set; }
    public string? EvidenceJson { get; set; }
}

// ── Result codes ─────────────────────────────────────────────────────────────

public static class PriceUpdateResult
{
    public const string Success              = "SUCCESS";
    public const string ValidationFailed     = "VALIDATION_FAILED";
    public const string ItemNotFound         = "ITEM_NOT_FOUND";
    public const string PriceListNotFound    = "PRICE_LIST_NOT_FOUND";
    public const string ConcurrencyConflict  = "CONCURRENCY_CONFLICT";
    public const string SapUpdateFailed      = "SAP_UPDATE_FAILED";
    public const string SapWriteVerificationFailed = "SAP_WRITE_VERIFICATION_FAILED";
    public const string CacheSyncWarning     = "CACHE_SYNC_WARNING";
    public const string AlreadyCompleted     = "ALREADY_COMPLETED";
}

// ── SAP price read result ─────────────────────────────────────────────────────

public sealed class SapCurrentPrice
{
    public decimal Price { get; set; }
    public string Currency { get; set; } = string.Empty;
}

// ── Preview ───────────────────────────────────────────────────────────────────

public sealed class BulkPreviewRequest
{
    public Guid RequestId { get; set; }
    public string RequestedBy { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public List<BulkPriceUpdateItem> Updates { get; set; } = new();
}

public sealed class BulkPreviewItemResult
{
    public string ItemCode { get; init; } = "";
    public string? ItemName { get; init; }
    public int PriceListNum { get; init; }
    public decimal? CurrentPrice { get; init; }
    public decimal ProposedPrice { get; init; }
    public decimal? Difference { get; init; }
    public decimal? DifferencePercent { get; init; }
    public string? Currency { get; init; }
    public decimal? ExpectedCurrentPrice { get; init; }
    public string ValidationStatus { get; init; } = "";
    public string? ValidationMessage { get; init; }
    public bool CanExecute { get; init; }
}

public sealed class BulkPreviewCurrencyTotal
{
    public string Currency { get; init; } = "";
    public decimal TotalCurrentValue { get; init; }
    public decimal TotalProposedValue { get; init; }
    public decimal NetDifference { get; init; }
    public int ItemCount { get; init; }
}

public sealed class BulkPreviewResponse
{
    public Guid RequestId { get; init; }
    public string RequestedBy { get; init; } = "";
    public int TotalRows { get; init; }
    public int ReadyRows { get; init; }
    public int NoChangeRows { get; init; }
    public int InvalidRows { get; init; }
    public int ConflictRows { get; init; }
    public IReadOnlyList<BulkPreviewCurrencyTotal> CurrencyTotals { get; init; } = [];
    public IReadOnlyList<BulkPreviewItemResult> Items { get; init; } = [];
}

// ── Cache repair ──────────────────────────────────────────────────────────────

public sealed class CacheRepairRequest
{
    public string RequestedBy { get; set; } = "";
    public string? Reason { get; set; }
    /// <summary>Optional link to the CACHE_SYNC_WARNING audit record that triggered this repair.</summary>
    public long? TriggeringAuditId { get; set; }
}

public sealed class CacheRepairResponse
{
    public string ItemCode { get; init; } = "";
    public int PriceListNum { get; init; }
    public decimal? SapPrice { get; init; }
    public string? Currency { get; init; }
    public string SqliteResult { get; init; } = "";   // OK, NOT_FOUND, FAILED
    public string NeonResult { get; init; } = "";     // OK, NOT_FOUND, FAILED
    public bool Success { get; init; }
    public string? Message { get; init; }
}

// ── Batch status ──────────────────────────────────────────────────────────────

public sealed class BulkRequestStatusResponse
{
    public Guid RequestId { get; init; }
    public int TotalRows { get; init; }
    public IReadOnlyList<ProductPriceAuditEntry> Rows { get; init; } = [];
}

// ── Preview validation status constants ──────────────────────────────────────

public static class PreviewValidationStatus
{
    public const string Ready                 = "READY";
    public const string NoChange              = "NO_CHANGE";
    public const string ItemNotFound          = "ITEM_NOT_FOUND";
    public const string PriceListNotFound     = "PRICE_LIST_NOT_FOUND";
    public const string ConcurrencyConflict   = "CONCURRENCY_CONFLICT";
    public const string InvalidPrice          = "INVALID_PRICE";
    public const string InvalidPriceList      = "INVALID_PRICE_LIST";
    public const string DuplicateItemPriceList = "DUPLICATE_ITEM_PRICE_LIST";
    public const string SapReadFailed         = "SAP_READ_FAILED";
}
