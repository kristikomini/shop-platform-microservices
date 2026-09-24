using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// This service owns its OWN database. No other service reads these tables
// directly — they must ask Catalog over HTTP. That is the core microservices rule.
builder.Services.AddDbContext<CatalogDb>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("CatalogDb")));

// OpenAPI document + a readiness health check that verifies the database.
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<CatalogDb>("catalog-db");

// Distributed tracing. "Npgsql" is Npgsql's built-in ActivitySource, so we get
// database spans without an extra instrumentation package.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("catalog"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource("Npgsql")
        .AddOtlpExporter())
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddRuntimeInstrumentation()
        .AddPrometheusExporter());   // exposes /metrics for Prometheus to scrape

// JWT bearer authentication (tokens issued by the auth service). Reads stay
// public; creating a product requires the Admin role.
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
builder.Services.AddAuthorization(o => o.AddPolicy("admin", p => p.RequireRole("Admin")));

var app = builder.Build();

// Create the schema and seed demo data on startup, retrying until the DB
// container is accepting connections (containers start in parallel).
await DbInitializer.InitializeAsync(app.Services, app.Logger);

// API docs: OpenAPI JSON at /openapi/v1.json, interactive UI at /scalar/v1
app.MapOpenApi();
app.MapScalarApiReference(o => o.WithTitle("Catalog API"));

// Health probe used by Docker Compose and the gateway to gate readiness.
app.MapHealthChecks("/health");

// Prometheus scrape endpoint at /metrics.
app.MapPrometheusScrapingEndpoint();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/products", async (string? search, CatalogDb db) =>
{
    var query = db.Products.AsQueryable();
    if (!string.IsNullOrWhiteSpace(search))
        query = query.Where(p => EF.Functions.ILike(p.Name, $"%{search}%"));
    return await query.OrderBy(p => p.Id).ToListAsync();
})
    .WithName("GetProducts").WithTags("Catalog");

app.MapGet("/products/{id:int}", async (int id, CatalogDb db) =>
    await db.Products.FindAsync(id) is Product p
        ? Results.Ok(p)
        : Results.NotFound())
    .WithName("GetProduct").WithTags("Catalog");

// Atomically reserve stock: decrement only if enough is available. The WHERE
// guard + rows-affected check makes this safe under concurrent orders without
// a read-modify-write race. Catalog owns stock — Orders asks it to reserve.
app.MapPost("/products/{id:int}/reserve", async (int id, ReserveStock req, CatalogDb db) =>
{
    if (req.Quantity < 1)
        return Results.BadRequest("Quantity must be at least 1.");

    var affected = await db.Products
        .Where(p => p.Id == id && p.Stock >= req.Quantity)
        .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, p => p.Stock - req.Quantity));

    if (affected == 0)
    {
        var exists = await db.Products.AnyAsync(p => p.Id == id);
        return exists
            ? Results.Conflict($"Insufficient stock for product {id}.")
            : Results.NotFound();
    }

    var product = await db.Products.AsNoTracking().FirstAsync(p => p.Id == id);
    return Results.Ok(product);
})
    .WithName("ReserveStock").WithTags("Catalog");

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
})
    .RequireAuthorization("admin")
    .WithName("CreateProduct").WithTags("Catalog");

app.Run();

// ---------------------------------------------------------------------------

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public int Stock { get; set; }
}

public record ReserveStock(int Quantity);

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
                await db.Database.MigrateAsync();
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

// Exposed so the integration test project can boot the app via WebApplicationFactory.
public partial class Program { }
