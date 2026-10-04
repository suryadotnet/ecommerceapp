﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using OrderService.Application.Commands.CreateOrder;
using OrderService.Application.Commands.UpdateOrder;
using OrderService.Application.Commands.DeleteOrder;
using OrderService.Application.Commands.CheckoutOrder;
using OrderService.Application.Queries.GetOrder;

// For more information on enabling Web API for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace OrderService.Controllers
{

    [ApiController]
    [Route("api/orders")]
    public class OrdersController : ControllerBase
    {
        private readonly IMediator _mediator;
        public OrdersController(IMediator mediator)
        {
            _mediator = mediator;
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
                   x.UnitPrice
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
            
            Console.WriteLine(
                    "Checkout started: {OrderId} at {Time}",
                    id,
                    DateTime.UtcNow);
            
                await Task.Delay(TimeSpan.FromSeconds(5));
            
              
            var result = await _mediator.Send(new CheckoutOrderCommand(id), cancellationToken);

            if (result is null)
                return NotFound();
            
            Console.WriteLine(
                "Checkout completed: {OrderId} at {Time}",
                id,
                DateTime.UtcNow);

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
    

