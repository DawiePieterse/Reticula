using Microsoft.EntityFrameworkCore;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Identity;

namespace Reticula.Api.Infrastructure;

public static class DatabaseInitialiser
{
    public static async Task InitialiseDatabaseAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseInitialiser");
        if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
        {
            await scope.ServiceProvider.GetRequiredService<ReticulaDbContext>().Database.MigrateAsync();
        }
        await IdentitySeeder.SeedAsync(scope.ServiceProvider, app.Configuration, logger);
    }
}
