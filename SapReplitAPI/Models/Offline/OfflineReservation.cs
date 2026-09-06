namespace SapReplitAPI.Models.Offline;

/// <summary>
/// Offline reservation ledger entry.
///
/// Problem this solves:
///   During SAP/network outage the Neon mirror shows available stock.
///   Without reservations, two concurrent offline orders could both "see"
///   the same 2 units available and both commit to consuming them.
///
/// Design:
///   OfflineOperationalAvailable = MirroredAvailable
///                               - SUM(ActiveOfflineReservations for that item/whs/bin)
///
/// Lifecycle:
///   Reserved → PickConfirmed → Recovering → AppliedToSAP → Completed
///   Reserved → Cancelled → Released (stock available again)
/// </summary>
public class OfflineReservation
{
    public int      Id                       { get; set; }
    public int      OfflineFulfillmentOrderId { get; set; }

    public string   ItemCode                 { get; set; } = "";
    public string   WhsCode                  { get; set; } = "";

    /// <summary>Null for non-bin-managed warehouses.</summary>
    public int?     BinAbsEntry              { get; set; }

    public decimal  ReservedQty              { get; set; }

    /// <summary>
    /// Reserved | PickConfirmed | Recovering | AppliedToSAP | Released | Cancelled
    /// </summary>
    public string   State                    { get; set; } = OfflineReservationState.Reserved;

    public DateTime CreatedAtUtc             { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc             { get; set; } = DateTime.UtcNow;

    public OfflineFulfillmentOrder? Order    { get; set; }
}

public static class OfflineReservationState
{
    public const string Reserved      = "Reserved";
    public const string PickConfirmed = "PickConfirmed";
    public const string Recovering    = "Recovering";
    public const string AppliedToSAP  = "AppliedToSAP";
    public const string Released      = "Released";
    public const string Cancelled     = "Cancelled";
}
