using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using RuleEngine.Domain.Actions;
using RuleEngine.Domain.Conditions;
using RuleEngine.Domain.Enums;

namespace RuleEngine.Domain.Evaluation;

/// <summary>Safety limits applied while evaluating condition trees.</summary>
public sealed class EvaluationOptions
{
    public int MaxDepth { get; init; } = 10;

    public int MaxNodes { get; init; } = 200;

    public TimeSpan RegexTimeout { get; init; } = TimeSpan.FromMilliseconds(100);
}

/// <summary>A specific rule version prepared for evaluation (already parsed from JSON).</summary>
public sealed class EvaluableRule
{
    public required Guid RuleId { get; init; }

    public required string RuleName { get; init; }

    public required Guid VersionId { get; init; }

    public required int VersionNumber { get; init; }

    public required int Priority { get; init; }

    public required ConditionNode Condition { get; init; }

    public required IReadOnlyList<RuleActionDefinition> Actions { get; init; }

    public RuleVersionStatus Status { get; init; } = RuleVersionStatus.Published;
}

/// <summary>
/// Explainability trace for one condition node: what was expected, what the fact actually was,
/// and the boolean outcome. Composite nodes carry their children.
/// </summary>
public sealed class TraceNode
{
    /// <summary>all | any | not | leaf | invalid</summary>
    public string Kind { get; init; } = "leaf";

    public bool Result { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Field { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Operator { get; init; }

    /// <summary>Expected value as raw JSON.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Expected { get; init; }

    /// <summary>Actual fact value as raw JSON; null when the fact was absent.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Actual { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Note { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TraceNode>? Children { get; init; }
}

public sealed class RuleEvaluationTrace
{
    public Guid RuleId { get; init; }

    public string RuleName { get; init; } = string.Empty;

    public Guid VersionId { get; init; }

    public int VersionNumber { get; init; }

    public RuleVersionStatus VersionStatus { get; init; }

    public int Priority { get; init; }

    /// <summary>False when a FirstMatch strategy stopped before reaching this rule.</summary>
    public bool Evaluated { get; init; }

    public bool Matched { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TraceNode? Condition { get; init; }
}

public sealed class SelectedAction
{
    public int Sequence { get; init; }

    public Guid RuleId { get; init; }

    public string RuleName { get; init; } = string.Empty;

    public Guid VersionId { get; init; }

    public int VersionNumber { get; init; }

    public required RuleActionDefinition Action { get; init; }
}

public sealed class EvaluationResult
{
    public required EvaluationStrategy Strategy { get; init; }

    /// <summary>One trace per rule, in evaluation (priority) order.</summary>
    public required IReadOnlyList<RuleEvaluationTrace> Rules { get; init; }

    public required IReadOnlyList<SelectedAction> Actions { get; init; }

    /// <summary>Input facts after all SetValue actions of matched rules were applied.</summary>
    public required JsonObject OutputFacts { get; init; }

    public IReadOnlyList<RuleEvaluationTrace> MatchedRules => Rules.Where(r => r.Matched).ToList();
}
