using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// This service owns its OWN database. No other service reads these tables
// directly — they must ask Catalog over HTTP. That is the core microservices rule.
builder.Services.AddDbContext<CatalogDb>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("CatalogDb")));

var app = builder.Build();

// Create the schema and seed demo data on startup, retrying until the DB
// container is accepting connections (containers start in parallel).
await DbInitializer.InitializeAsync(app.Services, app.Logger);

// Liveness probe — the orchestrator/gateway uses this to know we are up.
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "catalog" }));

app.MapGet("/products", async (CatalogDb db) =>
    await db.Products.OrderBy(p => p.Id).ToListAsync());

app.MapGet("/products/{id:int}", async (int id, CatalogDb db) =>
    await db.Products.FindAsync(id) is Product p
        ? Results.Ok(p)
        : Results.NotFound());

app.MapPost("/products", async (Product input, CatalogDb db) =>
{
    var product = new Product
    {
        Name = input.Name,
        Price = input.Price,
        Stock = input.Stock
    };
    db.Products.Add(product);
    await db.SaveChangesAsync();
    return Results.Created($"/products/{product.Id}", product);
});

app.Run();

// ---------------------------------------------------------------------------

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public int Stock { get; set; }
}

public class CatalogDb(DbContextOptions<CatalogDb> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
}

public static class DbInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, ILogger logger)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CatalogDb>();

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            try
            {
                await db.Database.EnsureCreatedAsync();
                if (!await db.Products.AnyAsync())
                {
                    db.Products.AddRange(
                        new Product { Name = "Mechanical Keyboard", Price = 89.90m, Stock = 40 },
                        new Product { Name = "27\" 4K Monitor",      Price = 329.00m, Stock = 15 },
                        new Product { Name = "USB-C Docking Station", Price = 129.50m, Stock = 25 });
                    await db.SaveChangesAsync();
                }
                logger.LogInformation("Catalog database ready.");
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("DB not ready (attempt {Attempt}/10): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
        throw new Exception("Catalog database did not become available in time.");
    }
}
