namespace Shop.Contracts;

// The messaging contracts shared by every service that publishes or consumes
// these events. Keeping them in one place means the wire format has a single
// source of truth and is decoupled from any service's internal/database model.

public record OrderPlaced(
    Guid Id,
    int ProductId,
    string ProductName,
    decimal UnitPrice,
    int Quantity,
    decimal Total,
    DateTime PlacedAt);

public record OrderShipped(Guid OrderId);
