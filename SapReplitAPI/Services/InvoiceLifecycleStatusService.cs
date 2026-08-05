using SAPbobsCOM;
using SapReplitAPI.Models.Cache;
using SapReplitAPI.Models.InvoiceLifecycle;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SapReplitAPI.Services;

public class InvoiceLifecycleStatusService
{
    private const decimal MoneyTolerance = 0.01m;
    private readonly ILogger<InvoiceLifecycleStatusService> _logger;

    public InvoiceLifecycleStatusService(ILogger<InvoiceLifecycleStatusService> logger)
    {
        _logger = logger;
    }

    public InvoiceLifecycleStatusResult ComputeStatus(InvoiceLifecycleEvidence evidence)
    {
        var total = Math.Abs(evidence.DocTotal);
        var paymentFromHeader = Math.Max(0m, evidence.PaidToDate);
        var paymentFromLinkedDocs = Math.Max(0m, evidence.AppliedPaymentSum);
        var effectivePaid = Math.Max(paymentFromHeader, paymentFromLinkedDocs);
        var outstanding = Math.Max(0m, Math.Max(evidence.BalanceDue, total - effectivePaid));

        var status = InvoiceLifecycleStatus.Unknown;
        var canceledFlag = NormalizeFlag(evidence.CanceledFlag);

        if (canceledFlag == "C")
        {
            status = InvoiceLifecycleStatus.CancellationDocument;
        }
        else if (canceledFlag == "Y")
        {
            status = InvoiceLifecycleStatus.CanceledInvoice;
        }
        else if (evidence.HasReplacement)
        {
            status = InvoiceLifecycleStatus.ReplacedOrReinvoiced;
        }
        else if (
            total > 0m &&
            (
                effectivePaid >= total - MoneyTolerance ||
                (evidence.HasLinkedPayment && outstanding <= MoneyTolerance)
            ))
        {
            status = InvoiceLifecycleStatus.PaidInvoice;
        }
        else if (
            total > 0m &&
            effectivePaid > MoneyTolerance &&
            effectivePaid < total - MoneyTolerance)
        {
            status = InvoiceLifecycleStatus.PartiallyPaidInvoice;
        }
        else if (string.Equals(evidence.DocStatus, "C", StringComparison.OrdinalIgnoreCase))
        {
            status = InvoiceLifecycleStatus.ClosedInvoice;
        }
        else if (string.Equals(evidence.DocStatus, "O", StringComparison.OrdinalIgnoreCase))
        {
            status = InvoiceLifecycleStatus.OpenInvoice;
        }

        var replacementNumbers = evidence.ReplacementInvoiceDocNums.Count == 0
            ? string.Empty
            : string.Join(", ", evidence.ReplacementInvoiceDocNums.OrderBy(x => x));

        return new InvoiceLifecycleStatusResult
        {
            DocEntry = evidence.DocEntry,
            DocNum = evidence.DocNum,
            Status = status,
            DocStatusDisplay = ToDocStatusDisplay(status),
            InvoiceStatus = ToInvoiceStatusText(status),
            PaymentsStatus = BuildPaymentsStatusText(status, evidence, effectivePaid, outstanding, replacementNumbers),
            CancellationStatus = BuildCancellationStatusText(status, evidence, replacementNumbers),
            EffectivePaidAmount = effectivePaid,
            OutstandingAmount = outstanding,
            AppliedPaymentSum = paymentFromLinkedDocs,
            LinkedPaymentCount = evidence.LinkedPaymentCount,
            CreditMemoCount = evidence.CreditMemoCount,
            CreditMemoTotal = evidence.CreditMemoTotal,
            HasReplacement = evidence.HasReplacement,
            ReplacementInvoiceNumbers = replacementNumbers,
            LatestPaymentDate = evidence.LatestPaymentDate
        };
    }

    public InvoiceLifecycleStatus ParseStatusDisplay(
        string? docStatusDisplay,
        string? legacyCanceled = null,
        string? legacyDocStatus = null)
    {
        var display = (docStatusDisplay ?? string.Empty).Trim();
        if (display.Length > 0)
        {
            if (display.Equals("Replaced", StringComparison.OrdinalIgnoreCase) ||
                display.Equals("Replaced / Re-invoiced", StringComparison.OrdinalIgnoreCase))
                return InvoiceLifecycleStatus.ReplacedOrReinvoiced;

            if (display.Equals("Paid", StringComparison.OrdinalIgnoreCase) ||
                display.Equals("Paid Invoice", StringComparison.OrdinalIgnoreCase))
                return InvoiceLifecycleStatus.PaidInvoice;

            if (display.Equals("Partially Paid", StringComparison.OrdinalIgnoreCase) ||
                display.Equals("Partially Paid Invoice", StringComparison.OrdinalIgnoreCase))
                return InvoiceLifecycleStatus.PartiallyPaidInvoice;

            if (display.Equals("Cancelled", StringComparison.OrdinalIgnoreCase) ||
                display.Equals("Canceled Invoice", StringComparison.OrdinalIgnoreCase))
                return InvoiceLifecycleStatus.CanceledInvoice;

            if (display.Equals("Cancellation", StringComparison.OrdinalIgnoreCase) ||
                display.Equals("Cancellation Document", StringComparison.OrdinalIgnoreCase))
                return InvoiceLifecycleStatus.CancellationDocument;

            if (display.Equals("Closed", StringComparison.OrdinalIgnoreCase) ||
                display.Equals("Closed Invoice", StringComparison.OrdinalIgnoreCase))
                return InvoiceLifecycleStatus.ClosedInvoice;

            if (display.Equals("Open", StringComparison.OrdinalIgnoreCase) ||
                display.Equals("Open Invoice", StringComparison.OrdinalIgnoreCase))
                return InvoiceLifecycleStatus.OpenInvoice;
        }

        if (string.Equals(legacyCanceled, "Cancellation", StringComparison.OrdinalIgnoreCase))
            return InvoiceLifecycleStatus.CancellationDocument;

        if (string.Equals(legacyCanceled, "Canceled", StringComparison.OrdinalIgnoreCase))
            return InvoiceLifecycleStatus.CanceledInvoice;

        if (string.Equals(legacyDocStatus, "C", StringComparison.OrdinalIgnoreCase))
            return InvoiceLifecycleStatus.ClosedInvoice;

        if (string.Equals(legacyDocStatus, "O", StringComparison.OrdinalIgnoreCase))
            return InvoiceLifecycleStatus.OpenInvoice;

        return InvoiceLifecycleStatus.Unknown;
    }

    public string ToDashboardStatus(InvoiceLifecycleStatus status)
    {
        return status switch
        {
            InvoiceLifecycleStatus.CancellationDocument => "Cancelled-Reversal",
            InvoiceLifecycleStatus.CanceledInvoice => "Cancelled",
            InvoiceLifecycleStatus.ReplacedOrReinvoiced => "Replaced",
            InvoiceLifecycleStatus.PaidInvoice => "Paid",
            InvoiceLifecycleStatus.PartiallyPaidInvoice => "Partially Paid",
            InvoiceLifecycleStatus.ClosedInvoice => "Closed",
            InvoiceLifecycleStatus.OpenInvoice => "Open",
            _ => "Unknown"
        };
    }

    public bool IsOutstandingStatus(InvoiceLifecycleStatus status)
    {
        return status == InvoiceLifecycleStatus.OpenInvoice ||
               status == InvoiceLifecycleStatus.PartiallyPaidInvoice;
    }

    public bool IsCollectedStatus(InvoiceLifecycleStatus status)
    {
        return status == InvoiceLifecycleStatus.PaidInvoice ||
               status == InvoiceLifecycleStatus.ClosedInvoice;
    }

    public bool IsExcludedFromReceivables(InvoiceLifecycleStatus status)
    {
        return status == InvoiceLifecycleStatus.CancellationDocument ||
               status == InvoiceLifecycleStatus.CanceledInvoice ||
               status == InvoiceLifecycleStatus.ReplacedOrReinvoiced ||
               status == InvoiceLifecycleStatus.PaidInvoice;
    }

    public decimal GetOutstandingAmount(CachedInvoice invoice)
    {
        return Math.Max(0m, Math.Max(invoice.BalanceDue, invoice.DocTotal - invoice.PaidToDate));
    }

    public IReadOnlyDictionary<int, InvoiceLifecycleStatusResult> GetLifecycleStatusResults(
        SAPbobsCOM.Company company,
        IEnumerable<int> docEntries)
    {
        var evidence = LoadEvidence(company, docEntries);
        return evidence.Values
            .Select(ComputeStatus)
            .ToDictionary(x => x.DocEntry);
    }

    public IReadOnlyDictionary<int, InvoiceLifecycleEvidence> LoadEvidence(
        SAPbobsCOM.Company company,
        IEnumerable<int> docEntries)
    {
        var targetDocEntries = docEntries
            .Where(x => x > 0)
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var evidenceByDocEntry = new Dictionary<int, InvoiceLifecycleEvidence>();
        if (targetDocEntries.Count == 0)
            return evidenceByDocEntry;

        foreach (var chunk in Chunk(targetDocEntries, 200))
        {
            LoadHeaders(company, chunk, evidenceByDocEntry);
            LoadPaymentEvidence(company, chunk, evidenceByDocEntry);
            LoadCreditMemoEvidence(company, chunk, evidenceByDocEntry);
            LoadLineageKeys(company, chunk, evidenceByDocEntry);
        }

        MarkReferenceReplacements(company, evidenceByDocEntry);
        MarkLineageReplacements(company, evidenceByDocEntry);

        return evidenceByDocEntry;
    }

    public IReadOnlyList<InvoiceLifecycleStatusResult> GetLifecycleStatusResultsByDocNumbers(
        SAPbobsCOM.Company company,
        IEnumerable<int> docNums)
    {
        var docNumList = docNums.Where(x => x > 0).Distinct().OrderBy(x => x).ToList();
        if (docNumList.Count == 0)
            return Array.Empty<InvoiceLifecycleStatusResult>();

        var evidenceByDocEntry = LoadEvidenceByDocNumbers(company, docNumList);
        return evidenceByDocEntry.Values
            .OrderBy(x => x.DocNum)
            .Select(ComputeStatus)
            .ToList();
    }

    public void LogDebugForDocNumbers(SAPbobsCOM.Company company, IEnumerable<int> docNums)
    {
        var docNumList = docNums.Where(x => x > 0).Distinct().OrderBy(x => x).ToList();
        if (docNumList.Count == 0)
            return;

        var evidenceByDocEntry = LoadEvidenceByDocNumbers(company, docNumList);
        foreach (var evidence in evidenceByDocEntry.Values.OrderBy(x => x.DocNum))
        {
            var status = ComputeStatus(evidence);
            _logger.LogInformation(
                "[InvoiceLifecycleDebug] DocEntry={DocEntry} DocNum={DocNum} RawStatus={DocStatus} Canceled={Canceled} PaidToDate={PaidToDate} PaymentSum={PaymentSum} Payments={PaymentCount} CreditMemos={CreditMemoCount} Replacement={HasReplacement} UpstreamKeys={KeyCount} FinalStatus={FinalStatus}",
                evidence.DocEntry,
                evidence.DocNum,
                evidence.DocStatus,
                evidence.CanceledFlag,
                evidence.PaidToDate,
                evidence.AppliedPaymentSum,
                evidence.LinkedPaymentCount,
                evidence.CreditMemoCount,
                evidence.HasReplacement,
                evidence.UpstreamKeys.Count,
                status.DocStatusDisplay);
        }
    }

    private IReadOnlyDictionary<int, InvoiceLifecycleEvidence> LoadEvidenceByDocNumbers(
        SAPbobsCOM.Company company,
        IReadOnlyCollection<int> docNums)
    {
        var docEntries = new List<int>();
        SAPbobsCOM.Recordset? rs = null;

        try
        {
            rs = (SAPbobsCOM.Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
SELECT DocEntry
FROM OINV
WHERE DocNum IN ({string.Join(",", docNums)})
");

            while (!rs.EoF)
            {
                docEntries.Add(GetInt(rs, "DocEntry"));
                rs.MoveNext();
            }
        }
        finally
        {
            if (rs != null)
                Marshal.ReleaseComObject(rs);
        }

        return LoadEvidence(company, docEntries);
    }

    private void LoadHeaders(
        SAPbobsCOM.Company company,
        IReadOnlyCollection<int> docEntries,
        IDictionary<int, InvoiceLifecycleEvidence> evidenceByDocEntry)
    {
        SAPbobsCOM.Recordset? rs = null;

        try
        {
            rs = (SAPbobsCOM.Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
SELECT
    DocEntry,
    DocNum,
    DocDate,
    DocStatus,
    CANCELED,
    CardCode,
    CardName,
    DocTotal,
    PaidToDate,
    (DocTotal - PaidToDate) AS BalanceDue,
    ISNULL(ReceiptNum, 0) AS ReceiptNum,
    ISNULL(Comments, '') AS Comments,
    ISNULL(U_ReinvoicedFrom, '') AS U_ReinvoicedFrom
FROM OINV
WHERE DocEntry IN ({string.Join(",", docEntries)})
");

            while (!rs.EoF)
            {
                var docEntry = GetInt(rs, "DocEntry");
                evidenceByDocEntry[docEntry] = new InvoiceLifecycleEvidence
                {
                    DocEntry = docEntry,
                    DocNum = GetInt(rs, "DocNum"),
                    DocDate = GetDateTime(rs, "DocDate"),
                    DocStatus = GetString(rs, "DocStatus"),
                    CanceledFlag = GetString(rs, "CANCELED"),
                    CardCode = GetString(rs, "CardCode"),
                    CardName = GetString(rs, "CardName"),
                    DocTotal = GetDecimal(rs, "DocTotal"),
                    PaidToDate = GetDecimal(rs, "PaidToDate"),
                    BalanceDue = GetDecimal(rs, "BalanceDue"),
                    ReceiptNum = GetInt(rs, "ReceiptNum"),
                    Comments = GetString(rs, "Comments"),
                    ReinvoicedFrom = GetString(rs, "U_ReinvoicedFrom")
                };

                rs.MoveNext();
            }
        }
        finally
        {
            if (rs != null)
                Marshal.ReleaseComObject(rs);
        }
    }

    private void LoadPaymentEvidence(
        SAPbobsCOM.Company company,
        IReadOnlyCollection<int> docEntries,
        IDictionary<int, InvoiceLifecycleEvidence> evidenceByDocEntry)
    {
        SAPbobsCOM.Recordset? rs = null;

        try
        {
            rs = (SAPbobsCOM.Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
SELECT
    T1.DocEntry AS InvoiceDocEntry,
    COUNT(DISTINCT T0.DocEntry) AS PaymentCount,
    SUM(ISNULL(T1.SumApplied, 0)) AS AppliedPaymentSum,
    MAX(T0.DocDate) AS LatestPaymentDate
FROM ORCT T0
JOIN RCT2 T1 ON T1.DocNum = T0.DocEntry AND T1.InvType = 13
WHERE T1.DocEntry IN ({string.Join(",", docEntries)})
GROUP BY T1.DocEntry
");

            while (!rs.EoF)
            {
                var docEntry = GetInt(rs, "InvoiceDocEntry");
                if (evidenceByDocEntry.TryGetValue(docEntry, out var evidence))
                {
                    evidence.LinkedPaymentCount = GetInt(rs, "PaymentCount");
                    evidence.AppliedPaymentSum = GetDecimal(rs, "AppliedPaymentSum");
                    evidence.LatestPaymentDate = GetNullableDateTime(rs, "LatestPaymentDate");
                }

                rs.MoveNext();
            }
        }
        finally
        {
            if (rs != null)
                Marshal.ReleaseComObject(rs);
        }
    }

    private void LoadCreditMemoEvidence(
        SAPbobsCOM.Company company,
        IReadOnlyCollection<int> docEntries,
        IDictionary<int, InvoiceLifecycleEvidence> evidenceByDocEntry)
    {
        SAPbobsCOM.Recordset? rs = null;

        try
        {
            rs = (SAPbobsCOM.Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
SELECT
    R1.BaseEntry AS InvoiceDocEntry,
    COUNT(DISTINCT O.DocEntry) AS CreditMemoCount,
    SUM(ISNULL(O.DocTotal, 0)) AS CreditMemoTotal
FROM RIN1 R1
JOIN ORIN O ON O.DocEntry = R1.DocEntry
WHERE R1.BaseType = 13
  AND R1.BaseEntry IN ({string.Join(",", docEntries)})
GROUP BY R1.BaseEntry
");

            while (!rs.EoF)
            {
                var docEntry = GetInt(rs, "InvoiceDocEntry");
                if (evidenceByDocEntry.TryGetValue(docEntry, out var evidence))
                {
                    evidence.CreditMemoCount = GetInt(rs, "CreditMemoCount");
                    evidence.CreditMemoTotal = GetDecimal(rs, "CreditMemoTotal");
                }

                rs.MoveNext();
            }
        }
        finally
        {
            if (rs != null)
                Marshal.ReleaseComObject(rs);
        }
    }

    private void LoadLineageKeys(
        SAPbobsCOM.Company company,
        IReadOnlyCollection<int> docEntries,
        IDictionary<int, InvoiceLifecycleEvidence> evidenceByDocEntry)
    {
        SAPbobsCOM.Recordset? rs = null;

        try
        {
            rs = (SAPbobsCOM.Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
            rs.DoQuery($@"
SELECT
    T0.DocEntry AS InvoiceDocEntry,
    T1.BaseType,
    T1.BaseEntry,
    T1.BaseLine,
    D1.BaseType AS DeliveryBaseType,
    D1.BaseEntry AS DeliveryBaseEntry,
    D1.BaseLine AS DeliveryBaseLine
FROM OINV T0
JOIN INV1 T1 ON T0.DocEntry = T1.DocEntry
LEFT JOIN DLN1 D1
    ON T1.BaseType = 15
   AND D1.DocEntry = T1.BaseEntry
   AND D1.LineNum = T1.BaseLine
WHERE T0.DocEntry IN ({string.Join(",", docEntries)})
");

            while (!rs.EoF)
            {
                var invoiceDocEntry = GetInt(rs, "InvoiceDocEntry");
                if (!evidenceByDocEntry.TryGetValue(invoiceDocEntry, out var evidence))
                {
                    rs.MoveNext();
                    continue;
                }

                var baseType = GetInt(rs, "BaseType");
                var baseEntry = GetInt(rs, "BaseEntry");
                var baseLine = GetInt(rs, "BaseLine");

                if (baseType == 15 && baseEntry > 0)
                    evidence.UpstreamKeys.Add(BuildUpstreamKey("D", baseEntry, baseLine));

                if (baseType == 17 && baseEntry > 0)
                    evidence.UpstreamKeys.Add(BuildUpstreamKey("O", baseEntry, baseLine));

                var deliveryBaseType = GetNullableInt(rs, "DeliveryBaseType");
                var deliveryBaseEntry = GetNullableInt(rs, "DeliveryBaseEntry");
                var deliveryBaseLine = GetNullableInt(rs, "DeliveryBaseLine");

                if (deliveryBaseType == 17 && deliveryBaseEntry.HasValue && deliveryBaseLine.HasValue)
                    evidence.UpstreamKeys.Add(BuildUpstreamKey("O", deliveryBaseEntry.Value, deliveryBaseLine.Value));

                rs.MoveNext();
            }
        }
        finally
        {
            if (rs != null)
                Marshal.ReleaseComObject(rs);
        }
    }

    private void MarkReferenceReplacements(
        SAPbobsCOM.Company company,
        IDictionary<int, InvoiceLifecycleEvidence> evidenceByDocEntry)
    {
        var evidenceByDocNum = evidenceByDocEntry.Values.ToDictionary(x => x.DocNum);
        if (evidenceByDocNum.Count == 0)
            return;

        var docNums = evidenceByDocNum.Keys.OrderBy(x => x).ToList();

        foreach (var chunk in Chunk(docNums, 200))
        {
            SAPbobsCOM.Recordset? rs = null;
            try
            {
                rs = (SAPbobsCOM.Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
                rs.DoQuery($@"
SELECT
    DocEntry,
    DocNum,
    DocDate,
    CardCode,
    CANCELED,
    ISNULL(U_ReinvoicedFrom, '') AS U_ReinvoicedFrom
FROM OINV
WHERE TRY_CAST(REPLACE(REPLACE(REPLACE(UPPER(ISNULL(U_ReinvoicedFrom, '')), 'INV', ''), ' ', ''), '-', '') AS INT)
      IN ({string.Join(",", chunk)})
");
                while (!rs.EoF)
                {
                    var originalDocNum = TryParseReferencedDocNum(GetString(rs, "U_ReinvoicedFrom"));
                    var candidateDocEntry = GetInt(rs, "DocEntry");
                    var candidateDocNum   = GetInt(rs, "DocNum");
                    var candidateDocDate  = GetDateTime(rs, "DocDate");
                    var candidateCardCode = GetString(rs, "CardCode");
                    var candidateCanceled = NormalizeFlag(GetString(rs, "CANCELED"));

                    if (originalDocNum.HasValue &&
                        evidenceByDocNum.TryGetValue(originalDocNum.Value, out var evidence) &&
                        evidence.DocEntry != candidateDocEntry &&
                        IsReplacementCandidate(candidateCanceled) &&
                        IsSameCustomer(evidence.CardCode, candidateCardCode) &&
                        IsNewerCandidate(evidence, candidateDocDate, candidateDocEntry))
                    {
                        evidence.HasReplacementByReinvoiceReference = true;
                        evidence.ReplacementInvoiceDocNums.Add(candidateDocNum);
                    }

                    rs.MoveNext();
                }
            }
            finally
            {
                if (rs != null) Marshal.ReleaseComObject(rs);
            }
        }
    }

    private void MarkLineageReplacements(
        SAPbobsCOM.Company company,
        IDictionary<int, InvoiceLifecycleEvidence> evidenceByDocEntry)
    {
        var keyOwners = evidenceByDocEntry.Values
            .SelectMany(e => e.UpstreamKeys.Select(key => new { key, evidence = e }))
            .GroupBy(x => x.key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => x.evidence).ToList(),
                StringComparer.OrdinalIgnoreCase);

        if (keyOwners.Count == 0)
            return;

        var deliveryKeys = keyOwners.Keys
            .Where(x => x.StartsWith("D:", StringComparison.OrdinalIgnoreCase))
            .Select(ParseUpstreamKey)
            .Where(x => x != null)
            .Cast<UpstreamKey>()
            .ToList();

        var orderKeys = keyOwners.Keys
            .Where(x => x.StartsWith("O:", StringComparison.OrdinalIgnoreCase))
            .Select(ParseUpstreamKey)
            .Where(x => x != null)
            .Cast<UpstreamKey>()
            .ToList();

        if (deliveryKeys.Count > 0)
        {
            ApplyLineageReplacementQuery(
                company,
                keyOwners,
                deliveryKeys,
                "D",
                @"
SELECT DISTINCT
    Inv.DocEntry AS CandidateDocEntry,
    Inv.DocNum AS CandidateDocNum,
    Inv.DocDate AS CandidateDocDate,
    Inv.CardCode AS CandidateCardCode,
    Inv.CANCELED AS CandidateCanceled,
    Inv1.BaseEntry AS MatchDocEntry,
    Inv1.BaseLine AS MatchLine
FROM OINV Inv
JOIN INV1 Inv1 ON Inv.DocEntry = Inv1.DocEntry AND Inv1.BaseType = 15
JOIN TargetKeys T ON T.SourceDocEntry = Inv1.BaseEntry AND T.SourceLine = Inv1.BaseLine
");
        }

        if (orderKeys.Count > 0)
        {
            ApplyLineageReplacementQuery(
                company,
                keyOwners,
                orderKeys,
                "O",
                @"
SELECT DISTINCT
    Inv.DocEntry AS CandidateDocEntry,
    Inv.DocNum AS CandidateDocNum,
    Inv.DocDate AS CandidateDocDate,
    Inv.CardCode AS CandidateCardCode,
    Inv.CANCELED AS CandidateCanceled,
    Inv1.BaseEntry AS MatchDocEntry,
    Inv1.BaseLine AS MatchLine
FROM OINV Inv
JOIN INV1 Inv1 ON Inv.DocEntry = Inv1.DocEntry AND Inv1.BaseType = 17
JOIN TargetKeys T ON T.SourceDocEntry = Inv1.BaseEntry AND T.SourceLine = Inv1.BaseLine
UNION
SELECT DISTINCT
    Inv.DocEntry AS CandidateDocEntry,
    Inv.DocNum AS CandidateDocNum,
    Inv.DocDate AS CandidateDocDate,
    Inv.CardCode AS CandidateCardCode,
    Inv.CANCELED AS CandidateCanceled,
    D1.BaseEntry AS MatchDocEntry,
    D1.BaseLine AS MatchLine
FROM OINV Inv
JOIN INV1 Inv1 ON Inv.DocEntry = Inv1.DocEntry AND Inv1.BaseType = 15
JOIN DLN1 D1
    ON D1.DocEntry = Inv1.BaseEntry
   AND D1.LineNum = Inv1.BaseLine
   AND D1.BaseType = 17
JOIN TargetKeys T ON T.SourceDocEntry = D1.BaseEntry AND T.SourceLine = D1.BaseLine
");
        }
    }

    private void ApplyLineageReplacementQuery(
        SAPbobsCOM.Company company,
        IReadOnlyDictionary<string, List<InvoiceLifecycleEvidence>> keyOwners,
        IReadOnlyCollection<UpstreamKey> keys,
        string keyType,
        string bodySql)
    {
        foreach (var chunk in keys.Chunk(200))
        {
            var targetKeySql = string.Join(
                " UNION ALL ",
                chunk.Select(x => $"SELECT {x.SourceDocEntry} AS SourceDocEntry, {x.SourceLine} AS SourceLine"));

            SAPbobsCOM.Recordset? rs = null;
            try
            {
                rs = (SAPbobsCOM.Recordset)company.GetBusinessObject(BoObjectTypes.BoRecordset);
                rs.DoQuery($@"
WITH TargetKeys AS (
    {targetKeySql}
)
{bodySql}
");
                while (!rs.EoF)
                {
                    var candidateDocEntry = GetInt(rs, "CandidateDocEntry");
                    var candidateDocNum   = GetInt(rs, "CandidateDocNum");
                    var candidateDocDate  = GetDateTime(rs, "CandidateDocDate");
                    var candidateCardCode = GetString(rs, "CandidateCardCode");
                    var candidateCanceled = NormalizeFlag(GetString(rs, "CandidateCanceled"));
                    var matchDocEntry     = GetInt(rs, "MatchDocEntry");
                    var matchLine         = GetInt(rs, "MatchLine");
                    var matchKey          = BuildUpstreamKey(keyType, matchDocEntry, matchLine);

                    if (!keyOwners.TryGetValue(matchKey, out var owners))
                    {
                        rs.MoveNext();
                        continue;
                    }

                    foreach (var evidence in owners)
                    {
                        if (evidence.DocEntry == candidateDocEntry) continue;
                        if (!IsReplacementCandidate(candidateCanceled)) continue;
                        if (!IsSameCustomer(evidence.CardCode, candidateCardCode)) continue;
                        if (!IsNewerCandidate(evidence, candidateDocDate, candidateDocEntry)) continue;

                        evidence.HasReplacementByLineage = true;
                        evidence.ReplacementInvoiceDocNums.Add(candidateDocNum);
                    }

                    rs.MoveNext();
                }
            }
            finally
            {
                if (rs != null) Marshal.ReleaseComObject(rs);
            }
        }
    }

    private static string ToDocStatusDisplay(InvoiceLifecycleStatus status)
    {
        return status switch
        {
            InvoiceLifecycleStatus.CancellationDocument => "Cancellation",
            InvoiceLifecycleStatus.CanceledInvoice => "Cancelled",
            InvoiceLifecycleStatus.ReplacedOrReinvoiced => "Replaced",
            InvoiceLifecycleStatus.PaidInvoice => "Paid",
            InvoiceLifecycleStatus.PartiallyPaidInvoice => "Partially Paid",
            InvoiceLifecycleStatus.ClosedInvoice => "Closed",
            InvoiceLifecycleStatus.OpenInvoice => "Open",
            _ => "Unknown"
        };
    }

    private static string ToInvoiceStatusText(InvoiceLifecycleStatus status)
    {
        return status switch
        {
            InvoiceLifecycleStatus.CancellationDocument => "Cancellation Document",
            InvoiceLifecycleStatus.CanceledInvoice => "Canceled Invoice",
            InvoiceLifecycleStatus.ReplacedOrReinvoiced => "Replaced / Re-invoiced",
            InvoiceLifecycleStatus.PaidInvoice => "Paid Invoice",
            InvoiceLifecycleStatus.PartiallyPaidInvoice => "Partially Paid Invoice",
            InvoiceLifecycleStatus.ClosedInvoice => "Closed Invoice",
            InvoiceLifecycleStatus.OpenInvoice => "Open Invoice",
            _ => "Unknown"
        };
    }

    private static string BuildPaymentsStatusText(
        InvoiceLifecycleStatus status,
        InvoiceLifecycleEvidence evidence,
        decimal effectivePaid,
        decimal outstanding,
        string replacementNumbers)
    {
        if (status == InvoiceLifecycleStatus.ReplacedOrReinvoiced)
            return string.IsNullOrWhiteSpace(replacementNumbers)
                ? "Replaced / re-invoiced"
                : $"Replaced by invoice(s) {replacementNumbers}";

        if (status == InvoiceLifecycleStatus.CancellationDocument ||
            status == InvoiceLifecycleStatus.CanceledInvoice)
        {
            if (evidence.HasCreditMemo)
                return "Reversed by credit memo / cancellation flow";

            return "No collectible balance";
        }

        if (status == InvoiceLifecycleStatus.PaidInvoice)
            return $"Paid - applied {effectivePaid.ToString("N2", CultureInfo.InvariantCulture)}";

        if (status == InvoiceLifecycleStatus.PartiallyPaidInvoice)
            return $"Partially paid - applied {effectivePaid.ToString("N2", CultureInfo.InvariantCulture)}, outstanding {outstanding.ToString("N2", CultureInfo.InvariantCulture)}";

        if (status == InvoiceLifecycleStatus.ClosedInvoice)
        {
            if (evidence.HasLinkedPayment || effectivePaid > 0m)
                return "Closed with payment evidence";

            return "Closed without linked payment evidence";
        }

        if (status == InvoiceLifecycleStatus.OpenInvoice)
        {
            if (evidence.HasLinkedPayment && outstanding > MoneyTolerance)
                return $"Payment linked but balance still open ({outstanding.ToString("N2", CultureInfo.InvariantCulture)})";

            return "No linked payment";
        }

        return "Unknown";
    }

    private static string BuildCancellationStatusText(
        InvoiceLifecycleStatus status,
        InvoiceLifecycleEvidence evidence,
        string replacementNumbers)
    {
        if (status == InvoiceLifecycleStatus.CancellationDocument)
            return "Cancellation Document";

        if (status == InvoiceLifecycleStatus.CanceledInvoice)
        {
            if (!string.IsNullOrWhiteSpace(replacementNumbers))
                return $"Canceled / replaced by {replacementNumbers}";

            if (evidence.HasCreditMemo)
                return "Canceled with linked credit memo";

            return "Canceled Invoice";
        }

        if (status == InvoiceLifecycleStatus.ReplacedOrReinvoiced)
            return string.IsNullOrWhiteSpace(replacementNumbers)
                ? "Replacement linked"
                : $"Replacement linked: {replacementNumbers}";

        if (evidence.HasCreditMemo)
            return "Credit memo linked";

        return "Not Canceled";
    }

    private static string NormalizeFlag(string? value)
    {
        return (value ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static bool IsReplacementCandidate(string candidateCanceledFlag)
    {
        return candidateCanceledFlag != "Y";
    }

    private static bool IsSameCustomer(string originalCardCode, string candidateCardCode)
    {
        if (string.IsNullOrWhiteSpace(originalCardCode) || string.IsNullOrWhiteSpace(candidateCardCode))
            return true;

        return string.Equals(originalCardCode, candidateCardCode, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNewerCandidate(
        InvoiceLifecycleEvidence original,
        DateTime candidateDocDate,
        int candidateDocEntry)
    {
        return candidateDocDate.Date > original.DocDate.Date ||
               (candidateDocDate.Date == original.DocDate.Date && candidateDocEntry > original.DocEntry);
    }

    private static int? TryParseReferencedDocNum(string rawReference)
    {
        if (string.IsNullOrWhiteSpace(rawReference))
            return null;

        var digits = new string(rawReference.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, out var parsed))
            return parsed;

        return null;
    }

    private static string BuildUpstreamKey(string sourceType, int docEntry, int lineNum)
    {
        return $"{sourceType}:{docEntry}:{lineNum}";
    }

    private static UpstreamKey? ParseUpstreamKey(string value)
    {
        var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
            return null;

        if (!int.TryParse(parts[1], out var docEntry) || !int.TryParse(parts[2], out var line))
            return null;

        return new UpstreamKey(parts[0], docEntry, line);
    }

    private static IEnumerable<List<int>> Chunk(IReadOnlyList<int> values, int size)
    {
        for (var i = 0; i < values.Count; i += size)
            yield return values.Skip(i).Take(size).ToList();
    }

    private static string GetString(SAPbobsCOM.Recordset rs, string fieldName)
    {
        return rs.Fields.Item(fieldName).Value is DBNull
            ? string.Empty
            : Convert.ToString(rs.Fields.Item(fieldName).Value)?.Trim() ?? string.Empty;
    }

    private static int GetInt(SAPbobsCOM.Recordset rs, string fieldName)
    {
        return rs.Fields.Item(fieldName).Value is DBNull
            ? 0
            : Convert.ToInt32(rs.Fields.Item(fieldName).Value);
    }

    private static int? GetNullableInt(SAPbobsCOM.Recordset rs, string fieldName)
    {
        return rs.Fields.Item(fieldName).Value is DBNull
            ? null
            : Convert.ToInt32(rs.Fields.Item(fieldName).Value);
    }

    private static decimal GetDecimal(SAPbobsCOM.Recordset rs, string fieldName)
    {
        return rs.Fields.Item(fieldName).Value is DBNull
            ? 0m
            : Convert.ToDecimal(rs.Fields.Item(fieldName).Value);
    }

    private static DateTime GetDateTime(SAPbobsCOM.Recordset rs, string fieldName)
    {
        return rs.Fields.Item(fieldName).Value is DBNull
            ? DateTime.MinValue
            : Convert.ToDateTime(rs.Fields.Item(fieldName).Value);
    }

    private static DateTime? GetNullableDateTime(SAPbobsCOM.Recordset rs, string fieldName)
    {
        return rs.Fields.Item(fieldName).Value is DBNull
            ? null
            : Convert.ToDateTime(rs.Fields.Item(fieldName).Value);
    }

    private sealed record UpstreamKey(string SourceType, int SourceDocEntry, int SourceLine);
}
