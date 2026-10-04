using System.ComponentModel.DataAnnotations;

namespace InventoryService.Domain;

public sealed class ProductStock
{
    [Key]
    public Guid ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public int AvailableQuantity { get; set; } 
    public int ReservedQuantity { get; set; } 
}
