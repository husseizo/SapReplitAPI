namespace SapReplitAPI.Services.Events;

public interface IInvoiceChangeSource
{
    List<int> GetChangedInvoiceDocEntries(DateTime since);
}
