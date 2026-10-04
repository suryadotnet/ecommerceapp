using MediatR;

namespace OrderService.Application.Commands.UpdateOrder;

public record UpdateOrderCommand(
    Guid OrderId,
    List<UpdateOrderItemRequest> Items
) : IRequest<bool>;

public record UpdateOrderItemRequest(
    Guid ProductId,
    int Quantity,
    decimal UnitPrice);

