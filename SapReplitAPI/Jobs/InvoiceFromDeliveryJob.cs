using Quartz;
using SapReplitAPI.Services;

namespace SapReplitAPI.Jobs;

[DisallowConcurrentExecution]
public class InvoiceFromDeliveryJob : IJob
{
    private readonly InvoiceFromDeliveryService _service;
    private readonly ILogger<InvoiceFromDeliveryJob> _log;

    public InvoiceFromDeliveryJob(
        InvoiceFromDeliveryService service,
        ILogger<InvoiceFromDeliveryJob> log)
    {
        _service = service;
        _log = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _log.LogInformation("🏭 [InvoiceFromDeliveryJob] Starting hourly invoice run from open deliveries (06:00–20:00)...");
        try
        {
            var result = await _service.ProcessOpenDeliveriesAsync("Job");
            _log.LogInformation(
                "✅ [InvoiceFromDeliveryJob] Finished. Found={Found} Succeeded={Succeeded} Skipped={Skipped} Failed={Failed} Locked={Locked}",
                result.TotalFound, result.Succeeded, result.Skipped, result.Failed, result.Locked);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ [InvoiceFromDeliveryJob] Unhandled error: {Error}", ex.Message);
        }
    }
}
