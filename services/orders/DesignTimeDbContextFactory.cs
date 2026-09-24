using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

// Used by `dotnet ef` at design time so the tool can build the DbContext
// WITHOUT running the app's startup code. The connection string here is only
// for the provider — creating migrations does not connect to a database.
public class OrdersDbContextFactory : IDesignTimeDbContextFactory<OrdersDb>
{
    public OrdersDb CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OrdersDb>()
            .UseNpgsql("Host=localhost;Database=orders;Username=app;Password=pass")
            .Options;
        return new OrdersDb(options);
    }
}
