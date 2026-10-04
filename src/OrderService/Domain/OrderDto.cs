using System;
namespace OrderService.Domain
{
    public sealed record OrderDto
    {
        public Guid Id { get; init; }

        public Guid CustomerId { get; init; }

        public string Status { get; init; } = string.Empty;

        public decimal Total { get; init; }
    }
}

