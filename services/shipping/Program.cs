using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<ShippingWorker>();

// Distributed tracing: emit spans for the messages we consume and publish,
// linked (via the message headers) into the trace that started the order.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("shipping"))
    .WithTracing(t => t
        .AddSource(Telemetry.MessagingSourceName)
        .AddOtlpExporter());

var host = builder.Build();
host.Run();

// This service has NO HTTP API and NO shared database. It only reacts to events.
// Orders doesn't know Shipping exists — that decoupling is the point of messaging.
public class ShippingWorker(IConfiguration config, ILogger<ShippingWorker> logger) : BackgroundService
{
    private readonly string _host = config["RabbitMq:Host"] ?? "rabbitmq";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IConnection? conn = null;

        // Wait for the broker to come up (containers start in parallel).
        for (var attempt = 1; attempt <= 15 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                var factory = new ConnectionFactory { HostName = _host };
                conn = await factory.CreateConnectionAsync(stoppingToken);
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("RabbitMQ not ready (attempt {Attempt}/15): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }

        if (conn is null)
        {
            logger.LogError("Could not connect to RabbitMQ. Shipping worker stopping.");
            return;
        }

        var channel = await conn.CreateChannelAsync(cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync("order-placed", durable: true, exclusive: false, autoDelete: false,
            cancellationToken: stoppingToken);
        // We also PUBLISH to this queue once a shipment is prepared.
        await channel.QueueDeclareAsync("order-shipped", durable: true, exclusive: false, autoDelete: false,
            cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            // Continue the trace that produced this event.
            var parentContext = Telemetry.Extract(ea.BasicProperties);
            using var activity = Telemetry.Messaging.StartActivity(
                "process order-placed", ActivityKind.Consumer, parentContext);

            var json = Encoding.UTF8.GetString(ea.Body.ToArray());
            try
            {
                var order = JsonSerializer.Deserialize<OrderPlaced>(json);
                if (order is null) return;

                logger.LogInformation(
                    "📦 Preparing shipment for order {OrderId}: {Qty} x {Product} (total {Total:C})",
                    order.Id, order.Quantity, order.ProductName, order.Total);

                // Simulate the physical work of preparing the parcel...
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

                // ...then announce it shipped, propagating the trace context.
                var props = new BasicProperties { Headers = new Dictionary<string, object?>() };
                Telemetry.Inject(activity?.Context ?? parentContext, props.Headers!);
                var shipped = Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new OrderShipped(order.Id)));
                await channel.BasicPublishAsync(exchange: "", routingKey: "order-shipped",
                    mandatory: false, basicProperties: props, body: shipped, cancellationToken: stoppingToken);

                logger.LogInformation("🚚 Order {OrderId} shipped.", order.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process order-placed event: {Json}", json);
            }
        };

        await channel.BasicConsumeAsync("order-placed", autoAck: true, consumer: consumer,
            cancellationToken: stoppingToken);
        logger.LogInformation("Shipping worker is listening for 'order-placed' events.");

        // Keep the service alive.
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}

public record OrderPlaced(
    Guid Id,
    int ProductId,
    string ProductName,
    decimal UnitPrice,
    int Quantity,
    decimal Total,
    DateTime PlacedAt);

public record OrderShipped(Guid OrderId);
