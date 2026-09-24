using System.Diagnostics;
using System.Text;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using RabbitMQ.Client;

// Propagates W3C trace context across RabbitMQ so an async message flow shows up
// as one connected trace (order request -> publish -> shipping -> back to orders).
public static class Telemetry
{
    public const string MessagingSourceName = "Shop.Messaging";
    public static readonly ActivitySource Messaging = new(MessagingSourceName);
    private static readonly TextMapPropagator Propagator = Propagators.DefaultTextMapPropagator;

    // Write the trace context into outgoing message headers.
    public static void Inject(ActivityContext context, IDictionary<string, object?> headers) =>
        Propagator.Inject(new PropagationContext(context, Baggage.Current), headers,
            static (carrier, key, value) => carrier[key] = value);

    // Read a parent trace context from incoming message headers.
    public static ActivityContext Extract(IReadOnlyBasicProperties props)
    {
        var parent = Propagator.Extract(default, props.Headers, static (headers, key) =>
        {
            if (headers is not null && headers.TryGetValue(key, out var value) && value is byte[] bytes)
                return new[] { Encoding.UTF8.GetString(bytes) };
            return Array.Empty<string>();
        });
        return parent.ActivityContext;
    }
}
