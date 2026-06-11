using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.CachedProducts;
using SapReplitAPI.Models.CustomerModels;
using System.Diagnostics;
using System.Text.Json;

public class CustomerCacheService
{
    private readonly CacheDbContext _db;
    private readonly SapService _sap;
    private readonly ILogger<CustomerCacheService> _logger;
    private static readonly SemaphoreSlim _syncLock = new(1, 1);

    public CustomerCacheService(CacheDbContext db, SapService sap, ILogger<CustomerCacheService> logger)
    {
        _db = db;
        _sap = sap;
        _logger = logger;
    }

    // ============================================================
    // RAW SQLITE UPSERT — only REAL columns from CachedCustomer
    // ============================================================
    private async Task<int> UpsertCustomersAsync(List<CachedCustomer> customers)
    {
        if (customers.Count == 0)
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
INSERT INTO Customers
(
    CardCode, CardName, Balance, Region, Phone, CustomerType,
    SalesPersonName, SalesPersonCode, TotalSpent,
    VIN1, VIN2, VIN3, AddressesJson
)
VALUES
(
    $CardCode, $CardName, $Balance, $Region, $Phone, $CustomerType,
    $SalesPersonName, $SalesPersonCode, $TotalSpent,
    $VIN1, $VIN2, $VIN3, $AddressesJson
)
ON CONFLICT(CardCode) DO UPDATE SET
    CardName = excluded.CardName,
    Balance = excluded.Balance,
    Region = excluded.Region,
    Phone = excluded.Phone,
    CustomerType = excluded.CustomerType,
    SalesPersonName = excluded.SalesPersonName,
    SalesPersonCode = excluded.SalesPersonCode,
    TotalSpent = excluded.TotalSpent,
    VIN1 = excluded.VIN1,
    VIN2 = excluded.VIN2,
    VIN3 = excluded.VIN3,
    AddressesJson = excluded.AddressesJson;
";

        // Bind parameters
        var pCode = cmd.Parameters.Add("$CardCode", SqliteType.Text);
        var pName = cmd.Parameters.Add("$CardName", SqliteType.Text);
        var pBalance = cmd.Parameters.Add("$Balance", SqliteType.Real);
        var pRegion = cmd.Parameters.Add("$Region", SqliteType.Text);
        var pPhone = cmd.Parameters.Add("$Phone", SqliteType.Text);
        var pType = cmd.Parameters.Add("$CustomerType", SqliteType.Text);
        var pSPName = cmd.Parameters.Add("$SalesPersonName", SqliteType.Text);
        var pSPCode = cmd.Parameters.Add("$SalesPersonCode", SqliteType.Integer);
        var pSpent = cmd.Parameters.Add("$TotalSpent", SqliteType.Real);
        var pV1 = cmd.Parameters.Add("$VIN1", SqliteType.Text);
        var pV2 = cmd.Parameters.Add("$VIN2", SqliteType.Text);
        var pV3 = cmd.Parameters.Add("$VIN3", SqliteType.Text);
        var pAddr = cmd.Parameters.Add("$AddressesJson", SqliteType.Text);

        int count = 0;

        foreach (var c in customers)
        {
            pCode.Value = c.CardCode;
            pName.Value = c.CardName ?? "";
            pBalance.Value = c.Balance;
            pRegion.Value = c.Region ?? "";
            pPhone.Value = c.Phone ?? "";
            pType.Value = c.CustomerType ?? "";
            pSPName.Value = c.SalesPersonName ?? "";
            pSPCode.Value = c.SalesPersonCode;
            pSpent.Value = c.TotalSpent;
            pV1.Value = c.VIN1 ?? "";
            pV2.Value = c.VIN2 ?? "";
            pV3.Value = c.VIN3 ?? "";
            pAddr.Value = c.AddressesJson ?? "[]";

            await cmd.ExecuteNonQueryAsync();
            count++;
        }

        await tx.CommitAsync();
        return count;
    }

    public async Task<bool> TryUpsertCreatedCustomerAsync(string cardCode, CreateCustomerDto dto)
    {
        if (string.IsNullOrWhiteSpace(cardCode))
            throw new ArgumentException("cardCode is required.", nameof(cardCode));

        if (!await _syncLock.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            _logger.LogWarning("⚠️ [CustomerCache] Skipped immediate cache upsert for {CardCode} because another customer sync is in progress.", cardCode);
            return false;
        }

        try
        {
            var addresses = string.IsNullOrWhiteSpace(dto.Address)
                ? new List<CustomerAddressDto>()
                : new List<CustomerAddressDto>
                {
                    new CustomerAddressDto
                    {
                        Address = dto.Address.Trim(),
                        City = string.Empty,
                        AddressType = "B"
                    }
                };

            var cachedCustomer = new CachedCustomer
            {
                CardCode = cardCode.Trim(),
                CardName = dto.CardName?.Trim() ?? string.Empty,
                Balance = 0m,
                Region = dto.Region?.Trim().ToUpperInvariant() ?? string.Empty,
                Phone = dto.Phone?.Trim() ?? string.Empty,
                CustomerType = dto.CustomerType?.Trim() ?? string.Empty,
                SalesPersonName = dto.SalesPersonName?.Trim() ?? string.Empty,
                SalesPersonCode = dto.SlpCode > 0 ? dto.SlpCode : null,
                TotalSpent = 0m,
                VIN1 = dto.VIN1?.Trim() ?? string.Empty,
                VIN2 = dto.VIN2?.Trim() ?? string.Empty,
                VIN3 = dto.VIN3?.Trim() ?? string.Empty,
                AddressesJson = JsonSerializer.Serialize(addresses)
            };

            await UpsertCustomersAsync(new List<CachedCustomer> { cachedCustomer });
            await UpdateSyncMetadataAsync("Customer");

            _logger.LogInformation("✅ [CustomerCache] Immediately upserted newly created customer {CardCode} into SQLite cache.", cardCode);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ [CustomerCache] Immediate cache upsert failed for newly created customer {CardCode}.", cardCode);
            return false;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    // ====================================================================
    // FULL SYNC — Pull ALL SAP customers, UPSERT, remove missing ones
    // ====================================================================
    public async Task<object> FullSyncFromSAPAsync()
    {
        await _syncLock.WaitAsync();
        var sw = Stopwatch.StartNew();

        try
        {
            await _db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            await _db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout = 5000;");

            _logger.LogInformation("🚀 [CustomerCache] FULL sync started.");

            // Pull from SAP paged
            var all = new List<CustomerDto>();
            int page = 1;
            const int pageSize = 5000;

            while (true)
            {
                var batch = _sap.GetCustomersPaged(page, pageSize)?.Customers ?? new List<CustomerDto>();
                if (batch.Count == 0) break;

                all.AddRange(batch);
                if (batch.Count < pageSize) break;
                page++;
            }

            if (all.Count == 0)
            {
                _logger.LogWarning("⚠️ [CustomerCache] SAP returned 0 customers for full sync.");
                return new { Message = "No customers received from SAP." };
            }

            // Build dictionary for deletes
            var incomingCodes = all.Select(c => c.CardCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existing = await _db.Customers.AsNoTracking().ToListAsync();
            var existingCodes = existing.Select(c => c.CardCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var toRemove = existing.Where(x => !incomingCodes.Contains(x.CardCode)).ToList();

            // Convert → CachedCustomer
            var toUpsert = all
                .Where(c => !string.IsNullOrWhiteSpace(c.CardCode))
                .Select(c => new CachedCustomer
                {
                    CardCode = c.CardCode!,
                    CardName = c.CardName ?? "",
                    Balance = c.Balance,
                    Region = c.Region ?? "",
                    Phone = c.Phone ?? "",
                    CustomerType = c.CustomerType ?? "",
                    SalesPersonName = c.SalesPersonName ?? "",
                    SalesPersonCode = c.SalesPersonCode,
                    TotalSpent = c.TotalSpent,
                    VIN1 = c.VIN1 ?? "",
                    VIN2 = c.VIN2 ?? "",
                    VIN3 = c.VIN3 ?? "",
                    AddressesJson = JsonSerializer.Serialize(c.Addresses ?? new List<CustomerAddressDto>())
                })
                .ToList();

            var insertedCount = toUpsert.Count(c => !existingCodes.Contains(c.CardCode));
            var updatedCount = toUpsert.Count - insertedCount;

            // Remove customers missing in SAP
            using (var tx = await _db.Database.BeginTransactionAsync())
            {
                if (toRemove.Count > 0)
                    _db.Customers.RemoveRange(toRemove);

                await _db.SaveChangesAsync();
                await tx.CommitAsync();
            }

            int upserted = await UpsertCustomersAsync(toUpsert);
            await UpdateSyncMetadataAsync("Customer");

            sw.Stop();
            _logger.LogInformation(
                "✅ [CustomerCache] FULL sync completed. Inserted={Inserted}, Updated={Updated}, Deleted={Deleted}, Upserted={Upserted}, Duration={DurationSeconds:F2}s",
                insertedCount,
                updatedCount,
                toRemove.Count,
                upserted,
                sw.Elapsed.TotalSeconds);
            return new
            {
                Message = "Full Customer Sync Complete",
                InsertedOrUpdated = upserted,
                Deleted = toRemove.Count,
                Duration = sw.Elapsed.TotalSeconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "❌ [CustomerCache] FULL sync failed after {DurationSeconds:F2}s", sw.Elapsed.TotalSeconds);
            throw;
        }
        finally
        {
            _syncLock.Release();
        }
    }



    public async Task<List<CachedCustomer>> GetCachedCustomersAsync()
    {
        return await _db.Customers
            .AsNoTracking()
            .OrderBy(c => c.CardName)
            .ToListAsync();
    }


    public async Task<List<CustomerPhoneResult>> GetCardNamesByPhoneAsync(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return new List<CustomerPhoneResult>();

        static string Digits(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", "[^0-9]", "");

        var needle = Digits(phone);
        var tail9 = needle.Length > 9 ? needle[^9..] : needle;

        var rough = await _db.Customers
            .AsNoTracking()
            .Where(c =>
                c.Phone.Contains(phone) ||
                EF.Functions.Like(c.Phone, $"%{tail9}") ||
                EF.Functions.Like(c.Phone, $"%{needle}%"))
            .Select(c => new { c.CardName, c.Phone })
            .ToListAsync();

        return rough
            .Where(x =>
            {
                var d = Digits(x.Phone);
                return d.EndsWith(tail9) || d.Contains(needle);
            })
            .Distinct()
            .Select(x => new CustomerPhoneResult
            {
                CardName = x.CardName ?? "",
                Phone = x.Phone ?? ""
            })
            .ToList();
    }


    // ====================================================================
    // DELTA SYNC — Uses paged SAP pulls + compares with local cache
    // ====================================================================
    public async Task<object> SyncDeltaFromSAPAsync()
    {
        await _syncLock.WaitAsync();
        var sw = Stopwatch.StartNew();

        try
        {
            var existing = await _db.Customers.AsNoTracking().ToDictionaryAsync(x => x.CardCode);
            var delta = new List<CustomerDto>();

            int page = 1;
            const int pageSize = 5000;

            while (true)
            {
                var batch = _sap.GetCustomersPaged(page, pageSize)?.Customers ?? new List<CustomerDto>();
                if (batch.Count == 0) break;

                foreach (var c in batch)
                {
                    if (c.CardCode == null) continue;

                    // Detect new or changed records
                    if (!existing.TryGetValue(c.CardCode, out var old))
                        delta.Add(c);
                    else
                    {
                        // compare values
                        if (
                            old.CardName != c.CardName ||
                            old.Phone != c.Phone ||
                            old.Region != c.Region ||
                            old.Balance != c.Balance ||
                            old.TotalSpent != c.TotalSpent
                        )
                        {
                            delta.Add(c);
                        }
                    }
                }

                if (batch.Count < pageSize) break;
                page++;
            }

            if (delta.Count == 0)
            {
                _logger.LogInformation("ℹ️ [CustomerCache] DELTA sync found no customer changes.");
                return new { Message = "No customer changes detected." };
            }

            var upserts = delta.Select(c => new CachedCustomer
            {
                CardCode = c.CardCode!,
                CardName = c.CardName ?? "",
                Balance = c.Balance,
                Region = c.Region ?? "",
                Phone = c.Phone ?? "",
                CustomerType = c.CustomerType ?? "",
                SalesPersonName = c.SalesPersonName ?? "",
                SalesPersonCode = c.SalesPersonCode,
                TotalSpent = c.TotalSpent,
                VIN1 = c.VIN1 ?? "",
                VIN2 = c.VIN2 ?? "",
                VIN3 = c.VIN3 ?? "",
                AddressesJson = JsonSerializer.Serialize(c.Addresses ?? new List<CustomerAddressDto>())
            }).ToList();

            var insertedCount = upserts.Count(c => !existing.ContainsKey(c.CardCode));
            var updatedCount = upserts.Count - insertedCount;

            int upserted = await UpsertCustomersAsync(upserts);
            await UpdateSyncMetadataAsync("Customer");

            sw.Stop();
            _logger.LogInformation(
                "✅ [CustomerCache] DELTA sync completed. Inserted={Inserted}, Updated={Updated}, Upserted={Upserted}, Duration={DurationSeconds:F2}s",
                insertedCount,
                updatedCount,
                upserted,
                sw.Elapsed.TotalSeconds);
            return new
            {
                Message = "Delta Customer Sync Complete",
                Updated = upserted,
                Duration = sw.Elapsed.TotalSeconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "❌ [CustomerCache] DELTA sync failed after {DurationSeconds:F2}s", sw.Elapsed.TotalSeconds);
            throw;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task UpdateSyncMetadataAsync(string type)
    {
        var meta = await _db.SyncMetadata.FirstOrDefaultAsync(x => x.Type == type);
        if (meta == null)
            await _db.SyncMetadata.AddAsync(new SyncMetadata { Type = type, LastSyncedAt = DateTime.UtcNow });
        else
            meta.LastSyncedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }
}
