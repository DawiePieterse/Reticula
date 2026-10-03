using Reticula.Infrastructure.Calc;

namespace Reticula.Api;

public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/system").WithTags("System");

        group.MapGet("/health", async (ICalcClient calc, CancellationToken ct) =>
        {
            var calcOk = await calc.IsHealthyAsync(ct);
            return Results.Ok(new HealthResponse("ok", calcOk ? "ok" : "unavailable"));
        }).WithName("Health");

        group.MapGet("/rules", async (ICalcClient calc, CancellationToken ct) =>
            Results.Ok(await calc.ListRulesAsync(ct))).WithName("ListRules");

        return app;
    }
}

public sealed record HealthResponse(string Api, string Calc);
