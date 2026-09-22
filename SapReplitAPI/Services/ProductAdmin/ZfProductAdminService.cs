using Microsoft.Extensions.Logging;
using SapReplitAPI.Models.ProductAdmin;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Services.ProductAdmin;

/// <summary>
/// Orchestrates single and bulk product price updates:
/// admin auth → validation → audit Pending → SAP read → concurrency check
/// → SAP write → SAP readback → SQLite update → Neon update → audit terminal.
/// </summary>
public sealed class ZfProductAdminService
{
    // Tolerance for comparing decimal SAP prices (SAP stores 2dp; use 0.005 to absorb rounding).
    private const decimal PriceTolerance = 0.005m;
    private static readonly IReadOnlySet<int> ValidPriceLists = new HashSet<int> { 1, 2, 3, 4, 5 };

    private readonly SapService _sap;
    private readonly ProductCacheService _cache;
    private readonly NeonProductSyncService _neon;
    private readonly ProductPriceAuditRepository _audit;
    private readonly ILogger<ZfProductAdminService> _log;

    public ZfProductAdminService(
        SapService sap,
        ProductCacheService cache,
        NeonProductSyncService neon,
        ProductPriceAuditRepository audit,
        ILogger<ZfProductAdminService> log)
    {
        _sap   = sap;
        _cache = cache;
        _neon  = neon;
        _audit = audit;
        _log   = log;
    }

    // ── Single price update ───────────────────────────────────────────────────

    public async Task<UpdateProductPriceResponse> UpdatePriceAsync(
        string itemCode, int priceListNum,
        UpdateProductPriceRequest req,
        CancellationToken ct = default)
    {
        var validation = Validate(itemCode, priceListNum, req.Price, req.RequestedBy);
        if (validation is not null)
            return new UpdateProductPriceResponse
            {
                ItemCode = itemCode, PriceListNum = priceListNum,
                ResultCode = PriceUpdateResult.ValidationFailed,
                RequestedPrice = req.Price, ActualSapPrice = 0
            };

        var entry = new ProductPriceAuditEntry
        {
            ItemCode             = itemCode,
            PriceListNum         = priceListNum,
            ExpectedCurrentPrice = req.ExpectedCurrentPrice,
            RequestedPrice       = req.Price,
            RequestedBy          = req.RequestedBy,
            Reason               = req.Reason,
            RequestedAtUtc       = DateTime.UtcNow,
            Result               = "Pending",
        };
        long auditId = await _audit.InsertAsync(entry, ct);

        return await ExecuteSingleUpdateAsync(itemCode, priceListNum, req.Price,
            req.ExpectedCurrentPrice, auditId, entry, ct);
    }

    // ── Bulk price update ─────────────────────────────────────────────────────

    public async Task<BulkUpdateProductPricesResponse> UpdatePricesBulkAsync(
        BulkUpdateProductPricesRequest req,
        CancellationToken ct = default)
    {
        var results = new List<BulkPriceItemResult>(req.Updates.Count);

        foreach (var item in req.Updates)
        {
            // Idempotency check — skip already-successfully-completed items.
            var existing = await _audit.FindCompletedAsync(req.RequestId, item.ItemCode, item.PriceListNum, ct);
            if (existing is not null && existing.Result == PriceUpdateResult.Success)
            {
                results.Add(new BulkPriceItemResult
                {
                    ItemCode     = item.ItemCode,
                    PriceListNum = item.PriceListNum,
                    ResultCode   = PriceUpdateResult.AlreadyCompleted,
                    OldPrice     = existing.OldPrice,
                    ActualSapPrice = existing.ActualPriceAfter,
                    Currency     = existing.Currency,
                    AuditId      = existing.Id,
                });
                continue;
            }

            var validation = Validate(item.ItemCode, item.PriceListNum, item.Price, req.RequestedBy);
            if (validation is not null)
            {
                results.Add(new BulkPriceItemResult
                {
                    ItemCode     = item.ItemCode,
                    PriceListNum = item.PriceListNum,
                    ResultCode   = PriceUpdateResult.ValidationFailed,
                    ErrorDetail  = validation,
                });
                continue;
            }

            var entry = new ProductPriceAuditEntry
            {
                BatchRequestId       = req.RequestId,
                ItemCode             = item.ItemCode,
                PriceListNum         = item.PriceListNum,
                ExpectedCurrentPrice = item.ExpectedCurrentPrice,
                RequestedPrice       = item.Price,
                RequestedBy          = req.RequestedBy,
                Reason               = req.Reason,
                RequestedAtUtc       = DateTime.UtcNow,
                Result               = "Pending",
            };
            long auditId = await _audit.InsertAsync(entry, ct);

            var single = await ExecuteSingleUpdateAsync(
                item.ItemCode, item.PriceListNum, item.Price,
                item.ExpectedCurrentPrice, auditId, entry, ct);

            results.Add(new BulkPriceItemResult
            {
                ItemCode     = item.ItemCode,
                PriceListNum = item.PriceListNum,
                ResultCode   = single.ResultCode,
                OldPrice     = single.OldPrice == 0 ? null : single.OldPrice,
                ActualSapPrice = single.SapUpdated ? single.ActualSapPrice : null,
                Currency     = single.Currency,
                AuditId      = auditId,
                ErrorDetail  = single.ResultCode == PriceUpdateResult.Success ? null : $"SAP or cache error — see audit {auditId}",
            });
        }

        int succeeded = results.Count(r => r.ResultCode is PriceUpdateResult.Success or PriceUpdateResult.AlreadyCompleted);
        return new BulkUpdateProductPricesResponse
        {
            RequestId = req.RequestId,
            Total     = results.Count,
            Succeeded = succeeded,
            Failed    = results.Count - succeeded,
            Results   = results,
        };
    }

    // ── Core execution ────────────────────────────────────────────────────────

    private async Task<UpdateProductPriceResponse> ExecuteSingleUpdateAsync(
        string itemCode, int priceListNum, decimal newPrice,
        decimal? expectedCurrentPrice, long auditId,
        ProductPriceAuditEntry entry, CancellationToken ct)
    {
        // Step 1: Read current SAP price.
        SapCurrentPrice? current;
        try
        {
            current = _sap.ReadItemPrice(itemCode, priceListNum);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ProductAdmin] SAP read failed for {Item} PL{Pl}", itemCode, priceListNum);
            await _audit.SetTerminalAsync(auditId, PriceUpdateResult.SapUpdateFailed,
                null, null, ex.Message, null, null, DateTime.UtcNow, ct);
            return Fail(itemCode, priceListNum, newPrice, PriceUpdateResult.SapUpdateFailed, auditId);
        }

        if (current is null)
        {
            string code = PriceUpdateResult.PriceListNotFound;
            // Distinguish item-not-found vs price-list-not-found.
            try { _sap.ReadItemPrice(itemCode, 3); } // PL3 exists for all items — if this throws/null, item missing
            catch { code = PriceUpdateResult.ItemNotFound; }

            await _audit.SetTerminalAsync(auditId, code, null, null, "SAP read returned null", null, null, DateTime.UtcNow, ct);
            return Fail(itemCode, priceListNum, newPrice, code, auditId);
        }

        entry.OldPrice  = current.Price;
        entry.Currency  = current.Currency;

        // Step 2: Concurrency check.
        if (expectedCurrentPrice.HasValue &&
            Math.Abs(current.Price - expectedCurrentPrice.Value) > PriceTolerance)
        {
            _log.LogWarning(
                "[ProductAdmin] CONCURRENCY_CONFLICT {Item} PL{Pl}: expected={Exp} actual={Act}",
                itemCode, priceListNum, expectedCurrentPrice.Value, current.Price);
            await _audit.SetTerminalAsync(auditId, PriceUpdateResult.ConcurrencyConflict,
                current.Price, null,
                $"Expected {expectedCurrentPrice.Value} but SAP has {current.Price}",
                null, null, DateTime.UtcNow, ct);
            return new UpdateProductPriceResponse
            {
                ItemCode       = itemCode, PriceListNum = priceListNum,
                OldPrice       = current.Price, RequestedPrice = newPrice, ActualSapPrice = current.Price,
                Currency       = current.Currency, SapUpdated = false,
                ResultCode     = PriceUpdateResult.ConcurrencyConflict, AuditId = auditId,
            };
        }

        // Step 3: SAP write + readback.
        int    rc;
        string sapError;
        decimal? actualAfter;
        string currency;
        try
        {
            (rc, sapError, actualAfter, currency) = _sap.UpdateItemPrice(itemCode, priceListNum, newPrice);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ProductAdmin] SAP UpdateItemPrice threw for {Item} PL{Pl}", itemCode, priceListNum);
            await _audit.SetTerminalAsync(auditId, PriceUpdateResult.SapUpdateFailed,
                null, null, ex.Message, null, null, DateTime.UtcNow, ct);
            return Fail(itemCode, priceListNum, newPrice, PriceUpdateResult.SapUpdateFailed, auditId);
        }

        if (rc != 0)
        {
            await _audit.SetTerminalAsync(auditId, PriceUpdateResult.SapUpdateFailed,
                null, rc, sapError, null, null, DateTime.UtcNow, ct);
            return Fail(itemCode, priceListNum, newPrice, PriceUpdateResult.SapUpdateFailed, auditId);
        }

        // Step 4: Readback mismatch detection.
        if (actualAfter is null || Math.Abs(actualAfter.Value - newPrice) > PriceTolerance)
        {
            _log.LogError(
                "[ProductAdmin] SAP_READBACK_MISMATCH {Item} PL{Pl}: requested={Req} actual={Act}",
                itemCode, priceListNum, newPrice, actualAfter);
            await _audit.SetTerminalAsync(auditId, PriceUpdateResult.SapReadbackMismatch,
                actualAfter, 0,
                $"Readback={actualAfter} != requested={newPrice}",
                null, null, DateTime.UtcNow, ct);
            return Fail(itemCode, priceListNum, newPrice, PriceUpdateResult.SapReadbackMismatch, auditId);
        }

        // Step 5: SQLite targeted update.
        string sqliteResult = "OK";
        try
        {
            bool updated = await _cache.UpdatePriceOnlyAsync(itemCode, priceListNum, actualAfter.Value, ct);
            if (!updated)
            {
                sqliteResult = "NOT_FOUND";
                _log.LogWarning("[ProductAdmin] SQLite: item {Item} not in cache — price drift until next sync", itemCode);
            }
        }
        catch (Exception ex)
        {
            sqliteResult = "FAILED";
            _log.LogWarning(ex, "[ProductAdmin] SQLite cache update failed for {Item} PL{Pl}", itemCode, priceListNum);
        }

        // Step 6: Neon targeted upsert.
        string neonResult = "OK";
        try
        {
            bool updated = await _neon.UpdatePriceOnlyAsync(itemCode, priceListNum, actualAfter.Value, ct);
            if (!updated)
            {
                neonResult = "NOT_FOUND";
                _log.LogWarning("[ProductAdmin] Neon: item {Item} not in Products table — price drift until next sync", itemCode);
            }
        }
        catch (Exception ex)
        {
            neonResult = "FAILED";
            _log.LogWarning(ex, "[ProductAdmin] Neon cache update failed for {Item} PL{Pl}", itemCode, priceListNum);
        }

        // Step 7: Determine final result.
        bool cacheFailure = sqliteResult != "OK" || neonResult != "OK";
        string finalResult = cacheFailure ? PriceUpdateResult.CacheSyncWarning : PriceUpdateResult.Success;

        if (cacheFailure)
            _log.LogWarning("[ProductAdmin] CACHE_SYNC_WARNING {Item} PL{Pl} SQLite={Sq} Neon={Ne}",
                itemCode, priceListNum, sqliteResult, neonResult);

        await _audit.SetTerminalAsync(auditId, finalResult,
            actualAfter, 0, null, sqliteResult, neonResult, DateTime.UtcNow, ct);

        return new UpdateProductPriceResponse
        {
            ItemCode       = itemCode,
            PriceListNum   = priceListNum,
            OldPrice       = current.Price,
            RequestedPrice = newPrice,
            ActualSapPrice = actualAfter.Value,
            Currency       = currency,
            SapUpdated     = true,
            SqliteUpdated  = sqliteResult == "OK",
            NeonUpdated    = neonResult   == "OK",
            AuditId        = auditId,
            ResultCode     = finalResult,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? Validate(string itemCode, int priceListNum, decimal price, string requestedBy)
    {
        if (string.IsNullOrWhiteSpace(itemCode))
            return "ItemCode is required.";
        if (!ValidPriceLists.Contains(priceListNum))
            return $"PriceListNum must be 1–5; received {priceListNum}.";
        if (price < 0)
            return $"Price must be >= 0 (zero is valid for marking a free item); received {price}.";
        if (price > 999_999_999.99m)
            return $"Price exceeds maximum allowed value (999,999,999.99).";
        if (string.IsNullOrWhiteSpace(requestedBy))
            return "RequestedBy is required.";
        return null;
    }

    private static UpdateProductPriceResponse Fail(
        string itemCode, int priceListNum, decimal requestedPrice, string code, long auditId) =>
        new()
        {
            ItemCode       = itemCode, PriceListNum = priceListNum,
            RequestedPrice = requestedPrice, ActualSapPrice = 0,
            SapUpdated     = false, ResultCode = code, AuditId = auditId,
        };
}
