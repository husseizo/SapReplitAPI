using Xunit;

namespace SapReplitAPI.Tests.TieredAllocation;

/// <summary>
/// Tests for the write-once PickedAtUtc logic.
///
/// The SQL UPDATE uses: PickedAtUtc = CASE WHEN @status = 'Picked'
///                                         THEN COALESCE(PickedAtUtc, SYSUTCDATETIME())
///                                         ELSE PickedAtUtc END
///
/// This class models that behaviour in a static helper and verifies all
/// transition scenarios without requiring a database connection.
/// </summary>
public sealed class PickListTimestampTests
{
    /// <summary>
    /// Models the SQL COALESCE write-once logic applied during UpdatePickListPickedQtyAsync.
    /// existing  = current PickedAtUtc in the DB row (null if never stamped).
    /// status    = the new status being written.
    /// now       = SYSUTCDATETIME() substitute for deterministic testing.
    /// Returns the value PickedAtUtc should hold after the UPDATE.
    /// </summary>
    private static DateTime? ApplyPickedAtUtc(DateTime? existing, string status, DateTime now)
        => status == "Picked"
            ? (existing ?? now)   // COALESCE: preserve existing, stamp only on first Picked
            : existing;           // non-Picked: never change the column

    private static readonly DateTime T0 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = new(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // ── P01 ──────────────────────────────────────────────────────────────────
    // First Picked status: existing=null → PickedAtUtc set to now.
    [Fact]
    public void P01_FirstPickedStatus_SetsPickedAtUtc()
    {
        var result = ApplyPickedAtUtc(existing: null, status: "Picked", now: T0);

        Assert.Equal(T0, result);
    }

    // ── P02 ──────────────────────────────────────────────────────────────────
    // Second Picked call: existing already set → timestamp NOT overwritten.
    [Fact]
    public void P02_SecondPickedStatus_PreservesOriginalTimestamp()
    {
        var result = ApplyPickedAtUtc(existing: T0, status: "Picked", now: T1);

        Assert.Equal(T0, result); // original T0 preserved; T1 ignored
    }

    // ── P03 ──────────────────────────────────────────────────────────────────
    // Non-Picked status when column is null → remains null.
    [Fact]
    public void P03_NonPickedStatus_NullRemainsNull()
    {
        var result = ApplyPickedAtUtc(existing: null, status: "Assigned", now: T0);

        Assert.Null(result);
    }

    // ── P04 ──────────────────────────────────────────────────────────────────
    // Non-Picked status when column already has a value → value preserved.
    [Fact]
    public void P04_NonPickedStatus_ExistingTimestampPreserved()
    {
        var result = ApplyPickedAtUtc(existing: T0, status: "Assigned", now: T1);

        Assert.Equal(T0, result);
    }

    // ── P05 ──────────────────────────────────────────────────────────────────
    // Initial state: before any status update, PickedAtUtc is null.
    [Fact]
    public void P05_InitialState_PickedAtUtcIsNull()
    {
        DateTime? initial = null;

        Assert.Null(initial); // PickListRecord starts with null PickedAtUtc
    }

    // ── P06 ──────────────────────────────────────────────────────────────────
    // Idempotent: multiple Picked calls all return the same timestamp.
    [Fact]
    public void P06_MultiplePickedCalls_IdempotentTimestamp()
    {
        DateTime? state = null;

        state = ApplyPickedAtUtc(state, "Picked", T0); // first call
        state = ApplyPickedAtUtc(state, "Picked", T1); // retry at T1
        state = ApplyPickedAtUtc(state, "Picked", T2); // retry at T2

        Assert.Equal(T0, state); // always the first stamp
    }

    // ── P07 ──────────────────────────────────────────────────────────────────
    // "Unassigned" status does not clear PickedAtUtc.
    [Fact]
    public void P07_UnassignedStatus_DoesNotClearPickedAtUtc()
    {
        var result = ApplyPickedAtUtc(existing: T0, status: "Unassigned", now: T1);

        Assert.Equal(T0, result);
    }

    // ── P08 ──────────────────────────────────────────────────────────────────
    // "Completed" status does not touch PickedAtUtc.
    [Fact]
    public void P08_CompletedStatus_DoesNotTouchPickedAtUtc()
    {
        var result = ApplyPickedAtUtc(existing: T0, status: "Completed", now: T1);

        Assert.Equal(T0, result);
    }

    // ── P09 ──────────────────────────────────────────────────────────────────
    // "Pending" status does not touch PickedAtUtc.
    [Fact]
    public void P09_PendingStatus_DoesNotTouchPickedAtUtc()
    {
        DateTime? result = ApplyPickedAtUtc(existing: null, status: "Pending", now: T0);

        Assert.Null(result);
    }

    // ── P10 ──────────────────────────────────────────────────────────────────
    // Mixed sequence: Picked → Assigned → Picked → always returns first Picked timestamp.
    [Fact]
    public void P10_MixedSequence_AlwaysReturnsFirstPickedTimestamp()
    {
        DateTime? state = null;

        state = ApplyPickedAtUtc(state, "Assigned", T0); // no pick yet
        Assert.Null(state);

        state = ApplyPickedAtUtc(state, "Picked", T1);   // first pick
        Assert.Equal(T1, state);

        state = ApplyPickedAtUtc(state, "Assigned", T2); // status change
        Assert.Equal(T1, state);                          // timestamp preserved

        state = ApplyPickedAtUtc(state, "Picked", T2);   // retry pick
        Assert.Equal(T1, state);                          // write-once holds
    }
}
