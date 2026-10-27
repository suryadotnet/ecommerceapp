namespace InventoryService.Infrastructure;

public sealed class InventoryEventReceipt
{
    public string MessageId { get; set; } = "";
    public DateTime ProcessedAtUtc { get; set; }
}
