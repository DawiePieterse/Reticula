using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Reticula.Infrastructure.Audit;

namespace Reticula.Infrastructure.Data;

public static class DataServiceCollectionExtensions
{
    public static IServiceCollection AddReticulaData(this IServiceCollection services)
    {
        // Connection string is resolved lazily so test hosts can override it.
        services.TryAddScoped(_ => new AuditActor());
        services.AddScoped<AuditInterceptor>();
        services.AddDbContext<ReticulaDbContext>((sp, o) =>
        {
            var cs = sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")
                     ?? throw new InvalidOperationException("ConnectionStrings:Default is not set.");
            Configure(o, cs);
            o.AddInterceptors(sp.GetRequiredService<AuditInterceptor>());
        });
        return services;
    }

    internal static void Configure(DbContextOptionsBuilder o, string connectionString) =>
        o.UseNpgsql(connectionString, n => n.UseNetTopologySuite())
         .UseSnakeCaseNamingConvention();
}
