using Xunit;

public class OutboxMessageTests
{
    [Fact]
    public void Create_serializes_payload_and_starts_unprocessed()
    {
        var order = OrderFactory.Create(
            Guid.NewGuid(), new ProductDto(1, "Mechanical Keyboard", 89.90m, 40), 2, DateTime.UtcNow);

        var msg = OutboxMessage.Create("order-placed", order);

        Assert.Equal("order-placed", msg.Type);
        Assert.Null(msg.ProcessedAt);          // not yet dispatched
        Assert.Equal(0, msg.Attempts);
        Assert.NotEqual(default, msg.OccurredAt);
        Assert.Contains("Mechanical Keyboard", msg.Payload); // real serialized JSON
        Assert.NotEqual(Guid.Empty, msg.Id);
    }
}
