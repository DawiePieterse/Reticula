using System.Security.Claims;
using Microsoft.AspNetCore.HttpLogging;
using Reticula.Api;
using Reticula.Api.Auth;
using Reticula.Api.Costs;
using Reticula.Api.Design;
using Reticula.Api.Documents;
using Reticula.Api.Field;
using Reticula.Api.Infrastructure;
using Reticula.Api.Jobs;
using Reticula.Api.Layout;
using Reticula.Api.Maps;
using Reticula.Api.Projects;
using Reticula.Api.Review;
using Reticula.Infrastructure.Audit;
using Reticula.Infrastructure.Calc;
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
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped(sp =>
{
    var http = sp.GetRequiredService<IHttpContextAccessor>();
    return new AuditActor(() => Guid.TryParse(http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null);
});
builder.Services.AddReticulaData();
builder.Services.AddReticulaAuth();
builder.Services.AddSignalR();
builder.Services.AddSingleton<IJobNotifier, SignalRJobNotifier>();
builder.Services.AddReticulaJobs();
builder.Services.AddJobHandler<DiagnosticsJob>();
builder.Services.AddTilePacks();
builder.Services.AddScoped<DesignInputs>();
builder.Services.AddJobHandler<LvDesignJob>();
builder.Services.AddJobHandler<MvDesignJob>();
builder.Services.AddJobHandler<BulkStudyJob>();
builder.Services.AddJobHandler<OptionSearchJob>();
builder.Services.AddScoped<DocumentGenerator>();
builder.Services.AddJobHandler<DocumentsJob>();
builder.Services.AddScoped<AssumptionRegister>();
builder.Services.AddScoped<ReviewReadiness>();
builder.Services.AddJobHandler<RevisionIssueJob>();
builder.Services.AddJobHandler<RevisionReproduceJob>();
builder.Services.AddJobHandler<ProjectExportJob>();
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
app.MapBulkEndpoints();
app.MapRateListEndpoints();
app.MapDocumentEndpoints();
app.MapReviewEndpoints();

await app.InitialiseDatabaseAsync();

app.Run();

public partial class Program;
