namespace SapReplitAPI.Services.Events;

public interface IInvoiceMirrorRefresher
{
    Task<RefreshResult> RefreshAsync(int docEntry, CancellationToken ct);
}
