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

        _log.LogInformation("[PendingCustomerSyncJob] {Count} customer(s) to retry.", eligible.Count);

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
                _log.LogInformation("[PendingCustomerSyncJob] id={Id} '{Name}' -> CardCode={CardCode}",
                    customer.Id, customer.CardName, cardCode);
            }
            catch (Exception ex) when (IsSapOffline(ex))
            {
                _log.LogWarning("[PendingCustomerSyncJob] SAP offline -- {ExType} HRESULT={HResult:X8}: {ExMsg}",
                    ex.GetType().Name,
                    (uint)Marshal.GetHRForException(ex),
                    ex.Message);
                break;
            }
            catch (Exception ex)
            {
                await _pending.RecordRejectionAsync(customer.Id, ex.Message);
                _log.LogWarning("[PendingCustomerSyncJob] id={Id} rejected: {Error}", customer.Id, ex.Message);
            }
        }
    }

    // Only treat as SAP offline for genuine connection failures:
    //   - Exception thrown by GetConnectedCompany() (message contains "SAP Connection failed")
    //   - COMException with RPC/infrastructure HRESULT (connection broken mid-call)
    //   SAP field-validation COMExceptions (HRESULT=0xFFFFFC14 = -1004) are NOT connection failures
    //   and must reach RecordRejectionAsync so the customer is marked rejected, not silently looped.
    private static bool IsSapOffline(Exception ex)
    {
        if (ex.Message.Contains("SAP Connection failed", StringComparison.OrdinalIgnoreCase)) return true;
        if (ex.Message.Contains("Cannot connect", StringComparison.OrdinalIgnoreCase)) return true;
        if (ex is COMException ce)
        {
            // RPC infrastructure errors = genuine connection failure
            return ce.ErrorCode is
                unchecked((int)0x80010108) or  // RPC_E_DISCONNECTED
                unchecked((int)0x800706BA) or  // RPC_S_SERVER_UNAVAILABLE
                unchecked((int)0x80010001);    // RPC_E_CALL_REJECTED
        }
        return false;
    }
}
