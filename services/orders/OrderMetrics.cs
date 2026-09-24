using System.Diagnostics.Metrics;

// Custom business metrics — the numbers a product owner actually cares about,
// exported to Prometheus alongside the infrastructure metrics.
public static class OrderMetrics
{
    public const string MeterName = "Shop.Orders";
    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> Placed = Meter.CreateCounter<long>(
        "orders.placed", unit: "{order}", description: "Orders successfully placed.");

    public static readonly Counter<long> Rejected = Meter.CreateCounter<long>(
        "orders.rejected", unit: "{order}", description: "Orders rejected (bad request / insufficient stock).");

    public static void CountRejected(string reason) =>
        Rejected.Add(1, new KeyValuePair<string, object?>("reason", reason));
}
