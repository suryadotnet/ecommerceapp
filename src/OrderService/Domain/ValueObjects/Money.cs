using System;
namespace OrderService.Domain.ValueObjects
{
    public record Money(
 decimal Amount,
 string Currency);

}
