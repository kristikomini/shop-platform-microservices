using System.Diagnostics.Metrics;

public static class ShippingMetrics
{
    public const string MeterName = "Shop.Shipping";
    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> Shipped = Meter.CreateCounter<long>(
        "shipments.completed", unit: "{shipment}", description: "Shipments completed.");
}
