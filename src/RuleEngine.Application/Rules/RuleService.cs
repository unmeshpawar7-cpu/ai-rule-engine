using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RuleEngine.Application.Common;
using RuleEngine.Application.Common.Abstractions;
using RuleEngine.Application.Testing;
using RuleEngine.Application.Validation;
using RuleEngine.Domain.Entities;
using RuleEngine.Domain.Enums;

namespace RuleEngine.Application.Rules;

/// <summary>Rule authoring and lifecycle: create, version, publish, activate, deactivate, delete drafts.</summary>
public sealed class RuleService
{
    private const string EntityType = "Rule";

    private readonly IRuleEngineDbContext _db;
    private readonly RuleValidator _validator;
    private readonly RuleTestRunner _testRunner;
    private readonly IAuditTrail _audit;
    private readonly ICurrentUser _currentUser;
    private readonly TimeProvider _clock;
    private readonly RuleEngineOptions _options;

    public RuleService(
        IRuleEngineDbContext db,
        RuleValidator validator,
        RuleTestRunner testRunner,
        IAuditTrail audit,
        ICurrentUser currentUser,
        TimeProvider clock,
        IOptions<RuleEngineOptions> options)
    {
        _db = db;
        _validator = validator;
        _testRunner = testRunner;
        _audit = audit;
        _currentUser = currentUser;
        _clock = clock;
        _options = options.Value;
    }

    public async Task<RuleWriteResultDto> CreateAsync(Guid ruleSetId, CreateRuleRequest request, CancellationToken cancellationToken)
    {
        var ruleSet = await _db.RuleSets.AsNoTracking().FirstOrDefaultAsync(s => s.Id == ruleSetId, cancellationToken)
            ?? throw new NotFoundException("Rule set", ruleSetId);

        var name = request.Name.Trim();
        if (await _db.Rules.AnyAsync(r => r.RuleSetId == ruleSetId && r.Name == name, cancellationToken))
        {
            throw new ConflictException($"Rule set '{ruleSet.Name}' already has a rule named '{name}'.");
        }

        var validation = await _validator.ValidateAsync(ruleSet, request.Condition, request.Actions, request.Priority, null, name, cancellationToken);
        EnsureValid(validation);

        var content = new RuleVersionContent(validation.NormalizedConditionJson, validation.NormalizedActionsJson, RuleSource.Manual);
        var rule = Rule.Create(ruleSetId, name, request.Description, request.Priority, content, _currentUser.UserName, Now);
        _db.Rules.Add(rule);
        _audit.Record(EntityType, rule.Id, "Created", null, new { rule.Name, rule.Priority, Version = 1, Source = RuleSource.Manual });
        await _db.SaveChangesAsync(cancellationToken);

        return new RuleWriteResultDto(await GetAsync(rule.Id, cancellationToken), validation.Warnings);
    }

    public async Task<PagedResult<RuleSummaryDto>> ListAsync(RuleListQuery query, CancellationToken cancellationToken)
    {
        var source = _db.Rules.AsNoTracking();
        if (query.RuleSetId is not null)
        {
            source = source.Where(r => r.RuleSetId == query.RuleSetId.Value);
        }

        if (query.IsActive is not null)
        {
            source = source.Where(r => r.IsActive == query.IsActive.Value);
        }

        if (query.HasDraft is not null)
        {
            var hasDraft = query.HasDraft.Value;
            source = source.Where(r => r.Versions.Any(v => v.Status == RuleVersionStatus.Draft) == hasDraft);
        }

        if (query.Source is not null)
        {
            var sourceFilter = query.Source.Value;
            source = source.Where(r => r.Versions.Any(v => v.Source == sourceFilter));
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            source = source.Where(r => r.Name.Contains(search));
        }

        return await source
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Name)
            .Select(r => new RuleSummaryDto(
                r.Id,
                r.RuleSetId,
                r.Name,
                r.Priority,
                r.IsActive,
                r.CurrentPublishedVersionNumber,
                r.Versions.Max(v => (int?)v.VersionNumber) ?? 0,
                r.Versions.Any(v => v.Status == RuleVersionStatus.Draft),
                r.Versions.Count,
                r.UpdatedAtUtc))
            .ToPagedResultAsync(query, cancellationToken);
    }

    public async Task<RuleDetailDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var rule = await _db.Rules.AsNoTracking()
            .Include(r => r.Versions)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new NotFoundException("Rule", id);
        var testCaseCount = await _db.RuleTestCases.CountAsync(t => t.RuleId == id, cancellationToken);
        return RuleMapper.ToDetail(rule, testCaseCount);
    }

    public async Task<RuleWriteResultDto> AddDraftVersionAsync(Guid id, AddRuleVersionRequest request, CancellationToken cancellationToken)
    {
        var rule = await LoadTrackedAsync(id, cancellationToken);
        EnsureStamp(rule, request.ExpectedConcurrencyStamp);
        var ruleSet = await _db.RuleSets.AsNoTracking().FirstAsync(s => s.Id == rule.RuleSetId, cancellationToken);

        var validation = await _validator.ValidateAsync(ruleSet, request.Condition, request.Actions, rule.Priority, rule.Id, rule.Name, cancellationToken);
        EnsureValid(validation);

        var content = new RuleVersionContent(validation.NormalizedConditionJson, validation.NormalizedActionsJson, RuleSource.Manual);
        var version = rule.AddDraftVersion(content, _currentUser.UserName, Now);
        _db.RuleVersions.Add(version);
        _audit.Record(EntityType, rule.Id, "DraftVersionAdded", null, new { version.VersionNumber, version.Source });
        await _db.SaveChangesAsync(cancellationToken);

        return new RuleWriteResultDto(await GetAsync(id, cancellationToken), validation.Warnings);
    }

    /// <summary>
    /// Publishes a draft version. When <see cref="RuleEngineOptions.RequirePassingTestsToPublish"/> is on,
    /// all of the rule's test cases are run against the version first and any failure blocks publishing (422).
    /// </summary>
    public async Task<RuleDetailDto> PublishAsync(Guid id, PublishRuleRequest request, CancellationToken cancellationToken)
    {
        var rule = await LoadTrackedAsync(id, cancellationToken);
        EnsureStamp(rule, request.ExpectedConcurrencyStamp);
        var version = rule.FindVersion(request.VersionNumber)
            ?? throw new NotFoundException("Rule version", $"{id}/v{request.VersionNumber}");

        if (_options.RequirePassingTestsToPublish)
        {
            var ruleSet = await _db.RuleSets.AsNoTracking().FirstAsync(s => s.Id == rule.RuleSetId, cancellationToken);
            var testCases = await _db.RuleTestCases.Where(t => t.RuleId == id).ToListAsync(cancellationToken);
            if (testCases.Count > 0)
            {
                var report = _testRunner.Run(ruleSet.GetFactSchema(), EvaluableRuleLoader.ToEvaluable(rule, version), testCases);
                foreach (var testCase in testCases)
                {
                    testCase.RecordRun(report.Results.First(r => r.TestCaseId == testCase.Id).Passed, Now);
                }

                if (report.Failed > 0)
                {
                    throw new BusinessRuleException(
                        $"Publishing blocked: {report.Failed} of {report.Total} test case(s) failed against version {version.VersionNumber}.",
                        report.Results.Where(r => !r.Passed).Select(r => new ErrorItem($"tests[{r.Name}]", string.Join(" ", r.Failures), "TEST_FAILED")));
                }
            }
        }

        var previous = rule.CurrentPublishedVersionNumber;
        rule.Publish(request.VersionNumber, _currentUser.UserName, Now);
        _audit.Record(EntityType, rule.Id, "Published", new { PublishedVersion = previous }, new { PublishedVersion = request.VersionNumber });
        await _db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<RuleDetailDto> ActivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var rule = await LoadTrackedAsync(id, cancellationToken);
        rule.Activate(Now);
        _audit.Record(EntityType, rule.Id, "Activated", new { IsActive = false }, new { IsActive = true, rule.CurrentPublishedVersionNumber });
        await _db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    public async Task<RuleDetailDto> DeactivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var rule = await LoadTrackedAsync(id, cancellationToken);
        var before = rule.IsActive;
        rule.Deactivate(Now);
        _audit.Record(EntityType, rule.Id, "Deactivated", new { IsActive = before }, new { IsActive = false });
        await _db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    /// <summary>Deletes a draft version. If it was the rule's only version, the rule itself is deleted.</summary>
    /// <returns>True when the whole rule was removed.</returns>
    public async Task<bool> DeleteDraftAsync(Guid id, int versionNumber, CancellationToken cancellationToken)
    {
        var rule = await LoadTrackedAsync(id, cancellationToken);
        if (rule.FindVersion(versionNumber) is null)
        {
            throw new NotFoundException("Rule version", $"{id}/v{versionNumber}");
        }

        var removed = rule.RemoveDraft(versionNumber, Now);
        _db.RuleVersions.Remove(removed);
        var ruleDeleted = rule.Versions.Count == 0;
        if (ruleDeleted)
        {
            _db.Rules.Remove(rule);
            _audit.Record(EntityType, rule.Id, "Deleted", new { rule.Name, DraftVersion = versionNumber }, null);
        }
        else
        {
            _audit.Record(EntityType, rule.Id, "DraftVersionDeleted", new { DraftVersion = versionNumber }, null);
        }

        await _db.SaveChangesAsync(cancellationToken);
        return ruleDeleted;
    }

    private static void EnsureValid(RuleDefinitionValidation validation)
    {
        if (!validation.IsValid)
        {
            throw new BusinessRuleException("Rule definition is invalid.", RuleMapper.ToErrorItems(validation.Errors));
        }
    }

    private static void EnsureStamp(Rule rule, Guid? expected)
    {
        if (expected is not null && expected.Value != rule.ConcurrencyStamp)
        {
            throw new ConflictException("The rule was modified by someone else. Reload it and retry.");
        }
    }

    private async Task<Rule> LoadTrackedAsync(Guid id, CancellationToken cancellationToken) =>
        await _db.Rules.Include(r => r.Versions).FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
        ?? throw new NotFoundException("Rule", id);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
}
