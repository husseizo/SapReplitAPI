using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Jobs;
using SapReplitAPI.Models;
using SapReplitAPI.Services;
using SapReplitAPI.Services.Neon;
using SapReplitAPI.Services.Queue;

namespace SapReplitAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AccountsController : ControllerBase
{
    private static readonly string[] ValidAccounts = ["163000", "164000", "165000", "166000", "167000"];

    private static readonly Dictionary<string, string> AccountNames = new()
    {
        ["163000"] = "Cash on Hand",
        ["164000"] = "CRDB",
        ["165000"] = "M-Pesa Lipa",
        ["166000"] = "AAL NMB",
        ["167000"] = "Tigo Lipa",
    };

    // GET /api/accounts/statement?account=163000&from=2024-01-01&to=2024-12-31&page=1&pageSize=500
    [HttpGet("statement")]
    public async Task<IActionResult> GetStatement(
        [FromServices] NeonDbContext neon,
        [FromQuery] string? account,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 500)
    {
        page     = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 10, 2000);

        if (account != null && !ValidAccounts.Contains(account))
            return BadRequest(new
            {
                message = $"Invalid account '{account}'. Valid GL codes: {string.Join(", ", ValidAccounts.Select(a => $"{a} ({AccountNames[a]})"))}."
            });

        var fromDate = DateTime.SpecifyKind(from ?? new DateTime(2024, 1, 1), DateTimeKind.Utc);
        var toDate   = DateTime.SpecifyKind((to ?? DateTime.Today).AddDays(1).AddTicks(-1), DateTimeKind.Utc);

        var query = neon.AccountStatements
            .AsNoTracking()
            .Where(s => s.RefDate >= fromDate && s.RefDate <= toDate);

        if (account != null)
            query = query.Where(s => s.Account == account);

        var total = await query.CountAsync();
        var rows  = await query
            .OrderByDescending(s => s.RefDate)
            .ThenByDescending(s => s.TransId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return Ok(new
        {
            TotalCount    = total,
            Page          = page,
            PageSize      = pageSize,
            From          = fromDate.ToString("yyyy-MM-dd"),
            To            = toDate.ToString("yyyy-MM-dd"),
            AccountFilter = account,
            AccountNames,
            Results       = rows
        });
    }

    // GET /api/accounts/list — returns the 5 valid payment GL accounts
    [HttpGet("list")]
    public IActionResult GetAccountList() =>
        Ok(AccountNames.Select(kv => new { Code = kv.Key, Name = kv.Value }));

    // GET /api/accounts/sync/status — shows row counts in SQLite and Neon for debugging
    [HttpGet("sync/status")]
    public async Task<IActionResult> SyncStatus(
        [FromServices] CacheDbContext sqlite,
        [FromServices] IServiceProvider sp)
    {
        var sqliteCount = await sqlite.AccountStatements.CountAsync();
        var sqliteWatermark = await sqlite.SyncMetadata
            .Where(m => m.Type == "AccountStatement")
            .Select(m => (DateTime?)m.LastSyncedAt)
            .FirstOrDefaultAsync();
        var neonMirrorWatermark = await sqlite.SyncMetadata
            .Where(m => m.Type == "NeonMirror:AccountStatements")
            .Select(m => (DateTime?)m.LastSyncedAt)
            .FirstOrDefaultAsync();

        string neonRows;
        var neon = sp.GetService<NeonDbContext>();
        if (neon != null)
            neonRows = (await neon.AccountStatements.CountAsync()).ToString();
        else
            neonRows = "not configured";

        return Ok(new
        {
            SQLite = new
            {
                Rows     = sqliteCount,
                LastSync = sqliteWatermark?.ToString("yyyy-MM-dd HH:mm:ss") ?? "never"
            },
            Neon = new
            {
                Rows           = neonRows,
                MirrorWatermark = neonMirrorWatermark?.ToString("yyyy-MM-dd HH:mm:ss") ?? "never pushed"
            }
        });
    }

    // POST /api/accounts/sync/push-neon
    // Queues a SQLite → Neon push and returns immediately (avoids ngrok/proxy timeouts on 16k rows).
    // Poll GET /api/accounts/sync/status to see when Neon row count increases.
    [HttpPost("sync/push-neon")]
    public IActionResult PushToNeon(
        [FromServices] IBackgroundTaskQueue taskQueue,
        [FromServices] IServiceProvider sp)
    {
        var neonJob = sp.GetService<NeonSyncJob>();
        if (neonJob == null)
            return StatusCode(503, new { error = "NeonSyncJob not registered (NeonDb connection string missing)." });

        taskQueue.Enqueue(async (scopedSp, _) =>
        {
            var log = scopedSp.GetRequiredService<ILogger<AccountsController>>();
            var job = scopedSp.GetRequiredService<NeonSyncJob>();
            try
            {
                log.LogInformation("☁️ [PushToNeon] Starting SQLite → Neon push for AccountStatements...");
                await job.SyncAccountStatementsNowAsync();
                log.LogInformation("✅ [PushToNeon] AccountStatements push complete.");
            }
            catch (Exception ex)
            {
                log.LogError(ex, "❌ [PushToNeon] AccountStatements push failed: {Message}", ex.Message);
            }
        });

        return Accepted(new { message = "Push queued. Poll GET /api/accounts/sync/status — Neon rows will increase as batches commit." });
    }

    // POST /api/accounts/sync/full-resync
    // Resets the AccountStatement watermark so the next SAP pull re-fetches from 2024-01-01,
    // then runs the full pipeline: SAP → SQLite → Neon.
    // Use this when the SAP query was fixed and existing SQLite data needs to be replaced.
    [HttpPost("sync/full-resync")]
    public IActionResult FullResync(
        [FromServices] IBackgroundTaskQueue taskQueue,
        [FromServices] CacheDbContext sqlite)
    {
        taskQueue.Enqueue(async (sp, _) =>
        {
            var log = sp.GetRequiredService<ILogger<AccountsController>>();
            try
            {
                // 1. Reset watermark → AccountStatementSyncJob will re-fetch from DefaultFrom (2024-01-01)
                var db = sp.GetRequiredService<CacheDbContext>();
                var meta = await db.SyncMetadata.FirstOrDefaultAsync(m => m.Type == "AccountStatement");
                if (meta != null) db.SyncMetadata.Remove(meta);
                var neonMeta = await db.SyncMetadata.FirstOrDefaultAsync(m => m.Type == "NeonMirror:AccountStatements");
                if (neonMeta != null) db.SyncMetadata.Remove(neonMeta);
                await db.SaveChangesAsync();
                log.LogInformation("🔄 [FullResync] Watermarks reset. Re-fetching from SAP...");

                // 2. SAP → SQLite (full re-fetch from 2024-01-01)
                var sapJob = sp.GetRequiredService<AccountStatementSyncJob>();
                await sapJob.Execute(null!);
                log.LogInformation("🔄 [FullResync] SQLite updated. Pushing to Neon...");

                // 3. SQLite → Neon
                var neonJob = sp.GetService<NeonSyncJob>();
                if (neonJob != null)
                    await neonJob.SyncAccountStatementsNowAsync();

                log.LogInformation("✅ [FullResync] Complete. Poll /api/accounts/sync/status to verify.");
            }
            catch (Exception ex)
            {
                var log2 = sp.GetRequiredService<ILogger<AccountsController>>();
                log2.LogError(ex, "❌ [FullResync] Failed: {Message}", ex.Message);
            }
        });

        return Accepted(new { message = "Full resync queued (watermarks reset → SAP → SQLite → Neon). Poll GET /api/accounts/sync/status." });
    }

    // POST /api/accounts/sync/manual
    // Triggers a full account statement sync: SAP → SQLite, then SQLite → Neon (if Neon is configured).
    [HttpPost("sync/manual")]
    public IActionResult ManualSync([FromServices] IBackgroundTaskQueue taskQueue)
    {
        taskQueue.Enqueue(async (sp, _) =>
        {
            Console.WriteLine("📊 [ManualSync] Starting account statement sync...");

            // Step 1: SAP → SQLite
            var sapToSqlite = sp.GetRequiredService<AccountStatementSyncJob>();
            await sapToSqlite.Execute(null!);

            // Step 2: SQLite → Neon (only if NeonSyncJob is registered)
            var neonJob = sp.GetService<NeonSyncJob>();
            if (neonJob != null)
                await neonJob.SyncAccountStatementsNowAsync();

            Console.WriteLine("✅ [ManualSync] Account statement sync complete (SAP → SQLite → Neon).");
        });

        return Ok(new { message = "Account statement sync queued (SAP → SQLite → Neon)." });
    }
}
