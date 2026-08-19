using SAPbobsCOM;
using SapReplitAPI.Models.Inventory;
using System.Runtime.InteropServices;

// Global namespace — matches SapService.cs and SapWarehouseInventoryService.cs convention.

public class SapBinInventoryService
{
    private const string WhsInClause = "'001','002','003','004'";
    private const int    BatchSize   = 200;

    // Base SELECT shared by full and item-scoped queries.
    // Filters: operational warehouses, bin-managed, positive stock only.
    private const string BaseSelect = @"
SELECT Q.ItemCode,
       Q.WhsCode,
       Q.BinAbs     AS BinAbsEntry,
       B.BinCode,
       Q.OnHandQty  AS BinOnHand
FROM   OIBQ Q
INNER JOIN OBIN B ON B.AbsEntry = Q.BinAbs
INNER JOIN OWHS W ON W.WhsCode  = Q.WhsCode
WHERE  Q.WhsCode IN ({0})
  AND  W.BinActivat = 'Y'
  AND  Q.OnHandQty > 0";

    // ── Full snapshot ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns every positive-stock OIBQ row for warehouses 001–004 (BinActivat='Y' guard).
    /// Zero-stock bins are intentionally excluded — BinInventory stores operational locations only.
    /// </summary>
    public List<BinInventoryRow> GetFullSnapshot(Company company)
    {
        var sql = string.Format(BaseSelect, WhsInClause)
                + "\nORDER BY Q.ItemCode, Q.WhsCode, Q.BinAbs";
        return RunSnapshotQuery(company, sql);
    }

    // ── Item-scoped snapshot (batched) ─────────────────────────────────────────

    /// <summary>
    /// Returns positive-stock OIBQ rows for the given ItemCodes, batched in chunks of 200
    /// to avoid excessively large IN clauses.
    /// </summary>
    public List<BinInventoryRow> GetSnapshotForItems(Company company, IReadOnlyCollection<string> itemCodes)
    {
        if (itemCodes.Count == 0) return new();

        var all  = new List<BinInventoryRow>();
        var list = itemCodes.ToList();

        for (int off = 0; off < list.Count; off += BatchSize)
        {
            var batch    = list.Skip(off).Take(BatchSize);
            var inClause = string.Join(",", batch.Select(c => $"'{c.Replace("'", "''")}'"));
            var sql      = string.Format(BaseSelect, WhsInClause)
                         + $"\n  AND  Q.ItemCode IN ({inClause})"
                         + "\nORDER BY Q.ItemCode, Q.WhsCode, Q.BinAbs";
            all.AddRange(RunSnapshotQuery(company, sql));
        }

        return all;
    }

    // ── Changed-ItemCode detection ─────────────────────────────────────────────

    /// <summary>
    /// Returns ItemCodes with physical stock changes since <paramref name="from"/>.
    ///
    /// Sources:
    ///   A — OINM: physical inventory movements (delivery, GR, transfer, issue, adjustment,
    ///       production). Date-only — OINM has no reliable time-of-day field. The 2-hour
    ///       lookback window in BinInventorySyncService compensates.
    ///   B — OITM: item master changes (defensive). Time-aware via UpdateTS.
    ///
    /// Deliberately excludes ORDR/RDR1:
    ///   Sales Order creation/change commits stock in OITW.IsCommited but does NOT
    ///   physically move stock between bins. OIBQ.OnHandQty is unchanged by SO events alone.
    ///   Only when the Delivery is posted (→ OINM entry) does bin stock change.
    /// </summary>
    public ChangedItemsResult GetChangedItemCodes(Company company, DateTime from)
    {
        var fromDate    = from.Date.ToString("yyyy-MM-dd");
        var fromTimeInt = from.Hour * 10000 + from.Minute * 100 + from.Second;

        var sqlOinm = $@"
SELECT DISTINCT ItemCode
FROM   OINM
WHERE  DocDate >= '{fromDate}'
  AND  ItemCode IS NOT NULL
  AND  ItemCode <> ''";

        var sqlOitm = $@"
SELECT DISTINCT ItemCode
FROM   OITM
WHERE  (UpdateDate > '{fromDate}'
   OR  (UpdateDate = '{fromDate}' AND UpdateTS >= {fromTimeInt}))
  AND  ItemCode IS NOT NULL
  AND  ItemCode <> ''";

        var oinmCodes = CollectDistinctItemCodes(company, sqlOinm);
        var oitmCodes = CollectDistinctItemCodes(company, sqlOitm);

        var union = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        union.UnionWith(oinmCodes);
        union.UnionWith(oitmCodes);

        // OrdrCount = 0 — SOs are excluded intentionally (see remarks above).
        return new ChangedItemsResult(union, oinmCodes.Count, 0, oitmCodes.Count);
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private static List<BinInventoryRow> RunSnapshotQuery(Company company, string sql)
    {
        var rs   = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
        var rows = new List<BinInventoryRow>();
        try
        {
            rs.DoQuery(sql);
            while (!rs.EoF)
            {
                var itemCode    = rs.Fields.Item("ItemCode").Value?.ToString()?.Trim()    ?? "";
                var whsCode     = rs.Fields.Item("WhsCode").Value?.ToString()?.Trim()     ?? "";
                var binAbsEntry = Convert.ToInt32(rs.Fields.Item("BinAbsEntry").Value     ?? 0);
                var binCode     = rs.Fields.Item("BinCode").Value?.ToString()?.Trim()     ?? "";
                var binOnHand   = Convert.ToDecimal(rs.Fields.Item("BinOnHand").Value     ?? 0m);

                if (!string.IsNullOrWhiteSpace(itemCode) && !string.IsNullOrWhiteSpace(whsCode))
                    rows.Add(new BinInventoryRow(itemCode, whsCode, binAbsEntry, binCode, binOnHand));

                rs.MoveNext();
            }
        }
        finally
        {
            Marshal.ReleaseComObject(rs);
        }
        return rows;
    }

    private static HashSet<string> CollectDistinctItemCodes(Company company, string sql)
    {
        var rs     = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            rs.DoQuery(sql);
            while (!rs.EoF)
            {
                var code = rs.Fields.Item(0).Value?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(code))
                    result.Add(code);
                rs.MoveNext();
            }
        }
        finally
        {
            Marshal.ReleaseComObject(rs);
        }
        return result;
    }
}
