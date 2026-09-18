using SapReplitAPI.Models.Warehouse;

namespace SapReplitAPI.Services.Warehouse;

/// <summary>
/// Pure static logic: derives the warehouse-app UI state from cache + live OIBQ data.
/// No SAP calls. No DI. Fully unit-testable.
/// </summary>
public static class PickListUiStateComputer
{
    /// <summary>
    /// Compute the UI state for a pick list.
    /// </summary>
    /// <param name="sapStatus">Raw OPKL.Status value ("R","Y","P","D","C").</param>
    /// <param name="canceled">Raw OPKL.Canceled value ("Y"/"N").</param>
    /// <param name="allocatedBinCount">Number of PKL2 rows (cached). 0 = no pre-allocated bin.</param>
    /// <param name="liveCandidateCount">
    ///   Total OIBQ-eligible bins across all lines (from a live query).
    ///   Pass -1 when the live query was skipped (e.g. bins already allocated).
    /// </param>
    public static PickListUiState Compute(
        string sapStatus,
        string canceled,
        int    allocatedBinCount,
        int    liveCandidateCount)
    {
        if (canceled == "Y" || sapStatus == "C") return PickListUiState.Closed;

        return sapStatus switch
        {
            "Y" => PickListUiState.Picked,
            "P" => PickListUiState.PartiallyPicked,
            "D" => PickListUiState.PartiallyDelivered,
            "R" when allocatedBinCount > 0 => PickListUiState.ReadyAllocated,
            "R" when liveCandidateCount > 0 => PickListUiState.AwaitingBinSelection,
            "R" => PickListUiState.NoStock,
            _   => PickListUiState.Closed,
        };
    }

    public static string ToLabel(PickListUiState state) => state switch
    {
        PickListUiState.AwaitingBinSelection   => "Ready to Pick",
        PickListUiState.NoStock                => "No Stock in Bins",
        PickListUiState.ReadyAllocated         => "Ready to Pick",
        PickListUiState.BinAllocationException => "Bin Allocation Exception",
        PickListUiState.PartiallyPicked        => "Partially Picked",
        PickListUiState.Picked                 => "Picked",
        PickListUiState.PartiallyDelivered     => "Partially Delivered",
        PickListUiState.Closed                 => "Closed",
        _                                      => "Unknown",
    };

    public static string ToAction(PickListUiState state) => state switch
    {
        PickListUiState.AwaitingBinSelection   => "Select Bin & Pick",
        PickListUiState.ReadyAllocated         => "Confirm Pick",
        PickListUiState.BinAllocationException => "Resolve Exception",
        _                                      => string.Empty,
    };

    public static string ToSupportingText(PickListUiState state) => state switch
    {
        PickListUiState.AwaitingBinSelection =>
            "Choose the bin where the item will be physically picked.",
        PickListUiState.NoStock =>
            "No stock found in bins for this warehouse.",
        PickListUiState.ReadyAllocated =>
            "Bin allocation is ready. Confirm the pick.",
        PickListUiState.BinAllocationException =>
            "The allocated bin no longer has sufficient stock.",
        _ => string.Empty,
    };
}
