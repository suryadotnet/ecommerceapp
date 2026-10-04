using MediatR;
using OrderService.Domain;
using OrderService.Domain.Enums;

namespace OrderService.Application.Commands.CreateOrder;

public record CreateOrderCommand(
 Guid CustomerId,
 List<CreateOrderItemRequest> Items
):IRequest<Guid>;

public record CreateOrderItemRequest(
 Guid ProductId,
 int Quantity,
 decimal UnitPrice);

//public sealed record CreateOrderResult(Guid OrderId, OrderStatus Status, string Message);'


//public sealed record CreateOrderRequest(
//    Guid CustomerId,
//    List<CreateOrderItemRequest> Items
//);

//public sealed record CreateOrderItemRequest(
//    Guid ProductId,
//    int Quantity,
//    decimal UnitPrice
//);
