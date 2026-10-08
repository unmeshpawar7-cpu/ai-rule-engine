using Microsoft.EntityFrameworkCore;
using RuleEngine.Application.Common.Abstractions;
using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Conditions;
using RuleEngine.Domain.Entities;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Evaluation;

namespace RuleEngine.Application.Rules;

/// <summary>Loads the rule versions that take part in an execution or a simulation, with one query each.</summary>
public sealed class EvaluableRuleLoader
{
    private readonly IRuleEngineDbContext _db;

    public EvaluableRuleLoader(IRuleEngineDbContext db)
    {
        _db = db;
    }

    /// <summary>Active rules with their current published version — what a real execution runs.</summary>
    public async Task<IReadOnlyList<EvaluableRule>> LoadPublishedAsync(Guid ruleSetId, CancellationToken cancellationToken)
    {
        var rows = await (
                from rule in _db.Rules.AsNoTracking()
                join version in _db.RuleVersions.AsNoTracking() on rule.CurrentPublishedVersionId equals (Guid?)version.Id
                where rule.RuleSetId == ruleSetId && rule.IsActive
                select new VersionRow(
                    rule.Id,
                    rule.Name,
                    rule.Priority,
                    rule.IsActive,
                    version.Id,
                    version.VersionNumber,
                    version.Status,
                    version.ConditionJson,
                    version.ActionsJson))
            .ToListAsync(cancellationToken);

        return rows.Select(r => ToEvaluable(r)).ToList();
    }

    /// <summary>
    /// Rules for a dry run. Active rules use their latest draft when <paramref name="includeDrafts"/> is
    /// set and the draft is newer than the published version, otherwise the published version.
    /// Never-published rules take part with their latest draft when drafts are included, and
    /// deactivated rules only when <paramref name="includeInactive"/> is set.
    /// </summary>
    public async Task<IReadOnlyList<EvaluableRule>> LoadForSimulationAsync(
        Guid ruleSetId,
        bool includeDrafts,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var rows = await (
                from version in _db.RuleVersions.AsNoTracking()
                join rule in _db.Rules.AsNoTracking() on version.RuleId equals rule.Id
                where rule.RuleSetId == ruleSetId && version.Status != RuleVersionStatus.Retired
                select new VersionRow(
                    rule.Id,
                    rule.Name,
                    rule.Priority,
                    rule.IsActive,
                    version.Id,
                    version.VersionNumber,
                    version.Status,
                    version.ConditionJson,
                    version.ActionsJson))
            .ToListAsync(cancellationToken);

        var selected = new List<EvaluableRule>();
        foreach (var group in rows.GroupBy(r => r.RuleId))
        {
            var published = group.FirstOrDefault(r => r.Status == RuleVersionStatus.Published);
            var latestDraft = group.Where(r => r.Status == RuleVersionStatus.Draft).OrderByDescending(r => r.VersionNumber).FirstOrDefault();
            var isActive = group.First().IsActive;

            VersionRow? chosen;
            if (published is null)
            {
                chosen = includeDrafts ? latestDraft : null;
            }
            else if (!isActive && !includeInactive)
            {
                chosen = null;
            }
            else
            {
                chosen = includeDrafts && latestDraft is not null && latestDraft.VersionNumber > published.VersionNumber
                    ? latestDraft
                    : published;
            }

            if (chosen is not null)
            {
                selected.Add(ToEvaluable(chosen));
            }
        }

        return selected;
    }

    public static EvaluableRule ToEvaluable(Rule rule, RuleVersion version) => new()
    {
        RuleId = rule.Id,
        RuleName = rule.Name,
        Priority = rule.Priority,
        VersionId = version.Id,
        VersionNumber = version.VersionNumber,
        Status = version.Status,
        Condition = ConditionSerializer.Deserialize(version.ConditionJson),
        Actions = RuleActionSerializer.DeserializeList(version.ActionsJson),
    };

    private static EvaluableRule ToEvaluable(VersionRow row) => new()
    {
        RuleId = row.RuleId,
        RuleName = row.RuleName,
        Priority = row.Priority,
        VersionId = row.VersionId,
        VersionNumber = row.VersionNumber,
        Status = row.Status,
        Condition = ConditionSerializer.Deserialize(row.ConditionJson),
        Actions = RuleActionSerializer.DeserializeList(row.ActionsJson),
    };

    private sealed record VersionRow(
        Guid RuleId,
        string RuleName,
        int Priority,
        bool IsActive,
        Guid VersionId,
        int VersionNumber,
        RuleVersionStatus Status,
        string ConditionJson,
        string ActionsJson);
}
