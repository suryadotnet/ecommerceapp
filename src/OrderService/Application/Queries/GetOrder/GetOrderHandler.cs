using MediatR;
using Microsoft.EntityFrameworkCore;
using OrderService.Domain;
using OrderService.Infrastructure;

namespace OrderService.Application.Queries.GetOrder;

public class GetOrderQueryHandler
 : IRequestHandler<GetOrderQuery, OrderDto?>
{
    private readonly OrderDbContext _db;
    public GetOrderQueryHandler(OrderDbContext db)
    {
        _db = db;
    }
    public async Task<OrderDto?> Handle(
    GetOrderQuery request,
    CancellationToken cancellationToken)
    {
        return await _db.Orders
        .AsNoTracking()
        .Where(x => x.Id == request.OrderId)
        .Select(x => new OrderDto
        {
            Id = x.Id,
            CustomerId = x.CustomerId,
            Status = x.Status.ToString(),
            Total = x.Items.Sum(i =>
     i.Quantity * i.UnitPrice)
        })
        .FirstOrDefaultAsync(cancellationToken);


    }
}