using System;
namespace OrderService.Domain.Exceptions
{
	public sealed class DomainException:Exception
	{
        public DomainException(string message)
      : base(message)
        {
        }
    }
}

