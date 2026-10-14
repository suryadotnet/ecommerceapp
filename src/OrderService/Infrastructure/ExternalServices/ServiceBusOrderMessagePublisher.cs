namespace OrderService.Infrastructure.ExternalServices;

public interface IOrderMessagePublisher
{
    Task PublishOrderCreatedAsync(OrderCreatedMessage message, CancellationToken cancellationToken);
}

public sealed record OrderCreatedMessage(
    Guid OrderId,
    Guid CustomerId,
    decimal TotalAmount,
    DateTime CreatedAtUtc);
