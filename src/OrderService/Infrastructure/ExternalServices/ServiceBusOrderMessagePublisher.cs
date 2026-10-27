namespace OrderService.Infrastructure.ExternalServices;

public interface IOrderMessagePublisher
{
    Task PublishOrderCreatedAsync(OrderCreatedMessage message, CancellationToken cancellationToken);
}

public sealed record OrderCreatedMessage(
    Guid OrderId,
    Guid CustomerId,
    decimal TotalAmount,
    DateTime CreatedAtUtc,
    IReadOnlyList<OrderCreatedItemMessage> Items);

public sealed record OrderCreatedItemMessage(
    Guid ProductId,
    int Quantity,
    decimal UnitPrice);
