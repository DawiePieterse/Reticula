using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Reticula.Infrastructure.Data;

/// <summary>Used by dotnet-ef only.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ReticulaDbContext>
{
    public ReticulaDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("RETICULA_DB")
                 ?? "Host=localhost;Database=reticula;Username=reticula;Password=reticula";
        var options = new DbContextOptionsBuilder<ReticulaDbContext>();
        DataServiceCollectionExtensions.Configure(options, cs);
        return new ReticulaDbContext(options.Options);
    }
}
