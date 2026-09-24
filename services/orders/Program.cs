using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;
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

    // --- SYNCHRONOUS communication: verify the product with Catalog ---
    var catalog = http.CreateClient("catalog");
    var response = await catalog.GetAsync($"/products/{cmd.ProductId}");
    if (!response.IsSuccessStatusCode)
        return Results.BadRequest($"Unknown product {cmd.ProductId}");

    var product = await response.Content.ReadFromJsonAsync<ProductDto>();
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
