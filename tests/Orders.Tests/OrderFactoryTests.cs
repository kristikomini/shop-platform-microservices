using Xunit;

// Pure unit tests for the extracted domain logic — no DB, HTTP or broker needed.
public class OrderFactoryTests
{
    private static ProductDto SampleProduct(decimal price = 89.90m) =>
        new(Id: 1, Name: "Mechanical Keyboard", Price: price, Stock: 40);

    [Fact]
    public void Create_computes_total_from_unit_price_and_quantity()
    {
        var order = OrderFactory.Create(Guid.NewGuid(), SampleProduct(89.90m), 3, DateTime.UtcNow);

        Assert.Equal(269.70m, order.Total);
        Assert.Equal("Mechanical Keyboard", order.ProductName);
        Assert.Equal(89.90m, order.UnitPrice);
        Assert.Equal(3, order.Quantity);
    }

    [Fact]
    public void Create_copies_the_product_snapshot_onto_the_order()
    {
        var placedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var order = OrderFactory.Create(Guid.NewGuid(), SampleProduct(), 1, placedAt);

        Assert.Equal(1, order.ProductId);
        Assert.Equal(placedAt, order.PlacedAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Create_rejects_non_positive_quantity(int quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OrderFactory.Create(Guid.NewGuid(), SampleProduct(), quantity, DateTime.UtcNow));
    }
}
