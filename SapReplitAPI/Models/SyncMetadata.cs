public class SyncMetadata
{
    public int Id { get; set; }
    public string Type { get; set; } = "";  // e.g. "Invoice", "Payment"
    public DateTime LastSyncedAt { get; set; }
}