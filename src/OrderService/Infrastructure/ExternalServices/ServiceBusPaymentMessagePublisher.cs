namespace OrderService.Infrastructure.ExternalServices;

public interface IPaymentMessagePublisher
{
    Task PublishPaymentInfoAsync(PaymentInfoMessage message, CancellationToken cancellationToken);
}

public sealed record PaymentInfoMessage(
    Guid OrderId,
    Guid CustomerId,
    decimal Amount,
    string Status,
    DateTime ProcessedAtUtc);
