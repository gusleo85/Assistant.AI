using Justina.Core.Infrastructure.Persistence;
using Justina.Recruitment.Application;
using Justina.Recruitment.Domain;
using Microsoft.EntityFrameworkCore;

namespace Justina.Recruitment.Infrastructure.Persistence;

/// <summary>
/// The candidate summary table. Additive only — nothing here touches Receipts or any expense mapping.
/// </summary>
public sealed class RecruitmentModelConfiguration : IModelConfiguration
{
    public void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CandidateSummary>(entity =>
        {
            entity.ToTable("CandidateSummaries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedNever();

            entity.Property(e => e.Channel).HasConversion<int>();
            entity.Property(e => e.RecipientUserId).HasMaxLength(128).IsRequired();
            entity.Property(e => e.CandidateId).HasMaxLength(64).IsRequired();
            entity.Property(e => e.JobOpeningId).HasMaxLength(64);
            entity.Property(e => e.StageId).HasMaxLength(64);
            entity.Property(e => e.CandidateName).HasMaxLength(256);
            entity.Property(e => e.CompanyId).HasMaxLength(64);

            // Long enough for a summary of a real CV. Truncating one would hide the part a hiring
            // manager is deciding on.
            entity.Property(e => e.SummaryText).HasMaxLength(8000).IsRequired();

            entity.Property(e => e.State).HasConversion<int>();
            entity.Property(e => e.ExternalInterviewId).HasMaxLength(128);
            entity.Property(e => e.FailureReason).HasMaxLength(256);

            entity.Property(e => e.CreatedAtUtc).HasColumnType("datetime2");
            entity.Property(e => e.UpdatedAtUtc).HasColumnType("datetime2");
            entity.Property(e => e.SentAtUtc).HasColumnType("datetime2");

            // Two sweeps running at once must not both send the same held summary.
            entity.Property(e => e.RowVersion).IsRowVersion();

            // The sweep reads deferred rows by state and age; the reply path reads the most recent one
            // sent to a person.
            entity.HasIndex(e => new { e.State, e.CreatedAtUtc })
                .HasDatabaseName("IX_CandidateSummaries_State_CreatedAtUtc");

            entity.HasIndex(e => new { e.Channel, e.RecipientUserId, e.State })
                .HasDatabaseName("IX_CandidateSummaries_Recipient_State");
        });
    }
}
