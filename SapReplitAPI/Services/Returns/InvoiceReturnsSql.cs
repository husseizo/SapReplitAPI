namespace SapReplitAPI.Services.Returns;

/// <summary>Read-only SAP SQL shared by the API and relational contract tests.</summary>
public static class InvoiceReturnsSql
{
    // A completed credit memo is a posted ORIN, including one whose DocStatus is O.
    // A return request contributes only its remaining OPEN line quantity, never INV1.OpenQty.
    public const string Quantities = """
        WITH CreditLines AS (
            SELECT R.BaseEntry AS InvoiceDocEntry, R.BaseLine AS InvoiceLineNum,
                   R.Quantity AS PathAQty, 0 AS PathBQty
            FROM RIN1 R JOIN ORIN H ON H.DocEntry = R.DocEntry
            WHERE R.BaseType = 13 AND H.CANCELED = 'N'
            UNION ALL
            SELECT Q.BaseEntry, Q.BaseLine, 0, R.Quantity
            FROM RIN1 R JOIN ORIN H ON H.DocEntry = R.DocEntry
            JOIN RRR1 Q ON Q.DocEntry = R.BaseEntry AND Q.LineNum = R.BaseLine AND Q.BaseType = 13
            WHERE R.BaseType = 234000031 AND H.CANCELED = 'N'
        ), Returned AS (
            SELECT InvoiceDocEntry, InvoiceLineNum, SUM(PathAQty + PathBQty) AS ReturnedQty
            FROM CreditLines GROUP BY InvoiceDocEntry, InvoiceLineNum
        ), Pending AS (
            SELECT Q.BaseEntry AS InvoiceDocEntry, Q.BaseLine AS InvoiceLineNum,
                   SUM(CASE WHEN Q.OpenQty > 0 THEN Q.OpenQty ELSE 0 END) AS PendingQty
            FROM RRR1 Q JOIN ORRR H ON H.DocEntry = Q.DocEntry
            WHERE Q.BaseType = 13 AND H.CANCELED = 'N' AND H.DocStatus = 'O' AND Q.LineStatus = 'O'
            GROUP BY Q.BaseEntry, Q.BaseLine
        )
        SELECT H.DocEntry, H.DocNum, H.DocDate, H.CardCode, H.CardName,
               H.DocStatus, H.CANCELED, H.DocTotal, H.PaidToDate,
               H.DocTotal - H.PaidToDate AS BalanceDue,
               L.LineNum, L.ItemCode, L.Dscription, L.Quantity, L.Price, L.WhsCode,
               COALESCE(R.ReturnedQty, 0) AS ReturnedQty,
               COALESCE(P.PendingQty, 0) AS PendingQty
        FROM OINV H JOIN INV1 L ON L.DocEntry = H.DocEntry
        LEFT JOIN Returned R ON R.InvoiceDocEntry = L.DocEntry AND R.InvoiceLineNum = L.LineNum
        LEFT JOIN Pending P ON P.InvoiceDocEntry = L.DocEntry AND P.InvoiceLineNum = L.LineNum
        """;

    // UNION de-duplicates a CM linked by multiple lines or by both paths to the same invoice.
    // Preserve the existing document-total semantics; this is not an allocation of tax/net value.
    public static string CreditMemoEvidence(string invoiceIds) => $$"""
        SELECT X.InvoiceDocEntry, COUNT(*) AS CreditMemoCount, SUM(H.DocTotal) AS CreditMemoTotal
        FROM (
            SELECT R.BaseEntry AS InvoiceDocEntry, R.DocEntry AS CreditMemoDocEntry
            FROM RIN1 R WHERE R.BaseType = 13
            UNION
            SELECT Q.BaseEntry, R.DocEntry
            FROM RIN1 R JOIN RRR1 Q ON Q.DocEntry = R.BaseEntry AND Q.LineNum = R.BaseLine
            WHERE R.BaseType = 234000031 AND Q.BaseType = 13
        ) X JOIN ORIN H ON H.DocEntry = X.CreditMemoDocEntry
        WHERE H.CANCELED = 'N' AND X.InvoiceDocEntry IN ({{invoiceIds}})
        GROUP BY X.InvoiceDocEntry
        """;
}
