using MediatR;

namespace OrderService.Application.Commands.DeleteOrder;

public record DeleteOrderCommand(Guid OrderId) : IRequest<bool>;

