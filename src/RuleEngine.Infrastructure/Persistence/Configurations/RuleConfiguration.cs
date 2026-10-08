using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RuleEngine.Domain.Entities;

namespace RuleEngine.Infrastructure.Persistence.Configurations;

internal sealed class RuleConfiguration : IEntityTypeConfiguration<Rule>
{
    public void Configure(EntityTypeBuilder<Rule> builder)
    {
        builder.ToTable("Rules");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Name).IsRequired().HasMaxLength(Rule.NameMaxLength);
        builder.Property(r => r.Description).HasMaxLength(Rule.DescriptionMaxLength);
        builder.Property(r => r.CreatedBy).IsRequired().HasMaxLength(100);
        builder.Property(r => r.ConcurrencyStamp).IsConcurrencyToken();

        builder.HasIndex(r => new { r.RuleSetId, r.Priority });
        builder.HasIndex(r => new { r.RuleSetId, r.Name }).IsUnique();

        builder.HasMany(r => r.Versions)
            .WithOne()
            .HasForeignKey(v => v.RuleId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(r => r.TestCases)
            .WithOne()
            .HasForeignKey(t => t.RuleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
