using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RuleEngine.Domain.Entities;

namespace RuleEngine.Infrastructure.Persistence.Configurations;

internal sealed class RuleSetConfiguration : IEntityTypeConfiguration<RuleSet>
{
    public void Configure(EntityTypeBuilder<RuleSet> builder)
    {
        builder.ToTable("RuleSets");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Name).IsRequired().HasMaxLength(RuleSet.NameMaxLength);
        builder.Property(s => s.Description).HasMaxLength(RuleSet.DescriptionMaxLength);
        builder.Property(s => s.Strategy).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(s => s.FactSchemaJson).IsRequired();
        builder.Property(s => s.CreatedBy).IsRequired().HasMaxLength(100);
        builder.Property(s => s.ConcurrencyStamp).IsConcurrencyToken();
        builder.HasIndex(s => s.Name).IsUnique();

        builder.HasMany(s => s.Rules)
            .WithOne()
            .HasForeignKey(r => r.RuleSetId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
