using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using InventoryService.Domain;
using Microsoft.EntityFrameworkCore;

namespace InventoryService.Infrastructure;

public sealed class ServiceBusConsumer : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string QueueName = "ordersvcqueue";
    private const string TopicName = "ordersvctopic";
    private const string SubscriptionName = "ordersvctopicsubscription";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ServiceBusConsumer> _logger;

    public ServiceBusConsumer(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<ServiceBusConsumer> logger)
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
                "Service Bus consumers are disabled because connection settings are missing.");
            return;
        }

        await using var client = string.IsNullOrWhiteSpace(connectionString)
            ? new ServiceBusClient(namespaceName, new DefaultAzureCredential())
            : new ServiceBusClient(connectionString);
        var processorOptions = new ServiceBusProcessorOptions
        {
            AutoCompleteMessages = false,
            MaxConcurrentCalls = 4
        };
        await using var queueProcessor = client.CreateProcessor(QueueName, processorOptions);
        await using var topicProcessor = client.CreateProcessor(TopicName, SubscriptionName, processorOptions);

        queueProcessor.ProcessMessageAsync += ProcessOrderCreatedAsync;
        topicProcessor.ProcessMessageAsync += ProcessPaymentInfoAsync;
        queueProcessor.ProcessErrorAsync += ProcessErrorAsync;
        topicProcessor.ProcessErrorAsync += ProcessErrorAsync;

        try
        {
            await queueProcessor.StartProcessingAsync(stoppingToken);
            await topicProcessor.StartProcessingAsync(stoppingToken);
            _logger.LogInformation(
                "Consuming OrderCreated messages from queue {QueueName} and PaymentInfo messages from {TopicName}/{SubscriptionName}.",
                QueueName, TopicName, SubscriptionName);
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await queueProcessor.StopProcessingAsync(CancellationToken.None);
            await topicProcessor.StopProcessingAsync(CancellationToken.None);
        }
    }

    private Task ProcessOrderCreatedAsync(ProcessMessageEventArgs args) =>
        ProcessMessageAsync(
            args,
            "OrderCreated",
            async (messageId, payload, cancellationToken) =>
            {
                var order = JsonSerializer.Deserialize<OrderCreatedEvent>(payload, JsonOptions);
                if (order is null || order.OrderId == Guid.Empty || order.CustomerId == Guid.Empty)
                    throw new JsonException("OrderCreated message is missing required identifiers.");

                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
                if (await db.OrderCreatedReceipts.AnyAsync(x => x.MessageId == messageId, cancellationToken))
                {
                    _logger.LogInformation("Ignoring duplicate OrderCreated message {MessageId}.", messageId);
                    return;
                }

                db.OrderCreatedReceipts.Add(new OrderCreatedReceipt
                {
                    MessageId = messageId,
                    OrderId = order.OrderId,
                    CustomerId = order.CustomerId,
                    TotalAmount = order.TotalAmount,
                    CreatedAtUtc = order.CreatedAtUtc,
                    ReceivedAtUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Recorded OrderCreated event for order {OrderId}.", order.OrderId);
            });

    private Task ProcessPaymentInfoAsync(ProcessMessageEventArgs args) =>
        ProcessMessageAsync(
            args,
            "PaymentInfo",
            async (messageId, payload, cancellationToken) =>
            {
                var payment = JsonSerializer.Deserialize<PaymentInfoEvent>(payload, JsonOptions);
                if (payment is null || payment.OrderId == Guid.Empty || payment.CustomerId == Guid.Empty
                    || string.IsNullOrWhiteSpace(payment.Status))
                    throw new JsonException("PaymentInfo message is missing required values.");

                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
                if (await db.PaymentInfoReceipts.AnyAsync(x => x.MessageId == messageId, cancellationToken))
                {
                    _logger.LogInformation("Ignoring duplicate PaymentInfo message {MessageId}.", messageId);
                    return;
                }

                db.PaymentInfoReceipts.Add(new PaymentInfoReceipt
                {
                    MessageId = messageId,
                    OrderId = payment.OrderId,
                    CustomerId = payment.CustomerId,
                    Amount = payment.Amount,
                    Status = payment.Status,
                    ProcessedAtUtc = payment.ProcessedAtUtc,
                    ReceivedAtUtc = DateTime.UtcNow
                });
                await db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation(
                    "Recorded payment outcome {Status} for order {OrderId}.", payment.Status, payment.OrderId);
            });

    private async Task ProcessMessageAsync(
        ProcessMessageEventArgs args,
        string expectedSubject,
        Func<string, string, CancellationToken, Task> persist)
    {
        if (!string.Equals(args.Message.Subject, expectedSubject, StringComparison.Ordinal))
        {
            await args.DeadLetterMessageAsync(
                args.Message,
                "UnexpectedSubject",
                $"Expected {expectedSubject}, received {args.Message.Subject ?? "(none)"}.",
                args.CancellationToken);
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(args.Message.MessageId))
                throw new JsonException("Service Bus message is missing its message ID.");

            await persist(
                args.Message.MessageId,
                args.Message.Body.ToString(),
                args.CancellationToken);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Dead-lettering malformed {Subject} message {MessageId}.",
                expectedSubject, args.Message.MessageId);
            await args.DeadLetterMessageAsync(args.Message, "InvalidPayload", ex.Message, args.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !args.CancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Failed to process {Subject} message {MessageId}; abandoning for retry.",
                expectedSubject, args.Message.MessageId);
            await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
        }
    }

    private Task ProcessErrorAsync(ProcessErrorEventArgs args)
    {
        _logger.LogError(args.Exception,
            "Service Bus processor error on {EntityPath} ({ErrorSource}).",
            args.EntityPath, args.ErrorSource);
        return Task.CompletedTask;
    }

    private sealed record OrderCreatedEvent(
        Guid OrderId,
        Guid CustomerId,
        decimal TotalAmount,
        DateTime CreatedAtUtc);

    private sealed record PaymentInfoEvent(
        Guid OrderId,
        Guid CustomerId,
        decimal Amount,
        string Status,
        DateTime ProcessedAtUtc);
}
