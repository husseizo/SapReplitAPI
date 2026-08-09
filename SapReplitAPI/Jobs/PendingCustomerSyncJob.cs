using Quartz;
using SapReplitAPI.Services;
using System.Runtime.InteropServices;

namespace SapReplitAPI.Jobs;

[DisallowConcurrentExecution]
public class PendingCustomerSyncJob : IJob
{
    private readonly PendingCustomerService _pending;
    private readonly SapService _sap;
    private readonly ILogger<PendingCustomerSyncJob> _log;

    public PendingCustomerSyncJob(
        PendingCustomerService pending,
        SapService sap,
        ILogger<PendingCustomerSyncJob> log)
    {
        _pending = pending;
        _sap = sap;
        _log = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var eligible = await _pending.GetEligibleAsync(limit: 20);
        if (eligible.Count == 0) return;

        _log.LogInformation("👥 [PendingCustomerSyncJob] {Count} customer(s) to retry.", eligible.Count);

        foreach (var customer in eligible)
        {
            try
            {
                var dto = new SapReplitAPI.Models.CustomerModels.CreateCustomerDto
                {
                    CardName        = customer.CardName,
                    Phone           = customer.Phone,
                    CustomerType    = customer.CustomerType,
                    Region          = customer.Region,
                    SalesPersonName = customer.SalesPersonName,
                    SlpCode         = customer.SlpCode,
                    VIN1            = customer.VIN1,
                    VIN2            = customer.VIN2,
                    VIN3            = customer.VIN3
                };

                var cardCode = _sap.CreateCustomer(dto);
                await _pending.MarkSyncedAsync(customer.Id, cardCode);
                _log.LogInformation("✅ [PendingCustomerSyncJob] id={Id} '{Name}' → CardCode={CardCode}",
                    customer.Id, customer.CardName, cardCode);
            }
            catch (Exception ex) when (IsSapOffline(ex))
            {
                _log.LogWarning("📡 [PendingCustomerSyncJob] SAP offline — stopping retry loop.");
                break;
            }
            catch (Exception ex)
            {
                await _pending.RecordRejectionAsync(customer.Id, ex.Message);
                _log.LogWarning("⚠️ [PendingCustomerSyncJob] id={Id} rejected: {Error}", customer.Id, ex.Message);
            }
        }
    }

    private static bool IsSapOffline(Exception ex) =>
        ex is COMException ||
        ex.Message.Contains("SAP Connection failed", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Cannot connect", StringComparison.OrdinalIgnoreCase);
}
