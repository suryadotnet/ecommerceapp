using System.Net.Http.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using OrderService.Domain;
using OrderService.Infrastructure;

namespace OrderService.Application.Commands.CreateOrder;

public class CreateOrderCommandHandler
 : IRequestHandler<CreateOrderCommand, Guid>
{
    private readonly OrderDbContext _db;
    public CreateOrderCommandHandler(OrderDbContext db)
    {
        _db = db;
    }
    public async Task<Guid> Handle(
    CreateOrderCommand request,
    CancellationToken cancellationToken)
    {
        if (request.CustomerId == Guid.Empty)
            throw new ArgumentException("CustomerId is required.", nameof(request.CustomerId));

        if (request.Items is null || request.Items.Count == 0)
            throw new ArgumentException("At least one order item is required.", nameof(request.Items));

        if (request.Items.Any(i => i.ProductId == Guid.Empty))
            throw new ArgumentException("Each order item requires a valid ProductId.", nameof(request.Items));

        var order = new Order(request.CustomerId);
        foreach (var item in request.Items)
        {
            order.AddItem(
            item.ProductId,
            item.Quantity,
            item.UnitPrice);
        }
        order.Confirm();
        _db.Orders.Add(order);
        await _db.SaveChangesAsync(cancellationToken);
        return order.Id;
    }
}

