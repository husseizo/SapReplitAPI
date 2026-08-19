namespace SapReplitAPI.Models.SoDelivery;

/// <summary>
/// Represents one open Sales Order header returned from ORDR.
/// Populated by SapService.GetOpenSosForDate().
/// </summary>
public class OpenSoDto
{
    public int      DocEntry    { get; set; }
    public int      DocNum      { get; set; }
    public string   CardCode    { get; set; } = "";
    public string   CardName    { get; set; } = "";
    public DateTime DocDate     { get; set; }
    public DateTime? DocDueDate { get; set; }
    public string   DocCurrency { get; set; } = "TZS";
    public decimal  DocTotal    { get; set; }
}
