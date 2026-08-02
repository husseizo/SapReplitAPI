namespace SapReplitAPI.Models.Payments
{
    public class IncomingPaymentResultDto
    {
        public bool Success { get; set; }
        public int PaymentDocEntry { get; set; }
        public int PaymentDocNum { get; set; }
        public string? ErrorMessage { get; set; }
        public int ErrorCode { get; set; }
    }
}
