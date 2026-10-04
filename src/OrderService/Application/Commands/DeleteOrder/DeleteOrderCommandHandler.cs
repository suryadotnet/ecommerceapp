using MediatR;
using Microsoft.EntityFrameworkCore;
using OrderService.Infrastructure;

namespace OrderService.Application.Commands.DeleteOrder;

public class DeleteOrderCommandHandler : IRequestHandler<DeleteOrderCommand, bool>
{
    private readonly OrderDbContext _db;
    public DeleteOrderCommandHandler(OrderDbContext db)
    {
        _db = db;
    }

    public async Task<bool> Handle(
        DeleteOrderCommand request,
        CancellationToken cancellationToken)
    {
        var order = await _db.Orders
            .FirstOrDefaultAsync(x => x.Id == request.OrderId, cancellationToken);

        if (order is null)
            return false;

        _db.Orders.Remove(order);
        await _db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

