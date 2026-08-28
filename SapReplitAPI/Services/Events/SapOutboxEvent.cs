namespace SapReplitAPI.Services.Events;

/// <summary>
/// Represents a single claimed row from SapEventOutbox.
/// Populated by OutboxClaimService.ClaimBatchAsync via the OUTPUT clause.
/// </summary>
public sealed record SapOutboxEvent(
    long      Id,
    Guid      EventId,
    string    ObjectType,
    string    TransactionType,
    int?      DocEntry,
    string?   KeyValues,
    DateTime  CreatedAtUtc,
    int       AttemptCount
);
