using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models.Orde_Models;
using SapReplitAPI.Models.Pending;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Services;

public class PendingOrderService
{
    private readonly NeonDbContext _neon;
    private readonly ILogger<PendingOrderService> _log;

    private static readonly int MaxRetries = 8;
    private static readonly int[] BackoffSeconds = { 30, 60, 120, 300, 600, 1200, 1800, 3600 };

    public PendingOrderService(NeonDbContext neon, ILogger<PendingOrderService> log)
    {
        _neon = neon;
        _log = log;
    }

    public static string NewReplitId() =>
        "OR-" + Guid.NewGuid().ToString("N")[..12].ToUpper();

    /// <summary>Saves a pending order to Neon. Call when SAP is offline or rejects the request.</summary>
    public async Task<PendingOrder> SavePendingAsync(CreateOrderDto dto, string replitId)
    {
        var order = new PendingOrder
        {
            ReplitId     = replitId,
            CardCode     = dto.CardCode,
            DocDate      = dto.DocDate,
            DeliveryDate = dto.DeliveryDate,
            SlpCode      = dto.SlpCode,
            DocCurrency  = dto.DocCur ?? "TZS",
            Status       = "Pending",
            CreatedAt    = DateTime.UtcNow,
            Lines        = dto.Lines.Select((l, i) => new PendingOrderLine
            {
                LineNum      = i,
                ItemCode     = l.ItemCode,
                Quantity     = l.Quantity,
                Price        = l.Price,
                WhsCode      = l.WhsCode ?? "001",
                Dscription   = l.Dscription,
                U_Manufacturer = l.U_Manufacturer
            }).ToList()
        };

        _neon.PendingOrders.Add(order);
        await _neon.SaveChangesAsync();
        _log.LogInformation("📥 [PendingOrder] Saved offline order {ReplitId} for {CardCode}", replitId, dto.CardCode);
        return order;
    }

    /// <summary>Creates a Draft order (no lines yet). Used by the draft-first path.</summary>
    public async Task<PendingOrder> CreateDraftAsync(CreateOrderDto dto, string replitId)
    {
        var order = new PendingOrder
        {
            ReplitId     = replitId,
            CardCode     = dto.CardCode,
            DocDate      = dto.DocDate,
            DeliveryDate = dto.DeliveryDate,
            SlpCode      = dto.SlpCode,
            DocCurrency  = dto.DocCur ?? "TZS",
            Status       = "Draft",
            CreatedAt    = DateTime.UtcNow
        };

        _neon.PendingOrders.Add(order);
        await _neon.SaveChangesAsync();
        return order;
    }

    public async Task<PendingOrder?> GetDraftAsync(string replitId) =>
        await _neon.PendingOrders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.ReplitId == replitId && o.Status == "Draft");

    public async Task UpsertLineAsync(string replitId, PendingOrderLine line)
    {
        var order = await _neon.PendingOrders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.ReplitId == replitId && o.Status == "Draft")
            ?? throw new InvalidOperationException($"Draft {replitId} not found.");

        var existing = order.Lines.FirstOrDefault(l => l.LineNum == line.LineNum);
        if (existing != null)
        {
            existing.ItemCode      = line.ItemCode;
            existing.Quantity      = line.Quantity;
            existing.Price         = line.Price;
            existing.WhsCode       = line.WhsCode;
            existing.Dscription    = line.Dscription;
            existing.U_Manufacturer = line.U_Manufacturer;
        }
        else
        {
            line.PendingOrderId = order.Id;
            _neon.PendingOrderLines.Add(line);
        }

        await _neon.SaveChangesAsync();
    }

    public async Task RemoveLineAsync(string replitId, int lineNum)
    {
        var order = await _neon.PendingOrders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.ReplitId == replitId && o.Status == "Draft")
            ?? throw new InvalidOperationException($"Draft {replitId} not found.");

        var line = order.Lines.FirstOrDefault(l => l.LineNum == lineNum)
            ?? throw new InvalidOperationException($"Line {lineNum} not found.");

        _neon.PendingOrderLines.Remove(line);
        await _neon.SaveChangesAsync();
    }

    /// <summary>Transitions a Draft to Pending (ready for SAP submission).</summary>
    public async Task<PendingOrder> PromoteToPendingAsync(string replitId)
    {
        var order = await _neon.PendingOrders
            .Include(o => o.Lines)
            .FirstOrDefaultAsync(o => o.ReplitId == replitId && o.Status == "Draft")
            ?? throw new InvalidOperationException($"Draft {replitId} not found.");

        if (!order.Lines.Any())
            throw new InvalidOperationException("Cannot submit a draft with no lines.");

        order.Status = "Pending";
        await _neon.SaveChangesAsync();
        return order;
    }

    public async Task CancelDraftAsync(string replitId)
    {
        var order = await _neon.PendingOrders
            .FirstOrDefaultAsync(o => o.ReplitId == replitId && o.Status == "Draft")
            ?? throw new InvalidOperationException($"Draft {replitId} not found.");

        order.Status = "Cancelled";
        await _neon.SaveChangesAsync();
    }

    /// <summary>Returns up to <paramref name="limit"/> orders eligible for SAP submission.</summary>
    public async Task<List<PendingOrder>> GetEligibleAsync(int limit = 20)
    {
        var now = DateTime.UtcNow;
        return await _neon.PendingOrders
            .Include(o => o.Lines)
            .Where(o => o.Status == "Pending" &&
                        (o.NextRetryAt == null || o.NextRetryAt <= now))
            .OrderBy(o => o.CreatedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task MarkSyncedAsync(int id, int sapDocEntry)
    {
        var order = await _neon.PendingOrders.FindAsync(id)
            ?? throw new InvalidOperationException($"PendingOrder {id} not found.");
        order.Status     = "Synced";
        order.SapDocEntry = sapDocEntry;
        order.SyncedAt   = DateTime.UtcNow;
        order.ErrorMessage = null;
        await _neon.SaveChangesAsync();
        _log.LogInformation("✅ [PendingOrder] {ReplitId} synced → SAP DocEntry={DocEntry}", order.ReplitId, sapDocEntry);
    }

    /// <summary>SAP is offline — leave Pending, no penalty.</summary>
    public async Task RecordTransientFailureAsync(int id)
    {
        // No change to RetryCount or NextRetryAt — try again next cycle
        await Task.CompletedTask;
    }

    /// <summary>SAP rejected the order — apply exponential backoff; after MaxRetries mark Failed.</summary>
    public async Task RecordRejectionAsync(int id, string error)
    {
        var order = await _neon.PendingOrders.FindAsync(id)
            ?? throw new InvalidOperationException($"PendingOrder {id} not found.");

        order.RetryCount++;
        order.ErrorMessage = error;

        if (order.RetryCount >= MaxRetries)
        {
            order.Status = "Failed";
            _log.LogWarning("❌ [PendingOrder] {ReplitId} permanently failed after {Count} attempts.", order.ReplitId, order.RetryCount);
        }
        else
        {
            var delay = BackoffSeconds[Math.Min(order.RetryCount - 1, BackoffSeconds.Length - 1)];
            order.NextRetryAt = DateTime.UtcNow.AddSeconds(delay);
            _log.LogWarning("⏳ [PendingOrder] {ReplitId} retry {Count} in {Delay}s. Error: {Error}",
                order.ReplitId, order.RetryCount, delay, error);
        }

        await _neon.SaveChangesAsync();
    }

    public async Task ResetForRetryAsync(int id)
    {
        var order = await _neon.PendingOrders.FindAsync(id)
            ?? throw new InvalidOperationException($"PendingOrder {id} not found.");

        if (order.Status != "Failed")
            throw new InvalidOperationException("Only Failed orders can be manually retried.");

        order.Status      = "Pending";
        order.RetryCount  = 0;
        order.NextRetryAt = null;
        order.ErrorMessage = null;
        await _neon.SaveChangesAsync();
    }

    public async Task<List<PendingOrder>> GetAllAsync(string? status = null)
    {
        var q = _neon.PendingOrders.Include(o => o.Lines).AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(o => o.Status == status);
        return await q.OrderByDescending(o => o.CreatedAt).ToListAsync();
    }
}
