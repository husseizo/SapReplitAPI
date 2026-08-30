using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Services.Inventory;
using System.Diagnostics;

public class ProductCacheService
{
    private readonly CacheDbContext _db;
    private readonly SapService _sap;
    private readonly InventoryCacheWriteCoordinator _coord;
    private readonly ILogger<ProductCacheService> _log;

    public ProductCacheService(
        CacheDbContext db,
        SapService sap,
        InventoryCacheWriteCoordinator coord,
        ILogger<ProductCacheService> log)
    {
        _db    = db;
        _sap   = sap;
        _coord = coord;
        _log   = log;
    }

    #region =========================== UPSERT HELPERS ===========================

    private async Task<int> UpsertProductsAsync(List<CachedProduct> products)
    {
        if (products.Count == 0)
            return 0;

        var conn = _db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();

        using var tx = await _db.Database.BeginTransactionAsync();
        var sqliteConn = (SqliteConnection)conn;
        var sqliteTx = (SqliteTransaction)tx.GetDbTransaction();

        using var cmd = sqliteConn.CreateCommand();
        cmd.Transaction = sqliteTx;

        cmd.CommandText =
@"
INSERT INTO Products
(
    ItemCode, ItemName, U_Article_No, U_MdlTEST, U_Item_Name,
    Price, Price05, TotalOnHand, OnHand, OnHandQty, WhsCode, LastUpdated,
    Whs_001, Whs_002, Whs_003, Whs_004
)
VALUES
(
    $ItemCode, $ItemName, $U_Article_No, $U_MdlTEST, $U_Item_Name,
    $Price, $Price05, $TotalOnHand, $OnHand, $OnHandQty, $WhsCode, $LastUpdated,
    $Whs001, $Whs002, $Whs003, $Whs004
)
ON CONFLICT(ItemCode) DO UPDATE SET
    ItemName = excluded.ItemName,
    U_Article_No = excluded.U_Article_No,
    U_MdlTEST = excluded.U_MdlTEST,
    U_Item_Name = excluded.U_Item_Name,
    Price = excluded.Price,
    Price05 = excluded.Price05,
    TotalOnHand = excluded.TotalOnHand,
    OnHand = excluded.OnHand,
    OnHandQty = excluded.OnHandQty,
    LastUpdated = excluded.LastUpdated,
    Whs_001 = excluded.Whs_001,
    Whs_002 = excluded.Whs_002,
    Whs_003 = excluded.Whs_003,
    Whs_004 = excluded.Whs_004;
";

        var pItemCode = cmd.Parameters.Add("$ItemCode", SqliteType.Text);
        var pItemName = cmd.Parameters.Add("$ItemName", SqliteType.Text);
        var pArticle = cmd.Parameters.Add("$U_Article_No", SqliteType.Text);
        var pMdl = cmd.Parameters.Add("$U_MdlTEST", SqliteType.Text);
        var pItemName2 = cmd.Parameters.Add("$U_Item_Name", SqliteType.Text);
        var pPrice = cmd.Parameters.Add("$Price", SqliteType.Real);
        var pPrice05 = cmd.Parameters.Add("$Price05", SqliteType.Real);
        var pTotal = cmd.Parameters.Add("$TotalOnHand", SqliteType.Real);
        var pOnHand = cmd.Parameters.Add("$OnHand", SqliteType.Real);
        var pOnHandQty = cmd.Parameters.Add("$OnHandQty", SqliteType.Real);
        var pWhsCode = cmd.Parameters.Add("$WhsCode", SqliteType.Text);
        var pUpdated = cmd.Parameters.Add("$LastUpdated", SqliteType.Text);
        var pWhs001 = cmd.Parameters.Add("$Whs001", SqliteType.Integer);
        var pWhs002 = cmd.Parameters.Add("$Whs002", SqliteType.Integer);
        var pWhs003 = cmd.Parameters.Add("$Whs003", SqliteType.Integer);
        var pWhs004 = cmd.Parameters.Add("$Whs004", SqliteType.Integer);

        int count = 0;

        foreach (var p in products)
        {
            pItemCode.Value = p.ItemCode ?? "";
            pItemName.Value = p.ItemName ?? "";
            pArticle.Value = p.U_Article_No ?? "";
            pMdl.Value = p.U_MdlTEST ?? "";
            pItemName2.Value = p.U_Item_Name ?? "";
            pPrice.Value = p.Price;
            pPrice05.Value = p.Price05;
            pTotal.Value = p.TotalOnHand;
            pOnHand.Value = p.OnHand;
            pOnHandQty.Value = p.OnHandQty;
            pWhsCode.Value = p.WhsCode;
            pUpdated.Value = p.LastUpdated.ToString("yyyy-MM-dd HH:mm:ss");

            pWhs001.Value = p.Whs_001 ?? 0;
            pWhs002.Value = p.Whs_002 ?? 0;
            pWhs003.Value = p.Whs_003 ?? 0;
            pWhs004.Value = p.Whs_004 ?? 0;

            await cmd.ExecuteNonQueryAsync();
            count++;
        }

        await tx.CommitAsync();
        return count;
    }

    #endregion

    #region =========================== FULL SYNC ================================

    public async Task<object> FullSyncFromSAPAsync()
    {
        await _coord.WaitAsync();
        var sw = Stopwatch.StartNew();

        try
        {
            await _db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            await _db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");

            _log.LogInformation("🚀 FULL Product Sync started ...");

            var sapProducts = _sap.GetLiveProducts() ?? new List<ProductWithWarehouseDto>();
            if (sapProducts.Count == 0)
                throw new Exception("SAP returned 0 products.");

            var cleanList = new List<CachedProduct>(sapProducts.Count);

            foreach (var p in sapProducts)
            {
                var whs = p.Warehouses ?? new List<WarehouseStockDto>();

                int whs1 = (int)whs.Where(w => w.WarehouseCode == "001").Sum(w => w.OnHandQty);
                int whs2 = (int)whs.Where(w => w.WarehouseCode == "002").Sum(w => w.OnHandQty);
                int whs3 = (int)whs.Where(w => w.WarehouseCode == "003").Sum(w => w.OnHandQty);
                int whs4 = (int)whs.Where(w => w.WarehouseCode == "004").Sum(w => w.OnHandQty);

                decimal total = p.TotalOnHand > 0 ? p.TotalOnHand : (whs1 + whs2 + whs3 + whs4);
                if (total <= 0) continue;

                cleanList.Add(new CachedProduct
                {
                    ItemCode = p.ItemCode ?? "",
                    ItemName = p.ItemName ?? "",
                    U_Article_No = p.U_Article_No ?? "",
                    U_MdlTEST = p.U_MdlTEST ?? "",
                    U_Item_Name = p.U_Item_Name ?? "",
                    Price = p.Price,
                    Price05 = p.Price05,

                    TotalOnHand = total,
                    OnHand = total,
                    OnHandQty = total,

                    WhsCode = "ALL",
                    LastUpdated = DateTime.Now,

                    Whs_001 = whs1,
                    Whs_002 = whs2,
                    Whs_003 = whs3,
                    Whs_004 = whs4
                });
            }

            if (cleanList.Count == 0)
                throw new Exception("No products with stock > 0");

            int inserted = await UpsertProductsAsync(cleanList);

            // Remove stale / frozen items that are no longer returned by SAP
            var activeCodes = cleanList.Select(p => p.ItemCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stale = await _db.Products
                .Where(p => p.ItemCode != null && !activeCodes.Contains(p.ItemCode))
                .ToListAsync();
            if (stale.Count > 0)
            {
                _db.Products.RemoveRange(stale);
                await _db.SaveChangesAsync();
                _log.LogInformation("🗑️ Removed {Count} stale/frozen products from cache", stale.Count);
            }

            // Update sync metadata
            await UpdateSyncMetadata("Product");

            sw.Stop();
            _log.LogInformation("✅ FULL Sync done: {Inserted} items in {Sec}s",
                inserted, sw.Elapsed.TotalSeconds);

            return new
            {
                Message = "Full product sync complete",
                Inserted = inserted,
                Duration = sw.Elapsed.TotalSeconds
            };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ FULL Sync failed.");
            throw;
        }
        finally
        {
            _coord.Release();
        }
    }

    #endregion

    #region =========================== DELTA SYNC ===============================

    public async Task<object> SyncDeltaFromSAPAsync()
    {
        await _coord.WaitAsync();
        var sw = Stopwatch.StartNew();

        try
        {
            var meta = await _db.SyncMetadata.FirstOrDefaultAsync(x => x.Type == "Product");
            var from = meta?.LastSyncedAt ?? DateTime.UtcNow.AddDays(-7);
            var to = DateTime.UtcNow.AddMinutes(-1);

            _log.LogInformation("🔄 DELTA Product Sync {From} → {To}", from, to);

            var changed = _sap.GetLiveProducts(from, to) ?? new List<ProductWithWarehouseDto>();
            if (changed.Count == 0)
            {
                _log.LogInformation("No product changes found.");
                return new { Message = "No changes" };
            }

            var list = new List<CachedProduct>();
            var zeroCodes = new List<string>(); // items that dropped to zero stock

            foreach (var p in changed)
            {
                var whs = p.Warehouses ?? new List<WarehouseStockDto>();

                int whs1 = (int)whs.Where(w => w.WarehouseCode == "001").Sum(w => w.OnHandQty);
                int whs2 = (int)whs.Where(w => w.WarehouseCode == "002").Sum(w => w.OnHandQty);
                int whs3 = (int)whs.Where(w => w.WarehouseCode == "003").Sum(w => w.OnHandQty);
                int whs4 = (int)whs.Where(w => w.WarehouseCode == "004").Sum(w => w.OnHandQty);

                decimal total = p.TotalOnHand > 0 ? p.TotalOnHand : (whs1 + whs2 + whs3 + whs4);

                if (total <= 0)
                {
                    // Item dropped to zero — remove from cache so stale stock isn't served
                    if (!string.IsNullOrWhiteSpace(p.ItemCode))
                        zeroCodes.Add(p.ItemCode);
                    continue;
                }

                list.Add(new CachedProduct
                {
                    ItemCode = p.ItemCode ?? "",
                    ItemName = p.ItemName ?? "",
                    U_Article_No = p.U_Article_No ?? "",
                    U_MdlTEST = p.U_MdlTEST ?? "",
                    U_Item_Name = p.U_Item_Name ?? "",
                    Price = p.Price,
                    Price05 = p.Price05,

                    TotalOnHand = total,
                    OnHand = total,
                    OnHandQty = total,

                    WhsCode = "ALL",
                    LastUpdated = DateTime.Now,

                    Whs_001 = whs1,
                    Whs_002 = whs2,
                    Whs_003 = whs3,
                    Whs_004 = whs4
                });
            }

            // Remove zero-stock items from cache
            if (zeroCodes.Count > 0)
            {
                var toDelete = await _db.Products
                    .Where(p => p.ItemCode != null && zeroCodes.Contains(p.ItemCode))
                    .ToListAsync();
                if (toDelete.Count > 0)
                {
                    _db.Products.RemoveRange(toDelete);
                    await _db.SaveChangesAsync();
                    _log.LogInformation("🗑️ Removed {Count} zero-stock products from cache", toDelete.Count);
                }
            }

            int upserted = await UpsertProductsAsync(list);
            await UpdateSyncMetadata("Product");

            sw.Stop();
            _log.LogInformation("✅ DELTA Sync: {Upserted} updated in {Sec}s",
                upserted, sw.Elapsed.TotalSeconds);

            return new { Message = "Delta sync complete", Updated = upserted };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ Delta Sync failed.");
            throw;
        }
        finally
        {
            _coord.Release();
        }
    }

    #endregion

    private async Task UpdateSyncMetadata(string type)
    {
        var meta = await _db.SyncMetadata.FirstOrDefaultAsync(x => x.Type == type);
        if (meta == null)
            await _db.SyncMetadata.AddAsync(new SyncMetadata { Type = type, LastSyncedAt = DateTime.UtcNow });
        else
            meta.LastSyncedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }




    public async Task<List<CachedProduct>> GetCachedProductsAsync()
    {
        return await _db.Products
            .AsNoTracking()
            .OrderBy(p => p.ItemCode)
            .ToListAsync();
    }

    public async Task<(int TotalCount, List<CachedProduct> Products)> GetCachedProductsPageAsync(int page, int pageSize)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 500);

        var query = _db.Products
            .AsNoTracking()
            .OrderBy(p => p.ItemCode);

        var totalCount = await query.CountAsync();
        var products = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (totalCount, products);
    }

    public async Task<CachedProduct?> GetCachedProductByItemCodeAsync(string itemCode, bool onlyWithStock = true)
    {
        if (string.IsNullOrWhiteSpace(itemCode))
            return null;

        var q = _db.Products.AsNoTracking()
                            .Where(p => p.ItemCode == itemCode);

        if (onlyWithStock)
            q = q.Where(p => p.TotalOnHand > 0);

        return await q.FirstOrDefaultAsync();
    }

    /// <summary>
    /// Updates ONLY stock-related columns for existing Products rows.
    /// Called from InventoryEventRefreshService inside the coordinator window — does NOT acquire the coordinator.
    /// Never touches ItemName, U_Article_No, U_MdlTEST, U_Item_Name, Price, Price05.
    /// Items not already in Products are silently skipped (they will be inserted separately as new items).
    /// </summary>
    public async Task UpdateStockOnlyAsync(
        IReadOnlyList<(string ItemCode, decimal Total, decimal W001, decimal W002, decimal W003, decimal W004)> stockByItem,
        CancellationToken ct = default)
    {
        if (stockByItem.Count == 0) return;

        var conn = (SqliteConnection)_db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);

        using var tx  = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
UPDATE Products SET
    TotalOnHand = $Total,
    OnHand      = $Total,
    OnHandQty   = $Total,
    Whs_001     = $W001,
    Whs_002     = $W002,
    Whs_003     = $W003,
    Whs_004     = $W004,
    LastUpdated = $LastUpdated
WHERE ItemCode = $ItemCode";

        var pCode    = cmd.Parameters.Add("$ItemCode",    SqliteType.Text);
        var pTotal   = cmd.Parameters.Add("$Total",       SqliteType.Real);
        var pW001    = cmd.Parameters.Add("$W001",        SqliteType.Real);
        var pW002    = cmd.Parameters.Add("$W002",        SqliteType.Real);
        var pW003    = cmd.Parameters.Add("$W003",        SqliteType.Real);
        var pW004    = cmd.Parameters.Add("$W004",        SqliteType.Real);
        var pUpdated = cmd.Parameters.Add("$LastUpdated", SqliteType.Text);

        var ts = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

        foreach (var (itemCode, total, w001, w002, w003, w004) in stockByItem)
        {
            pCode.Value    = itemCode;
            pTotal.Value   = (double)total;
            pW001.Value    = (double)w001;
            pW002.Value    = (double)w002;
            pW003.Value    = (double)w003;
            pW004.Value    = (double)w004;
            pUpdated.Value = ts;

            await cmd.ExecuteNonQueryAsync(ct);
        }

        tx.Commit();
    }
}
