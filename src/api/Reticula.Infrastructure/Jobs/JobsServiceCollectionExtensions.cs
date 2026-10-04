using Hangfire;
using Hangfire.AspNetCore;
using Hangfire.PostgreSql;
using Hangfire.PostgreSql.Factories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Reticula.Infrastructure.Jobs;

public static class JobsServiceCollectionExtensions
{
    /// <summary>
    /// Hangfire on the same Postgres database (schema "hangfire"). Settings under "Jobs":
    /// WorkerCount, PollIntervalMs, CancellationCheckMs, RunServer.
    /// </summary>
    public static IServiceCollection AddReticulaJobs(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IJobNotifier, NullJobNotifier>();
        services.AddScoped<IJobQueue, HangfireJobQueue>();
        services.AddScoped<JobExecutor>();

        // Storage and activator are registered per container rather than through Hangfire's statics,
        // so each host (and each test host) uses its own database and services.
        services.AddSingleton<JobStorage>(sp =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var cs = config.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default is not set.");
            var options = new PostgreSqlStorageOptions
            {
                SchemaName = "hangfire",
                PrepareSchemaIfNecessary = true,
                QueuePollInterval = TimeSpan.FromMilliseconds(config.GetValue("Jobs:PollIntervalMs", 1000)),
            };
            return new PostgreSqlStorage(new NpgsqlConnectionFactory(cs, options), options);
        });

        // Hangfire otherwise resolves JobActivator.Current, a static set by whichever host configured last.
        services.AddSingleton<JobActivator>(sp => new AspNetCoreJobActivator(sp.GetRequiredService<IServiceScopeFactory>()));

        services.AddHangfire(c => c
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings());

        services.AddHangfireServer((sp, o) =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            o.WorkerCount = config.GetValue("Jobs:WorkerCount", 2);
            o.CancellationCheckInterval = TimeSpan.FromMilliseconds(config.GetValue("Jobs:CancellationCheckMs", 5000));
            o.ServerName = $"reticula-{Environment.MachineName}";
        });

        return services;
    }

    public static IServiceCollection AddJobHandler<T>(this IServiceCollection services) where T : class, IJobHandler
    {
        services.AddScoped<IJobHandler, T>();
        return services;
    }
}
