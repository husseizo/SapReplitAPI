using SapReplitAPI.Services.CachedServices;
using SapReplitAPI.Services.Neon;

namespace SapReplitAPI.Services.Events;

/// <summary>
/// Handles ORRR events from outbox: ObjectType=234000031, TransactionType=A/U/C.
/// Idempotent snapshot write to SQLite + Neon mirrors.
/// </summary>
public sealed class ReturnRequestEventHandler : ISapEventHandler
{
    private const string OrrrObjectType = "234000031";

    private readonly SapService _sap;
    private readonly ReturnRequestCacheService _cache;
    private readonly NeonReturnRequestWriteService _neon;
    private readonly ILogger<ReturnRequestEventHandler> _log;

    public ReturnRequestEventHandler(
        SapService sap,
        ReturnRequestCacheService cache,
        NeonReturnRequestWriteService neon,
        ILogger<ReturnRequestEventHandler> log)
    {
        _sap = sap;
        _cache = cache;
        _neon = neon;
        _log = log;
    }

    public bool CanHandle(SapOutboxEvent ev)
        => ev.ObjectType == OrrrObjectType && (ev.TransactionType == "A" || ev.TransactionType == "U" || ev.TransactionType == "C");

    public async Task<(bool ok, string? error)> HandleAsync(SapOutboxEvent ev, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (ev.DocEntry is not { } docEntry)
            {
                _log.LogWarning("[ReturnRequestHandler] EventId={EventId} has null DocEntry.", ev.EventId);
                return (false, "DocEntry is null for ORRR event");
            }

            var snapshot = _sap.GetReturnRequestSnapshotByDocEntry(docEntry);
            if (!snapshot.HasValue)
            {
                _log.LogWarning("[ReturnRequestHandler] ORRR DocEntry={DocEntry} not found. EventId={EventId}", docEntry, ev.EventId);
                return (false, $"ReturnRequest DocEntry={docEntry} not found in SAP");
            }

            await _cache.RefreshSingleAsync(snapshot.Value.header, snapshot.Value.lines, ct);
            await _neon.UpsertReturnRequestAsync(snapshot.Value.header, snapshot.Value.lines, ct);

            sw.Stop();
            _log.LogInformation(
                "[ReturnRequestHandler] Done: DocEntry={DocEntry} DocNum={DocNum} Tx={Tx} Lines={Lines} {Elapsed:F1}ms EventId={EventId}",
                snapshot.Value.header.DocEntry,
                snapshot.Value.header.DocNum,
                ev.TransactionType,
                snapshot.Value.lines.Count,
                sw.Elapsed.TotalMilliseconds,
                ev.EventId);

            return (true, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _log.LogError(ex,
                "[ReturnRequestHandler] Failed for DocEntry={DocEntry} Tx={Tx} EventId={EventId} after {Elapsed:F1}ms",
                ev.DocEntry,
                ev.TransactionType,
                ev.EventId,
                sw.Elapsed.TotalMilliseconds);
            return (false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }
}
