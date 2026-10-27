using System.Data;
using System.Text.Json;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using InventoryService.Domain;
using Microsoft.EntityFrameworkCore;

namespace InventoryService.Infrastructure;

public sealed class ServiceBusConsumer : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
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
            _logger.LogWarning("Inventory saga consumer is disabled because Service Bus settings are missing.");
            return;
        }

        await using var client = string.IsNullOrWhiteSpace(connectionString)
            ? new ServiceBusClient(namespaceName, new DefaultAzureCredential())
            : new ServiceBusClient(connectionString);
        await using var processor = client.CreateProcessor(
            _configuration["ServiceBus:Topic"] ?? "ordersvctopic",
            _configuration["ServiceBus:InventorySubscription"] ?? "inventorysvcsubscription",
            new ServiceBusProcessorOptions { AutoCompleteMessages = false, MaxConcurrentCalls = 1 });
        processor.ProcessMessageAsync += ProcessMessageAsync;
        processor.ProcessErrorAsync += args =>
        {
            _logger.LogError(args.Exception, "Inventory saga processor error on {EntityPath}.", args.EntityPath);
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
                case "OrderCreated":
                    await ReserveInventoryAsync(args);
                    break;
                case "PaymentFailed":
                    await ReleaseInventoryAsync(args);
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

    private async Task ReserveInventoryAsync(ProcessMessageEventArgs args)
    {
        var order = JsonSerializer.Deserialize<OrderCreatedEvent>(args.Message.Body.ToString(), JsonOptions);
        if (order is null || order.OrderId == Guid.Empty || order.CustomerId == Guid.Empty ||
            order.Items is null || order.Items.Count == 0 ||
            order.Items.Any(item => item.ProductId == Guid.Empty || item.Quantity <= 0 || item.UnitPrice < 0))
            throw new JsonException("OrderCreated event is missing valid order or item data.");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, args.CancellationToken);

        if (await db.EventReceipts.AnyAsync(x => x.MessageId == args.Message.MessageId, args.CancellationToken))
            return;

        if (await db.InventoryReservations.AnyAsync(x => x.OrderId == order.OrderId, args.CancellationToken))
        {
            db.EventReceipts.Add(new InventoryEventReceipt
            {
                MessageId = args.Message.MessageId,
                ProcessedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync(args.CancellationToken);
            await transaction.CommitAsync(args.CancellationToken);
            _logger.LogInformation("Ignoring duplicate OrderCreated event for order {OrderId}.", order.OrderId);
            return;
        }

        var groupedItems = order.Items
            .GroupBy(item => item.ProductId)
            .Select(group => new ReservedItem(group.Key, group.Sum(item => item.Quantity)))
            .ToList();
        var reservationId = Guid.NewGuid();
        foreach (var item in groupedItems)
        {
            var product = await db.Products.SingleOrDefaultAsync(
                x => x.ProductId == item.ProductId, args.CancellationToken);
            if (product is null || product.AvailableQuantity < item.Quantity)
                throw new InvalidOperationException(
                    $"Insufficient inventory for product {item.ProductId} on order {order.OrderId}.");

            product.AvailableQuantity -= item.Quantity;
            product.ReservedQuantity += item.Quantity;
            db.InventoryReservations.Add(new InventoryReservation
            {
                ReservationId = reservationId,
                OrderId = order.OrderId,
                ProductId = item.ProductId,
                Quantity = item.Quantity,
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        db.EventReceipts.Add(new InventoryEventReceipt
        {
            MessageId = args.Message.MessageId,
            ProcessedAtUtc = DateTime.UtcNow
        });
        var reservedEvent = new InventoryReservedEvent(
            "InventoryReserved",
            order.OrderId,
            order.CustomerId,
            order.TotalAmount,
            reservationId,
            groupedItems,
            DateTime.UtcNow);
        db.OutboxMessages.Add(InventoryOutboxMessage.Create(
            _configuration["ServiceBus:Topic"] ?? "ordersvctopic",
            "InventoryReserved",
            JsonSerializer.Serialize(reservedEvent)));

        await db.SaveChangesAsync(args.CancellationToken);
        await transaction.CommitAsync(args.CancellationToken);
        _logger.LogInformation("Reserved inventory {ReservationId} for order {OrderId}.",
            reservationId, order.OrderId);
    }

    private async Task ReleaseInventoryAsync(ProcessMessageEventArgs args)
    {
        var payment = JsonSerializer.Deserialize<PaymentFailedEvent>(args.Message.Body.ToString(), JsonOptions);
        if (payment is null || payment.OrderId == Guid.Empty)
            throw new JsonException("PaymentFailed event must contain a valid orderId.");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, args.CancellationToken);

        if (await db.EventReceipts.AnyAsync(x => x.MessageId == args.Message.MessageId, args.CancellationToken))
            return;

        var orderReservations = await db.InventoryReservations
            .Where(x => x.OrderId == payment.OrderId)
            .ToListAsync(args.CancellationToken);
        var reservations = orderReservations.Where(x => !x.IsReleased).ToList();
        if (reservations.Count == 0)
        {
            if (orderReservations.Count == 0)
                throw new InvalidOperationException(
                    $"No inventory reservation exists for failed order {payment.OrderId}.");

            db.EventReceipts.Add(new InventoryEventReceipt
            {
                MessageId = args.Message.MessageId,
                ProcessedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync(args.CancellationToken);
            await transaction.CommitAsync(args.CancellationToken);
            _logger.LogInformation(
                "Inventory was already released for failed order {OrderId}.", payment.OrderId);
            return;
        }

        foreach (var reservation in reservations)
        {
            var product = await db.Products.SingleOrDefaultAsync(
                x => x.ProductId == reservation.ProductId, args.CancellationToken)
                ?? throw new InvalidOperationException(
                    $"Reserved product {reservation.ProductId} no longer exists.");
            if (product.ReservedQuantity < reservation.Quantity)
                throw new InvalidOperationException(
                    $"Reserved stock for product {reservation.ProductId} is inconsistent.");

            product.ReservedQuantity -= reservation.Quantity;
            product.AvailableQuantity += reservation.Quantity;
            reservation.IsReleased = true;
        }

        db.EventReceipts.Add(new InventoryEventReceipt
        {
            MessageId = args.Message.MessageId,
            ProcessedAtUtc = DateTime.UtcNow
        });
        var releasedEvent = new InventoryReleasedEvent("InventoryReleased", payment.OrderId, DateTime.UtcNow);
        db.OutboxMessages.Add(InventoryOutboxMessage.Create(
            _configuration["ServiceBus:Topic"] ?? "ordersvctopic",
            "InventoryReleased",
            JsonSerializer.Serialize(releasedEvent)));

        await db.SaveChangesAsync(args.CancellationToken);
        await transaction.CommitAsync(args.CancellationToken);
        _logger.LogInformation("Released inventory for failed order {OrderId}.", payment.OrderId);
    }

    private sealed record OrderCreatedEvent(
        Guid OrderId,
        Guid CustomerId,
        decimal TotalAmount,
        DateTime CreatedAtUtc,
        List<OrderItemEvent>? Items);

    private sealed record OrderItemEvent(Guid ProductId, int Quantity, decimal UnitPrice);
    private sealed record ReservedItem(Guid ProductId, int Quantity);
    private sealed record InventoryReservedEvent(
        string EventType,
        Guid OrderId,
        Guid CustomerId,
        decimal Amount,
        Guid ReservationId,
        List<ReservedItem> Items,
        DateTime OccurredAtUtc);
    private sealed record PaymentFailedEvent(Guid OrderId);
    private sealed record InventoryReleasedEvent(string EventType, Guid OrderId, DateTime OccurredAtUtc);
}
