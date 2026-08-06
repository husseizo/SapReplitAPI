using Quartz;
using SapReplitAPI.Models.Orde_Models;
using SapReplitAPI.Services;

namespace SapReplitAPI.Jobs;

/// <summary>
/// Retries pending (offline-captured) orders against SAP every 15 seconds.
/// Processes up to 20 eligible orders per cycle.
/// If SAP is offline → leave Pending, no penalty.
/// If SAP rejects → exponential backoff; after 8 rejections → Failed.
/// </summary>
[DisallowConcurrentExecution]
public class PendingOrderSyncJob : IJob
{
    private readonly PendingOrderService _pendingOrders;
    private readonly SapService _sap;
    private readonly ILogger<PendingOrderSyncJob> _log;

    public PendingOrderSyncJob(
        PendingOrderService pendingOrders,
        SapService sap,
        ILogger<PendingOrderSyncJob> log)
    {
        _pendingOrders = pendingOrders;
        _sap = sap;
        _log = log;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var eligible = await _pendingOrders.GetEligibleAsync(limit: 20);
        if (eligible.Count == 0) return;

        _log.LogInformation("🔄 [PendingOrderSync] Processing {Count} pending order(s).", eligible.Count);

        foreach (var order in eligible)
        {
            try
            {
                var dto = new CreateOrderDto
                {
                    CardCode     = order.CardCode,
                    DocDate      = order.DocDate,
                    DeliveryDate = order.DeliveryDate,
                    SlpCode      = order.SlpCode,
                    DocCur       = order.DocCurrency,
                    Lines        = order.Lines.Select(l => new OrderLineDto
                    {
                        ItemCode      = l.ItemCode,
                        Quantity      = l.Quantity,
                        Price         = l.Price,
                        WhsCode       = l.WhsCode,
                        Dscription    = l.Dscription ?? "",
                        U_Manufacturer = l.U_Manufacturer ?? ""
                    }).ToList()
                };

                var docEntry = _sap.CreateOrder(dto, order.ReplitId);
                await _pendingOrders.MarkSyncedAsync(order.Id, docEntry);
            }
            catch (Exception ex) when (IsSapOffline(ex))
            {
                // SAP is unreachable — leave Pending, no penalty, try next cycle
                _log.LogDebug("[PendingOrderSync] SAP offline — will retry {ReplitId} next cycle.", order.ReplitId);
                break; // No point retrying the rest this cycle
            }
            catch (Exception ex)
            {
                // SAP online but rejected the order — apply backoff
                await _pendingOrders.RecordRejectionAsync(order.Id, ex.Message);
            }
        }
    }

    private static bool IsSapOffline(Exception ex) =>
        ex.Message.Contains("SAP Connection failed", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("Cannot connect", StringComparison.OrdinalIgnoreCase) ||
        ex is System.Runtime.InteropServices.COMException;
}
