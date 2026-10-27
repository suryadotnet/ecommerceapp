using MediatR;
using Microsoft.EntityFrameworkCore;
using OrderService.Domain.Enums;
using OrderService.Infrastructure;

namespace OrderService.Application.Commands.CheckoutOrder;

public class CheckoutOrderCommandHandler : IRequestHandler<CheckoutOrderCommand, CheckoutOrderResult?>
{
    private readonly OrderDbContext _db;

    public CheckoutOrderCommandHandler(OrderDbContext db)
    {
        _db = db;
    }

    public async Task<CheckoutOrderResult?> Handle(CheckoutOrderCommand request, CancellationToken ct)
    {
        var order = await _db.Orders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.Id == request.OrderId, ct);
        if (order is null) return null;

        return order.Status switch
        {
            OrderStatus.Pending => new CheckoutOrderResult(
                order.Id, "Pending", "The choreography saga is processing this order."),
            OrderStatus.Confirmed => new CheckoutOrderResult(
                order.Id, "Completed", "Inventory was reserved and payment succeeded."),
            OrderStatus.Failed => new CheckoutOrderResult(
                order.Id, "Failed", "Payment failed; inventory compensation was requested."),
            _ => new CheckoutOrderResult(order.Id, order.Status.ToString(), "The order is not awaiting checkout.")
        };
    }
}
