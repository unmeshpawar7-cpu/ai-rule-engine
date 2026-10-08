using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RuleEngine.Domain.Entities;

namespace RuleEngine.Infrastructure.Persistence.Configurations;

internal sealed class RuleVersionConfiguration : IEntityTypeConfiguration<RuleVersion>
{
    public void Configure(EntityTypeBuilder<RuleVersion> builder)
    {
        builder.ToTable("RuleVersions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.ConditionJson).IsRequired();
        builder.Property(v => v.ActionsJson).IsRequired();
        builder.Property(v => v.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(v => v.Source).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(v => v.PromptVersion).HasMaxLength(RuleVersion.PromptVersionMaxLength);
        builder.Property(v => v.CreatedBy).IsRequired().HasMaxLength(100);
        builder.Property(v => v.PublishedBy).HasMaxLength(100);

        builder.HasIndex(v => new { v.RuleId, v.VersionNumber }).IsUnique();
        builder.HasIndex(v => new { v.RuleId, v.Status });
    }
}
