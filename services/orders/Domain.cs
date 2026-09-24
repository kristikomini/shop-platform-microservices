// Pure, dependency-free domain logic — extracted so it can be unit-tested
// without a database, HTTP, or a message broker.
public static class OrderFactory
{
    public static Order Create(Guid id, ProductDto product, int quantity, DateTime placedAtUtc)
    {
        if (quantity < 1)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be at least 1.");

        return new Order
        {
            Id = id,
            ProductId = product.Id,
            ProductName = product.Name,
            UnitPrice = product.Price,
            Quantity = quantity,
            PlacedAt = placedAtUtc
        };
    }
}
