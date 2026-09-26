using Hispania.Library.Persistence.EFCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Hispania.Library.Persistence.PostgreSql;

public sealed class PostgreSqlDesignTimeFactory : IDesignTimeDbContextFactory<LibraryCatalogDbContext>
{
    public LibraryCatalogDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("HISPANIA_POSTGRES_DESIGN_CONNECTION")
            ?? "Host=localhost;Database=hispania_design;Username=hispania;Password=design-only";
        var options = new DbContextOptionsBuilder<LibraryCatalogDbContext>()
            .UseNpgsql(connectionString, postgres => postgres.MigrationsAssembly(typeof(PostgreSqlLibraryPersistence).Assembly.FullName))
            .Options;
        return new LibraryCatalogDbContext(options);
    }
}
