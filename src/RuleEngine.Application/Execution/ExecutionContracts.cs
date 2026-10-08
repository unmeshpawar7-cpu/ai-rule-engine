using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using RuleEngine.Application.Common;
using RuleEngine.Domain.Enums;
using RuleEngine.Domain.Evaluation;

namespace RuleEngine.Application.Execution;

public sealed class ExecuteRuleSetRequest
{
    /// <summary>Input facts, validated against the rule set's fact schema.</summary>
    public JsonObject? Facts { get; init; }
}

public sealed class SimulateRuleSetRequest
{
    public JsonObject? Facts { get; init; }

    /// <summary>Evaluate draft versions (latest draft per rule) instead of / in addition to published ones. Default true.</summary>
    public bool IncludeDrafts { get; init; } = true;

    /// <summary>Also evaluate deactivated rules. Default false.</summary>
    public bool IncludeInactive { get; init; }
}

public sealed class ExecutionListQuery : PageQuery
{
    public Guid? RuleSetId { get; set; }

    public ExecutionStatus? Status { get; set; }

    public string? CorrelationId { get; set; }

    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtc { get; set; }
}

public static class Decisions
{
    public const string NoMatch = "NoMatch";
    public const string Accepted = "Accepted";
    public const string ApprovalRequired = "ApprovalRequired";
    public const string Rejected = "Rejected";
}

public sealed record MatchedRuleDto(
    Guid RuleId,
    string RuleName,
    Guid VersionId,
    int VersionNumber,
    RuleVersionStatus VersionStatus,
    int Priority);

public sealed record ActionOutcomeDto(
    int Sequence,
    Guid RuleId,
    Guid RuleVersionId,
    string ActionType,
    Dictionary<string, JsonElement>? Parameters,
    ActionExecutionStatus Status,
    int Attempts,
    string? Message,
    long DurationMs);

/// <summary>Response of execute, simulate and execution lookups.</summary>
public sealed class ExecutionResultDto
{
    /// <summary>Null for simulations (nothing is persisted).</summary>
    public Guid? ExecutionId { get; init; }

    public Guid RuleSetId { get; init; }

    public string CorrelationId { get; init; } = string.Empty;

    public bool IsSimulation { get; init; }

    /// <summary>True when an Idempotency-Key matched a previous execution and its stored result was returned.</summary>
    public bool Replayed { get; init; }

    /// <summary>Overall action outcome (null for simulations).</summary>
    public ExecutionStatus? Status { get; init; }

    /// <summary>NoMatch | Accepted | ApprovalRequired | Rejected.</summary>
    public string Decision { get; init; } = Decisions.NoMatch;

    public DateTime StartedAtUtc { get; init; }

    public long DurationMs { get; init; }

    public IReadOnlyList<MatchedRuleDto> MatchedRules { get; init; } = Array.Empty<MatchedRuleDto>();

    public IReadOnlyList<ActionOutcomeDto> Actions { get; init; } = Array.Empty<ActionOutcomeDto>();

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>Facts after SetValue actions were applied.</summary>
    public JsonNode? OutputFacts { get; init; }

    /// <summary>Per-rule, per-node evaluation trace (expected vs actual values).</summary>
    public IReadOnlyList<RuleEvaluationTrace> Trace { get; init; } = Array.Empty<RuleEvaluationTrace>();
}

public sealed record ExecutionSummaryDto(
    Guid Id,
    Guid RuleSetId,
    string CorrelationId,
    string? IdempotencyKey,
    ExecutionStatus Status,
    string Decision,
    int ActionCount,
    string ExecutedBy,
    DateTime StartedAtUtc,
    long DurationMs);

public sealed class ExecuteRuleSetRequestValidator : AbstractValidator<ExecuteRuleSetRequest>
{
    public ExecuteRuleSetRequestValidator()
    {
        RuleFor(x => x.Facts).NotNull().WithMessage("'facts' must be a JSON object.");
    }
}

public sealed class SimulateRuleSetRequestValidator : AbstractValidator<SimulateRuleSetRequest>
{
    public SimulateRuleSetRequestValidator()
    {
        RuleFor(x => x.Facts).NotNull().WithMessage("'facts' must be a JSON object.");
    }
}
