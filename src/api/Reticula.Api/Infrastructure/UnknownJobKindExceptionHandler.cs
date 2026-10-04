using Microsoft.AspNetCore.Diagnostics;
using Reticula.Infrastructure.Jobs;

namespace Reticula.Api.Infrastructure;

public sealed class UnknownJobKindExceptionHandler(IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not UnknownJobKindException) return false;
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = { Status = 400, Title = "Unknown job kind", Detail = exception.Message },
        });
    }
}
