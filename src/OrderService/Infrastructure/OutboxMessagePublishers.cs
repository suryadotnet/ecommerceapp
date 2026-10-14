using System.Text.Json;
using OrderService.Infrastructure.ExternalServices;

namespace OrderService.Infrastructure;

public sealed class OutboxOrderMessagePublisher : IOrderMessagePublisher
{
    private const string QueueName = "ordersvcqueue";
    private readonly OrderDbContext _db;

    public OutboxOrderMessagePublisher(OrderDbContext db)
    {
        _db = db;
    }

    public Task PublishOrderCreatedAsync(OrderCreatedMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _db.OutboxMessages.Add(OutboxMessage.Create(
            "Queue",
            QueueName,
            "OrderCreated",
            JsonSerializer.Serialize(message)));
        return Task.CompletedTask;
    }
}

public sealed class OutboxPaymentMessagePublisher : IPaymentMessagePublisher
{
    private const string TopicName = "ordersvctopic";
    private readonly OrderDbContext _db;

    public OutboxPaymentMessagePublisher(OrderDbContext db)
    {
        _db = db;
    }

    public Task PublishPaymentInfoAsync(PaymentInfoMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _db.OutboxMessages.Add(OutboxMessage.Create(
            "Topic",
            TopicName,
            "PaymentInfo",
            JsonSerializer.Serialize(message)));
        return Task.CompletedTask;
    }
}
