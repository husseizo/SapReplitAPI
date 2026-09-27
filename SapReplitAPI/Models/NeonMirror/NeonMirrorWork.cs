namespace SapReplitAPI.Models.NeonMirror;

/// <summary>
/// A durable unit of "make Neon reflect already-established authoritative local state"
/// work. Lives in SQLite (CacheDbContext) — the authoritative local cache for the
/// domains this table currently covers (Inventory: WarehouseInventory/BinInventory/
/// Products). Written in the SAME SQLite transaction as the cache mutation that
/// created the need for it, so local-commit-plus-mirror-intent is genuinely atomic
/// (no separate SQL Server round-trip, no distributed transaction).
///
/// The worker (NeonMirrorWorker) rereads CURRENT authoritative SQLite state for
/// EntityKey at processing time rather than replaying a stored payload — this is
/// what gives latest-wins safety: an older, retried row can never overwrite newer
/// state, because it never carries stale data to begin with, it just re-triggers a
/// fresh read. SourceVersion/SourceEventId are informational/traceability only.
/// </summary>
public class NeonMirrorWork
{
    public long Id { get; set; }

    /// <summary>Initial scope: "INVENTORY_SNAPSHOT" only (WarehouseInventory + BinInventory
    /// + Products for one ItemCode). Other EntityTypes are not yet produced by any writer —
    /// this column exists so future domains can be added without a schema change.</summary>
    public string EntityType { get; set; } = "";

    /// <summary>ItemCode for EntityType=INVENTORY_SNAPSHOT.</summary>
    public string EntityKey { get; set; } = "";

    /// <summary>UPSERT is the only operation the current writer produces.</summary>
    public string Operation { get; set; } = "UPSERT";

    /// <summary>Same derivation as SapEventOutbox's claim priority — informational/
    /// observability here; the worker itself is single-threaded so priority does not
    /// currently change processing order (see NeonMirrorWorker doc comment).</summary>
    public int Priority { get; set; }

    /// <summary>Traceability only — the SapEventOutbox.EventId that produced this
    /// mirror-work row, where known. Never used for correctness/gating.</summary>
    public Guid? SourceEventId { get; set; }

    /// <summary>Traceability only — ticks at enqueue time. Never used for correctness;
    /// the worker always rereads current state rather than trusting a stored version.</summary>
    public long? SourceVersion { get; set; }

    /// <summary>Pending | Processing | Retrying | Done | DeadLetter.</summary>
    public string Status { get; set; } = NeonMirrorWorkStatus.Pending;

    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? LastError { get; set; }
}

public static class NeonMirrorWorkStatus
{
    public const string Pending    = "Pending";
    public const string Processing = "Processing";
    public const string Retrying   = "Retrying";
    public const string Done       = "Done";
    public const string DeadLetter = "DeadLetter";
}

public static class NeonMirrorEntityType
{
    public const string InventorySnapshot = "INVENTORY_SNAPSHOT";
}
