using MediatR;
using OrderService.Domain;

namespace OrderService.Application.Queries.GetOrder;

public record GetOrderQuery(
 Guid OrderId
) : IRequest<OrderDto?>;
