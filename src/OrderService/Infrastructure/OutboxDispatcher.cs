using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;

namespace OrderService.Infrastructure;

public sealed class OutboxDispatcher : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<OutboxDispatcher> logger)
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
            _logger.LogWarning(
                "Outbox dispatch is disabled because Service Bus connection settings are missing. Outbox messages will remain pending.");
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
                _logger.LogError(ex, "Outbox dispatch batch failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task<int> DispatchBatchAsync(ServiceBusClient client, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var now = DateTime.UtcNow;
        var messages = await db.OutboxMessages
            .Where(message => message.ProcessedAtUtc == null && message.NextAttemptAtUtc <= now)
            .OrderBy(message => message.CreatedAtUtc)
            .Take(25)
            .ToListAsync(cancellationToken);

        foreach (var outboxMessage in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var sender = client.CreateSender(outboxMessage.DestinationName);
                var message = new ServiceBusMessage(BinaryData.FromString(outboxMessage.Payload))
                {
                    MessageId = outboxMessage.Id.ToString(),
                    Subject = outboxMessage.Subject,
                    ContentType = "application/json"
                };
                message.ApplicationProperties["EventType"] = outboxMessage.Subject;

                await sender.SendMessageAsync(message, cancellationToken);
                outboxMessage.MarkProcessed(DateTime.UtcNow);
                _logger.LogInformation(
                    "Dispatched outbox message {MessageId} ({Subject}) to {Destination}.",
                    outboxMessage.Id, outboxMessage.Subject, outboxMessage.DestinationName);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var attempts = outboxMessage.Attempts;
                var retrySeconds = Math.Min(300, 5 * Math.Pow(2, Math.Min(attempts, 6)));
                outboxMessage.MarkFailed(
                    DateTime.UtcNow.AddSeconds(retrySeconds),
                    ex.ToString());
                _logger.LogError(
                    ex,
                    "Failed to dispatch outbox message {MessageId} to {Destination}; retry scheduled in {RetrySeconds} seconds.",
                    outboxMessage.Id, outboxMessage.DestinationName, retrySeconds);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return messages.Count;
    }
}
