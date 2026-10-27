using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using OrderService.Domain.Enums;

namespace OrderService.Infrastructure;

public sealed class OrderSagaConsumer : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OrderSagaConsumer> _logger;

    public OrderSagaConsumer(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<OrderSagaConsumer> logger)
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
            _logger.LogWarning("Order saga consumer is disabled because Service Bus settings are missing.");
            return;
        }

        await using var client = string.IsNullOrWhiteSpace(connectionString)
            ? new ServiceBusClient(namespaceName, new DefaultAzureCredential())
            : new ServiceBusClient(connectionString);
        await using var processor = client.CreateProcessor(
            _configuration["ServiceBus:Topic"] ?? "ordersvctopic",
            _configuration["ServiceBus:OrderSubscription"] ?? "ordersvctopicsubscription",
            new ServiceBusProcessorOptions { AutoCompleteMessages = false, MaxConcurrentCalls = 4 });

        processor.ProcessMessageAsync += ProcessMessageAsync;
        processor.ProcessErrorAsync += args =>
        {
            _logger.LogError(args.Exception, "Order saga processor error on {EntityPath}.", args.EntityPath);
            return Task.CompletedTask;
        };

        try
        {
            await processor.StartProcessingAsync(stoppingToken);
            _logger.LogInformation("Consuming saga events from {EntityPath}.", processor.Identifier);
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await processor.StopProcessingAsync(CancellationToken.None);
        }
    }

    private async Task ProcessMessageAsync(ProcessMessageEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Message.MessageId))
        {
            await args.DeadLetterMessageAsync(
                args.Message, "MissingMessageId", "Saga events must have a Service Bus message ID.",
                args.CancellationToken);
            return;
        }

        var eventType = args.Message.ApplicationProperties.TryGetValue("EventType", out var value)
            ? value as string
            : null;
        if (string.IsNullOrWhiteSpace(eventType) ||
            !string.Equals(eventType, args.Message.Subject, StringComparison.Ordinal))
        {
            await args.DeadLetterMessageAsync(
                args.Message,
                "InvalidEventType",
                "The EventType application property must match the message subject.",
                args.CancellationToken);
            return;
        }

        try
        {
            switch (eventType)
            {
                case "PaymentSucceeded":
                    var succeeded = Deserialize(args.Message.Body.ToString());
                    await UpdateOrderAsync(succeeded.OrderId, succeeded.EventType, args.CancellationToken);
                    break;
                case "PaymentFailed":
                    var failed = Deserialize(args.Message.Body.ToString());
                    await UpdateOrderAsync(failed.OrderId, failed.EventType, args.CancellationToken);
                    break;
                case "InventoryReleased":
                    var released = Deserialize(args.Message.Body.ToString());
                    _logger.LogInformation(
                        "Inventory compensation completed for order {OrderId}.", released.OrderId);
                    break;
                case "OrderCreated":
                case "InventoryReserved":
                    _logger.LogInformation(
                        "Ignoring {EventType} delivered to the Order Service subscription.", eventType);
                    break;
                default:
                    await args.DeadLetterMessageAsync(
                        args.Message, "UnsupportedEventType", $"Unsupported event type {eventType}.",
                        args.CancellationToken);
                    return;
            }

            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Dead-lettering malformed {EventType} message {MessageId}.",
                eventType, args.Message.MessageId);
            await args.DeadLetterMessageAsync(
                args.Message, "InvalidPayload", ex.Message, args.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !args.CancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Failed to process {EventType} message {MessageId}; abandoning for retry.",
                eventType, args.Message.MessageId);
            await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
        }
    }

    private static SagaEvent Deserialize(string payload)
    {
        var message = JsonSerializer.Deserialize<SagaEvent>(payload, JsonOptions);
        if (message is null || message.OrderId == Guid.Empty)
            throw new JsonException("Saga event must contain a valid orderId.");
        return message;
    }

    private async Task UpdateOrderAsync(Guid orderId, string eventType, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();
        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == orderId, cancellationToken)
            ?? throw new InvalidOperationException($"Order {orderId} was not found for {eventType}.");

        if (order.Status != OrderStatus.Pending)
        {
            _logger.LogInformation(
                "Ignoring {EventType} for order {OrderId} in status {Status}.",
                eventType, orderId, order.Status);
            return;
        }

        if (eventType == "PaymentSucceeded")
            order.MarkPaymentSucceeded();
        else
            order.MarkPaymentFailed();

        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Order {OrderId} moved to {Status} after {EventType}.",
            orderId, order.Status, eventType);
    }

    private sealed record SagaEvent(Guid OrderId, string EventType);
}
