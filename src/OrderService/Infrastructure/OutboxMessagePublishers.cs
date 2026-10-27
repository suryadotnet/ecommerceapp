using System.Text.Json;
using OrderService.Infrastructure.ExternalServices;

namespace OrderService.Infrastructure;

public sealed class OutboxOrderMessagePublisher : IOrderMessagePublisher
{
    private const string TopicName = "ordersvctopic";
    private readonly OrderDbContext _db;

    public OutboxOrderMessagePublisher(OrderDbContext db)
    {
        _db = db;
    }

    public Task PublishOrderCreatedAsync(OrderCreatedMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _db.OutboxMessages.Add(OutboxMessage.Create(
            "Topic",
            TopicName,
            "OrderCreated",
            JsonSerializer.Serialize(message)));
        return Task.CompletedTask;
    }
}
