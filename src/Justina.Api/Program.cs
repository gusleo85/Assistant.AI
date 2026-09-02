using Justina.Api.Hosting;
using Justina.Api.Mock;
using Justina.Api.Notifications;
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

// A failed startup must end the process, and in a container it does not do so on its own.
//
// The app is PID 1 there, and PID 1 is exempt from the default action of any signal it has not handled.
// When the runtime meets an unhandled exception it calls abort(), which raises SIGABRT — ignored, because
// PID 1 — so abort() raises it again, forever. What that looks like from outside is a container that
// started, printed a stack trace, never exited, and pinned a core: 15 hours and five CPU-hours in the one
// case we saw. An orchestrator watching it sees a process that started and never crashed, so it never
// restarts it and never alerts. It simply never serves.
//
// Startup validation elsewhere is deliberately fail-fast — a stub seam in Production, a live seam with no
// address, missing identity credentials all throw here rather than let a misconfigured deployment quietly
// file nothing. That design assumes the process actually dies, so this is what makes it true. Exit()
// unwinds normally and sets an exit code; FailFast would abort and land straight back in the same loop.
AppDomain.CurrentDomain.UnhandledException += (_, args) =>
{
    var exception = args.ExceptionObject as Exception;

    // Serilog may not be configured yet — a configuration error throws before the host is built — so the
    // message goes to stderr as well. A fatal error nobody can read is not much of a fatal error.
    Console.Error.WriteLine($"FATAL: {exception}");
    Log.Logger.Fatal(exception, "Justina is stopping: unhandled exception");
    Log.CloseAndFlush();

    Environment.Exit(1);
};

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

// Who recruitment messages go to. One seam, one implementation today — the seeded principal — so
// mapping hiring managers to their own chat accounts later touches nothing but this line.
builder.Services.AddScoped<IRecruitmentRecipientResolver, SeededPrincipalRecipientResolver>();

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
app.MapNotificationEndpoints();
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
