using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

// Used by `dotnet ef` at design time so the tool can build the DbContext
// WITHOUT running the app's startup code. The connection string here is only
// for the provider — creating migrations does not connect to a database.
public class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDb>
{
    public CatalogDb CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CatalogDb>()
            .UseNpgsql("Host=localhost;Database=catalog;Username=app;Password=pass")
            .Options;
        return new CatalogDb(options);
    }
}
