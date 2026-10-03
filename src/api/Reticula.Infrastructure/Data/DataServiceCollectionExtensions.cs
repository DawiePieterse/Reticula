using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Reticula.Infrastructure.Data;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddReticulaData(this IServiceCollection services)
    {
        // Connection string is resolved lazily so test hosts can override it.
        services.AddDbContext<ReticulaDbContext>((sp, o) =>
        {
            var cs = sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")
                     ?? throw new InvalidOperationException("ConnectionStrings:Default is not set.");
            Configure(o, cs);
        });
        return services;
    }

    internal static void Configure(DbContextOptionsBuilder o, string connectionString) =>
        o.UseNpgsql(connectionString, n => n.UseNetTopologySuite())
         .UseSnakeCaseNamingConvention();
}
