using Reticula.Api;
using Reticula.Api.Auth;
using Reticula.Api.Infrastructure;
using Reticula.Api.Projects;
using Reticula.Infrastructure.Calc;
using Reticula.Infrastructure.Data;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<CalcUnavailableExceptionHandler>();
builder.Services.AddCalcClient(builder.Configuration);
builder.Services.AddReticulaData();
builder.Services.AddReticulaAuth();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin()));

var app = builder.Build();

app.UseExceptionHandler();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapSystemEndpoints();
app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapProjectEndpoints();

await app.InitialiseDatabaseAsync();

app.Run();

public partial class Program;
