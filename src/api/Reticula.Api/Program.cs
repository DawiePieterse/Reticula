using Microsoft.AspNetCore.HttpLogging;
using Reticula.Api;
using Reticula.Api.Assistant;
using Reticula.Api.Auth;
using Reticula.Api.Costing;
using Reticula.Api.Design;
using Reticula.Api.Documents;
using Reticula.Api.Field;
using Reticula.Api.Infrastructure;
using Reticula.Api.Jobs;
using Reticula.Api.Layout;
using Reticula.Api.Maps;
using Reticula.Api.Projects;
using Reticula.Api.Review;
using Reticula.Infrastructure.Assistant;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Costing;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Design;
using Reticula.Infrastructure.Documents;
using Reticula.Infrastructure.Field;
using Reticula.Infrastructure.Files;
using Reticula.Infrastructure.Jobs;
using Reticula.Infrastructure.Jobs.Handlers;
using Reticula.Infrastructure.Layout;
using Reticula.Infrastructure.Maps;
using Reticula.Infrastructure.Review;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Structured JSON logs outside development; request trace ids flow to the calc service via traceparent.
if (!builder.Environment.IsDevelopment())
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(o =>
    {
        o.IncludeScopes = true;
        o.UseUtcTimestamp = true;
        o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
    });
}
builder.Services.AddHttpLogging(o =>
{
    o.LoggingFields = HttpLoggingFields.RequestMethod | HttpLoggingFields.RequestPath
                      | HttpLoggingFields.ResponseStatusCode | HttpLoggingFields.Duration;
    o.CombineLogs = true;
});

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<CalcUnavailableExceptionHandler>();
builder.Services.AddExceptionHandler<UnknownJobKindExceptionHandler>();
builder.Services.AddCalcClient(builder.Configuration);
builder.Services.AddReticulaData();
builder.Services.AddReticulaAuth();
builder.Services.AddSignalR();
builder.Services.AddSingleton<IJobNotifier, SignalRJobNotifier>();
builder.Services.AddReticulaJobs();
builder.Services.AddJobHandler<DiagnosticsJob>();
builder.Services.AddJobHandler<MapPackJob>();
builder.Services.AddJobHandler<PlacementJob>();
builder.Services.AddJobHandler<DesignRunJob>();
builder.Services.AddJobHandler<DocumentJob>();
builder.Services.AddScoped<LayoutService>();
builder.Services.AddScoped<FieldService>();
builder.Services.AddScoped<LvNetworkService>();
builder.Services.AddScoped<RateService>();
builder.Services.AddScoped<DesignInputsBuilder>();
builder.Services.AddScoped<ReviewService>();
builder.Services.AddScoped<ProjectExport>();
builder.Services.AddAssistant();
builder.Services.AddSingleton<IFileStore, FileSystemFileStore>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseHttpLogging();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseCors();
app.UseAuthentication();
// The audit trail (plan 7.4) records the signed-in user against every change the request saves.
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true)
        context.RequestServices.GetRequiredService<AuditActor>().UserId = context.User.UserId();
    await next(context);
});
app.UseAuthorization();

app.MapSystemEndpoints();
app.MapClientErrorEndpoints();
app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapProjectEndpoints();
app.MapJobEndpoints();
app.MapLayoutEndpoints();
app.MapFieldEndpoints();
app.MapMapPackEndpoints();
app.MapLvNetworkEndpoints();
app.MapPlacementEndpoints();
app.MapDesignEndpoints();
app.MapRateEndpoints();
app.MapDocumentEndpoints();
app.MapReviewEndpoints();
app.MapAssistantEndpoints();

await app.InitialiseDatabaseAsync();

app.Run();

public partial class Program;
