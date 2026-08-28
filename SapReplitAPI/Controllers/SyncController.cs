using Microsoft.AspNetCore.Mvc;
using Quartz;
using SapReplitAPI.Jobs;
using SapReplitAPI.Services;
using SapReplitAPI.Services.Queue;

namespace SapReplitAPI.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SyncController : ControllerBase
{
    // POST /api/sync/neon
    // Enqueues a full NeonSyncJob run immediately (same logic as the Quartz schedule).
    [HttpPost("neon")]
    public IActionResult TriggerNeonSync([FromServices] IBackgroundTaskQueue taskQueue)
    {
        taskQueue.Enqueue(async (sp, _) =>
        {
            var log = sp.GetRequiredService<ILogger<SyncController>>();

            var neonJob = sp.GetService<NeonSyncJob>();
            if (neonJob == null)
            {
                log.LogWarning("⚠️ [ManualNeonSync] NeonSyncJob is not registered (NeonDb connection string missing).");
                return;
            }

            log.LogInformation("☁️ [ManualNeonSync] Triggering full Neon sync...");
            await neonJob.Execute(null!);
            log.LogInformation("✅ [ManualNeonSync] Neon sync complete.");
        });

        return Ok(new { message = "Neon sync queued. Check logs for progress." });
    }
}
