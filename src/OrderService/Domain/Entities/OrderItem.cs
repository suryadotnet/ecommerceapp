﻿using System;
using OrderService.Domain.Exceptions;

namespace OrderService.Domain
{
	public class OrderItem
	{
		
        public Guid Id { get; private set; }
        public Guid ProductId { get; private set; }
        public int Quantity { get; private set; }
        public decimal UnitPrice { get; private set; }
        // URL of the product image uploaded to Blob Storage at order-creation time.
        public string? ImageUrl { get; private set; }
        private OrderItem() { }
        internal OrderItem(
        Guid productId,
        int quantity,
        decimal unitPrice,
        string? imageUrl = null)
        {
            if (quantity <= 0)
                throw new DomainException(
                "Quantity must be greater than zero.");
            if (unitPrice <= 0)
                throw new DomainException(
                "Price must be greater than zero.");
            Id = Guid.NewGuid();
            ProductId = productId;
            Quantity = quantity;
            UnitPrice = unitPrice;
            ImageUrl = imageUrl;
        }
        public decimal Total => Quantity * UnitPrice;
    }
}

