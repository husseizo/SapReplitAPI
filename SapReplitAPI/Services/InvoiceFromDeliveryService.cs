using Microsoft.EntityFrameworkCore;
using SapReplitAPI.Models.Invoicing;

namespace SapReplitAPI.Services;

public class InvoiceFromDeliveryService
{
    private readonly SapService _sap;
    private readonly CacheDbContext _db;
    private readonly ILogger<InvoiceFromDeliveryService> _log;

    public InvoiceFromDeliveryService(
        SapService sap,
        CacheDbContext db,
        ILogger<InvoiceFromDeliveryService> log)
    {
        _sap = sap;
        _db = db;
        _log = log;
    }

    public async Task<ProcessInvoicesResult> ProcessOpenDeliveriesAsync(
        string triggerSource,
        DateTime? overrideDueDate = null)
    {
        var result = new ProcessInvoicesResult();

        List<OpenDeliveryDto> deliveries;
        try
        {
            deliveries = _sap.GetOpenDeliveries();
            _log.LogInformation("📋 [InvoiceJob] {Count} open deliveries found in SAP.", deliveries.Count);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "❌ [InvoiceJob] Cannot fetch open deliveries: {Error}", ex.Message);
            throw;
        }

        result.TotalFound = deliveries.Count;

        foreach (var delivery in deliveries)
        {
            // Idempotency — skip if already successfully invoiced this delivery
            bool alreadyDone = await _db.InvoiceFromDeliveryLogs
                .AsNoTracking()
                .AnyAsync(l => l.DeliveryDocEntry == delivery.DocEntry && l.Status == "Success");

            if (alreadyDone)
            {
                result.Skipped++;
                _log.LogDebug("[InvoiceJob] Delivery {DocEntry} already invoiced — skip.", delivery.DocEntry);
                continue;
            }

            // DocDueDate is mandatory
            DateTime? dueDate = overrideDueDate ?? delivery.DocDueDate;
            if (dueDate == null)
            {
                await WriteLogAsync(delivery, null, "Skipped",
                    "DocDueDate is null on delivery and no override provided.", triggerSource);
                result.Skipped++;
                _log.LogWarning("⚠️ [InvoiceJob] Delivery {DocEntry} has no DocDueDate — skipped.", delivery.DocEntry);
                continue;
            }

            try
            {
                var (invEntry, invNum) = _sap.CreateInvoiceFromDelivery(delivery, dueDate.Value);

                await WriteLogAsync(delivery, (invEntry, invNum), "Success", null, triggerSource);
                result.Succeeded++;
                result.Items.Add(new InvoiceResultItem
                {
                    DeliveryDocEntry = delivery.DocEntry,
                    DeliveryDocNum   = delivery.DocNum,
                    CardCode         = delivery.CardCode,
                    Status           = "Success",
                    InvoiceDocEntry  = invEntry,
                    InvoiceDocNum    = invNum
                });
                _log.LogInformation(
                    "✅ [InvoiceJob] Delivery {DlvEntry} → Invoice DocEntry={InvEntry} DocNum={InvNum} ({CardCode})",
                    delivery.DocEntry, invEntry, invNum, delivery.CardCode);
            }
            catch (Exception ex)
            {
                string status = IsLockError(ex.Message) ? "Locked" : "Failed";
                await WriteLogAsync(delivery, null, status, ex.Message, triggerSource);

                if (status == "Locked") result.Locked++;
                else result.Failed++;

                result.Items.Add(new InvoiceResultItem
                {
                    DeliveryDocEntry = delivery.DocEntry,
                    DeliveryDocNum   = delivery.DocNum,
                    CardCode         = delivery.CardCode,
                    Status           = status,
                    Error            = ex.Message
                });
                _log.LogWarning("⚠️ [InvoiceJob] Delivery {DocEntry} → {Status}: {Error}",
                    delivery.DocEntry, status, ex.Message);
            }
        }

        return result;
    }

    private static bool IsLockError(string msg) =>
        msg.Contains("locked", StringComparison.OrdinalIgnoreCase) ||
        msg.Contains("in use", StringComparison.OrdinalIgnoreCase);

    private async Task WriteLogAsync(
        OpenDeliveryDto delivery,
        (int DocEntry, int DocNum)? invoice,
        string status,
        string? error,
        string triggerSource)
    {
        _db.InvoiceFromDeliveryLogs.Add(new InvoiceFromDeliveryLog
        {
            DeliveryDocEntry = delivery.DocEntry,
            DeliveryDocNum   = delivery.DocNum,
            CardCode         = delivery.CardCode,
            InvoiceDocEntry  = invoice?.DocEntry,
            InvoiceDocNum    = invoice?.DocNum,
            Status           = status,
            ErrorMessage     = error,
            TriggerSource    = triggerSource,
            ProcessedAt      = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
        });
        try { await _db.SaveChangesAsync(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "⚠️ [InvoiceJob] Could not write audit log for delivery {DocEntry}",
                delivery.DocEntry);
        }
    }
}

public class ProcessInvoicesResult
{
    public int TotalFound { get; set; }
    public int Succeeded { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public int Locked { get; set; }
    public List<InvoiceResultItem> Items { get; set; } = new();
}

public class InvoiceResultItem
{
    public int DeliveryDocEntry { get; set; }
    public int DeliveryDocNum { get; set; }
    public string CardCode { get; set; } = "";
    public string Status { get; set; } = "";
    public int? InvoiceDocEntry { get; set; }
    public int? InvoiceDocNum { get; set; }
    public string? Error { get; set; }
}
