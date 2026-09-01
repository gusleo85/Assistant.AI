using Microsoft.EntityFrameworkCore;

namespace Justina.Core.Infrastructure.Persistence;

/// <summary>
/// Lets a domain contribute its own entity mappings without Core referencing that domain.
/// This is what keeps Expense and Recruitment out of each other's — and out of Core's — dependency graph
/// while still sharing one database and one transaction (§15).
/// </summary>
public interface IModelConfiguration
{
    void Apply(ModelBuilder modelBuilder);
}

public sealed class JustinaDbContext(
    DbContextOptions<JustinaDbContext> options,
    IEnumerable<IModelConfiguration> modelConfigurations)
    : DbContext(options)
{
    public DbSet<ConversationRecord> Conversations => Set<ConversationRecord>();

    public DbSet<InboundMessageRecord> InboundMessages => Set<InboundMessageRecord>();

    public DbSet<IdempotencyKeyRecord> IdempotencyKeys => Set<IdempotencyKeyRecord>();

    public DbSet<PrincipalRecord> Principals => Set<PrincipalRecord>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        // One rule for every timestamp in the schema, rather than a converter repeated per property.
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<UtcDateTimeOffsetConverter>()
            .HaveColumnType("datetime2");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        new CoreModelConfiguration().Apply(modelBuilder);

        foreach (var configuration in modelConfigurations)
        {
            configuration.Apply(modelBuilder);
        }
    }
}
