using System.Data;
using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using PaymentService.Domain;

namespace PaymentService.Infrastructure;

public sealed class PaymentSagaConsumer : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<PaymentSagaConsumer> _logger;

    public PaymentSagaConsumer(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<PaymentSagaConsumer> logger)
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
            _logger.LogWarning("Payment saga consumer is disabled because Service Bus settings are missing.");
            return;
        }

        await using var client = string.IsNullOrWhiteSpace(connectionString)
            ? new ServiceBusClient(namespaceName, new DefaultAzureCredential())
            : new ServiceBusClient(connectionString);
        await using var processor = client.CreateProcessor(
            _configuration["ServiceBus:Topic"] ?? "ordersvctopic",
            _configuration["ServiceBus:PaymentSubscription"] ?? "paymentsvcsubscription",
            new ServiceBusProcessorOptions { AutoCompleteMessages = false, MaxConcurrentCalls = 1 });
        processor.ProcessMessageAsync += ProcessMessageAsync;
        processor.ProcessErrorAsync += args =>
        {
            _logger.LogError(args.Exception, "Payment saga processor error on {EntityPath}.", args.EntityPath);
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
        if (!string.Equals(eventType, "InventoryReserved", StringComparison.Ordinal) ||
            !string.Equals(eventType, args.Message.Subject, StringComparison.Ordinal))
        {
            await args.DeadLetterMessageAsync(
                args.Message,
                "InvalidEventType",
                "Payment Service expected EventType and subject InventoryReserved.",
                args.CancellationToken);
            return;
        }

        try
        {
            await ProcessReservationAsync(args);
            await args.CompleteMessageAsync(args.Message, args.CancellationToken);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Dead-lettering malformed InventoryReserved message {MessageId}.",
                args.Message.MessageId);
            await args.DeadLetterMessageAsync(
                args.Message, "InvalidPayload", ex.Message, args.CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !args.CancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Failed to process InventoryReserved message {MessageId}; abandoning for retry.",
                args.Message.MessageId);
            await args.AbandonMessageAsync(args.Message, cancellationToken: args.CancellationToken);
        }
    }

    private async Task ProcessReservationAsync(ProcessMessageEventArgs args)
    {
        var reserved = JsonSerializer.Deserialize<InventoryReservedEvent>(
            args.Message.Body.ToString(), JsonOptions);
        if (reserved is null || reserved.OrderId == Guid.Empty || reserved.CustomerId == Guid.Empty ||
            reserved.Amount < 0 || reserved.ReservationId == Guid.Empty)
            throw new JsonException("InventoryReserved event is missing valid payment details.");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, args.CancellationToken);

        if (await db.EventReceipts.AnyAsync(
                x => x.MessageId == args.Message.MessageId, args.CancellationToken))
            return;

        var payment = await db.Payments.SingleOrDefaultAsync(
            x => x.OrderId == reserved.OrderId, args.CancellationToken);
        if (payment is null)
        {
            payment = new Payment
            {
                Id = Guid.NewGuid(),
                OrderId = reserved.OrderId,
                CustomerId = reserved.CustomerId,
                Amount = reserved.Amount,
                CreatedAtUtc = DateTime.UtcNow,
                Status = reserved.Amount >= 99999 ? PaymentStatus.Failed : PaymentStatus.Processed,
                FailureReason = reserved.Amount >= 99999
                    ? "Demo payment declined because the amount is at or above the failure threshold."
                    : null
            };
            db.Payments.Add(payment);
        }

        var succeeded = payment.Status == PaymentStatus.Processed;
        var eventType = succeeded ? "PaymentSucceeded" : "PaymentFailed";
        var paymentEvent = new PaymentOutcomeEvent(
            eventType,
            payment.OrderId,
            payment.CustomerId,
            payment.Id,
            payment.Amount,
            payment.FailureReason,
            DateTime.UtcNow);
        db.EventReceipts.Add(new PaymentEventReceipt
        {
            MessageId = args.Message.MessageId,
            ProcessedAtUtc = DateTime.UtcNow
        });
        db.OutboxMessages.Add(PaymentOutboxMessage.Create(
            _configuration["ServiceBus:Topic"] ?? "ordersvctopic",
            eventType,
            JsonSerializer.Serialize(paymentEvent)));

        await db.SaveChangesAsync(args.CancellationToken);
        await transaction.CommitAsync(args.CancellationToken);
        _logger.LogInformation("Payment {Status} for order {OrderId}.", eventType, payment.OrderId);
    }

    private sealed record InventoryReservedEvent(
        string EventType,
        Guid OrderId,
        Guid CustomerId,
        decimal Amount,
        Guid ReservationId);

    private sealed record PaymentOutcomeEvent(
        string EventType,
        Guid OrderId,
        Guid CustomerId,
        Guid PaymentId,
        decimal Amount,
        string? FailureReason,
        DateTime OccurredAtUtc);
}
