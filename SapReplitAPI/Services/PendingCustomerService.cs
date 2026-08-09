using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models.CustomerModels;
using SapReplitAPI.Models.Pending;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Services;

public class PendingCustomerService
{
    private static readonly int MaxRetries = 8;
    private static readonly int[] BackoffSeconds = { 30, 60, 120, 300, 600, 1200, 1800, 3600 };

    private readonly NeonDbContext _neon;
    private readonly ILogger<PendingCustomerService> _log;

    public PendingCustomerService(NeonDbContext neon, ILogger<PendingCustomerService> log)
    {
        _neon = neon;
        _log = log;
    }

    public async Task<PendingCustomer> SavePendingAsync(CreateCustomerDto dto)
    {
        var pending = new PendingCustomer
        {
            CardName       = dto.CardName,
            Phone          = dto.Phone ?? "",
            CustomerType   = dto.CustomerType ?? "",
            Region         = !string.IsNullOrWhiteSpace(dto.Region) ? dto.Region : dto.City ?? "",
            SalesPersonName = dto.SalesPersonName ?? "",
            SlpCode        = dto.SlpCode,
            VIN1           = dto.VIN1,
            VIN2           = dto.VIN2,
            VIN3           = dto.VIN3,
            Status         = "Pending",
            CreatedAt      = DateTime.UtcNow
        };
        _neon.PendingCustomers.Add(pending);
        await _neon.SaveChangesAsync();
        _log.LogInformation("📥 [PendingCustomer] Saved id={Id} CardName={Name}", pending.Id, pending.CardName);
        return pending;
    }

    public async Task<List<PendingCustomer>> GetEligibleAsync(int limit = 20)
    {
        var now = DateTime.UtcNow;
        return await _neon.PendingCustomers
            .Where(c => c.Status == "Pending" &&
                        (c.NextRetryAt == null || c.NextRetryAt <= now))
            .OrderBy(c => c.CreatedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<List<PendingCustomer>> GetAllAsync(string? status = null)
    {
        var q = _neon.PendingCustomers.AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(c => c.Status == status);
        return await q.OrderByDescending(c => c.CreatedAt).ToListAsync();
    }

    public async Task MarkSyncedAsync(int id, string cardCode)
    {
        var c = await _neon.PendingCustomers.FindAsync(id);
        if (c == null) return;
        c.Status      = "Synced";
        c.SapCardCode = cardCode;
        c.SyncedAt    = DateTime.UtcNow;
        await _neon.SaveChangesAsync();
    }

    public async Task RecordRejectionAsync(int id, string error)
    {
        var c = await _neon.PendingCustomers.FindAsync(id);
        if (c == null) return;
        c.RetryCount++;
        c.ErrorMessage = error;
        if (c.RetryCount >= MaxRetries)
        {
            c.Status = "Failed";
        }
        else
        {
            var delay = BackoffSeconds[Math.Min(c.RetryCount - 1, BackoffSeconds.Length - 1)];
            c.NextRetryAt = DateTime.UtcNow.AddSeconds(delay);
        }
        await _neon.SaveChangesAsync();
    }

    public async Task ResetForRetryAsync(int id)
    {
        var c = await _neon.PendingCustomers.FindAsync(id)
            ?? throw new InvalidOperationException($"PendingCustomer {id} not found.");
        if (c.Status != "Failed")
            throw new InvalidOperationException($"Only Failed customers can be retried (current: {c.Status}).");
        c.Status       = "Pending";
        c.NextRetryAt  = null;
        c.ErrorMessage = null;
        await _neon.SaveChangesAsync();
    }
}
