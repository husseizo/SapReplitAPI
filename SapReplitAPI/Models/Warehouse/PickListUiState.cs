namespace SapReplitAPI.Models.Warehouse;

/// <summary>
/// Derived UI state for a pick list line displayed in the Warehouse App.
/// Computed from SAP status + cached bin allocation count + live OIBQ candidate count.
/// </summary>
public enum PickListUiState
{
    /// <summary>R, bins=0, OIBQ candidates available — picker must select source bin.</summary>
    AwaitingBinSelection,

    /// <summary>R, bins=0, no OIBQ stock found in this warehouse.</summary>
    NoStock,

    /// <summary>R, bins>0 — SAP PKL2 has pre-allocated bins, proceed to pick.</summary>
    ReadyAllocated,

    /// <summary>R, bins>0 but allocated bin no longer has sufficient stock.</summary>
    BinAllocationException,

    /// <summary>Status=P — partially picked.</summary>
    PartiallyPicked,

    /// <summary>Status=Y — fully picked.</summary>
    Picked,

    /// <summary>Status=D — partially delivered.</summary>
    PartiallyDelivered,

    /// <summary>Status=C or Canceled=Y — closed.</summary>
    Closed,
}
