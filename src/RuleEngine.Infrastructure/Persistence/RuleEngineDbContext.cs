using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using RuleEngine.Application.Common.Abstractions;
using RuleEngine.Domain.Entities;

namespace RuleEngine.Infrastructure.Persistence;

/// <summary>
/// EF Core context and unit of work. One SaveChangesAsync per use case = one transaction, which is how
/// audit records are guaranteed to commit together with the change they describe.
/// </summary>
public sealed class RuleEngineDbContext : DbContext, IRuleEngineDbContext
{
    public RuleEngineDbContext(DbContextOptions<RuleEngineDbContext> options)
        : base(options)
    {
    }

    public DbSet<RuleSet> RuleSets => Set<RuleSet>();

    public DbSet<Rule> Rules => Set<Rule>();

    public DbSet<RuleVersion> RuleVersions => Set<RuleVersion>();

    public DbSet<RuleTestCase> RuleTestCases => Set<RuleTestCase>();

    public DbSet<RuleExecution> RuleExecutions => Set<RuleExecution>();

    public DbSet<ActionExecution> ActionExecutions => Set<ActionExecution>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(RuleEngineDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // All timestamps are UTC; make sure they come back with DateTimeKind.Utc so JSON carries the 'Z'.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();
    }
}

/// <summary>Stores and reads all <see cref="DateTime"/> values as UTC.</summary>
internal sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(
            v => v.Kind == DateTimeKind.Utc ? v : DateTime.SpecifyKind(v, DateTimeKind.Utc),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
    {
    }
}
