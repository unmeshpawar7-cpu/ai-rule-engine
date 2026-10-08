using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RuleEngine.Domain.Entities;

namespace RuleEngine.Infrastructure.Persistence.Configurations;

internal sealed class ActionExecutionConfiguration : IEntityTypeConfiguration<ActionExecution>
{
    public void Configure(EntityTypeBuilder<ActionExecution> builder)
    {
        builder.ToTable("ActionExecutions");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.ActionType).IsRequired().HasMaxLength(64);
        builder.Property(a => a.ParametersJson).IsRequired();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(a => a.Message).HasMaxLength(ActionExecution.MessageMaxLength);
        builder.HasIndex(a => new { a.RuleExecutionId, a.Sequence }).IsUnique();
    }
}
