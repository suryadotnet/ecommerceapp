using System.Net.Http.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using OrderService.Domain;
using OrderService.Infrastructure;
using OrderService.Infrastructure.ExternalServices;

namespace OrderService.Application.Commands.CreateOrder;

public class CreateOrderCommandHandler
 : IRequestHandler<CreateOrderCommand, Guid>
{
    private readonly OrderDbContext _db;
    private readonly IOrderMessagePublisher _messagePublisher;

    public CreateOrderCommandHandler(OrderDbContext db, IOrderMessagePublisher messagePublisher)
    {
        _db = db;
        _messagePublisher = messagePublisher;
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
            item.UnitPrice,
            item.ImageUrl);
        }
        order.Submit();
        _db.Orders.Add(order);

        await _messagePublisher.PublishOrderCreatedAsync(
            new OrderCreatedMessage(
                order.Id,
                order.CustomerId,
                order.GetTotal(),
                order.CreatedAtUtc,
                order.Items.Select(item => new OrderCreatedItemMessage(
                    item.ProductId,
                    item.Quantity,
                    item.UnitPrice)).ToList()),
            cancellationToken);

        // The order and its event are committed together; Service Bus delivery is asynchronous.
        await _db.SaveChangesAsync(cancellationToken);

        return order.Id;
    }
}
