namespace SapReplitAPI.Models
{
    public class PaymentSettings
    {
        public string CashOnHand  { get; set; } = "163000";
        public string MPesaLipa   { get; set; } = "165000";
        public string TigoLipa    { get; set; } = "167000";
        public string CRDB        { get; set; } = "164000";
        public string AALNMB      { get; set; } = "166000";
        // Unallocated Cash (from customers) — used for on-account/advance payments
        public string AdvanceCustomerPayments { get; set; } = "140200";
    }
}
