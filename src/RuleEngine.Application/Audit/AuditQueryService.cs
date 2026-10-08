using Microsoft.EntityFrameworkCore;
using RuleEngine.Application.Common;
using RuleEngine.Application.Common.Abstractions;

namespace RuleEngine.Application.Audit;

public sealed class AuditLogQuery : PageQuery
{
    /// <summary>RuleSet | Rule | RuleTestCase | RuleExecution.</summary>
    public string? EntityType { get; set; }

    public string? EntityId { get; set; }

    public string? Action { get; set; }

    public string? Actor { get; set; }

    public string? CorrelationId { get; set; }

    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtc { get; set; }
}

public sealed record AuditLogDto(
    Guid Id,
    string EntityType,
    string EntityId,
    string Action,
    string Actor,
    DateTime OccurredAtUtc,
    string? BeforeSummary,
    string? AfterSummary,
    string? CorrelationId);

public sealed class AuditQueryService
{
    private readonly IRuleEngineDbContext _db;

    public AuditQueryService(IRuleEngineDbContext db)
    {
        _db = db;
    }

    public Task<PagedResult<AuditLogDto>> ListAsync(AuditLogQuery query, CancellationToken cancellationToken)
    {
        var source = _db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.EntityType))
        {
            source = source.Where(a => a.EntityType == query.EntityType);
        }

        if (!string.IsNullOrWhiteSpace(query.EntityId))
        {
            source = source.Where(a => a.EntityId == query.EntityId);
        }

        if (!string.IsNullOrWhiteSpace(query.Action))
        {
            source = source.Where(a => a.Action == query.Action);
        }

        if (!string.IsNullOrWhiteSpace(query.Actor))
        {
            source = source.Where(a => a.Actor == query.Actor);
        }

        if (!string.IsNullOrWhiteSpace(query.CorrelationId))
        {
            source = source.Where(a => a.CorrelationId == query.CorrelationId);
        }

        if (query.FromUtc is not null)
        {
            source = source.Where(a => a.OccurredAtUtc >= query.FromUtc.Value);
        }

        if (query.ToUtc is not null)
        {
            source = source.Where(a => a.OccurredAtUtc < query.ToUtc.Value);
        }

        return source
            .OrderByDescending(a => a.OccurredAtUtc)
            .ThenBy(a => a.Id)
            .Select(a => new AuditLogDto(a.Id, a.EntityType, a.EntityId, a.Action, a.Actor, a.OccurredAtUtc, a.BeforeSummary, a.AfterSummary, a.CorrelationId))
            .ToPagedResultAsync(query, cancellationToken);
    }
}
