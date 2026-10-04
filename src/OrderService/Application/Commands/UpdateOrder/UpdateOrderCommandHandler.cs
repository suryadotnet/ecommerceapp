using MediatR;
using Microsoft.EntityFrameworkCore;
using OrderService.Infrastructure;

namespace OrderService.Application.Commands.UpdateOrder;

public class UpdateOrderCommandHandler : IRequestHandler<UpdateOrderCommand, bool>
{
    private readonly OrderDbContext _db;
    public UpdateOrderCommandHandler(OrderDbContext db)
    {
        _db = db;
    }

    public async Task<bool> Handle(
        UpdateOrderCommand request,
        CancellationToken cancellationToken)
    {
        var order = await _db.Orders
            .FirstOrDefaultAsync(x => x.Id == request.OrderId, cancellationToken);

        if (order is null)
            return false;

        if (request.Items is null || request.Items.Count == 0)
            throw new ArgumentException("At least one order item is required.", nameof(request.Items));

        if (request.Items.Any(i => i.ProductId == Guid.Empty))
            throw new ArgumentException("Each order item requires a valid ProductId.", nameof(request.Items));

        order.UpdateItems(request.Items.Select(i => (i.ProductId, i.Quantity, i.UnitPrice)));

        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

