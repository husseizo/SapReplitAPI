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

public sealed class BulkPriceUpdateItem
{
    public string ItemCode { get; set; } = string.Empty;
    public int PriceListNum { get; set; }
    public decimal Price { get; set; }
    public decimal? ExpectedCurrentPrice { get; set; }
}

public sealed class BulkUpdateProductPricesResponse
{
    public Guid RequestId { get; set; }
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
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
