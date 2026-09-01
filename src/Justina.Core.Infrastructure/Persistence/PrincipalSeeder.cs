using System.Text.Json;
using Justina.Core.Domain.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Justina.Core.Infrastructure.Persistence;

public sealed class PrincipalSeedOptions
{
    public const string SectionName = "Seed";

    /// <summary>
    /// A JSON array of principals to ensure exist at startup, supplied as a single string so it can be
    /// set from one environment variable:
    ///
    /// <code>
    /// Seed__Principals=[{"channel":"telegram","userId":"12345","displayName":"Gus",
    ///                    "capabilities":["expense.submit","expense.read"]}]
    /// </code>
    ///
    /// Justina refuses everything for an unmapped user, which is correct but makes a fresh environment
    /// unusable until someone writes a row by hand. This is the supported way to grant that first access.
    /// </summary>
    public string Principals { get; set; } = string.Empty;
}

public sealed record PrincipalSeed(
    string Channel,
    string UserId,
    string DisplayName,
    IReadOnlyList<string> Capabilities);

/// <summary>
/// Upserts configured principals. Idempotent: existing rows have their name and capabilities updated
/// rather than being duplicated, so re-running with a changed configuration converges.
/// </summary>
public sealed class PrincipalSeeder(
    JustinaDbContext context,
    IOptions<PrincipalSeedOptions> options,
    ILogger<PrincipalSeeder> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var raw = options.Value.Principals;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        PrincipalSeed[]? seeds;

        try
        {
            seeds = JsonSerializer.Deserialize<PrincipalSeed[]>(raw, JsonOptions);
        }
        catch (JsonException exception)
        {
            // Bad configuration must be loud but must not stop the service from starting.
            logger.LogError(exception, "Seed:Principals is not valid JSON; no principals were seeded");
            return;
        }

        if (seeds is null || seeds.Length == 0)
        {
            return;
        }

        var seeded = 0;

        foreach (var seed in seeds)
        {
            if (!TryParseChannel(seed.Channel, out var channel) || string.IsNullOrWhiteSpace(seed.UserId))
            {
                logger.LogWarning("Skipping a seed entry with an unknown channel or missing user id");
                continue;
            }

            var capabilities = JsonSerializer.Serialize(seed.Capabilities ?? [], JsonOptions);

            var existing = await context.Principals
                .FirstOrDefaultAsync(p => p.Channel == channel && p.UserId == seed.UserId, cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                context.Principals.Add(new PrincipalRecord
                {
                    Id = Guid.NewGuid(),
                    Channel = channel,
                    UserId = seed.UserId,
                    DisplayName = string.IsNullOrWhiteSpace(seed.DisplayName) ? seed.UserId : seed.DisplayName,
                    CapabilitiesJson = capabilities,
                });
            }
            else
            {
                existing.DisplayName = string.IsNullOrWhiteSpace(seed.DisplayName)
                    ? existing.DisplayName
                    : seed.DisplayName;
                existing.CapabilitiesJson = capabilities;
            }

            seeded++;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // The user id identifies a person on a channel; it is not a secret, and knowing which principals
        // exist is the first thing anyone debugging an authorization refusal needs.
        logger.LogInformation("Ensured {PrincipalCount} configured principal(s)", seeded);
    }

    private static bool TryParseChannel(string? value, out ChannelKind channel)
    {
        channel = ChannelKind.Unknown;

        return value?.Trim().ToLowerInvariant() switch
        {
            "telegram" => Set(ChannelKind.Telegram, out channel),
            "whatsapp" => Set(ChannelKind.WhatsApp, out channel),
            _ => false,
        };

        static bool Set(ChannelKind value, out ChannelKind target)
        {
            target = value;
            return true;
        }
    }
}
