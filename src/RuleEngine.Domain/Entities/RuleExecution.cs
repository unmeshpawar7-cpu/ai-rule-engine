using RuleEngine.Domain.Enums;

namespace RuleEngine.Domain.Entities;

/// <summary>Persistent record of one (non-simulated) execution of a rule set.</summary>
public sealed class RuleExecution
{
    public const int CorrelationIdMaxLength = 100;
    public const int IdempotencyKeyMaxLength = 100;

    private RuleExecution()
    {
    }

    public Guid Id { get; private set; }

    public Guid RuleSetId { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    public string? IdempotencyKey { get; private set; }

    /// <summary>SHA-256 (hex) of the canonicalised input facts.</summary>
    public string InputHash { get; private set; } = string.Empty;

    public ExecutionStatus Status { get; private set; }

    /// <summary>Accepted | ApprovalRequired | Rejected.</summary>
    public string Decision { get; private set; } = string.Empty;

    public string MatchedRulesJson { get; private set; } = "[]";

    public string TraceJson { get; private set; } = "[]";

    public string OutputFactsJson { get; private set; } = "{}";

    public string ExecutedBy { get; private set; } = string.Empty;

    public DateTime StartedAtUtc { get; private set; }

    public DateTime CompletedAtUtc { get; private set; }

    public long DurationMs { get; private set; }

    public List<ActionExecution> Actions { get; private set; } = new();

    public static RuleExecution Start(
        Guid ruleSetId,
        string correlationId,
        string? idempotencyKey,
        string inputHash,
        string executedBy,
        DateTime startedAtUtc) =>
        new()
        {
            Id = Guid.NewGuid(),
            RuleSetId = ruleSetId,
            CorrelationId = correlationId,
            IdempotencyKey = idempotencyKey,
            InputHash = inputHash,
            ExecutedBy = executedBy,
            StartedAtUtc = startedAtUtc,
            CompletedAtUtc = startedAtUtc,
        };

    public void Complete(
        ExecutionStatus status,
        string decision,
        string matchedRulesJson,
        string traceJson,
        string outputFactsJson,
        DateTime completedAtUtc,
        long durationMs)
    {
        Status = status;
        Decision = decision;
        MatchedRulesJson = matchedRulesJson;
        TraceJson = traceJson;
        OutputFactsJson = outputFactsJson;
        CompletedAtUtc = completedAtUtc;
        DurationMs = durationMs;
    }

    public void AddAction(ActionExecution action) => Actions.Add(action);
}
