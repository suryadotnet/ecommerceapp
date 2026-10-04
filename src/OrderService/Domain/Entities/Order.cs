using OrderService.Domain.Enums;
using OrderService.Domain.Exceptions;

namespace OrderService.Domain;

//public enum OrderStatus { Pending, Confirmed, Cancelled }

public sealed class Order
{
    private readonly List<OrderItem> _items = new();
    public Guid Id { get; private set; }
    public Guid CustomerId { get; private set; }
    public OrderStatus Status { get; private set; }
    public DateTime CreatedAtUtc { get; private set; }
    public string CreatedBy { get; private set; }
    public IReadOnlyCollection<OrderItem> Items =>
    _items.AsReadOnly();
    private Order() { }
    public Order(Guid customerId)
    {
        Id = Guid.NewGuid();
        CustomerId = customerId;
        Status = OrderStatus.Draft;
        CreatedAtUtc = DateTime.UtcNow;
        CreatedBy = "System"; // Default value, can be changed later
    }
    public void AddItem(
    Guid productId,
    int quantity,
    decimal unitPrice)
    {
        if (Status != OrderStatus.Draft)
            throw new DomainException(
            "Items cannot be added after order confirmation.");
        var item = new OrderItem(
        productId, quantity, unitPrice);
        _items.Add(item);
    }

    public void UpdateItems(IEnumerable<(Guid ProductId, int Quantity, decimal UnitPrice)> items)
    {
        if (Status != OrderStatus.Draft)
            throw new DomainException(
            "Only draft orders can be updated.");

        _items.Clear();
        foreach (var (productId, quantity, unitPrice) in items)
        {
            var item = new OrderItem(productId, quantity, unitPrice);
            _items.Add(item);
        }
    }

    public void Confirm()
    {
        if (!_items.Any())
            throw new DomainException(
            "Order must contain at least one item.");
        if (Status != OrderStatus.Draft)
            throw new DomainException(
            "Only draft orders can be confirmed.");
        Status = OrderStatus.Confirmed;
    }

    public void Cancel()
    {
        if (Status == OrderStatus.Cancelled)
        {
            throw new DomainException(
                "Order is already cancelled.");
        }

        Status = OrderStatus.Cancelled;
    }

 
    public decimal GetTotal() =>
    _items.Sum(x => x.Total);
}
