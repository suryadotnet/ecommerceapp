namespace InventoryService.Domain;

public sealed class InventoryReservation
{
    public Guid ReservationId { get; set; }
    public Guid OrderId { get; set; }
    public Guid ProductId { get; set; }
    public int Quantity { get; set; }
    public bool IsReleased { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
