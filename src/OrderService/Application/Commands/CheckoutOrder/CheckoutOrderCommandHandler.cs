using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;
using OrderService.Infrastructure;
using OrderService.Infrastructure.ExternalServices;

namespace OrderService.Application.Commands.CheckoutOrder;

// Saga-style checkout. Every outbound call goes through a typed HttpClient that has the
// resilience pipeline attached (Rate Limiter -> Retry -> Circuit Breaker -> Timeout),
// so transient failures are retried before they ever reach the catch blocks below.
public class CheckoutOrderCommandHandler : IRequestHandler<CheckoutOrderCommand, CheckoutOrderResult?>
{
    private readonly OrderDbContext _db;
    private readonly IInventoryServiceClient _inventory;
    private readonly IPaymentServiceClient _payment;
    private readonly ILogger<CheckoutOrderCommandHandler> _logger;

    public CheckoutOrderCommandHandler(
        OrderDbContext db,
        IInventoryServiceClient inventory,
        IPaymentServiceClient payment,
        ILogger<CheckoutOrderCommandHandler> logger)
    {
        _db = db;
        _inventory = inventory;
        _payment = payment;
        _logger = logger;
    }

    public async Task<CheckoutOrderResult?> Handle(CheckoutOrderCommand request, CancellationToken ct)
    {
        var order = await _db.Orders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == request.OrderId, ct);
        if (order is null) return null;

        // Step 1: reserve stock for each line. Remember what succeeded for compensation.
        var reserved = new List<(Guid ProductId, int Quantity)>();
        try
        {
            foreach (var item in order.Items)
            {
                await _inventory.ReserveAsync(
                    new ReserveInventoryRequest(order.Id, item.ProductId, item.Quantity), ct);
                reserved.Add((item.ProductId, item.Quantity));
            }

            // Step 2: charge the customer.
            var total = order.Items.Sum(i => i.Quantity * i.UnitPrice);
            await _payment.ProcessPaymentAsync(
                new ProcessPaymentRequest(order.Id, order.CustomerId, total), ct);

            return new CheckoutOrderResult(order.Id, "Completed", "Inventory reserved and payment processed.");
        }
        catch (Exception ex) when (ex is HttpRequestException
                                      or BrokenCircuitException
                                      or TimeoutRejectedException
                                      or RateLimiterRejectedException
                                      or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Checkout failed for order {OrderId}; compensating.", order.Id);

            // Compensation: release whatever stock we already reserved (best effort).
            foreach (var (productId, quantity) in reserved)
            {
                try
                {
                    await _inventory.ReleaseAsync(
                        new ReleaseInventoryRequest(order.Id, productId, quantity), CancellationToken.None);
                }
                catch (Exception releaseEx)
                {
                    _logger.LogError(releaseEx, "Failed to release stock for product {ProductId}", productId);
                }
            }

            return new CheckoutOrderResult(order.Id, "Failed", ex.Message);
        }
    }
}

