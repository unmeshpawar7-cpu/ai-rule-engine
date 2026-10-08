using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RuleEngine.Domain.Entities;

namespace RuleEngine.Infrastructure.Persistence.Configurations;

internal sealed class RuleExecutionConfiguration : IEntityTypeConfiguration<RuleExecution>
{
    public void Configure(EntityTypeBuilder<RuleExecution> builder)
    {
        builder.ToTable("RuleExecutions");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.CorrelationId).IsRequired().HasMaxLength(RuleExecution.CorrelationIdMaxLength);
        builder.Property(e => e.IdempotencyKey).HasMaxLength(RuleExecution.IdempotencyKeyMaxLength);
        builder.Property(e => e.InputHash).IsRequired().HasMaxLength(64);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(e => e.Decision).IsRequired().HasMaxLength(32);
        builder.Property(e => e.MatchedRulesJson).IsRequired();
        builder.Property(e => e.TraceJson).IsRequired();
        builder.Property(e => e.OutputFactsJson).IsRequired();
        builder.Property(e => e.ExecutedBy).IsRequired().HasMaxLength(100);

        builder.HasIndex(e => new { e.RuleSetId, e.IdempotencyKey })
            .IsUnique()
            .HasFilter("[IdempotencyKey] IS NOT NULL");
        builder.HasIndex(e => new { e.RuleSetId, e.StartedAtUtc });
        builder.HasIndex(e => e.CorrelationId);

        builder.HasOne<RuleSet>()
            .WithMany()
            .HasForeignKey(e => e.RuleSetId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(e => e.Actions)
            .WithOne()
            .HasForeignKey(a => a.RuleExecutionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
