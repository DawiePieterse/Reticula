using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Reticula.Domain.Auth;

namespace Reticula.Api.Infrastructure;

public sealed record ClientErrorReport(string Message, string? Stack, string? Url, string? AppVersion);

/// <summary>Browser errors from the PWA, so problems on a field tablet reach the server log.</summary>
public static class ClientErrorEndpoints
{
    private const int MaxField = 4000;

    public static IEndpointRouteBuilder MapClientErrorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/system/client-errors", Report)
            .WithTags("System")
            .RequireAuthorization(Policies.FieldUser);
        return app;
    }

    private static NoContent Report(ClientErrorReport report, HttpContext http, ClaimsPrincipal user, ILoggerFactory loggers)
    {
        loggers.CreateLogger("ClientError").LogWarning(
            "Client error from {UserId} at {Url} (app {AppVersion}, {UserAgent}): {Message}\n{Stack}",
            user.FindFirstValue(ClaimTypes.NameIdentifier),
            Trim(report.Url),
            Trim(report.AppVersion),
            Trim(http.Request.Headers.UserAgent.ToString()),
            Trim(report.Message),
            Trim(report.Stack));
        return TypedResults.NoContent();
    }

    private static string? Trim(string? s) => s is { Length: > MaxField } ? s[..MaxField] + "…" : s;
}
