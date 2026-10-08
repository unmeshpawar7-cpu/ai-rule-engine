using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RuleEngine.Application.Common;
using RuleEngine.Application.Common.Abstractions;
using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Conditions;
using RuleEngine.Domain.Entities;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Facts;

namespace RuleEngine.Application.RuleSets;

public sealed class RuleSetService
{
    private const string EntityType = "RuleSet";

    private readonly IRuleEngineDbContext _db;
    private readonly IAuditTrail _audit;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _clock;

    public RuleSetService(IRuleEngineDbContext db, IAuditTrail audit, ICurrentUser currentUser, TimeProvider clock)
    {
        _db = db;
        _audit = audit;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<RuleSetDto> CreateAsync(CreateRuleSetRequest request, CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        if (await _db.RuleSets.AnyAsync(s => s.Name == name, cancellationToken))
        {
            throw new ConflictException($"A rule set named '{name}' already exists.");
        }

        var ruleSet = RuleSet.Create(name, request.Description, request.Strategy, request.FactSchema!, _currentUser.UserName, Now);
        _db.RuleSets.Add(ruleSet);
        _audit.Record(EntityType, ruleSet.Id, "Created", null, new { ruleSet.Name, ruleSet.Strategy, Fields = request.FactSchema!.Fields.Count });
        await _db.SaveChangesAsync(cancellationToken);
        return await GetAsync(ruleSet.Id, cancellationToken);
    }

    public async Task<PagedResult<RuleSetDto>> ListAsync(RuleSetListQuery query, CancellationToken cancellationToken)
    {
        var source = _db.RuleSets.AsNoTracking();
        if (query.IsActive is not null)
        {
            source = source.Where(s => s.IsActive == query.IsActive.Value);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            source = source.Where(s => s.Name.Contains(search));
        }

        var page = await Project(source.OrderBy(s => s.Name)).ToPagedResultAsync(query, cancellationToken);
        return page.Map(ToDto);
    }

    public async Task<RuleSetDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await Project(_db.RuleSets.AsNoTracking().Where(s => s.Id == id)).FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException("Rule set", id);
        return ToDto(row);
    }

    public async Task<RuleSetDto> UpdateSchemaAsync(Guid id, UpdateFactSchemaRequest request, CancellationToken cancellationToken)
    {
        var ruleSet = await LoadTrackedAsync(id, cancellationToken);
        EnsureStamp(ruleSet, request.ExpectedConcurrencyStamp);

        var schema = request.FactSchema!;
        var referenced = await ReferencedFieldsAsync(id, cancellationToken);
        var missing = referenced.Where(f => schema.Find(f) is null).OrderBy(f => f, StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            throw new BusinessRuleException(
                "The new schema removes fields that draft or published rule versions still use.",
                missing.Select(f => new ErrorItem("factSchema.fields", $"Field '{f}' is referenced by existing rules.", "FIELD_IN_USE")));
        }

        var before = ruleSet.GetFactSchema().Fields.Select(f => f.Path).ToList();
        ruleSet.UpdateFactSchema(schema, Now);
        _audit.Record(EntityType, ruleSet.Id, "SchemaUpdated", new { Fields = before }, new { Fields = schema.Fields.Select(f => f.Path).ToList() });
        await _db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public Task<RuleSetDto> ActivateAsync(Guid id, CancellationToken cancellationToken) =>
        SetActiveAsync(id, active: true, cancellationToken);

    public Task<RuleSetDto> DeactivateAsync(Guid id, CancellationToken cancellationToken) =>
        SetActiveAsync(id, active: false, cancellationToken);

    private async Task<RuleSetDto> SetActiveAsync(Guid id, bool active, CancellationToken cancellationToken)
    {
        var ruleSet = await LoadTrackedAsync(id, cancellationToken);
        var before = ruleSet.IsActive;
        if (active)
        {
            ruleSet.Activate(Now);
        }
        else
        {
            ruleSet.Deactivate(Now);
        }

        _audit.Record(EntityType, ruleSet.Id, active ? "Activated" : "Deactivated", new { IsActive = before }, new { IsActive = active });
        await _db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    private async Task<HashSet<string>> ReferencedFieldsAsync(Guid ruleSetId, CancellationToken cancellationToken)
    {
        var versions = await (
                from version in _db.RuleVersions.AsNoTracking()
                join rule in _db.Rules.AsNoTracking() on version.RuleId equals rule.Id
                where rule.RuleSetId == ruleSetId && version.Status != RuleVersionStatus.Retired
                select new { version.ConditionJson, version.ActionsJson })
            .ToListAsync(cancellationToken);

        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var version in versions)
        {
            try
            {
                fields.UnionWith(ConditionLimits.CollectFields(ConditionSerializer.Deserialize(version.ConditionJson)));
                foreach (var action in RuleActionSerializer.DeserializeList(version.ActionsJson))
                {
                    if (action.ActionType == RuleActionType.SetValue && action.GetString(ActionParameters.Field) is { } target)
                    {
                        fields.Add(target);
                    }
                }
            }
            catch (JsonException)
            {
                // Unreadable versions cannot reference anything meaningful; ignore.
            }
        }

        return fields;
    }

    private async Task<RuleSet> LoadTrackedAsync(Guid id, CancellationToken cancellationToken) =>
        await _db.RuleSets.FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
        ?? throw new NotFoundException("Rule set", id);

    private static void EnsureStamp(RuleSet ruleSet, Guid? expected)
    {
        if (expected is not null && expected.Value != ruleSet.ConcurrencyStamp)
        {
            throw new ConflictException("The rule set was modified by someone else. Reload it and retry.");
        }
    }

    private static IQueryable<RuleSetRow> Project(IQueryable<RuleSet> source) =>
        source.Select(s => new RuleSetRow
        {
            Id = s.Id,
            Name = s.Name,
            Description = s.Description,
            Strategy = s.Strategy,
            FactSchemaJson = s.FactSchemaJson,
            IsActive = s.IsActive,
            RuleCount = s.Rules.Count,
            ActiveRuleCount = s.Rules.Count(r => r.IsActive),
            CreatedBy = s.CreatedBy,
            CreatedAtUtc = s.CreatedAtUtc,
            UpdatedAtUtc = s.UpdatedAtUtc,
            ConcurrencyStamp = s.ConcurrencyStamp,
        });

    private static RuleSetDto ToDto(RuleSetRow row) => new(
        row.Id,
        row.Name,
        row.Description,
        row.Strategy,
        FactSchema.FromJson(row.FactSchemaJson),
        row.IsActive,
        row.RuleCount,
        row.ActiveRuleCount,
        row.CreatedBy,
        row.CreatedAtUtc,
        row.UpdatedAtUtc,
        row.ConcurrencyStamp);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private sealed class RuleSetRow
    {
        public Guid Id { get; init; }

        public string Name { get; init; } = string.Empty;

        public string? Description { get; init; }

        public EvaluationStrategy Strategy { get; init; }

        public string FactSchemaJson { get; init; } = "{}";

        public bool IsActive { get; init; }

        public int RuleCount { get; init; }

        public int ActiveRuleCount { get; init; }

        public string CreatedBy { get; init; } = string.Empty;

        public DateTime CreatedAtUtc { get; init; }

        public DateTime UpdatedAtUtc { get; init; }

        public Guid ConcurrencyStamp { get; init; }
    }
}
