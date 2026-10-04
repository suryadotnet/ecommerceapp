using MediatR;

namespace OrderService.Application.Commands.CheckoutOrder;

// Result of a checkout attempt.
public sealed record CheckoutOrderResult(Guid OrderId, string Status, string Message);

// Checkout = reserve inventory + process payment for an EXISTING order.
public sealed record CheckoutOrderCommand(Guid OrderId) : IRequest<CheckoutOrderResult?>;
