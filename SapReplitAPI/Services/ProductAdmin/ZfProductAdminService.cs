using Microsoft.Extensions.Logging;
using SapReplitAPI.Models.ProductAdmin;
using SapReplitAPI.Services.Neon;
using SapReplitAPI.Services.Product;

namespace SapReplitAPI.Services.ProductAdmin;

/// <summary>
/// Orchestrates single and bulk product price updates:
/// validation → audit Pending → SAP read → concurrency check
/// → SAP write → SAP readback → SQLite update → Neon update → audit terminal.
///
/// Cache update steps are protected virtual to allow test subclasses to
/// substitute controlled results without hitting live SQLite or Neon.
/// </summary>
public class ZfProductAdminService
{
    private const decimal PriceTolerance = 0.005m;
    private static readonly IReadOnlySet<int> ValidPriceLists = new HashSet<int> { 1, 2, 3, 4, 5 };

    private readonly ISapPriceAdapter _sap;
    private readonly ProductCacheService _cache;
    private readonly NeonProductSyncService _neon;
    private readonly ProductPriceAuditRepository _audit;
    private readonly ProductPriceListSyncService _priceSync;
    private readonly ILogger<ZfProductAdminService> _log;

    public ZfProductAdminService(
        ISapPriceAdapter sap,
        ProductCacheService cache,
        NeonProductSyncService neon,
        ProductPriceAuditRepository audit,
        ProductPriceListSyncService priceSync,
        ILogger<ZfProductAdminService> log)
    {
        _sap       = sap;
        _cache     = cache;
        _neon      = neon;
        _audit     = audit;
        _priceSync = priceSync;
        _log       = log;
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
            // Idempotency: classify prior terminal results by SAP mutation status.
            var existing = await _audit.FindCompletedAsync(req.RequestId, item.ItemCode, item.PriceListNum, ct);
            if (existing is not null)
            {
                if (existing.Result is PriceUpdateResult.Success or PriceUpdateResult.CacheSyncWarning)
                {
                    // SAP mutation confirmed — do NOT replay Items.Update().
                    results.Add(new BulkPriceItemResult
                    {
                        ItemCode       = item.ItemCode,
                        PriceListNum   = item.PriceListNum,
                        ResultCode     = PriceUpdateResult.AlreadyCompleted,
                        OldPrice       = existing.OldPrice,
                        ActualSapPrice = existing.ActualPriceAfter,
                        Currency       = existing.Currency,
                        AuditId        = existing.Id,
                    });
                    continue;
                }

                if (existing.Result == PriceUpdateResult.SapWriteVerificationFailed)
                {
                    // SAP mutation status is ambiguous — do NOT auto-replay.
                    // Requires manual review. See audit record for details.
                    results.Add(new BulkPriceItemResult
                    {
                        ItemCode       = item.ItemCode,
                        PriceListNum   = item.PriceListNum,
                        ResultCode     = PriceUpdateResult.SapWriteVerificationFailed,
                        OldPrice       = existing.OldPrice,
                        ActualSapPrice = existing.ActualPriceAfter,
                        Currency       = existing.Currency,
                        AuditId        = existing.Id,
                        ErrorDetail    = $"Prior attempt resulted in SAP_WRITE_VERIFICATION_FAILED — audit {existing.Id}. Requires manual review before retry.",
                    });
                    continue;
                }

                // VALIDATION_FAILED, CONCURRENCY_CONFLICT, SAP_UPDATE_FAILED:
                // SAP was definitively NOT mutated — allow retry by falling through.
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
                // Per-row Reason overrides batch-level Reason when set.
                Reason               = string.IsNullOrWhiteSpace(item.Reason) ? req.Reason : item.Reason,
                RequestedAtUtc       = DateTime.UtcNow,
                Result               = "Pending",
            };
            long auditId = await _audit.InsertAsync(entry, ct);

            var single = await ExecuteSingleUpdateAsync(
                item.ItemCode, item.PriceListNum, item.Price,
                item.ExpectedCurrentPrice, auditId, entry, ct);

            results.Add(new BulkPriceItemResult
            {
                ItemCode       = item.ItemCode,
                PriceListNum   = item.PriceListNum,
                ResultCode     = single.ResultCode,
                OldPrice       = single.OldPrice == 0 ? null : single.OldPrice,
                ActualSapPrice = single.SapUpdated ? single.ActualSapPrice : null,
                Currency       = single.Currency,
                AuditId        = auditId,
                ErrorDetail    = single.ResultCode is PriceUpdateResult.Success or PriceUpdateResult.CacheSyncWarning
                    ? null
                    : $"See audit {auditId} for details.",
            });
        }

        int succeeded      = results.Count(r => r.ResultCode is PriceUpdateResult.Success or PriceUpdateResult.AlreadyCompleted or PriceUpdateResult.CacheSyncWarning);
        int alreadyDone    = results.Count(r => r.ResultCode == PriceUpdateResult.AlreadyCompleted);
        int cacheSyncWarn  = results.Count(r => r.ResultCode == PriceUpdateResult.CacheSyncWarning);
        int conflict       = results.Count(r => r.ResultCode == PriceUpdateResult.ConcurrencyConflict);
        int valFailed      = results.Count(r => r.ResultCode == PriceUpdateResult.ValidationFailed);
        int sapFailed      = results.Count(r => r.ResultCode is PriceUpdateResult.SapUpdateFailed or PriceUpdateResult.SapWriteVerificationFailed);

        return new BulkUpdateProductPricesResponse
        {
            RequestId       = req.RequestId,
            Total           = results.Count,
            Succeeded       = succeeded,
            Failed          = results.Count - succeeded,
            AlreadyCompleted = alreadyDone,
            CacheSyncWarning = cacheSyncWarn,
            Conflict         = conflict,
            ValidationFailed = valFailed,
            SapFailed        = sapFailed,
            Results          = results,
        };
    }

    // ── Preview ───────────────────────────────────────────────────────────────

    public async Task<BulkPreviewResponse> PreviewBulkPricesAsync(
        BulkPreviewRequest req,
        CancellationToken ct = default)
    {
        var items = new List<BulkPreviewItemResult>(req.Updates.Count);

        // First pass: count occurrences of each (ItemCode, PriceListNum) pair to detect duplicates
        var seenKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in req.Updates)
        {
            var key = $"{u.ItemCode?.Trim() ?? ""}:{u.PriceListNum}";
            seenKeys.TryGetValue(key, out int cnt);
            seenKeys[key] = cnt + 1;
        }

        foreach (var item in req.Updates)
        {
            var itemCode = item.ItemCode?.Trim() ?? "";
            var key = $"{itemCode}:{item.PriceListNum}";

            // Duplicate check
            if (seenKeys.TryGetValue(key, out int keyCount) && keyCount > 1)
            {
                items.Add(new BulkPreviewItemResult
                {
                    ItemCode         = itemCode,
                    PriceListNum     = item.PriceListNum,
                    ProposedPrice    = item.Price,
                    ExpectedCurrentPrice = item.ExpectedCurrentPrice,
                    ValidationStatus = PreviewValidationStatus.DuplicateItemPriceList,
                    ValidationMessage = $"ItemCode {itemCode} with PriceListNum {item.PriceListNum} appears more than once in this request.",
                    CanExecute       = false,
                });
                continue;
            }

            // Basic field validation
            if (string.IsNullOrWhiteSpace(itemCode))
            {
                items.Add(new BulkPreviewItemResult
                {
                    ItemCode         = itemCode,
                    PriceListNum     = item.PriceListNum,
                    ProposedPrice    = item.Price,
                    ValidationStatus = PreviewValidationStatus.InvalidPrice,
                    ValidationMessage = "ItemCode is required.",
                    CanExecute       = false,
                });
                continue;
            }

            if (!ValidPriceLists.Contains(item.PriceListNum))
            {
                items.Add(new BulkPreviewItemResult
                {
                    ItemCode         = itemCode,
                    PriceListNum     = item.PriceListNum,
                    ProposedPrice    = item.Price,
                    ValidationStatus = PreviewValidationStatus.InvalidPriceList,
                    ValidationMessage = $"PriceListNum must be 1–5; received {item.PriceListNum}.",
                    CanExecute       = false,
                });
                continue;
            }

            if (item.Price < 0)
            {
                items.Add(new BulkPreviewItemResult
                {
                    ItemCode         = itemCode,
                    PriceListNum     = item.PriceListNum,
                    ProposedPrice    = item.Price,
                    ValidationStatus = PreviewValidationStatus.InvalidPrice,
                    ValidationMessage = $"Price must be >= 0; received {item.Price}.",
                    CanExecute       = false,
                });
                continue;
            }

            if (item.Price > 999_999_999.99m)
            {
                items.Add(new BulkPreviewItemResult
                {
                    ItemCode         = itemCode,
                    PriceListNum     = item.PriceListNum,
                    ProposedPrice    = item.Price,
                    ValidationStatus = PreviewValidationStatus.InvalidPrice,
                    ValidationMessage = $"Price exceeds maximum allowed value (999,999,999.99).",
                    CanExecute       = false,
                });
                continue;
            }

            // SAP read — read-only, never calls UpdateItemPrice
            SapCurrentPrice? current;
            try
            {
                current = _sap.ReadItemPrice(itemCode, item.PriceListNum);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[ProductAdmin Preview] SAP read failed for {Item} PL{Pl}", itemCode, item.PriceListNum);
                items.Add(new BulkPreviewItemResult
                {
                    ItemCode         = itemCode,
                    PriceListNum     = item.PriceListNum,
                    ProposedPrice    = item.Price,
                    ExpectedCurrentPrice = item.ExpectedCurrentPrice,
                    ValidationStatus = PreviewValidationStatus.SapReadFailed,
                    ValidationMessage = $"SAP read threw: {ex.Message}",
                    CanExecute       = false,
                });
                continue;
            }

            if (current is null)
            {
                // Determine ITEM_NOT_FOUND vs PRICE_LIST_NOT_FOUND
                bool itemExists = false;
                try
                {
                    var probe = _sap.ReadItemPrice(itemCode, 3);
                    itemExists = probe is not null;
                }
                catch { itemExists = false; }

                var notFoundStatus = itemExists
                    ? PreviewValidationStatus.PriceListNotFound
                    : PreviewValidationStatus.ItemNotFound;

                items.Add(new BulkPreviewItemResult
                {
                    ItemCode         = itemCode,
                    PriceListNum     = item.PriceListNum,
                    ProposedPrice    = item.Price,
                    ExpectedCurrentPrice = item.ExpectedCurrentPrice,
                    ValidationStatus = notFoundStatus,
                    ValidationMessage = notFoundStatus == PreviewValidationStatus.ItemNotFound
                        ? $"Item '{itemCode}' not found in SAP."
                        : $"Item '{itemCode}' exists but price list {item.PriceListNum} is not configured.",
                    CanExecute       = false,
                });
                continue;
            }

            // Concurrency check
            if (item.ExpectedCurrentPrice.HasValue &&
                Math.Abs(current.Price - item.ExpectedCurrentPrice.Value) > PriceTolerance)
            {
                items.Add(new BulkPreviewItemResult
                {
                    ItemCode         = itemCode,
                    ItemName         = await TryGetItemNameAsync(itemCode, ct),
                    PriceListNum     = item.PriceListNum,
                    CurrentPrice     = current.Price,
                    ProposedPrice    = item.Price,
                    Difference       = item.Price - current.Price,
                    DifferencePercent = current.Price != 0 ? Math.Round((item.Price - current.Price) / current.Price * 100, 4) : null,
                    Currency         = current.Currency,
                    ExpectedCurrentPrice = item.ExpectedCurrentPrice,
                    ValidationStatus = PreviewValidationStatus.ConcurrencyConflict,
                    ValidationMessage = $"Expected {item.ExpectedCurrentPrice.Value} but SAP has {current.Price}.",
                    CanExecute       = false,
                });
                continue;
            }

            // NO_CHANGE check
            if (Math.Abs(current.Price - item.Price) <= PriceTolerance)
            {
                items.Add(new BulkPreviewItemResult
                {
                    ItemCode         = itemCode,
                    ItemName         = await TryGetItemNameAsync(itemCode, ct),
                    PriceListNum     = item.PriceListNum,
                    CurrentPrice     = current.Price,
                    ProposedPrice    = item.Price,
                    Difference       = 0m,
                    DifferencePercent = 0m,
                    Currency         = current.Currency,
                    ExpectedCurrentPrice = item.ExpectedCurrentPrice,
                    ValidationStatus = PreviewValidationStatus.NoChange,
                    ValidationMessage = "Proposed price equals the current SAP price (within tolerance).",
                    CanExecute       = false,
                });
                continue;
            }

            // READY
            var diff    = item.Price - current.Price;
            var diffPct = current.Price != 0 ? Math.Round(diff / current.Price * 100, 4) : (decimal?)null;

            items.Add(new BulkPreviewItemResult
            {
                ItemCode         = itemCode,
                ItemName         = await TryGetItemNameAsync(itemCode, ct),
                PriceListNum     = item.PriceListNum,
                CurrentPrice     = current.Price,
                ProposedPrice    = item.Price,
                Difference       = diff,
                DifferencePercent = diffPct,
                Currency         = current.Currency,
                ExpectedCurrentPrice = item.ExpectedCurrentPrice,
                ValidationStatus = PreviewValidationStatus.Ready,
                ValidationMessage = null,
                CanExecute       = true,
            });
        }

        // Build currency totals (READY + NO_CHANGE rows with known currency)
        var currencyTotals = items
            .Where(i => i.ValidationStatus is PreviewValidationStatus.Ready or PreviewValidationStatus.NoChange
                        && i.Currency is not null && i.CurrentPrice.HasValue)
            .GroupBy(i => i.Currency!)
            .Select(g => new BulkPreviewCurrencyTotal
            {
                Currency           = g.Key,
                TotalCurrentValue  = g.Sum(i => i.CurrentPrice!.Value),
                TotalProposedValue = g.Sum(i => i.ProposedPrice),
                NetDifference      = g.Sum(i => i.ProposedPrice - i.CurrentPrice!.Value),
                ItemCount          = g.Count(),
            })
            .OrderBy(t => t.Currency)
            .ToList();

        return new BulkPreviewResponse
        {
            RequestId     = req.RequestId,
            RequestedBy   = req.RequestedBy,
            TotalRows     = items.Count,
            ReadyRows     = items.Count(i => i.ValidationStatus == PreviewValidationStatus.Ready),
            NoChangeRows  = items.Count(i => i.ValidationStatus == PreviewValidationStatus.NoChange),
            InvalidRows   = items.Count(i => i.ValidationStatus is PreviewValidationStatus.InvalidPrice
                                or PreviewValidationStatus.InvalidPriceList
                                or PreviewValidationStatus.DuplicateItemPriceList
                                or PreviewValidationStatus.ItemNotFound
                                or PreviewValidationStatus.PriceListNotFound
                                or PreviewValidationStatus.SapReadFailed),
            ConflictRows  = items.Count(i => i.ValidationStatus == PreviewValidationStatus.ConcurrencyConflict),
            CurrencyTotals = currencyTotals,
            Items          = items,
        };
    }

    // ── Cache repair ──────────────────────────────────────────────────────────

    /// <summary>
    /// Read the live SAP price and push it into SQLite and Neon caches.
    /// Zero SAP mutations — never calls UpdateItemPrice.
    /// </summary>
    public async Task<CacheRepairResponse> RepairCacheAsync(
        string itemCode, int priceListNum,
        CacheRepairRequest req,
        CancellationToken ct = default)
    {
        SapCurrentPrice? current;
        try
        {
            current = _sap.ReadItemPrice(itemCode, priceListNum);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ProductAdmin CacheRepair] SAP read failed for {Item} PL{Pl}", itemCode, priceListNum);
            return new CacheRepairResponse
            {
                ItemCode     = itemCode,
                PriceListNum = priceListNum,
                Success      = false,
                Message      = $"SAP read failed: {ex.Message}",
            };
        }

        if (current is null)
        {
            return new CacheRepairResponse
            {
                ItemCode     = itemCode,
                PriceListNum = priceListNum,
                Success      = false,
                Message      = $"SAP returned no price for item '{itemCode}' on price list {priceListNum}.",
            };
        }

        // UpdateSqlitePriceAsync/UpdateNeonPriceAsync now update ItemPriceLists +
        // the Products projection atomically per side — no separate IPL step needed.
        string sqliteResult = await UpdateSqlitePriceAsync(itemCode, priceListNum, current.Price, current.Currency ?? "", ct);
        string neonResult   = await UpdateNeonPriceAsync(itemCode, priceListNum, current.Price, current.Currency ?? "", ct);

        bool success = sqliteResult == "OK" && neonResult == "OK";

        _log.LogInformation(
            "[ProductAdmin CacheRepair] {Item} PL{Pl} price={Price} SQLite={Sq} Neon={Ne} by={By}",
            itemCode, priceListNum, current.Price, sqliteResult, neonResult, req.RequestedBy);

        return new CacheRepairResponse
        {
            ItemCode     = itemCode,
            PriceListNum = priceListNum,
            SapPrice     = current.Price,
            Currency     = current.Currency,
            SqliteResult = sqliteResult,
            NeonResult   = neonResult,
            Success      = success,
            Message      = success ? null : $"Cache repair partially failed: SQLite={sqliteResult}, Neon={neonResult}",
        };
    }

    // ── Batch status ──────────────────────────────────────────────────────────

    public async Task<BulkRequestStatusResponse> GetBulkRequestStatusAsync(
        Guid requestId, CancellationToken ct = default)
    {
        var rows = await _audit.GetByRequestIdAsync(requestId, ct);
        return new BulkRequestStatusResponse
        {
            RequestId = requestId,
            TotalRows = rows.Count,
            Rows      = rows,
        };
    }

    // ── Helpers (private) ─────────────────────────────────────────────────────

    private async Task<string?> TryGetItemNameAsync(string itemCode, CancellationToken ct)
    {
        try
        {
            var product = await _cache.GetCachedProductByItemCodeAsync(itemCode, onlyWithStock: false);
            return product?.ItemName;
        }
        catch
        {
            return null;
        }
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
            try { _sap.ReadItemPrice(itemCode, 3); }
            catch { code = PriceUpdateResult.ItemNotFound; }

            await _audit.SetTerminalAsync(auditId, code, null, null, "SAP read returned null", null, null, DateTime.UtcNow, ct);
            return Fail(itemCode, priceListNum, newPrice, code, auditId);
        }

        entry.OldPrice = current.Price;
        entry.Currency = current.Currency;

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
                null, null, DateTime.UtcNow, ct,
                oldPrice: current.Price, currency: current.Currency);
            return new UpdateProductPriceResponse
            {
                ItemCode       = itemCode, PriceListNum = priceListNum,
                OldPrice       = current.Price, RequestedPrice = newPrice, ActualSapPrice = current.Price,
                Currency       = current.Currency, SapUpdated = false,
                ResultCode     = PriceUpdateResult.ConcurrencyConflict, AuditId = auditId,
            };
        }

        // Step 3: SAP write + readback.
        int     rc;
        string  sapError;
        decimal? actualAfter;
        string  currency;
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
                null, rc, sapError, null, null, DateTime.UtcNow, ct,
                oldPrice: current.Price, currency: current.Currency);
            return Fail(itemCode, priceListNum, newPrice, PriceUpdateResult.SapUpdateFailed, auditId);
        }

        // Step 4: Readback verification.
        // Items.Update() returned rc=0 but readback differs — SAP mutation status is ambiguous.
        // Do NOT treat as safe-to-retry: a replay must not call Items.Update() again.
        if (actualAfter is null || Math.Abs(actualAfter.Value - newPrice) > PriceTolerance)
        {
            _log.LogError(
                "[ProductAdmin] SAP_WRITE_VERIFICATION_FAILED {Item} PL{Pl}: requested={Req} readback={Act}",
                itemCode, priceListNum, newPrice, actualAfter);
            await _audit.SetTerminalAsync(auditId, PriceUpdateResult.SapWriteVerificationFailed,
                actualAfter, 0,
                $"Readback={actualAfter} != requested={newPrice} — SAP mutation status ambiguous",
                null, null, DateTime.UtcNow, ct,
                oldPrice: current.Price, currency: current.Currency);
            return Fail(itemCode, priceListNum, newPrice, PriceUpdateResult.SapWriteVerificationFailed, auditId);
        }

        // Step 5: SQLite targeted update — ItemPriceLists + Products projection, one transaction.
        string sqliteResult = await UpdateSqlitePriceAsync(itemCode, priceListNum, actualAfter.Value, currency, ct);

        // Step 6: Neon targeted update — ItemPriceLists + Products projection, one transaction.
        string neonResult = await UpdateNeonPriceAsync(itemCode, priceListNum, actualAfter.Value, currency, ct);

        // Step 7: Determine final result.
        bool   cacheFailure = sqliteResult != "OK" || neonResult != "OK";
        string finalResult  = cacheFailure ? PriceUpdateResult.CacheSyncWarning : PriceUpdateResult.Success;

        if (cacheFailure)
            _log.LogWarning("[ProductAdmin] CACHE_SYNC_WARNING {Item} PL{Pl} SQLite={Sq} Neon={Ne}",
                itemCode, priceListNum, sqliteResult, neonResult);

        await _audit.SetTerminalAsync(auditId, finalResult,
            actualAfter, 0, null, sqliteResult, neonResult, DateTime.UtcNow, ct,
            oldPrice: current.Price, currency: currency);

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

    // ── Cache update steps — overridable for unit tests ───────────────────────
    // Each updates the normalized ItemPriceLists row AND the Products compatibility
    // projection atomically (one transaction per side, via ProductPriceListSyncService),
    // so a successful cache write never leaves the two models disagreeing.

    protected virtual async Task<string> UpdateSqlitePriceAsync(
        string itemCode, int priceListNum, decimal price, string currency, CancellationToken ct)
    {
        try
        {
            bool updated = await _priceSync.UpdateSqliteNormalizedPriceAsync(itemCode, priceListNum, price, currency, ct);
            if (!updated)
            {
                _log.LogWarning("[ProductAdmin] SQLite: item {Item} not in cache — price drift until next sync", itemCode);
                return "NOT_FOUND";
            }
            return "OK";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ProductAdmin] SQLite cache update failed for {Item} PL{Pl}", itemCode, priceListNum);
            return "FAILED";
        }
    }

    protected virtual async Task<string> UpdateNeonPriceAsync(
        string itemCode, int priceListNum, decimal price, string currency, CancellationToken ct)
    {
        try
        {
            bool updated = await _priceSync.UpdateNeonNormalizedPriceAsync(itemCode, priceListNum, price, currency, ct);
            if (!updated)
            {
                _log.LogWarning("[ProductAdmin] Neon: item {Item} not in Products table — price drift until next sync", itemCode);
                return "NOT_FOUND";
            }
            return "OK";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[ProductAdmin] Neon cache update failed for {Item} PL{Pl}", itemCode, priceListNum);
            return "FAILED";
        }
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
