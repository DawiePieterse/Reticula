using Microsoft.AspNetCore.Diagnostics;
using Reticula.Infrastructure.Calc;

namespace Reticula.Api.Infrastructure;

/// <summary>Maps an unreachable calc service to 503 so the client can tell the user, rather than a generic 500.</summary>
public sealed class CalcUnavailableExceptionHandler(IProblemDetailsService problems, ILogger<CalcUnavailableExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not CalcUnavailableException) return false;
        logger.LogWarning(exception, "Calc service unavailable");
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = { Status = 503, Title = "Calculation service unavailable", Detail = exception.Message },
        });
    }
}
