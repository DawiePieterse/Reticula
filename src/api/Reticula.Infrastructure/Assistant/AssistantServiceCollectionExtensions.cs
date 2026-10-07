using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Reticula.Infrastructure.Assistant;

public static class AssistantServiceCollectionExtensions
{
    /// <summary>
    /// The design assistant (Phase 8). Settings are read per request, so it can be turned on or off without a restart; when it is off,
    /// nothing reaches the model service and every assistant endpoint says so (plan 8.5).
    /// </summary>
    public static IServiceCollection AddAssistant(this IServiceCollection services)
    {
        services.AddScoped(sp => AssistantSettings.From(sp.GetRequiredService<IConfiguration>()));
        services.AddHttpClient<IAssistantModel, AnthropicModel>(c => c.Timeout = TimeSpan.FromSeconds(120));
        services.AddScoped<AssistantTools>();
        services.AddScoped<AssistantService>();
        return services;
    }
}
