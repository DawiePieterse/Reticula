using Microsoft.AspNetCore.HttpLogging;
using Reticula.Api;
using Reticula.Api.Auth;
using Reticula.Api.Design;
using Reticula.Api.Field;
using Reticula.Api.Infrastructure;
using Reticula.Api.Jobs;
using Reticula.Api.Layout;
using Reticula.Api.Maps;
using Reticula.Api.Projects;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;
using Reticula.Infrastructure.Design;
using Reticula.Infrastructure.Field;
using Reticula.Infrastructure.Files;
using Reticula.Infrastructure.Jobs;
using Reticula.Infrastructure.Jobs.Handlers;
using Reticula.Infrastructure.Layout;
using Reticula.Infrastructure.Maps;
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
builder.Services.AddTilePacks();
builder.Services.AddJobHandler<LvDesignJob>();
builder.Services.AddScoped<LayoutService>();
builder.Services.AddHttpClient<OverpassClient>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(120);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Reticula/0.1 (electrification design)");
});
builder.Services.AddScoped<FieldService>();
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
app.UseAuthorization();

app.MapSystemEndpoints();
app.MapClientErrorEndpoints();
app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapProjectEndpoints();
app.MapJobEndpoints();
app.MapLayoutEndpoints();
app.MapFieldEndpoints();
app.MapTilePackEndpoints();
app.MapDesignEndpoints();

await app.InitialiseDatabaseAsync();

app.Run();

public partial class Program;
