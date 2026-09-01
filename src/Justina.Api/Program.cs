using Justina.Api.Hosting;
using Justina.Api.Mock;
using Justina.Api.Security;
using Justina.Api.Tools;
using ModelContextProtocol.Server;
using Justina.Core.Infrastructure;
using Justina.Core.Infrastructure.Persistence;
using Justina.Core.Infrastructure.Security;
using Justina.Expense.Application;
using Justina.Expense.Infrastructure;
using Justina.Recruitment.Application;
using Justina.Recruitment.Infrastructure;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
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
    // Default HttpClient logging records request URIs, which for some channels contain the credential.
    .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
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

// OpenClaw reaches Justina over MCP — it has no configuration for calling a plain HTTP JSON API.
// The REST endpoints under /tools stay for testing and non-MCP clients; both funnel into the same
// commands and queries, so authorization and state cannot diverge between them.
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();
builder.Services.AddHostedService<MediaCleanupService>();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<JustinaDbContext>("database");

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("justina-app"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation(options =>
        {
            // Spans record the full request URL, and the Telegram bot token lives in the URL path.
            // Overwrite the recorded URL with a scrubbed one rather than dropping the span (§40).
            options.EnrichWithHttpRequestMessage = (activity, request) =>
                activity.SetTag("url.full", SecretScrubber.Redact(request.RequestUri));
        })
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter());

var app = builder.Build();

app.UseSerilogRequestLogging();

// Outermost: an unhandled exception must still leave the tool contract intact.
app.UseMiddleware<UnhandledExceptionMiddleware>();
app.UseMiddleware<ToolApiKeyMiddleware>();

// Liveness must be shallow: it answers "is this process alive", not "is the database reachable".
// Including the database check here would make a transient outage look like a dead container, and
// justina-openclaw — which waits for this to be healthy — would never start.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

// Readiness is the one that covers dependencies (§25).
app.MapHealthChecks("/health/ready");
app.MapToolEndpoints();
app.MapMcp("/mcp");

// A stand-in for the Expense endpoint that does not exist yet. Only mounted when the submission seam is
// in Mock; startup already refuses Mock in Production, so this cannot appear there.
if (string.Equals(builder.Configuration["ExpenseApi:Mode"], "Mock", StringComparison.OrdinalIgnoreCase)
    || string.Equals(builder.Configuration["ExpenseApi:SubmissionMode"], "Mock", StringComparison.OrdinalIgnoreCase))
{
    app.MapMockExpenseApi();
    app.Logger.LogWarning(
        "MOCK Expense API mounted at /mock/expense/v1. Submissions are accepted and discarded.");
}

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

        // An unmapped user holds no capabilities, so without this a fresh environment has nobody who can
        // do anything. Seeding is configuration-driven and idempotent (§20).
        await scope.ServiceProvider
            .GetRequiredService<PrincipalSeeder>()
            .SeedAsync(CancellationToken.None)
            .ConfigureAwait(false);
    }
    catch (Exception exception)
    {
        logger.LogCritical(exception, "Database migration failed");
        throw;
    }
}

/// <summary>Exposed so the integration tests can host the application with WebApplicationFactory.</summary>
public partial class Program;
