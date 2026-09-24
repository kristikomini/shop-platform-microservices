using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;
using Xunit;

// Spins up a REAL PostgreSQL in a throwaway container and boots the actual
// Catalog service against it via WebApplicationFactory — a true end-to-end
// integration test of the HTTP + EF Core + database stack.
public class CatalogFixture : IAsyncLifetime
{
#pragma warning disable CS0618 // builder ctor deprecation; WithImage is set explicitly below
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .WithDatabase("catalog")
        .WithUsername("app")
        .WithPassword("pass")
        .Build();
#pragma warning restore CS0618

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _db.StartAsync();
        // The service reads its connection string from configuration; point it
        // at the container. (Startup then creates the schema and seeds data.)
        Environment.SetEnvironmentVariable("ConnectionStrings__CatalogDb", _db.GetConnectionString());
        Factory = new WebApplicationFactory<Program>();
        Client = Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await _db.DisposeAsync();
    }
}

public class CatalogApiTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private readonly HttpClient _client = fixture.Client;

    [Fact]
    public async Task Get_products_returns_the_seeded_catalog()
    {
        var products = await _client.GetFromJsonAsync<List<Product>>("/products");

        Assert.NotNull(products);
        Assert.True(products!.Count >= 3);
        Assert.Contains(products, p => p.Name == "Mechanical Keyboard");
        Assert.Contains(products, p => p.Name == "USB-C Docking Station");
    }

    [Fact]
    public async Task Get_missing_product_returns_404()
    {
        var response = await _client.GetAsync("/products/99999");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_product_then_it_is_retrievable()
    {
        var create = await _client.PostAsJsonAsync("/products",
            new { name = "HD Webcam", price = 59.00m, stock = 12 });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var created = await create.Content.ReadFromJsonAsync<Product>();
        Assert.NotNull(created);
        Assert.True(created!.Id > 0);

        var fetched = await _client.GetFromJsonAsync<Product>($"/products/{created.Id}");
        Assert.Equal("HD Webcam", fetched!.Name);
        Assert.Equal(59.00m, fetched.Price);
    }

    [Fact]
    public async Task Reserve_decrements_stock_when_available()
    {
        var created = await (await _client.PostAsJsonAsync("/products",
            new { name = "Reserve OK Widget", price = 5.00m, stock = 10 }))
            .Content.ReadFromJsonAsync<Product>();

        var reserve = await _client.PostAsJsonAsync($"/products/{created!.Id}/reserve",
            new { quantity = 3 });

        Assert.Equal(HttpStatusCode.OK, reserve.StatusCode);
        var updated = await reserve.Content.ReadFromJsonAsync<Product>();
        Assert.Equal(7, updated!.Stock);
    }

    [Fact]
    public async Task Reserve_rejects_and_leaves_stock_untouched_when_insufficient()
    {
        var created = await (await _client.PostAsJsonAsync("/products",
            new { name = "Low Stock Widget", price = 5.00m, stock = 2 }))
            .Content.ReadFromJsonAsync<Product>();

        var reserve = await _client.PostAsJsonAsync($"/products/{created!.Id}/reserve",
            new { quantity = 5 });

        Assert.Equal(HttpStatusCode.Conflict, reserve.StatusCode);

        var after = await _client.GetFromJsonAsync<Product>($"/products/{created.Id}");
        Assert.Equal(2, after!.Stock); // unchanged
    }

    [Fact]
    public async Task Search_filters_products_by_name()
    {
        await _client.PostAsJsonAsync("/products",
            new { name = "Zebra Print Mousepad", price = 9.00m, stock = 5 });

        var results = await _client.GetFromJsonAsync<List<Product>>("/products?search=zebra");

        Assert.NotNull(results);
        Assert.All(results!, p =>
            Assert.Contains("zebra", p.Name, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(results!, p => p.Name == "Zebra Print Mousepad");
    }
}
