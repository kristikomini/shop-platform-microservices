using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Scalar.AspNetCore;

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

// OpenAPI + health checks (database reachable AND broker reachable).
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<OrdersDb>("orders-db")
    .AddCheck<RabbitMqHealthCheck>("rabbitmq");

var app = builder.Build();

await OrdersDbInitializer.InitializeAsync(app.Services, app.Logger);

app.MapOpenApi();
app.MapScalarApiReference(o => o.WithTitle("Orders API"));
app.MapHealthChecks("/health");

app.MapGet("/orders", async (OrdersDb db) =>
    await db.Orders.OrderByDescending(o => o.PlacedAt).ToListAsync())
    .WithName("GetOrders").WithTags("Orders");

app.MapPost("/orders", async (
    CreateOrder cmd,
    OrdersDb db,
    IHttpClientFactory http,
    EventBus bus,
    ILogger<Program> logger) =>
{
    if (cmd.Quantity < 1)
        return Results.BadRequest("Quantity must be at least 1.");

    // --- SYNCHRONOUS communication: ask Catalog to RESERVE stock ---
    // Catalog owns stock, so Orders asks it to atomically decrement. A 409 means
    // there wasn't enough — the order is rejected before anything is persisted.
    var catalog = http.CreateClient("catalog");
    var reserve = await catalog.PostAsJsonAsync(
        $"/products/{cmd.ProductId}/reserve", new { quantity = cmd.Quantity });

    if (reserve.StatusCode == System.Net.HttpStatusCode.Conflict)
        return Results.BadRequest($"Insufficient stock for product {cmd.ProductId}.");
    if (reserve.StatusCode == System.Net.HttpStatusCode.NotFound)
        return Results.BadRequest($"Unknown product {cmd.ProductId}.");
    if (!reserve.IsSuccessStatusCode)
        return Results.BadRequest("Could not reserve stock.");

    var product = await reserve.Content.ReadFromJsonAsync<ProductDto>();
    if (product is null)
        return Results.BadRequest("Invalid product response");

    var order = OrderFactory.Create(Guid.NewGuid(), product, cmd.Quantity, DateTime.UtcNow);
    db.Orders.Add(order);
    await db.SaveChangesAsync();

    // --- ASYNCHRONOUS communication: announce the event, don't wait for anyone ---
    await bus.PublishAsync("order-placed", order);
    logger.LogInformation("Order {OrderId} placed and event published.", order.Id);

    return Results.Created($"/orders/{order.Id}", order);
})
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

    protected override void OnModelCreating(ModelBuilder b) =>
        b.Entity<Order>().Ignore(o => o.Total); // computed, not stored
}

// Thin wrapper over RabbitMQ. In a bigger system this would live in a shared
// contracts library so every service serializes events the same way.
public class EventBus(IConfiguration config, ILogger<EventBus> logger)
{
    private readonly string _host = config["RabbitMq:Host"] ?? "rabbitmq";

    public async Task PublishAsync<T>(string queue, T message)
    {
        var factory = new ConnectionFactory { HostName = _host };
        await using var conn = await factory.CreateConnectionAsync();
        await using var channel = await conn.CreateChannelAsync();
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false);

        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        await channel.BasicPublishAsync(exchange: "", routingKey: queue, body: body);
        logger.LogInformation("Published event to queue '{Queue}'.", queue);
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

public record OrderShipped(Guid OrderId);

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
                await db.Database.EnsureCreatedAsync();
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
