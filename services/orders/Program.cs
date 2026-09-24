using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Scalar.AspNetCore;
using Shop.Contracts;

var builder = WebApplication.CreateBuilder(args);

// Orders owns its own database, separate from Catalog's.
builder.Services.AddDbContext<OrdersDb>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("OrdersDb")));

// Typed HTTP client pointed at the Catalog SERVICE (by DNS name, set in compose).
// AddStandardResilienceHandler adds retries, a circuit breaker and timeouts
// (Polly under the hood) — so a Catalog blip doesn't immediately fail an order.
builder.Services.AddHttpClient("catalog", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:Catalog"]!))
    .AddStandardResilienceHandler();

builder.Services.AddSingleton<EventBus>();

// Orders is also a CONSUMER: it listens for "order-shipped" events from the
// Shipping service and updates the order's status. So the lifecycle is driven
// by events, not by Orders polling anyone.
builder.Services.AddHostedService<OrderShippedConsumer>();

// The OUTBOX dispatcher: reads unpublished outbox rows and pushes them to
// RabbitMQ. Events are written to the outbox in the same transaction as the
// order, so we never lose an event or publish one for an order that rolled back.
builder.Services.AddHostedService<OutboxDispatcher>();

// OpenAPI + health checks (database reachable AND broker reachable).
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<OrdersDb>("orders-db")
    .AddCheck<RabbitMqHealthCheck>("rabbitmq");

// Distributed tracing. "Shop.Messaging" is our custom source for the spans that
// bridge the RabbitMQ hops (publish from the outbox, consume order-shipped).
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("orders"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("Npgsql")
        .AddSource(Telemetry.MessagingSourceName)
        .AddOtlpExporter())
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter(OrderMetrics.MeterName)   // custom business counters
        .AddPrometheusExporter());

// JWT bearer authentication (tokens issued by the auth service). Reads stay
// public; placing an order requires an authenticated user.
var jwt = builder.Configuration.GetSection("Jwt");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o => o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = jwt["Issuer"],
        ValidateAudience = true,
        ValidAudience = jwt["Audience"],
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt["Key"]!)),
        ValidateLifetime = true
    });
builder.Services.AddAuthorization();

var app = builder.Build();

await OrdersDbInitializer.InitializeAsync(app.Services, app.Logger);

app.MapOpenApi();
app.MapScalarApiReference(o => o.WithTitle("Orders API"));
app.MapHealthChecks("/health");
app.MapPrometheusScrapingEndpoint();   // /metrics for Prometheus

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/orders", async (OrdersDb db) =>
    await db.Orders.OrderByDescending(o => o.PlacedAt).ToListAsync())
    .WithName("GetOrders").WithTags("Orders");

app.MapPost("/orders", async (
    CreateOrder cmd,
    OrdersDb db,
    IHttpClientFactory http,
    ILogger<Program> logger) =>
{
    if (cmd.Quantity < 1)
    {
        OrderMetrics.CountRejected("invalid_quantity");
        return Results.BadRequest("Quantity must be at least 1.");
    }

    // --- SYNCHRONOUS communication: ask Catalog to RESERVE stock ---
    // Catalog owns stock, so Orders asks it to atomically decrement. A 409 means
    // there wasn't enough — the order is rejected before anything is persisted.
    var catalog = http.CreateClient("catalog");
    var reserve = await catalog.PostAsJsonAsync(
        $"/products/{cmd.ProductId}/reserve", new { quantity = cmd.Quantity });

    if (reserve.StatusCode == System.Net.HttpStatusCode.Conflict)
    {
        OrderMetrics.CountRejected("insufficient_stock");
        return Results.BadRequest($"Insufficient stock for product {cmd.ProductId}.");
    }
    if (reserve.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        OrderMetrics.CountRejected("unknown_product");
        return Results.BadRequest($"Unknown product {cmd.ProductId}.");
    }
    if (!reserve.IsSuccessStatusCode)
    {
        OrderMetrics.CountRejected("reserve_failed");
        return Results.BadRequest("Could not reserve stock.");
    }

    var product = await reserve.Content.ReadFromJsonAsync<ProductDto>();
    if (product is null)
    {
        OrderMetrics.CountRejected("reserve_failed");
        return Results.BadRequest("Invalid product response");
    }

    var order = OrderFactory.Create(Guid.NewGuid(), product, cmd.Quantity, DateTime.UtcNow);

    // --- OUTBOX: persist the order AND the event in ONE transaction. ---
    // We do NOT publish to RabbitMQ here; the OutboxDispatcher does that from
    // the committed row, so the event and the order can never disagree.
    // Publish the shared OrderPlaced CONTRACT (not the EF entity) so the wire
    // format is decoupled from how Orders happens to store the row.
    var orderPlaced = new OrderPlaced(
        order.Id, order.ProductId, order.ProductName, order.UnitPrice,
        order.Quantity, order.Total, order.PlacedAt);
    db.Orders.Add(order);
    db.Outbox.Add(OutboxMessage.Create("order-placed", orderPlaced));
    await db.SaveChangesAsync();

    OrderMetrics.Placed.Add(1);
    logger.LogInformation("Order {OrderId} placed (event queued in outbox).", order.Id);

    return Results.Created($"/orders/{order.Id}", order);
})
    .RequireAuthorization()
    .WithName("PlaceOrder").WithTags("Orders");

app.Run();

// ---------------------------------------------------------------------------

public record CreateOrder(int ProductId, int Quantity);
public record ProductDto(int Id, string Name, decimal Price, int Stock);

public class Order
{
    public Guid Id { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public decimal Total => UnitPrice * Quantity;
    public DateTime PlacedAt { get; set; }
    public string Status { get; set; } = "Placed";
}

public class OrdersDb(DbContextOptions<OrdersDb> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b) =>
        b.Entity<Order>().Ignore(o => o.Total); // computed, not stored
}

// A pending domain event, written in the SAME transaction as the state change
// it describes. The dispatcher publishes it and stamps ProcessedAt.
public class OutboxMessage
{
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTime OccurredAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    // W3C traceparent of the request that produced this event, so the eventual
    // publish (which happens later, in the dispatcher) links to the same trace.
    public string? TraceParent { get; set; }

    public static OutboxMessage Create(string type, object payload) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        Payload = JsonSerializer.Serialize(payload),
        OccurredAt = DateTime.UtcNow,
        TraceParent = Activity.Current?.Id
    };
}

// Thin wrapper over RabbitMQ. Publishes an already-serialized payload (the JSON
// stored in the outbox), so the dispatcher doesn't re-serialize.
public class EventBus(IConfiguration config)
{
    private readonly string _host = config["RabbitMq:Host"] ?? "rabbitmq";

    public async Task PublishRawAsync(string queue, string json,
        ActivityContext traceContext = default, CancellationToken ct = default)
    {
        var factory = new ConnectionFactory { HostName = _host };
        await using var conn = await factory.CreateConnectionAsync(ct);
        await using var channel = await conn.CreateChannelAsync(cancellationToken: ct);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: ct);

        var props = new BasicProperties { Headers = new Dictionary<string, object?>() };
        Telemetry.Inject(traceContext, props.Headers!);

        var body = Encoding.UTF8.GetBytes(json);
        await channel.BasicPublishAsync(exchange: "", routingKey: queue, mandatory: false,
            basicProperties: props, body: body, cancellationToken: ct);
    }
}

// Polls the outbox and publishes unprocessed messages, stamping ProcessedAt on
// success and counting Attempts on failure (at-least-once delivery).
public class OutboxDispatcher(
    IServiceProvider services,
    EventBus bus,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<OrdersDb>();

                var pending = await db.Outbox
                    .Where(m => m.ProcessedAt == null)
                    .OrderBy(m => m.OccurredAt)
                    .Take(20)
                    .ToListAsync(stoppingToken);

                foreach (var msg in pending)
                {
                    try
                    {
                        // Re-attach to the trace that created this event so the
                        // publish span is a child of the original order request.
                        ActivityContext.TryParse(msg.TraceParent, null, out var orderContext);
                        using var activity = Telemetry.Messaging.StartActivity(
                            $"publish {msg.Type}", ActivityKind.Producer, orderContext);

                        await bus.PublishRawAsync(msg.Type, msg.Payload,
                            activity?.Context ?? orderContext, stoppingToken);
                        msg.ProcessedAt = DateTime.UtcNow;
                        logger.LogInformation("Dispatched outbox message {Id} ({Type}).", msg.Id, msg.Type);
                    }
                    catch (Exception ex)
                    {
                        msg.Attempts++;
                        logger.LogWarning(ex, "Failed to dispatch outbox message {Id} (attempt {Attempts}).",
                            msg.Id, msg.Attempts);
                    }
                }

                if (pending.Count > 0)
                    await db.SaveChangesAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox dispatch loop error.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}

// Background consumer: listens for "order-shipped" and advances the order's
// status to "Shipped". Uses a DI scope per message to resolve the DbContext.
public class OrderShippedConsumer(
    IServiceProvider services,
    IConfiguration config,
    ILogger<OrderShippedConsumer> logger) : BackgroundService
{
    private readonly string _host = config["RabbitMq:Host"] ?? "rabbitmq";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IConnection? conn = null;
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
            logger.LogError("Could not connect to RabbitMQ. Order-shipped consumer stopping.");
            return;
        }

        var channel = await conn.CreateChannelAsync(cancellationToken: stoppingToken);
        await channel.QueueDeclareAsync("order-shipped", durable: true, exclusive: false, autoDelete: false,
            cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            var parentContext = Telemetry.Extract(ea.BasicProperties);
            using var activity = Telemetry.Messaging.StartActivity(
                "process order-shipped", ActivityKind.Consumer, parentContext);

            var json = Encoding.UTF8.GetString(ea.Body.ToArray());
            try
            {
                var evt = JsonSerializer.Deserialize<OrderShipped>(json);
                if (evt is not null)
                {
                    using var scope = services.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<OrdersDb>();
                    var order = await db.Orders.FindAsync(evt.OrderId);
                    if (order is not null)
                    {
                        order.Status = "Shipped";
                        await db.SaveChangesAsync();
                        logger.LogInformation("Order {OrderId} marked as Shipped.", evt.OrderId);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process order-shipped event: {Json}", json);
            }
        };

        await channel.BasicConsumeAsync("order-shipped", autoAck: true, consumer: consumer,
            cancellationToken: stoppingToken);
        logger.LogInformation("Orders is listening for 'order-shipped' events.");
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }
}

// Health check that verifies the message broker is reachable.
public class RabbitMqHealthCheck(IConfiguration config) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            var factory = new ConnectionFactory { HostName = config["RabbitMq:Host"] ?? "rabbitmq" };
            await using var conn = await factory.CreateConnectionAsync(ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ unreachable", ex);
        }
    }
}

public static class OrdersDbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDb>();
        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync();
                logger.LogInformation("Orders database ready.");
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("DB not ready (attempt {Attempt}/10): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
        throw new Exception("Orders database did not become available in time.");
    }
}

// Exposed so a test project could boot the app via WebApplicationFactory.
public partial class Program { }
