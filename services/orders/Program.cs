using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;

var builder = WebApplication.CreateBuilder(args);

// Orders owns its own database, separate from Catalog's.
builder.Services.AddDbContext<OrdersDb>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("OrdersDb")));

// Typed HTTP client pointed at the Catalog SERVICE (by DNS name, set in compose).
// Orders asks Catalog over the network — it never touches Catalog's database.
builder.Services.AddHttpClient("catalog", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:Catalog"]!));

// Publishes domain events to RabbitMQ so other services can react asynchronously.
builder.Services.AddSingleton<EventBus>();

var app = builder.Build();

await OrdersDbInitializer.InitializeAsync(app.Services, app.Logger);

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "orders" }));

app.MapGet("/orders", async (OrdersDb db) =>
    await db.Orders.OrderByDescending(o => o.PlacedAt).ToListAsync());

app.MapPost("/orders", async (
    CreateOrder cmd,
    OrdersDb db,
    IHttpClientFactory http,
    EventBus bus,
    ILogger<Program> logger) =>
{
    // --- SYNCHRONOUS communication: verify the product with Catalog ---
    var catalog = http.CreateClient("catalog");
    var response = await catalog.GetAsync($"/products/{cmd.ProductId}");
    if (!response.IsSuccessStatusCode)
        return Results.BadRequest($"Unknown product {cmd.ProductId}");

    var product = await response.Content.ReadFromJsonAsync<ProductDto>();
    if (product is null)
        return Results.BadRequest("Invalid product response");

    var order = new Order
    {
        Id = Guid.NewGuid(),
        ProductId = product.Id,
        ProductName = product.Name,
        UnitPrice = product.Price,
        Quantity = cmd.Quantity,
        PlacedAt = DateTime.UtcNow
    };
    db.Orders.Add(order);
    await db.SaveChangesAsync();

    // --- ASYNCHRONOUS communication: announce the event, don't wait for anyone ---
    await bus.PublishAsync("order-placed", order);
    logger.LogInformation("Order {OrderId} placed and event published.", order.Id);

    return Results.Created($"/orders/{order.Id}", order);
});

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
