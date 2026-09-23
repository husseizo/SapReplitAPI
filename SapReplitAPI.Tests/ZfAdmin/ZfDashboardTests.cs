using SapReplitAPI.Models.ZoneFulfillment;
using SapReplitAPI.Services.ZoneFulfillment;
using Xunit;

namespace SapReplitAPI.Tests.ZfAdmin;

/// <summary>
/// ZDB_01–ZDB_20: ZF Diagnosis Console Phase 2 — dashboard model tests.
///
/// All tests are pure in-memory (no SQL, no COM, no SAP mutations).
/// Tests validate the model/filter/aging/idempotency logic without requiring
/// a live database connection.
///
/// Reference cases:
///   SO 28879: TechnicalStatus=ACTIVE, ResolutionStatus=RESOLVED — primary acceptance case.
///   SO 28890: no active incident (ZF_HEALTHY).
///   SO 28917: no active incident (ZF_COMPLETED).
/// </summary>
public sealed class ZfDashboardTests
{
    // ── ZDB_01: summary counts active correctly ───────────────────────────────

    [Fact]
    public void ZDB_01_Summary_CountsActive()
    {
        var items = new List<ZfIncidentListItem>
        {
            MakeItem("28879_20092_ZF_FRAGMENT_RDR1_MISSING", 28879, ZfDiagnosticIncidentStatus.Active,
                     ZfIncidentStatusValue.Resolved, ZfDiagnosticSeverity.High),
            MakeItem("99001_10001_ZF_FRAGMENT_RDR1_MISSING", 99001, ZfDiagnosticIncidentStatus.Active,
                     ZfIncidentStatusValue.Active, ZfDiagnosticSeverity.High),
        };

        var summary = BuildSummary(items);

        Assert.Equal(2, summary.ActiveIncidentCount);
        Assert.Equal(2, summary.HighSeverityCount);
    }

    // ── ZDB_02: SO 28879 appears correctly — TechnicalStatus=ACTIVE, ResolutionStatus=RESOLVED ─

    [Fact]
    public void ZDB_02_SO28879_ActiveTechnical_ResolvedHuman()
    {
        var items = new List<ZfIncidentListItem>
        {
            MakeItem("28879_20092_ZF_FRAGMENT_RDR1_MISSING", 28879,
                     ZfDiagnosticIncidentStatus.Active,
                     ZfIncidentStatusValue.Resolved,
                     ZfDiagnosticSeverity.High,
                     latestResolution: ZfIncidentResolution.AcknowledgedExternalSapEdit)
        };

        var item = items[0];

        // B1: both status dimensions must be populated and distinct
        Assert.Equal(ZfDiagnosticIncidentStatus.Active, item.TechnicalStatus);
        Assert.Equal(ZfIncidentStatusValue.Resolved, item.ResolutionStatus);
        Assert.Equal(ZfIncidentResolution.AcknowledgedExternalSapEdit, item.LatestResolution);
        Assert.Equal(20092L, item.FragmentId);
        Assert.Equal(ZfDiagnosticSeverity.High, item.Severity);
        Assert.Equal(ZfConsistencyStatus.FragmentRdr1Missing, item.Code);
    }

    // ── ZDB_03: ACTIVE technical + no resolution → ResolutionStatus = ACTIVE ──

    [Fact]
    public void ZDB_03_NoResolution_ResolutionStatusIsActive()
    {
        var item = MakeItem("99001_10001_ZF_FRAGMENT_RDR1_MISSING", 99001,
            ZfDiagnosticIncidentStatus.Active, ZfIncidentStatusValue.Active,
            ZfDiagnosticSeverity.High);

        Assert.Equal(ZfIncidentStatusValue.Active, item.ResolutionStatus);
        Assert.Null(item.LatestResolution);
    }

    // ── ZDB_04: filter by TechnicalStatus=ACTIVE excludes HISTORICAL ─────────

    [Fact]
    public void ZDB_04_Filter_TechnicalStatus_Active_ExcludesHistorical()
    {
        var items = new List<ZfIncidentListItem>
        {
            MakeItem("A", 1, ZfDiagnosticIncidentStatus.Active, ZfIncidentStatusValue.Active, "HIGH"),
            MakeItem("B", 2, ZfDiagnosticIncidentStatus.Historical, ZfIncidentStatusValue.Resolved, "HIGH"),
        };

        var query = new ZfIncidentListQuery { TechnicalStatus = ZfDiagnosticIncidentStatus.Active };
        var filtered = ApplyFilters(items, query);

        Assert.Single(filtered);
        Assert.Equal("A", filtered[0].IncidentKey);
    }

    // ── ZDB_05: filter by ResolutionStatus=RESOLVED returns correct items ─────

    [Fact]
    public void ZDB_05_Filter_ResolutionStatus_Resolved()
    {
        var items = new List<ZfIncidentListItem>
        {
            MakeItem("K1", 1, "ACTIVE", ZfIncidentStatusValue.Resolved, "HIGH"),
            MakeItem("K2", 2, "ACTIVE", ZfIncidentStatusValue.Active, "HIGH"),
            MakeItem("K3", 3, "ACTIVE", ZfIncidentStatusValue.Deferred, "HIGH"),
        };

        var query = new ZfIncidentListQuery { ResolutionStatus = ZfIncidentStatusValue.Resolved };
        var filtered = ApplyFilters(items, query);

        Assert.Single(filtered);
        Assert.Equal("K1", filtered[0].IncidentKey);
    }

    // ── ZDB_06: filter by severity=HIGH ──────────────────────────────────────

    [Fact]
    public void ZDB_06_Filter_Severity_High()
    {
        var items = new List<ZfIncidentListItem>
        {
            MakeItem("H", 1, "ACTIVE", "ACTIVE", ZfDiagnosticSeverity.High),
            MakeItem("M", 2, "ACTIVE", "ACTIVE", ZfDiagnosticSeverity.Medium),
        };

        var query = new ZfIncidentListQuery { Severity = ZfDiagnosticSeverity.High };
        var filtered = ApplyFilters(items, query);

        Assert.Single(filtered);
        Assert.Equal("H", filtered[0].IncidentKey);
    }

    // ── ZDB_07: filter by incidentCode ───────────────────────────────────────

    [Fact]
    public void ZDB_07_Filter_IncidentCode()
    {
        var items = new List<ZfIncidentListItem>
        {
            MakeItem("K1", 1, "ACTIVE", "ACTIVE", "HIGH",
                code: ZfConsistencyStatus.FragmentRdr1Missing),
            MakeItem("K2", 2, "ACTIVE", "ACTIVE", "HIGH",
                code: "OTHER_CODE"),
        };

        var query = new ZfIncidentListQuery { IncidentCode = ZfConsistencyStatus.FragmentRdr1Missing };
        var filtered = ApplyFilters(items, query);

        Assert.Single(filtered);
        Assert.Equal("K1", filtered[0].IncidentKey);
    }

    // ── ZDB_08: filter by SoDocNum ────────────────────────────────────────────

    [Fact]
    public void ZDB_08_Filter_SoDocNum()
    {
        var items = new List<ZfIncidentListItem>
        {
            MakeItem("K1", 28879, "ACTIVE", "ACTIVE", "HIGH"),
            MakeItem("K2", 99001, "ACTIVE", "ACTIVE", "HIGH"),
        };

        var query = new ZfIncidentListQuery { SoDocNum = 28879 };
        var filtered = ApplyFilters(items, query);

        Assert.Single(filtered);
        Assert.Equal(28879, filtered[0].SoDocNum);
    }

    // ── ZDB_09: filter by ItemCode ────────────────────────────────────────────

    [Fact]
    public void ZDB_09_Filter_ItemCode()
    {
        var items = new List<ZfIncidentListItem>
        {
            MakeItem("K1", 1, "ACTIVE", "ACTIVE", "HIGH", itemCode: "VAG13782"),
            MakeItem("K2", 2, "ACTIVE", "ACTIVE", "HIGH", itemCode: "BM12441"),
        };

        var query = new ZfIncidentListQuery { ItemCode = "VAG" };
        var filtered = ApplyFilters(items, query);

        Assert.Single(filtered);
        Assert.Equal("VAG13782", filtered[0].ItemCode);
    }

    // ── ZDB_10: filter by date range ──────────────────────────────────────────

    [Fact]
    public void ZDB_10_Filter_DateRange()
    {
        var now = DateTime.UtcNow;
        var items = new List<ZfIncidentListItem>
        {
            MakeItem("Old", 1, "ACTIVE", "ACTIVE", "HIGH", detectedAt: now.AddDays(-10)),
            MakeItem("Recent", 2, "ACTIVE", "ACTIVE", "HIGH", detectedAt: now.AddDays(-1)),
        };

        var query = new ZfIncidentListQuery { DateFrom = now.AddDays(-3) };
        var filtered = ApplyFilters(items, query);

        Assert.Single(filtered);
        Assert.Equal("Recent", filtered[0].IncidentKey);
    }

    // ── ZDB_11: filter by hasResolution=true ──────────────────────────────────

    [Fact]
    public void ZDB_11_Filter_HasResolution_True()
    {
        var items = new List<ZfIncidentListItem>
        {
            MakeItem("W", 1, "ACTIVE", "RESOLVED", "HIGH",
                latestResolution: ZfIncidentResolution.AcknowledgedExternalSapEdit),
            MakeItem("X", 2, "ACTIVE", "ACTIVE", "HIGH"),
        };

        var query = new ZfIncidentListQuery { HasResolution = true };
        var filtered = ApplyFilters(items, query);

        Assert.Single(filtered);
        Assert.Equal("W", filtered[0].IncidentKey);
    }

    // ── ZDB_12: filter by hasResolution=false ────────────────────────────────

    [Fact]
    public void ZDB_12_Filter_HasResolution_False()
    {
        var items = new List<ZfIncidentListItem>
        {
            MakeItem("W", 1, "ACTIVE", "RESOLVED", "HIGH",
                latestResolution: ZfIncidentResolution.AcknowledgedExternalSapEdit),
            MakeItem("X", 2, "ACTIVE", "ACTIVE", "HIGH"),
        };

        var query = new ZfIncidentListQuery { HasResolution = false };
        var filtered = ApplyFilters(items, query);

        Assert.Single(filtered);
        Assert.Equal("X", filtered[0].IncidentKey);
    }

    // ── ZDB_13: pagination returns correct slice ──────────────────────────────

    [Fact]
    public void ZDB_13_Pagination_CorrectSlice()
    {
        var items = Enumerable.Range(1, 10)
            .Select(i => MakeItem($"K{i}", i, "ACTIVE", "ACTIVE", "HIGH"))
            .ToList();

        var page1 = Paginate(items, 1, 3);
        var page2 = Paginate(items, 2, 3);
        var page4 = Paginate(items, 4, 3);

        Assert.Equal(3, page1.Items.Count);
        Assert.Equal(3, page2.Items.Count);
        Assert.Single(page4.Items);       // last page: item 10
        Assert.Equal(10, page1.TotalCount);
        Assert.True(page1.HasMore);
        Assert.False(page4.HasMore);
    }

    // ── ZDB_14: aging buckets classify correctly ──────────────────────────────

    [Theory]
    [InlineData(30,    ZfAgingBucket.Under1H)]
    [InlineData(90,    ZfAgingBucket.H1To4)]
    [InlineData(300,   ZfAgingBucket.H4To24)]
    [InlineData(2000,  ZfAgingBucket.D1To3)]
    [InlineData(10000, ZfAgingBucket.Over3D)]
    public void ZDB_14_AgingBucket_ClassifiesCorrectly(double minutes, string expected)
    {
        Assert.Equal(expected, ZfAgingBucket.Classify(minutes));
    }

    // ── ZDB_15: filter by agingBucket ─────────────────────────────────────────

    [Fact]
    public void ZDB_15_Filter_AgingBucket()
    {
        var now = DateTime.UtcNow;
        var items = new List<ZfIncidentListItem>
        {
            MakeItem("Fresh", 1, "ACTIVE", "ACTIVE", "HIGH",
                detectedAt: now.AddMinutes(-10)),   // <1h
            MakeItem("Old",   2, "ACTIVE", "ACTIVE", "HIGH",
                detectedAt: now.AddHours(-5)),      // 4-24h
        };

        // Assign aging
        foreach (var i in items)
        {
            var age = (now - i.DetectedAtUtc).TotalMinutes;
            i.AgeMinutes  = age;
            i.AgeHours    = age / 60;
            i.AgingBucket = ZfAgingBucket.Classify(age);
        }

        var query = new ZfIncidentListQuery { AgingBucket = ZfAgingBucket.Under1H };
        var filtered = ApplyFilters(items, query);

        Assert.Single(filtered);
        Assert.Equal("Fresh", filtered[0].IncidentKey);
    }

    // ── ZDB_16: summary keeps status dimensions separate ──────────────────────

    [Fact]
    public void ZDB_16_Summary_StatusDimensionsSeparate()
    {
        var items = new List<ZfIncidentListItem>
        {
            // ACTIVE technical + RESOLVED human (SO 28879 state)
            MakeItem("K1", 28879, ZfDiagnosticIncidentStatus.Active,
                     ZfIncidentStatusValue.Resolved, "HIGH"),
            // ACTIVE technical + ACTIVE human (no resolution)
            MakeItem("K2", 99001, ZfDiagnosticIncidentStatus.Active,
                     ZfIncidentStatusValue.Active, "HIGH"),
        };

        var summary = BuildSummary(items);

        Assert.Equal(2, summary.ActiveIncidentCount);
        Assert.True(summary.IncidentsByTechnicalStatus.ContainsKey("ACTIVE"));
        Assert.Equal(2, summary.IncidentsByTechnicalStatus["ACTIVE"]);
        Assert.True(summary.IncidentsByResolutionStatus.ContainsKey("RESOLVED"));
        Assert.Equal(1, summary.IncidentsByResolutionStatus["RESOLVED"]);
        Assert.True(summary.IncidentsByResolutionStatus.ContainsKey("ACTIVE"));
        Assert.Equal(1, summary.IncidentsByResolutionStatus["ACTIVE"]);
    }

    // ── ZDB_17: UnresolvedOver24h counts correctly ────────────────────────────
    // Unresolved = TechnicalStatus=ACTIVE AND no human resolution AND age > 24h.
    // SO 28879 (ACTIVE technical + RESOLVED human) must NOT count even though it is >24h.

    [Fact]
    public void ZDB_17_UnresolvedOver24h_CountsCorrectly()
    {
        var now = DateTime.UtcNow;
        var items = new List<ZfIncidentListItem>
        {
            // > 24h, no resolution → counts
            MakeItem("Old", 1, "ACTIVE", "ACTIVE", "HIGH",
                detectedAt: now.AddHours(-30)),
            // < 24h, no resolution → does not count
            MakeItem("Fresh", 2, "ACTIVE", "ACTIVE", "HIGH",
                detectedAt: now.AddHours(-12)),
            // > 24h, HAS resolution (SO 28879 case) → must NOT count
            MakeItem("Resolved", 3, "ACTIVE", "RESOLVED", "HIGH",
                detectedAt: now.AddHours(-50),
                latestResolution: ZfIncidentResolution.AcknowledgedExternalSapEdit),
        };

        foreach (var i in items)
            i.AgeHours = (now - i.DetectedAtUtc).TotalHours;

        var summary = BuildSummary(items);

        // Only item 1 should count — item 2 is <24h, item 3 is human-resolved
        Assert.Equal(1, summary.UnresolvedOver24h);
    }

    // ── ZDB_18: SO 28890 / 28917 not in active queue ────────────────────────

    [Fact]
    public void ZDB_18_SO28890_SO28917_NotInActiveQueue()
    {
        // 28890 (Orch=20068, Frag=20117) and 28917 (Orch=20095, Frag=20155) had ZF fragments
        // that recovered naturally (RDR1 lines now present in MOLAS_Live_2021).
        // They have no ZfIncidentResolutions record, so they are invisible to both the live
        // detection query (excluded by WHERE sap.DocEntry IS NULL) and the historical section.
        // This is a documented architectural gap — these are not in the active queue.
        var items = new List<ZfIncidentListItem>
        {
            // Only 28879 active
            MakeItem("28879_20092_ZF_FRAGMENT_RDR1_MISSING", 28879,
                     ZfDiagnosticIncidentStatus.Active, ZfIncidentStatusValue.Resolved, "HIGH"),
        };

        var query = new ZfIncidentListQuery { TechnicalStatus = ZfDiagnosticIncidentStatus.Active };
        var filtered = ApplyFilters(items, query);

        Assert.Single(filtered);
        Assert.Equal(28879, filtered[0].SoDocNum);

        // 28890 / 28917 absent — they have no incidents so never appear in list
        Assert.DoesNotContain(filtered, i => i.SoDocNum == 28890);
        Assert.DoesNotContain(filtered, i => i.SoDocNum == 28917);
    }

    // ── ZDB_19: historical incident retains resolution data ───────────────────

    [Fact]
    public void ZDB_19_HistoricalIncident_RetainsResolutionData()
    {
        var item = new ZfIncidentListItem
        {
            IncidentKey            = "28879_20092_ZF_FRAGMENT_RDR1_MISSING",
            SoDocNum               = 28879,
            TechnicalStatus        = ZfDiagnosticIncidentStatus.Historical,
            ResolutionStatus       = ZfIncidentStatusValue.Resolved,
            LatestResolution       = ZfIncidentResolution.AcknowledgedExternalSapEdit,
            LatestResolutionOperator = "hussein",
            Code                   = ZfConsistencyStatus.FragmentRdr1Missing,
            Severity               = ZfDiagnosticSeverity.High,
            DetectedAtUtc          = DateTime.UtcNow.AddHours(-5),
        };

        Assert.Equal(ZfDiagnosticIncidentStatus.Historical, item.TechnicalStatus);
        Assert.Equal(ZfIncidentStatusValue.Resolved, item.ResolutionStatus);
        Assert.NotNull(item.LatestResolution);
    }

    // ── ZDB_20: incident list response structure ──────────────────────────────

    [Fact]
    public void ZDB_20_IncidentListResponse_Structure()
    {
        var items = Enumerable.Range(1, 5)
            .Select(i => MakeItem($"K{i}", i, "ACTIVE", "ACTIVE", "HIGH"))
            .ToList();

        var response = Paginate(items, 1, 10);

        Assert.IsType<ZfIncidentListResponse>(response);
        Assert.Equal(5, response.TotalCount);
        Assert.Equal(1, response.Page);
        Assert.Equal(10, response.PageSize);
        Assert.False(response.HasMore);
        Assert.Equal(5, response.Items.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ZfIncidentListItem MakeItem(
        string key, int soDocNum,
        string technicalStatus, string resolutionStatus, string severity,
        string code            = ZfConsistencyStatus.FragmentRdr1Missing,
        string itemCode        = "VAG13782",
        string? latestResolution = null,
        DateTime? detectedAt   = null) => new()
    {
        IncidentKey          = key,
        SoDocNum             = soDocNum,
        FragmentId           = 20092,
        ItemCode             = itemCode,
        Code                 = code,
        Category             = ZfDiagnosticCategory.SapZfIntegrityDivergence,
        Severity             = severity,
        TechnicalStatus      = technicalStatus,
        ResolutionStatus     = resolutionStatus,
        LatestResolution     = latestResolution,
        OrchestrationState   = "Accepted",
        DetectedAtUtc        = detectedAt ?? DateTime.UtcNow.AddHours(-5),
        LastObservedAtUtc    = DateTime.UtcNow,
    };

    /// <summary>Simulates the summary calculation logic from ZfDashboardService.</summary>
    private static ZfDiagnosticSummaryDto BuildSummary(List<ZfIncidentListItem> items)
    {
        var dto = new ZfDiagnosticSummaryDto();
        var now = DateTime.UtcNow;
        foreach (var inc in items)
        {
            if (inc.TechnicalStatus == ZfDiagnosticIncidentStatus.Active)
            {
                dto.ActiveIncidentCount++;
                if (inc.Severity == ZfDiagnosticSeverity.High)   dto.HighSeverityCount++;
                if (inc.Severity == ZfDiagnosticSeverity.Medium) dto.MediumSeverityCount++;
                if (inc.AgeHours >= 24 && inc.LatestResolution is null) dto.UnresolvedOver24h++;
            }
            Increment(dto.IncidentsByCode, inc.Code);
            Increment(dto.IncidentsBySeverity, inc.Severity);
            Increment(dto.IncidentsByTechnicalStatus, inc.TechnicalStatus);
            Increment(dto.IncidentsByResolutionStatus, inc.ResolutionStatus);
        }
        return dto;
    }

    private static List<ZfIncidentListItem> ApplyFilters(
        List<ZfIncidentListItem> items, ZfIncidentListQuery query)
    {
        IEnumerable<ZfIncidentListItem> q = items;
        if (!string.IsNullOrWhiteSpace(query.TechnicalStatus))
            q = q.Where(i => i.TechnicalStatus.Equals(query.TechnicalStatus, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query.ResolutionStatus))
            q = q.Where(i => i.ResolutionStatus.Equals(query.ResolutionStatus, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query.Severity))
            q = q.Where(i => i.Severity.Equals(query.Severity, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(query.IncidentCode))
            q = q.Where(i => i.Code.Equals(query.IncidentCode, StringComparison.OrdinalIgnoreCase));
        if (query.SoDocNum.HasValue)
            q = q.Where(i => i.SoDocNum == query.SoDocNum.Value);
        if (!string.IsNullOrWhiteSpace(query.ItemCode))
            q = q.Where(i => i.ItemCode.Contains(query.ItemCode, StringComparison.OrdinalIgnoreCase));
        if (query.DateFrom.HasValue)
            q = q.Where(i => i.DetectedAtUtc >= query.DateFrom.Value);
        if (query.DateTo.HasValue)
            q = q.Where(i => i.DetectedAtUtc <= query.DateTo.Value);
        if (query.HasResolution == true)
            q = q.Where(i => i.LatestResolution != null);
        if (query.HasResolution == false)
            q = q.Where(i => i.LatestResolution == null);
        if (!string.IsNullOrWhiteSpace(query.AgingBucket))
            q = q.Where(i => i.AgingBucket == query.AgingBucket);
        return q.ToList();
    }

    private static ZfIncidentListResponse Paginate(
        List<ZfIncidentListItem> items, int page, int size) => new()
    {
        TotalCount = items.Count,
        Page       = page,
        PageSize   = size,
        HasMore    = page * size < items.Count,
        Items      = items.Skip((page - 1) * size).Take(size).ToList(),
    };

    private static void Increment(Dictionary<string, int> dict, string key)
    {
        dict.TryGetValue(key, out var v);
        dict[key] = v + 1;
    }
}
