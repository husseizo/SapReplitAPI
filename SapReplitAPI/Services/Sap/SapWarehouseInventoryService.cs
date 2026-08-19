using SAPbobsCOM;
using SapReplitAPI.Models.Inventory;
using System.Runtime.InteropServices;

// Namespace required for ChangedItemsResult — file is in global namespace intentionally
// to match SapService.cs which also lives in the global namespace.

public class SapWarehouseInventoryService
{
    // Warehouses in scope — shared across full and batched queries
    private const string WhsInClause = "'001','002','003','004'";
    private const int BatchSize = 200;

    // ── Full snapshot ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns every OITW row for warehouses 001-004, joined to OWHS.
    /// No stock filter — zero-stock rows are included intentionally.
    /// </summary>
    public List<WarehouseInventoryRow> GetFullSnapshot(Company company)
    {
        const string sql = $@"
SELECT  W.ItemCode,
        W.WhsCode,
        H.WhsName        AS WarehouseName,
        W.OnHand,
        W.IsCommited     AS IsCommitted,
        W.OnOrder,
        (W.OnHand - W.IsCommited) AS AvailableToSell,
        H.BinActivat
FROM    OITW W
INNER   JOIN OWHS H ON H.WhsCode = W.WhsCode
WHERE   W.WhsCode IN ({WhsInClause})
ORDER   BY W.ItemCode, W.WhsCode";

        return RunSnapshotQuery(company, sql);
    }

    // ── Batched snapshot for a set of ItemCodes ────────────────────────────────

    /// <summary>
    /// Returns current OITW+OWHS rows for the specified ItemCodes, restricted to 001-004.
    /// Internally batched in chunks of 200 to avoid huge IN clauses.
    /// Zero-stock rows included.
    /// </summary>
    public List<WarehouseInventoryRow> GetSnapshotForItems(Company company, IReadOnlyCollection<string> itemCodes)
    {
        var all = new List<WarehouseInventoryRow>();
        var list = itemCodes.ToList();

        for (int off = 0; off < list.Count; off += BatchSize)
        {
            var batch = list.Skip(off).Take(BatchSize);
            var inClause = string.Join(",", batch.Select(c => $"'{c.Replace("'", "''")}'"));

            var sql = $@"
SELECT  W.ItemCode,
        W.WhsCode,
        H.WhsName        AS WarehouseName,
        W.OnHand,
        W.IsCommited     AS IsCommitted,
        W.OnOrder,
        (W.OnHand - W.IsCommited) AS AvailableToSell,
        H.BinActivat
FROM    OITW W
INNER   JOIN OWHS H ON H.WhsCode = W.WhsCode
WHERE   W.WhsCode IN ({WhsInClause})
  AND   W.ItemCode IN ({inClause})
ORDER   BY W.ItemCode, W.WhsCode";

            all.AddRange(RunSnapshotQuery(company, sql));
        }

        return all;
    }

    // ── Changed ItemCode detection ─────────────────────────────────────────────

    /// <summary>
    /// Returns per-source counts plus the union of ItemCodes changed since
    /// <paramref name="from"/> across three SAP sources.
    ///
    /// Precision strategy (correctness over cleverness):
    ///
    /// A — OINM: DocDate >= fromDate (date-only).
    ///     OINM has no reliable update-time field — DocDate can be backdated by users.
    ///     Date-only with the 2-hour lookback window is the correct tradeoff.
    ///
    /// B — ORDR/RDR1: time-aware using UpdateDate + UpdateTS (integer HHMMSS).
    ///     ORDR.UpdateTS is reliably maintained by SAP on every header change.
    ///     This avoids rescanning the entire day's SOs on every 15-min delta run.
    ///
    /// C — OITM: time-aware using UpdateDate + UpdateTS (same HHMMSS integer).
    ///     OITM.UpdateTS is present in the SQL Server SAP B1 version and reliable.
    ///     Master-data changes are infrequent, so the gain is modest but harmless.
    /// </summary>
    public ChangedItemsResult GetChangedItemCodes(Company company, DateTime from)
    {
        var fromDate    = from.Date.ToString("yyyy-MM-dd");
        // HHMMSS integer matching ORDR.UpdateTS / OITM.UpdateTS storage format
        var fromTimeInt = from.Hour * 10000 + from.Minute * 100 + from.Second;

        // Source A — physical inventory movements (DocDate only — no reliable time field)
        var sqlOinm = $@"
SELECT DISTINCT ItemCode
FROM   OINM
WHERE  DocDate >= '{fromDate}'
  AND  ItemCode IS NOT NULL
  AND  ItemCode <> ''";

        // Source B — Sales Order commitment changes, time-aware via UpdateTS.
        // No DocStatus/CANCELED filter: cancellation itself is a commitment change.
        var sqlOrdr = $@"
SELECT DISTINCT R.ItemCode
FROM   ORDR O
INNER  JOIN RDR1 R ON R.DocEntry = O.DocEntry
WHERE  (O.UpdateDate > '{fromDate}'
   OR  (O.UpdateDate = '{fromDate}' AND O.UpdateTS >= {fromTimeInt}))
  AND  R.ItemCode IS NOT NULL
  AND  R.ItemCode <> ''";

        // Source C — item master changes, time-aware via UpdateTS.
        var sqlOitm = $@"
SELECT DISTINCT ItemCode
FROM   OITM
WHERE  (UpdateDate > '{fromDate}'
   OR  (UpdateDate = '{fromDate}' AND UpdateTS >= {fromTimeInt}))
  AND  ItemCode IS NOT NULL
  AND  ItemCode <> ''";

        var oinmCodes = CollectDistinctItemCodes(company, sqlOinm);
        var ordrCodes = CollectDistinctItemCodes(company, sqlOrdr);
        var oitmCodes = CollectDistinctItemCodes(company, sqlOitm);

        var union = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        union.UnionWith(oinmCodes);
        union.UnionWith(ordrCodes);
        union.UnionWith(oitmCodes);

        return new ChangedItemsResult(union, oinmCodes.Count, ordrCodes.Count, oitmCodes.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HashSet<string> CollectDistinctItemCodes(Company company, string sql)
    {
        var rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
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

    private static List<WarehouseInventoryRow> RunSnapshotQuery(Company company, string sql)
    {
        var rs = (Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
        var rows = new List<WarehouseInventoryRow>();
        try
        {
            rs.DoQuery(sql);
            while (!rs.EoF)
            {
                var itemCode  = rs.Fields.Item("ItemCode").Value?.ToString()?.Trim()     ?? "";
                var whsCode   = rs.Fields.Item("WhsCode").Value?.ToString()?.Trim()       ?? "";
                var whsName   = rs.Fields.Item("WarehouseName").Value?.ToString()?.Trim() ?? "";
                var onHand    = Convert.ToDecimal(rs.Fields.Item("OnHand").Value     ?? 0m);
                var committed = Convert.ToDecimal(rs.Fields.Item("IsCommitted").Value ?? 0m);
                var onOrder   = Convert.ToDecimal(rs.Fields.Item("OnOrder").Value    ?? 0m);
                var avail     = Convert.ToDecimal(rs.Fields.Item("AvailableToSell").Value ?? 0m);
                var binAct    = rs.Fields.Item("BinActivat").Value?.ToString() ?? "N";

                rows.Add(new WarehouseInventoryRow(
                    itemCode, whsCode, whsName,
                    onHand, committed, onOrder, avail,
                    binAct.Equals("Y", StringComparison.OrdinalIgnoreCase)));

                rs.MoveNext();
            }
        }
        finally
        {
            Marshal.ReleaseComObject(rs);
        }
        return rows;
    }
}
