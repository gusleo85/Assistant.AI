using Microsoft.EntityFrameworkCore;

namespace Justina.Core.Infrastructure.Persistence;

/// <summary>
/// SQL Server mappings for the shared tables. Types are declared explicitly rather than inherited from
/// EF defaults, because the defaults for money and timestamps are not the ones we want (§24).
/// </summary>
public sealed class CoreModelConfiguration : IModelConfiguration
{
    public void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConversationRecord>(entity =>
        {
            entity.ToTable("Conversations");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Channel).HasConversion<int>();
            entity.Property(e => e.ExternalConversationId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.UserId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.ActiveWorkflow).HasMaxLength(64);
            entity.Property(e => e.UpdatedAtUtc).HasColumnType("datetime2");

            entity.HasIndex(e => new { e.Channel, e.ExternalConversationId })
                .IsUnique()
                .HasDatabaseName("UX_Conversations_Channel_ExternalConversationId");
        });

        modelBuilder.Entity<InboundMessageRecord>(entity =>
        {
            entity.ToTable("InboundMessages");
            entity.HasKey(e => new { e.Channel, e.MessageId });
            entity.Property(e => e.Channel).HasConversion<int>();
            entity.Property(e => e.MessageId).HasMaxLength(128);
            entity.Property(e => e.ReceivedAtUtc).HasColumnType("datetime2");
        });

        modelBuilder.Entity<IdempotencyKeyRecord>(entity =>
        {
            entity.ToTable("IdempotencyKeys");
            entity.HasKey(e => e.KeyValue);
            entity.Property(e => e.KeyValue).HasMaxLength(256);
            entity.Property(e => e.CommandType).HasMaxLength(128).IsRequired();
            entity.Property(e => e.ResultJson).HasColumnType("nvarchar(max)").IsRequired();
            entity.Property(e => e.CreatedAtUtc).HasColumnType("datetime2");
        });

        modelBuilder.Entity<PrincipalRecord>(entity =>
        {
            entity.ToTable("Principals");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Channel).HasConversion<int>();
            entity.Property(e => e.UserId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(256).IsRequired();
            entity.Property(e => e.CapabilitiesJson).HasColumnType("nvarchar(max)").IsRequired();

            entity.HasIndex(e => new { e.Channel, e.UserId })
                .IsUnique()
                .HasDatabaseName("UX_Principals_Channel_UserId");
        });
    }
}
