namespace SapReplitAPI.Models.Offline;

/// <summary>
/// Physical fulfillment truth — what was ACTUALLY picked and from which location.
/// This record becomes immutable after OfflinePickConfirmed state is set.
///
/// CRITICAL: After Offline Confirm Pick, the SAP recovery process MUST use these
/// exact WhsCode/BinAbsEntry/PickedQty values. It must NOT silently reallocate
/// to a different warehouse or bin. Any mismatch → ReconciliationRequired.
///
/// One OfflineFulfillmentOrder can have many picks (one per item/bin combination).
/// One item may span multiple bins — record one row per bin per item.
/// </summary>
public class OfflineFulfillmentPick
{
    public int      Id                       { get; set; }
    public int      OfflineFulfillmentOrderId { get; set; }

    /// <summary>Links this pick to the original order line intent.</summary>
    public Guid     RequestedLineId          { get; set; }

    // ── Physical truth ────────────────────────────────────────
    public string   ItemCode                 { get; set; } = "";
    public decimal  RequestedQty             { get; set; }
    public decimal  PickedQty                { get; set; }

    /// <summary>Actual warehouse from which items were physically removed.</summary>
    public string   WhsCode                  { get; set; } = "";

    /// <summary>SAP OBIN.AbsEntry. Null for non-bin-managed warehouses.</summary>
    public int?     BinAbsEntry              { get; set; }

    /// <summary>Human-readable bin label (e.g. BIN-A-001). Null for non-bin warehouses.</summary>
    public string?  BinCode                  { get; set; }

    // ── Picker identity ───────────────────────────────────────
    /// <summary>Display reference for the human who performed the pick.</summary>
    public string   PickerReference          { get; set; } = "";

    // ── Confirmation ──────────────────────────────────────────
    /// <summary>Set when the picker first touches the record.</summary>
    public DateTime? PickedAtUtc             { get; set; }

    /// <summary>Set when OFFLINE CONFIRM PICK is pressed. Immutable after this.</summary>
    public DateTime? ConfirmedAtUtc          { get; set; }

    /// <summary>Unique confirmation identifier — generated at confirm time.</summary>
    public Guid?    OfflineConfirmId         { get; set; }

    /// <summary>True after Offline Confirm Pick. Cannot be edited by normal users.</summary>
    public bool     IsConfirmed              { get; set; }

    public OfflineFulfillmentOrder? Order    { get; set; }
}
