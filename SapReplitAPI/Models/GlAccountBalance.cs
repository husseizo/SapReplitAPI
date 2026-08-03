namespace SapReplitAPI.Models;

public class GlAccountBalance
{
    public string Account { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;

    /// <summary>
    /// Sum of (Debit - Credit) for all JDT1 entries with RefDate strictly before AsOf.
    /// Positive = debit balance (asset account has money). Negative = credit balance.
    /// </summary>
    public decimal OpeningBalance { get; set; }

    /// <summary>The date the opening balance is calculated up to (exclusive).</summary>
    public DateTime AsOf { get; set; }

    /// <summary>
    /// Earliest RefDate ever found in JDT1 for this account (across all history).
    /// Null if the account has no journal entries at all.
    /// Use this to confirm whether an account existed before the backfill start date.
    /// </summary>
    public DateTime? FirstTransactionDate { get; set; }
}
