namespace SapReplitAPI.Models.Cache;

public class PaymentIdempotencyLog
{
    public int    Id               { get; set; }
    public string ClientReference  { get; set; } = string.Empty;
    public int    PaymentDocEntry  { get; set; }
    public int    PaymentDocNum    { get; set; }
    public DateTime CreatedAt      { get; set; }
}
