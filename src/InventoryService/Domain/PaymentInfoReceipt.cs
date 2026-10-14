namespace InventoryService.Domain;

public sealed class PaymentInfoReceipt
{
    public string MessageId { get; set; } = "";
    public Guid OrderId { get; set; }
    public Guid CustomerId { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = "";
    public DateTime ProcessedAtUtc { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
}
