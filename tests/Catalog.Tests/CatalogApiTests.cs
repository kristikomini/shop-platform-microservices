using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Xunit;

// Spins up a REAL PostgreSQL in a throwaway container and boots the actual
// Catalog service against it via WebApplicationFactory — a true end-to-end
// integration test of the HTTP + EF Core + database stack.
public class CatalogFixture : IAsyncLifetime
{
    // Must match the demo values in the Catalog service's appsettings.json.
    private const string JwtKey = "shop-platform-demo-signing-key-change-in-production-0123456789";
    private const string JwtIssuer = "shop-platform";
    private const string JwtAudience = "shop-clients";

#pragma warning disable CS0618 // builder ctor deprecation; WithImage is set explicitly below
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder()
        .WithImage("postgres:16")
        .WithDatabase("catalog")
        .WithUsername("app")
        .WithPassword("pass")
        .Build();
#pragma warning restore CS0618

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;        // anonymous
    public HttpClient AdminClient { get; private set; } = null!;   // Bearer, Admin role
    public HttpClient CustomerClient { get; private set; } = null!; // Bearer, Customer role

    public async Task InitializeAsync()
    {
        await _db.StartAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__CatalogDb", _db.GetConnectionString());
        Factory = new WebApplicationFactory<Program>();

        Client = Factory.CreateClient();
        AdminClient = Factory.CreateClient();
        AdminClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MakeToken("Admin"));
        CustomerClient = Factory.CreateClient();
        CustomerClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MakeToken("Customer"));
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        AdminClient.Dispose();
        CustomerClient.Dispose();
        await Factory.DisposeAsync();
        await _db.DisposeAsync();
    }

    private static string MakeToken(string role)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, role.ToLowerInvariant()),
            new Claim(ClaimTypes.Role, role)
        };
        var token = new JwtSecurityToken(JwtIssuer, JwtAudience, claims,
            expires: DateTime.UtcNow.AddHours(1), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public class CatalogApiTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private readonly HttpClient _client = fixture.Client;
    private readonly HttpClient _admin = fixture.AdminClient;
    private readonly HttpClient _customer = fixture.CustomerClient;

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
    public async Task Post_product_without_token_returns_401()
    {
        var response = await _client.PostAsJsonAsync("/products",
            new { name = "No Auth Widget", price = 1.00m, stock = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Post_product_with_customer_token_returns_403()
    {
        var response = await _customer.PostAsJsonAsync("/products",
            new { name = "Customer Widget", price = 1.00m, stock = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Post_product_as_admin_then_it_is_retrievable()
    {
        var create = await _admin.PostAsJsonAsync("/products",
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
        var created = await (await _admin.PostAsJsonAsync("/products",
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
        var created = await (await _admin.PostAsJsonAsync("/products",
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
        await _admin.PostAsJsonAsync("/products",
            new { name = "Zebra Print Mousepad", price = 9.00m, stock = 5 });

        var results = await _client.GetFromJsonAsync<List<Product>>("/products?search=zebra");

        Assert.NotNull(results);
        Assert.All(results!, p =>
            Assert.Contains("zebra", p.Name, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(results!, p => p.Name == "Zebra Print Mousepad");
    }
}
