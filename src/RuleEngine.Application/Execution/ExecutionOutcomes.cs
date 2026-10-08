using System.Text.Json;
using System.Text.Json.Nodes;
using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Common;
using RuleEngine.Domain.Entities;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Evaluation;

namespace RuleEngine.Application.Execution;

/// <summary>Derivations shared by execution, simulation and execution lookups.</summary>
public static class ExecutionOutcomes
{
    public static ExecutionStatus OverallStatus(IReadOnlyCollection<ActionExecutionStatus> statuses)
    {
        var failed = statuses.Count(s => s == ActionExecutionStatus.Failed);
        if (failed == 0)
        {
            return ExecutionStatus.Succeeded;
        }

        return failed == statuses.Count ? ExecutionStatus.Failed : ExecutionStatus.PartiallyFailed;
    }

    public static string Decision(bool anyMatched, IEnumerable<RuleActionType?> actionTypes)
    {
        if (!anyMatched)
        {
            return Decisions.NoMatch;
        }

        var types = actionTypes.ToList();
        if (types.Contains(RuleActionType.Reject))
        {
            return Decisions.Rejected;
        }

        return types.Contains(RuleActionType.RequireApproval) ? Decisions.ApprovalRequired : Decisions.Accepted;
    }

    public static IReadOnlyList<MatchedRuleDto> MatchedRules(EvaluationResult evaluation) =>
        evaluation.MatchedRules
            .Select(r => new MatchedRuleDto(r.RuleId, r.RuleName, r.VersionId, r.VersionNumber, r.VersionStatus, r.Priority))
            .ToList();

    public static IReadOnlyList<string> Tags(IEnumerable<RuleActionDefinition> actions) =>
        actions
            .Where(a => a.ActionType == RuleActionType.AddTag)
            .Select(a => a.GetString(ActionParameters.Tag))
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Rebuilds the API response from a persisted execution (used for lookups and idempotent replays).</summary>
    public static ExecutionResultDto FromEntity(RuleExecution execution, bool replayed)
    {
        var actions = execution.Actions.OrderBy(a => a.Sequence).ToList();
        var tags = actions
            .Where(a => a.ActionType == nameof(RuleActionType.AddTag) && a.Status == ActionExecutionStatus.Succeeded)
            .Select(a => DeserializeParameters(a.ParametersJson))
            .Select(p => p is not null && p.TryGetValue(ActionParameters.Tag, out var tag) && tag.ValueKind == JsonValueKind.String ? tag.GetString() : null)
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new ExecutionResultDto
        {
            ExecutionId = execution.Id,
            RuleSetId = execution.RuleSetId,
            CorrelationId = execution.CorrelationId,
            IsSimulation = false,
            Replayed = replayed,
            Status = execution.Status,
            Decision = execution.Decision,
            StartedAtUtc = execution.StartedAtUtc,
            DurationMs = execution.DurationMs,
            MatchedRules = JsonSerializer.Deserialize<List<MatchedRuleDto>>(execution.MatchedRulesJson, DomainJson.Options) ?? new List<MatchedRuleDto>(),
            Actions = actions.Select(a => new ActionOutcomeDto(
                a.Sequence,
                a.RuleId,
                a.RuleVersionId,
                a.ActionType,
                DeserializeParameters(a.ParametersJson),
                a.Status,
                a.Attempts,
                a.Message,
                a.DurationMs)).ToList(),
            Tags = tags,
            OutputFacts = JsonNode.Parse(execution.OutputFactsJson),
            Trace = JsonSerializer.Deserialize<List<RuleEvaluationTrace>>(execution.TraceJson, DomainJson.Options) ?? new List<RuleEvaluationTrace>(),
        };
    }

    public static string SerializeParameters(RuleActionDefinition action) =>
        JsonSerializer.Serialize(action.Parameters ?? new Dictionary<string, JsonElement>(), DomainJson.Options);

    private static Dictionary<string, JsonElement>? DeserializeParameters(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, DomainJson.Options);
}
