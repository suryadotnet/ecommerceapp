using System;
using System.ComponentModel.DataAnnotations;

namespace InventoryService.Domain
{
	public class EmailNotification
	{
        [Key]
        public Guid ID { get; set; }
        public string EmailType { get; set; } = "Order Details";
    }
}

