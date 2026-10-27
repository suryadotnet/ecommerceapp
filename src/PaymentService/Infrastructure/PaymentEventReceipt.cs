namespace PaymentService.Infrastructure;

public sealed class PaymentEventReceipt
{
    public string MessageId { get; set; } = "";
    public DateTime ProcessedAtUtc { get; set; }
}
