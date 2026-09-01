using Justina.Core.Infrastructure.Persistence;
using Justina.Expense.Domain;
using Microsoft.EntityFrameworkCore;

namespace Justina.Expense.Infrastructure.Persistence;

/// <summary>
/// SQL Server mappings for the Expense aggregate. Money, timestamps and concurrency are declared
/// explicitly because the EF defaults are not the ones this domain needs (§24).
/// </summary>
public sealed class ExpenseModelConfiguration : IModelConfiguration
{
    public void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Receipt>(entity =>
        {
            entity.ToTable("Receipts");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.State).HasConversion<int>();

            // Native rowversion: a losing concurrent confirmation raises DbUpdateConcurrencyException,
            // which the unit of work turns into a typed conflict (§22).
            entity.Property(e => e.RowVersion).IsRowVersion();

            entity.Property(e => e.SourceMediaId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.Merchant).HasMaxLength(256);
            entity.Property(e => e.ReceiptDate).HasColumnType("date");
            entity.Property(e => e.Currency).HasColumnType("char(3)");
            entity.Property(e => e.Amount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Category).HasMaxLength(64);
            entity.Property(e => e.ReceiptNumber).HasMaxLength(64);
            entity.Property(e => e.TaxAmount).HasColumnType("decimal(18,2)");
            entity.Property(e => e.ExternalExpenseId).HasMaxLength(128);
            entity.Property(e => e.FailureReason).HasMaxLength(256);
            entity.Property(e => e.CreatedAtUtc).HasColumnType("datetime2");
            entity.Property(e => e.UpdatedAtUtc).HasColumnType("datetime2");

            entity.Property(e => e.SequenceInBatch).HasDefaultValue(1);

            entity.HasIndex(e => e.ConversationId).HasDatabaseName("IX_Receipts_ConversationId");
            entity.HasIndex(e => e.BatchId).HasDatabaseName("IX_Receipts_BatchId");

            // Filtered: SQL Server treats multiple NULLs as duplicates in a plain unique index, which would
            // block every receipt that has not been submitted yet.
            entity.HasIndex(e => e.ExternalExpenseId)
                .IsUnique()
                .HasFilter("[ExternalExpenseId] IS NOT NULL")
                .HasDatabaseName("UX_Receipts_ExternalExpenseId");

            entity.HasMany(e => e.LineItems)
                .WithOne()
                .HasForeignKey(i => i.ReceiptId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Events)
                .WithOne()
                .HasForeignKey(e => e.ReceiptId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Navigation(e => e.LineItems).UsePropertyAccessMode(PropertyAccessMode.Field);
            entity.Navigation(e => e.Events).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<ReceiptLineItem>(entity =>
        {
            entity.ToTable("ReceiptLineItems");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Description).HasMaxLength(512).IsRequired();
            entity.Property(e => e.Quantity).HasColumnType("decimal(18,4)");
            entity.Property(e => e.UnitPrice).HasColumnType("decimal(18,2)");
            entity.Property(e => e.Amount).HasColumnType("decimal(18,2)");
        });

        modelBuilder.Entity<ReceiptEvent>(entity =>
        {
            entity.ToTable("ReceiptEvents");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Id).ValueGeneratedOnAdd();
            entity.Property(e => e.EventType).HasMaxLength(64).IsRequired();
            entity.Property(e => e.FromState).HasConversion<int>();
            entity.Property(e => e.ToState).HasConversion<int>();
            entity.Property(e => e.Actor).HasMaxLength(128).IsRequired();
            entity.Property(e => e.PayloadJson).HasColumnType("nvarchar(max)");
            entity.Property(e => e.CreatedAtUtc).HasColumnType("datetime2");
            entity.HasIndex(e => e.ReceiptId).HasDatabaseName("IX_ReceiptEvents_ReceiptId");
        });

        modelBuilder.Entity<ReceiptBatch>(entity =>
        {
            entity.ToTable("ReceiptBatches");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SourceMediaId).HasMaxLength(256).IsRequired();
            entity.Property(e => e.CreatedAtUtc).HasColumnType("datetime2");
            entity.Ignore(e => e.Receipts);
        });
    }
}
