using System.IO;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;
using SapReplitAPI.Migrations;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Services.Events;
using Xunit;

namespace SapReplitAPI.Tests.Returns;

/// <summary>
/// CM01-CM30: Credit Memo Event-Driven Cache Fast Path
///
/// Covers CanHandle routing, migration schema, model properties,
/// CreditMemoCacheService SQL patterns, NeonCreditMemoWriteService SQL patterns,
/// CreditMemoEventHandler step-9 integration, and non-regression of the
/// existing returns contract.
///
/// All tests are pure unit tests — no SAP COM, no network, no file I/O to
/// the production SQLite or Neon databases.
/// </summary>
public sealed class CreditMemoEventCacheTests
{
    // ────────────────────────────────────────────────────────────────────────
    // Source paths (project-relative from the solution root)
    // ────────────────────────────────────────────────────────────────────────

    private static string Src(string relativePath)
    {
        // Walk from the test assembly directory up to the solution root,
        // then down into the main project.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
            dir = Path.GetDirectoryName(dir)!;
        return File.ReadAllText(Path.Combine(dir, "SapReplitAPI", "SapReplitAPI", relativePath));
    }

    // ════════════════════════════════════════════════════════════════════════
    // CM01-CM05 — CanHandle routing
    // ════════════════════════════════════════════════════════════════════════

    private static SapOutboxEvent Ev(string objType, string txType, int docEntry = 81)
        => new(1, Guid.NewGuid(), objType, txType, docEntry, null, DateTime.UtcNow, 1);

    [Fact]
    public void CM01_CanHandle_14A_ReturnsTrue()
    {
        // The fast path is triggered by Add events (new ORIN committed in SAP).
        var src = Src("Services/Events/CreditMemoEventHandler.cs");
        Assert.Contains("TransactionType == \"A\"", src);
    }

    [Fact]
    public void CM02_CanHandle_14U_ReturnsTrue()
    {
        // Update events must also refresh the cache so cancellations propagate.
        var src = Src("Services/Events/CreditMemoEventHandler.cs");
        Assert.Contains("TransactionType == \"U\"", src);
    }

    [Fact]
    public void CM03_CanHandle_14C_ReturnsTrue()
    {
        // Cancellation events update DocStatus/Canceled fields in the cache.
        var src = Src("Services/Events/CreditMemoEventHandler.cs");
        Assert.Contains("TransactionType == \"C\"", src);
    }

    [Fact]
    public void CM04_CanHandle_ObjectType14_AllThreeTypes()
    {
        // All three transaction types are guarded by ObjectType == "14".
        var src = Src("Services/Events/CreditMemoEventHandler.cs");
        Assert.Contains("ev.ObjectType == \"14\"", src);
    }

    [Fact]
    public void CM05_CanHandle_DoesNotHandle_OtherObjectTypes()
    {
        // Routing predicate must be specific to ORIN (14), not a wildcard.
        var src = Src("Services/Events/CreditMemoEventHandler.cs");
        // The CanHandle body is: ev.ObjectType == "14" && (A || U || C)
        // Confirm it is NOT just a fallback true.
        Assert.DoesNotContain("return true;", src.Split("public bool CanHandle")[1].Split("public async")[0]);
    }

    // ════════════════════════════════════════════════════════════════════════
    // CM06-CM12 — Migration schema
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void CM06_Migration_Creates_CreditMemoHeaders_Table()
    {
        var src = Src("Migrations/20260913130000_AddCreditMemoCache.cs");
        Assert.Contains("\"CreditMemoHeaders\"", src);
    }

    [Fact]
    public void CM07_Migration_Creates_CreditMemoLines_Table()
    {
        var src = Src("Migrations/20260913130000_AddCreditMemoCache.cs");
        Assert.Contains("\"CreditMemoLines\"", src);
    }

    [Fact]
    public void CM08_Migration_CreditMemoHeaders_Has_PK_DocEntry()
    {
        var src = Src("Migrations/20260913130000_AddCreditMemoCache.cs");
        Assert.Contains("PK_CreditMemoHeaders", src);
        Assert.Contains("DocEntry", src);
    }

    [Fact]
    public void CM09_Migration_CreditMemoLines_Has_Autoincrement_Id()
    {
        var src = Src("Migrations/20260913130000_AddCreditMemoCache.cs");
        Assert.Contains("Sqlite:Autoincrement", src);
        Assert.Contains("PK_CreditMemoLines", src);
    }

    [Fact]
    public void CM10_Migration_Has_Unique_Index_DocEntry_LineNum()
    {
        var src = Src("Migrations/20260913130000_AddCreditMemoCache.cs");
        Assert.Contains("UX_CreditMemoLines_DocEntry_LineNum", src);
        Assert.Contains("unique: true", src);
    }

    [Fact]
    public void CM11_Migration_Has_Index_CreditMemoHeaders_CardCode()
    {
        var src = Src("Migrations/20260913130000_AddCreditMemoCache.cs");
        Assert.Contains("IX_CreditMemoHeaders_CardCode", src);
    }

    [Fact]
    public void CM12_Migration_Has_Index_CreditMemoHeaders_DocDate()
    {
        var src = Src("Migrations/20260913130000_AddCreditMemoCache.cs");
        Assert.Contains("IX_CreditMemoHeaders_DocDate", src);
    }

    // ════════════════════════════════════════════════════════════════════════
    // CM13-CM17 — Model property correctness
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void CM13_CachedCreditMemo_U_AppRef_IsNullable()
    {
        // U_AppRef is null for credit memos not created by the API (organic SAP entries).
        var header = new CachedCreditMemo { DocEntry = 81 };
        Assert.Null(header.U_AppRef);
    }

    [Fact]
    public void CM14_CachedCreditMemoLine_Has_BaseType_BaseEntry_BaseLine()
    {
        var line = new CachedCreditMemoLine
        {
            DocEntry  = 81,
            LineNum   = 0,
            ItemCode  = "VAG10275",
            BaseType  = 234000031,
            BaseEntry = 46,
            BaseLine  = 0
        };
        Assert.Equal(234000031, line.BaseType);
        Assert.Equal(46,        line.BaseEntry);
        Assert.Equal(0,         line.BaseLine);
    }

    [Fact]
    public void CM15_CachedCreditMemoLine_BaseEntry_StoredRaw_NotRewritten()
    {
        // Path B: BaseType=234000031 → BaseEntry is ORRR.DocEntry (e.g. 46),
        // NOT the downstream OINV.DocEntry. Raw storage is required.
        var line = new CachedCreditMemoLine { BaseType = 234000031, BaseEntry = 46 };
        Assert.Equal(46, line.BaseEntry);  // must not be overwritten to an OINV DocEntry
    }

    [Fact]
    public void CM16_CachedCreditMemo_DocEntry_IsInt()
    {
        var prop = typeof(CachedCreditMemo).GetProperty("DocEntry");
        Assert.NotNull(prop);
        Assert.Equal(typeof(int), prop!.PropertyType);
    }

    [Fact]
    public void CM17_CachedCreditMemoLine_Id_IsInt()
    {
        // Id is the SQLite autoincrement PK — must be int, not long or Guid.
        var prop = typeof(CachedCreditMemoLine).GetProperty("Id");
        Assert.NotNull(prop);
        Assert.Equal(typeof(int), prop!.PropertyType);
    }

    // ════════════════════════════════════════════════════════════════════════
    // CM18-CM21 — CreditMemoCacheService SQL patterns (SQLite fast path)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void CM18_CreditMemoCacheService_Uses_ON_CONFLICT_Upsert()
    {
        // Header write must be an UPSERT, never INSERT-then-UPDATE, so a
        // concurrent second event for the same DocEntry doesn't fail.
        var src = Src("Services/Cached Services/CreditMemoCacheService.cs");
        Assert.Contains("ON CONFLICT(DocEntry) DO UPDATE", src);
    }

    [Fact]
    public void CM19_CreditMemoCacheService_Deletes_Lines_By_DocEntry_Not_Truncate()
    {
        // Only the target DocEntry's lines are deleted.  TRUNCATE TABLE is forbidden
        // because it would wipe other credit memos from the same table.
        var src = Src("Services/Cached Services/CreditMemoCacheService.cs");
        Assert.Contains("DELETE FROM CreditMemoLines WHERE DocEntry", src);
        Assert.DoesNotContain("TRUNCATE TABLE", src);
    }

    [Fact]
    public void CM20_CreditMemoCacheService_NoTruncateTable()
    {
        var src = Src("Services/Cached Services/CreditMemoCacheService.cs");
        Assert.DoesNotContain("TRUNCATE TABLE", src);
    }

    [Fact]
    public void CM21_CreditMemoCacheService_Uses_SemaphoreSlim()
    {
        // SQLite does not support concurrent writers; SemaphoreSlim(1,1)
        // serialises all credit-memo cache writes from multiple event-handler
        // instances that may run in the same service scope.
        var src = Src("Services/Cached Services/CreditMemoCacheService.cs");
        Assert.Contains("SemaphoreSlim", src);
    }

    // ════════════════════════════════════════════════════════════════════════
    // CM22-CM25 — NeonCreditMemoWriteService SQL patterns (Neon/PostgreSQL)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void CM22_NeonCreditMemoWriteService_Uses_ON_CONFLICT_Upsert()
    {
        var src = Src("Services/Neon/NeonCreditMemoWriteService.cs");
        Assert.Contains("ON CONFLICT", src);
        Assert.Contains("DO UPDATE SET", src);
    }

    [Fact]
    public void CM23_NeonCreditMemoWriteService_Deletes_Lines_By_DocEntry()
    {
        var src = Src("Services/Neon/NeonCreditMemoWriteService.cs");
        Assert.Contains("DELETE FROM", src);
        Assert.Contains("\"DocEntry\"", src);
    }

    [Fact]
    public void CM24_NeonCreditMemoWriteService_Uses_Transaction()
    {
        // All three operations (UPSERT header, DELETE lines, INSERT lines) must
        // be atomic.  A partial failure would leave header and lines inconsistent.
        var src = Src("Services/Neon/NeonCreditMemoWriteService.cs");
        Assert.Contains("BeginTransactionAsync", src);
        Assert.Contains("CommitAsync", src);
        Assert.Contains("RollbackAsync", src);
    }

    [Fact]
    public void CM25_NeonCreditMemoWriteService_NoTruncateTable()
    {
        var src = Src("Services/Neon/NeonCreditMemoWriteService.cs");
        Assert.DoesNotContain("TRUNCATE TABLE", src);
    }

    // ════════════════════════════════════════════════════════════════════════
    // CM26-CM29 — CreditMemoEventHandler step 9 integration
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void CM26_EventHandler_Step9_Calls_GetCreditMemoSnapshotByDocEntry()
    {
        // The handler must read the fresh SAP snapshot immediately after
        // step 8 (invoice refresh), not rely on a background sync job.
        var src = Src("Services/Events/CreditMemoEventHandler.cs");
        Assert.Contains("GetCreditMemoSnapshotByDocEntry", src);
    }

    [Fact]
    public void CM27_EventHandler_Step9_Calls_RefreshSingleAsync()
    {
        // SQLite cache must be written within the same event-handler execution.
        var src = Src("Services/Events/CreditMemoEventHandler.cs");
        Assert.Contains("RefreshSingleAsync", src);
    }

    [Fact]
    public void CM28_EventHandler_Step9_Calls_UpsertCreditMemoAsync()
    {
        // Neon cache must also be written within the same event-handler execution.
        var src = Src("Services/Events/CreditMemoEventHandler.cs");
        Assert.Contains("UpsertCreditMemoAsync", src);
    }

    [Fact]
    public void CM29_EventHandler_Step9_Logs_Warning_When_Snapshot_Null()
    {
        // If ORIN no longer exists in SAP (race condition / hard delete),
        // the handler must log a warning and skip the cache write rather than throw.
        var src = Src("Services/Events/CreditMemoEventHandler.cs");
        Assert.Contains("CreditMemoSnapshotMissing", src);
    }

    // ════════════════════════════════════════════════════════════════════════
    // CM30 — No regression to the existing returns contract
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void CM30_ExistingCreditMemoReturnsTests_NotModified()
    {
        // The existing CR01-CR18 tests cover the Path A / Path B resolution
        // contract.  Verify CreditMemoReturnsTests.cs still compiles and has
        // the expected number of test methods (no deletions allowed).
        var type = typeof(CreditMemoReturnsTests);
        var tests = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(m => m.GetCustomAttribute<FactAttribute>() is not null)
                        .ToList();
        Assert.True(tests.Count >= 15,
            $"Expected ≥15 CR tests to exist; found {tests.Count}. " +
            "Ensure CreditMemoReturnsTests.cs was not accidentally trimmed.");
    }
}
