using System;
using System.Collections.Generic;
namespace OrderService.Domain
{
    public sealed record OrderDto
    {
        public Guid Id { get; init; }

        public Guid CustomerId { get; init; }

        public string Status { get; init; } = string.Empty;

        public decimal Total { get; init; }

        public List<OrderItemDto> Items { get; init; } = new();
    }

    public sealed record OrderItemDto
    {
        public Guid ProductId { get; init; }
        public int Quantity { get; init; }
        public decimal UnitPrice { get; init; }
        // Blob Storage URL of the product image uploaded at order-creation time.
        public string? ImageUrl { get; init; }
    }
}

