﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using OrderService.Application.Commands.CreateOrder;
using OrderService.Application.Commands.UpdateOrder;
using OrderService.Application.Commands.DeleteOrder;
using OrderService.Application.Commands.CheckoutOrder;
using OrderService.Application.Queries.GetOrder;
using OrderService.Infrastructure.ExternalServices;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace OrderService.Controllers
{

    [ApiController]
    [Route("api/orders")]
    public class OrdersController : ControllerBase
    {
        private readonly IMediator _mediator;
        private readonly IBlobStorageService _blobStorageService;

        // Container where product images uploaded at order-creation time are stored.
        private const string ProductImagesContainer = "product-images";

        public OrdersController(IMediator mediator, IBlobStorageService blobStorageService)
        {
            _mediator = mediator;
            _blobStorageService = blobStorageService;
        }

        [HttpPost]
        public async Task<IActionResult> Create(
        CreateOrderCommand request, CancellationToken cancellationToken)
        {
            var command = new CreateOrderCommand(
           request.CustomerId,
           request.Items.Select(x =>
               new CreateOrderItemRequest(
                   x.ProductId,
                   x.Quantity,
                   x.UnitPrice,
                   x.ImageUrl
               )).ToList()
       );

            var orderId = await _mediator.Send(
            command,
                cancellationToken);

            return CreatedAtAction(
                nameof(GetById),
                new { id = orderId },
                new { orderId });
            //new { OrderId = orderId });
        }

        // Accepts order data + one product image file per line item as multipart/form-data.
        // "items" is a JSON-encoded array of { productId, quantity, unitPrice } matching
        // CreateOrderItemRequest (minus ImageUrl, which is resolved here from the upload).
        // "images" are matched to "items" by array index (images[0] -> items[0], etc.).
        // Each image is uploaded to Blob Storage container "product-images" and its
        // resulting URL is stored on the corresponding OrderItem.ImageUrl column.
        [HttpPost("with-images")]
        [RequestSizeLimit(50_000_000)] // 50 MB total request size cap
        public async Task<IActionResult> CreateWithImages(
            [FromForm] Guid customerId,
            [FromForm] string items,
            List<IFormFile>? images,
            CancellationToken cancellationToken)
        {
            var parsedItems = JsonSerializer.Deserialize<List<CreateOrderItemRequest>>(
                items,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (parsedItems is null || parsedItems.Count == 0)
                return BadRequest("At least one order item is required.");

            var itemsWithImages = new List<CreateOrderItemRequest>(parsedItems.Count);
            for (var i = 0; i < parsedItems.Count; i++)
            {
                var item = parsedItems[i];
                string? imageUrl = null;

                var file = images is not null && i < images.Count ? images[i] : null;
                if (file is not null && file.Length > 0)
                {
                    await using var stream = file.OpenReadStream();
                    imageUrl = await _blobStorageService.UploadAsync(
                        ProductImagesContainer,
                        file.FileName,
                        stream,
                        file.ContentType ?? "application/octet-stream",
                        cancellationToken);
                }

                itemsWithImages.Add(item with { ImageUrl = imageUrl });
            }

            var command = new CreateOrderCommand(customerId, itemsWithImages);
            var orderId = await _mediator.Send(command, cancellationToken);

            return CreatedAtAction(
                nameof(GetById),
                new { id = orderId },
                new { orderId });
        }

        [HttpGet("{id:guid}")]
        public async Task<IActionResult> GetById(
        Guid id,
        CancellationToken cancellationToken)
        {
            var result = await _mediator.Send(
                new GetOrderQuery(id),
                cancellationToken);

            if (result is null)
                return NotFound();

            return Ok(result);
        }

        // Reserves inventory and processes payment for an existing order. Calls to
        // InventoryService/PaymentService are protected by Rate Limiter, Retry,
        // Circuit Breaker and Timeout. Returns 200 on success, 503 if downstream failed.
        [HttpPost("{id:guid}/checkout")]
        public async Task<IActionResult> Checkout(
        Guid id,
        CancellationToken cancellationToken)
        {
            
            // Console.WriteLine(
            //         "Checkout started: {OrderId} at {Time}",
            //         id,
            //         DateTime.UtcNow);
            //
            //     await Task.Delay(TimeSpan.FromSeconds(5));
            
              
            var result = await _mediator.Send(new CheckoutOrderCommand(id), cancellationToken);

            if (result is null)
                return NotFound();
            
            // Console.WriteLine(
            //     "Checkout completed: {OrderId} at {Time}",
            //     id,
            //     DateTime.UtcNow);

            return result.Status == "Completed"
                ? Ok(result)
                : StatusCode(StatusCodes.Status503ServiceUnavailable, result);
        }

        [HttpPut("{id:guid}")]
        public async Task<IActionResult> Update(
        Guid id,
        UpdateOrderCommand request,
        CancellationToken cancellationToken)
        {
            var command = new UpdateOrderCommand(
                id,
                request.Items.Select(x =>
                    new UpdateOrderItemRequest(
                        x.ProductId,
                        x.Quantity,
                        x.UnitPrice
                    )).ToList()
            );

            var updated = await _mediator.Send(command, cancellationToken);

            if (!updated)
                return NotFound();

            return NoContent();
        }

        [HttpDelete("{id:guid}")]
        public async Task<IActionResult> Delete(
        Guid id,
        CancellationToken cancellationToken)
        {
            var deleted = await _mediator.Send(
                new DeleteOrderCommand(id),
                cancellationToken);

            if (!deleted)
                return NotFound();

            return NoContent();
        }
    }
}
    

