namespace InventoryService.Domain;

public sealed class OrderCreatedReceipt
{
    public string MessageId { get; set; } = "";
    public Guid OrderId { get; set; }
    public Guid CustomerId { get; set; }
    public decimal TotalAmount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ReceivedAtUtc { get; set; }
}
