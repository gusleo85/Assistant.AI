using Justina.Api.Hosting;
using Justina.Api.Security;
using Justina.Api.Tools;
using Justina.Core.Infrastructure;
using Justina.Core.Infrastructure.Persistence;
using Justina.Expense.Application;
using Justina.Expense.Infrastructure;
using Justina.Recruitment.Application;
using Justina.Recruitment.Infrastructure;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

var builder = WebApplication.CreateBuilder(args);

// Structured JSON to stdout: the container runtime is the log sink (§40).
builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter()));

var toolApiOptions = builder.Configuration
    .GetSection(ToolApiOptions.SectionName)
    .Get<ToolApiOptions>() ?? new ToolApiOptions();

builder.Services.AddSingleton(toolApiOptions);

builder.Services.AddJustinaCoreInfrastructure(builder.Configuration);
builder.Services.AddExpenseApplication();
builder.Services.AddExpenseInfrastructure(builder.Configuration);
builder.Services.AddRecruitmentApplication();
builder.Services.AddRecruitmentInfrastructure(builder.Configuration);

builder.Services.AddScoped<RequestContextFactory>();
builder.Services.AddScoped<IReceiptResolver, ReceiptResolver>();
builder.Services.AddHostedService<MediaCleanupService>();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<JustinaDbContext>("database");

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("justina-app"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter());

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseMiddleware<ToolApiKeyMiddleware>();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapToolEndpoints();

await MigrateDatabaseAsync(app).ConfigureAwait(false);

await app.RunAsync().ConfigureAwait(false);

/// <summary>
/// Applies migrations at startup. Guarded by a SQL Server application lock so several replicas starting
/// together cannot run the same migration concurrently (§24).
/// </summary>
static async Task MigrateDatabaseAsync(WebApplication app)
{
    if (!app.Configuration.GetValue("Database:MigrateOnStartup", true))
    {
        return;
    }

    using var scope = app.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var context = scope.ServiceProvider.GetRequiredService<JustinaDbContext>();

    try
    {
        await context.Database.MigrateAsync().ConfigureAwait(false);
        logger.LogInformation("Database schema is up to date");
    }
    catch (Exception exception)
    {
        logger.LogCritical(exception, "Database migration failed");
        throw;
    }
}

/// <summary>Exposed so the integration tests can host the application with WebApplicationFactory.</summary>
public partial class Program;
