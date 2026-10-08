using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RuleEngine.Application.Common.Abstractions;
using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Conditions;
using RuleEngine.Domain.Entities;

namespace RuleEngine.Application.Validation;

/// <summary>
/// The validation gate every rule version passes before it is stored — whether typed by a person
/// or proposed by a generator: definition validation against the fact schema, then conflict
/// detection against the rule set's active, published rules (warnings only).
/// </summary>
public sealed class RuleValidator
{
    private readonly IRuleEngineDbContext _db;
    private readonly RuleDefinitionValidator _definitionValidator;
    private readonly ConflictDetector _conflictDetector;

    public RuleValidator(IRuleEngineDbContext db, RuleDefinitionValidator definitionValidator, ConflictDetector conflictDetector)
    {
        _db = db;
        _definitionValidator = definitionValidator;
        _conflictDetector = conflictDetector;
    }

    public async Task<RuleDefinitionValidation> ValidateAsync(
        RuleSet ruleSet,
        JsonElement condition,
        JsonElement actions,
        int priority,
        Guid? ruleId,
        string ruleName,
        CancellationToken cancellationToken)
    {
        var schema = ruleSet.GetFactSchema();
        var result = _definitionValidator.Validate(schema, condition, actions);
        if (!result.IsValid)
        {
            return result;
        }

        var ruleSetId = ruleSet.Id;
        var activeRules = await (
                from rule in _db.Rules.AsNoTracking()
                join version in _db.RuleVersions.AsNoTracking() on rule.CurrentPublishedVersionId equals (Guid?)version.Id
                where rule.RuleSetId == ruleSetId && rule.IsActive
                select new { rule.Id, rule.Name, rule.Priority, version.ConditionJson, version.ActionsJson })
            .ToListAsync(cancellationToken);

        var existing = new List<RuleCandidate>();
        foreach (var active in activeRules.Where(r => r.Id != ruleId))
        {
            try
            {
                existing.Add(new RuleCandidate(
                    active.Id,
                    active.Name,
                    active.Priority,
                    ConditionSerializer.Deserialize(active.ConditionJson),
                    RuleActionSerializer.DeserializeList(active.ActionsJson)));
            }
            catch (JsonException)
            {
                // Stored versions were validated on write; an unreadable one is skipped rather than blocking authoring.
            }
        }

        var candidate = new RuleCandidate(ruleId, ruleName, priority, result.Condition!, result.Actions!);
        result.Warnings.AddRange(_conflictDetector.Detect(candidate, existing, schema));
        return result;
    }
}
