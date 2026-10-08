using Microsoft.EntityFrameworkCore;
using RuleEngine.Domain.Entities;

namespace RuleEngine.Application.Common.Abstractions;

/// <summary>
/// The EF Core context seen by use cases. It is the unit of work: every use case stages its
/// changes (including the audit record) and commits them with a single SaveChangesAsync call,
/// which EF Core wraps in one database transaction. See docs/adr/0003-dbcontext-as-unit-of-work.md.
/// </summary>
public interface IRuleEngineDbContext
{
    DbSet<RuleSet> RuleSets { get; }

    DbSet<Rule> Rules { get; }

    DbSet<RuleVersion> RuleVersions { get; }

    DbSet<RuleTestCase> RuleTestCases { get; }

    DbSet<RuleExecution> RuleExecutions { get; }

    DbSet<ActionExecution> ActionExecutions { get; }

    DbSet<AuditLog> AuditLogs { get; }

    DbSet<User> Users { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
