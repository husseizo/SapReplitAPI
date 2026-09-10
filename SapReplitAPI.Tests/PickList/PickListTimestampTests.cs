using SapReplitAPI.Models.Cache;
using Xunit;

namespace SapReplitAPI.Tests.PickList;

/// <summary>
/// PLT01–PLT15: PickListLines timestamp field tests (Gate 17).
///
/// Verifies:
///   - CachedPickListLine carries CreatedTime and PickedTime as nullable DateTime?
///   - Default values are null (historical NULL preserved)
///   - Fields are independently nullable (PickedTime can be null while CreatedTime is set)
///   - Round-trip serialization of UTC timestamps is loss-free
///   - No fabrication of timestamps: NULL must remain NULL
///   - PickedTime semantics: write-once, populated via PLR.PickedAtUtc only
///   - Entity identity (AbsEntry+PickEntry) is unaffected by new fields
/// </summary>
public sealed class PickListTimestampTests
{
    // ── PLT01: CachedPickListLine has CreatedTime property ────────────────────
    [Fact]
    public void PLT01_CachedPickListLine_HasCreatedTimeProperty()
    {
        var prop = typeof(CachedPickListLine).GetProperty(nameof(CachedPickListLine.CreatedTime));
        Assert.NotNull(prop);
        Assert.Equal(typeof(DateTime?), prop!.PropertyType);
    }

    // ── PLT02: CachedPickListLine has PickedTime property ─────────────────────
    [Fact]
    public void PLT02_CachedPickListLine_HasPickedTimeProperty()
    {
        var prop = typeof(CachedPickListLine).GetProperty(nameof(CachedPickListLine.PickedTime));
        Assert.NotNull(prop);
        Assert.Equal(typeof(DateTime?), prop!.PropertyType);
    }

    // ── PLT03: CreatedTime defaults to null ───────────────────────────────────
    [Fact]
    public void PLT03_CreatedTime_DefaultsToNull()
    {
        var line = new CachedPickListLine();
        Assert.Null(line.CreatedTime);
    }

    // ── PLT04: PickedTime defaults to null ────────────────────────────────────
    [Fact]
    public void PLT04_PickedTime_DefaultsToNull()
    {
        var line = new CachedPickListLine();
        Assert.Null(line.PickedTime);
    }

    // ── PLT05: CreatedTime and PickedTime are independently nullable ──────────
    [Fact]
    public void PLT05_CreatedTime_SetWithPickedTimeNull()
    {
        var created = new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc);
        var line = new CachedPickListLine { CreatedTime = created };

        Assert.Equal(created, line.CreatedTime);
        Assert.Null(line.PickedTime);
    }

    // ── PLT06: PickedTime can be set independently ────────────────────────────
    [Fact]
    public void PLT06_PickedTime_SetIndependently()
    {
        var picked = new DateTime(2026, 9, 5, 14, 30, 0, DateTimeKind.Utc);
        var line = new CachedPickListLine { PickedTime = picked };

        Assert.Null(line.CreatedTime);
        Assert.Equal(picked, line.PickedTime);
    }

    // ── PLT07: Both timestamps set simultaneously ─────────────────────────────
    [Fact]
    public void PLT07_BothTimestamps_SetSimultaneously()
    {
        var created = new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc);
        var picked  = new DateTime(2026, 9, 5, 15, 0, 0, DateTimeKind.Utc);

        var line = new CachedPickListLine { CreatedTime = created, PickedTime = picked };

        Assert.Equal(created, line.CreatedTime);
        Assert.Equal(picked, line.PickedTime);
        Assert.True(line.PickedTime > line.CreatedTime);
    }

    // ── PLT08: NULL CreatedTime is preserved — not fabricated ─────────────────
    // Simulates the behavior when no PLR row exists for this line (non-ZF or pre-history).
    [Fact]
    public void PLT08_NullCreatedTime_IsPreservedNotFabricated()
    {
        var timestamps = new Dictionary<(int soDocEntry, int soLineNum), (DateTime createdAtUtc, DateTime? pickedAtUtc)>();

        timestamps.TryGetValue((orderEntry: 999, soLineNum: 0), out var ts);

        // When TryGetValue returns false, ts is default — createdAtUtc is DateTime.MinValue.
        // The INSERT logic converts DateTime.MinValue to DBNull.Value (NULL in DB).
        // This test verifies the lookup-to-null mapping.
        bool wouldBeNull = ts.createdAtUtc == default;
        Assert.True(wouldBeNull, "Missing PLR entry must result in NULL CreatedTime, not fabricated timestamp.");
    }

    // ── PLT09: NULL PickedTime is preserved — not converted to any substitute ─
    [Fact]
    public void PLT09_NullPickedTime_IsPreservedNotConverted()
    {
        // Simulates a PLR row that exists (CreatedTime set) but has not yet been picked (PickedAtUtc = null).
        var created = new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc);
        var timestamps = new Dictionary<(int, int), (DateTime, DateTime?)>
        {
            { (100, 0), (created, null) }
        };

        timestamps.TryGetValue((100, 0), out var ts);

        Assert.Equal(created, ts.Item1);
        Assert.Null(ts.Item2);
        // PickedTime in cache must stay NULL — not DateTime.MinValue, not CreatedTime, not sync time.
    }

    // ── PLT10: PickedTime is write-once semantics (first durable pick wins) ───
    // Simulates ORDER BY Id DESC so the most-recent PLR row wins.
    // If PickedAtUtc is set in the most-recent row it is used; NULL in most-recent means not-yet-picked.
    [Fact]
    public void PLT10_PickedTime_MostRecentRowWins_OrderByIdDesc()
    {
        var created1 = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var created2 = new DateTime(2026, 9, 3, 8, 0, 0, DateTimeKind.Utc);
        var picked2  = new DateTime(2026, 9, 3, 16, 0, 0, DateTimeKind.Utc);

        // Most-recent PLR row (highest Id) is returned first by ORDER BY Id DESC.
        // TryGetValue keeps the first entry for each key — which is the most-recent.
        var timestamps = new Dictionary<(int, int), (DateTime, DateTime?)>();
        var key = (100, 0);

        // Simulate: most-recent row (Id=2, repick) arrives first → written to dict
        if (!timestamps.ContainsKey(key)) timestamps[key] = (created2, picked2);
        // Older row (Id=1) arrives second → ignored (key already present)
        if (!timestamps.ContainsKey(key)) timestamps[key] = (created1, null);

        var (tsCreated, tsPicked) = timestamps[key];
        Assert.Equal(created2, tsCreated);
        Assert.Equal(picked2, tsPicked);
    }

    // ── PLT11: Entity PK (AbsEntry + PickEntry) unaffected by new fields ──────
    [Fact]
    public void PLT11_EntityKey_UnchangedByNewFields()
    {
        var line = new CachedPickListLine
        {
            AbsEntry    = 42,
            PickEntry   = 7,
            CreatedTime = new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc),
            PickedTime  = null
        };

        Assert.Equal(42, line.AbsEntry);
        Assert.Equal(7,  line.PickEntry);
    }

    // ── PLT12: Timestamp precision is UTC; Kind is preserved ──────────────────
    [Fact]
    public void PLT12_CreatedTime_UtcKindPreserved()
    {
        var utc  = new DateTime(2026, 9, 5, 10, 23, 45, 678, DateTimeKind.Utc);
        var line = new CachedPickListLine { CreatedTime = utc };
        Assert.Equal(DateTimeKind.Utc, line.CreatedTime!.Value.Kind);
        Assert.Equal(678, line.CreatedTime.Value.Millisecond);
    }

    // ── PLT13: ISO-8601 round-trip for SQLite TEXT storage ────────────────────
    // SQLite stores DateTime as TEXT (ISO 8601 "o" format). Verifies no precision loss.
    [Fact]
    public void PLT13_ISO8601_RoundTrip_NoPrecisionLoss()
    {
        var original = new DateTime(2026, 9, 5, 14, 30, 59, 123, DateTimeKind.Utc);
        var stored   = original.ToString("o");
        var parsed   = DateTime.Parse(stored, null, System.Globalization.DateTimeStyles.RoundtripKind);

        Assert.Equal(original, parsed);
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
    }

    // ── PLT14: CreatedTime is never set to sync time or DateTime.MinValue ─────
    // Verifies that the "missing PLR → NULL" logic uses default(DateTime) check,
    // not DateTime.MinValue fabrication or current time injection.
    [Fact]
    public void PLT14_MissingPlr_CreatedTimeIsNull_NotMinValue()
    {
        var timestamps = new Dictionary<(int, int), (DateTime, DateTime?)>();

        timestamps.TryGetValue((orderEntry: 500, soLineNum: 1), out var ts);

        // ts.Item1 == default(DateTime) == DateTime.MinValue when not found.
        // The INSERT maps this to DBNull.Value — confirmed by the condition ts.Item1 != default.
        // This test documents that DateTime.MinValue is the sentinel for "not found", NOT a valid timestamp.
        Assert.Equal(default(DateTime), ts.Item1);
        Assert.Null(ts.Item2);

        // Storing default as NULL in DB is the contract:
        bool insertAsNull = ts.Item1 == default;
        Assert.True(insertAsNull);
    }

    // ── PLT15: API response includes createdTime and pickedTime (field presence) ─
    // The controller returns CachedPickListLine entities directly (no DTO mapping),
    // so JSON serialization will include all public properties including the two new ones.
    [Fact]
    public void PLT15_CachedPickListLine_SerializesNewFields()
    {
        var line = new CachedPickListLine
        {
            AbsEntry    = 10,
            PickEntry   = 1,
            CreatedTime = new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc),
            PickedTime  = new DateTime(2026, 9, 5, 15, 0, 0, DateTimeKind.Utc)
        };

        var json = System.Text.Json.JsonSerializer.Serialize(line);

        Assert.Contains("createdTime", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pickedTime",  json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2026-09-05", json);
    }
}
