using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RuleEngine.Domain.Entities;

namespace RuleEngine.Infrastructure.Persistence.Configurations;

internal sealed class RuleTestCaseConfiguration : IEntityTypeConfiguration<RuleTestCase>
{
    public void Configure(EntityTypeBuilder<RuleTestCase> builder)
    {
        builder.ToTable("RuleTestCases");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.Name).IsRequired().HasMaxLength(RuleTestCase.NameMaxLength);
        builder.Property(t => t.InputFactsJson).IsRequired();
        builder.Property(t => t.CreatedBy).IsRequired().HasMaxLength(100);
        builder.HasIndex(t => new { t.RuleId, t.Name }).IsUnique();
    }
}
