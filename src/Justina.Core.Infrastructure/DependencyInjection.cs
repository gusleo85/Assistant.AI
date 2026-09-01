using Justina.Core.Application.Abstractions;
using Justina.Core.Application.Channels;
using Justina.Core.Application.Documents;
using Justina.Core.Application.Messaging;
using Justina.Core.Application.Session;
using Justina.Core.Application.Vision;
using Justina.Core.Infrastructure.Channels;
using Justina.Core.Infrastructure.Channels.Telegram;
using Justina.Core.Infrastructure.Channels.WhatsApp;
using Justina.Core.Infrastructure.Documents;
using Justina.Core.Infrastructure.Media;
using Justina.Core.Infrastructure.Persistence;
using Justina.Core.Infrastructure.Security;
using Justina.Core.Infrastructure.Vision;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Justina.Core.Infrastructure;

public static class CoreInfrastructureServiceCollectionExtensions
{
    /// <summary>
    /// Binds every concrete implementation. This is the only place SDK types are named, which is what keeps
    /// them out of the domain and application layers (§12).
    /// </summary>
    public static IServiceCollection AddJustinaCoreInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<DocumentProcessingOptions>(configuration.GetSection(DocumentProcessingOptions.SectionName));
        services.Configure<MediaStoreOptions>(configuration.GetSection(MediaStoreOptions.SectionName));
        services.Configure<StagedMediaOptions>(configuration.GetSection(StagedMediaOptions.SectionName));
        services.Configure<OpenAiVisionOptions>(configuration.GetSection(OpenAiVisionOptions.SectionName));
        services.Configure<TelegramOptions>(configuration.GetSection(TelegramOptions.SectionName));
        services.Configure<WhatsAppOptions>(configuration.GetSection(WhatsAppOptions.SectionName));
        services.Configure<PrincipalSeedOptions>(configuration.GetSection(PrincipalSeedOptions.SectionName));

        services.AddDbContext<JustinaDbContext>((provider, builder) =>
            builder.UseSqlServer(
                configuration.GetConnectionString("Justina"),
                sql => sql.EnableRetryOnFailure(maxRetryCount: 5, TimeSpan.FromSeconds(10), null)));

        // Applies to every HttpClient, including ones added later: a credential-bearing header is never
        // written to the logs even if a future client enables request logging (§40).
        services.ConfigureHttpClientDefaults(builder =>
            builder.RedactLoggedHeaders(SecretScrubber.IsSensitiveHeader));

        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IConversationStateStore, SqlServerConversationStateStore>();
        services.AddScoped<IIdempotencyStore, SqlServerIdempotencyStore>();
        services.AddScoped<IInboundMessageDeduplicator, SqlServerInboundMessageDeduplicator>();
        services.AddScoped<IAuthorizationService, AuthorizationService>();
        services.AddScoped<PrincipalSeeder>();

        services.AddSingleton<IMediaStore, FileSystemMediaStore>();
        services.AddSingleton<IStagedMediaReader, StagedMediaReader>();
        services.AddSingleton<IPdfPageRenderer, PdfiumPageRenderer>();
        services.AddScoped<IDocumentProcessor, DocumentProcessor>();

        services.AddScoped<IDispatcher, Dispatcher>();
        services.AddQueryHandler<GetSessionContextQuery, SessionContext, GetSessionContextQueryHandler>();

        AddVision(services);
        AddChannels(services);

        return services;
    }

    private static void AddVision(IServiceCollection services)
    {
        services.AddHttpClient<IVisionProvider, OpenAiVisionProvider>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<OpenAiVisionOptions>>().Value;

            client.BaseAddress = new Uri($"{options.BaseUrl.TrimEnd('/')}/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {options.ApiKey}");
        });
    }

    private static void AddChannels(IServiceCollection services)
    {
        // RemoveAllLoggers: Telegram carries the bot token in the URL path, and the default HttpClient
        // loggers record request URIs. Suppressing them here is the only way to keep the token out of the
        // logs entirely; the adapters log their own status-code-only lines instead (§40).
        services.AddHttpClient<TelegramMediaDownloader>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<TelegramOptions>>().Value;
            client.BaseAddress = new Uri($"{options.ApiBaseUrl.TrimEnd('/')}/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        }).RemoveAllLoggers();

        services.AddHttpClient<TelegramResponder>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<TelegramOptions>>().Value;
            client.BaseAddress = new Uri($"{options.ApiBaseUrl.TrimEnd('/')}/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        }).RemoveAllLoggers();

        services.AddHttpClient<WhatsAppMediaDownloader>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<WhatsAppOptions>>().Value;
            client.BaseAddress = new Uri($"{options.GraphBaseUrl.TrimEnd('/')}/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {options.AccessToken}");
        });

        services.AddHttpClient<WhatsAppResponder>((provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<WhatsAppOptions>>().Value;
            client.BaseAddress = new Uri($"{options.GraphBaseUrl.TrimEnd('/')}/");
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {options.AccessToken}");
        });

        services.AddScoped<IChannelMediaDownloader>(p => p.GetRequiredService<TelegramMediaDownloader>());
        services.AddScoped<IChannelMediaDownloader>(p => p.GetRequiredService<WhatsAppMediaDownloader>());
        services.AddScoped<IChannelResponder>(p => p.GetRequiredService<TelegramResponder>());
        services.AddScoped<IChannelResponder>(p => p.GetRequiredService<WhatsAppResponder>());
        services.AddScoped<IChannelRegistry, ChannelRegistry>();
    }
}
