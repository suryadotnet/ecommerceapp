using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;

namespace InventoryService.Infrastructure;

public sealed class InventoryOutboxDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<InventoryOutboxDispatcher> _logger;

    public InventoryOutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<InventoryOutboxDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = _configuration.GetConnectionString("ServiceBus");
        var namespaceName = _configuration["ServiceBus:Namespace"];
        if (string.IsNullOrWhiteSpace(connectionString) && string.IsNullOrWhiteSpace(namespaceName))
        {
            _logger.LogWarning("Inventory outbox is disabled because Service Bus settings are missing.");
            return;
        }

        await using var client = string.IsNullOrWhiteSpace(connectionString)
            ? new ServiceBusClient(namespaceName, new DefaultAzureCredential())
            : new ServiceBusClient(connectionString);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dispatched = await DispatchBatchAsync(client, stoppingToken);
                if (dispatched == 0)
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Inventory outbox dispatch batch failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<int> DispatchBatchAsync(ServiceBusClient client, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var messages = await db.OutboxMessages
            .Where(x => x.ProcessedAtUtc == null && x.NextAttemptAtUtc <= DateTime.UtcNow)
            .OrderBy(x => x.CreatedAtUtc)
            .Take(25)
            .ToListAsync(cancellationToken);

        foreach (var item in messages)
        {
            try
            {
                await using var sender = client.CreateSender(item.DestinationName);
                var message = new ServiceBusMessage(BinaryData.FromString(item.Payload))
                {
                    MessageId = item.Id.ToString(),
                    Subject = item.Subject,
                    ContentType = "application/json"
                };
                message.ApplicationProperties["EventType"] = item.Subject;
                await sender.SendMessageAsync(message, cancellationToken);
                item.MarkProcessed(DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var delaySeconds = Math.Min(300, 5 * Math.Pow(2, Math.Min(item.Attempts, 6)));
                item.MarkFailed(DateTime.UtcNow.AddSeconds(delaySeconds), ex.ToString());
                _logger.LogError(ex, "Failed to dispatch inventory event {MessageId}.", item.Id);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return messages.Count;
    }
}
