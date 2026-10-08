using Microsoft.EntityFrameworkCore;
using RuleEngine.Application.Common;
using RuleEngine.Application.Common.Abstractions;

namespace RuleEngine.Application.Execution;

public sealed class ExecutionQueryService
{
    private readonly IRuleEngineDbContext _db;

    public ExecutionQueryService(IRuleEngineDbContext db)
    {
        _db = db;
    }

    public async Task<PagedResult<ExecutionSummaryDto>> ListAsync(ExecutionListQuery query, CancellationToken cancellationToken)
    {
        var source = _db.RuleExecutions.AsNoTracking();
        if (query.RuleSetId is not null)
        {
            source = source.Where(e => e.RuleSetId == query.RuleSetId.Value);
        }

        if (query.Status is not null)
        {
            source = source.Where(e => e.Status == query.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.CorrelationId))
        {
            source = source.Where(e => e.CorrelationId == query.CorrelationId);
        }

        if (query.FromUtc is not null)
        {
            source = source.Where(e => e.StartedAtUtc >= query.FromUtc.Value);
        }

        if (query.ToUtc is not null)
        {
            source = source.Where(e => e.StartedAtUtc < query.ToUtc.Value);
        }

        return await source
            .OrderByDescending(e => e.StartedAtUtc)
            .ThenBy(e => e.Id)
            .Select(e => new ExecutionSummaryDto(
                e.Id,
                e.RuleSetId,
                e.CorrelationId,
                e.IdempotencyKey,
                e.Status,
                e.Decision,
                e.Actions.Count,
                e.ExecutedBy,
                e.StartedAtUtc,
                e.DurationMs))
            .ToPagedResultAsync(query, cancellationToken);
    }

    public async Task<ExecutionResultDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var execution = await _db.RuleExecutions.AsNoTracking()
            .Include(e => e.Actions)
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken)
            ?? throw new NotFoundException("Execution", id);
        return ExecutionOutcomes.FromEntity(execution, replayed: false);
    }
}
